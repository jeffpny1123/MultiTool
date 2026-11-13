using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Ai;

/// <summary>
/// 本地 AI 製圖：stable-diffusion.cpp（sd-cli）+ 免費模型，喺你部機畫圖，唔使帳號、唔使錢。
/// 模型同程式都係用家自己由網上下載（唔會包喺拉比工具箱入面）；有顯示卡會用 Vulkan 加速，唔夠位就借用記憶體。
/// </summary>
internal static class LocalImage
{
    /// <summary>一個要下載嘅檔：邊個 repo、點揀檔（Hugging Face 檔名會變，所以用規則搵）。</summary>
    public sealed record Part(string Key, string Repo, Func<string, bool> Match, string[] Prefer);

    /// <summary>製圖模型：三個檔（畫圖模型、文字理解、VAE）+ 點樣行。</summary>
    public sealed record Model(string Key, string Label, string Size, string Note, int NeedGb, int MinGb, Part[] Parts, Func<Model, string, int, int, string, string> Args)
    {
        /// <summary>顯示卡記憶體（GB）：少過 MinVram = 未達最低要求；NeedVram = 建議。</summary>
        public int MinVram { get; init; } = 4;
        public int NeedVram { get; init; } = 8;
    }

    private static bool Gguf(string f) => f.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && !f.Contains("mmproj", StringComparison.OrdinalIgnoreCase) && !f.Contains('/');
    private static readonly string[] Q4 = { "Q4_K_M", "Q4_K", "Q4_0" };

    public static readonly Model[] Models =
    {
        new("zimage-turbo", "Z-Image Turbo", "≈ 6 GB", "快，8 步出圖；Apache 2.0 授權，畫出嚟嘅圖可以商用", 16, 8,
            new[]
            {
                new Part("diffusion", "leejet/Z-Image-Turbo-GGUF", Gguf, Q4),
                new Part("llm", "unsloth/Qwen3-4B-Instruct-2507-GGUF", Gguf, Q4),
                new Part("vae", "Comfy-Org/z_image_turbo", f => f.Contains("vae/", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase), new[] { "ae.safetensors" }),
            },
            (m, prompt, w, h, outFile) => Common(m, prompt, w, h, outFile) + " --cfg-scale 1.0 --steps 8 --diffusion-fa"),
        new("qwenimage21-turbo", "Qwen-Image-2.1 Turbo", "≈ 10 GB", "畫質最好嘅免費模型，字都寫得準；只限非商業用途（Qwen Research License）", 24, 16,
            new[]
            {
                new Part("diffusion", "unsloth/Qwen-Image-2.1-Turbo-GGUF", Gguf, Q4),
                new Part("llm", "Qwen/Qwen3-VL-8B-Instruct-GGUF", Gguf, Q4),
                new Part("vae", "Comfy-Org/Qwen-Image-2.1", f => f.Contains("vae/", StringComparison.OrdinalIgnoreCase) && f.Contains("qwen_image_2.1_vae", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase), new[] { "bf16" }),
            },
            (m, prompt, w, h, outFile) => Common(m, prompt, w, h, outFile) + " --sigmas qwen-image-2.1-turbo --steps 8 --cfg-scale 1.0 --sampling-method euler") { MinVram = 6, NeedVram = 12 },
    };

    // stable-diffusion.cpp（MIT）：固定一個已經支援 Z-Image 同 Qwen-Image-2.1 Turbo 嘅版本
    private const string BinTag = "master-1047-984eb06";
    private const string BinUrl = "https://github.com/leejet/stable-diffusion.cpp/releases/download/" + BinTag + "/sd-master-984eb06-bin-win-vulkan-x64.zip";

    public static string Dir => Path.Combine(AiProvider.WorkDir, "image");
    internal static string BinDir => Path.Combine(Dir, "bin");
    internal static string Exe => Path.Combine(BinDir, "sd-cli.exe");
    private static string BinVerFile => Path.Combine(BinDir, "version.txt");
    private static string ModelDir(Model m) => Path.Combine(Dir, m.Key);
    private static string PartFile(Model m, Part p) => Path.Combine(ModelDir(m), p.Key + Path.GetExtension(p.Key == "vae" ? ".safetensors" : ".gguf"));
    /// <summary>畫好嘅圖：圖片\RabiToolkit\AI</summary>
    public static string OutDir => Path.Combine(Program.BrandFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)), "AI");

    public static bool BinReady
    {
        get { try { return File.Exists(Exe) && File.Exists(BinVerFile) && File.ReadAllText(BinVerFile).Trim() == BinTag; } catch { return false; } }
    }
    public static bool ModelReady(Model m) => m.Parts.All(p => File.Exists(PartFile(m, p)));
    public static Model[] Ready => BinReady ? Models.Where(ModelReady).ToArray() : Array.Empty<Model>();

    /// <summary>部機夠唔夠（記憶體 + 顯示卡）：Level 0 = 夠、1 = 可能唔夠、2 = 未達最低要求（紅字，下載前再確認）。</summary>
    public static (int Level, string Text) Fit(Model m) => LocalAi.Check(m.MinGb, m.NeedGb, m.MinVram, m.NeedVram);

    internal static string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
    private static string Common(Model m, string prompt, int w, int h, string outFile)
    {
        string F(string key) => PartFile(m, m.Parts.First(p => p.Key == key));
        // --offload-to-cpu：顯示卡記憶體唔夠就放啲去記憶體（12 GB 卡都行到）
        return $"--diffusion-model {Q(F("diffusion"))} --vae {Q(F("vae"))} --llm {Q(F("llm"))} -p {Q(prompt)} -W {w} -H {h} --seed -1 --offload-to-cpu -o {Q(outFile)}";
    }

    private static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(90) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(Program.FileName);
        return c;
    }

    internal static async Task DownloadAsync(string url, string dest, Action<long, long> progress, CancellationToken ct)
    {
        string tmp = dest + ".part";
        using (var http = NewClient())
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if ((int)resp.StatusCode is 401 or 403) throw new InvalidOperationException(Lang.T("呢個模型要登入 Hugging Face 先可以下載。"));
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress(done, total);
            }
        }
        File.Move(tmp, dest, true);
    }

    /// <summary>問 Hugging Face 個 repo 有咩檔，按規則揀一個（優先 Q4_K_M 等）。</summary>
    private static Task<string> ResolveAsync(Part p, CancellationToken ct) => ResolveAsync(p.Repo, p.Match, p.Prefer, ct);
    internal static async Task<string> ResolveAsync(string repo, Func<string, bool> match, string[] prefer, CancellationToken ct)
    {
        using var http = NewClient();
        string json = await http.GetStringAsync($"https://huggingface.co/api/models/{repo}", ct);
        using var doc = JsonDocument.Parse(json);
        var files = doc.RootElement.GetProperty("siblings").EnumerateArray()
            .Select(s => s.GetProperty("rfilename").GetString() ?? "").Where(match).ToList();
        foreach (var pref in prefer)
        {
            var hit = files.FirstOrDefault(f => Path.GetFileName(f).Contains(pref, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return files.FirstOrDefault() ?? throw new InvalidOperationException(Lang.T("搵唔到模型檔案。"));
    }

    /// <summary>一 click 安裝：未有程式就先下載 stable-diffusion.cpp，再逐個下載模型檔。</summary>
    public static async Task InstallAsync(Model m, Action<string, double> progress, CancellationToken ct)
    {
        await EnsureBinAsync(progress, ct);
        Directory.CreateDirectory(ModelDir(m));
        int i = 0;
        foreach (var p in m.Parts)
        {
            i++;
            if (File.Exists(PartFile(m, p))) continue;
            progress(Lang.T("搵緊模型…"), -1);
            string file = await ResolveAsync(p, ct);
            await DownloadAsync($"https://huggingface.co/{p.Repo}/resolve/main/{file}", PartFile(m, p),
                (d, t) => progress(Lang.F("下載模型 {0}（{1}/{2}）…", m.Label, i, m.Parts.Length), t > 0 ? (double)d / t : -1), ct);
        }
        progress(Lang.T("完成"), 1);
    }

    /// <summary>未有（或者唔係指定版本嘅）stable-diffusion.cpp 就下載；製圖同製影片共用。</summary>
    internal static async Task EnsureBinAsync(Action<string, double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        if (!BinReady)
        {
            string zip = Path.Combine(Dir, "sd-bin.zip");
            await DownloadAsync(BinUrl, zip, (d, t) => progress(Lang.T("下載製圖程式…"), t > 0 ? (double)d / t : -1), ct);
            if (Directory.Exists(BinDir)) try { Directory.Delete(BinDir, true); } catch { }
            Directory.CreateDirectory(BinDir);
            using (var z = ZipFile.OpenRead(zip))
                foreach (var e in z.Entries)
                {
                    if (e.Name.Length == 0 || e.FullName.Contains("..")) continue;
                    if (!e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && e.Name != "sd-cli.exe") continue;
                    e.ExtractToFile(Path.Combine(BinDir, e.Name), true);
                }
            File.Delete(zip);
            File.WriteAllText(BinVerFile, BinTag);
        }
    }

    public static void Delete(Model m)
    {
        try { if (Directory.Exists(ModelDir(m))) Directory.Delete(ModelDir(m), true); } catch (Exception ex) { AppPaths.LogError("刪除製圖模型", ex); }
    }

    private static Process? _running;
    public static void Stop() { try { if (_running is { HasExited: false }) _running.Kill(true); } catch { } }

    /// <summary>畫一張圖，回傳 PNG 路徑。progress(第幾步, 總步數, 狀態字)。</summary>
    public static async Task<string> GenerateAsync(Model m, string prompt, int w, int h, Action<int, int, string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(OutDir);
        string outFile = Path.Combine(OutDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
        await RunAsync(m.Args(m, prompt.Replace("\r", " ").Replace("\n", " "), w, h, outFile), outFile, "AI 製圖", progress, ct);
        return outFile;
    }

    /// <summary>行 sd-cli，讀進度；行完冇出到 outFile 就當失敗（製圖同製影片共用）。</summary>
    internal static async Task RunAsync(string args, string outFile, string logName, Action<int, int, string> progress, CancellationToken ct, string? failText = null)
    {
        var psi = new ProcessStartInfo(Exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = BinDir,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException(Lang.T("開唔到製圖程式"));
        _running = p;
        ChildJob.Add(p);
        var tail = new StringBuilder();
        var stepRx = new Regex(@"\|\s*(\d+)/(\d+)\s*-");
        // 進度用 \r 刷新，所以自己逐個字讀
        async Task Read(StreamReader r)
        {
            var sb = new StringBuilder();
            var buf = new char[512];
            int n;
            while ((n = await r.ReadAsync(buf, 0, buf.Length)) > 0)
                for (int i = 0; i < n; i++)
                {
                    char c = buf[i];
                    if (c != '\r' && c != '\n') { sb.Append(c); continue; }
                    if (sb.Length == 0) continue;
                    string line = sb.ToString(); sb.Clear();
                    lock (tail) { tail.AppendLine(line); if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000); }
                    var mm = stepRx.Match(line);
                    if (mm.Success) progress(int.Parse(mm.Groups[1].Value), int.Parse(mm.Groups[2].Value), "");
                    else if (line.Contains("loading", StringComparison.OrdinalIgnoreCase)) progress(0, 0, Lang.T("載入模型…"));
                    else if (line.Contains("decod", StringComparison.OrdinalIgnoreCase)) progress(0, 0, Lang.T("完成緊…"));
                }
        }
        using var reg = ct.Register(Stop);
        await Task.WhenAll(Read(p.StandardOutput), Read(p.StandardError), p.WaitForExitAsync());
        _running = null;
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(outFile))
        {
            string err;
            lock (tail) err = tail.ToString().Trim();
            AppPaths.LogError(logName, new Exception(err));
            var lastLines = string.Join("\n", err.Split('\n').TakeLast(3));
            throw new InvalidOperationException((failText ?? Lang.T("畫唔到：")) + (lastLines.Length > 300 ? lastLines[^300..] : lastLines));
        }
    }
}

/// <summary>「製圖模型」小視窗：揀模型，一 click 下載。</summary>
internal sealed class ImageModelDialog : Form
{
    public event Action? Changed;
    private CancellationTokenSource? _cts;

    public ImageModelDialog()
    {
        Text = Lang.T("製圖模型");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.F(9.75f);
        int rowH = Ui.S(84);
        double ram = LocalAi.RamGb;
        ClientSize = new Size(Ui.S(500), Ui.S(88) + rowH * LocalImage.Models.Length + Ui.S(8));
        FormClosing += (_, _) => _cts?.Cancel();
        Controls.Add(new Label
        {
            Text = Lang.T("免費模型，喺你部機自己畫圖：唔使帳號、唔使錢，下載完唔使上網。有顯示卡會自動加速；各模型有自己嘅授權，請睇清楚。") +
                   "\n" + LocalAi.SpecText(),
            ForeColor = Ui.TextDim, Font = Ui.F(9f), AutoSize = false, Bounds = new Rectangle(Ui.S(16), Ui.S(10), Ui.S(468), Ui.S(70))
        });
        int y = Ui.S(84);
        var rows = new List<Action>();
        foreach (var m in LocalImage.Models)
        {
            var (fit, fitText) = LocalImage.Fit(m);
            var panel = new Panel { BackColor = Ui.CardHi, Bounds = new Rectangle(Ui.S(12), y, Ui.S(476), rowH - Ui.S(8)) };
            var name = new Label { Text = $"{m.Label}　{m.Size}", Font = Ui.F(10.5f, FontStyle.Bold), ForeColor = Ui.Text, AutoSize = true, Location = new Point(Ui.S(12), Ui.S(8)) };
            var need = new Label { Text = Lang.F("建議 {0} GB RAM", m.NeedGb) + " · " + Lang.F("顯示卡 {0} GB", m.NeedVram), Font = Ui.F(8.75f, FontStyle.Bold), AutoSize = true,
                ForeColor = fit == 2 ? Ui.Danger : fit == 1 ? Ui.Hex(0xE0A030) : Ui.TextDim };
            name.SizeChanged += (_, _) => need.Location = new Point(name.Right + Ui.S(6), Ui.S(11));
            var note = new Label { Text = Lang.T(m.Note), Font = Ui.F(8.75f), ForeColor = Ui.TextFaint, AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(Ui.S(12), Ui.S(30), Ui.S(345), Ui.S(18)) };
            new ToolTip().SetToolTip(note, Lang.T(m.Note));
            var state = new Label { Font = Ui.F(8.75f), AutoSize = false, Bounds = new Rectangle(Ui.S(12), Ui.S(50), Ui.S(340), Ui.S(18)) };
            var bar = new ProgressBar { Bounds = new Rectangle(Ui.S(12), Ui.S(68), Ui.S(452), Ui.S(6)), Visible = false };
            var main = new FancyButton { Kind = BtnKind.Primary, Font = Ui.F(9.5f), Bounds = new Rectangle(Ui.S(366), Ui.S(10), Ui.S(98), Ui.S(32)) };
            var del = new FancyButton { Text = Lang.T("刪除"), Kind = BtnKind.Ghost, Font = Ui.F(8.75f), Bounds = new Rectangle(Ui.S(366), Ui.S(46), Ui.S(98), Ui.S(22)) };
            panel.Controls.AddRange(new Control[] { name, need, note, state, bar, main, del });
            need.Location = new Point(name.Right + Ui.S(6), Ui.S(11));
            Controls.Add(panel);

            void Refresh()
            {
                bool ready = LocalImage.BinReady && LocalImage.ModelReady(m);
                state.Text = ready ? Lang.T("✓ 已下載") : Lang.T("未下載");
                state.ForeColor = ready ? Ui.Success : Ui.TextFaint;
                if (!ready && fit == 1) { state.Text = fitText; state.ForeColor = Ui.Hex(0xE0A030); }
                if (!ready && fit == 2) { state.Text = "⚠ " + fitText; state.ForeColor = Ui.Danger; }
                // 未達最低要求：成行紅字，仍然可以撳下載（會再警告）
                if (fit == 2) { name.ForeColor = Ui.Danger; main.Kind = ready ? BtnKind.Primary : BtnKind.Danger; }
                main.Text = ready ? Lang.T("✓ 已下載") : Lang.T("下載");
                main.Enabled = !ready && _cts == null;
                del.Visible = ready && _cts == null;
                main.Invalidate();
            }
            rows.Add(Refresh);

            main.Click += async (_, _) =>
            {
                // 未達最低要求：再提醒一次（警告 + 免責聲明），用家堅持先下載
                if (fit == 2 && !LocalAi.ConfirmWeak(this, m.Label, fitText)) return;
                _cts = new CancellationTokenSource();
                rows.ForEach(r => r());
                bar.Visible = true;
                bool ok = false;
                try
                {
                    await LocalImage.InstallAsync(m, (text, frac) => BeginInvoke(() =>
                    {
                        if (IsDisposed) return;
                        state.ForeColor = Ui.TextDim;
                        state.Text = text + (frac >= 0 ? $" {frac * 100:0}%" : "");
                        bar.Style = frac >= 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                        if (frac >= 0) bar.Value = (int)Math.Clamp(frac * 100, 0, 100);
                    }), _cts.Token);
                    ok = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppPaths.LogError("下載製圖模型", ex);
                    if (!IsDisposed) MessageBox.Show(this, Lang.T("下載失敗：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                _cts = null;
                if (IsDisposed) return;
                bar.Visible = false;
                if (ok) Changed?.Invoke();
                rows.ForEach(r => r());
            };
            del.Click += (_, _) =>
            {
                if (MessageBox.Show(this, Lang.F("刪除 {0}？", m.Label), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                LocalImage.Delete(m);
                Changed?.Invoke();
                rows.ForEach(r => r());
            };
            Refresh();
            y += rowH;
        }
    }
}

/// <summary>「🎨 AI 製圖」視窗：輸入描述 → 喺你部機畫圖。</summary>
internal sealed class ImageGenDialog : Form
{
    private readonly TextBox _prompt = new();
    private readonly ComboBox _model = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _size = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly FancyButton _go, _models, _save, _folder, _copy;
    private readonly Label _status;
    private readonly ProgressBar _bar = new() { Visible = false };
    private readonly PictureBox _pic = new() { SizeMode = PictureBoxSizeMode.Zoom };
    private CancellationTokenSource? _cts;
    private string? _last;
    private static readonly (string Label, int W, int H)[] Sizes =
    {
        ("1024 × 1024（正方）", 1024, 1024), ("768 × 1344（直）", 768, 1344), ("1344 × 768（橫）", 1344, 768), ("512 × 512（快）", 512, 512)
    };

    public ImageGenDialog(string prompt)
    {
        Text = Lang.T("🎨 AI 製圖");
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.F(9.75f);
        ClientSize = new Size(Ui.S(760), Ui.S(640));
        MinimumSize = new Size(Ui.S(560), Ui.S(480));

        _prompt.Multiline = true; _prompt.Text = prompt; _prompt.BorderStyle = BorderStyle.FixedSingle;
        _prompt.BackColor = Ui.Input; _prompt.ForeColor = Ui.Text; _prompt.Font = Ui.F(10.5f); _prompt.ScrollBars = ScrollBars.Vertical;
        _prompt.PlaceholderText = Lang.T("描述你想要嘅圖，例如：一隻戴住耳機嘅白兔，坐喺霓虹燈城市屋頂，動漫風格");
        foreach (var c in new[] { _model, _size }) { c.BackColor = Ui.Input; c.ForeColor = Ui.Text; c.FlatStyle = FlatStyle.Flat; c.Font = Ui.F(9.75f); }
        foreach (var s in Sizes) _size.Items.Add(Lang.T(s.Label));
        _size.SelectedIndex = 0;
        _go = new FancyButton { Text = Lang.T("🎨 畫圖"), Kind = BtnKind.Primary, Font = Ui.F(10f, FontStyle.Bold) };
        _models = new FancyButton { Text = Lang.T("製圖模型…"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f) };
        _save = new FancyButton { Text = Lang.T("另存新檔…"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Enabled = false };
        _copy = new FancyButton { Text = Lang.T("複製"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Enabled = false };
        _folder = new FancyButton { Text = Lang.T("開啟資料夾"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f) };
        _status = new Label { ForeColor = Ui.TextDim, Font = Ui.F(9f), AutoSize = false, AutoEllipsis = true };
        _pic.BackColor = Ui.Bg;
        Controls.AddRange(new Control[] { _prompt, _model, _size, _go, _models, _status, _bar, _pic, _save, _copy, _folder });

        Resize += (_, _) => Place();
        Place();
        FillModels();
        _go.Click += async (_, _) => await GoAsync();
        _models.Click += (_, _) => { using var d = new ImageModelDialog(); d.Changed += FillModels; d.ShowDialog(this); FillModels(); };
        _folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(LocalImage.OutDir);
            try { Process.Start(new ProcessStartInfo("explorer.exe", _last != null ? "/select,\"" + _last + "\"" : "\"" + LocalImage.OutDir + "\"") { UseShellExecute = true }); } catch { }
        };
        _save.Click += (_, _) =>
        {
            if (_last == null) return;
            using var d = new SaveFileDialog { Filter = "PNG|*.png", FileName = Path.GetFileName(_last) };
            if (d.ShowDialog(this) == DialogResult.OK) try { File.Copy(_last, d.FileName, true); } catch (Exception ex) { MessageBox.Show(this, ex.Message, Program.AppName); }
        };
        _copy.Click += (_, _) => { if (_pic.Image != null) try { Clipboard.SetImage(_pic.Image); Tools.Toast.Show(Lang.T("已複製圖片"), Ui.Success, 2000); } catch { } };
        FormClosing += (_, _) => { _cts?.Cancel(); LocalImage.Stop(); };
    }

    private void Place()
    {
        int p = Ui.S(14), w = ClientSize.Width - p * 2;
        _prompt.SetBounds(p, p, w, Ui.S(70));
        int y = _prompt.Bottom + Ui.S(10);
        _model.SetBounds(p, y + Ui.S(3), Ui.S(220), Ui.S(28));
        _size.SetBounds(_model.Right + Ui.S(8), y + Ui.S(3), Ui.S(180), Ui.S(28));
        _models.SetBounds(_size.Right + Ui.S(8), y, Ui.S(110), Ui.S(34));
        _go.SetBounds(ClientSize.Width - p - Ui.S(120), y, Ui.S(120), Ui.S(34));
        y += Ui.S(42);
        _status.SetBounds(p, y, w, Ui.S(20));
        _bar.SetBounds(p, y + Ui.S(22), w, Ui.S(6));
        y += Ui.S(34);
        int by = ClientSize.Height - p - Ui.S(32);
        _pic.SetBounds(p, y, w, by - y - Ui.S(10));
        _save.SetBounds(p, by, Ui.S(120), Ui.S(32));
        _copy.SetBounds(_save.Right + Ui.S(8), by, Ui.S(90), Ui.S(32));
        _folder.SetBounds(_copy.Right + Ui.S(8), by, Ui.S(120), Ui.S(32));
    }

    private void FillModels()
    {
        var ready = LocalImage.Ready;
        string? cur = (_model.SelectedItem as ModelItem)?.M.Key;
        _model.Items.Clear();
        foreach (var m in ready) _model.Items.Add(new ModelItem(m));
        if (_model.Items.Count > 0)
            _model.SelectedIndex = Math.Max(0, ready.ToList().FindIndex(m => m.Key == cur));
        _status.Text = ready.Length == 0 ? Lang.T("未有製圖模型：撳「製圖模型…」免費下載一個。") : "";
        _status.ForeColor = Ui.TextDim;
        _go.Enabled = ready.Length > 0 || _cts != null;
        _go.Invalidate();
    }

    private sealed record ModelItem(LocalImage.Model M) { public override string ToString() => M.Label; }

    private async Task GoAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; } // 畫緊：撳多次 = 停
        if (_model.SelectedItem is not ModelItem mi) return;
        string prompt = _prompt.Text.Trim();
        if (prompt.Length == 0) { _status.Text = Lang.T("請先輸入描述。"); return; }
        var (_, w, h) = Sizes[Math.Max(0, _size.SelectedIndex)];
        _cts = new CancellationTokenSource();
        _go.Text = Lang.T("停止"); _go.Kind = BtnKind.Danger; _go.Invalidate();
        _bar.Visible = true; _bar.Style = ProgressBarStyle.Marquee;
        _status.ForeColor = Ui.TextDim; _status.Text = Lang.T("準備緊…（第一次要載入模型，會慢啲）");
        var sw = Stopwatch.StartNew();
        try
        {
            string file = await LocalImage.GenerateAsync(mi.M, prompt, w, h, (step, total, text) => BeginInvoke(() =>
            {
                if (IsDisposed) return;
                if (total > 0)
                {
                    _bar.Style = ProgressBarStyle.Continuous;
                    _bar.Value = Math.Clamp(step * 100 / total, 0, 100);
                    _status.Text = Lang.F("畫緊… 第 {0} / {1} 步", step, total);
                }
                else if (text.Length > 0) _status.Text = text;
            }), _cts.Token);
            _last = file;
            using (var fs = File.OpenRead(file)) { var old = _pic.Image; _pic.Image = Image.FromStream(fs); old?.Dispose(); }
            _status.ForeColor = Ui.Success;
            _status.Text = Lang.F("✓ 畫好咗（{0:0} 秒）：{1}", sw.Elapsed.TotalSeconds, file);
            _save.Enabled = _copy.Enabled = true; _save.Invalidate(); _copy.Invalidate();
        }
        catch (OperationCanceledException) { _status.Text = Lang.T("已停止。"); }
        catch (Exception ex) { _status.ForeColor = Ui.Danger; _status.Text = ex.Message; }
        finally
        {
            _cts = null;
            if (!IsDisposed)
            {
                _bar.Visible = false;
                _go.Text = Lang.T("🎨 畫圖"); _go.Kind = BtnKind.Primary; _go.Invalidate();
            }
        }
    }
}
