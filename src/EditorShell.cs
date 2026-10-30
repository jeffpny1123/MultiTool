using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace MultiTool.Video;

/// <summary>
/// 影片剪輯同 Windows 檔案總管整合：
/// 右鍵影片 / 相片 →「開啟檔案」入面有「影片剪輯」，另外右鍵第一層有「使用 MultiTool 編輯」（好似 Clipchamp 咁），
/// 撳落去會開新剪片專案並加入嗰啲檔案。Windows 11 嘅新式右鍵要撳「顯示更多選項」先見到。
/// 只寫 HKCU（唔使管理員）；移除影片剪輯就清走。MultiTool 已經開咗就用 named pipe 交俾嗰個。
/// </summary>
internal static class EdShell
{
    private const string ProgId = "MultiTool.VideoEditor";
    private const string Verb = "MultiTool.Edit";
    private static string VerbKey(string ext) => $@"Software\Classes\SystemFileAssociations\{ext}\shell\{Verb}";
    /// <summary>第二次開 MultiTool：叫已經開咗嗰個彈出嚟。</summary>
    public const string ShowMsg = "\u0001show";

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int pid);
    private static string PipeName => "MultiTool-open-" + Environment.UserName;
    private static IEnumerable<string> Exts => MediaInfo.VideoExt.Concat(MediaInfo.ImageExt).Distinct();

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    /// <summary>跟返工具庫：裝咗就登記，冇裝就清走。</summary>
    public static void Sync()
    {
        try
        {
            if (Extensions.IsInstalled(Extensions.Editor)) Register();
            else Unregister();
        }
        catch (Exception ex) { AppPaths.LogError("影片剪輯右鍵登記", ex); }
    }

    private static void Register()
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0 || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
        string cmd = $"\"{exe}\" --edit \"%1\"";
        string name = Lang.T("影片剪輯") + " (MultiTool)";
        using (var chk = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
        using (var chkName = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open"))
            if (chk?.GetValue("") as string == cmd && chkName?.GetValue("FriendlyAppName") as string == name && Exts.All(HasExt) && Exts.All(e => HasVerb(e, cmd))) return; // 已經登記好
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            k.SetValue("", name);
            k.SetValue("FriendlyTypeName", name);
        }
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon")) k.SetValue("", $"\"{exe}\",0");
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\Application")) k.SetValue("ApplicationName", name);
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open"))
        {
            k.SetValue("", name);
            k.SetValue("FriendlyAppName", name);
        }
        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command")) k.SetValue("", cmd);
        foreach (var ext in Exts)
            using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids"))
                k.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        // 右鍵第一層：「使用 MultiTool 編輯」
        string verbName = Lang.T("使用 MultiTool 編輯");
        foreach (var ext in Exts)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(VerbKey(ext)))
            {
                k.SetValue("MUIVerb", verbName);
                k.SetValue("Icon", $"\"{exe}\",0");
            }
            using (var k = Registry.CurrentUser.CreateSubKey(VerbKey(ext) + @"\command")) k.SetValue("", cmd);
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
    }

    private static bool HasVerb(string ext, string cmd)
    {
        using var k = Registry.CurrentUser.OpenSubKey(VerbKey(ext));
        using var c = Registry.CurrentUser.OpenSubKey(VerbKey(ext) + @"\command");
        return k?.GetValue("MUIVerb") as string == Lang.T("使用 MultiTool 編輯") && c?.GetValue("") as string == cmd;
    }

    private static bool HasExt(string ext)
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids");
        return k?.GetValueNames().Contains(ProgId) == true;
    }

    private static void Unregister()
    {
        bool any = false;
        using (var chk = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}"))
            if (chk != null) { any = true; }
        if (any) Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false);
        foreach (var ext in Exts)
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", true);
            if (k?.GetValueNames().Contains(ProgId) == true) { k.DeleteValue(ProgId, false); any = true; }
            using (var v = Registry.CurrentUser.OpenSubKey(VerbKey(ext)))
                if (v == null) continue;
            Registry.CurrentUser.DeleteSubKeyTree(VerbKey(ext), false);
            any = true;
        }
        if (any) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>命令列：--edit 檔案…（冇就 null）。</summary>
    public static string[]? EditArgs(string[] args)
    {
        int i = Array.IndexOf(args, "--edit");
        if (i < 0) return null;
        var files = args.Skip(i + 1).Where(a => !a.StartsWith("--")).ToArray();
        return files.Length > 0 ? files : null;
    }

    /// <summary>MultiTool 已經開咗：交俾佢開，成功就 true。</summary>
    public static bool TrySend(string[] files)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            c.Connect(1000);
            AllowSetForegroundWindow(-1); // 俾嗰邊可以彈去最前
            var data = Encoding.UTF8.GetBytes(string.Join("\n", files));
            c.Write(data, 0, data.Length);
            c.Flush();
            return true;
        }
        catch { return false; }
    }

    /// <summary>主程式：聽其他 MultiTool 交過嚟嘅檔案。</summary>
    public static void Listen(Action<string[]> onFiles)
    {
        var th = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var s = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte);
                    s.WaitForConnection();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    var files = Encoding.UTF8.GetString(ms.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (files.Length > 0) onFiles(files);
                }
                catch (IOException) { return; } // 已經有另一個 MultiTool 聽緊
                catch (Exception ex) { AppPaths.LogError("影片剪輯開檔", ex); Thread.Sleep(1000); }
            }
        }) { IsBackground = true, Name = "EdShell" };
        th.Start();
    }
}
