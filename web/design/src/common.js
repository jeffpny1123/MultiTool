// 設計工具共用：介面文字、同主程式（src/Design.cs）溝通、選單、對話框、提示。
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
export const clamp = (v, a, b) => Math.max(a, Math.min(b, v));

// ---------- 選單（撳開一個之後，滑鼠移去隔籬自動轉；支援子選單）----------
let openMenu = null;   // { btn, panels: [] }
export function closeMenus() { if (!openMenu) return; openMenu.panels.forEach(p => p.remove()); openMenu.btn && openMenu.btn.classList.remove('on'); openMenu = null; }
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
    b.onclick = () => { if (openMenu && openMenu.btn === b) { if (Date.now() - openMenu.at > 400) closeMenus(); } else open(); };
    b.onmouseenter = () => { if (openMenu && openMenu.btn !== b) open(); };
    bar.appendChild(b);
  }
}
/** 右鍵 / 細掣彈出嘅選單。 */
export function popupMenu(items, x, y) { closeMenus(); openMenu = { btn: null, panels: [] }; openMenu.panels.push(panel(items, x, y, 0)); }

// ---------- 簡單對話框 ----------
/** buttons = [[label, run (回傳 false = 唔閂), primary]] */
export function dialog(title, body, buttons) {
  const m = el('div', 'rmask'), d = el('div', 'rdlg');
  const h3 = el('h3', '', title); d.appendChild(h3);
  // 拖標題移動對話框（睇到後面張圖）
  h3.onpointerdown = e => {
    if (e.button !== 0) return;
    const r = d.getBoundingClientRect(), sx = e.clientX - r.left, sy = e.clientY - r.top;
    Object.assign(d.style, { position: 'fixed', margin: '0', left: r.left + 'px', top: r.top + 'px' });
    h3.setPointerCapture(e.pointerId);
    h3.onpointermove = ev => { d.style.left = Math.max(0, Math.min(innerWidth - 60, ev.clientX - sx)) + 'px'; d.style.top = Math.max(0, Math.min(innerHeight - 40, ev.clientY - sy)) + 'px'; };
    h3.onpointerup = () => { h3.onpointermove = null; };
  };
  if (typeof body === 'string') d.appendChild(el('div', 'rbody', body)); else if (body) d.appendChild(body);
  const row = el('div', 'row');
  const close = () => m.remove();
  for (const [label, run, pri] of buttons || [[T('close'), null, true]]) { const b = el('button', 'rb' + (pri ? ' pri' : ''), label); b.onclick = () => { if (!run || run() !== false) close(); }; row.appendChild(b); }
  d.appendChild(row); m.appendChild(d);
  m.onclick = e => { if (e.target === m) close(); };
  d.addEventListener('keydown', e => {
    e.stopPropagation();
    if (e.key === 'Escape') close();
    else if (e.key === 'Enter' && e.target.tagName !== 'TEXTAREA') { const p = row.querySelector('.pri'); p && p.click(); }
  });
  document.body.appendChild(m);
  const f = d.querySelector('input, textarea, select') || row.lastChild; f && f.focus();
  return { close, el: d };
}
/** 對話框入面一行「標籤 + 控件」。 */
export function field(label, input) { const w = el('div', 'fld'); w.appendChild(el('label', '', label)); w.appendChild(input); return w; }
export function numInput(v, min, max, step = 1) { const i = el('input'); i.type = 'number'; i.value = v; i.min = min; i.max = max; i.step = step; return i; }

/** 將 bytes 分段交俾主程式寫檔（WebView2 訊息唔好太大）。 */
export function sendBytes(bytes, head) {
  post({ t: 'save-begin', ...head, size: bytes.length });
  const CH = 3 * 1024 * 1024;
  for (let i = 0; i < bytes.length; i += CH) {
    let s = ''; const part = bytes.subarray(i, Math.min(bytes.length, i + CH));
    for (let k = 0; k < part.length; k += 0x8000) s += String.fromCharCode.apply(null, part.subarray(k, k + 0x8000));
    post({ t: 'save-chunk', d: btoa(s) });
  }
  post({ t: 'save-end' });
}
