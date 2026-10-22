using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool;

/// <summary>
/// 擴充工具：功能本身已經喺 exe 入面，用家「安裝」先喺側欄顯示；要額外檔案（例如 ffmpeg）嘅就安裝時先下載。
/// 已安裝清單存喺 data\extensions.txt。
/// </summary>
internal static class Extensions
{
    public const string Scripts = "scripts";
    public const string Recorder = "recorder";
    public const string Replay = "replay";
    public const string Remap = "remap";
    public const string Window = "window";
    public const string Clipboard = "clipboard";
    public const string Ocr = "ocr";
    public const string Convert = "convert";
    public const string GameServer = "gameserver";
    public const string Download = "download";

    /// <summary>用 ffmpeg 嘅工具：最後一個移除先刪 ffmpeg。</summary>
    private static readonly string[] FfmpegUsers = { Recorder, Replay, Convert, Download };

    private static void RemoveFfmpeg(string id)
    {
        if (FfmpegUsers.Any(x => x != id && IsInstalled(x))) return;
        Video.Ffmpeg.Delete();
    }

    private static Info FfmpegTool(string id, string name, string desc) =>
        new(id, name, desc, false, (p, ct) => Video.Ffmpeg.InstallAsync(p, ct), () => Video.Ffmpeg.Ready, () => RemoveFfmpeg(id), "~100 MB");

    internal sealed record Info(
        string Id, string Name, string Desc, bool DefaultOn,
        Func<Action<string, double>, CancellationToken, Task>? Install = null, // 安裝時要做嘅嘢（例如下載）
        Func<bool>? Ready = null,                                              // 額外檔案齊唔齊
        Action? Remove = null,                                                 // 移除時清理
        string? Size = null);

    public static readonly List<Info> All = new()
    {
        new(Scripts, Lang.T("我的腳本"),
            Lang.T("自己砌自動化腳本（按鍵、滑鼠、搵圖、循環、IF、錄製…），包括腳本庫。"), true),
        FfmpegTool(Recorder, Lang.T("錄影工具"),
            Lang.T("錄低螢幕、範圍或者視窗，連電腦聲同咪高峰，輸出 MP4。需要下載 ffmpeg。")),
        FfmpegTool(Replay, Lang.T("精彩重播"),
            Lang.T("背景一直錄住最近 15 秒至 5 分鐘，撳快捷鍵先存低，錯過精彩位都唔怕。需要下載 ffmpeg。")),
        new(Remap, Lang.T("遊戲改鍵"),
            Lang.T("將一粒鍵變成另一粒鍵，滑鼠側鍵都得，可以只限某個遊戲生效。"), false),
        new(Window, Lang.T("視窗工具"),
            Lang.T("將視窗置頂、設透明度、變無邊框全螢幕或者置中，有快捷鍵。"), false),
        new(Clipboard, Lang.T("剪貼簿紀錄"),
            Lang.T("記低你複製過嘅文字同圖片，快捷鍵叫出細視窗，撳 1-9 就貼上。"), false),
        new(Ocr, Lang.T("文字辨識"),
            Lang.T("框住畫面上嘅文字就抽出嚟複製，用 Windows 內置辨識，唔使上網。"), false),
        FfmpegTool(Convert, Lang.T("轉檔工具"),
            Lang.T("影片、聲音、圖片互相轉換（MP4、GIF、MP3、PNG…），可以剪頭尾同縮細。需要下載 ffmpeg。")),
        new(GameServer, Lang.T("遊戲伺服器"),
            Lang.T("一撳架設 Palworld、Valheim、7 Days to Die 等 Steam 遊戲伺服器，有設定、自動備份同開端口。需要下載 SteamCMD。"), false,
            (p, ct) => Tools.SteamCmd.InstallAsync(p, ct), () => Tools.SteamCmd.Ready, Tools.SteamCmd.Delete, "~5 MB"),
        new(Download, Lang.T("影片下載"),
            Lang.T("貼上 YouTube 等網站嘅連結，存做 MP3 或者 MP4，支援播放清單。需要下載 yt-dlp 同 ffmpeg。"), false,
            (p, ct) => Tools.YtDlp.InstallAsync(p, ct), () => Tools.YtDlp.Ready, () => { Tools.YtDlp.Delete(); RemoveFfmpeg(Download); }, "~120 MB"),
    };

    private static string File_ => Path.Combine(AppPaths.Data, "extensions.txt");
    private static HashSet<string>? _installed;
    public static event Action? Changed;

    private static HashSet<string> Installed
    {
        get
        {
            if (_installed != null) return _installed;
            _installed = new HashSet<string>();
            try
            {
                if (File.Exists(File_))
                {
                    foreach (var l in File.ReadAllLines(File_))
                        if (l.Trim().Length > 0) _installed.Add(l.Trim());
                }
                else foreach (var e in All.Where(e => e.DefaultOn)) _installed.Add(e.Id); // 第一次：用預設
            }
            catch { }
            return _installed;
        }
    }

    public static bool IsInstalled(string id) => Installed.Contains(id);

    /// <summary>每個工具嘅圖示同顏色（工具庫同首頁用）。</summary>
    public static (string Glyph, Color Hue) Look(string id) => id switch
    {
        Scripts => ("▶", Ui.Accent),
        Recorder => ("●", Ui.Danger),
        Replay => ("↺", Ui.Danger),
        Remap => ("⌨", Ui.Accent2),
        Window => ("▣", Ui.Accent),
        Clipboard => ("≡", Ui.Success),
        Ocr => ("T", Ui.Accent2),
        Convert => ("⇄", Ui.Success),
        GameServer => ("◆", Ui.Success),
        Download => ("⇩", Ui.Danger),
        "ai" => ("✦", Ui.Accent2),
        "add" => ("+", Ui.Accent),
        _ => ("★", Ui.Accent2)
    };

    public static void Set(string id, bool on)
    {
        if (on) Installed.Add(id); else Installed.Remove(id);
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllLines(File_, Installed);
        }
        catch { }
        Changed?.Invoke();
    }
}

/// <summary>「擴充工具」頁：一格一格排（好似 App Store），闊啲就多幾欄。</summary>
internal sealed class ExtensionsPage : Panel
{
    private readonly Label _title, _hint, _empty;
    private readonly TextBox _query = new();
    private readonly InputHost _queryHost;
    private readonly List<ExtTile> _tiles = new();
    private bool _ready;

    public ExtensionsPage()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        AutoScroll = true;
        _title = Ui.Text_(Lang.T("工具庫"), Ui.Text, Ui.F(14f, FontStyle.Bold), new Padding(0));
        _query.PlaceholderText = Lang.T("搜尋工具…");
        _query.TextChanged += (_, _) => PerformLayout();
        _queryHost = new InputHost(_query, Ui.S(260));
        Controls.Add(_queryHost);
        _empty = Ui.Text_(Lang.T("搵唔到相關工具"), Ui.TextDim, Ui.F(9.75f), new Padding(0));
        Controls.Add(_empty);
        _hint = Ui.Text_(Lang.T("揀你想用嘅工具：安裝之後會喺左邊顯示；唔用可以移除（你嘅資料唔會刪走）。"), Ui.TextDim, Ui.F(9.5f), new Padding(0));
        Controls.Add(_title);
        Controls.Add(_hint);
        foreach (var e in Extensions.All)
        {
            var t = new ExtTile(e);
            _tiles.Add(t);
            Controls.Add(t);
        }
        _ready = true;
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (!_ready) return; // 建構期間（AutoScroll / 加控件都會觸發排位）
        try { Arrange(); }
        catch (Exception ex) { AppPaths.LogError("擴充工具排位", ex); }
    }

    private void Arrange()
    {
        int pad = Ui.S(24), gap = Ui.S(14);
        int w = Math.Max(Ui.S(200), ClientSize.Width - pad * 2);
        int min = Ui.S(240), max = Ui.S(320);
        int cols = Math.Max(1, (w + gap) / (min + gap));
        int tw = Math.Min(max, (w - gap * (cols - 1)) / cols);
        int th = Ui.S(196);
        var o = AutoScrollPosition;
        int y = Ui.S(20);
        _title.Location = new Point(pad + o.X, y + o.Y);
        y += _title.PreferredHeight + Ui.S(6);
        _hint.MaximumSize = new Size(w, 0);
        _hint.Location = new Point(pad + o.X, y + o.Y);
        y += _hint.PreferredHeight + Ui.S(14);
        _queryHost.Location = new Point(pad + o.X, y + o.Y);
        _queryHost.Width = Math.Min(Ui.S(360), w);
        y += _queryHost.Height + Ui.S(16);
        // 搜尋：名或者簡介有齊所有關鍵字先顯示
        var words = _query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = _tiles.Where(t => words.All(t.Matches)).ToList();
        foreach (var t in _tiles) t.Visible = shown.Contains(t);
        _empty.Visible = shown.Count == 0;
        _empty.Location = new Point(pad + o.X, y + o.Y);
        for (int i = 0; i < shown.Count; i++)
            shown[i].SetBounds(pad + (i % cols) * (tw + gap) + o.X, y + (i / cols) * (th + gap) + o.Y, tw, th);
        int rows = Math.Max(1, (shown.Count + cols - 1) / cols);
        int bottom = y + rows * (th + gap) + Ui.S(30);
        var want = new Size(0, bottom);
        if (AutoScrollMinSize != want) AutoScrollMinSize = want;
    }

    /// <summary>一格工具：圖示、名、簡介、大細，底部一個掣。</summary>
    private sealed class ExtTile : Control
    {
        private readonly Extensions.Info _e;
        private readonly FancyButton _btn;
        private string _status = "";
        private bool _hover;
        private CancellationTokenSource? _cts;

        public ExtTile(Extensions.Info e)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            _e = e;
            BackColor = Ui.Card;
            _btn = new FancyButton { Font = Ui.F(9.75f, FontStyle.Bold), Size = new Size(Ui.S(96), Ui.S(32)) };
            _btn.Click += async (_, _) => await ClickAsync();
            _btn.MouseEnter += (_, _) => { _hover = true; Invalidate(); };
            _btn.MouseLeave += (_, _) =>
            {
                if (!ClientRectangle.Contains(PointToClient(MousePosition))) { _hover = false; Invalidate(); }
            };
            Controls.Add(_btn);
            Refresh_();
        }

        private (string Glyph, Color Hue) Look => Extensions.Look(_e.Id);

        private bool Installed => Extensions.IsInstalled(_e.Id) && (_e.Ready?.Invoke() ?? true);

        public bool Matches(string word) =>
            _e.Name.Contains(word, StringComparison.OrdinalIgnoreCase) || _e.Desc.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            _e.Id.Contains(word, StringComparison.OrdinalIgnoreCase);

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!ClientRectangle.Contains(PointToClient(MousePosition))) { _hover = false; Invalidate(); }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_btn == null) return;
            int p = Ui.S(16);
            _btn.Location = new Point(p, Height - p - _btn.Height);
        }

        private void Refresh_()
        {
            bool on = Installed;
            _btn.Text = _cts != null ? Lang.T("取消") : on ? Lang.T("移除") : Lang.T("安裝");
            _btn.Kind = _cts != null || on ? BtnKind.Default : BtnKind.Success;
            if (_cts == null) _status = on ? Lang.T("✓ 已安裝") : _e.Size ?? "";
            _btn.Invalidate();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Ui.Bg);
            Ui.Smooth(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            bool on = Installed;
            using (var path = Ui.Round(r, Ui.S(12f)))
            {
                using var b = new SolidBrush(Ui.Card);
                g.FillPath(b, path);
                using var pen = new Pen(_hover ? Ui.BorderHi : Ui.Border, 1f);
                g.DrawPath(pen, path);
            }
            int p = Ui.S(16);
            // 圖示
            var (glyph, hue) = Look;
            var ir = new RectangleF(p, p, Ui.S(44f), Ui.S(44f));
            using (var ip = Ui.Round(ir, Ui.S(10f)))
            {
                using var ib = new SolidBrush(Ui.Mix(Ui.Card, hue, 0.22f));
                g.FillPath(ib, ip);
            }
            TextRenderer.DrawText(g, glyph, Ui.F(15f, FontStyle.Bold), Rectangle.Round(ir), Ui.OnTint(hue),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            // 名 + 狀態
            int tx = (int)ir.Right + Ui.S(12);
            var tf = Ui.F(11.5f, FontStyle.Bold);
            TextRenderer.DrawText(g, _e.Name, tf, new Rectangle(tx, p + Ui.S(2), Width - tx - p, tf.Height + 2), Ui.Text,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            var sf = Ui.F(8.75f);
            string sub = on ? Lang.T("✓ 已安裝") : Lang.T("未安裝");
            TextRenderer.DrawText(g, sub, sf, new Point(tx, p + Ui.S(4) + tf.Height), on ? Ui.Success : Ui.TextFaint,
                TextFormatFlags.NoPadding);
            // 簡介
            int dy = (int)ir.Bottom + Ui.S(12);
            var dr = new Rectangle(p, dy, Width - p * 2, Math.Max(0, _btn.Top - Ui.S(10) - dy));
            TextRenderer.DrawText(g, _e.Desc, Ui.F(9f), dr, Ui.TextDim,
                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl);
            // 掣隔籬：下載大細 / 進度
            if (_status.Length > 0 && !(on && _cts == null))
            {
                int sx = _btn.Right + Ui.S(10);
                TextRenderer.DrawText(g, _status, sf, new Rectangle(sx, _btn.Top, Width - sx - p, _btn.Height), Ui.TextDim,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }

        private void SetStatus(string s) { _status = s; Invalidate(); }

        private async Task ClickAsync()
        {
            if (_cts != null) { _cts.Cancel(); return; }
            if (Installed)
            {
                if (MessageBox.Show(FindForm(), Lang.F("移除「{0}」？\n\n左邊會唔再顯示佢；你嘅腳本 / 影片等資料唔會刪走。", _e.Name), Program.AppName,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                if (_e.Id == Extensions.Scripts) Scripts.ScriptRunner.StopEverything(Lang.T("已移除我的腳本"));
                try { _e.Remove?.Invoke(); } catch { }
                Extensions.Set(_e.Id, false);
                Refresh_();
                return;
            }
            if (_e.Install != null && !(_e.Ready?.Invoke() ?? false))
            {
                _cts = new CancellationTokenSource();
                SetStatus(Lang.T("準備緊…"));
                Refresh_();
                try
                {
                    await _e.Install((text, frac) => BeginInvoke(() =>
                        SetStatus(text + (frac >= 0 && frac < 1 ? $" {frac:P0}" : ""))), _cts.Token);
                }
                catch (OperationCanceledException) { _cts = null; Refresh_(); SetStatus(Lang.T("已取消")); return; }
                catch (Exception ex)
                {
                    AppPaths.LogError("安裝 " + _e.Id, ex);
                    _cts = null;
                    Refresh_();
                    SetStatus(Lang.T("安裝失敗：") + ex.Message);
                    return;
                }
                _cts = null;
            }
            Extensions.Set(_e.Id, true);
            Refresh_();
        }
    }
}
