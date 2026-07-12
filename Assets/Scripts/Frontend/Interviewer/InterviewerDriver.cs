using UnityEngine;
using VerbalProcess;

namespace VRoom.Backend
{
    /// <summary>
    /// 백엔드 이벤트를 실제 면접관 캐릭터에 연결하는 드라이버.
    /// persona -> Emotion(감정 축), 오디오 재생 여부 -> Speaking(발화 축) 으로
    /// Animator Blend Tree 를 구동하고, expression_id 는 얼굴 BlendShape 로 넘긴다.
    /// </summary>
    public class InterviewerDriver : MonoBehaviour
    {
        [Header("참조")]
        public BackendControlClient backend;
        public Animator animator;
        public Speaker speaker;
        public InterviewerExpression expression;
        public ResultUI resultUI;

        [Header("면접 설정")]
        public string company = "";
        public string jobTitle = "";
        [TextArea] public string resume = "";

        [Header("블렌드 반응 속도")]
        [SerializeField] float emotionLerp = 6f;
        [SerializeField] float speakingLerp = 10f;

        private static readonly int EmotionHash = Animator.StringToHash("Emotion");
        private static readonly int SpeakingHash = Animator.StringToHash("Speaking");

        private float _targetEmotion = 0f;   // -1(부정) ~ +1(긍정)
        private float _targetSpeaking = 0f;   //  0(경청) ~  1(말하기)

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
            if (InterviewConfig.IsReady)
            {
                company = InterviewConfig.Company;
                jobTitle = InterviewConfig.JobTitle;
                resume = InterviewConfig.Resume;
            }
            backend.StartInterview(company, jobTitle, resume);
        }

        void HandlePacket(BehaviorPacket p)
        {
            Debug.Log($"[면접관/{p.stage}/{p.persona}] {p.dialogue} " +
                      $"(점수 {p.score}, expr={p.expression_id}, gesture={p.gesture_id})");

            _targetEmotion = p.persona switch
            {
                "POSITIVE" => 1f,
                "NEGATIVE" => -1f,
                _ => 0f,
            };
            expression?.Apply(p.expression_id);

            if (p.is_final) _ = backend.RequestFeedback();
        }

        private void HandleAudio(byte[] pcm)
        {
            _targetSpeaking = 1f;
            if (speaker != null)
                speaker.HandleAudioChunkReceived(pcm);
        }

        private void HandleAudioEnd()
        {
            _targetSpeaking = 0f;
            if (speaker != null)
                speaker.SetEndOfStream();
        }

        private void HandleFeedback(FeedbackReport r)
        {
            if (resultUI != null) 
                resultUI.Show(r);
            else 
                Debug.LogWarning("[InterviewerDriver] resultUI 미연결");
        }

        private void Update()
        {
            if (animator == null) return;

            animator.SetFloat(EmotionHash,
                Mathf.Lerp(animator.GetFloat(EmotionHash), _targetEmotion, Time.deltaTime * emotionLerp));
            animator.SetFloat(SpeakingHash,
                Mathf.Lerp(animator.GetFloat(SpeakingHash), _targetSpeaking, Time.deltaTime * speakingLerp));
        }
    }
}