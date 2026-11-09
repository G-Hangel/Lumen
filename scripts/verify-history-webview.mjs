import assert from 'node:assert/strict';
import { readFile, writeFile, mkdir, stat, unlink } from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';

const stage = process.argv[2];
assert.ok(['prepare', 'resume', 'cleanup'].includes(stage), 'Use prepare, restart the Exp, then resume and cleanup.');
const manifestPath = new URL('../artifacts/history-fixtures.json', import.meta.url);
const reportPath = new URL('../artifacts/history-webview.json', import.meta.url);
const directory = path.join(process.env.LOCALAPPDATA, 'Lumen', 'conversations');
const pages = await (await fetch('http://127.0.0.1:9223/json/list')).json();
const page = pages.find(value => value.url === 'https://lumen.local/index.html');
assert.ok(page, 'Open Lumen in the experimental instance.');
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
let sequence = 0;
const pending = new Map(), exceptions = [], requests = [];
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
    const id = ++sequence;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(method)); }, 25000);
    pending.set(id, { resolve, reject, timer }); socket.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const result = await command('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
  return result.result.value;
}
const wait = predicate => evaluate(`new Promise((resolve,reject)=>{
  const ready=()=>(${predicate});
  const check=()=>{if(ready()){clearTimeout(timer);observer.disconnect();resolve(true);}};
  const timer=setTimeout(()=>{observer.disconnect();reject(new Error('UI condition missing: '+${JSON.stringify(predicate)}));},15000);
  const observer=new MutationObserver(check);observer.observe(document,{subtree:true,attributes:true,childList:true,characterData:true});check();
})`);
const idle = () => wait("!document.querySelector('#byok-history-new').disabled");
async function action(type, expression) {
  await idle();
  return evaluate(`new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('Missing ${type}'));},15000);
    const handler=e=>{if(e.data.type!==${JSON.stringify(type)}&&e.data.type!=='error')return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);if(e.data.type==='error')reject(new Error(e.data.payload.code));else resolve(e.data.payload);};
    chrome.webview.addEventListener('message',handler);${expression}
  })`);
}
async function rpc(type, payload) {
  await idle(); requests.push(type);
  return evaluate(`new Promise((resolve,reject)=>{
    const id=crypto.randomUUID(),timer=setTimeout(()=>reject(new Error('Native RPC missing')),15000);
    const handler=e=>{if(e.data.id!==id)return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve(e.data);};
    chrome.webview.addEventListener('message',handler);chrome.webview.postMessage({version:1,id,type:${JSON.stringify(type)},payload:${JSON.stringify(payload)}});
  })`);
}
const stats = async () => (await (await fetch('http://127.0.0.1:4184/fixture/stats')).json());
const selectArchive = async id => {
  await action('byok.history.open.result', `const list=document.querySelector('#byok-history-list');list.value=${JSON.stringify(id)};list.dispatchEvent(new Event('change'));`);
  await idle();
};
const send = text => action('byok.chat.result', `const input=document.querySelector('#byok-message');input.value=${JSON.stringify(text)};input.dispatchEvent(new Event('input'));document.querySelector('#byok-send').click();`);
let manifest, report;
try {
  await command('Runtime.enable'); await command('Page.enable');
  await wait("document.querySelector('#connection-status')?.dataset.state==='connected'");
  if (stage === 'prepare') {
    try { await stat(manifestPath); assert.fail('Clean up the previous owned fixture manifest first.'); } catch (error) { if (error.code !== 'ENOENT') throw error; }
    manifest = { configurationId: null, ids: [], token: `history-${randomUUID()}` };
    report = { source: 'real experimental WebView2, host-owned archive and loopback-only HTTP', startedAt: new Date().toISOString() };
    await writeFile(manifestPath, JSON.stringify(manifest));
    const configured = await action('byok.configure.result', `document.querySelector('#byok-panel').open=true;
      const kind=document.querySelector('#byok-kind');kind.value='OpenRouter';kind.dispatchEvent(new Event('change'));
      document.querySelector('#byok-provider-id').value='history-fixture';document.querySelector('#byok-url').value='http://127.0.0.1:4184/';
      document.querySelector('#byok-key').value='synthetic-history-webview-key';document.querySelector('#byok-configure-button').click();`);
    manifest.configurationId = configured.configuration.id; await writeFile(manifestPath, JSON.stringify(manifest));
    await action('byok.select.result', "const models=document.querySelector('#byok-models');models.value='fixture/plain';models.dispatchEvent(new Event('change'));");
    const chat = await send(manifest.token);
    assert.equal(chat.completed, true); manifest.liveId = chat.conversation.id; manifest.ids.push(manifest.liveId);
    await writeFile(manifestPath, JSON.stringify(manifest));
    const cancellation = evaluate(`new Promise((resolve,reject)=>{
      const timer=setTimeout(()=>reject(new Error('Cancellation missing')),15000);
      const handler=e=>{if(e.data.type==='byok.event'&&e.data.payload.kind==='TextDelta')document.querySelector('#byok-cancel').click();
        if(e.data.type==='error'&&e.data.payload.code==='cancelled'){clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve(e.data.payload);}};
      chrome.webview.addEventListener('message',handler);const input=document.querySelector('#byok-message');input.value='fixture-cancel-history';input.dispatchEvent(new Event('input'));document.querySelector('#byok-send').click();
    })`);
    await cancellation; await idle();
    const disk = JSON.parse(await readFile(path.join(directory, `${manifest.liveId}.json`), 'utf8'));
    assert.deepEqual(disk.turnStates, ['completed', 'cancelled']);
    assert.ok(JSON.stringify(disk).includes('Partial fixture history'));
    assert.ok(!JSON.stringify(disk).includes('synthetic-history-webview-key'));
    const seedId = randomUUID(); manifest.seedId = seedId; manifest.ids.push(seedId);
    await writeFile(manifestPath, JSON.stringify(manifest)); await stat(path.dirname(directory)); await mkdir(directory, { recursive: true });
    const now = new Date().toISOString();
    const messages = Array.from({ length: 500 }, (_, index) => [
      { role: 'User', parts: [{ kind: 'text', text: `${manifest.token} question ${index}` }] },
      { role: 'Assistant', modelId: 'fixture/plain', parts: [{ kind: 'text', text: index % 7 === 0 ? `reply ${index}\n\n\`\`\`csharp\n${Array.from({ length: 24 }, (_, line) => `int value${line} = ${line};`).join('\n')}\n\`\`\`` : `reply ${index}` }, { kind: 'reasoning', text: 'Reasoning line\n'.repeat(index % 9 + 2) }] }
    ]).flat();
    await writeFile(path.join(directory, `${seedId}.json`), JSON.stringify({ version: 1, configurationId: manifest.configurationId,
      conversation: { id: seedId, mode: 'Ask', messages, title: manifest.token + ' 500 turns', modelId: 'fixture/plain', reasoningIntent: 'Off', createdAtUtc: now, updatedAtUtc: now }, turnStates: Array(500).fill('completed') }), { flag: 'wx' });
    const before = await stats();
    await action('byok.history.list.result', `document.querySelector('#byok-history').open=true;document.querySelector('#byok-history-query').value=${JSON.stringify(manifest.token)};document.querySelector('#byok-history-search-form').requestSubmit();`);
    await selectArchive(seedId);
    await wait("!document.querySelector('#byok-history-items .history-placeholder')");
    await command('Emulation.setDeviceMetricsOverride', { width: 900, height: 900, deviceScaleFactor: 1, mobile: false });
    await evaluate("document.querySelector('#byok-panel').open=false;document.querySelector('#byok-history').open=false;");
    report.variable = await evaluate(`({mounted:document.querySelector('#byok-history-items').children.length,heights:[...document.querySelector('#byok-history-items').children].map(row=>row.getBoundingClientRect().height)})`);
    assert.ok(report.variable.mounted < 20); assert.ok(new Set(report.variable.heights).size > 1);
    await evaluate("document.querySelector('#byok-history-items details').open=true;");
    await wait("document.querySelector('#byok-history-items details')?.open===true");
    await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
    assert.equal(await evaluate("document.querySelector('#byok-history-items details').open"), true, 'Measurement must not close reasoning.');
    await evaluate("document.querySelector('#byok-history-last').click();");
    await wait("document.querySelector('#byok-history-items [aria-posinset=\"500\"]')?.textContent.includes('reply 499')&&!document.querySelector('#byok-history-new').disabled");
    report.last = await evaluate("({mounted:document.querySelector('#byok-history-items').children.length,last:document.querySelector('#byok-history-items').lastElementChild.getAttribute('aria-posinset')})");
    assert.ok(report.last.mounted < 20); assert.equal(report.last.last, '500');
    const checkLayout = `(()=>{const rows=[...document.querySelector('#byok-history-items').children].map(row=>row.getBoundingClientRect());return rows.every((row,index)=>!index||row.top>=rows[index-1].bottom-1);})()`;
    assert.equal(await evaluate(checkLayout), true);
    await evaluate("document.querySelector('#byok-history-items').lastElementChild.style.transform='translateY(-1000px)';");
    report.layoutNegative = !(await evaluate(checkLayout)); assert.equal(report.layoutNegative, true);
    await evaluate("document.querySelector('#byok-history-items').lastElementChild.style.removeProperty('transform');document.querySelector('#byok-history').open=true;");
    const rejected = await rpc('byok.history.delete', { conversationId: seedId, confirm: false });
    assert.equal(rejected.type, 'error'); assert.equal(rejected.payload.code, 'invalid_message');
    await stat(path.join(directory, `${seedId}.json`)); report.deleteNegative = true;
    const renamed = await action('byok.history.rename.result', "document.querySelector('#byok-history-name').value='Renamed offline history';document.querySelector('#byok-history-rename-form').requestSubmit();");
    assert.equal(renamed.conversation.id, seedId); assert.equal(renamed.conversation.title, 'Renamed offline history');
    await action('byok.history.new.result', "document.querySelector('#byok-history-new').click();");
    await stat(path.join(directory, `${seedId}.json`)); assert.equal(await evaluate("document.querySelectorAll('#byok-output .byok-turn').length"), 0);
    const after = await stats(); assert.equal(after.chatRequests, before.chatRequests); assert.equal(after.probeRequests, before.probeRequests);
    report.readOnlyHttpUnchanged = true; report.beforeRestart = after;
    report.prepareExceptions = exceptions; assert.deepEqual(exceptions, []);
    await writeFile(reportPath, JSON.stringify(report, null, 2));
  } else {
    manifest = JSON.parse(await readFile(manifestPath, 'utf8')); report = stage === 'resume' ? JSON.parse(await readFile(reportPath, 'utf8')) : {};
    if (stage === 'resume') {
      const before = await stats();
      await action('byok.history.list.result', "document.querySelector('#byok-history').open=true;");
      await selectArchive(manifest.liveId);
      await wait("document.querySelector('#byok-history-items').textContent.includes('Partial fixture history')");
      assert.ok(await evaluate("document.querySelector('#byok-history-items').textContent.includes('não enviado novamente')||document.querySelector('#byok-history-items').textContent.includes('never replayed')"));
      assert.equal((await stats()).chatRequests, before.chatRequests);
      await action('byok.list.result', "document.querySelector('#byok-panel').open=true;");
      await action('byok.discover.result', `const providers=document.querySelector('#byok-providers');providers.value=${JSON.stringify(manifest.configurationId)};providers.dispatchEvent(new Event('change'));`);
      await action('byok.select.result', "const models=document.querySelector('#byok-models');models.value='fixture/plain';models.dispatchEvent(new Event('change'));");
      assert.ok(await evaluate("document.querySelector('#byok-history-items').textContent.includes('Partial fixture history')"));
      const resumed = await send('explicit fixture resume'); assert.equal(resumed.conversation.id, manifest.liveId); assert.equal(resumed.conversation.turnCount, 3);
      const after = await stats(); assert.equal(after.chatRequests, before.chatRequests + 1); assert.equal(after.chatMessageCounts.at(-1), 3, 'Cancelled pair must not replay.');
      report.restartResume = { turnCount: 3, replayMessageCount: 3, paidServices: 0 };
      await action('byok.history.list.result', "document.querySelector('#byok-history-search-form').requestSubmit();");
      await selectArchive(manifest.seedId);
      await command('Emulation.setDeviceMetricsOverride', { width: 900, height: 900, deviceScaleFactor: 1, mobile: false });
      await evaluate("document.querySelector('#byok-panel').open=false;document.querySelector('#byok-history').open=false;window.scrollTo(0,0);new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
      const screenshot = await command('Page.captureScreenshot', { format: 'png' });
      await writeFile(new URL('../artifacts/history-webview.png', import.meta.url), Buffer.from(screenshot.data, 'base64'));
      await evaluate("document.querySelector('#byok-history').open=true;document.querySelector('#byok-history-delete').click();document.querySelector('#byok-history-keep').click();");
      await stat(path.join(directory, `${manifest.seedId}.json`));
      await evaluate("document.querySelector('#byok-history-delete').click();");
      await action('byok.history.delete.result', "document.querySelector('#byok-history-confirm').click();");
      await assert.rejects(stat(path.join(directory, `${manifest.seedId}.json`)), { code: 'ENOENT' });
      report.confirmedDeletion = true;
      report.exceptions = exceptions; assert.deepEqual(exceptions, []); await writeFile(reportPath, JSON.stringify(report, null, 2));
    } else {
      for (const id of manifest.ids) {
        const result = await rpc('byok.history.delete', { conversationId: id, confirm: true });
        assert.ok(result.type === 'byok.history.delete.result' || result.payload.code === 'conversation_not_found');
      }
      if (manifest.configurationId) assert.equal((await rpc('byok.remove', { configurationId: manifest.configurationId })).type, 'byok.remove.result');
      await unlink(manifestPath);
    }
  }
  console.log(JSON.stringify({ stage, exceptions, report }, null, 2));
} finally {
  await command('Emulation.clearDeviceMetricsOverride'); socket.close();
}
