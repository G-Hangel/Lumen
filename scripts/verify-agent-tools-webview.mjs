import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, writeFile } from 'node:fs/promises';
import { randomUUID, createHash } from 'node:crypto';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

const stage = process.argv[2] || 'verify';
assert.ok(['verify', 'resume', 'cleanup'].includes(stage));
const manifestPath = new URL('../artifacts/context-fixtures.json', import.meta.url);
const manifest = JSON.parse((await readFile(manifestPath, 'utf8')).replace(/^\uFEFF/, ''));
const requests = [], sockets = new Set();
const textOf = message => typeof message.content === 'string' ? message.content : (message.content ?? []).map(part => part.text ?? '').join('');
const command = { name: 'run_command', args: { command: 'cmd', args: ['/c', 'echo', 'lumen-agent-marker'] } };
const currentResults = payload => payload.messages.slice(payload.messages.findLastIndex(message => message.role === 'user') + 1).filter(message => message.role === 'tool');
function answer(response, text) {
  response.end(`data: ${JSON.stringify({ choices: [{ index: 0, delta: { content: text }, finish_reason: 'stop' }] })}\n\ndata: [DONE]\n\n`);
}
function calls(response, tools, index) {
  response.end(`data: ${JSON.stringify({ choices: [{ index: 0, delta: { tool_calls: tools.map((tool, position) => ({ index: position, id: `agent-${index}-${position}`, type: 'function', function: { name: tool.name, arguments: JSON.stringify(tool.args) } })) }, finish_reason: 'tool_calls' }] })}\n\ndata: [DONE]\n\n`);
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
  const userIndex = payload.messages.findLastIndex(message => message.role === 'user');
  const text = textOf(payload.messages[userIndex]);
  const results = payload.messages.slice(userIndex + 1).filter(message => message.role === 'tool');
  if (results.length) { answer(response, `Agent tool results received: ${results.length} <script>window.lumenAgentExecuted=true</script><img src=x onerror="window.lumenAgentExecuted=true">`); return; }
  if (text === 'fixture-agent-write') {
    calls(response, [{ name: 'edit_file', args: { path: 'Sample.cs', content: 'public class AgentEdited {}\r\n' } },
      { name: 'create_file', args: { path: 'AgentCreated.txt', content: 'created-by-agent\n' } }], requests.length); return;
  }
  if (text === 'fixture-agent-write-second') {
    calls(response, [{ name: 'edit_file', args: { path: 'Sample.cs', content: 'public class AgentSecond {}\r\n' } },
      { name: 'edit_file', args: { path: 'AgentCreated.txt', content: 'second-turn\n' } }], requests.length); return;
  }
  if (text === 'fixture-agent-command') { calls(response, [command], requests.length); return; }
  if (text === 'fixture-agent-negative') {
    calls(response, [{ name: 'edit_file', args: { path: 'Sample.cs', content: 'forged' } }, command], requests.length); return;
  }
  if (text === 'fixture-agent-outside') {
    calls(response, [{ name: 'edit_file', args: { path: 'C:/Windows/Temp/lumen-agent-outside.txt', content: 'forged' } }], requests.length); return;
  }
  if (text === 'fixture-agent-cancel') { calls(response, [command], requests.length); return; }
  answer(response, 'Unexpected fixture text.');
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
function command_(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++sequence, timer = setTimeout(() => { pending.delete(id); reject(new Error(method)); }, 30000);
    pending.set(id, { resolve, reject, timer }); socket.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const result = await command_('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails)); return result.result.value;
}
const wait = predicate => evaluate(`new Promise((resolve,reject)=>{
 const check=()=>{if(${predicate}){clearTimeout(timer);observer.disconnect();resolve(true);}};
 const observer=new MutationObserver(check),timer=setTimeout(()=>{observer.disconnect();reject(new Error('UI not ready'));},15000);
 observer.observe(document,{subtree:true,attributes:true,childList:true,characterData:true});check();})`);
const idle = () => wait("!document.querySelector('#byok-history-new').disabled");
async function action(type, expression) {
  await idle();
  return evaluate(`new Promise((resolve,reject)=>{
   const events=[],timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('UI action timeout'));},20000);
   const handler=e=>{if(e.data.type==='byok.event'){events.push(e.data.payload);return;}if(e.data.type!==${JSON.stringify(type)}&&e.data.type!=='error')return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve({...e.data,events});};
   chrome.webview.addEventListener('message',handler);(()=>{${expression}})();})`);
}
async function rpc(type, payload) {
  await idle();
  return evaluate(`new Promise((resolve,reject)=>{
   const id=crypto.randomUUID(),events=[],timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('RPC timeout'));},20000);
   const handler=e=>{if(e.data.id!==id)return;if(e.data.type==='byok.event'){events.push(e.data.payload);return;}clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve({...e.data,events});};
   chrome.webview.addEventListener('message',handler);chrome.webview.postMessage({version:1,id,type:${JSON.stringify(type)},payload:${JSON.stringify(payload)}});})`);
}
const automation = async action => {
  const { stdout } = await promisify(execFile)('powershell.exe', ['-NoProfile', '-File', fileURLToPath(new URL('./context-experimental.ps1', import.meta.url)), '-Action', action, '-InstanceId', '25e762c6']);
  return ['WriteInspect', 'OutputInspect'].includes(action) ? JSON.parse(stdout.trim()) : stdout;
};
const saveManifest = () => writeFile(manifestPath, JSON.stringify(manifest, null, 2));
async function collectOwnedConversations() {
  if (!manifest.configurationId) return;
  const listed = await rpc('byok.history.list', { query: 'fixture-agent' });
  assert.equal(listed.type, 'byok.history.list.result');
  for (const value of listed.payload.conversations.filter(value => value.configurationId === manifest.configurationId)) {
    if (!manifest.conversationIds.includes(value.id)) manifest.conversationIds.push(value.id);
  }
  await saveManifest();
}
// decision: 'none' (no approval expected; auto-deny if one appears), 0/1/2 (button index), or 'cancel'.
async function agentChat(text, checkboxes, decision) {
  await idle();
  const setters = checkboxes.map(id => `const c_${id.replace(/-/g, '_')}=document.querySelector('#byok-${id}');c_${id.replace(/-/g, '_')}.checked=true;c_${id.replace(/-/g, '_')}.dispatchEvent(new Event('change'));`).join('');
  const onApproval = decision === 'cancel'
    ? "approval={category:e.data.payload.category};document.querySelector('#byok-cancel').click();"
    : `const buttons=card.querySelectorAll('button');approval={toolName:e.data.payload.toolName,category:e.data.payload.category,buttonCount:buttons.length,canAllowSession:e.data.payload.canAllowSession};buttons[${decision === 'none' ? 0 : decision}].click();${decision === 'none' ? 'approvalUnexpected=true;' : ''}`;
  return evaluate(`new Promise((resolve,reject)=>{
   const events=[];let approval=null,approvalUnexpected=false;
   const timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('agent chat timeout'));},30000);
   const handler=e=>{
     if(e.data.type==='byok.event'){events.push(e.data.payload);
       if(e.data.payload.kind==='ApprovalRequest'){setTimeout(()=>{const card=[...document.querySelectorAll('#byok-output .byok-approval')].pop();${onApproval}},0);}
       return;}
     if(e.data.type==='byok.chat.result'||e.data.type==='error'){clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve({...e.data,events,approval,approvalUnexpected});}
   };
   chrome.webview.addEventListener('message',handler);
   const input=document.querySelector('#byok-message');input.value=${JSON.stringify(text)};input.dispatchEvent(new Event('input'));
   ${setters}
   document.querySelector('#byok-send').click();})`);
}
const report = { startedAt: new Date().toISOString(), source: 'real VS Exp/WebView2 + owned loopback provider; no paid APIs' };
const root = manifest.root;
const sample = path.join(root, 'Sample.cs');
try {
  await command_('Runtime.enable'); await command_('Page.enable');
  await wait("document.querySelector('#connection-status')?.dataset.state==='connected'");
  if (stage !== 'cleanup') {
    let port = 0;
    if (stage === 'resume') {
      const listed = await rpc('byok.list', {});
      const configuration = listed.payload.configurations.find(value => value.id === manifest.configurationId);
      assert.ok(configuration);
      const endpoint = new URL(configuration.baseUrl);
      assert.equal(endpoint.hostname, '127.0.0.1');
      port = Number(endpoint.port);
      assert.ok(port > 0);
    }
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(port, '127.0.0.1', resolve); });
  }
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
    console.log('Owned agent-tools provider, credential and conversations removed.');
  } else {
    const beforeDisk = await readFile(sample);
    const originalBuffer = await automation('WriteInspect');
    assert.equal(originalBuffer.saved, false);
    for (const id of ['auto-write-tools', 'auto-terminal']) assert.equal(await evaluate(`document.querySelector('#byok-${id}').checked`), false);
    if (stage === 'verify') {
      assert.equal(manifest.configurationId, null, 'Do not overwrite a fixture provider from an interrupted run.');
      manifest.configurationId = randomUUID(); await saveManifest();
    } else {
      assert.ok(manifest.configurationId && manifest.conversationIds.length);
      assert.ok(originalBuffer.text.includes(manifest.marker));
      await assert.rejects(readFile(path.join(root, 'AgentCreated.txt')));
      await assert.rejects(readFile(path.join(root, 'ManualBefore.txt')));
      await evaluate("document.querySelector('#byok-write-tools').open=true");
      await wait("document.querySelector('#byok-write-pending').textContent.includes('0 / 64')");
      await evaluate("document.querySelector('#byok-write-tools').open=false");
      report.resumedAfter = 'manual + two automatic batches, isolated undo, terminal deny and allow-once passed before OutputInspect COM correction';
    }
    const configured = await rpc('byok.configure', { id: manifest.configurationId, providerId: manifest.marker, kind: 'OpenRouter', baseUrl: `http://127.0.0.1:${server.address().port}/`, key: 'synthetic-agent-tool-key' });
    assert.equal(configured.type, 'byok.configure.result');
    await evaluate("document.querySelector('#byok-panel').open=true");
    await action('byok.list.result', "document.querySelector('#byok-list').click()");
    await action('byok.discover.result', `const p=document.querySelector('#byok-providers');p.value=${JSON.stringify(manifest.configurationId)};p.dispatchEvent(new Event('change'));`);
    await action('byok.select.result', "const m=document.querySelector('#byok-models');m.value='fixture/auto';m.dispatchEvent(new Event('change'));");
    await action('byok.history.new.result', "document.querySelector('#byok-history-new').click()");
    await evaluate("document.querySelector('#byok-panel').open=false");
    await wait("document.querySelector('#byok-auto-terminal').disabled===false");

    let writeConversationId = manifest.conversationIds[0];
    if (stage === 'verify') {
    // 1. Model-requested writes inside the solution auto-apply with diff + batch undo.
    await evaluate("document.querySelector('#byok-write-tools').open=true");
    await wait("document.querySelector('#byok-write-pending').textContent.includes('0 / 64')");
    await idle();
    const manual = await action('byok.tool.write.result', "document.querySelector('#byok-write-name').value='create_file';document.querySelector('#byok-write-path').value='ManualBefore.txt';document.querySelector('#byok-write-content').value='manual-batch\\n';document.querySelector('#byok-write-path').dispatchEvent(new Event('input'));document.querySelector('#byok-write-content').dispatchEvent(new Event('input'));document.querySelector('#byok-write-apply').click();");
    assert.equal(manual.type, 'byok.tool.write.result', JSON.stringify(manual)); assert.equal(manual.payload.pending, 1); assert.equal(manual.payload.isError, false);
    const write = await agentChat('fixture-agent-write', ['auto-write-tools'], 'none');
    assert.equal(write.type, 'byok.chat.result', JSON.stringify(write)); assert.equal(write.payload.completed, true);
    assert.equal(write.approvalUnexpected, false); assert.equal(write.approval, null);
    manifest.conversationIds.push(write.payload.conversation.id); await saveManifest();
    assert.deepEqual(requests[0].tools.map(tool => tool.function.name).sort(), ['create_file', 'edit_file', 'find_files', 'grep', 'list_dir', 'read_file']);
    assert.equal(write.events.filter(event => event.kind === 'ToolExecution' && event.status === 'Completed').length, 2);
    assert.equal((await automation('WriteInspect')).text.replaceAll('\r\n', '\n'), 'public class AgentEdited {}\n');
    assert.deepEqual(await readFile(sample), beforeDisk, 'open document edits the buffer, not disk');
    assert.equal(await readFile(path.join(root, 'AgentCreated.txt'), 'utf8'), 'created-by-agent\n');
    await wait("document.querySelector('#byok-write-pending').textContent.includes('3 / 64')");
    assert.ok((await evaluate("document.querySelector('#byok-output').textContent")).includes('AgentEdited'));
    report.modelWriteAutoApply = true; report.bufferNotDisk = true; report.visibleDiff = true;
    await idle();
    const secondWrite = await agentChat('fixture-agent-write-second', ['auto-write-tools'], 'none');
    assert.equal(secondWrite.type, 'byok.chat.result'); assert.equal(secondWrite.approval, null);
    assert.equal((await automation('WriteInspect')).text.replaceAll('\r\n', '\n'), 'public class AgentSecond {}\n');
    assert.equal(await readFile(path.join(root, 'AgentCreated.txt'), 'utf8'), 'second-turn\n');
    const latestUndo = await action('byok.tool.write.undo.result', "document.querySelector('#byok-write-undo').click()");
    assert.equal(latestUndo.type, 'byok.tool.write.undo.result', JSON.stringify(latestUndo)); assert.equal(latestUndo.payload.pending, 3);
    assert.equal((await automation('WriteInspect')).text.replaceAll('\r\n', '\n'), 'public class AgentEdited {}\n');
    assert.equal(await readFile(path.join(root, 'AgentCreated.txt'), 'utf8'), 'created-by-agent\n');
    report.undoLatestTurnPreservesPrevious = true;
    await evaluate("document.querySelector('#byok-write-tools').open=true");
    const undone = await action('byok.tool.write.undo.result', "document.querySelector('#byok-write-undo').click()");
    assert.equal(undone.type, 'byok.tool.write.undo.result'); assert.equal(undone.payload.pending, 1);
    assert.equal((await automation('WriteInspect')).text, originalBuffer.text);
    await assert.rejects(readFile(path.join(root, 'AgentCreated.txt')));
    assert.equal(await readFile(path.join(root, 'ManualBefore.txt'), 'utf8'), 'manual-batch\n');
    const manualUndo = await action('byok.tool.write.undo.result', "document.querySelector('#byok-write-undo').click()");
    assert.equal(manualUndo.type, 'byok.tool.write.undo.result'); assert.equal(manualUndo.payload.pending, 0);
    await assert.rejects(readFile(path.join(root, 'ManualBefore.txt')));
    report.batchUndo = true; report.manualBatchIsolatedFromAgent = true;
    await evaluate("document.querySelector('#byok-write-tools').open=false");

    // 2. Terminal: deny reinjects approval_denied; allow once executes; allow session persists.
    await idle();
    const deny = await agentChat('fixture-agent-command', ['auto-terminal'], 0);
    assert.equal(deny.type, 'byok.chat.result'); assert.equal(deny.approval.category, 'ExecuteCommand');
    assert.equal(deny.approval.canAllowSession, true); assert.equal(deny.approval.buttonCount, 3);
    const denyResult = currentResults(requests.at(-1)).map(message => JSON.parse(message.content).code);
    assert.deepEqual(denyResult, ['approval_denied']); report.commandDenyReinjected = true;
    await idle();
    const once = await agentChat('fixture-agent-command', ['auto-terminal'], 1);
    assert.equal(once.type, 'byok.chat.result'); assert.equal(once.approval.buttonCount, 3);
    const onceResult = JSON.parse(currentResults(requests.at(-1))[0].content);
    assert.equal(onceResult.exitCode, 0); assert.ok(onceResult.stdout.includes('lumen-agent-marker'));
    report.commandAllowOnceExecutes = true;
    writeConversationId = write.payload.conversation.id;
    }
    const commandOutput = (await automation('OutputInspect')).text;
    assert.ok(commandOutput.includes('lumen-agent-marker')); assert.ok(commandOutput.includes('exit 0'));
    report.outputPaneReceivesCommand = true;
    await idle();
    const cancelled = await agentChat('fixture-agent-cancel', ['auto-terminal'], 'cancel');
    assert.ok(cancelled.type === 'error' && cancelled.payload.code === 'cancelled', JSON.stringify(cancelled));
    assert.ok(cancelled.approval, 'approval card appeared before cancel');
    await idle(); report.cancelDuringApproval = true;
    const session = await agentChat('fixture-agent-command', ['auto-terminal'], 2);
    assert.equal(session.type, 'byok.chat.result'); assert.equal(session.approval.buttonCount, 3);
    report.commandAllowSession = true;
    await idle();
    const persisted = await agentChat('fixture-agent-command', ['auto-terminal'], 'none');
    assert.equal(persisted.type, 'byok.chat.result'); assert.equal(persisted.approvalUnexpected, false);
    assert.equal(persisted.approval, null, 'session consent must not re-prompt');
    assert.ok(JSON.parse(currentResults(requests.at(-1))[0].content).stdout.includes('lumen-agent-marker'));
    report.sessionConsentPersists = true;

    // 3. Read-only consent never advertises writes or commands.
    await idle();
    const negative = await agentChat('fixture-agent-negative', ['auto-read-tools'], 'none');
    assert.equal(negative.type, 'byok.chat.result'); assert.equal(negative.approval, null);
    assert.deepEqual(requests.at(-2).tools.map(tool => tool.function.name).sort(), ['find_files', 'grep', 'list_dir', 'read_file']);
    const rejected = currentResults(requests.at(-1)).map(message => JSON.parse(message.content).code);
    assert.deepEqual(rejected, ['unknown_tool', 'unknown_tool']); report.readOnlyNoWriteOrCommand = true;

    // 4. Outside-workspace write requires approval and forbids the session decision.
    await idle();
    const outside = await agentChat('fixture-agent-outside', ['auto-write-tools'], 0);
    assert.equal(outside.type, 'byok.chat.result'); assert.equal(outside.approval.category, 'WriteOutsideWorkspace');
    assert.equal(outside.approval.canAllowSession, false); assert.equal(outside.approval.buttonCount, 2);
    assert.deepEqual(currentResults(requests.at(-1)).map(message => JSON.parse(message.content).code), ['approval_denied']);
    report.outsideWriteApprovalNoSession = true;

    assert.deepEqual(await readFile(sample), beforeDisk);
    assert.equal((await automation('WriteInspect')).text, originalBuffer.text);
    const inert = await evaluate("({script:document.querySelector('#byok-output script')!==null,executed:window.lumenAgentExecuted===true})");
    assert.equal(inert.script, false); assert.equal(inert.executed, false); report.htmlInert = true;
    await collectOwnedConversations();
    const archive = JSON.parse(await readFile(path.join(process.env.LOCALAPPDATA, 'Lumen', 'conversations', `${writeConversationId}.json`), 'utf8'));
    assert.ok(!JSON.stringify(archive).includes('synthetic-agent-tool-key')); report.credentialRedacted = true;

    await automation('Close');
    const beforeClosed = requests.length;
    const closed = await rpc('byok.chat', { configurationId: manifest.configurationId, text: 'fixture-agent-closed', effort: 'Off', terminal: true });
    assert.equal(closed.type, 'error'); assert.equal(closed.payload.code, 'workspace_unavailable'); assert.equal(requests.length, beforeClosed);
    report.closedSolutionNoProviderRequest = true;
    await automation('Initialize');
    await idle();
    const reopened = await agentChat('fixture-agent-command', ['auto-terminal'], 0);
    assert.equal(reopened.type, 'byok.chat.result'); assert.equal(reopened.approval.category, 'ExecuteCommand');
    assert.deepEqual(currentResults(requests.at(-1)).map(message => JSON.parse(message.content).code), ['approval_denied']);
    report.reopenRevokesSessionConsent = true;
    await command_('Emulation.setDeviceMetricsOverride', { width: 203, height: 900, deviceScaleFactor: 1, mobile: false });
    report.narrowLayout = await evaluate('({width:innerWidth,overflow:document.documentElement.scrollWidth>innerWidth})'); assert.equal(report.narrowLayout.overflow, false);
    assert.equal(await evaluate("(()=>{const app=document.querySelector('.app');app.style.minWidth='240px';const rejected=document.documentElement.scrollWidth>innerWidth;app.style.removeProperty('min-width');return rejected})()"), true);
    assert.equal(await evaluate('document.documentElement.scrollWidth>innerWidth'), false); report.layoutNegativeDetected = true;
    await command_('Emulation.clearDeviceMetricsOverride');
    try {
      const shot = await command_('Page.captureScreenshot', { format: 'png' });
      await writeFile(new URL('../artifacts/agent-tools-webview.png', import.meta.url), Buffer.from(shot.data, 'base64'));
      report.screenshot = true;
    } catch { report.screenshot = false; }
    assert.deepEqual(exceptions, []); report.javascriptExceptions = 0; report.providerRequests = requests.length; report.paidRequests = 0;
    await writeFile(new URL('../artifacts/agent-tools-webview.json', import.meta.url), JSON.stringify(report, null, 2)); console.log(JSON.stringify(report));
  }
} finally {
  try { await command_('Emulation.clearDeviceMetricsOverride'); } catch {}
  for (const task of pending.values()) clearTimeout(task.timer);
  socket.close(); for (const owned of sockets) owned.destroy(); await new Promise(resolve => server.close(resolve));
}