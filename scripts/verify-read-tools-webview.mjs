import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';

const stage = process.argv[2] || 'verify';
assert.ok(['verify', 'closed'].includes(stage));
const manifest = JSON.parse((await readFile(new URL('../artifacts/context-fixtures.json', import.meta.url), 'utf8')).replace(/^\uFEFF/, ''));
const pages = await (await fetch('http://127.0.0.1:9223/json/list')).json();
const page = pages.find(value => value.url === 'https://lumen.local/index.html');
assert.ok(page, 'Open Lumen in the experimental instance.');
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
let sequence = 0;
const pending = new Map(), exceptions = [];
socket.addEventListener('message', event => {
  const message = JSON.parse(event.data);
  if (message.method === 'Runtime.exceptionThrown') exceptions.push(message.params.exceptionDetails.text);
  const task = pending.get(message.id);
  if (!task) return;
  pending.delete(message.id); clearTimeout(task.timer);
  if (message.error) task.reject(new Error(message.error.message)); else task.resolve(message.result);
});
function command(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++sequence, timer = setTimeout(() => { pending.delete(id); reject(new Error(method)); }, 20000);
    pending.set(id, { resolve, reject, timer }); socket.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const result = await command('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
  return result.result.value;
}
async function rpc(name, args, extra = {}) {
  return evaluate(`new Promise((resolve,reject)=>{
    const id=crypto.randomUUID(),timer=setTimeout(()=>{chrome.webview.removeEventListener('message',listener);reject(new Error('Read RPC timeout'));},15000);
    const listener=e=>{if(e.data.id!==id)return;clearTimeout(timer);chrome.webview.removeEventListener('message',listener);resolve(e.data);};
    chrome.webview.addEventListener('message',listener);
    chrome.webview.postMessage({version:1,id,type:'byok.tool.read',payload:${JSON.stringify({ name, arguments: args, ...extra })}});
  })`);
}
function content(reply, name) {
  assert.equal(reply.type, 'byok.tool.read.result', JSON.stringify(reply));
  assert.equal(reply.payload.name, name);
  assert.equal(reply.payload.isError, false, JSON.stringify(reply));
  return JSON.parse(reply.payload.content);
}
function error(reply, code) {
  if (reply.type === 'error') assert.equal(reply.payload.code, code);
  else { assert.equal(reply.payload.isError, true); assert.equal(JSON.parse(reply.payload.content).code, code); }
}
const report = { startedAt: new Date().toISOString(), stage, source: 'real experimental Visual Studio and WebView2; explicit disk tools; no configured model' };
try {
  await command('Runtime.enable'); await command('Page.enable');
  assert.equal(await evaluate("document.querySelector('#connection-status').dataset.state"), 'connected');
  if (stage === 'closed') {
    error(await rpc('read_file', { path: 'Sample.cs' }), 'workspace_unavailable');
    report.noSolutionRejected = true;
  } else {
    assert.equal(await evaluate("document.querySelector('#byok-send').disabled"), true, 'No provider or model selected in UI.');
    const diskBefore = await readFile(path.join(manifest.root, 'Sample.cs'), 'utf8');
    const hash = text => createHash('sha256').update(text).digest('hex');
    const file = content(await rpc('read_file', { path: 'Sample.cs', startLine: 2, endLine: 2 }), 'read_file');
    assert.equal(file.lines.length, 1); assert.equal(file.lines[0].line, 2);
    assert.ok(file.lines[0].text.includes('disk-marker'));
    assert.ok(!file.lines[0].text.includes(manifest.marker), 'Disk tools do not silently read unsaved editor buffers.');
    const listed = content(await rpc('list_dir', { path: '.' }), 'list_dir');
    assert.ok(listed.entries.some(value => value.path === 'Sample.cs'));
    const found = content(await rpc('find_files', { path: '.', glob: '**/*.cs' }), 'find_files');
    assert.ok(found.results.some(value => value.path === 'Sample.cs'));
    const grep = content(await rpc('grep', { path: '.', pattern: 'disk-marker', glob: '*.cs' }), 'grep');
    assert.equal(grep.results[0].line, 2);
    const limited = content(await rpc('list_dir', { path: '.', maxResults: 1 }), 'list_dir');
    assert.equal(limited.entries.length, 1); assert.equal(limited.truncated, true);
    error(await rpc('read_file', { path: '../STATUS.md' }), 'invalid_path');
    error(await rpc('read_file', { path: 'Sample.cs:secret' }), 'invalid_path');
    error(await rpc('read_file', { path: 'Binary.bin' }), 'binary_file');
    error(await rpc('read_file', { path: 'TooLargeTools.txt' }), 'file_too_large');
    error(await rpc('create_file', { path: 'forged.txt' }), 'unknown_tool');
    error(await rpc('run_command', { command: 'anything' }), 'unknown_tool');
    error(await rpc('read_file', { path: 'Sample.cs' }, { root: manifest.root, mode: 'Agent' }), 'invalid_message');
    report.readRange = true; report.diskNotUnsavedBuffer = true; report.listFindGrep = true; report.truncation = true;
    report.pathAndBinaryNegatives = true; report.forgedWritesAndAuthorityRejected = true;
    assert.equal(hash(await readFile(path.join(manifest.root, 'Sample.cs'), 'utf8')), hash(diskBefore));
    report.diskUnchanged = true;
    await evaluate(`document.querySelector('#byok-read-tools').open=true;`);
    async function uiRead(name, values) {
      await evaluate(`(()=>{const select=document.querySelector('#byok-read-name');select.value=${JSON.stringify(name)};select.dispatchEvent(new Event('change'));})()`);
      await evaluate(Object.entries(values).map(([id, value]) => `document.querySelector('#byok-read-${id}').value=${JSON.stringify(String(value))};`).join(''));
      const response = await evaluate(`new Promise((resolve,reject)=>{
        const timer=setTimeout(()=>{chrome.webview.removeEventListener('message',listener);reject(new Error('UI read timeout'));},15000);
        const listener=e=>{if(e.data.type!=='byok.tool.read.result'&&e.data.type!=='error')return;clearTimeout(timer);chrome.webview.removeEventListener('message',listener);resolve(e.data);};
        chrome.webview.addEventListener('message',listener);document.querySelector('#byok-read-form').requestSubmit();
      })`);
      await evaluate(`new Promise((resolve,reject)=>{
        const check=()=>{if(!document.querySelector('#byok-read-run').disabled){clearTimeout(timer);observer.disconnect();resolve(true);}};
        const timer=setTimeout(()=>{observer.disconnect();reject(new Error('UI remained busy'));},10000),observer=new MutationObserver(check);
        observer.observe(document,{subtree:true,attributes:true,childList:true});check();
      })`);
      return response;
    }
    const ui = content(await uiRead('read_file', { path: 'InertTools.txt', start: '', end: '' }), 'read_file');
    assert.ok(ui.lines[0].text.includes('<script>'));
    const inert = await evaluate(`({text:document.querySelector('#byok-read-result').textContent,nodes:document.querySelector('#byok-read-result').childElementCount,executed:window.lumenToolExecuted===true,chatTurns:document.querySelectorAll('#byok-output .byok-turn').length})`);
    assert.equal(inert.nodes, 0); assert.equal(inert.executed, false); assert.ok(inert.text.includes('script')); assert.equal(inert.chatTurns, 0);
    error(await uiRead('read_file', { path: '../STATUS.md' }), 'invalid_path');
    assert.ok(await evaluate(`document.querySelector('#byok-read-status').textContent.length>0`));
    content(await uiRead('find_files', { path: '.', glob: '**/*.cs', max: '10' }), 'find_files');
    content(await uiRead('grep', { path: '.', glob: '*.cs', pattern: 'disk-marker', max: '10' }), 'grep');
    report.explicitUiWithoutProvider = true; report.htmlInert = true; report.uiErrorRecovery = true;
    report.layout = await evaluate(`({width:innerWidth,overflow:document.documentElement.scrollWidth>innerWidth,outputOverflow:document.querySelector('#byok-read-result').scrollWidth>document.querySelector('#byok-read-result').clientWidth})`);
    assert.equal(report.layout.overflow, false);
    await command('Emulation.setDeviceMetricsOverride', { width: 203, height: 900, deviceScaleFactor: 1, mobile: false });
    report.narrowLayout = await evaluate(`({width:innerWidth,overflow:document.documentElement.scrollWidth>innerWidth,formOverflow:document.querySelector('#byok-read-form').scrollWidth>document.querySelector('#byok-read-form').clientWidth})`);
    assert.equal(report.narrowLayout.width, 203); assert.equal(report.narrowLayout.overflow, false); assert.equal(report.narrowLayout.formOverflow, false);
    await evaluate(`(()=>{const controls=document.querySelector('.read-tools-controls');controls.scrollTop=controls.scrollHeight;})()`);
    const screenshot = await command('Page.captureScreenshot', { format: 'png' });
    await writeFile(new URL('../artifacts/read-tools-webview.png', import.meta.url), Buffer.from(screenshot.data, 'base64'));
    await command('Emulation.clearDeviceMetricsOverride');
  }
  assert.deepEqual(exceptions, []);
  report.javascriptExceptions = exceptions.length;
  await writeFile(new URL(`../artifacts/read-tools-${stage}.json`, import.meta.url), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report));
} finally {
  for (const task of pending.values()) { clearTimeout(task.timer); task.reject(new Error('Verifier closed')); }
  pending.clear(); socket.close();
}
