using UnityEngine;
using VerbalProcess;

namespace VRoom.Backend
{
    /// <summary>
    /// 백엔드 이벤트를 실제 면접관 캐릭터에 연결하는 드라이버.
    /// persona -> Emotion(감정 축), 실제 오디오 재생 여부 -> Speaking(발화 축) 으로
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

        [Header("결과 화면")]
        [Tooltip("재생 완료 신호가 오지 않을 때 강제로 피드백을 요청하기까지의 대기 시간(초)")]
        [SerializeField] float feedbackFallbackTimeout = 8f;

        private static readonly int EmotionHash = Animator.StringToHash("Emotion");
        private static readonly int SpeakingHash = Animator.StringToHash("Speaking");

        private float _targetEmotion = 0f;   // -1(부정) ~ +1(긍정)
        private float _targetSpeaking = 0f;  //  0(경청) ~  1(말하기)

        private bool _feedbackPending = false;
        private bool _feedbackRequested = false;
        private float _feedbackDeadline = float.MaxValue;

        private void OnEnable()
        {
            backend.OnBehaviorPacket += HandlePacket;
            backend.OnAudioChunk += HandleAudio;
            backend.OnAudioEnd += HandleAudioEnd;
            backend.OnFeedback += HandleFeedback;

            if (speaker != null)
                speaker.OnPlaybackFinished += HandlePlaybackFinished;
        }

        private void OnDisable()
        {
            backend.OnBehaviorPacket -= HandlePacket;
            backend.OnAudioChunk -= HandleAudio;
            backend.OnAudioEnd -= HandleAudioEnd;
            backend.OnFeedback -= HandleFeedback;

            if (speaker != null)
                speaker.OnPlaybackFinished -= HandlePlaybackFinished;
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
                      $"(점수 {p.score}, emo={p.persona_value:F2}, expr={p.expression_id})");

            _targetEmotion = p.persona_value;
            expression?.Apply(p.expression_id);

            if (p.is_final && !_feedbackRequested)
            {
                _feedbackPending = true;
                _feedbackDeadline = float.MaxValue;
                Debug.Log("[면접관] 마무리 멘트 수신 - 재생 완료 후 결과 요청 예정");
            }
        }

        private void HandleAudio(byte[] pcm)
        {
            _targetSpeaking = 1f;
            if (speaker != null)
                speaker.HandleAudioChunkReceived(pcm);
        }

        private void HandleAudioEnd()
        {
            if (speaker != null)
                speaker.SetEndOfStream();
    
            else
            {
                _targetSpeaking = 0f;
                if (_feedbackPending) 
                    RequestFeedbackOnce();
                return;
            }

            if (_feedbackPending && !_feedbackRequested)
                _feedbackDeadline = Time.time + feedbackFallbackTimeout;
        }

        /// <summary> Speaker 버퍼가 완전히 비워져 실제 재생이 끝났을 때 호출된다. </summary>
        private void HandlePlaybackFinished()
        {
            _targetSpeaking = 0f;

            if (_feedbackPending)
                RequestFeedbackOnce();
        }

        private void RequestFeedbackOnce()
        {
            if (_feedbackRequested) 
                return;

            _feedbackRequested = true;
            _feedbackPending = false;
            _feedbackDeadline = float.MaxValue;

            Debug.Log("[면접관] 마무리 멘트 재생 완료 - 피드백 요청");
            _ = backend.RequestFeedback();
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
            if (_feedbackPending && !_feedbackRequested && Time.time >= _feedbackDeadline)
            {
                Debug.LogWarning("[면접관] 재생 완료 신호 미수신 - 타임아웃으로 피드백 요청");
                RequestFeedbackOnce();
            }

            if (animator == null) 
                return;

            animator.SetFloat(EmotionHash,
                Mathf.Lerp(animator.GetFloat(EmotionHash), _targetEmotion, Time.deltaTime * emotionLerp));
            animator.SetFloat(SpeakingHash,
                Mathf.Lerp(animator.GetFloat(SpeakingHash), _targetSpeaking, Time.deltaTime * speakingLerp));
        }
    }
}