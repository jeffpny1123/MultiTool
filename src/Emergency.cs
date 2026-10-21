using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MultiTool.Common;
using MultiTool.UI;

namespace MultiTool.Scripts;

/// <summary>
/// 緊急停止（唔使設定，腳本行緊就自動生效）：
///  * 連撳 3 下 Esc（1.5 秒內）
///  * 將滑鼠推去螢幕左上角
/// 用低階 hook 分得出係「你自己」定係腳本模擬嘅輸入，腳本自己撳 Esc / 郁滑鼠唔會觸發。
/// </summary>
internal static class EmergencyStop
{
    private delegate nint HookProc(int code, nint w, nint l);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint h);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint h, int code, nint w, nint l);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)] private struct KBD { public uint vk, scan, flags, time; public nint extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLL { public Point pt; public uint data, flags, time; public nint extra; }

    private static readonly HookProc KbProc = Kb, MsProc = Ms;
    private static nint _kb, _ms;
    private static bool _inited;
    private static readonly long[] EscTimes = new long[3];
    private static int _escN;

    public static void Init()
    {
        if (_inited) return;
        _inited = true;
        ScriptRunner.Changed += Update;
    }

    private static void Update()
    {
        bool want = ScriptRunner.AnyRunning;
        if (want && _kb == 0)
        {
            var mod = GetModuleHandle(null);
            _kb = SetWindowsHookEx(13, KbProc, mod, 0);
            _ms = SetWindowsHookEx(14, MsProc, mod, 0);
            _escN = 0;
        }
        else if (!want && _kb != 0)
        {
            UnhookWindowsHookEx(_kb); _kb = 0;
            if (_ms != 0) { UnhookWindowsHookEx(_ms); _ms = 0; }
        }
    }

    private static void Fire(string reason)
    {
        // 喺 hook 入面唔好做太多嘢：排隊去 UI thread 先停
        var f = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
        Action a = () =>
        {
            if (!ScriptRunner.AnyRunning) return;
            ScriptRunner.StopEverything(reason);
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        };
        if (f != null && f.IsHandleCreated) f.BeginInvoke(a); else a();
    }

    private static nint Kb(int code, nint w, nint l)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBD>(l);
            bool injected = (k.flags & 0x10) != 0;
            if (!injected && k.vk == 0x1B && (int)w is 0x100 or 0x104)
            {
                long now = Environment.TickCount64;
                EscTimes[_escN % 3] = now;
                _escN++;
                if (_escN >= 3 && now - EscTimes[_escN % 3] <= 1500)
                {
                    _escN = 0;
                    Fire(Lang.T("已緊急停止（連撳 3 下 Esc）"));
                }
            }
        }
        return CallNextHookEx(_kb, code, w, l);
    }

    private static nint Ms(int code, nint w, nint l)
    {
        if (code >= 0 && (int)w == 0x200)
        {
            var m = Marshal.PtrToStructure<MSLL>(l);
            bool injected = (m.flags & 0x1) != 0;
            if (!injected)
            {
                var vs = SystemInformation.VirtualScreen;
                bool corner = m.pt.X <= vs.Left + 2 && m.pt.Y <= vs.Top + 2;
                // 你自己將滑鼠推到左上角：即刻停（就算腳本一直郁滑鼠都推得到）
                if (corner) Fire(Lang.T("已緊急停止（滑鼠推去左上角）"));
            }
        }
        return CallNextHookEx(_ms, code, w, l);
    }
}
