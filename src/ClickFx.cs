using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MultiTool.Video;

// =====================================================================
//  錄影：滑鼠點擊效果 — 撳落去嗰點出一個擴散圓圈（左鍵橙、右鍵藍），
//  係真視窗所以會錄入影片；滑鼠穿透、唔搶焦點。
// =====================================================================
internal static class ClickFx
{
    private delegate nint HookProc(int code, nint w, nint l);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint h);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint h, int code, nint w, nint l);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)] private struct MSLL { public Point pt; public uint data, flags, time; public nint extra; }

    private static readonly HookProc Proc = Hook; // 要留住，費事俾 GC 收咗
    private static nint _hook;
    private static int _users;

    private static Control? Ui => Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.IsHandleCreated && !f.IsDisposed);

    private static void OnUi(Action a)
    {
        var c = Ui;
        if (c == null) return;
        try { if (c.InvokeRequired) c.BeginInvoke(a); else a(); } catch { }
    }

    public static void Start() => OnUi(() =>
    {
        if (_users++ > 0) return;
        _hook = SetWindowsHookEx(14 /*WH_MOUSE_LL*/, Proc, GetModuleHandle(null), 0);
    });

    public static void Stop() => OnUi(() =>
    {
        if (_users == 0 || --_users > 0) return;
        if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
    });

    private static nint Hook(int code, nint w, nint l)
    {
        if (code >= 0)
        {
            int msg = (int)w;
            if (msg is 0x0201 or 0x0204 or 0x0207) // 左 / 右 / 中鍵撳落
            {
                var m = Marshal.PtrToStructure<MSLL>(l);
                var c = Ui;
                // hook 入面唔好做重嘢：排隊再開效果
                try { c?.BeginInvoke(() => Ripple.Show(m.pt, msg == 0x0204 ? 1 : msg == 0x0207 ? 2 : 0)); } catch { }
            }
        }
        return CallNextHookEx(_hook, code, w, l);
    }

    /// <summary>一個擴散圓圈（per-pixel alpha 嘅 layered 視窗，邊緣夠順）。</summary>
    private sealed class Ripple : Form
    {
        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint h, nint dstDc, ref Point dst, ref Size size, nint srcDc, ref Point src, int key, ref Blend blend, int flags);
        [DllImport("user32.dll")] private static extern nint GetDC(nint h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(nint h, nint dc);
        [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
        [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Op, Flags, Alpha, Format; }

        private const int Ms = 450;
        private static readonly int Size_ = (int)(72 * Math.Max(1f, MultiTool.UI.Ui.Scale));
        private readonly Point _at;
        private readonly Color _hue;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly Timer _t = new() { Interval = 15 };

        public static void Show(Point at, int button)
        {
            var hue = button switch { 1 => Color.FromArgb(70, 150, 255), 2 => Color.FromArgb(80, 210, 130), _ => Color.FromArgb(255, 170, 40) };
            new Ripple(at, hue).Show();
        }

        private Ripple(Point at, Color hue)
        {
            _at = at; _hue = hue;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Bounds = new Rectangle(at.X - Size_ / 2, at.Y - Size_ / 2, Size_, Size_);
            _t.Tick += (_, _) => Frame();
            Shown += (_, _) => { Frame(); _t.Start(); };
            FormClosed += (_, _) => _t.Dispose();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8; // layered、穿透、工具視窗、唔搶焦點、置頂
                return cp;
            }
        }

        private void Frame()
        {
            double t = Math.Min(1, _sw.ElapsedMilliseconds / (double)Ms);
            if (t >= 1) { Close(); return; }
            using var bmp = new Bitmap(Size_, Size_, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float c = Size_ / 2f, max = Size_ / 2f - 3;
                double ease = 1 - Math.Pow(1 - t, 3);
                // 中間實心點：慢慢淡出
                float dot = (float)(max * 0.32);
                using (var b = new SolidBrush(Color.FromArgb((int)(150 * (1 - t)), _hue))) g.FillEllipse(b, c - dot, c - dot, dot * 2, dot * 2);
                // 擴散圓圈
                float r = (float)(max * (0.3 + 0.7 * ease));
                using (var p = new Pen(Color.FromArgb((int)(230 * (1 - t)), _hue), Math.Max(2f, 3.5f * (float)(1 - t * 0.5))))
                    g.DrawEllipse(p, c - r, c - r, r * 2, r * 2);
            }
            Push(bmp);
        }

        private void Push(Bitmap bmp)
        {
            nint screen = GetDC(0), mem = CreateCompatibleDC(screen), hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
            try
            {
                var size = new Size(bmp.Width, bmp.Height);
                var src = Point.Empty;
                var dst = new Point(_at.X - Size_ / 2, _at.Y - Size_ / 2);
                var blend = new Blend { Op = 0, Flags = 0, Alpha = 255, Format = 1 /*AC_SRC_ALPHA*/ };
                UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
            }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(hbmp);
                DeleteDC(mem);
                ReleaseDC(0, screen);
            }
        }
    }
}
