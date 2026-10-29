// Node port of build-standalone.ps1 for machines without PowerShell (e.g. macOS).
// Usage: node scripts/build-standalone.cjs [output-dir]
'use strict';
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');

const root = path.resolve(__dirname, '..');
const defaultDist = path.join(root, 'dist');
const dist = process.argv[2] ? path.resolve(process.argv[2]) : defaultDist;
const syncRootRelease = dist === defaultDist;
const output = path.join(dist, 'index.html');
const selfOutput = path.join(dist, 'index.self-extract.html');

fs.mkdirSync(dist, { recursive: true });
const source = fs.readFileSync(path.join(root, 'src', 'index.template.html'), 'utf8');
const configRaw = fs.readFileSync(path.join(root, 'app.config.json'), 'utf8');
const config = JSON.parse(configRaw);
const manifestObj = {
  name: config.name,
  version: config.version,
  generatedAtUtc: new Date().toISOString(),
  dependencyCount: 0,
  runtimeNetworkBlocked: true
};
const manifest = JSON.stringify(manifestObj);

for (const token of ['__APP_CONFIG_JSON__', '__BUILD_MANIFEST_JSON__', '__EMBEDDED_ASSET_BUNDLE_BASE64__']) {
  const count = source.split(token).length - 1;
  if (count !== 1) throw new Error(`Expected exactly one ${token} placeholder, found ${count}.`);
}

const html = source
  .replace('__APP_CONFIG_JSON__', () => configRaw.trim())
  .replaceAll('data-app-version></span>', `data-app-version>v${config.version}</span>`)
  .replace('__BUILD_MANIFEST_JSON__', () => manifest)
  .replace('__EMBEDDED_ASSET_BUNDLE_BASE64__', '"e30="');
fs.writeFileSync(output, html);
if (syncRootRelease) fs.writeFileSync(path.join(root, 'document-scanner.html'), html);
fs.writeFileSync(path.join(dist, 'build-manifest.json'), JSON.stringify(manifestObj, null, 2));
fs.writeFileSync(path.join(dist, '.nojekyll'), '');
fs.writeFileSync(path.join(dist, 'dependency-manifest.json'),
  JSON.stringify({ generatedAtUtc: manifestObj.generatedAtUtc, dependencies: [] }, null, 2));

const bytes = Buffer.from(html, 'utf8');
const compressed = zlib.gzipSync(bytes, { level: 9 });
const b64 = compressed.toString('base64');
const title = config.name.replace(/&/g, '&amp;').replace(/</g, '&lt;');
const self = `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"><meta name="color-scheme" content="light"><meta http-equiv="Content-Security-Policy" content="default-src 'self' data: blob:; script-src 'unsafe-inline' 'self' blob:; style-src 'unsafe-inline' 'self'; connect-src 'none'; object-src 'none'; frame-src 'none'; base-uri 'none'"><title>${title}</title></head><body><noscript>JavaScript is required.</noscript><script>(async()=>{const b='${b64}',raw=Uint8Array.from(atob(b),c=>c.charCodeAt(0));if(!('DecompressionStream'in window)){document.body.textContent='This browser does not support DecompressionStream. Open dist/index.html instead.';return}const stream=new Blob([raw]).stream().pipeThrough(new DecompressionStream('gzip'));const html=await new Response(stream).text();document.open();document.write(html);document.close()})().catch(e=>{document.body.textContent='Failed to open standalone app: '+e.message})</script></body></html>`;
fs.writeFileSync(selfOutput, self);

const sha = data => crypto.createHash('sha256').update(data).digest('hex');
const selfBytes = Buffer.from(self, 'utf8');
fs.writeFileSync(path.join(dist, 'self-extract-manifest.json'), JSON.stringify({
  generatedAtUtc: manifestObj.generatedAtUtc,
  sourceBytes: bytes.length,
  compressedBytes: compressed.length,
  selfExtractBytes: selfBytes.length,
  sourceSha256: sha(bytes),
  selfExtractSha256: sha(selfBytes)
}, null, 2));
console.log('Built ' + output);
console.log('Built ' + selfOutput);
