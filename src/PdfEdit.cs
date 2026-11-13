using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
//  PDF 修改器（文書工具入面嘅 PDF 部分；WebView2 同視窗喺 Office.cs）：加文字 / 簽名 / 圖片 / 畫筆 / 螢光筆 / 塗白，旋轉、刪除、排頁、合併
//  • 介面係 lib\pdfedit\editor.html（WebView2），用 pdf.js（Apache-2.0）顯示、pdf-lib（MIT）寫檔
//  • 成個 lib\pdfedit 資料夾壓做 pdfedit.zip 嵌入 exe，第一次用解壓去 data\lib\pdfedit-<版本>
//  • 檔案全部喺本機處理：頁面只可以讀自己嘅檔案（其他網絡請求一律擋）
//  • 有連接 AI 先顯示 AI 掣：問文件內容，或者叫 AI 幫手改（旋轉 / 刪頁 / 搬頁 / 加文字）
// =====================================================================
internal static class PdfEditLib
{
    public const string Host = "pdf.rabi";
    public static string Dir { get; private set; } = "";

    /// <summary>解壓嵌入嘅編輯器檔案（zip 大細變咗 = 新版本，重新解壓）。</summary>
    public static bool Prepare()
    {
        try
        {
            using var s = typeof(PdfEditLib).Assembly.GetManifestResourceStream("pdfedit.zip") ?? throw new FileNotFoundException("pdfedit.zip");
            string dir = Path.Combine(AppPaths.Data, "lib", "pdfedit");
            string mark = Path.Combine(dir, ".size");
            string want = s.Length.ToString();
            if (!File.Exists(mark) || File.ReadAllText(mark) != want || !File.Exists(Path.Combine(dir, "editor.html")))
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
            AppPaths.LogError("PDF 修改器準備", ex);
            return false;
        }
    }

    /// <summary>頁面用嘅介面文字同設定（strings.js）。</summary>
    public static string StringsJs(bool ai)
    {
        var l = new Dictionary<string, string>
        {
            ["open"] = Lang.T("開啟 PDF"), ["save"] = Lang.T("儲存"), ["select"] = Lang.T("選取"), ["text"] = Lang.T("文字"),
            ["sign"] = Lang.T("簽名"), ["image"] = Lang.T("圖片"), ["draw"] = Lang.T("畫筆"), ["highlight"] = Lang.T("螢光筆"),
            ["whiteout"] = Lang.T("塗白"), ["undo"] = Lang.T("復原"), ["redo"] = Lang.T("重做"), ["addpdf"] = Lang.T("加入 PDF"),
            ["blank"] = Lang.T("空白頁"), ["color"] = Lang.T("顏色"), ["size"] = Lang.T("大小"),
            ["drop"] = Lang.T("或者將 PDF 檔拖入呢個視窗"),
            ["dropsub"] = Lang.T("可以加文字、簽名、圖片、畫筆同螢光筆，亦可以旋轉、刪除、重新排頁同合併 PDF。檔案只會喺你部電腦處理，唔會上載。"),
            ["rotate"] = Lang.T("旋轉"), ["up"] = Lang.T("上移"), ["down"] = Lang.T("下移"), ["del"] = Lang.T("刪除"),
            ["page"] = Lang.T("第 {0} 頁，共 {1} 頁"),
            ["sigtitle"] = Lang.T("簽名"), ["sigdraw"] = Lang.T("喺下面白色位置畫出你嘅簽名"), ["clear"] = Lang.T("清除"), ["use"] = Lang.T("使用"),
            ["sigsave"] = Lang.T("記住呢個簽名"), ["sigsaved"] = Lang.T("已儲存嘅簽名"), ["sigimg"] = Lang.T("用圖片…"), ["cancel"] = Lang.T("取消"),
            ["place"] = Lang.T("撳一下頁面放置"),
            ["saving"] = Lang.T("儲存緊…"), ["saved"] = Lang.T("已儲存：{0}"), ["savefail"] = Lang.T("儲存失敗：{0}"),
            ["openfail"] = Lang.T("開唔到呢個 PDF：{0}"), ["encrypted"] = Lang.T("呢個 PDF 有保護，只可以填寫佢自帶嘅欄位再儲存；加嘢或者改頁面就儲存唔到。"),
            ["unsaved"] = Lang.T("有未儲存嘅改動，確定要開另一個檔案？"),
            ["aititle"] = Lang.T("AI 助手"), ["aiask"] = Lang.T("問 AI，或者叫佢幫你改…"), ["aisend"] = Lang.T("傳送"),
            ["aisum"] = Lang.T("摘要"), ["aikey"] = Lang.T("重點"),
            ["aisum_q"] = Lang.T("摘要呢份文件嘅內容"), ["aikey_q"] = Lang.T("列出呢份文件嘅重點"),
            ["back"] = Lang.T("← 返回"), ["title"] = Lang.T("PDF 修改器"), ["print"] = Lang.T("列印"), ["form"] = Lang.T("表單"), ["newwin"] = Lang.T("開新視窗"),
            ["f_text"] = Lang.T("輸入欄"), ["f_multi"] = Lang.T("多行輸入欄"), ["f_check"] = Lang.T("剔選方格"),
            ["f_print"] = Lang.T("列印按鈕"), ["f_reset"] = Lang.T("重設按鈕"), ["cap_print"] = Lang.T("列印"), ["cap_reset"] = Lang.T("重設"),
            ["thick"] = Lang.T("粗幼"), ["thin"] = Lang.T("幼"), ["medium"] = Lang.T("中"), ["thicker"] = Lang.T("粗"),
            ["signew"] = Lang.T("新簽名…"), ["imgpick"] = Lang.T("揀圖片…"),
            ["tip_select"] = Lang.T("撳一下加咗嘅物件可以移動、縮放或者刪除；PDF 自帶嘅輸入欄同方格可以直接填。"),
            ["tip_text"] = Lang.T("撳一下頁面開始打字；之後雙擊可以再改。"),
            ["tip_draw"] = Lang.T("喺頁面上面拖住畫。"),
            ["tip_box"] = Lang.T("喺頁面上面拖出一個方格。"),
            ["tip_form"] = Lang.T("揀一種欄位，再喺頁面拖出位置（或者撳一下）；儲存之後就係可以填寫嘅 PDF。"),
            ["recent"] = Lang.T("最近開過"), ["recentclear"] = Lang.T("清除紀錄"), ["recentdel"] = Lang.T("由紀錄移除"),
            ["setdefault"] = Lang.T("設為預設 PDF 程式…"), ["loading"] = Lang.T("載入緊…"), ["ai_attach"] = Lang.T("附加圖片或者檔案俾 AI 睇（今次發問用完就刪）"), ["ai_once"] = Lang.T("對話同附件都唔會儲存。"), ["saveas"] = Lang.T("另存新檔"),
            ["aithink"] = Lang.T("AI 諗緊…"), ["aiapplied"] = Lang.T("已套用 {0} 項改動（可以撳「復原」還原）"), ["you"] = Lang.T("你"),
        };
        string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        var cfg = new
        {
            ai,
            suite = true,   // 喺文書工具入面行：頁面冇自己嘅首頁
            recent = new List<Dictionary<string, string>>(),
            colors = new Dictionary<string, string>
            {
                ["bg"] = Hex(Ui.Bg), ["card"] = Hex(Ui.Card), ["hi"] = Hex(Ui.CardHi), ["text"] = Hex(Ui.Text), ["dim"] = Hex(Ui.TextDim),
                ["border"] = Hex(Ui.Border), ["accent"] = Hex(Ui.Accent), ["input"] = Hex(Ui.Input), ["danger"] = Hex(Ui.Danger), ["ok"] = Hex(Ui.Success)
            }
        };
        var opt = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return "window.L=" + JsonSerializer.Serialize(l, opt) + ";window.CFG=" + JsonSerializer.Serialize(cfg, opt) + ";";
    }

    /// <summary>而家有冇可以用嘅 AI（揀咗 + 裝咗）。</summary>
    public static bool AiReady()
    {
        try { var p = Ai.AiProvider.LoadCurrent(); return p != null && p.Find() != null; } catch { return false; }
    }

    public static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8", ".js" or ".mjs" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
        ".json" => "application/json", ".png" => "image/png", ".svg" => "image/svg+xml", ".ttf" => "font/ttf", ".wasm" => "application/wasm",
        _ => "application/octet-stream"
    };
}

/// <summary>
/// 文件類工具（PDF 修改器、文書工具）同 Windows 檔案總管整合（跟 EdShell 一樣只寫 HKCU，移除工具就清走）：
/// • 「開啟檔案」清單有「PDF 修改器 (Rabi Toolkit)」/「文書工具 (Rabi Toolkit)」，亦可以喺 Windows 設定揀做預設程式
/// • 右鍵第一層有「使用 Rabi Toolkit - PDF 開啟」/「使用 Rabi Toolkit - 文件 開啟」（Windows 11 新式右鍵要撳「顯示更多選項」）
/// • 命令列：RabiToolkit.exe --pdf 檔案 / --doc 檔案；程式已經開咗就用 named pipe 交俾佢開新視窗
/// </summary>
internal static class DocShell
{
    public const string PdfMsg = "\u0002pdf";
    private const string CapKey = @"Software\RabiToolkit\Capabilities";

    /// <summary>一種檔案：副檔名、登記名、邊個工具負責、圖示（exe 入面嘅 resource）。</summary>
    private sealed record Kind(string Ext, string ProgId, string Verb, string Flag, string Tool, string IconRes, Func<string> Name, Func<string> VerbName, Action<string> Open)
    {
        public string VerbKey => $@"Software\Classes\SystemFileAssociations\{Ext}\shell\{Verb}";
    }

    private static readonly Kind[] Kinds =
    {
        new(".pdf", "RabiToolkit.Pdf", "RabiToolkit.OpenPdf", "--pdf", Extensions.Office, "icon.pdf",
            () => Lang.T("PDF 修改器"), () => Lang.T("使用 Rabi Toolkit - PDF 開啟"), p => OfficeWindow.Open(p)),
        new(".docx", "RabiToolkit.Docx", "RabiToolkit.OpenDocx", "--doc", Extensions.Office, "icon.docx",
            () => Lang.T("文書工具"), () => Lang.T("使用 Rabi Toolkit - 文件 開啟"), p => OfficeWindow.Open(p)),
        new(".xlsx", "RabiToolkit.Xlsx", "RabiToolkit.OpenXlsx", "--doc", Extensions.Office, "icon.xlsx",
            () => Lang.T("文書工具"), () => Lang.T("使用 Rabi Toolkit - 試算表 開啟"), p => OfficeWindow.Open(p)),
        new(".pptx", "RabiToolkit.Pptx", "RabiToolkit.OpenPptx", "--doc", Extensions.Office, "icon.pptx",
            () => Lang.T("文書工具"), () => Lang.T("使用 Rabi Toolkit - 簡報 開啟"), p => OfficeWindow.Open(p)),
    };

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    /// <summary>命令列：--pdf 檔案… / --doc 檔案…（冇就 null）。回傳嘅陣列第一格係 PdfMsg。</summary>
    public static string[]? Args(string[] args)
    {
        int i = -1;
        foreach (var k in Kinds) { i = Array.IndexOf(args, k.Flag); if (i >= 0) break; }
        if (i < 0) return null;
        var files = new List<string> { PdfMsg };
        for (int k = i + 1; k < args.Length && !args[k].StartsWith("--"); k++) files.Add(args[k]);
        return files.Count > 1 ? files.ToArray() : null;
    }

    /// <summary>開檔訊息（第一格 PdfMsg）：每個檔一個新視窗，睇副檔名決定用邊個工具。</summary>
    public static bool Handle(string[] msg)
    {
        if (msg.Length < 2 || msg[0] != PdfMsg) return false;
        for (int i = 1; i < msg.Length; i++)
        {
            if (!File.Exists(msg[i])) continue;
            string ext = Path.GetExtension(msg[i]).ToLowerInvariant();
            var k = Array.Find(Kinds, x => x.Ext == ext) ?? Kinds[0];
            k.Open(msg[i]);
        }
        return true;
    }

    private static int _open;
    /// <summary>開咗一個文件視窗：全部閂晒就話俾主視窗知（由檔案總管開文件而啟動嘅話，程式可以結束）。</summary>
    public static void WindowOpened(Form f)
    {
        _open++;
        f.FormClosed += (_, _) => { if (--_open <= 0) { _open = 0; MainForm.DocWindowsClosed(); } };
    }

    public static void Sync()
    {
        try
        {
            bool any = false, changed = false;
            foreach (var k in Kinds)
            {
                if (Extensions.IsInstalled(k.Tool)) { any = true; changed |= Register(k); }
                else changed |= Unregister(k);
            }
            var R = Microsoft.Win32.Registry.CurrentUser;
            if (!any)
            {
                using (var chk = R.OpenSubKey(@"Software\RabiToolkit"))
                    if (chk != null)
                    {
                        R.DeleteSubKeyTree(@"Software\RabiToolkit", false);
                        using var k = R.OpenSubKey(@"Software\RegisteredApplications", true);
                        k?.DeleteValue(Program.FileName, false);
                        changed = true;
                    }
            }
            if (changed) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { AppPaths.LogError("文件右鍵登記", ex); }
    }

    /// <summary>登記一種檔案；回傳有冇改過登錄檔。</summary>
    private static bool Register(Kind t)
    {
        var R = Microsoft.Win32.Registry.CurrentUser;
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0 || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        string cmd = $"\"{exe}\" {t.Flag} \"%1\"";
        string name = t.Name() + " (" + Program.AppName + ")";
        string verbName = t.VerbName();
        string icon = DocIcon(t);
        using (var c = R.OpenSubKey($@"Software\Classes\{t.ProgId}\shell\open\command"))
        using (var n = R.OpenSubKey($@"Software\Classes\{t.ProgId}"))
        using (var v = R.OpenSubKey(t.VerbKey))
        using (var vc = R.OpenSubKey(t.VerbKey + @"\command"))
        using (var cap = R.OpenSubKey(CapKey + @"\FileAssociations"))
        using (var ic = R.OpenSubKey($@"Software\Classes\{t.ProgId}\DefaultIcon"))
            if (ic?.GetValue("") as string == icon && c?.GetValue("") as string == cmd && n?.GetValue("") as string == name && v?.GetValue("MUIVerb") as string == verbName
                && vc?.GetValue("") as string == cmd && cap?.GetValue(t.Ext) as string == t.ProgId) return false;   // 已經登記好
        using (var k = R.CreateSubKey($@"Software\Classes\{t.ProgId}")) { k.SetValue("", name); k.SetValue("FriendlyTypeName", name); }
        using (var k = R.CreateSubKey($@"Software\Classes\{t.ProgId}\DefaultIcon")) k.SetValue("", icon);
        using (var k = R.CreateSubKey($@"Software\Classes\{t.ProgId}\Application")) k.SetValue("ApplicationName", name);
        using (var k = R.CreateSubKey($@"Software\Classes\{t.ProgId}\shell\open")) { k.SetValue("", name); k.SetValue("FriendlyAppName", name); }
        using (var k = R.CreateSubKey($@"Software\Classes\{t.ProgId}\shell\open\command")) k.SetValue("", cmd);
        using (var k = R.CreateSubKey($@"Software\Classes\{t.Ext}\OpenWithProgids")) k.SetValue(t.ProgId, Array.Empty<byte>(), Microsoft.Win32.RegistryValueKind.None);
        // Windows 設定 →「預設應用程式」清單
        using (var k = R.CreateSubKey(CapKey)) { k.SetValue("ApplicationName", Program.AppName); k.SetValue("ApplicationDescription", Program.AppName); }
        using (var k = R.CreateSubKey(CapKey + @"\FileAssociations")) k.SetValue(t.Ext, t.ProgId);
        using (var k = R.CreateSubKey(@"Software\RegisteredApplications")) k.SetValue(Program.FileName, CapKey);
        // 右鍵第一層
        using (var k = R.CreateSubKey(t.VerbKey)) { k.SetValue("MUIVerb", verbName); k.SetValue("Icon", $"\"{exe}\",0"); }
        using (var k = R.CreateSubKey(t.VerbKey + @"\command")) k.SetValue("", cmd);
        return true;
    }

    /// <summary>設做預設程式之後檔案顯示嘅圖示（文件 + 右上角兔仔）：由 exe 解壓去 data\lib\icons\<類型>.ico。</summary>
    private static string DocIcon(Kind t)
    {
        string exe = Environment.ProcessPath ?? "";
        try
        {
            using var s = typeof(DocShell).Assembly.GetManifestResourceStream(t.IconRes);
            if (s == null) return $"\"{exe}\",0";
            string path = Path.Combine(AppPaths.Data, "lib", "icons", t.Ext.TrimStart('.') + ".ico");
            if (!File.Exists(path) || new FileInfo(path).Length != s.Length)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var f = File.Create(path);
                s.CopyTo(f);
            }
            return $"\"{path}\"";
        }
        catch { return $"\"{exe}\",0"; }
    }

    /// <summary>取消登記一種檔案；回傳有冇改過登錄檔。</summary>
    private static bool Unregister(Kind t)
    {
        var R = Microsoft.Win32.Registry.CurrentUser;
        using (var chk = R.OpenSubKey($@"Software\Classes\{t.ProgId}")) if (chk == null) return false;
        R.DeleteSubKeyTree($@"Software\Classes\{t.ProgId}", false);
        using (var k = R.OpenSubKey($@"Software\Classes\{t.Ext}\OpenWithProgids", true)) k?.DeleteValue(t.ProgId, false);
        R.DeleteSubKeyTree(t.VerbKey, false);
        using (var k = R.OpenSubKey(CapKey + @"\FileAssociations", true)) k?.DeleteValue(t.Ext, false);
        return true;
    }

    /// <summary>開 Windows「預設應用程式」設定（Windows 唔准程式自己改預設，要用家自己揀）。</summary>
    public static void OpenDefaultApps()
    {
        // Windows 11：直接跳去拉比工具箱嗰頁（registeredAppUser = RegisteredApplications 入面個名）；舊版 Windows 會開返預設應用程式總頁
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(Program.FileName)) { UseShellExecute = true }); } catch { }
    }
}
