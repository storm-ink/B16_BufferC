// Phase 2 交互验收：左侧栏/页签/设置开关/持久化/互跳
export default async ({ ev, wait }) => {
  const out = [];
  // 清空 localStorage 后重载，回到默认状态
  await ev(`localStorage.clear(); location.reload(); true`);
  await wait(1800);

  out.push({
    label: '默认状态：侧栏 7 项（命令页已移除）、页签仅总览、激活总览',
    ok: await ev(`document.querySelectorAll('.side-item').length === 7 && document.querySelectorAll('.tab-wrap').length === 1 && curPage === 'overview'`),
    got: await ev(`({ side: document.querySelectorAll('.side-item').length, tabs: openTabs.join(','), cur: curPage })`),
  });

  await ev(`openPage('plcdetail'); openPage('events'); true`);
  out.push({
    label: '开 2 页签：共 3 页签、2 个 ×、激活 events',
    ok: await ev(`openTabs.length === 3 && document.querySelectorAll('.tab-x').length === 2 && curPage === 'events'`),
    got: await ev(`({ tabs: openTabs.join(','), x: document.querySelectorAll('.tab-x').length, cur: curPage })`),
  });

  await ev(`closeTab('events'); true`);
  out.push({
    label: '关闭激活页签 → 回落总览',
    ok: await ev(`openTabs.length === 2 && curPage === 'overview'`),
    got: await ev(`({ tabs: openTabs.join(','), cur: curPage })`),
  });

  await ev(`togglePageSetting('agvctest', false); togglePageSetting('mcstest', false); true`);
  out.push({
    label: '关闭 AGVC/MCS 开关 → 侧栏 5 项、已开页签保留',
    ok: await ev(`document.querySelectorAll('.side-item').length === 5 && openTabs.length === 2 && ![...document.querySelectorAll('.side-item')].some(b => ['AGVC 联调','MCS 联调'].includes(b.textContent))`),
    got: await ev(`({ side: document.querySelectorAll('.side-item').length, tabs: openTabs.join(',') })`),
  });

  out.push({
    label: '总览开关禁用（固定）',
    ok: await ev(`document.querySelector('#settingsList input[data-page="overview"]') === null || document.querySelector('#settingsList input[data-page="overview"]').disabled === true`),
  });

  await ev(`location.reload(); true`);
  await wait(1800);
  out.push({
    label: '刷新后：侧栏仍 5 项、页签集恢复（总览+PLC详情）、激活总览',
    ok: await ev(`document.querySelectorAll('.side-item').length === 5 && openTabs.join(',') === 'overview,plcdetail' && curPage === 'overview'`),
    got: await ev(`({ side: document.querySelectorAll('.side-item').length, tabs: openTabs.join(','), cur: curPage })`),
  });

  out.push({
    label: '侧栏底部「旧版界面」链接指向 /',
    ok: await ev(`document.querySelector('#sideFooter a').getAttribute('href') === '/'`),
    got: await ev(`document.querySelector('#sideFooter a').getAttribute('href')`),
  });

  // 恢复默认配置（清 bc-pages），避免遗留测试状态
  await ev(`localStorage.removeItem('bc-pages'); localStorage.removeItem('bc-tabs'); localStorage.removeItem('bc-active'); true`);
  return out;
};
