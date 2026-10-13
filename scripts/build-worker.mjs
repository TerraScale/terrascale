import { cp, mkdir, rm, writeFile } from 'node:fs/promises';
import config from '../cloudflare.config.ts';

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
