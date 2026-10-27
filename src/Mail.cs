using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTool.Mail;

// =====================================================================
//  電子郵件：帳戶、IMAP（收信）、SMTP（寄信）、MIME（拆 / 砌信件）、微軟 OAuth
//  全部自己寫，唔使第三方套件。密碼 / token 用 Windows DPAPI 加密（淨係呢部電腦呢個用戶解得返）。
// =====================================================================

/// <summary>一個郵件帳戶。</summary>
internal sealed class MailAccount
{
    public string Id = Guid.NewGuid().ToString("N")[..8];
    public string Kind = "gmail";          // gmail / outlook / imap
    public string Email = "";
    public string Name = "";               // 寄信時顯示嘅名
    public string ImapHost = "", SmtpHost = "";
    public int ImapPort = 993, SmtpPort = 465;
    public string Secret = "";             // 加密咗嘅密碼（gmail / imap）或者 refresh token（outlook）
    public bool Notify = true;             // 新郵件通知
    public string Tenant = "";             // Outlook 用邊個微軟登入點：consumers（個人帳戶）/ organizations（公司 / 學校）；空 = 睇電郵地址估

    public bool IsOAuth => Kind == "outlook";
    public string Display => Name.Length > 0 ? $"{Name} <{Email}>" : Email;

    public static MailAccount Gmail(string email, string appPassword) => new()
    {
        Kind = "gmail", Email = email.Trim(), ImapHost = "imap.gmail.com", ImapPort = 993, SmtpHost = "smtp.gmail.com", SmtpPort = 465,
        Secret = MailStore.Protect(appPassword.Replace(" ", ""))
    };

    public static MailAccount Outlook(string email, string refreshToken, string tenant = "") => new()
    {
        Tenant = tenant,
        Kind = "outlook", Email = email.Trim(), ImapHost = "outlook.office365.com", ImapPort = 993, SmtpHost = "smtp-mail.outlook.com", SmtpPort = 587,
        Secret = MailStore.Protect(refreshToken)
    };
}

/// <summary>帳戶設定存喺 data\mail\accounts.json；密碼用 DPAPI 加密。</summary>
internal static class MailStore
{
    public static string Dir => Path.Combine(AppPaths.Data, "mail");
    private static string File_ => Path.Combine(Dir, "accounts.json");
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };
    private static List<MailAccount>? _all;
    public static event Action? Changed;

    public static List<MailAccount> All
    {
        get
        {
            if (_all != null) return _all;
            try { _all = File.Exists(File_) ? JsonSerializer.Deserialize<List<MailAccount>>(File.ReadAllText(File_), Json) ?? new() : new(); }
            catch { _all = new(); }
            return _all;
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(File_, JsonSerializer.Serialize(All, Json));
        }
        catch (Exception ex) { AppPaths.LogError("儲存郵件帳戶", ex); }
        Changed?.Invoke();
    }

    public static void Add(MailAccount a)
    {
        All.RemoveAll(x => string.Equals(x.Email, a.Email, StringComparison.OrdinalIgnoreCase) && x.Kind == a.Kind);
        All.Add(a);
        Save();
    }

    public static void Remove(MailAccount a) { All.Remove(a); Save(); }

    public static string Protect(string s)
    {
        if (s.Length == 0) return "";
        try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    public static string Unprotect(string s)
    {
        if (s.Length == 0) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    // ---- 准顯示遠端圖片嘅寄件人（data\mail\trusted_images.txt）----
    private static HashSet<string>? _trusted;
    private static string TrustFile => Path.Combine(Dir, "trusted_images.txt");

    public static bool ImagesTrusted(string addr)
    {
        if (_trusted == null)
        {
            _trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(TrustFile)) foreach (var l in File.ReadAllLines(TrustFile)) if (l.Trim().Length > 0) _trusted.Add(l.Trim()); } catch { }
        }
        return addr.Length > 0 && _trusted.Contains(addr.Trim());
    }

    public static void TrustImages(string addr)
    {
        if (addr.Trim().Length == 0) return;
        ImagesTrusted(addr);
        if (!_trusted!.Add(addr.Trim())) return;
        try { Directory.CreateDirectory(Dir); File.WriteAllLines(TrustFile, _trusted); } catch { }
    }

    /// <summary>Outlook OAuth 用嘅 Azure App Client ID（data\mail\outlook_client.txt）。</summary>
    public static string OutlookClientId
    {
        get { try { var f = Path.Combine(Dir, "outlook_client.txt"); return File.Exists(f) ? File.ReadAllText(f).Trim() : ""; } catch { return ""; } }
        set { try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, "outlook_client.txt"), value.Trim()); } catch { } }
    }
}

// ---------------------------------------------------------------------
//  微軟 OAuth（裝置代碼登入）：顯示一個代碼，用戶去 microsoft.com/devicelogin 輸入
// ---------------------------------------------------------------------
internal static class MsOAuth
{
    private const string Scope = "openid email offline_access https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static string Base(string tenant) => $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/";

    /// <summary>
    /// 個人帳戶（hotmail / outlook / live）一定要用 consumers 登入點：用 common 嘅話，如果呢個帳戶喺 Azure 有自己嘅目錄，
    /// 微軟可能會俾一個「公司帳戶」token，IMAP 就會答 "User is authenticated but not connected"。
    /// </summary>
    public static string TenantFor(MailAccount a) =>
        a.Tenant.Length > 0 ? a.Tenant : IsPersonal(a.Email) ? "consumers" : "common";

    public static bool IsPersonal(string email)
    {
        string d = email.Contains('@') ? email[(email.LastIndexOf('@') + 1)..].ToLowerInvariant() : "";
        return d.StartsWith("hotmail.") || d.StartsWith("outlook.") || d.StartsWith("live.") || d.StartsWith("msn.") || d == "passport.com";
    }

    public sealed record DeviceCode(string UserCode, string DeviceCodeValue, string Url, int Interval, int ExpiresIn, string Message);

    public static async Task<DeviceCode> StartAsync(string clientId, string tenant, CancellationToken ct)
    {
        var res = await Http.PostAsync(Base(tenant) + "devicecode", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["scope"] = Scope
        }), ct);
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(Err(doc));
        return new DeviceCode(doc.GetProperty("user_code").GetString()!, doc.GetProperty("device_code").GetString()!,
            doc.GetProperty("verification_uri").GetString()!, doc.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5,
            doc.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 900, doc.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
    }

    /// <summary>等用戶登入完：回傳 (access token, refresh token, email)。</summary>
    public static async Task<(string Access, string Refresh, string Email)> PollAsync(string clientId, string tenant, DeviceCode dc, CancellationToken ct)
    {
        var until = DateTime.Now.AddSeconds(dc.ExpiresIn);
        int interval = Math.Max(2, dc.Interval);
        while (DateTime.Now < until)
        {
            await Task.Delay(interval * 1000, ct);
            var res = await Http.PostAsync(Base(tenant) + "token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["client_id"] = clientId, ["device_code"] = dc.DeviceCodeValue
            }), ct);
            var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
            if (res.IsSuccessStatusCode)
            {
                string access = doc.GetProperty("access_token").GetString()!;
                string refresh = doc.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? "" : "";
                string email = doc.TryGetProperty("id_token", out var idt) ? EmailFromIdToken(idt.GetString() ?? "") : "";
                return (access, refresh, email);
            }
            string err = doc.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            if (err == "authorization_pending") continue;
            if (err == "slow_down") { interval += 5; continue; }
            throw new InvalidOperationException(Err(doc));
        }
        throw new TimeoutException(Lang.T("登入逾時，請再試一次。"));
    }

    // access token 暫存（大約一個鐘有效）
    private static readonly Dictionary<string, (string Token, DateTime Until)> Cache = new();

    /// <summary>啱啱登入攞到嘅 access token 直接放入暫存（唔使即刻再 refresh，避免微軟當係「請求迴圈」AADSTS50196）。</summary>
    public static void Seed(MailAccount a, string access, int seconds = 3000)
    {
        lock (Cache) Cache[a.Id] = (access, DateTime.Now.AddSeconds(seconds));
    }

    public static async Task<string> AccessTokenAsync(MailAccount a, CancellationToken ct)
    {
        lock (Cache)
            if (Cache.TryGetValue(a.Id, out var c) && c.Until > DateTime.Now.AddMinutes(2)) return c.Token;
        string clientId = MailStore.OutlookClientId;
        if (clientId.Length == 0) throw new InvalidOperationException(Lang.T("未設定 Outlook 嘅 Client ID（電子郵件 → 帳戶設定）"));
        string tenant = TenantFor(a);
        async Task<(HttpResponseMessage, JsonElement)> Refresh(string t)
        {
            var r = await Http.PostAsync(Base(t) + "token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = MailStore.Unprotect(a.Secret), ["scope"] = Scope
            }), ct);
            return (r, JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement);
        }
        var (res, doc) = await Refresh(tenant);
        // 舊版用 common 登入嘅個人帳戶：consumers 唔認得嗰個 refresh token 就用返 common
        if (!res.IsSuccessStatusCode && a.Tenant.Length == 0 && tenant != "common")
        {
            var (res2, doc2) = await Refresh("common");
            if (res2.IsSuccessStatusCode) { res = res2; doc = doc2; }
        }
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(Lang.T("Outlook 登入失敗：") + Err(doc));
        string access = doc.GetProperty("access_token").GetString()!;
        if (doc.TryGetProperty("refresh_token", out var nr) && nr.GetString() is { Length: > 0 } newRefresh)
        {
            a.Secret = MailStore.Protect(newRefresh); // 微軟會換新 refresh token
            MailStore.Save();
        }
        int sec = doc.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 3600;
        lock (Cache) Cache[a.Id] = (access, DateTime.Now.AddSeconds(sec));
        return access;
    }

    private static string Err(JsonElement doc) =>
        doc.TryGetProperty("error_description", out var d) ? (d.GetString() ?? "").Split('\n')[0].Trim() :
        doc.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "error";

    private static string EmailFromIdToken(string jwt)
    {
        try
        {
            var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
            var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(part))).RootElement;
            foreach (var k in new[] { "email", "preferred_username", "upn" })
                if (doc.TryGetProperty(k, out var v) && v.GetString() is { Length: > 0 } s) return s;
        }
        catch { }
        return "";
    }
}

// ---------------------------------------------------------------------
//  IMAP
// ---------------------------------------------------------------------

/// <summary>信件清單一行（淨係標頭）。</summary>
internal sealed class MailHeader
{
    public uint Uid;
    public string From = "", FromAddr = "", To = "", Cc = "", Subject = "", MessageId = "", References = "";
    public DateTime Date;
    public bool Seen, Flagged, Answered;
    public bool HasAttachment;
    public long Size;
    public string Folder = "";
    public MailAccount? Acct;              // 屬於邊個帳戶（「全部帳戶」合併清單用）
}

internal sealed class MailFolder
{
    public string Name = "";               // 伺服器上面嘅名（modified UTF-7）
    public string Display = "";            // 顯示名
    public string Special = "";            // inbox / sent / drafts / trash / junk / all / flagged / archive
    public bool NoSelect;
    public char Delim = '/';
}

/// <summary>
/// 簡單 IMAP 客戶端（IMAP4rev1 over SSL）：登入、資料夾、搜尋、攞標頭 / 全文、旗標、搬信。
/// 一個連線一次只做一件事（用 lock）。
/// </summary>
internal sealed class ImapClient : IDisposable
{
    private TcpClient? _tcp;
    private Stream? _s;
    private int _tag;
    private readonly SemaphoreSlim _lock = new(1, 1);
    public HashSet<string> Caps = new(StringComparer.OrdinalIgnoreCase);
    public string? Selected;
    public bool IsGmail;

    public bool Connected => _tcp?.Connected == true;

    // Outlook 個人帳戶有時喺 outlook.office365.com 會答「User is authenticated but not connected」，
    // 轉用舊嘅個人郵箱伺服器 imap-mail.outlook.com 就得（或者相反）：自動試另一個，得咗就記住。
    private static readonly string[] OutlookHosts = { "outlook.office365.com", "imap-mail.outlook.com" };

    public async Task ConnectAsync(MailAccount a, CancellationToken ct)
    {
        try { await ConnectHostAsync(a, a.ImapHost, ct); }
        catch (InvalidOperationException ex) when (a.IsOAuth && ex.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase) &&
                                                   OutlookHosts.Contains(a.ImapHost, StringComparer.OrdinalIgnoreCase))
        {
            string other = OutlookHosts.First(h => !h.Equals(a.ImapHost, StringComparison.OrdinalIgnoreCase));
            Dispose();
            _bufPos = _bufLen = 0;
            Caps.Clear();
            await ConnectHostAsync(a, other, ct); // 再失敗就由佢 throw
            a.ImapHost = other;
            MailStore.Save();
        }
    }

    private async Task ConnectHostAsync(MailAccount a, string host, CancellationToken ct)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, a.ImapPort, ct);
        var ssl = new SslStream(_tcp.GetStream());
        await ssl.AuthenticateAsClientAsync(host);
        _s = ssl;
        await ReadLineAsync(ct); // * OK greeting
        IsGmail = host.Contains("gmail", StringComparison.OrdinalIgnoreCase);
        if (a.IsOAuth)
        {
            string token = await MsOAuth.AccessTokenAsync(a, ct);
            string xo = Convert.ToBase64String(Encoding.UTF8.GetBytes($"user={a.Email}\u0001auth=Bearer {token}\u0001\u0001"));
            await CmdAsync("AUTHENTICATE XOAUTH2 " + xo, ct);
        }
        else
        {
            string pass = MailStore.Unprotect(a.Secret);
            await CmdAsync($"LOGIN {Q(a.Email)} {Q(pass)}", ct);
        }
        var cap = await CmdAsync("CAPABILITY", ct);
        foreach (var l in cap.Untagged)
            if (l.StartsWith("* CAPABILITY", StringComparison.OrdinalIgnoreCase))
                foreach (var c in l[12..].Split(' ', StringSplitOptions.RemoveEmptyEntries)) Caps.Add(c);
    }

    public void Dispose()
    {
        try { _s?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
    }

    // ---- 低層：讀行 / 讀 literal ----
    private readonly byte[] _buf = new byte[65536];
    private int _bufPos, _bufLen;

    private async Task<int> ReadByteAsync(CancellationToken ct)
    {
        if (_bufPos >= _bufLen)
        {
            _bufLen = await _s!.ReadAsync(_buf.AsMemory(0, _buf.Length), ct);
            _bufPos = 0;
            if (_bufLen <= 0) throw new IOException(Lang.T("郵件伺服器斷咗線"));
        }
        return _buf[_bufPos++];
    }

    private async Task<byte[]> ReadBytesAsync(int n, CancellationToken ct)
    {
        var res = new byte[n];
        int got = 0;
        while (got < n)
        {
            if (_bufPos < _bufLen)
            {
                int take = Math.Min(n - got, _bufLen - _bufPos);
                Buffer.BlockCopy(_buf, _bufPos, res, got, take);
                _bufPos += take;
                got += take;
            }
            else
            {
                _bufLen = await _s!.ReadAsync(_buf.AsMemory(0, _buf.Length), ct);
                _bufPos = 0;
                if (_bufLen <= 0) throw new IOException(Lang.T("郵件伺服器斷咗線"));
            }
        }
        return res;
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var ms = new MemoryStream();
        while (true)
        {
            int b = await ReadByteAsync(ct);
            if (b == '\n') break;
            if (b != '\r') ms.WriteByte((byte)b);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>一個回應：行 + 佢帶住嘅 literal（{n}）。Literal 用 \0n\0 標記喺行入面。</summary>
    internal sealed class Resp
    {
        public List<string> Untagged = new();
        public List<byte[]> Literals = new();
        public string Status = "";
    }

    private async Task WriteAsync(string s, CancellationToken ct)
    {
        var b = Encoding.UTF8.GetBytes(s);
        await _s!.WriteAsync(b, ct);
        await _s.FlushAsync(ct);
    }

    /// <summary>送一個指令，讀晒回應。literals：指令入面要用 literal 送嘅字串（{n} + 等 "+" 再送）。</summary>
    public async Task<Resp> CmdAsync(string cmd, CancellationToken ct, params string[] literals)
    {
        await _lock.WaitAsync(ct);
        try
        {
            string tag = "A" + (++_tag).ToString("0000");
            var resp = new Resp();
            if (literals.Length == 0) await WriteAsync(tag + " " + cmd + "\r\n", ct);
            else
            {
                // cmd 入面用 {0} {1}… 標 literal 位置
                var parts = Regex.Split(cmd, @"\{L\}");
                await WriteAsync(tag + " " + parts[0], ct);
                for (int i = 0; i < literals.Length; i++)
                {
                    var lb = Encoding.UTF8.GetBytes(literals[i]);
                    await WriteAsync("{" + lb.Length + "}\r\n", ct);
                    while (true)
                    {
                        var l = await ReadLineAsync(ct);
                        if (l.StartsWith("+")) break;
                        if (l.StartsWith(tag + " ")) throw new InvalidOperationException(l[(tag.Length + 1)..]);
                        resp.Untagged.Add(l);
                    }
                    await _s!.WriteAsync(lb, ct);
                    await WriteAsync(i + 1 < parts.Length ? parts[i + 1] : "", ct);
                }
                await WriteAsync("\r\n", ct);
            }
            var sb = new StringBuilder();
            while (true)
            {
                var line = await ReadLineAsync(ct);
                // literal：行尾 {n}
                var m = Regex.Match(line, @"\{(\d+)\}$");
                if (m.Success)
                {
                    int n = int.Parse(m.Groups[1].Value);
                    var data = await ReadBytesAsync(n, ct);
                    sb.Append(line[..m.Index]).Append('\0').Append(resp.Literals.Count).Append('\0');
                    resp.Literals.Add(data);
                    continue; // 同一個回應繼續讀落去
                }
                sb.Append(line);
                string full = sb.ToString();
                sb.Clear();
                if (full.StartsWith(tag + " "))
                {
                    resp.Status = full[(tag.Length + 1)..];
                    if (!resp.Status.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(resp.Status);
                    return resp;
                }
                if (full.StartsWith("+") && cmd.StartsWith("AUTHENTICATE", StringComparison.OrdinalIgnoreCase))
                {
                    // XOAUTH2 失敗會先俾一行錯誤（base64），要送空行先收到 NO
                    await WriteAsync("\r\n", ct);
                    continue;
                }
                resp.Untagged.Add(full);
            }
        }
        finally { _lock.Release(); }
    }

    public static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ---- 資料夾 ----
    public async Task<List<MailFolder>> ListAsync(CancellationToken ct)
    {
        var r = await CmdAsync("LIST \"\" \"*\"", ct);
        var list = new List<MailFolder>();
        foreach (var l in r.Untagged)
        {
            if (!l.StartsWith("* LIST", StringComparison.OrdinalIgnoreCase)) continue;
            var toks = Tokenize(l[6..], r.Literals);
            if (toks.Count < 3 || toks[0] is not List<object> attrs) continue;
            string name = toks[2] is byte[] nb ? Encoding.UTF8.GetString(nb) : toks[2] as string ?? "";
            string delim = toks[1] as string ?? "/";
            var f = new MailFolder { Name = name, Delim = delim.Length > 0 ? delim[0] : '/' };
            var a = attrs.Select(x => x?.ToString() ?? "").ToList();
            f.NoSelect = a.Any(x => x.Equals("\\Noselect", StringComparison.OrdinalIgnoreCase) || x.Equals("\\NonExistent", StringComparison.OrdinalIgnoreCase));
            f.Special = a.Select(x => x.ToLowerInvariant()).FirstOrDefault(x => x is "\\sent" or "\\drafts" or "\\trash" or "\\junk" or "\\all" or "\\flagged" or "\\archive")?.TrimStart('\\') ?? "";
            if (name.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) f.Special = "inbox";
            f.Display = DecodeUtf7(name);
            list.Add(f);
        }
        return list;
    }

    public async Task<(int Exists, uint UidNext)> SelectAsync(string folder, CancellationToken ct)
    {
        var r = await CmdAsync("SELECT " + Q(folder), ct);
        Selected = folder;
        int exists = 0; uint next = 0;
        foreach (var l in r.Untagged)
        {
            var m = Regex.Match(l, @"^\* (\d+) EXISTS", RegexOptions.IgnoreCase);
            if (m.Success) exists = int.Parse(m.Groups[1].Value);
            var n = Regex.Match(l, @"UIDNEXT (\d+)", RegexOptions.IgnoreCase);
            if (n.Success) next = uint.Parse(n.Groups[1].Value);
        }
        return (exists, next);
    }

    /// <summary>UID SEARCH；query 用 IMAP 搜尋條件（可以有 {L} literal）。</summary>
    public async Task<List<uint>> SearchAsync(string criteria, CancellationToken ct, params string[] literals)
    {
        var r = await CmdAsync("UID SEARCH " + criteria, ct, literals);
        var uids = new List<uint>();
        foreach (var l in r.Untagged)
            if (l.StartsWith("* SEARCH", StringComparison.OrdinalIgnoreCase))
                foreach (var p in l[8..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (uint.TryParse(p, out var u)) uids.Add(u);
        uids.Sort();
        return uids;
    }

    /// <summary>搜尋文字：Gmail 用佢自己嘅搜尋語法（X-GM-RAW），其他用標題 / 寄件人 / 內文。</summary>
    public Task<List<uint>> SearchTextAsync(string text, CancellationToken ct)
    {
        if (IsGmail && Caps.Contains("X-GM-EXT-1")) return SearchAsync("CHARSET UTF-8 X-GM-RAW {L}", ct, text);
        return SearchAsync("CHARSET UTF-8 OR OR SUBJECT {L} FROM {L} TEXT {L}", ct, text, text, text);
    }

    public async Task<List<MailHeader>> FetchHeadersAsync(IEnumerable<uint> uids, CancellationToken ct)
    {
        var set = string.Join(",", uids);
        var res = new List<MailHeader>();
        if (set.Length == 0) return res;
        var r = await CmdAsync($"UID FETCH {set} (UID FLAGS RFC822.SIZE BODYSTRUCTURE BODY.PEEK[HEADER.FIELDS (FROM TO CC SUBJECT DATE MESSAGE-ID REFERENCES CONTENT-TYPE)])", ct);
        foreach (var l in r.Untagged)
        {
            var m = Regex.Match(l, @"^\* \d+ FETCH \(", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var toks = Tokenize(l[(m.Length - 1)..], r.Literals);
            if (toks.Count == 0 || toks[0] is not List<object> kv) continue;
            var h = new MailHeader { Folder = Selected ?? "" };
            for (int i = 0; i + 1 < kv.Count; i += 2)
            {
                string key = (kv[i] as string ?? "").ToUpperInvariant();
                var val = kv[i + 1];
                if (key == "UID") uint.TryParse(val as string, out h.Uid);
                else if (key == "FLAGS" && val is List<object> fl)
                {
                    var f = fl.Select(x => (x as string ?? "").ToLowerInvariant()).ToList();
                    h.Seen = f.Contains("\\seen"); h.Flagged = f.Contains("\\flagged"); h.Answered = f.Contains("\\answered");
                }
                else if (key == "RFC822.SIZE") long.TryParse(val as string, out h.Size);
                else if (key == "BODYSTRUCTURE") h.HasAttachment = HasAttach(val);
                else if (key.StartsWith("BODY[") && val is byte[] hb)
                {
                    var hd = Mime.ParseHeaders(hb, out _);
                    h.Subject = Mime.DecodeWords(Mime.Get(hd, "subject"));
                    (h.From, h.FromAddr) = Mime.ParseAddress(Mime.DecodeWords(Mime.Get(hd, "from")));
                    h.To = Mime.DecodeWords(Mime.Get(hd, "to"));
                    h.Cc = Mime.DecodeWords(Mime.Get(hd, "cc"));
                    h.MessageId = Mime.Get(hd, "message-id").Trim();
                    h.References = Mime.Get(hd, "references").Trim();
                    h.Date = Mime.ParseDate(Mime.Get(hd, "date"));
                }
                else if (key.StartsWith("BODY[") && val is string hs)
                {
                    var hd = Mime.ParseHeaders(Encoding.UTF8.GetBytes(hs), out _);
                    h.Subject = Mime.DecodeWords(Mime.Get(hd, "subject"));
                    (h.From, h.FromAddr) = Mime.ParseAddress(Mime.DecodeWords(Mime.Get(hd, "from")));
                    h.Date = Mime.ParseDate(Mime.Get(hd, "date"));
                }
            }
            if (h.Uid != 0) res.Add(h);
        }
        return res;
    }

    private static bool HasAttach(object? bs)
    {
        // BODYSTRUCTURE 入面有 "attachment" disposition 或者 application/* 就當有附件
        if (bs is List<object> l)
        {
            if (l.Count > 0 && l[0] is string t && l.Count > 1 && l[1] is string sub)
            {
                if (t.Equals("application", StringComparison.OrdinalIgnoreCase) && !sub.Equals("pgp-signature", StringComparison.OrdinalIgnoreCase)) return true;
            }
            foreach (var x in l)
            {
                if (x is string s && s.Equals("attachment", StringComparison.OrdinalIgnoreCase)) return true;
                if (x is List<object> && HasAttach(x)) return true;
            }
        }
        return false;
    }

    public async Task<byte[]?> FetchRawAsync(uint uid, CancellationToken ct)
    {
        var r = await CmdAsync($"UID FETCH {uid} (BODY.PEEK[])", ct);
        return r.Literals.OrderByDescending(x => x.Length).FirstOrDefault();
    }

    public Task StoreAsync(uint uid, string flag, bool add, CancellationToken ct) =>
        CmdAsync($"UID STORE {uid} {(add ? "+" : "-")}FLAGS.SILENT ({flag})", ct);

    public async Task MoveAsync(uint uid, string target, CancellationToken ct)
    {
        if (Caps.Contains("MOVE")) { await CmdAsync($"UID MOVE {uid} {Q(target)}", ct); return; }
        await CmdAsync($"UID COPY {uid} {Q(target)}", ct);
        await StoreAsync(uid, "\\Deleted", true, ct);
        await CmdAsync(Caps.Contains("UIDPLUS") ? $"UID EXPUNGE {uid}" : "EXPUNGE", ct);
    }

    public async Task DeleteForeverAsync(uint uid, CancellationToken ct)
    {
        await StoreAsync(uid, "\\Deleted", true, ct);
        await CmdAsync(Caps.Contains("UIDPLUS") ? $"UID EXPUNGE {uid}" : "EXPUNGE", ct);
    }

    public Task NoopAsync(CancellationToken ct) => CmdAsync("NOOP", ct);

    /// <summary>某個資料夾有幾多封未讀（STATUS，唔使 SELECT）。</summary>
    public async Task<int> UnseenAsync(string folder, CancellationToken ct)
    {
        var r = await CmdAsync($"STATUS {Q(folder)} (UNSEEN)", ct);
        foreach (var l in r.Untagged)
        {
            var m = Regex.Match(l, @"UNSEEN (\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return 0;
    }

    /// <summary>將一封信放入資料夾（一般 IMAP 寄完要自己放入「寄件備份」）。</summary>
    public Task AppendAsync(string folder, byte[] message, CancellationToken ct) =>
        CmdAsync($"APPEND {Q(folder)} (\\Seen) {{L}}", ct, Encoding.ASCII.GetString(message));

    // ---- 拆 IMAP 回應：( ) 列表、"字串"、literal、NIL、原子 ----
    internal static List<object> Tokenize(string s, List<byte[]> lits)
    {
        int i = 0;
        var root = new List<object>();
        var stack = new Stack<List<object>>();
        var cur = root;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == ' ') { i++; continue; }
            if (c == '(') { var n = new List<object>(); cur.Add(n); stack.Push(cur); cur = n; i++; continue; }
            if (c == ')') { if (stack.Count > 0) cur = stack.Pop(); i++; continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) i++;
                    sb.Append(s[i]);
                    i++;
                }
                i++;
                cur.Add(sb.ToString());
                continue;
            }
            if (c == '\0')
            {
                int j = s.IndexOf('\0', i + 1);
                int idx = int.Parse(s[(i + 1)..j]);
                cur.Add(idx < lits.Count ? lits[idx] : Array.Empty<byte>());
                i = j + 1;
                continue;
            }
            // 原子（BODY[HEADER.FIELDS (...)] 要成段當一個）
            int st = i;
            int depth = 0;
            while (i < s.Length)
            {
                char d = s[i];
                if (d == '[') depth++;
                else if (d == ']') depth--;
                else if (depth == 0 && (d == ' ' || d == '(' || d == ')')) break;
                i++;
            }
            string atom = s[st..i];
            cur.Add(atom.Equals("NIL", StringComparison.OrdinalIgnoreCase) ? null! : atom);
        }
        return root;
    }

    /// <summary>IMAP 資料夾名用 modified UTF-7（例如 &amp;U,BTFw-）。</summary>
    public static string DecodeUtf7(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '&') { sb.Append(s[i]); continue; }
            int j = s.IndexOf('-', i);
            if (j < 0) { sb.Append(s[i..]); break; }
            if (j == i + 1) { sb.Append('&'); i = j; continue; }
            string b64 = s[(i + 1)..j].Replace(',', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            try { sb.Append(Encoding.BigEndianUnicode.GetString(Convert.FromBase64String(b64))); }
            catch { sb.Append(s[i..(j + 1)]); }
            i = j;
        }
        return sb.ToString();
    }
}

// ---------------------------------------------------------------------
//  MIME：拆信
// ---------------------------------------------------------------------
internal sealed class MailPart
{
    public List<(string Key, string Value)> Headers = new();
    public string ContentType = "text/plain";
    public Dictionary<string, string> Params = new(StringComparer.OrdinalIgnoreCase);
    public string Disposition = "";
    public string FileName = "";
    public string ContentId = "";
    public byte[] Body = Array.Empty<byte>();   // 已經解碼（base64 / QP）
    public List<MailPart> Children = new();
}

internal sealed class MailMessage
{
    public MailPart Root = new();
    public string Subject = "", From = "", FromAddr = "", To = "", Cc = "", ReplyTo = "", MessageId = "", References = "";
    public DateTime Date;
    public string Text = "", Html = "";
    public List<MailPart> Attachments = new();
}

internal static class Mime
{
    static Mime()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    public static Encoding Enc(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Encoding.UTF8;
        name = name.Trim().Trim('"');
        if (name.Equals("gb2312", StringComparison.OrdinalIgnoreCase) || name.Equals("gbk", StringComparison.OrdinalIgnoreCase)) name = "gb18030";
        if (name.Equals("big5-hkscs", StringComparison.OrdinalIgnoreCase)) name = "big5";
        try { return Encoding.GetEncoding(name); } catch { return Encoding.UTF8; }
    }

    public static string Get(List<(string Key, string Value)> h, string key) =>
        h.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value ?? "";

    /// <summary>拆標頭（處理摺行），回傳標頭同 body 開始位置。</summary>
    public static List<(string Key, string Value)> ParseHeaders(byte[] data, out int bodyStart)
    {
        var list = new List<(string, string)>();
        int i = 0;
        string? key = null;
        var val = new StringBuilder();
        bodyStart = data.Length;
        while (i < data.Length)
        {
            int e = Array.IndexOf(data, (byte)'\n', i);
            if (e < 0) e = data.Length;
            int len = e - i;
            if (len > 0 && data[e - 1 == i - 1 ? i : e - 1] == '\r') len--;
            // 用 latin1 讀，之後再按需要解碼（標頭應該係 ASCII；有啲 8bit 標頭當 UTF-8）
            string line = Latin1OrUtf8(data, i, len);
            i = e + 1;
            if (line.Length == 0) { bodyStart = i; break; }
            if ((line[0] == ' ' || line[0] == '\t') && key != null) { val.Append(' ').Append(line.Trim()); continue; }
            if (key != null) list.Add((key, val.ToString()));
            int c = line.IndexOf(':');
            if (c <= 0) { key = null; continue; }
            key = line[..c].Trim();
            val.Clear().Append(line[(c + 1)..].Trim());
        }
        if (key != null) list.Add((key, val.ToString()));
        return list;
    }

    private static string Latin1OrUtf8(byte[] d, int start, int len)
    {
        bool high = false;
        for (int k = start; k < start + len; k++) if (d[k] >= 0x80) { high = true; break; }
        return high ? Encoding.UTF8.GetString(d, start, len) : Encoding.ASCII.GetString(d, start, len);
    }

    /// <summary>RFC 2047：=?utf-8?B?...?= / =?big5?Q?...?=</summary>
    public static string DecodeWords(string s)
    {
        if (!s.Contains("=?")) return s;
        // 相連嘅 encoded-word 中間嘅空白要拎走
        s = Regex.Replace(s, @"(\?=)\s+(=\?)", "$1$2");
        return Regex.Replace(s, @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", m =>
        {
            try
            {
                var enc = Enc(m.Groups[1].Value.Split('*')[0]);
                var data = m.Groups[2].Value.ToUpperInvariant() == "B"
                    ? Convert.FromBase64String(PadB64(m.Groups[3].Value))
                    : QpDecode(m.Groups[3].Value.Replace('_', ' '), header: true);
                return enc.GetString(data);
            }
            catch { return m.Value; }
        });
    }

    private static string PadB64(string s) { s = s.Trim(); return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='); }

    public static byte[] QpDecode(string s, bool header = false)
    {
        var ms = new MemoryStream();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '=' && i + 2 < s.Length + (header ? 0 : 0))
            {
                if (i + 1 < s.Length && (s[i + 1] == '\r' || s[i + 1] == '\n'))
                {
                    // 軟換行
                    i++;
                    if (s[i] == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                    continue;
                }
                if (i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
                {
                    ms.WriteByte(byte.Parse(s.Substring(i + 1, 2), NumberStyles.HexNumber));
                    i += 2;
                    continue;
                }
            }
            else if (c == '=' && i == s.Length - 1) continue;
            if (c < 256) ms.WriteByte((byte)c);
            else { var b = Encoding.UTF8.GetBytes(c.ToString()); ms.Write(b); }
        }
        return ms.ToArray();
    }

    /// <summary>Content-Type: text/plain; charset="utf-8"; name*=UTF-8''%E4... </summary>
    public static (string Value, Dictionary<string, string> Params) ParseParams(string header)
    {
        var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = SplitSemi(header);
        string value = parts.Count > 0 ? parts[0].Trim().ToLowerInvariant() : "";
        var star = new SortedDictionary<string, SortedDictionary<int, (string V, bool Enc)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in parts.Skip(1))
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            string k = raw[..eq].Trim(), v = raw[(eq + 1)..].Trim();
            if (v.StartsWith("\"") && v.EndsWith("\"") && v.Length >= 2) v = v[1..^1].Replace("\\\"", "\"");
            // RFC 2231：name*0*=、name*=
            var m = Regex.Match(k, @"^(.+?)\*(?:(\d+)\*?)?$");
            if (m.Success)
            {
                string bk = m.Groups[1].Value;
                int n = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
                bool enc = k.EndsWith("*");
                if (!star.TryGetValue(bk, out var d)) star[bk] = d = new();
                d[n] = (v, enc);
                continue;
            }
            p[k] = DecodeWords(v);
        }
        foreach (var (k, segs) in star)
        {
            string charset = "utf-8";
            var bytes = new List<byte>();
            var sb = new StringBuilder();
            bool first = true;
            foreach (var (_, (v0, enc)) in segs)
            {
                string v = v0;
                if (enc)
                {
                    if (first)
                    {
                        var q = v.Split('\'');
                        if (q.Length >= 3) { charset = q[0]; v = string.Join("'", q.Skip(2)); }
                    }
                    for (int i = 0; i < v.Length; i++)
                    {
                        if (v[i] == '%' && i + 2 < v.Length && Uri.IsHexDigit(v[i + 1]) && Uri.IsHexDigit(v[i + 2]))
                        { bytes.Add(byte.Parse(v.Substring(i + 1, 2), NumberStyles.HexNumber)); i += 2; }
                        else bytes.Add((byte)v[i]);
                    }
                }
                else bytes.AddRange(Encoding.UTF8.GetBytes(v));
                first = false;
            }
            p[k] = Enc(charset).GetString(bytes.ToArray());
        }
        return (value, p);
    }

    private static List<string> SplitSemi(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        foreach (char c in s)
        {
            if (c == '"') q = !q;
            if (c == ';' && !q) { list.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    public static (string Name, string Addr) ParseAddress(string s)
    {
        s = s.Trim();
        var m = Regex.Match(s, @"^(.*?)<([^>]+)>");
        if (m.Success)
        {
            string n = m.Groups[1].Value.Trim().Trim('"').Trim();
            return (n.Length > 0 ? n : m.Groups[2].Value.Trim(), m.Groups[2].Value.Trim());
        }
        return (s, s);
    }

    /// <summary>「a@x.com, "名" &lt;b@y.com&gt;」→ 地址清單。</summary>
    public static List<string> Addresses(string s)
    {
        var res = new List<string>();
        foreach (Match m in Regex.Matches(s ?? "", @"[A-Za-z0-9._%+\-']+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}"))
            if (!res.Contains(m.Value, StringComparer.OrdinalIgnoreCase)) res.Add(m.Value);
        return res;
    }

    public static DateTime ParseDate(string s)
    {
        s = Regex.Replace(s ?? "", @"\s*\(.*\)\s*$", "").Trim();
        s = Regex.Replace(s, @"^[A-Za-z]{3},\s*", "");
        foreach (var f in new[] { "d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm zzz", "d MMM yy HH:mm:ss zzz", "dd MMM yyyy HH:mm:ss zzz" })
        {
            string t = Regex.Replace(s, @"([+-]\d\d)(\d\d)$", "$1:$2");
            if (DateTimeOffset.TryParseExact(t, f, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)) return d.LocalDateTime;
        }
        string tz = Regex.Replace(s, @"\s+(GMT|UT|UTC)$", " +00:00");
        if (DateTimeOffset.TryParse(tz, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d2)) return d2.LocalDateTime;
        return DateTime.MinValue;
    }

    public static MailMessage Parse(byte[] raw)
    {
        var msg = new MailMessage { Root = ParsePart(raw) };
        var h = msg.Root.Headers;
        msg.Subject = DecodeWords(Get(h, "subject"));
        (msg.From, msg.FromAddr) = ParseAddress(DecodeWords(Get(h, "from")));
        msg.To = DecodeWords(Get(h, "to"));
        msg.Cc = DecodeWords(Get(h, "cc"));
        msg.ReplyTo = DecodeWords(Get(h, "reply-to"));
        msg.MessageId = Get(h, "message-id").Trim();
        msg.References = Get(h, "references").Trim();
        msg.Date = ParseDate(Get(h, "date"));
        Collect(msg.Root, msg);
        return msg;
    }

    private static void Collect(MailPart p, MailMessage msg)
    {
        if (p.ContentType.StartsWith("multipart/"))
        {
            if (p.ContentType == "multipart/alternative")
            {
                // 揀最好嘅：html 優先
                var html = p.Children.LastOrDefault(c => c.ContentType == "text/html" || c.ContentType.StartsWith("multipart/related"));
                var text = p.Children.FirstOrDefault(c => c.ContentType == "text/plain");
                if (text != null && msg.Text.Length == 0) msg.Text = TextOf(text);
                if (html != null) Collect(html, msg);
                foreach (var c in p.Children.Where(c => c != html && c != text && !c.ContentType.StartsWith("text/"))) Collect(c, msg);
                return;
            }
            foreach (var c in p.Children) Collect(c, msg);
            return;
        }
        if (p.ContentType == "message/rfc822" && p.Disposition != "attachment")
        {
            var inner = Parse(p.Body);
            if (msg.Text.Length == 0) msg.Text = inner.Text;
            if (msg.Html.Length == 0) msg.Html = inner.Html;
            msg.Attachments.AddRange(inner.Attachments);
            return;
        }
        bool isAttach = p.Disposition == "attachment" || (p.FileName.Length > 0 && p.Disposition != "inline")
                        || !(p.ContentType.StartsWith("text/") || p.ContentId.Length > 0);
        if (!isAttach && p.ContentType == "text/html" && msg.Html.Length == 0) { msg.Html = TextOf(p); return; }
        if (!isAttach && p.ContentType == "text/plain" && msg.Text.Length == 0) { msg.Text = TextOf(p); return; }
        if (p.ContentId.Length > 0 && !p.ContentType.StartsWith("text/")) { msg.Attachments.Add(p); return; } // 內嵌圖（cid）
        if (isAttach || p.ContentType.StartsWith("text/")) msg.Attachments.Add(p);
    }

    public static string TextOf(MailPart p) => Enc(p.Params.GetValueOrDefault("charset")).GetString(p.Body);

    private static MailPart ParsePart(byte[] data)
    {
        var part = new MailPart { Headers = ParseHeaders(data, out int bodyStart) };
        var (ct, ps) = ParseParams(Get(part.Headers, "content-type") is { Length: > 0 } c ? c : "text/plain; charset=us-ascii");
        part.ContentType = ct.Length > 0 ? ct : "text/plain";
        part.Params = ps;
        var (disp, dps) = ParseParams(Get(part.Headers, "content-disposition"));
        part.Disposition = disp;
        part.FileName = dps.GetValueOrDefault("filename") ?? ps.GetValueOrDefault("name") ?? "";
        part.ContentId = Get(part.Headers, "content-id").Trim().Trim('<', '>');
        var body = new byte[Math.Max(0, data.Length - bodyStart)];
        Buffer.BlockCopy(data, Math.Min(bodyStart, data.Length), body, 0, body.Length);
        if (part.ContentType.StartsWith("multipart/") && ps.TryGetValue("boundary", out var boundary) && boundary.Length > 0)
        {
            foreach (var sub in SplitMultipart(body, boundary)) part.Children.Add(ParsePart(sub));
            return part;
        }
        string cte = Get(part.Headers, "content-transfer-encoding").Trim().ToLowerInvariant();
        try
        {
            part.Body = cte switch
            {
                "base64" => Convert.FromBase64String(Regex.Replace(Encoding.ASCII.GetString(body), @"[^A-Za-z0-9+/=]", "") is var b ? b[..(b.Length - b.Length % 4)] : ""),
                "quoted-printable" => QpDecode(Encoding.Latin1.GetString(body)),
                _ => body
            };
        }
        catch { part.Body = body; }
        return part;
    }

    private static List<byte[]> SplitMultipart(byte[] body, string boundary)
    {
        var res = new List<byte[]>();
        var delim = Encoding.ASCII.GetBytes("--" + boundary);
        var starts = new List<int>();
        int i = 0;
        while (true)
        {
            int k = IndexOf(body, delim, i);
            if (k < 0) break;
            // 要喺行頭
            if (k == 0 || body[k - 1] == '\n') starts.Add(k);
            i = k + delim.Length;
        }
        for (int s = 0; s < starts.Count; s++)
        {
            int st = starts[s] + delim.Length;
            if (st + 1 < body.Length && body[st] == '-' && body[st + 1] == '-') break; // 結尾
            // 跳過行尾
            int nl = Array.IndexOf(body, (byte)'\n', st);
            if (nl < 0) break;
            int from = nl + 1;
            int to = s + 1 < starts.Count ? starts[s + 1] : body.Length;
            // 去走分隔線前面嘅 CRLF
            int end = to;
            if (end > from && body[end - 1] == '\n') end--;
            if (end > from && body[end - 1] == '\r') end--;
            if (end < from) end = from;
            var seg = new byte[end - from];
            Buffer.BlockCopy(body, from, seg, 0, seg.Length);
            res.Add(seg);
        }
        return res;
    }

    private static int IndexOf(byte[] h, byte[] n, int start)
    {
        for (int i = start; i <= h.Length - n.Length; i++)
        {
            if (h[i] != n[0]) continue;
            int j = 1;
            while (j < n.Length && h[i + j] == n[j]) j++;
            if (j == n.Length) return i;
        }
        return -1;
    }

    /// <summary>HTML 入面嘅 cid: 圖片換做 data: URL（內嵌圖可以顯示）。</summary>
    public static string InlineImages(string html, List<MailPart> parts)
    {
        foreach (var p in parts.Where(p => p.ContentId.Length > 0))
            html = html.Replace("cid:" + p.ContentId, $"data:{p.ContentType};base64,{Convert.ToBase64String(p.Body)}", StringComparison.OrdinalIgnoreCase);
        return html;
    }

    /// <summary>HTML → 純文字（AI 摘要 / 回覆引用用）。</summary>
    public static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>|</p>|</div>|</tr>|</h\d>|</li>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t ]+", " ");
        s = Regex.Replace(s, @"\n\s*\n\s*\n+", "\n\n");
        return s.Trim();
    }

    public static string PlainText(MailMessage m) => m.Text.Trim().Length > 0 ? m.Text.Trim() : HtmlToText(m.Html);

    // ---------------- 砌信 ----------------

    public static string EncodeWord(string s) =>
        s.All(c => c < 128) ? s : "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?=";

    private static string EncodeAddrList(string list)
    {
        // 「名 <a@b>」入面嘅名要 encode
        return string.Join(", ", SplitAddrs(list).Select(a =>
        {
            var (n, addr) = ParseAddress(a);
            return n.Length > 0 && n != addr ? $"{EncodeWord(n)} <{addr}>" : addr;
        }));
    }

    public static List<string> SplitAddrs(string s)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        bool q = false, angle = false;
        foreach (char c in s ?? "")
        {
            if (c == '"') q = !q;
            if (c == '<') angle = true;
            if (c == '>') angle = false;
            if ((c == ',' || c == ';') && !q && !angle) { if (sb.ToString().Trim().Length > 0) res.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(c);
        }
        if (sb.ToString().Trim().Length > 0) res.Add(sb.ToString().Trim());
        return res;
    }

    public sealed record Outgoing(string FromName, string FromAddr, string To, string Cc, string Bcc, string Subject, string Body,
        List<string> Attachments, string InReplyTo = "", string References = "");

    /// <summary>砌一封信（UTF-8 純文字 + 附件）。</summary>
    public static byte[] Build(Outgoing o)
    {
        var sb = new StringBuilder();
        string boundary = "=_mt_" + Guid.NewGuid().ToString("N");
        string domain = o.FromAddr.Contains('@') ? o.FromAddr[(o.FromAddr.IndexOf('@') + 1)..] : "localhost";
        sb.Append("From: ").Append(o.FromName.Length > 0 ? $"{EncodeWord(o.FromName)} <{o.FromAddr}>" : o.FromAddr).Append("\r\n");
        if (o.To.Trim().Length > 0) sb.Append("To: ").Append(EncodeAddrList(o.To)).Append("\r\n");
        if (o.Cc.Trim().Length > 0) sb.Append("Cc: ").Append(EncodeAddrList(o.Cc)).Append("\r\n");
        sb.Append("Subject: ").Append(EncodeWord(o.Subject)).Append("\r\n");
        sb.Append("Date: ").Append(DateTimeOffset.Now.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture))
          .Append(DateTimeOffset.Now.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "")).Append("\r\n");
        sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append('@').Append(domain).Append(">\r\n");
        if (o.InReplyTo.Length > 0) sb.Append("In-Reply-To: ").Append(o.InReplyTo).Append("\r\n");
        if (o.References.Length > 0) sb.Append("References: ").Append(o.References).Append("\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        string bodyB64 = Wrap(Convert.ToBase64String(Encoding.UTF8.GetBytes(o.Body.Replace("\r\n", "\n").Replace("\n", "\r\n"))));
        if (o.Attachments.Count == 0)
        {
            sb.Append("Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(bodyB64);
            return Encoding.ASCII.GetBytes(sb.ToString());
        }
        sb.Append($"Content-Type: multipart/mixed; boundary=\"{boundary}\"\r\n\r\n");
        sb.Append($"--{boundary}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(bodyB64);
        foreach (var f in o.Attachments)
        {
            string name = Path.GetFileName(f);
            string enc = EncodeWord(name);
            string star = "UTF-8''" + Uri.EscapeDataString(name);
            sb.Append($"--{boundary}\r\nContent-Type: {MimeType(name)}; name=\"{enc}\"\r\n");
            sb.Append($"Content-Disposition: attachment; filename=\"{enc}\"; filename*={star}\r\nContent-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(Wrap(Convert.ToBase64String(File.ReadAllBytes(f))));
        }
        sb.Append($"--{boundary}--\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Wrap(string b64)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < b64.Length; i += 76) sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n");
        return sb.ToString();
    }

    public static string MimeType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".pdf" => "application/pdf",
        ".txt" => "text/plain", ".zip" => "application/zip", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".mp4" => "video/mp4", ".mp3" => "audio/mpeg",
        _ => "application/octet-stream"
    };
}

// ---------------------------------------------------------------------
//  SMTP（寄信）
// ---------------------------------------------------------------------
internal static class Smtp
{
    public static async Task SendAsync(MailAccount a, IEnumerable<string> rcpts, byte[] message, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(a.SmtpHost, a.SmtpPort, ct);
        Stream s = tcp.GetStream();
        if (a.SmtpPort == 465)
        {
            var ssl = new SslStream(s);
            await ssl.AuthenticateAsClientAsync(a.SmtpHost);
            s = ssl;
        }
        var r = new StreamReader(s, Encoding.ASCII, false, 4096, leaveOpen: true);
        async Task<string> Read()
        {
            string all = "";
            while (true)
            {
                var l = await r.ReadLineAsync(ct) ?? throw new IOException(Lang.T("SMTP 伺服器斷咗線"));
                all += l + "\n";
                if (l.Length < 4 || l[3] != '-') return all;
            }
        }
        async Task<string> Cmd(string c, params int[] ok)
        {
            var b = Encoding.ASCII.GetBytes(c + "\r\n");
            await s.WriteAsync(b, ct);
            await s.FlushAsync(ct);
            var resp = await Read();
            if (!int.TryParse(resp[..3], out int code) || !ok.Contains(code)) throw new InvalidOperationException("SMTP: " + resp.Trim());
            return resp;
        }
        await Read(); // 220
        var ehlo = await Cmd("EHLO multitool", 250);
        if (a.SmtpPort != 465 && ehlo.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase))
        {
            await Cmd("STARTTLS", 220);
            var ssl = new SslStream(s);
            await ssl.AuthenticateAsClientAsync(a.SmtpHost);
            s = ssl;
            r = new StreamReader(s, Encoding.ASCII, false, 4096, leaveOpen: true);
            await Cmd("EHLO multitool", 250);
        }
        if (a.IsOAuth)
        {
            string token = await MsOAuth.AccessTokenAsync(a, ct);
            string xo = Convert.ToBase64String(Encoding.UTF8.GetBytes($"user={a.Email}\u0001auth=Bearer {token}\u0001\u0001"));
            await Cmd("AUTH XOAUTH2 " + xo, 235);
        }
        else
        {
            string plain = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + a.Email + "\0" + MailStore.Unprotect(a.Secret)));
            await Cmd("AUTH PLAIN " + plain, 235);
        }
        await Cmd($"MAIL FROM:<{a.Email}>", 250);
        foreach (var to in rcpts) await Cmd($"RCPT TO:<{to}>", 250, 251);
        await Cmd("DATA", 354);
        // 行頭 "." 要變 ".."
        var text = Encoding.ASCII.GetString(message).Replace("\r\n.", "\r\n..");
        if (text.StartsWith(".")) text = "." + text;
        if (!text.EndsWith("\r\n")) text += "\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(text + ".\r\n"), ct);
        await s.FlushAsync(ct);
        var done = await Read();
        if (!done.StartsWith("250")) throw new InvalidOperationException("SMTP: " + done.Trim());
        try { await Cmd("QUIT", 221); } catch { }
    }
}
