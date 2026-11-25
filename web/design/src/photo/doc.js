// 修圖：文件（圖層、選取範圍）同復原紀錄。
//  • 每個圖層係一張同文件一樣大嘅 canvas。
//  • 選取範圍係一張 mask canvas（alpha = 揀咗幾多）；null = 冇揀（成張圖都用得）。
//  • 復原紀錄兩種：
//      pixels：一個圖層某個範圍改之前 / 之後嘅像素（畫筆、填色、刪除…）
//      state ：成個文件嘅結構（圖層次序、屬性、每層用邊張 canvas、選取範圍、尺寸）。
//    改尺寸、裁切、旋轉、選取都係「換一張新 canvas」而唔係改舊嗰張，所以 state 只要記住 canvas 參照，唔使複製像素。

let seq = 0;
export function canvas(w, h) { const c = document.createElement('canvas'); c.width = Math.max(1, w | 0); c.height = Math.max(1, h | 0); return c; }
export function ctx2d(c) { return c.getContext('2d', { willReadFrequently: false }); }
export function cloneCanvas(src) { const c = canvas(src.width, src.height); ctx2d(c).drawImage(src, 0, 0); return c; }

export const BLENDS = ['source-over', 'multiply', 'screen', 'overlay', 'darken', 'lighten', 'color-dodge', 'color-burn', 'hard-light', 'soft-light', 'difference', 'exclusion', 'hue', 'saturation', 'color', 'luminosity'];

export class Layer {
  constructor(w, h, name) {
    this.id = ++seq;
    this.name = name;
    this.canvas = canvas(w, h);
    this.visible = true;
    this.opacity = 1;
    this.blend = 'source-over';
    this.text = null;          // 文字圖層：{ str, font, size, color, bold, italic, align, x, y }
  }
  get ctx() { return ctx2d(this.canvas); }
}

/** 矩形工具：{x, y, w, h}；空 = null。 */
export const R = {
  of: (x, y, w, h) => ({ x, y, w, h }),
  union(a, b) { if (!a) return b; if (!b) return a; const x = Math.min(a.x, b.x), y = Math.min(a.y, b.y); return { x, y, w: Math.max(a.x + a.w, b.x + b.w) - x, h: Math.max(a.y + a.h, b.y + b.h) - y }; },
  clip(a, w, h) { if (!a) return null; const x = Math.max(0, Math.floor(a.x)), y = Math.max(0, Math.floor(a.y)); const x2 = Math.min(w, Math.ceil(a.x + a.w)), y2 = Math.min(h, Math.ceil(a.y + a.h)); return x2 > x && y2 > y ? { x, y, w: x2 - x, h: y2 - y } : null; },
};

export class PhotoDoc {
  constructor(w, h) {
    this.w = w; this.h = h;
    this.layers = [];          // [0] = 最底
    this.active = 0;
    this.sel = null;           // mask canvas / null
    this._edges = null;        // 選取範圍嘅邊（Path2D，文件座標）
    this.undoStack = []; this.redoStack = [];
    this.onChange = () => { };
  }
  get layer() { return this.layers[this.active]; }

  // ---------- 結構狀態 ----------
  state() {
    return { w: this.w, h: this.h, active: this.active, sel: this.sel, layers: this.layers.map(l => ({ l, canvas: l.canvas, name: l.name, visible: l.visible, opacity: l.opacity, blend: l.blend, text: l.text })) };
  }
  restore(s) {
    this.w = s.w; this.h = s.h; this.active = s.active; this.setSel(s.sel, true);
    this.layers = s.layers.map(x => { Object.assign(x.l, { canvas: x.canvas, name: x.name, visible: x.visible, opacity: x.opacity, blend: x.blend, text: x.text || null }); return x.l; });
  }
  /** 做一件會改結構嘅事（fn 直接改 this），之後記入復原紀錄。 */
  change(label, fn) {
    const before = this.state();
    const r = fn();
    this.push({ label, kind: 'state', before, after: this.state() });
    this.onChange('struct');
    return r;
  }

  // ---------- 像素改動 ----------
  /** 開始改一個圖層嘅像素：返回 end(rect) 記入復原紀錄。 */
  beginPixels(layer, label) {
    const snap = cloneCanvas(layer.canvas);
    return (rect) => {
      rect = R.clip(rect, layer.canvas.width, layer.canvas.height);
      if (!rect) return;
      const before = ctx2d(snap).getImageData(rect.x, rect.y, rect.w, rect.h);
      const after = layer.ctx.getImageData(rect.x, rect.y, rect.w, rect.h);
      this.push({ label, kind: 'pixels', layer, canvas: layer.canvas, rect, before, after });
      this.onChange('pixels');
    };
  }

  // ---------- 復原 ----------
  /** 幾件事當做一步復原（例如移動選取範圍：像素 + 選取範圍一齊）。 */
  group(label, fn) {
    const outer = this._group;
    this._group = [];
    let items;
    try { fn(); } finally { items = this._group; this._group = outer; }
    if (items.length === 1) { items[0].label = label; this.push(items[0]); }
    else if (items.length) this.push({ label, kind: 'group', items });
  }
  push(e) {
    if (this._group) { this._group.push(e); return; }
    this.undoStack.push(e); this.redoStack.length = 0;
    // 限住記憶體：最多 60 步、像素資料大約 600 MB
    let bytes = 0;
    for (let i = this.undoStack.length - 1; i >= 0; i--) {
      const x = this.undoStack[i];
      bytes += size(x);
      if (bytes > 600e6 || this.undoStack.length - i > 60) { this.undoStack.splice(0, i + 1); break; }
    }
    this.dirty = true;
  }
  apply(e, undo) {
    if (e.kind === 'group') { const it = undo ? [...e.items].reverse() : e.items; for (const x of it) this.apply(x, undo); }
    else if (e.kind === 'state') this.restore(undo ? e.before : e.after);
    else ctx2d(e.canvas).putImageData(undo ? e.before : e.after, e.rect.x, e.rect.y);
  }
  undo() { const e = this.undoStack.pop(); if (!e) return null; this.apply(e, true); this.redoStack.push(e); this.dirty = true; this.onChange('struct'); return e.label; }
  redo() { const e = this.redoStack.pop(); if (!e) return null; this.apply(e, false); this.undoStack.push(e); this.dirty = true; this.onChange('struct'); return e.label; }

  // ---------- 選取範圍 ----------
  setSel(mask, quiet) {
    this.sel = mask; this._edges = null;
    if (!quiet) this.onChange('sel');
  }
  /** 改選取範圍（記入復原）。mask = null 即取消選取。 */
  select(mask, label) { this.change(label, () => this.setSel(isEmpty(mask) ? null : mask, true)); }
  edges() {
    if (!this.sel) return null;
    if (!this._edges) this._edges = maskEdges(this.sel);
    return this._edges;
  }
  selBounds() { return this.sel ? maskBounds(this.sel) : { x: 0, y: 0, w: this.w, h: this.h }; }

  /** 合併所有看得見嘅圖層。 */
  flatten(bg) {
    const c = canvas(this.w, this.h), g = ctx2d(c);
    if (bg) { g.fillStyle = bg; g.fillRect(0, 0, this.w, this.h); }
    for (const l of this.layers) {
      if (!l.visible || l.opacity <= 0) continue;
      g.globalAlpha = l.opacity; g.globalCompositeOperation = l.blend;
      g.drawImage(l.canvas, 0, 0);
    }
    g.globalAlpha = 1; g.globalCompositeOperation = 'source-over';
    return c;
  }
}

const size = x => x.kind === 'group' ? x.items.reduce((s, i) => s + size(i), 0) : x.kind === 'pixels' ? x.rect.w * x.rect.h * 8 : 1000;

// ---------- mask 工具 ----------
export function isEmpty(mask) {
  if (!mask) return true;
  const d = ctx2d(mask).getImageData(0, 0, mask.width, mask.height).data;
  for (let i = 3; i < d.length; i += 4) if (d[i]) return false;
  return true;
}
export function maskBounds(mask) {
  const w = mask.width, h = mask.height, d = ctx2d(mask).getImageData(0, 0, w, h).data;
  let x1 = w, y1 = h, x2 = -1, y2 = -1;
  for (let y = 0; y < h; y++) {
    const row = y * w * 4;
    for (let x = 0; x < w; x++) if (d[row + x * 4 + 3] > 0) { if (x < x1) x1 = x; if (x > x2) x2 = x; if (y < y1) y1 = y; y2 = y; }
  }
  return x2 < 0 ? null : { x: x1, y: y1, w: x2 - x1 + 1, h: y2 - y1 + 1 };
}
/** 搵出 mask（alpha ≥ 128）嘅邊界線段，砌做一條 Path2D（俾「螞蟻線」用）。 */
export function maskEdges(mask) {
  const w = mask.width, h = mask.height, d = ctx2d(mask).getImageData(0, 0, w, h).data;
  const on = (x, y) => x >= 0 && y >= 0 && x < w && y < h && d[(y * w + x) * 4 + 3] >= 128;
  const p = new Path2D();
  // 橫線：上下兩格唔同就有邊；連續嘅合埋一條
  for (let y = 0; y <= h; y++) {
    let start = -1;
    for (let x = 0; x <= w; x++) {
      const edge = x < w && on(x, y - 1) !== on(x, y);
      if (edge && start < 0) start = x;
      else if (!edge && start >= 0) { p.moveTo(start, y); p.lineTo(x, y); start = -1; }
    }
  }
  for (let x = 0; x <= w; x++) {
    let start = -1;
    for (let y = 0; y <= h; y++) {
      const edge = y < h && on(x - 1, y) !== on(x, y);
      if (edge && start < 0) start = y;
      else if (!edge && start >= 0) { p.moveTo(x, start); p.lineTo(x, y); start = -1; }
    }
  }
  return p;
}
/** 由一條形狀（Path2D）整 mask；mode = new / add / sub / and，base = 原本嘅 mask。 */
export function shapeMask(w, h, path, mode, base) {
  const c = canvas(w, h), g = ctx2d(c);
  if (base && mode !== 'new') g.drawImage(base, 0, 0);
  g.fillStyle = '#000';
  if (mode === 'sub') g.globalCompositeOperation = 'destination-out';
  else if (mode === 'and') g.globalCompositeOperation = base ? 'destination-in' : 'source-over';
  g.fill(path);
  return c;
}
/** 兩張 mask 按 mode 合併（魔術棒用）。 */
export function combineMask(w, h, add, mode, base) {
  const c = canvas(w, h), g = ctx2d(c);
  if (base && mode !== 'new') g.drawImage(base, 0, 0);
  if (mode === 'sub') g.globalCompositeOperation = 'destination-out';
  else if (mode === 'and' && base) g.globalCompositeOperation = 'destination-in';
  g.drawImage(add, 0, 0);
  return c;
}
export function invertMask(w, h, mask) {
  const c = canvas(w, h), g = ctx2d(c);
  g.fillStyle = '#000'; g.fillRect(0, 0, w, h);
  if (mask) { g.globalCompositeOperation = 'destination-out'; g.drawImage(mask, 0, 0); }
  return c;
}

/**
 * 由 (sx, sy) 開始搵顏色相近嘅像素（填色同魔術棒用）。
 * 返回 { mask: Uint8Array(w*h)，rect }。contiguous = false 就成張圖搵。
 */
export function floodRegion(img, sx, sy, tol, contiguous) {
  const { width: w, height: h, data: d } = img;
  const i0 = (sy * w + sx) * 4, r0 = d[i0], g0 = d[i0 + 1], b0 = d[i0 + 2], a0 = d[i0 + 3];
  const t = tol * 4;
  const near = i => Math.abs(d[i] - r0) + Math.abs(d[i + 1] - g0) + Math.abs(d[i + 2] - b0) + Math.abs(d[i + 3] - a0) <= t;
  const out = new Uint8Array(w * h);
  let x1 = sx, x2 = sx, y1 = sy, y2 = sy;
  const mark = (x, y) => { out[y * w + x] = 1; if (x < x1) x1 = x; if (x > x2) x2 = x; if (y < y1) y1 = y; if (y > y2) y2 = y; };
  if (!contiguous) {
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) if (near((y * w + x) * 4)) mark(x, y);
  } else {
    // 掃描線填充
    const stack = [sx, sy];
    while (stack.length) {
      const y = stack.pop(), x0 = stack.pop();
      let x = x0;
      while (x >= 0 && !out[y * w + x] && near((y * w + x) * 4)) x--;
      x++;
      let up = false, down = false;
      while (x < w && !out[y * w + x] && near((y * w + x) * 4)) {
        mark(x, y);
        if (y > 0) { const n = (y - 1) * w + x; if (!out[n] && near(n * 4)) { if (!up) { stack.push(x, y - 1); up = true; } } else up = false; }
        if (y < h - 1) { const n = (y + 1) * w + x; if (!out[n] && near(n * 4)) { if (!down) { stack.push(x, y + 1); down = true; } } else down = false; }
        x++;
      }
    }
  }
  return { mask: out, rect: { x: x1, y: y1, w: x2 - x1 + 1, h: y2 - y1 + 1 } };
}
/** Uint8Array 區域 → mask canvas。 */
export function regionToMask(w, h, region) {
  const c = canvas(w, h), g = ctx2d(c), img = g.createImageData(w, h), d = img.data;
  for (let i = 0; i < region.length; i++) if (region[i]) d[i * 4 + 3] = 255;
  g.putImageData(img, 0, 0);
  return c;
}
