import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import timers from 'node:timers/promises';
import { pathToFileURL } from 'node:url';

const digest = (bytes) => createHash('sha256').update(bytes).digest('hex');

export async function checkResponse(url, expectedBytes, status, contentType) {
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(20_000) });
      assert.equal(response.status, status, `${url}: expected HTTP ${status}`);
      assert.ok(response.headers.get('content-type')?.includes(contentType), `${url}: wrong content type`);
      assert.equal(digest(Buffer.from(await response.arrayBuffer())), digest(expectedBytes), `${url}: hosted content differs from the checked build`);
      return;
    } catch (error) {
      if (attempt === 1) throw error;
      // Cloudflare can acknowledge deployment before its assets reach every edge.
      console.warn(`${url}: publication is not ready; retrying the same checks in 30 seconds.`);
      await timers.setTimeout(30_000);
    }
  }
}

async function main() {

  const dist = 'docs/.vitepress/dist';
  const assets = readdirSync(`${dist}/assets`);
  const app = assets.find((name) => /^app\..+\.js$/.test(name));
  const style = assets.find((name) => /^style\..+\.css$/.test(name));
  const search = readdirSync(`${dist}/assets/chunks`).find((name) => /^VPLocalSearchBox\..+\.js$/.test(name));
  assert.ok(app && style && search, 'Missing application or search assets');

  const routes = [
    ['/build-info.json', 'build-info.json', 'application/json'],
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
    await checkResponse(`https://docs.terrascale.tech${route}`, readFileSync(`${dist}/${file}`), 200, contentType);
  }));
  await checkResponse('https://docs.terrascale.tech/__docs_deployment_missing__', readFileSync(`${dist}/404.html`), 404, 'text/html');
  console.log('Workers hosted check passed: 9 pages/assets match the checked build; custom HTTP 404 works.');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
