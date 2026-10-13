import { cp, mkdir, rm, writeFile } from 'node:fs/promises';
import config from '../cloudflare.config.ts';

// cf's v0 Build Output publishes the tested VitePress site without rebuilding it.
const output = '.cloudflare/output/v0';
await rm(output, { recursive: true, force: true });
await mkdir(`${output}/workers/default`, { recursive: true });
await writeFile(`${output}/config.json`, JSON.stringify({
  // cf requires this platform build context to publish branch previews safely.
  buildContext: { isPreview: process.env.CLOUDFLARE_PREVIEW_BUILD === 'true' },
}));
await writeFile(`${output}/workers/default/worker.config.json`, JSON.stringify(config.worker));
await cp('docs/.vitepress/dist', `${output}/workers/default/assets`, { recursive: true });
