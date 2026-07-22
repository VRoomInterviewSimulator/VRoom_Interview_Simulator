using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

namespace VRoom.Backend
{
    public class InterviewSetup : MonoBehaviour
    {
        [Header("파일 불러오기 버튼")]
        public Button loadFileButton;

        [Header("미리보기 텍스트")]
        public TMP_Text companyPreview;
        public TMP_Text jobPreview;
        public TMP_Text resumePreview;

        [Header("씬 이름")]
        public string interviewSceneName = "InterviewRoomScene";

        [Header("시작 버튼 / 안내")]
        public Button startButton;
        public TMP_Text warningText;

        [Header("백엔드 프리웜")]
        public BackendWarmup warmup;


        private string _company = "";
        private string _job = "";
        private string _resume = "";
        private bool _loaded = false;
        private bool _preparing = false;
        private string _fileLine = "";
        private string _statusLine = "";

        void Start()
        {
            loadFileButton.onClick.AddListener(OnLoadFile);
            startButton.onClick.AddListener(OnStart);
            startButton.interactable = false;

            _fileLine = "먼저 면접 정보 txt 파일을 불러와주세요.";
            _statusLine = "";
            Redraw();
            ClearPreview();
            if (warmup != null)
            {
                warmup.OnProgress += OnWarmupProgress;
                warmup.OnFinished += OnWarmupFinished;
            }
        }

        void OnDestroy()
        {
            if (warmup != null)
            {
                warmup.OnProgress -= OnWarmupProgress;
                warmup.OnFinished -= OnWarmupFinished;
            }
        }

        void OnLoadFile()
        {
            string path = WindowsFileDialog.Open("면접 정보 txt 선택");
            if (string.IsNullOrEmpty(path))
                return;

            string raw;
            try
            {
                raw = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (System.Exception e)
            {
                SetWarning($"파일을 읽지 못했습니다: {e.Message}");
                return;
            }

            if (!TryParse(raw, out _company, out _job, out _resume, out string err))
            {
                _loaded = false;
                startButton.interactable = false;
                ClearPreview();
                SetWarning(err);
                _statusLine = "";
                return;
            }

            _loaded = true;
            if (companyPreview)
                companyPreview.text = _company;
            if (jobPreview)
                jobPreview.text = _job;
            if (resumePreview)
            {
                resumePreview.text =
                string.IsNullOrEmpty(_resume) ? "(이력서 없음)" : _resume;
            }

            SetWarning($"불러오기 완료: {Path.GetFileName(path)}");

            InterviewConfig.Company = _company;
            InterviewConfig.JobTitle = _job;
            InterviewConfig.Resume = _resume;
            InterviewConfig.IsReady = true;
            InterviewConfig.Prewarmed = false;
            if (warmup != null)
            {
                _preparing = true;
                startButton.interactable = false;
                _statusLine = "";
                warmup.Prepare(_company, _job, _resume);
            }
            else
                startButton.interactable = true;     
        }

        void OnStart()
        {
            if (_preparing)
            {
                SetWarning("면접관 준비 중입니다. 잠시만 기다려주세요.");
                return;
            }

            if (!_loaded || string.IsNullOrEmpty(_company) || string.IsNullOrEmpty(_job))
            {
                SetWarning("기업과 직무가 채워진 파일을 먼저 불러오세요.");
                return;
            }

            InterviewConfig.Company = _company;
            InterviewConfig.JobTitle = _job;
            InterviewConfig.Resume = _resume;
            InterviewConfig.IsReady = true;

            SceneManager.LoadScene(interviewSceneName);
        }

        private enum Section { None, Company, Job, Resume }

        public static bool TryParse(string raw, out string company, out string job, out string resume, out string error)
        {
            company = job = resume = "";
            error = "";
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "빈 파일입니다.";
                return false;
            }

            var sb = new Dictionary<Section, StringBuilder>
            {
                { Section.Company, new StringBuilder() },
                { Section.Job,     new StringBuilder() },
                { Section.Resume,  new StringBuilder() },
            };

            Section cur = Section.None;
            foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("[") && t.EndsWith("]") && t.Length >= 3)
                {
                    cur = Classify(t.Substring(1, t.Length - 2).Trim());
                    continue;
                }
                if (cur == Section.None) 
                    continue;
                if (sb[cur].Length > 0) 
                    sb[cur].Append('\n');

                sb[cur].Append(line);
            }

            company = sb[Section.Company].ToString().Trim();
            job = sb[Section.Job].ToString().Trim();
            resume = sb[Section.Resume].ToString().Trim();

            if (string.IsNullOrEmpty(company) || string.IsNullOrEmpty(job))
            {
                error = "[기업]과 [직무] 섹션이 모두 필요합니다. 파일 형식을 확인하세요.";
                return false;
            }
            return true;
        }

        private static Section Classify(string header)
        {
            if (header.Contains("기업") || header.Contains("회사")) 
                return Section.Company;
            if (header.Contains("직무") || header.Contains("직군") || header.Contains("포지션")) 
                return Section.Job;
            if (header.Contains("이력") || header.Contains("자기소개") || header.Contains("경력")) 
                return Section.Resume;

            return Section.None;
        }

        private void SetWarning(string msg)
        {
            _fileLine = msg ?? "";
            Redraw();
        }

        private void OnWarmupProgress(string msg)
        {
            _statusLine = msg ?? "";
            Redraw();
        }

        private void Redraw()
        {
            if (warningText == null) return;

            if (string.IsNullOrEmpty(_statusLine))
                warningText.text = _fileLine;
            else if (string.IsNullOrEmpty(_fileLine))
                warningText.text = _statusLine;
            else
                warningText.text = $"{_fileLine}\n{_statusLine}";
        }

        private void ClearPreview()
        {
            if (companyPreview) 
                companyPreview.text = "-";
            if (jobPreview) 
                jobPreview.text = "-";
            if (resumePreview) 
                resumePreview.text = "-";
        }

        private void OnWarmupFinished(bool ok, string msg)
        {
            _preparing = false;
            _statusLine = msg;
            Redraw();

            startButton.interactable = _loaded;
            if (!ok)
                Debug.LogWarning($"[InterviewSetup] 프리웜 실패: {msg}");
        }
    }
}