using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using IControlReporter.Models.Report;
using IControlReporter.Services;
using IControlReporter.Unit.EEPLUS;
using IControlReporter.Data;
using IControlReporter.Models; 

namespace IControlReporter.Test
{
    public class ReportCoreTests
    {
        public static async Task RunTheLocalTestAsync()
        {
            // ====================================================================
            // 🎯 【關鍵修正】：時間必須切回 00:00:00
            // 這樣才能同時命中日報設定的 "00:00" 與月報表嚴格限制的 "Hour == 0 && Minute == 0"
            // ====================================================================
            DateTime fakeNow = DateTime.Parse("2025-10-01 00:00:00"); 
            
            Console.WriteLine($"=== [ReportWorker 核心邏輯在地測試 - 模擬目標日: {fakeNow:yyyy-MM-dd}] ===");

            var mockEnv = new FakeHostEnvironment();
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var workerLogger = loggerFactory.CreateLogger<ReportWorker>();
            
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase("EMS_Test_Db"));
            services.AddScoped<IReportService, ReportService>(); 
            services.AddSingleton<IHostEnvironment>(mockEnv);
            services.AddSingleton(loggerFactory.CreateLogger<ReportService>());

            var serviceProvider = services.BuildServiceProvider(); 
            var worker = new ReportWorker(serviceProvider, mockEnv, workerLogger);

            string myCustomPath = @"C:\日報表"; 

            // 呼叫更新後的防呆建立（內含日報與月報假排程）
            CreateTestFilesIfNotExist(mockEnv.ContentRootPath, myCustomPath);

            SeedFakeDatabaseData(serviceProvider, fakeNow);

            Console.WriteLine("\n▶️ 測試 [排程時間觸發比對]...");
            var loadMethod = typeof(ReportWorker).GetMethod(
                                "LoadSchedulesFromFile", 
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                             );
            loadMethod?.Invoke(worker, null);

            List<ReportSchedule> triggerList = worker.EvaluateSchedules(fakeNow);
            Console.WriteLine($"👉 成功觸發的排程數量: {triggerList.Count}");

            if (triggerList.Any())
            {
                Console.WriteLine("\n🚀 [核心加碼測試] 偵測到觸發任務，強行啟動 ProcessSingleReportAsync...");
                
                var processMethod = typeof(ReportWorker).GetMethod(
                                        "ProcessSingleReportAsync", 
                                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                                        null,
                                        new Type[] { typeof(ReportSchedule), typeof(DateTime?) },
                                        null
                                     );

                foreach (var schedule in triggerList)
                {
                    Console.WriteLine($"\n👉 正在生成: 【{schedule.ReportTitle}】({schedule.ReportType}) 的 Excel 檔案...");
                    await (Task)processMethod.Invoke(worker, new object[] { schedule, fakeNow });

                    // 💡 修正 1：不要死綁 myCustomPath！改為直接讀取該排程設定的 StoragePath，如果沒填才用執行目錄
                    string baseOutputPath = string.IsNullOrWhiteSpace(schedule.StoragePath) 
                        ? AppDomain.CurrentDomain.BaseDirectory 
                        : schedule.StoragePath;

                    bool isDaily = schedule.ReportType.Equals("daily", StringComparison.OrdinalIgnoreCase);
                    DateTime targetReportDate = fakeNow.AddDays(-1).Date; 
                    
                    // 💡 修正 2：精密組裝日報表（有月份夾）與月報表（無月份夾）的實體路徑
                    string expectedDir = isDaily
                        ? Path.Combine(baseOutputPath, schedule.ReportTitle, $"{targetReportDate:yyyy}年", $"{targetReportDate:MM}月")
                        : Path.Combine(baseOutputPath, schedule.ReportTitle, $"{targetReportDate:yyyy}年");

                    string filePrefix = isDaily ? "Daily_Report" : "Monthly_Report";
                    string dateMarker = isDaily ? $"{targetReportDate:yyyy-MM-dd}" : $"{targetReportDate:yyyy-MM}";
                    string expectedFullPath = Path.Combine(expectedDir, $"{filePrefix}_{schedule.ReportTitle}_{dateMarker}.xlsx");

                    Console.WriteLine($"🎯 [實體硬碟驗證成果 - {schedule.ReportTitle}]");
                    if (File.Exists(expectedFullPath))
                    {
                        var fileInfo = new FileInfo(expectedFullPath);
                        Console.WriteLine($"✅ 成功！檔案生成在: {expectedFullPath}");
                        Console.WriteLine($"✅ 檔案大小: {fileInfo.Length} bytes");
                    }
                    else
                    {
                        Console.WriteLine($"❌ 失敗！在目標路徑找不到生成的 Excel 檔案。");
                        Console.WriteLine($"🔍 請檢查此路徑是否存在: {expectedFullPath}");
                    }
                }
            }

            Console.WriteLine("\n=== 測試完畢 ===");
        }

        // 💡 升級：優化初始設定建立，同時塞入日報與月報排程
        private static void CreateTestFilesIfNotExist(string basePath, string customStoragePath)
        {
            string tagPoolPath = Path.Combine(basePath, "TagPools.json");
            string reportParamsPath = Path.Combine(basePath, "ReportParams.json");

            if (!File.Exists(tagPoolPath))
            {
                var dummyTagPool = "{\"TagPool\": [{\"Name\":\"MVCB\",\"Tag_ID\":101},{\"Name\":\"VCB1\",\"Tag_ID\":102}]}";
                File.WriteAllText(tagPoolPath, dummyTagPool);
            }

            // 🎯 如果排程檔不存在，建立內含「日報」與「月報（設定在每月 1 號觸發）」的完整陣列
            if (!File.Exists(reportParamsPath))
            {
                string escapedPath = customStoragePath.Replace(@"\", @"\\");
                var dummySchedules = $@"
                [
                    {{""ReportTitle"":""每日用電報表"",""ReportType"":""daily"",""ExecutionTime"":""00:00"",""IsEnabled"":true,""SelectedDataPoints"":[""MVCB""],""StoragePath"":""{escapedPath}""}},
                    {{""ReportTitle"":""每月用電報表"",""ReportType"":""monthly"",""ExecutionTime"":""1"",""IsEnabled"":true,""SelectedDataPoints"":[""MVCB""],""StoragePath"":""{escapedPath}""}}
                ]";
                File.WriteAllText(reportParamsPath, dummySchedules.Trim());
                Console.WriteLine($"💡 [測試準備] 已建立預設雙報表排程檔 (指定路徑: {customStoragePath})。");
            }
            else
            {
                Console.WriteLine("💡 [測試準備] 偵測到現有的 ReportParams.json，維持現有排程內容。");
            }
        }

        private static void SeedFakeDatabaseData(IServiceProvider provider, DateTime fakeNow)
        {
            using var scope = provider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            DateTime targetDataDate = fakeNow.AddDays(-1).Date; 
            float initialValue = 5000f;

            if (!context.AnalogEvents.Any(x => x.EventTime.Date == targetDataDate))
            {
                for (int h = 0; h <= 24; h++)
                {
                    context.AnalogEvents.Add(new analogevents
                    {
                        PointsIdPoint = 101, 
                        EventTime = targetDataDate.AddHours(h),
                        Value = initialValue + (h * 10.5f)
                    });
                }
                context.SaveChanges();
                Console.WriteLine($"💡 [測試準備] 已成功往記憶體資料庫種入 {targetDataDate:yyyy-MM-dd} 共 25 筆電表讀值。");
            }
        }

        private class FakeHostEnvironment : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = "Development";
            public string ApplicationName { get; set; } = "TestApp";
            public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory(); 
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
        }
    }
}