using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using MultiTool.UI;

namespace MultiTool.Mail;

// =====================================================================
//  寫信嘅內文編輯器（WebView2）：好似 Gmail 咁可以直接貼圖（Ctrl+V 截圖）、拖相入嚟，圖會顯示喺內文入面，
//  寄出時變做內嵌圖片（cid），唔使逐張加附件。冇 WebView2 嘅電腦用返普通文字格（貼圖會變附件）。
//  所有用到 WebView2 型別嘅嘢都喺呢個 class（WebView2Lib.Prepare 之後先會掂到）。
// =====================================================================
internal sealed class ComposeWeb
{
    private CoreWebView2Controller _ctl = null!;
    private bool _loaded;

    /// <summary>用家改過內文。</summary>
    public bool Dirty;
    /// <summary>喺內文撳 Ctrl + Enter。</summary>
    public event Action? Send;
    /// <summary>拖咗唔係圖片嘅檔案入嚟（真實路徑）：當附件。</summary>
    public event Action<string[]>? Files;

    public static async Task<ComposeWeb> CreateAsync(Control host, string placeholder)
    {
        var w = new ComposeWeb();
        string data = Path.Combine(MailStore.Dir, "webview");   // 同睇信共用（同一個瀏覽器程序）
        Directory.CreateDirectory(data);
        var env = await CoreWebView2Environment.CreateAsync(null, data);
        w._ctl = await env.CreateCoreWebView2ControllerAsync(host.Handle);
        w._ctl.DefaultBackgroundColor = Ui.Input;
        try { w._ctl.AllowExternalDrop = true; } catch { }
        w._ctl.Bounds = host.ClientRectangle;
        host.Resize += (_, _) => { try { w._ctl.Bounds = host.ClientRectangle; } catch { } };
        host.Disposed += (_, _) => { try { w._ctl.Close(); } catch { } };
        var wv = w._ctl.CoreWebView2;
        var st = wv.Settings;
        st.IsScriptEnabled = true;
        st.IsWebMessageEnabled = true;
        st.AreHostObjectsAllowed = false;
        st.AreDevToolsEnabled = false;
        st.IsStatusBarEnabled = false;
        st.AreBrowserAcceleratorKeysEnabled = false;   // F5 / Ctrl+R 會成頁重新載入、寫咗嘅嘢冇晒
        st.IsPasswordAutosaveEnabled = false;
        st.IsGeneralAutofillEnabled = false;
        st.IsZoomControlEnabled = false;
        // 載入完編輯器之後唔准再去其他網頁（例如拖咗條連結入嚟）
        wv.NavigationStarting += (_, e) => { if (w._loaded) e.Cancel = true; };
        wv.NewWindowRequested += (_, e) => e.Handled = true;
        wv.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                string t = doc.RootElement.TryGetProperty("t", out var x) ? x.GetString() ?? "" : "";
                if (t == "dirty") w.Dirty = true;
                else if (t == "send") w.Send?.Invoke();
                else if (t == "files")
                {
                    var paths = new List<string>();
                    if (e.AdditionalObjects != null)
                        foreach (var o in e.AdditionalObjects)
                            if (o is CoreWebView2File f && File.Exists(f.Path)) paths.Add(f.Path);
                    if (paths.Count > 0) w.Files?.Invoke(paths.ToArray());
                }
            }
            catch (Exception ex) { AppPaths.LogError("寫信編輯器訊息", ex); }
        };
        var done = new TaskCompletionSource<bool>();
        wv.NavigationCompleted += (_, e) => { w._loaded = true; done.TrySetResult(e.IsSuccess); };
        wv.NavigateToString(ComposeEditor.Html(placeholder));
        if (!await done.Task) throw new InvalidOperationException("editor");
        return w;
    }

    private async Task<string> Js(string script) => await _ctl.CoreWebView2.ExecuteScriptAsync(script);

    /// <summary>內文：HTML（圖片仲係 data: 網址）同純文字（圖片寫做 [圖片]）。</summary>
    public async Task<(string Html, string Text)> GetAsync()
    {
        string r = await Js("getContent()");
        string json = JsonSerializer.Deserialize<string>(r) ?? "{}";
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.GetProperty("html").GetString() ?? "", doc.RootElement.GetProperty("text").GetString() ?? "");
    }

    /// <summary>換內文文字（keepImages = 保留原本嘅圖片，放喺文字後面）。</summary>
    public Task SetTextAsync(string text, bool keepImages) => Js($"setText({JsonSerializer.Serialize(text)},{(keepImages ? "true" : "false")})");
    public Task SetHtmlAsync(string html) => Js($"setHtml({JsonSerializer.Serialize(html)})");
    public void SetReadOnly(bool ro) => _ = Js($"ed.contentEditable={(ro ? "'false'" : "'true'")}");

    public void FocusEditor()
    {
        try { _ctl.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch { }
        _ = Js("focusEd()");
    }
}

/// <summary>編輯器網頁（自己寫，冇第三方程式庫）。</summary>
internal static class ComposeEditor
{
    /// <summary>純文字版本入面代表圖片嘅字。</summary>
    public static string ImageLabel => Lang.T("[圖片]");
    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static string Html(string placeholder) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>" +
        "html,body{margin:0;height:100%;background:" + Hex(Ui.Input) + ";color:" + Hex(Ui.Text) + ";font:15px \"Segoe UI\",\"Microsoft JhengHei UI\",\"Microsoft YaHei UI\",\"Yu Gothic UI\",\"Malgun Gothic\",sans-serif}" +
        "#ed{box-sizing:border-box;min-height:100%;padding:8px 10px;outline:none;line-height:1.5;word-wrap:break-word;white-space:pre-wrap}" +
        "#ed:empty:before{content:attr(data-ph);color:" + Hex(Ui.TextFaint) + "}" +
        "#ed img{max-width:100%;height:auto;vertical-align:bottom}" +
        "::selection{background:" + Hex(Ui.Accent) + ";color:#fff}" +
        "#tmp{position:fixed;left:-100000px;top:0;width:800px;white-space:pre-wrap}" +
        "</style></head><body><div id=\"ed\" contenteditable=\"true\" spellcheck=\"false\" data-ph=\"" + System.Net.WebUtility.HtmlEncode(placeholder) + "\"></div><script>const IMGLABEL = " + JsonSerializer.Serialize(ImageLabel) + ";" + Script + "</script></body></html>";

    /// <summary>tools 測試都用呢段（web/office 冇關）。</summary>
    public const string Script = """
const ed = document.getElementById('ed');
const host = window.chrome && chrome.webview ? chrome.webview : null;
const post = (m, objs) => { try { if (!host) return; if (objs && host.postMessageWithAdditionalObjects) host.postMessageWithAdditionalObjects(m, objs); else host.postMessage(m); } catch (e) { } };
const isImg = f => /^image\/(png|jpe?g|gif|webp|bmp)$/i.test(f.type || '');
const readUrl = f => new Promise((ok, no) => { const r = new FileReader(); r.onload = () => ok(r.result); r.onerror = no; r.readAsDataURL(f); });
// 太大嘅相（例如手機相）縮到闊度 2000 以內再放入去，費事封信大到寄唔到
async function fitUrl(f) {
  const url = await readUrl(f);
  if (f.size < 1500000 || /gif/i.test(f.type)) return url;
  try {
    const img = await new Promise((ok, no) => { const i = new Image(); i.onload = () => ok(i); i.onerror = no; i.src = url; });
    const k = Math.min(1, 2000 / Math.max(img.naturalWidth, img.naturalHeight));
    const c = document.createElement('canvas'); c.width = Math.round(img.naturalWidth * k); c.height = Math.round(img.naturalHeight * k);
    c.getContext('2d').drawImage(img, 0, 0, c.width, c.height);
    const png = /png/i.test(f.type);
    const out = c.toDataURL(png ? 'image/png' : 'image/jpeg', 0.88);
    return out.length < url.length ? out : url;
  } catch (e) { return url; }
}
async function insertFiles(files) {
  const other = [];
  for (const f of files) {
    if (!isImg(f)) { other.push(f); continue; }
    const url = await fitUrl(f);
    ed.focus();
    document.execCommand('insertImage', false, url);
  }
  if (other.length) post({ t: 'files' }, other);
  dirty();
}
let dirtySent = false;
const dirty = () => { if (!dirtySent) { dirtySent = true; post({ t: 'dirty' }); } };
ed.addEventListener('input', dirty);
// 貼上：有圖就放圖；文字一律當純文字貼（費事帶埋網頁嘅字型、顏色）
ed.addEventListener('paste', e => {
  const dt = e.clipboardData; if (!dt) return;
  const files = [...dt.files];
  if (!files.length) for (const it of dt.items || []) if (it.kind === 'file') { const f = it.getAsFile(); if (f) files.push(f); }
  e.preventDefault();
  if (files.length) { insertFiles(files); return; }
  const t = dt.getData('text/plain');
  if (t) document.execCommand('insertText', false, t);
});
document.addEventListener('dragover', e => { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; });
document.addEventListener('drop', e => {
  e.preventDefault();
  const dt = e.dataTransfer; if (!dt) return;
  const files = [...dt.files];
  const r = document.caretRangeFromPoint ? document.caretRangeFromPoint(e.clientX, e.clientY) : null;
  if (r && ed.contains(r.startContainer)) { const s = getSelection(); s.removeAllRanges(); s.addRange(r); } else ed.focus();
  if (files.length) { insertFiles(files); return; }
  const t = dt.getData('text/plain');
  if (t) { document.execCommand('insertText', false, t); }
});
document.addEventListener('keydown', e => { if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); post({ t: 'send' }); } });
document.body.addEventListener('mousedown', e => { if (e.target === document.body || e.target === document.documentElement) { e.preventDefault(); ed.focus(); const s = getSelection(); s.selectAllChildren(ed); s.collapseToEnd(); } });
// 讀內文：純文字用畫面上見到嘅換行（innerText 要掛喺頁面先計得啱），圖片寫做 [圖片]
function getContent() {
  const c = ed.cloneNode(true); c.id = 'tmp'; c.removeAttribute('contenteditable');
  c.querySelectorAll('img').forEach(i => i.replaceWith(document.createTextNode(typeof IMGLABEL === 'string' ? IMGLABEL : '[image]')));
  document.body.appendChild(c);
  const text = c.innerText; c.remove();
  return JSON.stringify({ html: ed.innerHTML, text: text.replace(/ /g, ' ') });
}
function setText(t, keepImgs) {
  const imgs = keepImgs ? [...ed.querySelectorAll('img')].map(i => i.src) : [];
  ed.textContent = '';
  String(t).replace(/\r\n/g, '\n').split('\n').forEach((line, i) => { if (i) ed.appendChild(document.createElement('br')); if (line) ed.appendChild(document.createTextNode(line)); });
  for (const s of imgs) { const d = document.createElement('div'); const im = document.createElement('img'); im.src = s; d.appendChild(im); ed.appendChild(d); }
}
function setHtml(h) { ed.innerHTML = h; }
function focusEd() { ed.focus(); const s = getSelection(); s.removeAllRanges(); const r = document.createRange(); r.setStart(ed, 0); r.collapse(true); s.addRange(r); ed.scrollTop = 0; scrollTo(0, 0); }
""";

    // ---------------- 寄出：編輯器 HTML → 信件 HTML + 內嵌圖片 ----------------
    private static readonly Regex ImgRx = new(@"(<img\b[^>]*?\bsrc\s*=\s*"")data:(image/(?:png|jpeg|jpg|gif|webp|bmp));base64,([A-Za-z0-9+/=\s]+)("")", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>將 data: 圖片抽出嚟變 cid: 內嵌圖片；其他 &lt;img&gt;（例如網上圖片）原封不動。</summary>
    public static (string Html, List<Mime.InlineImage> Images) ToMail(string editorHtml, string domain)
    {
        var imgs = new List<Mime.InlineImage>();
        string html = ImgRx.Replace(editorHtml, m =>
        {
            byte[] data;
            try { data = Convert.FromBase64String(Regex.Replace(m.Groups[3].Value, @"\s", "")); } catch { return m.Value; }
            string type = m.Groups[2].Value.ToLowerInvariant() == "image/jpg" ? "image/jpeg" : m.Groups[2].Value.ToLowerInvariant();
            string cid = "img" + (imgs.Count + 1) + "." + Guid.NewGuid().ToString("N")[..12] + "@" + domain;
            imgs.Add(new Mime.InlineImage(cid, type, data));
            return m.Groups[1].Value + "cid:" + cid + m.Groups[4].Value;
        });
        // 文字入面嘅換行（insertText 會留低 \n）變 <br>：有啲郵件程式唔認 white-space
        html = html.Replace("\r\n", "\n").Replace("\n", "<br>");
        // 圖片唔好闊過收件人個畫面
        html = Regex.Replace(html, @"<img\b(?![^>]*\bstyle=)", "<img style=\"max-width:100%\"", RegexOptions.IgnoreCase);
        return (html, imgs);
    }

    /// <summary>成封信嘅 HTML（內文 + 引用原文）。</summary>
    public static string Document(string inner, string quoteHtml) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>" +
        "<div style=\"font-family:Arial,'Microsoft JhengHei','PingFang TC',sans-serif;font-size:14px;line-height:1.5;white-space:pre-wrap\">" + inner + "</div>" +
        quoteHtml + "</body></html>";

    /// <summary>純文字 → HTML（保留換行）。</summary>
    public static string TextHtml(string s) => System.Net.WebUtility.HtmlEncode(s.Replace("\r\n", "\n")).Replace("\n", "<br>");
}
