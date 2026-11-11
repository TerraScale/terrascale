import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const target = process.env.E2E_TARGET ?? 'local';
if (target !== 'local' && target !== 'production') throw new Error(`E2E_TARGET is local or production, not ${target}`);

export const production = target === 'production';
export const e2eRoot = join(__dirname, '..');
export const stateDir = process.env.E2E_STATE ?? join(e2eRoot, '.state');
export const dash = 'https://dash.terrascale.tech';
export const auth = 'https://auth.terrascale.tech';
export const mailpit = 'http://127.0.0.1:8025';
export const cloudflareSink = 'http://127.0.0.1:8026';
export const stripeStandIn = 'http://127.0.0.1:8027';
export const mailWait = production ? 120_000 : 30_000;
export const browserHome = production ? join(stateDir, 'browser-home') : join(stateDir, 'pki', 'browser-home');
export const sessionFile = (setup: string) => join(stateDir, `${setup}.session.json`);
const hostnames = readFileSync(join(e2eRoot, 'env', 'hostnames'), 'utf8').split('\n').filter(Boolean);
export const stackHosts: ReadonlySet<string> = new Set(production ? hostnames.filter((host) => host.endsWith('.terrascale.tech')) : hostnames);
export const desktop = { width: 1440, height: 900 };
export const mobile = { width: 390, height: 844 };

export async function onLocalStack(step: () => Promise<void>): Promise<void> {
  if (!production) await step();
}
