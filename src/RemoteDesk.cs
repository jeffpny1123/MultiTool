using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Tools;

// =====================================================================
//  遠端桌面（MultiTool 自己做，唔使另外裝程式）
//  • 搵對方：同一個網絡用廣播；外面用 BitTorrent DHT 用 ID 搵返 IP（同腳本庫一樣，冇自己伺服器）
//  • 連線：直接 TCP（UPnP 自動開路由器端口）；全程 ECDH + AES-GCM 加密，密碼用 PBKDF2 + HMAC 驗證
//  • 畫面：分格比較，有變嘅格先用 JPEG 傳；輸入：滑鼠、鍵盤、剪貼簿
//  限制：兩邊都喺 CGNAT / 嚴格 NAT 後面就連唔到（冇中轉伺服器）；控制唔到管理員權限視窗（UAC）
// =====================================================================

/// <summary>設定（data\remote.txt）。</summary>
internal static class RemoteSettings
{
    private static string File_ => Path.Combine(AppPaths.Data, "remote.txt");
    public static string Id = "";
    public static int Port = 47820;
    public static bool AutoHost;
    public static byte[]? FixedSalt, FixedKey; // 固定密碼：只存 PBKDF2 結果，唔存密碼本身
    public static string OneTime = "";

    static RemoteSettings()
    {
        try
        {
            if (File.Exists(File_))
                foreach (var l in File.ReadAllLines(File_))
                {
                    int eq = l.IndexOf('=');
                    if (eq < 0) continue;
                    string k = l[..eq], v = l[(eq + 1)..];
                    switch (k)
                    {
                        case "id": Id = v; break;
                        case "port": if (int.TryParse(v, out int p) && p is > 1024 and < 65536) Port = p; break;
                        case "auto": AutoHost = v == "1"; break;
                        case "fsalt": FixedSalt = v.Length > 0 ? Convert.FromBase64String(v) : null; break;
                        case "fkey": FixedKey = v.Length > 0 ? Convert.FromBase64String(v) : null; break;
                    }
                }
        }
        catch { }
        if (Id.Length != 9 || !Id.All(char.IsDigit))
        {
            Id = (RandomNumberGenerator.GetInt32(100_000_000, 999_999_999)).ToString();
            Save();
        }
        NewOneTime();
    }

    public static void NewOneTime()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        OneTime = new string(Enumerable.Range(0, 10).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
    }

    /// <summary>固定密碼太弱就回傳原因；夠強回傳 null。</summary>
    public static string? Weak(string p)
    {
        if (p.Length < 12) return Lang.T("固定密碼最少要 12 個字。");
        if (!p.Any(char.IsLetter) || !p.Any(char.IsDigit)) return Lang.T("固定密碼要同時有英文字母同數字。");
        if (p.Distinct().Count() < 6) return Lang.T("固定密碼太簡單（重複字太多）。");
        string low = p.ToLowerInvariant();
        foreach (var bad in new[] { "password", "12345678", "qwerty", "abcdefg", "11111111", "multitool" })
            if (low.Contains(bad)) return Lang.T("固定密碼太常見，請換一個。");
        return null;
    }

    public static void SetFixed(string? password)
    {
        if (string.IsNullOrEmpty(password)) { FixedSalt = null; FixedKey = null; }
        else
        {
            FixedSalt = RandomNumberGenerator.GetBytes(16);
            FixedKey = RemoteCrypto.PwKey(password, FixedSalt);
        }
        Save();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllLines(File_, new[]
            {
                "id=" + Id, "port=" + Port, "auto=" + (AutoHost ? 1 : 0),
                "fsalt=" + (FixedSalt != null ? Convert.ToBase64String(FixedSalt) : ""),
                "fkey=" + (FixedKey != null ? Convert.ToBase64String(FixedKey) : "")
            });
        }
        catch { }
    }

    public static string Pretty(string id) => id.Length == 9 ? $"{id[..3]} {id[3..6]} {id[6..]}" : id;

    /// <summary>DHT 用嘅 key（由 ID 計）。</summary>
    public static byte[] DhtKey(string id) => SHA1.HashData(Encoding.UTF8.GetBytes("MultiTool-Remote-v1-" + id));
}

internal static class RemoteCrypto
{
    public static byte[] PwKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 120_000, HashAlgorithmName.SHA256, 32);

    public static byte[] Mac(byte[] key, string label, byte[] transcript, byte[] shared) =>
        HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(label).Concat(transcript).Concat(shared).ToArray());
}

/// <summary>加密嘅訊息通道：每個訊息 = [長度 4][AES-GCM(類型 1 + 內容) + tag 16]。</summary>
internal sealed class SecureChannel : IDisposable
{
    private readonly NetworkStream _s;
    private AesGcm? _tx, _rx;
    private ulong _txN, _rxN;
    private readonly SemaphoreSlim _wlock = new(1, 1);

    public SecureChannel(NetworkStream s) { _s = s; }

    public void SetKeys(byte[] tx, byte[] rx)
    {
        _tx = new AesGcm(tx, 16);
        _rx = new AesGcm(rx, 16);
    }

    // ---- 明文（握手用）----
    public async Task WriteRawAsync(byte[] data, CancellationToken ct)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        await _s.WriteAsync(len, ct);
        await _s.WriteAsync(data, ct);
    }

    public async Task<byte[]> ReadRawAsync(CancellationToken ct, int max = 4096)
    {
        var len = new byte[4];
        await _s.ReadExactlyAsync(len, ct);
        int n = BinaryPrimitives.ReadInt32BigEndian(len);
        if (n < 0 || n > max) throw new InvalidDataException("bad frame");
        var b = new byte[n];
        await _s.ReadExactlyAsync(b, ct);
        return b;
    }

    private static byte[] Nonce(ulong n)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(4), n);
        return b;
    }

    public async Task SendAsync(byte type, byte[] payload, CancellationToken ct)
    {
        await _wlock.WaitAsync(ct);
        try
        {
            var plain = new byte[1 + payload.Length];
            plain[0] = type;
            payload.CopyTo(plain, 1);
            var cipher = new byte[plain.Length + 16];
            _tx!.Encrypt(Nonce(_txN++), plain, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length));
            await WriteRawAsync(cipher, ct);
        }
        finally { _wlock.Release(); }
    }

    public async Task<(byte Type, byte[] Data)> ReceiveAsync(CancellationToken ct)
    {
        var cipher = await ReadRawAsync(ct, 16 * 1024 * 1024);
        if (cipher.Length < 17) throw new InvalidDataException("short");
        var plain = new byte[cipher.Length - 16];
        _rx!.Decrypt(Nonce(_rxN++), cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), plain);
        return (plain[0], plain[1..]);
    }

    public void Dispose()
    {
        _tx?.Dispose();
        _rx?.Dispose();
        try { _s.Dispose(); } catch { }
    }
}

/// <summary>訊息類型。</summary>
internal static class RMsg
{
    // 被控 → 控制
    public const byte Screens = 0x10, FrameBegin = 0x11, Tile = 0x12, FrameEnd = 0x13, Clip = 0x14, Note = 0x15;
    // 控制 → 被控
    public const byte Select = 0x20, MouseMove = 0x21, MouseBtn = 0x22, Wheel = 0x23, Key = 0x24, ClipSet = 0x25, Ack = 0x26, ClipGet = 0x27;
}

/// <summary>握手（兩邊共用）。</summary>
internal static class RemoteHandshake
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MTRD1");

    /// <summary>控制方：連去對方，用密碼驗證。成功就回傳加密通道同對方嘅 ID。</summary>
    public static async Task<(SecureChannel Ch, string HostId)> ClientAsync(TcpClient c, string expectId, string password, string myName, CancellationToken ct)
    {
        var ch = new SecureChannel(c.GetStream());
        try
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            byte[] myPub = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
            await ch.WriteRawAsync(Magic.Concat(myPub).ToArray(), ct);
            await ch.WriteRawAsync(Encoding.UTF8.GetBytes(myName), ct);
            var hello = await ch.ReadRawAsync(ct);
            if (!hello.AsSpan(0, 5).SequenceEqual(Magic)) throw new InvalidDataException(Lang.T("對方唔係 MultiTool 遠端桌面"));
            string hostId = Encoding.ASCII.GetString(hello, 5, 9);
            if (expectId.Length > 0 && hostId != expectId) throw new InvalidDataException(Lang.T("ID 唔啱"));
            var sessionSalt = hello.AsSpan(14, 16).ToArray();
            var fixedSalt = hello.AsSpan(30, 16).ToArray();
            var hostPub = hello[46..];
            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(hostPub, out _);
            byte[] shared = ecdh.DeriveRawSecretAgreement(peer.PublicKey);
            byte[] transcript = SHA256.HashData(myPub.Concat(hostPub).Concat(sessionSalt).Concat(fixedSalt).ToArray());
            // 兩個都試：一次性密碼（session salt）同固定密碼（fixed salt）
            var k1 = await Task.Run(() => RemoteCrypto.PwKey(password, sessionSalt), ct);
            var k2 = await Task.Run(() => RemoteCrypto.PwKey(password, fixedSalt), ct);
            await ch.WriteRawAsync(RemoteCrypto.Mac(k1, "view", transcript, shared).Concat(RemoteCrypto.Mac(k2, "view", transcript, shared)).ToArray(), ct);
            var reply = await ch.ReadRawAsync(ct);
            if (reply.Length == 1) throw new UnauthorizedAccessException(reply[0] switch
            {
                1 => Lang.T("密碼錯誤"), 2 => Lang.T("對方拒絕咗連線"), 3 => Lang.T("試錯太多次，對方已經鎖咗 30 分鐘"), _ => Lang.T("對方拒絕")
            });
            byte[]? key = null;
            if (reply.AsSpan().SequenceEqual(RemoteCrypto.Mac(k1, "host", transcript, shared))) key = k1;
            else if (reply.AsSpan().SequenceEqual(RemoteCrypto.Mac(k2, "host", transcript, shared))) key = k2;
            if (key == null) throw new UnauthorizedAccessException(Lang.T("對方身份驗證失敗"));
            var (v2h, h2v) = Keys(shared, transcript, key);
            ch.SetKeys(v2h, h2v);
            return (ch, hostId);
        }
        catch { ch.Dispose(); throw; }
    }

    public static (byte[] V2H, byte[] H2V) Keys(byte[] shared, byte[] transcript, byte[] pwKey)
    {
        var info = transcript.Concat(pwKey).ToArray();
        return (HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, null, Encoding.ASCII.GetBytes("v2h").Concat(info).ToArray()),
                HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, null, Encoding.ASCII.GetBytes("h2v").Concat(info).ToArray()));
    }

    public static byte[] Magic_ => Magic;
}

// =====================================================================
//  被控方（呢部機）
// =====================================================================
internal static class RemoteHost
{
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }

    public static bool Running => _listener != null;
    public static string Status = "";
    public static event Action? Changed;
    public static readonly List<string> History = new();
    public static readonly List<string> Active = new();
    private static TcpListener? _listener;
    private static UdpClient? _lan;
    private static CancellationTokenSource? _cts;
    private static bool _mapped;
    private static readonly List<DateTime> Fails = new();
    private const int MaxFails = 5;                          // 連續錯 5 次 …
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(30); // … 鎖 30 分鐘
    private static DateTime _lockedUntil;
    public static DateTime LockedUntil => _lockedUntil;
    private static Control? _ui;
    private static readonly List<(CancellationTokenSource Cts, string Who)> Sessions = new();

    private static void Set(string s) { Status = s; Changed?.Invoke(); }

    public static void Init(Control ui) => _ui ??= ui;

    public static void Start()
    {
        if (Running) return;
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, RemoteSettings.Port);
            _listener.Start();
            try
            {
                _lan = new UdpClient(new IPEndPoint(IPAddress.Any, RemoteSettings.Port + 1)) { EnableBroadcast = true };
                _ = Task.Run(() => LanLoop(_lan, _cts.Token));
            }
            catch { }
            _ = Task.Run(() => AcceptLoop(_listener, _cts.Token));
            _ = Task.Run(() => AnnounceLoop(_cts.Token));
            Set(Lang.T("等緊連線…"));
        }
        catch (Exception ex)
        {
            _listener = null;
            Set(Lang.T("開唔到：") + ex.Message);
        }
    }

    public static void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _lan?.Dispose(); } catch { }
        if (_mapped) Scripts.Upnp.Unmap(RemoteSettings.Port);
        _mapped = false;
        _listener = null;
        _lan = null;
        lock (Sessions) foreach (var s in Sessions) s.Cts.Cancel();
        Set(Lang.T("已關閉"));
    }

    public static void DisconnectAll()
    {
        lock (Sessions) foreach (var s in Sessions) s.Cts.Cancel();
    }

    private static async Task LanLoop(UdpClient u, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await u.ReceiveAsync(ct);
                var msg = Encoding.ASCII.GetString(r.Buffer);
                if (msg == "MTRD?" + RemoteSettings.Id)
                {
                    var reply = Encoding.ASCII.GetBytes($"MTRD!{RemoteSettings.Id}:{RemoteSettings.Port}");
                    await u.SendAsync(reply, reply.Length, r.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch { }
        }
    }

    private static async Task AnnounceLoop(CancellationToken ct)
    {
        _mapped = await Scripts.Upnp.MapAsync(RemoteSettings.Port, "TCP", "MultiTool Remote", ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var dht = new Scripts.DhtClient();
                await dht.LookupAsync(RemoteSettings.Port, TimeSpan.FromSeconds(12), ct, RemoteSettings.DhtKey(RemoteSettings.Id));
                if (Active.Count == 0)
                    Set(_mapped ? Lang.T("等緊連線（路由器已自動開端口，外面都連到）")
                                : Lang.T("等緊連線（路由器未能自動開端口：同一網絡連到；外面可能連唔到）"));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AppPaths.LogError("遠端桌面 DHT", ex); }
            try { await Task.Delay(TimeSpan.FromMinutes(10), ct); } catch { return; }
        }
    }

    private static async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(c, ct));
        }
    }

    /// <summary>問用戶允唔允許（一次性密碼先問；固定密碼唔問，方便無人看管）。</summary>
    private static Task<bool> AskAsync(string who)
    {
        var tcs = new TaskCompletionSource<bool>();
        var ui = _ui;
        if (ui == null || ui.IsDisposed) { tcs.SetResult(false); return tcs.Task; }
        ui.BeginInvoke(() =>
        {
            using var dlg = new AcceptDialog(who);
            tcs.TrySetResult(dlg.ShowDialog() == DialogResult.OK);
        });
        return tcs.Task;
    }

    private static async Task HandleAsync(TcpClient c, CancellationToken stop)
    {
        string ip = (c.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        c.NoDelay = true;
        var ch = new SecureChannel(c.GetStream());
        string who = ip;
        try
        {
            using var hs = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var link = CancellationTokenSource.CreateLinkedTokenSource(hs.Token, stop);
            var ct = link.Token;
            var first = await ch.ReadRawAsync(ct);
            if (first.Length < 6 || !first.AsSpan(0, 5).SequenceEqual(RemoteHandshake.Magic_)) return;
            var viewerPub = first[5..];
            var name = Encoding.UTF8.GetString(await ch.ReadRawAsync(ct, 200));
            who = (name.Length > 40 ? name[..40] : name) + " (" + ip + ")";
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var myPub = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
            var sessionSalt = RandomNumberGenerator.GetBytes(16);
            var fixedSalt = RemoteSettings.FixedSalt ?? RandomNumberGenerator.GetBytes(16);
            await ch.WriteRawAsync(RemoteHandshake.Magic_.Concat(Encoding.ASCII.GetBytes(RemoteSettings.Id))
                .Concat(sessionSalt).Concat(fixedSalt).Concat(myPub).ToArray(), ct);
            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(viewerPub, out _);
            var shared = ecdh.DeriveRawSecretAgreement(peer.PublicKey);
            var transcript = SHA256.HashData(viewerPub.Concat(myPub).Concat(sessionSalt).Concat(fixedSalt).ToArray());
            var auth = await ch.ReadRawAsync(ct, 128);
            if (auth.Length != 64) return;

            lock (Fails)
            {
                if (DateTime.Now < _lockedUntil) { _ = ch.WriteRawAsync(new byte[] { 3 }, ct); return; }
            }
            var k1 = await Task.Run(() => RemoteCrypto.PwKey(RemoteSettings.OneTime, sessionSalt), ct);
            byte[]? key = null;
            bool viaFixed = false;
            if (CryptographicOperations.FixedTimeEquals(auth.AsSpan(0, 32), RemoteCrypto.Mac(k1, "view", transcript, shared))) key = k1;
            else if (RemoteSettings.FixedKey != null &&
                     CryptographicOperations.FixedTimeEquals(auth.AsSpan(32, 32), RemoteCrypto.Mac(RemoteSettings.FixedKey, "view", transcript, shared)))
            { key = RemoteSettings.FixedKey; viaFixed = true; }
            if (key == null)
            {
                bool locked = false;
                lock (Fails)
                {
                    Fails.RemoveAll(t => (DateTime.Now - t) > LockFor);
                    Fails.Add(DateTime.Now);
                    if (Fails.Count >= MaxFails) { _lockedUntil = DateTime.Now + LockFor; Fails.Clear(); locked = true; }
                }
                Log(Lang.F("密碼錯誤：{0}", who));
                if (locked)
                {
                    Log(Lang.F("密碼錯咗 {0} 次，鎖 {1} 分鐘", MaxFails, (int)LockFor.TotalMinutes));
                    Scripts.Notifier.Send(Lang.T("遠端桌面：有人試錯密碼"),
                        Lang.F("{0} 試錯密碼 {1} 次，已經鎖咗 {2} 分鐘。唔係你嘅話，建議換固定密碼。", who, MaxFails, (int)LockFor.TotalMinutes), true, false);
                }
                await ch.WriteRawAsync(new byte[] { 1 }, ct);
                return;
            }
            if (!viaFixed && !await AskAsync(who))
            {
                Log(Lang.F("拒絕咗：{0}", who));
                await ch.WriteRawAsync(new byte[] { 2 }, ct);
                return;
            }
            await ch.WriteRawAsync(RemoteCrypto.Mac(key, "host", transcript, shared), ct);
            var (v2h, h2v) = RemoteHandshake.Keys(shared, transcript, key);
            ch.SetKeys(h2v, v2h);
            if (!viaFixed) { RemoteSettings.NewOneTime(); Changed?.Invoke(); } // 一次性密碼用完就換

            using var sess = CancellationTokenSource.CreateLinkedTokenSource(stop);
            lock (Sessions) Sessions.Add((sess, who));
            lock (Active) Active.Add(who);
            lock (Fails) Fails.Clear(); // 成功登入：錯誤次數重新計
            Log(Lang.F("已連線：{0}", who));
            Scripts.Notifier.Send(Lang.T("遠端桌面：有人連咗入嚟"), Lang.F("{0} 正在遠端控制呢部電腦。唔係你允許嘅話，即刻撳畫面頂嘅「中斷」。", who), true, false);
            Set(Lang.F("{0} 正在遠端控制呢部電腦", who));
            ShowBar(who, () => sess.Cancel());
            try { await new HostSession(ch).RunAsync(sess.Token); }
            finally
            {
                sess.Cancel(); // 停埋傳畫面嘅迴圈
                lock (Sessions) Sessions.RemoveAll(s => s.Cts == sess);
                lock (Active) Active.Remove(who);
                Log(Lang.F("已中斷：{0}", who));
                Scripts.Notifier.Send(Lang.T("遠端桌面：已中斷"), Lang.F("{0} 已經中斷連線。", who), true, false);
                Set(Running ? Lang.T("等緊連線…") : Lang.T("已關閉"));
                HideBar();
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException or SocketException) { }
        catch (Exception ex) { AppPaths.LogError("遠端桌面", ex); }
        finally { ch.Dispose(); try { c.Dispose(); } catch { } }
    }

    private static void Log(string s)
    {
        lock (History)
        {
            History.Insert(0, $"{DateTime.Now:MM-dd HH:mm}  {s}");
            if (History.Count > 30) History.RemoveAt(History.Count - 1);
        }
        Changed?.Invoke();
    }

    // ---- 「有人控制緊」提示條 ----
    private static ControlBar? _bar;

    private static readonly List<RemoteFrame> _frames = new();

    private static void ShowBar(string who, Action disconnect)
    {
        _ui?.BeginInvoke(() =>
        {
            _bar?.Close();
            _bar = new ControlBar(who, disconnect);
            _bar.Show();
            // 成個螢幕加一圈紅框：一眼就知有人連緊
            foreach (var f in _frames) f.Close();
            _frames.Clear();
            foreach (var scr in Screen.AllScreens) { var fr = new RemoteFrame(scr.Bounds); _frames.Add(fr); fr.Show(); }
        });
    }

    private static void HideBar()
    {
        _ui?.BeginInvoke(() =>
        {
            if (Active.Count == 0)
            {
                _bar?.Close(); _bar = null;
                foreach (var f in _frames) f.Close();
                _frames.Clear();
            }
        });
    }

    // ---- 輸入 ----
    public static void MouseAbs(Rectangle mon, float nx, float ny)
    {
        var vs = SystemInformation.VirtualScreen;
        double x = mon.X + Math.Clamp(nx, 0, 1) * (mon.Width - 1), y = mon.Y + Math.Clamp(ny, 0, 1) * (mon.Height - 1);
        int ax = (int)((x - vs.X) * 65535.0 / Math.Max(1, vs.Width - 1)), ay = (int)((y - vs.Y) * 65535.0 / Math.Max(1, vs.Height - 1));
        SendMouse(0x0001 | 0x8000 | 0x4000, ax, ay, 0);
    }

    public static void MouseButton(int btn, bool down)
    {
        uint f = btn switch { 1 => down ? 0x0008u : 0x0010u, 2 => down ? 0x0020u : 0x0040u, _ => down ? 0x0002u : 0x0004u };
        SendMouse(f, 0, 0, 0);
    }

    public static void Wheel(int delta) => SendMouse(0x0800, 0, 0, (uint)delta);

    private static void SendMouse(uint flags, int dx, int dy, uint data)
    {
        var inp = new[] { new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, mouseData = data } } } };
        SendInput(1, inp, Marshal.SizeOf<INPUT>());
    }

    public static void Key(int vk, bool down, bool ext)
    {
        uint scan = MapVirtualKey((uint)vk, 0);
        uint flags = (down ? 0u : 0x2u) | (ext ? 0x1u : 0u);
        var inp = new[] { new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)scan, dwFlags = flags } } } };
        SendInput(1, inp, Marshal.SizeOf<INPUT>());
    }

    public static void OnUi(Action a) => _ui?.BeginInvoke(a);

    public static T OnUi<T>(Func<T> f)
    {
        var ui = _ui;
        if (ui == null || ui.IsDisposed) return default!;
        return (T)ui.Invoke(f)!;
    }
}

/// <summary>一次被控連線：傳畫面、收輸入。</summary>
internal sealed class HostSession
{
    private readonly SecureChannel _ch;
    private int _mon, _quality = 1;
    private volatile bool _full = true;
    private int _inflight; // 未確認收到嘅畫面數（最多 2 個，慢網都唔會塞車）
    private Bitmap? _prev;
    private Rectangle _monRect;
    private const int TileSize = 128;

    public HostSession(SecureChannel ch) { _ch = ch; }

    public async Task RunAsync(CancellationToken ct)
    {
        await SendScreens(ct);
        var send = Task.Run(() => SendLoop(ct), ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (t, d) = await _ch.ReceiveAsync(ct);
                Handle(t, d, ct);
            }
        }
        finally { _prev?.Dispose(); }
        await Task.WhenAny(send, Task.Delay(500));
    }

    private async Task SendScreens(CancellationToken ct)
    {
        var screens = Screen.AllScreens;
        var b = new byte[4 + screens.Length * 17];
        BinaryPrimitives.WriteInt32LittleEndian(b, screens.Length);
        for (int i = 0; i < screens.Length; i++)
        {
            var r = screens[i].Bounds;
            int o = 4 + i * 17;
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), r.X);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 4), r.Y);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 8), r.Width);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 12), r.Height);
            b[o + 16] = (byte)(screens[i].Primary ? 1 : 0);
        }
        await _ch.SendAsync(RMsg.Screens, b, ct);
    }

    private void Handle(byte t, byte[] d, CancellationToken ct)
    {
        switch (t)
        {
            case RMsg.Select:
                _mon = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(d), 0, Screen.AllScreens.Length - 1);
                _quality = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4)), 0, 2);
                _full = true;
                break;
            case RMsg.Ack: if (Interlocked.Decrement(ref _inflight) < 0) Interlocked.Exchange(ref _inflight, 0); break;
            case RMsg.MouseMove:
                RemoteHost.MouseAbs(_monRect, BitConverter.ToSingle(d, 0), BitConverter.ToSingle(d, 4));
                break;
            case RMsg.MouseBtn: RemoteHost.MouseButton(d[0], d[1] == 1); break;
            case RMsg.Wheel: RemoteHost.Wheel(BinaryPrimitives.ReadInt32LittleEndian(d)); break;
            case RMsg.Key: RemoteHost.Key(BinaryPrimitives.ReadInt16LittleEndian(d), d[2] == 1, d[3] == 1); break;
            case RMsg.ClipSet:
                var text = Encoding.UTF8.GetString(d);
                RemoteHost.OnUi(() => { try { Clipboard.SetText(text); } catch { } });
                break;
            case RMsg.ClipGet:
                string clip = RemoteHost.OnUi(() => { try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { return ""; } }) ?? "";
                _ = _ch.SendAsync(RMsg.Clip, Encoding.UTF8.GetBytes(clip), ct);
                break;
        }
    }

    private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);

    private async Task SendLoop(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            if (Volatile.Read(ref _inflight) >= 2) { await Task.Delay(5, ct); continue; }
            var wait = 60 - (int)sw.ElapsedMilliseconds; // 最多大約 16 fps
            if (wait > 0) await Task.Delay(wait, ct);
            sw.Restart();
            var screens = Screen.AllScreens;
            _monRect = screens[Math.Clamp(_mon, 0, screens.Length - 1)].Bounds;
            float scale = _quality == 0 ? 0.5f : 1f;
            long q = _quality switch { 0 => 45, 2 => 85, _ => 65 };
            int w = Math.Max(16, (int)(_monRect.Width * scale)), h = Math.Max(16, (int)(_monRect.Height * scale));
            Bitmap cur;
            try
            {
                using var raw = new Bitmap(_monRect.Width, _monRect.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(raw)) g.CopyFromScreen(_monRect.Location, Point.Empty, _monRect.Size);
                cur = scale == 1f ? (Bitmap)raw.Clone() : new Bitmap(raw, w, h);
            }
            catch { await Task.Delay(300, ct); continue; } // 例如鎖咗機 / UAC 畫面
            bool full = _full || _prev == null || _prev.Width != w || _prev.Height != h;
            _full = false;
            var tiles = Changed(cur, full ? null : _prev);
            _prev?.Dispose();
            _prev = cur;
            if (tiles.Count == 0) continue;
            var begin = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(begin, w);
            BinaryPrimitives.WriteInt32LittleEndian(begin.AsSpan(4), h);
            BinaryPrimitives.WriteInt32LittleEndian(begin.AsSpan(8), tiles.Count);
            Interlocked.Increment(ref _inflight);
            await _ch.SendAsync(RMsg.FrameBegin, begin, ct);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
            foreach (var r in tiles)
            {
                using var tile = cur.Clone(r, PixelFormat.Format24bppRgb);
                using var ms = new MemoryStream();
                ms.Write(new byte[8]);
                tile.Save(ms, Jpeg, ep);
                var buf = ms.ToArray();
                BinaryPrimitives.WriteInt16LittleEndian(buf, (short)r.X);
                BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(2), (short)r.Y);
                BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(4), (short)r.Width);
                BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(6), (short)r.Height);
                await _ch.SendAsync(RMsg.Tile, buf, ct);
            }
            await _ch.SendAsync(RMsg.FrameEnd, Array.Empty<byte>(), ct);
        }
    }

    /// <summary>同上一格比較，搵出有變嘅格。</summary>
    private static List<Rectangle> Changed(Bitmap cur, Bitmap? prev)
    {
        var list = new List<Rectangle>();
        int w = cur.Width, h = cur.Height;
        if (prev == null)
        {
            for (int y = 0; y < h; y += TileSize)
                for (int x = 0; x < w; x += TileSize)
                    list.Add(new Rectangle(x, y, Math.Min(TileSize, w - x), Math.Min(TileSize, h - y)));
            return list;
        }
        var a = cur.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var b = prev.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int ty = 0; ty < h; ty += TileSize)
                    for (int tx = 0; tx < w; tx += TileSize)
                    {
                        int tw = Math.Min(TileSize, w - tx), th = Math.Min(TileSize, h - ty);
                        bool diff = false;
                        for (int y = ty; y < ty + th && !diff; y++)
                        {
                            var ra = new ReadOnlySpan<byte>((byte*)a.Scan0 + y * a.Stride + tx * 4, tw * 4);
                            var rb = new ReadOnlySpan<byte>((byte*)b.Scan0 + y * b.Stride + tx * 4, tw * 4);
                            if (!ra.SequenceEqual(rb)) diff = true;
                        }
                        if (diff) list.Add(new Rectangle(tx, ty, tw, th));
                    }
            }
        }
        finally { cur.UnlockBits(a); prev.UnlockBits(b); }
        return list;
    }
}

/// <summary>「允許連線？」視窗（30 秒冇反應就拒絕）。</summary>
internal sealed class AcceptDialog : Form
{
    public AcceptDialog(string who)
    {
        Text = Program.AppName;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        MinimizeBox = MaximizeBox = false;
        BackColor = Ui.Bg;
        ClientSize = new Size(Ui.S(440), Ui.S(170));
        var lbl = Ui.Text_(Lang.F("「{0}」想遠端控制呢部電腦。\n允許嗎？（30 秒內冇揀就自動拒絕）", who), Ui.Text, Ui.F(10.5f), new Padding(0));
        lbl.MaximumSize = new Size(Ui.S(408), 0);
        lbl.Location = new Point(Ui.S(16), Ui.S(18));
        var yes = new FancyButton { Text = Lang.T("允許"), Kind = BtnKind.Success, Size = new Size(Ui.S(100), Ui.S(36)), Location = new Point(Ui.S(214), Ui.S(116)) };
        var no = new FancyButton { Text = Lang.T("拒絕"), Kind = BtnKind.Danger, Size = new Size(Ui.S(100), Ui.S(36)), Location = new Point(Ui.S(324), Ui.S(116)) };
        yes.Click += (_, _) => DialogResult = DialogResult.OK;
        no.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.AddRange(new Control[] { lbl, yes, no });
        var t = new System.Windows.Forms.Timer { Interval = 30000 };
        t.Tick += (_, _) => { t.Stop(); DialogResult = DialogResult.Cancel; };
        Shown += (_, _) => { t.Start(); Activate(); try { System.Media.SystemSounds.Exclamation.Play(); } catch { } };
        FormClosed += (_, _) => t.Dispose();
    }
}

/// <summary>被控時螢幕頂嘅提示條（撳「中斷」即刻斷）。</summary>
/// <summary>被控時螢幕四邊嘅紅框（撳穿、唔會出現喺對方畫面）。</summary>
internal sealed class RemoteFrame : Form
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000008 | 0x80 | 0x20 | 0x80000; // 唔攞焦點、置頂、唔喺 Alt+Tab、撳穿、layered
            return cp;
        }
    }

    public RemoteFrame(Rectangle bounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int w = Math.Max(4, Ui.S(5));
        using var b = new SolidBrush(Ui.Danger);
        e.Graphics.FillRectangle(b, 0, 0, Width, w);
        e.Graphics.FillRectangle(b, 0, Height - w, Width, w);
        e.Graphics.FillRectangle(b, 0, 0, w, Height);
        e.Graphics.FillRectangle(b, Width - w, 0, w, Height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { SetWindowDisplayAffinity(Handle, 0x11); } catch { }
    }
}

internal sealed class ControlBar : Form
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000008 | 0x80;
            return cp;
        }
    }

    public ControlBar(string who, Action disconnect)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Danger;
        var text = Lang.F("● {0} 正在遠端控制", who);
        var f = Ui.F(9.5f, FontStyle.Bold);
        int tw = TextRenderer.MeasureText(text, f).Width;
        Size = new Size(tw + Ui.S(130), Ui.S(36));
        var wa = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y);
        var lbl = new Label { Text = text, ForeColor = Color.White, Font = f, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
        lbl.SetBounds(Ui.S(12), 0, tw + Ui.S(10), Height);
        var btn = new FancyButton { Text = Lang.T("中斷"), Kind = BtnKind.Default, Font = Ui.F(9f) };
        btn.SetBounds(Width - Ui.S(96), Ui.S(4), Ui.S(88), Ui.S(28));
        btn.Click += (_, _) => disconnect();
        Controls.Add(lbl);
        Controls.Add(btn);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { SetWindowDisplayAffinity(Handle, 0x11); } catch { } // 唔會出現喺對方畫面
    }
}

// =====================================================================
//  控制方（連去其他機）
// =====================================================================
internal static class RemoteConnect
{
    /// <summary>用 ID（或者 IP:port）搵對方，逐個試連。</summary>
    public static async Task<(SecureChannel Ch, TcpClient C, string HostId)> ConnectAsync(string target, string password, Action<string> status, CancellationToken ct)
    {
        target = target.Trim().Replace(" ", "");
        var candidates = new List<IPEndPoint>();
        string expectId = "";
        if (target.Length == 9 && target.All(char.IsDigit))
        {
            expectId = target;
            status(Lang.T("喺同一個網絡搵緊…"));
            candidates.AddRange(await LanFindAsync(expectId, ct));
            if (candidates.Count == 0)
            {
                status(Lang.T("經 DHT 網絡搵緊對方（大約 10 秒）…"));
                using var dht = new Scripts.DhtClient();
                var (peers, _) = await dht.LookupAsync(0, TimeSpan.FromSeconds(10), ct, RemoteSettings.DhtKey(expectId));
                candidates.AddRange(peers);
            }
        }
        else
        {
            // 直接輸入 IP 或者 IP:port（例如用 Tailscale）
            int port = RemoteSettings.Port;
            string host = target;
            int colon = target.LastIndexOf(':');
            if (colon > 0 && int.TryParse(target[(colon + 1)..], out int p)) { host = target[..colon]; port = p; }
            var addrs = IPAddress.TryParse(host, out var ip) ? new[] { ip } : await Dns.GetHostAddressesAsync(host, ct);
            candidates.AddRange(addrs.Select(a => new IPEndPoint(a, port)));
        }
        if (candidates.Count == 0) throw new InvalidOperationException(Lang.T("搵唔到對方：佢可能未開「允許遠端連入」，或者剛剛先開（等 1 分鐘再試）。"));
        Exception? last = null;
        foreach (var ep in candidates.Distinct().Take(12))
        {
            status(Lang.F("連緊 {0}…", ep));
            var c = new TcpClient { NoDelay = true };
            try
            {
                using var to = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                using var link = CancellationTokenSource.CreateLinkedTokenSource(to.Token, ct);
                await c.ConnectAsync(ep, link.Token);
                status(Lang.T("驗證緊（對方可能要撳「允許」）…"));
                using var hs = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                using var link2 = CancellationTokenSource.CreateLinkedTokenSource(hs.Token, ct);
                var (ch, id) = await RemoteHandshake.ClientAsync(c, expectId, password, Environment.MachineName, link2.Token);
                return (ch, c, id);
            }
            catch (UnauthorizedAccessException) { c.Dispose(); throw; }
            catch (Exception ex) { last = ex; c.Dispose(); }
        }
        throw new InvalidOperationException(Lang.T("連唔到對方：兩邊網絡可能都喺 NAT / CGNAT 後面，路由器又開唔到端口。") +
                                            (last != null ? "（" + last.Message + "）" : ""));
    }

    private static async Task<List<IPEndPoint>> LanFindAsync(string id, CancellationToken ct)
    {
        var found = new List<IPEndPoint>();
        try
        {
            using var u = new UdpClient(0) { EnableBroadcast = true };
            var msg = Encoding.ASCII.GetBytes("MTRD?" + id);
            await u.SendAsync(msg, msg.Length, new IPEndPoint(IPAddress.Broadcast, RemoteSettings.Port + 1));
            if (RemoteHost.Running) await u.SendAsync(msg, msg.Length, new IPEndPoint(IPAddress.Loopback, RemoteSettings.Port + 1));
            using var to = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            using var link = CancellationTokenSource.CreateLinkedTokenSource(to.Token, ct);
            while (true)
            {
                var r = await u.ReceiveAsync(link.Token);
                var s = Encoding.ASCII.GetString(r.Buffer);
                if (s.StartsWith("MTRD!" + id + ":") && int.TryParse(s[(6 + id.Length)..], out int port))
                    found.Add(new IPEndPoint(r.RemoteEndPoint.Address, port));
            }
        }
        catch { }
        return found;
    }
}

/// <summary>遠端畫面視窗。</summary>
internal sealed class ViewerForm : Form
{
    private readonly SecureChannel _ch;
    private readonly TcpClient _c;
    private readonly CancellationTokenSource _cts = new();
    private readonly Canvas _canvas;
    private readonly FancyButton _monBtn, _qBtn, _fullBtn;
    private readonly Label _info;
    private readonly List<(Rectangle R, bool Primary)> _screens = new();
    private int _mon, _q = 1;
    private Bitmap? _frame;
    private readonly object _lock = new();
    private int _frames;
    private long _bytes;
    private bool _closing;

    public ViewerForm(SecureChannel ch, TcpClient c, string title)
    {
        _ch = ch;
        _c = c;
        Text = title + " - " + Program.AppName;
        BackColor = Color.Black;
        Size = new Size(Ui.S(1280), Ui.S(800));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = false;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Ui.S(44), BackColor = Ui.Panel, Padding = new Padding(Ui.S(8), Ui.S(5), Ui.S(8), 0), WrapContents = false };
        FancyButton B(string t, Action a, int w)
        {
            var b = new FancyButton { Text = t, Kind = BtnKind.Ghost, Font = Ui.F(9f), Size = new Size(Ui.S(w), Ui.S(32)), Margin = new Padding(0, 0, Ui.S(6), 0) };
            b.Click += (_, _) => { a(); _canvas!.Focus(); };
            bar.Controls.Add(b);
            return b;
        }
        _monBtn = B("", MonMenu, 120);
        _qBtn = B("", QMenu, 110);
        B(Lang.T("傳送我嘅剪貼簿"), () =>
        {
            try { if (Clipboard.ContainsText()) _ = _ch.SendAsync(RMsg.ClipSet, Encoding.UTF8.GetBytes(Clipboard.GetText()), _cts.Token); } catch { }
        }, 140);
        B(Lang.T("攞對方剪貼簿"), () => _ = _ch.SendAsync(RMsg.ClipGet, Array.Empty<byte>(), _cts.Token), 120);
        B(Lang.T("Win 鍵"), () => { SendKey(0x5B, true, true); SendKey(0x5B, false, true); }, 80);
        _fullBtn = B(Lang.T("全螢幕"), ToggleFull, 80);
        var disc = B(Lang.T("中斷"), Close, 70);
        disc.Kind = BtnKind.Danger;
        _info = Ui.Text_("", Ui.TextDim, Ui.F(9f), new Padding(Ui.S(6), Ui.S(8), 0, 0));
        bar.Controls.Add(_info);
        _canvas = new Canvas(this) { Dock = DockStyle.Fill };
        Controls.Add(_canvas);
        Controls.Add(bar);
        UpdateButtons();
        Shown += (_, _) => { _canvas.Focus(); _ = ReceiveLoop(); };
        FormClosing += (_, _) => { _closing = true; _cts.Cancel(); try { _c.Dispose(); } catch { } _ch.Dispose(); };
        var stats = new System.Windows.Forms.Timer { Interval = 1000 };
        stats.Tick += (_, _) =>
        {
            int f = Interlocked.Exchange(ref _frames, 0);
            long b = Interlocked.Exchange(ref _bytes, 0);
            _info.Text = $"{f} fps · {b / 1024} KB/s";
        };
        stats.Start();
        FormClosed += (_, _) => stats.Dispose();
    }

    private void UpdateButtons()
    {
        _monBtn.Text = Lang.F("螢幕 {0}", _mon + 1) + " ▾";
        _qBtn.Text = (_q switch { 0 => Lang.T("流暢"), 2 => Lang.T("高畫質"), _ => Lang.T("平衡") }) + " ▾";
        _monBtn.Invalidate();
        _qBtn.Invalidate();
    }

    private void MonMenu()
    {
        TUi.Pick(_monBtn, _screens.Select((s, i) => (Lang.F("螢幕 {0}", i + 1) + $"  {s.R.Width}×{s.R.Height}" + (s.Primary ? Lang.T("（主螢幕）") : ""), i)), _mon,
            v => { _mon = v; SendSelect(); UpdateButtons(); });
    }

    private void QMenu()
    {
        TUi.Pick(_qBtn, new[] { (Lang.T("流暢（解像度減半，慢網用）"), 0), (Lang.T("平衡"), 1), (Lang.T("高畫質（要快網）"), 2) }, _q,
            v => { _q = v; SendSelect(); UpdateButtons(); });
    }

    private FormWindowState _prevState;
    private FormBorderStyle _prevBorder;

    private void ToggleFull()
    {
        if (FormBorderStyle != FormBorderStyle.None)
        {
            _prevState = WindowState;
            _prevBorder = FormBorderStyle;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            _fullBtn.Text = Lang.T("離開全螢幕");
        }
        else
        {
            FormBorderStyle = _prevBorder;
            WindowState = _prevState;
            if (WindowState == FormWindowState.Normal) Size = new Size(Ui.S(1280), Ui.S(800));
            _fullBtn.Text = Lang.T("全螢幕");
        }
        _fullBtn.Invalidate();
    }

    private void SendSelect()
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(b, _mon);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), _q);
        _ = _ch.SendAsync(RMsg.Select, b, _cts.Token);
    }

    private async Task ReceiveLoop()
    {
        Bitmap? work = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (t, d) = await _ch.ReceiveAsync(_cts.Token);
                Interlocked.Add(ref _bytes, d.Length);
                switch (t)
                {
                    case RMsg.Screens:
                    {
                        int n = BinaryPrimitives.ReadInt32LittleEndian(d);
                        _screens.Clear();
                        for (int i = 0; i < n; i++)
                        {
                            int o = 4 + i * 17;
                            _screens.Add((new Rectangle(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o)), BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 4)),
                                BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 8)), BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 12))), d[o + 16] == 1));
                        }
                        _mon = Math.Max(0, _screens.FindIndex(s => s.Primary));
                        UpdateButtons();
                        SendSelect();
                        break;
                    }
                    case RMsg.FrameBegin:
                    {
                        int w = BinaryPrimitives.ReadInt32LittleEndian(d), h = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4));
                        lock (_lock)
                        {
                            if (_frame == null || _frame.Width != w || _frame.Height != h)
                            {
                                var nb = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                                if (_frame != null) using (var g = Graphics.FromImage(nb)) g.DrawImage(_frame, 0, 0, w, h);
                                _frame?.Dispose();
                                _frame = nb;
                            }
                            work = _frame;
                        }
                        break;
                    }
                    case RMsg.Tile:
                    {
                        if (work == null) break;
                        int x = BinaryPrimitives.ReadInt16LittleEndian(d), y = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(2));
                        using var ms = new MemoryStream(d, 8, d.Length - 8);
                        using var img = Image.FromStream(ms);
                        lock (_lock) using (var g = Graphics.FromImage(work)) g.DrawImage(img, x, y, img.Width, img.Height);
                        break;
                    }
                    case RMsg.FrameEnd:
                        Interlocked.Increment(ref _frames);
                        _ = _ch.SendAsync(RMsg.Ack, Array.Empty<byte>(), _cts.Token);
                        _canvas.Invalidate();
                        break;
                    case RMsg.Clip:
                        var text = Encoding.UTF8.GetString(d);
                        try { if (text.Length > 0) { Clipboard.SetText(text); Toast.Show(Lang.T("已攞到對方剪貼簿"), Ui.Success, 1200); } } catch { }
                        break;
                }
            }
        }
        catch (Exception ex) when (!_closing)
        {
            if (ex is not OperationCanceledException) AppPaths.LogError("遠端畫面", ex);
            MessageBox.Show(this, Lang.T("連線中斷咗。"), Program.AppName);
            Close();
        }
        catch { }
    }

    // ---- 輸入 ----
    private long _lastMove;

    public void SendMove(float nx, float ny)
    {
        long now = Environment.TickCount64;
        if (now - _lastMove < 15) return;
        _lastMove = now;
        var b = new byte[8];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), nx);
        BitConverter.TryWriteBytes(b.AsSpan(4, 4), ny);
        _ = _ch.SendAsync(RMsg.MouseMove, b, _cts.Token);
    }

    public void SendButton(MouseButtons btn, bool down)
    {
        byte n = btn switch { MouseButtons.Right => 1, MouseButtons.Middle => 2, _ => 0 };
        _ = _ch.SendAsync(RMsg.MouseBtn, new[] { n, (byte)(down ? 1 : 0) }, _cts.Token);
    }

    public void SendWheel(int delta)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, delta);
        _ = _ch.SendAsync(RMsg.Wheel, b, _cts.Token);
    }

    public void SendKey(int vk, bool down, bool ext = false)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(b, (short)vk);
        b[2] = (byte)(down ? 1 : 0);
        b[3] = (byte)(ext ? 1 : 0);
        _ = _ch.SendAsync(RMsg.Key, b, _cts.Token);
    }

    public Bitmap? Frame => _frame;
    public object FrameLock => _lock;

    /// <summary>畫面：按比例縮放，收晒滑鼠鍵盤。</summary>
    private sealed class Canvas : Control
    {
        private readonly ViewerForm _v;

        public Canvas(ViewerForm v)
        {
            _v = v;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
            TabStop = true;
        }

        private Rectangle View()
        {
            var f = _v.Frame;
            if (f == null) return Rectangle.Empty;
            double k = Math.Min((double)Width / f.Width, (double)Height / f.Height);
            int w = (int)(f.Width * k), h = (int)(f.Height * k);
            return new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.Black);
            lock (_v.FrameLock)
            {
                var f = _v.Frame;
                if (f == null)
                {
                    TextRenderer.DrawText(g, Lang.T("等緊畫面…"), Ui.F(12f), ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.DrawImage(f, View());
            }
        }

        private bool Map(Point p, out float nx, out float ny)
        {
            var r = View();
            nx = ny = 0;
            if (r.Width <= 0) return false;
            nx = (float)(p.X - r.X) / r.Width;
            ny = (float)(p.Y - r.Y) / r.Height;
            return nx is >= 0 and <= 1 && ny is >= 0 and <= 1;
        }

        protected override void OnMouseMove(MouseEventArgs e) { if (Map(e.Location, out var x, out var y)) _v.SendMove(x, y); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (Map(e.Location, out var x, out var y)) { _v._lastMove = 0; _v.SendMove(x, y); _v.SendButton(e.Button, true); }
        }

        protected override void OnMouseUp(MouseEventArgs e) => _v.SendButton(e.Button, false);
        protected override void OnMouseWheel(MouseEventArgs e) => _v.SendWheel(e.Delta);

        protected override bool IsInputKey(Keys keyData) => true;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Tab、方向鍵、Alt 組合等全部傳去對方
            if (msg.Msg is 0x100 or 0x104 or 0x101 or 0x105)
            {
                bool down = msg.Msg is 0x100 or 0x104;
                int vk = (int)msg.WParam;
                bool ext = ((long)msg.LParam & 0x01000000) != 0;
                _v.SendKey(vk, down, ext);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            // 鍵盤彈起（WM_KEYUP / WM_SYSKEYUP）唔會經 ProcessCmdKey
            if (m.Msg is 0x101 or 0x105 && Focused)
            {
                _v.SendKey((int)m.WParam, false, ((long)m.LParam & 0x01000000) != 0);
                return;
            }
            if (m.Msg is 0x100 or 0x104 && Focused)
            {
                _v.SendKey((int)m.WParam, true, ((long)m.LParam & 0x01000000) != 0);
                return;
            }
            base.WndProc(ref m);
        }
    }
}

// =====================================================================
//  頁面
// =====================================================================
internal sealed class RemotePage : ToolPage
{
    private sealed record Peer(string Id, string Name);

    private static string BookFile => Path.Combine(AppPaths.Data, "remote_peers.txt");
    private readonly FancyButton _hostBtn;
    private readonly Label _id, _pw, _hostStatus, _connStatus, _fixedLbl;
    private readonly TextBox _target = new(), _password = new(), _name = new();
    private readonly FlowLayoutPanel _book, _history;
    private readonly List<Peer> _peers = new();
    private CancellationTokenSource? _connecting;

    public static Control Build() => new RemotePage();

    private RemotePage()
    {
        RemoteHost.Init(this);
        CleanupOld();
        LoadBook();

        // ---- 先講清楚邊啲情況用唔到 ----
        Card(Lang.T("邊啲情況用唔到？"),
            Lang.T("MultiTool 冇自己嘅中轉伺服器，要兩部電腦直接連到先用得。重點係「被控嗰部電腦」嘅網絡。"),
            new WrapText(
                Lang.T("✗ 外面連唔到入嚟（同一個網絡就冇問題）：\n") +
                Lang.T("   • 被控電腦用手機流動數據 / 熱點（4G、5G）上網\n") +
                Lang.T("   • 被控電腦嘅寬頻係 CGNAT（部分 ISP 會咁做；可以問 ISP 攞「公共 IP」）\n") +
                Lang.T("   • 路由器冇開 UPnP，又冇手動轉發 TCP 端口 47820\n") +
                Lang.T("   • 公司、學校、酒店、宿舍、咖啡店等公共網絡（通常封鎖咗連入或者 BitTorrent DHT）\n") +
                Lang.T("✗ 搵唔到對方：\n") +
                Lang.T("   • 被控電腦關咗機、瞓咗眠，或者 MultiTool 冇開、冇開「允許遠端連入」\n") +
                Lang.T("   • Windows 防火牆冇允許 MultiTool（第一次開會彈視窗問，要撳允許）\n") +
                Lang.T("   • 對方用緊舊版 MultiTool（冇呢個功能）\n") +
                Lang.T("✗ 連到但控制唔到：\n") +
                Lang.T("   • UAC 管理員權限視窗、鎖定畫面、登入畫面、Ctrl+Alt+Del\n") +
                Lang.T("   • 用管理員身份開嘅程式（被控電腦嘅 MultiTool 都要用管理員身份開先得）\n") +
                Lang.T("✓ 補救：兩部電腦都裝 Tailscale 或者 ZeroTier（免費），再喺「對方 ID」輸入對方嘅 Tailscale IP；或者喺路由器手動轉發 TCP 47820 去被控電腦。\n") +
                Lang.T("ℹ 全程加密（ECDH + AES-GCM），密碼唔會喺網上傳送；連續試錯 5 次會鎖 30 分鐘。長途連線（例如香港 ↔ 加拿大）每秒大約幾格畫面，操作冇問題，睇片就唔啱。")));

        // ---- 此電腦 ----
        _hostBtn = TUi.Btn("", () => { if (RemoteHost.Running) RemoteHost.Stop(); else RemoteHost.Start(); UpdateHost(); }, 200);
        _id = Ui.Text_(RemoteSettings.Pretty(RemoteSettings.Id), Ui.Text, Ui.F(18f, FontStyle.Bold), new Padding(0, 0, Ui.S(12), 0));
        _pw = Ui.Text_("", Ui.Text, Ui.F(14f, FontStyle.Bold), new Padding(0, Ui.S(3), Ui.S(12), 0));
        var copyId = TUi.Btn(Lang.T("複製"), () => Copy(RemoteSettings.Id), 70);
        var copyPw = TUi.Btn(Lang.T("複製"), () => Copy(RemoteSettings.OneTime), 70);
        var newPw = TUi.Btn(Lang.T("換一個"), () => { RemoteSettings.NewOneTime(); UpdateHost(); }, 80);
        _fixedLbl = TUi.L("");
        var setFixed = TUi.Btn(Lang.T("設定固定密碼…"), SetFixed);
        var clearFixed = TUi.Btn(Lang.T("取消固定密碼"), () => { RemoteSettings.SetFixed(null); UpdateHost(); });
        var auto = TUi.Check(Lang.T("開 MultiTool 時自動允許遠端連入"), RemoteSettings.AutoHost, v => { RemoteSettings.AutoHost = v; RemoteSettings.Save(); });
        _hostStatus = TUi.L("");
        _history = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Card(Lang.T("此電腦"),
            Lang.T("開咗「允許遠端連入」，將 ID 同一次性密碼俾對方就連到。用一次性密碼連入嚟每次都會問你允唔允許；用固定密碼就唔使（方便你喺外面連返屋企）。"),
            TUi.Col(
                TUi.Row(_hostBtn, _hostStatus),
                TUi.Row(TUi.L(Lang.T("你嘅 ID")), _id, copyId),
                TUi.Row(TUi.L(Lang.T("一次性密碼")), _pw, copyPw, newPw),
                TUi.Row(TUi.L(Lang.T("固定密碼")), _fixedLbl, setFixed, clearFixed),
                TUi.Row(auto),
                TUi.L(Lang.T("最近連線紀錄")),
                _history));

        // ---- 連去其他電腦 ----
        _target.PlaceholderText = Lang.T("對方 ID（或者 IP / IP:端口）");
        _password.PlaceholderText = Lang.T("密碼");
        _password.UseSystemPasswordChar = true;
        _name.PlaceholderText = Lang.T("名稱（加入常用時用）");
        _target.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _password.Focus(); } };
        _password.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = ConnectAsync(_target.Text, _password.Text); } };
        var go = TUi.Btn(Lang.T("連線"), () => _ = ConnectAsync(_target.Text, _password.Text), 90, BtnKind.Primary);
        var save = TUi.Btn(Lang.T("加入常用"), () =>
        {
            var id = _target.Text.Trim();
            if (id.Length == 0) return;
            _peers.RemoveAll(p => p.Id == id);
            _peers.Insert(0, new Peer(id, _name.Text.Trim()));
            SaveBook();
            BuildBook();
            _name.Clear();
        });
        _connStatus = TUi.L("");
        _book = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Card(Lang.T("連線到其他電腦"), Lang.T("對方都要用 MultiTool 開咗「允許遠端連入」。"),
            TUi.Col(
                TUi.Row(new InputHost(_target, Ui.S(220)), new InputHost(_password, Ui.S(160)), go),
                TUi.Row(_connStatus),
                TUi.Row(new InputHost(_name, Ui.S(220)), save),
                TUi.L(Lang.T("常用電腦")),
                _book));


        RemoteHost.Changed += OnHostChanged;
        Disposed += (_, _) => RemoteHost.Changed -= OnHostChanged;
        BuildBook();
        UpdateHost();
        if (RemoteSettings.AutoHost && !RemoteHost.Running) RemoteHost.Start();
        Extensions.Changed += () => { if (!Extensions.IsInstalled(Extensions.Remote) && RemoteHost.Running) RemoteHost.Stop(); };
    }

    /// <summary>之前整合 RustDesk 嘅版本：刪走下載咗嘅 RustDesk。</summary>
    private static void CleanupOld()
    {
        try
        {
            var dir = Path.Combine(AppPaths.Data, "tools", "rustdesk");
            if (!Directory.Exists(dir)) return;
            var exe = Path.Combine(dir, "rustdesk.exe");
            foreach (var p in Process.GetProcessesByName("rustdesk"))
                try { if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) p.Kill(true); } catch { }
            Thread.Sleep(300);
            Directory.Delete(dir, true);
        }
        catch { }
    }

    private static void Copy(string s)
    {
        try { Clipboard.SetText(s); Toast.Show(Lang.T("已複製"), Ui.Success, 1200); } catch { }
    }

    private void OnHostChanged() { if (!IsDisposed) BeginInvoke(UpdateHost); }

    private void UpdateHost()
    {
        bool on = RemoteHost.Running;
        _hostBtn.Text = on ? Lang.T("● 允許遠端連入（撳關閉）") : Lang.T("○ 開啟「允許遠端連入」");
        _hostBtn.Kind = on ? BtnKind.Success : BtnKind.Default;
        _hostBtn.Invalidate();
        _hostStatus.Text = on ? RemoteHost.Status : Lang.T("已關閉：其他人連唔到入嚟");
        if (on && RemoteHost.LockedUntil > DateTime.Now)
            _hostStatus.Text = Lang.F("🔒 密碼試錯太多次，鎖到 {0:HH:mm}（期間所有人都連唔到）", RemoteHost.LockedUntil);
        _pw.Text = RemoteSettings.OneTime;
        _fixedLbl.Text = RemoteSettings.FixedKey != null ? Lang.T("已設定") : Lang.T("未設定");
        _history.SuspendLayout();
        foreach (Control c in _history.Controls.Cast<Control>().ToList()) { _history.Controls.Remove(c); c.Dispose(); }
        List<string> h;
        lock (RemoteHost.History) h = RemoteHost.History.Take(8).ToList();
        if (h.Count == 0) _history.Controls.Add(TUi.L(Lang.T("（未有）"), Ui.TextFaint));
        foreach (var l in h) _history.Controls.Add(TUi.L(l, Ui.TextDim));
        _history.ResumeLayout(true);
        Relayout();
    }

    private void SetFixed()
    {
        using var f = new Form
        {
            Text = Lang.T("設定固定密碼"), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, BackColor = Ui.Bg, ForeColor = Ui.Text, ClientSize = new Size(Ui.S(420), Ui.S(170))
        };
        var lbl = Ui.Text_(Lang.T("固定密碼（最少 12 個字，要有英文字母同數字，建議加符號）："), Ui.TextDim, Ui.F(9.5f), new Padding(0));
        lbl.Location = new Point(Ui.S(16), Ui.S(16));
        var box = new TextBox { UseSystemPasswordChar = true, BorderStyle = BorderStyle.None };
        var host = new InputHost(box, Ui.S(388)) { Location = new Point(Ui.S(16), Ui.S(46)) };
        var ok = new FancyButton { Text = Lang.T("儲存"), Kind = BtnKind.Primary, Size = new Size(Ui.S(90), Ui.S(34)), Location = new Point(Ui.S(214), Ui.S(110)) };
        var cancel = new FancyButton { Text = Lang.T("取消"), Kind = BtnKind.Ghost, Size = new Size(Ui.S(90), Ui.S(34)), Location = new Point(Ui.S(314), Ui.S(110)) };
        ok.Click += (_, _) =>
        {
            if (RemoteSettings.Weak(box.Text) is { } why) { MessageBox.Show(f, why, Program.AppName); return; }
            f.DialogResult = DialogResult.OK;
        };
        cancel.Click += (_, _) => f.DialogResult = DialogResult.Cancel;
        f.Controls.AddRange(new Control[] { lbl, host, ok, cancel });
        if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
        RemoteSettings.SetFixed(box.Text);
        UpdateHost();
    }

    private async Task ConnectAsync(string target, string password)
    {
        if (_connecting != null) { _connecting.Cancel(); return; }
        target = target.Trim();
        if (target.Length == 0) { _connStatus.Text = Lang.T("請輸入對方 ID"); return; }
        if (password.Length == 0) { _connStatus.Text = Lang.T("請輸入密碼"); return; }
        _connecting = new CancellationTokenSource();
        _connStatus.ForeColor = Ui.TextDim;
        try
        {
            var (ch, c, id) = await Task.Run(() => RemoteConnect.ConnectAsync(target, password,
                s => BeginInvoke(() => _connStatus.Text = s), _connecting.Token));
            _connStatus.Text = Lang.T("✓ 已連線");
            _connStatus.ForeColor = Ui.Success;
            _password.Clear();
            var name = _peers.FirstOrDefault(p => p.Id == target)?.Name;
            var v = new ViewerForm(ch, c, (name is { Length: > 0 } ? name + " · " : "") + RemoteSettings.Pretty(id));
            v.Show();
        }
        catch (OperationCanceledException) { _connStatus.Text = Lang.T("已取消"); }
        catch (Exception ex) { _connStatus.Text = ex.Message; _connStatus.ForeColor = Ui.Danger; }
        finally { _connecting = null; }
    }

    private void LoadBook()
    {
        try
        {
            if (!File.Exists(BookFile)) return;
            foreach (var l in File.ReadAllLines(BookFile))
            {
                var p = l.Split('|');
                if (p.Length >= 1 && p[0].Trim().Length > 0)
                    _peers.Add(new Peer(p[0].Trim(), p.Length > 1 ? Encoding.UTF8.GetString(Convert.FromBase64String(p[1])) : ""));
            }
        }
        catch { }
    }

    private void SaveBook()
    {
        try { File.WriteAllLines(BookFile, _peers.Select(p => p.Id + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Name)))); } catch { }
    }

    private void BuildBook()
    {
        _book.SuspendLayout();
        foreach (Control c in _book.Controls.Cast<Control>().ToList()) { _book.Controls.Remove(c); c.Dispose(); }
        if (_peers.Count == 0) _book.Controls.Add(TUi.L(Lang.T("未有常用電腦，輸入 ID 後撳「加入常用」。")));
        foreach (var p0 in _peers)
        {
            var p = p0;
            var name = new Label
            {
                Text = (p.Name.Length > 0 ? p.Name + "  ·  " : "") + RemoteSettings.Pretty(p.Id), AutoSize = false, Width = Ui.S(320), Height = Ui.S(34),
                ForeColor = Ui.Text, BackColor = Ui.Card, Font = Ui.F(10f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                UseMnemonic = false, Margin = new Padding(0, 0, Ui.S(10), 0)
            };
            var row = TUi.Row(name, TUi.Btn(Lang.T("揀"), () => { _target.Text = p.Id; _password.Focus(); }, 60, BtnKind.Primary),
                TUi.Btn("✕", () => { _peers.Remove(p); SaveBook(); BuildBook(); }, 40));
            row.WrapContents = false;
            row.Margin = new Padding(0, 0, 0, Ui.S(4));
            _book.Controls.Add(row);
        }
        _book.ResumeLayout(true);
        Relayout();
    }
}
