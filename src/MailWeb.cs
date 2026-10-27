using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace MultiTool.Mail;

// =====================================================================
//  睇信用嘅瀏覽器：優先用 WebView2（Edge 核心，Windows 10 / 11 通常已經有），
//  冇就用返舊 IE 核心（WebBrowser）。兩樣都：唔行 script、撳連結開外部瀏覽器、預設唔載入遠端圖片。
// =====================================================================

/// <summary>WebView2 嘅 DLL 包咗喺 MultiTool.dll 入面（resource），第一次用先抽出嚟 data\lib。</summary>
internal static class WebView2Lib
{
    private const string Ver = "1.0.3856.49";
    private static bool? _ok;
    private static string _core = "", _loader = "";

    /// <summary>準備好 WebView2（DLL + 部電腦有冇裝 WebView2 Runtime）。唔得就 false（用 IE 核心）。</summary>
    public static bool Prepare()
    {
        if (_ok != null) return _ok.Value;
        try
        {
            // MultiTool 係 32-bit 定 64-bit：用返啱嘅 WebView2Loader
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
            if (arch is not (Architecture.X86 or Architecture.X64)) return (_ok = false).Value;
            string sub = arch == Architecture.X86 ? "x86" : "x64";
            string dir = Path.Combine(AppPaths.Data, "lib", "webview2-" + Ver);
            Directory.CreateDirectory(Path.Combine(dir, sub));
            _core = Extract("wv2.core", Path.Combine(dir, "Microsoft.Web.WebView2.Core.dll"));
            _loader = Extract(arch == Architecture.X86 ? "wv2.loader.x86" : "wv2.loader", Path.Combine(dir, sub, "WebView2Loader.dll"));
            NativeLibrary.Load(_loader);
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                if (new AssemblyName(e.Name).Name != "Microsoft.Web.WebView2.Core") return null;
                var asm = Assembly.LoadFrom(_core);
                try { NativeLibrary.SetDllImportResolver(asm, (n, _, _) => n.Contains("WebView2Loader", StringComparison.OrdinalIgnoreCase) ? NativeLibrary.Load(_loader) : IntPtr.Zero); }
                catch { }
                return asm;
            };
            _ok = Wv2Host.RuntimeVersion() != null;
            if (_ok == false) AppPaths.LogError("WebView2", new Exception("WebView2 Runtime 未安裝，用返 IE 核心"));
        }
        catch (Exception ex)
        {
            AppPaths.LogError("WebView2 準備", ex);
            _ok = false;
        }
        return _ok.Value;
    }

    private static string Extract(string res, string path)
    {
        using var s = typeof(WebView2Lib).Assembly.GetManifestResourceStream(res) ?? throw new FileNotFoundException(res);
        if (!File.Exists(path) || new FileInfo(path).Length != s.Length)
        {
            string tmp = path + ".tmp";
            using (var f = File.Create(tmp)) s.CopyTo(f);
            File.Move(tmp, path, true);
        }
        return path;
    }
}

/// <summary>所有用到 WebView2 型別嘅嘢都喺呢度（WebView2Lib.Prepare 之後先會用到，咁先搵到 DLL）。</summary>
internal sealed class Wv2Host
{
    private CoreWebView2Environment _env = null!;
    private CoreWebView2Controller _ctl = null!;
    private bool _allowRemote;
    private string _tempUri = "";

    public static string? RuntimeVersion()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { return null; }
    }

    public static async Task<Wv2Host> CreateAsync(Control host, Action<string> link, Action esc)
    {
        var h = new Wv2Host();
        string data = Path.Combine(MailStore.Dir, "webview");
        Directory.CreateDirectory(data);
        h._env = await CoreWebView2Environment.CreateAsync(null, data);
        h._ctl = await h._env.CreateCoreWebView2ControllerAsync(host.Handle);
        h._ctl.DefaultBackgroundColor = Color.White;
        h._ctl.Bounds = host.ClientRectangle;
        host.Resize += (_, _) => { try { h._ctl.Bounds = host.ClientRectangle; } catch { } };
        host.Disposed += (_, _) => { try { h._ctl.Close(); } catch { } };
        var wv = h._ctl.CoreWebView2;
        var st = wv.Settings;
        st.IsScriptEnabled = false;            // 信件唔准行 JavaScript
        st.IsWebMessageEnabled = false;
        st.AreHostObjectsAllowed = false;
        st.AreDevToolsEnabled = false;
        st.IsStatusBarEnabled = true;          // 指住連結會見到網址（防釣魚）
        st.AreDefaultContextMenusEnabled = true;
        st.IsZoomControlEnabled = true;
        st.IsPasswordAutosaveEnabled = false;
        st.IsGeneralAutofillEnabled = false;
        // 撳連結：唔喺度開，交俾外面瀏覽器 / 寫信
        wv.NavigationStarting += (_, e) =>
        {
            string u = e.Uri ?? "";
            if (u.StartsWith("about:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                (h._tempUri.Length > 0 && u.StartsWith(h._tempUri, StringComparison.OrdinalIgnoreCase))) return;
            e.Cancel = true;
            link(u);
        };
        wv.NewWindowRequested += (_, e) => { e.Handled = true; link(e.Uri ?? ""); };
        // 遠端圖片 / CSS / 字型：未准就一律擋（防追蹤）
        wv.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        wv.WebResourceRequested += (_, e) =>
        {
            string u = e.Request.Uri ?? "";
            if (!h._allowRemote && (u.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https:", StringComparison.OrdinalIgnoreCase)))
                e.Response = h._env.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
        };
        h._ctl.AcceleratorKeyPressed += (_, e) =>
        {
            if (e.KeyEventKind == CoreWebView2KeyEventKind.KeyDown && e.VirtualKey == 0x1B) { e.Handled = true; esc(); }
        };
        return h;
    }

    public void Show(string html, bool allowRemote)
    {
        _allowRemote = allowRemote;
        var wv = _ctl.CoreWebView2;
        // NavigateToString 上限大約 2 MB（內嵌圖多嘅信會超過）：大嘅寫落暫存檔再開
        if (html.Length < 1_500_000) { wv.NavigateToString(html); return; }
        string dir = Path.Combine(MailStore.Dir, "tmp");
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "view.html");
        File.WriteAllText(f, html);
        _tempUri = new Uri(f).AbsoluteUri;
        wv.Navigate(_tempUri);
    }
}

/// <summary>睇信嘅格（WebView2 或者 IE 核心）。</summary>
internal sealed class MailView : Panel
{
    /// <summary>撳咗連結（http / mailto …）。</summary>
    public event Action<string>? LinkClicked;
    /// <summary>喺信入面撳 Esc。</summary>
    public event Action? EscPressed;

    private object? _wv2;            // Wv2Host（用 object：未準備好 DLL 之前唔好掂到 WebView2 型別）
    private WebBrowser? _ie;
    private int _state;              // 0 = 未試，1 = 準備緊，2 = WebView2，3 = IE
    private string? _html;
    private bool _remote;

    public bool IsWebView2 => _state == 2;

    public MailView()
    {
        BackColor = Color.White;
    }

    public void Show(string html, bool allowRemote)
    {
        _html = html;
        _remote = allowRemote;
        if (_state == 0) { _state = 1; _ = InitAsync(); return; }
        if (_state == 1) return; // 準備好會自己顯示
        Render();
    }

    public void Clear() => Show("<html><body style='background:#fff'></body></html>", false);

    private async Task InitAsync()
    {
        try
        {
            if (WebView2Lib.Prepare())
            {
                _wv2 = await CreateWv2();
                _state = 2;
            }
            else _state = 3;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("WebView2 開唔到，用返 IE 核心", ex);
            _state = 3;
        }
        if (!IsDisposed) Render();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task<object> CreateWv2() =>
        await Wv2Host.CreateAsync(this, u => LinkClicked?.Invoke(u), () => EscPressed?.Invoke());

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ShowWv2(string html, bool remote) => ((Wv2Host)_wv2!).Show(html, remote);

    private void Render()
    {
        if (_html == null) return;
        if (_state == 2)
        {
            try { ShowWv2(_html, _remote); return; }
            catch (Exception ex) { AppPaths.LogError("WebView2 顯示", ex); }
        }
        EnsureIe();
        _ie!.DocumentText = _remote ? _html : MailHtml.BlockRemote(_html);
    }

    private void EnsureIe()
    {
        if (_ie != null) return;
        _ie = new WebBrowser
        {
            Dock = DockStyle.Fill, ScriptErrorsSuppressed = true, AllowWebBrowserDrop = false, IsWebBrowserContextMenuEnabled = true,
            WebBrowserShortcutsEnabled = true
        };
        _ie.Navigating += (_, e) =>
        {
            var u = e.Url;
            if (u == null || u.Scheme == "about") return;
            e.Cancel = true;
            LinkClicked?.Invoke(u.OriginalString);
        };
        _ie.NewWindow += (_, e) => e.Cancel = true;
        _ie.PreviewKeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) EscPressed?.Invoke(); };
        Controls.Add(_ie);
        _ie.SendToBack();
    }
}

/// <summary>處理信件 HTML：遠端內容（圖片、CSS、字型）。</summary>
internal static class MailHtml
{
    private static readonly Regex RemoteRx = new(
        @"(?:\b(?:src|srcset|background|poster)\s*=\s*[""']?\s*(?:https?:)?//)|(?:url\(\s*[""']?\s*(?:https?:)?//)|(?:<link\b[^>]*\bhref\s*=\s*[""']?\s*(?:https?:)?//)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>封信有冇遠端內容（圖片之類）。</summary>
    public static bool HasRemote(string html) => RemoteRx.IsMatch(html);

    /// <summary>IE 核心用：將遠端圖片 / CSS / 背景圖拆走（WebView2 直接喺網絡層擋）。</summary>
    public static string BlockRemote(string html)
    {
        const RegexOptions o = RegexOptions.IgnoreCase;
        html = Regex.Replace(html, @"\b(src|srcset|background|poster)(\s*=\s*[""']?\s*)((?:https?:)?//)", "data-blocked-$1$2$3", o);
        html = Regex.Replace(html, @"url\(\s*[""']?\s*(?:https?:)?//[^)]*\)", "none", o);
        html = Regex.Replace(html, @"<link\b[^>]*\bhref\s*=\s*[""']?\s*(?:https?:)?//[^>]*>", "", o);
        return html;
    }
}
