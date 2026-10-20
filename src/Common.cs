using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Common;

// 共用工具：滑鼠 / 鍵盤輸入、截圖配對、快捷鍵、按鍵清單

/// <summary>可以揀 / 設定嘅按鍵清單。</summary>
public static class KeyList
{
    public sealed record Item(string Name, int Vk)
    {
        public override string ToString() => Name;
    }

    public static List<Item> Build()
    {
        var l = new List<Item> { new(Lang.T("（無）"), 0) };
        for (char c = 'A'; c <= 'Z'; c++) l.Add(new(c.ToString(), c));
        for (char c = '0'; c <= '9'; c++) l.Add(new(c.ToString(), c));
        for (int f = 1; f <= 12; f++) l.Add(new("F" + f, 0x6F + f));
        l.Add(new("Space", 0x20)); l.Add(new("Enter", 0x0D)); l.Add(new("Tab", 0x09)); l.Add(new("Esc", 0x1B));
        l.Add(new("Backspace", 0x08));
        l.Add(new("↑", 0x26)); l.Add(new("↓", 0x28)); l.Add(new("←", 0x25)); l.Add(new("→", 0x27));
        l.Add(new("Shift", 0x10)); l.Add(new("Ctrl", 0x11)); l.Add(new("Alt", 0x12));
        l.Add(new("Insert", 0x2D)); l.Add(new("Delete", 0x2E)); l.Add(new("Home", 0x24)); l.Add(new("End", 0x23));
        l.Add(new("PageUp", 0x21)); l.Add(new("PageDown", 0x22));
        for (int n = 0; n <= 9; n++) l.Add(new("Num " + n, 0x60 + n));
        l.Add(new("`", 0xC0)); l.Add(new("-", 0xBD)); l.Add(new("=", 0xBB)); l.Add(new("[", 0xDB)); l.Add(new("]", 0xDD));
        l.Add(new(";", 0xBA)); l.Add(new("'", 0xDE)); l.Add(new(",", 0xBC)); l.Add(new(".", 0xBE)); l.Add(new("/", 0xBF));
        return l;
    }
}

/// <summary>真滑鼠操作（前景）。</summary>
internal static class Mouse
{
    private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010;

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, nint extra);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    /// <summary>移去 (x, y) 撳一下；restore = 撳完將滑鼠放返原位。</summary>
    public static void Click(int x, int y, bool right, bool restore)
    {
        GetCursorPos(out var old);
        SetCursorPos(x, y);
        Thread.Sleep(30);
        mouse_event(right ? RightDown : LeftDown, 0, 0, 0, 0);
        Thread.Sleep(40);
        mouse_event(right ? RightUp : LeftUp, 0, 0, 0, 0);
        if (restore)
        {
            Thread.Sleep(30);
            SetCursorPos(old.X, old.Y);
        }
    }

    public static void Move(int x, int y) => SetCursorPos(x, y);

    public static void Down(int x, int y, bool right)
    {
        SetCursorPos(x, y);
        Thread.Sleep(20);
        mouse_event(right ? RightDown : LeftDown, 0, 0, 0, 0);
    }

    public static void Up(int x, int y, bool right, bool move = true)
    {
        if (move)
        {
            SetCursorPos(x, y);
            Thread.Sleep(20);
        }
        mouse_event(right ? RightUp : LeftUp, 0, 0, 0, 0);
    }

    public static void LeftClick(int x, int y)
    {
        GetCursorPos(out var old);
        SetCursorPos(x, y);
        Thread.Sleep(30);
        mouse_event(LeftDown, 0, 0, 0, 0);
        Thread.Sleep(40);
        mouse_event(LeftUp, 0, 0, 0, 0);
        Thread.Sleep(30);
        SetCursorPos(old.X, old.Y);
    }
}

/// <summary>灰階影像（0–255 float）。</summary>
internal sealed class Gray
{
    public readonly int W, H;
    public readonly float[] P;

    public Gray(int w, int h) { W = w; H = h; P = new float[w * h]; }

    public static Gray From(Bitmap src)
    {
        var g = new Gray(src.Width, src.Height);
        var data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var row = new byte[stride];
            for (int y = 0; y < g.H; y++)
            {
                Marshal.Copy(data.Scan0 + y * stride, row, 0, stride);
                int o = y * g.W;
                for (int x = 0; x < g.W; x++)
                {
                    int i = x * 4;
                    float a = row[i + 3] / 255f;
                    // 透明位當黑色處理
                    g.P[o + x] = (row[i + 2] * 0.299f + row[i + 1] * 0.587f + row[i] * 0.114f) * a;
                }
            }
        }
        finally { src.UnlockBits(data); }
        return g;
    }

    public Gray Down(int s)
    {
        if (s <= 1) return this;
        int w = W / s, h = H / s;
        var d = new Gray(Math.Max(1, w), Math.Max(1, h));
        float inv = 1f / (s * s);
        for (int y = 0; y < d.H; y++)
        for (int x = 0; x < d.W; x++)
        {
            float sum = 0;
            for (int yy = 0; yy < s; yy++)
            {
                int o = (y * s + yy) * W + x * s;
                for (int xx = 0; xx < s; xx++) sum += P[o + xx];
            }
            d.P[y * d.W + x] = sum * inv;
        }
        return d;
    }
}

internal static class Matcher
{
    public struct Result { public double Score; public int X, Y, W, H; }

    public static Result Find(Gray screen, Gray tpl, Rectangle? exclude, CancellationToken ct)
    {
        var none = new Result { Score = 0, W = tpl.W, H = tpl.H };
        if (tpl.W > screen.W || tpl.H > screen.H || tpl.W < 2 || tpl.H < 2) return none;

        // 1) 縮細版搵候選
        int s = Math.Clamp(Math.Min(tpl.W, tpl.H) / 12, 1, 4);
        var sc = screen.Down(s);
        var tc = tpl.Down(s);
        if (tc.W > sc.W || tc.H > sc.H) return none;
        var map = NccMap(sc, tc, ct);
        int mw = sc.W - tc.W + 1, mh = sc.H - tc.H + 1;

        bool Excluded(int fx, int fy) =>
            exclude.HasValue && exclude.Value.Contains(fx + tpl.W / 2, fy + tpl.H / 2);

        var cands = new List<(int X, int Y)>();
        var used = (float[])map.Clone();
        for (int k = 0; k < 6; k++)
        {
            int bi = -1; float bv = float.MinValue;
            for (int i = 0; i < used.Length; i++)
            {
                if (used[i] <= bv) continue;
                int x = i % mw, y = i / mw;
                if (Excluded(x * s, y * s)) continue;
                bv = used[i]; bi = i;
            }
            if (bi < 0 || bv <= 0) break;
            int cx = bi % mw, cy = bi / mw;
            cands.Add((cx, cy));
            int rx = Math.Max(1, tc.W / 2), ry = Math.Max(1, tc.H / 2);
            for (int y = Math.Max(0, cy - ry); y <= Math.Min(mh - 1, cy + ry); y++)
            for (int x = Math.Max(0, cx - rx); x <= Math.Min(mw - 1, cx + rx); x++)
                used[y * mw + x] = float.MinValue;
        }

        // 2) 原尺寸微調
        var best = none;
        PrepTemplate(tpl, out var tz, out double tNorm, out double tMean);
        foreach (var (cx, cy) in cands)
        {
            ct.ThrowIfCancellationRequested();
            int r = s;
            for (int y = cy * s - r; y <= cy * s + r; y++)
            for (int x = cx * s - r; x <= cx * s + r; x++)
            {
                if (x < 0 || y < 0 || x + tpl.W > screen.W || y + tpl.H > screen.H) continue;
                if (Excluded(x, y)) continue;
                double v = NccAt(screen, x, y, tpl.W, tpl.H, tz, tNorm, tMean);
                if (v > best.Score) best = new Result { Score = v, X = x, Y = y, W = tpl.W, H = tpl.H };
            }
        }
        return best;
    }

    private static void PrepTemplate(Gray t, out float[] tz, out double norm, out double mean)
    {
        double sum = 0;
        foreach (var v in t.P) sum += v;
        mean = sum / t.P.Length;
        tz = new float[t.P.Length];
        double sq = 0;
        for (int i = 0; i < tz.Length; i++)
        {
            tz[i] = (float)(t.P[i] - mean);
            sq += tz[i] * (double)tz[i];
        }
        norm = Math.Sqrt(sq);
    }

    private static double NccAt(Gray img, int x0, int y0, int w, int h, float[] tz, double tNorm, double tMean)
    {
        double sum = 0, sq = 0, cross = 0;
        int n = w * h;
        for (int y = 0; y < h; y++)
        {
            int o = (y0 + y) * img.W + x0, t = y * w;
            for (int x = 0; x < w; x++)
            {
                float v = img.P[o + x];
                sum += v; sq += v * (double)v; cross += v * (double)tz[t + x];
            }
        }
        double var = sq - sum * sum / n;
        return Score(cross, var, tNorm, sum / n, tMean);
    }

    /// <summary>NCC 轉成 0–1 嘅相似度；兩邊都係單色就比平均亮度。</summary>
    private static double Score(double cross, double var, double tNorm, double iMean, double tMean)
    {
        const double flat = 1e-3;
        double iNorm = Math.Sqrt(Math.Max(0, var));
        if (tNorm < flat || iNorm < flat)
        {
            if (tNorm < flat && iNorm < flat) return 1 - Math.Abs(iMean - tMean) / 255.0;
            return 0;
        }
        return Math.Max(0, cross / (tNorm * iNorm));
    }

    /// <summary>全圖 NCC（用積分圖計窗口和，按行並行）。</summary>
    private static float[] NccMap(Gray img, Gray tpl, CancellationToken ct)
    {
        int W = img.W, H = img.H, w = tpl.W, h = tpl.H, n = w * h;
        int mw = W - w + 1, mh = H - h + 1;
        var ii = new double[(W + 1) * (H + 1)];
        var i2 = new double[(W + 1) * (H + 1)];
        for (int y = 0; y < H; y++)
        {
            double rs = 0, rs2 = 0;
            for (int x = 0; x < W; x++)
            {
                double v = img.P[y * W + x];
                rs += v; rs2 += v * v;
                ii[(y + 1) * (W + 1) + x + 1] = ii[y * (W + 1) + x + 1] + rs;
                i2[(y + 1) * (W + 1) + x + 1] = i2[y * (W + 1) + x + 1] + rs2;
            }
        }
        PrepTemplate(tpl, out var tz, out double tNorm, out double tMean);
        var map = new float[mw * mh];
        Parallel.For(0, mh, new ParallelOptions { CancellationToken = ct }, y =>
        {
            for (int x = 0; x < mw; x++)
            {
                int a = y * (W + 1) + x, b = a + w, c = (y + h) * (W + 1) + x, d = c + w;
                double sum = ii[d] - ii[b] - ii[c] + ii[a];
                double sq = i2[d] - i2[b] - i2[c] + i2[a];
                double cross = 0;
                for (int ty = 0; ty < h; ty++)
                {
                    int o = (y + ty) * W + x, t = ty * w;
                    for (int tx = 0; tx < w; tx++) cross += img.P[o + tx] * tz[t + tx];
                }
                map[y * mw + x] = (float)Score(cross, sq - sum * sum / n, tNorm, sum / n, tMean);
            }
        });
        return map;
    }
}

/// <summary>冇邊框、冇背景卡嘅一行（用嚟放卡外嘅掣）。</summary>
public sealed class BareRow : CardPanel
{
    public BareRow() { Padding = new Padding(0); }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var b = new SolidBrush(Ui.Bg);
        e.Graphics.FillRectangle(b, ClientRectangle);
    }
}

/// <summary>用 scan code 送按鍵（遊戲多數要 scan code 先收到）。</summary>
internal static class Keyboard
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    private static bool IsExtended(int vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E;

    private static long _lastInjectTicks;

    /// <summary>啱啱自己送過按鍵（避免自動按鍵觸發返快捷鍵）。</summary>
    public static bool RecentlyInjected => Environment.TickCount64 - Interlocked.Read(ref _lastInjectTicks) < 200;

    public static void Press(int vk, int holdMs = 50)
    {
        Interlocked.Exchange(ref _lastInjectTicks, Environment.TickCount64);
        Send(vk, false);
        Thread.Sleep(Math.Max(10, holdMs));
        Send(vk, true);
        Interlocked.Exchange(ref _lastInjectTicks, Environment.TickCount64);
    }

    /// <summary>淨係按下 / 淨係彈起（腳本用）。</summary>
    public static void Down(int vk)
    {
        Interlocked.Exchange(ref _lastInjectTicks, Environment.TickCount64);
        Send(vk, false);
    }

    public static void Up(int vk)
    {
        Send(vk, true);
        Interlocked.Exchange(ref _lastInjectTicks, Environment.TickCount64);
    }

    private static void Send(int vk, bool up)
    {
        ushort scan = (ushort)MapVirtualKey((uint)vk, 0);
        uint flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki = new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = flags };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}

/// <summary>快捷鍵：最多 2 個鍵（一個 Ctrl / Shift / Alt + 一個鍵，或者單一個鍵）。</summary>
public readonly record struct Hotkey(Keys Modifier, int Vk)
{
    public bool IsEmpty => Vk == 0;

    public string Serialize() => $"{(int)Modifier}|{Vk}";

    public static Hotkey Parse(string text)
    {
        var p = text.Trim().Split('|');
        if (p.Length == 2 && int.TryParse(p[0], out int m) && int.TryParse(p[1], out int vk))
        {
            var mod = (Keys)m;
            if (mod is Keys.None or Keys.Control or Keys.Shift or Keys.Alt) return new Hotkey(mod, vk);
        }
        return default;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public bool IsDown()
    {
        if (IsEmpty || !Down(Vk)) return false;
        bool ctrl = Down(0x11), shift = Down(0x10), alt = Down(0x12);
        // 修飾鍵要啱啱好：要求嘅要撳住，其他唔可以撳住（本身就係修飾鍵嗰個除外）
        bool needCtrl = Modifier == Keys.Control || Vk == 0x11;
        bool needShift = Modifier == Keys.Shift || Vk == 0x10;
        bool needAlt = Modifier == Keys.Alt || Vk == 0x12;
        return ctrl == needCtrl && shift == needShift && alt == needAlt;
    }

    public static string KeyName(int vk)
    {
        var k = KeyList.Build().FirstOrDefault(x => x.Vk == vk && vk != 0);
        if (k != null) return k.Name;
        return (Keys)vk switch
        {
            Keys.LWin or Keys.RWin => "Win",
            Keys.Apps => "Menu",
            _ => ((Keys)vk).ToString()
        };
    }

    public override string ToString()
    {
        if (IsEmpty) return Lang.T("（無）");
        string mod = Modifier switch { Keys.Control => "Ctrl + ", Keys.Shift => "Shift + ", Keys.Alt => "Alt + ", _ => "" };
        return mod + KeyName(Vk);
    }
}

/// <summary>撳一下然後撳組合鍵（例如 Ctrl + F1）就設定；Backspace / Delete 清除。</summary>
internal sealed class HotkeyBox : TextBox
{
    private readonly string? _file;
    private Hotkey _hotkey;
    private readonly ToolTip _tip = new();

    /// <summary>設定咗新快捷鍵。</summary>
    public event Action? Changed;

    /// <param name="file">有就自動存 / 讀呢個檔；null 就由外面用 Hotkey + Changed 處理。</param>
    public HotkeyBox(string? file = null)
    {
        _file = file;
        ReadOnly = true;
        ShortcutsEnabled = false;
        TextAlign = HorizontalAlignment.Center;
        Cursor = Cursors.Hand;
        Load();
        Text = _hotkey.ToString();
        _tip.SetToolTip(this, Lang.T("撳一下，再撳快捷鍵（最多 2 個鍵，例如 Ctrl + F1）。Backspace / Delete 清除。"));
    }

    public Hotkey Hotkey
    {
        get => _hotkey;
        set { _hotkey = value; if (!Focused) Text = value.ToString(); }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Text = Lang.T("請撳快捷鍵…");
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Text = _hotkey.ToString();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        if (msg.Msg != WM_KEYDOWN && msg.Msg != WM_SYSKEYDOWN) return base.ProcessCmdKey(ref msg, keyData);
        var code = keyData & Keys.KeyCode;
        var mods = keyData & Keys.Modifiers;
        if (code == Keys.Tab && mods == Keys.None) return base.ProcessCmdKey(ref msg, keyData);

        if ((code is Keys.Back or Keys.Delete) && mods == Keys.None)
        {
            Set(default);
            return true;
        }
        if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LControlKey or Keys.RControlKey
                or Keys.LShiftKey or Keys.RShiftKey or Keys.LMenu or Keys.RMenu)
        {
            // 淨係撳住修飾鍵：顯示提示，等埋第二個鍵
            Text = (mods == Keys.Control ? "Ctrl" : mods == Keys.Shift ? "Shift" : mods == Keys.Alt ? "Alt" : "…") + " + …";
            return true;
        }
        int modCount = (mods.HasFlag(Keys.Control) ? 1 : 0) + (mods.HasFlag(Keys.Shift) ? 1 : 0) + (mods.HasFlag(Keys.Alt) ? 1 : 0);
        if (modCount > 1)
        {
            Text = Lang.T("最多 2 個鍵");
            return true;
        }
        Set(new Hotkey(mods, (int)code));
        return true;
    }

    protected override void OnKeyPress(KeyPressEventArgs e) => e.Handled = true;

    private void Set(Hotkey hk)
    {
        _hotkey = hk;
        Text = hk.ToString();
        if (_file != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, hk.Serialize());
            }
            catch { }
        }
        Changed?.Invoke();
        // 設定完離開格仔，費事再撳就改咗
        var f = FindForm();
        if (f != null) f.ActiveControl = null;
    }

    private void Load()
    {
        try
        {
            if (_file != null && File.Exists(_file)) _hotkey = Hotkey.Parse(File.ReadAllText(_file));
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
