// Phase 5 验收：空闲轮询 0 DOM 变更 / 共享渲染器结构 / apiGet 防过期 / appendCapped / 轮询暂停
export default async ({ ev, wait }) => {
  const out = [];
  await ev(`localStorage.clear(); location.reload(); true`);
  await wait(1800);

  // ---- 事件页空闲轮询：指纹 diff 后应 0 DOM 变更 ----
  await ev(`openPage('events'); true`);
  await wait(3500);   // 让首轮 + 一轮空闲轮询完成
  await ev(`window.__mut = 0; window.__obs = new MutationObserver(() => window.__mut++);
    window.__obs.observe(document.getElementById('page-events'), { childList: true, subtree: true, characterData: true }); true`);
  await wait(5200);   // 覆盖 ≥1 个 3s 轮询周期
  out.push({
    label: '事件页空闲轮询 5.2s：DOM 变更数 = 0',
    ok: await ev(`window.__mut === 0`),
    got: await ev(`({ mut: window.__mut })`),
  });
  await ev(`window.__obs.disconnect(); true`);

  // ---- 共享渲染器结构（PLC 详情 + MCS 可编辑表） ----
  await ev(`openPage('plcdetail'); true`);
  await wait(1500);
  out.push({
    label: 'PLC 详情寄存器表经 buildRegTable（data-r 行数一致）',
    ok: await ev(`document.querySelectorAll('#tbl-reg-status [data-r]').length === 50 && document.querySelectorAll('#tbl-reg-echo [data-r]').length === 19 && document.querySelectorAll('#tbl-reg-id [data-sid]').length === 16`),
    got: await ev(`({ st: document.querySelectorAll('#tbl-reg-status [data-r]').length, echo: document.querySelectorAll('#tbl-reg-echo [data-r]').length, id: document.querySelectorAll('#tbl-reg-id [data-sid]').length })`),
  });
  // 直接开单机测试页（从未开设备详情）→ 寄存器视图也应建表
  await ev(`detBuilt = false; document.getElementById('plctPlc').value = '1'; openPage('plctest'); true`);
  await wait(1500);
  out.push({
    label: '直接开 PLC 单机测试：寄存器视图自动建表（无需先开设备详情）',
    ok: await ev(`document.querySelectorAll('#tbl-reg-status [data-r]').length === 50 && document.getElementById('detByteOrder').textContent !== '—'`),
    got: await ev(`({ regs: document.querySelectorAll('#tbl-reg-status [data-r]').length, bo: document.getElementById('detByteOrder').textContent })`),
  });

  await ev(`openPage('overview'); true`);
  await wait(2500);
  out.push({
    label: '台账（总览页）：静态 thead + 仅 tbody 刷新 + sticky 容器 + 打开自动拉取',
    ok: await ev(`document.querySelector('#tbl-inv thead') !== null && document.querySelectorAll('#tbl-inv-body tr').length === 16 && document.querySelector('#invWrap.tbl-scroll') !== null`),
    got: await ev(`({ thead: !!document.querySelector('#tbl-inv thead'), rows: document.querySelectorAll('#tbl-inv-body tr').length })`),
  });
  await ev(`openPage('mcstest'); true`);
  await wait(2500);
  out.push({
    label: 'MCS 可编辑表经 buildRegTable（data-m/data-mid + 委托属性）',
    ok: await ev(`document.querySelectorAll('#tbl-mcs-fast [data-m]').length === 50 && document.querySelectorAll('#tbl-mcs-id [data-mid]').length === 16 && document.querySelectorAll('#tbl-mcs-scan [data-m]').length === 2 && document.querySelector('#mcsScanCode[data-action="mcs-write-scan"]') !== null`),
    got: await ev(`({ fast: document.querySelectorAll('#tbl-mcs-fast [data-m]').length, id: document.querySelectorAll('#tbl-mcs-id [data-mid]').length })`),
  });

  // ---- apiGet 过期响应丢弃 ----
  out.push({
    label: 'apiGet：并发同端点仅最新请求 fresh',
    ok: await ev(`(async () => { const a = apiGet('/api/status'); const b = apiGet('/api/status'); const [ra, rb] = await Promise.all([a, b]); return ra.fresh === false && rb.fresh === true; })()`),
  });

  // ---- appendCapped 上限 + 滚动不跳 ----
  out.push({
    label: 'appendCapped：裁剪到上限且底部跟随',
    ok: await ev(`(() => { const d = document.createElement('div'); d.style.height = '50px'; d.style.overflow = 'auto'; document.body.appendChild(d); for (let i = 0; i < 10; i++) appendCapped(d, '<div style="height:20px">' + i + '</div>', 5); const ok = d.children.length === 5 && d.scrollTop > 0 && d.firstChild.textContent === '5'; d.remove(); return ok; })()`),
    got: await ev(`(async () => { const d = document.createElement('div'); d.style.height = '50px'; d.style.overflow = 'auto'; document.body.appendChild(d); for (let i = 0; i < 10; i++) appendCapped(d, '<div style="height:20px">' + i + '</div>', 5); const r = { n: d.children.length, top: d.scrollTop, first: d.firstChild.textContent }; d.remove(); return r; })()`),
  });

  // ---- 轮询暂停/恢复 ----
  out.push({
    label: 'stopPolling/startPolling 定时器管理（status/events/logs/agvc/mcs/大屏跑马 = 6）',
    ok: await ev(`stopPolling() || pollTimers.length === 0 ? (startPolling() || true) && pollTimers.length === 6 : false`),
    got: await ev(`pollTimers.length`),
  });

  await ev(`localStorage.removeItem('bc-pages'); localStorage.removeItem('bc-tabs'); localStorage.removeItem('bc-active'); true`);
  return out;
};
