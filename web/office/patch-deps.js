// 修補第三方套件嘅已知 bug（npm install 之後、打包之前行；build.sh 會自動叫）。重複行冇問題。
const fs = require('fs'), path = require('path');
function patch(file, from, to, note) {
  const p = path.join(__dirname, 'node_modules', file);
  let s = fs.readFileSync(p, 'utf8');
  if (s.includes(to)) return;
  if (!s.includes(from)) { console.error('patch-deps: 搵唔到要修補嘅位置（套件版本變咗？）: ' + note); process.exit(1); }
  fs.writeFileSync(p, s.replace(from, to));
  console.log('patched: ' + note);
}
// pptx-browser 4.1.5：renderTextBody 入面 defRPr 未宣告就用（段落有 a:pPr 就成個文字框畫唔出）
patch('pptx-browser/src/render.js',
  "    const bullet = pPr ? parseBullet(pPr, defRPr, themeColors, themeData) : null;\n",
  "    const bullet = pPr ? parseBullet(pPr, g1(pPr, 'defRPr'), themeColors, themeData) : null;\n",
  'pptx-browser render.js defRPr TDZ');
// pptx-browser 4.1.5：文字顏色已經係 CSS 字串，畫字時又當物件再轉一次 → 變咗無效顏色（有指定顏色嘅字畫唔出 / 畫錯色）
patch('pptx-browser/src/render.js',
  "        ctx.fillStyle = c ? colorToCss(c) : '#000000';\n\n        // Superscript / subscript",
  "        ctx.fillStyle = c ? (typeof c === 'string' ? c : colorToCss(c)) : '#000000';\n\n        // Superscript / subscript",
  'pptx-browser render.js run colour');
