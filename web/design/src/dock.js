// 面板版面：左右兩條停駐欄 + 浮動視窗。每個面板可以拖去另一組做分頁、拖去停駐欄做新一組、拖去畫布上面變浮動視窗。
// 停駐欄可以摺埋做一列圖示（撳圖示彈出嗰個面板）；版面記喺 localStorage，下次開返一樣。
import { el } from './common.js';

/**
 * opts = {
 *   work: 放停駐欄同浮動視窗嘅容器（position:relative）, stage: 中間畫布（停駐欄夾住佢）,
 *   panels: { id: { title, icon, el, weight?, compact?, width? } },
 *   layout: 預設版面, key: localStorage key, T: 翻譯, onChange?: () => void
 * }
 * 版面：{ left: Dock, right: Dock, floats: [Group & {x,y,w,h}], hidden: [id] }
 *   Dock = { w, collapsed, groups: [Group] }，Group = { tabs: [id], active: id, min?: bool }
 */
export function createDock(o) {
  const { work, stage, panels, T } = o;
  const ids = Object.keys(panels);
  const clone = x => JSON.parse(JSON.stringify(x));
  let L = load();
  let hideAll = false;
  let pop = null;           // 摺埋時彈出嘅面板 { id, el }

  const holder = el('div'); holder.style.display = 'none'; work.appendChild(holder);   // 收埋嘅面板放喺度（元素一直存在）
  const dockEl = { left: el('div', 'dock left'), right: el('div', 'dock right') };
  work.insertBefore(dockEl.left, stage); work.appendChild(dockEl.right);
  const floatLayer = el('div', 'floats'); work.appendChild(floatLayer);
  const hint = el('div', 'dhint'); work.appendChild(hint);

  function load() {
    try { const s = JSON.parse(localStorage.getItem(o.key) || 'null'); if (s && s.left && s.right) return normalize(s); } catch { }
    return normalize(clone(o.layout));
  }
  function save() { try { localStorage.setItem(o.key, JSON.stringify(L)); } catch { } }
  /** 每個面板只可以喺一個位置；唔認得嘅拎走，漏咗嘅放返預設位置。 */
  function normalize(s) {
    const seen = new Set();
    const fix = g => { g.tabs = g.tabs.filter(t => ids.includes(t) && !seen.has(t) && seen.add(t)); if (!g.tabs.includes(g.active)) g.active = g.tabs[0]; return g.tabs.length > 0; };
    for (const side of ['left', 'right']) { s[side].groups = (s[side].groups || []).filter(fix); s[side].w = s[side].w || 260; }
    s.floats = (s.floats || []).filter(fix);
    s.hidden = (s.hidden || []).filter(t => ids.includes(t) && !seen.has(t) && seen.add(t));
    for (const id of ids) if (!seen.has(id)) home(s, id);
    return s;
  }
  /** 面板放返預設嗰邊（新一組）。 */
  function home(s, id) {
    const side = clone(o.layout).left.groups.some(g => g.tabs.includes(id)) ? 'left' : 'right';
    s[side].groups.push({ tabs: [id], active: id });
  }
  function allGroups() { return [...L.left.groups, ...L.right.groups, ...L.floats]; }
  function cleanup() {
    for (const side of ['left', 'right']) L[side].groups = L[side].groups.filter(g => g.tabs.length);
    L.floats = L.floats.filter(g => g.tabs.length);
    for (const g of allGroups()) if (!g.tabs.includes(g.active)) g.active = g.tabs[0];
  }
  function removeIds(list) { for (const g of allGroups()) g.tabs = g.tabs.filter(t => !list.includes(t)); L.hidden = L.hidden.filter(t => !list.includes(t)); }
  /** 停駐欄淨係得「精簡」面板（工具列）：用窄身。 */
  const compactOnly = d => d.groups.length > 0 && d.groups.every(g => g.tabs.every(t => panels[t].compact));

  // ---------- 畫 ----------
  function render() {
    closePop();
    for (const id of ids) holder.appendChild(panels[id].el);
    for (const side of ['left', 'right']) renderDock(side);
    floatLayer.textContent = '';
    L.floats.forEach((g, i) => floatLayer.appendChild(renderFloat(g, i)));
    clampFloats();
    work.classList.toggle('nopanels', hideAll);
    o.onChange && o.onChange();
  }
  /** 浮動視窗唔好跌出工作區（視窗縮細咗 / 浮動視窗太高）；工作區未顯示（大細係 0）就唔理。只係移位，唔改記住嘅位置。 */
  function clampFloats() {
    const ww = work.clientWidth, wh = work.clientHeight;
    if (!ww || !wh) return;
    [...floatLayer.children].forEach((f, i) => {
      const g = L.floats[i]; if (!g) return;
      f.style.left = Math.max(0, Math.min(g.x, ww - f.offsetWidth)) + 'px';
      f.style.top = Math.max(0, Math.min(g.y, wh - f.offsetHeight)) + 'px';
    });
  }
  new ResizeObserver(clampFloats).observe(work);
  function renderDock(side) {
    const d = L[side], host = dockEl[side];
    host.textContent = '';
    host.style.display = d.groups.length && !hideAll ? '' : 'none';
    const narrow = compactOnly(d);
    host.classList.toggle('narrow', narrow);
    host.classList.toggle('icons', !!d.collapsed && !narrow);
    if (d.collapsed && !narrow) { renderIcons(side); host.style.width = ''; return; }
    host.style.width = narrow ? '' : d.w + 'px';
    if (!narrow) {
      const bar = el('div', 'dbar');
      const c = el('button', 'mini', side === 'right' ? '»' : '«'); c.type = 'button'; c.title = T('ph_dockcollapse');
      c.onclick = () => { d.collapsed = true; save(); render(); };
      bar.appendChild(c); host.appendChild(bar);
    }
    d.groups.forEach((g, gi) => host.appendChild(renderGroup(g, side, gi)));
    if (!narrow) {
      const rz = el('div', 'dresize'); host.appendChild(rz);
      rz.onpointerdown = e => {
        e.preventDefault(); rz.setPointerCapture(e.pointerId);
        const x0 = e.clientX, w0 = d.w;
        rz.onpointermove = ev => { d.w = Math.max(180, Math.min(520, w0 + (side === 'right' ? x0 - ev.clientX : ev.clientX - x0))); host.style.width = d.w + 'px'; };
        rz.onpointerup = () => { rz.onpointermove = rz.onpointerup = null; save(); o.onChange && o.onChange(); };
      };
    }
  }
  /** 摺埋：一列圖示，撳一下喺隔籬彈出嗰個面板。 */
  function renderIcons(side) {
    const d = L[side], host = dockEl[side];
    const bar = el('div', 'dbar');
    const c = el('button', 'mini', side === 'right' ? '«' : '»'); c.type = 'button'; c.title = T('ph_dockexpand');
    c.onclick = () => { d.collapsed = false; save(); render(); };
    bar.appendChild(c); host.appendChild(bar);
    d.groups.forEach((g, gi) => {
      const box = el('div', 'igroup');
      for (const id of g.tabs) {
        const b = el('button', 'iconbtn'); b.type = 'button'; b.innerHTML = panels[id].icon || ''; b.title = panels[id].title;
        const lab = el('span', '', panels[id].title); b.appendChild(lab);
        b.onclick = () => { if (pop && pop.id === id) closePop(); else openPop(id, b, side); };
        b.onpointerdown = e => startDrag(e, [id], b);
        box.appendChild(b);
      }
      host.appendChild(box);
    });
  }
  function openPop(id, btn, side) {
    closePop();
    const p = el('div', 'dpop'), r = btn.getBoundingClientRect(), wr = work.getBoundingClientRect();
    const head = el('div', 'ghead'); head.appendChild(el('span', 'ptab on', panels[id].title));
    head.appendChild(el('span', 'grow'));
    const x = el('button', 'mini', '×'); x.type = 'button'; x.onclick = closePop; head.appendChild(x);
    const body = el('div', 'gbody'); body.appendChild(panels[id].el);
    p.append(head, body);
    p.style.width = (panels[id].width || 260) + 'px';
    work.appendChild(p);
    const left = side === 'right' ? r.left - wr.left - p.offsetWidth - 4 : r.right - wr.left + 4;
    p.style.left = Math.max(0, left) + 'px';
    p.style.top = Math.max(0, Math.min(r.top - wr.top, wr.height - p.offsetHeight - 4)) + 'px';
    pop = { id, el: p };
    o.onChange && o.onChange();
  }
  function closePop() { if (!pop) return; holder.appendChild(panels[pop.id].el); pop.el.remove(); pop = null; }
  addEventListener('pointerdown', e => { if (pop && !e.target.closest('.dpop') && !e.target.closest('.iconbtn')) closePop(); }, true);

  function renderGroup(g, where, gi) {
    const box = el('div', 'pgroup' + (g.min ? ' min' : ''));
    box.dataset.where = where; box.dataset.gi = gi;
    const compact = g.tabs.length === 1 && panels[g.tabs[0]].compact;
    const head = el('div', 'ghead' + (compact ? ' grip' : ''));
    if (compact) head.title = T('ph_dragtip');
    else for (const id of g.tabs) {
      const t = el('button', 'ptab' + (id === g.active ? ' on' : ''), panels[id].title); t.type = 'button';
      t.onclick = () => { if (g.active !== id || g.min) { g.active = id; g.min = false; save(); render(); } };
      t.onpointerdown = e => startDrag(e, [id], t);
      head.appendChild(t);
    }
    head.appendChild(el('span', 'grow'));
    if (!compact) {
      const m = el('button', 'mini', g.min ? '▸' : '▾'); m.type = 'button'; m.title = T('ph_collapse');
      m.onclick = () => { g.min = !g.min; save(); render(); };
      const x = el('button', 'mini', '×'); x.type = 'button'; x.title = T('ph_closepanel');
      x.onclick = () => { removeIds([g.active]); L.hidden.push(g.active); cleanup(); save(); render(); };
      head.append(m, x);
    }
    // 撳住標題列空白位：成組一齊搬
    head.onpointerdown = e => { if (e.target === head || e.target.classList.contains('grow')) startDrag(e, [...g.tabs], head, where === 'float' ? g : null); };
    head.ondblclick = e => { if (!compact && (e.target === head || e.target.classList.contains('grow'))) { g.min = !g.min; save(); render(); } };
    box.appendChild(head);
    if (!g.min) {
      const body = el('div', 'gbody');
      body.appendChild(panels[g.active].el);
      box.appendChild(body);
      const w = panels[g.active].weight || 0;
      box.style.flex = w ? `${w} 1 0` : 'none';
    } else box.style.flex = 'none';
    return box;
  }
  function renderFloat(g, i) {
    const f = renderGroup(g, 'float', i);
    f.classList.add('float');
    const compact = g.tabs.length === 1 && panels[g.tabs[0]].compact;
    Object.assign(f.style, { left: g.x + 'px', top: g.y + 'px', width: g.w + 'px', height: g.min ? '' : g.h + 'px', flex: '' });
    if (compact) f.style.height = '';
    new ResizeObserver(() => {
      if (!f.isConnected || g.min || compact) return;
      const w = f.offsetWidth, h = f.offsetHeight;
      if (Math.abs(w - g.w) > 2 || Math.abs(h - g.h) > 2) { g.w = w; g.h = h; save(); o.onChange && o.onChange(); }
    }).observe(f);
    return f;
  }

  // ---------- 拖曳 ----------
  function startDrag(e, list, src, floatGroup) {
    if (e.button !== 0) return;
    const x0 = e.clientX, y0 = e.clientY;
    let ghost = null, target = null;
    const fr = floatGroup ? { dx: x0 - work.getBoundingClientRect().left - floatGroup.x, dy: y0 - work.getBoundingClientRect().top - floatGroup.y } : { dx: 20, dy: 12 };
    const move = ev => {
      if (!ghost) {
        if (Math.hypot(ev.clientX - x0, ev.clientY - y0) < 6) return;
        closePop();
        ghost = el('div', 'dghost', list.map(id => panels[id].title).join(' · '));
        document.body.appendChild(ghost);
        document.body.classList.add('docking');
      }
      ghost.style.left = ev.clientX + 12 + 'px'; ghost.style.top = ev.clientY + 12 + 'px';
      target = hit(ev.clientX, ev.clientY, list, fr);
      showHint(target);
    };
    const up = () => {
      removeEventListener('pointermove', move, true); removeEventListener('pointerup', up, true);
      if (!ghost) return;
      ghost.remove(); document.body.classList.remove('docking'); hint.style.display = 'none';
      if (target) apply(list, target);
    };
    addEventListener('pointermove', move, true); addEventListener('pointerup', up, true);
  }
  /** 而家放手會放去邊。 */
  function hit(x, y, list, fr) {
    const wr = work.getBoundingClientRect();
    if (x < wr.left || x > wr.right || y < wr.top || y > wr.bottom) return null;
    const edge = 28;
    if (x < wr.left + edge) return { dock: 'left', index: L.left.groups.length, rect: edgeRect('left') };
    if (x > wr.right - edge) return { dock: 'right', index: L.right.groups.length, rect: edgeRect('right') };
    const t = document.elementFromPoint(x, y);
    const head = t && t.closest('.ghead'), grp = t && t.closest('.pgroup');
    if (grp && work.contains(grp)) {
      const where = grp.dataset.where, g = where === 'float' ? L.floats[+grp.dataset.gi] : L[where].groups[+grp.dataset.gi];
      if (head) return { into: g, rect: rel(head.getBoundingClientRect()) };
      if (where !== 'float') {
        const r = grp.getBoundingClientRect(), after = y > r.top + r.height / 2;
        return { dock: where, index: +grp.dataset.gi + (after ? 1 : 0), rect: rel({ left: r.left, width: r.width, top: after ? r.bottom - 3 : r.top - 1, height: 4 }) };
      }
      return { into: g, rect: rel(grp.getBoundingClientRect()) };
    }
    const d = t && t.closest('.dock');
    if (d) { const side = d.classList.contains('left') ? 'left' : 'right'; return { dock: side, index: L[side].groups.length, rect: rel(d.getBoundingClientRect()) }; }
    // 畫布上面：變浮動視窗
    const compact = list.length === 1 && panels[list[0]].compact;
    const w = compact ? 64 : Math.max(220, ...list.map(id => panels[id].width || 260)), h = compact ? 0 : 320;
    const fx = Math.max(0, Math.min(x - wr.left - fr.dx, wr.width - w)), fy = Math.max(0, Math.min(y - wr.top - fr.dy, wr.height - 60));
    return { float: { x: fx, y: fy, w, h: h || 300 }, rect: { left: fx, top: fy, width: w, height: h || 360 } };
  }
  function rel(r) { const wr = work.getBoundingClientRect(); return { left: r.left - wr.left, top: r.top - wr.top, width: r.width, height: r.height }; }
  function edgeRect(side) { const wr = work.getBoundingClientRect(); return { left: side === 'left' ? 0 : wr.width - 8, top: 0, width: 8, height: wr.height }; }
  function showHint(t) {
    if (!t) { hint.style.display = 'none'; return; }
    Object.assign(hint.style, { display: 'block', left: t.rect.left + 'px', top: t.rect.top + 'px', width: t.rect.width + 'px', height: t.rect.height + 'px' });
    hint.classList.toggle('line', t.rect.height <= 4);
  }
  function apply(list, t) {
    if (t.into && t.into.tabs.length === list.length && list.every(id => t.into.tabs.includes(id))) return;   // 放返原位
    const placeholder = { tabs: [], active: null };
    if (t.dock) { const d = L[t.dock]; d.groups.splice(t.index, 0, placeholder); d.collapsed = false; }
    removeIds(list);
    if (t.into) { t.into.tabs.push(...list); t.into.active = list[0]; t.into.min = false; }
    else if (t.dock) { placeholder.tabs = [...list]; placeholder.active = list[0]; }
    else if (t.float) L.floats.push({ tabs: [...list], active: list[0], ...t.float });
    cleanup(); save(); render();
  }

  // ---------- 對外 ----------
  return {
    render,
    isShown: id => !L.hidden.includes(id),
    toggle(id) {
      if (L.hidden.includes(id)) { L.hidden = L.hidden.filter(x => x !== id); home(L, id); const side = L.left.groups.some(g => g.tabs.includes(id)) ? 'left' : 'right'; L[side].collapsed = false; }
      else { removeIds([id]); L.hidden.push(id); cleanup(); }
      save(); render();
    },
    reset() { L = normalize(clone(o.layout)); hideAll = false; save(); render(); },
    toggleAll() { hideAll = !hideAll; render(); },
  };
}
