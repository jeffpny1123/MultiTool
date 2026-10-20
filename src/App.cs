using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool;

internal static class Program
{
    public static string AppName => Lang.T("多功能工具");
    public const string Version = "v1.5";
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
        Application.Run(new MainForm());
    }
}

/// <summary>主視窗：左邊側欄揀功能，右邊顯示嗰個功能嘅頁。以後加功能就喺 Pages 加一項。</summary>
internal sealed class MainForm : Form
{
    private readonly Panel _sidebar = new();
    private readonly Panel _content = new();
    private readonly System.Collections.Generic.List<(NavItem Nav, Func<Control> Build, Control? Page)> _pages = new();
    private int _current = -1;

    public MainForm()
    {
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
        void PlaceBy()
        {
            by.Location = new Point(Ui.S(22), _sidebar.ClientSize.Height - by.PreferredHeight - Ui.S(16));
            lang.Location = new Point(Ui.S(20), by.Top - lang.PreferredHeight - Ui.S(8));
        }
        _sidebar.Resize += (_, _) => PlaceBy();
        PlaceBy();

        // ---- 功能清單（以後喺度加）----
        AddPage(Lang.T("首頁"), BuildHomePage);
        AddPage(Lang.T("我的腳本"), Scripts.ScriptsPage.Build);
        AddPage(Lang.T("AI 助手"), Ai.AiPage.Build);
        AddPage(Lang.T("關於"), BuildAboutPage);

        Controls.Add(_content);
        Controls.Add(_sidebar);
        Controls.Add(new TitleBar(this, _sidebar.Width)); // 自己畫嘅標題列（最後加 = 最先 dock，橫跨成個頂）
        // 開頭就整晒所有頁（咁各頁嘅快捷鍵唔使打開過都用得）
        for (int i = _pages.Count - 1; i >= 0; i--) Show(i);
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

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // 開程式自動檢查更新（冇網 / 冇新版就唔出聲）
        _ = Updater.CheckAsync(this, silent: true);
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

    private void AddPage(string name, Func<Control> build)
    {
        var nav = new NavItem(name);
        int index = _pages.Count;
        nav.SetBounds(0, Ui.S(70) + index * Ui.S(44), _sidebar.Width, Ui.S(40));
        nav.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        nav.Click += (_, _) => Show(index);
        _sidebar.Controls.Add(nav);
        _pages.Add((nav, build, null));
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
    private Control BuildHomePage()
    {
        var stack = new CardStack();

        var welcome = new CardPanel { Title = Lang.T("歡迎使用 ") + Program.AppName, Hint = Program.Version + Lang.T(" · 開發者：") + Program.Author };
        welcome.SetContent(new WrapText(
            Lang.T("一個自用嘅 Windows 小工具，將平時重複嘅滑鼠同鍵盤動作自動化。左邊揀功能；") +
            Lang.T("所有設定同圖片會自動儲存喺 exe 隔籬嘅 data 資料夾，重開都仲喺度。")));
        stack.AddRow(welcome);

        stack.AddRow(Feature(Lang.T("我的腳本"), 1,
            Lang.T("好似按鍵精靈咁自己砌腳本：按鍵（可組合鍵、撳住 / 按下 / 彈起）、滑鼠點擊、等待、") +
            Lang.T("截圖搵圖點擊、循環同中斷循環、IF 條件（搵到圖 / 搵唔到圖 / 某點顏色 / 時間 / 倒數，可以一層層 nest）。") +
            Lang.T("IF 框同主線程同時行；每個腳本可以揀「行一次」或者「一直行」，亦可以設自己嘅快捷鍵（入咗嗰個腳本嘅預覽頁先用得）。\n\n") +
            Lang.T("已經幫你寫好一個「自動點擊」腳本：截圖搵怪物 → 搵到就點擊 → 喺點選CD 期間循環按鍵 → 再搵。") +
            Lang.T("入去截好怪物圖、設好按鍵就用得。")));

        stack.AddRow(Feature(Lang.T("AI 助手"), 2,
            Lang.T("用你部機已經安裝同登入咗嘅 Claude Code、Codex CLI（ChatGPT）或者 Gemini CLI 嚟問嘢，做基本日常問答，對話會自動儲存。") +
            Lang.T("唔使喺呢度輸入任何帳號密碼；撳右上角「登入」揀用邊個，未登入就喺彈出嘅視窗登入一次。")));

        var tips = new CardPanel { Title = Lang.T("小貼士") };
        tips.SetContent(new WrapText(
            Lang.T("• 搵圖用嘅圖片最好直接喺遊戲畫面截，唔好縮放過，配對會準好多。\n") +
            Lang.T("• 如果遊戲用管理員權限開，呢個工具都要用管理員權限開，否則點擊同按鍵會被 Windows 擋住。\n") +
            Lang.T("• 出錯會寫入 data\\error.log。")));
        stack.AddRow(tips);
        return stack;
    }

    private CardPanel Feature(string name, int pageIndex, string text)
    {
        var card = new CardPanel { Title = name };
        var go = new FancyButton { Text = Lang.T("前往 →"), Kind = BtnKind.Primary, Font = Ui.F(9.75f, FontStyle.Bold) };
        go.Size = new Size(Ui.S(96), Ui.S(34));
        go.Click += (_, _) => BeginInvoke(() => Show(pageIndex));
        card.SetContent(new WrapText(text, go));
        return card;
    }

    private Control BuildAboutPage()
    {
        var stack = new CardStack();
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
