using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Ai;

/// <summary>
/// 本地 AI：llama.cpp（llama-server）+ 免費開源模型，全部喺你部機行，唔使帳號、唔使上網（下載完之後）。
/// 有顯示卡會用 Vulkan 加速，冇就用 CPU。
/// </summary>
internal static class LocalAi
{
    /// <summary>NeedGb = 建議嘅電腦記憶體（RAM）；MinGb = 少過呢個就唔俾下載（一定行唔到）。</summary>
    /// <remarks>Hidden = 已經唔再提供（太弱），只係下載咗嘅舊用家先見到，可以繼續用或者刪除。</remarks>
    /// <remarks>Vision = 模型本身識睇圖片（要另外下載「圖片識別組件」mmproj 先用到）。</remarks>
    public sealed record Model(string Key, string Repo, string Label, string Size, string Note, int NeedGb, int MinGb, bool Hidden = false, bool Vision = true);

    public static readonly Model[] Models =
    {
        new("qwen35-2b", "unsloth/Qwen3.5-2B-GGUF", "Qwen3.5 2B", "≈ 1.3 GB", "最輕量、最快，適合簡單問答同舊電腦", 4, 3),
        new("qwen35-4b", "unsloth/Qwen3.5-4B-GGUF", "Qwen3.5 4B", "≈ 2.7 GB", "聰明啲，速度適中，日常問答同寫作", 8, 5),
        new("gemma4-e4b", "unsloth/gemma-4-E4B-it-GGUF", "Gemma 4 E4B", "≈ 5.0 GB", "Google 出品，問答、寫作、摘要都唔錯", 12, 7),
        new("qwen35-9b", "unsloth/Qwen3.5-9B-GGUF", "Qwen3.5 9B", "≈ 5.7 GB", "聰明好多，寫作、摘要、推理都好", 16, 8),
        new("gemma4-12b", "unsloth/gemma-4-12b-it-GGUF", "Gemma 4 12B", "≈ 7.1 GB", "Google 出品，寫作同推理更好", 16, 10),
        new("qwen38-27b", "unsloth/Qwen3.8-27B-GGUF", "Qwen3.8 27B", "≈ 16.5 GB", "最勁（接近雲端 AI）；有 24 GB 顯示卡會快好多", 32, 20),
        new("glm47-flash", "unsloth/GLM-4.7-Flash-GGUF", "GLM-4.7 Flash", "≈ 18.3 GB", "智譜出品；大模型但行得快，寫程式同推理好強", 32, 22, Vision: false),
    };

    /// <summary>
    /// 部機未達模型最低要求仍然想下載：彈警告 + 免責聲明，預設揀「否」。回傳用家係咪堅持下載。
    /// reason = Check() 出嘅原因（記憶體 / 顯示卡差幾多）。本地 AI、AI 製圖、AI 製影片共用。
    /// </summary>
    public static bool ConfirmWeak(IWin32Window owner, string label, string reason)
    {
        string msg = Lang.F("⚠ 警告：你部電腦未達「{0}」嘅最低要求。", label) + "\n" + reason + "\n\n" +
                     Lang.T("下載咗好大機會行唔到；就算行到都會極慢，仲可能令電腦冇反應、其他程式被迫關閉，未儲存嘅資料可能會遺失。") + "\n\n" +
                     Lang.F("免責聲明：如果你堅持下載同使用，因此引起嘅任何問題或損失（包括資料遺失、系統唔穩定、浪費嘅下載流量同磁碟空間）一律由你自行承擔，{0} 同開發者概不負責。", Program.AppName) + "\n\n" +
                     Lang.T("仲係要下載？");
        return MessageBox.Show(owner, msg, Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    // ---------------- 顯示卡 ----------------
    private static double _vramGb = double.NaN;
    private static string _gpuName = "";
    /// <summary>最大嗰張顯示卡嘅專用記憶體（GB）；冇獨立顯示卡 = 0；讀唔到 = -1（唔做顯示卡檢查）。</summary>
    public static double VramGb { get { ReadGpu(); return _vramGb; } }
    public static string GpuName { get { ReadGpu(); return _gpuName; } }

    /// <summary>由驅動程式登錄檔讀顯示卡名同專用記憶體（同效能監控一樣嘅做法）。</summary>
    private static void ReadGpu()
    {
        if (!double.IsNaN(_vramGb)) return;
        _vramGb = -1;
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return;
            long best = 0;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k == null) continue;
                long mem = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0), byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0), int i => (uint)i, _ => 0
                };
                if (mem == 0 && k.GetValue("HardwareInformation.MemorySize") is int m32) mem = (uint)m32;
                if (mem == 0 && k.GetValue("HardwareInformation.MemorySize") is byte[] mb && mb.Length >= 4) mem = BitConverter.ToUInt32(mb, 0);
                if (mem > best || _gpuName.Length == 0) { best = Math.Max(best, mem); _gpuName = k.GetValue("DriverDesc") as string ?? _gpuName; }
            }
            _vramGb = best / 1073741824.0;
        }
        catch { _vramGb = -1; }
    }

    /// <summary>視窗頂嗰句：你部機嘅記憶體同顯示卡。</summary>
    public static string SpecText(bool star = false)
    {
        double ram = RamGb, v = VramGb;
        string s = ram > 0 ? (star ? Lang.F("你部機記憶體：{0:0} GB（★ = 建議）", ram) : Lang.F("你部機記憶體：{0:0} GB", ram)) : "";
        if (v < 0) return s;
        string gpu = v >= 1 ? Lang.F("顯示卡：{0}（{1:0.#} GB）", GpuName, v) : Lang.T("顯示卡：偵測唔到獨立顯示卡");
        return s.Length > 0 ? s + "　" + gpu : gpu;
    }

    /// <summary>
    /// 部機夠唔夠行一個模型（記憶體 + 顯示卡一齊計）。Level：0 = 夠、1 = 可能唔夠（會慢）、2 = 未達最低要求（紅字，下載前要再確認）。
    /// minVram = 0（本地 AI 對話）：冇顯示卡都用 CPU 行到，顯示卡記憶體當多咗記憶體計。
    /// minVram > 0（製圖 / 製影片）：一定要有夠記憶體嘅顯示卡。
    /// </summary>
    public static (int Level, string Text) Check(int minGb, int needGb, int minVram = 0, int needVram = 0)
    {
        double ram = RamGb, v = VramGb;
        if (ram <= 0) return (0, "");
        if (minVram > 0)
        {
            if (v >= 0 && v + 0.3 < minVram)
                return (2, v < 1 ? Lang.F("要有最少 {0} GB 記憶體嘅獨立顯示卡，但偵測唔到獨立顯示卡。", minVram)
                                 : Lang.F("顯示卡記憶體不足：最少要 {0} GB（你部機 {1:0.#} GB）", minVram, v));
            if (ram + 0.5 < minGb) return (2, Lang.F("記憶體不足：最少要 {0} GB（你部機 {1:0} GB）", minGb, ram));
            if (v >= 0 && v + 0.3 < needVram) return (1, Lang.F("⚠ 顯示卡記憶體偏低（{0:0.#} GB，建議 {1} GB），會好慢", v, needVram));
            return ram + 0.5 < needGb ? (1, Lang.T("⚠ 記憶體可能唔夠，可能會好慢")) : (0, "");
        }
        double mem = ram + Math.Max(0, v);
        if (mem + 0.5 < minGb)
            return (2, v >= 1 ? Lang.F("記憶體不足：最少要 {0} GB（你部機記憶體 {1:0} GB + 顯示卡 {2:0.#} GB）", minGb, ram, v)
                              : Lang.F("記憶體不足：最少要 {0} GB（你部機 {1:0} GB）", minGb, ram));
        return mem + 0.5 < needGb ? (1, Lang.T("⚠ 記憶體可能唔夠，可能會好慢")) : (0, "");
    }

    public static (int Level, string Text) Fit(Model m) => Check(m.MinGb, m.NeedGb);
    public static int RamFit(Model m) => Fit(m).Level;

    // 新模型（Gemma 4、Qwen3.8）要新版 llama.cpp；舊版用家下載模型時會順手更新
    private const string BinTag = "b12210";
    private const string BinUrl = "https://github.com/ggml-org/llama.cpp/releases/download/" + BinTag + "/llama-" + BinTag + "-bin-win-vulkan-x64.zip";
    private const string Quant = "Q4_K_M";

    /// <summary>部機總記憶體（GB）。</summary>
    /// <remarks>程式係 32-bit：GC 只會報 2 GB（自己可以用嘅上限），所以要問 Windows 部機實際有幾多。</remarks>
    public static double RamGb
    {
        get
        {
            try
            {
                var st = new MemStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemStatus>() };
                return GlobalMemoryStatusEx(ref st) ? st.TotalPhys / 1073741824.0 : 0;
            }
            catch { return 0; }
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemStatus st);

    /// <summary>按記憶體建議用邊個（Qwen 廣東話最好）。</summary>
    public static Model Recommended
    {
        get
        {
            double ram = RamGb;
            var small = Models.First(m => m.Key == "qwen35-4b");
            if (ram <= 0) return small;
            // 記憶體細過 8 GB：建議最輕量嘅 2B
            return new[] { "qwen35-9b", "qwen35-4b" }.Select(k => Models.First(m => m.Key == k))
                       .FirstOrDefault(m => ram + 0.5 >= m.NeedGb) ?? Models.First(m => m.Key == "qwen35-2b");
        }
    }

    public static string Dir => Path.Combine(AiProvider.WorkDir, "local");
    private static string BinDir => Path.Combine(Dir, "bin");
    public static string ServerExe => Path.Combine(BinDir, "llama-server.exe");
    private static string SettingFile => Path.Combine(Dir, "model.txt");
    private static string ModelFile(Model m) => Path.Combine(Dir, m.Key + ".gguf");

    public static bool BinReady => File.Exists(ServerExe);
    private static string BinVerFile => Path.Combine(BinDir, "version.txt");
    private static bool BinOutdated
    {
        get { try { return !File.Exists(BinVerFile) || File.ReadAllText(BinVerFile).Trim() != BinTag; } catch { return true; } }
    }
    public static bool ModelReady(Model m) => File.Exists(ModelFile(m));

    // ---------------- 圖片識別 ----------------
    // 識睇圖嘅模型（Qwen3.5 / 3.8、Gemma 4）要多一個「圖片識別組件」（mmproj）先睇到圖；llama-server 開嗰陣加 --mmproj。
    // 唔識睇圖嘅模型（GLM-4.7 Flash）：借用另一個已下載、有組件嘅模型（「圖片識別模型」）先將圖片寫成文字。
    private static string MmprojFile(Model m) => Path.Combine(Dir, m.Key + ".mmproj.gguf");
    /// <summary>呢個模型而家真係睇到圖（模型同組件都下載咗）。</summary>
    public static bool VisionReady(Model m) => m.Vision && ModelReady(m) && File.Exists(MmprojFile(m));
    private static string HelperFile => Path.Combine(Dir, "vision.txt");

    /// <summary>可以用嚟做「圖片識別模型」嘅細模型（由細到大）。</summary>
    public static Model[] HelperChoices => new[] { "qwen35-2b", "qwen35-4b", "gemma4-e4b" }.Select(k => Models.First(m => m.Key == k)).ToArray();

    /// <summary>借用嚟睇圖嘅模型：用家揀咗嗰個，冇揀就用已經睇到圖嘅最細嗰個。</summary>
    public static Model? VisionHelper
    {
        get
        {
            string key = "";
            try { if (File.Exists(HelperFile)) key = File.ReadAllText(HelperFile).Trim(); } catch { }
            return Models.FirstOrDefault(x => x.Key == key && VisionReady(x)) ?? Models.Where(VisionReady).OrderBy(x => x.MinGb).FirstOrDefault();
        }
        set { try { Directory.CreateDirectory(Dir); File.WriteAllText(HelperFile, value?.Key ?? ""); } catch { } }
    }

    public enum ImageSupport { Direct, Helper, NeedAddon, NeedHelper }

    /// <summary>
    /// 而家嘅本地 AI 睇唔睇到圖：Direct = 自己睇到；Helper = 借「圖片識別模型」；
    /// NeedAddon = 模型識睇圖但未下載組件；NeedHelper = 模型唔識睇圖，又未有圖片識別模型。
    /// </summary>
    public static ImageSupport Images
    {
        get
        {
            var m = Current;
            if (m != null && VisionReady(m)) return ImageSupport.Direct;
            if (m != null && VisionHelper is { } h && h != m) return ImageSupport.Helper;
            return m is { Vision: true } ? ImageSupport.NeedAddon : ImageSupport.NeedHelper;
        }
    }

    /// <summary>搵 repo 入面嘅圖片識別組件（mmproj）同大細；冇就 null。</summary>
    public static async Task<(string File, long Size)?> ResolveMmprojAsync(Model m, CancellationToken ct)
    {
        using var http = NewClient();
        string json = await http.GetStringAsync($"https://huggingface.co/api/models/{m.Repo}?blobs=true", ct);
        using var doc = JsonDocument.Parse(json);
        var files = doc.RootElement.GetProperty("siblings").EnumerateArray()
            .Select(x => (Name: x.GetProperty("rfilename").GetString() ?? "", Size: x.TryGetProperty("size", out var z) && z.ValueKind == JsonValueKind.Number ? z.GetInt64() : 0L))
            .Where(f => f.Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && !f.Name.Contains('/') && f.Name.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (files.Count == 0) return null;
        var pick = files.FirstOrDefault(f => f.Name.Contains("-F16", StringComparison.OrdinalIgnoreCase));
        if (pick.Name == null) pick = files.FirstOrDefault(f => f.Name.Contains("BF16", StringComparison.OrdinalIgnoreCase));
        if (pick.Name == null) pick = files[0];
        return (pick.Name, pick.Size);
    }

    /// <summary>下載圖片識別組件（模型本身要已經下載）。</summary>
    public static async Task InstallVisionAsync(Model m, Action<string, double> progress, CancellationToken ct)
    {
        if (!m.Vision) throw new InvalidOperationException(Lang.F("{0} 唔識睇圖片。", m.Label));
        progress(Lang.T("搵緊圖片識別組件…"), -1);
        var mm = await ResolveMmprojAsync(m, ct) ?? throw new InvalidOperationException(Lang.T("搵唔到圖片識別組件。"));
        await DownloadAsync($"https://huggingface.co/{m.Repo}/resolve/main/{mm.File}", MmprojFile(m),
            (d, t) => progress(Lang.F("下載圖片識別組件（{0}）…", m.Label), t > 0 ? (double)d / t : -1), ct);
        if (Current == m) Server.Stop();   // 下次問就會連組件一齊載入
        progress(Lang.T("完成"), 1);
    }

    /// <summary>將圖片縮細（最長邊 1024）做 JPEG，變 data: 網址（太大張會食晒模型嘅上文）。</summary>
    public static string ImageDataUrl(string path)
    {
        using var img = System.Drawing.Image.FromFile(path);
        double k = Math.Min(1.0, 1024.0 / Math.Max(img.Width, img.Height));
        using var bmp = new System.Drawing.Bitmap(Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k)));
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.White);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, 0, 0, bmp.Width, bmp.Height);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
        return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>問題 + 圖片 → llama-server 嘅 content（OpenAI 格式）。</summary>
    public static object ImageContent(string text, IEnumerable<string> images)
    {
        var parts = new List<object> { new { type = "text", text } };
        foreach (var f in images)
            try { parts.Add(new { type = "image_url", image_url = new { url = ImageDataUrl(f) } }); } catch (Exception ex) { AppPaths.LogError("AI 圖片", ex); }
        return parts;
    }

    /// <summary>
    /// 用「圖片識別模型」將圖片寫成文字（俾唔識睇圖嘅模型用）：另外開一個 llama-server，問完就閂。
    /// </summary>
    public static async Task<string> DescribeAsync(Model helper, IReadOnlyList<string> images, string question)
    {
        Process? p = null;
        try
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            p = Process.Start(new ProcessStartInfo(ServerExe, $"-m \"{ModelFile(helper)}\" --mmproj \"{MmprojFile(helper)}\" --host 127.0.0.1 --port {port} -c 8192 -ngl 99 --jinja")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = BinDir })!;
            ChildJob.Add(p);
            p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            bool up = false;
            for (int i = 0; i < 720 && !p.HasExited && !up; i++)
            {
                try { using var r = await Server.Http.GetAsync($"http://127.0.0.1:{port}/health"); up = r.IsSuccessStatusCode; } catch { }
                if (!up) await Task.Delay(250);
            }
            if (!up) throw new InvalidOperationException(Lang.T("圖片識別模型開唔到。"));
            var body = new Dictionary<string, object>
            {
                ["messages"] = new object[]
                {
                    new { role = "user", content = ImageContent(
                        "Describe the image(s) in detail for someone who cannot see them: what is shown, layout, colours, any charts or tables with their values, " +
                        "and transcribe ALL visible text exactly in its original language. If there are several images, describe them one by one. " +
                        "The user's question about the image(s) is: " + question, images) }
                },
                ["temperature"] = 0.2, ["max_tokens"] = 1500,
                ["chat_template_kwargs"] = new Dictionary<string, object> { ["enable_thinking"] = false }
            };
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await Server.Http.PostAsync($"http://127.0.0.1:{port}/v1/chat/completions", content);
            string json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(json.Length > 300 ? json[..300] : json);
            using var doc = JsonDocument.Parse(json);
            var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            string text = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
            if (text.Trim().Length == 0 && msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String) text = rc.GetString() ?? "";
            return Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
        }
        finally { try { if (p is { HasExited: false }) p.Kill(true); } catch { } p?.Dispose(); }
    }

    /// <summary>
    /// 附咗圖片但而家嘅本地 AI 睇唔到：話俾用家知，問佢要唔要去下載圖片識別；要就開「圖片識別（本地）」視窗。
    /// 回傳之後睇唔睇到圖。
    /// </summary>
    private static bool _visionDeclined;
    public static bool OfferVision(IWin32Window? owner, bool once = false)
    {
        var st = Images;
        if (st is ImageSupport.Direct or ImageSupport.Helper) return true;
        if (once && _visionDeclined) return false;   // 用家講咗唔要：今次開 app 唔再煩佢
        string msg = st == ImageSupport.NeedAddon
            ? Lang.F("而家用緊嘅本地 AI（{0}）識睇圖片，但未下載「圖片識別組件」，所以暫時睇唔到你附加嘅圖片。\n\n而家去下載？", Current?.Label ?? "")
            : Lang.F("而家用緊嘅本地 AI（{0}）唔識睇圖片。只要下載多一個標明「👁 識睇圖片」嘅模型（最細係 Qwen3.5 2B），之後附圖就會自動借佢幫手睇圖。\n\n而家去下載？", Current?.Label ?? "");
        if (MessageBox.Show(owner, msg, Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) { _visionDeclined = true; return false; }
        using var dlg = new LocalAiDialog(forVision: true);
        dlg.ShowDialog(owner);
        return Images is ImageSupport.Direct or ImageSupport.Helper;
    }

    /// <summary>揀咗用邊個模型（要已下載）；冇就 null。</summary>
    public static Model? Current
    {
        get
        {
            string key = "";
            try { if (File.Exists(SettingFile)) key = File.ReadAllText(SettingFile).Trim(); } catch { }
            var m = Models.FirstOrDefault(x => x.Key == key && ModelReady(x)) ?? Models.FirstOrDefault(ModelReady);
            return BinReady ? m : null;
        }
        set
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllText(SettingFile, value?.Key ?? ""); } catch { }
            Server.Stop();
        }
    }

    private static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(Program.FileName);
        return c;
    }

    private static async Task DownloadAsync(string url, string dest, Action<long, long> progress, CancellationToken ct)
    {
        string tmp = dest + ".part";
        using (var http = NewClient())
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
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

    /// <summary>問 Hugging Face 個 repo 有咩檔，揀 Q4_K_M 嗰個 .gguf（費事寫死檔名）。</summary>
    private static async Task<string> ResolveFileAsync(Model m, CancellationToken ct)
    {
        using var http = NewClient();
        string json = await http.GetStringAsync($"https://huggingface.co/api/models/{m.Repo}", ct);
        using var doc = JsonDocument.Parse(json);
        var files = doc.RootElement.GetProperty("siblings").EnumerateArray()
            .Select(s => s.GetProperty("rfilename").GetString() ?? "")
            .Where(f => f.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && !f.Contains('/') &&
                        !f.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return files.FirstOrDefault(f => f.Contains("-" + Quant + ".", StringComparison.OrdinalIgnoreCase))
               ?? files.FirstOrDefault(f => f.Contains(Quant, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(Lang.T("搵唔到模型檔案。"));
    }

    /// <summary>一click 安裝：未有程式就先下載 llama.cpp，再下載模型。</summary>
    public static async Task InstallAsync(Model m, Action<string, double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        if (!BinReady || BinOutdated)
        {
            Server.Stop();
            string zip = Path.Combine(Dir, "llama-bin.zip");
            await DownloadAsync(BinUrl, zip, (d, t) => progress(Lang.T("下載本地 AI 程式…"), t > 0 ? (double)d / t : -1), ct);
            Directory.CreateDirectory(BinDir);
            using (var z = ZipFile.OpenRead(zip))
                foreach (var e in z.Entries)
                {
                    if (e.Name.Length == 0) continue;
                    if (!e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && e.Name != "llama-server.exe") continue;
                    e.ExtractToFile(Path.Combine(BinDir, e.Name), true);
                }
            File.Delete(zip);
            File.WriteAllText(BinVerFile, BinTag);
        }
        if (!ModelReady(m))
        {
            progress(Lang.T("搵緊模型…"), -1);
            string file = await ResolveFileAsync(m, ct);
            await DownloadAsync($"https://huggingface.co/{m.Repo}/resolve/main/{file}", ModelFile(m),
                (d, t) => progress(Lang.F("下載模型 {0}…", m.Label), t > 0 ? (double)d / t : -1), ct);
        }
        // 識睇圖嘅模型：順手下載埋圖片識別組件（失敗唔影響用嚟傾偈，之後可以喺「圖片識別」再裝）
        if (m.Vision && !VisionReady(m))
            try { await InstallVisionAsync(m, progress, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppPaths.LogError("下載圖片識別組件", ex); }
        progress(Lang.T("完成"), 1);
    }

    public static void Delete(Model m)
    {
        if (Current == m) Server.Stop();
        try { File.Delete(ModelFile(m)); } catch (Exception ex) { AppPaths.LogError("刪除模型", ex); }
        try { File.Delete(MmprojFile(m)); } catch { }
    }

    /// <summary>常駐 llama-server（模型只載入一次），15 分鐘冇用就閂。</summary>
    internal static class Server
    {
        private static Process? _proc;
        private static int _port;
        private static string _model = "";
        private static Task<bool>? _starting;
        private static DateTime _lastUse = DateTime.Now;
        private static System.Threading.Timer? _idle;
        public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
        public static int Port => _port;

        public static Task<bool> EnsureAsync()
        {
            _lastUse = DateTime.Now;
            var m = Current;
            if (m == null) return Task.FromResult(false);
            if (_proc is { HasExited: false } && _model == m.Key && _starting != null) return _starting;
            Stop();
            _model = m.Key;
            _starting = StartAsync(m);
            _idle ??= new System.Threading.Timer(_ => { if ((DateTime.Now - _lastUse).TotalMinutes > 15) Stop(); }, null, 60_000, 60_000);
            return _starting;
        }

        private static async Task<bool> StartAsync(Model m)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                _port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                var psi = new ProcessStartInfo(ServerExe,
                    $"-m \"{ModelFile(m)}\"" + (VisionReady(m) ? $" --mmproj \"{MmprojFile(m)}\"" : "") + $" --host 127.0.0.1 --port {_port} -c 8192 -ngl 99 --jinja")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = BinDir
                };
                var p = Process.Start(psi)!;
                ChildJob.Add(p);
                p.OutputDataReceived += (_, _) => { };
                p.ErrorDataReceived += (_, _) => { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                _proc = p;
                int wait = m.NeedGb >= 32 ? 1440 : m.NeedGb >= 16 ? 960 : 480; // 大模型載入要耐啲（最多 6 分鐘）
                for (int i = 0; i < wait && !p.HasExited; i++)
                {
                    try
                    {
                        using var r = await Http.GetAsync($"http://127.0.0.1:{_port}/health");
                        if (r.IsSuccessStatusCode) return true;
                    }
                    catch { }
                    await Task.Delay(250);
                }
                return false;
            }
            catch (Exception ex)
            {
                AppPaths.LogError("開本地 AI", ex);
                return false;
            }
        }

        public static void Touch() => _lastUse = DateTime.Now;

        public static void Stop()
        {
            try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
            _proc = null;
            _starting = null;
        }
    }
}

/// <summary>本地 AI 當做一個「平台」：唔使登入，下載咗模型就用得。</summary>
internal sealed class LocalProvider : AiProvider
{
    public override string Key => "local";
    public override string Name => Lang.T("本地 AI");
    public override string ToolName => Lang.T("本地 AI 模型");
    public override string InstallUrl => "";
    protected override string Command => "llama-server";
    protected override string LoginArgs => "";

    // 每段對話嘅上文（程式閂咗就冇，之後當新對話）
    private static readonly Dictionary<string, List<object>> History = new();

    public override string? Find() => LocalAi.Current != null ? LocalAi.ServerExe : null;

    public override Task<Auth> AuthAsync(string exe) =>
        Task.FromResult(new Auth(LocalAi.Current != null, LocalAi.Current?.Label));

    public override Task<bool> LogoutAsync(string exe) => Task.FromResult(true);

    /// <summary>唔入上文、問一句攞一段短答（俾「上網搵資料」揀來源用）。</summary>
    internal static async Task<string> RawAsync(string system, string user, int maxTokens)
    {
        var body = new Dictionary<string, object>
        {
            ["messages"] = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
            ["temperature"] = 0.0,
            ["max_tokens"] = maxTokens,
            ["chat_template_kwargs"] = new Dictionary<string, object> { ["enable_thinking"] = false }
        };
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await LocalAi.Server.Http.PostAsync($"http://127.0.0.1:{LocalAi.Server.Port}/v1/chat/completions", content);
        string json = await resp.Content.ReadAsStringAsync();
        LocalAi.Server.Touch();
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(json);
        var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        string text = msg.TryGetProperty("content", out var ct) && ct.ValueKind == JsonValueKind.String ? ct.GetString() ?? "" : "";
        if (text.Trim().Length == 0 && msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String) text = rc.GetString() ?? "";
        return Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
    }

    public override async Task<Answer> AskAsync(string exe, string prompt, string? sessionId, Action<Process> started, bool quick = false, string? folder = null, IReadOnlyList<string>? files = null)
    {
        if (!await LocalAi.Server.EnsureAsync())
            return new Answer(Lang.T("本地 AI 開唔到，請喺「AI 模型 → 本地 AI」重新下載。"), sessionId, true);
        string sid = sessionId != null && History.ContainsKey(sessionId) ? sessionId : Guid.NewGuid().ToString("N");
        if (!History.TryGetValue(sid, out var msgs))
            History[sid] = msgs = new List<object>
            {
                new { role = "system", content =
                    "You are a helpful assistant. Always reply in the same language and dialect as the user. " +
                    "If the user writes Cantonese (e.g. 係、喺、唔、咩、嘅、我哋、乜嘢、點解), reply in colloquial Hong Kong Cantonese written in Traditional Chinese, not Mandarin. " +
                    (VoiceSettings.Speech == SpeechLang.Cantonese && quick ? "The user speaks Cantonese; reply in colloquial Hong Kong Cantonese. " : "") +
                    "Do not show your reasoning." }
            };
        // 上網搵資料（只限 AI 助手對話；文書工具、語音等 quick 模式唔搵）：資料只貼入今次送出去嗰句，上文唔記
        string webCtx = "";
        var webUsed = new List<(string Name, string Url)>();
        if (!quick && WebSearch.Enabled)
            try { (webCtx, webUsed) = await WebSearch.GatherAsync(prompt, (sys, user) => RawAsync(sys, user, 120), CancellationToken.None); }
            catch (Exception ex) { AppPaths.LogError("上網搵資料", ex); }
        // 附件：文字檔直接貼入；圖片 = 自己睇（有圖片識別組件）/ 借圖片識別模型寫成文字 / 睇唔到（照舊講明）
        var pics = files?.Where(AiFiles.IsImage).ToList() ?? new List<string>();
        object? withImages = null;
        if (pics.Count > 0)
        {
            var sup = LocalAi.Images;
            var rest = files!.Where(f => !AiFiles.IsImage(f)).ToList();
            if (sup == LocalAi.ImageSupport.Direct)
            {
                if (rest.Count > 0) prompt += AiFiles.Inline(rest);
                withImages = LocalAi.ImageContent(prompt + webCtx, pics);
                prompt += "\n[" + string.Join("、", pics.Select(Path.GetFileName)) + "]";   // 上文只記檔名，唔記成張圖
            }
            else if (sup == LocalAi.ImageSupport.Helper && LocalAi.VisionHelper is { } helper)
            {
                if (rest.Count > 0) prompt += AiFiles.Inline(rest);
                try
                {
                    string desc = await LocalAi.DescribeAsync(helper, pics, prompt);
                    prompt += "\n\n[系統: 用戶附加咗圖片（" + string.Join("、", pics.Select(Path.GetFileName)) + "）。你睇唔到原圖；以下係圖片識別模型 " + helper.Label + " 對圖片嘅描述，請根據佢回答：\n" + desc + "]";
                }
                catch (Exception ex) { AppPaths.LogError("圖片識別", ex); prompt += AiFiles.Inline(pics); }
            }
            else prompt += AiFiles.Inline(files!);
        }
        else if (files is { Count: > 0 }) prompt += AiFiles.Inline(files);
        msgs.Add(new { role = "user", content = prompt });
        // 有圖：今次送出去嘅最後一句換做「文字 + 圖片」，上文（msgs）照舊只留文字
        object? sendLast = withImages ?? (webCtx.Length > 0 ? prompt + webCtx : null);
        var send = sendLast == null ? msgs : msgs.Take(msgs.Count - 1).Append(new { role = "user", content = sendLast }).ToList();
        var body = new Dictionary<string, object>
        {
            ["messages"] = send,
            ["temperature"] = 0.7,
            ["max_tokens"] = quick ? 256 : 2048,
            ["chat_template_kwargs"] = new Dictionary<string, object> { ["enable_thinking"] = false }
        };
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await LocalAi.Server.Http.PostAsync($"http://127.0.0.1:{LocalAi.Server.Port}/v1/chat/completions", content);
            string json = await resp.Content.ReadAsStringAsync();
            LocalAi.Server.Touch();
            if (!resp.IsSuccessStatusCode) { msgs.RemoveAt(msgs.Count - 1); return new Answer(json, sid, true); }
            using var doc = JsonDocument.Parse(json);
            var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            string text = msg.TryGetProperty("content", out var ct) && ct.ValueKind == JsonValueKind.String ? ct.GetString() ?? "" : "";
            // 有啲模型（例如 Gemma 4 12B）會將答案放咗喺 reasoning_content
            if (text.Trim().Length == 0 && msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String) text = rc.GetString() ?? "";
            text = Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
            msgs.Add(new { role = "assistant", content = text });
            // 來源由程式自己列（唔靠模型抄網址，費事抄錯）
            if (text.Length > 0 && webUsed.Count > 0)
                text += "\n\n🌐 " + Lang.T("資料來源：") + "\n" + string.Join("\n", webUsed.Select(u => "• " + u.Name + "　" + u.Url));
            return new Answer(text.Length > 0 ? text : Lang.T("(冇回覆)"), sid, false);
        }
        catch (Exception ex)
        {
            msgs.RemoveAt(msgs.Count - 1);
            return new Answer(Lang.T("出錯：") + ex.Message, sid, true);
        }
    }
}

/// <summary>「本地 AI」小視窗：揀模型，一 click 就下載安裝。</summary>
internal sealed class LocalAiDialog : Form
{
    public event Action? Changed;
    private CancellationTokenSource? _cts;

    /// <param name="forVision">由「附咗圖但睇唔到」帶過嚟：下載完唔自動轉用新模型（等佢留返做幫手睇圖）。</param>
    public LocalAiDialog(bool forVision = false)
    {
        Text = Lang.T("本地 AI");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.F(9.75f);
        // 每行四行字：名 + 大細｜建議記憶體 · 識唔識睇圖｜簡介｜狀態；右邊一欄係按鈕（唔會同字重疊）
        int rowH = Ui.S(104), W = Ui.S(540), btnW = Ui.S(112), textW = Ui.S(540 - 24 - 24 - 112 - 28);
        double ram = LocalAi.RamGb;
        var rec = LocalAi.Recommended;
        // 已經唔再提供嘅模型：下載咗先顯示
        var shown = LocalAi.Models.Where(m => !m.Hidden || LocalAi.ModelReady(m)).ToArray();
        int listH = Math.Min(rowH * shown.Length, Math.Max(rowH * 3, Screen.FromPoint(Cursor.Position).WorkingArea.Height - Ui.S(200)));
        ClientSize = new Size(W, Ui.S(92) + listH + Ui.S(10));
        FormClosing += (_, _) => _cts?.Cancel();
        Controls.Add(new Label
        {
            Text = Lang.T("免費開源模型，喺你部機自己行：唔使帳號、唔使錢，下載完唔使上網。有顯示卡會自動加速。") + "\n" +
                   Lang.T("標明「👁 識睇圖片」嘅模型會連埋圖片識別組件一齊下載，可以睇你附加嘅圖片。") +
                   "\n" + LocalAi.SpecText(star: true),
            ForeColor = Ui.TextDim, Font = Ui.F(9f), AutoSize = false, Bounds = new Rectangle(Ui.S(16), Ui.S(10), W - Ui.S(32), Ui.S(76))
        });
        var list = new Panel { AutoScroll = true, BackColor = Ui.Card, Bounds = new Rectangle(0, Ui.S(88), W, listH) };
        Controls.Add(list);
        var rows = new List<Action>();
        int y = 0;
        foreach (var m in shown)
        {
            var panel = new Panel { BackColor = Ui.CardHi, Bounds = new Rectangle(Ui.S(12), y, W - Ui.S(44), rowH - Ui.S(8)) };
            var (fit, fitText) = LocalAi.Fit(m);
            int bx = panel.Width - btnW - Ui.S(12);
            var name = new Label { Text = (m == rec ? "★ " : "") + $"{m.Label}　{m.Size}", Font = Ui.F(10.5f, FontStyle.Bold), ForeColor = m == rec ? Ui.Accent : Ui.Text, AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(Ui.S(12), Ui.S(8), textW, Ui.S(22)) };
            var need = new Label { Text = Lang.F("建議 {0} GB RAM", m.NeedGb), Font = Ui.F(8.75f, FontStyle.Bold), AutoSize = true, Location = new Point(Ui.S(12), Ui.S(32)),
                ForeColor = fit == 2 ? Ui.Danger : fit == 1 ? Ui.Hex(0xE0A030) : Ui.TextDim };
            // 寫明呢個模型識唔識睇圖片
            var vis = new Label { Text = m.Vision ? "👁 " + Lang.T("識睇圖片") : Lang.T("唔識睇圖片"), Font = Ui.F(8.75f), AutoSize = true, ForeColor = m.Vision ? Ui.Success : Ui.TextFaint };
            void PlaceTags() => vis.Location = new Point(need.Right + Ui.S(12), Ui.S(32));
            need.SizeChanged += (_, _) => PlaceTags();
            var note = new Label { Text = Lang.T(m.Note), Font = Ui.F(8.75f), ForeColor = Ui.TextFaint, AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(Ui.S(12), Ui.S(52), textW, Ui.S(18)) };
            new ToolTip().SetToolTip(note, Lang.T(m.Note));
            var state = new Label { Font = Ui.F(8.75f), AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(Ui.S(12), Ui.S(72), textW, Ui.S(18)) };
            var bar = new ProgressBar { Bounds = new Rectangle(Ui.S(12), Ui.S(90), panel.Width - Ui.S(24), Ui.S(4)), Visible = false };
            var main = new FancyButton { Kind = BtnKind.Primary, Font = Ui.F(9.5f), Bounds = new Rectangle(bx, Ui.S(10), btnW, Ui.S(30)) };
            // 舊用家：模型下載咗但未有圖片識別組件 → 喺呢度補裝
            var add = new FancyButton { Text = Lang.T("加裝圖片識別"), Kind = BtnKind.Ghost, Font = Ui.F(8.75f), Bounds = new Rectangle(bx, Ui.S(44), btnW, Ui.S(22)), Visible = false };
            var del = new FancyButton { Text = Lang.T("刪除"), Kind = BtnKind.Ghost, Font = Ui.F(8.75f), Bounds = new Rectangle(bx, Ui.S(44), btnW, Ui.S(22)) };
            panel.Controls.AddRange(new Control[] { name, need, vis, note, state, bar, main, add, del });
            PlaceTags();
            list.Controls.Add(panel);

            void Refresh()
            {
                bool ready = LocalAi.BinReady && LocalAi.ModelReady(m);
                bool inUse = ready && LocalAi.Current == m && AiProvider.LoadCurrent() is LocalProvider;
                bool noAddon = ready && m.Vision && !LocalAi.VisionReady(m);
                state.Text = (inUse ? Lang.T("✓ 使用中") : ready ? Lang.T("✓ 已下載") : Lang.T("未下載")) + (noAddon ? Lang.T("（未裝圖片識別組件）") : "");
                state.ForeColor = noAddon ? Ui.Hex(0xE0A030) : ready ? Ui.Success : Ui.TextFaint;
                if (!ready && fit == 1) { state.Text = fitText; state.ForeColor = Ui.Hex(0xE0A030); }
                if (!ready && fit == 2) { state.Text = "⚠ " + fitText; state.ForeColor = Ui.Danger; }
                // 未達最低要求：成行紅字，仍然可以撳下載（會再警告）
                if (fit == 2) { name.ForeColor = Ui.Danger; main.Kind = ready ? BtnKind.Primary : BtnKind.Danger; }
                main.Text = ready ? Lang.T("使用") : Lang.T("下載");
                main.Enabled = !inUse && _cts == null;
                add.Visible = noAddon && _cts == null;
                del.Visible = ready && _cts == null;
                del.Top = add.Visible ? Ui.S(70) : Ui.S(44);
                main.Invalidate();
            }
            rows.Add(Refresh);

            // 下載（成個模型，或者淨係補裝圖片識別組件）
            async Task Run(bool addonOnly)
            {
                _cts = new CancellationTokenSource();
                rows.ForEach(r => r());
                bar.Visible = true;
                bool ok = false;
                void Progress(string text, double frac) => BeginInvoke(() =>
                {
                    if (IsDisposed) return;
                    state.ForeColor = Ui.TextDim;
                    state.Text = text + (frac >= 0 ? $" {frac * 100:0}%" : "");
                    bar.Style = frac >= 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                    if (frac >= 0) bar.Value = (int)Math.Clamp(frac * 100, 0, 100);
                });
                try
                {
                    if (addonOnly)
                    {
                        await LocalAi.InstallVisionAsync(m, Progress, _cts.Token);
                        if (!LocalAi.VisionReady(m)) throw new InvalidOperationException(Lang.T("搵唔到圖片識別組件。"));
                    }
                    else await LocalAi.InstallAsync(m, Progress, _cts.Token);
                    ok = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppPaths.LogError("下載本地 AI", ex);
                    if (!IsDisposed) MessageBox.Show(this, Lang.T("下載失敗：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                _cts = null;
                if (IsDisposed) return;
                bar.Visible = false;
                // 下載完就直接轉用；由「睇唔到圖」帶過嚟而且本身有模型用緊，就留返原本嗰個（新嘅自動幫手睇圖）
                if (ok && !addonOnly && !(forVision && LocalAi.Current != null)) LocalAi.Current = m;
                if (ok) Changed?.Invoke();
                rows.ForEach(r => r());
            }

            main.Click += async (_, _) =>
            {
                if (LocalAi.BinReady && LocalAi.ModelReady(m))
                {
                    LocalAi.Current = m;
                    Changed?.Invoke();
                    rows.ForEach(r => r());
                    return;
                }
                // 未達最低要求：再提醒一次（警告 + 免責聲明），用家堅持先下載
                if (fit == 2 && !LocalAi.ConfirmWeak(this, m.Label, fitText)) return;
                await Run(false);
            };
            add.Click += async (_, _) => await Run(true);
            del.Click += (_, _) =>
            {
                if (MessageBox.Show(this, Lang.F("刪除 {0}？", m.Label), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                LocalAi.Delete(m);
                Changed?.Invoke();
                rows.ForEach(r => r());
            };
            Refresh();
            y += rowH;
        }
    }
}
