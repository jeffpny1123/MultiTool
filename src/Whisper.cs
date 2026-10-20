using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTool.Ai;

/// <summary>用 Windows waveIn 錄咪（16 kHz、單聲道、16-bit），錄完寫成 wav。</summary>
internal sealed class WaveRecorder : IDisposable
{
    private const uint WAVE_MAPPER = 0xFFFFFFFF;
    private const int Rate = 16000, BufMs = 100, BufCount = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR { public nint lpData; public uint dwBufferLength, dwBytesRecorded; public nint dwUser; public uint dwFlags, dwLoops; public nint lpNext, reserved; }

    [DllImport("winmm.dll")] private static extern int waveInOpen(out nint h, uint dev, ref WAVEFORMATEX fmt, nint cb, nint inst, uint flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInStart(nint h);
    [DllImport("winmm.dll")] private static extern int waveInReset(nint h);
    [DllImport("winmm.dll")] private static extern int waveInClose(nint h);

    private static readonly int HdrSize = Marshal.SizeOf<WAVEHDR>();
    private static readonly int OffFlags = (int)Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwFlags));
    private static readonly int OffRecorded = (int)Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwBytesRecorded));

    private nint _h;
    private readonly nint[] _hdrs = new nint[BufCount], _data = new nint[BufCount];
    private readonly MemoryStream _pcm = new();
    private readonly List<float> _levels = new();

    /// <summary>由第 from 格開始嘅音量（每格 20 ms）。</summary>
    public List<float> LevelsFrom(int from)
    {
        lock (_levels) return from < _levels.Count ? _levels.GetRange(from, _levels.Count - from) : new List<float>();
    }
    private Thread? _thread;
    private volatile bool _running;
    private int _next;

    public TimeSpan Length => TimeSpan.FromSeconds(_pcm.Length / 2.0 / Rate);

    /// <summary>開始錄音；開唔到咪就 throw。</summary>
    public void Start()
    {
        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = Rate, nAvgBytesPerSec = Rate * 2, nBlockAlign = 2, wBitsPerSample = 16 };
        int r = waveInOpen(out _h, WAVE_MAPPER, ref fmt, 0, 0, 0);
        if (r != 0) throw new InvalidOperationException(Lang.F("開唔到咪高峰（錯誤 {0}）。請檢查 Windows「私隱 → 麥克風」有冇俾桌面程式用。", r));
        int bytes = Rate * 2 * BufMs / 1000;
        for (int i = 0; i < BufCount; i++)
        {
            _data[i] = Marshal.AllocHGlobal(bytes);
            _hdrs[i] = Marshal.AllocHGlobal(HdrSize);
            Marshal.StructureToPtr(new WAVEHDR { lpData = _data[i], dwBufferLength = (uint)bytes }, _hdrs[i], false);
            waveInPrepareHeader(_h, _hdrs[i], HdrSize);
            waveInAddBuffer(_h, _hdrs[i], HdrSize);
        }
        _running = true;
        waveInStart(_h);
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    private bool Collect()
    {
        nint hdr = _hdrs[_next];
        if ((Marshal.ReadInt32(hdr, OffFlags) & 1) == 0) return false; // WHDR_DONE
        int n = Marshal.ReadInt32(hdr, OffRecorded);
        if (n > 0)
        {
            var buf = new byte[n];
            Marshal.Copy(_data[_next], buf, 0, n);
            lock (_pcm) _pcm.Write(buf, 0, n);
            // 每 20 ms 計一次音量（RMS），俾自動停止用
            lock (_levels)
                for (int off = 0; off + 640 <= n; off += 640)
                {
                    double sum = 0;
                    for (int k = off; k < off + 640; k += 2)
                    {
                        short v = (short)(buf[k] | (buf[k + 1] << 8));
                        sum += (double)v * v;
                    }
                    _levels.Add((float)Math.Sqrt(sum / 320));
                }
        }
        if (_running)
        {
            Marshal.WriteInt32(hdr, OffRecorded, 0);
            waveInAddBuffer(_h, hdr, HdrSize);
        }
        else Marshal.WriteInt32(hdr, OffFlags, 0);
        _next = (_next + 1) % BufCount;
        return true;
    }

    private void Loop()
    {
        while (_running)
        {
            while (_running && Collect()) { }
            Thread.Sleep(15);
        }
    }

    /// <summary>停止，返錄到嘅 wav 檔路徑。</summary>
    public string Stop(string path)
    {
        if (_h == 0) throw new InvalidOperationException("not started");
        _running = false;
        _thread?.Join(500);
        waveInReset(_h); // 未填滿嘅 buffer 都會標記做 done
        for (int i = 0; i < BufCount && Collect(); i++) { }
        Free();
        byte[] pcm;
        lock (_pcm) pcm = _pcm.ToArray();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
        return path;
    }

    private void Free()
    {
        if (_h == 0) return;
        for (int i = 0; i < BufCount; i++)
        {
            if (_hdrs[i] != 0) { waveInUnprepareHeader(_h, _hdrs[i], HdrSize); Marshal.FreeHGlobal(_hdrs[i]); _hdrs[i] = 0; }
            if (_data[i] != 0) { Marshal.FreeHGlobal(_data[i]); _data[i] = 0; }
        }
        waveInClose(_h);
        _h = 0;
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        if (_h != 0) { waveInReset(_h); Free(); }
    }
}

/// <summary>本機語音辨識：whisper.cpp（whisper-cli.exe）+ ggml 模型，第一次用要下載。</summary>
internal static class Whisper
{
    public sealed record Model(string Key, string File, int SizeMb, string Label);

    public static readonly Model[] Models =
    {
        new("base", "ggml-base.bin", 148, "base · 148 MB"),
        new("small", "ggml-small.bin", 488, "small · 488 MB"),
        new("turbo", "ggml-large-v3-turbo-q5_0.bin", 574, "large-v3-turbo · 574 MB"),
    };

    private const string BinUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static string Dir => Path.Combine(AiProvider.WorkDir, "whisper");
    private static string BinDir => Path.Combine(Dir, "bin");
    private static string Cli => Path.Combine(BinDir, "whisper-cli.exe");
    public static Model Current => Models.FirstOrDefault(m => m.Key == VoiceSettings.Model) ?? Models[2];
    private static string ModelPath(Model m) => Path.Combine(Dir, m.File);

    public static bool BinReady => File.Exists(Cli) && File.Exists(Path.Combine(BinDir, "whisper-server.exe"));
    public static bool ModelReady(Model m) => File.Exists(ModelPath(m));

    /// <summary>刪除模型檔（先閂咗辨識程式，否則檔案被鎖住）。</summary>
    public static bool DeleteModel(Model m)
    {
        WhisperServer.Stop();
        for (int i = 0; i < 10; i++)
        {
            try { if (File.Exists(ModelPath(m))) File.Delete(ModelPath(m)); return true; }
            catch { System.Threading.Thread.Sleep(200); } // 等 server 完全閂咗
        }
        return false;
    }
    public static bool Ready => BinReady && ModelReady(Current);

    private static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MultiTool");
        return c;
    }

    private static async Task DownloadAsync(string url, string dest, Action<long, long> progress, CancellationToken ct)
    {
        string tmp = dest + ".part";
        using (var http = NewClient())
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress(done, total);
            }
        }
        File.Move(tmp, dest, true);
    }

    /// <summary>下載程式（如果未有）同揀咗嘅模型。progress：(說明, 0–1 或 -1)。</summary>
    public static async Task InstallAsync(Model m, Action<string, double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        if (!BinReady)
        {
            WhisperServer.Stop();
            string zip = Path.Combine(Dir, "whisper-bin.zip");
            await DownloadAsync(BinUrl, zip, (d, t) => progress(Lang.T("下載語音辨識程式…"), t > 0 ? (double)d / t : -1), ct);
            Directory.CreateDirectory(BinDir);
            using (var z = ZipFile.OpenRead(zip))
                foreach (var e in z.Entries)
                {
                    if (e.Name.Length == 0) continue;
                    if (!e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && e.Name is not ("whisper-cli.exe" or "whisper-server.exe")) continue;
                    e.ExtractToFile(Path.Combine(BinDir, e.Name), true);
                }
            File.Delete(zip);
        }
        if (!ModelReady(m))
            await DownloadAsync(ModelUrl + m.File, ModelPath(m),
                (d, t) => progress(Lang.F("下載模型 {0}…", m.Label), t > 0 ? (double)d / t : -1), ct);
        progress(Lang.T("完成"), 1);
    }

    /// <summary>將 wav 轉做文字（自動判斷語言）。</summary>
    public static async Task<string> TranscribeAsync(string wav, Action<Process> started)
    {
        string outBase = Path.Combine(Dir, "last");
        string txt = outBase + ".txt";
        try { File.Delete(txt); } catch { }
        int threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        var psi = new ProcessStartInfo(Cli,
            $"-m \"{ModelPath(Current)}\" -f \"{wav}\" -l auto -t {threads} -nt -np -otxt -of \"{outBase}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = BinDir
        };
        using var p = Process.Start(psi)!;
        started(p);
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        await o;
        string err = await e;
        if (!File.Exists(txt))
            throw new InvalidOperationException(Lang.T("語音辨識失敗：") + (err.Length > 200 ? err[^200..] : err).Trim());
        return string.Join(" ", File.ReadAllLines(txt, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0)).Trim();
    }
}
