import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir, writeFile, readFile, rm } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import path from 'node:path';

const stage = process.argv[2] || 'verify';
assert.ok(['verify', 'cleanup'].includes(stage), `Unknown stage: ${stage}`);
const artifacts = new URL('../artifacts/', import.meta.url);
await mkdir(artifacts, { recursive: true });
const manifestPath = new URL('skills-fixtures.json', artifacts);
const contextManifest = JSON.parse((await readFile(new URL('context-fixtures.json', artifacts), 'utf8')).replace(/^\uFEFF/, ''));
const requests = [], sockets = new Set();
const textOf = message => typeof message.content === 'string' ? message.content : (message.content ?? []).map(part => part.text ?? '').join('');

const server = createServer(async (request, response) => {
  if (request.method === 'GET' && request.url === '/models') {
    response.writeHead(200, { 'Content-Type': 'application/json' });
    response.end(JSON.stringify({ data: [{ id: 'fixture/auto', architecture: { input_modalities: ['text'] }, supported_parameters: ['tools'], context_length: 32768 }] }));
    return;
  }
  if (request.method !== 'POST' || request.url !== '/chat/completions') { response.writeHead(404); response.end(); return; }
  let bytes = 0; const chunks = [];
  for await (const chunk of request) { bytes += chunk.length; if (bytes > 8 * 1024 * 1024) { response.writeHead(413); response.end(); return; } chunks.push(chunk); }
  const payload = JSON.parse(Buffer.concat(chunks).toString('utf8'));
  requests.push(payload);
  const userIndex = payload.messages.findLastIndex(message => message.role === 'user');
  const text = textOf(payload.messages[userIndex]);
  response.writeHead(200, { 'Content-Type': 'text/event-stream' });
  response.end(`data: ${JSON.stringify({ choices: [{ index: 0, delta: { content: `fixture-skills-ok:${text.length}` }, finish_reason: 'stop' }] })}\n\ndata: [DONE]\n\n`);
});
server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); });

let manifest = null;
if (stage === 'verify') {
  assert.ok(contextManifest.root, 'Run scripts/context-experimental.ps1 -Action Prepare first.');
  assert.equal(contextManifest.configurationId, null, 'A fixture provider from an interrupted run is still configured.');
  const stale = await readFile(manifestPath, 'utf8').then(() => true, error => { if (error.code === 'ENOENT') return false; throw error; });
  assert.equal(stale, false, 'artifacts/skills-fixtures.json exists; run the cleanup stage first.');
  manifest = { root: contextManifest.root, configurationId: randomUUID(), conversationIds: [], createdFolders: [], initialEnabled: null };
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
} else {
  manifest = JSON.parse(await readFile(manifestPath, 'utf8').catch(error => { if (error.code === 'ENOENT') return null; throw error; }));
  assert.ok(manifest, 'Nothing to clean up.');
}

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
   const id=crypto.randomUUID(),events=[],timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('RPC timeout'));},20000);
   const handler=e=>{if(e.data.id!==id)return;if(e.data.type==='byok.event'){events.push(e.data.payload);return;}clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve({...e.data,events});};
   chrome.webview.addEventListener('message',handler);chrome.webview.postMessage({version:1,id,type:${JSON.stringify(type)},payload:${JSON.stringify(payload)}});})`);
}
async function chat(text, mode = 'Ask') {
  await idle();
  await evaluate(`(()=>{const mode=document.querySelector('#byok-mode');mode.value=${JSON.stringify(mode)};mode.dispatchEvent(new Event('change'));})()`);
  return evaluate(`new Promise((resolve,reject)=>{
   const events=[],timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('chat timeout'));},30000);
   const handler=e=>{if(e.data.type==='byok.event'){events.push(e.data.payload);return;}if(e.data.type!=='byok.chat.result'&&e.data.type!=='error')return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve({...e.data,events});};
   chrome.webview.addEventListener('message',handler);
   const input=document.querySelector('#byok-message');input.value=${JSON.stringify(text)};input.dispatchEvent(new Event('input'));
   document.querySelector('#byok-send').click();})`, 35000);
}
const skillState = async () => {
  const listed = await rpc('byok.skills.list', {});
  assert.equal(listed.type, 'byok.skills.list.result', JSON.stringify(listed));
  return listed.payload;
};
const toggle = async (name, enabled) => {
  const result = await action('byok.skills.set.result',
    `(()=>{const input=[...document.querySelectorAll('#byok-skills-list input')].find(node=>node.dataset.skill===${JSON.stringify(name)}&&node.dataset.action==='enabled');input.checked=${enabled};input.dispatchEvent(new Event('change'));})()`);
  assert.equal(result.type, 'byok.skills.set.result', JSON.stringify(result));
  assert.equal(result.payload.name, name); assert.equal(result.payload.enabled, enabled);
};
const userText = payload => { const index = payload.messages.findLastIndex(message => message.role === 'user'); return textOf(payload.messages[index]); };
const saveManifest = () => writeFile(manifestPath, JSON.stringify(manifest, null, 2));
const report = { startedAt: new Date().toISOString(), source: 'real VS Exp/WebView2 + owned loopback provider; no paid APIs' };

try {
  await command('Runtime.enable'); await command('Page.enable');
  await wait("document.querySelector('#connection-status')?.dataset.state==='connected'");
  await idle();

  if (stage === 'cleanup') {
    if (manifest.initialEnabled) {
      for (const [name, enabled] of Object.entries(manifest.initialEnabled)) {
        const current = await skillState();
        const item = current.skills.find(value => value.name === name);
        if (item && item.enabled !== enabled) {
          const reply = await rpc('byok.skills.set', { name, enabled });
          assert.equal(reply.type, 'byok.skills.set.result', JSON.stringify(reply));
        }
      }
    }
    for (const folder of manifest.createdFolders) await rm(folder, { recursive: true, force: true });
    if (manifest.configurationId) {
      const listed = await rpc('byok.list', {});
      if (listed.payload.configurations.some(value => value.id === manifest.configurationId)) assert.equal((await rpc('byok.remove', { configurationId: manifest.configurationId })).type, 'byok.remove.result');
    }
    const current = await rpc('byok.history.list', { query: '' }).catch(() => null);
    for (const id of manifest.conversationIds) {
      const reply = current && await rpc('byok.history.delete', { conversationId: id, confirm: true }).catch(() => null);
      if (reply) assert.ok(reply.type === 'byok.history.delete.result' || reply.payload.code === 'conversation_not_found');
    }
    await rm(manifestPath, { force: true });
    console.log('Owned skills provider, credential, conversations, skill folders and disabled states removed.');
  } else {
    const beforeIds = (await rpc('byok.history.list', { query: '' })).payload.conversations.map(value => value.id);

    // 1. The bundled example skills shipped inside the VSIX are discovered.
    const initial = await skillState();
    report.bundled = initial.skills.filter(value => value.source === 'bundled/skills').map(value => ({ name: value.name, scope: value.scope, modes: value.modes }));
    assert.deepEqual(report.bundled.map(value => value.name).sort(), ['code-review', 'explain-selection', 'write-tests']);
    for (const value of report.bundled) { assert.equal(value.scope, 'User'); assert.deepEqual(value.modes, ['Ask', 'Plan', 'Agent']); }
    manifest.initialEnabled = Object.fromEntries(initial.skills.map(value => [value.name, value.enabled]));
    await saveManifest();

    // 2. The panel lists them with origin and enables the reload control after a model is selected.
    await evaluate("document.querySelector('#byok-skills').open=true");
    await wait("document.querySelectorAll('#byok-skills-list li').length>0");
    report.panel = await evaluate(`({
      open: document.querySelector('#byok-skills').open,
      items: document.querySelectorAll('#byok-skills-list li').length,
      status: document.querySelector('#byok-skills-status').textContent,
      reloadDisabledBeforeModel: document.querySelector('#byok-skills-reload').disabled
    })`);
    assert.equal(report.panel.open, true); assert.ok(report.panel.items >= 3); assert.ok(report.panel.status.length > 0);

    // 3. Owned loopback provider so the composer and the reload control become usable.
    const configured = await rpc('byok.configure', { id: manifest.configurationId, providerId: 'fixture-skills', kind: 'OpenRouter', baseUrl: `http://127.0.0.1:${server.address().port}/`, key: 'synthetic-skills-key' });
    assert.equal(configured.type, 'byok.configure.result', JSON.stringify(configured));
    await evaluate("document.querySelector('#byok-panel').open=true");
    await action('byok.list.result', "document.querySelector('#byok-list').click()");
    await action('byok.discover.result', `const p=document.querySelector('#byok-providers');p.value=${JSON.stringify(manifest.configurationId)};p.dispatchEvent(new Event('change'));`);
    await action('byok.select.result', "const m=document.querySelector('#byok-models');m.value='fixture/auto';m.dispatchEvent(new Event('change'));");
    await action('byok.history.new.result', "document.querySelector('#byok-history-new').click()");
    await evaluate("document.querySelector('#byok-panel').open=false");
    await idle();

    // 4. Reload re-runs discovery without restarting Visual Studio.
    await evaluate("document.querySelector('#byok-skills').open=true");
    const reloaded = await action('byok.skills.list.result', "document.querySelector('#byok-skills-reload').click()");
    assert.equal(reloaded.type, 'byok.skills.list.result');
    assert.ok(reloaded.payload.skills.some(value => value.name === 'code-review' && value.source === 'bundled/skills'));
    report.reloadKeptBundled = true;

    // 5. AC: a new project skill appears after reload, without restarting the IDE.
    const projectFolder = path.join(manifest.root, '.lumen', 'skills', 'exp-project-skill');
    manifest.createdFolders.push(projectFolder);
    await mkdir(projectFolder, { recursive: true });
    await writeFile(path.join(projectFolder, 'SKILL.md'), '---\nname: exp-project-skill\ndescription: Temporary skill created by the Lumen skills verifier.\n---\n\n# Temporary\n\nMarker body for the verifier.\n');
    await saveManifest();
    await action('byok.skills.list.result', "document.querySelector('#byok-skills-reload').click()");
    const afterCreate = await skillState();
    const created = afterCreate.skills.find(value => value.name === 'exp-project-skill');
    assert.ok(created, 'The new project skill must appear without restarting Visual Studio.');
    assert.equal(created.source, '.lumen/skills'); assert.equal(created.scope, 'Project'); assert.equal(created.enabled, true);
    report.projectSkillWithoutRestart = { source: created.source, scope: created.scope };

    // 6. CN: invalid skills produce actionable diagnostics and are never loaded.
    const mismatchFolder = path.join(manifest.root, '.lumen', 'skills', 'exp-mismatch');
    const frontmatterFolder = path.join(manifest.root, '.lumen', 'skills', 'exp-no-frontmatter');
    manifest.createdFolders.push(mismatchFolder, frontmatterFolder);
    await mkdir(mismatchFolder, { recursive: true });
    await mkdir(frontmatterFolder, { recursive: true });
    await writeFile(path.join(mismatchFolder, 'SKILL.md'), '---\nname: other-name\ndescription: Wrong folder name.\n---\n\nBody.\n');
    await writeFile(path.join(frontmatterFolder, 'SKILL.md'), 'No frontmatter at all.\n');
    await saveManifest();
    await action('byok.skills.list.result', "document.querySelector('#byok-skills-reload').click()");
    const invalid = await skillState();
    report.diagnostics = invalid.diagnostics.filter(value => value.location.includes('exp-mismatch') || value.location.includes('exp-no-frontmatter'));
    assert.ok(invalid.diagnostics.some(value => value.code === 'name_mismatch' && value.location.includes('exp-mismatch')), JSON.stringify(invalid.diagnostics));
    assert.ok(invalid.diagnostics.some(value => value.code === 'missing_frontmatter' && value.location.includes('exp-no-frontmatter')), JSON.stringify(invalid.diagnostics));
    assert.ok(!invalid.skills.some(value => value.name === 'exp-mismatch' || value.name === 'exp-no-frontmatter' || value.name === 'other-name'), 'Invalid skills must not load silently.');

    // 7. AC: explicit activation injects the skill body into the conversation payload.
    const activated = await chat('/code-review review this file');
    assert.equal(activated.type, 'byok.chat.result', JSON.stringify(activated));
    const activatedText = userText(requests.at(-1));
    assert.ok(activatedText.includes('[Skill: code-review]'), activatedText.slice(0, 400));
    assert.ok(activatedText.includes('# Code Review'), 'The skill body must reach the provider.');
    report.activationInjected = true;

    // 8. CN: an untouched skill contributes nothing but its catalog line.
    const plain = await chat('plain question without any skill');
    assert.equal(plain.type, 'byok.chat.result', JSON.stringify(plain));
    const plainText = userText(requests.at(-1));
    assert.ok(!plainText.includes('[Skill:'), 'Progressive disclosure must not inject unactivated bodies.');
    report.progressiveDisclosureNegative = true;

    // 9. CN: disabling a skill removes its effect immediately, in the panel and on the wire.
    await evaluate("document.querySelector('#byok-skills').open=true");
    await toggle('code-review', false);
    const suggestions = await evaluate(`(()=>{const input=document.querySelector('#byok-message');input.value='/c';input.dispatchEvent(new Event('input'));const names=[...document.querySelectorAll('#byok-skill-suggestions button')].map(button=>button.textContent.split(' ')[0]);input.value='';input.dispatchEvent(new Event('input'));return names;})()`);
    assert.ok(!suggestions.some(name => name === '/code-review'), JSON.stringify(suggestions));
    const afterDisable = await skillState();
    assert.equal(afterDisable.skills.find(value => value.name === 'code-review').enabled, false);
    const requestsBefore = requests.length;
    const rejected = await chat('/code-review review this again');
    assert.equal(rejected.type, 'error', JSON.stringify(rejected));
    assert.equal(rejected.payload.code, 'skill_unavailable');
    assert.equal(requests.length, requestsBefore, 'A disabled skill must be rejected before any provider call.');
    report.disabledSkillRejected = { suggestions, code: rejected.payload.code, providerCalls: requests.length - requestsBefore };

    // 10. Re-enable from the panel and confirm the state survives a fresh listing.
    await toggle('code-review', true);
    const afterEnable = await skillState();
    assert.equal(afterEnable.skills.find(value => value.name === 'code-review').enabled, true);
    report.reEnabled = true;

    // 11. Remove the temporary project skills and confirm discovery drops them (still no restart).
    for (const folder of manifest.createdFolders) await rm(folder, { recursive: true, force: true });
    await action('byok.skills.list.result', "document.querySelector('#byok-skills-reload').click()");
    const afterDelete = await skillState();
    assert.ok(!afterDelete.skills.some(value => ['exp-project-skill', 'exp-mismatch', 'exp-no-frontmatter'].includes(value.name)));
    assert.ok(!afterDelete.diagnostics.some(value => value.location.includes('exp-mismatch') || value.location.includes('exp-no-frontmatter')));
    report.removedAfterReload = true;
    manifest.createdFolders = [];

    // 12. Restore the initial enabled state and collect the conversations this run created.
    for (const [name, enabled] of Object.entries(manifest.initialEnabled)) {
      const item = (await skillState()).skills.find(value => value.name === name);
      if (item && item.enabled !== enabled) await toggle(name, enabled);
    }
    const afterIds = (await rpc('byok.history.list', { query: '' })).payload.conversations.map(value => value.id);
    manifest.conversationIds = afterIds.filter(id => !beforeIds.includes(id));
    await saveManifest();
    report.conversations = manifest.conversationIds.length;
    report.providerRequests = requests.length;

    await wait("!document.querySelector('#byok-history-new').disabled");
    report.exceptions = exceptions;
    assert.equal(exceptions.length, 0, JSON.stringify(exceptions));
    const screenshot = await command('Page.captureScreenshot', { format: 'png' });
    await writeFile(new URL('skills-webview.png', artifacts), Buffer.from(screenshot.data, 'base64'));
    await writeFile(new URL('skills-webview.json', artifacts), JSON.stringify(report, null, 2) + '\n');
    console.log(JSON.stringify(report, null, 2));
  }
} finally {
  for (const socket of sockets) socket.destroy();
  if (server.listening) server.close();
  socket.close();
}
