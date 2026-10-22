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
    public const string Version = "v1.8";
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
        bool startup = Environment.GetCommandLineArgs().Any(a => a == "--startup");
        Application.Run(new MainForm(startup));
    }
}

/// <summary>主視窗：左邊側欄揀功能，右邊顯示嗰個功能嘅頁。以後加功能就喺 Pages 加一項。</summary>
internal sealed class MainForm : Form
{
    private readonly Panel _sidebar = new();
    private readonly Panel _content = new();
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
        // 拖視窗邊改大細：放手先重新排位（步驟多時唔會拖到卡）
        Size sizeAtBegin = Size.Empty;
        ResizeBegin += (_, _) => { sizeAtBegin = Size; LiveResize.Active = true; };
        // 腳本庫分享（有開先會行）
        Shown += (_, _) => Scripts.ShareService.Start();
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
        _sidebar.Resize += (_, _) => PlaceBy();
        PlaceBy();

        // ---- 功能清單（以後喺度加）----
        AddPage(Lang.T("首頁"), BuildHomePage, "home");
        AddPage(Lang.T("我的腳本"), Scripts.ScriptsPage.Build, "scripts", Extensions.Scripts);
        AddPage(Lang.T("錄影工具"), Video.RecordPage.Build, "recorder", Extensions.Recorder);
        AddPage(Lang.T("精彩重播"), Video.ReplayPage.Build, "replay", Extensions.Replay);
        AddPage(Lang.T("遊戲改鍵"), Tools.RemapPage.Build, "remap", Extensions.Remap);
        AddPage(Lang.T("視窗工具"), Tools.WindowPage.Build, "window", Extensions.Window);
        AddPage(Lang.T("剪貼簿紀錄"), Tools.ClipboardPage.Build, "clipboard", Extensions.Clipboard);
        AddPage(Lang.T("文字辨識"), Tools.OcrPage.Build, "ocr", Extensions.Ocr);
        AddPage(Lang.T("轉檔工具"), Tools.ConvertPage.Build, "convert", Extensions.Convert);
        AddPage(Lang.T("遊戲伺服器"), Tools.GameServerPage.Build, "gameserver", Extensions.GameServer);
        AddPage(Lang.T("AI 助手"), Ai.AiPage.Build, "ai");
        AddPage(Lang.T("工具庫"), () => new ExtensionsPage(), "extensions");
        AddPage(Lang.T("設定"), BuildAboutPage, "settings");
        LoadNavOrder();
        LayoutNav();
        HotkeyHub.CurrentPage = () => _current >= 0 ? _pageKeys[_current].Key : null;
        HotkeyHub.Start();
        Extensions.Changed += () => BeginInvoke(() =>
        {
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
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
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
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
        }
        catch (Exception ex) { AppPaths.LogError("重新啟動", ex); }
        Application.Exit();
    }

    private void AddPage(string name, Func<Control> build, string key, string? ext = null)
    {
        var nav = new NavItem(name);
        int index = _pages.Count;
        nav.SetBounds(0, Ui.S(70) + index * Ui.S(44), _sidebar.Width, Ui.S(40));
        nav.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        nav.Click += (_, _) =>
        {
            if (_navNoClick) { _navNoClick = false; return; }
            Show(index);
        };
        // 拖住上下搬 = 自己排次序
        nav.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
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
            int minTop = Ui.S(70), maxTop = Ui.S(70) + (vis.Count - 1) * Ui.S(44);
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
        _sidebar.Controls.Add(nav);
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

    private System.Collections.Generic.List<int> VisibleOrder() => _order.Where(PageVisible).ToList();

    /// <summary>拖緊：其他項讓位。</summary>
    private void PreviewDrag(int dragged, int centerY)
    {
        var vis = VisibleOrder();
        vis.Remove(dragged);
        int slot = Math.Max(0, Math.Min(vis.Count, (centerY - Ui.S(70)) / Ui.S(44)));
        int y = Ui.S(70);
        for (int k = 0; k < vis.Count; k++)
        {
            if (k == slot) y += Ui.S(44);
            var n = _pages[vis[k]].Nav;
            if (n.Top != y) n.Top = y;
            y += Ui.S(44);
        }
    }

    private void CommitDrag(int dragged, int centerY)
    {
        var vis = VisibleOrder();
        vis.Remove(dragged);
        int slot = Math.Max(0, Math.Min(vis.Count, (centerY - Ui.S(70)) / Ui.S(44)));
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
    private void LayoutNav()
    {
        int y = Ui.S(70);
        foreach (int i in _order)
        {
            bool vis = PageVisible(i);
            _pages[i].Nav.Visible = vis;
            if (!vis) continue;
            _pages[i].Nav.SetBounds(0, y, _sidebar.Width, Ui.S(40));
            y += Ui.S(44);
        }
        // 而家顯示緊嘅頁被移除咗：返去首頁
        if (_current >= 0 && !PageVisible(_current)) Show(0);
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

        // ---- 快捷鍵表 ----
        var hk = new CardPanel
        {
            Title = Lang.T("快捷鍵"),
            Hint = Lang.T("已安裝工具嘅快捷鍵（喺度改都得）。剔「頁面外使用」= 喺任何地方都用得；冇剔就只喺嗰個工具頁先用得。快捷鍵重複就唔可以剔。")
        };
        hk.SetContent(new HotkeyTable());
        stack.AddRow(hk);

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
