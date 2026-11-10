import { readFileSync } from 'node:fs';
import { join } from 'node:path';

export const e2eRoot = join(__dirname, '..');
export const stateDir = process.env.E2E_STATE ?? join(e2eRoot, '.state');
export const dash = 'https://dash.terrascale.tech';
export const auth = 'https://auth.terrascale.tech';
export const mailpit = 'http://127.0.0.1:8025';
export const cloudflareSink = 'http://127.0.0.1:8026';
export const browserHome = join(stateDir, 'pki', 'browser-home');
export const userSession = join(stateDir, 'user.json');
export const stackHosts: ReadonlySet<string> = new Set(
  readFileSync(join(e2eRoot, 'env', 'hostnames'), 'utf8').split('\n').filter(Boolean),
);
export const desktop = { width: 1440, height: 900 };
export const mobile = { width: 390, height: 844 };
