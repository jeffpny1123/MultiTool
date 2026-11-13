// 文書工具：試算表（.xlsx）。格仔介面用 Univer（Apache-2.0），讀寫 xlsx 用 ExcelJS（MIT）。選單跟 Google 試算表。
import { createUniver, LocaleType, mergeLocales, defaultTheme } from '@univerjs/presets';
import { UniverSheetsCorePreset } from '@univerjs/preset-sheets-core';
import { UniverSheetsFindReplacePreset } from '@univerjs/preset-sheets-find-replace';
import { UniverSheetsFilterPreset } from '@univerjs/preset-sheets-filter';
import { UniverSheetsSortPreset } from '@univerjs/preset-sheets-sort';
import '@univerjs/preset-sheets-core/lib/index.css';
import '@univerjs/preset-sheets-find-replace/lib/index.css';
import '@univerjs/preset-sheets-filter/lib/index.css';
import '@univerjs/preset-sheets-sort/lib/index.css';
import { xlsxToUniver, univerToXlsx, blankWorkbook, colName, colNum } from './xlsx.js';
import { addCharts, readCharts, stripDrawings } from './xlsx-charts.js';
import * as charts from './charts.js';
import { T, CFG, $, post, toast, busy, shell, initShell, buildMenubar, dialog, el } from './shell.js';
import './shell.css';

// 介面語言：sheet.html 按 CFG.lang 另外載入 uloc/<語言>.js（window.__ULOC），唔使將十種語言都打包入嚟
const LOC = { 'zh-Hant': LocaleType.ZH_TW, 'zh-Hans': LocaleType.ZH_CN, ja: LocaleType.JA_JP, ko: LocaleType.KO_KR, ru: LocaleType.RU_RU, es: LocaleType.ES_ES, 'pt-BR': LocaleType.PT_BR, de: LocaleType.DE_DE, fr: LocaleType.FR_FR }[CFG.lang] || LocaleType.EN_US;
const LOCDATA = mergeLocales(...(window.__ULOC || []));
// Univer 會清空佢個容器，所以另外開一格俾佢，圖表層放隔籬
{ const u = document.createElement('div'); u.id = 'univer'; $('stage').appendChild(u); }
const { univerAPI } = createUniver({
  locale: LOC, locales: { [LOC]: LOCDATA }, theme: defaultTheme, darkMode: false,   // 深色主題都用白底格仔：Univer 嘅深色模式會改晒儲存格顏色，同印出嚟 / 其他程式見到嘅唔同
  presets: [UniverSheetsCorePreset({ container: 'univer', header: true, toolbar: true, ribbonType: 'simple', formulaBar: true, footer: true }), UniverSheetsFindReplacePreset(), UniverSheetsFilterPreset(), UniverSheetsSortPreset()],
});
window.__api = univerAPI;
let lossy = [];

const wbk = () => univerAPI.getActiveWorkbook();
const sht = () => wbk() && wbk().getActiveSheet();
const sel = () => sht() && sht().getSelection() && sht().getSelection().getActiveRange();
const cmd = (id, params) => univerAPI.executeCommand(id, params);

function load(data, name, token) {
  charts.clear();
  const old = wbk();
  if (old) try { univerAPI.disposeUnit(old.getId()); } catch (e) { console.error(e); }
  univerAPI.createWorkbook(data);
  shell.opened(name, token);
}
async function openBytes(buf, name, token) {
  const r = await xlsxToUniver(buf, name, await stripDrawings(buf)), cr = await readCharts(buf);
  // 圖表全部讀得返（直條 / 橫條 / 折線 / 區域 / 圓形）就唔算「唔支援嘅內容」
  lossy = r.lossy.filter(k => k !== 'chart' || cr.unsupported);
  load(r.data, name, token);
  if (cr.charts.length) setTimeout(() => { try { charts.load(cr.charts); shell.setDirty(false); } catch (e) { console.error(e); } }, 300);
  if (lossy.length) dialog(T('sh_lossy_t'), T('sh_lossy', lossy.map(k => T('sh_l_' + k)).join('、')));
}
function newDoc() { lossy = []; load(blankWorkbook(T('sh_untitled'), T('sh_sheet') + '1'), T('sh_untitled') + '.xlsx', null); }
async function getBytes() {
  const w = wbk(), snap = w.save();
  const bytes = await univerToXlsx(snap, id => {
    const out = {};
    try { const s = w.getSheetBySheetId(id), dr = s.getDataRange(), fs = dr.getFormulas(), r0 = dr.getRow(), c0 = dr.getColumn(); fs.forEach((row, i) => row.forEach((f, j) => { if (f) out[(r0 + i) + ',' + (c0 + j)] = f; })); } catch (e) { console.error(e); }
    return out;
  });
  return addCharts(bytes, charts.forSave());   // 圖表寫做真正嘅 Excel 圖表
}
/** 而家工作表有資料嗰範圍嘅值（顯示用）。 */
function grid(max = 400) {
  const s = sht(); if (!s) return { r0: 0, c0: 0, rows: [] };
  const dr = s.getDataRange(), vals = dr.getValues(), fs = dr.getFormulas();
  return { r0: dr.getRow(), c0: dr.getColumn(), rows: vals.slice(0, max), formulas: fs.slice(0, max), total: vals.length };
}
function print() {
  const g = grid(5000), f = document.createElement('iframe');
  f.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0;visibility:hidden'; document.body.appendChild(f);
  const d = f.contentDocument, esc = x => String(x ?? '').replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
  d.write('<!doctype html><meta charset="utf-8"><title>' + esc(shell.name) + '</title><style>body{font:11px "Segoe UI","Microsoft JhengHei",sans-serif;margin:0}table{border-collapse:collapse}td{border:1px solid #bbb;padding:3px 6px;white-space:pre-wrap}h3{font-size:13px;margin:0 0 8px}@page{margin:12mm}</style><h3>' + esc(sht().getSheetName()) + '</h3><table>' +
    g.rows.map(r => '<tr>' + r.map(v => '<td>' + esc(v) + '</td>').join('') + '</tr>').join('') + '</table>');
  d.close();
  setTimeout(() => { f.contentWindow.focus(); f.contentWindow.print(); setTimeout(() => f.remove(), 60000); }, 200);
}
function downloadCsv() {
  const g = grid(100000), q = v => { v = String(v ?? ''); return /[",\n]/.test(v) ? '"' + v.replace(/"/g, '""') + '"' : v; };
  const csv = '﻿' + g.rows.map(r => r.map(q).join(',')).join('\r\n');
  shell.exportFile(new TextEncoder().encode(csv), shell.name.replace(/\.xlsx$/i, '') + '-' + sht().getSheetName() + '.csv');
}

// ---------- AI ----------
function aiContext() {
  const g = grid(300); let text = '';
  g.rows.forEach((row, i) => row.forEach((v, j) => { const f = g.formulas[i][j]; if (v !== '' && v != null || f) { const line = colName(g.c0 + j) + (g.r0 + i + 1) + ': ' + (f ? f + ' → ' : '') + String(v ?? '') + '\n'; if (text.length < 45000) text += line; } }));
  const cl = charts.list();
  if (cl.length) text += '\n# charts\n' + cl.map(c => '#' + c.n + ' ' + c.type + ' ' + c.sheet + '!' + c.range + (c.title ? ' "' + c.title + '"' : '')).join('\n') + '\n';
  return { text, info: T('sh_sheet') + ': ' + sht().getSheetName() + ' (' + wbk().getSheets().map(s => s.getSheetName()).join(', ') + ')' };
}
function aiApply(list) {
  const s = sht(); let n = 0;
  for (const a of list) {
    try {
      if (a.op === 'set' && a.cell) { s.getRange(a.cell).setValue(typeof a.value === 'string' && a.value[0] === '=' ? { f: a.value } : a.value); n++; }
      else if (a.op === 'fill' && a.range && Array.isArray(a.values)) { const m = /^([A-Z]+)(\d+)/.exec(a.range); const r0 = +m[2] - 1, c0 = colNum(m[1]); a.values.forEach((row, i) => row.forEach((v, j) => s.getRange(r0 + i, c0 + j).setValue(typeof v === 'string' && v[0] === '=' ? { f: v } : v))); n++; }
      else if (a.op === 'format' && a.range) { const r = s.getRange(a.range);
        if (a.bold != null) r.setFontWeight(a.bold ? 'bold' : 'normal'); if (a.italic != null) r.setFontStyle(a.italic ? 'italic' : 'normal'); if (a.color) r.setFontColor(a.color); if (a.bg) r.setBackground(a.bg);
        if (a.align) r.setHorizontalAlignment(a.align); if (a.size) r.setFontSize(+a.size); if (a.numfmt) r.setNumberFormat(a.numfmt); if (a.wrap != null) r.setWrap(!!a.wrap); n++; }
      else if (a.op === 'clear' && a.range) { s.getRange(a.range).clearContent(); n++; }
      else if (a.op === 'insertRows' && a.before) { s.insertRows(+a.before - 1, +a.count || 1); n++; }
      else if (a.op === 'insertCols' && a.before) { s.insertColumns(colNum(String(a.before).toUpperCase()), +a.count || 1); n++; }
      else if (a.op === 'deleteRows' && a.row) { s.deleteRows(+a.row - 1, +a.count || 1); n++; }
      else if (a.op === 'deleteCols' && a.col) { s.deleteColumns(colNum(String(a.col).toUpperCase()), +a.count || 1); n++; }
      else if (a.op === 'chart' && a.range) { if (charts.add(a.type || 'column', String(a.range).toUpperCase(), a.title || '', s)) n++; }
      else if (a.op === 'deleteChart') { const c = charts.list()[(+a.chart || 1) - 1]; if (c) { charts.removeAt((+a.chart || 1) - 1); n++; } }
      else if (a.op === 'addSheet') { wbk().create(a.name || (T('sh_sheet') + (wbk().getSheets().length + 1)), 1000, 26); n++; }
    } catch (e) { console.error(a, e); }
  }
  return n;
}

// ---------- 選單（跟 Google 試算表）----------
const fmt = p => () => { const r = sel(); if (r) r.setNumberFormat(p); };
const align = h => () => { const r = sel(); if (r) r.setHorizontalAlignment(h); };
const valign = v => () => { const r = sel(); if (r) r.setVerticalAlignment(v); };
function freeze(rows, cols) { const s = sht(); if (rows != null) s.setFrozenRows(rows); if (cols != null) s.setFrozenColumns(cols); }
function rowsCols(kind) {
  const r = sel(), s = sht(); if (!r) return;
  const r0 = r.getRow(), c0 = r.getColumn(), nr = r.getHeight(), nc = r.getWidth();
  if (kind === 'ra') s.insertRowsBefore(r0, nr); else if (kind === 'rb') s.insertRowsAfter(r0 + nr - 1, nr);
  else if (kind === 'cl') s.insertColumnsBefore(c0, nc); else if (kind === 'cr') s.insertColumnsAfter(c0 + nc - 1, nc);
  else if (kind === 'dr') s.deleteRows(r0, nr); else if (kind === 'dc') s.deleteColumns(c0, nc);
}
function zoom(z) { try { sht().zoom(z); } catch (e) { cmd('sheet.operation.set-zoom-ratio', { zoomRatio: z, unitId: wbk().getId(), subUnitId: sht().getSheetId() }); } }
function renameSheet() {
  const box = el('div'); const i = el('input'); i.type = 'text'; i.value = sht().getSheetName(); box.appendChild(i);
  dialog(T('sh_rename'), box, [[T('cancel')], [T('ok'), () => { const v = i.value.trim(); if (v) sht().setName(v); }, true]]);
}
function insertFn(name) { const r = sel(); if (!r) return; const s = sht(), r0 = r.getRow(), c0 = r.getColumn(), nr = r.getHeight(), nc = r.getWidth();
  if (nr * nc > 1) { const tgt = s.getRange(r0 + nr, c0); tgt.setValue({ f: '=' + name + '(' + colName(c0) + (r0 + 1) + ':' + colName(c0 + nc - 1) + (r0 + nr) + ')' }); }
  else r.setValue({ f: '=' + name + '()' }); }
const MENUS = [
  { label: T('m_file'), items: () => [
    { label: T('m_new'), icon: '＋', run: () => { if (shell.okToLeave()) newDoc(); } }, { label: T('open'), key: 'Ctrl+O', run: shell.pick }, '-',
    { label: T('save'), key: 'Ctrl+S', run: () => shell.save(false) }, { label: T('saveas'), key: 'Ctrl+Shift+S', run: () => shell.save(true) },
    { label: T('sh_csv'), run: downloadCsv }, '-', { label: T('print'), key: 'Ctrl+P', run: print }] },
  { label: T('m_edit'), items: () => [
    { label: T('undo'), icon: '↶', key: 'Ctrl+Z', run: () => univerAPI.undo() }, { label: T('redo'), icon: '↷', key: 'Ctrl+Y', run: () => univerAPI.redo() }, '-',
    { label: T('cut'), key: 'Ctrl+X', run: () => document.execCommand('cut') }, { label: T('copy'), key: 'Ctrl+C', run: () => document.execCommand('copy') }, { label: T('paste'), key: 'Ctrl+V', run: () => toast(T('paste_tip')) }, '-',
    { label: T('findreplace'), icon: '⌕', key: 'Ctrl+H', run: () => cmd('ui.operation.open-replace-dialog') },
    { label: T('sh_delvals'), run: () => { const r = sel(); if (r) r.clearContent(); } },
    { label: T('sh_delrow'), run: () => rowsCols('dr') }, { label: T('sh_delcol'), run: () => rowsCols('dc') }] },
  { label: T('m_view'), items: () => [
    { label: T('sh_freeze'), sub: [{ label: T('sh_fz_none'), run: () => freeze(0, 0) }, { label: T('sh_fz_r1'), run: () => freeze(1, null) }, { label: T('sh_fz_r2'), run: () => freeze(2, null) }, '-',
      { label: T('sh_fz_c1'), run: () => freeze(null, 1) }, { label: T('sh_fz_c2'), run: () => freeze(null, 2) }] },
    { label: T('sh_grid'), check: sht() && sht().hasHiddenGridLines && !sht().hasHiddenGridLines(), run: () => cmd('sheet.command.toggle-gridlines') },
    { label: T('sh_zoom'), sub: [50, 75, 100, 125, 150, 200].map(z => ({ label: z + '%', run: () => zoom(z / 100) })) }] },
  { label: T('m_insert'), items: () => [
    { label: T('sh_row_above'), run: () => rowsCols('ra') }, { label: T('sh_row_below'), run: () => rowsCols('rb') }, { label: T('sh_col_left'), run: () => rowsCols('cl') }, { label: T('sh_col_right'), run: () => rowsCols('cr') }, '-',
    { label: T('sh_newsheet'), run: () => wbk().create(T('sh_sheet') + (wbk().getSheets().length + 1), 1000, 26) }, '-',
    { label: T('ch_insert'), icon: '📊', sub: charts.TYPES.map(t => ({ label: T('ch_t_' + t), run: () => charts.editDialog(null, t) })) },
    { label: T('sh_function'), icon: 'Σ', sub: ['SUM', 'AVERAGE', 'COUNT', 'MAX', 'MIN'].map(f => ({ label: f, run: () => insertFn(f) })) }] },
  { label: T('m_format'), items: () => [
    { label: T('sh_number'), sub: [{ label: T('sh_n_auto'), run: fmt('General') }, { label: T('sh_n_num'), key: '1,000.12', run: fmt('#,##0.00') }, { label: T('sh_n_pct'), key: '10.12%', run: fmt('0.00%') },
      { label: T('sh_n_cur'), key: '$1,000.12', run: fmt('"$"#,##0.00') }, { label: T('sh_n_date'), key: '2026-09-26', run: fmt('yyyy-mm-dd') }, { label: T('sh_n_time'), key: '15:59:00', run: fmt('hh:mm:ss') }, { label: T('sh_n_text'), run: fmt('@') }] },
    { label: T('sh_text'), sub: [{ label: T('bold'), key: 'Ctrl+B', run: () => cmd('sheet.command.set-range-bold') }, { label: T('italic'), key: 'Ctrl+I', run: () => cmd('sheet.command.set-range-italic') }, { label: T('underline'), key: 'Ctrl+U', run: () => cmd('sheet.command.set-range-underline') }] },
    { label: T('sh_align'), sub: [{ label: T('al_left'), run: align('left') }, { label: T('al_center'), run: align('center') }, { label: T('al_right'), run: align('right') }, '-', { label: T('al_top'), run: valign('top') }, { label: T('al_middle'), run: valign('middle') }, { label: T('al_bottom'), run: valign('bottom') }] },
    { label: T('sh_wrap'), sub: [{ label: T('sh_wrap_over'), run: () => sel() && sel().setWrap(false) }, { label: T('sh_wrap_wrap'), run: () => sel() && sel().setWrap(true) }] }, '-',
    { label: T('sh_merge'), sub: [{ label: T('sh_merge_all'), run: () => cmd('sheet.command.add-worksheet-merge-all') }, { label: T('sh_merge_h'), run: () => cmd('sheet.command.add-worksheet-merge-horizontal') }, { label: T('sh_merge_v'), run: () => cmd('sheet.command.add-worksheet-merge-vertical') }, { label: T('sh_unmerge'), run: () => cmd('sheet.command.remove-worksheet-merge') }] }, '-',
    { label: T('sh_clearfmt'), key: 'Ctrl+\\', run: () => cmd('sheet.command.clear-selection-format') }] },
  { label: T('m_data'), items: () => [
    { label: T('sh_sort_az'), run: () => cmd('sheet.command.sort-range-asc') }, { label: T('sh_sort_za'), run: () => cmd('sheet.command.sort-range-desc') }, { label: T('sh_sort_custom'), run: () => cmd('sheet.command.sort-range-custom') }, '-',
    { label: T('sh_filter'), run: () => cmd('sheet.command.smart-toggle-filter') }] },
  { label: T('m_tools'), items: () => [
    { label: T('sh_rename'), run: renameSheet },
    { label: T('sh_stats'), icon: '#', run: () => { const g = grid(100000); let cells = 0; g.rows.forEach(r => r.forEach(v => { if (v !== '' && v != null) cells++; })); dialog(T('sh_stats'), T('sh_stats_body', wbk().getSheets().length, g.rows.length, g.rows[0] ? g.rows[0].length : 0, cells)); } }] },
];

initShell({ ext: 'xlsx', getBytes, print, openBytes, newDoc, aiContext, aiApply, get lossy() { return lossy.length > 0; } });
buildMenubar($('menubar'), MENUS);
charts.init(univerAPI);
// 有改動（mutation）就當未儲存
try { univerAPI.addEvent(univerAPI.Event.CommandExecuted, e => { if (e.type === 2 && !/selection|scroll|zoom|formula\.mutation|set-formula|other-formula|calculation|render|skeleton/i.test(e.id)) shell.setDirty(); }); } catch (e) { console.error(e); }
window.__test = { charts, openBytes, newDoc, getBytes, aiApply, aiContext, grid, MENUS };
if (location.hash === '#new') newDoc(); else busy(T('loading'));
post({ t: 'ready', page: 'sheet' });
if (location.hash === '#new') busy('');
