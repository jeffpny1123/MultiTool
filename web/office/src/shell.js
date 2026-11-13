// 文書工具各編輯器（試算表、簡報）共用嘅外殼：第一行（返回 / 開啟 / 選單列 / 開新視窗 / AI / 儲存）、AI 面板、
// 同主程式（src/Office.cs）溝通、未儲存提示、拖放、快捷鍵。文件（.docx）嗰頁有自己一套（app.jsx）。
import { setupAttach } from './ai-attach.js';
export const L = window.L || {}, CFG = window.CFG || {};
export const T = (k, ...a) => (L[k] || k).replace(/\{(\d)\}/g, (_, i) => a[+i] ?? '');
export const $ = id => document.getElementById(id);
export const host = window.chrome && chrome.webview ? chrome.webview : null;
export const post = m => { if (host) host.postMessage(m); else if (window.__post) window.__post(m); };
if (CFG.colors) for (const [k, v] of Object.entries(CFG.colors)) document.documentElement.style.setProperty('--r-' + k, v);
document.documentElement.classList.toggle('light', !!CFG.light);

export function toast(s, ms = 2600) { const t = $('toast'); t.textContent = s; t.style.display = 'block'; clearTimeout(toast.h); toast.h = setTimeout(() => t.style.display = 'none', ms); }
export function busy(s) { $('busy').textContent = s || ''; $('busy').classList.toggle('show', !!s); }
export function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }

// ---------- 選單列（Google 式：撳開一個之後，滑鼠移去隔籬自動轉；支援子選單）----------
let openMenu = null;   // { btn, panels: [] }
export function closeMenus() { if (!openMenu) return; openMenu.panels.forEach(p => p.remove()); openMenu.btn.classList.remove('on'); openMenu = null; }
addEventListener('pointerdown', e => { if (openMenu && !e.target.closest('.mpanel') && e.target !== openMenu.btn) closeMenus(); }, true);
addEventListener('keydown', e => { if (e.key === 'Escape') closeMenus(); }, true);
addEventListener('blur', closeMenus);
function panel(items, x, y, level) {
  const p = el('div', 'mpanel');
  for (const it of items) {
    if (!it) continue;
    if (it === '-') { p.appendChild(el('div', 'msep')); continue; }
    const b = el('button', 'mitem'); b.type = 'button';
    b.appendChild(el('span', 'mi', it.check ? '✓' : (it.icon || '')));
    b.appendChild(el('span', 'ml', it.label));
    if (it.key) b.appendChild(el('span', 'mk', it.key));
    if (it.sub) b.appendChild(el('span', 'mk', '▸'));
    if (it.disabled) b.disabled = true;
    b.onmouseenter = () => {
      while (openMenu && openMenu.panels.length > level + 1) openMenu.panels.pop().remove();
      if (it.sub && openMenu) { const r = b.getBoundingClientRect(); openMenu.panels.push(panel(typeof it.sub === 'function' ? it.sub() : it.sub, r.right - 2, r.top - 5, level + 1)); }
    };
    b.onclick = e => { e.stopPropagation(); if (it.sub) return; closeMenus(); setTimeout(() => { try { it.run && it.run(); } catch (er) { console.error(er); toast(String(er && er.message || er), 5000); } }, 0); };
    p.appendChild(b);
  }
  document.body.appendChild(p);
  // 唔好出界
  const w = p.offsetWidth, h = p.offsetHeight;
  p.style.left = Math.max(4, Math.min(x, innerWidth - w - 4)) + 'px';
  p.style.top = Math.max(4, Math.min(y, innerHeight - h - 4)) + 'px';
  return p;
}
/** defs = [{ label, items: () => [ { label, run, key, icon, check, sub, disabled } | '-' ] }] */
export function buildMenubar(bar, defs) {
  bar.textContent = '';
  for (const d of defs) {
    const b = el('button', 'mtop', d.label); b.type = 'button';
    const open = () => { closeMenus(); const r = b.getBoundingClientRect(); b.classList.add('on'); openMenu = { btn: b, panels: [], at: Date.now() }; openMenu.panels.push(panel(d.items(), r.left, r.bottom + 2, 0)); };
    // 由隔籬選單移過嚟（hover 已經開咗）再撳：唔好當做「再撳一下閂返」
    b.onclick = () => { if (openMenu && openMenu.btn === b) { if (Date.now() - openMenu.at > 400) closeMenus(); } else open(); };
    b.onmouseenter = () => { if (openMenu && openMenu.btn !== b) open(); };
    bar.appendChild(b);
  }
}

// ---------- 簡單對話框 ----------
export function dialog(title, body, buttons) {
  const m = el('div', 'rmask'), d = el('div', 'rdlg');
  d.appendChild(el('h3', '', title));
  if (typeof body === 'string') d.appendChild(el('div', 'rbody', body)); else if (body) d.appendChild(body);
  const row = el('div', 'row');
  const close = () => m.remove();
  for (const [label, run, pri] of buttons || [[T('close'), null, true]]) { const b = el('button', 'rb' + (pri ? ' pri' : ''), label); b.onclick = () => { if (!run || run() !== false) close(); }; row.appendChild(b); }
  d.appendChild(row); m.appendChild(d);
  m.onclick = e => { if (e.target === m) close(); };
  document.body.appendChild(m);
  const f = d.querySelector('input, textarea, select') || row.lastChild; f && f.focus();
  return { close, el: d };
}

// ---------- 外殼 ----------
let app = null, dirty = false, quietUntil = 0, curName = '', aiWait = null, attach = null;
export const shell = {
  get dirty() { return dirty; },
  setDirty(v = true) { if (v && Date.now() < quietUntil) return; dirty = v; },
  quiet(ms = 1500) { quietUntil = Date.now() + ms; },
  get name() { return curName; },
  setName(n) { curName = n; const i = $('docName'); i.value = n.replace(new RegExp('\\.' + app.ext + '$', 'i'), ''); i.size = Math.max(6, Math.min(60, [...i.value].reduce((w, ch) => w + (ch.charCodeAt(0) > 0x2e7f ? 2 : 1), 0) + 1)); document.title = n; },
  /** 撳檔名改名（同 Google 一樣）：已經儲存過嘅檔案會連磁碟上面個檔一齊改名；未儲存過就只係改之後儲存用嘅名。 */
  rename(v) {
    v = String(v || '').replace(/[\\/:*?"<>|]/g, '').trim().replace(new RegExp('\\.' + app.ext + '$', 'i'), '').trim();
    const old = curName, next = v + '.' + app.ext;
    if (!v || next === old) { shell.setName(old); return; }
    shell.setName(next);
    post({ t: 'rename', name: next, old });
  },
  okToLeave: () => !dirty || confirm(T('leave')),
  opened(name, token) { shell.setName(name); dirty = false; shell.quiet(); post({ t: 'opened', name, token: token || null }); },
  goHome() { if (!shell.okToLeave()) return; dirty = false; post({ t: 'closed' }); location.replace('index.html'); },
  pick() { post({ t: 'pick', kind: 'any' }); },
  async save(as) {
    toast(T('saving'), 60000);
    try {
      const bytes = await app.getBytes();
      window.__last = bytes;
      post({ t: 'save-begin', name: curName, as: !!as || !!app.lossy, size: bytes.length });   // lossy：檔案有我哋唔支援嘅內容，唔覆寫原檔
      const CH = 3 * 1024 * 1024;
      for (let i = 0; i < bytes.length; i += CH) { let s = ''; const part = bytes.subarray(i, Math.min(bytes.length, i + CH)); for (let k = 0; k < part.length; k += 0x8000) s += String.fromCharCode.apply(null, part.subarray(k, k + 0x8000)); post({ t: 'save-chunk', d: btoa(s) }); }
      post({ t: 'save-end' });
      if (!host) { dirty = false; toast('ok'); }
    } catch (e) { console.error(e); toast(T('savefail', e && e.message || e), 7000); }
  },
  /** 匯出另一種格式（CSV、PDF）：主程式彈儲存對話框，唔會當做而家份文件嘅儲存。 */
  exportFile(bytes, name) {
    post({ t: 'save-begin', name, export: true, size: bytes.length });
    const CH = 3 * 1024 * 1024;
    for (let i = 0; i < bytes.length; i += CH) { let s = ''; const part = bytes.subarray(i, Math.min(bytes.length, i + CH)); for (let k = 0; k < part.length; k += 0x8000) s += String.fromCharCode.apply(null, part.subarray(k, k + 0x8000)); post({ t: 'save-chunk', d: btoa(s) }); }
    post({ t: 'save-end' });
    if (!host) { window.__lastExport = { bytes, name }; toast('export ok'); }
  },
  print() { try { app.print(); } catch (e) { toast(String(e && e.message || e), 5000); } },
  newWindow() { post({ t: 'new-window' }); },
  toggleAi() { $('ai').classList.toggle('show'); if (app.onResize) setTimeout(app.onResize, 50); },
};

function aiAdd(cls, who, text) { const d = el('div', 'm ' + cls); if (who) d.appendChild(el('b', '', who)); d.appendChild(document.createTextNode(text)); $('aiLog').appendChild(d); $('aiLog').scrollTop = 1e9; return d; }
function aiSend(q) {
  q = (q || '').trim();
  if (!q || aiWait) return;
  aiAdd('me', T('aiyou'), q + (attach ? attach.label() : ''));
  aiWait = aiAdd('note', '', T('aithinking'));
  $('aiIn').value = '';
  const d = app.aiContext();
  post({ t: 'ai', q, kind: app.ext, text: d.text, info: d.info || '' });
}
function aiReply(text, err, note) {
  if (aiWait) { aiWait.remove(); aiWait = null; }
  if (note) aiAdd('note', '', note);
  if (err) { aiAdd('note', '', T('aifail') + err); return; }
  let acts = null;
  const m = /```json\s*([\s\S]*?)```/i.exec(text || '');
  if (m) { try { acts = JSON.parse(m[1]); } catch (e) { } text = (text.slice(0, m.index) + text.slice(m.index + m[0].length)).trim(); }
  if (text) aiAdd('ai', 'AI', text);
  if (Array.isArray(acts) && acts.length) Promise.resolve().then(() => app.aiApply(acts)).catch(e => { console.error(e); return 0; }).then(n => { if (n) dirty = true; aiAdd('note', '', n ? T('aidone', n) : T('ainone')); });
}

/**
 * app = { ext, getBytes(): Promise<Uint8Array>, print(), openBytes(buf, name, token), newDoc(), aiContext(): {text, info}, aiApply(actions): number, onResize? }
 * 頁面要有：#bar（第一行）、#stage、#ai、#toast、#busy。
 */
export function initShell(a) {
  app = a;
  const bar = $('bar');
  bar.innerHTML = '<div class="rbar"><button class="rb" id="bBack"></button><button class="rb pri" id="bOpen"></button></div>' +
    '<div class="tcol"><input id="docName" spellcheck="false"><div id="menubar"></div></div><span class="grow"></span>' +
    '<div class="rbar"><button class="rb" id="bWin" style="display:none">⧉</button><button class="rb" id="bAi" style="display:none">✦ AI</button>' +
    '<div class="split"><button class="rb ok l" id="bSave"></button><button class="rb ok r" id="bMore">▾</button></div></div>';
  $('bBack').textContent = T('back'); $('bOpen').textContent = T('open'); $('bSave').textContent = T('save'); $('bWin').title = T('newwin');
  { const i = $('docName'); i.title = T('rename_tip'); i.addEventListener('focus', () => i.select()); i.addEventListener('change', () => shell.rename(i.value));
    i.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Enter') i.blur(); else if (e.key === 'Escape') { i.value = curName.replace(new RegExp('\\.' + app.ext + '$', 'i'), ''); i.blur(); } }); }
  $('bBack').onclick = shell.goHome; $('bOpen').onclick = shell.pick; $('bSave').onclick = () => shell.save(false); $('bWin').onclick = shell.newWindow; $('bAi').onclick = shell.toggleAi;
  $('bMore').onclick = e => { e.stopPropagation(); closeMenus(); const r = $('bMore').getBoundingClientRect(); openMenu = { btn: $('bMore'), panels: [] }; openMenu.panels.push(panel([{ label: T('saveas'), run: () => shell.save(true) }, { label: T('print'), run: shell.print }], r.right - 170, r.bottom + 6, 0)); };
  if (host) $('bWin').style.display = '';
  const setAi = on => { $('bAi').style.display = on ? '' : 'none'; if (!on) $('ai').classList.remove('show'); };
  setAi(!!CFG.ai);
  $('ai').innerHTML = '<div class="head"><span id="aiTitle"></span><span class="grow"></span><button id="aiClose" class="rb">✕</button></div><div class="log" id="aiLog"></div>' +
    '<div class="quick"><button id="aq1" class="rb"></button></div><div class="in"><textarea id="aiIn"></textarea><button id="aiSend" class="rb pri"></button></div>';
  $('aiTitle').textContent = T('aititle'); $('aiSend').textContent = T('aisend'); $('aq1').textContent = T('aisum'); $('aiIn').placeholder = T('aiask');
  attach = setupAttach({ ai: $('ai'), post, T, note: s => aiAdd('note', '', s) });
  $('aiSend').onclick = () => aiSend($('aiIn').value); $('aq1').onclick = () => aiSend(T('aisum')); $('aiClose').onclick = shell.toggleAi;
  $('aiIn').addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); aiSend($('aiIn').value); } e.stopPropagation(); });

  addEventListener('keydown', e => {
    if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
    const k = e.key.toLowerCase();
    if (k === 'o') { e.preventDefault(); e.stopPropagation(); shell.pick(); }
    else if (k === 's') { e.preventDefault(); e.stopPropagation(); shell.save(e.shiftKey); }
    else if (k === 'p') { e.preventDefault(); e.stopPropagation(); shell.print(); }
  }, true);
  // 拖放：交個 File 俾主程式攞真實路徑（佢會決定用邊個編輯器開）；主程式冇反應而係同類檔案就自己讀
  let dropWait = null;
  addEventListener('dragover', e => { if (e.dataTransfer && [...e.dataTransfer.types].includes('Files')) e.preventDefault(); }, true);
  addEventListener('drop', e => {
    const f = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
    if (!f) return;
    e.preventDefault(); e.stopPropagation();
    const own = async () => { dropWait = null; if (new RegExp('\\.' + app.ext + '$', 'i').test(f.name) && shell.okToLeave()) app.openBytes(await f.arrayBuffer(), f.name, null); };
    try { if (host && host.postMessageWithAdditionalObjects) { dropWait = setTimeout(own, 1500); host.postMessageWithAdditionalObjects({ t: 'drop' }, [f]); return; } } catch (er) { }
    own();
  }, true);
  const onHost = async m => {
    if (m.t === 'file') {
      if (dropWait) { clearTimeout(dropWait); dropWait = null; }
      if (!shell.okToLeave()) return;
      busy(T('loading'));
      try { const r = await fetch(m.url); if (!r.ok) throw new Error(r.status); const buf = await r.arrayBuffer(); await app.openBytes(buf, m.name, m.token); busy(''); }
      catch (e) { console.error(e); busy(''); toast(T('openfail') + (e && e.message ? '：' + e.message : ''), 8000); }
    }
    else if (m.t === 'saved') { if (m.ok && m.export) toast(T('saved', m.name || ''), 3000); else if (m.ok) { dirty = false; if (m.name) shell.setName(m.name); toast(T('saved', m.name || ''), 3000); } else if (m.err) toast(T('savefail', m.err), 7000); else $('toast').style.display = 'none'; }
    else if (m.t === 'renamed') { shell.setName(m.name); if (!m.ok && m.err) toast(m.err, 6000); }
    else if (m.t === 'ai-reply') aiReply(m.text, m.err, m.note);
    else if (m.t === 'ai-files') attach.onFiles(m);
    else if (m.t === 'cfg') setAi(!!m.ai);
    else if (m.t === 'toast') toast(m.text, 5000);
    else if (m.t === 'switch') { if (dropWait) { clearTimeout(dropWait); dropWait = null; } if (shell.okToLeave()) { dirty = false; location.replace(m.url); } else post({ t: 'switch-cancel' }); }
  };
  if (host) host.addEventListener('message', e => onHost(e.data));
  window.__shell = { onHost, shell, aiReply };
  return { onHost };
}
