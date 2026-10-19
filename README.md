# MultiTool 多功能工具

A lightweight Windows automation tool (.NET 9 WinForms, single-file `.exe`). Build your own macros with keyboard and mouse actions, on-screen image matching, loops, conditions and background execution.

一款輕量的 Windows 自動化工具（.NET 9 WinForms，單一 `.exe` 檔案）。可自行編寫巨集腳本，支援鍵盤與滑鼠操作、螢幕圖像比對、循環、條件判斷及背景執行。

Developed by / 開發者：**白兔糖v3v**
Interface language / 介面語言：Traditional Chinese / 繁體中文

---

## Features 功能

**My Scripts 我的腳本** — build automation scripts step by step / 逐步編寫自動化腳本

| Step 步驟 | Description 說明 |
|---|---|
| Key press 按鍵 | Any key or key combination (e.g. `Shift + Alt + T`); hold for N seconds, press down only, or release only.<br>任何按鍵或組合鍵（例如 `Shift + Alt + T`）；可設定按住 N 秒、僅按下或僅放開。 |
| Mouse click 滑鼠點擊 | Left or right button at a screen position, with a 3-second position picker.<br>於指定螢幕座標按下左鍵或右鍵，並提供 3 秒倒數的座標拾取功能。 |
| Wait 等待 | Pause for N seconds.<br>暫停 N 秒。 |
| Find image & click 搵圖點擊 | Snip screen regions (up to 6 images per step) and click the best match above the similarity threshold.<br>擷取螢幕區域作為目標圖片（每個步驟最多 6 張），並點擊相似度最高且達到門檻的位置。 |
| Loop / Break 循環／中斷循環 | Repeat steps N times (0 = forever) and break out early when needed.<br>重複執行步驟 N 次（0 代表無限次），並可提前跳出循環。 |
| IF condition IF 條件 | Image found, image not found, pixel color, clock time or countdown; conditions can be nested.<br>條件包括：找到圖片、找不到圖片、指定位置的顏色、現實時間及倒數計時；可多層巢狀使用。 |

- **Parallel IF blocks** run alongside the main thread and keep watching their conditions.
  **並行 IF 區塊**與主線程同時運行，持續監察條件。
- Run **once** or **continuously**, with a per-script **hotkey** to start and stop.
  可選擇**執行一次**或**持續執行**，每個腳本可設定獨立**快捷鍵**開始及停止。
- **Background mode** captures, clicks and types only inside a selected program window, so you can switch to other windows while the script runs. Some games do not accept background input.
  **背景執行模式**只於指定程式視窗內擷取畫面、點擊及輸入按鍵，運行期間可切換至其他視窗。部分遊戲不接受背景輸入。
- Scripts are validated before running (missing images, unset keys, empty loops, etc.).
  執行前會自動檢查腳本（例如缺少圖片、未設定按鍵、空白循環等）。
- **Auto update** checks GitHub Releases on startup and updates with one click.
  **自動更新**：啟動時檢查 GitHub Releases，一鍵完成更新。

---

## Download 下載

Download the latest `MultiTool.exe` (or zip) from **Releases**, extract it and run. No .NET installation is required.
從 **Releases** 下載最新的 `MultiTool.exe`（或壓縮檔），解壓後直接執行，無須安裝 .NET。

Scripts, screenshots and settings are stored in the `data/` folder next to the executable.
腳本、截圖及設定均儲存於執行檔旁的 `data/` 資料夾。

> If the target program runs as Administrator, run MultiTool as Administrator as well; otherwise Windows will block the simulated input.
> 若目標程式以系統管理員身分執行，MultiTool 亦須以系統管理員身分執行，否則 Windows 會阻擋模擬輸入。

---

## Repository layout 專案結構

| Path 路徑 | Purpose 用途 |
|---|---|
| `src/` | Source code 原始碼：`App.cs` main window, home page, title bar 主視窗、首頁及標題列；`UI.cs` dark theme and controls 深色主題及控件；`Common.cs` input, screen capture, image matching, hotkeys 輸入、截圖、圖像比對及快捷鍵；`Scripts.cs` script model, editor and runner 腳本資料、編輯器及執行器；`Updater.cs` auto update 自動更新 |
| `tools/` | Packaging tools 打包工具（`make_exe.py`, `bundle.py`） |
| `template/` | Example script shipped to users 隨附的範例腳本（自動點擊） |
| `release/` | User guide shipped with releases 隨發佈版本附上的使用說明（`使用說明.txt`） |
| `Build/` | Local build output, ignored by git 本機打包輸出（不納入 git） |

---

## Building 編譯

No Windows Desktop SDK is required. Compile with Roslyn `csc` against the framework assemblies extracted from an existing .NET 9 WinForms single-file executable, then repack:
無須安裝 Windows Desktop SDK。使用 Roslyn `csc`，引用從現有 .NET 9 WinForms 單一執行檔中解出的框架組件進行編譯，然後重新打包：

```
python tools/bundle.py Build/MultiTool.exe ref/
csc -nostdlib -noconfig -target:winexe -langversion:12 -nullable:enable -optimize+ -out:MultiTool.dll -r:ref/*.dll src/*.cs
python tools/make_exe.py Build/MultiTool.exe MultiTool.dll Build/MultiTool.exe.new
```

`make_exe.py` uses the previous `Build/MultiTool.exe` as its base (app host and runtime), so keep that file.
`make_exe.py` 以上一版 `Build/MultiTool.exe` 作為基礎（應用程式主機及執行環境），因此請保留該檔案。

---

## Releasing a new version 發佈新版本

1. Update `Version` in `src/App.cs` (e.g. `v1.1`).
   更新 `src/App.cs` 中的 `Version`（例如 `v1.1`）。
2. Build `Build/MultiTool.exe`.
   打包產生 `Build/MultiTool.exe`。
3. Create a GitHub Release whose **tag matches the version** (`v1.1`), attach `MultiTool.exe` or a zip containing it, and mark it as **latest**.
   建立 GitHub Release，**標籤須與版本號相同**（`v1.1`），附上 `MultiTool.exe` 或包含該檔案的壓縮檔，並設為 **latest**。
4. Existing users will be prompted to update the next time they launch the app.
   現有用戶下次啟動程式時便會收到更新提示。

---

## License 授權

© 白兔糖v3v. Licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE).
© 白兔糖v3v。本軟件依據 [PolyForm Noncommercial License 1.0.0](LICENSE) 授權。

Free to use, modify and share for non-commercial purposes. **Resale and any commercial use are prohibited.**
可免費作非商業用途使用、修改及分享。**嚴禁轉售及任何商業用途。**
