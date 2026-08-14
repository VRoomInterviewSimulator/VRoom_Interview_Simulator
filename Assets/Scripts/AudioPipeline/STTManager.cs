using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VerbalProcess
{
    /// <summary>
    /// WebSocket을 통해 실시간 STT 및 Feature 데이터를 전송하는 매니저
    /// </summary>
    public class STTManager : MonoBehaviour
    {
        [SerializeField] private string wsUrl = "ws://127.0.0.1:8000/ws/interview";
        [SerializeField] private string sessionId = "default";

        public string SessionId
        {
            get => sessionId;
            set => sessionId = value;
        }

        public Action<FinalResponse> OnTranscriptionReceived; // 최종 결과 수신
        public Action<byte[]> OnAudioChunkReceived; // 서버로부터 오디오 청크(Raw PCM) 수신 시 발생
        public Action OnAudioStreamEnded; // 서버에서 모든 오디오 스트리밍이 완료되었을 때 발생
        public Action<string> OnSubtitleReceived; // 서버로부터 자막 텍스트 수신 시 발생
        public Action<CorrectionRequestMessage> OnCorrectionRequested; // 저신뢰 교정 요청 수신 시 발생
        public Action OnSentenceCompletedFlag; // 부분 전사에서 문장 종결이 감지되었을 때 발생
        public Action OnSttSkipped;            // 빈 전사로 인해 STT 처리가 스킵되었을 때 발생

        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cts;
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
        private bool _isFirstChunk = true;
        private int _reconnectScheduled = 0;

        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1); // 웹소켓 연결에 따른 세마포어
        private readonly SemaphoreSlim _sendSignal = new SemaphoreSlim(0); // 전송 큐 소비자 깨우기
        private readonly object _utteranceStateLock = new object();
        private readonly ConcurrentQueue<OutgoingPacket> _outgoingPackets = new ConcurrentQueue<OutgoingPacket>();
        private Task _sendLoopTask;

        private async void Start()
        {
            _sendLoopTask = SendLoopAsync();
            try
            {
                await ConnectAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[STT] Initial connection unavailable. Queued packets will retry after reconnect: {e.Message}");
                _ = ScheduleReconnectAsync();
            }
        }

        private void OnDestroy()
        {
            _lifetimeCts.Cancel();
            _cts?.Cancel();
            _webSocket?.Dispose();
            FailQueuedPackets(new OperationCanceledException("STTManager was destroyed."));
            try { _sendSignal.Release(); } catch (SemaphoreFullException) { }
        }

        public async Task ConnectAsync()
        {
            // 연결 중인 호출도 동일한 lock을 기다린 뒤 연결 결과를 공유합니다.
            if (_webSocket?.State == WebSocketState.Open) return;

            await _connectLock.WaitAsync(_lifetimeCts.Token);
            ClientWebSocket socket = null;
            CancellationTokenSource socketCts = null;
            try
            {
                if (_webSocket?.State == WebSocketState.Open) return;

                _cts?.Cancel();
                _webSocket?.Dispose();

                socket = new ClientWebSocket();
                socketCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);

                // 🌟 오디오 스트리밍 최적화: 버퍼 크기를 8KB로 조절하여 레이턴시 단축 및 네이글 알고리즘 완화
                socket.Options.SetBuffer(8192, 8192);
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);

                string urlWithSid = wsUrl;
                if (!urlWithSid.Contains("session_id="))
                {
                    string separator = urlWithSid.Contains("?") ? "&" : "?";
                    urlWithSid += $"{separator}session_id={Uri.EscapeDataString(sessionId)}";
                }

                await socket.ConnectAsync(new Uri(urlWithSid), socketCts.Token);
                if (_lifetimeCts.IsCancellationRequested)
                {
                    socket.Dispose();
                    socketCts.Dispose();
                    return;
                }

                _webSocket = socket;
                _cts = socketCts;
                Debug.Log($"[STT] WebSocket Connected to: {urlWithSid}");
                _ = ReceiveLoop(socket, socketCts);
            }
            catch (Exception e)
            {
                if (socket != null && !ReferenceEquals(_webSocket, socket))
                    socket.Dispose();
                if (socketCts != null && !ReferenceEquals(_cts, socketCts))
                    socketCts.Dispose();
                if (!_lifetimeCts.IsCancellationRequested)
                    Debug.LogError($"[STT] Connection Error: {e.Message}");
                throw;
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private async Task ScheduleReconnectAsync()
        {
            if (_lifetimeCts.IsCancellationRequested ||
                System.Threading.Interlocked.Exchange(ref _reconnectScheduled, 1) == 1)
                return;

            try
            {
                while (!_lifetimeCts.IsCancellationRequested && _webSocket?.State != WebSocketState.Open)
                {
                    await Task.Delay(3000, _lifetimeCts.Token);
                    if (_webSocket?.State == WebSocketState.Open) break;

                    try
                    {
                        Debug.Log("[STT] Attempting to reconnect to STT Worker...");
                        await ConnectAsync();
                    }
                    catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[STT] Reconnect failed: {e.Message}");
                    }
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _reconnectScheduled, 0);
            }
        }

        /// <summary>
        /// 발화가 새로 시작됨을 알림 (헤더 전송 준비)
        /// </summary>
        public void ResetUtteranceState()
        {
            lock (_utteranceStateLock)
            {
                _isFirstChunk = true;
            }
        }

        /// <summary>
        /// 오디오 청크를 바이너리로 전송 (첫 청크만 헤더 포함)
        /// </summary>
        public Task SendAudioChunkAsync(AudioClip clip)
        {
            if (clip == null)
                return Task.CompletedTask;

            byte[] audioBytes;
            lock (_utteranceStateLock)
            {
                if (_isFirstChunk)
                {
                    audioBytes = AudioUtils.GetWavBytes(clip);
                    _isFirstChunk = false;
                }
                else
                {
                    audioBytes = AudioUtils.GetRawPcmBytes(clip);
                }
            }

            return EnqueuePacket(audioBytes, WebSocketMessageType.Binary, "audio chunk");
        }

        private string EscapeJson(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private Task EnqueuePacket(byte[] payload, WebSocketMessageType messageType, string description, bool resetUtteranceStateAfterSend = false)
        {
            if (_lifetimeCts.IsCancellationRequested)
                return Task.FromException(new OperationCanceledException("STTManager is shutting down."));

            var packet = new OutgoingPacket(payload, messageType, description, resetUtteranceStateAfterSend);
            _outgoingPackets.Enqueue(packet);
            _sendSignal.Release();
            return packet.Completion.Task;
        }

        private async Task SendLoopAsync()
        {
            try
            {
                while (!_lifetimeCts.IsCancellationRequested)
                {
                    await _sendSignal.WaitAsync(_lifetimeCts.Token);

                    while (_outgoingPackets.TryDequeue(out var packet))
                    {
                        try
                        {
                            await SendPacketAsync(packet);
                            packet.Completion.TrySetResult(true);
                        }
                        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                        {
                            packet.Completion.TrySetCanceled();
                            FailQueuedPackets(new OperationCanceledException("STT send loop was cancelled."));
                            return;
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[STT] Failed to send {packet.Description}: {e.Message}");
                            packet.Completion.TrySetException(e);

                            // 현재 발화의 후속 패킷을 새 연결에 이어 보내면 오디오가 중간부터 시작될 수 있습니다.
                            // 따라서 현재 큐를 비우고, 다음 발화부터 새 연결을 사용합니다.
                            FailQueuedPackets(e);
                            _cts?.Cancel();
                            _webSocket?.Dispose();
                            _ = ScheduleReconnectAsync();
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                FailQueuedPackets(new OperationCanceledException("STT send loop was cancelled."));
            }
        }

        private async Task SendPacketAsync(OutgoingPacket packet)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                await ConnectAsync();

            var socket = _webSocket;
            var socketCts = _cts;
            if (socket == null || socket.State != WebSocketState.Open || socketCts == null)
                throw new InvalidOperationException("STT WebSocket is not open.");

            await socket.SendAsync(
                new ArraySegment<byte>(packet.Payload),
                packet.MessageType,
                true,
                socketCts.Token);

            if (packet.ResetUtteranceStateAfterSend)
            {
                lock (_utteranceStateLock)
                {
                    _isFirstChunk = true;
                }
            }
        }

        private void FailQueuedPackets(Exception error)
        {
            while (_outgoingPackets.TryDequeue(out var queuedPacket))
            {
                queuedPacket.Completion.TrySetException(error);
            }
        }

        /// <summary>
        /// 발화 종료 신호와 Feature 데이터를 전송
        /// </summary>
        public Task SendEndUtteranceAsync(FeatureData features)
        {
            string featuresJson = features != null ? JsonUtility.ToJson(features) : "{}";
            string json = $"{{\"type\":\"utterance_end\",\"session_id\":\"{EscapeJson(sessionId)}\",\"features\":{featuresJson}}}";
            return EnqueuePacket(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                "utterance_end",
                resetUtteranceStateAfterSend: true);
        }

        /// <summary>
        /// 재발화 교정 종료 신호와 메타데이터를 전송
        /// </summary>
        public Task SendCorrectionEndUtteranceAsync(FeatureData features, int startIdx, int endIdx, string[] originalWords)
        {
            string wordsJson = "[]";
            if (originalWords != null && originalWords.Length > 0)
            {
                StringBuilder sb = new StringBuilder("[");
                for (int i = 0; i < originalWords.Length; i++)
                {
                    sb.Append($"\"{originalWords[i].Replace("\"", "\\\"")}\"");
                    if (i < originalWords.Length - 1) sb.Append(",");
                }
                sb.Append("]");
                wordsJson = sb.ToString();
            }

            string featuresJson = features != null ? JsonUtility.ToJson(features) : "{}";
            string json = $"{{\"type\":\"utterance_end\"," +
                          $"\"session_id\":\"{EscapeJson(sessionId)}\"," +
                          $"\"mode\":\"correction\"," +
                          $"\"target_range\":[{startIdx},{endIdx}]," +
                          $"\"original_words\":{wordsJson}," +
                          $"\"features\":{featuresJson}}}";

            return EnqueuePacket(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                "correction utterance_end",
                resetUtteranceStateAfterSend: true);
        }

        /// <summary>
        /// 수정 없이 그대로 질문을 전송
        /// </summary>
        public Task SendAnywayAsync(string text, FeatureData features)
        {
            string featuresJson = features != null ? JsonUtility.ToJson(features) : "{}";
            string json = $"{{\"type\":\"send_anyway\"," +
                          $"\"session_id\":\"{EscapeJson(sessionId)}\"," +
                          $"\"text\":\"{EscapeJson(text)}\"," +
                          $"\"features\":{featuresJson}}}";

            return EnqueuePacket(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                "send_anyway");
        }

        /// <summary>
        /// 교정을 포기하고 전체 발화를 폐기
        /// </summary>
        public Task SendDiscardAsync()
        {
            string json = $"{{\"type\":\"discard\",\"session_id\":\"{EscapeJson(sessionId)}\"}}";
            return EnqueuePacket(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                "discard");
        }

        private async Task ReceiveLoop(ClientWebSocket socket, CancellationTokenSource socketCts)
        {
            byte[] buffer = new byte[1024 * 32];
            using (var ms = new System.IO.MemoryStream())
            {
                while (socket.State == WebSocketState.Open && !socketCts.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = null;
                    try
                    {
                        ms.SetLength(0); // 매 메시지마다 스트림 초기화
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), socketCts.Token);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, socketCts.Token);
                            _ = ScheduleReconnectAsync();
                            break;
                        }

                        if (ms.Length == 0) continue;

                        byte[] data = ms.ToArray();

                        if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            // 바이너리 데이터는 TTS Worker에서 온 오디오 청크(Raw PCM)로 간주
                            OnAudioChunkReceived?.Invoke(data);
                        }
                        else if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string message = Encoding.UTF8.GetString(data);
                            Debug.Log($"[STT] Received JSON: {message}");

                            try
                            {
                                ServerMessage msg = JsonUtility.FromJson<ServerMessage>(message);
                                if (msg.type == "final")
                                {
                                    FinalResponse response = JsonUtility.FromJson<FinalResponse>(message);
                                    OnTranscriptionReceived?.Invoke(response);
                                }
                                else if (msg.type == "correction_request")
                                {
                                    CorrectionRequestMessage corrMsg = JsonUtility.FromJson<CorrectionRequestMessage>(message);
                                    OnCorrectionRequested?.Invoke(corrMsg);
                                }
                                else if (msg.type == "subtitle")
                                {
                                    SubtitleMessage subMsg = JsonUtility.FromJson<SubtitleMessage>(message);
                                    OnSubtitleReceived?.Invoke(subMsg.text);
                                }
                                else if (msg.type == "tts_end")
                                {
                                    OnAudioStreamEnded?.Invoke();
                                }
                                else if (msg.type == "adaptive_vad_trigger")
                                {
                                    OnSentenceCompletedFlag?.Invoke();
                                }
                                else if (msg.type == "stt_skip")
                                {
                                    OnSttSkipped?.Invoke();
                                }
                            }
                            catch (Exception e)
                            {
                                Debug.LogWarning($"Failed to parse server message: {e.Message}");
                            }
                        }
                    }
                    catch (OperationCanceledException) when (socketCts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[STT] Receive Loop Error: {e.Message}");
                        if (ReferenceEquals(_webSocket, socket))
                        {
                            _cts?.Cancel();
                            _webSocket?.Dispose();
                        }
                        _ = ScheduleReconnectAsync();
                        break;
                    }
                }
            }
        }

        private sealed class OutgoingPacket
        {
            public readonly byte[] Payload;
            public readonly WebSocketMessageType MessageType;
            public readonly string Description;
            public readonly bool ResetUtteranceStateAfterSend;
            public readonly TaskCompletionSource<bool> Completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public OutgoingPacket(byte[] payload, WebSocketMessageType messageType, string description, bool resetUtteranceStateAfterSend)
            {
                Payload = payload;
                MessageType = messageType;
                Description = description;
                ResetUtteranceStateAfterSend = resetUtteranceStateAfterSend;
            }
        }

        [Serializable]
        public class ServerMessage {
            public string type;
        }

        [Serializable]
        public class SubtitleMessage {
            public string type;
            public string text;
        }

        [Serializable]
        public class FinalResponse {
            public string type;
            public TranscriptionData data;
        }

        [Serializable]
        public class TranscriptionData {
            public string sttText;
            public float speakingTime;
            public int pauseCount;
            public int meaningfulPauseCount;
            public float volumeVariance;
            public float lowVolumeRatio;
            public float averageVolume;
            public float responseTime;
        }

        [Serializable]
        public class CorrectionRequestMessage {
            public string type;
            public TranscriptionData data;
            public string[] words;
            public float[] word_confidences;
        }
    }
}
