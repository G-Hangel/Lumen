import assert from 'node:assert/strict';
import { readFile, rm, stat } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';

const stage = process.argv[2] || 'verify';
assert.ok(['verify', 'cleanup'].includes(stage), `Unknown stage: ${stage}`);

let page = null;
for (let attempt = 0; attempt < 30 && !page; attempt++) {
  const pages = await (await fetch('http://127.0.0.1:9223/json/list')).json();
  page = pages.find(value => value.url === 'https://lumen.local/index.html') ?? null;
  if (!page) await new Promise(resolve => setTimeout(resolve, 1000));
}
assert.ok(page, 'The Lumen WebView is not open in the experimental instance.');
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });

let sequence = 0;
const pending = new Map();
const exceptions = [];
socket.addEventListener('message', event => {
  const value = JSON.parse(event.data);
  if (value.method === 'Runtime.exceptionThrown') exceptions.push(value.params.exceptionDetails.text);
  const task = pending.get(value.id); if (!task) return;
  pending.delete(value.id); clearTimeout(task.timer);
  if (value.error) task.reject(new Error(value.error.message)); else task.resolve(value.result);
});
function command(method, params = {}, timeoutMs = 30000) {
  return new Promise((resolve, reject) => {
    const id = ++sequence, timer = setTimeout(() => { pending.delete(id); reject(new Error(method)); }, timeoutMs);
    pending.set(id, { resolve, reject, timer }); socket.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression, timeoutMs = 30000) {
  const result = await command('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, timeoutMs);
  assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails)); return result.result.value;
}
const wait = predicate => evaluate(`new Promise((resolve,reject)=>{
  const check=()=>{if(${predicate}){clearTimeout(timer);observer.disconnect();resolve(true);}};
  const observer=new MutationObserver(check),timer=setTimeout(()=>{observer.disconnect();reject(new Error('UI not ready: ${predicate.replace(/'/g, "\\'")}'));},20000);
  observer.observe(document,{subtree:true,attributes:true,childList:true,characterData:true});check();})`);
async function rpc(type, payload) {
  return evaluate(`new Promise((resolve,reject)=>{
   const id=crypto.randomUUID(),timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('RPC timeout'));},20000);
   const handler=e=>{if(e.data.id!==id)return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve({...e.data});};
   chrome.webview.addEventListener('message',handler);chrome.webview.postMessage({version:1,id,type:${JSON.stringify(type)},payload:${JSON.stringify(payload)}});})`);
}
const connected = () => wait("document.querySelector('#connection-status')?.dataset.state==='connected'");
const variables = () => evaluate(`(()=>{const style=document.documentElement.style;const value=name=>style.getPropertyValue(name).trim();
  return {background:value('--vs-background'),foreground:value('--vs-foreground'),accent:value('--vs-accent'),chatFont:value('--chat-font'),mono:value('--mono'),
    fontSize:value('--font-size'),radius:value('--radius'),density:document.documentElement.getAttribute('data-density'),modeAccent:value('--mode-accent')};})()`);
const themeFile = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'Lumen', 'providers.theme.json');

async function reload(expectedSource) {
  try { await evaluate('location.reload()'); } catch { /* the reload destroys the execution context on purpose */ }
  await new Promise(resolve => setTimeout(resolve, 1500));
  await connected();
  // The preset is fetched asynchronously right after the handshake, so wait for the applied source.
  await wait(`document.documentElement.dataset.themeSource===${JSON.stringify(expectedSource)}`);
}

const preset = { name: 'Lumen Exp', background: '#101014', foreground: '#f2f2f5', accent: '#42c99a', chatFont: 'Segoe UI', codeFont: 'Cascadia Mono', fontSize: 15, density: 'compact', borderRadius: 4, modeAccent: { ask: '#66d9a6', plan: '#f2c14e', agent: '#7fb2ff' } };

if (stage === 'cleanup') {
  await rpc('byok.theme.reset', {});
  await rm(themeFile, { force: true });
  console.log('Custom theme removed from the experimental instance.');
} else {
  await connected();
  // Start from a clean slate: the baseline must be the VS theme, not a preset left by a previous run.
  await rpc('byok.theme.reset', {});
  await reload('vs');
  const baseline = await variables();
  assert.ok(baseline.background, 'The VS theme must be applied before the custom theme.');
  assert.equal(baseline.chatFont, '', 'The VS theme must not carry custom variables.');

  // 1. A valid preset is accepted and persisted per user.
  const applied = await rpc('byok.theme.set', { theme: preset });
  assert.equal(applied.type, 'byok.theme.set.result', JSON.stringify(applied));
  assert.deepEqual(applied.payload.warnings, [], JSON.stringify(applied.payload.warnings));
  assert.equal(applied.payload.theme.name, 'Lumen Exp');
  assert.ok((await stat(themeFile)).size > 0, 'The preset must be persisted in the per-user settings.');

  // 2. After a WebView reload the persisted preset is applied without any user action.
  await reload('custom');
  const custom = await variables();
  assert.equal(custom.background, '#101014', JSON.stringify(custom));
  assert.equal(custom.foreground, '#f2f2f5');
  assert.equal(custom.accent, '#42c99a');
  assert.equal(custom.chatFont, 'Segoe UI');
  assert.equal(custom.mono, 'Cascadia Mono');
  assert.equal(custom.fontSize, '15px');
  assert.equal(custom.radius, '4px');
  assert.equal(custom.density, 'compact');
  assert.ok(custom.modeAccent, 'The accent of the current mode must be materialized.');

  // 3. CN: an invalid preset is rejected and never reaches the CSS variables.
  const rejected = await rpc('byok.theme.set', { theme: { background: 'not-a-color' } });
  assert.equal(rejected.type, 'error', JSON.stringify(rejected));
  assert.equal(rejected.payload.code, 'invalid_configuration');
  await reload('custom');
  const afterReject = await variables();
  assert.equal(afterReject.background, '#101014', 'A rejected preset must not change the applied theme.');

  // 4. A low-contrast preset is accepted with a warning instead of being silently applied.
  const warned = await rpc('byok.theme.set', { theme: { background: '#ffffff', foreground: '#bbbbbb', accent: '#dddddd' } });
  assert.equal(warned.type, 'byok.theme.set.result', JSON.stringify(warned));
  assert.ok(warned.payload.warnings.includes('low_contrast_text'), JSON.stringify(warned.payload.warnings));
  assert.ok(warned.payload.warnings.includes('low_contrast_accent'), JSON.stringify(warned.payload.warnings));

  // 5. Reset returns the WebView to the VS theme.
  const reset = await rpc('byok.theme.reset', {});
  assert.equal(reset.type, 'byok.theme.reset.result', JSON.stringify(reset));
  await reload('vs');
  const restored = await variables();
  assert.equal(restored.background, baseline.background, JSON.stringify(restored));
  assert.equal(restored.chatFont, '');
  assert.equal(restored.density, null);

  assert.deepEqual(exceptions, [], 'The WebView must not raise JavaScript exceptions.');
  console.log(JSON.stringify({
    source: 'real VS Exp/WebView2 + per-user settings; no paid APIs',
    baseline: baseline.background,
    applied: custom,
    rejectedCode: rejected.payload.code,
    warnings: warned.payload.warnings,
    restored: restored.background,
    exceptions
  }, null, 2));
}
socket.close();
