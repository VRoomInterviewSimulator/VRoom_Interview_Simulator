using System;

namespace VRoom.Backend
{
    /// <summary>
    /// 백엔드 -> Unity 행동 지시 패킷. 백엔드 domain.py 의 BehaviorPacket 과 1:1 대응.
    /// JsonUtility 로 역직렬화하므로 필드명/타입을 그대로 맞춰야 한다.
    /// </summary>


    [Serializable]
    public class BehaviorPacket
    {
        public string type;          // "interviewer_turn" | "thinking" | "audio_end" | "feedback_report"
        public string session_id;
        public string stage;         // SELF_INTRO / TECH_Q1 / ... / DONE
        public string persona;       // POSITIVE / NEUTRAL / NEGATIVE
        public float persona_value;  //  연속 감정 강도(-1.0 ~ +1.0)
        public string dialogue;      // 면접관 대사 (자막용)
        public int expression_id;    // Animator 의 Expression_ID 파라미터로 전달
        public int gesture_id;       // Animator 의 Gesture_ID 파라미터로 전달
        public int score;            // 직전 답변 점수 (-1 = 해당 없음)
        public bool is_final;
    }

    /// <summary> 10개 평가 항목, 각 0~10점. </summary>
    [Serializable]
    public class InterviewScore
    {
        public int gaze;         // 1. 시선 추적
        public int gesture;      // 2. 손짓 추적
        public int posture;      // 3. 신체 추적
        public int expression;   // 4. 표정 분석
        public int voiceVolume;  // 5. 답변 목소리 크기
        public int voiceSpeed;   // 6. 답변 목소리 빠르기
        public int answerLength; // 7. 답변 길이
        public int fillerWords;  // 8. 답변 내 필러 단어 여부
        public int accuracy;     // 9. 답변의 정확도
        public int responseTime; // 10. 답변 반응 속도
    }

    /// <summary> 면접 종료 후 결과 UI 시각화용 종합 피드백. </summary>
    [Serializable]
    public class FeedbackReport
    {
        public string type;
        public string session_id;
        public InterviewScore scores;
        public int overall_score;
        public string summary;
    }

    /// <summary> type 필드만 먼저 읽어 어떤 패킷인지 판별하기 위한 경량 구조체. </summary>
    [Serializable]
    public class ServerMessage
    {
        public string type;
    }
}
