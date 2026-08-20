// status.js — 状态轮询 + 总览 + PLC 详情（导出: loadStatus, renderStatusBar, renderOverview, fillPlcSelects, detPlcChanged, getDetPlc, buildDetail, renderDetail, regRead, readRegBlocks, readIdRaw）
// 依赖: main.js 的 lastStatus/curPage/overviewKey/detPlc/detBuilt；protocol.js 的 unpackAscii/hex4

// ---------- 状态轮询（常开：状态栏 + 当前页共用） ----------
async function loadStatus() {
  try {
    const res = await apiGet('/api/status');
    if (!res.fresh) return;   // 过期响应丢弃
    const s = res.data;
    lastStatus = s;
    fillPlcSelects(s.plcs);
    renderStatusBar(s);
    if (typeof screenMode !== 'undefined' && screenMode) renderScreenDash(s);
    if (curPage === 'overview') renderOverview(s);
    if (curPage === 'plcdetail') renderDetail(s);
    if (curPage === 'plctest') { renderPlctEcho(); renderPlctReg(); }
    if (curPage === 'mcstest') { mcsBuild(); mcsRefresh(s); }
  } catch (e) {
    document.getElementById('statusbar').innerHTML = `<span class="badge b-err">加载失败: ${esc(e)}</span>`;
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
    tile('字节序', esc(sys.byteOrderSummary || '—')) +
    `<button class="bar-btn" data-action="enter-screen">大屏</button>`;
}

// ---------- 总览：15 台紧凑表（首建 + 原地更新） ----------
function renderOverview(s) {
  const tbody = document.querySelector('#tbl-overview tbody');
  const key = s.plcs.map(p => p.index).join(',');
  if (key !== overviewKey) {
    overviewKey = key;
    tbody.innerHTML = s.plcs.map(p => `
      <tr id="ov-${p.index}">
        <td>${p.name || p.index + ' 号 Buffer'}</td>
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
    row.querySelector('[data-k=occ]').textContent = `${occ}/${p.stations.length}`;
    row.querySelector('[data-k=poll]').textContent = st.pollCount;
    row.querySelector('[data-k=err]').innerHTML = st.errorCount ? `<span class="fail-txt">${st.errorCount}</span>` : '0';
    row.querySelector('[data-k=rc]').textContent = st.reconnectCount;
    row.querySelector('[data-k=cmd]').textContent = `${st.commandCount}(${st.commandFailCount})`;
  }
}

// 站口状态图标（与 ST 状态名配对；色阶 + 图标 + 文字三者并用）
const STATE_ICON = { 0: '◌', 1: '●', 2: '↓', 3: '↑', 4: '·', 5: '✋' };

// ---------- PLC 详情：单台 16 站口 + 完整寄存器视图（原地更新防缩回） ----------
let detBuiltPlc = null;   // Q5：建表对应的 PLC index

function fillPlcSelects(plcs) {
  // PLC 详情页选择器（下拉）；调试面板为手填输入（dbgPlcNum 解析）
  const sel = document.getElementById('detPlc');
  const cur = sel.value;
  sel.innerHTML = plcs.map(p =>
    `<option value="${p.index}" ${String(p.index) === cur ? 'selected' : ''}>${p.name || p.index + ' 号 Buffer'}（${p.connected ? '在线' : '离线'}）</option>`).join('');
  if (!sel.value && plcs.length) sel.value = plcs[0].index;
  // 单机测试页寄存器视图的独立 PLC 下拉（Q5：与命令表单互不影响；1.5s 重填保留选中项）
  const regSel = document.getElementById('plctRegPlc');
  if (regSel) {
    const cur2 = regSel.value;
    regSel.innerHTML = plcs.map(p =>
      `<option value="${p.index}" ${String(p.index) === cur2 ? 'selected' : ''}>${p.name || p.index + ' 号 Buffer'}（${p.connected ? '在线' : '离线'}）</option>`).join('');
    if (!cur2 && plcs.length) regSel.value = plcs[0].index;
  }
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
  const stCnt = p.stations.length;   // 该机台逻辑站口数（8/16）
  document.getElementById('detByteOrder').textContent = p.registers.byteOrder;
  document.getElementById('detGrid').innerHTML =
    Array.from({ length: stCnt }, (_, i) => `<div class="cell" id="det-cell-${i + 1}"></div>`).join('');

  // 状态区：严格按地址 0→49 从上到下排（0 编号、1 告警汇总、2~17 站口状态、18~33 站口告警码、34~49 站口可用）。
  // 2026-08-20 映射口径：站口标签=逻辑号、地址=物理（physOf）；data-r 键=物理地址（fillRegTables 按快照值填）
  const addrRow = (addr, key, name) => ({ cells: [{ t: 'text', v: addr }, { t: 'r', k: key }, { t: 'text', v: name }] });
  const statusRows = [addrRow(0, 0, 'Buffer 编号'), addrRow(1, 1, '告警汇总')];
  for (let L = 1; L <= stCnt; L++) { const p = physOf(L, stCnt); statusRows.push(addrRow(2 + p - 1, 2 + p - 1, `站口${L}(物理${p}) 状态`)); }
  for (let L = 1; L <= stCnt; L++) { const p = physOf(L, stCnt); statusRows.push(addrRow(18 + p - 1, 18 + p - 1, `站口${L}(物理${p}) 告警码`)); }
  for (let L = 1; L <= stCnt; L++) { const p = physOf(L, stCnt); statusRows.push(addrRow(34 + p - 1, 34 + p - 1, `站口${L}(物理${p}) 可用(0=在线 1=下线)`)); }
  buildRegTable(document.getElementById('tbl-reg-status'), { cols: ['地址', '值', '含义'], rows: statusRows });

  const echoRows = [addrRow(306, 306, '命令编号回显')];
  for (let L = 1; L <= stCnt; L++) { const p = physOf(L, stCnt); echoRows.push(addrRow(307 + p - 1, 307 + p - 1, `站口${L}(物理${p}) 命令回显`)); }
  echoRows.push(addrRow(323, 323, '扫码站口号（物理）'));
  echoRows.push({ cells: [{ t: 'text', v: '324~339' }, { t: 'id', k: 'det-scan' }, { t: 'text', v: '货物扫码号' }] });
  echoRows.push(addrRow(340, 340, '握手(1=PLC请求)'));
  buildRegTable(document.getElementById('tbl-reg-echo'), { cols: ['地址', '值', '含义'], rows: echoRows });

  const idRows = [];
  for (let L = 1; L <= stCnt; L++) {
    const p = physOf(L, stCnt);
    idRows.push({ cells: [{ t: 'text', v: `站口${L}(物理${p})` }, { t: 'raw', attrs: 'class="mono"', v: `${50 + (p - 1) * 16}~${65 + (p - 1) * 16}` }, { t: 'sid', k: L }, { t: 'hid', k: L }] });
  }
  buildRegTable(document.getElementById('tbl-reg-id'), { cols: ['站口', '地址', '载具ID（快照）', 'HEX（原始值）'], rows: idRows });

  // 命令区 400~416（按需读取，data-r 键为物理地址——fillRegTables 快照键只到 340，不会覆盖）
  // 行按逻辑站排（与状态区/ID 区一致；2026-08-20 用户口径）
  const cmdRows = [addrRow(400, 400, '命令编号（触发）')];
  for (let L = 1; L <= stCnt; L++) { const p = physOf(L, stCnt); cmdRows.push(addrRow(401 + p - 1, 401 + p - 1, `站口${L}(物理${p}) 操作命令码（1=写入ID 2=清除）`)); }
  buildRegTable(document.getElementById('tbl-reg-cmd'), { cols: ['地址', '值', '含义'], rows: cmdRows });

  detBuilt = true;
  detBuiltPlc = p.index;   // Q5：记录建表对应的 PLC（8/16 站行数不同，换 PLC 才重建表）
}

function renderDetail(s) {
  const p = getDetPlc(s);
  if (!p) { document.getElementById('detStat').textContent = '（无 PLC 配置）'; return; }
  if (!detBuilt || detBuiltPlc !== p.index) buildDetail(p);

  // 统计行
  const st = p.stats;
  document.getElementById('detStat').innerHTML =
    `<span class="stat-line">轮询:${st.pollCount} 错误:${st.errorCount} 重连:${st.reconnectCount} 命令:${st.commandCount}/${st.commandFailCount}败 最后轮询:${fmtTime(st.lastPollAt)}` +
    (st.lastError ? ` <span class="fail-txt">最后错误:${st.lastError}</span>` : '') + `</span>`;

  // 16 站口网格（原地更新；色阶 + 图标 + 文字三者并用，颜色绝不单独表意）
  // 2026-08-20 映射口径：显示「站L(物理p)」并列
  for (const x of p.stations) {
    const cell = document.getElementById('det-cell-' + x.station);
    if (!cell) continue;
    const phys = physOf(x.station, p.stations.length);
    // 载具确认机制（2026-08-19）：中间态站口（有货无 ID、pending 等待中）黄色闪烁 + 点击补填
    const pend = (typeof pendingRows !== 'undefined' ? pendingRows : []).find(r => r.plcIndex === p.index && r.station === x.station);
    const isPend = !!(pend && (x.state === 1 || x.state === 5));
    cell.className = 'cell st' + (x.state >= 5 ? 99 : x.state) + (isPend ? ' blink' : '');
    cell.dataset.action = isPend ? 'fill-open' : '';
    cell.dataset.arg = isPend ? `${p.index}:${x.station}` : '';
    cell.innerHTML = `站${x.station}(物理${phys}) · ${STATE_ICON[x.state] || '·'}${ST[x.state] || ('状态' + x.state)}<br>${esc(x.carrierId) || '—'}${x.truncated ? ' <span class="chip-trunc">截断</span>' : ''}` +
      (isPend ? ` <span class="chip-pending">等待ID(${pend.pendingCeid})</span>` : '') +
      (x.alarm ? ` <span class="chip-alarm">A${x.alarm}</span>` : '') +
      (x.avail ? '<span class="offline-mark">下线</span>' : '');
  }

  fillRegTables(p);
}

// 寄存器值表原地更新（[data-r]/[data-sid] 按地址填值；表在 PLC 单机测试页，设备详情页与单机测试页共用）
// 2026-08-20 映射口径：data-r 键=物理地址，快照数组=逻辑索引 → 按 physOf 换算后填值
function fillRegTables(p) {
  const r = p.registers;
  const stCnt = p.stations.length;   // 该机台逻辑站口数（8/16）
  const v = {};
  v[0] = r.bufferNo; v[1] = r.alarmSummary;
  for (let i = 0; i < stCnt; i++) {
    const phys = physOf(i + 1, stCnt);
    v[2 + phys - 1] = p.stations[i].state;
    v[18 + phys - 1] = p.stations[i].alarm;
    v[34 + phys - 1] = p.stations[i].avail;
  }
  v[306] = r.echoNo;
  for (let i = 0; i < stCnt; i++) { const phys = physOf(i + 1, stCnt); v[307 + phys - 1] = r.echoStation[i]; }
  v[323] = r.scanStation; v[340] = r.handshake;
  for (const td of document.querySelectorAll('#page-plctest [data-r], #page-plcdetail [data-r]'))
    if (td.dataset.r in v) td.textContent = v[td.dataset.r];   // 仅快照键（c* 命令区按需读取的值不覆盖）
  const scan = document.getElementById('det-scan');
  if (scan) scan.textContent = r.scanCode || '—';

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
  // &t= 防浏览器缓存：同 URL 重复 GET 可能命中缓存导致「读到旧值」（2026-08-19 现场：PLC 已清区仍显示旧 ID）
  const r = await fetch(`/api/debug/regread?plc=${plc}&addr=${a}&count=${n}&t=${Date.now()}`);
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

// 目标 PLC：优先取寄存器视图独立下拉（Q5），否则退回设备详情页选择
function regTargetPlc() {
  const regSel = document.getElementById('plctRegPlc');
  if (regSel && regSel.value) return +regSel.value;
  return detPlc;
}

async function readIdRaw() {
  const plc = regTargetPlc();
  if (plc == null) return;
  const box = document.getElementById('regIdRaw');
  box.innerHTML = '<span class="stat-line">读取中…</span>';
  const stCnt = document.querySelectorAll('#tbl-reg-id [data-hid]').length;   // 该机台逻辑站口数
  try {
    // 单次大段读（后端持锁连续分段，不被轮询插队）；真 PLC 帧长受限时自动降级 125→16 小片
    let vals = await regRead(plc, 50, 256);
    if (!vals) vals = await readRegBlocks(plc, 50, 256);
    const bo = document.getElementById('detByteOrder').textContent;
    for (let st = 1; st <= stCnt; st++) {
      const words = vals.slice((st - 1) * 16, st * 16);
      const td = document.querySelector(`#tbl-reg-id [data-hid="${st}"]`);
      if (td) td.innerHTML = `<div>${unpackAscii(words, bo) || '—'}</div><div class="mono">${words.map(hex4).join(' ')}</div>`;
    }
    box.innerHTML = `<span class="ok-txt">已读 50~305（256 字）@ ${new Date().toLocaleTimeString('zh-CN', { hour12: false })}</span>`;
  } catch (e) {
    // 读失败：清空 HEX 单元格——失败绝不静默保留旧值（旧值会被误读为「PLC 还有数据」）
    for (let st = 1; st <= stCnt; st++) {
      const td = document.querySelector(`#tbl-reg-id [data-hid="${st}"]`);
      if (td) td.innerHTML = '—';
    }
    box.innerHTML = `<span class="fail-txt">${e.message}</span>`;
  }
}

// 命令区 400~416 按需读取（写区：只读前 17 字，失败自动降级 16 字小片——真 PLC 长读会掉线，2026-08-19 现场口径）
async function readCmdRaw() {
  const plc = regTargetPlc();
  if (plc == null) return;
  const box = document.getElementById('regCmdRaw');
  box.innerHTML = '<span class="stat-line">读取中…</span>';
  try {
    let vals = await regRead(plc, 400, 17);
    if (!vals) vals = await readRegBlocks(plc, 400, 17);
    if (!vals || vals.length < 17) throw new Error('400~416 读取失败（PLC 未连接或该区禁读）');
    for (let a = 400; a <= 416; a++) {
      const td = document.querySelector(`#tbl-reg-cmd [data-r="${a}"]`);
      if (td) td.textContent = vals[a - 400];
    }
    box.innerHTML = `<span class="ok-txt">已读 400~416（17 字）@ ${new Date().toLocaleTimeString('zh-CN', { hour12: false })}</span>`;
  } catch (e) {
    // 读失败：清空命令区单元格——失败绝不静默保留旧值（与 ID 区同口径）
    for (let a = 400; a <= 416; a++) {
      const td = document.querySelector(`#tbl-reg-cmd [data-r="${a}"]`);
      if (td) td.textContent = '—';
    }
    box.innerHTML = `<span class="fail-txt">${e.message}</span>`;
  }
}

