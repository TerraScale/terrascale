import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { relative } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { LOCALES } from '../docs/.vitepress/supportedLocales.mjs';

const root = new URL('../', import.meta.url);
const read = (path) => readFileSync(new URL(path, root), 'utf8');
const json = (path) => JSON.parse(read(path));
const docs = new URL('docs/', root);
const locales = new Set(LOCALES.map(({ code }) => code).filter((code) => code !== 'en'));

test('active SDK installation pins match the dashboard manifest', async () => {
  const url = 'https://raw.githubusercontent.com/TerraScale/ts-dashboard/main/sdk-versions.json';
  const response = await fetch(url, { signal: AbortSignal.timeout(15_000) });
  assert.ok(response.ok, `SDK manifest returned HTTP ${response.status}`);
  const versions = await response.json();
  assert.deepEqual(Object.keys(versions).sort(), ['go', 'javascript', 'python', 'rust']);
  assert.match(versions.javascript, /^\d+\.\d+\.\d+$/);
  for (const language of ['go', 'python', 'rust']) {
    assert.match(versions[language], /^v\d+\.\d+\.\d+$/);
  }

  for (const path of [
    'examples/terrabase-js', 'examples/server-token-provider',
    'examples/typed-models', 'examples/browser-todos', 'create-terrascale-app/template',
  ]) {
    assert.equal(json(`${path}/package.json`).dependencies['@terrascale/sdk'], versions.javascript, path);
    if (['examples/typed-models', 'create-terrascale-app/template'].includes(path)) continue;
    const lock = json(`${path}/package-lock.json`).packages;
    assert.equal(lock[''].dependencies['@terrascale/sdk'], versions.javascript, path);
    assert.equal(lock['node_modules/@terrascale/sdk'].version, versions.javascript, path);
  }
  for (const entry of readdirSync(docs, { recursive: true, withFileTypes: true })) {
    if (!entry.isFile() || !entry.name.endsWith('.md')) continue;
    const path = `${entry.parentPath}/${entry.name}`;
    if (locales.has(relative(fileURLToPath(docs), path).split('/')[0])) continue;
    const text = readFileSync(path, 'utf8');
    for (const match of text.matchAll(/npm install[^\n`]*@terrascale\/sdk@([\d.]+)/g)) {
      assert.equal(match[1], versions.javascript, path);
    }
    for (const match of text.matchAll(/go get terrascale\.tech\/sdk@(v[\d.]+)/g)) {
      assert.equal(match[1], versions.go, path);
    }
  }
});
