using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace MultiTool.Plugins;

// =====================================================================
//  插件：用 HTML + JavaScript 寫嘅自製工具
//  • 每個插件一個資料夾（data\plugins\<id>）：manifest.json、ui.html、main.js…
//  • 喺 WebView2 入面行（https://<id>.plugin.mt/），淨係可以經 mt.* API 做嘢
//  • 權限喺 manifest 寫明，第一次行（或者權限改咗）要用家同意
//  • 網絡：只准連 manifest "net" 入面嘅網站；其他一律擋
//  • 兩種：「工具」（type: tool，有自己介面，可以加到左側工具欄）
//         「插件」（type: mod，冇自己介面，喺背景行 mod.js，用 mt.ui.* 以 override 方式改 MultiTool 原有介面，見 PluginMods.cs）
// =====================================================================

/// <summary>manifest.json</summary>
internal sealed class PluginManifest
{
    public string Id = "", Name = "", Version = "1.0.0", Author = "", Description = "", Icon = "🧩", Entry = "ui.html";
    public bool Background;
    public List<string> Net = new();
    public bool Notify, Clipboard, Secrets, Ai, Scripts, Ui;
    /// <summary>"tool" = 工具（有自己介面）；"mod" = 插件（改原有介面）。</summary>
    public string Type = "tool";
    /// <summary>插件主要改邊個工具（頁 key，例如 "editor"；"" = 成個程式），淨係顯示 / 俾 AI 參考用。</summary>
    public string Target = "";
    public bool IsMod => Type == "mod";

    /// <summary>同一個插件、另一個入口頁（插件加落介面嘅 HTML 面板用）。</summary>
    public PluginManifest WithEntry(string entry)
    {
        var c = (PluginManifest)MemberwiseClone();
        c.Entry = entry;
        return c;
    }

    public static PluginManifest Parse(string id, string json)
    {
        var m = new PluginManifest { Id = id, Name = id };
        var o = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new InvalidDataException(Lang.T("manifest.json 唔係 JSON 物件"));
        string S(string k, string d) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : d;
        m.Name = S("name", id);
        m.Version = S("version", "1.0.0");
        m.Author = S("author", "");
        m.Description = S("description", "");
        m.Icon = S("icon", "🧩");
        m.Type = S("type", "tool").Trim().ToLowerInvariant() == "mod" ? "mod" : "tool";
        m.Target = S("target", "").Trim();
        m.Entry = S("entry", m.IsMod ? "mod.js" : "ui.html");
        m.Background = o["background"] is JsonValue bv && bv.TryGetValue<bool>(out var b) && b;
        if (o["permissions"] is JsonObject p)
        {
            bool B(string k) => p[k] is JsonValue x && x.TryGetValue<bool>(out var y) && y;
            m.Notify = B("notify"); m.Clipboard = B("clipboard"); m.Secrets = B("secrets"); m.Ai = B("ai"); m.Scripts = B("scripts"); m.Ui = B("ui");
            if (p["net"] is JsonArray arr)
                foreach (var n in arr) if (n is JsonValue nv && nv.TryGetValue<string>(out var h) && h.Trim().Length > 0) m.Net.Add(h.Trim().ToLowerInvariant());
        }
        if (m.Entry.Contains("..") || Path.IsPathRooted(m.Entry)) m.Entry = m.IsMod ? "mod.js" : "ui.html";
        if (!m.IsMod) m.Ui = false; // 改介面淨係插件先用得
        return m;
    }

    /// <summary>權限清單（俾用家睇）。</summary>
    public List<(string Text, bool Danger)> PermissionList()
    {
        var l = new List<(string, bool)>();
        if (Net.Contains("*")) l.Add((Lang.T("連去任何網站"), true));
        else if (Net.Count > 0) l.Add((Lang.F("連去：{0}", string.Join("、", Net)), false));
        if (Background) l.Add((Lang.T("MultiTool 開住就喺背景運行"), false));
        if (Notify) l.Add((Lang.T("彈 Windows 通知"), false));
        if (Clipboard) l.Add((Lang.T("讀寫剪貼簿"), true));
        if (Secrets) l.Add((Lang.T("加密儲存密碼 / API key"), false));
        if (Ai) l.Add((Lang.T("用你嘅 AI（Claude / ChatGPT / Gemini）"), false));
        if (Scripts) l.Add((Lang.T("睇同埋開始「我的腳本」（腳本會控制滑鼠鍵盤）"), true));
        if (Ui) l.Add((Lang.T("改 MultiTool 嘅外觀（主題、背景、側欄、首頁、設定；讀到呢啲位置嘅文字、可以撳掣）"), true));
        return l;
    }

    /// <summary>權限指紋：改咗就要重新同意。</summary>
    public string PermKey() =>
        string.Join("|", Net.OrderBy(x => x)) + $";{Background};{Notify};{Clipboard};{Secrets};{Ai};{Scripts}" + (Ui ? ";ui" : "");
}

internal static class PluginStore
{
    public static string Dir => Path.Combine(AppPaths.Data, "plugins");
    public static string DataDir(string id) => Path.Combine(AppPaths.Data, "plugin-data", id);
    public static event Action? Changed;

    public static string PathOf(string id) => Path.Combine(Dir, id);

    public static List<PluginManifest> List()
    {
        var res = new List<PluginManifest>();
        try
        {
            Directory.CreateDirectory(Dir);
            foreach (var d in Directory.GetDirectories(Dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string id = Path.GetFileName(d);
                try { res.Add(Load(id)); }
                catch { res.Add(new PluginManifest { Id = id, Name = id + " ⚠", Description = Lang.T("manifest.json 有錯") }); }
            }
        }
        catch (Exception ex) { AppPaths.LogError("讀插件", ex); }
        return res;
    }

    public static PluginManifest Load(string id) => PluginManifest.Parse(id, File.ReadAllText(Path.Combine(PathOf(id), "manifest.json")));

    public static void Notify() => Changed?.Invoke();

    // ---- 加咗去左側工具欄嘅插件（data\plugins_pinned.txt）----
    private static string PinFile => Path.Combine(AppPaths.Data, "plugins_pinned.txt");
    private static List<string>? _pins;
    public static event Action? PinsChanged;

    public static List<string> Pinned
    {
        get
        {
            if (_pins != null) return _pins;
            _pins = new();
            try { if (File.Exists(PinFile)) _pins.AddRange(File.ReadAllLines(PinFile).Select(l => l.Trim()).Where(l => l.Length > 0 && Directory.Exists(PathOf(l)))); } catch { }
            return _pins;
        }
    }

    public static bool IsPinned(string id) => Pinned.Contains(id);

    public static void SetPinned(string id, bool on)
    {
        if (on == IsPinned(id)) return;
        if (on) Pinned.Add(id); else Pinned.Remove(id);
        try { Directory.CreateDirectory(AppPaths.Data); File.WriteAllLines(PinFile, Pinned); } catch { }
        PinsChanged?.Invoke();
    }

    /// <summary>插件 id：細楷英文、數字、-。</summary>
    public static string MakeId(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        string id = sb.ToString().Trim('-');
        if (id.Length == 0) id = "plugin";
        if (id.Length > 40) id = id[..40];
        string baseId = id;
        for (int i = 2; Directory.Exists(PathOf(id)); i++) id = baseId + "-" + i;
        return id;
    }

    public static string Create(string name)
    {
        string id = MakeId(name);
        string dir = PathOf(id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), PluginTemplates.Manifest(name));
        File.WriteAllText(Path.Combine(dir, "ui.html"), PluginTemplates.Html);
        File.WriteAllText(Path.Combine(dir, "main.js"), PluginTemplates.Js);
        Notify();
        return id;
    }

    /// <summary>新插件（改原有介面）：manifest + mod.js。</summary>
    public static string CreateMod(string name, string target)
    {
        string id = MakeId(name);
        string dir = PathOf(id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), PluginTemplates.ModManifest(name, target));
        File.WriteAllText(Path.Combine(dir, "mod.js"), PluginTemplates.ModJs);
        Notify();
        return id;
    }

    public static void Delete(string id)
    {
        PluginRuntime.Stop(id);
        ModHost.Stop(id);
        ModStore.Remove(id);
        UI.ThemeStore.RemoveMod(id);
        try { Directory.Delete(PathOf(id), true); } catch (Exception ex) { AppPaths.LogError("刪插件", ex); }
        try { Directory.Delete(DataDir(id), true); } catch { }
        Notify();
    }

    /// <summary>匯出做 .mtplugin（其實係 zip）。</summary>
    public static void Export(string id, string file)
    {
        if (File.Exists(file)) File.Delete(file);
        ZipFile.CreateFromDirectory(PathOf(id), file, CompressionLevel.Optimal, false);
    }

    /// <summary>匯入 .mtplugin / .zip；回傳新 id。</summary>
    public static string Import(string file)
    {
        using var zip = ZipFile.OpenRead(file);
        var man = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').TrimStart('/').Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidDataException(Lang.T("入面冇 manifest.json"));
        string json;
        using (var r = new StreamReader(man.Open())) json = r.ReadToEnd();
        var m = PluginManifest.Parse("x", json);
        string id = MakeId(m.Name);
        string dir = PathOf(id);
        string full = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith("/")) continue;
            string dest = Path.GetFullPath(Path.Combine(dir, e.FullName));
            if (!dest.StartsWith(full, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Lang.T("檔案路徑唔安全"));
            total += e.Length;
            if (total > 50L << 20) throw new InvalidDataException(Lang.T("插件太大（上限 50 MB）"));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, true);
        }
        Notify();
        return id;
    }

    // ---- 同意咗嘅權限 ----
    private static string ApprovedFile(string id) => Path.Combine(DataDir(id), "approved.txt");
    public static bool Approved(PluginManifest m)
    {
        try { return File.Exists(ApprovedFile(m.Id)) && File.ReadAllText(ApprovedFile(m.Id)) == m.PermKey(); } catch { return false; }
    }
    public static void Approve(PluginManifest m)
    {
        Directory.CreateDirectory(DataDir(m.Id));
        File.WriteAllText(ApprovedFile(m.Id), m.PermKey());
    }

    /// <summary>未同意就問；同意咗回傳 true。</summary>
    public static bool AskApprove(IWin32Window? owner, PluginManifest m)
    {
        if (Approved(m)) return true;
        var perms = m.PermissionList();
        if (perms.Count == 0) { Approve(m); return true; }
        string list = string.Join("\n", perms.Select(p => (p.Danger ? "⚠ " : "• ") + p.Text));
        var r = MessageBox.Show(owner, Lang.F("插件「{0}」要求以下權限：\n\n{1}\n\n只安裝你信任嘅插件。同意？", m.Name, list), Lang.T("插件權限"),
            MessageBoxButtons.YesNo, perms.Any(p => p.Danger) ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return false;
        Approve(m);
        return true;
    }
}

/// <summary>插件用嘅 WebView2（全部 WebView2 型別喺呢度；WebView2Lib.Prepare 之後先用）。</summary>
internal sealed class PluginWeb
{
    private static CoreWebView2Environment? _env;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private CoreWebView2Controller _ctl = null!;
    private PluginManifest _m = null!;
    private Control _host = null!;
    private string _origin = "";
    public Action<string, string>? Log;   // (level, text)

    private static async Task<CoreWebView2Environment> Env()
    {
        if (_env != null) return _env;
        string data = Path.Combine(AppPaths.Data, "webview", "plugins");
        Directory.CreateDirectory(data);
        return _env = await CoreWebView2Environment.CreateAsync(null, data);
    }

    public static async Task<PluginWeb> CreateAsync(Control host, PluginManifest m, bool dev, Action<string, string> log)
    {
        var w = new PluginWeb { _m = m, _host = host, Log = log };
        var env = await Env();
        w._ctl = await env.CreateCoreWebView2ControllerAsync(host.Handle);
        w._ctl.DefaultBackgroundColor = UI.Ui.Bg;
        w._ctl.Bounds = host.ClientRectangle;
        host.Resize += (_, _) => { try { w._ctl.Bounds = host.ClientRectangle; } catch { } };
        var wv = w._ctl.CoreWebView2;
        try { wv.Profile.PreferredColorScheme = UI.Ui.Light ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark; } catch { }
        var st = wv.Settings;
        st.IsScriptEnabled = true;
        st.IsWebMessageEnabled = true;
        st.AreHostObjectsAllowed = false;
        st.AreDevToolsEnabled = dev;           // 編寫模式先俾 F12
        st.IsStatusBarEnabled = false;
        st.IsPasswordAutosaveEnabled = false;
        st.IsGeneralAutofillEnabled = false;
        st.AreBrowserAcceleratorKeysEnabled = dev;
        string hostName = m.Id + ".plugin.mt";
        w._origin = "https://" + hostName + "/";
        // 唔用 SetVirtualHostNameToFolderMapping：佢嘅請求唔經 WebResourceRequested，加唔到主題顏色同 no-store。
        // 全部檔案由下面 WebResourceRequested 自己讀出嚟俾佢。
        await wv.AddScriptToExecuteOnDocumentCreatedAsync(PluginBridgeJs.Script);
        // 只准喺自己個插件入面轉頁；外面連結用瀏覽器開
        wv.NavigationStarting += (_, e) =>
        {
            string u = e.Uri ?? "";
            if (u.StartsWith(w._origin, StringComparison.OrdinalIgnoreCase) || u.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                if (u.StartsWith(w._origin, StringComparison.OrdinalIgnoreCase)) w.Navigating?.Invoke(); // 重新載入：清走舊 override
                return;
            }
            e.Cancel = true;
            OpenExternal(u);
        };
        wv.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri ?? ""); };
        // 網絡：自己 + data/blob + manifest 准咗嘅網站；其他擋
        wv.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        wv.WebResourceRequested += (_, e) =>
        {
            string u = e.Request.Uri ?? "";
            if (u.StartsWith(w._origin, StringComparison.OrdinalIgnoreCase))
            {
                // 自己嘅檔案：自己讀，唔俾瀏覽器 cache（改完 main.js 試行即刻見到新版）
                try
                {
                    string rel = Uri.UnescapeDataString(new Uri(u).AbsolutePath.TrimStart('/'));
                    string root = Path.GetFullPath(PluginStore.PathOf(w._m.Id)) + Path.DirectorySeparatorChar;
                    string p = Path.GetFullPath(Path.Combine(root, rel.Length == 0 ? w._m.Entry : rel));
                    // 插件（mod）：冇自己介面，自動整一頁空白 HTML 載入 mod.js
                    if (rel == "__mod__.html")
                    {
                        string html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body><script src=\"" + Uri.EscapeDataString(w._m.Entry).Replace("%2F", "/") + "\"></script></body></html>";
                        e.Response = env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
                        return;
                    }
                    if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                    {
                        var bytes = File.ReadAllBytes(p);
                        // HTML：加入程式主題（日光 / 深色）嘅顏色變數
                        string mime = MimeOf(p);
                        if (mime.StartsWith("text/html")) bytes = Encoding.UTF8.GetBytes(PluginTheme.Inject(Encoding.UTF8.GetString(bytes)));
                        else if (mime.StartsWith("text/css")) bytes = Encoding.UTF8.GetBytes(PluginTheme.MapColors(Encoding.UTF8.GetString(bytes)));
                        var ms = new MemoryStream(bytes);
                        e.Response = env.CreateWebResourceResponse(ms, 200, "OK", $"Content-Type: {MimeOf(p)}\r\nCache-Control: no-store");
                    }
                    else e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
                }
                catch { }
                return;
            }
            if (u.StartsWith("data:") || u.StartsWith("blob:")) return;
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http") && PluginBridge.HostAllowed(w._m, uri.Host)) return;
            w.Log?.Invoke("warn", Lang.F("已擋：{0}（未喺 manifest 嘅 net 權限）", u.Length > 120 ? u[..120] : u));
            e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
        };
        wv.WebMessageReceived += async (_, e) =>
        {
            string json;
            try { json = e.WebMessageAsJson; } catch { return; }
            if (!e.Source.StartsWith(w._origin, StringComparison.OrdinalIgnoreCase)) return;
            var reply = await PluginBridge.HandleAsync(w._m, json, host, (lvl, t) => w.Log?.Invoke(lvl, t), w.Post);
            if (reply != null) try { wv.PostWebMessageAsJson(reply); } catch { }
        };
        wv.Navigate(w._origin + w.Page);
        return w;
    }

    /// <summary>重新載入 / 轉頁之前（插件要清走舊嘅 override）。</summary>
    public Action? Navigating;

    /// <summary>入口頁：插件（mod）用自動整嘅空白頁載入 mod.js。</summary>
    private string Page => _m.IsMod && _m.Entry.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "__mod__.html" : _m.Entry;

    /// <summary>主動傳嘢俾頁面（mt.ui 事件）。</summary>
    public void Post(string json)
    {
        try
        {
            if (_host.InvokeRequired) _host.BeginInvoke(() => Post(json));
            else _ctl.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch { }
    }

    public void Reload(PluginManifest m)
    {
        _m = m;
        try { _ctl.CoreWebView2.Navigate(_origin + Page + "?t=" + DateTime.Now.Ticks); } catch { }
    }

    public void OpenDevTools() { try { _ctl.CoreWebView2.OpenDevToolsWindow(); } catch { } }

    public void Close() { try { _ctl.Close(); } catch { } }

    private static string MimeOf(string p) => Path.GetExtension(p).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8", ".js" or ".mjs" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8", ".svg" => "image/svg+xml", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif", ".webp" => "image/webp", ".ico" => "image/x-icon", ".woff2" => "font/woff2", ".woff" => "font/woff",
        ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".mp4" => "video/mp4", ".txt" or ".md" or ".csv" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };

    private static void OpenExternal(string u)
    {
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
    }

    internal static HttpClient Client => Http;
}

/// <summary>mt.* API：檢查權限，做嘢，回覆。</summary>
internal static class PluginBridge
{
    public static bool HostAllowed(PluginManifest m, string host)
    {
        host = host.ToLowerInvariant();
        foreach (var n in m.Net)
        {
            if (n == "*") return true;
            if (n.StartsWith("*.") ? host.EndsWith(n[1..]) || host == n[2..] : host == n) return true;
        }
        return false;
    }

    public static async Task<string?> HandleAsync(PluginManifest m, string json, Control ui, Action<string, string> log, Action<string>? post = null)
    {
        JsonObject msg;
        try { msg = JsonNode.Parse(json) as JsonObject ?? new JsonObject(); } catch { return null; }
        if (msg["__mtlog"] != null)
        {
            log(msg["lvl"]?.GetValue<string>() ?? "info", msg["text"]?.GetValue<string>() ?? "");
            return null;
        }
        if (msg["__mt"] == null) return null;
        var id = msg["id"]?.DeepClone();
        string method = msg["method"]?.GetValue<string>() ?? "";
        var args = msg["args"] as JsonArray ?? new JsonArray();
        string Arg(int i) => i < args.Count && args[i] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : "";
        try
        {
            JsonNode? result = method.StartsWith("ui.") || method.StartsWith("app.")
                ? OnUi(ui, () => { Need(m.IsMod && m.Ui, "ui"); return ModEngine.Call(m, method, args, post); })
                : await CallAsync(m, method, args, Arg, ui);
            return new JsonObject { ["__mt"] = 1, ["id"] = id, ["ok"] = true, ["result"] = result }.ToJsonString();
        }
        catch (Exception ex)
        {
            log("error", method + "：" + ex.Message);
            return new JsonObject { ["__mt"] = 1, ["id"] = id, ["ok"] = false, ["error"] = ex.Message }.ToJsonString();
        }
    }

    private static void Need(bool ok, string perm)
    {
        if (!ok) throw new UnauthorizedAccessException(Lang.F("冇權限：manifest.json 要加 \"{0}\"", perm));
    }

    private static string StorageFile(PluginManifest m) => Path.Combine(PluginStore.DataDir(m.Id), "storage.json");
    private static string FilesDir(PluginManifest m) => Path.Combine(PluginStore.DataDir(m.Id), "files");
    private static readonly SemaphoreSlim StoreLock = new(1, 1);

    private static string SafeFile(PluginManifest m, string name)
    {
        string dir = Path.GetFullPath(FilesDir(m)) + Path.DirectorySeparatorChar;
        string p = Path.GetFullPath(Path.Combine(dir, name));
        if (!p.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || name.Trim().Length == 0) throw new UnauthorizedAccessException(Lang.T("檔名唔啱"));
        return p;
    }

    private static T OnUi<T>(Control ui, Func<T> f)
    {
        if (!ui.InvokeRequired) return f();
        return (T)ui.Invoke(f)!;
    }

    private static async Task<JsonNode?> CallAsync(PluginManifest m, string method, JsonArray args, Func<int, string> Arg, Control ui)
    {
        switch (method)
        {
            case "info":
                return new JsonObject { ["id"] = m.Id, ["name"] = m.Name, ["version"] = m.Version, ["lang"] = Lang.Code(Lang.Current), ["app"] = Program.Version, ["theme"] = PluginTheme.Name };

            // ---- 儲存（每個插件自己一份 JSON）----
            case "storage.get":
            case "storage.set":
            case "storage.remove":
            case "storage.all":
            {
                await StoreLock.WaitAsync();
                try
                {
                    var f = StorageFile(m);
                    var o = File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) as JsonObject ?? new JsonObject() : new JsonObject();
                    string k = Arg(0);
                    if (method == "storage.get") return o[k]?.DeepClone();
                    if (method == "storage.all") return o.DeepClone();
                    if (method == "storage.set") o[k] = args.Count > 1 ? args[1]?.DeepClone() : null;
                    else o.Remove(k);
                    string text = o.ToJsonString();
                    if (text.Length > 10 << 20) throw new InvalidOperationException(Lang.T("儲存超過 10 MB 上限"));
                    Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                    File.WriteAllText(f, text);
                    return true;
                }
                finally { StoreLock.Release(); }
            }

            // ---- 加密密鑰 ----
            case "secrets.get":
            case "secrets.set":
            {
                Need(m.Secrets, "secrets");
                var f = Path.Combine(PluginStore.DataDir(m.Id), "secrets.json");
                var o = File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) as JsonObject ?? new JsonObject() : new JsonObject();
                if (method == "secrets.get")
                    return o[Arg(0)] is JsonValue v && v.TryGetValue<string>(out var enc) ? Mail.MailStore.Unprotect(enc) : null;
                o[Arg(0)] = Mail.MailStore.Protect(Arg(1));
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                File.WriteAllText(f, o.ToJsonString());
                return true;
            }

            // ---- 網絡 ----
            case "net.fetch":
            {
                string url = Arg(0);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                    throw new ArgumentException(Lang.T("網址唔啱（要 http / https）"));
                Need(HostAllowed(m, uri.Host), "net: [\"" + uri.Host + "\"]");
                var opt = args.Count > 1 ? args[1] as JsonObject : null;
                var req = new HttpRequestMessage(new HttpMethod(opt?["method"]?.GetValue<string>() ?? "GET"), uri);
                string? body = opt?["body"] is JsonValue bv ? (bv.TryGetValue<string>(out var bs) ? bs : bv.ToJsonString()) : opt?["body"]?.ToJsonString();
                string ctype = "";
                if (opt?["headers"] is JsonObject hs)
                    foreach (var kv in hs)
                    {
                        string hv = kv.Value?.ToString() ?? "";
                        if (kv.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)) { ctype = hv; continue; }
                        req.Headers.TryAddWithoutValidation(kv.Key, hv);
                    }
                if (body != null)
                {
                    req.Content = new StringContent(body, Encoding.UTF8);
                    if (ctype.Length > 0) req.Content.Headers.Remove("Content-Type");
                    if (ctype.Length > 0) req.Content.Headers.TryAddWithoutValidation("Content-Type", ctype);
                }
                if (!req.Headers.UserAgent.Any()) req.Headers.TryAddWithoutValidation("User-Agent", "MultiTool-Plugin/" + Program.Version);
                using var res = await PluginWeb.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                var bytes = await res.Content.ReadAsByteArrayAsync();
                if (bytes.Length > 10 << 20) throw new InvalidOperationException(Lang.T("回應超過 10 MB 上限"));
                var headers = new JsonObject();
                foreach (var h in res.Headers.Concat(res.Content.Headers)) headers[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
                bool binary = opt?["binary"] is JsonValue bin && bin.TryGetValue<bool>(out var bb) && bb;
                return new JsonObject
                {
                    ["status"] = (int)res.StatusCode, ["ok"] = res.IsSuccessStatusCode, ["headers"] = headers,
                    ["text"] = binary ? null : Encoding.UTF8.GetString(bytes), ["base64"] = binary ? Convert.ToBase64String(bytes) : null
                };
            }

            // ---- 通知 / 剪貼簿 / 瀏覽器 ----
            case "notify":
                Need(m.Notify, "notify");
                MultiTool.Scripts.Notifier.Send(Arg(0).Length > 0 ? Arg(0) : m.Name, Arg(1), true, false);
                return true;
            case "clipboard.read":
                Need(m.Clipboard, "clipboard");
                return OnUi(ui, () => Clipboard.ContainsText() ? Clipboard.GetText() : "");
            case "clipboard.write":
                Need(m.Clipboard, "clipboard");
                OnUi(ui, () => { if (Arg(0).Length > 0) Clipboard.SetText(Arg(0)); else Clipboard.Clear(); return true; });
                return true;
            case "open":
            {
                string u = Arg(0);
                if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(Lang.T("淨係開得 http / https 網址"));
                Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
                return true;
            }

            // ---- 檔案：自己個資料夾隨便用；其他要用家揀 ----
            case "file.read":
            {
                var p = SafeFile(m, Arg(0));
                return File.Exists(p) ? File.ReadAllText(p) : null;
            }
            case "file.write":
            {
                var p = SafeFile(m, Arg(0));
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, Arg(1));
                return true;
            }
            case "file.delete":
            {
                var p = SafeFile(m, Arg(0));
                if (File.Exists(p)) File.Delete(p);
                return true;
            }
            case "file.list":
            {
                var d = FilesDir(m);
                var arr = new JsonArray();
                if (Directory.Exists(d))
                    foreach (var f in Directory.GetFiles(d, "*", SearchOption.AllDirectories)) arr.Add(Path.GetRelativePath(d, f).Replace('\\', '/'));
                return arr;
            }
            case "file.pick":
            {
                var opt = args.Count > 0 ? args[0] as JsonObject : null;
                string filter = opt?["filter"]?.GetValue<string>() ?? (Lang.T("所有檔案") + "|*.*");
                bool binary = opt?["binary"] is JsonValue bv && bv.TryGetValue<bool>(out var bb) && bb;
                string? path = OnUi(ui, () =>
                {
                    using var d = new OpenFileDialog { Filter = filter };
                    return d.ShowDialog(ui.FindForm()) == DialogResult.OK ? d.FileName : null;
                });
                if (path == null) return null;
                if (new FileInfo(path).Length > 20 << 20) throw new InvalidOperationException(Lang.T("檔案超過 20 MB 上限"));
                var bytes = await File.ReadAllBytesAsync(path);
                return new JsonObject
                {
                    ["name"] = Path.GetFileName(path), ["size"] = bytes.Length,
                    ["text"] = binary ? null : Encoding.UTF8.GetString(bytes), ["base64"] = binary ? Convert.ToBase64String(bytes) : null
                };
            }
            case "file.save":
            {
                string name = Path.GetFileName(Arg(0));
                bool b64 = args.Count > 2 && args[2] is JsonValue bv && bv.TryGetValue<bool>(out var bb) && bb;
                string? path = OnUi(ui, () =>
                {
                    using var d = new SaveFileDialog { FileName = name.Length > 0 ? name : "file.txt", Filter = Lang.T("所有檔案") + "|*.*" };
                    return d.ShowDialog(ui.FindForm()) == DialogResult.OK ? d.FileName : null;
                });
                if (path == null) return false;
                if (b64) await File.WriteAllBytesAsync(path, Convert.FromBase64String(Arg(1)));
                else await File.WriteAllTextAsync(path, Arg(1));
                return true;
            }

            // ---- AI ----
            case "ai.ask":
                Need(m.Ai, "ai");
                return await Mail.MailAi.AskAsync(Arg(0));

            // ---- 我的腳本 ----
            case "scripts.list":
            {
                Need(m.Scripts, "scripts");
                var arr = new JsonArray();
                foreach (var s in MultiTool.Scripts.Script.LoadAll()) arr.Add(s.Name);
                return arr;
            }
            case "scripts.run":
            {
                Need(m.Scripts, "scripts");
                var s = MultiTool.Scripts.Script.LoadAll().FirstOrDefault(x => x.Name == Arg(0))
                        ?? throw new ArgumentException(Lang.F("搵唔到腳本「{0}」", Arg(0)));
                OnUi(ui, () => { MultiTool.Scripts.ScriptRunner.Start(s); return true; });
                return true;
            }
        }
        throw new NotSupportedException(Lang.F("冇呢個 API：{0}", method));
    }
}

/// <summary>插件跟程式主題：HTML 加入 --mt-* 顏色變數同 data-theme。</summary>
internal static class PluginTheme
{
    public static string Name => UI.Ui.Light ? "light" : "dark";
    private static string H(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    public static string Css =>
        ":root{color-scheme:" + Name +
        ";--mt-bg:" + H(UI.Ui.Bg) + ";--mt-panel:" + H(UI.Ui.Panel) + ";--mt-card:" + H(UI.Ui.Card) + ";--mt-card-hi:" + H(UI.Ui.CardHi) +
        ";--mt-input:" + H(UI.Ui.Input) + ";--mt-border:" + H(UI.Ui.Border) + ";--mt-border-hi:" + H(UI.Ui.BorderHi) +
        ";--mt-text:" + H(UI.Ui.Text) + ";--mt-text-dim:" + H(UI.Ui.TextDim) + ";--mt-text-faint:" + H(UI.Ui.TextFaint) +
        ";--mt-accent:" + H(UI.Ui.Accent) + ";--mt-accent2:" + H(UI.Ui.Accent2) + ";--mt-success:" + H(UI.Ui.Success) + ";--mt-danger:" + H(UI.Ui.Danger) + "}" +
        "html{background:var(--mt-bg);color:var(--mt-text)}";

    // 舊插件（或者 AI）寫死咗 MultiTool 預設深色嘅顏色：自動換做主題變數，日光主題都會跟住轉
    private static readonly (string Hex, string Var)[] Known =
    {
        ("#0e1014", "var(--mt-bg)"), ("#14171e", "var(--mt-panel)"), ("#191d26", "var(--mt-card)"), ("#222836", "var(--mt-card-hi)"),
        ("#11141a", "var(--mt-input)"), ("#2a3040", "var(--mt-border)"), ("#3b4459", "var(--mt-border-hi)"), ("#e7eaf1", "var(--mt-text)"),
        ("#a3abbc", "var(--mt-text-dim)"), ("#8a93a6", "var(--mt-text-dim)"), ("#6c7487", "var(--mt-text-faint)"), ("#4f8cff", "var(--mt-accent)"),
        ("#8c6cff", "var(--mt-accent2)"), ("#2bb673", "var(--mt-success)"), ("#e5484d", "var(--mt-danger)")
    };

    /// <summary>CSS 入面寫死嘅預設深色 → 主題變數（淨係換 CSS 屬性值，唔郁 JavaScript）。</summary>
    public static string MapColors(string css)
    {
        foreach (var (hex, v) in Known)
            css = System.Text.RegularExpressions.Regex.Replace(css, System.Text.RegularExpressions.Regex.Escape(hex) + @"\b", v, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return css;
    }

    public static string Inject(string html)
    {
        // <style> 入面同 style="..." 嘅寫死顏色換做主題變數
        html = System.Text.RegularExpressions.Regex.Replace(html, @"(<style\b[^>]*>)(.*?)(</style>)", m => m.Groups[1].Value + MapColors(m.Groups[2].Value) + m.Groups[3].Value,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"\bstyle\s*=\s*""([^""]*)""", m => "style=\"" + MapColors(m.Groups[1].Value) + "\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string style = "<style id=\"mt-theme\">" + Css + "</style>";
        var h = System.Text.RegularExpressions.Regex.Match(html, @"<html\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (h.Success && !h.Value.Contains("data-theme", StringComparison.OrdinalIgnoreCase))
            html = html.Insert(h.Index + 5, " data-theme=\"" + Name + "\"");
        var m = System.Text.RegularExpressions.Regex.Match(html, @"<head\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return html.Insert(m.Index + m.Length, style);
        h = System.Text.RegularExpressions.Regex.Match(html, @"<html\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return h.Success ? html.Insert(h.Index + h.Length, "<head>" + style + "</head>") : style + html;
    }
}

/// <summary>注入每個插件頁面嘅 mt.* 物件。</summary>
internal static class PluginBridgeJs
{
    public const string Script = """
(() => {
  if (window.mt) return;
  const w = window.chrome && window.chrome.webview;
  if (!w) return;
  let seq = 0; const pend = {};
  w.addEventListener('message', e => {
    const m = e.data;
    if (m && m.__mt) { const p = pend[m.id]; if (!p) return; delete pend[m.id]; m.ok ? p.res(m.result) : p.rej(new Error(m.error)); }
    else if (m && m.__mtev) { const f = cbs[m.cb]; if (f) { try { f(m.data); } catch (err) { console.error(err); } } }
  });
  // mt.ui 事件 callback
  let cbSeq = 0; const cbs = {};
  const cb = f => { if (typeof f !== 'function') return 0; const id = ++cbSeq; cbs[id] = f; return id; };
  const spec = o => { const r = {}; for (const k in (o || {})) { const v = o[k]; if (typeof v === 'function') r['__cb_' + k] = cb(v); else r[k] = v; } return r; };
  const call = (method, args) => new Promise((res, rej) => { const id = ++seq; pend[id] = { res, rej }; w.postMessage({ __mt: 1, id, method, args: args || [] }); });
  const fmt = a => a.map(x => typeof x === 'string' ? x : (x instanceof Error ? (x.stack || x.message) : (() => { try { return JSON.stringify(x); } catch { return String(x); } })())).join(' ');
  const send = (lvl, a) => { try { w.postMessage({ __mtlog: 1, lvl, text: fmt(a) }); } catch {} };
  for (const lvl of ['log', 'info', 'warn', 'error']) { const o = console[lvl].bind(console); console[lvl] = (...a) => { send(lvl === 'log' ? 'info' : lvl, a); o(...a); }; }
  window.addEventListener('error', e => send('error', [e.message + ' (' + (e.filename || '').split('/').pop() + ':' + e.lineno + ')']));
  window.addEventListener('unhandledrejection', e => send('error', ['Promise: ' + (e.reason && e.reason.message || e.reason)]));
  window.mt = {
    info: () => call('info'),
    log: (...a) => send('info', a),
    storage: { get: k => call('storage.get', [k]), set: (k, v) => call('storage.set', [k, v]), remove: k => call('storage.remove', [k]), all: () => call('storage.all') },
    secrets: { get: k => call('secrets.get', [k]), set: (k, v) => call('secrets.set', [k, v]) },
    net: {
      fetch: (url, o) => call('net.fetch', [url, o || {}]),
      json: async (url, o) => { const r = await call('net.fetch', [url, o || {}]); if (!r.ok) throw new Error('HTTP ' + r.status); return JSON.parse(r.text); }
    },
    notify: (title, body) => call('notify', [title, body || '']),
    clipboard: { read: () => call('clipboard.read'), write: t => call('clipboard.write', [t]) },
    file: {
      read: n => call('file.read', [n]), write: (n, t) => call('file.write', [n, t]), delete: n => call('file.delete', [n]), list: () => call('file.list'),
      pick: o => call('file.pick', [o || {}]), save: (name, content, base64) => call('file.save', [name, content, !!base64])
    },
    open: url => call('open', [url]),
    ai: { ask: p => call('ai.ask', [p]) },
    scripts: { list: () => call('scripts.list'), run: n => call('scripts.run', [n]) },
    ui: {
      override: (sel, props) => call('ui.override', [sel, props || {}]),
      hide: sel => call('ui.override', [sel, { visible: false }]),
      add: o => call('ui.add', [spec(o)]),
      on: (event, sel, f) => { if (typeof sel === 'function') { f = sel; sel = ''; } return call('ui.on', [event, sel || '', cb(f)]); },
      remove: id => call('ui.remove', [id]),
      reset: () => call('ui.reset'),
      query: sel => call('ui.query', [sel]),
      tree: (sel, depth) => call('ui.tree', [sel || '@main', depth || 6]),
      click: sel => call('ui.click', [sel]),
      setValue: (sel, v) => call('ui.setValue', [sel, v])
    },
    app: {
      page: () => call('app.page'), pages: () => call('app.pages'), open: key => call('app.open', [key]),
      addTheme: t => call('app.addTheme', [t || {}]), removeTheme: id => call('app.removeTheme', [id]),
      themes: () => call('app.themes'), useTheme: id => call('app.useTheme', [id])
    }
  };
})();
""";
}

/// <summary>新插件範本 + 俾 AI 睇嘅 API 說明。</summary>
internal static partial class PluginTemplates
{
    public static string Manifest(string name) => "{\n" +
        "  \"name\": " + JsonSerializer.Serialize(name) + ",\n" +
        "  \"version\": \"1.0.0\",\n" +
        "  \"author\": \"\",\n" +
        "  \"description\": \"\",\n" +
        "  \"icon\": \"🧩\",\n" +
        "  \"entry\": \"ui.html\",\n" +
        "  \"background\": false,\n" +
        "  \"permissions\": {\n" +
        "    \"net\": [],\n" +
        "    \"notify\": true\n" +
        "  }\n" +
        "}\n";

    private const string HtmlHant = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<style>
  /* 顏色使用 --mt-* 變數，會跟隨 MultiTool 的日光／深色主題自動切換 */
  body { background:var(--mt-bg); color:var(--mt-text); font-family:'Segoe UI','Microsoft JhengHei UI',sans-serif; margin:24px; }
  .card { background:var(--mt-card); border:1px solid var(--mt-border); border-radius:12px; padding:20px; max-width:520px; }
  button { background:var(--mt-accent); color:#fff; border:0; border-radius:8px; padding:8px 16px; font-size:14px; cursor:pointer; }
  button:hover { filter:brightness(1.1); }
  input { background:var(--mt-input); color:var(--mt-text); border:1px solid var(--mt-border); border-radius:8px; padding:8px 10px; font-size:14px; }
</style>
</head>
<body>
  <div class="card">
    <h2>👋 Hello</h2>
    <p>已按 <b id="count">0</b> 次</p>
    <input id="msg" placeholder="通知內容" value="Hello from my plugin!">
    <button id="btn">按我</button>
  </div>
  <script src="main.js"></script>
</body>
</html>
""";

    private const string HtmlHans = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<style>
  /* 颜色使用 --mt-* 变数，会跟随 MultiTool 的日光／深色主题自动切换 */
  body { background:var(--mt-bg); color:var(--mt-text); font-family:'Segoe UI','Microsoft JhengHei UI',sans-serif; margin:24px; }
  .card { background:var(--mt-card); border:1px solid var(--mt-border); border-radius:12px; padding:20px; max-width:520px; }
  button { background:var(--mt-accent); color:#fff; border:0; border-radius:8px; padding:8px 16px; font-size:14px; cursor:pointer; }
  button:hover { filter:brightness(1.1); }
  input { background:var(--mt-input); color:var(--mt-text); border:1px solid var(--mt-border); border-radius:8px; padding:8px 10px; font-size:14px; }
</style>
</head>
<body>
  <div class="card">
    <h2>👋 Hello</h2>
    <p>已按 <b id="count">0</b> 次</p>
    <input id="msg" placeholder="通知内容" value="Hello from my plugin!">
    <button id="btn">按我</button>
  </div>
  <script src="main.js"></script>
</body>
</html>
""";

    private const string HtmlEn = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<style>
  /* Use the --mt-* colour variables so the plugin follows MultiTool's light / dark theme */
  body { background:var(--mt-bg); color:var(--mt-text); font-family:'Segoe UI','Microsoft JhengHei UI',sans-serif; margin:24px; }
  .card { background:var(--mt-card); border:1px solid var(--mt-border); border-radius:12px; padding:20px; max-width:520px; }
  button { background:var(--mt-accent); color:#fff; border:0; border-radius:8px; padding:8px 16px; font-size:14px; cursor:pointer; }
  button:hover { filter:brightness(1.1); }
  input { background:var(--mt-input); color:var(--mt-text); border:1px solid var(--mt-border); border-radius:8px; padding:8px 10px; font-size:14px; }
</style>
</head>
<body>
  <div class="card">
    <h2>👋 Hello</h2>
    <p>Clicked <b id="count">0</b> times</p>
    <input id="msg" placeholder="Notification text" value="Hello from my plugin!">
    <button id="btn">Click me</button>
  </div>
  <script src="main.js"></script>
</body>
</html>
""";

    public static string Html => Lang.Base switch { Language.En => HtmlEn, Language.ZhHans => HtmlHans, _ => HtmlHant };

    private const string JsHant = """
// 在此編寫工具的邏輯。可使用 mt.* API（詳見上方「API 說明」）
const countEl = document.getElementById('count');

async function init() {
  const n = (await mt.storage.get('count')) || 0;   // 讀取上次儲存的次數
  countEl.textContent = n;
}

document.getElementById('btn').onclick = async () => {
  const n = ((await mt.storage.get('count')) || 0) + 1;
  await mt.storage.set('count', n);
  countEl.textContent = n;
  await mt.notify('Hello', document.getElementById('msg').value);
  console.log('已按', n, '次');
};

init();
""";

    private const string JsHans = """
// 在此编写工具的逻辑。可使用 mt.* API（详见上方「API 说明」）
const countEl = document.getElementById('count');

async function init() {
  const n = (await mt.storage.get('count')) || 0;   // 读取上次储存的次数
  countEl.textContent = n;
}

document.getElementById('btn').onclick = async () => {
  const n = ((await mt.storage.get('count')) || 0) + 1;
  await mt.storage.set('count', n);
  countEl.textContent = n;
  await mt.notify('Hello', document.getElementById('msg').value);
  console.log('已按', n, '次');
};

init();
""";

    private const string JsEn = """
// Write your tool's logic here. You can use the mt.* API (see "API reference" above).
const countEl = document.getElementById('count');

async function init() {
  const n = (await mt.storage.get('count')) || 0;   // read the count saved last time
  countEl.textContent = n;
}

document.getElementById('btn').onclick = async () => {
  const n = ((await mt.storage.get('count')) || 0) + 1;
  await mt.storage.set('count', n);
  countEl.textContent = n;
  await mt.notify('Hello', document.getElementById('msg').value);
  console.log('Clicked', n, 'times');
};

init();
""";

    public static string Js => Lang.Base switch { Language.En => JsEn, Language.ZhHans => JsHans, _ => JsHant };

    private const string ApiDocHant = """
MultiTool 插件 API（v1）

每個插件是一個資料夾：
  manifest.json   名稱、版本、圖示、入口、權限
  ui.html         介面（一般 HTML / CSS / JavaScript）
  main.js 等      其他檔案，在 ui.html 以 <script src="main.js"> 載入

manifest.json：
{
  "name": "工具名稱", "version": "1.0.0", "author": "", "description": "", "icon": "🧩",
  "entry": "ui.html",
  "permissions": {
    "net": ["api.example.com", "*.example.org"],   // 允許連接的網站；"*" = 任何網站（會顯示警告）
    "notify": true,               // mt.notify
    "clipboard": true,            // mt.clipboard
    "secrets": true,              // mt.secrets（加密儲存 API key）
    "ai": true,                   // mt.ai.ask
    "scripts": true               // mt.scripts（開始「我的腳本」）
  }
}

所有 API 都是非同步（回傳 Promise），須使用 await：
  mt.info()                          → { id, name, version, lang, app, theme }
  mt.log(...)                        → 寫入編寫器的記錄（console.log 亦可）
  mt.storage.get(key) / set(key, value) / remove(key) / all()   插件專用的儲存（JSON 值）
  mt.secrets.get(key) / set(key, text)                           加密儲存（需要 "secrets"）
  mt.net.fetch(url, { method, headers, body, binary })           → { status, ok, headers, text, base64 }（網站需在 "net" 內，沒有 CORS 限制）
  mt.net.json(url, options)                                       → 直接回傳已解析的 JSON
  mt.notify(title, body)                                          Windows 通知（需要 "notify"）
  mt.clipboard.read() / write(text)                               （需要 "clipboard"）
  mt.file.read(name) / write(name, text) / delete(name) / list()  插件專用的檔案資料夾
  mt.file.pick({ filter: "文字檔|*.txt", binary })                 讓使用者選擇檔案 → { name, size, text, base64 }
  mt.file.save(name, content, isBase64)                           讓使用者選擇儲存位置
  mt.open(url)                                                    以瀏覽器開啟網址
  mt.ai.ask(prompt)                                               → AI 回覆文字（需要 "ai"）
  mt.scripts.list() / run(name)                                   （需要 "scripts"）

注意：
  • 頁面直接 fetch 或 <img> 只能連接 manifest "net" 允許的網站，其他會被封鎖；呼叫 API 建議使用 mt.net.fetch。
  • 主題：介面會跟隨 MultiTool 的日光／深色主題。顏色請使用以下 CSS 變數，不要寫死顏色：
      var(--mt-bg) 背景、var(--mt-card) 卡片、var(--mt-card-hi) 卡片（滑鼠經過）、var(--mt-input) 輸入框、
      var(--mt-border) 邊框、var(--mt-text) 文字、var(--mt-text-dim) 次要文字、var(--mt-text-faint) 提示文字、
      var(--mt-accent) 強調色、var(--mt-accent2)、var(--mt-success) 成功、var(--mt-danger) 錯誤
    <html> 有 data-theme="light" 或 "dark"；mt.info().theme 亦可取得目前主題。
  • 加到左側工具欄的插件會在開啟 MultiTool 時自動運行，可用 setInterval 定時執行。
  • 介面文字請使用書面語。
""";

    private const string ApiDocHans = """
MultiTool 插件 API（v1）

每个插件是一个资料夹：
  manifest.json   名称、版本、图示、入口、权限
  ui.html         介面（一般 HTML / CSS / JavaScript）
  main.js 等      其他档案，在 ui.html 以 <script src="main.js"> 载入

manifest.json：
{
  "name": "工具名称", "version": "1.0.0", "author": "", "description": "", "icon": "🧩",
  "entry": "ui.html",
  "permissions": {
    "net": ["api.example.com", "*.example.org"],   // 允许连接的网站；"*" = 任何网站（会显示警告）
    "notify": true,               // mt.notify
    "clipboard": true,            // mt.clipboard
    "secrets": true,              // mt.secrets（加密储存 API key）
    "ai": true,                   // mt.ai.ask
    "scripts": true               // mt.scripts（开始「我的脚本」）
  }
}

所有 API 都是非同步（回传 Promise），须使用 await：
  mt.info()                          → { id, name, version, lang, app, theme }
  mt.log(...)                        → 写入编写器的记录（console.log 亦可）
  mt.storage.get(key) / set(key, value) / remove(key) / all()   插件专用的储存（JSON 值）
  mt.secrets.get(key) / set(key, text)                           加密储存（需要 "secrets"）
  mt.net.fetch(url, { method, headers, body, binary })           → { status, ok, headers, text, base64 }（网站需在 "net" 内，没有 CORS 限制）
  mt.net.json(url, options)                                       → 直接回传已解析的 JSON
  mt.notify(title, body)                                          Windows 通知（需要 "notify"）
  mt.clipboard.read() / write(text)                               （需要 "clipboard"）
  mt.file.read(name) / write(name, text) / delete(name) / list()  插件专用的档案资料夹
  mt.file.pick({ filter: "文字档|*.txt", binary })                 让使用者选择档案 → { name, size, text, base64 }
  mt.file.save(name, content, isBase64)                           让使用者选择储存位置
  mt.open(url)                                                    以浏览器开启网址
  mt.ai.ask(prompt)                                               → AI 回复文字（需要 "ai"）
  mt.scripts.list() / run(name)                                   （需要 "scripts"）

注意：
  • 页面直接 fetch 或 <img> 只能连接 manifest "net" 允许的网站，其他会被封锁；呼叫 API 建议使用 mt.net.fetch。
  • 主题：介面会跟随 MultiTool 的日光／深色主题。颜色请使用以下 CSS 变数，不要写死颜色：
      var(--mt-bg) 背景、var(--mt-card) 卡片、var(--mt-card-hi) 卡片（滑鼠经过）、var(--mt-input) 输入框、
      var(--mt-border) 边框、var(--mt-text) 文字、var(--mt-text-dim) 次要文字、var(--mt-text-faint) 提示文字、
      var(--mt-accent) 强调色、var(--mt-accent2)、var(--mt-success) 成功、var(--mt-danger) 错误
    <html> 有 data-theme="light" 或 "dark"；mt.info().theme 亦可取得目前主题。
  • 加到左侧工具栏的插件会在开启 MultiTool 时自动运行，可用 setInterval 定时执行。
  • 介面文字请使用书面语。
""";

    private const string ApiDocEn = """
MultiTool plugin API (v1)

A plugin is a folder:
  manifest.json   name, version, icon, entry page, permissions
  ui.html         the interface (plain HTML / CSS / JavaScript)
  main.js etc.    other files, loaded from ui.html with <script src="main.js">

manifest.json:
{
  "name": "Tool name", "version": "1.0.0", "author": "", "description": "", "icon": "🧩",
  "entry": "ui.html",
  "permissions": {
    "net": ["api.example.com", "*.example.org"],   // websites it may connect to; "*" = any website (shows a warning)
    "notify": true,               // mt.notify
    "clipboard": true,            // mt.clipboard
    "secrets": true,              // mt.secrets (encrypted storage for API keys)
    "ai": true,                   // mt.ai.ask
    "scripts": true               // mt.scripts (start "My Scripts")
  }
}

Every API is asynchronous (returns a Promise) — use await:
  mt.info()                          → { id, name, version, lang, app, theme }
  mt.log(...)                        → writes to the editor's log (console.log works too)
  mt.storage.get(key) / set(key, value) / remove(key) / all()   the plugin's own storage (JSON values)
  mt.secrets.get(key) / set(key, text)                           encrypted storage (needs "secrets")
  mt.net.fetch(url, { method, headers, body, binary })           → { status, ok, headers, text, base64 } (site must be in "net"; no CORS limits)
  mt.net.json(url, options)                                       → the parsed JSON
  mt.notify(title, body)                                          Windows notification (needs "notify")
  mt.clipboard.read() / write(text)                               (needs "clipboard")
  mt.file.read(name) / write(name, text) / delete(name) / list()  the plugin's own file folder
  mt.file.pick({ filter: "Text files|*.txt", binary })            let the user pick a file → { name, size, text, base64 }
  mt.file.save(name, content, isBase64)                           let the user choose where to save
  mt.open(url)                                                    open a URL in the browser
  mt.ai.ask(prompt)                                               → the AI's reply text (needs "ai")
  mt.scripts.list() / run(name)                                   (needs "scripts")

Notes:
  • fetch / <img> in the page can only reach sites allowed in the manifest's "net"; others are blocked. Use mt.net.fetch for API calls.
  • Theme: the interface follows MultiTool's light / dark theme. Use these CSS variables instead of fixed colours:
      var(--mt-bg) background, var(--mt-card) card, var(--mt-card-hi) card (hover), var(--mt-input) input box,
      var(--mt-border) border, var(--mt-text) text, var(--mt-text-dim) secondary text, var(--mt-text-faint) hint text,
      var(--mt-accent) accent, var(--mt-accent2), var(--mt-success) success, var(--mt-danger) error
    <html> has data-theme="light" or "dark"; mt.info().theme also returns the current theme.
  • Plugins added to the sidebar run automatically when MultiTool opens; use setInterval for timed jobs.
  • Write interface text in the user's interface language.
""";

    public static string ApiDoc => Lang.Base switch { Language.En => ApiDocEn, Language.ZhHans => ApiDocHans, _ => ApiDocHant };

}

/// <summary>開咗嘅插件（背景插件開程式就開始）。</summary>
internal static class PluginRuntime
{
    public static readonly Dictionary<string, PluginView> Running = new();
    public static event Action? Changed;

    public static void Stop(string id)
    {
        if (Running.Remove(id, out var v)) { v.Close(); Changed?.Invoke(); }
    }

    public static void Add(string id, PluginView v) { Running[id] = v; Changed?.Invoke(); }
}

/// <summary>顯示一個插件嘅格（WebView2）。</summary>
internal sealed class PluginView : Panel
{
    private object? _web;           // PluginWeb
    private readonly Label _msg;
    public PluginManifest Manifest { get; private set; }
    public bool Dev { get; }
    public event Action<string, string>? Log;

    public PluginView(PluginManifest m, bool dev)
    {
        Manifest = m;
        Dev = dev;
        BackColor = UI.Ui.Bg;
        Dock = DockStyle.Fill;
        _msg = UI.Ui.Text_(Lang.T("載入緊…"), UI.Ui.TextDim, UI.Ui.F(10f), new Padding(0));
        _msg.AutoSize = false; _msg.Dock = DockStyle.Fill; _msg.TextAlign = ContentAlignment.MiddleCenter;
        Controls.Add(_msg);
    }

    public async Task StartAsync()
    {
        if (!Mail.WebView2Lib.Prepare())
        {
            _msg.Text = Lang.T("插件要用 Microsoft Edge WebView2 Runtime（Windows 10 / 11 通常已經有）。\n請喺 Microsoft 網站下載安裝之後再試。");
            return;
        }
        try
        {
            _web = await Create();
            _msg.Visible = false;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("開插件", ex);
            _msg.Text = Lang.T("開唔到插件：") + ex.Message;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<object> Create()
    {
        var w = await PluginWeb.CreateAsync(this, Manifest, Dev, (l, t) => BeginInvokeLog(l, t));
        w.Navigating = () => Navigating?.Invoke();
        return w;
    }

    /// <summary>頁面重新載入（插件：清走佢舊嘅 override）。</summary>
    public event Action? Navigating;

    /// <summary>傳 JSON 俾頁面（mt.ui 事件）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Post(string json) { if (_web is PluginWeb w) w.Post(json); }

    private void BeginInvokeLog(string l, string t)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => Log?.Invoke(l, t)); } catch { } }
        else Log?.Invoke(l, t);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Reload(PluginManifest m)
    {
        Manifest = m;
        if (_web is PluginWeb w) w.Reload(m);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void DevTools() { if (_web is PluginWeb w) w.OpenDevTools(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Close()
    {
        if (_web is PluginWeb w) w.Close();
        _web = null;
        Dispose();
    }
}
