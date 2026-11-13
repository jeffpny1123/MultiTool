// 試算表圖表：浮喺格仔上面嘅圖（Chart.js，MIT），資料直接讀儲存格，改數字圖會跟住變。
// 儲存時由 xlsx-charts.js 寫做真正嘅 Excel 圖表；開返檔案時讀返出嚟繼續改。
import { Chart, registerables } from 'chart.js';
import { T, $, el, dialog, toast, shell } from './shell.js';
import { colName, colNum } from './xlsx.js';
import { refStr } from './xlsx-charts.js';
Chart.register(...registerables);

export const TYPES = ['column', 'bar', 'line', 'area', 'pie'];
const COLORS = ['#4E79A7', '#F28E2B', '#59A14F', '#E15759', '#76B7B2', '#EDC948', '#B07AA1', '#FF9DA7', '#9C755F', '#BAB0AC'];
const HDR_W = 46, HDR_H = 20;   // Univer 列號欄闊、欄名列高（未計縮放）
let api = null, layer = null, charts = [], selId = null, seq = 1, timer = 0;
const wb = () => api.getActiveWorkbook();
const sheetById = id => wb() && wb().getSheetBySheetId(id);
const parseA1 = a1 => { const m = /^([A-Z]+)(\d+)(?::([A-Z]+)(\d+))?$/.exec(String(a1 || '').toUpperCase().replace(/\$/g, '').trim()); if (!m) return null; const c1 = colNum(m[1]), r1 = +m[2] - 1, c2 = m[3] ? colNum(m[3]) : c1, r2 = m[4] ? +m[4] - 1 : r1; return { r1: Math.min(r1, r2), c1: Math.min(c1, c2), r2: Math.max(r1, r2), c2: Math.max(c1, c2) }; };
const a1Of = (r1, c1, r2, c2) => colName(c1) + (r1 + 1) + (r2 === r1 && c2 === c1 ? '' : ':' + colName(c2) + (r2 + 1));
function values(ref) {
  const s = ref && sheetById(ref.sheetId), p = ref && parseA1(ref.a1); if (!s || !p) return [];
  try { return s.getRange(p.r1, p.c1, p.r2 - p.r1 + 1, p.c2 - p.c1 + 1).getRawValues().flat(); } catch (e) { return []; }
}
const num = v => (v === null || v === undefined || v === '' || isNaN(+v)) ? null : +v;
const serName = (s, i) => s.name ? (s.name.text != null ? s.name.text : String(values(s.name)[0] ?? '')) || T('ch_series', i + 1) : T('ch_series', i + 1);

// ---------- 格 ⇄ 像素（未計縮放、由 A1 左上角起計）----------
function walk(sheet, px, size, max) { let acc = 0; for (let i = 0; i < max; i++) { const w = size(i); if (px < acc + w) return { i, off: px - acc }; acc += w; } return { i: max - 1, off: 0 }; }
function startOf(size, n) { let acc = 0; for (let i = 0; i < n; i++) acc += size(i); return acc; }
const colW = s => i => { try { return s.getColumnWidth(i); } catch (e) { return 88; } };
const rowH = s => i => { try { return s.getRowHeight(i); } catch (e) { return 24; } };
export function anchorOf(ch) { const s = sheetById(ch.sheetId); const c = walk(s, Math.max(0, ch.x), colW(s), 16384), r = walk(s, Math.max(0, ch.y), rowH(s), 1048576); return { col: c.i, dx: c.off, row: r.i, dy: r.off, w: ch.w, h: ch.h }; }

// ---------- 由範圍砌系列：第一列有文字 = 系列名，第一欄有文字 = 分類 ----------
function seriesFromRange(sheet, a1) {
  const p = parseA1(a1); if (!p) return null;
  const sid = sheet.getSheetId(), grid = sheet.getRange(p.r1, p.c1, p.r2 - p.r1 + 1, p.c2 - p.c1 + 1).getRawValues();
  const rows = grid.length, cols = grid[0] ? grid[0].length : 0; if (!rows || !cols) return null;
  const text = v => v !== null && v !== undefined && v !== '' && isNaN(+v);
  const hasCat = cols > 1 && grid.slice(1).some(r => text(r[0])) || (cols > 1 && grid.every(r => num(r[0]) === null));
  const hasHead = rows > 1 && grid[0].slice(hasCat ? 1 : 0).some(text);
  const r0 = p.r1 + (hasHead ? 1 : 0), c0 = p.c1 + (hasCat ? 1 : 0);
  if (r0 > p.r2 || c0 > p.c2) return null;
  const out = [];
  for (let c = c0; c <= p.c2; c++) out.push({ name: hasHead ? { sheetId: sid, a1: a1Of(p.r1, c, p.r1, c) } : null, cat: hasCat ? { sheetId: sid, a1: a1Of(r0, p.c1, p.r2, p.c1) } : null, val: { sheetId: sid, a1: a1Of(r0, c, p.r2, c) } });
  return out;
}

// ---------- 畫 ----------
function config(ch) {
  const labels = ch.series[0] && ch.series[0].cat ? values(ch.series[0].cat).map(v => String(v ?? '')) : (ch.series[0] ? values(ch.series[0].val).map((_, i) => String(i + 1)) : []);
  const pie = ch.type === 'pie', area = ch.type === 'area';
  const datasets = (pie ? ch.series.slice(0, 1) : ch.series).map((s, i) => ({ label: serName(s, i), data: values(s.val).map(num),
    backgroundColor: pie ? labels.map((_, k) => COLORS[k % COLORS.length]) : (area ? COLORS[i % COLORS.length] + '66' : COLORS[i % COLORS.length]),
    borderColor: pie ? '#fff' : COLORS[i % COLORS.length], borderWidth: ch.type === 'line' || area ? 2 : (pie ? 1 : 0), fill: area, tension: 0, pointRadius: ch.type === 'line' ? 2 : 0 }));
  return { type: pie ? 'pie' : (ch.type === 'line' || area ? 'line' : 'bar'), data: { labels, datasets },
    options: { responsive: true, maintainAspectRatio: false, animation: false, indexAxis: ch.type === 'bar' ? 'y' : 'x',
      plugins: { title: { display: !!ch.title, text: ch.title, color: '#222', font: { size: 14, weight: '600' } }, legend: { display: pie || datasets.length > 1 || !!(ch.series[0] && ch.series[0].name), position: 'right', labels: { color: '#333', boxWidth: 12 } } },
      scales: pie ? {} : { x: { ticks: { color: '#444' }, grid: { color: '#e6e6e6' } }, y: { ticks: { color: '#444' }, grid: { color: '#e6e6e6' }, beginAtZero: true } } } };
}
function place() {
  if (!layer || !wb()) return;
  const s = wb().getActiveSheet(), sid = s.getSheetId(), cv = [...$('univer').querySelectorAll('canvas')].sort((a, b) => b.width * b.height - a.width * a.height)[0];
  if (!cv) return;
  const st = $('stage').getBoundingClientRect(), cr = cv.getBoundingClientRect();
  let zoom = 1; try { zoom = s.getZoom(); } catch (e) { }
  Object.assign(layer.style, { left: cr.left - st.left + HDR_W * zoom + 'px', top: cr.top - st.top + HDR_H * zoom + 'px', width: Math.max(0, cr.width - HDR_W * zoom) + 'px', height: Math.max(0, cr.height - HDR_H * zoom) + 'px' });
  const lr = layer.getBoundingClientRect();
  for (const ch of charts) {
    const on = ch.sheetId === sid; ch.el.style.display = on ? '' : 'none'; if (!on) continue;
    const a = anchorOf(ch);
    let rect; try { rect = s.getRange(a.row, a.col).getCellRect(); } catch (e) { rect = null; }
    if (!rect) { ch.el.style.display = 'none'; continue; }
    // getCellRect 係相對主畫布左上角
    Object.assign(ch.el.style, { left: cr.left + rect.left - lr.left + a.dx * zoom + 'px', top: cr.top + rect.top - lr.top + a.dy * zoom + 'px', width: ch.w * zoom + 'px', height: ch.h * zoom + 'px' });
    ch.zoom = zoom;
  }
}
function draw(ch) { try { if (ch.chart) ch.chart.destroy(); ch.chart = new Chart(ch.el.querySelector('canvas'), config(ch)); } catch (e) { console.error(e); } }
export function refresh(data = true) { if (!layer) return; place(); if (data) for (const ch of charts) if (ch.el.style.display !== 'none' && ch.chart) { const c = config(ch); ch.chart.data = c.data; ch.chart.update('none'); } else if (ch.el.style.display !== 'none') draw(ch); }
const later = (data = true) => { clearTimeout(timer); timer = setTimeout(() => refresh(data), 60); };
function select(id) { selId = id; for (const ch of charts) ch.el.classList.toggle('sel', ch.id === id); }

function mount(ch) {
  const d = el('div', 'rchart'); d.innerHTML = '<div class="rc-tools"><button class="rb" data-a="edit">✎</button><button class="rb" data-a="del">🗑</button></div><div class="rc-body"><canvas></canvas></div><div class="rc-hd"></div>';
  d.querySelector('[data-a=edit]').title = T('ch_edit'); d.querySelector('[data-a=del]').title = T('ch_delete');
  d.querySelector('[data-a=edit]').onclick = e => { e.stopPropagation(); editDialog(ch); };
  d.querySelector('[data-a=del]').onclick = e => { e.stopPropagation(); remove(ch.id); };
  d.addEventListener('pointerdown', e => {
    if (e.button !== 0 || e.target.closest('.rc-tools')) return;
    e.preventDefault(); e.stopPropagation(); select(ch.id);
    const rs = e.target.classList.contains('rc-hd'), sx = e.clientX, sy = e.clientY, o = { x: ch.x, y: ch.y, w: ch.w, h: ch.h }, z = ch.zoom || 1; let moved = false;
    const mv = ev => { const dx = (ev.clientX - sx) / z, dy = (ev.clientY - sy) / z; if (!moved && Math.abs(dx) + Math.abs(dy) < 3) return; moved = true;
      if (rs) { ch.w = Math.max(160, o.w + dx); ch.h = Math.max(120, o.h + dy); } else { ch.x = Math.max(0, o.x + dx); ch.y = Math.max(0, o.y + dy); } place(); };
    const up = () => { removeEventListener('pointermove', mv); removeEventListener('pointerup', up); if (moved) { shell.setDirty(); if (rs && ch.chart) ch.chart.resize(); } };
    addEventListener('pointermove', mv); addEventListener('pointerup', up);
  });
  d.addEventListener('dblclick', () => editDialog(ch));
  ch.el = d; layer.appendChild(d);
}
function push(ch) { ch.id = 'c' + (seq++); mount(ch); charts.push(ch); place(); draw(ch); return ch; }
export function remove(id) { const i = charts.findIndex(c => c.id === id); if (i < 0) return; const ch = charts[i]; if (ch.chart) ch.chart.destroy(); ch.el.remove(); charts.splice(i, 1); selId = null; shell.setDirty(); }
export function removeAt(i) { if (charts[i]) remove(charts[i].id); }
export function clear() { for (const ch of charts) { if (ch.chart) ch.chart.destroy(); ch.el.remove(); } charts = []; selId = null; }
export const count = () => charts.length;
export const list = () => charts.map((c, i) => ({ n: i + 1, type: c.type, title: c.title, sheet: sheetById(c.sheetId) ? sheetById(c.sheetId).getSheetName() : '', range: c.range || c.series.map(s => s.val.a1).join(',') }));

/** 用一個範圍加圖表（放喺資料右邊）。回傳圖表，或者 null（範圍唔啱 / 冇數字）。 */
export function add(type, a1, title, sheet) {
  sheet = sheet || wb().getActiveSheet();
  const series = seriesFromRange(sheet, a1), p = parseA1(a1);
  if (!series || !series.length || !series.some(s => values(s.val).some(v => num(v) !== null))) return null;
  let last = p.c2; try { last = Math.max(last, sheet.getDataRange().getLastColumn()); } catch (e) { }   // 放喺所有資料嘅右邊，唔好遮住數字
  const x = startOf(colW(sheet), last + 1) + 24, n = charts.filter(c => c.sheetId === sheet.getSheetId()).length;
  const ch = push({ sheetId: sheet.getSheetId(), type: TYPES.includes(type) ? type : 'column', title: title || '', series, range: a1.toUpperCase(), x, y: startOf(rowH(sheet), p.r1) + n * 316, w: 480, h: 300 });   // 同一張工作表嘅圖表逐個向下排，唔好疊埋
  shell.setDirty(); select(ch.id);
  return ch;
}
/** 開檔：xlsx-charts.readCharts 讀到嘅圖表（工作表用名、位置用格 + EMU）。 */
export function load(parsed) {
  for (const c of parsed) {
    const s = wb().getSheetByName(c.sheet); if (!s) continue;
    const ref = r => { if (!r) return null; const t = wb().getSheetByName(r.sheet); return t ? { sheetId: t.getSheetId(), a1: r.a1 } : null; };
    const series = c.series.map(x => ({ name: x.name ? (x.name.text != null ? { text: x.name.text } : ref(x.name.ref)) : null, cat: ref(x.cat), val: ref(x.val) })).filter(x => x.val);
    if (!series.length) continue;
    const f = c.anchor.from, x = startOf(colW(s), f.col) + f.dx, y = startOf(rowH(s), f.row) + f.dy;
    let w = 480, h = 300;
    if (c.anchor.ext) { w = c.anchor.ext.w; h = c.anchor.ext.h; }
    else if (c.anchor.to) { w = startOf(colW(s), c.anchor.to.col) + c.anchor.to.dx - x; h = startOf(rowH(s), c.anchor.to.row) + c.anchor.to.dy - y; }
    push({ sheetId: s.getSheetId(), type: c.type, title: c.title || '', series, range: '', x, y, w: Math.max(160, w), h: Math.max(120, h) });
  }
  select(null);
}
/** 儲存用：{ 工作表名: [{ anchor, chart }] }，連埋而家嘅數值做快取（其他程式未重新計之前都顯示到）。 */
export function forSave() {
  const out = {};
  for (const ch of charts) {
    const s = sheetById(ch.sheetId); if (!s) continue;
    const rf = r => { const t = sheetById(r.sheetId); return t ? refStr(t.getSheetName(), r.a1) : null; };
    const series = ch.series.map((x, i) => ({ name: x.name ? (x.name.text != null ? { text: x.name.text } : { ref: rf(x.name), text: serName(x, i) }) : { text: serName(x, i) },
      cat: x.cat && rf(x.cat) ? { ref: rf(x.cat), values: values(x.cat).map(v => String(v ?? '')) } : null, val: { ref: rf(x.val), values: values(x.val).map(num) } })).filter(x => x.val.ref);
    if (!series.length) continue;
    (out[s.getSheetName()] = out[s.getSheetName()] || []).push({ anchor: anchorOf(ch), chart: { type: ch.type, title: ch.title, series } });
  }
  return out;
}

export function editDialog(ch, preset) {
  const s = wb().getActiveSheet(), cur = s.getSelection() && s.getSelection().getActiveRange();
  const box = el('div');
  box.innerHTML = '<label></label><select id="chType"></select><label></label><input type="text" id="chTitle"><label></label><input type="text" id="chRange" spellcheck="false"><div class="rhint"></div>';
  const [l1, l2, l3] = box.querySelectorAll('label'); l1.textContent = T('ch_type'); l2.textContent = T('ch_title'); l3.textContent = T('ch_range');
  box.querySelector('.rhint').textContent = ch && !ch.range ? T('ch_range_keep') : T('ch_range_tip');
  const sel = box.querySelector('#chType'); for (const t of TYPES) { const o = el('option', '', T('ch_t_' + t)); o.value = t; sel.appendChild(o); }
  sel.value = ch ? ch.type : (preset || 'column'); box.querySelector('#chTitle').value = ch ? ch.title : '';
  box.querySelector('#chRange').value = ch ? ch.range : (cur ? a1Of(cur.getRow(), cur.getColumn(), cur.getLastRow(), cur.getLastColumn()) : '');
  for (const i of box.querySelectorAll('input')) i.addEventListener('keydown', e => e.stopPropagation());
  dialog(ch ? T('ch_edit') : T('ch_insert'), box, [[T('cancel')], [T('ok'), () => {
    const type = sel.value, title = box.querySelector('#chTitle').value.trim(), range = box.querySelector('#chRange').value.trim().toUpperCase();
    if (!ch) { if (!add(type, range, title)) { toast(T('ch_bad_range'), 5000); return false; } return; }
    if (range && range !== ch.range) { const ser = seriesFromRange(sheetById(ch.sheetId), range); if (!ser || !ser.length) { toast(T('ch_bad_range'), 5000); return false; } ch.series = ser; ch.range = range; }
    ch.type = type; ch.title = title; draw(ch); shell.setDirty();
  }, true]]);
}
export function init(univerAPI) {
  api = univerAPI;
  layer = el('div'); layer.id = 'chartLayer'; $('stage').appendChild(layer);
  const E = api.Event;
  for (const ev of [E.Scroll, E.SheetZoomChanged, E.SheetSkeletonChanged, E.ActiveSheetChanged]) if (ev) try { api.addEvent(ev, () => later(false)); } catch (e) { }
  try { api.addEvent(E.CommandExecuted, e => { if (charts.length && e.type === 2) later(true); }); } catch (e) { }
  addEventListener('resize', () => later(false));
  new ResizeObserver(() => later(false)).observe($('stage'));
  $('stage').addEventListener('pointerdown', e => { if (!e.target.closest('.rchart')) select(null); }, true);
}
