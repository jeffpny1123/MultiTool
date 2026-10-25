using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MultiTool;

/// <summary>
/// 腳本同設定備份：將 data 資料夾（唔包下載返嚟嘅工具、AI 模型、遊戲伺服器同大檔）打包做 zip；
/// 還原時解返落 data，再重新啟動。
/// </summary>
internal static class DataBackup
{
    private const string Marker = "multitool-backup.txt";

    // 唔備份：可以重新下載嘅工具 / 遊戲伺服器檔案
    private static readonly string[] SkipDirs = { "tools", "servers" };
    private static readonly string[] SkipExt = { ".gguf", ".bin", ".exe", ".dll", ".zip", ".7z", ".mp4", ".mkv", ".avi", ".mov", ".tmp", ".lock" };
    private const long MaxFile = 50L * 1024 * 1024;

    private static IEnumerable<string> Files()
    {
        var root = AppPaths.Data;
        if (!Directory.Exists(root)) yield break;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, f);
            var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (SkipDirs.Any(d => string.Equals(d, first, StringComparison.OrdinalIgnoreCase)) && rel.Contains(Path.DirectorySeparatorChar)) continue;
            if (SkipExt.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            if (string.Equals(rel, "error.log", StringComparison.OrdinalIgnoreCase)) continue;
            long len;
            try { len = new FileInfo(f).Length; } catch { continue; }
            if (len > MaxFile) continue;
            yield return f;
        }
    }

    /// <summary>匯出：揀位置 → 打包。</summary>
    public static async Task ExportAsync(Form owner)
    {
        using var dlg = new SaveFileDialog
        {
            Filter = Lang.T("備份檔") + " (*.zip)|*.zip",
            FileName = $"MultiTool-backup-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        string target = dlg.FileName;
        try
        {
            int count = await Task.Run(() =>
            {
                var tmp = target + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
                int n = 0;
                using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                {
                    var m = zip.CreateEntry(Marker);
                    using (var w = new StreamWriter(m.Open()))
                        w.Write($"MultiTool {Program.Version}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                    foreach (var f in Files())
                    {
                        var rel = Path.GetRelativePath(AppPaths.Data, f).Replace('\\', '/');
                        try
                        {
                            // 用分享模式讀（設定檔有機會開住）
                            using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            var e = zip.CreateEntry(rel, CompressionLevel.Optimal);
                            e.LastWriteTime = File.GetLastWriteTime(f);
                            using var dst = e.Open();
                            src.CopyTo(dst);
                            n++;
                        }
                        catch (Exception ex) { AppPaths.LogError("備份 " + rel, ex); }
                    }
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                return n;
            });
            if (MessageBox.Show(owner, Lang.F("備份好咗（{0} 個檔案）：\n{1}\n\n要唔要打開所在資料夾？", count, target), Program.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + target + "\"") { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            AppPaths.LogError("備份", ex);
            MessageBox.Show(owner, Lang.T("備份失敗：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>還原：揀備份 zip → 解返落 data（同名檔案會被覆蓋）→ 重新啟動。</summary>
    public static async Task ImportAsync(Form owner)
    {
        using var dlg = new OpenFileDialog
        {
            Filter = Lang.T("備份檔") + " (*.zip)|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        string file = dlg.FileName;
        try
        {
            using (var check = ZipFile.OpenRead(file))
                if (check.GetEntry(Marker) == null)
                {
                    MessageBox.Show(owner, Lang.T("呢個唔係 MultiTool 嘅備份檔。"), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, Lang.T("讀唔到備份檔：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(owner, Lang.T("還原會用備份入面嘅腳本同設定覆蓋而家同名嘅檔案（備份冇嘅檔案會保留），之後 MultiTool 會重新啟動，運行中嘅腳本會停止。\n\n繼續？"),
                Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            Scripts.ScriptRunner.StopEverything(Lang.T("還原備份"));
            var root = Path.GetFullPath(AppPaths.Data);
            await Task.Run(() =>
            {
                Directory.CreateDirectory(root);
                using var zip = ZipFile.OpenRead(file);
                foreach (var e in zip.Entries)
                {
                    if (e.FullName == Marker || e.FullName.EndsWith("/")) continue;
                    var dest = Path.GetFullPath(Path.Combine(root, e.FullName));
                    // 防止 zip 入面有 ..\ 寫出 data 以外
                    if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    try { e.ExtractToFile(dest, true); }
                    catch (Exception ex) { AppPaths.LogError("還原 " + e.FullName, ex); }
                }
            });
            MessageBox.Show(owner, Lang.T("還原好咗，MultiTool 而家重新啟動。"), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
            Application.Exit();
        }
        catch (Exception ex)
        {
            AppPaths.LogError("還原備份", ex);
            MessageBox.Show(owner, Lang.T("還原失敗：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
