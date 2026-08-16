// mcstest.js — MCS 联调页（导出: cimState, invRefresh, inventoryRequest, mcsPlcNum, mcsBo, mcsBuild, mcsRefresh, mcsWriteCell, mcsWriteId, mcsWriteScan, mcsTrafficKey, loadMcsTraffic, mcsToggleTraffic, mcsSetDir, mcsTogglePause, loadMcsEvents）
// 依赖: main.js 的 lastStatus；status.js 的 readRegBlocks；plctest.js 的 plctRegRead；protocol.js 的 unpackAscii/agtPackAscii

// ---------- MCS 联调页（虚拟 PLC 寄存器表 + HSMS 收发记录 + 最近事件） ----------
async function cimState(online) {
  const msg = document.getElementById('cimMsg');
  try {
    const r = await fetch('/api/debug/cim', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ online }) });
    const j = await r.json();
    msg.textContent = j.sent ? `已发 S6F11 CEID ${online ? 3 : 1}（${online ? 'OnlineRemote' : 'Offline'}）` : '发送失败（MCS 未连接？）';
  } catch (e) { msg.textContent = '请求失败: ' + e; }
}

async function invRefresh() {
  const box = document.getElementById('tbl-inv');
  try {
    const j = await (await fetch('/api/inventory')).json();
    const ss = j.stations || [];
    clearError(box);
    box.innerHTML =
      '<tr><th>设备号</th><th>位置号</th><th>单位状态</th><th>站口状态</th><th>载具ID</th><th>来源</th><th>报警码</th><th>数据更新时间</th><th>命令状态</th><th>命令类型</th><th>命令载具</th><th>命令时间</th><th>命令编号</th><th>命令来源</th></tr>' +
      (ss.length ? ss.map(x => `<tr>
        <td>${esc(x.deviceNo)}</td>
        <td>${esc(x.unitId)}</td>
        <td>${esc(x.unitStateLabel)}</td>
        <td>${esc(x.stationState)} ${esc(x.stateLabel)}</td>
        <td>${esc(x.carrierId) || '（空）'}</td>
        <td>${esc(x.installSource) || '—'}</td>
        <td>${esc(x.alarmCode) || '—'}</td>
        <td>${esc(x.updatedAt) || '—'}</td>
        <td>${esc(x.cmdStateLabel)}</td>
        <td>${x.cmdType === 1 ? '安装' : x.cmdType === 2 ? '取走' : '—'}</td>
        <td>${esc(x.cmdCarrierId) || '—'}</td>
        <td>${esc(x.cmdTime) || '—'}</td>
        <td>${esc(x.cmdSeq) || '—'}</td>
        <td>${esc(x.cmdSource) || '—'}</td>
      </tr>`).join('')
                 : '<tr><td colspan="14" class="empty-row">（无 PLC 配置）</td></tr>');
  } catch (e) { reportError(box, e); }
}

async function inventoryRequest() {
  const msg = document.getElementById('cimMsg');
  try {
    const r = await fetch('/api/debug/inventory-request', { method: 'POST' });
    const j = await r.json();
    msg.textContent = j.sent ? '已发 S6F11 CEID 502（InventoryUpdateRequest）' : '发送失败（MCS 未连接？）';
  } catch (e) { msg.textContent = '请求失败: ' + e; }
}
let mcsBuilt = false, mcsTrafficDir = '', mcsTrafficPaused = false;
let mcsExpanded = new Set();
let mcsLastIdRead = 0;

const mcsPlcNum = () => { const s = document.getElementById('mcsPlc'); return s && s.value ? +s.value : NaN; };
function mcsBo() {
  const p = lastStatus ? (lastStatus.plcs || []).find(x => x.index === mcsPlcNum()) : null;
  return p ? p.registers.byteOrder : 'high';
}

function mcsBuild() {
  const sel = document.getElementById('mcsPlc');
  const plcs = lastStatus ? lastStatus.plcs || [] : [];
  if (sel.options.length === 0)
    sel.innerHTML = plcs.map(p => `<option value="${p.index}">${p.index} 号 Buffer（${p.connected ? '在线' : '离线'}）</option>`).join('');
  if (!sel.value && plcs.length) sel.value = plcs[0].index;
  if (mcsBuilt) return;

  // 快速区 0~49（原始寄存器表：地址 | 值 | 说明，纯手改）
  let h = '<tr><th>地址</th><th>值</th><th>说明</th></tr>';
  const rows = [[0, 'Buffer 编号'], [1, '告警汇总']];
  for (let i = 0; i < 16; i++) rows.push([2 + i, `站口${i + 1} 状态（0空/1有货/2正放/3正取/4故障/5人工有货）`]);
  for (let i = 0; i < 16; i++) rows.push([18 + i, `站口${i + 1} 告警码（0=无告警）`]);
  for (let i = 0; i < 16; i++) rows.push([34 + i, `站口${i + 1} 可用（0=在服/1=停服）`]);
  h += rows.map(([a, d]) =>
    `<tr><td class="mono">${a}</td><td><input data-m="${a}" size="6" data-enter="blur" data-action="mcs-write-cell"></td><td>${d}</td></tr>`).join('');
  document.getElementById('tbl-mcs-fast').innerHTML = h;

  // ID 区 50~305：文本输入（agtPackAscii 16 字写回）
  h = '<tr><th>站口</th><th>地址</th><th>货物 ID（≤32 字符，回车写回）</th></tr>';
  for (let st = 1; st <= 16; st++)
    h += `<tr><td>站口${st}</td><td class="mono">${50 + (st - 1) * 16}~${65 + (st - 1) * 16}</td>` +
      `<td><input data-mid="${st}" size="36" data-enter="blur" data-action="mcs-write-id"></td></tr>`;
  document.getElementById('tbl-mcs-id').innerHTML = h;

  // 扫码/握手
  h = '<tr><th>地址</th><th>值</th><th>说明</th></tr>' +
    `<tr><td class="mono">323</td><td><input data-m="323" size="6" data-enter="blur" data-action="mcs-write-cell"></td><td>扫码站口号（1~16）</td></tr>` +
    `<tr><td class="mono">324~339</td><td><input id="mcsScanCode" size="36" data-enter="blur" data-action="mcs-write-scan"></td><td>扫码号文本（≤32 字符；BCR NG 用 UNK- 前缀）</td></tr>` +
    `<tr><td class="mono">340</td><td><input data-m="340" size="6" data-enter="blur" data-action="mcs-write-cell"></td><td>握手：写 1 = PLC 请求扫码（BufferC 自动应答 0 → 501+201）</td></tr>`;
  document.getElementById('tbl-mcs-scan').innerHTML = h;

  // 只读：回显 + 当前 400
  h = '<tr><th>地址</th><th>值</th><th>说明</th></tr>' +
    `<tr><td class="mono">306</td><td data-r="306"></td><td>命令编号回显</td></tr>`;
  for (let i = 0; i < 16; i++)
    h += `<tr><td class="mono">${307 + i}</td><td data-r="${307 + i}"></td><td>站口${i + 1} 命令回显</td></tr>`;
  h += `<tr><td class="mono">400</td><td id="mcsCur400">—</td><td>命令编号（当前值，401~672 见 PLC 详情页）</td></tr>`;
  document.getElementById('tbl-mcs-echo').innerHTML = h;

  mcsBuilt = true;
}

async function mcsRefresh(s) {
  const plc = mcsPlcNum();
  const p = s ? (s.plcs || []).find(x => x.index === plc) : null;
  if (p) {
    // 快速区值 = 轮询快照（状态/告警/可用/回显/握手全在快照里，1.5s 一次）；编辑中的格子不覆盖
    const v = {};
    v[0] = p.registers.bufferNo; v[1] = p.registers.alarmSummary;
    for (let i = 0; i < 16; i++) { v[2 + i] = p.stations[i].state; v[18 + i] = p.stations[i].alarm; v[34 + i] = p.stations[i].avail; }
    v[306] = p.registers.echoNo;
    for (let i = 0; i < 16; i++) v[307 + i] = p.registers.echoStation[i];
    v[323] = p.registers.scanStation; v[340] = p.registers.handshake;
    for (const el of document.querySelectorAll('#tbl-mcs-fast [data-m], #tbl-mcs-scan [data-m]')) {
      if (document.activeElement === el) continue;
      const val = v[+el.dataset.m];
      if (val !== undefined && +el.value !== val) el.value = val;
    }
    const sc = document.getElementById('mcsScanCode');
    if (sc && document.activeElement !== sc && p.registers.scanCode && sc.value !== p.registers.scanCode)
      sc.value = p.registers.scanCode;
    for (const td of document.querySelectorAll('#tbl-mcs-echo [data-r]'))
      td.textContent = v[+td.dataset.r] !== undefined ? v[+td.dataset.r] : '—';
  }
  if (isNaN(plc)) return;
  // ID 区：快照仅状态变化时重读（会陈旧）→ 3s 节流直接 regread 假 PLC 实况
  if (mcsLastIdRead + 3000 <= Date.now()) {
    mcsLastIdRead = Date.now();
    try {
      const vals = await readRegBlocks(plc, 50, 256);
      clearError(document.getElementById('tbl-mcs-id'));
      for (let st = 1; st <= 16; st++) {
        const el = document.querySelector(`#tbl-mcs-id [data-mid="${st}"]`);
        if (!el || document.activeElement === el) continue;
        const cur = unpackAscii(vals.slice((st - 1) * 16, st * 16), mcsBo());
        if (el.value !== cur) el.value = cur;
      }
    } catch (e) { reportError(document.getElementById('tbl-mcs-id'), e); }
  }
  const cur = await plctRegRead(plc, 400, 1);
  const c400 = document.getElementById('mcsCur400');
  if (c400 && cur) c400.textContent = cur[0];
}

async function mcsWriteCell(el) {
  const plc = mcsPlcNum();
  const addr = +el.dataset.m;
  const val = parseInt(el.value.trim(), 10);
  if (isNaN(plc) || isNaN(val) || val < 0 || val > 65535) { el.classList.add('invalid'); return; }
  el.classList.remove('invalid');
  const r = await fetch('/api/debug/regwrite', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ plc, addr, values: [val] }) });
  if (!r.ok) el.classList.add('invalid');
}

async function mcsWriteId(el) {
  const plc = mcsPlcNum();
  const st = +el.dataset.mid;
  const id = el.value.trim();
  if (isNaN(plc) || id.length > 32) { el.classList.add('invalid'); return; }
  el.classList.remove('invalid');
  const r = await fetch('/api/debug/regwrite', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ plc, addr: 50 + (st - 1) * 16, values: agtPackAscii(id, mcsBo()) }) });
  if (!r.ok) el.classList.add('invalid');
}

async function mcsWriteScan(el) {
  const plc = mcsPlcNum();
  const st = parseInt(document.querySelector('#tbl-mcs-scan [data-m="323"]').value, 10);
  if (isNaN(plc) || isNaN(st) || st < 1 || st > 16 || el.value.trim().length > 32) { el.classList.add('invalid'); return; }
  el.classList.remove('invalid');
  // 先写码 324~339，再写站口 323（340 由用户单独写 1 触发）
  const r1 = await fetch('/api/debug/regwrite', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ plc, addr: 324, values: agtPackAscii(el.value.trim(), mcsBo()) }) });
  const r2 = await fetch('/api/debug/regwrite', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ plc, addr: 323, values: [st] }) });
  if (!r1.ok || !r2.ok) el.classList.add('invalid');
}

function mcsTrafficKey(x) { return `${x.time}|${x.name}|${x.systemBytes}|${x.direction}`; }
async function loadMcsTraffic() {
  if (mcsTrafficPaused) return;
  const box = document.getElementById('mcsTrafficList');
  try {
    const d = await (await fetch('/api/hsms/traffic?tail=50')).json();
    const t = (d.traffic || []).filter(x => !mcsTrafficDir || x.direction === mcsTrafficDir);
    clearError(box);
    box.innerHTML = t.map(x => {
      const k = mcsTrafficKey(x), open = mcsExpanded.has(k);
      return `<div class="agvc-entry"><div>` +
        `<span class="t">${fmtTime(x.time)}</span> ` +
        `<span class="dir ${x.direction === '收' ? 'dir-in' : 'dir-out'}">${x.direction}</span> ` +
        `<b>${esc(x.name)}</b> sys=${x.systemBytes}${x.wBit ? ' W' : ''} ` +
        `<button data-action="mcs-toggle-traffic" data-arg="${k.replace(/"/g, '&quot;')}">${open ? '收起' : '数据'}</button></div>` +
        (open ? `<pre class="mono">${esc(x.sml)}</pre><pre class="mono">HEX ${esc(x.hex)}</pre>` : '') +
        `</div>`;
    }).reverse().join('');
    mcsExpanded = new Set([...mcsExpanded].filter(k => t.some(x => mcsTrafficKey(x) === k)));
  } catch (e) { reportError(box, e); }
}
function mcsToggleTraffic(k) { mcsExpanded.has(k) ? mcsExpanded.delete(k) : mcsExpanded.add(k); loadMcsTraffic(); }
function mcsSetDir(d, btn) {
  mcsTrafficDir = d;
  btn.parentElement.querySelectorAll('button').forEach(b => b.classList.remove('active'));
  btn.classList.add('active');
  loadMcsTraffic();
}
function mcsTogglePause(btn) {
  mcsTrafficPaused = !mcsTrafficPaused;
  btn.textContent = mcsTrafficPaused ? '继续' : '暂停';
  if (!mcsTrafficPaused) loadMcsTraffic();
}

async function loadMcsEvents() {
  const box = document.getElementById('tbl-mcsevents');
  try {
    const ev = await (await fetch('/api/events?tail=20')).json();
    clearError(box);
    box.innerHTML =
      '<tr><th>时间</th><th>CEID</th><th>描述</th></tr>' +
      (ev.length ? ev.map(e => `<tr><td>${fmtTime(e.time)}</td><td>${e.ceid}</td><td>${esc(e.description)}</td></tr>`).join('')
        : '<tr><td colspan="3" class="empty-row">暂无事件</td></tr>');
  } catch (e) { reportError(box, e); }
}
