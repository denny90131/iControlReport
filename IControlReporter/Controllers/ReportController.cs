using System;
using Microsoft.AspNetCore.Mvc;
using IControlReporter.Models.Report;
using IControlReporter.Services;
using System.Text.Json;
using System.Globalization;
using IControlReporter.Unit.EEPLUS;
using OfficeOpenXml;


namespace IControlReporter.Controllers;

public class ReportController : Controller
{
    private readonly IReportService _reportService; 
    private readonly ILogger<ReportController> _logger;

    public ReportController(IReportService reportService, ILogger<ReportController> logger)
    {
        _reportService = reportService;
        _logger = logger;
    }

    public IActionResult Generator()
    {
        // 💡 只讀取目前的點位池 (TagPools.json)，供畫面呈現可選取的點位
        string filePath = Path.Combine(Directory.GetCurrentDirectory(), "TagPools.json");
        
        // 💡 修正：動態內聯的 List 型別必須嚴格對齊 ExecuteTagPool.TagNode
        ExecuteTagPool existingPool = new ExecuteTagPool { TagPool = new List<ExecuteTagPool.TagNode>() };
        
        if (System.IO.File.Exists(filePath))
        {
            string json = System.IO.File.ReadAllText(filePath);
            if (!string.IsNullOrWhiteSpace(json))
            {
                // 💡 加上不區分大小寫的解析設定，確保 JSON 對應更安全
                existingPool = JsonSerializer.Deserialize<ExecuteTagPool>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? existingPool;
            }
        }
        ViewBag.TagPool = existingPool;

        return View();
    }

    [HttpPost]
    public async Task<IActionResult> GenerateManualReport([FromBody] ManualReportRequest request)
    {
        try
        {
            // 1. 基本安全防呆
            if (request == null || request.SelectedDataPoints == null || !request.SelectedDataPoints.Any())
            {
                return BadRequest("未選取任何測點，無法產生報表。");
            }

            _logger.LogInformation($"手動報表流式下載觸發 - 類型: {request.ReportType}, 目標時間: {request.TargetTime}");

            DateTime startTime, endTime;
            List<ReportResultDto> reportResult; // 💡 保持在這裡宣告
            float maxMeterValue = 999999f; // 電表翻轉上限
            string sheetName = "日用電總表";
            string filePrefix = "Daily_Report";

            // 日報檔案設置
            if (request.ReportType.Equals("daily", StringComparison.OrdinalIgnoreCase))
            {
                // 💡 假設前端日報表傳入 "2026-06-22"
                if (!DateTime.TryParseExact(request.TargetTime, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime targetDate))
                {
                    return BadRequest("日期格式錯誤，請使用 yyyy-MM-dd");
                }

                startTime = targetDate.Date;          // 00:00:00
                endTime = targetDate.Date.AddDays(1); // 隔天 00:00:00
                sheetName = $"{targetDate:yyyy-MM-dd} 日報表";
                filePrefix = "Daily_Report";

                // 💡 一鍵調用日報表（內部自帶跨日補點與自動補 0 邏輯）
                reportResult = await _reportService.GetDailyReportDataAsync(startTime, endTime, request.SelectedDataPoints, maxMeterValue);
            }
            //月報檔案設置
            else if (request.ReportType.Equals("monthly", StringComparison.OrdinalIgnoreCase))
            {
                // 💡 假設前端月報表傳入 "2026-06"
                if (!DateTime.TryParseExact(request.TargetTime, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime targetMonth))
                {
                    return BadRequest("月份格式錯誤，請使用 yyyy-MM");
                }

                startTime = new DateTime(targetMonth.Year, targetMonth.Month, 1); // 該月 1 號 00:00
                endTime = startTime.AddMonths(1);                                // 下個月 1 號 00:00
                sheetName = $"{targetMonth:yyyy-MM} 月報表";
                filePrefix = "Monthly_Report";

                // 💡 一鍵調用月報表
                reportResult = await _reportService.GetMonthlyReportDataAsync(startTime, endTime, request.SelectedDataPoints, maxMeterValue);
            }
            else
            {
                return BadRequest("未知的報表類型。");
            }


            // 3. 🚀 呼叫封裝好的 Excel 產生方法
            byte[] fileBytes = GenerateExcelReportBytes(request, reportResult, startTime, sheetName);
            string fileName = $"{filePrefix}_{request.TargetTime}.xlsx";

            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "手動觸發即時 Excel 下載時發生異常。");
            return StatusCode(500, $"伺服器內部錯誤: {ex.Message}");
        }
    }
    
   // 生成Excel報表 (日報/月報通用)
    private byte[] GenerateExcelReportBytes(ManualReportRequest request, List<ReportResultDto> reportResult, DateTime startTime, string sheetName)
    {
        ExcelPackage.License.SetNonCommercialPersonal("QQ Chuang");

        using (var package = new ExcelPackage())
        {
            var worksheet = package.Workbook.Worksheets.Add(sheetName);
            bool isDaily = request.ReportType.Equals("daily", StringComparison.OrdinalIgnoreCase);

            // 💡 關鍵：只要計算出來的結果大於選取的點位數，就代表有多天/多小時的資料，需要合計列
            bool needTotalRow = reportResult.Count > request.SelectedDataPoints.Count;

            // 1. 初始化時間軸資料與結構
            List<DateTime> timeline = ExcelEPPlusExtensions.GenerateTimeline(isDaily, startTime);
            int totalCols = 1 + request.SelectedDataPoints.Count;
            int titleRowIdx = 3;
            int dataStartRowIdx = 4;

            // 2. 建立大抬頭與時間軸欄位
            ExcelEPPlusExtensions.BuildFileHeader(worksheet, request.FileHeader, totalCols);
            ExcelEPPlusExtensions.BuildTimelineColumn(worksheet, timeline, needTotalRow, titleRowIdx, dataStartRowIdx);

            // 3. 填入各個 Tag 點位數據與公式
            int actualCols = FillTagDataColumns(worksheet, request.SelectedDataPoints, reportResult, timeline, needTotalRow, titleRowIdx, dataStartRowIdx);

            // 4. 美化樣式與調校外觀
            ExcelEPPlusExtensions.ApplyTableStyles(worksheet, timeline.Count, needTotalRow, titleRowIdx, actualCols);

            worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();
            return package.GetAsByteArray();
        }
    }

    // 填入點位數據與加總公式
    private int FillTagDataColumns(ExcelWorksheet worksheet, List<string> selectedDataPoints, List<ReportResultDto> reportResult, List<DateTime> timeline, bool needTotalRow, int titleRowIdx, int dataStartRowIdx)
    {
        int currentColumn = 2;
        var reportLookup = reportResult.ToLookup(r => r.PointsIdPoint);

        foreach (var tagName in selectedDataPoints)
        {
            var matchedIds = _reportService.GetTagIdsFromPool(new List<string> { tagName });
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


    // 前端DTO回傳
    public class ManualReportRequest
    {
        public string FileHeader {get; set;} = string.Empty;
        public string ReportType { get; set; } = string.Empty;
        public string TargetTime { get; set; } = string.Empty;
        public List<string> SelectedDataPoints { get; set; } = new();
    }
}