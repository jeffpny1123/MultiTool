using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool;

/// <summary>
/// 回報問題：用家寫低發生咩事，連埋（佢同意先附上嘅）系統資料同最近嘅錯誤紀錄，寄去開發者電郵。
/// 冇自己嘅伺服器，所以用用家自己嘅電郵寄：裝咗「電子郵件」工具又登入咗帳戶就直接寄；否則開佢部機嘅電郵程式，或者複製報告。
/// 寄出之前用家可以預覽晒會寄出嘅內容；錯誤紀錄入面嘅使用者資料夾路徑會遮住。
/// </summary>
internal sealed class ReportDialog : Form
{
    private readonly ComboBox _kind, _acct;
    private readonly TextBox _desc, _contact;
    private readonly CheckBox _sys, _log;
    private readonly Label _status;
    private readonly FancyButton _send, _mailto, _copy;
    private readonly List<Mail.MailAccount> _accounts = new();

    public ReportDialog()
    {
        Text = Lang.T("回報問題 / 功能建議");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.F(9.75f);
        int W = Ui.S(560), p = Ui.S(16), w = W - p * 2, y = Ui.S(14);

        Controls.Add(new Label
        {
            Text = Lang.F("遇到錯誤，或者想要新功能？寫低寄俾開發者 Jeff（{0}），睇到會立即更新。", Program.ContactEmail),
            ForeColor = Ui.TextDim, Font = Ui.F(9f), AutoSize = false, Bounds = new Rectangle(p, y, w, Ui.S(36))
        });
        y += Ui.S(40);
        _kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Ui.Input, ForeColor = Ui.Text, FlatStyle = FlatStyle.Flat, Font = Ui.F(9.75f), Bounds = new Rectangle(p, y, Ui.S(170), Ui.S(28)) };
        _kind.Items.AddRange(new object[] { Lang.T("回報問題"), Lang.T("功能建議"), Lang.T("其他") });
        _kind.SelectedIndex = 0;
        Controls.Add(_kind);
        y += Ui.S(36);
        _desc = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, BackColor = Ui.Input, ForeColor = Ui.Text, Font = Ui.F(10f),
            PlaceholderText = Lang.T("發生咩事？你做咗啲乜、預期會點、實際點？越具體越易搵到問題。"), Bounds = new Rectangle(p, y, w, Ui.S(140))
        };
        Controls.Add(_desc);
        y += Ui.S(148);
        _contact = new TextBox { BorderStyle = BorderStyle.FixedSingle, BackColor = Ui.Input, ForeColor = Ui.Text, Font = Ui.F(10f), PlaceholderText = Lang.T("你嘅電郵（選填，想收到回覆先填）"), Bounds = new Rectangle(p, y, w, Ui.S(26)) };
        Controls.Add(_contact);
        y += Ui.S(36);
        _sys = new CheckBox { Text = Lang.T("附上系統資料（程式版本、Windows 版本、語言、記憶體、顯示卡）"), Checked = true, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, Location = new Point(p, y) };
        Controls.Add(_sys);
        y += Ui.S(26);
        _log = new CheckBox { Text = Lang.T("附上最近嘅錯誤紀錄（幫到手搵原因；使用者資料夾路徑會遮住）"), Checked = true, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, Location = new Point(p, y) };
        Controls.Add(_log);
        y += Ui.S(30);
        // 揀「功能建議」：提示字轉做問想要咩功能，亦唔使附錯誤紀錄
        _kind.SelectedIndexChanged += (_, _) =>
        {
            bool idea = _kind.SelectedIndex == 1;
            _desc.PlaceholderText = idea ? Lang.T("你想加咩功能，或者邊度可以做得更好？講埋你會點用，會更易做啱。")
                                         : Lang.T("發生咩事？你做咗啲乜、預期會點、實際點？越具體越易搵到問題。");
            _log.Checked = !idea;
        };
        var preview = new FancyButton { Text = Lang.T("預覽會寄出嘅內容"), Kind = BtnKind.Ghost, Font = Ui.F(9.25f), Bounds = new Rectangle(p, y, Ui.S(190), Ui.S(30)) };
        preview.Click += (_, _) => ShowPreview();
        Controls.Add(preview);
        y += Ui.S(42);

        // 寄出方法
        try { if (Extensions.IsInstalled(Extensions.Mail)) _accounts.AddRange(Mail.MailStore.All); } catch { }
        _acct = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Ui.Input, ForeColor = Ui.Text, FlatStyle = FlatStyle.Flat, Font = Ui.F(9.75f), Bounds = new Rectangle(p, y + Ui.S(3), Ui.S(260), Ui.S(28)), Visible = _accounts.Count > 0 };
        foreach (var a in _accounts) _acct.Items.Add(a.Email);
        if (_accounts.Count > 0) _acct.SelectedIndex = 0;
        _send = new FancyButton { Text = Lang.T("用呢個帳戶寄出"), Kind = BtnKind.Primary, Font = Ui.F(9.5f), Bounds = new Rectangle(p + Ui.S(268), y, Ui.S(170), Ui.S(34)), Visible = _accounts.Count > 0 };
        _send.Click += async (_, _) => await SendAsync();
        Controls.Add(_acct); Controls.Add(_send);
        if (_accounts.Count > 0) y += Ui.S(44);
        _mailto = new FancyButton { Text = Lang.T("用電郵程式寄出"), Kind = _accounts.Count > 0 ? BtnKind.Ghost : BtnKind.Primary, Font = Ui.F(9.5f), Bounds = new Rectangle(p, y, Ui.S(170), Ui.S(34)) };
        _mailto.Click += (_, _) => OpenMailApp();
        _copy = new FancyButton { Text = Lang.T("複製報告"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Bounds = new Rectangle(p + Ui.S(178), y, Ui.S(130), Ui.S(34)) };
        _copy.Click += (_, _) => { if (Check()) { try { Clipboard.SetText(Build(true)); Say(Lang.F("已複製。請貼入電郵寄去 {0}", Program.ContactEmail), Ui.Success); } catch { } } };
        Controls.Add(_mailto); Controls.Add(_copy);
        y += Ui.S(42);
        _status = new Label { ForeColor = Ui.TextDim, Font = Ui.F(9f), AutoSize = false, Bounds = new Rectangle(p, y, w, Ui.S(38)) };
        Controls.Add(_status);
        ClientSize = new Size(W, y + Ui.S(44));
    }

    private void Say(string s, Color c) { _status.ForeColor = c; _status.Text = s; }

    private bool Check()
    {
        if (_desc.Text.Trim().Length >= 5) return true;
        Say(Lang.T("請先寫低內容。"), Ui.Danger);
        _desc.Focus();
        return false;
    }

    private string Subject()
    {
        string first = _desc.Text.Trim().Replace("\r", " ").Replace("\n", " ");
        if (first.Length > 60) first = first[..60] + "…";
        string kind = _kind.SelectedIndex == 0 ? "Bug" : _kind.SelectedIndex == 1 ? "Suggestion" : "Other";
        return $"[Rabi Toolkit {Program.Version}{(Program.IsSteam ? " Steam" : "")}] {kind}: {first}";
    }

    private static string SystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"App: Rabi Toolkit {Program.Version} (build {Program.Build}){(Program.IsSteam ? " Steam" : "")}");
        sb.AppendLine($"Windows: {Environment.OSVersion.Version} {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}");
        sb.AppendLine($"Language: {Lang.Code(Lang.Current)} / system {System.Globalization.CultureInfo.CurrentUICulture.Name}");
        try { sb.AppendLine($"RAM: {Ai.LocalAi.RamGb:0} GB; GPU: {Ai.LocalAi.GpuName} ({Ai.LocalAi.VramGb:0.#} GB)"); } catch { }
        try { sb.AppendLine("Tools: " + string.Join(", ", Extensions.All.Where(e => Extensions.IsInstalled(e.Id)).Select(e => e.Id))); } catch { }
        try { sb.AppendLine("AI: " + (Ai.AiProvider.LoadCurrent()?.Key ?? "none")); } catch { }
        return sb.ToString();
    }

    /// <summary>error.log 最尾 200 行（最多 40 KB）；使用者資料夾路徑同使用者名遮住。</summary>
    private static string RecentLog()
    {
        try
        {
            string f = AppPaths.ErrorLog;
            if (!File.Exists(f)) return "";
            var lines = File.ReadAllLines(f);
            string text = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 200)));
            if (text.Length > 40000) text = text[^40000..];
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), user = Environment.UserName;
            if (home.Length > 3) text = text.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            if (user.Length >= 3) text = text.Replace("\\" + user + "\\", "\\<user>\\", StringComparison.OrdinalIgnoreCase);
            return text;
        }
        catch { return ""; }
    }

    /// <summary>成份報告嘅文字。withLog = 連埋錯誤紀錄（直接寄嗰陣錯誤紀錄會做附件，唔放入內文）。</summary>
    private string Build(bool withLog)
    {
        var sb = new StringBuilder();
        sb.AppendLine(_desc.Text.Trim());
        sb.AppendLine();
        if (_contact.Text.Trim().Length > 0) sb.AppendLine("Reply to: " + _contact.Text.Trim());
        if (_sys.Checked) { sb.AppendLine("---- System ----"); sb.Append(SystemInfo()); }
        if (withLog && _log.Checked)
        {
            string log = RecentLog();
            if (log.Length > 0) { sb.AppendLine("---- error.log ----"); sb.AppendLine(log); }
        }
        return sb.ToString();
    }

    private void ShowPreview()
    {
        using var d = new Form
        {
            Text = Lang.T("預覽會寄出嘅內容"), StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(Ui.S(720), Ui.S(520)), BackColor = Ui.Card,
            MinimizeBox = false, ShowInTaskbar = false
        };
        d.Controls.Add(new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            BackColor = Ui.Input, ForeColor = Ui.Text, Font = Ui.Mono(9.5f), Text = ("Subject: " + Subject() + "\n\n" + Build(true)).Replace("\r\n", "\n").Replace("\n", "\r\n")
        });
        d.ShowDialog(this);
    }

    private async Task SendAsync()
    {
        if (!Check() || _acct.SelectedIndex < 0) return;
        var a = _accounts[_acct.SelectedIndex];
        _send.Enabled = false;
        Say(Lang.T("寄緊…"), Ui.TextDim);
        string? tmp = null;
        try
        {
            var files = new List<string>();
            if (_log.Checked && RecentLog() is { Length: > 0 } log)
            {
                tmp = Path.Combine(Path.GetTempPath(), "RabiToolkit-error-log.txt");
                File.WriteAllText(tmp, log, new UTF8Encoding(true));
                files.Add(tmp);
            }
            var o = new Mail.Mime.Outgoing(a.Name, a.Email, Program.ContactEmail, "", "", Subject(), Build(false), files);
            var msg = await Task.Run(() => Mail.Mime.Build(o));
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Mail.Smtp.SendAsync(a, new[] { Program.ContactEmail }, msg, cts.Token);
            Tools.Toast.Show(Lang.T("已寄出，多謝你嘅回報！"), Ui.Success, 3000);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("回報問題", ex);
            Say(Lang.T("寄唔到：") + ex.Message + "　" + Lang.T("可以改用「用電郵程式寄出」或者「複製報告」。"), Ui.Danger);
            _send.Enabled = true;
        }
        finally { try { if (tmp != null) File.Delete(tmp); } catch { } }
    }

    private void OpenMailApp()
    {
        if (!Check()) return;
        // mailto 連結有長度限制：內文放描述同系統資料；成份報告（連錯誤紀錄）複製埋去剪貼簿，用家可以喺信入面貼上
        string full = Build(true), body = Build(false);
        if (body.Length > 1400) body = body[..1400] + "…";
        bool hasLog = _log.Checked && full.Length > Build(false).Length;
        if (hasLog) body += "\n\n" + "(error log: paste from clipboard / 請喺呢度貼上錯誤紀錄)";
        try { Clipboard.SetText(full); } catch { }
        try
        {
            Process.Start(new ProcessStartInfo("mailto:" + Program.ContactEmail + "?subject=" + Uri.EscapeDataString(Subject()) + "&body=" + Uri.EscapeDataString(body.Replace("\r\n", "\n").Replace("\n", "\r\n"))) { UseShellExecute = true });
            Say(hasLog ? Lang.T("已開啟電郵程式。成份報告（連錯誤紀錄）已經複製咗，請喺信入面撳 Ctrl+V 貼上再寄出。") : Lang.T("已開啟電郵程式，請撳寄出。"), Ui.Success);
        }
        catch
        {
            Say(Lang.F("開唔到電郵程式。報告已經複製咗，請自己開電郵貼上，寄去 {0}", Program.ContactEmail), Ui.Danger);
        }
    }
}
