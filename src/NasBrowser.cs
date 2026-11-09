using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Tools;

/// <summary>
/// 好似 Google Drive 咁嘅檔案瀏覽：清單（有欄位標題，撳標題排序）或者格仔（資料夾 + 檔案卡，相片有縮圖）。
/// 支援 Ctrl / Shift 多選、方向鍵、Enter、Ctrl+A、滑鼠滾輪。
/// </summary>
internal sealed class NasBrowser : Control
{
    public enum SortKey { Name, Modified, Size }

    private List<NasEntry> _all = new(), _view = new();
    private readonly List<Rectangle> _rects = new();     // 每個項目喺內容座標嘅位置
    private readonly List<(string Text, int Y)> _sections = new();
    private readonly HashSet<NasEntry> _sel = new();
    private int _focus = -1, _anchor = -1, _hover = -1, _scroll, _contentH, _cols = 1;
    private bool _grid, _loading, _vsOn;
    private string _filter = "";
    private SortKey _sort = SortKey.Name;
    private bool _asc = true;
    private bool _drag, _sbHot; private int _dragOff; // 拉緊捲軸
    private readonly Dictionary<string, Image?> _thumbs = new();

    public event Action<NasEntry>? Open;
    public event Action? SelectionChanged;
    /// <summary>攞縮圖（由頁面提供，用 NAS API）。</summary>
    public Func<NasEntry, Task<Image?>>? ThumbLoader;

    private int HeadH => Ui.S(34);
    private int RowH => Ui.S(40);
    private int SbW => Ui.S(12);
    private int MaxScroll => Math.Max(0, _contentH - ClientSize.Height);

    public NasBrowser()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Ui.Input;
        ForeColor = Ui.Text;
        Font = Ui.F(9.75f);
        TabStop = true;
        Disposed += (_, _) => ClearThumbs();
    }

    // ---------------- 公開 ----------------

    public bool Loading { get => _loading; set { _loading = value; Invalidate(); } }
    public bool Grid { get => _grid; set { _grid = value; _scroll = 0; Relayout(); } }
    public string Filter { get => _filter; set { _filter = value ?? ""; Rebuild(); } }
    public List<NasEntry> Selected => _view.Where(_sel.Contains).ToList();

    public void SetItems(IEnumerable<NasEntry> items)
    {
        _all = items.ToList();
        _sel.Clear();
        _focus = _anchor = _hover = -1;
        _scroll = 0;
        ClearThumbs();
        Rebuild();
        SelectionChanged?.Invoke();
    }

    public NasEntry? ItemAt(Point p)
    {
        int i = HitTest(p);
        return i >= 0 ? _view[i] : null;
    }

    /// <summary>右鍵：未揀中就淨係揀佢一個。</summary>
    public void SelectForMenu(Point p)
    {
        int i = HitTest(p);
        if (i < 0) { _sel.Clear(); }
        else if (!_sel.Contains(_view[i])) { _sel.Clear(); _sel.Add(_view[i]); _focus = _anchor = i; }
        Invalidate();
        SelectionChanged?.Invoke();
    }

    // ---------------- 排版 ----------------

    private void ClearThumbs()
    {
        foreach (var im in _thumbs.Values) im?.Dispose();
        _thumbs.Clear();
    }

    private void Rebuild()
    {
        IEnumerable<NasEntry> q = _all;
        if (_filter.Length > 0) q = q.Where(e => e.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase));
        Func<NasEntry, IComparable> key = _sort switch
        {
            SortKey.Modified => e => e.Modified,
            SortKey.Size => e => e.IsDir ? -1L : e.Size,
            _ => e => e.Name.ToLowerInvariant()
        };
        var dirs = q.Where(e => e.IsDir);
        var files = q.Where(e => !e.IsDir);
        dirs = _asc ? dirs.OrderBy(key) : dirs.OrderByDescending(key);
        files = _asc ? files.OrderBy(key) : files.OrderByDescending(key);
        _view = dirs.Concat(files).ToList();
        _sel.RemoveWhere(e => !_view.Contains(e));
        if (_focus >= _view.Count) _focus = _view.Count - 1;
        Relayout();
    }

    private int ViewW => Math.Max(Ui.S(100), ClientSize.Width - (_vsOn ? SbW : 0));

    private void Relayout()
    {
        _rects.Clear();
        _sections.Clear();
        int w = ViewW, pad = Ui.S(12), gap = Ui.S(12);
        if (!_grid)
        {
            int y = HeadH;
            foreach (var _ in _view) { _rects.Add(new Rectangle(0, y, w, RowH)); y += RowH; }
            _contentH = y;
            _cols = 1;
        }
        else
        {
            int y = pad;
            int nd = _view.Count(e => e.IsDir);
            // 資料夾：一粒粒長條
            if (nd > 0)
            {
                _sections.Add((Lang.T("資料夾"), y));
                y += Ui.S(30);
                int cols = Math.Max(1, (w - pad * 2 + gap) / (Ui.S(210) + gap));
                int tw = (w - pad * 2 - gap * (cols - 1)) / cols, th = Ui.S(48);
                for (int i = 0; i < nd; i++)
                {
                    int c = i % cols, r = i / cols;
                    _rects.Add(new Rectangle(pad + c * (tw + gap), y + r * (th + gap), tw, th));
                }
                y += ((nd + cols - 1) / cols) * (th + gap) + Ui.S(6);
                _cols = cols;
            }
            int nf = _view.Count - nd;
            if (nf > 0)
            {
                _sections.Add((Lang.T("檔案"), y));
                y += Ui.S(30);
                int cols = Math.Max(1, (w - pad * 2 + gap) / (Ui.S(170) + gap));
                int tw = (w - pad * 2 - gap * (cols - 1)) / cols, th = tw * 9 / 10 + Ui.S(44);
                for (int i = 0; i < nf; i++)
                {
                    int c = i % cols, r = i / cols;
                    _rects.Add(new Rectangle(pad + c * (tw + gap), y + r * (th + gap), tw, th));
                }
                y += ((nf + cols - 1) / cols) * (th + gap);
                if (nd == 0) _cols = cols;
            }
            _contentH = y + pad;
        }
        int page = ClientSize.Height;
        bool need = _contentH > page;
        if (_vsOn != need) { _vsOn = need; Relayout(); return; }
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        Invalidate();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated) Relayout(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }

    private int HitTest(Point p)
    {
        if (!_grid && p.Y < HeadH) return -1;
        var c = new Point(p.X, p.Y + _scroll);
        for (int i = 0; i < _rects.Count; i++) if (_rects[i].Contains(c)) return i;
        return -1;
    }

    private void EnsureVisible(int i)
    {
        if (i < 0 || i >= _rects.Count) return;
        var r = _rects[i];
        int top = _grid ? 0 : HeadH;
        if (r.Y - top < _scroll) _scroll = r.Y - top - (_grid ? Ui.S(36) : 0);
        else if (r.Bottom > _scroll + ClientSize.Height) _scroll = r.Bottom - ClientSize.Height + Ui.S(8);
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        Invalidate();
    }

    // ---------------- 滑鼠 / 鍵盤 ----------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_vsOn && e.X >= ViewW && e.Button == MouseButtons.Left)
        {
            var t = ThumbRect();
            if (t.Contains(e.Location)) { _drag = true; _dragOff = e.Y - t.Y; Capture = true; }
            else { _scroll = Math.Clamp(_scroll + (e.Y < t.Y ? -1 : 1) * ClientSize.Height * 9 / 10, 0, MaxScroll); Invalidate(); }
            return;
        }
        if (!_grid && e.Y < HeadH && e.Button == MouseButtons.Left) { HeaderClick(e.X); return; }
        if (e.Button != MouseButtons.Left) return;
        int i = HitTest(e.Location);
        if (i < 0) { if (ModifierKeys == Keys.None) { _sel.Clear(); Invalidate(); SelectionChanged?.Invoke(); } return; }
        Click(i);
    }

    private void Click(int i)
    {
        var e = _view[i];
        if ((ModifierKeys & Keys.Shift) != 0 && _anchor >= 0)
        {
            if ((ModifierKeys & Keys.Control) == 0) _sel.Clear();
            for (int k = Math.Min(_anchor, i); k <= Math.Max(_anchor, i); k++) _sel.Add(_view[k]);
        }
        else if ((ModifierKeys & Keys.Control) != 0)
        {
            if (!_sel.Remove(e)) _sel.Add(e);
            _anchor = i;
        }
        else { _sel.Clear(); _sel.Add(e); _anchor = i; }
        _focus = i;
        EnsureVisible(i);
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        int i = HitTest(e.Location);
        if (i >= 0) Open?.Invoke(_view[i]);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag) { _drag = false; Capture = false; Invalidate(); }
    }

    // 捲軸位置（自己畫，跟深 / 淺色主題）
    private Rectangle ThumbRect()
    {
        int top = _grid ? 0 : HeadH, track = ClientSize.Height - top;
        if (track <= 0 || _contentH <= 0) return Rectangle.Empty;
        int th = Math.Max(Ui.S(32), track * ClientSize.Height / Math.Max(1, _contentH));
        int ty = top + (MaxScroll == 0 ? 0 : (int)((long)(track - th) * _scroll / MaxScroll));
        return new Rectangle(ViewW, ty, SbW, th);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag)
        {
            int top = _grid ? 0 : HeadH, track = ClientSize.Height - top, th = ThumbRect().Height;
            if (track > th) _scroll = Math.Clamp((int)((long)(e.Y - _dragOff - top) * MaxScroll / (track - th)), 0, MaxScroll);
            Invalidate();
            return;
        }
        bool hs = _vsOn && e.X >= ViewW;
        if (hs != _sbHot) { _sbHot = hs; Invalidate(); }
        int i = HitTest(e.Location);
        bool head = !_grid && e.Y < HeadH;
        Cursor = head ? Cursors.Hand : Cursors.Default;
        if (i != _hover) { _hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _sbHot = false; _hover = -1; Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_vsOn) return; // 清單短過個框就由外面嘅頁面捲
        // 標記處理咗，唔好再傳去外面令成頁一齊捲
        if (e is HandledMouseEventArgs h) h.Handled = true;
        _scroll = Math.Clamp(_scroll - e.Delta * RowH * 3 / 120, 0, MaxScroll);
        Invalidate();
    }

    protected override bool IsInputKey(Keys k) =>
        (k & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown || base.IsInputKey(k);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _view.Count == 0) return;
        if (e.KeyCode == Keys.A && e.Control) { foreach (var x in _view) _sel.Add(x); Invalidate(); SelectionChanged?.Invoke(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Enter && _focus >= 0) { Open?.Invoke(_view[_focus]); e.Handled = true; return; }
        int step = e.KeyCode switch
        {
            Keys.Down => _grid ? _cols : 1,
            Keys.Up => _grid ? -_cols : -1,
            Keys.Right => _grid ? 1 : 0,
            Keys.Left => _grid ? -1 : 0,
            Keys.PageDown => Math.Max(1, ClientSize.Height / RowH),
            Keys.PageUp => -Math.Max(1, ClientSize.Height / RowH),
            Keys.Home => -_view.Count,
            Keys.End => _view.Count,
            _ => 0
        };
        if (step == 0) return;
        int n = Math.Clamp((_focus < 0 ? (step > 0 ? -1 : _view.Count) : _focus) + step, 0, _view.Count - 1);
        Click(n);
        e.Handled = true;
    }

    private void HeaderClick(int x)
    {
        var (_, dateX, sizeX) = Cols();
        var k = x >= sizeX ? SortKey.Size : x >= dateX ? SortKey.Modified : SortKey.Name;
        if (k == _sort) _asc = !_asc; else { _sort = k; _asc = k == SortKey.Name; }
        Rebuild();
    }

    // 清單模式三欄嘅 x：名、日期、大細（太窄就收埋日期 / 大細）
    private (int Name, int Date, int Size) Cols()
    {
        int w = ViewW;
        int sizeW = w < Ui.S(420) ? 0 : Ui.S(110), dateW = w < Ui.S(520) ? 0 : Ui.S(170);
        return (Ui.S(14), w - sizeW - dateW, w - sizeW);
    }

    // ---------------- 畫 ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Input);
        if (_view.Count == 0)
        {
            string t = _loading ? Lang.T("載入緊…") : _filter.Length > 0 ? Lang.T("搵唔到相符嘅檔案") : Lang.T("（空）");
            if (!_grid) DrawHeader(g);
            TextRenderer.DrawText(g, t, Font, ClientRectangle, Ui.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        if (_grid) PaintGrid(g); else PaintList(g);
        if (_vsOn)
        {
            var t = ThumbRect();
            t.Inflate(-Ui.S(3), -Ui.S(2));
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool hot = _drag || _sbHot;
            using (var p = Ui.Round(t, t.Width / 2f))
            using (var b = new SolidBrush(Ui.Alpha(Ui.TextFaint, hot ? 200 : 120))) g.FillPath(b, p);
            g.SmoothingMode = sm;
        }
    }

    private const TextFormatFlags TF = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private void DrawHeader(Graphics g)
    {
        int w = ViewW;
        using (var b = new SolidBrush(Ui.Card)) g.FillRectangle(b, 0, 0, w, HeadH);
        using (var p = new Pen(Ui.Border)) g.DrawLine(p, 0, HeadH - 1, w, HeadH - 1);
        var (nx, dx, sx) = Cols();
        string Arrow(SortKey k) => _sort == k ? (_asc ? "  ↑" : "  ↓") : "";
        var f = Ui.F(9f, FontStyle.Bold);
        TextRenderer.DrawText(g, Lang.T("名稱") + Arrow(SortKey.Name), f, new Rectangle(nx + Ui.S(34), 0, dx - nx - Ui.S(34), HeadH), Ui.TextDim, TF);
        if (sx > dx) TextRenderer.DrawText(g, Lang.T("修改日期") + Arrow(SortKey.Modified), f, new Rectangle(dx, 0, sx - dx, HeadH), Ui.TextDim, TF);
        if (w > sx) TextRenderer.DrawText(g, Lang.T("大小") + Arrow(SortKey.Size), f, new Rectangle(sx, 0, w - sx - Ui.S(14), HeadH), Ui.TextDim, TF | TextFormatFlags.Right);
    }

    private void PaintList(Graphics g)
    {
        var (nx, dx, sx) = Cols();
        int w = ViewW;
        g.SetClip(new Rectangle(0, HeadH, w, ClientSize.Height - HeadH));
        using var line = new Pen(Ui.Alpha(Ui.Border, 120));
        for (int i = 0; i < _view.Count; i++)
        {
            var r = _rects[i]; r.Y -= _scroll;
            if (r.Bottom < HeadH || r.Y > ClientSize.Height) continue;
            var n = _view[i];
            bool sel = _sel.Contains(n);
            if (sel || i == _hover)
                using (var b = new SolidBrush(sel ? Ui.Alpha(Ui.Accent, 60) : Ui.Alpha(Ui.CardHi, 200))) g.FillRectangle(b, r);
            g.DrawLine(line, r.X, r.Bottom - 1, r.Right, r.Bottom - 1);
            var (ic, col) = Kind(n);
            DrawBadge(g, new Rectangle(nx, r.Y + (r.Height - Ui.S(24)) / 2, Ui.S(24), Ui.S(24)), ic, col);
            TextRenderer.DrawText(g, n.Name, Font, new Rectangle(nx + Ui.S(34), r.Y, dx - nx - Ui.S(44), r.Height), Ui.Text, TF);
            if (sx > dx && n.Modified > DateTime.MinValue)
                TextRenderer.DrawText(g, n.Modified.ToString("yyyy-MM-dd HH:mm"), Font, new Rectangle(dx, r.Y, sx - dx, r.Height), Ui.TextDim, TF);
            if (w > sx)
                TextRenderer.DrawText(g, n.IsDir ? "—" : NasPage.Fmt(n.Size), Font, new Rectangle(sx, r.Y, w - sx - Ui.S(14), r.Height), Ui.TextDim, TF | TextFormatFlags.Right);
        }
        g.ResetClip();
        DrawHeader(g);
    }

    private void PaintGrid(Graphics g)
    {
        Ui.Smooth(g);
        var hf = Ui.F(9.5f, FontStyle.Bold);
        foreach (var (t, y) in _sections)
            TextRenderer.DrawText(g, t, hf, new Rectangle(Ui.S(14), y - _scroll, ViewW, Ui.S(24)), Ui.TextDim, TF);
        for (int i = 0; i < _view.Count; i++)
        {
            var r = _rects[i]; r.Y -= _scroll;
            if (r.Bottom < 0 || r.Y > ClientSize.Height) continue;
            var n = _view[i];
            bool sel = _sel.Contains(n), hov = i == _hover;
            var fill = sel ? Ui.Mix(Ui.Card, Ui.Accent, 0.28f) : hov ? Ui.CardHi : Ui.Card;
            using (var path = Ui.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Ui.S(10f)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using var p = new Pen(sel ? Ui.Accent : Ui.Border, sel ? 1.5f : 1f);
                g.DrawPath(p, path);
            }
            var (ic, col) = Kind(n);
            if (n.IsDir)
            {
                DrawBadge(g, new Rectangle(r.X + Ui.S(12), r.Y + (r.Height - Ui.S(26)) / 2, Ui.S(26), Ui.S(26)), ic, col);
                TextRenderer.DrawText(g, n.Name, Font, new Rectangle(r.X + Ui.S(48), r.Y, r.Width - Ui.S(58), r.Height), Ui.Text, TF);
                continue;
            }
            // 檔案卡：上面預覽、下面名
            int nameH = Ui.S(40), m = Ui.S(8);
            var pv = new Rectangle(r.X + m, r.Y + m, r.Width - m * 2, r.Height - nameH - m);
            using (var pp = Ui.Round(pv, Ui.S(6f)))
            using (var pb = new SolidBrush(Ui.Input)) g.FillPath(pb, pp);
            var img = Thumb(n);
            if (img != null)
            {
                // 置中裁切填滿
                float s = Math.Max((float)pv.Width / img.Width, (float)pv.Height / img.Height);
                float iw = img.Width * s, ih = img.Height * s;
                var st = g.Save();
                using (var clip = Ui.Round(pv, Ui.S(6f))) g.SetClip(clip);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, pv.X + (pv.Width - iw) / 2, pv.Y + (pv.Height - ih) / 2, iw, ih);
                g.Restore(st);
            }
            else
            {
                int bs = Math.Min(Ui.S(52), Math.Min(pv.Width, pv.Height) - Ui.S(8));
                DrawBadge(g, new Rectangle(pv.X + (pv.Width - bs) / 2, pv.Y + (pv.Height - bs) / 2, bs, bs), ic, col, true);
            }
            DrawBadge(g, new Rectangle(r.X + Ui.S(10), r.Bottom - nameH + (nameH - Ui.S(20)) / 2, Ui.S(20), Ui.S(20)), ic, col);
            TextRenderer.DrawText(g, n.Name, Font, new Rectangle(r.X + Ui.S(36), r.Bottom - nameH, r.Width - Ui.S(44), nameH), Ui.Text, TF);
        }
    }

    /// <summary>彩色圓角方塊 + 符號（好似 Drive 啲檔案類型圖示）。</summary>
    private void DrawBadge(Graphics g, Rectangle r, string sym, Color col, bool big = false)
    {
        var sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = Ui.Round(r, r.Width / 4.5f))
        using (var b = new SolidBrush(Ui.Alpha(col, big ? 60 : 255))) g.FillPath(b, p);
        g.SmoothingMode = sm;
        using var f = new Font("Segoe UI Symbol", Math.Max(7f, r.Height * (sym.Length > 1 ? 0.36f : 0.48f)), FontStyle.Bold, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, sym, f, r, big ? col : Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    private static readonly string[] ImgExt = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".bmp", ".tif", ".tiff" };
    private static readonly string[] VidExt = { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".ts" };

    private static (string Sym, Color Col) Kind(NasEntry n)
    {
        if (n.IsDir) return ("▬", Color.FromArgb(0x5F, 0x6F, 0x86));
        string x = Path.GetExtension(n.Name).ToLowerInvariant();
        if (ImgExt.Contains(x)) return ("▲", Color.FromArgb(0xE0, 0x4A, 0x3F));
        if (VidExt.Contains(x)) return ("▶", Color.FromArgb(0xD9, 0x30, 0x25));
        return x switch
        {
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" or ".ogg" => ("♪", Color.FromArgb(0x8E, 0x44, 0xAD)),
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => ("Z", Color.FromArgb(0x7F, 0x8C, 0x8D)),
            ".pdf" => ("P", Color.FromArgb(0xE5, 0x39, 0x35)),
            ".doc" or ".docx" or ".txt" or ".md" or ".rtf" => ("W", Color.FromArgb(0x42, 0x85, 0xF4)),
            ".xls" or ".xlsx" or ".csv" => ("X", Color.FromArgb(0x0F, 0x9D, 0x58)),
            ".ppt" or ".pptx" => ("P", Color.FromArgb(0xF4, 0xB4, 0x00)),
            ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" => ("⚙", Color.FromArgb(0x60, 0x7D, 0x8B)),
            ".iso" or ".img" => ("◉", Color.FromArgb(0x60, 0x7D, 0x8B)),
            ".cs" or ".js" or ".ts" or ".py" or ".json" or ".xml" or ".html" or ".css" => ("<>", Color.FromArgb(0x26, 0xA6, 0x9A)),
            _ => ("■", Color.FromArgb(0x78, 0x86, 0x9C))
        };
    }

    /// <summary>相片 / 影片縮圖：有就回，冇就背景攞（攞完再畫）。</summary>
    private Image? Thumb(NasEntry n)
    {
        string x = Path.GetExtension(n.Name).ToLowerInvariant();
        if (ThumbLoader == null || !(ImgExt.Contains(x) || VidExt.Contains(x))) return null;
        string key = n.Path + "|" + n.Modified.Ticks;
        if (_thumbs.TryGetValue(key, out var im)) return im;
        _thumbs[key] = null; // 攞緊
        var loader = ThumbLoader;
        _ = Task.Run(async () =>
        {
            Image? got = null;
            try { got = await loader(n); } catch { }
            if (IsDisposed || !IsHandleCreated) { got?.Dispose(); return; }
            try
            {
                BeginInvoke(() =>
                {
                    if (IsDisposed) { got?.Dispose(); return; }
                    if (_thumbs.ContainsKey(key)) _thumbs[key] = got; else got?.Dispose();
                    Invalidate();
                });
            }
            catch { got?.Dispose(); }
        });
        return null;
    }
}
