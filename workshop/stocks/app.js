// 股票（工作坊工具）：自選股、走勢圖、買入價計賺蝕、到價提醒
// • 數據來自 Yahoo Finance 公開接口（唔使註冊；可能延遲）
// • 每 30 秒攞一次報價；試埋 WebSocket 即時推送，連唔到就照用輪詢
// • 插件喺側邊欄就會喺背景運行，到價提醒唔使開住呢頁
'use strict';

const T = {
  zh: {
    search: '輸入公司名或者代號，例如 Apple、0700.HK、SHOP.TO、BTC-USD', hot: '🔥 熱門', starOnly: '☆ 只睇標記', settings: '設定',
    idx: '指數', us: '美股', hk: '港股', ca: '加股', crypto: '加密貨幣', other: '其他', addAll: '全部加入',
    ranges: ['1日', '5日', '1個月', '6個月', '1年', '5年'], reset: '⤢ 還原', resetTip: '還原縮放同位置',
    star: '★ 標記', unstar: '☆ 取消標記', cost: '💰 買入價', costMenu: '💰 設定我嘅買入價…', alert: '🔔 到價提醒', alertMenu: '🔔 到價提醒…',
    yahoo: '↗ Yahoo', yahooMenu: '喺 Yahoo Finance 網頁開', remove: '移除',
    empty: '未有自選股：喺上面輸入公司名或者代號，撳 Enter 加入；或者撳「🔥 熱門」。',
    emptyStar: '未有標記嘅股票：撳股票左邊嘅 ☆ 標記想特別留意嘅。',
    emptyHidden: '預設股票已經隱藏：喺上面搜尋或者撳「🔥 熱門」加入你想睇嘅。',
    pick: '撳左邊一隻股票睇走勢圖', loading: '載入緊…', noData: '呢段時間冇數據', searching: '搜尋緊…', noHit: '搵唔到', searchFail: '搜尋失敗：',
    note: '數據來自 Yahoo Finance，可能有延遲，只供參考，唔係投資建議。走勢圖：滑鼠指住 K 線先會顯示詳細數據；滾輪放大縮細；撳住拖可以上下睇（放大咗仲可以左右睇）。',
    updated: '更新：', live: '即時推送', poll: '每 30 秒更新', fetching: '攞緊報價…',
    open: '開', high: '高', low: '低', close: '收', change: '升跌', volume: '成交量', prevClose: '昨收', costLine: '買入價',
    mine: '我嘅回報：', perShare: '每股', total: '💰 總賺蝕：',
    costTitle: '💰 設定我嘅買入價', costHint: '只係用嚟計你賺蝕幾多，唔會連接任何券商，亦唔會買賣。全部清空就等於刪除買入價。',
    costLabel: '買入價（每股平均成本）', sharesLabel: '股數（可以留空：留空就只計 %）', clearCost: '清除買入價',
    alertTitle: '🔔 到價提醒', alertHint: '到價會彈 Windows 通知：價錢提醒響一次就自動取消；% 提醒每日最多響一次，會一直保留。要拉比工具箱開住先會提醒（縮細去系統匣都得）。',
    above: '升到或者高過（留空 = 唔提醒）', below: '跌到或者低過（留空 = 唔提醒）', pct: '單日升跌超過幾多 %（例如 5，留空 = 唔提醒）', clearAlert: '取消到價提醒',
    save: '儲存', cancel: '取消', closeBtn: '關閉', badNum: '請輸入數字（例如 185.5），或者留空。', alertSet: '已設定到價提醒',
    setTitle: '股票設定', colors: '升跌顏色', greenUp: '綠升紅跌（美股、加股習慣）', redUp: '紅升綠跌（港股、A 股習慣）',
    style: '圖表款式', styles: { area: '面積圖', line: '線圖', candle: 'K 線（蠟燭圖）', hollow: '空心 K 線', ohlc: '美國線（OHLC）' },
    showVol: '顯示成交量', hidePresets: '隱藏預設股票', hidePresetsSub: '第一次用時自動加入嗰幾隻（AAPL、騰訊、BTC 等）唔顯示；打咗星、設咗買入價或者提醒嘅照樣顯示。',
    notFound: '搵唔到股票代號「{0}」', quoteFail: '攞唔到「{0}」嘅報價：{1}', badResp: '報價伺服器回應唔正常（HTTP {0}）',
    nUp: '📈 {0} 升到 {1}', nDown: '📉 {0} 跌到 {1}', nUpBody: '{0} 而家 {1} {2}（提醒價：{3} 或以上）', nDownBody: '{0} 而家 {1} {2}（提醒價：{3} 或以下）',
    nPct: '{0} {1} 今日 {2}%', nPctBody: '{0} 而家 {1} {2}（提醒：單日升跌超過 {3}%）'
  },
  en: {
    search: 'Company name or symbol, e.g. Apple, 0700.HK, SHOP.TO, BTC-USD', hot: '🔥 Popular', starOnly: '☆ Starred only', settings: 'Settings',
    idx: 'Indices', us: 'US', hk: 'Hong Kong', ca: 'Canada', crypto: 'Crypto', other: 'Other', addAll: 'Add all',
    ranges: ['1D', '5D', '1M', '6M', '1Y', '5Y'], reset: '⤢ Reset', resetTip: 'Reset zoom and position',
    star: '★ Star', unstar: '☆ Unstar', cost: '💰 Cost', costMenu: '💰 Set my buy price…', alert: '🔔 Alert', alertMenu: '🔔 Price alert…',
    yahoo: '↗ Yahoo', yahooMenu: 'Open on Yahoo Finance', remove: 'Remove',
    empty: 'No stocks yet: type a company name or symbol above and press Enter, or click "🔥 Popular".',
    emptyStar: 'No starred stocks: click the ☆ next to a stock to star it.',
    emptyHidden: 'Preset stocks are hidden: search above or click "🔥 Popular" to add the ones you want.',
    pick: 'Click a stock on the left to see its chart', loading: 'Loading…', noData: 'No data for this period', searching: 'Searching…', noHit: 'No results', searchFail: 'Search failed: ',
    note: 'Data from Yahoo Finance, may be delayed. For reference only, not investment advice. Chart: hover a candle for details; scroll to zoom; drag to look up and down (and sideways when zoomed).',
    updated: 'Updated: ', live: 'Live', poll: 'Every 30 s', fetching: 'Getting quotes…',
    open: 'Open', high: 'High', low: 'Low', close: 'Close', change: 'Change', volume: 'Volume', prevClose: 'Prev close', costLine: 'Buy price',
    mine: 'My return: ', perShare: 'per share', total: '💰 Total P/L: ',
    costTitle: '💰 Set my buy price', costHint: 'Only used to work out your gain or loss. It does not connect to any broker and never trades. Clear everything to remove the buy price.',
    costLabel: 'Buy price (average cost per share)', sharesLabel: 'Shares (optional: leave empty to show % only)', clearCost: 'Clear buy price',
    alertTitle: '🔔 Price alert', alertHint: 'You get a Windows notification. Price alerts fire once and then clear; the % alert fires at most once a day and stays. Rabi Toolkit must be running (minimised to the tray is fine).',
    above: 'Rises to or above (empty = off)', below: 'Falls to or below (empty = off)', pct: 'Daily move over % (e.g. 5, empty = off)', clearAlert: 'Clear alerts',
    save: 'Save', cancel: 'Cancel', closeBtn: 'Close', badNum: 'Enter a number (e.g. 185.5) or leave it empty.', alertSet: 'Price alert set',
    setTitle: 'Stock settings', colors: 'Up / down colours', greenUp: 'Green up, red down (US / Canada style)', redUp: 'Red up, green down (Hong Kong / China style)',
    style: 'Chart style', styles: { area: 'Area', line: 'Line', candle: 'Candles', hollow: 'Hollow candles', ohlc: 'OHLC bars' },
    showVol: 'Show volume', hidePresets: 'Hide preset stocks', hidePresetsSub: 'Hides the stocks added automatically on first use (AAPL, Tencent, BTC, etc.). Ones you starred or gave a buy price or alert stay visible.',
    notFound: 'Symbol "{0}" not found', quoteFail: 'Could not get a quote for "{0}": {1}', badResp: 'The quote server sent an unexpected reply (HTTP {0})',
    nUp: '📈 {0} rose to {1}', nDown: '📉 {0} fell to {1}', nUpBody: '{0} is now {1} {2} (alert: {3} or above)', nDownBody: '{0} is now {1} {2} (alert: {3} or below)',
    nPct: '{0} {1} {2}% today', nPctBody: '{0} is now {1} {2} (alert: daily move over {3}%)'
  }
};
let L = T.en;
const F = (s, ...a) => s.replace(/\{(\d)\}/g, (_, i) => a[i]);
const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

// ---------- 預設 / 熱門 ----------
const DEFAULTS = [['AAPL', 'Apple'], ['NVDA', 'NVIDIA'], ['MSFT', 'Microsoft'], ['TSLA', 'Tesla'], ['0700.HK', 'Tencent'], ['9988.HK', 'Alibaba'],
  ['SHOP.TO', 'Shopify'], ['RY.TO', 'Royal Bank of Canada'], ['BTC-USD', 'Bitcoin'], ['ETH-USD', 'Ethereum']];
const POPULAR = [
  ['idx', [['^GSPC', 'S&P 500'], ['^IXIC', 'Nasdaq'], ['^DJI', 'Dow Jones'], ['^HSI', 'Hang Seng Index'], ['^GSPTSE', 'S&P/TSX Composite']]],
  ['us', [['AAPL', 'Apple'], ['MSFT', 'Microsoft'], ['NVDA', 'NVIDIA'], ['AMZN', 'Amazon'], ['GOOGL', 'Alphabet (Google)'], ['META', 'Meta'], ['TSLA', 'Tesla'], ['AMD', 'AMD'], ['NFLX', 'Netflix'], ['SPY', 'SPDR S&P 500 ETF'], ['QQQ', 'Invesco QQQ (Nasdaq 100)']]],
  ['hk', [['0700.HK', 'Tencent'], ['9988.HK', 'Alibaba'], ['3690.HK', 'Meituan'], ['1810.HK', 'Xiaomi'], ['0005.HK', 'HSBC'], ['1299.HK', 'AIA'], ['0388.HK', 'HKEX'], ['0941.HK', 'China Mobile'], ['9618.HK', 'JD.com'], ['2800.HK', 'Tracker Fund of Hong Kong']]],
  ['ca', [['SHOP.TO', 'Shopify'], ['RY.TO', 'Royal Bank of Canada'], ['TD.TO', 'TD Bank'], ['BMO.TO', 'Bank of Montreal'], ['BNS.TO', 'Scotiabank'], ['ENB.TO', 'Enbridge'], ['CNR.TO', 'CN Rail'], ['SU.TO', 'Suncor'], ['CSU.TO', 'Constellation Software'], ['XIU.TO', 'iShares S&P/TSX 60 ETF']]],
  ['crypto', [['BTC-USD', 'Bitcoin'], ['ETH-USD', 'Ethereum'], ['SOL-USD', 'Solana'], ['XRP-USD', 'XRP'], ['BNB-USD', 'BNB'], ['DOGE-USD', 'Dogecoin']]]
];
const GROUPS = ['idx', 'us', 'hk', 'ca', 'crypto', 'other'];
const RANGES = [['1d', '5m', true], ['5d', '15m', true], ['1mo', '1d', false], ['6mo', '1d', false], ['1y', '1d', false], ['5y', '1wk', false]];

/** 由代號估市場：^ 開頭 = 指數、.HK = 港股、.TO / .V / .CN / .NE = 加股、XXX-USD = 加密貨幣。 */
function groupOf(sym) {
  sym = sym.toUpperCase();
  if (sym.startsWith('^')) return 'idx';
  if (sym.endsWith('.HK')) return 'hk';
  if (/\.(TO|V|CN|NE)$/.test(sym)) return 'ca';
  if (/^[A-Z0-9]+-(USD|USDT|CAD|HKD|EUR|BTC)$/.test(sym)) return 'crypto';
  if (/[.=]/.test(sym)) return 'other';
  return 'us';
}

// ---------- 狀態 ----------
let items = [];          // [{symbol, name, above, below, pct, pctFired, starred, cost, shares, userAdded}]
const quotes = {};       // symbol → {price, prev, name, cur, exch, time, gmt}
const errors = {};
let prefs = { redUp: false, style: 'area', volume: true, hidePresets: false, range: 0, ma20: false, ma50: false, collapsed: [], starOnly: false, sel: '' };
let sel = '', lastUpdate = null, chart = null, chartToken = 0, dragging = null;

const find = sym => items.find(x => x.symbol.toUpperCase() === String(sym).toUpperCase());
const hasAlert = it => it.above != null || it.below != null || it.pct != null;
const isPreset = it => !it.userAdded && !it.starred && it.cost == null && !hasAlert(it) && DEFAULTS.some(d => d[0] === it.symbol.toUpperCase());
const saveItems = () => mt.storage.set('watch', items).catch(() => {});
const savePrefs = () => mt.storage.set('prefs', prefs).catch(() => {});

// ---------- Yahoo ----------
const HDR = { 'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36', 'Accept': 'application/json' };
async function yget(url) {
  const r = await mt.net.fetch(url, { headers: HDR });
  let j = null; try { j = JSON.parse(r.text); } catch { }
  if (!j) throw new Error(F(L.badResp, r.status));
  return j;
}
function result(j, sym) {
  const err = j.chart && j.chart.error;
  if (err) throw new Error(err.code === 'Not Found' ? F(L.notFound, sym) : F(L.quoteFail, sym, err.description || err.code));
  const r = j.chart && j.chart.result && j.chart.result[0];
  if (!r) throw new Error(F(L.notFound, sym));
  return r;
}
const chartUrl = (sym, range, iv) => 'https://query1.finance.yahoo.com/v8/finance/chart/' + encodeURIComponent(sym) + '?range=' + range + '&interval=' + iv + '&includePrePost=false';
const num = v => typeof v === 'number' && isFinite(v) ? v : NaN;

/** 最新報價。 */
async function getQuote(sym) {
  const r = result(await yget(chartUrl(sym, '1d', '5m')), sym), m = r.meta || {};
  const closes = ((r.indicators && r.indicators.quote && r.indicators.quote[0] && r.indicators.quote[0].close) || []).filter(x => typeof x === 'number');
  let price = num(m.regularMarketPrice); if (isNaN(price) && closes.length) price = closes[closes.length - 1];
  let prev = num(m.chartPreviousClose); if (isNaN(prev)) prev = num(m.previousClose); if (isNaN(prev)) prev = price;
  return { price, prev, name: m.shortName || m.longName || sym, cur: m.currency || '', exch: m.fullExchangeName || m.exchangeName || '',
    time: (m.regularMarketTime || 0) * 1000 || Date.now(), gmt: m.gmtoffset || 0 };
}

/** 走勢圖數據（時間轉做交易所當地時間）。 */
async function getChart(sym, ri) {
  const [range, iv, intraday] = RANGES[ri];
  const r = result(await yget(chartUrl(sym, range, iv)), sym), m = r.meta || {}, gmt = m.gmtoffset || 0;
  const ts = r.timestamp || [], q = (r.indicators && r.indicators.quote && r.indicators.quote[0]) || {};
  const candles = [];
  for (let i = 0; i < ts.length; i++) {
    const c = num(q.close && q.close[i]); if (isNaN(c)) continue; // 冇成交嘅格
    const o = num(q.open && q.open[i]), h = num(q.high && q.high[i]), l = num(q.low && q.low[i]), v = num(q.volume && q.volume[i]);
    candles.push({ t: (ts[i] + gmt) * 1000, o: isNaN(o) ? c : o, h: isNaN(h) ? c : h, l: isNaN(l) ? c : l, c, v: isNaN(v) ? 0 : v });
  }
  let prev = num(m.chartPreviousClose); if (isNaN(prev)) prev = num(m.previousClose);
  const ivMin = iv.endsWith('m') ? parseInt(iv) : iv === '1d' ? 1440 : 10080;
  return { candles, prevClose: isNaN(prev) ? 0 : prev, intraday, iv: ivMin, range, gmt, cur: m.currency || '' };
}

async function searchYahoo(q) {
  const j = await yget('https://query2.finance.yahoo.com/v1/finance/search?q=' + encodeURIComponent(q) + '&quotesCount=10&newsCount=0&listsCount=0');
  return (j.quotes || []).filter(x => x && x.symbol && x.quoteType !== 'OPTION' && x.quoteType !== 'FUTURE')
    .map(x => ({ symbol: x.symbol, name: x.shortname || x.longname || x.symbol, exch: x.exchDisp || '', type: x.typeDisp || x.quoteType || '' }));
}

// ---------- 定時攞報價 + 到價提醒 ----------
let refreshing = false;
async function refresh() {
  if (refreshing || !items.length) return;
  refreshing = true;
  if (!lastUpdate) $('upd').textContent = L.fetching;
  try {
    const list = items.slice(); let i = 0;
    // 幾隻一齊攞，唔好太多同時
    await Promise.all([0, 1, 2, 3].map(async () => {
      while (i < list.length) {
        const it = list[i++];
        try { quotes[it.symbol] = await getQuote(it.symbol); delete errors[it.symbol]; }
        catch (e) { errors[it.symbol] = e.message; }
      }
    }));
    lastUpdate = new Date();
    checkAlerts(list);
    render();
    const q = quotes[sel]; if (q && chart && chart.d) chart.tick(q.price, q.time + q.gmt * 1000);
  } finally { refreshing = false; }
}

function checkAlerts(list) {
  let changed = false;
  const today = new Date().toISOString().slice(0, 10);
  for (const it of list) {
    const q = quotes[it.symbol]; if (!q || !isFinite(q.price)) continue;
    const name = it.name || it.symbol;
    if (it.above != null && q.price >= it.above) {
      mt.notify(F(L.nUp, it.symbol, fmt(q.price)), F(L.nUpBody, name, fmt(q.price), q.cur, fmt(it.above))).catch(() => {});
      it.above = null; changed = true; // 提醒一次就清走
    }
    if (it.below != null && q.price <= it.below) {
      mt.notify(F(L.nDown, it.symbol, fmt(q.price)), F(L.nDownBody, name, fmt(q.price), q.cur, fmt(it.below))).catch(() => {});
      it.below = null; changed = true;
    }
    // 單日升跌 %：每隻每日最多提醒一次
    const pc = q.prev ? (q.price - q.prev) / q.prev * 100 : 0;
    if (it.pct != null && it.pctFired !== today && Math.abs(pc) >= it.pct) {
      mt.notify(F(L.nPct, pc >= 0 ? '📈' : '📉', it.symbol, (pc >= 0 ? '+' : '') + pc.toFixed(2)), F(L.nPctBody, name, fmt(q.price), q.cur, it.pct)).catch(() => {});
      it.pctFired = today; changed = true;
    }
  }
  if (changed) saveItems();
}

// ---------- WebSocket 即時推送（Base64 包住嘅 Protobuf）----------
const WS = { ws: null, live: false, fails: 0, sent: new Set(), timer: 0, lastDraw: 0 };
function wsStart() {
  if (WS.ws || WS.fails >= 6 || !items.length) return;
  let ws;
  try { ws = new WebSocket('wss://streamer.finance.yahoo.com/?version=2'); } catch { WS.fails++; return; }
  WS.ws = ws; WS.sent = new Set();
  ws.onopen = () => { WS.live = true; WS.fails = 0; wsSync(); footer(); };
  ws.onmessage = e => {
    try {
      let b64 = String(e.data || '').trim();
      if (b64.startsWith('{')) { const j = JSON.parse(b64); if (j.type && j.type !== 'pricing') return; b64 = j.message || ''; }
      if (b64) onTick(decode(b64));
    } catch { } // 一條壞訊息唔影響其他
  };
  ws.onclose = () => {
    const was = WS.live; WS.ws = null; WS.live = false; if (!was) WS.fails++;
    footer();
    // 重連：越失敗等越耐（最多 1 分鐘）；連續失敗幾次就放棄，照用輪詢
    if (WS.fails < 6) setTimeout(wsStart, Math.min(60, 2 << Math.min(WS.fails, 5)) * 1000);
  };
  ws.onerror = () => { };
}
function wsSync() {
  const ws = WS.ws; if (!ws || ws.readyState !== 1) return;
  const want = new Set(items.map(x => x.symbol.toUpperCase()));
  const add = [...want].filter(s => !WS.sent.has(s)), rem = [...WS.sent].filter(s => !want.has(s));
  if (add.length) ws.send(JSON.stringify({ subscribe: add }));
  if (rem.length) ws.send(JSON.stringify({ unsubscribe: rem }));
  WS.sent = want;
}
function onTick(t) {
  if (!t.id || !isFinite(t.price)) return;
  const it = find(t.id); if (!it) return;
  const old = quotes[it.symbol];
  const prev = t.prev > 0 ? t.prev : isFinite(t.chg) ? t.price - t.chg : old ? old.prev : t.price;
  quotes[it.symbol] = Object.assign({}, old || { name: t.name || it.name, cur: t.cur, exch: '', gmt: 0 }, { price: t.price, prev, time: t.time || Date.now() });
  delete errors[it.symbol];
  lastUpdate = new Date();
  checkAlerts([it]);
  if (it.symbol === sel && chart && chart.d) chart.tick(t.price, (t.time || Date.now()) + (quotes[it.symbol].gmt || chart.d.gmt || 0) * 1000);
  // 畫面每秒最多刷新一次
  if (!WS.timer) WS.timer = setTimeout(() => { WS.timer = 0; render(); }, 1000);
}
// 1 id 2 price(float) 3 time(sint64 毫秒) 4 currency 8 changePercent(float) 9 dayVolume 12 change(float) 13 shortName 16 previousClose(float)
function decode(b64) {
  const s = atob(b64), b = new Uint8Array(s.length);
  for (let i = 0; i < s.length; i++) b[i] = s.charCodeAt(i);
  const dv = new DataView(b.buffer), td = new TextDecoder();
  const r = { id: '', price: NaN, time: 0, cur: '', pct: NaN, chg: NaN, name: '', prev: NaN };
  let i = 0;
  const varint = () => { let v = 0, m = 1, x; do { x = b[i++]; v += (x & 127) * m; m *= 128; } while (x & 128 && i < b.length); return v; };
  while (i < b.length) {
    const key = varint(), f = Math.floor(key / 8), w = key & 7;
    if (w === 0) { const v = varint(), z = v % 2 ? -(v + 1) / 2 : v / 2; if (f === 3) r.time = z; }
    else if (w === 1) i += 8;
    else if (w === 2) { const n = varint(); const str = td.decode(b.subarray(i, i + n)); i += n; if (f === 1) r.id = str; else if (f === 4) r.cur = str; else if (f === 13) r.name = str; }
    else if (w === 5) { const v = dv.getFloat32(i, true); i += 4; if (f === 2) r.price = v; else if (f === 8) r.pct = v; else if (f === 12) r.chg = v; else if (f === 16) r.prev = v; }
    else break; // 唔識嘅格式：停
  }
  return r;
}

// ---------- 左邊清單 ----------
function pnl(it, q) {
  if (it.cost == null || !(it.cost > 0) || !q || !isFinite(q.price)) return null;
  const per = q.price - it.cost;
  return { per, pct: per / it.cost * 100, total: it.shares != null ? per * it.shares : null };
}
const sign = v => (v >= 0 ? '+' : '') + fmt(v);
const cls = v => v >= 0 ? 'up' : 'down';

function render() {
  if (dragging) return;
  let list = items.slice();
  if (prefs.starOnly) list = list.filter(x => x.starred);
  if (prefs.hidePresets) list = list.filter(x => !isPreset(x));
  const box = $('list');
  if (!list.length) {
    box.innerHTML = `<div class="empty">${esc(!items.length ? L.empty : prefs.starOnly ? L.emptyStar : L.emptyHidden)}</div>`;
  } else {
    let h = '';
    for (const g of GROUPS) {
      // 同組入面：★ 標記咗嘅排最前，其他保持原本次序
      const rows = list.filter(x => groupOf(x.symbol) === g).sort((a, b) => (b.starred ? 1 : 0) - (a.starred ? 1 : 0));
      if (!rows.length) continue;
      const col = prefs.collapsed.includes(g);
      h += `<div class="grp" data-g="${g}"><span class="arw">${col ? '▸' : '▾'}</span>${esc(L[g])} <span class="faint">(${rows.length})</span></div>`;
      if (col) continue;
      for (const it of rows) {
        const q = quotes[it.symbol], err = errors[it.symbol];
        const ch = q ? q.price - q.prev : 0, pc = q && q.prev ? ch / q.prev * 100 : 0, p = pnl(it, q);
        h += `<div class="row${it.symbol === sel ? ' sel' : ''}" draggable="true" data-s="${esc(it.symbol)}" data-g="${g}">` +
          `<span class="star${it.starred ? ' on' : ''}">${it.starred ? '★' : '☆'}</span>` +
          `<div class="nm"><div class="sym">${esc(it.symbol)}${hasAlert(it) ? `<span class="bell" title="${esc(L.alertSet)}">🔔</span>` : ''}</div>` +
          `<div class="full" title="${esc(err || '')}">${esc(err ? '⚠ ' + err : (q && q.name) || it.name || '')}</div></div>` +
          `<div class="px">${q ? `<b>${fmt(q.price)}</b><span class="${cls(ch)}">${(pc >= 0 ? '+' : '') + pc.toFixed(2)}%</span>` : '<span class="faint">–</span>'}` +
          (p ? `<span class="pl ${cls(p.per)}">${p.total != null ? sign(p.total) : (p.pct >= 0 ? '+' : '') + p.pct.toFixed(2) + '%'}</span>` : '') + `</div></div>`;
      }
    }
    const top = box.scrollTop; box.innerHTML = h; box.scrollTop = top;
  }
  // 總賺蝕（按貨幣分開加）
  const sums = {};
  for (const it of items) { const q = quotes[it.symbol], p = pnl(it, q); if (p && p.total != null) sums[q.cur || '?'] = (sums[q.cur || '?'] || 0) + p.total; }
  const keys = Object.keys(sums);
  $('total').innerHTML = keys.length ? esc(L.total) + keys.map(c => `<span class="${cls(sums[c])}">${sign(sums[c])} ${esc(c)}</span>`).join(' · ') : '';
  $('total').style.display = keys.length ? '' : 'none';
  footer(); head();
}

function footer() {
  $('dot').className = 'dot' + (WS.live ? ' live' : '');
  $('upd').textContent = (lastUpdate ? L.updated + lastUpdate.toLocaleTimeString() + ' · ' : '') + (WS.live ? L.live : L.poll);
}

// ---------- 右邊：標題 + 工具列 ----------
function head() {
  const it = find(sel), q = quotes[sel];
  if (!it) { $('hName').textContent = ''; $('hPrice').innerHTML = ''; $('hMine').innerHTML = ''; return; }
  const sub = [it.symbol, q && q.exch, q && q.cur].filter(Boolean).join(' · ');
  $('hName').innerHTML = esc((q && q.name) || it.name || it.symbol) + `<small>${esc(sub)}</small>`;
  if (q && isFinite(q.price)) {
    const ch = q.price - q.prev, pc = q.prev ? ch / q.prev * 100 : 0;
    $('hPrice').innerHTML = esc(fmt(q.price)) + `<span class="${cls(ch)}">${sign(ch)} (${(pc >= 0 ? '+' : '') + pc.toFixed(2)}%)</span>`;
  } else $('hPrice').innerHTML = '<span class="faint">–</span>';
  const p = pnl(it, q);
  $('hMine').innerHTML = p ? `<span class="${cls(p.per)}">${esc(L.mine)}${(p.pct >= 0 ? '+' : '') + p.pct.toFixed(2)}% · ${sign(p.per)} ${esc(L.perShare)}` +
    (p.total != null ? ` · ${sign(p.total)} ${esc(q.cur)}` : '') + `</span> <span class="faint">(${esc(L.costLine)} ${fmt(it.cost)}${it.shares != null ? ' × ' + it.shares : ''})</span>` : '';
  if (chart) { const c = it.cost > 0 ? it.cost : null; if (chart.o.cost !== c) { chart.o.cost = c; chart.draw(); } }
  bar();
}

function bar() {
  const it = find(sel), b = $('bar');
  let h = L.ranges.map((r, i) => `<button data-r="${i}" class="${prefs.range === i ? 'on' : ''}">${esc(r)}</button>`).join('');
  h += `<span class="sep"></span><button data-ma="ma20" class="${prefs.ma20 ? 'on' : ''}">MA20</button><button data-ma="ma50" class="${prefs.ma50 ? 'on' : ''}">MA50</button>`;
  h += `<button id="resetBtn" title="${esc(L.resetTip)}" style="${chart && chart.moved ? '' : 'display:none'}">${esc(L.reset)}</button><span class="sp"></span>`;
  if (it) h += `<button data-a="star" class="${it.starred ? 'on' : ''}">${esc(it.starred ? L.unstar : L.star)}</button>` +
    `<button data-a="cost">${esc(L.cost)}</button><button data-a="alert" class="${hasAlert(it) ? 'on' : ''}">${esc(L.alert)}</button>` +
    `<button data-a="yahoo">${esc(L.yahoo)}</button>`;
  if (b.innerHTML !== h) b.innerHTML = h;
}

async function select(sym) {
  sel = sym; prefs.sel = sym; savePrefs();
  render(); await showChart();
}

async function showChart() {
  const tok = ++chartToken;
  if (!sel) { chart.setData(null); $('msg').textContent = L.pick; return; }
  $('msg').textContent = L.loading;
  try {
    const d = await getChart(sel, prefs.range);
    if (tok !== chartToken) return;
    $('msg').textContent = d.candles.length ? '' : L.noData;
    chart.o.cost = (find(sel) || {}).cost > 0 ? find(sel).cost : null;
    chart.setData(d);
  } catch (e) {
    if (tok !== chartToken) return;
    chart.setData(null); $('msg').textContent = '⚠ ' + e.message;
  }
}

// ---------- 加入 / 移除 ----------
function addSymbol(sym, name, noSelect) {
  sym = sym.trim().toUpperCase(); if (!sym) return;
  let it = find(sym);
  if (!it) {
    it = { symbol: sym, name: name || sym, userAdded: true };
    items.push(it); saveItems(); wsSync(); wsStart();
    getQuote(sym).then(q => { quotes[sym] = q; delete errors[sym]; if (!it.name || it.name === sym) { it.name = q.name; saveItems(); } render(); })
      .catch(e => { errors[sym] = e.message; render(); });
  }
  if (!noSelect) select(it.symbol); else render();
}
function removeSymbol(sym) {
  const i = items.findIndex(x => x.symbol === sym); if (i < 0) return;
  items.splice(i, 1); delete quotes[sym]; delete errors[sym]; saveItems(); wsSync();
  if (sel === sym) { const n = items.find(x => x.starred) || items[0]; sel = ''; if (n) select(n.symbol); else { prefs.sel = ''; savePrefs(); showChart(); } }
  render();
}
function toggleStar(sym) { const it = find(sym); if (!it) return; it.starred = !it.starred; saveItems(); render(); }

// ---------- 彈出選單 ----------
const pop = $('pop');
function menu(entries, x, y) {
  pop.innerHTML = entries.map((e, i) => e.hr ? '<div class="hr"></div>' :
    e.hd != null ? `<div class="hd">${esc(e.hd)}${e.link ? `<a data-i="${i}">${esc(e.link)}</a>` : ''}</div>` :
      `<div class="it${e.off ? ' off' : ''}" data-i="${i}">${esc(e.label)}${e.right ? `<span class="r">${esc(e.right)}</span>` : ''}</div>`).join('');
  pop.onclick = ev => {
    const el = ev.target.closest('[data-i]'); if (!el) return;
    const e = entries[+el.dataset.i]; if (e.off) return;
    hidePop(); (e.act || (() => { }))();
  };
  pop.style.width = ''; pop.style.display = 'block';
  pop.style.left = Math.max(4, Math.min(x, innerWidth - pop.offsetWidth - 4)) + 'px';
  pop.style.top = Math.max(4, Math.min(y, innerHeight - pop.offsetHeight - 4)) + 'px';
}
function hidePop() { pop.style.display = 'none'; }
document.addEventListener('mousedown', e => { if (!pop.contains(e.target) && e.target !== $('q')) hidePop(); });

function rowMenu(sym, x, y) {
  const it = find(sym); if (!it) return;
  menu([
    { label: it.starred ? L.unstar : L.star, act: () => toggleStar(sym) },
    { label: L.costMenu, act: () => costDlg(sym) },
    { label: L.alertMenu, act: () => alertDlg(sym) },
    { label: L.yahooMenu, act: () => mt.open('https://finance.yahoo.com/quote/' + encodeURIComponent(sym)) },
    { hr: true },
    { label: L.remove, act: () => removeSymbol(sym) }
  ], x, y);
}

function hotMenu() {
  const r = $('hot').getBoundingClientRect(), e = [];
  for (const [g, list] of POPULAR) {
    e.push({ hd: L[g], link: L.addAll, act: () => { for (const [s, n] of list) addSymbol(s, n, true); } });
    for (const [s, n] of list) e.push({ label: n, right: find(s) ? '✓ ' + s : s, off: !!find(s), act: () => addSymbol(s, n) });
  }
  menu(e, r.left, r.bottom + 4);
}

// 搜尋（打字停一陣先搵）
let searchTimer = 0, searchSeq = 0, hits = [];
function onSearchInput() {
  clearTimeout(searchTimer);
  const q = $('q').value.trim();
  if (!q) { hidePop(); return; }
  searchTimer = setTimeout(async () => {
    const seq = ++searchSeq, r = $('q').getBoundingClientRect();
    menu([{ label: L.searching, off: true }], r.left, r.bottom + 4);
    try {
      hits = await searchYahoo(q);
      if (seq !== searchSeq) return;
      menu(hits.length ? hits.map(h => ({ label: h.symbol + '  ' + h.name, right: [h.exch, h.type].filter(Boolean).join(' · '), act: () => { $('q').value = ''; addSymbol(h.symbol, h.name); } }))
        : [{ label: L.noHit, off: true }], r.left, r.bottom + 4);
      pop.style.width = Math.max(r.width + 60, 320) + 'px';
    } catch (e) { if (seq === searchSeq) menu([{ label: L.searchFail + e.message, off: true }], r.left, r.bottom + 4); }
  }, 350);
}

// ---------- 視窗 ----------
function dialog(html, wire) {
  $('dlg').innerHTML = html; $('mask').style.display = 'flex';
  const close = () => { $('mask').style.display = 'none'; $('dlg').innerHTML = ''; };
  wire(close);
  const f = $('dlg').querySelector('input[type=text]'); if (f) f.focus();
}
$('mask').addEventListener('mousedown', e => { if (e.target === $('mask')) { $('mask').style.display = 'none'; } });
document.addEventListener('keydown', e => {
  if (e.key === 'Escape') { hidePop(); $('mask').style.display = 'none'; }
  if (e.key === 'Enter' && $('mask').style.display === 'flex') { const b = $('dlg').querySelector('.primary'); if (b) b.click(); }
});
/** 「」= null；數字 = 數字；其他 = undefined（錯） */
function parseNum(s) { s = String(s || '').replace(/[,\s$]/g, ''); if (!s) return null; const v = Number(s); return isFinite(v) && v >= 0 ? v : undefined; }
const numStr = v => v == null ? '' : String(v);

function costDlg(sym) {
  const it = find(sym); if (!it) return;
  dialog(`<h3>${esc(L.costTitle)} — ${esc(sym)}</h3><p>${esc(L.costHint)}</p>
    <label>${esc(L.costLabel)}</label><input type="text" id="dCost" value="${esc(numStr(it.cost))}">
    <label>${esc(L.sharesLabel)}</label><input type="text" id="dShares" value="${esc(numStr(it.shares))}">
    <div class="err" id="dErr"></div>
    <div class="btns"><button class="l" id="dClear">${esc(L.clearCost)}</button><button id="dCancel">${esc(L.cancel)}</button><button class="primary" id="dOk">${esc(L.save)}</button></div>`, close => {
    $('dCancel').onclick = close;
    $('dClear').onclick = () => { it.cost = null; it.shares = null; saveItems(); close(); render(); };
    $('dOk').onclick = () => {
      const c = parseNum($('dCost').value), s = parseNum($('dShares').value);
      if (c === undefined || s === undefined) { $('dErr').textContent = L.badNum; return; }
      it.cost = c; it.shares = c == null ? null : s; saveItems(); close(); render();
    };
  });
}

function alertDlg(sym) {
  const it = find(sym); if (!it) return;
  const q = quotes[sym];
  dialog(`<h3>${esc(L.alertTitle)} — ${esc(sym)}${q ? ` <span class="dim" style="font-weight:400">(${fmt(q.price)} ${esc(q.cur)})</span>` : ''}</h3><p>${esc(L.alertHint)}</p>
    <label>${esc(L.above)}</label><input type="text" id="dUp" value="${esc(numStr(it.above))}">
    <label>${esc(L.below)}</label><input type="text" id="dDown" value="${esc(numStr(it.below))}">
    <label>${esc(L.pct)}</label><input type="text" id="dPct" value="${esc(numStr(it.pct))}">
    <div class="err" id="dErr"></div>
    <div class="btns"><button class="l" id="dClear">${esc(L.clearAlert)}</button><button id="dCancel">${esc(L.cancel)}</button><button class="primary" id="dOk">${esc(L.save)}</button></div>`, close => {
    $('dCancel').onclick = close;
    $('dClear').onclick = () => { it.above = it.below = it.pct = null; saveItems(); close(); render(); };
    $('dOk').onclick = () => {
      const a = parseNum($('dUp').value), b = parseNum($('dDown').value), p = parseNum($('dPct').value);
      if (a === undefined || b === undefined || p === undefined) { $('dErr').textContent = L.badNum; return; }
      it.above = a; it.below = b; if (p !== it.pct) it.pctFired = ''; it.pct = p;
      saveItems(); close(); render(); checkAlerts([it]);
    };
  });
}

function settingsDlg() {
  const opt = Object.keys(L.styles).map(k => `<option value="${k}"${prefs.style === k ? ' selected' : ''}>${esc(L.styles[k])}</option>`).join('');
  dialog(`<h3>${esc(L.setTitle)}</h3>
    <label>${esc(L.colors)}</label>
    <label class="chk"><input type="radio" name="clr" value="0"${prefs.redUp ? '' : ' checked'}> <span class="up" style="color:var(--mt-success)">▲</span><span style="color:var(--mt-danger)">▼</span> ${esc(L.greenUp)}</label>
    <label class="chk"><input type="radio" name="clr" value="1"${prefs.redUp ? ' checked' : ''}> <span style="color:var(--mt-danger)">▲</span><span style="color:var(--mt-success)">▼</span> ${esc(L.redUp)}</label>
    <label>${esc(L.style)}</label><select id="dStyle">${opt}</select>
    <label class="chk"><input type="checkbox" id="dVol"${prefs.volume ? ' checked' : ''}> ${esc(L.showVol)}</label>
    <label class="chk"><input type="checkbox" id="dHide"${prefs.hidePresets ? ' checked' : ''}> ${esc(L.hidePresets)}</label>
    <div class="sub">${esc(L.hidePresetsSub)}</div>
    <div class="btns"><button class="primary" id="dOk">${esc(L.closeBtn)}</button></div>`, close => {
    const apply = () => {
      prefs.redUp = document.querySelector('input[name=clr]:checked').value === '1';
      prefs.style = $('dStyle').value; prefs.volume = $('dVol').checked; prefs.hidePresets = $('dHide').checked;
      savePrefs(); colors(); chart.o.style = prefs.style; chart.o.volume = prefs.volume; chart.draw(); render();
    };
    $('dlg').querySelectorAll('input, select').forEach(e => e.onchange = apply);
    $('dOk').onclick = close;
  });
}

/** 升 / 跌嘅顏色（紅升綠跌模式會反轉）。 */
function colors() {
  const s = document.documentElement.style;
  s.setProperty('--up', prefs.redUp ? 'var(--mt-danger)' : 'var(--mt-success)');
  s.setProperty('--down', prefs.redUp ? 'var(--mt-success)' : 'var(--mt-danger)');
}

// ---------- 開始 ----------
(async () => {
  const info = await mt.info();
  L = (info.lang || '').startsWith('zh') ? T.zh : T.en;
  prefs = Object.assign(prefs, (await mt.storage.get('prefs')) || {});
  if (!Array.isArray(prefs.collapsed)) prefs.collapsed = [];
  const saved = await mt.storage.get('watch');
  // 第一次用：預設幾隻熱門股（用家之後可以自己移除）
  items = Array.isArray(saved) ? saved.filter(x => x && x.symbol) : DEFAULTS.map(([symbol, name]) => ({ symbol, name }));
  if (!Array.isArray(saved)) saveItems();
  colors();

  $('q').placeholder = L.search; $('hot').textContent = L.hot; $('gear').textContent = '⚙'; $('gear').title = L.settings;
  $('note').textContent = L.note;
  const so = $('starOnly'); so.textContent = L.starOnly; so.classList.toggle('on', prefs.starOnly);
  so.onclick = () => { prefs.starOnly = !prefs.starOnly; so.classList.toggle('on', prefs.starOnly); savePrefs(); render(); };

  chart = new StockChart($('cv'), $('tip'));
  chart.o = Object.assign(chart.o, { style: prefs.style, volume: prefs.volume, ma20: prefs.ma20, ma50: prefs.ma50,
    L: { open: L.open, high: L.high, low: L.low, close: L.close, change: L.change, volume: L.volume, prevClose: L.prevClose, costLine: L.costLine } });
  chart.onView = moved => { const b = $('resetBtn'); if (b) b.style.display = moved ? '' : 'none'; };

  // 搜尋
  $('q').addEventListener('input', onSearchInput);
  $('q').addEventListener('keydown', e => {
    if (e.key !== 'Enter') return;
    const v = $('q').value.trim(); if (!v) return;
    const h = hits.find(x => x.symbol.toUpperCase() === v.toUpperCase()) || (pop.style.display === 'block' && hits[0]);
    $('q').value = ''; hidePop(); clearTimeout(searchTimer); searchSeq++;
    if (h) addSymbol(h.symbol, h.name); else addSymbol(v);
  });
  $('hot').onclick = hotMenu;
  $('gear').onclick = settingsDlg;

  // 清單：撳 = 揀、撳 ☆ = 標記、右鍵 = 選單、組名 = 摺疊
  const list = $('list');
  list.addEventListener('click', e => {
    const g = e.target.closest('.grp');
    if (g) { const k = g.dataset.g, i = prefs.collapsed.indexOf(k); i < 0 ? prefs.collapsed.push(k) : prefs.collapsed.splice(i, 1); savePrefs(); render(); return; }
    const r = e.target.closest('.row'); if (!r) return;
    if (e.target.classList.contains('star')) { toggleStar(r.dataset.s); return; }
    if (r.dataset.s !== sel) select(r.dataset.s);
  });
  list.addEventListener('contextmenu', e => { const r = e.target.closest('.row'); if (!r) return; e.preventDefault(); rowMenu(r.dataset.s, e.clientX, e.clientY); });
  // 拖動排次序（只可以喺同一組入面）
  list.addEventListener('dragstart', e => { const r = e.target.closest('.row'); if (!r) return; dragging = { s: r.dataset.s, g: r.dataset.g }; e.dataTransfer.effectAllowed = 'move'; });
  list.addEventListener('dragover', e => {
    const r = e.target.closest('.row'); list.querySelectorAll('.dragover').forEach(x => x.classList.remove('dragover'));
    if (!r || !dragging || r.dataset.g !== dragging.g || r.dataset.s === dragging.s) return;
    e.preventDefault(); r.classList.add('dragover');
  });
  list.addEventListener('drop', e => {
    const r = e.target.closest('.row'); if (!r || !dragging) return;
    e.preventDefault();
    const from = items.findIndex(x => x.symbol === dragging.s); const [it] = items.splice(from, 1);
    items.splice(items.findIndex(x => x.symbol === r.dataset.s), 0, it); saveItems();
  });
  list.addEventListener('dragend', () => { dragging = null; render(); });

  // 工具列
  $('bar').addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b) return;
    if (b.dataset.r != null) { prefs.range = +b.dataset.r; savePrefs(); bar(); showChart(); }
    else if (b.dataset.ma) { prefs[b.dataset.ma] = !prefs[b.dataset.ma]; chart.o[b.dataset.ma] = prefs[b.dataset.ma]; savePrefs(); bar(); chart.draw(); }
    else if (b.id === 'resetBtn') chart.reset();
    else if (b.dataset.a === 'star') toggleStar(sel);
    else if (b.dataset.a === 'cost') costDlg(sel);
    else if (b.dataset.a === 'alert') alertDlg(sel);
    else if (b.dataset.a === 'yahoo') mt.open('https://finance.yahoo.com/quote/' + encodeURIComponent(sel));
  });

  sel = find(prefs.sel) ? find(prefs.sel).symbol : ((items.find(x => x.starred) || items[0] || {}).symbol || '');
  render(); bar(); showChart();
  refresh();
  setInterval(refresh, 30000);
  wsStart();
})();
