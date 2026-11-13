// 文書工具：簡報（.pptx）。顯示 / 播放 / 寫檔用 pptx-browser（MIT）：直接改原檔入面嘅 XML，冇掂過嘅嘢（動畫、母片、圖表）會原封不動保留。
// 選單跟 Google 簡報。
import { PptxRenderer, PptxWriter } from 'pptx-browser';
import { BLANK_PPTX } from './blank-pptx.js';
import { T, CFG, $, post, toast, busy, shell, initShell, buildMenubar, dialog, el, closeMenus } from './shell.js';
import './shell.css';

const NSA = 'http://schemas.openxmlformats.org/drawingml/2006/main', NSP = 'http://schemas.openxmlformats.org/presentationml/2006/main';
const KINDS = ['sp', 'pic', 'graphicFrame', 'cxnSp', 'grpSp'];
let writer = null, renderer = null, curBytes = null, cur = 0, selId = null, undo = [], redo = [], sizeEmu = { cx: 12192000, cy: 6858000 }, scale = 1, editing = null, busyRender = false;
const kids = (n, name) => [...n.children].filter(c => c.localName === name);
const kid = (n, name) => kids(n, name)[0] || null;
const count = () => writer ? writer.getSlideCount() : 0;
const dec = new TextDecoder();
function readDoc(path) { const f = writer._files[path]; if (!f) return null; return new DOMParser().parseFromString(typeof f === 'string' ? f : dec.decode(f), 'application/xml'); }
function relTarget(path, typeEnd) {
  const d = readDoc(path.replace(/([^/]+)$/, '_rels/$1.rels')); if (!d) return null;
  const r = [...d.documentElement.children].find(x => (x.getAttribute('Type') || '').endsWith(typeEnd)); if (!r) return null;
  const parts = (path.replace(/[^/]+$/, '') + r.getAttribute('Target')).split('/'), out = [];
  for (const p of parts) { if (p === '..') out.pop(); else if (p !== '.') out.push(p); }
  return out.join('/');
}
const tree = doc => { const c = kid(doc.documentElement, 'cSld'); return c ? kid(c, 'spTree') : null; };
const nvPr = n => { const nv = [...n.children].find(c => /^nv/.test(c.localName)); return nv ? { c: kid(nv, 'cNvPr'), ph: kid(nv, 'nvPr') ? kid(kid(nv, 'nvPr'), 'ph') : null } : { c: null, ph: null }; };
function xfrmOf(n) { if (n.localName === 'graphicFrame') return kid(n, 'xfrm'); const sp = kid(n, n.localName === 'grpSp' ? 'grpSpPr' : 'spPr'); return sp ? kid(sp, 'xfrm') : null; }
function rectOf(x) { const o = x && kid(x, 'off'), e = x && kid(x, 'ext'); return o && e ? { x: +o.getAttribute('x'), y: +o.getAttribute('y'), w: +e.getAttribute('cx'), h: +e.getAttribute('cy') } : null; }
/** 版面配置 / 母片入面同一個預留位置（placeholder）嘅位置。 */
function inherited(slidePath, ph) {
  let path = relTarget(slidePath, '/slideLayout');
  for (let i = 0; i < 2 && path; i++) {
    const d = readDoc(path), t = d && tree(d);
    if (t) {
      const type = ph.getAttribute('type') || 'body', idx = ph.getAttribute('idx');
      const same = a => a === type || (/title/i.test(a) && /title/i.test(type));
      const list = [...t.children].filter(c => nvPr(c).ph);
      const hit = list.find(c => idx && nvPr(c).ph.getAttribute('idx') === idx) || list.find(c => same(nvPr(c).ph.getAttribute('type') || 'body'));
      const r = hit && rectOf(xfrmOf(hit)); if (r) return r;
    }
    path = relTarget(path, '/slideMaster');
  }
  return null;
}
function shapes(idx) {
  const doc = writer._slideDoc(idx), t = tree(doc), out = [];
  if (!t) return { doc, list: out };
  for (const n of t.children) {
    if (!KINDS.includes(n.localName)) continue;
    const { c, ph } = nvPr(n);
    const rect = rectOf(xfrmOf(n)) || (ph ? inherited(writer.getSlidePaths()[idx], ph) : null);
    const tb = kid(n, 'txBody');
    const text = tb ? kids(tb, 'p').map(p => [...p.getElementsByTagNameNS(NSA, 't')].map(x => x.textContent).join('')).join('\n') : null;
    out.push({ id: c ? c.getAttribute('id') : '', name: c ? c.getAttribute('name') : '', kind: n.localName, node: n, rect, text, ph: ph ? (ph.getAttribute('type') || 'body') : null });
  }
  return { doc, list: out };
}
// 備忘稿：只改原本已經有備忘稿嘅投影片。pptx-browser 新建備忘稿時冇登記檔案類型，PowerPoint 會話個檔要修復，所以唔新建。
const hasNotes = idx => !!relTarget(writer.getSlidePaths()[idx], '/notesSlide');
function setNotes(idx, text) { if (hasNotes(idx)) { writer.setSlideNotes(idx, text); return true; } return false; }
/** 抄出嚟嘅投影片會同原本嗰張共用同一份備忘稿：抄完即刻斷開，費事改一張變咗兩張。 */
function dropNotesRel(idx) {
  const path = writer.getSlidePaths()[idx].replace(/([^/]+)$/, '_rels/$1.rels'), d = readDoc(path); if (!d) return;
  let hit = false;
  for (const r of [...d.documentElement.children]) if ((r.getAttribute('Type') || '').endsWith('/notesSlide')) { r.remove(); hit = true; }
  if (hit) writer._files[path] = new TextEncoder().encode(new XMLSerializer().serializeToString(d));
}
const R4 = o => { for (const k of ['x', 'y', 'w', 'h']) if (o[k] != null) o[k] = Math.round(o[k]); return o; };   // EMU 一定要整數，PowerPoint 唔接受小數
const addTextBox = (idx, text, st) => writer.addTextBox(idx, text, R4(st));
const find = (idx, id) => { const s = shapes(idx); return { doc: s.doc, sh: s.list.find(x => x.id === String(id)) }; };

// ---------- 顯示 ----------
async function refresh(keepSel) {
  busyRender = true;
  try {
    curBytes = await writer.save();
    const r = new PptxRenderer(); await r.load(curBytes);
    if (renderer) try { renderer.destroy(); } catch (e) { }
    renderer = r;
    const ps = writer._presDoc.getElementsByTagNameNS(NSP, 'sldSz')[0];
    if (ps) sizeEmu = { cx: +ps.getAttribute('cx'), cy: +ps.getAttribute('cy') };
    cur = Math.max(0, Math.min(cur, count() - 1));
    if (!keepSel) selId = null;
    await drawMain(); drawThumbs();
  } finally { busyRender = false; }
}
async function drawMain() {
  const wrap = $('slideWrap'), area = $('slideArea');
  const maxW = area.clientWidth - 48, maxH = area.clientHeight - 48, ar = sizeEmu.cx / sizeEmu.cy;
  const w = Math.max(200, Math.min(maxW, maxH * ar));
  const cv = $('slideCanvas');
  await renderer.renderSlide(cur, cv, Math.round(w * Math.min(2, devicePixelRatio || 1)));
  cv.style.width = w + 'px'; cv.style.height = w / ar + 'px'; wrap.style.width = w + 'px'; wrap.style.height = w / ar + 'px';
  scale = w / sizeEmu.cx;
  drawOverlay();
  $('pageInfo').textContent = (cur + 1) + ' / ' + count();
  const hn = hasNotes(cur); $('notes').disabled = !hn; $('notes').placeholder = hn ? T('sl_notes_ph') : T('sl_notes_none');
  try { $('notes').value = hn ? ((await renderer.getSlideNotes(cur)) || '') : ''; } catch (e) { $('notes').value = ''; }
}
function drawOverlay() {
  const ov = $('overlay'); ov.textContent = '';
  for (const s of shapes(cur).list) {
    if (!s.rect) continue;
    const b = el('div', 'shp' + (s.id === selId ? ' sel' : '')); b.dataset.id = s.id;
    Object.assign(b.style, { left: s.rect.x * scale + 'px', top: s.rect.y * scale + 'px', width: Math.max(6, s.rect.w * scale) + 'px', height: Math.max(6, s.rect.h * scale) + 'px' });
    b.title = s.name; b.appendChild(el('div', 'hd'));
    b.addEventListener('pointerdown', e => shapeDown(e, s, b));
    if (s.text != null) b.addEventListener('dblclick', () => editText(s, b));
    ov.appendChild(b);
  }
  updTools();
}
let thumbSeq = 0;
async function drawThumbs() {
  const my = ++thumbSeq, box = $('thumbs'), n = count();
  while (box.children.length > n) box.lastChild.remove();
  for (let i = 0; i < n; i++) {
    let d = box.children[i];
    if (!d) { d = el('div', 'th'); d.appendChild(el('span', 'no')); d.appendChild(document.createElement('canvas')); box.appendChild(d); }
    d.firstChild.textContent = i + 1; d.classList.toggle('on', i === cur);
    d.onclick = () => go(i);
    d.oncontextmenu = e => { e.preventDefault(); go(i); };
  }
  const on = box.children[cur]; if (on) on.scrollIntoView({ block: 'nearest' });
  for (let i = 0; i < n; i++) { if (my !== thumbSeq) return; try { await renderer.renderSlide(i, box.children[i].lastChild, 240); } catch (e) { } }
}
async function go(i) { if (busyRender || i < 0 || i >= count()) return; cur = i; selId = null; await drawMain(); [...$('thumbs').children].forEach((d, k) => d.classList.toggle('on', k === cur)); }

// ---------- 修改（每次改之前記低而家嘅檔案內容做復原）----------
async function change(fn, keepSel) {
  if (!writer || busyRender) return;
  undo.push(curBytes); if (undo.length > 15) undo.shift(); redo = [];
  try { await fn(); } catch (e) { console.error(e); toast(String(e && e.message || e), 5000); }
  await refresh(keepSel);
  shell.setDirty();
}
async function unredo(from, to) {
  if (!from.length || busyRender) return;
  to.push(curBytes); writer = await PptxWriter.fromBytes(from.pop()); await refresh(); shell.setDirty();
}
function setRect(sh, r) {
  let x = xfrmOf(sh.node);
  if (!x) { const sp = kid(sh.node, 'spPr'); if (!sp) return; x = sh.node.ownerDocument.createElementNS(NSA, 'a:xfrm'); sp.insertBefore(x, sp.firstChild); }
  let o = kid(x, 'off'), e = kid(x, 'ext');
  if (!o) { o = x.ownerDocument.createElementNS(NSA, 'a:off'); x.insertBefore(o, x.firstChild); }
  if (!e) { e = x.ownerDocument.createElementNS(NSA, 'a:ext'); x.appendChild(e); }
  o.setAttribute('x', Math.round(r.x)); o.setAttribute('y', Math.round(r.y)); e.setAttribute('cx', Math.round(Math.max(1, r.w))); e.setAttribute('cy', Math.round(Math.max(1, r.h)));
}
function setText(sh, text) {
  const tb = kid(sh.node, 'txBody'); if (!tb) return;
  const D = tb.ownerDocument, ps = kids(tb, 'p'), first = ps[0];
  const pPr = first && kid(first, 'pPr'), run = first && first.getElementsByTagNameNS(NSA, 'r')[0], rPr = run && kid(run, 'rPr');
  ps.forEach(p => p.remove());
  for (const line of String(text).split('\n')) {
    const p = D.createElementNS(NSA, 'a:p');
    if (pPr) p.appendChild(pPr.cloneNode(true));
    const r = D.createElementNS(NSA, 'a:r');
    if (rPr) r.appendChild(rPr.cloneNode(true));
    const t = D.createElementNS(NSA, 'a:t'); t.textContent = line; r.appendChild(t); p.appendChild(r); tb.appendChild(p);
  }
}
function setFormat(sh, f) {
  const tb = kid(sh.node, 'txBody'); if (!tb) return;
  for (const r of [...tb.getElementsByTagNameNS(NSA, 'r')]) {
    let p = kid(r, 'rPr');
    if (!p) { p = r.ownerDocument.createElementNS(NSA, 'a:rPr'); p.setAttribute('lang', 'en-US'); r.insertBefore(p, r.firstChild); }
    if (f.size) p.setAttribute('sz', Math.round(f.size * 100));
    if (f.bold != null) p.setAttribute('b', f.bold ? '1' : '0');
    if (f.italic != null) p.setAttribute('i', f.italic ? '1' : '0');
    if (f.underline != null) p.setAttribute('u', f.underline ? 'sng' : 'none');
    if (f.color) { for (const c of [...p.children]) if (/Fill$/.test(c.localName)) c.remove(); const sf = p.ownerDocument.createElementNS(NSA, 'a:solidFill'), c = p.ownerDocument.createElementNS(NSA, 'a:srgbClr'); c.setAttribute('val', f.color.replace('#', '').toUpperCase()); sf.appendChild(c); p.insertBefore(sf, p.firstChild); }
  }
  if (f.align) for (const p of kids(tb, 'p')) { let pp = kid(p, 'pPr'); if (!pp) { pp = p.ownerDocument.createElementNS(NSA, 'a:pPr'); p.insertBefore(pp, p.firstChild); } pp.setAttribute('algn', f.align); }
}
const curFormat = sh => { const tb = sh && kid(sh.node, 'txBody'), p = tb && tb.getElementsByTagNameNS(NSA, 'rPr')[0]; return p ? { bold: p.getAttribute('b') === '1', italic: p.getAttribute('i') === '1', underline: (p.getAttribute('u') || 'none') !== 'none', size: p.getAttribute('sz') ? +p.getAttribute('sz') / 100 : null } : {}; };
const onSel = fn => { if (!selId) { toast(T('sl_pick')); return; } change(() => { const { doc, sh } = find(cur, selId); if (sh) { fn(sh); writer._saveSlideDoc(cur, doc); } }, true); };
const fmtSel = f => onSel(sh => setFormat(sh, f));
const delSel = () => { if (selId) change(() => { const { doc, sh } = find(cur, selId); if (sh) { sh.node.remove(); writer._saveSlideDoc(cur, doc); } }); };
function order(front) { onSel(sh => { const t = sh.node.parentNode; if (front) t.appendChild(sh.node); else { const firstShape = [...t.children].find(c => KINDS.includes(c.localName)); t.insertBefore(sh.node, firstShape); } }); }
const addText = () => change(() => { addTextBox(cur, T('sl_newtext'), { x: sizeEmu.cx * .25, y: sizeEmu.cy * .4, w: sizeEmu.cx * .5, h: sizeEmu.cy * .12, fontSize: 2400, fontFamily: 'Arial', align: 'ctr' }); });
function addImage() {
  const i = document.createElement('input'); i.type = 'file'; i.accept = 'image/png,image/jpeg,image/gif';
  i.onchange = async () => {
    const f = i.files[0]; if (!f) return;
    const bytes = new Uint8Array(await f.arrayBuffer()), img = new Image(); img.src = URL.createObjectURL(f);
    try { await img.decode(); } catch (e) { toast(T('openfail')); return; }
    const k = Math.min(sizeEmu.cx * .6 / img.naturalWidth, sizeEmu.cy * .6 / img.naturalHeight), w = img.naturalWidth * k, h = img.naturalHeight * k;
    change(() => writer.addImage(cur, bytes, f.type || 'image/png', R4({ x: (sizeEmu.cx - w) / 2, y: (sizeEmu.cy - h) / 2, w, h })));
  };
  i.click();
}
/** 新投影片：抄而家呢張嘅版面，清走文字同其他物件。 */
const newSlide = () => change(() => {
  const at = count() ? cur + 1 : 0;
  writer.duplicateSlide(cur, at); dropNotesRel(at);
  const s = shapes(at);
  for (const x of s.list) { if (x.ph && x.text != null) setText(x, ''); else x.node.remove(); }
  writer._saveSlideDoc(at, s.doc);
  if (!s.list.some(x => x.ph)) addTextBox(at, T('sl_title_ph'), { x: sizeEmu.cx * .08, y: sizeEmu.cy * .08, w: sizeEmu.cx * .84, h: sizeEmu.cy * .16, fontSize: 3600, bold: true, fontFamily: 'Arial' });
  cur = at;
});
const dupSlide = () => change(() => { writer.duplicateSlide(cur, cur + 1); dropNotesRel(cur + 1); cur++; });
const delSlide = () => { if (count() <= 1) { toast(T('sl_last')); return; } change(() => { writer.removeSlide(cur); }); };
const moveSlide = d => { const to = cur + d; if (to < 0 || to >= count()) return; change(() => { const o = [...Array(count()).keys()]; o.splice(to, 0, o.splice(cur, 1)[0]); writer.reorderSlides(o); cur = to; }); };
function background() {
  const box = el('div'), i = el('input'); i.type = 'color'; i.value = '#ffffff'; i.style.cssText = 'width:100%;height:44px'; box.appendChild(i);
  dialog(T('sl_bg'), box, [[T('cancel')], [T('ok'), () => { change(() => writer.setSlideBackground(cur, i.value.slice(1).toUpperCase())); }, true]]);
}

// ---------- 滑鼠：揀、搬、拉大細、雙擊改字 ----------
function shapeDown(e, s, b) {
  if (editing || e.button !== 0) return;
  e.preventDefault(); e.stopPropagation(); closeMenus();
  selId = s.id; [...$('overlay').children].forEach(x => x.classList.toggle('sel', x === b)); updTools();
  const resize = e.target.classList.contains('hd'), sx = e.clientX, sy = e.clientY, o = { ...s.rect };
  let moved = false, r = { ...o };
  const mv = ev => {
    const dx = (ev.clientX - sx) / scale, dy = (ev.clientY - sy) / scale;
    if (!moved && Math.abs(ev.clientX - sx) + Math.abs(ev.clientY - sy) < 3) return;
    moved = true;
    r = resize ? { ...o, w: Math.max(sizeEmu.cx * .01, o.w + dx), h: Math.max(sizeEmu.cy * .01, o.h + dy) } : { ...o, x: o.x + dx, y: o.y + dy };
    Object.assign(b.style, { left: r.x * scale + 'px', top: r.y * scale + 'px', width: r.w * scale + 'px', height: r.h * scale + 'px' });
  };
  const up = () => { removeEventListener('pointermove', mv); removeEventListener('pointerup', up); if (moved) change(() => { const { doc, sh } = find(cur, s.id); if (sh) { setRect(sh, r); writer._saveSlideDoc(cur, doc); } }, true); };
  addEventListener('pointermove', mv); addEventListener('pointerup', up);
}
function editText(s, b) {
  if (editing) return;
  const ta = el('textarea', 'ted'); ta.value = s.text || '';
  Object.assign(ta.style, { left: b.style.left, top: b.style.top, width: b.style.width, height: Math.max(parseFloat(b.style.height), 60) + 'px' });
  $('overlay').appendChild(ta); editing = ta; ta.focus(); ta.select();
  const done = save => { if (editing !== ta) return; editing = null; const v = ta.value; ta.remove(); if (save && v !== (s.text || '')) change(() => { const { doc, sh } = find(cur, s.id); if (sh) { setText(sh, v); writer._saveSlideDoc(cur, doc); } }, true); };
  ta.addEventListener('blur', () => done(true));
  ta.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Escape') done(false); });
}
function updTools() {
  const { sh } = selId ? find(cur, selId) : { sh: null }, f = curFormat(sh), txt = !!(sh && sh.text != null);
  for (const id of ['tB', 'tI', 'tU', 'tSize', 'tColor', 'tAl', 'tAc', 'tAr']) $(id).disabled = !txt;
  $('tDel').disabled = !sh;
  $('tB').classList.toggle('on', !!f.bold); $('tI').classList.toggle('on', !!f.italic); $('tU').classList.toggle('on', !!f.underline);
  if (f.size) $('tSize').value = f.size;
}

// ---------- 播放、列印、PDF ----------
function play(from) { if (!renderer) return; selId = null; drawOverlay(); const show = renderer.createShow(document.body, { fullscreen: true, showHud: true }); show.start(from == null ? cur : from); }
async function print() {
  toast(T('sl_preparing'), 60000);
  const f = document.createElement('iframe'); f.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0;visibility:hidden'; document.body.appendChild(f);
  const d = f.contentDocument; d.write('<!doctype html><meta charset="utf-8"><style>@page{size:landscape;margin:0}body{margin:0}img{width:100vw;height:100vh;object-fit:contain;display:block;break-after:page}</style>');
  for (let i = 0; i < count(); i++) { const img = d.createElement('img'); img.src = await renderer.toDataURL(i, 1600, 'image/jpeg', .92); d.body.appendChild(img); }
  d.close(); $('toast').style.display = 'none';
  setTimeout(() => { f.contentWindow.focus(); f.contentWindow.print(); setTimeout(() => f.remove(), 60000); }, 300);
}
async function exportPdf() { toast(T('sl_preparing'), 60000); try { shell.exportFile(await renderer.toPdf({ width: 1920 }), shell.name.replace(/\.pptx$/i, '') + '.pdf'); } catch (e) { toast(String(e && e.message || e), 6000); } }

// ---------- 開檔 / 新增 / 儲存 ----------
async function openBytes(buf, name, token) { writer = await PptxWriter.fromBytes(buf); undo = []; redo = []; cur = 0; await refresh(); shell.opened(name, token); }
async function newDoc() {
  const bin = atob(BLANK_PPTX), u = new Uint8Array(bin.length); for (let i = 0; i < bin.length; i++) u[i] = bin.charCodeAt(i);
  writer = await PptxWriter.fromBytes(u.buffer); undo = []; redo = []; cur = 0;
  const s = shapes(0);
  for (const x of s.list) { if (x.text != null) setText(x, x.name === 'Title' ? T('sl_title_ph') : T('sl_sub_ph')); const sp = kid(x.node, 'spPr'), ln = sp && kid(sp, 'ln'); if (ln && !ln.children.length) ln.remove(); }   // 空嘅 <a:ln/> 會畫出外框
  writer._saveSlideDoc(0, s.doc);
  await refresh(); shell.opened(T('sl_untitled') + '.pptx', null);
}
const getBytes = async () => { if (editing) editing.blur(); return writer.save(); };

// ---------- AI ----------
function aiContext() {
  let text = '';
  for (let i = 0; i < count() && text.length < 45000; i++) { text += '## ' + T('sl_slide') + ' ' + (i + 1) + '\n'; for (const s of shapes(i).list) text += '[' + s.id + '] ' + (s.ph || s.kind) + (s.text != null ? ': ' + s.text.replace(/\n/g, ' / ') : '') + '\n'; }
  return { text, info: String(count()) };
}
async function aiApply(list) {
  let n = 0;
  await change(async () => {
    // 刪除 / 加投影片會改編號：由大編號做起
    const ord = list.map((a, i) => ({ a, i })).sort((x, y) => ((y.a.slide || y.a.after || y.a.from || 0) - (x.a.slide || x.a.after || x.a.from || 0)) || (x.i - y.i));
    for (const { a } of ord) {
      try {
        const i = (a.slide || 1) - 1, pct = (v, base, def) => (v == null ? def : Math.max(0, Math.min(100, +v))) / 100 * base;
        if (a.op === 'text') { const { doc, sh } = find(i, a.shape); if (!sh || sh.text == null) continue; setText(sh, String(a.text ?? '')); writer._saveSlideDoc(i, doc); n++; }
        else if (a.op === 'addText') { addTextBox(i, String(a.text ?? ''), { x: pct(a.x, sizeEmu.cx, 10), y: pct(a.y, sizeEmu.cy, 10), w: pct(a.w, sizeEmu.cx, 80), h: pct(a.h, sizeEmu.cy, 15), fontSize: Math.round((+a.size || 20) * 100), bold: !!a.bold, color: (a.color || '#000000').replace('#', ''), fontFamily: 'Arial' }); n++; }
        else if (a.op === 'deleteShape') { const { doc, sh } = find(i, a.shape); if (!sh) continue; sh.node.remove(); writer._saveSlideDoc(i, doc); n++; }
        else if (a.op === 'addSlide') { const at = Math.max(0, Math.min(count(), +a.after || count())); const src = Math.max(0, at - 1); writer.duplicateSlide(src, at); dropNotesRel(at); const s = shapes(at); for (const x of s.list) x.node.remove(); writer._saveSlideDoc(at, s.doc);
          addTextBox(at, String(a.title || ''), { x: sizeEmu.cx * .06, y: sizeEmu.cy * .07, w: sizeEmu.cx * .88, h: sizeEmu.cy * .16, fontSize: 3600, bold: true, fontFamily: 'Arial' });
          if (a.body) addTextBox(at, String(a.body), { x: sizeEmu.cx * .06, y: sizeEmu.cy * .27, w: sizeEmu.cx * .88, h: sizeEmu.cy * .62, fontSize: 2000, fontFamily: 'Arial' });
          const s2 = shapes(at); const body = s2.list[1]; if (body && a.body) { setText(body, String(a.body)); writer._saveSlideDoc(at, s2.doc); }
          n++; }
        else if (a.op === 'deleteSlide' && count() > 1) { writer.removeSlide(i); n++; }
        else if (a.op === 'moveSlide') { const o = [...Array(count()).keys()]; o.splice(a.to - 1, 0, o.splice(a.from - 1, 1)[0]); writer.reorderSlides(o); n++; }
        else if (a.op === 'background' && a.color) { writer.setSlideBackground(i, a.color.replace('#', '').toUpperCase()); n++; }
        else if (a.op === 'notes') { if (setNotes(i, String(a.text ?? ''))) n++; }
      } catch (e) { console.error(a, e); }
    }
  });
  return n;
}

// ---------- 介面 ----------
$('stage').innerHTML = '<div id="tools"></div><div id="work"><div id="thumbs"></div><div id="mid"><div id="slideArea"><div id="slideWrap"><canvas id="slideCanvas"></canvas><div id="overlay"></div></div></div>' +
  '<div id="notesBar"><textarea id="notes"></textarea></div></div></div>';
const tb = (id, label, title, run, cls) => { const b = el('button', 'rb' + (cls ? ' ' + cls : ''), label); b.id = id; b.title = title || ''; b.onclick = run; $('tools').appendChild(b); return b; };
const sepT = () => $('tools').appendChild(el('span', 'tsep'));
tb('tNew', '＋ ' + T('sl_slide'), T('sl_new'), newSlide); tb('tText', 'T ' + T('sl_textbox'), '', addText); tb('tImg', '🖼 ' + T('sl_image'), '', addImage); sepT();
tb('tB', 'B', T('bold'), () => fmtSel({ bold: !$('tB').classList.contains('on') }), 'fb'); tb('tI', 'I', T('italic'), () => fmtSel({ italic: !$('tI').classList.contains('on') }), 'fi'); tb('tU', 'U', T('underline'), () => fmtSel({ underline: !$('tU').classList.contains('on') }), 'fu');
{ const i = el('input'); i.type = 'number'; i.id = 'tSize'; i.min = 6; i.max = 400; i.value = 24; i.title = T('sl_size'); i.onchange = () => fmtSel({ size: Math.max(6, Math.min(400, +i.value || 24)) }); i.addEventListener('keydown', e => e.stopPropagation()); $('tools').appendChild(i);
  const c = el('input'); c.type = 'color'; c.id = 'tColor'; c.value = '#000000'; c.title = T('sl_color'); c.onchange = () => fmtSel({ color: c.value }); $('tools').appendChild(c); }
tb('tAl', '⯇', T('al_left'), () => fmtSel({ align: 'l' })); tb('tAc', '≡', T('al_center'), () => fmtSel({ align: 'ctr' })); tb('tAr', '⯈', T('al_right'), () => fmtSel({ align: 'r' })); sepT();
tb('tDel', '🗑', T('sl_delobj'), delSel); $('tools').appendChild(el('span', 'grow')); $('tools').appendChild(Object.assign(el('span', 'pinfo'), { id: 'pageInfo' }));
tb('tPlay', '▶ ' + T('sl_play'), 'F5', () => play(), 'pri');
$('notes').placeholder = T('sl_notes_ph');
$('notes').addEventListener('keydown', e => e.stopPropagation());
$('notes').addEventListener('change', () => { const v = $('notes').value; change(() => { setNotes(cur, v); }, true); });
$('slideArea').addEventListener('pointerdown', e => { if (!e.target.closest('.shp, .ted')) { selId = null; [...$('overlay').children].forEach(x => x.classList.remove('sel')); updTools(); } });
addEventListener('keydown', e => {
  if (!writer || editing || /^(INPUT|TEXTAREA|SELECT)$/.test((document.activeElement || {}).tagName || '')) return;
  const c = e.ctrlKey || e.metaKey, k = e.key;
  if (c && k.toLowerCase() === 'z') { e.preventDefault(); unredo(undo, redo); }
  else if (c && k.toLowerCase() === 'y') { e.preventDefault(); unredo(redo, undo); }
  else if (c && k.toLowerCase() === 'm') { e.preventDefault(); newSlide(); }
  else if (c && k.toLowerCase() === 'd') { e.preventDefault(); dupSlide(); }
  else if (k === 'Delete' || k === 'Backspace') { if (selId) { e.preventDefault(); delSel(); } }
  else if (k === 'F5') { e.preventDefault(); play(e.shiftKey ? cur : 0); }
  else if (k === 'PageDown' || (k === 'ArrowDown' && !selId)) { e.preventDefault(); go(cur + 1); }
  else if (k === 'PageUp' || (k === 'ArrowUp' && !selId)) { e.preventDefault(); go(cur - 1); }
  else if (selId && /^Arrow/.test(k)) { e.preventDefault(); const d = (e.shiftKey ? 0.002 : 0.01) * sizeEmu.cx, dx = k === 'ArrowLeft' ? -d : k === 'ArrowRight' ? d : 0, dy = k === 'ArrowUp' ? -d : k === 'ArrowDown' ? d : 0; onSel(sh => { const r = sh.rect; if (r) setRect(sh, { ...r, x: r.x + dx, y: r.y + dy }); }); }
});
let rz = 0; addEventListener('resize', () => { clearTimeout(rz); rz = setTimeout(() => { if (renderer && !busyRender) drawMain(); }, 150); });

const MENUS = [
  { label: T('m_file'), items: () => [
    { label: T('m_new'), icon: '＋', run: () => { if (shell.okToLeave()) newDoc(); } }, { label: T('open'), key: 'Ctrl+O', run: shell.pick }, '-',
    { label: T('save'), key: 'Ctrl+S', run: () => shell.save(false) }, { label: T('saveas'), key: 'Ctrl+Shift+S', run: () => shell.save(true) }, { label: T('sl_pdf'), run: exportPdf }, '-', { label: T('print'), key: 'Ctrl+P', run: print }] },
  { label: T('m_edit'), items: () => [
    { label: T('undo'), icon: '↶', key: 'Ctrl+Z', disabled: !undo.length, run: () => unredo(undo, redo) }, { label: T('redo'), icon: '↷', key: 'Ctrl+Y', disabled: !redo.length, run: () => unredo(redo, undo) }, '-',
    { label: T('sl_delobj'), key: 'Delete', disabled: !selId, run: delSel }, { label: T('sl_dup'), key: 'Ctrl+D', run: dupSlide }] },
  { label: T('m_view'), items: () => [
    { label: T('sl_play'), icon: '▶', key: 'Shift+F5', run: () => play() }, { label: T('sl_play0'), key: 'F5', run: () => play(0) }, '-',
    { label: T('sl_shownotes'), check: !$('notesBar').classList.contains('off'), run: () => { $('notesBar').classList.toggle('off'); drawMain(); } }] },
  { label: T('m_insert'), items: () => [
    { label: T('sl_textbox'), icon: 'T', run: addText }, { label: T('sl_image'), icon: '🖼', run: addImage }, '-', { label: T('sl_new'), key: 'Ctrl+M', run: newSlide }] },
  { label: T('m_format'), items: () => [
    { label: T('sh_text'), sub: [{ label: T('bold'), run: () => fmtSel({ bold: !curFormat(find(cur, selId).sh).bold }) }, { label: T('italic'), run: () => fmtSel({ italic: !curFormat(find(cur, selId).sh).italic }) }, { label: T('underline'), run: () => fmtSel({ underline: !curFormat(find(cur, selId).sh).underline }) }] },
    { label: T('sh_align'), sub: [{ label: T('al_left'), run: () => fmtSel({ align: 'l' }) }, { label: T('al_center'), run: () => fmtSel({ align: 'ctr' }) }, { label: T('al_right'), run: () => fmtSel({ align: 'r' }) }] },
    { label: T('sl_size'), sub: [12, 16, 20, 24, 32, 44, 60, 80].map(z => ({ label: String(z), run: () => fmtSel({ size: z }) })) }] },
  { label: T('m_slide'), items: () => [
    { label: T('sl_new'), key: 'Ctrl+M', run: newSlide }, { label: T('sl_dup'), key: 'Ctrl+D', run: dupSlide }, { label: T('sl_del'), run: delSlide }, '-',
    { label: T('sl_up'), disabled: cur === 0, run: () => moveSlide(-1) }, { label: T('sl_down'), disabled: cur >= count() - 1, run: () => moveSlide(1) }, '-', { label: T('sl_bg'), run: background }] },
  { label: T('m_arrange'), items: () => [
    { label: T('sl_front'), disabled: !selId, run: () => order(true) }, { label: T('sl_back'), disabled: !selId, run: () => order(false) }, '-',
    { label: T('sl_center_h'), disabled: !selId, run: () => onSel(sh => sh.rect && setRect(sh, { ...sh.rect, x: (sizeEmu.cx - sh.rect.w) / 2 })) }, { label: T('sl_center_v'), disabled: !selId, run: () => onSel(sh => sh.rect && setRect(sh, { ...sh.rect, y: (sizeEmu.cy - sh.rect.h) / 2 })) }] },
  { label: T('m_tools'), items: () => [{ label: T('sl_stats'), icon: '#', run: () => { let w = 0; for (let i = 0; i < count(); i++) for (const s of shapes(i).list) w += (s.text || '').replace(/\s/g, '').length; dialog(T('sl_stats'), T('sl_stats_body', count(), w)); } }] },
];
initShell({ ext: 'pptx', getBytes, print, openBytes, newDoc, aiContext, aiApply, onResize: () => renderer && drawMain() });
buildMenubar($('menubar'), MENUS);
window.__test = { openBytes, newDoc, getBytes, aiApply, aiContext, shapes: i => shapes(i).list.map(s => ({ id: s.id, name: s.name, kind: s.kind, rect: s.rect, text: s.text, ph: s.ph })), go, newSlide, dupSlide, delSlide, addText, get cur() { return cur; }, get count() { return count(); }, sel: id => { selId = String(id); drawOverlay(); }, fmtSel, unredo: () => unredo(undo, redo) };
if (location.hash === '#new') newDoc().then(() => busy('')); else busy(T('loading'));
post({ t: 'ready', page: 'slides' });
