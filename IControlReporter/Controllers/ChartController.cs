using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using IControlReporter.Models;
using IControlReporter.Data;

namespace IControlReporter.Controllers;

public class ChartController : Controller
{
    private readonly AppDbContext _context;

    public ChartController(AppDbContext context)
    {
        _context = context;
    }
    public IActionResult Viewer()
    {
        // 直接去 Points 表撈取所有測點的 ID 與 Tag 名稱，完全不 Join 歷史表
        var result = _context.Points
            .Select(p => new
            {
                PointId = p.IdPoint,
                PointName = p.Tag
            })
            .ToList(); // 這樣會直接生成 SELECT idPoint, tag FROM points; 速度極快

        ViewBag.Points = result;
        return View();
    }

     // --- 處理前端發送的 Ajax POST 請求(加載曲線) ---
    [HttpPost]
    public IActionResult GetChartData([FromBody] ChartQueryModel query)
    {
        if (query == null || query.TagIds == null || query.TagIds.Count == 0)
        {
            return BadRequest("Invalid request data.");
        }

        // 2. 將前端傳來的字串手動解析為 DateTime 型態（安全防炸）
        if (!DateTime.TryParse(query.StartTime, out DateTime start) ||
            !DateTime.TryParse(query.EndTime, out DateTime end))
        {
            return BadRequest("時間格式不正確");
        }

        //💡 3. 如果你的資料庫中的 Id 是數字型態 (例如 int)，先將前端傳來的 string 陣列轉成 int 陣列
        // (如果你的 Id 原本就是 string/Guid，請略過這行，並在下方把 .Contains(id) 改成 Contains(ae.PointsIdPoint))
        var tagIdsAsInt = query.TagIds.Select(id => int.Parse(id)).ToList();

        // 💡 4. 真實資料庫查詢與多表 Join
        var dbData = _context.AnalogEvents
            // 過濾時間區間與多個 Tag ID (效能核心：先過濾再 Join)
            .Where(ae => tagIdsAsInt.Contains(ae.PointsIdPoint)
                      && ae.EventTime >= start
                      && ae.EventTime <= end)
            // Join Points 資料表，目的是為了拿到 Tag 的「名稱」(例如：FIC-101)
            .Join(_context.Points,
                ae => ae.PointsIdPoint,
                p => p.IdPoint,
                (ae, p) => new
                {
                    PointId = p.IdPoint,
                    TagName = p.Tag,
                    // 假設你的時間欄位叫 Timestamp 或 EventTime，數值欄位叫 Value
                    Timestamp = ae.EventTime,
                    Value = ae.Value
                })
            // // 依照時間排序，確保圖表由左到右呈現是正確的
            // .OrderBy(x => x.Timestamp)
            .ToList();

        // 💡 5. 將資料重組為 Chart.js 最好讀的格式
        // 抽出所有不重複的時間點作為圖表的 X 軸 Labels (格式化為方便閱讀的字串)
        var labels = dbData.Select(x => x.Timestamp.ToString("yyyy-MM-dd HH:mm")).Distinct().ToList();

        // 依據 TagName 將數據分組，做出多條線的 Dataset
        var datasets = dbData
            .GroupBy(x => x.TagName)
            .Select(g => new
            {
                label = g.Key + " Values", // 線條名稱
                // 這裡對齊全域的 labels，如果該時間點沒資料就給 null，防圖表斷線或錯位
                data = labels.Select(l => g.FirstOrDefault(d => d.Timestamp.ToString("yyyy-MM-dd HH:mm") == l)?.Value).ToList()
            })
            .ToList();

        // 這裡模擬回傳假資料 (屆時需替換成您真實的查詢結果)
        var result = new
        {
            success = true,
            message = "取得成功",
            labels = labels,       // X 軸時間陣列
            datasets = datasets    // 多條線的數據陣列
        };

        return Json(result);
    }

    // --- 處理前端發送的 Ajax POST 請求(下載資料) ---
    [HttpPost]
    public IActionResult DownloadChartData([FromBody] ChartQueryModel query)
    {
        if (query == null || query.TagIds == null || query.TagIds.Count == 0)
        {
            return BadRequest("Invalid request data.");
        }

        // 2. 將前端傳來的字串手動解析為 DateTime 型態（安全防炸）
        if (!DateTime.TryParse(query.StartTime, out DateTime start) ||
            !DateTime.TryParse(query.EndTime, out DateTime end))
        {
            return BadRequest("時間格式不正確");
        }

        //💡 3. 如果你的資料庫中的 Id 是數字型態 (例如 int)，先將前端傳來的 string 陣列轉成 int 陣列
        // (如果你的 Id 原本就是 string/Guid，請略過這行，並在下方把 .Contains(id) 改成 Contains(ae.PointsIdPoint))
        var tagIdsAsInt = query.TagIds.Select(id => int.Parse(id)).ToList();

        // 💡 4. 真實資料庫查詢與多表 Join
        var dbData = _context.AnalogEvents
            // 過濾時間區間與多個 Tag ID (效能核心：先過濾再 Join)
            .Where(ae => tagIdsAsInt.Contains(ae.PointsIdPoint)
                      && ae.EventTime >= start
                      && ae.EventTime <= end)
            // Join Points 資料表，目的是為了拿到 Tag 的「名稱」(例如：FIC-101)
            .Join(_context.Points,
                ae => ae.PointsIdPoint,
                p => p.IdPoint,
                (ae, p) => new
                {
                    PointId = p.IdPoint,
                    TagName = p.Tag,
                    // 假設你的時間欄位叫 Timestamp 或 EventTime，數值欄位叫 Value
                    Timestamp = ae.EventTime,
                    Value = ae.Value
                })
            .ToList();


        var csvBuilder = new System.Text.StringBuilder();
        
        // CSV 標題列
        csvBuilder.AppendLine("Timestamp,TagName,Value");

        // CSV 資料內容
        foreach (var item in dbData)
        {
            // 確保資料包含逗號時不會破壞結構 (簡單處理)
            var line = $"{item.Timestamp:yyyy-MM-dd HH:mm:ss},{item.TagName},{item.Value}";
            csvBuilder.AppendLine(line);
        }

        // 2. 將 StringBuilder 轉為 Byte Array
        var bytes = System.Text.Encoding.UTF8.GetBytes(csvBuilder.ToString());
        
        // 3. 回傳檔案給瀏覽器
        // "text/csv" 是 MIME type，瀏覽器看到會自動觸發下載
        var fileName = $"DataExport_{DateTime.Now:yyyyMMddHHmmss}.csv";
        
        // 加入 BOM (Byte Order Mark) 讓 Excel 開啟中文時不會亂碼
        var fileContent = System.Text.Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();

        return File(fileContent, "text/csv", fileName);
    }


    // --- 用來承接前端 JSON 的 DTO 模型 ---
    public class ChartQueryModel
    {
        public List<string> TagIds { get; set; }
        public string StartTime { get; set; }
        public string EndTime { get; set; }
    }
}
