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
        public string dialogue;      // 면접관 대사 (자막용)
        public int expression_id;    // Animator 의 Expression_ID 파라미터로 전달
        public int gesture_id;       // Animator 의 Gesture_ID 파라미터로 전달
        public int score;            // 직전 답변 점수 (-1 = 해당 없음)
        public bool is_final;
    }

    [Serializable]
    public class StageScore
    {
        public string stage;
        public int score;
    }

    /// <summary> 면접 종료 후 결과 UI 시각화용 종합 피드백. </summary>
    [Serializable]
    public class FeedbackReport
    {
        public string type;          // "feedback_report"
        public string session_id;
        public int overall_score;
        public StageScore[] stage_scores;
        public string strengths;
        public string improvements;
        public string summary;
        public float avg_speaking_time;
        public int total_pauses;
    }

    /// <summary> type 필드만 먼저 읽어 어떤 패킷인지 판별하기 위한 경량 구조체. </summary>
    [Serializable]
    public class ServerMessage
    {
        public string type;
    }
}
