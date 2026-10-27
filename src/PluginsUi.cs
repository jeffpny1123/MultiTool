using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using MultiTool.Tools;
using MultiTool.UI;

namespace MultiTool.Plugins;

// =====================================================================
//  插件工房：揀插件、用插件、寫插件（程式碼編輯器 + 即時試行 + AI 幫手）
// =====================================================================

/// <summary>程式碼編輯器嘅 WebView2（CodeMirror，包咗喺 MultiTool.dll 入面）。</summary>
internal sealed class EditorWeb
{
    private static CoreWebView2Environment? _env;
    private CoreWebView2Controller _ctl = null!;

    public static async Task<EditorWeb> CreateAsync(Control host, Action<JsonObject> onMsg)
    {
        var w = new EditorWeb();
        if (_env == null)
        {
            string data = Path.Combine(AppPaths.Data, "webview", "editor");
            Directory.CreateDirectory(data);
            _env = await CoreWebView2Environment.CreateAsync(null, data);
        }
        w._ctl = await _env.CreateCoreWebView2ControllerAsync(host.Handle);
        w._ctl.DefaultBackgroundColor = EditorWeb.Back;
        w._ctl.Bounds = host.ClientRectangle;
        host.Resize += (_, _) => { try { w._ctl.Bounds = host.ClientRectangle; } catch { } };
        var wv = w._ctl.CoreWebView2;
        wv.Settings.AreDevToolsEnabled = false;
        wv.Settings.IsStatusBarEnabled = false;
        wv.Settings.AreDefaultContextMenusEnabled = true;
        wv.Settings.AreBrowserAcceleratorKeysEnabled = false;
        wv.NavigationStarting += (_, e) => { if (!(e.Uri ?? "").StartsWith("data:") && !(e.Uri ?? "").StartsWith("about:")) e.Cancel = true; };
        wv.NewWindowRequested += (_, e) => e.Handled = true;
        wv.WebMessageReceived += (_, e) =>
        {
            try { if (JsonNode.Parse(e.WebMessageAsJson) is JsonObject o) onMsg(o); } catch { }
        };
        wv.NavigateToString(EditorHtml());
        return w;
    }

    public void Post(JsonObject o) { try { _ctl.CoreWebView2.PostWebMessageAsJson(o.ToJsonString()); } catch { } }
    public void Focus() { try { _ctl.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch { } }

    private static string Res(string name)
    {
        using var s = typeof(EditorWeb).Assembly.GetManifestResourceStream(name);
        if (s == null) return "";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>編輯器底色（跟程式主題）。</summary>
    public static Color Back => Ui.Light ? Ui.Hex(0xFFFFFF) : Ui.Hex(0x212121);

    private static string EditorHtml() =>
        "<!DOCTYPE html><html><head><meta charset='utf-8'><style>" + Res("cm.css") +
        "html,body{margin:0;height:100%;background:" + (Ui.Light ? "#ffffff" : "#212121") + ";overflow:hidden}" +
        ".CodeMirror{height:100%;font-family:'Cascadia Mono',Consolas,'Microsoft JhengHei UI',monospace;font-size:14px;line-height:1.45}" +
        ".CodeMirror-activeline-background{background:" + (Ui.Light ? "#eef3fb" : "#2a2a2a") + "!important}" +
        (Ui.Light ? ".CodeMirror-gutters{background:#f6f7f9;border-right:1px solid #e3e6eb}.CodeMirror-linenumber{color:#9aa3b2}" : "") +
        "</style><script>" + Res("cm.js") + "</script></head><body><script>window.MT_THEME='" + (Ui.Light ? "default" : "material-darker") + "';" + """
const post = o => window.chrome.webview.postMessage(o);
const ed = CodeMirror(document.body, {
  theme: window.MT_THEME, lineNumbers: true, autoCloseBrackets: true, matchBrackets: true, autoCloseTags: true,
  styleActiveLine: true, indentUnit: 2, tabSize: 2, indentWithTabs: false, lineWrapping: false,
  extraKeys: {
    'Ctrl-S': () => post({ type: 'save', text: ed.getValue() }),
    'F5': () => post({ type: 'run', text: ed.getValue() }),
    'Ctrl-/': 'toggleComment',
    'Tab': cm => cm.somethingSelected() ? cm.indentSelection('add') : cm.replaceSelection('  ', 'end'),
    'Shift-Tab': cm => cm.indentSelection('subtract')
  }
});
let silent = false, timer = null;
ed.on('change', () => {
  if (silent) return;
  clearTimeout(timer);
  timer = setTimeout(() => post({ type: 'change', text: ed.getValue() }), 300);
});
window.chrome.webview.addEventListener('message', e => {
  const m = e.data;
  if (m.type === 'set') {
    silent = true;
    ed.setOption('mode', m.mode);
    ed.setOption('readOnly', !!m.readOnly);
    ed.setValue(m.text);
    ed.clearHistory();
    silent = false;
    ed.refresh(); ed.focus();
  } else if (m.type === 'get') {
    clearTimeout(timer);
    post({ type: 'text', text: ed.getValue(), seq: m.seq });
  }
});
post({ type: 'ready' });
""" + "</script></body></html>";
}

/// <summary>包住 EditorWeb 嘅格（冇 WebView2 就用普通文字框）。</summary>
internal sealed class CodeBox : Panel
{
    private object? _web;
    private TextBox? _plain;
    private bool _ready;
    private (string Text, object Mode, bool Ro)? _pending;
    private int _seq;
    private readonly Dictionary<int, TaskCompletionSource<string>> _waits = new();
    private string _plainText = "";

    public event Action<string>? TextChanged2;
    public event Action<string>? SaveRequested;
    public event Action<string>? RunRequested;

    public CodeBox()
    {
        BackColor = EditorWeb.Back;
        Dock = DockStyle.Fill;
    }

    public async Task StartAsync()
    {
        if (Mail.WebView2Lib.Prepare())
        {
            try { _web = await Create(); return; }
            catch (Exception ex) { AppPaths.LogError("程式碼編輯器", ex); }
        }
        // 後備：普通文字框
        _plain = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, AcceptsTab = true, AcceptsReturn = true, Dock = DockStyle.Fill,
            BackColor = EditorWeb.Back, ForeColor = Ui.Text, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 11f)
        };
        _plain.TextChanged += (_, _) => TextChanged2?.Invoke(_plain.Text);
        _plain.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; SaveRequested?.Invoke(_plain.Text); }
            else if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; RunRequested?.Invoke(_plain.Text); }
        };
        Controls.Add(_plain);
        _ready = true;
        if (_pending is { } p) SetText(p.Text, p.Mode, p.Ro);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<object> Create() => await EditorWeb.CreateAsync(this, OnMsg);

    private void OnMsg(JsonObject o)
    {
        string type = o["type"]?.GetValue<string>() ?? "";
        string text = o["text"]?.GetValue<string>() ?? "";
        switch (type)
        {
            case "ready":
                _ready = true;
                if (_pending is { } p) SetText(p.Text, p.Mode, p.Ro);
                break;
            case "change": TextChanged2?.Invoke(text); break;
            case "save": TextChanged2?.Invoke(text); SaveRequested?.Invoke(text); break;
            case "run": TextChanged2?.Invoke(text); RunRequested?.Invoke(text); break;
            case "text":
                int seq = o["seq"]?.GetValue<int>() ?? 0;
                if (_waits.Remove(seq, out var tcs)) tcs.TrySetResult(text);
                break;
        }
    }

    /// <summary>mode：CodeMirror 嘅 mode（字串或者物件）。</summary>
    public void SetText(string text, object mode, bool readOnly = false)
    {
        _pending = (text, mode, readOnly);
        if (!_ready) return;
        if (_plain != null) { _plain.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"); _plain.ReadOnly = readOnly; return; }
        var m = mode is string ms ? (JsonNode)ms : JsonSerializer.SerializeToNode(mode)!;
        PostWeb(new JsonObject { ["type"] = "set", ["text"] = text, ["mode"] = m, ["readOnly"] = readOnly });
    }

    /// <summary>攞返編輯器而家嘅內容（未傳返嚟嘅改動都包埋）。</summary>
    public async Task<string?> GetTextAsync()
    {
        if (_plain != null) return _plain.Text.Replace("\r\n", "\n");
        if (_web == null || !_ready) return null;
        int seq = ++_seq;
        var tcs = new TaskCompletionSource<string>();
        _waits[seq] = tcs;
        PostWeb(new JsonObject { ["type"] = "get", ["seq"] = seq });
        var done = await Task.WhenAny(tcs.Task, Task.Delay(1500));
        _waits.Remove(seq);
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PostWeb(JsonObject o) => (_web as EditorWeb)?.Post(o);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void FocusEditor() { if (_plain != null) _plain.Focus(); else (_web as EditorWeb)?.Focus(); }

    public static object ModeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".js" or ".mjs" => "javascript",
        ".json" => new Dictionary<string, object> { ["name"] = "javascript", ["json"] = true },
        ".html" or ".htm" => "htmlmixed",
        ".css" => "css",
        ".xml" or ".svg" => "xml",
        _ => "text/plain"
    };
}

/// <summary>大廳一格插件卡（右下角有「加到工具欄」掣）。</summary>
internal sealed class PluginCard : Control
{
    public PluginManifest M { get; }
    private bool _hover, _hoverBtn;
    public event Action? Open, PinToggle;

    public PluginCard(PluginManifest m)
    {
        M = m;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(Ui.S(280), Ui.S(140));
        Margin = new Padding(0, 0, Ui.S(14), Ui.S(14));
        Cursor = Cursors.Hand;
        BackColor = Ui.Bg;
    }

    private bool Pinned => PluginStore.IsPinned(M.Id);
    private string BtnText => Pinned ? Lang.T("✓ 已加到工具欄") : Lang.T("📌 加到工具欄");

    private Rectangle BtnRect()
    {
        int p = Ui.S(12);
        int w = TextRenderer.MeasureText(BtnText, Ui.F(8.75f)).Width + Ui.S(20);
        return new Rectangle(Width - p - w, Height - p - Ui.S(28), w, Ui.S(28));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _hoverBtn = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool hb = BtnRect().Contains(e.Location);
        if (hb != _hoverBtn) { _hoverBtn = hb; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (BtnRect().Contains(e.Location)) PinToggle?.Invoke();
        else Open?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Ui.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Ui.Round(r, Ui.S(12f)))
        {
            using var b = new SolidBrush(_hover ? Ui.CardHi : Ui.Card);
            g.FillPath(b, path);
            using var pen = new Pen(_hover ? Ui.BorderHi : Ui.Border);
            g.DrawPath(pen, path);
        }
        int p = Ui.S(14);
        TextRenderer.DrawText(g, M.Icon, Ui.F(18f), new Rectangle(p, p, Ui.S(40), Ui.S(40)), Ui.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        var tf = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        int x = p + Ui.S(50);
        TextRenderer.DrawText(g, M.Name, Ui.F(10.5f, FontStyle.Bold), new Rectangle(x, p, Width - x - p, Ui.S(22)), Ui.Text, tf);
        string sub = "v" + M.Version + (M.Author.Length > 0 ? " · " + M.Author : "");
        TextRenderer.DrawText(g, sub, Ui.F(8.5f), new Rectangle(x, p + Ui.S(22), Width - x - p, Ui.S(18)), Ui.TextFaint, tf);
        var br = BtnRect();
        string desc = M.Description.Length > 0 ? M.Description : Lang.T("（冇介紹）");
        TextRenderer.DrawText(g, desc, Ui.F(9f), new Rectangle(p, Ui.S(60), Width - 2 * p, br.Top - Ui.S(64)), Ui.TextDim,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        // 加到工具欄掣
        bool pinned = Pinned;
        using (var path = Ui.Round(br, Ui.S(7f)))
        {
            var fill = pinned ? Ui.Alpha(Ui.Success, _hoverBtn ? 70 : 40) : _hoverBtn ? Ui.Mix(Ui.Accent, Color.White, 0.12f) : Ui.Accent;
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
        }
        TextRenderer.DrawText(g, BtnText, Ui.F(8.75f, FontStyle.Bold), br, pinned ? Ui.Success : Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// 插件工房：大廳（一格格插件）→ 詳情（介紹、權限、即時預覽、加到工具欄）→ 編寫（程式碼 + 試行 + AI）。
/// </summary>
internal sealed class PluginsPage : Panel
{
    public static Control Build() => new PluginsPage();

    private enum View { Lobby, Detail, Edit }
    private View _view = View.Lobby;
    private PluginManifest? _cur;

    // 大廳
    private readonly Panel _lobby = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private readonly FlowLayoutPanel _grid = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg, AutoScroll = true, Padding = new Padding(0, Ui.S(4), 0, 0) };
    private readonly Label _empty;

    // 詳情
    private readonly Panel _detail = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg, Visible = false };
    private readonly Label _dTitle, _dInfo;
    private readonly FancyButton _pinBtn;
    private readonly Panel _dPreview = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private PluginView? _preview;

    // 編寫
    private readonly Panel _edit = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg, Visible = false };
    private readonly Label _eTitle;
    private readonly FancyButton _saveBtn;
    private readonly FlowLayoutPanel _tabs;
    private readonly CodeBox _code = new();
    private readonly Panel _devHost = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private readonly TextBox _console;
    private PluginView? _dev;
    private string _file = "ui.html";
    private bool _codeStarted, _loading;
    private readonly Dictionary<string, string> _buf = new();     // 未儲存嘅改動
    private readonly PluginAssist _assist;
    private readonly ToolTip _tip = new();

    private PluginsPage()
    {
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        Padding = new Padding(Ui.S(24), Ui.S(16), Ui.S(20), Ui.S(14));

        FancyButton Btn(Control host, string t, Action a, string? tip = null, BtnKind k = BtnKind.Ghost)
        {
            var b = TUi.Btn(t, a, 0, k);
            b.Margin = new Padding(0, 0, Ui.S(8), 0);
            if (tip != null) _tip.SetToolTip(b, tip);
            host.Controls.Add(b);
            return b;
        }
        FlowLayoutPanel Bar() => new() { Dock = DockStyle.Top, Height = Ui.S(48), BackColor = Ui.Bg, WrapContents = false };
        Label Title(Control host)
        {
            var l = Ui.Text_("", Ui.Text, Ui.F(13f, FontStyle.Bold), new Padding(0, Ui.S(4), Ui.S(16), 0));
            host.Controls.Add(l);
            return l;
        }

        // ---------- 大廳 ----------
        var lBar = Bar();
        var lTitle = Title(lBar);
        lTitle.Text = Lang.T("工作坊");
        Btn(lBar, Lang.T("+ 新插件"), NewPlugin, null, BtnKind.Primary);
        Btn(lBar, Lang.T("📥 匯入插件…"), ImportPlugin, Lang.T("匯入其他人整嘅 .mtplugin 檔"));
        var lHint = Ui.Text_(Lang.T("撳一個插件睇介紹同預覽；想常用就「加到左側工具欄」。"), Ui.TextFaint, Ui.F(9f), new Padding(0));
        lHint.AutoSize = false; lHint.Dock = DockStyle.Top; lHint.Height = Ui.S(30);
        _empty = Ui.Text_(Lang.T("撳上面「+ 新插件」開始寫你自己嘅工具。\n\n插件用 HTML + JavaScript 寫，可以連網上 API、彈通知、儲存資料、叫 AI 幫手…\n唔識寫 code？入去編寫之後撳「✦ AI 幫我寫」同 AI 傾就得。"),
            Ui.TextDim, Ui.F(10.5f), new Padding(0));
        _empty.AutoSize = false; _empty.Dock = DockStyle.Fill; _empty.TextAlign = ContentAlignment.MiddleCenter; _empty.Visible = false;
        _lobby.Controls.Add(_grid);
        _lobby.Controls.Add(_empty);
        _lobby.Controls.Add(lHint);
        _lobby.Controls.Add(lBar);

        // ---------- 詳情 ----------
        var dBar = Bar();
        Btn(dBar, Lang.T("← 返回"), () => GoLobby());
        _dTitle = Title(dBar);
        _pinBtn = Btn(dBar, "", TogglePin, Lang.T("加咗之後喺左邊工具欄有自己一格，一開 MultiTool 就會運行"), BtnKind.Primary);
        Btn(dBar, Lang.T("✎ 編寫"), () => GoEdit());
        Btn(dBar, Lang.T("📤 匯出"), ExportCurrent, Lang.T("存做 .mtplugin 檔，俾其他人匯入"));
        Btn(dBar, Lang.T("🗑 刪除"), DeleteCurrent);
        _dInfo = Ui.Text_("", Ui.TextDim, Ui.F(9.5f), new Padding(0));
        _dInfo.AutoSize = false; _dInfo.Dock = DockStyle.Left; _dInfo.Width = Ui.S(300); _dInfo.UseMnemonic = false;
        _dInfo.Padding = new Padding(0, Ui.S(8), Ui.S(16), 0);
        var dPrevHead = Ui.Text_(Lang.T("預覽"), Ui.TextFaint, Ui.F(9f, FontStyle.Bold), new Padding(0));
        dPrevHead.AutoSize = false; dPrevHead.Dock = DockStyle.Top; dPrevHead.Height = Ui.S(26); dPrevHead.TextAlign = ContentAlignment.BottomLeft;
        var dPrevWrap = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Border, Padding = new Padding(1) };
        dPrevWrap.Controls.Add(_dPreview);
        var dRight = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
        dRight.Controls.Add(dPrevWrap);
        dRight.Controls.Add(dPrevHead);
        _detail.Controls.Add(dRight);
        _detail.Controls.Add(_dInfo);
        _detail.Controls.Add(dBar);

        // ---------- 編寫 ----------
        var eBar = Bar();
        Btn(eBar, Lang.T("← 返回"), () => _ = LeaveEditAsync());
        _eTitle = Title(eBar);
        _saveBtn = Btn(eBar, Lang.T("💾 儲存"), () => SaveAll(true), Lang.T("儲存所有改動（Ctrl+S）"), BtnKind.Primary);
        Btn(eBar, Lang.T("▶ 試行"), () => _ = RunDevAsync(), Lang.T("儲存並重新載入右邊嘅試行畫面（F5）"), BtnKind.Success);
        Btn(eBar, Lang.T("✦ AI 幫我寫"), () => _assist!.Open(), Lang.T("同 AI 傾偈：講低想要咩功能，佢會問清楚再幫你寫（或者改）成個插件"), BtnKind.Default);
        Btn(eBar, Lang.T("📖 API 說明"), ShowApi);
        Btn(eBar, "F12", () => _dev?.DevTools(), Lang.T("開發者工具（睇 HTML、除錯）"));
        Btn(eBar, Lang.T("📂 資料夾"), () => { if (_cur != null) TUi.Open(PluginStore.PathOf(_cur.Id), folder: true); }, Lang.T("用檔案總管開呢個插件嘅資料夾"));
        _tabs = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Ui.S(40), BackColor = Ui.Bg, WrapContents = false };
        var codeHost = new Panel { Dock = DockStyle.Fill, BackColor = EditorWeb.Back, Padding = new Padding(1) };
        codeHost.Controls.Add(_code);
        var left = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
        left.Controls.Add(codeHost);
        left.Controls.Add(_tabs);
        var right = new Panel { Dock = DockStyle.Right, Width = Ui.S(460), BackColor = Ui.Bg, Padding = new Padding(Ui.S(12), Ui.S(40), 0, 0) };
        _console = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Bottom, Height = Ui.S(160), BorderStyle = BorderStyle.None,
            BackColor = Ui.Card, ForeColor = Ui.TextDim, Font = new Font("Consolas", 9.5f)
        };
        var consoleHead = Ui.Text_(Lang.T("記錄（console.log、錯誤、被擋嘅網絡請求）"), Ui.TextFaint, Ui.F(8.75f), new Padding(0));
        consoleHead.AutoSize = false; consoleHead.Dock = DockStyle.Bottom; consoleHead.Height = Ui.S(24); consoleHead.TextAlign = ContentAlignment.BottomLeft;
        var devWrap = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Border, Padding = new Padding(1) };
        devWrap.Controls.Add(_devHost);
        right.Controls.Add(devWrap);
        right.Controls.Add(consoleHead);
        right.Controls.Add(_console);
        var eBody = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
        eBody.Controls.Add(left);
        eBody.Controls.Add(right);
        eBody.Resize += (_, _) => right.Width = Math.Max(Ui.S(320), eBody.Width * 42 / 100);
        _edit.Controls.Add(eBody);
        _edit.Controls.Add(eBar);

        _code.TextChanged2 += t => { if (!_loading) { _buf[_file] = t; MarkDirty(); } };
        _code.SaveRequested += t => { _buf[_file] = t; SaveAll(true); };
        _code.RunRequested += t => { _buf[_file] = t; _ = RunDevAsync(); };
        _assist = new PluginAssist(_edit, this, () => _cur, AfterAiApply);

        Controls.Add(_edit);
        Controls.Add(_detail);
        Controls.Add(_lobby);

        PluginStore.Changed += OnStoreChanged;
        Disposed += (_, _) => PluginStore.Changed -= OnStoreChanged;
        BuildGrid();
    }

    private void OnStoreChanged() { if (!IsDisposed && IsHandleCreated) BeginInvoke(BuildGrid); }

    private void SetView(View v)
    {
        _view = v;
        _lobby.Visible = v == View.Lobby;
        _detail.Visible = v == View.Detail;
        _edit.Visible = v == View.Edit;
        (v == View.Lobby ? _lobby : v == View.Detail ? _detail : _edit).BringToFront();
        if (v != View.Detail) ClosePreview();
    }

    // ================= 大廳 =================

    private void BuildGrid()
    {
        _grid.SuspendLayout();
        foreach (Control c in _grid.Controls.Cast<Control>().ToList()) { _grid.Controls.Remove(c); c.Dispose(); }
        var list = PluginStore.List();
        foreach (var m in list)
        {
            var card = new PluginCard(m);
            card.Open += () => GoDetail(m.Id);
            card.PinToggle += () =>
            {
                if (PluginStore.IsPinned(m.Id)) PluginStore.SetPinned(m.Id, false);
                else
                {
                    PluginManifest mm;
                    try { mm = PluginStore.Load(m.Id); } catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, Program.AppName); return; }
                    if (!PluginStore.AskApprove(FindForm(), mm)) return;
                    PluginStore.SetPinned(m.Id, true);
                    Toast.Show(Lang.T("已加到左側工具欄"), Ui.Success, 1500);
                }
                card.Invalidate();
            };
            _tip.SetToolTip(card, m.Description.Length > 0 ? m.Description : m.Name);
            _grid.Controls.Add(card);
        }
        _grid.ResumeLayout(true);
        _empty.Visible = list.Count == 0;
        if (list.Count == 0) _empty.BringToFront();
    }

    private void GoLobby()
    {
        _cur = null;
        BuildGrid();
        SetView(View.Lobby);
    }

    // ================= 詳情 + 預覽 =================

    private void GoDetail(string id)
    {
        PluginManifest m;
        try { m = PluginStore.Load(id); }
        catch (Exception ex)
        {
            // manifest 有錯：直接入編寫度改
            _cur = new PluginManifest { Id = id, Name = id };
            GoEdit();
            Log("error", "manifest.json：" + ex.Message);
            return;
        }
        _cur = m;
        _dTitle.Text = m.Icon + "  " + m.Name;
        var sb = new StringBuilder();
        sb.AppendLine(m.Description.Length > 0 ? m.Description : Lang.T("（冇介紹）"));
        sb.AppendLine();
        sb.AppendLine(Lang.T("版本：") + m.Version);
        if (m.Author.Length > 0) sb.AppendLine(Lang.T("作者：") + m.Author);
        sb.AppendLine();
        var perms = m.PermissionList();
        sb.AppendLine(Lang.T("權限："));
        if (perms.Count == 0) sb.AppendLine("  " + Lang.T("（冇特別權限）"));
        foreach (var p in perms) sb.AppendLine((p.Danger ? "  ⚠ " : "  • ") + p.Text);
        _dInfo.Text = sb.ToString();
        UpdatePinBtn();
        SetView(View.Detail);
        StartPreview(m);
    }

    private void UpdatePinBtn()
    {
        if (_cur == null) return;
        bool pinned = PluginStore.IsPinned(_cur.Id);
        _pinBtn.Text = pinned ? Lang.T("📌 從工具欄移除") : Lang.T("📌 加到左側工具欄");
        _pinBtn.Kind = pinned ? BtnKind.Ghost : BtnKind.Primary;
        _pinBtn.Width = _pinBtn.GetPreferredSize(Size.Empty).Width + Ui.S(10);
        _pinBtn.Invalidate();
    }

    private Control? _permBox;

    /// <summary>右邊即時預覽（未同意權限就先問）。</summary>
    private void StartPreview(PluginManifest m)
    {
        ClosePreview();
        if (!PluginStore.Approved(m) && m.PermissionList().Count > 0)
        {
            var box = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Ui.Bg, FlowDirection = FlowDirection.TopDown, Padding = new Padding(Ui.S(20)) };
            box.Controls.Add(Ui.Text_(Lang.T("預覽之前要先同意呢個插件嘅權限（睇左邊清單）。只預覽你信任嘅插件。"), Ui.TextDim, Ui.F(9.75f), new Padding(0, 0, 0, Ui.S(12))));
            var ok = TUi.Btn(Lang.T("同意並預覽"), () => { if (PluginStore.AskApprove(FindForm(), m)) StartPreview(m); }, 0, BtnKind.Primary);
            box.Controls.Add(ok);
            _dPreview.Controls.Add(box);
            _permBox = box;
            return;
        }
        PluginStore.Approve(m);
        _preview = new PluginView(m, false);
        _dPreview.Controls.Add(_preview);
        _ = _preview.StartAsync();
    }

    private void ClosePreview()
    {
        _preview?.Close();
        _preview = null;
        _permBox?.Dispose();
        _permBox = null;
    }

    private void TogglePin()
    {
        if (_cur == null) return;
        if (PluginStore.IsPinned(_cur.Id)) PluginStore.SetPinned(_cur.Id, false);
        else
        {
            if (!PluginStore.AskApprove(FindForm(), _cur)) return;
            PluginStore.SetPinned(_cur.Id, true);
            Toast.Show(Lang.T("已加到左側工具欄"), Ui.Success, 1500);
        }
        UpdatePinBtn();
    }

    // ================= 編寫 =================

    private void GoEdit()
    {
        if (_cur == null) return;
        _eTitle.Text = "✎ " + _cur.Name;
        _buf.Clear();
        _console.Clear();
        _file = File.Exists(Path.Combine(PluginStore.PathOf(_cur.Id), _cur.Entry)) ? _cur.Entry : "manifest.json";
        _dev?.Close();
        _dev = null;
        SetView(View.Edit);
        MarkDirty();
        _assist.Refresh();
        _ = OpenEditorAsync();
    }

    /// <summary>離開編寫：有未儲存就問。</summary>
    private async Task LeaveEditAsync()
    {
        _buf.TryGetValue(_file, out _);
        var latest = await _code.GetTextAsync();
        if (latest != null && _cur != null && latest != ReadDisk(_file)) _buf[_file] = latest;
        if (_buf.Count > 0)
        {
            var r = MessageBox.Show(FindForm(), Lang.T("有未儲存嘅改動，儲存咗先離開？"), Program.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;
            if (r == DialogResult.Yes) SaveAll(false);
            else _buf.Clear();
        }
        _dev?.Close();
        _dev = null;
        if (_cur != null) GoDetail(_cur.Id); else GoLobby();
    }

    private async Task OpenEditorAsync()
    {
        if (_cur == null) return;
        if (!_codeStarted)
        {
            _codeStarted = true;
            await _code.StartAsync();
        }
        BuildTabs();
        LoadFile(_file);
        await RunDevAsync();
    }

    private List<string> Files()
    {
        if (_cur == null) return new();
        string dir = PluginStore.PathOf(_cur.Id);
        var list = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Where(IsText).ToList()
            : new List<string>();
        int Rank(string f) => f == "manifest.json" ? 0 : f == _cur!.Entry ? 1 : f.EndsWith(".js") ? 2 : 3;
        return list.OrderBy(Rank).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsText(string f) => Path.GetExtension(f).ToLowerInvariant() is ".json" or ".html" or ".htm" or ".js" or ".mjs" or ".css" or ".txt" or ".md" or ".svg" or ".xml" or ".csv";

    private void BuildTabs()
    {
        _tabs.SuspendLayout();
        foreach (Control c in _tabs.Controls.Cast<Control>().ToList()) { _tabs.Controls.Remove(c); c.Dispose(); }
        foreach (var f in Files())
        {
            string file = f;
            var b = TUi.Btn((_buf.ContainsKey(file) ? "● " : "") + file, () => SwitchFile(file), 0, file == _file ? BtnKind.Primary : BtnKind.Ghost);
            b.Height = Ui.S(32);
            b.Margin = new Padding(0, 0, Ui.S(4), 0);
            b.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) FileMenu(b, file); };
            _tabs.Controls.Add(b);
        }
        var add = TUi.Btn("+", NewFile, 34);
        add.Height = Ui.S(32);
        _tip.SetToolTip(add, Lang.T("新增檔案（例如 style.css）"));
        _tabs.Controls.Add(add);
        _tabs.ResumeLayout(true);
    }

    private async void SwitchFile(string file)
    {
        var t = await _code.GetTextAsync();
        if (t != null && t != (_buf.TryGetValue(_file, out var b) ? b : ReadDisk(_file))) _buf[_file] = t;
        _file = file;
        BuildTabs();
        LoadFile(file);
    }

    private void FileMenu(Control b, string file)
    {
        if (file == "manifest.json") return;
        var m = TUi.Menu(this);
        m.Items.Add(Lang.T("刪除呢個檔案"), null, (_, _) =>
        {
            if (_cur == null) return;
            if (MessageBox.Show(FindForm(), Lang.F("刪除「{0}」？", file), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { File.Delete(Path.Combine(PluginStore.PathOf(_cur.Id), file)); } catch (Exception ex) { Log("error", ex.Message); }
            _buf.Remove(file);
            if (_file == file) _file = "manifest.json";
            BuildTabs();
            LoadFile(_file);
        });
        m.Show(b, new Point(0, b.Height));
    }

    private void NewFile()
    {
        if (_cur == null) return;
        var name = Mail.AskText.Show(FindForm(), Lang.T("新增檔案"), Lang.T("檔名（例如 style.css、helper.js）："), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim().Replace('\\', '/');
        string dir = Path.GetFullPath(PluginStore.PathOf(_cur.Id)) + Path.DirectorySeparatorChar;
        string p = Path.GetFullPath(Path.Combine(dir, name));
        if (!p.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || !IsText(name)) { MessageBox.Show(FindForm(), Lang.T("檔名唔啱（只支援 .html .js .css .json .txt .md 等文字檔）。"), Program.AppName); return; }
        if (!File.Exists(p)) { Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, ""); }
        SwitchFile(name);
    }

    private string ReadDisk(string file)
    {
        if (_cur == null) return "";
        try { string p = Path.Combine(PluginStore.PathOf(_cur.Id), file); return File.Exists(p) ? File.ReadAllText(p).Replace("\r\n", "\n") : ""; }
        catch { return ""; }
    }

    private void LoadFile(string file)
    {
        if (_cur == null) return;
        _loading = true;
        _code.SetText(_buf.TryGetValue(file, out var b) ? b : ReadDisk(file), CodeBox.ModeFor(file));
        _loading = false;
    }

    private void MarkDirty()
    {
        bool dirty = _buf.Count > 0;
        _saveBtn.Text = dirty ? Lang.T("💾 儲存 ●") : Lang.T("💾 儲存");
        _saveBtn.Kind = dirty ? BtnKind.Primary : BtnKind.Ghost;
        _saveBtn.Width = _saveBtn.GetPreferredSize(Size.Empty).Width + Ui.S(10);
        _saveBtn.Invalidate();
        // 分頁「●」
        foreach (Control c in _tabs.Controls)
            if (c is FancyButton fb)
            {
                string name = fb.Text.StartsWith("● ") ? fb.Text[2..] : fb.Text;
                if (name == "+") continue;
                string want = (_buf.ContainsKey(name) ? "● " : "") + name;
                if (fb.Text != want) { fb.Text = want; fb.Width = fb.GetPreferredSize(Size.Empty).Width; fb.Invalidate(); }
            }
    }

    /// <summary>儲存所有未儲存嘅檔案。</summary>
    private void SaveAll(bool log)
    {
        if (_cur == null) return;
        string dir = PluginStore.PathOf(_cur.Id);
        foreach (var (file, text) in _buf.ToList())
        {
            try { File.WriteAllText(Path.Combine(dir, file), text); _buf.Remove(file); }
            catch (Exception ex) { Log("error", Lang.T("儲存失敗：") + file + "：" + ex.Message); }
        }
        MarkDirty();
        if (log) Log("info", Lang.T("已儲存"));
        try { _cur = PluginStore.Load(_cur.Id); _eTitle.Text = "✎ " + _cur.Name; } catch { }
        PluginStore.Notify();
    }

    private async Task FlushAsync()
    {
        var t = await _code.GetTextAsync();
        if (t != null && t != (_buf.TryGetValue(_file, out var b) ? b : ReadDisk(_file))) _buf[_file] = t;
        if (_buf.Count > 0) SaveAll(false);
    }

    /// <summary>試行：儲存 → 讀 manifest → 重新載入右邊。</summary>
    private async Task RunDevAsync()
    {
        if (_cur == null) return;
        await FlushAsync();
        PluginManifest m;
        try { m = PluginStore.Load(_cur.Id); }
        catch (Exception ex) { Log("error", "manifest.json：" + ex.Message); return; }
        _cur = m;
        _eTitle.Text = "✎ " + m.Name;
        if (!PluginStore.AskApprove(FindForm(), m)) { Log("warn", Lang.T("未同意權限，冇試行。")); return; }
        _console.Clear();
        Log("info", Lang.F("▶ 試行 {0} v{1}", m.Name, m.Version));
        if (_dev != null && !_dev.IsDisposed) { _dev.Reload(m); return; }
        _dev = new PluginView(m, true);
        _dev.Log += Log;
        _devHost.Controls.Add(_dev);
        await _dev.StartAsync();
    }

    private void Log(string level, string text)
    {
        if (_console.IsDisposed) return;
        string prefix = level switch { "error" => "✖ ", "warn" => "⚠ ", _ => "  " };
        _console.AppendText($"{DateTime.Now:HH:mm:ss} {prefix}{text}\r\n");
    }

    private void ShowApi()
    {
        using var f = new Form
        {
            Text = Lang.T("插件 API 說明"), StartPosition = FormStartPosition.CenterParent, Size = new Size(Ui.S(820), Ui.S(680)), BackColor = Ui.Bg,
            ShowInTaskbar = false, MinimizeBox = false
        };
        try { DarkTheme.DarkTitleBar(f); } catch { }
        var tb = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            BackColor = Ui.Card, ForeColor = Ui.Text, Font = new Font("Consolas", 10f), Text = PluginTemplates.ApiDoc.Replace("\n", "\r\n")
        };
        f.Controls.Add(tb);
        f.ShowDialog(FindForm());
    }

    // ================= 新增 / 匯入 / 匯出 / 刪除 =================

    private void NewPlugin()
    {
        var name = Mail.AskText.Show(FindForm(), Lang.T("新插件"), Lang.T("插件名稱："), Lang.T("我的工具"));
        if (string.IsNullOrWhiteSpace(name)) return;
        string id = PluginStore.Create(name.Trim());
        _cur = PluginStore.Load(id);
        GoEdit();
    }

    private void ImportPlugin()
    {
        using var d = new OpenFileDialog { Filter = Lang.T("MultiTool 插件") + "|*.mtplugin;*.zip" };
        if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            string id = PluginStore.Import(d.FileName);
            Toast.Show(Lang.T("已匯入"), Ui.Success, 1500);
            GoDetail(id);
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), Lang.T("匯入唔到：") + ex.Message, Program.AppName); }
    }

    private void ExportCurrent()
    {
        if (_cur == null) return;
        using var d = new SaveFileDialog { FileName = Mail.MailPage.SafeName(_cur.Name) + ".mtplugin", Filter = Lang.T("MultiTool 插件") + "|*.mtplugin" };
        if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
        try { PluginStore.Export(_cur.Id, d.FileName); Toast.Show(Lang.T("已匯出"), Ui.Success, 1500); }
        catch (Exception ex) { MessageBox.Show(FindForm(), Lang.T("匯出唔到：") + ex.Message, Program.AppName); }
    }

    private void DeleteCurrent()
    {
        if (_cur == null) return;
        if (MessageBox.Show(FindForm(), Lang.F("刪除插件「{0}」？程式碼同佢儲存嘅資料都會刪走。", _cur.Name), Program.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        ClosePreview();
        string id = _cur.Id;
        PluginStore.SetPinned(id, false);
        PluginStore.Delete(id);
        GoLobby();
    }

    // ================= AI =================

    /// <summary>AI 套用咗新檔案：重新載入編輯器 + 試行。</summary>
    private void AfterAiApply()
    {
        _buf.Clear();
        MarkDirty();
        BuildTabs();
        LoadFile(Files().Contains(_file) ? _file : "manifest.json");
        _ = RunDevAsync();
    }

    internal List<string> CurrentFiles() => Files();
    internal Task FlushForAi() => FlushAsync();
}

/// <summary>加咗去左側工具欄嘅插件：自己一頁，一開 MultiTool 就運行。</summary>
internal sealed class PluginSidePage : Panel
{
    public string Id { get; }
    private PluginView? _view;

    public PluginSidePage(string id)
    {
        Id = id;
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        // 開程式時唔好即刻開 WebView2（拖慢啟動）：等主視窗出咗、閒咗先開
        HandleCreated += async (_, _) =>
        {
            await Task.Delay(Program.StartupDone ? 0 : 2500);
            if (!IsDisposed) Start();
        };
        Disposed += (_, _) => { PluginRuntime.Running.Remove(Id); _view?.Close(); };
    }

    private void Start()
    {
        foreach (Control c in Controls.Cast<Control>().ToList()) { Controls.Remove(c); c.Dispose(); }
        PluginManifest m;
        try { m = PluginStore.Load(Id); }
        catch (Exception ex) { Msg(Lang.T("manifest.json 有錯：") + ex.Message); return; }
        if (!PluginStore.Approved(m))
        {
            // 權限改咗：要再同意
            var box = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Ui.Bg, FlowDirection = FlowDirection.TopDown, Padding = new Padding(Ui.S(24)) };
            box.Controls.Add(Ui.Text_(Lang.F("「{0}」嘅權限改咗，要你再同意先會運行。", m.Name), Ui.TextDim, Ui.F(10f), new Padding(0, 0, 0, Ui.S(12))));
            box.Controls.Add(TUi.Btn(Lang.T("睇權限並同意"), () => { if (PluginStore.AskApprove(FindForm(), m)) Start(); }, 0, BtnKind.Primary));
            Controls.Add(box);
            return;
        }
        _view = new PluginView(m, false);
        Controls.Add(_view);
        PluginRuntime.Running[Id] = _view;
        _ = _view.StartAsync();
    }

    private void Msg(string s)
    {
        var l = new Label { Text = s, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Ui.TextDim, Font = Ui.F(10f), BackColor = Ui.Bg };
        Controls.Add(l);
    }
}

/// <summary>插件編寫嘅 AI 對話（右下角雲掣 + 對話欄，同「我的腳本」一樣）。</summary>
internal sealed class PluginAssist
{
    private readonly Control _host;
    private readonly PluginsPage _page;
    private readonly Func<PluginManifest?> _cur;
    private readonly Action _applied;
    private readonly Ai.CloudButton _cloud = new() { Visible = false };
    private readonly Ai.AssistPanel _panel = new() { Visible = false };
    private Ai.AiProvider? _prov;
    private System.Diagnostics.Process? _running;
    private bool _stopped, _checked;

    private sealed class State
    {
        public readonly Ai.AssistState Chat = new();
        public Dictionary<string, string>? PendingFiles;
        public Dictionary<string, string?>? Undo;   // 套用之前嘅檔案（null = 本來冇）
    }
    private static readonly Dictionary<string, State> States = new();
    private State? St => _cur() is { } m ? (States.TryGetValue(m.Id, out var s) ? s : States[m.Id] = new State()) : null;

    public PluginAssist(Control host, PluginsPage page, Func<PluginManifest?> cur, Action applied)
    {
        _host = host;
        _page = page;
        _cur = cur;
        _applied = applied;
        _panel.Intro = Lang.T("話我知你想整個咩工具，例如：「每 10 分鐘睇一次比特幣價，超過 10 萬就通知我」。\n") +
                       Lang.T("唔清楚嘅地方我會先問你；寫好之後撳「套用到插件」就會換上同即刻試行（可以復原）。");
        _panel.ApplyText = Lang.T("套用到插件");
        _cloud.Click += (_, _) => Toggle();
        _panel.CloseRequested += () => { if (St is { } s) s.Chat.Open = false; Layout(); };
        _panel.SendRequested += async () => await SendAsync();
        _panel.ApplyRequested += Apply;
        _panel.UndoRequested += Undo;
        _panel.ClearRequested += Clear;
        host.Controls.Add(_panel);
        host.Controls.Add(_cloud);
        host.Resize += (_, _) => Layout();
        host.VisibleChanged += async (_, _) => { if (host.Visible) { await CheckAsync(); Refresh(); } };
        host.Disposed += (_, _) => Kill();
    }

    private void Kill()
    {
        if (_running == null) return;
        _stopped = true;
        try { _running.Kill(true); } catch { }
    }

    /// <summary>揀咗 AI 而且登入咗先出個雲。</summary>
    private async Task CheckAsync()
    {
        if (_checked && _prov != null) return;
        _checked = true;
        var p = Ai.AiProvider.LoadCurrent();
        if (p == null) return;
        string? exe = await Task.Run(p.Find);
        if (exe == null) return;
        var a = await p.AuthAsync(exe);
        if (a.LoggedIn != true || _host.IsDisposed) return;
        _prov = p;
        _panel.SetTitle(Lang.F("AI 助手 · {0}", p.Name));
        _cloud.Visible = true;
        Refresh();
    }

    /// <summary>轉咗插件：顯示返嗰個插件嘅對話。</summary>
    public void Refresh()
    {
        if (St is { } s && _prov != null)
        {
            _panel.Render(s.Chat, _prov.Name);
            ShowActions(s);
        }
        Layout();
    }

    public async void Open()
    {
        await CheckAsync();
        if (_prov == null)
        {
            MessageBox.Show(_host.FindForm(), Lang.T("未揀 AI：請去「AI 助手」頁登入 Claude / ChatGPT / Gemini。"), Program.AppName);
            return;
        }
        if (St is { } s) s.Chat.Open = true;
        Refresh();
        _panel.FocusInput();
    }

    private void Toggle()
    {
        if (St is not { } s) return;
        s.Chat.Open = !s.Chat.Open;
        Refresh();
        if (s.Chat.Open) _panel.FocusInput();
    }

    private void Layout()
    {
        int m = Ui.S(24), size = Ui.S(52);
        var cs = _host.ClientSize;
        _cloud.SetBounds(cs.Width - size - m, cs.Height - size - Ui.S(18), size, size);
        _cloud.BringToFront();
        bool open = St is { Chat.Open: true } && _cloud.Visible;
        if (open)
        {
            int w = Math.Min(Ui.S(420), cs.Width - Ui.S(40));
            int h = Math.Min(Ui.S(540), _cloud.Top - Ui.S(20));
            _panel.SetBounds(cs.Width - w - m, _cloud.Top - Math.Max(Ui.S(200), h) - Ui.S(10), w, Math.Max(Ui.S(200), h));
            _panel.BringToFront();
        }
        _panel.Visible = open;
        _cloud.Active = open;
    }

    // 「套用」掣：用 AssistState.Pending 控制顯唔顯示；「復原」用 Undo（腳本用嘅型別，呢度只係借嚟開關個掣）
    private void ShowActions(State s)
    {
        s.Chat.Pending = s.PendingFiles != null ? "files" : null;
        s.Chat.Undo = s.Undo != null ? (false, new List<Scripts.Step>(), new List<Scripts.IfBlock>()) : null;
        _panel.Render(s.Chat, _prov?.Name ?? "AI");
    }

    private void Clear()
    {
        if (_running != null || St is not { } s) return;
        s.Chat.Msgs.Clear();
        s.Chat.Session = null;
        s.PendingFiles = null;
        ShowActions(s);
    }

    private string BuildPrompt(PluginManifest m, string q, bool first)
    {
        var sb = new StringBuilder();
        if (first)
        {
            sb.AppendLine("你係「多功能工具」MultiTool 入面嘅插件開發助手，同用戶一問一答，幫佢寫 / 改佢而家編輯緊嘅插件（HTML + JavaScript 小工具）。");
            sb.AppendLine(Mail.MailAi.ReplyRule + "唔好用任何工具，唔好讀寫檔案，直接答。");
            sb.AppendLine("如果用戶嘅要求唔夠清楚（例如要用邊個網站 / API、幾耐檢查一次、要唔要通知、介面要顯示咩），先簡短問 1 至 3 條問題，等佢答咗先寫。");
            sb.AppendLine();
            sb.AppendLine(PluginTemplates.ApiDoc);
            sb.AppendLine("規則：");
            sb.AppendLine("- 要建立或者修改檔案，就喺回答最後，每個檔案用以下格式俾出「完整內容」（唔係淨係改咗嘅部分），唔好用 Markdown code block 包住：");
            sb.AppendLine("=== FILE: 檔名 ===");
            sb.AppendLine("（完整內容）");
            sb.AppendLine("=== END ===");
            sb.AppendLine("- 淨係解答問題或者問用戶問題、唔使改嘅話，就唔好俾檔案。");
            sb.AppendLine("- 網絡請求用 mt.net.fetch / mt.net.json，並將用到嘅網站加入 manifest permissions.net；用到乜權限就喺 manifest 開。唔好用外部 CDN（會被擋）。");
            sb.AppendLine("- 顏色一律用 API 說明入面嘅 --mt-* CSS 變數（會跟程式日光 / 深色主題），唔好寫死顏色。" + Mail.MailAi.UiTextRule + "盡量保留用戶原有嘅程式碼，只改佢要求嘅部分。");
            sb.AppendLine();
        }
        sb.AppendLine("而家插件嘅檔案（用戶可能自己改過，以呢個為準）：");
        string dir = PluginStore.PathOf(m.Id);
        foreach (var f in _page.CurrentFiles())
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(dir, f)); } catch { continue; }
            if (text.Length > 20000) { sb.AppendLine($"=== FILE: {f} ===\n（太長，略過）\n=== END ==="); continue; }
            sb.Append("=== FILE: ").Append(f).Append(" ===\n").Append(text.TrimEnd()).Append("\n=== END ===\n");
        }
        sb.AppendLine();
        sb.AppendLine("用戶：" + q);
        return sb.ToString();
    }

    private static readonly Regex FileRx = new(@"=== FILE:\s*(.+?)\s*===\r?\n(.*?)\r?\n=== END ===", RegexOptions.Singleline | RegexOptions.Compiled);

    private async Task SendAsync()
    {
        if (_running != null) { Kill(); return; }
        var m = _cur();
        var s = St;
        if (_prov == null || m == null || s == null) return;
        string q = _panel.TakeInput();
        if (q.Length == 0) return;
        string? exe = _prov.Find();
        if (exe == null) { _panel.System(Lang.F("搵唔到 {0}。", _prov.ToolName)); return; }
        await _page.FlushForAi();

        _stopped = false;
        s.Chat.Msgs.Add(("user", q));
        ShowActions(s);
        _panel.Thinking(Lang.F("{0} 諗緊…", _prov.Name));
        _panel.SetBusy(true);
        Ai.AiProvider.Answer ans;
        try
        {
            ans = await _prov.AskAsync(exe, BuildPrompt(m, q, s.Chat.Session == null), s.Chat.Session, p => _running = p);
            if (ans.IsError && !_stopped && s.Chat.Session != null && Ai.AiProvider.IsSessionGone(ans.Text))
            {
                s.Chat.Session = null;
                ans = await _prov.AskAsync(exe, BuildPrompt(m, q, true), null, p => _running = p);
            }
        }
        catch (Exception ex) { ans = new Ai.AiProvider.Answer(Lang.T("出錯：") + ex.Message, null, true); }
        _running = null;
        if (_host.IsDisposed) return;
        _panel.SetBusy(false);
        if (ans.SessionId != null) s.Chat.Session = ans.SessionId;
        if (_stopped) s.Chat.Msgs.Add(("system", Lang.T("已停止。")));
        else if (ans.IsError) s.Chat.Msgs.Add(("error", ans.Text));
        else
        {
            var files = new Dictionary<string, string>();
            foreach (Match fm in FileRx.Matches(ans.Text))
                files[fm.Groups[1].Value.Trim().Replace('\\', '/')] = fm.Groups[2].Value.Replace("\r\n", "\n") + "\n";
            string text = FileRx.Replace(ans.Text, "").Trim();
            if (files.Count > 0)
            {
                s.PendingFiles = files;
                text = (text.Length > 0 ? text + "\n\n" : "") + Lang.F("（已經寫好 {0}，撳下面「套用到插件」就會換上同試行）", string.Join("、", files.Keys));
            }
            s.Chat.Msgs.Add(("ai", text.Length > 0 ? text : Lang.T("（冇回覆）")));
        }
        if (_cur()?.Id == m.Id) { ShowActions(s); _panel.FocusInput(); }
    }

    private void Apply()
    {
        var m = _cur();
        var s = St;
        if (m == null || s?.PendingFiles == null) return;
        string dir = PluginStore.PathOf(m.Id);
        string full = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        var undo = new Dictionary<string, string?>();
        var bad = new List<string>();
        foreach (var (name, content) in s.PendingFiles)
        {
            string p = Path.GetFullPath(Path.Combine(dir, name));
            if (!p.StartsWith(full, StringComparison.OrdinalIgnoreCase) || !IsTextFile(name)) { bad.Add(name); continue; }
            try
            {
                undo[name] = File.Exists(p) ? File.ReadAllText(p) : null;
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, content);
            }
            catch (Exception ex) { bad.Add(name + "：" + ex.Message); }
        }
        s.Undo = undo;
        s.PendingFiles = null;
        string msg = Lang.T("已套用，右邊即刻試行。");
        if (bad.Count > 0) msg += "\n" + Lang.T("略過：") + string.Join("、", bad);
        s.Chat.Msgs.Add(("system", msg));
        ShowActions(s);
        _applied();
    }

    private void Undo()
    {
        var m = _cur();
        var s = St;
        if (m == null || s?.Undo == null) return;
        string dir = PluginStore.PathOf(m.Id);
        foreach (var (name, old) in s.Undo)
        {
            string p = Path.Combine(dir, name);
            try { if (old == null) { if (File.Exists(p)) File.Delete(p); } else File.WriteAllText(p, old); } catch { }
        }
        s.Undo = null;
        s.Chat.Msgs.Add(("system", Lang.T("已復原返套用之前嘅檔案。")));
        ShowActions(s);
        _applied();
    }

    private static bool IsTextFile(string f) => Path.GetExtension(f).ToLowerInvariant() is ".json" or ".html" or ".htm" or ".js" or ".mjs" or ".css" or ".txt" or ".md" or ".svg" or ".xml" or ".csv";
}
