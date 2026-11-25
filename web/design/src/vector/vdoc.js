// 向量繪圖：文件（paper.js 場景）、復原紀錄、開檔同儲存。
//  • 三個圖層：board（畫板白底，唔匯出）、art（用家畫嘅嘢）、（選取框等等畫喺另一張 2D canvas，唔入 paper）。
//  • 復原：每一步記低 art 圖層嘅 JSON（向量檔細，簡單又穩陣）。
//  • 檔案：SVG（開 / 存）、設計工具專案 .rdesign（ZIP：doc.json + thumb.png）、匯出 PNG / PDF。
import paper from 'paper/dist/paper-core';
import JSZip from 'jszip';
import { jsPDF } from 'jspdf';
import 'svg2pdf.js';

export { paper };

export class VectorDoc {
  /** scope = 已經 setup 咗 canvas 嘅 PaperScope。 */
  constructor(scope, w, h) {
    this.scope = scope;
    this.w = w; this.h = h;
    this.undoStack = []; this.redoStack = [];
    this.dirty = false;
    this.onChange = () => { };
    scope.activate();
    scope.project.clear();
    this.board = new scope.Layer({ name: 'board', locked: true });
    this.art = new scope.Layer({ name: 'art' });
    this.drawBoard();
    this.art.activate();
    this.cur = this.snapshot();
  }
  drawBoard() {
    this.scope.activate();
    this.board.removeChildren();
    this.board.addChild(new this.scope.Path.Rectangle({ point: [0, 0], size: [this.w, this.h], fillColor: '#ffffff', shadowColor: new this.scope.Color(0, 0, 0, 0.35), shadowBlur: 12, shadowOffset: [0, 2] }));
  }
  get rect() { return new this.scope.Rectangle(0, 0, this.w, this.h); }

  // ---------- 復原 ----------
  snapshot() { return JSON.stringify({ w: this.w, h: this.h, art: this.art.exportJSON({ asString: false }) }); }
  restore(s) {
    const o = JSON.parse(s);
    this.scope.activate();
    this.w = o.w; this.h = o.h; this.drawBoard();
    this.art.removeChildren();
    this.art.importJSON(o.art);
    this.art.activate();
  }
  /** 做完一件事之後叫：記入復原紀錄。 */
  commit(label) {
    const after = this.snapshot();
    if (after === this.cur) return;
    this.undoStack.push({ label, before: this.cur, after });
    if (this.undoStack.length > 100) this.undoStack.shift();
    this.redoStack.length = 0;
    this.cur = after;
    this.dirty = true;
    this.onChange('commit');
  }
  /** 改咗嘢但想放棄（例如拉到一半撳 Esc）。 */
  revert() { this.restore(this.cur); this.onChange('restore'); }
  undo() { const e = this.undoStack.pop(); if (!e) return null; this.restore(e.before); this.cur = e.before; this.redoStack.push(e); this.dirty = true; this.onChange('restore'); return e.label; }
  redo() { const e = this.redoStack.pop(); if (!e) return null; this.restore(e.after); this.cur = e.after; this.undoStack.push(e); this.dirty = true; this.onChange('restore'); return e.label; }

  // ---------- 匯出 ----------
  /** 畫板範圍嘅 SVG（唔包畫板白底）。 */
  svgString() {
    this.scope.activate();
    // 畫板白底唔匯出：暫時拎走個圖層
    this.board.remove();
    let s;
    try { s = this.scope.project.exportSVG({ asString: true, bounds: this.rect, matchShapes: true }); }
    finally { this.scope.project.insertLayer(0, this.board); this.art.activate(); }
    if (!/^<\?xml/.test(s)) s = '<?xml version="1.0" encoding="UTF-8"?>\n' + s;
    return s;
  }
  async png(scale = 1, bg = null) {
    const svg = this.svgString();
    const url = URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml' }));
    try {
      const img = new Image();
      await new Promise((ok, no) => { img.onload = ok; img.onerror = () => no(new Error('svg')); img.src = url; });
      const c = document.createElement('canvas');
      c.width = Math.max(1, Math.round(this.w * scale)); c.height = Math.max(1, Math.round(this.h * scale));
      const g = c.getContext('2d');
      if (bg) { g.fillStyle = bg; g.fillRect(0, 0, c.width, c.height); }
      g.drawImage(img, 0, 0, c.width, c.height);
      return c;
    } finally { URL.revokeObjectURL(url); }
  }
  async pngBytes(scale = 1) {
    const c = await this.png(scale);
    const b = await new Promise(ok => c.toBlob(ok, 'image/png'));
    return new Uint8Array(await b.arrayBuffer());
  }
  /** PDF：向量輸出；有中日韓等文字（PDF 標準字型冇）就改用高解像度圖片，免得變亂碼。 */
  async pdfBytes() {
    const w = this.w, h = this.h;
    const pdf = new jsPDF({ unit: 'px', format: [w, h], orientation: w > h ? 'landscape' : 'portrait', hotfixes: ['px_scaling'], compress: true });
    let raster = false;
    this.art.getItems({ class: this.scope.PointText }).forEach(t => { if (/[^\x00-\x7f]/.test(t.content)) raster = true; });
    if (raster) {
      const c = await this.png(3, '#ffffff');
      pdf.addImage(c.toDataURL('image/png'), 'PNG', 0, 0, w, h);
    } else {
      const el = new DOMParser().parseFromString(this.svgString(), 'image/svg+xml').documentElement;
      document.body.appendChild(el);   // svg2pdf 要量度字型，要喺 DOM 入面
      try { await pdf.svg(el, { x: 0, y: 0, width: w, height: h }); } finally { el.remove(); }
    }
    return { bytes: new Uint8Array(pdf.output('arraybuffer')), raster };
  }
  async projectBytes() {
    const zip = new JSZip();
    zip.file('doc.json', JSON.stringify({ format: 'rabi-design-vector', version: 1, w: this.w, h: this.h, art: JSON.parse(this.art.exportJSON()) }));
    try {
      const s = Math.min(1, 256 / Math.max(this.w, this.h)), c = await this.png(s, '#ffffff');
      zip.file('thumb.png', await new Promise(ok => c.toBlob(ok, 'image/png')));
    } catch { }
    return zip.generateAsync({ type: 'uint8array', compression: 'DEFLATE', compressionOptions: { level: 6 } });
  }
}

/** 由 SVG 文字開：畫板大細跟 viewBox / width、height。 */
export function docFromSvg(scope, text) {
  scope.activate();
  const el = new DOMParser().parseFromString(text, 'image/svg+xml').documentElement;
  if (!el || el.nodeName.toLowerCase() !== 'svg') throw new Error('svg');
  const vb = (el.getAttribute('viewBox') || '').split(/[\s,]+/).map(Number);
  const num = v => { const n = parseFloat(v); return isFinite(n) && n > 0 ? n : 0; };
  const w = vb.length === 4 && vb[2] > 0 ? vb[2] : num(el.getAttribute('width')) || 800;
  const h = vb.length === 4 && vb[3] > 0 ? vb[3] : num(el.getAttribute('height')) || 600;
  const d = new VectorDoc(scope, Math.round(w), Math.round(h));
  const g = scope.project.importSVG(text, { insert: false, expandShapes: false });
  if (g) {
    // 匯入嘅最外層：攞走同畫板一樣大嘅剪裁遮罩，內容直接放入 art
    let kids = g.removeChildren();
    if (kids.length && kids[0].clipMask && Math.abs(kids[0].bounds.width - w) < 1 && Math.abs(kids[0].bounds.height - h) < 1) kids = kids.slice(1);
    if (vb.length === 4 && (vb[0] || vb[1])) kids.forEach(k => k.translate(new scope.Point(-vb[0], -vb[1])));
    d.art.addChildren(kids);
  }
  d.cur = d.snapshot();
  return d;
}

export async function docFromProject(scope, buf) {
  const zip = await JSZip.loadAsync(buf);
  const meta = JSON.parse(await zip.file('doc.json').async('string'));
  if (!meta || meta.format !== 'rabi-design-vector') throw new Error('format');
  const d = new VectorDoc(scope, meta.w, meta.h);
  d.art.importJSON(meta.art);
  d.art.activate();
  d.cur = d.snapshot();
  return d;
}

/** 睇吓個 .rdesign 係修圖定向量。 */
export async function projectKind(buf) {
  try { const zip = await JSZip.loadAsync(buf); const m = JSON.parse(await zip.file('doc.json').async('string')); return m.format === 'rabi-design-vector' ? 'vector' : 'photo'; }
  catch { return 'photo'; }
}
