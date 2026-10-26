using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool;

internal static class Program
{
    public static string AppName => Lang.T("多功能工具");
    public const string Version = "v2.4.2";
    public const string Author = "白兔糖v3v";

    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppPaths.LogError("UI", e.Exception);
            MessageBox.Show(Lang.T("出錯：") + e.Exception.Message + Lang.T("\n\n詳情已寫入 data\\error.log"), AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) AppPaths.LogError("Fatal", ex);
        };
        var argv = Environment.GetCommandLineArgs();
        // 右鍵影片 / 相片 →「開啟檔案」→ 影片剪輯：--edit 檔案
        var edit = Video.EdShell.EditArgs(argv);
        // 只准開一個：已經開咗就叫嗰個彈出嚟（或者交檔案俾佢開），自己即刻走
        // --restart（換語言 / 主題 / 還原備份 / 管理員重開）：等舊嗰個閂咗先開
        if (!AcquireSingle(argv.Contains("--restart") ? 15000 : 0))
        {
            var msg = edit ?? new[] { Video.EdShell.ShowMsg };
            for (int i = 0; i < 10 && !Video.EdShell.TrySend(msg); i++) System.Threading.Thread.Sleep(500);
            return;
        }
        PendingEdit = edit;
        bool startup = argv.Any(a => a == "--startup");
        Application.Run(new MainForm(startup && edit == null));
    }

    /// <summary>啟動時要用影片剪輯開嘅檔案。</summary>
    public static string[]? PendingEdit;

    private static System.Threading.Mutex? _single;

    /// <summary>攞單一實例鎖（成個程式運行期間都揸住）；攞唔到 = 已經有一個開緊。</summary>
    private static bool AcquireSingle(int waitMs)
    {
        try
        {
            _single = new System.Threading.Mutex(false, "MultiTool-Single-" + Environment.UserName);
            try { return _single.WaitOne(waitMs); }
            catch (System.Threading.AbandonedMutexException) { return true; } // 上一個閃退：當攞到
        }
        catch (UnauthorizedAccessException) { return false; } // 管理員身分開緊嗰個
        catch { return true; }
    }
}

/// <summary>主視窗：左邊側欄揀功能，右邊顯示嗰個功能嘅頁。以後加功能就喺 Pages 加一項。</summary>
internal sealed class MainForm : Form
{
    private readonly Panel _sidebar = new();
    private readonly Panel _content = new();
    private readonly NavHost _navHost = new();
    private int _step = Ui.S(44), _navOffset;
    private readonly System.Collections.Generic.List<(NavItem Nav, Func<Control> Build, Control? Page)> _pages = new();
    private readonly System.Collections.Generic.List<(string Key, string? Ext)> _pageKeys = new(); // 同 _pages 一一對應
    private int _current = -1;

    private readonly bool _startup;
    private NotifyIcon? _tray;
    private bool _exiting;

    public MainForm(bool startup = false)
    {
        _startup = startup;
        Text = Program.AppName;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(9.75f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(Ui.S(1100), Ui.S(680));
        MinimumSize = new Size(Ui.S(900), Ui.S(500));
        // 記住上次嘅視窗大細同位置（data\window.txt）
        bool wasMax = LoadWindowBounds();
        if (wasMax) Shown += (_, _) => { if (Visible && WindowState == FormWindowState.Normal) WindowState = FormWindowState.Maximized; };
        ResizeEnd += (_, _) => SaveWindowBounds();
        FormClosing += (_, _) => SaveWindowBounds();
        // 拖視窗邊改大細：放手先重新排位（步驟多時唔會拖到卡）
        Size sizeAtBegin = Size.Empty;
        ResizeBegin += (_, _) => { sizeAtBegin = Size; LiveResize.Active = true; };
        // 腳本庫分享（有開先會行）
        Shown += (_, _) => Scripts.ShareService.Start();
        Shown += (_, _) =>
        {
            Video.EdShell.Sync();
            Video.EdShell.Listen(files => BeginInvoke(() =>
            {
                if (files.Length == 1 && files[0] == Video.EdShell.ShowMsg) RestoreFromTray();
                else OpenInEditor(files);
            }));
            if (Program.PendingEdit is { } pe) { Program.PendingEdit = null; BeginInvoke(() => OpenInEditor(pe)); }
        };
        FormClosed += (_, _) => Scripts.ShareService.Stop();
        ResizeEnd += (_, _) =>
        {
            if (Size == sizeAtBegin) LiveResize.Active = false; // 淨係搬視窗：唔使重新排
            else LiveResize.End(this);
        };
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? Icon; } catch { }

        _sidebar.Dock = DockStyle.Left;
        _sidebar.Width = Ui.S(190);
        _sidebar.BackColor = Ui.Panel;
        _sidebar.Padding = new Padding(0, Ui.S(18), 0, Ui.S(12));
        _content.Dock = DockStyle.Fill;
        _content.BackColor = Ui.Bg;

        var title = Ui.Text_(Program.AppName, Ui.Text, Ui.F(12.5f, FontStyle.Bold), new Padding(0));
        title.Location = new Point(Ui.S(22), Ui.S(6));
        var ver = Ui.Text_(Program.Version, Ui.TextFaint, Ui.F(8.5f), new Padding(0));
        ver.Location = new Point(Ui.S(22), Ui.S(34));
        _sidebar.Controls.Add(title);
        _sidebar.Controls.Add(ver);

        // 左下角：開發者
        var by = Ui.Text_(Lang.T("開發者：") + Program.Author, Ui.TextFaint, Ui.F(9f), new Padding(0));
        _sidebar.Controls.Add(by);
        // 左下角：切換語言（喺「開發者」上面）
        var lang = new Label
        {
            Text = "🌐 " + Lang.DisplayName(Lang.Current) + " ▾", AutoSize = true, ForeColor = Ui.TextDim, Font = Ui.F(9f),
            Cursor = Cursors.Hand, BackColor = Ui.Panel
        };
        var langMenu = new ContextMenuStrip();
        try { langMenu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        langMenu.BackColor = Ui.CardHi;
        langMenu.ForeColor = Ui.Text;
        foreach (Language l in Enum.GetValues(typeof(Language)))
        {
            var item = new ToolStripMenuItem(Lang.DisplayName(l)) { Checked = l == Lang.Current, ForeColor = Ui.Text };
            item.Click += (_, _) => SwitchLanguage(l);
            langMenu.Items.Add(item);
        }
        lang.Click += (_, _) => langMenu.Show(lang, new Point(0, -langMenu.PreferredSize.Height));
        lang.MouseEnter += (_, _) => lang.ForeColor = Ui.Text;
        lang.MouseLeave += (_, _) => lang.ForeColor = Ui.TextDim;
        _sidebar.Controls.Add(lang);
        // 左下角：日光 / 夜間主題（喺語言上面）
        var theme = new Label
        {
            Text = Ui.Light ? Lang.T("🌙 夜間主題") : Lang.T("☀ 日光主題"), AutoSize = true, ForeColor = Ui.TextDim, Font = Ui.F(9f),
            Cursor = Cursors.Hand, BackColor = Ui.Panel
        };
        theme.Click += (_, _) => SwitchTheme(!Ui.Light);
        theme.MouseEnter += (_, _) => theme.ForeColor = Ui.Text;
        theme.MouseLeave += (_, _) => theme.ForeColor = Ui.TextDim;
        _sidebar.Controls.Add(theme);
        void PlaceBy()
        {
            by.Location = new Point(Ui.S(22), _sidebar.ClientSize.Height - by.PreferredHeight - Ui.S(16));
            lang.Location = new Point(Ui.S(20), by.Top - lang.PreferredHeight - Ui.S(8));
            theme.Location = new Point(Ui.S(20), lang.Top - theme.PreferredHeight - Ui.S(8));
        }
        _sidebar.Controls.Add(_navHost);
        _navHost.MouseWheel += (_, e) => ScrollNav(e.Delta);
        void PlaceNavHost()
        {
            _navHost.SetBounds(0, Ui.S(70), _sidebar.Width, Math.Max(Ui.S(40), theme.Top - Ui.S(10) - Ui.S(70)));
            LayoutNav();
        }
        _sidebar.Resize += (_, _) => { PlaceBy(); PlaceNavHost(); };
        PlaceBy();
        PlaceNavHost();

        // ---- 功能清單（以後喺度加）----
        AddPage(Lang.T("首頁"), BuildHomePage, "home");
        AddPage(Lang.T("我的腳本"), Scripts.ScriptsPage.Build, "scripts", Extensions.Scripts);
        AddPage(Lang.T("螢幕錄影"), Video.RecordPage.Build, "recorder", Extensions.Recorder);
        AddPage(Lang.T("影片剪輯"), Video.EditorPage.Build, "editor", Extensions.Editor);
        AddPage(Lang.T("精彩重播"), Video.ReplayPage.Build, "replay", Extensions.Replay);
        AddPage(Lang.T("遊戲改鍵"), Tools.RemapPage.Build, "remap", Extensions.Remap);
        AddPage(Lang.T("視窗管理"), Tools.WindowPage.Build, "window", Extensions.Window);
        AddPage(Lang.T("剪貼簿紀錄"), Tools.ClipboardPage.Build, "clipboard", Extensions.Clipboard);
        AddPage(Lang.T("文字辨識"), Tools.OcrPage.Build, "ocr", Extensions.Ocr);
        AddPage(Lang.T("格式轉換"), Tools.ConvertHub.Build, "convert", Extensions.Convert);
        AddPage(Lang.T("遊戲伺服器"), Tools.GameServerPage.Build, "gameserver", Extensions.GameServer);
        AddPage(Lang.T("影片下載"), Tools.DownloadPage.Build, "download", Extensions.Download);
        AddPage(Lang.T("遠端桌面（測試）"), Tools.RemotePage.Build, "remote", Extensions.Remote);
        AddPage(Lang.T("效能監控"), Tools.PerfHub.Build, "perf", Extensions.Perf);
        AddPage(Lang.T("磁碟空間及健康"), Tools.DiskPage.Build, "disk", Extensions.Disk);
        AddPage(Lang.T("AI 助手"), Ai.AiPage.Build, "ai");
        AddPage(Lang.T("工具庫"), () => new ExtensionsPage(), "extensions");
        AddPage(Lang.T("設定"), BuildAboutPage, "settings");
        LoadNavOrder();
        LayoutNav();
        HotkeyHub.CurrentPage = () => _current >= 0 ? _pageKeys[_current].Key : null;
        HotkeyHub.Start();
        Tools.PerfOverlay.Wire();
        Extensions.Changed += () => BeginInvoke(() =>
        {
            Video.EdShell.Sync();
            LayoutNav();
            // 啱啱安裝咗：即刻整好個頁（佢嘅快捷鍵唔使打開過都用得）
            for (int i = 0; i < _pages.Count; i++)
                if (PageVisible(i)) EnsureBuilt(i);
        });

        Controls.Add(_content);
        Controls.Add(_sidebar);
        Controls.Add(new TitleBar(this, _sidebar.Width)); // 自己畫嘅標題列（最後加 = 最先 dock，橫跨成個頂）
        // 開頭就整晒所有（已安裝嘅）頁（咁各頁嘅快捷鍵唔使打開過都用得）
        for (int i = _pages.Count - 1; i >= 0; i--) if (PageVisible(i)) Show(i);
    }

    // ---------- 系統匣 ----------
    private void EnsureTray()
    {
        if (_tray != null) return;
        var menu = new ContextMenuStrip();
        try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        menu.BackColor = Ui.CardHi;
        menu.ForeColor = Ui.Text;
        menu.Items.Add(Lang.T("開啟"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(Lang.T("結束"), null, (_, _) => ExitApp());
        _tray = new NotifyIcon { Icon = Icon, Text = Program.AppName, ContextMenuStrip = menu, Visible = false };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void HideToTray(bool tip)
    {
        EnsureTray();
        _tray!.Visible = true;
        Hide();
        if (tip) try { _tray.ShowBalloonTip(3000, Program.AppName, Lang.T("MultiTool 喺系統匣繼續運行，腳本同快捷鍵照用得。右鍵圖示可以結束。"), ToolTipIcon.Info); } catch { }
    }

    private static string WindowFile => Path.Combine(AppPaths.Data, "window.txt");

    /// <summary>讀返上次嘅視窗位置 / 大細；仲喺某個螢幕入面先用。回傳上次係咪最大化。</summary>
    private bool LoadWindowBounds()
    {
        try
        {
            if (!File.Exists(WindowFile)) return false;
            var p = File.ReadAllText(WindowFile).Trim().Split(',');
            if (p.Length < 4) return false;
            var r = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
            bool max = p.Length > 4 && p[4] == "1";
            r.Width = Math.Max(r.Width, MinimumSize.Width);
            r.Height = Math.Max(r.Height, MinimumSize.Height);
            // 螢幕拔走咗 / 解像度變咗：標題列要見得到先用，唔係就淨係用大細、放返中間
            bool visible = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(r.X + 40, r.Y, Math.Max(1, r.Width - 80), Ui.S(30))));
            if (visible)
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = r;
            }
            else
            {
                var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
                Size = new Size(Math.Min(r.Width, wa.Width), Math.Min(r.Height, wa.Height));
            }
            return max;
        }
        catch { return false; }
    }

    private void SaveWindowBounds()
    {
        try
        {
            if (WindowState == FormWindowState.Minimized && !Visible) return;
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (r.Width < 100 || r.Height < 100) return;
            File.WriteAllText(WindowFile, $"{r.X},{r.Y},{r.Width},{r.Height},{(WindowState == FormWindowState.Maximized ? 1 : 0)}");
        }
        catch { }
    }

    /// <summary>用影片剪輯開檔案（新專案）。</summary>
    private void OpenInEditor(string[] files)
    {
        RestoreFromTray();
        if (!Extensions.IsInstalled(Extensions.Editor))
        {
            MessageBox.Show(this, Lang.T("未安裝影片剪輯：請去「工具庫」安裝。"), Program.AppName);
            return;
        }
        ShowKey("editor");
        Video.EditorPage.Instance?.OpenAsNewProject(files);
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (_tray != null) _tray.Visible = false;
    }

    private void ExitApp()
    {
        _exiting = true;
        if (_tray != null) _tray.Visible = false;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            if (!AppSettings.AskedClose)
            {
                // 第一次閂：問想閂定縮去系統匣（之後喺「設定」改）
                var r = MessageBox.Show(this,
                    Lang.T("閂視窗時要點做？\n\n「是」：最少化至系統匣（腳本同快捷鍵繼續用得）\n「否」：關閉程式\n\n之後可以喺「設定」改。"),
                    Program.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                AppSettings.Tray = r == DialogResult.Yes;
                AppSettings.AskedClose = true;
                AppSettings.Save();
                _trayBox?.Invoke(AppSettings.Tray);
            }
            if (AppSettings.Tray)
            {
                e.Cancel = true;
                HideToTray(tip: !_trayTipShown);
                _trayTipShown = true;
                return;
            }
        }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        base.OnFormClosing(e);
    }

    private bool _trayTipShown;
    private Action<bool>? _trayBox; // 設定頁個剔格（第一次問完同步）

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // 開機自動啟動：有開「系統匣」就直接收埋，冇就最少化
        if (_startup)
        {
            if (AppSettings.Tray) HideToTray(tip: false);
            else WindowState = FormWindowState.Minimized;
        }
        // 開程式自動檢查更新（冇網 / 冇新版就唔出聲）
        _ = Updater.CheckAsync(this, silent: true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTheme.DarkTitleBar(this);
        // 叫 Windows 重新計外框（拎走預設標題列）
        SetWindowPos(Handle, 0, 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0004 | 0x0020 /*NOMOVE|NOSIZE|NOZORDER|FRAMECHANGED*/);
    }

    // ---------- 拎走 Windows 預設標題列，但保留邊框拉大細、陰影、貼邊分割畫面 ----------
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NCCALCSIZE_PARAMS { public RECT R0, R1, R2; public nint Pos; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsZoomed(nint h);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int i);

    protected override void WndProc(ref Message m)
    {
        const int WM_NCCALCSIZE = 0x0083;
        if (m.Msg == WM_NCCALCSIZE && m.WParam != 0)
        {
            // 先等 Windows 計好左右下嘅（隱形）拉大細邊框，再將頂部改返去視窗最頂 = 冇標題列
            var p = System.Runtime.InteropServices.Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
            int top = p.R0.Top;
            base.WndProc(ref m);
            p = System.Runtime.InteropServices.Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
            // 最大化時視窗會超出螢幕少少，頂部要縮返入嚟
            p.R0.Top = top + (IsZoomed(Handle) ? GetSystemMetrics(33 /*SM_CYSIZEFRAME*/) + GetSystemMetrics(92 /*SM_CXPADDEDBORDER*/) : 0);
            System.Runtime.InteropServices.Marshal.StructureToPtr(p, m.LParam, false);
            m.Result = 0;
            return;
        }
        base.WndProc(ref m);
    }


    /// <summary>換主題：記低，再重新啟動程式。</summary>
    private void SwitchTheme(bool light)
    {
        if (MessageBox.Show(this, Lang.T("切換主題需要重新啟動程式，運行中嘅腳本會停止。而家重新啟動？"), Program.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Ui.SaveTheme(light);
        try
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
        }
        catch (Exception ex) { AppPaths.LogError("重新啟動", ex); }
        Application.Exit();
    }

    /// <summary>換語言：記低，再重新啟動程式（介面全部重新整過）。</summary>
    private void SwitchLanguage(Language l)
    {
        if (l == Lang.Current) return;
        if (MessageBox.Show(this, Lang.T("切換語言需要重新啟動程式，運行中嘅腳本會停止。而家重新啟動？"), Program.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Lang.Save(l);
        try
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
        }
        catch (Exception ex) { AppPaths.LogError("重新啟動", ex); }
        Application.Exit();
    }

    private void AddPage(string name, Func<Control> build, string key, string? ext = null)
    {
        var nav = new NavItem(name);
        (nav.Glyph, nav.Hue) = Extensions.Look(ext ?? key);
        int index = _pages.Count;
        nav.SetBounds(0, index * Ui.S(44), _sidebar.Width, Ui.S(40));
        nav.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        nav.Click += (_, _) =>
        {
            if (_navNoClick) { _navNoClick = false; return; }
            Show(index);
        };
        // 拖住上下搬 = 自己排次序
        nav.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || IsFixed(index)) return; // 首頁 / 工具庫 / 設定 固定位置
            _dragNav = index;
            _dragY = e.Y;
            _dragging = false;
        };
        nav.MouseMove += (_, e) =>
        {
            if (_dragNav != index || Control.MouseButtons != MouseButtons.Left) return;
            if (!_dragging)
            {
                if (Math.Abs(e.Y - _dragY) < Ui.S(6)) return;
                _dragging = true;
                nav.BringToFront();
                nav.Cursor = Cursors.SizeNS;
            }
            var vis = VisibleOrder();
            int minTop = -_navOffset + FixedTop * _step, maxTop = -_navOffset + (FixedTop + vis.Count - 1) * _step;
            nav.Top = Math.Max(minTop, Math.Min(maxTop, nav.Top + e.Y - _dragY));
            PreviewDrag(index, nav.Top + nav.Height / 2);
        };
        nav.MouseUp += (_, _) =>
        {
            if (_dragNav == index && _dragging)
            {
                CommitDrag(index, nav.Top + nav.Height / 2);
                nav.Cursor = Cursors.Hand;
                _navNoClick = true;
                // 部分情況 Click 唔會跟住嚟：之後清返個旗
                BeginInvoke(() => _navNoClick = false);
            }
            _dragNav = -1;
            _dragging = false;
        };
        nav.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) ShowNavMenu(index, nav, e.Location);
        };
        nav.MouseWheel += (_, e) => ScrollNav(e.Delta);
        _navHost.Controls.Add(nav);
        _pages.Add((nav, build, null));
        _pageKeys.Add((key, ext));
        _order.Add(index);
    }

    // ---------- 側欄排序（存喺 data\nav_order.txt）----------
    private readonly System.Collections.Generic.List<int> _order = new();
    private int _dragNav = -1, _dragY;
    private bool _dragging, _navNoClick;

    private static string NavOrderFile => Path.Combine(AppPaths.Data, "nav_order.txt");

    private void LoadNavOrder()
    {
        try
        {
            if (!File.Exists(NavOrderFile)) return;
            var order = new System.Collections.Generic.List<int>();
            foreach (var l in File.ReadAllLines(NavOrderFile))
            {
                int i = _pageKeys.FindIndex(k => k.Key == l.Trim());
                if (i >= 0 && !order.Contains(i)) order.Add(i);
            }
            // 新版本加咗嘅頁：放喺佢預設前一頁後面
            for (int i = 0; i < _pages.Count; i++)
            {
                if (order.Contains(i)) continue;
                int at = i == 0 ? 0 : order.IndexOf(i - 1) + 1;
                order.Insert(Math.Max(0, Math.Min(at, order.Count)), i);
            }
            _order.Clear();
            _order.AddRange(order);
        }
        catch (Exception ex) { AppPaths.LogError("讀側欄排序", ex); }
    }

    private void SaveNavOrder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllLines(NavOrderFile, _order.Select(i => _pageKeys[i].Key));
        }
        catch (Exception ex) { AppPaths.LogError("儲存側欄排序", ex); }
    }

    // 首頁固定喺最頂；工具庫、設定固定喺最底（唔可以拖 / 改次序）
    private static readonly string[] FixedTopKeys = { "home" };
    private static readonly string[] FixedBottomKeys = { "extensions", "settings" };
    private bool IsFixed(int i) => FixedTopKeys.Contains(_pageKeys[i].Key) || FixedBottomKeys.Contains(_pageKeys[i].Key);
    private int FixedTop => FixedTopKeys.Count(k => _pageKeys.Any(p => p.Key == k));

    /// <summary>可以排次序嘅項目（已安裝、唔係固定位置）。</summary>
    private System.Collections.Generic.List<int> VisibleOrder() => _order.Where(i => PageVisible(i) && !IsFixed(i)).ToList();

    /// <summary>側欄實際顯示次序：首頁 → 可排序嘅工具 → 工具庫 → 設定。</summary>
    private System.Collections.Generic.List<int> DisplayOrder()
    {
        int Find(string k) => _pageKeys.FindIndex(p => p.Key == k);
        var list = FixedTopKeys.Select(Find).Where(i => i >= 0).ToList();
        list.AddRange(VisibleOrder());
        list.AddRange(FixedBottomKeys.Select(Find).Where(i => i >= 0));
        return list.Where(PageVisible).ToList();
    }

    /// <summary>拖緊：其他項讓位。</summary>
    private void PreviewDrag(int dragged, int centerY)
    {
        var vis = VisibleOrder();
        vis.Remove(dragged);
        int slot = Math.Max(0, Math.Min(vis.Count, (centerY + _navOffset) / _step - FixedTop));
        int y = -_navOffset + FixedTop * _step;
        for (int k = 0; k < vis.Count; k++)
        {
            if (k == slot) y += _step;
            var n = _pages[vis[k]].Nav;
            if (n.Top != y) n.Top = y;
            y += _step;
        }
    }

    private void CommitDrag(int dragged, int centerY)
    {
        var vis = VisibleOrder();
        vis.Remove(dragged);
        int slot = Math.Max(0, Math.Min(vis.Count, (centerY + _navOffset) / _step - FixedTop));
        MoveNav(dragged, slot < vis.Count ? vis[slot] : -1, slot >= vis.Count && vis.Count > 0 ? vis[^1] : -1);
    }

    /// <summary>將 page 搬去 before 前面（或者 after 後面）。</summary>
    private void MoveNav(int page, int before, int after)
    {
        _order.Remove(page);
        if (before >= 0) _order.Insert(_order.IndexOf(before), page);
        else if (after >= 0) _order.Insert(_order.IndexOf(after) + 1, page);
        else _order.Add(page);
        SaveNavOrder();
        LayoutNav();
    }

    private void ShowNavMenu(int index, Control nav, Point at)
    {
        if (IsFixed(index)) return; // 固定位置：冇排序選單
        var vis = VisibleOrder();
        int pos = vis.IndexOf(index);
        var menu = new ContextMenuStrip();
        try { menu.Renderer = DarkTheme.MenuRenderer(); } catch { }
        menu.BackColor = Ui.CardHi;
        menu.ForeColor = Ui.Text;
        ToolStripMenuItem Item(string text, bool enabled, Action act)
        {
            var it = new ToolStripMenuItem(text) { Enabled = enabled, ForeColor = Ui.Text };
            it.Click += (_, _) => act();
            menu.Items.Add(it);
            return it;
        }
        Item(Lang.T("上移"), pos > 0, () => MoveNav(index, vis[pos - 1], -1));
        Item(Lang.T("下移"), pos >= 0 && pos < vis.Count - 1, () => MoveNav(index, -1, vis[pos + 1]));
        menu.Items.Add(new ToolStripSeparator());
        Item(Lang.T("重設排序"), true, () =>
        {
            _order.Clear();
            for (int i = 0; i < _pages.Count; i++) _order.Add(i);
            try { File.Delete(NavOrderFile); } catch { }
            LayoutNav();
        });
        var hint = new ToolStripMenuItem(Lang.T("（亦可以直接拖住上下搬）")) { Enabled = false };
        menu.Items.Add(hint);
        menu.Closed += (_, _) => BeginInvoke(() => menu.Dispose());
        menu.Show(nav, at);
    }

    private bool PageVisible(int i) => _pageKeys[i].Ext is not { } e || Extensions.IsInstalled(e);

    /// <summary>側欄：跟用家排嘅次序，淨係顯示已安裝嘅擴充工具。</summary>
    /// <summary>側欄項目：太多就逐格縮細少少，再唔夠位就可以用滑鼠滾輪捲。</summary>
    private void LayoutNav()
    {
        if (_pages.Count == 0) return;
        var shown = DisplayOrder();
        int n = shown.Count, avail = _navHost.Height;
        _step = n == 0 ? Ui.S(44) : Math.Clamp(avail / n, Ui.S(34), Ui.S(44));
        int total = n * _step;
        _navOffset = Math.Clamp(_navOffset, 0, Math.Max(0, total - avail));
        bool overflow = total > avail;
        int w = _navHost.Width - (overflow ? Ui.S(6) : 0);
        int y = -_navOffset;
        for (int i = 0; i < _pages.Count; i++) _pages[i].Nav.Visible = shown.Contains(i);
        foreach (int i in shown)
        {
            _pages[i].Nav.SetBounds(0, y, w, _step - Ui.S(4));
            y += _step;
        }
        _navHost.SetScroll(overflow ? _navOffset : -1, total, avail);
        // 而家顯示緊嘅頁被移除咗：返去首頁
        if (_current >= 0 && !PageVisible(_current)) Show(0);
    }

    private void ScrollNav(int delta)
    {
        int before = _navOffset;
        _navOffset -= Math.Sign(delta) * _step;
        LayoutNav();
        if (before != _navOffset) _navHost.Invalidate();
    }

    private void EnsureBuilt(int i)
    {
        if (_pages[i].Page != null) return;
        int cur = _current;
        Show(i);
        if (cur >= 0 && cur != i) { _current = -1; Show(cur); }
    }

    /// <summary>用 key 跳去某一頁（首頁「前往」用）。</summary>
    internal void ShowKey(string key)
    {
        if (key == "library") // 腳本庫喺「我的腳本」入面
        {
            ShowKey("scripts");
            Scripts.ScriptsPage.Instance?.OpenLibrary();
            return;
        }
        int i = _pageKeys.FindIndex(k => k.Key == key);
        if (i >= 0) Show(i);
    }

    private void Show(int index)
    {
        if (index == _current) return;
        var p = _pages[index];
        if (p.Page == null)
        {
            Control page;
            try { page = p.Build(); }
            catch (Exception ex)
            {
                AppPaths.LogError("Build page " + p.Nav.Text, ex);
                page = new Label { Text = Lang.T("載入失敗：") + ex.Message, ForeColor = Ui.Danger, Dock = DockStyle.Fill, Padding = new Padding(Ui.S(24)) };
            }
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _content.Controls.Add(page);
            _pages[index] = (p.Nav, p.Build, page);
            p = _pages[index];
        }
        for (int i = 0; i < _pages.Count; i++)
        {
            _pages[i].Nav.Active = i == index;
            if (_pages[i].Page != null) _pages[i].Page!.Visible = i == index;
        }
        _current = index;
    }

    /// <summary>首頁：簡單介紹各個功能，撳「前往」直接跳去。</summary>
    /// <summary>首頁：歡迎橫額 + 已安裝工具一格格 + 小貼士。</summary>
    private Control BuildHomePage() => new HomePage(k => BeginInvoke(() => ShowKey(k)));

    private CardPanel Feature(string name, string key, string? ext, string text)
    {
        var card = new CardPanel { Title = name };
        var go = new FancyButton { Text = Lang.T("前往 →"), Kind = BtnKind.Primary, Font = Ui.F(9.75f, FontStyle.Bold) };
        go.Size = new Size(Ui.S(96), Ui.S(34));
        // 未安裝嘅擴充工具：帶去「擴充工具」頁安裝
        go.Click += (_, _) => BeginInvoke(() => ShowKey(ext != null && !Extensions.IsInstalled(ext) ? "extensions" : key));
        card.SetContent(new WrapText(text, go));
        return card;
    }

    private Control BuildAboutPage()
    {
        var stack = new CardStack();

        // ---- 設定 ----
        var set = new CardPanel { Title = Lang.T("設定") };
        var flow = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        CheckBox Box(string text, bool value, Action<bool> changed)
        {
            var cb = new CheckBox { Text = text, Checked = value, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, Font = Ui.F(10f), Margin = new Padding(0, Ui.S(4), 0, Ui.S(8)) };
            cb.CheckedChanged += (_, _) => changed(cb.Checked);
            flow.Controls.Add(cb);
            return cb;
        }
        var auto = Box(Lang.T("開機時啟動"), AutoStart.Enabled, v =>
        {
            if (!AutoStart.Set(v)) MessageBox.Show(this, Lang.T("設定唔到開機啟動。"), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
        var tray = Box(Lang.T("關閉時最少化至系統匣"), AppSettings.Tray, v =>
        {
            AppSettings.Tray = v;
            AppSettings.AskedClose = true;
            AppSettings.Save();
        });
        _trayBox = v => { if (!tray.IsDisposed) tray.Checked = v; };
        set.SetContent(flow);
        stack.AddRow(set);

        // ---- 通知（我的腳本：完成 / 出錯 / 通知步驟）----
        var notify = new CardPanel
        {
            Title = Lang.T("通知"),
            Hint = Lang.T("我的腳本嘅「通知」步驟同「完成 / 出錯時通知」會彈 Windows 通知；填咗 Discord webhook 就可以同時發去 Discord（頻道設定 → 整合 → Webhook → 複製網址）。")
        };
        var hookBox = new TextBox { Text = Scripts.Notifier.DiscordUrl, PlaceholderText = "https://discord.com/api/webhooks/…", UseSystemPasswordChar = Scripts.Notifier.DiscordUrl.Length > 0 };
        var hookHost = new InputHost(hookBox, Ui.S(420));
        var hookStatus = Ui.Text_("", Ui.TextFaint, Ui.F(9f), new Padding(0, Ui.S(9), 0, 0));
        hookBox.Enter += (_, _) => hookBox.UseSystemPasswordChar = false;
        hookBox.Leave += (_, _) =>
        {
            var v = hookBox.Text.Trim();
            if (v.Length > 0 && !Scripts.Notifier.ValidUrl(v)) { hookStatus.Text = Lang.T("網址唔啱：要係 https://discord.com/api/webhooks/… 開頭"); hookStatus.ForeColor = Ui.Danger; return; }
            Scripts.Notifier.DiscordUrl = v;
            hookStatus.Text = v.Length > 0 ? Lang.T("已儲存") : "";
            hookStatus.ForeColor = Ui.Success;
            hookBox.UseSystemPasswordChar = v.Length > 0;
        };
        var hookTest = new FancyButton { Text = Lang.T("試發"), Kind = BtnKind.Default };
        hookTest.Size = new Size(Math.Max(Ui.S(72), hookTest.GetPreferredSize(Size.Empty).Width), Ui.S(34));
        hookTest.Click += async (_, _) =>
        {
            var v = hookBox.Text.Trim();
            Scripts.Notifier.Send(Program.AppName, Lang.T("測試通知"), toast: true, discord: false);
            if (v.Length == 0) { hookStatus.Text = Lang.T("已彈 Windows 通知（未填 Discord）"); hookStatus.ForeColor = Ui.TextFaint; return; }
            hookTest.Enabled = false;
            hookStatus.Text = Lang.T("發緊…"); hookStatus.ForeColor = Ui.TextFaint;
            var err = await Scripts.Notifier.SendDiscordAsync(Program.AppName, Lang.T("測試通知"), v);
            hookTest.Enabled = true;
            if (err == null) { Scripts.Notifier.DiscordUrl = v; hookStatus.Text = Lang.T("已發送，Discord 應該收到"); hookStatus.ForeColor = Ui.Success; }
            else { hookStatus.Text = Lang.T("發送失敗：") + err; hookStatus.ForeColor = Ui.Danger; }
        };
        var hookRow = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, WrapContents = true };
        hookRow.Controls.Add(Ui.Text_(Lang.T("Discord webhook"), Ui.TextDim, Ui.F(9.75f), new Padding(0, Ui.S(9), Ui.S(10), 0)));
        hookHost.Margin = new Padding(0, 0, Ui.S(10), 0);
        hookRow.Controls.Add(hookHost);
        hookTest.Margin = new Padding(0, 0, Ui.S(10), 0);
        hookRow.Controls.Add(hookTest);
        hookRow.Controls.Add(hookStatus);
        notify.SetContent(hookRow);
        stack.AddRow(notify);

        // ---- 備份及還原 ----
        var backup = new CardPanel
        {
            Title = Lang.T("備份及還原"),
            Hint = Lang.T("將我的腳本、各工具設定同快捷鍵打包做一個 zip，換電腦或者重裝都可以還原。唔包下載返嚟嘅工具（ffmpeg 等）、AI 模型、遊戲伺服器同大過 50 MB 嘅檔案。")
        };
        var bRow = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, WrapContents = true };
        var exp = new FancyButton { Text = Lang.T("匯出備份…"), Kind = BtnKind.Primary };
        exp.Size = exp.GetPreferredSize(Size.Empty);
        exp.Click += async (_, _) => { exp.Enabled = false; try { await DataBackup.ExportAsync(this); } finally { exp.Enabled = true; } };
        var imp = new FancyButton { Text = Lang.T("由備份還原…"), Kind = BtnKind.Default };
        imp.Size = imp.GetPreferredSize(Size.Empty);
        imp.Click += async (_, _) => { imp.Enabled = false; try { await DataBackup.ImportAsync(this); } finally { if (!imp.IsDisposed) imp.Enabled = true; } };
        bRow.Controls.Add(exp);
        bRow.Controls.Add(imp);
        backup.SetContent(bRow);
        stack.AddRow(backup);


        var card = new CardPanel { Title = Program.AppName + " " + Program.Version, Hint = Lang.T("開發者：") + Program.Author + Lang.T("。設定同圖片全部存喺 exe 隔籬嘅 data 資料夾。") };
        var row = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, WrapContents = false };
        var update = new FancyButton { Text = Lang.T("檢查更新"), Kind = BtnKind.Primary };
        update.Size = update.GetPreferredSize(Size.Empty);
        update.Click += (_, _) => _ = Updater.CheckAsync(this, silent: false);
        row.Controls.Add(update);
        var gh = new FancyButton { Text = "GitHub", Kind = BtnKind.Default };
        gh.Size = gh.GetPreferredSize(Size.Empty);
        gh.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Updater.RepoUrl) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, Program.AppName); }
        };
        row.Controls.Add(gh);
        var open = new FancyButton { Text = Lang.T("開啟 data 資料夾"), Kind = BtnKind.Default };
        open.Size = open.GetPreferredSize(Size.Empty);
        open.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Data);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppPaths.Data + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, Program.AppName); }
        };
        row.Controls.Add(open);
        card.SetContent(row);
        stack.AddRow(card);

        var lic = new CardPanel { Title = Lang.T("授權") };
        lic.SetContent(new WrapText(
            "© " + Program.Author + Lang.T("。本軟件按 PolyForm Noncommercial License 1.0.0 授權：") +
            Lang.T("可以免費使用、修改同分享，但禁止轉售或任何商業用途。\n") +
            "https://polyformproject.org/licenses/noncommercial/1.0.0"));
        stack.AddRow(lic);
        return stack;
    }
}

/// <summary>程式設定（data\\settings.txt）。</summary>
internal static class AppSettings
{
    private static string File_ => Path.Combine(AppPaths.Data, "settings.txt");
    public static bool Tray;
    public static bool AskedClose;

    static AppSettings()
    {
        try
        {
            if (!File.Exists(File_)) return;
            foreach (var line in File.ReadAllLines(File_))
            {
                if (line == "tray=1") Tray = true;
                if (line == "asked=1") AskedClose = true;
            }
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllLines(File_, new[] { "tray=" + (Tray ? "1" : "0"), "asked=" + (AskedClose ? "1" : "0") });
        }
        catch { }
    }
}

/// <summary>開機啟動（目前用戶嘅 Run 登錄機碼，唔使管理員）。</summary>
internal static class AutoStart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "MultiTool";
    private static string Exe => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool Enabled
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
                return k?.GetValue(Name) is string v && v.Contains(Exe, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static bool Set(bool on)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
            if (on) k.SetValue(Name, $"\"{Exe}\" --startup");
            else k.DeleteValue(Name, false);
            return true;
        }
        catch (Exception ex) { AppPaths.LogError("開機啟動", ex); return false; }
    }
}

/// <summary>會自動換行嘅文字（可以喺右下角放一個掣）。</summary>
internal sealed class WrapText : Control
{
    private readonly Control? _button;

    public WrapText(string text, Control? button = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Text = text;
        Font = Ui.F(10f);
        _button = button;
        if (button != null) Controls.Add(button);
    }

    private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;

    private int TextH(int width) => TextRenderer.MeasureText(Text, Font, new Size(Math.Max(10, width), 0), Flags).Height;

    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 0 ? proposed.Width : Math.Max(Width, Ui.S(300));
        int h = TextH(w) + (_button != null ? Ui.S(14) + _button.Height : 0);
        return new Size(w, h);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_button != null) _button.Location = new Point(0, TextH(ClientSize.Width) + Ui.S(14));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Width, TextH(Width)), Ui.TextDim, Flags);
    }
}

/// <summary>
/// 自己畫嘅標題列：左邊同側欄同色，右邊有最小化 / 最大化 / 關閉；
/// 拖住可以移動視窗，雙擊最大化，最頂幾 px 可以拉高。
/// </summary>
internal sealed class TitleBar : Control
{
    private readonly Form _form;
    private readonly int _sidebarW;
    private int _hover = -1, _down = -1; // 0 最小化, 1 最大化, 2 關閉

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SendMessage(nint h, int msg, nint w, nint l);

    public TitleBar(Form form, int sidebarWidth)
    {
        _form = form;
        _sidebarW = sidebarWidth;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        Height = Ui.S(34);
        form.Resize += (_, _) => Invalidate();
    }

    private int BtnW => Ui.S(46);
    private Rectangle Btn(int i) => new(Width - BtnW * (3 - i), 0, BtnW, Height);

    private int HitButton(Point p)
    {
        for (int i = 0; i < 3; i++) if (Btn(i).Contains(p)) return i;
        return -1;
    }

    private bool Maximized => _form.WindowState == FormWindowState.Maximized;
    private int TopGrip => Maximized ? 0 : Ui.S(5);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Bg);
        using (var side = new SolidBrush(Ui.Panel)) g.FillRectangle(side, 0, 0, _sidebarW, Height);

        for (int i = 0; i < 3; i++)
        {
            var r = Btn(i);
            if (i == _hover)
            {
                var bg = i == 2 ? (_down == 2 ? ControlPaint.Dark(Ui.Danger, 0.1f) : Ui.Danger) : (_down == i ? Ui.BorderHi : Ui.CardHi);
                using var b = new SolidBrush(bg);
                g.FillRectangle(b, r);
            }
            var col = i == _hover && i == 2 ? Color.White : i == _hover ? Ui.Text : Ui.TextDim;
            using var pen = new Pen(col, Math.Max(1f, Ui.S(1f)));
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, s = Ui.S(5);
            switch (i)
            {
                case 0: // 最小化
                    g.DrawLine(pen, cx - s, cy, cx + s, cy);
                    break;
                case 1: // 最大化 / 還原
                    if (Maximized)
                    {
                        g.DrawRectangle(pen, cx - s, cy - s + Ui.S(2), s * 2 - Ui.S(2), s * 2 - Ui.S(2));
                        g.DrawLine(pen, cx - s + Ui.S(2), cy - s, cx + s, cy - s);
                        g.DrawLine(pen, cx + s, cy - s, cx + s, cy + s - Ui.S(2));
                    }
                    else g.DrawRectangle(pen, cx - s, cy - s, s * 2, s * 2);
                    break;
                case 2: // 關閉
                    var old = g.SmoothingMode;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
                    g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
                    g.SmoothingMode = old;
                    break;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitButton(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        Cursor = h < 0 && e.Y < TopGrip ? Cursors.SizeNS : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = _down = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int h = HitButton(e.Location);
        if (h >= 0) { _down = h; Invalidate(); return; }
        if (e.Clicks >= 2)
        {
            ToggleMax();
            return;
        }
        // 交俾 Windows：拖動 = 移動視窗（支援貼邊分割畫面）；最頂幾 px = 拉高
        const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2, HTTOP = 12;
        ReleaseCapture();
        SendMessage(_form.Handle, WM_NCLBUTTONDOWN, e.Y < TopGrip ? HTTOP : HTCAPTION, 0);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int h = HitButton(e.Location);
        int down = _down;
        _down = -1;
        Invalidate();
        if (h < 0 || h != down) return;
        switch (h)
        {
            case 0: _form.WindowState = FormWindowState.Minimized; break;
            case 1: ToggleMax(); break;
            case 2: _form.Close(); break;
        }
    }

    private void ToggleMax()
    {
        _form.WindowState = Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        Invalidate();
    }
}

/// <summary>側欄項目嘅容器：超出範圍時喺右邊畫細捲軸。</summary>
internal sealed class NavHost : Panel
{
    private int _offset = -1, _total, _view;

    public NavHost()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Ui.Panel;
    }

    public void SetScroll(int offset, int total, int view)
    {
        if (offset == _offset && total == _total && view == _view) return;
        _offset = offset; _total = total; _view = view;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (_offset < 0 || _total <= _view || _view <= 0) return;
        Ui.Smooth(e.Graphics);
        float h = Math.Max(Ui.S(24f), Height * (float)_view / _total);
        float y = (Height - h) * _offset / Math.Max(1, _total - _view);
        using var p = Ui.Round(new RectangleF(Width - Ui.S(5f), y, Ui.S(3f), h), Ui.S(1.5f));
        using var b = new SolidBrush(Ui.BorderHi);
        e.Graphics.FillPath(b, p);
    }
}
