// Phase 7 验收：载具事件「数据完整才上报」前端（2026-08-19）——
// 提示条 / 台账事件待上报列 / 详情页黄色闪烁+点击弹窗补填 / 待补数据 tab 行内补填 / 中断撤销
// 前置：Simulator plc --plc-port 5590 + BufferC.Host 用 /tmp/bc-ui-config.json（web 7090 / hsms 5190）
export default async ({ ev, wait, baseUrl }) => {
  const out = [];
  const regwrite = (addr, v) => ev(`fetch('/api/debug/regwrite', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ plc: 1, addr: ${addr}, values: [${v}] }) }).then(r => r.ok); true`);

  await ev(`localStorage.clear(); location.reload(); true`);
  await wait(1800);

  // 站口 1：AGV 放入 0→2→1 → 中间态等 ID
  await regwrite(2, 2);           // 站口1 状态=2 正放
  await wait(600);
  await regwrite(2, 1);           // 站口1 状态=1 放完
  await wait(4500);               // 轮询(200ms) + loadPending(3s)

  out.push({
    label: '提示条：1 个载具事件等待数据（黄色条可见）',
    ok: await ev(`!document.getElementById('pendingStrip').hidden && document.getElementById('pendingStrip').textContent.includes('1 个载具事件等待数据')`),
    got: await ev(`document.getElementById('pendingStrip').textContent`),
  });
  out.push({
    label: '台账「事件待上报」列显示 等待货物ID(204)+时长，行高亮 row-pending',
    ok: await ev(`document.querySelector('#tbl-inv-body tr.row-pending') !== null && document.querySelector('#tbl-inv-body tr.row-pending').textContent.includes('等待货物ID(204)')`),
    got: await ev(`(() => { const t = document.querySelector('#tbl-inv-body tr.row-pending'); return t ? t.textContent.slice(0, 80) : '无高亮行'; })()`),
  });

  // 设备详情页：站口 1 黄色闪烁 + chip + 点击弹窗
  await ev(`openPage('plcdetail'); true`);
  await wait(2500);
  out.push({
    label: '详情页站口 1 中间态：blink 类 + chip 等待ID + 可点击',
    ok: await ev(`(() => { const c = document.getElementById('det-cell-1'); return c.classList.contains('blink') && c.textContent.includes('等待ID(204)') && c.dataset.action === 'fill-open'; })()`),
    got: await ev(`(() => { const c = document.getElementById('det-cell-1'); return { cls: c.className, txt: c.textContent }; })()`),
  });
  await ev(`document.getElementById('det-cell-1').click(); true`);
  await wait(300);
  out.push({
    label: '点击中间态站口 → 补填弹窗打开且显示等待信息',
    ok: await ev(`getComputedStyle(document.getElementById('fillBox')).display === 'flex' && document.getElementById('fillInfo').textContent.includes('等待货物 ID')`),
    got: await ev(`document.getElementById('fillInfo').textContent.slice(0, 80)`),
  });
  await ev(`(() => { const i = document.getElementById('fillCarrierId'); i.value = 'UI001'; document.querySelector('#fillBox [data-action="fill-submit"]').click(); return true; })()`);
  await wait(4000);               // 命令执行（写 PLC + 上报出口）
  out.push({
    label: '弹窗补填 → 台账载具ID=UI001、中间态清理（列显示 —）',
    ok: await ev(`document.querySelectorAll('#tbl-inv-body tr').length && (() => { const t = [...document.querySelectorAll('#tbl-inv-body tr')].find(r => r.textContent.includes('UI001')); return t !== undefined; })()`),
    got: await ev(`(() => { const t = [...document.querySelectorAll('#tbl-inv-body tr')].find(r => r.textContent.includes('UI001')); return t ? t.textContent.slice(0, 90) : '未找到 UI001'; })()`),
  });
  out.push({
    label: '台账新列：确认=✓ 已确认、等待ID=UI001（留痕），表头含两列',
    ok: await ev(`(() => { const h = [...document.querySelectorAll('#tbl-inv th')].map(x => x.textContent); const t = [...document.querySelectorAll('#tbl-inv-body tr')].find(r => r.children[4].textContent.includes('UI001')); return h.includes('确认') && h.includes('等待ID') && t && t.children[6].textContent.includes('已确认') && t.children[8].textContent.includes('UI001'); })()`),
    got: await ev(`(() => { const t = [...document.querySelectorAll('#tbl-inv-body tr')].find(r => r.children[4].textContent.includes('UI001')); return t ? { 确认: t.children[6].textContent.replace(/\\s+/g, ' '), 等待ID: t.children[8].textContent.replace(/\\s+/g, ' ') } : '未找到 UI001'; })()`),
  });
  await ev(`document.querySelector('#fillBox [data-action="fill-close"]').click(); true`);
  await wait(3500);
  out.push({
    label: '补填完成后提示条隐藏',
    ok: await ev(`document.getElementById('pendingStrip').hidden`),
    got: await ev(`({ hidden: document.getElementById('pendingStrip').hidden })`),
  });

  // 站口 2：人工放入 0→5 → 待补数据 tab 行内补填
  await regwrite(4, 5);           // 站口2=逻辑2→物理3→状态地址4（2026-08-20 映射）；状态=5 人工有货
  await wait(4500);
  await ev(`document.getElementById('pendingStrip').click(); true`);   // 提示条点击 → 跳事件页「待补数据」tab
  await wait(1500);
  out.push({
    label: '待补数据 tab：1 行（站口2 事件201）+ 输入框 + 补填按钮',
    ok: await ev(`document.querySelectorAll('#tab-pending tr').length === 2 && document.getElementById('tab-pending').textContent.includes('BUFFER01_02') && document.getElementById('tab-pending').querySelector('input.fill-id') !== null`),
    got: await ev(`(() => { const t = document.getElementById('tab-pending'); return t.textContent.replace(/\\s+/g, ' ').slice(0, 90); })()`),
  });
  await ev(`(() => { const i = document.querySelector('#tab-pending input.fill-id'); i.value = 'UI002'; document.querySelector('#tab-pending [data-action="fill-row"]').click(); return true; })()`);
  await wait(4000);
  out.push({
    label: '行内补填 → 待补列表清空 + 提示条隐藏 + 台账出现 UI002',
    ok: await ev(`document.getElementById('tab-pending').textContent.includes('无等待中的载具事件') && document.getElementById('pendingStrip').hidden && [...document.querySelectorAll('#tbl-inv-body tr')].some(r => r.textContent.includes('UI002'))`),
    got: await ev(`({ pending: document.getElementById('tab-pending').textContent.replace(/\\s+/g, ' ').slice(0, 40), strip: document.getElementById('pendingStrip').hidden })`),
  });

  // 站口 3：等 ID 期间被取走 → 中间态撤销（提示条/列表清空，无事件）
  await regwrite(6, 2);           // 站口3=逻辑3→物理5→状态地址6
  await wait(600);
  await regwrite(6, 1);
  await wait(4500);
  out.push({
    label: '站口3 中间态登记（提示条再次出现）',
    ok: await ev(`!document.getElementById('pendingStrip').hidden`),
    got: await ev(`document.getElementById('pendingStrip').textContent`),
  });
  await regwrite(6, 0);           // 取走 → 撤销
  await wait(4500);
  out.push({
    label: '状态离开有货态 → 中间态撤销（提示条隐藏、列表清空）',
    ok: await ev(`document.getElementById('pendingStrip').hidden && document.getElementById('tab-pending').textContent.includes('无等待中的载具事件')`),
    got: await ev(`({ strip: document.getElementById('pendingStrip').hidden, pending: document.getElementById('tab-pending').textContent.replace(/\\s+/g, ' ').slice(0, 40) })`),
  });

  // PLC 单机测试页命令区：400~416 按需读取（2026-08-19）
  await ev(`openPage('plctest'); true`);
  await wait(2500);
  await ev(`document.querySelector('[data-action="readCmdRaw"]').click(); true`);
  await wait(1500);
  out.push({
    label: '命令区读 400~416 → 18 行（表头+400+16 站）值已填充 + 提示「已读」',
    ok: await ev(`(() => { const rows = document.querySelectorAll('#tbl-reg-cmd tr'); const v400 = document.querySelector('#tbl-reg-cmd [data-r="400"]'); return rows.length === 18 && v400 && v400.textContent !== '' && document.getElementById('regCmdRaw').textContent.includes('已读 400~416'); })()`),
    got: await ev(`(() => { const v400 = document.querySelector('#tbl-reg-cmd [data-r="400"]'); const v401 = document.querySelector('#tbl-reg-cmd [data-r="401"]'); return { 行数: document.querySelectorAll('#tbl-reg-cmd tr').length, 400: v400 ? v400.textContent : '?', 401: v401 ? v401.textContent : '?' }; })()`),
  });

  // 站口物理↔逻辑映射显示（2026-08-20）：16 站机台 逻辑2=物理3（蛇形布线）
  out.push({
    label: '映射标签：状态区「站口2(物理3) 状态」地址4、命令区表「站口2(物理3) 操作命令码」地址403',
    ok: await ev(`(() => { const st = document.getElementById('tbl-reg-status'); const cmd = document.getElementById('tbl-reg-cmd'); const r = [...st.querySelectorAll('tr')].find(x => x.textContent.includes('站口2(物理3) 状态')); return r !== undefined && r.children[0].textContent.trim() === '4' && cmd.textContent.includes('站口2(物理3) 操作命令码') && [...cmd.querySelectorAll('tr')].some(x => x.textContent.includes('站口2(物理3)') && x.children[0].textContent.trim() === '403'); })()`),
    got: await ev(`(() => { const st = document.getElementById('tbl-reg-status'); const r = [...st.querySelectorAll('tr')].find(x => x.textContent.includes('站口2(物理3)')); return r ? r.textContent.replace(/\\s+/g, ' ') : '未找到'; })()`),
  });
  await ev(`openPage('plcdetail'); true`);
  await wait(2000);
  out.push({
    label: '详情页网格「站2(物理3)」并列显示',
    ok: await ev(`document.getElementById('det-cell-2').textContent.includes('站2(物理3)')`),
    got: await ev(`document.getElementById('det-cell-2').textContent.slice(0, 36)`),
  });

  return out;
};
