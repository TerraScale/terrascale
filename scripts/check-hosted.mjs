import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';

const dist = 'docs/.vitepress/dist';
const digest = (bytes) => createHash('sha256').update(bytes).digest('hex');
const assets = readdirSync(`${dist}/assets`);
const app = assets.find((name) => /^app\..+\.js$/.test(name));
const style = assets.find((name) => /^style\..+\.css$/.test(name));
const search = readdirSync(`${dist}/assets/chunks`).find((name) => /^VPLocalSearchBox\..+\.js$/.test(name));
assert.ok(app && style && search, 'Missing application or search assets');

const routes = [
  ['/', 'index.html', 'text/html'],
  ['/getting-started/javascript', 'getting-started/javascript.html', 'text/html'],
  ['/ar/getting-started/', 'ar/getting-started/index.html', 'text/html'],
  ['/guides/zero-configuration', 'guides/zero-configuration.html', 'text/html'],
  ['/examples/terrabase-js/quickstart.mjs', 'examples/terrabase-js/quickstart.mjs', 'javascript'],
  [`/assets/${app}`, `assets/${app}`, 'javascript'],
  [`/assets/${style}`, `assets/${style}`, 'text/css'],
  [`/assets/chunks/${search}`, `assets/chunks/${search}`, 'javascript'],
];
await Promise.all(routes.map(async ([route, file, contentType]) => {
  const response = await fetch(`https://docs.terrascale.tech${route}`, { signal: AbortSignal.timeout(20_000) });
  assert.equal(response.status, 200, `${route}: expected HTTP 200`);
  assert.ok(response.headers.get('content-type')?.includes(contentType), `${route}: wrong content type`);
  assert.equal(digest(Buffer.from(await response.arrayBuffer())), digest(readFileSync(`${dist}/${file}`)), `${route}: hosted content differs from the checked build`);
}));
const missing = await fetch('https://docs.terrascale.tech/__docs_deployment_missing__', { signal: AbortSignal.timeout(20_000) });
assert.equal(missing.status, 404, 'Missing routes must return HTTP 404');
assert.equal(digest(Buffer.from(await missing.arrayBuffer())), digest(readFileSync(`${dist}/404.html`)), 'Missing route did not serve the VitePress 404 shell');
console.log('Workers hosted check passed: 8 pages/assets match the checked build; custom HTTP 404 works.');
