using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IControlReporter.Models;
using IControlReporter.Data;
using IControlReporter.Models.Report;
using System.IO;
using System.Text.Json;

namespace IControlReporter.Controllers;

public class PointListController : Controller
{
    private readonly AppDbContext _context;

    public PointListController(AppDbContext context)
    {
        _context = context;
    }
    public IActionResult Setting()
    {
        // 💡 1. 直接查詢 Points 表，完全不碰 AnalogEvents 歷史表
        var result = _context.Points
            .Select(p => new
            {
                PointId = p.IdPoint,
                // 直接在記憶體/資料庫端組合成你要的名稱格式
                PointName = $"{p.Tag}_{p.T0}_{p.T1}_{p.T2}_{p.T3}"
            })
            .AsNoTracking() // 唯讀選單必加，加速查詢
            .ToList(); // 這樣在 MySQL 只會生成：SELECT idPoint, tag, T0, T1, T2, T3 FROM points;

        ViewBag.Points = result;

        // --- 2. 讀取已儲存的 JSON 檔案並傳遞給 View ---
        string filePath = Path.Combine(Directory.GetCurrentDirectory(), "TagPools.json");
        ExecuteTagPool existingPool = new ExecuteTagPool { TagPool = new List<ExecuteTagPool.TagNode>() };
        if (System.IO.File.Exists(filePath))
        {
            string json = System.IO.File.ReadAllText(filePath);
            if (!string.IsNullOrWhiteSpace(json))
            {
                existingPool = JsonSerializer.Deserialize<ExecuteTagPool>(json) ?? existingPool;
            }
        }
        ViewBag.TagPool = existingPool;

        return View();
    }

    // --- 儲存選取的 Tag 群組 API ---
    [HttpPost]
    public IActionResult SaveTagPool([FromBody] ExecuteTagPool payload)
    {
        // 1. 驗證進來的 Payload 有沒有綁定成功且包含資料
        if (payload == null || payload.TagPool == null)
        {
            return BadRequest(new { success = false, message = "沒有接收到任何 Tag 資料" });
        }

        try
        {
            // 2. 定義 JSON 檔案儲存路徑 (存在專案根目錄)
            string filePath = Path.Combine(Directory.GetCurrentDirectory(), "TagPools.json");

            // 3. 序列化後直接覆寫檔案 
            // 💡 修正：加入 UnsafeRelaxedJsonEscaping，防止中文在 JSON 中變成 \uXXXX 字元
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // 👈 關鍵：保持中文原樣
            };
            
            string newJson = JsonSerializer.Serialize(payload, options);
            System.IO.File.WriteAllText(filePath, newJson);

            return Json(new { success = true, message = "Tag 組合已成功儲存！" });
        }
        catch (Exception ex)
        {
            // 發生例外錯誤時回傳錯誤訊息
            return Json(new { success = false, message = "儲存時發生錯誤：" + ex.Message });
        }
    }
}
