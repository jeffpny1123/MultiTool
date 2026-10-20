using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Ai;

/// <summary>
/// 用戶自己部電腦安裝咗嘅 AI 命令列工具（Claude Code / Codex / Gemini CLI）。
/// 我哋唔會處理任何帳號密碼 / token：登入全部由嗰個工具自己負責。
/// </summary>
internal abstract class AiProvider
{
    public abstract string Key { get; }          // 存檔用：claude / codex / gemini
    public abstract string Name { get; }         // 顯示：Claude / ChatGPT / Gemini
    public abstract string ToolName { get; }     // Claude Code / Codex CLI / Gemini CLI
    public abstract string InstallUrl { get; }
    protected abstract string Command { get; }   // claude / codex / gemini
    public virtual string LoginHint => "瀏覽器會彈出嚟俾你登入（如果冇彈，跟視窗入面嘅指示做）。";

    public static readonly AiProvider[] All = { new ClaudeProvider(), new CodexProvider(), new GeminiProvider() };
    public static AiProvider Get(string? key) => All.FirstOrDefault(p => p.Key == key) ?? All[0];
    public static AiProvider? TryGet(string? key) => All.FirstOrDefault(p => p.Key == key);

    /// <summary>用戶喺「AI 助手」揀咗用邊個（存喺 data\ai\provider.txt）；未揀就 null。</summary>
    public static string ProviderFile => Path.Combine(WorkDir, "provider.txt");
    public static AiProvider? LoadCurrent()
    {
        try { return File.Exists(ProviderFile) ? TryGet(File.ReadAllText(ProviderFile).Trim()) : null; }
        catch { return null; }
    }

    public sealed record Auth(bool? LoggedIn, string? Plan);
    public sealed record Answer(string Text, string? SessionId, bool IsError);

    /// <summary>搵執行檔；搵唔到就 null。</summary>
    public string? Find()
    {
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string d = dir.Trim().Trim('"');
            if (d.Length == 0) continue;
            candidates.Add(Path.Combine(d, Command + ".exe"));
            candidates.Add(Path.Combine(d, Command + ".cmd"));
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(home, ".local", "bin", Command + ".exe"));   // 官方安裝程式
        candidates.Add(Path.Combine(appData, "npm", Command + ".cmd"));          // npm 全域安裝
        try { return candidates.FirstOrDefault(File.Exists); } catch { return null; }
    }

    /// <summary>AI 喺呢個空資料夾入面行，唔會掂到你其他檔案。</summary>
    public static string WorkDir
    {
        get
        {
            var d = Path.Combine(AppPaths.Data, "ai");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    protected static ProcessStartInfo Start(string exe, string args)
    {
        bool cmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        return new ProcessStartInfo
        {
            FileName = cmd ? "cmd.exe" : exe,
            Arguments = cmd ? $"/d /s /c \"\"{exe}\" {args}\"" : args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = WorkDir
        };
    }

    /// <summary>行一個短指令，返回 (exit code, stdout + stderr)。逾時 / 出錯返 (-1, "")。</summary>
    protected static async Task<(int Code, string Out)> RunAsync(string exe, string args, int timeoutMs = 20000)
    {
        try
        {
            using var p = Process.Start(Start(exe, args))!;
            p.StandardInput.Close();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            var exit = p.WaitForExitAsync();
            if (await Task.WhenAny(exit, Task.Delay(timeoutMs)) != exit) { try { p.Kill(true); } catch { } return (-1, ""); }
            return (p.ExitCode, ((await outTask) + "\n" + (await errTask)).Trim());
        }
        catch { return (-1, ""); }
    }

    /// <summary>行問答指令：問題經 stdin 傳入，返回 (exit code, stdout, stderr)。</summary>
    protected static async Task<(int Code, string Out, string Err)> RunPromptAsync(string exe, string args, string prompt, Action<Process> started)
    {
        using var p = new Process { StartInfo = Start(exe, args) };
        p.Start();
        started(p);
        await p.StandardInput.WriteAsync(prompt);
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, (await outTask).Trim(), (await errTask).Trim());
    }

    protected static Answer Fail(int code, string output, string err, string? sid)
    {
        string msg = (output + "\n" + err).Trim();
        return new Answer(msg.Length > 0 ? msg : $"程式結束咗（代碼 {code}），冇回覆。", sid, true);
    }

    protected static IEnumerable<JsonProperty> Walk(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) yield break;
        foreach (var p in e.EnumerateObject())
        {
            yield return p;
            foreach (var c in Walk(p.Value)) yield return c;
        }
    }

    protected static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public abstract Task<Auth> AuthAsync(string exe);
    public abstract Task<bool> LogoutAsync(string exe);
    public abstract Task<Answer> AskAsync(string exe, string prompt, string? sessionId, Action<Process> started);
    protected abstract string LoginArgs { get; }

    /// <summary>開一個終端機視窗做登入，等用戶自己喺瀏覽器登入。</summary>
    public void OpenLoginTerminal(string exe)
    {
        string args = LoginArgs.Length > 0 ? " " + LoginArgs : "";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{exe}\"{args}\"")
        {
            UseShellExecute = true,
            WorkingDirectory = WorkDir
        });
    }

    /// <summary>舊對話喺工具嗰邊已經清咗？</summary>
    public static bool IsSessionGone(string text) =>
        text.Contains("No conversation found", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Invalid session identifier", StringComparison.OrdinalIgnoreCase) ||
        (text.Contains("session", StringComparison.OrdinalIgnoreCase) &&
         (text.Contains("not found", StringComparison.OrdinalIgnoreCase) || text.Contains("no such", StringComparison.OrdinalIgnoreCase)));
}

/// <summary>Claude Code（claude）。</summary>
internal sealed class ClaudeProvider : AiProvider
{
    public override string Key => "claude";
    public override string Name => "Claude";
    public override string ToolName => "Claude Code";
    public override string InstallUrl => "https://claude.com/claude-code";
    protected override string Command => "claude";
    protected override string LoginArgs => "auth login";

    /// <summary>claude auth status：exit 0 = 已登入、1 = 未登入；JSON 順手攞方案。</summary>
    public override async Task<Auth> AuthAsync(string exe)
    {
        var (code, output) = await RunAsync(exe, "auth status");
        if (code != 0 && code != 1) return new Auth(null, null);
        string? plan = null, method = null;
        bool? loggedIn = code == 0;
        try
        {
            int brace = output.IndexOf('{');
            if (brace >= 0)
            {
                using var doc = JsonDocument.Parse(output[brace..(output.LastIndexOf('}') + 1)]);
                foreach (var prop in Walk(doc.RootElement))
                {
                    string k = prop.Name.ToLowerInvariant();
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string v = prop.Value.GetString() ?? "";
                        if (v.Length == 0) continue;
                        if (plan == null && (k.Contains("subscription") || k == "plan")) plan = v;
                        else if (method == null && k == "authmethod") method = v;
                    }
                    else if (k == "loggedin" && prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        loggedIn = prop.Value.ValueKind == JsonValueKind.True;
                }
            }
        }
        catch { }
        if (method == "none") loggedIn = false;
        return new Auth(loggedIn, plan != null ? Cap(plan) : null);
    }

    public override async Task<bool> LogoutAsync(string exe) => (await RunAsync(exe, "auth logout")).Code == 0;

    public override async Task<Answer> AskAsync(string exe, string prompt, string? sessionId, Action<Process> started)
    {
        // 日常問答：唔俾佢改檔案 / 行指令
        string args = "-p --output-format json" +
                      (sessionId != null ? $" --resume {sessionId}" : "") +
                      " --disallowedTools Bash Edit Write NotebookEdit";
        var (code, output, err) = await RunPromptAsync(exe, args, prompt, started);
        // {"type":"result","result":"...","session_id":"...","is_error":false}
        try
        {
            int brace = output.IndexOf('{');
            if (brace >= 0)
            {
                using var doc = JsonDocument.Parse(output[brace..]);
                var r = doc.RootElement;
                string text = r.TryGetProperty("result", out var t) ? t.GetString() ?? "" : "";
                string? sid = r.TryGetProperty("session_id", out var s) ? s.GetString() : null;
                bool isErr = r.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                return new Answer(text.Length > 0 ? text : "(冇回覆)", sid, isErr);
            }
        }
        catch { }
        return Fail(code, output, err, null);
    }
}

/// <summary>OpenAI Codex CLI（codex），用 ChatGPT 帳號登入。</summary>
internal sealed class CodexProvider : AiProvider
{
    public override string Key => "codex";
    public override string Name => "ChatGPT";
    public override string ToolName => "Codex CLI";
    public override string InstallUrl => "https://github.com/openai/codex";
    protected override string Command => "codex";
    protected override string LoginArgs => "login";
    public override string LoginHint => "瀏覽器會彈出嚟俾你用 ChatGPT 帳號登入（如果冇彈，跟視窗入面嘅指示做）。";

    public override async Task<Auth> AuthAsync(string exe)
    {
        var (code, output) = await RunAsync(exe, "login status");
        if (code < 0) return new Auth(null, null);
        bool loggedIn = code == 0 && !output.Contains("Not logged in", StringComparison.OrdinalIgnoreCase);
        return new Auth(loggedIn, loggedIn ? ReadPlan() : null);
    }

    /// <summary>由 ~/.codex/auth.json 嘅 id_token 讀 ChatGPT 方案（plus / pro…），讀唔到就 null。</summary>
    private static string? ReadPlan()
    {
        try
        {
            string home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h
                ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            string f = Path.Combine(home, "auth.json");
            if (!File.Exists(f)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            if (!doc.RootElement.TryGetProperty("tokens", out var tokens) ||
                !tokens.TryGetProperty("id_token", out var idt) || idt.ValueKind != JsonValueKind.String) return null;
            var parts = (idt.GetString() ?? "").Split('.');
            if (parts.Length < 2) return null;
            string b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var payload = JsonDocument.Parse(Convert.FromBase64String(b64));
            foreach (var prop in Walk(payload.RootElement))
                if (prop.Name == "chatgpt_plan_type" && prop.Value.ValueKind == JsonValueKind.String)
                    return Cap(prop.Value.GetString() ?? "");
        }
        catch { }
        return null;
    }

    public override async Task<bool> LogoutAsync(string exe) => (await RunAsync(exe, "logout")).Code == 0;

    public override async Task<Answer> AskAsync(string exe, string prompt, string? sessionId, Action<Process> started)
    {
        string outFile = Path.Combine(WorkDir, "codex_" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        // 唯讀 sandbox：唔俾佢改檔案
        string common = $"--json --skip-git-repo-check -c sandbox_mode=read-only -o \"{outFile}\"";
        string args = sessionId != null ? $"exec resume {common} {sessionId} -" : $"exec {common} -";
        try
        {
            var (code, output, err) = await RunPromptAsync(exe, args, prompt, started);
            string? sid = sessionId, text = null, error = null;
            foreach (var line in output.Split('\n'))
            {
                string l = line.Trim();
                if (!l.StartsWith('{')) continue;
                try
                {
                    using var doc = JsonDocument.Parse(l);
                    var r = doc.RootElement;
                    string type = r.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    if (r.TryGetProperty("thread_id", out var th) && th.ValueKind == JsonValueKind.String) sid = th.GetString();
                    if (type == "item.completed" && r.TryGetProperty("item", out var item) &&
                        item.TryGetProperty("type", out var it) && it.GetString() == "agent_message" &&
                        item.TryGetProperty("text", out var tx)) text = tx.GetString();
                    if (type == "error" && r.TryGetProperty("message", out var m)) error = m.GetString();
                    if (type == "turn.failed" && r.TryGetProperty("error", out var fe) && fe.TryGetProperty("message", out var fm)) error = fm.GetString();
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(text) && File.Exists(outFile)) text = File.ReadAllText(outFile, Encoding.UTF8).Trim();
            if (!string.IsNullOrWhiteSpace(text)) return new Answer(text!, sid, false);
            if (!string.IsNullOrWhiteSpace(error)) return new Answer(error!, sid, true);
            return Fail(code, "", err, sid);
        }
        finally { try { File.Delete(outFile); } catch { } }
    }
}

/// <summary>Google Gemini CLI（gemini），用 Google 帳號登入。</summary>
internal sealed class GeminiProvider : AiProvider
{
    public override string Key => "gemini";
    public override string Name => "Gemini";
    public override string ToolName => "Gemini CLI";
    public override string InstallUrl => "https://github.com/google-gemini/gemini-cli";
    protected override string Command => "gemini";
    protected override string LoginArgs => "";
    public override string LoginHint => "喺彈出視窗揀「Sign in with Google」，瀏覽器會彈出嚟俾你登入。登入完可以閂咗嗰個視窗。";

    private static string GeminiDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
    private static string CredFile => Path.Combine(GeminiDir, "oauth_creds.json");

    /// <summary>Gemini CLI 冇「登入狀態」指令：睇 Google 登入檔或者 API key。</summary>
    public override Task<Auth> AuthAsync(string exe)
    {
        bool logged = File.Exists(CredFile) ||
                      !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY")) ||
                      !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_API_KEY"));
        return Task.FromResult(new Auth(logged, null));
    }

    public override Task<bool> LogoutAsync(string exe)
    {
        try { if (File.Exists(CredFile)) File.Delete(CredFile); return Task.FromResult(true); }
        catch { return Task.FromResult(false); }
    }

    public override async Task<Answer> AskAsync(string exe, string prompt, string? sessionId, Action<Process> started)
    {
        // 新對話自己定 session id，之後用佢接返上文
        string sid = sessionId ?? Guid.NewGuid().ToString();
        string args = "--output-format json --skip-trust " + (sessionId != null ? $"--resume {sid}" : $"--session-id {sid}");
        var (code, output, err) = await RunPromptAsync(exe, args, prompt, started);
        // {"session_id":"...","response":"...","stats":{...}}；出錯時 {"error":{"message":"..."}} 會喺 stderr
        try
        {
            string json = output.Contains('{') ? output : err;
            int brace = json.IndexOf('{');
            if (brace >= 0)
            {
                using var doc = JsonDocument.Parse(json[brace..(json.LastIndexOf('}') + 1)]);
                var r = doc.RootElement;
                if (r.TryGetProperty("session_id", out var s) && s.ValueKind == JsonValueKind.String) sid = s.GetString() ?? sid;
                if (r.TryGetProperty("response", out var t) && t.ValueKind == JsonValueKind.String && (t.GetString() ?? "").Length > 0)
                    return new Answer(t.GetString()!, sid, false);
                if (r.TryGetProperty("error", out var e))
                    return new Answer(e.TryGetProperty("message", out var m) ? m.GetString() ?? e.ToString() : e.ToString(), sessionId, true);
            }
        }
        catch { }
        return Fail(code, output, err, sessionId);
    }
}

/// <summary>「AI 助手」頁：連接用戶自己嘅 Claude Code / Codex / Gemini CLI 做日常問答。</summary>
internal sealed class AiPage : Panel
{
    private readonly Label _status;
    private readonly FancyButton _detect, _login, _newChat, _send;
    private readonly RichTextBox _log;
    private readonly TextBox _input;
    private readonly Panel _top, _bottom, _logHost, _side;
    private readonly ChatList _list;
    private AiProvider? _provider;   // null = 未揀平台
    private string? _session;
    private ChatRecord? _current;   // null = 未傳過嘢嘅新對話
    private Process? _running;
    private bool _stopped;
    private System.Windows.Forms.Timer? _poll;
    private DateTime _pollUntil;
    private AiProvider? _pollProvider;

    private static string ProviderFile => AiProvider.ProviderFile;

    public static Control Build() => new AiPage();

    private AiPage()
    {
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        Padding = new Padding(Ui.S(24), Ui.S(20), Ui.S(24), Ui.S(20));
        _provider = AiProvider.LoadCurrent();

        // ---- 頂行：狀態 + 掣 ----
        _top = new Panel { Dock = DockStyle.Top, Height = Ui.S(44), BackColor = Ui.Bg };
        var title = Ui.Text_("AI 助手", Ui.Text, Ui.F(12f, FontStyle.Bold), new Padding(0));
        title.Location = new Point(0, Ui.S(8));
        _status = Ui.Text_("偵測緊…", Ui.TextDim, Ui.F(9.5f), new Padding(0));
        _detect = Btn("重新偵測", BtnKind.Ghost, async () => await RefreshStatusAsync());
        _login = Btn("登入", BtnKind.Ghost, ShowPicker);
        _top.Controls.AddRange(new Control[] { title, _status, _detect, _login });
        _top.Layout += (_, _) =>
        {
            _status.Location = new Point(title.Right + Ui.S(14), Ui.S(13));
            int x = _top.ClientSize.Width;
            foreach (var b in new[] { _login, _detect })
            {
                x -= b.Width;
                b.Location = new Point(x, Ui.S(4));
                x -= Ui.S(8);
            }
        };

        // ---- 對話記錄 ----
        _log = new RichTextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Ui.Card,
            ForeColor = Ui.Text,
            Font = Ui.F(10.5f),
            DetectUrls = true,
            Dock = DockStyle.Fill,
            ScrollBars = RichTextBoxScrollBars.Vertical
        };
        _logHost = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Card, Padding = new Padding(Ui.S(16)) };
        _logHost.Controls.Add(_log);
        _log.LinkClicked += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.LinkText!) { UseShellExecute = true }); } catch { }
        };

        // ---- 輸入 ----
        _bottom = new Panel { Dock = DockStyle.Bottom, Height = Ui.S(92), BackColor = Ui.Bg, Padding = new Padding(0, Ui.S(12), 0, 0) };
        _input = new TextBox
        {
            Multiline = true,
            BorderStyle = BorderStyle.None,
            BackColor = Ui.Input,
            ForeColor = Ui.Text,
            Font = Ui.F(10.5f),
            ScrollBars = ScrollBars.Vertical
        };
        var inputHost = new Panel { BackColor = Ui.Input, Padding = new Padding(Ui.S(10)) };
        inputHost.Controls.Add(_input);
        _input.Dock = DockStyle.Fill;
        _send = Btn("傳送", BtnKind.Primary, async () => await SendAsync());
        _send.Size = new Size(Ui.S(96), Ui.S(80));
        _bottom.Controls.Add(inputHost);
        _bottom.Controls.Add(_send);
        _bottom.Layout += (_, _) =>
        {
            int h = _bottom.ClientSize.Height - _bottom.Padding.Top;
            _send.SetBounds(_bottom.ClientSize.Width - _send.Width, _bottom.Padding.Top, _send.Width, h);
            inputHost.SetBounds(0, _bottom.Padding.Top, _bottom.ClientSize.Width - _send.Width - Ui.S(10), h);
        };
        _input.KeyDown += async (_, e) =>
        {
            // Enter 傳送；Shift + Enter 換行
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                await SendAsync();
            }
        };

        // ---- 左邊：歷史對話 ----
        _side = new Panel { Dock = DockStyle.Left, Width = Ui.S(230), BackColor = Ui.Panel, Padding = new Padding(Ui.S(10)) };
        _newChat = Btn("＋ 新對話", BtnKind.Primary, NewChat);
        _newChat.Dock = DockStyle.Top;
        var histLabel = new Label
        {
            Text = "歷史對話", Dock = DockStyle.Top, Height = Ui.S(34), ForeColor = Ui.TextFaint, Font = Ui.F(9f),
            TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(Ui.S(4), 0, 0, Ui.S(6))
        };
        _list = new ChatList { Dock = DockStyle.Fill };
        _list.Picked += LoadChat;
        _list.DeleteRequested += DeleteChat;
        _list.RenameRequested += RenameChat;
        _side.Controls.Add(_list);
        _side.Controls.Add(histLabel);
        _side.Controls.Add(_newChat);
        var gap = new Panel { Dock = DockStyle.Left, Width = Ui.S(16), BackColor = Ui.Bg };

        Controls.Add(_logHost);
        Controls.Add(_bottom);
        Controls.Add(_top);
        Controls.Add(gap);
        Controls.Add(_side);
        _list.SetItems(ChatStore.LoadAll());

        Intro();
        HandleCreated += async (_, _) => await RefreshStatusAsync();
    }

    private void Intro() =>
        AppendSystem("呢度會用你自己部電腦已安裝、已登入嘅 AI（Claude Code / Codex CLI / Gemini CLI）回答問題，" +
                     "用嘅係你自己帳號嘅額度。本工具唔會接觸你嘅帳號或密碼。撳右上角「登入」揀用邊個。");

    private FancyButton Btn(string text, BtnKind kind, Action click)
    {
        var b = new FancyButton { Text = text, Kind = kind, Font = Ui.F(9.75f) };
        b.Size = new Size(Math.Max(Ui.S(84), b.GetPreferredSize(Size.Empty).Width), Ui.S(34));
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>而家用緊嘅 AI：搵唔搵到、登入咗未，更新右上角。</summary>
    private async Task RefreshStatusAsync()
    {
        var prov = _provider;
        if (prov == null)
        {
            _status.Text = "未登入";
            _status.ForeColor = Ui.Danger;
            UpdateButtons();
            return;
        }
        _status.Text = $"偵測緊 {prov.ToolName}…";
        _status.ForeColor = Ui.TextDim;
        string? exe = await Task.Run(prov.Find);
        if (prov != _provider) return;
        if (exe == null)
        {
            _status.Text = $"未安裝 {prov.ToolName}";
            _status.ForeColor = Ui.Danger;
            UpdateButtons();
            return;
        }
        var a = await prov.AuthAsync(exe);
        if (prov != _provider) return;
        if (a.LoggedIn == true)
        {
            _status.Text = $"已登入 {prov.Name}" + (a.Plan is { Length: > 0 } pl ? " - " + pl : "");
            _status.ForeColor = Ui.Success;
        }
        else if (a.LoggedIn == false)
        {
            _status.Text = $"未登入 {prov.Name}";
            _status.ForeColor = Ui.Danger;
        }
        else
        {
            _status.Text = $"睇唔到 {prov.Name} 登入狀態";
            _status.ForeColor = Ui.TextDim;
        }
        UpdateButtons();
    }

    private void SetProvider(AiProvider p)
    {
        if (_provider == p) return;
        _provider = p;
        try { File.WriteAllText(ProviderFile, p.Key); } catch { }
        _ = RefreshStatusAsync();
    }

    /// <summary>撳「登入」：彈小視窗揀 Claude / ChatGPT / Gemini。</summary>
    private void ShowPicker()
    {
        if (_running != null) return;
        using var dlg = new ProviderPicker(_provider);
        dlg.UseRequested += p =>
        {
            SetProvider(p);
            if (_current != null && _current.Provider != p.Key) NewChat();
        };
        dlg.LoginRequested += (p, exe) =>
        {
            try
            {
                p.OpenLoginTerminal(exe);
                StartLoginPoll(p, exe);
                AppendSystem($"已經開咗 {p.ToolName} 嘅登入視窗。{p.LoginHint}登入完呢度會自動更新。");
            }
            catch (Exception ex) { AppendSystem("開唔到終端機：" + ex.Message); }
        };
        dlg.LoggedOut += p =>
        {
            AppendSystem($"已登出 {p.Name}。");
            if (p == _provider) _ = RefreshStatusAsync();
        };
        dlg.ShowDialog(FindForm());
    }

    /// <summary>撳完登入之後每 3 秒問一次，登入咗就轉用嗰個（最多等 5 分鐘）。</summary>
    private void StartLoginPoll(AiProvider p, string exe)
    {
        _pollProvider = p;
        _pollUntil = DateTime.Now.AddMinutes(5);
        if (_poll == null)
        {
            _poll = new System.Windows.Forms.Timer { Interval = 3000 };
            bool checking = false;
            _poll.Tick += async (_, _) =>
            {
                if (checking || _pollProvider == null) return;
                checking = true;
                try
                {
                    var prov = _pollProvider;
                    string? x = prov.Find();
                    var a = x != null ? await prov.AuthAsync(x) : null;
                    if (a?.LoggedIn == true)
                    {
                        _poll!.Stop();
                        _pollProvider = null;
                        SetProvider(prov);
                        await RefreshStatusAsync();
                        AppendSystem($"{prov.Name} 登入成功，可以開始問嘢喇。嗰個終端機視窗可以閂咗。");
                    }
                    else if (DateTime.Now > _pollUntil) _poll!.Stop();
                }
                finally { checking = false; }
            };
        }
        _poll.Start();
    }

    private void NewChat()
    {
        if (_running != null) return;
        _current = null;
        _session = null;
        _list.Select(null);
        _log.Clear();
        AppendSystem(_provider != null ? $"新對話開始（用 {_provider.Name}）。" : "新對話開始。");
        _input.Focus();
    }

    private void LoadChat(ChatRecord rec)
    {
        if (_running != null) { _list.Select(_current); return; }
        _current = rec;
        _session = rec.SessionId;
        SetProvider(AiProvider.Get(rec.Provider));   // 舊對話用返佢原本嘅 AI
        _log.Clear();
        AppendSystem($"{rec.Title}　·　{AiProvider.Get(rec.Provider).Name}　·　{rec.Updated:yyyy-MM-dd HH:mm}");
        foreach (var m in rec.Messages) Render(m, AiProvider.Get(rec.Provider));
        _input.Focus();
    }

    private void DeleteChat(ChatRecord rec)
    {
        if (_running != null && rec == _current) return;
        if (MessageBox.Show(FindForm(), $"刪除對話「{rec.Title}」？", Program.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        ChatStore.Delete(rec);
        _list.Remove(rec);
        if (rec == _current) NewChat();
    }

    private void RenameChat(ChatRecord rec)
    {
        string? name = RenameDialog.Ask(FindForm(), rec.Title);
        if (string.IsNullOrWhiteSpace(name)) return;
        rec.Title = name.Trim();
        ChatStore.Save(rec);
        _list.Invalidate();
        if (rec == _current) LoadChat(rec);
    }

    private void Render(ChatMsg m, AiProvider p)
    {
        switch (m.Role)
        {
            case "user": AppendMessage("你", m.Text, Ui.Accent); break;
            case "error": AppendMessage("錯誤", m.Text, Ui.Danger); break;
            default: AppendMessage(p.Name, m.Text, Ui.Success); break;
        }
    }

    private void UpdateButtons()
    {
        bool busy = _running != null;
        _send.Text = busy ? "停止" : "傳送";
        _send.Kind = busy ? BtnKind.Danger : BtnKind.Primary;
        _send.Enabled = true;
        _newChat.Enabled = !busy;
        _detect.Enabled = !busy;
        _login.Enabled = !busy;
        _input.ReadOnly = busy;
        _send.Invalidate();
        _top.PerformLayout();
    }

    private async Task SendAsync()
    {
        if (_running != null)
        {
            _stopped = true;
            try { _running.Kill(true); } catch { }
            return;
        }
        var prov = _current != null ? AiProvider.Get(_current.Provider) : _provider;
        if (prov == null)
        {
            AppendSystem("未揀用邊個 AI：撳右上角「登入」揀 Claude / ChatGPT / Gemini。");
            return;
        }
        string? exe = prov.Find();
        if (exe == null)
        {
            AppendSystem($"搵唔到 {prov.ToolName}。撳右上角「登入」睇安裝方法，安裝完撳「重新偵測」。");
            return;
        }
        string q = _input.Text.Trim();
        if (q.Length == 0) return;
        _input.Clear();
        _stopped = false;
        if (_current == null)
        {
            _current = ChatRecord.New(q, prov.Key);
            _list.Add(_current);
        }
        var chat = _current;
        chat.Messages.Add(new ChatMsg { Role = "user", Text = q });
        chat.Updated = DateTime.Now;
        ChatStore.Save(chat);
        _list.Touch(chat);
        AppendMessage("你", q, Ui.Accent);
        int thinkingStart = _log.TextLength;
        AppendSystem($"{prov.Name} 諗緊…");
        int thinkingEnd = _log.TextLength;

        AiProvider.Answer ans;
        try
        {
            ans = await prov.AskAsync(exe, q, _session, p => { _running = p; BeginInvoke(UpdateButtons); });
            // 舊對話喺工具嗰邊已經清咗，就當新對話再問一次
            if (ans.IsError && !_stopped && _session != null && AiProvider.IsSessionGone(ans.Text))
            {
                _session = null;
                ans = await prov.AskAsync(exe, q, null, p => { _running = p; BeginInvoke(UpdateButtons); });
                if (!ans.IsError) ans = ans with { Text = "（舊對話嘅上文已過期，呢個回答唔記得之前講過乜）\n\n" + ans.Text };
            }
        }
        catch (Exception ex)
        {
            ans = new AiProvider.Answer("出錯：" + ex.Message, null, true);
        }
        _running = null;
        UpdateButtons();

        // 拎走「諗緊…」
        try
        {
            _log.Select(thinkingStart, thinkingEnd - thinkingStart);
            _log.ReadOnly = false;
            _log.SelectedText = "";
            _log.ReadOnly = true;
        }
        catch { }

        if (ans.SessionId != null) _session = ans.SessionId;
        chat.SessionId = _session;
        if (!_stopped)
        {
            chat.Messages.Add(new ChatMsg { Role = ans.IsError ? "error" : "ai", Text = ans.Text });
            chat.Updated = DateTime.Now;
        }
        ChatStore.Save(chat);
        _list.Touch(chat);
        if (chat != _current) return;
        if (_stopped) AppendSystem("已停止。");
        else if (ans.IsError)
        {
            string hint = ans.Text.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                          ans.Text.Contains("auth", StringComparison.OrdinalIgnoreCase)
                ? "\n（未登入或者登入過期：撳右上角「登入」）"
                : "";
            AppendMessage("錯誤", ans.Text + hint, Ui.Danger);
            _ = RefreshStatusAsync();
        }
        else AppendMessage(prov.Name, ans.Text, Ui.Success);
        _input.Focus();
    }

    private void AppendMessage(string who, string text, Color whoColor)
    {
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = whoColor;
        _log.SelectionFont = Ui.F(10f, FontStyle.Bold);
        _log.AppendText((_log.TextLength > 0 ? "\n" : "") + who + "\n");
        _log.SelectionColor = Ui.Text;
        _log.SelectionFont = Ui.F(10.5f);
        _log.AppendText(text.Replace("\r\n", "\n").TrimEnd() + "\n");
        _log.ScrollToCaret();
    }

    private void AppendSystem(string text)
    {
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = Ui.TextFaint;
        _log.SelectionFont = Ui.F(9.5f);
        _log.AppendText((_log.TextLength > 0 ? "\n" : "") + text + "\n");
        _log.ScrollToCaret();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _poll?.Dispose(); try { _running?.Kill(true); } catch { } }
        base.Dispose(disposing);
    }
}

/// <summary>「登入」小視窗：Claude / ChatGPT / Gemini，各自顯示狀態同掣。</summary>
internal sealed class ProviderPicker : Form
{
    public event Action<AiProvider>? UseRequested, LoggedOut;
    public event Action<AiProvider, string>? LoginRequested;
    private readonly AiProvider? _active;

    public ProviderPicker(AiProvider? active)
    {
        _active = active;
        Text = "登入";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        int rowH = Ui.S(64);
        ClientSize = new Size(Ui.S(440), Ui.S(52) + rowH * AiProvider.All.Length + Ui.S(12));
        Controls.Add(new Label
        {
            Text = "揀要用嘅 AI（用你自己部電腦安裝咗嘅工具同帳號）",
            ForeColor = Ui.TextDim, Font = Ui.F(9.5f), AutoSize = false,
            Bounds = new Rectangle(Ui.S(16), Ui.S(14), Ui.S(408), Ui.S(24))
        });
        int y = Ui.S(48);
        foreach (var p in AiProvider.All)
        {
            Controls.Add(new Row(this, p, new Rectangle(Ui.S(12), y, Ui.S(416), rowH - Ui.S(8))));
            y += rowH;
        }
    }

    private sealed class Row : Panel
    {
        private readonly ProviderPicker _owner;
        private readonly AiProvider _p;
        private readonly Label _name, _state;
        private readonly FancyButton _main, _logout;
        private string? _exe;
        private bool _loggedIn;

        public Row(ProviderPicker owner, AiProvider p, Rectangle bounds)
        {
            _owner = owner;
            _p = p;
            Bounds = bounds;
            BackColor = Ui.CardHi;
            _name = new Label
            {
                Text = p.Name + (p == owner._active ? "（使用中）" : ""), Font = Ui.F(10.5f, FontStyle.Bold), ForeColor = Ui.Text,
                AutoSize = false, Bounds = new Rectangle(Ui.S(12), Ui.S(8), Ui.S(200), Ui.S(22))
            };
            _state = new Label
            {
                Text = $"{p.ToolName} · 檢查緊…", Font = Ui.F(8.75f), ForeColor = Ui.TextFaint,
                AutoSize = false, Bounds = new Rectangle(Ui.S(12), Ui.S(31), Ui.S(220), Ui.S(18))
            };
            _main = new FancyButton { Text = "…", Kind = BtnKind.Primary, Font = Ui.F(9.5f), Enabled = false };
            _logout = new FancyButton { Text = "登出", Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Visible = false };
            _main.SetBounds(bounds.Width - Ui.S(96), Ui.S(12), Ui.S(84), Ui.S(32));
            _logout.SetBounds(bounds.Width - Ui.S(188), Ui.S(12), Ui.S(84), Ui.S(32));
            _main.Click += (_, _) => Main();
            _logout.Click += async (_, _) => await LogoutAsync();
            Controls.AddRange(new Control[] { _name, _state, _main, _logout });
            HandleCreated += async (_, _) => await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            _exe = await Task.Run(_p.Find);
            if (IsDisposed) return;
            if (_exe == null)
            {
                _state.Text = $"未安裝 {_p.ToolName}";
                _state.ForeColor = Ui.Danger;
                _main.Text = "安裝方法";
                _main.Kind = BtnKind.Ghost;
                _main.Enabled = true;
                _main.Invalidate();
                return;
            }
            var a = await _p.AuthAsync(_exe);
            if (IsDisposed) return;
            _loggedIn = a.LoggedIn == true;
            if (_loggedIn)
            {
                _state.Text = "已登入" + (a.Plan is { Length: > 0 } pl ? " - " + pl : "");
                _state.ForeColor = Ui.Success;
                _main.Text = "使用";
                _main.Enabled = _p != _owner._active;
                _logout.Visible = true;
            }
            else
            {
                _state.Text = a.LoggedIn == false ? "未登入" : "睇唔到登入狀態";
                _state.ForeColor = a.LoggedIn == false ? Ui.TextDim : Ui.TextFaint;
                _main.Text = "登入";
                _main.Enabled = true;
                _logout.Visible = false;
            }
            _main.Invalidate();
        }

        private void Main()
        {
            if (_exe == null)
            {
                try { Process.Start(new ProcessStartInfo(_p.InstallUrl) { UseShellExecute = true }); } catch { }
                return;
            }
            if (_loggedIn) _owner.UseRequested?.Invoke(_p);
            else _owner.LoginRequested?.Invoke(_p, _exe);
            _owner.Close();
        }

        private async Task LogoutAsync()
        {
            if (_exe == null) return;
            if (MessageBox.Show(_owner, $"確定要登出 {_p.Name}？\n（{_p.ToolName} 本身都會一齊登出）", Program.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _logout.Enabled = false;
            bool ok = await _p.LogoutAsync(_exe);
            _logout.Enabled = true;
            if (!ok)
            {
                MessageBox.Show(_owner, "登出失敗。", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _owner.LoggedOut?.Invoke(_p);
            await RefreshAsync();
        }
    }
}

/// <summary>一句對話。Role：user / claude / error。</summary>
internal sealed class ChatMsg
{
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
}

/// <summary>一個對話：存喺 data\\ai\\chats\\&lt;Id&gt;.json。</summary>
internal sealed class ChatRecord
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? SessionId { get; set; }
    public string Provider { get; set; } = "claude";
    public DateTime Updated { get; set; }
    public List<ChatMsg> Messages { get; set; } = new();

    public static ChatRecord New(string firstQuestion, string provider)
    {
        string t = firstQuestion.Replace("\r", " ").Replace("\n", " ").Trim();
        if (t.Length > 30) t = t[..30] + "…";
        return new ChatRecord
        {
            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            Title = t,
            Provider = provider,
            Updated = DateTime.Now
        };
    }
}

internal static class ChatStore
{
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    private static string Dir
    {
        get
        {
            var d = Path.Combine(AiProvider.WorkDir, "chats");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string FileOf(ChatRecord r) => Path.Combine(Dir, r.Id + ".json");

    public static List<ChatRecord> LoadAll()
    {
        var list = new List<ChatRecord>();
        try
        {
            foreach (var f in Directory.GetFiles(Dir, "*.json"))
            {
                try
                {
                    var r = JsonSerializer.Deserialize<ChatRecord>(File.ReadAllText(f, Encoding.UTF8), Opt);
                    if (r != null && r.Id.Length > 0) list.Add(r);
                }
                catch (Exception ex) { AppPaths.LogError("讀對話 " + Path.GetFileName(f), ex); }
            }
        }
        catch { }
        return list.OrderByDescending(r => r.Updated).ToList();
    }

    public static void Save(ChatRecord r)
    {
        try
        {
            string f = FileOf(r), tmp = f + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(r, Opt), new UTF8Encoding(false));
            File.Move(tmp, f, true);
        }
        catch (Exception ex) { AppPaths.LogError("儲存對話", ex); }
    }

    public static void Delete(ChatRecord r)
    {
        try { File.Delete(FileOf(r)); } catch (Exception ex) { AppPaths.LogError("刪除對話", ex); }
    }
}

/// <summary>左邊歷史對話清單（最新喺最上）。右鍵：重新命名 / 刪除。</summary>
internal sealed class ChatList : ListBox
{
    private readonly ContextMenuStrip _menu = new();
    private int _hover = -1;
    private ChatRecord? _menuItem;
    private bool _silent;

    public event Action<ChatRecord>? Picked, DeleteRequested, RenameRequested;

    public ChatList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = Ui.S(50);
        BorderStyle = BorderStyle.None;
        BackColor = Ui.Panel;
        ForeColor = Ui.Text;
        IntegralHeight = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        try { _menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        _menu.BackColor = Ui.CardHi;
        _menu.ForeColor = Ui.Text;
        _menu.Items.Add("重新命名", null, (_, _) => { if (_menuItem != null) RenameRequested?.Invoke(_menuItem); });
        _menu.Items.Add("刪除", null, (_, _) => { if (_menuItem != null) DeleteRequested?.Invoke(_menuItem); });
    }

    public void SetItems(IEnumerable<ChatRecord> items)
    {
        BeginUpdate();
        Items.Clear();
        foreach (var r in items) Items.Add(r);
        EndUpdate();
    }

    public void Add(ChatRecord r)
    {
        Items.Insert(0, r);
        Select(r);
    }

    public void Remove(ChatRecord r) => Items.Remove(r);

    /// <summary>有新訊息：推返去最上面。</summary>
    public void Touch(ChatRecord r)
    {
        int i = Items.IndexOf(r);
        if (i < 0) return;
        bool wasSelected = SelectedItem == r;
        if (i > 0)
        {
            _silent = true;
            Items.RemoveAt(i);
            Items.Insert(0, r);
            if (wasSelected) SelectedIndex = 0;
            _silent = false;
        }
        Invalidate();
    }

    public void Select(ChatRecord? r)
    {
        _silent = true;
        SelectedIndex = r == null ? -1 : Items.IndexOf(r);
        _silent = false;
        Invalidate();
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        if (!_silent && SelectedItem is ChatRecord r) Picked?.Invoke(r);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            int i = IndexFromPoint(e.Location);
            if (i >= 0 && GetItemRectangle(i).Contains(e.Location))
            {
                _menuItem = (ChatRecord)Items[i];
                _menu.Show(this, e.Location);
            }
            return;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexFromPoint(e.Location);
        if (i >= 0 && !GetItemRectangle(i).Contains(e.Location)) i = -1;
        if (i != _hover) { _hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; Invalidate(); }
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var g = e.Graphics;
        var b = e.Bounds;
        using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, b);
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var r = (ChatRecord)Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var card = new Rectangle(b.X, b.Y + Ui.S(2), b.Width - 1, b.Height - Ui.S(4));
        if (sel || e.Index == _hover)
        {
            using var hb = new SolidBrush(sel ? Ui.CardHi : Ui.Card);
            g.FillRectangle(hb, card);
        }
        if (sel)
        {
            using var ab = new SolidBrush(Ui.Accent);
            g.FillRectangle(ab, card.X, card.Y + Ui.S(8), Ui.S(3), card.Height - Ui.S(16));
        }
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        int x = card.X + Ui.S(12), w = card.Width - Ui.S(20);
        TextRenderer.DrawText(g, r.Title, Ui.F(9.75f), new Rectangle(x, card.Y + Ui.S(6), w, Ui.S(20)), sel ? Ui.Text : Ui.TextDim, flags);
        string sub = AiProvider.Get(r.Provider).Name + " · " +
                     (r.Updated.Date == DateTime.Today ? r.Updated.ToString("HH:mm") : r.Updated.ToString("yyyy-MM-dd"));
        TextRenderer.DrawText(g, sub, Ui.F(8.25f), new Rectangle(x, card.Y + Ui.S(26), w, Ui.S(16)), Ui.TextFaint, flags);
    }
}

/// <summary>重新命名用嘅細視窗。</summary>
internal sealed class RenameDialog : Form
{
    private readonly TextBox _box;

    private RenameDialog(string current)
    {
        Text = "重新命名";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Ui.S(360), Ui.S(116));
        _box = new TextBox
        {
            Text = current, Font = Ui.F(10.5f), BackColor = Ui.Input, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle,
            Bounds = new Rectangle(Ui.S(16), Ui.S(16), Ui.S(328), Ui.S(30))
        };
        var ok = new FancyButton { Text = "確定", Kind = BtnKind.Primary, Font = Ui.F(9.75f),
            Bounds = new Rectangle(Ui.S(164), Ui.S(66), Ui.S(84), Ui.S(34)) };
        var cancel = new FancyButton { Text = "取消", Kind = BtnKind.Ghost, Font = Ui.F(9.75f),
            Bounds = new Rectangle(Ui.S(260), Ui.S(66), Ui.S(84), Ui.S(34)) };
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DialogResult = DialogResult.OK; Close(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; DialogResult = DialogResult.Cancel; Close(); }
        };
        Controls.AddRange(new Control[] { _box, ok, cancel });
        Shown += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(IWin32Window? owner, string current)
    {
        using var d = new RenameDialog(current);
        return d.ShowDialog(owner) == DialogResult.OK ? d._box.Text : null;
    }
}
