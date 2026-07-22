using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace VRoom.Backend
{
    /// <summary>
    /// SetupScene 전용. txt 를 불러온 직후 백엔드 /session/prepare 를 호출해
    ///   - 세션 선생성
    ///   - TTS 워커 소켓 선연결
    ///   - 첫 질문(템플릿) 음성 선합성 캐시
    /// 를 끝내 둔다. 면접 씬 진입 시 첫 발화 지연이 사실상 사라진다.
    /// </summary>
    public class BackendWarmup : MonoBehaviour
    {
        [Header("백엔드 (BackendControlClient 와 같은 호스트)")]
        [Tooltip("예: http://127.0.0.1:8080  (뒤에 / 붙이지 말 것)")]
        public string backendHttp = "http://127.0.0.1:8080";

        [Tooltip("STT 워커가 백엔드에 붙일 때 쓰는 session_id 와 동일해야 한다.")]
        public string sessionId = "default";

        [Header("타임아웃(초)")]
        public int healthTimeout = 5;
        public int prepareTimeout = 90;

        public bool IsReady { get; private set; }

        public event Action<bool, string> OnFinished;
        public event Action<string> OnProgress;

        public void Prepare(string company, string jobTitle, string resume)
        {
            StopAllCoroutines();
            IsReady = false;
            StartCoroutine(PrepareRoutine(company, jobTitle, resume));
        }

        private IEnumerator PrepareRoutine(string company, string jobTitle, string resume)
        {
            string baseUrl = backendHttp.TrimEnd('/');
            OnProgress?.Invoke("백엔드 연결 확인 중...");
            using (var health = UnityWebRequest.Get(baseUrl + "/health"))
            {
                health.timeout = healthTimeout;
                yield return health.SendWebRequest();

                if (health.result != UnityWebRequest.Result.Success)
                {
                    OnFinished?.Invoke(false, $"백엔드({baseUrl})에 연결할 수 없습니다: {health.error}");
                    yield break;
                }
                Debug.Log($"[Warmup] /health OK: {health.downloadHandler.text}");
            }
            OnProgress?.Invoke("면접관 첫 질문 음성을 준비하는 중입니다...");

            string json =
                "{\"session_id\":\"" + Escape(sessionId) + "\"," +
                "\"company\":\"" + Escape(company) + "\"," +
                "\"job_title\":\"" + Escape(jobTitle) + "\"," +
                "\"resume\":\"" + Escape(resume) + "\"}";

            using (var req = new UnityWebRequest(baseUrl + "/session/prepare", "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = prepareTimeout;

                float t0 = Time.realtimeSinceStartup;
                yield return req.SendWebRequest();
                float elapsed = Time.realtimeSinceStartup - t0;

                if (req.result != UnityWebRequest.Result.Success)
                {
                    string detail = req.downloadHandler != null ? req.downloadHandler.text : "";
                    Debug.LogError($"[Warmup] prepare 실패 ({req.responseCode})\n" +
                                   $"error={req.error}\ndetail={detail}\n보낸 JSON={json}");
                    OnFinished?.Invoke(false, $"첫 질문 사전 준비 실패: {req.error}");
                    yield break;
                }

                Debug.Log($"[Warmup] prepare 완료 ({elapsed:F1}s): {req.downloadHandler.text}");
            }

            InterviewConfig.SessionId = sessionId;
            InterviewConfig.Prewarmed = true;
            IsReady = true;
            OnFinished?.Invoke(true, "면접 준비 완료. 시작을 눌러주세요.");
        }

        private static string Escape(string s)
            => (s ?? "")
               .Replace("\\", "\\\\")
               .Replace("\"", "\\\"")
               .Replace("\r", "")
               .Replace("\n", "\\n")
               .Replace("\t", " ");
    }
}