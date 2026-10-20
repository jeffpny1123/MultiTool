using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MultiTool.Common;
using MultiTool.UI;

namespace MultiTool.Scripts;

/// <summary>
/// 錄製腳本：用低階鍵盤 / 滑鼠 hook 記低你撳嘅鍵、滑鼠掣、滾輪同埋中間隔咗幾耐，
/// 停咗之後變做步驟（按鍵 / 點擊 / 延遲 / 滑鼠滾輪）。滑鼠移動唔記。
/// </summary>
internal sealed class ScriptRecorder : IDisposable
{
    private enum Ev { KeyDown, KeyUp, MouseDown, MouseUp, Wheel }
    private sealed record Item(Ev Kind, long Ms, int Vk, bool Right, Point Pos, int Notches);

    private delegate nint HookProc(int code, nint w, nint l);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint h);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint h, int code, nint w, nint l);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint h, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);

    [StructLayout(LayoutKind.Sequential)] private struct KBD { public uint vk, scan, flags, time; public nint extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLL { public Point pt; public uint data, flags, time; public nint extra; }

    private readonly HookProc _kbProc, _msProc; // 要留住，費事俾 GC 收咗
    private nint _kb, _ms;
    private readonly List<Item> _items = new();
    private readonly Stopwatch _clock = new();
    private readonly HashSet<int> _down = new();
    private readonly Keys _stopKey;

    /// <summary>撳咗停止鍵（喺 hook 入面觸發）。</summary>
    public event Action? StopRequested;

    public ScriptRecorder(Keys stopKey)
    {
        _stopKey = stopKey;
        _kbProc = KbHook;
        _msProc = MsHook;
    }

    public void Start()
    {
        var mod = GetModuleHandle(null);
        _clock.Restart();
        _kb = SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, _kbProc, mod, 0);
        _ms = SetWindowsHookEx(14 /*WH_MOUSE_LL*/, _msProc, mod, 0);
    }

    public void Stop()
    {
        if (_kb != 0) { UnhookWindowsHookEx(_kb); _kb = 0; }
        if (_ms != 0) { UnhookWindowsHookEx(_ms); _ms = 0; }
        _clock.Stop();
    }

    public void Dispose() => Stop();

    private static bool OwnWindowAt(Point p)
    {
        var h = GetAncestor(WindowFromPoint(p), 2 /*GA_ROOT*/);
        GetWindowThreadProcessId(h, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static int Generic(int vk) => vk switch { 0xA0 or 0xA1 => 0x10, 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12, _ => vk };

    private nint KbHook(int code, nint w, nint l)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBD>(l);
            bool injected = (k.flags & 0x10) != 0;
            int msg = (int)w;
            bool down = msg is 0x100 or 0x104, up = msg is 0x101 or 0x105;
            if (!injected && (down || up))
            {
                int vk = Generic((int)k.vk);
                if (vk == (int)_stopKey)
                {
                    if (down) BeginStop();
                    return 1; // 停止鍵唔傳落去
                }
                if (down)
                {
                    if (_down.Add(vk)) _items.Add(new Item(Ev.KeyDown, _clock.ElapsedMilliseconds, vk, false, Point.Empty, 0)); // 撳住自動重複唔記
                }
                else
                {
                    _down.Remove(vk);
                    _items.Add(new Item(Ev.KeyUp, _clock.ElapsedMilliseconds, vk, false, Point.Empty, 0));
                }
            }
        }
        return CallNextHookEx(_kb, code, w, l);
    }

    private nint MsHook(int code, nint w, nint l)
    {
        if (code >= 0)
        {
            var m = Marshal.PtrToStructure<MSLL>(l);
            bool injected = (m.flags & 0x1) != 0;
            int msg = (int)w;
            if (!injected && msg != 0x200 && !OwnWindowAt(m.pt)) // 撳 MultiTool 自己個視窗（例如「停止錄製」）唔記
            {
                long t = _clock.ElapsedMilliseconds;
                switch (msg)
                {
                    case 0x201: _items.Add(new Item(Ev.MouseDown, t, 0, false, m.pt, 0)); break;
                    case 0x202: _items.Add(new Item(Ev.MouseUp, t, 0, false, m.pt, 0)); break;
                    case 0x204: _items.Add(new Item(Ev.MouseDown, t, 0, true, m.pt, 0)); break;
                    case 0x205: _items.Add(new Item(Ev.MouseUp, t, 0, true, m.pt, 0)); break;
                    case 0x20A:
                    {
                        int notches = (short)(m.data >> 16) / 120;
                        if (notches == 0) break;
                        // 連續碌嘅合埋做一步
                        if (_items.Count > 0 && _items[^1] is { Kind: Ev.Wheel } last && t - last.Ms < 400 && Math.Sign(last.Notches) == Math.Sign(notches)
                            && Math.Abs(last.Pos.X - m.pt.X) < 20 && Math.Abs(last.Pos.Y - m.pt.Y) < 20)
                            _items[^1] = last with { Ms = t, Notches = last.Notches + notches };
                        else _items.Add(new Item(Ev.Wheel, t, 0, false, m.pt, notches));
                        break;
                    }
                }
            }
        }
        return CallNextHookEx(_ms, code, w, l);
    }

    private bool _stopping;
    private void BeginStop()
    {
        if (_stopping) return;
        _stopping = true;
        StopRequested?.Invoke();
    }

    /// <summary>將錄到嘅嘢變做步驟。toScript = 螢幕座標 → 腳本座標（揀咗視窗就係相對視窗）。</summary>
    public List<Step> ToSteps(Func<Point, Point> toScript)
    {
        var steps = new List<Step>();
        long lastT = 0;
        bool first = true;
        var used = new bool[_items.Count];
        void Gap(long t)
        {
            long gap = t - lastT;
            if (!first && gap >= 30) steps.Add(new Step { Kind = StepKind.Wait, Seconds = Math.Round(gap / 10m) * 10m / 1000m });
            first = false;
        }
        for (int i = 0; i < _items.Count; i++)
        {
            if (used[i]) continue;
            var it = _items[i];
            switch (it.Kind)
            {
                case Ev.KeyDown:
                {
                    Gap(it.Ms);
                    // 下一個事件就係同一粒鍵彈起：合做「撳住 N ms」
                    if (i + 1 < _items.Count && _items[i + 1] is { Kind: Ev.KeyUp } nx && nx.Vk == it.Vk)
                    {
                        steps.Add(new Step { Kind = StepKind.Key, Vk = it.Vk, Mode = PressMode.Hold, Hold = Math.Max(10, nx.Ms - it.Ms) / 1000m });
                        used[i + 1] = true;
                        lastT = nx.Ms;
                    }
                    else
                    {
                        steps.Add(new Step { Kind = StepKind.Key, Vk = it.Vk, Mode = PressMode.Down });
                        lastT = it.Ms;
                    }
                    break;
                }
                case Ev.KeyUp:
                    Gap(it.Ms);
                    steps.Add(new Step { Kind = StepKind.Key, Vk = it.Vk, Mode = PressMode.Up });
                    lastT = it.Ms;
                    break;
                case Ev.MouseDown:
                {
                    Gap(it.Ms);
                    var p = toScript(it.Pos);
                    if (i + 1 < _items.Count && _items[i + 1] is { Kind: Ev.MouseUp } nx && nx.Right == it.Right
                        && Math.Abs(nx.Pos.X - it.Pos.X) <= 3 && Math.Abs(nx.Pos.Y - it.Pos.Y) <= 3)
                    {
                        steps.Add(new Step { Kind = StepKind.Click, Right = it.Right, X = p.X, Y = p.Y, Mode = PressMode.Hold, Hold = Math.Max(10, nx.Ms - it.Ms) / 1000m });
                        used[i + 1] = true;
                        lastT = nx.Ms;
                    }
                    else
                    {
                        steps.Add(new Step { Kind = StepKind.Click, Right = it.Right, X = p.X, Y = p.Y, Mode = PressMode.Down });
                        lastT = it.Ms;
                    }
                    break;
                }
                case Ev.MouseUp:
                {
                    Gap(it.Ms);
                    var p = toScript(it.Pos);
                    steps.Add(new Step { Kind = StepKind.Click, Right = it.Right, X = p.X, Y = p.Y, Mode = PressMode.Up });
                    lastT = it.Ms;
                    break;
                }
                case Ev.Wheel:
                {
                    Gap(it.Ms);
                    var p = toScript(it.Pos);
                    steps.Add(new Step { Kind = StepKind.Scroll, X = p.X, Y = p.Y, Count = it.Notches });
                    lastT = it.Ms;
                    break;
                }
            }
        }
        return steps;
    }
}

/// <summary>錄製中嘅細窗（右上角，唔搶焦點）：顯示時間同埋「停止錄製」掣。</summary>
internal sealed class RecordingBar : Form
{
    private readonly Label _text;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };
    public event Action? StopClicked;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000008 /*WS_EX_TOPMOST*/ | 0x80 /*WS_EX_TOOLWINDOW*/;
            return cp;
        }
    }

    public RecordingBar(string stopKeyName)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Panel;
        Size = new Size(Ui.S(300), Ui.S(56));
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.Right - Width - Ui.S(16), wa.Top + Ui.S(16));
        _text = new Label { ForeColor = Ui.Text, Font = Ui.F(9.5f, FontStyle.Bold), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        _text.SetBounds(Ui.S(14), 0, Ui.S(170), Height);
        var stop = new FancyButton { Text = Lang.T("■ 停止錄製"), Kind = BtnKind.Danger, Font = Ui.F(9.5f) };
        stop.SetBounds(Width - Ui.S(112), Ui.S(11), Ui.S(100), Ui.S(34));
        stop.Click += (_, _) => StopClicked?.Invoke();
        Controls.Add(_text);
        Controls.Add(stop);
        _tick.Tick += (_, _) => _text.Text = Lang.F("● 錄製中 {0:mm\\:ss}", _sw.Elapsed) + "\n" + Lang.F("撳 {0} 停止", stopKeyName);
        _tick.Start();
        _text.Text = Lang.F("● 錄製中 {0:mm\\:ss}", _sw.Elapsed) + "\n" + Lang.F("撳 {0} 停止", stopKeyName);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Ui.Danger, 2f);
        e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>播放音效（wav / mp3 等，用 Windows MCI；要喺 UI thread 用）。一直重複嘅會喺腳本停嗰陣停。</summary>
internal static class SoundFx
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int mciSendString(string cmd, System.Text.StringBuilder? ret, int len, nint cb);

    private sealed record Clip(string Owner, string Alias, bool Loop);
    private static readonly List<Clip> Clips = new();
    private static int _n;
    private static System.Windows.Forms.Timer? _gc;

    public static void Play(string owner, string path, bool loop)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return;
            if (owner == "preview") StopAll("preview", includeOnce: true);
            string alias = "mtfx" + (++_n);
            if (mciSendString($"open \"{path}\" type mpegvideo alias {alias}", null, 0, 0) != 0) return;
            mciSendString($"play {alias}" + (loop ? " repeat" : ""), null, 0, 0);
            Clips.Add(new Clip(owner, alias, loop));
            EnsureGc();
        }
        catch { }
    }

    /// <summary>停呢個腳本嘅音效（預設淨係停一直重複嗰啲；播一次嘅等佢播完）。</summary>
    public static void StopAll(string owner, bool includeOnce = false)
    {
        foreach (var c in Clips.Where(c => c.Owner == owner && (c.Loop || includeOnce)).ToList())
        {
            try { mciSendString($"close {c.Alias}", null, 0, 0); } catch { }
            Clips.Remove(c);
        }
    }

    // 播完嘅自動閂返
    private static void EnsureGc()
    {
        if (_gc != null) return;
        _gc = new System.Windows.Forms.Timer { Interval = 1000 };
        _gc.Tick += (_, _) =>
        {
            var sb = new System.Text.StringBuilder(32);
            foreach (var c in Clips.Where(c => !c.Loop).ToList())
            {
                sb.Clear();
                mciSendString($"status {c.Alias} mode", sb, sb.Capacity, 0);
                if (sb.ToString() != "playing")
                {
                    mciSendString($"close {c.Alias}", null, 0, 0);
                    Clips.Remove(c);
                }
            }
        };
        _gc.Start();
    }
}
