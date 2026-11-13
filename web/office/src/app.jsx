// 文書工具（文件編輯器）網頁部分。編輯器用 @eigenpal/docx-editor-react（Apache-2.0）。
// 同主程式（src/Office.cs）用 chrome.webview 訊息溝通；介面文字由 strings.js（window.L / window.CFG）提供。
import React, { useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { DocxEditor, createEmptyDocument } from '@eigenpal/docx-editor-react';
import { AllSelection } from 'prosemirror-state';
import { de, fr, ptBR, zhCN, en as EN } from '@eigenpal/docx-editor-i18n';
import zhHant from './zh-Hant.json';
import { setupAttach } from './ai-attach.js';
import '@eigenpal/docx-editor-react/styles.css';

const L = window.L || {}, CFG = window.CFG || {};
const T = (k, ...a) => (L[k] || k).replace(/\{(\d)\}/g, (_, i) => a[+i] ?? '');
const $ = id => document.getElementById(id);
const host = window.chrome && chrome.webview ? chrome.webview : null;
const post = m => { if (host) host.postMessage(m); else if (window.__post) window.__post(m); };
const LOCALE = { 'zh-Hant': zhHant, 'zh-Hans': zhCN, de, fr, 'pt-BR': ptBR }[CFG.lang] || undefined;   // 其他語言用編輯器內置英文
const LT = k => (LOCALE && LOCALE.toolbar && LOCALE.toolbar[k]) || EN.toolbar[k];   // 編輯器自己選單嘅文字（用嚟認返邊個選單）

// ---------- 主題顏色（跟程式）----------
if (CFG.colors) for (const [k, v] of Object.entries(CFG.colors)) document.documentElement.style.setProperty('--r-' + k, v);

function toast(s, ms = 2600) { const t = $('toast'); t.textContent = s; t.style.display = 'block'; clearTimeout(toast.h); toast.h = setTimeout(() => t.style.display = 'none', ms); }
function busy(s) { $('busy').textContent = s || ''; $('busy').classList.toggle('show', !!s); }

// ---------- 狀態 ----------
let ed = null;            // DocxEditorRef
let dirty = false, quiet = 0, saveAs = false, seq = 0;
let setFile = () => {};   // React：{ key, name, buffer | null }
let curName = '';
let viewState = { mode: 'editing', ruler: false, outline: false }, setViewState = () => {};   // 「檢視」選單（React 狀態）
const okToLeave = () => !dirty || confirm(T('leave'));

// ---------- 首頁 ----------
$('hTitle').textContent = T('title'); $('hSub').textContent = T('sub'); $('hOpen').textContent = T('openfile'); $('hDrop').textContent = T('drop');
$('hWin').textContent = T('newwin'); $('hDef').textContent = T('defapps'); $('newT').textContent = T('newt');
$('rTitle').textContent = T('recent'); $('rClear').textContent = T('recentclear');
document.querySelectorAll('.kind').forEach(k => {
  const kind = k.dataset.kind;
  k.querySelector('b').textContent = T('k_' + kind);
  if (k.classList.contains('soon')) { k.querySelector('.tag').textContent = T('soon'); return; }
  k.querySelector('.open').textContent = T('open');
  if (k.querySelector('.new')) { k.querySelector('.new').textContent = T('new'); k.querySelector('.new').onclick = () => newDoc(kind); }
  k.querySelector('.open').onclick = () => post({ t: 'pick', kind });
});
$('hOpen').onclick = () => post({ t: 'pick', kind: 'any' });
$('hWin').onclick = () => post({ t: 'new-window' });
$('hDef').onclick = () => post({ t: 'default-apps' });
if (host) { $('hWin').style.display = ''; $('hDef').style.display = ''; }
function drawRecent(list) {
  $('recent').style.display = list && list.length ? 'block' : 'none';
  $('rList').textContent = '';
  for (const r of list || []) {
    const d = document.createElement('div'); d.className = 'it';
    d.innerHTML = '<div class="fi">' + (/\.pdf$/i.test(r.name) ? '📄' : /\.xlsx$/i.test(r.name) ? '📊' : /\.pptx$/i.test(r.name) ? '📽️' : '📝') + '</div><div class="c"><div class="n"></div><div class="p"></div></div><button class="rb x">✕</button>';
    d.querySelector('.n').textContent = r.name; d.querySelector('.p').textContent = r.dir;
    d.querySelector('.x').title = T('recentdel');
    d.querySelector('.x').onclick = e => { e.stopPropagation(); post({ t: 'recent-remove', path: r.path }); };
    d.onclick = () => post({ t: 'recent-open', path: r.path });
    $('rList').appendChild(d);
  }
}
$('rClear').onclick = () => post({ t: 'recent-clear' });
drawRecent(CFG.recent);

// ---------- 開檔 / 新增 / 返回 ----------
function show(file) {
  dirty = false; quiet = Date.now() + 1500; curName = file ? file.name : '';
  setFile(file);
  $('app').classList.toggle('doc', !!file);
  // token = 主程式交出嚟嘅檔案（有真實路徑，可以直接儲存）；冇 = 新文件 / 拖入但攞唔到路徑，第一次儲存要揀位置
  if (!file) { $('ai').classList.remove('show'); post({ t: 'closed' }); } else post({ t: 'opened', name: file.name, token: file.token || null });
}
function newDoc(kind) {
  if (kind !== 'docx') { post({ t: 'new-doc', kind }); return; }   // 試算表 / 簡報：主程式叫呢頁轉去嗰個編輯器開新檔
  if (!okToLeave()) return;
  show({ key: ++seq, name: T('untitled') + '.docx', buffer: null });
}
async function openBytes(buf, name, token) {
  if (!/\.docx$/i.test(name)) { toast(T('notsupported'), 5000); return; }
  if (!okToLeave()) return;
  show({ key: ++seq, name, buffer: buf, token });
}
/** 撳檔名改名（同 Google 一樣）：已經儲存過嘅檔案會連磁碟上面個檔一齊改名；未儲存過就只係改之後儲存用嘅名。 */
function rename(v) {
  v = String(v || '').replace(/[\\/:*?"<>|]/g, '').trim().replace(/\.docx$/i, '').trim();
  const old = curName, next = v + '.docx';
  if (!v || next === old) { window.__setName(old + ' '); setTimeout(() => window.__setName(old), 0); return; }   // 唔啱：還原顯示
  window.__setName(next);
  post({ t: 'rename', name: next, old });
}
function goHome() { if (okToLeave()) show(null); }

// ---------- 儲存 ----------
function b64(u8) { let s = ''; for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000)); return btoa(s); }
async function save(as) {
  if (!ed) return;
  saveAs = !!as;
  toast(T('saving'), 60000);
  try { const r = await ed.save(); if (!r) toast(T('savefail', ''), 7000); }   // 成功會行 onSave → sendSave
  catch (e) { toast(T('savefail', e && e.message || e), 7000); }
}
function sendSave(buffer) {
  const bytes = new Uint8Array(buffer);
  window.__last = bytes;
  post({ t: 'save-begin', name: curName, as: saveAs, size: bytes.length });
  saveAs = false;
  const CH = 3 * 1024 * 1024;
  for (let i = 0; i < bytes.length; i += CH) post({ t: 'save-chunk', d: b64(bytes.subarray(i, Math.min(bytes.length, i + CH))) });
  post({ t: 'save-end' });
  if (!host) { dirty = false; toast('ok'); }
}

// ---------- 列印：編輯器會開新視窗嚟印，WebView2 入面改用隱藏 iframe ----------
const realOpen = window.open.bind(window);
window.open = function (url, target, feat) {
  if (url) return null;   // 外部連結：唔開（主程式會擋）
  const f = document.createElement('iframe');
  f.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0;visibility:hidden';
  document.body.appendChild(f);
  const w = f.contentWindow;
  const px = { document: w.document, closed: false, onload: null,
    print() { try { w.focus(); w.print(); } catch (e) { } },
    close() { px.closed = true; setTimeout(() => f.remove(), 60000); } };
  return px;
};

// ---------- 編輯器 ----------
function Bar() {
  return (
    <div className="rbar">
      <button className="rb" onClick={goHome}>{T('back')}</button>
      <button className="rb pri" onClick={() => post({ t: 'pick', kind: 'any' })}>{T('open')}</button>
    </div>
  );
}
/** 儲存掣：右邊有下箭咀，撳開可以揀「另存新檔」/「列印」。 */
function SaveMenu() {
  const [open, setOpen] = useState(false);
  const box = useRef(null);
  useEffect(() => {
    if (!open) return;
    const close = e => { if (!box.current || !box.current.contains(e.target)) setOpen(false); };
    addEventListener('pointerdown', close, true);
    return () => removeEventListener('pointerdown', close, true);
  }, [open]);
  const run = f => { setOpen(false); f(); };
  return (
    <div className="split" ref={box}>
      <button className="rb ok l" onClick={() => save(false)}>{T('save')}</button>
      <button className="rb ok r" title={T('more')} onClick={() => setOpen(!open)}>▾</button>
      {open && <div className="menu">
        <button onClick={() => run(() => save(true))}>{T('saveas')}</button>
        <button onClick={() => run(() => ed && ed.print())}>{T('print')}</button>
      </div>}
    </div>
  );
}
function Right({ ai }) {
  return (
    <div className="rbar">
      {host && <button className="rb" title={T('newwin')} onClick={() => post({ t: 'new-window' })}>⧉</button>}
      {ai && <button className="rb" onClick={() => $('ai').classList.toggle('show')}>✦ AI</button>}
      <SaveMenu />
    </div>
  );
}
/** 開始編輯而家嗰頁嘅頁首 / 頁尾（同雙擊頁面頂 / 底一樣）。 */
function editHf(which) {
  if (!ed) return;
  const list = document.querySelectorAll('.layout-page-' + which);
  const h = list[Math.max(0, Math.min(list.length - 1, (ed.getCurrentPage() || 1) - 1))];
  if (!h) return;
  h.scrollIntoView({ block: 'center' });
  setTimeout(() => {
    const r = h.getBoundingClientRect();
    const o = { bubbles: true, cancelable: true, clientX: r.left + r.width / 2, clientY: r.top + r.height / 2, view: window, detail: 2 };
    for (const t of ['mousedown', 'mouseup', 'click', 'dblclick']) h.dispatchEvent(new MouseEvent(t, o));
  }, 80);
}
/** 喺文件最頂加一頁封面：標題、副標題、作者、日期（都係示範文字，直接改），後面分頁。 */
function insertCover() {
  const v = view();
  if (!v) return;
  const sc = v.state.schema, P = sc.nodes.paragraph;
  const line = (text, pt, bold, color) => {
    const marks = [];
    if (text) { if (sc.marks.fontSize) marks.push(sc.marks.fontSize.create({ size: pt * 2, sizeCs: pt * 2 })); if (bold && sc.marks.bold) marks.push(sc.marks.bold.create()); if (color && sc.marks.textColor) marks.push(sc.marks.textColor.create({ rgb: color })); }
    return P.create({ alignment: 'center' }, text ? sc.text(text, marks) : null);
  };
  const nodes = [];
  for (let i = 0; i < 7; i++) nodes.push(line(''));
  nodes.push(line(T('cv_title'), 32, true), line(''), line(T('cv_sub'), 16, false, '5B6475'));
  for (let i = 0; i < 9; i++) nodes.push(line(''));
  nodes.push(line(T('cv_author'), 12), line(new Date().toLocaleDateString(), 12));
  if (sc.nodes.pageBreak) nodes.push(sc.nodes.pageBreak.create());
  let tr = v.state.tr, at = 0;
  for (const n of nodes) { tr = tr.insert(at, n); at += n.nodeSize; }
  v.dispatch(tr);
  dirty = true;
  ed.scrollToPage(1);
}
function App() {
  const [file, set] = useState(null);
  const [ai, setAi] = useState(!!CFG.ai);
  const [name, setName] = useState('');
  const [vw, setVw] = useState({ mode: 'editing', ruler: false, outline: false });
  viewState = vw; setViewState = setVw;
  const ref = useRef(null);
  useEffect(() => { setFile = f => { set(f); setName(f ? f.name : ''); }; window.__setAi = setAi; window.__setName = n => { curName = n; setName(n); }; }, []);
  useEffect(() => { ed = file ? ref.current : null; });
  if (!file) return null;
  const common = {
    ref, mode: vw.mode, onModeChange: m => setVw(v => ({ ...v, mode: m })), showRuler: vw.ruler, rulerUnit: 'cm', showOutline: vw.outline, colorMode: CFG.light ? 'light' : 'dark', i18n: LOCALE,
    showFileOpen: false, showHelpMenu: false, documentName: name.replace(/\.docx$/i, ''), documentNameEditable: true, onDocumentNameChange: rename,
    renderLogo: () => <Bar />, renderTitleBarRight: () => <Right ai={ai} />,
    onSave: sendSave, onPrint: () => { },
    onChange: () => { if (Date.now() > quiet) dirty = true; },
    onError: e => { busy(''); toast(T('openfail') + (e && e.message ? '：' + e.message : ''), 8000); },
    style: { height: '100%' },
  };
  return file.buffer
    ? <DocxEditor key={file.key} documentBuffer={file.buffer} {...common} />
    : <DocxEditor key={file.key} document={file.doc || (file.doc = createEmptyDocument())} {...common} />;
}
createRoot($('root')).render(<App />);


// ---------- 選單列：跟 Google 文件嘅次序（檔案、編輯、檢視、插入、格式、工具）----------
// 編輯器自己有「檔案 / 格式 / 插入」；「編輯 / 檢視 / 工具」同「插入」入面嘅封面、頁首、頁尾係我哋加嘅（直接加 DOM，編輯器重畫唔會郁到）。
const ITEM_CSS = 'display:flex;align-items:center;gap:8px;padding:6px 12px;border:none;background:transparent;cursor:pointer;font-size:13px;color:var(--doc-text);width:100%;text-align:left;white-space:nowrap;';
function menuItem(label, run, opt = {}) {
  const b = document.createElement('button'); b.type = 'button'; b.style.cssText = ITEM_CSS; b.dataset.rabi = '1';
  const ic = document.createElement('span'); ic.style.cssText = 'width:18px;text-align:center;flex:none;'; ic.textContent = opt.check ? '✓' : (opt.icon || ''); b.appendChild(ic);
  const t = document.createElement('span'); t.textContent = label; b.appendChild(t);
  if (opt.key) { const k = document.createElement('span'); k.style.cssText = 'margin-left:auto;padding-left:24px;color:var(--doc-text-muted);font-size:12px;'; k.textContent = opt.key; b.appendChild(k); }
  b.onmouseenter = () => b.style.background = 'var(--doc-bg-hover)'; b.onmouseleave = () => b.style.background = 'transparent';
  b.onclick = e => { e.stopPropagation(); closeMenus(); closeTheirs(); setTimeout(run, 0); };
  return b;
}
function menuSep() { const d = document.createElement('div'); d.style.cssText = 'height:1px;background-color:var(--doc-border);margin:4px 0;'; d.dataset.rabi = '1'; return d; }
let openMenu = null;
/** 編輯器自己嘅選單（檔案 / 插入 / 格式）：開住嘅話再撳一下個名就閂。 */
function theirOpen() { const bar = document.querySelector('#root [role=menubar]'); return bar ? [...bar.children].find(c => !c.dataset.rabiMenu && c.children.length > 1) : null; }
function closeTheirs() { const c = theirOpen(); if (c) c.querySelector('button').click(); }
function closeMenus() { if (openMenu) { openMenu.panel.remove(); openMenu.btn.style.background = 'transparent'; openMenu = null; } }
addEventListener('pointerdown', e => { if (openMenu && !openMenu.panel.contains(e.target) && e.target !== openMenu.btn) closeMenus(); }, true);
addEventListener('keydown', e => { if (e.key === 'Escape') closeMenus(); }, true);
addEventListener('blur', closeMenus);
/** 我哋自己嘅選單（樣式抄編輯器嘅）。items() 每次開先砌，等剔號跟住而家嘅狀態。 */
function makeMenu(label, order, items, like) {
  const w = document.createElement('div'); w.style.cssText = 'position:relative;order:' + order; w.dataset.rabiMenu = '1';
  const b = document.createElement('button'); b.type = 'button'; b.style.cssText = like.style.cssText; b.style.background = 'transparent'; b.textContent = label;
  b.onmouseenter = () => { if (!openMenu || openMenu.btn !== b) b.style.background = 'var(--doc-bg-hover)'; };
  b.onmouseleave = () => { if (!openMenu || openMenu.btn !== b) b.style.background = 'transparent'; };
  b.onclick = () => {
    if (openMenu && openMenu.btn === b) { closeMenus(); return; }
    closeMenus();
    closeTheirs();   // 閂咗編輯器自己開住嘅選單
    const r = b.getBoundingClientRect(), p = document.createElement('div');
    p.style.cssText = 'position:fixed;top:' + (r.bottom + 2) + 'px;left:' + r.left + 'px;background-color:var(--doc-surface);border:1px solid var(--doc-border);border-radius:6px;box-shadow:0 4px 12px var(--doc-shadow);padding:4px 0;z-index:10000;min-width:220px;';
    for (const it of items()) p.appendChild(it);
    b.parentElement.closest('.ep-root').appendChild(p);
    b.style.background = 'var(--doc-bg-hover)';
    openMenu = { btn: b, panel: p };
  };
  w.appendChild(b);
  return w;
}
function pmUndo(redo) { const r = ed && ed.getEditorRef(); if (r) { redo ? r.redo() : r.undo(); r.focus(); } }
function selectAll() { const v = view(); if (v) { v.dispatch(v.state.tr.setSelection(new AllSelection(v.state.doc))); v.focus(); } }
function findReplace() { const v = view(); if (!v) return; v.focus(); const o = { key: 'h', code: 'KeyH', ctrlKey: true, bubbles: true, cancelable: true }; (document.activeElement || document).dispatchEvent(new KeyboardEvent('keydown', o)); }
function setView(patch) { setViewState(v => ({ ...v, ...patch })); }
function wordCount() {
  const v = view(); if (!v) return;
  const text = v.state.doc.textBetween(0, v.state.doc.content.size, '\n', ' ');
  const cjk = (text.match(/[぀-ヿ㐀-鿿가-힯豈-﫿]/g) || []).length;
  const latin = (text.replace(/[぀-ヿ㐀-鿿가-힯豈-﫿]/g, ' ').match(/[^\s\p{P}]+/gu) || []).length;
  const chars = text.replace(/\n/g, '').length, nosp = text.replace(/\s/g, '').length;
  const rows = [[T('wc_pages'), ed.getTotalPages()], [T('wc_words'), cjk + latin], [T('wc_chars'), chars], [T('wc_nospace'), nosp]];
  const m = document.createElement('div'); m.className = 'rmask';
  m.innerHTML = '<div class="rdlg"><h3></h3><table></table><div class="row"><button class="rb pri"></button></div></div>';
  m.querySelector('h3').textContent = T('wordcount');
  for (const [k, n] of rows) { const tr = m.querySelector('table').insertRow(); tr.insertCell().textContent = k; tr.insertCell().textContent = Number(n).toLocaleString(); }
  const ok = m.querySelector('button'); ok.textContent = T('close'); ok.onclick = () => m.remove();
  m.onclick = e => { if (e.target === m) m.remove(); };
  $('app').appendChild(m); ok.focus();
}
function setupMenus() {
  const bar = document.querySelector('#root [role=menubar]');
  if (!bar) return;
  if (!bar.dataset.rabiHover) {
    // 同一般選單列一樣：開咗一個選單之後，滑鼠移去隔籬個名就自動轉過去（我哋加嘅同編輯器自己嘅都一樣）
    bar.dataset.rabiHover = '1';
    bar.addEventListener('mouseover', e => {
      const btn = e.target.closest('button');
      if (!btn || btn.parentElement.parentElement !== bar) return;
      const mine = !!btn.parentElement.dataset.rabiMenu, theirs = theirOpen();
      if (!openMenu && !theirs) return;                                             // 冇選單開住：唔做嘢
      if (mine ? (openMenu && openMenu.btn === btn) : theirs === btn.parentElement) return;   // 已經係呢個
      closeMenus(); closeTheirs();
      btn.click();
    });
  }
  if (!bar.querySelector('[data-rabi-menu]')) {
    const own = [...bar.children], like = own[0] && own[0].querySelector('button');
    if (!like) return;
    const ord = { [LT('file')]: 0, [LT('insert')]: 3, [LT('format')]: 4 };
    for (const c of own) { const t = c.querySelector('button'); if (t && t.textContent.trim() in ord) c.style.order = ord[t.textContent.trim()]; else c.style.order = 6; }
    bar.appendChild(makeMenu(T('m_edit'), 1, () => [
      menuItem(T('undo'), () => pmUndo(false), { icon: '↶', key: 'Ctrl+Z' }), menuItem(T('redo'), () => pmUndo(true), { icon: '↷', key: 'Ctrl+Y' }), menuSep(),
      menuItem(T('selectall'), selectAll, { key: 'Ctrl+A' }), menuItem(T('findreplace'), findReplace, { icon: '⌕', key: 'Ctrl+H' })], like));
    bar.appendChild(makeMenu(T('m_view'), 2, () => [
      menuItem(T('mode_edit'), () => setView({ mode: 'editing' }), { check: viewState.mode === 'editing' }),
      menuItem(T('mode_suggest'), () => setView({ mode: 'suggesting' }), { check: viewState.mode === 'suggesting' }),
      menuItem(T('mode_view'), () => setView({ mode: 'viewing' }), { check: viewState.mode === 'viewing' }), menuSep(),
      menuItem(T('ruler'), () => setView({ ruler: !viewState.ruler }), { check: viewState.ruler }),
      menuItem(T('outline'), () => setView({ outline: !viewState.outline }), { check: viewState.outline })], like));
    bar.appendChild(makeMenu(T('m_tools'), 5, () => [menuItem(T('wordcount'), wordCount, { icon: '#' })], like));
  }
  // 編輯器嘅「插入」選單開咗：喺最尾加封面、頁首、頁尾
  for (const c of bar.children) {
    const t = c.querySelector('button'), panel = c.children[1];
    if (!t || !panel || t.textContent.trim() !== LT('insert') || panel.querySelector('[data-rabi]')) continue;
    panel.append(menuSep(), menuItem(T('cover'), insertCover, { icon: '▤' }), menuItem(T('header'), () => editHf('header'), { icon: '▔' }), menuItem(T('footer'), () => editHf('footer'), { icon: '▁' }));
  }
}
// 樣式下拉選單：文件自帶嘅樣式名（Heading 4…）係英文，顯示時換做介面語言
function localizeStyles(root) {
  const w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  for (let n = w.nextNode(); n; n = w.nextNode()) {
    const m = /^\s*heading (\d)\s*$/i.exec(n.nodeValue);
    if (m && !n.parentElement.closest('.layout-page, .ProseMirror')) n.nodeValue = T('heading_n', m[1]);
  }
}
let menuTick = 0;
new MutationObserver(muts => {
  if (menuTick) return;
  menuTick = requestAnimationFrame(() => { menuTick = 0; setupMenus(); if (L.heading_n && !/^Heading/.test(L.heading_n)) for (const e of document.querySelectorAll('[role=listbox], [role=combobox], [role=option]')) localizeStyles(e); });
}).observe(document.body, { childList: true, subtree: true, characterData: true });   // 下拉選單畫喺 body 下面

// ---------- 快捷鍵 ----------
addEventListener('keydown', e => {
  if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
  const k = e.key.toLowerCase();
  if (k === 'o') { e.preventDefault(); e.stopPropagation(); post({ t: 'pick', kind: 'any' }); }
  else if (k === 's' && ed) { e.preventDefault(); e.stopPropagation(); save(e.shiftKey); }
  else if (k === 'p' && ed) { e.preventDefault(); e.stopPropagation(); ed.print(); }
}, true);

// ---------- 拖放 ----------
addEventListener('dragover', e => { if (e.dataTransfer && [...e.dataTransfer.types].includes('Files')) e.preventDefault(); });
addEventListener('drop', e => {
  const f = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
  if (!f) return;
  e.preventDefault(); e.stopPropagation();
  // 交個 File 俾主程式攞真實路徑（先入到歷史紀錄、之後可以直接儲存）；主程式冇反應就自己讀
  let done = false;
  window.__dropDone = () => { done = true; };
  try { if (host && host.postMessageWithAdditionalObjects) host.postMessageWithAdditionalObjects({ t: 'drop' }, [f]); } catch (er) { }
  setTimeout(async () => { if (!done) openBytes(await f.arrayBuffer(), f.name); }, host ? 1500 : 0);
}, true);

// ---------- AI ----------
$('aiTitle').textContent = T('aititle'); $('aiSend').textContent = T('aisend'); $('aq1').textContent = T('aisum'); $('aq2').textContent = T('aifix'); $('aiIn').placeholder = T('aiask');
function aiAdd(cls, who, text) { const d = document.createElement('div'); d.className = 'm ' + cls; if (who) { const b = document.createElement('b'); b.textContent = who; d.appendChild(b); } d.appendChild(document.createTextNode(text)); $('aiLog').appendChild(d); $('aiLog').scrollTop = 1e9; return d; }
function view() { const r = ed && ed.getEditorRef(); return r ? r.getView() : null; }
/** 文件入面每一段（表格入面嘅都計）：[{ n, pos, node }]，n 由 1 開始。 */
function paras() {
  const v = view(), out = [];
  if (!v) return out;
  v.state.doc.descendants((node, pos) => { if (node.isTextblock) { out.push({ n: out.length + 1, pos, node }); return false; } return true; });
  return out;
}
function docText() {
  let s = '', n = 0;
  for (const p of paras()) {
    // 樣式同字型大細（點數）：等 AI 知而家幾大，「整細一半」先計得啱
    let half = null; p.node.descendants(x => { if (half == null && x.isText) { const m = x.marks.find(k => k.type.name === 'fontSize'); if (m && m.attrs.size) half = m.attrs.size; } return half == null; });
    if (half == null) { const d = p.node.attrs && p.node.attrs.defaultTextFormatting; if (d && d.fontSize) half = d.fontSize; }
    const info = [p.node.attrs && p.node.attrs.styleId, half ? half / 2 + 'pt' : null].filter(Boolean).join(', ');
    const st = info ? ' {' + info + '}' : '';
    const line = '[' + p.n + ']' + st + ' ' + p.node.textContent + '\n';
    if (s.length + line.length > 45000) break;
    s += line; n++;
  }
  return { text: s, count: n };
}
let aiWait = null;
function aiSend(q) {
  q = (q || '').trim();
  if (!q || aiWait || !ed) return;
  aiAdd('me', T('aiyou'), q + attach.label());
  aiWait = aiAdd('note', '', T('aithinking'));
  $('aiIn').value = '';
  const d = docText();
  post({ t: 'ai', q, kind: 'docx', paras: d.count, text: d.text });
}
/** 段落入面第 off 個字嘅文件位置。 */
function posAt(p, off) {
  let left = off, res = p.pos + 1 + p.node.content.size;
  let found = false;
  p.node.descendants((node, pos) => {
    if (found) return false;
    if (node.isText) { if (left <= node.text.length) { res = p.pos + 1 + pos + left; found = true; } else left -= node.text.length; }
    else if (node.isLeaf) { }   // 圖片等：textContent 冇佢，唔計
    return true;
  });
  return res;
}
function applyActions(list) {
  const v = view();
  if (!v) return 0;
  let done = 0;
  // 由後面嘅段落做起：前面段落嘅編號唔會因為加減段落而變
  const order = list.map((a, i) => ({ a, i })).sort((x, y) => ((y.a.p || y.a.after || 0) - (x.a.p || x.a.after || 0)) || (x.i - y.i));
  for (const { a } of order) {
    try {
      const ps = paras(), st = v.state;
      const p = ps[(a.p || a.after || 0) - 1];
      if (a.op === 'replace' && p && a.with != null) {   // 冇寫 with：當 AI 寫錯，唔好刪字
        const txt = p.node.textContent, find = a.find || '';
        let from, to;
        if (find === '') { from = p.pos + 1; to = p.pos + 1 + p.node.content.size; }          // 冇 find：成段換
        else { const i = txt.indexOf(find); if (i < 0) continue; from = posAt(p, i); to = posAt(p, i + find.length); }
        const w = String(a.with ?? '');
        v.dispatch(w ? st.tr.insertText(w, from, to) : st.tr.delete(from, to)); done++;
      } else if (a.op === 'insert' && (p || a.after === 0)) {
        const lines = String(a.text ?? '').split('\n');
        const base = p || ps[0]; if (!base) continue;
        let at = p ? p.pos + p.node.nodeSize : base.pos, tr = st.tr;
        for (const line of lines) {
          const attrs = { ...base.node.attrs };
          for (const k of ['paraId', 'textId', '_originalFormatting', '_originalRunBoundaries', '_sectionProperties', 'sectionBreakType', 'bookmarks', 'pageBreakBefore']) if (k in attrs) attrs[k] = null;
          if (a.style && 'styleId' in attrs) attrs.styleId = a.style;
          const marks = base.node.firstChild && base.node.firstChild.isText ? base.node.firstChild.marks : [];
          const nd = base.node.type.create(attrs, line ? st.schema.text(line, a.style ? [] : marks) : null);
          tr = tr.insert(at, nd); at += nd.nodeSize;
        }
        v.dispatch(tr); done++;
      } else if (a.op === 'delete' && p && st.doc.resolve(p.pos).parent.childCount > 1) {   // 儲存格 / 文件入面最後一段唔刪
        v.dispatch(st.tr.delete(p.pos, p.pos + p.node.nodeSize)); done++;
      } else if (a.op === 'style' && p && a.style) {
        // 有 Word 段落編號就用編輯器自己嘅做法（會連埋樣式嘅字型大細一齊套用）
        if (!(p.node.attrs.paraId && ed.setParagraphStyle({ paraId: p.node.attrs.paraId, styleId: a.style })))
          v.dispatch(st.tr.setNodeMarkup(p.pos, null, { ...p.node.attrs, styleId: a.style }));
        done++;
      } else if (a.op === 'format' && p) {
        const txt = p.node.textContent, find = a.find || '';
        let from = p.pos + 1, to = p.pos + 1 + p.node.content.size;
        if (find) { const i = txt.indexOf(find); if (i < 0) continue; from = posAt(p, i); to = posAt(p, i + find.length); }
        if (formatRange(v, from, to, a)) done++;
      }
    } catch (e) { console.error(e); }
  }
  if (done) dirty = true;
  return done;
}
/** 套用文字格式（粗體 / 斜體 / 底線 / 顏色 / 大小）。mark 名同屬性跟編輯器 schema。 */
function formatRange(v, from, to, a) {
  const sc = v.state.schema.marks; let tr = v.state.tr, n = 0;
  const tog = (name, on) => { const m = sc[name]; if (!m || on == null) return; tr = on ? tr.addMark(from, to, m.create()) : tr.removeMark(from, to, m); n++; };
  tog('bold', a.bold); tog('italic', a.italic); tog('strike', a.strike);
  if (a.underline != null && sc.underline) { tr = a.underline ? tr.addMark(from, to, sc.underline.create({ style: 'single' })) : tr.removeMark(from, to, sc.underline); n++; }
  if (a.color && sc.textColor) { tr = tr.addMark(from, to, sc.textColor.create({ rgb: String(a.color).replace('#', '').toUpperCase() })); n++; }
  if (a.size && sc.fontSize) { tr = tr.addMark(from, to, sc.fontSize.create({ size: Math.round(+a.size * 2) })); n++; }   // Word 用半點
  if (n) v.dispatch(tr);
  return n > 0;
}
function aiReply(text, err, note) {
  if (aiWait) { aiWait.remove(); aiWait = null; }
  if (note) aiAdd('note', '', note);
  if (err) { aiAdd('note', '', T('aifail') + err); return; }
  let acts = null;
  const m = /```json\s*([\s\S]*?)```/i.exec(text || '');
  if (m) { try { acts = JSON.parse(m[1]); } catch (e) { } text = (text.slice(0, m.index) + text.slice(m.index + m[0].length)).trim(); }
  if (text) aiAdd('ai', T('ainame'), text);
  if (Array.isArray(acts) && acts.length) { const n = applyActions(acts); aiAdd('note', '', n ? T('aidone', n) : T('ainone')); }
}
const attach = setupAttach({ ai: $('ai'), post, T, note: s => aiAdd('note', '', s) });
$('aiSend').onclick = () => aiSend($('aiIn').value);
$('aiIn').addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); aiSend($('aiIn').value); } });
$('aq1').onclick = () => aiSend(T('aisum')); $('aq2').onclick = () => aiSend(T('aifix'));
$('aiClose').onclick = () => $('ai').classList.remove('show');

// ---------- 主程式訊息 ----------
async function onHost(m) {
  if (m.t === 'file') {
    if (window.__dropDone) window.__dropDone();
    if (!okToLeave()) return;
    const was = dirty;
    busy(T('loading'));
    try { const r = await fetch(m.url); if (!r.ok) throw new Error(r.status); const buf = await r.arrayBuffer(); busy(''); dirty = false; await openBytes(buf, m.name, m.token); }   // 已經問過，openBytes 唔使再問
    catch (e) { busy(''); dirty = was; toast(T('openfail'), 6000); }
  }
  else if (m.t === 'recent') drawRecent(m.list);
  else if (m.t === 'saved') {
    if (m.ok) { dirty = false; if (m.name) window.__setName(m.name); toast(T('saved', m.name || ''), 3000); }
    else if (m.err) toast(T('savefail', m.err), 7000); else $('toast').style.display = 'none';
  }
  else if (m.t === 'renamed') { window.__setName(m.name); if (!m.ok && m.err) toast(m.err, 6000); }
  else if (m.t === 'ai-reply') aiReply(m.text, m.err, m.note);
  else if (m.t === 'ai-files') attach.onFiles(m);
  else if (m.t === 'cfg') { if (window.__setAi) window.__setAi(!!m.ai); if (!m.ai) $('ai').classList.remove('show'); }
  else if (m.t === 'toast') toast(m.text, 5000);
  // 主程式要轉去另一個編輯器（例如開 PDF）：有未儲存嘅改動就問先
  else if (m.t === 'switch') { if (window.__dropDone) window.__dropDone(); if (okToLeave()) { dirty = false; location.replace(m.url); } else post({ t: 'switch-cancel' }); }
}
if (host) host.addEventListener('message', e => onHost(e.data));
window.__test = { rename, setupMenus, wordCount, findReplace, selectAll, insertCover, editHf, onHost, openBytes, newDoc, paras, docText, applyActions, aiReply, save, view, get ed() { return ed; }, get dirty() { return dirty; } };
post({ t: 'ready', page: 'home' });
