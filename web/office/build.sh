#!/bin/bash
# 建立文書工具網頁（輸出去 lib/office）。要先喺呢個資料夾行 npm install。
#   doc.js   = 首頁 + 文件（.docx）編輯器      sheet.js = 試算表（.xlsx）      slides.js = 簡報（.pptx）
cd "$(dirname "$0")"
OUT=../../lib/office
mkdir -p $OUT/uloc
node patch-deps.js || exit 1
EB="npx esbuild --bundle --minify --format=iife --target=chrome110 --loader:.css=css --legal-comments=none --define:process.env.NODE_ENV=\"production\" --define:global=window"
$EB src/app.jsx --outfile=$OUT/doc.js --metafile=meta.json || exit 1
$EB src/sheet.js --outfile=$OUT/sheet.js --metafile=meta-sheet.json || exit 1
for f in src/uloc/*.js; do $EB "$f" --outfile=$OUT/uloc/$(basename "$f") --log-level=error || exit 1; done
if [ -f src/slides.js ]; then $EB src/slides.js --outfile=$OUT/slides.js --metafile=meta-slides.json || exit 1; cp src/slides.html $OUT/slides.html; fi
cp src/index.html $OUT/index.html
cp src/sheet.html $OUT/sheet.html
node licenses.js > $OUT/THIRD-PARTY-LICENSES.txt
