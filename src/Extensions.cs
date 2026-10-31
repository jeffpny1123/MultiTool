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
    public const string Remote = "remote";
    public const string Perf = "perf";
    public const string Disk = "disk";
    public const string Doc = "doc";
    public const string Editor = "editor";
    public const string Mail = "mail";
    public const string Plugins = "plugins";
    public const string Repair = "repair";

    /// <summary>工具庫分類（一個工具可以屬幾類）。</summary>
    public static readonly string[] Categories = { "game", "media", "work", "system", "net" };

    public static string CategoryName(string c) => c switch
    {
        "game" => Lang.T("遊戲"),
        "media" => Lang.T("媒體"),
        "work" => Lang.T("效率"),
        "system" => Lang.T("系統"),
        "net" => Lang.T("網絡"),
        _ => c
    };

    public static string[] CategoriesOf(string id) => id switch
    {
        Scripts => new[] { "work", "game" },
        Recorder => new[] { "media", "game" },
        Editor => new[] { "media" },
        Replay => new[] { "media", "game" },
        Remap => new[] { "game" },
        Window => new[] { "work", "game" },
        Clipboard => new[] { "work" },
        Ocr => new[] { "work" },
        Convert => new[] { "media", "work" },
        GameServer => new[] { "game", "net" },
        Download => new[] { "media", "net" },
        Remote => new[] { "net", "system" },
        Perf => new[] { "system", "game" },
        Disk => new[] { "system" },
        Mail => new[] { "work", "net" },
        Plugins => new[] { "work" },
        Repair => new[] { "work", "system", "media" },
        _ => Array.Empty<string>()
    };

    /// <summary>用 ffmpeg 嘅工具：最後一個移除先刪 ffmpeg。</summary>
    private static readonly string[] FfmpegUsers = { Recorder, Replay, Convert, Download, Editor, Repair };

    private static void RemoveFfmpeg(string id)
    {
        if (FfmpegUsers.Any(x => x != id && IsInstalled(x))) return;
        Video.Ffmpeg.Delete();
    }

    private static Info FfmpegTool(string id, string name, string desc, string? since = null) =>
        new(id, name, desc, false, (p, ct) => Video.Ffmpeg.InstallAsync(p, ct), () => Video.Ffmpeg.Ready, () => RemoveFfmpeg(id), "~100 MB", since);

    internal sealed record Info(
        string Id, string Name, string Desc, bool DefaultOn,
        Func<Action<string, double>, CancellationToken, Task>? Install = null, // 安裝時要做嘅嘢（例如下載）
        Func<bool>? Ready = null,                                              // 額外檔案齊唔齊
        Action? Remove = null,                                                 // 移除時清理
        string? Size = null,
        string? Since = null);                                                 // 邊個版本推出（新工具：工具庫右上角顯示 NEW）

    /// <summary>
    /// 新推出嘅工具：淨係最新嗰批（Since 最大嘅版本）先顯示 NEW，再新一批出咗舊嘅就冇。
    /// 以後加新工具記得喺 All 入面填 Since: "vX.Y"。
    /// </summary>
    public static bool IsNew(Info e) => !string.IsNullOrEmpty(e.Since) && Num(e.Since) == LatestSince;

    private static int Num(string v)
    {
        var p = v.Trim().TrimStart('v', 'V').Split('.');
        int major = p.Length > 0 && int.TryParse(p[0], out var a) ? a : 0;
        int minor = p.Length > 1 && int.TryParse(p[1], out var b) ? b : 0;
        return major * 100 + minor;
    }

    private static int LatestSince => All.Where(x => !string.IsNullOrEmpty(x.Since)).Select(x => Num(x.Since!)).DefaultIfEmpty(-1).Max();

    public static readonly List<Info> All = new()
    {
        new(Scripts, Lang.T("我的腳本"),
            Lang.T("自己砌自動化腳本（按鍵、滑鼠、搵圖、循環、IF、錄製…），包括腳本庫。"), true),
        FfmpegTool(Recorder, Lang.T("螢幕錄影"),
            Lang.T("錄低螢幕、範圍或者視窗，連電腦聲同咪高峰，輸出 MP4。需要下載 ffmpeg。")),
        FfmpegTool(Editor, Lang.T("影片剪輯"),
            Lang.T("好似 Clipchamp 咁剪片：時間軸剪接、分割、文字標題、背景音樂、轉場、速度同濾鏡，即時預覽連聲，匯出 MP4（可以用顯示卡加速）。需要下載 ffmpeg。"), "v2.3"),
        FfmpegTool(Replay, Lang.T("精彩重播"),
            Lang.T("背景一直錄住最近 15 秒至 5 分鐘，撳快捷鍵先存低，錯過精彩位都唔怕。需要下載 ffmpeg。")),
        new(Remap, Lang.T("遊戲改鍵"),
            Lang.T("將一粒鍵變成另一粒鍵，滑鼠側鍵都得，可以只限某個遊戲生效。"), false),
        new(Window, Lang.T("視窗管理"),
            Lang.T("將視窗置頂、設透明度、變無邊框全螢幕或者置中，有快捷鍵。"), false),
        new(Clipboard, Lang.T("剪貼簿紀錄"),
            Lang.T("記低你複製過嘅文字同圖片，快捷鍵叫出細視窗，撳 1-9 就貼上。"), false),
        new(Ocr, Lang.T("文字辨識"),
            Lang.T("框住畫面上嘅文字就抽出嚟複製，用 Windows 內置辨識，唔使上網。"), false),
        new(Convert, Lang.T("格式轉換"),
            Lang.T("影音：影片、聲音、圖片互轉（MP4、GIF、MP3、PNG…），剪頭尾同縮細，第一次用要下載 ffmpeg（~100 MB）。文件：Excel、CSV、TSV、JSON 互轉，修正 CSV 中文亂碼。"), false,
            Remove: () => RemoveFfmpeg(Convert)),
        new(GameServer, Lang.T("遊戲伺服器"),
            Lang.T("一撳架設 Palworld、Valheim、7 Days to Die 等 Steam 遊戲伺服器，有設定、自動備份同開端口。需要下載 SteamCMD。"), false,
            (p, ct) => Tools.SteamCmd.InstallAsync(p, ct), () => Tools.SteamCmd.Ready, Tools.SteamCmd.Delete, "~5 MB"),
        new(Download, Lang.T("影片下載"),
            Lang.T("貼上 YouTube、Bilibili、Twitch、TikTok、X、Vimeo、SoundCloud 等 1000 幾個網站嘅連結，存做 MP3 或者 MP4，支援播放清單。需要下載 yt-dlp 同 ffmpeg。"), false,
            (p, ct) => Tools.YtDlp.InstallAsync(p, ct), () => Tools.YtDlp.Ready, () => { Tools.YtDlp.Delete(); RemoveFfmpeg(Download); }, "~120 MB"),
        new(Remote, Lang.T("遠端桌面（測試）"),
            Lang.T("MultiTool 內置嘅遠端桌面：用 ID + 密碼控制其他電腦或者俾人連入嚟，全程加密。冇中轉伺服器：被控電腦用流動數據、CGNAT 或者公共網絡就連唔到。"), false,
            Remove: () => Tools.RemoteHost.Stop()),
        new(Perf, Lang.T("效能監控"),
            Lang.T("即時睇 CPU、GPU、RAM、VRAM、磁碟、網速同 GPU 溫度，仲可以喺遊戲畫面角落顯示；有 Ping 監看同一撳閂背景程式嘅遊戲模式。用 Windows 內置資料，唔使第三方程式。"), false,
            Remove: () => Tools.PerfOverlay.Set(false, false)),
        new(Mail, Lang.T("電子郵件"),
            Lang.T("好似 Gmail 咁收信、睇信、搜尋、寫信、回覆、轉寄同附件，支援 Gmail 同 Outlook / Hotmail 多個帳戶，有新郵件通知，仲可以叫 AI 摘要、整理收件匣同草擬回覆。"), false,
            Remove: () => MultiTool.Mail.MailWatch.Stop(), Since: "v2.5"),
        new(Plugins, Lang.T("工作坊"),
            Lang.T("用 HTML + JavaScript 寫你自己嘅工具：連網上 API、彈通知、儲存資料、叫 AI 幫手；亦可以寫插件改 MultiTool 原有嘅介面（例如換背景、改顏色、加掣）。有程式碼編輯器、即時試行，唔識寫 code 可以叫 AI 幫你寫；工具同插件都可以匯出俾其他人用。"), false,
            Remove: () =>
            {
                foreach (var id in MultiTool.Plugins.PluginRuntime.Running.Keys.ToList()) MultiTool.Plugins.PluginRuntime.Stop(id);
                MultiTool.Plugins.ModHost.StopAll(); // 插件改咗嘅介面即刻還原
            },
            Since: "v2.5"),
        new(Repair, Lang.T("檔案修復"),
            Lang.T("開唔到嘅影片、聲音、相片、ZIP / Word / Excel / PowerPoint、PDF 拖入嚟就自動修復，修好嘅另存一份。部分修復第一次用要下載免費開源工具（ffmpeg、qpdf、untrunc）。"), false,
            Remove: () => { Tools.RepairTools.Delete(); RemoveFfmpeg(Repair); }, Since: "v0.252"),
        new(Disk, Lang.T("磁碟空間及健康"),
            Lang.T("睇每個磁碟剩幾多位、硬碟健康（溫度、SSD 壽命、讀寫錯誤），掃描邊個資料夾最食位，一撳掉入資源回收筒。"), false),
    };

    /// <summary>工具庫 / 首頁排序：新工具最前，跟住常用嘅，我的腳本第一，測試版最後。</summary>
    private static readonly string[] Order =
        { Scripts, Repair, Mail, Plugins, Recorder, Editor, Download, Convert, Clipboard, Perf, Window, Ocr, Replay, Remap, Disk, GameServer, Remote };

    private static readonly bool Sorted = SortAll();

    private static bool SortAll()
    {
        // 新工具（NEW）放最前，之後照 Order
        var list = All.OrderByDescending(IsNew).ThenBy(e => Rank(e.Id)).ToList();
        All.Clear();
        All.AddRange(list);
        return true;
        static int Rank(string id) { int i = Array.IndexOf(Order, id); return i < 0 ? Order.Length : i; }
    }

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
                if (_installed.Remove(Doc)) _installed.Add(Convert); // 舊版「文件轉檔」併咗入格式轉換
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
        Editor => ("✂", Ui.Hex(0xE07A5F)),
        Replay => ("↺", Ui.Danger),
        Remap => ("⌨", Ui.Accent2),
        Window => ("▣", Ui.Accent),
        Clipboard => ("≡", Ui.Success),
        Ocr => ("T", Ui.Accent2),
        Convert => ("⇄", Ui.Success),
        GameServer => ("◆", Ui.Success),
        Download => ("⇩", Ui.Danger),
        Remote => ("⇆", Ui.Accent),
        Perf => ("▤", Ui.Success),
        Disk => ("◔", Ui.Accent2),
        Mail => ("✉", Ui.Hex(0xEA4335)),
        Plugins => ("◈", Ui.Accent2),
        Repair => ("✚", Ui.Success),
        Doc => ("▦", Ui.Accent),
        "ai" => ("✦", Ui.Accent2),
        "home" => ("⌂", Ui.Accent),
        "extensions" => ("▦", Ui.Success),
        "settings" => ("⚙", Ui.TextDim),
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
    private readonly Tools.TabStrip _tabs;
    private readonly ChipRow _cats;
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
        // 分頁：全部 / 已安裝 / 未安裝
        _tabs = new Tools.TabStrip(Lang.T("全部"), Lang.T("已安裝"), Lang.T("未安裝"));
        _tabs.Selected += _ => PerformLayout();
        Controls.Add(_tabs);
        // 分類：全部 / 遊戲 / 媒體 …
        _cats = new ChipRow(new[] { Lang.T("全部類別") }.Concat(Extensions.Categories.Select(Extensions.CategoryName)).ToArray());
        _cats.Selected += _ => PerformLayout();
        Controls.Add(_cats);
        Extensions.Changed += OnExtChanged;
        Disposed += (_, _) => Extensions.Changed -= OnExtChanged;
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

    private bool _relayoutQueued;

    protected override void OnLayout(LayoutEventArgs e)
    {
        if (!_ready) { base.OnLayout(e); return; } // 建構期間（AutoScroll / 加控件都會觸發排位）
        var before = AutoScrollMinSize;
        try { Arrange(); }
        catch (Exception ex) { AppPaths.LogError("擴充工具排位", ex); }
        // 排好位先俾 ScrollableControl 計捲軸；高度變咗再排多次，捲軸先會即刻出
        base.OnLayout(e);
        if (before != AutoScrollMinSize && IsHandleCreated && !_relayoutQueued)
        {
            _relayoutQueued = true;
            BeginInvoke(() => { _relayoutQueued = false; PerformLayout(); Invalidate(true); });
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_ready) PerformLayout();
    }

    private void OnExtChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() => { PerformLayout(); Invalidate(true); });
    }

    private void Arrange()
    {
        int pad = Ui.S(24), gap = Ui.S(14);
        // 永遠預留捲軸闊度，唔係捲軸一出一收欄數會跳
        int sb = VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth;
        int w = Math.Max(Ui.S(200), ClientSize.Width - sb - pad * 2);
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
        y += _queryHost.Height + Ui.S(12);
        _cats.SetBounds(pad + o.X, y + o.Y, w, _cats.Height);
        y += _cats.Height + Ui.S(8);
        _tabs.SetBounds(pad + o.X, y + o.Y, w, _tabs.Height);
        y += _tabs.Height + Ui.S(16);
        // 搜尋：名或者簡介有齊所有關鍵字先顯示
        var words = _query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? cat = _cats.Index == 0 ? null : Extensions.Categories[_cats.Index - 1];
        var shown = _tiles.Where(t => words.All(t.Matches) && (_tabs.Index == 0 || (_tabs.Index == 1) == t.IsOn) &&
                                      (cat == null || Extensions.CategoriesOf(t.Id).Contains(cat))).ToList();
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

        public bool IsOn => Installed;
        public string Id => _e.Id;

        public bool Matches(string word) =>
            _e.Name.Contains(word, StringComparison.OrdinalIgnoreCase) || _e.Desc.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            _e.Id.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            Extensions.CategoriesOf(_e.Id).Any(c => Extensions.CategoryName(c).Contains(word, StringComparison.OrdinalIgnoreCase));

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
            Ui.Clear(g, this, Parent?.BackColor ?? Ui.Bg);
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
            int nameRight = Width - p;
            if (Extensions.IsNew(_e))
            {
                // 右上角 NEW 標籤
                var nf = Ui.F(7.75f, FontStyle.Bold);
                var ns = TextRenderer.MeasureText("NEW", nf);
                var nr = new RectangleF(Width - p - ns.Width - Ui.S(12), p - Ui.S(4), ns.Width + Ui.S(12), ns.Height + Ui.S(4));
                using (var np = Ui.Round(nr, nr.Height / 2f))
                using (var nb = new SolidBrush(Ui.Danger))
                    g.FillPath(nb, np);
                TextRenderer.DrawText(g, "NEW", nf, Rectangle.Round(nr), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                nameRight = (int)nr.Left - Ui.S(6);
            }
            tf = Ui.Fit(_e.Name, new Size(Math.Max(10, nameRight - tx), tf.Height + 2), 11.5f, 9f, FontStyle.Bold, wrap: false);
            TextRenderer.DrawText(g, _e.Name, tf, new Rectangle(tx, p + Ui.S(2), Math.Max(10, nameRight - tx), tf.Height + 2), Ui.Text,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            var sf = Ui.F(8.75f);
            string sub = on ? Lang.T("✓ 已安裝") : Lang.T("未安裝");
            TextRenderer.DrawText(g, sub, sf, new Point(tx, p + Ui.S(4) + tf.Height), on ? Ui.Success : Ui.TextFaint,
                TextFormatFlags.NoPadding);
            // 簡介
            int dy = (int)ir.Bottom + Ui.S(12);
            var dr = new Rectangle(p, dy, Width - p * 2, Math.Max(0, _btn.Top - Ui.S(10) - dy));
            TextRenderer.DrawText(g, _e.Desc, Ui.Fit(_e.Desc, dr.Size, 9f, 7.5f), dr, Ui.TextDim,
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

/// <summary>一排圓角分類掣（揀一個）。</summary>
internal sealed class ChipRow : Control
{
    private readonly string[] _items;
    private int _sel, _hover = -1;
    public event Action<int>? Selected;

    public ChipRow(params string[] items)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _items = items;
        Height = Ui.S(34);
        Cursor = Cursors.Hand;
    }

    public int Index { get => _sel; set { _sel = value; Invalidate(); Selected?.Invoke(value); } }

    private Rectangle ChipRect(int i)
    {
        int x = 0;
        for (int k = 0; k < _items.Length; k++)
        {
            int w = TextRenderer.MeasureText(_items[k], Ui.F(9.75f, FontStyle.Bold)).Width + Ui.S(26);
            if (k == i) return new Rectangle(x, Ui.S(2), w, Height - Ui.S(4));
            x += w + Ui.S(8);
        }
        return Rectangle.Empty;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = Enumerable.Range(0, _items.Length).FirstOrDefault(i => ChipRect(i).Contains(e.Location), -1);
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        for (int i = 0; i < _items.Length; i++) if (ChipRect(i).Contains(e.Location)) { Index = i; return; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Clear(g, this, Parent?.BackColor ?? Ui.Bg);
        Ui.Smooth(g);
        for (int i = 0; i < _items.Length; i++)
        {
            var r = ChipRect(i);
            bool sel = i == _sel;
            using (var p = Ui.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), (r.Height - 1) / 2f))
            {
                using var b = new SolidBrush(sel ? Ui.Accent : i == _hover ? Ui.CardHi : Ui.Card);
                g.FillPath(b, p);
                if (!sel) { using var pen = new Pen(Ui.Border); g.DrawPath(pen, p); }
            }
            TextRenderer.DrawText(g, _items[i], Ui.F(9.75f, sel ? FontStyle.Bold : FontStyle.Regular), r,
                sel ? Color.White : Ui.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
