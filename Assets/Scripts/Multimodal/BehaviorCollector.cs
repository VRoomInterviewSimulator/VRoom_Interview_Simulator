using System.Collections;
using UnityEngine;
using VerbalProcess;
using VRoom.Backend;

namespace VRoom.Multimodal
{
    /// <summary>
    /// 원격 Vision 워커(노트북)에 턴 경계만 알려주는 오케스트레이터.
    /// 웹캠 캡처는 워커가 로컬에서 직접 수행하므로 Unity 는 프레임을 다루지 않는다.
    ///
    /// 턴 구간: 면접관 질문 오디오 재생 완료 ~ 사용자 발화 종료
    ///   -> 생각하는 동안의 시선 회피/자세 흔들림까지 포함해야 압박 효과가 잡힌다.
    /// </summary>
    public class BehaviorCollector : MonoBehaviour
    {
        [Header("참조")]
        public VisionStreamClient client;
        public VoiceActivityDetector vad;
        public Speaker speaker;
        public BackendControlClient backend;

        [Header("캘리브레이션")]
        [Tooltip("면접 시작 전 '정면 응시' 기준을 잡는 시간(초)")]
        public float calibrationSeconds = 3f;
        public bool calibrateOnStart = true;

        [Header("상태(읽기 전용)")]
        [SerializeField] private bool _inTurn;
        [SerializeField] private string _currentStage = "";
        [SerializeField] private bool _calibrated;

        private bool _turnStartPending;
        private bool _calibrating;
        private bool _ready;

        private IEnumerator Start()
        {
            if (!string.IsNullOrEmpty(InterviewConfig.SessionId))
                client.SessionId = InterviewConfig.SessionId;
            else if (backend != null && !string.IsNullOrEmpty(backend.sessionId))
                client.SessionId = backend.sessionId;

            var connect = client.ConnectAsync();
            while (!connect.IsCompleted) yield return null;

            if (!connect.Result)
            {
                Debug.LogWarning("[Behavior] Vision 워커 연결 실패. " +
                                 "시각 4항목은 채점에서 제외됩니다.");
                yield break;   // 면접은 정상 진행
            }

            client.OnCalibrated += (ok, n) =>
            {
                _calibrated = ok;
                Debug.Log($"[Behavior] 캘리브레이션 {(ok ? "성공" : "실패")} (샘플 {n})");
            };
            _ready = true;


            if (vad != null) vad.OnUtteranceEnded += HandleUtteranceEnded;
            if (speaker != null) speaker.OnPlaybackFinished += HandlePlaybackFinished;
            if (backend != null) backend.OnBehaviorPacket += HandlePacket;

            if (calibrateOnStart) 
                yield return Calibrate();

            if (_turnStartPending)
            {
                _turnStartPending = false;
                BeginTurn();
            }
        }

        private void OnDestroy()
        {
            if (vad != null) vad.OnUtteranceEnded -= HandleUtteranceEnded;
            if (speaker != null) speaker.OnPlaybackFinished -= HandlePlaybackFinished;
            if (backend != null) backend.OnBehaviorPacket -= HandlePacket;
        }

        public IEnumerator Calibrate()
        {
            Debug.Log($"[Behavior] 캘리브레이션 {calibrationSeconds}초 — 정면을 응시하세요.");
            _calibrating = true;
            yield return client.SendCalibrateStart();
            yield return new WaitForSeconds(calibrationSeconds);
            yield return client.SendCalibrateEnd();
            _calibrating = false;
        }

        /// <summary> 면접관 발화 재생 완료 = 사용자 차례 시작. </summary>
        private void HandlePlaybackFinished()
        {
            if (!_ready || _inTurn) return;
            if (_currentStage == "DONE") return;
            if (_calibrating) { _turnStartPending = true; return; }
            BeginTurn();
        }

        private void HandleUtteranceEnded(VoiceActivityDetector.VoiceFeatures features)
        {
            if (!_ready || !_inTurn) return;
            _inTurn = false;
            _ = client.SendTurnEnd();
            Debug.Log("[Behavior] 턴 종료 → 워커 집계 요청");
        }

        private void HandlePacket(BehaviorPacket p)
        {
            if (!string.IsNullOrEmpty(p.stage)) _currentStage = p.stage;
        }

        private void BeginTurn()
        {
            _inTurn = true;
            _ = client.SendTurnStart(_currentStage);
            Debug.Log($"[Behavior] 턴 시작 (stage={_currentStage})");
        }

        // ---- 에디터 단독 검증용 ----
        [ContextMenu("Test / 캘리브레이션")]
        private void TestCalibrate() { _ready = true; StartCoroutine(Calibrate()); }
        [ContextMenu("Test / 턴 시작")]
        private void TestTurnStart() { _ready = true; HandlePlaybackFinished(); }
        [ContextMenu("Test / 턴 종료")]
        private void TestTurnEnd() => HandleUtteranceEnded(default);
    }
}