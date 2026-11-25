// 向量繪圖嘅面板：內容（位置、大細、顏色、筆畫）、對齊、形狀運算、物件清單。每個 = { el, sync() }。
import { el, clamp, dialog, field } from '../common.js';
import { VICON } from './vicons.js';
import { ICON } from '../photo/icons.js';

const hex = c => c ? c.toCSS(true) : null;
const r2 = v => Math.round(v * 100) / 100;
/** 一件嘢嘅「代表」路徑（群組就攞第一件有樣式嘅）。 */
function styled(it, scope) { if (it instanceof scope.Group) { for (const c of it.children) { const s = styled(c, scope); if (s) return s; } return null; } return it; }
function eachLeaf(it, scope, fn) { if (it instanceof scope.Group) it.children.forEach(c => eachLeaf(c, scope, fn)); else fn(it); }
const keep = i => { i.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Enter') i.blur(); }); return i; };
function num(v, step = 1) { const i = el('input', 'pnum'); i.type = 'number'; i.step = step; i.value = r2(v); return keep(i); }
function row(...kids) { const r = el('div', 'prow'); r.append(...kids); return r; }
function lab(text, input) { const l = el('label', 'plab'); l.append(el('span', '', text), input); return l; }

// ---------- 內容 ----------
export function propertiesPanel(api) {
  const root = el('div', 'pprops');
  const T = api.T;
  function sync() {
    if (root.contains(document.activeElement)) return;   // 打緊字：唔好重畫
    root.textContent = '';
    const doc = api.doc, scope = api.scope;
    if (!doc) return;
    const sel = api.sel;
    if (!sel.length) {
      root.appendChild(el('div', 'psec', T('vp_document')));
      const w = num(doc.w), h = num(doc.h);
      w.onchange = h.onchange = () => api.artboardSize(+w.value || doc.w, +h.value || doc.h);
      root.appendChild(row(lab('W', w), lab('H', h)));
      root.appendChild(el('div', 'phint', T('vp_nosel')));
      return;
    }
    // 位置同大細（畫板座標）
    let b = sel[0].bounds.clone(); sel.slice(1).forEach(i => b = b.unite(i.bounds));
    root.appendChild(el('div', 'psec', T('vp_transform')));
    const x = num(b.x), y = num(b.y), w = num(b.width), h = num(b.height), rot = num(0);
    const link = el('button', 'mini plink on'); link.type = 'button'; link.textContent = '🔗'; link.title = T('ph_keepratio');
    link.onclick = () => link.classList.toggle('on');
    x.onchange = y.onchange = () => { const d = new scope.Point((+x.value || 0) - b.x, (+y.value || 0) - b.y); sel.forEach(i => i.translate(d)); api.commit(T('vt_move')); };
    const size = which => {
      let sx = Math.max(0.01, +w.value || b.width) / (b.width || 1), sy = Math.max(0.01, +h.value || b.height) / (b.height || 1);
      if (link.classList.contains('on')) { if (which === 'w') sy = sx; else sx = sy; }
      sel.forEach(i => i.scale(sx, sy, b.topLeft)); api.commit(T('vc_scale'));
    };
    w.onchange = () => size('w'); h.onchange = () => size('h');
    rot.onchange = () => { const a = +rot.value || 0; if (a) { sel.forEach(i => i.rotate(a, b.center)); api.commit(T('vc_rotate')); } };
    root.appendChild(row(lab('X', x), lab('Y', y)));
    root.appendChild(row(lab('W', w), link, lab('H', h)));
    root.appendChild(row(lab('⟳', rot), el('span', 'phint', T('vp_rotateby'))));
    // 外觀
    const s = styled(sel[0], scope) || sel[0];
    root.appendChild(el('div', 'psec', T('vp_appearance')));
    const colorRow = (label, key) => {
      const cur = hex(s[key + 'Color']);
      const c = el('input'); c.type = 'color'; c.value = cur || '#000000'; c.disabled = !cur;
      const none = el('input'); none.type = 'checkbox'; none.checked = !cur;
      const nl = el('label', 'ocheck'); nl.append(none, el('span', '', T('vc_none')));
      c.oninput = () => { sel.forEach(i => eachLeaf(i, scope, l => l[key + 'Color'] = c.value)); api.setStyle(key, c.value); api.refresh(); };
      c.onchange = () => api.commit(T('vc_color'));
      none.onchange = () => { const v = none.checked ? null : (c.value || '#000000'); c.disabled = none.checked; sel.forEach(i => eachLeaf(i, scope, l => { l[key + 'Color'] = v; if (key === 'stroke' && v && !l.strokeWidth) l.strokeWidth = 1; })); api.setStyle(key, v); api.commit(T('vc_color')); };
      return row(el('span', 'pname', label), c, nl);
    };
    root.appendChild(colorRow(T('vc_fill'), 'fill'));
    root.appendChild(colorRow(T('vc_stroke'), 'stroke'));
    const sw = num(s.strokeWidth || 0, 0.5);
    sw.onchange = () => { const v = clamp(+sw.value || 0, 0, 1000); sel.forEach(i => eachLeaf(i, scope, l => l.strokeWidth = v)); api.setStyle('strokeWidth', v); api.commit(T('vo_strokew')); };
    const cap = el('select', 'psel'), join = el('select', 'psel');
    for (const v of ['butt', 'round', 'square']) { const o = el('option', '', T('vp_cap_' + v)); o.value = v; cap.appendChild(o); }
    for (const v of ['miter', 'round', 'bevel']) { const o = el('option', '', T('vp_join_' + v)); o.value = v; join.appendChild(o); }
    cap.value = s.strokeCap || 'butt'; join.value = s.strokeJoin || 'miter';
    cap.onchange = () => { sel.forEach(i => eachLeaf(i, scope, l => l.strokeCap = cap.value)); api.commit(T('vp_cap')); };
    join.onchange = () => { sel.forEach(i => eachLeaf(i, scope, l => l.strokeJoin = join.value)); api.commit(T('vp_join')); };
    root.appendChild(row(lab(T('vo_strokew'), sw), lab(T('vp_cap'), cap)));
    const dash = keep(el('input', 'pnum wide')); dash.placeholder = '4 2'; dash.value = (s.dashArray || []).join(' ');
    dash.onchange = () => { const a = dash.value.split(/[\s,]+/).map(Number).filter(v => v > 0); sel.forEach(i => eachLeaf(i, scope, l => l.dashArray = a)); api.commit(T('vp_dash')); };
    root.appendChild(row(lab(T('vp_join'), join), lab(T('vp_dash'), dash)));
    const op = el('input'); op.type = 'range'; op.min = 0; op.max = 100; op.value = Math.round((sel[0].opacity ?? 1) * 100);
    const opv = el('span', 'oval', op.value + '%');
    op.oninput = () => { sel.forEach(i => i.opacity = +op.value / 100); opv.textContent = op.value + '%'; api.refresh(); };
    op.onchange = () => api.commit(T('ph_opacity'));
    root.appendChild(row(el('span', 'pname', T('ph_opacity')), op, opv));
  }
  return { el: root, sync };
}

// ---------- 對齊 ----------
export function alignPanel(api) {
  const root = el('div', 'palign');
  const T = api.T;
  const btn = (icon, key, fn) => { const b = el('button', 'mini abtn'); b.type = 'button'; b.innerHTML = VICON[icon]; b.title = T(key); b.onclick = fn; return b; };
  const note = el('div', 'phint');
  function align(how) {
    const doc = api.doc, sel = api.sel, scope = api.scope;
    if (!doc || !sel.length) return;
    let t;
    if (sel.length > 1) { t = sel[0].bounds.clone(); sel.slice(1).forEach(i => t = t.unite(i.bounds)); }
    else t = new scope.Rectangle(0, 0, doc.w, doc.h);
    for (const i of sel) {
      const b = i.bounds;
      const dx = how === 'l' ? t.left - b.left : how === 'r' ? t.right - b.right : how === 'ch' ? t.center.x - b.center.x : 0;
      const dy = how === 't' ? t.top - b.top : how === 'b' ? t.bottom - b.bottom : how === 'cv' ? t.center.y - b.center.y : 0;
      i.translate(new scope.Point(dx, dy));
    }
    api.commit(T('vp_align'));
  }
  function distribute(axis) {
    const sel = [...api.sel], scope = api.scope;
    if (sel.length < 3) return;
    const c = i => axis === 'h' ? i.bounds.center.x : i.bounds.center.y;
    sel.sort((a, b) => c(a) - c(b));
    const first = c(sel[0]), step = (c(sel[sel.length - 1]) - first) / (sel.length - 1);
    sel.forEach((i, k) => { const d = first + step * k - c(i); i.translate(axis === 'h' ? new scope.Point(d, 0) : new scope.Point(0, d)); });
    api.commit(T('vp_distribute'));
  }
  root.append(
    el('div', 'psec', T('vp_alignobj')),
    row(btn('alignL', 'vp_al_l', () => align('l')), btn('alignCh', 'vp_al_ch', () => align('ch')), btn('alignR', 'vp_al_r', () => align('r')), btn('alignT', 'vp_al_t', () => align('t')), btn('alignCv', 'vp_al_cv', () => align('cv')), btn('alignB', 'vp_al_b', () => align('b'))),
    el('div', 'psec', T('vp_distribute')),
    row(btn('distH', 'vp_dist_h', () => distribute('h')), btn('distV', 'vp_dist_v', () => distribute('v'))),
    note);
  function sync() { const n = api.sel.length; note.textContent = n === 1 ? T('vp_toboard') : n > 1 ? T('vp_tosel') : T('vp_pick'); }
  return { el: root, sync };
}

// ---------- 形狀運算 ----------
export function shapeOpsPanel(api) {
  const root = el('div', 'palign');
  const T = api.T;
  const note = el('div', 'phint');
  function op(kind) {
    const doc = api.doc, scope = api.scope;
    const items = doc.art.children.filter(i => api.sel.includes(i) && (i instanceof scope.Path || i instanceof scope.CompoundPath));
    if (items.length < 2) { note.textContent = T('vp_ops_need'); return; }
    const top = items[items.length - 1], bottom = items[0];
    let res;
    if (kind === 'minus') { res = bottom; for (const o of items.slice(1)) res = res.subtract(o, { insert: false }); res.style = bottom.style; }
    else {
      res = items[0];
      for (const o of items.slice(1)) res = kind === 'unite' ? res.unite(o, { insert: false }) : kind === 'intersect' ? res.intersect(o, { insert: false }) : res.exclude(o, { insert: false });
      res.style = top.style;
    }
    res.insertAbove(top);
    items.forEach(i => i.remove());
    api.select([res]);
    api.commit(T('vp_op_' + kind));
  }
  const btn = (icon, kind) => { const b = el('button', 'mini abtn big'); b.type = 'button'; b.innerHTML = VICON[icon]; b.title = T('vp_op_' + kind); b.onclick = () => op(kind); return b; };
  root.append(el('div', 'psec', T('vp_ops_modes')), row(btn('unite', 'unite'), btn('minus', 'minus'), btn('intersect', 'intersect'), btn('exclude', 'exclude')), note);
  function sync() { note.textContent = T('vp_ops_tip'); }
  return { el: root, sync };
}

// ---------- 物件清單 ----------
export function objectsPanel(api) {
  const root = el('div', 'pobjs');
  const T = api.T;
  let dragFrom = null;
  const label = (it, scope) => it.name || (it instanceof scope.PointText ? '“' + it.content.slice(0, 24) + '”' : it instanceof scope.Group ? T('vo_group') : it instanceof scope.CompoundPath ? T('vo_compound') : it instanceof scope.Raster ? T('vo_image') : T('vo_path'));
  function sync() {
    root.textContent = '';
    const doc = api.doc, scope = api.scope; if (!doc) return;
    const kids = [...doc.art.children].reverse();
    if (!kids.length) { root.appendChild(el('div', 'phint pad', T('vp_empty'))); return; }
    for (const it of kids) {
      const r = el('div', 'lrow orow' + (api.sel.includes(it) ? ' on' : '')); r.draggable = true;
      const eye = el('button', 'mini'); eye.type = 'button'; eye.innerHTML = it.visible ? ICON.eye : ICON.eyeOff; eye.title = T('ph_visible');
      eye.onclick = e => { e.stopPropagation(); it.visible = !it.visible; api.commit(T('ph_visible')); };
      const lk = el('button', 'mini'); lk.type = 'button'; lk.innerHTML = it.locked ? VICON.lock : VICON.unlock; lk.title = T('vp_lock'); lk.classList.toggle('dim', !it.locked);
      lk.onclick = e => { e.stopPropagation(); it.locked = !it.locked; if (it.locked) api.select(api.sel.filter(x => x !== it)); api.commit(T('vp_lock')); };
      const sw = el('span', 'osw'); const s = styled(it, scope);
      if (s) { sw.style.background = hex(s.fillColor) || 'transparent'; sw.style.borderColor = hex(s.strokeColor) || 'var(--r-border)'; }
      r.append(eye, lk, sw, el('span', 'lname', label(it, scope)));
      r.onclick = e => { if (it.locked) return; api.select(e.shiftKey ? (api.sel.includes(it) ? api.sel.filter(x => x !== it) : [...api.sel, it]) : [it]); };
      r.ondblclick = () => {
        const i = el('input'); i.type = 'text'; i.value = it.name || '';
        dialog(T('vp_rename'), field(T('vp_name'), i), [[T('cancel')], [T('ok'), () => { it.name = i.value.trim() || null; api.commit(T('vp_rename')); }, true]]);
      };
      r.ondragstart = e => { dragFrom = it; e.dataTransfer.effectAllowed = 'move'; };
      r.ondragover = e => { if (dragFrom) { e.preventDefault(); r.classList.add('dropat'); } };
      r.ondragleave = () => r.classList.remove('dropat');
      r.ondrop = e => { e.preventDefault(); r.classList.remove('dropat'); const f = dragFrom; dragFrom = null; if (!f || f === it) return; f.insertAbove(it); api.commit(T('ph_reorder')); };
      r.ondragend = () => dragFrom = null;
      root.appendChild(r);
    }
  }
  return { el: root, sync };
}
