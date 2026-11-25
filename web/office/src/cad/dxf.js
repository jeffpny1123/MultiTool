// DXF 讀寫。
//   讀：LINE、CIRCLE、ARC、LWPOLYLINE、POLYLINE、TEXT、MTEXT、DIMENSION（線性 / 對齊）、INSERT（圖塊會拆開）、
//       ELLIPSE / SPLINE（轉做折線）、SOLID / TRACE / 3DFACE（外框）、LEADER；圖層同索引色。其他種類會數低，話俾用戶知。
//   寫：R12（AC1009）ASCII —— 最多軟件讀到嘅版本；非英文字用 \U+XXXX。
import { TAU, add, sub, mul, perp, ang, polar, normA, sweep, fmt, textW, textLines, LINE_GAP, dimGeo, tMatrix, applyT, tess, pathClosed, prims } from './geom.js';

// ---------- 索引色（ACI） ----------
const BASE = ['#000000', '#ff0000', '#ffff00', '#00ff00', '#00ffff', '#0000ff', '#ff00ff', '#ffffff', '#808080', '#c0c0c0'];
const hex = (r, g, b) => '#' + [r, g, b].map(v => Math.round(Math.max(0, Math.min(255, v))).toString(16).padStart(2, '0')).join('');
const ACI = (() => {
  const t = BASE.slice();
  const V = [1, 0.65, 0.5, 0.3, 0.15];
  for (let i = 10; i < 250; i++) {
    const h = Math.floor((i - 10) / 10) * 15, d = i % 10, v = V[d >> 1], s = d & 1 ? 0.5 : 1;
    const f = n => { const k = (n + h / 60) % 6; return v - v * s * Math.max(0, Math.min(k, 4 - k, 1)); };
    t.push(hex(f(5) * 255, f(3) * 255, f(1) * 255));
  }
  for (const g of [0x33, 0x50, 0x69, 0x82, 0xbe, 0xff]) t.push(hex(g, g, g));
  return t;
})();
export const aciHex = i => ACI[i] || '#ffffff';
const rgbOf = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16));
export function nearestAci(h) {
  const [r, g, b] = rgbOf(h); let best = 7, bd = Infinity;
  for (let i = 1; i < 256; i++) { const [x, y, z] = rgbOf(ACI[i]), d = (x - r) ** 2 + (y - g) ** 2 + (z - b) ** 2; if (d < bd) { bd = d; best = i; } }
  return best;
}

// ---------- 讀 ----------
/** 檔案冇寫明（或者寫錯）編碼時，用介面語言估：繁中 Big5、簡中 GBK、日文 Shift_JIS、韓文 EUC-KR。 */
let guessEnc = '';
export function setGuessEncoding(lang) { lang = String(lang || '').toLowerCase(); guessEnc = /^zh.*(hans|cn|sg)/.test(lang) ? 'gbk' : /^zh/.test(lang) ? 'big5' : /^ja/.test(lang) ? 'shift_jis' : /^ko/.test(lang) ? 'euc-kr' : ''; }
function decode(bytes) {
  const head = new TextDecoder('latin1').decode(bytes.subarray(0, Math.min(bytes.length, 200000)));
  if (head.startsWith('AutoCAD Binary DXF')) throw new Error('binary');
  try { return new TextDecoder('utf-8', { fatal: true }).decode(bytes); } catch (e) { }
  const cp = (/\$DWGCODEPAGE\s*\r?\n\s*3\s*\r?\n\s*(\S+)/i.exec(head) || [])[1] || '';
  const n = (/(\d+)/.exec(cp) || [])[1];
  const label = { 932: 'shift_jis', 936: 'gbk', 949: 'euc-kr', 950: 'big5', 874: 'windows-874' }[n] || (n && /^125\d$/.test(n) ? 'windows-' + n : 'windows-1252');
  if (label === 'windows-1252' && guessEnc) { try { return new TextDecoder(guessEnc, { fatal: true }).decode(bytes); } catch (e) { } }
  try { return new TextDecoder(label).decode(bytes); } catch (e) { return new TextDecoder('latin1').decode(bytes); }
}
const unesc = s => String(s).replace(/\\U\+([0-9A-Fa-f]{4})/g, (_, h) => String.fromCharCode(parseInt(h, 16)));
const unpct = s => unesc(s).replace(/%%[dD]/g, '°').replace(/%%[pP]/g, '±').replace(/%%[cC]/g, '⌀').replace(/%%[uUoOkK]/g, '').replace(/%%%/g, '%');
function mtext(s) {
  s = s.replace(/\\\\/g, '\u0001').replace(/\\\{/g, '\u0002').replace(/\\\}/g, '\u0003');
  s = s.replace(/\\P/g, '\n').replace(/\\~/g, ' ').replace(/\\S([^;]*);/g, (_, x) => x.replace(/[\^#]/g, '/')).replace(/\\[fFHhCcTtQqWwAap][^;\\]*;/g, '').replace(/\\[LlOoKkNX]/g, '').replace(/[{}]/g, '');
  return unpct(s.replace(/\u0001/g, '\\').replace(/\u0002/g, '{').replace(/\u0003/g, '}'));
}
const D = Math.PI / 180;

export function parseDxf(bytes) {
  const lines = decode(bytes).split(/\r\n|\r|\n/);
  let i = 0;
  while (i < lines.length && lines[i].trim() !== '0') i++;      // 跳過開頭嘅雜項
  const next = () => {     // 讀一個記錄：{ type, p:[[碼, 值]] }
    if (i + 1 >= lines.length) return null;
    const type = lines[i + 1].trim().toUpperCase(); i += 2;
    const p = [];
    while (i + 1 < lines.length) { const c = parseInt(lines[i], 10); if (c === 0) break; if (!isNaN(c)) p.push([c, lines[i + 1]]); i += 2; }
    return { type, p };
  };
  const get = (r, c, d) => { for (const q of r.p) if (q[0] === c) return q[1].trim(); return d; };
  const num = (r, c, d = 0) => { const v = parseFloat(get(r, c)); return isFinite(v) ? v : d; };
  const raw = (r, c, d = '') => { for (const q of r.p) if (q[0] === c) return q[1].replace(/^\s+|\s+$/g, ''); return d; };
  const pt = (r, cx, d) => { const x = get(r, cx), y = get(r, cx + 10); return x == null && y == null ? d : [parseFloat(x) || 0, parseFloat(y) || 0]; };

  const layers = [], blocks = new Map(), model = [], skipped = {};
  let section = '', table = '', block = null, poly = null, insAttr = null, lossy = false, dimscale = 1;
  const skip = t => { skipped[t] = (skipped[t] || 0) + 1; lossy = true; };
  const common = r => {
    const c = get(r, 62), tc = get(r, 420); let color = null;
    if (tc != null && isFinite(+tc)) color = '#' + (+tc & 0xffffff).toString(16).padStart(6, '0');
    else if (c != null) { const n = parseInt(c, 10); color = n === 0 ? 'byblock' : n === 256 || isNaN(n) ? null : Math.abs(n) % 256; }
    return { layer: unesc(get(r, 8, '0')) || '0', color };
  };
  const flip = r => num(r, 230, 1) < 0;
  const textAt = (q, s, h, rot, hal, val) => {   // 由對齊點計返第一行基線左邊嗰點
    const ls = s.split('\n'), w = Math.max(...ls.map(l => textW(l, h))), d = [Math.cos(rot), Math.sin(rot)], up = perp(d);
    return add(add(q, mul(d, -w * hal)), mul(up, -val));
  };
  function conv(r) {
    const b = common(r), out = [], T = r.type;
    if (+get(r, 67, 0) === 1 && !block) return out;            // 圖紙空間
    if (+get(r, 60, 0) === 1) return out;
    switch (T) {
      case 'LINE': out.push({ ...b, t: 'line', a: pt(r, 10, [0, 0]), b: pt(r, 11, [0, 0]) }); break;
      case 'CIRCLE': { const c = pt(r, 10, [0, 0]); if (flip(r)) c[0] = -c[0]; const rad = num(r, 40); if (rad > 0) out.push({ ...b, t: 'circle', c, r: rad }); break; }
      case 'ARC': {
        const c = pt(r, 10, [0, 0]); let a0 = num(r, 50) * D, a1 = num(r, 51) * D;
        if (flip(r)) { c[0] = -c[0]; [a0, a1] = [Math.PI - a1, Math.PI - a0]; }
        const rad = num(r, 40); if (rad > 0) out.push({ ...b, t: 'arc', c, r: rad, a0: normA(a0), a1: normA(a1) });
        break;
      }
      case 'LWPOLYLINE': case 'LEADER': {
        const pts = []; const f = T === 'LWPOLYLINE' && flip(r) ? -1 : 1;
        for (const [c, v] of r.p) { if (c === 10) pts.push([f * (parseFloat(v) || 0), 0, 0]); else if (c === 20 && pts.length) pts[pts.length - 1][1] = parseFloat(v) || 0; else if (c === 42 && pts.length && T === 'LWPOLYLINE') pts[pts.length - 1][2] = f * (parseFloat(v) || 0); }
        if (pts.length >= 2) out.push({ ...b, t: 'pline', pts, closed: T === 'LWPOLYLINE' && !!(num(r, 70) & 1) });
        break;
      }
      case 'TEXT': case 'ATTRIB': {
        const h = num(r, 40, 1) || 1, s = unpct(raw(r, 1)), rot = num(r, 50) * D, hal = num(r, 72), val = num(r, T === 'TEXT' ? 73 : 74);
        if (!s.trim()) break;
        let p = pt(r, 10, [0, 0]); const q = pt(r, 11);
        if ((hal || val) && q && hal !== 3 && hal !== 5) p = textAt(q, s, h, rot, hal === 1 || hal === 4 ? .5 : hal === 2 ? 1 : 0, hal === 4 ? h / 2 : val === 2 ? h / 2 : val === 3 ? h : 0);
        out.push({ ...b, t: 'text', p, h, s, rot: normA(rot) });
        break;
      }
      case 'MTEXT': {
        let s = ''; for (const [c, v] of r.p) if (c === 3 || c === 1) s += v.replace(/\r$/, '');
        s = mtext(s).replace(/\n+$/, ''); if (!s.trim()) break;
        const h = num(r, 40, 1) || 1, dir = pt(r, 11), rot = dir ? ang(dir) : num(r, 50) * D, at = num(r, 71, 1), n = s.split('\n').length;
        const col = (at - 1) % 3, row = Math.floor((at - 1) / 3), H = h + (n - 1) * h * LINE_GAP;
        out.push({ ...b, t: 'text', p: textAt(pt(r, 10, [0, 0]), s, h, rot, col / 2, row === 0 ? h : row === 1 ? h - H / 2 : h - H), h, s, rot: normA(rot) });
        break;
      }
      case 'INSERT': out.push({ ...b, ins: unesc(get(r, 2, '')), p: pt(r, 10, [0, 0]), sx: num(r, 41, 1) || 1, sy: num(r, 42, 1) || 1, rot: num(r, 50) * D, fl: flip(r) }); break;
      case 'DIMENSION': {
        const ty = num(r, 70) & 7, a = pt(r, 13), bb = pt(r, 14), p = pt(r, 10), an = num(r, 50) * D, name = unesc(get(r, 2, ''));
        let mode = ty === 1 ? 'al' : ty === 0 ? (Math.abs(Math.sin(an)) < 1e-6 ? 'h' : Math.abs(Math.cos(an)) < 1e-6 ? 'v' : null) : null;
        // 有啲軟件將「對齊標註」寫成轉咗角度嘅線性標註：角度同兩點連線平行就當對齊
        if (!mode && ty === 0 && a && bb) { const d = sub(bb, a), l = Math.hypot(d[0], d[1]); if (l > 1e-12 && Math.abs(d[0] * Math.sin(an) - d[1] * Math.cos(an)) / l < 1e-6) mode = 'al'; }
        if (mode && a && bb && p) { const ov = unpct(raw(r, 1)); out.push({ ...b, t: 'dim', a, b: bb, p, mode, h: 0, blk: name, s: ov && ov !== '<>' && ov.trim() ? ov.replace(/<>/g, '').trim() || undefined : undefined }); }
        else if (name) { out.push({ ...b, ins: name, p: [0, 0], sx: 1, sy: 1, rot: 0, dimx: true }); }
        break;
      }
      case 'ELLIPSE': {
        const c = pt(r, 10, [0, 0]), m = pt(r, 11, [1, 0]), k = num(r, 40, 1), t0 = num(r, 41, 0); let t1 = num(r, 42, TAU);
        if (t1 <= t0 + 1e-9) t1 += TAU;
        const full = t1 - t0 >= TAU - 1e-6, n = Math.max(8, Math.ceil(96 * (t1 - t0) / TAU)), pts = [], mp = mul(perp(m), k);
        for (let j = 0; j < (full ? n : n + 1); j++) { const t = t0 + (t1 - t0) * j / n; pts.push([c[0] + m[0] * Math.cos(t) + mp[0] * Math.sin(t), c[1] + m[1] * Math.cos(t) + mp[1] * Math.sin(t), 0]); }
        out.push({ ...b, t: 'pline', pts, closed: full });
        break;
      }
      case 'SPLINE': {
        const deg = Math.max(1, num(r, 71, 3)), knots = [], ctrl = [], fit = [], wts = [];
        for (const [c, v] of r.p) { const f = parseFloat(v) || 0; if (c === 40) knots.push(f); else if (c === 41) wts.push(f); else if (c === 10) ctrl.push([f, 0]); else if (c === 20 && ctrl.length) ctrl[ctrl.length - 1][1] = f; else if (c === 11) fit.push([f, 0]); else if (c === 21 && fit.length) fit[fit.length - 1][1] = f; }
        let pts = null;
        if (ctrl.length > deg && knots.length >= ctrl.length + deg + 1) {
          const n = ctrl.length, u0 = knots[deg], u1 = knots[n], N = Math.min(2000, Math.max(24, n * 10)); pts = [];
          for (let j = 0; j <= N; j++) {
            const u = Math.min(u1, u0 + (u1 - u0) * j / N); let k = deg; while (k < n - 1 && knots[k + 1] <= u) k++;
            const d = []; for (let q = 0; q <= deg; q++) { const w = wts.length === n ? wts[k - deg + q] : 1; d.push([ctrl[k - deg + q][0] * w, ctrl[k - deg + q][1] * w, w]); }
            for (let rr = 1; rr <= deg; rr++) for (let q = deg; q >= rr; q--) { const den = knots[q + 1 + k - rr] - knots[q + k - deg], al = den > 1e-300 ? (u - knots[q + k - deg]) / den : 0; d[q] = [0, 1, 2].map(x => (1 - al) * d[q - 1][x] + al * d[q][x]); }
            pts.push([d[deg][0] / (d[deg][2] || 1), d[deg][1] / (d[deg][2] || 1), 0]);
          }
        } else if (fit.length >= 2) pts = fit.map(p => [p[0], p[1], 0]); else if (ctrl.length >= 2) pts = ctrl.map(p => [p[0], p[1], 0]);
        if (pts) out.push({ ...b, t: 'pline', pts, closed: false });
        break;
      }
      case 'SOLID': case 'TRACE': case '3DFACE': {
        const q = [pt(r, 10), pt(r, 11), pt(r, 12), pt(r, 13)]; if (!q[0] || !q[1] || !q[2]) break;
        const ord = T === '3DFACE' ? [0, 1, 2, 3] : [0, 1, 3, 2], pts = [];
        for (const k of ord) { const p = q[k]; if (p && !pts.some(x => x[0] === p[0] && x[1] === p[1])) pts.push([p[0], p[1], 0]); }
        if (pts.length >= 3) out.push({ ...b, t: 'pline', pts, closed: true });
        break;
      }
      case 'POINT': case 'VIEWPORT': case 'ATTDEF': case 'SEQEND': case 'VERTEX': break;
      default: skip(T);
    }
    return out;
  }

  for (let r = next(); r; r = next()) {
    const T = r.type;
    if (T === 'EOF') break;
    if (T === 'SECTION') { section = (get(r, 2, '') || '').toUpperCase(); if (section === 'HEADER') { for (let k = 0; k < r.p.length - 1; k++) if (r.p[k][0] === 9 && r.p[k][1].trim() === '$DIMSCALE') dimscale = parseFloat(r.p[k + 1][1]) || 1; } continue; }
    if (T === 'ENDSEC') { section = ''; continue; }
    if (section === 'TABLES') {
      if (T === 'TABLE') table = (get(r, 2, '') || '').toUpperCase();
      else if (T === 'ENDTAB') table = '';
      else if (T === 'LAYER' && table === 'LAYER') { const c = parseInt(get(r, 62, '7'), 10) || 7, fl = num(r, 70), name = unesc(get(r, 2, '0')); if (!layers.some(l => l.name === name)) layers.push({ name, color: Math.abs(c) % 256 || 7, on: c >= 0 && !(fl & 1), lock: !!(fl & 4) }); }
      continue;
    }
    if (section !== 'BLOCKS' && section !== 'ENTITIES') continue;
    if (T === 'BLOCK') { block = { name: unesc(get(r, 2, '')), base: pt(r, 10, [0, 0]), ents: [] }; continue; }
    if (T === 'ENDBLK') { if (block) blocks.set(block.name, block); block = null; continue; }
    const dest = block ? block.ents : model;
    if (poly) {
      if (T === 'VERTEX') { if (!(num(r, 70) & 16)) { const p = pt(r, 10, [0, 0]); poly.e.pts.push([p[0], p[1], num(r, 42)]); } continue; }
      if (poly.ok && poly.e.pts.length >= 2) poly.dest.push(poly.e); else if (!poly.ok) skip('POLYLINE (3D)');
      poly = null; if (T === 'SEQEND') continue;
    }
    if (insAttr && T === 'SEQEND') { insAttr = null; continue; }
    if (T === 'POLYLINE') { const fl = num(r, 70); poly = { e: { ...common(r), t: 'pline', pts: [], closed: !!(fl & 1) }, ok: !(fl & (16 | 64)), dest }; if (+get(r, 67, 0) === 1 && !block) poly.dest = []; continue; }
    for (const e of conv(r)) dest.push(e);
    if (T === 'INSERT' && +get(r, 66, 0) === 1) insAttr = true;
  }
  if (poly && poly.ok && poly.e.pts.length >= 2) poly.dest.push(poly.e);

  // 拆圖塊
  const cache = new Map(); let count = 0; const LIMIT = 400000;
  const applyM = (e, m) => {
    const T = tMatrix(m); if (T) return [applyT(e, T)];
    const f = p => [m[0] * p[0] + m[2] * p[1] + m[4], m[1] * p[0] + m[3] * p[1] + m[5]];
    if (e.t === 'text') { const r = e.rot || 0, p0 = f(e.p), d = sub(f(add(e.p, [Math.cos(r), Math.sin(r)])), p0); return [{ ...e, p: p0, h: e.h * Math.hypot(m[2], m[3]), rot: normA(ang(d)) }]; }
    if (e.t === 'dim') { const g = dimGeo(e); return [...g.segs.map(([a, b]) => ({ layer: e.layer, color: e.color, t: 'line', a: f(a), b: f(b) })), ...applyM({ layer: e.layer, color: e.color, t: 'text', p: add(g.text.p, mul([Math.cos(g.text.rot), Math.sin(g.text.rot)], -textW(g.text.s, e.h) / 2)), h: e.h, s: g.text.s, rot: g.text.rot }, m)]; }
    return [{ layer: e.layer, color: e.color, t: 'pline', pts: tess(e).map(p => { const q = f(p); return [q[0], q[1], 0]; }), closed: pathClosed(e) }];
  };
  const dimH = name => { const b = blocks.get(name); if (!b) return null; for (const e of b.ents) if (e.t === 'text') return e; return null; };
  function resolve(list, depth, seen) {
    const out = [];
    for (const e of list) {
      if (count > LIMIT) { lossy = true; break; }
      if (!e.ins) {
        if (e.t === 'dim') {
          const tx = dimH(e.blk); const o = { ...e }; delete o.blk;
          o.h = tx ? tx.h : 2.5 * dimscale;
          if (!o.s && tx && tx.s.trim() && tx.s.trim() !== fmt(dimGeo(o).val)) o.s = tx.s.trim();
          if (!o.s) delete o.s;
          out.push(o); count++; continue;
        }
        out.push(e); count++; continue;
      }
      const b = blocks.get(e.ins); if (!b || depth > 12 || seen.has(e.ins)) continue;
      if (!e.dimx) lossy = true;
      let inner = cache.get(e.ins);
      if (!inner) { seen.add(e.ins); inner = resolve(b.ents, depth + 1, seen); seen.delete(e.ins); cache.set(e.ins, inner); }
      const c = Math.cos(e.rot), s = Math.sin(e.rot), sx = e.fl ? -e.sx : e.sx, px = e.fl ? -e.p[0] : e.p[0];
      const m = [c * sx, s * sx, -s * e.sy, c * e.sy, 0, 0];
      m[4] = px - (m[0] * b.base[0] + m[2] * b.base[1]); m[5] = e.p[1] - (m[1] * b.base[0] + m[3] * b.base[1]);
      for (const x of inner) for (const y of applyM(x, m)) {
        if (count++ > LIMIT) break;
        out.push({ ...y, layer: y.layer === '0' ? e.layer : y.layer, color: y.color === 'byblock' ? e.color : y.color });
      }
    }
    return out;
  }
  const ents = resolve(model, 0, new Set()).filter(e => e.t).map(e => e.color === 'byblock' ? { ...e, color: null } : e);
  if (!layers.some(l => l.name === '0')) layers.unshift({ name: '0', color: 7, on: true, lock: false });
  const known = new Set(layers.map(l => l.name));
  for (const e of ents) if (!known.has(e.layer)) { known.add(e.layer); layers.push({ name: e.layer, color: 7, on: true, lock: false }); }
  return { ents, layers, skipped, lossy };
}

// ---------- 寫（R12） ----------
const esc = s => String(s).replace(/[^\x20-\x7e]/g, ch => { let o = ''; for (let k = 0; k < ch.length; k++) o += '\\U+' + ch.charCodeAt(k).toString(16).toUpperCase().padStart(4, '0'); return o; });
const N = v => fmt(Math.abs(v) < 1e-10 ? 0 : v, 8);
export function writeDxf(doc) {
  const o = [];
  const w = (...a) => { for (let k = 0; k < a.length; k += 2) o.push(String(a[k]).padStart(3, ' '), String(a[k + 1])); };
  const col = c => c == null ? [] : [62, typeof c === 'string' ? nearestAci(c) : c];
  const head = e => [8, esc(e.layer || '0'), ...col(e.color)];
  const ent = (e) => {
    switch (e.t) {
      case 'line': w(0, 'LINE', ...head(e), 10, N(e.a[0]), 20, N(e.a[1]), 30, 0, 11, N(e.b[0]), 21, N(e.b[1]), 31, 0); break;
      case 'circle': w(0, 'CIRCLE', ...head(e), 10, N(e.c[0]), 20, N(e.c[1]), 30, 0, 40, N(e.r)); break;
      case 'arc': w(0, 'ARC', ...head(e), 10, N(e.c[0]), 20, N(e.c[1]), 30, 0, 40, N(e.r), 50, N(normA(e.a0) / D), 51, N(normA(e.a1) / D)); break;
      case 'pline':
        w(0, 'POLYLINE', ...head(e), 66, 1, 10, 0, 20, 0, 30, 0, 70, e.closed ? 1 : 0);
        for (const p of e.pts) { w(0, 'VERTEX', 8, esc(e.layer || '0'), 10, N(p[0]), 20, N(p[1]), 30, 0); if (p[2]) w(42, N(p[2])); }
        w(0, 'SEQEND', 8, esc(e.layer || '0'));
        break;
      case 'text': {
        const r = e.rot || 0, dn = mul(perp([Math.cos(r), Math.sin(r)]), -e.h * LINE_GAP);
        textLines(e).forEach((s, k) => {
          if (!s) return;
          const p = add(e.p, mul(dn, k));
          if (e.align === 'c') { const q = add(p, mul([Math.cos(r), Math.sin(r)], -textW(s, e.h) / 2)); w(0, 'TEXT', ...head(e), 10, N(q[0]), 20, N(q[1]), 30, 0, 40, N(e.h), 1, esc(s), 50, N(normA(r) / D), 72, 1, 11, N(p[0]), 21, N(p[1]), 31, 0); }
          else { w(0, 'TEXT', ...head(e), 10, N(p[0]), 20, N(p[1]), 30, 0, 40, N(e.h), 1, esc(s)); if (r) w(50, N(normA(r) / D)); }
        });
        break;
      }
    }
  };
  let x0 = 0, y0 = 0, x1 = 0, y1 = 0, any = false;
  for (const e of doc.ents) for (const p of e.t === 'text' ? [e.p] : e.t === 'dim' ? [e.a, e.b, e.p] : tess(e, TAU / 8)) { if (!any) { x0 = x1 = p[0]; y0 = y1 = p[1]; any = true; } x0 = Math.min(x0, p[0]); x1 = Math.max(x1, p[0]); y0 = Math.min(y0, p[1]); y1 = Math.max(y1, p[1]); }
  w(0, 'SECTION', 2, 'HEADER', 9, '$ACADVER', 1, 'AC1009', 9, '$INSBASE', 10, 0, 20, 0, 30, 0, 9, '$EXTMIN', 10, N(x0), 20, N(y0), 30, 0, 9, '$EXTMAX', 10, N(x1), 20, N(y1), 30, 0, 0, 'ENDSEC');
  w(0, 'SECTION', 2, 'TABLES');
  w(0, 'TABLE', 2, 'LTYPE', 70, 1, 0, 'LTYPE', 2, 'CONTINUOUS', 70, 0, 3, 'Solid line', 72, 65, 73, 0, 40, 0, 0, 'ENDTAB');
  w(0, 'TABLE', 2, 'LAYER', 70, doc.layers.length);
  for (const l of doc.layers) w(0, 'LAYER', 2, esc(l.name), 70, l.lock ? 4 : 0, 62, (l.on === false ? -1 : 1) * (typeof l.color === 'string' ? nearestAci(l.color) : l.color || 7), 6, 'CONTINUOUS');
  w(0, 'ENDTAB');
  w(0, 'TABLE', 2, 'STYLE', 70, 1, 0, 'STYLE', 2, 'STANDARD', 70, 0, 40, 0, 41, 1, 50, 0, 71, 0, 42, 2.5, 3, 'txt', 4, '', 0, 'ENDTAB');
  w(0, 'TABLE', 2, 'DIMSTYLE', 70, 1, 0, 'DIMSTYLE', 2, 'STANDARD', 70, 0, 0, 'ENDTAB');
  w(0, 'ENDSEC');
  const dims = doc.ents.filter(e => e.t === 'dim');
  w(0, 'SECTION', 2, 'BLOCKS');
  dims.forEach((e, k) => {
    const g = dimGeo(e), name = '*D' + (k + 1), L = { layer: e.layer, color: e.color };
    w(0, 'BLOCK', 8, '0', 2, name, 70, 1, 10, 0, 20, 0, 30, 0, 3, name);
    for (const [a, b] of g.segs) ent({ ...L, t: 'line', a, b });
    for (const t of g.tris) w(0, 'SOLID', ...head(e), 10, N(t[0][0]), 20, N(t[0][1]), 30, 0, 11, N(t[1][0]), 21, N(t[1][1]), 31, 0, 12, N(t[2][0]), 22, N(t[2][1]), 32, 0, 13, N(t[2][0]), 23, N(t[2][1]), 33, 0);
    ent({ ...L, t: 'text', align: 'c', ...g.text });
    w(0, 'ENDBLK', 8, '0');
  });
  w(0, 'ENDSEC', 0, 'SECTION', 2, 'ENTITIES');
  let dn = 0;
  for (const e of doc.ents) {
    if (e.t !== 'dim') { ent(e); continue; }
    const g = dimGeo(e);
    w(0, 'DIMENSION', ...head(e), 2, '*D' + (++dn), 10, N(g.B[0]), 20, N(g.B[1]), 30, 0, 11, N(g.text.p[0]), 21, N(g.text.p[1]), 31, 0, 13, N(e.a[0]), 23, N(e.a[1]), 33, 0, 14, N(e.b[0]), 24, N(e.b[1]), 34, 0, 70, 32 + (e.mode === 'al' ? 1 : 0));
    if (e.s) w(1, esc(e.s));
    if (e.mode !== 'al') w(50, e.mode === 'v' ? 90 : 0);
    w(3, 'STANDARD');
  }
  w(0, 'ENDSEC', 0, 'EOF');
  return o.join('\r\n') + '\r\n';
}
// 冇用到但保留俾外面用
export const _u = { polar, sweep, prims };
