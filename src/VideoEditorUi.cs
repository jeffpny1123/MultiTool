using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.Tools;
using MultiTool.UI;

namespace MultiTool.Video;

/// <summary>揀咗「第 i 段去下一段」嘅轉場。</summary>
internal sealed record TransSel(int Index);

// =====================================================================
//  時間軸：文字 / 影片 / 音樂三條軌
// =====================================================================
internal sealed class EdTimeline : Control
{
    public EdProject P = new();
    public double Pps = 50;             // 每秒幾多 px
    private int _scroll;
    public double Playhead;
    public object? Selected;
    private readonly HScrollBar _bar = new() { Dock = DockStyle.Bottom };

    public event Action<double, bool>? Seek;        // (時間, 放手)
    public event Action? SelectionChanged, BeforeChange, Changed;

    private int HeadW => Ui.S(80);
    private int RulerH => Ui.S(26);
    private int RowGap => Ui.S(6);
    // ---- 軌道排位（由上至下）：文字 n…1、圖片 n…1、影片 n…2、影片 1（主）、音軌 1…n ----
    internal readonly record struct LaneInfo(EdTrack Kind, int Row, int Y, int H, bool Phantom = false);
    /// <summary>拖緊片段時：喺同類軌最外面多出一條「新軌」，放落去就自動加軌。</summary>
    private EdTrack? PhantomKind => _drag == DragKind.Move && _moved && _dragObj != null && P.RowCount(KindOf(_dragObj)) < EdProject.MaxRows ? KindOf(_dragObj) : null;
    private int SmallH => Ui.S(30);
    private int VidH => Ui.S(60);
    private int SubGap => Ui.S(3);
    private int LaneH(EdTrack k, int row) => k == EdTrack.Vid ? (row == 0 ? VidH : Ui.S(40)) : SmallH;

    public List<LaneInfo> Lanes()
    {
        var list = new List<LaneInfo>();
        int y = RulerH + RowGap;
        foreach (var k in new[] { EdTrack.Text, EdTrack.Vid, EdTrack.Aud })
        {
            int n = P.RowCount(k);
            int shown = PhantomKind == k ? n + 1 : n;
            var rows = k == EdTrack.Aud ? Enumerable.Range(0, shown) : Enumerable.Range(0, shown).Reverse();
            foreach (int r in rows)
            {
                bool ph = r >= n;
                int h = ph ? Ui.S(26) : LaneH(k, r);
                list.Add(new LaneInfo(k, r, y, h, ph));
                y += h + SubGap;
            }
            y += RowGap - SubGap;
        }
        return list;
    }

    private LaneInfo LaneOf(EdTrack k, int row)
    {
        var ls = Lanes();
        return ls.FirstOrDefault(l => l.Kind == k && l.Row == row) is var l && l.H > 0 ? l : ls.Last(x => x.Kind == k);
    }
    private int VidY => LaneOf(EdTrack.Vid, 0).Y;
    private int LanesBottom { get { var l = Lanes()[^1]; return l.Y + l.H; } }
    public int WantHeight => LanesBottom + RowGap + _bar.Height;
    private LaneInfo? LaneAt(int y)
    {
        foreach (var l in Lanes()) if (y >= l.Y - SubGap / 2 && y < l.Y + l.H + SubGap) return l;
        return null;
    }
    /// <summary>軌名旁邊「+」：加多一條同類嘅軌（喺最新嗰條軌名度顯示）。</summary>
    private Rectangle PlusRect(LaneInfo l) => new(HeadW - Ui.S(24), l.Y + (l.H - Ui.S(20)) / 2, Ui.S(20), Ui.S(20));
    private bool ShowsPlus(LaneInfo l) => l.Row == P.RowCount(l.Kind) - 1 && P.RowCount(l.Kind) < EdProject.MaxRows;
    private static EdTrack KindOf(object o) => o switch { EdText => EdTrack.Text, EdOverlay => EdTrack.Vid, EdMusic => EdTrack.Aud, _ => EdTrack.Vid };
    private static int RowOf(object o) => o switch { EdText t => t.Row, EdOverlay ov => ov.Row, EdMusic m => m.Row, _ => 0 };
    private static void SetRow(object o, int r) { switch (o) { case EdText t: t.Row = r; break; case EdOverlay ov: ov.Row = r; break; case EdMusic m: m.Row = r; break; } }
    /// <summary>放低檔案嘅位置：邊條軌 + 時間。</summary>
    public (EdTrack Kind, int Row, double Time)? DropTarget(Point client)
    {
        var l = LaneAt(client.Y);
        if (l == null || client.X < HeadW) return null;
        return (l.Value.Kind, l.Value.Row, T(client.X));
    }
    // ---------- 拖住片段：片段跟住滑鼠，放手先至換位置 / 換軌 ----------
    private float _grabDy;
    private double _dropStart;
    private int Faint(object o) => _drag == DragKind.Move && _moved && _dragObj == o ? 60 : 255;

    private static double LenOf(object o) => o switch { EdClip c => c.Len, EdText t => t.Dur, EdOverlay ov => ov.Dur, EdMusic m => m.Len, _ => 1 };
    private static string NameOf(object o) => o switch
    {
        EdClip c => c.Name, EdText t => "T  " + t.Text.Replace("\n", " "), EdOverlay ov => (ov.IsVideo ? "▶  " : "▣  ") + ov.Name, EdMusic m => "♪  " + m.Name, _ => ""
    };
    private Color HueOf(object o) => o switch { EdText => TextHue, EdMusic => MusHue, EdOverlay ov when !ov.IsVideo => ImgHue, EdClip c when c.IsImage => Ui.Accent, _ => Ui.Accent };

    /// <summary>而家放手會落邊度：目標軌 + 開始時間；InsertMain = 插入主影片軌。</summary>
    private (LaneInfo Lane, double Start, bool InsertMain) DropPlan(Point m)
    {
        var obj = _dragObj!;
        var orig = LaneOf(KindOf(obj), RowOf(obj));
        var at = LaneAt(m.Y);
        if (obj is EdClip c)
        {
            if (at is { } l && CanTake(l, c)) return (l, SnapMove(Math.Max(0, T((int)(m.X - _grabDx))), c.Len, c), false);
            return (orig, 0, true);
        }
        if (obj is EdOverlay && at is { Kind: EdTrack.Vid, Row: 0 }) return (at.Value, 0, true);
        var lane = at is { } l2 && l2.Kind == KindOf(obj) && !(l2.Kind == EdTrack.Vid && l2.Row == 0) ? l2 : orig;
        return (lane, _dropStart, false);
    }

    private void DrawDragGhost(Graphics g)
    {
        if (_drag != DragKind.Move || !_moved || _dragObj == null) return;
        var obj = _dragObj;
        double len = LenOf(obj);
        var plan = DropPlan(_mouse);
        var src = LaneOf(KindOf(obj), RowOf(obj));
        // 落點提示
        if (plan.InsertMain)
        {
            var starts = P.Starts();
            int ins = InsertIndex(_mouse.X);
            float ix = ins >= P.Clips.Count ? (float)X(P.VideoEnd) : (float)X(starts[ins]);
            using var pen = new Pen(Ui.Accent2, Ui.S(3f));
            g.DrawLine(pen, ix, VidY - Ui.S(2), ix, VidY + VidH + Ui.S(2));
        }
        else
        {
            var tr = new RectangleF((float)X(plan.Start), plan.Lane.Y + Ui.S(2), (float)(len * Pps), plan.Lane.H - Ui.S(4));
            using var path = Ui.Round(tr, Ui.S(6f));
            using (var b = new SolidBrush(Color.FromArgb(40, Ui.Accent2))) g.FillPath(b, path);
            using (var pen = new Pen(Ui.Accent2, Ui.S(1.5f)) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
        }
        // 跟住滑鼠嘅片段（好似拎住咁）
        var r = new RectangleF(_mouse.X - _grabDx, _mouse.Y - _grabDy, (float)(len * Pps), src.H - Ui.S(4));
        using (var sp = Ui.Round(new RectangleF(r.X + Ui.S(3), r.Y + Ui.S(5), r.Width, r.Height), Ui.S(6f)))
        using (var sb = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillPath(sb, sp);
        Bitmap? th = obj switch { EdClip c when !c.IsImage => Thumb(c), EdOverlay o when o.IsVideo => ThumbOv(o), _ => null };
        Block(g, r, HueOf(obj), NameOf(obj), true, th, 235);
    }

    // ---------- 拖軌名改排位 ----------
    private LaneInfo _dragLane;
    /// <summary>主影片軌（影片 1）固定唔郁；其他同類有兩條或以上先拖得。</summary>
    private bool CanMoveLane(LaneInfo l) => !(l.Kind == EdTrack.Vid && l.Row == 0) && P.RowCount(l.Kind) - (l.Kind == EdTrack.Vid ? 1 : 0) > 1;

    /// <summary>滑鼠喺邊條同類軌度 → 目標 row。</summary>
    private int? LaneTarget(int y)
    {
        var same = Lanes().Where(l => l.Kind == _dragLane.Kind && !(l.Kind == EdTrack.Vid && l.Row == 0)).ToList();
        if (same.Count == 0) return null;
        if (y < same[0].Y) return same[0].Row;
        if (y >= same[^1].Y + same[^1].H) return same[^1].Row;
        foreach (var l in same) if (y >= l.Y - SubGap && y < l.Y + l.H + SubGap) return l.Row;
        return null;
    }

    /// <summary>將第 from 條軌搬去第 to 條（中間嘅順移）。</summary>
    private void MoveLane(EdTrack k, int from, int to)
    {
        int n = P.RowCount(k);
        var order = Enumerable.Range(0, n).ToList();
        order.Remove(from);
        order.Insert(to, from);
        var map = new Dictionary<int, int>();
        for (int i = 0; i < order.Count; i++) map[order[i]] = i;
        var items = Enumerable.Range(0, n).SelectMany(r => P.OnRow(k, r).Select(x => (x.Obj, r))).ToList();
        BeforeChange?.Invoke();
        foreach (var (o, r) in items) if (o is not EdClip) SetRow(o, map[r]);
        Changed?.Invoke();
    }

    private void DrawLaneDrag(Graphics g)
    {
        if (_drag != DragKind.Lane || !_moved) return;
        int? dst = LaneTarget(_mouse.Y);
        if (dst is int d && LaneOf(_dragLane.Kind, d) is var tl)
        {
            // 目標位置：上面 / 下面一條線
            bool up = tl.Y < _dragLane.Y;
            int y = up ? tl.Y - SubGap / 2 - 1 : tl.Y + tl.H + SubGap / 2;
            using var pen = new Pen(Ui.Accent2, Ui.S(3f));
            g.DrawLine(pen, 0, y, Width, y);
        }
        // 跟住滑鼠嘅軌名
        var r = new RectangleF(Ui.S(2), _mouse.Y - _grabDy, Width - Ui.S(4), _dragLane.H);
        using (var path = Ui.Round(r, Ui.S(6f)))
        {
            using (var b = new SolidBrush(Color.FromArgb(150, Ui.Accent))) g.FillPath(b, path);
            using (var pen = new Pen(Ui.Accent2, Ui.S(1.5f))) g.DrawPath(pen, path);
        }
        string name = _dragLane.Kind switch
        {
            EdTrack.Text => Lang.F("文字 {0}", _dragLane.Row + 1), EdTrack.Img => Lang.F("圖片 {0}", _dragLane.Row + 1),
            EdTrack.Vid => Lang.F("影片 {0}", _dragLane.Row + 1), _ => Lang.F("音軌 {0}", _dragLane.Row + 1)
        };
        TextRenderer.DrawText(g, name, Ui.F(8.75f, FontStyle.Bold), Rectangle.Round(new RectangleF(Ui.S(10), r.Y, HeadW, r.Height)), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>主影片軌嘅片段可唔可以拖去呢條軌（影片 → 影片 2 軌以上；相片 → 圖片軌）。</summary>
    private static bool CanTake(LaneInfo l, EdClip c) => l.Kind == EdTrack.Vid && l.Row > 0;
    private int _lastH = -1;
    public event Action? HeightChanged;

    public EdTimeline()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Ui.Card;
        Controls.Add(_bar);
        _bar.Scroll += (_, _) => { _scroll = _bar.Value; Invalidate(); };
        Cursor = Cursors.Default;
    }

    private double X(double t) => HeadW + t * Pps - _scroll;
    private double T(int x) => Math.Max(0, (x - HeadW + _scroll) / Pps);

    public void Refresh2()
    {
        int content = (int)((P.Duration + 10) * Pps);
        int view = Math.Max(1, Width - HeadW);
        _bar.Minimum = 0;
        _bar.LargeChange = view;
        _bar.SmallChange = Math.Max(1, view / 10);
        _bar.Maximum = Math.Max(view, content);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _bar.Maximum - view));
        _bar.Value = Math.Min(_scroll, _bar.Maximum);
        _bar.Visible = content > view;
        Invalidate();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Refresh2(); }

    /// <summary>播放時播放頭行出畫面就跟住捲。</summary>
    public void Follow()
    {
        double x = X(Playhead);
        if (x > Width - Ui.S(40) || x < HeadW)
        {
            _scroll = Math.Max(0, (int)(Playhead * Pps) - Ui.S(60));
            Refresh2();
        }
    }

    public void Zoom(double factor, int anchorX = -1)
    {
        if (anchorX < 0) anchorX = (int)Math.Clamp(X(Playhead), HeadW, Width);
        double t = T(anchorX);
        Pps = Math.Clamp(Pps * factor, 4, 400);
        _scroll = Math.Max(0, (int)(t * Pps - (anchorX - HeadW)));
        Refresh2();
    }

    // ---------- 縮圖 ----------
    private readonly Dictionary<string, Bitmap?> _thumbs = new();
    private readonly HashSet<string> _loading = new();

    private Bitmap? Thumb(EdClip c) => Thumb(c.Path, c.In, c.Len, VidH);
    private Bitmap? ThumbOv(EdOverlay o) => Thumb(o.Path, o.In, o.Dur, Ui.S(40));

    private Bitmap? Thumb(string path, double inPt, double len, int laneH)
    {
        string key = path + "|" + Math.Round(inPt, 1) + "|" + laneH;
        if (_thumbs.TryGetValue(key, out var b)) return b;
        if (_loading.Add(key))
        {
            double at = inPt + Math.Min(0.5, len / 2); int h = laneH - Ui.S(10);
            Task.Run(() => MediaInfo.Thumb(path, at, h)).ContinueWith(t =>
            {
                try { if (!IsDisposed) BeginInvoke(() => { _thumbs[key] = t.Result; Invalidate(); }); } catch { }
            });
        }
        return null;
    }

    // ---------- 畫 ----------
    private static readonly Color TextHue = Color.FromArgb(232, 160, 60), MusHue = Color.FromArgb(70, 180, 120), ImgHue = Color.FromArgb(170, 110, 230);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Card);
        Ui.Smooth(g);
        int w = Width;
        // 軌道底色
        void Lane(int y, int h, string name)
        {
            using (var b = new SolidBrush(Ui.Input)) g.FillRectangle(b, HeadW, y, w - HeadW, h);
            TextRenderer.DrawText(g, name, Ui.F(8.75f, FontStyle.Bold), new Rectangle(Ui.S(10), y, HeadW - Ui.S(14), h), Ui.TextDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
        var lanes = Lanes();
        foreach (var l in lanes)
        {
            if (l.Phantom)
            {
                // 新軌：虛線框
                var pr = new Rectangle(Ui.S(6), l.Y, w - Ui.S(10), l.H);
                using (var path = Ui.Round(pr, Ui.S(5f)))
                {
                    using (var b = new SolidBrush(Color.FromArgb(30, Ui.Accent2))) g.FillPath(b, path);
                    using (var pen = new Pen(Ui.Accent2, 1f) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
                }
                TextRenderer.DrawText(g, Lang.T("＋ 拖落嚟 = 新增一條軌"), Ui.F(8.5f, FontStyle.Bold), pr, Ui.Accent2,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                continue;
            }
            int n = P.RowCount(l.Kind);
            string name = l.Kind switch
            {
                EdTrack.Text => n > 1 ? Lang.F("文字 {0}", l.Row + 1) : Lang.T("文字"),
                EdTrack.Img => n > 1 ? Lang.F("圖片 {0}", l.Row + 1) : Lang.T("圖片"),
                EdTrack.Vid => n > 1 ? Lang.F("影片 {0}", l.Row + 1) : Lang.T("影片"),
                _ => Lang.F("音軌 {0}", l.Row + 1)
            };
            Lane(l.Y, l.H, name);
            if (l.Kind == EdTrack.Vid && l.Row > 0 && !P.OnRow(EdTrack.Vid, l.Row).Any())
                TextRenderer.DrawText(g, Lang.T("將影片 / 相片拖入呢條軌 = 疊喺主影片上面（畫中畫 / 貼圖）"), Ui.F(8.25f), new Rectangle(HeadW + Ui.S(10), l.Y, w - HeadW - Ui.S(20), l.H), Ui.TextFaint,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            if (ShowsPlus(l))
            {
                var ar = PlusRect(l);
                bool hot = ar.Contains(_mouse);
                using (var path = Ui.Round(ar, Ui.S(4f)))
                {
                    using (var b = new SolidBrush(hot ? Ui.Accent : Ui.CardHi)) g.FillPath(b, path);
                    using (var pen = new Pen(hot ? Ui.Accent : Ui.BorderHi, 1f)) g.DrawPath(pen, path);
                }
                TextRenderer.DrawText(g, "+", Ui.F(10f, FontStyle.Bold), ar, hot ? Color.White : Ui.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
        int wantH = WantHeight;
        if (wantH != _lastH)
        {
            bool first = _lastH < 0;
            _lastH = wantH;
            if (!first || Height < wantH) BeginInvoke(() => HeightChanged?.Invoke());
        }
        var clip = new Rectangle(HeadW, 0, w - HeadW, Height);
        g.SetClip(clip);
        // 尺
        double step = Pps >= 120 ? 1 : Pps >= 40 ? 2 : Pps >= 15 ? 5 : Pps >= 6 ? 15 : 30;
        double t0 = Math.Floor(T(HeadW) / step) * step;
        using (var pen = new Pen(Ui.Border))
            for (double t = t0; X(t) < w; t += step)
            {
                int x = (int)X(t);
                g.DrawLine(pen, x, RulerH - Ui.S(8), x, RulerH);
                TextRenderer.DrawText(g, Time(t, false), Ui.F(7.75f), new Point(x + Ui.S(3), Ui.S(4)), Ui.TextFaint, TextFormatFlags.NoPadding);
                for (int k = 1; k < 5; k++)
                {
                    int sx = (int)X(t + step * k / 5);
                    g.DrawLine(pen, sx, RulerH - Ui.S(4), sx, RulerH);
                }
            }
        // 文字
        RectangleF On(object o, double start, double len) { var k = KindOf(o); int r = RowOf(o); var l = lanes.FirstOrDefault(x => x.Kind == k && x.Row == r); if (l.H == 0) l = lanes.Last(x => x.Kind == k); return new RectangleF((float)X(start), l.Y + Ui.S(2), (float)(len * Pps), l.H - Ui.S(4)); }
        foreach (var t in P.Texts)
            Block(g, On(t, t.Start, t.Dur), TextHue,
                "T  " + t.Text.Replace("\n", " "), IsSel(t), null, Faint(t));
        // 圖片
        foreach (var o in P.Overlays)
            Block(g, On(o, o.Start, o.Dur), o.IsVideo ? Ui.Accent : ImgHue, (o.IsVideo ? "▶  " : "▣  ") + o.Name, IsSel(o), o.IsVideo ? ThumbOv(o) : null, Faint(o));
        // 影片
        var starts = P.Starts();
        for (int i = 0; i < P.Clips.Count; i++)
        {
            var c = P.Clips[i];
            var r = new RectangleF((float)X(starts[i]), VidY + Ui.S(3), (float)(c.Len * Pps), VidH - Ui.S(6));
            Block(g, r, Ui.Accent, (c.Speed != 1 && !c.IsImage ? $"{c.Speed:0.##}x · " : "") + c.Name, IsSel(c), Thumb(c), Faint(c));
        }
        // 轉場掣
        for (int i = 0; i + 1 < P.Clips.Count; i++)
        {
            var r = TransRect(i, starts);
            bool on = P.Clips[i].Trans.Length > 0, sel = Selected is TransSel ts && ts.Index == i;
            using var path = Ui.Round(r, Ui.S(5f));
            using (var b = new SolidBrush(sel ? Ui.Accent2 : on ? Ui.Mix(Ui.CardHi, Ui.Accent2, 0.5f) : Ui.CardHi)) g.FillPath(b, path);
            using (var pen = new Pen(sel || on ? Ui.Accent2 : Ui.BorderHi, 1f)) g.DrawPath(pen, path);
            TextRenderer.DrawText(g, "⇄", Ui.F(8f, FontStyle.Bold), Rectangle.Round(r), Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        // 拖緊嘅片：跟住滑鼠 + 插入位置
        // 音樂
        foreach (var m in P.Music)
            Block(g, On(m, m.Start, m.Len), MusHue, (m.Speed != 1 ? $"{m.Speed:0.##}x · " : "") + "♪  " + m.Name, IsSel(m), null, Faint(m));
        // 播放頭
        float px = (float)X(Playhead);
        using (var pen = new Pen(Ui.Danger, Ui.S(2f))) g.DrawLine(pen, px, 0, px, LanesBottom);
        using (var b = new SolidBrush(Ui.Danger))
            g.FillPolygon(b, new[] { new PointF(px - Ui.S(6), 0), new PointF(px + Ui.S(6), 0), new PointF(px, Ui.S(9)) });
        DrawSnapLine(g);
        DrawDragGhost(g);
        g.ResetClip();
        DrawLaneDrag(g);
        g.ResetClip();
        if (P.Clips.Count == 0 && P.Texts.Count == 0 && P.Music.Count == 0)
            TextRenderer.DrawText(g, Lang.T("撳「+ 加入媒體」或者將影片 / 相片 / 音樂拖入嚟"), Ui.F(9.5f), new Rectangle(HeadW, VidY, w - HeadW, VidH), Ui.TextFaint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void Block(Graphics g, RectangleF r, Color hue, string text, bool sel, Bitmap? thumb, int alpha = 255)
    {
        if (r.Right < HeadW || r.Left > Width) return;
        using var path = Ui.Round(r, Ui.S(6f));
        using (var b = new SolidBrush(Color.FromArgb(alpha, Ui.Mix(Ui.Card, hue, 0.35f)))) g.FillPath(b, path);
        if (thumb != null)
        {
            var old = g.Clip;
            g.SetClip(path, CombineMode.Intersect);
            float tw = thumb.Width * (r.Height / thumb.Height);
            for (float x = r.X; x < r.Right; x += tw)
                g.DrawImage(thumb, x, r.Y, tw, r.Height);
            using (var shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillRectangle(shade, r.X, r.Bottom - Ui.S(20), r.Width, Ui.S(20));
            g.Clip = old;
        }
        using (var pen = new Pen(sel ? Color.White : hue, sel ? Ui.S(2f) : 1f)) g.DrawPath(pen, path);
        var tr = thumb != null ? new Rectangle((int)r.X + Ui.S(6), (int)r.Bottom - Ui.S(20), (int)r.Width - Ui.S(10), Ui.S(20))
                               : new Rectangle((int)r.X + Ui.S(8), (int)r.Y, (int)r.Width - Ui.S(12), (int)r.Height);
        if (tr.Width > Ui.S(14))
            TextRenderer.DrawText(g, text, Ui.F(8.5f, FontStyle.Bold), tr, thumb != null ? Color.White : Ui.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        if (sel)
        {
            // 兩邊拉柄
            using var hb = new SolidBrush(Color.White);
            g.FillRectangle(hb, r.X + 1, r.Y + r.Height / 2 - Ui.S(8), Ui.S(3), Ui.S(16));
            g.FillRectangle(hb, r.Right - Ui.S(4), r.Y + r.Height / 2 - Ui.S(8), Ui.S(3), Ui.S(16));
        }
    }

    // ---------- 多選（Shift + 左鍵）----------
    public readonly List<object> Multi = new();
    private bool Exists(object o) => o switch { EdClip c => P.Clips.Contains(c), EdText t => P.Texts.Contains(t), EdOverlay ov => P.Overlays.Contains(ov), EdMusic m => P.Music.Contains(m), _ => false };
    /// <summary>揀咗嘅全部嘢（多選 + 主選擇），只計仲喺專案入面嘅。</summary>
    public List<object> SelectedAll()
    {
        var list = Multi.Where(Exists).ToList();
        if (Selected != null && Selected is not TransSel && Exists(Selected) && !list.Contains(Selected)) list.Add(Selected);
        return list;
    }
    private bool IsSel(object o) => Selected == o || Multi.Contains(o);

    private RectangleF TransRect(int i, double[] starts)
    {
        double d = P.TransLen(i);
        double mid = starts[i + 1] + d / 2;
        float sz = Ui.S(20f);
        return new RectangleF((float)X(mid) - sz / 2, VidY - Ui.S(4), sz, sz);
    }

    public static string Time(double t, bool tenths = true)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, t));
        string s = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes:00}:{ts.Seconds:00}";
        return tenths ? s + "." + (ts.Milliseconds / 100) : s;
    }

    // ---------- 撳 / 拖 ----------
    private enum DragKind { None, Playhead, Move, TrimL, TrimR, Lane }
    private DragKind _drag;
    private object? _dragObj;
    private Point _down, _mouse;
    private float _grabDx;
    private bool _moved;
    private (double A, double B, double C) _orig;

    private (object? Obj, DragKind Kind) Hit(Point p)
    {
        int edge = Ui.S(7);
        if (p.X < HeadW) return (null, DragKind.None);
        if (p.Y < RulerH) return (null, DragKind.Playhead);
        var starts = P.Starts();
        if (p.Y >= VidY - Ui.S(6) && p.Y < VidY + Ui.S(18))
            for (int i = 0; i + 1 < P.Clips.Count; i++)
            {
                var tr = TransRect(i, starts);
                tr.Inflate(Ui.S(3), Ui.S(3));
                if (tr.Contains(p)) return (new TransSel(i), DragKind.None);
            }
        DragKind Edge(double s, double len)
        {
            double x1 = X(s), x2 = X(s + len);
            if (Math.Abs(p.X - x1) <= edge && x2 - x1 > edge * 3) return DragKind.TrimL;
            if (Math.Abs(p.X - x2) <= edge) return DragKind.TrimR;
            return p.X > x1 && p.X < x2 ? DragKind.Move : DragKind.None;
        }
        var lane = LaneAt(p.Y);
        if (lane is { } ln)
            foreach (var (o, st, len) in P.OnRow(ln.Kind, ln.Row).Reverse())
            {
                var k = Edge(st, len);
                if (k != DragKind.None) return (o, k);
            }
        return (null, DragKind.None);
    }

    private int InsertIndex(int x)
    {
        var starts = P.Starts();
        for (int i = 0; i < P.Clips.Count; i++)
            if (x < X(starts[i] + P.Clips[i].Len / 2)) return i;
        return P.Clips.Count;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.X < HeadW && LaneAt(e.Y) is { } hl)
        {
            if (e.Button == MouseButtons.Left && ShowsPlus(hl) && PlusRect(hl).Contains(e.Location))
            {
                // 「+」：加多一條同類嘅軌
                BeforeChange?.Invoke();
                P.SetRows(hl.Kind, P.RowCount(hl.Kind) + 1);
                Changed?.Invoke();
                Invalidate();
                return;
            }
            if (e.Button == MouseButtons.Left && CanMoveLane(hl))
            {
                // 撳住軌名：可以上下拖改排位
                _drag = DragKind.Lane;
                _dragLane = hl;
                _down = _mouse = e.Location;
                _grabDy = e.Y - hl.Y;
                _moved = false;
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                // 右鍵軌名：加軌 / 刪走空嘅軌（主影片軌唔刪得）
                var cm = new ContextMenuStrip();
                int n = P.RowCount(hl.Kind), r = hl.Row;
                var addIt = cm.Items.Add(Lang.T("加一條同類嘅軌"));
                addIt.Enabled = n < EdProject.MaxRows;
                addIt.Click += (_, _) => { BeforeChange?.Invoke(); P.SetRows(hl.Kind, n + 1); Changed?.Invoke(); Invalidate(); };
                bool empty = !P.OnRow(hl.Kind, r).Any();
                bool canDel = empty && n > 1 && !(hl.Kind == EdTrack.Vid && r == 0);
                var del = cm.Items.Add(Lang.T("刪除呢條軌"));
                del.Enabled = canDel;
                del.Click += (_, _) =>
                {
                    BeforeChange?.Invoke();
                    var objs = Enumerable.Range(r + 1, Math.Max(0, n - r - 1)).SelectMany(x => P.OnRow(hl.Kind, x).Select(y => y.Obj)).ToList();
                    foreach (var o in objs) SetRow(o, RowOf(o) - 1);
                    P.SetRows(hl.Kind, n - 1);
                    Changed?.Invoke();
                    Invalidate();
                };
                if (!empty) cm.Items.Add(Lang.T("（軌上面有嘢，要清空先刪得）")).Enabled = false;
                cm.Show(this, e.Location);
                return;
            }
        }
        if (e.Button != MouseButtons.Left) return;
        _down = _mouse = e.Location;
        _moved = false;
        var (obj, kind) = Hit(e.Location);
        if ((ModifierKeys & Keys.Shift) != 0 && obj != null && obj is not TransSel)
        {
            // Shift + 左鍵：加入 / 移出多選
            if (Multi.Count == 0 && Selected != null && Selected is not TransSel && Selected != obj) Multi.Add(Selected);
            if (Multi.Contains(obj) || (Multi.Count == 0 && Selected == obj))
            {
                Multi.Remove(obj);
                Selected = Multi.LastOrDefault();
            }
            else
            {
                if (Multi.Count == 0 && Selected == null) { }
                Multi.Add(obj);
                Selected = obj;
            }
            if (Multi.Count <= 1) Multi.Clear();
            SelectionChanged?.Invoke();
            Invalidate();
            return;
        }
        Multi.Clear();
        if (obj is TransSel)
        {
            Selected = obj;
            SelectionChanged?.Invoke();
            Invalidate();
            return;
        }
        if (obj == null)
        {
            if (e.X < HeadW) return;
            // 空位 / 尺：移動播放頭
            _drag = DragKind.Playhead;
            Playhead = T(e.X);
            if (e.Y >= RulerH && Selected != null) { Selected = null; SelectionChanged?.Invoke(); }
            Seek?.Invoke(Playhead, false);
            Invalidate();
            return;
        }
        if (Selected != obj) { Selected = obj; SelectionChanged?.Invoke(); }
        _drag = kind;
        _dragObj = obj;
        var starts = P.Starts();
        switch (obj)
        {
            case EdClip c:
                _orig = (c.In, c.Out, 0);
                _grabDx = (float)(e.X - X(starts[P.Clips.IndexOf(c)]));
                break;
            case EdText t: _orig = (t.Start, t.Dur, 0); _grabDx = (float)(e.X - X(t.Start)); break;
            case EdOverlay o: _orig = (o.Start, o.Dur, o.In); _grabDx = (float)(e.X - X(o.Start)); break;
            case EdMusic m: _orig = (m.Start, m.In, m.Out); _grabDx = (float)(e.X - X(m.Start)); break;
        }
        _grabDy = e.Y - (LaneOf(KindOf(obj), RowOf(obj)).Y + Ui.S(2));
        _edgeL = StartOf(obj);
        _edgeR = _edgeL + LenOf(obj);
        _snapT = null;
        _dropStart = _orig.A;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;
        if (_drag == DragKind.None)
        {
            var (_, k) = Hit(e.Location);
            bool head = e.X < HeadW && LaneAt(e.Y) is { } hh && CanMoveLane(hh) && !(ShowsPlus(hh) && PlusRect(hh).Contains(e.Location));
            Cursor = k is DragKind.TrimL or DragKind.TrimR ? Cursors.SizeWE : k == DragKind.Move ? Cursors.Hand : head ? Cursors.SizeNS : Cursors.Default;
            if (e.X < HeadW) Invalidate(new Rectangle(0, 0, HeadW, Height));
            return;
        }
        if (_drag == DragKind.Lane)
        {
            if (Math.Abs(e.Y - _down.Y) >= Ui.S(4)) _moved = true;
            Invalidate();
            return;
        }
        if (!_moved && Math.Abs(e.X - _down.X) < Ui.S(3) && !(_drag == DragKind.Move && _dragObj is EdText or EdOverlay or EdMusic or EdClip && Math.Abs(e.Y - _down.Y) >= Ui.S(6))) return;
        if (!_moved && _drag != DragKind.Playhead) BeforeChange?.Invoke();
        _moved = true;
        double dt = (e.X - _down.X) / Pps;
        _snapT = null;
        switch (_drag)
        {
            case DragKind.Playhead:
                Playhead = T(e.X);
                Seek?.Invoke(Playhead, false);
                break;
            case DragKind.Move:
                // 唔即時改：片段跟住滑鼠，放手先至落位
                _dropStart = SnapMove(Math.Max(0, _orig.A + dt), LenOf(_dragObj!), _dragObj);
                break;
            case DragKind.TrimL:
                if (_dragObj is not EdClip) dt = SnapT(_edgeL + dt, _dragObj) - _edgeL;
                switch (_dragObj)
                {
                    case EdClip c when !c.IsImage:
                        c.In = Math.Clamp(_orig.A + dt * c.Speed, 0, c.Out - 0.1);
                        break;
                    case EdClip c:
                        c.Out = Math.Max(0.2, _orig.B - dt);
                        break;
                    case EdText tx:
                    {
                        double ns = Math.Clamp(_orig.A + dt, 0, _orig.A + _orig.B - 0.2);
                        tx.Dur = _orig.A + _orig.B - ns;
                        tx.Start = ns;
                        break;
                    }
                    case EdOverlay ov:
                    {
                        double lo = ov.IsVideo ? Math.Max(0, _orig.A - _orig.C) : 0;   // 影片唔可以拉過原片開頭
                        double ns = Math.Clamp(_orig.A + dt, lo, _orig.A + _orig.B - 0.2);
                        ov.Dur = _orig.A + _orig.B - ns;
                        ov.Start = ns;
                        if (ov.IsVideo) ov.In = _orig.C + (ns - _orig.A);
                        break;
                    }
                    case EdMusic m:
                    {
                        double d = Math.Clamp(dt, -Math.Min(_orig.B / m.Sp, _orig.A), (_orig.C - _orig.B - 0.2) / m.Sp);
                        m.In = _orig.B + d * m.Sp;
                        m.Start = _orig.A + d;
                        break;
                    }
                }
                break;
            case DragKind.TrimR:
                dt = SnapT(_edgeR + dt, _dragObj) - _edgeR;
                switch (_dragObj)
                {
                    case EdClip c when !c.IsImage:
                        c.Out = Math.Clamp(_orig.B + dt * c.Speed, c.In + 0.1, c.SrcDur > 0 ? c.SrcDur : 1e6);
                        break;
                    case EdClip c:
                        c.Out = Math.Max(0.2, _orig.B + dt);
                        break;
                    case EdText tx: tx.Dur = Math.Max(0.2, _orig.B + dt); break;
                    case EdOverlay ov: ov.Dur = Math.Clamp(_orig.B + dt, 0.2, ov.IsVideo && ov.SrcDur > 0 ? Math.Max(0.2, ov.SrcDur - ov.In) : 1e6); break;
                    case EdMusic m: m.Out = Math.Clamp(_orig.C + dt * m.Sp, m.In + 0.2, m.SrcDur > 0 ? m.SrcDur : 1e6); break;
                }
                break;
        }
        if (_drag != DragKind.Playhead && _drag != DragKind.Move) Changed?.Invoke();
        Invalidate();
    }

    // ---------- 磁貼：貼近 0 秒、播放頭、其他片段頭尾就吸住 ----------
    private double? _snapT;
    private double _edgeL, _edgeR;

    private IEnumerable<double> SnapPoints(object? except)
    {
        yield return 0;
        yield return Playhead;
        foreach (var k in new[] { EdTrack.Text, EdTrack.Vid, EdTrack.Aud })
            for (int r = 0; r < P.RowCount(k); r++)
                foreach (var (o, st, len) in P.OnRow(k, r))
                {
                    if (o == except) continue;
                    yield return st;
                    yield return st + len;
                }
    }

    /// <summary>單一個邊：最近嘅吸附點（8px 之內）。</summary>
    private double SnapT(double t, object? except)
    {
        double tol = Ui.S(10) / Pps, best = t, bd = tol;
        foreach (var p in SnapPoints(except))
        {
            double d = Math.Abs(p - t);
            if (d < bd) { bd = d; best = p; }
        }
        if (bd < tol) _snapT = best;
        return best;
    }

    /// <summary>成段搬：頭或者尾邊個近啲就用邊個吸。</summary>
    private double SnapMove(double start, double len, object? except)
    {
        double tol = Ui.S(10) / Pps, best = start, bd = tol;
        double? line = null;
        foreach (var p in SnapPoints(except))
        {
            double d1 = Math.Abs(p - start), d2 = Math.Abs(p - (start + len));
            if (d1 < bd) { bd = d1; best = p; line = p; }
            if (d2 < bd) { bd = d2; best = p - len; line = p; }
        }
        _snapT = line;
        return Math.Max(0, best);
    }

    private double Snap(double t) => SnapT(t, _dragObj);

    private double StartOf(object o) => o switch
    {
        EdClip c => P.Starts()[Math.Max(0, P.Clips.IndexOf(c))], EdText t => t.Start, EdOverlay ov => ov.Start, EdMusic m => m.Start, _ => 0
    };

    private void DrawSnapLine(Graphics g)
    {
        if (_snapT is not double t || _drag is DragKind.None or DragKind.Playhead or DragKind.Lane || !_moved) return;
        float x = (float)X(t);
        using var pen = new Pen(Color.FromArgb(255, 210, 60), Ui.S(1.5f)) { DashStyle = DashStyle.Dash };
        g.DrawLine(pen, x, RulerH, x, LanesBottom);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == DragKind.Lane)
        {
            if (_moved && LaneTarget(e.Y) is { } dst && dst != _dragLane.Row) MoveLane(_dragLane.Kind, _dragLane.Row, dst);
            _drag = DragKind.None;
            Invalidate();
            return;
        }
        if (_drag == DragKind.Playhead) Seek?.Invoke(Playhead, true);
        else if (_drag == DragKind.Move && _moved && _dragObj is EdClip cc && DropPlan(e.Location) is { InsertMain: false } pl)
        {
            // 主影片軌 → 影片 2 軌以上（畫中畫）/ 圖片軌
            double st = pl.Start;
            var tl = pl.Lane;
            int w = 0, h = 0;
            try { var info = MediaInfo.Probe(cc.Path); w = info.W; h = info.H; } catch { }
            var o = cc.IsImage
                ? new EdOverlay { Path = cc.Path, Start = st, Dur = cc.Len, X = 0.5, Y = 0.5, Scale = 0.3, ImgW = w, ImgH = h }
                : new EdOverlay
                {
                    Path = cc.Path, IsVideo = true, Start = st, In = cc.In, Dur = Math.Max(0.2, cc.Out - cc.In), SrcDur = cc.SrcDur,
                    HasAudio = cc.HasAudio, Mute = cc.Mute, Volume = cc.Volume, X = 0.72, Y = 0.72, Scale = 0.4, ImgW = w, ImgH = h, Fade = false
                };
            var kind = EdTrack.Vid;
            if (tl.Phantom) P.SetRows(kind, tl.Row + 1);
            int i = P.Clips.IndexOf(cc);
            if (i > 0) P.Clips[i - 1].Trans = i + 1 < P.Clips.Count ? P.Clips[i - 1].Trans : "";
            P.Clips.Remove(cc);
            o.Row = P.OnRow(kind, tl.Row).Any(x => x.Start < st + o.Dur - 0.01 && st < x.Start + x.Len - 0.01) ? P.FreeRow(kind, st, o.Dur) : tl.Row;
            P.Overlays.Add(o);
            Selected = o;
            SelectionChanged?.Invoke();
            Changed?.Invoke();
        }
        else if (_drag == DragKind.Move && _moved && _dragObj is EdOverlay ov && DropPlan(e.Location).InsertMain)
        {
            // 影片 / 圖片 → 主影片軌（插入去放手位置）
            var c2 = ov.IsVideo
                ? new EdClip { Path = ov.Path, In = ov.In, Out = ov.In + ov.Dur, SrcDur = ov.SrcDur, HasAudio = ov.HasAudio, Mute = ov.Mute, Volume = ov.Volume, SrcW = ov.ImgW, SrcH = ov.ImgH }
                : new EdClip { Path = ov.Path, IsImage = true, In = 0, Out = ov.Dur, SrcW = ov.ImgW, SrcH = ov.ImgH };
            int to = InsertIndex(e.X);
            P.Overlays.Remove(ov);
            P.Clips.Insert(Math.Clamp(to, 0, P.Clips.Count), c2);
            Selected = c2;
            SelectionChanged?.Invoke();
            Changed?.Invoke();
        }
        else if (_drag == DragKind.Move && _moved && _dragObj is EdText or EdOverlay or EdMusic)
        {
            var pl2 = DropPlan(e.Location);
            if (pl2.Lane.Phantom) P.SetRows(pl2.Lane.Kind, pl2.Lane.Row + 1);
            switch (_dragObj) { case EdText t: t.Start = pl2.Start; break; case EdOverlay o: o.Start = pl2.Start; break; case EdMusic m: m.Start = pl2.Start; break; }
            SetRow(_dragObj, pl2.Lane.Row);
            Changed?.Invoke();
        }
        else if (_drag == DragKind.Move && _moved && _dragObj is EdClip c)
        {
            int from = P.Clips.IndexOf(c), to = InsertIndex(e.X);
            if (to > from) to--;
            if (to != from)
            {
                P.Clips.RemoveAt(from);
                P.Clips.Insert(Math.Clamp(to, 0, P.Clips.Count), c);
            }
            Changed?.Invoke();
        }
        else if (_moved) Changed?.Invoke();
        _drag = DragKind.None;
        _dragObj = null;
        _snapT = null;
        Refresh2();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Control) != 0) { Zoom(e.Delta > 0 ? 1.25 : 0.8, e.X); return; }
        _scroll = Math.Clamp(_scroll - e.Delta, 0, Math.Max(0, _bar.Maximum - _bar.LargeChange));
        Refresh2();
    }
}

// =====================================================================
//  預覽畫面
// =====================================================================
internal sealed class EdPreview : Control
{
    private Bitmap? _img;
    public string Note = "";
    public Size Aspect = new(16, 9);
    public EdProject P = new();
    public Func<double> Playhead = () => 0;
    public object? Selected;

    /// <summary>喺預覽撳中文字 / 圖片。</summary>
    public event Action<object>? Picked;
    /// <summary>開始拖（記復原）/ 拖完（更新）。</summary>
    public event Action? BeforeMove, Moved;
    /// <summary>由外面拖檔案入預覽：檔案 + 放低嘅位置（畫面比例）。</summary>
    public event Action<string[], PointF>? FilesDropped;

    public EdPreview()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
        AllowDrop = true;
    }

    public void SetImage(Bitmap? b, bool keepLive = false)
    {
        var old = _img;
        _img = b;
        old?.Dispose();
        if (!keepLive && _drag == null) { Live = null; LiveBg = false; }
        else if (keepLive) LiveBg = true;
        Invalidate();
    }

    // ---------- 拖緊 / 改緊大細：即時喺本機畫，唔使等 ffmpeg ----------
    /// <summary>拖緊嘅文字 / 圖片；背景會換成「冇咗佢」嘅畫面，佢自己喺上面即時畫。</summary>
    public object? Live;
    public bool LiveBg;
    /// <summary>圖片 / 影片嗰格嘅樣（EditorPage 負責管理，呢度唔 Dispose）。</summary>
    public Bitmap? LiveImg;
    public event Action<object>? LiveStart;

    private void DrawLive(Graphics g, RectangleF rr)
    {
        switch (Live)
        {
            case EdOverlay o when LiveImg != null:
            {
                using var ia = new ImageAttributes();
                if (o.Opacity < 0.999) ia.SetColorMatrix(new ColorMatrix { Matrix33 = (float)Math.Clamp(o.Opacity, 0, 1) });
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(LiveImg, Rectangle.Round(rr), 0, 0, LiveImg.Width, LiveImg.Height, GraphicsUnit.Pixel, ia);
                break;
            }
            case EdOverlay:
                using (var b = new SolidBrush(Color.FromArgb(90, Ui.Accent))) g.FillRectangle(b, rr);
                break;
            case EdClip:
            {
                // 主影片：成個畫面先塗黑（舊位置嘅畫面唔要），再喺新位置畫嗰格
                var fr = Frame();
                var st = g.Save();
                g.SetClip(fr);
                g.FillRectangle(Brushes.Black, fr);
                if (LiveImg != null)
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(LiveImg, rr);
                }
                else using (var b = new SolidBrush(Color.FromArgb(90, Ui.Accent))) g.FillRectangle(b, rr);
                g.Restore(st);
                break;
            }
            case EdText t:
            {
                var fr = Frame();
                float px = Math.Max(4f, (float)(t.Size * fr.Height / 1080.0));
                using var f = new Font("Microsoft JhengHei", px, FontStyle.Bold, GraphicsUnit.Pixel);
                if (t.Box) using (var b = new SolidBrush(Color.FromArgb(115, 0, 0, 0))) g.FillRectangle(b, rr);
                Color col = Color.White;
                try { col = ColorTranslator.FromHtml(t.Color); } catch { }
                var inner = Rectangle.Round(rr);
                if (!t.Box)
                    foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                        TextRenderer.DrawText(g, t.Text, f, new Rectangle(inner.X + dx, inner.Y + dy, inner.Width, inner.Height), Color.Black,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, t.Text, f, inner, col, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                break;
            }
        }
    }

    /// <summary>畫面喺螢幕上嘅最長邊（px）。</summary>
    public int FrameLong { get { var f = Frame(); return Math.Max(f.Width, f.Height); } }

    private Rectangle Frame()
    {
        double k = Math.Min((double)Width / Aspect.Width, (double)Height / Aspect.Height);
        int w = Math.Max(1, (int)(Aspect.Width * k)), h = Math.Max(1, (int)(Aspect.Height * k));
        return new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
    }

    private static Font? _measure;

    /// <summary>而家播放頭見到嘅文字 / 圖片，同佢哋喺預覽嘅框（螢幕座標）。後面嘅喺上面。</summary>
    private List<(object Obj, RectangleF R)> Items()
    {
        var list = new List<(object, RectangleF)>();
        var fr = Frame();
        double t = Playhead();
        // 主影片軌：播放頭所在嗰段（放最底，所以疊圖 / 文字會先被撳中）
        if (ClipAt(t) is { } mc) list.Add((mc, ClipRect(mc, fr)));
        foreach (var o in P.OverlayOrder())
        {
            if (t < o.Start || t >= o.Start + o.Dur) continue;
            float w = (float)(fr.Width * Math.Clamp(o.Scale, 0.02, 2));
            float h = o.ImgW > 0 && o.ImgH > 0 ? w * o.ImgH / o.ImgW : w;
            list.Add((o, new RectangleF(fr.X + (float)(fr.Width * o.X) - w / 2, fr.Y + (float)(fr.Height * o.Y) - h / 2, w, h)));
        }
        foreach (var x in P.TextOrder())
        {
            if (t < x.Start || t >= x.Start + x.Dur || x.Text.Trim().Length == 0) continue;
            float px = Math.Max(4f, (float)(x.Size * fr.Height / 1080.0));
            _measure?.Dispose();
            _measure = new Font("Microsoft JhengHei", px, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = TextRenderer.MeasureText(x.Text, _measure, Size.Empty, TextFormatFlags.NoPadding);
            float pad = x.Box ? px / 3 : px / 10;
            var (cx, cy) = EdGraph.TextPos(x);
            float w = sz.Width + pad * 2, h = sz.Height + pad * 2;
            list.Add((x, new RectangleF(fr.X + (float)(fr.Width * cx) - w / 2, fr.Y + (float)(fr.Height * cy) - h / 2, w, h)));
        }
        return list;
    }

    /// <summary>播放頭所在嘅主影片段。</summary>
    private EdClip? ClipAt(double t)
    {
        var st = P.Starts();
        for (int i = 0; i < P.Clips.Count && i < st.Length; i++)
            if (t >= st[i] && t < st[i] + P.Clips[i].Len) return P.Clips[i];
        return null;
    }

    private readonly HashSet<string> _probing = new();

    /// <summary>主影片段喺預覽嘅框（螢幕座標）：同 EdGraph 一樣計（完整顯示 / 填滿 × Zoom，中心 X / Y）。</summary>
    private RectangleF ClipRect(EdClip c, Rectangle fr)
    {
        double sw = c.SrcW, sh = c.SrcH;
        if (sw <= 0 || sh <= 0)
        {
            // 舊專案未記低原片大細：喺背景讀一次，讀到再重畫
            if (Ffmpeg.Ready && IsHandleCreated && _probing.Add(c.Path))
            {
                string path = c.Path;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var info = MediaInfo.Probe(path);
                        if (!IsDisposed) BeginInvoke(() => { foreach (var x in P.Clips.Where(x => x.Path == path)) { x.SrcW = info.W; x.SrcH = info.H; } Invalidate(); });
                    }
                    catch { }
                });
            }
            sw = fr.Width; sh = fr.Height;
        }
        if (c.Rotate is 90 or 270) (sw, sh) = (sh, sw);
        double k = c.Fill ? Math.Max(fr.Width / sw, fr.Height / sh) : Math.Min(fr.Width / sw, fr.Height / sh);
        k *= Math.Clamp(c.Zoom, 0.05, 5);
        float w = (float)(sw * k), h = (float)(sh * k);
        return new RectangleF(fr.X + (float)(fr.Width * c.X) - w / 2, fr.Y + (float)(fr.Height * c.Y) - h / 2, w, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Bg);
        var r = Frame();
        g.FillRectangle(Brushes.Black, r);
        if (_img != null)
        {
            // 畫面同預覽框差唔多大就直接畫，唔使模糊放大
            g.InterpolationMode = Math.Abs(_img.Width - r.Width) <= 2 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_img, r);
            g.PixelOffsetMode = PixelOffsetMode.Default;
        }
        // 文字 / 圖片框：揀咗嘅實線，其他虛線；拖緊就跟住滑鼠
        if (Live != null && Items().FirstOrDefault(x => x.Obj == Live) is { Obj: not null } lv) DrawLive(g, lv.R);
        foreach (var (obj, rr) in Items())
        {
            bool sel = obj == Selected;
            if (obj is EdClip && !sel) continue;
            using var pen = new Pen(sel ? Ui.Accent : Color.FromArgb(120, 255, 255, 255), sel ? Ui.S(2f) : 1f);
            if (!sel) pen.DashStyle = DashStyle.Dash;
            g.DrawRectangle(pen, rr.X, rr.Y, rr.Width, rr.Height);
            if (sel)
            {
                using var b = new SolidBrush(Ui.Accent);
                using var wp = new Pen(Color.White, 1f);
                foreach (var c in Corners(rr))
                {
                    var hr = new RectangleF(c.X - Ui.S(5), c.Y - Ui.S(5), Ui.S(10), Ui.S(10));
                    g.FillRectangle(b, hr);
                    g.DrawRectangle(wp, hr.X, hr.Y, hr.Width, hr.Height);
                }
            }
        }
        if (Note.Length > 0)
            TextRenderer.DrawText(g, Note, Ui.F(9.5f), r, Color.Gainsboro, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    // ---------- 拖文字 / 圖片（中間拖 = 搬位；四角拖 = 改大細）----------
    private object? _drag;
    private PointF _grab;       // 撳落位置同中心嘅距離（畫面比例）
    private bool _moved, _resize;
    private PointF _rCenter;    // 改大細：中心（螢幕座標）
    private double _rDist, _rOrig;

    private static PointF[] Corners(RectangleF r) => new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom), new PointF(r.Right, r.Bottom) };

    /// <summary>揀咗嘅嘢嘅四角：撳中邊隻角（-1 = 冇）。</summary>
    private int CornerAt(Point p, out RectangleF rect)
    {
        rect = default;
        if (Selected == null) return -1;
        var it = Items().FirstOrDefault(x => x.Obj == Selected);
        if (it.Obj == null) return -1;
        rect = it.R;
        var cs = Corners(it.R);
        for (int i = 0; i < 4; i++)
            if (Math.Abs(p.X - cs[i].X) <= Ui.S(8) && Math.Abs(p.Y - cs[i].Y) <= Ui.S(8)) return i;
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        if (CornerAt(e.Location, out var cr) >= 0)
        {
            _drag = Selected;
            _resize = true;
            _moved = false;
            _rCenter = new PointF(cr.X + cr.Width / 2, cr.Y + cr.Height / 2);
            _rDist = Math.Max(4, Dist(e.Location, _rCenter));
            _rOrig = Selected switch { EdOverlay o => o.Scale, EdText t => t.Size, EdClip c => c.Zoom, _ => 1 };
            return;
        }
        var items = Items();
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var (obj, rr) = items[i];
            var hit = rr;
            hit.Inflate(Ui.S(4), Ui.S(4));
            if (!hit.Contains(e.Location)) continue;
            Selected = obj;
            Picked?.Invoke(obj);
            var fr = Frame();
            var (cx, cy) = Center(obj);
            _grab = new PointF((float)((e.X - fr.X) / (double)fr.Width - cx), (float)((e.Y - fr.Y) / (double)fr.Height - cy));
            _drag = obj;
            _resize = false;
            _moved = false;
            Invalidate();
            return;
        }
    }

    /// <summary>主影片大細貼近 100% 就吸住（易啲拉返原本大細）。</summary>
    private static double SnapZoom(double z) => Math.Abs(z - 1) < 0.03 ? 1 : z;

    private static double Dist(PointF a, PointF b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static (double X, double Y) Center(object o) => o switch
    {
        EdOverlay ov => (ov.X, ov.Y),
        EdText t => EdGraph.TextPos(t),
        EdClip c => (c.X, c.Y),
        _ => (0.5, 0.5)
    };

    private void BeginLive()
    {
        if (_moved || _drag == null) return;
        BeforeMove?.Invoke();
        _moved = true;
        Live = _drag;
        LiveBg = false;
        LiveStart?.Invoke(_drag);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == null)
        {
            int c = CornerAt(e.Location, out _);
            Cursor = c is 0 or 3 ? Cursors.SizeNWSE : c is 1 or 2 ? Cursors.SizeNESW
                   : Items().Any(x => x.R.Contains(e.Location)) ? Cursors.SizeAll : Cursors.Default;
            return;
        }
        if (_resize)
        {
            double k = Dist(e.Location, _rCenter) / _rDist;
            if (!_moved && Math.Abs(k - 1) < 0.01) return;
            BeginLive();
            switch (_drag)
            {
                case EdOverlay o: o.Scale = Math.Clamp(_rOrig * k, 0.02, 2); break;
                case EdText t: t.Size = (int)Math.Clamp(Math.Round(_rOrig * k), 12, 300); break;
                case EdClip c: c.Zoom = SnapZoom(Math.Clamp(_rOrig * k, 0.1, 5)); break;
            }
            Invalidate();
            return;
        }
        var fr = Frame();
        double nx = Math.Clamp((e.X - fr.X) / (double)fr.Width - _grab.X, 0, 1);
        double ny = Math.Clamp((e.Y - fr.Y) / (double)fr.Height - _grab.Y, 0, 1);
        // 貼近中間線就吸住
        if (Math.Abs(nx - 0.5) < 0.015) nx = 0.5;
        if (Math.Abs(ny - 0.5) < 0.015) ny = 0.5;
        BeginLive();
        switch (_drag)
        {
            case EdOverlay o: o.X = nx; o.Y = ny; break;
            case EdText t: t.X = nx; t.Y = ny; break;
            case EdClip c: c.X = nx; c.Y = ny; break;
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bool moved = _drag != null && _moved;
        _drag = null;
        _resize = false;
        if (moved) Moved?.Invoke();
    }

    /// <summary>滾輪：揀咗嘅圖片放大縮細 / 文字大細。</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Selected == null || !Items().Any(x => x.Obj == Selected && x.R.Contains(e.Location))) return;
        BeforeMove?.Invoke();
        if (Live != Selected) { Live = Selected; LiveBg = false; LiveStart?.Invoke(Selected); }
        switch (Selected)
        {
            case EdOverlay o: o.Scale = Math.Clamp(o.Scale * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.02, 2); break;
            case EdText t: t.Size = Math.Clamp(t.Size + (e.Delta > 0 ? 4 : -4), 12, 300); break;
            case EdClip c: c.Zoom = SnapZoom(Math.Clamp(c.Zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.1, 5)); break;
        }
        Invalidate();
        Moved?.Invoke();
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;
        var fr = Frame();
        var pt = PointToClient(new Point(e.X, e.Y));
        FilesDropped?.Invoke(files, new PointF((float)Math.Clamp((pt.X - fr.X) / (double)fr.Width, 0, 1), (float)Math.Clamp((pt.Y - fr.Y) / (double)fr.Height, 0, 1)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _img?.Dispose();
        base.Dispose(disposing);
    }
}

// =====================================================================
//  影片剪輯頁
// =====================================================================
internal sealed class EditorPage : Panel
{
    private EdProject _p;
    private readonly EdTimeline _tl = new();
    private readonly EdPreview _pv = new();
    private readonly EdPlayer _player = new();
    private readonly Panel _props = new() { AutoScroll = true, BackColor = Ui.Bg };
    private readonly FlowLayoutPanel _propFlow = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.Bg };
    private readonly FancyButton _projBtn, _play;
    private readonly TextBox _nameBox;
    private readonly Label _time;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _save = new() { Interval = 800 };
    private readonly System.Windows.Forms.Timer _still = new() { Interval = 120 };
    private readonly List<string> _undo = new();
    private DateTime _lastUndo;
    private CancellationTokenSource? _stillCts;
    private int _stillGen;

    public static Control Build() => new EditorPage();
    public static EditorPage? Instance { get; private set; }

    /// <summary>由檔案總管右鍵開：開新專案（名同第一個檔案一樣，畫面比例跟第一條片），再加入檔案。</summary>
    public async void OpenAsNewProject(string[] files)
    {
        files = files.Where(File.Exists).ToArray();
        if (files.Length == 0) return;
        if (!Ffmpeg.Ready) { MessageBox.Show(FindForm(), Lang.T("未下載 ffmpeg：請去「工具庫」重新安裝影片剪輯。"), Program.AppName); return; }
        StopPlay();
        CommitName();
        EdStore.Save(_p);
        var have = new HashSet<string>(EdStore.List(), StringComparer.OrdinalIgnoreCase);
        string baseName = Scripts.Script.Sanitize(Path.GetFileNameWithoutExtension(files[0]));
        if (baseName.Length == 0) baseName = EdStore.NewName();
        string name = baseName;
        for (int i = 2; have.Contains(name); i++) name = $"{baseName} ({i})";
        var np = new EdProject { Name = name, W = _p.W, H = _p.H, Fps = _p.Fps };
        try
        {
            var info = await Task.Run(() => MediaInfo.Probe(files[0]));
            if (info.W > 0 && info.H > 0)
            {
                // 跟第一個檔案嘅比例；短邊最多 1080
                int shortSide = Math.Min(1080, Math.Min(info.W, info.H));
                double k = shortSide / (double)Math.Min(info.W, info.H);
                np.W = Math.Max(2, (int)Math.Round(info.W * k / 2) * 2);
                np.H = Math.Max(2, (int)Math.Round(info.H * k / 2) * 2);
            }
        }
        catch { }
        _p = np;
        _undo.Clear();
        EdStore.Save(_p);
        Reload();
        await AddFilesAsync(files);
    }

    private EditorPage()
    {
        Instance = this;
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        AllowDrop = true;
        string? last = EdStore.Last();
        _p = (last != null ? EdStore.Load(last) : null) ?? new EdProject { Name = EdStore.NewName() };

        // ---- 頂行：左邊專案 / 加入媒體；右上角復原 / 匯出 ----
        var top = new Panel { Dock = DockStyle.Top, Height = Ui.S(56), BackColor = Ui.Bg, Padding = new Padding(Ui.S(20), Ui.S(12), Ui.S(20), 0) };
        var topL = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Ui.Bg, WrapContents = false };
        var topR = new FlowLayoutPanel { Dock = DockStyle.Right, BackColor = Ui.Bg, WrapContents = false, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        top.Controls.Add(topL);
        top.Controls.Add(topR);
        FancyButton B(FlowLayoutPanel host, string t, Action a, BtnKind k = BtnKind.Ghost, string? tip = null)
        {
            var b = new FancyButton { Text = t, Kind = k, Font = Ui.F(9.75f), Margin = new Padding(0, 0, Ui.S(8), 0) };
            b.Size = new Size(b.GetPreferredSize(Size.Empty).Width + Ui.S(6), Ui.S(34));
            b.Click += (_, _) => a();
            if (tip != null) _tip.SetToolTip(b, tip);
            host.Controls.Add(b);
            return b;
        }
        _projBtn = B(topL, Lang.T("📁 專案 ▾"), ProjectMenu, BtnKind.Default, Lang.T("新專案、開啟其他專案、刪除"));
        // 影片名：匯出時直接用做檔名
        _nameBox = new TextBox { MaxLength = 80, Font = Ui.F(10.5f, FontStyle.Bold) };
        var nameHost = new InputHost(_nameBox, Ui.S(240)) { Margin = new Padding(0, 0, Ui.S(8), 0) };
        _tip.SetToolTip(_nameBox, Lang.T("影片名稱（匯出時用做檔名）"));
        _nameBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitName(); _tl.Focus(); } else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _nameBox.Text = _p.Name; _tl.Focus(); } };
        _nameBox.Leave += (_, _) => CommitName();
        topL.Controls.Add(nameHost);
        B(topL, Lang.T("+ 加入媒體"), AddMedia, BtnKind.Primary, Lang.T("影片、相片放喺影片軌；MP3 等聲音檔放喺音樂軌。都可以直接拖入嚟。"));
        B(topR, Lang.T("↶ 復原"), Undo, BtnKind.Ghost, Lang.T("復原上一步（Ctrl+Z）"));
        var exp = B(topR, Lang.T("⬆ 匯出"), Export, BtnKind.Success, Lang.T("輸出 MP4"));
        exp.Margin = new Padding(0);

        // ---- 下面：播放掣 + 圖示工具列 + 時間軸 ----
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = _tl.WantHeight + Ui.S(46), BackColor = Ui.Bg, Padding = new Padding(Ui.S(20), 0, Ui.S(20), Ui.S(12)) };
        var transport = new Panel { Dock = DockStyle.Top, Height = Ui.S(42), BackColor = Ui.Bg };
        int tx = 0;
        FancyButton Icon(string glyph, string tip, Action a, BtnKind k = BtnKind.Ghost, int w = 38)
        {
            var b = new FancyButton { Text = glyph, Kind = k, Font = IconFont(11f), Size = new Size(Ui.S(w), Ui.S(34)), Location = new Point(tx, Ui.S(2)) };
            b.Click += (_, _) => a();
            _tip.SetToolTip(b, tip);
            transport.Controls.Add(b);
            tx = b.Right + Ui.S(4);
            return b;
        }
        void Sep() => tx += Ui.S(10);
        _play = Icon(PlayGlyph, Lang.T("播放 / 暫停（空白鍵）"), TogglePlay, BtnKind.Primary, 48);
        Sep();
        Icon(HasIcons ? "\uE892" : "⏮", Lang.T("返去開頭（Home）"), () => SeekTo(0, true));
        Icon(HasIcons ? "\uE76B" : "◀", Lang.T("前一幀（←）"), () => Step(-1));
        Icon(HasIcons ? "\uE76C" : "▶", Lang.T("後一幀（→）"), () => Step(1));
        Sep();
        Icon(HasIcons ? "\uE8D2" : "T", Lang.T("加文字：喺播放頭位置加一段標題 / 字幕"), AddText);
        Icon(HasIcons ? "\uE8B9" : "▣", Lang.T("加圖片：疊喺畫面上面，可以喺預覽度拖位置（亦可以直接拖相入預覽）"), AddOverlay);
        Icon(HasIcons ? "\uE8C6" : "✂", Lang.T("分割：喺播放頭位置切開揀咗嘅片段（S）"), Split);
        Icon(HasIcons ? "\uE8D6" : "♪", Lang.T("分離音軌：將揀咗嘅影片嘅聲音拆出嚟放落音樂軌"), DetachAudio);
        Icon(HasIcons ? "\uE71B" : "⧉", Lang.T("合併：將揀咗嘅相連片段合併成一段（Shift + 左鍵多選）"), Merge);
        Icon(HasIcons ? "\uE74D" : "🗑", Lang.T("刪除揀咗嘅片段（Delete）"), DeleteSel);
        Sep();
        _time = Ui.Text_("", Ui.Text, Ui.Mono(10f), new Padding(0));
        _time.Location = new Point(tx, Ui.S(10));
        transport.Controls.Add(_time);
        var zin = new FancyButton { Text = HasIcons ? "\uE8A3" : "＋", Kind = BtnKind.Ghost, Font = IconFont(10f), Size = new Size(Ui.S(34), Ui.S(30)) };
        var zout = new FancyButton { Text = HasIcons ? "\uE71F" : "－", Kind = BtnKind.Ghost, Font = IconFont(10f), Size = new Size(Ui.S(34), Ui.S(30)) };
        zin.Click += (_, _) => _tl.Zoom(1.4);
        zout.Click += (_, _) => _tl.Zoom(1 / 1.4);
        _tip.SetToolTip(zin, Lang.T("放大時間軸（Ctrl + 滾輪）"));
        _tip.SetToolTip(zout, Lang.T("縮細時間軸（Ctrl + 滾輪）"));
        // 預覽音量（淨係影響預覽，唔改影片）
        var vol = new EdVolume();
        _tip.SetToolTip(vol, Lang.T("預覽音量（淨係影響預覽，唔會改影片）：撳喇叭靜音，拖或者用滾輪調"));
        transport.Controls.AddRange(new Control[] { zin, zout, vol });
        transport.Resize += (_, _) =>
        {
            zin.Location = new Point(transport.Width - zin.Width, Ui.S(4));
            zout.Location = new Point(zin.Left - zout.Width - Ui.S(6), Ui.S(4));
            vol.Location = new Point(zout.Left - vol.Width - Ui.S(16), Ui.S(4));
        };
        _tl.Dock = DockStyle.Fill;
        _tl.HeightChanged += () => bottom.Height = _tl.WantHeight + Ui.S(46);
        bottom.Controls.Add(_tl);
        bottom.Controls.Add(transport);

        // ---- 中間：預覽 + 右邊設定 ----
        var mid = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding(Ui.S(20), Ui.S(4), Ui.S(20), Ui.S(8)) };
        _props.Dock = DockStyle.Right;
        _props.Width = Ui.S(350);
        _props.Padding = new Padding(0);
        _props.Controls.Add(_propFlow);
        var gap = new Panel { Dock = DockStyle.Right, Width = Ui.S(14), BackColor = Ui.Bg };
        _pv.Dock = DockStyle.Fill;
        mid.Controls.Add(_pv);
        mid.Controls.Add(gap);
        mid.Controls.Add(_props);

        Controls.Add(mid);
        Controls.Add(bottom);
        Controls.Add(top);

        _tl.P = _p;
        _tl.Seek += (t, final) => SeekTo(t, final);
        _tl.SelectionChanged += () => { _pv.Selected = _tl.Selected; _pv.Invalidate(); BuildProps(); };
        _pv.Picked += o => { if (_tl.Selected == o) return; _tl.Selected = o; _tl.Invalidate(); BuildProps(); };
        _pv.BeforeMove += PushUndoSoft;
        _pv.Moved += () => Dirty(true);
        _pv.LiveStart += o => _ = LiveBgAsync(o);
        _pv.Resize += (_, _) =>
        {
            int vl = _pv.FrameLong;
            if (Math.Abs(vl - EdPlayer.ViewLong) > 40) { EdPlayer.ViewLong = vl; RequestStill(); }
        };
        _pv.FilesDropped += (files, at) => _ = AddFilesAsync(files, at);
        _tl.AllowDrop = true;
        _tl.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        _tl.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) _ = AddFilesAsync(files, null, _tl.DropTarget(_tl.PointToClient(new Point(e.X, e.Y)))); };
        _tl.BeforeChange += PushUndo;
        _tl.Changed += () => Dirty(true);
        _player.Failed += msg => { try { BeginInvoke(() => { StopPlay(); _pv.Note = Lang.T("預覽出錯：") + msg; _pv.Invalidate(); }); } catch { } };
        _tick.Tick += (_, _) => OnTick();
        _save.Tick += (_, _) => { _save.Stop(); EdStore.Save(_p); };
        _still.Tick += (_, _) => { _still.Stop(); _ = ShowStillAsync(); };
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) _ = AddFilesAsync(files); };
        VisibleChanged += (_, _) => { if (!Visible) StopPlay(); else if (_needStill) RequestStill(); };
        Disposed += (_, _) => { _player.Dispose(); _tick.Dispose(); _save.Dispose(); _still.Dispose(); _tip.Dispose(); };
        Reload();
    }

    private readonly ToolTip _tip = new();

    // Windows 內置圖示字型（Win11 = Segoe Fluent Icons，Win10 = Segoe MDL2 Assets）；冇就用普通符號
    private static readonly string? IconFamily = new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }
        .FirstOrDefault(n => FontFamily.Families.Any(f => f.Name == n));
    internal static bool HasIcons => IconFamily != null;
    internal static Font IconFont(float size) => IconFamily != null ? new Font(IconFamily, Ui.S(size) * 72f / 96f, GraphicsUnit.Point) : Ui.F(size, FontStyle.Bold);
    private static string PlayGlyph => HasIcons ? "\uE768" : "▶";
    private static string PauseGlyph => HasIcons ? "\uE769" : "⏸";

    /// <summary>前一幀 / 後一幀。</summary>
    private void Step(int dir) => SeekTo(_tl.Playhead + dir / (double)Math.Max(1, _p.Fps), true);

    // ---------- 專案 ----------
    private void Reload()
    {
        _tl.Multi.Clear();
        StopPlay();
        _tl.P = _p;
        _tl.Selected = null;
        _tl.Playhead = Math.Min(_tl.Playhead, _p.Duration);
        _pv.Aspect = new Size(_p.W, _p.H);
        _pv.P = _p;
        _pv.Playhead = () => _tl.Playhead;
        _pv.Selected = null;
        _nameBox.Text = _p.Name;
        _tl.Refresh2();
        BuildProps();
        UpdateTime();
        RequestStill();
    }

    /// <summary>改影片名（= 專案名，匯出檔名）。</summary>
    private void CommitName()
    {
        string n = (_nameBox.Text ?? "").Trim();
        if (n.Length == 0 || n == _p.Name) { _nameBox.Text = _p.Name; return; }
        if (EdStore.List().Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase) && !string.Equals(x, _p.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Hint(Lang.T("已經有同名嘅專案。"));
            _nameBox.Text = _p.Name;
            return;
        }
        EdStore.Delete(_p.Name);
        _p.Name = n;
        EdStore.Save(_p);
    }

    private void ProjectMenu()
    {
        CommitName();
        var m = TUi.Menu(_projBtn);
        m.Items.Add(new ToolStripMenuItem(Lang.T("新專案"), null, (_, _) =>
        {
            EdStore.Save(_p);
            _p = new EdProject { Name = EdStore.NewName(), W = _p.W, H = _p.H, Fps = _p.Fps };
            _undo.Clear();
            EdStore.Save(_p);
            Reload();
        }) { ForeColor = Ui.Text });
        var open = new ToolStripMenuItem(Lang.T("開啟")) { ForeColor = Ui.Text };
        foreach (var n in EdStore.List())
        {
            string name = n;
            open.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
            {
                EdStore.Save(_p);
                var p = EdStore.Load(name);
                if (p == null) return;
                _p = p;
                _undo.Clear();
                EdStore.Save(_p);
                Reload();
            }) { Checked = name == _p.Name, BackColor = Ui.CardHi, ForeColor = Ui.Text });
        }
        try { open.DropDown.Renderer = DarkTheme.MenuRenderer(); } catch { }
        if (open.DropDownItems.Count == 0) open.Enabled = false;
        m.Items.Add(open);
        m.Items.Add(new ToolStripMenuItem(Lang.T("刪除呢個專案"), null, (_, _) =>
        {
            if (MessageBox.Show(FindForm(), Lang.F("刪除專案「{0}」？\n（你嘅影片 / 相片檔案唔會刪走）", _p.Name), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            EdStore.Delete(_p.Name);
            var next = EdStore.List().FirstOrDefault();
            _p = (next != null ? EdStore.Load(next) : null) ?? new EdProject { Name = EdStore.NewName() };
            _undo.Clear();
            EdStore.Save(_p);
            Reload();
        }) { ForeColor = Ui.Text });
        m.Show(_projBtn, new Point(0, _projBtn.Height));
    }

    // ---------- 改動 / 復原 ----------
    private void PushUndo()
    {
        _undo.Add(_p.ToJson());
        if (_undo.Count > 60) _undo.RemoveAt(0);
        _lastUndo = DateTime.Now;
    }

    /// <summary>設定欄改數值：一秒內連續改只記一次復原。</summary>
    private void PushUndoSoft()
    {
        if ((DateTime.Now - _lastUndo).TotalSeconds > 1) PushUndo();
        _lastUndo = DateTime.Now;
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var p = EdProject.FromJson(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        if (p == null) return;
        p.Name = _p.Name;
        _p = p;
        Reload();
        EdStore.Save(_p);
    }

    /// <summary>專案改咗：儲存（延遲）、重畫時間軸、更新預覽。</summary>
    private void Dirty(bool fromTimeline = false)
    {
        _save.Stop(); _save.Start();
        if (_player.Playing) StopPlay();
        _tl.Refresh2();
        _pv.Invalidate();
        UpdateTime();
        RequestStill();
        if (fromTimeline) BuildPropsValuesOnly();
    }

    // ---------- 加入 ----------
    private void AddMedia()
    {
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = Lang.T("影片 / 相片 / 聲音") + "|" + string.Join(";", MediaInfo.VideoExt.Concat(MediaInfo.ImageExt).Concat(MediaInfo.AudioExt).Select(x => "*" + x)) + "|" + Lang.T("所有檔案") + "|*.*"
        };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) _ = AddFilesAsync(dlg.FileNames);
    }

    /// <summary>加入檔案。at = 拖入預覽嘅位置：相片就變做疊圖放喺嗰度。</summary>
    private async Task AddFilesAsync(string[] files, PointF? at = null, (EdTrack Kind, int Row, double Time)? drop = null)
    {
        if (!Ffmpeg.Ready) { MessageBox.Show(FindForm(), Lang.T("未下載 ffmpeg：請去「工具庫」重新安裝影片剪輯。"), Program.AppName); return; }
        _pv.Note = Lang.T("讀緊檔案…");
        _pv.Invalidate();
        var errs = new List<string>();
        var added = new List<object>();
        foreach (var f in files.Where(File.Exists))
        {
            try
            {
                var info = await Task.Run(() => MediaInfo.Probe(f));
                bool img = MediaInfo.IsImage(f), aud = !info.Video || MediaInfo.IsAudio(f);
                // 拖落指定嘅軌（文字軌 / 主影片軌就照舊）
                if (drop is { } d && !(d.Kind == EdTrack.Text || (d.Kind == EdTrack.Vid && d.Row == 0)))
                {
                    double t0 = d.Time;
                    if (d.Kind == EdTrack.Aud)
                    {
                        if (!info.Audio) { errs.Add(Lang.F("「{0}」冇聲", Path.GetFileName(f))); continue; }
                        double len = info.Dur > 0 ? info.Dur : 10;
                        var m = new EdMusic { Path = f, In = 0, Out = len, SrcDur = info.Dur, Start = t0 };
                        m.Row = _p.OnRow(EdTrack.Aud, d.Row).Any(x => x.Start < t0 + len && t0 < x.Start + x.Len) ? _p.FreeRow(EdTrack.Aud, t0, len) : d.Row;
                        added.Add(m);
                        continue;
                    }
                    if (img)
                    {
                        var o = NewOverlay(f, info.W, info.H, 0.5, 0.5);
                        o.Start = t0;
                        o.Row = d.Kind == EdTrack.Vid && !_p.OnRow(EdTrack.Vid, d.Row).Any(x => x.Start < t0 + o.Dur && t0 < x.Start + x.Len) ? d.Row : _p.FreeRow(EdTrack.Vid, t0, o.Dur);
                        added.Add(o);
                        continue;
                    }
                    if (!aud)
                    {
                        var o = NewVideoOverlay(f, info.Dur, info.Audio, info.W, info.H);
                        o.Start = t0;
                        o.Row = d.Kind == EdTrack.Vid && !_p.OnRow(EdTrack.Vid, d.Row).Any(x => x.Start < t0 + o.Dur && t0 < x.Start + x.Len) ? d.Row : _p.FreeRow(EdTrack.Vid, t0, o.Dur);
                        added.Add(o);
                        continue;
                    }
                }
                if (MediaInfo.IsImage(f) && at != null)
                    added.Add(NewOverlay(f, info.W, info.H, at.Value.X, at.Value.Y));
                else if (MediaInfo.IsImage(f))
                    added.Add(new EdClip { Path = f, IsImage = true, In = 0, Out = 4, SrcW = info.W, SrcH = info.H });
                else if (!info.Video || MediaInfo.IsAudio(f))
                    added.Add(new EdMusic { Path = f, In = 0, Out = info.Dur > 0 ? info.Dur : 10, SrcDur = info.Dur });
                else
                    added.Add(new EdClip { Path = f, In = 0, Out = info.Dur > 0 ? info.Dur : 5, SrcDur = info.Dur, HasAudio = info.Audio, SrcW = info.W, SrcH = info.H });
            }
            catch (Exception ex) { errs.Add(ex.Message); }
        }
        _pv.Note = "";
        if (added.Count > 0)
        {
            PushUndo();
            int ins = _tl.Selected is EdClip sc ? _p.Clips.IndexOf(sc) + 1 : _p.Clips.Count;
            foreach (var o in added)
            {
                if (o is EdClip c) _p.Clips.Insert(ins++, c);
                else if (o is EdOverlay ov) _p.Overlays.Add(ov);
                else if (o is EdMusic m && drop is { Kind: EdTrack.Aud }) _p.Music.Add(m);
                else if (o is EdMusic m2) { m2.Start = _p.Music.Count == 0 ? 0 : _tl.Playhead; m2.Row = _p.FreeMusicRow(m2.Start, m2.Len); _p.Music.Add(m2); }
            }
            _tl.Selected = added.Last();
            _pv.Selected = _tl.Selected;
            BuildProps();
            Dirty();
        }
        if (errs.Count > 0) MessageBox.Show(FindForm(), string.Join("\n", errs), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _pv.Invalidate();
    }

    /// <summary>新疊圖：由播放頭開始 3 秒（如果喺片段入面就到片段完），闊度約畫面三成。</summary>
    private EdOverlay NewOverlay(string path, int w, int h, double x, double y)
    {
        double start = Math.Min(_tl.Playhead, Math.Max(0, _p.Duration));
        var o = new EdOverlay { Path = path, Start = start, Dur = 3, X = x, Y = y, Scale = w > 0 && h > w * 2 ? 0.15 : 0.3, ImgW = w, ImgH = h };
        o.Row = _p.FreeRow(EdTrack.Vid, o.Start, o.Dur);
        return o;
    }

    /// <summary>影片 2、3…軌（畫中畫）：預設擺喺右下角，闊度四成。</summary>
    private EdOverlay NewVideoOverlay(string path, double dur, bool audio, int w, int h) => new()
    {
        Path = path, IsVideo = true, In = 0, SrcDur = dur, Dur = dur > 0 ? dur : 5, HasAudio = audio,
        X = 0.72, Y = 0.72, Scale = 0.4, ImgW = w, ImgH = h, Fade = false, Row = 1
    };

    private void AddOverlay()
    {
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = Lang.T("相片") + "|" + string.Join(";", MediaInfo.ImageExt.Select(x => "*" + x))
        };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) _ = AddFilesAsync(dlg.FileNames, new PointF(0.5f, 0.5f));
    }

    private void AddText()
    {
        PushUndo();
        var t = new EdText { Text = Lang.T("輸入文字"), Start = Math.Min(_tl.Playhead, Math.Max(0, _p.Duration)), Dur = 3, X = 0.5, Y = 0.86 };
        t.Row = _p.FreeRow(EdTrack.Text, t.Start, t.Dur);
        _p.Texts.Add(t);
        _tl.Selected = t;
        _pv.Selected = t;
        BuildProps();
        Dirty();
    }

    private void Split()
    {
        double t = _tl.Playhead;
        var starts = _p.Starts();
        object? target = _tl.Selected;
        if (target == null || target is TransSel)
            for (int i = 0; i < _p.Clips.Count; i++)
                if (t > starts[i] && t < starts[i] + _p.Clips[i].Len) { target = _p.Clips[i]; break; }
        switch (target)
        {
            case EdClip c:
            {
                int i = _p.Clips.IndexOf(c);
                double local = t - starts[i];
                if (local < 0.1 || local > c.Len - 0.1) { Hint(Lang.T("將播放頭擺喺片段中間先可以分割")); return; }
                PushUndo();
                var b = EdProject.FromJson(System.Text.Json.JsonSerializer.Serialize(new EdProject { Clips = { c } }, new System.Text.Json.JsonSerializerOptions { IncludeFields = true }))!.Clips[0];
                if (c.IsImage) { b.Out = c.Len - local; c.Out = local; }
                else { double cut = c.In + local * c.Speed; b.In = cut; c.Out = cut; }
                c.Trans = "";
                _p.Clips.Insert(i + 1, b);
                _tl.Selected = b;
                break;
            }
            case EdText x:
            {
                double local = t - x.Start;
                if (local < 0.1 || local > x.Dur - 0.1) { Hint(Lang.T("將播放頭擺喺片段中間先可以分割")); return; }
                PushUndo();
                var y = new EdText { Text = x.Text, Start = t, Dur = x.Dur - local, Pos = x.Pos, X = x.X, Y = x.Y, Size = x.Size, Color = x.Color, Box = x.Box, Fade = x.Fade, Row = x.Row };
                x.Dur = local;
                _p.Texts.Add(y);
                _tl.Selected = y;
                break;
            }
            case EdOverlay o:
            {
                double local = t - o.Start;
                if (local < 0.1 || local > o.Dur - 0.1) { Hint(Lang.T("將播放頭擺喺片段中間先可以分割")); return; }
                PushUndo();
                var n = new EdOverlay { Path = o.Path, Start = t, Dur = o.Dur - local, X = o.X, Y = o.Y, Scale = o.Scale, Opacity = o.Opacity, Fade = o.Fade, ImgW = o.ImgW, ImgH = o.ImgH,
                    Row = o.Row, IsVideo = o.IsVideo, In = o.In + local, SrcDur = o.SrcDur, HasAudio = o.HasAudio, Mute = o.Mute, Volume = o.Volume };
                o.Dur = local;
                _p.Overlays.Add(n);
                _tl.Selected = n;
                break;
            }
            case EdMusic m:
            {
                double local = t - m.Start;
                if (local < 0.1 || local > m.Len - 0.1) { Hint(Lang.T("將播放頭擺喺片段中間先可以分割")); return; }
                PushUndo();
                double cutAt = m.In + local * m.Sp;
                var n = new EdMusic { Path = m.Path, Start = t, In = cutAt, Out = m.Out, SrcDur = m.SrcDur, Volume = m.Volume, FadeIn = 0, FadeOut = m.FadeOut, Speed = m.Speed, Row = m.Row };
                m.Out = cutAt;
                m.FadeOut = 0;
                _p.Music.Add(n);
                _tl.Selected = n;
                break;
            }
            default:
                Hint(Lang.T("先揀一段片，再將播放頭擺喺要切開嘅位置"));
                return;
        }
        BuildProps();
        Dirty();
    }

    /// <summary>將影片片段嘅聲音拆出嚟：音樂軌加一段同位置、同速度嘅音軌，原片段靜音。</summary>
    private void DetachAudio()
    {
        if (_tl.Selected is not EdClip c || c.IsImage) { Hint(Lang.T("先揀一段有聲嘅影片，再撳分離音軌")); return; }
        if (!c.HasAudio) { Hint(c.Detached ? Lang.T("呢段片嘅聲音已經分離咗") : Lang.T("呢段片冇聲")); return; }
        int i = _p.Clips.IndexOf(c);
        if (i < 0) return;
        PushUndo();
        var m = new EdMusic
        {
            Path = c.Path, Start = _p.Starts()[i], In = c.In, Out = c.Out, SrcDur = c.SrcDur,
            Volume = c.Volume, Speed = c.Speed, FadeIn = c.FadeIn, FadeOut = c.FadeOut
        };
        // 段片本身唔再出聲（唔係淨係剔靜音）：聲音只會由音樂軌嗰段播
        m.Row = _p.FreeMusicRow(m.Start, m.Len);
        c.HasAudio = false;
        c.Detached = true;
        c.Mute = false;
        _p.Music.Add(m);
        _tl.Selected = m;
        _pv.Selected = m;
        BuildProps();
        Dirty();
        Hint(Lang.T("已經分離音軌：聲音放咗落音樂軌，原片段唔再有聲"));
    }

    private void MultiProps(List<object> all, List<PropSection> add)
    {
        var sec = Section(Lang.F("已揀 {0} 段", all.Count), "multi");
        int clips = all.Count(o => o is EdClip);
        sec.Add(Small(Lang.T("Shift + 左鍵可以再加 / 減。合併只計影片 1 軌上面相連嘅片段。")));
        var mg = TUi.Btn(Lang.T("合併"), Merge);
        mg.Margin = new Padding(0, 0, Ui.S(6), Ui.S(4));
        mg.Enabled = clips >= 2;
        var del = TUi.Btn(Lang.T("全部刪除"), DeleteSel);
        del.Margin = new Padding(0, 0, 0, Ui.S(4));
        sec.Add(Row(mg, del));
        add.Add(sec);
    }

    /// <summary>
    /// 合併影片 1 軌上面揀咗嘅相連片段：
    /// 同一條片、頭尾駁得埋（例如分割完）→ 即刻變返一段；否則用 ffmpeg 砌成一條新片再放返入去。
    /// </summary>
    private async void Merge()
    {
        var sel = _tl.SelectedAll().OfType<EdClip>().Where(c => _p.Clips.Contains(c)).OrderBy(c => _p.Clips.IndexOf(c)).ToList();
        if (sel.Count < 2) { Hint(Lang.T("用 Shift + 左鍵揀兩段或以上影片 1 軌嘅片段先合併得")); return; }
        var idx = sel.Select(c => _p.Clips.IndexOf(c)).ToList();
        for (int k = 1; k < idx.Count; k++)
            if (idx[k] != idx[k - 1] + 1) { Hint(Lang.T("要揀相連嘅片段先合併得")); return; }
        StopPlay();
        var first = sel[0];
        var last = sel[^1];
        bool same(EdClip a, EdClip b) => a.Path == b.Path && !a.IsImage && !b.IsImage && Math.Abs(a.Speed - b.Speed) < 0.001 && Math.Abs(a.Volume - b.Volume) < 0.001
            && a.Mute == b.Mute && a.HasAudio == b.HasAudio && a.Bright == b.Bright && a.Contrast == b.Contrast && a.Sat == b.Sat && a.Gray == b.Gray
            && a.Rotate == b.Rotate && a.Flip == b.Flip && a.Fill == b.Fill;
        bool quick = true;
        for (int k = 1; k < sel.Count; k++)
            if (!same(sel[k - 1], sel[k]) || Math.Abs(sel[k].In - sel[k - 1].Out) > 0.05 || sel[k - 1].Trans.Length > 0) { quick = false; break; }
        if (quick)
        {
            PushUndo();
            first.Out = last.Out;
            first.Trans = last.Trans;
            first.TransDur = last.TransDur;
            first.FadeOut = last.FadeOut;
            foreach (var c in sel.Skip(1)) _p.Clips.Remove(c);
            AfterMerge(first);
            return;
        }
        // 唔同嘅片：砌成一條新片
        var tmpP = new EdProject { Name = _p.Name, W = _p.W, H = _p.H, Fps = _p.Fps };
        foreach (var c in sel) tmpP.Clips.Add(EdProject.FromJson(System.Text.Json.JsonSerializer.Serialize(new EdProject { Clips = { c } }, new System.Text.Json.JsonSerializerOptions { IncludeFields = true }))!.Clips[0]);
        tmpP.Clips[^1].Trans = "";
        string dir = Path.Combine(EdStore.Dir, "merged");
        Directory.CreateDirectory(dir);
        string outFile = Path.Combine(dir, Scripts.Script.Sanitize(_p.Name) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mp4");
        _pv.Note = Lang.T("合併緊…");
        _pv.Invalidate();
        Enabled = false;
        string err = "";
        int code = await Task.Run(() =>
        {
            try
            {
                string args = EdGraph.Args(tmpP, 0, tmpP.W, tmpP.H, true, true, "merge", out _) +
                              $"-map [vout] -map [aout] -c:v libx264 -preset veryfast -crf 16 -pix_fmt yuv420p -r {tmpP.Fps} -c:a aac -b:a 192k -y \"{outFile}\"";
                var psi = new ProcessStartInfo(Ffmpeg.Exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using var pr = Process.Start(psi)!;
                err = pr.StandardError.ReadToEnd();
                pr.WaitForExit();
                return pr.ExitCode;
            }
            catch (Exception ex) { err = ex.Message; return -1; }
        });
        Enabled = true;
        _pv.Note = "";
        if (code != 0 || !File.Exists(outFile))
        {
            MessageBox.Show(FindForm(), Lang.T("合併失敗：") + err.Trim().Split('\n').LastOrDefault(), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _pv.Invalidate();
            return;
        }
        double dur = tmpP.VideoEnd;
        try { dur = MediaInfo.Probe(outFile).Dur; } catch { }
        if (_p.Clips.IndexOf(first) is int at && at < 0) return;
        PushUndo();
        int pos = _p.Clips.IndexOf(first);
        var merged = new EdClip { Path = outFile, In = 0, Out = dur, SrcDur = dur, HasAudio = true, Trans = last.Trans, TransDur = last.TransDur };
        foreach (var c in sel) _p.Clips.Remove(c);
        _p.Clips.Insert(Math.Clamp(pos, 0, _p.Clips.Count), merged);
        AfterMerge(merged);
    }

    private void AfterMerge(EdClip c)
    {
        _tl.Multi.Clear();
        _tl.Selected = c;
        _pv.Selected = c;
        BuildProps();
        Dirty();
        Hint(Lang.T("已經合併"));
    }

    private void DeleteSel()
    {
        var all = _tl.SelectedAll();
        if (all.Count > 1)
        {
            PushUndo();
            foreach (var o in all)
                switch (o) { case EdClip c: _p.Clips.Remove(c); break; case EdText t: _p.Texts.Remove(t); break; case EdOverlay ov: _p.Overlays.Remove(ov); break; case EdMusic m: _p.Music.Remove(m); break; }
            _tl.Multi.Clear();
            _tl.Selected = null;
            BuildProps();
            Dirty();
            return;
        }
        switch (_tl.Selected)
        {
            case EdClip c: PushUndo(); _p.Clips.Remove(c); break;
            case EdText t: PushUndo(); _p.Texts.Remove(t); break;
            case EdOverlay o: PushUndo(); _p.Overlays.Remove(o); break;
            case EdMusic m: PushUndo(); _p.Music.Remove(m); break;
            case TransSel ts when ts.Index < _p.Clips.Count: PushUndo(); _p.Clips[ts.Index].Trans = ""; break;
            default: return;
        }
        _tl.Selected = null;
        BuildProps();
        Dirty();
    }

    private void Hint(string s) => Tools.Toast.Show(s, Ui.Text, 2500);

    // ---------- 播放 ----------
    private void TogglePlay()
    {
        if (_player.Playing) { StopPlay(); return; }
        if (!Ffmpeg.Ready) { MessageBox.Show(FindForm(), Lang.T("未下載 ffmpeg：請去「工具庫」重新安裝影片剪輯。"), Program.AppName); return; }
        if (_p.Duration <= 0.05) return;
        if (_tl.Playhead >= _p.Duration - 0.1) _tl.Playhead = 0;
        _stillCts?.Cancel();
        EdStore.Save(_p);
        _pv.Note = "";
        try { _player.Start(_p, _tl.Playhead); }
        catch (Exception ex) { _pv.Note = Lang.T("預覽出錯：") + ex.Message; _pv.Invalidate(); return; }
        _play.Text = PauseGlyph;
        _tick.Start();
    }

    private void StopPlay()
    {
        if (!_player.Playing && !_tick.Enabled) return;
        _tl.Playhead = Math.Min(_player.Clock, _p.Duration);
        _player.Stop();
        _tick.Stop();
        _play.Text = PlayGlyph;
        _tl.Invalidate();
        UpdateTime();
    }

    private void OnTick()
    {
        if (!_player.Playing) { _tick.Stop(); return; }
        var f = _player.TakeFrame();
        if (f != null) _pv.SetImage(f);
        _tl.Playhead = Math.Min(_player.Clock, _p.Duration);
        _pv.Invalidate();
        _tl.Follow();
        _tl.Invalidate();
        UpdateTime();
        if (_player.Finished) StopPlay();
    }

    private bool _resume; // 播放中拖時間軸：放手之後繼續播

    private void SeekTo(double t, bool final)
    {
        if (_player.Playing) { StopPlay(); _resume = true; }
        _tl.Playhead = Math.Clamp(t, 0, Math.Max(0, _p.Duration));
        _tl.Invalidate();
        _pv.Invalidate();
        UpdateTime();
        if (final && _resume)
        {
            _resume = false;
            if (_tl.Playhead < _p.Duration - 0.1) { TogglePlay(); return; }
        }
        RequestStill();
    }

    private void UpdateTime()
    {
        int fps = Math.Max(1, _p.Fps);
        long total = Math.Max(1, (long)Math.Round(_p.Duration * fps));
        long frame = Math.Clamp((long)Math.Floor(_tl.Playhead * fps + 1e-6), 0, total);
        _time.Text = EdTimeline.Time(_tl.Playhead) + "  /  " + EdTimeline.Time(_p.Duration) + "     " + frame + " / " + total;
    }

    private void RequestStill()
    {
        if (_player.Playing) return;
        _still.Stop();
        _still.Start();
    }

    private bool _needStill;

    // ---------- 預覽拖緊：背景換成「冇咗嗰樣嘢」嘅畫面，嗰樣嘢喺本機即時畫 ----------
    private readonly Dictionary<string, Bitmap> _imgCache = new();
    private Bitmap? _liveThumb;

    private Bitmap? LoadImg(string path)
    {
        if (_imgCache.TryGetValue(path, out var b)) return b;
        try
        {
            using var fs = File.OpenRead(path);
            using var src = Image.FromStream(fs);
            double k = Math.Min(1, 1280.0 / Math.Max(src.Width, src.Height));
            var bmp = new Bitmap(src, Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
            _imgCache[path] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    private async Task LiveBgAsync(object o)
    {
        if (!Ffmpeg.Ready || _player.Playing) return;
        double at = Math.Min(_tl.Playhead, Math.Max(0, _p.Duration - 0.05));
        // 嗰樣嘢嘅樣
        _pv.LiveImg = null;
        if (o is EdClip clip)
        {
            int ci = _p.Clips.IndexOf(clip);
            if (ci < 0) return;
            string path = clip.Path;
            double t = clip.IsImage ? 0 : clip.In + Math.Max(0, at - _p.Starts()[ci]) * clip.Speed;
            var src = clip.IsImage ? LoadImg(path) : null;
            var th = clip.IsImage ? (src != null ? new Bitmap(src) : null)
                                  : await Task.Run(() => { try { return MediaInfo.Thumb(path, t, 480); } catch { return null; } });
            if (_pv.Live != o) { th?.Dispose(); return; }
            if (th != null)
            {
                var rf = clip.Rotate switch { 90 => RotateFlipType.Rotate90FlipNone, 180 => RotateFlipType.Rotate180FlipNone, 270 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone };
                if (clip.Flip) rf = rf switch
                {
                    RotateFlipType.Rotate90FlipNone => RotateFlipType.Rotate90FlipX, RotateFlipType.Rotate180FlipNone => RotateFlipType.Rotate180FlipX,
                    RotateFlipType.Rotate270FlipNone => RotateFlipType.Rotate270FlipX, _ => RotateFlipType.RotateNoneFlipX
                };
                th.RotateFlip(rf);
            }
            var old = _liveThumb; _liveThumb = th; _pv.LiveImg = th; old?.Dispose();
            _pv.Invalidate();
            return;
        }
        if (o is EdOverlay ov)
        {
            if (!ov.IsVideo) _pv.LiveImg = LoadImg(ov.Path);
            else
            {
                string path = ov.Path; double t = ov.In + Math.Max(0, at - ov.Start);
                var th = await Task.Run(() => { try { return MediaInfo.Thumb(path, t, 480); } catch { return null; } });
                if (_pv.Live != o) { th?.Dispose(); return; }
                var old = _liveThumb; _liveThumb = th; _pv.LiveImg = th; old?.Dispose();
            }
            _pv.Invalidate();
        }
        // 冇咗佢嘅背景
        var snap = _p.Clone();
        if (o is EdOverlay o2 && _p.Overlays.IndexOf(o2) is int i && i >= 0) snap.Overlays.RemoveAt(i);
        else if (o is EdText t2 && _p.Texts.IndexOf(t2) is int j && j >= 0) snap.Texts.RemoveAt(j);
        else return;
        _stillCts?.Cancel();
        var cts = _stillCts = new CancellationTokenSource();
        int gen = ++_stillGen;
        try
        {
            var bmp = await Task.Run(() => EdPlayer.Still(snap, at, cts.Token));
            if (gen != _stillGen || cts.IsCancellationRequested || _pv.Live != o) { bmp?.Dispose(); return; }
            _pv.SetImage(bmp, keepLive: true);
        }
        catch { }
    }

    private async Task ShowStillAsync()
    {
        // 未打開呢頁（例如啱啱開 MultiTool）：唔好喺背景行 ffmpeg，等打開先整
        if (!Visible || !IsHandleCreated) { _needStill = true; return; }
        _needStill = false;
        if (!Ffmpeg.Ready || _player.Playing) return;
        if (_p.Duration <= 0.05) { _pv.SetImage(null); _pv.Note = ""; return; }
        _stillCts?.Cancel();
        var cts = _stillCts = new CancellationTokenSource();
        int gen = ++_stillGen;
        var snap = _p.Clone();
        double at = Math.Min(_tl.Playhead, Math.Max(0, snap.Duration - 0.05));
        try
        {
            var bmp = await Task.Run(() => EdPlayer.Still(snap, at, cts.Token));
            if (gen != _stillGen || cts.IsCancellationRequested || _player.Playing) { bmp?.Dispose(); return; }
            _pv.Note = "";
            _pv.SetImage(bmp);
        }
        catch (Exception ex)
        {
            if (gen != _stillGen) return;
            AppPaths.LogError("剪片預覽", ex);
            _pv.Note = Lang.T("預覽出錯：") + ex.Message;
            _pv.Invalidate();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        bool typing = GetFocused() is TextBoxBase;
        if (!typing && Visible)
        {
            switch (keyData)
            {
                case Keys.Space: TogglePlay(); return true;
                case Keys.Delete: DeleteSel(); return true;
                case Keys.S: Split(); return true;
                case Keys.Control | Keys.Z: Undo(); return true;
                case Keys.Left: Step(-1); return true;
                case Keys.Right: Step(1); return true;
                case Keys.Home: SeekTo(0, true); return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private Control? GetFocused()
    {
        Control? a = FindForm()?.ActiveControl;
        while (a is ContainerControl ac && ac.ActiveControl != null) a = ac.ActiveControl;
        return a;
    }

    // ---------- 右邊設定（一格格有外框、可以摺埋嘅分組）----------
    private readonly List<Action> _valueSync = new(); // 拖時間軸 / 預覽時更新數字（唔重建）
    private int SecW => _props.ClientSize.Width - _props.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;

    private void BuildPropsValuesOnly()
    {
        foreach (var a in _valueSync) a();
    }

    private Label Small(string t) { var l = Ui.Text_(t, Ui.TextFaint, Ui.F(8.75f), new Padding(0, 0, 0, Ui.S(8))); l.BackColor = Ui.Card; l.MaximumSize = new Size(Math.Max(Ui.S(200), SecW - Ui.S(28)), 0); return l; }

    private static Label RowLabel(string label)
    {
        var l = Ui.Text_(label, Ui.TextDim, Ui.F(9.5f), new Padding(0, Ui.S(8), Ui.S(8), 0));
        l.AutoSize = false;
        l.Width = Ui.S(88);
        l.Height = Ui.S(30);
        l.BackColor = Ui.Card;
        return l;
    }

    private static FlowLayoutPanel Row(params Control[] cs)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Ui.Card, Margin = new Padding(0, 0, 0, Ui.S(6)) };
        foreach (var c in cs) row.Controls.Add(c);
        return row;
    }

    private Control Num(string label, double value, double min, double max, int dec, double inc, Action<double> set, Func<double>? get = null, string unit = "")
    {
        var nb = new NumberBox(Ui.S(110)) { Minimum = (decimal)min, Maximum = (decimal)max, DecimalPlaces = dec, Increment = (decimal)inc };
        nb.Value = (decimal)Math.Clamp(value, min, max);
        bool syncing = false;
        nb.ValueChanged += (_, _) => { if (syncing) return; PushUndoSoft(); set((double)nb.Value); Dirty(); };
        if (get != null) _valueSync.Add(() => { syncing = true; try { nb.Value = (decimal)Math.Clamp(get(), min, max); } finally { syncing = false; } });
        nb.Margin = new Padding(0);
        var row = Row(RowLabel(label), nb);
        if (unit.Length > 0) row.Controls.Add(Ui.Text_(unit, Ui.TextDim, Ui.F(9.5f), new Padding(Ui.S(6), Ui.S(8), 0, 0)));
        return row;
    }

    private CheckBox Check(string text, bool v, Action<bool> set)
    {
        var cb = TUi.Check(text, v, x => { PushUndo(); set(x); Dirty(); });
        cb.Margin = new Padding(0, Ui.S(2), 0, Ui.S(8));
        return cb;
    }

    private Control Pick<T>(string label, IEnumerable<(string Name, T Value)> items, T cur, Action<T> set, bool rebuild = false)
    {
        var list = items.ToList();
        FancyButton? b = null;
        b = new FancyButton { Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Text = (list.FirstOrDefault(x => EqualityComparer<T>.Default.Equals(x.Value, cur)).Name ?? Lang.T("自訂")) + "  ▾" };
        b.Size = new Size(Math.Max(Ui.S(150), SecW - Ui.S(28) - Ui.S(96)), Ui.S(32));
        b.Click += (_, _) => TUi.Pick(b, list, cur, v =>
        {
            PushUndo();
            cur = v;
            set(v);
            b.Text = list.First(x => EqualityComparer<T>.Default.Equals(x.Value, v)).Name + "  ▾";
            Dirty();
            if (rebuild) BeginInvoke(BuildProps);
        });
        b.Margin = new Padding(0);
        return Row(RowLabel(label), b);
    }

    /// <summary>顏色：黑 / 白 / 自己揀。</summary>
    private Control ColorRow(string label, string cur, Action<string> set)
    {
        int rgb = int.TryParse((cur ?? "").TrimStart('#'), NumberStyles.HexNumber, null, out var x) ? x : 0;
        var sw = new Scripts.ColorSwatch(rgb) { Margin = new Padding(0, 0, Ui.S(6), 0) };
        sw.Width = Ui.S(104);
        bool quiet = false;
        sw.ColorChanged += v => { if (quiet) return; PushUndo(); set($"#{v:X6}"); Dirty(); };
        FancyButton Q(string t, int v)
        {
            var b = new FancyButton { Text = t, Kind = BtnKind.Ghost, Font = Ui.F(8.75f), Size = new Size(Ui.S(36), Ui.S(32)), Margin = new Padding(0, 0, Ui.S(4), 0) };
            b.Click += (_, _) => sw.Value = v;
            return b;
        }
        return Row(RowLabel(label), sw, Q(Lang.T("黑"), 0x000000), Q(Lang.T("白"), 0xFFFFFF));
    }

    private PropSection Section(string title, string key, bool open = true)
    {
        var sec = new PropSection(title, key, open, SecW);
        sec.Toggled += () => _propFlow.PerformLayout();
        return sec;
    }

    private void BuildProps()
    {
        _propFlow.SuspendLayout();
        foreach (Control c in _propFlow.Controls.Cast<Control>().ToList()) { _propFlow.Controls.Remove(c); c.Dispose(); }
        _valueSync.Clear();
        var add = new List<PropSection>();
        var all = _tl.SelectedAll();
        if (all.Count > 1) MultiProps(all, add);
        else switch (_tl.Selected)
        {
            case EdClip c: ClipProps(c, add); break;
            case EdText t: TextProps(t, add); break;
            case EdOverlay o: OverlayProps(o, add); break;
            case EdMusic m: MusicProps(m, add); break;
            case TransSel ts when ts.Index + 1 < _p.Clips.Count:
            {
                var sec = Section(Lang.T("轉場"), "trans");
                sec.Add(Small(Lang.F("「{0}」→「{1}」", _p.Clips[ts.Index].Name, _p.Clips[ts.Index + 1].Name)));
                TransProps(_p.Clips[ts.Index], sec);
                add.Add(sec);
                break;
            }
            default: ProjectProps(add); break;
        }
        foreach (var s in add) { s.Margin = new Padding(0, 0, 0, Ui.S(10)); s.Relayout(); }
        _propFlow.Controls.AddRange(add.ToArray());
        _propFlow.ResumeLayout(true);
        _props.AutoScrollPosition = Point.Empty;
    }

    /// <summary>揀咗「自訂…」：就算闊高啱好係某個比例，都照顯示闊 / 高輸入格。</summary>
    private bool _customRatio;

    private static readonly (string Name, int A, int B)[] Ratios =
    {
        ("16:9", 16, 9), ("9:16", 9, 16), ("1:1", 1, 1), ("4:3", 4, 3), ("3:4", 3, 4), ("4:5", 4, 5), ("5:4", 5, 4),
        ("3:2", 3, 2), ("2:3", 2, 3), ("21:9", 21, 9), ("2:1", 2, 1)
    };

    private static string RatioLabel(string r) => r switch
    {
        "16:9" => Lang.T("16:9 橫向（YouTube）"),
        "9:16" => Lang.T("9:16 直向（Shorts / Reels / TikTok）"),
        "1:1" => Lang.T("1:1 正方"),
        "4:5" => Lang.T("4:5 直向（IG 帖文）"),
        "21:9" => Lang.T("21:9 電影闊銀幕"),
        "custom" => Lang.T("自訂…"),
        _ => r
    };

    private void ProjectProps(List<PropSection> add)
    {
        var sec = Section(Lang.T("影片設定"), "project");
        string cur = _customRatio ? "custom" : Ratios.FirstOrDefault(r => _p.W * r.B == _p.H * r.A).Name ?? "custom";
        int quality = Math.Min(_p.W, _p.H);
        if (!new[] { 720, 1080, 1440, 2160 }.Contains(quality)) quality = 1080;
        void Apply(string ratio, int q)
        {
            var r = Ratios.FirstOrDefault(x => x.Name == ratio);
            if (r.Name == null) return;
            // 短邊 = 畫質（720 / 1080 / 1440 / 2160）
            if (r.A >= r.B) { _p.H = q; _p.W = (int)Math.Round(q * r.A / (double)r.B / 2) * 2; }
            else { _p.W = q; _p.H = (int)Math.Round(q * r.B / (double)r.A / 2) * 2; }
            _pv.Aspect = new Size(_p.W, _p.H);
        }
        sec.Add(Pick(Lang.T("畫面比例"), Ratios.Select(r => (RatioLabel(r.Name), r.Name)).Append((RatioLabel("custom"), "custom")), cur, v =>
        {
            cur = v;
            _customRatio = v == "custom";
            if (v != "custom") Apply(v, quality);
        }, rebuild: true));
        if (cur == "custom")
        {
            sec.Add(Num(Lang.T("闊"), _p.W, 64, 7680, 0, 2, v => { _p.W = (int)Math.Round(v / 2) * 2; _pv.Aspect = new Size(_p.W, _p.H); }, () => _p.W, "px"));
            sec.Add(Num(Lang.T("高"), _p.H, 64, 7680, 0, 2, v => { _p.H = (int)Math.Round(v / 2) * 2; _pv.Aspect = new Size(_p.W, _p.H); }, () => _p.H, "px"));
        }
        else
            sec.Add(Pick(Lang.T("畫質"), new[] { ("720p", 720), ("1080p", 1080), ("1440p (2K)", 1440), ("2160p (4K)", 2160) }, quality, v => { quality = v; Apply(cur, v); }, rebuild: true));
        sec.Add(Pick("FPS", new[] { ("24", 24), ("30", 30), ("60", 60) }, _p.Fps, v => _p.Fps = v));
        sec.Add(Small(Lang.F("而家：{0} × {1}，長度 {2}", _p.W, _p.H, EdTimeline.Time(_p.Duration))));
        add.Add(sec);
        var help = Section(Lang.T("點用"), "help", false);
        help.Add(Small(Lang.T("• 撳「+ 加入媒體」或者拖檔案入嚟：影片 / 相片入影片軌，聲音檔入音樂軌。\n• 撳時間軸上嘅片段揀佢，右邊會出設定；拖兩邊可以剪頭尾，拖中間可以搬位。\n• 片段之間嘅「⇄」係轉場。\n• 空白鍵播放 / 暫停，S 分割，Delete 刪除，Ctrl+Z 復原，← → 逐格。\n• 專案會自動儲存。")));
        help.Add(Small(Lang.T("• 相片拖入預覽畫面 = 疊圖（貼圖 / logo）；文字同疊圖都可以喺預覽度拖位置，滾輪改大細。")));
        help.Add(Small(Lang.T("• 每種軌（文字 / 影片 / 音軌）軌名旁邊撳「+」可以加多一條軌，可以重疊；上下拖可以換軌；拖軌名可以調次序；右鍵軌名可以刪走空軌。影片 2 軌以上 = 疊喺主影片上面（畫中畫 / 貼圖）。")));
        add.Add(help);
    }

    private void ClipProps(EdClip c, List<PropSection> add)
    {
        var main = Section(c.Name, "clip");
        main.Add(Small(c.IsImage ? Lang.T("相片") : Lang.F("原片長度 {0}{1}", EdTimeline.Time(c.SrcDur), c.HasAudio ? "" : c.Detached ? Lang.T("（聲音已分離到音樂軌）") : Lang.T("（冇聲）"))));
        if (c.IsImage)
            main.Add(Num(Lang.T("顯示"), c.Out, 0.2, 3600, 1, 0.5, v => c.Out = v, () => c.Out, Lang.T("秒")));
        else
        {
            main.Add(Num(Lang.T("開始"), c.In, 0, Math.Max(0.1, c.SrcDur), 2, 0.1, v => c.In = Math.Min(v, c.Out - 0.1), () => c.In, Lang.T("秒")));
            main.Add(Num(Lang.T("結束"), c.Out, 0.1, Math.Max(0.2, c.SrcDur > 0 ? c.SrcDur : 1e6), 2, 0.1, v => c.Out = Math.Max(v, c.In + 0.1), () => c.Out, Lang.T("秒")));
            main.Add(Num(Lang.T("速度"), c.Speed, 0.25, 4, 2, 0.25, v => c.Speed = v, null, "x"));
            if (c.HasAudio)
            {
                main.Add(Num(Lang.T("音量"), c.Volume * 100, 0, 200, 0, 10, v => c.Volume = v / 100, null, "%"));
                main.Add(Check(Lang.T("靜音"), c.Mute, v => c.Mute = v));
                if (!c.Mute)
                {
                    var det = TUi.Btn(Lang.T("分離音軌"), DetachAudio);
                    det.Margin = new Padding(0, 0, 0, Ui.S(4));
                    _tip.SetToolTip(det, Lang.T("分離音軌：將揀咗嘅影片嘅聲音拆出嚟放落音樂軌"));
                    main.Add(det);
                }
            }
        }
        add.Add(main);

        var fade = Section(Lang.T("淡入淡出"), "fade", c.FadeIn > 0 || c.FadeOut > 0);
        fade.Add(Small(Lang.T("片段開頭由顏色慢慢淡入、結尾淡出去顏色，接駁兩段片好用；聲音會跟住淡入淡出。")));
        fade.Add(Num(Lang.T("開頭淡入"), c.FadeIn, 0, 10, 1, 0.5, v => c.FadeIn = v, null, Lang.T("秒")));
        fade.Add(Num(Lang.T("結尾淡出"), c.FadeOut, 0, 10, 1, 0.5, v => c.FadeOut = v, null, Lang.T("秒")));
        fade.Add(ColorRow(Lang.T("顏色"), c.FadeColor, v => c.FadeColor = v));
        add.Add(fade);

        var pic = Section(Lang.T("畫面"), "picture", false);
        pic.Add(Pick(Lang.T("大細"), new[] { (Lang.T("完整顯示（加黑邊）"), false), (Lang.T("填滿（裁走多出）"), true) }, c.Fill, v => c.Fill = v));
        pic.Add(Pick(Lang.T("旋轉"), new[] { ("0°", 0), ("90°", 90), ("180°", 180), ("270°", 270) }, c.Rotate, v => c.Rotate = v));
        pic.Add(Check(Lang.T("左右反轉"), c.Flip, v => c.Flip = v));
        add.Add(pic);

        var pos = Section(Lang.T("位置及大細"), "clippos", c.Moved);
        pos.Add(Small(Lang.T("喺預覽畫面拖中間搬位，拖四角（或者滾輪）改大細。")));
        pos.Add(Num(Lang.T("大細"), c.Zoom * 100, 10, 500, 0, 5, v => c.Zoom = v / 100, () => c.Zoom * 100, "%"));
        PosRows(pos, () => (c.X, c.Y), (x, y) => { c.X = x; c.Y = y; });
        var center = TUi.Btn(Lang.T("重設位置及大細"), () => { PushUndo(); c.X = 0.5; c.Y = 0.5; c.Zoom = 1; BuildProps(); Dirty(); });
        center.Margin = new Padding(0, 0, 0, Ui.S(4));
        pos.Add(center);
        add.Add(pos);

        bool filtered = Math.Abs(c.Bright) > 0.001 || Math.Abs(c.Contrast - 1) > 0.001 || Math.Abs(c.Sat - 1) > 0.001 || c.Gray;
        var flt = Section(Lang.T("濾鏡"), "filter", filtered);
        flt.Add(Num(Lang.T("亮度"), c.Bright * 100, -100, 100, 0, 5, v => c.Bright = v / 100));
        flt.Add(Num(Lang.T("對比"), c.Contrast * 100, 0, 200, 0, 5, v => c.Contrast = v / 100, null, "%"));
        flt.Add(Num(Lang.T("飽和度"), c.Sat * 100, 0, 300, 0, 5, v => c.Sat = v / 100, null, "%"));
        flt.Add(Check(Lang.T("黑白"), c.Gray, v => c.Gray = v));
        var reset = TUi.Btn(Lang.T("重設濾鏡"), () => { PushUndo(); c.Bright = 0; c.Contrast = 1; c.Sat = 1; c.Gray = false; BuildProps(); Dirty(); });
        reset.Margin = new Padding(0, 0, 0, Ui.S(4));
        flt.Add(reset);
        add.Add(flt);

        int i = _p.Clips.IndexOf(c);
        if (i >= 0 && i + 1 < _p.Clips.Count)
        {
            var tr = Section(Lang.T("轉場（去下一段）"), "trans", c.Trans.Length > 0);
            TransProps(c, tr);
            add.Add(tr);
        }
    }

    private void TransProps(EdClip c, PropSection sec)
    {
        sec.Add(Pick(Lang.T("效果"), new[] { "" }.Concat(EdGraph.Transitions).Select(t => (EdGraph.TransName(t), t)), c.Trans, v => { c.Trans = v; _tl.Invalidate(); }));
        sec.Add(Num(Lang.T("長度"), c.TransDur, 0.1, 5, 1, 0.1, v => c.TransDur = v, null, Lang.T("秒")));
    }

    /// <summary>位置（%）：同預覽度拖嘅同步。</summary>
    private void PosRows(PropSection sec, Func<(double X, double Y)> get, Action<double, double> set)
    {
        sec.Add(Num(Lang.T("橫向位置"), get().X * 100, 0, 100, 0, 1, v => set(v / 100, get().Y), () => get().X * 100, "%"));
        sec.Add(Num(Lang.T("直向位置"), get().Y * 100, 0, 100, 0, 1, v => set(get().X, v / 100), () => get().Y * 100, "%"));
        FancyButton Q(string t, double y)
        {
            var b = new FancyButton { Text = t, Kind = BtnKind.Ghost, Font = Ui.F(8.75f), Size = new Size(Ui.S(52), Ui.S(30)), Margin = new Padding(0, 0, Ui.S(4), 0) };
            b.Click += (_, _) => { PushUndo(); set(0.5, y); BuildPropsValuesOnly(); Dirty(); };
            return b;
        }
        var lbl = RowLabel(Lang.T("快速"));
        sec.Add(Row(lbl, Q(Lang.T("上"), 0.12), Q(Lang.T("中"), 0.5), Q(Lang.T("下"), 0.86)));
    }

    private void TextProps(EdText t, List<PropSection> add)
    {
        var sec = Section(Lang.T("文字"), "text");
        var tb = new TextBox
        {
            Multiline = true, Text = t.Text.Replace("\n", "\r\n"), Font = Ui.F(10.5f), BackColor = Ui.Input, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(SecW - Ui.S(28), Ui.S(80)), ScrollBars = ScrollBars.Vertical, Margin = new Padding(0, 0, 0, Ui.S(8))
        };
        tb.TextChanged += (_, _) => { PushUndoSoft(); t.Text = tb.Text.Replace("\r\n", "\n"); Dirty(); };
        sec.Add(tb);
        sec.Add(Num(Lang.T("開始"), t.Start, 0, 36000, 2, 0.1, v => t.Start = v, () => t.Start, Lang.T("秒")));
        sec.Add(Num(Lang.T("長度"), t.Dur, 0.2, 36000, 2, 0.5, v => t.Dur = v, () => t.Dur, Lang.T("秒")));
        add.Add(sec);

        var style = Section(Lang.T("樣式"), "textstyle");
        style.Add(Num(Lang.T("字體大小"), t.Size, 12, 300, 0, 4, v => t.Size = (int)v, () => t.Size));
        style.Add(ColorRow(Lang.T("顏色"), t.Color, v => t.Color = v));
        style.Add(Check(Lang.T("半透明黑底"), t.Box, v => t.Box = v));
        style.Add(Check(Lang.T("淡入淡出"), t.Fade, v => t.Fade = v));
        add.Add(style);

        var pos = Section(Lang.T("位置"), "textpos");
        pos.Add(Small(Lang.T("喺預覽畫面拖中間搬位，拖四角（或者滾輪）改大細。")));
        PosRows(pos, () => EdGraph.TextPos(t), (x, y) => { t.X = x; t.Y = y; });
        add.Add(pos);
    }

    private void OverlayProps(EdOverlay o, List<PropSection> add)
    {
        var sec = Section(o.Name, "overlay");
        sec.Add(Small(o.IsVideo ? Lang.T("畫中畫：疊喺主影片上面嘅影片。喺預覽畫面拖中間搬位，拖四角（或者滾輪）改大細。")
                                : Lang.T("疊喺主影片上面嘅圖片。喺預覽畫面拖中間搬位，拖四角（或者滾輪）改大細。")));
        sec.Add(Num(Lang.T("開始"), o.Start, 0, 36000, 2, 0.1, v => o.Start = v, () => o.Start, Lang.T("秒")));
        if (o.IsVideo)
            sec.Add(Num(Lang.T("由原片"), o.In, 0, Math.Max(0.1, o.SrcDur), 2, 0.1, v => o.In = Math.Min(v, Math.Max(0, o.SrcDur - 0.2)), () => o.In, Lang.T("秒")));
        sec.Add(Num(Lang.T("長度"), o.Dur, 0.2, o.IsVideo && o.SrcDur > 0 ? Math.Max(0.2, o.SrcDur) : 36000, 2, 0.5,
            v => o.Dur = o.IsVideo && o.SrcDur > 0 ? Math.Min(v, Math.Max(0.2, o.SrcDur - o.In)) : v, () => o.Dur, Lang.T("秒")));
        add.Add(sec);
        if (o.IsVideo && o.HasAudio)
        {
            var snd = Section(Lang.T("聲音"), "ovsound");
            snd.Add(Num(Lang.T("音量"), o.Volume * 100, 0, 200, 0, 10, v => o.Volume = v / 100, null, "%"));
            snd.Add(Check(Lang.T("靜音"), o.Mute, v => o.Mute = v));
            add.Add(snd);
        }
        var look = Section(Lang.T("位置及大細"), "overlaypos");
        look.Add(Num(Lang.T("闊度"), o.Scale * 100, 2, 200, 0, 5, v => o.Scale = v / 100, () => o.Scale * 100, "%"));
        look.Add(Num(Lang.T("不透明度"), o.Opacity * 100, 5, 100, 0, 5, v => o.Opacity = v / 100, null, "%"));
        look.Add(Check(Lang.T("淡入淡出"), o.Fade, v => o.Fade = v));
        PosRows(look, () => (o.X, o.Y), (x, y) => { o.X = x; o.Y = y; });
        add.Add(look);
    }

    private void MusicProps(EdMusic m, List<PropSection> add)
    {
        var sec = Section(m.Name, "music");
        sec.Add(Small(Lang.F("原檔長度 {0}", EdTimeline.Time(m.SrcDur))));
        sec.Add(Num(Lang.T("喺時間軸"), m.Start, 0, 36000, 2, 0.5, v => m.Start = v, () => m.Start, Lang.T("秒")));
        sec.Add(Num(Lang.T("由原檔"), m.In, 0, Math.Max(0.1, m.SrcDur), 2, 0.5, v => m.In = Math.Min(v, m.Out - 0.2), () => m.In, Lang.T("秒")));
        sec.Add(Num(Lang.T("到原檔"), m.Out, 0.2, Math.Max(0.3, m.SrcDur > 0 ? m.SrcDur : 1e6), 2, 0.5, v => m.Out = Math.Max(v, m.In + 0.2), () => m.Out, Lang.T("秒")));
        if (Math.Abs(m.Sp - 1) > 0.001 || m.Speed != 1)
            sec.Add(Num(Lang.T("速度"), m.Speed, 0.25, 4, 2, 0.25, v => m.Speed = v, null, "x"));
        var al = TUi.Btn(Lang.T("對齊播放頭"), () => { PushUndo(); m.Start = _tl.Playhead; BuildPropsValuesOnly(); Dirty(); });
        al.Margin = new Padding(0, 0, 0, Ui.S(4));
        sec.Add(al);
        add.Add(sec);
        var snd = Section(Lang.T("聲音"), "musicvol");
        snd.Add(Num(Lang.T("音量"), m.Volume * 100, 0, 200, 0, 10, v => m.Volume = v / 100, null, "%"));
        snd.Add(Num(Lang.T("淡入"), m.FadeIn, 0, 10, 1, 0.5, v => m.FadeIn = v, null, Lang.T("秒")));
        snd.Add(Num(Lang.T("淡出"), m.FadeOut, 0, 10, 1, 0.5, v => m.FadeOut = v, null, Lang.T("秒")));
        add.Add(snd);
    }

    // ---------- 匯出 ----------
    private void Export()
    {
        CommitName();
        StopPlay();
        if (_p.Duration <= 0.05) { Hint(Lang.T("未有內容可以匯出")); return; }
        if (!Ffmpeg.Ready) { MessageBox.Show(FindForm(), Lang.T("未下載 ffmpeg：請去「工具庫」重新安裝影片剪輯。"), Program.AppName); return; }
        EdStore.Save(_p);
        using var f = new EdExportForm(_p.Clone());
        f.ShowDialog(FindForm());
    }
}

/// <summary>右邊設定嘅一格：圓角外框、標題可以撳嚟摺埋 / 打開（記住每種嘅狀態）。</summary>
internal sealed class PropSection : Panel
{
    private static readonly Dictionary<string, bool> OpenState = new();
    private readonly string _title, _key;
    private readonly FlowLayoutPanel _body = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.Card };
    private bool _open, _hover;
    public event Action? Toggled;
    private int HeadH => Ui.S(38);
    private int Pad => Ui.S(14);

    public PropSection(string title, string key, bool defOpen, int width)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _title = title;
        _key = key;
        _open = OpenState.TryGetValue(key, out var o) ? o : defOpen;
        BackColor = Ui.Bg;
        Width = width;
        _body.Location = new Point(Pad, HeadH);
        Controls.Add(_body);
    }

    public void Add(Control c) => _body.Controls.Add(c);

    public void Relayout()
    {
        _body.Visible = _open;
        _body.PerformLayout();
        Height = _open ? HeadH + _body.PreferredSize.Height + Ui.S(8) : HeadH;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool h = e.Y < HeadH;
        Cursor = h ? Cursors.Hand : Cursors.Default;
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover) { _hover = false; Invalidate(); } }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Y >= HeadH) return;
        _open = !_open;
        OpenState[_key] = _open;
        Relayout();
        Toggled?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Bg);
        Ui.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Ui.Round(r, Ui.S(10f)))
        {
            using var b = new SolidBrush(Ui.Card);
            g.FillPath(b, path);
            using var pen = new Pen(_hover ? Ui.BorderHi : Ui.Border, 1f);
            g.DrawPath(pen, path);
        }
        var head = new Rectangle(Pad, 0, Width - Pad * 2 - Ui.S(20), HeadH);
        TextRenderer.DrawText(g, _title, Ui.F(10.25f, FontStyle.Bold), head, Ui.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, _open ? "▾" : "▸", Ui.F(10f), new Rectangle(Width - Pad - Ui.S(16), 0, Ui.S(16), HeadH), Ui.TextDim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        if (_open)
            using (var pen = new Pen(Ui.Border, 1f))
                g.DrawLine(pen, Pad, HeadH - Ui.S(6), Width - Pad, HeadH - Ui.S(6));
    }
}

/// <summary>匯出細窗：揀畫質 → 揀位置 → 進度。</summary>
internal sealed class EdExportForm : Form
{
    private readonly EdProject _p;
    private readonly ThinBar _bar = new();
    private readonly Label _status;
    private readonly FancyButton _go, _cancel;
    private Process? _proc;
    private bool _running;
    private int _quality = 1;
    private bool _gpu = true;
    private string _fmt = "mp4";
    private CheckBox _gpuBox = null!;

    /// <summary>匯出格式：(副檔名, 顯示名, 淨係聲音, 可以用顯示卡)。</summary>
    private static readonly (string Ext, string Name, bool Audio, bool Gpu)[] Formats =
    {
        ("mp4", "MP4 (H.264)", false, true),
        ("mov", "MOV (H.264)", false, true),
        ("mkv", "MKV (H.264)", false, true),
        ("avi", "AVI (H.264)", false, true),
        ("webm", "WebM (VP9)", false, false),
        ("wmv", "WMV", false, false),
        ("mp3", "MP3", true, false),
        ("wav", "WAV", true, false),
        ("m4a", "M4A (AAC)", true, false),
        ("flac", "FLAC", true, false),
    };
    private (string Ext, string Name, bool Audio, bool Gpu) Fmt => Formats.FirstOrDefault(f => f.Ext == _fmt) is { Ext: not null } f ? f : Formats[0];
    private static string FmtFile => Path.Combine(EdStore.Dir, "format.txt");

    public EdExportForm(EdProject p)
    {
        _p = p;
        Text = Lang.T("匯出影片");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.F(9.75f);
        int x = Ui.S(20), y = Ui.S(18);
        var info = Ui.Text_(Lang.F("{0} × {1} · {2} fps · 長度 {3}", p.W, p.H, p.Fps, EdTimeline.Time(p.Duration)), Ui.Text, Ui.F(10f, FontStyle.Bold), new Padding(0));
        info.Location = new Point(x, y);
        Controls.Add(info);
        y += Ui.S(36);
        try { var f = File.Exists(FmtFile) ? File.ReadAllText(FmtFile).Trim() : ""; if (Formats.Any(x => x.Ext == f)) _fmt = f; } catch { }
        // 格式 + 質素
        FancyButton? fb = null;
        fb = new FancyButton { Text = Lang.T("格式：") + Fmt.Name + "  ▾", Kind = BtnKind.Ghost, Location = new Point(x, y), Size = new Size(Ui.S(222), Ui.S(32)) };
        var items = Formats.Select(f => (f.Audio ? f.Name + "  " + Lang.T("（淨係聲音）") : f.Name, f.Ext));
        fb.Click += (_, _) => TUi.Pick(fb, items, _fmt, v =>
        {
            _fmt = v;
            fb.Text = Lang.T("格式：") + Fmt.Name + "  ▾";
            try { Directory.CreateDirectory(EdStore.Dir); File.WriteAllText(FmtFile, v); } catch { }
            UpdateFmt();
        });
        Controls.Add(fb);
        string[] qs = { Lang.T("高（檔案大）"), Lang.T("標準"), Lang.T("細檔（質素較低）") };
        FancyButton? qb = null;
        qb = new FancyButton { Text = Lang.T("質素：") + qs[_quality] + "  ▾", Kind = BtnKind.Ghost, Location = new Point(x + Ui.S(238), y), Size = new Size(Ui.S(222), Ui.S(32)) };
        qb.Click += (_, _) => TUi.Pick(qb, qs.Select((n, i) => (n, i)), _quality, v => { _quality = v; qb.Text = Lang.T("質素：") + qs[v] + "  ▾"; });
        Controls.Add(qb);
        y += Ui.S(42);
        _gpuBox = new CheckBox { Text = Lang.T("用顯示卡加速（冇就自動用 CPU）"), Checked = true, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, Location = new Point(x, y) };
        _gpuBox.CheckedChanged += (_, _) => _gpu = _gpuBox.Checked;
        Controls.Add(_gpuBox);
        y += Ui.S(38);
        _bar.SetBounds(x, y, Ui.S(460), Ui.S(8));
        _bar.Value = -1;
        Controls.Add(_bar);
        y += Ui.S(18);
        _status = Ui.Text_(Lang.T("撳「匯出」揀儲存位置。"), Ui.TextDim, Ui.F(9.25f), new Padding(0));
        _status.Location = new Point(x, y);
        _status.MaximumSize = new Size(Ui.S(460), 0);
        Controls.Add(_status);
        y += Ui.S(46);
        _go = new FancyButton { Text = Lang.T("匯出"), Kind = BtnKind.Success, Size = new Size(Ui.S(100), Ui.S(34)) };
        _cancel = new FancyButton { Text = Lang.T("閂"), Kind = BtnKind.Ghost, Size = new Size(Ui.S(100), Ui.S(34)) };
        _cancel.Location = new Point(x + Ui.S(460) - _cancel.Width, y);
        _go.Location = new Point(_cancel.Left - Ui.S(10) - _go.Width, y);
        _go.Click += async (_, _) => await GoAsync();
        _cancel.Click += (_, _) => { if (_running) Cancel(); else Close(); };
        Controls.Add(_go);
        Controls.Add(_cancel);
        ClientSize = new Size(Ui.S(500), y + Ui.S(52));
        UpdateFmt();
        FormClosing += (_, e) =>
        {
            if (!_running) return;
            if (MessageBox.Show(this, Lang.T("匯出緊，要取消？"), Program.AppName, MessageBoxButtons.YesNo) != DialogResult.Yes) { e.Cancel = true; return; }
            Cancel();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { DarkTheme.DarkTitleBar(this); } catch { }
    }

    /// <summary>淨係聲音 / WebM / WMV 唔用顯示卡：剔格變灰。</summary>
    private void UpdateFmt()
    {
        _gpuBox.Enabled = Fmt.Gpu;
        _gpuBox.ForeColor = Fmt.Gpu ? Ui.Text : Ui.TextFaint;
    }

    // 記住上次匯出嘅資料夾（data\editor\export.txt），下次預設喺同一個位置
    private static string DirFile => Path.Combine(EdStore.Dir, "export.txt");

    private static string LastDir()
    {
        try
        {
            if (File.Exists(DirFile))
            {
                var d = File.ReadAllText(DirFile).Trim();
                if (d.Length > 0 && Directory.Exists(d)) return d;
            }
        }
        catch { }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }

    private static void SaveDir(string dir)
    {
        try
        {
            if (dir.Length == 0) return;
            Directory.CreateDirectory(EdStore.Dir);
            File.WriteAllText(DirFile, dir);
        }
        catch { }
    }

    private void Cancel()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { }
    }

    private async Task GoAsync()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = $"{Fmt.Name} (*.{Fmt.Ext})|*.{Fmt.Ext}",
            FileName = Scripts.Script.Sanitize(_p.Name) + "." + Fmt.Ext,
            InitialDirectory = LastDir()
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string outFile = dlg.FileName;
        SaveDir(Path.GetDirectoryName(outFile) ?? "");
        _running = true;
        _go.Enabled = false;
        _cancel.Text = Lang.T("取消");
        _bar.Value = 0;
        _status.Text = Lang.T("準備緊…");
        string err = "";
        int code = -1;
        try
        {
            var fm = Fmt;
            string enc = "libx264", vopt, aopt;
            int crf = new[] { 18, 22, 27 }[_quality];
            if (fm.Audio)
            {
                enc = fm.Ext;
                aopt = fm.Ext switch
                {
                    "mp3" => $"-c:a libmp3lame -q:a {new[] { 0, 2, 5 }[_quality]}",
                    "wav" => "-c:a pcm_s16le",
                    "flac" => "-c:a flac",
                    _ => $"-c:a aac -b:a {new[] { 256, 192, 128 }[_quality]}k"
                };
                vopt = "";
            }
            else if (fm.Ext == "webm")
            {
                enc = "libvpx-vp9";
                vopt = $"-c:v libvpx-vp9 -crf {crf + 12} -b:v 0 -row-mt 1 -deadline good -cpu-used 4 -pix_fmt yuv420p -r {_p.Fps}";
                aopt = "-c:a libopus -b:a 160k";
            }
            else if (fm.Ext == "wmv")
            {
                enc = "wmv2";
                vopt = $"-c:v wmv2 -q:v {new[] { 2, 4, 7 }[_quality]} -r {_p.Fps}";
                aopt = "-c:a wmav2 -b:a 192k";
            }
            else
            {
                if (_gpu) { try { enc = await Task.Run(() => Ffmpeg.Encoder("h264", true)); } catch { enc = "libx264"; } }
                string q = enc switch
                {
                    var e when e.EndsWith("_nvenc") => $"-preset p5 -rc vbr -cq {crf + 1} -b:v 0",
                    var e when e.EndsWith("_qsv") => $"-global_quality {crf + 1}",
                    var e when e.EndsWith("_amf") => $"-rc cqp -qp_i {crf} -qp_p {crf + 2}",
                    _ => $"-preset medium -crf {crf}"
                };
                vopt = $"-c:v {enc} {q} -pix_fmt yuv420p -r {_p.Fps}";
                aopt = fm.Ext == "avi" ? "-c:a libmp3lame -b:a 192k" : "-c:a aac -b:a 192k";
                if (fm.Ext is "mp4" or "mov") aopt += " -movflags +faststart";
            }
            string args = EdGraph.Args(_p, 0, _p.W, _p.H, !fm.Audio, true, "export", out double total) +
                          (fm.Audio ? "-map [aout] -vn " : "-map [vout] -map [aout] ") + $"{vopt} {aopt} -progress pipe:1 -nostats -y \"{outFile}\"";
            var psi = new ProcessStartInfo(Ffmpeg.Exe, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            _proc = Process.Start(psi)!;
            var errTask = _proc.StandardError.ReadToEndAsync();
            _status.Text = Lang.F("匯出緊…（{0}）", Ffmpeg.EncoderName(enc));
            while (await _proc.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!line.StartsWith("out_time_us=") && !line.StartsWith("out_time_ms=")) continue;
                if (!long.TryParse(line[(line.IndexOf('=') + 1)..], out var us) || us < 0) continue;
                double sec = us / 1_000_000.0;
                _bar.Value = Math.Clamp(sec / Math.Max(0.1, total), 0, 1);
                _status.Text = Lang.F("匯出緊…{0:P0}（{1}）", _bar.Value, Ffmpeg.EncoderName(enc));
            }
            await _proc.WaitForExitAsync();
            code = _proc.ExitCode;
            err = (await errTask).Trim();
        }
        catch (Exception ex) { err = ex.Message; }
        finally
        {
            _running = false;
            _proc?.Dispose();
            _proc = null;
            _go.Enabled = true;
            _cancel.Text = Lang.T("閂");
        }
        if (code == 0 && File.Exists(outFile))
        {
            // 完成：即刻喺檔案總管揀住個檔，閂埋匯出細窗
            _bar.Value = 1;
            TUi.Reveal(outFile);
            Tools.Toast.Show(Lang.T("匯出完成"), Ui.Success, 3000);
            Close();
        }
        else
        {
            if (err.Length > 0) AppPaths.LogError("剪片匯出", new Exception(err));
            _bar.Value = -1;
            _status.Text = Lang.T("匯出失敗：") + (err.Length > 0 ? err.Split('\n').Last().Trim() : Lang.T("已取消"));
        }
    }
}

/// <summary>預覽音量：左邊喇叭撳一下 = 靜音；右邊條拖 / 滾輪 = 音量。淨係影響預覽，唔改影片。</summary>
internal sealed class EdVolume : Control
{
    private bool _drag;
    public event Action? Changed;

    public EdVolume()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Bg;
        Size = new Size(Ui.S(150), Ui.S(30));
        Cursor = Cursors.Hand;
    }

    private int IconW => Ui.S(30);
    private Rectangle Bar => new(IconW + Ui.S(4), Height / 2 - Ui.S(2), Width - IconW - Ui.S(46), Ui.S(4));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        float v = WaveOut.Muted ? 0 : WaveOut.Volume;
        string glyph = EditorPage.HasIcons
            ? (v <= 0.001f ? "" : v < 0.34f ? "" : v < 0.67f ? "" : "")
            : (v <= 0.001f ? "🔇" : "🔊");
        using var iconFont = EditorPage.IconFont(11f);
        TextRenderer.DrawText(g, glyph, iconFont, new Rectangle(0, 0, IconW, Height), Ui.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        var bar = Bar;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Ui.Border)) g.FillRectangle(bg, bar);
        int fill = (int)(bar.Width * v);
        using (var fb = new SolidBrush(WaveOut.Muted ? Ui.TextDim : Ui.Accent)) g.FillRectangle(fb, bar.X, bar.Y, fill, bar.Height);
        int r = Ui.S(6);
        using (var kb = new SolidBrush(Ui.Text)) g.FillEllipse(kb, bar.X + fill - r, bar.Y + bar.Height / 2 - r, r * 2, r * 2);
        TextRenderer.DrawText(g, $"{(int)Math.Round(v * 100)}%", Ui.F(8.75f), new Rectangle(bar.Right + Ui.S(6), 0, Width - bar.Right - Ui.S(6), Height), Ui.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void SetFrom(int x)
    {
        var bar = Bar;
        WaveOut.Volume = (float)Math.Clamp((x - bar.X) / (double)Math.Max(1, bar.Width), 0, 1);
        WaveOut.Muted = false;
        Invalidate();
        Changed?.Invoke();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (e.X < IconW) { WaveOut.Muted = !WaveOut.Muted; Invalidate(); Changed?.Invoke(); return; }
        _drag = true;
        Capture = true;
        SetFrom(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag) SetFrom(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_drag) return;
        _drag = false;
        Capture = false;
        WaveOut.SaveVolume();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        WaveOut.Volume = Math.Clamp(WaveOut.Volume + (e.Delta > 0 ? 0.05f : -0.05f), 0, 1);
        WaveOut.Muted = false;
        Invalidate();
        WaveOut.SaveVolume();
        Changed?.Invoke();
    }
}
