using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Ai;

/// <summary>
/// 本地 AI「上網搵資料」：本地模型自己唔識上網，所以由程式幫佢搵——
/// ① 叫模型揀邊啲來源同搜尋字眼 → ② 程式去問嗰啲公開 API → ③ 將結果貼入問題俾模型睇住答。
/// 內置來源全部係官方公開、唔使金鑰嘅 API；另外可以填自己嘅搜尋 API 金鑰（Brave Search / Tavily）或者 SearXNG 網址做全網搜尋。
/// </summary>
internal static class WebSearch
{
    public sealed record Hit(string Title, string Url, string Text);

    /// <summary>Key = 模型用嚟揀嘅名；Hint = 講俾模型聽呢個來源啱搵乜、搜尋字眼用咩語言。</summary>
    public sealed record Source(string Key, string Name, string Hint, Func<string, CancellationToken, Task<List<Hit>>> Run);

    // ---------------- 設定（data\ai\websearch.json；金鑰用 Windows DPAPI 加密）----------------
    private static string SettingFile => Path.Combine(AiProvider.WorkDir, "websearch.json");
    private static bool _loaded, _enabled;
    private static string _provider = "", _secret = "";

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(SettingFile)) return;
            var o = JsonNode.Parse(File.ReadAllText(SettingFile));
            _enabled = (bool?)o?["enabled"] ?? false;
            _provider = (string?)o?["provider"] ?? "";
            _secret = Mail.MailStore.Unprotect((string?)o?["secret"] ?? "");
        }
        catch (Exception ex) { AppPaths.LogError("上網搵資料設定", ex); }
    }

    public static void Save(bool enabled, string provider, string secret)
    {
        _loaded = true; _enabled = enabled; _provider = provider; _secret = secret.Trim();
        try
        {
            Directory.CreateDirectory(AiProvider.WorkDir);
            File.WriteAllText(SettingFile, new JsonObject { ["enabled"] = enabled, ["provider"] = provider, ["secret"] = Mail.MailStore.Protect(_secret) }.ToJsonString());
        }
        catch (Exception ex) { AppPaths.LogError("上網搵資料設定", ex); }
    }

    public static bool Enabled { get { Load(); return _enabled; } }
    /// <summary>自訂搜尋："" = 唔用、brave、tavily、searxng。</summary>
    public static string Provider { get { Load(); return _provider; } }
    /// <summary>金鑰（Brave / Tavily）或者網址（SearXNG）。</summary>
    public static string Secret { get { Load(); return _secret; } }
    private static bool HasCustom => Provider.Length > 0 && Secret.Length > 0;

    // ---------------- HTTP ----------------
    private static readonly HttpClient Http = NewHttp();
    private static HttpClient NewHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // 有啲 API（MusicBrainz、Bangumi、Wikipedia）要求寫明係邊個程式同聯絡方法
        c.DefaultRequestHeaders.UserAgent.ParseAdd("RabiToolkit/" + Program.Version.TrimStart('v') + " (jeffjob1123@gmail.com)");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }
    private static string E(string s) => Uri.EscapeDataString(s);

    private static async Task<JsonElement> GetAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? prep = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        prep?.Invoke(req);
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
    }

    private static async Task<JsonElement> PostAsync(string url, object body, CancellationToken ct, Action<HttpRequestMessage>? prep = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        prep?.Invoke(req);
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
    }

    // ---- 讀 JSON 嘅小工具：欄位唔存在 / 類型唔啱都唔會掟例外 ----
    private static JsonElement? P(this JsonElement e, params string[] path)
    {
        var cur = e;
        foreach (var k in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(k, out var next)) return null;
            cur = next;
        }
        return cur;
    }
    private static string S(this JsonElement e, params string[] path)
    {
        var v = e.P(path);
        return v == null ? "" : v.Value.ValueKind switch
        {
            JsonValueKind.String => v.Value.GetString() ?? "",
            JsonValueKind.Number => v.Value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }
    private static IEnumerable<JsonElement> A(this JsonElement e, params string[] path)
    {
        var v = path.Length == 0 ? e : e.P(path);
        return v is { ValueKind: JsonValueKind.Array } arr ? arr.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }
    private static string Plain(string html) => System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, @"<br\s*/?>", " ", RegexOptions.IgnoreCase), "<[^>]+>", "")).Replace("\r", " ").Replace("\n", " ").Trim();
    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    private static string Join(params string[] parts) => string.Join("；", parts.Where(p => p.Trim().Length > 0 && !p.TrimEnd().EndsWith("：") && !p.TrimEnd().EndsWith(":")));

    // ---------------- 內置來源 ----------------
    public static readonly Source[] All =
    {
        new("wikipedia", "Wikipedia", "general knowledge, people, places, history, concepts; search terms in the user's own language", Wikipedia),
        new("news", "GDELT", "recent world news and current events; 2-4 ENGLISH keywords", Gdelt),
        new("hackernews", "Hacker News", "technology and programming news and discussion; ENGLISH keywords", HackerNews),
        new("anilist", "AniList", "anime and manga facts (episodes, airing dates, score, genres); romaji or English title", AniList),
        new("bangumi", "Bangumi", "anime, manga, games with Chinese/Japanese information; Chinese or Japanese title", Bangumi),
        new("quote", "AnimeChan", "a quote from an anime; ENGLISH anime title", AnimeChan),
        new("pokemon", "PokéAPI", "Pokémon data (types, stats, abilities); ENGLISH Pokémon name", Pokemon),
        new("steam", "Steam", "PC game information, price, current player count; game name", Steam),
        new("deals", "CheapShark", "PC game discounts and cheapest price across stores; ENGLISH game name", CheapShark),
        new("tv", "TVmaze", "TV series information (status, network, premiere); ENGLISH title", TvMaze),
        new("music", "MusicBrainz", "music artists and albums (not lyrics); artist or album name", MusicBrainz),
    };

    // 只收容許商業用途嘅來源（Open-Meteo 免費 API 只限非商業，冇加）
    public static Source[] Sources => All;

    private static string WikiLang()
    {
        string code = Lang.Code(Lang.Current).ToLowerInvariant();
        return code.StartsWith("zh") ? "zh" : code.Split('-')[0];
    }

    private static async Task<List<Hit>> Wikipedia(string q, CancellationToken ct)
    {
        async Task<List<Hit>> One(string lang)
        {
            string variant = lang == "zh" ? "&variant=" + (Lang.Code(Lang.Current).Contains("Hans", StringComparison.OrdinalIgnoreCase) ? "zh-cn" : "zh-hk") : "";
            var root = await GetAsync($"https://{lang}.wikipedia.org/w/api.php?action=query&format=json&generator=search&gsrsearch={E(q)}&gsrlimit=3&prop=extracts|info&inprop=url&exintro=1&explaintext=1&exchars=900{variant}", ct);
            var list = new List<(int, Hit)>();
            if (root.P("query", "pages") is { ValueKind: JsonValueKind.Object } pages)
                foreach (var p in pages.EnumerateObject())
                    list.Add((int.TryParse(p.Value.S("index"), out int i) ? i : 99, new Hit(p.Value.S("title"), p.Value.S("fullurl"), p.Value.S("extract").Replace("\n", " "))));
            return list.OrderBy(x => x.Item1).Select(x => x.Item2).Where(h => h.Text.Length > 0).ToList();
        }
        string lang = WikiLang();
        var hits = await One(lang);
        if (hits.Count == 0 && lang != "en") hits = await One("en");
        return hits;
    }

    private static async Task<List<Hit>> Gdelt(string q, CancellationToken ct)
    {
        var root = await GetAsync($"https://api.gdeltproject.org/api/v2/doc/doc?query={E(q)}&mode=ArtList&format=json&maxrecords=8&sort=DateDesc&timespan=14d", ct);
        return root.A("articles").Select(a =>
        {
            string d = a.S("seendate");
            if (d.Length >= 8) d = d[..4] + "-" + d.Substring(4, 2) + "-" + d.Substring(6, 2);
            return new Hit(a.S("title"), a.S("url"), Join(d, a.S("domain"), a.S("sourcecountry")));
        }).Where(h => h.Title.Length > 0).GroupBy(h => h.Title).Select(g => g.First()).Take(6).ToList();
    }

    private static async Task<List<Hit>> HackerNews(string q, CancellationToken ct)
    {
        var root = await GetAsync($"https://hn.algolia.com/api/v1/search?query={E(q)}&tags=story&hitsPerPage=5", ct);
        return root.A("hits").Select(h => new Hit(h.S("title"), "https://news.ycombinator.com/item?id=" + h.S("objectID"),
            Join(Cut(h.S("created_at"), 10).TrimEnd('…'), h.S("points") + " points", h.S("num_comments") + " comments", h.S("url")))).Where(h => h.Title.Length > 0).ToList();
    }

    private static async Task<List<Hit>> AniList(string q, CancellationToken ct)
    {
        const string gql = "query($s:String){Page(perPage:3){media(search:$s){title{romaji english native} type format status episodes chapters season seasonYear averageScore genres siteUrl description(asHtml:false) startDate{year month day} nextAiringEpisode{episode airingAt}}}}";
        var root = await PostAsync("https://graphql.anilist.co", new { query = gql, variables = new { s = q } }, ct);
        return root.A("data", "Page", "media").Select(m =>
        {
            string next = "";
            if (m.P("nextAiringEpisode") is { ValueKind: JsonValueKind.Object } n && long.TryParse(n.S("airingAt"), out long at))
                next = "next episode " + n.S("episode") + " airs " + DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            string title = string.Join(" / ", new[] { m.S("title", "native"), m.S("title", "romaji"), m.S("title", "english") }.Where(t => t.Length > 0).Distinct());
            return new Hit(title, m.S("siteUrl"), Join(
                m.S("type") + " " + m.S("format"), "status: " + m.S("status"), "episodes: " + m.S("episodes"), "chapters: " + m.S("chapters"),
                "season: " + (m.S("season") + " " + m.S("seasonYear")).Trim(), "start: " + string.Join("-", new[] { m.S("startDate", "year"), m.S("startDate", "month"), m.S("startDate", "day") }.Where(x => x.Length > 0)),
                "score: " + m.S("averageScore"), "genres: " + string.Join(", ", m.A("genres").Select(g => g.GetString())), next, Cut(Plain(m.S("description")), 400)));
        }).ToList();
    }

    private static async Task<List<Hit>> Bangumi(string q, CancellationToken ct)
    {
        var root = await PostAsync("https://api.bgm.tv/v0/search/subjects?limit=4", new { keyword = q }, ct);
        string[] types = { "", "書籍", "動畫", "音樂", "遊戲", "", "三次元" };
        return root.A("data").Select(s =>
        {
            int t = int.TryParse(s.S("type"), out int x) && x < types.Length ? x : 0;
            string score = s.S("score").Length > 0 ? s.S("score") : s.S("rating", "score");
            string title = string.Join(" / ", new[] { s.S("name_cn"), s.S("name") }.Where(n => n.Length > 0).Distinct());
            return new Hit(title, "https://bgm.tv/subject/" + s.S("id"), Join(types[t], "日期：" + s.S("date"), "評分：" + score, Cut(s.S("summary").Replace("\r", " ").Replace("\n", " "), 400)));
        }).Where(h => h.Title.Length > 0).ToList();
    }

    private static async Task<List<Hit>> AnimeChan(string q, CancellationToken ct)
    {
        JsonElement root;
        try { root = await GetAsync($"https://api.animechan.io/v1/quotes/random?anime={E(q)}", ct); }
        catch (HttpRequestException) { root = await GetAsync($"https://api.animechan.io/v1/quotes/random?character={E(q)}", ct); }
        var d = root.P("data");
        if (d == null || d.Value.S("content").Length == 0) return new List<Hit>();
        return new List<Hit> { new(d.Value.S("anime", "name"), "https://animechan.io", "“" + d.Value.S("content") + "” — " + d.Value.S("character", "name")) };
    }

    private static async Task<List<Hit>> Pokemon(string q, CancellationToken ct)
    {
        string name = Regex.Replace(q.Trim().ToLowerInvariant(), @"[^a-z0-9\-]+", "-").Trim('-');
        if (name.Length == 0) return new List<Hit>();
        var p = await GetAsync($"https://pokeapi.co/api/v2/pokemon/{E(name)}", ct);
        string flavor = "", genus = "";
        try
        {
            var sp = await GetAsync($"https://pokeapi.co/api/v2/pokemon-species/{p.S("species", "name")}", ct);
            flavor = sp.A("flavor_text_entries").Where(f => f.S("language", "name") == "en").Select(f => Regex.Replace(f.S("flavor_text"), @"\s+", " ")).LastOrDefault() ?? "";
            genus = sp.A("genera").Where(g => g.S("language", "name") == "en").Select(g => g.S("genus")).FirstOrDefault() ?? "";
        }
        catch { }
        return new List<Hit>
        {
            new(p.S("name") + " (#" + p.S("id") + ")", "https://pokeapi.co/api/v2/pokemon/" + name, Join(genus,
                "types: " + string.Join(", ", p.A("types").Select(t => t.S("type", "name"))),
                "abilities: " + string.Join(", ", p.A("abilities").Select(a => a.S("ability", "name"))),
                "base stats: " + string.Join(", ", p.A("stats").Select(s => s.S("stat", "name") + " " + s.S("base_stat"))),
                "height: " + (double.TryParse(p.S("height"), NumberStyles.Any, CultureInfo.InvariantCulture, out double h) ? (h / 10).ToString("0.0", CultureInfo.InvariantCulture) + " m" : ""),
                "weight: " + (double.TryParse(p.S("weight"), NumberStyles.Any, CultureInfo.InvariantCulture, out double w) ? (w / 10).ToString("0.0", CultureInfo.InvariantCulture) + " kg" : ""), flavor))
        };
    }

    private static async Task<List<Hit>> Steam(string q, CancellationToken ct)
    {
        string code = Lang.Code(Lang.Current);
        string l = code.Contains("Hant", StringComparison.OrdinalIgnoreCase) ? "tchinese" : code.Contains("Hans", StringComparison.OrdinalIgnoreCase) ? "schinese"
            : code.StartsWith("ja") ? "japanese" : code.StartsWith("ko") ? "koreana" : "english";
        // 一定要有 cc（地區），唔係就乜都搵唔到；用 Windows 設定嘅地區，價錢就係當地貨幣
        string cc = "us";
        try { cc = RegionInfo.CurrentRegion.TwoLetterISORegionName.ToLowerInvariant(); } catch { }
        var root = await GetAsync($"https://store.steampowered.com/api/storesearch/?term={E(q)}&l={l}&cc={cc}", ct);
        var hits = new List<Hit>();
        foreach (var it in root.A("items").Take(3))
        {
            string id = it.S("id"), url = "https://store.steampowered.com/app/" + id;
            if (hits.Count > 0) { hits.Add(new Hit(it.S("name"), url, "")); continue; }
            // 第一個結果先攞詳細資料同而家幾多人玩緊
            string detail = "";
            try
            {
                var d = (await GetAsync($"https://store.steampowered.com/api/appdetails?appids={id}&l={l}&cc={cc}", ct)).P(id, "data");
                if (d != null)
                    detail = Join(d.Value.S("is_free") == "true" ? "free to play" : "price: " + d.Value.S("price_overview", "final_formatted"),
                        d.Value.S("price_overview", "discount_percent") is { Length: > 0 } dp && dp != "0" ? "discount: -" + dp + "%" : "",
                        "release: " + d.Value.S("release_date", "date"), "developer: " + string.Join(", ", d.Value.A("developers").Select(x => x.GetString())),
                        "genres: " + string.Join(", ", d.Value.A("genres").Select(g => g.S("description"))), "metacritic: " + d.Value.S("metacritic", "score"),
                        Cut(Plain(d.Value.S("short_description")), 350));
            }
            catch { }
            try
            {
                var pc = await GetAsync($"https://api.steampowered.com/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid={id}", ct);
                if (pc.S("response", "player_count") is { Length: > 0 } n) detail = Join("players online now: " + n, detail);
            }
            catch { }
            hits.Add(new Hit(it.S("name"), url, detail));
        }
        return hits;
    }

    private static async Task<List<Hit>> CheapShark(string q, CancellationToken ct)
    {
        var root = await GetAsync($"https://www.cheapshark.com/api/1.0/games?title={E(q)}&limit=4", ct);
        return root.A().Select(g => new Hit(g.S("external"), "https://www.cheapshark.com/redirect?dealID=" + g.S("cheapestDealID"), "cheapest current price: US$" + g.S("cheapest"))).Where(h => h.Title.Length > 0).ToList();
    }

    private static async Task<List<Hit>> TvMaze(string q, CancellationToken ct)
    {
        var root = await GetAsync($"https://api.tvmaze.com/search/shows?q={E(q)}", ct);
        return root.A().Take(3).Select(x => x.P("show")).Where(s => s != null).Select(s => s!.Value).Select(s => new Hit(s.S("name"), s.S("url"), Join(
            s.S("type"), "language: " + s.S("language"), "status: " + s.S("status"), "premiered: " + s.S("premiered"), "ended: " + s.S("ended"),
            "network: " + (s.S("network", "name").Length > 0 ? s.S("network", "name") : s.S("webChannel", "name")), "rating: " + s.S("rating", "average"),
            "genres: " + string.Join(", ", s.A("genres").Select(g => g.GetString())), Cut(Plain(s.S("summary")), 350)))).ToList();
    }

    private static async Task<List<Hit>> MusicBrainz(string q, CancellationToken ct)
    {
        var hits = new List<Hit>();
        var a = await GetAsync($"https://musicbrainz.org/ws/2/artist?query={E(q)}&limit=2&fmt=json", ct);
        foreach (var x in a.A("artists"))
            hits.Add(new Hit(x.S("name"), "https://musicbrainz.org/artist/" + x.S("id"), Join(x.S("type"), x.S("disambiguation"), "country: " + x.S("country"),
                "active: " + x.S("life-span", "begin") + " – " + x.S("life-span", "end"), "tags: " + string.Join(", ", x.A("tags").Select(t => t.S("name")).Take(6)))));
        await Task.Delay(1100, ct);   // MusicBrainz 規定每秒最多一次
        var r = await GetAsync($"https://musicbrainz.org/ws/2/release-group?query={E(q)}&limit=3&fmt=json", ct);
        foreach (var x in r.A("release-groups"))
            hits.Add(new Hit(x.S("title"), "https://musicbrainz.org/release-group/" + x.S("id"), Join(x.S("primary-type"),
                "by " + string.Join(", ", x.A("artist-credit").Select(c => c.S("name"))), "first release: " + x.S("first-release-date"))));
        return hits;
    }

    // ---------------- 自訂搜尋（用家自己嘅金鑰 / 網址）----------------
    private static async Task<List<Hit>> Custom(string q, CancellationToken ct)
    {
        string key = Secret;
        switch (Provider)
        {
            case "brave":
            {
                var root = await GetAsync($"https://api.search.brave.com/res/v1/web/search?q={E(q)}&count=5", ct, r => r.Headers.TryAddWithoutValidation("X-Subscription-Token", key));
                return root.A("web", "results").Select(x => new Hit(Plain(x.S("title")), x.S("url"), Cut(Plain(x.S("description")), 400))).ToList();
            }
            case "tavily":
            {
                var root = await PostAsync("https://api.tavily.com/search", new { query = q, max_results = 5 }, ct, r => r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key));
                return root.A("results").Select(x => new Hit(x.S("title"), x.S("url"), Cut(x.S("content").Replace("\n", " "), 400))).ToList();
            }
            case "searxng":
            {
                string baseUrl = key.TrimEnd('/');
                if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) baseUrl = "http://" + baseUrl;
                var root = await GetAsync($"{baseUrl}/search?q={E(q)}&format=json", ct);
                return root.A("results").Take(5).Select(x => new Hit(x.S("title"), x.S("url"), Cut(x.S("content").Replace("\n", " "), 400))).ToList();
            }
        }
        return new List<Hit>();
    }

    private static string CustomName => Provider switch { "brave" => "Brave Search", "tavily" => "Tavily", "searxng" => "SearXNG", _ => "" };

    // ---------------- 主流程 ----------------
    /// <summary>
    /// 幫一條問題上網搵資料。ask(system, user) = 叫本地模型出一段短文字（用嚟揀來源）。
    /// 回傳：貼入問題嘅資料（冇就空字串）同用過嘅來源（名、網址）。
    /// </summary>
    public static async Task<(string Context, List<(string Name, string Url)> Used)> GatherAsync(string question, Func<string, string, Task<string>> ask, CancellationToken ct)
    {
        var used = new List<(string, string)>();
        var sources = Sources.ToList();
        if (HasCustom) sources.Add(new Source("web", CustomName, "general web search for anything else or anything recent; search terms in any language", Custom));
        // ① 叫模型揀來源
        var plan = new List<(Source Src, string Q)>();
        bool none = false;
        try
        {
            string sys = "Today is " + DateTime.Now.ToString("yyyy-MM-dd") + ". You decide which online sources to look up before answering the user's message. " +
                         "Reply ONLY with up to 3 lines, each in the form `source: search terms`, nothing else. If no lookup is needed (chit-chat, writing, translation, maths, coding) reply exactly `none`.\nSources:\n" +
                         string.Join("\n", sources.Select(s => "- " + s.Key + " = " + s.Hint));
            string reply = await ask(sys, question.Length > 1500 ? question[..1500] : question);
            none = Regex.IsMatch(reply.Trim(), @"^\W*none\W*$", RegexOptions.IgnoreCase);
            foreach (Match m in Regex.Matches(reply, @"^\W*([a-zA-Z]+)\W*[:：]\s*(.+?)\s*$", RegexOptions.Multiline))
            {
                var src = sources.FirstOrDefault(s => s.Key.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                string q = m.Groups[2].Value.Trim('`', '"', '\'', ' ', '「', '」');
                if (src != null && q.Length > 0 && q.Length <= 120 && plan.All(p => p.Src != src)) plan.Add((src, q));
                if (plan.Count >= 3) break;
            }
        }
        catch (Exception ex) { AppPaths.LogError("上網搵資料（揀來源）", ex); }
        if (none && plan.Count == 0) return ("", used);
        // 模型答得唔合格式：用條問題本身搵 Wikipedia（同自訂搜尋）
        if (plan.Count == 0)
        {
            string q = Regex.Replace(question, @"\s+", " ").Trim();
            if (q.Length > 80) q = q[..80];
            plan.Add((sources.First(s => s.Key == "wikipedia"), q));
            if (sources.FirstOrDefault(s => s.Key == "web") is { } web) plan.Add((web, q));
        }
        // ② 同時去搵
        var tasks = plan.Select(async p =>
        {
            try { return (p.Src, p.Q, Hits: await p.Src.Run(p.Q, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                AppPaths.LogError("上網搵資料（" + p.Src.Name + "）", ex);
                return (p.Src, p.Q, Hits: new List<Hit>());
            }
        }).ToList();
        var results = await Task.WhenAll(tasks);
        // ③ 砌成一段資料（每個來源最多 1100 字，總共最多 3000 字，費事迫爆模型嘅上文）
        var sb = new StringBuilder();
        foreach (var (src, q, hits) in results)
        {
            if (hits.Count == 0) continue;
            var part = new StringBuilder().Append("■ ").Append(src.Name).Append("（").Append(q).Append("）\n");
            foreach (var h in hits)
            {
                string line = "- " + h.Title + (h.Text.Length > 0 ? "：" + h.Text : "") + (h.Url.Length > 0 ? " <" + h.Url + ">" : "") + "\n";
                if (part.Length + line.Length > 1100) break;
                part.Append(line);
                if (h.Url.Length > 0 && used.Count(u => u.Item1 == src.Name) < 2) used.Add((src.Name, h.Url));
            }
            if (sb.Length + part.Length > 3000) break;
            sb.Append(part);
        }
        if (sb.Length == 0) return ("", used);
        string ctx = "\n\n[系統：以下係程式啱啱喺網上搵到嘅資料（今日係 " + DateTime.Now.ToString("yyyy-MM-dd") + "）。請主要根據呢啲資料回答用戶；資料冇講到嘅就老實講搵唔到，唔好自己作。唔使列網址，程式會自動附上來源。\n" + sb + "]";
        return (ctx, used);
    }
}

/// <summary>「上網搵資料」設定：開關、內置來源一覽、自訂搜尋 API（金鑰 / 網址）。</summary>
internal sealed class WebSearchDialog : Form
{
    private static readonly (string Key, string Label)[] Providers = { ("", ""), ("brave", "Brave Search"), ("tavily", "Tavily"), ("searxng", "SearXNG") };

    public WebSearchDialog()
    {
        Text = Lang.T("上網搵資料");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.F(9.75f);
        int W = Ui.S(520), p = Ui.S(16), w = W - p * 2, y = Ui.S(14);

        var on = new CheckBox { Text = Lang.T("本地 AI 回答之前，先上網搵資料"), Checked = WebSearch.Enabled, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, Font = Ui.F(10.5f, FontStyle.Bold), Location = new Point(p, y) };
        Controls.Add(on);
        y += Ui.S(32);
        Controls.Add(new Label
        {
            Text = Lang.T("本地 AI 自己唔識上網。開咗之後，AI 助手入面嘅本地 AI 會先決定要搵乜，程式再去下面嘅公開資料來源搵，然後睇住結果回答，答案最尾會列出來源。Claude、ChatGPT、Gemini 用佢哋自己嘅搜尋，唔受呢度影響。"),
            ForeColor = Ui.TextDim, Font = Ui.F(9f), AutoSize = false, Bounds = new Rectangle(p, y, w, Ui.S(72))
        });
        y += Ui.S(76);
        Controls.Add(new Label { Text = Lang.T("內置資料來源（免費，唔使金鑰）"), ForeColor = Ui.Text, Font = Ui.F(9.5f, FontStyle.Bold), AutoSize = true, Location = new Point(p, y) });
        y += Ui.S(22);
        Controls.Add(new Label
        {
            Text = string.Join(" · ", WebSearch.Sources.Select(s => s.Name)),
            ForeColor = Ui.TextDim, Font = Ui.F(9f), AutoSize = false, Bounds = new Rectangle(p, y, w, Ui.S(38))
        });
        y += Ui.S(44);
        Controls.Add(new Label { Text = Lang.T("自訂搜尋 API（選填）"), ForeColor = Ui.Text, Font = Ui.F(9.5f, FontStyle.Bold), AutoSize = true, Location = new Point(p, y) });
        y += Ui.S(24);
        var prov = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Ui.Input, ForeColor = Ui.Text, FlatStyle = FlatStyle.Flat, Font = Ui.F(9.75f), Bounds = new Rectangle(p, y, Ui.S(150), Ui.S(28)) };
        foreach (var (k, label) in Providers) prov.Items.Add(k.Length == 0 ? Lang.T("唔使用") : label);
        prov.SelectedIndex = Math.Max(0, Array.FindIndex(Providers, x => x.Key == WebSearch.Provider));
        var key = new TextBox { BorderStyle = BorderStyle.FixedSingle, BackColor = Ui.Input, ForeColor = Ui.Text, Font = Ui.F(9.75f), Text = WebSearch.Secret, Bounds = new Rectangle(prov.Right + Ui.S(8), y, w - Ui.S(158), Ui.S(26)) };
        void Sync()
        {
            string k = Providers[Math.Max(0, prov.SelectedIndex)].Key;
            key.Enabled = k.Length > 0;
            key.UseSystemPasswordChar = k is "brave" or "tavily";   // 金鑰唔顯示出嚟；網址照顯示
            key.PlaceholderText = k == "searxng" ? "https://…" : k.Length > 0 ? "API key" : "";
        }
        prov.SelectedIndexChanged += (_, _) => Sync();
        Sync();
        Controls.Add(prov); Controls.Add(key);
        y += Ui.S(36);
        Controls.Add(new Label
        {
            Text = Lang.T("金鑰加密後只會存喺你部電腦，只會傳去你揀嘅搜尋服務。") + "\n" +
                   Lang.T("私隱：開咗之後，你問題入面嘅搜尋字眼會傳去上面嘅網站；唔想就唔好開。搵返嚟嘅資料由第三方提供，可能唔準確或者過時。"),
            ForeColor = Ui.TextFaint, Font = Ui.F(8.75f), AutoSize = false, Bounds = new Rectangle(p, y, w, Ui.S(72))
        });
        y += Ui.S(78);
        var ok = new FancyButton { Text = Lang.T("儲存"), Kind = BtnKind.Primary, Font = Ui.F(9.5f), Bounds = new Rectangle(W - p - Ui.S(100), y, Ui.S(100), Ui.S(32)) };
        var cancel = new FancyButton { Text = Lang.T("取消"), Kind = BtnKind.Ghost, Font = Ui.F(9.5f), Bounds = new Rectangle(ok.Left - Ui.S(108), y, Ui.S(100), Ui.S(32)) };
        ok.Click += (_, _) =>
        {
            string k = Providers[Math.Max(0, prov.SelectedIndex)].Key;
            WebSearch.Save(on.Checked, k, k.Length > 0 ? key.Text : "");
            DialogResult = DialogResult.OK;
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.Add(ok); Controls.Add(cancel);
        ClientSize = new Size(W, y + Ui.S(46));
    }
}
