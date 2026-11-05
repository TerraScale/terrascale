import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { readBuildOutput } from '@cloudflare/build-output-utils';
import { filesIn } from './public-audit.mjs';
import { secureHtml } from './security-headers.mjs';

test('cf output is portable and contains exactly the checked site bytes', async () => {
  const { workers, rootConfig } = await readBuildOutput(process.cwd());
  assert.equal(workers.default.config.name, 'ts-docs');
  assert.deepEqual(workers.default.config.domains, rootConfig.buildContext.isPreview ? undefined : ['docs.terrascale.tech']);
  assert.equal(workers.default.bundleDir, undefined);
  assert.equal(workers.default.config.assets.notFoundHandling, '404-page');
  const source = 'docs/.vitepress/dist';
  const assets = workers.default.assetsDir;
  const sourceFiles = filesIn(source).map((file) => file.slice(source.length));
  assert.deepEqual(filesIn(assets).map((file) => file.slice(assets.length)), sourceFiles);
  // Read independent pairs together without opening the entire site at once.
  for (let offset = 0; offset < sourceFiles.length; offset += 32) {
    await Promise.all(sourceFiles.slice(offset, offset + 32).map(async (file) => {
      const [published, checked] = await Promise.all([readFile(`${assets}${file}`), readFile(`${source}${file}`)]);
      assert.deepEqual(published, checked, file);
    }));
  }
});

test('preview publication preserves site bytes without claiming a production domain', async () => {
  const root = await mkdtemp(join(tmpdir(), 'docs-preview-'));
  try {
    await mkdir(`${root}/docs/.vitepress/dist`, { recursive: true });
    const html = '<!doctype html><html><head><meta charset="utf-8"></head><body><main>Preview</main></body></html>';
    await writeFile(`${root}/docs/.vitepress/dist/index.html`, html);
    execFileSync(process.execPath, [fileURLToPath(new URL('./build-worker.mjs', import.meta.url))], {
      cwd: root, env: { ...process.env, CLOUDFLARE_PREVIEW_BUILD: 'true' },
    });
    const { rootConfig, workers } = await readBuildOutput(root);
    assert.equal(rootConfig.buildContext.isPreview, true);
    assert.equal(workers.default.config.domains, undefined);
    assert.equal(await readFile(`${workers.default.assetsDir}/index.html`, 'utf8'), secureHtml(html));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
