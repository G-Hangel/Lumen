import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { EventEmitter } from 'node:events';
import { createHash } from 'node:crypto';

const fixture = await readFile(new URL('../tests/Lumen.Tests/Fixtures/openai.sse', import.meta.url), 'utf8');
const codeFixture = 'data: {"choices":[{"index":0,"delta":{"content":"Exemplo de código:\\n\\n```csharp\\npublic static int Add(int a, int b)\\n{\\n    return a + b;\\n}\\n```"}}]}\n\ndata: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}\n\ndata: {"choices":[],"usage":{"prompt_tokens":4,"completion_tokens":12}}\n\ndata: [DONE]\n\n';
export const fixtures = {
  data: [
    { id: 'fixture/plain', name: 'Offline · text only', architecture: { input_modalities: ['text'] }, supported_parameters: ['tools'], context_length: 4096 },
    { id: 'fixture/vision', name: 'Offline · vision + reasoning', architecture: { input_modalities: ['text', 'image'] }, supported_parameters: ['tools', 'reasoning'], reasoning: { mandatory: false, supported_efforts: ['low', 'high'] }, context_length: 4096 },
    { id: 'fixture/code', name: 'Offline · code block', architecture: { input_modalities: ['text'] }, supported_parameters: ['tools'], context_length: 4096 }
  ]
};

export function createFixtureServer() {
  let probeRequests = 0, chatRequests = 0, httpRequests = 0;
  const chatMessageCounts = [], chatTextHashes = [];
  const errorAttempts = new Map();
  const events = new EventEmitter();
  return createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1');
    if (request.method === 'GET' && url.pathname === '/fixture/stats') { response.writeHead(200, { 'Content-Type': 'application/json' }); response.end(JSON.stringify({ probeRequests, chatRequests, httpRequests, chatMessageCounts, chatTextHashes, errorAttempts: Object.fromEntries(errorAttempts) })); return; }
    if (request.method === 'GET' && url.pathname === '/fixture/wait-probe') {
      const reply = () => { response.writeHead(200, { 'Content-Type': 'application/json' }); response.end(JSON.stringify({ probeRequests, chatRequests, httpRequests, chatMessageCounts, chatTextHashes })); };
      if (probeRequests > Number(url.searchParams.get('after'))) reply();
      else { events.once('probe', reply); response.once('close', () => events.removeListener('probe', reply)); }
      return;
    }
    httpRequests++;
    const status = /^\/error(401|403|404|429|402)\/models$/.exec(url.pathname);
    if (request.method === 'GET' && status) {
      // HttpTransport retries a 429 GET up to three times. The first two responses carry Retry-After 0
      // so the retries are instant; the third (the one returned to the caller) carries 90 to exercise the UI wait.
      const attempt = (errorAttempts.get(url.pathname) ?? 0) + 1;
      errorAttempts.set(url.pathname, attempt);
      const retryAfter = status[1] === '429' && attempt % 3 === 0 ? '90' : '0';
      response.writeHead(Number(status[1]), { 'Content-Type': 'application/json', 'Retry-After': retryAfter });
      response.end('{"error":"synthetic rejection"}');
      return;
    }
    if (request.method === 'GET' && url.pathname === '/models') { response.writeHead(200, { 'Content-Type': 'application/json' }); response.end(JSON.stringify(fixtures)); return; }
    if (request.method === 'POST' && url.pathname === '/chat/completions') {
      let bytes = 0;
      const chunks = [];
      for await (const chunk of request) { bytes += chunk.length; if (bytes > 8 * 1024 * 1024) { response.writeHead(413); response.end(); return; } chunks.push(chunk); }
      let payload;
      try { payload = JSON.parse(Buffer.concat(chunks).toString('utf8')); } catch { response.writeHead(400); response.end(); return; }
      if (payload.stream === false) {
        probeRequests++;
        events.emit('probe');
        const reply = () => { response.writeHead(200, { 'Content-Type': 'application/json' }); response.end('{"choices":[{"message":{"content":"ok"}}]}'); };
        if (payload.tools) {
          const timer = setTimeout(reply, 5000);
          response.once('close', () => clearTimeout(timer));
        } else reply();
        return;
      }
      chatRequests++; chatMessageCounts.push(payload.messages?.length ?? 0);
      chatTextHashes.push((payload.messages ?? []).map(message => {
        const parts = typeof message.content === 'string' ? [message.content] : (message.content ?? []).filter(part => part.type === 'text').map(part => part.text);
        return parts.map(text => createHash('sha256').update(text).digest('hex'));
      }));
      if (chatMessageCounts.length > 256) { chatMessageCounts.shift(); chatTextHashes.shift(); }
      response.writeHead(200, { 'Content-Type': 'text/event-stream' });
      const lastContent = payload.messages?.at(-1)?.content;
      const lastText = typeof lastContent === 'string' ? lastContent : Array.isArray(lastContent) ? lastContent.map(part => part.text ?? '').join('') : '';
      if (lastText === 'fixture-cancel-history') {
        response.write('data: {"choices":[{"index":0,"delta":{"content":"Partial fixture history"}}]}\n\n');
        const timer = setTimeout(() => response.end('data: [DONE]\n\n'), 20000);
        response.once('close', () => clearTimeout(timer));
      } else response.end(payload.model === 'fixture/code' ? codeFixture : fixture);
      return;
    }
    response.writeHead(404); response.end();
  });
}
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const server = createFixtureServer();
  server.listen(4184, '127.0.0.1', () => console.log('Offline BYOK fixtures listening on 127.0.0.1:4184; no external APIs.'));
}
