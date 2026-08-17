// agvctest.js — AGVC 联调页（导出: agvManualQuery, agvManualPush, agvEnter, loadAgvcTraffic）
// 依赖: util.js 的 esc/reportError/clearError

// ---------- AGVC 联调页（手动触发 + 发送记录） ----------
async function agvManualQuery() {
  const cmsIndex = document.getElementById('agvqCms').value.trim();
  const el = document.getElementById('agvqResult');
  if (!cmsIndex) { el.innerHTML = '<span class="fail-txt">请填 cmsIndex</span>'; return; }
  el.textContent = '发送中…';
  try {
    const r = await fetch('/api/agvc/manual/queryMachines', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cmsIndex }) });
    const j = await r.json();
    el.innerHTML = j.ok ? `<span class="ok-txt">OK</span> ${esc(j.message)}` : `<span class="fail-txt">FAIL</span> ${esc(j.message)}`;
    loadAgvcTraffic();
  } catch (e) { el.innerHTML = `<span class="fail-txt">请求失败: ${esc(e)}</span>`; }
}
async function agvManualPush() {
  const cmsIndex = document.getElementById('agvpCms').value.trim();
  const el = document.getElementById('agvpResult');
  if (!cmsIndex) { el.innerHTML = '<span class="fail-txt">请填 cmsIndex</span>'; return; }
  el.textContent = '发送中…';
  try {
    const r = await fetch('/api/agvc/manual/pushDeviceStatusInfo', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        cmsIndex,
        service: document.getElementById('agvpSvc').value.trim(),
        present: document.getElementById('agvpPres').value.trim(),
        trayId: document.getElementById('agvpTray').value.trim(),
      }) });
    const j = await r.json();
    el.innerHTML = j.ok ? `<span class="ok-txt">OK</span> ${esc(j.message)}` : `<span class="fail-txt">FAIL</span> ${esc(j.message)}`;
    loadAgvcTraffic();
  } catch (e) { el.innerHTML = `<span class="fail-txt">请求失败: ${esc(e)}</span>`; }
}
// 发送记录（手动+自动全量，原始 JSON 原文展示）+ 统计行
async function loadAgvcTraffic() {
  const box = document.getElementById('tbl-agvc');
  try {
    const res = await apiGet('/api/agvc/traffic?tail=50');
    if (!res.fresh) return;   // 过期响应丢弃
    const t = res.data.traffic || [];
    clearError(box);
    const qOk = t.filter(x => x.type === 'queryMachines' && x.ok).length;
    const qFail = t.filter(x => x.type === 'queryMachines' && !x.ok).length;
    const pOk = t.filter(x => x.type === 'pushDeviceStatusInfo' && x.ok).length;
    const pFail = t.filter(x => x.type === 'pushDeviceStatusInfo' && !x.ok).length;
    const stats = document.getElementById('agvStats');
    if (stats) stats.textContent = `queryMachines 成功 ${qOk}/失败 ${qFail}；push 成功 ${pOk}/失败 ${pFail}（最近 ${t.length} 条）`;
    box.innerHTML = t.map(x =>
      `<div class="agvc-entry">` +
      `<div><span class="t">${fmtTime(x.time)}</span> <b>${esc(x.type)}</b> cmsIndex=${esc(x.cmsIndex)} ` +
      `${x.ok ? '<span class="ok-txt">OK</span>' : '<span class="fail-txt">FAIL</span>'}</div>` +
      `<pre class="mono">请求: ${esc(x.request)}</pre>` +
      `<pre class="mono">响应: ${esc(x.response)}</pre>` +
      `</div>`).reverse().join('');
  } catch (e) { reportError(box, e); }
}
