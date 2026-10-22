using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using MultiTool.Common;
using MultiTool.UI;

namespace MultiTool.Tools;

// =====================================================================
//  文件轉檔：Excel (.xlsx) ↔ CSV / TSV / JSON，CSV 編碼轉換。
//  自己讀寫 xlsx（佢係 zip + xml），唔使裝 Office 或者第三方程式。
// =====================================================================

internal sealed record Sheet(string Name, List<List<string>> Rows);

internal static class Tables
{
    static Tables() { try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { } }
    public static void Init() { }

    // ---------------- 讀 ----------------

    public static List<Sheet> Read(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" or ".xlsm" => ReadXlsx(path),
            ".xls" => throw new NotSupportedException(Lang.T("舊版 .xls 唔支援：請用 Excel「另存新檔」做 .xlsx")),
            ".json" => new() { new(Path.GetFileNameWithoutExtension(path), ReadJson(File.ReadAllText(path, DetectEncoding(path)))) },
            _ => new() { new(Path.GetFileNameWithoutExtension(path), ReadDelimited(File.ReadAllText(path, DetectEncoding(path)), ext == ".tsv" ? '\t' : null)) }
        };
    }

    /// <summary>有 BOM 用 BOM；合法 UTF-8 就 UTF-8；否則當繁中 Big5（香港 / 台灣 Excel 舊 CSV 多數係）。</summary>
    public static Encoding DetectEncoding(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return new UTF8Encoding(false);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode;
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode;
        try { new UTF8Encoding(false, true).GetString(b); return new UTF8Encoding(false); }
        catch (DecoderFallbackException) { }
        try { return Encoding.GetEncoding(950); } catch { return Encoding.Default; }
    }

    public static List<List<string>> ReadDelimited(string text, char? sep)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        char d = sep ?? GuessSep(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool q = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (q)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else q = false;
                }
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) q = true;
            else if (c == d) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                rows.Add(row); row = new List<string>();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    private static char GuessSep(string text)
    {
        var first = text.Split('\n').Take(5).ToList();
        char[] cands = { ',', '\t', ';', '|' };
        return cands.OrderByDescending(c => first.Sum(l => l.Count(x => x == c))).First();
    }

    public static List<List<string>> ReadJson(string text)
    {
        var node = JsonNode.Parse(text);
        if (node is JsonObject o)
        {
            // {"data":[...]} 呢類：搵第一個 array
            node = o.Select(kv => kv.Value).OfType<JsonArray>().FirstOrDefault() ?? new JsonArray(o.DeepClone());
        }
        if (node is not JsonArray arr) throw new InvalidDataException(Lang.T("JSON 要係一個列表（array）"));
        static string V(JsonNode? n) => n switch
        {
            null => "",
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v => v.ToJsonString(),
            _ => n.ToJsonString()
        };
        var rows = new List<List<string>>();
        if (arr.All(x => x is JsonArray))
        {
            foreach (JsonArray r in arr.Cast<JsonArray>()) rows.Add(r.Select(V).ToList());
            return rows;
        }
        var keys = new List<string>();
        foreach (var x in arr.OfType<JsonObject>())
            foreach (var kv in x) if (!keys.Contains(kv.Key)) keys.Add(kv.Key);
        rows.Add(keys.ToList());
        foreach (var x in arr)
            rows.Add(x is JsonObject jo ? keys.Select(k => jo.TryGetPropertyValue(k, out var v) ? V(v) : "").ToList() : new List<string> { V(x) });
        return rows;
    }

    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static XDocument LoadXml(ZipArchive z, string name)
    {
        var e = z.GetEntry(name) ?? z.Entries.FirstOrDefault(x => string.Equals(x.FullName, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("missing " + name);
        using var s = e.Open();
        return XDocument.Load(s);
    }

    public static List<Sheet> ReadXlsx(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var z = new ZipArchive(fs, ZipArchiveMode.Read);
        // 共用字串
        var shared = new List<string>();
        if (z.GetEntry("xl/sharedStrings.xml") != null)
            foreach (var si in LoadXml(z, "xl/sharedStrings.xml").Root!.Elements(Ns + "si"))
                shared.Add(string.Concat(si.Descendants(Ns + "t").Where(t => t.Parent?.Name != Ns + "rPh").Select(t => t.Value)));
        // 邊啲格式係日期
        var dateStyle = new List<bool>();
        if (z.GetEntry("xl/styles.xml") != null)
        {
            var st = LoadXml(z, "xl/styles.xml").Root!;
            var custom = st.Element(Ns + "numFmts")?.Elements(Ns + "numFmt")
                .ToDictionary(x => (int)x.Attribute("numFmtId")!, x => (string?)x.Attribute("formatCode") ?? "") ?? new();
            foreach (var xf in st.Element(Ns + "cellXfs")?.Elements(Ns + "xf") ?? Enumerable.Empty<XElement>())
            {
                int id = (int?)xf.Attribute("numFmtId") ?? 0;
                bool date = id is >= 14 and <= 22 or >= 45 and <= 47 or >= 27 and <= 36 or >= 50 and <= 58;
                if (custom.TryGetValue(id, out var code))
                {
                    var clean = Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", "");
                    date = Regex.IsMatch(clean, "[dmyhs]", RegexOptions.IgnoreCase) && !clean.Contains('0') && !clean.Contains('#');
                }
                dateStyle.Add(date);
            }
        }
        var wb = LoadXml(z, "xl/workbook.xml").Root!;
        var rels = LoadXml(z, "xl/_rels/workbook.xml.rels").Root!.Elements(PkgRel + "Relationship")
            .ToDictionary(x => (string)x.Attribute("Id")!, x => (string)x.Attribute("Target")!);
        bool date1904 = (string?)wb.Element(Ns + "workbookPr")?.Attribute("date1904") is "1" or "true";
        var sheets = new List<Sheet>();
        foreach (var s in wb.Element(Ns + "sheets")?.Elements(Ns + "sheet") ?? Enumerable.Empty<XElement>())
        {
            var rid = (string?)s.Attribute(RelNs + "id");
            if (rid == null || !rels.TryGetValue(rid, out var target)) continue;
            target = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (z.GetEntry(target) == null) continue;
            var rows = new List<List<string>>();
            var data = LoadXml(z, target).Root!.Element(Ns + "sheetData");
            int nextRow = 1;
            foreach (var r in data?.Elements(Ns + "row") ?? Enumerable.Empty<XElement>())
            {
                int rn = (int?)r.Attribute("r") ?? nextRow;
                while (rows.Count < rn - 1) rows.Add(new List<string>());
                nextRow = rn + 1;
                var row = new List<string>();
                int nextCol = 0;
                foreach (var c in r.Elements(Ns + "c"))
                {
                    int col = ColIndex((string?)c.Attribute("r")) ?? nextCol;
                    while (row.Count < col) row.Add("");
                    nextCol = col + 1;
                    string t = (string?)c.Attribute("t") ?? "n";
                    string v = c.Element(Ns + "v")?.Value ?? "";
                    int sIdx = (int?)c.Attribute("s") ?? 0;
                    string text = t switch
                    {
                        "s" => int.TryParse(v, out int i) && i < shared.Count ? shared[i] : "",
                        "inlineStr" => string.Concat(c.Element(Ns + "is")?.Descendants(Ns + "t").Select(x => x.Value) ?? Array.Empty<string>()),
                        "b" => v == "1" ? "TRUE" : "FALSE",
                        "str" or "e" => v,
                        _ => sIdx < dateStyle.Count && dateStyle[sIdx] && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv) ? OaDate(dv, date1904) : v
                    };
                    row.Add(text);
                }
                rows.Add(row);
            }
            // 去尾空行
            while (rows.Count > 0 && rows[^1].All(x => x.Length == 0)) rows.RemoveAt(rows.Count - 1);
            sheets.Add(new Sheet((string?)s.Attribute("name") ?? "Sheet" + (sheets.Count + 1), rows));
        }
        return sheets;
    }

    private static string OaDate(double v, bool from1904)
    {
        try
        {
            var d = DateTime.FromOADate(v + (from1904 ? 1462 : 0));
            if (v < 1) return d.ToString("HH:mm:ss");
            return d.TimeOfDay.TotalSeconds < 1 ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch { return v.ToString(CultureInfo.InvariantCulture); }
    }

    private static int? ColIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return null;
        int n = 0, i = 0;
        for (; i < cellRef.Length && char.IsLetter(cellRef[i]); i++) n = n * 26 + (char.ToUpperInvariant(cellRef[i]) - 'A' + 1);
        return i == 0 ? null : n - 1;
    }

    private static string ColName(int i)
    {
        var s = "";
        for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s;
        return s;
    }

    // ---------------- 寫 ----------------

    public static void WriteDelimited(string path, List<List<string>> rows, char sep, Encoding enc)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Append(string.Join(sep, r.Select(c =>
                c.IndexOfAny(new[] { sep, '"', '\r', '\n' }) >= 0 || (c.Length > 0 && (c[0] == ' ' || c[^1] == ' ')) ? "\"" + c.Replace("\"", "\"\"") + "\"" : c)));
            sb.Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString(), enc);
    }

    public static void WriteJson(string path, List<List<string>> rows, bool header)
    {
        JsonNode root;
        if (header && rows.Count > 0)
        {
            var keys = rows[0].Select((k, i) => k.Length > 0 ? k : "col" + (i + 1)).ToList();
            // 同名欄加編號
            for (int i = 0; i < keys.Count; i++)
                for (int j = 0, n = 2; j < i; j++) if (keys[j] == keys[i]) { keys[i] = keys[i] + "_" + n++; j = -1; }
            var arr = new JsonArray();
            foreach (var r in rows.Skip(1))
            {
                var o = new JsonObject();
                for (int i = 0; i < keys.Count; i++)
                {
                    var v = i < r.Count ? r[i] : "";
                    o[keys[i]] = AsNumber(v, out _) ? JsonValue.Create(double.Parse(v, CultureInfo.InvariantCulture)) : JsonValue.Create(v);
                }
                arr.Add(o);
            }
            root = arr;
        }
        else root = new JsonArray(rows.Select(r => (JsonNode)new JsonArray(r.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray())).ToArray());
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
    }

    /// <summary>數字就存做數字（Excel 可以計），但「007」、長過 15 位（電話 / 身份證）保留文字。</summary>
    private static bool AsNumber(string s, out string norm)
    {
        norm = s;
        if (s.Length == 0 || s.Length > 15 || s != s.Trim()) return false;
        if (s.Length > 1 && s[0] == '0' && s[1] != '.') return false;
        if (s.StartsWith("+")) return false;
        if (!Regex.IsMatch(s, @"^-?\d+(\.\d+)?([eE][+-]?\d+)?$")) return false;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private static string SafeSheetName(string n, HashSet<string> used)
    {
        n = Regex.Replace(n, @"[\[\]\*\?/\\:]", "_").Trim('\'');
        if (n.Length == 0) n = "Sheet";
        if (n.Length > 31) n = n[..31];
        var b = n;
        for (int i = 2; !used.Add(n); i++) { var suf = " (" + i + ")"; n = (b.Length + suf.Length > 31 ? b[..(31 - suf.Length)] : b) + suf; }
        return n;
    }

    public static void WriteXlsx(string path, List<Sheet> sheets)
    {
        if (File.Exists(path)) File.Delete(path);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string name, string xml)
        {
            var e = z.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = sheets.Select(s => SafeSheetName(s.Name, used)).ToList();
        var ct = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (int i = 0; i < sheets.Count; i++)
            ct.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        ct.Append("</Types>");
        Put("[Content_Types].xml", ct.ToString());
        Put("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (int i = 0; i < sheets.Count; i++)
        {
            wb.Append($"<sheet name=\"{SecurityElement.Escape(names[i])}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
        }
        wb.Append("</sheets></workbook>");
        rels.Append($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        Put("xl/workbook.xml", wb.ToString());
        Put("xl/_rels/workbook.xml.rels", rels.ToString());
        Put("xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>");
        for (int i = 0; i < sheets.Count; i++)
        {
            var rows = sheets[i].Rows;
            var x = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (rows.Count > 0) x.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            // 欄闊：睇頭 100 行最長嘅字
            int cols = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
            if (cols > 0)
            {
                x.Append("<cols>");
                for (int c = 0; c < cols; c++)
                {
                    int len = rows.Take(100).Select(r => c < r.Count ? r[c].Sum(ch => ch > 0x2E80 ? 2 : 1) : 0).DefaultIfEmpty(0).Max();
                    x.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{Math.Clamp(len + 2, 6, 60)}\" customWidth=\"1\"/>");
                }
                x.Append("</cols>");
            }
            x.Append("<sheetData>");
            for (int r = 0; r < rows.Count; r++)
            {
                x.Append($"<row r=\"{r + 1}\">");
                for (int c = 0; c < rows[r].Count; c++)
                {
                    var v = rows[r][c];
                    if (v.Length == 0) continue;
                    string refName = ColName(c) + (r + 1), style = r == 0 ? " s=\"1\"" : "";
                    v = new string(v.Where(ch => ch == '\t' || ch == '\n' || ch == '\r' || ch >= 0x20).ToArray());
                    if (r > 0 && AsNumber(v, out var num)) x.Append($"<c r=\"{refName}\"{style}><v>{num}</v></c>");
                    else x.Append($"<c r=\"{refName}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(v)}</t></is></c>");
                }
                x.Append("</row>");
            }
            x.Append("</sheetData></worksheet>");
            Put($"xl/worksheets/sheet{i + 1}.xml", x.ToString());
        }
    }
}

internal sealed class DocConvertPage : ToolPage
{
    private sealed class Job { public required string Path; public string Status = ""; public Label? Label; }

    private static readonly (string Ext, string Name)[] Outs =
    {
        ("xlsx", "Excel (.xlsx)"), ("csv", "CSV"), ("tsv", Lang.T("TSV（Tab 分隔）")), ("json", "JSON")
    };
    private static readonly (string Key, string Name)[] Encs =
    {
        ("utf8bom", Lang.T("UTF-8（Excel 開到中文，建議）")), ("utf8", Lang.T("UTF-8 冇 BOM（程式用）")), ("big5", Lang.T("Big5 繁體（舊系統）")), ("gbk", Lang.T("GBK 簡體（舊系統）"))
    };
    private static readonly string[] InExts = { ".xlsx", ".xlsm", ".xls", ".csv", ".tsv", ".txt", ".json" };

    private readonly List<Job> _jobs = new();
    private readonly FlowLayoutPanel _list;
    private readonly FancyButton _fmtBtn, _encBtn, _outBtn, _start;
    private readonly CheckBox _merge, _header;
    private readonly Label _status;
    private string _fmt = "xlsx", _enc = "utf8bom", _outDir = "";
    private bool _busy;

    public static Control Build() => new DocConvertPage();

    private DocConvertPage()
    {
        AllowDrop = true;
        Stack.AllowDrop = true;
        void Enter(object? s, DragEventArgs e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        void Drop(object? s, DragEventArgs e) { if (e.Data?.GetData(DataFormats.FileDrop) is string[] f) Add(f); }
        DragEnter += Enter; DragDrop += Drop; Stack.DragEnter += Enter; Stack.DragDrop += Drop;

        var add = TUi.Btn(Lang.T("+ 加入檔案…"), () =>
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Filter = Lang.T("表格檔案") + "|*.xlsx;*.xlsm;*.csv;*.tsv;*.txt;*.json|" + Lang.T("所有檔案") + "|*.*" };
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK) Add(dlg.FileNames);
        }, 0, BtnKind.Primary);
        var clear = TUi.Btn(Lang.T("清空"), () => { if (!_busy) { _jobs.Clear(); BuildRows(); } });
        _list = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Card(Lang.T("文件轉檔"), Lang.T("Excel (.xlsx)、CSV、TSV、JSON 互轉，亦可以改 CSV 編碼（例如 Excel 開 CSV 中文亂碼）。可以直接拖檔案入嚟。"),
            TUi.Col(TUi.Row(add, clear), _list));

        _fmtBtn = TUi.Btn("", () => TUi.Pick(_fmtBtn!, Outs.Select(o => (o.Name, o.Ext)), _fmt, v => { _fmt = v; UpdateLabels(); }), 170);
        _encBtn = TUi.Btn("", () => TUi.Pick(_encBtn!, Encs.Select(o => (o.Name, o.Key)), _enc, v => { _enc = v; UpdateLabels(); }), 270);
        _outBtn = TUi.Btn("", () =>
        {
            var menu = TUi.Menu(_outBtn!);
            var same = new ToolStripMenuItem(Lang.T("同原本檔案一樣")) { Checked = _outDir.Length == 0, ForeColor = Ui.Text };
            same.Click += (_, _) => { _outDir = ""; UpdateLabels(); };
            var pick = new ToolStripMenuItem(Lang.T("揀資料夾…")) { Checked = _outDir.Length > 0, ForeColor = Ui.Text };
            pick.Click += (_, _) =>
            {
                using var dlg = new FolderBrowserDialog();
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK) { _outDir = dlg.SelectedPath; UpdateLabels(); }
            };
            menu.Items.Add(same); menu.Items.Add(pick);
            menu.Show(_outBtn!, new Point(0, _outBtn!.Height));
        }, 240);
        _merge = TUi.Check(Lang.T("轉 Excel 時合併做一個檔（每個檔案一張工作表）"), false, _ => { });
        _header = TUi.Check(Lang.T("第一行係標題（轉 JSON 用）"), true, _ => { });
        _start = TUi.Btn(Lang.T("▶ 開始轉檔"), () => _ = StartAsync(), 130, BtnKind.Success);
        _status = TUi.L("");
        Card(Lang.T("輸出"), Lang.T("Excel 有幾張工作表、轉做 CSV / TSV / JSON 時會每張出一個檔。數字、日期會照 Excel 顯示嘅值輸出；公式淨係輸出計好嘅結果。"),
            TUi.Col(
                TUi.Row(TUi.L(Lang.T("格式")), _fmtBtn, TUi.L(Lang.T("編碼")), _encBtn),
                TUi.Row(_merge),
                TUi.Row(_header),
                TUi.Row(TUi.L(Lang.T("儲存位置")), _outBtn),
                TUi.Row(_start, _status)));
        UpdateLabels();
        BuildRows();
    }

    private void UpdateLabels()
    {
        _fmtBtn.Text = Lang.T("格式：") + Outs.First(o => o.Ext == _fmt).Name + " ▾";
        _encBtn.Text = Encs.First(o => o.Key == _enc).Name + " ▾";
        _encBtn.Enabled = _fmt is "csv" or "tsv";
        _outBtn.Text = (_outDir.Length == 0 ? Lang.T("同原本檔案一樣") : _outDir) + " ▾";
        _merge.Enabled = _fmt == "xlsx";
        _header.Enabled = _fmt == "json";
        foreach (var b in new[] { _fmtBtn, _encBtn, _outBtn }) b.Invalidate();
    }

    private void Add(IEnumerable<string> files)
    {
        if (_busy) return;
        foreach (var f in files.Where(File.Exists))
        {
            if (!InExts.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            if (!_jobs.Any(j => string.Equals(j.Path, f, StringComparison.OrdinalIgnoreCase))) _jobs.Add(new Job { Path = f });
        }
        BuildRows();
    }

    private void BuildRows()
    {
        _list.SuspendLayout();
        foreach (Control c in _list.Controls.Cast<Control>().ToList()) { _list.Controls.Remove(c); c.Dispose(); }
        if (_jobs.Count == 0) _list.Controls.Add(TUi.L(Lang.T("未有檔案：撳「+ 加入檔案…」或者直接拖入嚟（.xlsx / .csv / .tsv / .txt / .json）")));
        foreach (var j in _jobs.ToList())
        {
            var job = j;
            var name = new Label
            {
                Text = Path.GetFileName(job.Path), AutoSize = false, Width = Ui.S(360), Height = Ui.S(34), ForeColor = Ui.Text, BackColor = Ui.Card,
                Font = Ui.F(9.5f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false, Margin = new Padding(0, 0, Ui.S(10), 0)
            };
            job.Label = new Label
            {
                Text = job.Status, AutoSize = false, Width = Ui.S(260), Height = Ui.S(34), ForeColor = Ui.TextDim, BackColor = Ui.Card,
                Font = Ui.F(9.25f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false, Margin = new Padding(0, 0, Ui.S(10), 0)
            };
            var del = TUi.Btn("✕", () => { if (!_busy) { _jobs.Remove(job); BuildRows(); } }, 40);
            var row = TUi.Row(name, job.Label, del);
            row.WrapContents = false;
            row.Margin = new Padding(0, 0, 0, Ui.S(4));
            _list.Controls.Add(row);
        }
        _list.ResumeLayout(true);
        Relayout();
    }

    private static void SetStatus(Job j, string text, Color? c = null)
    {
        j.Status = text;
        if (j.Label is { IsDisposed: false } l) { l.Text = text; l.ForeColor = c ?? Ui.TextDim; }
    }

    private Encoding OutEncoding() => _enc switch
    {
        "utf8" => new UTF8Encoding(false),
        "big5" => Encoding.GetEncoding(950),
        "gbk" => Encoding.GetEncoding(936),
        _ => new UTF8Encoding(true)
    };

    private string OutPath(string src, string suffix, string ext)
    {
        string dir = _outDir.Length > 0 ? _outDir : Path.GetDirectoryName(src) ?? "";
        string p = Path.Combine(dir, Path.GetFileNameWithoutExtension(src) + suffix + "." + ext);
        if (string.Equals(p, src, StringComparison.OrdinalIgnoreCase)) p = Path.Combine(dir, Path.GetFileNameWithoutExtension(src) + Lang.T(" (轉檔)") + "." + ext);
        return TUi.Unique(p);
    }

    private async Task StartAsync()
    {
        if (_busy) return;
        var todo = _jobs.ToList();
        if (todo.Count == 0) { _status.Text = Lang.T("未有要轉嘅檔案"); return; }
        _busy = true;
        _start.Enabled = false;
        Tables.Init(); // 確保註冊咗舊編碼
        string fmt = _fmt;
        bool merge = _merge.Checked && fmt == "xlsx", header = _header.Checked;
        var enc = fmt is "csv" or "tsv" ? OutEncoding() : new UTF8Encoding(false);
        int ok = 0;
        string? last = null;
        var merged = new List<Sheet>();
        foreach (var j in todo)
        {
            SetStatus(j, Lang.T("轉緊…"), Ui.Accent);
            try
            {
                var (outs, sheets) = await Task.Run(() =>
                {
                    var sheets = Tables.Read(j.Path);
                    var outs = new List<string>();
                    if (sheets.Count == 0) throw new InvalidDataException(Lang.T("檔案入面冇資料"));
                    if (merge) return (outs, sheets);
                    if (fmt == "xlsx")
                    {
                        var p = OutPath(j.Path, "", "xlsx");
                        Tables.WriteXlsx(p, sheets);
                        outs.Add(p);
                    }
                    else
                    {
                        foreach (var s in sheets)
                        {
                            string suffix = sheets.Count > 1 ? " - " + Regex.Replace(s.Name, @"[\\/:*?""<>|]", "_") : "";
                            var p = OutPath(j.Path, suffix, fmt);
                            if (fmt == "json") Tables.WriteJson(p, s.Rows, header);
                            else Tables.WriteDelimited(p, s.Rows, fmt == "tsv" ? '\t' : ',', enc);
                            outs.Add(p);
                        }
                    }
                    return (outs, sheets);
                });
                if (merge)
                {
                    foreach (var s in sheets) merged.Add(sheets.Count > 1 ? s with { Name = Path.GetFileNameWithoutExtension(j.Path) + " - " + s.Name } : s with { Name = Path.GetFileNameWithoutExtension(j.Path) });
                    SetStatus(j, Lang.F("✓ 讀咗 {0} 張表", sheets.Count), Ui.Success);
                }
                else
                {
                    SetStatus(j, "✓ " + string.Join("、", outs.Select(Path.GetFileName)), Ui.Success);
                    last = outs.LastOrDefault();
                }
                ok++;
            }
            catch (Exception ex)
            {
                SetStatus(j, Lang.T("失敗：") + ex.Message, Ui.Danger);
                AppPaths.LogError("文件轉檔", ex);
            }
        }
        if (merge && merged.Count > 0)
        {
            try
            {
                var p = OutPath(todo[0].Path, Lang.T(" (合併)"), "xlsx");
                await Task.Run(() => Tables.WriteXlsx(p, merged));
                last = p;
            }
            catch (Exception ex) { _status.Text = Lang.T("失敗：") + ex.Message; AppPaths.LogError("文件轉檔", ex); ok = 0; }
        }
        _busy = false;
        _start.Enabled = true;
        if (ok > 0 || !merge) _status.Text = Lang.F("完成 {0} / {1} 個", ok, todo.Count);
        if (last != null) TUi.Reveal(last);
    }
}

/// <summary>轉檔工具：上面分頁揀「影音」或者「文件」。</summary>
internal sealed class ConvertHub : Panel
{
    private readonly Control[] _pages = new Control[2];
    private readonly Func<Control>[] _builders = { ConvertPage.Build, DocConvertPage.Build };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private static int _last;

    public static Control Build() => new ConvertHub();

    private ConvertHub()
    {
        BackColor = Ui.Bg;
        Dock = DockStyle.Fill;
        var tabs = new TabStrip(Lang.T("影音"), Lang.T("文件")) { Dock = DockStyle.Fill };
        var head = new Panel { Dock = DockStyle.Top, Height = Ui.S(42) + Ui.S(14), BackColor = Ui.Bg, Padding = new Padding(Ui.S(24), Ui.S(14), Ui.S(24), 0) };
        head.Controls.Add(tabs);
        Controls.Add(_body);
        Controls.Add(head);
        tabs.Selected += Show;
        tabs.Index = _last;
    }

    private void Show(int i)
    {
        _last = i;
        _body.SuspendLayout();
        for (int k = 0; k < _pages.Length; k++)
        {
            if (k == i && _pages[k] == null) { _pages[k] = _builders[k](); _body.Controls.Add(_pages[k]); }
            if (_pages[k] != null) _pages[k].Visible = k == i;
        }
        _body.ResumeLayout(true);
    }
}
