using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using IControlReporter.Models;
using IControlReporter.Models.Report;
using IControlReporter.Services;
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

    /// <summary>
    /// 📊 報表管理中心主頁面（整合手動生成與自動排程設定）
    /// </summary>
    public IActionResult Generator()
    {
        _logger.LogInformation("🔍 [報表中心] 使用者進入報表管理中心主頁面。");

            // 🎯【維運防呆關鍵】：明確宣告檔案路徑，並使用 FileShare.Read 確保讀到最新寫入的檔案
            string poolPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TagPools.json");
            ExecuteTagPool existingPool = new ExecuteTagPool { TagPool = new List<ExecuteTagPool.TagNode>() };
            
            if (System.IO.File.Exists(poolPath))
            {
                try
                {
                    // 🎯【核心技術】：改用 FileStream 搭配 FileShare.ReadWrite 
                    // 這樣可以確保即使剛寫入，也能讀到最新資料
                    using (var stream = new FileStream(poolPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(stream))
                    {
                        string json = reader.ReadToEnd();
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            existingPool = JsonSerializer.Deserialize<ExecuteTagPool>(json, 
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? existingPool;
                        }
                    }
                    _logger.LogInformation($"✅ [報表中心] 成功同步最新點位池，共 {existingPool.TagPool?.Count ?? 0} 筆。");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"🚨 [報表中心] 同步失敗！原因: {ex.Message}");
                }
            }
            

        // 2. 讀取報表排程參數設定 (ReportParams.json)
        string paramsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReportParams.json");
        List<ReportParameterModel> reportParams = new List<ReportParameterModel>();
        
        if (System.IO.File.Exists(paramsPath))
        {
            try
            {
                string paramsJson = System.IO.File.ReadAllText(paramsPath);
                if (!string.IsNullOrWhiteSpace(paramsJson))
                {
                    reportParams = JsonSerializer.Deserialize<List<ReportParameterModel>>(paramsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? reportParams;
                }
                _logger.LogInformation($"✅ [報表中心] 成功載入自動排程列表，共 {reportParams.Count} 筆配置中。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"🚨 [報表中心] 讀取 ReportParams.json 失敗！原因: {ex.Message}");
            }
        }
        
        ViewBag.TagPool = existingPool;
        ViewBag.ReportParams = reportParams;

        return View();
    }

    /// <summary>
    /// ⚡ 手動觸發：非同步計算並即時下載 Excel 報表檔案串流
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> GenerateManualReport([FromBody] ManualReportRequest request)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (request == null || request.SelectedDataPoints == null || !request.SelectedDataPoints.Any())
        {
            _logger.LogWarning("⚠️ [手動報表] 產出終止：前端傳入之請求為空或未選取任何測點欄位。");
            return BadRequest("未選取任何測點，無法產生報表。");
        }

        string pointsStr = string.Join(", ", request.SelectedDataPoints);
        _logger.LogInformation($"🚀 [手動報表] 接收到即時產出請求 -> 類型: {request.ReportType}, 目標時間: [{request.TargetTime}], 勾選測點: [{pointsStr}]");

        try
        {
            DateTime startTime, endTime;
            List<ReportResultDto> reportResult; 
            float maxMeterValue = 999999f; 
            string sheetName;
            string filePrefix;

            // 日報檔案設置
            if (request.ReportType.Equals("daily", StringComparison.OrdinalIgnoreCase))
            {
                if (!DateTime.TryParseExact(request.TargetTime, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime targetDate))
                {
                    _logger.LogWarning($"⚠️ [手動報表] 日期格式解析失敗，傳入值: {request.TargetTime} (預期格式: yyyy-MM-dd)");
                    return BadRequest("日期格式錯誤，請使用 yyyy-MM-dd");
                }

                startTime = targetDate.Date;          
                endTime = targetDate.Date.AddDays(1); 
                sheetName = $"{targetDate:yyyy-MM-dd} 日報表";
                filePrefix = "Daily_Report";

                reportResult = await _reportService.GetDailyReportDataAsync(startTime, endTime, request.SelectedDataPoints, maxMeterValue);
            }
            // 月報檔案設置
            else if (request.ReportType.Equals("monthly", StringComparison.OrdinalIgnoreCase))
            {
                if (!DateTime.TryParseExact(request.TargetTime, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime targetMonth))
                {
                    _logger.LogWarning($"⚠️ [手動報表] 月份格式解析失敗，傳入值: {request.TargetTime} (預期格式: yyyy-MM)");
                    return BadRequest("月份格式錯誤，請使用 yyyy-MM");
                }

                startTime = new DateTime(targetMonth.Year, targetMonth.Month, 1); 
                endTime = startTime.AddMonths(1);                               
                sheetName = $"{targetMonth:yyyy-MM} 月報表";
                filePrefix = "Monthly_Report";

                reportResult = await _reportService.GetMonthlyReportDataAsync(startTime, endTime, request.SelectedDataPoints, maxMeterValue);
            }
            else
            {
                _logger.LogWarning($"⚠️ [手動報表] 未知的報表類型: {request.ReportType}");
                return BadRequest("未知的報表類型。");
            }

            _logger.LogInformation($"📥 [手動報表] 後台數據計算完成，共取得 {reportResult.Count} 筆計算結果。開始執行 EPPlus 渲染並轉換二進位串流...");

            // 呼叫封裝好的 EPPlus 產生二進位陣列
            byte[] fileBytes = GenerateExcelReportBytes(request, reportResult, startTime, sheetName);
            string fileName = $"{filePrefix}_{request.TargetTime}.xlsx";

            stopwatch.Stop();
            _logger.LogInformation($"✨ [手動報表] 檔案產出完畢！輸出檔名: [{fileName}] (大小: {fileBytes.Length} Bytes)，總耗時: {stopwatch.ElapsedMilliseconds}ms");

            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"🚨 [手動報表] 手動觸發即時 Excel 下載時發生異常！目標時間: {request.TargetTime}。原因: {ex.Message}");
            return StatusCode(500, $"伺服器內部錯誤: {ex.Message}");
        }
    }

    /// <summary>
    /// 💾 整合：儲存 / 修改自動排程參數設定
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> SaveReportParam([FromBody] ReportParameterModel payload)
    {
        if (payload == null || string.IsNullOrWhiteSpace(payload.ReportTitle) || payload.SelectedDataPoints == null || !payload.SelectedDataPoints.Any())
        {
            _logger.LogWarning("⚠️ [排程設定] 儲存失敗：前端傳入之參數 incomplete (缺少標題、類型或測點欄位)。");
            return BadRequest(new { success = false, message = "請填寫完整的報表標題與選擇點位" });
        }
        
        _logger.LogInformation($"📝 [排程設定] 收到儲存請求 -> 排程標題: [{payload.ReportTitle}], 類型: {payload.ReportType}, 時間: {payload.ExecutionTime}, 儲存路徑: {payload.StoragePath}");

        try
        {
            string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReportParams.json");
            List<ReportParameterModel> allParams = new List<ReportParameterModel>();
            
            if (System.IO.File.Exists(filePath))
            {
                string json = await System.IO.File.ReadAllTextAsync(filePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    allParams = JsonSerializer.Deserialize<List<ReportParameterModel>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? allParams;
                }
            }
            
            var existing = allParams.FirstOrDefault(p => p.ReportTitle == payload.ReportTitle);
            if (existing != null)
            {
                _logger.LogInformation($"🔄 [排程設定] 偵測到重複標題，覆寫現有排程配置：[{payload.ReportTitle}]");
                existing.ReportType = payload.ReportType;
                existing.SelectedDataPoints = payload.SelectedDataPoints;
                existing.IsEnabled = payload.IsEnabled;
                existing.ExecutionTime = payload.ExecutionTime;
                existing.StoragePath = payload.StoragePath;
            }
            else
            {
                _logger.LogInformation($"➕ [排程設定] 新增全新報表排程至設定檔：[{payload.ReportTitle}]");
                allParams.Add(payload);
            }
            
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
            };
            
            await System.IO.File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(allParams, options));
            _logger.LogInformation($"✅ [排程設定] 報表參數設定檔寫入成功。當前總排程數: {allParams.Count} 筆。");

            return Json(new { success = true, message = "報表參數儲存成功！" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"🚨 [排程設定] 寫入 ReportParams.json 發生異常。排程名稱: {payload.ReportTitle}。錯誤原因: {ex.Message}");
            return Json(new { success = false, message = "儲存時發生錯誤：" + ex.Message });
        }
    }

    /// <summary>
    /// ❌ 整合：刪除指定的報表排程
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> DeleteReportParam([FromBody] DeleteRequestModel payload)
    {
        if (payload == null || string.IsNullOrWhiteSpace(payload.ReportTitle))
        {
            _logger.LogWarning("⚠️ [排程設定] 刪除失敗：未提供目標報表標題名稱。");
            return BadRequest(new { success = false, message = "請提供要刪除的標題" });
        }

        try
        {
            string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReportParams.json");
            List<ReportParameterModel> allParams = new List<ReportParameterModel>();
            
            if (System.IO.File.Exists(filePath))
            {
                string json = await System.IO.File.ReadAllTextAsync(filePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    allParams = JsonSerializer.Deserialize<List<ReportParameterModel>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? allParams;
                }
            }

            var item = allParams.FirstOrDefault(p => p.ReportTitle == payload.ReportTitle);
            if (item != null)
            {
                allParams.Remove(item);
                
                var options = new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
                };
                
                await System.IO.File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(allParams, options));
                
                _logger.LogInformation($"🗑️ [排程設定] 報表排程刪除成功！目標標題: [{payload.ReportTitle}]，賸餘排程數: {allParams.Count} 筆。");
                return Json(new { success = true, message = "刪除成功！" });
            }
            
            _logger.LogWarning($"⚠️ [排程設定] 刪除失敗：在設定檔中找不到標題為 [{payload.ReportTitle}] 的排程配置。");
            return Json(new { success = false, message = "找不到指定的設定" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"🚨 [排程設定] 刪除排程 [{payload.ReportTitle}] 時發生硬碟寫入異常。原因: {ex.Message}");
            return Json(new { success = false, message = "刪除時發生錯誤：" + ex.Message });
        }
    }

    // ====================================================================
    // 📦 Private Excel Helpers
    // ====================================================================

    private byte[] GenerateExcelReportBytes(ManualReportRequest request, List<ReportResultDto> reportResult, DateTime startTime, string sheetName)
    {
        ExcelPackage.License.SetNonCommercialPersonal("QQ Chuang");

        using (var package = new ExcelPackage())
        {
            var worksheet = package.Workbook.Worksheets.Add(sheetName);
            bool isDaily = request.ReportType.Equals("daily", StringComparison.OrdinalIgnoreCase);
            bool needTotalRow = reportResult.Count > request.SelectedDataPoints.Count;

            List<DateTime> timeline = ExcelEPPlusExtensions.GenerateTimeline(isDaily, startTime);
            int totalCols = 1 + request.SelectedDataPoints.Count;
            int titleRowIdx = 3;
            int dataStartRowIdx = 4;

            ExcelEPPlusExtensions.BuildFileHeader(worksheet, request.FileHeader, totalCols);
            ExcelEPPlusExtensions.BuildTimelineColumn(worksheet, timeline, needTotalRow, titleRowIdx, dataStartRowIdx);

            int actualCols = FillTagDataColumns(worksheet, request.SelectedDataPoints, reportResult, timeline, needTotalRow, titleRowIdx, dataStartRowIdx);

            ExcelEPPlusExtensions.ApplyTableStyles(worksheet, timeline.Count, needTotalRow, titleRowIdx, actualCols);

            worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();
            return package.GetAsByteArray();
        }
    }

    private int FillTagDataColumns(ExcelWorksheet worksheet, List<string> selectedDataPoints, List<ReportResultDto> reportResult, List<DateTime> timeline, bool needTotalRow, int titleRowIdx, int dataStartRowIdx)
    {
        int currentColumn = 2;
        var reportLookup = reportResult.ToLookup(r => r.PointsIdPoint);

        foreach (var tagName in selectedDataPoints)
        {
            var matchedIds = _reportService.GetTagIdsFromPool(new List<string> { tagName });
            if (!matchedIds.Any())
            {
                _logger.LogWarning($"⚠️ [Excel繪製] 點位池比對警告：勾選的名稱 [{tagName}] 無法對照出任何實體 Tag_ID，跳過此欄位繪製。");
                continue;
            }
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

    // ====================================================================
    // 📦 Request DTO Models
    // ====================================================================

    public class ManualReportRequest
    {
        public string FileHeader { get; set; } = string.Empty;
        public string ReportType { get; set; } = string.Empty;
        public string TargetTime { get; set; } = string.Empty;
        public List<string> SelectedDataPoints { get; set; } = new();
    }

    public class DeleteRequestModel
    {
        public string ReportTitle { get; set; } = string.Empty;
    }
}