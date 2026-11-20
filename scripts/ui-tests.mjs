import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { request } from 'node:http';
import { readFile, mkdtemp, rm, symlink, readdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { HostBridge, MAX_TEXT_LENGTH, isEnvelope, isTheme } from '../src/Lumen.Vsix/wwwroot/bridge.mjs';
import { createMarkdownRenderer, isSafeLink, renderDiff } from '../src/Lumen.Vsix/wwwroot/renderers.mjs';
import { VirtualHistory, visibleRange, ROW_HEIGHT, OVERSCAN } from '../src/Lumen.Vsix/wwwroot/virtual-history.mjs';
import { contrast, themeProperties, presetColors } from '../src/Lumen.Vsix/wwwroot/theme.mjs';
import { startThemeEditor, parseThemeFile, MAX_THEME_FILE_BYTES } from '../src/Lumen.Vsix/wwwroot/theme-editor.mjs';
import { bundledThemes } from '../src/Lumen.Vsix/wwwroot/theme-presets.mjs';
import { isThemePreset } from '../src/Lumen.Vsix/wwwroot/byok-bridge.mjs';
import { createUiServer, resolveStaticPath, webRoot, csp } from './serve-ui.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const loadJson = async filename => JSON.parse(await readFile(filename, 'utf8'));
const pt = await loadJson(path.join(webRoot, 'locales/pt-BR.json'));
const en = await loadJson(path.join(webRoot, 'locales/en.json'));
const sampleMarkdown = '### Uma resposta, bem legível\nTexto com **ênfase**, listas e código, renderizado com assets locais.\n\n- Markdown sem HTML executável.\n- [Ir para as mensagens](#section), sem sair da tool window.\n\n```csharp\npublic static string Greet(string name)\n{\n    return $"Olá, {name}!";\n}\n```';
const sampleDiff = 'diff --git a/Greeting.cs b/Greeting.cs\nindex 1111111..2222222 100644\n--- a/Greeting.cs\n+++ b/Greeting.cs\n@@ -1,4 +1,4 @@\n public static class Greeting\n {\n-    public static string Text => "Olá";\n+    public static string Text => "Olá, Lumen";\n }\n';
const diffEmpty = 'Arquivo sem alterações';
const theme = { culture: 'pt-BR', background: '#202124', foreground: '#eeeeee', accent: '#75b9ff' };
const id = '12345678-1234-4234-8234-123456789abc';
const envelope = (type, payload, correlation = id) => ({ version: 1, id: correlation, type, payload });

class FakeTransport {
  messages = [];
  listeners = new Set();
  addEventListener(type, listener) { assert.equal(type, 'message'); this.listeners.add(listener); }
  removeEventListener(type, listener) { this.listeners.delete(listener); }
  postMessage(message) { this.messages.push(message); }
  emit(message) { for (const listener of this.listeners) listener({ data: message }); }
}

function fixture(options = {}) {
  const transport = new FakeTransport();
  const errors = [];
  const themes = [];
  const bridge = new HostBridge(transport, { onError: code => errors.push(code), onTheme: value => themes.push(value), ...options });
  return { bridge, transport, errors, themes };
}

async function connectFake(fixture) {
  const ready = fixture.bridge.request('ready', {});
  const sent = fixture.transport.messages.at(-1);
  fixture.transport.emit(envelope('ready.result', theme, sent.id));
  await ready;
}

const rejectsCode = (promise, code) => assert.rejects(promise, error => error.code === code);

test('theme editor previews, rejects invalid inputs, saves, discards and resets without a provider', async () => {
  class Control {
    value = ''; textContent = ''; disabled = false; open = false; validity = { valid: true }; listeners = {};
    addEventListener(type, fn) { this.listeners[type] = fn; }
    emit(type) { return this.listeners[type]?.({ preventDefault() {} }); }
  }
  const ids = ['form', 'panel', 'background', 'foreground', 'accent', 'chatFont', 'codeFont', 'fontSize', 'density', 'borderRadius',
    'ask', 'plan', 'agent', 'warning', 'status', 'apply', 'cancel', 'reset', 'preset', 'import', 'export'];
  const nodes = Object.fromEntries(ids.map(id => [id, new Control()]));
  const document = { getElementById: id => nodes[id.slice(6)] };
  let saved = { background: '#000000', foreground: '#ffffff' }, displayed;
  const requests = [];
  const bridge = { isConnected: () => true, async request(type, payload) {
    requests.push(type);
    if (type === 'byok.theme.get') return { theme: saved };
    if (type === 'byok.theme.set') { saved = payload.theme; return { theme: saved }; }
    if (type === 'byok.theme.reset') { saved = null; return { reset: true }; }
    throw new Error(type);
  } };
  const editor = startThemeEditor({ document, bridge, text: key => key,
    hostTheme: () => ({ background: '#ffffff', foreground: '#000000', accent: '#000000' }), display: value => { displayed = value; } });
  await editor.load();
  nodes.background.value = '#abcdef'; nodes.form.emit('input');
  assert.equal(displayed.background, '#abcdef');
  assert.deepEqual(requests, ['byok.theme.get']);
  nodes.fontSize.value = '99'; nodes.form.emit('input');
  assert.equal(nodes.apply.disabled, true);
  assert.equal(nodes.warning.textContent, 'themeInvalid');
  assert.equal(displayed.background, '#000000');
  await nodes.form.emit('submit');
  assert.deepEqual(requests, ['byok.theme.get']);
  nodes.fontSize.value = '24'; nodes.foreground.value = '#abcdef'; nodes.form.emit('input');
  assert.equal(nodes.warning.textContent, 'themeLowContrast');
  assert.equal(nodes.apply.disabled, false);
  await nodes.form.emit('submit');
  assert.equal(saved.fontSize, 24);
  nodes.background.value = '#123456'; nodes.form.emit('input');
  nodes.cancel.emit('click');
  assert.equal(displayed.background, '#abcdef');
  nodes.background.value = '#123456'; nodes.form.emit('input');
  nodes.panel.emit('toggle');
  assert.equal(displayed.background, '#abcdef');
  await nodes.reset.emit('click');
  assert.equal(saved, null); assert.equal(displayed, null);
  assert.deepEqual(requests, ['byok.theme.get', 'byok.theme.set', 'byok.theme.reset']);
});

test('bundled themes meet WCAG AA and malformed theme files are rejected', () => {
  for (const theme of Object.values(bundledThemes)) {
    assert.equal(isThemePreset(theme), true);
    assert.ok(contrast(theme.foreground, theme.background) >= 4.5);
    assert.ok(contrast(theme.accent, theme.background) >= 3);
    for (const color of Object.values(theme.modeAccent)) assert.ok(contrast(color, theme.background) >= 3);
    const tokens = themeProperties(theme);
    for (const name of ['--link-color', '--syntax-string']) assert.ok(contrast(tokens[name], theme.background) >= 4.5);
    assert.ok(contrast(tokens['--focus-color'], theme.background) >= 3);
    assert.ok(contrast(tokens['--button-foreground'], theme.accent) >= 4.5);
    assert.deepEqual(parseThemeFile(JSON.stringify(theme)), theme);
  }
  for (const invalid of ['{', 'null', '[]', '{"background":"#12345"}', '{"fontSize":99}', '{"extra":true}'])
    assert.throws(() => parseThemeFile(invalid), invalid);
  assert.equal(parseThemeFile('{"chatFont":"Fonte 中文"}').chatFont, 'Fonte 中文');
  assert.throws(() => parseThemeFile('{"name":"x\\u007fy"}'));
  assert.ok(contrast('#bbbbbb', '#ffffff') < 4.5);
});

test('theme import previews without saving and export round trips; invalid files keep the draft', async () => {
  class Control {
    value = ''; textContent = ''; disabled = false; open = true; validity = { valid: true }; listeners = {}; files = [];
    addEventListener(type, fn) { this.listeners[type] = fn; }
    emit(type) { return this.listeners[type]?.({ preventDefault() {} }); }
  }
  const ids = ['form', 'panel', 'background', 'foreground', 'accent', 'chatFont', 'codeFont', 'fontSize', 'density', 'borderRadius',
    'ask', 'plan', 'agent', 'warning', 'status', 'apply', 'cancel', 'reset', 'preset', 'import', 'export'];
  const nodes = Object.fromEntries(ids.map(id => [id, new Control()]));
  let link;
  const document = {
    getElementById: id => nodes[id.slice(6)],
    createElement: () => (link = { click() {} })
  };
  const calls = [], bridge = { isConnected: () => true, async request(type) { calls.push(type); return { theme: null }; } };
  let displayed;
  const editor = startThemeEditor({ document, bridge, text: key => key,
    hostTheme: () => ({ background: '#ffffff', foreground: '#000000', accent: '#000000' }), display: theme => { displayed = theme; } });
  await editor.load();
  nodes.preset.value = 'dark'; nodes.preset.emit('change');
  assert.equal(displayed.name, 'Lumen Dark');
  assert.deepEqual(calls, ['byok.theme.get']);
  const incoming = { name: 'Imported', background: '#abc', fontSize: 20 };
  nodes.import.files = [{ size: 100, text: async () => JSON.stringify(incoming) }];
  await nodes.import.emit('change');
  assert.deepEqual(displayed, incoming);
  nodes.export.emit('click');
  assert.equal(link.download, 'lumen-theme.json');
  assert.deepEqual(await (await fetch(link.href)).json(), incoming);
  for (const file of [{ size: MAX_THEME_FILE_BYTES + 1, text: async () => '{}' }, { size: 3, text: async () => '{' }]) {
    nodes.import.files = [file]; await nodes.import.emit('change');
    assert.equal(nodes.status.textContent, 'themeImportFailed');
    assert.deepEqual(displayed, incoming);
  }
  let finishRead;
  nodes.import.files = [{ size: 10, text: () => new Promise(resolve => { finishRead = resolve; }) }];
  const reading = nodes.import.emit('change');
  nodes.panel.open = false; nodes.panel.emit('toggle');
  finishRead(JSON.stringify(incoming)); await reading;
  assert.equal(displayed, null, 'Closing the panel must discard an in-flight import');
  assert.deepEqual(calls, ['byok.theme.get']);
});

test('partial themes inherit colors and low contrast text is made readable', () => {
  const host = { background: '#ffffff', foreground: '#000000', accent: '#0055aa' };
  assert.equal(presetColors({ background: '#123456' }, host).background, '#123456');
  const adjusted = presetColors({ background: '#999999', foreground: '#999999' }, host);
  assert.ok(contrast(adjusted.foreground, adjusted.background) >= 4.5);
  assert.equal(adjusted.accent, host.accent);
});

test('responses reject malformed, oversized and unknown envelopes before accepting valid schemas', () => {
  for (const bad of [null, [], '{"version":', {}, envelope('ready.result', {}),
    envelope('ready.result', { ...theme, culture: 'fr' }), envelope('ready.result', { ...theme, background: 'red;display:none' }),
    envelope('ready.result', { ...theme, extra: true }), { ...envelope('ready.result', theme), version: 2 },
    envelope('ready.result', theme, null), envelope('ready.result', theme, 'not-a-uuid'),
    envelope('other', {}), envelope('ping.result', { text: 'x'.repeat(MAX_TEXT_LENGTH + 1) }),
    envelope('ping.result', { text: 1 }), envelope('error', { code: '<script>' }, null),
    envelope('error', { code: 'invalid_message', detail: 'untrusted' }, null)]) {
    assert.equal(isEnvelope(bad), false, JSON.stringify(bad)?.slice(0, 80));
  }
  assert.equal(isEnvelope(envelope('ready.result', theme)), true);
  assert.equal(isEnvelope(envelope('theme.changed', { background: '#fff', foreground: '#000', accent: '#123456' })), true);
  assert.equal(isEnvelope(envelope('ping.result', { text: '' })), true);
  assert.equal(isEnvelope(envelope('error', { code: 'invalid_message' }, null)), true);
  assert.equal(isTheme({ ...theme, accent: 'url(https://example.org)' }), false);
});

test('preview never fabricates a successful bridge and ping requires a real ready.result', async () => {
  const unavailable = new HostBridge(undefined);
  assert.equal(unavailable.available, false);
  assert.equal(unavailable.connected, false);
  await rejectsCode(unavailable.request('ready'), 'bridge_unavailable');
  const f = fixture();
  await rejectsCode(f.bridge.request('ping', { text: 'hello' }), 'bridge_unavailable');
  assert.equal(f.transport.messages.length, 0);
  f.bridge.dispose();
});

test('bridge uses exact object requests and rejects unknown ids and duplicate replies', async () => {
  const f = fixture();
  const ready = f.bridge.request('ready');
  const sent = f.transport.messages[0];
  assert.deepEqual(Object.keys(sent).sort(), ['id', 'payload', 'type', 'version']);
  assert.match(sent.id, /^[0-9a-f-]{36}$/);
  assert.equal(sent.type, 'ready');
  assert.equal(sent.version, 1);
  assert.deepEqual(sent.payload, {});
  assert.equal(f.bridge.connected, false);
  f.transport.emit(envelope('ready.result', theme));
  assert.deepEqual(f.errors, ['invalid_response']);
  assert.equal(f.bridge.connected, false);
  f.transport.emit(envelope('ready.result', theme, sent.id));
  assert.deepEqual(await ready, theme);
  assert.equal(f.bridge.connected, true);
  const pending = f.bridge.request('ping', { text: 'Olá <script> & hello' });
  const ping = f.transport.messages.at(-1);
  assert.notEqual(ping.id, sent.id);
  assert.equal(ping.type, 'ping');
  f.transport.emit(envelope('ping.result', ping.payload, ping.id));
  assert.deepEqual(await pending, ping.payload);
  f.transport.emit(envelope('ping.result', ping.payload, ping.id));
  assert.equal(f.errors.at(-1), 'invalid_response');
  f.bridge.dispose();
});

test('correlated invalid payloads and response types reject pending requests', async () => {
  for (const response of [envelope('ready.result', {}), envelope('ping.result', { text: 'unexpected' }), '{"version":']) {
    const f = fixture({ timeoutMs: 15 });
    const pending = f.bridge.request('ready');
    const rejected = rejectsCode(pending, typeof response === 'string' ? 'timeout' : 'invalid_response');
    f.transport.emit(typeof response === 'string' ? response : { ...response, id: f.transport.messages[0].id });
    await rejected;
    assert.equal(f.bridge.connected, false);
    assert.equal(f.bridge.pending.size, 0);
    f.bridge.dispose();
  }
});

test('echo mismatch and invalid outgoing requests are rejected', async () => {
  const f = fixture();
  await connectFake(f);
  for (const [type, payload] of [['unknown', {}], ['ready', { extra: true }], ['ping', { text: ' ' }], ['ping', { text: 'x'.repeat(MAX_TEXT_LENGTH + 1) }]]) {
    await rejectsCode(f.bridge.request(type, payload), 'invalid_message');
  }
  const pending = f.bridge.request('ping', { text: 'expected' });
  await rejectsCode(f.bridge.request('ping', { text: 'second' }), 'bridge_busy');
  f.transport.emit(envelope('ping.result', { text: 'different' }, f.transport.messages.at(-1).id));
  await rejectsCode(pending, 'invalid_response');
  f.bridge.dispose();
});

test('host errors, null correlation, timeout, transport exceptions and disposal are controlled', async () => {
  const f = fixture({ timeoutMs: 15 });
  const pending = f.bridge.request('ready');
  f.transport.emit(envelope('error', { code: 'invalid_message' }, null));
  assert.deepEqual(f.errors, ['invalid_message']);
  assert.equal(f.bridge.pending.size, 1);
  f.transport.emit(envelope('error', { code: 'invalid_message' }, f.transport.messages[0].id));
  await rejectsCode(pending, 'invalid_message');
  await rejectsCode(f.bridge.request('ready'), 'timeout');
  assert.equal(f.bridge.pending.size, 0);
  f.transport.postMessage = () => { throw new Error('native transport unavailable'); };
  await rejectsCode(f.bridge.request('ready'), 'bridge_unavailable');
  f.transport.postMessage = () => {};
  const interrupted = f.bridge.request('ready');
  f.bridge.dispose();
  await rejectsCode(interrupted, 'bridge_unavailable');
  assert.equal(f.bridge.pending.size, 0);
  assert.equal(f.transport.listeners.size, 0);
});

test('theme events require a handshake, valid colors and a fresh id', async () => {
  const f = fixture();
  f.transport.emit(envelope('theme.changed', theme));
  assert.equal(f.themes.length, 0);
  await connectFake(f);
  f.transport.emit(envelope('theme.changed', theme, f.transport.messages[0].id));
  assert.equal(f.themes.length, 0);
  f.transport.emit(envelope('theme.changed', { ...theme, accent: 'invalid' }));
  assert.equal(f.themes.length, 0);
  f.transport.emit(envelope('theme.changed', theme));
  assert.deepEqual(f.themes, [theme]);
  f.transport.emit(envelope('theme.changed', theme));
  assert.equal(f.themes.length, 1);
  assert.equal(f.errors.length, 4);
  f.bridge.dispose();
});

test('Markdown blocks executable HTML, tracking images and unsafe link schemes', () => {
  const markdown = createMarkdownRenderer();
  const badLinks = ['javascript:alert(1)', 'data:text/html,hi', 'vbscript:x', '//example.org', '/outside', 'file:///C:/secret', 'https://user:pass@example.org', 'https://example.org\n', 'jav&#x61;script:alert(1)'];
  for (const href of badLinks) assert.equal(isSafeLink(href), false, href);
  const attack = markdown.render('<script>alert(1)</script>\n<img src="https://example.org/pixel" onerror="alert(1)">\n![pixel](https://example.org/pixel)\n[x](javascript:alert%281%29)\n[x](data:text/html,bad)\n[x](file:///secret)');
  assert.doesNotMatch(attack, /<(script|img|iframe)\b|<[^>]+\son\w+=|href="(?:javascript|file|data):/i);
  assert.match(attack, /&lt;script&gt;/);
  const rendered = markdown.render(sampleMarkdown);
  assert.match(rendered, /<h3>/);
  assert.match(rendered, /<ul>/);
  assert.match(rendered, /href="#section"/);
  assert.match(rendered, /class="hljs-keyword"/);
  for (const href of ['https://example.org', 'http://example.org', 'mailto:hello@example.org', '#section']) assert.equal(isSafeLink(href), true);
  assert.match(markdown.render('[safe](https://example.org)'), /rel="noopener noreferrer"/);
  assert.doesNotMatch(markdown.render('```unknown\n<script>alert(1)</script>\n```'), /<script>/);
});

test('real diff2html renders two sides without English controls or unescaped source HTML', () => {
  const hostile = sampleDiff.replace('Olá, Lumen', '<img src=x onerror=alert(1)>');
  const output = renderDiff(hostile, diffEmpty);
  assert.doesNotMatch(output, /<img|\bViewed\b|\bCHANGED\b|type="checkbox"/);
  assert.match(output, /&lt;img/);
  assert.equal((output.match(/class="d2h-file-side-diff"/g) || []).length, 2);
  assert.match(output, /<del>/);
  assert.match(output, /<ins>/);
});

test('virtual ranges cover 500 records with a bounded window, including overscroll', () => {
  assert.equal(visibleRange(0, 0, 280).end, 0);
  assert.equal(visibleRange(500, -1000, 280).start, 0);
  assert.equal(visibleRange(500, 999999, 280).end, 500);
  const seen = new Set();
  for (let offset = 0; offset < 500 * ROW_HEIGHT; offset += ROW_HEIGHT) {
    const range = visibleRange(500, offset, 280);
    assert.ok(range.end - range.start <= Math.ceil(280 / ROW_HEIGHT) + 1 + OVERSCAN * 2);
    assert.ok(range.end - range.start < 500);
    assert.equal(range.offset, range.start * ROW_HEIGHT);
    for (let index = range.start; index < range.end; index++) seen.add(index);
  }
  assert.equal(seen.size, 500);
});

function expectedRange(heights, scrollTop, height) {
  let position = 0;
  const boxes = heights.map(size => { const box = { top: position, bottom: position + size }; position += size; return box; });
  const top = Math.max(0, Math.min(scrollTop, Math.max(0, position - height)));
  const visible = boxes.map((box, index) => ({ ...box, index })).filter(box => box.bottom > top && box.top < top + height);
  const start = Math.max(0, (visible[0]?.index ?? heights.length) - OVERSCAN);
  const end = Math.min(heights.length, (visible.at(-1)?.index ?? heights.length - 1) + 1 + OVERSCAN);
  return { start, end, offset: boxes[start]?.top ?? position, totalHeight: position };
}

test('variable-height ranges match an independent geometric oracle at boundaries, overscroll and every visible turn', () => {
  const heights = Array.from({ length: 500 }, (_, index) => [24, 113, 900, 47, 301][index % 5]);
  const total = heights.reduce((sum, height) => sum + height, 0), seen = new Set();
  for (const offset of [-1000, 0, 24, 137, 1037, ...Array.from({ length: Math.ceil(total / 80) }, (_, index) => index * 80), total, total + 9999]) {
    const actual = visibleRange(500, offset, 280, heights), expected = expectedRange(heights, offset, 280);
    assert.deepEqual(actual, expected, `scrollTop=${offset}`);
    assert.ok(actual.end - actual.start <= Math.ceil(280 / 24) + 1 + OVERSCAN * 2);
    for (let index = actual.start; index < actual.end; index++) seen.add(index);
  }
  assert.equal(seen.size, 500);
  const mixed = [24, undefined, 900, 0, 47];
  assert.deepEqual(visibleRange(mixed.length, 150, 280, mixed), expectedRange([24, ROW_HEIGHT, 900, ROW_HEIGHT, 47], 150, 280));
  const expected = expectedRange(heights, 4000, 280);
  const deliberatelyWrong = visibleRange(500, 4000, 280, Array(500).fill(ROW_HEIGHT));
  assert.throws(() => assert.deepEqual(deliberatelyWrong, expected), assert.AssertionError, 'the oracle must reject fixed/wrong heights');
});

function virtualFixture(t, sizes = Array(500).fill(ROW_HEIGHT)) {
  const keys = ['document', 'ResizeObserver', 'requestAnimationFrame', 'cancelAnimationFrame'];
  const original = new Map(keys.map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  const frames = new Map(), observers = [], ranges = [], renders = []; let nextFrame = 0;
  globalThis.document = { createDocumentFragment: () => ({ children: [], append(row) { this.children.push(row); } }) };
  globalThis.requestAnimationFrame = callback => { frames.set(++nextFrame, callback); return nextFrame; };
  globalThis.cancelAnimationFrame = id => frames.delete(id);
  globalThis.ResizeObserver = class {
    targets = new Set();
    constructor(callback) { this.callback = callback; observers.push(this); }
    observe(target) { this.targets.add(target); }
    disconnect() { this.targets.clear(); }
  };
  const scrollListeners = new Set();
  const viewport = { clientHeight: 280, scrollTop: 0,
    addEventListener(type, callback) { assert.equal(type, 'scroll'); scrollListeners.add(callback); },
    removeEventListener(type, callback) { assert.equal(type, 'scroll'); scrollListeners.delete(callback); },
    focus() { this.focused = true; } };
  const layer = { style: {}, children: [], replaceChildren(fragment) { this.children = [...fragment.children]; fragment.children.length = 0; } };
  const spacer = { style: {} };
  const history = new VirtualHistory(viewport, spacer, layer, (item, index) => {
    renders.push(index);
    return { item, index, style: {}, attributes: {}, setAttribute(key, value) { this.attributes[key] = value; },
      getAttribute(key) { return this.attributes[key] ?? null; }, getBoundingClientRect() { return { height: sizes[index] }; } };
  }, range => ranges.push(range));
  const flush = () => {
    for (let step = 0; frames.size && step < 100; step++) {
      const pending = [...frames]; frames.clear(); for (const [, callback] of pending) callback(0);
    }
    assert.equal(frames.size, 0, 'measurement must converge, not schedule an endless render loop');
  };
  t.after(() => {
    try { history.dispose(); assert.equal(frames.size, 0); assert.equal(scrollListeners.size, 0); assert.ok(observers.every(observer => !observer.targets.size)); }
    finally { for (const [key, descriptor] of original) { if (descriptor) Object.defineProperty(globalThis, key, descriptor); else delete globalThis[key]; } }
  });
  return { history, viewport, layer, spacer, sizes, frames, observers, ranges, renders, flush,
    scroll(top) { viewport.scrollTop = top; for (const callback of scrollListeners) callback(); },
    resize() { for (const observer of observers) observer.callback([]); } };
}

test('virtualizer mounts only the current slice and coalesces/cancels RAF (DOM adapter, not a browser test)', t => {
  const f = virtualFixture(t); f.history.setItems(Array.from({ length: 500 })); f.flush();
  assert.ok(f.layer.children.length < 12);
  assert.equal(f.layer.children[0].getAttribute('aria-setsize'), '500');
  assert.equal(f.spacer.style.height, `${500 * ROW_HEIGHT}px`);
  const mounted = [...f.layer.children]; f.history.render(); assert.deepEqual(f.layer.children, mounted);
  f.scroll(112); f.scroll(224); f.scroll(336); assert.equal(f.frames.size, 1); f.flush();
  f.history.jump(true); f.flush();
  assert.equal(f.layer.children.at(-1).index, 499); assert.ok(f.layer.children.length < 12);
  assert.equal(f.layer.children.at(-1).getAttribute('aria-posinset'), '500'); assert.equal(f.viewport.focused, true);
  f.scroll(0); assert.equal(f.frames.size, 1); f.history.dispose(); assert.equal(f.frames.size, 0);
});

test('measured variable heights update spacer/translation, preserve exact visible coverage and reject wrong-height verification', t => {
  const sizes = Array.from({ length: 500 }, (_, index) => [23.2, 150.1, 800, 51.9, 305.4][index % 5]);
  const f = virtualFixture(t, sizes); f.history.setItems(Array.from({ length: 500 }, (_, index) => index)); f.flush();
  const verify = () => {
    const measured = Array.from({ length: 500 }, (_, index) => f.history.heights[index] || ROW_HEIGHT);
    const range = expectedRange(measured, f.viewport.scrollTop, f.viewport.clientHeight);
    assert.equal(f.spacer.style.height, `${range.totalHeight}px`);
    assert.equal(f.layer.style.transform, `translateY(${range.offset}px)`);
    assert.deepEqual(f.layer.children.map(row => row.index), Array.from({ length: range.end - range.start }, (_, index) => range.start + index));
    assert.ok(f.layer.children.length <= Math.ceil(280 / 24) + 1 + OVERSCAN * 2);
    for (const row of f.layer.children) assert.equal(f.history.heights[row.index], Math.ceil(sizes[row.index]));
  };
  verify();
  for (const top of [200, 1000, 2200, 4000, 7000, 10000, 15000, 25000, 50000]) { f.scroll(top); f.flush(); verify(); }
  f.history.jump(true); f.flush(); verify(); assert.equal(f.layer.children.at(-1).index, 499);
  assert.notEqual(f.spacer.style.height, `${500 * ROW_HEIGHT}px`);
  const measuredIndex = f.layer.children[0].index, correctHeight = f.history.heights[measuredIndex];
  f.history.heights[measuredIndex] = correctHeight + 99;
  assert.throws(verify, assert.AssertionError, 'verifier must reject deliberately wrong measurement');
  f.history.heights[measuredIndex] = correctHeight;
  verify();
  assert.equal(f.history.measure(), false, 'unchanged rows do not invalidate repeatedly');
  sizes[measuredIndex] += 211.4; f.resize(); assert.equal(f.frames.size, 1); f.flush(); verify();
  assert.equal(f.history.heights[measuredIndex], Math.ceil(sizes[measuredIndex]));
  f.history.setItems([]); f.flush(); assert.equal(f.layer.children.length, 0); assert.equal(f.spacer.style.height, '0px');
  assert.deepEqual(f.history.heights, []);
});

test('theme tokens preserve host colors and reject low-contrast accents for focus and text', () => {
  assert.ok(contrast('#202124', '#202124') < 3);
  for (const value of [theme, { background: '#ffffff', foreground: '#202124', accent: '#eeeeee' }]) {
    const tokens = themeProperties(value);
    assert.equal(tokens['--vs-background'], value.background);
    assert.equal(tokens['--vs-foreground'], value.foreground);
    assert.equal(tokens['--vs-accent'], value.accent);
    assert.ok(contrast(tokens['--focus-color'], value.background) >= 3);
    assert.ok(contrast(tokens['--link-color'], value.background) >= 4.5);
    assert.ok(contrast(tokens['--syntax-string'], value.background) >= 4.5);
    assert.ok(contrast(tokens['--button-foreground'], value.accent) >= 4.5);
  }
});

test('local resources cover all static labels and have matching translations/placeholders', async () => {
  assert.deepEqual(Object.keys(pt).sort(), Object.keys(en).sort());
  for (const key of Object.keys(pt)) {
    assert.equal(typeof pt[key], 'string');
    assert.ok(pt[key].length && en[key].length);
    assert.deepEqual(pt[key].match(/\{\w+\}/g), en[key].match(/\{\w+\}/g), key);
  }
  const html = await readFile(path.join(webRoot, 'index.html'), 'utf8');
  for (const [, key] of html.matchAll(/data-i18n(?:-label|-title|-placeholder)?="([^"]+)"/g)) assert.ok(Object.hasOwn(pt, key), key);
  assert.equal(html.replace(/<[^>]*>/g, '').trim(), '');
  const application = await readFile(path.join(webRoot, 'app.mjs'), 'utf8');
  for (const [, key] of application.matchAll(/\btext\('([^']+)'/g)) assert.ok(Object.hasOwn(pt, key), key);
  assert.doesNotMatch(application, /localStorage|sessionStorage|fetch\(|XMLHttpRequest|WebSocket/);
  assert.match(application, /connected: \(\) => state === 'connected'/);
  assert.doesNotMatch(application, /connected: \(\) => true/);
  const byok = await readFile(path.join(webRoot, 'byok.mjs'), 'utf8');
  assert.match(byok, /isComposing/);
});

test('context controls remain a collapsed native composer section with labelled bounds and explicit privacy guidance', async () => {
  const html = await readFile(path.join(webRoot, 'index.html'), 'utf8');
  const css = await readFile(path.join(webRoot, 'styles.css'), 'utf8');
  const composer = html.slice(html.indexOf('<footer class="composer-panel">'), html.indexOf('</footer>'));
  assert.match(composer, /<details id="byok-context"[^>]*>\s*<summary data-i18n="contextTitle">/);
  assert.doesNotMatch(composer.match(/<details id="byok-context"[^>]*>/)[0], /\bopen\b/);
  assert.match(composer, /<label for="byok-context-path" data-i18n="contextPath">/);
  assert.match(composer, /<input id="byok-context-path" type="text" maxlength="1024"[^>]*disabled/);
  assert.match(composer, /<textarea id="byok-message"[^>]*maxlength="262144"/);
  for (const id of ['reference', 'file', 'solution', 'selection', 'output']) {
    assert.match(composer, new RegExp(`<button id="byok-context-${id}" type="button"[^>]*disabled`));
  }
  for (const locale of [pt, en]) {
    assert.match(locale.contextHelp, /64K/); assert.match(locale.contextHelp, /128K/);
    assert.match(locale.contextHelp, /snapshot/i); assert.match(locale.contextHelp, /inventário|inventory/i);
  }
  assert.equal(pt.contextTitle, 'Contexto'); assert.equal(en.contextTitle, 'Context');
  assert.match(css, /\.context-controls input\s*\{[^}]*var\(--vs-background\)[^}]*var\(--vs-foreground\)/);
  assert.match(css, /\.context-list pre, \.context-history pre\s*\{[^}]*max-height: 240px;[^}]*overflow: auto/);
});

test('CSP and asset graph contain no CDN, fonts, inline script or tracking images', async () => {
  const html = await readFile(path.join(webRoot, 'index.html'), 'utf8');
  assert.ok(html.includes(`content="${csp}"`));
  assert.match(csp, /img-src 'none'/);
  assert.match(csp, /connect-src 'self'/);
  assert.match(csp, /script-src 'self';/);
  assert.doesNotMatch(csp, /unsafe-eval|script-src[^;]*unsafe-inline/);
  assert.doesNotMatch(html, /<script(?![^>]*\bsrc=)[^>]*>|\son\w+=|<img\b|<iframe\b/i);
  for (const [, target] of html.matchAll(/(?:src|href)="([^"]+)"/g)) {
    assert.ok(target.startsWith('./'), target);
    await resolveStaticPath(`/${target.slice(2)}`);
  }
  const css = await readFile(path.join(webRoot, 'styles.css'), 'utf8');
  assert.doesNotMatch(css, /@import|url\(|@font-face/);
  for (const name of (await readdir(webRoot)).filter(name => name.endsWith('.mjs'))) {
    const source = await readFile(path.join(webRoot, name), 'utf8');
    for (const [, target] of source.matchAll(/\bfrom\s+['"]([^'"]+)['"]/g)) {
      assert.ok(target.startsWith('./'));
      await resolveStaticPath(`/${target.slice(2)}`);
    }
  }
});

test('vendored bytes match their manifest and every bundled package has a local license', async () => {
  const directory = path.join(webRoot, 'vendor');
  const manifest = await loadJson(path.join(directory, 'manifest.json'));
  const bundle = await readFile(path.join(directory, manifest.bundle));
  assert.equal(createHash('sha256').update(bundle).digest('hex'), manifest.sha256);
  assert.notEqual(createHash('sha256').update(Buffer.concat([bundle, Buffer.from('tampered')])).digest('hex'), manifest.sha256);
  const packageJson = await loadJson(path.join(root, 'package.json'));
  const lock = await loadJson(path.join(root, 'package-lock.json'));
  for (const [name, version] of Object.entries({ ...packageJson.dependencies, ...packageJson.devDependencies })) {
    assert.match(version, /^\d+\.\d+\.\d+$/);
    assert.equal(lock.packages[`node_modules/${name}`].version, version);
  }
  for (const dependency of Object.keys(packageJson.dependencies)) assert.ok(manifest.packages.some(pkg => pkg.name === dependency));
  for (const pkg of manifest.packages) {
    assert.ok(pkg.files.length > 0, pkg.name);
    for (const file of pkg.files) assert.ok((await readFile(path.join(directory, file), 'utf8')).length > 100);
  }
});

test('path confinement rejects traversal, malformed encoding, directories and outside junctions', async t => {
  for (const target of ['/../AGENTS.md', '/%2e%2e/AGENTS.md', '/%2e%2e%5cAGENTS.md', '/C:/Windows/win.ini', '/vendor/../../index.html', '/vendor/.. /index.html', '/index.html:stream', '/%00index.html', '/%ZZ', '//outside', '/vendor']) {
    await assert.rejects(resolveStaticPath(target), error => [400, 403, 404].includes(error.status), target);
  }
  const directory = await mkdtemp(path.join(webRoot, 'ui-test-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  await symlink(path.join(root, 'scripts'), path.join(directory, 'outside'), process.platform === 'win32' ? 'junction' : 'dir');
  await assert.rejects(resolveStaticPath(`/${path.basename(directory)}/outside/serve-ui.mjs`), error => error.status === 403);
  assert.equal(await resolveStaticPath('/'), path.join(webRoot, 'index.html'));
  assert.equal(await resolveStaticPath('/locales/en.json?culture=en'), path.join(webRoot, 'locales/en.json'));
});

function httpCall(port, target, options = {}) {
  return new Promise((resolve, reject) => {
    const req = request({ hostname: '127.0.0.1', port, path: target, ...options }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => resolve({ status: response.statusCode, headers: response.headers, body: Buffer.concat(chunks).toString('utf8') }));
    });
    req.on('error', reject);
    req.end();
  });
}

test('loopback server serves only UI assets and refuses traversal, foreign Host and writes', async t => {
  const server = createUiServer();
  t.after(() => new Promise(resolve => server.close(resolve)));
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const { port, address } = server.address();
  assert.equal(address, '127.0.0.1');
  for (const target of ['/../../AGENTS.md', '/%2e%2e/AGENTS.md', '/vendor/../../../package.json']) {
    const response = await httpCall(port, target);
    assert.equal(response.status, 403);
    assert.equal(response.body, '');
  }
  assert.equal((await httpCall(port, '/', { method: 'POST' })).status, 405);
  assert.equal((await httpCall(port, '/', { headers: { Host: 'example.org' } })).status, 403);
  assert.equal((await httpCall(port, '/missing.js')).status, 404);
  const page = await httpCall(port, '/index.html');
  assert.equal(page.status, 200);
  assert.match(page.headers['content-security-policy'], /frame-ancestors 'none'/);
  assert.match(page.body, /id="byok-output"/);
  const locale = await httpCall(port, '/locales/en.json');
  assert.match(locale.headers['content-type'], /^application\/json/);
  assert.equal(JSON.parse(locale.body).appTitle, en.appTitle);
  const module = await httpCall(port, '/vendor/libraries.mjs', { method: 'HEAD' });
  assert.equal(module.status, 200);
  assert.match(module.headers['content-type'], /^text\/javascript/);
  assert.equal(module.body, '');
});
