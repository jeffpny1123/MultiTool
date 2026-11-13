using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Tools;

/// <summary>
/// 畫面即時翻譯：用家畫一個框 → 每隔一陣（最快 0.5 秒）睇吓框入面有冇變 → 有變先用 Windows 文字辨識讀字 → 字定咗先翻譯 → 喺譯文視窗顯示。
/// 翻譯可以用翻譯服務（DeepL / Google / Microsoft，要用家自己嘅金鑰，快）或者「AI 助手」揀咗嘅 AI。
/// 框同譯文視窗都設咗「唔會被截圖」，所以讀字嗰陣唔會讀到自己嘅譯文。
/// </summary>
internal static class ScreenTranslate
{
    /// <summary>目標語言：顯示名、俾 AI 嘅講法、DeepL / Google / Microsoft 嘅語言代碼。</summary>
    public static readonly (string Name, string Prompt, string Deepl, string Google, string Azure)[] Targets =
    {
        ("繁體中文", "Traditional Chinese", "ZH-HANT", "zh-TW", "zh-Hant"),
        ("廣東話（口語）", "colloquial Hong Kong Cantonese written in Traditional Chinese", "ZH-HANT", "yue", "yue"),
        ("简体中文", "Simplified Chinese", "ZH-HANS", "zh-CN", "zh-Hans"),
        ("English", "English", "EN-US", "en", "en"),
        ("日本語", "Japanese", "JA", "ja", "ja"),
        ("한국어", "Korean", "KO", "ko", "ko"),
        ("Русский", "Russian", "RU", "ru", "ru"),
        ("Español", "Spanish", "ES", "es", "es"),
        ("Português", "Brazilian Portuguese", "PT-BR", "pt", "pt"),
        ("Deutsch", "German", "DE", "de", "de"),
        ("Français", "French", "FR", "fr", "fr"),
    };
    public static readonly string[] Engines = { "ai", "deepl", "google", "azure" };

    public static string SourceLang = "";      // Windows 文字辨識語言（空 = 跟 Windows）
    public static int Target;                  // Targets 嘅索引
    public static int IntervalMs = 1000;       // 幾耐睇一次（最快 500）
    public static string Engine = "ai";        // 用邊個翻譯
    public static string Key = "", Region = "";// 翻譯服務金鑰（DPAPI 加密存）；Microsoft 要埋地區
    public static int FontSize = 12;
    public static int Back;                    // 0 深色、1 淺色、2 透明
    public static int Opacity = 92;            // 有色背景嘅不透明度 %
    public static Rectangle Saved;             // 用家擺好嘅譯文視窗位置同大細（空 = 未擺過，放喺框下面）
    private static string Settings_ => Path.Combine(AppPaths.Data, "screentranslate.txt");
    private static bool _loaded;

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        // 預設譯做介面語言
        string code = Lang.Code(Lang.Current);
        Target = code.Contains("Hans", StringComparison.OrdinalIgnoreCase) ? 2 : code.StartsWith("zh") ? 0 : code.StartsWith("ja") ? 4 : code.StartsWith("ko") ? 5 : code.StartsWith("ru") ? 6
            : code.StartsWith("es") ? 7 : code.StartsWith("pt") ? 8 : code.StartsWith("de") ? 9 : code.StartsWith("fr") ? 10 : 3;
        try
        {
            if (!File.Exists(Settings_)) return;
            foreach (var l in File.ReadAllLines(Settings_))
            {
                int eq = l.IndexOf('=');
                if (eq < 0) continue;
                string k = l[..eq], v = l[(eq + 1)..];
                int.TryParse(v, out int n);
                switch (k)
                {
                    case "src": SourceLang = v; break;
                    case "target": Target = Math.Clamp(n, 0, Targets.Length - 1); break;
                    case "interval": IntervalMs = n <= 30 ? Math.Clamp(n, 1, 30) * 1000 : Math.Clamp(n, 500, 30000); break;   // 舊版係秒
                    case "engine": if (Engines.Contains(v)) Engine = v; break;
                    case "key": Key = Mail.MailStore.Unprotect(v); break;
                    case "region": Region = v; break;
                    case "font": FontSize = Math.Clamp(n, 8, 48); break;
                    case "back": Back = Math.Clamp(n, 0, 2); break;
                    case "opacity": Opacity = Math.Clamp(n, 30, 100); break;
                    case "box":
                        var p = v.Split(',');
                        if (p.Length == 4 && p.All(x => int.TryParse(x, out _))) Saved = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                        break;
                }
            }
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllLines(Settings_, new[]
            {
                "src=" + SourceLang, "target=" + Target, "interval=" + IntervalMs, "engine=" + Engine, "key=" + Mail.MailStore.Protect(Key), "region=" + Region,
                "font=" + FontSize, "back=" + Back, "opacity=" + Opacity, "box=" + (Saved.IsEmpty ? "" : $"{Saved.X},{Saved.Y},{Saved.Width},{Saved.Height}")
            });
        }
        catch { }
    }

    private static CancellationTokenSource? _cts;
    private static Frame? _frame;
    private static Result? _result;
    public static bool Running => _cts != null;
    public static event Action? StateChanged;

    public static void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _frame?.Close(); _frame = null;
        _result?.Close(); _result = null;
        StateChanged?.Invoke();
    }

    /// <summary>譯文視窗嘅樣（字型大細、背景）改咗：行緊就即時套用。</summary>
    public static void RestyleResult() => _result?.Restyle();

    /// <summary>唔再用用家擺過嘅位置：下次（或者而家）放返喺框下面。</summary>
    public static void ResetBox()
    {
        Saved = Rectangle.Empty;
        Save();
        _result?.PlaceDefault();
    }

    /// <summary>俾用家畫框，然後開始翻譯。行緊就先停。</summary>
    public static async void Toggle()
    {
        if (Running) { Stop(); return; }
        Load();
        if (Engine != "ai" && Key.Trim().Length == 0)
        {
            Toast.Show(Lang.T("畫面翻譯：未填翻譯服務嘅金鑰，請去「畫面翻譯」頁填，或者改用 AI。"), Ui.Danger, 5000);
            return;
        }
        Rectangle r;
        using (var p = new Video.RegionPicker(Lang.T("拉一個框揀要翻譯嘅位置（Esc 取消）")))
        {
            if (p.ShowDialog() != DialogResult.OK) return;
            r = p.Result;
        }
        if (r.Width < 20 || r.Height < 12) return;
        await Task.Delay(150);
        var cts = _cts = new CancellationTokenSource();
        _frame = new Frame(r);
        _result = new Result(r);
        _frame.Show(); _result.Show();
        _result.SetText(Lang.T("讀緊畫面…"), true);
        StateChanged?.Invoke();
        _ = Task.Run(() => LoopAsync(r, cts));
    }

    private static string Norm(string s) => Regex.Replace(s, @"\s+", "");

    private static void Ui_(Action a)
    {
        var f = _result;
        try { if (f is { IsDisposed: false, IsHandleCreated: true }) f.BeginInvoke(a); } catch { }
    }

    /// <summary>影低個框，回傳圖同「畫面指紋」（隔幾粒像素抽樣）；指紋冇變即係畫面冇變，唔使再讀字。</summary>
    private static (Bitmap Bmp, ulong Hash) Capture(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        var d = bmp.LockBits(new Rectangle(0, 0, r.Width, r.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        ulong h = 1469598103934665603UL;
        try
        {
            int stride = Math.Abs(d.Stride), step = Math.Max(1, Math.Min(r.Width, r.Height) / 60);
            var row = new byte[stride];
            for (int y = 0; y < r.Height; y += step)
            {
                Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, stride);
                for (int x = 0; x < r.Width * 3; x += step * 3) h = (h ^ (byte)(row[x] >> 3)) * 1099511628211UL;   // 去走最低 3 位：細微閃動唔當變
            }
        }
        finally { bmp.UnlockBits(d); }
        return (bmp, h);
    }

    private static async Task LoopAsync(Rectangle r, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        string shown = "", pending = "";
        ulong lastHash = 0;
        var cache = new Dictionary<string, string>();
        int fails = 0;
        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var (bmp, hash) = Capture(r);
                using (bmp)
                {
                    // 畫面冇變、而且冇字等緊確認：呢輪乜都唔使做
                    if (hash != lastHash || pending.Length > 0)
                    {
                        lastHash = hash;
                        string text = FastOcr.Recognize(bmp, SourceLang, ct).Trim();
                        string key = Norm(text);
                        if (key.Length == 0)
                        {
                            pending = "";
                            if (shown.Length > 0) { shown = ""; Ui_(() => _result?.SetText("", true)); }
                        }
                        else if (key == shown) pending = "";
                        // 字啱啱變：等多一輪睇吓定咗未（遊戲逐隻字出嗰陣唔好次次都譯）；間隔長過 1.5 秒就唔使等
                        else if (key != pending && IntervalMs < 1500) pending = key;
                        else
                        {
                            pending = "";
                            if (!cache.TryGetValue(key, out var tr))
                            {
                                Ui_(() => _result?.Busy(true));
                                tr = await TranslateAsync(text, ct);
                                if (cache.Count > 300) cache.Clear();
                                cache[key] = tr;
                            }
                            if (ct.IsCancellationRequested) break;
                            shown = key;
                            string outText = tr;
                            Ui_(() => _result?.SetText(outText, false));
                        }
                    }
                }
                fails = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                fails++;
                shown = ""; pending = ""; lastHash = 0;
                string msg = ex.Message;
                Ui_(() => _result?.SetText(msg, true));
                if (fails == 1) AppPaths.LogError("畫面翻譯", ex);
                if (fails >= 5)
                {
                    Ui_(() => { Stop(); Toast.Show(Lang.T("畫面翻譯已停止：") + msg, Ui.Danger, 5000); });
                    return;
                }
                try { await Task.Delay(1500, ct); } catch { break; }
            }
            int wait = IntervalMs - (int)sw.ElapsedMilliseconds;
            try { await Task.Delay(Math.Max(30, wait), ct); } catch { break; }
        }
    }

    // ---------------- 翻譯 ----------------
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static async Task<string> TranslateAsync(string text, CancellationToken ct)
    {
        var t = Targets[Target];
        text = Mail.MailAi.Clip(text, 3000);
        if (Engine == "ai")
        {
            string prompt = "Translate the text below into " + t.Prompt + ". It was read from a screen by OCR and may contain small recognition errors; fix obvious ones silently. " +
                            "Output ONLY the translation, keeping the line breaks, with no explanations and no quotes. If the text is already in the target language, output it unchanged.\n\n" + text;
            return await Mail.MailAi.AskAsync(prompt);
        }
        string key = Key.Trim();
        using var req = new HttpRequestMessage(HttpMethod.Post, "");
        object body;
        switch (Engine)
        {
            case "deepl":
                // 免費版金鑰以 :fx 結尾，用另一個網址
                req.RequestUri = new Uri(key.EndsWith(":fx") ? "https://api-free.deepl.com/v2/translate" : "https://api.deepl.com/v2/translate");
                req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + key);
                body = new { text = new[] { text }, target_lang = t.Deepl };
                break;
            case "google":
                req.RequestUri = new Uri("https://translation.googleapis.com/language/translate/v2");
                req.Headers.TryAddWithoutValidation("X-goog-api-key", key);
                body = new { q = new[] { text }, target = t.Google, format = "text" };
                break;
            default:
                req.RequestUri = new Uri("https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" + t.Azure);
                req.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
                if (Region.Trim().Length > 0) req.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", Region.Trim());
                body = new[] { new { Text = text } };
                break;
        }
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(Lang.F("翻譯服務回覆錯誤 {0}：請檢查金鑰同用量。", (int)resp.StatusCode) + " " + Mail.MailAi.Clip(Regex.Replace(json, @"\s+", " "), 160));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return Engine switch
        {
            "deepl" => root.GetProperty("translations")[0].GetProperty("text").GetString() ?? "",
            "google" => root.GetProperty("data").GetProperty("translations")[0].GetProperty("translatedText").GetString() ?? "",
            _ => root[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? ""
        };
    }

    // ---------------- 快速文字辨識 ----------------
    /// <summary>
    /// 一直開住嘅 PowerShell 文字辨識程序（Windows 內置 OCR）：唔使次次開新程序，讀一次只要零點幾秒，先做到 0.5 秒一次。
    /// 每行輸入「語言 TAB 圖片路徑（base64）」，輸出讀到嘅每行字，最後一行 &lt;&lt;END&gt;&gt;。
    /// </summary>
    private static class FastOcr
    {
        private const string Script = @"$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(-1) | Out-Null; $task.Result }
$engines = @{}
while ($true) {
  $line = [Console]::In.ReadLine()
  if ($line -eq $null) { break }
  try {
    $parts = $line.Split(""`t"")
    $lang = $parts[0]
    $path = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($parts[1].Trim()))
    if (-not $engines.ContainsKey($lang)) {
      if ($lang) { $engines[$lang] = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new($lang)) }
      else { $engines[$lang] = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
    }
    $engine = $engines[$lang]
    if (-not $engine) { [Console]::Out.WriteLine('<<ERR>>NOENGINE') }
    else {
      $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($path)) ([Windows.Storage.StorageFile])
      $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
      $dec = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
      $bmp = Await ($dec.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
      $res = Await ($engine.RecognizeAsync($bmp)) ([Windows.Media.Ocr.OcrResult])
      foreach ($l in $res.Lines) { [Console]::Out.WriteLine($l.Text) }
      $stream.Dispose()
    }
  } catch { [Console]::Out.WriteLine('<<ERR>>' + $_.Exception.Message) }
  [Console]::Out.WriteLine('<<END>>')
  [Console]::Out.Flush()
}
";
        private static readonly object Lock = new();
        private static Process? _p;
        private static bool _hooked;
        private static string Dir => Path.Combine(AppPaths.Data, "tools", "ocr");

        private static Process Start()
        {
            if (_p is { HasExited: false }) return _p;
            try { _p?.Dispose(); } catch { }
            Directory.CreateDirectory(Dir);
            var ps1 = Path.Combine(Dir, "ocr-live.ps1");
            File.WriteAllText(ps1, Script, new UTF8Encoding(true)); // PowerShell 5 要 BOM 先識讀 UTF-8
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var psi = new ProcessStartInfo(exe, $"-NoProfile -NonInteractive -File \"{ps1}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            Scripts.Win.AllowOwnScript(psi);
            _p = Process.Start(psi)!;
            _p.ErrorDataReceived += (_, _) => { };
            _p.BeginErrorReadLine();
            if (!_hooked) { _hooked = true; Application.ApplicationExit += (_, _) => Stop(); }
            return _p;
        }

        public static void Stop()
        {
            lock (Lock)
            {
                try { if (_p is { HasExited: false }) _p.Kill(); } catch { }
                try { _p?.Dispose(); } catch { }
                _p = null;
            }
        }

        public static string Recognize(Bitmap img, string lang, CancellationToken ct)
        {
            lock (Lock)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Dir);
                var path = Path.Combine(Dir, "live.bmp");
                // 細字放大先讀會準啲
                int k = img.Height < 80 ? 3 : img.Height < 400 ? 2 : 1;
                if (k == 1) img.Save(path, ImageFormat.Bmp);
                else
                    using (var big = new Bitmap(Math.Min(2600, img.Width * k), Math.Min(2600, img.Height * k), PixelFormat.Format24bppRgb))
                    {
                        using (var g = Graphics.FromImage(big)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(img, 0, 0, big.Width, big.Height); }
                        big.Save(path, ImageFormat.Bmp);
                    }
                var p = Start();
                p.StandardInput.WriteLine(lang + "\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(path)));
                p.StandardInput.Flush();
                var lines = new List<string>();
                var until = DateTime.Now.AddSeconds(20);
                string? err = null;
                while (true)
                {
                    var read = p.StandardOutput.ReadLineAsync();
                    if (!read.Wait(Math.Max(1, (int)(until - DateTime.Now).TotalMilliseconds)) || read.Result == null)
                    {
                        try { p.Kill(); } catch { }
                        throw new InvalidOperationException(Lang.T("文字辨識冇回應"));
                    }
                    var l = read.Result;
                    if (l == "<<END>>") break;
                    if (l.StartsWith("<<ERR>>")) err = l[7..];
                    else lines.Add(l);
                }
                if (err != null)
                    throw new InvalidOperationException(err == "NOENGINE" ? Lang.T("Windows 未裝呢種語言嘅文字辨識：Windows 設定 → 語言 → 加入語言。") : err);
                return string.Join("\n", lines);
            }
        }
    }

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

    /// <summary>框住翻譯範圍嘅幼框（滑鼠穿透，唔阻住你撳嘢）。</summary>
    private sealed class Frame : Form
    {
        public Frame(Rectangle r)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = Rectangle.Inflate(r, 3, 3);
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000 | 0x20 | 0x80000 | 0x8; return cp; } // 工具視窗、唔搶焦點、滑鼠穿透、layered、置頂
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); try { SetWindowDisplayAffinity(Handle, 0x11); } catch { } }
        protected override void OnPaint(PaintEventArgs e)
        {
            using var pen = new Pen(Ui.Accent, 2);
            e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }
    }

    /// <summary>
    /// 顯示譯文嘅視窗：成個視窗可以拖，四邊同角可以拉大細，位置同大細會記低；右撳有選單。
    /// 透明背景嗰陣透明嘅位置撳唔到，所以左上角有粒「拖動」、右下角有粒「拉大細」嘅把手。
    /// </summary>
    private sealed class Result : Form
    {
        private readonly Rectangle _region;
        private string _text = "";
        private bool _dim, _busy;
        private static readonly Color Key_ = Color.FromArgb(1, 1, 1);   // 透明色：近黑，字邊就唔會有彩色毛邊
        private int Grip => Ui.S(14);

        public Result(Rectangle r)
        {
            _region = r;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(Ui.S(120), Ui.S(36));
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            var menu = new ContextMenuStrip();
            menu.Items.Add(Lang.T("複製譯文"), null, (_, _) => { try { if (_text.Length > 0) Clipboard.SetText(_text); } catch { } });
            menu.Items.Add(Lang.T("放返喺框下面"), null, (_, _) => ResetBox());
            menu.Items.Add(Lang.T("停止翻譯"), null, (_, _) => Stop());
            ContextMenuStrip = menu;
            Restyle();
            if (!Saved.IsEmpty && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Saved))) Bounds = Saved;
            else PlaceDefault();
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000 | 0x8; return cp; } // 工具視窗、唔搶焦點、置頂
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); try { SetWindowDisplayAffinity(Handle, 0x11); } catch { } }

        public void Restyle()
        {
            if (IsDisposed) return;
            if (Back == 2) { BackColor = Key_; TransparencyKey = Key_; Opacity = 1; }
            else { TransparencyKey = Color.Empty; BackColor = Back == 1 ? Color.FromArgb(246, 246, 248) : Color.FromArgb(22, 24, 32); Opacity = ScreenTranslate.Opacity / 100.0; }
            Invalidate();
        }

        /// <summary>預設位置：框下面夠位就放下面，唔夠就放上面；闊度跟個框。</summary>
        public void PlaceDefault()
        {
            if (IsDisposed) return;
            var screen = Screen.FromRectangle(_region).WorkingArea;
            int w = Math.Max(_region.Width, Ui.S(320)), h = Ui.S(90);
            int x = Math.Clamp(_region.Left, screen.Left, Math.Max(screen.Left, screen.Right - w));
            int y = _region.Bottom + Ui.S(8) + h <= screen.Bottom ? _region.Bottom + Ui.S(8) : Math.Max(screen.Top, _region.Top - Ui.S(8) - h);
            Bounds = new Rectangle(x, y, w, h);
        }

        public void Busy(bool on) { if (!IsDisposed && _busy != on) { _busy = on; Invalidate(); } }

        public void SetText(string s, bool dim)
        {
            if (IsDisposed) return;
            _text = s; _dim = dim; _busy = false;
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, WM_EXITSIZEMOVE = 0x232, WM_NCRBUTTONUP = 0xA5;
            if (m.Msg == WM_NCHITTEST)
            {
                var p = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
                int e = Ui.S(7);
                bool l = p.X < e, r = p.X >= Width - e, t = p.Y < e, b = p.Y >= Height - e;
                // 右下角把手一定係拉大細（透明背景都撳到）
                if (p.X >= Width - Grip && p.Y >= Height - Grip) { m.Result = (IntPtr)17; return; }
                m.Result = (IntPtr)(t && l ? 13 : t && r ? 14 : b && l ? 16 : b && r ? 17 : l ? 10 : r ? 11 : t ? 12 : b ? 15 : 2); // 2 = 標題列（拖動）
                return;
            }
            if (m.Msg == WM_NCRBUTTONUP) { ContextMenuStrip?.Show(Cursor.Position); return; }
            base.WndProc(ref m);
            if (m.Msg == WM_EXITSIZEMOVE) { Saved = Bounds; Save(); }   // 用家擺好咗：記低
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            bool clear = Back == 2;
            int pad = Ui.S(10);
            var area = new RectangleF(pad, Ui.S(6), Width - pad * 2, Height - Ui.S(12));
            if (clear)
            {
                // 把手（實色，透明背景都撳到）同淡淡嘅外框
                using var gb = new SolidBrush(Color.FromArgb(70, 76, 96));
                g.FillRectangle(gb, 0, 0, Grip, Grip);
                g.FillRectangle(gb, Width - Grip, Height - Grip, Grip, Grip);
                area = new RectangleF(pad + Grip, Ui.S(6), Width - pad * 2 - Grip, Height - Ui.S(12));
            }
            string s = _text.Length == 0 && _dim ? Lang.T("（呢個位置冇字）") : _text;
            if (s.Length == 0) return;
            Color fore = Back == 1 ? Color.FromArgb(20, 22, 28) : Color.White;
            if (_dim || _busy) fore = Back == 1 ? Color.FromArgb(110, 115, 130) : Color.FromArgb(170, 175, 190);
            var font = Ui.F(FontSize);
            using var fmt = new StringFormat(StringFormatFlags.NoClip) { Trimming = StringTrimming.EllipsisWord };
            fmt.FormatFlags &= ~StringFormatFlags.NoWrap;
            if (!clear)
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using var b = new SolidBrush(fore);
                g.DrawString(s, font, b, area, fmt);
                return;
            }
            // 透明背景：字加黑邊，喺任何畫面上面都睇得清
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            path.AddString(s, font.FontFamily, (int)font.Style, g.DpiY * font.SizeInPoints / 72f, area, fmt);
            using var pen = new Pen(Color.Black, Math.Max(3f, FontSize / 3.2f)) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
            using var fill = new SolidBrush(fore);
            g.FillPath(fill, path);
        }
    }
}

internal sealed class ScreenTranslatePage : ToolPage
{
    private readonly FancyButton _go, _langBtn;
    private readonly Label _status;
    private static readonly int[] Intervals = { 500, 1000, 2000, 3000, 5000 };

    public static Control Build() => new ScreenTranslatePage();

    private ScreenTranslatePage()
    {
        ScreenTranslate.Load();
        _go = TUi.Btn("", ScreenTranslate.Toggle, 220, BtnKind.Primary);
        _status = TUi.L("");
        _langBtn = TUi.Btn("", ShowLangMenu, 220);
        var target = TIn.Combo(190, ScreenTranslate.Targets.Select(t => t.Name), ScreenTranslate.Target);
        target.SelectedIndexChanged += (_, _) => { ScreenTranslate.Target = target.SelectedIndex; ScreenTranslate.Save(); };
        var interval = TIn.Combo(120, Intervals.Select(ms => Lang.F("每 {0} 秒", (ms / 1000.0).ToString("0.#"))), Math.Max(0, Array.IndexOf(Intervals, ScreenTranslate.IntervalMs)));
        interval.SelectedIndexChanged += (_, _) => { ScreenTranslate.IntervalMs = Intervals[interval.SelectedIndex]; ScreenTranslate.Save(); };
        Card(Lang.T("畫面即時翻譯"),
            Lang.T("喺畫面上面畫一個框（遊戲、影片、網頁、圖片都得），框入面嘅字一變就自動翻譯。讀字用 Windows 內置嘅文字辨識（唔使上網）。畫面冇變就唔會重複讀，所以揀 0.5 秒都唔會好食資源。"),
            TUi.Col(
                TUi.Row(_go, _status),
                TUi.Row(TUi.L(Lang.T("原文語言")), _langBtn, TUi.L(Lang.T("譯做")), target),
                TUi.Row(TUi.L(Lang.T("幾耐睇一次")), interval, TUi.L(Lang.T("開始 / 停止快捷鍵")), HotkeyHub.Box("translate.toggle", 140)),
                TUi.L(Lang.T("提示：原文語言要揀啱（例如日文遊戲揀日文），Windows 未裝嗰種語言就讀唔到。用獨佔全螢幕嘅遊戲請改用無邊框視窗模式。"))));

        // ---- 用乜翻譯 ----
        var engine = TIn.Combo(260, new[] { Lang.T("AI（用「AI 助手」揀咗嗰個）"), "DeepL", "Google Cloud Translation", "Microsoft Translator" }, Math.Max(0, Array.IndexOf(ScreenTranslate.Engines, ScreenTranslate.Engine)));
        var key = TIn.Box(ScreenTranslate.Key, 300, "API key");
        key.UseSystemPasswordChar = true;
        var region = TIn.Box(ScreenTranslate.Region, 130, Lang.T("地區（例如 eastasia）"));
        var keyRow = TUi.Row(TUi.L(Lang.T("金鑰")), key, region);
        var note = TUi.L("");
        void SyncEngine()
        {
            bool ai = engine.SelectedIndex == 0;
            keyRow.Visible = !ai;
            region.Visible = engine.SelectedIndex == 3;
            note.Text = ai ? Lang.T("用 AI 翻譯：唔使金鑰，但每句要等 AI 回覆，會慢啲；用本地 AI 就完全唔使上網。")
                           : Lang.T("用翻譯服務：唔經 AI，通常零點幾秒就有結果。要用你自己喺嗰間服務申請嘅金鑰（各間都有免費額度）；金鑰加密後只存喺你部電腦，框入面嘅文字會傳去嗰間服務翻譯。");
            Relayout();
        }
        engine.SelectedIndexChanged += (_, _) => { ScreenTranslate.Engine = ScreenTranslate.Engines[engine.SelectedIndex]; ScreenTranslate.Save(); SyncEngine(); };
        key.TextChanged += (_, _) => { ScreenTranslate.Key = key.Text; ScreenTranslate.Save(); };
        region.TextChanged += (_, _) => { ScreenTranslate.Region = region.Text; ScreenTranslate.Save(); };
        Card(Lang.T("用乜翻譯"), null, TUi.Col(TUi.Row(engine), keyRow, note));

        // ---- 譯文視窗 ----
        var font = new NumberBox(Ui.S(84)) { Minimum = 8, Maximum = 48, Value = ScreenTranslate.FontSize, Margin = new Padding(0, Ui.S(2), Ui.S(16), 0) };
        font.ValueChanged += (_, _) => { ScreenTranslate.FontSize = (int)font.Value; ScreenTranslate.Save(); ScreenTranslate.RestyleResult(); };
        var back = TIn.Combo(130, new[] { Lang.T("深色背景"), Lang.T("淺色背景"), Lang.T("透明背景") }, ScreenTranslate.Back);
        var opacity = new NumberBox(Ui.S(84)) { Minimum = 30, Maximum = 100, Increment = 5, Value = ScreenTranslate.Opacity, Margin = new Padding(0, Ui.S(2), Ui.S(16), 0) };
        opacity.Enabled = ScreenTranslate.Back != 2;
        back.SelectedIndexChanged += (_, _) => { ScreenTranslate.Back = back.SelectedIndex; opacity.Enabled = back.SelectedIndex != 2; ScreenTranslate.Save(); ScreenTranslate.RestyleResult(); };
        opacity.ValueChanged += (_, _) => { ScreenTranslate.Opacity = (int)opacity.Value; ScreenTranslate.Save(); ScreenTranslate.RestyleResult(); };
        Card(Lang.T("譯文視窗"), Lang.T("開始翻譯之後，譯文視窗可以直接拖去你想要嘅位置，拉四邊或者角改大細，會記住；右撳有選單。透明背景嗰陣，拖左上角嗰粒搬位、拖右下角嗰粒改大細。"),
            TUi.Col(
                TUi.Row(TUi.L(Lang.T("文字大細")), font, TUi.L(Lang.T("背景")), back, TUi.L(Lang.T("不透明度 %")), opacity),
                TUi.Row(TUi.Btn(Lang.T("重設位置（放返喺框下面）"), ScreenTranslate.ResetBox))));

        ScreenTranslate.StateChanged += Sync;
        Disposed += (_, _) => ScreenTranslate.StateChanged -= Sync;
        UpdateLang();
        SyncEngine();
        Sync();
    }

    private void Sync()
    {
        if (IsDisposed) return;
        _go.Text = ScreenTranslate.Running ? Lang.T("■ 停止翻譯") : Lang.T("▣ 畫框開始翻譯");
        _go.Kind = ScreenTranslate.Running ? BtnKind.Danger : BtnKind.Primary;
        _go.Invalidate();
        _status.Text = ScreenTranslate.Running ? Lang.T("翻譯緊…") : "";
    }

    private void UpdateLang()
    {
        _langBtn.Text = (ScreenTranslate.SourceLang.Length == 0 ? Lang.T("自動（跟 Windows 語言）") : ScreenTranslate.SourceLang) + " ▾";
        _langBtn.Invalidate();
    }

    private async void ShowLangMenu()
    {
        _status.Text = Lang.T("讀取語言…");
        List<(string Tag, string Name)> langs;
        try { langs = await Ocr.LanguagesAsync(); }
        catch (Exception ex) { _status.Text = ex.Message; return; }
        _status.Text = "";
        var items = new List<(string, string)> { (Lang.T("自動（跟 Windows 語言）"), "") };
        items.AddRange(langs.Select(l => (l.Name + "  (" + l.Tag + ")", l.Tag)));
        TUi.Pick(_langBtn, items, ScreenTranslate.SourceLang, v => { ScreenTranslate.SourceLang = v; ScreenTranslate.Save(); UpdateLang(); },
            Lang.T("冇你要嘅語言：Windows 設定 → 語言 → 加入語言"));
    }
}
