// util.js — 通用工具（导出: fmtTime, fmtDur, esc, reportError, clearError, physOf, logOf）
function fmtTime(t) { return t ? new Date(t).toLocaleString('zh-CN', { hour12: false }) : '—'; }
function fmtDur(ms) { return ms < 1000 ? ms + 'ms' : (ms / 1000).toFixed(1) + 's'; }

// 站口物理↔逻辑映射（2026-08-20 现场口径，与后端 StationMap 同公式）：
// 物理奇数槽=左列、偶数槽=右列；逻辑编号按列自上而下（16 站 1..8/9..16，8 站 1..4/5..8）
const physOf = (L, N) => (L >= 1 && L <= N) ? (L <= N / 2 ? 2 * L - 1 : 2 * (L - N / 2)) : NaN;
const logOf = (p, N) => (p >= 1 && p <= N) ? (p % 2 === 1 ? (p + 1) / 2 : p / 2 + N / 2) : NaN;

// 转义 HTML 特殊字符——所有 API/用户数据插入 innerHTML 前必须过一遍（XSS 防线）
const esc = s => String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

// 加载失败提示 chip（替换静默 catch；下次成功自动清除）
function reportError(el, e) {
  if (!el) return;
  let chip = el.querySelector(':scope > .err-chip');
  if (!chip) { chip = document.createElement('div'); chip.className = 'err-chip'; el.prepend(chip); }
  chip.textContent = '加载失败：' + (e && e.message ? e.message : e);
}
function clearError(el) { const c = el && el.querySelector(':scope > .err-chip'); if (c) c.remove(); }

// 带过期响应丢弃的 GET（每个端点独立序号：只渲染最新一次请求的结果）
const fetchSeq = {};
async function apiGet(path) {
  const key = path.split('?')[0];
  const id = (fetchSeq[key] = (fetchSeq[key] || 0) + 1);
  const r = await fetch(path);
  if (!r.ok) throw new Error(path + ' HTTP ' + r.status);
  const j = await r.json();
  return { fresh: fetchSeq[key] === id, data: j };
}

// 追加并裁剪 DOM 行数上限（顶部裁剪 + 滚动补偿，不跳屏）
function appendCapped(el, html, cap) {
  const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
  el.insertAdjacentHTML('beforeend', html);
  let removed = 0;
  while (el.children.length > cap) { removed += el.firstChild.offsetHeight || 0; el.removeChild(el.firstChild); }
  if (removed && !atBottom) el.scrollTop = Math.max(0, el.scrollTop - removed);
  if (atBottom) el.scrollTop = el.scrollHeight;
}
