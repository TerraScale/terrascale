import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect } from '@playwright/test';
import { test, json, project } from '../placement/support';
import { signInToken } from '../support/auth-management';
import { auth, stateDir } from '../support/stack';
import { journey, ownerSession, saveSecret } from '../support/state';
import { nodes, restartAfterKill, topology } from './support';

test('project tokens recover after the first writer is down for thirty seconds', async ({ cells, request }) => {
  await topology();
  await json(await cells.get(`https://gru.api.terrascale.tech${project()}/databases`));
  const current = journey();
  const grant = await json<{ access_token: string }>(await request.post(`${auth}/terrascale/oauth/token`, {
    form: { grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey, project: current.projectId, scope: 'databases:read' },
  }));
  saveSecret('outage-token', grant.access_token);
  const responses: { status: number; retryAfter?: string; code?: string }[] = [];
  const recover = async () => {
    await expect.poll(async () => {
      const response = await request.get(`http://127.0.0.1:8080${project()}/databases`, {
        headers: { authorization: `Bearer ${grant.access_token}` },
      });
      const body = await response.json();
      responses.push({ status: response.status(), retryAfter: response.headers()['retry-after'], code: body.error?.code });
      return response.status();
    }, { message: 'valid project requests recover within twenty seconds', timeout: 20_000, intervals: [250, 500, 1000] }).toBe(200);
  };
  const stopped = Date.now();
  const restart = restartAfterKill(nodes[0], recover);
  try {
    await expect.poll(() => {
      const pid = Number(readFileSync(join(stateDir, 'pids', 'terrabase.pid'), 'utf8').trim());
      const exited = !existsSync(`/proc/${pid}/stat`) || readFileSync(`/proc/${pid}/stat`, 'utf8').split(') ')[1].startsWith('Z');
      return exited && Date.now() - stopped >= 30_000;
    }, { message: 'the first writer remains stopped for thirty seconds', timeout: 40_000, intervals: [1000] }).toBe(true);
  } finally {
    try {
      await restart();
    } finally {
      expect(responses.length).toBeGreaterThan(0);
      expect(responses.map(response => response.status), JSON.stringify(responses)).not.toContain(401);
      for (const response of responses.filter(response => response.status === 503)) {
        expect(response.retryAfter).toBeTruthy();
        expect(response.code).toBe('auth_unavailable');
      }
      console.log(`project token recovery: ${JSON.stringify(responses)}`);
    }
  }
});

test('stored issuer keys validate through an issuer outage and unknown keys remain retryable', async ({ playwright, request }) => {
  const signedIn = await playwright.request.newContext({ storageState: ownerSession() });
  const token = await signInToken(signedIn, 'stored-signing-keys');
  await signedIn.dispose();
  const url = 'http://127.0.0.1:8180/v1/management/organizations';
  const headers = { authorization: `Bearer ${token}` };
  expect((await request.get(url, { headers })).status()).toBe(200);
  const issuer = Number(readFileSync(join(stateDir, 'pids', 'ts-auth.pid'), 'utf8').trim());
  process.kill(issuer, 'SIGSTOP');
  try {
    await restartAfterKill(nodes[1])();
    expect((await request.get(url, { headers })).status()).toBe(200);
    const stored = join(stateDir, 'terrabase-fra', 'auth-signing-keys.json');
    const snapshot = JSON.parse(readFileSync(stored, 'utf8'));
    expect(snapshot.issuer).toBe(`${auth}/terrascale`);
    expect(snapshot.jwks.keys.length).toBeGreaterThan(0);
    for (const key of snapshot.jwks.keys) {
      for (const secret of ['d', 'p', 'q', 'dp', 'dq', 'qi', 'oth', 'k']) expect(key[secret]).toBeUndefined();
    }

    const [header, payload, signature] = token.split('.');
    const broken = Buffer.from(signature, 'base64url');
    broken[0] ^= 1;
    const invalid = `${header}.${payload}.${broken.toString('base64url')}`;
    saveSecret('invalid-signature-token', invalid);
    expect((await request.get(url, { headers: { authorization: `Bearer ${invalid}` } })).status()).toBe(401);
    const metrics = async () => (await (await request.get('http://127.0.0.2:9191/metrics')).text());
    const fetches = (text: string) => [...text.matchAll(/^terrabase_auth_key_fetches_total\{[^\n]*outcome="failure"[^\n]*\} (\d+)/gm)]
      .reduce((sum, value) => sum + Number(value[1]), 0);
    const before = fetches(await metrics());
    for (let index = 0; index < 4; index++) {
      const unknown = Buffer.from(JSON.stringify({ ...JSON.parse(Buffer.from(header, 'base64url').toString()), kid: `unknown-${index}` })).toString('base64url');
      const token = `${unknown}.${payload}.${signature}`;
      saveSecret(`unknown-key-token-${index}`, token);
      const response = await request.get(url, { headers: { authorization: `Bearer ${token}` } });
      expect(response.status()).toBe(503);
      expect((await response.json()).error.code).toBe('auth_unavailable');
      expect(response.headers()['retry-after']).toBe('1');
    }
    const after = fetches(await metrics());
    expect(after).toBeGreaterThanOrEqual(1);
    expect(after - before).toBeLessThanOrEqual(1);
    expect(await metrics()).toMatch(/^terrabase_auth_signing_keys_loaded(?:\{[^\n]*\})? 1$/m);
    expect(await metrics()).toMatch(/^terrabase_auth_signing_keys_age_seconds(?:\{[^\n]*\})? [\d.]+$/m);
    writeFileSync(stored, JSON.stringify({ ...snapshot, fetchedAt: new Date(Date.now() - 24 * 60 * 60 * 1000 - 1000).toISOString() }));
    try {
      await restartAfterKill(nodes[1])();
      const expired = await request.get(url, { headers });
      expect(expired.status()).toBe(503);
      expect((await expired.json()).error.code).toBe('auth_unavailable');
      expect(expired.headers()['retry-after']).toBe('1');
      expect(await metrics()).toMatch(/^terrabase_auth_signing_keys_loaded(?:\{[^\n]*\})? 0$/m);
    } finally {
      writeFileSync(stored, JSON.stringify(snapshot));
    }
    console.log('stored public keys: valid user 200, invalid signature 401, four unknown kids 503 with one shared bounded refresh');
  } finally {
    process.kill(issuer, 'SIGCONT');
  }
});
