import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { createHash, randomUUID } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const stage = process.argv[2] || 'verify';
assert.ok(['verify', 'cleanup'].includes(stage));
const manifestPath = new URL('../artifacts/context-fixtures.json', import.meta.url);
const manifest = JSON.parse((await readFile(manifestPath, 'utf8')).replace(/^\uFEFF/, ''));
const pages = await (await fetch('http://127.0.0.1:9223/json/list')).json();
const page = pages.find(value => value.url === 'https://lumen.local/index.html');
assert.ok(page, 'Open Lumen in the experimental instance.');
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
const pending = new Map(), exceptions = [];
let sequence = 0;
socket.addEventListener('message', event => {
  const value = JSON.parse(event.data);
  if (value.method === 'Runtime.exceptionThrown') exceptions.push(value.params.exceptionDetails.text);
  const task = pending.get(value.id);
  if (!task) return;
  pending.delete(value.id); clearTimeout(task.timer);
  if (value.error) task.reject(new Error(value.error.message)); else task.resolve(value.result);
});
function command(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++sequence, timer = setTimeout(() => { pending.delete(id); reject(new Error(method)); }, 30000);
    pending.set(id, { resolve, reject, timer }); socket.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const result = await command('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
  return result.result.value;
}
const wait = predicate => evaluate(`new Promise((resolve,reject)=>{
  const check=()=>{if(${predicate}){clearTimeout(timer);observer.disconnect();resolve(true);}};
  const observer=new MutationObserver(check),timer=setTimeout(()=>{observer.disconnect();reject(new Error('UI not ready'));},15000);
  observer.observe(document,{subtree:true,attributes:true,childList:true,characterData:true});check();
})`);
const idle = () => wait("!document.querySelector('#byok-history-new').disabled");
async function rpc(type, payload) {
  await idle();
  return evaluate(`new Promise((resolve,reject)=>{
    const id=crypto.randomUUID(),timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('RPC missing'));},15000);
    const handler=e=>{if(e.data.id!==id||e.data.type==='byok.event')return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve(e.data);};
    chrome.webview.addEventListener('message',handler);chrome.webview.postMessage({version:1,id,type:${JSON.stringify(type)},payload:${JSON.stringify(payload)}});
  })`);
}
async function action(type, expression) {
  await idle();
  return evaluate(`new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('UI result missing'));},15000);
    const handler=e=>{if(e.data.type!==${JSON.stringify(type)}&&e.data.type!=='error')return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);if(e.data.type==='error')reject(new Error(e.data.payload.code));else resolve(e.data.payload);};
    chrome.webview.addEventListener('message',handler);${expression}
  })`);
}
const stats = async () => (await (await fetch('http://127.0.0.1:4184/fixture/stats')).json());
const saveManifest = () => writeFile(manifestPath, JSON.stringify(manifest, null, 2));
const hash = text => createHash('sha256').update(text).digest('hex');
async function capture(kind, extra = {}) {
  const result = await rpc('byok.context.capture', { kind, ...extra });
  assert.equal(result.type, 'byok.context.capture.result', JSON.stringify(result));
  return result.payload.context;
}
try {
  await command('Runtime.enable'); await command('Page.enable');
  await wait("document.querySelector('#connection-status')?.dataset.state==='connected'");
  if (stage === 'cleanup') {
    for (const id of manifest.conversationIds) {
      const result = await rpc('byok.history.delete', { conversationId: id, confirm: true });
      assert.ok(result.type === 'byok.history.delete.result' || result.payload.code === 'conversation_not_found');
    }
    if (manifest.configurationId) {
      const listed = await rpc('byok.list', {});
      if (listed.payload.configurations.some(value => value.id === manifest.configurationId)) assert.equal((await rpc('byok.remove', { configurationId: manifest.configurationId })).type, 'byok.remove.result');
    }
    console.log('Owned context provider, credential and conversations removed.');
  } else {
    const report = { source: 'real experimental editor and WebView2, synthetic loopback provider', startedAt: new Date().toISOString() };
    const before = await stats();
    const file = await capture('file', { path: 'Sample.cs' });
    assert.ok(file.text.includes(`${manifest.marker}-unsaved`)); assert.ok(!file.text.includes('disk-marker'));
    assert.ok((await readFile(path.join(manifest.root, 'Sample.cs'), 'utf8')).includes('disk-marker'));
    const activeFile = await capture('file');
    assert.equal(activeFile.text, file.text, 'Empty file path must capture the same active unsaved editor buffer.');
    const selection = await capture('selection');
    assert.ok(selection.text.includes(`${manifest.marker}-unsaved`)); assert.ok(!selection.text.includes('public class'));
    assert.ok(/:2(?:-|$)/.test(selection.label));
    const solution = await capture('solution');
    assert.ok(solution.text.includes('Sample.cs')); assert.ok(!solution.text.includes(`${manifest.marker}-unsaved`));
    const output = await capture('output');
    assert.ok(output.text.includes(`${manifest.marker}-output`));
    for (const [payload, expected] of [
      [{ kind: 'file', path: '../STATUS.md' }, 'context_outside_workspace'],
      [{ kind: 'file', path: 'Oversized.txt' }, 'context_too_large'],
      [{ kind: 'file', path: 'Sample.cs:secret' }, 'context_outside_workspace'],
      [{ kind: 'solution', path: 'Sample.cs' }, 'invalid_message']
    ]) {
      const result = await rpc('byok.context.capture', payload);
      assert.equal(result.type, 'error'); assert.equal(result.payload.code, expected);
    }
    const binary = await rpc('byok.context.capture', { kind: 'file', path: 'Binary.bin' });
    assert.equal(binary.type, 'error');
    assert.deepEqual(await stats(), before, 'Capturing context must not call any provider.');
    assert.equal((await rpc('byok.context.release', { ids: [file.id, activeFile.id, selection.id, solution.id, output.id] })).type, 'byok.context.release.result');
    report.unsavedBuffer = true; report.selectionLines = selection.label; report.inventoryOnly = true; report.output = true; report.pathNegative = true; report.sizeNegative = true; report.binaryNegative = true; report.captureHttpUnchanged = true;

    manifest.configurationId = randomUUID(); await saveManifest();
    const configured = await rpc('byok.configure', { id: manifest.configurationId, providerId: manifest.marker, kind: 'OpenRouter', baseUrl: 'http://127.0.0.1:4184/', key: 'synthetic-context-webview-key' });
    assert.equal(configured.type, 'byok.configure.result');
    await evaluate("document.querySelector('#byok-panel').open=true;");
    await wait("!document.querySelector('#byok-list').disabled");
    await action('byok.list.result', "document.querySelector('#byok-list').click();");
    await action('byok.discover.result', `const providers=document.querySelector('#byok-providers');providers.value=${JSON.stringify(manifest.configurationId)};providers.dispatchEvent(new Event('change'));`);
    await action('byok.select.result', "const models=document.querySelector('#byok-models');models.value='fixture/plain';models.dispatchEvent(new Event('change'));");
    await action('byok.history.new.result', "document.querySelector('#byok-history-new').click();");
    await evaluate("document.querySelector('#byok-panel').open=false;");

    const controls = await evaluate("[...document.querySelectorAll('[id^=byok-context]')].map(value=>({id:value.id,tag:value.tagName,text:value.textContent}))");
    report.controls = controls.map(value => value.id);
    await writeFile(new URL('../artifacts/context-controls.json', import.meta.url), JSON.stringify(controls, null, 2));
    const uiFile = await action('byok.context.capture.result', "document.querySelector('#byok-context').open=true;document.querySelector('#byok-context-path').value='Sample.cs';document.querySelector('#byok-context-file').click();");
    await action('byok.context.capture.result', "document.querySelector('#byok-context-output').click();");
    await action('byok.context.release.result', "document.querySelector('#byok-context-list li:last-child button').click();");
    await idle();
    assert.equal(await evaluate("document.querySelectorAll('#byok-context-list li').length"), 1);
    report.uiRemoval = true;
    const uiInventory = await action('byok.context.capture.result', "document.querySelector('#byok-context-path').value='@solução';document.querySelector('#byok-context-reference').click();");
    assert.equal(uiInventory.context.kind, 'solution');
    assert.ok(uiInventory.context.text.includes('Sample.cs'));
    assert.ok(!uiInventory.context.text.includes(`${manifest.marker}-unsaved`));
    await action('byok.context.release.result', "document.querySelector('#byok-context-list li:last-child button').click();");
    report.uiReference = true;
    const uiSelection = await action('byok.context.capture.result', "document.querySelector('#byok-context-selection').click();");
    assert.ok(uiFile.context.text.includes(`${manifest.marker}-unsaved`)); assert.ok(uiSelection.context.text.includes(`${manifest.marker}-unsaved`));
    await idle();
    const contextText = [uiFile.context, uiSelection.context].map(value => `[Context: ${value.label}]\n${value.text}`).join('\n\n');
    assert.ok(await evaluate(`document.querySelector('#byok-context-list').textContent.includes(${JSON.stringify(manifest.marker + '-unsaved')})`));
    const beforeSend = await stats();
    assert.equal(beforeSend.chatRequests, before.chatRequests);
    const sent = await action('byok.chat.result', `const input=document.querySelector('#byok-message');input.value=${JSON.stringify(manifest.marker + ' explain attached context')};input.dispatchEvent(new Event('input'));document.querySelector('#byok-send').click();`);
    assert.equal(sent.completed, true);
    manifest.conversationIds.push(sent.conversation.id); await saveManifest();
    const after = await stats();
    assert.equal(after.chatRequests, beforeSend.chatRequests + 1);
    assert.equal(after.probeRequests, before.probeRequests);
    assert.ok(after.chatTextHashes.at(-1).at(-1).includes(hash(contextText)), 'Actual provider wire must contain precisely the captured context.');
    const archive = JSON.parse(await readFile(path.join(process.env.LOCALAPPDATA, 'Lumen', 'conversations', `${sent.conversation.id}.json`), 'utf8'));
    assert.equal(archive.conversation.messages[0].parts[1].text, contextText);
    assert.ok(!JSON.stringify(archive).includes('synthetic-context-webview-key'));
    const reopened = await rpc('byok.history.open', { conversationId: sent.conversation.id, offset: 0, limit: 10 });
    assert.equal(reopened.payload.turns[0].contextText, contextText);
    report.uiSend = true; report.wireHash = hash(contextText); report.archivePreserved = true; report.probes = 0; report.paidServices = 0;

    await command('Emulation.setDeviceMetricsOverride', { width: 900, height: 900, deviceScaleFactor: 1, mobile: false });
    await evaluate("(()=>{document.querySelector('#byok-context').open=false;const context=document.querySelector('.context-history');if(context)context.open=true;window.scrollTo(0,0);return new Promise(resolve=>requestAnimationFrame(()=>{document.querySelector('#byok-output').scrollTop=0;requestAnimationFrame(resolve);}));})()");
    const screenshot = await command('Page.captureScreenshot', { format: 'png' });
    await writeFile(new URL('../artifacts/context-webview.png', import.meta.url), Buffer.from(screenshot.data, 'base64'));
    const stale = await capture('file', { path: 'Sample.cs' });
    await promisify(execFile)('powershell.exe', ['-NoProfile', '-File', fileURLToPath(new URL('./context-experimental.ps1', import.meta.url)), '-Action', 'Switch', '-InstanceId', '25e762c6']);
    const beforeStale = await stats();
    const rejected = await rpc('byok.chat', { configurationId: manifest.configurationId, text: 'must not reach provider', effort: 'Off', contextIds: [stale.id] });
    assert.equal(rejected.type, 'error'); assert.equal(rejected.payload.code, 'context_stale');
    assert.deepEqual(await stats(), beforeStale); report.staleNegative = true;
    report.exceptions = exceptions; assert.deepEqual(exceptions, []);
    await writeFile(new URL('../artifacts/context-webview.json', import.meta.url), JSON.stringify(report, null, 2));
    console.log(JSON.stringify(report, null, 2));
  }
} finally {
  await command('Emulation.clearDeviceMetricsOverride'); socket.close();
}
