// plctest.js — PLC 单机测试页（导出: plctNorm, plctNum, plctSt, plctNeedPlc, plctStValid, plctEnter, plctSetCmd, plctAdd, plctFmtVals, plctRegWrite, plctRegRead, plctBo, plctCmdName, plctClear, plctWriteCmd, plctWriteId, plctTrigger, plctAuto, renderPlctEcho）
// 依赖: main.js 的 lastStatus；protocol.js 的 agtPackAscii/unpackAscii

// ---------- PLC 单机测试页（命令协议测试：全手填 + 回显区 + 富文本下发记录） ----------
let plctLastOk = null;   // 最近一次④成功：{plc, station, seq, cmd}（回显区标绿用）

// 全角数字归一化（中文输入法可能打出 ０-９）：转半角后再解析
function plctNorm(s) { return String(s).replace(/[０-９]/g, c => String.fromCharCode(c.charCodeAt(0) - 0xFEE0)).trim(); }
function plctNum() { const m = plctNorm(document.getElementById('plctPlc').value).match(/\d+/); return m ? +m[0] : NaN; }
function plctSt() { return parseInt(plctNorm(document.getElementById('plctStation').value), 10); }
// 站口全页可选：①②③ 纯地址写不需要站口；④ 填了做完整核对（站口回显+存储区），没填只核对编号回显
function plctNeedPlc() {
  const plc = plctNum();
  if (isNaN(plc)) { plctAdd('fail', `请填 PLC 编号（当前读到：${JSON.stringify(document.getElementById('plctPlc').value)}）`); return null; }
  return plc;
}
function plctStValid() { const st = plctSt(); return !isNaN(st) && st >= 1 && st <= 16 ? st : NaN; }
function plctSetCmd(v) { document.getElementById('plctCmdCode').value = v; }

// 下发记录条目：主行（时间+结果）+ 明细行（转译/原始数据/回读/诊断）
function plctAdd(cls, main, detail) {
  const t = new Date().toLocaleTimeString('zh-CN', { hour12: false });
  const color = cls === 'ok' ? 'ok-txt' : cls === 'fail' ? 'fail-txt' : 'stat-line';
  appendCapped(document.getElementById('plctLog'),
    `<div style="padding:2px 0"><div><span class="t">${t}</span> <span class="${color}">${main}</span></div>` +
    (detail ? `<div class="stat-line" style="padding-left:14px">${detail}</div>` : '') + '</div>', 200);
}
// HTML 转义统一走 util.js 的 esc()（Phase 4：plctEsc 已废弃）

// 原始数据格式化：≤16 字逐地址（十进制+0x 十六进制）；长段首尾
function plctFmtVals(addr, vals) {
  if (!vals || vals.length === 0) return '—';
  if (vals.length <= 16)
    return vals.map((v, i) => `${addr + i}=${v}(0x${v.toString(16).padStart(4, '0')})`).join(' ');
  return `${vals.length} 字: ${addr}=${vals[0]}(0x${vals[0].toString(16).padStart(4, '0')}) … ${addr + vals.length - 1}=${vals[vals.length - 1]}(0x${vals[vals.length - 1].toString(16).padStart(4, '0')})`;
}

async function plctRegWrite(plc, addr, values) {
  const t0 = performance.now();
  const r = await fetch('/api/debug/regwrite', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ plc, addr, values }) });
  const ms = Math.round(performance.now() - t0);
  if (!r.ok) { plctAdd('fail', `写 PLC${plc} 地址 ${addr} 失败（HTTP ${r.status}）`, 'PLC 未配置/断线/地址越界'); return { ok: false, ms }; }
  return { ok: true, ms };
}
async function plctRegRead(plc, addr, count) {
  const r = await fetch(`/api/debug/regread?plc=${plc}&addr=${addr}&count=${count}`);
  if (!r.ok) return null;
  return (await r.json()).values;
}
// 该 PLC 当前 config 的字节序（lastStatus 由 loadStatus 每 1.5s 刷新；config 变化自动跟随）
function plctBo() {
  const plc = plctNum();
  if (!isNaN(plc) && lastStatus) {
    const p = (lastStatus.plcs || []).find(x => x.index === plc);
    if (p) return p.registers.byteOrder;
  }
  return null;
}
function plctCmdName(cmd) { return cmd === 1 ? '1=写入货物ID' : cmd === 2 ? '2=清除货物ID' : `${cmd}=自定义命令码`; }

async function plctClear() {
  const plc = plctNeedPlc(); if (plc == null) return false;
  const r = await plctRegWrite(plc, 401, new Array(272).fill(0));
  plctAdd(r.ok ? 'ok' : 'fail', `① 清零 401~672（272 字）${r.ok ? '→ OK' : '→ 失败'}（${r.ms}ms）`,
    '转译：清掉旧命令/旧 ID——PLC 只看 400 变化，此步不会触发执行；原始数据 ' + (r.ok ? '272 字全 0' : '—'));
  return r.ok;
}

async function plctWriteCmd() {
  const plc = plctNeedPlc(); if (plc == null) return false;
  const addr = parseInt(plctNorm(document.getElementById('plctCmdAddr').value), 10);
  const cmd = parseInt(plctNorm(document.getElementById('plctCmdCode').value), 10);
  if (isNaN(addr) || addr < 0 || addr > 65535) { plctAdd('fail', '② 请填地址（0~65535，如 403）'); return false; }
  if (isNaN(cmd) || cmd < 0 || cmd > 65535) { plctAdd('fail', '② 请填命令码（0~65535）'); return false; }
  // 单步=纯寄存器写：PLC 只看 400 变化，中间步骤不触发，不校验后续步骤的输入（命令 1 需 ID 的校验在④/一键触发前）
  const r = await plctRegWrite(plc, addr, [cmd]);
  plctAdd(r.ok ? 'ok' : 'fail', `② 各站操作命令 地址 ${addr} = ${cmd} ${r.ok ? '→ OK' : '→ 失败'}（${r.ms}ms）`,
    `转译：${plctCmdName(cmd)}；原始数据 ${r.ok ? plctFmtVals(addr, [cmd]) : '—'}`);
  return r.ok;
}

async function plctWriteId() {
  const plc = plctNeedPlc(); if (plc == null) return false;
  const addr = parseInt(plctNorm(document.getElementById('plctIdAddr').value), 10);
  const id = document.getElementById('plctIdVal').value.trim();
  if (isNaN(addr) || addr < 0 || addr > 65535) { plctAdd('fail', '③ 请填地址（0~65535，如 449）'); return false; }
  if (!id) { plctAdd('fail', '③ 请填货物 ID（命令码 1 必填）'); return false; }
  const bo = plctBo();
  if (!bo) { plctAdd('fail', '③ 字节序读取失败（PLC 未配置/未在线？）'); return false; }
  const words = agtPackAscii(id, bo);
  const r = await plctRegWrite(plc, addr, words);
  plctAdd(r.ok ? 'ok' : 'fail', `③ 货物ID写入 地址 ${addr} 起 16 字 = "${esc(id)}" ${r.ok ? '→ OK' : '→ 失败'}（${r.ms}ms）`,
    `转译：命令 1 的货物 ID（字节序 ${bo}）——PLC 收到 400 新值后拷入 50~305 存储区；原始数据 ${r.ok ? plctFmtVals(addr, words) : '—'}`);
  return r.ok;
}

async function plctTrigger() {
  const plc = plctNeedPlc(); if (plc == null) return;
  const st = plctStValid();   // 站口可选：填了做完整核对（站口回显+存储区），没填只核对编号回显
  const seq = parseInt(plctNorm(document.getElementById('plctSeqNo').value), 10);
  const cmd = parseInt(plctNorm(document.getElementById('plctCmdCode').value), 10);
  if (isNaN(seq) || seq < 1 || seq > 65535) return plctAdd('fail', '④ 请填编号（1~65535，且与当前 400 值不同才触发）');
  if (isNaN(cmd)) return plctAdd('fail', '④ 核对回显需要②的命令码——请先填命令码');
  if (cmd === 1 && !document.getElementById('plctIdVal').value.trim())
    return plctAdd('fail', '④ 命令码=1（写入ID）——给 PLC 下发命令码 1 需要③货物ID，请先填并执行③');
  const r = await plctRegWrite(plc, 400, [seq]);
  if (!r.ok) {
    plctAdd('fail', `④ 写 400=${seq} 触发失败（${r.ms}ms）`,
      '转译：PLC 只看 400 新值——写失败则 PLC 不会扫描执行');
    return;
  }
  // 等待回显：每 200ms 读 306~322，最多 3s（耗时=PLC 处理性能）
  const t0 = performance.now();
  let echo = null, elapsed = 0;
  while (performance.now() - t0 < 3000) {
    await new Promise(res => setTimeout(res, 200));
    echo = await plctRegRead(plc, 306, 17);
    elapsed = Math.round(performance.now() - t0);
    if (echo && echo[0] === seq && (isNaN(st) || echo[st] === cmd)) break;
  }
  const matched = echo != null && echo[0] === seq && (isNaN(st) || echo[st] === cmd);
  if (!matched) {
    plctAdd('fail', `④ 写 400=${seq} 已发出（${r.ms}ms）→ 等待回显 ${elapsed}ms 未匹配`,
      `转译：PLC 只看 400 新值，收到后扫描 401~672 执行。诊断：①编号与当前 400 相同（未变化不触发）②命令码地址填错 ③PLC 程序未扫描该区 ④回显延迟 &gt;3s。实际回显：编号=${echo ? echo[0] : '—'}${isNaN(st) ? '' : ` 站口${st}=${echo ? echo[st] : '—'}`}（期望 ${seq}/${cmd}）`);
    return;
  }
  // 处理结果验证：读存储区 50~305（回显证「收到」、存储区证「处理」；站口未填则跳过站口级验证）
  let resultLine;
  if (isNaN(st)) {
    resultLine = `回显核对：编号=${echo[0]} ✓（${elapsed}ms）——未填站口，仅核对编号回显（未验证站口回显/存储区）`;
  } else {
    const stored = await plctRegRead(plc, 50 + (st - 1) * 16, 16);
    resultLine = `回显核对：编号=${echo[0]} 站口${st}=${echo[st]} ✓（${elapsed}ms）`;
    if (stored) {
      const storedId = unpackAscii(stored, plctBo() || 'high');
      resultLine += `；处理结果：存储区(50+${(st - 1) * 16} 起 16 字)=${storedId ? `"${esc(storedId)}"` : '（空）'}——` +
        (cmd === 1 ? (storedId ? '命令 1 已生效 ✓（PLC 已拷入 ID）' : '存储区为空，PLC 未拷入 ID？')
          : cmd === 2 ? (storedId === '' ? '命令 2 已生效 ✓（PLC 已清除）' : '存储区仍有内容，PLC 未清除？')
          : '自定义命令，观察存储区变化');
    }
  }
  plctLastOk = { plc, station: isNaN(st) ? 0 : st, seq, cmd };
  plctAdd('ok', `④ 写 400=${seq} 触发 → PLC 已执行 ✓（发送 ${r.ms}ms + 回显 ${elapsed}ms）`,
    `转译：PLC 收到 400 新值扫描 401~672 执行；原始数据 400=${seq}(0x${seq.toString(16).padStart(4, '0')})；${resultLine}`);
  renderPlctEcho();
}

async function plctAuto() {
  if (plctNeedPlc() == null) return;
  const cmd = parseInt(plctNorm(document.getElementById('plctCmdCode').value), 10);
  // 触发前校验：命令码 1（写入ID）必须有货物 ID——卡在触发前，不卡中间写步骤
  if (cmd === 1 && !document.getElementById('plctIdVal').value.trim())
  { plctAdd('fail', '▶ 命令码=1（写入ID）——给 PLC 下发命令码 1 需要③货物ID，请先填'); return; }
  plctAdd('info', '▶ 一键开始：按所填值依次执行 ①②③④（任一步失败即停止）');
  if (!(await plctClear())) return;
  if (!(await plctWriteCmd())) return;
  if (cmd === 1 && !(await plctWriteId())) return;   // 命令 1 必写货物 ID，失败不触发
  await plctTrigger();
}

// 回显/握手 306~340 自动刷新（lastStatus 每 1.5s 更新；目标站口行高亮、④成功后标绿）
async function renderPlctEcho() {
  const plc = plctNum();
  const el = document.getElementById('tbl-plct-echo');
  if (isNaN(plc)) { el.innerHTML = '<tr><td>请先填 PLC 编号</td></tr>'; return; }
  const p = lastStatus ? (lastStatus.plcs || []).find(x => x.index === plc) : null;
  if (!p) { el.innerHTML = '<tr><td>PLC 未配置或未在线</td></tr>'; return; }
  const r = p.registers;
  // 寄存器视图已迁至本页：首次确保建表（设备详情页可能从未打开）+ 每轮填值
  if (!detBuilt) buildDetail(p);
  document.getElementById('detByteOrder').textContent = r.byteOrder;
  fillRegTables(p);
  document.getElementById('plctBo').textContent = r.byteOrder === 'low' ? '低字节在前' : '高字节在前';
  const st = plctStValid();
  const rows = [{ cells: [{ t: 'text', v: 306 }, { t: 'text', v: r.echoNo }, { t: 'text', v: '命令编号回显' }] }];
  for (let i = 0; i < p.stations.length; i++) {
    const isTarget = !isNaN(st) && i + 1 === st;
    const isOk = plctLastOk && plc === plctLastOk.plc && i + 1 === plctLastOk.station && r.echoStation[i] === plctLastOk.cmd;
    rows.push({ cls: isOk ? 'hl-ok' : isTarget ? 'hl-target' : '', cells: [{ t: 'text', v: 307 + i }, { t: 'text', v: r.echoStation[i] }, { t: 'text', v: `站口${i + 1} 命令回显` }] });
  }
  rows.push({ cells: [{ t: 'text', v: 323 }, { t: 'text', v: r.scanStation }, { t: 'text', v: '扫码站口号' }] });
  rows.push({ cells: [{ t: 'text', v: '324~339' }, { t: 'text', v: esc(r.scanCode || '—') }, { t: 'text', v: '货物扫码号' }] });
  rows.push({ cells: [{ t: 'text', v: 340 }, { t: 'text', v: r.handshake }, { t: 'text', v: '握手（1=PLC 请求）' }] });
  buildRegTable(el, { cols: ['地址', '值', '含义'], rows });
  // 当前 400 值 → 用 306 回显（命令区为写区禁读，实测读会掉线；306=最后执行的 400 编号）
  document.getElementById('plctCur400').textContent = r.echoNo;
}
