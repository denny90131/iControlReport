using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IControlReporter.Models.Report;

namespace IControlReporter.Services
{
    public interface IReportService
    {
        /// <summary>
        /// 依據測點名稱清單對照出 Tag_ID 清單
        /// </summary>
        List<int> GetTagIdsFromPool(List<string> selectedNames);

        /// <summary>
        /// 💡 封裝：一鍵自動撈取資料 + 核心日報表 GroupBy 計算
        /// </summary>
        Task<List<ReportResultDto>> GetDailyReportDataAsync(DateTime startTime, DateTime endTime, List<string> selectedNames, float maxMeterValue = 999999f);

        /// <summary>
        /// 💡 封裝：一鍵自動撈取資料 + 核心月報表 GroupBy 計算
        /// </summary>
        Task<List<ReportResultDto>> GetMonthlyReportDataAsync(DateTime startTime, DateTime endTime, List<string> selectedNames, float maxMeterValue = 999999f);

        /// <summary>
        /// 💡 修正：將參數改為強型別 ReportRawDataDto，與 Service 保持完全一致
        /// </summary>
        List<ReportResultDto> CalculateDailyReport(List<ReportRawDataDto> rawData, DateTime reportStartTime, DateTime reportEndTime, float maxMeterValue);

        /// <summary>
        /// 💡 修正：將參數改為強型別 ReportRawDataDto，與 Service 保持完全一致
        /// </summary>
        List<ReportResultDto> CalculateMonthlyReport(List<ReportRawDataDto> rawData, DateTime startTime, DateTime endTime, float maxMeterValue);
    }
}