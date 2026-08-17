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
  const box = document.getElementById('tbl-inv-body');
  const wrap = document.getElementById('invWrap');
  try {
    const res = await apiGet('/api/inventory');
    if (!res.fresh) return;   // 过期响应丢弃
    const ss = res.data.stations || [];
    clearError(wrap);
    box.innerHTML =
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
  } catch (e) { reportError(wrap, e); }
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
  const fastRows = [{ cells: [{ t: 'raw', attrs: 'class="mono"', v: 0 }, { t: 'm', k: 0 }, { t: 'text', v: 'Buffer 编号' }] },
                    { cells: [{ t: 'raw', attrs: 'class="mono"', v: 1 }, { t: 'm', k: 1 }, { t: 'text', v: '告警汇总' }] }];
  for (let i = 0; i < 16; i++) fastRows.push({ cells: [{ t: 'raw', attrs: 'class="mono"', v: 2 + i }, { t: 'm', k: 2 + i }, { t: 'text', v: `站口${i + 1} 状态（0空/1有货/2正放/3正取/4故障/5人工有货）` }] });
  for (let i = 0; i < 16; i++) fastRows.push({ cells: [{ t: 'raw', attrs: 'class="mono"', v: 18 + i }, { t: 'm', k: 18 + i }, { t: 'text', v: `站口${i + 1} 告警码（0=无告警）` }] });
  for (let i = 0; i < 16; i++) fastRows.push({ cells: [{ t: 'raw', attrs: 'class="mono"', v: 34 + i }, { t: 'm', k: 34 + i }, { t: 'text', v: `站口${i + 1} 可用（0=在服/1=停服）` }] });
  buildRegTable(document.getElementById('tbl-mcs-fast'), { cols: ['地址', '值', '说明'], rows: fastRows });

  // ID 区 50~305：文本输入（agtPackAscii 16 字写回）
  const idRows = [];
  for (let st = 1; st <= 16; st++)
    idRows.push({ cells: [{ t: 'text', v: `站口${st}` }, { t: 'raw', attrs: 'class="mono"', v: `${50 + (st - 1) * 16}~${65 + (st - 1) * 16}` }, { t: 'mid', k: st }] });
  buildRegTable(document.getElementById('tbl-mcs-id'), { cols: ['站口', '地址', '货物 ID（≤32 字符，回车写回）'], rows: idRows });

  // 扫码/握手
  const scanRows = [
    { cells: [{ t: 'raw', attrs: 'class="mono"', v: 323 }, { t: 'm', k: 323 }, { t: 'text', v: '扫码站口号（1~16）' }] },
    { cells: [{ t: 'raw', attrs: 'class="mono"', v: '324~339' }, { t: 'scan' }, { t: 'text', v: '扫码号文本（≤32 字符；BCR NG 用 UNK- 前缀）' }] },
    { cells: [{ t: 'raw', attrs: 'class="mono"', v: 340 }, { t: 'm', k: 340 }, { t: 'text', v: '握手：写 1 = PLC 请求扫码（BufferC 自动应答 0 → 501+201）' }] },
  ];
  buildRegTable(document.getElementById('tbl-mcs-scan'), { cols: ['地址', '值', '说明'], rows: scanRows });

  // 只读：回显 + 当前 400
  const echoRows = [{ cells: [{ t: 'raw', attrs: 'class="mono"', v: 306 }, { t: 'r', k: 306 }, { t: 'text', v: '命令编号回显' }] }];
  for (let i = 0; i < 16; i++)
    echoRows.push({ cells: [{ t: 'raw', attrs: 'class="mono"', v: 307 + i }, { t: 'r', k: 307 + i }, { t: 'text', v: `站口${i + 1} 命令回显` }] });
  echoRows.push({ cells: [{ t: 'raw', attrs: 'class="mono"', v: 400 }, { t: 'id', k: 'mcsCur400', v: '—' }, { t: 'text', v: '命令编号（当前值，401~672 见 PLC 详情页）' }] });
  buildRegTable(document.getElementById('tbl-mcs-echo'), { cols: ['地址', '值', '说明'], rows: echoRows });

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
      // 单次大段读（后端持锁连续分段）；帧长受限自动降级
      let vals = await regRead(plc, 50, 256);
      if (!vals) vals = await readRegBlocks(plc, 50, 256);
      clearError(document.getElementById('tbl-mcs-id'));
      for (let st = 1; st <= 16; st++) {
        const el = document.querySelector(`#tbl-mcs-id [data-mid="${st}"]`);
        if (!el || document.activeElement === el) continue;
        const cur = unpackAscii(vals.slice((st - 1) * 16, st * 16), mcsBo());
        if (el.value !== cur) el.value = cur;
      }
    } catch (e) { reportError(document.getElementById('tbl-mcs-id'), e); }
  }
  // 当前 400 值 → 用 306 回显（命令区为写区禁读，实测读会掉线）
  const c400 = document.getElementById('mcsCur400');
  if (c400 && p) c400.textContent = p.registers.echoNo;
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
    const res = await apiGet('/api/hsms/traffic?tail=50');
    if (!res.fresh) return;   // 过期响应丢弃
    const t = (res.data.traffic || []).filter(x => !mcsTrafficDir || x.direction === mcsTrafficDir);
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
    const res = await apiGet('/api/events?tail=20');
    if (!res.fresh) return;   // 过期响应丢弃
    const ev = res.data;
    clearError(box);
    box.innerHTML =
      '<tr><th>时间</th><th>CEID</th><th>描述</th></tr>' +
      (ev.length ? ev.map(e => `<tr><td>${fmtTime(e.time)}</td><td>${e.ceid}</td><td>${esc(e.description)}</td></tr>`).join('')
        : '<tr><td colspan="3" class="empty-row">暂无事件</td></tr>');
  } catch (e) { reportError(box, e); }
}
