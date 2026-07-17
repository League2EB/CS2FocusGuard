<p align="center">
  <img src="src/CS2FocusGuard.App/Assets/AppIcon.png" width="120" alt="CS2 Focus Guard 圖示">
</p>

<h1 align="center">CS2 專注守衛</h1>

<p align="center">在 Counter-Strike 2 執行期間自動抑制 Windows 通知與未允許的應用程式聲音，避免干擾遊戲。</p>

<p align="center"><a href="README.en.md">English</a></p>

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows%2010%2022H2%2B-0078D4?style=for-the-badge&amp;logo=windows&amp;logoColor=white" alt="Platform: Windows 10 22H2 or later">
  <img src="https://img.shields.io/badge/.NET-8-512BD4?style=for-the-badge&amp;logo=dotnet&amp;logoColor=white" alt=".NET 8">
  <img src="https://img.shields.io/badge/License-MIT-80C342?style=for-the-badge" alt="License: MIT">
</p>

<p align="center">
  <a href="#-功能">功能</a> · <a href="#-製作原因">製作原因</a> · <a href="#-螢幕截圖">螢幕截圖</a> · <a href="#-運作方式">運作方式</a> · <a href="#-安裝">安裝</a> · <a href="#-從原始碼建置">從原始碼建置</a> · <a href="#-常見問題">常見問題</a>
</p>

---

> 此專案與 Valve 或 Counter-Strike 2 並無隸屬、合作或背書關係。

## 🚀 關於本專案

CS2 專注守衛會監控 `cs2.exe` 的執行狀態，並在遊戲期間自動隔離 Windows 通知與非白名單應用程式的音訊。遊戲結束、功能停用或應用程式關閉時，程式會安全地還原原先的通知與音訊狀態。

## 💭 製作原因

> 討厭室友一直在那邊「登登登 登登登」 - by 蛋堡

因為習慣 Telegram 常開在背景，但還是會發生靜步的時候突然超大聲通知。僅僅是因為這個原因所以製作了這個軟體。

## ✨ 功能

- 偵測 `cs2.exe` 執行狀態，每秒檢查一次。
- 啟用後，在遊戲開始時切換 Windows 通知模式：
  - Windows 11 使用「僅優先事項」。
  - Windows 10 使用「僅鬧鐘」。
- 提供音訊白名單：`cs2.exe` 永遠允許；Discord、Oopz 與 KOOK 預設允許。
- 使用者可從應用程式清單自行切換其他白名單程式；已允許的程式會顯示在列表前方。
- 遊戲期間，非白名單應用程式與 Windows 系統音效會自動靜音，包含稍後新建立的音訊工作階段。
- 在遊戲結束、停用功能或程式關閉時，還原原先的通知設定與由程式修改的音訊靜音狀態。
- 使用者若在遊戲期間手動變更通知模式，程式會保留該選擇，不會覆寫。
- 使用者若手動解除非白名單程式的靜音，該程式會在本次遊戲暫時允許發聲。
- 儲存通知與音訊還原狀態，以便程式異常結束後於下次啟動時嘗試復原設定。
- 支援系統匣、單一執行個體、關閉時最小化至系統匣與開機啟動。
- 提供繁體中文與英文介面。

## 📷 螢幕截圖

螢幕截圖即將提供。

## 🔧 運作方式

程式每秒檢查一次 `cs2.exe` 是否正在執行。

**遊戲啟動時：**

1. 儲存目前的 Windows 通知設定。
2. Windows 11 切換為「僅優先事項」，Windows 10 切換為「僅鬧鐘」。
3. 列舉所有作用中的輸出裝置與音訊工作階段，保留 CS2 與白名單程式的音訊，靜音其他應用程式和系統音效。
4. 監聽新建立的音訊工作階段與輸出裝置變更，持續套用白名單規則。
5. 將通知與音訊還原狀態寫入本機，以便程式異常結束後在下次啟動時嘗試復原。

**遊戲結束、功能停用或程式關閉時：**

1. 若通知設定仍由程式控制，還原遊戲開始前的設定。
2. 還原由程式靜音的音訊工作階段原始狀態。
3. 清除已儲存的還原狀態。

若使用者在遊戲期間手動變更通知模式，程式會保留該選擇，不會覆寫。若使用者手動解除非白名單程式的靜音，該程式會在本次 CS2 遊戲中暫時保持允許，直到遊戲結束。

## 📥 安裝

### 系統需求

- Windows 10 22H2（版本 19045）或更新版本
- 64 位元 Windows

### 安裝步驟

1. 下載並執行 [CS2FocusGuard-Setup-1.0.1-x64.exe](artifacts/CS2FocusGuard-Setup-1.0.1-x64.exe)。
2. 依照安裝精靈完成安裝，可選擇建立桌面捷徑與隨 Windows 啟動。
3. 啟動 CS2 專注守衛並啟用防護功能。
4. 開啟 Counter-Strike 2，程式會自動套用通知與音訊白名單防護。

程式設定、通知還原狀態與音訊還原狀態只會儲存於：

```text
%LocalAppData%\CS2FocusGuard
```

## 🛠 從原始碼建置

### 需求

- Windows 10 22H2（版本 19045）或更新版本
- 64 位元 Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

在專案根目錄執行：

```powershell
dotnet restore .\CS2FocusGuard.sln
dotnet build .\CS2FocusGuard.sln -c Release
```

執行應用程式：

```powershell
dotnet run --project .\src\CS2FocusGuard.App\CS2FocusGuard.App.csproj
```

執行測試：

```powershell
dotnet test .\CS2FocusGuard.sln -c Release
```

## ❓ 常見問題

### 這個程式會修改 CS2 的檔案或記憶體嗎？

不會。程式只會監控 `cs2.exe` 是否執行，並控制 Windows 的通知與音訊工作階段，不會修改遊戲檔案、存取遊戲記憶體或注入程式碼。

### 我在遊戲期間手動變更通知模式會怎樣？

程式會保留你的手動選擇，且在遊戲結束後不會覆寫它。

### 音訊白名單如何運作？

CS2 永遠保持允許。Discord、Oopz 與 KOOK 預設在白名單中，你可在應用程式列表中切換其他程式。未在白名單的應用程式與 Windows 系統音效會在遊戲期間靜音。

### 白名單如何辨識應用程式？

程式會從 Windows 音訊工作階段取得 PID，再以對應的可執行檔名稱比對，例如 `Discord.exe` 會識別為 `discord`。改變捷徑或顯示名稱不會影響辨識；若重新命名 `.exe`，需重新將該名稱加入白名單。

### 程式異常結束後，通知設定會還原嗎？

程式會在套用通知抑制與音訊靜音前儲存還原狀態，並在下次啟動時嘗試還原原先設定與音訊狀態。

### 程式設定儲存在哪裡？

所有設定、通知還原狀態與音訊還原狀態都儲存於 `%LocalAppData%\CS2FocusGuard`。

### 支援哪些 Windows 版本？

支援 64 位元 Windows 10 22H2（版本 19045）或更新版本。

## 📄 第三方聲明

本專案使用的互通性定義與安裝程式翻譯來源，請見 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。

## 📜 授權

本專案採用 [MIT License](LICENSE) 授權。你可以自由使用、修改、散布與商業使用本專案，但須保留授權與著作權聲明。
