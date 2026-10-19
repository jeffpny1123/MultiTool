# MultiTool

A lightweight Windows automation tool (.NET 9 WinForms, single-file `.exe`) — build your own macros like a key/mouse recorder, with screen image matching, loops, conditions and background execution.

Developed by **白兔糖v3v**. UI language: Traditional Chinese (Cantonese).

## Features
- **My Scripts** — build automation scripts step by step
  - **Key press** — any key or combination (e.g. `Shift + Alt + T`); hold for N seconds, press down only, or release only
  - **Mouse click** — left / right button at a screen position (with a 3-second "pick position" helper)
  - **Wait** — pause for N seconds
  - **Find image & click** — snip a region of the screen (up to 6 images per step); clicks the best match above the similarity threshold
  - **Loop / Break loop** — repeat steps N times (0 = forever), break out early
  - **IF conditions** — image found / image not found / pixel color at a point / clock time / countdown; can be nested
  - **IF blocks running in parallel** with the main thread, continuously watching their condition
  - Run **once** or **forever**, with a per-script **hotkey** to start / stop
  - **Background mode** — capture, click and type only inside a chosen program window, so you can switch to other windows while it runs (not every game accepts background input)
  - Validates scripts before running (missing images, unset keys, empty loops…)
- **Auto update** — checks GitHub Releases on startup and updates itself in one click
- Dark custom UI with its own title bar

## Download
Get the latest `MultiTool.exe` (or zip) from **Releases**, unzip and run — no .NET installation required.

Scripts, screenshots and settings are stored in the `data/` folder next to the exe.

> If the target game runs as Administrator, run MultiTool as Administrator too, otherwise Windows blocks the simulated input.

## Repository layout
| Path | Purpose |
|---|---|
| `src/` | Source code — `App.cs` (main window, home, title bar), `UI.cs` (dark theme & controls), `Common.cs` (input, screen capture & template matching, hotkeys), `Scripts.cs` (script model, editor, runner), `Updater.cs` (auto update) |
| `tools/` | Packaging tools (`make_exe.py`, `bundle.py`) |
| `template/` | Example script shipped to users ("自動點擊" / auto-click) |
| `release/` | User guide shipped with releases (`使用說明.txt`) |
| `Build/` | Local build output (git-ignored) |

## Building
No Windows Desktop SDK needed — compile with Roslyn `csc` against the framework assemblies extracted from an existing .NET 9 WinForms single-file exe, then repack:
```
python tools/bundle.py Build/MultiTool.exe ref/
csc -nostdlib -noconfig -target:winexe -langversion:12 -nullable:enable -optimize+ -out:MultiTool.dll -r:ref/*.dll src/*.cs
python tools/make_exe.py Build/MultiTool.exe MultiTool.dll Build/MultiTool.exe.new
```
`make_exe.py` uses the previous `Build/MultiTool.exe` as the base (for its app host and runtime), so keep that file.

## Releasing a new version
1. Bump `Version` in `src/App.cs` (e.g. `v1.1`).
2. Build `Build/MultiTool.exe`.
3. Create a GitHub Release whose **tag matches the version** (`v1.1`), attach `MultiTool.exe` or a zip containing it, and mark it as **latest**.
4. Existing users get an update prompt the next time they launch the app.

## License
© 白兔糖v3v — licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE).
Free to use, modify and share for non-commercial purposes. **Reselling or any commercial use is prohibited.**
