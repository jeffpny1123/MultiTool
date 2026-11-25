// 修圖編輯器：畫布檢視、工具、圖層面板、選單、快捷鍵。
import { T, $, el, toast, clamp, buildMenubar, popupMenu, dialog, field, numInput } from '../common.js';
import { PhotoDoc, Layer, canvas, ctx2d, cloneCanvas, BLENDS, R, shapeMask, combineMask, invertMask, maskBounds, floodRegion, regionToMask } from './doc.js';
import { ICON } from './icons.js';
import { createDock } from '../dock.js';
import { colorPanel, swatchesPanel, historyPanel, navigatorPanel } from './panels.js';
import * as A from './adjust.js';

// ---------- 狀態 ----------
let doc = null;
let ui = null;                         // { onDirty, save(as), exportAs(ext), goHome, newWindow, open, newDoc }
const view = { zoom: 1, ox: 0, oy: 0 };
let cv, cg, W = 0, H = 0;              // 畫面 canvas（CSS 像素大細 W × H）
let comp = canvas(1, 1), compDirty = true;
let raf = 0, antsOff = 0;
let tool = 'brush', prevTool = null, spaceHeld = false;
let fg = '#000000', bg = '#ffffff';
const opt = {
  brush: { size: 20, hard: 0.8, opacity: 1 },
  pencil: { size: 1, hard: 1, opacity: 1 },
  eraser: { size: 30, hard: 0.8, opacity: 1 },
  clone: { size: 40, hard: 0.6, opacity: 1, aligned: true },
  fill: { tol: 32, contiguous: true, opacity: 1 },
  wand: { tol: 32, contiguous: true },
  gradient: { type: 'linear', mode: 'fgbg', opacity: 1, reverse: false },
  text: { font: 'Microsoft JhengHei', size: 48, bold: false, italic: false, align: 'left' },
  shape: { mode: 'fill', width: 4 },
  selMode: 'new',
};
const PAINT_TOOLS = ['brush', 'pencil', 'eraser', 'clone'];
const FONTS = ['Microsoft JhengHei', 'Microsoft YaHei', 'PMingLiU', 'Arial', 'Segoe UI', 'Times New Roman', 'Georgia', 'Verdana', 'Tahoma', 'Courier New', 'Impact', 'Comic Sans MS', 'Consolas', 'Yu Gothic', 'Malgun Gothic'];
let xf = null;            // 自由變形緊（Ctrl+T）
let crop = null;          // 裁切工具嘅框 { x, y, w, h }
let poly = null;          // 多邊形套索：撳緊嘅點
let editing = null;       // 打緊字嘅文字圖層
let cloneSrc = null, cloneOff = null;
let lastSel = null, lastFilter = null;
let showGrid = false, showExtras = true;
let tempTool = null;      // 撳住 Ctrl / Alt 暫時轉嘅工具
let numBuf = '', numT = 0;
let drag = null;                       // 而家拖緊乜（每種工具自己嘅資料）
let hover = null;                      // 滑鼠喺文件上嘅位置
let clip = null;                       // 內部剪貼簿 { canvas, x, y }
const strokeC = canvas(1, 1), clipC = canvas(1, 1), liveC = canvas(1, 1);

// 工具列：一格一組（同類工具收埋一齊，撳住 / 右鍵彈出揀），格仔顯示嗰組最後用過嘅工具；同一個字母 + Shift 喺組入面輪住揀
const TOOL_KEY = {
  move: 'V', rect: 'M', ellipse: 'M', lasso: 'L', polylasso: 'L', wand: 'W', crop: 'C', picker: 'I', brush: 'B', pencil: 'B', clone: 'S', eraser: 'E',
  gradient: 'G', fill: 'G', text: 'T', shrect: 'U', shellipse: 'U', shline: 'U', hand: 'H', zoom: 'Z',
};
const GROUPS = [['move'], ['rect', 'ellipse'], ['lasso', 'polylasso'], ['wand'], ['crop'], '-', ['picker'], ['brush', 'pencil'], ['clone'], ['eraser'], ['gradient', 'fill'], '-', ['text'], ['shrect', 'shellipse', 'shline'], '-', ['hand'], ['zoom']];
const groupOf = id => GROUPS.find(g => g !== '-' && g.includes(id));
const groupPick = {};      // 組 → 最後用過嘅工具
const SEL_TOOLS = ['rect', 'ellipse', 'lasso', 'polylasso', 'wand'];
const SHAPES = ['shrect', 'shellipse', 'shline'];
let dock = null, pColor, pSwatch, pHist, pNav;
const recentColors = [];

// ---------- 開始 ----------
export function mountEditor(callbacks) {
  ui = callbacks;
  const root = $('editor');
  root.innerHTML =
    '<div id="opts"></div>' +
    '<div id="work"><div id="center"><div id="doctabs"><div class="dtab on"><span id="dtName"></span><button type="button" class="mini" id="dtClose">×</button></div></div><div id="stage"><canvas id="cv"></canvas></div></div></div>' +
    '<div id="status"><span id="stZoom"></span><span id="stSize"></span><span id="stPos"></span><span class="grow"></span><span id="stHint"></span></div>';
  cv = $('cv'); cg = cv.getContext('2d');
  $('dtClose').onclick = () => ui.goHome(); $('dtClose').title = T('ph_close');
  // 面板（可以拖去其他位置 / 變浮動視窗）
  const api = {
    get doc() { return doc; }, get fg() { return fg; }, get bg() { return bg; }, get recent() { return recentColors; }, view,
    setColor(which, hex) { if (which === 'bg') bg = hex; else fg = hex; syncColors(); },
    used: usedColor,
    pickNative(which) { const i = $(which === 'bg' ? 'cBg' : 'cFg'); i && i.click(); },
    comp: () => { if (doc && compDirty) compose(); return comp; },
    size: () => ({ W, H }),
    zoomTo: z => { if (doc) zoomTo(z); },
    centerOn: (x, y) => { view.ox = W / 2 - x * view.zoom; view.oy = H / 2 - y * view.zoom; req(); },
    historyGo,
  };
  const toolsEl = el('div'); toolsEl.id = 'tools';
  const layersEl = el('div'); layersEl.id = 'side';
  pColor = colorPanel(api); pSwatch = swatchesPanel(api); pHist = historyPanel(api); pNav = navigatorPanel(api);
  dock = createDock({
    work: $('work'), stage: $('center'), T, key: 'rabi-design-layout-v1',
    panels: {
      tools: { title: T('ph_p_tools'), icon: ICON.brush, el: toolsEl, compact: true },
      color: { title: T('ph_p_color'), icon: ICON.palette, el: pColor.el },
      swatches: { title: T('ph_p_swatches'), icon: ICON.grid, el: pSwatch.el },
      nav: { title: T('ph_p_nav'), icon: ICON.nav, el: pNav.el },
      history: { title: T('ph_p_history'), icon: ICON.history, el: pHist.el, weight: 1 },
      layers: { title: T('ph_layers'), icon: ICON.layers, el: layersEl, weight: 3 },
    },
    layout: {
      left: { w: 52, groups: [{ tabs: ['tools'], active: 'tools' }] },
      right: { w: 280, groups: [{ tabs: ['color', 'swatches'], active: 'color' }, { tabs: ['nav', 'history'], active: 'nav' }, { tabs: ['layers'], active: 'layers' }] },
      floats: [], hidden: [],
    },
    onChange: () => { syncPanels(); },
  });
  dock.render();
  buildTools(); buildSide();
  const stage = $('stage');
  new ResizeObserver(resize).observe(stage);
  stage.addEventListener('pointerdown', onDown);
  stage.addEventListener('pointermove', onMove);
  stage.addEventListener('pointerup', onUp);
  stage.addEventListener('pointercancel', onUp);
  stage.addEventListener('pointerleave', () => { hover = null; showPos(); req(); });
  stage.addEventListener('wheel', onWheel, { passive: false });
  stage.addEventListener('contextmenu', e => e.preventDefault());
  stage.addEventListener('dblclick', onDbl);
  addEventListener('keydown', onKey);
  addEventListener('keyup', e => {
    if (e.code === 'Space' && spaceHeld) { spaceHeld = false; if (prevTool) { setTool(prevTool); prevTool = null; } }
    // 放開 Ctrl / Alt：轉返原本工具
    if (tempTool && ((e.key === 'Control' && tempTool.key === 'Control') || (e.key === 'Alt' && tempTool.key === 'Alt'))) { e.preventDefault(); const t = tempTool.tool; tempTool = null; if (!drag) setTool(t); else drag.restoreTool = t; }
  });
  addEventListener('blur', () => { if (tempTool && !drag) { const t = tempTool.tool; tempTool = null; setTool(t); } spaceHeld = false; });
  addEventListener('paste', onPaste);
  setInterval(() => { if (doc && doc.sel && $('editor').classList.contains('show')) { antsOff = (antsOff + 1) % 8; req(); } }, 140);
  setTool('brush');
}

export const hasDoc = () => !!doc;
export const isDirty = () => !!(doc && doc.dirty);
export const markSaved = () => { if (doc) doc.dirty = false; };
export const currentDoc = () => doc;

export function setDoc(d) {
  if (editing) endText(false);
  xf = null; crop = null; poly = null; cloneSrc = null; lastSel = null;
  doc = d;
  doc.dirty = false;
  doc.onChange = kind => {
    compDirty = true;
    if (kind === 'struct') { buildLayers(); }
    else if (kind === 'pixels') thumbSoon();
    histSoon();
    ui.onDirty && ui.onDirty();
    req();
  };
  comp = canvas(doc.w, doc.h); compDirty = true;
  buildLayers(); histSoon();
  setTimeout(() => { fit(1); req(); }, 0);
}

export function menus() {
  const has = !!doc, sel = has && !!doc.sel;
  return [
    {
      label: T('m_file'), items: () => [
        { label: T('ph_new'), key: 'Ctrl+N', run: () => ui.newDoc() },
        { label: T('open'), key: 'Ctrl+O', run: () => ui.open() },
        '-',
        { label: T('save'), key: 'Ctrl+S', run: () => ui.save(false), disabled: !has },
        { label: T('saveas'), key: 'Ctrl+Shift+S', run: () => ui.save(true), disabled: !has },
        { label: T('ph_export'), disabled: !has, sub: [['png', 'PNG'], ['jpg', 'JPG'], ['webp', 'WebP']].map(([x, n]) => ({ label: n, run: () => ui.exportAs(x) })) },
        '-',
        { label: T('newwin'), run: () => ui.newWindow() },
        { label: T('ph_close'), key: 'Ctrl+W', run: () => ui.goHome() },
      ]
    },
    {
      label: T('m_edit'), items: () => [
        { label: T('undo') + (doc && doc.undoStack.length ? '：' + doc.undoStack[doc.undoStack.length - 1].label : ''), key: 'Ctrl+Z', run: undo, disabled: !has || !doc.undoStack.length },
        { label: T('redo'), key: 'Ctrl+Shift+Z', run: redo, disabled: !has || !doc.redoStack.length },
        { label: T('ph_togglelast'), key: 'Ctrl+Alt+Z', run: toggleLast, disabled: !has || (!doc.undoStack.length && !doc.redoStack.length) },
        '-',
        { label: T('cut'), key: 'Ctrl+X', run: () => copy(true), disabled: !has },
        { label: T('copy'), key: 'Ctrl+C', run: () => copy(false), disabled: !has },
        { label: T('ph_copymerged'), key: 'Ctrl+Shift+C', run: () => copy(false, true), disabled: !has },
        { label: T('paste'), key: 'Ctrl+V', run: () => { if (clip) pasteCanvas(clip.canvas, clip.x, clip.y); else toast(T('paste_tip')); }, disabled: !has },
        { label: T('ph_pasteinplace'), key: 'Ctrl+Shift+V', run: () => { if (clip) pasteCanvas(clip.canvas, clip.x, clip.y); else toast(T('paste_tip')); }, disabled: !has },
        { label: T('ph_clear'), key: 'Delete', run: clearSel, disabled: !has },
        '-',
        { label: T('ph_filldlg'), key: 'Shift+F5', run: fillDlg, disabled: !has },
        { label: T('ph_strokedlg'), run: strokeDlg, disabled: !sel },
        { label: T('ph_fillfg'), key: 'Alt+Backspace', run: () => fillSel(fg), disabled: !has },
        { label: T('ph_fillbg'), key: 'Ctrl+Backspace', run: () => fillSel(bg), disabled: !has },
        '-',
        { label: T('ph_freetransform'), key: 'Ctrl+T', run: startTransform, disabled: !has },
        {
          label: T('ph_transform'), disabled: !has, sub: [
            { label: T('ph_lrot_180'), run: () => rotLayer(180) },
            { label: T('ph_lrot_cw'), run: () => rotLayer(90) },
            { label: T('ph_lrot_ccw'), run: () => rotLayer(-90) },
            { label: T('ph_lflip_h'), run: () => flipLayer(true) },
            { label: T('ph_lflip_v'), run: () => flipLayer(false) },
          ]
        },
      ]
    },
    {
      label: T('ph_m_image'), items: () => [
        {
          label: T('ph_adjust'), disabled: !has, sub: [
            { label: T('ph_a_bc'), run: () => adjust('bc') },
            { label: T('ph_a_levels'), key: 'Ctrl+L', run: levelsDlg },
            { label: T('ph_a_curves'), key: 'Ctrl+M', run: curvesDlg },
            { label: T('ph_a_exposure'), run: () => adjust('exposure') },
            '-',
            { label: T('ph_a_vibrance'), run: () => adjust('vibrance') },
            { label: T('ph_a_huesat'), key: 'Ctrl+U', run: () => adjust('huesat') },
            { label: T('ph_a_balance'), key: 'Ctrl+B', run: () => adjust('balance') },
            { label: T('ph_a_bw'), key: 'Ctrl+Alt+Shift+B', run: () => adjust('bw') },
            { label: T('ph_a_photofilter'), run: () => adjust('photofilter') },
            '-',
            { label: T('ph_a_invert'), key: 'Ctrl+I', run: () => applyNow(T('ph_a_invert'), A.invert) },
            { label: T('ph_a_posterize'), run: () => adjust('posterize') },
            { label: T('ph_a_threshold'), run: () => adjust('threshold') },
            '-',
            { label: T('ph_a_desat'), key: 'Ctrl+Shift+U', run: () => applyNow(T('ph_a_desat'), A.desaturate) },
          ]
        },
        '-',
        { label: T('ph_a_autotone'), key: 'Ctrl+Shift+L', run: () => applyNow(T('ph_a_autotone'), A.autoTone), disabled: !has },
        { label: T('ph_a_autocontrast'), key: 'Ctrl+Alt+Shift+L', run: () => applyNow(T('ph_a_autocontrast'), A.autoContrast), disabled: !has },
        { label: T('ph_a_autocolor'), key: 'Ctrl+Shift+B', run: () => applyNow(T('ph_a_autocolor'), A.autoColor), disabled: !has },
        '-',
        { label: T('ph_imgsize'), key: 'Ctrl+Alt+I', run: imageSizeDlg, disabled: !has },
        { label: T('ph_canvassize'), key: 'Ctrl+Alt+C', run: canvasSizeDlg, disabled: !has },
        {
          label: T('ph_imgrotate'), disabled: !has, sub: [
            { label: T('ph_rot_180'), run: () => rotateDoc(180) },
            { label: T('ph_rot_cw'), run: () => rotateDoc(90) },
            { label: T('ph_rot_ccw'), run: () => rotateDoc(-90) },
            { label: T('ph_flip_h'), run: () => flipDoc(true) },
            { label: T('ph_flip_v'), run: () => flipDoc(false) },
          ]
        },
        { label: T('ph_crop'), run: cropToSel, disabled: !sel },
        { label: T('ph_trim'), run: trim, disabled: !has },
      ]
    },
    {
      label: T('ph_m_layer'), items: () => [
        { label: T('ph_newlayer'), key: 'Ctrl+Shift+N', run: newLayer, disabled: !has },
        { label: T('ph_duplayer'), key: 'Ctrl+J', run: dupLayer, disabled: !has },
        { label: T('ph_layercut'), key: 'Ctrl+Shift+J', run: layerViaCut, disabled: !sel },
        { label: T('ph_dellayer'), run: delLayer, disabled: !has || doc.layers.length < 2 },
        { label: T('ph_renamelayer'), run: () => renameLayer(doc.active), disabled: !has },
        { label: T('ph_rasterize'), run: () => rasterize(doc.layer), disabled: !has || !doc.layer.text },
        '-',
        {
          label: T('ph_arrange'), disabled: !has, sub: [
            { label: T('ph_tofront'), key: 'Ctrl+Shift+]', run: () => moveLayerTo(doc.layers.length - 1) },
            { label: T('ph_layerup'), key: 'Ctrl+]', run: () => moveLayer(1) },
            { label: T('ph_layerdown'), key: 'Ctrl+[', run: () => moveLayer(-1) },
            { label: T('ph_toback'), key: 'Ctrl+Shift+[', run: () => moveLayerTo(0) },
          ]
        },
        { label: T('ph_selabove'), key: 'Alt+]', run: () => pickLayer(1), disabled: !has },
        { label: T('ph_selbelow'), key: 'Alt+[', run: () => pickLayer(-1), disabled: !has },
        '-',
        { label: T('ph_mergedown'), key: 'Ctrl+E', run: mergeDown, disabled: !has || doc.active === 0 },
        { label: T('ph_mergevisible'), key: 'Ctrl+Shift+E', run: mergeVisible, disabled: !has || doc.layers.length < 2 },
        { label: T('ph_stamp'), key: 'Ctrl+Alt+Shift+E', run: stampVisible, disabled: !has },
        { label: T('ph_flatten'), run: flatten, disabled: !has || doc.layers.length < 2 },
      ]
    },
    {
      label: T('ph_m_select'), items: () => [
        { label: T('selectall'), key: 'Ctrl+A', run: selectAll, disabled: !has },
        { label: T('ph_deselect'), key: 'Ctrl+D', run: deselect, disabled: !sel },
        { label: T('ph_reselect'), key: 'Ctrl+Shift+D', run: reselect, disabled: !has || !lastSel },
        { label: T('ph_invert'), key: 'Ctrl+Shift+I', run: invertSel, disabled: !has },
        '-',
        { label: T('ph_sellayer'), run: selectLayerPixels, disabled: !has },
        {
          label: T('ph_modify'), disabled: !sel, sub: [
            { label: T('ph_border'), run: () => modifySel('border') },
            { label: T('ph_smooth'), run: () => modifySel('smooth') },
            { label: T('ph_expand'), run: () => modifySel('expand') },
            { label: T('ph_contract'), run: () => modifySel('contract') },
            { label: T('ph_feather'), key: 'Shift+F6', run: () => modifySel('feather') },
          ]
        },
      ]
    },
    {
      label: T('ph_m_filter'), items: () => [
        { label: lastFilter ? T('ph_lastfilter', lastFilter.title) : T('ph_lastfilter0'), key: 'Ctrl+Alt+F', run: repeatFilter, disabled: !has || !lastFilter },
        '-',
        { label: T('ph_f_blur'), disabled: !has, sub: [{ label: T('ph_f_gauss'), run: () => adjust('gauss') }, { label: T('ph_f_motion'), run: () => adjust('motion') }] },
        { label: T('ph_f_sharpen'), disabled: !has, sub: [{ label: T('ph_f_unsharp'), run: () => adjust('unsharp') }, { label: T('ph_f_sharp'), run: () => applyNow(T('ph_f_sharp'), A.sharpen) }] },
        { label: T('ph_f_noise'), disabled: !has, sub: [{ label: T('ph_f_addnoise'), run: () => adjust('noise') }, { label: T('ph_f_median'), run: () => adjust('median') }] },
        { label: T('ph_f_pixelate'), disabled: !has, sub: [{ label: T('ph_f_mosaic'), run: () => adjust('mosaic') }] },
        { label: T('ph_f_stylize'), disabled: !has, sub: [{ label: T('ph_f_emboss'), run: () => adjust('emboss') }, { label: T('ph_f_edges'), run: () => applyNow(T('ph_f_edges'), A.findEdges) }] },
        { label: T('ph_f_other'), disabled: !has, sub: [{ label: T('ph_f_highpass'), run: () => adjust('highpass') }] },
      ]
    },
    {
      label: T('m_view'), items: () => [
        { label: T('ph_zoomin'), key: 'Ctrl++', run: () => zoomBy(1.25), disabled: !has },
        { label: T('ph_zoomout'), key: 'Ctrl+-', run: () => zoomBy(0.8), disabled: !has },
        { label: T('ph_fit'), key: 'Ctrl+0', run: () => fit(), disabled: !has },
        { label: T('ph_actual'), key: 'Ctrl+1', run: () => zoomTo(1), disabled: !has },
        '-',
        { label: T('ph_extras'), key: 'Ctrl+H', check: showExtras, run: () => { showExtras = !showExtras; req(); } },
        { label: T('ph_grid'), key: "Ctrl+'", check: showGrid, run: () => { showGrid = !showGrid; req(); } },
      ]
    },
    {
      label: T('m_window'), items: () => [
        ...[['tools', 'ph_p_tools', ''], ['color', 'ph_p_color', 'F6'], ['swatches', 'ph_p_swatches', ''], ['history', 'ph_p_history', ''], ['layers', 'ph_layers', 'F7'], ['nav', 'ph_p_nav', '']]
          .map(([id, k, key]) => ({ label: T(k), key, check: dock.isShown(id), run: () => dock.toggle(id) })),
        '-',
        { label: T('ph_togglepanels'), key: 'Tab', run: () => dock.toggleAll() },
        { label: T('ph_resetlayout'), run: () => dock.reset() },
      ]
    },
  ];
}

// ---------- 面板 ----------
function syncPanels() {
  try { pColor && pColor.sync(); pSwatch && pSwatch.sync(); pHist && pHist.sync(); pNav && pNav.sync(); } catch (e) { console.error(e); }
}
let histT = 0;
function histSoon() { cancelAnimationFrame(histT); histT = requestAnimationFrame(() => pHist && pHist.sync()); }
let navT = 0;
function navSoon() { if (navT) return; navT = setTimeout(() => { navT = 0; pNav && pNav.sync(); }, 120); }
/** 用過嘅顏色記入「最近用過」（色板面板）。 */
function usedColor(c) {
  const i = recentColors.indexOf(c);
  if (i === 0) return;
  if (i > 0) recentColors.splice(i, 1);
  recentColors.unshift(c);
  if (recentColors.length > 16) recentColors.length = 16;
  pSwatch && pSwatch.sync();
}
/** 步驟紀錄：撳第 n 行（0 = 開啟時）→ 復原 / 重做到嗰一步。 */
function historyGo(n) {
  if (!doc) return;
  while (doc.undoStack.length > n && doc.undo()) { }
  while (doc.undoStack.length < n && doc.redo()) { }
  syncSize();
}

// ---------- 工具列 ----------
const toolTip = id => T('ph_t_' + id) + '  (' + TOOL_KEY[id] + ')';
function buildTools() {
  const t = $('tools');
  t.textContent = '';
  for (const g of GROUPS) {
    if (g === '-') { t.appendChild(el('div', 'tsep')); continue; }
    const key = g.join(',');
    const id = groupPick[key] || g[0];
    const b = el('button', 'tool' + (g.length > 1 ? ' multi' : '')); b.type = 'button'; b.dataset.group = key; b.dataset.tool = id;
    b.innerHTML = ICON[id];
    b.title = toolTip(id);
    b.onclick = () => setTool(b.dataset.tool);
    if (g.length > 1) {
      // 撳住半秒 / 右鍵：彈出成組工具揀
      let hold = 0;
      const flyout = () => { const r = b.getBoundingClientRect(); popupMenu(g.map(x => ({ label: T('ph_t_' + x), key: TOOL_KEY[x], icon: '', check: x === tool, run: () => setTool(x) })), r.right + 4, r.top); };
      b.onpointerdown = e => { if (e.button === 0) hold = setTimeout(flyout, 450); };
      b.onpointerup = b.onpointerleave = () => clearTimeout(hold);
      b.oncontextmenu = e => { e.preventDefault(); flyout(); };
    }
    t.appendChild(b);
  }
  t.appendChild(el('div', 'tsep'));
  const sw = el('div', 'swatch');
  sw.innerHTML = '<input type="color" id="cBg"><input type="color" id="cFg"><button type="button" id="cSwap" class="mini"></button><button type="button" id="cReset" class="mini">◩</button>';
  t.appendChild(sw);
  $('cSwap').innerHTML = ICON.swap; $('cSwap').title = T('ph_swap') + '  (X)'; $('cReset').title = T('ph_resetcolors') + '  (D)';
  $('cFg').title = T('ph_fg'); $('cBg').title = T('ph_bg');
  $('cFg').oninput = e => { fg = e.target.value; syncColors(); };
  $('cBg').oninput = e => { bg = e.target.value; syncColors(); };
  $('cFg').onchange = () => usedColor(fg); $('cBg').onchange = () => usedColor(bg);
  $('cSwap').onclick = swapColors; $('cReset').onclick = resetColors;
  syncColors();
}
function syncColors() { const f = $('cFg'), b = $('cBg'); if (f) { f.value = fg; b.value = bg; } pColor && pColor.sync(); }
function swapColors() { [fg, bg] = [bg, fg]; syncColors(); }
function resetColors() { fg = '#000000'; bg = '#ffffff'; syncColors(); }

export function setTool(id) {
  if (id !== tool) {
    if (editing) endText(true);
    const temp = tempTool || spaceHeld;
    if (xf && !temp) commitTransform();
    if (poly && !temp) poly = null;
    if (tool === 'crop' && id !== 'crop' && !temp) crop = null;
  }
  if (id === 'crop' && doc && !crop) crop = { x: 0, y: 0, w: doc.w, h: doc.h };
  tool = id;
  const g = groupOf(id);
  if (g) groupPick[g.join(',')] = id;
  document.querySelectorAll('#tools .tool').forEach(b => {
    if (g && b.dataset.group === g.join(',') && b.dataset.tool !== id) { b.dataset.tool = id; b.innerHTML = ICON[id]; b.title = toolTip(id); }
    b.classList.toggle('on', b.dataset.tool === id);
  });
  buildOpts();
  const stage = $('stage');
  stage.dataset.tool = id;
  $('stHint').textContent = T('ph_h_' + id);
  req();
}

// ---------- 工具選項列 ----------
function slider(label, get, set, min, max, step, fmt) {
  const w = el('label', 'oslider');
  w.appendChild(el('span', '', label));
  const r = el('input'); r.type = 'range'; r.min = min; r.max = max; r.step = step; r.value = get();
  const v = el('span', 'oval', fmt(get()));
  r.oninput = () => { set(+r.value); v.textContent = fmt(+r.value); req(); };
  w.appendChild(r); w.appendChild(v);
  w.sync = () => { r.value = get(); v.textContent = fmt(get()); };
  return w;
}
function check(label, get, set) {
  const w = el('label', 'ocheck'); const c = el('input'); c.type = 'checkbox'; c.checked = get(); c.onchange = () => set(c.checked);
  w.appendChild(c); w.appendChild(el('span', '', label)); return w;
}
function seg(items, get, set) {
  const g = el('div', 'oseg');
  for (const [v, label] of items) { const b = el('button', 'rb' + (get() === v ? ' on' : ''), label); b.type = 'button'; b.onclick = () => { set(v); buildOpts(); }; g.appendChild(b); }
  return g;
}
function obtn(label, run, pri) { const b = el('button', 'rb' + (pri ? ' pri' : ''), label); b.type = 'button'; b.onclick = run; return b; }
let sizeSlider = null, hardSlider = null, opSlider = null;
function buildOpts() {
  const o = $('opts'); o.textContent = '';
  sizeSlider = hardSlider = opSlider = null;
  if (xf) { buildXfOpts(o); return; }
  o.appendChild(el('span', 'oname', T('ph_t_' + tool)));
  if (PAINT_TOOLS.includes(tool)) {
    const b = opt[tool];
    o.appendChild(sizeSlider = slider(T('ph_size'), () => b.size, v => b.size = v, 1, tool === 'pencil' ? 100 : 500, 1, v => v + ' px'));
    if (tool !== 'pencil') o.appendChild(hardSlider = slider(T('ph_hard'), () => b.hard, v => b.hard = v, 0, 1, 0.05, v => Math.round(v * 100) + '%'));
    o.appendChild(opSlider = slider(T('ph_opacity'), () => b.opacity, v => b.opacity = v, 0.01, 1, 0.01, v => Math.round(v * 100) + '%'));
    if (tool === 'clone') o.appendChild(check(T('ph_aligned'), () => b.aligned, v => { b.aligned = v; cloneOff = null; }));
  } else if (tool === 'gradient') {
    const gopt = opt.gradient;
    o.appendChild(seg([['linear', T('ph_g_linear')], ['radial', T('ph_g_radial')]], () => gopt.type, v => gopt.type = v));
    o.appendChild(seg([['fgbg', T('ph_g_fgbg')], ['fgclear', T('ph_g_fgclear')]], () => gopt.mode, v => gopt.mode = v));
    o.appendChild(check(T('ph_g_reverse'), () => gopt.reverse, v => gopt.reverse = v));
    o.appendChild(opSlider = slider(T('ph_opacity'), () => gopt.opacity, v => gopt.opacity = v, 0.01, 1, 0.01, v => Math.round(v * 100) + '%'));
  } else if (tool === 'text') {
    buildTextOpts(o);
  } else if (SHAPES.includes(tool)) {
    if (tool !== 'shline') o.appendChild(seg([['fill', T('ph_s_fill')], ['stroke', T('ph_s_stroke')]], () => opt.shape.mode, v => opt.shape.mode = v));
    o.appendChild(slider(T('ph_s_width'), () => opt.shape.width, v => opt.shape.width = v, 1, 100, 1, v => v + ' px'));
  } else if (tool === 'crop') {
    o.appendChild(obtn('✓ ' + T('ph_apply'), commitCrop, true));
    o.appendChild(obtn('✕ ' + T('cancel'), () => { crop = { x: 0, y: 0, w: doc.w, h: doc.h }; req(); }));
  } else if (SEL_TOOLS.includes(tool)) {
    const g = el('div', 'oseg');
    for (const m of ['new', 'add', 'sub', 'and']) {
      const b = el('button', 'rb' + (opt.selMode === m ? ' on' : ''), T('ph_sm_' + m)); b.type = 'button';
      b.onclick = () => { opt.selMode = m; buildOpts(); };
      g.appendChild(b);
    }
    o.appendChild(g);
    if (tool === 'wand') {
      o.appendChild(slider(T('ph_tol'), () => opt.wand.tol, v => opt.wand.tol = v, 0, 255, 1, v => v));
      o.appendChild(check(T('ph_contig'), () => opt.wand.contiguous, v => opt.wand.contiguous = v));
    }
  } else if (tool === 'fill') {
    o.appendChild(slider(T('ph_tol'), () => opt.fill.tol, v => opt.fill.tol = v, 0, 255, 1, v => v));
    o.appendChild(check(T('ph_contig'), () => opt.fill.contiguous, v => opt.fill.contiguous = v));
    o.appendChild(opSlider = slider(T('ph_opacity'), () => opt.fill.opacity, v => opt.fill.opacity = v, 0.01, 1, 0.01, v => Math.round(v * 100) + '%'));
  }
  o.appendChild(el('span', 'ohint', T('ph_h_' + tool)));
}

// ---------- 圖層面板 ----------
function buildSide() {
  const s = $('side');
  s.innerHTML = '<div class="lprops"><select id="lBlend"></select><label class="oslider"><span></span><input type="range" id="lOp" min="0" max="100" step="1"><span class="oval" id="lOpV"></span></label></div>' +
    '<div id="layers"></div><div class="lbtns"></div>';
  s.querySelector('.lprops .oslider span').textContent = T('ph_opacity') + '：';
  const sel = $('lBlend');
  for (const b of BLENDS) { const o = el('option', '', T('ph_bl_' + b)); o.value = b; sel.appendChild(o); }
  sel.title = T('ph_blend');
  sel.onchange = () => { if (!doc) return; const v = sel.value; doc.change(T('ph_blend'), () => doc.layer.blend = v); };
  // 透明度：拉緊即時睇，放手先記入復原
  let before = null;
  $('lOp').oninput = () => {
    if (!doc) return;
    if (!before) before = doc.state();
    doc.layer.opacity = +$('lOp').value / 100; $('lOpV').textContent = $('lOp').value + '%';
    compDirty = true; req();
  };
  $('lOp').onchange = () => {
    if (!doc || !before) return;
    doc.push({ label: T('ph_opacity'), kind: 'state', before, after: doc.state() }); before = null;
    doc.onChange('struct');
  };
  const bt = s.querySelector('.lbtns');
  for (const [ic, k, run] of [['plus', 'ph_newlayer', newLayer], ['dup', 'ph_duplayer', dupLayer], ['up', 'ph_layerup', () => moveLayer(1)], ['down', 'ph_layerdown', () => moveLayer(-1)], ['merge', 'ph_mergedown', mergeDown], ['trash', 'ph_dellayer', delLayer]]) {
    const b = el('button', 'mini'); b.type = 'button'; b.innerHTML = ICON[ic]; b.title = T(k); b.onclick = () => doc && run(); bt.appendChild(b);
  }
}
let dragFrom = -1;
function buildLayers() {
  const list = $('layers'); if (!list || !doc) return;
  list.textContent = '';
  for (let i = doc.layers.length - 1; i >= 0; i--) {
    const l = doc.layers[i];
    const row = el('div', 'lrow' + (i === doc.active ? ' on' : '')); row.draggable = true;
    const eye = el('button', 'mini eye'); eye.type = 'button'; eye.innerHTML = l.visible ? ICON.eye : ICON.eyeOff; eye.title = T('ph_visible');
    eye.onclick = e => { e.stopPropagation(); doc.change(T('ph_visible'), () => l.visible = !l.visible); };
    const th = el('canvas', 'lthumb'); th.width = 44; th.height = 34; drawThumb(th, l);
    // Ctrl + 撳縮圖：揀返呢個圖層有內容嘅位置
    th.onclick = e => { if (e.ctrlKey || e.metaKey) { e.stopPropagation(); loadLayerSel(l, e.shiftKey ? (e.altKey ? 'and' : 'add') : e.altKey ? 'sub' : 'new'); } };
    th.title = T('ph_thumbtip');
    const name = el('span', 'lname', l.name);
    row.append(eye, th, name);
    if (l.text) { const tb = el('span', 'ltext', 'T'); tb.title = T('ph_textlayer'); row.insertBefore(tb, name); th.ondblclick = e => { e.stopPropagation(); setTool('text'); editText(l, false); }; }
    if (l.opacity < 1 || l.blend !== 'source-over') row.appendChild(el('span', 'lmeta', (l.blend !== 'source-over' ? T('ph_bl_' + l.blend) + ' ' : '') + Math.round(l.opacity * 100) + '%'));
    row.onclick = () => { if (doc.active !== i) { if (editing) endText(true); doc.active = i; buildLayers(); req(); } };
    row.ondblclick = () => renameLayer(i);
    row.oncontextmenu = e => { e.preventDefault(); doc.active = i; buildLayers(); popupMenu(menus()[3].items(), e.clientX, e.clientY); };
    row.ondragstart = e => { dragFrom = i; e.dataTransfer.effectAllowed = 'move'; };
    row.ondragover = e => { if (dragFrom >= 0) { e.preventDefault(); row.classList.add('dropat'); } };
    row.ondragleave = () => row.classList.remove('dropat');
    row.ondrop = e => {
      e.preventDefault(); e.stopPropagation(); row.classList.remove('dropat');
      const from = dragFrom; dragFrom = -1;
      if (from < 0 || from === i) return;
      doc.change(T('ph_reorder'), () => { const [x] = doc.layers.splice(from, 1); doc.layers.splice(i, 0, x); doc.active = i; });
    };
    row.ondragend = () => { dragFrom = -1; };
    list.appendChild(row);
  }
  const l = doc.layer;
  $('lBlend').value = l.blend; $('lOp').value = Math.round(l.opacity * 100); $('lOpV').textContent = Math.round(l.opacity * 100) + '%';
}
function drawThumb(th, l) {
  const g = th.getContext('2d');
  g.clearRect(0, 0, th.width, th.height);
  const s = Math.min(th.width / doc.w, th.height / doc.h), w = doc.w * s, h = doc.h * s, x = (th.width - w) / 2, y = (th.height - h) / 2;
  g.fillStyle = '#fff'; g.fillRect(x, y, w, h);
  g.fillStyle = '#d9d9d9';
  for (let yy = 0; yy < h; yy += 5) for (let xx = (yy / 5) % 2 * 5; xx < w; xx += 10) g.fillRect(x + xx, y + yy, Math.min(5, w - xx), Math.min(5, h - yy));
  g.imageSmoothingQuality = 'high';
  g.drawImage(l.canvas, x, y, w, h);
}
let thumbT = 0;
function thumbSoon() {
  clearTimeout(thumbT);
  thumbT = setTimeout(() => {
    const rows = document.querySelectorAll('#layers .lrow');
    rows.forEach((r, k) => { const l = doc.layers[doc.layers.length - 1 - k]; if (l) drawThumb(r.querySelector('canvas'), l); });
  }, 250);
}

// ---------- 畫面 ----------
function resize() {
  const s = $('stage').getBoundingClientRect(), d = devicePixelRatio || 1;
  const first = W === 0;
  W = s.width; H = s.height;
  cv.width = Math.round(W * d); cv.height = Math.round(H * d);
  cv.style.width = W + 'px'; cv.style.height = H + 'px';
  if (first && doc) fit(1);
  req();
}
function req() { if (!raf) raf = requestAnimationFrame(render); }

let checker = null;
function checkerPattern() {
  if (checker) return checker;
  const c = canvas(16, 16), g = ctx2d(c);
  g.fillStyle = '#ffffff'; g.fillRect(0, 0, 16, 16); g.fillStyle = '#d6d6d6'; g.fillRect(0, 0, 8, 8); g.fillRect(8, 8, 8, 8);
  return checker = cg.createPattern(c, 'repeat');
}

function compose() {
  if (comp.width !== doc.w || comp.height !== doc.h) { comp.width = doc.w; comp.height = doc.h; }
  const g = ctx2d(comp);
  g.clearRect(0, 0, doc.w, doc.h);
  doc.layers.forEach((l, i) => {
    if (!l.visible || l.opacity <= 0) return;
    const src = drag && drag.stroke && i === doc.active ? liveLayer() : l.canvas;
    g.globalAlpha = l.opacity; g.globalCompositeOperation = l.blend;
    g.drawImage(src, 0, 0);
  });
  g.globalAlpha = 1; g.globalCompositeOperation = 'source-over';
  compDirty = false;
}

function render() {
  raf = 0;
  const d = devicePixelRatio || 1;
  cg.setTransform(d, 0, 0, d, 0, 0);
  cg.clearRect(0, 0, W, H);
  if (!doc) return;
  if (compDirty) compose();
  const { zoom: z, ox, oy } = view, w = doc.w * z, h = doc.h * z;
  cg.save();
  cg.translate(ox, oy);
  cg.fillStyle = checkerPattern(); cg.fillRect(0, 0, w, h);
  cg.imageSmoothingEnabled = z < 2; cg.imageSmoothingQuality = 'high';
  cg.drawImage(comp, 0, 0, w, h);
  cg.restore();
  cg.strokeStyle = 'rgba(0,0,0,.35)'; cg.lineWidth = 1; cg.strokeRect(Math.round(ox) - .5, Math.round(oy) - .5, Math.round(w) + 1, Math.round(h) + 1);
  // 格線
  if (showGrid) {
    const step = gridStep(z);
    cg.save(); cg.beginPath();
    for (let x = step; x < doc.w; x += step) { const sx = Math.round(ox + x * z) + .5; cg.moveTo(sx, oy); cg.lineTo(sx, oy + h); }
    for (let y = step; y < doc.h; y += step) { const sy = Math.round(oy + y * z) + .5; cg.moveTo(ox, sy); cg.lineTo(ox + w, sy); }
    cg.strokeStyle = 'rgba(120,120,255,.45)'; cg.lineWidth = 1; cg.stroke(); cg.restore();
  }
  // 選取範圍（螞蟻線）
  const edges = showExtras && !xf ? doc.edges() : null;
  if (edges) {
    const off = drag && drag.kind === 'move' && drag.sel ? [drag.dx, drag.dy] : [0, 0];
    cg.save();
    cg.translate(ox + off[0] * z, oy + off[1] * z); cg.scale(z, z);
    cg.lineWidth = 1 / z;
    cg.setLineDash([4 / z, 4 / z]);
    cg.strokeStyle = '#000'; cg.lineDashOffset = -antsOff / z; cg.stroke(edges);
    cg.strokeStyle = '#fff'; cg.lineDashOffset = (4 - antsOff) / z; cg.stroke(edges);
    cg.restore();
  }
  // 拉緊嘅選取框 / 套索
  if (drag && drag.preview) {
    cg.save(); cg.translate(ox, oy); cg.scale(z, z);
    cg.lineWidth = 1 / z; cg.setLineDash([4 / z, 4 / z]);
    cg.strokeStyle = '#000'; cg.stroke(drag.preview);
    cg.strokeStyle = '#fff'; cg.lineDashOffset = 4 / z; cg.stroke(drag.preview);
    cg.restore();
  }
  // 多邊形套索
  if (poly) {
    const p = new Path2D(); poly.pts.forEach((q, i) => i ? p.lineTo(q.x, q.y) : p.moveTo(q.x, q.y));
    if (hover) p.lineTo(hover.x, hover.y);
    cg.save(); cg.translate(ox, oy); cg.scale(z, z); cg.lineWidth = 1 / z; cg.setLineDash([4 / z, 4 / z]);
    cg.strokeStyle = '#000'; cg.stroke(p); cg.strokeStyle = '#fff'; cg.lineDashOffset = 4 / z; cg.stroke(p); cg.restore();
  }
  // 漸層 / 形狀：拉緊嘅預覽
  if (drag && drag.kind === 'grad') {
    const a = toScreen(drag.a), b = toScreen(drag.b);
    cg.save(); cg.beginPath(); cg.moveTo(a.x, a.y); cg.lineTo(b.x, b.y);
    cg.lineWidth = 3; cg.strokeStyle = 'rgba(0,0,0,.5)'; cg.stroke(); cg.lineWidth = 1; cg.strokeStyle = '#fff'; cg.stroke();
    for (const q of [a, b]) { cg.beginPath(); cg.arc(q.x, q.y, 4, 0, Math.PI * 2); cg.fillStyle = '#fff'; cg.fill(); cg.stroke(); }
    cg.restore();
  }
  if (drag && drag.kind === 'shape') {
    cg.save(); cg.translate(ox, oy); cg.scale(z, z);
    drawShape(cg, drag.tool, drag.a, drag.b, drag.shift, drag.alt);
    cg.restore();
  }
  if (xf) drawXf();
  if (tool === 'crop' && crop) drawCrop();
  // 筆刷大細
  if (hover && PAINT_TOOLS.includes(tool) && !xf) {
    const r = Math.max(1, opt[tool].size * z / 2), x = ox + hover.x * z, y = oy + hover.y * z;
    cg.beginPath(); cg.arc(x, y, r, 0, Math.PI * 2);
    cg.lineWidth = 1; cg.strokeStyle = 'rgba(0,0,0,.7)'; cg.stroke();
    cg.beginPath(); cg.arc(x, y, r + 1, 0, Math.PI * 2); cg.strokeStyle = 'rgba(255,255,255,.7)'; cg.stroke();
    // 仿製印章：顯示取樣點
    if (tool === 'clone' && cloneSrc) {
      const off = drag && drag.clone ? drag.off : opt.clone.aligned ? cloneOff : null;
      const s = toScreen(off ? { x: hover.x + off.x, y: hover.y + off.y } : cloneSrc);
      cg.beginPath(); cg.moveTo(s.x - 6, s.y); cg.lineTo(s.x + 6, s.y); cg.moveTo(s.x, s.y - 6); cg.lineTo(s.x, s.y + 6);
      cg.lineWidth = 3; cg.strokeStyle = 'rgba(0,0,0,.6)'; cg.stroke(); cg.lineWidth = 1; cg.strokeStyle = '#fff'; cg.stroke();
    }
  }
  if (editing) placeText();
  $('stSize').textContent = doc.w + ' × ' + doc.h + ' px';
  $('stZoom').textContent = Math.round(view.zoom * 1000) / 10 + '%';
  $('dtName').textContent = docTitle + ' @ ' + Math.round(view.zoom * 1000) / 10 + '% (' + (doc.layer ? doc.layer.name : '') + ', RGB/8)';
  navSoon();
}
let docTitle = '';
/** 文件分頁顯示嘅檔名（main.js 改名 / 開檔時叫）。 */
export function setDocTitle(n) { docTitle = n; req(); }
function showPos() { $('stPos').textContent = hover ? Math.floor(hover.x) + ', ' + Math.floor(hover.y) : ''; }
const toScreen = p => ({ x: view.ox + p.x * view.zoom, y: view.oy + p.y * view.zoom });
/** 格線間距：畫面上大約 40–100 px 一格。 */
function gridStep(z) { for (const s of [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000]) if (s * z >= 40) return s; return 5000; }

// ---------- 縮放 / 平移 ----------
/** 成張圖放入視窗；max = 最多放幾大（啱啱開檔時唔好將細圖放大）。 */
function fit(max = 32) {
  if (!doc || !W) return;
  const z = Math.min((W - 48) / doc.w, (H - 48) / doc.h, max);
  view.zoom = clamp(z, 0.02, 32);
  view.ox = (W - doc.w * view.zoom) / 2; view.oy = (H - doc.h * view.zoom) / 2;
  req();
}
function zoomAt(z, sx, sy) {
  z = clamp(z, 0.02, 32);
  const dx = (sx - view.ox) / view.zoom, dy = (sy - view.oy) / view.zoom;
  view.zoom = z; view.ox = sx - dx * z; view.oy = sy - dy * z;
  req();
}
function zoomBy(f) { zoomAt(view.zoom * f, W / 2, H / 2); }
function zoomTo(z) { zoomAt(z, W / 2, H / 2); }
function onWheel(e) {
  if (!doc) return;
  e.preventDefault();
  const r = cv.getBoundingClientRect();
  if (e.ctrlKey || e.altKey) zoomAt(view.zoom * Math.pow(1.0015, -e.deltaY), e.clientX - r.left, e.clientY - r.top);
  else if (e.shiftKey) { view.ox -= e.deltaY; req(); }
  else { view.ox -= e.deltaX; view.oy -= e.deltaY; req(); }
}

// ---------- 滑鼠 / 筆 ----------
function docPt(e) { const r = cv.getBoundingClientRect(); return { x: (e.clientX - r.left - view.ox) / view.zoom, y: (e.clientY - r.top - view.oy) / view.zoom }; }
function selModeFor(e) { return e.shiftKey && e.altKey ? 'and' : e.shiftKey ? 'add' : e.altKey ? 'sub' : opt.selMode; }

function onDown(e) {
  if (!doc || drag) return;
  $('stage').setPointerCapture(e.pointerId);
  const p = docPt(e);
  // 中鍵 / 空白鍵 / 手形工具：平移
  if (e.button === 1 || tool === 'hand') { drag = { kind: 'pan', sx: e.clientX, sy: e.clientY, ox: view.ox, oy: view.oy }; $('stage').classList.add('panning'); return; }
  if (e.button !== 0) return;
  if (xf) return xfDown(p, e);
  if (editing && tool !== 'text') endText(true);
  const l = doc.layer;
  switch (tool) {
    case 'brush': case 'pencil': case 'eraser': case 'clone': {
      if (e.altKey && tool !== 'eraser') {
        // Alt + 撳：筆刷 = 吸色；仿製印章 = 設定取樣點
        if (tool === 'clone') { cloneSrc = { x: p.x, y: p.y }; cloneOff = null; toast(T('ph_clonesrc_set'), 1200); req(); }
        else pickAt(p, false);
        return;
      }
      if (!l.visible) { toast(T('ph_hidden')); return; }
      if (tool === 'clone' && !cloneSrc) { toast(T('ph_clonesrc')); return; }
      if (l.text) rasterize(l, true);
      const o = opt[tool];
      for (const c of [strokeC, clipC, liveC]) if (c.width !== doc.w || c.height !== doc.h) { c.width = doc.w; c.height = doc.h; }
      ctx2d(strokeC).clearRect(0, 0, doc.w, doc.h);
      drag = { kind: 'paint', stroke: true, tool, erase: tool === 'eraser', o, color: tool === 'eraser' || tool === 'clone' ? '#000000' : fg, end: doc.beginPixels(l, T('ph_t_' + tool)), rect: null, last: null };
      if (tool === 'clone') {
        // 取樣嘅位置 = 筆嘅位置 + off；對齊 = 每一筆都用返同一個相對位置
        if (!opt.clone.aligned || !cloneOff) cloneOff = { x: cloneSrc.x - p.x, y: cloneSrc.y - p.y };
        drag.clone = true; drag.off = cloneOff; drag.src = cloneCanvas(l.canvas);
      }
      // Shift + 撳：由上一筆嘅終點畫直線過嚟
      if (e.shiftKey && lastPaint) { drag.last = lastPaint; paintTo(p, pressureOf(e)); }
      else { dabAt(p, pressureOf(e)); drag.last = { ...p, pr: pressureOf(e) }; }
      compDirty = true; req();
      return;
    }
    case 'rect': case 'ellipse': case 'lasso':
      drag = { kind: 'sel', mode: selModeFor(e), a: p, b: p, pts: [p], preview: null };
      return;
    case 'polylasso': return polyClick(p, e);
    case 'wand': return wandAt(p, selModeFor(e));
    case 'fill': if (e.altKey) return pickAt(p, false); return fillAt(p);
    case 'gradient': if (e.altKey) return pickAt(p, false); drag = { kind: 'grad', a: p, b: p }; return;
    case 'picker': return pickAt(p, e.altKey);
    case 'zoom': { const r = cv.getBoundingClientRect(); zoomAt(view.zoom * (e.altKey ? 0.8 : 1.25), e.clientX - r.left, e.clientY - r.top); return; }
    case 'move': return moveStart(p, e);
    case 'crop': return cropDown(p, e);
    case 'text': return textDown(p, e);
    case 'shrect': case 'shellipse': case 'shline':
      drag = { kind: 'shape', tool, a: p, b: p, shift: false, alt: false };
      return;
  }
}
function onDbl(e) {
  if (!doc) return;
  if (xf) { e.preventDefault(); commitTransform(); return; }
  if (tool === 'crop' && crop) { e.preventDefault(); commitCrop(); return; }
  if (poly) { e.preventDefault(); closePoly(); }
}
function onMove(e) {
  if (!doc) return;
  const p = docPt(e);
  hover = p; showPos();
  if (!drag) {
    if (PAINT_TOOLS.includes(tool) || poly) req();
    updateCursor(p);
    return;
  }
  if (drag.kind === 'pan') { view.ox = drag.ox + e.clientX - drag.sx; view.oy = drag.oy + e.clientY - drag.sy; req(); return; }
  if (drag.kind === 'xf') { xfMove(p, e); return; }
  if (drag.kind === 'crop') { cropMove(p, e); return; }
  if (drag.kind === 'movetext') { moveText(p, e.shiftKey); return; }
  if (drag.kind === 'grad') { drag.b = e.shiftKey ? snap45(drag.a, p) : p; req(); return; }
  if (drag.kind === 'shape') { drag.b = p; drag.shift = e.shiftKey; drag.alt = e.altKey; req(); return; }
  if (drag.kind === 'paint') {
    const evs = e.getCoalescedEvents ? e.getCoalescedEvents() : [e];
    for (const ce of evs.length ? evs : [e]) paintTo(docPt(ce), pressureOf(ce));
    compDirty = true; req(); return;
  }
  if (drag.kind === 'sel') {
    drag.b = p;
    if (tool === 'lasso') drag.pts.push(p);
    drag.preview = selPath(e.shiftKey && tool !== 'lasso' && drag.mode !== 'add');
    req(); return;
  }
  if (drag.kind === 'move') { moveTo(p, e.shiftKey); return; }
}
function onUp(e) {
  if (!drag) return;
  const d = drag;
  if (d.restoreTool) setTimeout(() => { if (!drag && !tempTool) setTool(d.restoreTool); }, 0);
  if (d.kind === 'pan') { drag = null; $('stage').classList.remove('panning'); return; }
  if (d.kind === 'paint') { finishStroke(); return; }
  if (d.kind === 'xf') { drag = null; syncXfOpts(); req(); return; }
  if (d.kind === 'crop') { drag = null; normCrop(); req(); return; }
  if (d.kind === 'movetext') { drag = null; if (d.moved) doc.push({ label: T('ph_t_move'), kind: 'state', before: d.before, after: doc.state() }); doc.onChange('struct'); return; }
  if (d.kind === 'grad') { drag = null; applyGradient(d.a, d.b); return; }
  if (d.kind === 'shape') { drag = null; applyShape(d); return; }
  if (d.kind === 'sel') {
    drag = null;
    const dist = Math.hypot(d.b.x - d.a.x, d.b.y - d.a.y);
    if (dist < 2 / view.zoom || (tool === 'lasso' && d.pts.length < 3)) { if (d.mode === 'new' && doc.sel) doc.select(null, T('ph_deselect')); req(); return; }
    const path = selPath(e.shiftKey && tool !== 'lasso' && d.mode !== 'add', d);
    doc.select(shapeMask(doc.w, doc.h, path, d.mode, doc.sel), T('ph_t_' + tool));
    req(); return;
  }
  if (d.kind === 'move') { moveEnd(); return; }
}

// ---------- 選取 ----------
function selPath(square, d = drag) {
  const p = new Path2D();
  if (tool === 'lasso') {
    d.pts.forEach((q, i) => i ? p.lineTo(q.x, q.y) : p.moveTo(q.x, q.y));
    p.closePath(); return p;
  }
  let x1 = Math.round(d.a.x), y1 = Math.round(d.a.y), x2 = Math.round(d.b.x), y2 = Math.round(d.b.y);
  if (square) { const s = Math.max(Math.abs(x2 - x1), Math.abs(y2 - y1)); x2 = x1 + Math.sign(x2 - x1 || 1) * s; y2 = y1 + Math.sign(y2 - y1 || 1) * s; }
  const x = Math.min(x1, x2), y = Math.min(y1, y2), w = Math.abs(x2 - x1), h = Math.abs(y2 - y1);
  if (tool === 'ellipse') p.ellipse(x + w / 2, y + h / 2, w / 2, h / 2, 0, 0, Math.PI * 2);
  else p.rect(x, y, w, h);
  return p;
}
function wandAt(p, mode) {
  const x = Math.floor(p.x), y = Math.floor(p.y);
  if (x < 0 || y < 0 || x >= doc.w || y >= doc.h) return;
  const img = doc.layer.ctx.getImageData(0, 0, doc.w, doc.h);
  const r = floodRegion(img, x, y, opt.wand.tol, opt.wand.contiguous);
  doc.select(combineMask(doc.w, doc.h, regionToMask(doc.w, doc.h, r.mask), mode, doc.sel), T('ph_t_wand'));
}
function selectAll() { const c = canvas(doc.w, doc.h), g = ctx2d(c); g.fillRect(0, 0, doc.w, doc.h); doc.select(c, T('selectall')); }
function deselect() { if (doc.sel) { lastSel = doc.sel; doc.select(null, T('ph_deselect')); } }
function reselect() { if (lastSel && lastSel.width === doc.w && lastSel.height === doc.h) doc.select(lastSel, T('ph_reselect')); }
function loadLayerSel(l, mode) { const c = canvas(doc.w, doc.h); ctx2d(c).drawImage(l.canvas, 0, 0); doc.select(combineMask(doc.w, doc.h, c, mode, doc.sel), T('ph_sellayer')); }
function invertSel() { doc.select(invertMask(doc.w, doc.h, doc.sel), T('ph_invert')); }
function selectLayerPixels() { const c = canvas(doc.w, doc.h); ctx2d(c).drawImage(doc.layer.canvas, 0, 0); doc.select(c, T('ph_sellayer')); }

// ---------- 畫筆 / 擦膠 ----------
let lastPaint = null;
const pressureOf = e => e.pointerType === 'pen' && e.pressure > 0 ? e.pressure : 1;
function rgba(hex, a) { const n = parseInt(hex.slice(1), 16); return `rgba(${n >> 16 & 255},${n >> 8 & 255},${n & 255},${a})`; }
/** 鉛筆：冇反鋸齒嘅實心圓（預先砌好一張小圖，逐點印）。 */
let pencilStamp = null;
function pencilDab(p) {
  const s = Math.max(1, Math.round(drag.o.size)), key = s + drag.color;
  if (!pencilStamp || pencilStamp.key !== key) {
    const c = canvas(s, s), g = ctx2d(c), img = g.createImageData(s, s), n = parseInt(drag.color.slice(1), 16), r = s / 2;
    for (let y = 0; y < s; y++) for (let x = 0; x < s; x++) if (s <= 2 || (x + 0.5 - r) ** 2 + (y + 0.5 - r) ** 2 <= r * r) { const i = (y * s + x) * 4; img.data[i] = n >> 16 & 255; img.data[i + 1] = n >> 8 & 255; img.data[i + 2] = n & 255; img.data[i + 3] = 255; }
    g.putImageData(img, 0, 0);
    pencilStamp = { key, c };
  }
  const x = Math.floor(p.x - s / 2 + (s % 2 ? 0 : 0.5)), y = Math.floor(p.y - s / 2 + (s % 2 ? 0 : 0.5));
  ctx2d(strokeC).drawImage(pencilStamp.c, x, y);
  drag.rect = R.union(drag.rect, { x: x - 1, y: y - 1, w: s + 2, h: s + 2 });
}
function dabAt(p, pr) {
  if (drag.tool === 'pencil') return pencilDab(p);
  const o = drag.o, r = Math.max(0.5, o.size * pr / 2), g = ctx2d(strokeC);
  if (o.hard >= 0.99) g.fillStyle = drag.color;
  else { const gr = g.createRadialGradient(p.x, p.y, r * o.hard, p.x, p.y, r); gr.addColorStop(0, rgba(drag.color, 1)); gr.addColorStop(1, rgba(drag.color, 0)); g.fillStyle = gr; }
  g.beginPath(); g.arc(p.x, p.y, r, 0, Math.PI * 2); g.fill();
  drag.rect = R.union(drag.rect, { x: p.x - r - 2, y: p.y - r - 2, w: r * 2 + 4, h: r * 2 + 4 });
}
function paintTo(p, pr) {
  const a = drag.last; if (!a) { dabAt(p, pr); drag.last = { ...p, pr }; return; }
  const dist = Math.hypot(p.x - a.x, p.y - a.y), step = drag.tool === 'pencil' ? Math.max(0.5, Math.min(1, drag.o.size * 0.25)) : Math.max(0.5, drag.o.size * Math.min(pr, a.pr || 1) * 0.12);
  if (dist < step) return;
  const n = Math.floor(dist / step);
  for (let i = 1; i <= n; i++) { const t = i / n; dabAt({ x: a.x + (p.x - a.x) * t, y: a.y + (p.y - a.y) * t }, (a.pr || 1) + (pr - (a.pr || 1)) * t); }
  drag.last = { ...p, pr };
}
/** 筆劃（限喺選取範圍入面）疊喺圖層上面嘅樣。 */
function clippedStroke() {
  const g = ctx2d(clipC);
  g.globalCompositeOperation = 'copy'; g.drawImage(strokeC, 0, 0);
  // 仿製印章：筆劃形狀入面填返取樣位置嘅內容
  if (drag.clone) { g.globalCompositeOperation = 'source-in'; g.drawImage(drag.src, -drag.off.x, -drag.off.y); }
  if (doc.sel) { g.globalCompositeOperation = 'destination-in'; g.drawImage(doc.sel, 0, 0); }
  g.globalCompositeOperation = 'source-over';
  return clipC;
}
function liveLayer() {
  const g = ctx2d(liveC);
  g.globalCompositeOperation = 'copy'; g.globalAlpha = 1; g.drawImage(doc.layer.canvas, 0, 0);
  g.globalCompositeOperation = drag.erase ? 'destination-out' : 'source-over'; g.globalAlpha = drag.o.opacity;
  g.drawImage(clippedStroke(), 0, 0);
  g.globalCompositeOperation = 'source-over'; g.globalAlpha = 1;
  return liveC;
}
function finishStroke() {
  const d = drag;
  const g = doc.layer.ctx;
  g.globalCompositeOperation = d.erase ? 'destination-out' : 'source-over'; g.globalAlpha = d.o.opacity;
  g.drawImage(clippedStroke(), 0, 0);
  g.globalCompositeOperation = 'source-over'; g.globalAlpha = 1;
  lastPaint = d.last;
  if (!d.erase) usedColor(d.color);
  drag = null;
  d.end(d.rect);
}

// ---------- 填色 / 吸管 ----------
function fillAt(p) {
  const x = Math.floor(p.x), y = Math.floor(p.y), l = doc.layer;
  if (x < 0 || y < 0 || x >= doc.w || y >= doc.h) return;
  if (doc.sel && ctx2d(doc.sel).getImageData(x, y, 1, 1).data[3] === 0) return;
  if (l.text) rasterize(l, true);
  const r = floodRegion(l.ctx.getImageData(0, 0, doc.w, doc.h), x, y, opt.fill.tol, opt.fill.contiguous);
  const m = regionToMask(doc.w, doc.h, r.mask), g = ctx2d(m);
  g.globalCompositeOperation = 'source-in'; g.fillStyle = fg; g.fillRect(0, 0, doc.w, doc.h);
  if (doc.sel) { g.globalCompositeOperation = 'destination-in'; g.drawImage(doc.sel, 0, 0); }
  const end = doc.beginPixels(l, T('ph_t_fill'));
  usedColor(fg);
  const lg = l.ctx; lg.globalAlpha = opt.fill.opacity; lg.drawImage(m, 0, 0); lg.globalAlpha = 1;
  end(r.rect);
}
function pickAt(p, toBg) {
  const x = Math.floor(p.x), y = Math.floor(p.y);
  if (x < 0 || y < 0 || x >= doc.w || y >= doc.h) return;
  if (compDirty) compose();
  const d = ctx2d(comp).getImageData(x, y, 1, 1).data;
  const a = d[3] / 255, mix = v => Math.round(v * a + 255 * (1 - a));   // 透明嘅位置當白底
  const hex = '#' + [mix(d[0]), mix(d[1]), mix(d[2])].map(v => v.toString(16).padStart(2, '0')).join('');
  if (toBg) bg = hex; else fg = hex;
  syncColors();
}
function fillSel(color) {
  if (doc.layer.text) rasterize(doc.layer, true);
  const l = doc.layer, end = doc.beginPixels(l, T(color === fg ? 'ph_fillfg' : 'ph_fillbg')), g = l.ctx;
  if (doc.sel) { const c = canvas(doc.w, doc.h), cgx = ctx2d(c); cgx.drawImage(doc.sel, 0, 0); cgx.globalCompositeOperation = 'source-in'; cgx.fillStyle = color; cgx.fillRect(0, 0, doc.w, doc.h); g.drawImage(c, 0, 0); }
  else { g.fillStyle = color; g.fillRect(0, 0, doc.w, doc.h); }
  end(doc.selBounds());
}
function clearSel() {
  if (doc.layer.text) rasterize(doc.layer, true);
  const l = doc.layer, end = doc.beginPixels(l, T('ph_clear')), g = l.ctx;
  if (doc.sel) { g.globalCompositeOperation = 'destination-out'; g.drawImage(doc.sel, 0, 0); g.globalCompositeOperation = 'source-over'; }
  else g.clearRect(0, 0, doc.w, doc.h);
  end(doc.selBounds());
}

// ---------- 移動 ----------
function moveStart(p, e) {
  const l = doc.layer;
  if (!l.visible) { toast(T('ph_hidden')); return; }
  // 文字圖層：只係改位置，保持可以再改字
  if (l.text && !doc.sel) { drag = { kind: 'movetext', a: p, x0: l.text.x, y0: l.text.y, before: doc.state(), moved: false }; return; }
  if (l.text) rasterize(l, true);
  const base = cloneCanvas(l.canvas);
  let float = null;
  if (doc.sel) {
    float = cloneCanvas(l.canvas);
    const fg2 = ctx2d(float); fg2.globalCompositeOperation = 'destination-in'; fg2.drawImage(doc.sel, 0, 0);
    const bg2 = ctx2d(base); bg2.globalCompositeOperation = 'destination-out'; bg2.drawImage(doc.sel, 0, 0);
  }
  drag = { kind: 'move', a: p, dx: 0, dy: 0, base, float, sel: doc.sel, layer: l, end: doc.beginPixels(l, T('ph_t_move')) };
}
function moveTo(p, axis) {
  let dx = Math.round(p.x - drag.a.x), dy = Math.round(p.y - drag.a.y);
  if (axis) { if (Math.abs(dx) > Math.abs(dy)) dy = 0; else dx = 0; }
  if (dx === drag.dx && dy === drag.dy) return;
  drag.dx = dx; drag.dy = dy;
  drawMoved(drag);
  compDirty = true; req();
}
function drawMoved(d) {
  const g = d.layer.ctx;
  g.clearRect(0, 0, doc.w, doc.h);
  if (d.float) { g.drawImage(d.base, 0, 0); g.drawImage(d.float, d.dx, d.dy); }
  else g.drawImage(d.base, d.dx, d.dy);
}
function moveEnd() {
  const d = drag; drag = null;
  if (!d.dx && !d.dy) { req(); return; }
  doc.group(T('ph_t_move'), () => {
    d.end({ x: 0, y: 0, w: doc.w, h: doc.h });
    if (d.sel) { const m = canvas(doc.w, doc.h); ctx2d(m).drawImage(d.sel, d.dx, d.dy); doc.select(m, T('ph_t_move')); }
  });
  req();
}
function nudge(dx, dy) {
  const l = doc.layer;
  if (l.text && !doc.sel) { doc.change(T('ph_t_move'), () => { l.text = { ...l.text, x: l.text.x + dx, y: l.text.y + dy }; renderText(l); }); return; }
  moveStart({ x: 0, y: 0 });
  if (!drag) return;
  drag.dx = dx; drag.dy = dy; drawMoved(drag); moveEnd();
}
function moveText(p, axis) {
  const l = doc.layer;
  let dx = Math.round(p.x - drag.a.x), dy = Math.round(p.y - drag.a.y);
  if (axis) { if (Math.abs(dx) > Math.abs(dy)) dy = 0; else dx = 0; }
  l.text = { ...l.text, x: drag.x0 + dx, y: drag.y0 + dy };
  drag.moved = !!(dx || dy);
  renderText(l); compDirty = true; req();
}
/** 選取工具撳住方向鍵：移動選取框（唔郁像素）。 */
function nudgeSel(dx, dy) {
  if (!doc.sel) return;
  const m = canvas(doc.w, doc.h); ctx2d(m).drawImage(doc.sel, dx, dy);
  doc.select(m, T('ph_movesel'));
}

// ---------- 剪貼簿 ----------
function copy(cut, merged) {
  const src = merged ? doc.flatten() : doc.layer.canvas;
  const b = doc.sel ? maskBounds(doc.sel) : { x: 0, y: 0, w: doc.w, h: doc.h };
  if (!b) { toast(T('ph_nosel')); return; }
  const c = canvas(b.w, b.h), g = ctx2d(c);
  g.drawImage(src, -b.x, -b.y);
  if (doc.sel) { g.globalCompositeOperation = 'destination-in'; g.drawImage(doc.sel, -b.x, -b.y); }
  clip = { canvas: c, x: b.x, y: b.y };
  // 都放一份入系統剪貼簿（第二個程式可以貼）
  try { c.toBlob(bl => { try { bl && navigator.clipboard.write([new ClipboardItem({ 'image/png': bl })]).catch(() => { }); } catch { } }, 'image/png'); } catch { }
  if (cut && !merged) clearSel();
}
async function onPaste(e) {
  if (!$('editor').classList.contains('show') || /INPUT|TEXTAREA/.test(document.activeElement?.tagName || '')) return;
  const item = [...(e.clipboardData?.items || [])].find(i => i.type.startsWith('image/'));
  if (!item) { if (clip && doc) { e.preventDefault(); pasteCanvas(clip.canvas, clip.x, clip.y); } return; }
  e.preventDefault();
  const bmp = await createImageBitmap(item.getAsFile());
  // 系統剪貼簿入面係啱啱喺呢度複製嗰張：用返原本位置
  if (clip && doc && clip.canvas.width === bmp.width && clip.canvas.height === bmp.height) { pasteCanvas(clip.canvas, clip.x, clip.y); return; }
  const c = canvas(bmp.width, bmp.height); ctx2d(c).drawImage(bmp, 0, 0);
  if (!doc) { ui.fromCanvas(c, T('ph_pasted')); return; }
  pasteCanvas(c, Math.round((doc.w - c.width) / 2), Math.round((doc.h - c.height) / 2));
}
function pasteCanvas(c, x, y) {
  doc.change(T('paste'), () => {
    const l = new Layer(doc.w, doc.h, T('ph_pasted'));
    l.ctx.drawImage(c, x, y);
    doc.layers.splice(doc.active + 1, 0, l); doc.active++;
    doc.setSel(null, true);
  });
  setTool('move');
}

// ---------- 圖層 ----------
function newLayer() {
  doc.change(T('ph_newlayer'), () => { doc.layers.splice(doc.active + 1, 0, new Layer(doc.w, doc.h, T('ph_layer_n', doc.layers.length + 1))); doc.active++; });
}
function dupLayer() {
  doc.change(T('ph_duplayer'), () => {
    const s = doc.layer, l = new Layer(doc.w, doc.h, T('ph_copyof', s.name));
    l.canvas = cloneCanvas(s.canvas); l.opacity = s.opacity; l.blend = s.blend; l.visible = s.visible; l.text = s.text ? { ...s.text } : null;
    // 有選取範圍：Ctrl+J = 只複製揀咗嘅部分（拷貝的圖層）
    if (doc.sel && !s.text) { const g = ctx2d(l.canvas); g.globalCompositeOperation = 'destination-in'; g.drawImage(doc.sel, 0, 0); l.name = T('ph_layer_n', doc.layers.length + 1); }
    doc.layers.splice(doc.active + 1, 0, l); doc.active++;
  });
}
/** 剪下的圖層（Ctrl+Shift+J）：揀咗嘅部分搬去新圖層。 */
function layerViaCut() {
  if (!doc.sel) return;
  if (doc.layer.text) rasterize(doc.layer, true);
  doc.change(T('ph_layercut'), () => {
    const s = doc.layer, l = new Layer(doc.w, doc.h, T('ph_layer_n', doc.layers.length + 1));
    const g = l.ctx; g.drawImage(s.canvas, 0, 0); g.globalCompositeOperation = 'destination-in'; g.drawImage(doc.sel, 0, 0);
    const rest = cloneCanvas(s.canvas), rg = ctx2d(rest); rg.globalCompositeOperation = 'destination-out'; rg.drawImage(doc.sel, 0, 0);
    s.canvas = rest;
    doc.layers.splice(doc.active + 1, 0, l); doc.active++;
  });
}
function moveLayerTo(j) {
  const i = doc.active; j = clamp(j, 0, doc.layers.length - 1);
  if (i === j) return;
  doc.change(T(j > i ? 'ph_tofront' : 'ph_toback'), () => { const [x] = doc.layers.splice(i, 1); doc.layers.splice(j, 0, x); doc.active = j; });
}
/** Alt + ] / [：揀上面 / 下面嗰個圖層。 */
function pickLayer(dir) { const j = doc.active + dir; if (j < 0 || j >= doc.layers.length) return; if (editing) endText(true); doc.active = j; buildLayers(); req(); }
function mergeVisible() {
  doc.change(T('ph_mergevisible'), () => {
    const vis = doc.layers.filter(l => l.visible);
    if (vis.length < 2) return;
    const keep = vis[0], c = doc.flatten();
    doc.layers = doc.layers.filter(l => !l.visible || l === keep);
    keep.canvas = c; keep.opacity = 1; keep.blend = 'source-over'; keep.text = null;
    doc.active = doc.layers.indexOf(keep);
  });
}
/** 蓋印可見圖層（Ctrl+Alt+Shift+E）：所有看得見嘅合併做一個新圖層，原本嘅保留。 */
function stampVisible() {
  doc.change(T('ph_stamp'), () => {
    const l = new Layer(doc.w, doc.h, T('ph_layer_n', doc.layers.length + 1));
    l.canvas = doc.flatten();
    doc.layers.splice(doc.active + 1, 0, l); doc.active++;
  });
}
function rasterize(l, quiet) {
  if (!l.text) return;
  doc.change(T('ph_rasterize'), () => { l.text = null; });
  if (quiet) toast(T('ph_rasterized'), 1600);
}
function rotLayer(deg) {
  const l = doc.layer;
  if (l.text) rasterize(l, true);
  doc.change(T(deg === 180 ? 'ph_lrot_180' : deg > 0 ? 'ph_lrot_cw' : 'ph_lrot_ccw'), () => {
    // 圍住圖層內容嘅中心轉（唔係成張畫布）
    const b = maskBounds(l.canvas) || { x: 0, y: 0, w: doc.w, h: doc.h }, cx = b.x + b.w / 2, cy = b.y + b.h / 2;
    l.canvas = transformed(l.canvas, doc.w, doc.h, g => { g.translate(cx, cy); g.rotate(deg * Math.PI / 180); g.translate(-cx, -cy); });
  });
}
/** 修剪：剪走四邊透明嘅位。 */
function trim() {
  const f = doc.flatten(), b = maskBounds(f);
  if (!b) { toast(T('ph_trimempty')); return; }
  if (b.x === 0 && b.y === 0 && b.w === doc.w && b.h === doc.h) { toast(T('ph_trimnone')); return; }
  eachCanvas(T('ph_trim'), b.w, b.h, g => g.translate(-b.x, -b.y), true);
}
function delLayer() {
  if (doc.layers.length < 2) { toast(T('ph_lastlayer')); return; }
  doc.change(T('ph_dellayer'), () => { doc.layers.splice(doc.active, 1); doc.active = Math.max(0, doc.active - 1); });
}
function moveLayer(dir) {
  const i = doc.active, j = i + dir;
  if (j < 0 || j >= doc.layers.length) return;
  doc.change(T(dir > 0 ? 'ph_layerup' : 'ph_layerdown'), () => { [doc.layers[i], doc.layers[j]] = [doc.layers[j], doc.layers[i]]; doc.active = j; });
}
function mergeDown() {
  if (doc.active === 0) return;
  doc.change(T('ph_mergedown'), () => {
    const up = doc.layer, lo = doc.layers[doc.active - 1], c = cloneCanvas(lo.canvas), g = ctx2d(c);
    if (up.visible) { g.globalAlpha = up.opacity; g.globalCompositeOperation = up.blend; g.drawImage(up.canvas, 0, 0); }
    lo.canvas = c; lo.text = null;
    doc.layers.splice(doc.active, 1); doc.active--;
  });
}
function flatten() {
  doc.change(T('ph_flatten'), () => { const l = new Layer(doc.w, doc.h, T('ph_background')); l.canvas = doc.flatten(); doc.layers = [l]; doc.active = 0; });
}
function renameLayer(i) {
  const l = doc.layers[i], inp = el('input'); inp.type = 'text'; inp.value = l.name;
  dialog(T('ph_renamelayer'), field(T('ph_layername'), inp), [[T('cancel')], [T('ok'), () => { const v = inp.value.trim(); if (v && v !== l.name) doc.change(T('ph_renamelayer'), () => l.name = v); }, true]]);
}
function flipLayer(h) {
  if (doc.layer.text) rasterize(doc.layer, true);
  doc.change(T(h ? 'ph_lflip_h' : 'ph_lflip_v'), () => { const l = doc.layer; l.canvas = transformed(l.canvas, doc.w, doc.h, g => { if (h) { g.translate(doc.w, 0); g.scale(-1, 1); } else { g.translate(0, doc.h); g.scale(1, -1); } }); });
}

// ---------- 成張圖 ----------
function transformed(src, w, h, fn) { const c = canvas(w, h), g = ctx2d(c); g.imageSmoothingQuality = 'high'; fn(g); g.drawImage(src, 0, 0); return c; }
/** 每個圖層同選取範圍都換一張新 canvas（復原時用返舊嗰張）。 */
function eachCanvas(label, w, h, fn, dropSel) {
  doc.change(label, () => {
    for (const l of doc.layers) { l.canvas = transformed(l.canvas, w, h, g => fn(g, l.canvas)); l.text = null; }
    if (dropSel) doc.setSel(null, true);
    else if (doc.sel) doc.setSel(transformed(doc.sel, w, h, g => fn(g, doc.sel)), true);
    doc.w = w; doc.h = h;
  });
  comp = canvas(w, h); compDirty = true;
  fit();
}
function rotateDoc(deg) {
  const sw = deg === 180 ? doc.w : doc.h, sh = deg === 180 ? doc.h : doc.w, ow = doc.w, oh = doc.h;
  eachCanvas(T(deg === 90 ? 'ph_rot_cw' : deg === -90 ? 'ph_rot_ccw' : 'ph_rot_180'), sw, sh, g => {
    if (deg === 90) { g.translate(oh, 0); g.rotate(Math.PI / 2); }
    else if (deg === -90) { g.translate(0, ow); g.rotate(-Math.PI / 2); }
    else { g.translate(ow, oh); g.rotate(Math.PI); }
  });
}
function flipDoc(h) {
  const w = doc.w, hh = doc.h;
  eachCanvas(T(h ? 'ph_flip_h' : 'ph_flip_v'), w, hh, g => { if (h) { g.translate(w, 0); g.scale(-1, 1); } else { g.translate(0, hh); g.scale(1, -1); } });
}
function cropToSel() {
  const b = doc.sel && maskBounds(doc.sel);
  if (!b) { toast(T('ph_nosel')); return; }
  eachCanvas(T('ph_crop'), b.w, b.h, g => g.translate(-b.x, -b.y), true);
}
/** 高質縮細：每次最多縮一半，避免出鋸齒。 */
function resample(src, w, h) {
  let cur = src;
  while (cur.width / 2 >= w && cur.height / 2 >= h) { const c = canvas(cur.width / 2, cur.height / 2), g = ctx2d(c); g.imageSmoothingQuality = 'high'; g.drawImage(cur, 0, 0, c.width, c.height); cur = c; }
  const c = canvas(w, h), g = ctx2d(c); g.imageSmoothingQuality = 'high'; g.drawImage(cur, 0, 0, w, h);
  return c;
}
function imageSizeDlg() {
  const ratio = doc.w / doc.h;
  const wi = numInput(doc.w, 1, 30000), hi = numInput(doc.h, 1, 30000);
  const keep = el('input'); keep.type = 'checkbox'; keep.checked = true;
  wi.oninput = () => { if (keep.checked) hi.value = Math.max(1, Math.round(+wi.value / ratio)); };
  hi.oninput = () => { if (keep.checked) wi.value = Math.max(1, Math.round(+hi.value * ratio)); };
  const body = el('div', 'grid2');
  body.append(field(T('ph_width'), wi), field(T('ph_height'), hi));
  const kl = el('label', 'ocheck wide'); kl.append(keep, el('span', '', T('ph_keepratio'))); body.appendChild(kl);
  dialog(T('ph_imgsize'), body, [[T('cancel')], [T('ok'), () => {
    const w = clamp(Math.round(+wi.value), 1, 30000), h = clamp(Math.round(+hi.value), 1, 30000);
    if (w === doc.w && h === doc.h) return;
    if (w * h > 268e6) { toast(T('ph_toobig')); return false; }
    doc.change(T('ph_imgsize'), () => {
      for (const l of doc.layers) { l.canvas = resample(l.canvas, w, h); l.text = null; }
      if (doc.sel) doc.setSel(resample(doc.sel, w, h), true);
      doc.w = w; doc.h = h;
    });
    comp = canvas(w, h); compDirty = true; fit();
  }, true]]);
}
function canvasSizeDlg() {
  const wi = numInput(doc.w, 1, 30000), hi = numInput(doc.h, 1, 30000);
  let ax = 0.5, ay = 0.5;
  const grid = el('div', 'anchor');
  const draw = () => grid.querySelectorAll('button').forEach(b => b.classList.toggle('on', +b.dataset.x === ax && +b.dataset.y === ay));
  for (const y of [0, 0.5, 1]) for (const x of [0, 0.5, 1]) { const b = el('button', 'mini'); b.type = 'button'; b.dataset.x = x; b.dataset.y = y; b.onclick = () => { ax = x; ay = y; draw(); }; grid.appendChild(b); }
  draw();
  const body = el('div', 'grid2');
  body.append(field(T('ph_width'), wi), field(T('ph_height'), hi), field(T('ph_anchor'), grid));
  dialog(T('ph_canvassize'), body, [[T('cancel')], [T('ok'), () => {
    const w = clamp(Math.round(+wi.value), 1, 30000), h = clamp(Math.round(+hi.value), 1, 30000);
    if (w === doc.w && h === doc.h) return;
    if (w * h > 268e6) { toast(T('ph_toobig')); return false; }
    const dx = Math.round((w - doc.w) * ax), dy = Math.round((h - doc.h) * ay);
    eachCanvas(T('ph_canvassize'), w, h, g => g.translate(dx, dy));
  }, true]]);
}

// ---------- 自由變形（Ctrl+T）----------
const HANDLES = [[-1, -1], [0, -1], [1, -1], [1, 0], [1, 1], [0, 1], [-1, 1], [-1, 0]];
function startTransform() {
  if (!doc || xf) return;
  if (editing) endText(true);
  const l = doc.layer;
  if (!l.visible) { toast(T('ph_hidden')); return; }
  if (l.text) rasterize(l, true);
  const orig = cloneCanvas(l.canvas);
  let b, float, base;
  if (doc.sel) {
    b = maskBounds(doc.sel);
    float = cloneCanvas(l.canvas); const fg2 = ctx2d(float); fg2.globalCompositeOperation = 'destination-in'; fg2.drawImage(doc.sel, 0, 0);
    base = cloneCanvas(l.canvas); const bg2 = ctx2d(base); bg2.globalCompositeOperation = 'destination-out'; bg2.drawImage(doc.sel, 0, 0);
  } else { b = maskBounds(l.canvas); float = l.canvas; base = canvas(doc.w, doc.h); }
  if (!b) { toast(T('ph_xf_empty')); return; }
  const fc = canvas(b.w, b.h); ctx2d(fc).drawImage(float, -b.x, -b.y);
  let selC = null;
  if (doc.sel) { selC = canvas(b.w, b.h); ctx2d(selC).drawImage(doc.sel, -b.x, -b.y); }
  xf = { l, orig, base, float: fc, selC, b, cx: b.x + b.w / 2, cy: b.y + b.h / 2, sx: 1, sy: 1, rot: 0 };
  drawXfLayer(); buildOpts(); req();
}
function xfMatrix(x = xf) { return new DOMMatrix().translate(x.cx, x.cy).rotate(x.rot).scale(x.sx, x.sy).translate(-x.b.w / 2, -x.b.h / 2); }
function xfPts() { const m = xfMatrix(), w = xf.b.w, h = xf.b.h; return HANDLES.map(([hx, hy]) => m.transformPoint(new DOMPoint((hx + 1) / 2 * w, (hy + 1) / 2 * h))); }
function drawXfLayer() {
  const g = xf.l.ctx;
  g.setTransform(1, 0, 0, 1, 0, 0); g.clearRect(0, 0, doc.w, doc.h); g.drawImage(xf.base, 0, 0);
  g.imageSmoothingQuality = 'high'; g.setTransform(xfMatrix()); g.drawImage(xf.float, 0, 0); g.setTransform(1, 0, 0, 1, 0, 0);
  compDirty = true; req();
}
function drawXf() {
  const pts = xfPts().map(p => toScreen(p)), c = [0, 2, 4, 6].map(i => pts[i]);
  cg.save();
  cg.beginPath(); c.forEach((p, i) => i ? cg.lineTo(p.x, p.y) : cg.moveTo(p.x, p.y)); cg.closePath();
  cg.lineWidth = 1; cg.strokeStyle = '#2f7cf6'; cg.stroke();
  for (const p of pts) { cg.fillStyle = '#fff'; cg.fillRect(p.x - 4, p.y - 4, 8, 8); cg.strokeRect(p.x - 4.5, p.y - 4.5, 9, 9); }
  const ctr = toScreen({ x: xf.cx, y: xf.cy });
  cg.beginPath(); cg.arc(ctr.x, ctr.y, 4, 0, Math.PI * 2); cg.stroke();
  cg.restore();
}
function inPoly(p, poly) {
  let ins = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) { const a = poly[i], b = poly[j]; if ((a.y > p.y) !== (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) ins = !ins; }
  return ins;
}
function xfHit(p) {
  const s = toScreen(p), pts = xfPts();
  for (let i = 0; i < 8; i++) { const q = toScreen(pts[i]); if (Math.abs(q.x - s.x) <= 7 && Math.abs(q.y - s.y) <= 7) return { mode: 'scale', h: i }; }
  return inPoly(p, [0, 2, 4, 6].map(i => pts[i])) ? { mode: 'move' } : { mode: 'rotate' };
}
function xfDown(p, e) {
  const hit = xfHit(p);
  drag = { kind: 'xf', ...hit, a: p, s0: { cx: xf.cx, cy: xf.cy, sx: xf.sx, sy: xf.sy, rot: xf.rot } };
}
function xfMove(p, e) {
  const d = drag, s = d.s0;
  if (d.mode === 'move') {
    let dx = p.x - d.a.x, dy = p.y - d.a.y;
    if (e.shiftKey) { if (Math.abs(dx) > Math.abs(dy)) dy = 0; else dx = 0; }
    xf.cx = s.cx + dx; xf.cy = s.cy + dy;
  } else if (d.mode === 'rotate') {
    const a0 = Math.atan2(d.a.y - s.cy, d.a.x - s.cx), a1 = Math.atan2(p.y - s.cy, p.x - s.cx);
    let r = s.rot + (a1 - a0) * 180 / Math.PI;
    if (e.shiftKey) r = Math.round(r / 15) * 15;
    xf.rot = ((r + 540) % 360) - 180;
  } else {
    const [hx, hy] = HANDLES[d.h], rad = s.rot * Math.PI / 180, cos = Math.cos(rad), sin = Math.sin(rad);
    const vx = p.x - s.cx, vy = p.y - s.cy, qx = vx * cos + vy * sin, qy = -vx * sin + vy * cos;   // 轉返去未旋轉嘅座標
    const W = xf.b.w, H = xf.b.h, alt = e.altKey;
    const ax = alt ? 0 : -hx * s.sx * W / 2, ay = alt ? 0 : -hy * s.sy * H / 2;   // 固定唔郁嗰點
    let sx = s.sx, sy = s.sy;
    if (hx) sx = alt ? 2 * hx * qx / W : hx * (qx - ax) / W;
    if (hy) sy = alt ? 2 * hy * qy / H : hy * (qy - ay) / H;
    // 角位：預設保持比例（Shift = 自由），同而家嘅修圖軟件一樣
    if (hx && hy && !e.shiftKey) { const kx = sx / s.sx, ky = sy / s.sy, k = Math.abs(kx) > Math.abs(ky) ? kx : ky; sx = s.sx * k; sy = s.sy * k; }
    const minS = 1 / Math.max(W, H);
    if (Math.abs(sx) < minS) sx = minS * (sx < 0 ? -1 : 1);
    if (Math.abs(sy) < minS) sy = minS * (sy < 0 ? -1 : 1);
    const cxl = alt || !hx ? 0 : ax + hx * sx * W / 2, cyl = alt || !hy ? 0 : ay + hy * sy * H / 2;
    xf.sx = sx; xf.sy = sy;
    xf.cx = s.cx + cxl * cos - cyl * sin; xf.cy = s.cy + cxl * sin + cyl * cos;
  }
  drawXfLayer(); syncXfOpts();
}
function commitTransform() {
  if (!xf) return;
  const x = xf; xf = null;
  const same = x.sx === 1 && x.sy === 1 && !x.rot && x.cx === x.b.x + x.b.w / 2 && x.cy === x.b.y + x.b.h / 2;
  if (same) { const g = x.l.ctx; g.clearRect(0, 0, doc.w, doc.h); g.drawImage(x.orig, 0, 0); }
  else {
    const label = T('ph_freetransform'), full = { x: 0, y: 0, w: doc.w, h: doc.h };
    doc.group(label, () => {
      doc.push({ label, kind: 'pixels', layer: x.l, canvas: x.l.canvas, rect: full, before: ctx2d(x.orig).getImageData(0, 0, doc.w, doc.h), after: x.l.ctx.getImageData(0, 0, doc.w, doc.h) });
      if (x.selC) { const m = canvas(doc.w, doc.h), g = ctx2d(m); g.setTransform(xfMatrix(x)); g.drawImage(x.selC, 0, 0); doc.select(m, label); }
    });
    doc.onChange('struct');
  }
  compDirty = true; buildOpts(); req();
}
function cancelTransform() {
  if (!xf) return;
  const x = xf; xf = null;
  const g = x.l.ctx; g.clearRect(0, 0, doc.w, doc.h); g.drawImage(x.orig, 0, 0);
  compDirty = true; buildOpts(); req();
}
const xfIn = {};
function buildXfOpts(o) {
  o.appendChild(el('span', 'oname', T('ph_freetransform')));
  const add = (k, label, step) => { const w = el('label', 'onum'); w.appendChild(el('span', '', label)); const i = numInput(0, -1e6, 1e6, step); i.onchange = () => xfFromInputs(); w.appendChild(i); o.appendChild(w); xfIn[k] = i; };
  add('x', 'X', 1); add('y', 'Y', 1); add('w', 'W %', 0.1); add('h', 'H %', 0.1); add('r', '∠ °', 0.1);
  o.appendChild(obtn('✓ ' + T('ph_apply'), commitTransform, true));
  o.appendChild(obtn('✕ ' + T('cancel'), cancelTransform));
  o.appendChild(el('span', 'ohint', T('ph_h_xf')));
  syncXfOpts();
}
function syncXfOpts() {
  if (!xf || !xfIn.x) return;
  const v = { x: Math.round(xf.cx * 10) / 10, y: Math.round(xf.cy * 10) / 10, w: Math.round(xf.sx * 1000) / 10, h: Math.round(xf.sy * 1000) / 10, r: Math.round(xf.rot * 10) / 10 };
  for (const k in v) if (document.activeElement !== xfIn[k]) xfIn[k].value = v[k];
}
function xfFromInputs() {
  if (!xf) return;
  const n = k => +xfIn[k].value;
  if (isFinite(n('x'))) xf.cx = n('x'); if (isFinite(n('y'))) xf.cy = n('y');
  if (n('w')) xf.sx = n('w') / 100; if (n('h')) xf.sy = n('h') / 100;
  if (isFinite(n('r'))) xf.rot = n('r');
  drawXfLayer();
}

// ---------- 裁切工具 ----------
function cropRect() { const c = crop; return { x: Math.min(c.x, c.x + c.w), y: Math.min(c.y, c.y + c.h), w: Math.abs(c.w), h: Math.abs(c.h) }; }
function cropHandles() { const r = cropRect(); return HANDLES.map(([hx, hy]) => ({ x: r.x + (hx + 1) / 2 * r.w, y: r.y + (hy + 1) / 2 * r.h })); }
function cropDown(p, e) {
  if (!crop) crop = { x: 0, y: 0, w: doc.w, h: doc.h };
  const s = toScreen(p), hs = cropHandles();
  for (let i = 0; i < 8; i++) { const q = toScreen(hs[i]); if (Math.abs(q.x - s.x) <= 8 && Math.abs(q.y - s.y) <= 8) { drag = { kind: 'crop', mode: 'h', h: i, c0: cropRect(), a: p }; return; } }
  const r = cropRect();
  if (p.x >= r.x && p.y >= r.y && p.x <= r.x + r.w && p.y <= r.y + r.h) drag = { kind: 'crop', mode: 'move', c0: r, a: p };
  else drag = { kind: 'crop', mode: 'new', a: p };
}
function cropMove(p, e) {
  const d = drag;
  if (d.mode === 'new') {
    let w = p.x - d.a.x, h = p.y - d.a.y;
    if (e.shiftKey) { const s = Math.max(Math.abs(w), Math.abs(h)); w = Math.sign(w || 1) * s; h = Math.sign(h || 1) * s; }
    crop = { x: d.a.x, y: d.a.y, w, h };
  } else if (d.mode === 'move') crop = { ...d.c0, x: d.c0.x + p.x - d.a.x, y: d.c0.y + p.y - d.a.y };
  else {
    const [hx, hy] = HANDLES[d.h], c = d.c0;
    let x1 = c.x, y1 = c.y, x2 = c.x + c.w, y2 = c.y + c.h;
    if (hx < 0) x1 = p.x; if (hx > 0) x2 = p.x; if (hy < 0) y1 = p.y; if (hy > 0) y2 = p.y;
    if (e.shiftKey && hx && hy) { const k = c.w / c.h, w = x2 - x1, h = (Math.abs(w) / k) * Math.sign(y2 - y1 || 1); if (hy < 0) y1 = y2 - h; else y2 = y1 + h; }
    crop = { x: x1, y: y1, w: x2 - x1, h: y2 - y1 };
  }
  req();
}
function normCrop() { const r = cropRect(); crop = { x: Math.round(r.x), y: Math.round(r.y), w: Math.max(1, Math.round(r.w)), h: Math.max(1, Math.round(r.h)) }; }
function drawCrop() {
  const r = cropRect(), a = toScreen(r), b = toScreen({ x: r.x + r.w, y: r.y + r.h });
  cg.save();
  cg.beginPath(); cg.rect(0, 0, W, H); cg.rect(a.x, a.y, b.x - a.x, b.y - a.y);
  cg.fillStyle = 'rgba(0,0,0,.55)'; cg.fill('evenodd');
  cg.strokeStyle = 'rgba(255,255,255,.5)'; cg.lineWidth = 1; cg.beginPath();
  for (const t of [1 / 3, 2 / 3]) { const x = a.x + (b.x - a.x) * t, y = a.y + (b.y - a.y) * t; cg.moveTo(x, a.y); cg.lineTo(x, b.y); cg.moveTo(a.x, y); cg.lineTo(b.x, y); }
  cg.stroke();
  cg.strokeStyle = '#fff'; cg.lineWidth = 1.5; cg.strokeRect(a.x, a.y, b.x - a.x, b.y - a.y);
  for (const h of cropHandles()) { const q = toScreen(h); cg.fillStyle = '#fff'; cg.fillRect(q.x - 4, q.y - 4, 8, 8); }
  cg.fillStyle = '#fff'; cg.font = '12px sans-serif'; cg.fillText(Math.round(r.w) + ' × ' + Math.round(r.h), a.x + 4, a.y - 6);
  cg.restore();
}
function commitCrop() {
  if (!crop) return;
  normCrop();
  const r = crop;
  if (r.x === 0 && r.y === 0 && r.w === doc.w && r.h === doc.h) return;
  if (r.w * r.h > 268e6) { toast(T('ph_toobig')); return; }
  eachCanvas(T('ph_crop'), r.w, r.h, g => g.translate(-r.x, -r.y), true);
  crop = { x: 0, y: 0, w: doc.w, h: doc.h }; req();
}

// ---------- 多邊形套索 ----------
function polyClick(p, e) {
  if (!poly) { poly = { pts: [p], mode: selModeFor(e) }; req(); return; }
  const f = toScreen(poly.pts[0]), s = toScreen(p);
  if (poly.pts.length >= 3 && Math.hypot(f.x - s.x, f.y - s.y) < 8) { closePoly(); return; }
  poly.pts.push(e.shiftKey ? snap45(poly.pts[poly.pts.length - 1], p) : p); req();
}
function closePoly() {
  const q = poly; poly = null;
  if (!q || q.pts.length < 3) { req(); return; }
  const path = new Path2D(); q.pts.forEach((v, i) => i ? path.lineTo(v.x, v.y) : path.moveTo(v.x, v.y)); path.closePath();
  doc.select(shapeMask(doc.w, doc.h, path, q.mode, doc.sel), T('ph_t_polylasso'));
  req();
}
function snap45(a, p) {
  const dx = p.x - a.x, dy = p.y - a.y, ang = Math.round(Math.atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4), len = Math.hypot(dx, dy);
  return { x: a.x + Math.cos(ang) * len, y: a.y + Math.sin(ang) * len };
}

// ---------- 漸層 ----------
function hexA(hex, a) { const n = parseInt(hex.slice(1), 16); return `rgba(${n >> 16 & 255},${n >> 8 & 255},${n & 255},${a})`; }
function applyGradient(a, b) {
  const len = Math.hypot(b.x - a.x, b.y - a.y);
  if (len < 1) return;
  const l = doc.layer;
  if (!l.visible) { toast(T('ph_hidden')); return; }
  if (l.text) rasterize(l, true);
  const o = opt.gradient, c = canvas(doc.w, doc.h), g = ctx2d(c);
  const gr = o.type === 'radial' ? g.createRadialGradient(a.x, a.y, 0, a.x, a.y, len) : g.createLinearGradient(a.x, a.y, b.x, b.y);
  let c0 = hexA(fg, 1), c1 = o.mode === 'fgbg' ? hexA(bg, 1) : hexA(fg, 0);
  if (o.reverse) [c0, c1] = [c1, c0];
  gr.addColorStop(0, c0); gr.addColorStop(1, c1);
  g.fillStyle = gr; g.fillRect(0, 0, doc.w, doc.h);
  if (doc.sel) { g.globalCompositeOperation = 'destination-in'; g.drawImage(doc.sel, 0, 0); }
  const end = doc.beginPixels(l, T('ph_t_gradient')), lg = l.ctx;
  lg.globalAlpha = o.opacity; lg.drawImage(c, 0, 0); lg.globalAlpha = 1;
  usedColor(fg);
  end(doc.selBounds());
}

// ---------- 形狀 ----------
function shapePath(kind, a, b, shift, alt) {
  const p = new Path2D();
  if (kind === 'shline') { const e = shift ? snap45(a, b) : b; p.moveTo(a.x, a.y); p.lineTo(e.x, e.y); return p; }
  let dx = b.x - a.x, dy = b.y - a.y;
  if (shift) { const s = Math.max(Math.abs(dx), Math.abs(dy)); dx = Math.sign(dx || 1) * s; dy = Math.sign(dy || 1) * s; }
  let x = a.x, y = a.y, w = dx, h = dy;
  if (alt) { x = a.x - dx; y = a.y - dy; w = dx * 2; h = dy * 2; }
  const rx = Math.min(x, x + w), ry = Math.min(y, y + h), rw = Math.abs(w), rh = Math.abs(h);
  if (kind === 'shellipse') p.ellipse(rx + rw / 2, ry + rh / 2, rw / 2, rh / 2, 0, 0, Math.PI * 2);
  else p.rect(rx, ry, rw, rh);
  return p;
}
function drawShape(g, kind, a, b, shift, alt) {
  const p = shapePath(kind, a, b, shift, alt), o = opt.shape;
  g.save();
  if (kind === 'shline' || o.mode === 'stroke') { g.lineWidth = o.width; g.strokeStyle = fg; g.lineJoin = 'miter'; g.lineCap = kind === 'shline' ? 'round' : 'butt'; g.stroke(p); }
  else { g.fillStyle = fg; g.fill(p); }
  g.restore();
}
function applyShape(d) {
  if (Math.hypot(d.b.x - d.a.x, d.b.y - d.a.y) < 1) return;
  const n = doc.layers.length + 1;
  doc.change(T('ph_t_' + d.tool), () => {
    const l = new Layer(doc.w, doc.h, T('ph_t_' + d.tool) + ' ' + n);
    drawShape(l.ctx, d.tool, d.a, d.b, d.shift, d.alt);
    doc.layers.splice(doc.active + 1, 0, l); doc.active++;
  });
  usedColor(fg);
}

// ---------- 文字 ----------
const fontStr = t => (t.italic ? 'italic ' : '') + (t.bold ? 'bold ' : '') + t.size + 'px "' + t.font + '", sans-serif';
const lineH = t => t.size * 1.25;
let measureC = null;
function metrics(t) {
  if (!measureC) measureC = ctx2d(canvas(1, 1));
  measureC.font = fontStr(t);
  const lines = t.str.split('\n'), m = measureC.measureText('Hg國');
  const asc = m.fontBoundingBoxAscent || t.size * 0.9, desc = m.fontBoundingBoxDescent || t.size * 0.25;
  return { lines, asc, desc, width: Math.max(t.size * 0.5, ...lines.map(s => measureC.measureText(s).width)) };
}
function renderText(l) {
  const t = l.text, c = canvas(doc.w, doc.h), g = ctx2d(c);
  g.font = fontStr(t); g.fillStyle = t.color; g.textAlign = t.align; g.textBaseline = 'alphabetic';
  t.str.split('\n').forEach((s, i) => g.fillText(s, t.x, t.y + i * lineH(t)));
  l.canvas = c;
}
function textBounds(l) {
  const t = l.text, m = metrics(t), x = t.align === 'center' ? t.x - m.width / 2 : t.align === 'right' ? t.x - m.width : t.x;
  return { x, y: t.y - m.asc, w: m.width, h: m.asc + m.desc + (m.lines.length - 1) * lineH(t) };
}
function textDown(p, e) {
  if (editing) {
    const b = textBounds(editing.l);
    if (p.x >= b.x - 4 && p.x <= b.x + b.w + 4 && p.y >= b.y - 4 && p.y <= b.y + b.h + 4) { e.preventDefault(); editing.ta.focus(); return; }
    endText(true);
  }
  e.preventDefault();
  for (let i = doc.layers.length - 1; i >= 0; i--) {
    const l = doc.layers[i];
    if (!l.text || !l.visible) continue;
    const b = textBounds(l);
    if (p.x >= b.x && p.x <= b.x + b.w && p.y >= b.y && p.y <= b.y + b.h) { doc.active = i; buildLayers(); editText(l, false); return; }
  }
  const before = doc.state(), o = opt.text;
  const l = new Layer(doc.w, doc.h, T('ph_t_text'));
  l.text = { str: '', font: o.font, size: o.size, bold: o.bold, italic: o.italic, align: o.align, color: fg, x: Math.round(p.x), y: Math.round(p.y) };
  renderText(l);
  doc.layers.splice(doc.active + 1, 0, l); doc.active++;
  buildLayers();
  editText(l, true, before);
}
function editText(l, isNew, before) {
  if (editing) endText(true);
  const ta = el('textarea', 'phtext'); ta.spellcheck = false; ta.value = l.text.str;
  editing = { l, isNew, before: before || doc.state(), ta };
  Object.assign(opt.text, { font: l.text.font, size: l.text.size, bold: l.text.bold, italic: l.text.italic, align: l.text.align });
  $('stage').appendChild(ta);
  ta.oninput = () => { l.text = { ...l.text, str: ta.value }; renderText(l); placeText(); compDirty = true; req(); };
  ta.onkeydown = e => {
    e.stopPropagation();
    if (e.key === 'Escape') { e.preventDefault(); endText(false); }
    else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey || e.code === 'NumpadEnter')) { e.preventDefault(); endText(true); }
  };
  ta.onpointerdown = e => e.stopPropagation();
  placeText(); buildOpts();
  ta.focus(); ta.setSelectionRange(ta.value.length, ta.value.length);
  compDirty = true; req();
}
function placeText() {
  if (!editing) return;
  const { l, ta } = editing, t = l.text, m = metrics(t), z = view.zoom, L = lineH(t);
  const b = textBounds(l), w = (m.width + t.size) * z;
  const top = view.oy + (t.y - (L - m.asc - m.desc) / 2 - m.asc) * z;
  const left = view.ox + (t.align === 'center' ? t.x * z - w / 2 : t.align === 'right' ? t.x * z - w : t.x * z);
  Object.assign(ta.style, {
    left: left + 'px', top: top + 'px', width: w + 'px', height: (m.lines.length * L * z + 4) + 'px',
    font: (t.italic ? 'italic ' : '') + (t.bold ? 'bold ' : '') + t.size * z + 'px "' + t.font + '", sans-serif',
    lineHeight: L * z + 'px', textAlign: t.align, caretColor: t.color,
  });
  void b;
}
function endText(commit) {
  const e = editing; if (!e) return;
  editing = null; e.ta.remove();
  const l = e.l;
  if (!commit) { doc.restore(e.before); doc.onChange('struct'); buildOpts(); return; }
  if (!l.text || !l.text.str.trim()) {
    // 冇字：唔留低空白圖層
    doc.restore(e.before);
    if (!e.isNew && doc.layers.includes(l) && doc.layers.length > 1) doc.change(T('ph_dellayer'), () => { const i = doc.layers.indexOf(l); doc.layers.splice(i, 1); doc.active = Math.max(0, Math.min(doc.active, doc.layers.length - 1)); });
    doc.onChange('struct'); buildOpts(); return;
  }
  if (e.isNew || l.name === T('ph_t_text') || l.autoName) { l.name = l.text.str.split('\n')[0].slice(0, 30); l.autoName = true; }
  doc.push({ label: T(e.isNew ? 'ph_t_text' : 'ph_edittext'), kind: 'state', before: e.before, after: doc.state() });
  usedColor(l.text.color);
  doc.onChange('struct'); buildOpts();
}
function buildTextOpts(o) {
  const t = opt.text;
  const fs = el('select', 'psel'); fs.title = T('ph_font');
  const fonts = FONTS.includes(t.font) ? FONTS : [t.font, ...FONTS];
  for (const f of fonts) { const op = el('option', '', f); op.value = f; op.style.fontFamily = f; fs.appendChild(op); }
  fs.value = t.font; fs.onchange = () => setTextOpt('font', fs.value);
  o.appendChild(fs);
  const sz = numInput(t.size, 1, 2000); sz.title = T('ph_fontsize'); sz.className = 'osize'; sz.onchange = () => setTextOpt('size', clamp(+sz.value || 12, 1, 2000));
  const szw = el('label', 'onum'); szw.append(el('span', '', T('ph_fontsize')), sz); o.appendChild(szw);
  const tog = (k, label, cls) => { const b = el('button', 'rb ' + cls + (t[k] ? ' on' : ''), label); b.type = 'button'; b.title = T('ph_' + k); b.onclick = () => setTextOpt(k, !t[k]); return b; };
  const g = el('div', 'oseg'); g.append(tog('bold', 'B', 'tb'), tog('italic', 'I', 'ti')); o.appendChild(g);
  o.appendChild(seg([['left', '⯇'], ['center', '≡'], ['right', '⯈']], () => t.align, v => setTextOpt('align', v)));
  const col = el('input'); col.type = 'color'; col.className = 'ocolor'; col.title = T('ph_textcolor');
  col.value = editing ? editing.l.text.color : doc && doc.layer && doc.layer.text ? doc.layer.text.color : fg;
  col.oninput = () => { if (editing) setTextOpt('color', col.value, true); };
  col.onchange = () => { if (!editing) setTextOpt('color', col.value); };
  o.appendChild(col);
}
function setTextOpt(k, v, live) {
  if (k !== 'color') opt.text[k] = v; else { fg = v; syncColors(); }
  const l = editing ? editing.l : doc && doc.layer && doc.layer.text ? doc.layer : null;
  if (l) {
    const apply = () => { l.text = { ...l.text, [k]: v }; renderText(l); };
    if (editing) { apply(); placeText(); compDirty = true; req(); }
    else doc.change(T('ph_textstyle'), apply);
  }
  if (!live) buildOpts();
  if (editing) editing.ta.focus();
}

// ---------- 調整 / 濾鏡 ----------
/** 開始改圖層像素（有預覽）：run(fn, p) 用原本像素計一次放返入去，commit() 記入復原，cancel() 還原。 */
function beginPreview(label) {
  const l = doc.layer;
  if (!l.visible) { toast(T('ph_hidden')); return null; }
  // 文字圖層要先點陣化；撳取消就還原返做文字
  const rz = !!l.text;
  if (rz) rasterize(l, true);
  const b = doc.sel ? maskBounds(doc.sel) : { x: 0, y: 0, w: doc.w, h: doc.h };
  if (!b) { toast(T('ph_nosel')); return null; }
  const src = l.ctx.getImageData(b.x, b.y, b.w, b.h), mask = doc.sel ? ctx2d(doc.sel).getImageData(b.x, b.y, b.w, b.h).data : null;
  const end = doc.beginPixels(l, label);
  let shown = false;
  return {
    run(fn, p) {
      const out = new ImageData(new Uint8ClampedArray(src.data), b.w, b.h);
      fn(out, p || {});
      if (mask) { const o = out.data, s = src.data; for (let i = 0; i < o.length; i += 4) { const k = mask[i + 3] / 255; if (k < 1) for (let c = 0; c < 4; c++) o[i + c] = s[i + c] + (o[i + c] - s[i + c]) * k; } }
      l.ctx.putImageData(out, b.x, b.y); shown = true;
      compDirty = true; req();
    },
    restore() { if (shown) { l.ctx.putImageData(src, b.x, b.y); shown = false; compDirty = true; req(); } },
    cancel() { if (this.cancelled) return; this.cancelled = true; this.restore(); if (rz && doc.undoStack.length && doc.undoStack[doc.undoStack.length - 1].label === T('ph_rasterize')) { doc.undo(); doc.redoStack.pop(); } },
    commit() { end(b); },
    get shown() { return shown; },
  };
}
const FILTERS = new Set(['gauss', 'motion', 'unsharp', 'noise', 'median', 'mosaic', 'emboss', 'highpass']);
const ADJ = {
  bc: { t: 'ph_a_bc', fn: A.brightnessContrast, c: [['b', 'ph_brightness', -150, 150, 1, 0], ['c', 'ph_contrast', -50, 100, 1, 0]] },
  exposure: { t: 'ph_a_exposure', fn: A.exposure, c: [['ev', 'ph_exposure', -5, 5, 0.01, 0], ['offset', 'ph_offset', -0.5, 0.5, 0.001, 0], ['gamma', 'ph_gamma', 0.1, 4, 0.01, 1]] },
  vibrance: { t: 'ph_a_vibrance', fn: A.vibrance, c: [['vib', 'ph_vibrance', -100, 100, 1, 0], ['sat', 'ph_saturation', -100, 100, 1, 0]] },
  huesat: { t: 'ph_a_huesat', fn: A.hueSat, c: [['h', 'ph_hue', -180, 180, 1, 0], ['s', 'ph_saturation', -100, 100, 1, 0], ['l', 'ph_lightness', -100, 100, 1, 0]], k: [['colorize', 'ph_colorize']] },
  balance: { t: 'ph_a_balance', fn: A.colorBalance, custom: balanceUi },
  bw: { t: 'ph_a_bw', fn: A.blackWhite, c: [['r', 'ph_c_red', -200, 300, 1, 40], ['y', 'ph_c_yellow', -200, 300, 1, 60], ['g', 'ph_c_green', -200, 300, 1, 40], ['c', 'ph_c_cyan', -200, 300, 1, 60], ['b', 'ph_c_blue', -200, 300, 1, 20], ['m', 'ph_c_magenta', -200, 300, 1, 80]], tint: true },
  photofilter: { t: 'ph_a_photofilter', fn: A.photoFilter, c: [['density', 'ph_density', 1, 100, 1, 25]], k: [['keepLum', 'ph_keeplum', true]], color: ['color', '#ec8a00'] },
  posterize: { t: 'ph_a_posterize', fn: A.posterize, c: [['n', 'ph_levelsn', 2, 64, 1, 4]] },
  threshold: { t: 'ph_a_threshold', fn: A.threshold, c: [['t', 'ph_threshold', 1, 255, 1, 128]] },
  gauss: { t: 'ph_f_gauss', fn: A.gaussian, c: [['r', 'ph_radius', 0.1, 250, 0.1, 4]] },
  motion: { t: 'ph_f_motion', fn: A.motionBlur, c: [['angle', 'ph_angle', -180, 180, 1, 0], ['dist', 'ph_distance', 1, 200, 1, 10]] },
  unsharp: { t: 'ph_f_unsharp', fn: A.unsharp, c: [['amount', 'ph_amount', 1, 500, 1, 100], ['r', 'ph_radius', 0.1, 50, 0.1, 1.5], ['th', 'ph_threshold', 0, 255, 1, 0]] },
  noise: { t: 'ph_f_addnoise', fn: A.addNoise, c: [['amount', 'ph_amount', 0, 100, 0.5, 10]], k: [['mono', 'ph_mono', true]] },
  median: { t: 'ph_f_median', fn: A.median, c: [['r', 'ph_radius', 1, 5, 1, 1]] },
  mosaic: { t: 'ph_f_mosaic', fn: A.mosaic, c: [['cell', 'ph_cell', 2, 200, 1, 10]] },
  emboss: { t: 'ph_f_emboss', fn: A.emboss, c: [['angle', 'ph_angle', -180, 180, 1, 135], ['height', 'ph_height2', 1, 10, 1, 2], ['amount', 'ph_amount', 1, 500, 1, 100]] },
  highpass: { t: 'ph_f_highpass', fn: A.highPass, c: [['r', 'ph_radius', 0.1, 100, 0.1, 10]] },
};
/** 一行「標籤 + 拉桿 + 數字」。 */
function ctlRow(label, min, max, step, val, on) {
  const w = el('div', 'actl');
  w.appendChild(el('label', '', label));
  const r = el('input'); r.type = 'range'; r.min = min; r.max = max; r.step = step; r.value = val;
  const n = numInput(val, min, max, step);
  r.oninput = () => { n.value = r.value; on(+r.value); };
  n.oninput = () => { const v = clamp(+n.value, min, max); if (isFinite(v)) { r.value = v; on(v); } };
  w.append(r, n);
  w.set = v => { r.value = v; n.value = v; };
  return w;
}
function checkRow(label, val, on) { const w = el('label', 'ocheck acheck'); const c = el('input'); c.type = 'checkbox'; c.checked = val; c.onchange = () => on(c.checked); w.append(c, el('span', '', label)); w.set = v => c.checked = v; return w; }
/** 調整 / 濾鏡對話框：拉桿改數值，即時睇效果。 */
function adjust(id, preset) {
  const def = ADJ[id], label = T(def.t);
  const s = beginPreview(label); if (!s) return;
  const p = preset ? JSON.parse(JSON.stringify(preset)) : {};
  if (!preset) { for (const [k, , , , , v] of def.c || []) p[k] = v; for (const [k, , v] of def.k || []) p[k] = !!v; if (def.color) p[def.color[0]] = def.color[1]; }
  let preview = true, pend = 0;
  const refresh = () => { if (!preview) return; cancelAnimationFrame(pend); pend = requestAnimationFrame(() => { $('stage').classList.add('busy'); setTimeout(() => { s.run(def.fn, p); $('stage').classList.remove('busy'); }, 0); }); };
  const body = el('div', 'adlg');
  const rows = {};
  if (def.custom) def.custom(body, p, refresh, preset);
  for (const [k, lk, mn, mx, st] of def.c || []) body.appendChild(rows[k] = ctlRow(T(lk), mn, mx, st, p[k], v => { p[k] = v; refresh(); }));
  for (const [k, lk] of def.k || []) body.appendChild(rows[k] = checkRow(T(lk), p[k], v => {
    p[k] = v;
    // 上色：同常見做法一樣，由色相 0、飽和度 25 開始
    if (k === 'colorize') { p.h = v ? 0 : 0; p.s = v ? 25 : 0; rows.h.set(p.h); rows.s.set(p.s); }
    refresh();
  }));
  if (def.color) { const ci = el('input'); ci.type = 'color'; ci.value = p[def.color[0]]; ci.oninput = () => { p[def.color[0]] = ci.value; refresh(); }; body.appendChild(field(T('ph_color'), ci)); }
  if (def.tint) {
    const ci = el('input'); ci.type = 'color'; ci.value = p.tintC || '#e1d3b3'; ci.disabled = !p.tint;
    body.appendChild(checkRow(T('ph_tint'), !!p.tint, v => { ci.disabled = !v; p.tint = v ? ci.value : null; p.tintC = ci.value; refresh(); }));
    ci.oninput = () => { p.tintC = ci.value; if (p.tint) { p.tint = ci.value; refresh(); } };
    body.appendChild(ci);
  }
  body.appendChild(checkRow(T('vc_preview'), true, v => { preview = v; if (v) refresh(); else s.restore(); }));
  refresh();
  dialog(label, body, [[T('cancel'), () => { cancelAnimationFrame(pend); s.cancel(); }], [T('ok'), () => {
    cancelAnimationFrame(pend);
    s.run(def.fn, p); s.commit();
    if (FILTERS.has(id)) lastFilter = { id, title: label, p: JSON.parse(JSON.stringify(p)) };
  }, true]]);
  fixDlgCancel(s);
}
/** 撳對話框外面 / Esc 閂都要還原預覽。 */
function fixDlgCancel(s) {
  const all = document.querySelectorAll('.rmask'), m = all[all.length - 1];
  if (!m) return;
  m.classList.add('clear'); m.onclick = null;   // 撳後面唔會閂（同其他修圖軟件一樣）
  const obs = new MutationObserver(() => { if (!m.isConnected) { obs.disconnect(); if (!s.done) s.cancel(); } });
  obs.observe(document.body, { childList: true });
  const c = s.commit; s.commit = () => { s.done = true; c(); };
}
function balanceUi(body, p, refresh) {
  p.sh = p.sh || [0, 0, 0]; p.mid = p.mid || [0, 0, 0]; p.hi = p.hi || [0, 0, 0]; if (p.keepLum === undefined) p.keepLum = true;
  let tone = 'mid';
  const rows = [['ph_c_cyan', 'ph_c_red'], ['ph_c_magenta', 'ph_c_green'], ['ph_c_yellow', 'ph_c_blue']].map(([a, b], i) => ctlRow(T(a) + ' ⇄ ' + T(b), -100, 100, 1, p[tone][i], v => { p[tone][i] = v; refresh(); }));
  const g = el('div', 'oseg');
  const segBtns = [['sh', 'ph_shadows'], ['mid', 'ph_midtones'], ['hi', 'ph_highlights']].map(([k, lk]) => {
    const b = el('button', 'rb' + (k === tone ? ' on' : ''), T(lk)); b.type = 'button';
    b.onclick = () => { tone = k; segBtns.forEach(x => x.classList.toggle('on', x === b)); rows.forEach((r, i) => r.set(p[tone][i])); };
    return b;
  });
  g.append(...segBtns);
  body.appendChild(g);
  rows.forEach(r => body.appendChild(r));
  body.appendChild(checkRow(T('ph_keeplum'), p.keepLum, v => { p.keepLum = v; refresh(); }));
}
/** 唔使對話框嘅指令（負片、去色、自動…）。 */
function applyNow(label, fn) {
  const s = beginPreview(label); if (!s) return;
  s.run(fn, {}); s.commit();
  if (fn === A.sharpen || fn === A.findEdges) lastFilter = { fn, title: label };
}
function repeatFilter() {
  if (!lastFilter) return;
  if (lastFilter.fn) { applyNow(lastFilter.title, lastFilter.fn); return; }
  const def = ADJ[lastFilter.id], s = beginPreview(lastFilter.title); if (!s) return;
  s.run(def.fn, lastFilter.p); s.commit();
}
/** 色階（Ctrl+L）：直方圖 + 輸入 / 輸出黑白點、中間調。 */
function levelsDlg() {
  const s = beginPreview(T('ph_a_levels')); if (!s) return;
  const l = doc.layer, b = doc.selBounds(), hist = A.histogram(l.ctx.getImageData(b.x, b.y, b.w, b.h));
  const p = { ch: 'rgb', inB: 0, inW: 255, gamma: 1, outB: 0, outW: 255 };
  let preview = true;
  let pend = 0; const refresh = () => { if (!preview) return; cancelAnimationFrame(pend); pend = requestAnimationFrame(() => s.run(A.levels, p)); };
  const body = el('div', 'adlg');
  const chSel = el('select', 'psel');
  for (const [v, k] of [['rgb', 'RGB'], ['r', T('ph_c_red')], ['g', T('ph_c_green')], ['b', T('ph_c_blue')]]) { const o = el('option', '', k); o.value = v; chSel.appendChild(o); }
  const hc = el('canvas', 'histo'); hc.width = 256; hc.height = 100;
  const drawH = () => {
    const g = hc.getContext('2d'), h = { rgb: hist.l, r: hist.r, g: hist.g, b: hist.b }[p.ch]; let mx = 1; for (const v of h) mx = Math.max(mx, v);
    g.clearRect(0, 0, 256, 100); g.fillStyle = getComputedStyle(document.documentElement).getPropertyValue('--r-text') || '#ccc';
    for (let i = 0; i < 256; i++) { const v = Math.sqrt(h[i] / mx) * 100; g.fillRect(i, 100 - v, 1, v); }
    g.fillStyle = '#2f7cf6'; for (const x of [p.inB, p.inW, p.inB + (p.inW - p.inB) * Math.pow(0.5, p.gamma)]) g.fillRect(Math.round(x) - 1, 0, 2, 100);
  };
  chSel.onchange = () => { p.ch = chSel.value; drawH(); refresh(); };
  body.append(field(T('ph_channel'), chSel), hc);
  const rows = [
    ['inB', 'ph_inblack', 0, 254, 1], ['gamma', 'ph_midtone', 0.1, 9.99, 0.01], ['inW', 'ph_inwhite', 1, 255, 1], ['outB', 'ph_outblack', 0, 255, 1], ['outW', 'ph_outwhite', 0, 255, 1],
  ].map(([k, lk, mn, mx, st]) => { const r = ctlRow(T(lk), mn, mx, st, p[k], v => { p[k] = v; if (p.inW <= p.inB) p.inW = p.inB + 1; drawH(); refresh(); }); r.k = k; body.appendChild(r); return r; });
  const auto = obtn(T('ph_auto'), () => {
    const h = hist.l; let total = 0; for (const v of h) total += v; const cut = total * 0.001; let a = 0, z = 255, acc = 0;
    while (a < 254 && (acc += h[a]) <= cut) a++; acc = 0; while (z > a + 1 && (acc += h[z]) <= cut) z--;
    Object.assign(p, { ch: 'rgb', inB: a, inW: z, gamma: 1 }); chSel.value = 'rgb'; rows.forEach(r => r.set(p[r.k])); drawH(); refresh();
  });
  body.appendChild(auto);
  body.appendChild(checkRow(T('vc_preview'), true, v => { preview = v; if (v) refresh(); else s.restore(); }));
  drawH(); refresh();
  dialog(T('ph_a_levels'), body, [[T('cancel'), () => s.cancel()], [T('ok'), () => { s.run(A.levels, p); s.commit(); }, true]]);
  fixDlgCancel(s);
}
/** 曲線（Ctrl+M）：撳加點、拖曳移動、拖出框外刪除。 */
function curvesDlg() {
  const s = beginPreview(T('ph_a_curves')); if (!s) return;
  const pts = { rgb: [[0, 0], [255, 255]], r: [[0, 0], [255, 255]], g: [[0, 0], [255, 255]], b: [[0, 0], [255, 255]] };
  let ch = 'rgb', sel = -1, preview = true;
  const run = img => { A.curves(img, { pts: pts.rgb }); for (const c of ['r', 'g', 'b']) A.curves(img, { pts: pts[c], ch: c }); return img; };
  let pend = 0; const refresh = () => { if (!preview) return; cancelAnimationFrame(pend); pend = requestAnimationFrame(() => s.run(run)); };
  const body = el('div', 'adlg');
  const chSel = el('select', 'psel');
  for (const [v, k] of [['rgb', 'RGB'], ['r', T('ph_c_red')], ['g', T('ph_c_green')], ['b', T('ph_c_blue')]]) { const o = el('option', '', k); o.value = v; chSel.appendChild(o); }
  const cvC = el('canvas', 'curve'); cvC.width = 256; cvC.height = 256;
  const inN = numInput(0, 0, 255), outN = numInput(0, 0, 255);
  const io = el('div', 'grid2'); io.append(field(T('ph_input'), inN), field(T('ph_output'), outN));
  const col = { rgb: '#ddd', r: '#ff5a5a', g: '#4cd964', b: '#4a90ff' };
  const draw = () => {
    const g = cvC.getContext('2d');
    g.clearRect(0, 0, 256, 256); g.fillStyle = 'rgba(128,128,128,.12)'; g.fillRect(0, 0, 256, 256);
    g.strokeStyle = 'rgba(128,128,128,.35)'; g.lineWidth = 1; g.beginPath();
    for (let i = 64; i < 256; i += 64) { g.moveTo(i + .5, 0); g.lineTo(i + .5, 256); g.moveTo(0, i + .5); g.lineTo(256, i + .5); }
    g.moveTo(0, 256); g.lineTo(256, 0); g.stroke();
    const lut = A.curveLut(pts[ch]);
    g.strokeStyle = col[ch]; g.lineWidth = 2; g.beginPath();
    for (let x = 0; x < 256; x++) { const y = 255 - lut[x]; x ? g.lineTo(x, y) : g.moveTo(x, y); }
    g.stroke();
    pts[ch].forEach((q, i) => { g.fillStyle = i === sel ? col[ch] : '#222'; g.strokeStyle = col[ch]; g.beginPath(); g.rect(q[0] - 4, 255 - q[1] - 4, 8, 8); g.fill(); g.stroke(); });
    const q = pts[ch][sel]; if (q) { inN.value = q[0]; outN.value = q[1]; }
  };
  const at = e => { const r = cvC.getBoundingClientRect(); return [Math.round((e.clientX - r.left) / r.width * 255), Math.round(255 - (e.clientY - r.top) / r.height * 255)]; };
  let dragI = -1;
  cvC.onpointerdown = e => {
    const [x, y] = at(e), P = pts[ch];
    sel = P.findIndex(q => Math.abs(q[0] - x) <= 6 && Math.abs(q[1] - y) <= 6);
    if (sel < 0) { const lut = A.curveLut(P); P.push([clamp(x, 0, 255), lut[clamp(x, 0, 255)]]); P.sort((a, b) => a[0] - b[0]); sel = P.findIndex(q => q[0] === clamp(x, 0, 255)); }
    dragI = sel; cvC.setPointerCapture(e.pointerId); draw(); refresh();
  };
  cvC.onpointermove = e => {
    if (dragI < 0) return;
    const [x, y] = at(e), P = pts[ch], last = P.length - 1;
    // 拖出框外：刪除（頭尾兩點唔刪）
    if ((x < -20 || x > 275 || y < -20 || y > 275) && dragI > 0 && dragI < last) { P.splice(dragI, 1); dragI = sel = -1; draw(); refresh(); return; }
    const lo = dragI > 0 ? P[dragI - 1][0] + 1 : 0, hi = dragI < last ? P[dragI + 1][0] - 1 : 255;
    P[dragI] = [clamp(x, lo, hi), clamp(y, 0, 255)]; draw(); refresh();
  };
  cvC.onpointerup = () => { dragI = -1; };
  const setPt = () => { const P = pts[ch]; if (sel < 0 || !P[sel]) return; const last = P.length - 1, lo = sel > 0 ? P[sel - 1][0] + 1 : 0, hi = sel < last ? P[sel + 1][0] - 1 : 255; P[sel] = [clamp(+inN.value, lo, hi), clamp(+outN.value, 0, 255)]; draw(); refresh(); };
  inN.onchange = setPt; outN.onchange = setPt;
  chSel.onchange = () => { ch = chSel.value; sel = -1; draw(); };
  const reset = obtn(T('ph_reset'), () => { pts[ch] = [[0, 0], [255, 255]]; sel = -1; draw(); refresh(); });
  body.append(field(T('ph_channel'), chSel), cvC, io, reset, checkRow(T('vc_preview'), true, v => { preview = v; if (v) refresh(); else s.restore(); }));
  draw();
  dialog(T('ph_a_curves'), body, [[T('cancel'), () => s.cancel()], [T('ok'), () => { s.run(run); s.commit(); }, true]]);
  fixDlgCancel(s);
}

// ---------- 填滿 / 筆畫 ----------
function fillDlg() {
  const sel = el('select', 'psel');
  for (const [v, k] of [['fg', 'ph_fg'], ['bg', 'ph_bg'], ['color', 'ph_color'], ['black', 'ph_black'], ['gray', 'ph_gray50'], ['white', 'ph_white']]) { const o = el('option', '', T(k)); o.value = v; sel.appendChild(o); }
  const ci = el('input'); ci.type = 'color'; ci.value = fg; ci.style.display = 'none';
  sel.onchange = () => { ci.style.display = sel.value === 'color' ? '' : 'none'; };
  const op = numInput(100, 1, 100), keep = el('input'); keep.type = 'checkbox';
  const body = el('div', 'adlg');
  const kl = el('label', 'ocheck'); kl.append(keep, el('span', '', T('ph_keepalpha')));
  body.append(field(T('ph_contents'), sel), ci, field(T('ph_opacity') + ' %', op), kl);
  dialog(T('ph_filldlg'), body, [[T('cancel')], [T('ok'), () => {
    const color = { fg, bg, color: ci.value, black: '#000000', gray: '#808080', white: '#ffffff' }[sel.value];
    paintMask(null, color, clamp(+op.value, 1, 100) / 100, keep.checked, T('ph_filldlg'));
  }, true]]);
}
/** 用顏色填 mask（null = 選取範圍 / 成張）。 */
function paintMask(mask, color, alpha, keepAlpha, label) {
  const l = doc.layer;
  if (l.text) rasterize(l, true);
  const c = canvas(doc.w, doc.h), g = ctx2d(c);
  g.fillStyle = color; g.fillRect(0, 0, doc.w, doc.h);
  const m = mask || doc.sel;
  if (m) { g.globalCompositeOperation = 'destination-in'; g.drawImage(m, 0, 0); }
  const end = doc.beginPixels(l, label), lg = l.ctx;
  lg.globalAlpha = alpha; lg.globalCompositeOperation = keepAlpha ? 'source-atop' : 'source-over';
  lg.drawImage(c, 0, 0);
  lg.globalAlpha = 1; lg.globalCompositeOperation = 'source-over';
  usedColor(color);
  end(m ? maskBounds(m) || { x: 0, y: 0, w: 0, h: 0 } : { x: 0, y: 0, w: doc.w, h: doc.h });
}
function maskAlpha(m) { const d = ctx2d(m).getImageData(0, 0, doc.w, doc.h).data, a = new Uint8Array(doc.w * doc.h); for (let i = 0; i < a.length; i++) a[i] = d[i * 4 + 3]; return a; }
function alphaMask(fnAt) {
  const c = canvas(doc.w, doc.h), g = ctx2d(c), img = g.createImageData(doc.w, doc.h), d = img.data;
  for (let i = 0; i < doc.w * doc.h; i++) d[i * 4 + 3] = fnAt(i);
  g.putImageData(img, 0, 0); return c;
}
function strokeDlg() {
  if (!doc.sel) return;
  const wi = numInput(3, 1, 250), ci = el('input'); ci.type = 'color'; ci.value = fg;
  const loc = el('select', 'psel');
  for (const [v, k] of [['inside', 'ph_inside'], ['center', 'ph_center'], ['outside', 'ph_outside']]) { const o = el('option', '', T(k)); o.value = v; loc.appendChild(o); }
  loc.value = 'center';
  const op = numInput(100, 1, 100);
  const body = el('div', 'adlg'); body.append(field(T('ph_s_width') + ' (px)', wi), field(T('ph_color'), ci), field(T('ph_location'), loc), field(T('ph_opacity') + ' %', op));
  dialog(T('ph_strokedlg'), body, [[T('cancel')], [T('ok'), () => {
    const w = clamp(+wi.value, 1, 250), a = maskAlpha(doc.sel), dIn = A.distanceField(a, doc.w, doc.h, true), dOut = A.distanceField(a, doc.w, doc.h, false);
    const L = loc.value, half = w / 2;
    const m = alphaMask(i => {
      const on = a[i] >= 128;
      if (L === 'inside') return on && dIn[i] <= w ? 255 : 0;
      if (L === 'outside') return !on && dOut[i] <= w ? 255 : 0;
      return (on && dIn[i] <= Math.ceil(half)) || (!on && dOut[i] <= Math.floor(half)) ? 255 : 0;
    });
    paintMask(m, ci.value, clamp(+op.value, 1, 100) / 100, false, T('ph_strokedlg'));
  }, true]]);
}

// ---------- 修改選取範圍 ----------
function modifySel(kind) {
  if (!doc.sel) return;
  const label = T('ph_' + kind), inp = numInput(kind === 'feather' ? 5 : kind === 'border' ? 10 : 4, kind === 'feather' ? 0.1 : 1, 500, kind === 'feather' ? 0.1 : 1);
  dialog(label, field(T(kind === 'feather' ? 'ph_featherr' : kind === 'border' ? 'ph_s_width' : kind === 'smooth' ? 'ph_radius' : 'ph_bypx'), inp), [[T('cancel')], [T('ok'), () => {
    const r = clamp(+inp.value, 0.1, 500), a = maskAlpha(doc.sel);
    let m;
    if (kind === 'feather' || kind === 'smooth') {
      const img = ctx2d(doc.sel).getImageData(0, 0, doc.w, doc.h);
      A.gaussian(img, { r: kind === 'feather' ? r * 2 : r });
      const c = canvas(doc.w, doc.h), g = ctx2d(c);
      if (kind === 'smooth') { const d = img.data; for (let i = 3; i < d.length; i += 4) d[i] = d[i] >= 128 ? 255 : 0; }
      g.putImageData(img, 0, 0); m = c;
    } else if (kind === 'expand') { const d = A.distanceField(a, doc.w, doc.h, false); m = alphaMask(i => a[i] >= 128 || d[i] <= r ? 255 : 0); }
    else if (kind === 'contract') { const d = A.distanceField(a, doc.w, doc.h, true); m = alphaMask(i => a[i] >= 128 && d[i] > r ? 255 : 0); }
    else { const di = A.distanceField(a, doc.w, doc.h, true), dO = A.distanceField(a, doc.w, doc.h, false), h = r / 2; m = alphaMask(i => (a[i] >= 128 ? di[i] <= h : dO[i] <= h) ? 255 : 0); }
    doc.select(m, label);
  }, true]]);
}

// ---------- 復原 ----------
let toggled = false;
function undo() { if (xf) { cancelTransform(); return; } const l = doc.undo(); toggled = false; if (l) toast(T('undo') + '：' + l, 1200); syncSize(); }
function redo() { const l = doc.redo(); toggled = false; if (l) toast(T('redo') + '：' + l, 1200); syncSize(); }
/** Ctrl+Alt+Z：喺最後兩個狀態之間切換（睇前後對比）。 */
function toggleLast() { if (toggled && doc.redoStack.length) { doc.redo(); toggled = false; } else if (doc.undoStack.length) { doc.undo(); toggled = true; } syncSize(); }
function syncSize() { if (comp.width !== doc.w || comp.height !== doc.h) { comp = canvas(doc.w, doc.h); fit(); } if (crop && tool === 'crop') crop = { x: 0, y: 0, w: doc.w, h: doc.h }; compDirty = true; req(); }

// ---------- 游標 ----------
const XF_CUR = ['nwse-resize', 'ns-resize', 'nesw-resize', 'ew-resize', 'nwse-resize', 'ns-resize', 'nesw-resize', 'ew-resize'];
function updateCursor(p) {
  const st = $('stage');
  let c = '';
  if (xf) { const h = xfHit(p); c = h.mode === 'scale' ? XF_CUR[h.h] : h.mode === 'move' ? 'move' : 'alias'; }
  else if (tool === 'crop' && crop) {
    const s = toScreen(p), hs = cropHandles();
    const i = hs.findIndex(q => { const v = toScreen(q); return Math.abs(v.x - s.x) <= 8 && Math.abs(v.y - s.y) <= 8; });
    const r = cropRect();
    c = i >= 0 ? XF_CUR[i] : p.x >= r.x && p.y >= r.y && p.x <= r.x + r.w && p.y <= r.y + r.h ? 'move' : 'crosshair';
  }
  if (st.style.cursor !== c) st.style.cursor = c;
}

// ---------- 快捷鍵 ----------
/** 數字鍵改不透明度：1 = 10% … 0 = 100%；快速撳兩個數字 = 準確數值（例如 4 5 = 45%）。 */
function numKey(d) {
  numBuf = Date.now() - numT < 600 && numBuf.length === 1 ? numBuf + d : d; numT = Date.now();
  const v = numBuf.length === 2 ? (+numBuf || 100) / 100 : (d === '0' ? 1 : +d / 10);
  if (tool === 'move' || !(PAINT_TOOLS.includes(tool) || tool === 'gradient' || tool === 'fill')) {
    const l = doc.layer; if (!l) return;
    doc.change(T('ph_opacity'), () => l.opacity = v);
    toast(T('ph_opacity') + ' ' + Math.round(v * 100) + '%', 900);
    return;
  }
  opt[tool].opacity = Math.max(0.01, v); opSlider && opSlider.sync();
  toast(T('ph_opacity') + ' ' + Math.round(v * 100) + '%', 900);
}
function onKey(e) {
  if (!doc || !$('editor').classList.contains('show')) return;
  if (document.querySelector('.rmask')) return;
  if (editing) return;
  const tag = document.activeElement?.tagName || '';
  if (/INPUT|TEXTAREA|SELECT/.test(tag) && document.activeElement.type !== 'range' && document.activeElement.type !== 'checkbox') return;
  const k = e.key.toLowerCase(), c = e.ctrlKey || e.metaKey, sh = e.shiftKey, alt = e.altKey;
  const run = f => { e.preventDefault(); e.stopPropagation(); f(); };
  if (e.code === 'Space' && !c) { e.preventDefault(); if (!spaceHeld && !drag && !xf) { spaceHeld = true; if (tool !== 'hand') { prevTool = tool; setTool('hand'); } } return; }
  // 撳住 Ctrl：暫時轉做移動工具
  if (e.key === 'Control' && !e.repeat && !tempTool && !drag && !xf && !poly && !['move', 'hand', 'zoom', 'crop'].includes(tool)) { tempTool = { key: 'Control', tool }; setTool('move'); return; }
  // 變形 / 裁切 / 多邊形套索：Enter 完成、Esc 取消
  if (k === 'enter' && !c) { if (xf) return run(commitTransform); if (tool === 'crop' && crop) return run(commitCrop); if (poly) return run(closePoly); }
  if (k === 'escape') { if (xf) return run(cancelTransform); if (poly) return run(() => { poly = null; req(); }); if (tool === 'crop' && crop) return run(() => { crop = { x: 0, y: 0, w: doc.w, h: doc.h }; req(); }); }
  if (poly && (k === 'backspace' || k === 'delete')) return run(() => { poly.pts.pop(); if (!poly.pts.length) poly = null; req(); });
  if (xf && k.startsWith('arrow') && !c) { const s = sh ? 10 : 1; return run(() => { xf.cx += k === 'arrowleft' ? -s : k === 'arrowright' ? s : 0; xf.cy += k === 'arrowup' ? -s : k === 'arrowdown' ? s : 0; drawXfLayer(); syncXfOpts(); }); }
  if (c) {
    if (xf && k !== 'z') return;
    if (k === 'z') return run(alt ? toggleLast : sh ? redo : undo);
    if (k === 'y') return run(redo);
    if (k === 'a') return run(selectAll);
    if (k === 'd') return run(sh ? reselect : deselect);
    if (k === 'i') return run(alt ? imageSizeDlg : sh ? invertSel : () => applyNow(T('ph_a_invert'), A.invert));
    if (k === 'c') return run(alt ? canvasSizeDlg : () => copy(false, sh));
    if (k === 'x') return run(() => copy(true));
    if (k === 'v') return run(() => { if (clip) pasteCanvas(clip.canvas, clip.x, clip.y); else if (!sh) navigator.clipboard?.read?.().then(async items => { for (const it of items) { const t = it.types.find(x => x.startsWith('image/')); if (t) { const bmp = await createImageBitmap(await it.getType(t)); const cc = canvas(bmp.width, bmp.height); ctx2d(cc).drawImage(bmp, 0, 0); pasteCanvas(cc, Math.round((doc.w - cc.width) / 2), Math.round((doc.h - cc.height) / 2)); return; } } toast(T('paste_tip')); }).catch(() => toast(T('paste_tip'))); else toast(T('paste_tip')); });
    if (k === 'j') return run(sh ? layerViaCut : dupLayer);
    if (k === 'e') return run(sh && alt ? stampVisible : sh ? mergeVisible : mergeDown);
    if (k === 'n' && sh) return run(newLayer);
    if (k === ']' || k === '}') return run(() => sh ? moveLayerTo(doc.layers.length - 1) : moveLayer(1));
    if (k === '[' || k === '{') return run(() => sh ? moveLayerTo(0) : moveLayer(-1));
    if (k === '0' || e.code === 'Digit0') return run(() => fit());
    if (k === '1' || e.code === 'Digit1') return run(() => zoomTo(1));
    if (k === '=' || k === '+') return run(() => zoomBy(1.25));
    if (k === '-' || k === '_') return run(() => zoomBy(0.8));
    if (k === 'backspace' || k === 'delete') return run(() => fillSel(bg));
    if (k === 't') return run(startTransform);
    if (k === 'l') return run(sh && alt ? () => applyNow(T('ph_a_autocontrast'), A.autoContrast) : sh ? () => applyNow(T('ph_a_autotone'), A.autoTone) : levelsDlg);
    if (k === 'm') return run(curvesDlg);
    if (k === 'u') return run(sh ? () => applyNow(T('ph_a_desat'), A.desaturate) : () => adjust('huesat'));
    if (k === 'b') return run(sh && alt ? () => adjust('bw') : sh ? () => applyNow(T('ph_a_autocolor'), A.autoColor) : () => adjust('balance'));
    if (k === 'f' && alt) return run(repeatFilter);
    if (k === 'h') return run(() => { showExtras = !showExtras; req(); });
    if (k === "'" || e.code === 'Quote') return run(() => { showGrid = !showGrid; req(); });
    if (k === 'w' && sh && alt) return run(() => ui.exportAs('png'));
    if (k === 'w') return run(() => ui.goHome());
    return;
  }
  if (xf) return;
  if (alt && (k === 'backspace' || k === 'delete')) return run(() => fillSel(fg));
  if (alt && (k === ']' || e.code === 'BracketRight')) return run(() => pickLayer(1));
  if (alt && (k === '[' || e.code === 'BracketLeft')) return run(() => pickLayer(-1));
  if (sh && k === 'f5') return run(fillDlg);
  if (sh && k === 'f6') return run(() => modifySel('feather'));
  if (sh && k === 'f7') return run(invertSel);
  if (sh && k === 'backspace') return run(fillDlg);
  if (alt) return;
  if (k === 'delete' || k === 'backspace') return run(clearSel);
  if (k.startsWith('arrow')) {
    const s = sh ? 10 : 1, dx = k === 'arrowleft' ? -s : k === 'arrowright' ? s : 0, dy = k === 'arrowup' ? -s : k === 'arrowdown' ? s : 0;
    if (tool === 'move') return run(() => nudge(dx, dy));
    if (SEL_TOOLS.includes(tool) && doc.sel) return run(() => nudgeSel(dx, dy));
    return;
  }
  if (e.code === 'BracketLeft' || e.code === 'BracketRight') {
    const up = e.code === 'BracketRight';
    if (PAINT_TOOLS.includes(tool)) {
      const o = opt[tool];
      if (sh && tool !== 'pencil') { o.hard = clamp(Math.round((o.hard + (up ? 0.25 : -0.25)) * 4) / 4, 0, 1); hardSlider && hardSlider.sync(); toast(T('ph_hard') + ' ' + Math.round(o.hard * 100) + '%', 900); }
      else { const f = up ? 1.15 : 1 / 1.15; o.size = clamp(Math.round(o.size * f + (up ? 1 : -1) * (o.size < 10 ? 1 : 0)), 1, tool === 'pencil' ? 100 : 500); sizeSlider && sizeSlider.sync(); }
      req(); e.preventDefault();
    }
    return;
  }
  if (/^[0-9]$/.test(k)) return run(() => numKey(k));
  if (k === 'tab') return run(() => dock.toggleAll());
  if (k === 'f6') return run(() => dock.toggle('color'));
  if (k === 'f7') return run(() => dock.toggle('layers'));
  if (k === 'x') return run(swapColors);
  if (k === 'd') return run(resetColors);
  // 工具字母：撳一下 = 嗰組最後用過嘅工具；Shift + 字母 = 喺組入面輪住揀
  const letter = k.toUpperCase();
  const g = GROUPS.find(gr => gr !== '-' && gr.some(id => TOOL_KEY[id] === letter));
  if (g) return run(() => {
    const cur = groupPick[g.join(',')] || g[0];
    if (sh && g.length > 1) setTool(g[(g.indexOf(g.includes(tool) ? tool : cur) + 1) % g.length]);
    else setTool(cur);
  });
}

/** 新文件（底色：white / transparent / 任何顏色）。 */
export function blankDoc(w, h, fill, layerName) {
  const d = new PhotoDoc(w, h), l = new Layer(w, h, layerName);
  if (fill && fill !== 'transparent') { const g = l.ctx; g.fillStyle = fill; g.fillRect(0, 0, w, h); }
  d.layers.push(l);
  return d;
}
export function docFromCanvas(c, name) { const d = new PhotoDoc(c.width, c.height), l = new Layer(c.width, c.height, name); l.ctx.drawImage(c, 0, 0); d.layers.push(l); return d; }
export const fgColor = () => fg;
