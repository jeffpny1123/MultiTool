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
    /// <summary>AI 一律唔准讀寫嘅私人位置（Claude 權限規則：~ = 用戶資料夾）。</summary>
    protected static readonly string[] PrivatePaths =
    {
        "~/.ssh/**", "~/.aws/**", "~/.azure/**", "~/.config/gcloud/**", "~/.gnupg/**", "~/.claude/**", "~/.codex/**", "~/.gemini/**",
        "~/AppData/Roaming/Microsoft/Credentials/**", "~/AppData/Local/Google/Chrome/User Data/**", "~/AppData/Local/Microsoft/Edge/User Data/**",
        "~/AppData/Roaming/Mozilla/Firefox/Profiles/**", "~/AppData/Roaming/discord/**", "~/AppData/Roaming/Telegram Desktop/**"
    };

    public abstract string Key { get; }          // 存檔用：claude / codex / gemini
    public abstract string Name { get; }         // 顯示：Claude / ChatGPT / Gemini
    public abstract string ToolName { get; }     // Claude Code / Codex CLI / Gemini CLI
    public abstract string InstallUrl { get; }
    protected abstract string Command { get; }   // claude / codex / gemini
    public virtual string LoginHint => Lang.T("瀏覽器會彈出嚟俾你登入（如果冇彈，跟視窗入面嘅指示做）。");

    public static readonly AiProvider[] All = { new ClaudeProvider(), new CodexProvider(), new GeminiProvider(), new LocalProvider() };
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
    public virtual string? Find()
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

    protected static ProcessStartInfo Start(string exe, string args, string? cwd = null)
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
            WorkingDirectory = cwd != null && Directory.Exists(cwd) ? cwd : WorkDir
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
    protected static async Task<(int Code, string Out, string Err)> RunPromptAsync(string exe, string args, string prompt, Action<Process> started, string? cwd = null)
    {
        using var p = new Process { StartInfo = Start(exe, args, cwd) };
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
        return new Answer(msg.Length > 0 ? msg : Lang.F("程式結束咗（代碼 {0}），冇回覆。", code), sid, true);
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
    /// <param name="quick">語音對話：用快啲嘅設定、唔留對話紀錄。</param>
    /// <param name="files">附件（已經用 AiFiles.Prepare 複製好）。</param>
    public abstract Task<Answer> AskAsync(string exe, string prompt, string? sessionId, Action<Process> started, bool quick = false, string? folder = null, IReadOnlyList<string>? files = null);
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

    public override async Task<Answer> AskAsync(string exe, string prompt, string? sessionId, Action<Process> started, bool quick = false, string? folder = null, IReadOnlyList<string>? files = null)
    {
        // 冇連接資料夾：唔俾佢改檔案 / 行指令。
        // 連接咗：讀邊度都得（私人位置除外），但淨係寫得入嗰個資料夾（工作目錄）。
        // • 寫：只批准 Edit(./**)（= 工作目錄入面，Write / MultiEdit 都跟呢條規則）；外面嘅修改冇人批准，-p 模式會直接拒絕
        // • 一律唔俾行指令（Bash 可以繞過以上限制）；唔俾讀網頁（防止被網頁 / 檔案入面嘅指示呃咗，將資料傳出去）
        bool att = files is { Count: > 0 };
        string tools = folder != null
            ? " --permission-mode default --allowedTools Read Glob Grep TodoWrite WebSearch \"Edit(./**)\"" +
              " --disallowedTools Bash WebFetch NotebookEdit " + string.Join(" ", PrivatePaths.Select(p => $"\"Read({p})\" \"Edit({p})\""))
            : att
                // 有附件：准讀檔（私人位置除外），但唔准讀網頁（費事附件入面嘅指示將資料傳出去）
                ? " --disallowedTools Bash Edit Write MultiEdit NotebookEdit WebFetch " + string.Join(" ", PrivatePaths.Select(p => $"\"Read({p})\"")) +
                  " --allowedTools WebSearch Read"
                : " --disallowedTools Bash Edit Write MultiEdit NotebookEdit --allowedTools WebSearch WebFetch";
        if (att)
        {
            tools += string.Concat(AiFiles.Dirs(files!).Select(d => $" --add-dir \"{d}\""));
            prompt += AiFiles.Note(files!, "用 Read 工具讀取；圖片、PDF、文字檔都讀得");
        }
        string args = "-p --output-format json" +
                      (sessionId != null ? $" --resume {sessionId}" : "") +
                      (quick ? " --model haiku --no-session-persistence" : "") + tools;
        var (code, output, err) = await RunPromptAsync(exe, args, prompt, started, folder);
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
                return new Answer(text.Length > 0 ? text : Lang.T("(冇回覆)"), sid, isErr);
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
    public override string LoginHint => Lang.T("瀏覽器會彈出嚟俾你用 ChatGPT 帳號登入（如果冇彈，跟視窗入面嘅指示做）。");

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

    public override async Task<Answer> AskAsync(string exe, string prompt, string? sessionId, Action<Process> started, bool quick = false, string? folder = null, IReadOnlyList<string>? files = null)
    {
        string outFile = Path.Combine(WorkDir, "codex_" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        // 唯讀 sandbox：唔俾佢改檔案
        // 連接咗資料夾：可以喺嗰個資料夾寫檔（workspace-write sandbox）；冇就唯讀
        string mode = folder != null ? "workspace-write" : "read-only";
        // workspace-write：淨係寫得入工作目錄；sandbox 入面唔准上網、唔使問就唔行指令
        if (folder != null) mode += " -c sandbox_workspace_write.network_access=false -c approval_policy=never";
        string common = $"--json --skip-git-repo-check -c sandbox_mode={mode} -o \"{outFile}\"" + (quick ? " --ephemeral" : "");
        // --search：俾佢上網搵資料（唔使逐次批准）
        // 附件：新對話嘅圖片用 -i 直接俾佢睇；其他（同接續對話）講明路徑，佢自己用唯讀 shell 讀
        string imgs = "";
        if (files is { Count: > 0 })
        {
            var pics = files.Where(AiFiles.IsImage).ToList();
            if (sessionId == null && pics.Count > 0) imgs = string.Concat(pics.Select(f => $"-i \"{f}\" "));
            prompt += AiFiles.Note(files, sessionId == null && pics.Count > 0 ? "圖片已經附上；其他檔案用唯讀指令讀取" : "用唯讀指令讀取；圖片可以用睇圖工具睇");
        }
        string args = sessionId != null ? $"--search exec resume {common} {sessionId} -" : $"--search exec {imgs}{common} -";
        try
        {
            var (code, output, err) = await RunPromptAsync(exe, args, prompt, started, folder);
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
    public override string LoginHint => Lang.T("喺彈出視窗揀「Sign in with Google」，瀏覽器會彈出嚟俾你登入。登入完可以閂咗嗰個視窗。");

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

    public override async Task<Answer> AskAsync(string exe, string prompt, string? sessionId, Action<Process> started, bool quick = false, string? folder = null, IReadOnlyList<string>? files = null)
    {
        // 新對話自己定 session id，之後用佢接返上文
        string sid = sessionId ?? Guid.NewGuid().ToString();
        string args = "--output-format json --skip-trust " + (sessionId != null ? $"--resume {sid}" : $"--session-id {sid}") +
                      (folder != null ? " --approval-mode auto_edit" : "");
        // 附件：@路徑 = 將檔案內容（圖片、PDF、文字）附入問題
        if (files is { Count: > 0 })
        {
            args += " --include-directories " + string.Join(",", AiFiles.Dirs(files).Select(d => $"\"{d}\""));
            prompt += AiFiles.Note(files, "內容已經用 @ 附上") + "\n" + string.Join("\n", files.Select(f => "@" + f.Replace('\\', '/')));
        }
        // 連接咗資料夾：只自動批准改檔（Gemini 只准改工作區 = 嗰個資料夾入面）；行指令要批准，冇人批准就唔會行
        var (code, output, err) = await RunPromptAsync(exe, args, prompt, started, folder);
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
    private readonly FancyButton _detect, _login, _newChat, _send, _settings, _attachBtn;
    private readonly AttachBar _attach = new();
    private readonly System.Windows.Forms.Timer _voiceKey = new() { Interval = 30 };
    private bool _voiceWasDown;
    private readonly RichTextBox _log;
    private readonly TextBox _input;
    private readonly Panel _top, _bottom, _logHost, _side;
    private readonly ChatList _list;
    private AiProvider? _provider;   // null = 未揀平台
    private string? _session;
    private ChatRecord? _current;   // null = 未傳過嘢嘅新對話
    private string? _pendingFolder; // 新對話未傳嘢之前揀咗嘅資料夾
    private FancyButton _folderBtn = null!;
    private string? Folder => _current != null ? _current.Folder : _pendingFolder;
    private readonly ToolTip _tipAttach = new();
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
        _top = new Panel { Dock = DockStyle.Top, Height = Ui.S(92), BackColor = Ui.Bg };
        var title = Ui.Text_(Lang.T("AI 助手"), Ui.Text, Ui.F(12f, FontStyle.Bold), new Padding(0));
        title.Location = new Point(0, Ui.S(8));
        _status = Ui.Text_(Lang.T("偵測緊…"), Ui.TextDim, Ui.F(9.5f), new Padding(0));
        _detect = Btn(Lang.T("重新偵測"), BtnKind.Ghost, async () => await RefreshStatusAsync());
        _login = Btn(Lang.T("帳號"), BtnKind.Ghost, ShowPicker);
        _settings = Btn(Lang.T("語音設定"), BtnKind.Ghost, () =>
        {
            using var dlg = new AiSettingsDialog();
            dlg.ShowDialog(FindForm());
        });
        _folderBtn = Btn("", BtnKind.Ghost, ShowFolderMenu);
        UpdateFolderButton();
        _top.Controls.AddRange(new Control[] { title, _status, _detect, _login, _settings, _folderBtn });
        _top.Layout += (_, _) =>
        {
            _status.Location = new Point(title.Right + Ui.S(14), Ui.S(13));
            int x = _top.ClientSize.Width;
            // 資料夾掣放第二行（左邊），唔會同狀態字疊埋
            _folderBtn.Location = new Point(0, Ui.S(50));
            foreach (var b in new[] { _settings, _login, _detect })
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
        _send = Btn(Lang.T("傳送"), BtnKind.Primary, async () => await SendAsync());
        _send.Size = new Size(Ui.S(96), Ui.S(80));
        // 附件：📎 揀檔案；亦可以拖檔案入嚟或者 Ctrl+V 貼圖
        _attachBtn = Btn("📎", BtnKind.Ghost, () => _attach.Pick(FindForm()));
        _attachBtn.Size = new Size(Ui.S(44), Ui.S(80));
        _tipAttach.SetToolTip(_attachBtn, Lang.T("附加圖片或文件（亦可以拖入嚟，或者 Ctrl+V 貼上截圖）"));
        _bottom.Controls.Add(_attach);
        _bottom.Controls.Add(inputHost);
        _bottom.Controls.Add(_attachBtn);
        _bottom.Controls.Add(_send);
        _bottom.Layout += (_, _) =>
        {
            int top = _bottom.Padding.Top;
            int w = _bottom.ClientSize.Width - _send.Width - _attachBtn.Width - Ui.S(20);
            if (_attach.Visible) { _attach.SetBounds(0, top, w, _attach.Height); top += _attach.Height; }
            int h = _bottom.ClientSize.Height - top;
            _send.SetBounds(_bottom.ClientSize.Width - _send.Width, top, _send.Width, h);
            _attachBtn.SetBounds(_send.Left - Ui.S(10) - _attachBtn.Width, top, _attachBtn.Width, h);
            inputHost.SetBounds(0, top, w, h);
        };
        _attach.Changed += () =>
        {
            _attach.Width = Math.Max(Ui.S(100), _bottom.ClientSize.Width - _send.Width - _attachBtn.Width - Ui.S(20));
            _bottom.Height = Ui.S(92) + (_attach.Visible ? _attach.Height : 0);
            _bottom.PerformLayout();
        };
        _attach.Wire(_input, _log, _logHost);
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
        _newChat = Btn(Lang.T("＋ 新對話"), BtnKind.Primary, NewChat);
        _newChat.Dock = DockStyle.Top;
        var histLabel = new Label
        {
            Text = Lang.T("歷史對話"), Dock = DockStyle.Top, Height = Ui.S(34), ForeColor = Ui.TextFaint, Font = Ui.F(9f),
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
        _ = Task.Run(AiFiles.Cleanup); // 清走 30 日前嘅附件

        // 語音對話快捷鍵（喺邊度都用得）
        _voiceKey.Tick += (_, _) =>
        {
            bool down = VoiceSettings.Enabled && !MultiTool.Common.HotkeyBox.Busy && VoiceSettings.Hotkey.IsDown();
            bool was = _voiceWasDown;
            _voiceWasDown = down;
            if (!down || was || MultiTool.Common.Keyboard.RecentlyInjected) return;
            if (Form.ActiveForm is AiSettingsDialog) return; // 設定緊快捷鍵
            VoiceChat.Begin();
        };
        _voiceKey.Start();
    }

    private void Intro() =>
        AppendSystem(Lang.T("呢度會用你自己部電腦已安裝、已登入嘅 AI（Claude Code / Codex CLI / Gemini CLI）回答問題，") +
                     Lang.T("用嘅係你自己帳號嘅額度。本工具唔會接觸你嘅帳號或密碼。撳右上角「登入」揀用邊個。"));

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
            _status.Text = Lang.T("未登入");
            _status.ForeColor = Ui.Danger;
            UpdateButtons();
            return;
        }
        _status.Text = Lang.F("偵測緊 {0}…", prov.ToolName);
        _status.ForeColor = Ui.TextDim;
        string? exe = await Task.Run(prov.Find);
        if (prov != _provider) return;
        if (exe == null)
        {
            _status.Text = Lang.F("未安裝 {0}", prov.ToolName);
            _status.ForeColor = Ui.Danger;
            UpdateButtons();
            return;
        }
        var a = await prov.AuthAsync(exe);
        if (prov != _provider) return;
        if (a.LoggedIn == true)
        {
            _status.Text = Lang.F("已登入 {0}", prov.Name) + (a.Plan is { Length: > 0 } pl ? " - " + pl : "");
            _status.ForeColor = Ui.Success;
        }
        else if (a.LoggedIn == false)
        {
            _status.Text = Lang.F("未登入 {0}", prov.Name);
            _status.ForeColor = Ui.Danger;
        }
        else
        {
            _status.Text = Lang.F("睇唔到 {0} 登入狀態", prov.Name);
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
            _ = RefreshStatusAsync();
        };
        dlg.LoginRequested += (p, exe) =>
        {
            try
            {
                p.OpenLoginTerminal(exe);
                StartLoginPoll(p, exe);
                AppendSystem(Lang.F("已經開咗 {0} 嘅登入視窗。{1}登入完呢度會自動更新。", p.ToolName, p.LoginHint));
            }
            catch (Exception ex) { AppendSystem(Lang.T("開唔到終端機：") + ex.Message); }
        };
        dlg.LoggedOut += p =>
        {
            AppendSystem(Lang.F("已登出 {0}。", p.Name));
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
                        AppendSystem(Lang.F("{0} 登入成功，可以開始問嘢喇。嗰個終端機視窗可以閂咗。", prov.Name));
                    }
                    else if (DateTime.Now > _pollUntil) _poll!.Stop();
                }
                finally { checking = false; }
            };
        }
        _poll.Start();
    }

    // ---------- 連接資料夾 ----------
    private void UpdateFolderButton()
    {
        var f = Folder;
        _folderBtn.Text = f == null ? Lang.T("資料夾：未連接（只可以對話）  ▾") : Lang.T("資料夾：") + f + "  ▾";
        _folderBtn.Kind = f == null ? BtnKind.Ghost : BtnKind.Primary;
        _folderBtn.Size = new Size(Math.Min(Ui.S(520), Math.Max(Ui.S(84), _folderBtn.GetPreferredSize(Size.Empty).Width + Ui.S(16))), Ui.S(34));
        _folderBtn.Invalidate();
        _top?.PerformLayout();
    }

    private void SetFolder(string? f)
    {
        if (_current != null) { _current.Folder = f; ChatStore.Save(_current); }
        else _pendingFolder = f;
        UpdateFolderButton();
        AppendSystem(f == null ? Lang.T("已中斷連接資料夾：AI 而家淨係可以對話。")
                               : Lang.F("已連接資料夾：{0}\nAI 可以喺呢個資料夾入面讀寫、刪除檔案同執行指令（例如編譯、測試）。", f));
    }

    private void ShowFolderMenu()
    {
        if (_running != null) return;
        var menu = new ContextMenuStrip();
        try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        menu.BackColor = Ui.CardHi;
        menu.ForeColor = Ui.Text;
        menu.Items.Add(Lang.T("連接資料夾…"), null, (_, _) => BeginInvoke(() =>
        {
            using var dlg = new FolderBrowserDialog { Description = Lang.T("揀一個資料夾：AI 只可以喺入面讀寫檔案同執行指令"), UseDescriptionForTitle = true };
            if (Folder != null) dlg.SelectedPath = Folder;
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            string path = dlg.SelectedPath;
            string root = Path.GetPathRoot(path) ?? "";
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path.TrimEnd('\\'), home.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(FindForm(), Lang.T("唔可以連接成個磁碟、用戶資料夾或者 Windows 資料夾，請揀一個專用嘅資料夾。"), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(FindForm(), Lang.F("連接「{0}」？\n\nAI 可以喺呢個資料夾入面建立、修改、刪除檔案同執行指令。建議用專用資料夾，重要檔案先備份。", path),
                    Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            SetFolder(path);
        }));
        if (Folder != null)
        {
            menu.Items.Add(Lang.T("開啟資料夾"), null, (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Folder + "\"") { UseShellExecute = true }); } catch { }
            });
            menu.Items.Add(Lang.T("中斷連接"), null, (_, _) => SetFolder(null));
        }
        menu.Closed += (_, _) => BeginInvoke(() => menu.Dispose());
        menu.Show(_folderBtn, new Point(0, _folderBtn.Height + Ui.S(2)));
    }

    /// <summary>每句附上：AI 而家有冇資料夾、可以做乜。</summary>
    private string FolderTag(AiProvider prov)
    {
        var f = prov.Key == "local" ? null : Folder; // 本地 AI 冇工具，連接咗都只可以對話
        return f == null
            ? "[MultiTool: 呢個對話冇連接資料夾。你冇權限讀寫檔案或者執行指令；寫程式就將完整程式碼直接放喺回覆，叫用戶自己儲存同執行。唔好話自己可以讀寫檔案或者執行指令；如果用戶想你直接改檔案，叫佢撳上面「資料夾：未連接」掣連接一個資料夾。]"
            : $"[MultiTool: 工作資料夾 = {f}。你可以喺呢個資料夾（同佢嘅子資料夾）入面讀取、建立、修改、刪除檔案同執行指令，例如編譯同測試。絕對唔可以讀取、改動或刪除呢個資料夾以外嘅任何檔案、程式或者系統設定，亦唔可以 cd 出去。]";
    }

    private void NewChat()
    {
        if (_running != null) return;
        _current = null;
        _pendingFolder = null;
        UpdateFolderButton();
        _session = null;
        _list.Select(null);
        _log.Clear();
        AppendSystem(_provider != null ? Lang.F("新對話開始（用 {0}）。", _provider.Name) : Lang.T("新對話開始。"));
        _input.Focus();
    }

    private void LoadChat(ChatRecord rec)
    {
        if (_running != null) { _list.Select(_current); return; }
        _current = rec;
        _session = rec.SessionId;
        UpdateFolderButton();
        SetProvider(AiProvider.Get(rec.Provider));   // 舊對話用返佢原本嘅 AI
        _log.Clear();
        AppendSystem($"{rec.Title}　·　{AiProvider.Get(rec.Provider).Name}　·　{rec.Updated:yyyy-MM-dd HH:mm}");
        foreach (var m in rec.Messages) Render(m, AiProvider.Get(rec.Provider));
        _input.Focus();
    }

    private void DeleteChat(ChatRecord rec)
    {
        if (_running != null && rec == _current) return;
        if (MessageBox.Show(FindForm(), Lang.F("刪除對話「{0}」？", rec.Title), Program.AppName,
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
            case "user": AppendMessage(Lang.T("你"), m.Text + (m.Files is { Count: > 0 } ? "\n" + AttachBar.Label(m.Files) : ""), Ui.Accent); break;
            case "error": AppendMessage(Lang.T("錯誤"), m.Text, Ui.Danger); break;
            default: AppendMessage(p.Name, m.Text, Ui.Success); break;
        }
    }

    private void UpdateButtons()
    {
        bool busy = _running != null;
        _send.Text = busy ? Lang.T("停止") : Lang.T("傳送");
        _send.Kind = busy ? BtnKind.Danger : BtnKind.Primary;
        _send.Enabled = true;
        _newChat.Enabled = !busy;
        _detect.Enabled = !busy;
        _login.Enabled = !busy;
        _input.ReadOnly = busy;
        _attachBtn.Enabled = !busy;
        _send.Invalidate();
        _top.PerformLayout();
    }

    /// <summary>問 AI 一條獨立問題（冇上文、唔記錄），用嚟問網址。</summary>
    private async Task<string?> AskOnceAsync(AiProvider prov, string exe, string prompt)
    {
        var a = await prov.AskAsync(exe, prompt, null, x => { _running = x; BeginInvoke(UpdateButtons); }, quick: true);
        return a.IsError ? null : a.Text;
    }

    /// <summary>唔使問 AI 就答到（時間 / 開嘢）：顯示 + 記錄。</summary>
    private void FinishLocal(ChatRecord chat, AiProvider prov, string reply, List<string> done)
    {
        chat.Messages.Add(new ChatMsg { Role = "ai", Text = reply });
        chat.Updated = DateTime.Now;
        ChatStore.Save(chat);
        AppendMessage(prov.Name, reply, Ui.Success);
        if (done.Count > 0) AppendSystem(string.Join("　", done));
        _input.Focus();
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
            AppendSystem(Lang.T("未揀用邊個 AI：撳右上角「登入」揀 Claude / ChatGPT / Gemini。"));
            return;
        }
        string? exe = prov.Find();
        if (exe == null)
        {
            AppendSystem(Lang.F("搵唔到 {0}。撳右上角「登入」睇安裝方法，安裝完撳「重新偵測」。", prov.ToolName));
            return;
        }
        string q = _input.Text.Trim();
        bool hasFiles = _attach.Files.Count > 0;
        if (q.Length == 0 && !hasFiles) return;
        if (hasFiles && prov.Key == "local" && _attach.Files.Any(AiFiles.IsImage))
            AppendSystem(Lang.T("本地 AI 睇唔到圖片；想 AI 睇圖請用 Claude / ChatGPT / Gemini。"));
        if (q.Length == 0) q = Lang.T("請睇吓附件。");
        _input.Clear();
        var files = _attach.Take();
        _stopped = false;
        if (_current == null)
        {
            _current = ChatRecord.New(q, prov.Key);
            _current.Folder = _pendingFolder;
            _pendingFolder = null;
            _list.Add(_current);
        }
        var chat = _current;
        chat.Messages.Add(new ChatMsg { Role = "user", Text = q, Files = files.Count > 0 ? files : null });
        chat.Updated = DateTime.Now;
        ChatStore.Save(chat);
        _list.Touch(chat);
        AppendMessage(Lang.T("你"), q + (files.Count > 0 ? "\n" + AttachBar.Label(files) : ""), Ui.Accent);

        // 問時間：程式直接答（有附件就一定問 AI）
        if (files.Count == 0 && PcActions.TryTime(q, out var timeReply))
        {
            FinishLocal(chat, prov, timeReply, new List<string>());
            return;
        }
        // 「開 X」：打字冇錯字，直接掃程式 / 問 AI 網址（網址靠 AI 記得）
        if (files.Count == 0 && PcActions.OpenTarget(q) is { } openTarget)
        {
            var r = await PcActions.OpenAsync(openTarget, p => AskOnceAsync(prov, exe, p), PcActions.WantsSite(q));
            _running = null;
            UpdateButtons();
            if (r.Ok)
            {
                FinishLocal(chat, prov, PcActions.OpenedReply(q, openTarget), r.Done);
                return;
            }
        }
        int thinkingStart = _log.TextLength;
        AppendSystem(Lang.F("{0} 諗緊…", prov.Name));
        int thinkingEnd = _log.TextLength;

        AiProvider.Answer ans;
        try
        {
            // 每句附上系統時間；新對話第一句再講明可以開網頁 / 程式
            string tag = FolderTag(prov);
            string Wrap(bool first) => (first ? PcActions.Rules + "\n\n" : "") + tag + "\n" + PcActions.NowTag + "\n" + q;
            string prompt = await Task.Run(() => Wrap(_session == null));
            var folder = chat.Folder != null && Directory.Exists(chat.Folder) ? chat.Folder : null;
            ans = await prov.AskAsync(exe, prompt, _session, p => { _running = p; BeginInvoke(UpdateButtons); }, folder: folder, files: files);
            // 舊對話喺工具嗰邊已經清咗，就當新對話再問一次
            if (ans.IsError && !_stopped && _session != null && AiProvider.IsSessionGone(ans.Text))
            {
                _session = null;
                string prompt2 = await Task.Run(() => Wrap(true));
                ans = await prov.AskAsync(exe, prompt2, null, p => { _running = p; BeginInvoke(UpdateButtons); }, folder: folder, files: files);
                if (!ans.IsError) ans = ans with { Text = Lang.T("（舊對話嘅上文已過期，呢個回答唔記得之前講過乜）\n\n") + ans.Text };
            }
        }
        catch (Exception ex)
        {
            ans = new AiProvider.Answer(Lang.T("出錯：") + ex.Message, null, true);
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
        List<string> done = new();
        if (!ans.IsError && !_stopped)
        {
            var (clean, acts) = PcActions.Run(ans.Text);
            // 冇標籤但 AI 講「幫你開咗 X」：照開 X
            if (acts.Count == 0 && PcActions.ReplyTarget(clean) is { } rt)
                acts = (await PcActions.OpenAsync(rt, p => AskOnceAsync(prov, exe, p), PcActions.WantsSite(clean + q))).Done;
            done = acts;
            ans = ans with { Text = clean.Length > 0 ? clean : string.Join("、", acts) };
        }
        if (!_stopped)
        {
            chat.Messages.Add(new ChatMsg { Role = ans.IsError ? "error" : "ai", Text = ans.Text });
            chat.Updated = DateTime.Now;
        }
        ChatStore.Save(chat);
        _list.Touch(chat);
        if (chat != _current) return;
        if (_stopped) AppendSystem(Lang.T("已停止。"));
        else if (ans.IsError)
        {
            string hint = ans.Text.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                          ans.Text.Contains("auth", StringComparison.OrdinalIgnoreCase)
                ? Lang.T("\n（未登入或者登入過期：撳右上角「登入」）")
                : "";
            AppendMessage(Lang.T("錯誤"), ans.Text + hint, Ui.Danger);
            _ = RefreshStatusAsync();
        }
        else
        {
            AppendMessage(prov.Name, ans.Text, Ui.Success);
            if (done.Count > 0) AppendSystem(string.Join("　", done));
        }
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
        if (disposing) { _poll?.Dispose(); _voiceKey.Dispose(); try { _running?.Kill(true); } catch { } }
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
        Text = Lang.T("帳號");
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
            Text = Lang.T("揀要用嘅 AI（用你自己部電腦安裝咗嘅工具同帳號）"),
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
                Text = p.Name + (p == owner._active ? Lang.T("（使用中）") : ""), Font = Ui.F(10.5f, FontStyle.Bold), ForeColor = Ui.Text,
                AutoSize = false, Bounds = new Rectangle(Ui.S(12), Ui.S(8), Ui.S(184), Ui.S(22))
            };
            _state = new Label
            {
                Text = Lang.F("{0} · 檢查緊…", p.ToolName), Font = Ui.F(8.75f), ForeColor = Ui.TextFaint,
                AutoSize = false, Bounds = new Rectangle(Ui.S(12), Ui.S(31), Ui.S(184), Ui.S(18))
            };
            _main = new FancyButton { Text = "…", Kind = BtnKind.Primary, Font = Ui.F(9.5f), Enabled = false };
            _logout = new FancyButton { Text = Lang.T("登出"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Visible = false };
            _main.SetBounds(bounds.Width - Ui.S(116), Ui.S(12), Ui.S(104), Ui.S(32));
            _logout.SetBounds(bounds.Width - Ui.S(208), Ui.S(12), Ui.S(84), Ui.S(32));
            _main.Click += (_, _) => Main();
            _logout.Click += async (_, _) => await LogoutAsync();
            Controls.AddRange(new Control[] { _name, _state, _main, _logout });
            HandleCreated += async (_, _) => await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            _exe = await Task.Run(_p.Find);
            if (IsDisposed) return;
            if (_p is LocalProvider)
            {
                // 本地 AI：冇登入，只分「未下載 / 已下載」
                var cur = LocalAi.Current;
                _loggedIn = cur != null;
                _state.Text = cur != null ? Lang.T("已下載") + " - " + cur.Label : Lang.T("未下載（免費）");
                _state.ForeColor = cur != null ? Ui.Success : Ui.TextDim;
                _main.Text = cur == null ? Lang.T("下載") : Lang.T("使用");
                _main.Kind = BtnKind.Primary;
                _main.Enabled = cur == null || _p != _owner._active;
                _logout.Text = Lang.T("模型");
                _logout.Visible = cur != null;
                _main.Invalidate();
                _logout.Invalidate();
                return;
            }
            if (_exe == null)
            {
                _state.Text = Lang.F("未安裝 {0}", _p.ToolName);
                _state.ForeColor = Ui.Danger;
                _main.Text = Lang.T("安裝方法");
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
                _state.Text = Lang.T("已登入") + (a.Plan is { Length: > 0 } pl ? " - " + pl : "");
                _state.ForeColor = Ui.Success;
                _main.Text = Lang.T("使用");
                _main.Enabled = _p != _owner._active;
                _logout.Visible = true;
            }
            else
            {
                _state.Text = a.LoggedIn == false ? Lang.T("未登入") : Lang.T("睇唔到登入狀態");
                _state.ForeColor = a.LoggedIn == false ? Ui.TextDim : Ui.TextFaint;
                _main.Text = Lang.T("登入");
                _main.Enabled = true;
                _logout.Visible = false;
            }
            _main.Invalidate();
        }

        private void ShowLocal()
        {
            using var dlg = new LocalAiDialog();
            dlg.Changed += () => { if (LocalAi.Current != null) _owner.UseRequested?.Invoke(_p); };
            dlg.ShowDialog(_owner);
            _ = RefreshAsync();
        }

        private void Main()
        {
            if (_p is LocalProvider)
            {
                if (LocalAi.Current == null) { ShowLocal(); return; }
                _owner.UseRequested?.Invoke(_p);
                _owner.Close();
                return;
            }
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
            if (_p is LocalProvider) { ShowLocal(); return; }
            if (_exe == null) return;
            if (MessageBox.Show(_owner, Lang.F("確定要登出 {0}？\n（{1} 本身都會一齊登出）", _p.Name, _p.ToolName), Program.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _logout.Enabled = false;
            bool ok = await _p.LogoutAsync(_exe);
            _logout.Enabled = true;
            if (!ok)
            {
                MessageBox.Show(_owner, Lang.T("登出失敗。"), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
    /// <summary>附件（data\ai\attach 入面嘅副本）；冇就 null。</summary>
    public List<string>? Files { get; set; }
}

/// <summary>一個對話：存喺 data\\ai\\chats\\&lt;Id&gt;.json。</summary>
internal sealed class ChatRecord
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? SessionId { get; set; }
    public string Provider { get; set; } = "claude";
    /// <summary>呢個對話連接嘅資料夾（AI 只可以喺入面讀寫 / 執行）；null = 淨係對話。</summary>
    public string? Folder { get; set; }
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
        _menu.Items.Add(Lang.T("重新命名"), null, (_, _) => { if (_menuItem != null) RenameRequested?.Invoke(_menuItem); });
        _menu.Items.Add(Lang.T("刪除"), null, (_, _) => { if (_menuItem != null) DeleteRequested?.Invoke(_menuItem); });
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
        Text = Lang.T("重新命名");
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
        var ok = new FancyButton { Text = Lang.T("確定"), Kind = BtnKind.Primary, Font = Ui.F(9.75f),
            Bounds = new Rectangle(Ui.S(164), Ui.S(66), Ui.S(84), Ui.S(34)) };
        var cancel = new FancyButton { Text = Lang.T("取消"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f),
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
