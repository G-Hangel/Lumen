import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, writeFile } from 'node:fs/promises';
import { randomUUID, createHash } from 'node:crypto';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

const stage = process.argv[2] || 'verify';
assert.ok(['verify', 'cleanup'].includes(stage));
const manifestPath = new URL('../artifacts/context-fixtures.json', import.meta.url);
const manifest = JSON.parse((await readFile(manifestPath, 'utf8')).replace(/^\uFEFF/, ''));
const requests = [], sockets = new Set();
let cancelReached;
const cancellationReady = new Promise(resolve => { cancelReached = resolve; });
const textOf = message => typeof message.content === 'string' ? message.content : (message.content ?? []).map(part => part.text ?? '').join('');
function answer(response, text) {
  response.end(`data: ${JSON.stringify({ choices: [{ index: 0, delta: { content: text }, finish_reason: 'stop' }] })}\n\ndata: [DONE]\n\n`);
}
function calls(response, tools, index) {
  response.end(`data: ${JSON.stringify({ choices: [{ index: 0, delta: { tool_calls: tools.map((tool, position) => ({ index: position, id: `auto-${index}-${position}`, type: 'function', function: { name: tool.name, arguments: JSON.stringify(tool.args) } })) }, finish_reason: 'tool_calls' }] })}\n\ndata: [DONE]\n\n`);
}
const server = createServer(async (request, response) => {
  if (request.method === 'GET' && request.url === '/models') {
    response.writeHead(200, { 'Content-Type': 'application/json' });
    response.end(JSON.stringify({ data: [{ id: 'fixture/auto', architecture: { input_modalities: ['text'] }, supported_parameters: ['tools'], context_length: 32768 }] })); return;
  }
  if (request.method !== 'POST' || request.url !== '/chat/completions') { response.writeHead(404); response.end(); return; }
  let bytes = 0; const chunks = [];
  for await (const chunk of request) { bytes += chunk.length; if (bytes > 8 * 1024 * 1024) { response.writeHead(413); response.end(); return; } chunks.push(chunk); }
  const payload = JSON.parse(Buffer.concat(chunks).toString('utf8'));
  requests.push(payload);
  response.writeHead(200, { 'Content-Type': 'text/event-stream' });
  const input = payload.messages.filter(message => message.role === 'user').at(-1);
  const text = textOf(input);
  const results = payload.messages.filter(message => message.role === 'tool');
  if (text === 'fixture-auto-followup') { answer(response, 'Follow-up preserved tool history.'); return; }
  if (text === 'fixture-auto-loop') { calls(response, [{ name: 'list_dir', args: { path: '.', maxResults: 1 } }], requests.length); return; }
  if (results.length) {
    if (text === 'fixture-auto-cancel') { response.write(': waiting for cancellation\n\n'); cancelReached(); return; }
    answer(response, `Tool results received: ${results.length}`); return;
  }
  const tools = text === 'fixture-auto-negative'
    ? [{ name: 'read_file', args: { path: '../STATUS.md' } }, { name: 'edit_file', args: { path: 'Sample.cs', text: 'forged' } }]
    : [{ name: 'read_file', args: { path: 'Sample.cs', startLine: 2, endLine: 2 } },
      { name: 'read_file', args: { path: 'InertTools.txt' } },
      { name: 'find_files', args: { path: '.', glob: '*.cs' } },
      { name: 'grep', args: { path: '.', pattern: 'disk-marker', glob: '*.cs' } }];
  calls(response, tools, requests.length);
});
server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); });
const pages = await (await fetch('http://127.0.0.1:9223/json/list')).json();
const page = pages.find(value => value.url === 'https://lumen.local/index.html');
assert.ok(page, 'Open Lumen in the experimental instance.');
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
let sequence = 0;
const pending = new Map(), exceptions = [];
socket.addEventListener('message', event => {
  const value = JSON.parse(event.data);
  if (value.method === 'Runtime.exceptionThrown') exceptions.push(value.params.exceptionDetails.text);
  const task = pending.get(value.id); if (!task) return;
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
  assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails)); return result.result.value;
}
const wait = predicate => evaluate(`new Promise((resolve,reject)=>{
 const check=()=>{if(${predicate}){clearTimeout(timer);observer.disconnect();resolve(true);}};
 const observer=new MutationObserver(check),timer=setTimeout(()=>{observer.disconnect();reject(new Error('UI not ready'));},15000);
 observer.observe(document,{subtree:true,attributes:true,childList:true,characterData:true});check();})`);
const idle = () => wait("!document.querySelector('#byok-history-new').disabled");
async function rpc(type, payload) {
  return evaluate(`new Promise((resolve,reject)=>{
   const id=crypto.randomUUID(),events=[],timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('RPC timeout'));},20000);
   const handler=e=>{if(e.data.id!==id)return;if(e.data.type==='byok.event'){events.push(e.data.payload);return;}clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve({...e.data,events});};
   chrome.webview.addEventListener('message',handler);chrome.webview.postMessage({version:1,id,type:${JSON.stringify(type)},payload:${JSON.stringify(payload)}});})`);
}
async function action(type, expression) {
  await idle();
  return evaluate(`new Promise((resolve,reject)=>{
   const events=[],timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('UI action timeout'));},20000);
   const handler=e=>{if(e.data.type==='byok.event'){events.push(e.data.payload);return;}if(e.data.type!==${JSON.stringify(type)}&&e.data.type!=='error')return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve({...e.data,events});};
   chrome.webview.addEventListener('message',handler);(()=>{${expression}})();})`);
}
const saveManifest = () => writeFile(manifestPath, JSON.stringify(manifest, null, 2));
async function collectOwnedConversations() {
  if (!manifest.configurationId) return;
  const listed = await rpc('byok.history.list', { query: 'fixture-auto' });
  assert.equal(listed.type, 'byok.history.list.result');
  for (const value of listed.payload.conversations.filter(value => value.configurationId === manifest.configurationId)) {
    if (!manifest.conversationIds.includes(value.id)) manifest.conversationIds.push(value.id);
  }
  await saveManifest();
}
async function send(text, enabled = true) {
  return action('byok.chat.result', `const input=document.querySelector('#byok-message');input.value=${JSON.stringify(text)};input.dispatchEvent(new Event('input'));const consent=document.querySelector('#byok-auto-read-tools');consent.checked=${enabled};consent.dispatchEvent(new Event('change'));document.querySelector('#byok-send').click();`);
}
function validateScreenshot(data) {
  const bytes = Buffer.from(data, 'base64');
  assert.ok(bytes.length >= 33, 'Screenshot is too short for a PNG header.');
  assert.deepEqual(bytes.subarray(0, 8), Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
  assert.equal(bytes.toString('ascii', 12, 16), 'IHDR');
  assert.ok(bytes.readUInt32BE(16) > 0 && bytes.readUInt32BE(20) > 0, 'Screenshot dimensions must be positive.');
  return bytes;
}
const report = { startedAt: new Date().toISOString(), source: 'real VS Exp/WebView2 + owned loopback provider; no paid APIs' };
try {
  if (stage === 'verify') await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  await command('Runtime.enable'); await command('Page.enable');
  await wait("document.querySelector('#connection-status')?.dataset.state==='connected'");
  if (stage === 'cleanup') {
    await collectOwnedConversations();
    for (const id of manifest.conversationIds) {
      const reply = await rpc('byok.history.delete', { conversationId: id, confirm: true });
      assert.ok(reply.type === 'byok.history.delete.result' || reply.payload.code === 'conversation_not_found');
    }
    if (manifest.configurationId) {
      const listed = await rpc('byok.list', {});
      if (listed.payload.configurations.some(value => value.id === manifest.configurationId)) assert.equal((await rpc('byok.remove', { configurationId: manifest.configurationId })).type, 'byok.remove.result');
    }
    console.log('Owned automatic-tools provider, credential and conversations removed.');
  } else {
    assert.equal(await evaluate("document.querySelector('#byok-auto-read-tools').checked"), false);
    const before = await readFile(path.join(manifest.root, 'Sample.cs'));
    assert.equal(manifest.configurationId, null, 'Do not overwrite a fixture provider from an interrupted run.');
    manifest.configurationId = randomUUID(); await saveManifest();
    const configured = await rpc('byok.configure', { id: manifest.configurationId, providerId: manifest.marker, kind: 'OpenRouter', baseUrl: `http://127.0.0.1:${server.address().port}/`, key: 'synthetic-auto-tool-key' });
    assert.equal(configured.type, 'byok.configure.result');
    await evaluate("document.querySelector('#byok-panel').open=true");
    await action('byok.list.result', "document.querySelector('#byok-list').click()");
    await action('byok.discover.result', `const p=document.querySelector('#byok-providers');p.value=${JSON.stringify(manifest.configurationId)};p.dispatchEvent(new Event('change'));`);
    await action('byok.select.result', "const m=document.querySelector('#byok-models');m.value='fixture/auto';m.dispatchEvent(new Event('change'));");
    await action('byok.history.new.result', "document.querySelector('#byok-history-new').click()");
    await evaluate("document.querySelector('#byok-panel').open=false");
    const good = await send('fixture-auto-positive');
    assert.equal(good.type, 'byok.chat.result', JSON.stringify(good)); assert.equal(good.payload.completed, true);
    manifest.conversationIds.push(good.payload.conversation.id); await saveManifest(); await idle();
    assert.equal(requests.length, 2);
    assert.deepEqual(requests[0].tools.map(tool => tool.function.name).sort(), ['detect_shells', 'find_files', 'get_edit_snapshot', 'get_errors', 'get_output_logs', 'grep', 'list_dir', 'read_file']);
    const wireResults = requests[1].messages.filter(message => message.role === 'tool');
    assert.equal(wireResults.length, 4);
    assert.ok(wireResults[0].content.includes('disk-marker')); assert.ok(!wireResults[0].content.includes(manifest.marker + '-unsaved'));
    assert.ok(JSON.parse(wireResults[1].content).lines.some(line => line.text.includes('<script>')));
    const callIndex = requests[1].messages.findIndex(message => message.tool_calls?.length);
    assert.equal(requests[1].messages[callIndex + 1].role, 'tool');
    const executions = good.events.filter(event => event.kind === 'ToolExecution');
    assert.equal(executions.filter(event => event.status === 'Completed').length, 4);
    assert.equal(await evaluate("document.querySelector('#byok-auto-read-tools').checked"), false);
    await wait("document.querySelectorAll('#byok-output .byok-tool').length===4");
    const inert = await evaluate("({executed:window.lumenToolExecuted===true,script:document.querySelector('#byok-output script')!==null,text:document.querySelector('#byok-output').textContent})");
    assert.equal(inert.executed, false); assert.equal(inert.script, false); assert.ok(inert.text.includes('script'));
    const archive = JSON.parse(await readFile(path.join(process.env.LOCALAPPDATA, 'Lumen', 'conversations', `${good.payload.conversation.id}.json`), 'utf8'));
    assert.equal(archive.conversation.messages.length, 2);
    assert.equal(archive.conversation.messages[1].parts.filter(part => part.kind === 'toolResult').length, 4);
    assert.ok(!JSON.stringify(archive).includes('synthetic-auto-tool-key'));
    const opened = await rpc('byok.history.open', { conversationId: good.payload.conversation.id, offset: 0, limit: 10 });
    assert.equal(opened.payload.turns[0].tools.filter(tool => tool.status === 'Completed').length, 4);
    await action('byok.history.new.result', "document.querySelector('#byok-history-new').click()");
    await action('byok.history.open.result', `const list=document.querySelector('#byok-history-list');list.value=${JSON.stringify(good.payload.conversation.id)};list.dispatchEvent(new Event('change'));`);
    const followup = await send('fixture-auto-followup', false); assert.equal(followup.payload.completed, true);
    const replayed = requests.at(-1).messages;
    const replayCall = replayed.findIndex(message => message.tool_calls?.length);
    assert.ok(replayCall >= 0); assert.equal(replayed[replayCall + 1].role, 'tool'); assert.equal(replayed.filter(message => message.role === 'tool').length, 4);
    report.automaticReadAndReinjection = true; report.diskNotBuffer = true; report.htmlInert = true; report.historyReplay = true; report.consentReset = true;
    await action('byok.history.new.result', "document.querySelector('#byok-history-new').click()");
    const disabledStart = requests.length;
    const disabled = await send('fixture-auto-optout', false); assert.equal(disabled.type, 'byok.chat.result');
    assert.equal(requests.length, disabledStart + 1); assert.equal(requests.at(-1).tools, undefined);
    assert.equal(disabled.events.filter(event => event.kind === 'ToolExecution').length, 0); report.optoutNoExecution = true;
    await action('byok.history.new.result', "document.querySelector('#byok-history-new').click()");
    const negativeStart = requests.length;
    const negative = await send('fixture-auto-negative'); assert.equal(negative.payload.completed, true);
    const rejected = requests.at(-1).messages.filter(message => message.role === 'tool').map(message => JSON.parse(message.content).code);
    assert.deepEqual(rejected, ['invalid_arguments', 'unknown_tool']); assert.equal(requests.length, negativeStart + 2);
    report.pathAndWriteRejected = true;
    await action('byok.history.new.result', "document.querySelector('#byok-history-new').click()");
    const loopStart = requests.length;
    const loop = await send('fixture-auto-loop');
    assert.ok(loop.type === 'error' && loop.payload.code === 'tool_budget_exceeded' || loop.events.some(event => event.kind === 'Error' && event.code === 'tool_budget_exceeded'));
    assert.ok(requests.length - loopStart <= 4); report.loopBudgetStopped = true;
    await idle();
    await action('byok.history.new.result', "document.querySelector('#byok-history-new').click()");
    await evaluate(`(()=>{const input=document.querySelector('#byok-message');input.value='fixture-auto-cancel';input.dispatchEvent(new Event('input'));document.querySelector('#byok-auto-read-tools').checked=true;window.autoCancelled=new Promise(resolve=>{const h=e=>{if(e.data.type==='error'&&e.data.payload.code==='cancelled'){chrome.webview.removeEventListener('message',h);resolve(e.data);}};chrome.webview.addEventListener('message',h);});document.querySelector('#byok-send').click();})()`);
    await Promise.race([cancellationReady, new Promise((_, reject) => { const timer = setTimeout(() => reject(new Error('Continuation not reached')), 15000); timer.unref(); })]);
    await evaluate("document.querySelector('#byok-cancel').click()");
    assert.equal((await evaluate('window.autoCancelled')).payload.code, 'cancelled'); await idle(); report.cancelAndGateRecovery = true;
    assert.equal(createHash('sha256').update(await readFile(path.join(manifest.root, 'Sample.cs'))).digest('hex'), createHash('sha256').update(before).digest('hex'));
    await collectOwnedConversations();
    await promisify(execFile)('powershell.exe', ['-NoProfile', '-File', fileURLToPath(new URL('./context-experimental.ps1', import.meta.url)), '-Action', 'Close', '-InstanceId', '25e762c6']);
    const beforeClosed = requests.length;
    const closed = await rpc('byok.chat', { configurationId: manifest.configurationId, text: 'fixture-auto-closed', effort: 'Off', readTools: true });
    assert.equal(closed.type, 'error'); assert.equal(closed.payload.code, 'workspace_unavailable'); assert.equal(requests.length, beforeClosed);
    report.closedSolutionNoProviderRequest = true;
    await command('Emulation.setDeviceMetricsOverride', { width: 203, height: 900, deviceScaleFactor: 1, mobile: false });
    report.narrowLayout = await evaluate('({width:innerWidth,overflow:document.documentElement.scrollWidth>innerWidth})'); assert.equal(report.narrowLayout.overflow, false);
    await command('Emulation.clearDeviceMetricsOverride');
    const shot = await command('Page.captureScreenshot', { format: 'png' });
    const screenshot = validateScreenshot(shot.data);
    const corrupted = Buffer.from(screenshot); corrupted[0] = 0;
    assert.throws(() => validateScreenshot(corrupted.toString('base64')), assert.AssertionError);
    report.screenshotNegativeDetected = true;
    await writeFile(new URL('../artifacts/auto-tools-webview.png', import.meta.url), screenshot);
    assert.deepEqual(exceptions, []); report.javascriptExceptions = 0; report.providerRequests = requests.length; report.paidRequests = 0;
    await writeFile(new URL('../artifacts/auto-tools-webview.json', import.meta.url), JSON.stringify(report, null, 2)); console.log(JSON.stringify(report));
  }
} finally {
  try { await command('Emulation.clearDeviceMetricsOverride'); } catch {}
  for (const task of pending.values()) clearTimeout(task.timer);
  socket.close(); for (const owned of sockets) owned.destroy(); await new Promise(resolve => server.close(resolve));
}
