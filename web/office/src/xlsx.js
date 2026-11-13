// .xlsx ⇄ Univer 活頁簿資料。讀寫用 ExcelJS（MIT）。
// 支援：數值、文字、公式、日期、字型 / 顏色 / 底色 / 對齊 / 換行 / 框線 / 數字格式、合併儲存格、欄寬列高、凍結窗格、多個工作表。
// 唔支援（開檔時會提示，儲存會另存新檔）：圖表、圖片、樞紐分析表、條件式格式、資料驗證。
import ExcelJS from 'exceljs/dist/exceljs.bare.min.js';

const BORDER_IN = { thin: 1, hair: 2, dotted: 3, dashed: 4, dashDot: 5, dashDotDot: 6, double: 7, medium: 8, mediumDashed: 9, mediumDashDot: 10, mediumDashDotDot: 11, slantDashDot: 12, thick: 13 };
const BORDER_OUT = Object.fromEntries(Object.entries(BORDER_IN).map(([k, v]) => [v, k]));
const H_IN = { left: 1, center: 2, right: 3, justify: 4, fill: 1, centerContinuous: 2, distributed: 4 }, H_OUT = { 1: 'left', 2: 'center', 3: 'right', 4: 'justify' };
const V_IN = { top: 1, middle: 2, bottom: 3, justify: 2, distributed: 2 }, V_OUT = { 1: 'top', 2: 'middle', 3: 'bottom' };
const rgbIn = c => c && typeof c.argb === 'string' && c.argb.length >= 6 ? '#' + c.argb.slice(-6).toUpperCase() : null;
const rgbOut = s => { const m = /^#?([0-9a-f]{6})$/i.exec(s || ''); if (m) return { argb: 'FF' + m[1].toUpperCase() }; const r = /^rgba?\((\d+),\s*(\d+),\s*(\d+)/.exec(s || ''); return r ? { argb: 'FF' + [r[1], r[2], r[3]].map(x => (+x).toString(16).padStart(2, '0')).join('').toUpperCase() } : null; };
const EPOCH = Date.UTC(1899, 11, 30);

function styleIn(st) {
  const o = {};
  const f = st.font;
  if (f) { if (f.name) o.ff = f.name; if (f.size) o.fs = f.size; if (f.bold) o.bl = 1; if (f.italic) o.it = 1; if (f.underline) o.ul = { s: 1 }; if (f.strike) o.st = { s: 1 }; const c = rgbIn(f.color); if (c) o.cl = { rgb: c }; }
  const fl = st.fill;
  if (fl && fl.type === 'pattern' && fl.pattern === 'solid') { const c = rgbIn(fl.fgColor); if (c) o.bg = { rgb: c }; }
  const a = st.alignment;
  if (a) { if (H_IN[a.horizontal]) o.ht = H_IN[a.horizontal]; if (V_IN[a.vertical]) o.vt = V_IN[a.vertical]; if (a.wrapText) o.tb = 3; if (typeof a.textRotation === 'number' && a.textRotation) o.tr = { a: a.textRotation, v: 0 }; }
  const b = st.border;
  if (b) { const bd = {}; for (const [k, u] of [['top', 't'], ['bottom', 'b'], ['left', 'l'], ['right', 'r']]) if (b[k] && BORDER_IN[b[k].style]) bd[u] = { s: BORDER_IN[b[k].style], cl: { rgb: rgbIn(b[k].color) || '#000000' } }; if (Object.keys(bd).length) o.bd = bd; }
  if (st.numFmt && st.numFmt !== 'General') o.n = { pattern: st.numFmt };
  return o;
}
function styleOut(s, cell) {
  if (!s) return;
  const f = {};
  if (s.ff) f.name = s.ff; if (s.fs) f.size = s.fs; if (s.bl) f.bold = true; if (s.it) f.italic = true; if (s.ul && s.ul.s) f.underline = true; if (s.st && s.st.s) f.strike = true;
  if (s.cl && s.cl.rgb) { const c = rgbOut(s.cl.rgb); if (c) f.color = c; }
  if (Object.keys(f).length) cell.font = f;
  if (s.bg && s.bg.rgb) { const c = rgbOut(s.bg.rgb); if (c) cell.fill = { type: 'pattern', pattern: 'solid', fgColor: c }; }
  const a = {};
  if (H_OUT[s.ht]) a.horizontal = H_OUT[s.ht]; if (V_OUT[s.vt]) a.vertical = V_OUT[s.vt]; if (s.tb === 3) a.wrapText = true; if (s.tr && s.tr.a) a.textRotation = s.tr.a;
  if (Object.keys(a).length) cell.alignment = a;
  if (s.bd) { const b = {}; for (const [k, u] of [['top', 't'], ['bottom', 'b'], ['left', 'l'], ['right', 'r']]) { const x = s.bd[u]; if (x && BORDER_OUT[x.s]) b[k] = { style: BORDER_OUT[x.s], color: rgbOut(x.cl && x.cl.rgb) || { argb: 'FF000000' } }; } if (Object.keys(b).length) cell.border = b; }
  if (s.n && s.n.pattern) cell.numFmt = s.n.pattern;
}
const hasName = (bytes, name) => { const n = name.length; outer: for (let i = bytes.length - n; i >= 0; i--) { for (let k = 0; k < n; k++) if (bytes[i + k] !== name.charCodeAt(k)) continue outer; return true; } return false; };

/** 回傳 { data: Univer 活頁簿資料, lossy: [唔支援嘅內容代號] } */
export async function xlsxToUniver(buffer, name, clean) {
  const bytes = new Uint8Array(buffer);   // 原檔：用嚟睇有冇圖表 / 圖片 / 樞紐分析表
  const wb = new ExcelJS.Workbook();
  await wb.xlsx.load(clean || buffer);     // clean：拎走咗 drawing 連結嘅版本（見 xlsx-charts.stripDrawings）
  const lossy = new Set();
  if (hasName(bytes, 'xl/charts/')) lossy.add('chart');
  if (hasName(bytes, 'xl/media/')) lossy.add('image');
  if (hasName(bytes, 'xl/pivotTables/')) lossy.add('pivot');
  const styles = {}, styleIds = new Map();
  const sid = o => { const k = JSON.stringify(o); if (k === '{}') return undefined; let id = styleIds.get(k); if (!id) { id = 's' + (styleIds.size + 1); styleIds.set(k, id); styles[id] = o; } return id; };
  const data = { id: 'wb' + Date.now(), name: name || 'Workbook', appVersion: '1.0.0', locale: 'enUS', sheetOrder: [], sheets: {}, styles };
  let n = 0;
  wb.eachSheet(ws => {
    const id = 'sheet' + (++n);
    const cellData = {}, rowData = {}, columnData = {};
    let maxR = 0, maxC = 0;
    ws.eachRow({ includeEmpty: false }, (row, r) => {
      if (row.height) rowData[r - 1] = { h: Math.round(row.height * 4 / 3), ah: Math.round(row.height * 4 / 3) };
      if (row.hidden) rowData[r - 1] = { ...(rowData[r - 1] || {}), hd: 1 };
      row.eachCell({ includeEmpty: false }, (cell, c) => {
        if (cell.isMerged && cell.master !== cell) return;
        const out = {};
        let v = cell.value, st = styleIn(cell.style || {});
        if (v && typeof v === 'object' && !(v instanceof Date)) {
          if (v.formula != null || v.sharedFormula != null) { const f = cell.formula; if (f) out.f = '=' + f; v = v.result; if (v && typeof v === 'object' && !(v instanceof Date)) v = v.error || ''; }
          else if (v.richText) v = v.richText.map(t => t.text).join('');
          else if (v.hyperlink != null) v = v.text && v.text.richText ? v.text.richText.map(t => t.text).join('') : (v.text != null ? v.text : v.hyperlink);
          else if (v.error) v = v.error;
          else v = String(v);
        }
        if (v instanceof Date) { v = (v.getTime() - EPOCH) / 86400000; if (!st.n) st.n = { pattern: 'yyyy-mm-dd' }; out.t = 2; }
        if (typeof v === 'boolean') { out.v = v ? 1 : 0; out.t = 3; }
        else if (typeof v === 'number') { out.v = v; out.t = 2; }
        else if (v != null && v !== '') { out.v = String(v); out.t = 1; }
        const s = sid(st); if (s) out.s = s;
        if (out.v === undefined && !out.f && !out.s) return;
        (cellData[r - 1] = cellData[r - 1] || {})[c - 1] = out;
        if (r > maxR) maxR = r; if (c > maxC) maxC = c;
      });
    });
    for (let c = 1; c <= Math.max(maxC, ws.columnCount || 0); c++) { const col = ws.getColumn(c); if (col.width) columnData[c - 1] = { w: Math.round(col.width * 7 + 5) }; if (col.hidden) columnData[c - 1] = { ...(columnData[c - 1] || {}), hd: 1 }; }
    const mergeData = [];
    for (const m of (ws.model && ws.model.merges) || []) { const x = /^([A-Z]+)(\d+):([A-Z]+)(\d+)$/.exec(m); if (x) mergeData.push({ startRow: +x[2] - 1, endRow: +x[4] - 1, startColumn: colNum(x[1]), endColumn: colNum(x[3]) }); }
    const view = (ws.views && ws.views[0]) || {};
    const sheet = { id, name: ws.name, cellData, rowData, columnData, mergeData, rowCount: Math.max(1000, maxR + 200), columnCount: Math.max(26, maxC + 10),
      defaultColumnWidth: 88, defaultRowHeight: 24, showGridlines: view.showGridLines === false ? 0 : 1, hidden: ws.state && ws.state !== 'visible' ? 1 : 0 };
    if (view.state === 'frozen' && (view.xSplit || view.ySplit)) sheet.freeze = { xSplit: view.xSplit || 0, ySplit: view.ySplit || 0, startRow: view.ySplit || -1, startColumn: view.xSplit || -1 };
    if (ws.conditionalFormattings && ws.conditionalFormattings.length) lossy.add('cf');
    if (ws.dataValidations && ws.dataValidations.model && Object.keys(ws.dataValidations.model).length) lossy.add('dv');
    data.sheetOrder.push(id); data.sheets[id] = sheet;
  });
  if (!n) return { data: blankWorkbook(name), lossy: [] };
  return { data, lossy: [...lossy] };
}
export function colNum(s) { let n = 0; for (const ch of s) n = n * 26 + ch.charCodeAt(0) - 64; return n - 1; }
export function colName(n) { let s = ''; for (n++; n > 0; n = Math.floor((n - 1) / 26)) s = String.fromCharCode(65 + (n - 1) % 26) + s; return s; }
export function blankWorkbook(name, sheetName) {
  return { id: 'wb' + Date.now(), name: name || 'Workbook', appVersion: '1.0.0', locale: 'enUS', sheetOrder: ['sheet1'], styles: {},
    sheets: { sheet1: { id: 'sheet1', name: sheetName || 'Sheet1', cellData: {}, rowCount: 1000, columnCount: 26, defaultColumnWidth: 88, defaultRowHeight: 24 } } };
}

/** snap = Univer 活頁簿快照；formulas(sheetId) 回傳 { 'r,c': '=…' }（共用公式喺快照入面只得編號，要另外攞返字串）。 */
export async function univerToXlsx(snap, formulas) {
  const wb = new ExcelJS.Workbook();
  wb.creator = 'Rabi Toolkit'; wb.created = new Date();
  const styles = snap.styles || {};
  for (const id of snap.sheetOrder) {
    const sh = snap.sheets[id]; if (!sh) continue;
    const ws = wb.addWorksheet(String(sh.name || id).slice(0, 31), { state: sh.hidden ? 'hidden' : 'visible' });
    const view = { showGridLines: sh.showGridlines !== 0 };
    const fz = sh.freeze;
    if (fz && ((fz.xSplit || 0) > 0 || (fz.ySplit || 0) > 0)) Object.assign(view, { state: 'frozen', xSplit: fz.xSplit || 0, ySplit: fz.ySplit || 0 });
    ws.views = [view];
    const fm = formulas ? formulas(id) : {};
    for (const [r, row] of Object.entries(sh.cellData || {})) for (const [c, cd] of Object.entries(row || {})) {
      if (!cd) continue;
      const cell = ws.getCell(+r + 1, +c + 1);
      let v = cd.v;
      if (cd.p && cd.p.body && typeof cd.p.body.dataStream === 'string') v = cd.p.body.dataStream.replace(/\r\n$/, '').replace(/\r/g, '\n');
      const f = fm[r + ',' + c] || cd.f;
      if (cd.t === 3) v = !!(+v);
      else if (cd.t === 2 && v !== undefined && v !== '' && !isNaN(+v)) v = +v;
      if (f && typeof f === 'string' && f[0] === '=') cell.value = { formula: f.slice(1), result: v === undefined ? undefined : v };
      else if (v !== undefined && v !== null && v !== '') cell.value = v;
      styleOut(typeof cd.s === 'string' ? styles[cd.s] : cd.s, cell);
    }
    for (const [c, cd] of Object.entries(sh.columnData || {})) { if (!cd) continue; const col = ws.getColumn(+c + 1); if (cd.w) col.width = Math.max(1, (cd.w - 5) / 7); if (cd.hd) col.hidden = true; }
    for (const [r, rd] of Object.entries(sh.rowData || {})) { if (!rd) continue; const row = ws.getRow(+r + 1); const h = rd.h || (rd.ia === 0 ? 0 : 0); if (h && h !== (sh.defaultRowHeight || 24)) row.height = h * 0.75; if (rd.hd) row.hidden = true; }
    for (const m of sh.mergeData || []) try { ws.mergeCells(m.startRow + 1, m.startColumn + 1, m.endRow + 1, m.endColumn + 1); } catch (e) { }
  }
  return new Uint8Array(await wb.xlsx.writeBuffer());
}
