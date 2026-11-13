using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using MultiTool.UI;

namespace MultiTool.Tools;

/// <summary>
/// 匯率：唔使 AI、唔使金鑰。主要用 fawazahmed0/exchange-api（CC0 公眾領域授權、冇用量限制，經 jsDelivr 同 Cloudflare Pages 派發），
/// 兩個網址都攞唔到就用歐洲央行每日公佈嘅參考匯率。攞到嘅匯率存喺 data\rates.json，冇網都用到上次嗰份。
/// </summary>
internal static class Rates
{
    // 常用貨幣（排先嘅會喺選單頂）
    public static readonly string[] Codes =
    {
        "HKD", "USD", "CNY", "TWD", "JPY", "CAD", "EUR", "GBP", "KRW", "AUD", "SGD", "MOP", "THB", "MYR", "NZD", "CHF", "PHP", "IDR", "VND", "INR",
        "SEK", "NOK", "DKK", "PLN", "CZK", "HUF", "RON", "TRY", "RUB", "UAH", "BRL", "MXN", "ARS", "CLP", "COP", "ZAR", "AED", "SAR", "ILS", "EGP"
    };

    private static string CacheFile => Path.Combine(AppPaths.Data, "rates.json");
    /// <summary>1 美元 = 幾多（代碼大寫）。</summary>
    public static Dictionary<string, double> PerUsd = new();
    public static string Date = "", Source = "";
    public static DateTime Fetched;

    public static void LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;
            var o = JsonNode.Parse(File.ReadAllText(CacheFile))!;
            Date = (string?)o["date"] ?? ""; Source = (string?)o["source"] ?? "";
            Fetched = DateTime.TryParse((string?)o["fetched"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var f) ? f : default;
            PerUsd = o["rates"]!.AsObject().ToDictionary(kv => kv.Key, kv => (double)kv.Value!);
        }
        catch { }
    }

    private static void SaveCache()
    {
        try
        {
            var r = new JsonObject();
            foreach (var kv in PerUsd) r[kv.Key] = kv.Value;
            File.WriteAllText(CacheFile, new JsonObject { ["date"] = Date, ["source"] = Source, ["fetched"] = DateTime.UtcNow.ToString("o"), ["rates"] = r }.ToJsonString());
        }
        catch { }
    }

    public static async Task UpdateAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Program.FileName + "/" + Program.Version.TrimStart('v'));
        Exception? last = null;
        foreach (string url in new[] { "https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@latest/v1/currencies/usd.min.json", "https://latest.currency-api.pages.dev/v1/currencies/usd.min.json" })
            try
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
                var map = new Dictionary<string, double>();
                foreach (var p in doc.RootElement.GetProperty("usd").EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.GetDouble() > 0) map[p.Name.ToUpperInvariant()] = p.Value.GetDouble();
                if (map.Count < 20) throw new InvalidDataException("rates");
                map["USD"] = 1;
                PerUsd = map; Date = doc.RootElement.GetProperty("date").GetString() ?? ""; Source = "fawazahmed0/exchange-api"; Fetched = DateTime.UtcNow;
                SaveCache();
                return;
            }
            catch (Exception ex) { last = ex; }
        try
        {
            // 後備：歐洲央行（以歐元做基準，約 30 種貨幣）
            var x = XDocument.Parse(await http.GetStringAsync("https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml"));
            var cubes = x.Descendants().Where(e => e.Name.LocalName == "Cube" && e.Attribute("currency") != null).ToList();
            var perEur = cubes.ToDictionary(e => e.Attribute("currency")!.Value, e => double.Parse(e.Attribute("rate")!.Value, CultureInfo.InvariantCulture));
            perEur["EUR"] = 1;
            double usd = perEur["USD"];
            PerUsd = perEur.ToDictionary(kv => kv.Key, kv => kv.Value / usd);
            Date = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "Cube" && e.Attribute("time") != null)?.Attribute("time")?.Value ?? "";
            Source = "European Central Bank"; Fetched = DateTime.UtcNow;
            SaveCache();
        }
        catch (Exception ex) { throw last ?? ex; }
    }

    public static double? Convert(double amount, string from, string to) =>
        PerUsd.TryGetValue(from, out double f) && PerUsd.TryGetValue(to, out double t) && f > 0 ? amount / f * t : null;
}

/// <summary>單位同匯率換算。單位全部離線計；匯率每日更新一次（免費公開數據，唔使 AI）。</summary>
internal sealed class UnitPage : ToolPage
{
    private sealed record Unit(string Name, double Factor, double Offset = 0);   // 基本單位 = 值 × Factor + Offset
    private static readonly (string Cat, Unit[] Units)[] Table =
    {
        ("長度", new Unit[] { new("m", 1), new("km", 1000), new("cm", 0.01), new("mm", 0.001), new("mi", 1609.344), new("yd", 0.9144), new("ft", 0.3048), new("in", 0.0254), new("nmi", 1852) }),
        ("重量", new Unit[] { new("kg", 1), new("g", 0.001), new("mg", 1e-6), new("t", 1000), new("lb", 0.45359237), new("oz", 0.028349523125), new("斤 (HK)", 0.60478982), new("兩 (HK)", 0.0377993638), new("斤 (TW)", 0.6), new("斤 (CN)", 0.5) }),
        ("溫度", new Unit[] { new("°C", 1), new("°F", 5.0 / 9, -160.0 / 9), new("K", 1, -273.15) }),
        ("面積", new Unit[] { new("m²", 1), new("km²", 1e6), new("cm²", 1e-4), new("ha", 1e4), new("ft²", 0.09290304), new("in²", 0.00064516), new("acre", 4046.8564224), new("坪", 3.305785) }),
        ("容量", new Unit[] { new("L", 1), new("mL", 0.001), new("m³", 1000), new("gal (US)", 3.785411784), new("gal (UK)", 4.54609), new("fl oz (US)", 0.0295735295625), new("cup (US)", 0.2365882365), new("tbsp", 0.01478676478), new("tsp", 0.00492892159) }),
        ("速度", new Unit[] { new("km/h", 1), new("m/s", 3.6), new("mph", 1.609344), new("kn", 1.852) }),
        ("時間", new Unit[] { new("s", 1), new("min", 60), new("h", 3600), new("day", 86400), new("week", 604800) }),
        ("資料大細", new Unit[] { new("B", 1), new("KB", 1024), new("MB", 1048576), new("GB", 1073741824), new("TB", 1099511627776), new("bit", 0.125), new("Mbit", 125000) }),
    };

    private readonly ComboBox _cat, _from, _to, _cFrom, _cTo;
    private readonly TextBox _value, _amount, _all;
    private readonly Label _result, _cResult, _cInfo;
    private static string Settings_ => Path.Combine(AppPaths.Data, "units.txt");

    public static Control Build() => new UnitPage();

    private static bool TryNum(string s, out double v) =>
        double.TryParse(s.Trim().Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    private static string Fmt(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "—";
        double a = Math.Abs(v);
        if (a != 0 && (a >= 1e15 || a < 1e-6)) return v.ToString("0.######E+0", CultureInfo.InvariantCulture);
        return v.ToString(a >= 100 ? "#,##0.##" : a >= 1 ? "#,##0.####" : "0.########", CultureInfo.InvariantCulture);
    }

    private UnitPage()
    {
        _cat = TIn.Combo(150, Table.Select(t => Lang.T(t.Cat)));
        _value = TIn.Box("1", 150);
        _from = TIn.Combo(130, Array.Empty<string>());
        _to = TIn.Combo(130, Array.Empty<string>());
        _result = new Label { AutoSize = true, ForeColor = Ui.Text, Font = Ui.F(15f, System.Drawing.FontStyle.Bold), Margin = new Padding(0, Ui.S(2), 0, Ui.S(8)) };
        _all = TIn.Box("", 640);
        _all.Multiline = true; _all.ReadOnly = true; _all.Height = Ui.S(150); _all.ScrollBars = ScrollBars.Vertical; _all.BorderStyle = BorderStyle.None;
        var swap = TUi.Btn("⇄", () => { int a = _from.SelectedIndex; _from.SelectedIndex = _to.SelectedIndex; _to.SelectedIndex = a; });
        Card(Lang.T("單位換算"), Lang.T("全部喺你部機計，唔使上網。"),
            TUi.Col(TUi.Row(_cat, _value, _from, swap, _to), _result, _all));
        _cat.SelectedIndexChanged += (_, _) => FillUnits();
        _value.TextChanged += (_, _) => Calc();
        _from.SelectedIndexChanged += (_, _) => Calc();
        _to.SelectedIndexChanged += (_, _) => Calc();

        Rates.LoadCache();
        string cf = "USD", ct = "HKD";
        try { if (File.Exists(Settings_)) { var p = File.ReadAllText(Settings_).Trim().Split(','); if (p.Length == 2) { cf = p[0]; ct = p[1]; } } } catch { }
        _amount = TIn.Box("100", 150);
        _cFrom = TIn.Combo(100, Rates.Codes, Math.Max(0, Array.IndexOf(Rates.Codes, cf)));
        _cTo = TIn.Combo(100, Rates.Codes, Math.Max(0, Array.IndexOf(Rates.Codes, ct)));
        _cResult = new Label { AutoSize = true, ForeColor = Ui.Text, Font = Ui.F(15f, System.Drawing.FontStyle.Bold), Margin = new Padding(0, Ui.S(2), 0, Ui.S(8)) };
        _cInfo = TUi.L("");
        var cSwap = TUi.Btn("⇄", () => { int a = _cFrom.SelectedIndex; _cFrom.SelectedIndex = _cTo.SelectedIndex; _cTo.SelectedIndex = a; });
        Card(Lang.T("匯率換算"), Lang.T("用免費公開嘅每日匯率（唔使 AI、唔使帳戶），只供參考，唔係銀行或者找換店嘅實際價。上次攞到嘅匯率會記低，冇網都用得。"),
            TUi.Col(TUi.Row(_amount, _cFrom, cSwap, _cTo, TUi.Btn(Lang.T("↻ 更新匯率"), () => _ = UpdateAsync(true))), _cResult, _cInfo));
        _amount.TextChanged += (_, _) => CalcRate();
        _cFrom.SelectedIndexChanged += (_, _) => { CalcRate(); SavePair(); };
        _cTo.SelectedIndexChanged += (_, _) => { CalcRate(); SavePair(); };

        FillUnits();
        CalcRate();
        // 未有匯率，或者上次係 12 個鐘之前攞：自動更新一次
        if (Rates.PerUsd.Count == 0 || (DateTime.UtcNow - Rates.Fetched).TotalHours > 12) _ = UpdateAsync(false);
    }

    private void SavePair() { try { File.WriteAllText(Settings_, _cFrom.Text + "," + _cTo.Text); } catch { } }

    private void FillUnits()
    {
        var units = Table[Math.Max(0, _cat.SelectedIndex)].Units;
        _from.Items.Clear(); _to.Items.Clear();
        foreach (var u in units) { _from.Items.Add(u.Name); _to.Items.Add(u.Name); }
        _from.SelectedIndex = 0;
        _to.SelectedIndex = Math.Min(1, units.Length - 1);
        Calc();
    }

    private void Calc()
    {
        var units = Table[Math.Max(0, _cat.SelectedIndex)].Units;
        if (_from.SelectedIndex < 0 || _to.SelectedIndex < 0 || !TryNum(_value.Text, out double v)) { _result.Text = ""; _all.Text = ""; return; }
        var f = units[_from.SelectedIndex];
        double baseVal = v * f.Factor + f.Offset;
        double To(Unit u) => (baseVal - u.Offset) / u.Factor;
        var t = units[_to.SelectedIndex];
        _result.Text = $"{Fmt(v)} {f.Name} = {Fmt(To(t))} {t.Name}";
        _all.Text = string.Join("\r\n", units.Where(u => u != f).Select(u => $"{Fmt(To(u))} {u.Name}"));
    }

    private void CalcRate()
    {
        if (Rates.PerUsd.Count == 0) { _cResult.Text = ""; _cInfo.Text = Lang.T("未有匯率：撳「↻ 更新匯率」（要上網）。"); return; }
        string from = _cFrom.Text, to = _cTo.Text;
        if (!TryNum(_amount.Text, out double v)) { _cResult.Text = ""; return; }
        var r = Rates.Convert(v, from, to);
        _cResult.Text = r == null ? Lang.F("呢個來源冇 {0} 或者 {1} 嘅匯率。", from, to) : $"{Fmt(v)} {from} = {Fmt(Math.Round(r.Value, 4))} {to}";
        var one = Rates.Convert(1, from, to);
        _cInfo.ForeColor = Ui.TextDim;
        _cInfo.Text = (one != null ? $"1 {from} = {Fmt(Math.Round(one.Value, 6))} {to}　" : "") + Lang.F("匯率日期：{0}　來源：{1}", Rates.Date, Rates.Source);
    }

    private async Task UpdateAsync(bool manual)
    {
        _cInfo.ForeColor = Ui.TextDim;
        _cInfo.Text = Lang.T("更新緊匯率…");
        try { await Rates.UpdateAsync(); if (!IsDisposed) CalcRate(); }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            AppPaths.LogError("匯率", ex);
            CalcRate();
            if (manual || Rates.PerUsd.Count == 0) { _cInfo.ForeColor = Ui.Danger; _cInfo.Text = Lang.T("更新唔到匯率，請檢查網絡。") + (Rates.PerUsd.Count > 0 ? "　" + Lang.F("繼續用 {0} 嘅匯率。", Rates.Date) : ""); }
        }
    }
}
