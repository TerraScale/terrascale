import { appendFileSync, globSync, mkdirSync, writeFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { request as playwrightRequest, test as base, type BrowserContextOptions } from '@playwright/test';
import { e2eRoot, production, stateDir } from './stack';

export default function allocateClientRun(): void {
  if (production) return;
  const root = join(stateDir, 'client-runs');
  mkdirSync(root, { recursive: true });
  for (let run = 1; run < 192; run++) {
    try {
      mkdirSync(join(root, String(run)));
      process.env.E2E_CLIENT_RUN = String(run);
      writeFileSync(join(stateDir, `client-addresses-${run}.jsonl`), '');
      return;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'EEXIST') throw error;
    }
  }
  throw new Error('the stack has exhausted its client address networks');
}

export function clientProxy(): BrowserContextOptions['proxy'] {
  if (production) return undefined;
  const run = Number(process.env.E2E_CLIENT_RUN);
  if (!Number.isInteger(run) || run < 1 || run >= 192) throw new Error('client run was not allocated by global setup');
  const specs = globSync('**/*.spec.ts', { cwd: e2eRoot, exclude: ['node_modules/**'] }).sort();
  const spec = relative(e2eRoot, base.info().file);
  const index = (specs.indexOf(spec) + 1) * 256 + base.info().repeatEachIndex;
  if (specs.indexOf(spec) < 0 || index > 65535 || base.info().repeatEachIndex > 255) throw new Error(`client address missing for ${spec}`);
  const address = `127.${64 + run}.${index >> 8}.${index & 255}`;
  appendFileSync(join(stateDir, `client-addresses-${run}.jsonl`), JSON.stringify({ spec, address }) + '\n');
  return { server: `http://${address}:8089`, bypass: '<-loopback>' };
}

export const test = base.extend({
  proxy: async ({}, use) => { await use(clientProxy()); },
});

export const request = {
  newContext: (options: Parameters<typeof playwrightRequest.newContext>[0] = {}) =>
    playwrightRequest.newContext({ ...options, proxy: clientProxy() }),
};
