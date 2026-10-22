using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.Tools;
using MultiTool.UI;

namespace MultiTool.Video;

/// <summary>精彩重播設定（data\replay.txt）。</summary>
internal static class ReplaySettings
{
    private static string File_ => Path.Combine(AppPaths.Data, "replay.txt");
    public static int Seconds = 30, Monitor, Fps = 60, Res = 1080, Quality = 1, SysVol = 100, MicVol = 100;
    public static bool SysAudio = true, Mic, Cursor = true, AutoStart = true;
    public static string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MultiTool", "Replays");

    static ReplaySettings()
    {
        try
        {
            if (!File.Exists(File_)) return;
            foreach (var line in File.ReadAllLines(File_))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line[..eq], v = line[(eq + 1)..];
                int I(int d) => int.TryParse(v, out var x) ? x : d;
                switch (k)
                {
                    case "seconds": Seconds = Math.Clamp(I(30), 10, 600); break;
                    case "monitor": Monitor = I(0); break;
                    case "fps": Fps = Math.Clamp(I(60), 10, 240); break;
                    case "res": Res = I(1080); break;
                    case "quality": Quality = Math.Clamp(I(1), 0, 3); break;
                    case "sysvol": SysVol = Math.Clamp(I(100), 0, 300); break;
                    case "micvol": MicVol = Math.Clamp(I(100), 0, 300); break;
                    case "sys": SysAudio = v == "1"; break;
                    case "mic": Mic = v == "1"; break;
                    case "cursor": Cursor = v != "0"; break;
                    case "auto": AutoStart = v != "0"; break;
                    case "folder": if (v.Length > 0) Folder = v; break;
                }
            }
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllLines(File_, new[]
            {
                "seconds=" + Seconds, "monitor=" + Monitor, "fps=" + Fps, "res=" + Res, "quality=" + Quality, "sysvol=" + SysVol,
                "micvol=" + MicVol, "sys=" + (SysAudio ? 1 : 0), "mic=" + (Mic ? 1 : 0), "cursor=" + (Cursor ? 1 : 0),
                "auto=" + (AutoStart ? 1 : 0), "folder=" + Folder
            });
        }
        catch { }
    }

    public static RecOpts Opts() => new()
    {
        Mode = CaptureMode.Screen, Monitor = Monitor, Fps = Fps, Res = Res, Quality = Quality, SysAudio = SysAudio, Mic = Mic,
        SysVol = SysVol, MicVol = MicVol, Cursor = Cursor, Codec = "h264", Hw = true, Format = "mp4", Folder = Folder,
        ReplayDir = ReplayEngine.TempDir, ReplaySeconds = Seconds
    };
}

/// <summary>精彩重播：背景一直錄住（循環覆蓋），撳快捷鍵就將最近 N 秒存做 MP4。</summary>
internal static class ReplayEngine
{
    public static string TempDir => Path.Combine(Path.GetTempPath(), "MultiTool-replay");
    private static RecordSession? _session;
    private static bool _busy;
    public static event Action? Changed;
    public static string Status = "";
    public static string? LastFile;

    public static bool Running => _session is { Running: true };

    static ReplayEngine()
    {
        Extensions.Changed += () => { if (!Extensions.IsInstalled(Extensions.Replay)) _ = StopAsync(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _session?.StopAsync().Wait(3000); } catch { } };
    }

    private static void Set(string status) { Status = status; Changed?.Invoke(); }

    public static async Task StartAsync()
    {
        if (_busy || Running) return;
        if (!Ffmpeg.Ready) { Set(Lang.T("未下載 ffmpeg：請去「工具庫」重新安裝精彩重播。")); return; }
        _busy = true;
        try
        {
            try { if (Directory.Exists(TempDir)) foreach (var f in Directory.GetFiles(TempDir)) File.Delete(f); } catch { }
            var s = new RecordSession(ReplaySettings.Opts());
            Set(Lang.T("準備緊…"));
            await s.StartAsync();
            _session = s;
            Set(Lang.F("背景錄緊（保留最近 {0} 秒）", ReplaySettings.Seconds));
        }
        catch (Exception ex) { Set(Lang.T("開始唔到：") + ex.Message); }
        finally { _busy = false; Changed?.Invoke(); }
    }

    public static async Task StopAsync()
    {
        var s = _session;
        _session = null;
        if (s != null) await s.StopAsync();
        Set(Lang.T("已停止"));
    }

    public static async Task ToggleAsync()
    {
        if (Running) { await StopAsync(); Toast.Show(Lang.T("精彩重播：已停止"), Ui.TextDim); }
        else
        {
            await StartAsync();
            if (Running) Toast.Show(Lang.T("精彩重播：開始背景錄影"), Ui.Success);
        }
    }

    /// <summary>設定改咗：行緊就重新開始。</summary>
    public static async Task RestartIfRunningAsync()
    {
        if (!Running) return;
        await StopAsync();
        await StartAsync();
    }

    /// <summary>將最近 N 秒存做 MP4。</summary>
    public static async Task SaveAsync()
    {
        if (!Running) { Toast.Show(Lang.T("精彩重播未開始（背景錄影關咗）"), Ui.Danger); return; }
        if (_busy) return;
        _busy = true;
        string snap = Path.Combine(Path.GetTempPath(), "MultiTool-replay-save");
        try
        {
            Set(Lang.T("儲存緊重播…"));
            // 攞最近嗰幾段（最新嗰段可能未寫完，都照用）
            var segs = new DirectoryInfo(TempDir).GetFiles("rp*.ts").Where(f => f.Length > 0)
                .OrderBy(f => f.LastWriteTimeUtc).ToList();
            int need = (int)Math.Ceiling(ReplaySettings.Seconds / (double)RecordSession.ReplaySeg) + 1;
            segs = segs.Skip(Math.Max(0, segs.Count - need)).ToList();
            if (segs.Count == 0) throw new InvalidOperationException(Lang.T("未錄到嘢，等多陣再試"));
            // 複製一份先（費事駁緊時被覆蓋）
            if (Directory.Exists(snap)) Directory.Delete(snap, true);
            Directory.CreateDirectory(snap);
            var list = new StringBuilder();
            for (int i = 0; i < segs.Count; i++)
            {
                var dst = Path.Combine(snap, $"{i:000}.ts");
                using (var src = new FileStream(segs[i].FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var o = File.Create(dst))
                    await src.CopyToAsync(o);
                list.AppendLine($"file '{dst.Replace("'", "'\\''")}'");
            }
            var listFile = Path.Combine(snap, "list.txt");
            await File.WriteAllTextAsync(listFile, list.ToString());
            Directory.CreateDirectory(ReplaySettings.Folder);
            string outFile = TUi.Unique(Path.Combine(ReplaySettings.Folder, $"Replay {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4"));
            var psi = new ProcessStartInfo(Ffmpeg.Exe,
                $"-hide_banner -loglevel error -y -f concat -safe 0 -i \"{listFile}\" -c copy -bsf:a aac_adtstoasc -movflags +faststart \"{outFile}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0 || !File.Exists(outFile)) throw new InvalidOperationException((await err).Trim());
            LastFile = outFile;
            Set(Lang.T("已儲存：") + Path.GetFileName(outFile));
            Toast.Show(Lang.F("已儲存最近 {0} 秒重播", ReplaySettings.Seconds), Ui.Success);
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        }
        catch (Exception ex)
        {
            AppPaths.LogError("精彩重播", ex);
            Set(Lang.T("儲存失敗：") + ex.Message);
            Toast.Show(Lang.T("精彩重播儲存失敗"), Ui.Danger);
        }
        finally
        {
            _busy = false;
            try { if (Directory.Exists(snap)) Directory.Delete(snap, true); } catch { }
        }
    }

    /// <summary>ffmpeg 自己停咗（例如出錯）：報返出嚟。</summary>
    public static void Check()
    {
        if (_session != null && !_session.Running && !_busy)
        {
            var tail = _session.LogTail();
            _session = null;
            Set(Lang.T("背景錄影中斷：") + tail);
        }
    }
}

internal sealed class ReplayPage : Panel
{
    private readonly CardStack _stack = new();
    private readonly FancyButton _toggle, _save, _len, _mon, _fps, _res, _qual, _folderBtn;
    private readonly Label _status, _folderLabel;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    public static Control Build() => new ReplayPage();

    private ReplayPage()
    {
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        _stack.Dock = DockStyle.Fill;
        HotkeyHub.Get("replay.save").Action = () => _ = ReplayEngine.SaveAsync();
        HotkeyHub.Get("replay.toggle").Action = () => _ = ReplayEngine.ToggleAsync();

        _toggle = TUi.Btn("", () => _ = ReplayEngine.ToggleAsync(), 170);
        _save = TUi.Btn(Lang.T("儲存重播"), () => _ = ReplayEngine.SaveAsync(), 130, BtnKind.Primary);
        _status = TUi.L("");
        var play = TUi.Btn(Lang.T("▶ 播放最新"), () => { if (ReplayEngine.LastFile != null) TUi.Open(ReplayEngine.LastFile); });
        var card = new CardPanel
        {
            Title = Lang.T("精彩重播"),
            Hint = Lang.T("背景一直錄住最近一段時間，撳快捷鍵就將嗰段存低（好似 ShadowPlay）。冇撳就唔會儲存任何嘢。")
        };
        card.SetContent(TUi.Col(
            TUi.Row(_toggle, _save, play),
            TUi.Row(_status),
            TUi.Row(TUi.L(Lang.T("儲存重播快捷鍵")), HotkeyHub.Box("replay.save", 140), TUi.L(Lang.T("開 / 關")), HotkeyHub.Box("replay.toggle", 140)),
            TUi.Row(TUi.Check(Lang.T("開 MultiTool 時自動開始背景錄影"), ReplaySettings.AutoStart, v => { ReplaySettings.AutoStart = v; ReplaySettings.Save(); }))));
        _stack.AddRow(card);

        _len = TUi.Btn("", () => TUi.Pick(_len!, new[] { 15, 30, 60, 120, 300 }.Select(s => (Len(s), s)), ReplaySettings.Seconds, v => Change(() => ReplaySettings.Seconds = v)), 130);
        _mon = TUi.Btn("", () => TUi.Pick(_mon!, Screen.AllScreens.Select((s, i) => (Lang.F("螢幕 {0}", i + 1) + $"  ({s.Bounds.Width}×{s.Bounds.Height})", i)),
            ReplaySettings.Monitor, v => Change(() => ReplaySettings.Monitor = v)), 130);
        _fps = TUi.Btn("", () => TUi.Pick(_fps!, new[] { 30, 60, 120 }.Select(f => (f + " FPS", f)), ReplaySettings.Fps, v => Change(() => ReplaySettings.Fps = v)), 100);
        _res = TUi.Btn("", () => TUi.Pick(_res!, RecSettings.ResList.Select(r => (RecSettings.ResName(r), r)), ReplaySettings.Res, v => Change(() => ReplaySettings.Res = v),
            Lang.T("比螢幕大嘅解像度唔會放大")), 140);
        _qual = TUi.Btn("", () => TUi.Pick(_qual!, new[] { 0, 1, 2, 3 }.Select(q => (RecSettings.QualityName(q), q)), ReplaySettings.Quality, v => Change(() => ReplaySettings.Quality = v)), 110);
        var sys = TUi.Check(Lang.T("電腦聲音"), ReplaySettings.SysAudio, v => Change(() => ReplaySettings.SysAudio = v));
        var mic = TUi.Check(Lang.T("咪高峰"), ReplaySettings.Mic, v => Change(() => ReplaySettings.Mic = v));
        var cursor = TUi.Check(Lang.T("錄製鼠標"), ReplaySettings.Cursor, v => Change(() => ReplaySettings.Cursor = v));
        sys.Margin = mic.Margin = new Padding(0, Ui.S(8), Ui.S(6), 0);
        var sysVol = TUi.Percent(ReplaySettings.SysVol, 0, 300, 10, v => Change(() => ReplaySettings.SysVol = v));
        var micVol = TUi.Percent(ReplaySettings.MicVol, 0, 300, 10, v => Change(() => ReplaySettings.MicVol = v));
        _folderLabel = TUi.L("", Ui.Text);
        _folderBtn = TUi.Btn(Lang.T("更改…"), () =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = ReplaySettings.Folder };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            ReplaySettings.Folder = dlg.SelectedPath;
            ReplaySettings.Save();
            UpdateLabels();
        }, 80);
        var openFolder = TUi.Btn(Lang.T("開啟資料夾"), () => TUi.Open(ReplaySettings.Folder, folder: true));
        var set = new CardPanel { Title = Lang.T("設定"), Hint = Lang.T("改設定時如果背景錄緊，會自動重新開始。錄全個螢幕，用顯示卡 H.264 編碼。") };
        set.SetContent(TUi.Col(
            TUi.Row(TUi.L(Lang.T("長度")), _len, TUi.L(Lang.T("螢幕")), _mon),
            TUi.Row(TUi.L(Lang.T("畫面")), _fps, _res, _qual, cursor),
            TUi.Row(TUi.L(Lang.T("聲音")), sys, sysVol, mic, micVol),
            TUi.Row(TUi.L(Lang.T("儲存位置")), _folderLabel, _folderBtn, openFolder)));
        _stack.AddRow(set);
        Controls.Add(_stack);

        ReplayEngine.Changed += OnChanged;
        Disposed += (_, _) => ReplayEngine.Changed -= OnChanged;
        _timer.Tick += (_, _) => ReplayEngine.Check();
        _timer.Start();
        UpdateLabels();
        if (ReplaySettings.AutoStart && Extensions.IsInstalled(Extensions.Replay) && !ReplayEngine.Running)
            BeginInvokeWhenReady(() => _ = ReplayEngine.StartAsync());
    }

    private void BeginInvokeWhenReady(Action a)
    {
        if (IsHandleCreated) BeginInvoke(a);
        else HandleCreated += (_, _) => BeginInvoke(a);
    }

    private static string Len(int s) => s < 60 ? Lang.F("{0} 秒", s) : Lang.F("{0} 分鐘", s / 60);

    private void Change(Action set)
    {
        set();
        ReplaySettings.Save();
        UpdateLabels();
        _ = ReplayEngine.RestartIfRunningAsync();
    }

    private void OnChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(UpdateLabels); else UpdateLabels();
    }

    private void UpdateLabels()
    {
        bool on = ReplayEngine.Running;
        _toggle.Text = on ? Lang.T("● 背景錄緊（撳停）") : Lang.T("○ 開始背景錄影");
        _toggle.Kind = on ? BtnKind.Danger : BtnKind.Success;
        _status.Text = ReplayEngine.Status;
        _len.Text = Lang.T("最近 ") + Len(ReplaySettings.Seconds) + " ▾";
        _mon.Text = Lang.F("螢幕 {0}", ReplaySettings.Monitor + 1) + " ▾";
        _fps.Text = ReplaySettings.Fps + " FPS ▾";
        _res.Text = Lang.T("解像度：") + RecSettings.ResName(ReplaySettings.Res) + " ▾";
        _qual.Text = Lang.T("品質：") + RecSettings.QualityName(ReplaySettings.Quality) + " ▾";
        _folderLabel.Text = ReplaySettings.Folder;
        foreach (var b in new[] { _toggle, _save, _len, _mon, _fps, _res, _qual }) b.Invalidate();
    }
}
