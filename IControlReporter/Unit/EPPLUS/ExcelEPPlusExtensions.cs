using System;
using System.Collections.Generic;
using System.Linq;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using IControlReporter.Models.Report;

namespace IControlReporter.Unit.EEPLUS
{
    public static class ExcelEPPlusExtensions
    {
        // 將 Excel 欄位數字（從 1 開始）轉換為標準英文字母（如 1->A, 2->B, 27->AA）
        public static string GetExcelColumnName(int columnNumber)
        {
            string columnName = "";
            while (columnNumber > 0)
            {
                int modulo = (columnNumber - 1) % 26;
                columnName = Convert.ToChar('A' + modulo) + columnName;
                columnNumber = (columnNumber - 1) / 26;
            }
            return columnName;
        }

        /// <summary>
        /// 💡 升級：時間軸產生器（支援日報 24 小時與月報當月每日）
        /// </summary>
        public static List<DateTime> GenerateTimeline(bool isDaily, DateTime startTime)
        {
            if (isDaily)
            {
                // 日報表：24 個小時
                return Enumerable.Range(0, 24).Select(h => startTime.AddHours(h)).ToList();
            }
            else
            {
                // 月報表：產出該月份的每一天 (例如 6/1 ~ 6/30)
                var timeline = new List<DateTime>();
                DateTime temp = startTime.Date;
                DateTime end = startTime.AddMonths(1).Date;
                while (temp < end)
                {
                    timeline.Add(temp);
                    temp = temp.AddDays(1);
                }
                return timeline;
            }
        }

        // 建立主檔案大抬頭
        public static void BuildFileHeader(ExcelWorksheet worksheet, string headerText, int totalCols)
        {
            int headerRow = 1;
            using (var range = worksheet.Cells[headerRow, 1, headerRow, totalCols])
            {
                range.Value = headerText;
                range.Merge = true;
                range.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                range.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
                range.Style.Font.Bold = true;
                range.Style.Font.Size = 16;
            }
            worksheet.Row(headerRow).Height = 40;
        }

        /// <summary>
        /// 💡 升級：建立 A 欄時間軸（改用 showTotalRow 來決定要不要塞「合計」）
        /// </summary>
        public static void BuildTimelineColumn(ExcelWorksheet worksheet, List<DateTime> timeline, bool showTotalRow, int titleRowIdx, int dataStartRowIdx)
        {
            worksheet.Cells[titleRowIdx, 1].Value = "時間軸";

            for (int i = 0; i < timeline.Count; i++)
            {
                int rowIdx = i + dataStartRowIdx;
                worksheet.Cells[rowIdx, 1].Value = timeline[i];
                
                // 💡 自動判斷：如果時間軸清單大於 24 筆或者是純日期，就顯示到天；否則印出小時
                worksheet.Cells[rowIdx, 1].Style.Numberformat.Format = (timeline.Count == 24) ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd";
            }

            // 💡 修正：只要需要合計列（無論日/月報），就塞入「合計」字樣
            if (showTotalRow)
            {
                int totalRowIdx = dataStartRowIdx + timeline.Count;
                worksheet.Cells[totalRowIdx, 1].Value = "合計";
            }
        }

        /// <summary>
        /// 💡 升級：樣式套用器（改用 showTotalRow 動態推算總列數，外框才不會畫短）
        /// </summary>
        public static void ApplyTableStyles(ExcelWorksheet worksheet, int timelineCount, bool showTotalRow, int titleRowIdx, int actualCols)
        {
            // 💡 修正：總列數必須加上實質的天數/小時數，再加上合計列（若有）
            int totalRows = titleRowIdx + timelineCount + (showTotalRow ? 1 : 0);

            // 1. 資料標題列灰底加粗
            using (var range = worksheet.Cells[titleRowIdx, 1, titleRowIdx, actualCols])
            {
                range.Style.Font.Bold = true;
                range.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
            }

            // 2. 整個表格範圍繪製外框線
            using (var range = worksheet.Cells[titleRowIdx, 1, totalRows, actualCols])
            {
                range.Style.Border.Top.Style = ExcelBorderStyle.Thin;
                range.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
                range.Style.Border.Left.Style = ExcelBorderStyle.Thin;
                range.Style.Border.Right.Style = ExcelBorderStyle.Thin;
            }
        }
    }
}