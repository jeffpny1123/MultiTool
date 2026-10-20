using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTool.Ai;

/// <summary>
/// 俾 AI「有限度」控制部電腦：知道系統時間、開網頁、開已安裝嘅程式。
/// · 網頁：網址全靠 AI 自己記得（唔寫死任何網站）。
/// · 程式：每次要開都即時掃一次部機有咩程式，結果寫入 data\ai\keywords.txt。
/// AI 唔會直接行指令：由程式檢查完先開；網址只接受 http / https，程式只可以開「開始」選單入面有嘅。
/// </summary>
internal static class PcActions
{
    private sealed record App(string Name, string Target, bool AppsFolder);

    public static string KeywordsFile => Path.Combine(AiProvider.WorkDir, "keywords.txt");

    // ================= 時間 =================

    public static string Now
    {
        get
        {
            var n = DateTimeOffset.Now;
            string off = n.Offset < TimeSpan.Zero ? "-" + n.Offset.ToString(@"hh\:mm") : "+" + n.Offset.ToString(@"hh\:mm");
            return $"{n.ToString("yyyy-MM-dd dddd HH:mm:ss", CultureInfo.InvariantCulture)} (UTC{off}, {TimeZoneInfo.Local.StandardName})";
        }
    }

    public static string NowTag => $"[Now: {Now}]";

    // ================= 掃描程式 =================

    private static readonly object ScanLock = new();

    /// <summary>即時掃描已安裝程式（「開始」選單 / 所有應用程式），順手寫入 keywords.txt。</summary>
    private static List<App> ScanApps()
    {
        lock (ScanLock)
        {
            var list = LoadAppsFolder();
            if (list.Count == 0) list = LoadShortcuts();
            try
            {
                File.WriteAllLines(KeywordsFile,
                    new[] { "# MultiTool 掃描到嘅程式（每次開程式都會自動更新，唔使改）", $"# {DateTime.Now:yyyy-MM-dd HH:mm:ss}" }
                        .Concat(list.Select(a => a.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)),
                    new UTF8Encoding(true));
            }
            catch { }
            return list;
        }
    }

    private static bool Junk(string name) =>
        Regex.IsMatch(name, "uninstall|解除安裝|卸载|移除|readme|說明文件|help|manual|website|license", RegexOptions.IgnoreCase);

    /// <summary>shell:AppsFolder（包括 Microsoft Store 程式）。Shell COM 要 STA thread。</summary>
    private static List<App> LoadAppsFolder()
    {
        var list = new List<App>();
        var t = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type == null) return;
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    try
                    {
                        string name = item.Name, path = item.Path;
                        if (name.Length > 0 && path.Length > 0 && !Junk(name)) list.Add(new App(name, path, true));
                    }
                    catch { }
                }
            }
            catch (Exception ex) { AppPaths.LogError("讀程式清單", ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join(8000);
        return list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>後備：直接讀「開始」選單嘅捷徑。</summary>
    private static List<App> LoadShortcuts()
    {
        var list = new List<App>();
        foreach (var dir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
                 })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is not (".lnk" or ".url" or ".appref-ms")) continue;
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (!Junk(name)) list.Add(new App(name, f, false));
                }
            }
            catch { }
        }
        return list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>喺程式清單搵最似嘅（完全一樣 → 開頭一樣 → 包含 → 差一兩個字）。</summary>
    private static App? FindApp(List<App> apps, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        return apps.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? apps.FirstOrDefault(a => name.Length >= 2 && a.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
               ?? apps.FirstOrDefault(a => name.Length >= 2 && a.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
               ?? apps.FirstOrDefault(a => a.Name.Length >= 3 && name.Contains(a.Name, StringComparison.OrdinalIgnoreCase))
               ?? apps.Where(a => Similar(name, a.Name)).OrderBy(a => Math.Abs(a.Name.Length - name.Length)).FirstOrDefault();
    }

    /// <summary>中文名三個字或以上，順序對中得只差一個字（語音辨識嘅同音錯字）。</summary>
    private static bool Similar(string said, string name)
    {
        if (name.Length < 3 || !Regex.IsMatch(name, "[一-鿿]") || said.Length > name.Length + 4) return false;
        int hit = 0, pos = 0;
        foreach (char ch in name)
        {
            int k = said.IndexOf(ch, pos);
            if (k >= 0) { hit++; pos = k + 1; }
        }
        return hit >= name.Length - 1;
    }

    // ================= 俾 AI 嘅說明（唔包任何寫死嘅網站 / 程式清單）=================

    public static string Rules =>
        "You run inside MultiTool on the user's Windows PC. Each user message starts with [Now: ...] = the PC's current local date/time; " +
        "use it to answer time/date/weekday questions (do not say you cannot know the time). " +
        "You CAN open things on the PC with action tags (they are hidden from the user and executed by the app): " +
        "<<open:URL>> opens a website in the default browser - use the official https address from your own knowledge. " +
        "<<app:NAME>> opens an installed Windows app - NAME is the app's name as shown in the Start menu (the app finds the closest match). " +
        "Only add tags when the user asks to open / launch / go to something. " +
        "Also reply briefly in words using the correct full name, e.g. 幫你開咗<name>. " +
        "If you have a web search tool, use it for news, weather, prices or anything current instead of saying you cannot look it up.";

    // ================= 執行 =================

    private static readonly Regex Tag = new(@"<<\s*(open|app)\s*:\s*([^>]+?)\s*>>", RegexOptions.IgnoreCase);

    /// <summary>執行回答入面嘅標籤，返回拎走咗標籤嘅文字同做咗乜。</summary>
    public static (string Text, List<string> Done) Run(string reply)
    {
        var done = new List<string>();
        List<App>? apps = null;
        foreach (Match m in Tag.Matches(reply))
        {
            string kind = m.Groups[1].Value.ToLowerInvariant(), arg = m.Groups[2].Value.Trim();
            if (kind == "open") OpenUrl(arg, done);
            else
            {
                apps ??= ScanApps();
                if (FindApp(apps, arg) is { } app) Launch(app, done);
                else done.Add(Lang.F("搵唔到程式：{0}", arg));
            }
        }
        string text = Regex.Replace(Tag.Replace(reply, "").Trim(), @"[ \t]{2,}", " ");
        if (done.Count > 0) Log($"tags \"{reply.Replace("\n", " ")}\" => {string.Join(",", done)}");
        return (text, done);
    }

    /// <summary>
    /// 開「X」：即時掃描程式，有就開程式；係網址就開網址；
    /// 都唔係就問 AI X 嘅官方網址（靠 AI 自己記得），驗證過先開。
    /// ask：問 AI 一條問題（冇上文），返佢嘅回答。
    /// </summary>
    public static async Task<(bool Ok, List<string> Done)> OpenAsync(string target, Func<string, Task<string?>> ask, bool preferSite = false)
    {
        var done = new List<string>();
        target = target.Trim().Trim('「', '」', '『', '』', '"', '“', '”');
        if (target.Length == 0) return (false, done);
        if (!preferSite)
        {
            var apps = await Task.Run(ScanApps);
            if (FindApp(apps, target) is { } app) { Log($"open \"{target}\" => app {app.Name}"); return (Launch(app, done), done); }
        }
        if (Regex.IsMatch(target, @"^(https?://)?[\w\-]+(\.[\w\-]+)+(/\S*)?$", RegexOptions.IgnoreCase))
        {
            return (OpenUrl(target, done), done);
        }
        string? ans = await ask(
            "What is the official website URL of \"" + target + "\"? (The name may contain speech-recognition typos; use the closest well-known site.) " +
            "Reply with the https URL only, or NONE if you don't know.");
        var m = Regex.Match(ans ?? "", @"https?://[^\s<>""'）)】]+", RegexOptions.IgnoreCase);
        Log($"open \"{target}\" ask url => {ans?.Replace("\n", " ")}");
        return (m.Success && OpenUrl(m.Value.TrimEnd('.', ',', '。'), done), done);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")] private static extern bool IsUserAnAdmin();

    /// <summary>
    /// 開網址 / 程式。MultiTool 用「系統管理員」身分運行（玩遊戲成日要）嗰陣，直接開會變成用管理員身分開 Chrome，
    /// Chrome 會拒絕或者淨係開個新分頁；所以交俾 explorer.exe（普通權限）去開，同你自己雙擊一樣。
    /// </summary>
    private static void ShellOpen(string target)
    {
        bool admin = false;
        try { admin = IsUserAnAdmin(); } catch { }
        Log($"admin={admin} open {target}");
        if (admin)
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private static bool OpenUrl(string url, List<string> done)
    {
        try
        {
            if (!url.Contains("://")) url = "https://" + url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                done.Add(Lang.F("唔開：{0}（只可以開網頁）", url));
                return false;
            }
            ShellOpen(uri.AbsoluteUri);
            done.Add(Lang.F("已開啟 {0}", uri.Host));
            return true;
        }
        catch (Exception ex) { AppPaths.LogError("開網址", ex); done.Add(Lang.F("開唔到：{0}", url)); return false; }
    }

    private static bool Launch(App app, List<string> done)
    {
        try
        {
            if (app.AppsFolder && !File.Exists(app.Target)) ShellOpen("shell:AppsFolder\\" + app.Target);
            else ShellOpen(app.Target);
            done.Add(Lang.F("已開啟 {0}", app.Name));
            return true;
        }
        catch (Exception ex) { AppPaths.LogError("開程式", ex); done.Add(Lang.F("開唔到：{0}", app.Name)); return false; }
    }

    private static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(AiProvider.WorkDir, "actions.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}"); } catch { }
    }

    // ================= 理解句子 =================

    /// <summary>
    /// 整理句子：語音辨識出嚟嘅字有時會斷開幾行、中文字之間有空格（例如「幫我開\n動畫瘋」），先合埋。
    /// </summary>
    private static string Clean(string q)
    {
        q = Regex.Replace(q, @"\s+", " ").Trim();
        q = Regex.Replace(q, @"(?<=[一-鿿，。！？、])\s+|\s+(?=[一-鿿，。！？、])", "");
        return Regex.Replace(q, @"^[，,。.、\s]+|[。！？!?，,.、~～\s]+$", "").Trim();
    }

    private static bool HasCjk(string s) => Regex.IsMatch(s, "[一-鿿]");

    private static bool Cantonese(string q) =>
        HasCjk(q) && (VoiceSettings.Speech == SpeechLang.Cantonese || Lang.Current == Language.ZhHant);

    /// <summary>「幫你開咗 X」（按句子語言）。</summary>
    public static string OpenedReply(string q, string name) =>
        !HasCjk(q) ? $"Opened {name}." : Cantonese(q) ? $"幫你開咗{name}。" : $"已为你打开{name}。";

    /// <summary>問時間 / 日期：程式直接答（唔使等 AI）。</summary>
    public static bool TryTime(string q, out string reply)
    {
        reply = "";
        string c = Clean(q);
        if (c.Length == 0 || c.Length > 16) return false;
        if (!Regex.IsMatch(c, @"幾點|几点|時間|时间|幾號|几号|日期|星期幾|星期几|禮拜幾|礼拜几|what time|what day|today'?s date|what'?s the date", RegexOptions.IgnoreCase)
            || Regex.IsMatch(c, "開|开|open", RegexOptions.IgnoreCase)) return false;
        var n = DateTime.Now;
        string[] wk = { "日", "一", "二", "三", "四", "五", "六" };
        if (!HasCjk(c))
            reply = n.ToString("'It''s' h:mm tt, dddd, MMMM d.", CultureInfo.InvariantCulture);
        else if (Cantonese(c))
        {
            string ap = n.Hour < 6 ? "凌晨" : n.Hour < 12 ? "朝早" : n.Hour < 13 ? "中午" : n.Hour < 18 ? "下晝" : "夜晚";
            int h = n.Hour % 12 == 0 ? 12 : n.Hour % 12;
            reply = $"而家係{n.Month}月{n.Day}號星期{wk[(int)n.DayOfWeek]}，{ap}{h}點{(n.Minute == 0 ? "正" : n.Minute + "分")}。";
        }
        else reply = $"现在是{n.Month}月{n.Day}日星期{wk[(int)n.DayOfWeek]}，{n:HH:mm}。";
        return true;
    }

    private static readonly Regex OpenRx = new(
        @"^.{0,12}?" + // 前面可以有少少客套說話，例如「你可唔可以幫我」「please」
        @"(?:打開|打开|開啟|开启|啟動|启动|開一開|開|开|open|launch|start|run|go to)\s*(?:個|个|一下)?\s*(.+?)\s*" +
        @"(?:網站|网站|網頁|网页|個網|嘅網站|嘅網頁|程式|程序|應用程式|应用|app|啦|呀|吖|喇|先|吧|一下|please|for me)*\s*$",
        RegexOptions.IgnoreCase);

    private static string TrimTarget(string t) =>
        Regex.Replace(t.Trim(), @"^(返|翻|下|一下|個|个)\s*|\s*(俾我|畀我|給我|给我|for me|吖|啦|呀|喇|了|咗)$", "", RegexOptions.IgnoreCase).Trim();

    /// <summary>用戶句子係唔係「開 X」；係就返 X。</summary>
    public static string? OpenTarget(string q)
    {
        var m = OpenRx.Match(Clean(q));
        if (!m.Success) return null;
        string t = TrimTarget(m.Groups[1].Value);
        return t.Length is > 0 and <= 20 ? t : null;
    }

    /// <summary>句子有冇講明要網站（就唔好開同名程式）。</summary>
    public static bool WantsSite(string text) => Regex.IsMatch(text, "網站|网站|網頁|网页|個網|website|site", RegexOptions.IgnoreCase);

    // 要講明「已經開咗」先算（「幫你開咗 X」「已為你打開 X」「開咗 X」），費事「你可以打開設定睇吓」都開
    private static readonly Regex OpenedRx = new(
        @"(?:(?:幫你|帮你|為你|为你|已經|已经|已|即刻|馬上|马上)\s*(?:打開|打开|開啟|开启|開|开)(?:咗|了|緊|紧)?" +
        @"|(?:打開|打开|開啟|开启|開|开)(?:咗|了|緊|紧))\s*" +
        @"(?:「|『|“|"")?([^，,。.！!？?\n「」『』“”""]{1,24})",
        RegexOptions.IgnoreCase);
    private static readonly Regex OpenedEnRx = new(@"\b(?:opened|opening|launched|launching)\s+([A-Za-z0-9 .\-]{1,30}?)(?:\s+for you)?[.!,]", RegexOptions.IgnoreCase);

    /// <summary>
    /// AI 回覆入面講「幫你開咗 X」就返 X：語音辨識成日有錯字，AI 會改正（例如佢答「幫你開咗動畫瘋」）。
    /// </summary>
    public static string? ReplyTarget(string answer)
    {
        var m = OpenedRx.Match(answer);
        if (!m.Success) m = OpenedEnRx.Match(answer);
        if (!m.Success) return null;
        string t = Regex.Replace(TrimTarget(m.Groups[1].Value), @"(網站|网站|網頁|网页|程式|程序|應用程式|俾你|畀你|給你|给你|for you)+$", "", RegexOptions.IgnoreCase).Trim();
        return t.Length > 0 ? t : null;
    }
}
