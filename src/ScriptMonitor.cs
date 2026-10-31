using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MultiTool.Common;
using MultiTool.UI;

namespace MultiTool.Scripts;

// =====================================================================
//  我的腳本：監視模式 — 每個腳本一格，即時睇到佢目標程式嘅畫面（DWM 縮圖，
//  同 Windows Alt+Tab 一樣由顯示卡畫，唔使截圖），剔咗可以一齊開始。
// =====================================================================

/// <summary>Windows DWM 即時縮圖。</summary>
internal static class DwmThumb
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Props
    {
        public int Flags;
        public RECT Dest;
        public RECT Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool ClientOnly;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint dest, nint src, out nint thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumb, ref Props p);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint h);

    public static nint Register(nint dest, nint src) => DwmRegisterThumbnail(dest, src, out var t) == 0 ? t : 0;
    public static void Unregister(nint t) { if (t != 0) DwmUnregisterThumbnail(t); }

    public static Size ClientSize(nint h) => GetClientRect(h, out var r) ? new Size(r.R - r.L, r.B - r.T) : Size.Empty;

    /// <summary>dest = 喺主視窗（連邊框）座標嘅位置；visible = false 就收埋。</summary>
    public static void Update(nint t, Rectangle dest, bool visible)
    {
        if (t == 0) return;
        var p = new Props
        {
            Flags = 0x1 | 0x4 | 0x8 | 0x10, // 位置、不透明度、顯示、淨係 client 區（同腳本座標一樣）
            Dest = new RECT { L = dest.Left, T = dest.Top, R = dest.Right, B = dest.Bottom },
            Opacity = 255, Visible = visible && dest.Width > 0 && dest.Height > 0, ClientOnly = true
        };
        DwmUpdateThumbnailProperties(t, ref p);
    }
}

/// <summary>監視模式：一格一個腳本（淨係顯示有設定目標程式、而且程式開緊嘅）。</summary>
internal sealed class MonitorView : Panel
{
    private readonly List<Script> _scripts;
    private readonly HashSet<Script> _selected;
    private readonly Action _selChanged, _startSel, _stopAll;
    private readonly Panel _top;
    private readonly Panel _body;
    private readonly FancyButton _startBtn, _stopBtn;
    private readonly Label _empty;
    private readonly List<MonitorTile> _tiles = new();
    private readonly System.Windows.Forms.Timer _scan = new() { Interval = 1500 }, _sync = new() { Interval = 250 };
    public event Action? Back;

    public MonitorView(List<Script> scripts, HashSet<Script> selected, Action selChanged, Action startSel, Action stopAll)
    {
        _scripts = scripts; _selected = selected; _selChanged = selChanged; _startSel = startSel; _stopAll = stopAll;
        BackColor = Ui.Bg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        _top = new Panel { Dock = DockStyle.Top, Height = Ui.S(64), BackColor = Ui.Bg };
        var back = new FancyButton { Text = Lang.T("← 返回清單"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f) };
        back.Size = new Size(back.GetPreferredSize(Size.Empty).Width, Ui.S(34));
        back.Click += (_, _) => Back?.Invoke();
        var title = Ui.Text_(Lang.T("監視模式"), Ui.Text, Ui.F(13f, FontStyle.Bold), new Padding(0));
        _startBtn = new FancyButton { Kind = BtnKind.Success, Font = Ui.F(9.75f) };
        _startBtn.Click += (_, _) => { _startSel(); Refresh2(); };
        _stopBtn = new FancyButton { Text = Lang.T("⏹ 全部停止"), Kind = BtnKind.Danger, Font = Ui.F(9.75f) };
        _stopBtn.Size = new Size(Math.Max(Ui.S(110), _stopBtn.GetPreferredSize(Size.Empty).Width), Ui.S(34));
        _stopBtn.Click += (_, _) => { _stopAll(); Refresh2(); };
        foreach (var c in new Control[] { back, title, _startBtn, _stopBtn }) _top.Controls.Add(c);
        _top.Resize += (_, _) => PlaceTop(back, title);

        _body = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, AutoScroll = true };
        _body.Scroll += (_, _) => SyncThumbs();
        _body.MouseWheel += (_, _) => BeginInvoke(SyncThumbs);
        _body.Resize += (_, _) => Arrange();
        _empty = Ui.Text_(Lang.T("未有可以監視嘅腳本：腳本要揀咗目標程式視窗，而且個程式開緊，先會喺度顯示。"), Ui.TextDim, Ui.F(10f), new Padding(0));
        _body.Controls.Add(_empty);
        Controls.Add(_body);
        Controls.Add(_top);

        _scan.Tick += (_, _) => Rescan();
        _sync.Tick += (_, _) => SyncThumbs(); // 主視窗搬位 / 其他嘢改咗排位都跟得返
        ScriptRunner.Changed += OnRunnerChanged;
        ScriptRunner.Status += OnStatus;
        // 離開監視模式（返回清單 / 去其他分頁 / 最小化）就即刻放低所有畫面，返嚟先再載入
        VisibleChanged += (_, _) => { if (Visible) Rescan(); else HideAll(); };
        Disposed += (_, _) =>
        {
            ScriptRunner.Changed -= OnRunnerChanged;
            ScriptRunner.Status -= OnStatus;
            _scan.Dispose(); _sync.Dispose();
            foreach (var t in _tiles) t.Release();
        };
        UpdateButtons();
        Rescan();
        _scan.Start(); _sync.Start();
    }

    private void PlaceTop(Control back, Control title)
    {
        int pad = Ui.S(24), y = Ui.S(16);
        back.Location = new Point(pad, y);
        title.Location = new Point(back.Right + Ui.S(14), y + Ui.S(4));
        _stopBtn.Location = new Point(_top.Width - pad - _stopBtn.Width, y);
        _startBtn.Location = new Point(_stopBtn.Left - Ui.S(10) - _startBtn.Width, y);
    }

    private void OnRunnerChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(Refresh2);
    }

    private void OnStatus(string id, string text)
    {
        foreach (var t in _tiles) if (t.Script.Id == id) t.SetStatus(text);
    }

    public void Refresh2()
    {
        UpdateButtons();
        if (Reorder()) Arrange(); else SyncThumbs();
        foreach (var t in _tiles) t.Invalidate();
    }

    /// <summary>有腳本行緊：剔咗 / 行緊嘅排最前，其他格唔顯示畫面（慳資源）。冇嘢行緊就全部顯示，方便揀。回傳次序有冇變。</summary>
    private bool Reorder()
    {
        bool any = _scripts.Any(ScriptRunner.IsRunning);
        bool Active(Script s) => ScriptRunner.IsRunning(s) || _selected.Contains(s);
        foreach (var t in _tiles) t.Live = !any || ScriptRunner.IsRunning(t.Script);
        var before = _tiles.ToList();
        _tiles.Sort((a, b) =>
        {
            int ka = any && !Active(a.Script) ? 1 : 0, kb = any && !Active(b.Script) ? 1 : 0;
            return ka != kb ? ka.CompareTo(kb) : _scripts.IndexOf(a.Script).CompareTo(_scripts.IndexOf(b.Script));
        });
        return !before.SequenceEqual(_tiles);
    }

    private void UpdateButtons()
    {
        int n = _selected.Count(s => _scripts.Contains(s));
        _startBtn.Text = Lang.F("▶ 開始已選（{0}）", n);
        _startBtn.Enabled = n > 0;
        _startBtn.Size = new Size(Math.Max(Ui.S(130), _startBtn.GetPreferredSize(Size.Empty).Width), Ui.S(34));
        _stopBtn.Enabled = _scripts.Any(ScriptRunner.IsRunning);
        _startBtn.Invalidate(); _stopBtn.Invalidate();
        _top.PerformLayout();
        if (_top.Controls.Count > 1) PlaceTop(_top.Controls[0], _top.Controls[1]);
    }

    /// <summary>搵邊啲腳本嘅程式開緊：多咗就加格，冇咗就移除。</summary>
    private void Rescan()
    {
        if (IsDisposed || !Visible) return;
        var want = new List<(Script S, nint H)>();
        foreach (var sc in _scripts)
        {
            if (string.IsNullOrEmpty(sc.TargetProcess)) continue;
            nint h = 0;
            try { h = Win.Find(sc); } catch { }
            if (h != 0) want.Add((sc, h));
        }
        bool changed = false;
        foreach (var t in _tiles.ToList())
        {
            var w = want.FirstOrDefault(x => x.S == t.Script);
            if (w.S == null) { _tiles.Remove(t); _body.Controls.Remove(t); t.Release(); t.Dispose(); changed = true; }
            else if (w.H != t.Window) { t.Window = w.H; changed = true; }
        }
        foreach (var (s, h) in want)
        {
            if (_tiles.Any(t => t.Script == s)) continue;
            var tile = new MonitorTile(s, h, _selected);
            tile.CheckChanged += () => { _selChanged(); Refresh2(); };
            tile.Toggle += () => { ScriptRunner.Toggle(s); Refresh2(); };
            _tiles.Add(tile);
            _body.Controls.Add(tile);
            changed = true;
        }
        if (Reorder() || changed) Arrange();
        UpdateButtons();
    }

    private void Arrange()
    {
        int pad = Ui.S(24), gap = Ui.S(16);
        int w = Math.Max(Ui.S(300), _body.ClientSize.Width - pad * 2 - (_body.VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth));
        int min = Ui.S(380);
        int cols = Math.Max(1, (w + gap) / (min + gap));
        int tw = (w - gap * (cols - 1)) / cols;
        int th = MonitorTile.HeightFor(tw);
        var o = _body.AutoScrollPosition;
        _empty.Visible = _tiles.Count == 0;
        _empty.MaximumSize = new Size(w, 0);
        _empty.Location = new Point(pad + o.X, Ui.S(8) + o.Y);
        for (int i = 0; i < _tiles.Count; i++)
            _tiles[i].SetBounds(pad + (i % cols) * (tw + gap) + o.X, Ui.S(4) + (i / cols) * (th + gap) + o.Y, tw, th);
        int rows = (_tiles.Count + cols - 1) / cols;
        var want = new Size(0, Ui.S(4) + rows * (th + gap) + Ui.S(24));
        if (_body.AutoScrollMinSize != want) _body.AutoScrollMinSize = want;
        SyncThumbs();
    }

    private void HideAll() { foreach (var t in _tiles) t.Release(); }

    /// <summary>將每格嘅即時畫面擺返啱位（淨係完全睇到嘅格先顯示，費事蓋住其他嘢）。</summary>
    private void SyncThumbs()
    {
        var form = FindForm();
        if (form == null || !Visible || IsDisposed || form.WindowState == FormWindowState.Minimized) { HideAll(); return; }
        var view = MonitorTile.ToWindow(form, _body.RectangleToScreen(_body.ClientRectangle));
        foreach (var t in _tiles) t.Sync(form, view);
    }
}

/// <summary>監視模式一格：上面名 + 剔格 + 開始掣，下面即時畫面。</summary>
internal sealed class MonitorTile : Control
{
    public readonly Script Script;
    public nint Window;
    /// <summary>顯唔顯示即時畫面（腳本行緊時，冇行嘅格唔顯示）。</summary>
    public bool Live = true;
    private readonly HashSet<Script> _selected;
    private nint _thumb, _thumbDest;
    private string _status = "";
    private Rectangle _check, _play, _image;
    private bool _hoverPlay, _hoverCheck;
    public event Action? CheckChanged, Toggle;

    private static int Head => Ui.S(48);
    private static int Foot => Ui.S(28);
    public static int HeightFor(int w) => Head + (int)((w - Ui.S(16)) * 9 / 16.0) + Foot;

    public MonitorTile(Script s, nint window, HashSet<Script> selected)
    {
        Script = s; Window = window; _selected = selected;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Default;
    }

    public void SetStatus(string text) { _status = text; Invalidate(); }

    public void Release()
    {
        DwmThumb.Unregister(_thumb);
        _thumb = 0; _thumbDest = 0;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = Ui.S(8);
        _check = new Rectangle(Ui.S(14), Ui.S(14), Ui.S(20), Ui.S(20));
        _play = new Rectangle(Width - Ui.S(14) - Ui.S(84), Ui.S(8), Ui.S(84), Ui.S(32));
        _image = new Rectangle(pad, Head, Width - pad * 2, Height - Head - Foot);
    }

    /// <summary>DWM 縮圖位置係以主視窗左上角（連邊框）計。</summary>
    public static Rectangle ToWindow(Form form, Rectangle screen)
    {
        var b = form.Bounds;
        return new Rectangle(screen.X - b.X, screen.Y - b.Y, screen.Width, screen.Height);
    }

    /// <summary>按格嘅位置更新 DWM 縮圖（保持比例，置中）。</summary>
    public void Sync(Form form, Rectangle view)
    {
        if (!Live || Window == 0 || !DwmThumb.IsWindow(Window)) { Release(); return; }
        if (_thumb == 0 || _thumbDest != form.Handle)
        {
            Release();
            _thumb = DwmThumb.Register(form.Handle, Window);
            _thumbDest = form.Handle;
        }
        var src = DwmThumb.ClientSize(Window);
        var area = ToWindow(form, RectangleToScreen(_image));
        var dest = area;
        if (src.Width > 0 && src.Height > 0)
        {
            double k = Math.Min((double)area.Width / src.Width, (double)area.Height / src.Height);
            int w = (int)(src.Width * k), h = (int)(src.Height * k);
            dest = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        }
        bool show = Visible && view.Contains(area) && !DwmThumb.IsIconic(Window);
        DwmThumb.Update(_thumb, dest, show);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Clear(g, this, Parent?.BackColor ?? Ui.Bg);
        Ui.Smooth(g);
        bool running = ScriptRunner.IsRunning(Script);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var p = Ui.Round(r, Ui.S(12f)))
        {
            using (var b = new SolidBrush(Ui.Card)) g.FillPath(b, p);
            using var pen = new Pen(running ? Ui.Success : Ui.Border, running ? Ui.S(2f) : 1f);
            g.DrawPath(pen, p);
        }
        // 剔格
        bool on = _selected.Contains(Script);
        using (var p = Ui.Round(_check, Ui.S(4f)))
        {
            using var b = new SolidBrush(on ? Ui.Accent : (_hoverCheck ? Ui.CardHi : Ui.Input));
            g.FillPath(b, p);
            if (!on) { using var pen = new Pen(Ui.BorderHi); g.DrawPath(pen, p); }
        }
        if (on) TextRenderer.DrawText(g, "✓", Ui.F(10f, FontStyle.Bold), _check, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        // 名
        var nameR = new Rectangle(_check.Right + Ui.S(10), Ui.S(6), _play.Left - _check.Right - Ui.S(20), Ui.S(22));
        TextRenderer.DrawText(g, Script.Name, Ui.F(10.5f, FontStyle.Bold), nameR, Ui.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        var procR = new Rectangle(nameR.X, nameR.Bottom, nameR.Width, Ui.S(18));
        TextRenderer.DrawText(g, (Script.Background ? Lang.T("背景：") : Lang.T("前景：")) + Script.TargetProcess, Ui.F(8.75f), procR, Ui.TextFaint,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        // 開始 / 停止
        using (var p = Ui.Round(_play, Ui.S(8f)))
        {
            var c = running ? Ui.Danger : Ui.Success;
            using var b = new SolidBrush(_hoverPlay ? Ui.Mix(c, Color.White, 0.12f) : c);
            g.FillPath(b, p);
        }
        TextRenderer.DrawText(g, running ? Lang.T("■ 停止") : Lang.T("▶ 開始"), Ui.F(9.5f, FontStyle.Bold), _play, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        // 畫面底（DWM 縮圖會畫喺上面）
        using (var b = new SolidBrush(Ui.Input)) g.FillRectangle(b, _image);
        string hint = !Live ? Lang.T("冇運行：唔顯示畫面（慳資源）")
            : Window != 0 && DwmThumb.IsIconic(Window) ? Lang.T("程式最小化咗，還原先睇到畫面") : Lang.T("捲返入嚟睇畫面");
        TextRenderer.DrawText(g, hint, Ui.F(9f), _image, Ui.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        // 狀態
        var stR = new Rectangle(Ui.S(12), Height - Foot, Width - Ui.S(24), Foot);
        string st = running ? (_status.Length > 0 ? _status : Lang.T("運行中")) : (_status.Length > 0 ? _status : Lang.T("未開始"));
        TextRenderer.DrawText(g, st, Ui.F(9f), stR, running ? Ui.Success : Ui.TextDim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool hp = _play.Contains(e.Location), hc = _check.Contains(e.Location);
        Cursor = hp || hc ? Cursors.Hand : _image.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        if (hp != _hoverPlay || hc != _hoverCheck) { _hoverPlay = hp; _hoverCheck = hc; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { _hoverPlay = _hoverCheck = false; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_play.Contains(e.Location)) { Toggle?.Invoke(); Invalidate(); return; }
        var head = new Rectangle(0, 0, _play.Left, Head);
        if (_check.Contains(e.Location) || head.Contains(e.Location))
        {
            if (!_selected.Remove(Script)) _selected.Add(Script);
            CheckChanged?.Invoke();
            Invalidate();
        }
    }

    /// <summary>雙擊畫面：切去嗰個程式。</summary>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _image.Contains(e.Location) && Window != 0) Win.Activate(Window);
    }
}
