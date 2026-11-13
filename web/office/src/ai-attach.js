// AI 面板嘅附件：📎 揀圖片 / 檔案、喺輸入格貼圖。附件由主程式保管，問完就刪（一次性）；對話本身亦唔會儲存。
// 文件頁（app.jsx）同試算表 / 簡報（shell.js）共用；PDF 頁（lib/pdfedit/editor.html）有一份同樣做法嘅細版。
export function setupAttach({ ai, post, T, note }) {
  const row = ai.querySelector('.in'), ta = row.querySelector('textarea');
  const chips = document.createElement('div'); chips.className = 'aifiles'; ai.insertBefore(chips, row);
  const clip = document.createElement('button'); clip.type = 'button'; clip.className = 'rb aiclip'; clip.textContent = '📎'; clip.title = T('ai_attach');
  clip.onclick = () => post({ t: 'ai-attach' });
  row.insertBefore(clip, ta);
  let files = [], lastHint = '';
  ta.addEventListener('paste', e => {
    const it = [...(e.clipboardData ? e.clipboardData.items : [])].find(x => x.kind === 'file' && /^image\//.test(x.type));
    if (!it) return;
    e.preventDefault();
    const f = it.getAsFile(); if (!f || f.size > 12 * 1024 * 1024) return;
    const r = new FileReader(); r.onload = () => post({ t: 'ai-attach-data', d: String(r.result).split(',')[1] }); r.readAsDataURL(f);
  });
  note(T('ai_once'));
  return {
    onFiles(m) {
      files = m.files || [];
      chips.textContent = '';
      for (const f of files) {
        const c = document.createElement('span'); c.className = 'aichip'; c.textContent = (f.image ? '🖼 ' : '📄 ') + f.name + ' ';
        const x = document.createElement('b'); x.textContent = '✕'; x.onclick = () => post({ t: 'ai-detach', name: f.name }); c.appendChild(x); chips.appendChild(c);
      }
      if (m.hint && m.hint !== lastHint) note(m.hint);   // 而家嘅 AI 睇唔到圖：即刻講
      lastHint = m.hint || '';
    },
    label() { return files.length ? '\n📎 ' + files.map(f => f.name).join('、') : ''; },
  };
}
