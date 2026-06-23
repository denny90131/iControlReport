using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace IControlReporter.Models.Report
{
    public class ReportSchedule
    {
        public string ReportType { get; set; } = "daily";       // "daily" 或 "monthly"
        public string ReportTitle { get; set; } = string.Empty;  // 報表名稱
        public List<string> SelectedDataPoints { get; set; } = new(); // 勾選的 VCB 測點
        public bool IsEnabled { get; set; }
        public string ExecutionTime { get; set; } = string.Empty; // "08:00" 或 "1"

        public string StoragePath { get; set; } = string.Empty;
        
        // 💡 僅存在記憶體中，不序列化回 JSON。用來避免同一分鐘或同一天重複觸發
        [JsonIgnore]
        public DateTime? LastExecuted { get; set; }
    }
}