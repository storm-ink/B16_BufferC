// ui-shot.mjs — B16 BufferC 前端截图驱动（CDP over native WebSocket，零依赖）
// 用法: node tools/ui-shot.mjs <输出目录> <baseUrl> [页面名,...]
//   页面名: overview plcdetail cmds plctest events logs agvctest mcstest（默认全部 8 页）
// 依赖: 本机 Chrome（/c/Program Files/Google/Chrome/Application/chrome.exe）
// 输出: <outdir>/<name>.png + version.txt；stdout 打印每页截图与 console 错误汇总

import { spawn } from 'node:child_process';
import { writeFileSync, mkdirSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const PORT = 9222;

const PAGES = [
  ['overview', '总览'],
  ['plcdetail', 'PLC 详情'],
  ['cmds', '命令'],
  ['plctest', 'PLC 单机测试'],
  ['events', '事件/告警'],
  ['logs', '日志'],
  ['agvctest', 'AGVC 联调'],
  ['mcstest', 'MCS 联调'],
];
// 每页渲染健康探测（截图后执行，输出到 stdout）
const PROBES = {
  overview: '({rows:document.querySelectorAll("#tbl-overview tbody tr").length, on:document.querySelectorAll("#tbl-overview .b-ok").length})',
  plcdetail: '({cells:document.querySelectorAll("#detGrid .cell").length, regs:document.querySelectorAll("#tbl-reg-status tr").length, ids:document.querySelectorAll("#tbl-reg-id tr").length})',
  cmds: '({loc:!!document.getElementById("loc"), overlay:getComputedStyle(document.getElementById("confirmBox")).display})',
  plctest: '({echoRows:document.querySelectorAll("#tbl-plct-echo tr").length, steps:document.querySelectorAll(".step-no").length})',
  events: '({ev:document.querySelectorAll("#tbl-events tr").length, cmds:document.querySelectorAll("#tbl-cmds tr").length, alarms:document.querySelectorAll("#tbl-alarms tr").length})',
  logs: '({lines:document.querySelectorAll("#logs > div").length, filterBtns:document.querySelectorAll("#logFilter button").length})',
  agvctest: '({entries:document.querySelectorAll("#tbl-agvc .agvc-entry").length, stats:document.getElementById("agvStats")?.textContent?.slice(0,50)})',
  mcstest: '({inv:document.querySelectorAll("#tbl-inv tr").length, fast:document.querySelectorAll("#tbl-mcs-fast tr").length, id:document.querySelectorAll("#tbl-mcs-id tr").length, hsms:document.querySelectorAll("#mcsTrafficList .agvc-entry").length, ev:document.querySelectorAll("#tbl-mcsevents tr").length})',
};

const outDir = process.argv[2];
const baseUrl = (process.argv[3] || 'http://localhost:7001').replace(/\/$/, '');
const want = process.argv[4] ? process.argv[4].split(',') : PAGES.map(p => p[0]);
const theme = process.argv[5] || null;   // light | dark：截屏前写入 localStorage 并刷新
if (!outDir) { console.error('用法: node tools/ui-shot.mjs <输出目录> [baseUrl] [页面名,...] [主题]'); process.exit(2); }

mkdirSync(outDir, { recursive: true });
const userData = path.join(os.tmpdir(), `bc-ui-shot-${process.pid}`);

function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }
async function fetchJson(url) {
  for (let i = 0; i < 100; i++) {
    try { const r = await fetch(url); if (r.ok) return await r.json(); } catch {}
    await sleep(200);
  }
  throw new Error('CDP 端点不可达: ' + url);
}

let nextId = 1;
const pending = new Map();
const errors = [];
let ws;

function send(method, params = {}) {
  console.error('[cdp>] ' + method + (method === 'Runtime.evaluate' ? ' (' + (params.expression || '').slice(0, 60) + '…)' : ''));
  return new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject, method });
    ws.send(JSON.stringify({ id, method, params }));
    setTimeout(() => { if (pending.has(id)) { pending.delete(id); reject(new Error(method + ' 超时')); } }, 20000);
  });
}
function waitEvent(name, timeoutMs = 15000) {
  return new Promise((resolve, reject) => {
    const h = ev => {
      if (ev.method === name) { ws.removeEventListener('message', h); clearTimeout(t); resolve(ev); }
    };
    ws.addEventListener('message', h);
    const t = setTimeout(() => { ws.removeEventListener('message', h); reject(new Error('事件 ' + name + ' 超时')); }, timeoutMs);
  });
}

let chrome = null;
const probe = await fetchJson(`http://127.0.0.1:${PORT}/json/version`).catch(() => null);
if (probe) {
  console.log('复用已运行的 Chrome CDP');
} else {
  chrome = spawn(CHROME, [
    '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
    `--remote-debugging-port=${PORT}`, `--user-data-dir=${userData}`, '--window-size=1600,900', 'about:blank',
  ], { stdio: 'ignore' });
  await fetchJson(`http://127.0.0.1:${PORT}/json/version`);
}

try {
  const version = await fetchJson(`http://127.0.0.1:${PORT}/json/version`);
  const list = await fetchJson(`http://127.0.0.1:${PORT}/json/list`);
  const page = list.find(t => t.type === 'page');
  if (!page) throw new Error('未找到 page 目标');
  console.log('浏览器: ' + version.Browser);
  writeFileSync(path.join(outDir, 'version.txt'), JSON.stringify(version, null, 2));

  ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((res, rej) => {
    const t = setTimeout(() => rej(new Error('WS 打开超时，state=' + ws.readyState + ' url=' + page.webSocketDebuggerUrl)), 15000);
    ws.onopen = () => { clearTimeout(t); res(); };
    ws.onerror = (e) => { clearTimeout(t); rej(new Error('WS 错误: ' + (e?.message || e))); };
  });
  ws.onmessage = ev => {
    let m;
    try { m = JSON.parse(ev.data); } catch { console.error('[cdp] 无法解析消息:', String(ev.data).slice(0, 120)); return; }
    if (m.id && pending.has(m.id)) {
      const p = pending.get(m.id); pending.delete(m.id);
      console.error('[cdp<] ' + p.method + (m.error ? ' ERR ' + m.error.message : ' OK'));
      m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result); return;
    }
    if (m.method === 'Runtime.consoleAPICalled' && ['error', 'assert'].includes(m.params.type))
      errors.push('console.error: ' + m.params.args.map(a => a.value ?? a.description ?? '').join(' '));
    if (m.method === 'Runtime.exceptionThrown')
      errors.push('exception: ' + (m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text));
    if (m.method === 'Log.entryAdded' && m.params.entry.level === 'error')
      errors.push('log.error: ' + m.params.entry.text + ' ' + (m.params.entry.url || ''));
  };

  await send('Runtime.enable');
  await send('Log.enable');
  await send('Page.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 900, deviceScaleFactor: 1, mobile: false });

  const loaded = waitEvent('Page.loadEventFired', 15000).catch(() => console.error('[cdp] 未等到 loadEventFired，继续'));
  await send('Page.navigate', { url: baseUrl + '/' });
  await loaded;
  await sleep(1600);
  if (theme) {
    await send('Runtime.evaluate', { expression: `localStorage.setItem('bc-theme','${theme}'); location.reload(); true` });
    await sleep(1800);
  }

  for (const [name, label] of PAGES) {
    if (!want.includes(name)) continue;
    await send('Runtime.evaluate', {
      expression: `window.openPage ? openPage('${name}') : [...document.querySelectorAll('.nav button')].find(b => b.textContent === '${label}')?.click()`,
    });
    await sleep(1400);
    const shot = await send('Page.captureScreenshot', { format: 'png', fromSurface: true });
    const file = path.join(outDir, name + '.png');
    writeFileSync(file, Buffer.from(shot.data, 'base64'));
    const probe = PROBES[name] ? await send('Runtime.evaluate', { expression: PROBES[name], returnByValue: true }) : null;
    console.log(`OK ${name}.png (${(Buffer.byteLength(shot.data, 'base64') / 1024).toFixed(1)} KB) probe=${probe ? JSON.stringify(probe.result.value) : '-'}`);
  }

  console.log('console 错误: ' + (errors.length ? errors.length + ' 条\n  ' + errors.slice(0, 10).join('\n  ') : '0'));
} finally {
  try { ws && ws.close(); } catch {}
  if (chrome) chrome.kill();
}
