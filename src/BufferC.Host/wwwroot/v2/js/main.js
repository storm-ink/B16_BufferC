// main.js — 全局状态 + 页签/侧栏/设置 + 页面激活 + 轮询调度（必须最后加载：顶层 let 状态 + kickoff）
// 导出: ST, logFilter/logPaused/logLevel/logWord/pendingCmd/pendingDbg, lastRenderedSeq/needFullRerender,
//       lastStatus, curPage, overviewKey, detPlc/detBuilt, openPage, closeTab, activatePage, setTheme
const ST = { 0: '空', 1: '有货', 2: '正放', 3: '正取', 4: '故障', 5: '人工有货' };
let logFilter = '', logPaused = false, logLevel = '', logWord = '', pendingCmd = null, pendingDbg = null;
let lastRenderedSeq = 0, needFullRerender = true;
let lastStatus = null;
let curPage = 'overview';
let overviewKey = '';
let detPlc = null, detBuilt = false;

// ---------- 页面元信息（分组/标签/固定） ----------
const PAGE_META = [
  { name: 'overview', label: '总览', group: '监控', pinned: true },
  { name: 'plcdetail', label: 'PLC 详情', group: '监控' },
  { name: 'events', label: '事件/告警', group: '监控' },
  { name: 'plctest', label: 'PLC 单机测试', group: '联调' },
  { name: 'agvctest', label: 'AGVC 联调', group: '联调' },
  { name: 'mcstest', label: 'MCS 联调', group: '联调' },
  { name: 'cmds', label: '命令', group: '工具' },
  { name: 'logs', label: '日志', group: '工具' },
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
  if (name === 'plcdetail') { detBuilt = false; loadStatus(); }
  if (name === 'plctest') loadStatus();
  if (name === 'events') loadActivity();
  if (name === 'logs') { needFullRerender = true; loadLogs(); }
  if (name === 'mcstest') { mcsBuild(); mcsRefresh(lastStatus); invRefresh(); }
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
    `<label class="switch"><input type="checkbox" data-action="set-theme" ${document.documentElement.getAttribute('data-theme') === 'dark' ? 'checked' : ''}><span class="slider"></span></label></div>`;
}

function setTheme(dark) {
  document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
  try { localStorage.setItem('bc-theme', dark ? 'dark' : 'light'); } catch (e) {}
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
  'do-send-cmd': arg => doSendCmd(arg === '1'),
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
})();

// ---------- 轮询调度（status 常开；活动/日志/AGVC 仅激活页） ----------
loadStatus();
setInterval(loadStatus, 1500);
setInterval(() => { if (curPage === 'events') loadActivity(); }, 3000);
setInterval(() => { if (curPage === 'logs') loadLogs(); }, 2000);
setInterval(() => { if (curPage === 'agvctest') loadAgvcTraffic(); }, 2000);
setInterval(() => { if (curPage === 'mcstest') { loadMcsTraffic(); loadMcsEvents(); } }, 2000);
