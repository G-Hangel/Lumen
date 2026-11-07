import { build } from 'esbuild';
import { createHash } from 'node:crypto';
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const output = path.join(root, 'src/Lumen.Vsix/wwwroot/vendor');
const result = await build({
  absWorkingDir: root,
  stdin: {
    contents: `import MarkdownIt from 'markdown-it';
import hljs from 'highlight.js/lib/core';
import csharp from 'highlight.js/lib/languages/csharp';
import { html as diffHtml } from 'diff2html';
hljs.registerLanguage('csharp', csharp);
export { MarkdownIt, hljs, diffHtml };`,
    resolveDir: root,
    sourcefile: 'lumen-vendor-entry.js',
  },
  bundle: true,
  format: 'esm',
  platform: 'browser',
  target: 'es2022',
  minify: true,
  legalComments: 'eof',
  metafile: true,
  write: false,
  outfile: path.join(output, 'libraries.mjs'),
});

await mkdir(output, { recursive: true });
const owners = new Map();
for (const input of Object.keys(result.metafile.inputs)) {
  const normalized = input.replaceAll('\\', '/');
  const marker = normalized.lastIndexOf('node_modules/');
  if (marker < 0) continue;
  const remainder = normalized.slice(marker + 'node_modules/'.length).split('/');
  const packageName = remainder[0].startsWith('@') ? remainder.slice(0, 2).join('/') : remainder[0];
  const directory = path.resolve(root, normalized.slice(0, marker), 'node_modules', packageName);
  owners.set(directory, JSON.parse(await readFile(path.join(directory, 'package.json'), 'utf8')));
}

const packages = [];
for (const [directory, metadata] of [...owners].sort(([a], [b]) => a.localeCompare(b, 'en'))) {
  const licenses = (await readdir(directory)).filter(name => /^(licen[cs]e|copying|notice)([.-]|$)/i.test(name)).sort();
  if (!licenses.length) throw new Error(`Missing license: ${metadata.name}`);
  const files = [];
  for (const license of licenses) {
    const filename = `${metadata.name.replaceAll('/', '-').replace('@', '')}-${metadata.version}-${license.replace(/\.[^.]+$/, '')}.txt`;
    await writeFile(path.join(output, filename), await readFile(path.join(directory, license)));
    files.push(filename);
  }
  packages.push({ name: metadata.name, version: metadata.version, license: metadata.license, files });
}

const bundle = result.outputFiles[0].contents;
await writeFile(path.join(output, 'libraries.mjs'), bundle);
await writeFile(path.join(output, 'manifest.json'), `${JSON.stringify({
  bundle: 'libraries.mjs',
  sha256: createHash('sha256').update(bundle).digest('hex'),
  packages,
}, null, 2)}\n`);
console.log(`Vendored ${packages.length} packages with licenses (${bundle.length} bytes).`);
