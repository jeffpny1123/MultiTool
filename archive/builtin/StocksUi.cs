using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTool.UI;

namespace MultiTool.Tools;

// =====================================================================
//  股票頁：搜尋加入自選股 → 清單（現價、升跌、即日細圖）→ 撳一隻睇走勢圖（線 / K 線、MA）→ 右鍵設到價提醒
// =====================================================================
internal sealed class StocksPage : ToolPage
{
    private readonly StockList _list = new();
    private readonly StockChart _chart = new();
    private readonly TextBox _search = new();
    private readonly Label _status;
    private readonly StockTitle _title = new();
    private readonly FixedPanel _listHost, _chartHost;
    private CancellationTokenSource? _chartCts;
    private string? _sel;
    private bool _starOnly;
    private readonly FancyButton _starBtn;
    private readonly FancyButton _settingsBtn;

    public static Control Build() => new StocksPage();

    private StocksPage()
    {
        // ---- 自選股 ----
        _search.PlaceholderText = Lang.T("輸入公司名或者代號，例如 Apple、0700.HK、SHOP.TO、BTC-USD");
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = SearchAsync(); } };
        var searchHost = new InputHost(_search, Ui.S(380));
        var add = TUi.Btn(Lang.T("＋ 加入"), () => _ = SearchAsync(), 0, BtnKind.Primary);
        var refresh = TUi.Btn("⟳", () => _ = StockWatch.RefreshAsync(), 44);
        _starBtn = TUi.Btn(Lang.T("☆ 只睇標記"), () =>
        {
            _starOnly = !_starOnly;
            _starBtn!.Text = _starOnly ? Lang.T("★ 只睇標記") : Lang.T("☆ 只睇標記");
            _starBtn.Kind = _starOnly ? BtnKind.Primary : BtnKind.Ghost;
            _starBtn.Invalidate();
            Reload();
        });
        var widget = TUi.Btn(Lang.T("🗔 桌面小工具"), StockWidget.Toggle);
        FancyButton? hot = null;
        hot = TUi.Btn(Lang.T("🔥 熱門"), () => HotMenu(hot!));
        _status = TUi.L("");
        // 左邊清單、右邊走勢圖（放喺同一張卡入面）
        _listHost = new FixedPanel(Ui.S(620)) { BackColor = Ui.Input, Padding = new Padding(1), Dock = DockStyle.Left };
        _list.Dock = DockStyle.Fill;
        _listHost.Controls.Add(_list);
        _title.Dock = DockStyle.Top;
        _title.SetText(Lang.T("撳左邊一隻股票睇走勢圖"));
        _chartHost = new FixedPanel(Ui.S(400)) { BackColor = Ui.Input, Padding = new Padding(1), Dock = DockStyle.Fill };
        _chart.Dock = DockStyle.Fill;
        _chartHost.Controls.Add(_chart);
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Card, Padding = new Padding(Ui.S(12), 0, 0, 0) };
        right.Controls.Add(_chartHost);
        right.Controls.Add(_title); // Dock Top 要後加先會喺上面
        var split = new FixedPanel(Ui.S(620)) { BackColor = Ui.Card, Margin = new Padding(0, 0, 0, Ui.S(6)) };
        split.Controls.Add(right);
        split.Controls.Add(_listHost);
        // 清單固定闊度，其餘位俾走勢圖
        _listHost.Width = Ui.S(360);
        // 工具列：左邊一排掣，最右「⚙ 設定」
        _settingsBtn = TUi.Btn("⚙ " + Lang.T("設定"), SettingsMenu);
        var leftRow = TUi.Row(searchHost, add, hot, refresh, _starBtn, widget);
        leftRow.Margin = new Padding(0);
        var bar = new FixedPanel(Ui.S(44)) { BackColor = Ui.Card, Margin = new Padding(0, 0, 0, Ui.S(8)) };
        _settingsBtn.Margin = new Padding(0);
        bar.Controls.Add(leftRow);
        bar.Controls.Add(_settingsBtn);
        bar.Resize += (_, _) =>
        {
            _settingsBtn.Location = new Point(bar.ClientSize.Width - _settingsBtn.Width, 0);
            leftRow.Location = new Point(0, 0);
        };
        var body = TUi.Col(bar, split, _status);
        Card(Lang.T("股票"),
            Lang.T("睇美股、港股、加股同加密貨幣嘅報價同走勢，設定到價提醒（程式開住就會通知）。數據來自 Yahoo Finance，可能延遲大約 15 分鐘，只供參考，唔係投資建議。"),
            body);

        // 跟張卡闊度
        void Fit()
        {
            int w = body.ClientSize.Width - Ui.S(4);
            if (w > Ui.S(200) && split.Width != w) { split.Width = w; bar.Width = w; }
        }
        body.Resize += (_, _) => Fit();
        body.Parent!.Resize += (_, _) => Fit();
        Fit();

        _list.Picked += sym => _ = ShowChartAsync(sym);
        _list.MenuAt += (sym, pt) => ItemMenu(sym, pt);
        _list.StarClicked += ToggleStar;
        _list.Moved += () => { lock (StockWatch.Items) { var order = _list.Order; StockWatch.Items.Sort((a, b) => order.IndexOf(a.Symbol).CompareTo(order.IndexOf(b.Symbol))); } StockWatch.Save(); };
        _chart.RangeChanged += () => { if (_sel != null) _ = ShowChartAsync(_sel); };
        StockPrefs.Changed += OnPrefs;
        Disposed += (_, _) => StockPrefs.Changed -= OnPrefs;

        StockWatch.Updated += OnUpdated;
        StockWatch.Ticked += OnTicked;
        Disposed += (_, _) => { StockWatch.Updated -= OnUpdated; StockWatch.Ticked -= OnTicked; };
        StockWatch.Start();
        Reload();
        StockItem? first;
        lock (StockWatch.Items) first = StockWatch.Items.OrderBy(x => x.Starred ? 0 : 1).FirstOrDefault();
        if (first != null) { _ = ShowChartAsync(first.Symbol); _ = StockWatch.RefreshAsync(); }
    }

    /// <summary>即時推送：而家揀緊嗰隻有新價就更新走勢圖最後一點。</summary>
    private void OnTicked(StockTick t)
    {
        if (IsDisposed || !IsHandleCreated || _sel == null || !t.Symbol.Equals(_sel, StringComparison.OrdinalIgnoreCase)) return;
        try { BeginInvoke(() => _chart.ApplyTick(t.Price, t.TimeUtc)); } catch { }
    }

    private void OnUpdated()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(Reload); } catch { }
    }

    /// <summary>重新畫清單同狀態。</summary>
    private void Reload()
    {
        List<StockItem> items;
        lock (StockWatch.Items) items = StockWatch.Items.ToList();
        int total = items.Count;
        if (_starOnly) items = items.Where(x => x.Starred).ToList();
        if (StockPrefs.HidePresets) items = items.Where(x => !x.IsPreset).ToList();
        items = items.OrderBy(x => x.Starred ? 0 : 1).ToList(); // ★ 標記咗嘅排最前
        Dictionary<string, StockQuote> quotes;
        Dictionary<string, string> errs;
        lock (StockWatch.Quotes) { quotes = new(StockWatch.Quotes, StringComparer.OrdinalIgnoreCase); errs = new(StockWatch.Errors, StringComparer.OrdinalIgnoreCase); }
        _list.SetItems(items, quotes, errs);
        _status.Text = total == 0 ? Lang.T("未有自選股：喺上面輸入公司名或者代號，撳 Enter 加入。")
            : items.Count == 0 && !_starOnly ? Lang.T("預設股票已經隱藏：喺上面搜尋或者撳「🔥 熱門」加入你想睇嘅。")
            : items.Count == 0 ? Lang.T("未有標記嘅股票：撳股票左邊嘅 ☆ 標記想特別留意嘅。")
            : StockWatch.LastUpdate == default ? Lang.T("攞緊報價…")
            : YahooStream.Connected
                ? Lang.F("⚡ 即時推送中（最後更新 {0}；冇成交嘅時候唔會跳）", StockWatch.LastUpdate.ToString("HH:mm:ss"))
                : Lang.F("最後更新 {0}（每 30 秒自動更新）", StockWatch.LastUpdate.ToString("HH:mm:ss"));
        // 有買入價嘅：按貨幣加埋總賺蝕
        var sums = new Dictionary<string, (double Pnl, double CostSum)>();
        foreach (var it in items)
            if (quotes.TryGetValue(it.Symbol, out var qq) && it.Pnl(qq.Price) is { Total: { } tot } && it.Cost is { } c && it.Shares is { } sh)
            {
                sums.TryGetValue(qq.Currency, out var cur);
                sums[qq.Currency] = (cur.Pnl + tot, cur.CostSum + c * sh);
            }
        if (sums.Count > 0)
            _status.Text += "      " + Lang.T("💰 總賺蝕：") + string.Join("   ", sums.Select(kv =>
            {
                string sg = kv.Value.Pnl >= 0 ? "+" : "";
                double pct = kv.Value.CostSum > 0 ? kv.Value.Pnl / kv.Value.CostSum * 100 : 0;
                return $"{kv.Key} {sg}{StockWatch.Fmt(kv.Value.Pnl)} ({sg}{pct:0.00}%)";
            }));
        if (_sel != null && quotes.TryGetValue(_sel, out var q)) SetTitle(q);
        // 走勢圖畫埋買入價線
        StockItem? selIt;
        lock (StockWatch.Items) selIt = StockWatch.Items.FirstOrDefault(x => x.Symbol == _sel);
        _chart.CostLine = selIt?.Cost;
    }

    /// <summary>標題：代號 + 名、現價、今日升跌（升跌色）、我嘅回報（賺 / 蝕色），每段獨立顏色。</summary>
    private void SetTitle(StockQuote q)
    {
        string sign = q.Change >= 0 ? "+" : "";
        Color dayCol = q.Change > 0 ? StockPrefs.Up : q.Change < 0 ? StockPrefs.Down : Ui.Text;
        var parts = new List<(string, Color)>
        {
            ($"{q.Symbol}  {q.Name}", Ui.Text),
            ($"{StockWatch.Fmt(q.Price)} {q.Currency}", Ui.Text),
            ($"{sign}{StockWatch.Fmt(q.Change)} ({sign}{q.Pct:0.00}%)", dayCol),
        };
        StockItem? it;
        lock (StockWatch.Items) it = StockWatch.Items.FirstOrDefault(x => x.Symbol == q.Symbol);
        if (it?.Pnl(q.Price) is { } p)
        {
            string ps = p.PerShare >= 0 ? "+" : "";
            parts.Add(("·", Ui.TextFaint));
            parts.Add((Lang.F("我嘅回報 {0}{1}%", ps, p.Pct.ToString("0.00")) + (p.Total is { } tot ? $"（{ps}{StockWatch.Fmt(tot)} {q.Currency}）" : ""),
                p.PerShare > 0 ? StockPrefs.Up : p.PerShare < 0 ? StockPrefs.Down : Ui.Text));
        }
        _title.SetParts(parts);
    }

    // ---------- 搜尋 / 加入 ----------

    private async Task SearchAsync()
    {
        string q = _search.Text.Trim();
        if (q.Length == 0) { _search.Focus(); return; }
        _status.Text = Lang.T("搜尋緊…");
        List<StockSearchHit> hits;
        try { hits = await Yahoo.SearchAsync(q); }
        catch (Exception ex) { _status.Text = Lang.T("搜尋失敗：") + ex.Message; return; }
        if (IsDisposed) return;
        // 冇結果但打嘅似代號：直接試加
        if (hits.Count == 0) { await AddAsync(q.ToUpperInvariant(), ""); return; }
        var m = TUi.Menu(this);
        foreach (var h in hits)
        {
            var hit = h;
            m.Items.Add($"{hit.Symbol}    {hit.Name}    ({hit.Exchange}{(hit.Type.Length > 0 ? " · " + hit.Type : "")})", null, (_, _) => _ = AddAsync(hit.Symbol, hit.Name));
        }
        m.Show(_search, new Point(0, _search.Height + Ui.S(6)));
        Reload();
    }

    private void OnPrefs()
    {
        Reload();
        _list.Invalidate();
        _chart.Invalidate();
        StockWidget.Refresh();
    }

    /// <summary>⚙ 設定：彈出細視窗（升跌顏色、圖表款式、成交量），改咗即時生效。</summary>
    private void SettingsMenu()
    {
        using var f = new StockSettingsForm();
        f.ShowDialog(FindForm());
    }

    /// <summary>熱門股票選單：分類 → 一撳加入 / 移除（已加入嘅有剔）。</summary>
    private void HotMenu(Control anchor)
    {
        HashSet<string> have;
        lock (StockWatch.Items) have = StockWatch.Items.Where(x => !(StockPrefs.HidePresets && x.IsPreset)).Select(x => x.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var m = TUi.Menu(this);
        foreach (var (cat, list) in StockStore.Popular)
        {
            var sub = new ToolStripMenuItem(Lang.T(cat));
            foreach (var (sym, name) in list)
            {
                var item = new ToolStripMenuItem(sym + "    " + name) { Checked = have.Contains(sym) };
                string s = sym, n = name;
                item.Click += (_, _) =>
                {
                    if (have.Contains(s))
                    {
                        lock (StockWatch.Items) StockWatch.Items.RemoveAll(x => x.Symbol.Equals(s, StringComparison.OrdinalIgnoreCase));
                        StockWatch.Save();
                        if (_sel == s) { _sel = null; _chart.SetData(null); _title.SetText(Lang.T("撳左邊一隻股票睇走勢圖")); }
                        Reload();
                    }
                    else _ = AddAsync(s, n);
                };
                sub.DropDownItems.Add(item);
            }
            // 成類一次過加入
            sub.DropDownItems.Add(new ToolStripSeparator());
            var all = new ToolStripMenuItem(Lang.T("全部加入"));
            var cl = list;
            all.Click += (_, _) => AddMany(cl);
            sub.DropDownItems.Add(all);
            m.Items.Add(sub);
        }
        m.Show(anchor, new Point(0, anchor.Height));
    }

    /// <summary>一次加入幾隻（唔逐隻檢查，報價下次更新先攞）。</summary>
    private void AddMany((string Symbol, string Name)[] list)
    {
        int n = 0;
        lock (StockWatch.Items)
            foreach (var (sym, name) in list)
                if (!StockWatch.Items.Any(x => x.Symbol.Equals(sym, StringComparison.OrdinalIgnoreCase)))
                {
                    StockWatch.Items.Add(new StockItem { Symbol = sym, Name = name, UserAdded = true });
                    n++;
                }
        if (n == 0) return;
        StockWatch.Save();
        Reload();
        _ = StockWatch.RefreshAsync();
        Toast.Show(Lang.F("已加入 {0} 隻", n), Ui.Success);
    }

    private async Task AddAsync(string sym, string name)
    {
        lock (StockWatch.Items)
            if (StockWatch.Items.FirstOrDefault(x => x.Symbol.Equals(sym, StringComparison.OrdinalIgnoreCase)) is { } exist)
            {
                if (exist.IsPreset && StockPrefs.HidePresets)
                {
                    // 隱藏咗嘅預設股票：當係自己加入，即刻顯示返
                    exist.UserAdded = true;
                    StockWatch.Save();
                    Reload();
                    _ = ShowChartAsync(sym);
                    return;
                }
                Toast.Show(Lang.F("{0} 已經喺自選股入面", sym), Ui.TextDim);
                _ = ShowChartAsync(sym);
                return;
            }
        _status.Text = Lang.F("檢查緊 {0}…", sym);
        StockQuote q;
        try { q = await Yahoo.QuoteAsync(sym); }
        catch (Exception ex) { _status.Text = ex.Message; return; }
        if (IsDisposed) return;
        lock (StockWatch.Items) StockWatch.Items.Add(new StockItem { Symbol = q.Symbol, Name = name.Length > 0 ? name : q.Name, UserAdded = true });
        lock (StockWatch.Quotes) StockWatch.Quotes[q.Symbol] = q;
        StockWatch.Save();
        _search.Text = "";
        Reload();
        _ = ShowChartAsync(q.Symbol);
    }

    // ---------- 右鍵 ----------

    private void ItemMenu(string sym, Point at)
    {
        StockItem? it;
        lock (StockWatch.Items) it = StockWatch.Items.FirstOrDefault(x => x.Symbol == sym);
        if (it == null) return;
        var m = TUi.Menu(this);
        m.Items.Add(Lang.T("睇走勢圖"), null, (_, _) => _ = ShowChartAsync(sym));
        m.Items.Add(it.Starred ? Lang.T("☆ 取消標記") : Lang.T("★ 標記"), null, (_, _) => ToggleStar(sym));
        m.Items.Add(Lang.T("💰 設定我嘅買入價…"), null, (_, _) => EditCost(it));
        if (it.Cost != null) m.Items.Add(Lang.T("清除買入價"), null, (_, _) => { it.Cost = it.Shares = null; StockWatch.Save(); Reload(); });
        m.Items.Add(Lang.T("🔔 到價提醒…"), null, (_, _) => EditAlert(it));
        if (it.HasAlert) m.Items.Add(Lang.T("取消到價提醒"), null, (_, _) => { it.Above = it.Below = it.PctAlert = null; StockWatch.Save(); Reload(); });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Lang.T("喺 Yahoo Finance 網頁開"), null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://finance.yahoo.com/quote/" + Uri.EscapeDataString(sym)) { UseShellExecute = true }); } catch { }
        });
        m.Items.Add(Lang.T("移除"), null, (_, _) =>
        {
            lock (StockWatch.Items) StockWatch.Items.Remove(it);
            StockWatch.Save();
            if (_sel == sym) { _sel = null; _chart.SetData(null); _title.SetText(Lang.T("撳左邊一隻股票睇走勢圖")); }
            Reload();
        });
        m.Show(_list, at);
    }

    /// <summary>★ 標記 / 取消（桌面小工具會顯示標記咗嘅）。</summary>
    private void ToggleStar(string sym)
    {
        lock (StockWatch.Items)
        {
            var it = StockWatch.Items.FirstOrDefault(x => x.Symbol == sym);
            if (it == null) return;
            it.Starred = !it.Starred;
        }
        StockWatch.Save();
        Reload();
        StockWidget.Refresh();
    }

    private void EditCost(StockItem it)
    {
        StockQuote? q;
        lock (StockWatch.Quotes) StockWatch.Quotes.TryGetValue(it.Symbol, out q);
        using var f = new StockCostForm(it, q);
        if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
        it.Cost = f.Cost;
        it.Shares = f.Cost == null ? null : f.Shares;
        StockWatch.Save();
        Reload();
    }

    private void EditAlert(StockItem it)
    {
        StockQuote? q;
        lock (StockWatch.Quotes) StockWatch.Quotes.TryGetValue(it.Symbol, out q);
        using var f = new StockAlertForm(it, q);
        if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
        it.Above = f.Above;
        it.Below = f.Below;
        if (it.PctAlert != f.Pct) it.PctFired = "";
        it.PctAlert = f.Pct;
        StockWatch.Save();
        Reload();
        if (it.HasAlert) Toast.Show(Lang.T("已設定到價提醒（程式開住就會通知）"), Ui.Success);
    }

    // ---------- 走勢圖 ----------

    private async Task ShowChartAsync(string sym)
    {
        _sel = sym;
        _list.Selected = sym;
        lock (StockWatch.Items) _chart.CostLine = StockWatch.Items.FirstOrDefault(x => x.Symbol == sym)?.Cost;
        lock (StockWatch.Quotes) if (StockWatch.Quotes.TryGetValue(sym, out var q)) SetTitle(q); else _title.SetText(sym);
        _chartCts?.Cancel();
        var cts = _chartCts = new CancellationTokenSource();
        _chart.Loading = true;
        try
        {
            var d = await Yahoo.ChartAsync(sym, _chart.RangeIndex, cts.Token);
            if (cts.IsCancellationRequested || IsDisposed) return;
            _chart.SetData(d);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) _chart.SetError(ex.Message); }
        finally { if (_chartCts == cts) _chart.Loading = false; }
    }
}

/// <summary>自選股清單：按市場分組（指數、美股、港股、加股、加密貨幣），每組可以摺埋；同組入面可以拖嚟拖去排次序。</summary>
internal sealed class StockList : Control
{
    private List<StockItem> _items = new();
    private Dictionary<string, StockQuote> _q = new();
    private Dictionary<string, string> _err = new();
    private int _hover = -1, _scroll, _drag = -1, _dragY;
    public string? Selected { get => _sel; set { _sel = value; Invalidate(); } }
    private string? _sel;
    public int RowH => Ui.S(54);
    private int HeadH => Ui.S(32);

    public event Action<string>? Picked;
    public event Action<string, Point>? MenuAt;
    public event Action? Moved;
    public event Action<string>? StarClicked;
    private int StarW => Ui.S(30);
    public List<string> Order => _items.Select(x => x.Symbol).ToList();

    // ---------- 分組 ----------
    private static readonly string[] Groups = { "idx", "us", "hk", "ca", "crypto", "other" };
    private static string GroupName(string g) => g switch
    {
        "idx" => Lang.T("指數"), "us" => Lang.T("美股"), "hk" => Lang.T("港股"), "ca" => Lang.T("加股"), "crypto" => Lang.T("加密貨幣"), _ => Lang.T("其他")
    };

    /// <summary>由代號估市場：^ 開頭 = 指數、.HK = 港股、.TO / .V / .CN / .NE = 加股、XXX-USD = 加密貨幣。</summary>
    public static string GroupOf(string sym)
    {
        sym = sym.ToUpperInvariant();
        if (sym.StartsWith('^')) return "idx";
        if (sym.EndsWith(".HK")) return "hk";
        if (sym.EndsWith(".TO") || sym.EndsWith(".V") || sym.EndsWith(".CN") || sym.EndsWith(".NE")) return "ca";
        if (sym.Contains('-') && !sym.Contains('.') && (sym.EndsWith("-USD") || sym.EndsWith("-USDT") || sym.EndsWith("-CAD") || sym.EndsWith("-HKD") || sym.EndsWith("-EUR") || sym.EndsWith("-BTC"))) return "crypto";
        if (sym.Contains('.') || sym.Contains('=')) return "other";
        return "us";
    }

    private static readonly HashSet<string> Collapsed = LoadCollapsed();
    private static string CollapsedPath => System.IO.Path.Combine(AppPaths.Data, "stocks", "groups.txt");
    private static HashSet<string> LoadCollapsed()
    {
        try { return System.IO.File.Exists(CollapsedPath) ? System.IO.File.ReadAllText(CollapsedPath).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet() : new(); }
        catch { return new(); }
    }
    private static void SaveCollapsed()
    {
        try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CollapsedPath)!); System.IO.File.WriteAllText(CollapsedPath, string.Join(",", Collapsed)); } catch { }
    }

    /// <summary>一行：組標題或者一隻股票。</summary>
    private readonly record struct Row(bool Head, string Group, int Index, int Y, int H, int Count);
    private List<Row> _rows = new();

    private void BuildRows()
    {
        _rows = new();
        int y = 0;
        foreach (var g in Groups)
        {
            var idx = Enumerable.Range(0, _items.Count).Where(i => GroupOf(_items[i].Symbol) == g).ToList();
            if (idx.Count == 0) continue;
            _rows.Add(new Row(true, g, -1, y, HeadH, idx.Count));
            y += HeadH;
            if (Collapsed.Contains(g)) continue;
            foreach (var i in idx) { _rows.Add(new Row(false, g, i, y, RowH, 0)); y += RowH; }
        }
        _contentH = y;
    }
    private int _contentH;

    public StockList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Ui.F(9.75f);
        BackColor = Ui.Input;
    }

    public void SetItems(List<StockItem> items, Dictionary<string, StockQuote> q, Dictionary<string, string> err)
    {
        // 按組排（同組入面保持原本次序，★ 已經排咗最前）
        _items = items.OrderBy(x => Array.IndexOf(Groups, GroupOf(x.Symbol))).ToList();
        _q = q; _err = err;
        BuildRows();
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        Invalidate();
    }

    private int MaxScroll => Math.Max(0, _contentH - ClientSize.Height);
    private int At(Point p)
    {
        int y = p.Y + _scroll;
        for (int i = 0; i < _rows.Count; i++) if (y >= _rows[i].Y && y < _rows[i].Y + _rows[i].H) return i;
        return -1;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e is HandledMouseEventArgs h) h.Handled = true; // 唔好捲埋成頁
        if (MaxScroll == 0) return;
        _scroll = Math.Clamp(_scroll - e.Delta / 120 * RowH, 0, MaxScroll);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag >= 0 && e.Button == MouseButtons.Left && Math.Abs(e.Y - _dragY) > Ui.S(6))
        {
            // 只可以喺同一組入面拖
            int ri = At(e.Location);
            if (ri >= 0 && !_rows[ri].Head && _rows[ri].Group == GroupOf(_items[_drag].Symbol) && _rows[ri].Index != _drag)
            {
                int to = _rows[ri].Index;
                var it = _items[_drag];
                _items.RemoveAt(_drag);
                _items.Insert(to, it);
                _drag = to;
                BuildRows();
                Moved?.Invoke();
                Invalidate();
            }
            return;
        }
        int i = At(e.Location);
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        if (i != _hover) { _hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int ri = At(e.Location);
        if (ri < 0) return;
        var row = _rows[ri];
        if (row.Head)
        {
            // 撳組標題：摺埋 / 打開
            if (e.Button == MouseButtons.Left)
            {
                if (!Collapsed.Remove(row.Group)) Collapsed.Add(row.Group);
                SaveCollapsed();
                BuildRows();
                _scroll = Math.Clamp(_scroll, 0, MaxScroll);
                Invalidate();
            }
            return;
        }
        int i = row.Index;
        if (e.Button == MouseButtons.Left && e.X < StarW + Ui.S(6)) { StarClicked?.Invoke(_items[i].Symbol); return; }
        if (e.Button == MouseButtons.Left) { _drag = i; _dragY = e.Y; Picked?.Invoke(_items[i].Symbol); }
        else if (e.Button == MouseButtons.Right) MenuAt?.Invoke(_items[i].Symbol, e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = -1; }

    private const TextFormatFlags TF = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Input);
        if (_items.Count == 0)
        {
            TextRenderer.DrawText(g, Lang.T("（未有自選股）"), Font, ClientRectangle, Ui.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        Ui.Smooth(g);
        int w = ClientSize.Width;
        var bold = Ui.F(10.5f, FontStyle.Bold);
        var small = Ui.F(8.75f);
        using var line = new Pen(Ui.Alpha(Ui.Border, 120));
        for (int ri = 0; ri < _rows.Count; ri++)
        {
            var row = _rows[ri];
            var r = new Rectangle(0, row.Y - _scroll, w, row.H);
            if (r.Bottom < 0 || r.Y > ClientSize.Height) continue;
            if (row.Head)
            {
                // 組標題：▾ 美股（4）
                using (var hb = new SolidBrush(ri == _hover ? Ui.CardHi : Ui.Card)) g.FillRectangle(hb, r);
                g.DrawLine(line, 0, r.Bottom - 1, w, r.Bottom - 1);
                bool col = Collapsed.Contains(row.Group);
                TextRenderer.DrawText(g, (col ? "▸  " : "▾  ") + GroupName(row.Group) + $"  ({row.Count})", Ui.F(9.25f, FontStyle.Bold),
                    new Rectangle(Ui.S(12), r.Y, w - Ui.S(24), r.Height), Ui.TextDim, TF);
                continue;
            }
            var it = _items[row.Index];
            bool sel = it.Symbol == _sel;
            if (sel || ri == _hover) using (var b = new SolidBrush(sel ? Ui.Alpha(Ui.Accent, 55) : Ui.Alpha(Ui.CardHi, 200))) g.FillRectangle(b, r);
            g.DrawLine(line, 0, r.Bottom - 1, w, r.Bottom - 1);
            _q.TryGetValue(it.Symbol, out var q);
            // 最左：★ 標記（撳就切換）
            TextRenderer.DrawText(g, it.Starred ? "★" : "☆", Ui.F(13f), new Rectangle(Ui.S(6), r.Y, StarW, r.Height),
                it.Starred ? Color.FromArgb(0xF5, 0xB7, 0x01) : Ui.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            int pad = StarW + Ui.S(10);
            // 左：代號 + 名（有提醒就加鈴）
            string bell = it.HasAlert ? "  🔔" : "";
            // 名嗰欄：窄清單就縮短（右邊要留位俾價錢）
            int nameW = Math.Clamp(w - pad - Ui.S(150) - Ui.S(24), Ui.S(80), Ui.S(200));
            int symW = TextRenderer.MeasureText(it.Symbol + bell, bold).Width;
            TextRenderer.DrawText(g, it.Symbol + bell, bold, new Rectangle(pad, r.Y + Ui.S(6), nameW, Ui.S(22)), Ui.Text, TF);
            // 有買入價：代號後面加個細標籤顯示我嘅賺蝕 %
            if (q != null && it.Pnl(q.Price) is { } pl && symW + Ui.S(70) < nameW)
            {
                string ptxt = (pl.Pct >= 0 ? "▲" : "▼") + Math.Abs(pl.Pct).ToString("0.0") + "%";
                var pf = Ui.F(8f, FontStyle.Bold);
                int pw = TextRenderer.MeasureText(ptxt, pf).Width + Ui.S(8);
                var pr = new Rectangle(pad + symW + Ui.S(2), r.Y + Ui.S(8), pw, Ui.S(18));
                Color pc = pl.Pct >= 0 ? StockPrefs.Up : StockPrefs.Down;
                using (var pp = Ui.Round(pr, Ui.S(9f))) using (var pb = new SolidBrush(Ui.Alpha(pc, 50))) g.FillPath(pb, pp);
                TextRenderer.DrawText(g, ptxt, pf, pr, pc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            TextRenderer.DrawText(g, q?.Name ?? it.Name, small, new Rectangle(pad, r.Y + Ui.S(27), nameW, Ui.S(20)), Ui.TextDim, TF);
            // 右：現價 + 升跌
            int rw = Ui.S(150);
            var right = new Rectangle(w - rw - Ui.S(12), r.Y, rw, r.Height);
            if (q != null)
            {
                Color col = q.Change > 0 ? StockPrefs.Up : q.Change < 0 ? StockPrefs.Down : Ui.TextDim;
                TextRenderer.DrawText(g, StockWatch.Fmt(q.Price) + " " + q.Currency, bold, new Rectangle(right.X, r.Y + Ui.S(6), rw, Ui.S(22)), Ui.Text, TF | TextFormatFlags.Right);
                string sign = q.Change >= 0 ? "+" : "";
                string chg = $"{sign}{StockWatch.Fmt(q.Change)}  ({sign}{q.Pct:0.00}%)";
                TextRenderer.DrawText(g, chg, Ui.F(9f, FontStyle.Bold), new Rectangle(right.X, r.Y + Ui.S(27), rw, Ui.S(20)), col, TF | TextFormatFlags.Right);
                // 中：即日細圖
                int sx = pad + nameW + Ui.S(10), sw = right.X - sx - Ui.S(20);
                if (sw > Ui.S(60) && q.Spark.Length > 1) Spark(g, new Rectangle(sx, r.Y + Ui.S(10), sw, r.Height - Ui.S(20)), q.Spark, q.PrevClose, col);
            }
            else
            {
                string t = _err.TryGetValue(it.Symbol, out var er) ? "⚠ " + er : Lang.T("載入緊…");
                TextRenderer.DrawText(g, t, small, right, _err.ContainsKey(it.Symbol) ? Ui.Danger : Ui.TextFaint, TF | TextFormatFlags.Right);
            }
        }
    }

    private static void Spark(Graphics g, Rectangle r, double[] v, double prev, Color col)
    {
        double lo = Math.Min(v.Min(), prev), hi = Math.Max(v.Max(), prev);
        if (hi - lo < 1e-12) hi = lo + 1;
        float X(int i) => r.X + (float)i / (v.Length - 1) * r.Width;
        float Y(double p) => r.Bottom - (float)((p - lo) / (hi - lo)) * r.Height;
        using (var dash = new Pen(Ui.Alpha(Ui.TextFaint, 140), 1f) { DashStyle = DashStyle.Dot })
            g.DrawLine(dash, r.X, Y(prev), r.Right, Y(prev));
        var pts = v.Select((p, i) => new PointF(X(i), Y(p))).ToArray();
        using var pen = new Pen(col, Ui.S(1.6f));
        g.DrawLines(pen, pts);
    }
}

/// <summary>走勢圖：上面揀範圍同圖類，中間價格（線 / K 線 + MA），下面成交量；十字線顯示每點數據。</summary>
internal sealed class StockChart : Control
{
    private StockChartData? _d;
    private double? _cost;
    /// <summary>我嘅買入價（畫一條金色虛線）。</summary>
    public double? CostLine { get => _cost; set { if (_cost != value) { _cost = value; Invalidate(); } } }
    private string? _error;
    private bool _loading;
    private int _hover = -1;
    private bool _ma20 = true, _ma50;
    private readonly List<(Rectangle R, Action Click)> _buttons = new();

    public int RangeIndex { get; private set; }
    public event Action? RangeChanged;

    private static readonly string[] Prefs = LoadPrefs();

    public StockChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Ui.F(9f);
        BackColor = Ui.Input;
        // 記住上次揀嘅範圍同圖類
        if (Prefs.Length >= 4)
        {
            RangeIndex = int.TryParse(Prefs[0], out var ri) ? Math.Clamp(ri, 0, Yahoo.Ranges.Length - 1) : 0;
            _ma20 = Prefs[2] == "1"; _ma50 = Prefs[3] == "1";
        }
    }

    private static string PrefPath => System.IO.Path.Combine(AppPaths.Data, "stocks", "chart.txt");
    private static string[] LoadPrefs() { try { return System.IO.File.Exists(PrefPath) ? System.IO.File.ReadAllText(PrefPath).Split(',') : Array.Empty<string>(); } catch { return Array.Empty<string>(); } }
    private void SavePrefs()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PrefPath)!);
            System.IO.File.WriteAllText(PrefPath, $"{RangeIndex},0,{(_ma20 ? 1 : 0)},{(_ma50 ? 1 : 0)}");
        }
        catch { }
    }

    public bool Loading { get => _loading; set { _loading = value; Invalidate(); } }
    public void SetData(StockChartData? d) { _d = d; _error = null; _hover = -1; _hit = false; _yShift = 0; _zs = 0; _zn = 0; Invalidate(); }

    // ---------- 縮放：滾輪放大縮細、拖動左右移、雙擊還原 ----------
    private int _zs, _zn; // 顯示由第 _zs 條開始、共 _zn 條（0 = 全部）
    private int _panX = -1, _panY, _panStart;
    private double _yShift, _panYShift; // 上下拖：價格範圍移咗幾多（以成個範圍做單位，正數 = 睇上面）
    private bool Moved_ => Zoomed || Math.Abs(_yShift) > 1e-9;
    private bool Zoomed => _zn > 0 && _d != null && _zn < _d.Candles.Count;

    private (int Start, int Count) View()
    {
        int total = _d?.Candles.Count ?? 0;
        if (_zn <= 0 || _zn >= total) return (0, total);
        int st = Math.Clamp(_zs, 0, total - _zn);
        return (st, _zn);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e is HandledMouseEventArgs h) h.Handled = true; // 唔好捲埋成頁
        if (_d == null || _d.Candles.Count < 12) return;
        var plot = Plot();
        int total = _d.Candles.Count;
        var (vs, vn) = View();
        double rel = Math.Clamp((e.X - plot.X) / (double)plot.Width, 0, 1);
        int anchor = vs + (int)(rel * vn);
        int nn = (int)Math.Round(vn * (e.Delta > 0 ? 0.8 : 1.25));
        nn = Math.Clamp(nn, Math.Min(10, total), total);
        if (nn == vn) return;
        _zn = nn >= total ? 0 : nn;
        _zs = Math.Clamp(anchor - (int)(rel * nn), 0, total - nn);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _panX = -1; }

    // 指住某條 K 線（或者線圖嗰點）先顯示資料框
    private bool _hit;
    // 上次畫圖嘅座標（OnMouseMove 判斷指住冇用）
    private double _pLo, _pHi;
    private Rectangle _pRect;
    private float _pSlot;

    public void SetError(string msg) { _error = msg; _d = null; Invalidate(); }

    /// <summary>即時推送嘅新價：更新最後一條 K 線，或者過咗一格時間就加一條新嘅。</summary>
    public void ApplyTick(double price, DateTime timeUtc)
    {
        var d = _d;
        if (d == null || d.Candles.Count == 0 || double.IsNaN(price) || d.IntervalMin <= 0) return;
        var c = d.Candles;
        var last = c[^1];
        var local = timeUtc.AddSeconds(d.GmtOffset); // 同走勢圖一樣用交易所當地時間
        bool sameBar;
        DateTime bucket = last.Time;
        if (d.IntervalMin < 1440)
        {
            long ticks = TimeSpan.FromMinutes(d.IntervalMin).Ticks;
            bucket = new DateTime(local.Ticks / ticks * ticks);
            sameBar = bucket <= last.Time;
            if (!sameBar && (local - last.Time).TotalHours > 12) return; // 隔咗成晚：等下次重新攞圖
        }
        else sameBar = true; // 日線 / 週線：只更新最後一條
        if (sameBar) c[^1] = last with { Close = price, High = Math.Max(last.High, price), Low = Math.Min(last.Low, price) };
        else
        {
            bool atEnd = Zoomed && View().Start + _zn >= c.Count;
            c.Add(new Candle(bucket, last.Close, Math.Max(last.Close, price), Math.Min(last.Close, price), price, 0));
            if (atEnd) _zs++; // 放大緊又睇緊最新：跟住向右移
        }
        Invalidate();
    }

    private int TopH => Ui.S(40);
    private int AxisW => Ui.S(72);
    private int BottomH => Ui.S(24);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var plot = Plot();
        // 拖動：放大咗之後左右移
        if (_panX >= 0 && e.Button == MouseButtons.Left && _d != null)
        {
            // 左右（放大咗先得）
            if (Zoomed)
            {
                var (_, vn0) = View();
                int shift = (int)Math.Round((_panX - e.X) / (double)plot.Width * vn0);
                _zs = Math.Clamp(_panStart + shift, 0, _d.Candles.Count - vn0);
            }
            // 上下：向下拖 = 睇上面嘅價錢
            if (_pRect.Height > 0) _yShift = Math.Clamp(_panYShift + (e.Y - _panY) / (double)_pRect.Height, -5, 5);
            Cursor = Cursors.SizeAll;
            Invalidate();
            return;
        }
        Cursor = _buttons.Any(b => b.R.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
        int h = -1;
        bool hit = false;
        var (vs, vn) = View();
        if (_d != null && vn > 0 && plot.Contains(e.Location))
        {
            h = Math.Clamp((int)Math.Round((e.X - plot.X) / (double)plot.Width * vn - 0.5), 0, vn - 1);
            // 判斷係咪真係指住條 K 線（高低之間）或者線圖嗰點附近
            if (_pRect.Height > 0 && _pHi > _pLo)
            {
                var k = _d.Candles[vs + h];
                float Y(double p) => _pRect.Bottom - (float)((p - _pLo) / (_pHi - _pLo)) * _pRect.Height;
                float cx = _pRect.X + (h + 0.5f) * _pSlot;
                float halfW = Math.Max(_pSlot * 0.4f, Ui.S(4));
                float tol = Ui.S(5);
                bool candle = StockPrefs.Style is ChartStyle.Candle or ChartStyle.Hollow or ChartStyle.Ohlc;
                hit = Math.Abs(e.X - cx) <= halfW + (candle ? 0 : tol) &&
                      (candle ? e.Y >= Y(k.High) - tol && e.Y <= Y(k.Low) + tol
                              : Math.Abs(e.Y - Y(k.Close)) <= Ui.S(10));
            }
        }
        if (h != _hover || hit != _hit) { _hover = h; _hit = hit; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        foreach (var (r, click) in _buttons) if (r.Contains(e.Location)) { click(); return; }
        if (e.Button == MouseButtons.Left && _d != null && Plot().Contains(e.Location)) { _panX = e.X; _panY = e.Y; _panStart = View().Start; _panYShift = _yShift; }
    }

    private Rectangle Plot() => new(Ui.S(10), TopH + Ui.S(6), Math.Max(10, ClientSize.Width - AxisW - Ui.S(10)), Math.Max(10, ClientSize.Height - TopH - BottomH - Ui.S(6)));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Input);
        Ui.Smooth(g);
        DrawToolbar(g);
        var plot = Plot();
        if (_d == null || _d.Candles.Count == 0)
        {
            string t = _loading ? Lang.T("載入緊…") : _error != null ? "⚠ " + _error : _d != null ? Lang.T("呢段時間冇數據") : "";
            TextRenderer.DrawText(g, t, Font, plot, _error != null ? Ui.Danger : Ui.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        var (vStart, vCount) = View();
        var c = vCount == _d.Candles.Count ? _d.Candles : _d.Candles.GetRange(vStart, vCount);
        int n = c.Count;
        // 價格區 80%，成交量區 20%
        int volH = StockPrefs.ShowVolume ? (int)(plot.Height * 0.2f) : 0;
        var price = new Rectangle(plot.X, plot.Y, plot.Width, plot.Height - volH - Ui.S(8));
        var vol = new Rectangle(plot.X, price.Bottom + Ui.S(8), plot.Width, volH);
        double lo = c.Min(x => x.Low), hi = c.Max(x => x.High);
        if (_d.Intraday && _d.PrevClose > 0) { lo = Math.Min(lo, _d.PrevClose); hi = Math.Max(hi, _d.PrevClose); }
        double padv = (hi - lo) * 0.06; if (padv <= 0) padv = Math.Max(hi * 0.01, 0.01);
        lo -= padv; hi += padv;
        double shiftV = _yShift * (hi - lo);
        lo += shiftV; hi += shiftV;
        float slot = price.Width / (float)n;
        float X(int i) => price.X + (i + 0.5f) * slot;
        float Y(double p) => price.Bottom - (float)((p - lo) / (hi - lo)) * price.Height;
        _pLo = lo; _pHi = hi; _pRect = price; _pSlot = slot;

        // 格線同右邊價錢
        using (var grid = new Pen(Ui.Alpha(Ui.Border, 110)))
        {
            for (int k = 0; k <= 4; k++)
            {
                double p = lo + (hi - lo) * k / 4;
                float y = Y(p);
                g.DrawLine(grid, price.X, y, price.Right, y);
                TextRenderer.DrawText(g, StockWatch.Fmt(p), Font, new Rectangle(price.Right + Ui.S(6), (int)y - Ui.S(9), AxisW - Ui.S(6), Ui.S(18)), Ui.TextFaint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            // 底部日期（大約 5 個）
            int step = Math.Max(1, n / 5);
            for (int i = 0; i < n; i += step)
            {
                string lab = c[i].Time.ToString(_d.Intraday ? (Yahoo.Ranges[RangeIndex].Range == "1d" ? "HH:mm" : "MM-dd HH:mm") : RangeIndex >= 5 ? "yyyy-MM" : "MM-dd");
                TextRenderer.DrawText(g, lab, Font, new Rectangle((int)X(i) - Ui.S(50), vol.Bottom + Ui.S(4), Ui.S(100), Ui.S(18)), Ui.TextFaint, TextFormatFlags.HorizontalCenter);
            }
        }
        // 我嘅買入價：喺範圍內就畫虛線，唔喺就喺頂 / 底提示
        if (_cost is { } cost)
        {
            var gold = Color.FromArgb(0xF5, 0xB7, 0x01);
            string lab = Lang.F("我嘅買入價 {0}", StockWatch.Fmt(cost));
            if (cost >= lo && cost <= hi)
            {
                float y = Y(cost);
                using (var pen = new Pen(gold, 1.3f) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, price.X, y, price.Right, y);
                TextRenderer.DrawText(g, lab, Font, new Rectangle(price.Right - Ui.S(260), (int)y - Ui.S(18), Ui.S(256), Ui.S(16)), gold, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }
            else
            {
                bool above = cost > hi;
                TextRenderer.DrawText(g, (above ? "↑ " : "↓ ") + lab, Font,
                    new Rectangle(price.Right - Ui.S(300), above ? price.Y + Ui.S(2) : price.Bottom - Ui.S(18), Ui.S(296), Ui.S(16)), gold, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }
        }
        // 昨收虛線
        if (_d.Intraday && _d.PrevClose > 0)
            using (var dash = new Pen(Ui.Alpha(Ui.TextFaint, 160), 1f) { DashStyle = DashStyle.Dash })
            {
                float y = Y(_d.PrevClose);
                g.DrawLine(dash, price.X, y, price.Right, y);
                TextRenderer.DrawText(g, Lang.T("昨收"), Font, new Rectangle(price.X + Ui.S(4), (int)y - Ui.S(18), Ui.S(60), Ui.S(16)), Ui.TextFaint, TextFormatFlags.Left);
            }

        double basePrice = _d.Intraday && _d.PrevClose > 0 ? _d.PrevClose : c[0].Close;
        Color main = c[^1].Close >= basePrice ? StockPrefs.Up : StockPrefs.Down;
        var style = StockPrefs.Style;
        if (style is ChartStyle.Candle or ChartStyle.Hollow or ChartStyle.Ohlc)
        {
            float bw = Math.Max(1f, slot * 0.65f);
            for (int i = 0; i < n; i++)
            {
                var k = c[i];
                bool up = k.Close >= k.Open;
                Color col = up ? StockPrefs.Up : StockPrefs.Down;
                using var p = new Pen(col, style == ChartStyle.Ohlc ? Ui.S(1.4f) : 1f);
                float top = Y(Math.Max(k.Open, k.Close)), bot = Y(Math.Min(k.Open, k.Close));
                if (style == ChartStyle.Ohlc)
                {
                    // 美國線：高低一條直線，左邊短橫 = 開市，右邊短橫 = 收市
                    g.DrawLine(p, X(i), Y(k.High), X(i), Y(k.Low));
                    g.DrawLine(p, X(i) - bw / 2, Y(k.Open), X(i), Y(k.Open));
                    g.DrawLine(p, X(i), Y(k.Close), X(i) + bw / 2, Y(k.Close));
                    continue;
                }
                g.DrawLine(p, X(i), Y(k.High), X(i), top);
                g.DrawLine(p, X(i), bot, X(i), Y(k.Low));
                if (style == ChartStyle.Hollow && up)
                {
                    // 空心 K 線：升嘅只畫框
                    using (var bg = new SolidBrush(Ui.Input)) g.FillRectangle(bg, X(i) - bw / 2, top, bw, Math.Max(1f, bot - top));
                    g.DrawRectangle(p, X(i) - bw / 2, top, bw, Math.Max(1f, bot - top));
                }
                else
                {
                    using var b = new SolidBrush(col);
                    g.FillRectangle(b, X(i) - bw / 2, top, bw, Math.Max(1f, bot - top));
                }
            }
        }
        else
        {
            var pts = c.Select((k, i) => new PointF(X(i), Y(k.Close))).ToArray();
            if (pts.Length > 1)
            {
                if (style == ChartStyle.Area)
                    using (var path = new GraphicsPath())
                    {
                        path.AddLines(pts);
                        path.AddLine(pts[^1], new PointF(pts[^1].X, price.Bottom));
                        path.AddLine(new PointF(pts[^1].X, price.Bottom), new PointF(pts[0].X, price.Bottom));
                        path.CloseFigure();
                        using var lg = new LinearGradientBrush(new RectangleF(price.X, price.Y, price.Width, price.Height + 1), Ui.Alpha(main, 70), Ui.Alpha(main, 0), 90f);
                        g.FillPath(lg, path);
                    }
                using var pen = new Pen(main, Ui.S(1.8f));
                g.DrawLines(pen, pts);
            }
        }
        // 移動平均線
        void Ma(int len, Color col)
        {
            if (n < len) return;
            var pts = new List<PointF>();
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += c[i].Close;
                if (i >= len) sum -= c[i - len].Close;
                if (i >= len - 1) pts.Add(new PointF(X(i), Y(sum / len)));
            }
            if (pts.Count > 1) using (var p = new Pen(col, Ui.S(1.3f))) g.DrawLines(p, pts.ToArray());
        }
        if (_ma20) Ma(20, Color.FromArgb(0xF5, 0xA6, 0x23));
        if (_ma50) Ma(50, Color.FromArgb(0xB3, 0x7F, 0xEB));

        // 成交量
        double vmax = c.Max(x => x.Volume);
        if (vmax > 0 && volH > 0)
            for (int i = 0; i < n; i++)
            {
                float h = (float)(c[i].Volume / vmax * vol.Height);
                Color col = c[i].Close >= c[i].Open ? StockPrefs.Up : StockPrefs.Down;
                using var b = new SolidBrush(Ui.Alpha(col, 110));
                g.FillRectangle(b, X(i) - Math.Max(1f, slot * 0.65f) / 2, vol.Bottom - h, Math.Max(1f, slot * 0.65f), h);
            }

        // 滑鼠指住：淨係畫條幼直線（唔擋嘢）
        if (_hover >= 0 && _hover < n)
            using (var cross = new Pen(Ui.Alpha(Ui.Text, 90), 1f) { DashStyle = DashStyle.Dot })
                g.DrawLine(cross, X(_hover), price.Y, X(_hover), vol.Bottom);
        // 指住條 K 線：十字線 + 資料框
        int pinRel = _hit ? _hover : -1;
        if (pinRel >= 0 && pinRel < n)
        {
            var k = c[pinRel];
            float x = X(pinRel), y = Y(k.Close);
            using (var cross = new Pen(Ui.Alpha(Ui.Text, 120), 1f) { DashStyle = DashStyle.Dot })
            {
                g.DrawLine(cross, x, price.Y, x, vol.Bottom);
                g.DrawLine(cross, price.X, y, price.Right, y);
            }
            using (var dot = new SolidBrush(main)) g.FillEllipse(dot, x - Ui.S(3.5f), y - Ui.S(3.5f), Ui.S(7f), Ui.S(7f));
            double chg = k.Close - basePrice, pct = basePrice != 0 ? chg / basePrice * 100 : 0;
            string sign = chg >= 0 ? "+" : "";
            string[] lines =
            {
                k.Time.ToString(_d.Intraday ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd"),
                Lang.F("開 {0}  高 {1}", StockWatch.Fmt(k.Open), StockWatch.Fmt(k.High)),
                Lang.F("低 {0}  收 {1}", StockWatch.Fmt(k.Low), StockWatch.Fmt(k.Close)),
                $"{sign}{StockWatch.Fmt(chg)} ({sign}{pct:0.00}%)",
                Lang.F("成交量 {0}", StockWatch.Vol(k.Volume)),
            };
            int bw = Ui.S(190), lh = Ui.S(18), bh = lh * lines.Length + Ui.S(12);
            int bx = x + Ui.S(14) + bw > price.Right ? (int)x - bw - Ui.S(14) : (int)x + Ui.S(14);
            var box = new Rectangle(bx, price.Y + Ui.S(4), bw, bh);
            using (var bp = Ui.Round(box, Ui.S(8f)))
            {
                using (var b = new SolidBrush(Ui.Alpha(Ui.CardHi, 235))) g.FillPath(b, bp);
                using (var p = new Pen(Ui.Border)) g.DrawPath(p, bp);
            }
            for (int i = 0; i < lines.Length; i++)
                TextRenderer.DrawText(g, lines[i], Font, new Rectangle(box.X + Ui.S(10), box.Y + Ui.S(6) + i * lh, bw - Ui.S(16), lh),
                    i == 3 ? (chg >= 0 ? StockPrefs.Up : StockPrefs.Down) : i == 0 ? Ui.Text : Ui.TextDim, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>上面一行：時間範圍、線 / K 線、MA20 / MA50。</summary>
    private void DrawToolbar(Graphics g)
    {
        _buttons.Clear();
        int x = Ui.S(10), y = Ui.S(8), h = Ui.S(26);
        void Chip(string text, bool on, Action click, Color? onCol = null)
        {
            int w = TextRenderer.MeasureText(text, Font).Width + Ui.S(18);
            var r = new Rectangle(x, y, w, h);
            using (var p = Ui.Round(r, h / 2f))
            {
                using (var b = new SolidBrush(on ? (onCol ?? Ui.Accent) : Ui.Alpha(Ui.CardHi, 200))) g.FillPath(b, p);
            }
            TextRenderer.DrawText(g, text, Font, r, on ? Color.White : Ui.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            _buttons.Add((r, click));
            x += w + Ui.S(6);
        }
        for (int i = 0; i < Yahoo.Ranges.Length; i++)
        {
            int idx = i;
            Chip(Lang.T(Yahoo.Ranges[i].Label), RangeIndex == i, () => { if (RangeIndex == idx) return; RangeIndex = idx; SavePrefs(); Invalidate(); RangeChanged?.Invoke(); });
        }
        x += Ui.S(14);
        bool lineFamily = StockPrefs.Style is ChartStyle.Area or ChartStyle.Line;
        Chip(Lang.T("線圖"), lineFamily, () => { if (!lineFamily) { StockPrefs.Style = ChartStyle.Area; StockPrefs.Save(); } });
        Chip(Lang.T("K 線"), !lineFamily, () => { if (lineFamily) { StockPrefs.Style = ChartStyle.Candle; StockPrefs.Save(); } });
        x += Ui.S(14);
        Chip("MA20", _ma20, () => { _ma20 = !_ma20; SavePrefs(); Invalidate(); }, Color.FromArgb(0xD9, 0x8E, 0x10));
        Chip("MA50", _ma50, () => { _ma50 = !_ma50; SavePrefs(); Invalidate(); }, Color.FromArgb(0x8E, 0x5C, 0xD6));
        // 放大咗：顯示「還原」
        if (Moved_) { x += Ui.S(14); Chip(Lang.T("⤢ 還原"), false, () => { _zn = 0; _zs = 0; _yShift = 0; Invalidate(); }); }
    }


}

/// <summary>到價提醒：升到 / 跌到幾多就通知（留空 = 唔提醒）。</summary>
internal sealed class StockAlertForm : Form
{
    private readonly TextBox _above, _below;
    public double? Above { get; private set; }
    public double? Below { get; private set; }
    public double? Pct { get; private set; }
    private readonly TextBox _pct;

    public StockAlertForm(StockItem it, StockQuote? q)
    {
        Text = Lang.F("到價提醒：{0}", it.Symbol);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.F(9.75f);
        try { DarkTheme.DarkTitleBar(this); } catch { }
        int x = Ui.S(16), w = Ui.S(340), y = Ui.S(14);
        Label L(string t, Color c, Font f)
        {
            var l = Ui.Text_(t, c, f, new Padding(0));
            l.UseMnemonic = false;
            l.MaximumSize = new Size(w, 0);
            l.AutoSize = true;
            l.Location = new Point(x, y);
            Controls.Add(l);
            y += l.GetPreferredSize(new Size(w, 0)).Height + Ui.S(8);
            return l;
        }
        L(it.Symbol + "  " + (q?.Name ?? it.Name), Ui.Text, Ui.F(11f, FontStyle.Bold));
        if (q != null) L(Lang.F("現價：{0} {1}", StockWatch.Fmt(q.Price), q.Currency), Ui.TextDim, Ui.F(9.5f));
        TextBox Field(string label, double? v)
        {
            L(label, Ui.TextDim, Ui.F(9.25f));
            y -= Ui.S(4);
            var tb = new TextBox { Text = v?.ToString(CultureInfo.InvariantCulture) ?? "", BackColor = Ui.Input, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle, Font = Ui.F(10f),
                Bounds = new Rectangle(x, y, Ui.S(180), Ui.S(28)) };
            Controls.Add(tb);
            y += Ui.S(38);
            return tb;
        }
        _above = Field(Lang.T("升到或者高過（留空 = 唔提醒）"), it.Above);
        _below = Field(Lang.T("跌到或者低過（留空 = 唔提醒）"), it.Below);
        _pct = Field(Lang.T("單日升跌超過幾多 %（例如 5，留空 = 唔提醒）"), it.PctAlert);
        L(Lang.T("到價會彈 Windows 通知：價錢提醒響一次就自動取消；% 提醒每日最多響一次，會一直保留。要程式開住先會提醒（縮細去系統匣都得）。"), Ui.TextFaint, Ui.F(8.75f));
        y += Ui.S(6);
        var ok = new FancyButton { Text = Lang.T("儲存"), Kind = BtnKind.Primary, Font = Ui.F(9.75f), Size = new Size(Ui.S(90), Ui.S(34)) };
        var cancel = new FancyButton { Text = Lang.T("取消"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(84), Ui.S(34)) };
        cancel.Location = new Point(x + w - cancel.Width, y);
        ok.Location = new Point(cancel.Left - Ui.S(10) - ok.Width, y);
        ok.Click += (_, _) =>
        {
            if (!Parse(_above.Text, out var a) || !Parse(_below.Text, out var b) || !Parse(_pct.Text.Replace("%", ""), out var pc)) { MessageBox.Show(this, Lang.T("請輸入數字（例如 185.5），或者留空。"), Program.AppName); return; }
            Above = a; Below = b; Pct = pc;
            DialogResult = DialogResult.OK;
            Close();
        };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(ok);
        Controls.Add(cancel);
        ClientSize = new Size(w + x * 2, y + Ui.S(50));
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static bool Parse(string s, out double? v)
    {
        v = null;
        s = s.Trim().Replace(",", "");
        if (s.Length == 0) return true;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0) { v = d; return true; }
        return false;
    }
}

/// <summary>設定我嘅買入價（每股平均成本）同股數（可以留空）。</summary>
internal sealed class StockCostForm : Form
{
    private readonly TextBox _cost, _shares;
    public double? Cost { get; private set; }
    public double? Shares { get; private set; }

    public StockCostForm(StockItem it, StockQuote? q)
    {
        Text = Lang.F("我嘅買入價：{0}", it.Symbol);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.F(9.75f);
        try { DarkTheme.DarkTitleBar(this); } catch { }
        int x = Ui.S(16), w = Ui.S(340), y = Ui.S(14);
        void L(string t, Color c, Font f)
        {
            var l = Ui.Text_(t, c, f, new Padding(0));
            l.UseMnemonic = false;
            l.MaximumSize = new Size(w, 0);
            l.AutoSize = true;
            l.Location = new Point(x, y);
            Controls.Add(l);
            y += l.GetPreferredSize(new Size(w, 0)).Height + Ui.S(8);
        }
        L(it.Symbol + "  " + (q?.Name ?? it.Name), Ui.Text, Ui.F(11f, FontStyle.Bold));
        if (q != null) L(Lang.F("現價：{0} {1}", StockWatch.Fmt(q.Price), q.Currency), Ui.TextDim, Ui.F(9.5f));
        TextBox Field(string label, double? v)
        {
            L(label, Ui.TextDim, Ui.F(9.25f));
            y -= Ui.S(4);
            var tb = new TextBox { Text = v?.ToString(CultureInfo.InvariantCulture) ?? "", BackColor = Ui.Input, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle, Font = Ui.F(10f),
                Bounds = new Rectangle(x, y, Ui.S(180), Ui.S(28)) };
            Controls.Add(tb);
            y += Ui.S(38);
            return tb;
        }
        _cost = Field(Lang.T("買入價（每股平均成本）"), it.Cost);
        _shares = Field(Lang.T("股數（可以留空：留空就只計 %）"), it.Shares);
        L(Lang.T("只係用嚟計你賺蝕幾多，唔會連接任何券商，亦唔會買賣。全部清空就等於刪除買入價。"), Ui.TextFaint, Ui.F(8.75f));
        y += Ui.S(6);
        var ok = new FancyButton { Text = Lang.T("儲存"), Kind = BtnKind.Primary, Font = Ui.F(9.75f), Size = new Size(Ui.S(90), Ui.S(34)) };
        var cancel = new FancyButton { Text = Lang.T("取消"), Kind = BtnKind.Ghost, Font = Ui.F(9.75f), Size = new Size(Ui.S(84), Ui.S(34)) };
        cancel.Location = new Point(x + w - cancel.Width, y);
        ok.Location = new Point(cancel.Left - Ui.S(10) - ok.Width, y);
        ok.Click += (_, _) =>
        {
            if (!Parse(_cost.Text, out var c) || !Parse(_shares.Text, out var s)) { MessageBox.Show(this, Lang.T("請輸入數字（例如 185.5），或者留空。"), Program.AppName); return; }
            Cost = c; Shares = s;
            DialogResult = DialogResult.OK;
            Close();
        };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(ok);
        Controls.Add(cancel);
        ClientSize = new Size(w + x * 2, y + Ui.S(50));
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static bool Parse(string s, out double? v)
    {
        v = null;
        s = s.Trim().Replace(",", "").Replace("$", "");
        if (s.Length == 0) return true;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0) { v = d; return true; }
        return false;
    }
}

internal enum ChartStyle { Area, Line, Candle, Hollow, Ohlc }

/// <summary>股票顯示設定：升跌顏色、圖表款式、成交量（存喺 data\stocks\prefs.txt）。</summary>
internal static class StockPrefs
{
    private static string PathFile => System.IO.Path.Combine(AppPaths.Data, "stocks", "prefs.txt");
    public static bool RedUp;
    public static ChartStyle Style = ChartStyle.Area;
    public static bool ShowVolume = true;
    public static bool HidePresets;
    public static event Action? Changed;

    /// <summary>升 / 跌嘅顏色（紅升綠跌模式會反轉）。</summary>
    public static Color Up => RedUp ? Ui.Danger : Ui.Success;
    public static Color Down => RedUp ? Ui.Success : Ui.Danger;

    static StockPrefs()
    {
        try
        {
            if (!System.IO.File.Exists(PathFile)) return;
            foreach (var line in System.IO.File.ReadAllLines(PathFile))
            {
                var kv = line.Split('=', 2);
                if (kv.Length < 2) continue;
                switch (kv[0].Trim())
                {
                    case "redUp": RedUp = kv[1].Trim() == "1"; break;
                    case "style": if (Enum.TryParse<ChartStyle>(kv[1].Trim(), out var cs)) Style = cs; break;
                    case "volume": ShowVolume = kv[1].Trim() != "0"; break;
                    case "hidePresets": HidePresets = kv[1].Trim() == "1"; break;
                }
            }
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);
            System.IO.File.WriteAllLines(PathFile, new[] { "redUp=" + (RedUp ? 1 : 0), "style=" + Style, "volume=" + (ShowVolume ? 1 : 0), "hidePresets=" + (HidePresets ? 1 : 0) });
        }
        catch { }
        Changed?.Invoke();
    }
}

/// <summary>走勢圖上面嘅標題：幾段文字，每段自己嘅顏色（例如升跌綠 / 紅、我嘅回報賺 / 蝕色）。</summary>
internal sealed class StockTitle : Control
{
    private List<(string Text, Color Color)> _parts = new();

    public StockTitle()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = Ui.S(36);
        Font = Ui.F(12f, FontStyle.Bold);
        BackColor = Ui.Card;
    }

    public void SetText(string t) => SetParts(new() { (t, Ui.Text) });
    public void SetParts(List<(string, Color)> parts) { _parts = parts; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Clear(g, this, Parent?.BackColor ?? Ui.Card);
        int x = 0;
        var f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        foreach (var (t, c) in _parts)
        {
            int w = TextRenderer.MeasureText(g, t, Font, Size.Empty, f).Width;
            if (x >= ClientSize.Width) break;
            TextRenderer.DrawText(g, t, Font, new Rectangle(x, 0, ClientSize.Width - x, ClientSize.Height), c, f | TextFormatFlags.EndEllipsis);
            x += w + Ui.S(16);
        }
    }
}

/// <summary>股票設定視窗：改咗即刻生效同儲存。</summary>
internal sealed class StockSettingsForm : Form
{
    public StockSettingsForm()
    {
        Text = Lang.T("股票設定");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        AutoScaleMode = AutoScaleMode.None;
        Font = Ui.F(9.75f);
        try { DarkTheme.DarkTitleBar(this); } catch { }
        int x = Ui.S(18), w = Ui.S(320), y = Ui.S(14);

        void Head(string t)
        {
            var l = Ui.Text_(t, Ui.Text, Ui.F(10.5f, FontStyle.Bold), new Padding(0));
            l.Location = new Point(x, y);
            Controls.Add(l);
            y += Ui.S(28);
        }
        RadioButton Radio(string t, bool on, Action pick, Control group)
        {
            var r = new RadioButton { Text = t, Checked = on, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, Font = Ui.F(9.75f), Location = new Point(Ui.S(8), group.Controls.Count * Ui.S(28)) };
            r.CheckedChanged += (_, _) => { if (r.Checked) { pick(); StockPrefs.Save(); } };
            group.Controls.Add(r);
            return r;
        }
        Panel Group(int rows)
        {
            var p = new Panel { Location = new Point(x, y), Size = new Size(w, rows * Ui.S(28)), BackColor = Ui.Card };
            Controls.Add(p);
            y += p.Height + Ui.S(12);
            return p;
        }

        Head(Lang.T("升跌顏色"));
        var g1 = Group(2);
        Radio(Lang.T("綠升紅跌（美股、加股習慣）"), !StockPrefs.RedUp, () => StockPrefs.RedUp = false, g1);
        Radio(Lang.T("紅升綠跌（港股、A 股習慣）"), StockPrefs.RedUp, () => StockPrefs.RedUp = true, g1);

        Head(Lang.T("圖表款式"));
        var g2 = Group(5);
        (string, ChartStyle)[] styles =
        {
            (Lang.T("面積圖"), ChartStyle.Area), (Lang.T("線圖"), ChartStyle.Line), (Lang.T("K 線（蠟燭圖）"), ChartStyle.Candle),
            (Lang.T("空心 K 線"), ChartStyle.Hollow), (Lang.T("美國線（OHLC）"), ChartStyle.Ohlc),
        };
        foreach (var (t, cs) in styles) { var c = cs; Radio(t, StockPrefs.Style == cs, () => StockPrefs.Style = c, g2); }

        Head(Lang.T("其他"));
        var vol = new CheckBox { Text = Lang.T("顯示成交量"), Checked = StockPrefs.ShowVolume, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, Location = new Point(x + Ui.S(8), y) };
        vol.CheckedChanged += (_, _) => { StockPrefs.ShowVolume = vol.Checked; StockPrefs.Save(); };
        Controls.Add(vol);
        y += Ui.S(30);
        var hide = new CheckBox { Text = Lang.T("隱藏預設股票"), Checked = StockPrefs.HidePresets, AutoSize = true, ForeColor = Ui.Text, BackColor = Ui.Card, Location = new Point(x + Ui.S(8), y) };
        hide.CheckedChanged += (_, _) => { StockPrefs.HidePresets = hide.Checked; StockPrefs.Save(); };
        Controls.Add(hide);
        y += Ui.S(26);
        var hideTip = Ui.Text_(Lang.T("第一次用時自動加入嗰幾隻（AAPL、騰訊、BTC 等）唔顯示；打咗星、設咗買入價或者提醒嘅照樣顯示。"), Ui.TextFaint, Ui.F(8.5f), new Padding(0));
        hideTip.MaximumSize = new Size(w - Ui.S(28), 0);
        hideTip.AutoSize = true;
        hideTip.Location = new Point(x + Ui.S(28), y);
        Controls.Add(hideTip);
        y += hideTip.GetPreferredSize(new Size(w - Ui.S(28), 0)).Height + Ui.S(12);
        var tip = Ui.Text_(Lang.T("走勢圖：滑鼠指住一條 K 線先會顯示詳細數據；滾輪放大縮細；撳住拖可以上下睇（放大咗仲可以左右睇）；撳「⤢ 還原」返去原本。"), Ui.TextFaint, Ui.F(8.75f), new Padding(0));
        tip.MaximumSize = new Size(w, 0);
        tip.AutoSize = true;
        tip.Location = new Point(x, y);
        Controls.Add(tip);
        y += tip.GetPreferredSize(new Size(w, 0)).Height + Ui.S(14);

        var close = new FancyButton { Text = Lang.T("關閉"), Kind = BtnKind.Primary, Font = Ui.F(9.75f), Size = new Size(Ui.S(90), Ui.S(34)) };
        close.Location = new Point(x + w - close.Width, y);
        close.Click += (_, _) => Close();
        Controls.Add(close);
        ClientSize = new Size(w + x * 2, y + Ui.S(50));
        AcceptButton = close;
        CancelButton = close;
    }
}
