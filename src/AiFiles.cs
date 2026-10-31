using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Ai;

/// <summary>
/// 對話附件（圖片 / 文件）：複製去 data\ai\attach\&lt;批次&gt;\，再話俾 AI 知喺邊。
/// • Claude Code：用 Read 工具讀（圖片、PDF、文字都得）；--add-dir 准佢讀嗰個資料夾
/// • Codex：圖片用 -i 直接俾佢睇；其他檔案佢自己用唯讀 shell 讀
/// • Gemini：喺問題入面用 @路徑 附上
/// • 本地 AI：睇唔到圖，文字檔直接貼入問題
/// Word / Excel / PowerPoint 會另外抽出文字（.txt），咁每個 AI 都讀得到。
/// </summary>
internal static class AiFiles
{
    public const int MaxFiles = 10;
    public const long MaxBytes = 30L << 20;

    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
    private static readonly string[] TextExt =
    {
        ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".html", ".htm", ".css", ".js", ".ts", ".py", ".cs", ".java", ".c", ".cpp", ".h",
        ".log", ".ini", ".yaml", ".yml", ".toml", ".sql", ".bat", ".ps1", ".sh", ".srt", ".lua", ".go", ".rs", ".php", ".rb", ".kt", ".swift"
    };

    public static bool IsImage(string p) => ImageExt.Contains(Path.GetExtension(p).ToLowerInvariant());
    private static bool IsText(string p) => TextExt.Contains(Path.GetExtension(p).ToLowerInvariant());

    public static string Filter =>
        Lang.T("圖片及文件") + "|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.pdf;*.docx;*.xlsx;*.pptx;*.txt;*.md;*.csv;*.json;*.xml;*.html;*.log;*.py;*.js;*.cs|" +
        Lang.T("圖片") + "|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|" + Lang.T("所有檔案") + "|*.*";

    private static string Root => Path.Combine(AiProvider.WorkDir, "attach");

    /// <summary>將揀咗嘅檔案複製去新嘅批次資料夾；回傳複製咗嘅路徑（連抽出嚟嘅文字檔）。</summary>
    public static List<string> Prepare(IEnumerable<string> src)
    {
        var res = new List<string>();
        var list = src.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToList();
        if (list.Count == 0) return res;
        string dir = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4]);
        Directory.CreateDirectory(dir);
        foreach (var f in list)
        {
            try
            {
                if (new FileInfo(f).Length > MaxBytes) continue;
                string dest = Unique(dir, SafeName(Path.GetFileName(f)));
                File.Copy(f, dest);
                res.Add(dest);
                // Office 文件：另外抽一份文字出嚟
                if (ExtractText(dest) is { Length: > 0 } text)
                {
                    string txt = Unique(dir, Path.GetFileNameWithoutExtension(dest) + "（文字）.txt");
                    File.WriteAllText(txt, text, new UTF8Encoding(false));
                    res.Add(txt);
                }
            }
            catch (Exception ex) { AppPaths.LogError("AI 附件", ex); }
        }
        return res;
    }

    /// <summary>貼上嘅圖片：存做 PNG（之後再 Prepare）。</summary>
    public static string SavePasted(Image img)
    {
        string dir = Path.Combine(Root, "pasted");
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, Lang.T("截圖") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
        img.Save(p, ImageFormat.Png);
        return p;
    }

    /// <summary>清走 30 日前嘅附件。</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var d in Directory.GetDirectories(Root))
                if (Directory.GetLastWriteTime(d) < DateTime.Now.AddDays(-30)) try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }

    /// <summary>檔名：空格同特別符號換做 _（Gemini 嘅 @路徑 遇到空格會斷）。</summary>
    private static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '（' or '）' ? c : '_');
        string s = sb.ToString().Trim('_');
        return s.Length == 0 ? "file" : s;
    }

    private static string Unique(string dir, string name)
    {
        string p = Path.Combine(dir, name);
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + "-" + i + Path.GetExtension(name));
        return p;
    }

    public static IEnumerable<string> Dirs(IReadOnlyList<string> files) =>
        files.Select(f => Path.GetDirectoryName(f)!).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>附喺問題後面：有咩檔、點讀。</summary>
    public static string Note(IReadOnlyList<string> files, string how)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"[MultiTool: 用戶附加咗以下 {files.Count} 個檔案（{how}）。回答之前要先睇晒；如果讀唔到就話俾用戶知：");
        foreach (var f in files) sb.AppendLine("- " + f);
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>本地 AI：文字檔直接貼入問題（太長就截短）；圖片同其他檔案講明睇唔到。</summary>
    public static string Inline(IReadOnlyList<string> files, int maxChars = 24000)
    {
        var sb = new StringBuilder();
        var skipped = new List<string>();
        foreach (var f in files)
        {
            if (!IsText(f)) { if (!files.Any(x => x != f && Path.GetFileNameWithoutExtension(x).StartsWith(Path.GetFileNameWithoutExtension(f)))) skipped.Add(Path.GetFileName(f)); continue; }
            string t;
            try { t = File.ReadAllText(f); } catch { continue; }
            int left = maxChars - sb.Length;
            if (left <= 200) { skipped.Add(Path.GetFileName(f)); continue; }
            if (t.Length > left) t = t[..left] + "\n…（太長，截短咗）";
            sb.AppendLine().AppendLine($"=== 附件：{Path.GetFileName(f)} ===").AppendLine(t).AppendLine("=== 完 ===");
        }
        if (skipped.Count > 0)
            sb.AppendLine().AppendLine($"[MultiTool: 本地 AI 睇唔到呢啲附件（圖片 / PDF 等）：{string.Join("、", skipped)}。請話俾用戶知要用 Claude / ChatGPT / Gemini 先睇到。]");
        return sb.ToString();
    }

    // ---------- Word / Excel / PowerPoint 抽文字 ----------

    private static string? ExtractText(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".docx" or ".xlsx" or ".pptx")) return null;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            string Read(string name)
            {
                var e = zip.GetEntry(name);
                if (e == null) return "";
                using var r = new StreamReader(e.Open());
                return r.ReadToEnd();
            }
            var sb = new StringBuilder();
            if (ext == ".docx")
            {
                foreach (var name in new[] { "word/document.xml" }.Concat(zip.Entries.Select(e => e.FullName).Where(n => Regex.IsMatch(n, @"^word/(header|footer|footnotes)\d*\.xml$"))))
                    sb.AppendLine(XmlText(Read(name), "w:p", "w:t", "w:tab"));
            }
            else if (ext == ".pptx")
            {
                var slides = zip.Entries.Select(e => e.FullName).Where(n => Regex.IsMatch(n, @"^ppt/slides/slide\d+\.xml$"))
                    .OrderBy(n => int.Parse(Regex.Match(n, @"\d+").Value)).ToList();
                for (int i = 0; i < slides.Count; i++)
                    sb.AppendLine($"--- {i + 1} ---").AppendLine(XmlText(Read(slides[i]), "a:p", "a:t", null));
            }
            else
            {
                // xlsx：共用字串 + 每張工作表逐格（Tab 分隔）
                var shared = new List<string>();
                foreach (Match si in Regex.Matches(Read("xl/sharedStrings.xml"), @"<si>(.*?)</si>", RegexOptions.Singleline))
                    shared.Add(string.Concat(Regex.Matches(si.Groups[1].Value, @"<t[^>]*>(.*?)</t>", RegexOptions.Singleline).Select(m => Unxml(m.Groups[1].Value))));
                var names = Regex.Matches(Read("xl/workbook.xml"), @"<sheet [^>]*name=""([^""]*)""").Select(m => Unxml(m.Groups[1].Value)).ToList();
                var sheets = zip.Entries.Select(e => e.FullName).Where(n => Regex.IsMatch(n, @"^xl/worksheets/sheet\d+\.xml$"))
                    .OrderBy(n => int.Parse(Regex.Match(n, @"\d+").Value)).ToList();
                for (int s = 0; s < sheets.Count; s++)
                {
                    sb.AppendLine($"--- {(s < names.Count ? names[s] : "Sheet" + (s + 1))} ---");
                    foreach (Match row in Regex.Matches(Read(sheets[s]), @"<row[^>]*>(.*?)</row>", RegexOptions.Singleline))
                    {
                        var cells = new List<string>();
                        foreach (Match c in Regex.Matches(row.Groups[1].Value, @"<c ([^>]*?)(?:/>|>(.*?)</c>)", RegexOptions.Singleline))
                        {
                            string attrs = c.Groups[1].Value, inner = c.Groups[2].Value;
                            string v = Regex.Match(inner, @"<v>(.*?)</v>", RegexOptions.Singleline).Groups[1].Value;
                            if (attrs.Contains("t=\"s\"") && int.TryParse(v, out var idx)) v = idx < shared.Count ? shared[idx] : "";
                            else if (attrs.Contains("t=\"inlineStr\"")) v = string.Concat(Regex.Matches(inner, @"<t[^>]*>(.*?)</t>", RegexOptions.Singleline).Select(m => m.Groups[1].Value));
                            cells.Add(Unxml(v));
                        }
                        if (cells.Any(x => x.Length > 0)) sb.AppendLine(string.Join("\t", cells));
                    }
                }
            }
            string text = sb.ToString().Trim();
            return text.Length > 2_000_000 ? text[..2_000_000] : text;
        }
        catch { return null; }
    }

    /// <summary>XML → 文字：每個段落一行。</summary>
    private static string XmlText(string xml, string para, string run, string? tab)
    {
        var sb = new StringBuilder();
        foreach (Match p in Regex.Matches(xml, $@"<{para}[ >].*?</{para}>", RegexOptions.Singleline))
        {
            string body = p.Value;
            if (tab != null) body = body.Replace($"<{tab}/>", "<x:t>\t</x:t>");
            var line = string.Concat(Regex.Matches(body, $@"<(?:{run}|x:t)(?: [^>]*)?>(.*?)</(?:{run}|x:t)>", RegexOptions.Singleline).Select(m => Unxml(m.Groups[1].Value)));
            sb.AppendLine(line);
        }
        return sb.ToString().Trim();
    }

    private static string Unxml(string s) => System.Net.WebUtility.HtmlDecode(s);
}

/// <summary>輸入格上面嘅附件列：一粒粒「🖼 名 ✕」。支援拖檔案入嚟、Ctrl+V 貼圖 / 貼檔案。</summary>
internal sealed class AttachBar : FlowLayoutPanel
{
    private readonly List<string> _files = new();
    public IReadOnlyList<string> Files => _files;
    public event Action? Changed;

    public AttachBar()
    {
        AutoSize = false;
        WrapContents = true;
        Height = 0;
        Visible = false;
        Padding = new Padding(0, 0, 0, Ui.S(4));
    }

    /// <summary>揀檔案。</summary>
    public void Pick(IWin32Window? owner)
    {
        using var d = new OpenFileDialog { Multiselect = true, Filter = AiFiles.Filter, Title = Lang.T("附加圖片或文件") };
        if (d.ShowDialog(owner) == DialogResult.OK) Add(d.FileNames);
    }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (!File.Exists(p) || _files.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
            if (_files.Count >= AiFiles.MaxFiles) { Tools.Toast.Show(Lang.F("最多附加 {0} 個檔案", AiFiles.MaxFiles), Ui.Danger, 2000); break; }
            try { if (new FileInfo(p).Length > AiFiles.MaxBytes) { Tools.Toast.Show(Lang.F("「{0}」太大（上限 30 MB）", Path.GetFileName(p)), Ui.Danger, 2500); continue; } } catch { continue; }
            _files.Add(p);
        }
        Rebuild();
    }

    /// <summary>傳送：攞走附件（已複製去 AI 資料夾），清空列。</summary>
    public List<string> Take()
    {
        var res = _files.Count > 0 ? AiFiles.Prepare(_files) : new List<string>();
        _files.Clear();
        Rebuild();
        return res;
    }

    /// <summary>顯示喺對話入面：「📎 a.png、b.pdf」。</summary>
    public static string Label(IEnumerable<string> files)
    {
        var names = files.Where(f => !f.EndsWith("（文字）.txt")).Select(Path.GetFileName).ToList();
        return names.Count == 0 ? "" : "📎 " + string.Join("、", names);
    }

    private void Rebuild()
    {
        SuspendLayout();
        foreach (Control c in Controls.Cast<Control>().ToList()) { Controls.Remove(c); c.Dispose(); }
        foreach (var f in _files.ToList())
        {
            string file = f;
            var chip = new FancyButton
            {
                Text = (AiFiles.IsImage(f) ? "🖼 " : "📄 ") + Short(Path.GetFileName(f)) + "  ✕", Kind = BtnKind.Ghost, Font = Ui.F(8.75f),
                Height = Ui.S(26), Margin = new Padding(0, 0, Ui.S(6), Ui.S(4))
            };
            chip.Width = chip.GetPreferredSize(Size.Empty).Width - Ui.S(10);
            chip.Click += (_, _) => { _files.Remove(file); Rebuild(); };
            Controls.Add(chip);
        }
        ResumeLayout(true);
        Visible = _files.Count > 0;
        Height = _files.Count == 0 ? 0 : Math.Max(Ui.S(30), GetPreferredSize(new Size(Math.Max(Width, Ui.S(100)), 0)).Height);
        Changed?.Invoke();
    }

    private static string Short(string s) => s.Length > 28 ? s[..12] + "…" + s[^12..] : s;

    /// <summary>令輸入格 / 對話區支援拖檔案入嚟，同 Ctrl+V 貼圖片 / 檔案。</summary>
    public void Wire(TextBox input, params Control[] dropTargets)
    {
        foreach (var t in dropTargets.Append(input))
        {
            t.AllowDrop = true;
            t.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
            t.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) Add(files); };
        }
        input.KeyDown += (_, e) =>
        {
            if (!(e.Control && e.KeyCode == Keys.V) || input.ReadOnly) return;
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    Add(Clipboard.GetFileDropList().Cast<string>());
                    e.SuppressKeyPress = true;
                }
                else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
                {
                    using (img) Add(new[] { AiFiles.SavePasted(img) });
                    e.SuppressKeyPress = true;
                }
            }
            catch (Exception ex) { AppPaths.LogError("貼上附件", ex); }
        };
    }
}
