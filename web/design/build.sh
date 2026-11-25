#!/bin/bash
# 建立設計工具網頁（輸出去 lib/design）。要先喺呢個資料夾行 npm install。
#   design.js / design.css = 首頁 + 修圖編輯器
cd "$(dirname "$0")"
OUT=../../lib/design
mkdir -p $OUT
EB="npx esbuild --platform=browser --external:canvg --external:html2canvas --external:dompurify --bundle --minify --format=iife --target=chrome110 --loader:.css=css --legal-comments=none --define:process.env.NODE_ENV=\"production\" --define:global=window"
$EB src/main.js --outfile=$OUT/design.js --metafile=meta.json || exit 1
cp src/index.html $OUT/index.html
node licenses.js > $OUT/THIRD-PARTY-LICENSES.txt
