import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { readBuildOutput } from '@cloudflare/build-output-utils';
import { filesIn } from './public-audit.mjs';

test('cf output is portable and contains exactly the checked site bytes', async () => {
  const { workers } = await readBuildOutput(process.cwd());
  assert.equal(workers.default.config.name, 'ts-docs');
  assert.deepEqual(workers.default.config.domains, ['docs.terrascale.tech']);
  assert.equal(workers.default.bundleDir, undefined);
  assert.equal(workers.default.config.assets.notFoundHandling, '404-page');
  const source = 'docs/.vitepress/dist';
  const assets = workers.default.assetsDir;
  const sourceFiles = filesIn(source).map((file) => file.slice(source.length));
  assert.deepEqual(filesIn(assets).map((file) => file.slice(assets.length)), sourceFiles);
  for (const file of sourceFiles) {
    assert.deepEqual(await readFile(`${assets}${file}`), await readFile(`${source}${file}`), file);
  }
});
