using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging; // 💡 確保引入 Logger 命名空間
using IControlReporter.Data;
using IControlReporter.Models.Report;

namespace IControlReporter.Controllers;

public class PointListController : Controller
{
    private readonly AppDbContext _context;
    private readonly ILogger<PointListController> _logger; // 💡 宣告私有 Logger 變數

    // 在建構子同時注入 DB 與 Logger 服務
    public PointListController(AppDbContext context, ILogger<PointListController> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// ⚙️ 進入 Tag Pool Management 點位池管理看板
    /// </summary>
    public IActionResult Setting()
    {
        _logger.LogInformation("🔍 [點位對照] 使用者請求載入 Tag Pool Management 設定頁面。");

        // 💡 1. 直接查詢 Points 表，完全不碰 AnalogEvents 歷史表
        var result = _context.Points
            .Select(p => new
            {
                PointId = p.IdPoint,
                PointName = $"{p.Tag}_{p.T0}_{p.T1}_{p.T2}_{p.T3}"
            })
            .AsNoTracking() // 唯讀選單必加，加速查詢
            .ToList();

        ViewBag.Points = result;
        _context.Database.CloseConnection(); // 確保連線釋放

        
        string exeBinDir = AppDomain.CurrentDomain.BaseDirectory;
        string filePath = Path.Combine(exeBinDir, "TagPools.json");
        ExecuteTagPool existingPool = new ExecuteTagPool { TagPool = new List<ExecuteTagPool.TagNode>() };
        
        if (System.IO.File.Exists(filePath))
        {
            try
            {
                string json = System.IO.File.ReadAllText(filePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    existingPool = JsonSerializer.Deserialize<ExecuteTagPool>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? existingPool;
                }
                
                // 🎯【維運日誌】：記錄成功讀取了多少條對照規則
                _logger.LogInformation($"✅ [點位對照] 成功載入現有對照設定檔，當前點位池內含：{existingPool.TagPool?.Count ?? 0} 筆映射關係。");
            }
            catch (Exception ex)
            {
                // 🎯【維運日誌】：記錄讀取檔案失敗的異常
                _logger.LogError(ex, $"🚨 [點位對照] 讀取實體 TagPools.json 失敗！原因: {ex.Message}");
                ViewBag.ErrorMessage = "無法讀取現有的點位池設定檔。";
            }
        }
        else
        {
            // 🎯【維運日誌】：初次佈署或設定檔不存在時的提示
            _logger.LogInformation("ℹ️ [點位對照] 偵測到尚未建立 TagPools.json 設定檔，系統將以空白清單初始化。");
        }

        ViewBag.TagPool = existingPool;
        return View();
    }

    /// <summary>
    /// 💾 儲存選取的 Tag 對照群組 API
    /// </summary>
    [HttpPost]
    public IActionResult SaveTagPool([FromBody] ExecuteTagPool payload)
    {
        // 1. 驗證進來的 Payload 有沒有綁定成功且包含資料
        if (payload == null || payload.TagPool == null)
        {
            _logger.LogWarning("⚠️ [點位對照] 拒絕儲存請求：接收到的 Payload 或 TagPool 為 null。");
            return BadRequest(new { success = false, message = "沒有接收到任何 Tag 資料" });
        }

        // 擷取目前嘗試儲存的所有商業邏輯名稱，用於日誌記錄
        var mappedNames = payload.TagPool.Select(t => t.Name).ToList();
        string nameListStr = mappedNames.Any() ? string.Join(", ", mappedNames) : "無(清空清單)";

        try
        {
            _logger.LogInformation($"💾 [點位對照] 開始儲存點位池設定。本次預計寫入筆數: {payload.TagPool.Count} 筆。目標欄位名稱為: [{nameListStr}]");

            string exeBinDir = AppDomain.CurrentDomain.BaseDirectory;
            string filePath = Path.Combine(exeBinDir, "TagPools.json");

            // 3. 序列化後直接覆寫檔案 
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // 保持中文原樣
            };
            
            string newJson = JsonSerializer.Serialize(payload, options);
            System.IO.File.WriteAllText(filePath, newJson);

            // 🎯【維運日誌】：儲存成功明細
            _logger.LogInformation($"✅ [點位對照] 點位池設定檔更新成功！實體路徑: {filePath}");
            return Json(new { success = true, message = "Tag 組合已成功儲存！" });
        }
        catch (Exception ex)
        {
            // 🎯【維運日誌】：發生寫入硬碟失敗的嚴重異常
            _logger.LogError(ex, $"🚨 [點位對照] 覆寫 TagPools.json 失敗！嘗試寫入的資料為: [{nameListStr}]。錯誤訊息: {ex.Message}");
            return Json(new { success = false, message = "儲存時發生錯誤：" + ex.Message });
        }
    }
}