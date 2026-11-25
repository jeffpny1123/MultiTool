// 向量繪圖編輯器：工具、選取框（縮放 / 旋轉）、鋼筆、形狀、文字、面板、選單、快捷鍵。
import { T, $, el, toast, clamp, popupMenu, dialog, field, numInput } from '../common.js';
import { paper, VectorDoc } from './vdoc.js';
import { ICON } from '../photo/icons.js';
import { VICON } from './vicons.js';
import { createDock } from '../dock.js';
import { colorPanel, swatchesPanel, historyPanel } from '../photo/panels.js';
import { propertiesPanel, alignPanel, shapeOpsPanel, objectsPanel } from './vpanels.js';

// ---------- 狀態 ----------
let scope = null, doc = null, ui = null;
let cv, ov, og, W = 0, H = 0;           // paper canvas + 疊喺上面畫選取框嘅 2D canvas
let tool = 'select', prevTool = null, spaceHeld = false;
let sel = [];                            // 揀咗嘅物件（art 圖層第一層）
let drag = null, hover = null;
let pen = null;                          // 鋼筆畫緊嘅路徑
let clip = null;                         // 內部剪貼簿（JSON 陣列）
let editing = null;                      // 改緊嘅文字 { item, ta }
const style = { fill: '#ffffff', stroke: '#000000', strokeWidth: 1, active: 'fill', fontFamily: 'sans-serif', fontSize: 36, poly: 6, star: 5, inner: 0.5, radius: 0 };
const recentColors = [];
let dock = null, P = {};                 // 面板

const TOOL_KEY = { select: 'V', direct: 'A', pen: 'P', text: 'T', line: '\\', rect: 'M', ellipse: 'L', polygon: '', star: '', pencil: 'N', picker: 'I', hand: 'H', zoom: 'Z' };
const GROUPS = [['select'], ['direct'], ['pen'], ['text'], ['line'], ['rect', 'ellipse', 'polygon', 'star'], ['pencil'], '-', ['picker'], ['hand'], ['zoom']];
const groupPick = {};
const groupOf = id => GROUPS.find(g => g !== '-' && g.includes(id));
const SHAPES = ['rect', 'ellipse', 'polygon', 'star', 'line'];
const ICONS = () => ({ ...ICON, ...VICON });

// ---------- 開始 ----------
export function mountVector(callbacks) {
  ui = callbacks;
  const root = $('veditor');
  root.innerHTML =
    '<div id="vopts" class="opts"></div>' +
    '<div id="vwork" class="vwork"><div id="vcenter" class="vcenter"><div class="doctabs"><div class="dtab on"><span id="vdtName"></span><button type="button" class="mini" id="vdtClose">×</button></div></div>' +
    '<div id="vstage" class="vstage"><canvas id="vcv" resize></canvas><canvas id="vov"></canvas></div></div></div>' +
    '<div id="vstatus" class="vstatus"><span id="vsZoom"></span><span id="vsSize"></span><span id="vsPos"></span><span class="grow"></span><span id="vsHint"></span></div>';
  cv = $('vcv'); ov = $('vov'); og = ov.getContext('2d');
  $('vdtClose').onclick = () => ui.goHome(); $('vdtClose').title = T('ph_close');
  scope = new paper.PaperScope();
  scope.setup(cv);
  scope.settings.handleSize = 7;
  scope.settings.hitTolerance = 4;
  scope.view.autoUpdate = true;

  const api = {
    get doc() { return doc; }, get fg() { return style.fill || '#ffffff'; }, get bg() { return style.stroke || '#000000'; }, get recent() { return recentColors; },
    labels: { fg: T('vc_fill'), bg: T('vc_stroke') },
    get noneFg() { return !style.fill; }, get noneBg() { return !style.stroke; },
    setColor(which, hex) { setStyleColor(which === 'bg' ? 'stroke' : 'fill', hex, true); },
    used(c) { usedColor(c); if (sel.length) doc.commit(T('vc_color')); },
    pickNative(which) { pickNative(which === 'bg' ? 'stroke' : 'fill'); },
    historyGo,
    get sel() { return sel; }, get style() { return style; }, get scope() { return scope; },
    commit: l => { doc.commit(l); refresh(); }, refresh: () => refresh(), select: (items, add) => { setSel(add ? [...new Set([...sel, ...items])] : items); },
    setStyle: (k, v) => setStyle(k, v), artboardSize, T,
  };
  P.color = colorPanel(api); P.swatches = swatchesPanel(api); P.history = historyPanel(api);
  P.props = propertiesPanel(api); P.align = alignPanel(api); P.ops = shapeOpsPanel(api); P.objects = objectsPanel(api);
  const toolsEl = el('div', 'vtools');
  dock = createDock({
    work: $('vwork'), stage: $('vcenter'), T, key: 'rabi-design-vector-layout-v1',
    panels: {
      tools: { title: T('ph_p_tools'), icon: ICON.brush, el: toolsEl, compact: true },
      props: { title: T('vp_props'), icon: VICON.props, el: P.props.el, width: 270 },
      color: { title: T('ph_p_color'), icon: ICON.palette, el: P.color.el },
      swatches: { title: T('ph_p_swatches'), icon: ICON.grid, el: P.swatches.el },
      align: { title: T('vp_align'), icon: VICON.alignCh, el: P.align.el },
      ops: { title: T('vp_ops'), icon: VICON.unite, el: P.ops.el },
      objects: { title: T('vp_objects'), icon: ICON.layers, el: P.objects.el, weight: 2 },
      history: { title: T('ph_p_history'), icon: ICON.history, el: P.history.el, weight: 1 },
    },
    layout: {
      left: { w: 52, groups: [{ tabs: ['tools'], active: 'tools' }] },
      right: { w: 280, groups: [{ tabs: ['props'], active: 'props' }, { tabs: ['color', 'swatches'], active: 'color' }, { tabs: ['align', 'ops'], active: 'align' }, { tabs: ['objects', 'history'], active: 'objects' }] },
      floats: [], hidden: [],
    },
    onChange: () => syncPanels(),
  });
  dock.render();
  buildTools(toolsEl);

  const stage = $('vstage');
  new ResizeObserver(resize).observe(stage);
  stage.addEventListener('pointerdown', onDown);
  stage.addEventListener('pointermove', onMove);
  stage.addEventListener('pointerup', onUp);
  stage.addEventListener('pointercancel', onUp);
  stage.addEventListener('pointerleave', () => { hover = null; showPos(); drawOverlay(); });
  stage.addEventListener('dblclick', onDbl);
  stage.addEventListener('wheel', onWheel, { passive: false });
  stage.addEventListener('contextmenu', e => { e.preventDefault(); if (doc && sel.length) popupMenu(objectMenu(), e.clientX, e.clientY); });
  addEventListener('keydown', onKey);
  addEventListener('keyup', e => { if (e.code === 'Space' && spaceHeld) { spaceHeld = false; if (prevTool) { setTool(prevTool); prevTool = null; } } });
  addEventListener('paste', onPaste);
  setTool('select');
}

const shown = () => $('veditor').classList.contains('show');
export const vHasDoc = () => !!doc;
export const vIsDirty = () => !!(doc && doc.dirty);
export const vMarkSaved = () => { if (doc) doc.dirty = false; };
export const vCurrentDoc = () => doc;
export const vScope = () => scope;
let docTitle = '';
export function vSetDocTitle(n) { docTitle = n; status(); }

export function vSetDoc(d) {
  finishPen(); stopText();
  doc = d; doc.dirty = false; sel = [];
  doc.onChange = kind => { if (kind === 'restore') { sel = []; } refresh(); };
  setTimeout(() => { fit(1); refresh(); }, 0);
  refresh();
}
export function vBlank(w, h) { return new VectorDoc(scope, w, h); }

// ---------- 面板 / 畫面更新 ----------
function syncPanels() { for (const k in P) try { P[k].sync(); } catch (e) { console.error(e); } }
let rT = 0;
function refresh() {
  if (rT) return;
  rT = requestAnimationFrame(() => { rT = 0; sel = sel.filter(i => i.parent && i.layer === (doc && doc.art)); syncPanels(); drawOverlay(); status(); });
}
function status() {
  if (!doc) return;
  const z = Math.round(scope.view.zoom * 1000) / 10 + '%';
  $('vsZoom').textContent = z; $('vsSize').textContent = doc.w + ' × ' + doc.h + ' px';
  $('vdtName').textContent = docTitle + ' @ ' + z + ' (RGB/' + T('vc_preview') + ')';
}
function showPos() { $('vsPos').textContent = hover ? Math.round(hover.x) + ', ' + Math.round(hover.y) : ''; }

// ---------- 顏色 ----------
function setStyleColor(which, hex, live) {
  style[which] = hex;
  style.active = which;
  for (const it of sel) applyColor(it, which, hex);
  if (!live && sel.length) doc.commit(T('vc_color'));
  syncColors(); refresh();
}
function applyColor(it, which, hex) {
  if (it instanceof scope.Group) { it.children.forEach(c => applyColor(c, which, hex)); return; }
  if (it instanceof scope.Raster) return;
  if (which === 'fill') it.fillColor = hex; else { it.strokeColor = hex; if (hex && !it.strokeWidth) it.strokeWidth = style.strokeWidth || 1; }
}
function setStyle(k, v) { style[k] = v; syncColors(); }
function usedColor(c) {
  if (!c) return;
  const i = recentColors.indexOf(c); if (i === 0) return; if (i > 0) recentColors.splice(i, 1);
  recentColors.unshift(c); if (recentColors.length > 16) recentColors.length = 16; P.swatches && P.swatches.sync();
}
function syncColors() {
  const f = $('vcFill'), s = $('vcStroke');
  if (f) {
    f.style.background = style.fill || ''; f.classList.toggle('none', !style.fill);
    s.style.borderColor = style.stroke || 'transparent'; s.classList.toggle('none', !style.stroke);
    f.classList.toggle('on', style.active === 'fill'); s.classList.toggle('on', style.active === 'stroke');
    f.style.zIndex = style.active === 'fill' ? 2 : 1; s.style.zIndex = style.active === 'stroke' ? 2 : 1;
  }
  P.color && P.color.sync(); P.props && P.props.sync();
}
let nativeIn = null;
function pickNative(which) {
  if (!nativeIn) { nativeIn = el('input'); nativeIn.type = 'color'; nativeIn.style.cssText = 'position:fixed;left:-50px;top:0;opacity:0'; document.body.appendChild(nativeIn); }
  nativeIn.value = style[which] || '#000000';
  nativeIn.oninput = () => setStyleColor(which, nativeIn.value, true);
  nativeIn.onchange = () => { usedColor(nativeIn.value); if (sel.length) doc.commit(T('vc_color')); };
  nativeIn.click();
}
function swapFillStroke() {
  [style.fill, style.stroke] = [style.stroke, style.fill];
  for (const it of sel) { applyColor(it, 'fill', style.fill); applyColor(it, 'stroke', style.stroke); }
  if (sel.length) doc.commit(T('vc_swap'));
  syncColors(); refresh();
}
function defaultColors() {
  style.fill = '#ffffff'; style.stroke = '#000000';
  for (const it of sel) { applyColor(it, 'fill', style.fill); applyColor(it, 'stroke', style.stroke); }
  if (sel.length) doc.commit(T('vc_color'));
  syncColors(); refresh();
}
function setNone() { setStyleColor(style.active, null, false); }

// ---------- 工具列 ----------
const toolTip = id => T('vt_' + id) + (TOOL_KEY[id] ? '  (' + TOOL_KEY[id] + ')' : '');
function buildTools(t) {
  t.textContent = '';
  const I = ICONS();
  for (const g of GROUPS) {
    if (g === '-') { t.appendChild(el('div', 'tsep')); continue; }
    const key = g.join(','), id = groupPick[key] || g[0];
    const b = el('button', 'tool' + (g.length > 1 ? ' multi' : '')); b.type = 'button'; b.dataset.group = key; b.dataset.tool = id;
    b.innerHTML = I['v_' + id] || I[id] || ''; b.title = toolTip(id);
    b.onclick = () => setTool(b.dataset.tool);
    if (g.length > 1) {
      let hold = 0;
      const fly = () => { const r = b.getBoundingClientRect(); popupMenu(g.map(x => ({ label: T('vt_' + x), key: TOOL_KEY[x], check: x === tool, run: () => setTool(x) })), r.right + 4, r.top); };
      b.onpointerdown = e => { if (e.button === 0) hold = setTimeout(fly, 450); };
      b.onpointerup = b.onpointerleave = () => clearTimeout(hold);
      b.oncontextmenu = e => { e.preventDefault(); fly(); };
    }
    t.appendChild(b);
  }
  t.appendChild(el('div', 'tsep'));
  const fs = el('div', 'fillstroke');
  fs.innerHTML = '<button type="button" id="vcFill" class="fsq fill"></button><button type="button" id="vcStroke" class="fsq stroke"></button>' +
    '<button type="button" id="vcSwap" class="mini"></button><button type="button" id="vcDef" class="mini">◩</button><button type="button" id="vcNone" class="mini">⊘</button>';
  t.appendChild(fs);
  $('vcSwap').innerHTML = ICON.swap;
  $('vcFill').title = T('vc_fill') + '  (X)'; $('vcStroke').title = T('vc_stroke') + '  (X)';
  $('vcSwap').title = T('vc_swap') + '  (Shift+X)'; $('vcDef').title = T('vc_default') + '  (D)'; $('vcNone').title = T('vc_none') + '  (/)';
  $('vcFill').onclick = () => { if (style.active === 'fill') pickNative('fill'); style.active = 'fill'; syncColors(); };
  $('vcStroke').onclick = () => { if (style.active === 'stroke') pickNative('stroke'); style.active = 'stroke'; syncColors(); };
  $('vcSwap').onclick = swapFillStroke; $('vcDef').onclick = defaultColors; $('vcNone').onclick = setNone;
  syncColors();
}
export function setTool(id) {
  if (tool === 'pen' && id !== 'pen') finishPen();
  stopText();
  tool = id;
  const g = groupOf(id), I = ICONS();
  if (g) groupPick[g.join(',')] = id;
  document.querySelectorAll('.vtools .tool').forEach(b => {
    if (g && b.dataset.group === g.join(',') && b.dataset.tool !== id) { b.dataset.tool = id; b.innerHTML = I['v_' + id] || I[id] || ''; b.title = toolTip(id); }
    b.classList.toggle('on', b.dataset.tool === id);
  });
  if (doc) { doc.art.getItems({ selected: true }).forEach(i => i.selected = false); if (id === 'direct') sel.forEach(i => i.selected = true); }
  $('vstage').dataset.tool = id;
  $('vsHint').textContent = T('vh_' + id);
  buildOpts(); drawOverlay();
}

// ---------- 選項列 ----------
function numOpt(label, get, set, min, max, step = 1, suffix = '') {
  const w = el('label', 'onum'); w.appendChild(el('span', '', label));
  const i = numInput(get(), min, max, step); i.onchange = () => { set(clamp(+i.value, min, max)); i.value = get(); };
  i.addEventListener('keydown', e => e.stopPropagation());
  w.appendChild(i); if (suffix) w.appendChild(el('span', 'osuf', suffix)); return w;
}
const FONTS = ['sans-serif', 'serif', 'monospace', 'Segoe UI', 'Arial', 'Times New Roman', 'Georgia', 'Impact', 'Microsoft JhengHei', 'Microsoft YaHei', 'PMingLiU', 'Yu Gothic', 'Malgun Gothic'];
function fontSelect() {
  const s = el('select', 'osel');
  for (const f of FONTS) { const o = el('option', '', f); o.value = f; o.style.fontFamily = f; s.appendChild(o); }
  s.value = style.fontFamily;
  s.onchange = () => { style.fontFamily = s.value; const ts = sel.filter(i => i instanceof scope.PointText); ts.forEach(t => t.fontFamily = s.value); if (ts.length) doc.commit(T('vo_font')); refresh(); };
  return s;
}
function buildOpts() {
  const o = $('vopts'); o.textContent = '';
  o.appendChild(el('span', 'oname', T('vt_' + tool)));
  o.appendChild(numOpt(T('vo_strokew'), () => style.strokeWidth, v => { style.strokeWidth = v; sel.forEach(i => { if (!(i instanceof scope.Group)) i.strokeWidth = v; }); if (sel.length) doc.commit(T('vo_strokew')); refresh(); }, 0, 500, 0.5, 'px'));
  if (tool === 'rect') o.appendChild(numOpt(T('vo_radius'), () => style.radius, v => style.radius = v, 0, 2000, 1, 'px'));
  if (tool === 'polygon') o.appendChild(numOpt(T('vo_sides'), () => style.poly, v => style.poly = Math.round(v), 3, 64));
  if (tool === 'star') { o.appendChild(numOpt(T('vo_points'), () => style.star, v => style.star = Math.round(v), 3, 64)); o.appendChild(numOpt(T('vo_inner'), () => Math.round(style.inner * 100), v => style.inner = v / 100, 5, 95, 1, '%')); }
  if (tool === 'text' || sel.some(i => i instanceof scope.PointText)) {
    o.appendChild(fontSelect());
    o.appendChild(numOpt(T('vo_fontsize'), () => style.fontSize, v => { style.fontSize = v; const ts = sel.filter(i => i instanceof scope.PointText); ts.forEach(t => t.fontSize = v); if (ts.length) doc.commit(T('vo_fontsize')); refresh(); }, 1, 2000, 1, 'px'));
  }
  o.appendChild(el('span', 'ohint', T('vh_' + tool)));
}

// ---------- 縮放 / 平移 ----------
function resize() {
  const r = $('vstage').getBoundingClientRect(), d = devicePixelRatio || 1;
  const first = W === 0;
  W = r.width; H = r.height;
  scope.view.viewSize = new scope.Size(W, H);
  ov.width = Math.round(W * d); ov.height = Math.round(H * d); ov.style.width = W + 'px'; ov.style.height = H + 'px';
  if (first && doc) fit(1);
  drawOverlay(); status();
}
function fit(max = 64) {
  if (!doc || !W) return;
  const z = clamp(Math.min((W - 60) / doc.w, (H - 60) / doc.h, max), 0.02, 64);
  scope.view.zoom = z; scope.view.center = new scope.Point(doc.w / 2, doc.h / 2);
  drawOverlay(); status(); syncPanels();
}
function zoomAt(z, sx, sy) {
  z = clamp(z, 0.02, 64);
  const v = scope.view, p = v.viewToProject(new scope.Point(sx, sy));
  v.zoom = z;
  const p2 = v.viewToProject(new scope.Point(sx, sy));
  v.center = v.center.add(p.subtract(p2));
  drawOverlay(); status(); placeText();
}
const zoomBy = f => zoomAt(scope.view.zoom * f, W / 2, H / 2);
const zoomTo = z => zoomAt(z, W / 2, H / 2);
function panBy(dx, dy) { const v = scope.view; v.center = v.center.subtract(new scope.Point(dx / v.zoom, dy / v.zoom)); drawOverlay(); placeText(); }
function onWheel(e) {
  if (!doc) return;
  e.preventDefault();
  const r = cv.getBoundingClientRect();
  if (e.ctrlKey || e.altKey) zoomAt(scope.view.zoom * Math.pow(1.0015, -e.deltaY), e.clientX - r.left, e.clientY - r.top);
  else if (e.shiftKey) panBy(-e.deltaY, 0);
  else panBy(-e.deltaX, -e.deltaY);
}

// ---------- 座標 ----------
function evPt(e) { const r = cv.getBoundingClientRect(); return { view: new scope.Point(e.clientX - r.left, e.clientY - r.top) }; }
const toDoc = vp => scope.view.viewToProject(vp);
const toView = p => scope.view.projectToView(p);
const tol = () => 5 / scope.view.zoom;
/** 一件嘢喺 art 圖層第一層嘅祖先（揀嘢 / 搬嘢都係用第一層）。 */
function topOf(item) { while (item && item.parent && item.parent !== doc.art) item = item.parent; return item && item.parent === doc.art ? item : null; }
function hitTop(p) {
  const h = doc.art.hitTest(p, { fill: true, stroke: true, segments: true, tolerance: tol() + 2 / scope.view.zoom });
  return h ? topOf(h.item) : null;
}
function selBounds() { if (!sel.length) return null; let b = sel[0].bounds.clone(); for (const i of sel.slice(1)) b = b.unite(i.bounds); return b; }
function setSel(items) { sel = items.filter(Boolean); if (tool === 'direct') { doc.art.getItems({ selected: true }).forEach(i => i.selected = false); sel.forEach(i => i.selected = true); } buildOpts(); refresh(); }

// 選取框嘅 8 個把手（view 座標）
function handles(b) {
  const pts = { nw: b.topLeft, n: b.topCenter, ne: b.topRight, e: b.rightCenter, se: b.bottomRight, s: b.bottomCenter, sw: b.bottomLeft, w: b.leftCenter };
  const out = {}; for (const k in pts) out[k] = toView(pts[k]); return out;
}
const OPP = { nw: 'se', n: 's', ne: 'sw', e: 'w', se: 'nw', s: 'n', sw: 'ne', w: 'e' };
const CURSOR = { nw: 'nwse-resize', se: 'nwse-resize', ne: 'nesw-resize', sw: 'nesw-resize', n: 'ns-resize', s: 'ns-resize', e: 'ew-resize', w: 'ew-resize' };
function handleAt(vp) {
  const b = selBounds(); if (!b || tool !== 'select') return null;
  const hs = handles(b);
  for (const k in hs) if (hs[k].getDistance(vp) <= 6) return { kind: 'scale', h: k };
  for (const k of ['nw', 'ne', 'se', 'sw']) { const d = hs[k].getDistance(vp); if (d > 6 && d <= 20) { const c = toView(b.center); if (vp.getDistance(c) > hs[k].getDistance(c)) return { kind: 'rotate', h: k }; } }
  return null;
}

// ---------- 滑鼠 ----------
function onDown(e) {
  if (!doc) return;
  if (editing) { if (e.target === editing.ta) return; stopText(); }
  $('vstage').setPointerCapture(e.pointerId);
  const { view: vp } = evPt(e), p = toDoc(vp);
  if (e.button === 1 || tool === 'hand') { drag = { kind: 'pan', last: vp }; $('vstage').classList.add('panning'); return; }
  if (e.button !== 0) return;
  switch (tool) {
    case 'zoom': return zoomAt(scope.view.zoom * (e.altKey ? 0.8 : 1.25), vp.x, vp.y);
    case 'select': return selectDown(e, vp, p);
    case 'direct': return directDown(e, p);
    case 'pen': return penDown(e, p);
    case 'pencil': drag = { kind: 'pencil', path: new scope.Path({ strokeColor: style.stroke || '#000000', strokeWidth: style.strokeWidth || 1, strokeCap: 'round', strokeJoin: 'round', parent: doc.art }) }; drag.path.add(p); return;
    case 'text': e.preventDefault(); return textAt(p);
    case 'picker': return pickAt(p);
    default: if (SHAPES.includes(tool)) drag = { kind: 'shape', a: p, b: p, item: null };
  }
}
function onMove(e) {
  if (!doc) return;
  const { view: vp } = evPt(e), p = toDoc(vp);
  hover = p; showPos();
  if (!drag) { hoverCursor(vp, p); if (tool === 'pen' && pen) drawOverlay(); return; }
  switch (drag.kind) {
    case 'pan': panBy(vp.x - drag.last.x, vp.y - drag.last.y); drag.last = vp; return;
    case 'move': return moveTo(p, e);
    case 'scale': return scaleTo(p, e);
    case 'rotate': return rotateTo(p, e);
    case 'marquee': drag.b = p; drawOverlay(); return;
    case 'seg': return segTo(p, e);
    case 'handle': return handleTo(p, e);
    case 'penhandle': return penHandle(p, e);
    case 'pencil': drag.path.add(p); return;
    case 'shape': drag.b = p; return shapeTo(e);
  }
}
function onUp(e) {
  if (!drag) return;
  const d = drag; drag = null;
  switch (d.kind) {
    case 'pan': $('vstage').classList.remove('panning'); return;
    case 'move': if (d.moved) doc.commit(T(d.copy ? 'vc_dupmove' : 'vt_move')); refresh(); return;
    case 'scale': doc.commit(T('vc_scale')); refresh(); return;
    case 'rotate': doc.commit(T('vc_rotate')); refresh(); return;
    case 'marquee': return marqueeEnd(d, e);
    case 'seg': case 'handle': if (d.moved) doc.commit(T('vt_direct')); refresh(); return;
    case 'penhandle': refresh(); return;
    case 'pencil': {
      if (d.path.segments.length < 2) { d.path.remove(); return; }
      d.path.simplify(2.5); d.path.fillColor = null;
      setSel([d.path]); doc.commit(T('vt_pencil')); return;
    }
    case 'shape': {
      if (!d.item || d.b.getDistance(d.a) * scope.view.zoom < 3) { d.item && d.item.remove(); if (tool !== 'line') shapeDialog(d.a); return; }
      setSel([d.item]); doc.commit(T('vt_' + tool)); return;
    }
  }
}
function onDbl(e) {
  if (!doc) return;
  const p = toDoc(evPt(e).view);
  if (tool === 'select' || tool === 'direct') {
    const h = doc.art.hitTest(p, { fill: true, stroke: true, tolerance: tol() });
    const t = h && (h.item instanceof scope.PointText ? h.item : null);
    if (t) { setTool('text'); editText(t); }
  }
}
function hoverCursor(vp, p) {
  const st = $('vstage');
  let c = '';
  if (tool === 'select') { const h = handleAt(vp); c = h ? (h.kind === 'scale' ? CURSOR[h.h] : 'alias') : hitTop(p) ? 'move' : ''; }
  st.style.cursor = c;
}

// ---------- 選取工具 ----------
function selectDown(e, vp, p) {
  const h = handleAt(vp);
  if (h) {
    const b = selBounds();
    if (h.kind === 'scale') {
      const anchor = b[{ nw: 'topLeft', n: 'topCenter', ne: 'topRight', e: 'rightCenter', se: 'bottomRight', s: 'bottomCenter', sw: 'bottomLeft', w: 'leftCenter' }[OPP[h.h]]];
      drag = { kind: 'scale', h: h.h, b, anchor, center: b.center, sx: 1, sy: 1 };
    } else drag = { kind: 'rotate', center: b.center, a0: p.subtract(b.center).angle, last: 0 };
    return;
  }
  const it = hitTop(p);
  if (it) {
    if (e.shiftKey) { setSel(sel.includes(it) ? sel.filter(x => x !== it) : [...sel, it]); return; }
    if (!sel.includes(it)) setSel([it]);
    drag = { kind: 'move', a: p, last: new scope.Point(0, 0), moved: false, copy: false, alt: e.altKey };
    return;
  }
  if (!e.shiftKey) setSel([]);
  drag = { kind: 'marquee', a: p, b: p, add: e.shiftKey };
}
function moveTo(p, e) {
  let d = p.subtract(drag.a);
  if (e.shiftKey) d = Math.abs(d.x) > Math.abs(d.y) ? new scope.Point(d.x, 0) : new scope.Point(0, d.y);
  if (!drag.moved && d.length * scope.view.zoom < 2) return;
  if (!drag.moved && drag.alt) { sel = sel.map(i => { const c = i.clone(); c.insertAbove(i); return c; }); drag.copy = true; }
  drag.moved = true;
  const step = d.subtract(drag.last);
  sel.forEach(i => i.translate(step));
  drag.last = d;
  drawOverlay();
}
function scaleTo(p, e) {
  const d = drag, b = d.b, from = e.altKey ? d.center : d.anchor;
  const hx = /e|w/.test(d.h), hy = /n|s/.test(d.h);
  const ref = { x: d.h.includes('e') ? b.right : d.h.includes('w') ? b.left : b.center.x, y: d.h.includes('s') ? b.bottom : d.h.includes('n') ? b.top : b.center.y };
  let sx = hx ? (p.x - from.x) / ((ref.x - from.x) || 1e-6) : 1, sy = hy ? (p.y - from.y) / ((ref.y - from.y) || 1e-6) : 1;
  if (e.shiftKey) { if (hx && hy) { const s = Math.max(Math.abs(sx), Math.abs(sy)); sx = Math.sign(sx || 1) * s; sy = Math.sign(sy || 1) * s; } else if (hx) sy = Math.abs(sx); else sx = Math.abs(sy); }
  const fix = v => Math.abs(v) < 0.001 ? 0.001 * Math.sign(v || 1) : v;
  sx = fix(sx); sy = fix(sy);
  const rx = sx / d.sx, ry = sy / d.sy;
  sel.forEach(i => i.scale(rx, ry, from));
  d.sx = sx; d.sy = sy;
  drawOverlay();
}
function rotateTo(p, e) {
  let a = p.subtract(drag.center).angle - drag.a0;
  if (e.shiftKey) a = Math.round(a / 45) * 45;
  sel.forEach(i => i.rotate(a - drag.last, drag.center));
  drag.last = a;
  drawOverlay();
}
function marqueeEnd(d, e) {
  const r = new scope.Rectangle(d.a, d.b);
  if (r.width * scope.view.zoom < 2 && r.height * scope.view.zoom < 2) { drawOverlay(); return; }
  if (d.direct) {
    // 直接選取：揀框入面嘅錨點
    const hit = [];
    doc.art.getItems({ class: scope.Path }).forEach(path => {
      if (!path.visible || path.locked) return;
      let any = false; path.segments.forEach(sg => { sg.selected = r.contains(sg.point); any = any || sg.selected; });
      if (any) hit.push(topOf(path));
    });
    sel = [...new Set(hit)]; refresh(); return;
  }
  const hit = doc.art.children.filter(i => i.visible && !i.locked && r.intersects(i.bounds));
  setSel(d.add ? [...new Set([...sel, ...hit])] : hit);
}

// ---------- 直接選取：郁錨點 / 把手 ----------
function directDown(e, p) {
  // 先睇揀咗嘅路徑嘅把手
  const h = doc.art.hitTest(p, { segments: true, handles: true, stroke: true, fill: true, curves: true, tolerance: tol() + 1 / scope.view.zoom });
  if (!h) { doc.art.getItems({ selected: true }).forEach(i => i.selected = false); sel = []; drag = { kind: 'marquee', a: p, b: p, add: false, direct: true }; refresh(); return; }
  const path = h.item;
  if (!e.shiftKey && !path.selected) { doc.art.getItems({ selected: true }).forEach(i => i.selected = false); }
  path.selected = true;
  sel = [...new Set([...sel.filter(i => e.shiftKey), topOf(path)])];
  if (h.type === 'segment') {
    if (!e.shiftKey && !h.segment.selected) path.segments.forEach(s => s.selected = false);
    h.segment.selected = true;
    drag = { kind: 'seg', a: p, last: new scope.Point(0, 0), moved: false };
  } else if (h.type === 'handle-in' || h.type === 'handle-out') {
    drag = { kind: 'handle', seg: h.segment, which: h.type === 'handle-in' ? 'handleIn' : 'handleOut', moved: false };
  } else if (h.type === 'curve' || h.type === 'stroke') {
    const c = h.location && h.location.curve;
    path.segments.forEach(s => s.selected = false);
    if (c) { c.segment1.selected = true; c.segment2.selected = true; }
    drag = { kind: 'seg', a: p, last: new scope.Point(0, 0), moved: false };
  } else { path.fullySelected = true; drag = { kind: 'seg', a: p, last: new scope.Point(0, 0), moved: false }; }
  refresh();
}
function segTo(p) {
  const d = p.subtract(drag.a), step = d.subtract(drag.last);
  doc.art.getItems({ class: scope.Path }).forEach(path => path.segments.forEach(s => { if (s.selected) s.point = s.point.add(step); }));
  drag.last = d; drag.moved = true; drawOverlay();
}
function handleTo(p, e) {
  const s = drag.seg, v = p.subtract(s.point);
  s[drag.which] = v;
  // 平滑點：對面把手跟住轉（保持長度）；Alt = 只郁呢邊
  const other = drag.which === 'handleIn' ? 'handleOut' : 'handleIn';
  if (!e.altKey && !s[other].isZero()) s[other] = v.normalize(-s[other].length);
  drag.moved = true; drawOverlay();
}

// ---------- 鋼筆 ----------
function penDown(e, p) {
  if (pen && pen.segments.length >= 2 && toView(pen.firstSegment.point).getDistance(toView(p)) <= 7) {
    pen.closed = true; const done = pen; pen = null; finishPenPath(done); return;
  }
  if (!pen) pen = new scope.Path({ strokeColor: style.stroke, fillColor: style.fill, strokeWidth: style.strokeWidth || 1, parent: doc.art });
  let q = p;
  if (e.shiftKey && pen.segments.length) { const last = pen.lastSegment.point, d = p.subtract(last); d.angle = Math.round(d.angle / 45) * 45; q = last.add(d); }
  pen.add(q);
  pen.selected = true;
  drag = { kind: 'penhandle', seg: pen.lastSegment };
  drawOverlay();
}
function penHandle(p, e) {
  const s = drag.seg, v = p.subtract(s.point);
  s.handleOut = v; if (!e.altKey) s.handleIn = v.multiply(-1);
  drawOverlay();
}
function finishPen() { if (!pen) return; const p = pen; pen = null; finishPenPath(p); }
function finishPenPath(p) {
  p.selected = false;
  if (p.segments.length < 2) { p.remove(); drawOverlay(); return; }
  if (!p.closed) p.fillColor = p.fillColor && p.segments.length > 2 ? p.fillColor : null;
  setSel([p]); doc.commit(T('vt_pen'));
}

// ---------- 形狀 ----------
function shapeTo(e) {
  const d = drag;
  let a = d.a, b = d.b;
  if (tool === 'line') {
    if (e.shiftKey) { const v = b.subtract(a); v.angle = Math.round(v.angle / 45) * 45; b = a.add(v); }
    if (e.altKey) a = d.a.subtract(b.subtract(d.a));
  } else {
    let w = b.x - a.x, h = b.y - a.y;
    if (e.shiftKey) { const s = Math.max(Math.abs(w), Math.abs(h)); w = Math.sign(w || 1) * s; h = Math.sign(h || 1) * s; }
    if (e.altKey) { a = new scope.Point(d.a.x - w, d.a.y - h); b = new scope.Point(d.a.x + w, d.a.y + h); }
    else b = new scope.Point(a.x + w, a.y + h);
  }
  d.item && d.item.remove();
  d.item = makeShape(tool, new scope.Rectangle(a, b), a, b);
  doc.art.addChild(d.item);
}
function makeShape(kind, r, a, b) {
  const st = { fillColor: kind === 'line' ? null : style.fill, strokeColor: kind === 'line' ? (style.stroke || '#000000') : style.stroke, strokeWidth: style.strokeWidth || (kind === 'line' ? 1 : 0), insert: false };
  if (kind === 'line') return new scope.Path.Line({ from: a, to: b, ...st });
  if (kind === 'rect') return new scope.Path.Rectangle({ rectangle: r, radius: Math.min(style.radius, r.width / 2, r.height / 2), ...st });
  if (kind === 'ellipse') return new scope.Path.Ellipse({ rectangle: r, ...st });
  const c = r.center, rad = Math.min(r.width, r.height) / 2;
  let path = kind === 'polygon' ? new scope.Path.RegularPolygon({ center: c, sides: style.poly, radius: rad, ...st })
    : new scope.Path.Star({ center: c, points: style.star, radius1: rad, radius2: rad * style.inner, ...st });
  // 拉成長方形就跟住拉伸
  if (path.bounds.width > 0 && path.bounds.height > 0) path.scale(r.width / path.bounds.width, r.height / path.bounds.height, c);
  return path;
}
function shapeDialog(at) {
  const wi = numInput(100, 1, 100000), hi = numInput(100, 1, 100000);
  const body = el('div', 'grid2'); body.append(field(T('ph_width'), wi), field(T('ph_height'), hi));
  dialog(T('vt_' + tool), body, [[T('cancel')], [T('ok'), () => {
    const w = +wi.value || 100, h = +hi.value || 100;
    const it = makeShape(tool, new scope.Rectangle(at.x, at.y, w, h), at, at.add([w, 0]));
    doc.art.addChild(it); setSel([it]); doc.commit(T('vt_' + tool));
  }, true]]);
}

// ---------- 文字 ----------
function textAt(p) {
  const h = doc.art.hitTest(p, { fill: true, tolerance: tol() });
  if (h && h.item instanceof scope.PointText) { editText(h.item); return; }
  const t = new scope.PointText({ point: p.add([0, style.fontSize * 0.35]), content: '', fillColor: style.fill && style.fill !== '#ffffff' ? style.fill : '#000000', fontFamily: style.fontFamily, fontSize: style.fontSize, parent: doc.art });
  t.data.fresh = true;
  editText(t);
}
function editText(t) {
  stopText();
  const ta = el('textarea', 'vtextedit');
  ta.value = t.content; ta.spellcheck = false;
  $('vstage').appendChild(ta);
  editing = { item: t, ta, before: t.content };
  t.visible = false;
  ta.oninput = () => { placeText(); };
  ta.onkeydown = e => { e.stopPropagation(); if (e.key === 'Escape') { e.preventDefault(); stopText(); } };
  placeText();
  ta.focus(); ta.select();
  setTimeout(() => { if (editing && editing.ta === ta) ta.focus(); }, 0);
}
function placeText() {
  if (!editing) return;
  const { item: t, ta } = editing, z = scope.view.zoom;
  const fs = t.fontSize * z, lines = ta.value.split('\n');
  const top = toView(t.point.subtract([0, t.fontSize * 0.8]));
  Object.assign(ta.style, {
    left: top.x + 'px', top: top.y + 'px', fontSize: fs + 'px', lineHeight: (t.leading * z) + 'px', fontFamily: t.fontFamily, fontWeight: t.fontWeight,
    color: t.fillColor ? t.fillColor.toCSS(true) : '#000', width: Math.max(40, Math.max(...lines.map(l => l.length || 1)) * fs * 1.05 + 20) + 'px', height: (lines.length * t.leading * z + 8) + 'px',
  });
}
function stopText() {
  if (!editing) return;
  const { item: t, ta, before } = editing; editing = null;
  t.content = ta.value; t.visible = true; ta.remove();
  if (!t.content.trim()) { t.remove(); if (!t.data.fresh) doc.commit(T('vt_text')); else doc.revert(); refresh(); return; }
  delete t.data.fresh;
  if (t.content !== before || before === '') { setSel([t]); doc.commit(T('vt_text')); }
  refresh();
}

// ---------- 吸管 ----------
function pickAt(p) {
  const h = doc.art.hitTest(p, { fill: true, stroke: true, tolerance: tol() });
  if (!h || h.item instanceof scope.Raster) return;
  const it = h.item;
  style.fill = it.fillColor ? it.fillColor.toCSS(true) : null;
  style.stroke = it.strokeColor ? it.strokeColor.toCSS(true) : null;
  style.strokeWidth = it.strokeWidth;
  if (sel.length) { sel.forEach(i => { applyColor(i, 'fill', style.fill); applyColor(i, 'stroke', style.stroke); if (!(i instanceof scope.Group)) i.strokeWidth = style.strokeWidth; }); doc.commit(T('vt_picker')); }
  syncColors(); buildOpts(); refresh();
}

// ---------- 疊加層：選取框、拉框、鋼筆預覽 ----------
function drawOverlay() {
  if (!og) return;
  const d = devicePixelRatio || 1;
  og.setTransform(d, 0, 0, d, 0, 0); og.clearRect(0, 0, W, H);
  if (!doc) return;
  const acc = getComputedStyle(document.documentElement).getPropertyValue('--r-accent').trim() || '#4f8cff';
  og.lineWidth = 1;
  // 揀咗嘅物件：每件外框 + 成組選取框同把手
  if (sel.length && tool !== 'direct') {
    og.strokeStyle = acc;
    for (const i of sel) { const b = i.bounds, a = toView(b.topLeft), c = toView(b.bottomRight); og.strokeRect(Math.round(a.x) + .5, Math.round(a.y) + .5, Math.round(c.x - a.x), Math.round(c.y - a.y)); }
    if (tool === 'select') {
      const b = selBounds(), hs = handles(b);
      og.setLineDash([]); og.strokeStyle = acc;
      const a = hs.nw, c = hs.se; og.strokeRect(Math.round(a.x) + .5, Math.round(a.y) + .5, Math.round(c.x - a.x), Math.round(c.y - a.y));
      og.fillStyle = '#fff';
      for (const k in hs) { const q = hs[k]; og.fillRect(Math.round(q.x) - 3.5, Math.round(q.y) - 3.5, 7, 7); og.strokeRect(Math.round(q.x) - 3.5, Math.round(q.y) - 3.5, 7, 7); }
    }
  }
  if (drag && drag.kind === 'marquee') {
    const a = toView(drag.a), b = toView(drag.b);
    og.setLineDash([4, 3]); og.strokeStyle = '#888'; og.strokeRect(Math.min(a.x, b.x) + .5, Math.min(a.y, b.y) + .5, Math.abs(b.x - a.x), Math.abs(b.y - a.y));
    og.setLineDash([]);
  }
  if (tool === 'pen' && pen && hover && !drag && pen.segments.length) {
    const last = pen.lastSegment, a = toView(last.point), ho = toView(last.point.add(last.handleOut)), b = toView(hover);
    og.strokeStyle = acc; og.beginPath(); og.moveTo(a.x, a.y); og.bezierCurveTo(ho.x, ho.y, b.x, b.y, b.x, b.y); og.stroke();
    if (pen.segments.length >= 2 && toView(pen.firstSegment.point).getDistance(b) <= 7) { const f = toView(pen.firstSegment.point); og.beginPath(); og.arc(f.x, f.y, 6, 0, 7); og.stroke(); }
  }
}

// ---------- 剪貼簿 ----------
function copy(cut) {
  if (!sel.length) return;
  clip = sel.map(i => i.exportJSON());
  // 系統剪貼簿放一份 SVG（可以貼去其他程式）；自己貼返就用內部嗰份
  try { navigator.clipboard.writeText('<svg xmlns="http://www.w3.org/2000/svg">' + sel.map(i => i.exportSVG({ asString: true })).join('') + '</svg>').catch(() => { }); } catch { }
  if (cut) { sel.forEach(i => i.remove()); sel = []; doc.commit(T('cut')); refresh(); }
}
function paste(mode) {
  if (!clip) return;
  scope.activate();
  const items = clip.map(j => scope.Base.importJSON(j));
  // 一般貼上：放喺畫面中間；Ctrl+F / Ctrl+B：原位貼喺上面 / 下面
  if (mode === 'front' || mode === 'back') {
    const ref = mode === 'front' ? (sel.length ? sel[sel.length - 1] : null) : (sel.length ? sel[0] : null);
    items.forEach(i => { if (ref) (mode === 'front' ? i.insertAbove(ref) : i.insertBelow(ref)); else doc.art.addChild(i); });
  } else {
    items.forEach(i => doc.art.addChild(i));
    let b = items[0].bounds.clone(); items.slice(1).forEach(i => b = b.unite(i.bounds));
    const v = scope.view.center.subtract(b.center); items.forEach(i => i.translate(v));
  }
  setSel(items); doc.commit(T('paste'));
}
async function onPaste(e) {
  if (!shown() || !doc || editing || /INPUT|TEXTAREA/.test(document.activeElement?.tagName || '')) return;
  const items = [...(e.clipboardData?.items || [])];
  const img = items.find(i => i.type.startsWith('image/'));
  const txt = e.clipboardData?.getData('text/plain') || '';
  if (img && !clip) {
    e.preventDefault();
    const url = await new Promise(ok => { const r = new FileReader(); r.onload = () => ok(r.result); r.readAsDataURL(img.getAsFile()); });
    const r = new scope.Raster({ source: url, position: scope.view.center, parent: doc.art });
    r.onLoad = () => { setSel([r]); doc.commit(T('paste')); };
    return;
  }
  if (!clip && /^\s*<svg[\s\S]*<\/svg>\s*$/i.test(txt)) {
    e.preventDefault();
    const g = scope.project.importSVG(txt, { insert: false });
    if (g) { doc.art.addChild(g); g.position = scope.view.center; setSel([g]); doc.commit(T('paste')); }
    return;
  }
  if (clip) { e.preventDefault(); paste(); }
}

// ---------- 物件 ----------
function del() {
  if (tool === 'direct') {
    let n = 0;
    doc.art.getItems({ class: scope.Path }).forEach(p => { const ss = p.segments.filter(s => s.selected); if (!ss.length || ss.length === p.segments.length) return; ss.reverse().forEach(s => s.remove()); n++; if (p.segments.length < 2) p.remove(); });
    if (n) { doc.commit(T('vc_delete')); refresh(); return; }
  }
  if (!sel.length) return;
  sel.forEach(i => i.remove()); sel = []; doc.commit(T('vc_delete')); refresh();
}
function selectAll() { setSel(doc.art.children.filter(i => i.visible && !i.locked)); }
function group() {
  if (sel.length < 2) return;
  const ordered = doc.art.children.filter(i => sel.includes(i));
  const g = new scope.Group(); g.insertAbove(ordered[ordered.length - 1]); g.addChildren(ordered);
  setSel([g]); doc.commit(T('vc_group'));
}
function ungroup() {
  const out = [];
  sel.forEach(i => { if (i instanceof scope.Group && !i.clipped) { const kids = i.removeChildren(); kids.forEach(k => k.insertBelow(i)); i.remove(); out.push(...kids); } else out.push(i); });
  setSel(out); doc.commit(T('vc_ungroup'));
}
function arrange(how) {
  if (!sel.length) return;
  const ordered = doc.art.children.filter(i => sel.includes(i));
  if (how === 'front') ordered.forEach(i => doc.art.addChild(i));
  else if (how === 'back') [...ordered].reverse().forEach(i => doc.art.insertChild(0, i));
  else if (how === 'forward') [...ordered].reverse().forEach(i => { const n = i.nextSibling; if (n && !sel.includes(n)) i.insertAbove(n); });
  else ordered.forEach(i => { const p = i.previousSibling; if (p && !sel.includes(p)) i.insertBelow(p); });
  doc.commit(T('vm_' + how)); refresh();
}
function transform(kind) {
  if (!sel.length) return;
  const c = selBounds().center;
  sel.forEach(i => { if (kind === 'fh') i.scale(-1, 1, c); else if (kind === 'fv') i.scale(1, -1, c); else i.rotate(kind === 'r90' ? 90 : -90, c); });
  doc.commit(T('vm_' + kind)); refresh();
}
function nudge(dx, dy) { if (!sel.length) return; sel.forEach(i => i.translate(new scope.Point(dx, dy))); doc.commit(T('vt_move')); refresh(); }
function artboardSize(w, h) {
  w = clamp(Math.round(w), 1, 100000); h = clamp(Math.round(h), 1, 100000);
  if (w === doc.w && h === doc.h) return;
  doc.w = w; doc.h = h; doc.drawBoard(); doc.art.activate(); doc.commit(T('vm_artboard')); fit(); refresh();
}
function artboardDlg() {
  const wi = numInput(doc.w, 1, 100000), hi = numInput(doc.h, 1, 100000);
  const body = el('div', 'grid2'); body.append(field(T('ph_width'), wi), field(T('ph_height'), hi));
  const fitB = el('button', 'rb wide', T('vm_fitart')); fitB.type = 'button';
  body.appendChild(fitB);
  const d = dialog(T('vm_artboard'), body, [[T('cancel')], [T('ok'), () => artboardSize(+wi.value, +hi.value), true]]);
  fitB.onclick = () => {
    if (!doc.art.children.length) return;
    const b = doc.art.bounds; doc.art.translate(new scope.Point(-b.x, -b.y));
    d.close(); artboardSize(Math.ceil(b.width), Math.ceil(b.height));
  };
}
function historyGo(n) {
  if (!doc) return;
  while (doc.undoStack.length > n && doc.undo()) { }
  while (doc.undoStack.length < n && doc.redo()) { }
}
function undo() { finishPen(); const l = doc.undo(); if (l) toast(T('undo') + '：' + l, 1200); }
function redo() { const l = doc.redo(); if (l) toast(T('redo') + '：' + l, 1200); }

function objectMenu() {
  const has = sel.length > 0;
  return [
    { label: T('vm_transform'), sub: [{ label: T('vm_fh'), run: () => transform('fh') }, { label: T('vm_fv'), run: () => transform('fv') }, { label: T('vm_r90'), run: () => transform('r90') }, { label: T('vm_r-90'), run: () => transform('r-90') }], disabled: !has },
    { label: T('vm_arrange'), sub: [{ label: T('vm_front'), key: 'Ctrl+Shift+]', run: () => arrange('front') }, { label: T('vm_forward'), key: 'Ctrl+]', run: () => arrange('forward') }, { label: T('vm_backward'), key: 'Ctrl+[', run: () => arrange('backward') }, { label: T('vm_back'), key: 'Ctrl+Shift+[', run: () => arrange('back') }], disabled: !has },
    '-',
    { label: T('vc_group'), key: 'Ctrl+G', run: group, disabled: sel.length < 2 },
    { label: T('vc_ungroup'), key: 'Ctrl+Shift+G', run: ungroup, disabled: !sel.some(i => i instanceof scope.Group) },
    '-',
    { label: T('vm_artboard') + '…', run: artboardDlg },
  ];
}

export function vMenus() {
  const has = !!doc, s = has && sel.length > 0;
  return [
    {
      label: T('m_file'), items: () => [
        { label: T('vm_new'), key: 'Ctrl+N', run: () => ui.newDoc() },
        { label: T('open'), key: 'Ctrl+O', run: () => ui.open() },
        '-',
        { label: T('save'), key: 'Ctrl+S', run: () => ui.save(false), disabled: !has },
        { label: T('saveas'), key: 'Ctrl+Shift+S', run: () => ui.save(true), disabled: !has },
        { label: T('ph_export'), disabled: !has, sub: [['svg', 'SVG'], ['pdf', 'PDF'], ['png', 'PNG']].map(([x, n]) => ({ label: n, run: () => ui.exportAs(x) })) },
        '-',
        { label: T('newwin'), run: () => ui.newWindow() },
        { label: T('ph_close'), run: () => ui.goHome() },
      ]
    },
    {
      label: T('m_edit'), items: () => [
        { label: T('undo') + (has && doc.undoStack.length ? '：' + doc.undoStack[doc.undoStack.length - 1].label : ''), key: 'Ctrl+Z', run: undo, disabled: !has || !doc.undoStack.length },
        { label: T('redo'), key: 'Ctrl+Shift+Z', run: redo, disabled: !has || !doc.redoStack.length },
        '-',
        { label: T('cut'), key: 'Ctrl+X', run: () => copy(true), disabled: !s },
        { label: T('copy'), key: 'Ctrl+C', run: () => copy(false), disabled: !s },
        { label: T('paste'), key: 'Ctrl+V', run: () => paste(), disabled: !clip },
        { label: T('vm_pastefront'), key: 'Ctrl+F', run: () => paste('front'), disabled: !clip },
        { label: T('vm_pasteback'), key: 'Ctrl+B', run: () => paste('back'), disabled: !clip },
        { label: T('vc_delete'), key: 'Delete', run: del, disabled: !s },
      ]
    },
    { label: T('vm_object'), items: () => objectMenu() },
    {
      label: T('ph_m_select'), items: () => [
        { label: T('selectall'), key: 'Ctrl+A', run: selectAll, disabled: !has },
        { label: T('ph_deselect'), key: 'Ctrl+Shift+A', run: () => setSel([]), disabled: !s },
      ]
    },
    {
      label: T('m_view'), items: () => [
        { label: T('ph_zoomin'), key: 'Ctrl++', run: () => zoomBy(1.25), disabled: !has },
        { label: T('ph_zoomout'), key: 'Ctrl+-', run: () => zoomBy(0.8), disabled: !has },
        { label: T('vm_fitboard'), key: 'Ctrl+0', run: () => fit(), disabled: !has },
        { label: T('ph_actual'), key: 'Ctrl+1', run: () => zoomTo(1), disabled: !has },
      ]
    },
    {
      label: T('m_window'), items: () => [
        ...[['tools', 'ph_p_tools'], ['props', 'vp_props'], ['color', 'ph_p_color'], ['swatches', 'ph_p_swatches'], ['align', 'vp_align'], ['ops', 'vp_ops'], ['objects', 'vp_objects'], ['history', 'ph_p_history']]
          .map(([id, k]) => ({ label: T(k), check: dock.isShown(id), run: () => dock.toggle(id) })),
        '-',
        { label: T('ph_togglepanels'), key: 'Tab', run: () => dock.toggleAll() },
        { label: T('ph_resetlayout'), run: () => dock.reset() },
      ]
    },
  ];
}

// ---------- 快捷鍵 ----------
function onKey(e) {
  if (!doc || !shown() || editing) return;
  if (document.querySelector('.rmask')) return;
  const tag = document.activeElement?.tagName || '';
  if (/INPUT|TEXTAREA|SELECT/.test(tag) && document.activeElement.type !== 'range' && document.activeElement.type !== 'checkbox') return;
  const k = e.key.toLowerCase(), c = e.ctrlKey || e.metaKey;
  const run = f => { e.preventDefault(); e.stopPropagation(); f(); };
  if (e.code === 'Space' && !c) { e.preventDefault(); if (!spaceHeld && !drag) { spaceHeld = true; if (tool !== 'hand') { prevTool = tool; setTool('hand'); } } return; }
  if (c) {
    if (k === 'z' && !e.shiftKey) return run(undo);
    if (k === 'y' || (k === 'z' && e.shiftKey)) return run(redo);
    if (k === 'a' && e.shiftKey) return run(() => setSel([]));
    if (k === 'a') return run(selectAll);
    if (k === 'c') return run(() => copy(false));
    if (k === 'x') return run(() => copy(true));
    if (k === 'f') return run(() => paste('front'));
    if (k === 'b') return run(() => paste('back'));
    if (k === 'g' && e.shiftKey) return run(ungroup);
    if (k === 'g') return run(group);
    if (k === ']' || k === '}') return run(() => arrange(e.shiftKey ? 'front' : 'forward'));
    if (k === '[' || k === '{') return run(() => arrange(e.shiftKey ? 'back' : 'backward'));
    if (k === '0') return run(() => fit());
    if (k === '1') return run(() => zoomTo(1));
    if (k === '=' || k === '+') return run(() => zoomBy(1.25));
    if (k === '-') return run(() => zoomBy(0.8));
    return;
  }
  if (k === 'enter' || k === 'escape') { if (pen) return run(finishPen); if (k === 'escape' && sel.length) return run(() => setSel([])); return; }
  if (k === 'delete' || k === 'backspace') return run(del);
  if (k.startsWith('arrow')) { const s = e.shiftKey ? 10 : 1; return run(() => nudge(k === 'arrowleft' ? -s : k === 'arrowright' ? s : 0, k === 'arrowup' ? -s : k === 'arrowdown' ? s : 0)); }
  if (k === 'tab') return run(() => dock.toggleAll());
  if (e.altKey) return;
  if (k === 'x' && e.shiftKey) return run(swapFillStroke);
  if (k === 'x') return run(() => { style.active = style.active === 'fill' ? 'stroke' : 'fill'; syncColors(); });
  if (k === 'd') return run(defaultColors);
  if (k === '/') return run(setNone);
  const map = { v: 'select', a: 'direct', p: 'pen', t: 'text', '\\': 'line', m: 'rect', l: 'ellipse', n: 'pencil', i: 'picker', h: 'hand', z: 'zoom' };
  if (map[k]) return run(() => setTool(map[k]));
}
