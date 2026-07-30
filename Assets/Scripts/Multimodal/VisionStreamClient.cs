using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VRoom.Multimodal
{
    public class VisionStreamClient : MonoBehaviour
    {
        [SerializeField] private string wsUrl = "ws://127.0.0.1:8002/ws/vision";
        [SerializeField] private string sessionId = "default";

        public string SessionId { get => sessionId; set => sessionId = value; }
        public bool IsOpen => _ws?.State == WebSocketState.Open;
        public Action<bool, int> OnCalibrated;

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private SynchronizationContext _main;

        private void Awake() => _main = SynchronizationContext.Current;

        public async Task<bool> ConnectAsync()
        {
            if (IsOpen) return true;
            try
            {
                _cts?.Cancel();
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                _cts = new CancellationTokenSource();
                _ws.Options.SetBuffer(4096, 4096);
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);

                string url = wsUrl.Contains("session_id=")
                    ? wsUrl
                    : wsUrl + (wsUrl.Contains("?") ? "&" : "?") + "session_id=" + sessionId;

                await _ws.ConnectAsync(new Uri(url), _cts.Token);
                _ = ReceiveLoop();
                Debug.Log($"[Vision] 연결됨: {url}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Vision] 연결 실패: {e.Message}");
                return false;
            }
        }

        public Task SendCalibrateStart() => SendJson("{\"type\":\"calibrate_start\"}");
        public Task SendCalibrateEnd() => SendJson("{\"type\":\"calibrate_end\"}");
        public Task SendTurnEnd() => SendJson("{\"type\":\"turn_end\"}");
        public Task SendTurnStart(string stage)
            => SendJson($"{{\"type\":\"turn_start\",\"stage\":\"{Escape(stage)}\"}}");

        private async Task SendJson(string json)
        {
            if (!IsOpen) return;
            await _sendLock.WaitAsync();
            try
            {
                byte[] b = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(new ArraySegment<byte>(b),
                    WebSocketMessageType.Text, true, _cts.Token);
            }
            catch (Exception e) { Debug.LogWarning($"[Vision] 텍스트 전송 실패: {e.Message}"); }
            finally { _sendLock.Release(); }
        }

        private async Task ReceiveLoop()
        {
            var buf = new byte[4096];
            while (IsOpen)
            {
                try
                {
                    var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    var m = JsonUtility.FromJson<CalibMsg>(
                        Encoding.UTF8.GetString(buf, 0, r.Count));
                    if (m != null && m.type == "calibrated")
                        _main.Post(_ => OnCalibrated?.Invoke(m.ok, m.samples), null);
                    if (m != null && m.type == "camera_error")
                        _main.Post(_ => Debug.LogError("[Vision] 노트북 웹캠을 열 수 없습니다."), null);
                }
                catch { break; }
            }
        }

        [Serializable]
        private class CalibMsg
        { public string type; public bool ok; public int samples; }

        private void OnDestroy() { _cts?.Cancel(); _ws?.Dispose(); }

        private static string Escape(string s)
            => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}