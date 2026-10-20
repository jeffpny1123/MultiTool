using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTool.Ai;

/// <summary>Windows Job：MultiTool 閂咗（包括 crash），入面嘅子程式都會一齊被閂。</summary>
internal static class ChildJob
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateJobObject(nint a, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(nint job, int cls, ref EXTENDED info, int size);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(nint job, nint proc);

    [StructLayout(LayoutKind.Sequential)]
    private struct BASIC { public long a, b; public uint LimitFlags; public nuint c, d; public uint e; public nuint f; public uint g, h; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IO { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    private struct EXTENDED { public BASIC Basic; public IO Io; public nuint p, q, r, s; }

    private static nint _job;

    public static void Add(Process p)
    {
        try
        {
            if (_job == 0)
            {
                _job = CreateJobObject(0, null);
                var info = new EXTENDED { Basic = new BASIC { LimitFlags = 0x2000 /*KILL_ON_JOB_CLOSE*/ } };
                SetInformationJobObject(_job, 9 /*ExtendedLimitInformation*/, ref info, Marshal.SizeOf<EXTENDED>());
            }
            AssignProcessToJobObject(_job, p.Handle);
        }
        catch { }
    }
}

/// <summary>辨識語言。</summary>
internal enum SpeechLang { Cantonese, Mandarin, English, Auto }

/// <summary>
/// 常駐嘅 whisper-server：模型只載入一次，之後每句好快。撳快捷鍵（開始錄音）嗰陣已經開始預熱。
/// 15 分鐘冇用就自動閂，慳記憶體。
/// </summary>
internal static class WhisperServer
{
    private static Process? _proc;
    private static int _port;
    private static string _modelFile = "";
    private static Task<bool>? _starting;
    private static DateTime _lastUse = DateTime.Now;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static System.Threading.Timer? _idle;

    private static string Exe => Path.Combine(Whisper.Dir, "bin", "whisper-server.exe");
    public static bool Installed => File.Exists(Exe);

    /// <summary>確保 server 開咗（已經開緊就即刻返）。</summary>
    public static Task<bool> EnsureAsync()
    {
        _lastUse = DateTime.Now;
        var m = Whisper.Current;
        if (_proc is { HasExited: false } && _modelFile == m.File && _starting is { IsCompleted: true, Result: true }) return _starting;
        if (_starting is { IsCompleted: false } && _modelFile == m.File) return _starting;
        Stop();
        _modelFile = m.File;
        _starting = StartAsync(m);
        _idle ??= new System.Threading.Timer(_ =>
        {
            if ((DateTime.Now - _lastUse).TotalMinutes > 15) Stop();
        }, null, 60_000, 60_000);
        return _starting;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static async Task<bool> StartAsync(Whisper.Model m)
    {
        try
        {
            _port = FreePort();
            int threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            var psi = new ProcessStartInfo(Exe,
                $"-m \"{Path.Combine(Whisper.Dir, m.File)}\" --host 127.0.0.1 --port {_port} -t {threads}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(Exe)!
            };
            var p = Process.Start(psi)!;
            ChildJob.Add(p);
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _proc = p;
            // 等佢載入模型、開始聽
            for (int i = 0; i < 240 && !p.HasExited; i++)
            {
                try
                {
                    using var r = await Http.GetAsync($"http://127.0.0.1:{_port}/");
                    return true;
                }
                catch { await Task.Delay(250); }
            }
            return false;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("開 whisper-server", ex);
            return false;
        }
    }

    public static void Stop()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        _proc = null;
        _starting = null;
    }

    /// <summary>辨識語言 → (language, prompt)。廣東話用 yue（只有 large-v3 系列有）+ 口語提示，令佢寫廣東話字。</summary>
    private static (string Lang, string Prompt) LangArgs()
    {
        bool v3 = Whisper.Current.Key == "turbo";
        return VoiceSettings.Speech switch
        {
            SpeechLang.Cantonese => (v3 ? "yue" : "zh", "以下係廣東話口語，用香港中文字寫：我哋而家喺度講緊嘢，佢哋話唔係咁㗎，你知唔知點解呀？"),
            SpeechLang.Mandarin => ("zh", "以下是普通话的句子，使用简体中文。"),
            SpeechLang.English => ("en", ""),
            _ => ("auto", "")
        };
    }

    public static async Task<string> TranscribeAsync(string wav, TimeSpan length, CancellationToken ct)
    {
        if (!await EnsureAsync()) throw new InvalidOperationException(Lang.T("語音辨識程式開唔到。"));
        _lastUse = DateTime.Now;
        var (lang, prompt) = LangArgs();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(wav, ct));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("0"), "temperature");
        form.Add(new StringContent(lang), "language");
        if (prompt.Length > 0) form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
        // 短句淨係計返需要嘅長度（快好多）：每秒 50 格，留啲位
        int ac = (int)Math.Clamp(length.TotalSeconds * 50 + 200, 384, 1500);
        form.Add(new StringContent(ac.ToString()), "audio_ctx");
        using var resp = await Http.PostAsync($"http://127.0.0.1:{_port}/inference", form, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(Lang.T("語音辨識失敗：") + body);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var e)) throw new InvalidOperationException(Lang.T("語音辨識失敗：") + e);
        return doc.RootElement.TryGetProperty("text", out var t) ? (t.GetString() ?? "").Trim() : "";
    }
}

/// <summary>
/// 常駐嘅 Claude Code（stream-json），問完一句唔使重新開程式，大約快 2–4 秒。
/// 10 分鐘冇用就重新開過（上文清空）；唔會留低對話紀錄。
/// </summary>
internal static class ClaudeLive
{
    /// <summary>講廣東話時，語音辨識多數出書面語，所以話明俾 AI 知要用廣東話口語答。</summary>
    public static string SystemPrompt =>
        "You are a voice assistant. Keep every reply within 50 units (each Chinese character or English word counts as 1), short and spoken-style. " +
        "Plain text only: no Markdown, no emoji, no lists. " +
        "The user's words come from speech recognition and may contain wrong homophones; infer what they meant. " +
        "When you open something, say it with its correct full name, e.g. 幫你開咗<name>. " +
        (VoiceSettings.Speech == SpeechLang.Cantonese
            ? "The user speaks Cantonese; speech-to-text often turns it into written Chinese. Always reply in colloquial Hong Kong Cantonese (e.g. 係、喺、唔、咩、嘅、我哋) unless the user clearly speaks English, then reply in English."
            : "Reply in the SAME language and dialect as the user.") +
        " For news, weather, prices, scores, schedules or anything that may have changed, use WebSearch (and WebFetch to read a page if the snippets are not enough) before answering, then give the actual answer, not just a link." +
        " Never put URLs, markdown links or source lists in the reply (it is read aloud)." +
        "\n\n" + PcActions.Rules;

    private static string _prompt = "";

    private static Process? _p;
    private static string _exe = "";
    private static DateTime _lastUse = DateTime.MinValue;
    private static TaskCompletionSource<AiProvider.Answer>? _pending;
    private static readonly object Lock = new();

    private static string PromptFile()
    {
        string f = Path.Combine(AiProvider.WorkDir, "voice_prompt.txt");
        File.WriteAllText(f, _prompt.Length > 0 ? _prompt : SystemPrompt, new UTF8Encoding(false));
        return f;
    }

    private static ProcessStartInfo Psi(string exe)
    {
        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--model", "haiku", "--effort", "low", "--no-session-persistence",
            "--tools", "WebSearch,WebFetch", "--allowedTools", "WebSearch,WebFetch", // 淨係俾上網搵資料
            "--strict-mcp-config", "--setting-sources", "",
            // 系統提示好長（有程式清單），經命令列傳會被截斷（cmd 上限 8191 字），所以寫入檔案
            "--system-prompt-file", PromptFile()
        };
        static string Q(string a) => a.Length == 0 ? "\"\"" : a.Contains(' ') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;
        bool cmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            FileName = cmd ? "cmd.exe" : exe,
            Arguments = cmd ? $"/d /s /c \"\"{exe}\" {string.Join(" ", args.Select(Q))}\"" : string.Join(" ", args.Select(Q)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = AiProvider.WorkDir
        };
        psi.Environment["MAX_THINKING_TOKENS"] = "0";
        return psi;
    }

    /// <summary>預熱（撳快捷鍵錄音嗰陣就叫）。</summary>
    public static void Warm(string exe)
    {
        lock (Lock)
        {
            bool stale = (DateTime.Now - _lastUse).TotalMinutes > 10;
            if (_p is { HasExited: false } && _exe == exe && !stale && _prompt == SystemPrompt) return;
            Kill();
            _exe = exe;
            _prompt = SystemPrompt;
            _lastUse = DateTime.Now;
            try
            {
                var p = Process.Start(Psi(exe))!;
                ChildJob.Add(p);
                p.ErrorDataReceived += (_, _) => { };
                p.BeginErrorReadLine();
                _p = p;
                _ = Task.Run(() => Read(p));
            }
            catch (Exception ex) { AppPaths.LogError("開 Claude", ex); _p = null; }
        }
    }

    private static void Read(Process p)
    {
        try
        {
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                if (!line.StartsWith('{')) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("type", out var t) || t.GetString() != "result") continue;
                    string text = r.TryGetProperty("result", out var x) ? x.GetString() ?? "" : "";
                    bool err = r.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                    _pending?.TrySetResult(new AiProvider.Answer(text, null, err));
                }
                catch { }
            }
        }
        catch { }
        _pending?.TrySetResult(new AiProvider.Answer(Lang.T("Claude 冇回應，請再試一次。"), null, true));
    }

    public static async Task<AiProvider.Answer> AskAsync(string exe, string q, CancellationToken ct)
    {
        Warm(exe);
        var p = _p ?? throw new InvalidOperationException(Lang.T("開唔到 Claude Code。"));
        _lastUse = DateTime.Now;
        var tcs = new TaskCompletionSource<AiProvider.Answer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;
        string msg = JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = PcActions.NowTag + "\n" + q } });
        await p.StandardInput.WriteLineAsync(msg);
        await p.StandardInput.FlushAsync();
        using (ct.Register(() => tcs.TrySetCanceled()))
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(60_000, ct));
            if (done != tcs.Task) { Kill(); throw new TimeoutException(Lang.T("Claude 回應超時。")); }
        }
        var ans = await tcs.Task;
        if (ans.IsError) Kill(); // 出錯（例如登入過期）就下次重新開
        return ans;
    }

    public static void Kill()
    {
        try { if (_p is { HasExited: false }) _p.Kill(true); } catch { }
        _p = null;
    }
}
