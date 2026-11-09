using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTool.Tools;

// =====================================================================
//  股票：自選股、走勢圖、到價提醒
//  • 數據來自 Yahoo Finance 公開接口（唔使註冊；報價可能延遲大約 15 分鐘）
//  • 美股 AAPL、港股 0700.HK、加股 SHOP.TO、加密貨幣 BTC-USD
//  • 自選股同提醒存喺 data\stocks\watch.json
// =====================================================================

/// <summary>一隻自選股（連到價提醒）。</summary>
internal sealed class StockItem
{
    public string Symbol { get; set; } = "";
    public string Name { get; set; } = "";
    public double? Above { get; set; }   // 升到或者高過呢個價就提醒（提醒完自動清走）
    public double? Below { get; set; }   // 跌到或者低過呢個價就提醒
    public double? PctAlert { get; set; } // 單日升跌超過呢個 % 就提醒（每日最多一次）
    public string PctFired { get; set; } = ""; // 今日已經提醒過（日期）
    public bool Starred { get; set; }    // ★ 標記：想特別留意嘅，桌面小工具會顯示
    public double? Cost { get; set; }    // 我嘅買入價（每股平均成本）
    public double? Shares { get; set; }  // 持有股數（可以留空，留空就只計 %）
    public bool UserAdded { get; set; }  // 用家自己加入（唔係第一次用時自動加入嘅預設股票）

    /// <summary>預設股票：自動加入、用家冇加過、冇打星、冇買入價、冇提醒。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPreset => !UserAdded && !Starred && Cost == null && !HasAlert && StockStore.IsDefault(Symbol);

    /// <summary>賺蝕：(每股差價, %, 總金額或者 null)。冇買入價就 null。</summary>
    public (double PerShare, double Pct, double? Total)? Pnl(double price) =>
        Cost is { } c && c > 0 && !double.IsNaN(price) ? (price - c, (price - c) / c * 100, Shares is { } s ? (price - c) * s : null) : null;

    [System.Text.Json.Serialization.JsonIgnore] public bool HasAlert => Above != null || Below != null || PctAlert != null;
}

/// <summary>即時報價（其實係最新一筆，可能有延遲）。</summary>
internal sealed record StockQuote(string Symbol, string Name, string Currency, double Price, double PrevClose, DateTime Time, double[] Spark)
{
    public double Change => Price - PrevClose;
    public double Pct => PrevClose != 0 ? Change / PrevClose * 100 : 0;
}

/// <summary>一條 K 線。</summary>
internal readonly record struct Candle(DateTime Time, double Open, double High, double Low, double Close, double Volume);

internal sealed record StockChartData(string Symbol, string Currency, double PrevClose, bool Intraday, List<Candle> Candles, int GmtOffset = 0, int IntervalMin = 0);

internal sealed record StockSearchHit(string Symbol, string Name, string Exchange, string Type);

internal static class StockStore
{
    private static string Dir => Path.Combine(AppPaths.Data, "stocks");
    private static string FilePath => Path.Combine(Dir, "watch.json");
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static List<StockItem> Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<StockItem>>(File.ReadAllText(FilePath)) ?? new();
            // 第一次用：預設幾隻熱門股（用家之後可以自己移除）
            return Defaults.Select(d => new StockItem { Symbol = d.Symbol, Name = d.Name }).ToList();
        }
        catch (Exception ex) { AppPaths.LogError("讀自選股", ex); return new(); }
    }

    public static bool IsDefault(string sym) => Defaults.Any(d => d.Symbol.Equals(sym, StringComparison.OrdinalIgnoreCase));

    private static readonly (string Symbol, string Name)[] Defaults =
    {
        ("AAPL", "Apple"), ("NVDA", "NVIDIA"), ("MSFT", "Microsoft"), ("TSLA", "Tesla"),
        ("0700.HK", "Tencent"), ("9988.HK", "Alibaba"), ("SHOP.TO", "Shopify"), ("RY.TO", "Royal Bank of Canada"),
        ("BTC-USD", "Bitcoin"), ("ETH-USD", "Ethereum"),
    };

    /// <summary>熱門股票（「🔥 熱門」選單用）：分類 → 代號、名。</summary>
    public static readonly (string Category, (string Symbol, string Name)[] Items)[] Popular =
    {
        ("指數", new[] { ("^GSPC", "S&P 500"), ("^IXIC", "Nasdaq"), ("^DJI", "Dow Jones"), ("^HSI", "Hang Seng Index"), ("^GSPTSE", "S&P/TSX Composite") }),
        ("美股", new[] { ("AAPL", "Apple"), ("MSFT", "Microsoft"), ("NVDA", "NVIDIA"), ("AMZN", "Amazon"), ("GOOGL", "Alphabet (Google)"), ("META", "Meta"),
                        ("TSLA", "Tesla"), ("AMD", "AMD"), ("NFLX", "Netflix"), ("SPY", "SPDR S&P 500 ETF"), ("QQQ", "Invesco QQQ (Nasdaq 100)") }),
        ("港股", new[] { ("0700.HK", "Tencent"), ("9988.HK", "Alibaba"), ("3690.HK", "Meituan"), ("1810.HK", "Xiaomi"), ("0005.HK", "HSBC"),
                        ("1299.HK", "AIA"), ("0388.HK", "HKEX"), ("0941.HK", "China Mobile"), ("9618.HK", "JD.com"), ("2800.HK", "Tracker Fund of Hong Kong") }),
        ("加股", new[] { ("SHOP.TO", "Shopify"), ("RY.TO", "Royal Bank of Canada"), ("TD.TO", "TD Bank"), ("BMO.TO", "Bank of Montreal"), ("BNS.TO", "Scotiabank"),
                        ("ENB.TO", "Enbridge"), ("CNR.TO", "CN Rail"), ("SU.TO", "Suncor"), ("CSU.TO", "Constellation Software"), ("XIU.TO", "iShares S&P/TSX 60 ETF") }),
        ("加密貨幣", new[] { ("BTC-USD", "Bitcoin"), ("ETH-USD", "Ethereum"), ("SOL-USD", "Solana"), ("XRP-USD", "XRP"), ("BNB-USD", "BNB"), ("DOGE-USD", "Dogecoin") }),
    };

    public static void Save(List<StockItem> list)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Opt)); }
        catch (Exception ex) { AppPaths.LogError("存自選股", ex); }
    }
}

/// <summary>Yahoo Finance 公開接口。</summary>
internal static class Yahoo
{
    private static readonly HttpClient Http = MakeHttp();

    private static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // 冇 User-Agent 會被拒
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return h;
    }

    /// <summary>走勢圖範圍：標籤、Yahoo range、interval、係咪即日。</summary>
    public static readonly (string Label, string Range, string Interval, bool Intraday)[] Ranges =
    {
        ("1日", "1d", "5m", true),
        ("5日", "5d", "15m", true),
        ("1個月", "1mo", "1d", false),
        ("6個月", "6mo", "1d", false),
        ("1年", "1y", "1d", false),
        ("5年", "5y", "1wk", false),
    };

    private static async Task<JsonNode> GetAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? j = null;
        try { j = JsonNode.Parse(body); } catch { }
        if (j == null) throw new Exception(Lang.F("報價伺服器回應唔正常（HTTP {0}）", (int)resp.StatusCode));
        return j;
    }

    private static string Url(string sym, string range, string interval) =>
        "https://query1.finance.yahoo.com/v8/finance/chart/" + Uri.EscapeDataString(sym) + "?range=" + range + "&interval=" + interval + "&includePrePost=false";

    private static JsonNode Result(JsonNode j, string sym)
    {
        var err = j["chart"]?["error"];
        if (err != null && err.GetValueKind() != JsonValueKind.Null)
        {
            string code = (string?)err["code"] ?? "";
            throw new Exception(code == "Not Found" ? Lang.F("搵唔到股票代號「{0}」", sym) : Lang.F("攞唔到「{0}」嘅報價：{1}", sym, (string?)err["description"] ?? code));
        }
        return j["chart"]?["result"]?[0] ?? throw new Exception(Lang.F("搵唔到股票代號「{0}」", sym));
    }

    private static double D(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN;

    /// <summary>最新報價 + 即日走勢（畫細圖用）。</summary>
    public static async Task<StockQuote> QuoteAsync(string sym, CancellationToken ct = default)
    {
        var r = Result(await GetAsync(Url(sym, "1d", "5m"), ct), sym);
        var m = r["meta"]!;
        double price = D(m["regularMarketPrice"]);
        double prev = D(m["chartPreviousClose"]);
        if (double.IsNaN(prev)) prev = D(m["previousClose"]);
        var closes = r["indicators"]?["quote"]?[0]?["close"]?.AsArray().Select(D).Where(x => !double.IsNaN(x)).ToArray() ?? Array.Empty<double>();
        if (double.IsNaN(price) && closes.Length > 0) price = closes[^1];
        long t = m["regularMarketTime"] is JsonValue tv && tv.TryGetValue<long>(out var tl) ? tl : 0;
        string name = (string?)m["shortName"] ?? (string?)m["longName"] ?? sym;
        return new StockQuote((string?)m["symbol"] ?? sym, name, (string?)m["currency"] ?? "", price, double.IsNaN(prev) ? price : prev,
            t > 0 ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime : DateTime.Now, closes);
    }

    /// <summary>走勢圖數據（時間轉做交易所當地時間）。</summary>
    public static async Task<StockChartData> ChartAsync(string sym, int rangeIndex, CancellationToken ct = default)
    {
        var (_, range, interval, intraday) = Ranges[rangeIndex];
        var r = Result(await GetAsync(Url(sym, range, interval), ct), sym);
        var m = r["meta"]!;
        int gmt = m["gmtoffset"] is JsonValue gv && gv.TryGetValue<int>(out var gi) ? gi : 0;
        var ts = r["timestamp"]?.AsArray();
        var q = r["indicators"]?["quote"]?[0];
        var list = new List<Candle>();
        if (ts != null && q != null)
        {
            var o = q["open"]?.AsArray(); var h = q["high"]?.AsArray(); var l = q["low"]?.AsArray(); var c = q["close"]?.AsArray(); var v = q["volume"]?.AsArray();
            for (int i = 0; i < ts.Count; i++)
            {
                double cl = D(c?[i]);
                if (double.IsNaN(cl)) continue; // 冇成交嘅格
                double op = D(o?[i]), hi = D(h?[i]), lo = D(l?[i]), vo = D(v?[i]);
                long sec = ts[i] is JsonValue sv && sv.TryGetValue<long>(out var s) ? s : 0;
                var time = DateTimeOffset.FromUnixTimeSeconds(sec + gmt).UtcDateTime;
                list.Add(new Candle(time, double.IsNaN(op) ? cl : op, double.IsNaN(hi) ? cl : hi, double.IsNaN(lo) ? cl : lo, cl, double.IsNaN(vo) ? 0 : vo));
            }
        }
        double prev = D(m["chartPreviousClose"]);
        if (double.IsNaN(prev)) prev = D(m["previousClose"]);
        int ivMin = interval.EndsWith("m") && int.TryParse(interval[..^1], out var iv) ? iv : interval == "1d" ? 1440 : interval == "1wk" ? 10080 : 0;
        return new StockChartData((string?)m["symbol"] ?? sym, (string?)m["currency"] ?? "", double.IsNaN(prev) ? 0 : prev, intraday, list, gmt, ivMin);
    }

    /// <summary>用名或者代號搵股票。</summary>
    public static async Task<List<StockSearchHit>> SearchAsync(string q, CancellationToken ct = default)
    {
        var j = await GetAsync("https://query2.finance.yahoo.com/v1/finance/search?q=" + Uri.EscapeDataString(q) + "&quotesCount=10&newsCount=0&listsCount=0", ct);
        var res = new List<StockSearchHit>();
        foreach (var x in j["quotes"]?.AsArray() ?? new JsonArray())
        {
            if (x == null || (string?)x["symbol"] is not { Length: > 0 } sym) continue;
            string type = (string?)x["quoteType"] ?? "";
            if (type is "OPTION" or "FUTURE") continue;
            res.Add(new StockSearchHit(sym, (string?)x["shortname"] ?? (string?)x["longname"] ?? sym, (string?)x["exchDisp"] ?? "", (string?)x["typeDisp"] ?? type));
        }
        return res;
    }
}

/// <summary>背景定時攞自選股報價，順便檢查到價提醒（程式開住就會提醒，唔使開住股票頁）。</summary>
internal static class StockWatch
{
    public static readonly List<StockItem> Items = StockStore.Load();
    public static readonly Dictionary<string, StockQuote> Quotes = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, string> Errors = new(StringComparer.OrdinalIgnoreCase);
    public static DateTime LastUpdate { get; private set; }

    /// <summary>報價更新咗（喺背景 thread 觸發）。</summary>
    public static event Action? Updated;

    private static System.Threading.Timer? _timer;
    private static int _running;

    /// <summary>即時推送：某隻股票有新價（背景 thread）。</summary>
    public static event Action<StockTick>? Ticked;

    private static System.Threading.Timer? _flush;
    private static int _pending;

    public static void Start()
    {
        if (_timer != null) return;
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
        // 即時推送：有新價就更新；畫面每秒最多刷新一次
        YahooStream.Tick += OnTick;
        SyncStream();
        YahooStream.Start();
        _flush = new System.Threading.Timer(_ => { if (Interlocked.Exchange(ref _pending, 0) == 1) Updated?.Invoke(); }, null, 1000, 1000);
    }

    public static void Save()
    {
        lock (Items) StockStore.Save(Items);
        SyncStream();
    }

    private static void SyncStream()
    {
        List<string> syms;
        lock (Items) syms = Items.Select(x => x.Symbol).ToList();
        YahooStream.SetSymbols(syms);
    }

    private static void OnTick(StockTick t)
    {
        if (!Extensions.IsInstalled(Extensions.Stocks)) return;
        StockItem? it;
        lock (Items) it = Items.FirstOrDefault(x => x.Symbol.Equals(t.Symbol, StringComparison.OrdinalIgnoreCase));
        if (it == null) return;
        lock (Quotes)
        {
            Quotes.TryGetValue(it.Symbol, out var old);
            double prev = t.PrevClose > 0 ? t.PrevClose : !double.IsNaN(t.Change) ? t.Price - t.Change : old?.PrevClose ?? t.Price;
            Quotes[it.Symbol] = old != null
                ? old with { Price = t.Price, PrevClose = prev, Time = t.TimeUtc.ToLocalTime() }
                : new StockQuote(it.Symbol, t.ShortName.Length > 0 ? t.ShortName : it.Name, t.Currency, t.Price, prev, t.TimeUtc.ToLocalTime(), Array.Empty<double>());
            Errors.Remove(it.Symbol);
        }
        LastUpdate = DateTime.Now;
        CheckAlerts(new List<StockItem> { it });
        Ticked?.Invoke(t);
        Interlocked.Exchange(ref _pending, 1);
    }

    public static async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            if (!Extensions.IsInstalled(Extensions.Stocks)) return;
            List<StockItem> items;
            lock (Items) items = Items.ToList();
            if (items.Count == 0) return;
            // 幾隻一齊攞，唔好太多同時
            using var gate = new SemaphoreSlim(4);
            var tasks = items.Select(async it =>
            {
                await gate.WaitAsync();
                try
                {
                    var q = await Yahoo.QuoteAsync(it.Symbol);
                    lock (Quotes) { Quotes[it.Symbol] = q; Errors.Remove(it.Symbol); }
                }
                catch (Exception ex) { lock (Quotes) Errors[it.Symbol] = ex.Message; }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
            LastUpdate = DateTime.Now;
            CheckAlerts(items);
            Updated?.Invoke();
        }
        catch (Exception ex) { AppPaths.LogError("股票報價", ex); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static void CheckAlerts(List<StockItem> items)
    {
        bool changed = false;
        foreach (var it in items)
        {
            StockQuote? q;
            lock (Quotes) Quotes.TryGetValue(it.Symbol, out q);
            if (q == null || double.IsNaN(q.Price)) continue;
            string name = it.Name.Length > 0 ? it.Name : it.Symbol;
            if (it.Above is { } up && q.Price >= up)
            {
                Scripts.Notifier.Send(Lang.F("📈 {0} 升到 {1}", it.Symbol, Fmt(q.Price)), Lang.F("{0} 而家 {1} {2}（提醒價：{3} 或以上）", name, Fmt(q.Price), q.Currency, Fmt(up)), true, false);
                it.Above = null; // 提醒一次就清走
                changed = true;
            }
            if (it.Below is { } dn && q.Price <= dn)
            {
                Scripts.Notifier.Send(Lang.F("📉 {0} 跌到 {1}", it.Symbol, Fmt(q.Price)), Lang.F("{0} 而家 {1} {2}（提醒價：{3} 或以下）", name, Fmt(q.Price), q.Currency, Fmt(dn)), true, false);
                it.Below = null;
                changed = true;
            }
        }
        // 單日升跌 %：每隻每日最多提醒一次
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        foreach (var it in items)
        {
            if (it.PctAlert is not { } pa || it.PctFired == today) continue;
            StockQuote? q;
            lock (Quotes) Quotes.TryGetValue(it.Symbol, out q);
            if (q == null || double.IsNaN(q.Pct) || Math.Abs(q.Pct) < pa) continue;
            string name = it.Name.Length > 0 ? it.Name : it.Symbol;
            string sign = q.Pct >= 0 ? "+" : "";
            Scripts.Notifier.Send(Lang.F("{0} {1} 今日 {2}{3}%", q.Pct >= 0 ? "📈" : "📉", it.Symbol, sign, q.Pct.ToString("0.00")),
                Lang.F("{0} 而家 {1} {2}（提醒：單日升跌超過 {3}%）", name, Fmt(q.Price), q.Currency, pa), true, false);
            it.PctFired = today;
            changed = true;
        }
        if (changed) Save();
    }

    /// <summary>價錢格式：細價多啲小數位。</summary>
    public static string Fmt(double v) =>
        double.IsNaN(v) ? "–" : Math.Abs(v) < 1 ? v.ToString("0.0000") : Math.Abs(v) < 10 ? v.ToString("0.000") : v.ToString("#,##0.00");

    public static string Vol(double v) =>
        v >= 1e9 ? (v / 1e9).ToString("0.##") + "B" : v >= 1e6 ? (v / 1e6).ToString("0.##") + "M" : v >= 1e3 ? (v / 1e3).ToString("0.#") + "K" : v.ToString("0");
}
