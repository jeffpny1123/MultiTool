using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.Common;
using MultiTool.UI;

namespace MultiTool.Tools;

// =====================================================================
//  工具頁共用：掣、選單、細提示
// =====================================================================
internal static class TUi
{
    public static FancyButton Btn(string text, Action click, int w = 0, BtnKind kind = BtnKind.Ghost)
    {
        var b = new FancyButton { Text = text, Kind = kind, Font = Ui.F(9.75f) };
        b.Size = new Size(w > 0 ? Ui.S(w) : b.GetPreferredSize(Size.Empty).Width, Ui.S(34));
        b.Click += (_, _) => click();
        return b;
    }

    public static Label L(string t, Color? c = null) =>
        Ui.Text_(t, c ?? Ui.TextDim, Ui.F(9.75f), new Padding(0, Ui.S(9), Ui.S(6), 0));

    public static FlowLayoutPanel Row(params Control[] cs)
    {
        var r = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, WrapContents = true, Margin = new Padding(0, 0, 0, Ui.S(10)) };
        foreach (var c in cs)
        {
            if (c is not Label && c is not CheckBox) c.Margin = new Padding(0, 0, Ui.S(10), 0);
            r.Controls.Add(c);
        }
        return r;
    }

    public static FlowLayoutPanel Col(params Control[] cs)
    {
        var col = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        foreach (var c in cs) col.Controls.Add(c);
        return col;
    }

    public static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var cb = new CheckBox
        {
            Text = text, Checked = value, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, Font = Ui.F(9.75f),
            Margin = new Padding(0, Ui.S(8), Ui.S(14), 0)
        };
        cb.CheckedChanged += (_, _) => changed(cb.Checked);
        return cb;
    }

    public static ContextMenuStrip Menu(Control owner)
    {
        var menu = new ContextMenuStrip();
        try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        menu.BackColor = Ui.CardHi;
        menu.ForeColor = Ui.Text;
        menu.Closed += (_, _) => owner.BeginInvoke(() => menu.Dispose());
        return menu;
    }

    /// <summary>喺掣下面彈選單揀一個值。</summary>
    public static void Pick<T>(Control at, IEnumerable<(string Name, T Value)> items, T current, Action<T> set, string? note = null)
    {
        var menu = Menu(at);
        foreach (var (name, value) in items)
        {
            var v = value;
            var it = new ToolStripMenuItem(name) { Checked = EqualityComparer<T>.Default.Equals(v, current), ForeColor = Ui.Text };
            it.Click += (_, _) => set(v);
            menu.Items.Add(it);
        }
        if (note != null)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(note) { Enabled = false });
        }
        menu.Show(at, new Point(0, at.Height));
    }

    /// <summary>音量 / 百分比格（數字 + %）。</summary>
    public static Control Percent(int value, int min, int max, int step, Action<int> set)
    {
        var nb = new NumberBox(Ui.S(84)) { Minimum = min, Maximum = max, Increment = step, Value = value, Margin = new Padding(0) };
        nb.ValueChanged += (_, _) => set((int)nb.Value);
        var wrap = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, WrapContents = false, Margin = new Padding(0, 0, Ui.S(14), 0) };
        wrap.Controls.Add(nb);
        wrap.Controls.Add(Ui.Text_("%", Ui.TextDim, Ui.F(9.75f), new Padding(Ui.S(4), Ui.S(9), 0, 0)));
        return wrap;
    }

    public static void Open(string path, bool folder = false)
    {
        try
        {
            if (folder) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); }
            else if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>喺檔案總管揀住個檔。</summary>
    public static void Reveal(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = true }); } catch { }
    }

    /// <summary>唔重複嘅檔名（file.mp4 → file (1).mp4）。</summary>
    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? "", name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    public static string KeyName(int vk) => vk switch
    {
        0 => Lang.T("（未設定）"),
        1 => Lang.T("滑鼠左鍵"),
        2 => Lang.T("滑鼠右鍵"),
        4 => Lang.T("滑鼠中鍵"),
        5 => Lang.T("滑鼠側鍵 1"),
        6 => Lang.T("滑鼠側鍵 2"),
        _ => Hotkey.KeyName(vk)
    };
}

/// <summary>右上角細提示（幾秒後自己消失，唔搶焦點，唔會被錄入片）。</summary>
internal sealed class Toast : Form
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    private static Toast? _current;
    private readonly string _text;
    private readonly Color _color;
    private readonly System.Windows.Forms.Timer _t = new();
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000008 | 0x80 | 0x20; // NOACTIVATE | TOPMOST | TOOLWINDOW | TRANSPARENT
            return cp;
        }
    }

    private Toast(string text, Color color, int ms)
    {
        _text = text;
        _color = color;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Panel;
        DoubleBuffered = true;
        var f = Ui.F(10.5f, FontStyle.Bold);
        var sz = TextRenderer.MeasureText(text, f);
        Size = new Size(Math.Min(Ui.S(520), sz.Width + Ui.S(40)), Ui.S(46));
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.Right - Width - Ui.S(16), wa.Top + Ui.S(70));
        _t.Interval = ms;
        _t.Tick += (_, _) => { _t.Stop(); Close(); };
    }

    public static void Show(string text, Color? color = null, int ms = 2200)
    {
        try
        {
            if (_current is { IsDisposed: false }) _current.Close();
            var t = new Toast(text, color ?? Ui.Text, ms);
            _current = t;
            t.Show();
            t._t.Start();
        }
        catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { SetWindowDisplayAffinity(Handle, 0x11); } catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Panel);
        using (var pen = new Pen(Ui.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        using (var b = new SolidBrush(_color)) g.FillRectangle(b, 0, 0, Ui.S(4), Height);
        TextRenderer.DrawText(g, _text, Ui.F(10.5f, FontStyle.Bold), new Rectangle(Ui.S(16), 0, Width - Ui.S(24), Height), Ui.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
}

/// <summary>一頁工具：上面一張張卡（可以捲動）。</summary>
internal class ToolPage : Panel
{
    protected readonly CardStack Stack = new();

    protected ToolPage()
    {
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        Stack.Dock = DockStyle.Fill;
        Controls.Add(Stack);
    }

    protected CardPanel Card(string title, string? hint, Control content)
    {
        var c = new CardPanel { Title = title, Hint = hint ?? "" };
        c.SetContent(content);
        Stack.AddRow(c);
        return c;
    }

    protected void Relayout() { Stack.PerformLayout(); Stack.Invalidate(true); }
}

/// <summary>撳一下，再撳一粒鍵（或者滑鼠中鍵 / 側鍵；目標可以揀左右鍵）。Esc 取消。</summary>
internal sealed class KeyCaptureBox : Control
{
    private int _vk;
    private bool _capturing, _hover;
    public bool AllowLeftRight;
    public event Action? Changed;

    public KeyCaptureBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Size = new Size(Ui.S(150), Ui.S(34));
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    public int Vk { get => _vk; set { _vk = value; Invalidate(); } }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_capturing)
        {
            int vk = e.Button switch
            {
                MouseButtons.Middle => 4, MouseButtons.XButton1 => 5, MouseButtons.XButton2 => 6,
                MouseButtons.Left when AllowLeftRight => 1, MouseButtons.Right when AllowLeftRight => 2, _ => 0
            };
            if (vk != 0) { Set(vk); return; }
            if (e.Button == MouseButtons.Right) { _capturing = false; Invalidate(); }
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            _capturing = true;
            Focus();
            Invalidate();
        }
    }

    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _capturing = false; Invalidate(); }

    protected override bool IsInputKey(Keys keyData) => _capturing || base.IsInputKey(keyData);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_capturing || (msg.Msg != 0x100 && msg.Msg != 0x104)) return base.ProcessCmdKey(ref msg, keyData);
        var code = keyData & Keys.KeyCode;
        if (code == Keys.Escape) { _capturing = false; Invalidate(); return true; }
        int vk = code switch
        {
            Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => 0x10,
            Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => 0x11,
            Keys.Menu or Keys.LMenu or Keys.RMenu => 0x12,
            _ => (int)code
        };
        Set(vk);
        return true;
    }

    private void Set(int vk)
    {
        _vk = vk;
        _capturing = false;
        Invalidate();
        Changed?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Field.BackFor(this));
        Ui.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Ui.Round(r, Ui.S(7f)))
        {
            using var b = new SolidBrush(Ui.Input);
            g.FillPath(b, path);
            using var pen = new Pen(_capturing ? Ui.Accent : _hover ? Ui.BorderHi : Ui.Border, _capturing ? Ui.S(1.5f) : 1f);
            g.DrawPath(pen, path);
        }
        string t = _capturing ? Lang.T("請撳一粒鍵…") : TUi.KeyName(_vk);
        TextRenderer.DrawText(g, t, Ui.F(9.75f), ClientRectangle, _capturing ? Ui.Accent : _vk == 0 ? Ui.TextFaint : Ui.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

// =====================================================================
//  遊戲改鍵
// =====================================================================
internal static class Remap
{
    public sealed class Rule
    {
        public int From, To;
        public string Process = ""; // 空 = 所有程式
    }

    public static readonly List<Rule> Rules = new();
    public static bool Enabled = true;
    public static event Action? Changed;

    private static string File_ => Path.Combine(AppPaths.Data, "remap.txt");

    private delegate nint HookProc(int code, nint w, nint l);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint h);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint h, int code, nint w, nint l);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

    [StructLayout(LayoutKind.Sequential)] private struct KBD { public uint vk, scan, flags, time; public nint extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLL { public Point pt; public uint data, flags, time; public nint extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }

    private static readonly HookProc KbProc = Kb, MsProc = Ms;
    private static nint _kb, _ms;
    private static readonly HashSet<int> Held = new(); // 而家撳住緊嘅目標鍵（關嗰陣要彈返起）
    private static readonly Dictionary<uint, string> ProcCache = new();

    static Remap()
    {
        try
        {
            if (File.Exists(File_))
                foreach (var l in File.ReadAllLines(File_))
                {
                    if (l.StartsWith("enabled=")) { Enabled = l.EndsWith("1"); continue; }
                    var p = l.Split('|');
                    if (p.Length >= 2 && int.TryParse(p[0], out int f) && int.TryParse(p[1], out int t))
                        Rules.Add(new Rule { From = f, To = t, Process = p.Length > 2 ? p[2] : "" });
                }
        }
        catch { }
        Extensions.Changed += () => Apply();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllLines(File_, new[] { "enabled=" + (Enabled ? 1 : 0) }
                .Concat(Rules.Select(r => $"{r.From}|{r.To}|{r.Process.Replace("|", "")}")));
        }
        catch { }
        Apply();
    }

    public static void SetEnabled(bool on)
    {
        Enabled = on;
        Save();
        Changed?.Invoke();
    }

    public static bool Active => _kb != 0;

    /// <summary>按設定裝 / 拆鍵盤滑鼠 hook（要喺 UI 執行緒叫）。</summary>
    public static void Apply()
    {
        bool want = Enabled && Extensions.IsInstalled(Extensions.Remap) && Rules.Any(r => r.From != 0 && r.To != 0);
        if (want && _kb == 0)
        {
            var mod = GetModuleHandle(null);
            _kb = SetWindowsHookEx(13, KbProc, mod, 0);
            _ms = SetWindowsHookEx(14, MsProc, mod, 0);
        }
        else if (!want && _kb != 0)
        {
            UnhookWindowsHookEx(_kb); _kb = 0;
            if (_ms != 0) { UnhookWindowsHookEx(_ms); _ms = 0; }
            foreach (var vk in Held.ToList()) Send(vk, true);
            Held.Clear();
        }
    }

    private static string ForegroundProcess()
    {
        var h = GetForegroundWindow();
        if (h == 0) return "";
        GetWindowThreadProcessId(h, out uint pid);
        if (ProcCache.TryGetValue(pid, out var n)) return n;
        try { n = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { n = ""; }
        if (ProcCache.Count > 200) ProcCache.Clear();
        ProcCache[pid] = n;
        return n;
    }

    private static Rule? Match(int vk)
    {
        Rule? any = null;
        string? fg = null;
        foreach (var r in Rules)
        {
            if (r.From != vk || r.To == 0) continue;
            if (r.Process.Length == 0) { any ??= r; continue; }
            fg ??= ForegroundProcess();
            string want = r.Process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? r.Process[..^4] : r.Process;
            if (string.Equals(fg, want, StringComparison.OrdinalIgnoreCase)) return r; // 指定程式嘅規則優先
        }
        return any;
    }

    /// <summary>MultiTool 自己喺最前（例如設定緊改鍵）：唔改。</summary>
    private static bool SelfInFront()
    {
        var h = GetForegroundWindow();
        if (h == 0) return false;
        GetWindowThreadProcessId(h, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static nint Kb(int code, nint w, nint l)
    {
        if (code >= 0 && !SelfInFront())
        {
            var k = Marshal.PtrToStructure<KBD>(l);
            if ((k.flags & 0x10) == 0) // 唔理自己（同其他程式）模擬出嚟嘅按鍵
            {
                int vk = (int)k.vk;
                vk = vk switch { 0xA0 or 0xA1 => 0x10, 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12, _ => vk };
                var r = Match(vk);
                if (r != null)
                {
                    bool up = w == 0x101 || w == 0x105;
                    Send(r.To, up);
                    return 1; // 食咗原本個鍵
                }
            }
        }
        return CallNextHookEx(_kb, code, w, l);
    }

    private static nint Ms(int code, nint w, nint l)
    {
        if (code >= 0 && (w is 0x207 or 0x208 or 0x20B or 0x20C) && !SelfInFront())
        {
            var m = Marshal.PtrToStructure<MSLL>(l);
            if ((m.flags & 0x1) == 0)
            {
                int vk = 0;
                bool up = false;
                switch ((int)w)
                {
                    case 0x207: vk = 4; break;
                    case 0x208: vk = 4; up = true; break;
                    case 0x20B: vk = (m.data >> 16) == 1 ? 5 : 6; break;
                    case 0x20C: vk = (m.data >> 16) == 1 ? 5 : 6; up = true; break;
                }
                if (vk != 0 && Match(vk) is { } r)
                {
                    Send(r.To, up);
                    return 1;
                }
            }
        }
        return CallNextHookEx(_ms, code, w, l);
    }

    private static void Send(int vk, bool up)
    {
        if (up) Held.Remove(vk); else Held.Add(vk);
        var inp = new INPUT[1];
        if (vk is 1 or 2 or 4 or 5 or 6)
        {
            uint flags = vk switch
            {
                1 => up ? 0x4u : 0x2u, 2 => up ? 0x10u : 0x8u, 4 => up ? 0x40u : 0x20u, _ => up ? 0x100u : 0x80u
            };
            inp[0] = new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = vk == 5 ? 1u : vk == 6 ? 2u : 0u } } };
        }
        else
        {
            // 用掃描碼送（遊戲多數讀掃描碼）
            uint scan = MapVirtualKey((uint)vk, 0);
            bool ext = vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F;
            uint flags = 0x8 | (ext ? 0x1u : 0) | (up ? 0x2u : 0);
            if (scan == 0) flags &= ~0x8u;
            inp[0] = new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = scan == 0 ? (ushort)vk : (ushort)0, wScan = (ushort)scan, dwFlags = flags } } };
        }
        SendInput(1, inp, Marshal.SizeOf<INPUT>());
    }
}

internal sealed class RemapPage : ToolPage
{
    private readonly FancyButton _toggle;
    private readonly FlowLayoutPanel _list;

    public static Control Build() => new RemapPage();

    private RemapPage()
    {
        _toggle = TUi.Btn("", () => { Remap.SetEnabled(!Remap.Enabled); Toast.Show(Remap.Enabled ? Lang.T("遊戲改鍵：已開啟") : Lang.T("遊戲改鍵：已關閉"), Remap.Enabled ? Ui.Success : Ui.TextDim); }, 150);
        HotkeyHub.Get("remap.toggle").Action = () =>
        {
            Remap.SetEnabled(!Remap.Enabled);
            Toast.Show(Remap.Enabled ? Lang.T("遊戲改鍵：已開啟") : Lang.T("遊戲改鍵：已關閉"), Remap.Enabled ? Ui.Success : Ui.TextDim);
        };
        Card(Lang.T("遊戲改鍵"),
            Lang.T("將一粒鍵變成另一粒鍵（例如 Caps Lock → Ctrl、滑鼠側鍵 → F）。可以只限某個程式先生效。"),
            TUi.Col(
                TUi.Row(_toggle, TUi.L(Lang.T("開 / 關快捷鍵")), HotkeyHub.Box("remap.toggle", 140)),
                new WrapText(Lang.T("遊戲用管理員權限開嘅話，MultiTool 都要用管理員身份開，改鍵先會生效。部分有反作弊嘅遊戲可能唔收模擬按鍵。"))));

        _list = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var add = TUi.Btn(Lang.T("+ 新增規則"), () => { Remap.Rules.Add(new Remap.Rule()); Remap.Save(); BuildRows(); }, 0, BtnKind.Primary);
        Card(Lang.T("規則"), Lang.T("撳左邊格仔再撳原本嘅鍵，右邊格仔撳你想變成嘅鍵（亦可以撳滑鼠鍵）。"), TUi.Col(_list, TUi.Row(add)));
        BuildRows();
        UpdateToggle();
        Remap.Changed += OnChanged;
        Disposed += (_, _) => Remap.Changed -= OnChanged;
        Remap.Apply();
    }

    private void OnChanged() { if (!IsDisposed) BeginInvoke(UpdateToggle); }

    private void UpdateToggle()
    {
        _toggle.Text = Remap.Enabled ? Lang.T("● 改鍵開啟中") : Lang.T("○ 改鍵已關閉");
        _toggle.Kind = Remap.Enabled ? BtnKind.Success : BtnKind.Default;
        _toggle.Invalidate();
    }

    private void BuildRows()
    {
        _list.SuspendLayout();
        foreach (Control c in _list.Controls.Cast<Control>().ToList()) { _list.Controls.Remove(c); c.Dispose(); }
        if (Remap.Rules.Count == 0) _list.Controls.Add(TUi.L(Lang.T("未有規則，撳下面「+ 新增規則」。")));
        foreach (var rule in Remap.Rules.ToList())
        {
            var r = rule;
            var from = new KeyCaptureBox { Vk = r.From };
            from.Changed += () => { r.From = from.Vk; Remap.Save(); };
            var to = new KeyCaptureBox { Vk = r.To, AllowLeftRight = true };
            to.Changed += () => { r.To = to.Vk; Remap.Save(); };
            var proc = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Text = r.Process };
            proc.DropDown += (_, _) =>
            {
                proc.Items.Clear();
                proc.Items.Add(Lang.T("（所有程式）"));
                foreach (var n in Scripts.Win.List().Select(w => w.Process).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n))
                    proc.Items.Add(n);
            };
            void SetProc()
            {
                string t = proc.Text.Trim();
                if (t == Lang.T("（所有程式）")) t = "";
                if (t == r.Process) return;
                r.Process = t;
                Remap.Save();
            }
            proc.SelectedIndexChanged += (_, _) => BeginInvoke(SetProc);
            proc.Leave += (_, _) => SetProc();
            if (r.Process.Length == 0) proc.Text = Lang.T("（所有程式）");
            var del = TUi.Btn("✕", () => { Remap.Rules.Remove(r); Remap.Save(); BuildRows(); }, 40);
            var row = TUi.Row(from, TUi.L("→"), to, TUi.L(Lang.T("只限")), new InputHost(proc, Ui.S(200)), del);
            row.Margin = new Padding(0, 0, 0, Ui.S(6));
            _list.Controls.Add(row);
        }
        _list.ResumeLayout(true);
        Relayout();
    }
}

// =====================================================================
//  視窗管理
// =====================================================================
internal static class WinTools
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint h, int i, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint h);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_EX_TOPMOST = 0x8, WS_EX_LAYERED = 0x80000;
    private const uint NOSIZE = 0x1, NOMOVE = 0x2, NOZORDER = 0x4, NOACTIVATE = 0x10, FRAMECHANGED = 0x20;

    private static readonly Dictionary<nint, (int Style, Rectangle Rect)> Saved = new();

    public static nint Foreground() => GetForegroundWindow();

    public static bool IsTop(nint h) => (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    public static void SetTop(nint h, bool on) =>
        SetWindowPos(h, on ? -1 : -2, 0, 0, 0, 0, NOMOVE | NOSIZE | NOACTIVATE);

    public static int Opacity(nint h)
    {
        if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_LAYERED) == 0) return 100;
        return GetLayeredWindowAttributes(h, out _, out byte a, out uint f) && (f & 2) != 0 ? (int)Math.Round(a / 2.55) : 100;
    }

    public static void SetOpacity(nint h, int percent)
    {
        percent = Math.Clamp(percent, 10, 100);
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        if (percent >= 100)
        {
            if ((ex & WS_EX_LAYERED) != 0)
            {
                SetLayeredWindowAttributes(h, 0, 255, 2);
                SetWindowLong(h, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
            }
            return;
        }
        if ((ex & WS_EX_LAYERED) == 0) SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_LAYERED);
        SetLayeredWindowAttributes(h, 0, (byte)Math.Round(percent * 2.55), 2);
    }

    public static bool IsBorderless(nint h) => Saved.ContainsKey(h) && IsWindow(h);

    /// <summary>無邊框全螢幕：拎走標題列同邊框，攤滿佢所在嘅螢幕；再叫一次還原。</summary>
    public static void ToggleBorderless(nint h)
    {
        if (Saved.TryGetValue(h, out var s))
        {
            Saved.Remove(h);
            SetWindowLong(h, GWL_STYLE, s.Style);
            SetWindowPos(h, 0, s.Rect.X, s.Rect.Y, s.Rect.Width, s.Rect.Height, NOZORDER | NOACTIVATE | FRAMECHANGED);
            return;
        }
        GetWindowRect(h, out var r);
        var rect = Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
        int style = GetWindowLong(h, GWL_STYLE);
        Saved[h] = (style, rect);
        SetWindowLong(h, GWL_STYLE, style & ~(WS_CAPTION | WS_THICKFRAME));
        var b = Screen.FromRectangle(rect).Bounds;
        SetWindowPos(h, 0, b.X, b.Y, b.Width, b.Height, NOZORDER | NOACTIVATE | FRAMECHANGED);
    }

    public static void Center(nint h)
    {
        GetWindowRect(h, out var r);
        var rect = Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
        var wa = Screen.FromRectangle(rect).WorkingArea;
        SetWindowPos(h, 0, wa.X + (wa.Width - rect.Width) / 2, wa.Y + (wa.Height - rect.Height) / 2, 0, 0, NOSIZE | NOZORDER | NOACTIVATE);
    }

    /// <summary>可以處理嘅視窗（唔包自己）。</summary>
    public static List<Scripts.Win.WinInfo> List()
    {
        int me = Environment.ProcessId;
        var mine = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        return Scripts.Win.List().Where(w => !string.Equals(w.Process, mine, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static string Describe(nint h)
    {
        var w = Scripts.Win.List().FirstOrDefault(x => x.Handle == h);
        return w == null ? "" : (w.Title.Length > 0 ? w.Title : w.Process);
    }
}

internal sealed class WindowPage : ToolPage
{
    private readonly FlowLayoutPanel _list;

    public static Control Build() => new WindowPage();

    private WindowPage()
    {
        HotkeyHub.Get("window.top").Action = () =>
        {
            var h = WinTools.Foreground();
            if (h == 0 || h == FindForm()?.Handle) return;
            bool on = !WinTools.IsTop(h);
            WinTools.SetTop(h, on);
            Toast.Show((on ? Lang.T("已置頂：") : Lang.T("取消置頂：")) + WinTools.Describe(h), on ? Ui.Accent : Ui.TextDim);
            if (Visible) BuildRows();
        };
        HotkeyHub.Get("window.borderless").Action = () =>
        {
            var h = WinTools.Foreground();
            if (h == 0 || h == FindForm()?.Handle) return;
            WinTools.ToggleBorderless(h);
            Toast.Show((WinTools.IsBorderless(h) ? Lang.T("無邊框全螢幕：") : Lang.T("已還原：")) + WinTools.Describe(h), Ui.Accent);
            if (Visible) BuildRows();
        };
        Card(Lang.T("視窗管理"), Lang.T("將任何程式視窗置頂、設透明度、變無邊框全螢幕或者置中。快捷鍵會作用喺而家最前面嘅視窗。"),
            TUi.Col(
                TUi.Row(TUi.L(Lang.T("置頂 / 取消")), HotkeyHub.Box("window.top", 140)),
                TUi.Row(TUi.L(Lang.T("無邊框全螢幕 / 還原")), HotkeyHub.Box("window.borderless", 140))));
        _list = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var refresh = TUi.Btn(Lang.T("↻ 重新整理"), BuildRows);
        Card(Lang.T("而家開住嘅視窗"), Lang.T("無邊框會攤滿視窗所在嘅螢幕；再撳一次還原。透明度最低 10%。"), TUi.Col(TUi.Row(refresh), _list));
        VisibleChanged += (_, _) => { if (Visible) BuildRows(); };
        BuildRows();
    }

    private void BuildRows()
    {
        if (IsDisposed) return;
        _list.SuspendLayout();
        foreach (Control c in _list.Controls.Cast<Control>().ToList()) { _list.Controls.Remove(c); c.Dispose(); }
        var wins = WinTools.List();
        if (wins.Count == 0) _list.Controls.Add(TUi.L(Lang.T("搵唔到視窗")));
        foreach (var w in wins.Take(40))
        {
            var h = w.Handle;
            var name = new Label
            {
                Text = (w.Title.Length > 0 ? w.Title : w.Process) + "  ·  " + w.Process, AutoSize = false, Width = Ui.S(330), Height = Ui.S(34),
                ForeColor = Ui.Text, BackColor = Ui.Card, Font = Ui.F(9.5f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                UseMnemonic = false, Margin = new Padding(0, 0, Ui.S(10), 0)
            };
            FancyButton? top = null, bl = null;
            top = TUi.Btn(WinTools.IsTop(h) ? Lang.T("✓ 置頂") : Lang.T("置頂"), () =>
            {
                bool on = !WinTools.IsTop(h);
                WinTools.SetTop(h, on);
                top!.Text = on ? Lang.T("✓ 置頂") : Lang.T("置頂");
                top.Kind = on ? BtnKind.Primary : BtnKind.Ghost;
                top.Invalidate();
            }, 80, WinTools.IsTop(h) ? BtnKind.Primary : BtnKind.Ghost);
            bl = TUi.Btn(WinTools.IsBorderless(h) ? Lang.T("✓ 無邊框") : Lang.T("無邊框"), () =>
            {
                WinTools.ToggleBorderless(h);
                bool on = WinTools.IsBorderless(h);
                bl!.Text = on ? Lang.T("✓ 無邊框") : Lang.T("無邊框");
                bl.Kind = on ? BtnKind.Primary : BtnKind.Ghost;
                bl.Invalidate();
            }, 96, WinTools.IsBorderless(h) ? BtnKind.Primary : BtnKind.Ghost);
            var center = TUi.Btn(Lang.T("置中"), () => WinTools.Center(h), 64);
            var op = TUi.Percent(WinTools.Opacity(h), 10, 100, 10, v => WinTools.SetOpacity(h, v));
            var row = TUi.Row(name, top, bl, center, TUi.L(Lang.T("透明度")), op);
            row.WrapContents = false;
            row.Margin = new Padding(0, 0, 0, Ui.S(6));
            _list.Controls.Add(row);
        }
        _list.ResumeLayout(true);
        Relayout();
    }
}

// =====================================================================
//  剪貼簿紀錄
// =====================================================================
internal static class ClipHistory
{
    public sealed class Item
    {
        public string? Text;
        public Bitmap? Image;
        public DateTime Time = DateTime.Now;
        public bool Pinned;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(nint h);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(nint h);

    public static readonly List<Item> Items = new();
    public static int Max = 50;
    public static bool Persist;
    public static event Action? Changed;
    private static Listener? _listener;
    private static string? _ignoreText;
    private static bool _ignoreImage;
    private static string File_ => Path.Combine(AppPaths.Data, "clipboard.txt");
    private static string Settings_ => Path.Combine(AppPaths.Data, "clipboard_settings.txt");

    static ClipHistory()
    {
        try
        {
            if (File.Exists(Settings_))
                foreach (var l in File.ReadAllLines(Settings_))
                {
                    if (l.StartsWith("max=") && int.TryParse(l[4..], out int m)) Max = Math.Clamp(m, 10, 500);
                    if (l.StartsWith("persist=")) Persist = l.EndsWith("1");
                }
            if (Persist && File.Exists(File_))
                foreach (var l in File.ReadAllLines(File_))
                {
                    var p = l.Split('|');
                    if (p.Length < 3) continue;
                    try
                    {
                        Items.Add(new Item
                        {
                            Time = new DateTime(long.Parse(p[0])), Pinned = p[1] == "1",
                            Text = Encoding.UTF8.GetString(Convert.FromBase64String(p[2]))
                        });
                    }
                    catch { }
                }
        }
        catch { }
        Extensions.Changed += () => Apply();
    }

    public static void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllLines(Settings_, new[] { "max=" + Max, "persist=" + (Persist ? 1 : 0) });
        }
        catch { }
        SaveItems();
    }

    private static void SaveItems()
    {
        try
        {
            if (!Persist) { if (File.Exists(File_)) File.Delete(File_); return; }
            File.WriteAllLines(File_, Items.Where(i => i.Text != null)
                .Select(i => $"{i.Time.Ticks}|{(i.Pinned ? 1 : 0)}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(i.Text!))}"));
        }
        catch { }
    }

    /// <summary>裝咗就開始聽剪貼簿（要喺 UI 執行緒叫）。</summary>
    public static void Apply()
    {
        bool want = Extensions.IsInstalled(Extensions.Clipboard);
        if (want && _listener == null)
        {
            _listener = new Listener();
            _listener.CreateHandle(new CreateParams());
            AddClipboardFormatListener(_listener.Handle);
        }
        else if (!want && _listener != null)
        {
            RemoveClipboardFormatListener(_listener.Handle);
            _listener.DestroyHandle();
            _listener = null;
        }
    }

    private sealed class Listener : NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x031D) Capture();
            base.WndProc(ref m);
        }
    }

    private static void Capture()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string t = Clipboard.GetText();
                if (t.Length == 0 || t.Length > 200_000) return;
                if (t == _ignoreText) { _ignoreText = null; return; }
                var same = Items.FirstOrDefault(i => i.Text == t);
                if (same != null) { Items.Remove(same); same.Time = DateTime.Now; Items.Insert(0, same); }
                else Items.Insert(0, new Item { Text = t });
            }
            else if (Clipboard.ContainsImage())
            {
                if (_ignoreImage) { _ignoreImage = false; return; }
                using var img = Clipboard.GetImage();
                if (img == null) return;
                Items.Insert(0, new Item { Image = new Bitmap(img) });
                // 圖片佔記憶體：最多留 10 張
                foreach (var old in Items.Where(i => i.Image != null && !i.Pinned).Skip(10).ToList()) { old.Image!.Dispose(); Items.Remove(old); }
            }
            else return;
            Trim();
            SaveItems();
            Changed?.Invoke();
        }
        catch { }
    }

    private static void Trim()
    {
        var extra = Items.Where(i => !i.Pinned).Skip(Max).ToList();
        foreach (var i in extra) { i.Image?.Dispose(); Items.Remove(i); }
    }

    /// <summary>放返入剪貼簿（唔會再記多一次）。</summary>
    public static void CopyBack(Item i)
    {
        try
        {
            if (i.Text != null) { _ignoreText = i.Text; Clipboard.SetText(i.Text); }
            else if (i.Image != null) { _ignoreImage = true; Clipboard.SetImage(i.Image); }
            Items.Remove(i);
            i.Time = DateTime.Now;
            Items.Insert(0, i);
            SaveItems();
            Changed?.Invoke();
        }
        catch { }
    }

    public static void Remove(Item i)
    {
        Items.Remove(i);
        i.Image?.Dispose();
        SaveItems();
        Changed?.Invoke();
    }

    public static void Clear()
    {
        foreach (var i in Items.Where(i => !i.Pinned).ToList()) { i.Image?.Dispose(); Items.Remove(i); }
        SaveItems();
        Changed?.Invoke();
    }

    public static void TogglePin(Item i)
    {
        i.Pinned = !i.Pinned;
        SaveItems();
        Changed?.Invoke();
    }

    public static string Preview(Item i, int max = 120)
    {
        if (i.Text == null) return Lang.F("[圖片 {0}×{1}]", i.Image?.Width ?? 0, i.Image?.Height ?? 0);
        var t = Regex.Replace(i.Text, @"\s+", " ").Trim();
        return t.Length > max ? t[..max] + "…" : t;
    }
}

/// <summary>剪貼簿紀錄細視窗（快捷鍵叫出）：撳 1-9 或者撳一下就貼上。</summary>
internal sealed class ClipPopup : Form
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    private readonly nint _prev;
    private readonly List<ClipHistory.Item> _items;
    private int _hover = -1;
    private static ClipPopup? _open;

    public static void ShowAtCursor()
    {
        if (_open is { IsDisposed: false }) { _open.Close(); return; }
        if (ClipHistory.Items.Count == 0) { Toast.Show(Lang.T("剪貼簿紀錄係空嘅"), Ui.TextDim); return; }
        _open = new ClipPopup();
        _open.Show();
        Scripts.Win.Activate(_open.Handle);
    }

    private int RowH => Ui.S(34);

    private ClipPopup()
    {
        _prev = GetForegroundWindow();
        _items = ClipHistory.Items.Take(9).ToList();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Panel;
        DoubleBuffered = true;
        KeyPreview = true;
        Size = new Size(Ui.S(380), Ui.S(30) + _items.Count * RowH + Ui.S(8));
        var p = Cursor.Position;
        var wa = Screen.FromPoint(p).WorkingArea;
        Location = new Point(Math.Min(p.X, wa.Right - Width), Math.Min(p.Y, wa.Bottom - Height));
        Deactivate += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            int n = e.KeyCode is >= Keys.D1 and <= Keys.D9 ? e.KeyCode - Keys.D1 : e.KeyCode is >= Keys.NumPad1 and <= Keys.NumPad9 ? e.KeyCode - Keys.NumPad1 : -1;
            if (n >= 0 && n < _items.Count) Choose(n);
        };
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = (e.Y - Ui.S(30)) / RowH;
        int h = e.Y >= Ui.S(30) && i < _items.Count ? i : -1;
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (_hover >= 0) Choose(_hover);
    }

    private void Choose(int i)
    {
        var item = _items[i];
        ClipHistory.CopyBack(item);
        Close();
        var prev = _prev;
        // 返返之前個視窗，再撳 Ctrl + V
        Task.Run(async () =>
        {
            await Task.Delay(60);
            if (prev != 0) Scripts.Win.Activate(prev);
            await Task.Delay(120);
            Keyboard.Down(0x11);
            Keyboard.Press(0x56, 30);
            Keyboard.Up(0x11);
        });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Panel);
        using (var pen = new Pen(Ui.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(g, Lang.T("剪貼簿紀錄（撳 1-9 貼上，Esc 關閉）"), Ui.F(9f), new Rectangle(Ui.S(12), 0, Width, Ui.S(30)), Ui.TextDim,
            TextFormatFlags.VerticalCenter);
        for (int i = 0; i < _items.Count; i++)
        {
            var r = new Rectangle(Ui.S(4), Ui.S(30) + i * RowH, Width - Ui.S(8), RowH);
            if (i == _hover) using (var b = new SolidBrush(Ui.CardHi)) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, (i + 1).ToString(), Ui.F(9.5f, FontStyle.Bold), new Rectangle(r.X + Ui.S(8), r.Y, Ui.S(20), r.Height), Ui.Accent,
                TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, ClipHistory.Preview(_items[i], 80), Ui.F(9.5f), new Rectangle(r.X + Ui.S(30), r.Y, r.Width - Ui.S(36), r.Height),
                _items[i].Text == null ? Ui.TextDim : Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); if (_open == this) _open = null; }
}

internal sealed class ClipboardPage : ToolPage
{
    private readonly FlowLayoutPanel _list;
    private readonly TextBox _query = new();
    private bool _dirty;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 150 };

    public static Control Build() => new ClipboardPage();

    private ClipboardPage()
    {
        HotkeyHub.Get("clipboard.show").Action = ClipPopup.ShowAtCursor;
        ClipHistory.Apply();
        FancyButton? maxBtn = null;
        maxBtn = TUi.Btn(Lang.F("最多 {0} 項", ClipHistory.Max), () =>
            TUi.Pick(maxBtn!, new[] { 20, 50, 100, 200 }.Select(n => (Lang.F("最多 {0} 項", n), n)), ClipHistory.Max, n =>
            {
                ClipHistory.Max = n;
                ClipHistory.SaveSettings();
                maxBtn!.Text = Lang.F("最多 {0} 項", n);
                maxBtn.Invalidate();
            }), 120);
        var persist = TUi.Check(Lang.T("重開程式都保留文字紀錄"), ClipHistory.Persist, v => { ClipHistory.Persist = v; ClipHistory.SaveSettings(); });
        Card(Lang.T("剪貼簿紀錄"), Lang.T("自動記低你複製過嘅文字同圖片。撳一項就放返入剪貼簿；用快捷鍵可以喺任何地方叫出細視窗，撳 1-9 直接貼上。"),
            TUi.Col(
                TUi.Row(TUi.L(Lang.T("打開紀錄視窗")), HotkeyHub.Box("clipboard.show", 140)),
                TUi.Row(maxBtn, persist),
                new WrapText(Lang.T("圖片唔會儲存落硬碟，最多留 10 張；密碼之類嘅敏感資料複製完記得喺度刪走。"))));

        _query.PlaceholderText = Lang.T("搜尋紀錄…");
        _query.TextChanged += (_, _) => MarkDirty();
        var clear = TUi.Btn(Lang.T("清除全部（釘選嘅除外）"), () =>
        {
            if (MessageBox.Show(FindForm(), Lang.T("清除所有剪貼簿紀錄？"), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                ClipHistory.Clear();
        });
        _list = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Card(Lang.T("紀錄"), null, TUi.Col(TUi.Row(new InputHost(_query, Ui.S(280)), clear), _list));
        _debounce.Tick += (_, _) => { _debounce.Stop(); if (_dirty) BuildRows(); };
        ClipHistory.Changed += MarkDirty;
        Disposed += (_, _) => ClipHistory.Changed -= MarkDirty;
        VisibleChanged += (_, _) => { if (Visible && _dirty) BuildRows(); };
        BuildRows();
    }

    private void MarkDirty()
    {
        _dirty = true;
        if (Visible && !IsDisposed) { _debounce.Stop(); _debounce.Start(); }
    }

    private void BuildRows()
    {
        _dirty = false;
        _list.SuspendLayout();
        foreach (Control c in _list.Controls.Cast<Control>().ToList()) { _list.Controls.Remove(c); c.Dispose(); }
        var words = _query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var items = ClipHistory.Items.OrderByDescending(i => i.Pinned)
            .Where(i => words.Length == 0 || (i.Text != null && words.All(w => i.Text.Contains(w, StringComparison.OrdinalIgnoreCase)))).Take(100).ToList();
        if (items.Count == 0) _list.Controls.Add(TUi.L(Lang.T("未有紀錄")));
        foreach (var it in items)
        {
            var item = it;
            Control preview;
            if (item.Image != null)
            {
                var pb = new PictureBox
                {
                    Image = item.Image, SizeMode = PictureBoxSizeMode.Zoom, Width = Ui.S(160), Height = Ui.S(70), BackColor = Ui.Input,
                    Cursor = Cursors.Hand, Margin = new Padding(0, 0, Ui.S(10), 0)
                };
                pb.Click += (_, _) => { ClipHistory.CopyBack(item); Toast.Show(Lang.T("已複製"), Ui.Success, 1200); };
                preview = pb;
            }
            else
            {
                var lb = new Label
                {
                    Text = ClipHistory.Preview(item, 200), AutoSize = false, Width = Ui.S(460), Height = Ui.S(40), ForeColor = Ui.Text, BackColor = Ui.Card,
                    Font = Ui.F(9.5f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false, Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, Ui.S(10), 0)
                };
                lb.Click += (_, _) => { ClipHistory.CopyBack(item); Toast.Show(Lang.T("已複製"), Ui.Success, 1200); };
                preview = lb;
            }
            var time = TUi.L(item.Time.ToString(item.Time.Date == DateTime.Today ? "HH:mm" : "MM-dd HH:mm"), Ui.TextFaint);
            var pin = TUi.Btn(item.Pinned ? Lang.T("✓ 釘選") : Lang.T("釘選"), () => ClipHistory.TogglePin(item), 76, item.Pinned ? BtnKind.Primary : BtnKind.Ghost);
            var del = TUi.Btn("✕", () => ClipHistory.Remove(item), 40);
            var row = TUi.Row(preview, time, pin, del);
            row.WrapContents = false;
            row.Margin = new Padding(0, 0, 0, Ui.S(6));
            _list.Controls.Add(row);
        }
        _list.ResumeLayout(true);
        Relayout();
    }
}

// =====================================================================
//  文字辨識（用 Windows 內置 OCR，經 PowerShell 叫）
// =====================================================================
internal static class Ocr
{
    private const string Script = @"param([string]$Path, [string]$Lang)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(-1) | Out-Null; $task.Result }
if ($Path -eq '--langs') {
  foreach ($l in [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages) { $l.LanguageTag + ""`t"" + $l.DisplayName }
  exit 0
}
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
$stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$dec = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bmp = Await ($dec.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
if ($Lang) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new($Lang)) }
else { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
if (-not $engine) { [Console]::Error.WriteLine('NOENGINE'); exit 2 }
$res = Await ($engine.RecognizeAsync($bmp)) ([Windows.Media.Ocr.OcrResult])
foreach ($line in $res.Lines) { $line.Text }
$stream.Dispose()
";

    private static string Dir => Path.Combine(AppPaths.Data, "tools", "ocr");
    private static string ScriptPath => Path.Combine(Dir, "ocr.ps1");
    private static List<(string Tag, string Name)>? _langs;

    private static string PowerShell =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private static async Task<(int Code, string Out, string Err)> Run(string args, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);
        // 每次寫一次（升級後內容會更新）；PowerShell 5 要有 BOM 先識讀 UTF-8
        await File.WriteAllTextAsync(ScriptPath, Script, new UTF8Encoding(true), ct);
        var psi = new ProcessStartInfo(PowerShell, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ScriptPath}\" {args}")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(ct);
        var e = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await o, await e);
    }

    /// <summary>部電腦裝咗邊啲 OCR 語言。</summary>
    public static async Task<List<(string Tag, string Name)>> LanguagesAsync()
    {
        if (_langs != null) return _langs;
        var (code, o, _) = await Run("--langs");
        var list = new List<(string, string)>();
        if (code == 0)
            foreach (var l in o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var p = l.Split('\t');
                if (p.Length == 2) list.Add((p[0], p[1]));
            }
        return _langs = list;
    }

    /// <summary>Windows OCR 最大 2600 像素：大過就縮細一份。</summary>
    private static string Prepare(string path)
    {
        try
        {
            using var img = Image.FromFile(path);
            int max = Math.Max(img.Width, img.Height);
            if (max <= 2600) return path;
            double k = 2600.0 / max;
            using var bmp = new Bitmap(img, Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k)));
            var p = Path.Combine(Path.GetTempPath(), "MultiTool-ocr-big.png");
            bmp.Save(p, System.Drawing.Imaging.ImageFormat.Png);
            return p;
        }
        catch { return path; }
    }

    public static async Task<string> RecognizeAsync(string imagePath, string lang, CancellationToken ct = default)
    {
        imagePath = Prepare(imagePath);
        var (code, o, e) = await Run($"\"{imagePath}\" \"{lang}\"", ct);
        if (code == 2 || e.Contains("NOENGINE"))
            throw new InvalidOperationException(Lang.T("Windows 冇呢個語言嘅文字辨識。請去 Windows 設定 → 時間與語言 → 語言，加入語言（連「光學字元辨識」）。"));
        if (code != 0) throw new InvalidOperationException(e.Trim().Split('\n').LastOrDefault() ?? Lang.T("辨識失敗"));
        // 中日韓文字之間 Windows OCR 會加空格：拎走
        string cjk = @"[\p{IsCJKUnifiedIdeographs}　-〿＀-￯぀-ヿ가-힯]";
        var text = Regex.Replace(o.Replace("\r", ""), $@"(?<={cjk}) +(?={cjk})", "");
        return text.Trim();
    }

    /// <summary>截一個螢幕範圍做圖（細字放大兩倍辨識會準啲）。</summary>
    public static string CaptureToFile(Rectangle r)
    {
        using var bmp = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        Bitmap final = bmp;
        if (r.Height < 600 && r.Width < 1300)
        {
            final = new Bitmap(r.Width * 2, r.Height * 2);
            using var g2 = Graphics.FromImage(final);
            g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g2.DrawImage(bmp, 0, 0, final.Width, final.Height);
        }
        var path = Path.Combine(Path.GetTempPath(), "MultiTool-ocr.png");
        final.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        if (final != bmp) final.Dispose();
        return path;
    }
}

internal sealed class OcrPage : ToolPage
{
    private readonly TextBox _result;
    private readonly Label _status;
    private readonly FancyButton _langBtn;
    private static string _lang = "";
    private static bool _autoCopy = true;
    private bool _busy;
    private static string Settings_ => Path.Combine(AppPaths.Data, "ocr.txt");

    public static Control Build() => new OcrPage();

    private OcrPage()
    {
        try
        {
            if (File.Exists(Settings_))
                foreach (var l in File.ReadAllLines(Settings_))
                {
                    if (l.StartsWith("lang=")) _lang = l[5..];
                    if (l.StartsWith("copy=")) _autoCopy = l.EndsWith("1");
                }
        }
        catch { }
        HotkeyHub.Get("ocr.capture").Action = () => _ = CaptureAsync();
        _langBtn = TUi.Btn("", ShowLangMenu, 180);
        _status = TUi.L("");
        var capture = TUi.Btn(Lang.T("▣ 框選畫面辨識"), () => _ = CaptureAsync(), 0, BtnKind.Primary);
        var file = TUi.Btn(Lang.T("從圖片辨識…"), () => _ = FromFileAsync());
        var auto = TUi.Check(Lang.T("辨識完自動複製"), _autoCopy, v => { _autoCopy = v; SaveSettings(); });
        Card(Lang.T("文字辨識"), Lang.T("框住畫面上任何文字（遊戲、圖片、影片都得）就抽出嚟。用 Windows 內置嘅辨識，唔使上網。"),
            TUi.Col(
                TUi.Row(capture, file, _status),
                TUi.Row(TUi.L(Lang.T("語言")), _langBtn, auto),
                TUi.Row(TUi.L(Lang.T("框選快捷鍵")), HotkeyHub.Box("ocr.capture", 140))));

        _result = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Ui.Input, ForeColor = Ui.Text,
            Font = Ui.F(10.5f), Width = Ui.S(640), Height = Ui.S(220), Margin = new Padding(0, 0, 0, Ui.S(10))
        };
        var copy = TUi.Btn(Lang.T("複製"), () => { if (_result.Text.Length > 0) { Clipboard.SetText(_result.Text); _status.Text = Lang.T("已複製"); } });
        var clear = TUi.Btn(Lang.T("清除"), () => _result.Clear());
        Card(Lang.T("結果"), null, TUi.Col(_result, TUi.Row(copy, clear)));
        UpdateLang();
    }

    private void SaveSettings()
    {
        try { File.WriteAllLines(Settings_, new[] { "lang=" + _lang, "copy=" + (_autoCopy ? 1 : 0) }); } catch { }
    }

    private void UpdateLang()
    {
        _langBtn.Text = Lang.T("語言：") + (_lang.Length == 0 ? Lang.T("自動") : _lang) + " ▾";
        _langBtn.Invalidate();
    }

    private async void ShowLangMenu()
    {
        _status.Text = Lang.T("讀取語言…");
        List<(string Tag, string Name)> langs;
        try { langs = await Ocr.LanguagesAsync(); }
        catch (Exception ex) { _status.Text = ex.Message; return; }
        _status.Text = "";
        var items = new List<(string, string)> { (Lang.T("自動（跟 Windows 語言）"), "") };
        items.AddRange(langs.Select(l => (l.Name + "  (" + l.Tag + ")", l.Tag)));
        TUi.Pick(_langBtn, items, _lang, v => { _lang = v; SaveSettings(); UpdateLang(); },
            Lang.T("冇你要嘅語言：Windows 設定 → 語言 → 加入語言"));
    }

    private async Task CaptureAsync()
    {
        if (_busy) return;
        Rectangle r;
        using (var p = new Video.RegionPicker(Lang.T("拉一個框揀要辨識嘅文字（Esc 取消）")))
        {
            if (p.ShowDialog() != DialogResult.OK) return;
            r = p.Result;
        }
        await Task.Delay(150); // 等個遮罩消失
        string path;
        try { path = Ocr.CaptureToFile(r); }
        catch (Exception ex) { _status.Text = ex.Message; return; }
        await RunAsync(path);
    }

    private async Task FromFileAsync()
    {
        using var dlg = new OpenFileDialog { Filter = Lang.T("圖片") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        await RunAsync(dlg.FileName);
    }

    private async Task RunAsync(string path)
    {
        _busy = true;
        _status.Text = Lang.T("辨識緊…");
        try
        {
            var text = await Ocr.RecognizeAsync(path, _lang);
            _result.Text = text.Replace("\n", "\r\n");
            if (text.Length == 0) { _status.Text = Lang.T("搵唔到文字"); Toast.Show(Lang.T("搵唔到文字"), Ui.TextDim); }
            else
            {
                if (_autoCopy) { Clipboard.SetText(text.Replace("\n", "\r\n")); _status.Text = Lang.T("完成，已複製"); }
                else _status.Text = Lang.T("完成");
                Toast.Show(Lang.T("文字辨識：") + (text.Length > 40 ? text[..40].Replace("\n", " ") + "…" : text.Replace("\n", " ")), Ui.Success);
            }
        }
        catch (Exception ex)
        {
            _status.Text = Lang.T("辨識失敗：") + ex.Message;
            Toast.Show(Lang.T("辨識失敗"), Ui.Danger);
        }
        finally { _busy = false; }
    }
}

// =====================================================================
//  影音轉檔（用 ffmpeg）
// =====================================================================
internal sealed class ConvertPage : ToolPage
{
    private sealed class Job
    {
        public required string Path;
        public string Status = "";
        public Label? Label;
    }

    private static readonly (string Ext, string Name, bool Image, bool Audio)[] Formats =
    {
        ("mp4", "MP4", false, false), ("mkv", "MKV", false, false), ("webm", "WEBM", false, false), ("avi", "AVI", false, false),
        ("mov", "MOV", false, false), ("gif", "GIF", false, false), ("mp3", "MP3", false, true), ("wav", "WAV", false, true),
        ("m4a", "M4A", false, true), ("png", "PNG", true, false), ("jpg", "JPG", true, false), ("webp", "WEBP", true, false)
    };

    private readonly List<Job> _jobs = new();
    private readonly FlowLayoutPanel _list;
    private readonly FancyButton _fmtBtn, _resBtn, _start, _outBtn;
    private readonly NumberBox _from, _to;
    private readonly Label _status;
    private string _fmt = "mp4", _outDir = "";
    private int _res;
    private CancellationTokenSource? _cts;

    public static Control Build() => new ConvertPage();

    private ConvertPage()
    {
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) Add(files); };
        Stack.AllowDrop = true;
        Stack.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        Stack.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) Add(files); };

        if (!Video.Ffmpeg.Ready) AddFfmpegCard();
        var add = TUi.Btn(Lang.T("+ 加入檔案…"), () =>
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Filter = Lang.T("所有檔案") + "|*.*" };
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK) Add(dlg.FileNames);
        }, 0, BtnKind.Primary);
        var clear = TUi.Btn(Lang.T("清空"), () => { if (_cts == null) { _jobs.Clear(); BuildRows(); } });
        _list = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Card(Lang.T("影音轉檔"), Lang.T("影片、聲音、圖片互相轉換，亦可以剪頭尾同縮細。可以直接拖檔案入嚟。"), TUi.Col(TUi.Row(add, clear), _list));

        _fmtBtn = TUi.Btn("", () => TUi.Pick(_fmtBtn!, Formats.Select(f => (f.Name + (f.Audio ? Lang.T("（淨聲音）") : f.Image ? Lang.T("（圖片）") : ""), f.Ext)), _fmt,
            v => { _fmt = v; UpdateLabels(); }), 140);
        _resBtn = TUi.Btn("", () => TUi.Pick(_resBtn!, new[] { 0, 2160, 1440, 1080, 720, 480, 360 }.Select(r => (r == 0 ? Lang.T("原始") : r + "P", r)), _res,
            v => { _res = v; UpdateLabels(); }, Lang.T("只會縮細，唔會放大")), 140);
        _from = new NumberBox(Ui.S(90)) { Minimum = 0, Maximum = 86400, Increment = 1, DecimalPlaces = 1 };
        _to = new NumberBox(Ui.S(90)) { Minimum = 0, Maximum = 86400, Increment = 1, DecimalPlaces = 1 };
        _outBtn = TUi.Btn("", () =>
        {
            var menu = TUi.Menu(_outBtn!);
            var same = new ToolStripMenuItem(Lang.T("同原本檔案一樣")) { Checked = _outDir.Length == 0, ForeColor = Ui.Text };
            same.Click += (_, _) => { _outDir = ""; UpdateLabels(); };
            var pick = new ToolStripMenuItem(Lang.T("揀資料夾…")) { Checked = _outDir.Length > 0, ForeColor = Ui.Text };
            pick.Click += (_, _) =>
            {
                using var dlg = new FolderBrowserDialog();
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK) { _outDir = dlg.SelectedPath; UpdateLabels(); }
            };
            menu.Items.Add(same);
            menu.Items.Add(pick);
            menu.Show(_outBtn!, new Point(0, _outBtn!.Height));
        }, 220);
        _start = TUi.Btn(Lang.T("▶ 開始轉檔"), () => _ = StartAsync(), 130, BtnKind.Success);
        _status = TUi.L("");
        Card(Lang.T("輸出"), Lang.T("剪片：開始 / 結束填 0 = 唔剪（單位：秒）。"),
            TUi.Col(
                TUi.Row(TUi.L(Lang.T("格式")), _fmtBtn, TUi.L(Lang.T("解像度")), _resBtn),
                TUi.Row(TUi.L(Lang.T("由")), _from, TUi.L(Lang.T("秒 至")), _to, TUi.L(Lang.T("秒"))),
                TUi.Row(TUi.L(Lang.T("儲存位置")), _outBtn),
                TUi.Row(_start, _status)));
        UpdateLabels();
        BuildRows();
    }

    /// <summary>未有 ffmpeg：頂部顯示下載卡，下載完自動收埋。</summary>
    private void AddFfmpegCard()
    {
        CardPanel? card = null;
        var bar = new ThinBar { Width = Ui.S(320), Margin = new Padding(0, Ui.S(14), Ui.S(12), 0) };
        var msg = TUi.L("");
        CancellationTokenSource? cts = null;
        FancyButton? btn = null;
        btn = TUi.Btn(Lang.T("⬇ 下載 ffmpeg"), async () =>
        {
            if (cts != null) { cts.Cancel(); return; }
            cts = new CancellationTokenSource();
            btn!.Text = Lang.T("取消"); btn.Kind = BtnKind.Ghost; btn.Invalidate();
            try
            {
                await Video.Ffmpeg.InstallAsync((t, p) => { try { BeginInvoke(() => { msg.Text = t; bar.Value = p; }); } catch { } }, cts.Token);
                if (card != null) Stack.RemoveRow(card);
                Relayout();
                Toast.Show(Lang.T("ffmpeg 下載好喇"));
                return;
            }
            catch (OperationCanceledException) { msg.Text = Lang.T("已取消"); }
            catch (Exception ex) { msg.Text = Lang.T("失敗：") + ex.Message; AppPaths.LogError("ffmpeg", ex); }
            finally { cts = null; }
            bar.Value = -1;
            btn.Text = Lang.T("⬇ 下載 ffmpeg"); btn.Kind = BtnKind.Primary; btn.Invalidate();
        }, 150, BtnKind.Primary);
        card = Card(Lang.T("要先下載 ffmpeg"), Lang.T("影音轉檔用 ffmpeg（免費開源第三方工具，~100 MB）。文件轉檔唔使。"), TUi.Row(btn, bar, msg));
    }

    private void UpdateLabels()
    {
        _fmtBtn.Text = Lang.T("格式：") + _fmt.ToUpperInvariant() + " ▾";
        _resBtn.Text = Lang.T("解像度：") + (_res == 0 ? Lang.T("原始") : _res + "P") + " ▾";
        _outBtn.Text = (_outDir.Length == 0 ? Lang.T("同原本檔案一樣") : _outDir) + " ▾";
        foreach (var b in new[] { _fmtBtn, _resBtn, _outBtn }) b.Invalidate();
    }

    private void Add(IEnumerable<string> files)
    {
        if (_cts != null) return;
        foreach (var f in files.Where(File.Exists))
            if (!_jobs.Any(j => string.Equals(j.Path, f, StringComparison.OrdinalIgnoreCase))) _jobs.Add(new Job { Path = f });
        BuildRows();
    }

    private void BuildRows()
    {
        _list.SuspendLayout();
        foreach (Control c in _list.Controls.Cast<Control>().ToList()) { _list.Controls.Remove(c); c.Dispose(); }
        if (_jobs.Count == 0) _list.Controls.Add(TUi.L(Lang.T("未有檔案：撳「+ 加入檔案…」或者直接拖入嚟")));
        foreach (var j in _jobs.ToList())
        {
            var job = j;
            var name = new Label
            {
                Text = System.IO.Path.GetFileName(job.Path), AutoSize = false, Width = Ui.S(360), Height = Ui.S(34), ForeColor = Ui.Text,
                BackColor = Ui.Card, Font = Ui.F(9.5f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false,
                Margin = new Padding(0, 0, Ui.S(10), 0)
            };
            job.Label = new Label
            {
                Text = job.Status, AutoSize = false, Width = Ui.S(200), Height = Ui.S(34), ForeColor = Ui.TextDim, BackColor = Ui.Card,
                Font = Ui.F(9.25f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = new Padding(0, 0, Ui.S(10), 0)
            };
            var del = TUi.Btn("✕", () => { if (_cts == null) { _jobs.Remove(job); BuildRows(); } }, 40);
            var row = TUi.Row(name, job.Label, del);
            row.WrapContents = false;
            row.Margin = new Padding(0, 0, 0, Ui.S(4));
            _list.Controls.Add(row);
        }
        _list.ResumeLayout(true);
        Relayout();
    }

    private static void SetStatus(Job j, string text, Color? c = null)
    {
        j.Status = text;
        if (j.Label is { IsDisposed: false } l) { l.Text = text; l.ForeColor = c ?? Ui.TextDim; }
    }

    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico" };

    private string BuildArgs(Job j, string outFile)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var f = Formats.First(x => x.Ext == _fmt);
        double from = (double)_from.Value, to = (double)_to.Value;
        var a = new StringBuilder("-hide_banner -y ");
        if (from > 0) a.Append($"-ss {from.ToString(inv)} ");
        a.Append($"-i \"{j.Path}\" ");
        if (to > 0 && to > from) a.Append($"-t {(to - from).ToString(inv)} ");
        string scale = _res > 0 ? $"scale=-2:'min(ih,{_res})':flags=lanczos" : "";
        if (f.Audio)
        {
            a.Append("-vn ");
            a.Append(_fmt switch
            {
                "mp3" => "-c:a libmp3lame -q:a 2 ",
                "wav" => "-c:a pcm_s16le ",
                _ => "-c:a aac -b:a 192k "
            });
        }
        else if (f.Image)
        {
            if (scale.Length > 0) a.Append($"-vf \"{scale}\" ");
            a.Append("-frames:v 1 -update 1 ");
            a.Append(_fmt switch { "jpg" => "-q:v 2 ", "webp" => "-c:v libwebp -quality 90 ", _ => "" });
        }
        else if (_fmt == "gif")
        {
            string sc = _res > 0 ? $"scale=-2:'min(ih,{_res})':flags=lanczos" : "scale=-2:'min(ih,480)':flags=lanczos";
            a.Append($"-vf \"fps=15,{sc},split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" -loop 0 ");
        }
        else
        {
            if (scale.Length > 0) a.Append($"-vf \"{scale}\" ");
            a.Append(_fmt switch
            {
                "webm" => "-c:v libvpx-vp9 -crf 32 -b:v 0 -row-mt 1 -c:a libopus -b:a 128k ",
                "avi" => "-c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p -c:a libmp3lame -b:a 192k ",
                _ => "-c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p -c:a aac -b:a 192k "
            });
            if (_fmt is "mp4" or "mov") a.Append("-movflags +faststart ");
        }
        a.Append($"\"{outFile}\"");
        return a.ToString();
    }

    private async Task StartAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        if (!Video.Ffmpeg.Ready) { _status.Text = Lang.T("未下載 ffmpeg：撳上面「下載 ffmpeg」先。"); return; }
        var todo = _jobs.Where(j => !j.Status.StartsWith("✓")).ToList();
        if (todo.Count == 0) { _status.Text = Lang.T("未有要轉嘅檔案"); return; }
        var f = Formats.First(x => x.Ext == _fmt);
        _cts = new CancellationTokenSource();
        _start.Text = Lang.T("■ 取消");
        _start.Kind = BtnKind.Danger;
        _start.Invalidate();
        int ok = 0;
        string? lastOut = null;
        foreach (var j in todo)
        {
            if (_cts.IsCancellationRequested) break;
            bool inImage = ImageExts.Contains(System.IO.Path.GetExtension(j.Path).ToLowerInvariant());
            if (inImage && !f.Image && _fmt != "gif") { SetStatus(j, Lang.T("圖片只可以轉做圖片格式"), Ui.Danger); continue; }
            string dir = _outDir.Length > 0 ? _outDir : System.IO.Path.GetDirectoryName(j.Path) ?? "";
            string outFile = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(j.Path) + "." + _fmt);
            if (string.Equals(outFile, j.Path, StringComparison.OrdinalIgnoreCase))
                outFile = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(j.Path) + Lang.T(" (轉檔)") + "." + _fmt);
            outFile = TUi.Unique(outFile);
            SetStatus(j, Lang.T("轉緊…"), Ui.Accent);
            try
            {
                await RunFfmpeg(BuildArgs(j, outFile), p => BeginInvoke(() => SetStatus(j, Lang.T("轉緊… ") + p.ToString("P0"), Ui.Accent)), _cts.Token);
                SetStatus(j, "✓ " + System.IO.Path.GetFileName(outFile), Ui.Success);
                ok++;
                lastOut = outFile;
            }
            catch (OperationCanceledException)
            {
                SetStatus(j, Lang.T("已取消"));
                try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
                break;
            }
            catch (Exception ex)
            {
                SetStatus(j, Lang.T("失敗：") + ex.Message, Ui.Danger);
                AppPaths.LogError("轉檔", ex);
            }
        }
        _cts = null;
        _start.Text = Lang.T("▶ 開始轉檔");
        _start.Kind = BtnKind.Success;
        _start.Invalidate();
        _status.Text = Lang.F("完成 {0} / {1} 個", ok, todo.Count);
        if (lastOut != null && ok > 0) TUi.Reveal(lastOut);
    }

    private static readonly Regex DurRx = new(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)"), TimeRx = new(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)");

    private static double Secs(Match m) =>
        int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 +
        double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);

    private async Task RunFfmpeg(string args, Action<double> progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Video.Ffmpeg.Exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true
        };
        using var p = Process.Start(psi)!;
        double total = 0;
        var log = new StringBuilder();
        double from = 0, to = 0;
        Invoke(() => { from = (double)_from.Value; to = (double)_to.Value; });
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (log) { log.AppendLine(e.Data); if (log.Length > 8000) log.Remove(0, 4000); }
            var d = DurRx.Match(e.Data);
            if (d.Success && total == 0)
            {
                total = Secs(d) - from;
                if (to > from) total = Math.Min(total, to - from);
            }
            var t = TimeRx.Match(e.Data);
            if (t.Success && total > 0) progress(Math.Clamp(Secs(t) / total, 0, 0.99));
        };
        p.OutputDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            await p.WaitForExitAsync();
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            string tail;
            lock (log) tail = string.Join(" ", log.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(2));
            throw new InvalidOperationException(tail);
        }
    }
}
