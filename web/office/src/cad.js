// 繪圖工具（2D）：線、聚合線、矩形、圓、弧、多邊形、文字、標註；圖層；物件捕捉；指令列；移動 / 複製 / 旋轉 / 縮放 / 鏡射 /
// 偏移 / 陣列 / 修剪 / 延伸 / 圓角 / 分解；DXF 讀寫；匯出 SVG / PNG；按比例列印。全部自己寫，冇用第三方程式庫。
import { T, CFG, $, post, toast, busy, shell, initShell, buildMenubar, dialog, el } from './shell.js';
import './shell.css';
import * as G from './cad/geom.js';
import { parseDxf, writeDxf, aciHex, setGuessEncoding } from './cad/dxf.js';
const { add, sub, mul, dist, ang, polar, lerp, norm, perp, fmt, TAU } = G;

// ---------- 狀態 ----------
let doc = { ents: [], layers: [{ name: '0', color: 7, on: true, lock: false }], cur: '0' };
let nextId = 1, byId = new Map(), sel = new Set(), undoS = [], redoS = [], clip = null;
const view = { k: 2, ox: 80, oy: 400 };
const st = { color: null, osnap: true, ortho: false, grid: true, gsnap: false, light: !!CFG.light, textH: 0, offD: 10, filR: 0, last: '', side: true };
let req = null, runTok = 0, running = '', cursor = [0, 0], cursorRaw = [0, 0], snapMark = null, hover = null, win = null, shiftDown = false, panning = null, lastPt = [0, 0];
const CANCEL = { cancel: true };
let cvB, cvO, cxB, cxO, W = 100, H = 100, dpr = 1, needB = false, needO = false;
const bbCache = new WeakMap();
const bb = e => { let b = bbCache.get(e); if (b === undefined) { b = G.bbox(e); bbCache.set(e, b); } return b; };

const w2s = p => [p[0] * view.k + view.ox, view.oy - p[1] * view.k];
const s2w = p => [(p[0] - view.ox) / view.k, (view.oy - p[1]) / view.k];
let lmap = new Map();
const layerOf = e => lmap.get(e.layer) || doc.layers[0];
const visible = e => layerOf(e).on !== false;
const pickable = e => { const l = layerOf(e); return l.on !== false && !l.lock; };
const colorOf = (e, light = st.light) => {
  let c = e.color == null ? layerOf(e).color : e.color;
  let h = typeof c === 'string' ? c : aciHex(c);
  if (light ? h === '#ffffff' : h === '#000000') h = light ? '#000000' : '#ffffff';
  return h;
};
const nice = v => { const p = Math.pow(10, Math.floor(Math.log10(v))), m = v / p; return (m < 1.5 ? 1 : m < 3.5 ? 2 : m < 7.5 ? 5 : 10) * p; };
const autoH = () => st.textH || nice(13 / view.k);
const deg = r => fmt(G.normA(r) * 180 / Math.PI, 2);

// ---------- 文件改動 ----------
function reindex() { lmap = new Map(doc.layers.map(l => [l.name, l])); byId = new Map(doc.ents.map(e => [e.id, e])); for (const id of [...sel]) if (!byId.has(id)) sel.delete(id); }
function snapshot() { return JSON.stringify(doc); }
function mark() { undoS.push(snapshot()); if (undoS.length > (doc.ents.length > 20000 ? 8 : 60)) undoS.shift(); redoS = []; }
function changed() { reindex(); shell.setDirty(); needB = needO = true; schedule(); sidePanel(); }
function restore(s) { doc = JSON.parse(s); nextId = doc.ents.reduce((m, e) => Math.max(m, e.id), 0) + 1; changed(); }
function undo() { if (!undoS.length) return false; redoS.push(snapshot()); restore(undoS.pop()); return true; }
function redo() { if (!redoS.length) return false; undoS.push(snapshot()); restore(redoS.pop()); return true; }
const mk = e => ({ layer: doc.cur, color: st.color, ...e, id: nextId++ });
/** 加圖元（一步復原）。 */
function addEnts(list, keepMark) { if (!list.length) return []; if (!keepMark) mark(); const out = list.map(mk); doc.ents.push(...out); changed(); return out; }
/** 用新圖元取代舊圖元：map = Map(舊 id → 新圖元陣列；第一個沿用舊 id)。 */
function replaceEnts(map) {
  mark();
  const out = [];
  for (const e of doc.ents) { if (!map.has(e.id)) { out.push(e); continue; } map.get(e.id).forEach((n, i) => out.push({ ...n, layer: n.layer ?? e.layer, color: n.color === undefined ? e.color : n.color, id: i === 0 ? e.id : nextId++ })); }
  doc.ents = out; changed();
}
function removeEnts(ids) { const s = new Set(ids); if (!s.size) return; mark(); doc.ents = doc.ents.filter(e => !s.has(e.id)); changed(); }
const selEnts = () => doc.ents.filter(e => sel.has(e.id));

// ---------- 畫 ----------
function pathPrims(c, ps, v) {
  for (const p of ps) {
    if (p.t === 'l') { c.moveTo(p.a[0] * v.k + v.ox, v.oy - p.a[1] * v.k); c.lineTo(p.b[0] * v.k + v.ox, v.oy - p.b[1] * v.k); continue; }
    const cx = p.c[0] * v.k + v.ox, cy = v.oy - p.c[1] * v.k, r = p.r * v.k;
    if (r > 30000) { const n = 720, s = G.sw(p); for (let i = 0; i <= n; i++) { const q = polar(p.c, p.r, p.a0 + s * i / n), x = q[0] * v.k + v.ox, y = v.oy - q[1] * v.k; if (i) c.lineTo(x, y); else c.moveTo(x, y); } continue; }
    if (p.full) { c.moveTo(cx + r, cy); c.arc(cx, cy, r, 0, TAU); } else { c.moveTo(cx + r * Math.cos(p.a0), cy - r * Math.sin(p.a0)); c.arc(cx, cy, r, -p.a0, -p.a0 - G.sw(p), true); }
  }
}
function drawText(c, t, v, color) {
  const px = t.h * v.k * G.EM, s = [t.p[0] * v.k + v.ox, v.oy - t.p[1] * v.k];
  if (px < 3) { const w = G.textW(G.textLines(t)[0], t.h) * v.k, r = t.rot || 0, x0 = t.align === 'c' ? -w / 2 : 0; c.beginPath(); c.moveTo(s[0] + x0 * Math.cos(r), s[1] - x0 * Math.sin(r)); c.lineTo(s[0] + (x0 + w) * Math.cos(r), s[1] - (x0 + w) * Math.sin(r)); c.strokeStyle = color; c.globalAlpha = .5; c.stroke(); c.globalAlpha = 1; return; }
  c.save(); c.translate(s[0], s[1]); c.rotate(-(t.rot || 0));
  c.font = px + 'px ' + G.FONT; c.fillStyle = color; c.textAlign = t.align === 'c' ? 'center' : 'left'; c.textBaseline = 'alphabetic';
  G.textLines(t).forEach((l, i) => c.fillText(l, 0, i * t.h * G.LINE_GAP * v.k));
  c.restore();
}
function drawEnt(c, e, v, color) {
  if (e.t === 'text') { drawText(c, e, v, color); return; }
  c.beginPath(); pathPrims(c, G.prims(e), v); c.strokeStyle = color; c.stroke();
  if (e.t === 'dim') {
    const g = G.dimGeo(e); c.fillStyle = color;
    for (const t of g.tris) { c.beginPath(); t.forEach((p, i) => { const x = p[0] * v.k + v.ox, y = v.oy - p[1] * v.k; if (i) c.lineTo(x, y); else c.moveTo(x, y); }); c.closePath(); c.fill(); }
    drawText(c, { ...g.text, align: 'c' }, v, color);
  }
}
/** 畫成張圖（畫面、匯出 PNG 都用）。 */
function paint(c, v, w, h, o) {
  c.fillStyle = o.bg; c.fillRect(0, 0, w, h);
  const x0 = -v.ox / v.k, x1 = (w - v.ox) / v.k, y0 = (v.oy - h) / v.k, y1 = v.oy / v.k;
  if (o.grid) {
    const step = nice(14 / v.k) , big = step * 10;
    c.lineWidth = 1;
    for (const [s, col] of [[step, o.light ? 'rgba(0,0,0,.06)' : 'rgba(255,255,255,.045)'], [big, o.light ? 'rgba(0,0,0,.13)' : 'rgba(255,255,255,.10)']]) {
      c.beginPath();
      for (let x = Math.ceil(x0 / s) * s; x <= x1; x += s) { const X = Math.round(x * v.k + v.ox) + .5; c.moveTo(X, 0); c.lineTo(X, h); }
      for (let y = Math.ceil(y0 / s) * s; y <= y1; y += s) { const Y = Math.round(v.oy - y * v.k) + .5; c.moveTo(0, Y); c.lineTo(w, Y); }
      c.strokeStyle = col; c.stroke();
    }
    c.beginPath(); c.moveTo(Math.round(v.ox) + .5, 0); c.lineTo(Math.round(v.ox) + .5, h); c.strokeStyle = o.light ? 'rgba(0,140,0,.45)' : 'rgba(80,200,120,.4)'; c.stroke();
    c.beginPath(); c.moveTo(0, Math.round(v.oy) + .5); c.lineTo(w, Math.round(v.oy) + .5); c.strokeStyle = o.light ? 'rgba(200,0,0,.45)' : 'rgba(230,90,90,.45)'; c.stroke();
  }
  c.lineWidth = o.lw || 1; c.lineJoin = 'round';
  // 同色嘅線一次過畫（快好多）
  const groups = new Map(), special = [];
  for (const e of doc.ents) {
    if (!visible(e)) continue;
    const b = bb(e); if (!b || b[2] < x0 || b[0] > x1 || b[3] < y0 || b[1] > y1) continue;
    const col = colorOf(e, o.light);
    if (e.t === 'text' || e.t === 'dim') { special.push([e, col]); continue; }
    let g = groups.get(col); if (!g) groups.set(col, g = []); g.push(e);
  }
  for (const [col, list] of groups) { c.beginPath(); for (const e of list) pathPrims(c, G.prims(e), v); c.strokeStyle = col; c.stroke(); }
  for (const [e, col] of special) drawEnt(c, e, v, col);
}
function drawBase() { cxB.setTransform(dpr, 0, 0, dpr, 0, 0); paint(cxB, view, W, H, { bg: st.light ? '#ffffff' : '#12151b', light: st.light, grid: st.grid }); }
const ACC = '#4F8CFF';
function drawOverlay() {
  const c = cxO; c.setTransform(dpr, 0, 0, dpr, 0, 0); c.clearRect(0, 0, W, H);
  const fg = st.light ? '#000' : '#fff';
  // 揀中嘅圖元
  const list = selEnts();
  if (list.length) {
    c.lineWidth = 1.6; c.setLineDash([5, 4]);
    for (const e of list) drawEnt(c, e, view, ACC);
    c.setLineDash([]);
    if (!req && list.length <= 300) { c.fillStyle = ACC; c.strokeStyle = fg; c.lineWidth = 1; for (const e of list) for (const g of G.grips(e)) { const s = w2s(g.p); c.fillRect(s[0] - 4, s[1] - 4, 8, 8); c.strokeRect(s[0] - 4.5, s[1] - 4.5, 9, 9); } }
  }
  if (hover && !sel.has(hover.id) && byId.has(hover.id)) { c.lineWidth = 2.5; c.globalAlpha = .8; drawEnt(c, hover, view, ACC); c.globalAlpha = 1; }
  // 預覽
  if (req && req.o.preview) {
    let pv = null; try { pv = req.o.preview(cursor); } catch (e) { }
    if (pv) { c.lineWidth = 1.2; for (const e of pv) if (e) drawEnt(c, e, view, e._c || (st.light ? '#0a58ff' : '#9cc2ff')); }
  }
  if (req && req.o.base && req.kind === 'point') {
    const a = w2s(req.o.base), b = w2s(cursor); c.beginPath(); c.moveTo(a[0], a[1]); c.lineTo(b[0], b[1]); c.setLineDash([3, 4]); c.strokeStyle = st.light ? 'rgba(0,0,0,.5)' : 'rgba(255,255,255,.5)'; c.lineWidth = 1; c.stroke(); c.setLineDash([]);
    const d = dist(req.o.base, cursor); if (d > 0) { const t = fmt(d, 3) + '  <' + deg(ang(sub(cursor, req.o.base))) + '°'; c.font = '12px ' + G.FONT; const w = c.measureText(t).width; c.fillStyle = st.light ? 'rgba(255,255,255,.85)' : 'rgba(20,24,32,.85)'; c.fillRect(b[0] + 14, b[1] + 14, w + 10, 20); c.fillStyle = fg; c.textAlign = 'left'; c.textBaseline = 'middle'; c.fillText(t, b[0] + 19, b[1] + 24); }
  }
  if (win) { const a = w2s(win.a), b = w2s(win.b), cross = b[0] < a[0]; c.fillStyle = cross ? 'rgba(60,200,110,.12)' : 'rgba(79,140,255,.12)'; c.strokeStyle = cross ? '#3cc86e' : ACC; c.lineWidth = 1; if (cross) c.setLineDash([5, 4]); c.fillRect(a[0], a[1], b[0] - a[0], b[1] - a[1]); c.strokeRect(a[0] + .5, a[1] + .5, b[0] - a[0], b[1] - a[1]); c.setLineDash([]); }
  // 十字游標
  const s = w2s(cursor);
  c.strokeStyle = fg; c.lineWidth = 1; c.beginPath();
  if (req && req.kind === 'point') { c.moveTo(s[0] - 40, s[1] + .5); c.lineTo(s[0] + 40, s[1] + .5); c.moveTo(s[0] + .5, s[1] - 40); c.lineTo(s[0] + .5, s[1] + 40); }
  else { c.moveTo(s[0] - 30, s[1] + .5); c.lineTo(s[0] - 5, s[1] + .5); c.moveTo(s[0] + 5, s[1] + .5); c.lineTo(s[0] + 30, s[1] + .5); c.moveTo(s[0] + .5, s[1] - 30); c.lineTo(s[0] + .5, s[1] - 5); c.moveTo(s[0] + .5, s[1] + 5); c.lineTo(s[0] + .5, s[1] + 30); c.rect(s[0] - 4.5, s[1] - 4.5, 10, 10); }
  c.stroke();
  if (snapMark) {
    const m = w2s(snapMark.p), z = 7; c.strokeStyle = '#ffb300'; c.lineWidth = 2; c.beginPath();
    if (snapMark.k === 'end') c.rect(m[0] - z, m[1] - z, 2 * z, 2 * z);
    else if (snapMark.k === 'mid') { c.moveTo(m[0], m[1] - z); c.lineTo(m[0] + z, m[1] + z); c.lineTo(m[0] - z, m[1] + z); c.closePath(); }
    else if (snapMark.k === 'cen') c.arc(m[0], m[1], z, 0, TAU);
    else if (snapMark.k === 'quad') { c.moveTo(m[0], m[1] - z); c.lineTo(m[0] + z, m[1]); c.lineTo(m[0], m[1] + z); c.lineTo(m[0] - z, m[1]); c.closePath(); }
    else { c.moveTo(m[0] - z, m[1] - z); c.lineTo(m[0] + z, m[1] + z); c.moveTo(m[0] + z, m[1] - z); c.lineTo(m[0] - z, m[1] + z); }
    c.stroke();
    c.font = '11px ' + G.FONT; c.fillStyle = '#ffb300'; c.textAlign = 'left'; c.textBaseline = 'bottom'; c.fillText(T('c_s_' + snapMark.k), m[0] + z + 4, m[1] - z);
  }
}
let raf = 0;
function schedule() { if (raf) return; raf = requestAnimationFrame(() => { raf = 0; if (needB) { needB = false; drawBase(); } if (needO) { needO = false; drawOverlay(); } }); }
const redrawO = () => { needO = true; schedule(); };
const redrawAll = () => { needB = needO = true; schedule(); };
function resize() {
  const r = $('cvWrap').getBoundingClientRect(); if (r.width < 10 || r.height < 10) return;
  const oldH = H; W = Math.floor(r.width); H = Math.floor(r.height); dpr = window.devicePixelRatio || 1;
  for (const cv of [cvB, cvO]) { cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr); cv.style.width = W + 'px'; cv.style.height = H + 'px'; }
  if (resize.done) view.oy += H - oldH; else { view.oy = H - 70; resize.done = true; }
  needB = needO = false; drawBase(); drawOverlay();
}
function extents(list = doc.ents.filter(visible)) {
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const e of list) { const b = bb(e); if (!b) continue; x0 = Math.min(x0, b[0]); y0 = Math.min(y0, b[1]); x1 = Math.max(x1, b[2]); y1 = Math.max(y1, b[3]); }
  return x0 > x1 ? null : [x0, y0, x1, y1];
}
function zoomTo(b, pad = 0.08) {
  if (!b) { view.k = 2; view.ox = 80; view.oy = H - 70; redrawAll(); return; }
  const w = Math.max(b[2] - b[0], 1e-9), h = Math.max(b[3] - b[1], 1e-9);
  view.k = Math.min(W * (1 - 2 * pad) / w, H * (1 - 2 * pad) / h); if (!isFinite(view.k) || view.k <= 0) view.k = 1;
  if (b[2] - b[0] < 1e-9 && b[3] - b[1] < 1e-9) view.k = 2;
  view.ox = W / 2 - (b[0] + b[2]) / 2 * view.k; view.oy = H / 2 + (b[1] + b[3]) / 2 * view.k; redrawAll(); status();
}
function zoomBy(f, at = [W / 2, H / 2]) {
  const k = Math.max(1e-7, Math.min(1e7, view.k * f)), w = s2w(at);
  view.k = k; view.ox = at[0] - w[0] * k; view.oy = at[1] + w[1] * k; redrawAll(); status();
}

// ---------- 搵圖元 / 捕捉 ----------
function hitAt(raw, tolPx = 7, filter = pickable) {
  const tol = tolPx / view.k; let best = null, bd = tol;
  for (let i = doc.ents.length - 1; i >= 0; i--) {
    const e = doc.ents[i], b = bb(e);
    if (!b || raw[0] < b[0] - tol || raw[0] > b[2] + tol || raw[1] < b[1] - tol || raw[1] > b[3] + tol || !filter(e)) continue;
    const d = G.entDist(e, raw); if (d < bd) { bd = d; best = e; }
  }
  return best;
}
function findSnap(raw) {
  const tol = 11 / view.k, near = []; let best = null, bs = Infinity;
  const W8 = { end: 1, int: 1, mid: 1.15, cen: 1.15, quad: 1.3 };
  const put = (k, p) => { const d = dist(p, raw); if (d <= tol && d * W8[k] < bs) { bs = d * W8[k]; best = { k, p: [p[0], p[1]] }; } };
  for (let i = doc.ents.length - 1; i >= 0 && near.length < 60; i--) {
    const e = doc.ents[i], b = bb(e);
    if (!b || raw[0] < b[0] - tol || raw[0] > b[2] + tol || raw[1] < b[1] - tol || raw[1] > b[3] + tol || !visible(e)) continue;
    if (e.t === 'circle' || e.t === 'arc') put('cen', e.c);
    if (G.entDist(e, raw) > tol * 1.5) continue;
    near.push(e);
    for (const [k, p] of G.snapPoints(e)) put(k, p);
  }
  const pr = near.slice(0, 10).map(e => G.prims(e).filter(p => dist(G.pNear(p, raw), raw) <= tol * 1.5));
  for (let i = 0; i < pr.length; i++) for (let j = i + 1; j < pr.length; j++) for (const p of pr[i]) for (const q of pr[j]) for (const x of G.pInter(p, q)) put('int', x);
  return best;
}
function gridSnap(p) { const s = nice(14 / view.k); return [Math.round(p[0] / s) * s, Math.round(p[1] / s) * s]; }
function orthoFrom(b, p) { const d = sub(p, b); return Math.abs(d[0]) >= Math.abs(d[1]) ? [p[0], b[1]] : [b[0], p[1]]; }
function resolveCursor(raw) {
  let p = raw; snapMark = null;
  if (req && req.kind === 'point') {
    const s = st.osnap && !req.o.nosnap ? findSnap(raw) : null;
    if (s) { p = s.p; snapMark = s; }
    else { if (st.gsnap) p = gridSnap(p); if ((st.ortho !== shiftDown) && req.o.base && !req.o.noortho) p = orthoFrom(req.o.base, p); }
  }
  cursor = p; cursorRaw = raw;
}
function segRect(a, b, r) {   // 線段同矩形有冇相交
  let t0 = 0, t1 = 1; const d = sub(b, a);
  for (const [p, q] of [[-d[0], a[0] - r[0]], [d[0], r[2] - a[0]], [-d[1], a[1] - r[1]], [d[1], r[3] - a[1]]]) {
    if (Math.abs(p) < 1e-300) { if (q < 0) return false; continue; }
    const t = q / p; if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; } else { if (t < t0) return false; if (t < t1) t1 = t; }
  }
  return true;
}
function windowPick(a, b) {
  const r = [Math.min(a[0], b[0]), Math.min(a[1], b[1]), Math.max(a[0], b[0]), Math.max(a[1], b[1])], crossing = b[0] < a[0], out = [];
  for (const e of doc.ents) {
    const q = bb(e); if (!q || !pickable(e) || q[2] < r[0] || q[0] > r[2] || q[3] < r[1] || q[1] > r[3]) continue;
    if (q[0] >= r[0] && q[2] <= r[2] && q[1] >= r[1] && q[3] <= r[3]) { out.push(e); continue; }
    if (!crossing) continue;
    const pts = e.t === 'text' ? [...G.textBox(e), G.textBox(e)[0]] : G.tess(e, TAU / 48);
    for (let i = 0; i + 1 < pts.length; i++) if (segRect(pts[i], pts[i + 1], r)) { out.push(e); break; }
  }
  return out;
}

// ---------- 指令輸入 ----------
function log(s) { const h = $('hist'), d = el('div', '', s); h.appendChild(d); while (h.children.length > 60) h.firstChild.remove(); h.scrollTop = 1e9; }
function setPrompt(s) { $('cprompt').textContent = s || T('c_ready'); }
/** kind：point（點）/ ent（揀一個圖元）/ ents（揀多個）/ str（打字）。o：base、kws:[{k,label}]、preview(pt)、num（接受數字）、def。 */
function ask(kind, prompt, o = {}) {
  return new Promise((res, rej) => {
    req = { kind, o, res, rej };
    const kw = (o.kws || []).length ? ' [' + o.kws.map(k => k.label + '(' + k.k + ')').join(' ') + ']' : '';
    setPrompt(prompt + kw + (o.def != null && o.def !== '' ? ' <' + o.def + '>' : '') + '：');
    resolveCursor(cursorRaw); redrawO();
  });
}
function answer(v) { const r = req; if (!r) return; req = null; snapMark = null; if (Array.isArray(v) && r.kind === 'point') lastPt = v; r.res(v); redrawO(); }
function cancel() { win = null; if (req) { const r = req; req = null; snapMark = null; r.rej(CANCEL); } }
function typed(text) {
  const s = text.trim();
  if (!req) { if (!s) { if (st.last) run(st.last); return; } const c = ALIAS[s.toUpperCase()]; if (c) run(c); else log(T('c_unknown', s)); return; }
  const o = req.o;
  if (!s) { if (req.kind === 'ents') answer(selEnts()); else answer(null); return; }
  const kw = (o.kws || []).find(k => k.k.toUpperCase() === s.toUpperCase());
  if (kw) { answer({ kw: kw.k }); return; }
  if (req.kind === 'str') { answer(s); return; }
  if (req.kind === 'ents') { if (s.toUpperCase() === 'ALL') { doc.ents.filter(pickable).forEach(e => sel.add(e.id)); redrawO(); sidePanel(); } else log(T('c_badinput')); return; }
  if (req.kind !== 'point') { log(T('c_badinput')); return; }
  const base = o.base || lastPt; let m;
  if ((m = /^(@|#)?\s*(-?[\d.]+(?:e-?\d+)?)\s*,\s*(-?[\d.]+(?:e-?\d+)?)$/i.exec(s))) { const p = [parseFloat(m[2]), parseFloat(m[3])]; if (isFinite(p[0]) && isFinite(p[1])) { answer(m[1] === '@' ? add(base, p) : p); return; } }
  if ((m = /^@?\s*(-?[\d.]+)\s*<\s*(-?[\d.]+)$/.exec(s))) { const d = parseFloat(m[1]), a = parseFloat(m[2]) * Math.PI / 180; if (isFinite(d) && isFinite(a)) { answer(polar(base, d, a)); return; } }
  if (/^-?[\d.]+(e-?\d+)?$/i.test(s) && isFinite(parseFloat(s))) {
    const v = parseFloat(s);
    if (o.num) { answer({ num: v }); return; }
    if (o.base) { const d = sub(cursor, o.base); answer(add(o.base, mul(G.len(d) < 1e-12 ? [1, 0] : norm(d), v))); return; }
  }
  log(T('c_badinput'));
}
async function run(name) {
  const c = CMDS[name]; if (!c) return;
  cancel(); const tok = ++runTok; running = name; if (!c.quiet) st.last = name;
  log('› ' + c.label());
  try { await c.fn(); }
  catch (e) { if (e !== CANCEL) { console.error(e); log(String(e && e.message || e)); } else if (tok === runTok) log(T('c_cancelled')); }
  if (tok === runTok) { running = ''; req = null; win = null; snapMark = null; setPrompt(''); redrawO(); toolState(); }
}
// 指令入面用嘅小工具
const KW = (k, key) => ({ k, label: T(key) });
async function getPt(prompt, o) { const p = await ask('point', prompt, o); if (p == null) throw CANCEL; return p; }
async function getEnts() {
  if (sel.size) return selEnts();
  const r = await ask('ents', T('c_p_select'));
  if (!r || !r.length) throw CANCEL;
  return r;
}
async function getNum(prompt, def, o = {}) {
  for (;;) { const s = await ask('str', prompt, { ...o, def: def == null ? '' : fmt(def, 6) }); if (s == null) { if (def == null) throw CANCEL; return def; } if (s.kw) return s; const v = parseFloat(s); if (isFinite(v)) return v; log(T('c_badinput')); }
}
const moved = (list, T2) => list.map(e => G.applyT(e, T2));

// ---------- 指令 ----------
const CMDS = {
  line: { key: 'c_line', a: ['L', 'LINE'], async fn() {
    const pts = [await getPt(T('c_p_first'))];
    for (;;) {
      const last = pts[pts.length - 1];
      const q = await ask('point', T('c_p_next'), { base: last, kws: [...(pts.length > 1 ? [KW('U', 'undo')] : []), ...(pts.length > 2 ? [KW('C', 'c_k_close')] : [])], preview: x => [{ t: 'line', a: last, b: x }] });
      if (q == null) return;
      if (q.kw === 'U') { undo(); pts.pop(); continue; }
      if (q.kw === 'C') { addEnts([{ t: 'line', a: last, b: pts[0] }]); return; }
      if (dist(q, last) < 1e-12) continue;
      addEnts([{ t: 'line', a: last, b: q }]); pts.push(q);
    }
  } },
  pline: { key: 'c_pline', a: ['PL', 'PLINE'], async fn() {
    const pts = [await getPt(T('c_p_first'))];
    const done = closed => { if (pts.length >= 2) addEnts([{ t: 'pline', pts: pts.map(p => [p[0], p[1], 0]), closed }]); };
    for (;;) {
      const q = await ask('point', T('c_p_next'), { base: pts[pts.length - 1], kws: [...(pts.length > 1 ? [KW('U', 'undo')] : []), ...(pts.length > 2 ? [KW('C', 'c_k_close')] : [])], preview: x => [{ t: 'pline', pts: [...pts, x].map(p => [p[0], p[1], 0]), closed: false }] }).catch(e => { done(false); throw e; });
      if (q == null) { done(false); return; }
      if (q.kw === 'U') { pts.pop(); continue; }
      if (q.kw === 'C') { done(true); return; }
      if (dist(q, pts[pts.length - 1]) > 1e-12) pts.push(q);
    }
  } },
  rect: { key: 'c_rect', a: ['REC', 'RECT', 'RECTANG', 'RECTANGLE'], async fn() {
    const a = await getPt(T('c_p_corner1')), R = b => ({ t: 'pline', pts: [[a[0], a[1], 0], [b[0], a[1], 0], [b[0], b[1], 0], [a[0], b[1], 0]], closed: true });
    const b = await getPt(T('c_p_corner2'), { base: a, noortho: true, preview: x => [R(x)] });
    if (Math.abs(b[0] - a[0]) > 1e-12 && Math.abs(b[1] - a[1]) > 1e-12) addEnts([R(b)]);
  } },
  circle: { key: 'c_circle', a: ['C', 'CIRCLE'], async fn() {
    const c = await getPt(T('c_p_center'), { kws: [KW('2P', 'c_k_2p'), KW('3P', 'c_k_3p')] });
    if (c.kw === '2P') { const a = await getPt(T('c_p_first')), b = await getPt(T('c_p_second'), { base: a, preview: x => [{ t: 'circle', c: lerp(a, x, .5), r: dist(a, x) / 2 }] }); if (dist(a, b) > 1e-12) addEnts([{ t: 'circle', c: lerp(a, b, .5), r: dist(a, b) / 2 }]); return; }
    if (c.kw === '3P') { const a = await getPt(T('c_p_first')), b = await getPt(T('c_p_second'), { base: a }), k = x => { const q = G.circle3(a, b, x); return q ? { t: 'circle', ...q } : null; }; const d = await getPt(T('c_p_third'), { preview: x => [k(x)] }); if (k(d)) addEnts([k(d)]); else log(T('c_collinear')); return; }
    const r = await getPt(T('c_p_radius'), { base: c, num: true, kws: [KW('D', 'c_k_diameter')], preview: x => [{ t: 'circle', c, r: dist(c, x) }] });
    let rad = r.num != null ? r.num : Array.isArray(r) ? dist(c, r) : 0;
    if (r.kw === 'D') { const d = await getNum(T('c_p_diameter'), null); rad = d / 2; }
    if (rad > 1e-12) addEnts([{ t: 'circle', c, r: rad }]);
  } },
  arc: { key: 'c_arc', a: ['A', 'ARC'], async fn() {
    const a = await getPt(T('c_p_arc1')), m = await getPt(T('c_p_arc2'), { base: a }), k = x => { const q = G.arc3(a, m, x); return q ? { t: 'arc', ...q } : null; };
    const b = await getPt(T('c_p_arc3'), { preview: x => [k(x)] });
    if (k(b)) addEnts([k(b)]); else log(T('c_collinear'));
  } },
  polygon: { key: 'c_polygon', a: ['POL', 'POLYGON'], async fn() {
    const n = Math.round(await getNum(T('c_p_sides'), st.sides || 6)); if (n < 3 || n > 1024) { log(T('c_badinput')); return; } st.sides = n;
    const c = await getPt(T('c_p_center')), P = x => { const r = dist(c, x), a0 = ang(sub(x, c)); return { t: 'pline', pts: Array.from({ length: n }, (_, i) => { const p = polar(c, r, a0 + i * TAU / n); return [p[0], p[1], 0]; }), closed: true }; };
    const r = await getPt(T('c_p_radius'), { base: c, num: true, preview: x => [P(x)] });
    const x = r.num != null ? polar(c, r.num, Math.PI / 2) : r; if (dist(c, x) > 1e-12) addEnts([P(x)]);
  } },
  text: { key: 'c_text', a: ['T', 'TEXT', 'DT', 'MT', 'MTEXT'], async fn() {
    const p = await getPt(T('c_p_textpos')), r = await textDialog({ s: '', h: autoH(), rot: 0 });
    if (r && r.s.trim()) { st.textH = r.h; addEnts([{ t: 'text', p, h: r.h, s: r.s, rot: r.rot }]); }
  } },
  dim: { key: 'c_dim', a: ['DIM', 'DLI', 'DIMLINEAR'], fn: () => dimCmd(false) },
  dimal: { key: 'c_dimal', a: ['DAL', 'DIMALIGNED'], fn: () => dimCmd(true) },
  move: { key: 'c_move', a: ['M', 'MOVE'], async fn() {
    const list = await getEnts(), b = await getPt(T('c_p_base')), q = await getPt(T('c_p_to'), { base: b, preview: x => moved(list, G.tMove(sub(x, b))) });
    replaceEnts(new Map(list.map(e => [e.id, [G.applyT(e, G.tMove(sub(q, b)))]])));
  } },
  copy: { key: 'c_copy', a: ['CO', 'CP', 'COPY'], async fn() {
    const list = await getEnts(), b = await getPt(T('c_p_base'));
    for (;;) { const q = await ask('point', T('c_p_to'), { base: b, preview: x => moved(list, G.tMove(sub(x, b))) }); if (q == null) return; addEnts(moved(list, G.tMove(sub(q, b))).map(({ id, ...e }) => e)); }
  } },
  rotate: { key: 'c_rotate', a: ['RO', 'ROTATE'], async fn() {
    const list = await getEnts(), b = await getPt(T('c_p_base')), q = await getPt(T('c_p_angle'), { base: b, num: true, preview: x => moved(list, G.tRot(b, ang(sub(x, b)))) });
    const a = q.num != null ? q.num * Math.PI / 180 : ang(sub(q, b));
    replaceEnts(new Map(list.map(e => [e.id, [G.applyT(e, G.tRot(b, a))]])));
  } },
  scale: { key: 'c_scale', a: ['SC', 'SCALE'], async fn() {
    const list = await getEnts(), b = await getPt(T('c_p_base')), f = await getNum(T('c_p_factor'), 1);
    if (!(f > 0)) { log(T('c_badinput')); return; }
    replaceEnts(new Map(list.map(e => [e.id, [G.applyT(e, G.tScale(b, f))]])));
  } },
  mirror: { key: 'c_mirror', a: ['MI', 'MIRROR'], async fn() {
    const list = await getEnts(), a = await getPt(T('c_p_mirror1')), b = await getPt(T('c_p_mirror2'), { base: a, preview: x => dist(a, x) > 1e-12 ? moved(list, G.tMirror(a, x)) : [] });
    if (dist(a, b) < 1e-12) return;
    const k = await ask('str', T('c_p_delsrc'), { kws: [KW('Y', 'c_k_yes'), KW('N', 'c_k_no')], def: 'N' });
    const out = moved(list, G.tMirror(a, b));
    if (k && k.kw === 'Y') replaceEnts(new Map(list.map((e, i) => [e.id, [out[i]]]))); else addEnts(out.map(({ id, ...e }) => e));
  } },
  offset: { key: 'c_offset', a: ['O', 'OFFSET'], async fn() {
    const d = await getNum(T('c_p_offdist'), st.offD); if (!(d > 0)) { log(T('c_badinput')); return; } st.offD = d; sel.clear(); redrawO();
    for (;;) {
      const h = await ask('ent', T('c_p_offobj')); if (!h) return;
      if (!['line', 'circle', 'arc', 'pline'].includes(h.e.t)) { log(T('c_cantoffset')); continue; }
      const P = x => G.offset(h.e, d, G.leftOf(h.e, x)), q = await ask('point', T('c_p_offside'), { nosnap: true, preview: x => [P(x)] });
      if (q == null) return; const n = P(q); if (n) addEnts([n]); else log(T('c_cantoffset'));
    }
  } },
  array: { key: 'c_array', a: ['AR', 'ARRAY'], async fn() {
    const list = await getEnts(), b = extents(list), r = await formDialog(T('c_array'), [['rows', T('c_f_rows'), 2], ['cols', T('c_f_cols'), 3], ['dy', T('c_f_rowgap'), fmt((b[3] - b[1]) * 1.5 || 10, 3)], ['dx', T('c_f_colgap'), fmt((b[2] - b[0]) * 1.5 || 10, 3)]]);
    if (!r) return; const rows = Math.round(r.rows), cols = Math.round(r.cols);
    if (!(rows >= 1 && cols >= 1 && rows * cols > 1) || rows * cols * list.length > 200000) { log(T('c_badinput')); return; }
    const out = []; for (let i = 0; i < rows; i++) for (let j = 0; j < cols; j++) if (i || j) out.push(...moved(list, G.tMove([j * r.dx, i * r.dy])).map(({ id, ...e }) => e));
    addEnts(out);
  } },
  trim: { key: 'c_trim', a: ['TR', 'TRIM'], async fn() {
    sel.clear(); redrawO();
    for (;;) {
      const h = await ask('ent', T('c_p_trim'), { kws: [KW('U', 'undo')] }); if (!h) return;
      if (h.kw === 'U') { undo(); continue; }
      const r = G.trimAt(h.e, doc.ents.filter(visible), h.pt);
      if (!r) { log(T('c_notrim')); continue; }
      if (r.length) replaceEnts(new Map([[h.e.id, r]])); else removeEnts([h.e.id]);
    }
  } },
  extend: { key: 'c_extend', a: ['EX', 'EXTEND'], async fn() {
    sel.clear(); redrawO();
    for (;;) {
      const h = await ask('ent', T('c_p_extend'), { kws: [KW('U', 'undo')] }); if (!h) return;
      if (h.kw === 'U') { undo(); continue; }
      const r = G.extendAt(h.e, doc.ents.filter(visible), h.pt);
      if (!r) { log(T('c_noextend')); continue; }
      replaceEnts(new Map([[h.e.id, [r]]]));
    }
  } },
  fillet: { key: 'c_fillet', a: ['F', 'FILLET'], async fn() {
    sel.clear(); redrawO();
    for (;;) {
      const a = await ask('ent', T('c_p_fillet1', fmt(st.filR, 4)), { kws: [KW('R', 'c_k_radius')] }); if (!a) return;
      if (a.kw === 'R') { const r = await getNum(T('c_p_filletr'), st.filR); if (r >= 0) st.filR = r; continue; }
      if (a.e.t !== 'line') { log(T('c_filletline')); continue; }
      const b = await ask('ent', T('c_p_fillet2')); if (!b) return;
      if (b.e.t !== 'line' || b.e.id === a.e.id) { log(T('c_filletline')); continue; }
      const r = G.fillet(a.e, a.pt, b.e, b.pt, st.filR);
      if (!r) { log(T('c_nofillet')); continue; }
      replaceEnts(new Map([[a.e.id, r.arc ? [r.a, r.arc] : [r.a]], [b.e.id, [r.b]]]));
      return;
    }
  } },
  explode: { key: 'c_explode', a: ['X', 'EXPLODE'], async fn() {
    const list = await getEnts(), map = new Map();
    for (const e of list) {
      if (e.t === 'pline') map.set(e.id, G.prims(e).map(p => p.t === 'l' ? { t: 'line', a: p.a, b: p.b } : { t: 'arc', c: p.c, r: p.r, a0: G.normA(p.a0), a1: G.normA(p.a1) }));
      else if (e.t === 'dim') { const g = G.dimGeo(e), d = [Math.cos(g.text.rot), Math.sin(g.text.rot)]; map.set(e.id, [...g.segs.map(([a, b]) => ({ t: 'line', a, b })), ...g.tris.map(t => ({ t: 'pline', pts: t.map(p => [p[0], p[1], 0]), closed: true })), { t: 'text', p: add(g.text.p, mul(d, -G.textW(g.text.s, e.h) / 2)), h: e.h, s: g.text.s, rot: g.text.rot }]); }
    }
    if (map.size) { replaceEnts(map); sel.clear(); redrawO(); sidePanel(); } else log(T('c_noexplode'));
  } },
  erase: { key: 'c_erase', a: ['E', 'ERASE', 'DEL', 'DELETE'], async fn() { const list = await getEnts(); removeEnts(list.map(e => e.id)); } },
  dist: { key: 'c_dist', a: ['DI', 'DIST'], quiet: true, async fn() {
    const a = await getPt(T('c_p_first')), b = await getPt(T('c_p_second'), { base: a });
    log(T('c_distres', fmt(dist(a, b), 4), fmt(b[0] - a[0], 4), fmt(b[1] - a[1], 4), deg(ang(sub(b, a)))));
  } },
  zoom: { key: 'c_zoomwin', a: ['Z', 'ZOOM'], quiet: true, async fn() {
    const a = await getPt(T('c_p_zoom'), { kws: [KW('E', 'c_k_extents')], nosnap: true });
    if (a.kw === 'E') { zoomTo(extents()); return; }
    const b = await getPt(T('c_p_corner2'), { nosnap: true, preview: x => [{ t: 'pline', pts: [[a[0], a[1], 0], [x[0], a[1], 0], [x[0], x[1], 0], [a[0], x[1], 0]], closed: true }] });
    zoomTo([Math.min(a[0], b[0]), Math.min(a[1], b[1]), Math.max(a[0], b[0]), Math.max(a[1], b[1])], 0);
  } },
  paste: { key: 'paste', a: [], quiet: true, async fn() {
    if (!clip || !clip.ents.length) return;
    const q = await getPt(T('c_p_to'), { preview: x => moved(clip.ents, G.tMove(sub(x, clip.base))) });
    const names = new Set(doc.layers.map(l => l.name));
    const out = addEnts(moved(clip.ents, G.tMove(sub(q, clip.base))).map(({ id, ...e }) => ({ ...e, layer: names.has(e.layer) ? e.layer : doc.cur })));
    sel = new Set(out.map(e => e.id)); redrawO(); sidePanel();
  } },
  undo: { key: 'undo', a: ['U', 'UNDO'], quiet: true, async fn() { if (!undo()) log(T('c_noundo')); } },
  redo: { key: 'redo', a: ['REDO'], quiet: true, async fn() { if (!redo()) log(T('c_noundo')); } },
  layer: { key: 'c_layers', a: ['LA', 'LAYER'], quiet: true, async fn() { st.side = true; layout(); newLayer(); } },
};
for (const k in CMDS) { const c = CMDS[k]; c.label = () => T(c.key); }
const ALIAS = {}; for (const k in CMDS) for (const a of CMDS[k].a) ALIAS[a] = k;
async function dimCmd(aligned) {
  const a = await getPt(T('c_p_dim1')), b = await getPt(T('c_p_dim2'), { base: a });
  if (dist(a, b) < 1e-12) return;
  const h = autoH();
  const D = x => {
    let mode = 'al';
    if (!aligned) { const ox = Math.max(0, Math.min(a[0], b[0]) - x[0], x[0] - Math.max(a[0], b[0])), oy = Math.max(0, Math.min(a[1], b[1]) - x[1], x[1] - Math.max(a[1], b[1])); mode = Math.abs(a[1] - b[1]) < 1e-12 ? 'h' : Math.abs(a[0] - b[0]) < 1e-12 ? 'v' : ox > oy ? 'v' : 'h'; }
    return { t: 'dim', a, b, p: x, mode, h };
  };
  const p = await getPt(T('c_p_dimpos'), { nosnap: true, preview: x => [D(x)] });
  st.textH = h; addEnts([D(p)]);
}
function gripEdit(e, g) {
  CMDS._grip = { key: 'c_stretch', a: [], quiet: true, label: () => T('c_stretch'), async fn() {
    const q = await getPt(T('c_p_to'), { base: g.p, preview: x => [g.set(e, x, g.p)] });
    const n = g.set(e, q, g.p); if (n.t === 'arc') { n.a0 = G.normA(n.a0); n.a1 = G.normA(n.a1); }
    replaceEnts(new Map([[e.id, [n]]]));
  } };
  run('_grip');
}

// ---------- 對話框 ----------
function formDialog(title, fields) {
  return new Promise(res => {
    const body = el('div'), inputs = {};
    for (const [k, label, def] of fields) { body.appendChild(el('label', '', label)); const i = el('input'); i.type = 'text'; i.value = def; inputs[k] = i; body.appendChild(i); }
    let done = false;
    const d = dialog(title, body, [[T('c_cancel'), () => { done = true; res(null); }], [T('c_ok'), () => { const o = {}; for (const k in inputs) { o[k] = parseFloat(inputs[k].value); if (!isFinite(o[k])) { inputs[k].focus(); return false; } } done = true; res(o); }, true]]);
    d.el.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Enter') d.el.querySelector('.rb.pri').click(); });
    new MutationObserver(() => { if (!document.body.contains(d.el) && !done) res(null); }).observe(document.body, { childList: true });
  });
}
function textDialog(v) {
  return new Promise(res => {
    const body = el('div'), ta = el('textarea'); ta.value = v.s; ta.rows = 4; ta.style.minWidth = '340px'; body.appendChild(el('label', '', T('c_f_text'))); body.appendChild(ta);
    const hI = el('input'), rI = el('input'); hI.type = rI.type = 'text'; hI.value = fmt(v.h, 4); rI.value = fmt((v.rot || 0) * 180 / Math.PI, 2);
    body.appendChild(el('label', '', T('c_f_height'))); body.appendChild(hI); body.appendChild(el('label', '', T('c_f_rot'))); body.appendChild(rI);
    let done = false;
    const d = dialog(T('c_text'), body, [[T('c_cancel'), () => { done = true; res(null); }], [T('c_ok'), () => { const h = parseFloat(hI.value); if (!(h > 0)) { hI.focus(); return false; } done = true; res({ s: ta.value.replace(/\r/g, ''), h, rot: (parseFloat(rI.value) || 0) * Math.PI / 180 }); }, true]]);
    d.el.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Enter' && e.ctrlKey) d.el.querySelector('.rb.pri').click(); });
    new MutationObserver(() => { if (!document.body.contains(d.el) && !done) res(null); }).observe(document.body, { childList: true });
    setTimeout(() => ta.focus(), 30);
  });
}
function colorDialog(cur, allowByLayer) {
  return new Promise(res => {
    const body = el('div', 'cgrid'); let done = false, d = null;
    const sw = (i, label) => { const b = el('button', 'csw' + (cur === i ? ' on' : '')); b.type = 'button'; b.title = label || String(i); if (i == null) b.textContent = T('c_bylayer'); else b.style.background = aciHex(i); b.onclick = () => { done = true; res({ v: i }); d.close(); }; return b; };
    if (allowByLayer) { const r = el('div', 'crow'); const b = sw(null); b.classList.add('wide'); r.appendChild(b); body.appendChild(r); }
    const r1 = el('div', 'crow basic'); for (let i = 1; i <= 9; i++) r1.appendChild(sw(i)); for (let i = 250; i <= 255; i++) r1.appendChild(sw(i)); body.appendChild(r1);
    for (let d2 = 0; d2 < 10; d2 += 2) { const r = el('div', 'crow'); for (let h = 0; h < 24; h++) r.appendChild(sw(10 + h * 10 + d2)); body.appendChild(r); }
    for (let d2 = 1; d2 < 10; d2 += 2) { const r = el('div', 'crow'); for (let h = 0; h < 24; h++) r.appendChild(sw(10 + h * 10 + d2)); body.appendChild(r); }
    d = dialog(T('c_color'), body, [[T('c_cancel'), () => { done = true; res(null); }]]);
    new MutationObserver(() => { if (!document.body.contains(d.el) && !done) res(null); }).observe(document.body, { childList: true });
  });
}

// ---------- 側欄：圖層 + 內容 ----------
function newLayer() {
  let n = 1; while (doc.layers.some(l => l.name === T('c_layer') + ' ' + n)) n++;
  mark(); doc.layers.push({ name: T('c_layer') + ' ' + n, color: 7, on: true, lock: false }); doc.cur = doc.layers[doc.layers.length - 1].name; changed();
}
function sidePanel() {
  ribbonState();
  const box = $('side'); if (!box || !st.side) return;
  box.textContent = '';
  const head = el('div', 'shead'); head.appendChild(el('b', '', T('c_layers'))); head.appendChild(el('span', 'grow'));
  const addB = el('button', 'rb sm', '＋'); addB.title = T('c_newlayer'); addB.onclick = newLayer; head.appendChild(addB); box.appendChild(head);
  const list = el('div', 'llist');
  for (const l of doc.layers) {
    const row = el('div', 'lrow' + (l.name === doc.cur ? ' cur' : ''));
    const eye = el('button', 'li' + (l.on === false ? ' off' : ''), l.on === false ? '○' : '●'); eye.title = T('c_l_show');
    eye.onclick = e => { e.stopPropagation(); mark(); l.on = l.on === false; changed(); };
    const lock = el('button', 'li' + (l.lock ? '' : ' off'), l.lock ? '🔒' : '🔓'); lock.title = T('c_l_lock');
    lock.onclick = e => { e.stopPropagation(); mark(); l.lock = !l.lock; changed(); };
    const sw = el('button', 'lsw'); sw.style.background = typeof l.color === 'string' ? l.color : aciHex(l.color); sw.title = T('c_color');
    sw.onclick = async e => { e.stopPropagation(); const r = await colorDialog(l.color, false); if (r && r.v != null) { mark(); l.color = r.v; bbTouch(); changed(); } };
    const name = el('span', 'lname', l.name); name.title = T('c_l_tip');
    row.append(eye, lock, sw, name);
    row.onclick = () => { if (doc.cur !== l.name) { doc.cur = l.name; sidePanel(); } };
    name.ondblclick = e => { e.stopPropagation(); renameLayer(l); };
    if (l.name !== '0') { const del = el('button', 'li del', '✕'); del.title = T('c_l_del'); del.onclick = e => { e.stopPropagation(); if (doc.ents.some(x => x.layer === l.name)) { toast(T('c_l_inuse')); return; } mark(); doc.layers = doc.layers.filter(x => x !== l); if (doc.cur === l.name) doc.cur = '0'; changed(); }; row.appendChild(del); }
    list.appendChild(row);
  }
  box.appendChild(list);
  // 內容
  const es = selEnts(), ph = el('div', 'shead'); ph.appendChild(el('b', '', T('c_props'))); box.appendChild(ph);
  const pr = el('div', 'props');
  if (!es.length) pr.appendChild(el('div', 'dim', T('c_nosel')));
  else {
    const kinds = [...new Set(es.map(e => e.t))];
    pr.appendChild(el('div', 'pt', kinds.length === 1 ? T('c_t_' + kinds[0]) + (es.length > 1 ? ' × ' + es.length : '') : T('c_nsel', es.length)));
    const row = (label, node) => { const r = el('div', 'prow'); r.appendChild(el('span', 'pl', label)); if (typeof node === 'string') r.appendChild(el('span', 'pv', node)); else r.appendChild(node); pr.appendChild(r); };
    const ls = el('select'); for (const l of doc.layers) { const o = el('option', '', l.name); o.value = l.name; ls.appendChild(o); }
    const same = es.every(e => e.layer === es[0].layer); if (same) ls.value = es[0].layer; else { const o = el('option', '', '—'); o.value = ''; ls.prepend(o); ls.value = ''; }
    ls.onchange = () => { if (ls.value) replaceEnts(new Map(es.map(e => [e.id, [{ ...e, layer: ls.value }]]))); };
    row(T('c_layer'), ls);
    const cb = el('button', 'rb sm'), c0 = es[0].color, sameC = es.every(e => e.color === c0);
    cb.textContent = !sameC ? '—' : c0 == null ? T('c_bylayer') : typeof c0 === 'string' ? c0 : '■ ' + c0; if (sameC && c0 != null) cb.style.color = typeof c0 === 'string' ? c0 : aciHex(c0);
    cb.onclick = async () => { const r = await colorDialog(sameC ? c0 : undefined, true); if (r) replaceEnts(new Map(es.map(e => [e.id, [{ ...e, color: r.v }]]))); };
    row(T('c_color'), cb);
    if (es.length === 1) {
      const e = es[0];
      const numIn = (label, val, apply) => { const i = el('input'); i.type = 'text'; i.value = fmt(val, 4); i.onchange = () => { const v = parseFloat(i.value); if (isFinite(v)) { const n = apply(v); if (n) replaceEnts(new Map([[e.id, [n]]])); } else i.value = fmt(val, 4); }; i.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter') i.blur(); }); row(label, i); };
      if (e.t === 'line') { row(T('c_i_len'), fmt(dist(e.a, e.b), 4)); row(T('c_i_angle'), deg(ang(sub(e.b, e.a))) + '°'); numIn('X1', e.a[0], v => ({ ...e, a: [v, e.a[1]] })); numIn('Y1', e.a[1], v => ({ ...e, a: [e.a[0], v] })); numIn('X2', e.b[0], v => ({ ...e, b: [v, e.b[1]] })); numIn('Y2', e.b[1], v => ({ ...e, b: [e.b[0], v] })); }
      else if (e.t === 'circle' || e.t === 'arc') { numIn(T('c_i_radius'), e.r, v => v > 0 ? { ...e, r: v } : null); numIn('X', e.c[0], v => ({ ...e, c: [v, e.c[1]] })); numIn('Y', e.c[1], v => ({ ...e, c: [e.c[0], v] })); row(T('c_i_len'), fmt(G.entLen(e), 4)); if (e.t === 'circle') row(T('c_i_area'), fmt(G.entArea(e), 4)); else { numIn(T('c_i_a0'), G.normA(e.a0) * 180 / Math.PI, v => ({ ...e, a0: G.normA(v * Math.PI / 180) })); numIn(T('c_i_a1'), G.normA(e.a1) * 180 / Math.PI, v => ({ ...e, a1: G.normA(v * Math.PI / 180) })); } }
      else if (e.t === 'pline') { row(T('c_i_len'), fmt(G.entLen(e), 4)); if (e.closed) row(T('c_i_area'), fmt(G.entArea(e), 4)); row(T('c_i_verts'), String(e.pts.length)); const cb2 = el('input'); cb2.type = 'checkbox'; cb2.checked = !!e.closed; cb2.onchange = () => replaceEnts(new Map([[e.id, [{ ...e, closed: cb2.checked }]]])); row(T('c_i_closed'), cb2); }
      else if (e.t === 'text') { numIn(T('c_f_height'), e.h, v => v > 0 ? { ...e, h: v } : null); numIn(T('c_f_rot'), (e.rot || 0) * 180 / Math.PI, v => ({ ...e, rot: G.normA(v * Math.PI / 180) })); const b = el('button', 'rb sm', T('c_edittext')); b.onclick = () => editText(e); row(T('c_f_text'), b); }
      else if (e.t === 'dim') { row(T('c_i_value'), fmt(G.dimGeo(e).val, 4)); numIn(T('c_f_height'), e.h, v => v > 0 ? { ...e, h: v } : null); const b = el('button', 'rb sm', T('c_edittext')); b.onclick = () => editText(e); row(T('c_f_text'), b); }
    } else { const L = es.reduce((s, e) => s + (e.t === 'text' || e.t === 'dim' ? 0 : G.entLen(e)), 0); if (L > 0) row(T('c_i_len'), fmt(L, 4)); }
  }
  box.appendChild(pr);
}
function bbTouch() { needB = true; }
function renameLayer(l) {
  if (l.name === '0') { toast(T('c_l_zero')); return; }
  const body = el('div'), i = el('input'); i.type = 'text'; i.value = l.name; body.appendChild(i);
  const d = dialog(T('c_l_rename'), body, [[T('c_cancel')], [T('c_ok'), () => { const v = i.value.trim(); if (!v || doc.layers.some(x => x !== l && x.name === v)) { i.focus(); return false; } mark(); const old = l.name; doc.ents = doc.ents.map(e => e.layer === old ? { ...e, layer: v } : e); if (doc.cur === old) doc.cur = v; l.name = v; changed(); }, true]]);
  d.el.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Enter') d.el.querySelector('.rb.pri').click(); });
}
async function editText(e) {
  if (e.t === 'dim') { const body = el('div'), i = el('input'); i.type = 'text'; i.value = e.s || ''; i.placeholder = fmt(G.dimGeo({ ...e, s: '' }).val); body.appendChild(el('label', '', T('c_dimtext'))); body.appendChild(i);
    const d = dialog(T('c_dim'), body, [[T('c_cancel')], [T('c_ok'), () => { const n = { ...e }; if (i.value.trim()) n.s = i.value.trim(); else delete n.s; replaceEnts(new Map([[e.id, [n]]])); }, true]]);
    d.el.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter') d.el.querySelector('.rb.pri').click(); }); return; }
  const r = await textDialog(e); if (r && r.s.trim()) replaceEnts(new Map([[e.id, [{ ...e, ...r }]]]));
}

// ---------- 工具列 / 狀態列 ----------
const IC = {
  line: 'M4 20L20 4', pline: 'M3 19L9 7l6 9 6-11', rect: 'M4 6h16v12H4z', circle: 'M12 12m-8 0a8 8 0 1 0 16 0a8 8 0 1 0-16 0', arc: 'M4 18A14 14 0 0 1 20 8', polygon: 'M12 3l8 6-3 10H7L4 9z',
  text: 'M5 6h14M12 6v13', dim: 'M4 5v14M20 5v14M4 12h16M7 9l-3 3 3 3M17 9l3 3-3 3', dimal: 'M5 15L17 5M3 13l4 4M15 3l4 4M8 20l12-10',
  move: 'M12 3v18M3 12h18M12 3l-3 3M12 3l3 3M12 21l-3-3M12 21l3-3M3 12l3-3M3 12l3 3M21 12l-3-3M21 12l-3 3', copy: 'M8 8h12v12H8zM4 16V4h12', rotate: 'M19 12a7 7 0 1 1-2.5-5.4M17 3v4h-4', scale: 'M4 20h9v-9H4zM13 11l7-7M15 4h5v5',
  mirror: 'M12 3v18M9 7L3 17h6zM15 7l6 10h-6z', offset: 'M4 18c0-9 5-13 14-13M9 20c0-7 4-10 11-10', array: 'M4 4h5v5H4zM15 4h5v5h-5zM4 15h5v5H4zM15 15h5v5h-5z', trim: 'M4 12h6M14 12h6M12 3v18M10 10l4 4M14 10l-4 4',
  extend: 'M20 3v18M4 12h12M12 8l4 4-4 4', fillet: 'M4 20V12a8 8 0 0 1 8-8h8', explode: 'M12 12L5 5M12 12l7-7M12 12l-7 7M12 12l7 7M12 4v3M12 17v3M4 12h3M17 12h3', erase: 'M5 6h14M9 6V4h6v2M7 6l1 14h8l1-14',
  dist: 'M3 17L17 3l4 4L7 21zM7 13l2 2M10 10l2 2M13 7l2 2', fit: 'M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5M9 9h6v6H9z', undo: 'M8 5L3 10l5 5M3 10h11a6 6 0 0 1 0 12h-3', redo: 'M16 5l5 5-5 5M21 10H10a6 6 0 0 0 0 12h3',
};
Object.assign(IC, {
  pastec: 'M9 4h6v3H9zM7 5H5v15h14V5h-2M9 12h6M9 16h6', copyc: 'M9 9h10v11H9zM5 15V4h10', cutc: 'M6 5l12 12M18 5L6 17M6 18m-2.5 0a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0M18 18m-2.5 0a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0',
  newlayer: 'M12 4l9 5-9 5-9-5zM3 14l9 5 9-5M19 2v5M16.5 4.5h5', panel: 'M4 5h16v14H4zM15 5v14',
});
// 功能區：一組組（繪製 / 修改 / 註解 / 圖層 / 內容 / 公用程式 / 剪貼簿），大掣有字，細掣得圖示（移上去有名同指令）
const svgI = (k, z) => '<svg viewBox="0 0 24 24" width="' + z + '" height="' + z + '" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="' + IC[k] + '"/></svg>';
const ACTS = {
  fit: { icon: 'fit', label: () => T('c_fit'), run: () => zoomTo(extents()) },
  pastec: { icon: 'pastec', label: () => T('paste'), tip: 'Ctrl+V', run: () => run('paste') },
  copyc: { icon: 'copyc', label: () => T('copy'), tip: 'Ctrl+C', run: () => copySel(false) },
  cutc: { icon: 'cutc', label: () => T('cut'), tip: 'Ctrl+X', run: () => copySel(true) },
  undo: { icon: 'undo', label: () => T('undo'), tip: 'Ctrl+Z', run: () => { cancel(); undo(); } },
  redo: { icon: 'redo', label: () => T('redo'), tip: 'Ctrl+Y', run: () => { cancel(); redo(); } },
  newlayer: { icon: 'newlayer', label: () => T('c_newlayer'), run: () => newLayer() },
  panel: { icon: 'panel', label: () => T('c_sidepanel'), run: () => { st.side = !st.side; layout(); } },
  measure: { icon: 'dist', label: () => T('c_measure'), tip: 'DI', run: () => run('dist'), k: 'dist' },
};
function rbtn(k, big, short) {
  const a = ACTS[k], c = CMDS[k], b = el('button', 'tb' + (big ? ' big' : '')); b.type = 'button'; b.dataset.k = a ? a.k || k : k;
  const name = a ? a.label() : c.label(), tip = a ? a.tip : c.a[0];
  b.innerHTML = svgI(a ? a.icon : k, big ? 28 : 17);
  if (big) b.appendChild(el('span', '', short || name));
  b.title = name + (tip ? '  (' + tip + ')' : '');
  b.onclick = () => { if (a) a.run(); else run(k); $('cin').focus(); };
  return b;
}
function buildTools() {
  const bar = $('tools'); bar.textContent = '';
  const panel = (title, parts) => {
    const p = el('div', 'rp'), body = el('div', 'rpb');
    for (const x of parts) {
      if (x instanceof Node) body.appendChild(x);
      else if (Array.isArray(x)) { const col = el('div', 'rcol'); x.forEach(k => col.appendChild(k instanceof Node ? k : rbtn(k))); body.appendChild(col); }
      else body.appendChild(rbtn(x.k, true, x.s));
    }
    p.append(body, el('div', 'rpt', title)); bar.appendChild(p);
  };
  const B = (k, s) => ({ k, s });
  panel(T('c_m_draw'), [B('line'), B('pline'), B('circle'), B('arc'), ['rect', 'polygon']]);
  panel(T('c_m_modify'), [['move', 'copy', 'scale'], ['rotate', 'mirror', 'array'], ['trim', 'extend', 'fillet'], ['offset', 'explode', 'erase']]);
  panel(T('c_g_annot'), [B('text'), B('dim', T('c_m_dim')), ['dimal']]);
  // 圖層：下拉揀目前圖層（有揀中物件就係改佢哋嘅圖層）
  const ls = el('select'); ls.id = 'rLayer'; ls.title = T('c_layer');
  ls.onchange = () => { const es = selEnts(); if (es.length) replaceEnts(new Map(es.map(e => [e.id, [{ ...e, layer: ls.value }]]))); else { doc.cur = ls.value; sidePanel(); } $('cin').focus(); };
  const lrow = el('div', 'rrow'); lrow.append(rbtn('newlayer'), rbtn('panel'));
  panel(T('c_layers'), [[ls, lrow]]);
  // 內容：顏色（有揀中物件就改佢哋；冇就係之後新畫嘅物件用嘅顏色）
  const cb = el('button', 'rb sm rsel'); cb.id = 'rColor'; cb.type = 'button'; cb.title = T('c_color');
  cb.onclick = async () => { const es = selEnts(), r = await colorDialog(es.length ? es[0].color : st.color, true); if (!r) return; if (es.length) replaceEnts(new Map(es.map(e => [e.id, [{ ...e, color: r.v }]]))); else { st.color = r.v; ribbonState(); } };
  panel(T('c_props'), [[cb]]);
  panel(T('c_g_util'), [B('measure'), B('fit')]);
  panel(T('c_g_clip'), [B('pastec'), ['copyc', 'cutc'], ['undo', 'redo']]);
}
function ribbonState() {
  const ls = $('rLayer'); if (!ls) return;
  const es = selEnts();
  ls.textContent = ''; for (const l of doc.layers) { const o = el('option', '', l.name); o.value = l.name; ls.appendChild(o); }
  const same = es.length && es.every(e => e.layer === es[0].layer);
  if (es.length && !same) { const o = el('option', '', '—'); o.value = ''; ls.prepend(o); ls.value = ''; } else ls.value = es.length ? es[0].layer : doc.cur;
  const c = es.length ? (es.every(e => e.color === es[0].color) ? es[0].color : undefined) : st.color, cb = $('rColor');
  cb.textContent = '';
  const sw = el('span', 'lsw'); sw.style.background = c === undefined ? 'transparent' : c == null ? (v => typeof v === 'string' ? v : aciHex(v))((lmap.get(es.length ? es[0].layer : doc.cur) || doc.layers[0]).color) : typeof c === 'string' ? c : aciHex(c);
  cb.append(sw, el('span', '', c === undefined ? '—' : c == null ? T('c_bylayer') : String(c)));
}
function toolState() { for (const b of $('tools').querySelectorAll('.tb')) b.classList.toggle('on', !!running && b.dataset.k === running); }
function status() {
  $('coord').textContent = fmt(cursor[0], 3) + ', ' + fmt(cursor[1], 3);
  for (const [id, on] of [['tSnap', st.osnap], ['tOrtho', st.ortho], ['tGrid', st.grid], ['tGsnap', st.gsnap]]) $(id).classList.toggle('on', on);
}
function layout() { $('side').style.display = st.side ? '' : 'none'; resize(); sidePanel(); }

// ---------- 滑鼠 / 鍵盤 ----------
function evPos(e) { const r = cvO.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; }
function finishWindow(b) {
  const list = windowPick(win.a, b); win = null;
  if (shiftDown) list.forEach(e => sel.delete(e.id)); else list.forEach(e => sel.add(e.id));
  redrawO(); sidePanel();
}
function onDown(e) {
  const s = evPos(e); $('cin').focus({ preventScroll: true }); e.preventDefault();
  if (e.button === 1 || (e.button === 0 && e.altKey)) { panning = { s, ox: view.ox, oy: view.oy }; return; }
  if (e.button === 2) { if (req) typed(''); return; }
  if (e.button !== 0) return;
  shiftDown = e.shiftKey;
  const raw = s2w(s); resolveCursor(raw);
  if (req && req.kind === 'point') { answer(cursor); return; }
  if (req && req.kind === 'ent') { const h = hitAt(raw); if (h) answer({ e: h, pt: raw }); return; }
  if (req && req.kind === 'str') return;
  // 揀圖元（閒置，或者指令叫你揀）
  if (win) { finishWindow(raw); return; }
  if (!req && sel.size && sel.size <= 300) {
    for (const x of selEnts()) for (const g of G.grips(x)) { const q = w2s(g.p); if (Math.abs(q[0] - s[0]) <= 6 && Math.abs(q[1] - s[1]) <= 6) { gripEdit(x, g); return; } }
  }
  const h = hitAt(raw);
  if (h) { if (e.shiftKey) sel.delete(h.id); else sel.add(h.id); redrawO(); sidePanel(); return; }
  win = { a: raw, b: raw, s }; redrawO();
}
function onMove(e) {
  const s = evPos(e);
  if (panning) { view.ox = panning.ox + s[0] - panning.s[0]; view.oy = panning.oy + s[1] - panning.s[1]; redrawAll(); return; }
  shiftDown = e.shiftKey;
  const raw = s2w(s); resolveCursor(raw);
  if (win) win.b = raw;
  const h = (!req || req.kind === 'ent' || req.kind === 'ents') && !win ? hitAt(raw) : null;
  hover = h; status(); redrawO();
}
function onUp(e) {
  if (panning) { panning = null; return; }
  if (win && e.button === 0) { const s = evPos(e); if (Math.hypot(s[0] - win.s[0], s[1] - win.s[1]) > 6) finishWindow(s2w(s)); }
}
function onWheel(e) { e.preventDefault(); zoomBy(Math.pow(1.0015, -e.deltaY * (e.deltaMode ? 30 : 1)), evPos(e)); resolveCursor(s2w(evPos(e))); }
function onDbl(e) {
  if (e.button === 1) { zoomTo(extents()); return; }
  if (req) return;
  const h = hitAt(s2w(evPos(e))); if (h && (h.t === 'text' || h.t === 'dim')) { win = null; sel = new Set([h.id]); editText(h); }
}
function copySel(cut) {
  const list = selEnts(); if (!list.length) return;
  const b = extents(list); clip = { ents: list.map(G.clone), base: [b[0], b[1]] };
  if (cut) removeEnts(list.map(e => e.id)); else toast(T('c_copied', list.length), 1500);
}
function toggle(k) { st[k] = !st[k]; status(); redrawAll(); }
function onKey(e) {
  const tg = e.target, inField = tg && (tg.tagName === 'INPUT' || tg.tagName === 'TEXTAREA' || tg.tagName === 'SELECT') && tg.id !== 'cin';
  if (inField || document.querySelector('.rmask')) return;
  const k = e.key, ci = $('cin');
  if (k === 'F3') { e.preventDefault(); toggle('osnap'); return; }
  if (k === 'F7') { e.preventDefault(); toggle('grid'); return; }
  if (k === 'F8') { e.preventDefault(); toggle('ortho'); return; }
  if (k === 'F9') { e.preventDefault(); toggle('gsnap'); return; }
  if (k === 'Shift') { shiftDown = true; resolveCursor(cursorRaw); redrawO(); return; }
  if (e.ctrlKey || e.metaKey) {
    const c = k.toLowerCase();
    if (c === 'z') { e.preventDefault(); cancel(); e.shiftKey ? redo() : undo(); }
    else if (c === 'y') { e.preventDefault(); cancel(); redo(); }
    else if (c === 'a') { e.preventDefault(); doc.ents.filter(pickable).forEach(x => sel.add(x.id)); redrawO(); sidePanel(); }
    else if (c === 'c' && !ci.value) { e.preventDefault(); copySel(false); }
    else if (c === 'x' && !ci.value) { e.preventDefault(); copySel(true); }
    else if (c === 'v' && clip) { e.preventDefault(); run('paste'); }
    return;
  }
  if (k === 'Escape') { e.preventDefault(); ci.value = ''; if (req || win) cancel(); else { sel.clear(); sidePanel(); } redrawO(); return; }
  if (k === 'Enter' || (k === ' ' && !(req && req.kind === 'str' && req.o.text))) { e.preventDefault(); const v = ci.value; ci.value = ''; typed(v); return; }
  if (k === 'Delete' && !ci.value) { e.preventDefault(); if (!req && sel.size) removeEnts([...sel]); return; }
  if (tg !== ci && k.length === 1 && !e.altKey) { e.preventDefault(); ci.value += k; ci.focus(); }
}

// ---------- 檔案 ----------
function resetDoc(d) {
  cancel(); doc = d; nextId = 1; for (const e of doc.ents) e.id = nextId++;
  sel.clear(); undoS = []; redoS = []; hover = null; reindex(); sidePanel();
}
function newDoc() {
  resetDoc({ ents: [], layers: [{ name: '0', color: 7, on: true, lock: false }], cur: '0' });
  app.lossy = false; st.textH = 0; st.color = null; resize.done = false; resize(); view.k = 2; view.ox = 80; view.oy = H - 70; redrawAll();
  shell.opened(T('c_untitled') + '.dxf', null); log(T('c_hint')); return Promise.resolve();
}
async function openBytes(buf, name, token) {
  let r;
  try { r = parseDxf(new Uint8Array(buf)); }
  catch (e) { console.error(e); throw new Error(e && e.message === 'binary' ? T('c_binary') : T('c_baddxf')); }
  resetDoc({ ents: r.ents, layers: r.layers, cur: r.layers.some(l => l.name === '0') ? '0' : r.layers[0].name });
  app.lossy = r.lossy; st.textH = 0;
  resize(); zoomTo(extents()); shell.opened(name, token);
  log(T('c_opened', name, doc.ents.length));
  const sk = Object.entries(r.skipped).map(([k, n]) => k + ' × ' + n);
  if (r.lossy) dialog(T('c_lossy_t'), T('c_lossy') + (sk.length ? '\n\n' + T('c_lossy_skip') + sk.join('、') : ''));
}
const enc = s => new TextEncoder().encode(s);
function getBytes() { return Promise.resolve(enc(writeDxf(doc))); }
const baseName = () => (shell.name || 'drawing').replace(/\.dxf$/i, '');
function svgOf(o) {   // o：w、h（輸出單位）、f（世界 → 輸出座標）、k（比例）、lw、mono
  const N = v => fmt(v, 3), P = p => { const q = o.f(p); return N(q[0]) + ' ' + N(q[1]); }, out = [];
  const x = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  const text = (t, col) => { const q = o.f(t.p), px = t.h * o.k * G.EM; G.textLines(t).forEach((l, i) => { if (l) out.push(`<text transform="translate(${N(q[0])} ${N(q[1])}) rotate(${N(-(t.rot || 0) * 180 / Math.PI)})" y="${N(i * t.h * G.LINE_GAP * o.k)}" font-size="${N(px)}" fill="${col}"${t.align === 'c' ? ' text-anchor="middle"' : ''}>${x(l)}</text>`); }); };
  for (const e of doc.ents) {
    if (!visible(e)) continue;
    const col = o.mono ? '#000000' : colorOf(e, true);
    if (e.t === 'text') { text(e, col); continue; }
    if (e.t === 'circle') { const c = o.f(e.c); out.push(`<circle cx="${N(c[0])}" cy="${N(c[1])}" r="${N(e.r * o.k)}" stroke="${col}"/>`); continue; }
    let d = '', last = null;
    for (const p of G.prims(e)) {
      const s = G.pStart(p), en = G.pEnd(p);
      if (!last || dist(last, s) > 1e-9) d += 'M' + P(s);
      if (p.t === 'l') d += 'L' + P(en);
      else if (p.full) { const m = G.pAt(p, .5); d += `A${N(p.r * o.k)} ${N(p.r * o.k)} 0 0 0 ${P(m)}A${N(p.r * o.k)} ${N(p.r * o.k)} 0 0 0 ${P(en)}`; }
      else d += `A${N(p.r * o.k)} ${N(p.r * o.k)} 0 ${G.sw(p) > Math.PI ? 1 : 0} ${p.rev ? 1 : 0} ${P(en)}`;
      last = en;
    }
    if (e.t === 'pline' && e.closed) d += 'Z';
    if (d) out.push(`<path d="${d}" stroke="${col}"/>`);
    if (e.t === 'dim') { const g = G.dimGeo(e); for (const t of g.tris) out.push(`<path d="M${P(t[0])}L${P(t[1])}L${P(t[2])}Z" fill="${col}" stroke="none"/>`); text({ ...g.text, align: 'c' }, col); }
  }
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${N(o.w)}${o.unit || ''}" height="${N(o.h)}${o.unit || ''}" viewBox="0 0 ${N(o.w)} ${N(o.h)}" fill="none" stroke-width="${o.lw}" stroke-linecap="round" stroke-linejoin="round" font-family="Segoe UI, Microsoft JhengHei, sans-serif">${o.bg ? `<rect width="100%" height="100%" fill="${o.bg}" stroke="none"/>` : ''}${out.join('')}</svg>`;
}
function exportSvg() {
  const b = extents(); if (!b) { toast(T('c_empty')); return; }
  const m = Math.max(b[2] - b[0], b[3] - b[1]) * 0.03 || 1, w = b[2] - b[0] + 2 * m, h = b[3] - b[1] + 2 * m;
  shell.exportFile(enc(svgOf({ w, h, k: 1, f: p => [p[0] - b[0] + m, b[3] - p[1] + m], lw: Math.max(w, h) / 1500, bg: '#ffffff' })), baseName() + '.svg');
}
function exportPng() {
  const b = extents(); if (!b) { toast(T('c_empty')); return; }
  const m = Math.max(b[2] - b[0], b[3] - b[1]) * 0.03 || 1, w = b[2] - b[0] + 2 * m, h = b[3] - b[1] + 2 * m, k = 3000 / Math.max(w, h);
  const cv = document.createElement('canvas'); cv.width = Math.max(1, Math.round(w * k)); cv.height = Math.max(1, Math.round(h * k));
  paint(cv.getContext('2d'), { k, ox: (m - b[0]) * k, oy: (b[3] + m) * k }, cv.width, cv.height, { bg: '#ffffff', light: true, grid: false, lw: 2 });
  cv.toBlob(async bl => shell.exportFile(new Uint8Array(await bl.arrayBuffer()), baseName() + '.png'), 'image/png');
}
const PAPERS = { A4: [210, 297], A3: [297, 420], A2: [420, 594], A1: [594, 841], A0: [841, 1189], Letter: [215.9, 279.4] };
function print() {
  const b = extents(); if (!b) { toast(T('c_empty')); return; }
  const body = el('div'), sel2 = (label, opts, def) => { body.appendChild(el('label', '', label)); const s = el('select'); for (const [v, t] of opts) { const o = el('option', '', t); o.value = v; s.appendChild(o); } s.value = def; body.appendChild(s); return s; };
  const land = (b[2] - b[0]) >= (b[3] - b[1]);
  const pS = sel2(T('c_pr_paper'), Object.keys(PAPERS).map(k => [k, k]), st.paper || 'A4'), oS = sel2(T('c_pr_orient'), [['l', T('c_pr_land')], ['p', T('c_pr_port')]], land ? 'l' : 'p');
  const sS = sel2(T('c_pr_scale'), [['fit', T('c_pr_fit')], ...[[10, 1], [5, 1], [2, 1], [1, 1], [1, 2], [1, 5], [1, 10], [1, 20], [1, 25], [1, 50], [1, 100], [1, 200], [1, 500], [1, 1000]].map(([a, c]) => [String(a / c), a + ':' + c])], st.pscale || 'fit');
  const cS = sel2(T('c_pr_color'), [['m', T('c_pr_mono')], ['c', T('c_pr_colour')]], st.pmono === false ? 'c' : 'm');
  body.appendChild(el('div', 'dim', T('c_pr_note')));
  dialog(T('print'), body, [[T('c_cancel')], [T('print'), () => {
    st.paper = pS.value; st.pscale = sS.value; st.pmono = cS.value === 'm';
    let [pw, ph] = PAPERS[pS.value]; if (oS.value === 'l') [pw, ph] = [ph, pw];
    const mg = 10, k = sS.value === 'fit' ? Math.min((pw - 2 * mg) / Math.max(b[2] - b[0], 1e-9), (ph - 2 * mg) / Math.max(b[3] - b[1], 1e-9)) : parseFloat(sS.value);
    const cx = (b[0] + b[2]) / 2, cy = (b[1] + b[3]) / 2;
    const svg = svgOf({ w: pw, h: ph, unit: 'mm', k, f: p => [pw / 2 + (p[0] - cx) * k, ph / 2 - (p[1] - cy) * k], lw: 0.25, mono: st.pmono });
    let area = $('printArea'); if (!area) { area = el('div'); area.id = 'printArea'; document.body.appendChild(area); }
    let stl = $('printStyle'); if (!stl) { stl = el('style'); stl.id = 'printStyle'; document.head.appendChild(stl); }
    stl.textContent = `@page { size: ${pw}mm ${ph}mm; margin: 0; }`;
    area.innerHTML = svg; window.__printSvg = svg;
    setTimeout(() => window.print(), 80);
  }, true]]);
}

// ---------- AI ----------
function aiContext() {
  const b = extents(), lines = [];
  const P = p => '(' + fmt(p[0], 2) + ',' + fmt(p[1], 2) + ')';
  for (const e of doc.ents.slice(0, 400)) {
    let s = '#' + e.id + ' ';
    if (e.t === 'line') s += 'line ' + P(e.a) + '-' + P(e.b);
    else if (e.t === 'circle') s += 'circle c' + P(e.c) + ' r' + fmt(e.r, 2);
    else if (e.t === 'arc') s += 'arc c' + P(e.c) + ' r' + fmt(e.r, 2) + ' ' + deg(e.a0) + '°→' + deg(e.a1) + '°';
    else if (e.t === 'pline') s += (e.closed ? 'closed ' : '') + 'polyline ' + e.pts.slice(0, 12).map(P).join(' ') + (e.pts.length > 12 ? ' …(' + e.pts.length + ')' : '');
    else if (e.t === 'text') s += 'text ' + P(e.p) + ' h' + fmt(e.h, 2) + ' "' + e.s.replace(/\n/g, ' / ').slice(0, 80) + '"';
    else if (e.t === 'dim') s += 'dimension ' + P(e.a) + '-' + P(e.b) + ' = ' + fmt(G.dimGeo(e).val, 2);
    lines.push(s + ' [' + e.layer + ']');
  }
  return { text: lines.join('\n') + (doc.ents.length > 400 ? '\n… (' + doc.ents.length + ')' : ''), info: 'entities: ' + doc.ents.length + '; layers: ' + doc.layers.map(l => l.name).join(', ') + '; current layer: ' + doc.cur + (b ? '; extents: ' + P([b[0], b[1]]) + ' - ' + P([b[2], b[3]]) : '; empty drawing') + '; units: mm; Y axis points up' };
}
function aiApply(acts) {
  const add2 = [], del = new Set(), n = v => { const x = +v; return isFinite(x) ? x : null; }; let layersNew = 0;
  const pt2 = (x, y) => { const a = n(x), b = n(y); return a == null || b == null ? null : [a, b]; };
  const before = snapshot();
  for (const a of acts) {
    if (!a || typeof a !== 'object') continue;
    const base = {}; if (a.layer && doc.layers.some(l => l.name === a.layer)) base.layer = a.layer;
    if (a.op === 'layer' && a.name && !doc.layers.some(l => l.name === a.name)) { doc.layers.push({ name: String(a.name).slice(0, 60), color: Math.max(1, Math.min(255, parseInt(a.color, 10) || 7)), on: true, lock: false }); layersNew++; }
    else if (a.op === 'line') { const p = pt2(a.x1, a.y1), q = pt2(a.x2, a.y2); if (p && q) add2.push({ ...base, t: 'line', a: p, b: q }); }
    else if (a.op === 'circle') { const c = pt2(a.cx, a.cy), r = n(a.r); if (c && r > 0) add2.push({ ...base, t: 'circle', c, r }); }
    else if (a.op === 'arc') { const c = pt2(a.cx, a.cy), r = n(a.r), s = n(a.start), e = n(a.end); if (c && r > 0 && s != null && e != null) add2.push({ ...base, t: 'arc', c, r, a0: G.normA(s * Math.PI / 180), a1: G.normA(e * Math.PI / 180) }); }
    else if (a.op === 'rect') { const p = pt2(a.x, a.y), w = n(a.w), h = n(a.h); if (p && w > 0 && h > 0) add2.push({ ...base, t: 'pline', pts: [[p[0], p[1], 0], [p[0] + w, p[1], 0], [p[0] + w, p[1] + h, 0], [p[0], p[1] + h, 0]], closed: true }); }
    else if (a.op === 'polyline' && Array.isArray(a.points)) { const pts = a.points.map(p => Array.isArray(p) ? pt2(p[0], p[1]) : null).filter(Boolean).map(p => [p[0], p[1], 0]); if (pts.length >= 2) add2.push({ ...base, t: 'pline', pts, closed: !!a.closed }); }
    else if (a.op === 'text') { const p = pt2(a.x, a.y); if (p && a.text) add2.push({ ...base, t: 'text', p, h: n(a.height) > 0 ? n(a.height) : autoH(), s: String(a.text), rot: G.normA((n(a.rotation) || 0) * Math.PI / 180) }); }
    else if (a.op === 'dim') { const p = pt2(a.x1, a.y1), q = pt2(a.x2, a.y2), o = n(a.offset) ?? autoH() * 4; if (p && q && dist(p, q) > 0) add2.push({ ...base, t: 'dim', a: p, b: q, p: add(lerp(p, q, .5), mul(perp(norm(sub(q, p))), o)), mode: 'al', h: autoH() }); }
    else if (a.op === 'erase' && Array.isArray(a.ids)) a.ids.forEach(i => { if (byId.has(+i)) del.add(+i); });
    else if (a.op === 'move' && Array.isArray(a.ids)) { const d = pt2(a.dx, a.dy); if (d) { const ids = new Set(a.ids.map(Number)); doc.ents = doc.ents.map(e => ids.has(e.id) ? G.applyT(e, G.tMove(d)) : e); layersNew++; } }
  }
  const cnt = add2.length + del.size + layersNew; if (!cnt) return 0;
  undoS.push(before); redoS = [];
  if (del.size) doc.ents = doc.ents.filter(e => !del.has(e.id));
  doc.ents.push(...add2.map(mk)); changed();
  if (add2.length && !del.size) { const b = extents(); const v = [s2w([0, H]), s2w([W, 0])]; if (b && (b[0] < v[0][0] || b[1] < v[0][1] || b[2] > v[1][0] || b[3] > v[1][1])) zoomTo(b); }
  return cnt;
}

// ---------- 選單 / 啟動 ----------
function help() {
  const rows = Object.values(CMDS).filter(c => c.a.length).map(c => c.a[0].padEnd(5) + ' ' + c.label());
  const half = Math.ceil(rows.length / 2), body = el('div', 'helpg');
  const col = list => { const p = el('pre'); p.textContent = list.join('\n'); return p; };
  body.append(col(rows.slice(0, half)), col(rows.slice(half)));
  const b2 = el('div'); b2.append(body, el('div', 'rbody', T('c_help')));
  dialog(T('c_cmdlist'), b2);
}
function stats() {
  const n = {}; for (const e of doc.ents) n[e.t] = (n[e.t] || 0) + 1;
  const b = extents();
  dialog(T('c_stats'), T('c_stats_body', doc.ents.length, doc.layers.length, b ? fmt(b[2] - b[0], 3) + ' × ' + fmt(b[3] - b[1], 3) : '—') + '\n' + Object.entries(n).map(([k, v]) => T('c_t_' + k) + '：' + v).join('\n'));
}
const mi = (k, key) => ({ label: CMDS[k].label(), key: key || CMDS[k].a[0], run: () => { run(k); $('cin').focus(); } });
const MENUS = [
  { label: T('m_file'), items: () => [
    { label: T('m_new'), icon: '＋', run: () => { if (shell.okToLeave()) newDoc(); } }, { label: T('open'), key: 'Ctrl+O', run: () => shell.pick() }, '-',
    { label: T('save'), key: 'Ctrl+S', run: () => shell.save(false) }, { label: T('saveas'), key: 'Ctrl+Shift+S', run: () => shell.save(true) }, '-',
    { label: T('c_exp_svg'), run: exportSvg }, { label: T('c_exp_png'), run: exportPng }, '-', { label: T('print'), key: 'Ctrl+P', run: print }] },
  { label: T('m_edit'), items: () => [
    { label: T('undo'), icon: '↶', key: 'Ctrl+Z', disabled: !undoS.length, run: () => { cancel(); undo(); } }, { label: T('redo'), icon: '↷', key: 'Ctrl+Y', disabled: !redoS.length, run: () => { cancel(); redo(); } }, '-',
    { label: T('cut'), key: 'Ctrl+X', disabled: !sel.size, run: () => copySel(true) }, { label: T('copy'), key: 'Ctrl+C', disabled: !sel.size, run: () => copySel(false) }, { label: T('paste'), key: 'Ctrl+V', disabled: !clip, run: () => run('paste') }, '-',
    { label: T('selectall'), key: 'Ctrl+A', run: () => { doc.ents.filter(pickable).forEach(x => sel.add(x.id)); redrawO(); sidePanel(); } }, mi('erase', 'Delete')] },
  { label: T('m_view'), items: () => [
    { label: T('c_fit'), run: () => zoomTo(extents()) }, mi('zoom'), { label: T('c_zoomin'), run: () => zoomBy(1.5) }, { label: T('c_zoomout'), run: () => zoomBy(1 / 1.5) }, '-',
    { label: T('c_grid'), key: 'F7', check: st.grid, run: () => toggle('grid') }, { label: T('c_whitebg'), check: st.light, run: () => toggle('light') }, { label: T('c_sidepanel'), check: st.side, run: () => { st.side = !st.side; layout(); } }] },
  { label: T('c_m_draw'), items: () => ['line', 'pline', 'rect', 'circle', 'arc', 'polygon', 'text'].map(k => mi(k)) },
  { label: T('c_m_dim'), items: () => [mi('dim'), mi('dimal')] },
  { label: T('c_m_modify'), items: () => [...['move', 'copy', 'rotate', 'scale', 'mirror', 'offset', 'array'].map(k => mi(k)), '-', ...['trim', 'extend', 'fillet', 'explode', 'erase'].map(k => mi(k))] },
  { label: T('m_tools'), items: () => [mi('dist'), { label: T('c_stats'), icon: '#', run: stats }, '-',
    { label: T('c_snap'), key: 'F3', check: st.osnap, run: () => toggle('osnap') }, { label: T('c_ortho'), key: 'F8', check: st.ortho, run: () => toggle('ortho') }, { label: T('c_gsnap'), key: 'F9', check: st.gsnap, run: () => toggle('gsnap') }, '-', { label: T('c_cmdlist'), run: help }] },
];
setGuessEncoding(CFG.lang);
const app = { ext: 'dxf', getBytes, print, openBytes, newDoc, aiContext, aiApply, onResize: () => resize(), lossy: false };

$('stage').innerHTML = '<div id="tools"></div><div id="work"><div id="cvWrap"><canvas id="cvB"></canvas><canvas id="cvO"></canvas></div><div id="side"></div></div>' +
  '<div id="cmd"><div id="hist"></div><div id="cline"><span id="cprompt"></span><input id="cin" spellcheck="false" autocomplete="off"></div></div>' +
  '<div id="status"><span id="coord"></span><span class="grow"></span><button id="tSnap" class="stb"></button><button id="tOrtho" class="stb"></button><button id="tGrid" class="stb"></button><button id="tGsnap" class="stb"></button></div>';
cvB = $('cvB'); cvO = $('cvO'); cxB = cvB.getContext('2d'); cxO = cvO.getContext('2d');
initShell(app);
// 獨立工具（唔係由文書工具入嚟）：冇「返回」，開啟只揀 DXF
if (/solo/.test(location.hash)) try { sessionStorage.setItem('cadSolo', '1'); } catch (e) { }
let solo = false; try { solo = sessionStorage.getItem('cadSolo') === '1'; } catch (e) { }
if (solo) { $('bBack').style.display = 'none'; $('bWin').style.display = 'none'; shell.pick = () => post({ t: 'pick', kind: 'dxf' }); $('bOpen').onclick = () => shell.pick(); }
buildMenubar($('menubar'), MENUS);
buildTools();
$('tSnap').textContent = T('c_snap') + ' F3'; $('tOrtho').textContent = T('c_ortho') + ' F8'; $('tGrid').textContent = T('c_grid') + ' F7'; $('tGsnap').textContent = T('c_gsnap') + ' F9';
$('tSnap').onclick = () => toggle('osnap'); $('tOrtho').onclick = () => toggle('ortho'); $('tGrid').onclick = () => toggle('grid'); $('tGsnap').onclick = () => toggle('gsnap');
cvO.addEventListener('mousedown', onDown); cvO.addEventListener('mousemove', onMove); addEventListener('mouseup', onUp);
cvO.addEventListener('wheel', onWheel, { passive: false }); cvO.addEventListener('dblclick', onDbl); cvO.addEventListener('auxclick', e => { if (e.button === 1 && e.detail === 2) zoomTo(extents()); });
cvO.addEventListener('contextmenu', e => e.preventDefault()); cvO.addEventListener('mouseleave', () => { hover = null; redrawO(); });
addEventListener('keydown', onKey); addEventListener('keyup', e => { if (e.key === 'Shift') { shiftDown = false; resolveCursor(cursorRaw); redrawO(); } });
addEventListener('resize', () => resize());
addEventListener('afterprint', () => { const a = $('printArea'); if (a) a.innerHTML = ''; });
setPrompt(''); status(); resize(); sidePanel();
window.__test = { run, typed, get doc() { return doc; }, get sel() { return [...sel]; }, setSel: ids => { sel = new Set(ids); redrawO(); sidePanel(); }, view, w2s, s2w, zoomTo, extents, openBytes, getBytes, newDoc, aiApply, aiContext, G, st, get req() { return req && req.kind; }, svg: () => svgOf({ w: 297, h: 210, unit: 'mm', k: 1, f: p => p, lw: .25 }), exportSvg, exportPng, undo, redo, get lossy() { return app.lossy; } };
if (/new/.test(location.hash)) newDoc().then(() => busy('')); else busy(T('loading'));
post({ t: 'ready', page: 'cad' });
