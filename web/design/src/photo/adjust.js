// 修圖：調整（亮度、色階、曲線、色相…）同濾鏡（模糊、銳化、雜訊…）。
// 全部都係純函數：收一個 ImageData（會直接改佢），唔掂 DOM。
// 演算法係通用嘅影像處理公式（查表、卷積、盒狀模糊近似高斯…），唔係抄任何軟件。

const c8 = v => v < 0 ? 0 : v > 255 ? 255 : v;

/** 用三張 256 格查表改 RGB（快）。 */
function lut3(img, lr, lg = lr, lb = lr) {
  const d = img.data;
  for (let i = 0; i < d.length; i += 4) { d[i] = lr[d[i]]; d[i + 1] = lg[d[i + 1]]; d[i + 2] = lb[d[i + 2]]; }
  return img;
}
const mkLut = f => { const t = new Uint8ClampedArray(256); for (let i = 0; i < 256; i++) t[i] = f(i); return t; };

// ---------- 色彩空間 ----------
function rgb2hsl(r, g, b) {
  r /= 255; g /= 255; b /= 255;
  const mx = Math.max(r, g, b), mn = Math.min(r, g, b), l = (mx + mn) / 2;
  if (mx === mn) return [0, 0, l];
  const d = mx - mn, s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
  let h = mx === r ? (g - b) / d + (g < b ? 6 : 0) : mx === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return [h * 60, s, l];
}
function hue2(p, q, t) { if (t < 0) t += 1; if (t > 1) t -= 1; return t < 1 / 6 ? p + (q - p) * 6 * t : t < 1 / 2 ? q : t < 2 / 3 ? p + (q - p) * (2 / 3 - t) * 6 : p; }
function hsl2rgb(h, s, l) {
  h = ((h % 360) + 360) % 360 / 360;
  if (s <= 0) { const v = l * 255; return [v, v, v]; }
  const q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
  return [hue2(p, q, h + 1 / 3) * 255, hue2(p, q, h) * 255, hue2(p, q, h - 1 / 3) * 255];
}
const lum = (r, g, b) => 0.299 * r + 0.587 * g + 0.114 * b;

// ---------- 調整 ----------
/** 亮度 / 對比（-150..150 / -100..100）。 */
export function brightnessContrast(img, { b = 0, c = 0 }) {
  // 亮度：gamma 曲線（推中間調，黑白兩端唔郁，唔會爆白）；對比：以中間灰為軸拉斜
  const gm = Math.pow(2, -b / 75), k = Math.tan((Math.min(99, c) / 100 + 1) * Math.PI / 4);
  return lut3(img, mkLut(i => (255 * Math.pow(i / 255, gm) - 128) * k + 128));
}
/** 色階：輸入黑 / 白點、中間調（gamma 0.1..9.99）、輸出黑 / 白點；ch = rgb / r / g / b。 */
export function levels(img, { inB = 0, inW = 255, gamma = 1, outB = 0, outW = 255, ch = 'rgb' }) {
  const span = Math.max(1, inW - inB);
  const t = mkLut(i => { const x = Math.min(1, Math.max(0, (i - inB) / span)); return outB + Math.pow(x, 1 / gamma) * (outW - outB); });
  const id = mkLut(i => i);
  return lut3(img, ch === 'rgb' || ch === 'r' ? t : id, ch === 'rgb' || ch === 'g' ? t : id, ch === 'rgb' || ch === 'b' ? t : id);
}
/** 曲線：pts = [[in, out], …]（0..255，已排序），單調三次插值；ch 同色階。 */
export function curveLut(pts) {
  const p = [...pts].sort((a, b) => a[0] - b[0]);
  const n = p.length;
  if (n < 2) return mkLut(i => i);
  const xs = p.map(q => q[0]), ys = p.map(q => q[1]);
  const dx = [], m = [];
  for (let i = 0; i < n - 1; i++) { dx[i] = Math.max(1e-6, xs[i + 1] - xs[i]); m[i] = (ys[i + 1] - ys[i]) / dx[i]; }
  const t = [m[0]];
  for (let i = 1; i < n - 1; i++) t[i] = m[i - 1] * m[i] <= 0 ? 0 : (m[i - 1] + m[i]) / 2;
  t[n - 1] = m[n - 2];
  for (let i = 0; i < n - 1; i++) {   // Fritsch–Carlson：保持單調，唔會彈過頭
    if (m[i] === 0) { t[i] = t[i + 1] = 0; continue; }
    const a = t[i] / m[i], b = t[i + 1] / m[i], s = a * a + b * b;
    if (s > 9) { const k = 3 / Math.sqrt(s); t[i] = k * a * m[i]; t[i + 1] = k * b * m[i]; }
  }
  return mkLut(x => {
    if (x <= xs[0]) return ys[0];
    if (x >= xs[n - 1]) return ys[n - 1];
    let i = 0; while (x > xs[i + 1]) i++;
    const h = dx[i], u = (x - xs[i]) / h, u2 = u * u, u3 = u2 * u;
    return (2 * u3 - 3 * u2 + 1) * ys[i] + (u3 - 2 * u2 + u) * h * t[i] + (-2 * u3 + 3 * u2) * ys[i + 1] + (u3 - u2) * h * t[i + 1];
  });
}
export function curves(img, { pts, ch = 'rgb' }) {
  const t = curveLut(pts), id = mkLut(i => i);
  return lut3(img, ch === 'rgb' || ch === 'r' ? t : id, ch === 'rgb' || ch === 'g' ? t : id, ch === 'rgb' || ch === 'b' ? t : id);
}
/** 曝光度：ev（-5..5）、位移（-0.5..0.5）、gamma（0.1..9.99）。 */
export function exposure(img, { ev = 0, offset = 0, gamma = 1 }) {
  const k = Math.pow(2, ev);
  return lut3(img, mkLut(i => { const x = Math.max(0, (i / 255) * k + offset); return Math.pow(x, 1 / gamma) * 255; }));
}
/** 色相 / 飽和度 / 明度（-180..180 / -100..100 / -100..100）；colorize = 上色。 */
export function hueSat(img, { h = 0, s = 0, l = 0, colorize = false }) {
  const d = img.data;
  for (let i = 0; i < d.length; i += 4) {
    let [hh, ss, ll] = rgb2hsl(d[i], d[i + 1], d[i + 2]);
    if (colorize) { hh = (h + 360) % 360; ss = Math.max(0, Math.min(100, s)) / 100; }
    else { hh += h; ss = s >= 0 ? ss + (1 - ss) * (s / 100) * ss * 1.5 : ss * (1 + s / 100); ss = Math.max(0, Math.min(1, ss)); }
    ll = l >= 0 ? ll + (1 - ll) * (l / 100) : ll * (1 + l / 100);
    const [r, g, b] = hsl2rgb(hh, ss, ll);
    d[i] = r; d[i + 1] = g; d[i + 2] = b;
  }
  return img;
}
/** 自然飽和度：vib 只加強唔夠飽和嘅顏色，sat 全部一樣（-100..100）。 */
export function vibrance(img, { vib = 0, sat = 0 }) {
  const d = img.data, v = vib / 100, s = sat / 100;
  for (let i = 0; i < d.length; i += 4) {
    const r = d[i], g = d[i + 1], b = d[i + 2];
    const amt = (1 + s) * (1 + v * (1 - (Math.max(r, g, b) - Math.min(r, g, b)) / 255) * (v > 0 ? 1.2 : 1));
    const L = lum(r, g, b);
    d[i] = L + (r - L) * amt; d[i + 1] = L + (g - L) * amt; d[i + 2] = L + (b - L) * amt;
  }
  return img;
}
/** 色彩平衡：三個色調範圍（陰影 / 中間調 / 亮部）各自 青紅、洋紅綠、黃藍（-100..100）。 */
export function colorBalance(img, { sh = [0, 0, 0], mid = [0, 0, 0], hi = [0, 0, 0], keepLum = true }) {
  const d = img.data;
  const w = x => { const t = x / 255; return [Math.max(0, 1 - t * 2.5) , 1 - Math.min(1, Math.abs(t - 0.5) * 2.2), Math.max(0, t * 2.5 - 1.5)]; };
  for (let i = 0; i < d.length; i += 4) {
    const r = d[i], g = d[i + 1], b = d[i + 2], L = lum(r, g, b), [ws, wm, wh] = w(L);
    let nr = r + (sh[0] * ws + mid[0] * wm + hi[0] * wh) * 1.1;
    let ng = g + (sh[1] * ws + mid[1] * wm + hi[1] * wh) * 1.1;
    let nb = b + (sh[2] * ws + mid[2] * wm + hi[2] * wh) * 1.1;
    if (keepLum) { const k = L - lum(nr, ng, nb); nr += k; ng += k; nb += k; }
    d[i] = nr; d[i + 1] = ng; d[i + 2] = nb;
  }
  return img;
}
/** 黑白：六個顏色範圍各自決定變幾光（%）；tint = 可選色調。 */
export function blackWhite(img, { r = 40, y = 60, g = 40, c = 60, b = 20, m = 80, tint = null }) {
  const d = img.data, wts = [r, y, g, c, b, m].map(v => v / 100);
  let tr = 1, tg = 1, tb = 1;
  if (tint) { const n = parseInt(tint.slice(1), 16); tr = (n >> 16 & 255) / 255; tg = (n >> 8 & 255) / 255; tb = (n & 255) / 255; const k = 1 / Math.max(0.01, lum(tr, tg, tb)); tr *= k; tg *= k; tb *= k; }
  for (let i = 0; i < d.length; i += 4) {
    const R = d[i], G = d[i + 1], B = d[i + 2], mn = Math.min(R, G, B);
    const [h, s] = rgb2hsl(R, G, B);
    // 灰度 = 最暗嗰個 + 色彩部分按色相權重（色相夾喺兩個最近嘅範圍之間）
    const pos = h / 60, i0 = Math.floor(pos) % 6, i1 = (i0 + 1) % 6, f = pos - Math.floor(pos);
    const wgt = wts[i0] * (1 - f) + wts[i1] * f;
    const chroma = Math.max(R, G, B) - mn;
    const v = s > 0 ? mn + chroma * wgt : R;
    d[i] = v * tr; d[i + 1] = v * tg; d[i + 2] = v * tb;
  }
  return img;
}
/** 相片濾鏡：疊一層顏色（density 0..100），keepLum 保持光暗。 */
export function photoFilter(img, { color = '#ec8a00', density = 25, keepLum = true }) {
  const n = parseInt(color.slice(1), 16), cr = n >> 16 & 255, cg = n >> 8 & 255, cb = n & 255, k = density / 100, d = img.data;
  for (let i = 0; i < d.length; i += 4) {
    const r = d[i], g = d[i + 1], b = d[i + 2], L = lum(r, g, b);
    let nr = r * (1 - k) + (r * cr / 255) * k, ng = g * (1 - k) + (g * cg / 255) * k, nb = b * (1 - k) + (b * cb / 255) * k;
    if (keepLum) { const f = L / Math.max(1, lum(nr, ng, nb)); nr *= f; ng *= f; nb *= f; }
    d[i] = nr; d[i + 1] = ng; d[i + 2] = nb;
  }
  return img;
}
export const invert = img => lut3(img, mkLut(i => 255 - i));
export function desaturate(img) { const d = img.data; for (let i = 0; i < d.length; i += 4) { const v = (Math.max(d[i], d[i + 1], d[i + 2]) + Math.min(d[i], d[i + 1], d[i + 2])) / 2; d[i] = d[i + 1] = d[i + 2] = v; } return img; }
export const posterize = (img, { n = 4 }) => lut3(img, mkLut(i => Math.round(Math.round(i / 255 * (n - 1)) / (n - 1) * 255)));
export function threshold(img, { t = 128 }) { const d = img.data; for (let i = 0; i < d.length; i += 4) { const v = lum(d[i], d[i + 1], d[i + 2]) >= t ? 255 : 0; d[i] = d[i + 1] = d[i + 2] = v; } return img; }

/** 直方圖：{ r, g, b, l }（各 256 格），透明像素唔計。 */
export function histogram(img) {
  const r = new Uint32Array(256), g = new Uint32Array(256), b = new Uint32Array(256), l = new Uint32Array(256), d = img.data;
  for (let i = 0; i < d.length; i += 4) { if (d[i + 3] < 8) continue; r[d[i]]++; g[d[i + 1]]++; b[d[i + 2]]++; l[Math.round(lum(d[i], d[i + 1], d[i + 2]))]++; }
  return { r, g, b, l };
}
/** 頭尾各剪走 clip（0.001 = 0.1%）之後嘅範圍。 */
function range(h, clip) {
  let total = 0; for (const v of h) total += v;
  if (!total) return [0, 255];
  const cut = total * clip;
  let a = 0, s = 0; while (a < 255 && (s += h[a]) <= cut) a++;
  let z = 255; s = 0; while (z > 0 && (s += h[z]) <= cut) z--;
  return z > a ? [a, z] : [0, 255];
}
/** 自動色調：每個色版各自拉闊（會順便修正偏色）。 */
export function autoTone(img) {
  const h = histogram(img), [r0, r1] = range(h.r, 0.001), [g0, g1] = range(h.g, 0.001), [b0, b1] = range(h.b, 0.001);
  const mk = (a, z) => mkLut(i => (i - a) * 255 / (z - a));
  return lut3(img, mk(r0, r1), mk(g0, g1), mk(b0, b1));
}
/** 自動對比：三個色版一齊拉（唔改顏色比例）。 */
export function autoContrast(img) {
  const h = histogram(img), [a, z] = range(h.l, 0.001);
  return lut3(img, mkLut(i => (i - a) * 255 / (z - a)));
}
/** 自動顏色：拉闊之後再令中間調變中性灰。 */
export function autoColor(img) {
  autoTone(img);
  const d = img.data; let sr = 0, sg = 0, sb = 0, n = 0;
  for (let i = 0; i < d.length; i += 4) { if (d[i + 3] < 8) continue; const L = lum(d[i], d[i + 1], d[i + 2]); if (L > 60 && L < 200) { sr += d[i]; sg += d[i + 1]; sb += d[i + 2]; n++; } }
  if (!n) return img;
  sr /= n; sg /= n; sb /= n; const m = (sr + sg + sb) / 3;
  const gm = (avg) => Math.log(m / 255) / Math.log(Math.max(1, avg) / 255);
  const kr = gm(sr), kg = gm(sg), kb = gm(sb);
  const mk = k => mkLut(i => Math.pow(i / 255, isFinite(k) ? Math.max(0.5, Math.min(2, k)) : 1) * 255);
  return lut3(img, mk(kr), mk(kg), mk(kb));
}

// ---------- 模糊（盒狀模糊做三次 ≈ 高斯；邊緣用最近嘅像素）----------
function boxPass(src, dst, w, h, r, horiz) {
  const len = horiz ? w : h, lines = horiz ? h : w, inv = 1 / (2 * r + 1);
  for (let ln = 0; ln < lines; ln++) {
    const at = k => { k = k < 0 ? 0 : k >= len ? len - 1 : k; return (horiz ? ln * w + k : k * w + ln) * 4; };
    let s0 = 0, s1 = 0, s2 = 0, s3 = 0;
    for (let k = -r; k <= r; k++) { const p = at(k); s0 += src[p]; s1 += src[p + 1]; s2 += src[p + 2]; s3 += src[p + 3]; }
    for (let k = 0; k < len; k++) {
      const o = at(k);
      dst[o] = s0 * inv; dst[o + 1] = s1 * inv; dst[o + 2] = s2 * inv; dst[o + 3] = s3 * inv;
      const a = at(k - r), b = at(k + r + 1);
      s0 += src[b] - src[a]; s1 += src[b + 1] - src[a + 1]; s2 += src[b + 2] - src[a + 2]; s3 += src[b + 3] - src[a + 3];
    }
  }
}
/** 高斯模糊（radius 像素）。先預乘 alpha，透明邊唔會出黑邊。 */
export function gaussian(img, { r = 4 }) {
  if (r <= 0) return img;
  const { width: w, height: h, data: d } = img, n = w * h * 4;
  let a = new Float32Array(n), b = new Float32Array(n);
  for (let i = 0; i < n; i += 4) { const al = d[i + 3] / 255; a[i] = d[i] * al; a[i + 1] = d[i + 1] * al; a[i + 2] = d[i + 2] * al; a[i + 3] = d[i + 3]; }
  // 三次盒狀模糊嘅闊度（Kovesi 嘅公式）
  const sigma = r / 2, wIdeal = Math.sqrt(12 * sigma * sigma / 3 + 1);
  let wl = Math.floor(wIdeal); if (wl % 2 === 0) wl--;
  const m = Math.round((12 * sigma * sigma - 3 * wl * wl - 12 * wl - 9) / (-4 * wl - 4));
  for (let k = 0; k < 3; k++) {
    const rr = Math.max(0, ((k < m ? wl : wl + 2) - 1) / 2);
    if (!rr) continue;
    boxPass(a, b, w, h, rr, true); boxPass(b, a, w, h, rr, false);
  }
  for (let i = 0; i < n; i += 4) { const al = a[i + 3]; const k = al > 0 ? 255 / al : 0; d[i] = a[i] * k; d[i + 1] = a[i + 1] * k; d[i + 2] = a[i + 2] * k; d[i + 3] = al; }
  return img;
}
/** 動態模糊：沿住 angle（度）方向，距離 dist 像素取平均。 */
export function motionBlur(img, { angle = 0, dist = 10 }) {
  const { width: w, height: h, data: d } = img, src = new Uint8ClampedArray(d);
  const n = Math.max(1, Math.min(200, Math.round(dist))), a = angle * Math.PI / 180, cx = Math.cos(a), cy = -Math.sin(a);
  const offs = []; for (let k = 0; k <= n; k++) { const t = k - n / 2; offs.push([Math.round(cx * t), Math.round(cy * t)]); }
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    let s0 = 0, s1 = 0, s2 = 0, s3 = 0;
    for (const [ox, oy] of offs) {
      const xx = x + ox < 0 ? 0 : x + ox >= w ? w - 1 : x + ox, yy = y + oy < 0 ? 0 : y + oy >= h ? h - 1 : y + oy, p = (yy * w + xx) * 4, al = src[p + 3];
      s0 += src[p] * al; s1 += src[p + 1] * al; s2 += src[p + 2] * al; s3 += al;
    }
    const o = (y * w + x) * 4;
    if (s3) { d[o] = s0 / s3; d[o + 1] = s1 / s3; d[o + 2] = s2 / s3; }
    d[o + 3] = s3 / offs.length;
  }
  return img;
}
/** 遮色片銳利化：amount（%）、radius、threshold（0..255）。 */
export function unsharp(img, { amount = 100, r = 1.5, th = 0 }) {
  const blur = gaussian(new ImageData(new Uint8ClampedArray(img.data), img.width, img.height), { r: r * 2 }), d = img.data, b = blur.data, k = amount / 100;
  for (let i = 0; i < d.length; i += 4) for (let c = 0; c < 3; c++) { const diff = d[i + c] - b[i + c]; if (Math.abs(diff) >= th) d[i + c] = c8(d[i + c] + diff * k); }
  return img;
}
/** 3×3 卷積（邊緣用最近嘅像素），alpha 唔變。 */
function convolve(img, k, bias = 0, gray = false) {
  const { width: w, height: h, data: d } = img, s = new Uint8ClampedArray(d);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    let r = 0, g = 0, b = 0, j = 0;
    for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++, j++) {
      const xx = x + dx < 0 ? 0 : x + dx >= w ? w - 1 : x + dx, yy = y + dy < 0 ? 0 : y + dy >= h ? h - 1 : y + dy, p = (yy * w + xx) * 4, kv = k[j];
      r += s[p] * kv; g += s[p + 1] * kv; b += s[p + 2] * kv;
    }
    const o = (y * w + x) * 4;
    if (gray) { const v = c8(lum(r, g, b) + bias); d[o] = d[o + 1] = d[o + 2] = v; }
    else { d[o] = r + bias; d[o + 1] = g + bias; d[o + 2] = b + bias; }
  }
  return img;
}
export const sharpen = img => convolve(img, [0, -0.5, 0, -0.5, 3, -0.5, 0, -0.5, 0]);
/** 浮雕：angle（度）、height（1..10）、amount（%）。 */
export function emboss(img, { angle = 135, height = 2, amount = 100 }) {
  const a = angle * Math.PI / 180, dx = Math.round(Math.cos(a) * height), dy = Math.round(-Math.sin(a) * height), k = amount / 100;
  const { width: w, height: h, data: d } = img, s = new Uint8ClampedArray(d);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const p1 = (Math.min(h - 1, Math.max(0, y + dy)) * w + Math.min(w - 1, Math.max(0, x + dx))) * 4, p2 = (Math.min(h - 1, Math.max(0, y - dy)) * w + Math.min(w - 1, Math.max(0, x - dx))) * 4;
    const v = c8(128 + (lum(s[p1], s[p1 + 1], s[p1 + 2]) - lum(s[p2], s[p2 + 1], s[p2 + 2])) * k), o = (y * w + x) * 4;
    d[o] = d[o + 1] = d[o + 2] = v;
  }
  return img;
}
/** 找出邊緣（Sobel，白底黑線）。 */
export function findEdges(img) {
  const { width: w, height: h, data: d } = img, s = new Uint8ClampedArray(d);
  const L = (x, y) => { x = x < 0 ? 0 : x >= w ? w - 1 : x; y = y < 0 ? 0 : y >= h ? h - 1 : y; const p = (y * w + x) * 4; return [s[p], s[p + 1], s[p + 2]]; };
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const o = (y * w + x) * 4;
    for (let c = 0; c < 3; c++) {
      const gx = -L(x - 1, y - 1)[c] - 2 * L(x - 1, y)[c] - L(x - 1, y + 1)[c] + L(x + 1, y - 1)[c] + 2 * L(x + 1, y)[c] + L(x + 1, y + 1)[c];
      const gy = -L(x - 1, y - 1)[c] - 2 * L(x, y - 1)[c] - L(x + 1, y - 1)[c] + L(x - 1, y + 1)[c] + 2 * L(x, y + 1)[c] + L(x + 1, y + 1)[c];
      d[o + c] = 255 - Math.min(255, Math.hypot(gx, gy) / 2);
    }
  }
  return img;
}
/** 增加雜訊：amount（%）、mono = 單色。 */
export function addNoise(img, { amount = 10, mono = true }) {
  const d = img.data, k = amount / 100 * 255;
  for (let i = 0; i < d.length; i += 4) {
    if (mono) { const n = (Math.random() - 0.5) * 2 * k; d[i] += n; d[i + 1] += n; d[i + 2] += n; }
    else { d[i] += (Math.random() - 0.5) * 2 * k; d[i + 1] += (Math.random() - 0.5) * 2 * k; d[i + 2] += (Math.random() - 0.5) * 2 * k; }
  }
  return img;
}
/** 中間值（減少雜訊）：radius 1..5，用每行直方圖滑動。 */
export function median(img, { r = 1 }) {
  const { width: w, height: h, data: d } = img, s = new Uint8ClampedArray(d), size = (2 * r + 1) ** 2, half = size >> 1;
  for (let c = 0; c < 3; c++) {
    for (let y = 0; y < h; y++) {
      const hist = new Uint16Array(256);
      const add = (x, v) => { for (let dy = -r; dy <= r; dy++) { const yy = y + dy < 0 ? 0 : y + dy >= h ? h - 1 : y + dy, xx = x < 0 ? 0 : x >= w ? w - 1 : x; hist[s[(yy * w + xx) * 4 + c]] += v; } };
      for (let dx = -r; dx <= r; dx++) add(dx, 1);
      for (let x = 0; x < w; x++) {
        let acc = 0, m = 0; while ((acc += hist[m]) <= half) m++;
        d[(y * w + x) * 4 + c] = m;
        add(x - r, -1); add(x + r + 1, 1);
      }
    }
  }
  return img;
}
/** 馬賽克：cell 像素一格。 */
export function mosaic(img, { cell = 10 }) {
  const { width: w, height: h, data: d } = img, n = Math.max(2, Math.round(cell));
  for (let by = 0; by < h; by += n) for (let bx = 0; bx < w; bx += n) {
    let r = 0, g = 0, b = 0, a = 0, c = 0;
    const ex = Math.min(w, bx + n), ey = Math.min(h, by + n);
    for (let y = by; y < ey; y++) for (let x = bx; x < ex; x++) { const p = (y * w + x) * 4, al = d[p + 3]; r += d[p] * al; g += d[p + 1] * al; b += d[p + 2] * al; a += al; c++; }
    const rr = a ? r / a : 0, gg = a ? g / a : 0, bb = a ? b / a : 0, aa = a / c;
    for (let y = by; y < ey; y++) for (let x = bx; x < ex; x++) { const p = (y * w + x) * 4; d[p] = rr; d[p + 1] = gg; d[p + 2] = bb; d[p + 3] = aa; }
  }
  return img;
}
/** 高反差保留：原圖 − 模糊 + 128 灰。 */
export function highPass(img, { r = 10 }) {
  const b = gaussian(new ImageData(new Uint8ClampedArray(img.data), img.width, img.height), { r }).data, d = img.data;
  for (let i = 0; i < d.length; i += 4) for (let c = 0; c < 3; c++) d[i + c] = c8(d[i + c] - b[i + c] + 128);
  return img;
}

// ---------- 選取範圍：距離 ----------
/**
 * 每個像素去「另一邊」最近嘅距離（倒角法 3-4，約等於直線距離）。
 * inside = true：計揀咗嘅像素去最近未揀嘅距離；false：倒轉。
 */
export function distanceField(alpha, w, h, inside) {
  const INF = 1e9, D = new Float32Array(w * h);
  for (let i = 0; i < w * h; i++) D[i] = (alpha[i] >= 128) === inside ? INF : 0;
  const A = 1, B = Math.SQRT2;
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const i = y * w + x; let v = D[i]; if (!v) continue;
    if (x > 0) v = Math.min(v, D[i - 1] + A);
    if (y > 0) { v = Math.min(v, D[i - w] + A); if (x > 0) v = Math.min(v, D[i - w - 1] + B); if (x < w - 1) v = Math.min(v, D[i - w + 1] + B); }
    D[i] = v;
  }
  for (let y = h - 1; y >= 0; y--) for (let x = w - 1; x >= 0; x--) {
    const i = y * w + x; let v = D[i]; if (!v) continue;
    if (x < w - 1) v = Math.min(v, D[i + 1] + A);
    if (y < h - 1) { v = Math.min(v, D[i + w] + A); if (x < w - 1) v = Math.min(v, D[i + w + 1] + B); if (x > 0) v = Math.min(v, D[i + w - 1] + B); }
    D[i] = v;
  }
  return D;
}
