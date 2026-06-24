using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using IControlReporter.Data;
using System.Text.Json.Serialization; // 💡 確保引入此命名空間

namespace IControlReporter.Controllers;

public class EventController : Controller
{
    private readonly ILogger<EventController> _logger; // 💡 宣告私有 Logger 變數

    // 在建構子同時注入 DB 與 Logger 服務
    public EventController(ILogger<EventController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 📝 讀取本地端 Serilog 每日 JSON 檔案日誌 (支援自動換檔 + 內含精準時分秒過濾)
    /// </summary>
    public IActionResult LogHistory(DateTime? filterStart, DateTime? filterEnd) 
    {
        // 🎯 強制禁止瀏覽器快取此頁面
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        var logList = new List<LocalLogModel>();
        
        // 1. 決定要開哪一天的檔案（有選時間就抓該時間的日期，沒選預設抓今天）
        DateTime targetDate = filterStart.HasValue ? filterStart.Value.Date : DateTime.Today;
        string targetDateStr = targetDate.ToString("yyyyMMdd");
        
        string exeBinDir = AppDomain.CurrentDomain.BaseDirectory;
        string logFilePath = Path.Combine(exeBinDir, "Logs", $"report_apps{targetDateStr}.json");

        // 將篩選時間原封不動傳回前端，用來初始化輸入框的預設值
        ViewBag.FilterStart = filterStart?.ToString("yyyy-MM-ddTHH:mm");
        ViewBag.FilterEnd = filterEnd?.ToString("yyyy-MM-ddTHH:mm");

        try
        {
            if (System.IO.File.Exists(logFilePath))
            {
                // 🎯 強制開啟檔案並忽略緩存
                using var stream = new FileStream(
                    logFilePath, 
                    FileMode.Open, 
                    FileAccess.Read, 
                    FileShare.ReadWrite, // 必須與 shared: true 配對
                    bufferSize: 4096, 
                    FileOptions.SequentialScan // 告訴 OS 這是日誌，順序讀取即可，不要預載入過多快取
                );
                using var reader = new StreamReader(stream);
                
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    
                    // 系統框架過濾器
                    if (line.Contains("Microsoft.AspNetCore") || 
                        line.Contains("Executing endpoint") || 
                        line.Contains("Request starting") || 
                        line.Contains("Request finished") ||
                        line.Contains("Sending file"))
                    {
                        continue; 
                    }
                    
                    var logEntry = JsonSerializer.Deserialize<LocalLogModel>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (logEntry != null)
                    {
                        logList.Add(logEntry);
                    }
                }
                
                // 最新時間置頂
                logList.Reverse(); 
            }
            else
            {
                ViewBag.ErrorMessage = $"找不到該日期的日誌實體檔案。預期路徑為: {logFilePath}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "網頁看盤讀取本地日誌檔案失敗。");
            ViewBag.ErrorMessage = $"讀取日誌衝突: {ex.Message}";
        }

        // 🎯【核心修正】：在這裡強行實施時分秒 LINQ 過濾！
        var filteredResult = logList.AsQueryable();

        if (filterStart.HasValue)
        {
            // 日誌時間必須大於或等於前端選的開始時間
            filteredResult = filteredResult.Where(log => log.Timestamp >= filterStart.Value);
        }

        if (filterEnd.HasValue)
        {
            // 日誌時間必須小於或等於前端選的結束時間
            filteredResult = filteredResult.Where(log => log.Timestamp <= filterEnd.Value);
        }

        // 🎯 預設最多塞 1000 筆回前端，兼顧效能與記憶體
        return View(filteredResult.Take(1000).ToList());
    }
    
    
    // ====================================================================
    // 📦 DTO Models
    // ====================================================================
    
    public class LocalLogModel
    {
        // 🎯 移除 [JsonPropertyName("@t")]，讓 C# 透過 PropertyNameCaseInsensitive 直接識別 "Timestamp"
        public DateTime Timestamp { get; set; }

        // 🎯 移除 [JsonPropertyName("@l")]，直接對齊 "Level"
        public string Level { get; set; } = "Information";

        // 🎯 移除 [JsonPropertyName("@mt")]，直接對齊 "MessageTemplate"
        public string MessageTemplate { get; set; } = string.Empty;
    }
}