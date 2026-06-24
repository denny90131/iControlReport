using Microsoft.EntityFrameworkCore;
using IControlReporter.Data;
using IControlReporter.Services;
using IControlReporter.Test; // 👈 確保引入測試命名空間
using Serilog;
using Serilog.Formatting.Json;
using Quartz;


var builder = WebApplication.CreateBuilder(args);
// ✨ 註冊 DbContext 並讀取 appsettings.json 的連線字串
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(
        connectionString, 
        ServerVersion.AutoDetect(connectionString)));


string exeBinDir = AppDomain.CurrentDomain.BaseDirectory;
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        new Serilog.Formatting.Json.JsonFormatter(),
        Path.Combine(exeBinDir, "Logs", "report_apps.json"), 
        rollingInterval: RollingInterval.Day,
        shared: true,
        // 🎯 關鍵設定：強制每 1 秒鐘將緩衝區資料寫入硬碟
        flushToDiskInterval: TimeSpan.FromSeconds(1) 
    )
    .CreateLogger();

// 2. 掛載至 Host 上
builder.Host.UseSerilog();

// Add services to the container.
builder.Services.AddControllersWithViews();

builder.Services.AddSingleton<IReportService, ReportService>();

builder.Services.AddHostedService<ReportWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

// 💡 當你在開發環境下，如果啟動參數帶有 "run-test"，就直接執行剛才那段測試，執行完直接結束！
if (args.Length > 0 && args[0] == "run-test")
{
    await ReportCoreTests.RunTheLocalTestAsync();
    return; // 💡 澈底攔截！不讓後面的 app.Run() Web 主機啟動
}


app.Run();
