using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Tools;

// =====================================================================
//  影片下載：用 yt-dlp（安裝時下載）將 YouTube 等網站嘅影片存做 MP3 / MP4。
// =====================================================================
internal static class YtDlp
{
    private const string Url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public static string Dir => Path.Combine(AppPaths.Data, "tools", "yt-dlp");
    public static string Exe => Path.Combine(Dir, "yt-dlp.exe");
    public static bool Ready => File.Exists(Exe) && Video.Ffmpeg.Ready;

    /// <summary>安裝：yt-dlp + ffmpeg（轉 MP3 / 合併影片聲音要用；同錄影工具共用）。</summary>
    public static async Task InstallAsync(Action<string, double> progress, CancellationToken ct)
    {
        if (!Video.Ffmpeg.Ready) await Video.Ffmpeg.InstallAsync(progress, ct);
        Directory.CreateDirectory(Dir);
        string tmp = Exe + ".part";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MultiTool");
            using var resp = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    progress(Lang.T("下載 yt-dlp…"), total > 0 ? (double)done / total : -1);
                }
            }
        }
        File.Move(tmp, Exe, true);
        progress(Lang.T("完成"), 1);
    }

    public static void Delete()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
    }

    public static ProcessStartInfo Psi(string args)
    {
        var psi = new ProcessStartInfo(Exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Dir
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        return psi;
    }
}

internal sealed class DownloadPage : ToolPage
{
    private sealed class Job
    {
        public required string Url;
        public required bool Audio;
        public required string Quality;
        public bool Playlist;
        public string Title = "", Status = "";
        public double Progress = -1;
        public string? File;
        public bool Done, Failed;
        public Label? TitleLabel, StatusLabel;
        public FancyButton? OpenBtn;
    }

    private static string Settings_ => Path.Combine(AppPaths.Data, "download.txt");
    private readonly TextBox _url = new();
    private readonly FancyButton _fmt, _qual, _folderBtn, _go;
    private readonly CheckBox _playlist;
    private readonly Label _status, _folderLabel;
    private readonly FlowLayoutPanel _list;
    private readonly List<Job> _jobs = new();
    private readonly System.Windows.Forms.Timer _ui = new() { Interval = 400 };
    private bool _audio = true;
    private string _aq = "0", _vq = "1080";
    private string _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MultiTool");
    private CancellationTokenSource? _cts;
    private Process? _proc;
    private bool _busy;

    public static Control Build() => new DownloadPage();

    private DownloadPage()
    {
        LoadSettings();
        _url.PlaceholderText = Lang.T("貼上影片連結（YouTube、Bilibili、TikTok…），可以一次貼幾條，一行一條");
        _url.Multiline = true;
        _url.Height = Ui.S(60);
        _url.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; Add(); } };
        var paste = TUi.Btn(Lang.T("貼上"), () => { try { if (Clipboard.ContainsText()) _url.Text = Clipboard.GetText().Trim(); } catch { } }, 70);
        _fmt = TUi.Btn("", () => TUi.Pick(_fmt!, new[] { (Lang.T("MP3（淨聲音）"), true), (Lang.T("MP4（影片）"), false) }, _audio,
            v => { _audio = v; SaveSettings(); UpdateLabels(); }), 150);
        _qual = TUi.Btn("", () =>
        {
            if (_audio)
                TUi.Pick(_qual!, new[] { (Lang.T("最好"), "0"), ("320 kbps", "320K"), ("192 kbps", "192K"), ("128 kbps", "128K") }, _aq,
                    v => { _aq = v; SaveSettings(); UpdateLabels(); });
            else
                TUi.Pick(_qual!, new[] { (Lang.T("最好"), "best"), ("2160P (4K)", "2160"), ("1440P (2K)", "1440"), ("1080P", "1080"), ("720P", "720"), ("480P", "480"), ("360P", "360") }, _vq,
                    v => { _vq = v; SaveSettings(); UpdateLabels(); });
        }, 150);
        _playlist = TUi.Check(Lang.T("播放清單：成個下載"), false, _ => { });
        _go = TUi.Btn(Lang.T("⬇ 下載"), Add, 120, BtnKind.Success);
        _status = TUi.L("");
        _folderLabel = TUi.L("", Ui.Text);
        _folderBtn = TUi.Btn(Lang.T("更改…"), () =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _folder };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            _folder = dlg.SelectedPath;
            SaveSettings();
            UpdateLabels();
        }, 80);
        var open = TUi.Btn(Lang.T("開啟資料夾"), () => TUi.Open(_folder, folder: true));
        var update = TUi.Btn(Lang.T("更新 yt-dlp"), () => _ = SelfUpdateAsync());
        Card(Lang.T("影片下載"),
            Lang.T("將網上影片存做 MP3 或者 MP4。只下載你有權使用嘅內容（例如自己嘅影片、允許下載嘅作品）。"),
            TUi.Col(
                new WrapText(Lang.T("支援網站：YouTube（連 Shorts、播放清單、YouTube Music）、Bilibili、Twitch、Vimeo、Dailymotion、X (Twitter)、Reddit、TikTok、") +
                    Lang.T("Facebook / Instagram（公開影片）、SoundCloud、Bandcamp、新聞台網站等 1000 幾個網站。\n") +
                    Lang.T("唔支援：要登入先睇到嘅影片，同有版權保護嘅串流（Netflix、Disney+、Spotify 等）。"),
                    TUi.Btn(Lang.T("完整網站列表"), () =>
                    {
                        try { Process.Start(new ProcessStartInfo("https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md") { UseShellExecute = true }); } catch { }
                    })),
                TUi.Row(new InputHost(_url, Ui.S(560)) { Height = Ui.S(70) }, paste),
                TUi.Row(TUi.L(Lang.T("格式")), _fmt, _qual, _playlist),
                TUi.Row(TUi.L(Lang.T("儲存位置")), _folderLabel, _folderBtn, open),
                TUi.Row(_go, update, _status)));

        _list = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var clear = TUi.Btn(Lang.T("清除已完成"), () => { _jobs.RemoveAll(j => j.Done || j.Failed); BuildRows(); });
        Card(Lang.T("下載清單"), Lang.T("一個一個下載；網站改版下載唔到嘅話，撳「更新 yt-dlp」再試。"), TUi.Col(TUi.Row(clear), _list));
        _ui.Tick += (_, _) => RefreshRows();
        _ui.Start();
        Disposed += (_, _) => { _ui.Stop(); _cts?.Cancel(); };
        UpdateLabels();
        BuildRows();
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(Settings_)) return;
            foreach (var l in File.ReadAllLines(Settings_))
            {
                int eq = l.IndexOf('=');
                if (eq < 0) continue;
                string k = l[..eq], v = l[(eq + 1)..];
                switch (k)
                {
                    case "audio": _audio = v != "0"; break;
                    case "aq": _aq = v; break;
                    case "vq": _vq = v; break;
                    case "folder": if (v.Length > 0) _folder = v; break;
                }
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        try { File.WriteAllLines(Settings_, new[] { "audio=" + (_audio ? 1 : 0), "aq=" + _aq, "vq=" + _vq, "folder=" + _folder }); } catch { }
    }

    private void UpdateLabels()
    {
        _fmt.Text = (_audio ? "MP3" : "MP4") + " ▾";
        _qual.Text = Lang.T("品質：") + (_audio ? (_aq == "0" ? Lang.T("最好") : _aq.Replace("K", " kbps")) : (_vq == "best" ? Lang.T("最好") : _vq + "P")) + " ▾";
        _folderLabel.Text = _folder;
        foreach (var b in new[] { _fmt, _qual }) b.Invalidate();
    }

    private void Add()
    {
        var urls = _url.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)).ToList();
        if (urls.Count == 0) { _status.Text = Lang.T("請貼上 http / https 連結"); return; }
        foreach (var u in urls)
            _jobs.Add(new Job { Url = u, Audio = _audio, Quality = _audio ? _aq : _vq, Playlist = _playlist.Checked, Title = u, Status = Lang.T("等緊…") });
        _url.Clear();
        _status.Text = "";
        BuildRows();
        _ = RunQueueAsync();
    }

    private void BuildRows()
    {
        _list.SuspendLayout();
        foreach (Control c in _list.Controls.Cast<Control>().ToList()) { _list.Controls.Remove(c); c.Dispose(); }
        if (_jobs.Count == 0) _list.Controls.Add(TUi.L(Lang.T("未有下載")));
        foreach (var j0 in _jobs)
        {
            var j = j0;
            j.TitleLabel = new Label
            {
                AutoSize = false, Width = Ui.S(400), Height = Ui.S(34), ForeColor = Ui.Text, BackColor = Ui.Card, Font = Ui.F(9.5f),
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false, Margin = new Padding(0, 0, Ui.S(10), 0)
            };
            j.StatusLabel = new Label
            {
                AutoSize = false, Width = Ui.S(220), Height = Ui.S(34), BackColor = Ui.Card, Font = Ui.F(9.25f),
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false, Margin = new Padding(0, 0, Ui.S(10), 0)
            };
            j.OpenBtn = TUi.Btn(Lang.T("打開"), () => { if (j.File != null) TUi.Reveal(j.File); }, 70);
            var cancel = TUi.Btn("✕", () =>
            {
                if (!j.Done && !j.Failed && _jobs.FirstOrDefault(x => !x.Done && !x.Failed) == j) _cts?.Cancel();
                _jobs.Remove(j);
                BuildRows();
            }, 40);
            var row = TUi.Row(j.TitleLabel, j.StatusLabel, j.OpenBtn, cancel);
            row.WrapContents = false;
            row.Margin = new Padding(0, 0, 0, Ui.S(4));
            _list.Controls.Add(row);
        }
        _list.ResumeLayout(true);
        RefreshRows();
        Relayout();
    }

    private void RefreshRows()
    {
        foreach (var j in _jobs)
        {
            if (j.TitleLabel is not { IsDisposed: false } t || j.StatusLabel is not { } st) continue;
            string title = (j.Audio ? "♪ " : "▶ ") + j.Title;
            if (t.Text != title) t.Text = title;
            string s = j.Status + (j.Progress >= 0 && !j.Done && !j.Failed ? $" {j.Progress:P0}" : "");
            if (st.Text != s) st.Text = s;
            st.ForeColor = j.Failed ? Ui.Danger : j.Done ? Ui.Success : Ui.TextDim;
            if (j.OpenBtn != null) j.OpenBtn.Visible = j.File != null;
        }
    }

    private static readonly Regex Pct = new(@"\[download\]\s+([\d.]+)%", RegexOptions.Compiled);

    private async Task RunQueueAsync()
    {
        if (_busy) return;
        if (!YtDlp.Ready) { _status.Text = Lang.T("未下載 yt-dlp / ffmpeg：請去「工具庫」重新安裝影片下載。"); return; }
        _busy = true;
        try
        {
            while (_jobs.FirstOrDefault(j => !j.Done && !j.Failed) is { } job)
            {
                _cts = new CancellationTokenSource();
                await RunJobAsync(job, _cts.Token);
                if (IsDisposed) return;
            }
        }
        finally { _busy = false; _cts = null; }
    }

    private string Args(Job j)
    {
        var ff = Path.GetDirectoryName(Video.Ffmpeg.Exe)!;
        // --encoding utf-8：唔加會用系統編碼輸出，中文 / 韓文名會變亂碼
        var sb = new StringBuilder("--encoding utf-8 --newline --progress --no-colors --windows-filenames --no-mtime ");
        sb.Append($"--ffmpeg-location \"{ff}\" ");
        sb.Append(j.Playlist ? "--yes-playlist " : "--no-playlist ");
        string tpl = j.Playlist ? "%(playlist_title|)s\\%(playlist_index|)s %(title)s.%(ext)s" : "%(title)s.%(ext)s";
        sb.Append($"-P \"{_folder}\" -o \"{tpl}\" ");
        sb.Append("--print \"before_dl:TITLE %(title)s\" --print \"after_move:FILE %(filepath)s\" ");
        if (j.Audio)
            sb.Append($"-x --audio-format mp3 --audio-quality {j.Quality} --embed-thumbnail --embed-metadata ");
        else
        {
            string h = j.Quality == "best" ? "" : $"[height<={j.Quality}]";
            sb.Append($"-f \"bv*{h}[ext=mp4]+ba[ext=m4a]/bv*{h}+ba/b{h}\" --merge-output-format mp4 --embed-metadata ");
        }
        sb.Append($"\"{j.Url.Replace("\"", "")}\"");
        return sb.ToString();
    }

    private async Task RunJobAsync(Job j, CancellationToken ct)
    {
        j.Status = Lang.T("分析緊…");
        j.Progress = -1;
        var err = new StringBuilder();
        int part = 0;
        try
        {
            Directory.CreateDirectory(_folder);
            using var p = Process.Start(YtDlp.Psi(Args(j)))!;
            _proc = p;
            void Line(string? l, bool stderr)
            {
                if (string.IsNullOrWhiteSpace(l)) return;
                if (l.StartsWith("TITLE ")) { j.Title = l[6..]; part = 0; return; }
                if (l.StartsWith("FILE ")) { j.File = l[5..].Trim(); return; }
                var m = Pct.Match(l);
                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                {
                    if (v < 1 && j.Progress > 0.9) part++; // 影片下完再落聲音
                    j.Progress = v / 100;
                    j.Status = part > 0 ? Lang.T("下載緊聲音…") : Lang.T("下載緊…");
                    return;
                }
                if (l.Contains("[ExtractAudio]")) { j.Status = Lang.T("轉換做 MP3…"); j.Progress = -1; }
                else if (l.Contains("[Merger]")) { j.Status = Lang.T("合併影片同聲音…"); j.Progress = -1; }
                else if (l.Contains("[EmbedThumbnail]")) { j.Status = Lang.T("加入封面…"); j.Progress = -1; }
                if (stderr || l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) lock (err) err.AppendLine(l);
            }
            p.OutputDataReceived += (_, e) => Line(e.Data, false);
            p.ErrorDataReceived += (_, e) => Line(e.Data, true);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using (ct.Register(() => { try { p.Kill(true); } catch { } }))
                await p.WaitForExitAsync();
            ct.ThrowIfCancellationRequested();
            if (p.ExitCode == 0)
            {
                j.Done = true;
                j.Progress = -1;
                j.Status = Lang.T("✓ 完成");
                Toast.Show(Lang.T("下載完成：") + j.Title, Ui.Success);
            }
            else
            {
                j.Failed = true;
                string e;
                lock (err) e = err.ToString().Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.StartsWith("ERROR")) ?? err.ToString().Trim().Split('\n').LastOrDefault() ?? "";
                j.Status = Lang.T("失敗：") + e.Replace("ERROR:", "").Trim();
                AppPaths.LogError("影片下載", new Exception(err.ToString()));
            }
        }
        catch (OperationCanceledException) { j.Failed = true; j.Status = Lang.T("已取消"); }
        catch (Exception ex) { j.Failed = true; j.Status = Lang.T("失敗：") + ex.Message; }
        finally { _proc = null; }
    }

    private async Task SelfUpdateAsync()
    {
        if (!File.Exists(YtDlp.Exe)) { _status.Text = Lang.T("未安裝 yt-dlp"); return; }
        if (_busy) { _status.Text = Lang.T("下載緊，完咗先更新"); return; }
        _status.Text = Lang.T("更新緊 yt-dlp…");
        try
        {
            using var p = Process.Start(YtDlp.Psi("-U"))!;
            string o = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var last = o.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0) ?? "";
            _status.Text = p.ExitCode == 0 ? Lang.T("✓ yt-dlp 已係最新：") + last : Lang.T("更新失敗：") + last;
        }
        catch (Exception ex) { _status.Text = Lang.T("更新失敗：") + ex.Message; }
    }
}
