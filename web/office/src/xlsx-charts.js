// 試算表圖表 ⇄ 真正嘅 Excel 圖表（DrawingML chart）。ExcelJS 唔識圖表，所以喺佢寫完 / 讀之前，自己用 JSZip（MIT）處理 xlsx 入面嘅圖表檔。
// 支援：直條圖、橫條圖、折線圖、區域圖、圓形圖（每個系列：名、分類範圍、數值範圍）。其他種類（立體、組合、散佈圖…）讀唔到，當唔支援。
import JSZip from 'jszip';

const NS_C = 'http://schemas.openxmlformats.org/drawingml/2006/chart', NS_A = 'http://schemas.openxmlformats.org/drawingml/2006/main';
const NS_R = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships', NS_XDR = 'http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing';
const CT_DRAWING = 'application/vnd.openxmlformats-officedocument.drawing+xml', CT_CHART = 'application/vnd.openxmlformats-officedocument.drawingml.chart+xml';
const REL_DRAWING = NS_R + '/drawing', REL_CHART = NS_R + '/chart';
export const EMU = 9525;   // 每像素
const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
/** 'Sheet 1'!$A$1:$B$3 */
export function refStr(sheetName, a1) {
  const abs = a1.replace(/\$/g, '').split(':').map(p => p.replace(/^([A-Z]+)(\d+)$/, '$$$1$$$2')).join(':');
  return (/^[A-Za-z_][A-Za-z0-9_.]*$/.test(sheetName) ? sheetName : "'" + sheetName.replace(/'/g, "''") + "'") + '!' + abs;
}
/** → { sheet, a1 }（冇 $）；唔係普通範圍就 null */
export function parseRef(f) {
  const m = /^(?:'((?:[^']|'')+)'|([^'!]+))!(\$?[A-Z]+\$?\d+(?::\$?[A-Z]+\$?\d+)?)$/.exec(String(f || '').trim());
  return m ? { sheet: (m[1] !== undefined ? m[1].replace(/''/g, "'") : m[2]), a1: m[3].replace(/\$/g, '') } : null;
}

// ---------- 寫 ----------
const strCache = vals => `<c:strCache><c:ptCount val="${vals.length}"/>${vals.map((v, i) => `<c:pt idx="${i}"><c:v>${esc(v)}</c:v></c:pt>`).join('')}</c:strCache>`;
const numCache = vals => `<c:numCache><c:formatCode>General</c:formatCode><c:ptCount val="${vals.length}"/>${vals.map((v, i) => (v === null || v === '' || isNaN(+v)) ? '' : `<c:pt idx="${i}"><c:v>${+v}</c:v></c:pt>`).join('')}</c:numCache>`;
function serXml(s, i, kind) {
  const tx = s.name ? (s.name.ref ? `<c:tx><c:strRef><c:f>${esc(s.name.ref)}</c:f>${strCache([s.name.text])}</c:strRef></c:tx>` : `<c:tx><c:v>${esc(s.name.text)}</c:v></c:tx>`) : '';
  const cat = s.cat ? `<c:cat><c:strRef><c:f>${esc(s.cat.ref)}</c:f>${strCache(s.cat.values)}</c:strRef></c:cat>` : '';
  const val = `<c:val><c:numRef><c:f>${esc(s.val.ref)}</c:f>${numCache(s.val.values)}</c:numRef></c:val>`;
  return `<c:ser><c:idx val="${i}"/><c:order val="${i}"/>${tx}${kind === 'bar' ? '<c:invertIfNegative val="0"/>' : ''}${kind === 'line' ? '<c:marker><c:symbol val="none"/></c:marker>' : ''}${cat}${val}${kind === 'line' ? '<c:smooth val="0"/>' : ''}</c:ser>`;
}
/** ch = { type: column|bar|line|area|pie, title, series: [{ name: {ref?, text}, cat: {ref, values}, val: {ref, values} }] } */
export function chartXml(ch) {
  const horiz = ch.type === 'bar', ax = '<c:axId val="50010001"/><c:axId val="50010002"/>';
  let plot;
  if (ch.type === 'pie') plot = `<c:pieChart><c:varyColors val="1"/>${ch.series.slice(0, 1).map((s, i) => serXml(s, i, 'pie')).join('')}<c:firstSliceAng val="0"/></c:pieChart>`;
  else if (ch.type === 'line') plot = `<c:lineChart><c:grouping val="standard"/><c:varyColors val="0"/>${ch.series.map((s, i) => serXml(s, i, 'line')).join('')}<c:marker val="1"/>${ax}</c:lineChart>`;
  else if (ch.type === 'area') plot = `<c:areaChart><c:grouping val="standard"/><c:varyColors val="0"/>${ch.series.map((s, i) => serXml(s, i, 'area')).join('')}${ax}</c:areaChart>`;
  else plot = `<c:barChart><c:barDir val="${horiz ? 'bar' : 'col'}"/><c:grouping val="clustered"/><c:varyColors val="0"/>${ch.series.map((s, i) => serXml(s, i, 'bar')).join('')}<c:gapWidth val="150"/>${ax}</c:barChart>`;
  const axes = ch.type === 'pie' ? '' :
    `<c:catAx><c:axId val="50010001"/><c:scaling><c:orientation val="minMax"/></c:scaling><c:delete val="0"/><c:axPos val="${horiz ? 'l' : 'b'}"/><c:numFmt formatCode="General" sourceLinked="0"/><c:majorTickMark val="out"/><c:minorTickMark val="none"/><c:tickLblPos val="nextTo"/><c:crossAx val="50010002"/><c:crosses val="autoZero"/><c:auto val="1"/><c:lblAlgn val="ctr"/><c:lblOffset val="100"/><c:noMultiLvlLbl val="0"/></c:catAx>` +
    `<c:valAx><c:axId val="50010002"/><c:scaling><c:orientation val="minMax"/></c:scaling><c:delete val="0"/><c:axPos val="${horiz ? 'b' : 'l'}"/><c:majorGridlines/><c:numFmt formatCode="General" sourceLinked="0"/><c:majorTickMark val="out"/><c:minorTickMark val="none"/><c:tickLblPos val="nextTo"/><c:crossAx val="50010001"/><c:crosses val="autoZero"/><c:crossBetween val="${ch.type === 'area' ? 'midCat' : 'between'}"/></c:valAx>`;
  const title = ch.title ? `<c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang="en-US"/><a:t>${esc(ch.title)}</a:t></a:r></a:p></c:rich></c:tx><c:overlay val="0"/></c:title><c:autoTitleDeleted val="0"/>` : '<c:autoTitleDeleted val="1"/>';
  return `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<c:chartSpace xmlns:c="${NS_C}" xmlns:a="${NS_A}" xmlns:r="${NS_R}"><c:roundedCorners val="0"/><c:chart>${title}<c:plotArea><c:layout/>${plot}${axes}</c:plotArea><c:legend><c:legendPos val="r"/><c:overlay val="0"/></c:legend><c:plotVisOnly val="1"/><c:dispBlanksAs val="gap"/></c:chart></c:chartSpace>`;
}
const xml = s => new DOMParser().parseFromString(s, 'application/xml');
const ser = d => '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n' + new XMLSerializer().serializeToString(d.documentElement);
async function sheetPaths(zip) {
  const wb = xml(await zip.file('xl/workbook.xml').async('string')), rels = xml(await zip.file('xl/_rels/workbook.xml.rels').async('string'));
  const target = {}; for (const r of rels.documentElement.children) target[r.getAttribute('Id')] = r.getAttribute('Target');
  const out = [];
  for (const s of wb.getElementsByTagName('sheet')) { const t = target[s.getAttributeNS(NS_R, 'id') || s.getAttribute('r:id')]; if (t) out.push({ name: s.getAttribute('name'), path: t.startsWith('/') ? t.slice(1) : 'xl/' + t }); }
  return out;
}
const relsOf = p => p.replace(/([^/]+)$/, '_rels/$1.rels');
/**
 * 將圖表寫入 ExcelJS 出嘅 xlsx。bySheet = { 工作表名: [{ anchor: {col, row, dx, dy, w, h}（像素）, chart: chartXml 嘅輸入 }] }
 */
export async function addCharts(bytes, bySheet) {
  if (!Object.values(bySheet).some(a => a && a.length)) return bytes;
  const zip = await JSZip.loadAsync(bytes), sheets = await sheetPaths(zip);
  const ct = xml(await zip.file('[Content_Types].xml').async('string'));
  const override = (part, type) => { const o = ct.createElementNS(ct.documentElement.namespaceURI, 'Override'); o.setAttribute('PartName', '/' + part); o.setAttribute('ContentType', type); ct.documentElement.appendChild(o); };
  let nd = 1, nc = 1;
  while (zip.file(`xl/drawings/drawing${nd}.xml`)) nd++;
  while (zip.file(`xl/charts/chart${nc}.xml`)) nc++;
  for (const sh of sheets) {
    const list = bySheet[sh.name]; if (!list || !list.length) continue;
    const sdoc = xml(await zip.file(sh.path).async('string')), root = sdoc.documentElement;
    if (root.getElementsByTagName('drawing').length) continue;   // 已經有 drawing（例如圖片）：唔掂，費事整壞
    const dpath = `xl/drawings/drawing${nd++}.xml`;
    let anchors = '', drels = '';
    list.forEach((it, i) => {
      const cpath = `xl/charts/chart${nc++}.xml`, a = it.anchor;
      zip.file(cpath, chartXml(it.chart)); override(cpath, CT_CHART);
      drels += `<Relationship Id="rId${i + 1}" Type="${REL_CHART}" Target="../charts/${cpath.split('/').pop()}"/>`;
      anchors += `<xdr:oneCellAnchor><xdr:from><xdr:col>${a.col}</xdr:col><xdr:colOff>${Math.round(a.dx * EMU)}</xdr:colOff><xdr:row>${a.row}</xdr:row><xdr:rowOff>${Math.round(a.dy * EMU)}</xdr:rowOff></xdr:from><xdr:ext cx="${Math.round(a.w * EMU)}" cy="${Math.round(a.h * EMU)}"/>` +
        `<xdr:graphicFrame macro=""><xdr:nvGraphicFramePr><xdr:cNvPr id="${i + 2}" name="Chart ${i + 1}"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr><xdr:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/></xdr:xfrm>` +
        `<a:graphic><a:graphicData uri="${NS_C}"><c:chart xmlns:c="${NS_C}" xmlns:r="${NS_R}" r:id="rId${i + 1}"/></a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:oneCellAnchor>`;
    });
    zip.file(dpath, `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<xdr:wsDr xmlns:xdr="${NS_XDR}" xmlns:a="${NS_A}">${anchors}</xdr:wsDr>`);
    zip.file(relsOf(dpath), `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">${drels}</Relationships>`);
    override(dpath, CT_DRAWING);
    // 工作表 → drawing 關係
    const rp = relsOf(sh.path), rf = zip.file(rp);
    const rdoc = xml(rf ? await rf.async('string') : '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>');
    let k = 1; const ids = new Set([...rdoc.documentElement.children].map(x => x.getAttribute('Id'))); while (ids.has('rId' + k)) k++;
    const rel = rdoc.createElementNS(rdoc.documentElement.namespaceURI, 'Relationship'); rel.setAttribute('Id', 'rId' + k); rel.setAttribute('Type', REL_DRAWING); rel.setAttribute('Target', '../drawings/' + dpath.split('/').pop());
    rdoc.documentElement.appendChild(rel); zip.file(rp, ser(rdoc));
    // <drawing r:id> 要放喺指定位置（legacyDrawing / tableParts / extLst 之前）
    const dr = sdoc.createElementNS(root.namespaceURI, 'drawing'); dr.setAttributeNS(NS_R, 'r:id', 'rId' + k);
    const after = ['legacyDrawing', 'legacyDrawingHF', 'drawingHF', 'picture', 'oleObjects', 'controls', 'webPublishItems', 'tableParts', 'extLst'];
    root.insertBefore(dr, [...root.children].find(c => after.includes(c.localName)) || null);
    zip.file(sh.path, ser(sdoc));
  }
  zip.file('[Content_Types].xml', ser(ct));
  return zip.generateAsync({ type: 'uint8array', compression: 'DEFLATE' });
}

/**
 * 俾 ExcelJS 讀之前，拎走工作表同 drawing（圖表 / 圖片）嘅連結。
 * ExcelJS 遇到佢唔識嘅 drawing（例如只有圖表、或者用絕對路徑）會成個檔讀唔到；圖表我哋另外由原檔讀（readCharts）。
 */
export async function stripDrawings(buffer) {
  let zip; try { zip = await JSZip.loadAsync(buffer); } catch (e) { return buffer; }
  if (!Object.keys(zip.files).some(p => p.startsWith('xl/drawings/'))) return buffer;
  try {
    for (const p of Object.keys(zip.files)) {
      if (/^xl\/worksheets\/[^/]+\.xml$/.test(p)) {
        const t = await zip.file(p).async('string'), t2 = t.replace(/<(?:\w+:)?(?:drawing|legacyDrawing)\b[^>]*\/>/g, '');
        if (t2 !== t) zip.file(p, t2);
      } else if (/^xl\/worksheets\/_rels\/[^/]+\.rels$/.test(p)) {
        const t = await zip.file(p).async('string'), t2 = t.replace(/<Relationship\b[^>]*Type="[^"]*\/(?:drawing|vmlDrawing)"[^>]*\/>/g, '');
        if (t2 !== t) zip.file(p, t2);
      }
    }
    // drawing / 圖表檔本身都拎走（ExcelJS 會逐個讀，讀到唔識嘅會出錯）
    for (const p of Object.keys(zip.files)) if (/^xl\/(drawings|charts)\//.test(p)) zip.remove(p);
    return await zip.generateAsync({ type: 'arraybuffer' });
  } catch (e) { console.error(e); return buffer; }
}

// ---------- 讀 ----------
const g = (n, name) => n ? [...n.getElementsByTagNameNS('*', name)] : [];
const g1 = (n, name) => g(n, name)[0] || null;
const child = (n, name) => n ? [...n.children].find(c => c.localName === name) || null : null;
const norm = (base, t) => { if (t.startsWith('/')) return t.slice(1); const out = base.split('/').slice(0, -1); for (const p of t.split('/')) { if (p === '..') out.pop(); else if (p !== '.') out.push(p); } return out.join('/'); };
async function relMap(zip, path) { const f = zip.file(relsOf(path)); const m = {}; if (!f) return m; for (const r of xml(await f.async('string')).documentElement.children) m[r.getAttribute('Id')] = { type: r.getAttribute('Type'), target: norm(path, r.getAttribute('Target')) }; return m; }
function parseChart(doc) {
  const plot = g1(doc, 'plotArea'); if (!plot) return null;
  const kinds = [...plot.children].filter(c => /Chart$/.test(c.localName));
  if (kinds.length !== 1) return null;   // 組合圖：唔支援
  const k = kinds[0]; let type;
  if (k.localName === 'barChart') type = (child(k, 'barDir') || { getAttribute: () => 'col' }).getAttribute('val') === 'bar' ? 'bar' : 'column';
  else if (k.localName === 'lineChart') type = 'line'; else if (k.localName === 'areaChart') type = 'area'; else if (k.localName === 'pieChart') type = 'pie'; else return null;
  const grouping = child(k, 'grouping'); if (grouping && /stacked/i.test(grouping.getAttribute('val') || '')) return null;   // 堆疊：暫時唔支援
  const series = [];
  for (const s of [...k.children].filter(c => c.localName === 'ser')) {
    const tx = child(s, 'tx'), cat = child(s, 'cat'), val = child(s, 'val');
    const vf = val && g1(val, 'f'), v = vf && parseRef(vf.textContent); if (!v) return null;
    const cf = cat && g1(cat, 'f'), c = cf ? parseRef(cf.textContent) : null; if (cf && !c) return null;
    const tf = tx && g1(tx, 'f'), tv = tx && g1(tx, 'v');
    const name = tf ? (parseRef(tf.textContent) ? { ref: parseRef(tf.textContent) } : null) : (tv ? { text: tv.textContent } : null);
    series.push({ name, cat: c, val: v });
  }
  if (!series.length) return null;
  const tt = g1(doc, 'title'), title = tt ? g(tt, 't').map(x => x.textContent).join('') : '';
  return { type, title, series };
}
/** 讀檔案入面嘅圖表。回傳 { charts: [{ sheet, anchor: {from, to?, ext?}（EMU / 格）, type, title, series }], unsupported: 有冇讀唔到嘅圖表 } */
export async function readCharts(buffer) {
  const out = { charts: [], unsupported: false };
  let zip; try { zip = await JSZip.loadAsync(buffer); } catch (e) { return out; }
  if (!Object.keys(zip.files).some(p => p.startsWith('xl/charts/'))) return out;
  try {
    for (const sh of await sheetPaths(zip)) {
      const rels = await relMap(zip, sh.path), dr = Object.values(rels).find(r => r.type === REL_DRAWING);
      if (!dr || !zip.file(dr.target)) continue;
      const ddoc = xml(await zip.file(dr.target).async('string')), drels = await relMap(zip, dr.target);
      for (const an of ddoc.documentElement.children) {
        const ce = g1(an, 'chart'); if (!ce) continue;
        const rid = ce.getAttributeNS(NS_R, 'id') || ce.getAttribute('r:id'), cr = drels[rid];
        const cf = cr && zip.file(cr.target); if (!cf) { out.unsupported = true; continue; }
        const ch = parseChart(xml(await cf.async('string')));
        if (!ch) { out.unsupported = true; continue; }
        const pos = n => n ? { col: +g1(n, 'col').textContent, row: +g1(n, 'row').textContent, dx: +g1(n, 'colOff').textContent / EMU, dy: +g1(n, 'rowOff').textContent / EMU } : null;
        const ext = child(an, 'ext');
        out.charts.push({ sheet: sh.name, anchor: { from: pos(child(an, 'from')) || { col: 0, row: 0, dx: 0, dy: 0 }, to: pos(child(an, 'to')), ext: ext ? { w: +ext.getAttribute('cx') / EMU, h: +ext.getAttribute('cy') / EMU } : null }, ...ch });
      }
    }
    // 檔案入面仲有冇其他圖表檔（例如圖表工作表）冇讀到
    const total = Object.keys(zip.files).filter(p => /^xl\/charts\/chart\d+\.xml$/.test(p)).length;
    if (total > out.charts.length) out.unsupported = true;
  } catch (e) { console.error(e); out.unsupported = true; }
  return out;
}
