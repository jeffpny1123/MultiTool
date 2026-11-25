// 設計工具：首頁 + 修圖編輯器 + 向量繪圖編輯器。同主程式（src/Design.cs）溝通：
//   頁面 → 主程式：ready / pick / opened / closed / recent-* / rename / new-window / save-ask / save-begin / save-chunk / save-end
//   主程式 → 頁面：file / save-target / saved / renamed / recent / toast / cfg
import './shell.css';
import './design.css';
import { T, $, el, CFG, host, post, toast, busy, buildMenubar, closeMenus, popupMenu, dialog, field, numInput, sendBytes, clamp } from './common.js';
import { ICON } from './photo/icons.js';
import * as photo from './photo/editor.js';
import { openBytes, encodeFor, extOf, FLAT } from './photo/io.js';
import * as vec from './vector/veditor.js';
import { docFromSvg, docFromProject, projectKind } from './vector/vdoc.js';

let mode = null;           // 'photo' / 'vector'（而家開緊邊個編輯器）
let curName = '';          // 而家份檔嘅名（連副檔名）
let recent = CFG.recent || [];
let saving = null;         // { export } 等緊主程式揀位置
let dropWait = null;

// 兩個編輯器嘅共同介面
const E = {
  photo: { box: 'editor', has: () => photo.hasDoc(), dirty: () => photo.isDirty(), saved: () => photo.markSaved(), title: n => photo.setDocTitle(n), menus: () => photo.menus(), exports: [['png', 'PNG'], ['jpg', 'JPG'], ['webp', 'WebP']], untitled: () => T('ph_untitled') + '.png' },
  vector: { box: 'veditor', has: () => vec.vHasDoc(), dirty: () => vec.vIsDirty(), saved: () => vec.vMarkSaved(), title: n => vec.vSetDocTitle(n), menus: () => vec.vMenus(), exports: [['svg', 'SVG'], ['pdf', 'PDF'], ['png', 'PNG']], untitled: () => T('vc_untitled') + '.svg' },
};
const cur = () => mode && E[mode];

// ---------- 首頁 ----------
function buildHome() {
  const h = $('home');
  h.innerHTML =
    '<div class="hwrap"><div class="htop"><div><h1></h1><div class="hsub"></div></div><span class="grow"></span><button class="rb" id="hWin"></button></div>' +
    '<div class="hcards"></div><div class="hrow"><button class="rb pri" id="hOpen"></button><span class="hdrop"></span></div>' +
    '<div class="hrecent"><div class="hrh"><b></b><span class="grow"></span><button class="rb link" id="hClear"></button></div><div id="recentList"></div></div></div>';
  h.querySelector('h1').textContent = T('title');
  h.querySelector('.hsub').textContent = T('sub');
  $('hWin').textContent = '⧉ ' + T('newwin'); $('hWin').style.display = host ? '' : 'none'; $('hWin').onclick = () => post({ t: 'new-window' });
  $('hOpen').textContent = T('openfile'); $('hOpen').onclick = pick;
  h.querySelector('.hdrop').textContent = T('drop');
  h.querySelector('.hrh b').textContent = T('recent');
  $('hClear').textContent = T('recentclear'); $('hClear').onclick = () => post({ t: 'recent-clear' });
  const cards = h.querySelector('.hcards');
  const card = (icon, title, desc, actions) => {
    const c = el('div', 'hcard');
    const i = el('div', 'hicon'); i.innerHTML = ICON[icon]; c.appendChild(i);
    const t = el('div', 'htext'); t.appendChild(el('b', '', title)); t.appendChild(el('span', '', desc)); c.appendChild(t);
    const a = el('div', 'hact');
    for (const [label, run, pri] of actions) { const b = el('button', 'rb' + (pri ? ' pri' : ''), label); b.onclick = run; a.appendChild(b); }
    c.appendChild(a);
    cards.appendChild(c);
  };
  card('photo', T('k_photo'), T('k_photo_d'), [[T('new'), () => newDocDlg('photo'), true], [T('open'), pick]]);
  card('vector', T('k_vector'), T('k_vector_d2'), [[T('new'), () => newDocDlg('vector'), true], [T('open'), pick]]);
  drawRecent();
}
function drawRecent() {
  const list = $('recentList'); if (!list) return;
  list.textContent = '';
  $('home').querySelector('.hrecent').style.display = recent.length ? '' : 'none';
  for (const r of recent) {
    const row = el('div', 'rrow');
    row.appendChild(el('b', '', r.name)); row.appendChild(el('span', 'rdir', r.dir));
    const x = el('button', 'rb link', '✕'); x.title = T('recentdel');
    x.onclick = e => { e.stopPropagation(); post({ t: 'recent-remove', path: r.path }); };
    row.appendChild(x);
    row.onclick = () => post({ t: 'recent-open', path: r.path });
    list.appendChild(row);
  }
}

// ---------- 切換首頁 / 編輯器 ----------
function show(m) {
  mode = m;
  $('home').classList.toggle('show', !m);
  $('editor').classList.toggle('show', m === 'photo');
  $('veditor').classList.toggle('show', m === 'vector');
  $('ebar').style.display = m ? '' : 'none';
  closeMenus();
  if (m) buildMenubar($('menubar'), E[m].menus().map((d, k) => ({ label: d.label, items: () => E[m].menus()[k].items() })));
}
function okToLeave() { return !cur() || !cur().dirty() || confirm(T('leave')); }
function goHome() {
  if (!okToLeave()) return;
  cur() && cur().saved(); curName = '';
  post({ t: 'closed' });
  show(null);
}
function setName(n) {
  curName = n;
  const i = $('docName');
  i.value = n.replace(/\.[a-z0-9]+$/i, '');
  i.size = Math.max(6, Math.min(60, [...i.value].reduce((w, ch) => w + (ch.charCodeAt(0) > 0x2e7f ? 2 : 1), 0) + 1));
  document.title = n;
  cur() && cur().title(n);
}
function rename(v) {
  const ext = extOf(curName) || 'png';
  v = String(v || '').replace(/[\\/:*?"<>|]/g, '').trim().replace(new RegExp('\\.' + ext + '$', 'i'), '').trim();
  const old = curName, next = v + '.' + ext;
  if (!v || next === old) { setName(old); return; }
  setName(next);
  post({ t: 'rename', name: next, old });
}
function openDoc(m, d, name, token) {
  show(m);
  if (m === 'photo') photo.setDoc(d); else vec.vSetDoc(d);
  setName(name);
  post({ t: 'opened', name, token: token || null });
}

// ---------- 新增 / 開啟 ----------
function pick() { post({ t: 'pick' }); }
function newDocDlg(kind) {
  kind = kind || mode || 'photo';
  if (!okToLeave()) return;
  const vector = kind === 'vector';
  const presets = vector
    ? [[794, 1123, 'ps_a4v'], [816, 1056, 'ps_letter'], [1920, 1080, 'ps_hd'], [1080, 1080, 'ps_square'], [512, 512, 'ps_icon'], [1024, 1024, 'ps_logo']]
    : [[1920, 1080, 'ps_hd'], [1080, 1080, 'ps_square'], [1080, 1920, 'ps_story'], [2480, 3508, 'ps_a4'], [1280, 720, 'ps_thumb'], [800, 600, '']];
  const sel = el('select');
  presets.forEach(([w, h, k], i) => { const o = el('option', '', (k ? T(k) + ' · ' : '') + w + ' × ' + h); o.value = i; sel.appendChild(o); });
  const wi = numInput(presets[0][0], 1, 30000), hi = numInput(presets[0][1], 1, 30000);
  sel.onchange = () => { const p = presets[+sel.value]; wi.value = p[0]; hi.value = p[1]; };
  const body = el('div', 'grid2');
  const f0 = field(T('ph_preset'), sel); f0.classList.add('wide');
  body.append(f0, field(T('ph_width'), wi), field(T('ph_height'), hi));
  let bgSel = null;
  if (!vector) {
    bgSel = el('select');
    for (const [v, k] of [['#ffffff', 'bg_white'], ['transparent', 'bg_clear'], ['#000000', 'bg_black']]) { const o = el('option', '', T(k)); o.value = v; bgSel.appendChild(o); }
    const f3 = field(T('ph_bgfill'), bgSel); f3.classList.add('wide'); body.appendChild(f3);
  }
  dialog(T(vector ? 'vc_newdoc' : 'ph_newimg'), body, [[T('cancel')], [T('ok'), () => {
    const w = clamp(Math.round(+wi.value) || 1, 1, 30000), h = clamp(Math.round(+hi.value) || 1, 1, 30000);
    if (!vector && w * h > 268e6) { toast(T('ph_toobig')); return false; }
    cur() && cur().saved();
    if (vector) { show('vector'); openDoc('vector', vec.vBlank(w, h), E.vector.untitled(), null); }
    else openDoc('photo', photo.blankDoc(w, h, bgSel.value, T('ph_background')), E.photo.untitled(), null);
  }, true]]);
}
async function openFromBytes(buf, name, token) {
  if (!okToLeave()) return;
  busy(T('loading'));
  try {
    const ext = extOf(name);
    const kind = ext === 'svg' ? 'vector' : ext === 'rdesign' ? await projectKind(buf) : 'photo';
    if (kind === 'vector') {
      show('vector');   // paper.js 要個 canvas 有大細先開得
      const d = ext === 'svg' ? docFromSvg(vec.vScope(), new TextDecoder().decode(buf)) : await docFromProject(vec.vScope(), buf);
      busy(''); cur() && cur().saved(); openDoc('vector', d, name, token);
    } else {
      const d = await openBytes(buf, name, T);
      busy(''); cur() && cur().saved(); openDoc('photo', d, name, token);
    }
  }
  catch (e) { console.error(e); busy(''); toast(T('openfail') + (e && e.message ? '：' + e.message : ''), 8000); if (!cur() || !cur().has()) show(null); }
}

// ---------- 儲存 ----------
/**
 * 儲存（as = 另存新檔）：先問主程式寫去邊（佢會彈對話框揀位置同格式），收到 save-target 先編碼。
 * 修圖有幾個圖層而格式係 PNG / JPG / WebP：先問要另存做專案（保留圖層），定係合併圖層儲存。
 * 向量：SVG 同專案可以直接存返；其他（PNG、PDF）係匯出。
 */
function save(as) {
  if (!cur() || !cur().has()) return;
  const ext = extOf(curName);
  if (mode === 'vector') { ask(as, false, ext === 'svg' || ext === 'rdesign' ? ext : 'svg'); return; }
  const layered = photo.currentDoc().layers.length > 1;
  if (!as && layered && FLAT.includes(ext)) {
    dialog(T('ph_flat_t'), T('ph_flat', ext.toUpperCase()), [
      [T('cancel')],
      [T('ph_flat_merge'), () => ask(false, false, ext)],
      [T('ph_flat_project'), () => ask(true, false, 'rdesign'), true],
    ]);
    return;
  }
  ask(as, false, layered ? 'rdesign' : (ext || 'png'));
}
function exportAs(ext) { if (cur() && cur().has()) ask(true, true, ext); }
function ask(as, exp, prefer) {
  saving = { export: exp };
  const base = curName.replace(/\.[a-z0-9]+$/i, '') || T(mode === 'vector' ? 'vc_untitled' : 'ph_untitled');
  post({ t: 'save-ask', as, export: exp, prefer, kind: mode, name: base + '.' + prefer });
  if (!host) onHost({ t: 'save-target', ext: prefer, name: base + '.' + prefer });
}
async function encodeVector(ext) {
  const d = vec.vCurrentDoc();
  if (ext === 'svg') return new TextEncoder().encode(d.svgString());
  if (ext === 'png') return d.pngBytes(1);
  if (ext === 'pdf') { const r = await d.pdfBytes(); if (r.raster) toast(T('vc_pdfraster'), 6000); return r.bytes; }
  return d.projectBytes();
}
async function writeTo(ext, name) {
  const s = saving; saving = null;
  toast(T('saving'), 60000);
  try {
    const bytes = mode === 'vector' ? await encodeVector(ext) : await encodeFor(photo.currentDoc(), ext, 0.92);
    sendBytes(bytes, { name, export: !!(s && s.export) });
    if (!host) { window.__last = { bytes, name }; toast('ok'); }
  } catch (e) { console.error(e); toast(T('savefail', e && e.message || e), 7000); }
}

// ---------- 同主程式溝通 ----------
async function onHost(m) {
  if (m.t === 'file') {
    if (dropWait) { clearTimeout(dropWait); dropWait = null; }
    busy(T('loading'));
    try { const r = await fetch(m.url); if (!r.ok) throw new Error(r.status); const buf = await r.arrayBuffer(); busy(''); await openFromBytes(buf, m.name, m.token); }
    catch (e) { console.error(e); busy(''); toast(T('openfail') + (e && e.message ? '：' + e.message : ''), 8000); }
  }
  else if (m.t === 'save-target') { if (m.ext) writeTo(m.ext, m.name); else saving = null; }
  else if (m.t === 'saved') {
    if (m.ok && m.export) toast(T('saved', m.name || ''), 3000);
    else if (m.ok) { cur() && cur().saved(); if (m.name) setName(m.name); toast(T('saved', m.name || ''), 3000); }
    else if (m.err) toast(T('savefail', m.err), 7000);
    else $('toast').style.display = 'none';
  }
  else if (m.t === 'renamed') { setName(m.name); if (!m.ok && m.err) toast(m.err, 6000); }
  else if (m.t === 'recent') { recent = m.list || []; drawRecent(); }
  else if (m.t === 'toast') toast(m.text, 5000);
}

// ---------- 開始 ----------
function init() {
  const bar = $('ebar');
  bar.innerHTML = '<div class="rbar"><button class="rb" id="bBack"></button><button class="rb pri" id="bOpen"></button></div>' +
    '<div class="tcol"><input id="docName" spellcheck="false"><div id="menubar"></div></div><span class="grow"></span>' +
    '<div class="rbar"><button class="rb" id="bWin" style="display:none">⧉</button>' +
    '<div class="split"><button class="rb ok l" id="bSave"></button><button class="rb ok r" id="bMore">▾</button></div></div>';
  $('bBack').textContent = T('back'); $('bOpen').textContent = T('open'); $('bSave').textContent = T('save'); $('bWin').title = T('newwin');
  $('bBack').onclick = goHome; $('bOpen').onclick = pick; $('bSave').onclick = () => save(false); $('bWin').onclick = () => post({ t: 'new-window' });
  if (host) $('bWin').style.display = '';
  const i = $('docName'); i.title = T('rename_tip');
  i.addEventListener('focus', () => i.select()); i.addEventListener('change', () => rename(i.value));
  i.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Enter') i.blur(); else if (e.key === 'Escape') { setName(curName); i.blur(); } });
  $('bMore').onclick = e => {
    e.stopPropagation();
    const r = $('bMore').getBoundingClientRect();
    popupMenu([{ label: T('saveas'), key: 'Ctrl+Shift+S', run: () => save(true) }, { label: T('ph_export'), sub: cur().exports.map(([x, n]) => ({ label: n, run: () => exportAs(x) })) }], r.right - 220, r.bottom + 6);
  };

  const common = { save, exportAs, goHome, open: pick, newWindow: () => post({ t: 'new-window' }), onDirty: () => { } };
  photo.mountEditor({ ...common, newDoc: () => newDocDlg('photo'), fromCanvas: (c, n) => { cur() && cur().saved(); openDoc('photo', photo.docFromCanvas(c, n), E.photo.untitled(), null); } });
  vec.mountVector({ ...common, newDoc: () => newDocDlg('vector') });
  buildHome();
  show(null);

  addEventListener('keydown', e => {
    if (!(e.ctrlKey || e.metaKey) || e.altKey || document.querySelector('.rmask')) return;
    const k = e.key.toLowerCase();
    const run = f => { e.preventDefault(); e.stopPropagation(); f(); };
    if (k === 'o') run(pick);
    else if (k === 'n' && !e.shiftKey) run(() => newDocDlg());
    else if (k === 's' && cur() && cur().has()) run(() => save(e.shiftKey));
  }, true);
  // 拖放：交個 File 俾主程式攞真實路徑；主程式冇反應就自己讀
  addEventListener('dragover', e => { if (e.dataTransfer && [...e.dataTransfer.types].includes('Files')) e.preventDefault(); }, true);
  addEventListener('drop', e => {
    const f = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
    if (!f) return;
    e.preventDefault(); e.stopPropagation();
    const own = async () => { dropWait = null; openFromBytes(await f.arrayBuffer(), f.name, null); };
    try { if (host && host.postMessageWithAdditionalObjects) { dropWait = setTimeout(own, 1500); host.postMessageWithAdditionalObjects({ t: 'drop' }, [f]); return; } } catch { }
    own();
  }, true);
  addEventListener('beforeunload', e => { if (cur() && cur().dirty()) { e.preventDefault(); e.returnValue = ''; } });
  if (host) host.addEventListener('message', e => onHost(e.data));
  window.__design = { onHost, save, newDocDlg, vec, photo };
  post({ t: 'ready', page: 'design' });
}
init();
