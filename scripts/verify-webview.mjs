import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';

const artifacts = new URL('../artifacts/', import.meta.url);
await mkdir(artifacts, { recursive: true });
const pages = await (await fetch('http://127.0.0.1:9223/json/list')).json();
const page = pages.find(candidate => candidate.url === 'https://lumen.local/index.html');
assert.ok(page, 'Open Lumen in the experimental Visual Studio with local WebView2 debugging enabled.');
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {
  socket.addEventListener('open', resolve, { once: true });
  socket.addEventListener('error', reject, { once: true });
});
let sequence = 0;
const pending = new Map();
const events = [];
socket.addEventListener('message', event => {
  const message = JSON.parse(event.data);
  if (!message.id) { events.push(message); return; }
  const request = pending.get(message.id);
  if (!request) return;
  pending.delete(message.id);
  clearTimeout(request.timer);
  if (message.error) request.reject(new Error(JSON.stringify(message.error)));
  else request.resolve(message.result);
});
function command(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`Timed out: ${method}`)); }, 15000);
    pending.set(id, { resolve, reject, timer });
    socket.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const result = await command('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
  return result.result.value;
}
const report = {};
try {
  await command('Runtime.enable');
  await command('Network.enable');
  await command('Page.enable');
  await evaluate(`new Promise((resolve,reject)=>{
    const ready=()=>document.querySelector('#connection-status')?.dataset.state==='connected';
    if(ready()){resolve(true);return;}
    const timer=setTimeout(()=>{observer.disconnect();reject(new Error('Native handshake missing'));},10000);
    const observer=new MutationObserver(()=>{if(ready()){clearTimeout(timer);observer.disconnect();resolve(true);}});
    observer.observe(document,{childList:true,subtree:true,attributes:true});
  })`);
  report.initial = await evaluate(`({
    state: document.querySelector('#connection-status').dataset.state,
    headings: document.querySelectorAll('#markdown-sample h3').length,
    keywords: document.querySelectorAll('.hljs-keyword').length,
    diffSides: document.querySelectorAll('.d2h-file-side-diff').length,
    total: document.querySelector('.message').getAttribute('aria-setsize'),
    rows: document.querySelectorAll('.message').length,
    background: getComputedStyle(document.documentElement).getPropertyValue('--vs-background'),
    width: innerWidth, height: innerHeight
  })`);
  assert.equal(report.initial.state, 'connected');
  assert.ok(report.initial.headings > 0 && report.initial.keywords > 0);
  assert.equal(report.initial.diffSides, 2);
  assert.equal(report.initial.total, '500');
  assert.ok(report.initial.rows < 30);
  report.last = await evaluate(`(() => {
    document.querySelector('#last-message').click();
    return [...document.querySelectorAll('.message')].at(-1).getAttribute('aria-posinset');
  })()`);
  assert.equal(report.last, '500');
  report.composer = await evaluate(`(() => {
    const input = document.querySelector('#message-input');
    const before = input.getBoundingClientRect().height;
    input.value = 'Eco real C#\\nLinha 2\\nLinha 3\\nLinha 4\\nLinha 5';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    return { before, after: input.getBoundingClientRect().height };
  })()`);
  assert.ok(report.composer.after > report.composer.before);
  report.echo = await evaluate(`new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Echo did not reach the UI')), 10000);
    const observer = new MutationObserver(() => {
      const host = document.querySelector('.message[data-kind="host"] .message-text');
      if (!host) return;
      clearTimeout(timer); observer.disconnect(); resolve(host.textContent);
    });
    observer.observe(document.querySelector('#history-items'), { childList: true, subtree: true });
    document.querySelector('#send').click();
  })`);
  assert.equal(report.echo, 'Eco real C#\nLinha 2\nLinha 3\nLinha 4\nLinha 5');
  report.negative = await evaluate(`new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Malformed message was not answered')), 10000);
    const handler = event => {
      if (event.data.type !== 'error' || event.data.id !== null) return;
      clearTimeout(timer); chrome.webview.removeEventListener('message', handler); resolve(event.data);
    };
    chrome.webview.addEventListener('message', handler);
    chrome.webview.postMessage({ version: 1, id: 'not-a-uuid', type: 'run_command', payload: {} });
  })`);
  assert.equal(report.negative.payload.code, 'invalid_message');
  report.unicodeNegative = await evaluate(`new Promise((resolve, reject) => {
    const id = crypto.randomUUID();
    const timer = setTimeout(() => reject(new Error('Malformed Unicode was not answered')), 10000);
    const handler = event => {
      if (event.data.id !== id) return;
      clearTimeout(timer); chrome.webview.removeEventListener('message', handler); resolve(event.data);
    };
    chrome.webview.addEventListener('message', handler);
    chrome.webview.postMessage({ version: 1, id, type: 'ping', payload: { text: String.fromCharCode(0xd800) } });
  })`);
  assert.equal(report.unicodeNegative.type, 'error');
  assert.equal(report.unicodeNegative.payload.code, 'invalid_message');
  report.recovered = await evaluate(`new Promise((resolve, reject) => {
    const id = crypto.randomUUID();
    const timer = setTimeout(() => reject(new Error('Host did not recover')), 10000);
    const handler = event => {
      if (event.data.id !== id) return;
      clearTimeout(timer); chrome.webview.removeEventListener('message', handler); resolve(event.data);
    };
    chrome.webview.addEventListener('message', handler);
    chrome.webview.postMessage({ version: 1, id, type: 'ping', payload: { text: 'after-negative-control' } });
  })`);
  assert.equal(report.recovered.type, 'ping.result');
  assert.equal(report.recovered.payload.text, 'after-negative-control');
  await command('Network.setCacheDisabled', { cacheDisabled: true });
  await command('Network.emulateNetworkConditions', { offline: true, latency: 0, downloadThroughput: 0, uploadThroughput: 0 });
  events.length = 0;
  await command('Page.reload', { ignoreCache: true });
  report.offline = await evaluate(`new Promise((resolve, reject) => {
    const timer = setTimeout(() => { observer.disconnect(); reject(new Error('Offline handshake failed')); }, 10000);
    const check = () => {
      if (document.querySelector('#connection-status')?.dataset.state !== 'connected') return;
      clearTimeout(timer); observer.disconnect(); resolve({
        state: document.querySelector('#connection-status').dataset.state,
        rows: document.querySelectorAll('.message').length,
        total: document.querySelector('.message')?.getAttribute('aria-setsize'),
        diffSides: document.querySelectorAll('.d2h-file-side-diff').length
      });
    };
    const observer = new MutationObserver(check);
    observer.observe(document, { childList: true, subtree: true, attributes: true });
    check();
  })`);
  assert.equal(report.offline.total, '500');
  assert.equal(report.offline.diffSides, 2);
  report.requests = events.filter(event => event.method === 'Network.requestWillBeSent').map(event => event.params.request.url);
  assert.ok(report.requests.length > 0, 'The offline check must observe actual uncached requests.');
  assert.ok(report.requests.every(url => new URL(url).origin === 'https://lumen.local'));
  report.exceptions = events.filter(event => event.method === 'Runtime.exceptionThrown');
  assert.equal(report.exceptions.length, 0);
  const screenshot = await command('Page.captureScreenshot', { format: 'png' });
  await writeFile(new URL('webview-offline.png', artifacts), Buffer.from(screenshot.data, 'base64'));
  report.responsive = [];
  for (const [width, height] of [[203, 422], [320, 320], [375, 700], [900, 900]]) {
    await command('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
    const layout = await evaluate(`(() => {
      const input = document.querySelector('#message-input');
      input.value = 'Line 1\\nLine 2\\nLine 3\\nLine 4\\nLine 5';
      input.dispatchEvent(new Event('input', { bubbles: true }));
      return { width: innerWidth, height: innerHeight,
        overflow: document.documentElement.scrollWidth > innerWidth,
        overlap: document.querySelector('.sample-scroll').getBoundingClientRect().bottom > document.querySelector('.history').getBoundingClientRect().top,
        rows: document.querySelectorAll('.message').length };
    })()`);
    assert.equal(layout.overflow, false, JSON.stringify(layout));
    assert.equal(layout.overlap, false, JSON.stringify(layout));
    assert.ok(layout.rows < 30);
    report.responsive.push(layout);
    if (width === 203) {
      report.layoutNegative = await evaluate(`(() => {
        const app = document.querySelector('.app');
        app.style.minWidth = '240px';
        const detected = document.documentElement.scrollWidth > innerWidth;
        app.style.removeProperty('min-width');
        return detected;
      })()`);
      assert.equal(report.layoutNegative, true, 'The layout checker must reject a forced overflow.');
    }
  }
  await command('Emulation.clearDeviceMetricsOverride');
  await writeFile(new URL('webview-verification.json', artifacts), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify(report, null, 2));
} finally {
  try {
    await command('Emulation.clearDeviceMetricsOverride');
    await command('Network.emulateNetworkConditions', { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
  } finally { socket.close(); }
}
