// Phase 6 验收：大屏 kiosk（进入/退出、Hero、KPI、PLC 卡、跑马、深链）
export default async ({ ev, wait }) => {
  const out = [];
  await ev(`localStorage.clear(); location.reload(); true`);
  await wait(1800);

  await ev(`enterScreen(); true`);
  await wait(1200);
  out.push({
    label: '进入大屏：data-screen=1、常规布局隐藏、看板显示',
    ok: await ev(`document.body.getAttribute('data-screen') === '1' && getComputedStyle(document.getElementById('below')).display === 'none' && getComputedStyle(document.getElementById('screen-dash')).display !== 'none'`),
    got: await ev(`({ screen: document.body.getAttribute('data-screen'), below: getComputedStyle(document.getElementById('below')).display })`),
  });
  out.push({
    label: 'Hero 告警数字 + KPI 瓷砖 + PLC 卡（16 格）',
    ok: await ev(`document.getElementById('dashAlarm').textContent === '0' && document.querySelectorAll('.dash-kpi').length === 4 && document.querySelectorAll('.dash-plc').length >= 1 && document.querySelectorAll('.dash-cell').length === 16 && document.getElementById('dashAlarmNote').textContent === '一切正常'`),
    got: await ev(`({ hero: document.getElementById('dashAlarm').textContent, kpi: document.querySelectorAll('.dash-kpi').length, plc: document.querySelectorAll('.dash-plc').length, cells: document.querySelectorAll('.dash-cell').length })`),
  });
  out.push({
    label: '底部跑马有内容（事件或空态可容忍，元素存在）',
    ok: await ev(`document.getElementById('dash-ticker') !== null`),
    got: await ev(`document.getElementById('dash-ticker').textContent.slice(0, 60)`),
  });

  // 告警 → Hero 变 crit
  await ev(`fetch('/api/debug/regwrite', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ plc: 1, addr: 18, values: [5] }) }).then(r => r.ok); true`);
  await wait(4000);
  out.push({
    label: '告警注入：Hero 变 crit + 提示「需要确认」',
    ok: await ev(`document.getElementById('dashAlarm').textContent !== '0' && document.getElementById('dashAlarm').className.includes('crit') && document.getElementById('dashAlarmNote').textContent === '需要确认'`),
    got: await ev(`({ hero: document.getElementById('dashAlarm').textContent, cls: document.getElementById('dashAlarm').className })`),
  });
  await ev(`fetch('/api/debug/regwrite', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ plc: 1, addr: 18, values: [0] }) }).then(r => r.ok); true`);
  await wait(2500);

  // 退出
  await ev(`exitScreen(); true`);
  await wait(400);
  out.push({
    label: '退出大屏：data-screen 移除、常规布局恢复',
    ok: await ev(`!document.body.hasAttribute('data-screen') && getComputedStyle(document.getElementById('below')).display !== 'none'`),
  });

  // 深链
  await ev(`location.href = '/v2/?screen=1'; true`);
  await wait(2500);
  out.push({
    label: '深链 /v2/?screen=1：自动进入大屏',
    ok: await ev(`document.body.getAttribute('data-screen') === '1'`),
    got: await ev(`document.body.getAttribute('data-screen')`),
  });
  await ev(`exitScreen(); location.href = '/v2/'; true`);
  await wait(1800);
  return out;
};
