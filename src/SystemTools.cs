using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using MultiTool.UI;

namespace MultiTool.Tools;

/// <summary>「磁碟及軟件管理」：四個分頁（磁碟空間、重複檔案、開機程式、已安裝軟件）。</summary>
internal sealed class DiskHub : Panel
{
    private readonly Control?[] _pages = new Control?[4];
    private readonly Func<Control>[] _builders = { DiskPage.Build, DupPage.Build, StartupPage.Build, AppsPage.Build };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private static int _last;

    public static Control Build() => new DiskHub();

    private DiskHub()
    {
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        var tabs = new TabStrip(Lang.T("磁碟空間"), Lang.T("重複檔案"), Lang.T("開機程式"), Lang.T("已安裝軟件")) { Dock = DockStyle.Fill };
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
//  重複檔案
// =====================================================================

/// <summary>搵出內容一模一樣嘅檔案：先比大細，再比頭 64 KB，最後比成個檔嘅 SHA-256。只會掉入資源回收筒，唔會永久刪除。</summary>
internal sealed class DupPage : ToolPage
{
    private readonly RowList _list = new();
    private readonly Label _status, _summary;
    private readonly FancyButton _scan, _recycle;
    private readonly ComboBox _min;
    private readonly List<string> _folders = new();
    private readonly Label _folderLabel;
    private CancellationTokenSource? _cts;
    private static readonly long[] MinSizes = { 1, 100 * 1024, 1024 * 1024, 10 * 1024 * 1024, 100 * 1024 * 1024 };

    public static Control Build() => new DupPage();

    private DupPage()
    {
        _folderLabel = TUi.L(Lang.T("未揀資料夾。"));
        _min = TIn.Combo(150, new[] { Lang.T("所有大細"), "≥ 100 KB", "≥ 1 MB", "≥ 10 MB", "≥ 100 MB" }, 2);
        _scan = TUi.Btn(Lang.T("開始搵"), () => _ = ScanAsync(), 120, BtnKind.Primary);
        _status = TUi.L("");
        Card(Lang.T("重複檔案"), Lang.T("搵出內容一模一樣嘅檔案（唔理檔名）。可以揀幾個資料夾一齊比。系統資料夾同冇權限嘅地方會略過。"),
            TUi.Col(
                TUi.Row(TUi.Btn(Lang.T("加入資料夾…"), AddFolder), TUi.Btn(Lang.T("清除"), () => { _folders.Clear(); ShowFolders(); }), _folderLabel),
                TUi.Row(TUi.L(Lang.T("只搵")), _min, _scan, _status)));

        _list.Columns = new[] { (Lang.T("檔案"), 0, false), (Lang.T("修改日期"), 130, false), (Lang.T("大細"), 90, true) };
        _list.Checks = true;
        _list.ActionText = Lang.T("開啟位置");
        _list.EmptyText = Lang.T("揀資料夾再撳「開始搵」。");
        _list.Width = Ui.S(820); _list.Height = Ui.S(420);
        _list.RowAction += r => { if (r.Tag is string p) OpenLocation(p); };
        _list.CheckChanged += UpdateSummary;
        _summary = TUi.L("");
        _recycle = TUi.Btn(Lang.T("將剔咗嘅掉入資源回收筒"), Recycle, 0, BtnKind.Danger);
        Card(Lang.T("結果"), Lang.T("每組係一模一樣嘅檔案。剔咗嘅會掉入資源回收筒（可以還原）；每組最少會留返一個。"),
            TUi.Col(
                TUi.Row(TUi.Btn(Lang.T("每組留最舊嗰個"), () => AutoPick(true)), TUi.Btn(Lang.T("每組留最新嗰個"), () => AutoPick(false)), TUi.Btn(Lang.T("全部唔剔"), () => { foreach (var r in _list.Rows) r.Checked = false; _list.Invalidate(); UpdateSummary(); })),
                _list,
                TUi.Row(_recycle, _summary)));
        Resize += (_, _) => _list.Width = Math.Max(Ui.S(480), Width - Ui.S(110));
        Disposed += (_, _) => _cts?.Cancel();
        UpdateSummary();
    }

    public static void OpenLocation(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); } catch { }
    }

    private void AddFolder()
    {
        using var d = new FolderBrowserDialog();
        if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (!_folders.Contains(d.SelectedPath, StringComparer.OrdinalIgnoreCase)) _folders.Add(d.SelectedPath);
        ShowFolders();
    }

    private void ShowFolders() => _folderLabel.Text = _folders.Count == 0 ? Lang.T("未揀資料夾。") : string.Join("；", _folders);

    /// <summary>行晒資料夾入面所有檔案（冇權限、連結點就略過）。</summary>
    private static IEnumerable<FileInfo> Walk(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            string dir = stack.Pop();
            FileInfo[] files; DirectoryInfo[] subs;
            try
            {
                var di = new DirectoryInfo(dir);
                files = di.GetFiles();
                subs = di.GetDirectories();
            }
            catch { continue; }
            foreach (var f in files) yield return f;
            foreach (var s in subs)
            {
                if ((s.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // 連結點：費事行重複或者兜圈
                string n = s.Name;
                if (n.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) || n.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                if (s.FullName.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(s.FullName);
            }
        }
    }

    private static string Hash(string path, long limit, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var buf = new byte[1 << 16];
        long left = limit;
        int n;
        while (left > 0 && (n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buf, 0, n, null, 0);
            left -= n;
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private async Task ScanAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        if (_folders.Count == 0) { _status.ForeColor = Ui.Danger; _status.Text = Lang.T("請先加入資料夾。"); return; }
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        _scan.Text = Lang.T("停止"); _scan.Kind = BtnKind.Danger; _scan.Invalidate();
        _status.ForeColor = Ui.TextDim;
        long min = MinSizes[Math.Max(0, _min.SelectedIndex)];
        var folders = _folders.ToList();
        void Say(string s) { try { BeginInvoke(() => { if (!IsDisposed) _status.Text = s; }); } catch { } }
        List<List<FileInfo>> groups = new();
        try
        {
            groups = await Task.Run(() =>
            {
                // ① 列出檔案，同大細嘅先有機會重複
                var bySize = new Dictionary<long, List<FileInfo>>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int count = 0;
                foreach (var root in folders)
                    foreach (var f in Walk(root, ct))
                    {
                        if (f.Length < min || !seen.Add(f.FullName)) continue;
                        if (!bySize.TryGetValue(f.Length, out var l)) bySize[f.Length] = l = new();
                        l.Add(f);
                        if (++count % 2000 == 0) Say(Lang.F("搵緊檔案… {0}", count));
                    }
                var cands = bySize.Values.Where(l => l.Count > 1).ToList();
                int total = cands.Sum(l => l.Count), done = 0;
                var result = new List<List<FileInfo>>();
                foreach (var same in cands)
                {
                    ct.ThrowIfCancellationRequested();
                    // ② 頭 64 KB 一樣先再比成個檔
                    IEnumerable<List<FileInfo>> Split(IEnumerable<FileInfo> fs, long limit)
                    {
                        var d = new Dictionary<string, List<FileInfo>>();
                        foreach (var f in fs)
                        {
                            try
                            {
                                string h = Hash(f.FullName, limit, ct);
                                if (!d.TryGetValue(h, out var l)) d[h] = l = new();
                                l.Add(f);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch { }   // 讀唔到（用緊 / 冇權限）：略過
                        }
                        return d.Values.Where(l => l.Count > 1);
                    }
                    foreach (var head in Split(same, 1 << 16))
                    {
                        if (same[0].Length <= 1 << 16) { result.Add(head); continue; }
                        foreach (var full in Split(head, long.MaxValue)) result.Add(full);
                    }
                    done += same.Count;
                    Say(Lang.F("比較緊內容… {0} / {1}", done, total));
                }
                return result.OrderByDescending(g => g[0].Length * (g.Count - 1)).ToList();
            }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppPaths.LogError("重複檔案", ex); if (!IsDisposed) { _status.ForeColor = Ui.Danger; _status.Text = ex.Message; } }
        bool cancelled = ct.IsCancellationRequested;
        if (_cts == cts) _cts = null;
        if (IsDisposed) return;
        _scan.Text = Lang.T("開始搵"); _scan.Kind = BtnKind.Primary; _scan.Invalidate();
        if (cancelled) { _status.Text = Lang.T("已停止。"); return; }
        _list.Rows.Clear();
        long waste = 0;
        int shown = 0;
        foreach (var g in groups)
        {
            waste += g[0].Length * (g.Count - 1);
            if (++shown > 500) continue;   // 最多顯示 500 組（最嘥位嘅排先）
            _list.Rows.Add(new RowList.Row { Header = true, Cells = new[] { Lang.F("{0} 個一樣嘅檔案，每個 {1}", g.Count, TIn.Size(g[0].Length)) } });
            foreach (var f in g.OrderBy(f => f.LastWriteTime))
                _list.Rows.Add(new RowList.Row { Cells = new[] { f.FullName, f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), TIn.Size(f.Length) }, Tag = f.FullName });
        }
        _list.EmptyText = Lang.T("搵唔到重複嘅檔案。");
        _list.Reload();
        _status.ForeColor = groups.Count > 0 ? Ui.Success : Ui.TextDim;
        _status.Text = groups.Count == 0 ? Lang.T("搵唔到重複嘅檔案。") : Lang.F("搵到 {0} 組重複，刪走多出嚟嘅可以慳返 {1}", groups.Count, TIn.Size(waste)) + (groups.Count > 500 ? "　" + Lang.T("（只顯示最嘥位嘅 500 組）") : "");
        UpdateSummary();
    }

    private IEnumerable<List<RowList.Row>> Groups()
    {
        List<RowList.Row>? cur = null;
        foreach (var r in _list.Rows)
        {
            if (r.Header) { if (cur is { Count: > 0 }) yield return cur; cur = new(); }
            else cur?.Add(r);
        }
        if (cur is { Count: > 0 }) yield return cur;
    }

    private void AutoPick(bool keepOldest)
    {
        foreach (var g in Groups())
        {
            // 每組已經按修改日期由舊到新排
            for (int i = 0; i < g.Count; i++) g[i].Checked = keepOldest ? i != 0 : i != g.Count - 1;
        }
        _list.Invalidate();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        long bytes = 0; int n = 0;
        foreach (var r in _list.Rows.Where(r => r.Checked && r.Tag is string))
            try { bytes += new FileInfo((string)r.Tag!).Length; n++; } catch { }
        _summary.Text = n == 0 ? "" : Lang.F("剔咗 {0} 個檔案（{1}）", n, TIn.Size(bytes));
        _recycle.Enabled = n > 0;
        _recycle.Invalidate();
    }

    private void Recycle()
    {
        // 每組最少留一個：成組都剔晒就唔剔第一個
        foreach (var g in Groups()) if (g.All(r => r.Checked)) g[0].Checked = false;
        var picks = _list.Rows.Where(r => r.Checked && r.Tag is string).ToList();
        _list.Invalidate();
        if (picks.Count == 0) { UpdateSummary(); return; }
        if (MessageBox.Show(FindForm(), Lang.F("將 {0} 個檔案掉入資源回收筒？\n（之後可以喺資源回收筒還原。每組會留返最少一個。）", picks.Count), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { UpdateSummary(); return; }
        int ok = 0, fail = 0;
        foreach (var r in picks)
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile((string)r.Tag!, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                _list.Rows.Remove(r);
                ok++;
            }
            catch (Exception ex) { fail++; AppPaths.LogError("重複檔案", ex); }
        // 得返一個檔嘅組唔再係重複：成組拎走
        var keep = new List<RowList.Row>();
        var rows = _list.Rows.ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            if (!rows[i].Header) continue;
            int j = i + 1;
            while (j < rows.Count && !rows[j].Header) j++;
            if (j - i - 1 >= 2) keep.AddRange(rows.GetRange(i, j - i));
        }
        _list.Rows.Clear(); _list.Rows.AddRange(keep);
        _list.Reload();
        _status.ForeColor = fail > 0 ? Ui.Danger : Ui.Success;
        _status.Text = Lang.F("已將 {0} 個檔案掉入資源回收筒。", ok) + (fail > 0 ? "　" + Lang.F("{0} 個掉唔到（可能用緊）。", fail) : "");
        UpdateSummary();
    }
}

// =====================================================================
//  開機程式
// =====================================================================

/// <summary>
/// 開機自動行嘅程式：只做「停用 / 啟用」（同工作管理員「啟動」分頁一樣嘅做法，寫 StartupApproved），唔會刪除任何嘢，隨時可以還原。
/// 成部機共用嘅項目（HKLM）要管理員先改到；改唔到就叫用家用工作管理員。
/// </summary>
internal sealed class StartupPage : ToolPage
{
    private sealed record Item(string Name, string Command, string Source, RegistryKey ApprovedRoot, string ApprovedKey, bool Machine);

    private readonly RowList _list = new();
    private readonly Label _status;
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private static readonly RegistryKey Hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
    private static readonly RegistryKey Hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);

    public static Control Build() => new StartupPage();

    private StartupPage()
    {
        _status = TUi.L("");
        _list.Columns = new[] { (Lang.T("名稱"), 220, false), (Lang.T("指令"), 0, false), (Lang.T("位置"), 150, false), (Lang.T("狀態"), 90, false) };
        _list.EmptyText = Lang.T("冇開機程式。");
        _list.Width = Ui.S(820); _list.Height = Ui.S(460);
        _list.RowAction += Toggle;
        _list.RowMenu += (r, p) =>
        {
            if (r.Tag is not Item it) return;
            var menu = TUi.Menu(_list);
            menu.Items.Add(Lang.T("開啟檔案位置"), null, (_, _) => { string? exe = ExePath(it.Command); if (exe != null) DupPage.OpenLocation(exe); });
            menu.Items.Add(Lang.T("複製指令"), null, (_, _) => { try { Clipboard.SetText(it.Command); } catch { } });
            menu.Show(_list, p);
        };
        Card(Lang.T("開機程式"), Lang.T("呢啲程式會喺你登入 Windows 嗰陣自動開。停用咗開機會快啲；只係停用，唔會刪除，隨時可以啟用返。唔識嘅項目唔好亂停（例如防毒軟件、顯示卡同音效驅動）。"),
            TUi.Col(TUi.Row(TUi.Btn(Lang.T("↻ 重新整理"), Reload), TUi.Btn(Lang.T("開啟工作管理員"), () => { try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { } }), _status), _list));
        Resize += (_, _) => _list.Width = Math.Max(Ui.S(480), Width - Ui.S(110));
        Reload();
    }

    /// <summary>由指令抽出 exe 路徑（有引號就攞引號入面；冇就搵到 .exe 為止）。</summary>
    private static string? ExePath(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        string path;
        if (cmd.StartsWith('"')) { int e = cmd.IndexOf('"', 1); path = e > 0 ? cmd[1..e] : cmd.Trim('"'); }
        else { int e = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); path = e > 0 ? cmd[..(e + 4)] : cmd; }
        return File.Exists(path) ? path : null;
    }

    private static bool IsEnabled(Item it)
    {
        try
        {
            using var k = it.ApprovedRoot.OpenSubKey(Approved + it.ApprovedKey);
            // 冇紀錄 = 啟用；第一個 byte 係雙數（02 / 06）= 啟用，單數（03 / 07）= 停用
            return k?.GetValue(it.Name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
        }
        catch { return true; }
    }

    private List<Item> Load()
    {
        var items = new List<Item>();
        void Run(RegistryKey root, string path, string approvedKey, string source, bool machine) => RunFrom(root, root, path, approvedKey, source, machine);
        // readRoot = 喺邊度讀開機項目；approvedRoot = 「停用 / 啟用」紀錄喺邊度（所有帳戶嘅一律喺 64 位元嗰邊）
        void RunFrom(RegistryKey readRoot, RegistryKey approvedRoot, string path, string approvedKey, string source, bool machine)
        {
            try
            {
                using var k = readRoot.OpenSubKey(path);
                if (k == null) return;
                foreach (var n in k.GetValueNames())
                    if (n.Length > 0 && k.GetValue(n) is string cmd && cmd.Trim().Length > 0) items.Add(new Item(n, cmd, source, approvedRoot, approvedKey, machine));
            }
            catch { }
        }
        Run(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", Lang.T("呢個帳戶"), false);
        // 程式可能係 32 位元：一定要講明睇 64 位元定 32 位元嗰邊嘅登錄檔，唔係會俾 Windows 自動轉咗去 WOW6432Node
        Run(Hklm64, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", Lang.T("所有帳戶"), true);
        if (Environment.Is64BitOperatingSystem) RunFrom(Hklm32, Hklm64, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run32", Lang.T("所有帳戶"), true);
        void Folder(Environment.SpecialFolder sf, RegistryKey root, string source, bool machine)
        {
            try
            {
                string dir = Environment.GetFolderPath(sf);
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir))
                    if (!Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) items.Add(new Item(Path.GetFileName(f), f, source, root, "StartupFolder", machine));
            }
            catch { }
        }
        Folder(Environment.SpecialFolder.Startup, Registry.CurrentUser, Lang.T("啟動資料夾"), false);
        Folder(Environment.SpecialFolder.CommonStartup, Hklm64, Lang.T("啟動資料夾（所有帳戶）"), true);
        return items;
    }

    private void Reload()
    {
        _list.Rows.Clear();
        foreach (var it in Load().OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            bool on = IsEnabled(it);
            _list.Rows.Add(new RowList.Row
            {
                Cells = new[] { Path.GetFileNameWithoutExtension(it.Name) is { Length: > 0 } n && it.ApprovedKey == "StartupFolder" ? n : it.Name, it.Command, it.Source, on ? Lang.T("已啟用") : Lang.T("已停用") },
                Dim = !on, Action = on ? Lang.T("停用") : Lang.T("啟用"), Tag = it
            });
        }
        _list.Reload();
    }

    private void Toggle(RowList.Row r)
    {
        if (r.Tag is not Item it) return;
        bool on = IsEnabled(it);
        try
        {
            using var k = it.ApprovedRoot.CreateSubKey(Approved + it.ApprovedKey, true);
            var data = new byte[12];
            data[0] = on ? (byte)3 : (byte)2;
            if (on) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);   // 停用時間（同工作管理員一樣記低）
            k.SetValue(it.Name, data, RegistryValueKind.Binary);
            _status.ForeColor = Ui.Success;
            _status.Text = on ? Lang.F("已停用 {0}，下次登入生效。", it.Name) : Lang.F("已啟用 {0}。", it.Name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            _status.ForeColor = Ui.Danger;
            _status.Text = Lang.T("呢個項目係成部機共用嘅，要管理員權限先改到：請撳「開啟工作管理員」→「啟動應用程式」改。");
        }
        catch (Exception ex) { _status.ForeColor = Ui.Danger; _status.Text = ex.Message; }
        Reload();
    }
}

// =====================================================================
//  已安裝軟件
// =====================================================================

/// <summary>列出已安裝嘅軟件（同「設定 → 應用程式」一樣嘅資料），可以搜尋、按大細排，撳「解除安裝」就開返嗰個軟件自己嘅解除安裝程式。</summary>
internal sealed class AppsPage : ToolPage
{
    private sealed record App(string Name, string Version, string Publisher, string Date, long SizeKb, string Uninstall, string Location);

    private readonly RowList _list = new();
    private readonly TextBox _search;
    private readonly ComboBox _sort;
    private readonly Label _status;
    private List<App> _apps = new();

    public static Control Build() => new AppsPage();

    private AppsPage()
    {
        _search = TIn.Box("", 240, Lang.T("搜尋軟件…"));
        _sort = TIn.Combo(150, new[] { Lang.T("按名稱排"), Lang.T("按大細排"), Lang.T("按安裝日期排") });
        _status = TUi.L("");
        _list.Columns = new[] { (Lang.T("名稱"), 0, false), (Lang.T("發行者"), 190, false), (Lang.T("版本"), 110, false), (Lang.T("安裝日期"), 100, false), (Lang.T("大細"), 90, true) };
        _list.ActionText = Lang.T("解除安裝");
        _list.EmptyText = Lang.T("搵唔到軟件。");
        _list.Width = Ui.S(860); _list.Height = Ui.S(480);
        _list.RowAction += Uninstall;
        _list.RowMenu += (r, p) =>
        {
            if (r.Tag is not App a) return;
            var menu = TUi.Menu(_list);
            var loc = menu.Items.Add(Lang.T("開啟安裝位置"), null, (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + a.Location + "\"") { UseShellExecute = true }); } catch { } });
            loc.Enabled = a.Location.Length > 0 && Directory.Exists(a.Location);
            menu.Show(_list, p);
        };
        Card(Lang.T("已安裝軟件"), Lang.T("撳「解除安裝」會開返嗰個軟件自己嘅解除安裝程式，由佢處理。由 Microsoft Store 裝嘅 app 唔喺呢度，請用 Windows 設定。"),
            TUi.Col(
                TUi.Row(_search, _sort, TUi.Btn(Lang.T("↻ 重新整理"), Reload), TUi.Btn(Lang.T("開啟 Windows 應用程式設定"), () => { try { Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }); } catch { } }), _status),
                _list));
        _search.TextChanged += (_, _) => Fill();
        _sort.SelectedIndexChanged += (_, _) => Fill();
        Resize += (_, _) => _list.Width = Math.Max(Ui.S(480), Width - Ui.S(110));
        Reload();
    }

    private static List<App> Load()
    {
        var apps = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);
        void Read(RegistryKey root, string path)
        {
            try
            {
                using var k = root.OpenSubKey(path);
                if (k == null) return;
                foreach (var sub in k.GetSubKeyNames())
                {
                    try
                    {
                        using var a = k.OpenSubKey(sub);
                        if (a == null) continue;
                        string name = (a.GetValue("DisplayName") as string ?? "").Trim();
                        string un = (a.GetValue("UninstallString") as string ?? "").Trim();
                        // 系統元件、更新、冇解除安裝程式嘅唔顯示（同 Windows 設定一樣）
                        if (name.Length == 0 || un.Length == 0) continue;
                        if (a.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        if (a.GetValue("ParentKeyName") is string pk && pk.Length > 0) continue;
                        if (a.GetValue("ReleaseType") is string rt && rt.Contains("Update", StringComparison.OrdinalIgnoreCase)) continue;
                        string date = a.GetValue("InstallDate") as string ?? "";
                        if (date.Length == 8) date = date[..4] + "-" + date.Substring(4, 2) + "-" + date.Substring(6, 2);
                        long size = a.GetValue("EstimatedSize") is int s ? (uint)s : 0;
                        // MSI：登記嘅通常係 /I（修改），解除安裝要用 /X
                        if (a.GetValue("WindowsInstaller") is int wi && wi == 1 && sub.StartsWith('{')) un = "MsiExec.exe /X" + sub;
                        apps[name + "|" + (a.GetValue("DisplayVersion") as string ?? "")] = new App(name, a.GetValue("DisplayVersion") as string ?? "", a.GetValue("Publisher") as string ?? "", date, size, un, (a.GetValue("InstallLocation") as string ?? "").Trim('"', ' '));
                    }
                    catch { }
                }
            }
            catch { }
        }
        const string U = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        // 64 位元同 32 位元軟件登記喺登錄檔唔同嘅一邊，兩邊都要睇（程式本身可能係 32 位元）
        using (var h64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) Read(h64, U);
        using (var h32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)) Read(h32, U);
        Read(Registry.CurrentUser, U);
        return apps.Values.ToList();
    }

    private async void Reload()
    {
        _status.ForeColor = Ui.TextDim;
        _status.Text = Lang.T("讀取緊…");
        _apps = await Task.Run(Load);
        if (IsDisposed) return;
        Fill();
    }

    private void Fill()
    {
        string q = _search.Text.Trim();
        IEnumerable<App> list = _apps.Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase));
        list = _sort.SelectedIndex switch
        {
            1 => list.OrderByDescending(a => a.SizeKb),
            2 => list.OrderByDescending(a => a.Date),
            _ => list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        _list.Rows.Clear();
        foreach (var a in list)
            _list.Rows.Add(new RowList.Row { Cells = new[] { a.Name, a.Publisher, a.Version, a.Date, a.SizeKb > 0 ? TIn.Size(a.SizeKb * 1024) : "" }, Tag = a });
        _list.Reload();
        _status.ForeColor = Ui.TextDim;
        _status.Text = Lang.F("{0} 個軟件", _list.Rows.Count) + (_apps.Sum(a => a.SizeKb) is > 0 and var kb ? "　" + Lang.F("合共約 {0}", TIn.Size(kb * 1024)) : "");
    }

    private void Uninstall(RowList.Row r)
    {
        if (r.Tag is not App a) return;
        if (MessageBox.Show(FindForm(), Lang.F("解除安裝「{0}」？\n\n會開啟呢個軟件自己嘅解除安裝程式，之後照佢嘅指示做。", a.Name), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            // 拆開「程式」同「參數」直接行，唔經命令提示字元
            string cmd = a.Uninstall, exe, args;
            if (cmd.StartsWith('"')) { int e = cmd.IndexOf('"', 1); exe = e > 0 ? cmd[1..e] : cmd.Trim('"'); args = e > 0 ? cmd[(e + 1)..].Trim() : ""; }
            else
            {
                int e = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (e > 0) { exe = cmd[..(e + 4)]; args = cmd[(e + 4)..].Trim(); } else { exe = cmd; args = ""; }
            }
            Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(exe), args) { UseShellExecute = true });
            _status.ForeColor = Ui.TextDim;
            _status.Text = Lang.T("已開啟解除安裝程式；完成後撳「↻ 重新整理」。");
        }
        catch (Exception ex)
        {
            AppPaths.LogError("解除安裝", ex);
            _status.ForeColor = Ui.Danger;
            _status.Text = Lang.T("開唔到解除安裝程式：") + ex.Message;
        }
    }
}
