using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Tools;

/// <summary>記帳資料：全部只存喺你部機（data\ledger.json），唔會上載去任何地方。</summary>
internal static class LedgerStore
{
    public sealed class Entry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Date { get; set; } = "";       // yyyy-MM-dd
        public bool Income { get; set; }
        public string Category { get; set; } = "";
        public decimal Amount { get; set; }
        public string Note { get; set; } = "";
    }

    private static string File_ => Path.Combine(AppPaths.Data, "ledger.json");
    private static List<Entry>? _all;
    public static List<Entry> All
    {
        get
        {
            if (_all != null) return _all;
            try { _all = File.Exists(File_) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(File_)) ?? new() : new(); }
            catch (Exception ex) { AppPaths.LogError("記帳", ex); _all = new(); }
            return _all;
        }
    }

    public static void Save()
    {
        try
        {
            // 先寫暫存檔再換：寫到一半斷電都唔會整爛本帳
            string tmp = File_ + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(All, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(tmp, File_, true);
        }
        catch (Exception ex) { AppPaths.LogError("記帳", ex); }
    }
}

/// <summary>記帳：記低每日收支，按月睇總數同每類用咗幾多，可以匯出 CSV。</summary>
internal sealed class LedgerPage : ToolPage
{
    private static readonly string[] ExpenseCats = { "飲食", "交通", "購物", "娛樂", "屋企", "醫療", "學習", "其他" };
    private static readonly string[] IncomeCats = { "人工", "獎金", "投資", "其他" };

    private readonly DateTimePicker _date;
    private readonly ComboBox _type, _cat;
    private readonly TextBox _amount, _note;
    private readonly Label _addStatus, _monthLabel, _income, _expense, _balance;
    private readonly BarChart _chart = new();
    private readonly PieChart _pie = new();
    private readonly FancyButton _viewBtn;
    private bool _pieView = true;
    private static string ViewFile => Path.Combine(AppPaths.Data, "ledger-view.txt");
    private readonly RowList _list = new();
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    public static Control Build() => new LedgerPage();

    private LedgerPage()
    {
        _date = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = Ui.S(130), Font = Ui.F(10f), Margin = new Padding(0, Ui.S(4), Ui.S(10), 0) };
        _type = TIn.Combo(90, new[] { Lang.T("支出"), Lang.T("收入") });
        _cat = TIn.Combo(130, Array.Empty<string>());
        _cat.DropDownStyle = ComboBoxStyle.DropDown;   // 可以自己打新分類
        _amount = TIn.Box("", 110, Lang.T("金額"));
        _note = TIn.Box("", 240, Lang.T("備註（可以唔填）"));
        _addStatus = TUi.L("");
        var add = TUi.Btn(Lang.T("加入"), Add, 90, BtnKind.Primary);
        _amount.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Add(); } };
        _note.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Add(); } };
        _type.SelectedIndexChanged += (_, _) => FillCats();
        Card(Lang.T("記帳"), Lang.T("記低每日收支。資料只存喺你部電腦，唔會上載。分類可以自己打新嘅。"),
            TUi.Col(TUi.Row(_date, _type, _cat, _amount, _note, add), _addStatus));

        _monthLabel = new Label { AutoSize = true, ForeColor = Ui.Text, Font = Ui.F(12f, FontStyle.Bold), Margin = new Padding(Ui.S(6), Ui.S(6), Ui.S(12), 0) };
        // 圓形圖 / 棒型圖：一次顯示一種，個掣寫住撳落去會轉做乜；揀咗會記住
        try { _pieView = !File.Exists(ViewFile) || File.ReadAllText(ViewFile).Trim() != "bar"; } catch { }
        _viewBtn = TUi.Btn("", () =>
        {
            _pieView = !_pieView;
            try { File.WriteAllText(ViewFile, _pieView ? "pie" : "bar"); } catch { }
            Reload();
        }, 150);
        Label Big(Color c) => new() { AutoSize = true, ForeColor = c, Font = Ui.F(12f, FontStyle.Bold), Margin = new Padding(0, Ui.S(2), Ui.S(28), Ui.S(8)) };
        _income = Big(Ui.Success); _expense = Big(Ui.Danger); _balance = Big(Ui.Text);
        _chart.Width = Ui.S(640);
        Card(Lang.T("每月總結"), null, TUi.Col(
            TUi.Row(TUi.Btn("◀", () => Move(-1)), _monthLabel, TUi.Btn("▶", () => Move(1)), TUi.Btn(Lang.T("今個月"), () => { _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); Reload(); }), _viewBtn),
            TUi.Row(_income, _expense, _balance),
            _pie,
            _chart));

        _list.Columns = new[] { (Lang.T("日期"), 100, false), (Lang.T("分類"), 110, false), (Lang.T("備註"), 0, false), (Lang.T("金額"), 120, true) };
        _list.ActionText = Lang.T("刪除");
        _list.EmptyText = Lang.T("呢個月未有紀錄。");
        _list.Width = Ui.S(760); _list.Height = Ui.S(300);
        _list.RowAction += r =>
        {
            if (r.Tag is not LedgerStore.Entry en) return;
            if (MessageBox.Show(FindForm(), Lang.F("刪除呢筆紀錄？\n{0}　{1}　{2}", en.Date, en.Category, en.Amount.ToString("#,##0.##")), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            LedgerStore.All.Remove(en);
            LedgerStore.Save();
            Reload();
        };
        Card(Lang.T("紀錄"), null, TUi.Col(_list, TUi.Row(TUi.Btn(Lang.T("匯出呢個月（CSV）…"), () => Export(false)), TUi.Btn(Lang.T("匯出全部（CSV）…"), () => Export(true)))));
        Resize += (_, _) => Fit();
        FillCats();
        Reload();
    }

    private void Fit()
    {
        int w = Math.Max(Ui.S(480), Width - Ui.S(110));
        _list.Width = w; _chart.Width = w; _pie.Width = w;
    }

    private void FillCats()
    {
        bool inc = _type.SelectedIndex == 1;
        // 預設分類 + 用家自己用過嘅分類
        var cats = (inc ? IncomeCats : ExpenseCats).Select(Lang.T).ToList();
        foreach (var c in LedgerStore.All.Where(e => e.Income == inc).Select(e => e.Category).Distinct()) if (!cats.Contains(c)) cats.Add(c);
        _cat.Items.Clear();
        foreach (var c in cats) _cat.Items.Add(c);
        _cat.SelectedIndex = 0;
    }

    private void Add()
    {
        string amt = _amount.Text.Trim().Replace(",", "");
        if (!decimal.TryParse(amt, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v) || v <= 0)
        {
            _addStatus.ForeColor = Ui.Danger; _addStatus.Text = Lang.T("請輸入金額（數字）。");
            _amount.Focus();
            return;
        }
        string cat = _cat.Text.Trim();
        if (cat.Length == 0) cat = Lang.T("其他");
        LedgerStore.All.Add(new LedgerStore.Entry { Date = _date.Value.ToString("yyyy-MM-dd"), Income = _type.SelectedIndex == 1, Category = cat, Amount = v, Note = _note.Text.Trim() });
        LedgerStore.Save();
        _amount.Clear(); _note.Clear();
        _addStatus.ForeColor = Ui.Success; _addStatus.Text = Lang.T("已加入");
        _month = new DateTime(_date.Value.Year, _date.Value.Month, 1);
        if (!_cat.Items.Contains(cat)) _cat.Items.Add(cat);
        Reload();
        _amount.Focus();
    }

    private void Move(int d) { _month = _month.AddMonths(d); Reload(); }

    private List<LedgerStore.Entry> MonthEntries() =>
        LedgerStore.All.Where(e => e.Date.StartsWith(_month.ToString("yyyy-MM"))).OrderByDescending(e => e.Date).ToList();

    private void Reload()
    {
        var es = MonthEntries();
        decimal inc = es.Where(e => e.Income).Sum(e => e.Amount), exp = es.Where(e => !e.Income).Sum(e => e.Amount);
        _monthLabel.Text = _month.ToString("yyyy-MM");
        _income.Text = Lang.T("收入") + " " + inc.ToString("#,##0.##");
        _expense.Text = Lang.T("支出") + " " + exp.ToString("#,##0.##");
        _balance.Text = Lang.T("結餘") + " " + (inc - exp).ToString("#,##0.##");
        _balance.ForeColor = inc - exp < 0 ? Ui.Danger : Ui.Text;
        _chart.Items = es.Where(e => !e.Income).GroupBy(e => e.Category).Select(g => (g.Key, g.Sum(e => e.Amount))).OrderByDescending(x => x.Item2).ToList();
        _chart.Height = Math.Max(Ui.S(30), _chart.Items.Count * Ui.S(26) + Ui.S(6));
        _chart.Invalidate();
        _pie.Items = _chart.Items;
        _pie.Visible = _pieView && _pie.Items.Count > 0;
        _chart.Visible = !_pieView || _pie.Items.Count == 0;   // 冇支出：用棒型圖嗰句「呢個月未有支出」
        _pie.Invalidate();
        _viewBtn.Text = _pieView ? Lang.T("顯示棒型圖") : Lang.T("顯示圓形圖");
        _viewBtn.Invalidate();
        _list.Rows.Clear();
        foreach (var e in es)
            _list.Rows.Add(new RowList.Row { Cells = new[] { e.Date, e.Category, e.Note, (e.Income ? "+" : "−") + e.Amount.ToString("#,##0.##") }, Tag = e });
        _list.Reload();
        Fit();
        Relayout();
    }

    private void Export(bool all)
    {
        var es = all ? LedgerStore.All.OrderBy(e => e.Date).ToList() : MonthEntries().OrderBy(e => e.Date).ToList();
        if (es.Count == 0) { _addStatus.ForeColor = Ui.TextDim; _addStatus.Text = Lang.T("冇紀錄可以匯出。"); return; }
        using var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = all ? "ledger.csv" : $"ledger-{_month:yyyy-MM}.csv" };
        if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[] { Lang.T("日期"), Lang.T("類型"), Lang.T("分類"), Lang.T("金額"), Lang.T("備註") }.Select(Q)));
        foreach (var e in es)
            sb.AppendLine(string.Join(",", Q(e.Date), Q(e.Income ? Lang.T("收入") : Lang.T("支出")), Q(e.Category), e.Amount.ToString(CultureInfo.InvariantCulture), Q(e.Note)));
        try { File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(true)); _addStatus.ForeColor = Ui.Success; _addStatus.Text = Lang.T("已匯出"); }
        catch (Exception ex) { _addStatus.ForeColor = Ui.Danger; _addStatus.Text = ex.Message; }
    }

    /// <summary>支出分類圓形圖（環形）+ 圖例。</summary>
    private sealed class PieChart : Control
    {
        public List<(string Name, decimal Value)> Items = new();
        // 一組分得開嘅顏色；多過呢個數就循環用
        private static readonly Color[] Palette =
        {
            Color.FromArgb(0x4F, 0x8E, 0xF7), Color.FromArgb(0xF2, 0x8B, 0x3C), Color.FromArgb(0x3D, 0xB8, 0x7A), Color.FromArgb(0xE2, 0x5C, 0x6A), Color.FromArgb(0x9B, 0x6C, 0xE0),
            Color.FromArgb(0x2F, 0xB5, 0xC4), Color.FromArgb(0xE0, 0xB8, 0x30), Color.FromArgb(0xD9, 0x6B, 0xB8), Color.FromArgb(0x8A, 0x94, 0xA6), Color.FromArgb(0x6F, 0xA8, 0x3A)
        };
        public PieChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Card;
            Height = Ui.S(220);
            Margin = new Padding(0, 0, 0, Ui.S(10));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Card);
            if (Items.Count == 0) return;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            decimal total = Items.Sum(i => i.Value);
            if (total <= 0) return;
            int d = Height - Ui.S(16), x = Ui.S(8), y = Ui.S(8);
            var rect = new Rectangle(x, y, d, d);
            float start = -90;
            for (int i = 0; i < Items.Count; i++)
            {
                float sweep = (float)(Items[i].Value / total * 360m);
                using var b = new SolidBrush(Palette[i % Palette.Length]);
                if (Items.Count == 1) g.FillEllipse(b, rect); else g.FillPie(b, rect, start, sweep);
                start += sweep;
            }
            // 中間挖空，寫總數
            int hole = d * 52 / 100;
            using (var hb = new SolidBrush(Ui.Card)) g.FillEllipse(hb, x + (d - hole) / 2, y + (d - hole) / 2, hole, hole);
            var bold = Ui.F(11f, FontStyle.Bold);
            var font = Ui.F(9.25f);
            TextRenderer.DrawText(g, total.ToString("#,##0.##"), bold, new Rectangle(x, y, d, d), Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            // 圖例：色塊、分類、百分比
            int lx = x + d + Ui.S(28), ly = y + Ui.S(4), rowH = Ui.S(22), perCol = Math.Max(1, (Height - Ui.S(16)) / rowH), colW = Ui.S(230);
            for (int i = 0; i < Items.Count; i++)
            {
                int cx = lx + i / perCol * colW, cy = ly + i % perCol * rowH;
                if (cx + colW > Width) break;
                using var b = new SolidBrush(Palette[i % Palette.Length]);
                g.FillRectangle(b, cx, cy + Ui.S(5), Ui.S(12), Ui.S(12));
                TextRenderer.DrawText(g, $"{Items[i].Name}　{Items[i].Value / total * 100:0.#}%", font, new Rectangle(cx + Ui.S(18), cy, colW - Ui.S(22), rowH), Ui.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>每類支出一條橫棒。</summary>
    private sealed class BarChart : Control
    {
        public List<(string Name, decimal Value)> Items = new();
        public BarChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Card;
            Margin = new Padding(0, 0, 0, Ui.S(4));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Card);
            var font = Ui.F(9.25f);
            if (Items.Count == 0) { TextRenderer.DrawText(g, Lang.T("呢個月未有支出。"), font, new Rectangle(0, 0, Width, Height), Ui.TextFaint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter); return; }
            decimal max = Items.Max(i => i.Value), total = Items.Sum(i => i.Value);
            int labelW = Ui.S(110), valW = Ui.S(150), rowH = Ui.S(26), barW = Math.Max(Ui.S(40), Width - labelW - valW);
            for (int i = 0; i < Items.Count; i++)
            {
                int y = i * rowH + Ui.S(3);
                TextRenderer.DrawText(g, Items[i].Name, font, new Rectangle(0, y, labelW - Ui.S(8), rowH - Ui.S(6)), Ui.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                using (var b = new SolidBrush(Ui.Input)) g.FillRectangle(b, labelW, y + Ui.S(4), barW, rowH - Ui.S(14));
                int w = max > 0 ? (int)(barW * (double)(Items[i].Value / max)) : 0;
                using (var b = new SolidBrush(Ui.Accent)) g.FillRectangle(b, labelW, y + Ui.S(4), Math.Max(2, w), rowH - Ui.S(14));
                string val = Items[i].Value.ToString("#,##0.##") + (total > 0 ? $"　{Items[i].Value / total * 100:0}%" : "");
                TextRenderer.DrawText(g, val, font, new Rectangle(labelW + barW + Ui.S(8), y, valW - Ui.S(8), rowH - Ui.S(6)), Ui.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
