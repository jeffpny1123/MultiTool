# 多功能工具 (MultiTool)

自用 Windows 小工具（.NET 9 WinForms，單一 exe）。開發者：**白兔糖v3v**

## 功能
- **我的腳本**：好似按鍵精靈咁自己砌腳本
  - 按鍵（可組合鍵；撳住 / 按下 / 彈起）、滑鼠點擊、等待
  - 截圖搵圖點擊（每步最多 6 張圖，搵最似嗰張）
  - 循環 / 中斷循環、IF 條件（搵到圖 / 搵唔到圖 / 某點顏色 / 時間 / 倒數），可以一層層 nest
  - IF 框（同主線程同時行）、「行一次 / 一直行」、每個腳本自己嘅快捷鍵
  - 背景執行：只喺指定程式視窗入面截圖 / 點擊 / 按鍵
- 開程式自動檢查 GitHub Release 更新

## 下載
去 [Releases](https://github.com/jeffpny1123/MultiTool/releases) 下載最新嘅 `MultiTool.exe`（或者 zip），直接開就用得。

## 資料夾
| 路徑 | 用途 |
|---|---|
| `src/` | 原始碼：`App.cs` 主視窗 / 首頁、`UI.cs` 深色控件、`Common.cs` 輸入 / 截圖配對、`Scripts.cs` 腳本、`Updater.cs` 自動更新 |
| `tools/` | 打包工具（`make_exe.py`、`bundle.py`） |
| `template/` | 派俾人用嘅範例腳本（「自動點擊」） |
| `release/` | 派俾人用嘅 `使用說明.txt` |
| `Build/` | 本機打包輸出（唔入 git） |

運行時資料（腳本、截圖、設定）存喺 exe 隔籬嘅 `data/`，唔會上傳。

## 打包
冇 Windows Desktop SDK 都得：用 Roslyn `csc` reference 一個 .NET 9 WinForms single-file exe 入面解出嚟嘅 framework dll：
```
python tools/bundle.py Build/MultiTool.exe ref/
csc -nostdlib -noconfig -target:winexe -langversion:12 -nullable:enable -optimize+ -out:MultiTool.dll -r:ref/*.dll src/*.cs
python tools/make_exe.py Build/MultiTool.exe MultiTool.dll Build/MultiTool.exe.new
```
（`make_exe.py` 用上一版 exe 做底本，所以 `Build/MultiTool.exe` 唔好刪。）

## 出新版
1. 改 `src/App.cs` 入面嘅 `Version`（例如 `v1.1`）。
2. 打包出 `Build/MultiTool.exe`。
3. GitHub → Releases → Draft a new release：Tag 填同一個版本號（`v1.1`），上載 `MultiTool.exe`，寫更新內容，Publish。
4. 用緊舊版嘅人開程式就會收到更新提示。

## 授權
© 白兔糖v3v — [PolyForm Noncommercial License 1.0.0](LICENSE)：可以免費使用、修改、分享，**禁止轉售或任何商業用途**。
