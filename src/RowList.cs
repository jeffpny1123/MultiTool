using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Tools;

/// <summary>工具頁用嘅輸入格 / 下拉選單（跟主題色）。</summary>
internal static class TIn
{
    public static TextBox Box(string text, int w, string placeholder = "")
    {
        return new TextBox
        {
            Text = text, Width = Ui.S(w), BorderStyle = BorderStyle.FixedSingle, BackColor = Ui.Input, ForeColor = Ui.Text, Font = Ui.F(10f),
            PlaceholderText = placeholder, Margin = new Padding(0, Ui.S(4), Ui.S(10), 0)
        };
    }

    public static ComboBox Combo(int w, IEnumerable<string> items, int selected = 0)
    {
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(w), BackColor = Ui.Input, ForeColor = Ui.Text, FlatStyle = FlatStyle.Flat, Font = Ui.F(9.75f),
            Margin = new Padding(0, Ui.S(4), Ui.S(10), 0)
        };
        foreach (var i in items) c.Items.Add(i);
        if (c.Items.Count > 0) c.SelectedIndex = Math.Clamp(selected, 0, c.Items.Count - 1);
        return c;
    }

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / 1073741824.0:0.##} GB" : bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.#} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0} KB" : bytes + " B";
}

/// <summary>
/// 簡單嘅清單（自己畫，幾千行都唔會慢）：有欄位標題、可選剔格、每行右邊一個掣、分組標題行。
/// 重複檔案、開機程式、已安裝軟件、記帳都用佢。
/// </summary>
internal sealed class RowList : Control
{
    public sealed class Row
    {
        public string[] Cells = Array.Empty<string>();
        public bool Checked;
        public bool Header;          // 分組標題：成行粗體，冇剔格冇掣
        public bool Dim;             // 灰色字（例如已停用）
        public string? Action;       // 呢行嘅掣（null = 用清單預設；"" = 冇掣）
        public object? Tag;
    }

    public readonly List<Row> Rows = new();
    /// <summary>欄：標題、闊度（未縮放；0 = 用晒剩低嘅位）、係咪靠右。</summary>
    public (string Title, int Width, bool Right)[] Columns = Array.Empty<(string, int, bool)>();
    public bool Checks;
    public string ActionText = "";
    public string EmptyText = "";
    public event Action<Row>? RowAction;
    public event Action? CheckChanged;
    public event Action<Row, Point>? RowMenu;

    private readonly VScrollBar _bar = new() { Dock = DockStyle.Right, Visible = false };
    private int _hover = -1;
    private bool _hoverBtn;
    private int RowH => Ui.S(30);
    private int HeadH => Ui.S(28);
    private int BtnW => ActionText.Length > 0 || Rows.Any(r => !string.IsNullOrEmpty(r.Action)) ? Ui.S(96) : 0;

    public RowList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Ui.Input;
        Height = Ui.S(320);
        Controls.Add(_bar);
        _bar.ValueChanged += (_, _) => Invalidate();
    }

    /// <summary>改完 Rows 之後叫：重新計捲軸同重畫。</summary>
    public void Reload()
    {
        int visible = Math.Max(1, (Height - HeadH) / RowH);
        _bar.Visible = Rows.Count > visible;
        _bar.Maximum = Math.Max(0, Rows.Count - 1);
        _bar.LargeChange = visible;
        if (_bar.Value > Math.Max(0, Rows.Count - visible)) _bar.Value = Math.Max(0, Rows.Count - visible);
        Invalidate();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Reload(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_bar.Visible) return;
        int max = Math.Max(0, _bar.Maximum - _bar.LargeChange + 1);
        _bar.Value = Math.Clamp(_bar.Value - Math.Sign(e.Delta) * 3, 0, max);
    }

    private int Top0 => _bar.Visible ? _bar.Value : 0;
    private int ContentW => Width - (_bar.Visible ? _bar.Width : 0);

    private int RowAt(int y) { if (y < HeadH) return -1; int i = Top0 + (y - HeadH) / RowH; return i < Rows.Count ? i : -1; }
    private Rectangle BtnRect(int i) => new(ContentW - BtnW - Ui.S(6), HeadH + (i - Top0) * RowH + Ui.S(3), BtnW, RowH - Ui.S(6));
    private string BtnText(Row r) => r.Header ? "" : r.Action ?? ActionText;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = RowAt(e.Y);
        bool onBtn = i >= 0 && BtnText(Rows[i]).Length > 0 && BtnRect(i).Contains(e.Location);
        if (i != _hover || onBtn != _hoverBtn) { _hover = i; _hoverBtn = onBtn; Invalidate(); }
        Cursor = onBtn ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; _hoverBtn = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int i = RowAt(e.Y);
        if (i < 0) return;
        var r = Rows[i];
        if (e.Button == MouseButtons.Right) { if (!r.Header) RowMenu?.Invoke(r, e.Location); return; }
        if (e.Button != MouseButtons.Left) return;
        if (BtnText(r).Length > 0 && BtnRect(i).Contains(e.Location)) { RowAction?.Invoke(r); return; }
        if (Checks && !r.Header) { r.Checked = !r.Checked; CheckChanged?.Invoke(); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Input);
        int w = ContentW, x0 = Ui.S(10) + (Checks ? Ui.S(26) : 0), btnW = BtnW;
        // 注意：Ui.F() 俾返嚟嘅字型係全程式共用嘅，唔可以 Dispose（Dispose 咗其他卡片會畫唔到，彈「Parameter is not valid」）
        // 欄位位置：0 闊度嘅欄分晒剩低嘅位
        int fixedW = Columns.Sum(c => Ui.S(c.Width)), flex = Columns.Count(c => c.Width == 0);
        int rest = Math.Max(Ui.S(80) * Math.Max(1, flex), w - x0 - fixedW - btnW - Ui.S(16));
        var xs = new int[Columns.Length]; var ws = new int[Columns.Length];
        for (int c = 0, x = x0; c < Columns.Length; c++) { xs[c] = x; ws[c] = Columns[c].Width == 0 ? rest / Math.Max(1, flex) : Ui.S(Columns[c].Width); x += ws[c]; }
        var bold = Ui.F(9f, FontStyle.Bold);
        var font = Ui.F(9.25f);
        // 標題
        using (var hb = new SolidBrush(Ui.CardHi)) g.FillRectangle(hb, 0, 0, Width, HeadH);
        for (int c = 0; c < Columns.Length; c++)
            TextRenderer.DrawText(g, Columns[c].Title, bold, new Rectangle(xs[c], 0, ws[c] - Ui.S(8), HeadH), Ui.TextDim,
                (Columns[c].Right ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Rows.Count == 0)
        {
            TextRenderer.DrawText(g, EmptyText, font, new Rectangle(0, HeadH, w, Height - HeadH), Ui.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        for (int i = Top0, y = HeadH; i < Rows.Count && y < Height; i++, y += RowH)
        {
            var r = Rows[i];
            var rc = new Rectangle(0, y, w, RowH);
            if (r.Header) { using var b = new SolidBrush(Ui.Card); g.FillRectangle(b, rc); }
            else if (i == _hover) { using var b = new SolidBrush(Ui.CardHi); g.FillRectangle(b, rc); }
            if (r.Header)
            {
                TextRenderer.DrawText(g, string.Join("　", r.Cells), bold, new Rectangle(Ui.S(10), y, w - Ui.S(20), RowH), Ui.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                continue;
            }
            if (Checks)
            {
                var box = new Rectangle(Ui.S(10), y + (RowH - Ui.S(16)) / 2, Ui.S(16), Ui.S(16));
                using var pen = new Pen(r.Checked ? Ui.Accent : Ui.TextFaint);
                if (r.Checked) { using var b = new SolidBrush(Ui.Accent); g.FillRectangle(b, box); TextRenderer.DrawText(g, "✓", bold, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); }
                g.DrawRectangle(pen, box);
            }
            for (int c = 0; c < Columns.Length && c < r.Cells.Length; c++)
                TextRenderer.DrawText(g, r.Cells[c], font, new Rectangle(xs[c], y, ws[c] - Ui.S(8), RowH), r.Dim ? Ui.TextFaint : c == 0 ? Ui.Text : Ui.TextDim,
                    (Columns[c].Right ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string bt = BtnText(r);
            if (bt.Length > 0)
            {
                var br = BtnRect(i);
                bool hot = i == _hover && _hoverBtn;
                using var b = new SolidBrush(hot ? Ui.Accent : Ui.Card);
                g.FillRectangle(b, br);
                TextRenderer.DrawText(g, bt, font, br, hot ? Color.White : Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
