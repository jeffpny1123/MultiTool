<p align="center"><img src="assets/icon.png" width="128" alt="MultiTool icon"></p>

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
| Key press 按鍵 | Any key or key combination (e.g. `Shift + Alt + T`); hold for N milliseconds, press down only, or release only.<br>任何按鍵或組合鍵（例如 `Shift + Alt + T`）；可設定按住 N 毫秒、僅按下或僅彈起。 |
| Mouse 滑鼠 | Left button, right button or wheel (chosen from a drop-down) at a position, with a 3-second position picker. Buttons: hold, press down only, or release only. Wheel: up / down N notches. **Drag**: "Down" at A → delay → "Up" at B.<br>於指定座標使用左鍵、右鍵或滾輪（下拉式選單選擇），並提供 3 秒倒數的座標拾取功能。按鍵可按住、僅按下或僅彈起；滾輪可向上／向下捲動 N 格。**拖曳**：在 A 點「按下」→ 延遲 → 在 B 點「彈起」。 |
| Delay 延遲 | Pause for N milliseconds, or a random time between two values.<br>暫停 N 毫秒（ms），或於兩個時間之間隨機暫停。 |
| Type text 輸入文字 | Types a piece of text (any language); line breaks press Enter.<br>輸入一段文字（支援中文），換行會按 Enter。 |
| Find image & click 搵圖點擊 | Snip screen regions (up to 6 images per step) and click the best match above the similarity threshold.<br>擷取螢幕區域作為目標圖片（每個步驟最多 6 張），並點擊相似度最高且達到門檻的位置。 |
| Loop / Break 循環／中斷循環 | Repeat steps N times (0 = forever), optionally stop after N seconds or at a set clock time, and break out early when needed. When a loop ends, everything inside it stops at once and keys / buttons it pressed down are released.<br>重複執行步驟 N 次（0 代表無限次），可設定運行 N 秒後或到指定時間停止，並可提前跳出循環。循環結束時，其內所有步驟會即時停止，循環內按下的按鍵／滑鼠鍵會自動彈起。 |
| Parallel 同時運行 | Two or more branches (add more with **+**, up to 8) run at the same time; the next step runs after all branches finish.<br>兩條或以上支線（可按 **+** 新增，最多 8 條）同時執行，全部完成後才執行下一步。 |
| IF condition IF 條件 | Image found, image not found, pixel color, clock time, countdown or time limit (script has run for N seconds). Several conditions can be combined with **AND / OR**; IFs can be nested.<br>條件包括：找到圖片、找不到圖片、指定位置的顏色、現實時間、倒數計時及時限（腳本已運行 N 秒）。多個條件可用 **AND／OR** 組合；可多層巢狀使用。 |
| System 系統 | Open an app, close a program, switch to a window, shut down the PC, or play a sound (once or repeating).<br>打開應用程式、關閉程式、切換視窗、關閉電腦，或播放音效（播放一次或不斷重複）。 |
| Stop script 停止腳本 | Stops the whole script, including all IF blocks and threads.<br>停止整個腳本，包括所有 IF 區塊及線程。 |

- **Threads**: click **+ New thread** below the main thread to add threads that run alongside it.
  **線程**：於主線程下方按 **+ 新線程**，新增與主線程同時運行的線程。
- **Record**: click **● Record** at the top of the editor, then play normally; keys, mouse buttons and wheel (not mouse movement) and the delays between them become steps. Press **F10** to stop.
  **錄製**：於修改頁頂部按 **● 錄製**，然後正常操作；按鍵、滑鼠鍵、滾輪（不記錄滑鼠移動）及其間隔會變成步驟。按 **F10** 停止。
- Run **once** or **continuously**, with a per-script **hotkey** to start and stop.
  可選擇**執行一次**或**持續執行**，每個腳本可設定獨立**快捷鍵**開始及停止。
- **Target window**: pick the program window a script works in (in the script list, the preview page or the editor). Positions are **relative to that window and scale with its size**, so you can move or resize it. If the same program is open several times, each window can be picked separately (`#1`, `#2`…).
  **目標視窗**：可選擇腳本運行的程式視窗（於腳本列表、預覽頁或修改頁設定）。座標**相對於該視窗並按視窗大小比例換算**，可隨意移動或調整視窗大小。同一程式開啟多個時可分別選擇（`#1`、`#2`…）。
- **Foreground mode** (default) uses the real mouse and keyboard; with a target window chosen, it switches to that window before starting. Only one foreground script can run at a time.
  **前景執行**（預設）使用真實滑鼠及鍵盤；已選擇目標視窗時，會先切換至該視窗再開始。同一時間只可運行一個前景腳本。
- **Background mode** captures, clicks, drags and types only inside the target window, even when it is covered by other windows. Some games (e.g. many DirectX games) do not accept background input; the window must not be minimized.
  **背景執行**只於目標視窗內擷取畫面、點擊、拖曳及輸入按鍵，即使被其他視窗遮擋亦可運行。部分遊戲（例如不少 DirectX 遊戲）不接受背景輸入；程式視窗不可最小化。
- **Administrator detection**: if the target program runs as Administrator, MultiTool offers to restart itself as Administrator (otherwise Windows blocks the input).
  **管理員權限偵測**：如目標程式以系統管理員權限執行，MultiTool 會提示以系統管理員身分重新啟動（否則 Windows 會阻擋輸入）。
- **Script list**: each script has its own **▶ Start / ■ Stop** button and a **run count** (once, N times or forever). Running scripts get a green frame and show their current round and step.
  **腳本列表**：每個腳本有獨立的 **▶ 開始／■ 停止** 按鈕及**運行次數**（一次、N 次或持續運行）。運行中的腳本會以綠色外框標示，並顯示目前輪數及步驟。
- **Emergency stop**: while scripts run, press **Esc 3 times** or push the mouse to the **top-left corner** of the screen to stop everything (only your own input counts, not the script's).
  **緊急停止**：腳本運行時連按 **3 下 Esc**，或將滑鼠移至螢幕**左上角**，即可停止所有腳本（只計你自己的操作，不計腳本模擬的輸入）。
- **Start several scripts together**: tick scripts in the list and click **Start selected**; **Stop all** stops every running script. One global hotkey does both: it starts the ticked scripts when nothing is running and stops all when something is.
  **多個腳本同時開始**：於列表勾選腳本後按**開始已選**；**全部停止**會停止所有運行中的腳本。同一個全域快捷鍵兼具兩者：沒有腳本運行時開始已選腳本，有腳本運行時全部停止。
- **Editing**: right-click a step to copy / cut / paste it (loops, IFs and branches are copied with everything inside) or insert a new step above / below. **↶ Undo** at the bottom-right restores the last change. Loops and parallel steps can be collapsed, and the top bar stays visible while scrolling.
  **編輯**：右鍵步驟可複製／剪下／貼上（循環、IF 及支線會連同其內容一併複製），或在上方／下方插入新步驟。右下角 **↶ 復原** 可還原上一次改動。循環及同時運行可摺疊，捲動時頂部列保持顯示。
- **Run speed** (0.5x – 3.0x) on the preview page scales delays, hold times, check intervals and countdowns; it can be changed while the script is running.
  預覽頁的**運行速度**（0.5x – 3.0x）會按倍率調整延遲、按住時間、檢查間隔及倒數，運行中亦可即時更改。
- Scripts are validated before running (missing images, unset keys, empty loops, etc.).
  執行前會自動檢查腳本（例如缺少圖片、未設定按鍵、空白循環等）。
- **Auto update** checks GitHub Releases on startup and updates with one click.
  **自動更新**：啟動時檢查 GitHub Releases，一鍵完成更新。

**Script library 腳本庫** — share scripts peer-to-peer, no server / 點對點分享腳本，無須伺服器

- Open it with **Script library →** at the bottom-right of **My Scripts**.
  於**我的腳本**右下角按 **腳本庫 →** 開啟。
- Search and download scripts shared by users who currently have MultiTool open; files come directly from their PC. If they are offline or unreachable, the download fails.
  搜尋及下載目前開啟 MultiTool 的用戶所公開的腳本，檔案直接從對方電腦傳送；對方離線或無法連線則無法下載。
- Share your own: turn on sharing in **Script library → Sharing settings**, then right-click a script → **Share to library**. Your IP address is visible to other users while sharing; window handles, hotkeys and your user folder path are not shared.
  公開自己的腳本：於 **腳本庫 → 分享設定** 開啟分享，再於腳本右鍵選擇 **公開到腳本庫**。分享期間你的 IP 地址會被其他用戶看到；視窗、快捷鍵及用戶資料夾路徑不會被分享。
- Before adding a downloaded script, its system actions and typed text are listed for you to confirm. Downloaded scripts never start automatically.
  加入下載的腳本前，會列出其中的系統動作及輸入文字供你確認；下載的腳本不會自動開始。

**Tool library 工具庫** — choose which tools appear in the sidebar / 選擇側欄顯示的工具

- Tools are shown as tiles with a search box. **Install** adds a tool to the sidebar and **Remove** hides it again; your scripts and videos are kept. A fresh download starts with no tools installed; when upgrading from an older version, **My Scripts** stays installed.
  工具以方格排列，並設有搜尋欄。按**安裝**會在側欄加入該工具，按**移除**則會隱藏；你的腳本及影片會保留。全新下載時預設未安裝任何工具；由舊版更新時，**我的腳本**會保持已安裝。
- Tools that need extra files download them when installed (e.g. the recorder downloads FFmpeg, about 100 MB). No server of our own is involved.
  需要額外檔案的工具會於安裝時下載（例如錄影工具會下載約 100 MB 的 FFmpeg），不經任何自設伺服器。
- **Categories** (Gaming, Media, Productivity, System, Network) and **All / Installed / Not installed** tabs can be combined; commonly used tools are listed first and beta tools last.
  **分類**（遊戲、媒體、效率、系統、網絡）可與**全部／已安裝／未安裝**分頁一併篩選；常用工具排在最前，測試版排在最後。

**Screen recorder 錄影工具** — install it from the Tool library / 於工具庫安裝

- Record the full screen, a region or a window, with system audio and / or microphone, each with its own volume (0–300%).
  錄製全螢幕、指定範圍或指定視窗，可同時錄製電腦聲音及／或咪高峰，兩者可分別調整音量（0–300%）。
- Frame rate 15–144 FPS; resolution Original / 720P / 1080P / 2K / 4K (never upscales); show or hide the mouse cursor.
  幀率 15–144 FPS；解像度可選原始／720P／1080P／2K／4K（不會放大）；可選擇是否錄製滑鼠游標。
- Codec H.264 / H.265 (HEVC) / AV1 with GPU encoding (NVIDIA / Intel / AMD, auto-detected) or CPU only; quality Highest / High / Medium / Low; output MP4, MKV or AVI.
  編碼器 H.264／H.265（HEVC）／AV1，可用顯示卡編碼（自動偵測 NVIDIA／Intel／AMD）或只用 CPU；品質分最高／高／中／低；輸出 MP4、MKV 或 AVI。
- Start / stop with a hotkey from anywhere. A small timer appears at the top-right while recording and is not captured in the video.
  可於任何地方以快捷鍵開始／停止。錄影期間右上角會顯示計時器，計時器不會被錄入影片。
- **Recent recording** shows the latest video with **Play** and **Delete** (moves it to the Recycle Bin).
  **最近錄影**顯示最新的影片，可**播放**或**刪除**（移至資源回收筒）。

**Instant Replay 精彩重播** — keeps recording in the background, saves the last moments on a hotkey / 背景持續錄影，按快捷鍵儲存最近一段

- Keeps the last 15 s – 5 min of the screen (with system audio / microphone) and saves it as MP4 only when you press the hotkey, like ShadowPlay. Nothing is saved otherwise.
  背景保留最近 15 秒至 5 分鐘的畫面（可連電腦聲音／咪高峰），按快捷鍵才儲存為 MP4，類似 ShadowPlay；未按則不會儲存任何內容。
- Choose screen, FPS, resolution, quality, audio volume and cursor; can start automatically with MultiTool.
  可選擇螢幕、FPS、解像度、品質、音量及滑鼠游標；可於開啟 MultiTool 時自動開始。

**Key Remap 遊戲改鍵** — turn one key into another / 將一個按鍵變成另一個按鍵

- Rules like Caps Lock → Ctrl or mouse side button → F; targets can be keys or mouse buttons. Each rule can be limited to one program. On / off with a hotkey.
  規則例如 Caps Lock → Ctrl、滑鼠側鍵 → F；目標可為按鍵或滑鼠鍵，每條規則可限定只於指定程式生效，並可用快捷鍵開關。

**Window Tools 視窗工具**

- Keep any window on top, set its opacity (10–100%), make it borderless fullscreen or center it. Hotkeys act on the window in front.
  將任何視窗置頂、設定透明度（10–100%）、變為無邊框全螢幕或置中；快捷鍵作用於最前面的視窗。

**Clipboard History 剪貼簿紀錄**

- Remembers copied text and images (searchable, pinnable). A hotkey opens a popup anywhere; press 1–9 to paste. Text history can optionally be kept after restart; images stay in memory only.
  記錄複製過的文字及圖片（可搜尋、釘選）；快捷鍵可於任何地方開啟小視窗，按 1–9 直接貼上。文字紀錄可選擇於重新開啟後保留；圖片只保留於記憶體。

**Text Recognition 文字辨識**

- Select any area on screen (or an image file) to extract its text with Windows' built-in OCR — offline, no download. Copies the result automatically.
  框選畫面任何範圍（或選擇圖片檔），以 Windows 內建 OCR 擷取文字——離線使用，無須下載，並自動複製結果。

**Converter 轉檔工具** — two tabs: Media and Documents / 分「影音」及「文件」兩個分頁

- **Media**: convert video, audio and images (MP4, MKV, WEBM, AVI, MOV, GIF, MP3, WAV, M4A, PNG, JPG, WEBP), resize and trim. Drag files in, convert in batches with progress. FFmpeg (~100 MB) is downloaded on first use from inside the tab.
  **影音**：影片、聲音、圖片互相轉換（MP4、MKV、WEBM、AVI、MOV、GIF、MP3、WAV、M4A、PNG、JPG、WEBP），可縮小及剪輯頭尾；可拖曳檔案批次轉換並顯示進度。首次使用時於分頁內下載 FFmpeg（約 100 MB）。
- **Documents**: convert between Excel (.xlsx), CSV, TSV and JSON without Office. Each sheet becomes its own file, or merge several files into one workbook. CSV output in UTF-8 with BOM (opens correctly in Excel), UTF-8, Big5 or GBK; old Big5 CSVs are detected automatically. Old `.xls` is not supported.
  **文件**：Excel（.xlsx）、CSV、TSV、JSON 互相轉換，無須安裝 Office。每張工作表輸出一個檔案，或將多個檔案合併為一個活頁簿。CSV 可輸出 UTF-8（附 BOM，Excel 可正常顯示中文）、UTF-8、Big5 或 GBK；自動辨認舊 Big5 CSV。不支援舊版 `.xls`。

**Video Downloader 影片下載** — uses [yt-dlp](https://github.com/yt-dlp/yt-dlp) / 使用 yt-dlp

- Paste a link from YouTube, Bilibili, Twitch, TikTok, X, Vimeo, SoundCloud and 1000+ other sites and save it as MP3 or MP4; playlists supported. yt-dlp and FFmpeg are downloaded on install.
  貼上 YouTube、Bilibili、Twitch、TikTok、X、Vimeo、SoundCloud 等 1000 多個網站的連結，儲存為 MP3 或 MP4，支援播放清單。安裝時下載 yt-dlp 及 FFmpeg。

**Performance Monitor 效能監看**

- Live CPU, GPU, RAM, VRAM, disk and network usage with graphs, plus GPU temperature on NVIDIA cards, using Windows' own performance counters. Lists the programs using the most CPU and memory.
  即時顯示 CPU、GPU、RAM、VRAM、磁碟及網絡用量與走勢圖，NVIDIA 顯示卡另顯示 GPU 溫度，使用 Windows 內置效能計數器；並列出最耗 CPU 及記憶體的程式。
- **Game overlay**: a small always-on-top, click-through box in a screen corner, with a hotkey. Choose items, corner, font size and opacity. Works with borderless / windowed games (exclusive fullscreen covers it).
  **遊戲疊加**：於螢幕角落顯示永遠置頂、滑鼠可穿透的小框，並設有快捷鍵；可選擇顯示項目、位置、字體大小及不透明度。適用於無邊框／視窗模式遊戲（獨佔全螢幕會將其覆蓋）。

**Disk Space & Health 磁碟空間及健康**

- Used / free space for every drive, and drive health (status, temperature, SSD life used, power-on hours, read / write errors) from Windows' built-in storage data. Temperature, wear and hours need MultiTool to run as Administrator.
  顯示每個磁碟的已用／剩餘空間，以及 Windows 內置存放裝置資料中的硬碟健康（狀態、溫度、SSD 已用壽命、開機時數、讀寫錯誤）。溫度、損耗及開機時數須以系統管理員身分執行 MultiTool。
- **Space analysis**: scan a drive or folder and browse it as a treemap; the 30 largest files (100 MB+) are listed. Right-click to open in Explorer or move to the Recycle Bin (with confirmation).
  **空間分析**：掃描磁碟或資料夾並以方格圖瀏覽，列出最大的 30 個檔案（100 MB 以上）；右鍵可於檔案總管開啟或移至資源回收筒（會先確認）。

**Remote Desktop (beta) 遠端桌面（測試版）** — built in, no extra program / 內置，無須額外程式

- Connect with an ID and password to control another PC running MultiTool, or let someone connect to yours. End-to-end encrypted (ECDH P-256 key exchange, password-authenticated, AES-GCM).
  以 ID 及密碼連接另一部執行 MultiTool 的電腦並遠端控制，或讓別人連入你的電腦；全程端對端加密（ECDH P-256 交換金鑰、密碼驗證、AES-GCM）。
- There is no relay server: the host PC must be reachable directly (same LAN, or a public IP / UPnP). It does **not** work when the host is behind CGNAT, mobile data or most public Wi-Fi; a VPN such as Tailscale works around this.
  沒有中轉伺服器：被控電腦必須可直接連線（同一區網，或有公共 IP／UPnP）。被控電腦使用 CGNAT、流動數據或大部分公共 Wi-Fi 時**無法連線**，可改用 Tailscale 等 VPN。

**Game Servers 遊戲伺服器** — host dedicated servers with Valve's SteamCMD / 使用 Valve 官方 SteamCMD 架設專用伺服器

- One-click install / update with download progress: Palworld, Valheim, Enshrouded, 7 Days to Die, Satisfactory, V Rising, ARK: Survival Ascended, Rust, Project Zomboid, Counter-Strike 2, plus any number of custom Steam dedicated servers (App ID + executable + arguments).
  一鍵安裝／更新並顯示下載進度：Palworld、Valheim、Enshrouded、7 Days to Die、Satisfactory、V Rising、ARK: Survival Ascended、Rust、Project Zomboid、Counter-Strike 2，另可無限新增自訂 Steam 專用伺服器（App ID + 執行檔 + 參數）。
- **Server info** tab: name, password, admin password, players, port and extra arguments (written into each game's config automatically), start with MultiTool, auto restart on crash, public / LAN IP, UPnP port opening, firewall exception, and scheduled backups with restore.
  **伺服器資訊**分頁：伺服器名稱、密碼、管理員密碼、人數、端口及額外參數（自動寫入各遊戲設定檔）、開啟 MultiTool 時自動開始、當機自動重新啟動、對外／區網 IP、UPnP 開放端口、防火牆例外，以及定時備份與還原。
- **Files** tab: quick folders, the game's config files with a built-in editor, and a file browser. **Console** tab: live output and commands.
  **檔案**分頁：常用資料夾、遊戲設定檔（內建編輯器）及檔案瀏覽；**控制台**分頁：即時輸出及輸入指令。
- Servers stop gracefully (save first) when stopped or when MultiTool closes.
  停止伺服器或關閉 MultiTool 時，會先讓伺服器儲存再停止。
- **AI helper**: with an AI signed in, a cloud button at the bottom-right can fill in server settings ("8 players, password abc123"), edit game config files inside the server folder only, and read the last lines of the console to explain errors. **Ask AI why** appears when a server fails.
  **AI 助手**：已登入 AI 時，右下角雲朵按鈕可代為填寫伺服器設定（例如「8 人遊玩，密碼 abc123」）、只於伺服器資料夾內修改遊戲設定檔，並查看控制台最後數行解釋錯誤；伺服器出問題時會出現**問 AI 原因**按鈕。

**Settings 設定**

- Start with Windows, minimize to the system tray on close (asked the first time you close), check for updates. Light / dark theme and language are at the bottom-left.
  開機時啟動、關閉時最少化至系統匣（首次關閉時會詢問）、檢查更新。日光／夜間主題及語言於左下角切換。
- **Hotkeys**: one table with every installed tool's hotkey (editable). Tick **Use outside page** to use a hotkey anywhere; otherwise it only works on that tool's page. Duplicate hotkeys cannot be ticked.
  **快捷鍵**：一個表格列出所有已安裝工具的快捷鍵（可直接修改）。勾選**頁面外使用**即可於任何地方使用，否則只可在該工具頁使用；重複的快捷鍵無法勾選。
- **Sidebar order**: drag items in the sidebar up or down, or right-click for Move up / Move down / Reset order.
  **側欄排序**：可直接拖曳側欄項目上下移動，或右鍵選擇上移／下移／重設排序。

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
- **Folder per chat**: connect a folder to a chat and the AI can write, run and debug code, and create / edit / delete files inside that folder. Without a folder the AI can only chat and cannot run commands or modify your files.
  **每個對話可連接資料夾**：連接後，AI 可於該資料夾內編寫、執行及除錯程式，並建立／修改／刪除檔案。未連接資料夾時，AI 只可對話，不能執行指令或修改你的檔案。

**Voice chat 語音對話** — set up under **AI Assistant → Voice settings** / 於「AI 助手 → 語音設定」設定

- Press the hotkey once and speak; it sends automatically after a 1-second pause (cancels if nothing is said within 1 second). A small window appears at the top-right without stealing focus from your game.
  按一下快捷鍵即可說話，停頓 1 秒後自動傳送（1 秒內沒有說話則取消）。小視窗會出現在右上角，不會搶走遊戲的焦點。
- Speech is recognized **offline on your PC** with Whisper (one-time model download; large-v3-turbo recommended for Cantonese). Recognition language: Cantonese, Mandarin, English or auto.
  語音以 Whisper **於本機離線辨識**（模型只需下載一次；廣東話建議使用 large-v3-turbo）。辨識語言：廣東話、普通話、英文或自動判斷。
- The reply (up to 50 words) is read aloud with a Windows voice (Cantonese voices supported) and is not saved.
  回答（50 字內）會以 Windows 語音讀出（支援廣東話語音），對話不會儲存。

---

## Changelog 更新紀錄

### v1.9
- **New: Video Downloader** — save links from YouTube, Bilibili, Twitch, TikTok and 1000+ sites as MP3 / MP4 (yt-dlp).
  **新增：影片下載**——將 YouTube、Bilibili、Twitch、TikTok 等 1000 多個網站的連結儲存為 MP3／MP4（yt-dlp）。
- **New: Remote Desktop (beta)** — built-in, ID + password, end-to-end encrypted, no relay server.
  **新增：遠端桌面（測試版）**——內置，以 ID + 密碼連線，端對端加密，沒有中轉伺服器。
- **New: Performance Monitor** — live CPU / GPU / RAM / VRAM / disk / network with graphs, top programs and an in-game corner overlay.
  **新增：效能監看**——即時 CPU／GPU／RAM／VRAM／磁碟／網絡走勢圖、最耗資源程式，以及遊戲角落疊加顯示。
- **New: Disk Space & Health** — free space per drive, drive health, treemap space analysis and largest files.
  **新增：磁碟空間及健康**——各磁碟剩餘空間、硬碟健康、方格圖空間分析及最大檔案。
- **Changed: Converter** now has **Media** and **Documents** tabs; Documents converts Excel / CSV / TSV / JSON and fixes garbled Chinese CSVs. FFmpeg is only downloaded when the Media tab is first used.
  **變更：轉檔工具**新增**影音**及**文件**分頁；文件分頁可轉換 Excel／CSV／TSV／JSON 並修正 CSV 中文亂碼。FFmpeg 只會於首次使用影音分頁時下載。
- **Changed: Tool library** — category filters, commonly used tools first, beta tools last.
  **變更：工具庫**——新增分類篩選，常用工具排最前，測試版排最後。
- **Fixed:** the Tool library could not be scrolled when the list was taller than the window.
  **修正：**工具庫內容超出視窗時無法向下捲動。

### v1.8
- **New tools** in the Tool library: Instant Replay, Key Remap, Window Tools, Clipboard History, Text Recognition, Converter and Game Servers.
  **新工具**（於工具庫安裝）：精彩重播、遊戲改鍵、視窗工具、剪貼簿紀錄、文字辨識、轉檔工具及遊戲伺服器。
- **New: Game Servers** — SteamCMD dedicated servers with settings, backups, UPnP, file editor, console and an AI helper that can fill in settings and explain errors.
  **新增：遊戲伺服器**——SteamCMD 專用伺服器，附設定、備份、UPnP、檔案編輯、控制台，以及可代填設定及解釋錯誤的 AI 助手。
- **New: Hotkeys table** in Settings — change every tool's hotkey in one place and choose whether it works outside its page.
  **新增：設定中的快捷鍵表**——一處修改所有工具的快捷鍵，並選擇是否於頁面外使用。
- **Changed:** redesigned home page — shows only installed tools, with tips for them.
  **變更：**重新設計首頁——只顯示已安裝的工具及相關提示。
- **Changed:** Tool library tiles show an icon for each tool.
  **變更：**工具庫方格顯示各工具圖示。

### v1.7
- **New: Tool library** (replaces the fixed sidebar) — install or remove tools as tiles, with search. Tools that need extra files download them on install.
  **新增：工具庫**——以方格安裝或移除工具，並可搜尋；需要額外檔案的工具會於安裝時下載。
- **New: Screen recorder** — full screen / region / window, system audio and microphone with separate volume, 15–144 FPS, 720P–4K, H.264 / H.265 / AV1 with GPU encoding, quality levels, MP4 / MKV / AVI, cursor on / off, hotkey, on-screen timer that is not recorded.
  **新增：錄影工具**——全螢幕／範圍／視窗，電腦聲音及咪高峰可分別調整音量，15–144 FPS、720P–4K，H.264／H.265／AV1 顯示卡編碼，品質級別，MP4／MKV／AVI，可開關滑鼠游標，快捷鍵，以及不會被錄入的計時器。
- **New: Sidebar order** — drag items or right-click to reorder; the order is remembered.
  **新增：側欄排序**——拖曳或右鍵調整次序，並會記住。
- **Changed:** the Script library moved into My Scripts (button at the bottom-right).
  **變更：**腳本庫移至我的腳本內（右下角按鈕）。
- **Changed:** script hotkeys now start their scripts from any other page or when MultiTool is minimized; on the My Scripts page only the previewed script can be started by hotkey.
  **變更：**於其他頁面或 MultiTool 最小化時，可用快捷鍵開始任何腳本；在我的腳本頁內，只有預覽中的腳本可用快捷鍵開始。
- Fixed: setting a hotkey immediately triggered it (e.g. started a recording).
  修正：設定快捷鍵時會即時觸發（例如開始錄影）。
- Fixed: all hotkeys stopped working after setting one and switching to another page.
  修正：設定快捷鍵後切換至其他頁面，所有快捷鍵失效。

### v1.6
- **New: Script library** — share and download scripts peer-to-peer, no server.
  **新增：腳本庫**——點對點分享及下載腳本，無須伺服器。
- **New: AI folder per chat** — the AI can write, run and debug code inside a connected folder.
  **新增：AI 對話連接資料夾**——AI 可於連接的資料夾內編寫、執行及除錯程式。
- **New: Run count** — run a script once, N times or forever, set from the list, preview or editor.
  **新增：運行次數**——可於列表、預覽頁或修改頁設定運行一次、N 次或持續運行。
- **New: Start / Stop button per script** in the list; running scripts have a green frame and show their round and step.
  **新增：列表中每個腳本的開始／停止按鈕**；運行中的腳本以綠色外框標示，並顯示輪數及步驟。
- **New: Emergency stop** — Esc 3 times or mouse to the top-left corner.
  **新增：緊急停止**——連按 3 下 Esc 或將滑鼠移至左上角。
- **New: Settings page** — start with Windows, minimize to tray on close.
  **新增：設定頁**——開機時啟動、關閉時最少化至系統匣。
- **New: Light theme**, app icon, and an updated home page.
  **新增：日光主題**、程式圖示，以及更新後的首頁。
- Background mode now also tells the game it is focused before sending input, which helps some games.
  背景執行會在送出輸入前通知遊戲已取得焦點，有助部分遊戲接收輸入。
- Fixed: overlapping buttons at the top of the preview / editor and on the AI page.
  修正：預覽頁、修改頁頂部及 AI 頁的按鈕重疊問題。

### v1.5
- **New: System step** — open an app, close a program, switch to a window, shut down the PC, or play a sound (once or repeating).
  **新增：「系統」步驟**——打開應用程式、關閉程式、切換視窗、關閉電腦，或播放音效（播放一次或不斷重複）。
- **New: Record script** — record keys, mouse buttons and wheel with their timing; press F10 to stop.
  **新增：錄製腳本**——錄製按鍵、滑鼠鍵及滾輪連同時間間隔；按 F10 停止。
- **New: Type text step** and **mouse wheel** (in the Mouse step's drop-down).
  **新增：「輸入文字」步驟**及**滑鼠滾輪**（於「滑鼠」步驟的下拉式選單）。
- **New: Random delay** — pause for a random time between two values.
  **新增：隨機延遲**——於兩個時間之間隨機暫停。
- **New: AND / OR** — an IF can have several conditions.
  **新增：AND／OR**——IF 可加入多個條件。
- **New: Threads below the main thread**, **Undo**, **Cut**, collapsible loops / parallel steps, and a fixed top bar in the editor.
  **新增：主線程下方的線程**、**復原**、**剪下**、可摺疊的循環／同時運行，以及修改頁固定頂部列。
- "Mouse click" is renamed "Mouse"; loop stop conditions are added with "+ Stop condition"; less common steps are under "+ More"; the editor's "Start" button is renamed "Test".
  「滑鼠點擊」改名為「滑鼠」；循環停止條件改為以「+ 停止條件」加入；較少用的步驟移至「+ 更多」；修改頁的「開始」改名為「測試」。
- Faster editing with many steps, smoother window resizing, and buttons no longer overlap on long rows.
  步驟多時編輯更快、調整視窗大小更流暢，長行的按鈕不再重疊。

### v1.4
- **New: Parallel step** — two or more branches run at the same time (add more with **+**); the next step runs after all finish.
  **新增：「同時運行」步驟**——兩條或以上支線同時執行（可按 **+** 新增），全部完成後才執行下一步。
- **New: New thread step** — starts a side thread; the main thread continues immediately.
  **新增：「新線程」步驟**——開啟一條同時執行的支線，主線程立即繼續。
- **New: Time limits** — loops can stop after N seconds or at a set clock time; new IF condition "time limit" (script has run for N seconds).
  **新增：時限**——循環可設定運行 N 秒後或到指定時間停止；新增「時限」IF 條件（腳本已運行 N 秒）。
- **New: Stop script step** — stops the whole script, including IF blocks and threads.
  **新增：「停止腳本」步驟**——停止整個腳本，包括 IF 區塊及線程。
- **New: Right-click copy / paste / insert** — copy a step (loops, IFs and branches with all their contents), paste it above or below, or insert a new step above or below.
  **新增：右鍵複製／貼上／插入**——複製步驟（循環、IF 及支線連同全部內容）、貼上至上方或下方，或在上方／下方插入新步驟。
- **New: Target window for foreground scripts** — choose a window even without background mode; the script switches to it before starting.
  **新增：前景腳本亦可選擇目標視窗**——無須開啟背景執行亦可選擇視窗，開始前會自動切換至該視窗。
- **New: Positions scale with the window size** — clicks and pixel checks follow the target window when it is resized.
  **新增：座標按視窗大小比例換算**——調整目標視窗大小後，點擊及顏色檢查位置會自動跟隨。
- **New: Administrator detection** — offers to restart MultiTool as Administrator when the target program needs it.
  **新增：管理員權限偵測**——目標程式需要時，提示以系統管理員身分重新啟動 MultiTool。
- Only one foreground script can run at a time; starting a second one shows a reminder.
  同一時間只可運行一個前景腳本；嘗試開始第二個時會顯示提醒。
- The stop-all hotkey now toggles: start the ticked scripts, or stop all.
  「全部停止」快捷鍵改為開關：開始已勾選的腳本，或全部停止。
- Foreground mouse input now uses real mouse events, so more games register clicks.
  前景滑鼠輸入改用真實滑鼠事件，更多遊戲能夠接收點擊。
- Fixed: when a loop's time limit ends, inner loops and delays now stop immediately, and keys pressed down inside the loop are released.
  修正：循環時限結束時，內層循環及延遲會即時停止，循環內按下的按鍵會自動彈起。
- Fixed: switching back to MultiTool no longer puts the hotkey box into recording mode.
  修正：切換回 MultiTool 時不會再自動進入快捷鍵設定。
- Fixed: the mouse wheel no longer changes number boxes; it scrolls the page instead.
  修正：滑鼠滾輪不會再改動數字欄，改為捲動頁面。
- The script AI helper understands all new steps and options.
  腳本 AI 助手支援所有新步驟及設定。

### v1.3
- **New: Start several scripts together** — the script list is now one script per row with a tick box; tick scripts and click **Start selected** (each follows its own run-once / repeat setting).
  **新增：多個腳本同時開始**——腳本列表改為每行一個腳本並附勾選格；勾選後按**開始已選**即可一同開始（各自按「執行一次／持續執行」設定運行）。
- **New: Stop all** button and global hotkey; the button is greyed out when nothing is running.
  **新增：全部停止**按鈕及全域快捷鍵；沒有腳本運行時按鈕會變灰。
- **New: Choose the background program from the script list**, preview page or editor (no need to open the editor).
  **新增：可於腳本列表、預覽頁或修改頁選擇背景執行的程式**（無須進入修改頁）。
- **New: Positions relative to the program window** in background mode — move the window freely; existing scripts are converted automatically.
  **新增：背景執行時座標相對程式視窗**——可隨意移動視窗位置；舊腳本會自動轉換。
- **New: Drag** — "Down" at A, a delay, then "Up" at B drags from A to B (also works in background mode).
  **新增：拖曳**——在 A 點「按下」、延遲、再在 B 點「彈起」即可由 A 拖曳至 B（背景執行亦適用）。
- Hold / Down / Up is now a drop-down menu; choosing "Up" without an earlier "Down" shows a reminder. "Release" is renamed **Up**.
  按住／按下／彈起改為下拉式選單；如前面沒有「按下」而選擇「彈起」會顯示提醒。「放開」改名為**彈起**。
- Fixed: with several windows of the same program open, picking one no longer selects all of them.
  修正：同一程式開啟多個視窗時，選擇其中一個不會再全部被選取。

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
| `src/` | Source code 原始碼：`App.cs` main window, home page, title bar 主視窗、首頁及標題列；`UI.cs` dark theme and controls 深色主題及控件；`Common.cs` input, screen capture, image matching, hotkeys 輸入、截圖、圖像比對及快捷鍵；`Scripts.cs` script model, editor and runner 腳本資料、編輯器及執行器；`Ai.cs` AI platforms and AI Assistant page AI 平台及 AI 助手頁面；`AiAssist.cs` script AI helper 腳本 AI 助手；`LocalAi.cs` local AI 本地 AI；`PcActions.cs` time / open apps & websites 報時及開啟程式與網頁；`Voice.cs`, `VoiceEngine.cs`, `Whisper.cs` voice chat and speech recognition 語音對話及語音辨識；`Lang.cs`, `LangTable.cs` interface languages 介面語言；`Updater.cs` auto update 自動更新；`Extensions.cs` Tool library 工具庫；`Video.cs` screen recorder 錄影工具；`P2P.cs` Script library 腳本庫；`Home.cs` home page 首頁；`Hotkeys.cs` hotkey table 快捷鍵表；`Replay.cs` Instant Replay 精彩重播；`Tools.cs` Key Remap, Window Tools, Clipboard, Text Recognition, Converter 遊戲改鍵、視窗工具、剪貼簿、文字辨識、轉檔；`GameServer.cs` Game Servers 遊戲伺服器；`Downloader.cs` Video Downloader 影片下載；`RemoteDesk.cs` Remote Desktop 遠端桌面；`PerfMon.cs` Performance Monitor 效能監看；`DiskTools.cs` Disk Space & Health 磁碟空間及健康；`DocConvert.cs` document converter 文件轉檔 |
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
python tools/make_exe.py Build/MultiTool.exe MultiTool.dll Build/MultiTool.exe.new tools/apphost.exe
```

`make_exe.py` uses the previous `Build/MultiTool.exe` as its base (runtime), so keep that file. The optional last argument `tools/apphost.exe` is the app host with the MultiTool icon.
`make_exe.py` 以上一版 `Build/MultiTool.exe` 作為基礎（執行環境），因此請保留該檔案。最後一個可選參數 `tools/apphost.exe` 為已加入 MultiTool 圖示的應用程式主機。

The icon is in `assets/` (`icon.png`, `MultiTool.ico`). To change it, edit and run `tools/make_icon.py`, then `python tools/seticon.py tools/apphost.exe assets/MultiTool.ico tools/apphost.exe` (requires `pip install lief`).
圖示位於 `assets/`（`icon.png`、`MultiTool.ico`）。如需修改，可編輯並執行 `tools/make_icon.py`，再執行上述 `seticon.py` 指令（需要 `pip install lief`）。

---

## Releasing a new version 發佈新版本

1. Update `Version` in `src/App.cs` (e.g. `v1.4`).
   更新 `src/App.cs` 中的 `Version`（例如 `v1.4`）。
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
- [FFmpeg](https://ffmpeg.org/) (essentials build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/)) — GPL, used by the recorder, Instant Replay, Converter and Video Downloader as a separate program / GPL 授權，錄影工具、精彩重播、轉檔工具及影片下載以獨立程式方式調用
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) — Unlicense, downloaded by Video Downloader / 由影片下載工具下載
- [SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD) — Valve's command-line Steam client, downloaded by Game Servers; dedicated servers are downloaded from Steam under each game's own terms / Valve 官方命令列 Steam 用戶端，由遊戲伺服器下載；各專用伺服器從 Steam 下載，受各遊戲條款約束
- [Qwen3.5](https://huggingface.co/Qwen) models (GGUF by [unsloth](https://huggingface.co/unsloth)) — see each model's license / 請參閱各模型的授權

---

## License 授權

© 白兔糖v3v. Licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE).
© 白兔糖v3v。本軟件依據 [PolyForm Noncommercial License 1.0.0](LICENSE) 授權。

Free to use, modify and share for non-commercial purposes. **Resale and any commercial use are prohibited.**
可免費作非商業用途使用、修改及分享。**嚴禁轉售及任何商業用途。**
