import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, writeFile, unlink } from 'node:fs/promises';
import { watch } from 'node:fs';
import { randomUUID, createHash } from 'node:crypto';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

const stage = process.argv[2] || 'verify';
assert.ok(['verify', 'modes', 'plan-budget', 'loop-resilience', 'acceptance', 'acceptance-resume', 'resume', 'cleanup'].includes(stage));
const manifestPath = new URL('../artifacts/context-fixtures.json', import.meta.url);
const manifest = JSON.parse((await readFile(manifestPath, 'utf8')).replace(/^\uFEFF/, ''));
const outsidePath = path.join(path.dirname(manifest.root), `${path.basename(manifest.root)}-owned-outside.txt`);
const requests = [], sockets = new Set();
let loopRetryFailed = false;
let streamReached;
const streamReady = new Promise(resolve => { streamReached = resolve; });
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
    response.end(JSON.stringify({ data: [
      { id: 'fixture/auto', architecture: { input_modalities: ['text'] }, supported_parameters: ['tools'], context_length: 32768 },
      { id: 'fixture/small', architecture: { input_modalities: ['text'] }, supported_parameters: ['tools'], context_length: 8192 },
      { id: 'fixture/parallel', architecture: { input_modalities: ['text'] }, supported_parameters: ['tools', 'parallel_tool_calls'], context_length: 32768 }
    ] })); return;
  }
  if (request.method !== 'POST' || request.url !== '/chat/completions') { response.writeHead(404); response.end(); return; }
  let bytes = 0; const chunks = [];
  for await (const chunk of request) { bytes += chunk.length; if (bytes > 8 * 1024 * 1024) { response.writeHead(413); response.end(); return; } chunks.push(chunk); }
  const payload = JSON.parse(Buffer.concat(chunks).toString('utf8'));
  requests.push(payload);
  const earlyUser = payload.messages.findLastIndex(message => message.role === 'user');
  const earlyText = textOf(payload.messages[earlyUser]);
  const earlyResults = payload.messages.slice(earlyUser + 1).filter(message => message.role === 'tool');
  // Transient 429 after the tool already ran: the loop must retry without re-executing it.
  if (earlyText === 'fixture-agent-loop-retry' && earlyResults.length === 1 && !loopRetryFailed) {
    loopRetryFailed = true;
    response.writeHead(429, { 'Retry-After': '1', 'Content-Type': 'application/json' }); response.end('{}'); return;
  }
  response.writeHead(200, { 'Content-Type': 'text/event-stream' });
  const userIndex = payload.messages.findLastIndex(message => message.role === 'user');
  const text = textOf(payload.messages[userIndex]);
  const results = payload.messages.slice(userIndex + 1).filter(message => message.role === 'tool');
  if (text === 'fixture-agent-acceptance-stream') {
    response.write(`data: ${JSON.stringify({ choices: [{ index: 0, delta: { content: 'stream-start' }, finish_reason: null }] })}\n\n`);
    response.write(': waiting for cancellation\n\n');
    streamReached(); return;
  }
  if (text === 'fixture-agent-patch' || text === 'fixture-agent-patch-negative') {
    if (!results.length) { calls(response, [{ name: 'get_edit_snapshot', args: { path: 'Sample.cs' } }], requests.length); return; }
    if (results.length === 1) {
      const snapshot = JSON.parse(results[0].content);
      calls(response, [{ name: 'apply_diff', args: { path: 'Sample.cs', base_sha256: text.endsWith('-negative') ? '0'.repeat(64) : snapshot.base_sha256,
        replacements: [{ old: 'public class ContextFixture', new: 'public class PatchedFixture' }] } }], requests.length); return;
    }
  }
  if (text === 'fixture-plan-budget-plan') { answer(response, 'Plan: phase 1 inspect Sample.cs; acceptance criteria: cite the file.'); return; }
  if (text === 'fixture-plan-budget-loop') { calls(response, [{ name: 'read_file', args: { path: 'Sample.cs' } }], requests.length); return; }
  if (text === 'fixture-plan-budget-done' && !results.length) { calls(response, [{ name: 'read_file', args: { path: 'Sample.cs' } }], requests.length); return; }
  if (text === 'fixture-agent-loop-retry') {
    if (!results.length) { calls(response, [{ name: 'read_file', args: { path: 'Sample.cs' } }], requests.length); return; }
    answer(response, 'retry recovered without re-executing the tool'); return;
  }
  if (text === 'fixture-agent-loop-parallel') {
    if (!results.length) { calls(response, [{ name: 'read_file', args: { path: 'Sample.cs' } }, { name: 'read_file', args: { path: 'ContextFixture.csproj' } }], requests.length); return; }
    answer(response, `parallel results received: ${results.length}`); return;
  }
  if (results.length) { answer(response, `Agent tool results received: ${results.length} <script>window.lumenAgentExecuted=true</script><img src=x onerror="window.lumenAgentExecuted=true">`); return; }
  if (text === 'fixture-agent-acceptance-outside') {
    calls(response, [{ name: 'edit_file', args: { path: outsidePath, content: 'outside-after\n' } }], requests.length); return;
  }
  if (text === 'fixture-agent-acceptance-tests') {
    calls(response, [{ name: 'run_tests', args: { path: 'Acceptance.Tests.csproj', timeout_ms: 60000 } }], requests.length); return;
  }
  if (text === 'fixture-agent-acceptance-errors') {
    calls(response, [{ name: 'get_errors', args: {} }], requests.length); return;
  }
  if (text === 'fixture-agent-acceptance-tool') {
    calls(response, [{ name: 'run_command', args: { command: 'powershell.exe', args: ['-NoProfile', '-NonInteractive', '-File', 'Wait.ps1'], timeout_ms: 60000 } }], requests.length); return;
  }
  if (text === 'fixture-agent-diagnostics') {
    calls(response, [{ name: 'get_errors', args: {} }, { name: 'get_output_logs', args: {} }, { name: 'detect_shells', args: {} }], requests.length); return;
  }
  if (text === 'fixture-agent-write') {
    calls(response, [{ name: 'edit_file', args: { path: 'Sample.cs', content: 'public class AgentEdited {}\r\n' } },
      { name: 'create_file', args: { path: 'AgentCreated.txt', content: 'created-by-agent\n' } }], requests.length); return;
  }
  if (text === 'fixture-agent-write-second') {
    calls(response, [{ name: 'edit_file', args: { path: 'Sample.cs', content: 'public class AgentSecond {}\r\n' } },
      { name: 'edit_file', args: { path: 'AgentCreated.txt', content: 'second-turn\n' } }], requests.length); return;
  }
  if (text === 'fixture-agent-command') { calls(response, [command], requests.length); return; }
  if (text === 'fixture-agent-build' || text === 'fixture-agent-tests') {
    calls(response, [{ name: text === 'fixture-agent-build' ? 'run_build' : 'run_tests', args: { path: 'ContextFixture.csproj' } }], requests.length); return;
  }
  if (text === 'fixture-agent-negative' || text === 'fixture-mode-plan' || text === 'fixture-mode-ask') {
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
async function agentChat(text, checkboxes, decision, mode = checkboxes.some(id => id === 'auto-write-tools' || id === 'auto-terminal') ? 'Agent' : 'Ask') {
  await idle();
  await evaluate(`(()=>{const mode=document.querySelector('#byok-mode');mode.value=${JSON.stringify(mode)};mode.dispatchEvent(new Event('change'));})()`);
  const setters = checkboxes.map(id => `const c_${id.replace(/-/g, '_')}=document.querySelector('#byok-${id}');c_${id.replace(/-/g, '_')}.checked=true;c_${id.replace(/-/g, '_')}.dispatchEvent(new Event('change'));`).join('');
  const onApproval = decision === 'cancel'
    ? "approval={category:e.data.payload.category};document.querySelector('#byok-cancel').click();"
    : `const buttons=card.querySelectorAll('button');approval={toolName:e.data.payload.toolName,category:e.data.payload.category,arguments:e.data.payload.arguments,targetPath:e.data.payload.targetPath,buttonCount:buttons.length,canAllowSession:e.data.payload.canAllowSession};buttons[${decision === 'none' ? 0 : decision}].click();${decision === 'none' ? 'approvalUnexpected=true;' : ''}`;
  return evaluate(`new Promise((resolve,reject)=>{
   const events=[],sequences=[];let approval=null,approvalUnexpected=false,settled=false;
   const timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('agent chat timeout; stall='+JSON.stringify(window.__lumenStall||null)+'; wire='+JSON.stringify((window.__lumenWire||[]).slice(-6))));},30000);
   const handler=e=>{
     if(e.data.type==='byok.event'){events.push(e.data.payload);sequences.push(e.data.sequence);
       if(e.data.payload.kind==='ApprovalRequest'){setTimeout(()=>{const card=[...document.querySelectorAll('#byok-output .byok-approval')].pop();${onApproval}},0);}
       return;}
     if(e.data.type==='byok.chat.result'||e.data.type==='error'){settled=true;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve({...e.data,events,sequences,approval,approvalUnexpected});}
   };
   chrome.webview.addEventListener('message',handler);
   const input=document.querySelector('#byok-message');input.value=${JSON.stringify(text)};input.dispatchEvent(new Event('input'));
   ${setters}
   document.querySelector('#byok-send').click();
   setTimeout(()=>{if(!settled&&events.length===0){window.__lumenStall={sendDisabled:document.querySelector('#byok-send').disabled,live:document.querySelector('#byok-live').children.length,error:document.querySelector('#byok-error')?.textContent||null,mode:document.querySelector('#byok-mode').value,inputLen:input.value.length,autoWrite:document.querySelector('#byok-auto-write-tools').checked,autoTerminal:document.querySelector('#byok-auto-terminal').checked,wire:(window.__lumenWire||[]).slice(-6)};}},2500);})`);
}
const report = { startedAt: new Date().toISOString(), source: 'real VS Exp/WebView2 + owned loopback provider; no paid APIs' };
const root = manifest.root;
const sample = path.join(root, 'Sample.cs');
try {
  await command_('Runtime.enable'); await command_('Page.enable');
  await wait("document.querySelector('#connection-status')?.dataset.state==='connected'");
  await evaluate(`(()=>{if(window.__lumenWire)return true;window.__lumenWire=[];const original=chrome.webview.postMessage.bind(chrome.webview);chrome.webview.postMessage=message=>{try{window.__lumenWire.push({type:message&&message.type,id:message&&message.id,at:Math.round(performance.now())});if(window.__lumenWire.length>512)window.__lumenWire.shift();}catch{}return original(message);};return true;})()`);
  if (stage !== 'cleanup') {
    let port = 0;
    if (stage === 'resume' || stage === 'acceptance-resume') {
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
    if (manifest.outsideFile) {
      assert.equal(path.resolve(manifest.outsideFile), path.resolve(outsidePath));
      await unlink(outsidePath).catch(error => { if (error.code !== 'ENOENT') throw error; });
    }
    console.log('Owned agent-tools provider, credential, conversations and external fixture removed.');
  } else {
    const diagnosticBackup = path.join(root, 'Sample.cs.lumen-fixture-backup');
    const diagnosticPending = stage === 'acceptance-resume' && await readFile(diagnosticBackup).then(() => true, error => { if (error.code === 'ENOENT') return false; throw error; });
    const beforeDisk = await readFile(diagnosticPending ? diagnosticBackup : sample);
    const originalBuffer = diagnosticPending ? null : await automation('WriteInspect');
    if (originalBuffer) assert.equal(originalBuffer.saved, false);
    for (const id of ['auto-write-tools', 'auto-terminal']) assert.equal(await evaluate(`document.querySelector('#byok-${id}').checked`), false);
    if (stage === 'verify' || stage === 'modes' || stage === 'plan-budget' || stage === 'loop-resilience' || stage === 'acceptance') {
      assert.equal(manifest.configurationId, null, 'Do not overwrite a fixture provider from an interrupted run.');
      manifest.configurationId = randomUUID(); await saveManifest();
    } else if (stage === 'resume') {
      assert.ok(manifest.configurationId && manifest.conversationIds.length);
      assert.ok(originalBuffer.text.includes(manifest.marker));
      await assert.rejects(readFile(path.join(root, 'AgentCreated.txt')));
      await assert.rejects(readFile(path.join(root, 'ManualBefore.txt')));
      await evaluate("document.querySelector('#byok-write-tools').open=true");
      await wait("document.querySelector('#byok-write-pending').textContent.includes('0 / 64')");
      await evaluate("document.querySelector('#byok-write-tools').open=false");
      report.resumedAfter = 'manual + two automatic batches, isolated undo, terminal deny and allow-once passed before OutputInspect COM correction';
    } else {
      assert.ok(manifest.configurationId && manifest.outsideFile);
      assert.equal(path.resolve(manifest.outsideFile), path.resolve(outsidePath));
      assert.equal(await readFile(outsidePath, 'utf8'), 'outside-before\n');
      if (originalBuffer) assert.ok(originalBuffer.text.includes(manifest.marker));
      report.resumedAfter = 'large text input, external deny/allow/undo and passing/failing dotnet tests passed before correcting the fixture build path';
    }
    const configured = await rpc('byok.configure', { id: manifest.configurationId, providerId: manifest.marker, kind: 'OpenRouter', baseUrl: `http://127.0.0.1:${server.address().port}/`, key: 'synthetic-agent-tool-key' });
    assert.equal(configured.type, 'byok.configure.result');
    await evaluate("document.querySelector('#byok-panel').open=true");
    await action('byok.list.result', "document.querySelector('#byok-list').click()");
    await action('byok.discover.result', `const p=document.querySelector('#byok-providers');p.value=${JSON.stringify(manifest.configurationId)};p.dispatchEvent(new Event('change'));`);
    await action('byok.select.result', "const m=document.querySelector('#byok-models');m.value='fixture/auto';m.dispatchEvent(new Event('change'));");
    await action('byok.history.new.result', "document.querySelector('#byok-history-new').click()");
    await evaluate("document.querySelector('#byok-panel').open=false");
    await wait("document.querySelector('#byok-auto-read-tools').disabled===false");

    if (stage === 'acceptance' || stage === 'acceptance-resume') {
      if (stage === 'acceptance') {
      await evaluate("document.querySelector('#byok-message').value='';document.querySelector('#byok-message').focus()");
      const pasteStart = performance.now();
      await command_('Input.insertText', { text: 'x'.repeat(1024 * 1024) });
      report.largeInputMs = Math.round(performance.now() - pasteStart);
      assert.ok(report.largeInputMs < 3000, `Large input took ${report.largeInputMs} ms.`);
      const inputState = await evaluate("({length:document.querySelector('#byok-message').value.length,max:document.querySelector('#byok-message').maxLength})");
      assert.equal(inputState.length, 4096); assert.equal(inputState.max, 4096);
      assert.equal(await evaluate("(()=>{const input=document.querySelector('#byok-message');input.value='x'.repeat(4097);input.dispatchEvent(new Event('input'));return document.querySelector('#byok-send').disabled})()"), true);
      await evaluate("(()=>{const input=document.querySelector('#byok-message');input.value='';input.dispatchEvent(new Event('input'));})()");
      report.largeTextInputAndNegative = true;

      await writeFile(outsidePath, 'outside-before\n', { flag: 'wx' });
      manifest.outsideFile = outsidePath; await saveManifest();
      const outsideDenied = await agentChat('fixture-agent-acceptance-outside', ['auto-write-tools'], 0);
      assert.equal(outsideDenied.type, 'byok.chat.result');
      assert.equal(outsideDenied.approval.category, 'WriteOutsideWorkspace');
      assert.equal(outsideDenied.approval.canAllowSession, false);
      assert.equal(outsideDenied.approval.buttonCount, 2);
      assert.equal(JSON.parse(currentResults(requests.at(-1))[0].content).code, 'approval_denied');
      assert.equal(await readFile(outsidePath, 'utf8'), 'outside-before\n');
      const outsideAllowed = await agentChat('fixture-agent-acceptance-outside', ['auto-write-tools'], 1);
      assert.equal(outsideAllowed.type, 'byok.chat.result');
      assert.equal(path.resolve(outsideAllowed.approval.targetPath).toLowerCase(), path.resolve(outsidePath).toLowerCase());
      assert.equal(await readFile(outsidePath, 'utf8'), 'outside-after\n');
      await evaluate("document.querySelector('#byok-write-tools').open=true");
      await wait("document.querySelector('#byok-write-pending').textContent.includes('1 / 64')");
      const outsideUndo = await action('byok.tool.write.undo.result', "document.querySelector('#byok-write-undo').click()");
      assert.equal(outsideUndo.type, 'byok.tool.write.undo.result'); assert.equal(outsideUndo.payload.pending, 0);
      assert.equal(await readFile(outsidePath, 'utf8'), 'outside-before\n');
      report.outsideApprovalDenialAndUndo = true;

      const project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.11.0"/><PackageReference Include="xunit" Version="2.9.3"/><PackageReference Include="xunit.runner.visualstudio" Version="4.0.1" PrivateAssets="all"/><Compile Include="AcceptanceTests.cs"/></ItemGroup></Project>';
      await writeFile(path.join(root, 'Acceptance.Tests.csproj'), project, { flag: 'wx' });
      await writeFile(path.join(root, 'AcceptanceTests.cs'), 'using Xunit; public class AcceptanceTests { [Fact] public void Fixture() => Assert.Equal(2, 1 + 1); }', { flag: 'wx' });
      await promisify(execFile)('dotnet', ['restore', path.join(root, 'Acceptance.Tests.csproj'), '--source', root, '--no-http-cache'], { timeout: 60000 });
      const testsPositive = await agentChat('fixture-agent-acceptance-tests', ['auto-terminal'], 1);
      assert.equal(testsPositive.type, 'byok.chat.result', JSON.stringify(testsPositive));
      assert.equal(testsPositive.approval.toolName, 'run_tests');
      const successfulTests = JSON.parse(currentResults(requests.at(-1))[0].content);
      assert.equal(successfulTests.exitCode, 0);
      assert.match(successfulTests.stdout, /Aprovado|Passed/i);
      await writeFile(path.join(root, 'AcceptanceTests.cs'), 'using Xunit; public class AcceptanceTests { [Fact] public void Fixture() => Assert.Equal(3, 1 + 1); }');
      const testsNegative = await agentChat('fixture-agent-acceptance-tests', ['auto-terminal'], 1);
      assert.equal(testsNegative.type, 'byok.chat.result');
      const failedTests = JSON.parse(currentResults(requests.at(-1))[0].content);
      assert.notEqual(failedTests.exitCode, 0);
      assert.match(failedTests.stdout + failedTests.stderr, /fail|falha/i);
      report.approvedTestsAndNegative = true;
      }
      if (!diagnosticPending) await automation('BuildFixtureError');
      try {
        const diagnostic = await agentChat('fixture-agent-acceptance-errors', ['auto-read-tools'], 'none');
        assert.equal(diagnostic.type, 'byok.chat.result');
        const errors = JSON.parse(currentResults(requests.at(-1))[0].content).errors;
        assert.ok(errors.some(error => error.description.includes('lumen-acceptance-diagnostic')), JSON.stringify(errors));
        assert.ok(!errors.some(error => error.description.includes('lumen-nonexistent-diagnostic')));
        report.populatedErrorListAndNegative = true;
      } finally { await automation('RestoreFixtureError'); }

      const streaming = agentChat('fixture-agent-acceptance-stream', [], 'none', 'Ask');
      await Promise.race([streamReady, new Promise((_, reject) => { const timer = setTimeout(() => reject(new Error('Stream did not start')), 15000); timer.unref(); })]);
      const streamCancelStart = performance.now();
      await evaluate("document.querySelector('#byok-cancel').click()");
      const streamCancelled = await streaming; await idle();
      report.streamCancelMs = Math.round(performance.now() - streamCancelStart);
      assert.equal(streamCancelled.type, 'error'); assert.equal(streamCancelled.payload.code, 'cancelled');
      assert.ok(report.streamCancelMs < 1000, `Stream cancellation took ${report.streamCancelMs} ms.`);
      assert.deepEqual(streamCancelled.sequences, streamCancelled.sequences.map((_, index) => index + 1));
      report.streamCancellation = true;

      await writeFile(path.join(root, 'Wait.ps1'), "[IO.File]::WriteAllText('process.marker', [string]$PID)\nStart-Sleep -Seconds 30\n[IO.File]::WriteAllText('unexpected-survivor', 'bad')\n", { flag: 'wx' });
      let signalStarted;
      const started = new Promise(resolve => { signalStarted = resolve; });
      const watcher = watch(root, (_, name) => { if (String(name) === 'process.marker') signalStarted(); });
      try {
        const running = agentChat('fixture-agent-acceptance-tool', ['auto-terminal'], 1);
        await Promise.race([started, new Promise((_, reject) => { const timer = setTimeout(() => reject(new Error('Controlled tool did not start')), 15000); timer.unref(); })]);
        const cancelStart = performance.now();
        await evaluate("document.querySelector('#byok-cancel').click()");
        const stopped = await running; await idle();
        report.toolCancelMs = Math.round(performance.now() - cancelStart);
        assert.equal(stopped.type, 'error'); assert.equal(stopped.payload.code, 'cancelled');
        assert.ok(report.toolCancelMs < 1000, `Tool cancellation took ${report.toolCancelMs} ms.`);
        assert.deepEqual(stopped.sequences, stopped.sequences.map((_, index) => index + 1));
        const pid = Number(await readFile(path.join(root, 'process.marker'), 'utf8'));
        assert.ok(Number.isSafeInteger(pid) && pid > 0);
        let alive = true;
        try { process.kill(pid, 0); } catch (error) { if (error.code !== 'ESRCH') throw error; alive = false; }
        assert.equal(alive, false, 'Cancelled test process is still alive.');
        await assert.rejects(readFile(path.join(root, 'unexpected-survivor')));
      } finally { watcher.close(); }
      report.toolCancellationAndProcessExit = true;
      assert.deepEqual(exceptions, []);
      await collectOwnedConversations();
      report.javascriptExceptions = 0; report.providerRequests = requests.length; report.paidRequests = 0;
      await writeFile(new URL('../artifacts/phase34-acceptance.json', import.meta.url), JSON.stringify(report, null, 2));
      console.log(JSON.stringify(report));
    } else if (stage === 'modes') {
      const initialMode = await evaluate("({mode:document.querySelector('#byok-mode').value,writeDisabled:document.querySelector('#byok-auto-write-tools').disabled,terminalDisabled:document.querySelector('#byok-auto-terminal').disabled})");
      assert.deepEqual(initialMode, { mode: 'Ask', writeDisabled: true, terminalDisabled: true });
      await evaluate("document.querySelector('#byok-write-tools').open=true");
      await wait("document.querySelector('#byok-write-pending').textContent.includes('0 / 64')");
      const agent = await agentChat('fixture-agent-write', ['auto-write-tools'], 'none', 'Agent');
      assert.equal(agent.type, 'byok.chat.result', JSON.stringify(agent));
      assert.equal(agent.approval, null);
      assert.equal((await automation('WriteInspect')).text.replaceAll('\r\n', '\n'), 'public class AgentEdited {}\n');
      assert.deepEqual(await readFile(sample), beforeDisk);
      const undo = await action('byok.tool.write.undo.result', "document.querySelector('#byok-write-undo').click()");
      assert.equal(undo.payload.pending, 0);
      assert.equal((await automation('WriteInspect')).text, originalBuffer.text);
      await assert.rejects(readFile(path.join(root, 'AgentCreated.txt')));
      for (const mode of ['Plan', 'Ask']) {
        const readonly = await agentChat(`fixture-mode-${mode.toLowerCase()}`, ['auto-read-tools'], 'none', mode);
        assert.equal(readonly.type, 'byok.chat.result', JSON.stringify(readonly));
        assert.equal(readonly.payload.conversation.id, agent.payload.conversation.id);
        assert.equal(readonly.approval, null);
        assert.deepEqual(currentResults(requests.at(-1)).map(value => JSON.parse(value.content).code), ['unknown_tool', 'unknown_tool']);
        const instruction = textOf(requests.at(-2).messages[0]);
        assert.ok(mode === 'Plan' ? instruction.includes('acceptance criteria') : instruction.includes('cite paths:lines'));
        assert.equal((await automation('WriteInspect')).text, originalBuffer.text);
      }
      assert.deepEqual(await readFile(sample), beforeDisk);
      await command_('Emulation.setDeviceMetricsOverride', { width: 203, height: 900, deviceScaleFactor: 1, mobile: false });
      assert.equal(await evaluate('document.documentElement.scrollWidth>innerWidth'), false);
      assert.equal(await evaluate("(()=>{const app=document.querySelector('.app');app.style.minWidth='240px';const rejected=document.documentElement.scrollWidth>innerWidth;app.style.removeProperty('min-width');return rejected})()"), true);
      await command_('Emulation.clearDeviceMetricsOverride');
      assert.deepEqual(exceptions, []);
      await collectOwnedConversations();
      report.agentWriteAndUndo = true; report.readOnlyModesAndHistory = true; report.layoutNegativeDetected = true;
      report.javascriptExceptions = 0; report.providerRequests = requests.length; report.paidRequests = 0;
      await writeFile(new URL('../artifacts/phase5-modes-webview.json', import.meta.url), JSON.stringify(report, null, 2));
      console.log(JSON.stringify(report));
    } else if (stage === 'plan-budget') {
      const pt = JSON.parse(await readFile(new URL('../src/Lumen.Vsix/wwwroot/locales/pt-BR.json', import.meta.url), 'utf8'));
      const en = JSON.parse(await readFile(new URL('../src/Lumen.Vsix/wwwroot/locales/en.json', import.meta.url), 'utf8'));
      const setBudget = (rounds, tokens, tools) => evaluate(`(()=>{for(const [id,value] of [['budget-rounds',${rounds}],['budget-tokens',${tokens}],['budget-tools',${tools}]]){const input=document.querySelector('#byok-'+id);input.value=String(value);input.dispatchEvent(new Event('input'));}})()`);
      const initial = await evaluate("({mode:document.querySelector('#byok-mode').value,applyHidden:document.querySelector('#byok-apply-plan').hidden,rounds:document.querySelector('#byok-budget-rounds').value,tokens:document.querySelector('#byok-budget-tokens').value,tools:document.querySelector('#byok-budget-tools').value})");
      assert.deepEqual(initial, { mode: 'Ask', applyHidden: true, rounds: '4', tokens: '8192', tools: '8' });
      await evaluate("(()=>{const mode=document.querySelector('#byok-mode');mode.value='Agent';mode.dispatchEvent(new Event('change'));})()");
      assert.equal(await evaluate("document.querySelector('#byok-apply-plan').hidden"), true, 'apply plan must stay hidden outside Plan');
      const plan = await agentChat('fixture-plan-budget-plan', ['auto-read-tools'], 'none', 'Plan');
      assert.equal(plan.type, 'byok.chat.result', JSON.stringify(plan));
      assert.ok(textOf(requests.at(-1).messages[0]).includes('acceptance criteria'));
      const planId = plan.payload.conversation.id;
      assert.deepEqual(await evaluate("({hidden:document.querySelector('#byok-apply-plan').hidden,disabled:document.querySelector('#byok-apply-plan').disabled})"), { hidden: false, disabled: false });
      await evaluate("(()=>{const consent=document.querySelector('#byok-auto-read-tools');consent.checked=true;consent.dispatchEvent(new Event('change'));})()");
      const beforeApply = requests.length;
      await evaluate("document.querySelector('#byok-apply-plan').click()");
      assert.deepEqual(await evaluate("({mode:document.querySelector('#byok-mode').value,hidden:document.querySelector('#byok-apply-plan').hidden,consent:document.querySelector('#byok-auto-read-tools').checked})"), { mode: 'Agent', hidden: true, consent: false });
      assert.equal(requests.length, beforeApply, 'apply plan must not call the provider');
      assert.ok([pt.byokApplyPlanDone, en.byokApplyPlanDone].includes(await evaluate("document.querySelector('#announcement').textContent")));
      report.applyPlanSwitchWithoutRequests = true;
      await setBudget(1, 8192, 8);
      const bounded = await agentChat('fixture-plan-budget-loop', ['auto-read-tools'], 'none', 'Agent');
      assert.equal(bounded.type, 'error', JSON.stringify(bounded));
      assert.equal(bounded.payload.code, 'tool_budget_exceeded');
      assert.equal(requests.length, beforeApply + 1, 'rounds=1 must stop after a single provider request');
      report.roundBudgetEnforced = true;
      for (const budget of [{ maxRounds: 99, maxOutputTokens: 8192, maxInvocations: 8 }, { maxRounds: 4, maxOutputTokens: 8192.5, maxInvocations: 8 }]) {
        const forged = await rpc('byok.chat', { configurationId: manifest.configurationId, text: 'forged-budget', effort: 'Off', mode: 'Ask', readTools: true, budget });
        assert.equal(forged.type, 'error'); assert.equal(forged.payload.code, 'invalid_message');
      }
      assert.equal(requests.length, beforeApply + 1, 'forged budgets must never reach the provider');
      report.forgedBudgetRejected = true;
      await setBudget(0, 8192, 8);
      await evaluate("(()=>{const input=document.querySelector('#byok-message');input.value='blocked';input.dispatchEvent(new Event('input'));})()");
      assert.equal(await evaluate("document.querySelector('#byok-send').disabled"), true, 'out-of-range budget must block sending');
      await evaluate("document.querySelector('#byok-send').click()");
      assert.equal(requests.length, beforeApply + 1);
      await setBudget(2, 2048, 8);
      const done = await agentChat('fixture-plan-budget-done', ['auto-read-tools'], 'none', 'Agent');
      assert.equal(done.type, 'byok.chat.result', JSON.stringify(done));
      assert.equal(done.payload.completed, true);
      assert.equal(done.payload.conversation.id, planId, 'apply plan and failed turns must preserve the conversation');
      assert.equal(requests.length, beforeApply + 3);
      report.customBudgetCompletesAndHistoryPreserved = true;
      await command_('Emulation.setDeviceMetricsOverride', { width: 203, height: 900, deviceScaleFactor: 1, mobile: false });
      assert.equal(await evaluate('document.documentElement.scrollWidth>innerWidth'), false);
      assert.equal(await evaluate("(()=>{const app=document.querySelector('.app');app.style.minWidth='240px';const rejected=document.documentElement.scrollWidth>innerWidth;app.style.removeProperty('min-width');return rejected})()"), true);
      await command_('Emulation.clearDeviceMetricsOverride');
      assert.deepEqual(exceptions, []);
      await collectOwnedConversations();
      report.layoutNegativeDetected = true;
      report.javascriptExceptions = 0; report.providerRequests = requests.length; report.paidRequests = 0;
      await writeFile(new URL('../artifacts/phase5-plan-budget-webview.json', import.meta.url), JSON.stringify(report, null, 2));
      console.log(JSON.stringify(report));
    } else if (stage === 'loop-resilience') {
      const selectModel = async id => {
        await evaluate("document.querySelector('#byok-panel').open=true");
        await action('byok.select.result', `const m=document.querySelector('#byok-models');m.value=${JSON.stringify(id)};m.dispatchEvent(new Event('change'));`);
        await evaluate("document.querySelector('#byok-panel').open=false");
      };
      const newHistory = () => action('byok.history.new.result', "document.querySelector('#byok-history-new').click()");
      // A. Compaction: 20 turns of ~320 chars overflow the 8192-token window of fixture/small.
      await selectModel('fixture/small');
      await newHistory();
      let compacted = null;
      for (let index = 0; index < 20; index++) {
        const chat = await agentChat(`fixture-agent-loop-history-${index} ${'h'.repeat(300)}`, [], 'none', 'Ask');
        assert.equal(chat.type, 'byok.chat.result', JSON.stringify(chat));
        assert.equal(chat.payload.completed, true);
        if (chat.events.some(event => event.kind === 'Notice' && event.code === 'history_compacted')) compacted = chat;
      }
      assert.ok(compacted, 'expected a history_compacted notice within 20 turns');
      assert.equal(requests[0].messages.length, 2);
      const lastCount = requests.at(-1).messages.length;
      assert.ok(lastCount < 41 && lastCount >= 3, `expected compaction, got ${lastCount} messages`);
      report.historyCompaction = true;
      // B. Transient 429 after the tool ran: retry must not re-execute it.
      await newHistory();
      const beforeRetry = requests.length;
      const retry = await agentChat('fixture-agent-loop-retry', ['auto-read-tools'], 'none', 'Ask');
      assert.equal(retry.type, 'byok.chat.result', JSON.stringify(retry));
      assert.equal(retry.payload.completed, true);
      assert.equal(requests.length - beforeRetry, 3, 'tool round + 429 + retried answer');
      assert.equal(retry.events.filter(event => event.kind === 'ToolExecution' && event.status === 'Completed').length, 1, 'the tool must execute exactly once');
      assert.ok(retry.events.some(event => event.kind === 'Notice' && event.code === 'retrying'));
      const retryResults = currentResults(requests.at(-1));
      assert.equal(retryResults.length, 1);
      assert.ok(retryResults[0].content.includes('ContextFixture'), 'the executed tool result must survive the retry');
      const retryCalls = requests.at(-1).messages.filter(message => message.role === 'assistant' && message.tool_calls);
      assert.equal(retryCalls.length, 1);
      assert.equal(retryCalls[0].tool_calls[0].id, retryResults[0].tool_call_id, 'compaction/retry must keep the tool_call paired with its result');
      report.transientRetryWithoutReexecution = true;
      // C. Two read-only calls overlap only with the ParallelTools capability; order is preserved.
      await selectModel('fixture/parallel');
      await newHistory();
      const beforeParallel = requests.length;
      const parallel = await agentChat('fixture-agent-loop-parallel', ['auto-read-tools'], 'none', 'Agent');
      assert.equal(parallel.type, 'byok.chat.result', JSON.stringify(parallel));
      assert.equal(parallel.payload.completed, true);
      assert.equal(requests.length - beforeParallel, 2);
      const completed = parallel.events.filter(event => event.kind === 'ToolExecution' && event.status === 'Completed');
      assert.equal(completed.length, 2);
      assert.deepEqual(completed.map(event => event.id), parallel.events.filter(event => event.kind === 'ToolCallEnd').map(event => event.id));
      const parallelResults = currentResults(requests.at(-1));
      assert.equal(parallelResults.length, 2);
      assert.ok(parallelResults[0].content.includes('ContextFixture'));
      assert.ok(parallelResults[1].content.length > 0);
      assert.notEqual(parallelResults[0].content, parallelResults[1].content);
      // Without the capability the same batch still completes, sequentially (overlap timing is proven by the C# MaxActive tests).
      await selectModel('fixture/auto');
      await newHistory();
      const sequential = await agentChat('fixture-agent-loop-parallel', ['auto-read-tools'], 'none', 'Agent');
      assert.equal(sequential.type, 'byok.chat.result', JSON.stringify(sequential));
      assert.equal(sequential.payload.completed, true);
      assert.equal(sequential.events.filter(event => event.kind === 'ToolExecution' && event.status === 'Completed').length, 2);
      report.parallelToolsGated = true;
      assert.deepEqual(exceptions, []);
      await collectOwnedConversations();
      report.javascriptExceptions = 0; report.providerRequests = requests.length; report.paidRequests = 0;
      await writeFile(new URL('../artifacts/phase5-loop-resilience-webview.json', import.meta.url), JSON.stringify(report, null, 2));
      console.log(JSON.stringify(report));
    } else {
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
    assert.deepEqual(requests[0].tools.map(tool => tool.function.name).sort(), ['apply_diff', 'create_file', 'detect_shells', 'edit_file', 'find_files', 'get_edit_snapshot', 'get_errors', 'get_output_logs', 'grep', 'list_dir', 'read_file']);
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
    const patch = await agentChat('fixture-agent-patch', ['auto-write-tools'], 'none');
    assert.equal(patch.type, 'byok.chat.result', JSON.stringify(patch));
    assert.equal(patch.approval, null);
    const patchResults = currentResults(requests.at(-1)).map(value => JSON.parse(value.content));
    assert.equal(patchResults.length, 2);
    assert.equal(patchResults[0].source, 'buffer');
    assert.ok(patchResults[0].text.includes(manifest.marker));
    assert.equal(patchResults[0].base_sha256, createHash('sha256').update(patchResults[0].text, 'utf8').digest('hex'));
    assert.equal(patchResults[1].edited, true);
    assert.ok((await automation('WriteInspect')).text.includes('public class PatchedFixture'));
    assert.deepEqual(await readFile(sample), beforeDisk);
    const patchUndo = await action('byok.tool.write.undo.result', "document.querySelector('#byok-write-undo').click()");
    assert.equal(patchUndo.type, 'byok.tool.write.undo.result'); assert.equal(patchUndo.payload.pending, 0);
    assert.equal((await automation('WriteInspect')).text, originalBuffer.text);
    const patchNegative = await agentChat('fixture-agent-patch-negative', ['auto-write-tools'], 'none');
    assert.equal(patchNegative.type, 'byok.chat.result');
    const negativePatchResults = currentResults(requests.at(-1)).map(value => JSON.parse(value.content));
    assert.equal(negativePatchResults[1].code, 'edit_conflict');
    assert.equal((await automation('WriteInspect')).text, originalBuffer.text);
    assert.deepEqual(await readFile(sample), beforeDisk);
    report.bufferSnapshotPatchUndo = true; report.invalidPatchHashRejected = true;
    for (const mode of ['Ask', 'Plan']) {
      const modeChat = await agentChat(`fixture-mode-${mode.toLowerCase()}`, ['auto-read-tools'], 'none', mode);
      assert.equal(modeChat.type, 'byok.chat.result', JSON.stringify(modeChat));
      assert.equal(modeChat.payload.conversation.id, write.payload.conversation.id);
      assert.equal(modeChat.approval, null);
      assert.deepEqual(currentResults(requests.at(-1)).map(value => JSON.parse(value.content).code), ['unknown_tool', 'unknown_tool']);
      const initial = requests.at(-2);
      const instruction = textOf(initial.messages[0]);
      assert.ok(mode === 'Plan' ? instruction.includes('acceptance criteria') : instruction.includes('cite paths:lines'));
      assert.ok(!initial.tools.some(tool => ['edit_file', 'run_command'].includes(tool.function.name)));
      assert.equal((await automation('WriteInspect')).text, originalBuffer.text);
    }
    report.readOnlyModesAndHistory = true;
    const diagnostics = await agentChat('fixture-agent-diagnostics', ['auto-read-tools'], 'none');
    assert.equal(diagnostics.type, 'byok.chat.result', JSON.stringify(diagnostics));
    assert.equal(diagnostics.approval, null);
    const diagnosticResults = currentResults(requests.at(-1)).map(value => JSON.parse(value.content));
    assert.equal(diagnosticResults.length, 3);
    assert.ok(Array.isArray(diagnosticResults[0].errors));
    assert.ok(diagnosticResults[1].text.includes(manifest.marker));
    assert.ok(diagnosticResults[2].shells.some(shell => shell.name === 'cmd'));
    report.diagnosticsAndShellDetection = true;
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
    const buildDeny = await agentChat('fixture-agent-build', ['auto-terminal'], 0);
    assert.equal(buildDeny.type, 'byok.chat.result');
    assert.equal(buildDeny.approval.toolName, 'run_build');
    assert.equal(buildDeny.approval.arguments.command, 'dotnet');
    assert.equal(buildDeny.approval.arguments.args[0], 'build');
    assert.ok(buildDeny.approval.arguments.args[1].endsWith('ContextFixture.csproj'));
    assert.equal(buildDeny.approval.arguments.args[2], '--no-restore');
    assert.equal(JSON.parse(currentResults(requests.at(-1))[0].content).code, 'approval_denied');
    const testsDeny = await agentChat('fixture-agent-tests', ['auto-terminal'], 0);
    assert.equal(testsDeny.type, 'byok.chat.result');
    assert.equal(testsDeny.approval.toolName, 'run_tests');
    assert.equal(testsDeny.approval.arguments.args[0], 'test');
    assert.equal(JSON.parse(currentResults(requests.at(-1))[0].content).code, 'approval_denied');
    const buildAllow = await agentChat('fixture-agent-build', ['auto-terminal'], 1);
    assert.equal(buildAllow.type, 'byok.chat.result', JSON.stringify(buildAllow));
    assert.equal(buildAllow.approval.toolName, 'run_build');
    assert.equal(typeof JSON.parse(currentResults(requests.at(-1))[0].content).exitCode, 'number');
    report.buildAndTestsApproval = true;
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
    assert.deepEqual(requests.at(-2).tools.map(tool => tool.function.name).sort(), ['detect_shells', 'find_files', 'get_edit_snapshot', 'get_errors', 'get_output_logs', 'grep', 'list_dir', 'read_file']);
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
    const closed = await rpc('byok.chat', { configurationId: manifest.configurationId, text: 'fixture-agent-closed', effort: 'Off', mode: 'Agent', terminal: true });
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
  }
} finally {
  try { await command_('Emulation.clearDeviceMetricsOverride'); } catch {}
  for (const task of pending.values()) clearTimeout(task.timer);
  socket.close(); for (const owned of sockets) owned.destroy(); await new Promise(resolve => server.close(resolve));
}