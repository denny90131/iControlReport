# IControlReporter 專案技術說明文件

本專案是一個基於 **ASP.NET Core** 的 Web 應用程式，主要用於工業控制系統的數據報表生成與可視化。

---

## 🔑 核心功能

### 1. 歷史曲線查看器 (`ChartController`)

- 提供網頁介面，讓使用者勾選多個測點（Tag）。
- 在指定時間區間內，從資料庫撈取歷史數據（`AnalogEvents` 表）。
- 使用 **Chart.js** 繪製可互動的趨勢曲線圖。
- 支援將查詢結果下載為 **CSV** 檔案。

### 2. 點位池管理 (`PointListController`)

- 提供設定頁面，建立「邏輯名稱」與資料庫「測點 ID」的對應關係。
- 對應關係稱為 **Tag Pool**，以 `TagPools.json` 形式儲存於伺服器。
- 報表生成時，依此對照表決定要撈取哪些測點的數據。

### 3. 報表中心 (`ReportController`)

**手動報表生成：**
- 選擇報表類型（日報／月報）、指定日期／月份，並勾選測點群組。
- 系統即時運算並產生 Excel 報表，供使用者立即下載。

**自動排程管理：**
- 提供 UI 介面新增、修改、刪除報表排程。
- 排程參數（報表類型、執行時間、儲存路徑等）儲存於 `ReportParams.json`。

### 4. 背景排程服務 (`ReportWorker`)

- 常駐背景服務（`BackgroundService`），每分鐘輪詢 `ReportParams.json`。
- 根據設定時間觸發日報表或月報表的自動生成。
- 生成的 Excel 檔案依設定路徑自動歸檔，規則範例：

  ```
  根目錄 / 報表名稱 / 年份 / 月份
  ```

### 5. 系統日誌檢視器 (`EventController`)

- 網頁介面讀取並顯示 Serilog 產生的本地 JSON 日誌檔。
- 支援依時／分／秒精確過濾，方便問題追蹤與維運。

---

## 🛠 技術棧 (Tech Stack)

| 類別 | 技術 |
|------|------|
| 後端框架 | ASP.NET Core MVC |
| 資料庫 | MySQL（透過 Entity Framework Core 操作） |
| 日誌 | Serilog（每日滾動生成 JSON 檔案） |
| Excel 處理 | EPPlus |
| 背景任務 | `IHostedService` / `BackgroundService` |

**設定檔：**

| 檔案 | 用途 |
|------|------|
| `appsettings.json` | 資料庫連線字串等應用程式設定 |
| `TagPools.json` | 自訂測點對照表 |
| `ReportParams.json` | 自訂報表排程參數 |

---

## 📁 專案架構

```plaintext
IControlReporter/
│
├── Controllers/
│   ├── ChartController.cs         # 歷史曲線圖表資料 API
│   ├── EventController.cs         # 系統日誌檢視器 API
│   ├── HomeController.cs          # 首頁與錯誤頁
│   ├── PointListController.cs     # 測點池 (Tag Pool) 管理 API
│   ├── ReportController.cs        # 報表中心（手動／排程設定）API
│   └── SettingController.cs       # (未使用或佔位)
│
├── Data/
│   └── AppDbContext.cs            # EF Core 資料庫上下文
│
├── Models/
│   ├── Report/
│   │   ├── ExecuteTagPool.cs      # TagPools.json 的 DTO
│   │   ├── ReportParameterModel.cs # ReportParams.json（前端傳入）的 DTO
│   │   ├── ReportSchedule.cs      # ReportParams.json（背景服務使用）的 DTO
│   │   └── ReportResultDto.cs     # 報表計算結果的 DTO
│   └── ErrorViewModel.cs
│
├── Services/
│   ├── IReportService.cs          # 報表服務介面
│   ├── ReportService.cs           # 報表核心運算邏輯（撈取資料、處理翻轉）
│   └── ReportWorker.cs            # 背景排程服務
│
├── Unit/
│   └── EEPLUS/
│       └── ExcelEPPlusExtensions.cs  # EPPlus 輔助擴充方法
│
├── Views/                         # Razor 視圖
├── wwwroot/                       # 靜態檔案（js, css, images）
├── Logs/                          # Serilog 日誌（執行後自動建立）
├── GeneratedReports/              # 報表預設產出路徑（執行後自動建立）
├── TagPools.json                  # 測點對照池設定檔
├── ReportParams.json              # 報表排程設定檔
├── Program.cs                     # 應用程式進入點、服務註冊
└── appsettings.json               # 應用程式設定
```

---

## ⚙️ 核心運算邏輯

### 報表計算 (`ReportService`)

報表計算核心位於 `ReportService`，特別針對工業電表常見的「**歸零翻轉**」問題設計補償演算法。

#### 日報表 (`CalculateDailyReport`)

- 以「**小時**」為單位進行 `GroupBy`。
- 比較「本小時第一筆資料」與「下一小時第一筆資料」的差值。
- 若 **下小時讀值 < 本小時讀值**，判定為發生歸零翻轉。
- 翻轉補償公式：

  ```
  用量 = (電表最大值 - 本小時讀值) + 下小時讀值
  ```

- 若某時段無數據，用量記為 `0`，確保數據連續性。

#### 月報表 (`CalculateMonthlyReport`)

- 邏輯與日報表相同，但時間粒度變為「**天**」。
- 比較「當天第一筆資料」與「隔天第一筆資料」的差值。
- 同樣套用翻轉補償演算法計算每日總用量。

---

### 背景排程 (`ReportWorker`)

```
啟動
  └── 無限迴圈（每分鐘執行一次）
        ├── 重新讀取 ReportParams.json 最新設定
        ├── 遍歷所有 IsEnabled = true 的排程
        ├── 判斷當前時間是否符合執行條件
        │     ├── 日報：比對 HH:mm
        │     └── 月報：比對第幾天
        ├── 檢查 LastExecuted，防止同天／同分鐘重複觸發
        └── 觸發 → 呼叫 ReportService 運算 → EPPlus 產生 Excel → 存檔至指定目錄
```

---

*IControlReporter — Industrial Energy Data Reporting Platform*
