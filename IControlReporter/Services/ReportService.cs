using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using IControlReporter.Models.Report;
using IControlReporter.Data;
using OfficeOpenXml;
using OfficeOpenXml.Style;

namespace IControlReporter.Services
{
    // 💡 定義強型別 DTO，徹底取代 dynamic，效能提升數倍且防呆
    public class ReportRawDataDto
    {
        public int PointsIdPoint { get; set; }
        public DateTime EventTime { get; set; }
        public float? Value { get; set; }
    }

    // 💡 報表計算結果強型別
    public class ReportResultDto
    {
        public int PointsIdPoint { get; set; }
        public DateTime TargetTime { get; set; }
        public float DiffValue { get; set; }
    }

    public class ReportService : IReportService
    {
        private readonly ILogger<ReportService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly string _tagPoolFilePath;

        public ReportService(IHostEnvironment env, ILogger<ReportService> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            string exeBinDir = AppDomain.CurrentDomain.BaseDirectory;
            _tagPoolFilePath = Path.Combine(exeBinDir, "TagPools.json");
        }

        public List<int> GetTagIdsFromPool(List<string> selectedNames)
        {
            try
            {
                if (!File.Exists(_tagPoolFilePath))
                {
                    _logger.LogError($"找不到 TagPool 檔案：{_tagPoolFilePath}");
                    return new List<int>();
                }
                string json = File.ReadAllText(_tagPoolFilePath);
                var poolWrapper = JsonSerializer.Deserialize<ExecuteTagPool>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (poolWrapper?.TagPool == null) return new List<int>();
                return poolWrapper.TagPool.Where(t => selectedNames.Contains(t.Name)).Select(t => t.Tag_ID).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析 TagPools.json 失敗");
                return new List<int>();
            }
        }

        /// <summary>
        /// 💡 封裝：一鍵撈取資料 + 核心日報表 GroupBy 計算 (支援跨日邊界優化)
        /// </summary>
        public async Task<List<ReportResultDto>> GetDailyReportDataAsync(DateTime startTime, DateTime endTime, List<string> selectedNames, float maxMeterValue = 999999f)
        {
            try
            {
                var targetTagIds = GetTagIdsFromPool(selectedNames);
                if (!targetTagIds.Any())
                {
                    _logger.LogWarning("⚠️ 傳入的測點名稱對照出來的 Tag_ID 清單為空，終止撈取。");
                    return new List<ReportResultDto>();
                }

                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                _logger.LogInformation($"📊 [報表服務] 開始撈取資料庫歷史點位。區間: {startTime:yyyy-MM-dd HH:mm} ~ {endTime:yyyy-MM-dd HH:mm}");

                // 💡 【工控優化】擴大查詢右邊界 5 分鐘：
                // 確保撈得到 endTime 點位（例如 00:00）之後的第一筆電表讀值，作為前一天最後一小時計算的 End 基準點
                DateTime optimizedEndTime = endTime.AddMinutes(5);

                var rawDataFromDb = await dbContext.AnalogEvents
                    .AsNoTracking()
                    .Where(x => targetTagIds.Contains(x.PointsIdPoint) && x.EventTime >= startTime && x.EventTime <= optimizedEndTime)
                    .Select(x => new ReportRawDataDto // 💡 改用強型別 DTO 投射
                    { 
                        PointsIdPoint = x.PointsIdPoint, 
                        EventTime = x.EventTime, 
                        Value = x.Value 
                    })
                    .ToListAsync();

                _logger.LogInformation($"✅ [報表服務] 成功撈取 {rawDataFromDb.Count} 筆原始資料，開始進行日報表翻轉補償計算。");

                // 💡 修正：呼叫端要補上 startTime 參數！
                return CalculateDailyReport(rawDataFromDb, startTime, endTime, maxMeterValue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "封裝撈取日報表資料並計算時發生異常。");
                return new List<ReportResultDto>();
            }
        }

        /// <summary>
        /// 💡 核心日報表計算 (純粹按每小時內的第一筆與最後一筆計算、處理電表翻轉、無資料自動補 0)
        /// </summary>
        public List<ReportResultDto> CalculateDailyReport(List<ReportRawDataDto> rawData, DateTime reportStartTime, DateTime reportEndTime, float maxMeterValue)
        {
            var results = new List<ReportResultDto>();

            // 1. 生成今日報表應有的 24 小時完整時間軸基準
            var fullHourTimeline = new List<DateTime>();
            DateTime tempHour = new DateTime(reportStartTime.Year, reportStartTime.Month, reportStartTime.Day, reportStartTime.Hour, 0, 0);
            DateTime endHourBoundary = new DateTime(reportEndTime.Year, reportEndTime.Month, reportEndTime.Day, reportEndTime.Hour, 0, 0);

            while (tempHour < endHourBoundary)
            {
                fullHourTimeline.Add(tempHour);
                tempHour = tempHour.AddHours(1);
            }

            if (!rawData.Any())
            {
                _logger.LogWarning($"⚠️ [驗證警告] 傳入的原始資料庫數據為空 (rawData count = 0)，無法進行計算。");
                return results; 
            }

            // 2. 先依據測點進行第一層分組
            var pointGroups = rawData.GroupBy(x => x.PointsIdPoint);

            foreach (var pointGroup in pointGroups)
            {
                int pointId = pointGroup.Key;
                var orderedData = pointGroup.OrderBy(x => x.EventTime).ToList();

                _logger.LogInformation($"📊 [報表數據驗證核心] 開始計算點位 ID: {pointId} | 本次總原始筆數: {orderedData.Count} 筆");
                _logger.LogInformation($"{"時間軸 (Hour)",-18} | {"狀態",-4} | {"每小時第一筆 (First)",-20} | {"每小時最後一筆 (Last)",-20} | {"翻轉補償",-4} | {"計算結果 (DiffValue)",-15}");

                // 💡 先把這點位「每個小時的第一筆資料」撈出來，做成以整點為 Key 的 Dictionary
                var firstRecordOfEachHour = orderedData
                    .GroupBy(x => new DateTime(x.EventTime.Year, x.EventTime.Month, x.EventTime.Day, x.EventTime.Hour, 0, 0))
                    .ToDictionary(
                        g => g.Key, 
                        g => g.OrderBy(x => x.EventTime).First() // 只拿該小時最早的那一筆
                    );

                // 3. 遍歷完整的 24 小時基準
                foreach (DateTime currentHour in fullHourTimeline)
                {
                    float diffValue = 0f;
                    string status = "OK";
                    string isFlipped = "NO";
                    string currentValStr = "N/A";
                    string nextValStr = "N/A";

                    DateTime nextHour = currentHour.AddHours(1);

                    // 💡 檢查是否有「這一小時的第一筆」與「下一小時的第一筆」
                    bool hasCurrentFirst = firstRecordOfEachHour.TryGetValue(currentHour, out var currentFirstRecord);
                    bool hasNextFirst = firstRecordOfEachHour.TryGetValue(nextHour, out var nextFirstRecord);

                    if (hasCurrentFirst && hasNextFirst)
                    {
                        float currentFirstVal = currentFirstRecord.Value ?? 0f;
                        float nextFirstVal = nextFirstRecord.Value ?? 0f;

                        currentValStr = $"{currentFirstVal:F2} ({currentFirstRecord.EventTime:mm:ss})";
                        nextValStr = $"{nextFirstVal:F2} ({nextFirstRecord.EventTime:mm:ss})";

                        // 電表翻轉補償演算法 (下小時第一筆 >= 這小時第一筆)
                        if (nextFirstVal >= currentFirstVal)
                        {
                            diffValue = nextFirstVal - currentFirstVal;
                        }
                        else
                        {
                            diffValue = (maxMeterValue - currentFirstVal) + nextFirstVal;
                            isFlipped = "YES 🔥"; 
                            _logger.LogWarning($"⚠️ [電表翻轉補償] 測點 {pointId} 在 {currentHour:yyyy-MM-dd HH:mm} 跨時段發生歸零。前值: {currentFirstVal}, 後值: {nextFirstVal}, 補償後增量: {diffValue}");
                        }
                    }
                    else
                    {
                        // 💡 防呆機制：如果缺了任一筆，就判定為資料不連續需補點（差值給 0f）
                        status = "補點";
                        diffValue = 0f;

                        currentValStr = hasCurrentFirst ? $"{currentFirstRecord.Value ?? 0f:F2} ({currentFirstRecord.EventTime:mm:ss})" : "無資料";
                        nextValStr = hasNextFirst ? $"{nextFirstRecord.Value ?? 0f:F2} ({nextFirstRecord.EventTime:mm:ss})" : "無資料";
                    }

                    // 🚀 將跨小時的精確比對數值與時間點刷出至 Console
                    string hourStr = currentHour.ToString("yyyy-MM-dd HH:mm");
                    _logger.LogInformation($"{hourStr,-18} | {status,-4} | {currentValStr,-25} | {nextValStr,-25} | {isFlipped,-6} | {diffValue,15:F2}");

                    results.Add(new ReportResultDto
                    {
                        PointsIdPoint = pointId,
                        TargetTime = currentHour,
                        DiffValue = diffValue
                    });
                }
            }

            return results.OrderBy(r => r.PointsIdPoint).ThenBy(r => r.TargetTime).ToList();
        }
        
        
        /// <summary>
        /// 💡 補齊：月報表核心撈取與計算（按月份整體結算）
        /// </summary>
        public async Task<List<ReportResultDto>> GetMonthlyReportDataAsync(DateTime startTime, DateTime endTime, List<string> selectedNames, float maxMeterValue = 999999f)
        {
            try
            {
                var targetTagIds = GetTagIdsFromPool(selectedNames);
                if (!targetTagIds.Any()) return new List<ReportResultDto>();

                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // 擴大邊界撈取跨月第一筆資料
                DateTime optimizedEndTime = endTime.AddHours(2);

                var rawDataFromDb = await dbContext.AnalogEvents
                    .AsNoTracking()
                    .Where(x => targetTagIds.Contains(x.PointsIdPoint) && x.EventTime >= startTime && x.EventTime <= optimizedEndTime)
                    .Select(x => new ReportRawDataDto { PointsIdPoint = x.PointsIdPoint, EventTime = x.EventTime, Value = x.Value })
                    .ToListAsync();

                return CalculateMonthlyReport(rawDataFromDb, startTime, endTime, maxMeterValue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "封裝撈取月報表資料時發生異常。");
                return new List<ReportResultDto>();
            }
        }

        public List<ReportResultDto> CalculateMonthlyReport(List<ReportRawDataDto> rawData, DateTime startTime, DateTime endTime, float maxMeterValue)
        {
            var results = new List<ReportResultDto>();
            if (!rawData.Any()) return results;

            // 1. 動態建立該月份完整的「天」時間軸基準 (例如 6/1, 6/2 ... 6/30)
            var fullDayTimeline = new List<DateTime>();
            DateTime tempDay = startTime.Date;
            while (tempDay < endTime.Date)
            {
                fullDayTimeline.Add(tempDay);
                tempDay = tempDay.AddDays(1);
            }

            // 2. 依據測點進行第一層分組
            var pointGroups = rawData.GroupBy(x => x.PointsIdPoint);

            foreach (var pointGroup in pointGroups)
            {
                int pointId = pointGroup.Key;
                var orderedData = pointGroup.OrderBy(x => x.EventTime).ToList();

                // 💡 【關鍵優化】先把這點位「每一天 的第一筆資料」撈出來，做成以「日期 (Date)」為 Key 的 Dictionary
                var firstRecordOfEachDay = orderedData
                    .GroupBy(x => x.EventTime.Date)
                    .ToDictionary(
                        g => g.Key, 
                        g => g.OrderBy(x => x.EventTime).First() // 只拿每天最早的那一筆
                    );

                // 3. 遍歷當月每一天，精準計算當天的跨日差值
                for (int i = 0; i < fullDayTimeline.Count; i++)
                {
                    DateTime currentDay = fullDayTimeline[i];
                    DateTime nextDay = currentDay.AddDays(1);

                    float diffValue = 0f;

                    // 💡 檢查是否有「這一天 的第一筆」與「隔一天 的第一筆」
                    bool hasCurrentFirst = firstRecordOfEachDay.TryGetValue(currentDay, out var currentDayFirst);
                    bool hasNextFirst = firstRecordOfEachDay.TryGetValue(nextDay, out var nextDayFirst);

                    if (hasCurrentFirst && hasNextFirst)
                    {
                        float currentFirstVal = currentDayFirst.Value ?? 0f;
                        float nextFirstVal = nextDayFirst.Value ?? 0f;

                        // 電表翻轉補償演算法 (隔天第一筆 >= 當天第一筆)
                        if (nextFirstVal >= currentFirstVal)
                        {
                            diffValue = nextFirstVal - currentFirstVal;
                        }
                        else
                        {
                            diffValue = (maxMeterValue - currentFirstVal) + nextFirstVal;
                            _logger.LogWarning($"⚠️ [月報表翻轉補償] 測點 {pointId} 在跨日邊界 {currentDay:yyyy-MM-dd} -> {nextDay:yyyy-MM-dd} 發生歸零。前值: {currentFirstVal}, 後值: {nextFirstVal}, 補償後增量: {diffValue}");
                        }
                    }
                    else
                    {
                        // 💡 防呆機制：如果缺了任一天的起點資料，判定數據不連續，當天用量自動補 0f
                        diffValue = 0f;
                    }

                    results.Add(new ReportResultDto
                    {
                        PointsIdPoint = pointId,
                        TargetTime = currentDay, // yyyy-MM-dd 00:00:00
                        DiffValue = diffValue
                    });
                }
            }

            return results.OrderBy(r => r.PointsIdPoint).ThenBy(r => r.TargetTime).ToList();
        }

    }
}