using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool;

/// <summary>
/// 首頁：上面一個歡迎橫額，下面「你的工具」一格格（撳入去），再落係未安裝嘅建議同小貼士。
/// </summary>
internal sealed class HomePage : Panel
{
    private readonly Action<string> _go;
    private readonly Banner _banner;
    private readonly Label _mineTitle, _tipsTitle;
    private readonly List<Tile> _mine = new();
    private readonly TipsBox _tips;
    private bool _ready;

    private sealed record ToolInfo(string Key, string? Ext, string Name, string Desc);

    public HomePage(Action<string> go)
    {
        _go = go;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        AutoScroll = true;
        _banner = new Banner(go);
        _mineTitle = Section(Lang.T("你的工具"));
        _tipsTitle = Section(Lang.T("小貼士"));
        _tips = new TipsBox();
        Controls.Add(_banner);
        Controls.Add(_mineTitle);
        Controls.Add(_tipsTitle);
        Controls.Add(_tips);
        Rebuild();
        _ready = true;
        Extensions.Changed += OnExt;
        Disposed += (_, _) => Extensions.Changed -= OnExt;
        PerformLayout();
    }

    private static Label Section(string t) => Ui.Text_(t, Ui.Text, Ui.F(11.5f, FontStyle.Bold), new Padding(0));

    private void OnExt()
    {
        if (IsDisposed) return;
        BeginInvoke(() => { Rebuild(); _tips.Rebuild(); PerformLayout(); Invalidate(true); });
    }

    private static IEnumerable<ToolInfo> AllTools()
    {
        foreach (var e in Extensions.All)
        {
            yield return new ToolInfo(e.Id, e.Id, e.Name, e.Desc);
            if (e.Id == Extensions.Scripts)
                yield return new ToolInfo("ai", null, Lang.T("AI 助手"),
                    Lang.T("用你嘅 Claude、ChatGPT、Gemini 或者免費本地 AI 問嘢、寫程式、語音對話。"));
        }
    }

    private void Rebuild()
    {
        SuspendLayout();
        foreach (var t in _mine) { Controls.Remove(t); t.Dispose(); }
        _mine.Clear();
        // 淨係顯示已安裝嘅工具（AI 助手係內置）
        foreach (var t in AllTools().Where(t => t.Ext == null || Extensions.IsInstalled(t.Ext)))
        {
            var key = t.Key;
            _mine.Add(new Tile(t.Key, t.Name, t.Desc, Tile.Mode.Open, () => _go(key)));
        }
        _mine.Add(new Tile("add", Lang.T("加入工具"), Lang.T("去工具庫揀更多工具"), Tile.Mode.Add, () => _go("extensions")));
        foreach (var t in _mine) Controls.Add(t);
        _banner.Count = _mine.Count - 1;
        ResumeLayout(false);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (!_ready) return;
        int pad = Ui.S(28), gap = Ui.S(14);
        int w = Math.Max(Ui.S(300), ClientSize.Width - pad * 2);
        var o = AutoScrollPosition;
        int y = Ui.S(22);
        _banner.SetBounds(pad + o.X, y + o.Y, w, Ui.S(150));
        y += _banner.Height + Ui.S(26);

        int Grid(List<Tile> tiles, int top, int min, int th)
        {
            int cols = Math.Max(1, (w + gap) / (min + gap));
            int tw = (w - gap * (cols - 1)) / cols;
            for (int i = 0; i < tiles.Count; i++)
                tiles[i].SetBounds(pad + (i % cols) * (tw + gap) + o.X, top + (i / cols) * (th + gap) + o.Y, tw, th);
            int rows = (tiles.Count + cols - 1) / cols;
            return top + rows * (th + gap) - gap;
        }

        _mineTitle.Location = new Point(pad + o.X, y + o.Y);
        y += _mineTitle.PreferredHeight + Ui.S(12);
        y = Grid(_mine, y, Ui.S(230), Ui.S(112)) + Ui.S(28);

        _tipsTitle.Location = new Point(pad + o.X, y + o.Y);
        y += _tipsTitle.PreferredHeight + Ui.S(12);
        int th2 = _tips.Measure(w);
        _tips.SetBounds(pad + o.X, y + o.Y, w, th2);
        y += th2 + Ui.S(36);
        var want = new Size(0, y);
        if (AutoScrollMinSize != want) AutoScrollMinSize = want;
    }

    // ------------------------------------------------------------------
    //  歡迎橫額
    // ------------------------------------------------------------------
    private sealed class Banner : Control
    {
        private readonly FancyButton _lib, _settings;
        private int _count;

        public int Count { get => _count; set { _count = value; Invalidate(); } }

        public Banner(Action<string> go)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            _lib = new FancyButton { Text = Lang.T("工具庫"), Kind = BtnKind.Primary, Font = Ui.F(10f, FontStyle.Bold) };
            _lib.Size = new Size(Math.Max(Ui.S(110), _lib.GetPreferredSize(Size.Empty).Width), Ui.S(38));
            _lib.Click += (_, _) => go("extensions");
            _settings = new FancyButton { Text = Lang.T("設定"), Kind = BtnKind.Ghost, Font = Ui.F(10f) };
            _settings.Size = new Size(Math.Max(Ui.S(90), _settings.GetPreferredSize(Size.Empty).Width), Ui.S(38));
            _settings.Click += (_, _) => go("settings");
            Controls.Add(_lib);
            Controls.Add(_settings);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_lib == null) return;
            int p = Ui.S(26);
            bool narrow = Width < Ui.S(620);
            if (narrow)
            {
                _lib.Location = new Point(p, Height - p - _lib.Height + Ui.S(6));
                _settings.Location = new Point(_lib.Right + Ui.S(10), _lib.Top);
            }
            else
            {
                _settings.Location = new Point(Width - p - _settings.Width, (Height - _settings.Height) / 2);
                _lib.Location = new Point(_settings.Left - Ui.S(10) - _lib.Width, _settings.Top);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Ui.Bg);
            Ui.Smooth(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Ui.Round(r, Ui.S(16f)))
            {
                // 底色：卡片色 + 淡淡藍紫漸變
                using (var b = new SolidBrush(Ui.Card)) g.FillPath(b, path);
                using (var grad = new LinearGradientBrush(r, Ui.Alpha(Ui.Accent, Ui.Light ? 40 : 60), Ui.Alpha(Ui.Accent2, Ui.Light ? 18 : 26), 20f))
                    g.FillPath(grad, path);
                // 右上角裝飾圓
                var old = g.Clip;
                g.SetClip(path);
                using (var c1 = new SolidBrush(Ui.Alpha(Ui.Accent2, Ui.Light ? 22 : 30)))
                    g.FillEllipse(c1, Width - Ui.S(230), -Ui.S(120), Ui.S(320), Ui.S(320));
                using (var c2 = new SolidBrush(Ui.Alpha(Ui.Accent, Ui.Light ? 18 : 26)))
                    g.FillEllipse(c2, Width - Ui.S(120), Ui.S(60), Ui.S(200), Ui.S(200));
                g.Clip = old;
                using var pen = new Pen(Ui.Alpha(Ui.Accent, 70), 1f);
                g.DrawPath(pen, path);
            }
            int p = Ui.S(26), y = Ui.S(24);
            var hello = Lang.T("歡迎使用 ") + Program.AppName;
            var tf = Ui.F(17f, FontStyle.Bold);
            TextRenderer.DrawText(g, hello, tf, new Point(p, y), Ui.Text, TextFormatFlags.NoPadding);
            y += tf.Height + Ui.S(8);
            var sub = Program.Version + "  ·  " + Lang.T("開發者：") + Program.Author + "  ·  " + Lang.F("已安裝 {0} 個工具", _count);
            TextRenderer.DrawText(g, sub, Ui.F(9.5f), new Point(p, y), Ui.TextDim, TextFormatFlags.NoPadding);
            y += Ui.F(9.5f).Height + Ui.S(10);
            int textW = (Width < Ui.S(620) ? Width : _lib.Left) - p * 2;
            TextRenderer.DrawText(g, Lang.T("自動化腳本、錄影、改鍵、AI 助手…一個程式搞掂。撳下面嘅工具開始，或者去工具庫加更多。"),
                Ui.F(10f), new Rectangle(p, y, Math.Max(10, textW), Ui.F(10f).Height * 2 + 4), Ui.TextDim,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    // ------------------------------------------------------------------
    //  工具格
    // ------------------------------------------------------------------
    private sealed class Tile : Control
    {
        public enum Mode { Open, Suggest, Add }

        private readonly string _key, _name, _desc;
        private readonly Mode _mode;
        private bool _hover, _down;

        public Tile(string key, string name, string desc, Mode mode, Action click)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            _key = key;
            _name = name;
            _desc = desc;
            _mode = mode;
            Cursor = Cursors.Hand;
            Click += (_, _) => click();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Ui.Bg);
            Ui.Smooth(g);
            float lift = _hover && !_down ? Ui.S(2f) : 0f;
            var r = new RectangleF(0.5f, 0.5f + (Ui.S(2f) - lift), Width - 1.5f, Height - 1.5f - Ui.S(2f));
            var (glyph, hue) = Extensions.Look(_key);
            using (var path = Ui.Round(r, Ui.S(12f)))
            {
                if (_mode == Mode.Add)
                {
                    using var b = new SolidBrush(_hover ? Ui.CardHi : Ui.Bg);
                    g.FillPath(b, path);
                    using var pen = new Pen(_hover ? Ui.Accent : Ui.BorderHi, Ui.S(1.2f)) { DashStyle = DashStyle.Dash };
                    g.DrawPath(pen, path);
                }
                else
                {
                    using var b = new SolidBrush(_hover ? Ui.Mix(Ui.Card, hue, 0.06f) : Ui.Card);
                    g.FillPath(b, path);
                    using var pen = new Pen(_hover ? Ui.Mix(Ui.Border, hue, 0.6f) : Ui.Border, 1f);
                    g.DrawPath(pen, path);
                }
            }
            int p = Ui.S(16);
            float top = r.Y;
            if (_mode == Mode.Add)
            {
                var plusF = Ui.F(18f, FontStyle.Bold);
                int cy = (int)(top + r.Height / 2);
                TextRenderer.DrawText(g, "+", plusF, new Rectangle(0, (int)top + Ui.S(14), Width, plusF.Height), _hover ? Ui.Accent : Ui.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, _name, Ui.F(10f, FontStyle.Bold), new Rectangle(0, cy + Ui.S(4), Width, Ui.S(22)), _hover ? Ui.Text : Ui.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                return;
            }
            bool big = _mode == Mode.Open;
            float isz = big ? Ui.S(42f) : Ui.S(34f);
            var ir = new RectangleF(p, top + (big ? p : (r.Height - isz) / 2), isz, isz);
            using (var ip = Ui.Round(ir, Ui.S(10f)))
            {
                using var ib = new LinearGradientBrush(ir, Ui.Mix(Ui.Card, hue, _mode == Mode.Suggest ? 0.16f : 0.30f),
                    Ui.Mix(Ui.Card, hue, _mode == Mode.Suggest ? 0.08f : 0.16f), 90f);
                g.FillPath(ib, ip);
            }
            TextRenderer.DrawText(g, glyph, Ui.F(big ? 14f : 12f, FontStyle.Bold), Rectangle.Round(ir), Ui.OnTint(hue),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int tx = (int)ir.Right + Ui.S(12);
            int tw = Width - tx - p;
            var nf = Ui.F(big ? 11f : 10f, FontStyle.Bold);
            if (big)
            {
                TextRenderer.DrawText(g, _name, nf, new Rectangle(tx, (int)ir.Y + Ui.S(1), tw, nf.Height + 2), Ui.Text,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                // 箭咀
                TextRenderer.DrawText(g, "→", Ui.F(11f, FontStyle.Bold), new Rectangle(Width - p - Ui.S(20), (int)ir.Y, Ui.S(20), nf.Height + 2),
                    _hover ? hue : Ui.TextFaint, TextFormatFlags.NoPadding | TextFormatFlags.Right);
                int dy = (int)ir.Bottom - Ui.S(18);
                var dr = new Rectangle(tx, (int)ir.Y + nf.Height + Ui.S(6), tw, (int)(top + r.Height) - p - ((int)ir.Y + nf.Height + Ui.S(6)));
                TextRenderer.DrawText(g, _desc, Ui.F(9f), dr, Ui.TextDim,
                    TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl);
            }
            else
            {
                int cy = (int)(top + r.Height / 2);
                TextRenderer.DrawText(g, _name, nf, new Rectangle(tx, cy - nf.Height - Ui.S(1), tw, nf.Height + 2), Ui.Text,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, _hover ? Lang.T("去工具庫安裝 →") : Lang.T("未安裝"), Ui.F(8.75f), new Rectangle(tx, cy + Ui.S(3), tw, Ui.S(18)),
                    _hover ? Ui.Accent : Ui.TextFaint, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }
    }

    // ------------------------------------------------------------------
    //  小貼士
    // ------------------------------------------------------------------
    private sealed class TipsBox : Control
    {
        private readonly (string? Ext, string Text)[] _all =
        {
            (Extensions.Scripts, Lang.T("緊急停止：腳本行緊時連撳 3 下 Esc，或者將滑鼠推去螢幕左上角，所有腳本即刻停。")),
            (Extensions.Scripts, Lang.T("撳錯刪咗步驟？修改頁右下角「↶ 復原」可以還原上一次改動。")),
            (Extensions.Replay, Lang.T("精彩重播開住就會一直喺背景錄，撳「儲存重播」快捷鍵先會存低最近嗰段。")),
            (Extensions.Remap, Lang.T("遊戲用管理員權限開嘅話，MultiTool 都要用管理員身份開，點擊同改鍵先會生效。")),
            (Extensions.Clipboard, Lang.T("剪貼簿紀錄：用快捷鍵喺任何地方叫出細視窗，撳 1-9 直接貼上。")),
            (null, Lang.T("每個工具嘅快捷鍵可以喺「設定 → 快捷鍵」一次過睇同改，亦可以揀喺工具頁以外用唔用得。")),
            (null, Lang.T("左邊側欄可以拖住上下搬，排你鍾意嘅次序。")),
            (null, Lang.T("所有設定同腳本都存喺 exe 隔籬嘅 data 資料夾；出錯會寫入 data\\error.log。"))
        };

        private string[] _tips = Array.Empty<string>();

        /// <summary>淨係顯示已安裝工具相關嘅貼士。</summary>
        public void Rebuild()
        {
            _tips = _all.Where(t => t.Ext == null || Extensions.IsInstalled(t.Ext)).Select(t => t.Text).ToArray();
            Invalidate();
        }

        public TipsBox()
        {
            Rebuild();
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
        }

        private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;

        public int Measure(int w)
        {
            int inner = w - Ui.S(36) - Ui.S(22);
            int h = Ui.S(16);
            foreach (var t in _tips) h += TextRenderer.MeasureText(t, Ui.F(9.75f), new Size(Math.Max(10, inner), 0), Flags).Height + Ui.S(10);
            return h + Ui.S(8);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Ui.Bg);
            Ui.Smooth(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Ui.Round(r, Ui.S(12f)))
            {
                using var b = new SolidBrush(Ui.Card);
                g.FillPath(b, path);
                using var pen = new Pen(Ui.Border, 1f);
                g.DrawPath(pen, path);
            }
            int x = Ui.S(18), y = Ui.S(16), inner = Width - x * 2 - Ui.S(22);
            foreach (var t in _tips)
            {
                using (var dot = new SolidBrush(Ui.Accent)) g.FillEllipse(dot, x + Ui.S(2), y + Ui.S(6), Ui.S(6), Ui.S(6));
                var sz = TextRenderer.MeasureText(t, Ui.F(9.75f), new Size(Math.Max(10, inner), 0), Flags);
                TextRenderer.DrawText(g, t, Ui.F(9.75f), new Rectangle(x + Ui.S(20), y, inner, sz.Height), Ui.TextDim, Flags);
                y += sz.Height + Ui.S(10);
            }
        }
    }
}
