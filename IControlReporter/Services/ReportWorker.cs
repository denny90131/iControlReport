using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection; // 👈 確保引入 Scope 所需命名空間
using IControlReporter.Models.Report;
using IControlReporter.Unit.EEPLUS; // 👈 引入剛剛的 Excel 擴充方法命名空間
using OfficeOpenXml;

namespace IControlReporter.Services
{
    public class ReportWorker : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ReportWorker> _logger;
        private readonly string _jsonFilePath;

        // 在記憶體中維護目前的排程狀態（包含執行紀錄）
        private List<ReportSchedule> _cachedSchedules = new();
        private readonly object _lock = new object();

        public ReportWorker(IServiceProvider serviceProvider, IHostEnvironment env, ILogger<ReportWorker> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _jsonFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReportParams.json");
        }

        // 讀取報表生產排程池
        private void LoadSchedulesFromFile()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_jsonFilePath)) return;
                    using var stream = new FileStream(_jsonFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    string json = reader.ReadToEnd();

                    var freshSchedules = JsonSerializer.Deserialize<List<ReportSchedule>>(json) ?? new();
                    foreach (var fs in freshSchedules)
                    {
                        var old = _cachedSchedules.FirstOrDefault(s => s.ReportTitle == fs.ReportTitle);
                        if (old != null) fs.LastExecuted = old.LastExecuted;
                    }
                    _cachedSchedules = freshSchedules;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "從 JSON 檔案載入排程設定時發生錯誤。");
                }
            }
        }

        // 背景輪詢
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("【報表背景服務】已全面啟動。");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    LoadSchedulesFromFile();
                    DateTime now = DateTime.Now;
                    List<ReportSchedule> tasksToExecute = EvaluateSchedules(now);

                    if (tasksToExecute.Any())
                    {
                        // 💡 優化：多個報表任務同時非同步執行，不再彼此卡線
                        var tasks = tasksToExecute.Select(schedule => ProcessSingleReportAsync(schedule));
                        await Task.WhenAll(tasks);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "【報表背景服務】核心檢查輪詢發生未預期異常。");
                }
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        // 觸發報表生成執行程序
        private async Task ProcessSingleReportAsync(ReportSchedule schedule, DateTime? evaluationTime = null)
        {
            try
            {
                var points = schedule.SelectedDataPoints;
                if (points == null || !points.Any()) return;


                // 💡 修正：每次執行任務時建立獨立 Scope，安全取出 IReportService，絕不造成 DB 連線洩漏
                using var scope = _serviceProvider.CreateScope();
                var reportService = scope.ServiceProvider.GetRequiredService<IReportService>();

                // 💡 核心邏輯：外層測試有傳模擬時間就用模擬的，正常排程沒傳就用真實世界的 Today
                DateTime baseToday = evaluationTime?.Date ?? DateTime.Today.Date;

                DateTime startTime, endTime;
                bool isDaily = schedule.ReportType.Equals("daily", StringComparison.OrdinalIgnoreCase);
                float maxMeterValue = 999999f; // 💡 校正：電表最大翻轉上限對齊 999999f
                string sheetName;
                string filePrefix;
                if (isDaily)
                {
                    // 💡 檢查點 3：原本寫死 DateTime.Today 的地方，通通要換成 baseToday！
                    startTime = baseToday.AddDays(-1).Date; 
                    endTime = baseToday;              
                    sheetName = $"{startTime:yyyy-MM-dd} 日報表";
                    filePrefix = "Daily_Report";
                }
                else
                {
                    // 💡 檢查點 4：月報表也同步換成 baseToday！
                    startTime = new DateTime(baseToday.Year, baseToday.Month, 1).AddMonths(-1); 
                    endTime = new DateTime(baseToday.Year, baseToday.Month, 1);                 
                    sheetName = $"{startTime:yyyy-MM} 月報表";
                    filePrefix = "Monthly_Report";
                }

                _logger.LogInformation($"⏱️ [排程觸發] 開始處理解理報表「{schedule.ReportTitle}」({schedule.ReportType})...");

                List<ReportResultDto> reportResult;
                if (isDaily)
                {
                    // 💡 升級：直接呼叫新版一鍵非同步封裝方法！自動處理對照、撈取、翻轉補償、強型別回傳
                    reportResult = await reportService.GetDailyReportDataAsync(startTime, endTime, points, maxMeterValue);
                    
                    _logger.LogInformation($"✅ [日報表產出成功] 報表「{schedule.ReportTitle}」計算完成。共 {reportResult.Count} 筆時段資料。");
                }
                else
                {
                    reportResult = await reportService.GetMonthlyReportDataAsync(startTime, endTime, points, maxMeterValue);
                    
                    _logger.LogInformation($"✅ [月報表產出成功] 報表「{schedule.ReportTitle}」計算完成。共 {reportResult.Count} 筆點位資料。");
                }


                // 🚀 執行背景 Excel 實體二進位檔案建置
                byte[] fileBytes = GenerateExcelReportBytes(reportService, schedule, reportResult, startTime, sheetName);

                // --- 💡 核心優化：依據報表結算時間 (startTime) 建立時間軸資料夾層級 ---
                string yearFolderName = $"{startTime:yyyy}年";  // 例如 "2026年"
                string monthFolderName = $"{startTime:MM}月";  // 例如 "06月"

                // 1. 決定根目錄（如果有填自訂路徑就用自訂的，沒有就用預設的 Base 目錄）
                string baseDir = !string.IsNullOrWhiteSpace(schedule.StoragePath)
                    ? schedule.StoragePath
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GeneratedReports");

                // 2. 依照日報/月報規則，動態組裝多層資料夾路徑
                string exportDir;
                if (isDaily)
                {
                    // 日報結構：根目錄 \ 報表名稱 \ yyyy年 \ MM月
                    exportDir = Path.Combine(baseDir, schedule.ReportTitle, yearFolderName, monthFolderName);
                }
                else
                {
                    // 月報結構：根目錄 \ 報表名稱 \ yyyy年
                    exportDir = Path.Combine(baseDir, schedule.ReportTitle, yearFolderName);
                }

                // 3. 防呆：不論是日報的多層還是月報，Directory.CreateDirectory 會自動建立所有不存在的中間資料夾
                if (!Directory.Exists(exportDir))
                {
                    Directory.CreateDirectory(exportDir);
                }

                // 產出實體檔名 (以報表結算起始日期為標記，如 Daily_Report_主變電所_2026-06-22.xlsx)
                string dateMarker = isDaily ? $"{startTime:yyyy-MM-dd}" : $"{startTime:yyyy-MM}";
                string fileName = $"{filePrefix}_{schedule.ReportTitle}_{dateMarker}.xlsx";
                string fullPath = Path.Combine(exportDir, fileName);

                // 🚀 使用非同步 I/O 寫入檔案，不阻塞執行緒
                await File.WriteAllBytesAsync(fullPath, fileBytes);

                _logger.LogInformation($"✅ [排程存檔成功] 報表「{schedule.ReportTitle}」已安全寫入至: {fullPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ 產生報表「{schedule.ReportTitle}」時發生異常。");
            }
        }

        /// <summary>
        /// 🚀 核心邏輯：負責建立 Excel 封裝實體、初始化工作表、呼叫擴充方法建立框架，並回傳 Excel 的二進位 byte 陣列。
        /// </summary>
        /// <param name="reportService">用於查詢測點 ID 映射關係的服務層執行個體</param>
        /// <param name="schedule">目前執行的報表排程設定內容（內含報表類型、自訂點位等）</param>
        /// <param name="reportResult">已計算完畢並內含 DiffValue 的強型別報表數據結果集</param>
        /// <param name="startTime">報表結算起始時間（用來提供擴充方法產生時間軸）</param>
        /// <param name="sheetName">Excel 分頁（Worksheet）的名稱</param>
        /// <returns>代表 Excel 實體檔案的二進位 Byte 陣列</returns>
        private byte[] GenerateExcelReportBytes(IReportService reportService, ReportSchedule schedule, List<ReportResultDto> reportResult, DateTime startTime, string sheetName)
        {
            ExcelPackage.License.SetNonCommercialPersonal("QQ Chuang");

            using (var package = new ExcelPackage())
            {
                var worksheet = package.Workbook.Worksheets.Add(sheetName);
                bool isDaily = schedule.ReportType.Equals("daily", StringComparison.OrdinalIgnoreCase);

                bool needTotalRow = reportResult.Count > schedule.SelectedDataPoints.Count;

                List<DateTime> timeline = ExcelEPPlusExtensions.GenerateTimeline(isDaily, startTime);
                int totalCols = 1 + schedule.SelectedDataPoints.Count;
                int titleRowIdx = 3;
                int dataStartRowIdx = 4;

                // 抬頭名稱直接套用後台設定的 ReportTitle 
                ExcelEPPlusExtensions.BuildFileHeader(worksheet, schedule.ReportTitle, totalCols);
                ExcelEPPlusExtensions.BuildTimelineColumn(worksheet, timeline, needTotalRow, titleRowIdx, dataStartRowIdx);

                // 💡 往下傳遞 reportService
                int actualCols = FillTagDataColumns(reportService, worksheet, schedule.SelectedDataPoints, reportResult, timeline, needTotalRow, titleRowIdx, dataStartRowIdx);

                ExcelEPPlusExtensions.ApplyTableStyles(worksheet, timeline.Count, needTotalRow, titleRowIdx, actualCols);

                worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();
                return package.GetAsByteArray();
            }
        }
        /// <summary>
        /// 📊 欄位填充邏輯：將分組後的報表數據（ReportResultDto）橫向展開，逐欄繪製點位標題、縱向填入數據，並埋入動態 SUM 公式。
        /// </summary>
        private int FillTagDataColumns(IReportService reportService, ExcelWorksheet worksheet, List<string> selectedDataPoints, List<ReportResultDto> reportResult, List<DateTime> timeline, bool needTotalRow, int titleRowIdx, int dataStartRowIdx)
        {
            int currentColumn = 2;
            var reportLookup = reportResult.ToLookup(r => r.PointsIdPoint);

            foreach (var tagName in selectedDataPoints)
            {
                // ✅ 修正：改用傳進來的區域變數 reportService
                var matchedIds = reportService.GetTagIdsFromPool(new List<string> { tagName });
                if (!matchedIds.Any()) continue;
                int targetId = matchedIds.First();

                worksheet.Cells[titleRowIdx, currentColumn].Value = tagName;
                var tagDataDict = reportLookup[targetId].ToDictionary(r => r.TargetTime);

                for (int i = 0; i < timeline.Count; i++)
                {
                    int rowIdx = i + dataStartRowIdx;
                    DateTime targetHour = timeline[i];
                    float value = tagDataDict.TryGetValue(targetHour, out var matchedData) ? matchedData.DiffValue : 0f;
                    
                    worksheet.Cells[rowIdx, currentColumn].Value = value;
                    worksheet.Cells[rowIdx, currentColumn].Style.Numberformat.Format = "#,##0.00";
                }

                if (needTotalRow)
                {
                    string colLetter = ExcelEPPlusExtensions.GetExcelColumnName(currentColumn);
                    int totalRowIdx = dataStartRowIdx + timeline.Count;
                    int dataEndRowIdx = totalRowIdx - 1;

                    worksheet.Cells[totalRowIdx, currentColumn].Formula = $"SUM({colLetter}{dataStartRowIdx}:{colLetter}{dataEndRowIdx})";
                    worksheet.Cells[totalRowIdx, currentColumn].Style.Numberformat.Format = "#,##0.00";
                }
                currentColumn++;
            }

            return currentColumn - 1;
        }

        // ======================== 💡Test💡 ==================================

        // 💡 測試 排程安排觸發在地驗證介面（維持你原本的在地測試機制，方便 Test.cs 調用）
        public List<ReportSchedule> EvaluateSchedules(DateTime evaluationTime)
        {
            List<ReportSchedule> tasksToExecute = new();
            lock (_lock)
            {
                foreach (var schedule in _cachedSchedules.Where(s => s.IsEnabled))
                {
                    if (schedule.ReportType.Equals("daily", StringComparison.OrdinalIgnoreCase))
                    {
                        if (schedule.ExecutionTime == evaluationTime.ToString("HH:mm") && 
                            (schedule.LastExecuted == null || schedule.LastExecuted.Value.Date < evaluationTime.Date))
                        {
                            tasksToExecute.Add(schedule);
                            schedule.LastExecuted = evaluationTime; 
                        }
                    }
                    else if (schedule.ReportType.Equals("monthly", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(schedule.ExecutionTime, out int targetDay))
                        {
                            if (evaluationTime.Day == targetDay && evaluationTime.Hour == 14 && evaluationTime.Minute == 05 &&
                                (schedule.LastExecuted == null || schedule.LastExecuted.Value.Month != evaluationTime.Month))
                            {
                                tasksToExecute.Add(schedule);
                                schedule.LastExecuted = evaluationTime;
                            }
                        }
                    }
                }
            }
            return tasksToExecute;
        }
    }
}