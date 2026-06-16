using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VRoom.Backend
{
    /// <summary>
    /// 백엔드(4.2.4)의 /ws/control 채널에 연결하는 Unity 클라이언트.
    ///
    /// 한 채널로 두 종류의 프레임을 받는다 (기존 STTManager 와 동일한 패턴):
    ///   - Text  프레임 = JSON 행동 패킷 / 피드백 리포트  -> 이벤트로 전달
    ///   - Binary프레임 = 44.1kHz Mono 32-bit Float PCM 면접관 음성 -> Speaker.cs 로 전달
    ///
    /// 사용법:
    ///   1) 이 컴포넌트를 씬의 GameObject 에 부착
    ///   2) backendUrl 을 백엔드 주소로 설정 (예: ws://192.168.0.10:8080/ws/control)
    ///   3) OnBehaviorPacket / OnAudioChunk / OnFeedback / OnAudioEnd 이벤트를
    ///      Animator, Speaker, 자막 UI 등에 연결
    ///   4) StartInterview(company, jobTitle, resume) 호출로 면접 시작
    ///   5) (STT 워커가 /process 로 답변을 보내면 면접관 응답이 이 채널로 흘러온다)
    /// </summary>
    public class BackendControlClient : MonoBehaviour
    {
        [Header("Backend")]
        public string backendUrl = "ws://127.0.0.1:8080/ws/control";
        public string sessionId = "default";

        // 이벤트 (메인 스레드에서 호출됨)
        public event Action<BehaviorPacket> OnBehaviorPacket;  // interviewer_turn / thinking
        public event Action<byte[]> OnAudioChunk;              // PCM 음성 청크 -> Speaker.cs
        public event Action OnAudioEnd;                        // 한 발화의 음성 종료
        public event Action<FeedbackReport> OnFeedback;        // 최종 피드백

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private SynchronizationContext _main;

        private void Awake() => _main = SynchronizationContext.Current;

        /// <summary> 면접 시작: 연결 후 init 메시지를 보낸다. </summary>
        public async void StartInterview(string company, string jobTitle, string resume)
        {
            _cts = new CancellationTokenSource();
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(backendUrl), _cts.Token);
            _ = ReceiveLoop();

            string init =
                "{\"type\":\"init\"," +
                $"\"session_id\":\"{Escape(sessionId)}\"," +
                $"\"company\":\"{Escape(company)}\"," +
                $"\"job_title\":\"{Escape(jobTitle)}\"," +
                $"\"resume\":\"{Escape(resume)}\"}}";
            await SendText(init);
        }

        /// <summary> 면접 종료 시 최종 피드백 리포트를 요청한다. </summary>
        public Task RequestFeedback()
            => SendText($"{{\"type\":\"request_feedback\",\"session_id\":\"{Escape(sessionId)}\"}}");

        private async Task SendText(string json)
        {
            if (_ws?.State != WebSocketState.Open) return;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, _cts.Token);
        }

        private async Task ReceiveLoop()
        {
            byte[] buffer = new byte[1024 * 32];
            using var ms = new System.IO.MemoryStream();
            while (_ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                byte[] data = ms.ToArray();
                if (data.Length == 0) continue;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    Post(() => OnAudioChunk?.Invoke(data));   // PCM 음성
                }
                else // Text = JSON
                {
                    string msg = Encoding.UTF8.GetString(data);
                    Dispatch(msg);
                }
            }
        }

        private void Dispatch(string json)
        {
            string type = JsonUtility.FromJson<ServerMessage>(json)?.type;
            switch (type)
            {
                case "audio_end":
                    Post(() => OnAudioEnd?.Invoke());
                    break;
                case "feedback_report":
                    var report = JsonUtility.FromJson<FeedbackReport>(json);
                    Post(() => OnFeedback?.Invoke(report));
                    break;
                default: // interviewer_turn / thinking
                    var packet = JsonUtility.FromJson<BehaviorPacket>(json);
                    Post(() => OnBehaviorPacket?.Invoke(packet));
                    break;
            }
        }

        // 백그라운드 수신 스레드 -> Unity 메인 스레드로 마샬링
        private void Post(Action a) => _main.Post(_ => a(), null);

        private static string Escape(string s)
            => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

        private async void OnDestroy()
        {
            try
            {
                _cts?.Cancel();
                if (_ws?.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch { /* ignore */ }
        }
    }
}
