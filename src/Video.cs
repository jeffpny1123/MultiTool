using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.Common;
using MultiTool.UI;

namespace MultiTool.Video;

// =====================================================================
//  錄影工具：ffmpeg（安裝時下載）錄畫面 + 自己用 WASAPI 錄電腦聲 / 咪高峰，經 named pipe 餵俾 ffmpeg。
// =====================================================================

/// <summary>ffmpeg：下載、搵、試編碼器。</summary>
internal static class Ffmpeg
{
    private const string Url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    public static string Dir => Path.Combine(AppPaths.Data, "tools", "ffmpeg");
    public static string Exe => Path.Combine(Dir, "ffmpeg.exe");
    public static bool Ready => File.Exists(Exe);

    public static async Task InstallAsync(Action<string, double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        string zip = Path.Combine(Dir, "ffmpeg.zip");
        string tmp = zip + ".part";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiTool");
            using var resp = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    progress(Lang.T("下載 ffmpeg…"), total > 0 ? (double)done / total : -1);
                }
            }
        }
        File.Move(tmp, zip, true);
        progress(Lang.T("解壓縮…"), -1);
        await Task.Run(() =>
        {
            using var z = ZipFile.OpenRead(zip);
            var e = z.Entries.FirstOrDefault(x => x.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(Lang.T("壓縮檔入面搵唔到 ffmpeg.exe"));
            e.ExtractToFile(Exe, true);
        }, ct);
        try { File.Delete(zip); } catch { }
        ClearCache();
        progress(Lang.T("完成"), 1);
    }

    public static void Delete()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
        ClearCache();
    }

    private static readonly Dictionary<string, string> _encoders = new();

    /// <summary>揀編碼器：顯示卡優先（NVIDIA → Intel → AMD），都唔得 / 揀咗淨用 CPU 就用軟件編碼。</summary>
    public static string Encoder(string codec, bool hw)
    {
        string key = codec + (hw ? "+hw" : "");
        lock (_encoders) if (_encoders.TryGetValue(key, out var c)) return c;
        var list = new List<string>();
        if (hw) list.AddRange(new[] { codec + "_nvenc", codec + "_qsv", codec + "_amf" });
        list.AddRange(codec switch
        {
            "hevc" => new[] { "libx265" },
            "av1" => new[] { "libsvtav1", "libaom-av1" },
            _ => new[] { "libx264" }
        });
        string found = "";
        foreach (var enc in list)
        {
            try
            {
                string fmt = enc.Contains("_") && !enc.StartsWith("lib") ? "nv12" : "yuv420p";
                var psi = new ProcessStartInfo(Exe, $"-hide_banner -loglevel error -f lavfi -i color=black:s=320x240:d=0.3 -vf format={fmt} -c:v {enc} -f null -")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                using var p = Process.Start(psi)!;
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } continue; }
                if (p.ExitCode == 0) { found = enc; break; }
            }
            catch { }
        }
        if (found.Length == 0)
            throw new InvalidOperationException(Lang.F("你部電腦用唔到 {0} 編碼，請揀第二個編碼器。", RecSettings.CodecName(codec)));
        lock (_encoders) _encoders[key] = found;
        return found;
    }

    public static string EncoderName(string enc) =>
        enc.EndsWith("_nvenc") ? "NVIDIA NVENC" : enc.EndsWith("_qsv") ? "Intel Quick Sync" : enc.EndsWith("_amf") ? "AMD AMF" : "CPU（" + enc + "）";

    public static void ClearCache() { lock (_encoders) _encoders.Clear(); }
}

// ---------------------------------------------------------------------
//  WASAPI 錄音（電腦聲 = loopback；咪高峰 = capture）
// ---------------------------------------------------------------------
internal sealed class AudioSource : IDisposable
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr h);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpcPos);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    private static readonly Guid FloatSub = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly bool _loopback;
    private readonly string _pipeName;
    private readonly NamedPipeServerStream _pipe;
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly ManualResetEventSlim _formatReady = new();
    public int Rate { get; private set; } = 48000;
    public int Channels { get; private set; } = 2;
    public string? Error { get; private set; }
    public string PipePath => @"\\.\pipe\" + _pipeName;

    public bool Loopback => _loopback;

    public AudioSource(bool loopback)
    {
        _loopback = loopback;
        _pipeName = "mtrec_" + (loopback ? "sys_" : "mic_") + Guid.NewGuid().ToString("N")[..8];
        _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        _thread = new Thread(Run) { IsBackground = true, Name = "rec-audio" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    /// <summary>開始（攞格式）；錄音要等 ffmpeg 連咗個 pipe 先開始。</summary>
    public bool Start()
    {
        _thread.Start();
        _formatReady.Wait(3000);
        return Error == null;
    }

    private void Run()
    {
        IAudioClient? client = null;
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            Check(en.GetDefaultAudioEndpoint(_loopback ? 0 : 1, 0, out var dev), "GetDefaultAudioEndpoint");
            var iid = typeof(IAudioClient).GUID;
            Check(dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out var o), "Activate");
            client = (IAudioClient)o;
            Check(client.GetMixFormat(out var fmt), "GetMixFormat");
            short tag = Marshal.ReadInt16(fmt, 0);
            Channels = Marshal.ReadInt16(fmt, 2);
            Rate = Marshal.ReadInt32(fmt, 4);
            int block = Marshal.ReadInt16(fmt, 12);
            int bits = Marshal.ReadInt16(fmt, 14);
            bool isFloat = tag == 3 || (tag == unchecked((short)0xFFFE) && new Guid(ReadBytes(fmt, 24, 16)) == FloatSub);
            Check(client.Initialize(0, _loopback ? 0x00020000 : 0, 2_000_000, 0, fmt, IntPtr.Zero), "Initialize");
            Marshal.FreeCoTaskMem(fmt);
            var cid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref cid, out var co), "GetService");
            var cap = (IAudioCaptureClient)co;
            _formatReady.Set();

            // 等 ffmpeg 連過嚟
            var connect = _pipe.WaitForConnectionAsync();
            while (!connect.IsCompleted) { if (_stop) return; Thread.Sleep(20); }
            if (connect.IsFaulted) return;

            Check(client.Start(), "Start");
            var sw = Stopwatch.StartNew();
            long written = 0;
            var outBuf = new byte[0];
            while (!_stop)
            {
                Thread.Sleep(10);
                while (cap.GetNextPacketSize(out uint n) == 0 && n > 0)
                {
                    if (cap.GetBuffer(out var data, out uint frames, out uint flags, out _, out _) != 0) break;
                    int samples = (int)frames * Channels;
                    int need = samples * 4;
                    if (outBuf.Length < need) outBuf = new byte[need];
                    if ((flags & 2) != 0) Array.Clear(outBuf, 0, need); // 靜音
                    else if (isFloat && bits == 32) Marshal.Copy(data, outBuf, 0, need);
                    else ConvertToFloat(data, samples, bits, outBuf);
                    cap.ReleaseBuffer(frames);
                    _pipe.Write(outBuf, 0, need);
                    written += frames;
                }
                // 冇聲時 loopback 唔會俾資料：自己補靜音，等聲畫同步
                long expected = (long)(sw.Elapsed.TotalSeconds * Rate);
                long gap = expected - written - Rate / 20; // 預留 50ms
                if (gap > 0)
                {
                    int need = (int)gap * Channels * 4;
                    if (outBuf.Length < need) outBuf = new byte[need];
                    Array.Clear(outBuf, 0, need);
                    _pipe.Write(outBuf, 0, need);
                    written += gap;
                }
            }
        }
        catch (IOException) { } // ffmpeg 閂咗 pipe
        catch (Exception ex) { Error = ex.Message; AppPaths.LogError("錄音", ex); }
        finally
        {
            _formatReady.Set();
            try { client?.Stop(); } catch { }
            try { _pipe.Dispose(); } catch { }
        }
    }

    private static byte[] ReadBytes(IntPtr p, int off, int n) { var b = new byte[n]; Marshal.Copy(p + off, b, 0, n); return b; }

    private static void ConvertToFloat(IntPtr data, int samples, int bits, byte[] outBuf)
    {
        for (int i = 0; i < samples; i++)
        {
            float v = bits switch
            {
                16 => Marshal.ReadInt16(data, i * 2) / 32768f,
                24 => ((Marshal.ReadByte(data, i * 3) | (Marshal.ReadByte(data, i * 3 + 1) << 8) | ((sbyte)Marshal.ReadByte(data, i * 3 + 2) << 16))) / 8388608f,
                32 => Marshal.ReadInt32(data, i * 4) / 2147483648f,
                _ => 0f
            };
            BitConverter.TryWriteBytes(outBuf.AsSpan(i * 4), v);
        }
    }

    private static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} 0x{hr:X8}"); }

    public void Dispose()
    {
        _stop = true;
        try { if (!_pipe.IsConnected) _pipe.Dispose(); } catch { }
        _thread.Join(1500);
    }
}

// ---------------------------------------------------------------------
//  錄影設定
// ---------------------------------------------------------------------
internal enum CaptureMode { Screen, Region, Window }

internal static class RecSettings
{
    private static string File_ => Path.Combine(AppPaths.Data, "recorder.txt");
    public static CaptureMode Mode = CaptureMode.Screen;
    public static int Monitor;
    public static Rectangle Region = Rectangle.Empty;
    public static string WinProcess = "", WinTitle = "";
    public static int Fps = 30;
    public static readonly int[] FpsList = { 15, 24, 30, 60, 90, 120, 144 };
    public static int Res; // 輸出高度：0 = 原始，720 / 1080 / 1440（2K）/ 2160（4K）
    public static readonly int[] ResList = { 0, 720, 1080, 1440, 2160 };
    public static bool SysAudio = true, Mic;
    public static int SysVol = 100, MicVol = 100; // 收音音量 %
    public static bool Cursor = true; // 錄埋滑鼠鼠標
    public static bool Clicks;        // 點擊位置加效果
    public static string Codec = "h264";  // h264 / hevc / av1
    public static bool Hw = true;         // 用顯示卡編碼（自動偵測）
    public static int Quality = 1;        // 0 最高 1 高 2 中 3 低
    public static string Format = "mp4";  // mp4 / mkv / avi
    public static readonly string[] Codecs = { "h264", "hevc", "av1" };
    public static readonly string[] Formats = { "mp4", "mkv", "avi" };

    public static string CodecName(string c) => c switch { "hevc" => "H.265 (HEVC)", "av1" => "AV1", _ => "H.264" };
    public static string QualityName(int q) => q switch { 0 => Lang.T("最高"), 1 => Lang.T("高"), 2 => Lang.T("中"), _ => Lang.T("低") };

    public static RecOpts Snapshot() => new()
    {
        Mode = Mode, Monitor = Monitor, Region = Region, WinProcess = WinProcess, WinTitle = WinTitle, Fps = Fps, Res = Res,
        SysVol = SysVol, MicVol = MicVol, Quality = Quality, SysAudio = SysAudio, Mic = Mic, Cursor = Cursor, Clicks = Clicks, Hw = Hw,
        Codec = Codec, Format = Format, Folder = Folder
    };

    public static string ResName(int r) => r switch
    {
        0 => Lang.T("原始"), 1440 => "2K", 2160 => "4K", _ => r + "P"
    };
    public static string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MultiTool");

    static RecSettings()
    {
        try
        {
            if (!File.Exists(File_)) return;
            foreach (var line in File.ReadAllLines(File_))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line[..eq], v = line[(eq + 1)..];
                int I(int d) => int.TryParse(v, out var x) ? x : d;
                switch (k)
                {
                    case "mode": Mode = (CaptureMode)Math.Clamp(I(0), 0, 2); break;
                    case "monitor": Monitor = I(0); break;
                    case "region":
                        var p = v.Split(',');
                        if (p.Length == 4 && p.All(x => int.TryParse(x, out _))) Region = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                        break;
                    case "winproc": WinProcess = v; break;
                    case "wintitle": WinTitle = v; break;
                    case "fps": Fps = Math.Clamp(I(30), 5, 240); break;
                    case "res": Res = ResList.Contains(I(0)) ? I(0) : 0; break;
                    case "cursor": Cursor = v != "0"; break;
                    case "clicks": Clicks = v == "1"; break;
                    case "codec": if (Codecs.Contains(v)) Codec = v; break;
                    case "hw": Hw = v != "0"; break;
                    case "quality2": Quality = Math.Clamp(I(1), 0, 3); break;
                    case "format": if (Formats.Contains(v)) Format = v; break;
                    case "sysvol": SysVol = Math.Clamp(I(100), 0, 300); break;
                    case "micvol": MicVol = Math.Clamp(I(100), 0, 300); break;
                    case "sys": SysAudio = v == "1"; break;
                    case "mic": Mic = v == "1"; break;
                    case "folder": if (v.Length > 0) Folder = v; break;
                }
            }
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllLines(File_, new[]
            {
                "mode=" + (int)Mode, "monitor=" + Monitor, $"region={Region.X},{Region.Y},{Region.Width},{Region.Height}",
                "winproc=" + WinProcess, "wintitle=" + WinTitle.Replace('\n', ' '), "fps=" + Fps, "res=" + Res,
                "sys=" + (SysAudio ? 1 : 0), "mic=" + (Mic ? 1 : 0), "sysvol=" + SysVol, "cursor=" + (Cursor ? 1 : 0), "clicks=" + (Clicks ? 1 : 0), "codec=" + Codec, "hw=" + (Hw ? 1 : 0), "quality2=" + Quality, "format=" + Format, "micvol=" + MicVol, "folder=" + Folder
            });
        }
        catch { }
    }
}

/// <summary>一次錄影用嘅設定（錄影工具 / 精彩重播各自一份）。</summary>
internal sealed class RecOpts
{
    public CaptureMode Mode;
    public int Monitor;
    public Rectangle Region;
    public string WinProcess = "", WinTitle = "";
    public int Fps = 30, Res, SysVol = 100, MicVol = 100, Quality = 1;
    public bool SysAudio = true, Mic, Cursor = true, Clicks, Hw = true;
    public string Codec = "h264", Format = "mp4", Folder = "";
    public string? ReplayDir;      // 有 = 精彩重播（循環錄細段）
    public int ReplaySeconds = 30;
}

// ---------------------------------------------------------------------
//  錄影（一次）
// ---------------------------------------------------------------------
internal sealed class RecordSession
{
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    private Process? _ff;
    private readonly List<AudioSource> _audio = new();
    private readonly StringBuilder _log = new();
    public string OutFile { get; private set; } = "";
    public DateTime Started { get; private set; }
    public const int ReplaySeg = 2;
    public bool Running => _ff is { HasExited: false };
    private readonly RecOpts _o;

    public RecordSession(RecOpts? o = null) { _o = o ?? RecSettings.Snapshot(); }

    /// <summary>而家要錄嘅範圍（螢幕座標）。</summary>
    public static Rectangle? TargetRect(RecOpts _o, out string? error)
    {
        error = null;
        var screens = Screen.AllScreens;
        switch (_o.Mode)
        {
            case CaptureMode.Region:
                if (_o.Region.Width < 16 || _o.Region.Height < 16) { error = Lang.T("未揀錄影範圍"); return null; }
                return _o.Region;
            case CaptureMode.Window:
            {
                var w = Scripts.Win.List().FirstOrDefault(x => x.Process == _o.WinProcess && x.Title == _o.WinTitle)
                        ?? Scripts.Win.List().FirstOrDefault(x => x.Process == _o.WinProcess);
                if (w == null) { error = Lang.F("搵唔到視窗（{0}），佢係咪閂咗？", _o.WinProcess); return null; }
                if (DwmGetWindowAttribute(w.Handle, 9, out var r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(w.Handle, out r);
                return Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
            }
            default:
                return screens[Math.Clamp(_o.Monitor, 0, screens.Length - 1)].Bounds;
        }
    }

    /// <summary>開始錄。失敗就 throw（訊息俾用戶睇）。</summary>
    public async Task StartAsync()
    {
        var rect = TargetRect(_o, out var err) ?? throw new InvalidOperationException(err);
        // 只可以喺一個螢幕入面錄：揀包含中心點嗰個，超出部分截走
        var screens = Screen.AllScreens;
        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        int mon = Array.FindIndex(screens, s => s.Bounds.Contains(center));
        if (mon < 0) mon = 0;
        var sb = screens[mon].Bounds;
        rect.Intersect(sb);
        if (rect.Width < 16 || rect.Height < 16) throw new InvalidOperationException(Lang.T("錄影範圍唔喺螢幕入面"));
        rect.Width -= rect.Width % 2;
        rect.Height -= rect.Height % 2;
        bool full = rect == sb;

        if (_o.ReplayDir != null) Directory.CreateDirectory(_o.ReplayDir);
        Directory.CreateDirectory(_o.Folder);
        OutFile = Path.Combine(_o.Folder, $"MultiTool {DateTime.Now:yyyy-MM-dd HH-mm-ss}.{_o.Format}");

        string codec = _o.Codec, enc = await Task.Run(() => Ffmpeg.Encoder(codec, _o.Hw));
        try { await RunAsync(rect, sb, mon, full, enc, useDda: true); }
        catch (InvalidOperationException) when (_log.ToString().Contains("ddagrab", StringComparison.OrdinalIgnoreCase) ||
                                                _log.ToString().Contains("Desktop Duplication", StringComparison.OrdinalIgnoreCase))
        {
            // 部分電腦（例如遠端桌面 / 舊顯示卡）冇 Desktop Duplication：改用 gdigrab
            await RunAsync(rect, sb, mon, full, enc, useDda: false);
        }
        if (_o.Clicks && _o.ReplayDir == null) { ClickFx.Start(); _clicks = true; }
    }

    private async Task RunAsync(Rectangle rect, Rectangle screen, int mon, bool full, string enc, bool useDda)
    {
        Cleanup();
        _log.Clear();
        int fps = _o.Fps;
        // 品質：數字越細越靚（檔案越大）；AV1 / HEVC 嘅刻度唔同 H.264
        int[] qs = enc switch
        {
            "libsvtav1" or "libaom-av1" => new[] { 22, 27, 32, 38 },
            "libx265" => new[] { 18, 22, 26, 31 },
            _ when enc.StartsWith("av1") => new[] { 20, 25, 30, 36 },
            _ => new[] { 16, 19, 23, 28 }
        };
        int q = qs[Math.Clamp(_o.Quality, 0, 3)];
        var args = new StringBuilder("-hide_banner -loglevel warning -y ");

        // 聲音（named pipe）
        if (_o.SysAudio) _audio.Add(new AudioSource(loopback: true));
        if (_o.Mic) _audio.Add(new AudioSource(loopback: false));
        var vols = new List<double>();
        foreach (var a in _audio.ToList())
            if (!a.Start()) { AppPaths.LogError("錄音", new Exception(a.Error)); a.Dispose(); _audio.Remove(a); }

        string vsrc;
        int vIndex;
        if (useDda)
        {
            string crop = full ? "" : $":offset_x={rect.X - screen.X}:offset_y={rect.Y - screen.Y}:video_size={rect.Width}x{rect.Height}";
            vsrc = $"ddagrab=output_idx={mon}:framerate={fps}:draw_mouse={(_o.Cursor ? 1 : 0)}{crop},hwdownload,format=bgra";
            vIndex = -1;
        }
        else
        {
            args.Append($"-f gdigrab -framerate {fps} -draw_mouse {(_o.Cursor ? 1 : 0)} -offset_x {rect.X} -offset_y {rect.Y} -video_size {rect.Width}x{rect.Height} -thread_queue_size 512 -i desktop ");
            vsrc = "[0:v]null";
            vIndex = 0;
        }
        int ai = vIndex + 1;
        var aLabels = new List<string>();
        foreach (var a in _audio)
        {
            args.Append($"-thread_queue_size 2048 -f f32le -ar {a.Rate} -ac {a.Channels} -i \"{a.PipePath}\" ");
            aLabels.Add($"[{ai++}:a]");
            vols.Add((a.Loopback ? _o.SysVol : _o.MicVol) / 100.0);
        }
        bool hw = !enc.StartsWith("lib");
        // 解像度：只會縮細（原本細過揀嘅就保持原本），闊度跟比例、雙數
        string scale = _o.Res > 0 ? $",scale=-2:'min(ih,{_o.Res})':flags=lanczos" : "";
        var fc = new StringBuilder($"{vsrc}{scale},format={(hw ? "nv12" : "yuv420p")}[v]");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int k = 0; k < aLabels.Count; k++)
            fc.Append($";{aLabels[k]}volume={vols[k].ToString("0.##", inv)}[a{k}]");
        if (aLabels.Count == 2) fc.Append(";[a0][a1]amix=inputs=2:duration=longest:normalize=0[a]");
        else if (aLabels.Count == 1) fc.Append(";[a0]anull[a]");
        args.Append($"-filter_complex \"{fc}\" -map \"[v]\" ");
        string fmt = Path.GetExtension(OutFile).TrimStart('.').ToLowerInvariant();
        if (aLabels.Count > 0)
            args.Append(fmt == "avi" ? "-map \"[a]\" -c:a libmp3lame -b:a 192k " : "-map \"[a]\" -c:a aac -b:a 160k ");
        args.Append(enc switch
        {
            _ when enc.EndsWith("_nvenc") => $"-c:v {enc} -preset p4 -rc vbr -cq {q} -b:v 0 ",
            _ when enc.EndsWith("_qsv") => $"-c:v {enc} -global_quality {q} ",
            _ when enc.EndsWith("_amf") => $"-c:v {enc} -rc cqp -qp_i {q} -qp_p {q} ",
            "libsvtav1" => $"-c:v libsvtav1 -preset 10 -crf {q} ",
            "libaom-av1" => $"-c:v libaom-av1 -cpu-used 8 -row-mt 1 -crf {q} -b:v 0 ",
            "libx265" => $"-c:v libx265 -preset veryfast -crf {q} ",
            _ => $"-c:v libx264 -preset veryfast -crf {q} "
        });
        if (fmt == "mp4" && enc.Contains("hevc") || enc == "libx265" && fmt == "mp4") args.Append("-tag:v hvc1 ");
        args.Append($"-g {fps * 2} ");
        if (_o.ReplayDir != null)
        {
            // 精彩重播：一直錄成好多段 2 秒嘅細檔，循環覆蓋，儲存時先駁埋
            int wrap = _o.ReplaySeconds / ReplaySeg + 3;
            args.Append($"-r {fps} -f segment -segment_time {ReplaySeg} -segment_wrap {wrap} -reset_timestamps 1 -segment_format mpegts \"{Path.Combine(_o.ReplayDir, "rp%03d.ts")}\"");
        }
        else args.Append($"-r {fps} " + (fmt == "mp4" ? "-movflags +faststart " : "") + $"\"{OutFile}\"");

        var psi = new ProcessStartInfo(Ffmpeg.Exe, args.ToString())
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        _ff = Process.Start(psi)!;
        _ff.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_log) { _log.AppendLine(e.Data); if (_log.Length > 20000) _log.Remove(0, 10000); } };
        _ff.OutputDataReceived += (_, _) => { };
        _ff.BeginErrorReadLine();
        _ff.BeginOutputReadLine();
        Started = DateTime.Now;
        // 頭 1.5 秒閂咗 = 出錯
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(100);
            if (_ff.HasExited)
            {
                Cleanup();
                string tail = LogTail();
                AppPaths.LogError("錄影", new Exception(tail));
                throw new InvalidOperationException(Lang.T("ffmpeg 開唔到：") + tail);
            }
        }
    }

    public string LogTail()
    {
        lock (_log)
        {
            var lines = _log.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 4)));
        }
    }

    /// <summary>停：叫 ffmpeg 收尾（寫好 MP4），最多等 15 秒。</summary>
    private bool _clicks;

    public async Task StopAsync()
    {
        if (_clicks) { _clicks = false; ClickFx.Stop(); }
        if (_ff == null) return;
        try
        {
            if (!_ff.HasExited)
            {
                await _ff.StandardInput.WriteAsync("q");
                await _ff.StandardInput.FlushAsync();
                _ff.StandardInput.Close();
            }
        }
        catch { }
        var exit = _ff.WaitForExitAsync();
        if (await Task.WhenAny(exit, Task.Delay(15000)) != exit) { try { _ff.Kill(true); } catch { } }
        Cleanup();
    }

    private void Cleanup()
    {
        foreach (var a in _audio) a.Dispose();
        _audio.Clear();
    }
}

// ---------------------------------------------------------------------
//  拉框揀範圍
// ---------------------------------------------------------------------
internal sealed class RegionPicker : Form
{
    private Point _start;
    private Rectangle _rect;
    private bool _drag;
    public Rectangle Result { get; private set; }

    private readonly string _prompt;

    public RegionPicker(string? prompt = null)
    {
        _prompt = prompt ?? Lang.T("拉一個框揀錄影範圍（Esc 取消）");
        var vs = SystemInformation.VirtualScreen;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = vs;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        Opacity = 0.35;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) { DialogResult = DialogResult.Cancel; Close(); return; }
        _drag = true;
        _start = e.Location;
        _rect = Rectangle.Empty;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_drag) return;
        _rect = Rectangle.FromLTRB(Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y), Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_drag) return;
        _drag = false;
        if (_rect.Width < 16 || _rect.Height < 16) return;
        Result = new Rectangle(_rect.X + Left, _rect.Y + Top, _rect.Width, _rect.Height);
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var f = new Font(Ui.UiFamily, 14f, FontStyle.Bold);
        TextRenderer.DrawText(g, _prompt, f, new Rectangle(0, Height / 2 - 40, Width, 40), Color.White,
            TextFormatFlags.HorizontalCenter);
        if (_rect.Width > 0)
        {
            using var b = new SolidBrush(Color.FromArgb(255, 80, 140, 255));
            g.FillRectangle(b, _rect);
            using var pen = new Pen(Color.White, 2f);
            g.DrawRectangle(pen, _rect);
            TextRenderer.DrawText(g, $"{_rect.Width} × {_rect.Height}", Font, new Point(_rect.X + 6, _rect.Y + 6), Color.White);
        }
    }
}

// ---------------------------------------------------------------------
//  錄緊時右上角細窗（唔會錄入片）
// ---------------------------------------------------------------------
internal sealed class RecordBar : Form
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    private readonly Label _text;
    public event Action? StopClicked;
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000008 | 0x80; // NOACTIVATE | TOPMOST | TOOLWINDOW
            return cp;
        }
    }

    public RecordBar()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Panel;
        Size = new Size(Ui.S(230), Ui.S(46));
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.Right - Width - Ui.S(16), wa.Top + Ui.S(16));
        _text = new Label { ForeColor = Ui.Danger, Font = Ui.F(10f, FontStyle.Bold), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        _text.SetBounds(Ui.S(12), 0, Ui.S(110), Height);
        var stop = new FancyButton { Text = Lang.T("■ 停止"), Kind = BtnKind.Danger, Font = Ui.F(9.5f) };
        stop.SetBounds(Width - Ui.S(96), Ui.S(7), Ui.S(86), Ui.S(32));
        stop.Click += (_, _) => StopClicked?.Invoke();
        Controls.Add(_text);
        Controls.Add(stop);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { SetWindowDisplayAffinity(Handle, 0x11 /*WDA_EXCLUDEFROMCAPTURE*/); } catch { }
    }

    public void SetTime(TimeSpan t) => _text.Text = "● " + (t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss"));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Ui.Danger, 2f);
        e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
    }
}

// ---------------------------------------------------------------------
//  錄影工具頁
// ---------------------------------------------------------------------
public sealed class RecordPage : Panel
{
    private readonly CardStack _stack = new();
    private readonly FancyButton _start, _area, _fps, _res, _folderBtn, _codec, _qual, _format;
    private readonly CheckBox _sys, _mic, _cursor;
    private readonly Label _status, _last, _folderLabel;
    private readonly FancyButton _play, _delete;
    private readonly HotkeyBox _hotkey;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private RecordSession? _session;
    private RecordBar? _bar;
    private bool _busy, _hkWas;
    private string? _lastFile;

    public static Control Build() => new RecordPage();

    private RecordPage()
    {
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        _stack.Dock = DockStyle.Fill;

        FancyButton Btn(string text, Action click, int w = 150)
        {
            var b = new FancyButton { Text = text, Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(w), Ui.S(34)) };
            b.Click += (_, _) => click();
            return b;
        }
        Label L(string t) => Ui.Text_(t, Ui.TextDim, Ui.F(9.75f), new Padding(0, Ui.S(9), Ui.S(4), 0));
        FlowLayoutPanel Row(params Control[] cs)
        {
            var r = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, WrapContents = true, Margin = new Padding(0, 0, 0, Ui.S(10)) };
            foreach (var c in cs) { if (c is not Label) c.Margin = new Padding(0, 0, Ui.S(10), 0); r.Controls.Add(c); }
            return r;
        }

        _area = Btn("", ShowAreaMenu, 260);
        _fps = Btn("", ShowFpsMenu, 100);
        _res = Btn("", ShowResMenu, 130);
        _codec = Btn("", ShowCodecMenu, 170);
        _qual = Btn("", () => ShowPick(_qual, new[] { 0, 1, 2, 3 }, RecSettings.Quality, RecSettings.QualityName,
            v => RecSettings.Quality = v, Lang.T("越高越靚，檔案越大")), 110);
        _format = Btn("", () => ShowPick(_format, Enumerable.Range(0, RecSettings.Formats.Length).ToArray(),
            Array.IndexOf(RecSettings.Formats, RecSettings.Format), i => RecSettings.Formats[i].ToUpperInvariant(),
            i => RecSettings.Format = RecSettings.Formats[i], Lang.T("MKV 中途閂機都唔會壞；AVI 建議配 H.264")), 110);
        _sys = new CheckBox { Text = Lang.T("電腦聲音"), Checked = RecSettings.SysAudio, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, Font = Ui.F(9.75f), Margin = new Padding(Ui.S(6), Ui.S(8), Ui.S(12), 0) };
        _sys.CheckedChanged += (_, _) => { RecSettings.SysAudio = _sys.Checked; RecSettings.Save(); };
        _mic = new CheckBox { Text = Lang.T("咪高峰"), Checked = RecSettings.Mic, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, Font = Ui.F(9.75f), Margin = new Padding(0, Ui.S(8), Ui.S(12), 0) };
        _mic.CheckedChanged += (_, _) => { RecSettings.Mic = _mic.Checked; RecSettings.Save(); };
        // 收音音量 %
        Control Vol(int value, Action<int> set)
        {
            var nb = new NumberBox(Ui.S(84)) { Minimum = 0, Maximum = 300, Increment = 10, Value = value };
            nb.ValueChanged += (_, _) => { set((int)nb.Value); RecSettings.Save(); };
            var wrap = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, WrapContents = false, Margin = new Padding(0, 0, Ui.S(18), 0) };
            nb.Margin = new Padding(0);
            wrap.Controls.Add(nb);
            wrap.Controls.Add(Ui.Text_("%", Ui.TextDim, Ui.F(9.75f), new Padding(Ui.S(4), Ui.S(9), 0, 0)));
            return wrap;
        }
        var sysVol = Vol(RecSettings.SysVol, v => RecSettings.SysVol = v);
        var micVol = Vol(RecSettings.MicVol, v => RecSettings.MicVol = v);
        _folderLabel = Ui.Text_("", Ui.Text, Ui.F(9.5f), new Padding(0, Ui.S(9), Ui.S(8), 0));
        _folderBtn = Btn(Lang.T("更改…"), ChangeFolder, 80);
        var openFolder = Btn(Lang.T("開啟資料夾"), () => Open(RecSettings.Folder, folder: true), 110);
        _hotkey = new HotkeyBox();
        HotkeyHub.Bind(_hotkey, "recorder.toggle");
        HotkeyHub.Get("recorder.toggle").Action = () => _ = ToggleAsync();
        var hkHost = new InputHost(_hotkey, Ui.S(140));
        _start = new FancyButton { Text = Lang.T("● 開始錄影"), Kind = BtnKind.Danger, Font = Ui.F(10.5f, FontStyle.Bold), Size = new Size(Ui.S(150), Ui.S(40)) };
        _start.Click += async (_, _) => await ToggleAsync();
        _status = Ui.Text_("", Ui.TextDim, Ui.F(9.5f), new Padding(Ui.S(4), Ui.S(12), 0, 0));

        var col = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        col.Controls.Add(Row(L(Lang.T("錄影範圍")), _area));
        _cursor = new CheckBox { Text = Lang.T("錄製鼠標"), Checked = RecSettings.Cursor, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, Font = Ui.F(9.75f) };
        _cursor.CheckedChanged += (_, _) => { RecSettings.Cursor = _cursor.Checked; RecSettings.Save(); };
        var picRow = Row(L(Lang.T("畫面")), _fps, _res);
        _cursor.Margin = new Padding(Ui.S(6), Ui.S(8), Ui.S(12), 0);
        picRow.Controls.Add(_cursor);
        var clicks = new CheckBox { Text = Lang.T("錄製滑鼠點擊"), Checked = RecSettings.Clicks, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, Font = Ui.F(9.75f),
            Margin = new Padding(0, Ui.S(8), Ui.S(12), 0) };
        clicks.CheckedChanged += (_, _) => { RecSettings.Clicks = clicks.Checked; RecSettings.Save(); };
        new ToolTip().SetToolTip(clicks, Lang.T("撳滑鼠嗰陣，點擊位置會出一個擴散圓圈（左鍵橙、右鍵藍），睇片更易見到撳咗邊度。"));
        picRow.Controls.Add(clicks);
        col.Controls.Add(picRow);
        col.Controls.Add(Row(L(Lang.T("編碼")), _codec, _qual, _format));
        _sys.Margin = new Padding(0, Ui.S(8), Ui.S(6), 0);
        _mic.Margin = new Padding(0, Ui.S(8), Ui.S(6), 0);
        var audioRow = Row(L(Lang.T("聲音")));
        foreach (var c in new[] { _sys, sysVol, _mic, micVol }) audioRow.Controls.Add(c);
        col.Controls.Add(audioRow);
        col.Controls.Add(Row(L(Lang.T("儲存位置")), _folderLabel, _folderBtn, openFolder));
        col.Controls.Add(Row(L(Lang.T("開始 / 停止快捷鍵")), hkHost));
        col.Controls.Add(Row(_start, _status));
        var card = new CardPanel { Title = Lang.T("錄影工具"), Hint = Lang.T("錄緊時右上角會有計時器（唔會錄入片）；撳「停止」或者快捷鍵完成，影片存做 MP4。") };
        card.SetContent(col);
        _stack.AddRow(card);

        _last = Ui.Text_(Lang.T("未有錄影"), Ui.TextDim, Ui.F(9.75f), new Padding(0, Ui.S(9), Ui.S(10), 0));
        _play = Btn(Lang.T("▶ 播放"), () => { if (_lastFile != null) Open(_lastFile, folder: false); }, 90);
        _delete = Btn(Lang.T("刪除"), DeleteLast, 80);
        var lastCard = new CardPanel { Title = Lang.T("最近錄影") };
        lastCard.SetContent(Row(_last, _play, _delete));
        _stack.AddRow(lastCard);
        Controls.Add(_stack);

        _lastFile = LatestFile();
        UpdateLabels();
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        HandleDestroyed += async (_, _) => { _timer.Stop(); if (_session != null) await _session.StopAsync(); };
    }

    /// <summary>刪除最近一條錄影（放入資源回收筒），之後顯示再上一條。</summary>
    private void DeleteLast()
    {
        if (_lastFile == null || _session != null) return;
        if (MessageBox.Show(FindForm(), Lang.F("刪除「{0}」？\n（會放入資源回收筒）", Path.GetFileName(_lastFile)), Program.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            if (File.Exists(_lastFile))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(_lastFile,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            _status.Text = Lang.T("已刪除：") + Path.GetFileName(_lastFile);
        }
        catch (Exception ex)
        {
            _status.Text = Lang.T("刪除唔到：") + ex.Message;
            return;
        }
        _lastFile = LatestFile();
        UpdateLabels();
    }

    private static string? LatestFile()
    {
        try
        {
            if (!Directory.Exists(RecSettings.Folder)) return null;
            return new DirectoryInfo(RecSettings.Folder).GetFiles("*.*").Where(f => RecSettings.Formats.Contains(f.Extension.TrimStart('.').ToLowerInvariant())).OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    private void UpdateLabels()
    {
        _area.Text = RecSettings.Mode switch
        {
            CaptureMode.Region => Lang.F("範圍 {0}×{1}", RecSettings.Region.Width, RecSettings.Region.Height),
            CaptureMode.Window => Lang.T("視窗：") + RecSettings.WinProcess + (RecSettings.WinTitle.Length > 0 ? " - " + RecSettings.WinTitle : ""),
            _ => Screen.AllScreens.Length > 1 ? Lang.F("全螢幕（螢幕 {0}）", RecSettings.Monitor + 1) : Lang.T("全螢幕")
        } + "  ▾";
        _fps.Text = RecSettings.Fps + " FPS ▾";
        _res.Text = Lang.T("解像度：") + RecSettings.ResName(RecSettings.Res) + " ▾";
        _codec.Text = RecSettings.CodecName(RecSettings.Codec) + (RecSettings.Hw ? "" : " · CPU") + " ▾";
        _qual.Text = Lang.T("品質：") + RecSettings.QualityName(RecSettings.Quality) + " ▾";
        _format.Text = Lang.T("格式：") + RecSettings.Format.ToUpperInvariant() + " ▾";
        _folderLabel.Text = RecSettings.Folder;
        _last.Text = _lastFile != null ? Path.GetFileName(_lastFile) : Lang.T("未有錄影");
        _play.Visible = _delete.Visible = _lastFile != null;
        foreach (var b in new[] { _area, _fps, _res, _codec, _qual, _format }) b.Invalidate();
    }

    private ContextMenuStrip NewMenu()
    {
        var menu = new ContextMenuStrip();
        try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        menu.BackColor = Ui.CardHi;
        menu.ForeColor = Ui.Text;
        menu.Closed += (_, _) => BeginInvoke(() => menu.Dispose());
        return menu;
    }

    private void ShowPick(Control at, int[] values, int current, Func<int, string> name, Action<int> set, string? note)
    {
        if (_session != null) return;
        var menu = NewMenu();
        foreach (int v0 in values)
        {
            int v = v0;
            var it = new ToolStripMenuItem(name(v)) { Checked = v == current, ForeColor = Ui.Text };
            it.Click += (_, _) => { set(v); RecSettings.Save(); UpdateLabels(); };
            menu.Items.Add(it);
        }
        if (note != null)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(note) { Enabled = false });
        }
        menu.Show(at, new Point(0, at.Height));
    }

    private void ShowCodecMenu()
    {
        if (_session != null) return;
        var menu = NewMenu();
        foreach (var c0 in RecSettings.Codecs)
        {
            string c = c0;
            string note = c switch
            {
                "hevc" => Lang.T("（檔案細啲，部分舊機播唔到）"),
                "av1" => Lang.T("（檔案最細，要新顯示卡）"),
                _ => Lang.T("（最通用）")
            };
            var it = new ToolStripMenuItem(RecSettings.CodecName(c) + "  " + note) { Checked = RecSettings.Codec == c, ForeColor = Ui.Text };
            it.Click += (_, _) => { RecSettings.Codec = c; RecSettings.Save(); UpdateLabels(); };
            menu.Items.Add(it);
        }
        menu.Items.Add(new ToolStripSeparator());
        var gpu = new ToolStripMenuItem(Lang.T("顯示卡加速（自動偵測）")) { Checked = RecSettings.Hw, ForeColor = Ui.Text };
        gpu.Click += (_, _) => { RecSettings.Hw = true; RecSettings.Save(); UpdateLabels(); };
        var cpu = new ToolStripMenuItem(Lang.T("只用 CPU（慢啲，但最穩定）")) { Checked = !RecSettings.Hw, ForeColor = Ui.Text };
        cpu.Click += (_, _) => { RecSettings.Hw = false; RecSettings.Save(); UpdateLabels(); };
        menu.Items.Add(gpu);
        menu.Items.Add(cpu);
        menu.Show(_codec, new Point(0, _codec.Height));
    }

    private void ShowFpsMenu()
    {
        if (_session != null) return;
        var menu = new ContextMenuStrip();
        try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        menu.BackColor = Ui.CardHi;
        menu.ForeColor = Ui.Text;
        foreach (int f in RecSettings.FpsList)
        {
            int v = f;
            var it = new ToolStripMenuItem(f + " FPS") { Checked = RecSettings.Fps == f, ForeColor = Ui.Text };
            it.Click += (_, _) => { RecSettings.Fps = v; RecSettings.Save(); UpdateLabels(); };
            menu.Items.Add(it);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Lang.T("高過螢幕更新率冇用；越高檔案越大")) { Enabled = false });
        menu.Closed += (_, _) => BeginInvoke(() => menu.Dispose());
        menu.Show(_fps, new Point(0, _fps.Height));
    }

    private void ShowResMenu()
    {
        if (_session != null) return;
        var menu = new ContextMenuStrip();
        try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        menu.BackColor = Ui.CardHi;
        menu.ForeColor = Ui.Text;
        foreach (int r in RecSettings.ResList)
        {
            int v = r;
            string extra = r switch { 0 => Lang.T("（同錄影範圍一樣大）"), 1440 => " (1440P)", 2160 => " (2160P)", _ => "" };
            var it = new ToolStripMenuItem(RecSettings.ResName(r) + extra) { Checked = RecSettings.Res == r, ForeColor = Ui.Text };
            it.Click += (_, _) => { RecSettings.Res = v; RecSettings.Save(); UpdateLabels(); };
            menu.Items.Add(it);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Lang.T("比錄影範圍大嘅解像度唔會放大")) { Enabled = false });
        menu.Closed += (_, _) => BeginInvoke(() => menu.Dispose());
        menu.Show(_res, new Point(0, _res.Height));
    }

    private void ShowAreaMenu()
    {
        if (_session != null) return;
        var menu = new ContextMenuStrip();
        try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        menu.BackColor = Ui.CardHi;
        menu.ForeColor = Ui.Text;
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            int idx = i;
            var s = screens[i];
            var it = new ToolStripMenuItem(screens.Length > 1 ? Lang.F("全螢幕（螢幕 {0}，{1}×{2}）", i + 1, s.Bounds.Width, s.Bounds.Height) : Lang.T("全螢幕"))
            { Checked = RecSettings.Mode == CaptureMode.Screen && RecSettings.Monitor == i };
            it.Click += (_, _) => { RecSettings.Mode = CaptureMode.Screen; RecSettings.Monitor = idx; RecSettings.Save(); UpdateLabels(); };
            menu.Items.Add(it);
        }
        var region = new ToolStripMenuItem(Lang.T("揀範圍…")) { Checked = RecSettings.Mode == CaptureMode.Region };
        region.Click += (_, _) => BeginInvoke(() =>
        {
            using var p = new RegionPicker();
            if (p.ShowDialog(FindForm()) != DialogResult.OK) return;
            RecSettings.Mode = CaptureMode.Region;
            RecSettings.Region = p.Result;
            RecSettings.Save();
            UpdateLabels();
        });
        menu.Items.Add(region);
        var win = new ToolStripMenuItem(Lang.T("揀視窗")) { Checked = RecSettings.Mode == CaptureMode.Window };
        foreach (var w in Scripts.Win.List())
        {
            var info = w;
            string t = $"{w.Process} - {w.Title}";
            if (t.Length > 70) t = t[..70] + "…";
            var it = new ToolStripMenuItem(t) { BackColor = Ui.CardHi, ForeColor = Ui.Text };
            it.Click += (_, _) =>
            {
                RecSettings.Mode = CaptureMode.Window;
                RecSettings.WinProcess = info.Process;
                RecSettings.WinTitle = info.Title;
                RecSettings.Save();
                UpdateLabels();
            };
            win.DropDownItems.Add(it);
        }
        try { win.DropDown.Renderer = DarkTheme.MenuRenderer(); } catch { }
        win.DropDown.BackColor = Ui.CardHi;
        if (win.DropDownItems.Count == 0) win.Enabled = false;
        menu.Items.Add(win);
        menu.Closed += (_, _) => BeginInvoke(() => menu.Dispose());
        menu.Show(_area, new Point(0, _area.Height + Ui.S(2)));
    }

    private void ChangeFolder()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = RecSettings.Folder };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        RecSettings.Folder = dlg.SelectedPath;
        RecSettings.Save();
        UpdateLabels();
    }

    private static void Open(string path, bool folder)
    {
        try
        {
            if (folder) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); }
            else if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    private async Task TickAsync()
    {
        // 快捷鍵由 HotkeyHub 處理（設定頁可以揀頁面外用唔用得）
        if (_session != null)
        {
            var t = DateTime.Now - _session.Started;
            _bar?.SetTime(t);
            _status.Text = Lang.F("錄緊 {0}", t.ToString(@"mm\:ss"));
            if (!_session.Running && !_busy)
            {
                // ffmpeg 自己停咗（例如出錯）
                var tail = _session.LogTail();
                await FinishAsync();
                _status.Text = Lang.T("錄影中斷：") + tail;
            }
        }
    }

    private async Task ToggleAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (_session != null) { await FinishAsync(); return; }
            if (!Ffmpeg.Ready)
            {
                _status.Text = Lang.T("未下載 ffmpeg：請去「工具庫」重新安裝錄影工具。");
                return;
            }
            _status.Text = Lang.T("準備緊…");
            _start.Enabled = false;
            var s = new RecordSession();
            try { await s.StartAsync(); }
            catch (Exception ex)
            {
                _status.Text = Lang.T("開始唔到：") + ex.Message;
                _start.Enabled = true;
                return;
            }
            _session = s;
            _start.Text = Lang.T("■ 停止錄影");
            _start.Kind = BtnKind.Default;
            _start.Enabled = true;
            _start.Invalidate();
            _bar = new RecordBar();
            _bar.StopClicked += async () => await ToggleAsync();
            _bar.Show();
        }
        finally { _busy = false; }
    }

    private async Task FinishAsync()
    {
        var s = _session;
        if (s == null) return;
        _session = null;
        _status.Text = Lang.T("儲存緊…");
        try { _bar?.Close(); _bar?.Dispose(); } catch { }
        _bar = null;
        await s.StopAsync();
        _start.Text = Lang.T("● 開始錄影");
        _start.Kind = BtnKind.Danger;
        _start.Invalidate();
        if (File.Exists(s.OutFile) && new FileInfo(s.OutFile).Length > 0)
        {
            _lastFile = s.OutFile;
            _status.Text = Lang.T("已儲存：") + Path.GetFileName(s.OutFile);
        }
        else _status.Text = Lang.T("錄影失敗：") + s.LogTail();
        UpdateLabels();
    }
}
