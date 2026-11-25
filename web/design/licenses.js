// 列出打包入 design.js 嘅所有第三方套件同授權全文（由 esbuild meta.json 攞清單）。
const fs = require('fs'), path = require('path');
const pk = new Set();
for (const mf of ['meta.json']) {
  if (!fs.existsSync(path.join(__dirname, mf))) continue;
  const meta = JSON.parse(fs.readFileSync(path.join(__dirname, mf), 'utf8'));
  for (const f of Object.keys(meta.inputs)) { const m = /node_modules\/((?:@[^/]+\/)?[^/]+)/.exec(f); if (m) pk.add(m[1]); }
}
let out = 'Third-party software bundled in design.js\n=================================================\n';
for (const n of [...pk].sort()) {
  const dir = path.join(__dirname, 'node_modules', n), pj = JSON.parse(fs.readFileSync(path.join(dir, 'package.json'), 'utf8'));
  const lf = fs.readdirSync(dir).filter(x => /^(licen[sc]e|notice|copying)/i.test(x));
  out += `\n\n----------------------------------------------------------------\n${n} ${pj.version}  (${typeof pj.license === 'string' ? pj.license : JSON.stringify(pj.license || pj.licenses)})\n----------------------------------------------------------------\n`;
  for (const l of lf) out += fs.readFileSync(path.join(dir, l), 'utf8').trim() + '\n';
  if (!lf.length) out += '(license text not shipped in the package; see ' + (pj.homepage || (pj.repository && (pj.repository.url || pj.repository)) || 'npm') + ')\n';
}
process.stdout.write(out);
