import { createServer } from 'node:http';
import { readFile, realpath, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const webRoot = fileURLToPath(new URL('../src/Lumen.Vsix/wwwroot/', import.meta.url));
export const csp = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'none'; font-src 'none'; object-src 'none'; frame-src 'none'; child-src 'none'; worker-src 'none'; base-uri 'none'; form-action 'none'";
const types = { '.html': 'text/html', '.css': 'text/css', '.js': 'text/javascript', '.mjs': 'text/javascript', '.json': 'application/json', '.txt': 'text/plain' };
const fail = status => Object.assign(new Error(`HTTP ${status}`), { status });
const isWithin = (root, target) => {
  const relative = path.relative(root, target);
  return relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative);
};

export async function resolveStaticPath(target, root = webRoot) {
  if (typeof target !== 'string' || !target.startsWith('/') || target.startsWith('//')) throw fail(400);
  let pathname;
  try { pathname = decodeURIComponent(target.split('?')[0]); } catch { throw fail(400); }
  if (/[\\:\u0000-\u001f\u007f]/.test(pathname)) throw fail(403);
  const parts = pathname.split('/').filter(Boolean);
  if (parts.some(part => part === '.' || part === '..' || /[. ]$/.test(part))) throw fail(403);
  const canonicalRoot = await realpath(root);
  const candidate = path.resolve(canonicalRoot, ...parts, ...(parts.length ? [] : ['index.html']));
  if (!isWithin(canonicalRoot, candidate)) throw fail(403);
  let canonical;
  try { canonical = await realpath(candidate); } catch { throw fail(404); }
  if (!isWithin(canonicalRoot, canonical)) throw fail(403);
  if (!types[path.extname(canonical).toLowerCase()] || !(await stat(canonical)).isFile()) throw fail(404);
  return canonical;
}

export function createUiServer() {
  const server = createServer(async (request, response) => {
    response.setHeader('Content-Security-Policy', `${csp}; frame-ancestors 'none'`);
    response.setHeader('X-Content-Type-Options', 'nosniff');
    response.setHeader('Referrer-Policy', 'no-referrer');
    response.setHeader('Cross-Origin-Resource-Policy', 'same-origin');
    response.setHeader('X-Frame-Options', 'DENY');
    response.setHeader('Cache-Control', 'no-store');
    try {
      if (request.headers.host !== `127.0.0.1:${server.address().port}`) throw fail(403);
      if (request.method !== 'GET' && request.method !== 'HEAD') {
        response.setHeader('Allow', 'GET, HEAD');
        throw fail(405);
      }
      const filename = await resolveStaticPath(request.url);
      const body = await readFile(filename);
      response.writeHead(200, { 'Content-Type': `${types[path.extname(filename).toLowerCase()]}; charset=utf-8`, 'Content-Length': body.length });
      response.end(request.method === 'HEAD' ? undefined : body);
    } catch (error) {
      response.writeHead(error.status || 500);
      response.end();
    }
  });
  return server;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const port = Number(process.env.PORT || 4173);
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('PORT must be an integer from 1 to 65535.');
  const server = createUiServer();
  server.on('error', error => {
    console.error(`UI preview: ${error.code}`);
    process.exitCode = 1;
  });
  server.listen(port, '127.0.0.1', () => console.log(`UI preview (no C# bridge): http://127.0.0.1:${port}/index.html`));
}
