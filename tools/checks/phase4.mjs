// Phase 4 验收：XSS 转义 / 事件委托 / 键盘 / 告警突出
export default async ({ ev, wait }) => {
  const out = [];
  await ev(`localStorage.clear(); location.reload(); true`);
  await wait(1800);

  // ---- esc() 单元 + 渲染函数级 XSS（构造数据喂真实 renderStatusBar/renderDetail） ----
  out.push({
    label: 'esc() 转义 & < >',
    ok: await ev(`esc('<img src=x>&') === '&lt;img src=x&gt;&amp;'`),
    got: await ev(`esc('<img src=x>&')`),
  });
  const payload = '<img src=x onerror=alert(1)>';
  await ev(`renderStatusBar({ hsmsConnected: true, alarms: [{ acked: false }],
      mcs: { peerEndpoint: '${payload}', peerMdln: '${payload}', peerSoftRev: '<b>x</b>', messagesIn: 1, messagesOut: 2, sendFail: 0, t3Timeout: 0, establishedAt: new Date().toISOString() },
      plcs: [{ connected: true }], system: { startedAt: new Date().toISOString(), mdln: '${payload}', softRev: '0.1' } }); true`);
  out.push({
    label: 'XSS：renderStatusBar 对端 MCS 字段按纯文本渲染（无元素注入）',
    ok: await ev(`document.querySelector('#statusbar img, #statusbar b:not(.tile-value)') === null && /&lt;img/.test(document.getElementById('statusbar').innerHTML)`),
    got: await ev(`document.getElementById('statusbar').textContent.slice(0, 80)`),
  });
  await ev(`openPage('plcdetail'); true`);
  await wait(800);
  await ev(`detBuilt = false; buildDetail({ registers: { byteOrder: 'high', alarmSummary: 0, bufferNo: 1, echoNo: 0, echoStation: new Array(16).fill(0), scanStation: 0, scanCode: '', handshake: 0 }, stats: { pollCount: 0, errorCount: 0, reconnectCount: 0, commandCount: 0, commandFailCount: 0, lastPollAt: null, lastError: '' } }); true`);
  await ev(`(() => { const p = { index: 1, ip: '127.0.0.1', registers: { byteOrder: 'high', alarmSummary: 0, bufferNo: 1, echoNo: 0, echoStation: new Array(16).fill(0), scanStation: 0, scanCode: '', handshake: 0 }, stats: { pollCount: 0, errorCount: 0, reconnectCount: 0, commandCount: 0, commandFailCount: 0, lastPollAt: null, lastError: '' }, stations: [] }; for (let i = 1; i <= 16; i++) p.stations.push({ station: i, state: i === 1 ? 1 : 0, carrierId: i === 1 ? '${payload}' : '', truncated: false, alarm: 0, avail: 0 }); detPlc = 1; document.getElementById('detPlc').value = '1'; renderDetail({ plcs: [p] }); return true; })()`);
  out.push({
    label: 'XSS：renderDetail 载具 ID 按纯文本渲染（网格 + ID 表无元素注入）',
    ok: await ev(`document.querySelector('#detGrid img, #tbl-reg-id img') === null && /&lt;img/.test(document.getElementById('det-cell-1').innerHTML) && /&lt;img/.test(document.getElementById('tbl-reg-id').innerHTML)`),
    got: await ev(`document.getElementById('det-cell-1').textContent.slice(0, 50)`),
  });
  await ev(`detBuilt = false; loadStatus(); true`);

  // ---- 键盘：方向键切页签 / Enter 委托 ----
  await ev(`openPage('plcdetail'); openPage('events'); true`);
  out.push({
    label: '左右方向键在页签间切换',
    ok: await ev(`(() => { document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true })); return curPage === 'plcdetail'; })()`),
    got: await ev(`curPage`),
  });
  await ev(`document.getElementById('plctPlc').value = '1'; openPage('plctest'); true`);
  await wait(1200);
  out.push({
    label: 'Enter 委托：plct 编号框回车触发校验（无写操作）',
    ok: await ev(`(() => { const el = document.getElementById('plctSeqNo'); el.focus(); el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true })); return document.getElementById('plctLog').textContent.includes('④ 请填编号'); })()`),
    got: await ev(`document.getElementById('plctLog').textContent.slice(-40)`),
  });

  // ---- 告警突出：注入站 1 告警码 → 条 + 瓷砖 + 角标 + ack ----
  await ev(`fetch('/api/debug/regwrite', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ plc: 1, addr: 18, values: [5] }) }).then(r => r.ok); true`);
  await wait(4000);
  out.push({
    label: '告警注入：顶部告警条出现 + 告警瓷砖 crit + 未确认角标',
    ok: await ev(`!document.getElementById('alarmStrip').hidden && document.querySelector('#statusbar .tile.t-crit.alarm-on') !== null`),
    got: await ev(`({ strip: document.getElementById('alarmStrip').textContent, tile: [...document.querySelectorAll('#statusbar .tile')].find(t => t.querySelector('.tile-label').textContent === '告警')?.querySelector('.tile-value').textContent })`),
  });
  await ev(`openPage('events'); true`);
  await wait(1500);
  const badgeOk = await ev(`!document.getElementById('alarmTabBadge').hidden`);
  await ev(`document.querySelector('#page-events [data-action="switch-tab"][data-arg="alarms"]').click(); true`);
  await wait(400);
  out.push({
    label: '事件页当前告警 tab：未确认角标 + 确认按钮存在',
    ok: badgeOk && await ev(`document.querySelectorAll('#tbl-alarms .btn-ack').length > 0`),
    got: await ev(`({ badge: document.getElementById('alarmTabBadge').textContent, acks: document.querySelectorAll('#tbl-alarms .btn-ack').length })`),
  });
  await ev(`document.querySelector('#tbl-alarms .btn-ack')?.click(); true`);
  await wait(1200);
  out.push({
    label: '点击确认 → 告警确认（角标清零）',
    ok: await ev(`document.getElementById('alarmTabBadge').hidden`),
    got: await ev(`({ badgeHidden: document.getElementById('alarmTabBadge').hidden, acks: document.querySelectorAll('#tbl-alarms .btn-ack').length })`),
  });
  // 清理：告警码归零
  await ev(`fetch('/api/debug/regwrite', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ plc: 1, addr: 18, values: [0] }) }).then(r => r.ok); true`);
  await wait(2500);
  out.push({
    label: '清理：告警码归零后告警条消失',
    ok: await ev(`document.getElementById('alarmStrip').hidden`),
  });
  return out;
};
