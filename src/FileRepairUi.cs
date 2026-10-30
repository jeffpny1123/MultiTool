using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Tools;

// =====================================================================
//  檔案修復頁：拖檔案入嚟就自動檢查同修復，修好嘅另存一份（原檔唔郁）。
// =====================================================================
internal sealed class FileRepairPage : ToolPage
{
    private sealed class Job
    {
        public required string Path;
        public string? Reference;
        public string Status = "";
        public double Progress = -1;
        public bool Done, Failed, Busy, NeedRef;
        public string? Output;
        public Label? Title, StatusLabel;
        public ThinBar? Bar;
        public FancyButton? OpenBtn, RefBtn;
    }

    private readonly List<Job> _jobs = new();
    private readonly FlowLayoutPanel _list;
    private readonly FancyButton _outBtn;
    private readonly CheckBox _h264;
    private string _outDir = "";
    private bool _running;
    private CancellationTokenSource? _cts;
    private readonly HashSet<string> _declined = new(); // 今次唔下載嘅工具
    private readonly System.Windows.Forms.Timer _ui = new() { Interval = 250 };

    public static Control Build() => new FileRepairPage();

    private FileRepairPage()
    {
        AllowDrop = true;
        Stack.AllowDrop = true;
        void Enter(object? s, DragEventArgs e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        void Drop(object? s, DragEventArgs e) { if (e.Data?.GetData(DataFormats.FileDrop) is string[] f) Add(f); }
        DragEnter += Enter; DragDrop += Drop; Stack.DragEnter += Enter; Stack.DragDrop += Drop;

        var add = TUi.Btn(Lang.T("+ 加入檔案…"), () =>
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Filter = Lang.T("所有檔案") + "|*.*" };
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK) Add(dlg.FileNames);
        }, 0, BtnKind.Primary);
        var clear = TUi.Btn(Lang.T("清除已完成"), () => { _jobs.RemoveAll(j => (j.Done || j.Failed) && !j.Busy); BuildRows(); });
        var stop = TUi.Btn(Lang.T("■ 取消"), () => _cts?.Cancel());
        _list = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Card(Lang.T("檔案修復"),
            Lang.T("將開唔到嘅檔案拖入嚟就會自動檢查同修復：影片、聲音、相片、ZIP / Word / Excel / PowerPoint、PDF。修好嘅檔案另存一份（名後面加「已修復」），唔會改原本嘅檔案。"),
            TUi.Col(TUi.Row(add, clear, stop), _list));

        _h264 = TUi.Check(Lang.T("影片一律轉做 H.264 / AAC（最兼容，但要重新壓縮，慢啲）"), false, _ => { });
        _outBtn = TUi.Btn("", () =>
        {
            var menu = TUi.Menu(_outBtn!);
            var same = new ToolStripMenuItem(Lang.T("同原本檔案一樣")) { Checked = _outDir.Length == 0, ForeColor = Ui.Text };
            same.Click += (_, _) => { _outDir = ""; UpdateLabels(); };
            var pick = new ToolStripMenuItem(Lang.T("揀資料夾…")) { Checked = _outDir.Length > 0, ForeColor = Ui.Text };
            pick.Click += (_, _) =>
            {
                using var dlg = new FolderBrowserDialog();
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK) { _outDir = dlg.SelectedPath; UpdateLabels(); }
            };
            menu.Items.Add(same); menu.Items.Add(pick);
            menu.Show(_outBtn!, new Point(0, _outBtn!.Height));
        }, 260);
        Card(Lang.T("選項"), null, TUi.Col(TUi.Row(_h264), TUi.Row(TUi.L(Lang.T("儲存位置")), _outBtn)));

        Card(Lang.T("修得到乜？"), null, new WrapText(
            Lang.T("• 影片 / 聲音：重建索引同時間戳、副檔名錯、播放器唔支援嘅格式（轉 H.264）、中間有損壞（重新壓縮，損壞位置可能有花屏）。\n") +
            Lang.T("• 錄影中途斷電 / 當機（MP4、MOV 冇咗索引）：要揀一條同一部機、同一個設定錄嘅正常影片做參考。\n") +
            Lang.T("• 相片：JPEG / PNG 唔完整（冇咗嘅部分用灰色補）、校驗碼錯、副檔名錯；HEIC / WEBP 轉做 PNG。\n") +
            Lang.T("• ZIP / Word / Excel / PowerPoint / EPUB：由殘骸重新砌過，救返入面完好嘅檔案。\n") +
            Lang.T("• PDF：重建損壞嘅索引表。\n") +
            Lang.T("• 部分修復要用免費開源工具（ffmpeg、qpdf、untrunc），第一次用先會問你下載。")));

        UpdateLabels();
        BuildRows();
        _ui.Tick += (_, _) => RefreshRows();
        _ui.Start();
        Disposed += (_, _) => { _ui.Stop(); _cts?.Cancel(); };
    }

    private void UpdateLabels()
    {
        _outBtn.Text = (_outDir.Length == 0 ? Lang.T("同原本檔案一樣") : _outDir) + " ▾";
        _outBtn.Invalidate();
    }

    private void Add(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            if (Directory.Exists(f)) continue;
            if (_jobs.Any(j => j.Path.Equals(f, StringComparison.OrdinalIgnoreCase) && !j.Done && !j.Failed)) continue;
            _jobs.Add(new Job { Path = f, Status = Lang.T("等緊…") });
        }
        BuildRows();
        _ = RunQueueAsync();
    }

    private void BuildRows()
    {
        _list.SuspendLayout();
        foreach (Control c in _list.Controls.Cast<Control>().ToList()) c.Dispose();
        _list.Controls.Clear();
        if (_jobs.Count == 0)
            _list.Controls.Add(TUi.L(Lang.T("未有檔案：撳「+ 加入檔案…」或者直接拖入嚟"), Ui.TextDim));
        foreach (var j0 in _jobs)
        {
            var j = j0;
            j.Title = new Label
            {
                Text = Path.GetFileName(j.Path), AutoSize = false, Width = Ui.S(520), Height = Ui.S(26), ForeColor = Ui.Text, BackColor = Ui.Card,
                Font = Ui.F(9.75f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false
            };
            j.OpenBtn = TUi.Btn(Lang.T("打開"), () => { if (j.Output != null) TUi.Reveal(j.Output); }, 70);
            j.RefBtn = TUi.Btn(Lang.T("揀參考影片…"), () => PickReference(j), 0, BtnKind.Primary);
            var del = TUi.Btn("✕", () => { if (j.Busy) return; _jobs.Remove(j); BuildRows(); }, 40);
            var top = TUi.Row(j.Title, j.OpenBtn, j.RefBtn, del);
            top.WrapContents = false;
            j.StatusLabel = new Label
            {
                AutoSize = true, MaximumSize = new Size(Ui.S(760), 0), BackColor = Ui.Card, Font = Ui.F(9.25f), UseMnemonic = false,
                Margin = new Padding(Ui.S(2), 0, 0, Ui.S(4))
            };
            j.Bar = new ThinBar { Width = Ui.S(520), Margin = new Padding(Ui.S(2), 0, 0, Ui.S(4)) };
            var col = TUi.Col(top, j.StatusLabel, j.Bar);
            col.Margin = new Padding(0, 0, 0, Ui.S(10));
            _list.Controls.Add(col);
        }
        _list.ResumeLayout(true);
        RefreshRows();
        Relayout();
    }

    private void RefreshRows()
    {
        bool relayout = false;
        foreach (var j in _jobs)
        {
            if (j.StatusLabel is not { IsDisposed: false } st) continue;
            if (st.Text != j.Status) { st.Text = j.Status; relayout = true; }
            st.ForeColor = j.Failed ? Ui.Danger : j.Done ? Ui.Success : j.NeedRef ? Ui.Text : Ui.TextDim;
            if (j.Bar != null)
            {
                bool show = j.Busy;
                if (j.Bar.Visible != show) { j.Bar.Visible = show; relayout = true; }
                if (Math.Abs(j.Bar.Value - j.Progress) > 0.001) j.Bar.Value = j.Progress;
            }
            if (j.OpenBtn != null && j.OpenBtn.Visible != (j.Output != null)) { j.OpenBtn.Visible = j.Output != null; relayout = true; }
            if (j.RefBtn != null && j.RefBtn.Visible != (j.NeedRef && !j.Busy)) { j.RefBtn.Visible = j.NeedRef && !j.Busy; relayout = true; }
        }
        if (relayout) { _list.PerformLayout(); Relayout(); }
    }

    private void PickReference(Job j)
    {
        using var dlg = new OpenFileDialog
        {
            Title = Lang.T("揀一條同一部機、同一個設定錄嘅正常影片"),
            Filter = Lang.T("影片") + "|*.mp4;*.mov;*.m4v;*.3gp|" + Lang.T("所有檔案") + "|*.*",
            InitialDirectory = Path.GetDirectoryName(j.Path) ?? ""
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (dlg.FileName.Equals(j.Path, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(FindForm(), Lang.T("參考影片要係另一條可以正常播放嘅影片。"), Program.AppName); return; }
        j.Reference = dlg.FileName;
        j.NeedRef = false;
        j.Failed = false;
        j.Done = false;
        j.Status = Lang.T("等緊…");
        _ = RunQueueAsync();
    }

    private async Task RunQueueAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            while (_jobs.FirstOrDefault(j => !j.Done && !j.Failed && !j.NeedRef && !j.Busy) is { } job)
            {
                _cts = new CancellationTokenSource();
                await RunJobAsync(job, _cts.Token);
                if (IsDisposed) return;
            }
        }
        finally { _running = false; _cts = null; }
    }

    private RepairOptions Options(Job j) => new()
    {
        Ffmpeg = Video.Ffmpeg.Ready ? Video.Ffmpeg.Exe : null,
        Qpdf = File.Exists(RepairTools.QpdfExe) ? RepairTools.QpdfExe : null,
        Untrunc = File.Exists(RepairTools.UntruncExe) ? RepairTools.UntruncExe : null,
        ToH264 = _h264.Checked,
        Reference = j.Reference,
        ImageResave = Resave
    };

    private async Task RunJobAsync(Job j, CancellationToken ct)
    {
        j.Busy = true;
        j.Progress = -1;
        j.Status = Lang.T("檢查緊…");
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var r = await FileRepair.RepairAsync(j.Path, _outDir, Options(j), (t, p) => { j.Status = t; j.Progress = p; }, ct);
                if (r.NeedTool is { } tool)
                {
                    if (!await EnsureToolAsync(j, tool, ct)) { j.Failed = true; j.Status = Lang.F("要先下載 {0} 先可以修復呢種檔案。", RepairTools.Info(tool).Name); return; }
                    continue;
                }
                var notes = string.Join("\n", r.Notes.Select(n => "• " + n));
                if (r.NeedReference)
                {
                    j.NeedRef = true;
                    j.Status = notes + (notes.Length > 0 ? "\n" : "") + Lang.T("撳「揀參考影片…」：揀一條同一部機、同一個設定（解像度、FPS）錄嘅正常影片，用嚟重建索引。");
                    return;
                }
                if (r.Ok)
                {
                    j.Done = true;
                    j.Output = r.Output;
                    j.Status = Lang.T("✓ 已修復：") + Path.GetFileName(r.Output) + (notes.Length > 0 ? "\n" + notes : "");
                    Toast.Show(Lang.T("已修復：") + Path.GetFileName(j.Path), Ui.Success);
                }
                else
                {
                    j.Failed = true;
                    j.Status = (notes.Length > 0 ? notes + "\n" : "") + "✗ " + (r.Error ?? Lang.T("修唔到"));
                }
                return;
            }
            j.Failed = true;
            j.Status = Lang.T("修唔到");
        }
        catch (OperationCanceledException) { j.Failed = true; j.Status = Lang.T("已取消"); }
        catch (Exception ex) { j.Failed = true; j.Status = "✗ " + ex.Message; AppPaths.LogError("檔案修復", ex); }
        finally { j.Busy = false; j.Progress = -1; }
    }

    /// <summary>未有嘅第三方工具：問一次，同意就下載。</summary>
    private async Task<bool> EnsureToolAsync(Job j, string tool, CancellationToken ct)
    {
        if (_declined.Contains(tool)) return false;
        var (name, size) = RepairTools.Info(tool);
        if (MessageBox.Show(FindForm(), Lang.F("修復呢個檔案要用 {0}（免費開源工具，{1}）。而家下載？", name, size), Program.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            _declined.Add(tool);
            return false;
        }
        try
        {
            await RepairTools.InstallAsync(tool, (t, p) => { j.Status = t; j.Progress = p; }, ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppPaths.LogError("下載 " + name, ex);
            j.Status = Lang.T("下載失敗：") + ex.Message;
            return false;
        }
    }

    /// <summary>相片用 Windows（GDI+）讀入再儲存：讀到就係修好。</summary>
    private static bool Resave(string src, string dst)
    {
        try
        {
            using var fs = File.OpenRead(src);
            using var img = Image.FromStream(fs, false, false);
            using var bmp = new Bitmap(img); // 強制解碼晒
            string ext = Path.GetExtension(dst).ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg")
            {
                var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var ps = new EncoderParameters(1);
                ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
                bmp.Save(dst, enc, ps);
            }
            else
            {
                var fmt = ext switch { ".png" => ImageFormat.Png, ".gif" => ImageFormat.Gif, ".bmp" => ImageFormat.Bmp, ".tif" or ".tiff" => ImageFormat.Tiff, _ => ImageFormat.Png };
                bmp.Save(dst, fmt);
            }
            return true;
        }
        catch
        {
            try { if (File.Exists(dst)) File.Delete(dst); } catch { }
            return false;
        }
    }
}
