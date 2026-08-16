// cmds.js — 手动命令 + PLC 调试面板（导出: askConfirm, doSendCmd, ack, dbgAdd, dbgReadAt, dbgPlcNum, dbgStNum, dbgAskCmd, dbgAskWrite, doSendDbg）
// 依赖: main.js 的 pendingCmd/pendingDbg

// ---------- 手动命令（业务路径，二次确认） ----------
function askConfirm() {
  const type = document.getElementById('cmdType').value;
  const loc = document.getElementById('loc').value.trim();
  const id = document.getElementById('carrierId').value.trim();
  if (!loc || (type === 'install' && !id)) { document.getElementById('cmdMsg').textContent = '请填写位置' + (type === 'install' ? '和载具 ID' : ''); return; }
  pendingCmd = { cmd: type, carrierId: id, carrierLoc: loc };
  document.getElementById('confirmText').innerHTML =
    `<b>${type === 'install' ? '安装' : '移除'}</b>：<br>位置 <b>${esc(loc)}</b><br>载具 <b>${esc(id) || '（按位置移除）'}</b>`;
  document.getElementById('confirmBox').style.display = 'flex';
}
async function doSendCmd(confirm) {
  document.getElementById('confirmBox').style.display = 'none';
  if (!confirm) { pendingCmd = null; pendingDbg = null; return; }
  if (pendingDbg) { await doSendDbg(pendingDbg); pendingDbg = null; return; }
  if (!pendingCmd) return;
  const r = await fetch('/api/command', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(pendingCmd) });
  document.getElementById('cmdMsg').textContent = r.ok ? '命令已执行' : '执行失败（检查位置/载具 ID）';
  pendingCmd = null;
}
async function ack(id) { await fetch('/api/alarms/' + id + '/ack', { method: 'POST' }); loadActivity(); }

// ---------- PLC 调试面板（命令直发 + 寄存器直读直写，绕过业务） ----------
function dbgAdd(msg) {
  const box = document.getElementById('dbgResult');
  box.insertAdjacentHTML('beforeend',
    `<div><span class="t">${new Date().toLocaleTimeString('zh-CN', { hour12: false })}</span> ${msg}</div>`);
  box.scrollTop = box.scrollHeight;
  while (box.children.length > 200) box.removeChild(box.firstChild);
}
async function dbgReadAt(plc, addr, count) {
  const r = await fetch(`/api/debug/regread?plc=${plc}&addr=${addr}&count=${count}`);
  if (!r.ok) { dbgAdd(`PLC${plc} 读 ${addr} 起 ${count} 字失败（离线/参数错误）`); return; }
  const d = await r.json();
  dbgAdd(`PLC${d.plc} 读 ${d.addr} 起 ${d.values.length} 字: ` +
    d.values.map((v, i) => `${d.addr + i}=${v}(0x${v.toString(16).padStart(4, '0')})`).join(' '));
}
// 调试面板手填输入解析：PLC 取第一个数字（支持 "1" / "1号Buffer"），站口取数字
function dbgPlcNum() {
  const m = document.getElementById('dbgPlc').value.trim().match(/\d+/);
  return m ? +m[0] : NaN;
}
function dbgStNum() { return parseInt(document.getElementById('dbgStation').value.trim(), 10); }

function dbgAskCmd() {
  const plc = dbgPlcNum();
  const st = dbgStNum();
  const cmd = parseInt(document.getElementById('dbgCmd').value.trim(), 10);
  const id = document.getElementById('dbgCarrierId').value.trim();
  const msg = document.getElementById('dbgMsg');
  if (isNaN(plc)) { msg.textContent = '请填写 PLC 编号（如 1 / 1号Buffer）'; return; }
  if (isNaN(st) || st < 1 || st > 16) { msg.textContent = '站口需为 1~16'; return; }
  if (isNaN(cmd) || cmd < 0 || cmd > 65535) { msg.textContent = '命令码需为 0~65535 的数字'; return; }
  if (cmd === 1 && !id) { msg.textContent = '命令 1 需填写载具 ID'; return; }
  msg.textContent = '';
  pendingDbg = { kind: 'cmd', plc: plc, station: st, cmd: cmd, carrierId: id };
  document.getElementById('confirmText').innerHTML =
    `<b>PLC 调试直发</b>：<br>PLC${plc} 站口 ${st} 命令 ${cmd}${cmd === 1 ? `<br>载具 <b>${esc(id)}</b>` : ''}`;
  document.getElementById('confirmBox').style.display = 'flex';
}
function dbgAskWrite() {
  const plc = dbgPlcNum();
  const addr = document.getElementById('dbgWAddr').value.trim();
  const vals = document.getElementById('dbgWVal').value.trim();
  if (isNaN(plc) || addr === '' || vals === '') { dbgAdd('写寄存器需 PLC 编号+地址+值'); return; }
  const v = vals.split(',').map(x => parseInt(x.trim(), 10));
  if (v.some(isNaN)) { dbgAdd('值必须为数字（逗号分隔）'); return; }
  pendingDbg = { kind: 'write', plc: plc, addr: +addr, values: v };
  document.getElementById('confirmText').innerHTML =
    `<b>⚠ 直写 PLC 寄存器</b>：<br>PLC${plc} 起始地址 ${addr}，共 ${v.length} 字<br>值 ${v.join(',')}`;
  document.getElementById('confirmBox').style.display = 'flex';
}
async function doSendDbg(d) {
  try {
    if (d.kind === 'cmd') {
      const r = await fetch('/api/debug/cmd', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ plc: d.plc, station: d.station, cmd: d.cmd, carrierId: d.carrierId }) });
      const j = await r.json();
      document.getElementById('dbgMsg').textContent = r.ok && j.ok ? `命令已执行 OK（${j.elapsedMs}ms）` : '命令执行失败（见结果区）';
      dbgAdd(`下发 PLC${d.plc} 站口${d.station} 命令${d.cmd} ${esc(d.carrierId) || ''} → ${r.ok && j.ok ? 'OK' : 'FAIL'}（${j.elapsedMs}ms）`);
      if (r.ok) await dbgReadAt(d.plc, 306, 17);   // 读回显区核对（306 编号 + 307~322 站口回显）
    } else if (d.kind === 'write') {
      const r = await fetch('/api/debug/regwrite', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ plc: d.plc, addr: d.addr, values: d.values }) });
      dbgAdd(`写 PLC${d.plc} 起始 ${d.addr} 共 ${d.values.length} 字 ${d.values.join(',')} → ${r.ok ? 'OK' : 'FAIL'}`);
      if (r.ok) await dbgReadAt(d.plc, d.addr, d.values.length);
    }
  } catch (e) { dbgAdd(`执行失败: ${e}`); }
}
