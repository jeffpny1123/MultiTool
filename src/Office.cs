using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using MultiTool.UI;

namespace MultiTool.Tools;

// =====================================================================
//  文書工具：文件（.docx）、試算表（.xlsx）、簡報（.pptx）、PDF 集合喺一個工具，開乜檔就用乜編輯器
//  • 文件（.docx）：@eigenpal/docx-editor；試算表（.xlsx）：Univer + ExcelJS；簡報（.pptx）：pptx-browser；PDF：pdf.js + pdf-lib
//  • 每種編輯器一頁：index.html（首頁 + 文件）、sheet.html、slides.html、pdf/editor.html；開另一種檔案時成頁轉過去
//  • PDF 用返 PDF 修改器嗰頁（lib\pdfedit，喺 https://office.rabi/pdf/ 下面），開 PDF 時成頁轉過去，「返回」返首頁
//  • 介面係 lib\office\index.html（WebView2）；文件編輯器用 @eigenpal/docx-editor（Apache-2.0），原始碼喺 web\office
//  • 成個 lib\office 資料夾壓做 office.zip 嵌入 exe，第一次用解壓去 data\lib\office
//  • 檔案全部喺本機處理：頁面只可以讀自己嘅檔案（其他網絡請求一律擋）
//  • 有連接 AI 先顯示 AI 掣：問文件內容，或者叫 AI 幫手改
// =====================================================================
internal static class OfficeLib
{
    public const string Host = "office.rabi";
    public static string Dir { get; private set; } = "";

    /// <summary>支援嘅副檔名（細楷，連點）。</summary>
    public static readonly string[] Exts = { ".docx", ".xlsx", ".pptx", ".pdf" };
    public static bool IsPdf(string path) => path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    /// <summary>邊種檔案用邊頁開：home（首頁 + 文件）/ sheet / slides / pdf。</summary>
    public static string PageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch { ".pdf" => "pdf", ".xlsx" => "sheet", ".pptx" => "slides", ".dxf" => "cad", _ => "home" };
    public static string PageUrl(string page) => "https://" + Host + "/" + page switch { "pdf" => "pdf/editor.html", "sheet" => "sheet.html", "slides" => "slides.html", "cad" => "cad.html", _ => "index.html" };
    /// <summary>每頁儲存用嘅副檔名（冇點）。</summary>
    public static string ExtOf(string page) => page switch { "sheet" => "xlsx", "slides" => "pptx", "pdf" => "pdf", "cad" => "dxf", _ => "docx" };
    public static bool Supported(string path) => Array.IndexOf(Exts, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>解壓嵌入嘅編輯器檔案（zip 大細變咗 = 新版本，重新解壓）。</summary>
    public static bool Prepare()
    {
        try
        {
            using var s = typeof(OfficeLib).Assembly.GetManifestResourceStream("office.zip") ?? throw new FileNotFoundException("office.zip");
            string dir = Path.Combine(AppPaths.Data, "lib", "office");
            string mark = Path.Combine(dir, ".size");
            string want = s.Length.ToString();
            if (!File.Exists(mark) || File.ReadAllText(mark) != want || !File.Exists(Path.Combine(dir, "index.html")))
            {
                if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { }
                Directory.CreateDirectory(dir);
                using (var zip = new ZipArchive(s, ZipArchiveMode.Read)) zip.ExtractToDirectory(dir, true);
                File.WriteAllText(mark, want);
            }
            Dir = dir;
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("文書工具準備", ex);
            return false;
        }
    }

    /// <summary>頁面用嘅介面文字同設定（strings.js）。</summary>
    public static string StringsJs(bool ai)
    {
        var l = new Dictionary<string, string>
        {
            ["title"] = Lang.T("文書工具"),
            ["sub"] = Lang.T("開啟同編輯文件、試算表、簡報同 PDF：開乜檔案就用對應嘅編輯器。檔案只會喺你部電腦處理，唔會上載。"),
            ["openfile"] = Lang.T("開啟檔案"), ["drop"] = Lang.T("或者將檔案拖入呢個視窗"),
            ["newwin"] = Lang.T("開新視窗"), ["defapps"] = Lang.T("設為預設程式…"),
            ["newt"] = Lang.T("新增或者開啟"), ["new"] = Lang.T("新增"), ["open"] = Lang.T("開啟"), ["soon"] = Lang.T("即將推出"),
            ["k_docx"] = Lang.T("文件"), ["k_xlsx"] = Lang.T("試算表"), ["k_pptx"] = Lang.T("簡報"), ["k_pdf"] = "PDF",
            ["recent"] = Lang.T("最近開過"), ["recentclear"] = Lang.T("清除紀錄"), ["recentdel"] = Lang.T("由紀錄移除"),
            ["back"] = Lang.T("← 返回"), ["save"] = Lang.T("儲存"), ["saveas"] = Lang.T("另存新檔"), ["print"] = Lang.T("列印"), ["more"] = Lang.T("更多"),
            ["cover"] = Lang.T("封面"), ["header"] = Lang.T("頁首"), ["footer"] = Lang.T("頁尾"),
            ["cover_tip"] = Lang.T("喺文件最頂加一頁封面（標題、副標題、作者同日期），加咗之後直接改文字。"),
            ["hf_tip"] = Lang.T("編輯頁首 / 頁尾（雙擊頁面頂部或者底部都得）；「選項」可以加頁碼。"),
            ["m_edit"] = Lang.T("編輯"), ["m_view"] = Lang.T("檢視"), ["m_tools"] = Lang.T("工具"),
            ["undo"] = Lang.T("復原"), ["redo"] = Lang.T("重做"), ["selectall"] = Lang.T("全選"), ["findreplace"] = Lang.T("尋找及取代"),
            ["mode_edit"] = Lang.T("編輯模式"), ["mode_suggest"] = Lang.T("建議模式（追蹤修訂）"), ["mode_view"] = Lang.T("檢視模式（唯讀）"),
            ["ruler"] = Lang.T("顯示尺規"), ["outline"] = Lang.T("顯示文件大綱"),
            ["wordcount"] = Lang.T("字數統計"), ["wc_pages"] = Lang.T("頁數"), ["wc_words"] = Lang.T("字數"), ["wc_chars"] = Lang.T("字元數"), ["wc_nospace"] = Lang.T("字元數（不含空格）"),
            ["close"] = Lang.T("關閉"), ["heading_n"] = Lang.T("標題 {0}"),
            // ---- 試算表 / 簡報共用 ----
            ["m_file"] = Lang.T("檔案"), ["m_insert"] = Lang.T("插入"), ["m_format"] = Lang.T("格式"), ["m_data"] = Lang.T("資料"), ["m_slide"] = Lang.T("投影片"), ["m_arrange"] = Lang.T("排列"),
            ["m_new"] = Lang.T("新增"), ["rename_tip"] = Lang.T("撳一下改名"), ["ai_attach"] = Lang.T("附加圖片或者檔案俾 AI 睇（今次發問用完就刪）"), ["ai_once"] = Lang.T("對話同附件都唔會儲存。"), ["cancel"] = Lang.T("取消"), ["ok"] = Lang.T("確定"),
            ["cut"] = Lang.T("剪下"), ["copy"] = Lang.T("複製"), ["paste"] = Lang.T("貼上"), ["paste_tip"] = Lang.T("請用 Ctrl+V 貼上。"),
            ["bold"] = Lang.T("粗體"), ["italic"] = Lang.T("斜體"), ["underline"] = Lang.T("底線"),
            ["al_left"] = Lang.T("靠左對齊"), ["al_center"] = Lang.T("置中對齊"), ["al_right"] = Lang.T("靠右對齊"), ["al_top"] = Lang.T("靠上"), ["al_middle"] = Lang.T("垂直置中"), ["al_bottom"] = Lang.T("靠下"),
            // ---- 試算表 ----
            ["sh_untitled"] = Lang.T("未命名試算表"), ["sh_sheet"] = Lang.T("工作表"), ["sh_rename"] = Lang.T("重新命名工作表"), ["sh_csv"] = Lang.T("下載而家嘅工作表（CSV）"),
            ["sh_lossy_t"] = Lang.T("部分內容顯示唔到"),
            ["sh_lossy"] = Lang.T("呢個檔案有：{0}。呢度顯示唔到，儲存嘅話亦唔會保留，所以儲存時會叫你另存新檔，唔會覆寫原檔。"),
            ["sh_l_chart"] = Lang.T("圖表"), ["sh_l_image"] = Lang.T("圖片"), ["sh_l_pivot"] = Lang.T("樞紐分析表"), ["sh_l_cf"] = Lang.T("條件式格式"), ["sh_l_dv"] = Lang.T("資料驗證"),
            ["sh_delvals"] = Lang.T("刪除值"), ["sh_delrow"] = Lang.T("刪除所選嘅列"), ["sh_delcol"] = Lang.T("刪除所選嘅欄"),
            ["sh_freeze"] = Lang.T("凍結"), ["sh_fz_none"] = Lang.T("取消凍結"), ["sh_fz_r1"] = Lang.T("1 列"), ["sh_fz_r2"] = Lang.T("2 列"), ["sh_fz_c1"] = Lang.T("1 欄"), ["sh_fz_c2"] = Lang.T("2 欄"),
            ["sh_grid"] = Lang.T("顯示格線"), ["sh_zoom"] = Lang.T("縮放"),
            ["sh_row_above"] = Lang.T("喺上面插入列"), ["sh_row_below"] = Lang.T("喺下面插入列"), ["sh_col_left"] = Lang.T("喺左邊插入欄"), ["sh_col_right"] = Lang.T("喺右邊插入欄"),
            ["ch_insert"] = Lang.T("圖表"), ["ch_edit"] = Lang.T("編輯圖表"), ["ch_delete"] = Lang.T("刪除圖表"), ["ch_series"] = Lang.T("系列 {0}"),
            ["ch_type"] = Lang.T("圖表類型"), ["ch_title"] = Lang.T("圖表標題"), ["ch_range"] = Lang.T("資料範圍"),
            ["ch_range_tip"] = Lang.T("例如 A1:C6。第一列有文字會當做系列名，第一欄有文字會當做分類。"),
            ["ch_range_keep"] = Lang.T("留空 = 保留原本嘅資料範圍。"),
            ["ch_bad_range"] = Lang.T("呢個範圍整唔到圖表：要有最少一欄數字。"),
            ["ch_t_column"] = Lang.T("直條圖"), ["ch_t_bar"] = Lang.T("橫條圖"), ["ch_t_line"] = Lang.T("折線圖"), ["ch_t_area"] = Lang.T("區域圖"), ["ch_t_pie"] = Lang.T("圓形圖"),
            ["sh_newsheet"] = Lang.T("新工作表"), ["sh_function"] = Lang.T("函式"),
            ["sh_number"] = Lang.T("數值"), ["sh_n_auto"] = Lang.T("自動"), ["sh_n_num"] = Lang.T("數字"), ["sh_n_pct"] = Lang.T("百分比"), ["sh_n_cur"] = Lang.T("貨幣"), ["sh_n_date"] = Lang.T("日期"), ["sh_n_time"] = Lang.T("時間"), ["sh_n_text"] = Lang.T("純文字"),
            ["sh_text"] = Lang.T("文字"), ["sh_align"] = Lang.T("對齊"), ["sh_wrap"] = Lang.T("文字換行"), ["sh_wrap_over"] = Lang.T("溢出"), ["sh_wrap_wrap"] = Lang.T("自動換行"),
            ["sh_merge"] = Lang.T("合併儲存格"), ["sh_merge_all"] = Lang.T("全部合併"), ["sh_merge_h"] = Lang.T("水平合併"), ["sh_merge_v"] = Lang.T("垂直合併"), ["sh_unmerge"] = Lang.T("取消合併"),
            ["sh_clearfmt"] = Lang.T("清除格式"),
            ["sh_sort_az"] = Lang.T("排序範圍（A → Z）"), ["sh_sort_za"] = Lang.T("排序範圍（Z → A）"), ["sh_sort_custom"] = Lang.T("自訂排序…"), ["sh_filter"] = Lang.T("建立 / 移除篩選器"),
            ["sh_stats"] = Lang.T("試算表統計"), ["sh_stats_body"] = Lang.T("工作表：{0} 個\n而家呢個工作表：{1} 列 × {2} 欄有資料，{3} 格有內容"),
            // ---- 簡報 ----
            ["sl_untitled"] = Lang.T("未命名簡報"), ["sl_slide"] = Lang.T("投影片"), ["sl_new"] = Lang.T("新投影片"), ["sl_dup"] = Lang.T("複製投影片"), ["sl_del"] = Lang.T("刪除投影片"),
            ["sl_up"] = Lang.T("投影片上移"), ["sl_down"] = Lang.T("投影片下移"), ["sl_bg"] = Lang.T("背景顏色"), ["sl_last"] = Lang.T("最少要有一張投影片。"),
            ["sl_textbox"] = Lang.T("文字方塊"), ["sl_image"] = Lang.T("圖片"), ["sl_newtext"] = Lang.T("雙擊輸入文字"), ["sl_title_ph"] = Lang.T("撳兩下加標題"), ["sl_sub_ph"] = Lang.T("撳兩下加副標題"),
            ["sl_size"] = Lang.T("文字大小"), ["sl_color"] = Lang.T("文字顏色"), ["sl_delobj"] = Lang.T("刪除所選物件"), ["sl_pick"] = Lang.T("請先揀一個物件。"),
            ["sl_play"] = Lang.T("播放"), ["sl_play0"] = Lang.T("由頭播放"), ["sl_shownotes"] = Lang.T("顯示演講者備忘稿"),
            ["sl_notes_ph"] = Lang.T("撳一下加演講者備忘稿"), ["sl_notes_none"] = Lang.T("呢張投影片冇備忘稿（暫時只可以改原本已經有嘅備忘稿）"),
            ["sl_pdf"] = Lang.T("下載為 PDF"), ["sl_preparing"] = Lang.T("準備緊…"),
            ["sl_front"] = Lang.T("移到最上層"), ["sl_back"] = Lang.T("移到最下層"), ["sl_center_h"] = Lang.T("水平置中"), ["sl_center_v"] = Lang.T("垂直置中"),
            ["sl_stats"] = Lang.T("簡報統計"), ["sl_stats_body"] = Lang.T("投影片：{0} 張\n文字：{1} 個字元"),
            ["cv_title"] = Lang.T("文件標題"), ["cv_sub"] = Lang.T("副標題"), ["cv_author"] = Lang.T("作者"),
            ["saving"] = Lang.T("儲存緊…"), ["saved"] = Lang.T("已儲存：{0}"), ["savefail"] = Lang.T("儲存失敗：{0}"),
            ["loading"] = Lang.T("載入緊…"), ["openfail"] = Lang.T("開唔到呢個檔案"), ["notsupported"] = Lang.T("呢種檔案暫時未支援。"),
            ["leave"] = Lang.T("有未儲存嘅改動，確定要離開？"), ["untitled"] = Lang.T("未命名文件"),
            ["aititle"] = Lang.T("AI 助手"), ["aiask"] = Lang.T("問 AI，或者叫佢幫你改…"), ["aisend"] = Lang.T("傳送"),
            ["aisum"] = Lang.T("摘要呢份文件嘅內容"), ["aifix"] = Lang.T("檢查錯字同文法，直接幫我改好"),
            ["aiyou"] = Lang.T("你"), ["ainame"] = "AI", ["aithinking"] = Lang.T("AI 諗緊…"),
            ["aidone"] = Lang.T("已套用 {0} 項改動（可以撳「復原」還原）"), ["ainone"] = Lang.T("AI 想改嘅位置搵唔到，冇改到嘢。"), ["aifail"] = Lang.T("AI 回覆失敗："),
        };
        CadStrings.Add(l);
        string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        var cfg = new
        {
            ai,
            light = Ui.Light,
            lang = Lang.Code(Lang.Current),
            recent = OfficeRecent.ForJs(),
            colors = new Dictionary<string, string>
            {
                ["bg"] = Hex(Ui.Bg), ["card"] = Hex(Ui.Card), ["hi"] = Hex(Ui.CardHi), ["text"] = Hex(Ui.Text), ["dim"] = Hex(Ui.TextDim),
                ["border"] = Hex(Ui.Border), ["accent"] = Hex(Ui.Accent), ["input"] = Hex(Ui.Input), ["danger"] = Hex(Ui.Danger), ["ok"] = Hex(Ui.Success)
            }
        };
        var opt = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return "window.L=" + JsonSerializer.Serialize(l, opt) + ";window.CFG=" + JsonSerializer.Serialize(cfg, opt) + ";";
    }

    /// <summary>開檔 / 儲存對話框嘅檔案類型。</summary>
    public static string Filter(string kind) => kind switch
    {
        "docx" => Lang.T("文件") + " (*.docx)|*.docx",
        "xlsx" => Lang.T("試算表") + " (*.xlsx)|*.xlsx",
        "pptx" => Lang.T("簡報") + " (*.pptx)|*.pptx",
        "pdf" => "PDF (*.pdf)|*.pdf",
        "csv" => "CSV (*.csv)|*.csv",
        "dxf" => Lang.T("圖面") + " (*.dxf)|*.dxf",
        "svg" => "SVG (*.svg)|*.svg",
        "png" => "PNG (*.png)|*.png",
        _ => Lang.T("支援嘅檔案") + " (*.docx;*.xlsx;*.pptx;*.pdf)|*.docx;*.xlsx;*.pptx;*.pdf|" + Lang.T("文件") + " (*.docx)|*.docx|" + Lang.T("試算表") + " (*.xlsx)|*.xlsx|" + Lang.T("簡報") + " (*.pptx)|*.pptx|PDF (*.pdf)|*.pdf"
    };
}

/// <summary>文書工具嘅 WebView2（WebView2 類型只可以喺呢個 class 出現）。</summary>
internal sealed class OfficeWeb
{
    private CoreWebView2Controller _ctl = null!;
    private Control _host = null!;
    private MemoryStream? _save;
    private string _saveName = "";
    private bool _saveAs, _saveExport;
    private string? _pending;                                   // 頁面準備好就開呢個檔
    private string? _curPath;                                   // 而家開緊嘅檔案（null = 未儲存過嘅新文件）
    private string _page = "";                                  // 而家顯示緊邊頁：home（首頁 + 文件）/ sheet / slides / pdf
    private readonly HashSet<string> _backed = new(StringComparer.OrdinalIgnoreCase);   // 今次已經備份過嘅檔案
    private readonly Dictionary<string, string> _files = new(); // 虛擬網址 token → 真實路徑（只可以讀主程式自己交出去嘅檔案）
    private int _fileSeq;
    /// <summary>開咗檔案（俾獨立視窗改標題）。</summary>
    public Action<string>? Opened;

    private bool _cad;   // 繪圖工具（獨立一個工具，同文書工具分開）：只開 .dxf，冇首頁
    public static async Task<OfficeWeb> CreateAsync(Control host, string? openPath = null, bool cad = false)
    {
        string data = Path.Combine(AppPaths.Data, "webview", "office");
        Directory.CreateDirectory(data);
        var env = await CoreWebView2Environment.CreateAsync(null, data);
        var w = new OfficeWeb { _host = host, _pending = openPath, _cad = cad, _ctl = await env.CreateCoreWebView2ControllerAsync(host.Handle) };
        w._ctl.DefaultBackgroundColor = Ui.Bg;
        try { w._ctl.AllowExternalDrop = true; } catch { }
        w._ctl.Bounds = host.ClientRectangle;
        host.Resize += (_, _) => { try { w._ctl.Bounds = host.ClientRectangle; } catch { } };
        host.VisibleChanged += (_, _) =>
        {
            try
            {
                w._ctl.IsVisible = host.Visible;
                if (!host.Visible) return;
                w._ctl.Bounds = host.ClientRectangle;
                w._ctl.NotifyParentWindowPositionChanged();
                // 返嚟呢頁：AI 可能啱啱連接咗 / 斷咗
                w.Post(new JsonObject { ["t"] = "cfg", ["ai"] = PdfEditLib.AiReady() });
            }
            catch { }
        };
        var wv = w._ctl.CoreWebView2;
        try { wv.Profile.PreferredColorScheme = Ui.Light ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark; } catch { }
        var st = wv.Settings;
        st.AreDevToolsEnabled = false;
        st.IsStatusBarEnabled = false;
        st.AreDefaultContextMenusEnabled = false;
        st.AreHostObjectsAllowed = false;
        st.IsPasswordAutosaveEnabled = false;
        st.IsGeneralAutofillEnabled = false;
        st.AreBrowserAcceleratorKeysEnabled = false;
        string origin = "https://" + OfficeLib.Host + "/";
        wv.NavigationStarting += (_, e) => { if (!(e.Uri ?? "").StartsWith(origin, StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
        wv.NewWindowRequested += (_, e) => e.Handled = true;
        // 只准讀編輯器自己嘅檔案；其他網絡請求一律擋（文件內容唔會離開部電腦，亦唔會上網攞字型）
        wv.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        wv.WebResourceRequested += (_, e) =>
        {
            string u = e.Request.Uri ?? "";
            if (u.StartsWith("data:") || u.StartsWith("blob:") || u.StartsWith("about:")) return;
            try
            {
                if (u.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = Uri.UnescapeDataString(new Uri(u).AbsolutePath.TrimStart('/'));
                    if (rel == "pdf/strings.js")
                    {
                        var pj = Encoding.UTF8.GetBytes(PdfEditLib.StringsJs(PdfEditLib.AiReady()));
                        e.Response = env.CreateWebResourceResponse(new MemoryStream(pj), 200, "OK", "Content-Type: text/javascript; charset=utf-8\r\nCache-Control: no-store");
                        return;
                    }
                    if (rel == "strings.js")
                    {
                        var js = Encoding.UTF8.GetBytes(OfficeLib.StringsJs(PdfEditLib.AiReady()));
                        e.Response = env.CreateWebResourceResponse(new MemoryStream(js), 200, "OK", "Content-Type: text/javascript; charset=utf-8\r\nCache-Control: no-store");
                        return;
                    }
                    if (rel.StartsWith("__file/"))
                    {
                        if (w._files.TryGetValue(rel[7..], out var fp) && File.Exists(fp))
                            e.Response = env.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(fp)), 200, "OK", "Content-Type: application/octet-stream\r\nCache-Control: no-store");
                        else e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
                        return;
                    }
                    // pdf/… = PDF 修改器嗰頁嘅檔案
                    bool pdf = rel.StartsWith("pdf/");
                    if (pdf) rel = rel[4..];
                    string root = Path.GetFullPath(pdf ? PdfEditLib.Dir : OfficeLib.Dir) + Path.DirectorySeparatorChar;
                    string p = Path.GetFullPath(Path.Combine(root, rel));
                    if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                    {
                        e.Response = env.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(p)), 200, "OK", $"Content-Type: {PdfEditLib.Mime(p)}\r\nCache-Control: no-store");
                        return;
                    }
                    e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
                    return;
                }
            }
            catch { }
            e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
        };
        wv.WebMessageReceived += (_, e) =>
        {
            try
            {
                // 拖入嚟嘅檔案：頁面用 postMessageWithAdditionalObjects 交個 File 過嚟，喺呢度先攞到真實路徑（記入歷史紀錄、之後可以直接儲存）
                string? dropped = null;
                try
                {
                    foreach (var o in e.AdditionalObjects)
                        if (o is CoreWebView2File f) { dropped = f.Path; break; }
                }
                catch { }
                if (dropped != null) { w.SendFile(dropped); return; }
                w.OnMessage(e.WebMessageAsJson);
            }
            catch (Exception ex) { AppPaths.LogError("文書工具訊息", ex); }
        };
        wv.ProcessFailed += (_, _) => { try { host.BeginInvoke(() => wv.Navigate(origin + (cad ? "cad.html#new-solo" : "index.html"))); } catch { } };
        // 一開始就係開 PDF：直接入 PDF 嗰頁，唔使經首頁轉
        if (cad) wv.Navigate(OfficeLib.PageUrl("cad") + (openPath != null ? "#solo" : "#new-solo"));
        else wv.Navigate(OfficeLib.PageUrl(openPath != null ? OfficeLib.PageOf(openPath) : "home"));
        return w;
    }

    private void Post(JsonObject o) { try { _ctl.CoreWebView2.PostWebMessageAsJson(o.ToJsonString()); } catch { } }

    private void OnMessage(string json)
    {
        var m = JsonNode.Parse(json) as JsonObject;
        if (m == null) return;
        switch ((string?)m["t"])
        {
            case "ready":
                _page = (string?)m["page"] ?? "home";
                _curPath = null;
                if (_pending is { } pp) { _pending = null; SendFile(pp); }
                break;
            case "switch-cancel":   // 用家喺「未儲存」提示揀咗取消：唔轉頁
                _pending = null;
                break;
            case "opened":
                // 頁面真係開咗（用家可能喺「未儲存」提示揀咗取消）：有 token = 主程式交出去嘅檔，冇 = 新文件 / 冇路徑
                if (_page != "pdf")
                {
                    _curPath = (string?)m["token"] is { } tk && _files.TryGetValue(tk, out var op) ? op : null;
                    if (_curPath != null && !_cad) { OfficeRecent.Add(_curPath); PostRecent(); }
                }
                Opened?.Invoke((string?)m["name"] ?? "");
                break;
            case "closed":
                _curPath = null;
                Opened?.Invoke("");
                PostRecent();
                break;
            case "pick":
            {
                // PDF 頁：mode = open（開另一個檔）/ add（加入另一個 PDF 嘅頁面）；首頁：kind = any / docx / pdf
                bool add = (string?)m["mode"] == "add";
                using var od = new OpenFileDialog { Filter = OfficeLib.Filter(add ? "pdf" : (string?)m["kind"] ?? "any"), InitialDirectory = Directory.Exists(_lastDir) ? _lastDir : "" };
                if (od.ShowDialog(_host.FindForm()) == DialogResult.OK) SendFile(od.FileName, add ? "add" : "open");
                break;
            }
            case "recent-open":
                if ((string?)m["path"] is { } rp && OfficeRecent.Contains(rp)) SendFile(rp);
                break;
            case "recent-remove":
                if ((string?)m["path"] is { } rr) { OfficeRecent.Remove(rr); PostRecent(); }
                break;
            case "recent-clear":
                OfficeRecent.Clear(); PostRecent();
                break;
            case "ai-attach":   // 📎：揀圖片 / 檔案俾 AI 睇（今次發問用完就刪）
            {
                using var od = new OpenFileDialog { Filter = Ai.AiFiles.Filter, Multiselect = true, InitialDirectory = Directory.Exists(_lastDir) ? _lastDir : "" };
                if (od.ShowDialog(_host.FindForm()) == DialogResult.OK) AddAiFiles(od.FileNames);
                break;
            }
            case "ai-attach-data":   // 喺輸入格貼上嘅圖片
                if ((string?)m["d"] is { } ad)
                {
                    string tmp = Path.Combine(Path.GetTempPath(), Program.FileName + "-paste-" + Guid.NewGuid().ToString("N")[..6] + ".png");
                    try { File.WriteAllBytes(tmp, Convert.FromBase64String(ad)); AddAiFiles(new[] { tmp }); }
                    finally { try { File.Delete(tmp); } catch { } }
                }
                break;
            case "ai-detach":
                if ((string?)m["name"] is { } dn) { _aiFiles.RemoveAll(f => Path.GetFileName(f) == dn || Path.GetFileName(f).StartsWith(Path.GetFileNameWithoutExtension(dn) + "（")); PostAiFiles(); }
                break;
            case "rename":
                Rename((string?)m["name"] ?? "", (string?)m["old"] ?? "");
                break;
            case "new-doc":   // 首頁撳「新增」試算表 / 簡報：轉去嗰頁開新檔（網址尾 #new）
            {
                string pg = (string?)m["kind"] == "pptx" ? "slides" : "sheet";
                Post(new JsonObject { ["t"] = "switch", ["url"] = OfficeLib.PageUrl(pg) + "#new" });
                break;
            }
            case "new-window":
                OfficeWindow.Open(null);
                break;
            case "default-apps":
                DocShell.OpenDefaultApps();
                break;
            case "save-begin":
                _save = new MemoryStream();
                _saveName = (string?)m["name"] ?? "document.docx";
                _saveAs = (bool?)m["as"] ?? false;
                _saveExport = (bool?)m["export"] ?? false;
                break;
            case "save-chunk":
                if (_save != null && (string?)m["d"] is { } d) { var b = Convert.FromBase64String(d); _save.Write(b, 0, b.Length); }
                break;
            case "save-end":
                SaveToFile();
                break;
            case "ai":
                if (_page == "pdf") _ = AskPdfAsync((string?)m["q"] ?? "", (string?)m["text"] ?? "", (int?)m["pages"] ?? 0);
                else if (_page == "sheet") _ = AskOtherAsync(SheetPrompt, (string?)m["q"] ?? "", (string?)m["text"] ?? "", (string?)m["info"] ?? "");
                else if (_page == "cad") _ = AskOtherAsync(CadPrompt, (string?)m["q"] ?? "", (string?)m["text"] ?? "", (string?)m["info"] ?? "");
                else if (_page == "slides") _ = AskOtherAsync(SlidesPrompt, (string?)m["q"] ?? "", (string?)m["text"] ?? "", (string?)m["info"] ?? "");
                else _ = AskAsync((string?)m["q"] ?? "", (string?)m["text"] ?? "", (int?)m["paras"] ?? 0);
                break;
        }
    }

    /// <summary>
    /// 喺編輯器撳檔名改名：已經儲存過嘅檔案連磁碟上面個檔一齊改名（同名已存在就唔改），未儲存過就只係記住個名（儲存時用）。
    /// 回覆 renamed{ok, name}：唔成功就叫頁面顯示返舊名。
    /// </summary>
    private void Rename(string name, string old)
    {
        void Fail(string err) => Post(new JsonObject { ["t"] = "renamed", ["ok"] = false, ["name"] = _curPath != null ? Path.GetFileName(_curPath) : old, ["err"] = err });
        try
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
            name = name.Trim().TrimEnd('.');
            string ext = "." + OfficeLib.ExtOf(_page);
            if (name.Length <= ext.Length || !name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { Fail(""); return; }
            if (_curPath != null)
            {
                string target = Path.Combine(Path.GetDirectoryName(_curPath) ?? "", name);
                if (!target.Equals(_curPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(target)) { Fail(Lang.F("已經有一個叫「{0}」嘅檔案，改唔到名。", name)); return; }
                    if (!File.Exists(_curPath)) { Fail(Lang.F("搵唔到檔案：{0}", _curPath)); return; }
                    File.Move(_curPath, target);
                    OfficeRecent.Remove(_curPath); OfficeRecent.Add(target);
                    _curPath = target;
                }
            }
            Post(new JsonObject { ["t"] = "renamed", ["ok"] = true, ["name"] = name });
            Opened?.Invoke(name);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("改檔名", ex);
            Fail(Lang.T("改名失敗：") + ex.Message);
        }
    }

    private static string _lastDir = "";

    private void PostRecent()
    {
        var o = new JsonObject { ["t"] = "recent" };
        o["list"] = JsonNode.Parse(JsonSerializer.Serialize(OfficeRecent.ForJs()));
        Post(o);
    }

    /// <summary>
    /// 將一個檔案交俾頁面開。唔同種類嘅檔案喺唔同頁：頁面唔啱就叫而家嗰頁轉過去（佢會先問未儲存嘅改動），新頁準備好（ready）再交檔案。
    /// 文件：頁面真係開咗（回覆 opened）先記入歷史紀錄；PDF：交出去就記。mode = add：PDF 頁加入另一個 PDF 嘅頁面。
    /// </summary>
    public void SendFile(string path, string mode = "open")
    {
        try
        {
            if (!File.Exists(path))
            {
                OfficeRecent.Remove(path); PostRecent();
                Post(new JsonObject { ["t"] = "toast", ["text"] = Lang.F("搵唔到檔案：{0}", path) });
                return;
            }
            // 繪圖工具只開 .dxf；文書工具唔開 .dxf
            if (_cad ? !path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase) : !OfficeLib.Supported(path))
            {
                Post(new JsonObject { ["t"] = "toast", ["text"] = Lang.T("呢種檔案暫時未支援。") });
                return;
            }
            bool pdf = OfficeLib.IsPdf(path);
            string origin = "https://" + OfficeLib.Host + "/", page = OfficeLib.PageOf(path);
            if (page != _page)
            {
                if (mode == "add") return;
                _pending = path;
                Post(new JsonObject { ["t"] = "switch", ["url"] = OfficeLib.PageUrl(page) });
                return;
            }
            string token = (++_fileSeq).ToString();
            _files[token] = path;
            _lastDir = Path.GetDirectoryName(path) ?? "";
            if (pdf && mode == "open") OfficeRecent.Add(path);
            Post(new JsonObject { ["t"] = "file", ["mode"] = mode, ["token"] = token, ["url"] = origin + "__file/" + token, ["name"] = Path.GetFileName(path) });
        }
        catch (Exception ex) { AppPaths.LogError("開文件", ex); }
    }

    private void SaveToFile()
    {
        var data = _save;
        _save = null;
        if (data == null) return;
        try
        {
            if (_saveExport) { SaveExport(data); return; }
            if (_page == "pdf") { SavePdf(data); return; }
            string ext = OfficeLib.ExtOf(_page);
            string? target = !_saveAs && _curPath != null ? _curPath : null;
            if (target == null)
            {
                foreach (char c in Path.GetInvalidFileNameChars()) _saveName = _saveName.Replace(c, '_');
                using var d = new SaveFileDialog
                {
                    Filter = OfficeLib.Filter(ext), FileName = _saveName, DefaultExt = ext, AddExtension = true, OverwritePrompt = true,
                    InitialDirectory = Directory.Exists(_lastDir) ? _lastDir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };
                if (d.ShowDialog(_host.FindForm()) != DialogResult.OK) { Post(new JsonObject { ["t"] = "saved", ["ok"] = false }); return; }
                target = d.FileName;
            }
            Backup(target);
            // 先寫去暫存檔再換：寫到一半出事都唔會整壞原檔
            string tmp = target + ".rabi-tmp";
            File.WriteAllBytes(tmp, data.ToArray());
            File.Move(tmp, target, true);
            _curPath = target;
            _lastDir = Path.GetDirectoryName(target) ?? "";
            if (!_cad) { OfficeRecent.Add(target); PostRecent(); }
            Post(new JsonObject { ["t"] = "saved", ["ok"] = true, ["name"] = Path.GetFileName(target) });
            Opened?.Invoke(Path.GetFileName(target));
        }
        catch (Exception ex)
        {
            AppPaths.LogError("儲存文件", ex);
            Post(new JsonObject { ["t"] = "saved", ["ok"] = false, ["err"] = ex.Message });
        }
    }

    /// <summary>匯出另一種格式（試算表 → CSV、簡報 → PDF）：揀位置寫檔，唔當做而家份文件嘅儲存。</summary>
    private void SaveExport(MemoryStream data)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) _saveName = _saveName.Replace(c, '_');
        string ext = Path.GetExtension(_saveName).TrimStart('.').ToLowerInvariant();
        if (ext != "csv" && ext != "pdf" && !(_cad && (ext == "svg" || ext == "png"))) { Post(new JsonObject { ["t"] = "saved", ["ok"] = false }); return; }
        using var d = new SaveFileDialog
        {
            Filter = OfficeLib.Filter(ext), FileName = _saveName, DefaultExt = ext, AddExtension = true, OverwritePrompt = true,
            InitialDirectory = Directory.Exists(_lastDir) ? _lastDir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (d.ShowDialog(_host.FindForm()) != DialogResult.OK) { Post(new JsonObject { ["t"] = "saved", ["ok"] = false }); return; }
        File.WriteAllBytes(d.FileName, data.ToArray());
        Post(new JsonObject { ["t"] = "saved", ["ok"] = true, ["export"] = true, ["name"] = Path.GetFileName(d.FileName) });
    }

    /// <summary>PDF：一定另存新檔（預設「原名-edited.pdf」），唔會改原檔。</summary>
    private void SavePdf(MemoryStream data)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) _saveName = _saveName.Replace(c, '_');
        using var d = new SaveFileDialog
        {
            Filter = OfficeLib.Filter("pdf"), FileName = _saveName, DefaultExt = "pdf", AddExtension = true, OverwritePrompt = true,
            InitialDirectory = Directory.Exists(_lastDir) ? _lastDir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (d.ShowDialog(_host.FindForm()) != DialogResult.OK) { Post(new JsonObject { ["t"] = "saved", ["ok"] = false }); return; }
        File.WriteAllBytes(d.FileName, data.ToArray());
        _lastDir = Path.GetDirectoryName(d.FileName) ?? "";
        OfficeRecent.Add(d.FileName);
        Post(new JsonObject { ["t"] = "saved", ["ok"] = true, ["name"] = Path.GetFileName(d.FileName) });
    }

    /// <summary>
    /// 第一次覆寫一個已有嘅檔案之前，先抄一份去 data\office\backup（只留最新 20 個）。
    /// 編輯器未必保留到 Word 檔入面每一樣嘢，有備份先可以救返。
    /// </summary>
    private void Backup(string target)
    {
        try
        {
            if (!File.Exists(target) || !_backed.Add(target)) return;
            string dir = Path.Combine(AppPaths.Data, "office", "backup");
            Directory.CreateDirectory(dir);
            File.Copy(target, Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + " " + Path.GetFileName(target)), true);
            var old = new DirectoryInfo(dir).GetFiles();
            Array.Sort(old, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = 20; i < old.Length; i++) try { old[i].Delete(); } catch { }
        }
        catch (Exception ex) { AppPaths.LogError("文件備份", ex); }
    }

    private async Task AskAsync(string q, string text, int paras)
    {
        try
        {
            string prompt =
                "你係文書工具入面嘅助手。用戶開住一份 Word 文件（.docx），下面係文件嘅文字，每段開頭嘅 [數字] 係段落編號（表格入面每格都係一段），{…} 係段落樣式。\n" +
                Mail.MailAi.ReplyRule + "\n" +
                "如果用戶只係發問（摘要、解釋、搵資料、翻譯畀佢睇），直接用文字答，唔好輸出 JSON。\n" +
                "如果用戶叫你修改文件，先用一句講你做咗乜，然後喺最尾加一個 ```json 區塊，入面係動作陣列，只可以用以下動作：\n" +
                "  {\"op\":\"replace\",\"p\":3,\"find\":\"原有文字\",\"with\":\"新文字\"}   // 將第 3 段入面嘅「原有文字」換做「新文字」；find 要同文件一模一樣；唔寫 find = 成段換；with 係空字串 = 刪除嗰啲字\n" +
                "  {\"op\":\"insert\",\"after\":3,\"text\":\"新段落\"}                  // 喺第 3 段後面加新段落（after 0 = 文件最頂）；text 入面嘅換行 = 分做幾段；可以加 \"style\":\"Heading1\"\n" +
                "  {\"op\":\"delete\",\"p\":5}                                        // 刪除成段\n" +
                "  {\"op\":\"style\",\"p\":1,\"style\":\"Heading1\"}                    // 段落樣式：Title、Heading1、Heading2、Heading3、Normal\n" +
                "  {\"op\":\"format\",\"p\":2,\"find\":\"某幾個字\",\"bold\":true,\"italic\":false,\"underline\":true,\"color\":\"#FF0000\",\"size\":14}   // 文字格式；唔寫 find = 成段；size 係點數；唔使改嘅屬性唔好寫\n" +
                "段落編號一律以修改之前（下面列出）嘅編號為準。你加唔到圖片同表格，亦改唔到頁面設定；用戶要求呢啲就老實講做唔到，叫佢用上面嘅「插入」同「檔案」選單。\n\n" +
                "【文件內容（共 " + paras + " 段）】\n" + Mail.MailAi.Clip(text, 45000) + "\n\n【用戶】\n" + q;
            var (ans, note) = await AskWithFiles(prompt);
            Post(new JsonObject { ["t"] = "ai-reply", ["text"] = ans, ["note"] = note });
        }
        catch (Exception ex)
        {
            Post(new JsonObject { ["t"] = "ai-reply", ["err"] = ex.Message });
        }
    }

    // ---------- AI 附件：一次性（問完就刪），對話本身亦唔留紀錄（MailAi.AskAsync 用 quick 模式）----------
    private readonly List<string> _aiFiles = new();

    private void AddAiFiles(IEnumerable<string> paths)
    {
        try
        {
            _aiFiles.AddRange(Ai.AiFiles.Prepare(paths));
            // 附咗圖片但本機 AI 睇唔到：講明，帶用家去下載圖片識別（講過唔要就唔再問，改用文字辨識）
            if (_aiFiles.Any(Ai.AiFiles.IsImage) && Ai.AiProvider.LoadCurrent()?.Key == "local")
                Ai.LocalAi.OfferVision(_host.FindForm(), once: true);
            PostAiFiles();
        }
        catch (Exception ex) { AppPaths.LogError("AI 附件", ex); }
    }

    /// <summary>話俾頁面知而家附咗乜（抽出嚟嘅「（文字）.txt」唔顯示）；本機 AI 睇唔到圖就提一提。</summary>
    private void PostAiFiles()
    {
        var arr = new JsonArray();
        bool img = false;
        foreach (var f in _aiFiles)
        {
            if (f.EndsWith("（文字）.txt")) continue;
            bool i = Ai.AiFiles.IsImage(f); img |= i;
            arr.Add(new JsonObject { ["name"] = Path.GetFileName(f), ["image"] = i });
        }
        string hint = "";
        try { if (img && Ai.AiProvider.LoadCurrent()?.Key == "local" && Ai.LocalAi.Images is Ai.LocalAi.ImageSupport.NeedAddon or Ai.LocalAi.ImageSupport.NeedHelper) hint = Lang.T("而家用緊嘅本機 AI 睇唔到圖片：會改用文字辨識讀出圖入面嘅字再交俾 AI；想 AI 真係睇圖，請去「AI 助手」頁改用 Claude / ChatGPT / Gemini。"); } catch { }
        Post(new JsonObject { ["t"] = "ai-files", ["files"] = arr, ["hint"] = hint });
    }

    /// <summary>
    /// 連附件一齊問 AI。睇唔到圖嘅 AI（本機 AI）：圖片改用 Windows 文字辨識讀出入面嘅字貼入問題，
    /// 讀唔到字就老實話俾用家知。問完附件即刻刪（一次性）。回傳（回答，要顯示俾用家睇嘅提示）。
    /// </summary>
    private async Task<(string Ans, string Note)> AskWithFiles(string prompt)
    {
        var files = _aiFiles.ToList();
        string note = "";
        try
        {
            if (files.Count > 0 && Ai.AiProvider.LoadCurrent()?.Key == "local" && Ai.LocalAi.Images is Ai.LocalAi.ImageSupport.NeedAddon or Ai.LocalAi.ImageSupport.NeedHelper)
            {
                var pics = files.Where(Ai.AiFiles.IsImage).ToList();
                if (pics.Count > 0)
                {
                    var sb = new StringBuilder();
                    int ok = 0;
                    try
                    {
                        var langs = await Ocr.LanguagesAsync();
                        string code = Lang.Code(Lang.Current), two = code.Split('-')[0];
                        string lang = langs.Select(l => l.Tag).FirstOrDefault(t => t.StartsWith(code, StringComparison.OrdinalIgnoreCase))
                            ?? langs.Select(l => l.Tag).FirstOrDefault(t => t.StartsWith(two, StringComparison.OrdinalIgnoreCase))
                            ?? langs.Select(l => l.Tag).FirstOrDefault() ?? "";
                        foreach (var pic in pics)
                        {
                            string text = lang.Length > 0 ? await Ocr.RecognizeAsync(pic, lang) : "";
                            if (text.Length == 0) continue;
                            ok++;
                            sb.AppendLine().AppendLine("【圖片「" + Path.GetFileName(pic) + "」入面用文字辨識讀到嘅字（你睇唔到張圖本身，只有呢啲字；可能有錯字）】").AppendLine(Mail.MailAi.Clip(text, 8000));
                        }
                    }
                    catch (Exception ex) { AppPaths.LogError("AI 附件文字辨識", ex); }
                    prompt += sb.ToString();
                    note = ok == pics.Count ? Lang.T("本機 AI 睇唔到圖片，已經改用文字辨識讀出圖入面嘅字。圖入面冇字嘅內容（相、圖表）AI 係唔知嘅。")
                         : ok > 0 ? Lang.T("本機 AI 睇唔到圖片：有啲圖讀到入面嘅字，有啲讀唔到。想 AI 真係睇圖，請去「AI 助手」頁改用 Claude / ChatGPT / Gemini。")
                         : Lang.T("本機 AI 睇唔到圖片，圖入面亦讀唔到字。想 AI 睇圖，請去「AI 助手」頁改用 Claude / ChatGPT / Gemini。");
                    if (ok == pics.Count) files = files.Where(f => !Ai.AiFiles.IsImage(f)).ToList();   // 讀晒：唔使再叫 AI 講「睇唔到附件」
                }
            }
            return (await Mail.MailAi.AskAsync(prompt, files), note);
        }
        finally
        {
            // 一次性：問完就刪走附件
            foreach (var dir in _aiFiles.Select(Path.GetDirectoryName).Where(d => d != null).Distinct().ToList())
                try { if (Directory.Exists(dir)) Directory.Delete(dir!, true); } catch { }
            _aiFiles.Clear();
            PostAiFiles();
        }
    }

    private const string SheetPrompt =
        "你係文書工具入面嘅試算表助手。用戶開住一份試算表（.xlsx），下面係而家工作表有資料嘅儲存格（格式「A1: 值」；有公式就係「A1: =公式 → 計出嚟嘅值」）。\n" +
        "如果用戶只係發問（摘要、解釋、分析、點樣寫公式），直接用文字答，唔好輸出 JSON。\n" +
        "如果用戶叫你修改試算表，先用一句講你做咗乜，然後喺最尾加一個 ```json 區塊，入面係動作陣列，只可以用以下動作：\n" +
        "  {\"op\":\"set\",\"cell\":\"B2\",\"value\":123}                       // 設定一格；value 可以係數字、文字，或者「=」開頭嘅公式（例如 \"=SUM(B2:B9)\"）\n" +
        "  {\"op\":\"fill\",\"range\":\"A1\",\"values\":[[\"名\",\"數量\"],[\"A\",1]]}   // 由 range 左上角開始填一個二維陣列\n" +
        "  {\"op\":\"format\",\"range\":\"A1:D1\",\"bold\":true,\"italic\":false,\"color\":\"#FFFFFF\",\"bg\":\"#2F6FED\",\"align\":\"center\",\"size\":12,\"numfmt\":\"#,##0.00\",\"wrap\":true}   // 格式；唔使改嘅屬性唔好寫；align = left / center / right\n" +
        "  {\"op\":\"clear\",\"range\":\"A5:C9\"}                                  // 清除內容\n" +
        "  {\"op\":\"insertRows\",\"before\":3,\"count\":1}  {\"op\":\"insertCols\",\"before\":\"C\",\"count\":1}   // 喺第 3 列 / C 欄之前插入\n" +
        "  {\"op\":\"deleteRows\",\"row\":5,\"count\":1}     {\"op\":\"deleteCols\",\"col\":\"D\",\"count\":1}\n" +
        "  {\"op\":\"addSheet\",\"name\":\"新工作表\"}\n" +
        "  {\"op\":\"chart\",\"type\":\"column\",\"range\":\"A1:C6\",\"title\":\"圖表標題\"}   // 加圖表；type = column（直條圖）/ bar（橫條圖）/ line（折線圖）/ area（區域圖）/ pie（圓形圖）；range 第一列係系列名、第一欄係分類，其餘係數字；圓形圖只用第一個數字欄\n" +
        "  {\"op\":\"deleteChart\",\"chart\":1}                                  // 刪除圖表（編號見下面「# charts」）\n" +
        "動作會按你寫嘅次序做，所以插入 / 刪除列欄之後嘅儲存格位置要用新位置。用戶叫你整圖表而資料未喺試算表入面，就先用 fill 填好資料再加 chart。你加唔到圖片，亦整唔到其他種類嘅圖表（散佈圖、組合圖、堆疊圖）；用戶要求呢啲就老實講做唔到。\n\n";
    private const string CadPrompt =
        "你係繪圖工具（2D 製圖）入面嘅助手。用戶開住一張圖面（.dxf），下面係圖面入面嘅物件（格式「#編號 種類 座標 [圖層]」）。座標單位係毫米，X 向右、Y 向上，角度係度數、逆時針為正。\n" +
        "如果用戶只係發問（圖面有乜、尺寸、建議），直接用文字答，唔好輸出 JSON。\n" +
        "如果用戶叫你畫嘢或者修改圖面，先用一句講你做咗乜，然後喺最尾加一個 ```json 區塊，入面係動作陣列，只可以用以下動作：\n" +
        "  {\"op\":\"line\",\"x1\":0,\"y1\":0,\"x2\":100,\"y2\":0}\n" +
        "  {\"op\":\"rect\",\"x\":0,\"y\":0,\"w\":100,\"h\":50}                       // x、y = 左下角\n" +
        "  {\"op\":\"circle\",\"cx\":50,\"cy\":25,\"r\":10}\n" +
        "  {\"op\":\"arc\",\"cx\":0,\"cy\":0,\"r\":20,\"start\":0,\"end\":90}             // 由 start 逆時針去 end\n" +
        "  {\"op\":\"polyline\",\"points\":[[0,0],[10,0],[10,10]],\"closed\":false}\n" +
        "  {\"op\":\"text\",\"x\":10,\"y\":10,\"text\":\"客廳\",\"height\":5,\"rotation\":0}\n" +
        "  {\"op\":\"dim\",\"x1\":0,\"y1\":0,\"x2\":100,\"y2\":0,\"offset\":-15}           // 尺寸標註；offset = 標註線離開兩點連線嘅距離（正 = 連線方向嘅左邊）\n" +
        "  {\"op\":\"layer\",\"name\":\"牆\",\"color\":1}                              // 加圖層；color 係索引色 1-255（1 紅 2 黃 3 綠 4 青 5 藍 6 洋紅 7 白/黑）\n" +
        "  {\"op\":\"move\",\"ids\":[3,4],\"dx\":10,\"dy\":0}    {\"op\":\"erase\",\"ids\":[5]}\n" +
        "每個畫圖動作都可以加 \"layer\":\"圖層名\"（要係已有或者前面用 layer 動作加咗嘅圖層）。座標要自己計好，確保圖形接得啱。你做唔到修剪、圓角、填充圖案同 3D；用戶要求呢啲就老實講做唔到。\n\n";
    private const string SlidesPrompt =
        "你係文書工具入面嘅簡報助手。用戶開住一份簡報（.pptx），下面係每張投影片嘅物件（格式「[物件編號] 種類: 文字」；title = 標題，body = 內文，sp = 圖形 / 文字方塊，pic = 圖片）。\n" +
        "如果用戶只係發問（摘要、建議、講稿），直接用文字答，唔好輸出 JSON。\n" +
        "如果用戶叫你修改簡報，先用一句講你做咗乜，然後喺最尾加一個 ```json 區塊，入面係動作陣列，只可以用以下動作：\n" +
        "  {\"op\":\"text\",\"slide\":1,\"shape\":\"2\",\"text\":\"新文字\"}                  // 改第 1 張投影片入面編號 2 嗰個物件嘅文字（換行 = 分段）\n" +
        "  {\"op\":\"addText\",\"slide\":1,\"text\":\"…\",\"x\":10,\"y\":70,\"w\":80,\"h\":15,\"size\":20,\"bold\":false,\"color\":\"#000000\"}   // 加文字方塊；x、y、w、h 係投影片百分比（0-100），size 係點數\n" +
        "  {\"op\":\"deleteShape\",\"slide\":1,\"shape\":\"5\"}\n" +
        "  {\"op\":\"addSlide\",\"after\":2,\"title\":\"標題\",\"body\":\"第一點\\n第二點\"}       // 喺第 2 張後面加一張新投影片（after 0 = 最頂）\n" +
        "  {\"op\":\"deleteSlide\",\"slide\":3}    {\"op\":\"moveSlide\",\"from\":4,\"to\":2}\n" +
        "  {\"op\":\"background\",\"slide\":1,\"color\":\"#FFF2CC\"}                     // 投影片背景顏色\n" +
        "投影片編號一律以修改之前（下面列出）嘅編號為準。你加唔到圖片、圖表同動畫；用戶要求呢啲就老實講做唔到。\n\n";

    private async Task AskOtherAsync(string intro, string q, string text, string info)
    {
        try
        {
            string prompt = intro + Mail.MailAi.ReplyRule + "\n\n【" + info + "】\n" + Mail.MailAi.Clip(text, 45000) + "\n\n【用戶】\n" + q;
            var (ans, note) = await AskWithFiles(prompt);
            Post(new JsonObject { ["t"] = "ai-reply", ["text"] = ans, ["note"] = note });
        }
        catch (Exception ex)
        {
            Post(new JsonObject { ["t"] = "ai-reply", ["err"] = ex.Message });
        }
    }

    private async Task AskPdfAsync(string q, string text, int pages)
    {
        try
        {
            string prompt =
                "你係 PDF 修改器入面嘅助手。用戶開住一份 PDF（共 " + pages + " 頁），下面係抽出嚟嘅文字（掃描檔可能冇文字）。\n" +
                Mail.MailAi.ReplyRule + "\n" +
                "如果用戶只係發問（摘要、解釋、搵資料、翻譯），直接用文字答，唔好輸出 JSON。\n" +
                "如果用戶叫你修改份 PDF，先用一句講你做咗乜，然後喺最尾加一個 ```json 區塊，入面係動作陣列，只可以用以下動作：\n" +
                "  {\"op\":\"rotate\",\"pages\":[1,2],\"deg\":90}          // 順時針旋轉 90 / 180 / 270\n" +
                "  {\"op\":\"delete\",\"pages\":[3]}                        // 刪除頁（唔可以刪晒）\n" +
                "  {\"op\":\"move\",\"from\":5,\"to\":1}                     // 將第 5 頁搬去第 1 頁位置\n" +
                "  {\"op\":\"text\",\"page\":1,\"x\":10,\"y\":90,\"text\":\"…\",\"size\":14,\"color\":\"#000000\"}   // 喺頁面加文字；x、y 係由左上角起計嘅百分比（0-100）\n" +
                "  {\"op\":\"field\",\"page\":1,\"kind\":\"text\",\"x\":30,\"y\":40,\"w\":35,\"h\":3}   // 加可以填寫嘅表格欄；kind = text（輸入欄）/ multi（多行）/ check（剔選方格）/ print（列印按鈕）/ reset（重設按鈕）；x、y、w、h 都係頁面百分比\n" +
                "頁碼由 1 開始，一律以修改之前嘅頁碼為準。你改唔到 PDF 原有嘅文字同圖片，亦加唔到簽名；用戶要求呢啲就老實講做唔到，叫佢用工具列（塗白再加文字、簽名掣）。\n\n" +
                "【PDF 文字】\n" + Mail.MailAi.Clip(text, 45000) + "\n\n【用戶】\n" + q;
            var (ans, note) = await AskWithFiles(prompt);
            Post(new JsonObject { ["t"] = "ai-reply", ["text"] = ans, ["note"] = note });
        }
        catch (Exception ex)
        {
            Post(new JsonObject { ["t"] = "ai-reply", ["err"] = ex.Message });
        }
    }

    public void Close() { try { _ctl.Close(); } catch { } }
}

/// <summary>「文書工具」頁。</summary>
internal sealed class OfficePage : Panel
{
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private readonly Label _msg = new() { Dock = DockStyle.Fill, ForeColor = Ui.TextDim, Font = Ui.F(10.5f), TextAlign = ContentAlignment.MiddleCenter, BackColor = Ui.Bg };
    private object? _web;   // OfficeWeb
    private bool _started;
    private string? _open;
    /// <summary>開咗檔案（檔名）。</summary>
    public event Action<string>? Opened;

    /// <summary>主視窗嗰頁：開住文件時側欄自動摺起（俾多啲位編輯），返首頁就還原。</summary>
    public static Control Build()
    {
        var p = new OfficePage(null);
        p.Opened += n => { try { MainForm.Instance?.SetDocEditing(n.Length > 0); } catch { } };
        return p;
    }

    private readonly bool _cad;
    /// <summary>繪圖工具嗰頁（同一套瀏覽器外殼，只係入面開繪圖頁）。</summary>
    public static Control BuildCad() => new OfficePage(null, true);

    public OfficePage(string? openPath, bool cad = false)
    {
        _open = openPath;
        _cad = cad;
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        Controls.Add(_host);
        _host.Controls.Add(_msg);
        // 第一次顯示先開瀏覽器（唔好拖慢開程式）
        VisibleChanged += (_, _) => { if (Visible && !_started) { _started = true; _ = StartAsync(); } };
        Disposed += (_, _) => { if (_web != null) CloseWeb(); };
    }

    private async Task StartAsync()
    {
        try
        {
            if (!Mail.WebView2Lib.Prepare()) { _msg.Text = Lang.T("要用 Microsoft Edge WebView2 Runtime 先用到（Windows 10 / 11 通常已經有）。"); return; }
            if (!OfficeLib.Prepare()) { _msg.Text = Lang.T("載入失敗：") + "office"; return; }
            if (!_cad && !PdfEditLib.Prepare()) { _msg.Text = Lang.T("載入失敗：") + "pdfedit"; return; }
            _web = await CreateWeb();
            _msg.Visible = false;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("開文書工具", ex);
            _msg.Text = Lang.T("載入失敗：") + ex.Message;
            _started = false;
        }
    }

    // WebView2 類型只喺呢幾個方法出現（唔好 inline，等 DLL 準備好先載入）
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<object> CreateWeb()
    {
        var w = await OfficeWeb.CreateAsync(_host, _open, _cad);
        _open = null;
        w.Opened = n => Opened?.Invoke(n);
        return w;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private void CloseWeb() => ((OfficeWeb)_web!).Close();
}

/// <summary>文書工具歷史紀錄（data\office\recent.json，最多 15 個；只記路徑，唔會複製檔案）。</summary>
internal static class OfficeRecent
{
    private static string FilePath => Path.Combine(AppPaths.Data, "office", "recent.json");
    private static List<string>? _list;
    private static List<string> List
    {
        get
        {
            if (_list != null) return _list;
            try { _list = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) : null; } catch { }
            if (_list == null)
            {
                // 第一次：接收返 PDF 修改器（已併入文書工具）嘅歷史紀錄
                try { string old = Path.Combine(AppPaths.Data, "pdf", "recent.json"); if (File.Exists(old)) _list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(old)); } catch { }
            }
            return _list ??= new();
        }
    }
    private static void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(List)); }
        catch (Exception ex) { AppPaths.LogError("文書工具歷史紀錄", ex); }
    }
    public static bool Contains(string path) => List.Exists(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
    public static void Add(string path)
    {
        List.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        List.Insert(0, path);
        if (List.Count > 15) List.RemoveRange(15, List.Count - 15);
        Save();
    }
    public static void Remove(string path) { if (List.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)) > 0) Save(); }
    public static void Clear() { List.Clear(); Save(); }
    public static List<Dictionary<string, string>> ForJs()
    {
        var r = new List<Dictionary<string, string>>();
        foreach (var p in List) r.Add(new() { ["path"] = p, ["name"] = Path.GetFileName(p), ["dir"] = Path.GetDirectoryName(p) ?? "" });
        return r;
    }
}

/// <summary>文書工具獨立視窗：由檔案總管開文件，或者撳「開新視窗」。可以同時開幾個。</summary>
internal sealed class OfficeWindow : Form
{
    public static void Open(string? path)
    {
        var f = new OfficeWindow(path);
        DocShell.WindowOpened(f);
        f.Show();
        f.Activate();
    }

    private OfficeWindow(string? path)
    {
        Text = Lang.T("文書工具") + " - " + Program.AppName;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? Icon; } catch { }
        BackColor = Ui.Bg;
        StartPosition = FormStartPosition.CenterScreen;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(Ui.S(1240), wa.Width - Ui.S(60)), Math.Min(Ui.S(920), wa.Height - Ui.S(60)));
        MinimumSize = new Size(Ui.S(820), Ui.S(520));
        var page = new OfficePage(path);
        page.Opened += n => { try { Text = n.Length > 0 ? n + " - " + Lang.T("文書工具") : Lang.T("文書工具") + " - " + Program.AppName; } catch { } };
        Controls.Add(page);
        HandleCreated += (_, _) => DarkTheme.DarkTitleBar(this);
        FormClosed += (_, _) => page.Dispose();
    }
}
