IControlReporter 專案說明文件
這是一份針對 IControlReporter 專案的技術概覽與架構說明文件。本專案是一個基於 ASP.NET Core 的 Web 應用程式，主要用於工業控制系統的數據報表生成與可視化。

核心功能
歷史曲線查看器 (ChartController):

提供一個網頁介面，讓使用者可以勾選多個測點 (Tag)。
在指定的時間區間內，從資料庫撈取歷史數據 (AnalogEvents 表)。
使用 Chart.js 將數據繪製成可互動的趨勢曲線圖。
支援將查詢結果下載為 CSV 檔案。
點位池管理 (PointListController):

提供一個設定頁面，讓使用者可以建立「邏輯名稱」與資料庫中「測點 ID」的對應關係。
這些對應關係被稱為 "Tag Pool"，並以 TagPools.json 的形式儲存在伺服器上。
報表生成時，會使用此對照表來決定要撈取哪些測點的數據。
報表中心 (ReportController):

手動報表生成:
使用者可以選擇報表類型（日報/月報）、指定日期/月份，並勾選要納入報表的測點群組。
系統會即時運算並產生對應的 Excel 報表檔案，供使用者立即下載。
自動排程管理:
提供 UI 介面來新增、修改、刪除報表排程。
排程參數（如報表類型、執行時間、儲存路徑等）儲存於 ReportParams.json。
背景排程服務 (ReportWorker):

一個常駐的背景服務 (BackgroundService)。
每分鐘輪詢一次 ReportParams.json 檔案。
根據設定的時間觸發日報表或月報表的自動生成。
生成的 Excel 檔案會依照設定的路徑與規則（例如 根目錄/報表名稱/年份/月份）自動歸檔。
系統日誌檢視器 (EventController):

提供一個網頁介面，用於讀取並顯示由 Serilog 產生的本地 JSON 格式日誌檔案。
支援依據精確的時/分/秒來過濾日誌內容，方便問題追蹤與系統維運。
技術棧
後端: ASP.NET Core MVC
資料庫: MySQL (透過 Entity Framework Core 進行操作)
日誌: Serilog (設定為每日滾動生成 JSON 檔案)
Excel 處理: EPPlus
背景任務: IHostedService / BackgroundService
設定檔:
appsettings.json: 資料庫連線字串等。
TagPools.json: 自訂的測點對照表。
ReportParams.json: 自訂的報表排程參數。
專案架構
plaintext
IControlReporter/
│
├── Controllers/
│   ├── ChartController.cs       # 歷史曲線圖表資料 API
│   ├── EventController.cs       # 系統日誌檢視器 API
│   ├── HomeController.cs        # 首頁與錯誤頁
│   ├── PointListController.cs   # 測點池 (Tag Pool) 管理 API
│   ├── ReportController.cs      # 報表中心 (手動/排程設定) API
│   └── SettingController.cs     # (未使用或佔位)
│
├── Data/
│   └── AppDbContext.cs          # EF Core 資料庫上下文
│
├── Models/
│   ├── Report/
│   │   ├── ExecuteTagPool.cs    # TagPools.json 的 DTO
│   │   ├── ReportParameterModel.cs # ReportParams.json (前端傳入) 的 DTO
│   │   ├── ReportSchedule.cs    # ReportParams.json (背景服務使用) 的 DTO
│   │   └── ReportResultDto.cs   # 報表計算結果的 DTO
│   └── ErrorViewModel.cs
│
├── Services/
│   ├── IReportService.cs        # 報表服務介面
│   ├── ReportService.cs         # 報表核心運算邏輯 (撈取資料、處理翻轉)
│   └── ReportWorker.cs          # 背景排程服務
│
├── Unit/
│   └── EEPLUS/
│       └── ExcelEPPlusExtensions.cs # EPPlus 輔助擴充方法
│
├── Views/                         # Razor 視圖
├── wwwroot/                       # 靜態檔案 (js, css, images)
├── Logs/                          # Serilog 產生的日誌 (執行後自動建立)
├── GeneratedReports/              # 背景服務預設的報表產出路徑 (執行後自動建立)
├── TagPools.json                  # 測點對照池設定檔 (手動建立或執行後儲存)
├── ReportParams.json              # 報表排程設定檔 (手動建立或執行後儲存)
├── Program.cs                     # 應用程式進入點、服務註冊
└── appsettings.json               # 應用程式設定
核心運算邏輯
報表計算 (ReportService)
本專案的報表計算核心在 ReportService 中，特別是 CalculateDailyReport 和 CalculateMonthlyReport 方法。其主要特點是為了解決工業電表常見的「歸零翻轉」問題。

日報表 (CalculateDailyReport):

以「小時」為單位進行 GroupBy。
核心思想是比較 「這個小時的第一筆資料」 與 「下一個小時的第一筆資料」 的差值。
如果 下小時讀值 < 本小時讀值，則判定為發生歸零翻轉。
翻轉補償公式為：(電表最大值 - 本小時讀值) + 下小時讀值。
如果某個小時或下個小時沒有數據，則該時段用量記為 0，確保數據的連續性與準確性。
月報表 (CalculateMonthlyReport):

邏輯與日報表類似，但時間粒度變為「天」。
比較 「當天的第一筆資料」 與 「隔天的第一筆資料」 的差值。
同樣套用翻轉補償演算法來計算每日的總用量。
背景排程 (ReportWorker)
ReportWorker 啟動後，會進入一個無限迴圈，每分鐘檢查一次。
在每次檢查中，它會重新讀取 ReportParams.json 的最新設定。
遍歷所有 IsEnabled 為 true 的排程。
判斷當前時間是否滿足排程設定的執行條件（例如，日報的 HH:mm 或月報的 第幾天）。
同時會檢查 LastExecuted 記憶體狀態，避免在同一天或同一分鐘內重複觸發。
觸發後，非同步呼叫 ReportService 進行運算，並使用 EPPlus 產生 Excel 檔案，最後存檔至指定目錄。
