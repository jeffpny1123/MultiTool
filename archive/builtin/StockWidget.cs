using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Tools;

/// <summary>
/// 股票桌面小工具：細細個置頂視窗，顯示 ★ 標記咗嘅股票（冇標記就顯示頭幾隻自選股）。
/// 可以拖去任何位置；右鍵揀透明度、置頂、關閉。開住 / 位置會記住，下次開程式自動再開。
/// </summary>
internal sealed class StockWidget : Form
{
    private static StockWidget? _inst;
    private static string PrefPath => Path.Combine(AppPaths.Data, "stocks", "widget.txt");

    private List<(StockItem It, StockQuote? Q)> _rows = new();
    private Point _dragFrom;
    private bool _dragging;

    private int RowH => Ui.S(30);
    private int HeadH => Ui.S(26);

    /// <summary>股票頁個掣：開 / 閂。</summary>
    public static void Toggle()
    {
        if (_inst is { IsDisposed: false }) { _inst.UserClose(); return; }
        Open();
    }

    /// <summary>開程式時：上次開住就再開。</summary>
    public static void RestoreIfOpen()
    {
        var p = LoadPrefs();
        if (p.Open) Open();
    }

    public static void Refresh()
    {
        if (_inst is { IsDisposed: false } w) { try { w.BeginInvoke(w.Reload); } catch { } }
    }

    private static void Open()
    {
        StockWatch.Start();
        _inst = new StockWidget();
        _inst.Show();
        SavePrefs(true, _inst.Location, _inst.Opacity, _inst.TopMost);
        _ = StockWatch.RefreshAsync();
    }

    private record Prefs(bool Open, Point? Pos, double Opacity, bool Top);

    private static Prefs LoadPrefs()
    {
        try
        {
            if (!File.Exists(PrefPath)) return new(false, null, 0.92, true);
            var p = File.ReadAllText(PrefPath).Split(',');
            return new(p[0] == "1", p.Length >= 3 && int.TryParse(p[1], out var x) && int.TryParse(p[2], out var y) ? new Point(x, y) : null,
                p.Length >= 4 && double.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var o) ? Math.Clamp(o, 0.4, 1) : 0.92,
                p.Length < 5 || p[4] != "0");
        }
        catch { return new(false, null, 0.92, true); }
    }

    private static void SavePrefs(bool open, Point pos, double opacity, bool top)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefPath)!);
            File.WriteAllText(PrefPath, $"{(open ? 1 : 0)},{pos.X},{pos.Y},{opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)},{(top ? 1 : 0)}");
        }
        catch { }
    }

    private StockWidget()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Card;
        Font = Ui.F(9.5f);
        Text = Lang.T("股票");
        var p = LoadPrefs();
        TopMost = p.Top;
        Opacity = p.Opacity;
        Width = Ui.S(270);
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = p.Pos is { } pos && Screen.AllScreens.Any(s => s.WorkingArea.Contains(pos)) ? pos : new Point(wa.Right - Width - Ui.S(20), wa.Top + Ui.S(120));
        StockWatch.Updated += OnUpdated;
        FormClosed += (_, _) =>
        {
            StockWatch.Updated -= OnUpdated;
            // 用家自己閂 = 下次唔再開；程式結束閂 = 保持開住狀態（下次開程式自動再開）
            SavePrefs(!_userClose, Location, Opacity, TopMost);
            if (_inst == this) _inst = null;
        };
        Reload();
    }

    private bool _userClose;
    private void UserClose() { _userClose = true; Close(); }

    // 唔好搶焦點
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW：唔喺 Alt+Tab 出現
            return cp;
        }
    }

    private void OnUpdated()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(Reload); } catch { }
    }

    private void Reload()
    {
        List<StockItem> items;
        lock (StockWatch.Items) items = StockWatch.Items.ToList();
        var show = items.Where(x => x.Starred).ToList();
        if (show.Count == 0) show = items.Where(x => !(StockPrefs.HidePresets && x.IsPreset)).Take(6).ToList(); // 冇標記就顯示頭幾隻
        lock (StockWatch.Quotes)
            _rows = show.Select(it => (it, StockWatch.Quotes.TryGetValue(it.Symbol, out var q) ? q : null)).ToList();
        int h = HeadH + Math.Max(1, _rows.Count) * RowH + Ui.S(6);
        if (Height != h) Height = h;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Card);
        Ui.Smooth(g);
        using (var p = new Pen(Ui.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        // 標題列
        string upd = StockWatch.LastUpdate == default ? "" : StockWatch.LastUpdate.ToString("HH:mm");
        TextRenderer.DrawText(g, "📈 " + Lang.T("股票"), Ui.F(8.75f, FontStyle.Bold), new Rectangle(Ui.S(10), 0, Width, HeadH), Ui.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, upd + "  ✕", Ui.F(8.75f), new Rectangle(0, 0, Width - Ui.S(8), HeadH), Ui.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        if (_rows.Count == 0)
        {
            TextRenderer.DrawText(g, Lang.T("未有自選股"), Font, new Rectangle(0, HeadH, Width, RowH), Ui.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        for (int i = 0; i < _rows.Count; i++)
        {
            var (it, q) = _rows[i];
            var r = new Rectangle(Ui.S(10), HeadH + i * RowH, Width - Ui.S(20), RowH);
            TextRenderer.DrawText(g, it.Symbol, Ui.F(9.5f, FontStyle.Bold), new Rectangle(r.X, r.Y, Ui.S(96), r.Height), Ui.Text, flags);
            if (q == null) { TextRenderer.DrawText(g, "…", Font, r, Ui.TextFaint, flags | TextFormatFlags.Right); continue; }
            Color col = q.Change > 0 ? StockPrefs.Up : q.Change < 0 ? StockPrefs.Down : Ui.TextDim;
            string sign = q.Pct >= 0 ? "+" : "";
            TextRenderer.DrawText(g, StockWatch.Fmt(q.Price), Font, new Rectangle(r.X + Ui.S(90), r.Y, Ui.S(90), r.Height), Ui.Text, flags | TextFormatFlags.Right);
            TextRenderer.DrawText(g, $"{sign}{q.Pct:0.00}%", Ui.F(9f, FontStyle.Bold), new Rectangle(r.Right - Ui.S(70), r.Y, Ui.S(70), r.Height), col, flags | TextFormatFlags.Right);
        }
    }

    // ---------- 拖動 / 右鍵 ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            if (e.Y < HeadH && e.X > Width - Ui.S(26)) { UserClose(); return; } // ✕
            _dragging = true;
            _dragFrom = e.Location;
        }
        else if (e.Button == MouseButtons.Right) Menu(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Location = new Point(Location.X + e.X - _dragFrom.X, Location.Y + e.Y - _dragFrom.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging) { _dragging = false; SavePrefs(true, Location, Opacity, TopMost); }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        // 雙擊：開返主程式嘅股票頁
        foreach (Form f in Application.OpenForms)
            if (f is MultiTool.MainForm mf) { mf.BringToFrontAndShow("stocks"); break; }
    }

    private void Menu(Point at)
    {
        var m = TUi.Menu(this);
        m.Items.Add(Lang.T("開股票頁"), null, (_, _) => OnMouseDoubleClick(new MouseEventArgs(MouseButtons.Left, 2, 0, 0, 0)));
        m.Items.Add(Lang.T("⟳ 更新"), null, (_, _) => _ = StockWatch.RefreshAsync());
        var top = new ToolStripMenuItem(Lang.T("永遠喺最上面")) { Checked = TopMost };
        top.Click += (_, _) => { TopMost = !TopMost; SavePrefs(true, Location, Opacity, TopMost); };
        m.Items.Add(top);
        var op = new ToolStripMenuItem(Lang.T("透明度"));
        foreach (var v in new[] { 1.0, 0.92, 0.8, 0.65, 0.5 })
        {
            var item = new ToolStripMenuItem((int)(v * 100) + "%") { Checked = Math.Abs(Opacity - v) < 0.01 };
            item.Click += (_, _) => { Opacity = v; SavePrefs(true, Location, Opacity, TopMost); };
            op.DropDownItems.Add(item);
        }
        m.Items.Add(op);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Lang.T("關閉小工具"), null, (_, _) => UserClose());
        m.Show(this, at);
    }
}
