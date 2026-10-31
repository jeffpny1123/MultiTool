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
    public sealed record Model(string Key, string Repo, string Label, string Size, string Note);

    public static readonly Model[] Models =
    {
        new("qwen35-2b", "unsloth/Qwen3.5-2B-GGUF", "Qwen3.5 2B", "≈ 1.3 GB", "快；廣東話一般，適合簡單問答"),
        new("qwen35-4b", "unsloth/Qwen3.5-4B-GGUF", "Qwen3.5 4B", "≈ 2.7 GB", "較聰明，廣東話較好（建議）；較慢"),
    };

    private const string BinUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b11750/llama-b11750-bin-win-vulkan-x64.zip";
    private const string Quant = "Q4_K_M";

    public static string Dir => Path.Combine(AiProvider.WorkDir, "local");
    private static string BinDir => Path.Combine(Dir, "bin");
    public static string ServerExe => Path.Combine(BinDir, "llama-server.exe");
    private static string SettingFile => Path.Combine(Dir, "model.txt");
    private static string ModelFile(Model m) => Path.Combine(Dir, m.Key + ".gguf");

    public static bool BinReady => File.Exists(ServerExe);
    public static bool ModelReady(Model m) => File.Exists(ModelFile(m));

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
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MultiTool");
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
        if (!BinReady)
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
        }
        if (!ModelReady(m))
        {
            progress(Lang.T("搵緊模型…"), -1);
            string file = await ResolveFileAsync(m, ct);
            await DownloadAsync($"https://huggingface.co/{m.Repo}/resolve/main/{file}", ModelFile(m),
                (d, t) => progress(Lang.F("下載模型 {0}…", m.Label), t > 0 ? (double)d / t : -1), ct);
        }
        progress(Lang.T("完成"), 1);
    }

    public static void Delete(Model m)
    {
        if (Current == m) Server.Stop();
        try { File.Delete(ModelFile(m)); } catch (Exception ex) { AppPaths.LogError("刪除模型", ex); }
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
                    $"-m \"{ModelFile(m)}\" --host 127.0.0.1 --port {_port} -c 8192 -ngl 99 --jinja")
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
                for (int i = 0; i < 480 && !p.HasExited; i++) // 最多等 2 分鐘載入
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

    public override async Task<Answer> AskAsync(string exe, string prompt, string? sessionId, Action<Process> started, bool quick = false, string? folder = null, IReadOnlyList<string>? files = null)
    {
        if (!await LocalAi.Server.EnsureAsync())
            return new Answer(Lang.T("本地 AI 開唔到，請喺「帳號 → 本地 AI」重新下載。"), sessionId, true);
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
        if (files is { Count: > 0 }) prompt += AiFiles.Inline(files); // 本地 AI 睇唔到圖：文字檔直接貼入
        msgs.Add(new { role = "user", content = prompt });
        var body = new Dictionary<string, object>
        {
            ["messages"] = msgs,
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
            string text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            text = Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
            msgs.Add(new { role = "assistant", content = text });
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

    public LocalAiDialog()
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
        int rowH = Ui.S(84);
        ClientSize = new Size(Ui.S(480), Ui.S(96) + rowH * LocalAi.Models.Length);
        FormClosing += (_, _) => _cts?.Cancel();
        Controls.Add(new Label
        {
            Text = Lang.T("免費開源模型，喺你部機自己行：唔使帳號、唔使錢，下載完唔使上網。有顯示卡會自動加速。"),
            ForeColor = Ui.TextDim, Font = Ui.F(9f), AutoSize = false, Bounds = new Rectangle(Ui.S(16), Ui.S(12), Ui.S(448), Ui.S(40))
        });
        int y = Ui.S(58);
        var rows = new List<Action>();
        foreach (var m in LocalAi.Models)
        {
            var panel = new Panel { BackColor = Ui.CardHi, Bounds = new Rectangle(Ui.S(12), y, Ui.S(456), rowH - Ui.S(8)) };
            var name = new Label { Text = $"{m.Label}　{m.Size}", Font = Ui.F(10.5f, FontStyle.Bold), ForeColor = Ui.Text, AutoSize = false, Bounds = new Rectangle(Ui.S(12), Ui.S(8), Ui.S(250), Ui.S(22)) };
            var note = new Label { Text = Lang.T(m.Note), Font = Ui.F(8.75f), ForeColor = Ui.TextFaint, AutoSize = false, Bounds = new Rectangle(Ui.S(12), Ui.S(30), Ui.S(250), Ui.S(18)) };
            var state = new Label { Font = Ui.F(8.75f), AutoSize = false, Bounds = new Rectangle(Ui.S(12), Ui.S(50), Ui.S(320), Ui.S(18)) };
            var bar = new ProgressBar { Bounds = new Rectangle(Ui.S(12), Ui.S(68), Ui.S(432), Ui.S(6)), Visible = false };
            var main = new FancyButton { Kind = BtnKind.Primary, Font = Ui.F(9.5f), Bounds = new Rectangle(Ui.S(346), Ui.S(10), Ui.S(98), Ui.S(32)) };
            var del = new FancyButton { Text = Lang.T("刪除"), Kind = BtnKind.Ghost, Font = Ui.F(9f), Bounds = new Rectangle(Ui.S(270), Ui.S(10), Ui.S(70), Ui.S(32)) };
            panel.Controls.AddRange(new Control[] { name, note, state, bar, main, del });
            Controls.Add(panel);

            void Refresh()
            {
                bool ready = LocalAi.BinReady && LocalAi.ModelReady(m);
                bool inUse = ready && LocalAi.Current == m && AiProvider.LoadCurrent() is LocalProvider;
                state.Text = inUse ? Lang.T("✓ 使用中") : ready ? Lang.T("✓ 已下載") : Lang.T("未下載");
                state.ForeColor = ready ? Ui.Success : Ui.TextFaint;
                main.Text = ready ? Lang.T("使用") : Lang.T("下載");
                main.Enabled = !inUse && _cts == null;
                del.Visible = ready && _cts == null;
                main.Invalidate();
            }
            rows.Add(Refresh);

            main.Click += async (_, _) =>
            {
                if (LocalAi.BinReady && LocalAi.ModelReady(m))
                {
                    LocalAi.Current = m;
                    Changed?.Invoke();
                    rows.ForEach(r => r());
                    return;
                }
                _cts = new CancellationTokenSource();
                rows.ForEach(r => r());
                bar.Visible = true;
                bool ok = false;
                try
                {
                    await LocalAi.InstallAsync(m, (text, frac) => BeginInvoke(() =>
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
                    AppPaths.LogError("下載本地 AI", ex);
                    if (!IsDisposed) MessageBox.Show(this, Lang.T("下載失敗：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                _cts = null;
                if (IsDisposed) return;
                bar.Visible = false;
                if (ok) { LocalAi.Current = m; Changed?.Invoke(); } // 下載完就直接轉用
                rows.ForEach(r => r());
            };
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
