using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using MultiTool.UI;

namespace MultiTool.Tools;

// =====================================================================
//  Flash 小遊戲：加入 .swf 檔，用 Ruffle（開源 Flash 模擬器，MIT / Apache 2.0）喺 WebView2 入面直接玩
//  • 遊戲放喺 data\flash-games\*.swf；顯示名稱存喺 names.json
//  • Ruffle 第一次用先由 GitHub 下載（約 10 MB），放喺 data\flash-games\.player\
//  • 成個資料夾 map 做 https://flash.rabi/（同一個網域，Ruffle 讀 swf 唔會撞 CORS）
// =====================================================================
internal static class FlashStore
{
    // 固定一個測試過嘅 Ruffle 版本（nightly），唔好每次攞最新
    private const string RuffleTag = "nightly-2026-11-10";
    private const string RuffleUrl = "https://github.com/ruffle-rs/ruffle/releases/download/" + RuffleTag + "/ruffle-nightly-2026_11_10-web-selfhosted.zip";

    public static string Dir => Path.Combine(AppPaths.Data, "flash-games");
    public static string PlayerDir => Path.Combine(Dir, ".player");
    private static string VerFile => Path.Combine(PlayerDir, "version.txt");
    private static string NamesFile => Path.Combine(Dir, "names.json");

    public static bool RuffleReady
    {
        get { try { return File.Exists(Path.Combine(PlayerDir, "ruffle.js")) && File.ReadAllText(VerFile).Trim() == RuffleTag; } catch { return false; } }
    }

    /// <summary>全部遊戲（檔名 → 顯示名稱），新加嘅排最前。</summary>
    public static List<(string File, string Name)> Games()
    {
        var names = LoadNames();
        if (!Directory.Exists(Dir)) return new();
        return new DirectoryInfo(Dir).GetFiles("*.swf").OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => (f.Name, names.TryGetValue(f.Name, out var n) && n.Length > 0 ? n : NiceName(f.Name))).ToList();
    }

    /// <summary>「age-of-war-616.swf」→「Age Of War」</summary>
    public static string NiceName(string file)
    {
        string s = Path.GetFileNameWithoutExtension(file).Replace('_', ' ').Replace('-', ' ');
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+\d+$", "").Trim();
        return string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpper(w[0]) + w[1..]));
    }

    private static Dictionary<string, string> LoadNames()
    {
        try { if (File.Exists(NamesFile)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(NamesFile)) ?? new(); } catch { }
        return new();
    }

    public static void Rename(string file, string name)
    {
        var n = LoadNames(); n[file] = name.Trim();
        try { File.WriteAllText(NamesFile, JsonSerializer.Serialize(n)); } catch (Exception ex) { AppPaths.LogError("Flash 改名", ex); }
    }

    /// <summary>加入 .swf（複製入遊戲資料夾），回傳檔名。</summary>
    public static string Add(string path)
    {
        Directory.CreateDirectory(Dir);
        var head = new byte[3];
        using (var fs = File.OpenRead(path)) fs.ReadExactly(head);
        string sig = Encoding.ASCII.GetString(head);
        if (sig is not ("FWS" or "CWS" or "ZWS")) throw new InvalidDataException(Lang.F("「{0}」唔係 Flash 遊戲檔（.swf）。", Path.GetFileName(path)));
        string name = Path.GetFileName(path), dest = Path.Combine(Dir, name);
        for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(Dir, Path.GetFileNameWithoutExtension(name) + "-" + i + ".swf");
        File.Copy(path, dest);
        return Path.GetFileName(dest);
    }

    public static void Delete(string file)
    {
        try { File.Delete(Path.Combine(Dir, file)); } catch (Exception ex) { AppPaths.LogError("刪除 Flash 遊戲", ex); }
        var n = LoadNames(); if (n.Remove(file)) try { File.WriteAllText(NamesFile, JsonSerializer.Serialize(n)); } catch { }
        try { File.Delete(CoverPath(file)); } catch { }
    }

    // ---- 封面：.swf 本身冇封面圖，所以玩嘅時候自動影一張（或者用家自己揀圖）----
    public static string CoverDir => Path.Combine(Dir, ".covers");
    public static string CoverPath(string file) => Path.Combine(CoverDir, file + ".png");
    public static bool HasCover(string file) => File.Exists(CoverPath(file));

    /// <summary>讀封面（唔鎖住個檔）；冇就 null。</summary>
    public static Image? LoadCover(string file)
    {
        try
        {
            if (!HasCover(file)) return null;
            using var fs = File.OpenRead(CoverPath(file));
            using var img = Image.FromStream(fs);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    /// <summary>用家揀嘅圖片做封面（統一轉做 PNG，最闊 480）。</summary>
    public static void SetCover(string file, string imagePath)
    {
        Directory.CreateDirectory(CoverDir);
        using var src = Image.FromFile(imagePath);
        double k = Math.Min(1, 480.0 / src.Width);
        using var bmp = new Bitmap(src, Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
        bmp.Save(CoverPath(file), System.Drawing.Imaging.ImageFormat.Png);
    }

    // ---- 設定：方格 / 清單、音量 ----
    private static string SettingsFile => Path.Combine(Dir, "settings.json");
    public sealed class Prefs { public bool Grid { get; set; } = true; public int Volume { get; set; } = 80; }
    public static Prefs Settings { get; } = LoadPrefs();
    private static Prefs LoadPrefs()
    {
        try { if (File.Exists(SettingsFile)) return JsonSerializer.Deserialize<Prefs>(File.ReadAllText(SettingsFile)) ?? new(); } catch { }
        return new();
    }
    public static void SavePrefs() { try { Directory.CreateDirectory(Dir); File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Settings)); } catch { } }

    /// <summary>下載 Ruffle，再寫播放頁。</summary>
    public static async Task InstallRuffleAsync(Action<double> progress)
    {
        Directory.CreateDirectory(PlayerDir);
        string zip = Path.Combine(Dir, "ruffle.zip");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Program.FileName);
            using var resp = await http.GetAsync(RuffleUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(zip))
            {
                var buf = new byte[1 << 16]; int n;
                while ((n = await src.ReadAsync(buf)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n)); done += n; if (total > 0) progress((double)done / total); }
            }
        }
        foreach (var f in Directory.GetFiles(PlayerDir)) try { File.Delete(f); } catch { }
        using (var z = ZipFile.OpenRead(zip))
            foreach (var e in z.Entries)
                if (e.Name.Length > 0 && !e.FullName.Contains("..") && !e.Name.EndsWith(".map")) e.ExtractToFile(Path.Combine(PlayerDir, e.Name), true);
        File.Delete(zip);
        WritePlayer();
        File.WriteAllText(VerFile, RuffleTag);
    }

    /// <summary>播放頁：成頁都係遊戲；?swf=檔名</summary>
    public static void WritePlayer() => File.WriteAllText(Path.Combine(PlayerDir, "player.html"), """
<!doctype html>
<html><head><meta charset="utf-8">
<style>html,body{margin:0;height:100%;background:#000;overflow:hidden;color:#ccc;font:14px "Segoe UI",sans-serif}#p{width:100%;height:100%}ruffle-player{width:100%;height:100%;display:block}</style>
<script>
window.RufflePlayer = window.RufflePlayer || {};
window.RufflePlayer.config = { publicPath: "/.player/", autoplay: "on", unmuteOverlay: "hidden", splashScreen: false, letterbox: "on",
  polyfills: false, warnOnUnsupportedContent: false, contextMenu: "rightClickOnly", allowScriptAccess: false, upgradeToHttps: false };
</script>
<script src="/.player/ruffle.js"></script>
</head><body><div id="p"></div>
<script>
const q = new URLSearchParams(location.search), swf = q.get("swf");
const player = window.RufflePlayer.newest().createPlayer();
document.getElementById("p").appendChild(player);
const api = player.ruffle ? player.ruffle() : player;
// 音量 0 至 1（拉比工具箱個音量掣會叫 setVol）
window.setVol = v => { try { api.volume = v; } catch (e) { } try { player.volume = v; } catch (e) { } };
api.load({ url: "/" + encodeURIComponent(swf) }).then(() => setVol(parseFloat(q.get("vol") || "0.8"))).catch(e => { document.body.innerText = "Ruffle: " + e; });
</script>
</body></html>
""");
}

/// <summary>WebView2 播放器（WebView2 類型放喺呢度，等 WebView2Lib.Prepare 之後先載入）。</summary>
internal sealed class FlashWeb
{
    private CoreWebView2Controller _ctl = null!;

    public static async Task<FlashWeb> CreateAsync(Control host)
    {
        string data = Path.Combine(AppPaths.Data, "webview", "flash");
        Directory.CreateDirectory(data);
        // 自己一個瀏覽器資料夾：可以設定遊戲聲音唔使先撳一下
        var env = await CoreWebView2Environment.CreateAsync(null, data, new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
        var w = new FlashWeb { _ctl = await env.CreateCoreWebView2ControllerAsync(host.Handle) };
        w._ctl.DefaultBackgroundColor = Color.Black;
        w._ctl.Bounds = host.ClientRectangle;
        host.Resize += (_, _) => { try { w._ctl.Bounds = host.ClientRectangle; } catch { } };
        var wv = w._ctl.CoreWebView2;
        wv.Settings.AreDevToolsEnabled = false;
        wv.Settings.IsStatusBarEnabled = false;
        wv.Settings.AreDefaultContextMenusEnabled = false;
        wv.SetVirtualHostNameToFolderMapping("flash.rabi", FlashStore.Dir, CoreWebView2HostResourceAccessKind.Deny);
        // 遊戲入面撳連結：用預設瀏覽器開，唔好喺播放器入面轉頁
        wv.NewWindowRequested += (_, e) => { e.Handled = true; try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { } };
        wv.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://flash.rabi/", StringComparison.OrdinalIgnoreCase) && e.Uri != "about:blank") e.Cancel = true; };
        return w;
    }

    public void Play(string file, int volume) { try { _ctl.CoreWebView2.Navigate("https://flash.rabi/.player/player.html?swf=" + Uri.EscapeDataString(file) + "&vol=" + (volume / 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture) + "&t=" + DateTime.Now.Ticks); } catch { } }
    public void SetVolume(int volume) { try { _ = _ctl.CoreWebView2.ExecuteScriptAsync("window.setVol && setVol(" + (volume / 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"); } catch { } }

    /// <summary>影低而家個畫面做封面（縮細到最闊 480）。</summary>
    public async Task<bool> CaptureAsync(string path)
    {
        try
        {
            using var ms = new MemoryStream();
            await _ctl.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            ms.Position = 0;
            using var src = Image.FromStream(ms);
            double k = Math.Min(1, 480.0 / src.Width);
            using var bmp = new Bitmap(src, Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
            // 成張差唔多全黑（仲載入緊）就唔要
            if (IsBlank(bmp)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return true;
        }
        catch { return false; }
    }

    private static bool IsBlank(Bitmap b)
    {
        int lit = 0, n = 0;
        for (int y = 0; y < b.Height; y += Math.Max(1, b.Height / 24))
            for (int x = 0; x < b.Width; x += Math.Max(1, b.Width / 24))
            {
                var c = b.GetPixel(x, y); n++;
                if (c.R + c.G + c.B > 60) lit++;
            }
        return lit < n / 20;
    }
    public void Stop() { try { _ctl.CoreWebView2.Navigate("about:blank"); } catch { } }
    public bool Muted { get { try { return _ctl.CoreWebView2.IsMuted; } catch { return false; } } set { try { _ctl.CoreWebView2.IsMuted = value; } catch { } } }
    public void Close() { try { _ctl.Close(); } catch { } }
}


/// <summary>「Flash 小遊戲」頁：方格模式（封面一格格）或者清單模式（左清單右遊戲）。</summary>
internal sealed class FlashGamesPage : Panel
{
    private static FlashStore.Prefs P => FlashStore.Settings;

    // 方格模式：成頁遊戲庫
    private readonly Panel _gridView = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private readonly FlowLayoutPanel _tiles = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Ui.Bg, Padding = new Padding(Ui.S(16)) };
    private readonly Label _gridEmpty = new() { AutoSize = false, ForeColor = Ui.TextDim, Font = Ui.F(10.5f), TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, BackColor = Ui.Bg };

    // 玩緊：左清單（清單模式先顯示）+ 右邊遊戲
    private readonly Panel _playView = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private readonly Panel _left = new() { Dock = DockStyle.Left, Width = Ui.S(280), BackColor = Ui.Card, Padding = new Padding(Ui.S(12)) };
    private readonly ListBox _list = new() { BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false };
    private readonly Panel _right = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private readonly Panel _bar = new() { Dock = DockStyle.Top, Height = Ui.S(48), BackColor = Ui.Bg };
    private readonly Label _title = new() { AutoSize = false, ForeColor = Ui.Text, Font = Ui.F(11f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly Label _msg = new() { Dock = DockStyle.Fill, ForeColor = Ui.TextDim, Font = Ui.F(10.5f), TextAlign = ContentAlignment.MiddleCenter, BackColor = Ui.Bg };
    private readonly FancyButton _setup, _restart, _mute, _wide, _back, _shot;
    private readonly VolumeSlider _vol = new();
    private readonly ProgressBar _dl = new() { Visible = false };
    private object? _web;   // FlashWeb
    private string? _playing;
    private int _playSeq;
    private List<(string File, string Name)> _games = new();

    public static Control Build() => new FlashGamesPage();

    private FlashGamesPage()
    {
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;

        // ---- 方格模式 ----
        var gTop = new Panel { Dock = DockStyle.Top, Height = Ui.S(56), BackColor = Ui.Bg };
        var gTitle = new Label { Text = Lang.T("Flash 小遊戲"), AutoSize = true, ForeColor = Ui.Text, Font = Ui.F(13f, FontStyle.Bold), Location = new Point(Ui.S(20), Ui.S(16)) };
        var gAdd = Btn(Lang.T("＋ 加遊戲（.swf）"), BtnKind.Primary, AddPick);
        var gMode = Btn(Lang.T("☰ 清單模式"), BtnKind.Ghost, () => SetGrid(false));
        gTop.Controls.AddRange(new Control[] { gTitle, gAdd, gMode });
        gTop.Resize += (_, _) => { int x = gTop.Width - Ui.S(20); foreach (var b in new[] { gMode, gAdd }) { x -= b.Width; b.Location = new Point(x, Ui.S(11)); x -= Ui.S(8); } };
        _gridView.Controls.Add(_tiles);
        _gridView.Controls.Add(_gridEmpty);
        _gridView.Controls.Add(gTop);

        // ---- 清單（左）----
        var head = new Label { Text = Lang.T("Flash 小遊戲"), Dock = DockStyle.Top, Height = Ui.S(30), ForeColor = Ui.Text, Font = Ui.F(12f, FontStyle.Bold) };
        var add = new FancyButton { Text = Lang.T("＋ 加遊戲（.swf）"), Kind = BtnKind.Primary, Font = Ui.F(9.75f), Dock = DockStyle.Top, Height = Ui.S(36) };
        add.Click += (_, _) => AddPick();
        var toGrid = new FancyButton { Text = Lang.T("▦ 方格模式"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Dock = DockStyle.Top, Height = Ui.S(32) };
        toGrid.Click += (_, _) => SetGrid(true);
        var hint = new Label { Text = Lang.T("撳一下遊戲就開始玩；右鍵可以改名、換封面或者刪除。亦可以將 .swf 拖入嚟。"), Dock = DockStyle.Bottom, Height = Ui.S(54), ForeColor = Ui.TextFaint, Font = Ui.F(8.75f) };
        _list.Dock = DockStyle.Fill; _list.BackColor = Ui.Card; _list.ForeColor = Ui.Text; _list.Font = Ui.F(10f); _list.ItemHeight = Ui.S(40);
        _list.DrawItem += DrawItem;
        _list.MouseClick += (_, e) => { int i = _list.IndexFromPoint(e.Location); if (i >= 0) Play(_games[i].File); };
        _list.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) { int i = _list.IndexFromPoint(e.Location); if (i >= 0) { _list.SelectedIndex = i; ItemMenu(_games[i], _list, e.Location); } } };
        _left.Controls.Add(_list);
        _left.Controls.Add(new Panel { Dock = DockStyle.Top, Height = Ui.S(8), BackColor = Ui.Card });
        _left.Controls.Add(toGrid);
        _left.Controls.Add(new Panel { Dock = DockStyle.Top, Height = Ui.S(6), BackColor = Ui.Card });
        _left.Controls.Add(add);
        _left.Controls.Add(head);
        _left.Controls.Add(hint);

        // ---- 遊戲（右）----
        _setup = new FancyButton { Text = Lang.T("下載 Flash 模擬器"), Kind = BtnKind.Primary, Font = Ui.F(10f), Visible = false };
        _setup.Click += async (_, _) => await SetupAsync();
        _back = Btn(Lang.T("← 遊戲庫"), BtnKind.Ghost, () => { StopGame(); ShowMode(); });
        _restart = Btn(Lang.T("⟳ 重新開始"), BtnKind.Ghost, () => { if (_playing != null) Play(_playing); });
        _shot = Btn(Lang.T("📷 設為封面"), BtnKind.Ghost, async () => await CaptureCoverAsync(true));
        _mute = Btn("🔊", BtnKind.Ghost, ToggleMute);
        _wide = Btn(Lang.T("⛶ 放大"), BtnKind.Ghost, () => { _left.Visible = !_left.Visible; _wide.Text = _left.Visible ? Lang.T("⛶ 放大") : Lang.T("↩ 還原"); LayoutBar(); });
        _vol.Value = P.Volume;
        _vol.Changed += () => { P.Volume = _vol.Value; FlashStore.SavePrefs(); if (_web != null) VolWeb(_vol.Value); };
        new ToolTip().SetToolTip(_vol, Lang.T("音量"));
        _bar.Controls.AddRange(new Control[] { _back, _title, _shot, _restart, _vol, _mute, _wide });
        _bar.Resize += (_, _) => LayoutBar();
        _host.Controls.Add(_msg);
        _right.Controls.Add(_host);
        _right.Controls.Add(_bar);
        _msg.Controls.Add(_setup);
        _msg.Controls.Add(_dl);
        _msg.Resize += (_, _) => { _setup.SetBounds((_msg.Width - Ui.S(200)) / 2, _msg.Height / 2 + Ui.S(40), Ui.S(200), Ui.S(38)); _dl.SetBounds((_msg.Width - Ui.S(300)) / 2, _msg.Height / 2 + Ui.S(90), Ui.S(300), Ui.S(8)); };
        _playView.Controls.Add(_right);
        _playView.Controls.Add(_left);

        Controls.Add(_playView);
        Controls.Add(_gridView);

        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = Dropped(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => AddFiles(Dropped(e));
        VisibleChanged += (_, _) => { if (!Visible) StopGame(); };   // 轉頁就停（唔好喺背景繼續出聲）
        Disposed += (_, _) => { if (_web != null) CloseWeb(); };
        Reload();
        ShowMode();
    }

    private static FancyButton Btn(string text, BtnKind kind, Action click)
    {
        var b = new FancyButton { Text = text, Kind = kind, Font = Ui.F(9.5f) };
        b.Size = new Size(Math.Max(Ui.S(44), b.GetPreferredSize(Size.Empty).Width), Ui.S(34));
        b.Click += (_, _) => click();
        return b;
    }

    private static string[] Dropped(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] fs ? fs.Where(f => f.EndsWith(".swf", StringComparison.OrdinalIgnoreCase)).ToArray() : Array.Empty<string>();

    private void SetGrid(bool grid) { P.Grid = grid; FlashStore.SavePrefs(); if (grid) StopGame(); ShowMode(); }

    /// <summary>方格模式未玩緊 → 遊戲庫；玩緊或者清單模式 → 遊戲畫面。</summary>
    private void ShowMode()
    {
        bool lib = P.Grid && _playing == null;
        _gridView.Visible = lib;
        _playView.Visible = !lib;
        if (lib) _gridView.BringToFront(); else _playView.BringToFront();
        _left.Visible = !P.Grid && _wide.Text == Lang.T("⛶ 放大");
        _back.Visible = P.Grid;
        _wide.Visible = !P.Grid;
        if (_playing == null) ShowIdle();
        LayoutBar();
    }

    private void LayoutBar()
    {
        int h = _bar.Height, p = Ui.S(8), x = _bar.Width - p;
        foreach (Control b in new Control[] { _wide, _mute, _vol, _restart, _shot })
        {
            if (!b.Visible) continue;
            int w = b == _vol ? Ui.S(110) : b.Width;
            x -= w; b.SetBounds(x, b == _vol ? Ui.S(15) : Ui.S(7), w, b == _vol ? Ui.S(18) : h - Ui.S(14)); x -= p;
        }
        int lx = Ui.S(12);
        if (_back.Visible) { _back.SetBounds(lx, Ui.S(7), _back.Width, h - Ui.S(14)); lx = _back.Right + Ui.S(10); }
        _title.SetBounds(lx, 0, Math.Max(0, x - lx), h);
    }

    private void DrawItem(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _games.Count) return;
        bool playing = _games[e.Index].File == _playing;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(playing ? Ui.Alpha(Ui.Accent, 60) : sel ? Ui.CardHi : Ui.Card)) e.Graphics.FillRectangle(b, e.Bounds);
        TextRenderer.DrawText(e.Graphics, (playing ? "▶ " : "🎮 ") + _games[e.Index].Name, Ui.F(10f, playing ? FontStyle.Bold : FontStyle.Regular),
            new Rectangle(e.Bounds.X + Ui.S(8), e.Bounds.Y, e.Bounds.Width - Ui.S(12), e.Bounds.Height), playing ? Ui.Accent : Ui.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    private void Reload()
    {
        _games = FlashStore.Games();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var g in _games) _list.Items.Add(g.Name);
        _list.EndUpdate();
        // 方格
        _tiles.SuspendLayout();
        foreach (Control c in _tiles.Controls.Cast<Control>().ToList()) c.Dispose();
        _tiles.Controls.Clear();
        foreach (var g in _games)
        {
            var t = new GameTile(g.File, g.Name, FlashStore.LoadCover(g.File));
            t.Click += (_, _) => Play(g.File);
            t.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ItemMenu(g, t, e.Location); };
            _tiles.Controls.Add(t);
        }
        _tiles.ResumeLayout();
        _gridEmpty.Text = !FlashStore.RuffleReady ? Lang.T("第一次用要下載 Flash 模擬器 Ruffle（開源免費，約 10 MB）。撳任何遊戲，或者先加一個遊戲。")
                        : Lang.T("未有遊戲：撳右上角「＋ 加遊戲」揀 .swf 檔，或者直接拖入嚟。");
        _gridEmpty.Visible = _games.Count == 0;
        _tiles.Visible = _games.Count > 0;
    }

    private void ShowIdle()
    {
        _msg.Visible = true; _msg.BringToFront();
        _title.Text = "";
        _restart.Visible = _mute.Visible = _shot.Visible = _vol.Visible = false;
        if (!FlashStore.RuffleReady)
        {
            _msg.Text = Lang.T("第一次用要下載 Flash 模擬器 Ruffle（開源免費，約 10 MB）。");
            _setup.Visible = true;
        }
        else
        {
            _setup.Visible = false;
            _msg.Text = _games.Count == 0 ? Lang.T("未有遊戲：撳左邊「＋ 加遊戲」揀 .swf 檔，或者直接拖入嚟。") : Lang.T("喺左邊揀一個遊戲開始玩。");
        }
        LayoutBar();
    }

    private async Task SetupAsync()
    {
        _setup.Enabled = false; _dl.Visible = true; _dl.Value = 0;
        _msg.Text = Lang.T("下載緊 Ruffle…");
        try
        {
            await FlashStore.InstallRuffleAsync(f => BeginInvoke(() => { if (!IsDisposed) _dl.Value = (int)Math.Clamp(f * 100, 0, 100); }));
            _dl.Visible = false;
            Reload();
            if (_pending != null) { var f = _pending; _pending = null; Play(f); } else ShowIdle();
        }
        catch (Exception ex)
        {
            AppPaths.LogError("下載 Ruffle", ex);
            _msg.Text = Lang.T("下載失敗：") + ex.Message;
            _dl.Visible = false; _setup.Enabled = true;
        }
    }

    private string? _pending;   // 未有 Ruffle 時撳咗嘅遊戲：下載完自動開

    private async void Play(string file)
    {
        if (!FlashStore.RuffleReady)
        {
            _pending = file;
            _gridView.Visible = false; _playView.Visible = true; _playView.BringToFront();
            _back.Visible = P.Grid; ShowIdle();
            return;
        }
        if (!Mail.WebView2Lib.Prepare()) { _msg.Text = Lang.T("要用 Microsoft Edge WebView2 Runtime 先玩到（Windows 10 / 11 通常已經有）。"); return; }
        if (!File.Exists(Path.Combine(FlashStore.PlayerDir, "player.html")) || !File.ReadAllText(Path.Combine(FlashStore.PlayerDir, "player.html")).Contains("setVol")) FlashStore.WritePlayer();
        try
        {
            _playing = file;
            ShowMode();
            _web ??= await CreateWeb();
            _title.Text = _games.FirstOrDefault(g => g.File == file).Name ?? file;
            _restart.Visible = _mute.Visible = _shot.Visible = _vol.Visible = true;
            _msg.Visible = false;
            LayoutBar();
            PlayWeb(file, P.Volume);
            _list.Invalidate();
            // 未有封面：玩咗一陣自動影一張
            int seq = ++_playSeq;
            if (!FlashStore.HasCover(file))
                foreach (int wait in new[] { 12000, 15000, 20000 })
                {
                    await Task.Delay(wait);
                    if (seq != _playSeq || _playing != file || IsDisposed || FlashStore.HasCover(file)) break;
                    if (await CaptureCoverAsync(false)) break;
                }
        }
        catch (Exception ex)
        {
            AppPaths.LogError("開 Flash 遊戲", ex);
            _msg.Visible = true; _msg.BringToFront(); _msg.Text = Lang.T("開唔到遊戲：") + ex.Message;
        }
    }

    /// <summary>影低而家個遊戲畫面做封面。</summary>
    private async Task<bool> CaptureCoverAsync(bool manual)
    {
        if (_web == null || _playing == null) return false;
        string file = _playing;
        bool ok = await CaptureWeb(FlashStore.CoverPath(file));
        if (ok) { RefreshTile(file); if (manual) Toast.Show(Lang.T("已設為封面"), Ui.Success, 1500); }
        else if (manual) Toast.Show(Lang.T("畫面仲係黑色，等遊戲出咗畫面再試"), Ui.Danger, 2500);
        return ok;
    }

    private void RefreshTile(string file)
    {
        foreach (var t in _tiles.Controls.OfType<GameTile>()) if (t.File == file) t.SetCover(FlashStore.LoadCover(file));
    }

    // WebView2 類型只喺呢幾個方法出現（唔好 inline，等 DLL 準備好先載入）
    [MethodImpl(MethodImplOptions.NoInlining)] private async Task<object> CreateWeb() => await FlashWeb.CreateAsync(_host);
    [MethodImpl(MethodImplOptions.NoInlining)] private void PlayWeb(string file, int vol) => ((FlashWeb)_web!).Play(file, vol);
    [MethodImpl(MethodImplOptions.NoInlining)] private void VolWeb(int vol) => ((FlashWeb)_web!).SetVolume(vol);
    [MethodImpl(MethodImplOptions.NoInlining)] private Task<bool> CaptureWeb(string path) => ((FlashWeb)_web!).CaptureAsync(path);
    [MethodImpl(MethodImplOptions.NoInlining)] private void StopWeb() => ((FlashWeb)_web!).Stop();
    [MethodImpl(MethodImplOptions.NoInlining)] private void CloseWeb() => ((FlashWeb)_web!).Close();
    [MethodImpl(MethodImplOptions.NoInlining)] private bool MuteWeb(bool? set) { var w = (FlashWeb)_web!; if (set is { } m) w.Muted = m; return w.Muted; }

    private void StopGame()
    {
        _playSeq++;
        _pending = null;
        if (_web == null || _playing == null) { _playing = null; return; }
        StopWeb();
        _playing = null;
        _list.Invalidate();
        ShowIdle();
    }

    private void ToggleMute()
    {
        if (_web == null) return;
        bool m = MuteWeb(!MuteWeb(null));
        _mute.Text = m ? "🔇" : "🔊"; _mute.Invalidate();
    }

    private void AddPick()
    {
        using var d = new OpenFileDialog { Filter = Lang.T("Flash 遊戲") + " (*.swf)|*.swf", Multiselect = true };
        if (d.ShowDialog(FindForm()) == DialogResult.OK) AddFiles(d.FileNames);
    }

    private void AddFiles(string[] files)
    {
        int added = 0;
        foreach (var f in files)
        {
            try { FlashStore.Add(f); added++; }
            catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        Reload();
        if (_playing == null) ShowIdle();
        if (added > 0) Toast.Show(Lang.F("已加入 {0} 個遊戲", added), Ui.Success, 2000);
    }

    private void ItemMenu((string File, string Name) g, Control owner, Point p)
    {
        var m = TUi.Menu(this);
        m.Items.Add(Lang.T("▶ 玩"), null, (_, _) => Play(g.File));
        m.Items.Add(Lang.T("改名…"), null, (_, _) =>
        {
            var v = Ai.RenameDialog.Ask(FindForm(), g.Name);
            if (v != null && v.Trim().Length > 0) { FlashStore.Rename(g.File, v); Reload(); if (_playing == g.File) _title.Text = v.Trim(); }
        });
        m.Items.Add(new ToolStripSeparator());
        if (_playing == g.File) m.Items.Add(Lang.T("📷 用而家畫面做封面"), null, async (_, _) => await CaptureCoverAsync(true));
        m.Items.Add(Lang.T("🖼 揀封面圖片…"), null, (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = Lang.T("圖片") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp" };
            if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
            try { FlashStore.SetCover(g.File, d.FileName); RefreshTile(g.File); }
            catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Program.AppName); }
        });
        if (FlashStore.HasCover(g.File))
            m.Items.Add(Lang.T("移除封面"), null, (_, _) => { try { File.Delete(FlashStore.CoverPath(g.File)); } catch { } RefreshTile(g.File); });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Lang.T("開啟資料夾"), null, (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + Path.Combine(FlashStore.Dir, g.File) + "\"") { UseShellExecute = true }); } catch { } });
        m.Items.Add(Lang.T("刪除"), null, (_, _) =>
        {
            if (MessageBox.Show(FindForm(), Lang.F("刪除「{0}」？", g.Name), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (_playing == g.File) StopGame();
            FlashStore.Delete(g.File); Reload(); ShowMode();
        });
        m.Show(owner, p);
    }
}

/// <summary>方格模式一格：封面（冇就畫個手掣）+ 名。</summary>
internal sealed class GameTile : Control
{
    public string File { get; }
    private readonly string _name;
    private Image? _cover;
    private bool _hover;

    public GameTile(string file, string name, Image? cover)
    {
        File = file; _name = name; _cover = cover;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(Ui.S(220), Ui.S(196));
        Margin = new Padding(Ui.S(8));
        Cursor = Cursors.Hand;
        new ToolTip().SetToolTip(this, name);
        Disposed += (_, _) => _cover?.Dispose();
    }

    public void SetCover(Image? img) { _cover?.Dispose(); _cover = img; Invalidate(); }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.Clear(Parent?.BackColor ?? Ui.Bg);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Ui.Round(r, Ui.S(10)))
        {
            using (var b = new SolidBrush(_hover ? Ui.CardHi : Ui.Card)) g.FillPath(b, path);
            var pic = new Rectangle(Ui.S(8), Ui.S(8), Width - Ui.S(16), Ui.S(140));
            if (_cover != null)
            {
                // 保持比例放入框（黑邊）
                using (var bg = new SolidBrush(Color.Black)) g.FillRectangle(bg, pic);
                double k = Math.Min((double)pic.Width / _cover.Width, (double)pic.Height / _cover.Height);
                int w = (int)(_cover.Width * k), h = (int)(_cover.Height * k);
                g.DrawImage(_cover, pic.X + (pic.Width - w) / 2, pic.Y + (pic.Height - h) / 2, w, h);
            }
            else
            {
                int hue = Math.Abs(File.GetHashCode()) % 360;
                using (var bg = new SolidBrush(FromHue(hue))) g.FillRectangle(bg, pic);
                TextRenderer.DrawText(g, "🎮", Ui.F(28f), pic, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            TextRenderer.DrawText(g, _name, Ui.F(10f, FontStyle.Bold), new Rectangle(Ui.S(10), Ui.S(152), Width - Ui.S(20), Ui.S(34)), Ui.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            using var pen = new Pen(_hover ? Ui.Accent : Ui.Border, _hover ? Ui.S(2f) : 1f);
            g.DrawPath(pen, path);
        }
    }

    private static Color FromHue(int hue)
    {
        double h = hue / 60.0, s = 0.45, v = 0.45, c = v * s, x = c * (1 - Math.Abs(h % 2 - 1)), m = v - c;
        var (r, gg, b) = h < 1 ? (c, x, 0d) : h < 2 ? (x, c, 0d) : h < 3 ? (0d, c, x) : h < 4 ? (0d, x, c) : h < 5 ? (x, 0d, c) : (c, 0d, x);
        return Color.FromArgb((int)((r + m) * 255), (int)((gg + m) * 255), (int)((b + m) * 255));
    }
}

/// <summary>音量拉桿（0 至 100）：拖、撳或者滾輪。</summary>
internal sealed class VolumeSlider : Control
{
    private int _value = 80;
    private bool _drag;
    public event Action? Changed;
    public int Value { get => _value; set { _value = Math.Clamp(value, 0, 100); Invalidate(); } }

    public VolumeSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(Ui.S(110), Ui.S(18));
        Cursor = Cursors.Hand;
    }

    private void SetFromX(int x) { int v = (int)Math.Round((x - Ui.S(6)) * 100.0 / Math.Max(1, Width - Ui.S(12))); if (Math.Clamp(v, 0, 100) != _value) { Value = v; Changed?.Invoke(); } }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _drag = true; SetFromX(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_drag) SetFromX(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = false; }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); if (e is HandledMouseEventArgs h) h.Handled = true; Value = _value + (e.Delta > 0 ? 5 : -5); Changed?.Invoke(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Ui.Bg);
        int y = Height / 2, x0 = Ui.S(6), x1 = Width - Ui.S(6), kx = x0 + (x1 - x0) * _value / 100;
        using (var p = new Pen(Ui.Border, Ui.S(4f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round }) g.DrawLine(p, x0, y, x1, y);
        if (kx > x0) using (var p = new Pen(Ui.Accent, Ui.S(4f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round }) g.DrawLine(p, x0, y, kx, y);
        using var b = new SolidBrush(Ui.Text);
        g.FillEllipse(b, kx - Ui.S(6), y - Ui.S(6), Ui.S(12), Ui.S(12));
    }
}
