import assert from 'node:assert/strict';
import { readFile, writeFile, access } from 'node:fs/promises';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';

const manifest = JSON.parse((await readFile(new URL('../artifacts/context-fixtures.json', import.meta.url), 'utf8')).replace(/^\uFEFF/, ''));
const automation = async action => {
  const { stdout } = await promisify(execFile)('powershell.exe', ['-NoProfile', '-File', fileURLToPath(new URL('./context-experimental.ps1', import.meta.url)), '-Action', action, '-InstanceId', '25e762c6']);
  return ['WriteInspect', 'NativeUndo', 'NativeRedo', 'UserRoundTrip'].includes(action) ? JSON.parse(stdout.trim()) : stdout;
};
const pages = await (await fetch('http://127.0.0.1:9223/json/list')).json();
const page = pages.find(item => item.url === 'https://lumen.local/index.html');
assert.ok(page, 'Open Lumen in the experimental instance.');
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
let sequence = 0;
const pending = new Map(), exceptions = [];
socket.addEventListener('message', event => {
  const data = JSON.parse(event.data);
  if (data.method === 'Runtime.exceptionThrown') exceptions.push(data.params.exceptionDetails.text);
  const task = pending.get(data.id); if (!task) return;
  pending.delete(data.id); clearTimeout(task.timer);
  if (data.error) task.reject(new Error(data.error.message)); else task.resolve(data.result);
});
function command(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++sequence, timer = setTimeout(() => { pending.delete(id); reject(new Error(method)); }, 30000);
    pending.set(id, { resolve, reject, timer }); socket.send(JSON.stringify({ id, method, params }));
  });
}
async function evaluate(expression) {
  const value = await command('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result.value;
}
async function rpc(type, payload) {
  return evaluate(`new Promise((resolve,reject)=>{const id=crypto.randomUUID(),timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('RPC timeout'));},15000);const handler=e=>{if(e.data.id!==id)return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);resolve(e.data);};chrome.webview.addEventListener('message',handler);chrome.webview.postMessage({version:1,id,type:${JSON.stringify(type)},payload:${JSON.stringify(payload)}});})`);
}
async function uiAction(type, expression) {
  return evaluate(`new Promise((resolve,reject)=>{const timer=setTimeout(()=>{chrome.webview.removeEventListener('message',handler);reject(new Error('UI timeout'));},15000);const handler=e=>{if(e.data.type!==${JSON.stringify(type)}&&e.data.type!=='error')return;clearTimeout(timer);chrome.webview.removeEventListener('message',handler);setTimeout(()=>resolve(e.data),0);};chrome.webview.addEventListener('message',handler);(()=>{${expression}})();})`);
}
const write = (name, file, content) => rpc('byok.tool.write', { name, arguments: { path: file, content } });
const apply = (name, file, content) => uiAction('byok.tool.write.result', `document.querySelector('#byok-write-name').value=${JSON.stringify(name)};document.querySelector('#byok-write-path').value=${JSON.stringify(file)};document.querySelector('#byok-write-content').value=${JSON.stringify(content)};document.querySelector('#byok-write-apply').click();`);
const unwrap = reply => { assert.equal(reply.type, 'byok.tool.write.result', JSON.stringify(reply)); assert.equal(reply.payload.isError, false, reply.payload.content); return JSON.parse(reply.payload.content); };
const normalized = text => text.replaceAll('\r\n', '\n').replaceAll('\r', '\n');
const root = manifest.root;
const sample = path.join(root, 'Sample.cs');
const createdPath = path.join(root, 'WriteFixture.txt');
const beforeDisk = await readFile(sample);
const originalBuffer = await automation('WriteInspect');
const report = { source: 'real VS Exp/WebView2; explicit Apply/Undo UI and native RPC negatives; no providers or paid requests', paidRequests: 0 };
try {
  await command('Runtime.enable'); await command('Page.enable');
  assert.equal(originalBuffer.saved, false);
  assert.ok(originalBuffer.text.includes(manifest.marker + '-unsaved'));
  assert.equal((await rpc('byok.tool.write.status', {})).payload.pending, 0);
  await uiAction('byok.tool.write.status.result', "document.querySelector('#byok-write-tools').open=true;");
  const created = unwrap(await apply('create_file', 'WriteFixture.txt', 'created\n'));
  assert.equal(created.created, true);
  assert.ok(created.diff.changes.some(change => change.op === 'add'));
  const edited = unwrap(await apply('edit_file', 'WriteFixture.txt', '<script>window.writeExecuted=true</script>\n'));
  assert.ok(edited.diff.changes.some(change => change.op === 'remove'));
  assert.equal(await evaluate('window.writeExecuted === true'), false);
  assert.equal(await evaluate("document.querySelector('#byok-write-diff').children.length"), 0);
  assert.ok((await evaluate("document.querySelector('#byok-write-diff').textContent")).includes('+ <script>window.writeExecuted=true</script> [LF]'));
  const buffer = unwrap(await apply('edit_file', 'Sample.cs', 'public class ChangedByLumen {}\r\n'));
  assert.equal(buffer.buffer, true);
  assert.equal(normalized((await automation('WriteInspect')).text), 'public class ChangedByLumen {}\n');
  assert.deepEqual(await readFile(sample), beforeDisk);
  unwrap(await apply('edit_file', 'Sample.cs', 'public class SecondLumenEdit {}\r\n'));
  assert.equal(normalized((await automation('WriteInspect')).text), 'public class SecondLumenEdit {}\n');
  assert.equal((await rpc('byok.tool.write.status', {})).payload.pending, 4);
  assert.ok((await evaluate("document.querySelector('#byok-write-pending').textContent")).includes('4 / 64'));
  await evaluate("document.querySelector('#byok-write-diff').scrollIntoView({block:'end'});true");
  const screenshot = await command('Page.captureScreenshot', { format: 'png' });
  await writeFile(new URL('../artifacts/write-tools-webview.png', import.meta.url), Buffer.from(screenshot.data, 'base64'));
  report.explicitApplyUndoAndPlainDiff = true;
  const undone = await uiAction('byok.tool.write.undo.result', "document.querySelector('#byok-write-undo').click();");
  assert.equal(undone.type, 'byok.tool.write.undo.result', JSON.stringify(undone)); assert.equal(undone.payload.pending, 0);
  await assert.rejects(access(createdPath));
  assert.equal((await automation('WriteInspect')).text, originalBuffer.text);
  report.diskAndBufferBatchUndo = true; report.unsavedBufferPreserved = true;

  unwrap(await write('edit_file', 'Sample.cs', 'public class NativeUndoFixture {}\r\n'));
  assert.equal((await automation('NativeUndo')).text, originalBuffer.text);
  const nativeConflict = await rpc('byok.tool.write.undo', {});
  assert.equal(nativeConflict.type, 'error'); assert.equal(nativeConflict.payload.code, 'undo_conflict');
  assert.equal(normalized((await automation('NativeRedo')).text), 'public class NativeUndoFixture {}\n');
  assert.equal((await rpc('byok.tool.write.undo', {})).payload.pending, 0);
  report.nativeUndoRedo = true;

  unwrap(await write('create_file', 'WriteFixture.txt', 'owned'));
  await writeFile(createdPath, 'user-change');
  const conflict = await rpc('byok.tool.write.undo', {});
  assert.equal(conflict.type, 'error'); assert.equal(conflict.payload.code, 'undo_conflict');
  assert.equal(await readFile(createdPath, 'utf8'), 'user-change');
  assert.equal((await rpc('byok.tool.write.status', {})).payload.pending, 1);
  await writeFile(createdPath, 'owned');
  assert.equal((await rpc('byok.tool.write.undo', {})).payload.pending, 0);
  report.externalChangeNotOverwritten = true;

  for (const file of ['../STATUS.md', 'Sample.cs:secret', 'C:/outside.cs']) {
    const reply = await write('edit_file', file, 'forged');
    assert.equal(reply.type, 'error'); assert.equal(reply.payload.code, 'invalid_path');
  }
  const mode = await rpc('byok.tool.write', { name: 'edit_file', arguments: { path: 'Sample.cs', content: 'forged' }, mode: 'Agent' });
  assert.equal(mode.payload.code, 'invalid_message');
  const unknown = await write('run_command', 'Sample.cs', 'forged'); assert.equal(unknown.payload.code, 'unknown_tool');
  assert.equal((await rpc('byok.tool.write.status', {})).payload.pending, 0);
  assert.deepEqual(await readFile(sample), beforeDisk);
  assert.equal((await automation('WriteInspect')).text, originalBuffer.text);
  report.pathAndAuthorityNegatives = true;

  unwrap(await write('edit_file', 'Sample.cs', 'public class UserChangeFixture {}\r\n'));
  const ownedText = (await automation('WriteInspect')).text;
  assert.equal((await automation('UserRoundTrip')).text, ownedText);
  const sameTextConflict = await rpc('byok.tool.write.undo', {});
  assert.equal(sameTextConflict.type, 'error'); assert.equal(sameTextConflict.payload.code, 'undo_conflict');
  assert.equal((await automation('WriteInspect')).text, ownedText);
  assert.deepEqual(await readFile(sample), beforeDisk);
  report.userEditWithSameTextDenied = true;

  await automation('Close');
  const closed = await write('create_file', 'WriteFixture.txt', 'forged');
  assert.equal(closed.type, 'error'); assert.equal(closed.payload.code, 'workspace_unavailable');
  report.closedSolutionDenied = true;
  await command('Emulation.setDeviceMetricsOverride', { width: 203, height: 900, deviceScaleFactor: 1, mobile: false });
  report.narrowLayout = await evaluate('({width:innerWidth,overflow:document.documentElement.scrollWidth>innerWidth})'); assert.equal(report.narrowLayout.overflow, false);
  await command('Emulation.clearDeviceMetricsOverride');
  assert.deepEqual(exceptions, []); report.javascriptExceptions = 0;
  await writeFile(new URL('../artifacts/write-tools-webview.json', import.meta.url), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report));
} finally {
  try { await command('Emulation.clearDeviceMetricsOverride'); } catch {}
  for (const task of pending.values()) clearTimeout(task.timer);
  socket.close();
}
