// 修圖嘅面板：顏色、色板、步驟紀錄、導覽。每個面板 = { el, sync() }，editor.js 喺有改動時叫 sync()。
import { T, el, clamp } from '../common.js';

// ---------- 顏色轉換 ----------
export function hexToRgb(h) { const n = parseInt(h.slice(1), 16); return [n >> 16 & 255, n >> 8 & 255, n & 255]; }
export function rgbToHex(r, g, b) { return '#' + [r, g, b].map(v => clamp(Math.round(v), 0, 255).toString(16).padStart(2, '0')).join(''); }
function rgbToHsv(r, g, b) {
  r /= 255; g /= 255; b /= 255;
  const mx = Math.max(r, g, b), mn = Math.min(r, g, b), d = mx - mn;
  let h = 0;
  if (d) h = mx === r ? ((g - b) / d) % 6 : mx === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return [(h * 60 + 360) % 360, mx ? d / mx : 0, mx];
}
function hsvToRgb(h, s, v) {
  const c = v * s, x = c * (1 - Math.abs((h / 60) % 2 - 1)), m = v - c;
  const [r, g, b] = h < 60 ? [c, x, 0] : h < 120 ? [x, c, 0] : h < 180 ? [0, c, x] : h < 240 ? [0, x, c] : h < 300 ? [x, 0, c] : [c, 0, x];
  return [(r + m) * 255, (g + m) * 255, (b + m) * 255];
}

/** 拖曳 helper：撳落同拖動都叫 fn(x, y)（相對元素，0-1）。 */
function scrub(target, fn, done) {
  target.addEventListener('pointerdown', e => {
    target.setPointerCapture(e.pointerId);
    const at = ev => { const r = target.getBoundingClientRect(); fn(clamp((ev.clientX - r.left) / r.width, 0, 1), clamp((ev.clientY - r.top) / r.height, 0, 1), ev); };
    at(e);
    target.onpointermove = at;
    target.onpointerup = () => { target.onpointermove = target.onpointerup = null; done && done(); };
  });
}

// ---------- 顏色 ----------
export function colorPanel(api) {
  const root = el('div', 'pcolor');
  root.innerHTML =
    '<div class="crow"><div class="cchips"><button type="button" class="cchip bg"></button><button type="button" class="cchip fg"></button></div>' +
    '<div class="cfields"><label><span>#</span><input class="chex" spellcheck="false" maxlength="7"></label>' +
    '<label><span>R</span><input class="cr" type="number" min="0" max="255"></label><label><span>G</span><input class="cg" type="number" min="0" max="255"></label><label><span>B</span><input class="cb" type="number" min="0" max="255"></label></div></div>' +
    '<div class="csv"><canvas></canvas><i></i></div><div class="chue"><canvas></canvas><i></i></div>';
  const [chipBg, chipFg] = [root.querySelector('.cchip.bg'), root.querySelector('.cchip.fg')];
  const sv = root.querySelector('.csv'), svC = sv.querySelector('canvas'), svDot = sv.querySelector('i');
  const hue = root.querySelector('.chue'), hueC = hue.querySelector('canvas'), hueDot = hue.querySelector('i');
  const hex = root.querySelector('.chex'), rI = root.querySelector('.cr'), gI = root.querySelector('.cg'), bI = root.querySelector('.cb');
  chipFg.title = api.labels ? api.labels.fg : T('ph_fg'); chipBg.title = api.labels ? api.labels.bg : T('ph_bg');
  let which = 'fg', hsv = [0, 0, 0];
  const cur = () => which === 'fg' ? api.fg : api.bg;
  chipFg.onclick = () => { if (which === 'fg') api.pickNative('fg'); which = 'fg'; sync(); };
  chipBg.onclick = () => { if (which === 'bg') api.pickNative('bg'); which = 'bg'; sync(); };
  function drawSv() {
    const w = svC.width = sv.clientWidth || 220, h = svC.height = sv.clientHeight || 120, g = svC.getContext('2d');
    const [r, gg, b] = hsvToRgb(hsv[0], 1, 1);
    g.fillStyle = rgbToHex(r, gg, b); g.fillRect(0, 0, w, h);
    const wh = g.createLinearGradient(0, 0, w, 0); wh.addColorStop(0, '#fff'); wh.addColorStop(1, 'rgba(255,255,255,0)'); g.fillStyle = wh; g.fillRect(0, 0, w, h);
    const bk = g.createLinearGradient(0, 0, 0, h); bk.addColorStop(0, 'rgba(0,0,0,0)'); bk.addColorStop(1, '#000'); g.fillStyle = bk; g.fillRect(0, 0, w, h);
    svDot.style.left = hsv[1] * 100 + '%'; svDot.style.top = (1 - hsv[2]) * 100 + '%';
    const hw = hueC.width = hue.clientWidth || 220, hh = hueC.height = hue.clientHeight || 12, hg = hueC.getContext('2d');
    const gr = hg.createLinearGradient(0, 0, hw, 0);
    for (let i = 0; i <= 6; i++) { const [a, b2, c] = hsvToRgb(i * 60 % 360, 1, 1); gr.addColorStop(i / 6, rgbToHex(a, b2, c)); }
    hg.fillStyle = gr; hg.fillRect(0, 0, hw, hh);
    hueDot.style.left = hsv[0] / 360 * 100 + '%';
  }
  function set(hexv, keepHsv) { api.setColor(which, hexv); if (!keepHsv) hsv = rgbToHsv(...hexToRgb(hexv)); fields(); drawSv(); }
  const fromHsv = () => set(rgbToHex(...hsvToRgb(...hsv)), true);
  scrub(sv, (x, y) => { hsv[1] = x; hsv[2] = 1 - y; fromHsv(); }, () => api.used(cur()));
  scrub(hue, x => { hsv[0] = Math.min(359.9, x * 360); fromHsv(); }, () => api.used(cur()));
  function fields() {
    const c = cur(), [r, g, b] = hexToRgb(c);
    if (document.activeElement !== hex) hex.value = c.slice(1);
    rI.value = r; gI.value = g; bI.value = b;
    chipFg.style.background = api.fg; chipBg.style.background = api.bg;
    chipFg.classList.toggle('none', !!api.noneFg); chipBg.classList.toggle('none', !!api.noneBg);
    chipFg.classList.toggle('on', which === 'fg'); chipBg.classList.toggle('on', which === 'bg');
  }
  hex.oninput = () => { const v = hex.value.replace(/[^0-9a-f]/gi, ''); if (v.length === 6) set('#' + v.toLowerCase()); };
  hex.onchange = () => api.used(cur());
  for (const i of [rI, gI, bI]) i.onchange = () => { set(rgbToHex(+rI.value, +gI.value, +bI.value)); api.used(cur()); };
  for (const i of root.querySelectorAll('input')) i.addEventListener('keydown', e => e.stopPropagation());
  function sync() {
    const c = cur(), h = rgbToHsv(...hexToRgb(c));
    if (rgbToHex(...hsvToRgb(...hsv)) !== c) hsv = h[1] === 0 || h[2] === 0 ? [hsv[0], h[1], h[2]] : h;   // 灰色唔好跳色相
    fields(); drawSv();
  }
  new ResizeObserver(() => root.isConnected && drawSv()).observe(sv);
  return { el: root, sync };
}

// ---------- 色板 ----------
const BASIC = ['#000000', '#404040', '#808080', '#bfbfbf', '#ffffff', '#ff0000', '#ff8000', '#ffff00', '#80ff00', '#00ff00', '#00ff80', '#00ffff', '#0080ff', '#0000ff', '#8000ff', '#ff00ff', '#ff0080',
  '#7f0000', '#7f4000', '#7f7f00', '#007f00', '#007f7f', '#00007f', '#7f007f', '#f4cccc', '#fce5cd', '#fff2cc', '#d9ead3', '#d0e0e3', '#cfe2f3', '#d9d2e9', '#ead1dc'];
export function swatchesPanel(api) {
  const root = el('div', 'pswatch');
  const recentT = el('div', 'stitle', T('ph_recentcolors')), recent = el('div', 'sgrid');
  const basicT = el('div', 'stitle', T('ph_basiccolors')), basic = el('div', 'sgrid');
  root.append(recentT, recent, basicT, basic);
  const cell = c => { const b = el('button', 'scell'); b.type = 'button'; b.style.background = c; b.title = c + '\n' + T('ph_swatchtip'); b.onclick = e => api.setColor(e.altKey ? 'bg' : 'fg', c); return b; };
  for (const c of BASIC) basic.appendChild(cell(c));
  function sync() { recent.textContent = ''; for (const c of api.recent) recent.appendChild(cell(c)); recentT.style.display = recent.style.display = api.recent.length ? '' : 'none'; }
  return { el: root, sync };
}

// ---------- 步驟紀錄 ----------
export function historyPanel(api) {
  const root = el('div', 'phist');
  function sync() {
    root.textContent = '';
    const d = api.doc; if (!d) return;
    const rows = [T('ph_hist_open'), ...d.undoStack.map(e => e.label), ...[...d.redoStack].reverse().map(e => e.label)];
    const cur = d.undoStack.length;
    rows.forEach((label, i) => {
      const r = el('div', 'hrowi' + (i === cur ? ' on' : '') + (i > cur ? ' future' : ''), label);
      r.onclick = () => api.historyGo(i);
      root.appendChild(r);
    });
    const on = root.querySelector('.on'); on && on.scrollIntoView({ block: 'nearest' });
  }
  return { el: root, sync };
}

// ---------- 導覽 ----------
export function navigatorPanel(api) {
  const root = el('div', 'pnav');
  const box = el('div', 'nbox'), c = el('canvas'), frame = el('i');
  box.append(c, frame);
  const row = el('div', 'nrow');
  const pct = el('span', 'npct');
  const out = el('button', 'mini', '−'), inn = el('button', 'mini', '+'), range = el('input');
  out.type = inn.type = 'button';
  range.type = 'range'; range.min = 0; range.max = 1000;
  const toZ = v => 0.02 * Math.pow(32 / 0.02, v / 1000), fromZ = z => Math.log(z / 0.02) / Math.log(32 / 0.02) * 1000;
  range.oninput = () => api.zoomTo(toZ(+range.value));
  out.onclick = () => api.zoomTo(api.view.zoom / 1.25); inn.onclick = () => api.zoomTo(api.view.zoom * 1.25);
  row.append(pct, out, range, inn);
  root.append(box, row);
  let s = 1, ox = 0, oy = 0;
  scrub(box, (x, y) => {
    const d = api.doc; if (!d) return;
    const r = box.getBoundingClientRect();
    api.centerOn((x * r.width - ox) / s, (y * r.height - oy) / s);
  });
  function sync() {
    const d = api.doc; if (!d || !root.isConnected) return;
    const w = box.clientWidth || 240, h = box.clientHeight || 150;
    c.width = w; c.height = h;
    s = Math.min(w / d.w, h / d.h); ox = (w - d.w * s) / 2; oy = (h - d.h * s) / 2;
    const g = c.getContext('2d');
    g.fillStyle = '#fff'; g.fillRect(ox, oy, d.w * s, d.h * s);
    g.imageSmoothingQuality = 'high';
    g.drawImage(api.comp(), ox, oy, d.w * s, d.h * s);
    const v = api.view, vw = api.size();
    const fx = ox + (-v.ox / v.zoom) * s, fy = oy + (-v.oy / v.zoom) * s, fw = vw.W / v.zoom * s, fh = vw.H / v.zoom * s;
    const x1 = clamp(fx, 0, w), y1 = clamp(fy, 0, h), x2 = clamp(fx + fw, 0, w), y2 = clamp(fy + fh, 0, h);
    Object.assign(frame.style, { left: x1 + 'px', top: y1 + 'px', width: Math.max(0, x2 - x1) + 'px', height: Math.max(0, y2 - y1) + 'px' });
    pct.textContent = Math.round(v.zoom * 1000) / 10 + '%';
    if (document.activeElement !== range) range.value = fromZ(v.zoom);
  }
  new ResizeObserver(() => sync()).observe(box);
  return { el: root, sync };
}
