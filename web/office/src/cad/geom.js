// 繪圖工具嘅幾何運算：向量、圖元（線 / 圓 / 弧 / 聚合線 / 文字 / 標註）、交點、偏移、修剪、變換。
// 圖元全部係可以直接 JSON 化嘅普通物件：
//   line  { a, b }                circle { c, r }              arc { c, r, a0, a1 }（弧度，逆時針由 a0 去 a1）
//   pline { pts:[[x,y,凸度]], closed }   text { p, h, s, rot }      dim { a, b, p, mode:'al'|'h'|'v', h, s? }
// 共通：id、layer、color（null = 跟圖層；數字 = 索引色；'#rrggbb'）

export const TAU = Math.PI * 2;
export const add = (a, b) => [a[0] + b[0], a[1] + b[1]], sub = (a, b) => [a[0] - b[0], a[1] - b[1]], mul = (a, s) => [a[0] * s, a[1] * s];
export const dot = (a, b) => a[0] * b[0] + a[1] * b[1], cross = (a, b) => a[0] * b[1] - a[1] * b[0];
export const len = a => Math.hypot(a[0], a[1]), dist = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1]);
export const norm = a => { const l = len(a); return l < 1e-300 ? [1, 0] : [a[0] / l, a[1] / l]; };
export const perp = a => [-a[1], a[0]], ang = a => Math.atan2(a[1], a[0]);
export const polar = (c, r, t) => [c[0] + r * Math.cos(t), c[1] + r * Math.sin(t)];
export const lerp = (a, b, t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t];
export const normA = a => { a %= TAU; return a < 0 ? a + TAU : a; };
/** 由 a0 逆時針去 a1 嘅角度（0 < 值 ≤ 2π）。 */
export const sweep = (a0, a1) => { const s = normA(a1 - a0); return s < 1e-12 ? TAU : s; };
export const fmt = (v, d = 2) => { const s = (+v).toFixed(d); return s.includes('.') ? s.replace(/\.?0+$/, '') || '0' : s; };
const P2 = p => [p[0], p[1]];

// ---------- 文字闊度（用畫布量；冇畫布就估） ----------
export const FONT = '"Segoe UI","Microsoft JhengHei UI","Microsoft YaHei UI","Yu Gothic UI","Malgun Gothic",sans-serif';
export const EM = 1 / 0.72;          // 字高（大楷高度）→ 字型大小
let mctx = null; const wcache = new Map();
export function textW(s, h) {
  let w = wcache.get(s);
  if (w == null) {
    try { if (!mctx) { mctx = document.createElement('canvas').getContext('2d'); mctx.font = '100px ' + FONT; } w = mctx.measureText(s).width / 100; }
    catch (e) { w = [...s].reduce((n, ch) => n + (ch.charCodeAt(0) > 0x2e7f ? 1 : 0.55), 0); }
    if (wcache.size > 20000) wcache.clear();
    wcache.set(s, w);
  }
  return w * h * EM;
}
export const textLines = e => String(e.s == null ? '' : e.s).split('\n');
export const LINE_GAP = 1.6;
/** 文字嘅四個角（世界座標），次序：左下、右下、右上、左上。 */
export function textBox(e) {
  const ls = textLines(e), w = Math.max(e.h * 0.5, ...ls.map(l => textW(l, e.h)));
  const y0 = -(ls.length - 1) * e.h * LINE_GAP - e.h * 0.3, y1 = e.h * 1.1, c = Math.cos(e.rot || 0), s = Math.sin(e.rot || 0);
  const x0 = e.align === 'c' ? -w / 2 : 0, x1 = x0 + w;
  return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]].map(([x, y]) => [e.p[0] + x * c - y * s, e.p[1] + x * s + y * c]);
}

// ---------- 基本段（線段 l / 圓弧 a） ----------
/** 聚合線嘅凸度 → 圓弧。rev = 行走方向係順時針（即係由 a1 行去 a0）。 */
export function bulgeArc(p1, p2, b) {
  const d = dist(p1, p2); if (d < 1e-12) return null;
  const k = (1 - b * b) / (4 * b);
  const c = [(p1[0] + p2[0]) / 2 - (p2[1] - p1[1]) * k, (p1[1] + p2[1]) / 2 + (p2[0] - p1[0]) * k];
  const r = d * (1 + b * b) / (4 * Math.abs(b)), s = ang(sub(p1, c)), e = ang(sub(p2, c));
  return b > 0 ? { t: 'a', c, r, a0: s, a1: e } : { t: 'a', c, r, a0: e, a1: s, rev: true };
}
export function prims(e) {
  switch (e.t) {
    case 'line': return [{ t: 'l', a: e.a, b: e.b }];
    case 'circle': return [{ t: 'a', c: e.c, r: e.r, a0: 0, a1: TAU, full: true }];
    case 'arc': return [{ t: 'a', c: e.c, r: e.r, a0: e.a0, a1: e.a1 }];
    case 'pline': {
      const out = [], n = e.pts.length, m = e.closed ? n : n - 1;
      for (let i = 0; i < m; i++) {
        const p = e.pts[i], q = e.pts[(i + 1) % n], b = p[2] || 0;
        let pr = Math.abs(b) > 1e-9 ? bulgeArc(p, q, b) : null;
        if (!pr) pr = { t: 'l', a: P2(p), b: P2(q) };
        pr.i = i; out.push(pr);
      }
      return out;
    }
    case 'dim': return dimGeo(e).segs.map(([a, b]) => ({ t: 'l', a, b }));
  }
  return [];
}
export const sw = p => p.full ? TAU : sweep(p.a0, p.a1);
export const pAt = (p, t) => p.t === 'l' ? lerp(p.a, p.b, t) : polar(p.c, p.r, p.a0 + sw(p) * (p.rev ? 1 - t : t));
export const pStart = p => pAt(p, 0), pEnd = p => pAt(p, 1);
export const pLen = p => p.t === 'l' ? dist(p.a, p.b) : p.r * sw(p);
/** 點喺段上面嘅參數（0 = 起點，1 = 終點；線段可以出界，圓弧出界就當最近嗰頭）。 */
export function pParam(p, pt) {
  if (p.t === 'l') { const d = sub(p.b, p.a), l2 = dot(d, d); return l2 < 1e-300 ? 0 : dot(sub(pt, p.a), d) / l2; }
  const s = sw(p); let f = normA(ang(sub(pt, p.c)) - p.a0);
  if (f > s) f = (f - s) < (TAU - f) ? s : 0;
  f /= s; return p.rev ? 1 - f : f;
}
export function pNear(p, pt) {
  if (p.t === 'l') return lerp(p.a, p.b, Math.max(0, Math.min(1, pParam(p, pt))));
  if (dist(pt, p.c) < 1e-12) return pStart(p);
  return pAt(p, pParam(p, pt));
}
export function onPrim(p, pt) {
  if (p.t === 'l') { const l = dist(p.a, p.b) || 1, t = pParam(p, pt), tol = 1e-7 / l; return t >= -tol && t <= 1 + tol; }
  if (p.full) return true;
  const f = normA(ang(sub(pt, p.c)) - p.a0), tol = 1e-7 / Math.max(p.r, 1e-9);
  return f <= sw(p) + tol || f >= TAU - tol;
}
/** 兩段嘅交點。ua / ub = 當嗰段無限延伸（直線 / 成個圓）。 */
export function pInter(p, q, ua, ub) {
  let out = [];
  if (p.t === 'l' && q.t === 'l') {
    const r = sub(p.b, p.a), s = sub(q.b, q.a), d = cross(r, s);
    if (Math.abs(d) > 1e-12 * (len(r) * len(s) || 1)) out.push(add(p.a, mul(r, cross(sub(q.a, p.a), s) / d)));
  } else if (p.t === 'l' || q.t === 'l') {
    const l = p.t === 'l' ? p : q, c = p.t === 'l' ? q : p, d = sub(l.b, l.a), L = len(d);
    if (L > 1e-300) {
      const u = mul(d, 1 / L), w = sub(c.c, l.a), t0 = dot(w, u), h2 = c.r * c.r - (dot(w, w) - t0 * t0);
      if (h2 >= -1e-9 * c.r * c.r) { const h = Math.sqrt(Math.max(0, h2)); out.push(add(l.a, mul(u, t0 - h))); if (h > 1e-9) out.push(add(l.a, mul(u, t0 + h))); }
    }
  } else {
    const d = dist(p.c, q.c);
    if (d > 1e-12 && d <= p.r + q.r + 1e-9 && d >= Math.abs(p.r - q.r) - 1e-9) {
      const a = (p.r * p.r - q.r * q.r + d * d) / (2 * d), h = Math.sqrt(Math.max(0, p.r * p.r - a * a)), u = mul(sub(q.c, p.c), 1 / d), m = add(p.c, mul(u, a));
      out.push(add(m, mul(perp(u), h))); if (h > 1e-9) out.push(add(m, mul(perp(u), -h)));
    }
  }
  return out.filter(x => (ua || onPrim(p, x)) && (ub || onPrim(q, x)));
}

// ---------- 範圍 / 距離 ----------
export function bbox(e) {
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  const put = p => { if (p[0] < x0) x0 = p[0]; if (p[0] > x1) x1 = p[0]; if (p[1] < y0) y0 = p[1]; if (p[1] > y1) y1 = p[1]; };
  if (e.t === 'text') textBox(e).forEach(put);
  else {
    if (e.t === 'dim') { const g = dimGeo(e); textBox({ ...g.text, align: 'c' }).forEach(put); put(e.a); put(e.b); }
    for (const p of prims(e)) {
      if (p.t === 'l') { put(p.a); put(p.b); continue; }
      if (p.full) { put([p.c[0] - p.r, p.c[1] - p.r]); put([p.c[0] + p.r, p.c[1] + p.r]); continue; }
      put(polar(p.c, p.r, p.a0)); put(polar(p.c, p.r, p.a1));
      for (let k = 0; k < 4; k++) { const t = k * Math.PI / 2; if (normA(t - p.a0) <= sw(p)) put(polar(p.c, p.r, t)); }
    }
  }
  return x0 > x1 ? null : [x0, y0, x1, y1];
}
function boxDist(q, pt) {   // 凸四邊形：入面 = 0
  let inside = true, best = Infinity;
  for (let i = 0; i < 4; i++) { const a = q[i], b = q[(i + 1) % 4]; if (cross(sub(b, a), sub(pt, a)) < 0) inside = false; best = Math.min(best, dist(pt, pNear({ t: 'l', a, b }, pt))); }
  return inside ? 0 : best;
}
export function entDist(e, pt) {
  if (e.t === 'text') return boxDist(textBox(e), pt);
  let best = Infinity;
  for (const p of prims(e)) best = Math.min(best, dist(pt, pNear(p, pt)));
  if (e.t === 'dim') best = Math.min(best, boxDist(textBox({ ...dimGeo(e).text, align: 'c' }), pt));
  return best;
}
/** 圖元嘅折線近似（揀框、匯出、非等比縮放用）。 */
export function tess(e, step = TAU / 72) {
  const out = [];
  for (const p of prims(e)) {
    if (p.t === 'l') { if (!out.length) out.push(p.a); out.push(p.b); continue; }
    const n = Math.max(2, Math.ceil(sw(p) / step));
    for (let i = out.length ? 1 : 0; i <= n; i++) out.push(pAt(p, i / n));
  }
  return out;
}
export const entLen = e => prims(e).reduce((s, p) => s + pLen(p), 0);
/** 封閉圖形嘅面積（圓、封閉聚合線；其他 = null）。 */
export function entArea(e) {
  if (e.t === 'circle') return Math.PI * e.r * e.r;
  if (e.t !== 'pline' || !e.closed) return null;
  let s = 0; const n = e.pts.length;
  for (let i = 0; i < n; i++) {
    const p = e.pts[i], q = e.pts[(i + 1) % n]; s += cross(p, q) / 2;
    const b = p[2] || 0;
    if (Math.abs(b) > 1e-9) { const a = bulgeArc(p, q, b); if (a) { const th = sw(a); s += (b > 0 ? 1 : -1) * a.r * a.r * (th - Math.sin(th)) / 2; } }
  }
  return Math.abs(s);
}

// ---------- 標註 ----------
export function dimGeo(e) {
  const h = e.h, a = e.a, b = e.b; let A, B;
  if (e.mode === 'h') { A = [a[0], e.p[1]]; B = [b[0], e.p[1]]; }
  else if (e.mode === 'v') { A = [e.p[0], a[1]]; B = [e.p[0], b[1]]; }
  else { const n = perp(norm(sub(b, a))), o = dot(sub(e.p, a), n); A = add(a, mul(n, o)); B = add(b, mul(n, o)); }
  const val = dist(A, B), segs = [], tris = [];
  const ext = (p, P) => { const L = dist(p, P); if (L < 1e-9) return; const d = norm(sub(P, p)); segs.push([add(p, mul(d, Math.min(L, h * 0.25))), add(P, mul(d, h * 0.5))]); };
  ext(a, A); ext(b, B); segs.push([A, B]);
  if (val > 1e-9) {
    const d = norm(sub(B, A)), n = perp(d), out = val < h * 3 ? -1 : 1;     // 太短：箭嘴掉去外面
    const ar = (P, s) => tris.push([P, add(add(P, mul(d, s * h)), mul(n, h / 6)), add(add(P, mul(d, s * h)), mul(n, -h / 6))]);
    ar(A, out); ar(B, -out);
    if (out < 0) { segs.push([A, add(A, mul(d, -h * 2))]); segs.push([B, add(B, mul(d, h * 2))]); }
  }
  let rot = val > 1e-9 ? ang(sub(B, A)) : 0;
  if (rot > Math.PI / 2 + 1e-9 || rot <= -Math.PI / 2 + 1e-9) rot += Math.PI;
  const up = [-Math.sin(rot), Math.cos(rot)];
  return { segs, tris, A, B, val, text: { p: add(lerp(A, B, .5), mul(up, h * 0.4)), rot: normA(rot), s: e.s || fmt(val), h } };
}

// ---------- 變換 ----------
export const tMove = d => ({ f: p => add(p, d), s: 1, rot: 0, flip: false });
export const tRot = (c, a) => { const cs = Math.cos(a), sn = Math.sin(a); return { f: p => [c[0] + (p[0] - c[0]) * cs - (p[1] - c[1]) * sn, c[1] + (p[0] - c[0]) * sn + (p[1] - c[1]) * cs], s: 1, rot: a, flip: false }; };
export const tScale = (c, s) => ({ f: p => [c[0] + (p[0] - c[0]) * s, c[1] + (p[1] - c[1]) * s], s: Math.abs(s), rot: s < 0 ? Math.PI : 0, flip: false });
export const tMirror = (p, q) => { const u = norm(sub(q, p)); return { f: x => { const w = sub(x, p), t = dot(w, u); return [p[0] + 2 * t * u[0] - w[0], p[1] + 2 * t * u[1] - w[1]]; }, s: 1, rot: 0, flip: true }; };
/** 矩陣 [a,b,c,d,e,f]（x' = a·x + c·y + e，y' = b·x + d·y + f）→ 變換；唔係等比就傳返 null。 */
export function tMatrix(m) {
  const s1 = Math.hypot(m[0], m[1]), s2 = Math.hypot(m[2], m[3]), det = m[0] * m[3] - m[1] * m[2];
  if (Math.abs(s1 - s2) > 1e-6 * Math.max(s1, s2, 1e-9) || Math.abs(m[0] * m[2] + m[1] * m[3]) > 1e-6 * s1 * s2) return null;
  return { f: p => [m[0] * p[0] + m[2] * p[1] + m[4], m[1] * p[0] + m[3] * p[1] + m[5]], s: s1, rot: Math.atan2(m[1], m[0]), flip: det < 0 };
}
export function applyT(e, T) {
  const o = { ...e }, f = T.f;
  switch (e.t) {
    case 'line': o.a = f(e.a); o.b = f(e.b); break;
    case 'circle': o.c = f(e.c); o.r = e.r * T.s; break;
    case 'arc': {
      o.c = f(e.c); o.r = e.r * T.s;
      const s = ang(sub(f(polar(e.c, e.r, e.a0)), o.c)), en = ang(sub(f(polar(e.c, e.r, e.a1)), o.c));
      const full = sweep(e.a0, e.a1) >= TAU - 1e-12;
      if (T.flip) { o.a0 = en; o.a1 = full ? en + TAU : s; } else { o.a0 = s; o.a1 = full ? s + TAU : en; }
      break;
    }
    case 'pline': o.pts = e.pts.map(p => { const q = f(p); return [q[0], q[1], (p[2] || 0) * (T.flip ? -1 : 1)]; }); break;
    case 'text': {
      const r = e.rot || 0, d = [Math.cos(r), Math.sin(r)], up = perp(d), w = textW(textLines(e)[0], e.h);
      o.h = e.h * T.s;
      if (!T.flip) { o.p = f(e.p); o.rot = normA(r + T.rot); }
      else {
        // 鏡射：文字唔會反轉，仍然可以讀（揀向上嗰個方向）
        const p0 = f(e.p), d2 = norm(sub(f(add(e.p, d)), p0)), a1 = ang(d2), keep = Math.cos(a1) > 1e-9 || (Math.abs(Math.cos(a1)) <= 1e-9 && Math.sin(a1) > 0);
        if (keep) { o.p = f(add(e.p, mul(up, e.h))); o.rot = normA(a1); }
        else { o.p = e.align === 'c' ? p0 : f(add(e.p, mul(d, w))); o.rot = normA(a1 + Math.PI); }
      }
      break;
    }
    case 'dim': {
      o.a = f(e.a); o.b = f(e.b); o.p = f(e.p); o.h = e.h * T.s;
      if (e.mode !== 'al') { const q = Math.abs(Math.sin(T.rot)); if (q > 1 - 1e-9) o.mode = e.mode === 'h' ? 'v' : 'h'; else if (q > 1e-9) o.mode = 'al'; }
      break;
    }
  }
  return o;
}
export const clone = e => JSON.parse(JSON.stringify(e));

// ---------- 路徑（線 / 弧 / 圓 / 聚合線當做一條有參數 u ∈ [0, N] 嘅路徑） ----------
export const pathClosed = e => e.t === 'circle' || (e.t === 'pline' && !!e.closed);
export function pathParam(e, pt) {
  const ps = prims(e); let best = Infinity, u = 0;
  ps.forEach((p, i) => { const q = pNear(p, pt), d = dist(q, pt); if (d < best) { best = d; u = i + Math.max(0, Math.min(1, pParam(p, q))); } });
  return u;
}
export function pathPoint(e, u) { const ps = prims(e), i = Math.min(ps.length - 1, Math.floor(u)); return pAt(ps[i], u - i); }
/** 路徑由 u0 去 u1 嗰一截（封閉路徑 u0 > u1 = 跨過起點）。 */
export function subPath(e, u0, u1) {
  const base = { layer: e.layer, color: e.color }, ps = prims(e), N = ps.length;
  if (e.t === 'line') return { ...base, t: 'line', a: pAt(ps[0], u0), b: pAt(ps[0], u1) };
  if (e.t === 'circle' || e.t === 'arc') { const p = ps[0], s = sw(p); return { ...base, t: 'arc', c: P2(e.c), r: e.r, a0: normA(p.a0 + s * u0), a1: normA(p.a0 + s * u1) }; }
  let i0 = Math.min(N - 1, Math.floor(u0)), t0 = u0 - i0, i1 = Math.min(N - 1, Math.floor(u1)), t1 = u1 - i1;
  if (t1 < 1e-12 && u1 > 0) { i1 = (i1 + N - 1) % N; t1 = 1; }
  if (t0 > 1 - 1e-12 && i0 < N - 1) { i0++; t0 = 0; }
  const wrap = u0 > u1, seq = [];
  if (!wrap) for (let i = i0; i <= i1; i++) seq.push(i); else { for (let i = i0; i < N; i++) seq.push(i); for (let i = 0; i <= i1; i++) seq.push(i); }
  const pts = []; let last = null;
  seq.forEach((i, k) => {
    const p = ps[i], ta = k === 0 ? t0 : 0, tb = k === seq.length - 1 ? t1 : 1;
    if (tb - ta < 1e-12) return;
    const s = pAt(p, ta);
    pts.push([s[0], s[1], p.t === 'a' ? Math.tan(sw(p) * (tb - ta) / 4) * (p.rev ? -1 : 1) : 0]);
    last = pAt(p, tb);
  });
  if (!last) return null;
  pts.push([last[0], last[1], 0]);
  return { ...base, t: 'pline', pts, closed: false };
}
/** e 同其他圖元嘅交點（路徑參數，排好序、去重）。 */
export function pathCuts(e, others) {
  const ps = prims(e), bb = bbox(e), us = [];
  for (const o of others) {
    if (o === e) continue;
    const ob = bbox(o); if (!ob || !bb || ob[0] > bb[2] || ob[2] < bb[0] || ob[1] > bb[3] || ob[3] < bb[1]) continue;
    const qs = prims(o);
    ps.forEach((p, i) => { for (const q of qs) for (const x of pInter(p, q)) us.push(i + Math.max(0, Math.min(1, pParam(p, x)))); });
  }
  us.sort((a, b) => a - b);
  return us.filter((u, i) => i === 0 || u - us[i - 1] > 1e-9);
}
/** 修剪：剪走 pick 嗰一截。傳返剩低嘅圖元（陣列）；剪唔到 = null。 */
export function trimAt(e, others, pick) {
  if (!['line', 'arc', 'circle', 'pline'].includes(e.t)) return null;
  const N = prims(e).length, us = pathCuts(e, others), up = pathParam(e, pick), closed = pathClosed(e) || (e.t === 'arc' && sweep(e.a0, e.a1) >= TAU - 1e-12);
  const ok = x => x && entLen(x) > 1e-9;
  if (closed) {
    const cuts = us.filter(u => u < N - 1e-9 || !us.some(v => v < 1e-9));
    if (cuts.length < 2) return null;
    const lo = cuts.filter(u => u < up).pop() ?? cuts[cuts.length - 1], hi = cuts.find(u => u > up) ?? cuts[0];
    if (Math.abs(lo - hi) < 1e-9) return null;
    const r = subPath(e, hi, lo); return ok(r) ? [r] : null;
  }
  const lo = us.filter(u => u < up - 1e-9).pop(), hi = us.find(u => u > up + 1e-9);
  if (lo == null && hi == null) return null;
  const out = [];
  if (lo != null && lo > 1e-9) out.push(subPath(e, 0, lo));
  if (hi != null && hi < N - 1e-9) out.push(subPath(e, hi, N));
  return out.filter(ok);
}
/** 延伸：將近 pick 嗰一頭延長到最近嘅圖元。只支援線同弧。 */
export function extendAt(e, others, pick) {
  if (e.t !== 'line' && e.t !== 'arc') return null;
  const p = prims(e)[0], atEnd = pParam(p, pick) > 0.5; let best = null, bd = Infinity;
  for (const o of others) {
    if (o === e) continue;
    for (const q of prims(o)) for (const x of pInter(p, q, true, false)) {
      let d;
      if (e.t === 'line') { const t = pParam(p, x); d = atEnd ? t - 1 : -t; d *= dist(p.a, p.b); }
      else { const s = sw(p); d = atEnd ? normA(ang(sub(x, p.c)) - p.a1) : normA(p.a0 - ang(sub(x, p.c))); if (d > TAU - s - 1e-9) continue; d *= p.r; }
      if (d > 1e-7 && d < bd) { bd = d; best = x; }
    }
  }
  if (!best) return null;
  const o = { ...e };
  if (e.t === 'line') { if (atEnd) o.b = best; else o.a = best; }
  else if (atEnd) o.a1 = ang(sub(best, e.c)); else o.a0 = ang(sub(best, e.c));
  return o;
}
/** 圓角：兩條線（pa / pb = 撳嘅位置），r = 0 即係直接接角。傳返 { a, b, arc? }；平行 = null。 */
export function fillet(la, pa, lb, pb, r) {
  const A = prims(la)[0], B = prims(lb)[0], x = pInter(A, B, true, true)[0];
  if (!x) return null;
  const side = (p, pk) => { const q = pNear(p, pk); if (dist(q, x) > 1e-9) return norm(sub(q, x)); return norm(sub(dist(p.a, x) > dist(p.b, x) ? p.a : p.b, x)); };
  const far = (p, u) => { const da = dot(sub(p.a, x), u), db = dot(sub(p.b, x), u); return da > db ? p.a : p.b; };
  const u1 = side(A, pa), u2 = side(B, pb), th = Math.acos(Math.max(-1, Math.min(1, dot(u1, u2))));
  if (th < 1e-9 || th > Math.PI - 1e-9) return null;
  const t = r > 0 ? r / Math.tan(th / 2) : 0, T1 = add(x, mul(u1, t)), T2 = add(x, mul(u2, t)), f1 = far(A, u1), f2 = far(B, u2);
  if (dot(sub(f1, x), u1) < t - 1e-9 || dot(sub(f2, x), u2) < t - 1e-9) return null;
  const out = { a: { ...la, a: f1, b: T1 }, b: { ...lb, a: f2, b: T2 } };
  if (r > 0) {
    const c = add(x, mul(norm(add(u1, u2)), r / Math.sin(th / 2))); let a0 = ang(sub(T1, c)), a1 = ang(sub(T2, c));
    if (sweep(a0, a1) > Math.PI) [a0, a1] = [a1, a0];
    out.arc = { t: 'arc', layer: la.layer, color: la.color, c, r, a0: normA(a0), a1: normA(a1) };
  }
  return out;
}

// ---------- 偏移 ----------
function offPrim(p, d) {   // d > 0 = 行走方向嘅左邊
  if (p.t === 'l') { const n = mul(norm(perp(sub(p.b, p.a))), d); return { ...p, a: add(p.a, n), b: add(p.b, n) }; }
  const r = p.rev ? p.r + d : p.r - d; return r > 1e-9 ? { ...p, r } : null;
}
/** pick 喺圖元行走方向嘅左邊？ */
export function leftOf(e, pick) {
  const ps = prims(e); let best = Infinity, res = true;
  for (const p of ps) {
    const q = pNear(p, pick), d = dist(q, pick);
    if (d < best) { best = d; res = p.t === 'l' ? cross(sub(p.b, p.a), sub(pick, p.a)) > 0 : (dist(pick, p.c) < p.r) !== !!p.rev; }
  }
  return res;
}
export function offset(e, d, left) {
  const s = left ? d : -d, base = { layer: e.layer, color: e.color };
  if (e.t === 'line') { const p = offPrim(prims(e)[0], s); return { ...base, t: 'line', a: p.a, b: p.b }; }
  if (e.t === 'circle' || e.t === 'arc') { const r = e.r - s; return r > 1e-9 ? { ...e, c: P2(e.c), r } : null; }
  if (e.t !== 'pline') return null;
  const op = prims(e).map(p => offPrim(p, s)).filter(Boolean), n = op.length;
  if (!n) return null;
  const joint = (p, q) => { const xs = pInter(p, q, true, true), en = pEnd(p); let b = null, bd = Infinity; for (const x of xs) { const dd = dist(x, en); if (dd < bd) { bd = dd; b = x; } } return b; };
  const bul = (p, S, E) => { if (p.t === 'l') return 0; const a = ang(sub(S, p.c)), b = ang(sub(E, p.c)); return Math.tan((p.rev ? sweep(b, a) : sweep(a, b)) / 4) * (p.rev ? -1 : 1); };
  const pts = []; let S = e.closed ? (joint(op[n - 1], op[0]) || pStart(op[0])) : pStart(op[0]);
  const first = S;
  for (let i = 0; i < n; i++) {
    const last = i === n - 1; let E, next;
    if (last && !e.closed) { E = pEnd(op[i]); next = null; }
    else { const j = last ? first : joint(op[i], op[i + 1]); if (j) { E = j; next = j; } else { E = pEnd(op[i]); next = pStart(op[(i + 1) % n]); } }
    pts.push([S[0], S[1], bul(op[i], S, E)]);
    if (next && dist(E, next) > 1e-9) pts.push([E[0], E[1], 0]);
    if (!next) pts.push([E[0], E[1], 0]);
    S = next;
  }
  return { ...base, t: 'pline', pts, closed: !!e.closed };
}

// ---------- 捕捉點 ----------
/** 圖元嘅特徵點：[種類, 點]。種類：end / mid / cen / quad。 */
export function snapPoints(e) {
  const out = [];
  if (e.t === 'text') { out.push(['end', e.p]); return out; }
  if (e.t === 'dim') { out.push(['end', e.a], ['end', e.b]); return out; }
  if (e.t === 'circle') { out.push(['cen', e.c]); for (let k = 0; k < 4; k++) out.push(['quad', polar(e.c, e.r, k * Math.PI / 2)]); return out; }
  for (const p of prims(e)) {
    out.push(['end', pStart(p)], ['end', pEnd(p)], ['mid', pAt(p, .5)]);
    if (p.t === 'a') out.push(['cen', p.c]);
  }
  return out;
}
/** 夾點（揀中之後可以拖嘅點）：[{ p, set(e, 新點) }]。 */
export function grips(e) {
  const g = [];
  const mv = () => (o, q, from) => applyT(o, tMove(sub(q, from)));
  switch (e.t) {
    case 'line': g.push({ p: e.a, set: (o, q) => ({ ...o, a: q }) }, { p: e.b, set: (o, q) => ({ ...o, b: q }) }, { p: lerp(e.a, e.b, .5), set: mv() }); break;
    case 'circle': g.push({ p: e.c, set: mv() }); for (let k = 0; k < 4; k++) g.push({ p: polar(e.c, e.r, k * Math.PI / 2), set: (o, q) => ({ ...o, r: Math.max(1e-9, dist(q, o.c)) }) }); break;
    case 'arc': g.push({ p: e.c, set: mv() }, { p: polar(e.c, e.r, e.a0), set: (o, q) => ({ ...o, a0: ang(sub(q, o.c)) }) }, { p: polar(e.c, e.r, e.a1), set: (o, q) => ({ ...o, a1: ang(sub(q, o.c)) }) },
      { p: polar(e.c, e.r, e.a0 + sweep(e.a0, e.a1) / 2), set: (o, q) => ({ ...o, r: Math.max(1e-9, dist(q, o.c)) }) }); break;
    case 'pline': e.pts.forEach((p, i) => g.push({ p: P2(p), set: (o, q) => ({ ...o, pts: o.pts.map((v, k) => k === i ? [q[0], q[1], v[2] || 0] : v) }) })); break;
    case 'text': g.push({ p: e.p, set: mv() }); break;
    case 'dim': g.push({ p: e.a, set: (o, q) => ({ ...o, a: q }) }, { p: e.b, set: (o, q) => ({ ...o, b: q }) }, { p: dimGeo(e).text.p, set: (o, q) => ({ ...o, p: q }) }); break;
  }
  return g;
}

/** 三點定圓。共線 = null。 */
export function circle3(a, b, c) {
  const d = 2 * (a[0] * (b[1] - c[1]) + b[0] * (c[1] - a[1]) + c[0] * (a[1] - b[1]));
  if (Math.abs(d) < 1e-12) return null;
  const A = dot(a, a), B = dot(b, b), C = dot(c, c);
  const cen = [(A * (b[1] - c[1]) + B * (c[1] - a[1]) + C * (a[1] - b[1])) / d, (A * (c[0] - b[0]) + B * (a[0] - c[0]) + C * (b[0] - a[0])) / d];
  return { c: cen, r: dist(cen, a) };
}
/** 三點定弧（起點、中間一點、終點）。 */
export function arc3(a, m, b) {
  const k = circle3(a, m, b); if (!k) return null;
  const s = ang(sub(a, k.c)), e = ang(sub(b, k.c)), mm = ang(sub(m, k.c));
  return normA(mm - s) <= sweep(s, e) ? { c: k.c, r: k.r, a0: normA(s), a1: normA(e) } : { c: k.c, r: k.r, a0: normA(e), a1: normA(s) };
}
