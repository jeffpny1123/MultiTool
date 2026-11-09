// 影片下載（工作坊工具）：用 yt-dlp 將網上影片存做 MP3 / MP4
// yt-dlp.exe 放喺插件自己嘅資料夾；ffmpeg 用拉比工具箱共用嗰個（mt.ffmpeg）
'use strict';

const YTDLP_URL = 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe';
const EXE = 'yt-dlp.exe';
// yt-dlp 自己搵 plugin：<exe 資料夾>\yt-dlp-plugins\<名>\yt_dlp_plugins\extractor\*.py
const BILI = 'yt-dlp-plugins/multitool/yt_dlp_plugins/extractor/multitool_bili.py';

const T = {
  zh: {
    title: '影片下載', setupTitle: '第一次使用：下載 yt-dlp', install: '下載並安裝',
    setupHint: '影片下載要用開源工具 yt-dlp（約 18 MB，由 GitHub 官方下載）同 ffmpeg（同錄影、轉檔共用，未有會一齊下載，約 100 MB）。',
    intro: '將網上影片存做 MP3 或者 MP4。只下載你有權使用嘅內容（例如自己嘅影片、容許下載嘅作品）。',
    sites: '支援 YouTube、Bilibili、Twitch、Vimeo、X、Reddit、TikTok、SoundCloud 等 1000 幾個網站；唔支援要登入或者有版權保護嘅串流（Netflix、Spotify 等）。',
    siteList: '完整網站列表', urlPh: '貼上影片連結，可以一次貼幾條，一行一條（Ctrl + Enter 開始）',
    paste: '貼上', format: '格式', quality: '品質', playlist: '播放清單：成個下載', saveTo: '儲存位置', change: '更改…', openFolder: '開啟資料夾',
    download: '⬇ 下載', update: '更新 yt-dlp', list: '下載清單', listHint: '一個一個下載；網站改版下載唔到嘅話，撳「更新 yt-dlp」再試。',
    clear: '清除已完成', none: '未有下載', best: '最好', open: '打開', noFolder: '（未揀）',
    needUrl: '請貼上 http / https 連結', needFolder: '請先揀儲存位置', waiting: '等緊…', analysing: '分析緊…', downloading: '下載緊…',
    dlAudio: '下載緊聲音…', toMp3: '轉換做 MP3…', merging: '合併影片同聲音…', thumb: '加入封面…', done: '✓ 完成', cancelled: '已取消',
    failed: '失敗：', doneToast: '下載完成', updating: '更新緊 yt-dlp…', updated: '✓ yt-dlp 已係最新：', updateFail: '更新失敗：',
    busy: '下載緊，完咗先更新', getYtdlp: '下載 yt-dlp…', getFfmpeg: '準備 ffmpeg…', ready: '✓ 準備好', setupFail: '安裝失敗：'
  },
  en: {
    title: 'Video Downloader', setupTitle: 'First use: download yt-dlp', install: 'Download and install',
    setupHint: 'This uses the open-source yt-dlp (about 18 MB, from its official GitHub) and ffmpeg (shared with recording and conversion; downloaded too if missing, about 100 MB).',
    intro: 'Save online videos as MP3 or MP4. Only download content you have the right to use (e.g. your own videos or works that allow downloading).',
    sites: 'Supports YouTube, Bilibili, Twitch, Vimeo, X, Reddit, TikTok, SoundCloud and 1000+ other sites; not videos that need a login or DRM streams (Netflix, Spotify, etc.).',
    siteList: 'Full site list', urlPh: 'Paste video links, one per line (Ctrl + Enter to start)',
    paste: 'Paste', format: 'Format', quality: 'Quality', playlist: 'Playlist: download all', saveTo: 'Save to', change: 'Change…', openFolder: 'Open folder',
    download: '⬇ Download', update: 'Update yt-dlp', list: 'Downloads', listHint: 'Downloads run one at a time. If a site changed and downloads fail, click "Update yt-dlp" and try again.',
    clear: 'Clear finished', none: 'No downloads yet', best: 'Best', open: 'Open', noFolder: '(not chosen)',
    needUrl: 'Paste http / https links', needFolder: 'Choose a save folder first', waiting: 'Waiting…', analysing: 'Analysing…', downloading: 'Downloading…',
    dlAudio: 'Downloading audio…', toMp3: 'Converting to MP3…', merging: 'Merging video and audio…', thumb: 'Adding cover…', done: '✓ Done', cancelled: 'Cancelled',
    failed: 'Failed: ', doneToast: 'Download finished', updating: 'Updating yt-dlp…', updated: '✓ yt-dlp is up to date: ', updateFail: 'Update failed: ',
    busy: 'A download is running; update after it finishes', getYtdlp: 'Downloading yt-dlp…', getFfmpeg: 'Preparing ffmpeg…', ready: '✓ Ready', setupFail: 'Setup failed: '
  }
};
let L = T.en;
const $ = id => document.getElementById(id);

let folder = '', ffDir = '', busy = false;
const jobs = [];
let current = null;

const AQ = [['0', 'best'], ['320K', '320 kbps'], ['192K', '192 kbps'], ['128K', '128 kbps']];
const VQ = [['best', 'best'], ['2160', '2160P (4K)'], ['1440', '1440P (2K)'], ['1080', '1080P'], ['720', '720P'], ['480', '480P'], ['360', '360P']];

function fillQuality(sel) {
  const list = $('fmt').value === 'mp3' ? AQ : VQ;
  $('q').innerHTML = list.map(([v, t]) => `<option value="${v}">${t === 'best' ? L.best : t}</option>`).join('');
  if (sel && list.some(x => x[0] === sel)) $('q').value = sel;
}

async function save() {
  await mt.storage.set('settings', { fmt: $('fmt').value, q: $('q').value, folder });
}

async function ready() {
  return (await mt.file.exists(EXE));
}

// ---------- 安裝 ----------
async function install() {
  $('install').disabled = true;
  const st = $('setupStatus'), bar = $('setupBar');
  try {
    st.textContent = L.getYtdlp;
    await mt.download(YTDLP_URL, EXE, p => { if (p.total > 0) bar.style.width = (p.done / p.total * 100).toFixed(0) + '%'; });
    st.textContent = L.getFfmpeg;
    bar.style.width = '0%';
    ffDir = await mt.ffmpeg(p => { st.textContent = p.text || L.getFfmpeg; if (p.progress >= 0) bar.style.width = (p.progress * 100).toFixed(0) + '%'; });
    await writeBili();
    st.textContent = L.ready;
    show();
  } catch (e) {
    st.textContent = L.setupFail + e.message;
  } finally { $('install').disabled = false; }
}

// Bilibili 修正（yt-dlp 未修好前用住）
async function writeBili() {
  try { const r = await fetch('multitool_bili.py'); if (r.ok) await mt.file.write(BILI, await r.text()); } catch { }
}

async function show() {
  const ok = await ready();
  $('setup').style.display = ok ? 'none' : 'block';
  $('main').style.opacity = ok ? '1' : '.5';
}

// ---------- 下載清單 ----------
function addJobs() {
  const urls = $('url').value.split('\n').map(x => x.trim()).filter(u => /^https?:\/\//i.test(u));
  if (!urls.length) { $('status').textContent = L.needUrl; return; }
  if (!folder) { $('status').textContent = L.needFolder; return; }
  for (const u of urls)
    jobs.push({ url: u, audio: $('fmt').value === 'mp3', q: $('q').value, playlist: $('playlist').checked, title: u, status: L.waiting, progress: -1 });
  $('url').value = '';
  $('status').textContent = '';
  render();
  runQueue();
}

function render() {
  const box = $('jobs');
  if (!jobs.length) { box.innerHTML = `<div class="status">${L.none}</div>`; return; }
  box.innerHTML = '';
  jobs.forEach(j => {
    const row = document.createElement('div');
    row.className = 'job';
    const pct = j.progress >= 0 && !j.done && !j.failed ? ' ' + Math.round(j.progress * 100) + '%' : '';
    row.innerHTML = `<span class="t"></span><span class="s ${j.done ? 'ok' : j.failed ? 'bad' : ''}"></span>`;
    row.querySelector('.t').textContent = (j.audio ? '♪ ' : '▶ ') + j.title;
    row.querySelector('.s').textContent = j.status + pct;
    if (j.file) {
      const b = document.createElement('button');
      b.textContent = L.open;
      b.onclick = () => mt.file.reveal(j.file).catch(() => {});
      row.appendChild(b);
    }
    const x = document.createElement('button');
    x.textContent = '✕';
    x.onclick = () => {
      if (j === current) mt.exec.kill();
      jobs.splice(jobs.indexOf(j), 1);
      render();
    };
    row.appendChild(x);
    box.appendChild(row);
  });
}

async function runQueue() {
  if (busy) return;
  if (!(await ready())) { show(); return; }
  busy = true;
  try {
    if (!ffDir) ffDir = await mt.ffmpeg();
    let j;
    while ((j = jobs.find(x => !x.done && !x.failed))) await runJob(j);
  } catch (e) { $('status').textContent = L.failed + e.message; }
  finally { busy = false; current = null; }
}

function argsOf(j) {
  // --encoding utf-8：唔加會用系統編碼輸出，中文名會變亂碼
  const a = ['--encoding', 'utf-8', '--newline', '--progress', '--no-colors', '--windows-filenames', '--no-mtime',
    '--ffmpeg-location', ffDir, j.playlist ? '--yes-playlist' : '--no-playlist',
    '-P', folder, '-o', j.playlist ? '%(playlist_title|)s\\%(playlist_index|)s %(title)s.%(ext)s' : '%(title)s.%(ext)s',
    '--print', 'before_dl:TITLE %(title)s', '--print', 'after_move:FILE %(filepath)s'];
  if (j.audio) a.push('-x', '--audio-format', 'mp3', '--audio-quality', j.q, '--embed-thumbnail', '--embed-metadata');
  else {
    const h = j.q === 'best' ? '' : `[height<=${j.q}]`;
    a.push('-f', `bv*${h}[ext=mp4]+ba[ext=m4a]/bv*${h}+ba/b${h}`, '--merge-output-format', 'mp4', '--embed-metadata');
  }
  a.push(j.url);
  return a;
}

async function runJob(j) {
  current = j;
  j.status = L.analysing; j.progress = -1;
  render();
  const errs = [];
  let part = 0, lastDraw = 0;
  const redraw = () => { const now = Date.now(); if (now - lastDraw > 300) { lastDraw = now; render(); } };
  try {
    const r = await mt.exec(EXE, argsOf(j), {
      onOutput: o => {
        const l = (o.line || '').trim();
        if (!l) return;
        if (l.startsWith('TITLE ')) { j.title = l.slice(6); part = 0; redraw(); return; }
        if (l.startsWith('FILE ')) { j.file = l.slice(5).trim(); return; }
        const m = /\[download\]\s+([\d.]+)%/.exec(l);
        if (m) {
          const v = parseFloat(m[1]);
          if (v < 1 && j.progress > 0.9) part++; // 影片下完再落聲音
          j.progress = v / 100;
          j.status = part > 0 ? L.dlAudio : L.downloading;
          redraw();
          return;
        }
        if (l.includes('[ExtractAudio]')) { j.status = L.toMp3; j.progress = -1; redraw(); }
        else if (l.includes('[Merger]')) { j.status = L.merging; j.progress = -1; redraw(); }
        else if (l.includes('[EmbedThumbnail]')) { j.status = L.thumb; j.progress = -1; redraw(); }
        if (o.err || /^ERROR/i.test(l)) errs.push(l);
      }
    });
    if (!jobs.includes(j)) return; // 用家撳咗 ✕
    if (r.code === 0) {
      j.done = true; j.progress = -1; j.status = L.done;
      mt.notify(L.doneToast, j.title).catch(() => {});
    } else {
      j.failed = true;
      const e = errs.slice().reverse().find(x => x.startsWith('ERROR')) || errs[errs.length - 1] || ('exit ' + r.code);
      j.status = r.code < 0 || r.code === 1 && !errs.length ? L.cancelled : L.failed + e.replace('ERROR:', '').trim();
      console.warn(errs.join('\n'));
    }
  } catch (e) {
    j.failed = true; j.status = L.failed + e.message;
  }
  render();
}

async function selfUpdate() {
  if (!(await ready())) { show(); return; }
  if (busy) { $('status').textContent = L.busy; return; }
  $('status').textContent = L.updating;
  const out = [];
  try {
    const r = await mt.exec(EXE, ['-U'], { onOutput: o => { if (o.line && o.line.trim()) out.push(o.line.trim()); } });
    const last = out[out.length - 1] || '';
    $('status').textContent = r.code === 0 ? L.updated + last : L.updateFail + last;
  } catch (e) { $('status').textContent = L.updateFail + e.message; }
}

// ---------- 開始 ----------
(async () => {
  const info = await mt.info();
  L = (info.lang || '').startsWith('zh') ? T.zh : T.en;
  document.querySelectorAll('[data-t]').forEach(e => e.textContent = L[e.dataset.t] || '');
  $('url').placeholder = L.urlPh;
  const s = (await mt.storage.get('settings')) || {};
  $('fmt').value = s.fmt || 'mp3';
  fillQuality(s.q || ($('fmt').value === 'mp3' ? '0' : '1080'));
  folder = s.folder || '';
  $('folder').textContent = folder || L.noFolder;

  $('install').onclick = install;
  $('fmt').onchange = () => { fillQuality($('fmt').value === 'mp3' ? '0' : '1080'); save(); };
  $('q').onchange = save;
  $('paste').onclick = async () => { try { $('url').value = (await navigator.clipboard.readText()).trim(); } catch { } };
  $('pick').onclick = async () => {
    const p = await mt.file.pickFolder(folder);
    if (p) { folder = p; $('folder').textContent = p; save(); }
  };
  $('openFolder').onclick = () => { if (folder) mt.file.reveal(folder).catch(() => {}); };
  $('go').onclick = addJobs;
  $('url').addEventListener('keydown', e => { if (e.key === 'Enter' && e.ctrlKey) { e.preventDefault(); addJobs(); } });
  $('update').onclick = selfUpdate;
  $('clear').onclick = () => { for (let i = jobs.length - 1; i >= 0; i--) if (jobs[i].done || jobs[i].failed) jobs.splice(i, 1); render(); };
  $('siteList').onclick = () => mt.open('https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md');
  render();
  await show();
  if (await ready()) writeBili();
})();
