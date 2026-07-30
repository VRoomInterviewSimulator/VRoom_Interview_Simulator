using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

namespace VRoom.Backend
{
    public class ResultUI : MonoBehaviour
    {
        [Header("Root")]
        public GameObject panelRoot;
        public TMP_Text titleText;

        [Header("점수 열")]
        public Transform leftColumn;
        public Transform rightColumn;
        public GameObject scoreCellPrefab;

        [Header("하단")]
        public TMP_Text totalText;
        public TMP_Text summaryText;
        public Button homeButton;

        [Header("씬")]
        public string homeSceneName = "SetupScene";

        private static readonly string[] Labels =
        {
            "시선 처리", "손 사용", "자세 안정성", "표정 변화", "답변 목소리 크기",
            "답변 목소리 빠르기", "답변 길이", "답변 내 필러 단어 여부", "답변의 정확도", "답변 반응 속도"
        };


        void Awake()
        {
            if (panelRoot) 
                panelRoot.SetActive(false);

            if (homeButton) 
                homeButton.onClick.AddListener(GoHome);
        }

        public void Show(FeedbackReport r)
        {
            if (r == null) 
                return;

            if (panelRoot) 
                panelRoot.SetActive(true);

            if (titleText) 
                titleText.text = "면접 결과";

            int[] values = ToArray(r.scores);
            int total = r.overall_score;
            if (total <= 0) 
            { 
                total = 0; 
                foreach (var v in values) 
                    total += v; 
            }
            ClearCells();

            for (int i = 0; i < Labels.Length; i++)
            {
                Transform parent = (i < 5) ? leftColumn : rightColumn;
                if (parent == null || scoreCellPrefab == null) 
                    continue;

                var go = Instantiate(scoreCellPrefab, parent);
                go.SetActive(true);
                var cell = go.GetComponent<ScoreCellView>();
                if (cell) 
                    cell.Set(i + 1, Labels[i], values[i], 10);
            }

            if (totalText) 
                totalText.text = $"총점: {total}/100";
            if (summaryText) 
                summaryText.text = string.IsNullOrEmpty(r.summary) ? "" : r.summary;
        }

        public void Hide()
        {
            if (panelRoot) 
                panelRoot.SetActive(false);
        }

        private void GoHome()
        {
            if (!string.IsNullOrEmpty(homeSceneName))
                SceneManager.LoadScene(homeSceneName);
        }

        private static int[] ToArray(InterviewScore s)
        {
            if (s == null) 
                return new int[10];

            return new[]
            {
                s.gaze, s.gesture, s.posture, s.expression, s.voiceVolume,
                s.voiceSpeed, s.answerLength, s.fillerWords, s.accuracy, s.responseTime
            };
        }

        private void ClearCells()
        {
            ClearChildren(leftColumn);
            ClearChildren(rightColumn);
        }

        private static void ClearChildren(Transform parent)
        {
            if (parent == null) return;

            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);   // 편집 모드: 즉시 삭제
            }
        }

        [ContextMenu("Show Sample")]
        private void ShowSample()
        {
            Show(new FeedbackReport
            {
                scores = new InterviewScore
                {
                    gaze = 10,
                    gesture = 8,
                    posture = 9,
                    expression = 7,
                    voiceVolume = 10,
                    voiceSpeed = 6,
                    answerLength = 8,
                    fillerWords = 5,
                    accuracy = 9,
                    responseTime = 7
                },
                summary = "전반적으로 안정적이었으나 필러 단어와 발화 속도에서 개선이 필요합니다."
            });
        }
    }
}