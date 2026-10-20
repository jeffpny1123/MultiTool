# MultiTool 多功能工具

A lightweight Windows automation tool (.NET 9 WinForms, single-file `.exe`). Build your own macros with keyboard and mouse actions, on-screen image matching, loops, conditions and background execution, and chat with your own AI (Claude / ChatGPT / Gemini, or a free local AI) by text or voice to answer questions, open apps and websites, or write scripts for you.

一款輕量的 Windows 自動化工具（.NET 9 WinForms，單一 `.exe` 檔案）。可自行編寫巨集腳本，支援鍵盤與滑鼠操作、螢幕圖像比對、循環、條件判斷及背景執行；亦可透過文字或語音連接你自己的 AI（Claude／ChatGPT／Gemini，或免費的本地 AI），進行問答、開啟程式及網頁，或協助編寫腳本。

Developed by / 開發者：**白兔糖v3v**
Interface languages / 介面語言：Traditional Chinese, Simplified Chinese, English / 繁體中文、簡體中文、英文 — switch at the bottom-left; the system language is used on first launch / 可於左下角切換，首次開啟時跟隨系統語言

---

## Features 功能

**My Scripts 我的腳本** — build automation scripts step by step / 逐步編寫自動化腳本

| Step 步驟 | Description 說明 |
|---|---|
| Key press 按鍵 | Any key or key combination (e.g. `Shift + Alt + T`); hold for N milliseconds, press down only, or release only.<br>任何按鍵或組合鍵（例如 `Shift + Alt + T`）；可設定按住 N 毫秒、僅按下或僅放開。 |
| Mouse click 滑鼠點擊 | Left or right button at a screen position, with a 3-second position picker.<br>於指定螢幕座標按下左鍵或右鍵，並提供 3 秒倒數的座標拾取功能。 |
| Delay 延遲 | Pause for N milliseconds.<br>暫停 N 毫秒（ms）。 |
| Find image & click 搵圖點擊 | Snip screen regions (up to 6 images per step) and click the best match above the similarity threshold.<br>擷取螢幕區域作為目標圖片（每個步驟最多 6 張），並點擊相似度最高且達到門檻的位置。 |
| Loop / Break 循環／中斷循環 | Repeat steps N times (0 = forever) and break out early when needed.<br>重複執行步驟 N 次（0 代表無限次），並可提前跳出循環。 |
| IF condition IF 條件 | Image found, image not found, pixel color, clock time or countdown; conditions can be nested.<br>條件包括：找到圖片、找不到圖片、指定位置的顏色、現實時間及倒數計時；可多層巢狀使用。 |

- **Parallel IF blocks** run alongside the main thread and keep watching their conditions.
  **並行 IF 區塊**與主線程同時運行，持續監察條件。
- Run **once** or **continuously**, with a per-script **hotkey** to start and stop.
  可選擇**執行一次**或**持續執行**，每個腳本可設定獨立**快捷鍵**開始及停止。
- **Background mode** captures, clicks and types only inside a selected program window, so you can switch to other windows while the script runs. Some games do not accept background input.
  **背景執行模式**只於指定程式視窗內擷取畫面、點擊及輸入按鍵，運行期間可切換至其他視窗。部分遊戲不接受背景輸入。
- **Run speed** (0.5x – 3.0x) on the preview page scales delays, hold times, check intervals and countdowns; it can be changed while the script is running.
  預覽頁的**運行速度**（0.5x – 3.0x）會按倍率調整延遲、按住時間、檢查間隔及倒數，運行中亦可即時更改。
- Scripts are validated before running (missing images, unset keys, empty loops, etc.).
  執行前會自動檢查腳本（例如缺少圖片、未設定按鍵、空白循環等）。
- **Auto update** checks GitHub Releases on startup and updates with one click.
  **自動更新**：啟動時檢查 GitHub Releases，一鍵完成更新。

**AI Assistant AI 助手** — uses the AI command-line tool already installed and signed in on your computer / 使用你電腦上已安裝並已登入的 AI 命令列工具

| Platform 平台 | Required tool 所需工具 |
|---|---|
| Claude | [Claude Code](https://claude.com/claude-code) |
| ChatGPT | [Codex CLI](https://github.com/openai/codex) |
| Gemini | [Gemini CLI](https://github.com/google-gemini/gemini-cli) |
| Local AI 本地 AI | Free, one-click download inside MultiTool (llama.cpp + Qwen3.5 2B / 4B) 免費，於 MultiTool 內一鍵下載（llama.cpp + Qwen3.5 2B／4B） |

- **Sign in / out** from the **Account** dialog; the top-right corner shows the current status (e.g. `已登入 Claude - Pro`). MultiTool never handles your account, password or token — sign-in is done by each official tool in your browser, and usage counts towards your own plan.
  在「帳號」視窗中**登入／登出**，右上角顯示目前狀態（例如 `已登入 Claude - Pro`）。MultiTool 不會接觸你的帳戶、密碼或權杖——登入由各官方工具於瀏覽器完成，用量計入你自己的方案。
- **Chat history** is saved automatically (`data/ai/chats/`) and listed on the left; reopen a chat to continue it, or rename / delete it with a right-click.
  **對話紀錄**自動儲存（`data/ai/chats/`）並列於左側；重新開啟可繼續對話，右鍵可重新命名或刪除。
- **Script AI helper**: while editing a script, click the cloud button at the bottom-right to describe what you want. The AI rewrites the current script, and you can **apply** or **undo** the result. It cannot create screenshots or guess screen positions — it leaves those for you to fill in and lists them after applying.
  **腳本 AI 助手**：編輯腳本時按右下角的雲朵按鈕，描述你想要的效果，AI 會改寫目前的腳本，你可**套用**或**復原**。AI 無法擷取圖片或猜測螢幕座標，會留空並於套用後列出需自行補充的項目。
- **Local AI** runs entirely on your PC — no account, no cost, no internet after downloading; uses your graphics card (Vulkan) when available. Cantonese is supported (the 4B model is recommended).
  **本地 AI** 完全在你的電腦上運行——無須帳戶、無須付費，下載後無須上網；有顯示卡時自動以 Vulkan 加速。支援廣東話（建議使用 4B 模型）。
- **PC actions**: the AI knows the PC's current date and time, and can **open installed apps** (scanned each time, list saved to `data/ai/keywords.txt`) and **open websites** in your default browser (addresses come from the AI's own knowledge). Only `http/https` pages and Start-menu apps can be opened.
  **電腦操作**：AI 知道電腦的目前日期及時間，並可**開啟已安裝程式**（每次即時掃描，清單儲存於 `data/ai/keywords.txt`）及以預設瀏覽器**開啟網頁**（網址由 AI 自行提供）。只可開啟 `http/https` 網頁及「開始」選單中的程式。
- **Web search**: Claude, ChatGPT and Gemini can search the web for news, weather, prices and other current information.
  **網上搜尋**：Claude、ChatGPT 及 Gemini 可上網搜尋新聞、天氣、價格等最新資料。
- The AI cannot run commands or modify your files.
  AI 不能執行指令或修改你的檔案。

**Voice chat 語音對話** — set up under **AI Assistant → Voice settings** / 於「AI 助手 → 語音設定」設定

- Press the hotkey once and speak; it sends automatically after a 1-second pause (cancels if nothing is said within 1 second). A small window appears at the top-right without stealing focus from your game.
  按一下快捷鍵即可說話，停頓 1 秒後自動傳送（1 秒內沒有說話則取消）。小視窗會出現在右上角，不會搶走遊戲的焦點。
- Speech is recognized **offline on your PC** with Whisper (one-time model download; large-v3-turbo recommended for Cantonese). Recognition language: Cantonese, Mandarin, English or auto.
  語音以 Whisper **於本機離線辨識**（模型只需下載一次；廣東話建議使用 large-v3-turbo）。辨識語言：廣東話、普通話、英文或自動判斷。
- The reply (up to 50 words) is read aloud with a Windows voice (Cantonese voices supported) and is not saved.
  回答（50 字內）會以 Windows 語音讀出（支援廣東話語音），對話不會儲存。

---

## Changelog 更新紀錄

### v1.2
- **New: Voice chat** — press a hotkey and talk to the AI; offline speech recognition (Whisper), automatic end-of-speech detection, replies read aloud, Cantonese supported.
  **新增：語音對話**——按快捷鍵即可與 AI 對話；本機離線語音辨識（Whisper）、自動偵測說話結束、讀出回答，支援廣東話。
- **New: Local AI** — free AI that runs on your PC (llama.cpp + Qwen3.5), one-click download, no account needed.
  **新增：本地 AI**——在電腦上運行的免費 AI（llama.cpp + Qwen3.5），一鍵下載，無須帳戶。
- **New: PC actions** — the AI can tell the current time, open installed apps and open websites.
  **新增：電腦操作**——AI 可報時、開啟已安裝程式及開啟網頁。
- **New: Web search** for Claude, ChatGPT and Gemini.
  **新增：網上搜尋**（Claude、ChatGPT 及 Gemini）。
- **New: Interface languages** — Traditional Chinese, Simplified Chinese and English, switchable at the bottom-left (follows the system language on first launch).
  **新增：介面語言**——繁體中文、簡體中文及英文，可於左下角切換（首次開啟時跟隨系統語言）。
- **New: Script run speed** (0.5x – 3.0x) on the preview page.
  **新增：腳本運行速度**（0.5x – 3.0x），設於預覽頁。
- The "Wait" step is renamed **Delay**; delay and hold times are now set in milliseconds (ms).
  「等待」步驟改名為**延遲**；延遲及按住時間改以毫秒（ms）設定。
- AI Assistant: the top-right "Sign in" button is now **Account** (shows sign-in status and plan, e.g. `已登入 Claude - Pro`); settings button renamed **Voice settings**.
  AI 助手：右上角「登入」改為**帳號**（顯示登入狀態及方案，例如 `已登入 Claude - Pro`）；設定按鈕改名為**語音設定**。
- Opening apps / websites now works when MultiTool runs as Administrator.
  以系統管理員身分運行 MultiTool 時亦可正常開啟程式及網頁。

### v1.1
- **New: AI Assistant page** — chat with Claude, ChatGPT or Gemini through your own installed CLI tool, with sign-in / sign-out, status display and saved chat history.
  **新增：AI 助手頁面**——透過你自己安裝的命令列工具與 Claude、ChatGPT 或 Gemini 對話，支援登入／登出、狀態顯示及對話紀錄。
- **New: Script AI helper** — a cloud button in the script editor opens a small chat that can write or modify the current script, with apply and undo.
  **新增：腳本 AI 助手**——腳本編輯頁右下角的雲朵按鈕可開啟小型對話欄，協助編寫或修改目前腳本，支援套用及復原。
- Home page now introduces the AI Assistant.
  首頁加入 AI 助手介紹。

### v1.0
- First release: My Scripts (keys, mouse clicks, waits, find image & click, loops, IF conditions, parallel IF blocks, background mode, hotkeys), custom title bar and auto update.
  首個版本：我的腳本（按鍵、滑鼠點擊、等待、搵圖點擊、循環、IF 條件、並行 IF 區塊、背景執行、快捷鍵）、自訂標題列及自動更新。

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
| `src/` | Source code 原始碼：`App.cs` main window, home page, title bar 主視窗、首頁及標題列；`UI.cs` dark theme and controls 深色主題及控件；`Common.cs` input, screen capture, image matching, hotkeys 輸入、截圖、圖像比對及快捷鍵；`Scripts.cs` script model, editor and runner 腳本資料、編輯器及執行器；`Ai.cs` AI platforms and AI Assistant page AI 平台及 AI 助手頁面；`AiAssist.cs` script AI helper 腳本 AI 助手；`LocalAi.cs` local AI 本地 AI；`PcActions.cs` time / open apps & websites 報時及開啟程式與網頁；`Voice.cs`, `VoiceEngine.cs`, `Whisper.cs` voice chat and speech recognition 語音對話及語音辨識；`Lang.cs`, `LangTable.cs` interface languages 介面語言；`Updater.cs` auto update 自動更新 |
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

1. Update `Version` in `src/App.cs` (e.g. `v1.2`).
   更新 `src/App.cs` 中的 `Version`（例如 `v1.2`）。
2. Build `Build/MultiTool.exe`.
   打包產生 `Build/MultiTool.exe`。
3. Create a GitHub Release whose **tag matches the version** (`v1.2`), attach `MultiTool.exe` or a zip containing it, and mark it as **latest**.
   建立 GitHub Release，**標籤須與版本號相同**（`v1.2`），附上 `MultiTool.exe` 或包含該檔案的壓縮檔，並設為 **latest**。
4. Existing users will be prompted to update the next time they launch the app.
   現有用戶下次啟動程式時便會收到更新提示。

---

## Third-party components 第三方元件

Downloaded by the user on demand, not bundled in the executable / 由用戶按需下載，並未包含於執行檔內：

- [whisper.cpp](https://github.com/ggml-org/whisper.cpp) and [OpenAI Whisper](https://github.com/openai/whisper) models — MIT License
- [llama.cpp](https://github.com/ggml-org/llama.cpp) — MIT License
- [Qwen3.5](https://huggingface.co/Qwen) models (GGUF by [unsloth](https://huggingface.co/unsloth)) — see each model's license / 請參閱各模型的授權

---

## License 授權

© 白兔糖v3v. Licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE).
© 白兔糖v3v。本軟件依據 [PolyForm Noncommercial License 1.0.0](LICENSE) 授權。

Free to use, modify and share for non-commercial purposes. **Resale and any commercial use are prohibited.**
可免費作非商業用途使用、修改及分享。**嚴禁轉售及任何商業用途。**
