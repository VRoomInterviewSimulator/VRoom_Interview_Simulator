using UnityEngine;
using VerbalProcess;

namespace VRoom.Backend
{
    /// <summary>
    /// 백엔드 이벤트를 실제 면접관 캐릭터에 연결하는 예시 드라이버.
    /// 이 파일은 '어떻게 연결하는지' 보여주는 샘플이므로 프로젝트 구조에 맞게 수정해서 쓰면 된다.
    /// </summary>
    public class InterviewerDriver : MonoBehaviour
    {
        [Header("참조")]
        public BackendControlClient backend;   // BackendControlClient 컴포넌트
        public Animator animator;              // 면접관 Animator (Expression_ID / Gesture_ID 파라미터 보유)
        public Speaker speaker;          // 팀의 Speaker.cs (OnAudioChunk 를 넘겨줄 대상)
        [Header("면접 설정")]
        public string company = "네이버";
        public string jobTitle = "백엔드 개발자";
        [TextArea] public string resume = "Spring/Java 3년, MSA 경험";

        // Animator 파라미터 이름 (uLipSync 와 별개로 표정/제스처 제어)
        private static readonly int ExpressionId = Animator.StringToHash("Expression_ID");
        private static readonly int GestureId = Animator.StringToHash("Gesture_ID");

        private void OnEnable()
        {
            backend.OnBehaviorPacket += HandlePacket;
            backend.OnAudioChunk += HandleAudio;
            backend.OnAudioEnd += HandleAudioEnd;
            backend.OnFeedback += HandleFeedback;
        }

        private void OnDisable()
        {
            backend.OnBehaviorPacket -= HandlePacket;
            backend.OnAudioChunk -= HandleAudio;
            backend.OnAudioEnd -= HandleAudioEnd;
            backend.OnFeedback -= HandleFeedback;
        }

        private void Start()
        {
            backend.StartInterview(company, jobTitle, resume);
        }

        // 행동 패킷 -> 표정/제스처 즉시 트리거 (음성보다 먼저 도착하므로 반응이 자연스럽다)
        void HandlePacket(BehaviorPacket p)
        {
            Debug.Log($"[면접관/{p.stage}/{p.persona}] {p.dialogue} " +
                      $"(점수 {p.score}, expr={p.expression_id}, gesture={p.gesture_id})");
            if (p.is_final) _ = backend.RequestFeedback();
        }

        // PCM 음성 청크 -> Speaker.cs 로 전달. (팀 Speaker 의 실제 메서드명에 맞춰 수정)
        private void HandleAudio(byte[] pcm)
        {
            if(speaker != null)
                speaker.HandleAudioChunkReceived(pcm);
        }

        private void HandleAudioEnd()
        {
            if(speaker != null)
                speaker.SetEndOfStream();
        }

        private void HandleFeedback(FeedbackReport r)
        {
            Debug.Log($"[피드백] 종합 {r.overall_score}점 / 평균발화 {r.avg_speaking_time}s");
            Debug.Log($"강점: {r.strengths}\n개선: {r.improvements}\n총평: {r.summary}");
            // 여기서 3D Spatial UI 에 결과를 시각화한다.
        }
    }
}
