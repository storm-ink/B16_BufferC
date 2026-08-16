// Phase 3 验收：KPI 瓷砖/站口网格图标/主题切换/去硬编码颜色
export default async ({ ev, wait }) => {
  const out = [];
  await ev(`localStorage.clear(); location.reload(); true`);
  await wait(1800);

  out.push({
    label: '状态栏为 KPI 瓷砖行（≥6 块，含标签+数值）',
    ok: await ev(`document.querySelectorAll('#statusbar .tile').length >= 6 && document.querySelectorAll('#statusbar .tile-label').length >= 6`),
    got: await ev(`({ tiles: document.querySelectorAll('#statusbar .tile').length, text: document.getElementById('statusbar').textContent.slice(0, 60) })`),
  });

  await ev(`openPage('plcdetail'); true`);
  await wait(1200);
  out.push({
    label: '站口网格：每格含状态图标+文字（非仅颜色）',
    ok: await ev(`(() => { const c = document.querySelector('#det-cell-1'); return c && /站1 · [◌●↓↑⚠✋][空有货正放正取故障人工]+/.test(c.textContent); })()`),
    got: await ev(`document.querySelector('#det-cell-1').textContent.slice(0, 30)`),
  });

  out.push({
    label: '浅色主题默认（data-theme 非 dark）',
    ok: await ev(`document.documentElement.getAttribute('data-theme') !== 'dark'`),
    got: await ev(`document.documentElement.getAttribute('data-theme')`),
  });

  await ev(`setTheme(true); true`);
  out.push({
    label: '切换深色：data-theme=dark + 持久化',
    ok: await ev(`document.documentElement.getAttribute('data-theme') === 'dark' && localStorage.getItem('bc-theme') === 'dark'`),
    got: await ev(`document.documentElement.getAttribute('data-theme')`),
  });

  await ev(`location.reload(); true`);
  await wait(1800);
  out.push({
    label: '刷新后深色保持（head 预载脚本无 FOUC 路径）',
    ok: await ev(`document.documentElement.getAttribute('data-theme') === 'dark'`),
  });

  await ev(`setTheme(false); localStorage.removeItem('bc-theme'); true`);
  return out;
};
