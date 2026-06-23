// --- 報表參數的 DTO 模型 ---
namespace IControlReporter.Models.Report
{
    public class ReportParameterModel
    {
        public string ReportType { get; set; }
        public string ReportTitle { get; set; }
        public List<string> SelectedDataPoints { get; set; }
        public bool IsEnabled { get; set; }
        public string ExecutionTime { get; set; }
        public string StoragePath { get; set; } = string.Empty;
    }
}