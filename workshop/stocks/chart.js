// 股票走勢圖（canvas）：面積 / 線 / K 線 / 空心 K 線 / 美國線，MA20 / MA50，成交量，昨收線，買入價線
// 滾輪放大縮細；撳住拖可以上下睇（放大咗仲可以左右睇）；滑鼠指住 K 線先顯示詳細數據
'use strict';

class StockChart {
  constructor(canvas, tip) {
    this.cv = canvas; this.tip = tip; this.ctx = canvas.getContext('2d');
    this.d = null;            // {candles:[{t,o,h,l,c,v}], prevClose, intraday, iv, range}
    this.o = { style: 'area', volume: true, ma20: false, ma50: false, cost: null, L: {} };
    this.zs = 0; this.zn = 0; // 顯示由第 zs 條開始、共 zn 條（0 = 全部）
    this.yShift = 0;          // 上下拖咗幾多（以成個價格範圍做單位，正數 = 睇上面）
    this.hover = -1; this.mouse = null; this.drag = null;
    this.onView = null;       // 放大 / 移動咗就通知（顯示「⤢ 還原」）
    new ResizeObserver(() => this.resize()).observe(canvas);
    canvas.addEventListener('wheel', e => this.wheel(e), { passive: false });
    canvas.addEventListener('mousedown', e => this.down(e));
    window.addEventListener('mousemove', e => this.move(e));
    window.addEventListener('mouseup', () => { if (this.drag) { this.drag = null; this.cv.classList.remove('grab'); } });
    canvas.addEventListener('mouseleave', () => { if (!this.drag) { this.mouse = null; this.hover = -1; this.hideTip(); this.draw(); } });
  }

  get moved() { return this.zoomed || Math.abs(this.yShift) > 1e-9; }
  get zoomed() { return this.d && this.zn > 0 && this.zn < this.d.candles.length; }

  setData(d) { this.d = d; this.zs = 0; this.zn = 0; this.yShift = 0; this.hover = -1; this.hideTip(); this.view(); this.draw(); }
  reset() { this.zs = 0; this.zn = 0; this.yShift = 0; this.view(); this.draw(); }
  view() { if (this.onView) this.onView(this.moved); }

  /** 即時推送：更新最後一條（過咗一格時間就加新一條）。t = 交易所當地時間（毫秒） */
  tick(price, t) {
    const d = this.d; if (!d || !d.candles.length || !isFinite(price)) return;
    const last = d.candles[d.candles.length - 1];
    if (d.intraday && d.iv > 0 && t >= last.t + d.iv * 60000) {
      const slot = last.t + Math.floor((t - last.t) / (d.iv * 60000)) * d.iv * 60000;
      d.candles.push({ t: slot, o: price, h: price, l: price, c: price, v: 0 });
      if (this.zn > 0 && this.zs + this.zn === d.candles.length - 1) this.zs++; // 睇緊最尾就跟住行
    } else {
      last.c = price; if (price > last.h) last.h = price; if (price < last.l) last.l = price;
    }
    this.draw();
  }

  resize() {
    const r = window.devicePixelRatio || 1, w = this.cv.clientWidth, h = this.cv.clientHeight;
    if (!w || !h) return;
    this.cv.width = Math.round(w * r); this.cv.height = Math.round(h * r);
    this.ctx.setTransform(r, 0, 0, r, 0, 0);
    this.draw();
  }

  range() {
    const n = this.d ? this.d.candles.length : 0;
    if (this.zn <= 0 || this.zn >= n) return [0, n];
    this.zs = Math.max(0, Math.min(this.zs, n - this.zn));
    return [this.zs, this.zn];
  }

  // ---------- 版面 ----------
  layout() {
    const W = this.cv.clientWidth, H = this.cv.clientHeight;
    const L = 4, R = W - 66, T = 8, B = H - 22;
    const volH = this.o.volume ? Math.round((B - T) * 0.18) : 0;
    return { W, H, L, R, T, B, pB: B - volH - (volH ? 6 : 0), volT: B - volH };
  }

  // ---------- 滑鼠 ----------
  wheel(e) {
    e.preventDefault();
    if (!this.d || this.d.candles.length < 2) return;
    const n = this.d.candles.length, [s, c] = this.range(), g = this.layout();
    const fx = Math.max(0, Math.min(1, (e.offsetX - g.L) / (g.R - g.L)));
    const anchor = s + fx * c;
    let nc = Math.round(c * (e.deltaY > 0 ? 1.25 : 0.8));
    nc = Math.max(Math.min(10, n), Math.min(n, nc));
    if (nc === c && e.deltaY < 0 && c > 10) nc = c - 1;
    if (nc >= n) { this.zs = 0; this.zn = 0; }
    else { this.zn = nc; this.zs = Math.round(anchor - fx * nc); }
    this.range(); this.view(); this.move(e, true);
  }

  down(e) {
    if (e.button !== 0 || !this.d) return;
    const [s] = this.range();
    this.drag = { x: e.clientX, y: e.clientY, zs: s, ys: this.yShift };
    this.cv.classList.add('grab'); this.hideTip();
  }

  move(e, force) {
    if (!this.d) return;
    const g = this.layout(), rect = this.cv.getBoundingClientRect();
    if (this.drag) {
      const [, c] = this.range();
      if (this.zoomed) {
        const per = (g.R - g.L) / c;
        this.zs = Math.round(this.drag.zs - (e.clientX - this.drag.x) / per);
        this.range();
      }
      this.yShift = this.drag.ys + (e.clientY - this.drag.y) / Math.max(1, g.pB - g.T);
      this.view(); this.draw();
      return;
    }
    if (!force && e.target !== this.cv) return;
    this.mouse = { x: e.clientX - rect.left, y: e.clientY - rect.top };
    this.draw();
  }

  hideTip() { this.tip.style.display = 'none'; }

  // ---------- 畫 ----------
  css(n) { return getComputedStyle(document.documentElement).getPropertyValue(n).trim() || '#888'; }

  draw() {
    const ctx = this.ctx, g = this.layout(), o = this.o, Lx = o.L;
    if (!g.W || !g.H) return;
    ctx.clearRect(0, 0, g.W, g.H);
    const d = this.d;
    if (!d || !d.candles.length) { this.hideTip(); return; }
    const col = { text: this.css('--mt-text'), dim: this.css('--mt-text-dim'), grid: this.css('--mt-border'), acc: this.css('--mt-accent'), up: this.css('--up'), down: this.css('--down'), bg: this.css('--mt-bg') };
    const [s, c] = this.range(), vis = d.candles.slice(s, s + c);
    const lineStyle = o.style === 'area' || o.style === 'line';

    // 價格範圍
    let lo = Infinity, hi = -Infinity;
    for (const k of vis) { const a = lineStyle ? k.c : k.l, b = lineStyle ? k.c : k.h; if (a < lo) lo = a; if (b > hi) hi = b; }
    if (d.intraday && d.range === '1d' && d.prevClose > 0 && !this.zoomed) { lo = Math.min(lo, d.prevClose); hi = Math.max(hi, d.prevClose); }
    if (!(hi > lo)) { hi = lo * 1.01 + 1e-6; lo = lo * 0.99 - 1e-6; }
    const pad = (hi - lo) * 0.06; lo -= pad; hi += pad;
    const span = hi - lo; lo += this.yShift * span; hi += this.yShift * span;
    const Y = v => g.pB - (v - lo) / (hi - lo) * (g.pB - g.T);
    const per = (g.R - g.L) / c, X = i => g.L + (i - s + 0.5) * per;

    // 格線 + 右邊價格
    ctx.font = '11px "Segoe UI", sans-serif'; ctx.textBaseline = 'middle'; ctx.lineWidth = 1;
    const step = nice((hi - lo) / 5);
    ctx.fillStyle = col.dim;
    for (let v = Math.ceil(lo / step) * step; v <= hi; v += step) {
      const y = Math.round(Y(v)) + 0.5;
      ctx.strokeStyle = col.grid; ctx.globalAlpha = 0.6; line(ctx, g.L, y, g.R, y); ctx.globalAlpha = 1;
      ctx.fillText(fmt(v), g.R + 6, y);
    }

    // 下面時間
    const every = Math.max(1, Math.ceil(90 / per));
    ctx.textBaseline = 'alphabetic'; ctx.textAlign = 'center';
    const spanMs = vis.length > 1 ? vis[vis.length - 1].t - vis[0].t : 0;
    for (let i = s; i < s + c; i += every) {
      const x = X(i); if (x < g.L + 20 || x > g.R - 20) continue;
      ctx.fillText(timeLabel(d.candles[i].t, d.intraday, spanMs), x, g.H - 6);
    }
    ctx.textAlign = 'left';

    // 成交量
    if (o.volume) {
      let vmax = 0; for (const k of vis) if (k.v > vmax) vmax = k.v;
      if (vmax > 0) {
        ctx.globalAlpha = 0.45;
        for (let i = s; i < s + c; i++) {
          const k = d.candles[i], h = k.v / vmax * (g.B - g.volT);
          const up = i > 0 ? k.c >= d.candles[i - 1].c : k.c >= k.o;
          ctx.fillStyle = up ? col.up : col.down;
          ctx.fillRect(X(i) - Math.max(1, per * 0.35), g.B - h, Math.max(1, per * 0.7), h);
        }
        ctx.globalAlpha = 1;
      }
    }

    // 主圖（剪走超出範圍嘅部份）
    ctx.save(); ctx.beginPath(); ctx.rect(g.L, g.T, g.R - g.L, g.pB - g.T); ctx.clip();
    const first = vis[0].o || vis[0].c, lastC = vis[vis.length - 1].c;
    const base = d.intraday && d.range === '1d' && d.prevClose > 0 ? d.prevClose : first;
    const trend = lastC >= base ? col.up : col.down;
    if (lineStyle) {
      ctx.beginPath();
      for (let i = s; i < s + c; i++) { const x = X(i), y = Y(d.candles[i].c); i === s ? ctx.moveTo(x, y) : ctx.lineTo(x, y); }
      ctx.strokeStyle = trend; ctx.lineWidth = 1.8; ctx.lineJoin = 'round'; ctx.stroke();
      if (o.style === 'area') {
        ctx.lineTo(X(s + c - 1), g.pB); ctx.lineTo(X(s), g.pB); ctx.closePath();
        const gr = ctx.createLinearGradient(0, g.T, 0, g.pB);
        gr.addColorStop(0, alpha(trend, 0.28)); gr.addColorStop(1, alpha(trend, 0.02));
        ctx.fillStyle = gr; ctx.fill();
      }
    } else {
      const bw = Math.max(1, Math.min(14, per * 0.65));
      for (let i = s; i < s + c; i++) {
        const k = d.candles[i], x = Math.round(X(i)) + 0.5, up = k.c >= k.o, cl = up ? col.up : col.down;
        ctx.strokeStyle = cl; ctx.fillStyle = cl; ctx.lineWidth = 1;
        if (o.style === 'ohlc') {
          line(ctx, x, Y(k.h), x, Y(k.l));
          line(ctx, x - bw / 2, Math.round(Y(k.o)) + 0.5, x, Math.round(Y(k.o)) + 0.5);
          line(ctx, x, Math.round(Y(k.c)) + 0.5, x + bw / 2, Math.round(Y(k.c)) + 0.5);
          continue;
        }
        const yo = Y(k.o), yc = Y(k.c), top = Math.min(yo, yc), h = Math.max(1, Math.abs(yo - yc));
        line(ctx, x, Y(k.h), x, top); line(ctx, x, top + h, x, Y(k.l));
        if (o.style === 'hollow' && up) ctx.strokeRect(x - bw / 2 + 0.5, top + 0.5, Math.max(1, bw - 1), Math.max(1, h - 1));
        else ctx.fillRect(x - bw / 2, top, bw, h);
      }
    }
    // 移動平均線
    const ma = (n, color) => {
      if (d.candles.length < n) return;
      ctx.beginPath(); let sum = 0, started = false;
      for (let i = 0; i < s + c; i++) {
        sum += d.candles[i].c; if (i >= n) sum -= d.candles[i - n].c;
        if (i < n - 1 || i < s) continue;
        const x = X(i), y = Y(sum / n); started ? ctx.lineTo(x, y) : ctx.moveTo(x, y); started = true;
      }
      ctx.strokeStyle = color; ctx.lineWidth = 1.3; ctx.stroke();
    };
    if (o.ma20) ma(20, '#f5a623');
    if (o.ma50) ma(50, '#a66cff');
    ctx.restore();

    // 昨收、買入價（虛線 + 右邊標籤）
    const hline = (v, color, label) => {
      const y = Math.round(Y(v)) + 0.5; if (y < g.T || y > g.pB) return;
      ctx.setLineDash([4, 4]); ctx.strokeStyle = color; ctx.lineWidth = 1; line(ctx, g.L, y, g.R, y); ctx.setLineDash([]);
      tag(ctx, g.R + 2, y, fmt(v), color, '#fff'); ctx.fillStyle = color; ctx.textBaseline = 'bottom'; ctx.fillText(label, g.L + 4, y - 2);
    };
    if (d.intraday && d.range === '1d' && d.prevClose > 0) hline(d.prevClose, col.dim, Lx.prevClose || '');
    if (o.cost > 0) hline(o.cost, col.acc, Lx.costLine || '');
    // 最新價
    tag(ctx, g.R + 2, Y(lastC), fmt(lastC), trend, '#fff');

    // 滑鼠：十字線 + 指住 K 線先顯示詳細數據
    this.hideTip(); this.hover = -1;
    const m = this.mouse;
    if (m && !this.drag && m.x >= g.L && m.x <= g.R && m.y >= g.T && m.y <= g.B) {
      const i = Math.max(s, Math.min(s + c - 1, Math.floor((m.x - g.L) / per) + s));
      const k = d.candles[i], x = Math.round(X(i)) + 0.5;
      ctx.setLineDash([3, 3]); ctx.strokeStyle = col.dim; ctx.globalAlpha = 0.7;
      line(ctx, x, g.T, x, g.B);
      if (m.y <= g.pB) line(ctx, g.L, Math.round(m.y) + 0.5, g.R, Math.round(m.y) + 0.5);
      ctx.setLineDash([]); ctx.globalAlpha = 1;
      if (m.y <= g.pB) tag(ctx, g.R + 2, m.y, fmt(lo + (g.pB - m.y) / (g.pB - g.T) * (hi - lo)), col.dim, col.bg);
      // 有冇指中：K 線就要喺最高至最低之間；線圖就要近條線
      const top = lineStyle ? Y(k.c) - 10 : Y(k.h) - 4, bot = lineStyle ? Y(k.c) + 10 : Y(k.l) + 4;
      const inVol = o.volume && m.y >= g.volT;
      if ((m.y >= top && m.y <= bot) || inVol) {
        this.hover = i;
        if (lineStyle) { ctx.fillStyle = trend; ctx.beginPath(); ctx.arc(X(i), Y(k.c), 3.5, 0, 7); ctx.fill(); }
        this.showTip(k, i, m, g);
      }
    }
  }

  showTip(k, i, m, g) {
    const L = this.o.L, d = this.d, prev = i > 0 ? d.candles[i - 1].c : (d.prevClose || k.o);
    const ch = k.c - prev, pc = prev ? ch / prev * 100 : 0, cls = ch >= 0 ? 'up' : 'down';
    const row = (a, b, c2) => `<tr><td class="dim">${a}</td><td${c2 ? ` class="${c2}"` : ''}>${b}</td></tr>`;
    this.tip.innerHTML = `<b>${fullTime(k.t, d.intraday)}</b><table>` +
      row(L.open, fmt(k.o)) + row(L.high, fmt(k.h)) + row(L.low, fmt(k.l)) + row(L.close, fmt(k.c)) +
      row(L.change, (ch >= 0 ? '+' : '') + fmt(ch) + ' (' + (ch >= 0 ? '+' : '') + pc.toFixed(2) + '%)', cls) +
      (k.v ? row(L.volume, vol(k.v)) : '') + '</table>';
    const t = this.tip; t.style.display = 'block';
    let x = m.x + 16, y = m.y + 16;
    if (x + t.offsetWidth > g.W - 4) x = m.x - t.offsetWidth - 16;
    if (y + t.offsetHeight > g.H - 4) y = m.y - t.offsetHeight - 16;
    t.style.left = Math.max(4, x) + 'px'; t.style.top = Math.max(4, y) + 'px';
  }
}

// ---------- 小工具 ----------
function line(ctx, x1, y1, x2, y2) { ctx.beginPath(); ctx.moveTo(x1, y1); ctx.lineTo(x2, y2); ctx.stroke(); }
function tag(ctx, x, y, text, bg, fg) {
  ctx.font = '11px "Segoe UI", sans-serif';
  const w = ctx.measureText(text).width + 10;
  ctx.fillStyle = bg; ctx.beginPath(); ctx.roundRect ? ctx.roundRect(x, y - 9, w, 18, 4) : ctx.rect(x, y - 9, w, 18); ctx.fill();
  ctx.fillStyle = fg; ctx.textBaseline = 'middle'; ctx.fillText(text, x + 5, y);
}
function nice(x) {
  const p = Math.pow(10, Math.floor(Math.log10(x))), f = x / p;
  return (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10) * p;
}
function alpha(c, a) {
  const m = /^#([0-9a-f]{6})$/i.exec(c);
  if (m) { const n = parseInt(m[1], 16); return `rgba(${n >> 16},${(n >> 8) & 255},${n & 255},${a})`; }
  const r = /rgba?\(([^)]+)\)/.exec(c);
  if (r) { const p = r[1].split(',').slice(0, 3).join(','); return `rgba(${p},${a})`; }
  return c;
}
/** 價錢格式：細價多啲小數位。 */
function fmt(v) {
  if (v == null || !isFinite(v)) return '–';
  const a = Math.abs(v);
  return a < 1 ? v.toFixed(4) : a < 10 ? v.toFixed(3) : v.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}
function vol(v) { return v >= 1e9 ? +(v / 1e9).toFixed(2) + 'B' : v >= 1e6 ? +(v / 1e6).toFixed(2) + 'M' : v >= 1e3 ? +(v / 1e3).toFixed(1) + 'K' : String(Math.round(v)); }
const p2 = n => String(n).padStart(2, '0');
// t 已經係交易所當地時間（當 UTC 咁讀）
function timeLabel(t, intraday, spanMs) {
  const d = new Date(t);
  if (intraday) return spanMs < 36e5 * 20 ? p2(d.getUTCHours()) + ':' + p2(d.getUTCMinutes()) : (d.getUTCMonth() + 1) + '/' + d.getUTCDate() + ' ' + p2(d.getUTCHours()) + ':' + p2(d.getUTCMinutes());
  return spanMs > 864e5 * 400 ? d.getUTCFullYear() + '/' + (d.getUTCMonth() + 1) : (d.getUTCMonth() + 1) + '/' + d.getUTCDate();
}
function fullTime(t, intraday) {
  const d = new Date(t), day = d.getUTCFullYear() + '-' + p2(d.getUTCMonth() + 1) + '-' + p2(d.getUTCDate());
  return intraday ? day + ' ' + p2(d.getUTCHours()) + ':' + p2(d.getUTCMinutes()) : day;
}
