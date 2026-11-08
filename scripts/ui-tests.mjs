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
import { contrast, themeProperties } from '../src/Lumen.Vsix/wwwroot/theme.mjs';
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

test('virtualizer mounts only the current slice (DOM adapter fake, not a browser test)', t => {
  const original = { document: globalThis.document, ResizeObserver: globalThis.ResizeObserver, cancelAnimationFrame: globalThis.cancelAnimationFrame };
  t.after(() => {
    for (const [key, value] of Object.entries(original)) {
      if (value === undefined) delete globalThis[key];
      else globalThis[key] = value;
    }
  });
  globalThis.document = { createDocumentFragment: () => ({ children: [], append(row) { this.children.push(row); } }) };
  globalThis.ResizeObserver = class { observe() {} disconnect() {} };
  globalThis.cancelAnimationFrame = () => {};
  const viewport = { clientHeight: 280, scrollTop: 0, addEventListener() {}, removeEventListener() {}, focus() {} };
  const layer = { style: {}, children: [], replaceChildren(fragment) { this.children = fragment.children; } };
  const spacer = { style: {} };
  const history = new VirtualHistory(viewport, spacer, layer, (_, index) => ({ index, style: {}, attributes: {}, setAttribute(key, value) { this.attributes[key] = value; } }), () => {});
  history.setItems(Array.from({ length: 500 }));
  assert.ok(layer.children.length < 12);
  assert.equal(layer.children[0].attributes['aria-setsize'], '500');
  assert.equal(spacer.style.height, `${500 * ROW_HEIGHT}px`);
  history.jump(true);
  assert.equal(layer.children.at(-1).index, 499);
  assert.ok(layer.children.length < 12);
  assert.equal(layer.children.at(-1).attributes['aria-posinset'], '500');
  history.dispose();
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
