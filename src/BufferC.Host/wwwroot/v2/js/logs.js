// logs.js — 日志页（导出: clsForLog, logRow, wordOk, loadLogs, setLogFilter, setLogWord, setLogLevel, toggleLogPause）
// 依赖: main.js 的 logFilter/logPaused/logLevel/logWord/needFullRerender/lastRenderedSeq
function clsForLog(line) {
  if (line.level === 'Trace') return 'l-trace';
  if (line.level === 'Debug') return 'l-debug';
  if (line.level === 'ERROR') return 'l-err';
  if (line.level === 'WARN') return 'l-warn';
  if (line.category === 'CMD' || line.category === 'S2F41') return 'l-cmd';
  if (line.message.includes('异常') || line.message.includes('失败') || line.message.includes('超时')) return 'l-err';
  if (line.message.includes('已连接') || line.message.includes('OK')) return 'l-ok';
  if (line.category === '←MCS' || line.category === '→MCS') return 'l-frame';
  return '';
}
function logRow(l) { return `<div><span class="t">${fmtTime(l.time)}</span> <span class="c">[${esc(l.category)}]</span> <span class="${clsForLog(l)}">${esc(l.message)}</span></div>`; }
function wordOk(l) { return !logWord || logWord.split('|').some(w => l.message.includes(w)); }

// ---------- 日志（仅本页激活时轮询） ----------
async function loadLogs() {
  if (logPaused) return;
  const box = document.getElementById('logs');
  try {
    const url = '/api/logs?tail=200' + (logFilter ? '&category=' + encodeURIComponent(logFilter) : '') + (logLevel ? '&level=' + logLevel : '');
    const logs = await (await fetch(url)).json();
    clearError(box);
    const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 30;
    // 过滤/级别/暂停恢复 → 全量重绘；普通轮询按 seq 增量追加（避免全量 innerHTML 重绘丢滚动）
    if (needFullRerender) {
      box.innerHTML = logs.filter(wordOk).map(logRow).join('');
      needFullRerender = false;
    } else {
      for (const l of logs) if (l.seq > lastRenderedSeq && wordOk(l)) box.insertAdjacentHTML('beforeend', logRow(l));
    }
    if (logs.length) lastRenderedSeq = logs[logs.length - 1].seq;
    if (atBottom) box.scrollTop = box.scrollHeight;
  } catch (e) { reportError(box, e); }
}

function setLogFilter(f) { logFilter = f; needFullRerender = true; loadLogs(); }
function setLogWord(w) { logWord = w; needFullRerender = true; loadLogs(); }
function setLogLevel(l) { logLevel = l; needFullRerender = true; loadLogs(); }
function toggleLogPause() {
  logPaused = !logPaused;
  const btn = document.getElementById('logPauseBtn');
  btn.textContent = logPaused ? '继续' : '暂停';
  document.getElementById('logState').textContent = logPaused ? '（已暂停）' : '';
  document.getElementById('logs').classList.toggle('paused', logPaused);
  if (!logPaused) { needFullRerender = true; loadLogs(); }
}
