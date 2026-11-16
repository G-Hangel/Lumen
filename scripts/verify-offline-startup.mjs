import assert from 'node:assert/strict';

const stage = process.argv[2] || 'verify';
assert.ok(['verify'].includes(stage), `Unknown stage: ${stage}`);
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
const requests = [];
const exceptions = [];
socket.addEventListener('message', event => {
  const message = JSON.parse(event.data);
  if (message.method === 'Network.requestWillBeSent') requests.push({ url: message.params.request.url, method: message.params.request.method });
  if (message.method === 'Runtime.exceptionThrown') exceptions.push(message.params.exceptionDetails.text);
  const task = pending.get(message.id);
  if (!task) return;
  pending.delete(message.id);
  message.error ? task.reject(new Error(message.error.message)) : task.resolve(message.result);
});
function command(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    pending.set(id, { resolve, reject });
    socket.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const result = await command('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
  return result.result.value;
}

await command('Runtime.enable');
await command('Network.enable');
await command('Page.enable');
// Reload with the network domain already attached, then observe the whole startup handshake.
try { await evaluate('location.reload()'); } catch { /* reload destroys the execution context by design */ }
await new Promise(resolve => setTimeout(resolve, 2000));
await evaluate(`new Promise((resolve, reject) => {
  const check = () => { if (document.querySelector('#connection-status')?.dataset.state === 'connected') { clearTimeout(timer); observer.disconnect(); resolve(true); } };
  const observer = new MutationObserver(check), timer = setTimeout(() => { observer.disconnect(); reject(new Error('UI not ready')); }, 20000);
  observer.observe(document, { subtree: true, attributes: true, childList: true, characterData: true }); check();
})`);
await new Promise(resolve => setTimeout(resolve, 1500));

const external = requests.filter(request => !request.url.startsWith('https://lumen.local/'));
const writes = requests.filter(request => request.method !== 'GET');
assert.deepEqual(external, [], 'Startup must not contact any host other than the local WebView2 virtual host.');
assert.deepEqual(writes, [], 'Startup must not POST anything before the user configures a provider.');
assert.deepEqual(exceptions, [], 'Startup must not raise JavaScript exceptions.');
console.log(JSON.stringify({
  source: 'real VS Exp/WebView2 startup; no paid APIs',
  requests: requests.length,
  hosts: [...new Set(requests.map(request => new URL(request.url).host))],
  external, writes, exceptions
}, null, 2));
socket.close();
