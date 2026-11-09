using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Tools;

// =====================================================================
//  NAS 管理頁：揀 Synology NAS → 連線 → 檔案（瀏覽、上載、下載、改名、刪除、分享）
//  + 系統狀態（CPU、RAM、硬碟、開關機）
// =====================================================================
internal sealed class NasPage : ToolPage
{
    private readonly List<NasProfile> _list = NasStore.Load();
    private NasProfile? _cur;
    private ICloud? _client;
    // NAS 最頂層係共用資料夾（唔可以喺度上載 / 改名 / 刪除）
    private bool RootLocked => _path == "/";
    private string _path = "/";
    private CancellationTokenSource? _xfer;
    private bool _busy;

    private readonly FancyButton _pick, _connect, _edit;
    private readonly Label _state, _xferLbl;
    private readonly FlowLayoutPanel _crumbs;
    private readonly FancyButton _viewBtn;
    private static bool _gridView = NasStore.GridView;
    private static readonly SemaphoreSlim _thumbGate = new(4);
    private readonly ThinBar _xferBar = new() { Width = Ui.S(360) };
    private readonly NasBrowser _files = new();
    private readonly FancyButton _xferStop;
    private readonly Control _filesBody, _statusBody, _powerRow;
    private readonly Label _sys, _net;
    private readonly NasMeter _cpu = new(), _ram = new();
    private readonly FlowLayoutPanel _vols;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };

    public static Control Build() => new NasPage();

    private NasPage()
    {
        // ---- 連線 ----
        _pick = TUi.Btn("", PickNas, 260);
        var add = TUi.Btn(Lang.T("＋ 加入 NAS"), () => EditNas(null), 0, BtnKind.Primary);
        _edit = TUi.Btn(Lang.T("✎ 設定"), () => { if (_cur != null) EditNas(_cur); });
        _connect = TUi.Btn(Lang.T("連線"), () => _ = ToggleAsync(), 120, BtnKind.Success);
        _state = TUi.L("");
        Card(Lang.T("NAS 管理"),
            Lang.T("連接 Synology NAS：瀏覽同管理檔案、上載下載、睇系統狀態、遙距開關機。用 DSM 帳號登入（支援兩步驗證）；密碼加密儲存喺呢部電腦。"),
            TUi.Col(TUi.Row(_pick, _connect, add, _edit), _state));

        // ---- 檔案 ----
        var up = TUi.Btn("↑", () => { if (_path != "/") _ = OpenAsync(ParentOf(_path)); }, 44);
        var refresh = TUi.Btn("⟳", () => _ = OpenAsync(_path), 44);
        _crumbs = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, WrapContents = true, Margin = new Padding(0, Ui.S(6), 0, 0) };
        SetCrumbs("/");
        var search = new TextBox { PlaceholderText = Lang.T("搜尋呢個資料夾…") };
        search.TextChanged += (_, _) => _files.Filter = search.Text.Trim();
        var searchHost = new InputHost(search, Ui.S(220));
        _viewBtn = TUi.Btn(_gridView ? Lang.T("☰ 清單") : Lang.T("▦ 格狀"), () =>
        {
            _gridView = !_gridView;
            NasStore.GridView = _gridView;
            _files.Grid = _gridView;
            _viewBtn!.Text = _gridView ? Lang.T("☰ 清單") : Lang.T("▦ 格狀");
        }, 96);
        _files.Grid = _gridView;
        _files.ThumbLoader = async e =>
        {
            var c = _client;
            if (c?.Connected != true) return null;
            // MEGA 嘅縮圖要逐張解密，太多會拖慢；只係相片先攞
            await _thumbGate.WaitAsync();
            try
            {
                var b = await c.ThumbAsync(e.Path, "medium");
                if (b == null) return null;
                using var ms = new MemoryStream(b);
                using var im = Image.FromStream(ms);
                return new Bitmap(im);
            }
            catch { return null; }
            finally { _thumbGate.Release(); }
        };
        var mk = TUi.Btn(Lang.T("📁 新資料夾"), () => _ = NewFolderAsync());
        var upl = TUi.Btn(Lang.T("⬆ 上載…"), () =>
        {
            using var d = new OpenFileDialog { Multiselect = true, Filter = Lang.T("所有檔案") + "|*.*" };
            if (d.ShowDialog(FindForm()) == DialogResult.OK) _ = UploadAsync(d.FileNames);
        }, 0, BtnKind.Primary);
        var dl = TUi.Btn(Lang.T("⬇ 下載"), () => _ = DownloadAsync(_files.Selected));
        var ren = TUi.Btn(Lang.T("✎ 改名"), () => _ = RenameAsync());
        var del = TUi.Btn(Lang.T("🗑 刪除"), () => _ = DeleteAsync());
        var share = TUi.Btn(Lang.T("🔗 分享連結"), () => _ = ShareAsync());
        _xferStop = TUi.Btn(Lang.T("■ 取消"), () => _xfer?.Cancel());
        _xferStop.Visible = false;
        _xferLbl = TUi.L("");
        _xferBar.Margin = new Padding(0, Ui.S(14), Ui.S(10), 0);
        var listHost = new FixedPanel(Ui.S(520)) { BackColor = Ui.Input, Padding = new Padding(1), Margin = new Padding(0, 0, 0, Ui.S(8)) };
        _files.Dock = DockStyle.Fill;
        listHost.Controls.Add(_files);
        _filesBody = TUi.Col(TUi.Row(up, refresh, _crumbs), TUi.Row(mk, upl, dl, ren, del, share, searchHost, _viewBtn), listHost, TUi.Row(_xferBar, _xferLbl, _xferStop));
        Card(Lang.T("檔案"), Lang.T("雙擊資料夾開入去，雙擊檔案下載並開啟；可以直接將檔案或者資料夾拖入嚟上載。右鍵有更多選項。"), _filesBody);
        // 清單闊度跟張卡（FlowLayoutPanel 唔會自動拉闊入面嘅嘢）
        void FitList()
        {
            int w = _filesBody.ClientSize.Width - Ui.S(4);
            if (w > Ui.S(200) && listHost.Width != w) { listHost.Width = w; _files.Invalidate(); }
        }
        _filesBody.Resize += (_, _) => FitList();
        _filesBody.Parent!.Resize += (_, _) => FitList();
        FitList();
        _files.Open += e => { if (e.IsDir) _ = OpenAsync(e.Path); else _ = OpenFileAsync(e); };
        _files.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) FileMenu(e.Location); };
        _files.AllowDrop = true;
        _files.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true && _client?.Connected == true && !RootLocked ? DragDropEffects.Copy : DragDropEffects.None;
        _files.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] f) _ = UploadAsync(f); };
        _files.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) _ = DeleteAsync();
            else if (e.KeyCode == Keys.F2) _ = RenameAsync();
            else if (e.KeyCode == Keys.F5) _ = OpenAsync(_path);
            else if (e.KeyCode == Keys.Back && _path != "/") _ = OpenAsync(ParentOf(_path));
        };

        // ---- 狀態 ----
        _sys = TUi.L("", Ui.Text);
        _cpu.Text = "CPU";
        _ram.Text = "RAM";
        _net = TUi.L("");
        _sys.Margin = new Padding(0, 0, 0, Ui.S(8));
        _net.Margin = new Padding(0, Ui.S(2), 0, Ui.S(6));
        _vols = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var sref = TUi.Btn("⟳ " + Lang.T("更新"), () => _ = StatusAsync());
        var wake = TUi.Btn(Lang.T("⏻ 開機（Wake-on-LAN）"), Wake);
        var reboot = TUi.Btn(Lang.T("↻ 重新啟動"), () => _ = PowerAsync(false));
        var off = TUi.Btn(Lang.T("⏼ 關機"), () => _ = PowerAsync(true), 0, BtnKind.Danger);
        _powerRow = TUi.Row(sref, wake, reboot, off);
        _statusBody = TUi.Col(_sys, _cpu, _ram, _net, _vols, _powerRow);
        Card(Lang.T("系統狀態"), NasStatusHint, _statusBody);

        _timer.Tick += (_, _) => { if (Visible && _client is NasClient { Connected: true } && !_busy) _ = StatusAsync(); };
        _timer.Start();
        Disposed += (_, _) => { _timer.Stop(); _xfer?.Cancel(); _client?.Dispose(); };

        // 開程式揀邊個帳號：連過嘅 NAS 優先（上次用嗰部最先），然後上次用嘅帳號，再唔係就第一個
        string last = NasStore.LastId;
        _cur = _list.Where(x => x.LastOk).OrderBy(x => x.Id == last ? 0 : 1).FirstOrDefault()
               ?? _list.FirstOrDefault(x => x.Id == last) ?? _list.FirstOrDefault();
        UpdateTop();
        ClearStatus();
        // 有紀錄就自動連一次（要確認證書 / 輸入密碼或者驗證碼就唔彈窗，留返俾用家撳「連線」）
        if (_cur != null) _ = ConnectAsync(silent: true);
    }

    // ================= 連線 =================

    private static string NasStatusHint => Lang.T("睇系統狀態同開關機要用管理員帳號。關咗機嘅 NAS 可以用「開機」喚醒（要 NAS 開咗 Wake-on-LAN，而且同一個網絡）。");

    private static string Icon(NasProfile p) => "🗄 ";

    private void UpdateTop()
    {
        _pick.Text = (_cur != null ? Icon(_cur) + _cur.Name : Lang.T("（未加入帳號）")) + "  ▾";
        _pick.Invalidate();
        bool on = _client?.Connected == true;
        _connect.Text = _busy ? Lang.T("連線中…") : on ? Lang.T("中斷連線") : Lang.T("連線");
        _connect.Kind = on ? BtnKind.Ghost : BtnKind.Success;
        _connect.Enabled = _cur != null && !_busy;
        _connect.Invalidate();
        _edit.Enabled = _cur != null;
        _filesBody.Enabled = on;
        if (!on) { _files.SetItems(Array.Empty<NasEntry>()); SetCrumbs("/"); }
    }

    private void SetState(string text, Color? c = null)
    {
        _state.Text = text;
        _state.ForeColor = c ?? Ui.TextDim;
        Relayout();
    }

    private void PickNas()
    {
        if (_busy) { Toast.Show(Lang.T("連線緊，請稍等"), Ui.TextDim, 2000); return; }
        if (_list.Count == 0) { EditNas(null); return; }
        TUi.Pick(_pick, _list.Select(p => (Icon(p) + p.Name + "  (" + p.Host + ")", p.Id)), _cur?.Id ?? "", id =>
        {
            var p = _list.FirstOrDefault(x => x.Id == id);
            if (p == null || p == _cur) return;
            Disconnect();
            _cur = p;
            UpdateTop();
            _ = ConnectAsync();
        });
    }

    private void EditNas(NasProfile? p)
    {
        using var f = new NasEditForm(p);
        var r = f.ShowDialog(FindForm());
        if (r == DialogResult.Abort && p != null)
        {
            if (MessageBox.Show(FindForm(), Lang.F("刪除「{0}」嘅連線設定？（唔會影響上面嘅檔案）", p.Name), Program.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (p == _cur) Disconnect();
            _list.Remove(p);
            NasStore.Save(_list);
            _cur = _list.FirstOrDefault();
            UpdateTop();
            return;
        }
        if (r != DialogResult.OK) return;
        if (p == null) _list.Add(f.Result);
        if (p == null || p == _cur) { Disconnect(); _cur = p ?? f.Result; }
        NasStore.Save(_list);
        UpdateTop();
        _ = ConnectAsync();
    }

    private async Task ToggleAsync()
    {
        if (_busy) return;
        if (_client?.Connected == true) { Disconnect(); return; }
        await ConnectAsync();
    }

    private void Disconnect()
    {
        _xfer?.Cancel();
        var c = _client;
        _client = null;
        if (c != null) _ = Task.Run(async () => { await c.LogoutAsync(); c.Dispose(); });
        SetState(Lang.T("未連線"));
        ClearStatus();
        UpdateTop();
    }

    private async Task ConnectAsync(bool silent = false)
    {
        if (_cur == null || _busy) return;
        var p = _cur;
        _busy = true;
        UpdateTop();
        SetState(Lang.F("連線緊 {0}…", p.BaseUrl));
        var c = new NasClient(p);
        string? otp = null;
        try
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    await c.LoginAsync(otp);
                    break;
                }
                catch (NasCertException) when (silent)
                {
                    SetState(Lang.T("自動連線停咗：要確認 NAS 嘅證書，請撳「連線」"), Ui.TextDim);
                    c.Dispose();
                    return;
                }
                catch (NasException ne) when (silent && ne.Code is 403 or 404)
                {
                    SetState(Lang.T("自動連線停咗：要輸入兩步驗證碼，請撳「連線」"), Ui.TextDim);
                    c.Dispose();
                    return;
                }
                catch (NasCertException ce)
                {
                    var q = MessageBox.Show(FindForm(),
                        Lang.F("NAS 用緊一張電腦唔認得嘅 HTTPS 證書（通常係 NAS 自己簽發嘅，屬正常）。\n\n{0}\n指紋：{1}\n\n確認係你自己部 NAS 先好信任。信任呢張證書？", ce.Subject, ce.Thumb),
                        Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (q != DialogResult.Yes) { SetState(Lang.T("已取消：未信任證書"), Ui.Danger); return; }
                    c.Dispose();
                    p.CertThumb = ce.Thumb;
                    NasStore.Save(_list);
                    c = new NasClient(p);
                }
                catch (NasException ne) when (ne.Code is 403 or 404)
                {
                    otp = Mail.AskText.Show(FindForm(), Lang.T("兩步驗證"),
                        (ne.Code == 404 ? Lang.T("驗證碼唔啱，請再輸入一次。\n") : "") + Lang.T("請輸入驗證應用程式顯示嘅 6 位數字（之後呢部電腦會被記住，唔使再輸）："), "");
                    if (string.IsNullOrWhiteSpace(otp)) { SetState(Lang.T("已取消"), Ui.Danger); return; }
                }
            }
            if (!c.Connected) { SetState(Lang.T("登入失敗"), Ui.Danger); return; }
            _client = c;
            p.LastOk = true;
            NasStore.LastId = p.Id;
            if (p.Mac.Length == 0 && await Task.Run(() => NasClient.MacOf(p.Host)) is { } mac) p.Mac = mac;
            NasStore.Save(_list);
            SetState(Lang.F("已連線：{0}（{1}）", p.Name, p.User), Ui.Success);
            _busy = false;
            UpdateTop();
            await OpenAsync("/");
            await StatusAsync();
        }
        catch (Exception ex)
        {
            c.Dispose();
            SetState(Lang.T("連唔到：") + Message(ex), Ui.Danger);
        }
        finally
        {
            _busy = false;
            UpdateTop();
        }
    }

    private static string Message(Exception ex) => ex switch
    {
        NasException n => n.Message,
        TaskCanceledException => Lang.T("逾時或者已取消"),
        System.Net.Http.HttpRequestException h => Lang.T("網絡連唔到（請檢查地址、端口、HTTPS 設定，同埋 NAS 有冇開機）") + "\n" + h.Message,
        _ => ex.Message
    };

    private void Fail(Exception ex)
    {
        AppPaths.LogError("NAS 管理", ex); // 記低方便查
        MessageBox.Show(FindForm(), Message(ex), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ================= 檔案 =================

    private static string ParentOf(string path)
    {
        int i = path.TrimEnd('/').LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    private static string Join(string folder, string name) => (folder.TrimEnd('/') + "/" + name).Replace("//", "/");

    /// <summary>頂部路徑：共用資料夾 › home › 相片，每一節都撳得。</summary>
    private void SetCrumbs(string path)
    {
        _crumbs.SuspendLayout();
        foreach (Control c in _crumbs.Controls.Cast<Control>().ToList()) c.Dispose();
        _crumbs.Controls.Clear();
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        void Add(string text, string target, bool last)
        {
            var l = Ui.Text_(text, last ? Ui.Text : Ui.TextDim, Ui.F(11f, last ? FontStyle.Bold : FontStyle.Regular), new Padding(0, 0, Ui.S(2), 0));
            l.UseMnemonic = false;
            if (!last)
            {
                l.Cursor = Cursors.Hand;
                l.Click += (_, _) => _ = OpenAsync(target);
                l.MouseEnter += (_, _) => l.ForeColor = Ui.Accent;
                l.MouseLeave += (_, _) => l.ForeColor = Ui.TextDim;
            }
            _crumbs.Controls.Add(l);
            if (!last) _crumbs.Controls.Add(Ui.Text_("›", Ui.TextFaint, Ui.F(11f), new Padding(0, 0, Ui.S(2), 0)));
        }
        Add(Lang.T("共用資料夾"), "/", parts.Length == 0);
        string acc = "";
        for (int i = 0; i < parts.Length; i++) { acc += "/" + parts[i]; Add(parts[i], acc, i == parts.Length - 1); }
        _crumbs.ResumeLayout(true);
    }

    private async Task OpenAsync(string path)
    {
        if (_client?.Connected != true) return;
        try
        {
            _files.Loading = true;
            var items = await _client.ListAsync(path);
            _path = path;
            SetCrumbs(path);
            _files.SetItems(items);
            Relayout();
        }
        catch (Exception ex) { Fail(ex); }
        finally { _files.Loading = false; }
    }

    private async Task NewFolderAsync()
    {
        if (_client?.Connected != true) return;
        if (RootLocked) { MessageBox.Show(FindForm(), Lang.T("共用資料夾要喺 DSM 控制台建立；請先開入一個共用資料夾。"), Program.AppName); return; }
        var name = Mail.AskText.Show(FindForm(), Lang.T("新資料夾"), Lang.T("資料夾名稱："), Lang.T("新資料夾"));
        if (string.IsNullOrWhiteSpace(name)) return;
        try { await _client.CreateFolderAsync(_path, name.Trim()); await OpenAsync(_path); }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task RenameAsync()
    {
        var e = _files.Selected.FirstOrDefault();
        if (_client?.Connected != true || e == null || RootLocked) return;
        var name = Mail.AskText.Show(FindForm(), Lang.T("改名"), Lang.T("新名稱："), e.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == e.Name) return;
        try { await _client.RenameAsync(e.Path, name.Trim()); await OpenAsync(_path); }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task DeleteAsync()
    {
        var sel = _files.Selected;
        if (_client?.Connected != true || sel.Count == 0 || RootLocked) return;
        string what = sel.Count == 1 ? "「" + sel[0].Name + "」" : Lang.F("{0} 個項目", sel.Count);
        string q = Lang.F("刪除 NAS 上面嘅 {0}？\n（如果共用資料夾開咗資源回收筒，可以喺 DSM 還原）", what);
        if (MessageBox.Show(FindForm(), q, Program.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            SetXfer(Lang.T("刪除緊…"), -1, true);
            await _client.DeleteAsync(sel.Select(x => x.Path));
            SetXfer(Lang.T("已刪除"), -2, false);
            await OpenAsync(_path);
        }
        catch (Exception ex) { SetXfer("", -2, false); Fail(ex); }
    }

    private async Task ShareAsync()
    {
        var e = _files.Selected.FirstOrDefault();
        if (_client?.Connected != true || e == null) return;
        try
        {
            string url = await _client.ShareAsync(e.Path);
            try { Clipboard.SetText(url); } catch { }
            MessageBox.Show(FindForm(), Lang.F("已複製分享連結：\n{0}\n\n可以喺 DSM「File Station → 共用連結管理員」設定到期日或者刪除。", url), Program.AppName);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void FileMenu(Point at)
    {
        if (_client?.Connected != true) return;
        _files.SelectForMenu(at);
        var sel = _files.Selected;
        var m = TUi.Menu(this);
        if (sel.Count == 1)
            m.Items.Add(sel[0].IsDir ? Lang.T("開啟") : Lang.T("開啟（下載後開）"), null, (_, _) => { if (sel[0].IsDir) _ = OpenAsync(sel[0].Path); else _ = OpenFileAsync(sel[0]); });
        if (sel.Count > 0)
        {
            m.Items.Add(Lang.T("下載…"), null, (_, _) => _ = DownloadAsync(sel));
            if (!RootLocked)
            {
                if (sel.Count == 1) m.Items.Add(Lang.T("改名…"), null, (_, _) => _ = RenameAsync());
                m.Items.Add(Lang.T("刪除…"), null, (_, _) => _ = DeleteAsync());
            }
            if (sel.Count == 1) m.Items.Add(Lang.T("建立分享連結"), null, (_, _) => _ = ShareAsync());
            m.Items.Add(Lang.T("複製路徑"), null, (_, _) => { try { Clipboard.SetText(string.Join("\n", sel.Select(x => x.Path))); } catch { } });
            m.Items.Add(new ToolStripSeparator());
        }
        if (!RootLocked)
        {
            m.Items.Add(Lang.T("新資料夾…"), null, (_, _) => _ = NewFolderAsync());
            m.Items.Add(Lang.T("上載檔案…"), null, (_, _) =>
            {
                using var d = new OpenFileDialog { Multiselect = true, Filter = Lang.T("所有檔案") + "|*.*" };
                if (d.ShowDialog(FindForm()) == DialogResult.OK) _ = UploadAsync(d.FileNames);
            });
        }
        m.Items.Add(Lang.T("重新整理"), null, (_, _) => _ = OpenAsync(_path));
        m.Show(_files, at);
    }

    // ---------- 傳送 ----------

    private long _lastUi;

    private void SetXfer(string text, double v, bool running)
    {
        void Do()
        {
            _xferLbl.Text = text;
            _xferBar.Value = v < -1.5 ? -1 : v < 0 ? 0 : v;
            _xferStop.Visible = running;
        }
        if (InvokeRequired) BeginInvoke(Do); else Do();
    }

    private Action<long, long> Progress(string label) => (done, total) =>
    {
        long now = Environment.TickCount64;
        if (now - _lastUi < 150 && done != total) return;
        _lastUi = now;
        SetXfer(label + "  " + Fmt(done) + (total > 0 ? " / " + Fmt(total) : ""), total > 0 ? (double)done / total : 0, true);
    };

    internal static string Fmt(long b) =>
        b < 1024 ? b + " B" : b < 1L << 20 ? (b / 1024.0).ToString("0.#") + " KB" : b < 1L << 30 ? (b / 1048576.0).ToString("0.#") + " MB" : (b / 1073741824.0).ToString("0.##") + " GB";

    private async Task UploadAsync(IEnumerable<string> paths)
    {
        if (_client?.Connected != true) return;
        if (RootLocked) { MessageBox.Show(FindForm(), Lang.T("請先開入一個共用資料夾先上載。"), Program.AppName); return; }
        if (_xfer != null) { MessageBox.Show(FindForm(), Lang.T("等而家嘅傳送完成先。"), Program.AppName); return; }
        // 資料夾：連入面所有檔案一齊上載，保持結構
        var jobs = new List<(string File, string Folder)>();
        foreach (var p in paths)
        {
            if (File.Exists(p)) jobs.Add((p, _path));
            else if (Directory.Exists(p))
            {
                string root = Path.GetDirectoryName(p.TrimEnd('\\'))!;
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    jobs.Add((f, Join(_path, Path.GetDirectoryName(Path.GetRelativePath(root, f))!.Replace('\\', '/'))));
            }
        }
        if (jobs.Count == 0) return;
        _xfer = new CancellationTokenSource();
        bool? overwriteAll = null;
        int ok = 0;
        try
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                var (file, folder) = jobs[i];
                string label = Lang.F("上載 {0}/{1}：{2}", i + 1, jobs.Count, Path.GetFileName(file));
                try { await _client.UploadAsync(file, folder, overwriteAll == true, Progress(label), _xfer.Token); ok++; }
                catch (NasException ne) when (ne.Code == 414 && overwriteAll == null)
                {
                    var r = MessageBox.Show(FindForm(), Lang.F("NAS 已經有「{0}」。覆蓋？\n（「是」= 之後全部覆蓋，「否」= 全部略過）", Path.GetFileName(file)),
                        Program.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Cancel) break;
                    overwriteAll = r == DialogResult.Yes;
                    if (overwriteAll == true) { await _client.UploadAsync(file, folder, true, Progress(label), _xfer.Token); ok++; }
                }
                catch (NasException ne) when (ne.Code == 414) { } // 略過已存在
            }
            SetXfer(Lang.F("上載完成：{0} 個檔案", ok), -2, false);
        }
        catch (OperationCanceledException) { SetXfer(Lang.T("已取消"), -2, false); }
        catch (Exception ex) { SetXfer("", -2, false); Fail(ex); }
        finally { _xfer = null; }
        await OpenAsync(_path);
    }

    private async Task DownloadAsync(IReadOnlyList<NasEntry> sel)
    {
        if (_client?.Connected != true || sel.Count == 0) return;
        if (_xfer != null) { MessageBox.Show(FindForm(), Lang.T("等而家嘅傳送完成先。"), Program.AppName); return; }
        var targets = new List<(NasEntry E, string Dest)>();
        if (sel.Count == 1)
        {
            var e = sel[0];
            using var d = new SaveFileDialog { FileName = e.IsDir ? e.Name + ".zip" : e.Name, Filter = Lang.T("所有檔案") + "|*.*" };
            if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
            targets.Add((e, d.FileName));
        }
        else
        {
            using var d = new FolderBrowserDialog { Description = Lang.T("下載去邊個資料夾？"), UseDescriptionForTitle = true };
            if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
            foreach (var e in sel) targets.Add((e, TUi.Unique(Path.Combine(d.SelectedPath, e.IsDir ? e.Name + ".zip" : e.Name))));
        }
        await RunDownloads(targets, open: false);
    }

    /// <summary>雙擊檔案：下載去暫存資料夾再開。</summary>
    private async Task OpenFileAsync(NasEntry e)
    {
        if (_xfer != null) return;
        string dir = Path.Combine(Path.GetTempPath(), Program.FileName + "-cloud");
        Directory.CreateDirectory(dir);
        await RunDownloads(new List<(NasEntry, string)> { (e, Path.Combine(dir, e.Name)) }, open: true);
    }

    private async Task RunDownloads(List<(NasEntry E, string Dest)> targets, bool open)
    {
        if (_client == null) return;
        _xfer = new CancellationTokenSource();
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var (e, dest) = targets[i];
                await _client.DownloadAsync(e.Path, e.IsDir, dest, Progress(Lang.F("下載 {0}/{1}：{2}", i + 1, targets.Count, e.Name)), _xfer.Token);
            }
            SetXfer(Lang.F("下載完成：{0} 個", targets.Count), -2, false);
            if (open) TUi.Open(targets[0].Dest);
            else if (targets.Count > 0) TUi.Reveal(targets[0].Dest);
        }
        catch (OperationCanceledException) { SetXfer(Lang.T("已取消"), -2, false); }
        catch (Exception ex) { SetXfer("", -2, false); Fail(ex); }
        finally { _xfer = null; }
    }

    // ================= 狀態 =================

    private void ClearStatus()
    {
        _sys.Text = Lang.T("未連線");
        _cpu.Value = _ram.Value = -1;
        _cpu.Text = "CPU";
        _ram.Text = "RAM";
        _net.Text = "";
        foreach (Control c in _vols.Controls.Cast<Control>().ToList()) c.Dispose();
        _vols.Controls.Clear();
        Relayout();
    }

    private async Task StatusAsync()
    {
        if (_client is not NasClient nc || !nc.Connected) return;
        NasStatus s;
        try { s = await nc.StatusAsync(); }
        catch { return; }
        if (IsDisposed) return;
        var parts = new List<string>();
        if (s.Model != null) parts.Add(s.Model);
        if (s.Dsm != null) parts.Add(s.Dsm);
        if (s.Temp != null) parts.Add(Lang.F("系統溫度 {0}°C", s.Temp));
        if (s.Uptime != null) parts.Add(Lang.T("運行時間 ") + s.Uptime);
        _sys.Text = parts.Count > 0 ? string.Join("  ·  ", parts) : Lang.T("讀唔到系統資料（可能要用管理員帳號）");
        _cpu.Value = s.Cpu is { } c ? c / 100.0 : -1;
        _cpu.Text = s.Cpu is { } c2 ? $"CPU {c2}%" : "CPU –";
        _ram.Value = s.Ram is { } r ? r / 100.0 : -1;
        _ram.Text = s.Ram is { } r2 ? $"RAM {r2}%" + (s.RamTotal is { } t ? " / " + Fmt(t) : "") : "RAM –";
        _net.Text = s.Rx != null ? Lang.F("網絡：⬇ {0}/s   ⬆ {1}/s", Fmt(s.Rx ?? 0), Fmt(s.Tx ?? 0)) : "";
        _vols.SuspendLayout();
        foreach (Control x in _vols.Controls.Cast<Control>().ToList()) x.Dispose();
        _vols.Controls.Clear();
        foreach (var v in s.Volumes)
        {
            string st = v.Status is "normal" or "" ? "" : "  ⚠ " + v.Status;
            _vols.Controls.Add(new NasMeter
            {
                Value = v.Total > 0 ? (double)v.Used / v.Total : -1,
                Text = $"{v.Name}  {Fmt(v.Used)} / {Fmt(v.Total)}{st}",
                TextColor = v.Status is "normal" or "" ? Ui.TextDim : Ui.Danger,
            });
        }
        foreach (var d in s.Disks)
        {
            bool bad = d.Status is not ("normal" or "" or "initialized");
            string line = "💽 " + d.Name + (d.Model.Length > 0 ? "  " + d.Model : "") + (d.Temp != null ? Lang.F("  {0}°C", d.Temp) : "") + (bad ? "  ⚠ " + d.Status : "");
            var l = TUi.L(line, bad ? Ui.Danger : Ui.TextDim);
            l.Margin = new Padding(0, Ui.S(4), 0, Ui.S(2));
            _vols.Controls.Add(l);
        }
        _vols.ResumeLayout(true);
        Relayout();
    }

    private void Wake()
    {
        if (_cur == null) return;
        string mac = _cur.Mac;
        if (mac.Length == 0)
        {
            mac = Mail.AskText.Show(FindForm(), Lang.T("開機（Wake-on-LAN）"), Lang.T("NAS 嘅 MAC 地址（DSM「控制台 → 網絡 → 網絡介面」睇到，例如 00:11:32:AA:BB:CC）："), "") ?? "";
            if (mac.Trim().Length == 0) return;
            _cur.Mac = mac.Trim();
            NasStore.Save(_list);
        }
        try
        {
            NasClient.Wake(mac);
            Toast.Show(Lang.T("已發出開機訊號，NAS 大約一兩分鐘後開好"), Ui.Success, 3000);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task PowerAsync(bool shutdown)
    {
        if (_client is not NasClient { Connected: true } || _cur == null) return;
        if (MessageBox.Show(FindForm(), shutdown ? Lang.F("將「{0}」關機？其他人正在用嘅連線會中斷。", _cur.Name) : Lang.F("將「{0}」重新啟動？其他人正在用嘅連線會中斷。", _cur.Name),
                Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            if (_client is not NasClient nc) return;
            if (shutdown) await nc.ShutdownAsync(); else await nc.RebootAsync();
            Toast.Show(shutdown ? Lang.T("NAS 關機中") : Lang.T("NAS 重新啟動中"), Ui.Success, 3000);
            Disconnect();
        }
        catch (Exception ex) { Fail(ex); }
    }
}

/// <summary>固定高度嘅格（卡入面用）。</summary>
internal sealed class FixedPanel : Panel
{
    private readonly int _h;
    public FixedPanel(int h) { _h = h; Height = h; }
    public override Size GetPreferredSize(Size proposed) => new(Math.Max(Width, Ui.S(200)), Height > 0 ? Height : _h);
}

/// <summary>一條橫 bar 加右邊一行字，畫喺同一個控制項入面，保證對齊。</summary>
internal sealed class NasMeter : Control
{
    private double _v = -1;
    private Color _tc = Ui.TextDim;

    public NasMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Ui.F(9.75f);
        Size = new Size(Ui.S(640), Ui.S(26));
        Margin = new Padding(0, 0, 0, Ui.S(4));
    }

    public double Value { get => _v; set { _v = value; Invalidate(); } }
    public Color TextColor { get => _tc; set { _tc = value; Invalidate(); } }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Clear(g, this, Parent?.BackColor ?? Ui.Bg);
        Ui.Smooth(g);
        int bw = Ui.S(260), bh = Ui.S(6);
        var r = new RectangleF(0, (Height - bh) / 2f, bw, bh);
        using (var p = Ui.Round(r, bh / 2f)) using (var b = new SolidBrush(Ui.Input)) g.FillPath(b, p);
        if (_v >= 0)
        {
            float w = (float)(Math.Clamp(_v, 0, 1) * bw);
            if (w >= 2)
            {
                var fr = new RectangleF(r.X, r.Y, w, bh);
                using var p = Ui.Round(fr, bh / 2f);
                using var b = _v >= 0.9 ? new SolidBrush(Ui.Danger) : Ui.AccentBrush(fr);
                g.FillPath(b, p);
            }
        }
        var tr = new Rectangle(bw + Ui.S(14), 0, Width - bw - Ui.S(14), Height);
        TextRenderer.DrawText(g, Text, Font, tr, _tc, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}


/// <summary>加入 / 修改 NAS 連線設定。DialogResult.Abort = 刪除。</summary>
internal sealed class NasEditForm : Form
{
    private readonly TextBox _name, _host, _port, _user, _pass, _mac;
    private readonly CheckBox _https;
    private readonly NasProfile? _orig;
    public NasProfile Result { get; private set; } = new();

    public NasEditForm(NasProfile? p)
    {
        _orig = p;
        Text = p == null ? Lang.T("加入 NAS") : Lang.T("NAS 設定");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.F(9.75f);
        try { DarkTheme.DarkTitleBar(this); } catch { }
        int x = Ui.S(16), w = Ui.S(400), y = Ui.S(14);
        TextBox Field(string label, string value, bool password = false, int width = 0)
        {
            var l = Ui.Text_(label, Ui.TextDim, Ui.F(9.25f), new Padding(0));
            l.Location = new Point(x, y);
            Controls.Add(l);
            y += Ui.S(22);
            var tb = new TextBox
            {
                Text = value, BackColor = Ui.Input, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle, Font = Ui.F(10f),
                Bounds = new Rectangle(x, y, width > 0 ? width : w, Ui.S(28)), UseSystemPasswordChar = password
            };
            Controls.Add(tb);
            y += Ui.S(38);
            return tb;
        }
        _name = Field(Lang.T("名稱（自己認得就得）"), p?.Name ?? "Synology");
        _host = Field(Lang.T("地址（IP 或者網域，例如 192.168.1.20、mynas.synology.me）"), p?.Host ?? "");
        _port = Field(Lang.T("端口（HTTPS 預設 5001，HTTP 預設 5000）"), (p?.Port ?? 5001).ToString(), false, Ui.S(120));
        y -= Ui.S(38);
        _https = new CheckBox { Text = Lang.T("用 HTTPS（建議）"), Checked = p?.Https ?? true, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, Location = new Point(x + Ui.S(140), y + Ui.S(3)) };
        _https.CheckedChanged += (_, _) =>
        {
            if (_port.Text.Trim() is "5000" or "5001") _port.Text = _https.Checked ? "5001" : "5000";
        };
        Controls.Add(_https);
        y += Ui.S(38);
        _user = Field(Lang.T("DSM 帳號"), p?.User ?? "");
        _pass = Field(p == null ? Lang.T("密碼") : Lang.T("密碼（留空 = 唔改）"), "", true);
        _mac = Field(Lang.T("MAC 地址（Wake-on-LAN 用，可以留空：同一網絡連線成功後會自動填）"), p?.Mac ?? "");
        y += Ui.S(6);
        var ok = new FancyButton { Text = Lang.T("儲存並連線"), Kind = BtnKind.Primary, Font = Ui.F(9.75f), Size = new Size(Ui.S(130), Ui.S(34)) };
        var cancel = new FancyButton { Text = Lang.T("取消"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(84), Ui.S(34)) };
        cancel.Location = new Point(x + w - cancel.Width, y);
        ok.Location = new Point(cancel.Left - Ui.S(10) - ok.Width, y);
        ok.Click += (_, _) => Save();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(ok);
        Controls.Add(cancel);
        if (p != null)
        {
            var del = new FancyButton { Text = Lang.T("🗑 刪除"), Kind = BtnKind.Danger, Font = Ui.F(9.75f), Size = new Size(Ui.S(90), Ui.S(34)), Location = new Point(x, y) };
            del.Click += (_, _) => { DialogResult = DialogResult.Abort; Close(); };
            Controls.Add(del);
        }
        ClientSize = new Size(w + x * 2, y + Ui.S(50));
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Save()
    {
        string host = _host.Text.Trim();
        // 貼咗成條網址都得
        if (Uri.TryCreate(host, UriKind.Absolute, out var u) && u.Host.Length > 0)
        {
            host = u.Host;
            if (!u.IsDefaultPort) _port.Text = u.Port.ToString();
            _https.Checked = u.Scheme == "https";
        }
        if (host.Length == 0 || _user.Text.Trim().Length == 0) { MessageBox.Show(this, Lang.T("請填地址同帳號。"), Program.AppName); return; }
        if (!int.TryParse(_port.Text.Trim(), out int port) || port is < 1 or > 65535) { MessageBox.Show(this, Lang.T("端口唔啱。"), Program.AppName); return; }
        if (_orig == null && _pass.Text.Length == 0) { MessageBox.Show(this, Lang.T("請填密碼。"), Program.AppName); return; }
        var p = _orig ?? new NasProfile();
        bool addrChanged = _orig != null && (_orig.Host != host || _orig.Port != port || _orig.Https != _https.Checked || _orig.User != _user.Text.Trim());
        p.Name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : host;
        p.Host = host;
        p.Port = port;
        p.Https = _https.Checked;
        p.User = _user.Text.Trim();
        p.Mac = _mac.Text.Trim();
        if (_pass.Text.Length > 0) p.Secret = NasStore.Protect(_pass.Text);
        if (addrChanged) { p.CertThumb = ""; p.Device = ""; }
        Result = p;
        DialogResult = DialogResult.OK;
        Close();
    }
}
