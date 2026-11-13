using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Tools;

/// <summary>二維碼：產生（文字 / 網址、Wi-Fi、聯絡人）同讀取（框選畫面、圖片、剪貼簿）。全部喺本機做，唔使上網。</summary>
internal sealed class QrPage : ToolPage
{
    private readonly ComboBox _type, _ecc, _wifiSec;
    private readonly TextBox _text, _ssid, _pass, _name, _phone, _mail, _org, _result;
    private readonly FlowLayoutPanel _fText, _fWifi, _fCard;
    private readonly PictureBox _pic = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly Label _genStatus, _readStatus;
    private readonly FancyButton _open;
    private bool[,]? _matrix;

    public static Control Build() => new QrPage();

    private QrPage()
    {
        _type = TIn.Combo(150, new[] { Lang.T("文字 / 網址"), "Wi-Fi", Lang.T("聯絡人") });
        _ecc = TIn.Combo(210, new[] { Lang.T("容錯：低（碼最細）"), Lang.T("容錯：中"), Lang.T("容錯：高"), Lang.T("容錯：最高（整污糟咗都讀到）") }, 1);
        _text = TIn.Box("", 520, Lang.T("輸入文字或者網址"));
        _text.Multiline = true; _text.Height = Ui.S(84); _text.ScrollBars = ScrollBars.Vertical;
        _ssid = TIn.Box("", 200, Lang.T("Wi-Fi 名稱"));
        _pass = TIn.Box("", 200, Lang.T("密碼"));
        _wifiSec = TIn.Combo(130, new[] { "WPA / WPA2", "WEP", Lang.T("冇密碼") });
        _name = TIn.Box("", 200, Lang.T("姓名"));
        _phone = TIn.Box("", 200, Lang.T("電話"));
        _mail = TIn.Box("", 240, Lang.T("電郵"));
        _org = TIn.Box("", 240, Lang.T("公司"));
        _fText = TUi.Row(_text);
        _fWifi = TUi.Row(_ssid, _pass, _wifiSec);
        _fCard = TUi.Col(TUi.Row(_name, _phone), TUi.Row(_mail, _org));
        _genStatus = TUi.L("");
        _pic.Size = new Size(Ui.S(240), Ui.S(240));
        _pic.Margin = new Padding(0, 0, Ui.S(16), Ui.S(8));
        Card(Lang.T("產生二維碼"), Lang.T("打字就即時產生。Wi-Fi 二維碼用手機相機一掃就連到；聯絡人二維碼一掃就加入通訊錄。"),
            TUi.Col(
                TUi.Row(_type, _ecc),
                _fText, _fWifi, _fCard,
                TUi.Row(_pic, TUi.Col(
                    TUi.Row(TUi.Btn(Lang.T("複製圖片"), CopyImage, 0, BtnKind.Primary), TUi.Btn(Lang.T("另存 PNG…"), SavePng)),
                    _genStatus))));
        foreach (var c in new Control[] { _text, _ssid, _pass, _name, _phone, _mail, _org }) c.TextChanged += (_, _) => Generate();
        _type.SelectedIndexChanged += (_, _) => { ShowFields(); Generate(); };
        _ecc.SelectedIndexChanged += (_, _) => Generate();
        _wifiSec.SelectedIndexChanged += (_, _) => Generate();

        _result = TIn.Box("", 640);
        _result.Multiline = true; _result.Height = Ui.S(110); _result.ScrollBars = ScrollBars.Vertical; _result.ReadOnly = true;
        _readStatus = TUi.L("");
        _open = TUi.Btn(Lang.T("開啟連結"), OpenLink);
        _open.Visible = false;
        Card(Lang.T("讀取二維碼"), Lang.T("框住畫面上嘅二維碼、揀一張圖片、由剪貼簿讀取，或者直接將圖片拖入呢頁。打斜影或者好矇嘅相未必讀到。"),
            TUi.Col(
                TUi.Row(TUi.Btn(Lang.T("▣ 框選畫面讀取"), () => _ = FromScreenAsync(), 0, BtnKind.Primary), TUi.Btn(Lang.T("從圖片讀取…"), FromFile), TUi.Btn(Lang.T("由剪貼簿讀取"), FromClipboard), _readStatus),
                _result,
                TUi.Row(TUi.Btn(Lang.T("複製"), () => { if (_result.Text.Length > 0) { Clipboard.SetText(_result.Text); _readStatus.Text = Lang.T("已複製"); } }), _open)));
        // 將圖片拖入嚟（檔案，或者由瀏覽器直接拖張圖）就讀取
        AllowDrop = true;
        DragEnter += (_, e) => { if (e.Data != null && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap))) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) =>
        {
            try
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                {
                    using var fs = File.OpenRead(files[0]);
                    using var img = Image.FromStream(fs);
                    await ShowAsync(img);
                }
                else if (e.Data?.GetData(DataFormats.Bitmap) is Image bmp) { using (bmp) await ShowAsync(bmp); }
            }
            catch (Exception ex) { _readStatus.ForeColor = Ui.Danger; _readStatus.Text = Lang.T("開唔到呢張圖片。") + " " + ex.Message; }
        };
        Disposed += (_, _) => _pic.Image?.Dispose();
        ShowFields();
        Generate();
    }

    private void ShowFields()
    {
        _fText.Visible = _type.SelectedIndex == 0;
        _fWifi.Visible = _type.SelectedIndex == 1;
        _fCard.Visible = _type.SelectedIndex == 2;
        Relayout();
    }

    /// <summary>Wi-Fi / 聯絡人格式入面要跳脫嘅字。</summary>
    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace(":", "\\:").Replace("\"", "\\\"");

    private string Payload()
    {
        switch (_type.SelectedIndex)
        {
            case 1:
                if (_ssid.Text.Length == 0) return "";
                string sec = _wifiSec.SelectedIndex == 0 ? "WPA" : _wifiSec.SelectedIndex == 1 ? "WEP" : "nopass";
                return $"WIFI:T:{sec};S:{Esc(_ssid.Text)};" + (sec == "nopass" ? "" : $"P:{Esc(_pass.Text)};") + ";";
            case 2:
                if (_name.Text.Trim().Length == 0 && _phone.Text.Trim().Length == 0) return "";
                var lines = new List<string> { "BEGIN:VCARD", "VERSION:3.0", "FN:" + _name.Text.Trim(), "N:" + _name.Text.Trim() + ";;;;" };
                if (_phone.Text.Trim().Length > 0) lines.Add("TEL;TYPE=CELL:" + _phone.Text.Trim());
                if (_mail.Text.Trim().Length > 0) lines.Add("EMAIL:" + _mail.Text.Trim());
                if (_org.Text.Trim().Length > 0) lines.Add("ORG:" + _org.Text.Trim());
                lines.Add("END:VCARD");
                return string.Join("\n", lines);
            default:
                return _text.Text;
        }
    }

    private void Generate()
    {
        string s = Payload();
        var old = _pic.Image;
        if (s.Length == 0) { _matrix = null; _pic.Image = null; old?.Dispose(); _genStatus.Text = ""; return; }
        try
        {
            _matrix = QrCode.Encode(s, (QrCode.Ecc)_ecc.SelectedIndex);
            _pic.Image = Render(_matrix, 8);
            old?.Dispose();
            _genStatus.ForeColor = Ui.TextDim;
            _genStatus.Text = Lang.F("{0} × {0} 格", _matrix.GetLength(0));
        }
        catch (ArgumentException)
        {
            _matrix = null; _pic.Image = null; old?.Dispose();
            _genStatus.ForeColor = Ui.Danger;
            _genStatus.Text = Lang.T("內容太長，一個二維碼放唔落。試吓縮短，或者揀低啲嘅容錯。");
        }
    }

    /// <summary>畫成圖片：每格 scale 像素，四邊留 4 格白邊（標準要求，冇白邊好多掃描器讀唔到）。</summary>
    private static Bitmap Render(bool[,] m, int scale)
    {
        int n = m.GetLength(0), size = (n + 8) * scale;
        var bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (m[y, x]) g.FillRectangle(Brushes.Black, (x + 4) * scale, (y + 4) * scale, scale, scale);
        return bmp;
    }

    private void CopyImage()
    {
        if (_matrix == null) return;
        try { using var bmp = Render(_matrix, 12); Clipboard.SetImage(bmp); _genStatus.ForeColor = Ui.Success; _genStatus.Text = Lang.T("已複製圖片"); } catch { }
    }

    private void SavePng()
    {
        if (_matrix == null) return;
        using var d = new SaveFileDialog { Filter = "PNG|*.png", FileName = "qr.png" };
        if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
        try { using var bmp = Render(_matrix, 16); bmp.Save(d.FileName, ImageFormat.Png); _genStatus.ForeColor = Ui.Success; _genStatus.Text = Lang.T("已儲存"); }
        catch (Exception ex) { _genStatus.ForeColor = Ui.Danger; _genStatus.Text = ex.Message; }
    }

    // ---------------- 讀取 ----------------

    /// <summary>將圖片變灰階再交俾 QrCode.Decode；太大嘅圖先縮細（快啲，亦都夠讀）。</summary>
    public static string? Read(Image img)
    {
        foreach (int limit in new[] { 1400, 2800, 700 })
        {
            double s = Math.Min(1.0, limit / (double)Math.Max(img.Width, img.Height));
            int w = Math.Max(1, (int)(img.Width * s)), h = Math.Max(1, (int)(img.Height * s));
            using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.InterpolationMode = s < 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
                g.DrawImage(img, 0, 0, w, h);
            }
            var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var px = new byte[Math.Abs(d.Stride) * h];
            Marshal.Copy(d.Scan0, px, 0, px.Length);
            bmp.UnlockBits(d);
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0, p = y * Math.Abs(d.Stride); x < w; x++, p += 3)
                    gray[y * w + x] = (byte)((px[p] * 29 + px[p + 1] * 150 + px[p + 2] * 77) >> 8);
            var r = QrCode.Decode(gray, w, h);
            if (r != null) return r;
            if (s >= 1) break;   // 已經係原大細：唔使再試其他大細
        }
        return null;
    }

    private async Task FromScreenAsync()
    {
        Rectangle r;
        using (var p = new Video.RegionPicker(Lang.T("拉一個框包住二維碼（Esc 取消）")))
        {
            if (p.ShowDialog() != DialogResult.OK) return;
            r = p.Result;
        }
        await Task.Delay(150); // 等個遮罩消失
        try
        {
            using var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            await ShowAsync(bmp);
        }
        catch (Exception ex) { _readStatus.Text = ex.Message; }
    }

    private async void FromFile()
    {
        using var d = new OpenFileDialog { Filter = Lang.T("圖片") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" };
        if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            using var fs = File.OpenRead(d.FileName);
            using var img = Image.FromStream(fs);
            await ShowAsync(img);
        }
        catch (Exception ex) { _readStatus.Text = ex.Message; }
    }

    private async void FromClipboard()
    {
        try
        {
            using var img = Clipboard.GetImage();
            if (img == null) { _readStatus.ForeColor = Ui.TextDim; _readStatus.Text = Lang.T("剪貼簿入面冇圖片。"); return; }
            await ShowAsync(img);
        }
        catch (Exception ex) { _readStatus.Text = ex.Message; }
    }

    private async Task ShowAsync(Image img)
    {
        _readStatus.ForeColor = Ui.TextDim;
        _readStatus.Text = Lang.T("讀取緊…");
        using var copy = new Bitmap(img);   // 背景執行緒用自己嗰份
        string? text = await Task.Run(() => Read(copy));
        if (IsDisposed) return;
        if (text == null)
        {
            _result.Text = "";
            _open.Visible = false;
            _readStatus.ForeColor = Ui.Danger;
            _readStatus.Text = Lang.T("搵唔到二維碼。框大少少、影正啲再試。");
            return;
        }
        _result.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _open.Visible = Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");
        _readStatus.ForeColor = Ui.Success;
        _readStatus.Text = Lang.T("讀到咗");
    }

    private void OpenLink()
    {
        string url = _result.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return;
        // 二維碼入面嘅連結可以係任何網站：開之前俾用家睇清楚
        if (MessageBox.Show(FindForm(), Lang.T("開啟呢個連結？請先睇清楚網址係咪你信得過嘅網站。") + "\n\n" + url, Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }
}
