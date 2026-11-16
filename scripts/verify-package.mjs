import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readdir, readFile, rm, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import os from 'node:os';

const root = fileURLToPath(new URL('../', import.meta.url));
export const vsixPath = path.join(root, 'src', 'Lumen.Vsix', 'bin', 'Release', 'net48', 'Lumen.Vsix.vsix');

const TEXT_EXTENSIONS = new Set(['.json', '.mjs', '.js', '.md', '.html', '.css', '.txt', '.xml', '.pkgdef', '.vsixmanifest']);
const SECRET_PATTERNS = [
  [/sk-[A-Za-z0-9_-]{16,}/, 'openai-style-key'],
  [/gh[pousr]_[A-Za-z0-9]{20,}/, 'github-token'],
  [/AIza[0-9A-Za-z_-]{30,}/, 'gemini-key'],
  [/Bearer\s+[A-Za-z0-9._~+/=-]{16,}/i, 'bearer-token'],
  [/['"]?(?:api[_-]?key|secret|token|password|authorization)['"]?\s*[:=]\s*['"]?[A-Za-z0-9_\-]{16,}/i, 'credential-assignment'],
  [/lumen-canary-[0-9a-f-]{16,}/i, 'canary']
];
const FORBIDDEN_NAMES = [/^\.env($|\.)/i, /\.vsix$/i, /\.pfx$/i, /\.p12$/i, /\.snk$/i, /\.key$/i, /^providers(\.theme|\.skills)?\.json$/i];
const FORBIDDEN_FOLDERS = new Set(['bin', 'obj', '.git', '.vs']);

// Findings are returned, not thrown, so the negative controls can prove the detector rejects bad packages.
export function scanPackageEntries(entries) {
  const findings = [];
  for (const entry of entries) {
    const name = entry.path.replace(/\\/g, '/');
    const segments = name.split('/');
    if (segments.some(segment => FORBIDDEN_FOLDERS.has(segment.toLowerCase()))) findings.push(`${name}: forbidden folder`);
    if (FORBIDDEN_NAMES.some(pattern => pattern.test(segments.at(-1)))) findings.push(`${name}: forbidden file`);
    if (!TEXT_EXTENSIONS.has(path.extname(name).toLowerCase())) continue;
    const text = entry.content.toString('utf8');
    for (const [pattern, code] of SECRET_PATTERNS)
      if (pattern.test(text)) findings.push(`${name}: ${code}`);
  }
  return findings;
}

async function collectFiles(folder, prefix = '') {
  const entries = [];
  for (const item of await readdir(folder, { withFileTypes: true })) {
    const relative = prefix ? `${prefix}/${item.name}` : item.name;
    const full = path.join(folder, item.name);
    if (item.isDirectory()) entries.push(...await collectFiles(full, relative));
    else entries.push({ path: relative, content: await readFile(full) });
  }
  return entries;
}

function extract(vsix, destination) {
  const script = `Add-Type -AssemblyName System.IO.Compression.FileSystem; ` +
    `[System.IO.Compression.ZipFile]::ExtractToDirectory('${vsix.replace(/\\/g, '/')}', '${destination.replace(/\\/g, '/')}')`;
  const result = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script], { encoding: 'utf8', timeout: 60000 });
  assert.equal(result.status, 0, result.stderr || result.stdout);
}

test('package scanner rejects secrets, key files and bin/obj with negative controls', () => {
  assert.deepEqual(scanPackageEntries([{ path: 'wwwroot/app.mjs', content: Buffer.from('const x = 1;') }]), []);
  assert.ok(scanPackageEntries([{ path: 'wwwroot/canary.txt', content: Buffer.from('lumen-canary-0123456789abcdef') }]).some(f => f.includes('canary')));
  assert.ok(scanPackageEntries([{ path: 'src/bin/leak.dll', content: Buffer.from('') }]).some(f => f.includes('forbidden folder')));
  assert.ok(scanPackageEntries([{ path: 'providers.json', content: Buffer.from('{}') }]).some(f => f.includes('forbidden file')));
  assert.ok(scanPackageEntries([{ path: 'wwwroot/config.json', content: Buffer.from('{"api_key":"abcdefghijklmnopqrstuvwxyz"}') }]).some(f => f.includes('credential-assignment')));
});

test('built VSIX contains no secret, bin/obj or key material', async () => {
  assert.ok((await stat(vsixPath)).isFile(), `Build the VSIX first: ${vsixPath}`);
  const temp = await mkdtemp(path.join(os.tmpdir(), 'lumen-package-'));
  try {
    extract(vsixPath, temp);
    const entries = await collectFiles(temp);
    assert.ok(entries.length > 20, 'A partial extraction would make the scan meaningless.');
    assert.deepEqual(scanPackageEntries(entries), []);
  } finally { await rm(temp, { recursive: true, force: true }); }
});
