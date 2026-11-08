import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const locales = Object.fromEntries(await Promise.all(['en', 'pt-BR'].map(async culture => [culture,
  JSON.parse(await readFile(new URL(`../src/Lumen.Vsix/wwwroot/locales/${culture}.json`, import.meta.url), 'utf8'))])));
const pages = await (await fetch('http://127.0.0.1:9223/json/list')).json();
const page = pages.find(candidate => candidate.url === 'https://lumen.local/index.html');
assert.ok(page, 'Open Lumen in the experimental instance.');
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
let sequence = 0;
const pending = new Map();
const exceptions = [];
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
    const id = ++sequence;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`Timed out: ${method}`)); }, 20000);
    pending.set(id, { resolve, reject, timer }); socket.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const result = await command('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  assert.equal(result.exceptionDetails, undefined, 'The runtime expression must not throw.');
  return result.result.value;
}
const waitForReply = (type, action) => `new Promise((resolve,reject)=>{
  const timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('Reply missing'));},15000);
  const handler=e=>{if(e.data.type!==${JSON.stringify(type)})return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve(e.data.payload);};
  chrome.webview.addEventListener('message',handler);${action}
})`;
const report = { source: 'real C# WebView2 bridge, loopback fixtures; not a real AI service' };
const ownedIds = [];
try {
  await command('Runtime.enable');
  await command('Page.enable');
  await evaluate(`new Promise((resolve,reject)=>{
    const ready=()=>document.querySelector('#connection-status')?.dataset.state==='connected';
    if(ready()){resolve(true);return;}
    const timer=setTimeout(()=>{observer.disconnect();reject(new Error('Native handshake missing'));},10000);
    const observer=new MutationObserver(()=>{if(ready()){clearTimeout(timer);observer.disconnect();resolve(true);}});
    observer.observe(document,{childList:true,subtree:true,attributes:true});
  })`);
  assert.equal(await evaluate("document.querySelector('#connection-status').dataset.state"), 'connected');
  report.initialBudget = await evaluate(waitForReply('byok.list.result', "document.querySelector('#byok-panel').open=true;document.querySelector('#byok-list').click();"));
  assert.equal(report.initialBudget.remainingBudget, 5);
  const initialStats = await (await fetch('http://127.0.0.1:4184/fixture/stats')).json();
  report.configure = await evaluate(waitForReply('byok.configure.result', `
    document.querySelector('#byok-panel').open=true;
    const kind=document.querySelector('#byok-kind');kind.value='OpenRouter';kind.dispatchEvent(new Event('change',{bubbles:true}));
    document.querySelector('#byok-provider-id').value='fixture';
    document.querySelector('#byok-url').value='http://127.0.0.1:4184/';
    document.querySelector('#byok-key').value='synthetic-webview-credential-test';
    document.querySelector('#byok-configure-button').click();`));
  ownedIds.push(report.configure.configuration.id);
  assert.equal(report.configure.credentialValid, null);
  assert.equal(report.configure.code, null);
  assert.equal(report.configure.models.length, 3);
  report.catalog = await evaluate(`({culture:document.documentElement.lang,status:document.querySelector('#byok-status').textContent,
    errorHidden:document.querySelector('#byok-error').hidden,providerId:document.querySelector('#byok-providers').value,
    providersEnabled:!document.querySelector('#byok-providers').disabled,modelsEnabled:!document.querySelector('#byok-models').disabled,
    models:[...document.querySelector('#byok-models').options].map(option=>option.value),keyCleared:document.querySelector('#byok-key').value===''
  })`);
  const text = locales[report.catalog.culture];
  assert.ok(text, 'The catalog status must use a supported locale.');
  assert.equal(report.catalog.status, text.byokCatalogUnverified);
  assert.notEqual(report.catalog.status, text.byokCredentialValid);
  assert.equal(report.catalog.errorHidden, true);
  assert.equal(report.catalog.providerId, report.configure.configuration.id);
  assert.equal(report.catalog.providersEnabled, true);
  assert.equal(report.catalog.modelsEnabled, true);
  assert.equal(report.catalog.keyCleared, true);
  assert.ok(report.catalog.models.includes('fixture/plain') && report.catalog.models.includes('fixture/vision') && report.catalog.models.includes('fixture/code'));
  const saved = await evaluate(`new Promise((resolve,reject)=>{
    const id=crypto.randomUUID();const timer=setTimeout(()=>reject(new Error('Saved catalog reply missing')),10000);
    const handler=e=>{if(e.data.id!==id)return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);
      if(e.data.type!=='byok.list.result')reject(new Error('Saved catalog listing rejected'));else resolve(e.data.payload.configurations);};
    chrome.webview.addEventListener('message',handler);chrome.webview.postMessage({version:1,id,type:'byok.list',payload:{}});
  })`);
  report.catalog.saved = saved.some(configuration => configuration.id === report.configure.configuration.id);
  assert.equal(report.catalog.saved, true, 'A public catalog must persist without claiming key validity.');
  const plain = report.configure.models.find(model => model.id === 'fixture/plain');
  assert.equal(plain.vision, false);
  assert.equal(plain.origins.find(origin => origin.capability === 'Vision').source, 'Native');
  await evaluate(waitForReply('byok.select.result', "const models=document.querySelector('#byok-models');models.value='fixture/plain';models.dispatchEvent(new Event('change',{bubbles:true}));"));
  report.plain = await evaluate("({imageDisabled:document.querySelector('#byok-image').disabled,effortDisabled:document.querySelector('#byok-effort').disabled,keyCleared:document.querySelector('#byok-key').value==='',badges:document.querySelector('#byok-panel').innerText})");
  assert.equal(report.plain.imageDisabled, true);
  assert.equal(report.plain.effortDisabled, true);
  assert.equal(report.plain.keyCleared, true);
  assert.ok(!report.plain.badges.includes('synthetic-webview-credential-test'));
  delete report.plain.badges;
  await evaluate(waitForReply('byok.select.result', "const models=document.querySelector('#byok-models');models.value='fixture/vision';models.dispatchEvent(new Event('change',{bubbles:true}));"));
  report.vision = await evaluate("({imageDisabled:document.querySelector('#byok-image').disabled,efforts:[...document.querySelector('#byok-effort').options].map(option=>option.value)})");
  assert.equal(report.vision.imageDisabled, false);
  assert.ok(report.vision.efforts.includes('Low') && report.vision.efforts.includes('High'));
  report.probe = { before: await evaluate("({consent:document.querySelector('#byok-probe-consent').checked,disabled:document.querySelector('#byok-verify').disabled,budget:document.querySelector('#byok-probe-budget').textContent})") };
  assert.equal(report.probe.before.consent, false);
  assert.equal(report.probe.before.disabled, true);
  assert.equal(report.probe.before.budget, text.byokProbeBudget.replace('{remaining}', '5').replace('{max}', '5'));
  await evaluate("document.querySelector('#byok-verify').click();");
  report.probe.noConsent = await evaluate(`new Promise((resolve,reject)=>{
    const id=crypto.randomUUID();const timer=setTimeout(()=>reject(new Error('Consent rejection missing')),10000);
    const handler=e=>{if(e.data.id!==id)return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve(e.data);};
    chrome.webview.addEventListener('message',handler);chrome.webview.postMessage({version:1,id,type:'byok.probe',payload:{configurationId:${JSON.stringify(report.configure.configuration.id)},capability:'Vision',consent:false}});
  })`);
  assert.equal(report.probe.noConsent.type, 'error');
  assert.equal(report.probe.noConsent.payload.code, 'consent_required');
  assert.equal((await (await fetch('http://127.0.0.1:4184/fixture/stats')).json()).probeRequests, initialStats.probeRequests);
  for (let attempt=0; attempt<2; attempt++) {
    const result = await evaluate(waitForReply('byok.probe.result', `
      document.querySelector('#byok-probe-capability').value='Vision';
      const consent=document.querySelector('#byok-probe-consent');consent.checked=true;consent.dispatchEvent(new Event('change',{bubbles:true}));
      document.querySelector('#byok-verify').click();`));
    assert.equal(result.supported, null, 'HTTP acceptance alone must remain inconclusive.');
    assert.equal(result.remainingBudget, 4);
    assert.equal(result.model.id, 'fixture/vision');
    assert.equal(result.model.origins.find(origin=>origin.capability==='Vision').source, 'Native');
    report.probe[`attempt${attempt+1}`] = result;
  }
  report.probe.after = await evaluate("({consent:document.querySelector('#byok-probe-consent').checked,disabled:document.querySelector('#byok-verify').disabled,budget:document.querySelector('#byok-probe-budget').textContent,status:document.querySelector('#byok-status').textContent})");
  assert.equal(report.probe.after.consent, false);
  assert.equal(report.probe.after.disabled, true);
  assert.equal(report.probe.after.budget, text.byokProbeBudget.replace('{remaining}', '4').replace('{max}', '5'));
  assert.equal(report.probe.after.status, text.byokProbeInconclusive);
  report.probe.httpRequests = (await (await fetch('http://127.0.0.1:4184/fixture/stats')).json()).probeRequests - initialStats.probeRequests;
  assert.equal(report.probe.httpRequests, 1, 'Discovery, disabled click, rejected consent and repeated attempts must not spend tokens.');
  const cancelledOperation = evaluate(waitForReply('error', `
    document.querySelector('#byok-probe-capability').value='Tools';
    const consent=document.querySelector('#byok-probe-consent');consent.checked=true;consent.dispatchEvent(new Event('change',{bubbles:true}));
    document.querySelector('#byok-verify').click();`));
  const entered = await (await fetch(`http://127.0.0.1:4184/fixture/wait-probe?after=${initialStats.probeRequests+1}`, {signal:AbortSignal.timeout(15000)})).json();
  assert.equal(entered.probeRequests, initialStats.probeRequests+2);
  assert.equal(await evaluate("document.querySelector('#byok-cancel').disabled"), false);
  const cancelStarted = Date.now();
  report.probe.cancel = await evaluate(waitForReply('byok.cancel.result', "document.querySelector('#byok-cancel').click();"));
  assert.equal(report.probe.cancel.cancelled, true);
  report.probe.cancelledOperation = await cancelledOperation;
  assert.equal(report.probe.cancelledOperation.code, 'cancelled');
  report.probe.cancelMilliseconds = Date.now()-cancelStarted;
  assert.ok(report.probe.cancelMilliseconds<2000, 'Cancellation should stop the pending native probe promptly.');
  report.probe.cancelledBudget = await evaluate(`new Promise((resolve,reject)=>{
    const target=document.querySelector('#byok-probe-budget');
    const expected=${JSON.stringify(text.byokProbeBudget.replace('{remaining}','3').replace('{max}','5'))};
    const ready=()=>target.textContent===expected&&!document.querySelector('#byok-configure-button').disabled;
    if(ready()){resolve(target.textContent);return;}
    const timer=setTimeout(()=>{observer.disconnect();reject(new Error('Cancelled probe budget missing'));},10000);
    const observer=new MutationObserver(()=>{if(ready()){clearTimeout(timer);observer.disconnect();resolve(target.textContent);}});
    observer.observe(document.querySelector('#byok-panel'),{subtree:true,attributes:true,childList:true});
  })`);
  assert.equal(await evaluate("document.querySelector('#byok-probe-consent').checked"), false);
  assert.equal(await evaluate("document.querySelector('#byok-verify').disabled"), true);
  await command('DOM.enable');
  const documentNode = await command('DOM.getDocument');
  const imageNode = await command('DOM.querySelector', { nodeId: documentNode.root.nodeId, selector: '#byok-image' });
  await command('DOM.setFileInputFiles', { nodeId: imageNode.nodeId, files: [fileURLToPath(new URL('../artifacts/webview-offline.png', import.meta.url))] });
  report.attachment = await evaluate(`new Promise((resolve,reject)=>{
    const attachment=document.querySelector('#byok-attachment');
    if(!attachment.hidden){resolve(true);return;}
    const timer=setTimeout(()=>{observer.disconnect();reject(new Error('Image attachment missing'));},10000);
    const observer=new MutationObserver(()=>{if(!attachment.hidden){clearTimeout(timer);observer.disconnect();resolve(true);}});
    observer.observe(attachment,{attributes:true});
  })`);
  assert.equal(report.attachment,true);
  report.chat = await evaluate(waitForReply('byok.chat.result', "document.querySelector('#byok-message').value='Use the offline fixture only';document.querySelector('#byok-message').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#byok-send').click();"));
  assert.equal(report.chat.completed, true);
  report.rendered = await evaluate("document.querySelector('#byok-output').innerText");
  assert.ok(report.rendered.includes('Hello world'));
  assert.ok(report.rendered.includes('read_file'));
  assert.ok(!report.rendered.includes('synthetic-webview-credential-test'));
  delete report.rendered;
  report.chatUi = await evaluate(`({
    outputInMain: !!document.querySelector('main #byok-output'),
    emptyHidden: document.querySelector('#byok-empty').hidden,
    turns: document.querySelectorAll('#byok-output .byok-turn').length,
    activeModel: document.querySelector('#byok-active-model').textContent,
    messageCleared: document.querySelector('#byok-message').value === '',
    count: document.querySelector('#byok-count').textContent
  })`);
  assert.equal(report.chatUi.outputInMain, true);
  assert.equal(report.chatUi.emptyHidden, true);
  assert.equal(report.chatUi.turns, 1);
  assert.ok(report.chatUi.activeModel.includes('Offline'));
  assert.equal(report.chatUi.messageCleared, true);
  assert.equal(report.chatUi.count, text.byokCount.replace('{count}', '0').replace('{max}', '4096'));
  await evaluate(waitForReply('byok.select.result', "const models=document.querySelector('#byok-models');models.value='fixture/code';models.dispatchEvent(new Event('change',{bubbles:true}));"));
  report.codeChat = await evaluate(waitForReply('byok.chat.result', "document.querySelector('#byok-message').value='Show a code block';document.querySelector('#byok-message').dispatchEvent(new Event('input',{bubbles:true}));document.querySelector('#byok-send').click();"));
  assert.equal(report.codeChat.completed, true);
  await command('Browser.grantPermissions', { origin: 'https://lumen.local', permissions: ['clipboardReadWrite', 'clipboardSanitizedWrite'] });
  await command('Page.bringToFront');
  await command('Emulation.setFocusEmulationEnabled', { enabled: true });
  const copyResult = await command('Runtime.evaluate', { awaitPromise: true, returnByValue: true, userGesture: true, expression: `(async () => {
    const block = document.querySelector('#byok-output .code-block');
    const button = block && block.querySelector('.code-copy');
    if (!button) return { present: false };
    const code = block.querySelector('pre').textContent;
    const aria = block.querySelector('pre').getAttribute('aria-label');
    button.click();
    await new Promise(resolve => setTimeout(resolve, 300));
    return { present: true, code, aria, label: button.textContent };
  })()` });
  assert.equal(copyResult.exceptionDetails, undefined);
  report.copy = copyResult.result.value;
  assert.equal(report.copy.present, true, 'The streamed code block must offer a copy button.');
  assert.ok(report.copy.code.includes('public static int Add'));
  assert.equal(report.copy.aria, text.byokCodeBlock);
  assert.equal(report.copy.label, text.byokCopied);
  delete report.copy.code;
  await command('Emulation.setDeviceMetricsOverride',{width:900,height:1100,deviceScaleFactor:1,mobile:false});
  await evaluate("document.querySelector('#byok-panel').open=false;document.querySelector('#byok-output').scrollTop=document.querySelector('#byok-output').scrollHeight;document.querySelector('#byok-key').value='';");
  const screenshot=await command('Page.captureScreenshot',{format:'png',captureBeyondViewport:true});
  await writeFile(new URL('../artifacts/byok-webview.png',import.meta.url),Buffer.from(screenshot.data,'base64'));
  report.statuses = [];
  for (const status of [401, 404, 429]) {
    const result = await evaluate(waitForReply('byok.configure.result', `
      const kind=document.querySelector('#byok-kind');kind.value='OpenAI';kind.dispatchEvent(new Event('change',{bubbles:true}));
      document.querySelector('#byok-url').value='http://127.0.0.1:4184/error${status}/';
      document.querySelector('#byok-key').value='synthetic-webview-credential-test';
      document.querySelector('#byok-configure-button').click();`));
    assert.equal(result.code, status === 401 ? 'invalid_key' : status === 404 ? 'invalid_url' : 'rate_limited');
    assert.equal(result.credentialValid, status === 429 ? true : status === 401 ? false : null);
    if (result.credentialValid === true) ownedIds.push(result.configuration.id);
    const ui = await evaluate(`({status:document.querySelector('#byok-status').textContent,error:document.querySelector('#byok-error').textContent,
      errorHidden:document.querySelector('#byok-error').hidden,keyCleared:document.querySelector('#byok-key').value==='',
      saved:[...document.querySelector('#byok-providers').options].some(option=>option.value===${JSON.stringify(result.configuration.id)})})`);
    assert.equal(ui.errorHidden, false);
    assert.equal(ui.error, text[status === 401 ? 'byokErrorInvalidKey' : status === 404 ? 'byokErrorUrl' : 'byokErrorRate']);
    assert.equal(ui.keyCleared, true);
    assert.equal(ui.saved, status === 429);
    assert.notEqual(ui.status, text.byokCatalogUnverified);
    if (status === 429) assert.equal(ui.status, text.byokCredentialValid);
    else assert.equal(ui.status, text.byokReady);
    report.statuses.push({ status, code: result.code, credentialValid: result.credentialValid, ui });
  }
  report.negative = await evaluate(`new Promise(resolve=>{
    const id=crypto.randomUUID();const handler=e=>{if(e.data.id!==id)return;chrome.webview.removeEventListener('message',handler);resolve(e.data);};
    chrome.webview.addEventListener('message',handler);chrome.webview.postMessage({version:1,id,type:'byok.chat',payload:{text:'unauthorized'}});
  })`);
  assert.equal(report.negative.type, 'error');
  assert.equal(report.negative.payload.code, 'invalid_message');
  report.layouts = [];
  for (const [width,height] of [[203,422],[375,700],[900,900]]) {
    await command('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});
    const layout = await evaluate(`(()=>{document.querySelector('#byok-panel').open=true;return {width:innerWidth,overflow:document.documentElement.scrollWidth>innerWidth,usable:document.querySelector('#byok-panel').getBoundingClientRect().width>0};})()`);
    assert.equal(layout.overflow,false); assert.equal(layout.usable,true); report.layouts.push(layout);
  }
  report.layoutNegative = await evaluate("(()=>{document.querySelector('.app').style.minWidth='1000px';const detected=document.documentElement.scrollWidth>innerWidth;document.querySelector('.app').style.removeProperty('min-width');return detected;})()");
  assert.equal(report.layoutNegative,true);
  report.exceptions = exceptions;
  assert.deepEqual(exceptions,[]);
  await writeFile(new URL('../artifacts/byok-webview.json',import.meta.url),JSON.stringify(report,null,2)+'\n');
  console.log(JSON.stringify(report,null,2));
} finally {
  for (const configurationId of ownedIds) {
    await evaluate(`new Promise((resolve,reject)=>{const id=crypto.randomUUID();const timer=setTimeout(()=>reject(new Error('Fixture cleanup failed')),10000);const handler=e=>{if(e.data.id!==id)return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);if(e.data.type==='error')reject(new Error('Fixture cleanup rejected'));else resolve(true);};chrome.webview.addEventListener('message',handler);chrome.webview.postMessage({version:1,id,type:'byok.remove',payload:{configurationId:${JSON.stringify(configurationId)}}});})`);
  }
  await command('Emulation.setFocusEmulationEnabled', { enabled: false });
  await command('Emulation.clearDeviceMetricsOverride');
  socket.close();
}
