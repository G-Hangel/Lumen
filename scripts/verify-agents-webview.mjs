import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir, writeFile, readFile, rm, access } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import path from 'node:path';

const stage = process.argv[2] || 'verify';
assert.ok(['verify', 'cleanup'].includes(stage), `Unknown stage: ${stage}`);
const artifacts = new URL('../artifacts/', import.meta.url);
await mkdir(artifacts, { recursive: true });
const manifestPath = new URL('agents-fixtures.json', artifacts);
const contextManifest = JSON.parse((await readFile(new URL('context-fixtures.json', artifacts), 'utf8').catch(error => { if (error.code === 'ENOENT') return '{}'; throw error; })).replace(/^\uFEFF/, ''));
const requests = [], sockets = new Set();
const exists = async file => { try { await access(file); return true; } catch { return false; } };
const AGENT_BODY = 'Read the diff and check correctness first.';

const server = createServer(async (request, response) => {
  if (request.method === 'GET' && request.url === '/models') {
    response.writeHead(200, { 'Content-Type': 'application/json' });
    response.end(JSON.stringify({ data: [{ id: 'fixture/auto', architecture: { input_modalities: ['text'] }, supported_parameters: ['tools'], context_length: 32768 }] }));
    return;
  }
  if (request.method !== 'POST' || request.url !== '/chat/completions') { response.writeHead(404); response.end(); return; }
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  const payload = JSON.parse(Buffer.concat(chunks).toString('utf8'));
  requests.push(payload);
  response.writeHead(200, { 'Content-Type': 'text/event-stream' });
  const data = requests.length % 2 === 1
    ? JSON.stringify({ choices: [{ index: 0, delta: { tool_calls: [{ index: 0, id: 'call-agent-' + requests.length, function: {
      name: 'delegate_agent', arguments: JSON.stringify({ name: 'review-helper', task: 'check the diff' })
    } }] }, finish_reason: 'tool_calls' }] })
    : JSON.stringify({ choices: [{ index: 0, delta: { content: 'fixture-agents-ok' }, finish_reason: 'stop' }] });
  response.end(`data: ${data}\n\ndata: [DONE]\n\n`);
});
server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); });

let manifest = null;
if (stage === 'verify') {
  assert.ok(contextManifest.root, 'Run scripts/context-experimental.ps1 -Action Prepare first.');
  assert.equal(contextManifest.configurationId, null, 'A fixture provider from an interrupted run is still configured.');
  assert.equal(await exists(manifestPath), false, 'artifacts/agents-fixtures.json exists; run the cleanup stage first.');
  manifest = { root: contextManifest.root, configurationId: randomUUID(), conversationIds: [], createdFolders: [], initialAgents: null };
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
} else {
  manifest = JSON.parse(await readFile(manifestPath, 'utf8').catch(error => { if (error.code === 'ENOENT') return null; throw error; }));
  assert.ok(manifest, 'Nothing to clean up.');
}

const page = (await (await fetch('http://127.0.0.1:9223/json/list')).json()).find(value => value.url === 'https://lumen.local/index.html');
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
  value.error ? task.reject(new Error(value.error.message)) : task.resolve(value.result);
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
  const observer=new MutationObserver(check),timer=setTimeout(()=>{observer.disconnect();reject(new Error('UI not ready: ${predicate.replace(/'/g, "\\'")}'));},15000);
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
    const id=crypto.randomUUID(),timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('RPC timeout'));},20000);
    const handler=e=>{if(e.data.id!==id)return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve({...e.data});};
    chrome.webview.addEventListener('message',handler);chrome.webview.postMessage({version:1,id,type:${JSON.stringify(type)},payload:${JSON.stringify(payload)}});})`);
}
async function rawRpc(type, payload) {
  return evaluate(`new Promise((resolve,reject)=>{
    const id=crypto.randomUUID(),timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('RPC timeout'));},20000);
    const handler=e=>{if(e.data.id!==id)return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve({...e.data});};
    chrome.webview.addEventListener('message',handler);chrome.webview.postMessage({version:1,id,type:${JSON.stringify(type)},payload:${JSON.stringify(payload)}});})`);
}
const agentState = async () => {
  const listed = await rpc('byok.agents.list', {});
  assert.equal(listed.type, 'byok.agents.list.result', JSON.stringify(listed));
  return listed.payload;
};
const saveManifest = () => writeFile(manifestPath, JSON.stringify(manifest, null, 2));

try {
  await command('Runtime.enable'); await command('Page.enable');
  await wait("document.querySelector('#connection-status')?.dataset.state==='connected'");

  if (stage === 'cleanup') {
    if (manifest.configurationId) {
      const listed = await rawRpc('byok.list', {});
      if (listed.payload.configurations.some(value => value.id === manifest.configurationId)) {
        assert.equal((await rawRpc('byok.remove', { configurationId: manifest.configurationId })).type, 'byok.remove.result');
      }
    }
    const current = await rawRpc('byok.history.list', { query: '' }).catch(() => null);
    for (const id of manifest.conversationIds) {
      const reply = current && await rawRpc('byok.history.delete', { conversationId: id, confirm: true }).catch(() => null);
      if (reply) assert.ok(reply.type === 'byok.history.delete.result' || reply.payload.code === 'conversation_not_found');
    }
    for (const folder of manifest.createdFolders) await rm(folder, { recursive: true, force: true });
    await rm(manifestPath, { force: true });
    console.log('Owned agent fixtures, provider, credential and conversations removed.');
  } else {
    // Reload clears a busy state left by a previous interrupted run; host settings survive.
    try { await evaluate('location.reload()'); } catch { /* execution context is replaced on purpose */ }
    await new Promise(resolve => setTimeout(resolve, 1500));
    await wait("document.querySelector('#connection-status')?.dataset.state==='connected'");
    await idle();
    // A project-local agent, discovered without restarting Visual Studio.
    const folder = path.join(manifest.root, '.lumen', 'agents', 'review-helper');
    manifest.createdFolders.push(folder);
    await mkdir(folder, { recursive: true });
    await writeFile(path.join(folder, 'AGENT.md'), `---\nname: review-helper\ndescription: Reviews code changes for correctness.\n---\n\n${AGENT_BODY}\n`);
    await saveManifest();
    const beforeIds = (await rpc('byok.history.list', { query: '' })).payload.conversations.map(value => value.id);

    const listed = await agentState();
    assert.ok(listed.agents.some(agent => agent.name === 'review-helper' && agent.scope === 'Project' && agent.source === '.lumen/agents'), JSON.stringify(listed.agents));
    assert.deepEqual(listed.diagnostics, []);
    const advertised = await rpc('byok.agents.list', {});
    assert.deepEqual(advertised.payload.agents[0].description, 'Reviews code changes for correctness.');

    // Owned loopback provider; every request stays on 127.0.0.1.
    const configured = await rpc('byok.configure', { id: manifest.configurationId, providerId: 'fixture-agents', kind: 'OpenRouter', baseUrl: `http://127.0.0.1:${server.address().port}/`, key: 'synthetic-agents-key' });
    assert.equal(configured.type, 'byok.configure.result', JSON.stringify(configured));
    await evaluate("document.querySelector('#byok-panel').open=true");
    await action('byok.list.result', "document.querySelector('#byok-list').click()");
    await action('byok.discover.result', `const p=document.querySelector('#byok-providers');p.value=${JSON.stringify(manifest.configurationId)};p.dispatchEvent(new Event('change'));`);
    await action('byok.select.result', "const m=document.querySelector('#byok-models');m.value='fixture/auto';m.dispatchEvent(new Event('change'));");
    await action('byok.history.new.result', "document.querySelector('#byok-history-new').click()");
    await evaluate("document.querySelector('#byok-panel').open=false");

    // CN: delegation stays in Agent mode, including on the raw host path.
    const denied = await rpc('byok.agents.delegate', { name: 'review-helper', task: 'check', mode: 'Plan' });
    assert.equal(denied.type, 'error', JSON.stringify(denied));
    assert.equal(denied.payload.code, 'mode_denied');

    // End-to-end: the model asks for delegate_agent and the host injects the agent body.
    await evaluate("document.querySelector('#byok-mode').value='Agent';document.querySelector('#byok-mode').dispatchEvent(new Event('change'))");
    await wait("!document.querySelector('#byok-auto-read-tools').disabled");
    await evaluate("document.querySelector('#byok-auto-read-tools').checked=true;document.querySelector('#byok-auto-read-tools').dispatchEvent(new Event('change'))");
    const beforeCalls = requests.length;
    const result = await evaluate(`new Promise((resolve,reject)=>{
      const events=[],timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('chat timeout'));},35000);
      const handler=e=>{if(e.data.type==='byok.event'){events.push(e.data.payload);return;}if(e.data.type!=='byok.chat.result'&&e.data.type!=='error')return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve({...e.data,events});};
      chrome.webview.addEventListener('message',handler);
      const input=document.querySelector('#byok-message');input.value='Please review this change';input.dispatchEvent(new Event('input'));
      document.querySelector('#byok-send').click();})`, 40000);
    assert.equal(result.type, 'byok.chat.result', JSON.stringify(result));
    assert.equal(result.payload.completed, true, JSON.stringify(result.payload));
    assert.ok(requests.length >= beforeCalls + 2, 'The model must receive the tool result and produce a final answer.');
    const first = requests[beforeCalls], second = requests[beforeCalls + 1];
    assert.ok((first.tools || []).some(tool => tool.function?.name === 'delegate_agent'), 'delegate_agent must be advertised in Agent mode.');
    const toolResult = JSON.stringify(second.messages);
    assert.ok(toolResult.includes('delegate_agent'), toolResult.slice(0, 500));
    assert.ok(toolResult.includes(AGENT_BODY), 'The subagent body must reach the provider as tool-result data: '
      + JSON.stringify(requests.slice(beforeCalls).map((item, index) => ({ index, roles: item.messages.map(message => message.role), tail: JSON.stringify(item.messages).slice(-900) }))));
    assert.ok(result.events.some(event => event.kind === 'ToolExecution' || event.kind === 'Notice'), JSON.stringify(result.events));

    await new Promise(resolve => setTimeout(resolve, 1500));
    const afterIds = (await rawRpc('byok.history.list', { query: '' })).payload.conversations.map(value => value.id);
    manifest.conversationIds = afterIds.filter(id => !beforeIds.includes(id));
    await saveManifest();
    assert.deepEqual(exceptions, []);
    console.log(JSON.stringify({
      source: 'real VS Exp/WebView2 + owned loopback provider; no paid APIs',
      listedAgents: listed.agents.map(agent => agent.name),
      modeDeniedCode: denied.payload.code,
      providerRequests: requests.length - beforeCalls,
      toolAdvertised: true,
      agentBodyInjected: true,
      exceptions
    }, null, 2));
  }
} finally {
  socket.close();
  for (const socket of sockets) socket.destroy();
  server.close();
}
