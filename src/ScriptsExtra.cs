using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Scripts;

// =====================================================================
//  我的腳本：文字 / 數字辨識、變數、通知、定時開始
// =====================================================================

/// <summary>
/// 腳本用嘅文字辨識：開一個常駐 PowerShell（Windows 內置 OCR），一張圖送入去就回傳文字。
/// 唔使每次重新開 PowerShell，所以 IF 條件每秒檢查都頂得順。
/// </summary>
internal static class OcrServer
{
    private const string Script = @"$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(-1) | Out-Null; $task.Result }
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
while ($true) {
  $line = [Console]::In.ReadLine()
  if ($line -eq $null) { break }
  try {
    $path = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line.Trim()))
    if (-not $engine) { [Console]::Out.WriteLine('<<ERR>>NOENGINE') }
    else {
      $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($path)) ([Windows.Storage.StorageFile])
      $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
      $dec = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
      $bmp = Await ($dec.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
      $res = Await ($engine.RecognizeAsync($bmp)) ([Windows.Media.Ocr.OcrResult])
      foreach ($l in $res.Lines) { [Console]::Out.WriteLine($l.Text) }
      $stream.Dispose()
    }
  } catch { [Console]::Out.WriteLine('<<ERR>>' + $_.Exception.Message) }
  [Console]::Out.WriteLine('<<END>>')
  [Console]::Out.Flush()
}
";

    private static readonly object Lock = new();
    private static Process? _p;
    private static string Dir => Path.Combine(AppPaths.Data, "tools", "ocr");

    private static string PowerShell =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private static Process Start()
    {
        if (_p is { HasExited: false }) return _p;
        try { _p?.Dispose(); } catch { }
        Directory.CreateDirectory(Dir);
        var ps1 = Path.Combine(Dir, "ocr-server.ps1");
        File.WriteAllText(ps1, Script, new UTF8Encoding(true)); // PowerShell 5 要 BOM 先識讀 UTF-8
        var psi = new ProcessStartInfo(PowerShell, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ps1}\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        _p = Process.Start(psi)!;
        _p.ErrorDataReceived += (_, _) => { };
        _p.BeginErrorReadLine();
        return _p;
    }

    /// <summary>閂咗 MultiTool 時一齊閂。</summary>
    public static void Stop()
    {
        lock (Lock)
        {
            try { if (_p is { HasExited: false }) _p.Kill(); } catch { }
            try { _p?.Dispose(); } catch { }
            _p = null;
        }
    }

    /// <summary>辨識一張圖（細圖會放大兩倍，準啲）。出錯會 throw。</summary>
    public static string Recognize(Bitmap img, CancellationToken ct)
    {
        lock (Lock)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, "region.png");
            int k = img.Height < 80 ? 3 : img.Height < 400 ? 2 : 1;
            using (var big = new Bitmap(Math.Min(2600, img.Width * k), Math.Min(2600, img.Height * k)))
            {
                using (var g = Graphics.FromImage(big))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, 0, 0, big.Width, big.Height);
                }
                big.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            var p = Start();
            p.StandardInput.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(path)));
            p.StandardInput.Flush();
            var lines = new List<string>();
            var until = DateTime.Now.AddSeconds(20);
            while (true)
            {
                var read = p.StandardOutput.ReadLineAsync();
                if (!read.Wait(Math.Max(1, (int)(until - DateTime.Now).TotalMilliseconds)) || read.Result == null)
                {
                    try { p.Kill(); } catch { }
                    throw new InvalidOperationException(Lang.T("文字辨識冇回應"));
                }
                var l = read.Result;
                if (l == "<<END>>") break;
                if (l.StartsWith("<<ERR>>"))
                {
                    var msg = l[7..];
                    throw new InvalidOperationException(msg == "NOENGINE"
                        ? Lang.T("Windows 冇文字辨識語言。請去 Windows 設定 → 時間與語言 → 語言，加入語言（連「光學字元辨識」）。")
                        : msg);
                }
                lines.Add(l);
            }
            // 中日韓文字之間 Windows OCR 會加空格：拎走
            string cjk = @"[\p{IsCJKUnifiedIdeographs}　-〿＀-￯぀-ヿ가-힯]";
            return Regex.Replace(string.Join("\n", lines), $@"(?<={cjk}) +(?={cjk})", "").Trim();
        }
    }

    /// <summary>由辨識到嘅文字攞第一個數字（例如「HP 1,234 / 5000」→ 1234）。搵唔到回傳 null。</summary>
    public static decimal? FirstNumber(string text)
    {
        // OCR 常見錯：O / o 當 0、l / I 當 1（淨係喺數字中間先改）
        var t = Regex.Replace(text, @"(?<=\d)[Oo](?=\d|\b)|(?<=\b)[Oo](?=\d)", "0");
        t = Regex.Replace(t, @"(?<=\d)[lI|](?=\d)", "1");
        var m = Regex.Match(t, @"-?\d[\d,]*(\.\d+)?");
        if (!m.Success) return null;
        var s = m.Value.Replace(",", "");
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>比較文字：唔理大細楷同空格。</summary>
    public static bool ContainsText(string haystack, string needle)
    {
        static string N(string s) => Regex.Replace(s ?? "", @"\s+", "");
        var n = N(needle);
        return n.Length > 0 && N(haystack).Contains(n, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>腳本變數：每次開始腳本都由 0 開始；主線程、新線程、IF 框共用。</summary>
/// <summary>
/// 位置清單（好似 array）：「變數 → 搵圖（記低所有位置）」存入去；
/// 循環可以逐個位置行，滑鼠可以撳「清單位置」（循環目前 / 第一個 / 最後一個 / 第 N 個）。
/// 每次開始腳本清空。座標係運行環境嘅座標（前景 = 螢幕；背景 = 視窗）。
/// </summary>
internal static class ScriptLists
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, List<System.Drawing.Point>>> All = new();

    private static ConcurrentDictionary<string, List<System.Drawing.Point>> Of(string scriptId) =>
        All.GetOrAdd(scriptId, _ => new ConcurrentDictionary<string, List<System.Drawing.Point>>(StringComparer.OrdinalIgnoreCase));

    public static void Reset(string scriptId) => All[scriptId] = new ConcurrentDictionary<string, List<System.Drawing.Point>>(StringComparer.OrdinalIgnoreCase);

    public static List<System.Drawing.Point> Get(string scriptId, string name) =>
        Of(scriptId).TryGetValue(name.Trim(), out var v) ? new List<System.Drawing.Point>(v) : new();

    public static void Set(string scriptId, string name, List<System.Drawing.Point> pts) => Of(scriptId)[name.Trim()] = new List<System.Drawing.Point>(pts);
}

internal static class ScriptVars
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, decimal>> All = new();

    private static ConcurrentDictionary<string, decimal> Of(string scriptId) =>
        All.GetOrAdd(scriptId, _ => new ConcurrentDictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));

    public static void Reset(string scriptId) => All[scriptId] = new ConcurrentDictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

    public static decimal Get(string scriptId, string name) => Of(scriptId).TryGetValue(name.Trim(), out var v) ? v : 0m;

    public static void Set(string scriptId, string name, decimal v) => Of(scriptId)[name.Trim()] = v;

    public static string Format(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>文字入面嘅 {變數名} 換做數值（輸入文字、通知用）。</summary>
    public static string Fill(string scriptId, string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('{')) return text;
        var vars = Of(scriptId);
        return Regex.Replace(text, @"\{([^{}\r\n]{1,30})\}", m =>
            vars.TryGetValue(m.Groups[1].Value.Trim(), out var v) ? Format(v) : m.Value);
    }

    /// <summary>腳本入面用過嘅變數名（編輯介面揀用）。</summary>
    public static List<string> NamesIn(Script sc)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Steps(IEnumerable<Step> steps)
        {
            foreach (var s in steps)
            {
                if (s.Kind == StepKind.Var && s.Var.Trim().Length > 0) names.Add(s.Var.Trim());
                if (s.Sub != null) { Cond(s.Sub); Steps(s.Sub.Steps); Steps(s.Sub.Else); }
                Steps(s.Body);
                foreach (var b in s.Branches) Steps(b);
            }
        }
        void Cond(IfBlock b)
        {
            foreach (var c in b.AllConds) if (c.Kind == CondKind.Variable && c.Var.Trim().Length > 0) names.Add(c.Var.Trim());
        }
        Steps(sc.Steps);
        foreach (var t in sc.Threads) Steps(t.Steps);
        foreach (var b in sc.Ifs) { Cond(b); Steps(b.Steps); Steps(b.Else); }
        return names.OrderBy(x => x).ToList();
    }
}

/// <summary>通知：Windows 通知 + Discord webhook（網址喺「設定 → 通知」）。</summary>
internal static class Notifier
{
    private static string File_ => Path.Combine(AppPaths.Data, "notify.txt");
    private static string? _url;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Discord webhook 網址（空 = 冇設定）。</summary>
    public static string DiscordUrl
    {
        get
        {
            if (_url != null) return _url;
            try
            {
                if (File.Exists(File_))
                    foreach (var l in File.ReadAllLines(File_))
                        if (l.StartsWith("discord=")) return _url = l[8..].Trim();
            }
            catch { }
            return _url = "";
        }
        set
        {
            _url = (value ?? "").Trim();
            try
            {
                Directory.CreateDirectory(AppPaths.Data);
                File.WriteAllLines(File_, new[] { "discord=" + _url });
            }
            catch (Exception ex) { AppPaths.LogError("儲存通知設定", ex); }
        }
    }

    public static bool ValidUrl(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("discordapp.com", StringComparison.OrdinalIgnoreCase)) &&
        u.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase);

    /// <summary>發通知（任何 thread 都叫得）。</summary>
    public static void Send(string title, string message, bool toast, bool discord)
    {
        if (toast) ShowToast(title, message);
        if (discord && DiscordUrl.Length > 0) _ = SendDiscordAsync(title, message);
    }

    private static void ShowToast(string title, string message)
    {
        var form = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
        if (form == null || form.IsDisposed) return;
        try
        {
            form.BeginInvoke(() =>
            {
                try
                {
                    // Windows 10 / 11 會顯示做通知（喺通知中心都睇到）
                    var icon = new NotifyIcon { Icon = form.Icon ?? SystemIcons.Information, Text = Program.AppName, Visible = true };
                    icon.ShowBalloonTip(5000, title, message.Length > 0 ? message : title, ToolTipIcon.Info);
                    var t = new System.Windows.Forms.Timer { Interval = 8000 };
                    t.Tick += (_, _) => { t.Stop(); t.Dispose(); icon.Visible = false; icon.Dispose(); };
                    t.Start();
                }
                catch (Exception ex) { AppPaths.LogError("Windows 通知", ex); }
            });
        }
        catch { }
    }

    /// <summary>發去 Discord；回傳出錯訊息（成功 = null）。</summary>
    public static async Task<string?> SendDiscordAsync(string title, string message, string? url = null)
    {
        url ??= DiscordUrl;
        if (!ValidUrl(url)) return Lang.T("Discord webhook 網址唔啱");
        try
        {
            string content = $"**{title}**" + (message.Length > 0 ? "\n" + message : "");
            if (content.Length > 1900) content = content[..1900] + "…";
            var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["content"] = content, ["username"] = "MultiTool" });
            using var body = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, body);
            if (resp.IsSuccessStatusCode) return null;
            return $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            AppPaths.LogError("Discord 通知", ex);
            return ex.Message;
        }
    }
}

/// <summary>定時開始 / 開 MultiTool 自動開始（「我的腳本」頁每 15 秒睇一次）。</summary>
internal static class ScriptSchedule
{
    private static readonly Dictionary<string, DateTime> LastFired = new();

    public static bool HasSchedule(Script s) => TimeSpan.TryParse(s.SchedTime, CultureInfo.InvariantCulture, out _) && (s.SchedDays & 127) != 0;

    /// <summary>星期幾（bit 0 = 星期日）。</summary>
    public static string DayName(int i) => i switch
    {
        0 => Lang.T("日"), 1 => Lang.T("一"), 2 => Lang.T("二"), 3 => Lang.T("三"), 4 => Lang.T("四"), 5 => Lang.T("五"), _ => Lang.T("六")
    };

    public static string Describe(Script s)
    {
        if (!HasSchedule(s)) return "";
        int d = s.SchedDays & 127;
        string days = d == 127 ? Lang.T("每日") : d == 0b0111110 ? Lang.T("星期一至五") : d == 0b1000001 ? Lang.T("星期六日")
            : Lang.T("星期") + string.Join(Lang.T("、"), Enumerable.Range(0, 7).Where(i => (d & (1 << i)) != 0).Select(DayName));
        return Lang.F("{0} {1} 自動開始", days, s.SchedTime);
    }

    /// <summary>下一次定時開始嘅時間（冇設定 = null）。</summary>
    public static DateTime? Next(Script s)
    {
        if (!HasSchedule(s)) return null;
        TimeSpan.TryParse(s.SchedTime, CultureInfo.InvariantCulture, out var at);
        var now = DateTime.Now;
        for (int d = 0; d <= 7; d++)
        {
            var t = now.Date.AddDays(d) + at;
            if (t <= now) continue;
            if ((s.SchedDays & (1 << (int)t.DayOfWeek)) != 0) return t;
        }
        return null;
    }

    /// <summary>腳本清單顯示：「⏰ 每日 08:00 · 下次 10-25（六）08:00」。</summary>
    public static string ListText(Script s)
    {
        var parts = new List<string>();
        var next = Next(s);
        if (next != null)
        {
            int d = s.SchedDays & 127;
            string days = d == 127 ? Lang.T("每日") : d == 0b0111110 ? Lang.T("星期一至五") : d == 0b1000001 ? Lang.T("星期六日")
                : Lang.T("星期") + string.Join(Lang.T("、"), Enumerable.Range(0, 7).Where(i => (d & (1 << i)) != 0).Select(DayName));
            parts.Add(Lang.F("⏰ {0} {1} · 下次 {2:MM-dd}（{3}）{2:HH:mm}", days, s.SchedTime, next.Value, DayName((int)next.Value.DayOfWeek)));
        }
        if (s.AutoStart) parts.Add(Lang.T("開 MultiTool 自動開始"));
        return string.Join(" · ", parts);
    }

    /// <summary>夠鐘就開始（同一日只會開一次；行緊就唔理）。</summary>
    public static void Tick(IEnumerable<Script> scripts)
    {
        var now = DateTime.Now;
        foreach (var s in scripts)
        {
            if (!HasSchedule(s) || ScriptRunner.IsRunning(s)) continue;
            if ((s.SchedDays & (1 << (int)now.DayOfWeek)) == 0) continue;
            TimeSpan.TryParse(s.SchedTime, CultureInfo.InvariantCulture, out var at);
            var due = now.Date + at;
            // 遲咗 2 分鐘內都照開（例如電腦啱啱醒）
            if (now < due || now > due.AddMinutes(2)) continue;
            string key = s.Folder.Length > 0 ? s.Folder : s.Id;
            if (LastFired.TryGetValue(key, out var last) && last.Date == now.Date && last >= due) continue;
            LastFired[key] = now;
            ScriptRunner.Start(s);
        }
    }
}

/// <summary>「⏰ 定時 / 通知」設定細窗。</summary>
internal sealed class ScriptAutoDialog : Form
{
    private ScriptAutoDialog(Script sc)
    {
        Text = Lang.T("定時同通知") + " · " + sc.Name;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.F(9.75f);

        bool sched = ScriptSchedule.HasSchedule(sc);
        TimeSpan.TryParse(sc.SchedTime, CultureInfo.InvariantCulture, out var ts);
        if (!sched) ts = new TimeSpan(DateTime.Now.Hour, 0, 0);
        int days = sc.SchedDays & 127;
        if (days == 0) days = 127;

        int x0 = Ui.S(20), y = Ui.S(18);
        Label L(string t, Color? c = null, float size = 9.75f, bool bold = false)
        {
            var l = Ui.Text_(t, c ?? Ui.TextDim, Ui.F(size, bold ? FontStyle.Bold : FontStyle.Regular), new Padding(0));
            l.BackColor = Ui.Card;
            Controls.Add(l);
            return l;
        }

        // ---- 每日定時開始 ----
        var schedChip = new ToggleChip(Lang.T("每日定時開始")) { Checked = sched, Location = new Point(x0, y) };
        Controls.Add(schedChip);
        y += Ui.S(40);
        var hh = new NumberBox(Ui.S(72)) { Minimum = 0, Maximum = 23, Increment = 1 };
        hh.Value = ts.Hours;
        var mm = new NumberBox(Ui.S(72)) { Minimum = 0, Maximum = 59, Increment = 1 };
        mm.Value = ts.Minutes;
        hh.Location = new Point(x0 + Ui.S(24), y);
        Controls.Add(hh);
        var lh = L(Lang.T("時")); lh.Location = new Point(hh.Right + Ui.S(6), y + Ui.S(8));
        mm.Location = new Point(lh.Right + Ui.S(10), y);
        Controls.Add(mm);
        var lm = L(Lang.T("分")); lm.Location = new Point(mm.Right + Ui.S(6), y + Ui.S(8));
        y += Ui.S(46);
        var dayChips = new List<ToggleChip>();
        int dx = x0 + Ui.S(24);
        foreach (int i in new[] { 1, 2, 3, 4, 5, 6, 0 }) // 星期一排先
        {
            var c = new ToggleChip(ScriptSchedule.DayName(i)) { Checked = (days & (1 << i)) != 0, Tag = i, Location = new Point(dx, y) };
            c.Size = new Size(Ui.S(38), Ui.S(30));
            Controls.Add(c);
            dayChips.Add(c);
            dx += c.Width + Ui.S(6);
        }
        y += Ui.S(42);
        var note = L(Lang.T("要 MultiTool 開住（縮去系統匣都得）先會定時開始；遲咗 2 分鐘內都照開。"), Ui.TextFaint, 8.75f);
        note.MaximumSize = new Size(Ui.S(400), 0);
        note.Location = new Point(x0 + Ui.S(24), y);
        y += note.PreferredHeight + Ui.S(18);
        void SyncSched()
        {
            hh.Enabled = mm.Enabled = schedChip.Checked;
            foreach (var c in dayChips) c.Enabled = schedChip.Checked;
        }
        schedChip.CheckedChanged += SyncSched;
        SyncSched();

        // ---- 開 MultiTool 自動開始 ----
        var autoChip = new ToggleChip(Lang.T("開 MultiTool 時自動開始")) { Checked = sc.AutoStart, Location = new Point(x0, y) };
        Controls.Add(autoChip);
        y += Ui.S(32);
        var autoNote = L(Lang.T("配合「設定 → 開機時啟動」，開機就自動行。"), Ui.TextFaint, 8.75f);
        autoNote.Location = new Point(x0 + Ui.S(24), y);
        y += autoNote.PreferredHeight + Ui.S(18);

        // ---- 通知 ----
        var notifyChip = new ToggleChip(Lang.T("完成 / 出錯時通知")) { Checked = sc.NotifyEnd, Location = new Point(x0, y) };
        Controls.Add(notifyChip);
        y += Ui.S(32);
        var nNote = L(Notifier.DiscordUrl.Length > 0 ? Lang.T("會彈 Windows 通知，同埋發去 Discord。")
                                                   : Lang.T("會彈 Windows 通知。想同時發去 Discord：去「設定 → 通知」填 webhook。"), Ui.TextFaint, 8.75f);
        nNote.MaximumSize = new Size(Ui.S(400), 0);
        nNote.Location = new Point(x0 + Ui.S(24), y);
        y += nNote.PreferredHeight + Ui.S(22);

        var ok = new FancyButton { Text = Lang.T("確定"), Kind = BtnKind.Primary, Font = Ui.F(9.75f), Size = new Size(Ui.S(90), Ui.S(34)) };
        var cancel = new FancyButton { Text = Lang.T("取消"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(90), Ui.S(34)) };
        int w = Ui.S(460);
        cancel.Location = new Point(w - Ui.S(20) - cancel.Width, y);
        ok.Location = new Point(cancel.Left - Ui.S(10) - ok.Width, y);
        Controls.Add(ok);
        Controls.Add(cancel);
        ClientSize = new Size(w, y + ok.Height + Ui.S(18));
        ok.Click += (_, _) =>
        {
            int mask = dayChips.Where(c => c.Checked).Aggregate(0, (a, c) => a | (1 << (int)c.Tag!));
            if (schedChip.Checked && mask == 0)
            {
                MessageBox.Show(this, Lang.T("請最少揀一日。"), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            sc.SchedTime = schedChip.Checked ? $"{(int)hh.Value:00}:{(int)mm.Value:00}" : "";
            sc.SchedDays = schedChip.Checked ? mask : 127;
            sc.AutoStart = autoChip.Checked;
            sc.NotifyEnd = notifyChip.Checked;
            sc.Save();
            DialogResult = DialogResult.OK;
            Close();
        };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { DarkTheme.DarkTitleBar(this); } catch { }
    }

    public static bool Edit(IWin32Window? owner, Script sc)
    {
        using var d = new ScriptAutoDialog(sc);
        return d.ShowDialog(owner) == DialogResult.OK;
    }
}

/// <summary>編輯介面共用：比較符號掣、揀範圍、揀腳本。</summary>
internal static class ScriptUiBits
{
    private static ContextMenuStrip Menu()
    {
        var m = new ContextMenuStrip();
        try { m.Renderer = DarkTheme.MenuRenderer(); } catch { }
        m.BackColor = Ui.CardHi;
        m.ForeColor = Ui.Text;
        return m;
    }

    private static void Show(ContextMenuStrip m, Control anchor)
    {
        m.Closed += (_, _) => { try { if (!anchor.IsDisposed) anchor.BeginInvoke(() => m.Dispose()); } catch { } };
        m.Show(anchor, new Point(0, anchor.Height + Ui.S(2)));
    }

    public static string OpLong(CmpOp o) => o switch
    {
        CmpOp.Gt => Lang.T("> 大過"),
        CmpOp.Ge => Lang.T("≥ 大過或等於"),
        CmpOp.Lt => Lang.T("< 細過"),
        CmpOp.Le => Lang.T("≤ 細過或等於"),
        CmpOp.Eq => Lang.T("= 等於"),
        _ => Lang.T("≠ 唔等於")
    };

    /// <summary>比較符號掣（撳一下揀 &gt; ≥ &lt; ≤ = ≠）。</summary>
    public static FancyButton OpButton(CmpOp cur, Action<CmpOp> set)
    {
        var b = new FancyButton { Text = IfBlock.OpName(cur) + " ▾", Kind = BtnKind.Ghost, Font = Ui.F(10f, FontStyle.Bold), Size = new Size(Ui.S(56), Ui.S(34)) };
        b.Click += (_, _) =>
        {
            var m = Menu();
            foreach (CmpOp o in Enum.GetValues(typeof(CmpOp)))
            {
                var chosen = o;
                var it = new ToolStripMenuItem(OpLong(o)) { Checked = o == cur };
                it.Click += (_, _) => { cur = chosen; b.Text = IfBlock.OpName(chosen) + " ▾"; set(chosen); };
                m.Items.Add(it);
            }
            Show(m, b);
        };
        return b;
    }

    public static string RegionText(int x1, int y1, int x2, int y2) =>
        x1 == x2 || y1 == y2 ? Lang.T("（未揀範圍）") : $"({x1}, {y1}) → ({x2}, {y2})";

    /// <summary>框一個範圍（文字 / 數字辨識用）：回傳腳本座標（左上、右下）。</summary>
    public static void PickRegion(Control owner, Script sc, Action<Point, Point> done, string? prompt = null)
    {
        var r = SnipOverlay.GrabRect(owner.FindForm(), prompt ?? Lang.T("拖一個框，框住要辨識嘅文字 / 數字（框細啲會準啲）　·　Esc / 右鍵取消"));
        if (r == null) return;
        var a = ScriptCoords.FromScreen(sc, r.Value.Location);
        var b = ScriptCoords.FromScreen(sc, new Point(r.Value.Right, r.Value.Bottom));
        if (a == null || b == null) { StepList.NoWindow(owner); return; }
        done(a.Value, b.Value);
    }

    /// <summary>
    /// 搵圖範圍掣：「全畫面」或者「範圍 ✓」，撳開選單揀範圍 / 取消範圍。
    /// get / set 用腳本座標（左上、右下）。
    /// </summary>
    public static FancyButton RegionButton(Script sc, ToolTip tip, Func<(bool On, int X1, int Y1, int X2, int Y2)> get, Action<bool, int, int, int, int> set)
    {
        var b = new FancyButton { Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Size = new Size(Ui.S(104), Ui.S(34)) };
        void Refresh()
        {
            var g = get();
            b.Text = (g.On ? Lang.T("範圍 ✓") : Lang.T("全畫面")) + " ▾";
            tip.SetToolTip(b, g.On ? Lang.T("搵圖範圍：") + RegionText(g.X1, g.Y1, g.X2, g.Y2) : Lang.T("搵圖範圍：成個畫面（揀咗視窗就係成個視窗）"));
        }
        Refresh();
        b.Click += (_, _) =>
        {
            var m = new ContextMenuStrip();
            try { m.Renderer = DarkTheme.MenuRenderer(); } catch { }
            m.BackColor = Ui.CardHi;
            m.ForeColor = Ui.Text;
            var pick = new ToolStripMenuItem(Lang.T("揀範圍…")) { ForeColor = Ui.Text };
            pick.Click += (_, _) => b.BeginInvoke(() => PickRegion(b, sc, (p1, p2) => { set(true, p1.X, p1.Y, p2.X, p2.Y); Refresh(); },
                Lang.T("拖一個框：淨係喺呢個範圍入面搵圖　·　Esc / 右鍵取消")));
            var all = new ToolStripMenuItem(Lang.T("全畫面（唔限範圍）")) { ForeColor = Ui.Text, Checked = !get().On };
            all.Click += (_, _) => { var g = get(); set(false, g.X1, g.Y1, g.X2, g.Y2); Refresh(); };
            m.Items.Add(pick);
            m.Items.Add(all);
            m.Closed += (_, _) => b.BeginInvoke(() => m.Dispose());
            m.Show(b, new Point(0, b.Height + Ui.S(2)));
        };
        return b;
    }

    /// <summary>試辨識：即刻截個範圍睇吓讀到乜（幫手調範圍）。</summary>
    public static async void TestRegion(Control owner, Script sc, int x1, int y1, int x2, int y2)
    {
        try
        {
            var ctx = await Task.Run(() => ScriptRunner.PreviewCtx(sc));
            string text = await Task.Run(() =>
            {
                using var img = ctx.CaptureRegion(x1, y1, x2, y2);
                return img == null ? "" : OcrServer.Recognize(img, CancellationToken.None);
            });
            var n = OcrServer.FirstNumber(text);
            MessageBox.Show(owner.FindForm(), Lang.F("讀到：\n{0}\n\n第一個數字：{1}", text.Length == 0 ? Lang.T("（冇字）") : text,
                n == null ? Lang.T("（冇）") : ScriptVars.Format(n.Value)), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner.FindForm(), Lang.T("辨識失敗：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>揀另一個腳本（行其他腳本用）。</summary>
    public static void PickScript(Control anchor, Script self, string current, Action<string, string> set)
    {
        var m = Menu();
        try
        {
            if (Directory.Exists(AppPaths.Scripts))
                foreach (var d in Directory.GetDirectories(AppPaths.Scripts).OrderBy(x => Directory.GetCreationTimeUtc(x)))
                {
                    string folder = Path.GetFileName(d);
                    if (string.Equals(folder, self.Folder, StringComparison.OrdinalIgnoreCase)) continue;
                    var sc = Script.Load(d);
                    if (sc == null) continue;
                    var it = new ToolStripMenuItem(sc.Name) { Checked = string.Equals(folder, current, StringComparison.OrdinalIgnoreCase) };
                    string name = sc.Name;
                    it.Click += (_, _) => set(folder, name);
                    m.Items.Add(it);
                }
        }
        catch (Exception ex) { AppPaths.LogError("列出腳本", ex); }
        if (m.Items.Count == 0) m.Items.Add(new ToolStripMenuItem(Lang.T("（冇其他腳本）")) { Enabled = false });
        Show(m, anchor);
    }
}

/// <summary>
/// Debug 模式：腳本開咗 Debug，每次運行開一個資料夾 data\debug\&lt;腳本&gt;\&lt;日期時間&gt;，
/// log.txt 記低每一步、每次搵圖 / 條件嘅結果；每次截圖都存做 PNG（最多 20000 張）。
/// </summary>
internal sealed class DebugSession
{
    public readonly string Dir;
    private readonly StreamWriter _log;
    private int _shot;
    private readonly object _lk = new();
    private bool _closed;

    public DebugSession(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(dir);
        _log = new StreamWriter(Path.Combine(dir, "log.txt"), false, new System.Text.UTF8Encoding(true));
    }

    public void Log(string line)
    {
        lock (_lk)
        {
            if (_closed) return;
            try { _log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] {line}"); _log.Flush(); } catch { }
        }
    }

    /// <summary>存一張截圖，回傳檔名（太多就唔再存）。</summary>
    public string Shot(System.Drawing.Bitmap b, string tag)
    {
        int n = Interlocked.Increment(ref _shot);
        if (n > 20000)
        {
            if (n == 20001) Log("截圖超過 20000 張，之後唔再存");
            return "";
        }
        string f = $"{n:00000}_{tag}.png";
        try { b.Save(Path.Combine(Dir, f), System.Drawing.Imaging.ImageFormat.Png); } catch { return ""; }
        return f;
    }

    public void Close()
    {
        lock (_lk)
        {
            if (_closed) return;
            _closed = true;
            try { _log.Dispose(); } catch { }
        }
    }
}

internal static class ScriptDebug
{
    /// <summary>而家呢條運行線嘅 debug（冇開就 null）；跟 async 流程走，IF 框 / 新線程都帶埋。</summary>
    public static readonly AsyncLocal<DebugSession?> Cur = new();

    public static string Root => Path.Combine(AppPaths.Data, "debug");

    public static string FolderOf(Script s) => Path.Combine(Root, Path.GetFileName(s.Dir));

    public static DebugSession Start(Script s) =>
        new(Path.Combine(FolderOf(s), DateTime.Now.ToString("yyyyMMdd-HHmmss")));

    public static void Log(string line) => Cur.Value?.Log(line);
}
