using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Tools;

/// <summary>圖片處理嘅設定（一次過套用落每一張圖）。</summary>
internal sealed class ImageJob
{
    public int ResizeMode;          // 0 = 唔縮、1 = 最長邊唔超過 N px、2 = 百分比、3 = 指定闊度
    public int ResizeValue = 1920;
    public int Format;              // 0 = 保持、1 JPG、2 PNG、3 BMP、4 GIF、5 TIFF
    public int Quality = 85;
    public bool AutoRotate = true;  // 跟相機記低嘅方向轉正
    public int Turn;                // 0 唔轉、1 順 90°、2 逆 90°、3 180°、4 左右反轉、5 上下反轉
    public bool AutoLevels;         // 自動調整光暗
    public int Brightness, Contrast, Saturation;   // -100 … 100
    public bool Sharpen;
    public string Watermark = "";
    public int WatermarkPos;        // 0 右下、1 左下、2 右上、3 左上、4 中間
    public int WatermarkOpacity = 60;
    public bool StripMeta = true;   // 清除相片資料（位置、相機型號…）
    public string Rename = "{name}";

    private static readonly string[] Exts = { "", ".jpg", ".png", ".bmp", ".gif", ".tif" };
    public static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff";

    /// <summary>輸出檔名（未連資料夾）：{name} 原名、{n} 編號、{date} 今日日期。</summary>
    public string OutName(string src, int index)
    {
        string name = Rename.Trim().Length == 0 ? "{name}" : Rename.Trim();
        name = name.Replace("{name}", Path.GetFileNameWithoutExtension(src)).Replace("{n}", (index + 1).ToString("000")).Replace("{date}", DateTime.Now.ToString("yyyyMMdd"));
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        string ext = Format == 0 ? Path.GetExtension(src).ToLowerInvariant() : Exts[Format];
        if (ext is ".jpeg") ext = ".jpg"; if (ext is ".tiff") ext = ".tif";
        return name + ext;
    }

    /// <summary>處理一張圖，回傳新圖（呼叫方負責 Dispose）。</summary>
    public Bitmap Apply(Image src, out PropertyItem[] meta)
    {
        meta = Array.Empty<PropertyItem>();
        try { meta = src.PropertyItems; } catch { }
        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);
        // 相機方向（EXIF 0x0112）
        if (AutoRotate)
        {
            var o = meta.FirstOrDefault(p => p.Id == 0x0112);
            int v = o?.Value is { Length: >= 2 } b ? b[0] : 1;
            var flip = v switch
            {
                2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone, 4 => RotateFlipType.RotateNoneFlipY, 5 => RotateFlipType.Rotate90FlipX,
                6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX, 8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone
            };
            if (flip != RotateFlipType.RotateNoneFlipNone) bmp.RotateFlip(flip);
            meta = meta.Where(p => p.Id != 0x0112).ToArray(); // 已經轉正：方向標記唔再啱，拎走
        }
        if (Turn > 0)
            bmp.RotateFlip(Turn switch
            {
                1 => RotateFlipType.Rotate90FlipNone, 2 => RotateFlipType.Rotate270FlipNone, 3 => RotateFlipType.Rotate180FlipNone,
                4 => RotateFlipType.RotateNoneFlipX, _ => RotateFlipType.RotateNoneFlipY
            });
        // 縮放
        int w = bmp.Width, h = bmp.Height;
        double scale = ResizeMode switch
        {
            1 => Math.Min(1.0, ResizeValue / (double)Math.Max(w, h)),
            2 => ResizeValue / 100.0,
            3 => ResizeValue / (double)w,
            _ => 1.0
        };
        if (Math.Abs(scale - 1) > 0.001 && scale > 0)
        {
            int nw = Math.Max(1, (int)Math.Round(w * scale)), nh = Math.Max(1, (int)Math.Round(h * scale));
            var small = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using var wrap = new ImageAttributes();
                wrap.SetWrapMode(WrapMode.TileFlipXY); // 邊緣唔會有灰線
                g.DrawImage(bmp, new Rectangle(0, 0, nw, nh), 0, 0, w, h, GraphicsUnit.Pixel, wrap);
            }
            bmp.Dispose();
            bmp = small;
        }
        if (AutoLevels) Levels(bmp);
        if (Brightness != 0 || Contrast != 0 || Saturation != 0) bmp = Adjust(bmp);
        if (Sharpen) SharpenInPlace(bmp);
        if (Watermark.Trim().Length > 0) DrawWatermark(bmp);
        return bmp;
    }

    /// <summary>亮度 / 對比 / 飽和度：用顏色矩陣一次過做。</summary>
    private Bitmap Adjust(Bitmap bmp)
    {
        float c = 1 + Contrast / 100f, b = Brightness / 100f * 0.5f, s = 1 + Saturation / 100f, t = (1 - c) / 2 + b;
        const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
        float sr = (1 - s) * lr, sg = (1 - s) * lg, sb = (1 - s) * lb;
        var m = new ColorMatrix(new[]
        {
            new[] { c * (sr + s), c * sr, c * sr, 0f, 0f },
            new[] { c * sg, c * (sg + s), c * sg, 0f, 0f },
            new[] { c * sb, c * sb, c * (sb + s), 0f, 0f },
            new[] { 0f, 0f, 0f, 1f, 0f },
            new[] { t, t, t, 0f, 1f },
        });
        var outp = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(outp))
        using (var ia = new ImageAttributes())
        {
            ia.SetColorMatrix(m);
            g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
        }
        bmp.Dispose();
        return outp;
    }

    private static byte[] Lock(Bitmap bmp, out BitmapData d)
    {
        d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var px = new byte[Math.Abs(d.Stride) * d.Height];
        Marshal.Copy(d.Scan0, px, 0, px.Length);
        return px;
    }
    private static void Unlock(Bitmap bmp, BitmapData d, byte[] px) { Marshal.Copy(px, 0, d.Scan0, px.Length); bmp.UnlockBits(d); }

    /// <summary>自動調整光暗：每個顏色去走最暗、最光各 0.5% 之後拉返盡，矇矇灰灰嘅相會清返。</summary>
    private static void Levels(Bitmap bmp)
    {
        var px = Lock(bmp, out var d);
        var lut = new byte[3][];
        long total = (long)bmp.Width * bmp.Height;
        for (int ch = 0; ch < 3; ch++)
        {
            var hist = new int[256];
            for (int i = ch; i < px.Length; i += 4) hist[px[i]]++;
            long cut = total / 200, acc = 0;
            int lo = 0, hi = 255;
            for (; lo < 255; lo++) { acc += hist[lo]; if (acc > cut) break; }
            acc = 0;
            for (; hi > 0; hi--) { acc += hist[hi]; if (acc > cut) break; }
            lut[ch] = new byte[256];
            for (int v = 0; v < 256; v++) lut[ch][v] = hi <= lo ? (byte)v : (byte)Math.Clamp((v - lo) * 255 / (hi - lo), 0, 255);
        }
        for (int i = 0; i < px.Length; i += 4) { px[i] = lut[0][px[i]]; px[i + 1] = lut[1][px[i + 1]]; px[i + 2] = lut[2][px[i + 2]]; }
        Unlock(bmp, d, px);
    }

    /// <summary>銳化（3×3：中間 5，上下左右 -1）。</summary>
    private static void SharpenInPlace(Bitmap bmp)
    {
        var px = Lock(bmp, out var d);
        var src = (byte[])px.Clone();
        int w = bmp.Width, h = bmp.Height, st = Math.Abs(d.Stride);
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int p = y * st + x * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    int v = src[p + ch] * 5 - src[p + ch - 4] - src[p + ch + 4] - src[p + ch - st] - src[p + ch + st];
                    // 一半力度，費事有白邊
                    px[p + ch] = (byte)Math.Clamp((v + src[p + ch]) / 2, 0, 255);
                }
            }
        Unlock(bmp, d, px);
    }

    private void DrawWatermark(Bitmap bmp)
    {
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        float size = Math.Max(10f, Math.Min(bmp.Width, bmp.Height) / 22f);
        using var font = new Font("Microsoft JhengHei UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
        var sz = g.MeasureString(Watermark, font);
        float pad = size * 0.8f;
        float x = WatermarkPos is 0 or 2 ? bmp.Width - sz.Width - pad : WatermarkPos == 4 ? (bmp.Width - sz.Width) / 2 : pad;
        float y = WatermarkPos is 0 or 1 ? bmp.Height - sz.Height - pad : WatermarkPos == 4 ? (bmp.Height - sz.Height) / 2 : pad;
        int a = Math.Clamp(WatermarkOpacity * 255 / 100, 10, 255);
        using var shadow = new SolidBrush(Color.FromArgb(a * 2 / 3, 0, 0, 0));
        using var fore = new SolidBrush(Color.FromArgb(a, 255, 255, 255));
        g.DrawString(Watermark, font, shadow, x + Math.Max(1, size / 14), y + Math.Max(1, size / 14));
        g.DrawString(Watermark, font, fore, x, y);
    }

    public void Save(Bitmap bmp, string path, PropertyItem[] meta)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        // 唔清相片資料：抄返原本嘅標記（JPG / TIFF 先有）
        if (!StripMeta && ext is ".jpg" or ".tif")
            foreach (var p in meta) try { bmp.SetPropertyItem(p); } catch { }
        if (ext == ".jpg")
        {
            // JPG 冇透明：先鋪白底
            using var flat = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(flat)) { g.Clear(Color.White); g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height); }
            if (!StripMeta) foreach (var p in meta) try { flat.SetPropertyItem(p); } catch { }
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(Quality, 1, 100));
            flat.Save(path, codec, ep);
            return;
        }
        bmp.Save(path, ext switch { ".png" => ImageFormat.Png, ".bmp" => ImageFormat.Bmp, ".gif" => ImageFormat.Gif, ".tif" => ImageFormat.Tiff, _ => ImageFormat.Png });
    }
}

/// <summary>圖片處理：一次過幫好多張圖縮細、轉格式、修靚、加水印、改名、清除相片資料。原圖唔會被改。</summary>
internal sealed class ImagePage : ToolPage
{
    private readonly List<string> _files = new();
    private readonly HashSet<string> _off = new(StringComparer.OrdinalIgnoreCase);   // 用家唔剔嘅圖（唔處理）
    private readonly RowList _list = new();
    /// <summary>剔咗、要處理嘅圖。</summary>
    private List<string> Picked => _files.Where(f => !_off.Contains(f)).ToList();
    private readonly ImageJob _job = new();
    private readonly Label _count, _status, _outLabel;
    private readonly PictureBox _before = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Ui.Bg }, _after = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Ui.Bg };
    private readonly ProgressBar _bar = new() { Width = Ui.S(420), Height = Ui.S(8), Visible = false, Margin = new Padding(0, Ui.S(4), 0, Ui.S(8)) };
    private readonly FancyButton _go;
    private string _outDir = "";
    private CancellationTokenSource? _cts;
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 350 };

    public static Control Build() => new ImagePage();

    private ImagePage()
    {
        AllowDrop = true;
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] f) Add(f); };

        _count = TUi.L(Lang.T("未加入圖片：撳「加入圖片…」或者將圖片 / 資料夾拖入嚟。"));
        // 圖片清單：剔咗嘅先會處理，所以可以淨係處理其中一張 / 幾張
        _list.Columns = new[] { (Lang.T("檔案"), 0, false), (Lang.T("大細"), 90, true) };
        _list.Checks = true;
        _list.ActionText = Lang.T("移除");
        _list.EmptyText = Lang.T("未加入圖片：撳「加入圖片…」或者將圖片 / 資料夾拖入嚟。");
        _list.Width = Ui.S(760); _list.Height = Ui.S(200);
        _list.CheckChanged += () => { foreach (var r in _list.Rows) if (r.Tag is string f) { if (r.Checked) _off.Remove(f); else _off.Add(f); } Changed(false); };
        _list.RowAction += r => { if (r.Tag is string f) { _files.Remove(f); _off.Remove(f); Changed(); } };
        void TickAll(bool on) { if (on) _off.Clear(); else foreach (var f in _files) _off.Add(f); Changed(); }
        Card(Lang.T("圖片處理"), Lang.T("一次過處理好多張圖：縮細、轉格式、修靚、加水印、改名、清除相片資料。原圖唔會被改，處理好嘅另存一份。支援 JPG、PNG、BMP、GIF、TIFF。"),
            TUi.Col(
                TUi.Row(TUi.Btn(Lang.T("加入圖片…"), PickFiles, 0, BtnKind.Primary), TUi.Btn(Lang.T("加入資料夾…"), PickFolder), TUi.Btn(Lang.T("清除"), () => { _files.Clear(); _off.Clear(); Changed(); }),
                    TUi.Btn(Lang.T("全部剔"), () => TickAll(true)), TUi.Btn(Lang.T("全部唔剔"), () => TickAll(false))),
                _list,
                _count));
        Resize += (_, _) => _list.Width = Math.Max(Ui.S(480), Width - Ui.S(110));

        // ---- 大細同格式 ----
        var rsMode = TIn.Combo(190, new[] { Lang.T("唔縮放"), Lang.T("最長邊唔超過（像素）"), Lang.T("百分比（%）"), Lang.T("指定闊度（像素）") });
        var rsVal = new NumberBox(Ui.S(100)) { Minimum = 1, Maximum = 20000, Value = _job.ResizeValue, Margin = new Padding(0, Ui.S(2), Ui.S(10), 0) };
        rsMode.SelectedIndexChanged += (_, _) =>
        {
            _job.ResizeMode = rsMode.SelectedIndex;
            rsVal.Value = _job.ResizeMode == 2 ? 50 : 1920;
            rsVal.Enabled = _job.ResizeMode != 0;
            Preview();
        };
        rsVal.ValueChanged += (_, _) => { _job.ResizeValue = (int)rsVal.Value; Preview(); };
        rsVal.Enabled = false;
        var fmt = TIn.Combo(140, new[] { Lang.T("保持原本格式"), "JPG", "PNG", "BMP", "GIF", "TIFF" });
        fmt.SelectedIndexChanged += (_, _) => _job.Format = fmt.SelectedIndex;
        var quality = new NumberBox(Ui.S(84)) { Minimum = 1, Maximum = 100, Value = _job.Quality, Margin = new Padding(0, Ui.S(2), Ui.S(10), 0) };
        quality.ValueChanged += (_, _) => _job.Quality = (int)quality.Value;
        Card(Lang.T("大細同格式"), null, TUi.Col(
            TUi.Row(TUi.L(Lang.T("縮放")), rsMode, rsVal),
            TUi.Row(TUi.L(Lang.T("格式")), fmt, TUi.L(Lang.T("JPG 質素（1–100，數字細 = 檔案細）")), quality)));

        // ---- 修靚 ----
        var turn = TIn.Combo(160, new[] { Lang.T("唔旋轉"), Lang.T("順時針 90°"), Lang.T("逆時針 90°"), "180°", Lang.T("左右反轉"), Lang.T("上下反轉") });
        turn.SelectedIndexChanged += (_, _) => { _job.Turn = turn.SelectedIndex; Preview(); };
        var adj = TUi.Row();
        foreach (var (label, set) in new (string, Action<int>)[] { (Lang.T("亮度"), v => _job.Brightness = v), (Lang.T("對比"), v => _job.Contrast = v), (Lang.T("飽和度"), v => _job.Saturation = v) })
        {
            var nb = new NumberBox(Ui.S(84)) { Minimum = -100, Maximum = 100, Increment = 5, Value = 0, Margin = new Padding(0, Ui.S(2), Ui.S(16), 0) };
            var s = set;
            nb.ValueChanged += (_, _) => { s((int)nb.Value); Preview(); };
            adj.Controls.Add(TUi.L(label));
            adj.Controls.Add(nb);
        }
        Card(Lang.T("修靚"), Lang.T("亮度、對比、飽和度：-100 至 100，0 = 唔改。"), TUi.Col(
            TUi.Row(
                TUi.Check(Lang.T("自動轉正（跟相機方向）"), _job.AutoRotate, v => { _job.AutoRotate = v; Preview(); }),
                TUi.Check(Lang.T("自動調整光暗"), _job.AutoLevels, v => { _job.AutoLevels = v; Preview(); }),
                TUi.Check(Lang.T("銳化（清啲）"), _job.Sharpen, v => { _job.Sharpen = v; Preview(); })),
            adj,
            TUi.Row(TUi.L(Lang.T("旋轉")), turn)));

        // ---- 水印、改名、私隱 ----
        var wm = TIn.Box("", 260, Lang.T("例如：© 你個名"));
        wm.TextChanged += (_, _) => { _job.Watermark = wm.Text; Preview(); };
        var wmPos = TIn.Combo(110, new[] { Lang.T("右下角"), Lang.T("左下角"), Lang.T("右上角"), Lang.T("左上角"), Lang.T("正中") });
        wmPos.SelectedIndexChanged += (_, _) => { _job.WatermarkPos = wmPos.SelectedIndex; Preview(); };
        var wmOp = new NumberBox(Ui.S(84)) { Minimum = 10, Maximum = 100, Increment = 10, Value = _job.WatermarkOpacity, Margin = new Padding(0, Ui.S(2), Ui.S(10), 0) };
        wmOp.ValueChanged += (_, _) => { _job.WatermarkOpacity = (int)wmOp.Value; Preview(); };
        var rename = TIn.Box(_job.Rename, 260);
        rename.TextChanged += (_, _) => _job.Rename = rename.Text;
        Card(Lang.T("水印、改名同私隱"), null, TUi.Col(
            TUi.Row(TUi.L(Lang.T("水印文字")), wm, wmPos, TUi.L(Lang.T("不透明度 %")), wmOp),
            TUi.Row(TUi.L(Lang.T("檔名")), rename, TUi.L(Lang.T("{name} = 原本檔名、{n} = 編號、{date} = 今日日期"))),
            TUi.Row(TUi.Check(Lang.T("清除相片資料（影相地點、相機型號、拍攝時間）"), _job.StripMeta, v => _job.StripMeta = v))));

        // ---- 預覽 ----
        _before.Size = _after.Size = new Size(Ui.S(400), Ui.S(260));
        _before.Margin = _after.Margin = new Padding(0, 0, Ui.S(12), Ui.S(4));
        Card(Lang.T("預覽"), Lang.T("顯示剔咗嘅第一張：左邊係原圖，右邊係處理後。"), TUi.Row(_before, _after));

        // ---- 輸出 ----
        _outLabel = TUi.L("");
        _status = TUi.L("");
        _go = TUi.Btn(Lang.T("開始處理"), () => _ = RunAsync(), 140, BtnKind.Primary);
        Card(Lang.T("輸出"), null, TUi.Col(
            TUi.Row(TUi.Btn(Lang.T("揀輸出資料夾…"), PickOut), _outLabel),
            TUi.Row(_go, TUi.Btn(Lang.T("開啟輸出資料夾"), OpenOut), _status),
            _bar));

        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); DoPreview(); };
        Disposed += (_, _) => { _cts?.Cancel(); _previewTimer.Dispose(); _before.Image?.Dispose(); _after.Image?.Dispose(); };
        Changed();
    }

    private void PickFiles()
    {
        using var d = new OpenFileDialog { Multiselect = true, Filter = Lang.T("圖片") + "|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff" };
        if (d.ShowDialog(FindForm()) == DialogResult.OK) Add(d.FileNames);
    }

    private void PickFolder()
    {
        using var d = new FolderBrowserDialog();
        if (d.ShowDialog(FindForm()) == DialogResult.OK) Add(new[] { d.SelectedPath });
    }

    private void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            try
            {
                if (Directory.Exists(p)) _files.AddRange(Directory.EnumerateFiles(p).Where(ImageJob.IsImage).Where(f => !_files.Contains(f)));
                else if (File.Exists(p) && ImageJob.IsImage(p) && !_files.Contains(p)) _files.Add(p);
            }
            catch { }
        if (_outDir.Length == 0 && _files.Count > 0) _outDir = Path.Combine(Path.GetDirectoryName(_files[0]) ?? "", Lang.T("處理後"));
        Changed();
    }

    private void Changed(bool rebuild = true)
    {
        long total = 0;
        var picked = Picked;
        foreach (var f in picked) try { total += new FileInfo(f).Length; } catch { }
        if (rebuild)
        {
            _list.Rows.Clear();
            foreach (var f in _files)
            {
                long size = 0;
                try { size = new FileInfo(f).Length; } catch { }
                _list.Rows.Add(new RowList.Row { Cells = new[] { f, TIn.Size(size) }, Checked = !_off.Contains(f), Tag = f });
            }
            _list.Reload();
        }
        _count.Text = _files.Count == 0 ? "" : Lang.F("已加入 {0} 張，會處理剔咗嘅 {1} 張（{2}）", _files.Count, picked.Count, TIn.Size(total));
        _outLabel.Text = _outDir.Length > 0 ? _outDir : Lang.T("（加入圖片後，預設存去原資料夾入面嘅「處理後」）");
        _go.Enabled = picked.Count > 0;
        _go.Invalidate();
        Preview();
    }

    private void PickOut()
    {
        using var d = new FolderBrowserDialog();
        if (d.ShowDialog(FindForm()) == DialogResult.OK) { _outDir = d.SelectedPath; Changed(); }
    }

    private void OpenOut()
    {
        if (_outDir.Length == 0 || !Directory.Exists(_outDir)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _outDir + "\"") { UseShellExecute = true }); } catch { }
    }

    private string? _beforeOf;
    private Bitmap? _thumb;   // 第一張圖縮細咗嘅版本，預覽用（改設定唔使次次讀大圖）

    private void Preview() { _previewTimer.Stop(); _previewTimer.Start(); }

    private void DoPreview()
    {
        string? first = Picked.FirstOrDefault();
        if (first == null)
        {
            _beforeOf = null;
            _before.Image?.Dispose(); _before.Image = null; _after.Image?.Dispose(); _after.Image = null; _thumb?.Dispose(); _thumb = null;
            return;
        }
        try
        {
            if (_beforeOf != first || _thumb == null)
            {
                using var fs = File.OpenRead(first);
                using var img = Image.FromStream(fs);
                double s = Math.Min(1.0, 900.0 / Math.Max(img.Width, img.Height));
                _thumb?.Dispose();
                _thumb = new Bitmap(Math.Max(1, (int)(img.Width * s)), Math.Max(1, (int)(img.Height * s)));
                using (var g = Graphics.FromImage(_thumb)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(img, 0, 0, _thumb.Width, _thumb.Height); }
                foreach (var p in img.PropertyItems.Where(p => p.Id == 0x0112)) try { _thumb.SetPropertyItem(p); } catch { }
                _beforeOf = first;
                var oldB = _before.Image; _before.Image = (Bitmap)_thumb.Clone(); oldB?.Dispose();
            }
            // 預覽唔做縮放（淨係睇效果）
            var job = (ImageJob)CloneJob();
            job.ResizeMode = 0;
            var res = job.Apply(_thumb, out _);
            var old = _after.Image; _after.Image = res; old?.Dispose();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private object CloneJob() => new ImageJob
    {
        ResizeMode = _job.ResizeMode, ResizeValue = _job.ResizeValue, Format = _job.Format, Quality = _job.Quality, AutoRotate = _job.AutoRotate, Turn = _job.Turn,
        AutoLevels = _job.AutoLevels, Brightness = _job.Brightness, Contrast = _job.Contrast, Saturation = _job.Saturation, Sharpen = _job.Sharpen,
        Watermark = _job.Watermark, WatermarkPos = _job.WatermarkPos, WatermarkOpacity = _job.WatermarkOpacity, StripMeta = _job.StripMeta, Rename = _job.Rename
    };

    private async Task RunAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; }   // 做緊：再撳 = 停
        var files = Picked;
        if (files.Count == 0) return;
        var job = (ImageJob)CloneJob();
        string outDir = _outDir;
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) { _status.Text = ex.Message; return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _go.Text = Lang.T("停止"); _go.Kind = BtnKind.Danger; _go.Invalidate();
        _bar.Visible = true; _bar.Maximum = files.Count; _bar.Value = 0;
        int ok = 0, fail = 0; long before = 0, after = 0;
        string lastErr = "";
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await Task.Run(() =>
        {
            for (int i = 0; i < files.Count && !ct.IsCancellationRequested; i++)
            {
                string src = files[i];
                try
                {
                    string dst = Path.Combine(outDir, job.OutName(src, i));
                    // 唔覆寫原圖、唔覆寫今次已經出咗嘅檔：撞名就加 (2)、(3)…
                    for (int n = 2; used.Contains(dst) || string.Equals(dst, src, StringComparison.OrdinalIgnoreCase) || File.Exists(dst); n++)
                        dst = Path.Combine(outDir, Path.GetFileNameWithoutExtension(job.OutName(src, i)) + $" ({n})" + Path.GetExtension(dst));
                    used.Add(dst);
                    using (var fs = File.OpenRead(src))
                    using (var img = Image.FromStream(fs))
                    using (var res = job.Apply(img, out var meta))
                        job.Save(res, dst, meta);
                    before += new FileInfo(src).Length; after += new FileInfo(dst).Length;
                    ok++;
                }
                catch (Exception ex) { fail++; lastErr = Path.GetFileName(src) + "：" + ex.Message; AppPaths.LogError("圖片處理", ex); }
                int done = i + 1;
                try { BeginInvoke(() => { if (!IsDisposed) { _bar.Value = Math.Min(done, _bar.Maximum); _status.Text = Lang.F("處理緊… {0} / {1}", done, files.Count); } }); } catch { }
            }
        });
        bool cancelled = ct.IsCancellationRequested;
        _cts = null;
        if (IsDisposed) return;
        _bar.Visible = false;
        _go.Text = Lang.T("開始處理"); _go.Kind = BtnKind.Primary; _go.Invalidate();
        _status.ForeColor = fail > 0 ? Ui.Danger : Ui.Success;
        _status.Text = (cancelled ? Lang.T("已停止。") + " " : "") + Lang.F("完成 {0} 張（{1} → {2}）", ok, TIn.Size(before), TIn.Size(after)) + (fail > 0 ? "　" + Lang.F("{0} 張失敗：{1}", fail, lastErr) : "");
        if (ok > 0) Toast.Show(Lang.F("圖片處理：完成 {0} 張", ok), Ui.Success);
    }
}
