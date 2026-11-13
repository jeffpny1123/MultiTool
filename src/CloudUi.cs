using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Tools;

/// <summary>雲端伺服器頁：Vultr 帳戶 → 租一部伺服器 → 喺上面開遊戲伺服器 / Discord bot。</summary>
internal sealed class CloudPage : ToolPage
{
    private static CloudData D => CloudStore.Data;
    private readonly FlowLayoutPanel _acct = TUi.Col(), _srv = TUi.Col(), _svc = TUi.Col();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15000 };
    private bool _busy;

    public static Control Build() => new CloudPage();

    private CloudPage()
    {
        Card(Lang.T("① Vultr 帳戶"),
            Lang.T("伺服器向 Vultr 租，費用由 Vultr 直接按用量收（按小時計，刪咗就停止收費）。拉比工具箱只係幫你開機同設定，唔會經手你嘅錢。"), _acct);
        Card(Lang.T("② 我的伺服器"),
            Lang.T("一部 Linux 雲端主機，可以同時行幾個遊戲伺服器同 Discord bot（每樣用唔同 port）。效能唔夠可以隨時升級 plan。"), _srv);
        Card(Lang.T("③ 伺服器上面嘅服務"),
            Lang.T("每樣服務獨立運行，當機會自動重啟；伺服器重開機之後都會自動開返。"), _svc);
        _timer.Tick += async (_, _) => { if (Visible && !_busy && D.Server is { Ready: true }) await RefreshServicesAsync(false); };
        _timer.Start();
        Disposed += (_, _) => _timer.Dispose();
        BuildAccount();
        BuildServer();
        BuildServices();
        if (D.Server is { Ready: false }) _ = WaitReadyAsync();
        else if (D.Server != null) _ = RefreshServicesAsync(true);
    }

    private static Label Lbl(string t, Color? c = null, float size = 9.75f) =>
        new() { Text = t, AutoSize = true, ForeColor = c ?? Ui.TextDim, BackColor = Ui.Card, Font = Ui.F(size), Margin = new Padding(0, Ui.S(4), Ui.S(8), Ui.S(4)), MaximumSize = new Size(Ui.S(760), 0) };

    private void Fill(FlowLayoutPanel p, params Control[] cs) { p.SuspendLayout(); foreach (Control c in p.Controls.Cast<Control>().ToList()) c.Dispose(); p.Controls.Clear(); p.Controls.AddRange(cs); p.ResumeLayout(); Relayout(); }

    private static void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

    // ---------------- ① 帳戶 ----------------
    private void BuildAccount()
    {
        if (CloudStore.ApiKey.Length == 0)
        {
            var key = new TextBox { UseSystemPasswordChar = true, PlaceholderText = Lang.T("貼上 Vultr API key") };
            var connect = TUi.Btn(Lang.T("連接"), async () => await ConnectAsync(key.Text), 90, BtnKind.Primary);
            Fill(_acct,
                Lbl(Lang.T("第一次用：\n1. 撳「開 Vultr 帳戶」註冊（新帳戶通常有試用額度）。\n2. 登入後撳「開 API 設定」→「Enable API」，喺「Access Control」揀「Allow All IPv4」，再複製 API key。\n3. 將 API key 貼入下面，撳「連接」。"), Ui.Text),
                TUi.Row(TUi.Btn(Lang.T("開 Vultr 帳戶 ↗"), () => Open(CloudStore.SignupUrl), 0, BtnKind.Success), TUi.Btn(Lang.T("開 API 設定 ↗"), () => Open(CloudStore.ApiPageUrl))),
                TUi.Row(new InputHost(key, Ui.S(360)), connect),
                Lbl(Lang.T("API key 會加密儲存喺呢部電腦，唔會傳去其他地方。")));
            return;
        }
        var state = Lbl(Lang.T("✓ 已連接 Vultr"), Ui.Success);
        Fill(_acct, TUi.Row(state, TUi.Btn(Lang.T("Vultr 網站 ↗"), () => Open("https://my.vultr.com/")), TUi.Btn(Lang.T("更換 API key"), () =>
        {
            if (MessageBox.Show(FindForm(), Lang.T("移除而家嘅 API key？（唔會刪除伺服器）"), Program.AppName, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            D.ApiKey = ""; CloudStore.Save(); BuildAccount(); BuildServer();
        })));
        _ = Task.Run(async () =>
        {
            try { var (bal, pend) = await Vultr.AccountAsync(); BeginInvoke(() => state.Text = Lang.F("✓ 已連接 Vultr · 本月未付 US${0:0.00} · 餘額 US${1:0.00}", pend, -bal)); }
            catch (Exception ex) { BeginInvoke(() => { state.Text = "⚠ " + ex.Message; state.ForeColor = Ui.Danger; }); }
        });
    }

    private async Task ConnectAsync(string key)
    {
        key = key.Trim();
        if (key.Length == 0) return;
        try
        {
            await Vultr.AccountAsync(key);
            CloudStore.ApiKey = key;
            BuildAccount(); BuildServer();
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    // ---------------- ② 伺服器 ----------------
    private void BuildServer()
    {
        if (CloudStore.ApiKey.Length == 0) { Fill(_srv, Lbl(Lang.T("請先完成 ① 連接 Vultr 帳戶。"))); return; }
        if (!Ssh.Available) { Fill(_srv, Lbl(Lang.T("呢部電腦冇 OpenSSH：請去 Windows「設定 → 系統 → 選用功能」加入「OpenSSH 用戶端」。"), Ui.Danger)); return; }
        var s = D.Server;
        if (s == null)
        {
            var region = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(220) };
            var plan = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(420) };
            var status = Lbl(Lang.T("載入緊地區同 plan…"));
            var create = TUi.Btn(Lang.T("建立伺服器"), async () =>
            {
                if (region.SelectedItem is not VultrRegion r || plan.SelectedItem is not VultrPlan p) return;
                if (MessageBox.Show(FindForm(), Lang.F("喺 {0} 建立伺服器？\n\n{1}\n\nVultr 會按小時收費，直至你刪除伺服器為止。", r, p), Program.AppName,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                await CreateServerAsync(r, p);
            }, 0, BtnKind.Primary);
            Fill(_srv, TUi.Row(TUi.L(Lang.T("地區")), new InputHost(region, Ui.S(230))), TUi.Row(TUi.L(Lang.T("效能")), new InputHost(plan, Ui.S(430))), TUi.Row(create), status,
                Lbl(Lang.T("建議：揀近你同玩家嘅地區（例如東京、新加坡）；開遊戲伺服器最少 4 GB RAM，Palworld 要 8 GB 或以上。")));
            region.SelectedIndexChanged += async (_, _) =>
            {
                if (region.SelectedItem is not VultrRegion r) return;
                plan.Items.Clear();
                try
                {
                    var plans = await Vultr.PlansAsync(r.Id);
                    foreach (var p in plans) plan.Items.Add(p);
                    plan.SelectedIndex = Math.Max(0, plans.FindIndex(p => p.RamMb >= 4096));
                    status.Text = "";
                }
                catch (Exception ex) { status.Text = "⚠ " + ex.Message; }
            };
            _ = Task.Run(async () =>
            {
                try
                {
                    var regions = await Vultr.RegionsAsync();
                    BeginInvoke(() =>
                    {
                        foreach (var r in regions) region.Items.Add(r);
                        region.SelectedIndex = Math.Max(0, regions.FindIndex(r => r.Id == "nrt"));
                    });
                }
                catch (Exception ex) { BeginInvoke(() => status.Text = "⚠ " + ex.Message); }
            });
            return;
        }
        var info = Lbl(Lang.F("{0}　IP：{1}　地區：{2}　Plan：{3}", s.Label, s.Ip.Length > 0 && s.Ip != "0.0.0.0" ? s.Ip : "…", s.Region, s.Plan), Ui.Text, 10.5f);
        var usage = Lbl(s.Ready ? Lang.T("讀緊用量…") : Lang.T("⏳ 設定緊伺服器（大約 3 至 5 分鐘）…"));
        usage.Name = "usage";
        Fill(_srv, info, usage, TUi.Row(
            TUi.Btn(Lang.T("複製 IP"), () => { try { Clipboard.SetText(s.Ip); Toast.Show(Lang.T("已複製"), Ui.Success, 1500); } catch { } }),
            TUi.Btn(Lang.T("🖥 終端機"), () => { try { Ssh.OpenTerminal(s.Ip); } catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message); } }),
            TUi.Btn(Lang.T("⬆ 升級效能"), async () => await UpgradeAsync()),
            TUi.Btn(Lang.T("⟳ 重新開機"), async () => await RebootAsync()),
            TUi.Btn(Lang.T("root 密碼"), () => MessageBox.Show(FindForm(), Lang.T("Vultr 網頁控制台用（平時唔使）：") + "\n\n" + Mail.MailStore.Unprotect(s.RootPass), Program.AppName)),
            TUi.Btn(Lang.T("🗑 刪除伺服器"), async () => await DeleteServerAsync(), 0, BtnKind.Danger)));
        if (s.Ready) _ = Task.Run(async () => { var u = await CloudKinds.UsageAsync(s.Ip); BeginInvoke(() => { if (!usage.IsDisposed) usage.Text = u.Length > 0 ? u : Lang.T("連唔到伺服器"); }); });
    }

    private async Task CreateServerAsync(VultrRegion r, VultrPlan p)
    {
        _busy = true;
        try
        {
            D.Server = await Vultr.CreateAsync(r.Id, p.Id, Program.FileName + "-" + DateTime.Now.ToString("MMdd"));
            D.Services.Clear();
            CloudStore.Save();
            BuildServer(); BuildServices();
            await WaitReadyAsync();
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), Lang.T("建立失敗：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _busy = false; }
    }

    /// <summary>等 Vultr 開好機、再等 Docker 裝好（最多 20 分鐘）。</summary>
    private async Task WaitReadyAsync()
    {
        var s = D.Server; if (s == null) return;
        for (int i = 0; i < 120 && D.Server == s && !IsDisposed; i++)
        {
            try
            {
                if (s.Ip.Length == 0 || s.Ip == "0.0.0.0" || i % 3 == 0)
                {
                    var g = await Vultr.GetAsync(s.Id);
                    if (g.Ip.Length > 0 && g.Ip != s.Ip) { s.Ip = g.Ip; CloudStore.Save(); BuildServer(); }
                }
                if (s.Ip.Length > 0 && s.Ip != "0.0.0.0")
                {
                    var (code, outp) = await Ssh.ExecAsync(s.Ip, "test -f /opt/rabi/ready && docker --version", 30);
                    if (code == 0 && outp.Contains("Docker"))
                    {
                        s.Ready = true; CloudStore.Save();
                        BuildServer(); BuildServices();
                        Toast.Show(Lang.T("伺服器準備好喇！"), Ui.Success, 4000);
                        return;
                    }
                }
            }
            catch { }
            await Task.Delay(10000);
        }
    }

    private async Task UpgradeAsync()
    {
        var s = D.Server; if (s == null) return;
        try
        {
            var ids = await Vultr.UpgradesAsync(s.Id);
            var plans = (await Vultr.PlansAsync(s.Region)).Where(p => ids.Contains(p.Id)).ToList();
            if (plans.Count == 0) { MessageBox.Show(FindForm(), Lang.T("而家冇得再升級。"), Program.AppName); return; }
            using var f = new PickForm(Lang.T("升級效能"), Lang.T("揀新 plan（升級後伺服器會重新開機，資料唔會唔見；之後唔可以降級）："), plans.Cast<object>().ToList());
            if (f.ShowDialog(FindForm()) != DialogResult.OK || f.Picked is not VultrPlan np) return;
            await Vultr.ChangePlanAsync(s.Id, np.Id);
            s.Plan = np.Id; CloudStore.Save(); BuildServer();
            Toast.Show(Lang.T("升級緊，伺服器會自動重新開機"), Ui.Success, 4000);
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Program.AppName); }
    }

    private async Task RebootAsync()
    {
        var s = D.Server; if (s == null) return;
        if (MessageBox.Show(FindForm(), Lang.T("重新開機？所有服務會暫停一陣，之後自動開返。"), Program.AppName, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try { await Vultr.RebootAsync(s.Id); Toast.Show(Lang.T("重新開機中…"), Ui.Success, 3000); }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Program.AppName); }
    }

    private async Task DeleteServerAsync()
    {
        var s = D.Server; if (s == null) return;
        if (MessageBox.Show(FindForm(), Lang.T("永久刪除伺服器？\n\n上面所有遊戲世界、bot 同檔案都會一齊刪除，冇得復原。刪除之後 Vultr 就唔會再收費。"), Program.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            await Vultr.DeleteAsync(s.Id);
            D.Server = null; D.Services.Clear(); CloudStore.Save();
            BuildServer(); BuildServices();
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Program.AppName); }
    }

    // ---------------- ③ 服務 ----------------
    private Dictionary<string, string> _status = new();

    private void BuildServices()
    {
        var s = D.Server;
        if (s == null || !s.Ready) { Fill(_svc, Lbl(s == null ? Lang.T("建立伺服器之後就可以加服務。") : Lang.T("等伺服器設定好…"))); return; }
        var rows = new List<Control>();
        foreach (var sv in D.Services.ToList())
        {
            var k = CloudKinds.All.FirstOrDefault(x => x.Key == sv.Kind);
            _status.TryGetValue(sv.Id, out var st);
            bool up = st?.StartsWith("Up") == true;
            string addr = sv.Ports.Count > 0 ? string.Join("、", sv.Ports.Select(p => s.Ip + ":" + p.Replace("/", " "))) : Lang.T("（冇對外 port）");
            var name = Lbl((k?.Icon ?? "•") + " " + sv.Name, Ui.Text, 10.5f);
            var info = Lbl(addr + "　·　" + (st == null ? Lang.T("狀態未知") : up ? Lang.T("● 運行中") : Lang.T("○ 已停止") + "（" + st + "）"), up ? Ui.Success : Ui.TextDim);
            rows.Add(TUi.Col(name, info, TUi.Row(
                TUi.Btn(up ? Lang.T("■ 停止") : Lang.T("▶ 開始"), async () => await DockerAsync(sv, up ? "stop" : "start")),
                TUi.Btn(Lang.T("⟳ 重啟"), async () => await DockerAsync(sv, "restart")),
                TUi.Btn(Lang.T("記錄"), async () => await LogsAsync(sv)),
                TUi.Btn(Lang.T("📂 上載檔案"), async () => await UploadAsync(sv)),
                TUi.Btn(Lang.T("複製位址"), () => { if (sv.Ports.Count > 0) try { Clipboard.SetText(s.Ip + ":" + sv.Ports[0].Split('/')[0]); Toast.Show(Lang.T("已複製"), Ui.Success, 1500); } catch { } }),
                TUi.Btn(Lang.T("🗑 刪除"), async () => await RemoveAsync(sv), 0, BtnKind.Danger))));
        }
        if (rows.Count == 0) rows.Add(Lbl(Lang.T("未有服務：撳「＋ 加服務」開第一個遊戲伺服器或者 Discord bot。")));
        rows.Add(TUi.Row(TUi.Btn(Lang.T("＋ 加服務"), AddService, 0, BtnKind.Primary), TUi.Btn(Lang.T("⟳ 重新整理"), async () => await RefreshServicesAsync(true))));
        Fill(_svc, rows.ToArray());
    }

    private async Task RefreshServicesAsync(bool usage)
    {
        var s = D.Server; if (s is not { Ready: true }) return;
        try { _status = await CloudKinds.StatusAsync(s.Ip); } catch { }
        if (IsDisposed) return;
        BuildServices();
        if (usage) BuildServer();
    }

    private async Task DockerAsync(CloudService sv, string op)
    {
        var s = D.Server; if (s == null) return;
        _busy = true;
        try { var (c, o) = await Ssh.ExecAsync(s.Ip, $"docker {op} rt-{sv.Id}", 180); if (c != 0) MessageBox.Show(FindForm(), o, Program.AppName); }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Program.AppName); }
        finally { _busy = false; }
        await RefreshServicesAsync(false);
    }

    private async Task LogsAsync(CloudService sv)
    {
        var s = D.Server; if (s == null) return;
        using var f = new Form { Text = Lang.T("記錄") + " - " + sv.Name, Size = new Size(Ui.S(900), Ui.S(600)), StartPosition = FormStartPosition.CenterParent, BackColor = Ui.Card, ShowInTaskbar = false };
        var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, BackColor = Ui.Input, ForeColor = Ui.Text, Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.None };
        var reload = new FancyButton { Text = Lang.T("⟳ 重新整理"), Dock = DockStyle.Bottom, Height = Ui.S(36), Kind = BtnKind.Ghost };
        async Task Load()
        {
            box.Text = Lang.T("載入緊…");
            try { var (_, o) = await Ssh.ExecAsync(s.Ip, $"docker logs --tail 400 rt-{sv.Id} 2>&1", 60); box.Text = o.Replace("\n", "\r\n"); box.SelectionStart = box.TextLength; box.ScrollToCaret(); }
            catch (Exception ex) { box.Text = ex.Message; }
        }
        reload.Click += async (_, _) => await Load();
        f.Controls.Add(box); f.Controls.Add(reload);
        f.Shown += async (_, _) => await Load();
        f.ShowDialog(FindForm());
    }

    private async Task UploadAsync(CloudService sv)
    {
        var s = D.Server; if (s == null) return;
        using var d = new OpenFileDialog { Multiselect = true, Title = Lang.T("揀要上載去呢個服務資料夾嘅檔案（例如 mod、地圖、設定檔）") };
        if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
        _busy = true;
        Toast.Show(Lang.T("上載緊…"), Ui.Accent, 2000);
        try
        {
            var (c, o) = await Ssh.UploadAsync(s.Ip, d.FileNames, $"/opt/rabi/{sv.Id}");
            if (c != 0) MessageBox.Show(FindForm(), Lang.T("上載失敗：") + o, Program.AppName);
            else Toast.Show(Lang.T("上載完成；可能要撳「重啟」先生效"), Ui.Success, 4000);
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Program.AppName); }
        finally { _busy = false; }
    }

    private async Task RemoveAsync(CloudService sv)
    {
        var s = D.Server; if (s == null) return;
        if (MessageBox.Show(FindForm(), Lang.F("刪除「{0}」？佢嘅世界存檔同檔案會一齊刪除，冇得復原。", sv.Name), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _busy = true;
        try { await CloudKinds.RemoveAsync(s.Ip, sv); D.Services.Remove(sv); CloudStore.Save(); }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Program.AppName); }
        finally { _busy = false; }
        await RefreshServicesAsync(false);
    }

    private void AddService()
    {
        var s = D.Server; if (s == null) return;
        using var dlg = new CloudAddDialog();
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK || dlg.Result is not { } r) return;
        _ = CreateServiceAsync(r.Service, r.Upload, r.Unzip);
    }

    private async Task CreateServiceAsync(CloudService sv, List<string>? upload, bool unzip)
    {
        var s = D.Server; if (s == null) return;
        _busy = true;
        Fill(_svc, Lbl(Lang.F("⏳ 建立緊「{0}」…（第一次要下載伺服器檔案，可能要幾分鐘）", sv.Name), Ui.Text));
        try
        {
            await CloudKinds.CreateAsync(s.Ip, sv, upload, unzip);
            D.Services.Add(sv); CloudStore.Save();
            Toast.Show(Lang.F("「{0}」已經開始", sv.Name), Ui.Success, 4000);
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), Lang.T("建立失敗：") + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally
        {
            _busy = false;
            if (upload != null && unzip) foreach (var f in upload) try { File.Delete(f); } catch { }
        }
        await RefreshServicesAsync(false);
    }
}

/// <summary>由清單揀一樣（升級 plan 用）。</summary>
internal sealed class PickForm : Form
{
    private readonly ComboBox _box = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    public object? Picked => _box.SelectedItem;

    public PickForm(string title, string text, List<object> items)
    {
        Text = title; FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        BackColor = Ui.Card; ForeColor = Ui.Text; AutoScaleMode = AutoScaleMode.None; Font = Ui.F(9.75f);
        ClientSize = new Size(Ui.S(520), Ui.S(170));
        var lbl = new Label { Text = text, AutoSize = false, Bounds = new Rectangle(Ui.S(16), Ui.S(14), Ui.S(488), Ui.S(44)), ForeColor = Ui.TextDim };
        foreach (var i in items) _box.Items.Add(i);
        if (_box.Items.Count > 0) _box.SelectedIndex = 0;
        _box.Bounds = new Rectangle(Ui.S(16), Ui.S(64), Ui.S(488), Ui.S(30));
        var ok = new FancyButton { Text = Lang.T("確定"), Kind = BtnKind.Primary, Bounds = new Rectangle(Ui.S(304), Ui.S(116), Ui.S(96), Ui.S(34)) };
        var cancel = new FancyButton { Text = Lang.T("取消"), Kind = BtnKind.Ghost, Bounds = new Rectangle(Ui.S(408), Ui.S(116), Ui.S(96), Ui.S(34)) };
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => Close();
        Controls.AddRange(new Control[] { lbl, _box, ok, cancel });
    }
}

/// <summary>「加服務」視窗：揀類型、填設定、（bot）揀資料夾。</summary>
internal sealed class CloudAddDialog : Form
{
    public (CloudService Service, List<string>? Upload, bool Unzip)? Result { get; private set; }
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _desc = new() { AutoSize = false, ForeColor = Ui.TextDim };
    private readonly Panel _fields = new() { AutoScroll = true };
    private readonly Dictionary<string, TextBox> _boxes = new();
    private readonly TextBox _name = new();
    private string? _folder;
    private readonly Label _folderLbl = new() { AutoSize = false, ForeColor = Ui.TextDim };
    private readonly FancyButton _pick;

    public CloudAddDialog()
    {
        Text = Lang.T("加服務"); FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        BackColor = Ui.Card; ForeColor = Ui.Text; AutoScaleMode = AutoScaleMode.None; Font = Ui.F(9.75f);
        ClientSize = new Size(Ui.S(540), Ui.S(520));
        foreach (var k in CloudKinds.All) _kind.Items.Add(k);
        _kind.Bounds = new Rectangle(Ui.S(16), Ui.S(16), Ui.S(508), Ui.S(30));
        _desc.Bounds = new Rectangle(Ui.S(16), Ui.S(52), Ui.S(508), Ui.S(40));
        var nl = new Label { Text = Lang.T("顯示名稱"), AutoSize = false, Bounds = new Rectangle(Ui.S(16), Ui.S(98), Ui.S(170), Ui.S(24)), ForeColor = Ui.TextDim };
        _name.Bounds = new Rectangle(Ui.S(190), Ui.S(96), Ui.S(334), Ui.S(28));
        _fields.Bounds = new Rectangle(Ui.S(16), Ui.S(134), Ui.S(508), Ui.S(250));
        _pick = new FancyButton { Text = Lang.T("📁 揀 bot 資料夾…"), Kind = BtnKind.Ghost, Bounds = new Rectangle(Ui.S(16), Ui.S(392), Ui.S(170), Ui.S(32)) };
        _folderLbl.Bounds = new Rectangle(Ui.S(194), Ui.S(398), Ui.S(330), Ui.S(24));
        _pick.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog();
            if (d.ShowDialog(this) == DialogResult.OK) { _folder = d.SelectedPath; _folderLbl.Text = _folder; }
        };
        var ok = new FancyButton { Text = Lang.T("建立"), Kind = BtnKind.Primary, Bounds = new Rectangle(Ui.S(324), Ui.S(470), Ui.S(96), Ui.S(34)) };
        var cancel = new FancyButton { Text = Lang.T("取消"), Kind = BtnKind.Ghost, Bounds = new Rectangle(Ui.S(428), Ui.S(470), Ui.S(96), Ui.S(34)) };
        ok.Click += (_, _) => Done();
        cancel.Click += (_, _) => Close();
        foreach (var t in new[] { _name }) { t.BackColor = Ui.Input; t.ForeColor = Ui.Text; t.BorderStyle = BorderStyle.FixedSingle; }
        Controls.AddRange(new Control[] { _kind, _desc, nl, _name, _fields, _pick, _folderLbl, ok, cancel });
        _kind.SelectedIndexChanged += (_, _) => ShowKind();
        _kind.SelectedIndex = 0;
    }

    private CloudKind K => (CloudKind)_kind.SelectedItem!;

    private void ShowKind()
    {
        var k = K;
        _desc.Text = k.Desc + (k.Ports.Length > 0 ? "\n" + Lang.T("用 port：") + string.Join("、", k.Ports) : "");
        _name.Text = k.Name;
        _fields.Controls.Clear(); _boxes.Clear();
        int y = 0;
        foreach (var f in k.Fields)
        {
            var l = new Label { Text = f.Label, AutoSize = false, Bounds = new Rectangle(0, y + Ui.S(2), Ui.S(170), Ui.S(24)), ForeColor = Ui.TextDim };
            var t = new TextBox { Text = f.Default, UseSystemPasswordChar = f.Secret, Bounds = new Rectangle(Ui.S(174), y, Ui.S(310), Ui.S(28)), BackColor = Ui.Input, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle };
            _fields.Controls.Add(l); _fields.Controls.Add(t); _boxes[f.Key] = t;
            y += Ui.S(36);
        }
        bool bot = k.Key.StartsWith("bot");
        _pick.Visible = _folderLbl.Visible = bot;
    }

    private void Done()
    {
        var k = K;
        var sv = new CloudService { Id = Guid.NewGuid().ToString("N")[..8], Kind = k.Key, Name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : k.Name };
        foreach (var (key, box) in _boxes) sv.Opt[key] = box.Text.Trim();
        var others = CloudStore.Data.Services;
        try
        {
            if (k.Key == "custom")
            {
                if (sv.Opt.GetValueOrDefault("image", "").Length == 0) { MessageBox.Show(this, Lang.T("請填映像名稱。")); return; }
                var ports = sv.Opt.GetValueOrDefault("ports", "").Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Contains('/') ? p.ToLowerInvariant() : p + "/tcp").ToList();
                if (ports.Any(p => !int.TryParse(p.Split('/')[0], out var n) || n < 1 || n > 65535 || n == 22)) { MessageBox.Show(this, Lang.T("Port 格式唔啱（例如 8080/tcp）。")); return; }
                if (ports.Any(p => others.Any(o => o.Ports.Contains(p)))) { MessageBox.Show(this, Lang.T("有 port 已經俾其他服務用緊。")); return; }
                sv.Ports = ports;
            }
            else sv.Ports = CloudKinds.AllocPorts(k.Ports, others);
            if (k.Key == "valheim" && sv.Opt.GetValueOrDefault("pass", "").Length < 5) { MessageBox.Show(this, Lang.T("Valheim 密碼最少要 5 個字。")); return; }
            if (k.Key.StartsWith("bot"))
            {
                if (sv.Opt.GetValueOrDefault("token", "").Length == 0) { MessageBox.Show(this, Lang.T("請填 Discord Bot Token。")); return; }
                if (_folder == null) { MessageBox.Show(this, Lang.T("請揀 bot 資料夾。")); return; }
                Result = (sv, new List<string> { CloudKinds.ZipFolder(_folder) }, true);
            }
            else Result = (sv, null, false);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Program.AppName); }
    }
}
