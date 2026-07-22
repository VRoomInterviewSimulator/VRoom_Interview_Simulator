namespace VRoom.Backend
{
    /// <summary>
    /// SetupScene 에서 입력한 지원 정보를 면접 씬으로 넘기기 위한 static 홀더.
    /// static 이라 씬 전환에도 값이 유지된다. MonoBehaviour 가 아니므로
    /// 씬에 붙이지 않는다(파일만 존재하면 됨).
    /// </summary>
    public static class InterviewConfig
    {
        public static string Company = "";
        public static string JobTitle = "";
        public static string Resume = "";
        public static bool IsReady = false;
        public static string SessionId = "default";
        public static bool Prewarmed = false;

        public static string CompanyShort
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Company)) return "";
                var lines = Company.Trim().Split('\n');
                return lines[0].Trim();
            }
        }

        public static string JobTitleShort
        {
            get
            {
                if (string.IsNullOrWhiteSpace(JobTitle)) return "";
                var lines = JobTitle.Trim().Split('\n');
                return lines[0].Trim();
            }
        }
    }
}