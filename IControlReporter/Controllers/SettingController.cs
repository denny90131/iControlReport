using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using IControlReporter.Models;
using IControlReporter.Data;
using IControlReporter.Models.Report;
using System.Text.Encodings.Web; // 💡 新增：處理不轉義中文必備的命名空間
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System;

namespace IControlReporter.Controllers;

public class SettingController : Controller
{
    private readonly AppDbContext _context;

    public SettingController(AppDbContext context)
    {
        _context = context;
    }
    public IActionResult Setting()
    {
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
        
        // --- 讀取報表參數設定並傳給 View ---
        string paramsPath = Path.Combine(Directory.GetCurrentDirectory(), "ReportParams.json");
        List<ReportParameterModel> reportParams = new List<ReportParameterModel>();
        if (System.IO.File.Exists(paramsPath))
        {
            string paramsJson = System.IO.File.ReadAllText(paramsPath);
            if (!string.IsNullOrWhiteSpace(paramsJson))
            {
                reportParams = JsonSerializer.Deserialize<List<ReportParameterModel>>(paramsJson) ?? reportParams;
            }
        }
        ViewBag.ReportParams = reportParams;

        return View();
    }

    [HttpPost]
    public IActionResult SaveReportParam([FromBody] ReportParameterModel payload)
    {
        if (payload == null || string.IsNullOrWhiteSpace(payload.ReportTitle) || payload.SelectedDataPoints == null || !payload.SelectedDataPoints.Any())
        {
            return BadRequest(new { success = false, message = "請填寫完整的報表標題與選擇點位" });
        }
        
        try
        {
            string filePath = Path.Combine(Directory.GetCurrentDirectory(), "ReportParams.json");
            List<ReportParameterModel> allParams = new List<ReportParameterModel>();
            if (System.IO.File.Exists(filePath))
            {
                string json = System.IO.File.ReadAllText(filePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    allParams = JsonSerializer.Deserialize<List<ReportParameterModel>>(json) ?? allParams;
                }
            }
            
            // 依據標題做為 Key，若標題相同則覆寫原本的設定，不同則新增
            var existing = allParams.FirstOrDefault(p => p.ReportTitle == payload.ReportTitle);
            if (existing != null)
            {
                existing.ReportType = payload.ReportType;
                existing.SelectedDataPoints = payload.SelectedDataPoints;
                existing.IsEnabled = payload.IsEnabled;
                existing.ExecutionTime = payload.ExecutionTime;
                existing.StoragePath = payload.StoragePath;
            }
            else
            {
                allParams.Add(payload);
            }
            
            // 💡 關鍵優化：增加 Encoder 設定，讓儲存的中文報表抬頭保持中文字，不轉義成 Unicode 碼
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
            };
            System.IO.File.WriteAllText(filePath, JsonSerializer.Serialize(allParams, options));
            
            return Json(new { success = true, message = "報表參數儲存成功！" });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = "儲存時發生錯誤：" + ex.Message });
        }
    }

    [HttpPost]
    public IActionResult DeleteReportParam([FromBody] DeleteRequestModel payload)
    {
        if (payload == null || string.IsNullOrWhiteSpace(payload.ReportTitle))
        {
            return BadRequest(new { success = false, message = "請提供要刪除的標題" });
        }

        try
        {
            string filePath = Path.Combine(Directory.GetCurrentDirectory(), "ReportParams.json");
            List<ReportParameterModel> allParams = new List<ReportParameterModel>();
            if (System.IO.File.Exists(filePath))
            {
                string json = System.IO.File.ReadAllText(filePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    allParams = JsonSerializer.Deserialize<List<ReportParameterModel>>(json) ?? allParams;
                }
            }

            var item = allParams.FirstOrDefault(p => p.ReportTitle == payload.ReportTitle);
            if (item != null)
            {
                allParams.Remove(item);
                
                // 💡 關鍵優化：刪除後重新寫入檔案時，同樣保持中文原樣
                var options = new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
                };
                System.IO.File.WriteAllText(filePath, JsonSerializer.Serialize(allParams, options));
                return Json(new { success = true, message = "刪除成功！" });
            }

            return Json(new { success = false, message = "找不到指定的設定" });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = "刪除時發生錯誤：" + ex.Message });
        }
    }

    public class DeleteRequestModel
    {
        public string ReportTitle { get; set; }
    }
}
