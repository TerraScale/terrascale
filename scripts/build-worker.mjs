import { execFileSync } from 'node:child_process';
import { cp, mkdir, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { secureHtml } from './security-headers.mjs';
import config from '../cloudflare.config.ts';

for (const entry of await readdir('docs/.vitepress/dist', { recursive: true })) {
  if (!entry.endsWith('.html')) continue;
  const file = `docs/.vitepress/dist/${entry}`;
  await writeFile(file, secureHtml(await readFile(file, 'utf8')));
}

// Public revision metadata travels with the same static assets as the site.
await writeFile('docs/.vitepress/dist/build-info.json', JSON.stringify({
  commit: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: new URL('..', import.meta.url), encoding: 'utf8' }).trim(),
  revision: 1,
}) + '\n');

// cf's v0 Build Output publishes the tested VitePress site without rebuilding it.
const output = '.cloudflare/output/v0';
// cf requires this platform build context to publish branch previews safely.
const isPreview = process.env.CLOUDFLARE_PREVIEW_BUILD === 'true';
// Cloudflare previews cannot claim the production custom domain.
const { domains, ...previewWorker } = config.worker;
await rm(output, { recursive: true, force: true });
await mkdir(`${output}/workers/default`, { recursive: true });
await writeFile(`${output}/config.json`, JSON.stringify({
  buildContext: { isPreview },
}));
await writeFile(`${output}/workers/default/worker.config.json`, JSON.stringify(isPreview ? previewWorker : config.worker));
await cp('docs/.vitepress/dist', `${output}/workers/default/assets`, { recursive: true });
