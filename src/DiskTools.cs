using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.Common;
using MultiTool.UI;

namespace MultiTool.Tools;

// =====================================================================
//  磁碟空間及健康分析：各磁碟用量、硬碟健康（Windows 內置 Storage 資料）、
//  資料夾空間分析（方格圖 + 最大檔案）。
// =====================================================================

internal sealed class DiskHealth
{
    public string Name = "", Media = "", Bus = "", Health = "", Status = "", Letters = "";
    public long Size;
    public double? Temp, TempMax, Wear, Hours, ReadErr, WriteErr;

    /// <summary>經 PowerShell 讀 Get-PhysicalDisk（溫度 / 損耗要管理員權限）。</summary>
    public static async Task<List<DiskHealth>> ReadAsync()
    {
        const string ps = @"[Console]::OutputEncoding=[Text.Encoding]::UTF8
$parts = @(Get-Partition -ErrorAction SilentlyContinue | Where-Object DriveLetter)
$r = foreach ($d in Get-PhysicalDisk) {
  $c = $null; try { $c = $d | Get-StorageReliabilityCounter -ErrorAction Stop } catch {}
  $l = ($parts | Where-Object { $_.DiskNumber -eq [int]$d.DeviceId } | ForEach-Object { ""$($_.DriveLetter):"" }) -join ' '
  [pscustomobject]@{ Name=$d.FriendlyName; Media=[string]$d.MediaType; Bus=[string]$d.BusType; Size=[int64]$d.Size;
    Health=[string]$d.HealthStatus; Status=($d.OperationalStatus -join ', '); Letters=$l;
    Temp=$c.Temperature; TempMax=$c.TemperatureMax; Wear=$c.Wear; Hours=$c.PowerOnHours;
    ReadErr=$c.ReadErrorsUncorrected; WriteErr=$c.WriteErrorsUncorrected }
}
ConvertTo-Json -Compress -InputObject @($r)";
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(ps)))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(30000);
        try { await p.WaitForExitAsync(cts.Token); } catch { try { p.Kill(); } catch { } throw new TimeoutException(); }
        var json = (await outTask).Trim();
        var list = new List<DiskHealth>();
        if (json.Length == 0) return list;
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
        static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static double? N(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        foreach (var e in arr)
            list.Add(new DiskHealth
            {
                Name = S(e, "Name"), Media = S(e, "Media"), Bus = S(e, "Bus"), Health = S(e, "Health"), Status = S(e, "Status"), Letters = S(e, "Letters"),
                Size = (long)(N(e, "Size") ?? 0), Temp = N(e, "Temp"), TempMax = N(e, "TempMax"), Wear = N(e, "Wear"), Hours = N(e, "Hours"),
                ReadErr = N(e, "ReadErr"), WriteErr = N(e, "WriteErr")
            });
        return list;
    }
}

/// <summary>掃描結果嘅一個資料夾 / 檔案。</summary>
internal sealed class SpaceNode
{
    public required string Name, Path;
    public bool IsDir;
    public long Size;
    public long Files;
    public SpaceNode? Parent;
    public List<SpaceNode> Children = new();
}

internal static class SpaceScanner
{
    private static readonly EnumerationOptions Opts = new()
    {
        IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false
    };

    public static SpaceNode Scan(string root, Action<long, long> progress, ConcurrentBag<SpaceNode> big, CancellationToken ct)
    {
        var node = new SpaceNode { Name = root, Path = root, IsDir = true };
        long files = 0, bytes = 0;
        var sw = Stopwatch.StartNew();
        void Walk(SpaceNode dir, int depth)
        {
            ct.ThrowIfCancellationRequested();
            var subs = new List<SpaceNode>();
            try
            {
                foreach (var fsi in new DirectoryInfo(dir.Path).EnumerateFileSystemInfos("*", Opts))
                {
                    if (fsi is DirectoryInfo d)
                        subs.Add(new SpaceNode { Name = d.Name, Path = d.FullName, IsDir = true, Parent = dir });
                    else if (fsi is FileInfo f)
                    {
                        long len = 0;
                        try { len = f.Length; } catch { }
                        dir.Size += len;
                        dir.Files++;
                        var fn = new SpaceNode { Name = f.Name, Path = f.FullName, Size = len, Files = 1, Parent = dir };
                        dir.Children.Add(fn);
                        if (len >= 100L << 20) big.Add(fn);
                        Interlocked.Increment(ref files);
                        Interlocked.Add(ref bytes, len);
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            if (depth < 3 && subs.Count > 1)
                Parallel.ForEach(subs, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, s => Walk(s, depth + 1));
            else
                foreach (var s in subs) Walk(s, depth + 1);
            foreach (var s in subs) { dir.Size += s.Size; dir.Files += s.Files; }
            lock (dir.Children) dir.Children.AddRange(subs);
            if (sw.ElapsedMilliseconds > 200) { sw.Restart(); progress(Interlocked.Read(ref files), Interlocked.Read(ref bytes)); }
        }
        Walk(node, 0);
        Sort(node);
        progress(files, bytes);
        return node;
    }

    private static void Sort(SpaceNode n)
    {
        n.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        foreach (var c in n.Children) if (c.IsDir) Sort(c);
    }
}

/// <summary>方格圖（squarified treemap）。</summary>
internal sealed class TreeMap : Control
{
    private SpaceNode? _node;
    private readonly List<(RectangleF R, SpaceNode N)> _rects = new();
    private SpaceNode? _hover;
    private readonly ToolTip _tip = new();
    public event Action<SpaceNode>? Open;
    public event Action<SpaceNode, Point>? Menu;

    public TreeMap()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = Ui.S(320);
        Cursor = Cursors.Hand;
    }

    public SpaceNode? Node { get => _node; set { _node = value; _hover = null; Invalidate(); } }

    private static void Squarify(List<SpaceNode> items, RectangleF r, List<(RectangleF, SpaceNode)> output)
    {
        double total = items.Sum(i => (double)i.Size);
        if (total <= 0 || r.Width < 1 || r.Height < 1) return;
        double scale = r.Width * r.Height / total;
        int i = 0;
        while (i < items.Count)
        {
            bool horiz = r.Width >= r.Height;
            float side = horiz ? r.Height : r.Width;
            var row = new List<SpaceNode>();
            double rowSum = 0, best = double.MaxValue;
            while (i < items.Count)
            {
                double a = items[i].Size * scale, s2 = rowSum + a;
                double max = Math.Max(row.Count > 0 ? row.Max(x => x.Size * scale) : 0, a), min = Math.Min(row.Count > 0 ? row.Min(x => x.Size * scale) : double.MaxValue, a);
                double worst = Math.Max(side * side * max / (s2 * s2), s2 * s2 / (side * side * min));
                if (row.Count > 0 && worst > best) break;
                best = worst; row.Add(items[i]); rowSum = s2; i++;
            }
            float thick = (float)(rowSum / side);
            float off = 0;
            foreach (var n in row)
            {
                float len = (float)(n.Size * scale / thick);
                var rr = horiz ? new RectangleF(r.X, r.Y + off, thick, len) : new RectangleF(r.X + off, r.Y, len, thick);
                output.Add((rr, n));
                off += len;
            }
            r = horiz ? new RectangleF(r.X + thick, r.Y, r.Width - thick, r.Height) : new RectangleF(r.X, r.Y + thick, r.Width, r.Height - thick);
        }
    }

    private static Color HueOf(SpaceNode n)
    {
        if (n.IsDir) return Ui.Accent;
        return System.IO.Path.GetExtension(n.Name).ToLowerInvariant() switch
        {
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => Ui.Danger,
            ".zip" or ".rar" or ".7z" or ".iso" or ".tar" or ".gz" => Ui.Hex(0xE0A030),
            ".jpg" or ".png" or ".gif" or ".webp" or ".psd" => Ui.Success,
            ".exe" or ".dll" or ".msi" or ".sys" => Ui.Accent2,
            ".pak" or ".bin" or ".dat" or ".vpk" or ".bundle" => Ui.Hex(0x3FB8C9),
            _ => Ui.TextFaint
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Card);
        _rects.Clear();
        if (_node == null || _node.Size <= 0) return;
        var items = _node.Children.Where(c => c.Size > 0).ToList();
        var outp = new List<(RectangleF, SpaceNode)>();
        Squarify(items, new RectangleF(0, 0, Width - 1, Height - 1), outp);
        _rects.AddRange(outp);
        foreach (var (r, n) in _rects)
        {
            var hue = HueOf(n);
            var fill = Ui.Mix(Ui.CardHi, hue, n == _hover ? 0.55f : 0.32f);
            using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
            using (var p = new Pen(Ui.Card, Ui.S(2f))) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            if (r.Width > Ui.S(60) && r.Height > Ui.S(34))
            {
                var tr = Rectangle.Round(new RectangleF(r.X + Ui.S(6), r.Y + Ui.S(5), r.Width - Ui.S(12), r.Height - Ui.S(10)));
                TextRenderer.DrawText(g, (n.IsDir ? "📁 " : "") + n.Name + "\n" + PerfSampler.Bytes(n.Size), Ui.F(9f), tr, Ui.Text,
                    TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }

    private SpaceNode? Hit(Point p) => _rects.FirstOrDefault(x => x.R.Contains(p)).N;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = Hit(e.Location);
        if (h == _hover) return;
        _hover = h;
        _tip.SetToolTip(this, h == null ? "" : $"{h.Path}\n{PerfSampler.Bytes(h.Size)}" + (h.IsDir ? Lang.F("（{0} 個檔案）", h.Files.ToString("N0")) : ""));
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = null; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var h = Hit(e.Location);
        if (h == null) return;
        if (e.Button == MouseButtons.Right) Menu?.Invoke(h, e.Location);
        else if (e.Button == MouseButtons.Left && h.IsDir) Open?.Invoke(h);
    }
}

internal sealed class DiskPage : ToolPage
{
    private readonly FlowLayoutPanel _drives, _health, _list, _bigList, _crumbs;
    private readonly Label _healthNote, _scanStatus;
    private readonly TreeMap _map;
    private readonly FancyButton _scanBtn;
    private SpaceNode? _root, _cur;
    private List<SpaceNode> _big = new();
    private CancellationTokenSource? _cts;

    public static Control Build() => new DiskPage();

    private DiskPage()
    {
        _drives = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Card(Lang.T("磁碟用量"), null, TUi.Col(TUi.Row(TUi.Btn(Lang.T("↻ 重新整理"), BuildDrives)), _drives));

        _health = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        _healthNote = TUi.L("");
        Card(Lang.T("硬碟健康"), Lang.T("用 Windows 內置嘅硬碟資料（同「設定 → 存放裝置 → 磁碟與磁碟區」一樣）。溫度、損耗、開機時數要用管理員身份開 MultiTool 先睇到。"),
            TUi.Col(TUi.Row(TUi.Btn(Lang.T("↻ 重新檢查"), () => _ = BuildHealthAsync()), _healthNote), _health));

        _scanBtn = TUi.Btn(Lang.T("揀資料夾…"), PickFolder, 0, BtnKind.Primary);
        _scanStatus = TUi.L("");
        var quick = TUi.Row(_scanBtn);
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var root = d.RootDirectory.FullName;
            quick.Controls.Add(TUi.Btn(Lang.T("掃 ") + d.Name.TrimEnd('\\'), () => _ = ScanAsync(root)));
        }
        quick.Controls.Add(_scanStatus);
        _crumbs = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, WrapContents = true, Margin = new Padding(0, 0, 0, Ui.S(6)) };
        _map = new TreeMap { Width = Ui.S(900) };
        _map.Open += n => Show(n);
        _map.Menu += (n, p) => NodeMenu(n, _map, p);
        _list = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Card(Lang.T("空間分析"), Lang.T("搵出邊個資料夾最食位：撳方格入去睇，右撳可以開資料夾或者掉入資源回收筒。冇權限嘅系統資料夾會略過。"),
            TUi.Col(quick, _crumbs, _map, _list));

        _bigList = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        _bigList.Controls.Add(TUi.L(Lang.T("掃描完會列出最大嘅 30 個檔案（100 MB 以上）。")));
        Card(Lang.T("最大嘅檔案"), null, _bigList);

        Resize += (_, _) => FitMap();
        BuildDrives();
        _ = BuildHealthAsync();
        Disposed += (_, _) => _cts?.Cancel();
    }

    private void FitMap()
    {
        int w = Math.Max(Ui.S(400), Width - Ui.S(120));
        if (_map.Width != w) _map.Width = w;
    }

    private static Control Bar(double v, int w, Color hue)
    {
        var b = new ThinBar { Width = Ui.S(w), Height = Ui.S(8), Value = v, Margin = new Padding(0, Ui.S(14), Ui.S(12), 0) };
        return b;
    }

    private static Label Cell(string t, int w, Color c, bool bold = false) => new()
    {
        Text = t, AutoSize = false, Width = Ui.S(w), Height = Ui.S(34), ForeColor = c, BackColor = Ui.Card, UseMnemonic = false, AutoEllipsis = true,
        Font = Ui.F(9.75f, bold ? FontStyle.Bold : FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, Ui.S(8), 0)
    };

    private void Clear(Control host) { foreach (Control c in host.Controls.Cast<Control>().ToList()) { host.Controls.Remove(c); c.Dispose(); } }

    private void BuildDrives()
    {
        _drives.SuspendLayout();
        Clear(_drives);
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                double used = d.TotalSize - d.TotalFreeSpace, pct = used / Math.Max(1, d.TotalSize);
                var name = d.Name.TrimEnd('\\') + (d.VolumeLabel.Length > 0 ? "  " + d.VolumeLabel : "");
                var row = TUi.Row(Cell(name, 200, Ui.Text, true), Bar(pct, 260, Ui.Accent),
                    Cell(Lang.F("用咗 {0} / {1}", PerfSampler.Bytes(used), PerfSampler.Bytes(d.TotalSize)), 220, Ui.TextDim),
                    Cell(Lang.F("剩 {0}", PerfSampler.Bytes(d.TotalFreeSpace)), 120, pct > 0.9 ? Ui.Danger : Ui.Success, true),
                    Cell(d.DriveFormat + (d.DriveType == DriveType.Removable ? Lang.T("（外置）") : ""), 120, Ui.TextFaint));
                row.WrapContents = false; row.Margin = new Padding(0, 0, 0, Ui.S(2));
                _drives.Controls.Add(row);
            }
            catch { }
        }
        _drives.ResumeLayout(true);
        Relayout();
    }

    private async Task BuildHealthAsync()
    {
        _healthNote.Text = Lang.T("檢查緊…");
        List<DiskHealth> list;
        try { list = await DiskHealth.ReadAsync(); }
        catch (Exception ex) { AppPaths.LogError("disk health", ex); _healthNote.Text = Lang.T("讀唔到硬碟資料"); return; }
        if (IsDisposed) return;
        _health.SuspendLayout();
        Clear(_health);
        bool anyLimited = false;
        foreach (var d in list)
        {
            var (txt, col) = d.Health switch
            {
                "Healthy" => (Lang.T("● 良好"), Ui.Success),
                "Warning" => (Lang.T("● 要留意"), Ui.Hex(0xE0A030)),
                "Unhealthy" => (Lang.T("● 有問題，快啲備份"), Ui.Danger),
                _ => (Lang.T("● 不明"), Ui.TextFaint)
            };
            string media = d.Media switch { "SSD" => "SSD", "HDD" => Lang.T("傳統硬碟"), _ => "" };
            string kind = string.Join(" · ", new[] { media, d.Bus, PerfSampler.Bytes(d.Size), d.Letters }.Where(x => x.Length > 0 && x != "Unspecified"));
            var head = TUi.Row(Cell(d.Name, 300, Ui.Text, true), Cell(txt, 190, col, true), Cell(kind, 320, Ui.TextDim));
            head.WrapContents = false; head.Margin = new Padding(0);
            _health.Controls.Add(head);
            var bits = new List<string>();
            if (d.Temp is > 0) bits.Add(Lang.F("溫度 {0}°C", d.Temp.Value.ToString("0")) + (d.TempMax is > 0 ? Lang.F("（上限 {0}°C）", d.TempMax.Value.ToString("0")) : ""));
            if (d.Wear != null) bits.Add(Lang.F("已用壽命 {0}%", d.Wear.Value.ToString("0")));
            if (d.Hours is > 0) bits.Add(Lang.F("開機 {0} 小時（約 {1} 日）", d.Hours.Value.ToString("N0"), (d.Hours.Value / 24).ToString("N0")));
            double errs = (d.ReadErr ?? 0) + (d.WriteErr ?? 0);
            if (d.ReadErr != null || d.WriteErr != null) bits.Add(errs > 0 ? Lang.F("⚠ 讀寫錯誤 {0} 次", errs.ToString("N0")) : Lang.T("冇讀寫錯誤"));
            if (d.Status.Length > 0 && d.Status != "OK") bits.Add(Lang.T("狀態：") + d.Status);
            if (d.Temp == null && d.Wear == null && d.Hours == null) anyLimited = true;
            var detail = TUi.L(bits.Count > 0 ? string.Join("   ·   ", bits) : Lang.T("（詳細資料要管理員權限，或者呢隻硬碟唔提供）"),
                errs > 0 || d.Wear is >= 90 ? Ui.Danger : Ui.TextFaint);
            detail.Margin = new Padding(0, 0, 0, Ui.S(12));
            _health.Controls.Add(detail);
        }
        if (list.Count == 0) _health.Controls.Add(TUi.L(Lang.T("搵唔到硬碟資料")));
        _health.ResumeLayout(true);
        _healthNote.Text = anyLimited && !IsAdmin() ? Lang.T("部分資料要用管理員身份開 MultiTool") : "";
        Relayout();
    }

    private static bool IsAdmin()
    {
        try { return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    private void PickFolder()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        using var dlg = new FolderBrowserDialog();
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) _ = ScanAsync(dlg.SelectedPath);
    }

    private async Task ScanAsync(string path)
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _scanBtn.Text = Lang.T("■ 停止掃描");
        _scanBtn.Kind = BtnKind.Danger;
        _scanBtn.Invalidate();
        var big = new ConcurrentBag<SpaceNode>();
        var ct = _cts.Token;
        try
        {
            var node = await Task.Run(() => SpaceScanner.Scan(path, (f, b) =>
            {
                try { BeginInvoke(() => _scanStatus.Text = Lang.F("掃緊… {0} 個檔案，{1}", f.ToString("N0"), PerfSampler.Bytes(b))); } catch { }
            }, big, ct), ct);
            _root = node;
            _big = big.OrderByDescending(x => x.Size).Take(30).ToList();
            _scanStatus.Text = Lang.F("完成：{0} 個檔案，{1}", node.Files.ToString("N0"), PerfSampler.Bytes(node.Size));
            Show(node);
            BuildBig();
        }
        catch (OperationCanceledException) { _scanStatus.Text = Lang.T("已停止"); }
        catch (Exception ex) { AppPaths.LogError("space scan", ex); _scanStatus.Text = Lang.T("失敗：") + ex.Message; }
        finally
        {
            _cts = null;
            if (!_scanBtn.IsDisposed)
            {
                _scanBtn.Text = Lang.T("揀資料夾…");
                _scanBtn.Kind = BtnKind.Primary;
                _scanBtn.Invalidate();
            }
        }
    }

    private void Show(SpaceNode n)
    {
        _cur = n;
        FitMap();
        _map.Node = n;
        _crumbs.SuspendLayout();
        Clear(_crumbs);
        var chain = new List<SpaceNode>();
        for (var x = n; x != null; x = x.Parent) chain.Insert(0, x);
        foreach (var c in chain)
        {
            var node = c;
            var b = TUi.Btn((c == chain[0] ? c.Path : c.Name) + (c == n ? "" : "  ›"), () => Show(node));
            if (c == n) b.Kind = BtnKind.Primary;
            _crumbs.Controls.Add(b);
            b.Margin = new Padding(0, 0, Ui.S(6), Ui.S(6));
        }
        _crumbs.ResumeLayout(true);

        _list.SuspendLayout();
        Clear(_list);
        foreach (var c in n.Children.Take(20))
        {
            var node = c;
            double pct = n.Size > 0 ? (double)c.Size / n.Size : 0;
            var name = Cell((c.IsDir ? "📁 " : "📄 ") + c.Name, 360, Ui.Text, c.IsDir);
            var row = TUi.Row(name, Bar(pct, 160, Ui.Accent), Cell(PerfSampler.Bytes(c.Size), 100, Ui.TextDim, true),
                Cell(pct.ToString("P1"), 70, Ui.TextFaint), Cell(c.IsDir ? Lang.F("{0} 個檔案", c.Files.ToString("N0")) : "", 130, Ui.TextFaint));
            row.WrapContents = false; row.Margin = new Padding(0);
            void Click(object? s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) NodeMenu(node, (Control)s!, e.Location);
                else if (node.IsDir) Show(node);
            }
            foreach (Control x in row.Controls) { x.MouseUp += Click; x.Cursor = Cursors.Hand; }
            _list.Controls.Add(row);
        }
        if (n.Children.Count > 20) _list.Controls.Add(TUi.L(Lang.F("…仲有 {0} 項", n.Children.Count - 20)));
        _list.ResumeLayout(true);
        Relayout();
    }

    private void BuildBig()
    {
        _bigList.SuspendLayout();
        Clear(_bigList);
        if (_big.Count == 0) _bigList.Controls.Add(TUi.L(Lang.T("冇 100 MB 以上嘅檔案")));
        foreach (var f in _big)
        {
            var node = f;
            var row = TUi.Row(Cell(f.Name, 300, Ui.Text, true), Cell(PerfSampler.Bytes(f.Size), 100, Ui.TextDim, true),
                Cell(System.IO.Path.GetDirectoryName(f.Path) ?? "", 420, Ui.TextFaint));
            row.WrapContents = false; row.Margin = new Padding(0);
            foreach (Control x in row.Controls) { x.Cursor = Cursors.Hand; x.MouseUp += (s, e) => NodeMenu(node, (Control)s!, e.Location); }
            _bigList.Controls.Add(row);
        }
        _bigList.ResumeLayout(true);
        Relayout();
    }

    private void NodeMenu(SpaceNode n, Control at, Point p)
    {
        var menu = TUi.Menu(at);
        if (n.IsDir)
        {
            var open = new ToolStripMenuItem(Lang.T("入去睇")) { ForeColor = Ui.Text };
            open.Click += (_, _) => Show(n);
            menu.Items.Add(open);
        }
        var reveal = new ToolStripMenuItem(Lang.T("喺檔案總管顯示")) { ForeColor = Ui.Text };
        reveal.Click += (_, _) => { if (n.IsDir) TUi.Open(n.Path, true); else TUi.Reveal(n.Path); };
        menu.Items.Add(reveal);
        if (n.Parent != null)
        {
            menu.Items.Add(new ToolStripSeparator());
            var del = new ToolStripMenuItem(Lang.T("掉入資源回收筒…")) { ForeColor = Ui.Danger };
            del.Click += (_, _) => Recycle(n);
            menu.Items.Add(del);
        }
        menu.Show(at, p);
    }

    private void Recycle(SpaceNode n)
    {
        if (MessageBox.Show(FindForm(), Lang.F("將「{0}」（{1}）掉入資源回收筒？\n\n{2}\n\n唔肯定係咩就唔好刪，特別係 Windows 同程式資料夾。", n.Name, PerfSampler.Bytes(n.Size), n.Path),
                Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            if (n.IsDir)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(n.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(n.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Program.AppName); return; }
        // 由上面嘅資料夾減返大小
        for (var x = n.Parent; x != null; x = x.Parent) { x.Size -= n.Size; x.Files -= n.Files; }
        n.Parent?.Children.Remove(n);
        _big.RemoveAll(b => b == n || b.Path.StartsWith(n.Path + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Show(n.Parent ?? _root!);
        BuildBig();
        BuildDrives();
        Toast.Show(Lang.T("已掉入資源回收筒"));
    }
}
