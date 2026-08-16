// ui-check.mjs — B16 BufferC 前端交互检查运行器（CDP over native WebSocket，零依赖）
// 用法: node tools/ui-check.mjs <baseUrl> <检查脚本.mjs>
// 检查脚本: export default async ({ ev, wait, baseUrl }) => [{ label, ok, got }...]
//   ev(expr) = 页面内 Runtime.evaluate（returnByValue）；wait(ms) = 等待
// 退出码 0 = 全部 ok；1 = 存在失败

import { spawn } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import os from 'node:os';
import path from 'node:path';

const CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const PORT = 9222;
const baseUrl = (process.argv[2] || 'http://localhost:7001/v2').replace(/\/$/, '');
const checkFile = process.argv[3];
if (!checkFile) { console.error('用法: node tools/ui-check.mjs <baseUrl> <检查脚本.mjs>'); process.exit(2); }

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
let ws;
function send(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, method, params }));
    setTimeout(() => { if (pending.has(id)) { pending.delete(id); reject(new Error(method + ' 超时')); } }, 20000);
  });
}

let chrome = null;
const probe = await fetchJson(`http://127.0.0.1:${PORT}/json/version`).catch(() => null);
if (!probe) {
  chrome = spawn(CHROME, [
    '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
    `--remote-debugging-port=${PORT}`, `--user-data-dir=${path.join(os.tmpdir(), 'bc-ui-check-' + process.pid)}`,
    '--window-size=1600,900', 'about:blank',
  ], { stdio: 'ignore' });
  await fetchJson(`http://127.0.0.1:${PORT}/json/version`);
}
try {
  const list = await fetchJson(`http://127.0.0.1:${PORT}/json/list`);
  const page = list.find(t => t.type === 'page');
  ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((res, rej) => {
    const t = setTimeout(() => rej(new Error('WS 打开超时')), 15000);
    ws.onopen = () => { clearTimeout(t); res(); };
    ws.onerror = e => { clearTimeout(t); rej(new Error('WS 错误: ' + (e?.message || e))); };
  });
  ws.onmessage = ev => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result); }
  };
  await send('Runtime.enable');
  await send('Page.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 900, deviceScaleFactor: 1, mobile: false });
  await send('Page.navigate', { url: baseUrl + '/' });
  await sleep(1800);

  const ev = async expr => {
    const r = await send('Runtime.evaluate', { expression: String(expr), returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error('页面异常: ' + (r.exceptionDetails.exception?.description || r.exceptionDetails.text) + '\n  表达式: ' + String(expr).slice(0, 120));
    return r.result.value;
  };
  const wait = ms => sleep(ms);

  const mod = await import(pathToFileURL(path.resolve(checkFile)).href);
  const results = await mod.default({ ev, wait, baseUrl });
  let fail = 0;
  for (const r of results) {
    if (!r.ok) fail++;
    console.log(`${r.ok ? 'PASS' : 'FAIL'} ${r.label}${r.got !== undefined ? ' → ' + JSON.stringify(r.got) : ''}`);
  }
  console.log(fail ? `共 ${fail} 项失败` : '全部通过');
  process.exitCode = fail ? 1 : 0;
} finally {
  try { ws && ws.close(); } catch {}
  if (chrome) chrome.kill();
}
