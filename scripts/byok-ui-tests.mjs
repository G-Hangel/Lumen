import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { ByokBridge, isByokEnvelope, isByokEvent, MAX_IMAGE_BYTES, PROBE_CAPABILITIES, PROBE_PROVIDER_KINDS, MAX_PROBE_BUDGET } from '../src/Lumen.Vsix/wwwroot/byok-bridge.mjs';
import { startByok, capabilityState, errorLabel, readImageAttachment, DEFAULT_BASE_URLS } from '../src/Lumen.Vsix/wwwroot/byok.mjs';

const root = new URL('../src/Lumen.Vsix/wwwroot/', import.meta.url);
const html = await readFile(new URL('index.html', root), 'utf8');
const css = await readFile(new URL('styles.css', root), 'utf8');
const pt = JSON.parse(await readFile(new URL('locales/pt-BR.json', root), 'utf8'));
const en = JSON.parse(await readFile(new URL('locales/en.json', root), 'utf8'));
const configurationId = '11111111-1111-4111-8111-111111111111';
const otherId = '22222222-2222-4222-8222-222222222222';
const config = { id: configurationId, providerId: 'local', kind: 'Ollama', baseUrl: 'http://127.0.0.1:11434/', selectedModelId: null, deploymentNames: [] };
const model = { id: 'local-model', name: 'Local model', vision: true, tools: true, reasoning: true,
  origins: ['Vision', 'Tools', 'Reasoning'].map(capability => ({ capability, source: 'Native', confidence: 'High' })), efforts: ['Low', 'High'] };
const catalog = (configuration = config, models = [model], extra = {}) => ({ configuration, models, credentialValid: true, code: null, ...extra });
const envelope = (type, payload, id = configurationId) => ({ version: 1, id, type, payload });
const rejects = (promise, code) => assert.rejects(promise, error => error.code === code && error.message === code);
const tick = () => new Promise(resolve => setImmediate(resolve));

class FakeTransport {
  messages = [];
  listeners = new Set();
  addEventListener(type, listener) { assert.equal(type, 'message'); this.listeners.add(listener); }
  removeEventListener(type, listener) { this.listeners.delete(listener); }
  postMessage(message) { this.messages.push(message); }
  emit(message) { for (const listener of this.listeners) listener({ data: message }); }
  reply(payload, request = this.messages.at(-1)) { this.emit(envelope(`${request.type}.result`, payload, request.id)); }
}
function fixture(t, options = {}) {
  const transport = new FakeTransport();
  const bridge = new ByokBridge(transport, { connected: () => true, ...options });
  t.after(() => bridge.dispose());
  return { bridge, transport };
}

test('negative envelopes reject malformed, secret-bearing, oversized and unknown shapes', () => {
  const good = envelope('byok.discover.result', catalog());
  assert.equal(isByokEnvelope(good), true);
  const badModels = [{ ...model, efforts: ['Ultra'] }, { ...model, origins: [{ capability: 'Vision', source: 'Invented', confidence: 'High' }] },
    { ...model, origins: [{ capability: 'Vision', source: 'Native', confidence: 'Certain' }] }, { ...model, vision: 'true' },
    { ...model, id: 'x'.repeat(513) }, { ...model, origins: [...model.origins, model.origins[0]] }, { ...model, efforts: ['Low', 'Low'] }];
  const bad = [null, [], {}, '{"version":', { ...good, version: 2 }, { ...good, id: null }, { ...good, id: 'not-guid' },
    { ...good, id: '00000000-0000-0000-0000-000000000000' }, { ...good, key: 'secret' },
    envelope('byok.discover.result', { ...catalog(), key: 'secret' }), envelope('byok.discover.result', { ...catalog(), credentialValid: 'true' }),
    envelope('byok.list.result', { remainingBudget: 5, configurations: [{ ...config, key: 'secret' }] }), envelope('byok.list.result', { remainingBudget: 5, configurations: [config, config] }),
    envelope('byok.list.result', { remainingBudget: 5, configurations: [{ ...config, baseUrl: 'https://user:secret@example.org/' }] }),
    envelope('byok.chat.result', { completed: 1 }), envelope('byok.cancel.result', { cancelled: false }), envelope('byok.unknown.result', {}),
    envelope('error', { code: 'invalid_key', message: 'secret' }), ...badModels.map(item => envelope('byok.discover.result', catalog(config, [item])))];
  for (const message of bad) assert.equal(isByokEnvelope(message), false);
  assert.equal(isByokEnvelope(envelope('byok.list.result', { remainingBudget: 5, configurations: Array.from({ length: 257 }, () => config) })), false);
  assert.equal(isByokEnvelope(envelope('byok.discover.result', catalog(config, [model, model]))), false);
});

test('probe/list results require an integer session budget in 0..5 and exact nullable support/model shapes', () => {
  assert.equal(MAX_PROBE_BUDGET, 5);
  assert.deepEqual(PROBE_CAPABILITIES, ['Vision', 'Tools']);
  assert.deepEqual(PROBE_PROVIDER_KINDS, ['OpenAI', 'OpenRouter', 'LMStudio', 'Azure']);
  for (let remainingBudget = 0; remainingBudget <= 5; remainingBudget++) {
    assert.equal(isByokEnvelope(envelope('byok.list.result', { configurations: [config], remainingBudget })), true);
    for (const supported of [true, false, null]) assert.equal(isByokEnvelope(envelope('byok.probe.result', { model, supported, remainingBudget })), true);
  }
  for (const remainingBudget of [-1, 6, 0.5, null, '5', true, NaN, Infinity, undefined]) {
    assert.equal(isByokEnvelope(envelope('byok.list.result', { configurations: [], remainingBudget })), false);
    assert.equal(isByokEnvelope(envelope('byok.probe.result', { model, supported: true, remainingBudget })), false);
  }
  assert.equal(isByokEnvelope(envelope('byok.list.result', { configurations: [] })), false);
  for (const result of [{ model, supported: true }, { model, remainingBudget: 5 }, { supported: true, remainingBudget: 5 },
    { model, supported: 'true', remainingBudget: 5 }, { model, supported: 0, remainingBudget: 5 },
    { model: { ...model, vision: null }, supported: null, remainingBudget: 5 },
    { model, supported: true, remainingBudget: 5, capability: 'Vision' },
    { model: { ...model, key: 'secret' }, supported: false, remainingBudget: 5 }]) {
    assert.equal(isByokEnvelope(envelope('byok.probe.result', result)), false);
  }
});

test('probe bridge refuses missing consent, reasoning, unselected wire model IDs and malformed requests before posting', async t => {
  const f = fixture(t);
  const valid = { configurationId, capability: 'Vision', consent: true };
  for (const payload of [{ configurationId, capability: 'Vision' }, ...[false, null, 1, 'true', undefined].map(consent => ({ ...valid, consent })),
    ...['Reasoning', 'reasoning', 'vision', 'All', '', null].map(capability => ({ ...valid, capability })),
    { ...valid, configurationId: otherId + 'bad' }, { ...valid, modelId: model.id }, { ...valid, key: 'secret' }, { ...valid, capabilities: ['Vision', 'Tools'] }]) {
    await rejects(f.bridge.request('byok.probe', payload), 'invalid_message');
  }
  await rejects(f.bridge.request('byok.probe', valid, { expectedModelId: '' }), 'invalid_message');
  assert.equal(f.transport.messages.length, 0);
  for (const capability of PROBE_CAPABILITIES) {
    const request = f.bridge.request('byok.probe', { ...valid, capability }, { expectedModelId: model.id });
    const sent = f.transport.messages.at(-1);
    assert.deepEqual(sent.payload, { configurationId, capability, consent: true });
    assert.equal(Object.hasOwn(sent.payload, 'modelId'), false);
    f.transport.reply({ model, supported: null, remainingBudget: 3 });
    assert.deepEqual(await request, { model, supported: null, remainingBudget: 3 });
  }
});

test('probe bridge correlates request and expected model without putting model identity on the wire', async t => {
  const f = fixture(t);
  const payload = { configurationId, capability: 'Tools', consent: true };
  for (const result of [{ model: { ...model, id: 'other-model' }, supported: true, remainingBudget: 4 },
    { model, supported: null, remainingBudget: 6 }, { model, supported: 'false', remainingBudget: 4 }]) {
    const request = f.bridge.request('byok.probe', payload, { expectedModelId: model.id });
    const sent = f.transport.messages.at(-1);
    assert.equal(f.bridge.receive(envelope('byok.probe.result', { model, supported: true, remainingBudget: 4 }, otherId)), false);
    assert.equal(f.bridge.pending.size, 1);
    f.transport.reply(result, sent); await rejects(request, 'invalid_response');
    assert.equal(f.bridge.pending.size, 0);
    assert.equal(f.bridge.receive(envelope('byok.probe.result', { model, supported: true, remainingBudget: 4 }, sent.id)), false);
  }
  const wrongType = f.bridge.request('byok.probe', payload, { expectedModelId: model.id });
  f.transport.emit(envelope('byok.override.result', { model }, f.transport.messages.at(-1).id));
  await rejects(wrongType, 'invalid_response');
});

test('negative event validators reject unknown kinds, malformed tool JSON and invalid usage', () => {
  for (const event of [{ kind: 'Unknown', text: 'bad' }, { kind: 'TextDelta', text: 1 }, { kind: 'TextDelta', text: 'x'.repeat(1024 * 1024 + 1) },
    { kind: 'ToolCallStart', id: 'tool', name: '<script>' }, { kind: 'ToolCallStart', id: 'tool', name: 123 },
    { kind: 'ToolCallEnd', id: 'tool', arguments: 'truncated {' }, { kind: 'ToolCallEnd', id: 'tool', arguments: [] },
    { kind: 'Usage', input: -1, output: 0 }, { kind: 'Usage', input: 0, output: Infinity }, { kind: 'Done', reason: 'Length' },
    { kind: 'Error', code: 'invalid_key', detail: 'secret' }]) assert.equal(isByokEvent(event), false);
  let nested = {};
  for (let depth = 0; depth < 18; depth++) nested = { child: nested };
  assert.equal(isByokEvent({ kind: 'ToolCallEnd', id: 'tool', arguments: nested }), false);
  assert.equal(isByokEvent({ kind: 'ToolCallEnd', id: 'tool', arguments: { file: '</pre><script>' } }), true);
  for (const reason of ['Stop', 'MaxTokens', 'ToolUse', 'ContentFiltered', 'Cancelled', 'Other']) assert.equal(isByokEvent({ kind: 'Done', reason }), true);
});

test('no native host / no real handshake: no request leaves the bridge', async t => {
  for (const transport of [undefined, {}, { postMessage() {} }]) {
    const bridge = new ByokBridge(transport, { connected: () => true });
    await rejects(bridge.request('byok.list'), 'bridge_unavailable');
    await rejects(bridge.request('byok.probe', { configurationId, capability: 'Vision', consent: true }), 'bridge_unavailable'); bridge.dispose();
  }
  const f = fixture(t, { connected: () => false });
  await rejects(f.bridge.request('byok.configure', { ...config, key: 'secret' }), 'bridge_unavailable');
  await rejects(f.bridge.request('byok.chat', { configurationId, text: 'hello', effort: 'Off' }), 'bridge_unavailable');
  await rejects(f.bridge.request('byok.probe', { configurationId, capability: 'Vision', consent: true }), 'bridge_unavailable');
  assert.equal(f.transport.messages.length, 0);
});

test('outgoing requests validate supported types, GUIDs, URLs, keys and exact fields', async t => {
  const f = fixture(t);
  await rejects(f.bridge.request('ping', { text: 'hello' }), 'unsupported_type');
  for (const [type, payload] of [['byok.list', { probe: true }], ['byok.cancel', { configurationId }],
    ['byok.configure', { id: configurationId, providerId: 'x', kind: 'Invented', baseUrl: config.baseUrl, key: '' }],
    ['byok.configure', { id: configurationId, providerId: 'x', kind: 'OpenAI', baseUrl: 'https://user:secret@example.org/', key: '' }],
    ['byok.configure', { id: configurationId, providerId: 'x', kind: 'OpenAI', baseUrl: 'http://example.org/', key: '' }],
    ['byok.configure', { id: configurationId, providerId: 'x', kind: 'OpenAI', baseUrl: 'https://example.org/?key=secret', key: '' }],
    ['byok.configure', { id: configurationId, providerId: 'x', kind: 'OpenAI', baseUrl: 'https://example.org/', key: 'line\nbreak' }],
    ['byok.discover', { configurationId: 'invalid' }], ['byok.select', { configurationId, modelId: '' }],
    ['byok.override', { configurationId, modelId: model.id, vision: true, tools: true, reasoning: null }],
    ['byok.chat', { configurationId, text: 'hello', effort: 'high' }],
    ['byok.chat', { configurationId, text: 'hello', effort: 'Off', image: { mediaType: 'image/svg+xml', base64Data: 'AAAA' } }],
    ['byok.chat', { configurationId, text: 'hello', effort: 'Off', image: { mediaType: 'image/png', base64Data: '!@#$' } }]]) {
    await rejects(f.bridge.request(type, payload), 'invalid_message');
  }
  assert.equal(f.transport.messages.length, 0);
  const request = f.bridge.request('byok.list');
  const sent = f.transport.messages.at(-1);
  assert.deepEqual(Object.keys(sent).sort(), ['id', 'payload', 'type', 'version']);
  assert.match(sent.id, /^[0-9a-f-]{36}$/); assert.equal(sent.version, 1); assert.deepEqual(sent.payload, {});
  f.transport.reply({ remainingBudget: 5, configurations: [] }); await request;
});

test('only own pending ids consume errors; unknown ids and replayed completion cannot finish work', async t => {
  const f = fixture(t);
  const request = f.bridge.request('byok.list'); const sent = f.transport.messages.at(-1);
  for (const message of [envelope('error', { code: 'invalid_key' }, otherId), envelope('byok.list.result', { remainingBudget: 5, configurations: [] }, otherId),
    envelope('error', { code: 'invalid_key' }, null), envelope('ready.result', {}, otherId), '{"version":']) {
    assert.equal(f.bridge.receive(message), false); assert.equal(f.bridge.pending.size, 1);
  }
  f.transport.reply({ remainingBudget: 5, configurations: [] }); assert.deepEqual(await request, { remainingBudget: 5, configurations: [] });
  assert.equal(f.bridge.receive(envelope('byok.list.result', { remainingBudget: 5, configurations: [] }, sent.id)), false);
  const next = f.bridge.request('byok.list');
  f.transport.emit(envelope('error', { code: 'invalid_key' }, sent.id)); assert.equal(f.bridge.pending.size, 1);
  f.transport.reply({ remainingBudget: 5, configurations: [] }); await next;
});

test('wrong result type, malformed owned response and configuration/model mismatch reject correlation', async t => {
  for (const [type, payload, responseType, result] of [
    ['byok.list', {}, 'byok.cancel.result', { cancelled: true }],
    ['byok.list', {}, 'byok.list.result', { remainingBudget: 5, configurations: [], key: 'secret' }],
    ['byok.discover', { configurationId }, 'byok.discover.result', catalog({ ...config, id: otherId })],
    ['byok.configure', { id: configurationId, providerId: 'local', kind: 'Ollama', baseUrl: config.baseUrl, key: '' }, 'byok.configure.result', catalog({ ...config, kind: 'OpenAI' })],
    ['byok.select', { configurationId, modelId: model.id }, 'byok.select.result', { configuration: { ...config, selectedModelId: 'wrong' } }],
    ['byok.override', { configurationId, modelId: model.id, vision: false, tools: false, reasoning: false }, 'byok.override.result', { model: { ...model, id: 'wrong' } }]]) {
    const f = fixture(t); const request = f.bridge.request(type, payload);
    f.transport.emit(envelope(responseType, result, f.transport.messages.at(-1).id));
    await rejects(request, 'invalid_response'); assert.equal(f.bridge.pending.size, 0);
  }
});

test('streaming supports all kinds and concurrent cancellation but rejects tool lifecycle / terminal replay', async t => {
  const f = fixture(t); const events = [];
  const request = f.bridge.request('byok.chat', { configurationId, text: 'hello', effort: 'High' }, { onEvent: value => events.push(value) });
  const chat = f.transport.messages.at(-1);
  await rejects(f.bridge.request('byok.list'), 'bridge_busy');
  const cancel = f.bridge.request('byok.cancel'); const cancelSent = f.transport.messages.at(-1);
  await rejects(f.bridge.request('byok.cancel'), 'bridge_busy');
  for (const event of [{ kind: 'TextDelta', text: 'hello' }, { kind: 'ReasoningDelta', text: 'thinking' },
    { kind: 'ToolCallStart', id: 'call', name: 'read_file' }, { kind: 'ToolCallArgsDelta', id: 'call', chunk: '{"path":' },
    { kind: 'ToolCallArgsDelta', id: 'call', chunk: '"a"}' }, { kind: 'ToolCallEnd', id: 'call', arguments: { path: 'a' } },
    { kind: 'Usage', input: 2, output: 3 }, { kind: 'Done', reason: 'ToolUse' }]) f.transport.emit(envelope('byok.event', event, chat.id));
  f.transport.reply({ cancelled: true }, cancelSent); await cancel;
  f.transport.reply({ completed: true }, chat); await request;
  assert.equal(events.length, 8); assert.equal(f.bridge.receive(envelope('byok.event', { kind: 'TextDelta', text: 'replay' }, chat.id)), false);
  for (const bad of [{ kind: 'ToolCallArgsDelta', id: 'absent', chunk: '{}' }, { kind: 'ToolCallEnd', id: 'absent', arguments: {} }, { kind: 'Unknown' }]) {
    const request = f.bridge.request('byok.chat', { configurationId, text: 'hello', effort: 'Off' });
    f.transport.emit(envelope('byok.event', bad, f.transport.messages.at(-1).id)); await rejects(request, 'invalid_response');
  }
  const terminal = f.bridge.request('byok.chat', { configurationId, text: 'hello', effort: 'Off' }); const sent = f.transport.messages.at(-1);
  f.transport.emit(envelope('byok.event', { kind: 'Done', reason: 'Stop' }, sent.id));
  f.transport.emit(envelope('byok.event', { kind: 'Done', reason: 'Stop' }, sent.id)); await rejects(terminal, 'invalid_response');
});

test('error codes never carry keys, payloads are not retained, and failed streams cannot claim completion', async t => {
  const f = fixture(t); const key = 'secret_credential_do_not_show';
  const request = f.bridge.request('byok.configure', { id: configurationId, providerId: 'local', kind: 'Ollama', baseUrl: config.baseUrl, key });
  const pending = f.bridge.pending.values().next().value;
  assert.equal(Object.hasOwn(pending, 'payload'), false); assert.doesNotMatch(JSON.stringify(pending, (name, value) => name === 'timer' ? '[timer]' : value), new RegExp(key));
  f.transport.emit(envelope('error', { code: key }, f.transport.messages.at(-1).id));
  await assert.rejects(request, error => error.code === 'unknown' && !String(error.stack).includes(key));
  const events = [];
  const chat = f.bridge.request('byok.chat', { configurationId, text: 'hello', effort: 'Off' }, { onEvent: value => events.push(value) });
  const sent = f.transport.messages.at(-1);
  f.transport.emit(envelope('byok.event', { kind: 'Error', code: key }, sent.id));
  assert.deepEqual(events, [{ kind: 'Error', code: 'unknown' }]);
  f.transport.reply({ completed: true }); await rejects(chat, 'invalid_response');
});

test('image payloads accept the 4 MB boundary without regex recursion and reject one extra byte', async t => {
  const f = fixture(t); assert.equal(f.bridge.timeoutMs, 120000);
  const base64Data = Buffer.alloc(MAX_IMAGE_BYTES).toString('base64');
  const request = f.bridge.request('byok.chat', { configurationId, text: 'image', effort: 'Off', image: { mediaType: 'image/png', base64Data } });
  f.transport.reply({ completed: true }); await request;
  await rejects(f.bridge.request('byok.chat', { configurationId, text: 'image', effort: 'Off',
    image: { mediaType: 'image/png', base64Data: Buffer.alloc(MAX_IMAGE_BYTES + 1).toString('base64') } }), 'invalid_message');
});

test('timeouts, transport failures, disconnect, disposal and reused request GUIDs are controlled', async t => {
  const f = fixture(t, { timeoutMs: 5 });
  await rejects(f.bridge.request('byok.list'), 'timeout'); assert.equal(f.bridge.pending.size, 0);
  f.transport.postMessage = () => { throw new Error('secret-key'); };
  await rejects(f.bridge.request('byok.list'), 'bridge_unavailable');
  f.transport.postMessage = () => {};
  const disconnected = f.bridge.request('byok.list'); f.bridge.disconnect(); await rejects(disconnected, 'bridge_unavailable');
  const interrupted = f.bridge.request('byok.list'); f.bridge.dispose(); await rejects(interrupted, 'bridge_unavailable'); assert.equal(f.transport.listeners.size, 0);
  const reused = fixture(t, { newId: () => configurationId });
  const first = reused.bridge.request('byok.list'); reused.transport.reply({ remainingBudget: 5, configurations: [] }); await first;
  await rejects(reused.bridge.request('byok.list'), 'invalid_message');
});

test('capability unknown is not false; origin and low-confidence heuristic remain explicit', () => {
  assert.deepEqual(capabilityState({ ...model, vision: false, origins: [] }, 'vision'), { value: null, source: null, confidence: null, unverified: true });
  assert.deepEqual(capabilityState({ ...model, vision: false }, 'vision'), { value: false, source: 'Native', confidence: 'High', unverified: false });
  assert.equal(capabilityState({ ...model, origins: [{ capability: 'Vision', source: 'Heuristic', confidence: 'Low' }] }, 'vision').unverified, true);
  assert.equal(new Set(['invalid_key', 'permission_denied', 'invalid_url', 'rate_limited', 'insufficient_credit'].map(errorLabel)).size, 5);
});

test('negative image checks reject disallowed MIME, spoofed files and >4 MB; valid signatures pass offline', async () => {
  const file = (type, bytes) => ({ type, size: bytes.length, arrayBuffer: async () => Uint8Array.from(bytes).buffer });
  for (const type of ['image/svg+xml', 'text/html', '', 'image/png; charset=utf-8']) await rejects(readImageAttachment(file(type, [1, 2, 3])), 'invalid_image_type');
  await rejects(readImageAttachment(file('image/png', [60, 115, 99, 114, 105, 112, 116, 62])), 'invalid_image_type');
  await rejects(readImageAttachment({ type: 'image/png', size: MAX_IMAGE_BYTES + 1, arrayBuffer() { assert.fail('must reject before reading'); } }), 'image_too_large');
  await rejects(readImageAttachment(file('image/jpeg', [])), 'invalid_image_type');
  for (const [type, bytes] of [['image/png', [137, 80, 78, 71, 13, 10, 26, 10]], ['image/jpeg', [255, 216, 255]],
    ['image/gif', [71, 73, 70, 56, 57, 97]], ['image/webp', [82, 73, 70, 70, 0, 0, 0, 0, 87, 69, 66, 80]]]) {
    const attachment = await readImageAttachment(file(type, bytes)); assert.equal(attachment.mediaType, type); assert.equal(attachment.base64Data, Buffer.from(bytes).toString('base64'));
  }
});

// A minimal DOM adapter, not a browser/product validation. No network or synthetic preview host is installed.
class Element {
  constructor(tag, document) {
    this.tagName = tag.toUpperCase(); this.document = document; this.children = []; this.dataset = {}; this.attributes = {}; this.listeners = new Map();
    this.value = ''; this.checked = false; this.disabled = false; this.hidden = false; this.open = false; this.className = ''; this._text = ''; this._html = '';
  }
  set textContent(value) { this.children = []; this._text = String(value); }
  get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
  set innerHTML(value) {
    this._html = value; this.children = []; this._text = '';
    const stack = [this]; let last = 0;
    for (const match of value.matchAll(/<(\/)?([a-z][\w-]*)([^>]*)>/gi)) {
      if (match.index > last) stack.at(-1).append(this.document.createTextNode(value.slice(last, match.index)));
      last = match.index + match[0].length;
      if (match[1]) { stack.pop(); continue; }
      const target = this.document.createElement(match[2]);
      for (const [, key, attribute = ''] of match[3].matchAll(/([\w-]+)(?:="([^"]*)")?/g)) target.setAttribute(key, attribute);
      stack.at(-1).append(target);
      if (!['br', 'hr', 'img', 'input'].includes(match[2].toLowerCase())) stack.push(target);
    }
    if (last < value.length) stack.at(-1).append(this.document.createTextNode(value.slice(last)));
  }
  get innerHTML() { return this._html; }
  append(...children) { for (const child of children) { child.parent = this; this.children.push(child); } }
  replaceChildren(...children) { this._text = ''; this.children = []; this.append(...children); }
  get firstElementChild() { return this.children[0]; }
  remove() { this.parent.children = this.parent.children.filter(child => child !== this); }
  setAttribute(key, value) {
    this.attributes[key] = String(value);
    if (key.startsWith('data-')) this.dataset[key.slice(5).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())] = value;
  }
  addEventListener(type, listener) { if (!this.listeners.has(type)) this.listeners.set(type, new Set()); this.listeners.get(type).add(listener); }
  removeEventListener(type, listener) { this.listeners.get(type)?.delete(listener); }
  async emit(type, extra = {}) { const event = { preventDefault() {}, ...extra }; for (const listener of this.listeners.get(type) || []) await listener(event); }
  querySelectorAll(selector) {
    const attribute = selector.startsWith('[') ? selector.slice(1, -1) : null;
    const tag = attribute ? null : selector.toUpperCase();
    const results = [];
    const visit = target => {
      const matches = attribute ? Object.hasOwn(target.attributes, attribute) || (attribute === 'data-byok-text' && target.dataset.byokText) : target.tagName === tag;
      if (matches) results.push(target);
      for (const child of target.children) visit(child);
    };
    for (const child of this.children) visit(child); return results;
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  replaceWith(...nodes) { const index = this.parent.children.indexOf(this); this.parent.children.splice(index, 1, ...nodes); for (const node of nodes) node.parent = this.parent; }
  select() {}
  focus() { this.document.activeElement = this; }
  requestSubmit() { void this.emit('submit'); }
}
function makeDocument() {
  const document = { ids: new Map(), createElement(tag) { return new Element(tag, this); }, createTextNode(value) { const node = this.createElement('#text'); node.textContent = value; return node; },
    getElementById(id) { return this.ids.get(id); }, execCommand: () => false };
  document.body = document.createElement('body');
  const tree = document.createElement('root'); const stack = [tree];
  for (const [, closing, tag, attrs] of html.matchAll(/<(\/)?([a-z][\w-]*)([^>]*)>/gi)) {
    if (closing) { stack.pop(); continue; }
    const target = document.createElement(tag);
    for (const [, key, value = ''] of attrs.matchAll(/([\w-]+)(?:="([^"]*)")?/g)) {
      target.setAttribute(key, value);
      if (key === 'id') document.ids.set(value, target);
      if (['disabled', 'hidden', 'open'].includes(key)) target[key] = true;
    }
    stack.at(-1).append(target);
    if (!['meta', 'link', 'input', 'br', 'hr'].includes(tag)) stack.push(target);
  }
  return document;
}
function uiFixture(t, { host = true, initiallyConnected = false, renderMarkdown } = {}) {
  const previous = globalThis.document; const document = makeDocument(); globalThis.document = document;
  const transport = host ? new FakeTransport() : undefined;
  let connected = initiallyConnected, culture = 'en';
  const ui = startByok({ transport, connected: () => connected, text: (key, values = {}) => {
    const value = (culture === 'en' ? en : pt)[key]; assert.equal(typeof value, 'string', key);
    return value.replace(/\{(\w+)\}/g, (match, name) => values[name] ?? match);
  }, renderMarkdown });
  const node = id => document.getElementById(`byok-${id}`);
  t.after(() => { ui.dispose(); if (previous === undefined) delete globalThis.document; else globalThis.document = previous; });
  return { ui, node, document, transport, connect() { connected = true; ui.setConnected(); },
    disconnect() { connected = false; ui.setConnected(); }, locale() { culture = 'pt-BR'; ui.refreshLocale(); } };
}
async function openWithModel(f, chosenModel = model, { configuration = config, remainingBudget = 5, otherModels = [], otherConfigurations = [] } = {}) {
  f.connect(); f.node('panel').open = true; await f.node('panel').emit('toggle');
  assert.equal(f.transport.messages.at(-1).type, 'byok.list'); f.transport.reply({ remainingBudget, configurations: [configuration, ...otherConfigurations] }); await tick();
  f.node('providers').value = configuration.id; await f.node('providers').emit('change');
  assert.equal(f.transport.messages.at(-1).type, 'byok.discover'); f.transport.reply(catalog(configuration, [chosenModel, ...otherModels])); await tick();
  f.node('models').value = chosenModel.id; const selection = f.node('models').emit('change');
  assert.equal(f.transport.messages.at(-1).type, 'byok.select'); f.transport.reply({ configuration: { ...configuration, selectedModelId: chosenModel.id } }); await selection;
}
const probeConfig = { ...config, kind: 'OpenAI', providerId: 'openai', baseUrl: DEFAULT_BASE_URLS.OpenAI };
const probeModel = { ...model, vision: false, tools: false, reasoning: false, origins: [], efforts: [] };
const probeResult = (supported, remainingBudget = 4, returnedModel = probeModel) => ({ model: returnedModel, supported, remainingBudget });
async function consentToProbe(f, capability = 'Vision') {
  f.node('probe-capability').value = capability; await f.node('probe-capability').emit('change');
  f.node('probe-consent').checked = true; await f.node('probe-consent').emit('change');
}

test('UI preview is closed, never posts, keeps configure/chat disabled and clears a submitted key', async t => {
  const f = uiFixture(t, { host: false, initiallyConnected: true });
  assert.equal(f.node('panel').open, false); assert.equal(f.node('config-fields').disabled, true); assert.equal(f.node('send').disabled, true);
  f.node('key').value = 'secret'; await f.node('configure').emit('submit'); assert.equal(f.node('key').value, '');
  f.node('panel').open = true; await f.node('panel').emit('toggle'); assert.match(f.node('status').textContent, /not connected/);
  assert.equal(f.node('effort').disabled, true); assert.equal(f.node('image').disabled, true);
});

test('UI listing is lazy/read-only; selection enables actual efforts and negative vision disables attachment', async t => {
  const f = uiFixture(t); assert.equal(f.transport.messages.length, 0);
  await openWithModel(f, { ...model, vision: false, reasoning: false, efforts: [] });
  assert.equal(f.transport.messages.some(message => message.type === 'byok.chat'), false);
  assert.equal(f.node('image').disabled, true); assert.equal(f.node('effort').disabled, true);
  assert.match(f.node('capabilities').textContent, /Vision · Not supported · Native metadata/);
  f.node('message').value = 'Hello'; await f.node('message').emit('input');
  const send = f.node('chat-form').emit('submit'); const sent = f.transport.messages.at(-1);
  assert.equal(sent.type, 'byok.chat'); assert.equal(sent.payload.effort, 'Off'); assert.equal(Object.hasOwn(sent.payload, 'image'), false);
  f.transport.reply({ completed: true }); await send;
});

test('UI configure clears keys immediately, uses Gemini host root and 429 still accepts credentials without paid chat', async t => {
  const f = uiFixture(t); f.connect();
  f.node('kind').value = 'Gemini'; await f.node('kind').emit('change'); assert.equal(f.node('url').value, DEFAULT_BASE_URLS.Gemini);
  const key = 'secret_do_not_show'; f.node('key').value = key;
  const submit = f.node('configure').emit('submit'); const sent = f.transport.messages.at(-1);
  assert.equal(sent.type, 'byok.configure'); assert.equal(sent.payload.key, key); assert.equal(f.node('key').value, ''); assert.equal(f.node('configure-button').disabled, true);
  f.transport.reply(catalog({ ...config, id: sent.payload.id, providerId: 'gemini', kind: 'Gemini', baseUrl: DEFAULT_BASE_URLS.Gemini }, [], { credentialValid: true, code: 'rate_limited' })); await submit;
  assert.match(f.node('status').textContent, /accepted the credential/); assert.match(f.node('error').textContent, /429/);
  assert.doesNotMatch(f.node('panel').textContent, new RegExp(key)); assert.equal(f.node('providers').disabled, false);
  assert.equal(f.transport.messages.length, 1); assert.equal(f.node('send').disabled, true);
  f.node('key').value = key; const errorSubmit = f.node('configure').emit('submit');
  f.transport.emit(envelope('error', { code: key }, f.transport.messages.at(-1).id)); await errorSubmit;
  assert.doesNotMatch(f.node('panel').textContent, new RegExp(key)); assert.equal(f.node('key').value, '');
});

for (const [credentialValid, code, label] of [[false, 'invalid_key', 'byokErrorInvalidKey'],
  [null, 'invalid_url', 'byokErrorUrl'], [null, 'rate_limited', 'byokErrorRate'],
  [null, 'network_error', 'byokErrorNetwork'], [null, 'unexpected_catalog_error', 'byokErrorUnknown']]) {
  test(`UI rejects an unsaved catalog with ${credentialValid} validity and ${code}`, async t => {
    const f = uiFixture(t); f.connect(); f.node('panel').open = true;
    await f.node('panel').emit('toggle'); f.transport.reply({ remainingBudget: 5, configurations: [] }); await tick();
    f.node('kind').value = 'OpenRouter'; await f.node('kind').emit('change');
    const key = 'synthetic_rejected_catalog_key'; f.node('key').value = key;
    const submit = f.node('configure').emit('submit'); const sent = f.transport.messages.at(-1);
    assert.equal(f.node('key').value, '');
    const configuration = { ...config, id: sent.payload.id, providerId: 'openrouter', kind: 'OpenRouter', baseUrl: DEFAULT_BASE_URLS.OpenRouter };
    f.transport.reply(catalog(configuration, [model], { credentialValid, code })); await submit;
    assert.equal(f.node('providers').children.some(option => option.value === configuration.id), false);
    assert.equal(f.node('providers').disabled, true); assert.equal(f.node('models').disabled, true);
    assert.equal(f.node('discover').disabled, true); assert.equal(f.node('send').disabled, true);
    assert.equal(f.node('error').hidden, false); assert.equal(f.node('error').textContent, en[label]);
    assert.equal(f.document.activeElement, f.node('error'));
    assert.notEqual(f.node('status').textContent, en.byokCatalogUnverified);
    assert.notEqual(f.node('status').textContent, en.byokCredentialValid);
    assert.equal(f.node('status').textContent, en.byokReady);
    assert.doesNotMatch(f.node('panel').textContent, new RegExp(key));
    f.locale(); assert.equal(f.node('error').textContent, pt[label]);
    assert.deepEqual(f.transport.messages.map(message => message.type), ['byok.list', 'byok.configure']);
  });
}

for (const key of ['', 'synthetic_public_catalog_key']) {
  test(`UI saves a public OpenRouter catalog with ${key ? 'a supplied' : 'no'} key without claiming verification`, async t => {
    const f = uiFixture(t); f.connect(); f.node('panel').open = true;
    await f.node('panel').emit('toggle'); f.transport.reply({ remainingBudget: 5, configurations: [] }); await tick();
    f.node('kind').value = 'OpenRouter'; await f.node('kind').emit('change');
    assert.equal(f.node('url').value, DEFAULT_BASE_URLS.OpenRouter); f.node('key').value = key;
    const submit = f.node('configure').emit('submit'); const sent = f.transport.messages.at(-1);
    assert.equal(sent.type, 'byok.configure'); assert.equal(sent.payload.key, key);
    assert.equal(f.node('key').value, ''); assert.equal(f.node('configure').attributes['aria-busy'], 'true');
    const configuration = { ...config, id: sent.payload.id, providerId: 'openrouter', kind: 'OpenRouter', baseUrl: DEFAULT_BASE_URLS.OpenRouter };
    f.transport.reply(catalog(configuration, [model], { credentialValid: null })); await submit;
    assert.equal(f.node('providers').value, configuration.id);
    assert.equal(f.node('providers').children.some(option => option.value === configuration.id), true);
    assert.equal(f.node('providers').disabled, false); assert.equal(f.node('models').disabled, false);
    assert.equal(f.node('discover').disabled, false); assert.equal(f.node('send').disabled, true);
    assert.equal(f.node('error').hidden, true); assert.equal(f.node('status').textContent, en.byokCatalogUnverified);
    assert.equal(f.node('configure').attributes['aria-busy'], 'false'); assert.equal(f.document.activeElement, f.node('models'));
    if (key) assert.doesNotMatch(f.node('panel').textContent, new RegExp(key));
    f.locale(); assert.equal(f.node('status').textContent, pt.byokCatalogUnverified);
    assert.equal(f.node('providers').value, configuration.id);
    f.node('models').value = model.id; const select = f.node('models').emit('change');
    assert.deepEqual(f.transport.messages.at(-1).payload, { configurationId: configuration.id, modelId: model.id });
    const selectedConfiguration = { ...configuration, selectedModelId: model.id };
    f.transport.reply({ configuration: selectedConfiguration }); await select;
    assert.equal(f.node('models').value, model.id); assert.equal(f.node('message').disabled, false);
    assert.equal(f.document.activeElement, f.node('message'));
    const discover = f.node('discover').emit('click');
    assert.deepEqual(f.transport.messages.at(-1).payload, { configurationId: configuration.id });
    f.transport.reply(catalog(selectedConfiguration, [model], { credentialValid: null })); await discover;
    assert.equal(f.node('models').value, model.id); assert.equal(f.node('status').textContent, pt.byokCatalogUnverified);
    assert.equal(f.node('error').hidden, true);
    assert.deepEqual(f.transport.messages.map(message => message.type), ['byok.list', 'byok.configure', 'byok.select', 'byok.discover']);
    f.node('key').value = 'clear_on_close'; f.node('panel').open = false; await f.node('panel').emit('toggle');
    assert.equal(f.node('key').value, '');
  });
}

test('UI unknown capability is visible, manual override requires explicit values, locale refresh preserves selections', async t => {
  const f = uiFixture(t); await openWithModel(f, { ...model, vision: false, tools: false, reasoning: false, origins: [], efforts: [] });
  assert.match(f.node('capabilities').textContent, /Vision · Unknown · Unknown/); assert.equal(f.node('apply-override').disabled, true);
  assert.match(f.node('models').children[1].textContent, /Unknown · Unknown · Unknown · Unverified/);
  for (const key of ['vision', 'tools', 'reasoning']) { f.node(key).value = key === 'vision' ? 'true' : 'false'; await f.node(key).emit('change'); }
  assert.equal(f.node('apply-override').disabled, false);
  const apply = f.node('apply-override').emit('click'); const sent = f.transport.messages.at(-1);
  assert.deepEqual(sent.payload, { configurationId, modelId: model.id, vision: true, tools: false, reasoning: false });
  const overridden = { ...model, reasoning: false, tools: false, efforts: [], origins: model.origins.map(origin => ({ ...origin, source: 'UserOverride' })) };
  f.transport.reply({ model: overridden }); await apply;
  assert.match(f.node('capabilities').textContent, /Manual override/); assert.equal(f.node('image').disabled, false);
  f.locale(); assert.match(f.node('capabilities').textContent, /Override manual/); assert.equal(f.node('models').value, model.id);
});

test('UI streams plain text/reasoning/tools without execution, safe Markdown is opt-in and cancellation stays concurrent', async t => {
  const rendered = [];
  const f = uiFixture(t, { renderMarkdown: text => { rendered.push(text); return '<p>safe output</p>'; } });
  await openWithModel(f); assert.deepEqual(f.node('effort').children.map(option => option.value), ['Low', 'High']); assert.equal(f.node('effort').disabled, false);
  f.node('message').value = 'Hello'; await f.node('message').emit('input'); f.node('effort').value = 'High';
  const send = f.node('chat-form').emit('submit'); const chat = f.transport.messages.at(-1);
  assert.equal(chat.payload.effort, 'High'); assert.equal(f.node('send').disabled, true); assert.equal(f.node('cancel').disabled, false);
  const events = [{ kind: 'TextDelta', text: '<script>not HTML</script>' }, { kind: 'ReasoningDelta', text: 'Visible reasoning' },
    { kind: 'ToolCallStart', id: 'call', name: 'run_command' }, { kind: 'ToolCallArgsDelta', id: 'call', chunk: '{"args":' },
    { kind: 'ToolCallEnd', id: 'call', arguments: { args: ['do not execute'] } }, { kind: 'Usage', input: 5, output: 7 }];
  for (const event of events) f.transport.emit(envelope('byok.event', event, chat.id));
  assert.match(f.node('output').textContent, /<script>not HTML<\/script>/); assert.match(f.node('output').textContent, /Visible reasoning/);
  assert.match(f.node('output').textContent, /not executed/); assert.equal(rendered.length, 0);
  const cancel = f.node('cancel').emit('click'); const cancelSent = f.transport.messages.at(-1);
  assert.equal(cancelSent.type, 'byok.cancel'); f.transport.reply({ cancelled: true }, cancelSent); await cancel;
  f.transport.emit(envelope('error', { code: 'cancelled' }, chat.id)); await send;
  assert.deepEqual(rendered, ['<script>not HTML</script>']); assert.match(f.node('status').textContent, /cancelled/);
  assert.equal(f.node('message').value, 'Hello'); assert.equal(f.transport.messages.filter(message => message.type === 'byok.chat').length, 1);
  f.ui.dispose(); assert.equal(f.transport.listeners.size, 0); assert.equal(f.node('send').disabled, true);
});

test('UI code blocks get an accessible copy button; clipboard failure is reported, never silent', async t => {
  const f = uiFixture(t, { renderMarkdown: () => '<pre><code>int x = 1;</code></pre>' });
  await openWithModel(f);
  const navigatorDescriptor = Object.getOwnPropertyDescriptor(globalThis, 'navigator');
  const writes = [];
  Object.defineProperty(globalThis, 'navigator', { value: { clipboard: { writeText: async value => { writes.push(value); } } }, configurable: true, writable: true });
  t.after(() => { if (navigatorDescriptor) Object.defineProperty(globalThis, 'navigator', navigatorDescriptor); else delete globalThis.navigator; });
  f.node('message').value = 'show code'; await f.node('message').emit('input');
  const send = f.node('chat-form').emit('submit'); const chat = f.transport.messages.at(-1);
  f.transport.emit(envelope('byok.event', { kind: 'TextDelta', text: '```csharp\nint x = 1;\n```' }, chat.id));
  f.transport.reply({ completed: true }); await send;
  const pre = f.node('output').querySelector('pre');
  assert.ok(pre); assert.equal(pre.attributes['aria-label'], en.byokCodeBlock); assert.equal(pre.tabIndex, 0);
  const copy = f.node('output').querySelectorAll('button').find(button => button.className === 'code-copy');
  assert.ok(copy); assert.equal(copy.textContent, en.byokCopy);
  await copy.emit('click');
  assert.deepEqual(writes, ['int x = 1;']); assert.equal(copy.textContent, en.byokCopied);
  globalThis.navigator = { clipboard: { writeText: async () => { throw new Error('clipboard denied'); } } };
  await copy.emit('click');
  assert.equal(writes.length, 1); assert.equal(copy.textContent, en.byokCopyFailed);
  assert.equal(f.document.body.children.length, 0);
  f.locale(); assert.equal(copy.textContent, pt.byokCopy);
});

test('UI chat log is bounded to 20 turns, empty state tracks real output and active model/count stay visible', async t => {
  const f = uiFixture(t); await openWithModel(f);
  assert.equal(f.node('empty').hidden, false);
  assert.equal(f.node('active-model').textContent, en.byokActiveModel.replace('{model}', model.name));
  assert.equal(f.node('count').textContent, en.byokCount.replace('{count}', '0').replace('{max}', '4096'));
  for (let index = 0; index < 21; index++) {
    f.node('message').value = `turn ${index}`; await f.node('message').emit('input');
    assert.equal(f.node('count').textContent, en.byokCount.replace('{count}', String(`turn ${index}`.length)).replace('{max}', '4096'));
    const send = f.node('chat-form').emit('submit'); const chat = f.transport.messages.at(-1);
    f.transport.emit(envelope('byok.event', { kind: 'TextDelta', text: `reply ${index}` }, chat.id));
    f.transport.reply({ completed: true }); await send;
  }
  assert.equal(f.node('output').children.length, 20);
  assert.equal(f.node('empty').hidden, true);
  assert.doesNotMatch(f.node('output').textContent, /turn 0\b/);
  assert.match(f.node('output').textContent, /turn 20/);
  f.node('providers').value = ''; await f.node('providers').emit('change');
  assert.equal(f.node('output').children.length, 0); assert.equal(f.node('empty').hidden, false);
  assert.equal(f.node('active-model').textContent, en.byokNoModel);
});

function assertRpcControlsLocked(f) {
  for (const id of ['config-fields', 'configure-button', 'list', 'providers', 'discover', 'models', 'vision', 'tools', 'reasoning',
    'apply-override', 'effort', 'message', 'image', 'remove-image', 'send', 'probe-consent', 'probe-capability', 'verify']) {
    assert.equal(f.node(id).disabled, true, id);
  }
}
async function assertNoNewRequestsWhileLocked(f) {
  const count = f.transport.messages.length;
  await f.node('list').emit('click');
  f.node('key').value = 'synthetic_key_while_cancelling'; await f.node('configure').emit('submit');
  assert.equal(f.node('key').value, '');
  await f.node('chat-form').emit('submit');
  await consentToProbe(f); await f.node('verify').emit('click'); await f.node('cancel').emit('click');
  assert.equal(f.transport.messages.length, count);
  assertRpcControlsLocked(f);
}

for (const type of ['configure', 'discover', 'probe', 'list', 'select', 'override']) {
  for (const replyOrder of ['ack-first', 'operation-first']) {
    test(`UI cancels ${type} (${replyOrder}) without unlocking controls or starting another paid request`, async t => {
      const f = uiFixture(t); await openWithModel(f, model, { configuration: probeConfig });
      f.node('message').value = 'Keep this draft'; await f.node('message').emit('input');
      await consentToProbe(f);
      let operation;
      if (type === 'configure') { f.node('key').value = 'synthetic_cancelled_key'; operation = f.node('configure').emit('submit'); }
      else if (type === 'select') operation = f.node('models').emit('change');
      else operation = f.node(type === 'probe' ? 'verify' : type === 'override' ? 'apply-override' : type).emit('click');
      const original = f.transport.messages.at(-1);
      assert.equal(original.type, `byok.${type}`); assert.equal(f.node('cancel').disabled, false);
      assertRpcControlsLocked(f);
      const loadingStatus = f.node('status').textContent;
      const cancellation = f.node('cancel').emit('click'); const cancelSent = f.transport.messages.at(-1);
      assert.equal(cancelSent.type, 'byok.cancel'); assert.deepEqual(cancelSent.payload, {});
      assert.equal(f.node('cancel').disabled, true); assert.equal(f.node('probe-consent').checked, false);
      assertRpcControlsLocked(f);
      if (replyOrder === 'ack-first') {
        f.transport.reply({ cancelled: true }, cancelSent); await cancellation;
        assert.equal(f.node('status').textContent, loadingStatus);
        assert.equal(f.node('cancel').disabled, true); assertRpcControlsLocked(f);
        await assertNoNewRequestsWhileLocked(f);
        f.transport.emit(envelope('error', { code: 'cancelled' }, original.id));
      } else {
        f.transport.emit(envelope('error', { code: 'cancelled' }, original.id)); await tick();
        assert.equal(f.node('cancel').disabled, true); assertRpcControlsLocked(f);
        await assertNoNewRequestsWhileLocked(f);
        f.transport.reply({ cancelled: true }, cancelSent); await cancellation;
      }
      if (type === 'probe') {
        await tick(); assert.equal(f.transport.messages.at(-1).type, 'byok.list');
        assert.match(f.node('probe-budget').textContent, /Unknown \/ 5/); assertRpcControlsLocked(f);
        f.transport.reply({ configurations: [probeConfig], remainingBudget: 4 });
      }
      await operation;
      assert.equal(f.node('config-fields').disabled, false); assert.equal(f.node('cancel').disabled, true);
      assert.equal(f.node('status').textContent, en.byokCancelled); assert.equal(f.node('error').hidden, true);
      assert.equal(f.document.activeElement?.attributes.id, 'byok-status'); assert.equal(f.node('message').value, 'Keep this draft');
      assert.equal(f.node('probe-consent').checked, false); assert.equal(f.node('verify').disabled, true);
      assert.match(f.node('probe-budget').textContent, new RegExp(`${type === 'probe' ? 4 : 5} / 5`));
      assert.equal(f.transport.messages.filter(message => message.type === 'byok.cancel').length, 1);
      assert.equal(f.transport.messages.filter(message => message.type === 'byok.probe').length, type === 'probe' ? 1 : 0);
      assert.equal(f.transport.messages.some(message => message.type === 'byok.chat'), false);
      assert.doesNotMatch(f.node('panel').textContent, /synthetic_cancelled_key|synthetic_key_while_cancelling/);
      f.locale(); assert.equal(f.node('status').textContent, pt.byokCancelled);
    });
  }
}

for (const replyOrder of ['ack-first', 'operation-first']) {
  test(`UI Cancel acknowledgement preserves a successful paid probe outcome (${replyOrder}) and the exact native budget`, async t => {
    const f = uiFixture(t); await openWithModel(f, probeModel, { configuration: probeConfig, remainingBudget: 3 });
    await consentToProbe(f); const verify = f.node('verify').emit('click'); const original = f.transport.messages.at(-1);
    const cancellation = f.node('cancel').emit('click'); const cancelSent = f.transport.messages.at(-1);
    const returned = { ...probeModel, vision: true, origins: [{ capability: 'Vision', source: 'Probe', confidence: 'High' }] };
    if (replyOrder === 'ack-first') {
      f.transport.reply({ cancelled: true }, cancelSent); await cancellation;
      assert.equal(f.node('status').textContent, en.byokProbing); assertRpcControlsLocked(f);
      f.transport.reply(probeResult(true, 0, returned), original);
    } else {
      f.transport.reply(probeResult(true, 0, returned), original); await verify;
      assert.equal(f.node('status').textContent, en.byokProbeSupported); assertRpcControlsLocked(f);
      await assertNoNewRequestsWhileLocked(f);
      f.transport.reply({ cancelled: true }, cancelSent); await cancellation;
    }
    await verify;
    assert.equal(f.node('status').textContent, en.byokProbeSupported); assert.equal(f.node('error').hidden, true);
    assert.match(f.node('capabilities').textContent, /Vision · Supported · Probe/); assert.match(f.node('probe-budget').textContent, /0 \/ 5/);
    assert.equal(f.node('probe-consent').checked, false); assert.equal(f.node('verify').disabled, true);
    assert.equal(f.node('configure-button').disabled, false); assert.equal(f.node('cancel').disabled, true);
    assert.deepEqual(f.transport.messages.map(message => message.type), ['byok.list', 'byok.discover', 'byok.select', 'byok.probe', 'byok.cancel']);
  });
}

test('UI failed cancellation leaves the original RPC locked, exposes a safe error and allows cancellation retry', async t => {
  const f = uiFixture(t); await openWithModel(f);
  const discover = f.node('discover').emit('click'); const original = f.transport.messages.at(-1);
  const cancellation = f.node('cancel').emit('click'); const cancelSent = f.transport.messages.at(-1);
  f.transport.emit(envelope('error', { code: 'timeout' }, cancelSent.id)); await cancellation;
  assert.equal(f.node('error').textContent, en.byokErrorTimeout); assert.equal(f.node('cancel').disabled, false); assertRpcControlsLocked(f);
  const retry = f.node('cancel').emit('click'); const retrySent = f.transport.messages.at(-1);
  f.transport.emit(envelope('error', { code: 'invalid_url' }, original.id)); await discover;
  assert.equal(f.node('error').textContent, en.byokErrorUrl); assertRpcControlsLocked(f);
  f.transport.reply({ cancelled: true }, retrySent); await retry;
  assert.equal(f.node('error').textContent, en.byokErrorUrl); assert.equal(f.node('status').textContent, en.byokReady);
  assert.equal(f.document.activeElement, f.node('error')); assert.equal(f.node('configure-button').disabled, false);
  assert.equal(f.node('cancel').disabled, true);
});

test('negative idle Cancel stays disabled and posts nothing, even if its DOM disabled flag is forged', async t => {
  const f = uiFixture(t); f.connect();
  assert.equal(f.node('cancel').disabled, true); await f.node('cancel').emit('click');
  f.node('cancel').disabled = false; await f.node('cancel').emit('click');
  assert.equal(f.transport.messages.length, 0);
  await openWithModel(f); assert.equal(f.node('cancel').disabled, true);
  const count = f.transport.messages.length;
  f.node('cancel').disabled = false; await f.node('cancel').emit('click');
  assert.equal(f.transport.messages.length, count);
});

test('negative image-only loading never enables or posts native Cancel and locks paid requests until reading finishes', async t => {
  const f = uiFixture(t); await openWithModel(f, model, { configuration: probeConfig });
  let finishImage;
  f.node('image').files = [{ type: 'image/png', size: 8, arrayBuffer: () => new Promise(resolve => { finishImage = resolve; }) }];
  const attachment = f.node('image').emit('change'); const count = f.transport.messages.length;
  assert.equal(f.node('chat-form').attributes['aria-busy'], 'true'); assert.equal(f.node('cancel').disabled, true);
  assertRpcControlsLocked(f);
  f.node('cancel').disabled = false; await f.node('cancel').emit('click'); await assertNoNewRequestsWhileLocked(f);
  assert.equal(f.transport.messages.length, count);
  finishImage(Uint8Array.from([137, 80, 78, 71, 13, 10, 26, 10]).buffer); await attachment;
  assert.equal(f.node('attachment').hidden, false); assert.equal(f.node('chat-form').attributes['aria-busy'], 'false');
  assert.equal(f.node('cancel').disabled, true); assert.equal(f.node('probe-consent').checked, false);
});

test('UI stream recovery Cancel clears the stale error without starting a second chat', async t => {
  const f = uiFixture(t); await openWithModel(f);
  f.node('message').value = 'Retry only on my action'; await f.node('message').emit('input');
  const send = f.node('chat-form').emit('submit'); const chat = f.transport.messages.at(-1);
  f.transport.emit(envelope('byok.event', { kind: 'Unknown' }, chat.id)); await send;
  assert.equal(f.node('error').textContent, en.byokErrorFormat); assert.equal(f.node('cancel').disabled, false); assertRpcControlsLocked(f);
  const cancellation = f.node('cancel').emit('click'); f.transport.reply({ cancelled: true }); await cancellation;
  assert.equal(f.node('error').hidden, true); assert.equal(f.node('status').textContent, en.byokCancelled);
  assert.equal(f.node('send').disabled, false); assert.equal(f.node('cancel').disabled, true);
  assert.equal(f.transport.messages.filter(message => message.type === 'byok.chat').length, 1);
});

test('UI attachment clears on model changes, bad images are rejected and Ctrl+Enter respects composition', async t => {
  const f = uiFixture(t); await openWithModel(f);
  f.node('image').files = [{ type: 'image/png', size: 8, arrayBuffer: async () => Uint8Array.from([137, 80, 78, 71, 13, 10, 26, 10]).buffer }];
  await f.node('image').emit('change'); assert.equal(f.node('attachment').hidden, false); assert.equal(f.node('image').value, '');
  f.node('models').value = ''; await f.node('models').emit('change'); assert.equal(f.node('attachment').hidden, true); assert.equal(f.node('image').disabled, true);
  let submitted = 0; f.node('chat-form').requestSubmit = () => submitted++;
  await f.node('message').emit('keydown', { key: 'Enter', ctrlKey: true, isComposing: true }); assert.equal(submitted, 0);
  await f.node('message').emit('keydown', { key: 'Enter', ctrlKey: true, isComposing: false }); assert.equal(submitted, 0);
});

test('UI never probes on render, list, discover, configure, selection, consent or locale refresh; click without consent is denied', async t => {
  const f = uiFixture(t, { initiallyConnected: true });
  assert.equal(f.node('probe-consent').checked, false);
  assert.equal(f.node('verify').disabled, true);
  assert.equal(f.transport.messages.length, 0);
  assert.match(f.node('probe-budget').textContent, /Unknown \/ 5/);
  await openWithModel(f, probeModel, { configuration: probeConfig, remainingBudget: 3 });
  assert.deepEqual(f.transport.messages.map(message => message.type), ['byok.list', 'byok.discover', 'byok.select']);
  assert.match(f.node('probe-budget').textContent, /3 \/ 5/);
  assert.equal(f.node('probe-consent').disabled, false);
  assert.equal(f.node('verify').disabled, true);
  f.node('verify').disabled = false; await f.node('verify').emit('click');
  assert.equal(f.transport.messages.length, 3);
  await consentToProbe(f); assert.equal(f.node('verify').disabled, false);
  f.locale(); assert.equal(f.node('probe-consent').checked, true);
  assert.match(f.node('probe-budget').textContent, /3 \/ 5/);
  const discover = f.node('discover').emit('click');
  assert.equal(f.node('probe-consent').checked, false);
  f.transport.reply(catalog({ ...probeConfig, selectedModelId: probeModel.id }, [probeModel])); await discover;
  await consentToProbe(f);
  f.node('key').value = 'synthetic_key';
  const configure = f.node('configure').emit('submit'); const sent = f.transport.messages.at(-1);
  assert.equal(sent.type, 'byok.configure'); assert.equal(f.node('key').value, '');
  f.transport.reply(catalog({ ...probeConfig, id: sent.payload.id, providerId: sent.payload.providerId, kind: sent.payload.kind, selectedModelId: probeModel.id }, [probeModel])); await configure;
  assert.equal(f.node('probe-consent').checked, false);
  assert.equal(f.transport.messages.some(message => ['byok.probe', 'byok.chat'].includes(message.type)), false);
  await consentToProbe(f); f.node('panel').open = false; await f.node('panel').emit('toggle');
  assert.equal(f.node('probe-consent').checked, false);
});

for (const kind of ['OpenAI', 'OpenRouter', 'LMStudio', 'Azure']) {
  test(`UI allows an explicit probe only for the selected ${kind} configuration and one capability`, async t => {
    const f = uiFixture(t);
    const selected = { ...probeConfig, id: otherId, kind, providerId: kind.toLowerCase(), baseUrl: DEFAULT_BASE_URLS[kind] || 'https://example.openai.azure.com/' };
    const unselected = { ...probeModel, id: 'unselected-model', name: 'Do not probe me' };
    await openWithModel(f, probeModel, { configuration: selected, otherConfigurations: [probeConfig], otherModels: [unselected] });
    await consentToProbe(f, 'Tools');
    f.node('providers').value = configurationId;
    await f.node('probe-consent').emit('change'); assert.equal(f.node('verify').disabled, true);
    f.node('verify').disabled = false; await f.node('verify').emit('click'); assert.equal(f.transport.messages.length, 3);
    f.node('providers').value = otherId; f.node('models').value = unselected.id;
    await f.node('probe-consent').emit('change'); assert.equal(f.node('verify').disabled, true);
    f.node('verify').disabled = false; await f.node('verify').emit('click'); assert.equal(f.transport.messages.length, 3);
    f.node('models').value = probeModel.id;
    // The editor's provider type is not the selected, saved configuration's type.
    f.node('kind').value = 'Anthropic'; await f.node('kind').emit('change');
    await f.node('probe-consent').emit('change'); assert.equal(f.node('verify').disabled, false);
    const verify = f.node('verify').emit('click'); const sent = f.transport.messages.at(-1);
    assert.equal(sent.type, 'byok.probe'); assert.deepEqual(sent.payload, { configurationId: otherId, capability: 'Tools', consent: true });
    assert.equal(f.node('verify').disabled, true); assert.equal(f.node('probe-consent').disabled, true);
    assert.equal(f.node('probe').attributes['aria-busy'], 'true'); assert.equal(f.node('status').textContent, en.byokProbing);
    await f.node('verify').emit('click'); assert.equal(f.transport.messages.length, 4);
    const returned = { ...probeModel, tools: true, origins: [{ capability: 'Tools', source: 'Probe', confidence: 'High' }] };
    f.transport.reply(probeResult(true, 4, returned)); await verify;
    assert.equal(f.node('probe').attributes['aria-busy'], 'false'); assert.equal(f.node('probe-consent').checked, false);
    assert.equal(f.node('verify').disabled, true); assert.equal(f.node('status').textContent, en.byokProbeSupported);
    assert.match(f.node('capabilities').textContent, /Tool calling · Supported · Probe · High confidence/);
    assert.match(f.node('probe-budget').textContent, /4 \/ 5/); assert.equal(f.document.activeElement, f.node('probe-budget'));
    assert.match(f.node('models').children.find(option => option.value === unselected.id).textContent, /Tool calling · Unknown/);
    assert.equal(f.transport.messages.filter(message => message.type === 'byok.probe').length, 1);
    f.locale(); assert.equal(f.node('status').textContent, pt.byokProbeSupported);
    assert.deepEqual(f.node('probe-capability').children.map(option => option.textContent), [pt.byokVision, pt.byokTools]);
    assert.equal(f.node('probe-capability').value, 'Tools'); assert.match(f.node('capabilities').textContent, /Chamada de ferramentas · Suportado · Probe/);
  });
}

for (const kind of ['Anthropic', 'Gemini', 'Ollama']) {
  test(`UI never probes unsupported native kind ${kind}, even with forged enabled controls and consent`, async t => {
    const f = uiFixture(t);
    await openWithModel(f, probeModel, { configuration: { ...probeConfig, kind, baseUrl: DEFAULT_BASE_URLS[kind] } });
    await consentToProbe(f); assert.equal(f.node('verify').disabled, true); assert.equal(f.node('probe-consent').disabled, true);
    f.node('kind').value = 'OpenAI'; await f.node('kind').emit('change');
    f.node('verify').disabled = false; await f.node('verify').emit('click');
    assert.equal(f.transport.messages.length, 3); assert.equal(f.transport.messages.some(message => message.type === 'byok.probe'), false);
  });
}

test('UI budget comes only from list/results: unknown and exhausted budgets disable probing, including after model changes', async t => {
  const f = uiFixture(t); f.connect(); f.node('panel').open = true;
  const configure = f.node('configure').emit('submit'); const sent = f.transport.messages.at(-1);
  const configured = { ...probeConfig, id: sent.payload.id, providerId: sent.payload.providerId, selectedModelId: probeModel.id };
  f.transport.reply(catalog(configured, [probeModel])); await configure;
  await consentToProbe(f); assert.equal(f.node('verify').disabled, true);
  f.node('verify').disabled = false; await f.node('verify').emit('click'); assert.equal(f.transport.messages.length, 1);
  const list = f.node('list').emit('click'); f.transport.reply({ configurations: [probeConfig], remainingBudget: 1 }); await list;
  assert.match(f.node('probe-budget').textContent, /1 \/ 5/);
  f.node('providers').value = configurationId; await f.node('providers').emit('change');
  f.transport.reply(catalog(probeConfig, [probeModel])); await tick();
  f.node('models').value = probeModel.id; const selection = f.node('models').emit('change');
  f.transport.reply({ configuration: { ...probeConfig, selectedModelId: probeModel.id } }); await selection;
  await consentToProbe(f); assert.equal(f.node('verify').disabled, false);
  const verify = f.node('verify').emit('click'); f.transport.reply(probeResult(null, 0)); await verify;
  assert.match(f.node('probe-budget').textContent, /0 \/ 5/); assert.equal(f.node('verify').disabled, true);
  assert.equal(f.node('probe-consent').disabled, true); assert.equal(f.node('probe-consent').checked, false);
  await consentToProbe(f); f.node('verify').disabled = false; const count = f.transport.messages.length;
  await f.node('verify').emit('click'); assert.equal(f.transport.messages.length, count);
  f.node('models').value = ''; await f.node('models').emit('change'); assert.equal(f.node('probe-consent').checked, false);
  const reload = f.node('list').emit('click'); f.transport.reply({ configurations: [probeConfig], remainingBudget: 0 }); await reload;
  assert.match(f.node('probe-budget').textContent, /0 \/ 5/); assert.equal(f.node('verify').disabled, true);
});

test('UI list budget zero remains exhausted after discovery/selection of an otherwise eligible model', async t => {
  const f = uiFixture(t); await openWithModel(f, probeModel, { configuration: probeConfig, remainingBudget: 0 });
  assert.equal(f.node('models').value, probeModel.id); assert.equal(f.node('message').disabled, false);
  assert.match(f.node('probe-budget').textContent, /0 \/ 5/);
  await consentToProbe(f); assert.equal(f.node('verify').disabled, true);
  f.node('verify').disabled = false; await f.node('verify').emit('click');
  assert.deepEqual(f.transport.messages.map(message => message.type), ['byok.list', 'byok.discover', 'byok.select']);
});

test('UI rejects reasoning/invalid capability before paid requests and clears consent when changing models/providers', async t => {
  const f = uiFixture(t); await openWithModel(f, probeModel, { configuration: probeConfig });
  assert.deepEqual(f.node('probe-capability').children.map(option => option.value), ['Vision', 'Tools']);
  for (const capability of ['Reasoning', 'vision', '', 'All']) {
    await consentToProbe(f, capability); assert.equal(f.node('verify').disabled, true);
    f.node('verify').disabled = false; await f.node('verify').emit('click');
  }
  assert.equal(f.transport.messages.length, 3);
  await consentToProbe(f); f.node('models').value = ''; await f.node('models').emit('change');
  assert.equal(f.node('probe-consent').checked, false); assert.equal(f.node('verify').disabled, true);
  f.node('probe-consent').checked = true; f.node('providers').value = ''; await f.node('providers').emit('change');
  assert.equal(f.node('probe-consent').checked, false); assert.equal(f.transport.messages.length, 3);
});

test('UI inconclusive probes update budget/status only, never flags, origins, attachments or manual values', async t => {
  const f = uiFixture(t); await openWithModel(f, model, { configuration: probeConfig, remainingBudget: 2 });
  const before = f.node('capabilities').textContent, label = f.node('models').children[1].textContent;
  f.node('vision').value = 'false'; await f.node('vision').emit('change');
  f.node('image').files = [{ type: 'image/png', size: 8, arrayBuffer: async () => Uint8Array.from([137, 80, 78, 71, 13, 10, 26, 10]).buffer }];
  await f.node('image').emit('change'); await consentToProbe(f);
  const verify = f.node('verify').emit('click');
  f.transport.reply(probeResult(null, 2, { ...probeModel, origins: [{ capability: 'Vision', source: 'Probe', confidence: 'High' }] })); await verify;
  assert.equal(f.node('capabilities').textContent, before); assert.equal(f.node('models').children[1].textContent, label);
  assert.equal(f.node('vision').value, 'false'); assert.equal(f.node('image').disabled, false); assert.equal(f.node('attachment').hidden, false);
  assert.equal(f.node('status').textContent, en.byokProbeInconclusive); assert.match(f.node('probe-budget').textContent, /2 \/ 5/);
  assert.equal(f.node('probe-consent').checked, false); f.locale(); assert.equal(f.node('status').textContent, pt.byokProbeInconclusive);
  assert.match(f.node('probe-budget').textContent, /Orçamento restante da sessão: 2 \/ 5/);
});

test('UI proven negative renders Probe-origin badges and disables vision, while stronger returned evidence is never replaced', async t => {
  const f = uiFixture(t); await openWithModel(f, model, { configuration: probeConfig });
  await consentToProbe(f); const verify = f.node('verify').emit('click');
  const negative = { ...model, vision: false, origins: model.origins.map(origin => origin.capability === 'Vision' ? { ...origin, source: 'Probe' } : origin) };
  f.transport.reply(probeResult(false, 4, negative)); await verify;
  assert.match(f.node('capabilities').textContent, /Vision · Not supported · Probe · High confidence/);
  assert.match(f.node('models').children[1].textContent, /Vision · Not supported · Probe/);
  assert.equal(f.node('image').disabled, true); assert.equal(f.node('vision').value, 'false'); assert.equal(f.node('reasoning').value, 'true');
  assert.equal(f.node('status').textContent, en.byokProbeUnsupported);
  await consentToProbe(f, 'Tools'); const stronger = f.node('verify').emit('click');
  f.transport.reply(probeResult(false, 3, negative)); await stronger;
  assert.match(f.node('capabilities').textContent, /Tool calling · Supported · Native metadata/);
  assert.equal(f.node('tools').value, 'true'); assert.equal(f.node('probe-consent').checked, false);
});

for (const [code, label] of [['invalid_key', 'byokErrorInvalidKey'], ['insufficient_credit', 'byokErrorCredit'], ['timeout', 'byokErrorTimeout'],
  ['cancelled', null], ['synthetic_secret_key', 'byokErrorUnknown']]) {
  test(`UI probe ${code} preserves flags, reconciles native budget and retains error/cancellation semantics`, async t => {
    const f = uiFixture(t); await openWithModel(f, probeModel, { configuration: probeConfig, remainingBudget: 3 });
    const before = f.node('capabilities').textContent;
    await consentToProbe(f); const verify = f.node('verify').emit('click');
    f.transport.emit(envelope('error', { code }, f.transport.messages.at(-1).id)); await tick();
    assert.equal(f.transport.messages.at(-1).type, 'byok.list');
    assert.match(f.node('probe-budget').textContent, /Unknown \/ 5/); assert.equal(f.node('configure-button').disabled, true);
    const remainingBudget = ['cancelled', 'synthetic_secret_key', 'timeout'].includes(code) ? 2 : 3;
    f.transport.reply({ configurations: [probeConfig], remainingBudget }); await verify;
    assert.equal(f.node('probe-consent').checked, false); assert.equal(f.node('verify').disabled, true);
    assert.equal(f.node('capabilities').textContent, before); assert.match(f.node('probe-budget').textContent, new RegExp(`${remainingBudget} / 5`));
    assert.equal(f.node('models').value, probeModel.id); assert.equal(f.node('message').disabled, false);
    if (label) {
      assert.equal(f.node('error').textContent, en[label]); assert.equal(f.document.activeElement, f.node('error'));
      f.locale(); assert.equal(f.node('error').textContent, pt[label]);
    } else { assert.equal(f.node('error').hidden, true); assert.equal(f.node('status').textContent, en.byokCancelled); }
    assert.doesNotMatch(f.node('panel').textContent, /synthetic_secret_key/);
    assert.equal(f.transport.messages.filter(message => message.type === 'byok.probe').length, 1);
  });
}

test('UI unknown probe outcome consumes only the native-reported budget, without capability changes or automatic retry', async t => {
  const f = uiFixture(t); await openWithModel(f, probeModel, { configuration: probeConfig, remainingBudget: 3 });
  const before = f.node('capabilities').textContent;
  await consentToProbe(f); const verify = f.node('verify').emit('click');
  f.transport.reply(probeResult(null, 2)); await verify;
  assert.equal(f.node('capabilities').textContent, before); assert.equal(f.node('status').textContent, en.byokProbeInconclusive);
  assert.match(f.node('probe-budget').textContent, /2 \/ 5/); assert.equal(f.node('probe-consent').checked, false);
  assert.equal(f.transport.messages.filter(message => message.type === 'byok.probe').length, 1);
  await f.node('verify').emit('click'); assert.equal(f.transport.messages.length, 4);
});

for (const failure of ['network-error', 'malformed-budget']) {
  test(`UI cancelled probe budget reconciliation ${failure} leaves Unknown and blocks payment until native list succeeds`, async t => {
    const f = uiFixture(t); await openWithModel(f, probeModel, { configuration: probeConfig, remainingBudget: 1 });
    await consentToProbe(f); const verify = f.node('verify').emit('click'); const original = f.transport.messages.at(-1);
    const cancellation = f.node('cancel').emit('click'); f.transport.reply({ cancelled: true }); await cancellation;
    f.transport.emit(envelope('error', { code: 'cancelled' }, original.id)); await tick();
    assert.equal(f.transport.messages.at(-1).type, 'byok.list');
    if (failure === 'network-error') f.transport.emit(envelope('error', { code: 'network_error' }, f.transport.messages.at(-1).id));
    else f.transport.reply({ configurations: [probeConfig], remainingBudget: 6 });
    await verify;
    assert.match(f.node('probe-budget').textContent, /Unknown \/ 5/); assert.equal(f.node('status').textContent, en.byokCancelled);
    assert.equal(f.node('error').hidden, true); assert.equal(f.node('probe-consent').checked, false);
    await consentToProbe(f); assert.equal(f.node('verify').disabled, true);
    f.node('verify').disabled = false; const count = f.transport.messages.length;
    await f.node('verify').emit('click'); assert.equal(f.transport.messages.length, count);
    const list = f.node('list').emit('click');
    f.transport.reply({ configurations: [probeConfig], remainingBudget: 0 }); await list;
    assert.match(f.node('probe-budget').textContent, /0 \/ 5/); assert.equal(f.node('verify').disabled, true);
    assert.equal(f.transport.messages.filter(message => message.type === 'byok.probe').length, 1);
  });
}

for (const scenario of ['wrong-model', 'malformed-result', 'changed-selection', 'disconnect', 'dispose']) {
  test(`UI rejects ${scenario} probe completion without applying returned flags/budget`, async t => {
    const f = uiFixture(t); await openWithModel(f, probeModel, { configuration: probeConfig, remainingBudget: 3 });
    const before = f.node('capabilities').textContent;
    await consentToProbe(f); const verify = f.node('verify').emit('click'); const sent = f.transport.messages.at(-1);
    if (scenario === 'changed-selection') { f.node('models').value = ''; await f.node('models').emit('change'); }
    if (scenario === 'disconnect') f.disconnect();
    if (scenario === 'dispose') f.ui.dispose();
    const returned = { ...probeModel, id: scenario === 'wrong-model' ? 'different-model' : probeModel.id, vision: true,
      origins: [{ capability: 'Vision', source: 'Probe', confidence: 'High' }] };
    f.transport.reply(probeResult(true, scenario === 'malformed-result' ? 6 : 2, returned), sent);
    if (['wrong-model', 'malformed-result', 'changed-selection'].includes(scenario)) {
      await tick(); assert.equal(f.transport.messages.at(-1).type, 'byok.list');
      assert.match(f.node('probe-budget').textContent, /Unknown \/ 5/);
      f.transport.reply({ configurations: [probeConfig], remainingBudget: 2 });
    }
    await verify;
    assert.equal(f.node('probe-consent').checked, false); assert.equal(f.node('verify').disabled, true);
    if (['wrong-model', 'malformed-result', 'changed-selection'].includes(scenario)) {
      assert.equal(f.node('error').textContent, en.byokErrorFormat); assert.match(f.node('probe-budget').textContent, /2 \/ 5/);
    }
    if (['wrong-model', 'malformed-result', 'dispose'].includes(scenario)) assert.equal(f.node('capabilities').textContent, before);
    if (scenario === 'disconnect') {
      assert.match(f.node('probe-budget').textContent, /Unknown \/ 5/);
      await consentToProbe(f); f.node('verify').disabled = false; await f.node('verify').emit('click');
    }
    assert.equal(f.transport.messages.filter(message => message.type === 'byok.probe').length, 1);
  });
}

test('static probe section follows manual overrides with native labels, unchecked opt-in, status/focus and token-based wrapping', () => {
  const probeStart = html.indexOf('<section id="byok-probe"');
  const probe = html.slice(probeStart, html.indexOf('</section>', probeStart) + '</section>'.length);
  assert.ok(html.indexOf('id="byok-override"') < html.indexOf('id="byok-probe"'));
  assert.equal([...probe.matchAll(/<button\b/g)].length, 1);
  assert.match(probe, /id="byok-probe-consent" type="checkbox"[^>]*disabled/); assert.doesNotMatch(probe, /\bchecked\b/);
  for (const id of ['probe-consent', 'probe-capability']) {
    assert.match(probe, new RegExp(`<label for="byok-${id}" data-i18n="[^"]+"`));
    assert.match(probe, new RegExp(`id="byok-${id}"[^>]*aria-describedby="[^"]+"`));
  }
  assert.match(probe, /id="byok-probe-budget"[^>]*role="status"[^>]*aria-live="polite"[^>]*aria-atomic="true"[^>]*tabindex="-1"/);
  assert.equal(en.byokProbeConsent, 'Verify capabilities (uses paid tokens)');
  assert.equal(pt.byokProbeConsent, 'Verificar capacidades (consome tokens pagos)');
  assert.match(css, /\.byok-probe \{[^}]*display: grid;[^}]*min-width: 0;[^}]*var\(--border\)/);
  assert.match(css, /\.byok-probe-consent \{[^}]*display: flex;[^}]*gap: 8px;[^}]*min-width: 0/);
  assert.match(css, /\.byok-panel \.byok-probe-consent input \{[^}]*flex: none;[^}]*width: 16px;[^}]*accent-color: var\(--vs-accent\)/);
  assert.match(css, /\.byok-probe-consent label \{[^}]*overflow-wrap: anywhere/);
  assert.doesNotMatch(probe + Object.keys(en).filter(key => key.startsWith('byokProbe')).map(key => en[key] + pt[key]).join(''), /\p{Extended_Pictographic}/u);
});

test('static UI retains unified chat ids/CSP, labels match locales and panel wraps responsively using existing tokens only', async () => {
  assert.match(html, /<details id="byok-panel" class="byok-panel">/); assert.doesNotMatch(html, /<details id="byok-panel"[^>]*\bopen\b/);
  for (const id of ['byok-output', 'byok-empty', 'byok-chat-form', 'byok-message', 'byok-send', 'byok-cancel', 'byok-active-model', 'byok-count', 'byok-effort', 'byok-image']) assert.equal([...html.matchAll(new RegExp(`id="${id}"`, 'g'))].length, 1);
  assert.doesNotMatch(html, /id="(composer|message-input|send|history-viewport|history-spacer|history-items|markdown-sample|diff-sample)"/);
  assert.match(html, /<main[^>]*aria-labelledby="byok-chat-title"/);
  assert.match(html, /img-src 'none'/); assert.match(html, /autocomplete="off"/);
  assert.match(html, /id="byok-status"[^>]*role="status"[^>]*aria-live="polite"[^>]*aria-atomic="true"/);
  assert.match(html, /id="byok-error"[^>]*role="alert"[^>]*hidden/);
  assert.match(css, /:focus-visible \{[^}]*outline: 2px solid var\(--focus-color\)/);
  assert.match(html, /id="byok-key" type="password"[^>]*autocomplete="off"/);
  assert.equal(html.replace(/<[^>]*>/g, '').trim(), ''); assert.deepEqual(Object.keys(en).sort(), Object.keys(pt).sort());
  for (const [, key] of html.matchAll(/data-i18n(?:-label|-placeholder)?="([^"]+)"/g)) assert.equal(typeof en[key], 'string', key);
  for (const key of Object.keys(en)) assert.deepEqual(en[key].match(/\{\w+\}/g), pt[key].match(/\{\w+\}/g), key);
  for (const name of ['byok.mjs', 'byok-bridge.mjs']) {
    const source = await readFile(new URL(name, root), 'utf8');
    assert.doesNotMatch(source, /fetch\(|XMLHttpRequest|WebSocket|localStorage|sessionStorage|console\.|createObjectURL/);
    for (const [, key] of source.matchAll(/\btext\('([^']+)'/g)) assert.equal(typeof en[key], 'string', key);
  }
  assert.match(css, /\.byok-scroll \{[^}]*max-height:[^}]*overflow: auto;/);
  assert.match(css, /\.byok-scroll \{[^}]*overflow-wrap: anywhere/);
  assert.match(css, /prefers-reduced-motion: reduce/); assert.match(css, /\.byok-panel select \{[^}]*text-overflow/);
  assert.doesNotMatch(css, /@import|@font-face|url\(/);
  assert.equal(DEFAULT_BASE_URLS.Anthropic, 'https://api.anthropic.com/v1/'); assert.equal(DEFAULT_BASE_URLS.Azure, '');
  assert.equal(typeof randomUUID(), 'string');
});
