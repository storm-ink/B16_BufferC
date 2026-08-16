// status.js — 状态轮询 + 总览 + PLC 详情（导出: loadStatus, renderStatusBar, renderOverview, fillPlcSelects, detPlcChanged, getDetPlc, buildDetail, renderDetail, regRead, readRegBlocks, readIdRaw, readCmdRaw）
// 依赖: main.js 的 lastStatus/curPage/overviewKey/detPlc/detBuilt；protocol.js 的 unpackAscii/hex4

// ---------- 状态轮询（常开：状态栏 + 当前页共用） ----------
async function loadStatus() {
  try {
    const s = await (await fetch('/api/status')).json();
    lastStatus = s;
    fillPlcSelects(s.plcs);
    renderStatusBar(s);
    if (curPage === 'overview') renderOverview(s);
    if (curPage === 'plcdetail') renderDetail(s);
    if (curPage === 'plctest') renderPlctEcho();
    if (curPage === 'mcstest') { mcsBuild(); mcsRefresh(s); }
  } catch (e) {
    document.getElementById('statusbar').innerHTML = `<span class="badge b-err">加载失败: ${e}</span>`;
  }
}

// KPI 瓷砖行（形态启发式：少量大字标题数字 → 瓷砖，非彩色文字片段）
function renderStatusBar(s) {
  const online = s.plcs.filter(p => p.connected).length;
  const unacked = s.alarms.filter(a => !a.acked).length;
  const mcs = s.mcs, sys = s.system;
  const tile = (label, value, cls, note) =>
    `<span class="tile ${cls || ''}"><span class="tile-label">${label}</span><span class="tile-value">${value}</span>` +
    (note ? `<span class="tile-note">${note}</span>` : '') + `</span>`;
  // 未确认告警条（>0 显示，点击跳当前告警 tab）
  const strip = document.getElementById('alarmStrip');
  if (strip) { strip.hidden = unacked === 0; strip.textContent = `${unacked} 条未确认告警 — 点击查看`; }
  document.getElementById('statusbar').innerHTML =
    (s.hsmsConnected
      ? tile('MCS', 'ONLINE', 't-ok', `${esc(mcs.peerEndpoint)} ${esc(mcs.peerMdln) || '—'} 收:${mcs.messagesIn} 发:${mcs.messagesOut} T3:${mcs.t3Timeout} 失败:${mcs.sendFail}`)
      : tile('MCS', 'OFFLINE', 't-off')) +
    tile('PLC 在线', `${online}/${s.plcs.length}`, s.plcs.length - online ? 't-crit' : 't-ok') +
    tile('告警', String(unacked), unacked ? 't-crit alarm-on' : 't-ok', unacked ? (s.alarms.length - unacked ? `共 ${s.alarms.length} 条` : '未确认') : '无告警') +
    tile('运行', fmtDur(Date.now() - new Date(sys.startedAt).getTime())) +
    tile('版本', `${esc(sys.mdln)} ${esc(sys.softRev)}`) +
    tile('字节序', esc(sys.byteOrderSummary || '—'));
}

// ---------- 总览：15 台紧凑表（首建 + 原地更新） ----------
function renderOverview(s) {
  const tbody = document.querySelector('#tbl-overview tbody');
  const key = s.plcs.map(p => p.index).join(',');
  if (key !== overviewKey) {
    overviewKey = key;
    tbody.innerHTML = s.plcs.map(p => `
      <tr id="ov-${p.index}">
        <td>${p.index} 号 Buffer</td>
        <td data-k="state"></td>
        <td data-k="ip">${p.ip}</td>
        <td data-k="alarm"></td>
        <td data-k="occ"></td>
        <td data-k="poll"></td>
        <td data-k="err"></td>
        <td data-k="rc"></td>
        <td data-k="cmd"></td>
      </tr>`).join('');
  }
  for (const p of s.plcs) {
    const row = document.getElementById('ov-' + p.index);
    if (!row) continue;
    const st = p.stats;
    const occ = p.stations.filter(x => x.state === 1 || x.state === 5).length;
    row.querySelector('[data-k=state]').innerHTML = `<span class="badge ${p.connected ? 'b-ok' : 'b-off'}">${p.connected ? '在线' : '离线'}</span>`;
    row.querySelector('[data-k=alarm]').innerHTML = p.registers.alarmSummary ? `<span class="badge b-err">${p.registers.alarmSummary}</span>` : '—';
    row.querySelector('[data-k=occ]').textContent = `${occ}/16`;
    row.querySelector('[data-k=poll]').textContent = st.pollCount;
    row.querySelector('[data-k=err]').innerHTML = st.errorCount ? `<span class="fail-txt">${st.errorCount}</span>` : '0';
    row.querySelector('[data-k=rc]').textContent = st.reconnectCount;
    row.querySelector('[data-k=cmd]').textContent = `${st.commandCount}(${st.commandFailCount})`;
  }
}

// 站口状态图标（与 ST 状态名配对；色阶 + 图标 + 文字三者并用）
const STATE_ICON = { 0: '◌', 1: '●', 2: '↓', 3: '↑', 4: '⚠', 5: '✋' };

// ---------- PLC 详情：单台 16 站口 + 完整寄存器视图（原地更新防缩回） ----------
function fillPlcSelects(plcs) {
  // PLC 详情页选择器（下拉）；调试面板为手填输入（dbgPlcNum 解析）
  const sel = document.getElementById('detPlc');
  const cur = sel.value;
  sel.innerHTML = plcs.map(p =>
    `<option value="${p.index}" ${String(p.index) === cur ? 'selected' : ''}>${p.index} 号 Buffer（${p.connected ? '在线' : '离线'}）</option>`).join('');
  if (!sel.value && plcs.length) sel.value = plcs[0].index;
}

function detPlcChanged() { detBuilt = false; loadStatus(); }

function getDetPlc(s) {
  const sel = document.getElementById('detPlc');
  if (sel.value) detPlc = +sel.value;
  else if (!detPlc || !s.plcs.some(p => p.index === detPlc)) detPlc = s.plcs.length ? s.plcs[0].index : null;
  if (detPlc != null && sel.value !== String(detPlc)) sel.value = detPlc;
  return detPlc == null ? null : s.plcs.find(p => p.index === detPlc) || null;
}

function buildDetail(p) {
  document.getElementById('detByteOrder').textContent = p.registers.byteOrder;
  document.getElementById('detGrid').innerHTML =
    Array.from({ length: 16 }, (_, i) => `<div class="cell" id="det-cell-${i + 1}"></div>`).join('');

  const tr = (addr, key, name) => `<tr><td>${addr}</td><td data-r="${key}"></td><td>${name}</td></tr>`;
  // 严格按地址 0→49 从上到下排（0 编号、1 告警汇总、2~17 站口状态、18~33 站口告警码、34~49 站口可用）
  let html = tr(0, 0, 'Buffer 编号') + tr(1, 1, '告警汇总');
  for (let i = 0; i < 16; i++) html += tr(2 + i, 2 + i, `站口${i + 1} 状态`);
  for (let i = 0; i < 16; i++) html += tr(18 + i, 18 + i, `站口${i + 1} 告警码`);
  for (let i = 0; i < 16; i++) html += tr(34 + i, 34 + i, `站口${i + 1} 可用(0=在线 1=下线)`);
  document.getElementById('tbl-reg-status').innerHTML = '<tr><th>地址</th><th>值</th><th>含义</th></tr>' + html;

  html = tr(306, 306, '命令编号回显');
  for (let i = 0; i < 16; i++) html += tr(307 + i, 307 + i, `站口${i + 1} 命令回显`);
  html += tr(323, 323, '扫码站口号') + `<tr><td>324~339</td><td id="det-scan"></td><td>货物扫码号</td></tr>` + tr(340, 340, '握手(1=PLC请求)');
  document.getElementById('tbl-reg-echo').innerHTML = '<tr><th>地址</th><th>值</th><th>含义</th></tr>' + html;

  html = '<tr><th>站口</th><th>地址</th><th>载具ID（快照）</th><th>HEX（原始值）</th></tr>';
  for (let st = 1; st <= 16; st++)
    html += `<tr><td>站口${st}</td><td class="mono">${50 + (st - 1) * 16}~${65 + (st - 1) * 16}</td><td data-sid="${st}"></td><td class="mono" data-hid="${st}">—</td></tr>`;
  document.getElementById('tbl-reg-id').innerHTML = html;

  html = '<tr><th>地址</th><th>值</th><th>含义</th></tr>' + tr(400, 'c0', '命令编号（写入）');
  for (let st = 1; st <= 16; st++) html += tr(401 + st - 1, `c${st}`, `站口${st} 命令`);
  document.getElementById('tbl-reg-cmd').innerHTML = html;

  detBuilt = true;
}

function renderDetail(s) {
  const p = getDetPlc(s);
  if (!p) { document.getElementById('detStat').textContent = '（无 PLC 配置）'; return; }
  if (!detBuilt) buildDetail(p);

  // 统计行
  const st = p.stats;
  document.getElementById('detStat').innerHTML =
    `<span class="stat-line">轮询:${st.pollCount} 错误:${st.errorCount} 重连:${st.reconnectCount} 命令:${st.commandCount}/${st.commandFailCount}败 最后轮询:${fmtTime(st.lastPollAt)}` +
    (st.lastError ? ` <span class="fail-txt">最后错误:${st.lastError}</span>` : '') + `</span>`;

  // 16 站口网格（原地更新；色阶 + 图标 + 文字三者并用，颜色绝不单独表意）
  for (const x of p.stations) {
    const cell = document.getElementById('det-cell-' + x.station);
    if (!cell) continue;
    cell.className = 'cell st' + (x.state >= 5 ? 99 : x.state);
    cell.innerHTML = `站${x.station} · ${STATE_ICON[x.state] || '·'}${ST[x.state] || ('状态' + x.state)}<br>${esc(x.carrierId) || '—'}${x.truncated ? ' <span class="chip-trunc">截断</span>' : ''}` +
      (x.alarm ? ` <span class="chip-alarm">A${x.alarm}</span>` : '') +
      (x.avail ? '<span class="offline-mark">下线</span>' : '');
  }

  // 寄存器值表（原地更新，[data-r] 按地址填值）
  const r = p.registers;
  const v = {};
  v[0] = r.bufferNo; v[1] = r.alarmSummary;
  for (let i = 0; i < 16; i++) { v[2 + i] = p.stations[i].state; v[18 + i] = p.stations[i].alarm; v[34 + i] = p.stations[i].avail; }
  v[306] = r.echoNo;
  for (let i = 0; i < 16; i++) v[307 + i] = r.echoStation[i];
  v[323] = r.scanStation; v[340] = r.handshake;
  for (const td of document.querySelectorAll('#page-plcdetail [data-r]'))
    if (td.dataset.r in v) td.textContent = v[td.dataset.r];   // 仅快照键（c* 命令区按需读取的值不覆盖）
  document.getElementById('det-scan').textContent = r.scanCode || '—';

  // ID 区快照字符串（原地更新）
  for (const x of p.stations) {
    const td = document.querySelector(`#tbl-reg-id [data-sid="${x.station}"]`);
    if (td) td.innerHTML = x.carrierId ? `${esc(x.carrierId)}${x.truncated ? ' <span class="chip-trunc">截断</span>' : ''}` : '—';
  }
}

// ---------- ID 区 / 命令区 按需原始读取（/api/debug/regread） ----------
// 优先 125 字大段（Modbus 规范上限）；某段失败自动降级 16 字小片（轮询器读 ID 区的同尺寸，
// 现场验证过可用——兼容真 PLC 帧长限制小于 125 的情况）
async function regRead(plc, a, n) {
  const r = await fetch(`/api/debug/regread?plc=${plc}&addr=${a}&count=${n}`);
  if (!r.ok) return null;
  return (await r.json()).values;
}

async function readRegBlocks(plc, addr, total) {
  const vals = new Array(total);
  for (let a = addr; a < addr + total; a += 125) {
    const n = Math.min(125, addr + total - a);
    let d = await regRead(plc, a, n);
    if (d) { for (let i = 0; i < n; i++) vals[a - addr + i] = d[i]; continue; }
    let degraded = true;
    for (let s = a; s < a + n; s += 16) {
      const sn = Math.min(16, a + n - s);
      d = await regRead(plc, s, sn);
      if (!d) { degraded = false; break; }
      for (let i = 0; i < sn; i++) vals[s - addr + i] = d[i];
    }
    if (!degraded) throw new Error(`${addr}~${addr + total - 1} 段 ${a} 起读取失败（已尝试 16 字降级）`);
  }
  return vals;
}

async function readIdRaw() {
  if (detPlc == null) return;
  const box = document.getElementById('regIdRaw');
  box.innerHTML = '<span class="stat-line">读取中…</span>';
  try {
    const vals = await readRegBlocks(detPlc, 50, 256);
    const bo = document.getElementById('detByteOrder').textContent;
    for (let st = 1; st <= 16; st++) {
      const words = vals.slice((st - 1) * 16, st * 16);
      const td = document.querySelector(`#tbl-reg-id [data-hid="${st}"]`);
      if (td) td.innerHTML = `<div>${unpackAscii(words, bo) || '—'}</div><div class="mono">${words.map(hex4).join(' ')}</div>`;
    }
    box.innerHTML = `<span class="ok-txt">已读 50~305（256 字）</span>`;
  } catch (e) { box.innerHTML = `<span class="fail-txt">${e.message}</span>`; }
}

async function readCmdRaw() {
  if (detPlc == null) return;
  const box = document.getElementById('regCmdRaw');
  box.innerHTML = '<span class="stat-line">读取中…</span>';
  try {
    const vals = await readRegBlocks(detPlc, 400, 273);
    const bo = document.getElementById('detByteOrder').textContent;
    const c0 = document.querySelector('#tbl-reg-cmd [data-r="c0"]');
    if (c0) c0.textContent = vals[0];
    for (let st = 1; st <= 16; st++) {
      const td = document.querySelector(`#tbl-reg-cmd [data-r="c${st}"]`);
      if (td) td.textContent = vals[st];
    }
    let html = '';
    for (let st = 1; st <= 16; st++) {
      const words = vals.slice(17 + (st - 1) * 16, 17 + st * 16);
      html += `<div>站口${st}（${417 + (st - 1) * 16}~${432 + (st - 1) * 16}）: "${unpackAscii(words, bo) || '—'}" <span class="mono">${words.map(hex4).join(' ')}</span></div>`;
    }
    box.innerHTML = html;
  } catch (e) { box.innerHTML = `<span class="fail-txt">${e.message}</span>`; }
}
