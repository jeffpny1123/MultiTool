using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MultiTool
{

/// <summary>所有設定 / 圖片都放喺 exe 隔籬嘅 data 資料夾。</summary>
public static class AppPaths
{
    public static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    public static string Scripts => Path.Combine(Data, "scripts");
    public static string ErrorLog => Path.Combine(Data, "error.log");

    public static void LogError(string where, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Data);
            File.AppendAllText(ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}

namespace UI
{
    /// <summary>深色主題：顏色、字型、DPI 縮放同繪圖小工具。</summary>
    public static class Ui
    {
        public static readonly Color Bg = Hex(0x0E1014);
        public static readonly Color Panel = Hex(0x14171E);
        public static readonly Color Card = Hex(0x191D26);
        public static readonly Color CardHi = Hex(0x222836);
        public static readonly Color Input = Hex(0x11141A);
        public static readonly Color Border = Hex(0x2A3040);
        public static readonly Color BorderHi = Hex(0x3B4459);
        public static readonly Color Text = Hex(0xE7EAF1);
        public static readonly Color TextDim = Hex(0xA3ABBC);
        public static readonly Color TextFaint = Hex(0x6C7487);
        public static readonly Color Accent = Hex(0x4F8CFF);
        public static readonly Color Accent2 = Hex(0x8C6CFF);
        public static readonly Color Success = Hex(0x2BB673);
        public static readonly Color Danger = Hex(0xE5484D);

        public static string UiFamily => Lang.Current switch
        {
            Language.ZhHans => "Microsoft YaHei UI",
            Language.En => "Segoe UI",
            _ => "Microsoft JhengHei UI"
        };
        public const string MonoFamily = "Consolas";

        public static readonly float Scale = GetScale();

        private static readonly Dictionary<(string, float, FontStyle), Font> Fonts = new();

        private static float GetScale()
        {
            try
            {
                using var g = Graphics.FromHwnd(IntPtr.Zero);
                return Math.Max(1f, g.DpiX / 96f);
            }
            catch { return 1f; }
        }

        public static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        public static Color Alpha(Color c, int a) => Color.FromArgb(a, c);

        /// <summary>a 同 b 混色（t = 0 全 a，1 全 b）。</summary>
        public static Color Mix(Color a, Color b, float t) => Color.FromArgb(255,
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        public static int S(int v) => (int)Math.Round(v * Scale);
        public static float S(float v) => v * Scale;

        private static Font Get(string family, float size, FontStyle style)
        {
            var key = (family, size, style);
            if (Fonts.TryGetValue(key, out var f)) return f;
            try { f = new Font(family, size, style, GraphicsUnit.Point); }
            catch { f = new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point); }
            Fonts[key] = f;
            return f;
        }

        public static Font F(float size, FontStyle style = FontStyle.Regular) => Get(UiFamily, size, style);
        public static Font Mono(float size, FontStyle style = FontStyle.Regular) => Get(MonoFamily, size, style);

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Brush AccentBrush(RectangleF r) =>
            new LinearGradientBrush(r.Width < 1 ? new RectangleF(r.X, r.Y, 1, Math.Max(1, r.Height)) : r, Accent, Accent2,
                LinearGradientMode.Horizontal);

        /// <summary>透明背景嘅文字 Label。</summary>
        public static Label Text_(string text, Color color, Font font, Padding margin) => new()
        {
            Text = text,
            ForeColor = color,
            Font = font,
            Margin = margin,
            AutoSize = true,
            BackColor = Color.Transparent,
            UseMnemonic = false
        };
    }

    public static class DarkTheme
    {
        public static ToolStripRenderer MenuRenderer() => new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false };

        private sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Ui.CardHi;
            public override Color MenuItemSelected => Ui.BorderHi;
            public override Color MenuItemSelectedGradientBegin => Ui.BorderHi;
            public override Color MenuItemSelectedGradientEnd => Ui.BorderHi;
            public override Color MenuItemBorder => Ui.BorderHi;
            public override Color MenuBorder => Ui.Border;
            public override Color ImageMarginGradientBegin => Ui.CardHi;
            public override Color ImageMarginGradientMiddle => Ui.CardHi;
            public override Color ImageMarginGradientEnd => Ui.CardHi;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

        /// <summary>Windows 10/11 深色標題列。</summary>
        public static void DarkTitleBar(Form f)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
            }
            catch { }
        }
    }

    /// <summary>圓角卡：標題 + 說明 + 一個內容控件。</summary>
    public class CardPanel : Panel
    {
        private string _title = "", _hint = "";

        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Card;
            UpdatePadding();
        }

        public string Title { get => _title; set { _title = value ?? ""; UpdatePadding(); Invalidate(); } }
        public string Hint { get => _hint; set { _hint = value ?? ""; UpdatePadding(); Invalidate(); } }
        public Control? Content { get; private set; }

        public CardPanel SetContent(Control c)
        {
            Content = c;
            c.Dock = DockStyle.Fill;
            Controls.Add(c);
            return this;
        }

        private void UpdatePadding()
        {
            int top = Ui.S(16);
            if (_title.Length > 0) top += Ui.F(11f, FontStyle.Bold).Height;
            if (_hint.Length > 0) top += Ui.S(4) + Ui.F(9f).Height;
            top += _title.Length > 0 || _hint.Length > 0 ? Ui.S(14) : 0;
            Padding = new Padding(Ui.S(18), top, Ui.S(18), Ui.S(16));
        }

        public virtual int MeasureHeight(int width)
        {
            int inner = Math.Max(10, width - Padding.Horizontal);
            int h = Content?.GetPreferredSize(new Size(inner, 0)).Height ?? 0;
            return Padding.Vertical + h;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Ui.Bg);
            Ui.Smooth(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Ui.Round(r, Ui.S(10f)))
            {
                using var b = new SolidBrush(Ui.Card);
                g.FillPath(b, path);
                using var pen = new Pen(Ui.Border, 1f);
                g.DrawPath(pen, path);
            }
            int x = Ui.S(18), y = Ui.S(16);
            if (_title.Length > 0)
            {
                var tf = Ui.F(11f, FontStyle.Bold);
                TextRenderer.DrawText(g, _title, tf, new Point(x, y), Ui.Text, TextFormatFlags.NoPadding);
                y += tf.Height + Ui.S(4);
            }
            if (_hint.Length > 0)
            {
                var hf = Ui.F(9f);
                TextRenderer.DrawText(g, _hint, hf, new Rectangle(x, y, Math.Max(0, Width - x * 2), hf.Height + 2), Ui.TextDim,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }
    }

    /// <summary>一頁：由上至下排卡，可以捲動。</summary>
    public class CardStack : Panel
    {
        private readonly List<CardPanel[]> _rows = new();
        private bool _inLayout;

        public CardStack()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            AutoScroll = true;
            BackColor = Ui.Bg;
            Padding = new Padding(Ui.S(24), Ui.S(20), Ui.S(24), Ui.S(40)); // 底部留白
        }

        public void AddRow(params CardPanel[] cards)
        {
            _rows.Add(cards);
            foreach (var c in cards) Controls.Add(c);
            PerformLayout();
        }

        /// <summary>移除包含呢張卡嘅一行（卡會被 Dispose）。</summary>
        public void RemoveRow(CardPanel card)
        {
            var row = _rows.FirstOrDefault(r => r.Contains(card));
            if (row == null) return;
            _rows.Remove(row);
            foreach (var c in row)
            {
                Controls.Remove(c);
                c.Dispose();
            }
            PerformLayout();
            Invalidate(true);
        }

        private bool _relayoutQueued;

        protected override void OnLayout(LayoutEventArgs e)
        {
            if (_inLayout) { base.OnLayout(e); return; }
            _inLayout = true;
            bool changed = false;
            try
            {
                int gap = Ui.S(16);
                // 永遠預留捲軸嘅闊度：唔係捲軸一出現，啲掣換行令內容變高，就會計少咗高度而被截
                int sb = VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth;
                int avail = ClientSize.Width - Padding.Horizontal - sb;
                if (avail >= 50)
                {
                    int y = Padding.Top + AutoScrollPosition.Y, x0 = Padding.Left + AutoScrollPosition.X, total = Padding.Top;
                    foreach (var row in _rows)
                    {
                        int w = (avail - gap * (row.Length - 1)) / row.Length;
                        int h = row.Max(c => c.MeasureHeight(w));
                        for (int i = 0; i < row.Length; i++) row[i].SetBounds(x0 + i * (w + gap), y, w, h);
                        y += h + gap;
                        total += h + gap;
                    }
                    total += Padding.Bottom - gap;
                    var min = new Size(0, total);
                    if (AutoScrollMinSize != min) { AutoScrollMinSize = min; changed = true; }
                }
            }
            finally { _inLayout = false; }
            // 卡排好位之後先俾 ScrollableControl 計捲軸（先後次序調轉咗就會唔出捲軸）
            base.OnLayout(e);
            // 內容高度變咗：等呢輪事件完再排多次，確保捲軸即刻出現 / 消失
            if (changed && IsHandleCreated && !_relayoutQueued)
            {
                _relayoutQueued = true;
                BeginInvoke(() =>
                {
                    _relayoutQueued = false;
                    PerformLayout();
                    Invalidate(true);
                });
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PerformLayout();
        }
    }

    /// <summary>輸入框外殼：圓角邊框，入面放 TextBox / NumericUpDown / ComboBox。</summary>
    public class InputHost : Panel
    {
        private readonly Control _inner;
        private bool _focused, _hover;

        public InputHost(Control inner, int width)
        {
            _inner = inner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Input;
            Size = new Size(width, Ui.S(34));
            Margin = new Padding(Ui.S(3));
            inner.BackColor = Ui.Input;
            inner.ForeColor = Ui.Text;
            inner.Font = Ui.F(9.75f);
            switch (inner)
            {
                case TextBox tb:
                    tb.BorderStyle = BorderStyle.None;
                    break;
                case NumericUpDown nud:
                    nud.BorderStyle = BorderStyle.None;
                    break;
                case ComboBox cb:
                    cb.FlatStyle = FlatStyle.Flat;
                    cb.DrawMode = DrawMode.OwnerDrawFixed;
                    cb.ItemHeight = Ui.S(22);
                    cb.DrawItem += DrawComboItem;
                    break;
            }
            inner.GotFocus += (_, _) => { _focused = true; Invalidate(); };
            inner.LostFocus += (_, _) => { _focused = false; Invalidate(); };
            inner.MouseEnter += (_, _) => { _hover = true; Invalidate(); };
            inner.MouseLeave += (_, _) => { _hover = false; Invalidate(); };
            Controls.Add(inner);
        }

        public Control Inner => _inner;

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_inner == null) return;
            int pad = _inner is ComboBox ? Ui.S(3) : Ui.S(10);
            int w = Width - pad * 2;
            _inner.SetBounds(pad, Math.Max(0, (Height - _inner.Height) / 2), Math.Max(10, w), _inner.Height);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _inner.Focus(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Field.BackOf(Parent));
            Ui.Smooth(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using var path = Ui.Round(r, Ui.S(7f));
            using var b = new SolidBrush(Ui.Input);
            g.FillPath(b, path);
            using var pen = new Pen(_focused ? Ui.Accent : _hover ? Ui.BorderHi : Ui.Border, _focused ? Ui.S(1.5f) : 1f);
            g.DrawPath(pen, path);
        }

        private static Color EffectiveBack(Control c)
        {
            for (var p = c; p != null; p = p.Parent)
            {
                if (p is CardPanel) return Ui.Card;
                if (p.BackColor.A == 255) return p.BackColor;
            }
            return Ui.Bg;
        }

        private static void DrawComboItem(object? sender, DrawItemEventArgs e)
        {
            if (sender is not ComboBox cb) return;
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(sel ? Ui.BorderHi : Ui.Input)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index >= 0)
            {
                var text = cb.GetItemText(cb.Items[e.Index]);
                var r = new Rectangle(e.Bounds.X + Ui.S(6), e.Bounds.Y, e.Bounds.Width - Ui.S(6), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, text, cb.Font, r, Ui.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }
    }

    public enum BtnKind { Default, Primary, Success, Danger, Ghost }

    /// <summary>圓角按鈕。</summary>
    public class FancyButton : Button
    {
        private BtnKind _kind;
        private bool _hover, _down;

        public FancyButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
            ForeColor = Color.White;
            Font = Ui.F(10f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            Size = new Size(Ui.S(96), Ui.S(34));
        }

        public BtnKind Kind { get => _kind; set { _kind = value; Invalidate(); } }

        /// <summary>Ghost 款用虛線邊（同「+」加圖片格一致）。</summary>
        public bool Dashed { get; set; }

        protected override bool ShowFocusCues => false;

        public override Size GetPreferredSize(Size proposed)
        {
            var t = TextRenderer.MeasureText(Text, Font);
            return new Size(t.Width + Ui.S(36), Ui.S(34));
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Field.BackOf(Parent));
            Ui.Smooth(g);
            if (_kind == BtnKind.Ghost)
            {
                var gr = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using var gp = Ui.Round(gr, Ui.S(7f));
                using (var gb = new SolidBrush(_hover && Enabled ? Ui.CardHi : Ui.Input)) g.FillPath(gb, gp);
                using (var pen = new Pen(_hover && Enabled ? Ui.Accent : Ui.Border, Dashed ? Ui.S(1.2f) : 1f))
                {
                    if (Dashed) pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, gp);
                }
                TextRenderer.DrawText(g, Text, Font, Rectangle.Round(gr), !Enabled ? Ui.TextFaint : _hover ? Ui.Text : Ui.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            Color baseCol = _kind switch
            {
                BtnKind.Primary => Ui.Accent,
                BtnKind.Success => Ui.Success,
                BtnKind.Danger => Ui.Danger,
                _ => Ui.CardHi
            };
            if (!Enabled) baseCol = Ui.CardHi;
            else if (_down) baseCol = ControlPaint.Dark(baseCol, 0.1f);
            else if (_hover) baseCol = ControlPaint.Light(baseCol, 0.25f);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using var path = Ui.Round(r, Ui.S(8f));
            using (var b = new SolidBrush(baseCol)) g.FillPath(b, path);
            if (_kind == BtnKind.Default || !Enabled)
            {
                using var pen = new Pen(Ui.Border, 1f);
                g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), Enabled ? ForeColor : Ui.TextFaint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>有自己底色嘅區塊（例如循環 / IF 框）：入面嘅控件畫圓角時用呢個顏色做底。</summary>
    public interface ITinted
    {
        Color Tint { get; }
    }

    /// <summary>
    /// 有色底嘅圓角區塊：底色由外面一層嘅底色混少少主題色，一層層 nest 會越嚟越明顯。
    /// </summary>
    public class TintPanel : Panel, ITinted
    {
        private readonly Color _hue;
        private readonly float _amount;

        public TintPanel(Color hue, float amount = 0.10f)
        {
            _hue = hue;
            _amount = amount;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public Color Tint => Ui.Mix(Field.BackOf(Parent), _hue, _amount);
        public Color Edge => Ui.Mix(Field.BackOf(Parent), _hue, 0.35f);
        public int Pad => Ui.S(12);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Field.BackOf(Parent));
            Ui.Smooth(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using var path = Ui.Round(r, Ui.S(9f));
            using (var b = new SolidBrush(Tint)) g.FillPath(b, path);
            using (var pen = new Pen(Edge, 1f)) g.DrawPath(pen, path);
            base.OnPaint(e);
        }
    }

    /// <summary>輸入框外框（同 InputHost 一樣嘅樣）。</summary>
    public static class Field
    {
        public static void Paint(Control c, Graphics g, bool focused, bool hover)
        {
            g.Clear(BackOf(c.Parent));
            Ui.Smooth(g);
            var r = new RectangleF(0.5f, 0.5f, c.Width - 1.5f, c.Height - 1.5f);
            using var path = Ui.Round(r, Ui.S(7f));
            using (var b = new SolidBrush(Ui.Input)) g.FillPath(b, path);
            using var pen = new Pen(focused ? Ui.Accent : hover ? Ui.BorderHi : Ui.Border, focused ? Ui.S(1.5f) : 1f);
            g.DrawPath(pen, path);
        }

        public static Color BackOf(Control? c)
        {
            for (var p = c; p != null; p = p.Parent)
            {
                if (p is ITinted t) return t.Tint;
                if (p is CardPanel) return Ui.Card;
                if (p.BackColor.A == 255) return p.BackColor;
            }
            return Ui.Bg;
        }

        public static void Chevron(Graphics g, PointF center, float size, bool up, Color color)
        {
            float h = size / 2;
            var pts = up
                ? new[] { new PointF(center.X - size, center.Y + h), new PointF(center.X, center.Y - h), new PointF(center.X + size, center.Y + h) }
                : new[] { new PointF(center.X - size, center.Y - h), new PointF(center.X, center.Y + h), new PointF(center.X + size, center.Y - h) };
            using var pen = new Pen(color, Ui.S(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, pts);
        }
    }

    /// <summary>深色數字輸入框：左邊數字，右邊有間距嘅上下箭咀（撳住會連續加減、滾輪、方向鍵都得）。</summary>
    public sealed class NumberBox : Control
    {
        private readonly TextBox _text = new();
        private decimal _value, _min, _max = 100, _step = 1;
        private int _decimals;
        private bool _hover, _focused;
        private int _hotArrow, _pressArrow; // 1 = 上, -1 = 下
        private readonly Timer _repeat = new();

        public event EventHandler? ValueChanged;

        public NumberBox(int width)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            Size = new Size(width, Ui.S(34));
            Cursor = Cursors.IBeam;
            _text.BorderStyle = BorderStyle.None;
            _text.BackColor = Ui.Input;
            _text.ForeColor = Ui.Text;
            _text.Font = Ui.F(9.75f);
            _text.TextAlign = HorizontalAlignment.Right;
            _text.GotFocus += (_, _) => { _focused = true; Invalidate(); };
            _text.LostFocus += (_, _) => { _focused = false; Commit(); Invalidate(); };
            _text.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Up) { Step(1); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Down) { Step(-1); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; }
            };
            _text.KeyPress += (_, e) =>
            {
                if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar) && !(e.KeyChar == '.' && _decimals > 0)) e.Handled = true;
            };
            _text.MouseWheel += (_, e) => ForwardWheel(e); // 滾輪唔改數字，交返俾頁面捲動
            _text.MouseEnter += (_, _) => { _hover = true; Invalidate(); };
            _text.MouseLeave += (_, _) => { _hover = false; Invalidate(); };
            Controls.Add(_text);
            _repeat.Tick += (_, _) => { _repeat.Interval = 70; if (_pressArrow != 0) Step(_pressArrow); };
            ShowValue();
        }

        public decimal Minimum { get => _min; set { _min = value; Value = _value; } }
        public decimal Maximum { get => _max; set { _max = value; Value = _value; } }
        public decimal Increment { get => _step; set => _step = value; }
        public int DecimalPlaces { get => _decimals; set { _decimals = value; ShowValue(); } }

        public decimal Value
        {
            get => _value;
            set
            {
                decimal v = Math.Round(Math.Clamp(value, _min, Math.Max(_min, _max)), _decimals);
                bool changed = v != _value;
                _value = v;
                ShowValue();
                if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void ShowValue() => _text.Text = _value.ToString("F" + _decimals, System.Globalization.CultureInfo.InvariantCulture);

        private void Step(int dir) => Value = _value + dir * _step;

        private void Commit()
        {
            if (decimal.TryParse(_text.Text, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)) Value = v;
            else ShowValue();
        }

        private int ArrowW => Ui.S(22);
        private Rectangle ArrowRect => new(Width - ArrowW - Ui.S(4), Ui.S(4), ArrowW, Height - Ui.S(8));

        private int ArrowAt(Point p)
        {
            var r = ArrowRect;
            if (!r.Contains(p)) return 0;
            return p.Y < r.Y + r.Height / 2 ? 1 : -1;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_text == null) return;
            int left = Ui.S(10), right = ArrowRect.Left - Ui.S(8); // 數字同箭咀之間留位
            _text.SetBounds(left, (Height - _text.Height) / 2, Math.Max(10, right - left), _text.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Field.Paint(this, g, _focused, _hover);
            var r = ArrowRect;
            using (var pen = new Pen(Ui.Border, 1f)) g.DrawLine(pen, r.Left - Ui.S(1), r.Top + Ui.S(2), r.Left - Ui.S(1), r.Bottom - Ui.S(2));
            float cx = r.Left + r.Width / 2f, q = r.Height / 4f;
            Field.Chevron(g, new PointF(cx, r.Top + q + Ui.S(1f)), Ui.S(3.5f), true, _hotArrow == 1 ? Ui.Accent : Ui.TextDim);
            Field.Chevron(g, new PointF(cx, r.Bottom - q - Ui.S(1f)), Ui.S(3.5f), false, _hotArrow == -1 ? Ui.Accent : Ui.TextDim);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int a = ArrowAt(e.Location);
            Cursor = a != 0 ? Cursors.Hand : Cursors.IBeam;
            if (a != _hotArrow) { _hotArrow = a; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false; _hotArrow = 0; _pressArrow = 0; _repeat.Stop();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _text.Focus();
            int a = ArrowAt(e.Location);
            if (a == 0 || e.Button != MouseButtons.Left) return;
            Commit();
            _pressArrow = a;
            Step(a);
            _repeat.Interval = 400;
            _repeat.Start();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _pressArrow = 0;
            _repeat.Stop();
        }

        protected override void OnMouseWheel(MouseEventArgs e) => ForwardWheel(e);

        /// <summary>滾輪唔改數字：捲動外面嘅頁面（費事捲頁時誤改咗設定）。</summary>
        private void ForwardWheel(MouseEventArgs e)
        {
            if (e is HandledMouseEventArgs h) h.Handled = true;
            for (Control? p = Parent; p != null; p = p.Parent)
            {
                if (p is ScrollableControl sc && sc.AutoScroll && sc.VerticalScroll.Visible)
                {
                    int y = -sc.AutoScrollPosition.Y - e.Delta;
                    sc.AutoScrollPosition = new Point(-sc.AutoScrollPosition.X, Math.Max(0, y));
                    return;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _repeat.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>側欄分頁掣。</summary>
    public class NavItem : Control
    {
        private bool _active, _hover;

        public NavItem(string text)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            Text = text;
            Font = Ui.F(10f);
            Cursor = Cursors.Hand;
            Height = Ui.S(40);
        }

        public bool Active { get => _active; set { _active = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Panel);
            Ui.Smooth(g);
            var r = new RectangleF(Ui.S(10f), Ui.S(3f), Width - Ui.S(20f), Height - Ui.S(6f));
            if (_active || _hover)
            {
                using var path = Ui.Round(r, Ui.S(7f));
                using var b = new SolidBrush(_active ? Ui.CardHi : Ui.Alpha(Ui.CardHi, 140));
                g.FillPath(b, path);
            }
            if (_active)
            {
                var bar = new RectangleF(r.X, r.Y + Ui.S(8f), Ui.S(3f), r.Height - Ui.S(16f));
                using var path = Ui.Round(bar, Ui.S(1.5f));
                using var b = Ui.AccentBrush(new RectangleF(bar.X, bar.Y, Ui.S(3f), bar.Height));
                g.FillPath(b, path);
            }
            TextRenderer.DrawText(g, Text, _active ? Ui.F(10f, FontStyle.Bold) : Font,
                new Rectangle((int)r.X + Ui.S(16), (int)r.Y, (int)r.Width - Ui.S(16), (int)r.Height),
                _active ? Ui.Text : Ui.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
}
