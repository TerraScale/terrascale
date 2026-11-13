import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { closeSync, mkdtempSync, openSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect } from '@playwright/test';
import { stateDir } from '../support/stack';

export type RegionProcess = { region: string; endpoint: string; stop(): Promise<void> };

export async function startRegion(region: string, index: number): Promise<RegionProcess> {
  const source = readFileSync(join(stateDir, 'pids', 'terrabase.pid'), 'utf8').trim();
  const environment = Object.fromEntries(readFileSync(`/proc/${source}/environ`, 'utf8').split('\0').filter(Boolean).map(value => {
    const split = value.indexOf('=');
    return [value.slice(0, split), value.slice(split + 1)];
  }));
  const directory = mkdtempSync(join(stateDir, `region-${region}-`));
  const port = 8380 + index * 100;
  const endpoint = `http://127.0.0.1:${port}`;
  const log = join(directory, 'node.log');
  const output = openSync(log, 'w', 0o600);
  const node = spawn('nice', ['-n', '19', 'dotnet', join(process.env.E2E_BUILD!, 'terrabase', 'terrabase.dll')], {
    env: {
      ...environment, TS_REGION: region, TS_NODE: `journey-extra-${region}`,
      TERRABASE_DATA_DIRECTORY: join(directory, 'data'),
      TERRABASE_PORT: String(port), TERRABASE_METRICS_PORT: String(9391 + index * 100),
      TERRABASE_POSTGRES_PORT: String(6732 + index * 100),
    },
    stdio: ['ignore', output, output],
  });
  closeSync(output);
  const exited = once(node, 'exit');
  const stop = async () => {
    if (node.exitCode === null) node.kill('SIGTERM');
    await exited;
  };
  try {
    await expect.poll(() => {
      expect(node.exitCode, `${region} stays running`).toBeNull();
      return readFileSync(log, 'utf8').includes('Application started.');
    }, { message: `${region} opens its real listeners` }).toBe(true);
    return { region, endpoint, stop };
  } catch (error) {
    await stop();
    throw error;
  }
}

export function extendPeers(regions: string[]): () => void {
  const path = join(stateDir, 'peers');
  const original = readFileSync(path, 'utf8');
  writeFileSync(path, original + regions.map((region, index) => `journey-extra-${region} ${region} 127.0.0.${5 + index}\n`).join(''));
  return () => writeFileSync(path, original);
}
