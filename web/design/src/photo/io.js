// 修圖：開檔同儲存。
//  • 圖片（PNG、JPG、WebP、BMP、GIF）：用瀏覽器自己解碼，變成一個圖層。
//  • 設計工具專案（.rdesign）：ZIP，入面 doc.json + 每個圖層一張 PNG，圖層、透明度、混合模式都保留。
import JSZip from 'jszip';
import { PhotoDoc, Layer, canvas, ctx2d, BLENDS } from './doc.js';

export const FLAT = ['png', 'jpg', 'jpeg', 'webp'];
export const extOf = name => (/\.([a-z0-9]+)$/i.exec(name || '') || [])[1]?.toLowerCase() || '';

export async function openBytes(buf, name, T) {
  const ext = extOf(name);
  if (ext === 'rdesign') return openProject(buf);
  const blob = new Blob([buf]);
  let bmp;
  try { bmp = await createImageBitmap(blob, { imageOrientation: 'from-image' }); }
  catch { throw new Error(T('ph_badimg')); }
  const doc = new PhotoDoc(bmp.width, bmp.height);
  const l = new Layer(bmp.width, bmp.height, T('ph_background'));
  l.ctx.drawImage(bmp, 0, 0);
  bmp.close && bmp.close();
  doc.layers.push(l);
  return doc;
}

async function openProject(buf) {
  const zip = await JSZip.loadAsync(buf);
  const meta = JSON.parse(await zip.file('doc.json').async('string'));
  if (!meta || meta.format !== 'rabi-design-photo') throw new Error('format');
  const doc = new PhotoDoc(meta.w, meta.h);
  for (const m of meta.layers) {
    const l = new Layer(meta.w, meta.h, m.name || '');
    l.visible = m.visible !== false;
    l.opacity = typeof m.opacity === 'number' ? Math.max(0, Math.min(1, m.opacity)) : 1;
    l.blend = BLENDS.includes(m.blend) ? m.blend : 'source-over';
    if (m.text && typeof m.text.str === 'string') l.text = m.text;
    const f = zip.file(m.file);
    if (f) { const bmp = await createImageBitmap(await f.async('blob')); l.ctx.drawImage(bmp, m.x || 0, m.y || 0); bmp.close && bmp.close(); }
    doc.layers.push(l);
  }
  if (!doc.layers.length) doc.layers.push(new Layer(meta.w, meta.h, ''));
  doc.active = Math.max(0, Math.min(doc.layers.length - 1, meta.active | 0));
  return doc;
}

const toBlob = (c, type, q) => new Promise((ok, no) => c.toBlob(b => b ? ok(b) : no(new Error('encode')), type, q));
const bytesOf = async b => new Uint8Array(await b.arrayBuffer());

export async function saveProject(doc) {
  const zip = new JSZip();
  const layers = [];
  for (let i = 0; i < doc.layers.length; i++) {
    const l = doc.layers[i], file = 'layers/' + i + '.png';
    zip.file(file, await toBlob(l.canvas, 'image/png'));
    layers.push({ name: l.name, visible: l.visible, opacity: l.opacity, blend: l.blend, file, ...(l.text ? { text: l.text } : {}) });
  }
  zip.file('doc.json', JSON.stringify({ format: 'rabi-design-photo', version: 1, w: doc.w, h: doc.h, active: doc.active, layers }, null, 1));
  // 縮圖（俾之後首頁 / 檔案總管預覽用）
  const f = doc.flatten(), s = Math.min(1, 256 / Math.max(doc.w, doc.h)), th = canvas(doc.w * s, doc.h * s);
  ctx2d(th).drawImage(f, 0, 0, th.width, th.height);
  zip.file('thumb.png', await toBlob(th, 'image/png'));
  return zip.generateAsync({ type: 'uint8array', compression: 'DEFLATE', compressionOptions: { level: 6 } });
}

/** 合併圖層再編碼做 PNG / JPG / WebP。JPG 冇透明：底色用白色。 */
export async function exportFlat(doc, ext, quality = 0.92) {
  const jpg = ext === 'jpg' || ext === 'jpeg';
  const c = doc.flatten(jpg ? '#ffffff' : null);
  const type = jpg ? 'image/jpeg' : ext === 'webp' ? 'image/webp' : 'image/png';
  return bytesOf(await toBlob(c, type, quality));
}

export async function encodeFor(doc, ext, quality) {
  return ext === 'rdesign' ? saveProject(doc) : exportFlat(doc, ext, quality);
}
