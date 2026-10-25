using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.Common;
using MultiTool.UI;

namespace MultiTool.Scripts;

// =====================================================================
//  腳本庫（P2P）：冇中央伺服器。
//  * 開咗分享嘅 MultiTool 會喺 BitTorrent 公用 DHT 用同一個 key 登記自己（IP + port）。
//  * 搜尋 = 喺 DHT 攞返所有登記咗嘅人，逐個直接連過去攞佢公開咗嘅腳本清單，喺自己部機篩選。
//  * 下載 = 直接由對方部機攞（TCP）。對方熄咗機 / 連唔到就算。
// =====================================================================

/// <summary>分享設定（data\share.txt）。</summary>
internal static class ShareSettings
{
    private static string File_ => Path.Combine(AppPaths.Data, "share.txt");
    public static bool Enabled;
    public static string Name = "";
    public static int Port = 47321;

    static ShareSettings() => Load();

    public static void Load()
    {
        try
        {
            if (!File.Exists(File_)) return;
            foreach (var line in File.ReadAllLines(File_))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line[..eq], v = line[(eq + 1)..];
                if (k == "enabled") Enabled = v == "1";
                else if (k == "name") Name = v;
                else if (k == "port" && int.TryParse(v, out var p) && p is > 1024 and < 65536) Port = p;
            }
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllLines(File_, new[] { "enabled=" + (Enabled ? "1" : "0"), "name=" + Name.Replace('\n', ' '), "port=" + Port });
        }
        catch { }
    }

    public static string DisplayName => string.IsNullOrWhiteSpace(Name) ? Lang.T("匿名") : Name.Trim();
}

// ---------------------------------------------------------------------
//  Bencode（DHT 用）
// ---------------------------------------------------------------------
internal static class Bencode
{
    public static byte[] Encode(object o)
    {
        using var ms = new MemoryStream();
        Write(ms, o);
        return ms.ToArray();
    }

    private static void Write(Stream s, object o)
    {
        void W(string t) { var b = Encoding.ASCII.GetBytes(t); s.Write(b, 0, b.Length); }
        switch (o)
        {
            case byte[] b: W(b.Length + ":"); s.Write(b, 0, b.Length); break;
            case string str: Write(s, Encoding.UTF8.GetBytes(str)); break;
            case int i: W("i" + i + "e"); break;
            case long l: W("i" + l + "e"); break;
            case IList<object> list: W("l"); foreach (var x in list) Write(s, x); W("e"); break;
            case IDictionary<string, object> d:
                W("d");
                foreach (var kv in d.OrderBy(kv => kv.Key, StringComparer.Ordinal)) { Write(s, kv.Key); Write(s, kv.Value); }
                W("e");
                break;
            default: throw new ArgumentException("bencode");
        }
    }

    public static object? Decode(byte[] b)
    {
        int i = 0;
        try { return Read(b, ref i); } catch { return null; }
    }

    private static object Read(byte[] b, ref int i)
    {
        char c = (char)b[i];
        if (c == 'i')
        {
            int e = Array.IndexOf(b, (byte)'e', i);
            long v = long.Parse(Encoding.ASCII.GetString(b, i + 1, e - i - 1));
            i = e + 1;
            return v;
        }
        if (c == 'l')
        {
            i++;
            var list = new List<object>();
            while (b[i] != (byte)'e') list.Add(Read(b, ref i));
            i++;
            return list;
        }
        if (c == 'd')
        {
            i++;
            var d = new Dictionary<string, object>();
            while (b[i] != (byte)'e')
            {
                var k = (byte[])Read(b, ref i);
                d[Encoding.Latin1.GetString(k)] = Read(b, ref i);
            }
            i++;
            return d;
        }
        int colon = Array.IndexOf(b, (byte)':', i);
        int len = int.Parse(Encoding.ASCII.GetString(b, i, colon - i));
        var bytes = new byte[len];
        Buffer.BlockCopy(b, colon + 1, bytes, 0, len);
        i = colon + 1 + len;
        return bytes;
    }
}

// ---------------------------------------------------------------------
//  DHT（BitTorrent mainline，淨係做 client：get_peers + announce_peer）
// ---------------------------------------------------------------------
internal sealed class DhtClient : IDisposable
{
    /// <summary>所有 MultiTool 腳本庫共用嘅 key。</summary>
    public static readonly byte[] InfoHash = SHA1.HashData(Encoding.UTF8.GetBytes("MultiTool-ScriptLibrary-v1"));

    private static readonly (string Host, int Port)[] Bootstrap =
    {
        ("router.bittorrent.com", 6881), ("dht.transmissionbt.com", 6881), ("router.utorrent.com", 6881), ("dht.libtorrent.org", 25401)
    };

    private readonly UdpClient _udp;
    private readonly byte[] _id = RandomNumberGenerator.GetBytes(20);
    private readonly ConcurrentQueue<(IPEndPoint From, Dictionary<string, object> Msg)> _inbox = new();
    private readonly CancellationTokenSource _cts = new();
    private int _tid;

    public DhtClient()
    {
        _udp = new UdpClient(0);
        try { _udp.Client.IOControl(-1744830452 /*SIO_UDP_CONNRESET*/, new byte[] { 0 }, null); } catch { }
        _ = Task.Run(ReceiveLoop);
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp.ReceiveAsync(_cts.Token);
                if (Bencode.Decode(r.Buffer) is Dictionary<string, object> d) _inbox.Enqueue((r.RemoteEndPoint, d));
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch { }
        }
    }

    private void Send(IPEndPoint to, string q, Dictionary<string, object> args)
    {
        args["id"] = _id;
        int t = Interlocked.Increment(ref _tid) & 0xFFFF;
        var msg = new Dictionary<string, object> { ["t"] = new[] { (byte)(t >> 8), (byte)t }, ["y"] = "q", ["q"] = q, ["a"] = args };
        try { var b = Bencode.Encode(msg); _udp.Send(b, b.Length, to); } catch { }
    }

    private static byte[] Xor(byte[] a, byte[] b) { var r = new byte[20]; for (int i = 0; i < 20; i++) r[i] = (byte)(a[i] ^ b[i]); return r; }
    private static int Cmp(byte[] a, byte[] b) { for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]); return 0; }

    /// <summary>喺 DHT 搵登記咗嘅人；announcePort &gt; 0 就順手登記自己。回傳 (對方 IP:port 清單, 我自己嘅外部 IP)。</summary>
    public async Task<(List<IPEndPoint> Peers, IPAddress? MyIp)> LookupAsync(int announcePort, TimeSpan timeout, CancellationToken ct, byte[]? key = null)
    {
        var target = key ?? InfoHash;
        var nodes = new Dictionary<string, (IPEndPoint Ep, byte[] Id)>();
        var queried = new HashSet<string>();
        var tokens = new Dictionary<string, (IPEndPoint Ep, byte[] Id, byte[] Token)>();
        var peers = new HashSet<string>();
        var peerList = new List<IPEndPoint>();
        IPAddress? myIp = null;

        foreach (var (host, port) in Bootstrap)
        {
            try
            {
                foreach (var ip in (await Dns.GetHostAddressesAsync(host, ct)).Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(2))
                {
                    var ep = new IPEndPoint(ip, port);
                    queried.Add(ep.ToString());
                    Send(ep, "get_peers", new() { ["info_hash"] = target });
                }
            }
            catch { }
        }

        var deadline = DateTime.UtcNow + timeout;
        int idle = 0;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(400, ct);
            bool closer = false;
            byte[]? bestBefore = nodes.Values.Select(n => Xor(n.Id, target)).OrderBy(x => x, Comparer<byte[]>.Create(Cmp)).FirstOrDefault();
            while (_inbox.TryDequeue(out var m))
            {
                if (!(m.Msg.TryGetValue("y", out var y) && y is byte[] yb && yb.Length == 1 && yb[0] == (byte)'r')) continue;
                if (m.Msg.TryGetValue("ip", out var ipv) && ipv is byte[] ipb && ipb.Length >= 4) myIp ??= new IPAddress(ipb.AsSpan(0, 4));
                if (m.Msg.GetValueOrDefault("r") is not Dictionary<string, object> r) continue;
                var rid = r.GetValueOrDefault("id") as byte[];
                if (r.GetValueOrDefault("token") is byte[] tok && rid is { Length: 20 }) tokens[m.From.ToString()] = (m.From, rid, tok);
                if (r.GetValueOrDefault("values") is List<object> vals)
                    foreach (var v in vals.OfType<byte[]>().Where(v => v.Length == 6))
                    {
                        var ep = new IPEndPoint(new IPAddress(v.AsSpan(0, 4)), (v[4] << 8) | v[5]);
                        if (peers.Add(ep.ToString())) peerList.Add(ep);
                    }
                if (r.GetValueOrDefault("nodes") is byte[] nb)
                    for (int i = 0; i + 26 <= nb.Length; i += 26)
                    {
                        var id = nb.AsSpan(i, 20).ToArray();
                        var ep = new IPEndPoint(new IPAddress(nb.AsSpan(i + 20, 4)), (nb[i + 24] << 8) | nb[i + 25]);
                        if (ep.Port == 0) continue;
                        if (nodes.TryAdd(ep.ToString(), (ep, id)) && (bestBefore == null || Cmp(Xor(id, target), bestBefore) < 0)) closer = true;
                    }
            }
            idle = closer ? 0 : idle + 1;
            // 下一輪：問最近嘅 8 個未問過嘅
            var next = nodes.Values.Where(n => !queried.Contains(n.Ep.ToString()))
                .OrderBy(n => Xor(n.Id, target), Comparer<byte[]>.Create(Cmp)).Take(8).ToList();
            foreach (var n in next)
            {
                queried.Add(n.Ep.ToString());
                Send(n.Ep, "get_peers", new() { ["info_hash"] = target });
            }
            if ((next.Count == 0 || idle >= 4) && queried.Count > 20) break;
        }

        if (announcePort > 0)
        {
            foreach (var t in tokens.Values.OrderBy(t => Xor(t.Id, target), Comparer<byte[]>.Create(Cmp)).Take(10))
                Send(t.Ep, "announce_peer", new() { ["info_hash"] = target, ["port"] = announcePort, ["token"] = t.Token, ["implied_port"] = 0 });
        }
        return (peerList, myIp);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Dispose(); } catch { }
    }
}

// ---------------------------------------------------------------------
//  UPnP：叫路由器開 port（開唔到就算）
// ---------------------------------------------------------------------
internal static class Upnp
{
    private static (string Control, string Service, IPAddress Local)? _gw;

    public static Task<bool> MapAsync(int port, CancellationToken ct) => MapAsync(port, "TCP", "MultiTool", ct);

    /// <summary>喺路由器開端口（TCP / UDP）。</summary>
    public static async Task<bool> MapAsync(int port, string proto, string desc, CancellationToken ct)
    {
        try
        {
            _gw ??= await DiscoverAsync(ct);
            if (_gw is not { } g) return false;
            desc = System.Security.SecurityElement.Escape(desc) ?? "MultiTool";
            string body = $"<u:AddPortMapping xmlns:u=\"{g.Service}\"><NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort>" +
                          $"<NewProtocol>{proto}</NewProtocol><NewInternalPort>{port}</NewInternalPort><NewInternalClient>{g.Local}</NewInternalClient>" +
                          $"<NewEnabled>1</NewEnabled><NewPortMappingDescription>{desc}</NewPortMappingDescription><NewLeaseDuration>0</NewLeaseDuration></u:AddPortMapping>";
            return await SoapAsync(g.Control, g.Service, "AddPortMapping", body, ct);
        }
        catch { return false; }
    }

    /// <summary>路由器睇到嘅本機 IP（UPnP 搵到先有）。</summary>
    public static IPAddress? LocalAddress => _gw?.Local;

    public static void Unmap(int port, string proto = "TCP")
    {
        try
        {
            if (_gw is not { } g) return;
            string body = $"<u:DeletePortMapping xmlns:u=\"{g.Service}\"><NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>{proto}</NewProtocol></u:DeletePortMapping>";
            SoapAsync(g.Control, g.Service, "DeletePortMapping", body, CancellationToken.None).Wait(1500);
        }
        catch { }
    }

    private static async Task<bool> SoapAsync(string url, string service, string action, string body, CancellationToken ct)
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var xml = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" + body + "</s:Body></s:Envelope>";
        var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url) { Content = new System.Net.Http.StringContent(xml, Encoding.UTF8, "text/xml") };
        req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service}#{action}\"");
        var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode;
    }

    private static async Task<(string, string, IPAddress)?> DiscoverAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(0);
        var msg = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
        await udp.SendAsync(msg, msg.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until)
        {
            var recv = udp.ReceiveAsync(ct).AsTask();
            if (await Task.WhenAny(recv, Task.Delay(until - DateTime.UtcNow, ct)) != recv) break;
            var text = Encoding.ASCII.GetString(recv.Result.Buffer);
            var loc = text.Split("\r\n").FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))?[9..].Trim();
            if (loc == null) continue;
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                var xml = await http.GetStringAsync(loc, ct);
                foreach (var svc in new[] { "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANIPConnection:2", "urn:schemas-upnp-org:service:WANPPPConnection:1" })
                {
                    int i = xml.IndexOf(svc, StringComparison.Ordinal);
                    if (i < 0) continue;
                    int c = xml.IndexOf("<controlURL>", i, StringComparison.Ordinal);
                    if (c < 0) continue;
                    int e = xml.IndexOf("</controlURL>", c, StringComparison.Ordinal);
                    var path = xml[(c + 12)..e].Trim();
                    var baseUri = new Uri(loc);
                    var control = new Uri(baseUri, path).ToString();
                    // 本機向住路由器嗰張網卡嘅 IP
                    using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    s.Connect(baseUri.Host, baseUri.Port);
                    var local = ((IPEndPoint)s.LocalEndPoint!).Address;
                    return (control, svc, local);
                }
            }
            catch { }
        }
        return null;
    }
}

// ---------------------------------------------------------------------
//  腳本打包（zip：script.txt + 用到嘅圖），同埋風險摘要
// ---------------------------------------------------------------------
internal static class ScriptPackage
{
    private const long MaxSize = 30L * 1024 * 1024;
    private static readonly string[] ImgExt = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    private static IEnumerable<Step> AllSteps(Script sc)
    {
        IEnumerable<Step> Walk(IEnumerable<Step> steps) => steps.SelectMany(s => new[] { s }.Concat(s.Kids().SelectMany(Walk)));
        return Walk(sc.Steps).Concat(sc.Threads.SelectMany(t => Walk(t.Steps))).Concat(sc.Ifs.SelectMany(b => Walk(b.Steps).Concat(Walk(b.Else))));
    }

    /// <summary>要提醒下載者嘅步驟（系統動作、打字）。</summary>
    public static List<string> Risks(Script sc)
    {
        var r = new List<string>();
        foreach (var st in AllSteps(sc))
        {
            if (st.Kind == StepKind.System && st.Sys != SysAction.PlaySound)
                r.Add(Step.SysName(st.Sys) + (st.Target.Length > 0 ? "：" + (st.TargetName.Length > 0 ? st.TargetName : st.Target) : ""));
            else if (st.Kind == StepKind.Text)
                r.Add(Lang.T("輸入文字") + "：" + (st.Target.Length > 30 ? st.Target[..30] + "…" : st.Target));
        }
        return r.Distinct().ToList();
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static byte[] Pack(Script src)
    {
        var sc = Script.FromLines(src.Lines(), src.Folder);
        // 唔好帶走私人資料：視窗 handle、快捷鍵、公開狀態；路徑入面嘅用戶資料夾換做 %USERPROFILE%
        sc.TargetHwnd = 0; sc.TargetIndex = 0; sc.Hotkey = default; sc.Public = false;
        foreach (var st in AllSteps(sc))
            if (st.Kind == StepKind.System && st.Target.StartsWith(Home, StringComparison.OrdinalIgnoreCase))
                st.Target = "%USERPROFILE%" + st.Target[Home.Length..];
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var e = zip.CreateEntry("script.txt");
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) foreach (var l in sc.Lines()) w.WriteLine(l);
            if (Directory.Exists(src.Dir))
                foreach (var f in Directory.GetFiles(src.Dir))
                {
                    var name = Path.GetFileName(f);
                    if (!ImgExt.Contains(Path.GetExtension(f).ToLowerInvariant()) || !src.UsesImage(name)) continue;
                    zip.CreateEntryFromFile(f, name);
                }
        }
        return ms.ToArray();
    }

    /// <summary>讀包入面嘅腳本（未寫入硬碟）。</summary>
    public static (Script Script, Dictionary<string, byte[]> Images)? Read(byte[] data)
    {
        if (data.Length > MaxSize) return null;
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        string[]? lines = null;
        var images = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var e in zip.Entries)
        {
            string name = Path.GetFileName(e.FullName);
            if (name != e.FullName || name.Length == 0) continue; // 唔准資料夾路徑
            total += e.Length;
            if (total > MaxSize) return null;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            if (name == "script.txt") lines = Encoding.UTF8.GetString(ms.ToArray()).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            else if (ImgExt.Contains(Path.GetExtension(name).ToLowerInvariant())) images[name] = ms.ToArray();
        }
        if (lines == null) return null;
        var sc = Script.FromLines(lines, "");
        foreach (var st in AllSteps(sc))
            if (st.Kind == StepKind.System) st.Target = Environment.ExpandEnvironmentVariables(st.Target);
        return (sc, images);
    }

    /// <summary>寫入「我的腳本」（新資料夾，唔公開、唔設快捷鍵）。</summary>
    public static Script Install(Script sc, Dictionary<string, byte[]> images)
    {
        sc.Folder = "";
        sc.Public = false;
        sc.Hotkey = default;
        Directory.CreateDirectory(sc.Dir);
        foreach (var (name, bytes) in images) File.WriteAllBytes(Path.Combine(sc.Dir, name), bytes);
        sc.Save();
        return sc;
    }
}

// ---------------------------------------------------------------------
//  分享服務：TCP 伺服器 + 定時喺 DHT 登記
// ---------------------------------------------------------------------
internal sealed record LibraryItem(string Id, string Name, string Desc, string Author, int Steps, List<string> Risks, string Version, IPEndPoint Peer);

internal static class ShareService
{
    private static TcpListener? _listener;
    private static CancellationTokenSource? _cts;
    public static string Status { get; private set; } = "";
    public static event Action? StatusChanged;
    private static readonly SemaphoreSlim Slots = new(8);

    private static void SetStatus(string s) { Status = s; StatusChanged?.Invoke(); }

    public static bool Running => _listener != null;

    public static void Start()
    {
        Stop();
        if (!ShareSettings.Enabled) { SetStatus(Lang.T("未開啟分享")); return; }
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, ShareSettings.Port);
            _listener.Start();
            var ct = _cts.Token;
            _ = Task.Run(() => AcceptLoop(_listener, ct));
            _ = Task.Run(() => AnnounceLoop(ct));
            SetStatus(Lang.F("分享中（port {0}）", ShareSettings.Port));
        }
        catch (Exception ex)
        {
            _listener = null;
            SetStatus(Lang.T("開唔到分享：") + ex.Message);
        }
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        if (_listener != null) Upnp.Unmap(ShareSettings.Port);
        _listener = null;
        _cts = null;
    }

    private static async Task AnnounceLoop(CancellationToken ct)
    {
        bool mapped = await Upnp.MapAsync(ShareSettings.Port, ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var dht = new DhtClient();
                await dht.LookupAsync(ShareSettings.Port, TimeSpan.FromSeconds(12), ct);
                SetStatus(Lang.F("分享中（port {0}{1}）", ShareSettings.Port, mapped ? Lang.T("，路由器已自動開 port") : Lang.T("，路由器未能自動開 port，其他人可能連唔到")));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AppPaths.LogError("DHT 登記", ex); }
            try { await Task.Delay(TimeSpan.FromMinutes(14), ct); } catch { return; }
        }
    }

    private static async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct); }
            catch { return; }
            if (!await Slots.WaitAsync(0)) { c.Dispose(); continue; }
            _ = Task.Run(async () =>
            {
                try { await Serve(c, ct); }
                catch { }
                finally { c.Dispose(); Slots.Release(); }
            });
        }
    }

    private static async Task<string?> ReadLine(Stream s, CancellationToken ct)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (buf.Count < 512)
        {
            int n = await s.ReadAsync(one, ct);
            if (n == 0) return null;
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buf.ToArray()).TrimEnd('\r');
            buf.Add(one[0]);
        }
        return null;
    }

    private static async Task Serve(TcpClient c, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = cts.Token;
        var s = c.GetStream();
        var line = await ReadLine(s, ct);
        if (line == null || !line.StartsWith("MTS1 ")) return;
        var scripts = Script.LoadPublic();
        if (line == "MTS1 LIST")
        {
            var arr = new JsonArray(scripts.Select(x => (JsonNode)new JsonObject
            {
                ["id"] = x.Folder, ["name"] = x.Name, ["desc"] = x.Desc, ["author"] = ShareSettings.DisplayName,
                ["steps"] = x.Steps.Count + x.Threads.Sum(t => t.Steps.Count), ["risks"] = new JsonArray(ScriptPackage.Risks(x).Select(r => (JsonNode)r!).ToArray()),
                ["version"] = Program.Version
            }).ToArray());
            var b = Encoding.UTF8.GetBytes(arr.ToJsonString() + "\n");
            await s.WriteAsync(b, ct);
        }
        else if (line.StartsWith("MTS1 GET "))
        {
            var id = line[9..];
            var sc = scripts.FirstOrDefault(x => x.Folder == id);
            if (sc == null) { await s.WriteAsync(Encoding.UTF8.GetBytes("ERR\n"), ct); return; }
            var data = ScriptPackage.Pack(sc);
            await s.WriteAsync(Encoding.UTF8.GetBytes($"OK {data.Length}\n"), ct);
            await s.WriteAsync(data, ct);
        }
    }

    // ---------------- 搜尋 / 下載（client） ----------------

    private static async Task<Stream?> Connect(IPEndPoint ep, TcpClient c, CancellationToken ct)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(TimeSpan.FromSeconds(4));
        try { await c.ConnectAsync(ep.Address, ep.Port, t.Token); return c.GetStream(); }
        catch { return null; }
    }

    /// <summary>搵所有在線用戶公開咗嘅腳本。progress：(搵到幾多人, 連到幾多人)。</summary>
    public static async Task<List<LibraryItem>> SearchAsync(Action<int, int>? progress, CancellationToken ct)
    {
        using var dht = new DhtClient();
        var (peers, myIp) = await dht.LookupAsync(0, TimeSpan.FromSeconds(10), ct);
        // 自己部機都計埋（方便自己睇吓公開咗咩），用本機地址連
        var targets = peers.Where(p => !(myIp != null && p.Address.Equals(myIp) && p.Port == ShareSettings.Port)).ToList();
        if (Running) targets.Add(new IPEndPoint(IPAddress.Loopback, ShareSettings.Port));
        var results = new ConcurrentBag<LibraryItem>();
        int ok = 0;
        progress?.Invoke(targets.Count, 0);
        using var gate = new SemaphoreSlim(16);
        await Task.WhenAll(targets.Select(async ep =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var c = new TcpClient();
                var s = await Connect(ep, c, ct);
                if (s == null) return;
                using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                t.CancelAfter(TimeSpan.FromSeconds(6));
                await s.WriteAsync(Encoding.UTF8.GetBytes("MTS1 LIST\n"), t.Token);
                using var sr = new StreamReader(s, Encoding.UTF8);
                var json = await sr.ReadLineAsync(t.Token);
                if (json == null || json.Length > 2_000_000) return;
                if (JsonNode.Parse(json) is not JsonArray arr) return;
                foreach (var n in arr.OfType<JsonObject>().Take(200))
                {
                    string S(string k) => n[k]?.GetValue<string>() ?? "";
                    var risks = (n["risks"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList() ?? new List<string>();
                    int steps = n["steps"] is JsonValue sv && sv.TryGetValue<int>(out var si) ? si : 0;
                    results.Add(new LibraryItem(S("id"), S("name"), S("desc"), S("author"), steps, risks, S("version"), ep));
                }
                progress?.Invoke(targets.Count, Interlocked.Increment(ref ok));
            }
            catch { }
            finally { gate.Release(); }
        }));
        return results.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static async Task<byte[]?> DownloadAsync(LibraryItem item, CancellationToken ct)
    {
        using var c = new TcpClient();
        var s = await Connect(item.Peer, c, ct);
        if (s == null) return null;
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(TimeSpan.FromSeconds(60));
        await s.WriteAsync(Encoding.UTF8.GetBytes("MTS1 GET " + item.Id + "\n"), t.Token);
        var head = await ReadLine(s, t.Token);
        if (head == null || !head.StartsWith("OK ") || !int.TryParse(head[3..], out int len) || len <= 0 || len > 30 * 1024 * 1024) return null;
        var data = new byte[len];
        int got = 0;
        while (got < len)
        {
            int n = await s.ReadAsync(data.AsMemory(got, len - got), t.Token);
            if (n == 0) return null;
            got += n;
        }
        return data;
    }
}

// ---------------------------------------------------------------------
//  介面：腳本庫頁
// ---------------------------------------------------------------------
public sealed class LibraryPage : Panel
{
    private readonly CardStack _stack = new();
    private readonly TextBox _query = new();
    private readonly FancyButton _search;
    private readonly Label _status;
    private readonly ListPanel _list;
    private List<LibraryItem> _all = new();
    private CancellationTokenSource? _cts;

    public static Control Build() => new LibraryPage();

    private LibraryPage()
    {
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        _stack.Dock = DockStyle.Fill;

        var top = new BareRow { Padding = new Padding(0) };
        var qHost = new InputHost(_query, Ui.S(320));
        _query.PlaceholderText = Lang.T("搜尋腳本名 / 簡介 / 作者");
        _query.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Filter(); } };
        _query.TextChanged += (_, _) => Filter();
        _search = new FancyButton { Text = Lang.T("🔍 搜尋在線腳本"), Kind = BtnKind.Success, Font = Ui.F(9.75f), Size = new Size(Ui.S(150), Ui.S(34)) };
        _search.Click += async (_, _) => await SearchAsync();
        var settings = new FancyButton { Text = Lang.T("分享設定"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(100), Ui.S(34)) };
        settings.Click += (_, _) => { using var d = new ShareDialog(); d.ShowDialog(FindForm()); UpdateStatus(); };
        top.SetContent(new FlowRow(new Control[] { qHost, _search }, new Control[] { settings }));
        _stack.AddRow(top);

        var card = new CardPanel
        {
            Title = Lang.T("腳本庫"),
            Hint = Lang.T("顯示而家開住 MultiTool 嘅用戶公開咗嘅腳本，直接由佢哋部機下載。對方熄咗機或者連唔到就下載唔到。喺「我的腳本」右鍵腳本可以公開。")
        };
        _status = Ui.Text_("", Ui.TextDim, Ui.F(9.5f), new Padding(0));
        _list = new ListPanel(this);
        card.SetContent(new StatusList(_status, _list));
        _stack.AddRow(card);
        Controls.Add(_stack);

        ShareService.StatusChanged += OnShareStatus;
        UpdateStatus();
    }

    private void OnShareStatus() { try { if (IsHandleCreated) BeginInvoke(UpdateStatus); } catch { } }

    private void UpdateStatus()
    {
        if (_all.Count == 0)
            _status.Text = (ShareSettings.Enabled ? ShareService.Status : Lang.T("你未開啟分享（其他人睇唔到你公開嘅腳本，但你都可以搜尋同下載）")) +
                           "\n" + Lang.T("撳「搜尋在線腳本」開始搵。");
    }

    private async Task SearchAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _search.Enabled = false;
        _status.Text = Lang.T("喺 DHT 網絡搵緊在線用戶…（大約 10 秒）");
        try
        {
            _all = await ShareService.SearchAsync((peers, ok) => BeginInvoke(() =>
                _status.Text = Lang.F("搵到 {0} 個在線用戶，已連上 {1} 個…", peers, ok)), ct);
            Filter();
            _status.Text = _all.Count == 0
                ? Lang.T("搵唔到公開腳本（可能暫時冇人在線，或者連唔到佢哋）。")
                : Lang.F("搵到 {0} 個公開腳本", _all.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = Lang.T("搜尋失敗：") + ex.Message; }
        finally { if (!_search.IsDisposed) _search.Enabled = true; }
    }

    private void Filter()
    {
        string q = _query.Text.Trim();
        var shown = q.Length == 0 ? _all : _all.Where(x =>
            x.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || x.Desc.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
            x.Author.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _list.SetItems(shown);
        StepList.RelayoutFrom(_list);
    }

    internal async Task DownloadAsync(LibraryItem item, FancyButton btn)
    {
        btn.Enabled = false;
        btn.Text = Lang.T("下載中…");
        try
        {
            var data = await ShareService.DownloadAsync(item, CancellationToken.None);
            var pkg = data == null ? null : ScriptPackage.Read(data);
            if (pkg == null)
            {
                MessageBox.Show(FindForm(), Lang.T("下載唔到（對方可能已經熄機或者連唔到）。"), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var (sc, images) = pkg.Value;
            var risks = ScriptPackage.Risks(sc);
            string msg = Lang.F("加入「{0}」（作者：{1}）到我的腳本？", sc.Name, item.Author);
            if (risks.Count > 0)
                msg += "\n\n" + Lang.T("⚠ 呢個腳本會做以下動作，請確認你信得過作者：") + "\n" + string.Join("\n", risks.Take(15).Select(r => "• " + r));
            msg += "\n\n" + Lang.T("下載之後唔會自動開始。");
            if (MessageBox.Show(FindForm(), msg, Program.AppName, MessageBoxButtons.YesNo, risks.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes) return;
            ScriptPackage.Install(sc, images);
            btn.Text = Lang.T("已加入 ✓");
            return;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("下載腳本", ex);
            MessageBox.Show(FindForm(), Lang.T("下載失敗：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!btn.IsDisposed && btn.Text == Lang.T("下載中…")) { btn.Text = Lang.T("下載"); btn.Enabled = true; }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { ShareService.StatusChanged -= OnShareStatus; _cts?.Cancel(); }
        base.Dispose(disposing);
    }

    /// <summary>狀態字 + 清單。</summary>
    private sealed class StatusList : Panel
    {
        private readonly Label _s;
        private readonly ListPanel _l;
        public StatusList(Label s, ListPanel l)
        {
            _s = s; _l = l;
            BackColor = Color.Transparent;
            _s.AutoSize = true;
            Controls.Add(_s);
            Controls.Add(_l);
        }
        public override Size GetPreferredSize(Size p)
        {
            int w = p.Width > 0 ? p.Width : Width;
            return new Size(w, _s.PreferredSize.Height + Ui.S(10) + _l.GetPreferredSize(new Size(w, 0)).Height);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            _s.Location = Point.Empty;
            int y = _s.PreferredSize.Height + Ui.S(10);
            _l.SetBounds(0, y, ClientSize.Width, _l.GetPreferredSize(new Size(ClientSize.Width, 0)).Height);
        }
    }

    /// <summary>結果清單：每個腳本一行。</summary>
    private sealed class ListPanel : Panel
    {
        private readonly LibraryPage _page;
        private readonly List<Control[]> _rows = new();
        private int RowH => Ui.S(60);

        public ListPanel(LibraryPage page) { _page = page; BackColor = Color.Transparent; }

        public void SetItems(List<LibraryItem> items)
        {
            SuspendLayout();
            foreach (var r in _rows) foreach (var c in r) { Controls.Remove(c); c.Dispose(); }
            _rows.Clear();
            foreach (var it in items.Take(300))
            {
                var title = Ui.Text_(it.Name, Ui.Text, Ui.F(10.5f, FontStyle.Bold), new Padding(0));
                string info = Lang.F("作者：{0} · {1} 個步驟", it.Author, it.Steps) + (it.Desc.Length > 0 ? " · " + it.Desc : "");
                var sub = Ui.Text_(info, Ui.TextDim, Ui.F(9f), new Padding(0));
                sub.AutoSize = false;
                sub.AutoEllipsis = true;
                var warn = Ui.Text_(it.Risks.Count > 0 ? Lang.T("⚠ 含系統動作") : "", Ui.Danger, Ui.F(9f), new Padding(0));
                var dl = new FancyButton { Text = Lang.T("下載"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(90), Ui.S(34)) };
                var item = it;
                dl.Click += async (_, _) => await _page.DownloadAsync(item, dl);
                var row = new Control[] { title, sub, warn, dl };
                foreach (var c in row) Controls.Add(c);
                _rows.Add(row);
            }
            ResumeLayout(false);
            PerformLayout();
        }

        public override Size GetPreferredSize(Size p) => new(p.Width > 0 ? p.Width : Width, Math.Max(Ui.S(10), _rows.Count * (RowH + Ui.S(6))));

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int y = 0, w = ClientSize.Width;
            foreach (var r in _rows)
            {
                var (title, sub, warn, dl) = (r[0], r[1], r[2], r[3]);
                dl.Location = new Point(w - dl.Width, y + (RowH - dl.Height) / 2);
                title.Location = new Point(Ui.S(4), y + Ui.S(8));
                warn.Location = new Point(title.Right + Ui.S(12), y + Ui.S(10));
                sub.SetBounds(Ui.S(4), y + Ui.S(34), Math.Max(50, dl.Left - Ui.S(16)), Ui.S(20));
                y += RowH + Ui.S(6);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Ui.Border, 1f);
            int y = RowH + Ui.S(2);
            for (int i = 0; i < _rows.Count - 1; i++) { e.Graphics.DrawLine(pen, 0, y, Width, y); y += RowH + Ui.S(6); }
        }
    }
}

/// <summary>分享設定：開關、顯示名、port，同私隱提醒。</summary>
internal sealed class ShareDialog : Form
{
    public ShareDialog()
    {
        Text = Lang.T("分享設定");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(9.75f);
        ClientSize = new Size(Ui.S(480), Ui.S(330));

        var enable = new CheckBox { Text = Lang.T("開啟分享（其他人可以睇到同下載我公開咗嘅腳本）"), Checked = ShareSettings.Enabled, AutoSize = true, ForeColor = Ui.Text };
        enable.Location = new Point(Ui.S(20), Ui.S(20));
        var nameL = new Label { Text = Lang.T("顯示名稱"), AutoSize = true, ForeColor = Ui.TextDim, Location = new Point(Ui.S(20), Ui.S(64)) };
        var name = new TextBox { Text = ShareSettings.Name, MaxLength = 30, Location = new Point(Ui.S(130), Ui.S(60)), Width = Ui.S(220), BackColor = Ui.CardHi, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle };
        name.PlaceholderText = Lang.T("匿名");
        var portL = new Label { Text = Lang.T("Port"), AutoSize = true, ForeColor = Ui.TextDim, Location = new Point(Ui.S(20), Ui.S(102)) };
        var port = new NumericUpDown { Minimum = 1025, Maximum = 65535, Value = ShareSettings.Port, Location = new Point(Ui.S(130), Ui.S(98)), Width = Ui.S(100), BackColor = Ui.CardHi, ForeColor = Ui.Text };
        var warn = new Label
        {
            Text = Lang.T("注意：開咗分享之後，你部電腦嘅 IP 地址會俾其他 MultiTool 用戶同 DHT 網絡見到。") + "\n" +
                   Lang.T("只有你喺「我的腳本」右鍵揀咗「公開到腳本庫」嘅腳本先會被分享；視窗、快捷鍵同你嘅用戶資料夾路徑唔會帶出去。") + "\n" +
                   Lang.T("會嘗試用 UPnP 叫路由器開 port；開唔到嘅話其他人可能連唔到你。"),
            ForeColor = Ui.TextDim, AutoSize = false, Location = new Point(Ui.S(20), Ui.S(140)), Size = new Size(Ui.S(440), Ui.S(120))
        };
        var ok = new FancyButton { Text = Lang.T("儲存"), Kind = BtnKind.Success, Size = new Size(Ui.S(100), Ui.S(34)), Location = new Point(Ui.S(360), Ui.S(280)) };
        ok.Click += (_, _) =>
        {
            ShareSettings.Enabled = enable.Checked;
            ShareSettings.Name = name.Text.Trim();
            ShareSettings.Port = (int)port.Value;
            ShareSettings.Save();
            ShareService.Start();
            Close();
        };
        Controls.AddRange(new Control[] { enable, nameL, name, portL, port, warn, ok });
    }
}

/// <summary>公開設定：簡介 + 公開開關。</summary>
internal sealed class PublishDialog : Form
{
    public PublishDialog(Script sc)
    {
        Text = Lang.T("公開到腳本庫");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(9.75f);
        ClientSize = new Size(Ui.S(460), Ui.S(250));
        var pub = new CheckBox { Text = Lang.F("公開「{0}」", sc.Name), Checked = sc.Public, AutoSize = true, ForeColor = Ui.Text, Location = new Point(Ui.S(20), Ui.S(20)) };
        var dL = new Label { Text = Lang.T("簡介（例如：用喺咩遊戲、做咩）"), AutoSize = true, ForeColor = Ui.TextDim, Location = new Point(Ui.S(20), Ui.S(60)) };
        var desc = new TextBox { Text = sc.Desc, MaxLength = 120, Location = new Point(Ui.S(20), Ui.S(86)), Width = Ui.S(420), BackColor = Ui.CardHi, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle };
        var risks = ScriptPackage.Risks(sc);
        var note = new Label
        {
            Text = (ShareSettings.Enabled ? "" : Lang.T("⚠ 你未開啟分享：要喺「腳本庫 → 分享設定」開咗，其他人先睇到。") + "\n") +
                   (risks.Count > 0 ? Lang.T("呢個腳本有系統動作 / 輸入文字，下載嘅人會見到提醒。") : ""),
            ForeColor = Ui.TextDim, AutoSize = false, Location = new Point(Ui.S(20), Ui.S(124)), Size = new Size(Ui.S(420), Ui.S(70))
        };
        var ok = new FancyButton { Text = Lang.T("儲存"), Kind = BtnKind.Success, Size = new Size(Ui.S(100), Ui.S(34)), Location = new Point(Ui.S(340), Ui.S(200)) };
        ok.Click += (_, _) =>
        {
            sc.Public = pub.Checked;
            sc.Desc = desc.Text.Trim();
            sc.Save();
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.AddRange(new Control[] { pub, dL, desc, note, ok });
    }
}
