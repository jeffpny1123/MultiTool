using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using MultiTool.Common;
using MultiTool.UI;

namespace MultiTool.Tools;

// =====================================================================
//  效能監控：CPU / GPU / RAM / VRAM / 磁碟 / 網絡 + 遊戲疊加顯示
//  全部用 Windows 內置效能計數器（PDH），唔使第三方程式。
// =====================================================================

internal sealed record PerfSample(
    double Cpu, long RamUsed, long RamTotal, double Gpu, long VramUsed, long VramTotal,
    double DiskRead, double DiskWrite, double DiskActive, double NetDown, double NetUp, double GpuTemp);

/// <summary>每秒取樣一次；有人用（頁面開住 / 疊加顯示）先會行。</summary>
internal static class PerfSampler
{
    public const int History = 120;
    public static readonly List<PerfSample> Samples = new();
    public static event Action<PerfSample>? Sampled;
    public static string GpuName { get; private set; } = "";

    private static readonly object Lock = new();
    private static int _users;
    private static System.Threading.Timer? _timer;
    private static PerformanceCounter? _cpu, _dr, _dw, _idle;
    private static Multi? _gpu, _vram, _net;
    private static long _vramTotal;
    private static Process? _smi;
    private static double _gpuTemp = double.NaN;
    private static int _busy;
    private static bool _needInit;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemStatus m);

    public static void Use()
    {
        lock (Lock)
        {
            if (_users++ > 0) return;
            _needInit = true; // 讀 GPU 類別可能要成秒，喺背景做
            _timer = new System.Threading.Timer(_ => Tick(), null, 0, 1000);
        }
    }

    public static void Release()
    {
        lock (Lock)
        {
            if (_users == 0 || --_users > 0) return;
            _timer?.Dispose(); _timer = null;
            foreach (var c in new[] { _cpu, _dr, _dw, _idle }) try { c?.Dispose(); } catch { }
            _cpu = _dr = _dw = _idle = null;
            _gpu = _vram = _net = null;
            try { if (_smi is { HasExited: false }) _smi.Kill(); } catch { }
            _smi?.Dispose(); _smi = null;
            _gpuTemp = double.NaN;
        }
    }

    /// <summary>有好多 instance 嘅類別（GPU 引擎、網卡）：每次讀成個類別，自己同上次比計速率。</summary>
    private sealed class Multi
    {
        private readonly PerformanceCounterCategory _cat;
        private readonly string[] _counters;
        private readonly Dictionary<string, Dictionary<string, CounterSample>> _prev = new();

        public Multi(string category, params string[] counters)
        {
            _cat = new PerformanceCounterCategory(category);
            _counters = counters;
        }

        /// <summary>回傳 counter → (instance, 值)。rate = 要同上次比（每秒 / 百分比）；否則直接用原始值。</summary>
        public Dictionary<string, List<(string Inst, double V)>> Read(bool rate)
        {
            var result = new Dictionary<string, List<(string, double)>>();
            var all = _cat.ReadCategory();
            foreach (var counter in _counters)
            {
                var list = new List<(string, double)>();
                result[counter] = list;
                InstanceDataCollection? data = null;
                foreach (string key in all.Keys)
                    if (string.Equals(key, counter, StringComparison.OrdinalIgnoreCase)) { data = all[key]; break; }
                if (data == null) continue;
                if (!_prev.TryGetValue(counter, out var prev)) _prev[counter] = prev = new();
                var now = new Dictionary<string, CounterSample>();
                foreach (InstanceData d in data.Values)
                {
                    var sample = d.Sample;
                    now[d.InstanceName] = sample;
                    if (!rate) list.Add((d.InstanceName, d.RawValue));
                    else if (prev.TryGetValue(d.InstanceName, out var old))
                    {
                        try { list.Add((d.InstanceName, CounterSample.Calculate(old, sample))); } catch { }
                    }
                }
                _prev[counter] = now;
            }
            return result;
        }
    }

    private static PerformanceCounter? Counter(string cat, string name, string inst)
    {
        try { var c = new PerformanceCounter(cat, name, inst, true); c.NextValue(); return c; } catch { return null; }
    }

    private static Multi? TryMulti(string cat, params string[] counters)
    {
        try { return PerformanceCounterCategory.Exists(cat) ? new Multi(cat, counters) : null; } catch { return null; }
    }

    private static void Init()
    {
        _cpu = Counter("Processor Information", "% Processor Utility", "_Total") ?? Counter("Processor", "% Processor Time", "_Total");
        _dr = Counter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        _dw = Counter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        _idle = Counter("PhysicalDisk", "% Idle Time", "_Total");
        _gpu = TryMulti("GPU Engine", "Utilization Percentage");
        _vram = TryMulti("GPU Adapter Memory", "Dedicated Usage");
        _net = TryMulti("Network Interface", "Bytes Received/sec", "Bytes Sent/sec");
        try { _gpu?.Read(true); _net?.Read(true); } catch { }
        ReadGpuInfo();
        StartSmi();
    }

    /// <summary>顯示卡名同 VRAM 總量（由驅動程式登錄檔讀）。</summary>
    private static void ReadGpuInfo()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k == null) continue;
                long mem = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0), byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0), int i => (uint)i, _ => 0
                };
                if (mem == 0 && k.GetValue("HardwareInformation.MemorySize") is int m32) mem = (uint)m32;
                if (mem == 0 && k.GetValue("HardwareInformation.MemorySize") is byte[] mb && mb.Length >= 4) mem = BitConverter.ToUInt32(mb, 0);
                if (mem > _vramTotal) { _vramTotal = mem; GpuName = k.GetValue("DriverDesc") as string ?? ""; }
            }
        }
        catch { }
    }

    /// <summary>NVIDIA 顯示卡：用驅動自帶嘅 nvidia-smi 讀溫度（冇就唔顯示）。</summary>
    private static void StartSmi()
    {
        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (!File.Exists(exe)) return;
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, "--query-gpu=temperature.gpu --format=csv,noheader,nounits -lms 2000")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true },
                EnableRaisingEvents = true
            };
            p.OutputDataReceived += (_, e) =>
            {
                var first = e.Data?.Split('\n')[0].Trim();
                if (double.TryParse(first, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t)) _gpuTemp = t;
            };
            p.Start();
            p.BeginOutputReadLine();
            _smi = p;
        }
        catch { }
    }

    private static double Next(PerformanceCounter? c)
    {
        if (c == null) return double.NaN;
        try { return c.NextValue(); } catch { return double.NaN; }
    }

    private static void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            lock (Lock)
            {
                if (_timer == null) return;
                if (_needInit)
                {
                    _needInit = false;
                    try { Init(); } catch (Exception ex) { AppPaths.LogError("perf init", ex); }
                    return;
                }
                double cpu = Math.Clamp(Next(_cpu), 0, 100);
                // GPU：同工作管理員一樣，每類引擎加埋，攞最高嗰類
                double gpu = double.NaN;
                try
                {
                    var g = _gpu?.Read(true)["Utilization Percentage"];
                    if (g is { Count: > 0 })
                        gpu = Math.Clamp(g.GroupBy(x => { int i = x.Inst.LastIndexOf("engtype_", StringComparison.Ordinal); return i >= 0 ? x.Inst[(i + 8)..] : ""; })
                            .Select(t => t.Sum(x => x.V)).Max(), 0, 100);
                }
                catch { }
                long vram = -1;
                try { var v = _vram?.Read(false)["Dedicated Usage"]; if (v is { Count: > 0 }) vram = (long)v.Max(x => x.V); } catch { }
                double rx = double.NaN, tx = double.NaN;
                try
                {
                    var n = _net?.Read(true);
                    static bool Real(string x) => !x.Contains("isatap", StringComparison.OrdinalIgnoreCase) && !x.Contains("Loopback", StringComparison.OrdinalIgnoreCase);
                    if (n != null) { rx = n["Bytes Received/sec"].Where(x => Real(x.Inst)).Sum(x => x.V); tx = n["Bytes Sent/sec"].Where(x => Real(x.Inst)).Sum(x => x.V); }
                }
                catch { }
                double idle = Next(_idle);
                double active = double.IsNaN(idle) ? double.NaN : Math.Clamp(100 - idle, 0, 100);
                var m = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() };
                GlobalMemoryStatusEx(ref m);
                var s = new PerfSample(cpu, (long)(m.TotalPhys - m.AvailPhys), (long)m.TotalPhys, gpu, vram, _vramTotal,
                    Math.Max(0, Next(_dr)), Math.Max(0, Next(_dw)), active, rx, tx, _gpuTemp);
                lock (Samples)
                {
                    Samples.Add(s);
                    if (Samples.Count > History) Samples.RemoveAt(0);
                }
                ThreadPool.QueueUserWorkItem(_ => Sampled?.Invoke(s));
            }
        }
        catch (Exception ex) { AppPaths.LogError("perf tick", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public static string Bytes(double b)
    {
        if (double.IsNaN(b) || b < 0) return "—";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return (b >= 100 || i == 0 ? b.ToString("0") : b.ToString("0.0")) + " " + u[i];
    }

    public static string Rate(double b) => Bytes(b) + "/s";
    public static string Pct(double v) => double.IsNaN(v) ? "—" : v.ToString("0") + "%";
}

/// <summary>一格數值 + 走勢圖。</summary>
internal sealed class MetricTile : Control
{
    public string Title = "", Value = "", Sub = "";
    public Color Hue = Ui.Accent;
    public double Max = 100;
    public bool AutoMax;
    private readonly List<double> _pts = new(), _pts2 = new();
    public Color Hue2 = Ui.Accent2;

    public MetricTile()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(Ui.S(250), Ui.S(130));
        Margin = new Padding(0, 0, Ui.S(12), Ui.S(12));
    }

    public void Push(double v, double v2 = double.NaN)
    {
        _pts.Add(v); _pts2.Add(v2);
        if (_pts.Count > PerfSampler.History) { _pts.RemoveAt(0); _pts2.RemoveAt(0); }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Clear(g, this, Parent?.BackColor ?? Ui.Card);
        Ui.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var p = Ui.Round(r, Ui.S(10f)))
        {
            using var b = new SolidBrush(Ui.CardHi); g.FillPath(b, p);
        }
        int pad = Ui.S(12);
        TextRenderer.DrawText(g, Title, Ui.F(9.25f), new Point(pad, pad - Ui.S(2)), Ui.TextDim, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Value, Ui.F(17f, FontStyle.Bold), new Point(pad, pad + Ui.S(16)), Ui.Text, TextFormatFlags.NoPadding);
        if (Sub.Length > 0)
            TextRenderer.DrawText(g, Sub, Ui.F(8.75f), new Rectangle(pad, pad + Ui.S(48), Width - pad * 2, Ui.S(18)), Ui.TextFaint,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        // 走勢圖
        var area = new RectangleF(pad, Height - Ui.S(44), Width - pad * 2, Ui.S(34));
        double max = Max;
        if (AutoMax) max = Math.Max(1, _pts.Concat(_pts2).Where(x => !double.IsNaN(x)).DefaultIfEmpty(1).Max() * 1.15);
        void Line(List<double> pts, Color hue, bool fill)
        {
            var valid = pts.Select((v, i) => (v, i)).Where(t => !double.IsNaN(t.v)).ToList();
            if (valid.Count < 2) return;
            float step = area.Width / (PerfSampler.History - 1);
            float x0 = area.Right - (pts.Count - 1) * step;
            var ptsF = valid.Select(t => new PointF(x0 + t.i * step, area.Bottom - (float)(Math.Clamp(t.v / max, 0, 1) * area.Height))).ToArray();
            if (fill)
            {
                using var path = new GraphicsPath();
                path.AddLines(ptsF);
                path.AddLine(ptsF[^1], new PointF(ptsF[^1].X, area.Bottom));
                path.AddLine(new PointF(ptsF[^1].X, area.Bottom), new PointF(ptsF[0].X, area.Bottom));
                path.CloseFigure();
                using var br = new LinearGradientBrush(area, Ui.Alpha(hue, 90), Ui.Alpha(hue, 8), LinearGradientMode.Vertical);
                g.FillPath(br, path);
            }
            using var pen = new Pen(hue, Ui.S(1.6f));
            g.DrawLines(pen, ptsF);
        }
        Line(_pts, Hue, true);
        Line(_pts2, Hue2, false);
    }
}

/// <summary>疊加顯示設定（data\perfmon.txt）。</summary>
internal static class PerfSettings
{
    public static bool Overlay, ShowCpu = true, ShowGpu = true, ShowRam = true, ShowVram, ShowTemp = true, ShowNet, ShowDisk, ShowClock, ShowPing;
    public static int Corner = 1; // 0 左上 1 右上 2 左下 3 右下
    public static int Opacity = 80, FontSize = 1; // 0 細 1 中 2 大
    private static string File_ => Path.Combine(AppPaths.Data, "perfmon.txt");

    static PerfSettings()
    {
        try
        {
            if (!File.Exists(File_)) return;
            foreach (var l in File.ReadAllLines(File_))
            {
                int i = l.IndexOf('=');
                if (i < 0) continue;
                string k = l[..i], v = l[(i + 1)..];
                bool b = v == "1";
                int.TryParse(v, out int n);
                switch (k)
                {
                    case "overlay": Overlay = b; break;
                    case "cpu": ShowCpu = b; break;
                    case "gpu": ShowGpu = b; break;
                    case "ram": ShowRam = b; break;
                    case "vram": ShowVram = b; break;
                    case "temp": ShowTemp = b; break;
                    case "net": ShowNet = b; break;
                    case "disk": ShowDisk = b; break;
                    case "clock": ShowClock = b; break;
                    case "ping": ShowPing = b; break;
                    case "corner": Corner = Math.Clamp(n, 0, 3); break;
                    case "opacity": Opacity = Math.Clamp(n, 20, 100); break;
                    case "font": FontSize = Math.Clamp(n, 0, 2); break;
                }
            }
        }
        catch { }
    }

    public static void Save()
    {
        static string B(bool b) => b ? "1" : "0";
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllLines(File_, new[]
            {
                "overlay=" + B(Overlay), "cpu=" + B(ShowCpu), "gpu=" + B(ShowGpu), "ram=" + B(ShowRam), "vram=" + B(ShowVram),
                "temp=" + B(ShowTemp), "net=" + B(ShowNet), "disk=" + B(ShowDisk), "clock=" + B(ShowClock), "ping=" + B(ShowPing),
                "corner=" + Corner, "opacity=" + Opacity, "font=" + FontSize
            });
        }
        catch { }
    }
}

/// <summary>遊戲上面嘅細疊加：置頂、滑鼠穿透、唔搶焦點。</summary>
internal sealed class PerfOverlay : Form
{
    private static PerfOverlay? _inst;
    public static event Action? StateChanged;
    public static bool Showing => _inst != null;

    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int w, int hh, uint flags);

    /// <summary>App 啟動時叫：設定快捷鍵 + 上次開住就自動開返。</summary>
    public static void Wire()
    {
        HotkeyHub.Get("perf.overlay").Action = () => Set(!Showing);
        GameMode.Wire();
        if (PerfSettings.Overlay && Extensions.IsInstalled(Extensions.Perf)) Set(true);
        Extensions.Changed += () => { if (!Extensions.IsInstalled(Extensions.Perf) && Showing) Application.OpenForms[0]?.BeginInvoke(() => Set(false, false)); };
    }

    public static void Set(bool on, bool remember = true)
    {
        if (on && _inst == null) { _inst = new PerfOverlay(); _inst.Show(); }
        else if (!on && _inst != null) { _inst.Close(); _inst = null; }
        if (remember) { PerfSettings.Overlay = on; PerfSettings.Save(); }
        StateChanged?.Invoke();
    }

    public static void Restyle() { if (_inst != null) { _inst.Opacity = PerfSettings.Opacity / 100.0; _inst.SyncPing(); _inst.Render(); } }

    // 疊加顯示 Ping：剔咗先開始 ping
    private bool _pingUsed;
    private void SyncPing()
    {
        if (PerfSettings.ShowPing == _pingUsed) return;
        _pingUsed = PerfSettings.ShowPing;
        if (_pingUsed) { PingMonitor.Sampled += OnPing; PingMonitor.Use(); }
        else { PingMonitor.Sampled -= OnPing; PingMonitor.Release(); }
    }

    private void OnPing()
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(Render); } catch { }
    }

    private PerfSample? _last;
    private readonly System.Windows.Forms.Timer _top = new() { Interval = 2000 };

    private PerfOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(14, 16, 20);
        Opacity = PerfSettings.Opacity / 100.0;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        PerfSampler.Sampled += OnSample;
        PerfSampler.Use();
        _top.Tick += (_, _) => { if (IsHandleCreated) SetWindowPos(Handle, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); }; // 保持最頂
        _top.Start();
        FormClosed += (_, _) => { PerfSampler.Sampled -= OnSample; PerfSampler.Release(); _top.Dispose(); if (_pingUsed) { _pingUsed = false; PingMonitor.Sampled -= OnPing; PingMonitor.Release(); } };
        SyncPing();
        Render();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000 | 0x20 | 0x80000 | 0x8; // 工具視窗、唔搶焦點、滑鼠穿透、layered、置頂
            return cp;
        }
    }

    private void OnSample(PerfSample s)
    {
        _last = s;
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(Render); } catch { }
    }

    private List<(string K, string V, Color C)> Lines()
    {
        var s = _last;
        var l = new List<(string, string, Color)>();
        Color Heat(double v) => double.IsNaN(v) ? Color.Gainsboro : v >= 90 ? Color.FromArgb(255, 110, 110) : v >= 70 ? Color.FromArgb(255, 200, 90) : Color.FromArgb(120, 230, 160);
        if (PerfSettings.ShowCpu) l.Add(("CPU", s == null ? "…" : PerfSampler.Pct(s.Cpu), Heat(s?.Cpu ?? double.NaN)));
        if (PerfSettings.ShowGpu) l.Add(("GPU", s == null ? "…" : PerfSampler.Pct(s.Gpu), Heat(s?.Gpu ?? double.NaN)));
        if (PerfSettings.ShowTemp && s != null && !double.IsNaN(s.GpuTemp))
            l.Add(("GPU °C", s.GpuTemp.ToString("0") + "°", s.GpuTemp >= 85 ? Color.FromArgb(255, 110, 110) : s.GpuTemp >= 75 ? Color.FromArgb(255, 200, 90) : Color.FromArgb(120, 230, 160)));
        if (PerfSettings.ShowRam)
            l.Add(("RAM", s == null ? "…" : $"{s.RamUsed / 1073741824.0:0.0}/{s.RamTotal / 1073741824.0:0} GB", Heat(s == null ? double.NaN : 100.0 * s.RamUsed / Math.Max(1, s.RamTotal))));
        if (PerfSettings.ShowVram)
            l.Add(("VRAM", s == null || s.VramUsed < 0 ? "—" : s.VramTotal > 0 ? $"{s.VramUsed / 1073741824.0:0.0}/{s.VramTotal / 1073741824.0:0} GB" : $"{s.VramUsed / 1073741824.0:0.0} GB",
                Heat(s == null || s.VramTotal <= 0 || s.VramUsed < 0 ? double.NaN : 100.0 * s.VramUsed / s.VramTotal)));
        if (PerfSettings.ShowNet && s != null) l.Add(("NET", "↓" + PerfSampler.Rate(s.NetDown) + " ↑" + PerfSampler.Rate(s.NetUp), Color.Gainsboro));
        if (PerfSettings.ShowDisk && s != null) l.Add(("DISK", PerfSampler.Pct(s.DiskActive), Heat(s.DiskActive)));
        if (PerfSettings.ShowPing && PingMonitor.Targets.FirstOrDefault() is { } pt)
            l.Add(("PING", PingMonitor.Ms(pt.Last), double.IsNaN(pt.Last) || pt.Last >= 150 ? Color.FromArgb(255, 110, 110) : pt.Last >= 80 ? Color.FromArgb(255, 200, 90) : Color.FromArgb(120, 230, 160)));
        if (PerfSettings.ShowClock) l.Add(("", DateTime.Now.ToString("HH:mm"), Color.Gainsboro));
        return l;
    }

    private Font Fnt(bool bold) => Ui.F(new[] { 8.5f, 10f, 12f }[PerfSettings.FontSize], bold ? FontStyle.Bold : FontStyle.Regular);

    private void Render()
    {
        var lines = Lines();
        if (lines.Count == 0) lines.Add(("", Lang.T("（未揀顯示項目）"), Color.Gainsboro));
        using var g = CreateGraphics();
        int pad = Ui.S(8), gap = Ui.S(10);
        int kw = lines.Max(x => TextRenderer.MeasureText(g, x.K, Fnt(false)).Width);
        int vw = lines.Max(x => TextRenderer.MeasureText(g, x.V, Fnt(true)).Width);
        int lh = TextRenderer.MeasureText(g, "Ag", Fnt(true)).Height;
        var size = new Size(pad * 2 + kw + gap + vw, pad * 2 + lh * lines.Count);
        var b = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        int m = Ui.S(12);
        var loc = PerfSettings.Corner switch
        {
            0 => new Point(b.Left + m, b.Top + m),
            1 => new Point(b.Right - size.Width - m, b.Top + m),
            2 => new Point(b.Left + m, b.Bottom - size.Height - m),
            _ => new Point(b.Right - size.Width - m, b.Bottom - size.Height - m)
        };
        if (Bounds != new Rectangle(loc, size)) Bounds = new Rectangle(loc, size);
        Tag = (lines, kw, lh);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Tag is not (List<(string K, string V, Color C)> lines, int kw, int lh)) return;
        var g = e.Graphics;
        g.Clear(BackColor);
        int pad = Ui.S(8), gap = Ui.S(10);
        for (int i = 0; i < lines.Count; i++)
        {
            int y = pad + i * lh;
            TextRenderer.DrawText(g, lines[i].K, Fnt(false), new Point(pad, y), Color.FromArgb(160, 168, 185), TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, lines[i].V, Fnt(true), new Point(pad + kw + gap, y), lines[i].C, TextFormatFlags.NoPadding);
        }
    }
}

internal sealed class PerfPage : ToolPage
{
    private readonly MetricTile _cpu, _gpu, _ram, _vram, _disk, _net;
    private readonly FlowLayoutPanel _procs;
    private readonly System.Windows.Forms.Timer _procTimer = new() { Interval = 2500 };
    private Dictionary<int, (TimeSpan Cpu, DateTime At)> _prev = new();
    private bool _using, _procBusy;
    private readonly CheckBox _overlayCb;

    public static Control Build() => new PerfPage();

    private PerfPage()
    {
        _cpu = new MetricTile { Title = "CPU", Hue = Ui.Accent };
        _gpu = new MetricTile { Title = "GPU", Hue = Ui.Accent2 };
        _ram = new MetricTile { Title = Lang.T("記憶體 RAM"), Hue = Ui.Success };
        _vram = new MetricTile { Title = Lang.T("顯示卡記憶體 VRAM"), Hue = Ui.Hex(0xE0A030) };
        _disk = new MetricTile { Title = Lang.T("磁碟"), Hue = Ui.Hex(0x3FB8C9), Hue2 = Ui.Hex(0xE07A5F), AutoMax = true };
        _net = new MetricTile { Title = Lang.T("網絡"), Hue = Ui.Accent, Hue2 = Ui.Success, AutoMax = true };
        var grid = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, WrapContents = true, MaximumSize = new Size(Ui.S(1100), 0) };
        grid.Controls.AddRange(new Control[] { _cpu, _gpu, _ram, _vram, _disk, _net });
        Card(Lang.T("效能監控"), Lang.T("每秒更新，數字同 Windows 工作管理員用同一個來源。GPU 溫度淨係 NVIDIA 顯示卡有。"), grid);

        _overlayCb = TUi.Check(Lang.T("喺畫面角落顯示（遊戲疊加）"), PerfOverlay.Showing, v => { if (v != PerfOverlay.Showing) PerfOverlay.Set(v); });
        void Sync() { if (!_overlayCb.IsDisposed && _overlayCb.Checked != PerfOverlay.Showing) _overlayCb.Checked = PerfOverlay.Showing; }
        PerfOverlay.StateChanged += Sync;
        Disposed += (_, _) => PerfOverlay.StateChanged -= Sync;
        CheckBox Item(string t, bool v, Action<bool> set) => TUi.Check(t, v, x => { set(x); PerfSettings.Save(); PerfOverlay.Restyle(); });
        Button? corner = null, font = null;
        string[] corners = { Lang.T("左上"), Lang.T("右上"), Lang.T("左下"), Lang.T("右下") };
        string[] fonts = { Lang.T("細"), Lang.T("中"), Lang.T("大") };
        corner = TUi.Btn(Lang.T("位置：") + corners[PerfSettings.Corner] + " ▾", () => TUi.Pick(corner!, corners.Select((n, i) => (n, i)), PerfSettings.Corner,
            v => { PerfSettings.Corner = v; PerfSettings.Save(); PerfOverlay.Restyle(); corner!.Text = Lang.T("位置：") + corners[v] + " ▾"; }), 120);
        font = TUi.Btn(Lang.T("字體：") + fonts[PerfSettings.FontSize] + " ▾", () => TUi.Pick(font!, fonts.Select((n, i) => (n, i)), PerfSettings.FontSize,
            v => { PerfSettings.FontSize = v; PerfSettings.Save(); PerfOverlay.Restyle(); font!.Text = Lang.T("字體：") + fonts[v] + " ▾"; }), 110);
        var opacity = TUi.Percent(PerfSettings.Opacity, 20, 100, 5, v => { PerfSettings.Opacity = v; PerfSettings.Save(); PerfOverlay.Restyle(); });
        Card(Lang.T("遊戲疊加"), Lang.T("永遠置頂、滑鼠撳唔到（唔會阻住遊戲）。遊戲要用「無邊框視窗」或者「視窗」模式先睇到，獨佔全螢幕會蓋過佢。"),
            TUi.Col(
                TUi.Row(_overlayCb, TUi.L(Lang.T("快捷鍵")), HotkeyHub.Box("perf.overlay", 140)),
                TUi.Row(Item("CPU", PerfSettings.ShowCpu, v => PerfSettings.ShowCpu = v), Item("GPU", PerfSettings.ShowGpu, v => PerfSettings.ShowGpu = v),
                    Item(Lang.T("GPU 溫度"), PerfSettings.ShowTemp, v => PerfSettings.ShowTemp = v), Item("RAM", PerfSettings.ShowRam, v => PerfSettings.ShowRam = v),
                    Item("VRAM", PerfSettings.ShowVram, v => PerfSettings.ShowVram = v), Item(Lang.T("網速"), PerfSettings.ShowNet, v => PerfSettings.ShowNet = v),
                    Item(Lang.T("磁碟"), PerfSettings.ShowDisk, v => PerfSettings.ShowDisk = v), Item("Ping", PerfSettings.ShowPing, v => PerfSettings.ShowPing = v),
                    Item(Lang.T("時間"), PerfSettings.ShowClock, v => PerfSettings.ShowClock = v)),
                TUi.Row(corner, font, TUi.L(Lang.T("不透明度")), opacity)));

        _procs = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        _procs.Controls.Add(TUi.L(Lang.T("計緊…")));
        Card(Lang.T("最食資源嘅程式"), Lang.T("同名程式加埋計（例如 Chrome 嘅多個程序）。"), _procs);

        _procTimer.Tick += (_, _) => _ = RefreshProcsAsync();
        VisibleChanged += (_, _) => Activate(Visible);
        HandleCreated += (_, _) => Activate(Visible);
        Disposed += (_, _) => { Activate(false); _procTimer.Dispose(); };
        lock (PerfSampler.Samples) foreach (var s in PerfSampler.Samples) Push(s, false);
    }

    private void Activate(bool on)
    {
        if (on == _using) return;
        _using = on;
        if (on) { PerfSampler.Sampled += OnSample; PerfSampler.Use(); _procTimer.Start(); _ = RefreshProcsAsync(); }
        else { PerfSampler.Sampled -= OnSample; PerfSampler.Release(); _procTimer.Stop(); }
    }

    private void OnSample(PerfSample s)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => Push(s, true)); } catch { }
    }

    private void Push(PerfSample s, bool text)
    {
        _cpu.Value = PerfSampler.Pct(s.Cpu);
        _cpu.Sub = Environment.ProcessorCount + Lang.T(" 條執行緒");
        _cpu.Push(s.Cpu);
        _gpu.Value = PerfSampler.Pct(s.Gpu) + (double.IsNaN(s.GpuTemp) ? "" : $"  ·  {s.GpuTemp:0}°C");
        _gpu.Sub = PerfSampler.GpuName;
        _gpu.Push(s.Gpu);
        double rp = 100.0 * s.RamUsed / Math.Max(1, s.RamTotal);
        _ram.Value = $"{s.RamUsed / 1073741824.0:0.0} / {s.RamTotal / 1073741824.0:0.0} GB";
        _ram.Sub = rp.ToString("0") + "%";
        _ram.Push(rp);
        double vp = s.VramTotal > 0 && s.VramUsed >= 0 ? 100.0 * s.VramUsed / s.VramTotal : double.NaN;
        _vram.Value = s.VramUsed < 0 ? "—" : s.VramTotal > 0 ? $"{s.VramUsed / 1073741824.0:0.0} / {s.VramTotal / 1073741824.0:0.0} GB" : $"{s.VramUsed / 1073741824.0:0.0} GB";
        _vram.Sub = double.IsNaN(vp) ? "" : vp.ToString("0") + "%";
        _vram.Push(double.IsNaN(vp) ? 0 : vp);
        _disk.Value = PerfSampler.Pct(s.DiskActive);
        _disk.Sub = Lang.T("讀 ") + PerfSampler.Rate(s.DiskRead) + Lang.T("  寫 ") + PerfSampler.Rate(s.DiskWrite);
        _disk.Push(s.DiskRead, s.DiskWrite);
        _net.Value = "↓ " + PerfSampler.Rate(s.NetDown);
        _net.Sub = "↑ " + PerfSampler.Rate(s.NetUp);
        _net.Push(s.NetDown, s.NetUp);
    }

    private async Task RefreshProcsAsync()
    {
        if (_procBusy) return;
        _procBusy = true;
        try
        {
            var prev = _prev;
            var (rows, next) = await Task.Run(() =>
            {
                var now = DateTime.UtcNow;
                var cur = new Dictionary<int, (TimeSpan, DateTime)>();
                var agg = new Dictionary<string, (double Cpu, long Mem)>(StringComparer.OrdinalIgnoreCase);
                int cores = Environment.ProcessorCount;
                foreach (var p in Process.GetProcesses())
                {
                    using (p)
                    {
                        try
                        {
                            if (p.Id == 0) continue;
                            long mem = p.WorkingSet64;
                            double cpu = 0;
                            try
                            {
                                var t = p.TotalProcessorTime;
                                cur[p.Id] = (t, now);
                                if (prev.TryGetValue(p.Id, out var o))
                                {
                                    double secs = (now - o.At).TotalSeconds;
                                    if (secs > 0) cpu = (t - o.Cpu).TotalSeconds / secs / cores * 100;
                                }
                            }
                            catch { }
                            var name = p.ProcessName;
                            agg[name] = agg.TryGetValue(name, out var a) ? (a.Cpu + cpu, a.Mem + mem) : (cpu, mem);
                        }
                        catch { }
                    }
                }
                var top = agg.OrderByDescending(x => x.Value.Cpu).ThenByDescending(x => x.Value.Mem).Take(10).ToList();
                return (top, cur);
            });
            _prev = next;
            if (IsDisposed) return;
            _procs.SuspendLayout();
            foreach (Control c in _procs.Controls.Cast<Control>().ToList()) { _procs.Controls.Remove(c); c.Dispose(); }
            Label Cell(string t, int w, Color c, bool bold = false, ContentAlignment al = ContentAlignment.MiddleLeft) => new()
            {
                Text = t, AutoSize = false, Width = Ui.S(w), Height = Ui.S(26), ForeColor = c, BackColor = Ui.Card, UseMnemonic = false, AutoEllipsis = true,
                Font = Ui.F(9.5f, bold ? FontStyle.Bold : FontStyle.Regular), TextAlign = al, Margin = new Padding(0)
            };
            var head = TUi.Row(Cell(Lang.T("程式"), 260, Ui.TextFaint), Cell("CPU", 80, Ui.TextFaint, false, ContentAlignment.MiddleRight), Cell(Lang.T("記憶體"), 110, Ui.TextFaint, false, ContentAlignment.MiddleRight));
            head.WrapContents = false; head.Margin = new Padding(0);
            _procs.Controls.Add(head);
            foreach (var (name, v) in rows)
            {
                var row = TUi.Row(Cell(name, 260, Ui.Text, true), Cell(v.Cpu.ToString("0.0") + "%", 80, v.Cpu >= 20 ? Ui.Danger : Ui.TextDim, false, ContentAlignment.MiddleRight),
                    Cell(PerfSampler.Bytes(v.Mem), 110, Ui.TextDim, false, ContentAlignment.MiddleRight));
                row.WrapContents = false; row.Margin = new Padding(0);
                _procs.Controls.Add(row);
            }
            _procs.ResumeLayout(true);
            Relayout();
        }
        catch (Exception ex) { AppPaths.LogError("perf procs", ex); }
        finally { _procBusy = false; }
    }
}
