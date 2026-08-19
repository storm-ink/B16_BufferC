// pending.js — 载具事件待补数据（2026-08-19 载具确认机制）：全局提示条/事件页 tab/台账列时长/详情页闪烁弹窗
// 导出: loadPending, openFill, fillRow, submitFill, pendingRows, pendingDurFrom
// 依赖: util.js 的 esc/apiGet；mcstest.js 的 invRefresh；events.js 的 switchTab

let pendingRows = [];
let pendingFp = '';          // 待补 tab 表体指纹（不含时长——时长原地刷新，不打断输入）
let pendingSetFp = '';       // 等待集合指纹：变化时触发台账刷新（中间态行高亮即时出现）
let fillPlc = null, fillStation = null;

// 服务端时间戳 yyyyMMddHHmmssfff → Date（本地时区）
function pendingDurFrom(at) {
  const m = /^(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})(\d{3})$/.exec(at || '');
  if (!m) return '—';
  const t = new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6], +m[7]).getTime();
  return fmtDur(Date.now() - t);
}

// ---------- 全局轮询（3s）：提示条 + 角标 + 待补 tab（常开，页面无关——提示条在状态栏下方全局可见） ----------
async function loadPending() {
  try {
    const res = await apiGet('/api/pending-carrier-events');
    if (!res.fresh) return;
    pendingRows = res.data || [];
    renderPendingStrip();
    renderPendingBadge();
    renderPendingTab();
    // 等待集合变化 → 台账「事件待上报」列/行高亮同步刷新（invRefresh 对非总览页无副作用）
    const setFp = pendingRows.map(x => `${x.plcIndex}:${x.station}:${x.pendingCeid}`).join('¶');
    if (setFp !== pendingSetFp) { pendingSetFp = setFp; invRefresh(); }
  } catch (e) { /* 静默：pending 面板加载失败不打断其他面板 */ }
}

function renderPendingStrip() {
  const strip = document.getElementById('pendingStrip');
  if (!strip) return;
  strip.hidden = pendingRows.length === 0;
  if (pendingRows.length) strip.textContent = `${pendingRows.length} 个载具事件等待数据 — 点击补填`;
}

function renderPendingBadge() {
  const b = document.getElementById('pendingTabBadge');
  if (!b) return;
  const wantHidden = pendingRows.length === 0;
  if (b.hidden !== wantHidden) b.hidden = wantHidden;
  if (b.textContent !== String(pendingRows.length)) b.textContent = pendingRows.length;
}

// 待补数据 tab 表（指纹 diff 重建；时长按行原地刷新，输入框不被打断）
function renderPendingTab() {
  const el = document.getElementById('tbl-pending');
  if (!el) return;
  const fp = pendingRows.map(x => `${x.plcIndex}:${x.station}:${x.pendingCeid}:${x.pendingAt}`).join('¶');
  if (fp !== pendingFp) {
    pendingFp = fp;
    el.innerHTML =
      '<tr><th>机台</th><th>站口</th><th>等待事件</th><th>等待时长</th><th>载具 ID</th><th></th></tr>' +
      (pendingRows.length ? pendingRows.map(x => `<tr>
        <td>${esc(x.deviceNo)}</td>
        <td>${esc(x.unitId)}</td>
        <td><span class="chip-pending">${x.pendingCeid}</span> 等待货物ID</td>
        <td data-dur="${x.plcIndex}:${x.station}">${pendingDurFrom(x.pendingAt)}</td>
        <td><input class="fill-id" data-plc="${x.plcIndex}" data-station="${x.station}" placeholder="载具 ID" size="14" data-enter="fill-row"></td>
        <td><button data-action="fill-row" data-plc="${x.plcIndex}" data-station="${x.station}">补填</button></td>
      </tr>`).join('')
        : '<tr><td colspan="6" class="empty-row">无等待中的载具事件</td></tr>');
  }
  for (const td of el.querySelectorAll('[data-dur]')) {
    const [p, s] = td.dataset.dur.split(':');
    const row = pendingRows.find(x => String(x.plcIndex) === p && String(x.station) === s);
    td.textContent = row ? pendingDurFrom(row.pendingAt) : '—';
  }
}

// 行内补填按钮（与弹窗同一 API）
async function fillRow(plc, station) {
  const input = document.querySelector(`#tab-pending input[data-plc="${plc}"][data-station="${station}"]`);
  const id = input ? input.value.trim() : '';
  const msg = document.getElementById('pendingMsg');
  if (!id) { if (msg) msg.textContent = '请输入载具 ID'; return; }
  await postFill(plc, station, id, msg);
  if (input) input.value = '';
}

// ---------- 补填弹窗（设备详情页点击中间态站口弹出） ----------
function openFill(plc, station) {
  fillPlc = plc; fillStation = station;
  const row = pendingRows.find(x => x.plcIndex === plc && x.station === station);
  const box = document.getElementById('fillBox');
  document.getElementById('fillInfo').innerHTML = row
    ? `站口 <b>${esc(row.unitId)}</b> 等待货物 ID（事件 <span class="chip-pending">${row.pendingCeid}</span>）已等 ${pendingDurFrom(row.pendingAt)}`
    : `站口 ${plc}-${station} 无等待中的载具事件`;
  document.getElementById('fillResult').textContent = '';
  const input = document.getElementById('fillCarrierId');
  input.value = '';
  box.style.display = 'flex';
  input.focus();
}

async function submitFill() {
  const id = document.getElementById('fillCarrierId').value.trim();
  const msg = document.getElementById('fillResult');
  if (!id) { msg.textContent = '请输入载具 ID'; return; }
  await postFill(fillPlc, fillStation, id, msg);
  document.getElementById('fillCarrierId').value = '';
}

async function postFill(plc, station, carrierId, msg) {
  try {
    const r = await fetch('/api/pending-carrier-events/fill', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ plc, station, carrierId }),
    });
    const j = await r.json();
    if (msg) msg.textContent = j.ok ? j.message : `补填失败: ${j.message}`;
    invRefresh();      // 台账同步
    loadPending();     // 提示条/列表刷新
  } catch (e) {
    if (msg) msg.textContent = '请求失败: ' + e;
  }
}
