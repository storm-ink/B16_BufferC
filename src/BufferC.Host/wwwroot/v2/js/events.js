// events.js — 事件/告警活动面板（导出: loadActivity, switchTab, updateAlarmTabBadge）
// 依赖: util.js 的 esc/reportError/clearError

// ---------- 活动面板（事件/命令/告警，仅本页激活时轮询） ----------
async function loadActivity() {
  const panels = {
    events: document.getElementById('tab-events'),
    cmds: document.getElementById('tab-cmds'),
    alarmhist: document.getElementById('tab-alarmhist'),
    alarms: document.getElementById('tab-alarms'),
  };
  try {
    const [ev, cmd, hist, alarms] = await Promise.all([
      (await fetch('/api/events?tail=50')).json(),
      (await fetch('/api/commands?tail=50')).json(),
      (await fetch('/api/alarm-history?tail=50')).json(),
      (await fetch('/api/alarms')).json(),
    ]);
    Object.values(panels).forEach(clearError);
    document.getElementById('tbl-events').innerHTML =
      '<tr><th>时间</th><th>CEID</th><th>描述</th></tr>' +
      (ev.length ? ev.map(e => `<tr><td>${fmtTime(e.time)}</td><td>${e.ceid}</td><td>${esc(e.description)}</td></tr>`).join('')
        : '<tr><td colspan="3" class="empty-row">暂无事件</td></tr>');
    document.getElementById('tbl-cmds').innerHTML =
      '<tr><th>时间</th><th>来源</th><th>站口</th><th>命令</th><th>结果</th><th>耗时</th></tr>' +
      (cmd.history.length ? cmd.history.map(c => `<tr><td>${fmtTime(c.time)}</td><td>${esc(c.source)}</td><td>${c.station}</td><td>${esc(c.cmd)}</td>` +
        `<td class="${c.ok ? 'ok-txt' : 'fail-txt'}">${c.ok ? 'OK' : 'FAIL'}</td><td>${c.elapsedMs}ms</td></tr>`).join('')
        : '<tr><td colspan="6" class="empty-row">暂无命令</td></tr>') +
      (cmd.pending.length ? `<tr><td colspan="6" class="fail-txt">悬空命令 ${cmd.pending.length} 条：${cmd.pending.map(p => esc(p.cmd + ' ' + p.carrierId)).join('；')}</td></tr>` : '');
    document.getElementById('tbl-alarmhist').innerHTML =
      '<tr><th>时间</th><th>动作</th><th>单位</th><th>告警码</th><th>文本</th></tr>' +
      (hist.length ? hist.map(h => `<tr><td>${fmtTime(h.time)}</td><td class="${h.action === 'SET' ? 'fail-txt' : 'ok-txt'}">${h.action}</td><td>${esc(h.unitId)}</td><td>${esc(h.alarmId)}</td><td>${esc(h.text)}</td></tr>`).join('')
        : '<tr><td colspan="5" class="empty-row">暂无告警历史</td></tr>');
    document.getElementById('tbl-alarms').innerHTML =
      '<tr><th>单位</th><th>告警码</th><th>文本</th><th>状态</th><th></th></tr>' +
      (alarms.length ? alarms.map(a => `<tr><td>${esc(a.unitId)}</td><td>${esc(a.alarmId)}</td><td>${esc(a.alarmText)}</td><td>${a.acked ? '已确认' : '未确认'}</td>` +
        `<td>${a.acked ? '' : `<button class="btn-ack" data-action="ack" data-arg="${a.alarmId}">确认</button>`}</td></tr>`).join('')
        : '<tr><td colspan="5" class="empty-row">无告警</td></tr>');
    updateAlarmTabBadge(alarms.filter(a => !a.acked).length);
  } catch (e) { reportError(panels.events, e); }
}

// 「当前告警」tab 未确认角标
function updateAlarmTabBadge(n) {
  const b = document.getElementById('alarmTabBadge');
  if (!b) return;
  b.hidden = n === 0;
  b.textContent = n;
}

function switchTab(name, btn) {
  document.querySelectorAll('#page-events .tabpanel').forEach(p => { p.classList.remove('active'); p.setAttribute('aria-hidden', 'true'); });
  document.querySelectorAll('#page-events .tabbar button').forEach(b => { b.classList.remove('active'); b.setAttribute('aria-selected', 'false'); });
  const panel = document.getElementById('tab-' + name);
  panel.classList.add('active');
  panel.removeAttribute('aria-hidden');
  btn.classList.add('active');
  btn.setAttribute('aria-selected', 'true');
}
