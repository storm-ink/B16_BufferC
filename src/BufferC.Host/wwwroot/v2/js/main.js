// main.js — 全局状态 + 页签/侧栏/设置 + 页面激活 + 轮询调度（必须最后加载：顶层 let 状态 + kickoff）
// 导出: ST, logFilter/logPaused/logLevel/logWord, lastRenderedSeq/needFullRerender,
//       lastStatus, curPage, overviewKey, detPlc/detBuilt, openPage, closeTab, activatePage, setTheme
const ST = { 0: '空', 1: '有货', 2: '正放', 3: '正取', 4: '其他', 5: '人工有货' };
let logFilter = '', logPaused = false, logLevel = '', logWord = '';
let lastRenderedSeq = 0, needFullRerender = true;
let lastStatus = null;
let curPage = 'overview';
let overviewKey = '';
let detPlc = null, detBuilt = false;

// ---------- 页面元信息（分组/标签/固定；命令页已按用户要求移除） ----------
const PAGE_META = [
  { name: 'overview', label: '总览', group: '监控', pinned: true },
  { name: 'plcdetail', label: '设备详情', group: '监控' },
  { name: 'events', label: '事件/告警', group: '监控' },
  { name: 'plctest', label: 'PLC 单机测试', group: '联调' },
  { name: 'agvctest', label: 'AGVC 联调', group: '联调' },
  { name: 'mcstest', label: 'MCS 联调', group: '联调' },
  { name: 'logs', label: '日志', group: '工具' },
  { name: 'flowlog', label: '流程日志', group: '工具' },
];
const pageLabel = name => (PAGE_META.find(m => m.name === name) || {}).label || name;
let enabledPages = PAGE_META.map(m => m.name);
let openTabs = ['overview'];

function loadJSON(key, def) {
  try { const v = JSON.parse(localStorage.getItem(key)); return v == null ? def : v; } catch (e) { return def; }
}
function saveJSON(key, v) { try { localStorage.setItem(key, JSON.stringify(v)); } catch (e) {} }

// ---------- 页面激活（原 switchPage 的页内钩子保留） ----------
function activatePage(name) {
  curPage = name;
  document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));
  const el = document.getElementById('page-' + name);
  if (el) el.classList.add('active');
  if (name === 'overview') invRefresh();
  if (name === 'plcdetail') { detBuilt = false; loadStatus(); }
  if (name === 'plctest') loadStatus();
  if (name === 'events') loadActivity();
  if (name === 'logs') { needFullRerender = true; loadLogs(); }
  if (name === 'flowlog') { needFullRerender = true; loadAuditLogs(); }
  if (name === 'mcstest') { mcsBuild(); mcsRefresh(lastStatus); }
  try { localStorage.setItem('bc-active', name); } catch (e) {}
  renderTabs();
  renderSidebar();
}

function openPage(name) {
  if (!enabledPages.includes(name)) return;
  if (!openTabs.includes(name)) { openTabs.push(name); saveJSON('bc-tabs', openTabs); }
  activatePage(name);
}

function closeTab(name) {
  if (name === 'overview') return;
  const i = openTabs.indexOf(name);
  if (i >= 0) openTabs.splice(i, 1);
  saveJSON('bc-tabs', openTabs);
  if (curPage === name) activatePage('overview');
  else renderTabs();
}

// ---------- 页签条 / 侧栏 / 设置面板 ----------
function renderTabs() {
  const box = document.getElementById('tabstrip');
  box.innerHTML = openTabs.map(name =>
    `<span class="tab-wrap${name === curPage ? ' active' : ''}">` +
    `<span class="tab" role="tab" aria-selected="${name === curPage}" data-action="open-page" data-arg="${name}">${pageLabel(name)}</span>` +
    (name === 'overview' ? '' : `<span class="tab-x" role="button" aria-label="关闭${pageLabel(name)}" data-action="close-tab" data-arg="${name}" title="关闭">×</span>`) +
    `</span>`).join('');
}

function renderSidebar() {
  const box = document.getElementById('sideGroups');
  const groups = [...new Set(PAGE_META.map(m => m.group))];
  box.innerHTML = groups.map(g =>
    `<div class="side-group"><div class="side-title">${g}</div>` +
    PAGE_META.filter(m => m.group === g && enabledPages.includes(m.name)).map(m =>
      `<button class="side-item${m.name === curPage ? ' active' : ''}" aria-current="${m.name === curPage ? 'page' : 'false'}" data-action="open-page" data-arg="${m.name}">${m.label}</button>`).join('') +
    `</div>`).join('');
}

function renderSettings() {
  const box = document.getElementById('settingsList');
  box.innerHTML = PAGE_META.map(m =>
    `<div class="set-item"><span>${m.label}${m.pinned ? '（固定）' : ''}</span>` +
    `<label class="switch"><input type="checkbox" data-page="${m.name}" ${m.pinned ? 'disabled' : ''} ${enabledPages.includes(m.name) ? 'checked' : ''}><span class="slider"></span></label></div>`).join('') +
    `<div class="set-item"><span>深色主题</span>` +
    `<label class="switch"><input type="checkbox" data-action="set-theme" ${document.documentElement.getAttribute('data-theme') === 'dark' ? 'checked' : ''}><span class="slider"></span></label></div>` +
    `<div class="set-item"><span>大屏模式（全屏看板）</span><button data-action="enter-screen">进入</button></div>`;
}

function setTheme(dark) {
  document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
  try { localStorage.setItem('bc-theme', dark ? 'dark' : 'light'); } catch (e) {}
}

// ---------- 大屏模式（kiosk：全屏 + 隐藏常规布局，Esc 退全屏时同步退出） ----------
let screenMode = false;
function enterScreen() {
  screenMode = true;
  document.body.setAttribute('data-screen', '1');
  if (lastStatus) renderScreenDash(lastStatus);
  renderScreenTicker();
  const el = document.documentElement;
  if (el.requestFullscreen) el.requestFullscreen().catch(() => {});
  const box = document.getElementById('settingsBox');
  if (box) box.style.display = 'none';
}
function exitScreen() {
  screenMode = false;
  document.body.removeAttribute('data-screen');
  if (document.fullscreenElement && document.exitFullscreen) document.exitFullscreen().catch(() => {});
}
document.addEventListener('fullscreenchange', () => {
  if (!document.fullscreenElement && screenMode) exitScreen();
});

// 大屏看板渲染（数据全部复用 loadStatus 快照，无新端点）
function renderScreenDash(s) {
  const unacked = s.alarms.filter(a => !a.acked).length;
  const hero = document.getElementById('dashAlarm');
  hero.textContent = String(unacked);
  hero.className = 'dash-hero-value' + (unacked ? ' crit' : '');
  document.getElementById('dashAlarmNote').textContent = unacked ? '需要确认' : '一切正常';
  const online = s.plcs.filter(p => p.connected).length;
  const occ = s.plcs.reduce((n, p) => n + p.stations.filter(x => x.state === 1 || x.state === 5).length, 0);
  document.getElementById('dash-kpis').innerHTML =
    `<div class="dash-kpi"><div class="dash-kpi-value ${s.plcs.length - online ? 'off' : 'ok'}">${online}/${s.plcs.length}</div><div class="dash-kpi-label">PLC 在线</div></div>` +
    `<div class="dash-kpi"><div class="dash-kpi-value">${occ}</div><div class="dash-kpi-label">有货站口</div></div>` +
    `<div class="dash-kpi"><div class="dash-kpi-value">${fmtDur(Date.now() - new Date(s.system.startedAt).getTime())}</div><div class="dash-kpi-label">运行时长</div></div>` +
    `<div class="dash-kpi"><div class="dash-kpi-value ${s.hsmsConnected ? 'ok' : 'off'}">${s.hsmsConnected ? 'ONLINE' : 'OFFLINE'}</div><div class="dash-kpi-label">MCS</div></div>`;
  document.getElementById('dash-plcs').innerHTML = s.plcs.map(p =>
    `<div class="dash-plc"><div class="dash-plc-title">${p.name || p.index + ' 号 Buffer'}${p.connected ? '' : '（离线）'}</div><div class="dash-grid">` +
    p.stations.map(x => `<div class="dash-cell st${x.state >= 5 ? 99 : x.state}">${STATE_ICON[x.state] || ''}${x.station}</div>`).join('') +
    `</div></div>`).join('');
}

// 底部告警/事件跑马（3s 轮询，appendCapped 上限）
async function renderScreenTicker() {
  const box = document.getElementById('dash-ticker');
  try {
    const [ev, al] = await Promise.all([apiGet('/api/events?tail=4'), apiGet('/api/alarms')]);
    if (!ev.fresh) return;
    const items = [
      ...ev.data.map(e => `${fmtTime(e.time)} CEID ${e.ceid} ${esc(e.description)}`),
      ...al.data.map(a => `告警 ${esc(a.alarmText)}（${a.acked ? '已确认' : '未确认'}）`),
    ];
    box.innerHTML = '';
    for (const t of items.slice(0, 6)) appendCapped(box, `<span class="dash-tick">${t}</span>`, 6);
  } catch (e) { reportError(box, e); }
}

function togglePageSetting(name, on) {
  if (name === 'overview') return;
  if (on && !enabledPages.includes(name)) enabledPages.push(name);
  if (!on) {
    const i = enabledPages.indexOf(name);
    if (i >= 0) enabledPages.splice(i, 1);
    closeTab(name);   // 已开页签一并关闭（若激活则回落总览）
  }
  saveJSON('bc-pages', enabledPages);
  renderSidebar();
}

// ---------- 事件委托（全部交互经 data-action / data-enter，无内联处理器） ----------
const ACTIONS = {
  // 需要参数或 this 原按钮的处理器
  'switch-tab': (arg, el) => switchTab(arg, el),
  'log-all': () => { setLogFilter(''); setLogWord(''); },
  'set-log-filter': arg => setLogFilter(arg),
  'set-log-word': arg => setLogWord(arg),
  'set-log-level': arg => setLogLevel(arg),
  'toggle-log-pause': () => toggleLogPause(),
  'plct-set-cmd': arg => plctSetCmd(+arg),
  'cim-state': arg => cimState(arg === '1'),
  'ack': arg => ack(arg),
  'mcs-set-dir': (arg, el) => mcsSetDir(arg, el),
  'mcs-toggle-pause': (arg, el) => mcsTogglePause(el),
  'mcs-toggle-traffic': arg => mcsToggleTraffic(arg),
  'mcs-select-changed': () => mcsRefresh(lastStatus),
  'det-plc-changed': () => detPlcChanged(),
  'goto-alarms': () => {
    openPage('events');
    const b = document.querySelector('#page-events .tabbar [data-action="switch-tab"][data-arg="alarms"]');
    if (b) switchTab('alarms', b);
  },
  'goto-pending': () => {
    openPage('events');
    const b = document.querySelector('#page-events .tabbar [data-action="switch-tab"][data-arg="pending"]');
    if (b) switchTab('pending', b);
  },
  // 载具事件补填（2026-08-19）：详情页站口弹窗 / 待补 tab 行内按钮 / 弹窗提交与关闭
  'fill-open': arg => { const [p, s] = arg.split(':').map(Number); openFill(p, s); },
  'fill-row': (arg, el) => fillRow(+el.dataset.plc, +el.dataset.station),
  'fill-submit': () => submitFill(),
  'fill-close': () => { document.getElementById('fillBox').style.display = 'none'; },
  'enter-screen': () => enterScreen(),
  'exit-screen': () => exitScreen(),
};

function runClickAction(act, arg, el) {
  if (act === 'open-page') openPage(el.dataset.arg);
  else if (act === 'close-tab') closeTab(el.dataset.arg);
  else if (act === 'open-settings') { renderSettings(); document.getElementById('settingsBox').style.display = 'flex'; }
  else if (act === 'close-settings') document.getElementById('settingsBox').style.display = 'none';
  else if (act === 'set-theme') setTheme(el.checked);
  else if (ACTIONS[act]) ACTIONS[act](arg, el);
  else if (typeof window[act] === 'function') window[act](el);
}

document.addEventListener('click', e => {
  const el = e.target.closest('[data-action]');
  if (el) runClickAction(el.dataset.action, el.dataset.arg, el);
});
document.addEventListener('change', e => {
  const el = e.target.closest('[data-action]');
  if (el) {
    const act = el.dataset.action;
    if (act === 'mcs-write-cell') mcsWriteCell(el);
    else if (act === 'plct-reg-plc-changed') { detBuilt = false; loadStatus(); }   // Q5：寄存器视图独立下拉切换 → 重建表
    else if (act === 'mcs-write-id') mcsWriteId(el);
    else if (act === 'mcs-write-scan') mcsWriteScan(el);
    else if (ACTIONS[act]) ACTIONS[act](el.dataset.arg, el);
    else if (typeof window[act] === 'function') window[act](el);
  }
  const p = e.target.closest('[data-page]');
  if (p) {
    if (p.dataset.page === 'overview') { p.checked = true; return; }
    togglePageSetting(p.dataset.page, p.checked);
  }
});
document.addEventListener('keydown', e => {
  if (e.key === 'Enter') {
    const el = e.target.closest('[data-enter]');
    if (!el) return;
    e.preventDefault();
    const which = el.dataset.enter;
    if (which === 'blur') el.blur();
    else if (which === 'plct-cmd') plctWriteCmd();
    else if (which === 'plct-id') plctWriteId();
    else if (which === 'plct-seq') plctTrigger();
    else if (which === 'agv-q') agvManualQuery();
    else if (which === 'agv-p') agvManualPush();
    else if (which === 'fill-row') fillRow(+el.dataset.plc, +el.dataset.station);
    else if (which === 'fill-submit') submitFill();
  }
  // 左右方向键在已开页签间切换（输入框内不拦截）
  if ((e.key === 'ArrowLeft' || e.key === 'ArrowRight') && !/INPUT|SELECT|TEXTAREA/.test(document.activeElement.tagName)) {
    const i = openTabs.indexOf(curPage);
    if (openTabs.length > 1 && i >= 0) {
      const d = e.key === 'ArrowRight' ? 1 : -1;
      activatePage(openTabs[(i + d + openTabs.length) % openTabs.length]);
    }
  }
});

// ---------- 初始化（恢复页面启用配置 + 页签集 + 激活页） ----------
(function init() {
  const all = PAGE_META.map(m => m.name);
  enabledPages = loadJSON('bc-pages', all).filter(n => all.includes(n));
  if (!enabledPages.includes('overview')) enabledPages.unshift('overview');
  openTabs = loadJSON('bc-tabs', ['overview']).filter(n => all.includes(n) && enabledPages.includes(n));
  if (!openTabs.includes('overview')) openTabs.unshift('overview');
  const active = loadJSON('bc-active', 'overview');
  renderSidebar();
  activatePage(openTabs.includes(active) ? active : 'overview');
  if (new URLSearchParams(location.search).get('screen') === '1') enterScreen();   // /v2/?screen=1 深链
})();

// ---------- 轮询调度（status 常开；活动/日志/AGVC 仅激活页；页面隐藏时暂停） ----------
let pollTimers = [];
function startPolling() {
  loadStatus();
  loadPending();
  pollTimers = [
    setInterval(loadStatus, 1500),
    setInterval(loadPending, 3000),   // 待补提示条/角标/tab（全局常开）
    setInterval(() => { if (curPage === 'events') loadActivity(); }, 3000),
    setInterval(() => { if (curPage === 'logs') loadLogs(); }, 2000),
    setInterval(() => { if (curPage === 'flowlog') loadAuditLogs(); }, 2000),
    setInterval(() => { if (curPage === 'agvctest') loadAgvcTraffic(); }, 2000),
    setInterval(() => { if (curPage === 'mcstest') { loadMcsTraffic(); loadMcsEvents(); } }, 2000),
    setInterval(() => { if (screenMode) renderScreenTicker(); }, 3000),
  ];
}
function stopPolling() { pollTimers.forEach(clearInterval); pollTimers = []; }
document.addEventListener('visibilitychange', () => {
  if (document.hidden) { stopPolling(); return; }
  // 恢复可见：立即刷新当前页 + 重启定时器
  if (curPage === 'events') loadActivity();
  if (curPage === 'logs') { needFullRerender = true; loadLogs(); }
  if (curPage === 'flowlog') { needFullRerender = true; loadAuditLogs(); }
  if (curPage === 'agvctest') loadAgvcTraffic();
  if (curPage === 'mcstest') { loadMcsTraffic(); loadMcsEvents(); }
  startPolling();
});
startPolling();
