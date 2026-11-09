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

test('active SDK installation pins match the dashboard manifest', () => {
  // The dashboard's manifest lives in this monorepo (the ts-dashboard repository was merged in).
  const versions = JSON.parse(readFileSync(new URL('../src/TerraScale.Dashboard/sdk-versions.json', root), 'utf8'));
  assert.deepEqual(Object.keys(versions).sort(), ['go', 'javascript', 'pythonRef']);
  assert.match(versions.javascript, /^\d+\.\d+\.\d+$/);
  assert.match(versions.go, /^v\d+\.\d+\.\d+$/);

  for (const path of [
    'examples/server-token-provider', 'create-terrascale-app/template',
  ]) {
    assert.equal(json(`${path}/package.json`).dependencies['@terrascale/sdk'], versions.javascript, path);
    if (path === 'create-terrascale-app/template') continue;
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

test('dashboard constants, Go README and getting-started pins match the dashboard manifest', () => {
  const versions = JSON.parse(readFileSync(new URL('../src/TerraScale.Dashboard/sdk-versions.json', root), 'utf8'));
  const constants = Object.fromEntries(
    [...read('../src/TerraScale.Dashboard/Features/Home/SdkVersions.cs')
      .matchAll(/public const string (\w+) = "([^"]+)";/g)]
      .map(([, name, value]) => [name, value]),
  );
  assert.deepEqual(Object.keys(constants).sort(), ['Go', 'JavaScript', 'PythonRef']);
  assert.equal(constants.JavaScript, versions.javascript);
  assert.equal(constants.Go, versions.go);
  assert.equal(constants.PythonRef, versions.pythonRef);

  const goReadme = read('../sdk/go/README.md');
  for (const match of goReadme.matchAll(/go get terrascale\.tech\/sdk@(v[\d.]+)/g)) {
    assert.equal(match[1], versions.go, 'sdk/go/README.md');
  }

  const paths = ['docs/getting-started/go.md', 'docs/guides/sdks.md'];
  for (const { code } of LOCALES) {
    if (code === 'en') continue;
    paths.push(`docs/${code}/getting-started/go.md`, `docs/${code}/guides/sdks.md`);
  }
  for (const path of paths) {
    const pins = [...read(path).matchAll(/go get terrascale\.tech\/sdk@(v[\d.]+)/g)];
    assert.ok(pins.length > 0, path);
    for (const match of pins) {
      assert.equal(match[1], versions.go, path);
    }
  }
});
