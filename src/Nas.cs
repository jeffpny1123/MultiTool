using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTool.Tools;

// =====================================================================
//  Synology NAS：用 DSM Web API（/webapi/entry.cgi）登入、檔案管理、系統狀態、開關機
//  • 密碼同兩步驗證嘅「信任裝置」記號用 DPAPI 加密，存喺 data\nas\nas.json
//  • HTTPS 自簽證書：第一次問用家信唔信，信就記低指紋（之後指紋唔同會再問）
// =====================================================================

/// <summary>一部 NAS 嘅連線設定。</summary>
internal sealed class NasProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5001;
    public bool Https { get; set; } = true;
    public string User { get; set; } = "";
    public string Secret { get; set; } = "";       // 密碼（DPAPI）
    public string Device { get; set; } = "";       // 兩步驗證信任裝置記號（DPAPI）
    public string CertThumb { get; set; } = "";    // 信任咗嘅自簽證書指紋
    public string Mac { get; set; } = "";          // Wake-on-LAN 用
    public bool LastOk { get; set; }               // 曾經成功連線

    public string BaseUrl => (Https ? "https://" : "http://") + Host.Trim() + ":" + Port;
}

internal static class NasStore
{
    private static string Dir => Path.Combine(AppPaths.Data, "nas");
    private static string FilePath => Path.Combine(Dir, "nas.json");
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static List<NasProfile> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<NasProfile>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex) { AppPaths.LogError("讀 NAS 設定", ex); return new(); }
    }

    public static void Save(List<NasProfile> list)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Opt)); }
        catch (Exception ex) { AppPaths.LogError("存 NAS 設定", ex); }
    }

    // 上次連線成功嘅帳號
    private static string LastPath => Path.Combine(Dir, "last.txt");
    public static string LastId
    {
        get { try { return File.Exists(LastPath) ? File.ReadAllText(LastPath).Trim() : ""; } catch { return ""; } }
        set { try { Directory.CreateDirectory(Dir); File.WriteAllText(LastPath, value); } catch { } }
    }

    // 檔案顯示方式（格狀 / 清單），記住上次揀嘅
    private static string ViewPath => Path.Combine(Dir, "view.txt");
    public static bool GridView
    {
        get { try { return !File.Exists(ViewPath) || File.ReadAllText(ViewPath).Trim() != "list"; } catch { return true; } }
        set { try { Directory.CreateDirectory(Dir); File.WriteAllText(ViewPath, value ? "grid" : "list"); } catch { } }
    }

    public static string Protect(string s) => Mail.MailStore.Protect(s);
    public static string Unprotect(string s) => Mail.MailStore.Unprotect(s);
}

/// <summary>DSM 回覆錯誤（code = DSM 錯誤碼）。</summary>
internal sealed class NasException : Exception
{
    public int Code { get; }
    public NasException(int code, string msg) : base(msg) => Code = code;
}

/// <summary>HTTPS 證書唔受信任：要用家確認。</summary>
internal sealed class NasCertException : Exception
{
    public string Thumb { get; }
    public string Subject { get; }
    public NasCertException(string thumb, string subject) : base("cert") { Thumb = thumb; Subject = subject; }
}

/// <summary>檔案 / 資料夾。</summary>
/// <summary>NAS 檔案操作介面。路徑一律用「/」分隔，「/」= 最頂層。</summary>
internal interface ICloud : IDisposable
{
    bool Connected { get; }
    Task<List<NasEntry>> ListAsync(string folder, CancellationToken ct = default);
    Task CreateFolderAsync(string parent, string name);
    Task RenameAsync(string path, string name);
    Task DeleteAsync(IEnumerable<string> paths, CancellationToken ct = default);
    Task<string> ShareAsync(string path);
    Task DownloadAsync(string path, bool isDir, string dest, Action<long, long> progress, CancellationToken ct);
    Task<byte[]?> ThumbAsync(string path, string size, CancellationToken ct = default);
    Task UploadAsync(string file, string folder, bool overwrite, Action<long, long> progress, CancellationToken ct);
    Task LogoutAsync();
}

internal sealed record NasEntry(string Name, string Path, bool IsDir, long Size, DateTime Modified);

/// <summary>系統狀態（讀唔到嘅欄位係 null）。</summary>
internal sealed class NasStatus
{
    public string? Model, Dsm, Uptime;
    public int? Temp, Cpu, Ram;
    public long? RamTotal, Rx, Tx;
    public readonly List<(string Name, long Used, long Total, string Status)> Volumes = new();
    public readonly List<(string Name, string Model, int? Temp, string Status)> Disks = new();
}

internal sealed class NasClient : ICloud
{
    public NasProfile P { get; }
    private readonly HttpClient _http;
    private string? _sid;
    private readonly Dictionary<string, (string Path, int Max)> _apis = new(StringComparer.OrdinalIgnoreCase);
    private X509Certificate2? _lastCert;
    private const string Session = "RabiToolkit";

    public bool Connected => _sid != null;

    public NasClient(NasProfile p)
    {
        P = p;
        var h = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                if (cert == null) return false;
                _lastCert = new X509Certificate2(cert);
                return P.CertThumb.Length > 0 && string.Equals(cert.GetCertHashString(), P.CertThumb, StringComparison.OrdinalIgnoreCase);
            }
        };
        _http = new HttpClient(h) { Timeout = TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(Program.FileName);
    }

    public void Dispose()
    {
        try { if (_sid != null) _ = LogoutAsync(); } catch { }
        _http.Dispose();
    }

    // ================= 基本呼叫 =================

    private string Url(string api) => P.BaseUrl + "/webapi/" + (_apis.TryGetValue(api, out var a) ? a.Path : api == "SYNO.API.Info" ? "query.cgi" : "entry.cgi");
    private int Ver(string api, int want) => _apis.TryGetValue(api, out var a) ? Math.Min(want, a.Max) : want;

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, HttpCompletionOption opt, CancellationToken ct)
    {
        try { return await _http.SendAsync(req, opt, ct); }
        catch (HttpRequestException) when (_lastCert != null && (P.CertThumb.Length == 0 || !string.Equals(_lastCert.GetCertHashString(), P.CertThumb, StringComparison.OrdinalIgnoreCase)))
        {
            throw new NasCertException(_lastCert.GetCertHashString(), _lastCert.Subject);
        }
    }

    /// <summary>叫一個 API（POST 表格），回傳 data；出錯 throw NasException。</summary>
    private async Task<JsonElement> CallAsync(string api, string method, int version, IEnumerable<KeyValuePair<string, string>>? args = null,
        CancellationToken ct = default, bool retry = true)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("api", api), new("version", Ver(api, version).ToString()), new("method", method)
        };
        if (args != null) form.AddRange(args);
        if (_sid != null) form.Add(new("_sid", _sid));
        using var req = new HttpRequestMessage(HttpMethod.Post, Url(api)) { Content = new FormUrlEncodedContent(form) };
        using var resp = await SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement.Clone(); }
        catch { throw new NasException(-1, Lang.F("NAS 回覆唔係 JSON（HTTP {0}）", (int)resp.StatusCode)); }
        if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
            return root.TryGetProperty("data", out var d) ? d : default;
        int code = root.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : -1;
        // 登入過期：重新登入再試一次
        if (retry && code is 106 or 107 or 119 && api != "SYNO.API.Auth")
        {
            _sid = null;
            await LoginAsync(null, ct);
            return await CallAsync(api, method, version, args, ct, false);
        }
        throw new NasException(code, Error(api, code));
    }

    private static string J(params string[] paths) => JsonSerializer.Serialize(paths);

    // ================= 登入 =================

    /// <summary>登入；要兩步驗證碼就 throw NasException(403)，再用 otp 叫多次。</summary>
    public async Task LoginAsync(string? otp, CancellationToken ct = default)
    {
        if (_apis.Count == 0)
        {
            var info = await CallAsync("SYNO.API.Info", "query", 1, new[] { new KeyValuePair<string, string>("query", "all") }, ct, false);
            foreach (var p in info.EnumerateObject())
                if (p.Value.TryGetProperty("path", out var path) && p.Value.TryGetProperty("maxVersion", out var mv))
                    _apis[p.Name] = (path.GetString() ?? "entry.cgi", mv.GetInt32());
        }
        var args = new List<KeyValuePair<string, string>>
        {
            new("account", P.User), new("passwd", NasStore.Unprotect(P.Secret)), new("session", Session), new("format", "sid"),
            new("device_name", Program.FileName)
        };
        string dev = P.Device.Length > 0 ? NasStore.Unprotect(P.Device) : "";
        if (dev.Length > 0) args.Add(new("device_id", dev));
        if (!string.IsNullOrWhiteSpace(otp)) { args.Add(new("otp_code", otp.Trim())); args.Add(new("enable_device_token", "yes")); }
        var d = await CallAsync("SYNO.API.Auth", "login", 6, args, ct, false);
        _sid = d.TryGetProperty("sid", out var sid) ? sid.GetString() : null;
        if (_sid == null) throw new NasException(-1, Lang.T("登入失敗：NAS 冇俾 session"));
        // 記住呢部電腦（下次唔使再輸驗證碼）
        if (d.TryGetProperty("did", out var did) && did.GetString() is { Length: > 0 } ds) P.Device = NasStore.Protect(ds);
        else if (d.TryGetProperty("device_id", out var did2) && did2.GetString() is { Length: > 0 } ds2) P.Device = NasStore.Protect(ds2);
    }

    public async Task LogoutAsync()
    {
        if (_sid == null) return;
        try { await CallAsync("SYNO.API.Auth", "logout", 6, new[] { new KeyValuePair<string, string>("session", Session) }, default, false); } catch { }
        _sid = null;
    }

    /// <summary>用家信任咗呢張證書：記低指紋。</summary>
    public void TrustCert(string thumb) => P.CertThumb = thumb;

    // ================= 檔案 =================

    public async Task<List<NasEntry>> ListAsync(string folder, CancellationToken ct = default)
    {
        var res = new List<NasEntry>();
        if (folder is "" or "/")
        {
            var d = await CallAsync("SYNO.FileStation.List", "list_share", 2, new[]
            {
                new KeyValuePair<string, string>("additional", J("time", "size")), new("limit", "1000")
            }, ct);
            foreach (var s in Arr(d, "shares")) res.Add(Entry(s, true));
            return res;
        }
        int offset = 0;
        while (true)
        {
            var d = await CallAsync("SYNO.FileStation.List", "list", 2, new[]
            {
                new KeyValuePair<string, string>("folder_path", folder), new("additional", J("size", "time")),
                new("offset", offset.ToString()), new("limit", "1000"), new("sort_by", "name")
            }, ct);
            var files = Arr(d, "files").ToList();
            foreach (var f in files) res.Add(Entry(f, f.TryGetProperty("isdir", out var isd) && isd.ValueKind == JsonValueKind.True));
            int total = d.TryGetProperty("total", out var t) && t.TryGetInt32(out var tn) ? tn : res.Count;
            offset += files.Count;
            if (files.Count == 0 || offset >= total) break;
        }
        return res.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IEnumerable<JsonElement> Arr(JsonElement d, string name) =>
        d.ValueKind == JsonValueKind.Object && d.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static NasEntry Entry(JsonElement e, bool dir)
    {
        string name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        string path = e.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        long size = 0;
        DateTime mod = DateTime.MinValue;
        if (e.TryGetProperty("additional", out var a))
        {
            if (a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz)) size = sz;
            if (a.TryGetProperty("time", out var t) && t.TryGetProperty("mtime", out var mt) && mt.TryGetInt64(out var unix))
                mod = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        }
        return new NasEntry(name, path, dir, size, mod);
    }

    public Task CreateFolderAsync(string parent, string name) =>
        CallAsync("SYNO.FileStation.CreateFolder", "create", 2, new[]
        {
            new KeyValuePair<string, string>("folder_path", J(parent)), new("name", J(name)), new("force_parent", "false")
        });

    public Task RenameAsync(string path, string name) =>
        CallAsync("SYNO.FileStation.Rename", "rename", 2, new[] { new KeyValuePair<string, string>("path", J(path)), new("name", J(name)) });

    /// <summary>刪除（背景工作，等佢做完）。</summary>
    public async Task DeleteAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var d = await CallAsync("SYNO.FileStation.Delete", "start", 2, new[]
        {
            new KeyValuePair<string, string>("path", J(paths.ToArray())), new("recursive", "true")
        }, ct);
        string task = d.TryGetProperty("taskid", out var t) ? t.GetString() ?? "" : "";
        if (task.Length == 0) return;
        for (int i = 0; i < 600; i++)
        {
            await Task.Delay(500, ct);
            var s = await CallAsync("SYNO.FileStation.Delete", "status", 2, new[] { new KeyValuePair<string, string>("taskid", JsonSerializer.Serialize(task)) }, ct);
            if (s.TryGetProperty("finished", out var f) && f.ValueKind == JsonValueKind.True) return;
        }
    }

    /// <summary>分享連結。</summary>
    public async Task<string> ShareAsync(string path)
    {
        var d = await CallAsync("SYNO.FileStation.Sharing", "create", 3, new[] { new KeyValuePair<string, string>("path", J(path)) });
        foreach (var l in Arr(d, "links"))
            if (l.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } url) return url;
        throw new NasException(-1, Lang.T("NAS 冇俾分享連結"));
    }

    /// <summary>下載（資料夾會變 zip）去 dest。progress(已下載, 總數或 -1)。</summary>
    public async Task DownloadAsync(string path, bool isDir, string dest, Action<long, long> progress, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("api", "SYNO.FileStation.Download"), new("version", Ver("SYNO.FileStation.Download", 2).ToString()), new("method", "download"),
            new("path", J(path)), new("mode", "download"), new("_sid", _sid ?? "")
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, Url("SYNO.FileStation.Download")) { Content = new FormUrlEncodedContent(form) };
        using var resp = await SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        // 出錯時 NAS 回 JSON
        if (resp.Content.Headers.ContentType?.MediaType is "application/json" or "text/plain")
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            int code = -1;
            try { var r = JsonDocument.Parse(body).RootElement; code = r.GetProperty("error").GetProperty("code").GetInt32(); } catch { }
            throw new NasException(code, Error("SYNO.FileStation.Download", code));
        }
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        string tmp = dest + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress(done, total);
            }
        }
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
    }

    /// <summary>上載一個檔案去 folder。檔案已存在而 overwrite = false 就 throw NasException(414)。</summary>
    /// <summary>縮圖（相片 / 影片）；攞唔到就回 null。size：small / medium / large。</summary>
    public async Task<byte[]?> ThumbAsync(string path, string size, CancellationToken ct = default)
    {
        if (_sid == null) return null;
        int v = Ver("SYNO.FileStation.Thumb", 2);
        // 新版要 JSON 引號包住路徑，舊版係純文字；兩樣都試吓
        foreach (var p in new[] { JsonSerializer.Serialize(path), path })
        {
            string url = Url("SYNO.FileStation.Thumb") + "?api=SYNO.FileStation.Thumb&version=" + v + "&method=get&size=" + size +
                         "&path=" + Uri.EscapeDataString(p) + "&_sid=" + Uri.EscapeDataString(_sid);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
                if (resp.IsSuccessStatusCode && (resp.Content.Headers.ContentType?.MediaType ?? "").StartsWith("image/"))
                    return await resp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return null;
    }

    public async Task UploadAsync(string file, string folder, bool overwrite, Action<long, long> progress, CancellationToken ct)
    {
        string url = Url("SYNO.FileStation.Upload") + "?api=SYNO.FileStation.Upload&version=" + Ver("SYNO.FileStation.Upload", 2) +
                     "&method=upload&_sid=" + Uri.EscapeDataString(_sid ?? "");
        using var form = new MultipartFormDataContent("----RabiToolkit" + Guid.NewGuid().ToString("N"));
        form.HeaderEncodingSelector = (_, _) => Encoding.UTF8; // 中文檔名用 UTF-8
        void Add(string k, string v)
        {
            var c = new StringContent(v);
            c.Headers.ContentType = null;
            c.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"" + k + "\"" };
            form.Add(c);
        }
        Add("path", folder);
        Add("create_parents", "true");
        Add("overwrite", overwrite ? "true" : "false");
        await using var fs = File.OpenRead(file);
        var fc = new ProgressContent(fs, progress);
        fc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // 檔名用 UTF-8 原文（DSM 識讀）
        fc.Headers.TryAddWithoutValidation("Content-Disposition",
            "form-data; name=\"file\"; filename=\"" + Path.GetFileName(file).Replace("\"", "'") + "\"");
        form.Add(fc);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        using var resp = await SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        JsonElement r;
        try { r = JsonDocument.Parse(body).RootElement; }
        catch { throw new NasException(-1, Lang.F("上載失敗（HTTP {0}）", (int)resp.StatusCode)); }
        if (r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True) return;
        int code = r.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c2) && c2.TryGetInt32(out var n2) ? n2 : -1;
        throw new NasException(code, Error("SYNO.FileStation.Upload", code));
    }

    /// <summary>上載時報告進度。</summary>
    private sealed class ProgressContent : HttpContent
    {
        private readonly Stream _s;
        private readonly Action<long, long> _p;
        public ProgressContent(Stream s, Action<long, long> p) { _s = s; _p = p; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buf = new byte[1 << 16];
            long done = 0, total = _s.Length;
            int n;
            while ((n = await _s.ReadAsync(buf)) > 0)
            {
                await stream.WriteAsync(buf.AsMemory(0, n));
                done += n;
                _p(done, total);
            }
        }
        protected override bool TryComputeLength(out long length) { length = _s.Length; return true; }
    }

    // ================= 系統狀態 =================

    public async Task<NasStatus> StatusAsync(CancellationToken ct = default)
    {
        var st = new NasStatus();
        try
        {
            var u = await CallAsync("SYNO.Core.System.Utilization", "get", 1, null, ct);
            if (u.TryGetProperty("cpu", out var cpu))
                st.Cpu = Int(cpu, "user_load") + Int(cpu, "system_load") + Int(cpu, "other_load");
            if (u.TryGetProperty("memory", out var mem))
            {
                st.Ram = Int(mem, "real_usage");
                if (mem.TryGetProperty("total_real", out var tr) && tr.TryGetInt64(out var kb)) st.RamTotal = kb * 1024;
            }
            foreach (var n in Arr(u, "network"))
                if (n.TryGetProperty("device", out var dv) && dv.GetString() == "total")
                {
                    if (n.TryGetProperty("rx", out var rx) && rx.TryGetInt64(out var r)) st.Rx = r;
                    if (n.TryGetProperty("tx", out var tx) && tx.TryGetInt64(out var t)) st.Tx = t;
                }
        }
        catch (NasException) { }
        try
        {
            var s = await CallAsync("SYNO.Core.System", "info", 1, null, ct);
            st.Model = Str(s, "model");
            st.Dsm = Str(s, "firmware_ver");
            st.Uptime = Str(s, "up_time");
            st.Temp = s.TryGetProperty("sys_temp", out var t1) && t1.TryGetInt32(out var tv) ? tv
                    : s.TryGetProperty("temperature", out var t2) && t2.TryGetInt32(out var tv2) ? tv2 : null;
        }
        catch (NasException) { }
        try
        {
            var g = await CallAsync("SYNO.Storage.CGI.Storage", "load_info", 1, null, ct);
            foreach (var v in Arr(g, "volumes"))
            {
                long used = 0, total = 0;
                if (v.TryGetProperty("size", out var sz))
                {
                    long.TryParse(Str(sz, "used"), out used);
                    long.TryParse(Str(sz, "total"), out total);
                }
                string name = Str(v, "display_name") ?? Str(v, "vol_path") ?? Str(v, "id") ?? "";
                st.Volumes.Add((name, used, total, Str(v, "status") ?? ""));
            }
            foreach (var d in Arr(g, "disks"))
            {
                int? temp = d.TryGetProperty("temp", out var tp) && tp.TryGetInt32(out var tpv) ? tpv : null;
                st.Disks.Add((Str(d, "longName") ?? Str(d, "name") ?? Str(d, "id") ?? "", DiskModel(Str(d, "vendor"), Str(d, "model")), temp,
                    Str(d, "overview_status") ?? Str(d, "status") ?? ""));
            }
        }
        catch (NasException) { }
        return st;
    }

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : int.TryParse(v.ToString(), out var m) ? m : 0 : 0;

    // 型號通常已經包咗牌子（WDC WUH72…），咁就唔好再加多次
    private static string DiskModel(string? vendor, string? model)
    {
        vendor = (vendor ?? "").Trim(); model = (model ?? "").Trim();
        if (vendor.Length == 0 || model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) return model;
        return (vendor + " " + model).Trim();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    public Task RebootAsync() => CallAsync("SYNO.Core.System", "reboot", 1);
    public Task ShutdownAsync() => CallAsync("SYNO.Core.System", "shutdown", 1);

    // ================= 錯誤訊息 =================

    private static string Error(string api, int code)
    {
        string? common = code switch
        {
            100 => Lang.T("NAS 出咗未知錯誤"),
            101 or 120 => Lang.T("參數唔啱"),
            102 => Lang.T("NAS 冇呢個功能（可能要裝對應嘅套件或者更新 DSM）"),
            103 => Lang.T("NAS 唔支援呢個操作"),
            104 => Lang.T("NAS 版本唔支援呢個操作"),
            105 => Lang.T("帳號冇權限做呢樣嘢"),
            106 or 107 or 119 => Lang.T("登入已過期，請重新連線"),
            _ => null
        };
        if (common != null) return common;
        if (api == "SYNO.API.Auth")
            return code switch
            {
                400 => Lang.T("帳號或者密碼唔啱"),
                401 => Lang.T("帳號已停用"),
                402 => Lang.T("帳號冇權限登入"),
                403 => Lang.T("要輸入兩步驗證碼"),
                404 => Lang.T("兩步驗證碼唔啱"),
                406 => Lang.T("NAS 要求開兩步驗證：請先喺 DSM 網頁設定好"),
                407 => Lang.T("呢個 IP 被 NAS 封鎖咗（登入失敗太多次），請喺 DSM 解除封鎖"),
                408 or 409 or 410 => Lang.T("密碼過期或者要改密碼：請先喺 DSM 網頁登入一次"),
                _ => Lang.F("登入失敗（錯誤碼 {0}）", code)
            };
        if (api.StartsWith("SYNO.FileStation"))
            return code switch
            {
                400 => Lang.T("檔案操作參數唔啱"),
                401 => Lang.T("檔案操作失敗（未知錯誤）"),
                402 => Lang.T("系統太忙，請遲啲再試"),
                403 => Lang.T("冇權限：呢個帳號唔可以操作呢個檔案"),
                404 => Lang.T("冇權限：呢個帳號唔可以用呢個共用資料夾"),
                405 => Lang.T("唔可以用呢個操作"),
                406 => Lang.T("冇呢部 NAS 嘅寫入權限"),
                407 => Lang.T("冇權限"),
                408 => Lang.T("搵唔到檔案或者資料夾"),
                409 => Lang.T("唔支援呢種檔案系統"),
                410 => Lang.T("連線失敗"),
                411 => Lang.T("唯讀檔案系統"),
                412 or 413 => Lang.T("檔名太長"),
                414 => Lang.T("已經有同名嘅檔案"),
                415 => Lang.T("NAS 容量唔夠"),
                416 => Lang.T("用戶配額唔夠"),
                417 => Lang.T("檔名有唔准用嘅字元"),
                418 => Lang.T("路徑唔啱"),
                419 => Lang.T("名稱唔啱"),
                1800 or 1801 or 1802 or 1803 or 1804 or 1805 => Lang.T("上載失敗：檔案唔完整或者太大"),
                _ => Lang.F("檔案操作失敗（錯誤碼 {0}）", code)
            };
        return Lang.F("NAS 回覆錯誤（錯誤碼 {0}）", code);
    }

    // ================= Wake-on-LAN =================

    [DllImport("iphlpapi.dll")] private static extern int SendARP(int dest, int src, byte[] mac, ref int len);

    /// <summary>同一個網絡入面：由 IP 攞 MAC（記低俾 Wake-on-LAN 用）。</summary>
    public static string? MacOf(string host)
    {
        try
        {
            var ip = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip == null) return null;
            var mac = new byte[6];
            int len = 6;
            if (SendARP(BitConverter.ToInt32(ip.GetAddressBytes(), 0), 0, mac, ref len) != 0) return null;
            return string.Join(":", mac.Take(len).Select(b => b.ToString("X2")));
        }
        catch { return null; }
    }

    /// <summary>發 Wake-on-LAN magic packet（廣播 UDP 9）。</summary>
    public static void Wake(string mac)
    {
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) throw new ArgumentException(Lang.T("MAC 地址格式唔啱（例如 00:11:32:AA:BB:CC）"));
        var m = Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
        var pkt = new byte[102];
        for (int i = 0; i < 6; i++) pkt[i] = 0xFF;
        for (int i = 1; i <= 16; i++) Buffer.BlockCopy(m, 0, pkt, i * 6, 6);
        using var u = new UdpClient { EnableBroadcast = true };
        u.Send(pkt, pkt.Length, new IPEndPoint(IPAddress.Broadcast, 9));
        u.Send(pkt, pkt.Length, new IPEndPoint(IPAddress.Broadcast, 7));
    }
}
