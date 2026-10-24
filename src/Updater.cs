using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool;

/// <summary>
/// 自動更新：問 GitHub 最新 Release，新過自己就下載 MultiTool.exe（或者 zip 入面嘅 MultiTool.exe），
/// 閂咗自己之後換走舊 exe 再重開。data 資料夾唔會郁。
/// </summary>
internal static class Updater
{
    public const string Repo = "jeffpny1123/MultiTool";
    public static string RepoUrl => "https://github.com/" + Repo;

    private static bool _busy;

    private static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MultiTool-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>"v1.2.3" → (1,2,3)；讀唔到就 0。</summary>
    private static Version ParseVersion(string s)
    {
        var digits = new string(s.Trim().TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        var parts = digits.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
        while (parts.Count < 3) parts.Add(0);
        return new Version(parts[0], parts[1], parts[2]);
    }

    private sealed record Release(string Tag, string Notes, string Url, string AssetName);

    private static async Task<Release?> GetLatestAsync()
    {
        using var http = NewClient();
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // 未有任何 Release
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        if (!root.TryGetProperty("assets", out var assets)) return null;
        string? url = null, name = null;
        // 優先：MultiTool.exe；其次：任何 .exe；再其次：.zip
        foreach (var pick in new Func<string, bool>[]
                 {
                     n => n.Equals("MultiTool.exe", StringComparison.OrdinalIgnoreCase),
                     n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                     n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                 })
        {
            foreach (var a in assets.EnumerateArray())
            {
                var n = a.GetProperty("name").GetString() ?? "";
                if (!pick(n)) continue;
                url = a.GetProperty("browser_download_url").GetString();
                name = n;
                break;
            }
            if (url != null) break;
        }
        return url == null ? null : new Release(tag, notes, url, name!);
    }

    /// <summary>檢查更新。silent = 開程式時自動檢查：冇新版 / 出錯都唔出聲。</summary>
    public static async Task CheckAsync(Form owner, bool silent)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            Release? rel;
            try { rel = await GetLatestAsync(); }
            catch (Exception ex)
            {
                if (!silent)
                    MessageBox.Show(owner, Lang.T("連唔到 GitHub 檢查更新：\n") + ex.Message, Program.AppName,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (rel == null || ParseVersion(rel.Tag) <= ParseVersion(Program.Version))
            {
                if (!silent)
                    MessageBox.Show(owner, Lang.F("已經係最新版本（{0}）。", Program.Version), Program.AppName,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 自動更新：有新版就直接下載、換檔、重開，唔使問
            if (!await DownloadAndRestartAsync(owner, rel))
            {
                if (silent) MultiTool.Tools.Toast.Show(Lang.T("自動更新失敗，下次開程式會再試"), MultiTool.UI.Ui.Danger, 4000);
                else MessageBox.Show(owner, Lang.T("下載更新失敗，詳情已寫入 data\\error.log"), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally { _busy = false; }
    }

    /// <summary>下載新版、換 exe、重開。成功（程式即將關閉）回傳 true。</summary>
    private static async Task<bool> DownloadAndRestartAsync(Form owner, Release rel)
    {
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MultiTool.exe");
        string dir = Path.GetDirectoryName(exe)!;
        string newExe = Path.Combine(dir, "MultiTool.update.exe");
        string tmp = Path.Combine(Path.GetTempPath(), "MultiTool_" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(rel.AssetName));

        using var progress = new ProgressForm(Lang.F("下載緊 {0}…", rel.Tag));
        progress.Show(owner);
        try
        {
            using (var http = NewClient())
            using (var resp = await http.GetAsync(rel.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    progress.SetProgress(done, total);
                }
            }

            // zip 就攞入面嘅 MultiTool.exe
            if (tmp.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(tmp);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("MultiTool.exe", StringComparison.OrdinalIgnoreCase))
                            ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new InvalidOperationException(Lang.T("zip 入面搵唔到 exe"));
                entry.ExtractToFile(newExe, true);
            }
            else File.Copy(tmp, newExe, true);
        }
        catch (Exception ex)
        {
            progress.Close();
            AppPaths.LogError("下載更新", ex);
            try { File.Delete(newExe); } catch { }
            return false;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
        progress.Close();

        // 用 PowerShell 等呢個程式閂咗，再換 exe、重開（EncodedCommand 費事中文路徑 / 引號出事）
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        string ps =
            $"$p={Environment.ProcessId}; try {{ Wait-Process -Id $p -Timeout 30 -ErrorAction SilentlyContinue }} catch {{}}; " +
            $"Start-Sleep -Milliseconds 300; " +
            $"for($i=0;$i -lt 20;$i++){{ try {{ Move-Item -LiteralPath {Q(newExe)} -Destination {Q(exe)} -Force -ErrorAction Stop; break }} catch {{ Start-Sleep -Milliseconds 500 }} }}; " +
            $"Start-Process -FilePath {Q(exe)} -WorkingDirectory {Q(dir)}";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            AppPaths.LogError("啟動更新", ex);
            MessageBox.Show(owner, Lang.F("自動換檔失敗，請手動將\n{0}\n改名做 MultiTool.exe。", newExe), Program.AppName);
            Application.Exit();
            return true;
        }
        Application.Exit();
        return true;
    }

    /// <summary>下載進度細視窗。</summary>
    private sealed class ProgressForm : Form
    {
        private readonly Label _label;
        private readonly ProgressBar _bar;

        public ProgressForm(string title)
        {
            Text = Program.AppName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            ControlBox = false;
            ShowInTaskbar = false;
            BackColor = Ui.Card;
            ForeColor = Ui.Text;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(380), Ui.S(96));
            _label = new Label { Text = title, Font = Ui.F(10f), AutoSize = false, ForeColor = Ui.Text,
                Bounds = new Rectangle(Ui.S(16), Ui.S(14), Ui.S(348), Ui.S(24)) };
            _bar = new ProgressBar { Bounds = new Rectangle(Ui.S(16), Ui.S(50), Ui.S(348), Ui.S(18)), Style = ProgressBarStyle.Marquee };
            Controls.Add(_label);
            Controls.Add(_bar);
        }

        public void SetProgress(long done, long total)
        {
            if (IsDisposed) return;
            void Do()
            {
                if (total > 0)
                {
                    _bar.Style = ProgressBarStyle.Continuous;
                    _bar.Value = (int)Math.Clamp(done * 100 / total, 0, 100);
                    _label.Text = Lang.F("下載緊… {0:0.0} / {1:0.0} MB", done / 1048576.0, total / 1048576.0);
                }
                else _label.Text = Lang.F("下載緊… {0:0.0} MB", done / 1048576.0);
            }
            if (InvokeRequired) BeginInvoke(Do); else Do();
        }
    }
}
