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
        return View();
    }

}
