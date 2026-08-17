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
// 日志行时间（约定格式：yyyy-MM-dd HH:mm:ss.fff）
function fmtLogTime(t) {
  const d = new Date(t);
  const p = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${String(d.getMilliseconds()).padStart(3, '0')}`;
}
// 级别位（定宽 5：与文件/控制台的约定行格式一致；审计行用 AUDIT）
function levelTag(l) {
  if (l.isAudit) return 'AUDIT';
  return ({ Error: 'ERROR', Warn: 'WARN ', Info: 'INFO ', Debug: 'DEBUG', Trace: 'TRACE' })[l.level] || l.level;
}
function logRow(l) {
  return `<div><span class="t">${fmtLogTime(l.time)}</span> <span class="${clsForLog(l) || 'c'}">[${levelTag(l)}]</span> <span class="c">[${esc(l.category)}]</span> <span class="t">[#${String(l.seq).padStart(6, '0')}]</span> <span class="${clsForLog(l)}">${esc(l.message)}</span></div>`;
}
function wordOk(l) { return !logWord || logWord.split('|').some(w => l.message.includes(w)); }

// ---------- 日志（仅本页激活时轮询） ----------
let lastRenderedAuditSeq = 0;

async function loadLogs() {
  if (logPaused) return;
  const box = document.getElementById('logs');
  try {
    const url = '/api/logs?tail=300' + (logFilter ? '&category=' + encodeURIComponent(logFilter) : '') + (logLevel ? '&level=' + logLevel : '');
    const res = await apiGet(url);
    if (!res.fresh) return;   // 过期响应丢弃
    const logs = res.data;
    clearError(box);
    // 过滤/级别/暂停恢复 → 全量重绘；普通轮询按 seq 增量追加（避免全量 innerHTML 重绘丢滚动）
    if (needFullRerender) {
      box.innerHTML = logs.filter(wordOk).map(logRow).join('');
      while (box.children.length > 500) box.removeChild(box.firstChild);
      needFullRerender = false;
    } else {
      for (const l of logs) if (l.seq > lastRenderedSeq && wordOk(l)) appendCapped(box, logRow(l), 500);
    }
    if (logs.length) lastRenderedSeq = logs[logs.length - 1].seq;
  } catch (e) { reportError(box, e); }
}

// 流程日志页：审计行（AUDIT）全量显示，不过滤（与日志页错开轮询，不并发 → 不踩 apiGet 序号）
async function loadAuditLogs() {
  const box = document.getElementById('auditLogs');
  if (!box) return;
  try {
    const res = await apiGet('/api/logs?tail=300');
    if (!res.fresh) return;
    const audit = res.data.filter(l => l.isAudit);
    clearError(box);
    if (needFullRerender) {
      box.innerHTML = audit.map(logRow).join('');
      needFullRerender = false;
    } else {
      for (const l of audit) if (l.seq > lastRenderedAuditSeq) appendCapped(box, logRow(l), 500);
    }
    if (audit.length) lastRenderedAuditSeq = audit[audit.length - 1].seq;
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
