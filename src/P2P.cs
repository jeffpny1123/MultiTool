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
//  網絡小工具：BitTorrent DHT（遠端桌面搵對方用）、UPnP 開 port（遊戲伺服器 / 遠端桌面用）
//  + 腳本包（.mtscript）打包 / 讀取。
//  舊嘅 P2P 腳本庫分享已經拎走（原本嘅 code 喺 archive\builtin\P2P.cs），將來改用 Steam 工作坊。
// =====================================================================

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

    public static Task<bool> MapAsync(int port, CancellationToken ct) => MapAsync(port, "TCP", Program.FileName, ct);

    /// <summary>
    /// 喺路由器開端口（TCP / UDP）。lease = 幾多秒後路由器自動閂（0 = 永久）；
    /// 有啲路由器唔支援限時，失敗就用返永久。
    /// </summary>
    public static async Task<bool> MapAsync(int port, string proto, string desc, CancellationToken ct, int lease = 0)
    {
        try
        {
            _gw ??= await DiscoverAsync(ct);
            if (_gw is not { } g) return false;
            desc = System.Security.SecurityElement.Escape(desc) ?? Program.FileName;
            string Body(int l) => $"<u:AddPortMapping xmlns:u=\"{g.Service}\"><NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort>" +
                          $"<NewProtocol>{proto}</NewProtocol><NewInternalPort>{port}</NewInternalPort><NewInternalClient>{g.Local}</NewInternalClient>" +
                          $"<NewEnabled>1</NewEnabled><NewPortMappingDescription>{desc}</NewPortMappingDescription><NewLeaseDuration>{l}</NewLeaseDuration></u:AddPortMapping>";
            if (await SoapAsync(g.Control, g.Service, "AddPortMapping", Body(lease), ct)) return true;
            return lease > 0 && await SoapAsync(g.Control, g.Service, "AddPortMapping", Body(0), ct);
        }
        catch { return false; }
    }

    /// <summary>閂端口（未搵過路由器都會先搵）。</summary>
    public static async Task UnmapAsync(int port, string proto, CancellationToken ct)
    {
        try
        {
            _gw ??= await DiscoverAsync(ct);
            Unmap(port, proto);
        }
        catch { }
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
