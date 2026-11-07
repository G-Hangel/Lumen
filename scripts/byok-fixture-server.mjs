import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { EventEmitter } from 'node:events';

const fixture = await readFile(new URL('../tests/Lumen.Tests/Fixtures/openai.sse', import.meta.url), 'utf8');
export const fixtures = {
  data: [
    { id: 'fixture/plain', name: 'Offline · text only', architecture: { input_modalities: ['text'] }, supported_parameters: ['tools'], context_length: 4096 },
    { id: 'fixture/vision', name: 'Offline · vision + reasoning', architecture: { input_modalities: ['text', 'image'] }, supported_parameters: ['tools', 'reasoning'], reasoning: { mandatory: false, supported_efforts: ['low', 'high'] }, context_length: 4096 }
  ]
};

export function createFixtureServer() {
  let probeRequests = 0;
  const events = new EventEmitter();
  return createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1');
    if (request.method === 'GET' && url.pathname === '/fixture/stats') { response.writeHead(200, { 'Content-Type': 'application/json' }); response.end(JSON.stringify({ probeRequests })); return; }
    if (request.method === 'GET' && url.pathname === '/fixture/wait-probe') {
      const reply = () => { response.writeHead(200, { 'Content-Type': 'application/json' }); response.end(JSON.stringify({ probeRequests })); };
      if (probeRequests > Number(url.searchParams.get('after'))) reply();
      else { events.once('probe', reply); response.once('close', () => events.removeListener('probe', reply)); }
      return;
    }
    const status = /^\/error(401|403|404|429|402)\/models$/.exec(url.pathname);
    if (request.method === 'GET' && status) { response.writeHead(Number(status[1]), { 'Content-Type': 'application/json', 'Retry-After': '0' }); response.end('{"error":"synthetic rejection"}'); return; }
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
      response.writeHead(200, { 'Content-Type': 'text/event-stream' });
      response.end(fixture);
      return;
    }
    response.writeHead(404); response.end();
  });
}
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const server = createFixtureServer();
  server.listen(4184, '127.0.0.1', () => console.log('Offline BYOK fixtures listening on 127.0.0.1:4184; no external APIs.'));
}
