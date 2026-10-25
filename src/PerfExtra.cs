using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Tools;

// =====================================================================
//  效能監控：分頁（效能 / Ping 監看 / 遊戲模式）
// =====================================================================

internal sealed class PerfHub : Panel
{
    private readonly Control?[] _pages = new Control?[3];
    private readonly Func<Control>[] _builders = { PerfPage.Build, PingPage.Build, GameModePage.Build };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private static int _last;

    public static Control Build() => new PerfHub();

    private PerfHub()
    {
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        var tabs = new TabStrip(Lang.T("效能"), Lang.T("Ping 監看"), Lang.T("遊戲模式")) { Dock = DockStyle.Fill };
        var head = new Panel { Dock = DockStyle.Top, Height = Ui.S(42) + Ui.S(14), BackColor = Ui.Bg, Padding = new Padding(Ui.S(24), Ui.S(14), Ui.S(24), 0) };
        head.Controls.Add(tabs);
        Controls.Add(_body);
        Controls.Add(head);
        tabs.Selected += Show;
        tabs.Index = _last;
    }

    private void Show(int i)
    {
        _last = i;
        _body.SuspendLayout();
        for (int k = 0; k < _pages.Length; k++)
        {
            if (k == i && _pages[k] == null) { _pages[k] = _builders[k](); _body.Controls.Add(_pages[k]); }
            if (_pages[k] != null) _pages[k]!.Visible = k == i;
        }
        _body.ResumeLayout(true);
    }
}

// =====================================================================
//  Ping 監看
// =====================================================================

/// <summary>每秒 ping 一次各個目標；有人用（Ping 分頁開住 / 疊加顯示 Ping）先會行。目標存喺 data\ping.txt。</summary>
internal static class PingMonitor
{
    public const int History = 120;

    internal sealed class Target
    {
        public required string Host;
        public readonly List<double> Ms = new(); // NaN = 掉包
        public double Last = double.NaN;
        public string Error = "";

        public double Loss => Ms.Count == 0 ? 0 : 100.0 * Ms.Count(double.IsNaN) / Ms.Count;
        public double Avg => Ms.Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Average();
        public double Jitter
        {
            get
            {
                var v = Ms.Where(x => !double.IsNaN(x)).ToList();
                if (v.Count < 2) return double.NaN;
                double sum = 0;
                for (int i = 1; i < v.Count; i++) sum += Math.Abs(v[i] - v[i - 1]);
                return sum / (v.Count - 1);
            }
        }
    }

    private static readonly object Lock = new();
    private static readonly List<Target> _targets = Load();
    private static int _users, _busy;
    private static System.Threading.Timer? _timer;
    public static event Action? Sampled;
    public static event Action? TargetsChanged;

    private static string File_ => Path.Combine(AppPaths.Data, "ping.txt");

    public static List<Target> Targets { get { lock (Lock) return _targets.ToList(); } }

    private static List<Target> Load()
    {
        var hosts = new List<string>();
        try
        {
            if (File.Exists(File_))
                hosts.AddRange(File.ReadAllLines(File_).Select(l => l.Trim()).Where(l => l.Length > 0));
        }
        catch { }
        if (hosts.Count == 0) hosts.AddRange(new[] { "1.1.1.1", "8.8.8.8" });
        return hosts.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).Select(h => new Target { Host = h }).ToList();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            lock (Lock) File.WriteAllLines(File_, _targets.Select(t => t.Host));
        }
        catch { }
    }

    /// <summary>主機名 / IP 合唔合法（唔准有空格、協定、路徑）。</summary>
    public static string? Clean(string host)
    {
        host = host.Trim();
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) host = host[7..];
        if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) host = host[8..];
        int slash = host.IndexOf('/');
        if (slash >= 0) host = host[..slash];
        // 去走端口（ping 唔使端口）
        var m = Regex.Match(host, @"^(.+?):\d+$");
        if (m.Success && !host.Contains("::")) host = m.Groups[1].Value;
        return Regex.IsMatch(host, @"^[A-Za-z0-9.\-:\[\]]{1,253}$") ? host.Trim('[', ']') : null;
    }

    public static bool Add(string host)
    {
        lock (Lock)
        {
            if (_targets.Count >= 8 || _targets.Any(t => string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase))) return false;
            _targets.Add(new Target { Host = host });
        }
        Save();
        TargetsChanged?.Invoke();
        return true;
    }

    public static void Remove(string host)
    {
        lock (Lock) _targets.RemoveAll(t => string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase));
        Save();
        TargetsChanged?.Invoke();
    }

    public static void Use()
    {
        lock (Lock)
        {
            if (_users++ > 0) return;
            _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, 0, 1000);
        }
    }

    public static void Release()
    {
        lock (Lock)
        {
            if (_users == 0 || --_users > 0) return;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private static async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var list = Targets;
            await Task.WhenAll(list.Select(async t =>
            {
                double ms = double.NaN;
                string err = "";
                try
                {
                    using var p = new Ping();
                    var r = await p.SendPingAsync(t.Host, 1000);
                    if (r.Status == IPStatus.Success) ms = r.RoundtripTime;
                    else err = r.Status == IPStatus.TimedOut ? Lang.T("逾時") : r.Status.ToString();
                }
                catch (Exception ex) { err = ex.InnerException?.Message ?? ex.Message; }
                lock (Lock)
                {
                    t.Last = ms;
                    t.Error = err;
                    t.Ms.Add(ms);
                    if (t.Ms.Count > History) t.Ms.RemoveAt(0);
                }
            }));
            Sampled?.Invoke();
        }
        catch (Exception ex) { AppPaths.LogError("Ping", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public static string Ms(double v) => double.IsNaN(v) ? "—" : v.ToString("0") + " ms";
}

internal sealed class PingPage : ToolPage
{
    private readonly FlowLayoutPanel _grid, _list;
    private readonly Dictionary<string, MetricTile> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _host = new() { PlaceholderText = Lang.T("IP 或者網址，例如 1.1.1.1") };
    private readonly Label _msg;
    private bool _using;

    public static Control Build() => new PingPage();

    private PingPage()
    {
        _grid = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, WrapContents = true, MaximumSize = new Size(Ui.S(1100), 0) };
        Card(Lang.T("Ping 監看"), Lang.T("每秒 ping 一次，睇延遲、掉包同抖動。打機卡 / lag 時可以睇吓係網絡定電腦問題。"), _grid);

        var hostHost = new InputHost(_host, Ui.S(260));
        hostHost.Margin = new Padding(0, 0, Ui.S(10), 0);
        _msg = TUi.L("");
        var add = TUi.Btn(Lang.T("+ 加目標"), AddHost, 0, BtnKind.Primary);
        _host.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddHost(); } };
        _list = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var presets = TUi.Row(TUi.L(Lang.T("常用：")),
            TUi.Btn("Cloudflare 1.1.1.1", () => Quick("1.1.1.1")),
            TUi.Btn("Google 8.8.8.8", () => Quick("8.8.8.8")),
            TUi.Btn(Lang.T("路由器"), () => Quick(Gateway() ?? "192.168.1.1")));
        Card(Lang.T("目標"), Lang.T("最多 8 個。可以加遊戲伺服器嘅 IP（有啲伺服器唔回應 ping，會顯示逾時）。"),
            TUi.Col(TUi.Row(hostHost, add, _msg), presets, _list));
        RebuildList();

        PingMonitor.TargetsChanged += OnTargets;
        VisibleChanged += (_, _) => Activate(Visible);
        HandleCreated += (_, _) => Activate(Visible);
        Disposed += (_, _) => { Activate(false); PingMonitor.TargetsChanged -= OnTargets; };
    }

    private static string? Gateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any))?.ToString();
        }
        catch { return null; }
    }

    private void Quick(string host)
    {
        _host.Text = host;
        AddHost();
    }

    private void AddHost()
    {
        var h = PingMonitor.Clean(_host.Text);
        if (h == null) { _msg.Text = Lang.T("格式唔啱"); _msg.ForeColor = Ui.Danger; return; }
        if (!PingMonitor.Add(h)) { _msg.Text = Lang.T("已經有咗，或者已經有 8 個"); _msg.ForeColor = Ui.Danger; return; }
        _msg.Text = "";
        _host.Text = "";
    }

    private void OnTargets()
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(RebuildList); } catch { }
    }

    private void RebuildList()
    {
        _grid.SuspendLayout();
        _list.SuspendLayout();
        var targets = PingMonitor.Targets;
        foreach (var k in _tiles.Keys.Where(k => targets.All(t => !string.Equals(t.Host, k, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _grid.Controls.Remove(_tiles[k]);
            _tiles[k].Dispose();
            _tiles.Remove(k);
        }
        foreach (var t in targets)
        {
            if (_tiles.ContainsKey(t.Host)) continue;
            var tile = new MetricTile { Title = t.Host, Hue = Ui.Success, AutoMax = true, Value = "…" };
            _tiles[t.Host] = tile;
            _grid.Controls.Add(tile);
        }
        foreach (Control c in _list.Controls.Cast<Control>().ToList()) { _list.Controls.Remove(c); c.Dispose(); }
        foreach (var t in targets)
        {
            string host = t.Host;
            var row = TUi.Row(TUi.L(host, Ui.Text), TUi.Btn("✕", () => PingMonitor.Remove(host), 34));
            row.WrapContents = false;
            row.Margin = new Padding(0, 0, 0, Ui.S(4));
            _list.Controls.Add(row);
        }
        _list.ResumeLayout(true);
        _grid.ResumeLayout(true);
        Relayout();
    }

    private void Activate(bool on)
    {
        if (on == _using) return;
        _using = on;
        if (on) { PingMonitor.Sampled += OnSample; PingMonitor.Use(); }
        else { PingMonitor.Sampled -= OnSample; PingMonitor.Release(); }
    }

    private void OnSample()
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(Push); } catch { }
    }

    private void Push()
    {
        foreach (var t in PingMonitor.Targets)
        {
            if (!_tiles.TryGetValue(t.Host, out var tile)) continue;
            tile.Value = double.IsNaN(t.Last) ? (t.Error.Length > 0 ? t.Error : "—") : PingMonitor.Ms(t.Last);
            tile.Sub = Lang.F("掉包 {0:0}% · 平均 {1} · 抖動 {2}", t.Loss, PingMonitor.Ms(t.Avg), PingMonitor.Ms(t.Jitter));
            tile.Hue = double.IsNaN(t.Last) ? Ui.Danger : t.Last >= 150 ? Ui.Danger : t.Last >= 80 ? Ui.Hex(0xE0A030) : Ui.Success;
            tile.Push(double.IsNaN(t.Last) ? double.NaN : t.Last);
        }
    }
}

// =====================================================================
//  遊戲模式：閂指定程式、切高效能電源計劃；結束時還原
// =====================================================================

internal static class GameMode
{
    public static bool Active { get; private set; }
    public static bool Power = true, Kill, Reopen = true;
    public static readonly List<string> CloseList = new();
    private static string _prevScheme = "";
    private static readonly List<string> Closed = new(); // 閂咗嘅程式路徑（結束時重開）
    public static event Action? Changed;

    private static string File_ => Path.Combine(AppPaths.Data, "gamemode.txt");

    static GameMode()
    {
        try
        {
            if (!File.Exists(File_)) return;
            foreach (var l in File.ReadAllLines(File_))
            {
                int i = l.IndexOf('=');
                if (i < 0) continue;
                string k = l[..i], v = l[(i + 1)..];
                switch (k)
                {
                    case "power": Power = v == "1"; break;
                    case "kill": Kill = v == "1"; break;
                    case "reopen": Reopen = v == "1"; break;
                    case "close": if (v.Trim().Length > 0) CloseList.Add(v.Trim()); break;
                    case "active": Active = v == "1"; break; // 上次未結束就閂咗 MultiTool：記住，等用家可以還原
                    case "prev": _prevScheme = v.Trim(); break;
                    case "closed": if (v.Trim().Length > 0) Closed.Add(v.Trim()); break;
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
            var lines = new List<string> { "power=" + B(Power), "kill=" + B(Kill), "reopen=" + B(Reopen), "active=" + B(Active), "prev=" + _prevScheme };
            lines.AddRange(CloseList.Select(x => "close=" + x));
            lines.AddRange(Closed.Select(x => "closed=" + x));
            File.WriteAllLines(File_, lines);
        }
        catch { }
    }

    /// <summary>App 啟動時叫：設定快捷鍵。</summary>
    public static void Wire()
    {
        HotkeyHub.Get("perf.gamemode").Action = () => _ = ToggleAsync();
    }

    private static int _busy;

    public static async Task<string> ToggleAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return "";
        try
        {
            string msg = Active ? await Task.Run(Exit) : await Task.Run(Enter);
            Changed?.Invoke();
            Toast.Show(msg, Active ? Ui.Success : Ui.Text, 3500);
            return msg;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static (int Code, string Out) PowerCfg(string args)
    {
        var psi = new ProcessStartInfo("powercfg.exe", args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        return (p.ExitCode, o);
    }

    private static string Enter()
    {
        var notes = new List<string>();
        if (Power)
        {
            try
            {
                var (_, o) = PowerCfg("/getactivescheme");
                var m = Regex.Match(o, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                _prevScheme = m.Success ? m.Value : "";
                var (code, _) = PowerCfg("/setactive SCHEME_MIN"); // 高效能
                notes.Add(code == 0 ? Lang.T("已切換高效能電源計劃") : Lang.T("呢部電腦冇「高效能」電源計劃"));
            }
            catch (Exception ex) { AppPaths.LogError("遊戲模式電源計劃", ex); }
        }
        Closed.Clear();
        int closedCount = 0;
        var self = Environment.ProcessId;
        foreach (var name in CloseList)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)); } catch { continue; }
            foreach (var p in procs)
            {
                try
                {
                    if (p.Id == self) continue;
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (path != null && !Closed.Contains(path, StringComparer.OrdinalIgnoreCase)) Closed.Add(path);
                    p.CloseMainWindow();
                }
                catch { }
            }
            // 等佢自己閂（最多 3 秒）
            var until = DateTime.Now.AddSeconds(3);
            while (DateTime.Now < until && procs.Any(p => { try { return !p.HasExited; } catch { return false; } })) Thread.Sleep(200);
            foreach (var p in procs)
            {
                try
                {
                    if (!p.HasExited && Kill) p.Kill(true);
                    if (p.HasExited || Kill) closedCount++;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        if (CloseList.Count > 0) notes.Add(Lang.F("閂咗 {0} 個程式", closedCount));
        Active = true;
        Save();
        return Lang.T("🎮 遊戲模式已開啟") + (notes.Count > 0 ? "：" + string.Join("，", notes) : "");
    }

    private static string Exit()
    {
        var notes = new List<string>();
        if (_prevScheme.Length > 0)
        {
            try
            {
                var (code, _) = PowerCfg("/setactive " + _prevScheme);
                if (code == 0) notes.Add(Lang.T("電源計劃已還原"));
            }
            catch (Exception ex) { AppPaths.LogError("遊戲模式還原電源計劃", ex); }
            _prevScheme = "";
        }
        if (Reopen && Closed.Count > 0)
        {
            int n = 0;
            foreach (var path in Closed)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    // 已經開返咗就唔再開
                    if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)).Length > 0) continue;
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
                    n++;
                }
                catch (Exception ex) { AppPaths.LogError("遊戲模式重開程式", ex); }
            }
            if (n > 0) notes.Add(Lang.F("重開咗 {0} 個程式", n));
        }
        Closed.Clear();
        Active = false;
        Save();
        return Lang.T("遊戲模式已結束") + (notes.Count > 0 ? "：" + string.Join("，", notes) : "");
    }

    /// <summary>而家開住、有視窗嘅程式（加入「要閂嘅程式」用）。</summary>
    public static List<string> RunningApps()
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "TextInputHost", "ApplicationFrameHost", "SystemSettings", "ShellExperienceHost", "StartMenuExperienceHost",
            "SearchHost", "LockApp", "dwm", "csrss", "winlogon", Process.GetCurrentProcess().ProcessName
        };
        var list = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle == 0 || skip.Contains(p.ProcessName)) continue;
                    if (!list.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) list.Add(p.ProcessName);
                }
                catch { }
            }
        }
        return list.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

internal sealed class GameModePage : ToolPage
{
    private readonly FancyButton _toggle;
    private readonly Label _state;
    private readonly FlowLayoutPanel _list;

    public static Control Build() => new GameModePage();

    private GameModePage()
    {
        _state = TUi.L("");
        _toggle = TUi.Btn("", () => _ = ClickAsync(), 160, BtnKind.Success);
        Card(Lang.T("遊戲模式"), Lang.T("打機前一撳：閂走指定嘅背景程式、切去高效能電源計劃；打完再撳就還原。"),
            TUi.Col(TUi.Row(_toggle, TUi.L(Lang.T("快捷鍵")), HotkeyHub.Box("perf.gamemode", 140)), _state));

        _list = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        FancyButton? add = null;
        add = TUi.Btn(Lang.T("+ 加程式 ▾"), () =>
        {
            var apps = GameMode.RunningApps().Where(a => !GameMode.CloseList.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
            var menu = TUi.Menu(add!);
            foreach (var a in apps)
            {
                var name = a;
                menu.Items.Add(new ToolStripMenuItem(a, null, (_, _) => { GameMode.CloseList.Add(name); GameMode.Save(); RebuildList(); }) { ForeColor = Ui.Text });
            }
            if (apps.Count == 0) menu.Items.Add(new ToolStripMenuItem(Lang.T("（冇其他開住嘅程式）")) { Enabled = false });
            if (menu.Items.Count > 20 && menu is ToolStripDropDownMenu dm) dm.MaximumSize = new Size(Ui.S(360), Ui.S(520));
            menu.Show(add!, new Point(0, add!.Height));
        }, 0, BtnKind.Primary);
        Card(Lang.T("開啟時"), Lang.T("「要閂嘅程式」會先叫佢自己閂（好似撳 ✕），3 秒內閂唔到就照留住，除非剔咗「強制結束」。"),
            TUi.Col(
                TUi.Check(Lang.T("切換到高效能電源計劃"), GameMode.Power, v => { GameMode.Power = v; GameMode.Save(); }),
                TUi.Check(Lang.T("閂唔到就強制結束（未儲存嘅嘢會冇咗）"), GameMode.Kill, v => { GameMode.Kill = v; GameMode.Save(); }),
                TUi.Check(Lang.T("結束遊戲模式時重新開返閂咗嘅程式"), GameMode.Reopen, v => { GameMode.Reopen = v; GameMode.Save(); }),
                TUi.Row(TUi.L(Lang.T("要閂嘅程式"), Ui.Text), add),
                _list));
        RebuildList();
        GameMode.Changed += OnChanged;
        Disposed += (_, _) => GameMode.Changed -= OnChanged;
        UpdateState();
    }

    private async Task ClickAsync()
    {
        _toggle.Enabled = false;
        _state.Text = GameMode.Active ? Lang.T("還原緊…") : Lang.T("開啟緊…");
        try { _state.Text = await GameMode.ToggleAsync(); }
        catch (Exception ex) { _state.Text = Lang.T("出錯：") + ex.Message; }
        finally { _toggle.Enabled = true; UpdateState(); }
    }

    private void OnChanged()
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(UpdateState); } catch { }
    }

    private void UpdateState()
    {
        _toggle.Text = GameMode.Active ? Lang.T("⏹ 結束遊戲模式") : Lang.T("🎮 開始遊戲模式");
        _toggle.Kind = GameMode.Active ? BtnKind.Danger : BtnKind.Success;
        _toggle.Invalidate();
        if (_state.Text.Length == 0) _state.Text = GameMode.Active ? Lang.T("遊戲模式開緊") : Lang.T("未開啟");
    }

    private void RebuildList()
    {
        _list.SuspendLayout();
        foreach (Control c in _list.Controls.Cast<Control>().ToList()) { _list.Controls.Remove(c); c.Dispose(); }
        if (GameMode.CloseList.Count == 0) _list.Controls.Add(TUi.L(Lang.T("（未加，例如 Discord、Chrome、OneDrive、Steam 網頁…）"), Ui.TextFaint));
        foreach (var n in GameMode.CloseList.ToList())
        {
            string name = n;
            var row = TUi.Row(TUi.L(name, Ui.Text), TUi.Btn("✕", () => { GameMode.CloseList.Remove(name); GameMode.Save(); RebuildList(); }, 34));
            row.WrapContents = false;
            row.Margin = new Padding(0, 0, 0, Ui.S(4));
            _list.Controls.Add(row);
        }
        _list.ResumeLayout(true);
        Relayout();
    }
}
