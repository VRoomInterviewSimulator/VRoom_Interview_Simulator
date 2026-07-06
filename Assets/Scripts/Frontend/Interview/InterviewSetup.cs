using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

namespace VRoom.Backend
{
    /// <summary>
    /// SetupScene: PC 2D UI 로 회사/직무/이력서를 입력받아
    /// InterviewConfig 에 저장 후 면접 씬으로 전환한다.
    /// </summary>
    public class InterviewSetup : MonoBehaviour
    {
        [Header("입력 필드 (TMP)")]
        public TMP_InputField companyInput;
        public TMP_InputField jobTitleInput;
        public TMP_InputField resumeInput;

        [Header("씬 이름")]
        public string interviewSceneName = "InterviewRoomScene";

        [Header("시작 버튼")]
        public Button startButton;
        public TMP_Text warningText;

        void Start()
        {
            startButton.onClick.AddListener(OnStart);
            if (warningText != null) warningText.text = "";
        }

        void OnStart()
        {
            string company = companyInput.text.Trim();
            string job = jobTitleInput.text.Trim();
            string resume = resumeInput.text.Trim();
            if (string.IsNullOrEmpty(company) || string.IsNullOrEmpty(job))
            {
                if (warningText != null) warningText.text = "회사와 직무는 필수 입력입니다.";
                return;
            }

            InterviewConfig.Company = company;
            InterviewConfig.JobTitle = job;
            InterviewConfig.Resume = resume;
            InterviewConfig.IsReady = true;

            SceneManager.LoadScene(interviewSceneName);
        }
    }
}