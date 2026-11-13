import { generateKeyPairSync, randomBytes, sign, type KeyObject } from 'node:crypto';
import { existsSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { once } from 'node:events';
import { join } from 'node:path';
import { performance } from 'node:perf_hooks';
import { expect, type APIRequestContext, type APIResponse } from '@playwright/test';
import { test, json, project } from '../placement/support';
import { auth, stateDir } from '../support/stack';
import { keyId } from '../support/api-key';
import { journey, ownerSession, saveSecret } from '../support/state';
import { signInToken } from '../support/auth-management';
import { request as addressedRequest } from '../support/client-address';
import { nodes, restartAfterKill, topology } from './support';
import { postgres, signingNode } from './signing-keys-support';

const url = 'http://127.0.0.1:18780/v1/management/organizations';
const hours = 24 * 60 * 60 * 1000;
type Snapshot = { version: number; issuer: string; fetchedAt: string; lastKnownTime: string; jwks: { keys: Record<string, unknown>[] } };

async function grant(request: APIRequestContext, projectToken = false) {
  if (!projectToken) {
    const context = await addressedRequest.newContext({ storageState: ownerSession() });
    try {
      return await signInToken(context, 'signing-keys-user');
    } finally {
      await context.dispose();
    }
  }

  const current = journey();
  const value = await json<{ access_token: string }>(await request.post(`${auth}/terrascale/oauth/token`, {
    form: { grant_type: 'client_credentials', client_id: keyId(current.apiKey), client_secret: current.apiKey,
      project: current.projectId, scope: 'sql:read sql:write databases:read databases:write' },
  }));
  saveSecret('signing-keys-project-token', value.access_token);
  return value.access_token;
}

function rewrite(token: string, header: Record<string, unknown>, payload: Record<string, unknown>, key?: KeyObject) {
  const [oldHeader, oldPayload, oldSignature] = token.split('.');
  const encoded = `${Buffer.from(JSON.stringify({ ...JSON.parse(Buffer.from(oldHeader, 'base64url').toString()), ...header })).toString('base64url')}.${Buffer.from(JSON.stringify({ ...JSON.parse(Buffer.from(oldPayload, 'base64url').toString()), ...payload })).toString('base64url')}`;
  const value = `${encoded}.${key ? sign(header.alg === 'RS512' ? 'sha512' : 'sha256', Buffer.from(encoded), { key, dsaEncoding: 'ieee-p1363' }).toString('base64url') : oldSignature}`;
  saveSecret('signing-keys-mutated-token', value);
  return value;
}

async function unavailable(response: APIResponse) {
  expect(response.status(), await response.text()).toBe(503);
  expect((await response.json()).error.code).toBe('auth_unavailable');
  expect(response.headers()['retry-after']).toBe('1');
}

async function accepts(request: APIRequestContext, token: string) {
  await expect.poll(async () => (await request.get(url, { headers: { authorization: `Bearer ${token}` } })).status(), {
    message: 'verification recovers and accepts the valid token',
  }).toBe(200);
}

test('project requests survive a thirty-second first-writer outage without misclassifying unavailable authentication', async ({ cells, request }) => {
  await topology();
  await json(await cells.get(`https://gru.api.terrascale.tech${project()}/databases`));
  const token = await grant(request, true);
  const headers = { authorization: `Bearer ${token}` };
  const endpoint = `http://127.0.0.1:8080${project()}/databases`;
  const issuer = Number(readFileSync(join(stateDir, 'pids/ts-auth.pid'), 'utf8').trim());
  let stoppedIssuer = false;
  const responses: number[] = [];
  const retried: { retryAfter?: string; code?: string }[] = [];
  const recover = async () => {
    try {
      const response = await request.get(endpoint, { headers });
      responses.push(response.status());
      await unavailable(response);
    } finally {
      process.kill(issuer, 'SIGCONT');
      stoppedIssuer = false;
    }
    await expect.poll(async () => {
      const response = await request.get(endpoint, { headers });
      responses.push(response.status());
      if (response.status() === 503) {
        const observed = { retryAfter: response.headers()['retry-after'], code: undefined as string | undefined };
        retried.push(observed);
        observed.code = (await response.json()).error?.code;
      }
      return response.status();
    }, { message: 'the valid project request recovers after the issuer resumes' }).toBe(200);
    for (let index = 0; index < 12; index++) {
      const response = await request.get(endpoint, { headers });
      responses.push(response.status());
      expect(response.status()).toBe(200);
    }
  };
  const stopped = performance.now();
  const restart = restartAfterKill(nodes[0], true, undefined, recover);
  try {
    await expect.poll(() => {
      const pid = Number(readFileSync(join(stateDir, 'pids/terrabase.pid'), 'utf8').trim());
      const exited = !existsSync(`/proc/${pid}/stat`) || readFileSync(`/proc/${pid}/stat`, 'utf8').split(') ')[1].startsWith('Z');
      return exited && performance.now() - stopped >= 30_000;
    }, { message: 'the first writer remains stopped for thirty seconds', timeout: 40_000, intervals: [1000] }).toBe(true);
    process.kill(issuer, 'SIGSTOP');
    stoppedIssuer = true;
    rmSync(join(stateDir, 'terrabase/auth-signing-keys.json'));
  } finally {
    try {
      await restart();
    } finally {
      if (stoppedIssuer) process.kill(issuer, 'SIGCONT');
    }
  }
  expect(responses).toContain(503);
  for (const response of responses) expect([200, 503]).toContain(response);
  for (const response of retried) expect(response).toEqual({ retryAfter: '1', code: 'auth_unavailable' });
});

test('every outage answer is valid or retryable and recovery stays healthy', async ({ request }) => {
  const token = await grant(request);
  const node = await signingNode(request);
  node.outage(true);
  const responses: { status: number; retryAfter?: string; code?: string }[] = [];
  try {
    await node.start();
    const before = await node.metrics();
    const failures = (value: string) => [...value.matchAll(/^terrabase_auth_key_fetches_total\{[^\n]*outcome="failure"[^\n]*\} (\d+)/gm)].reduce((sum, match) => sum + Number(match[1]), 0);
    const begun = performance.now();
    await expect.poll(async () => {
      const response = await request.get(url, { headers: { authorization: `Bearer ${token}` } });
      const observed = { status: response.status(), retryAfter: response.headers()['retry-after'], code: undefined as string | undefined };
      responses.push(observed);
      if (response.status() === 503) observed.code = (await response.json()).error?.code;
      return performance.now() - begun;
    }, { message: 'all requests during the thirty-second outage obey the contract', timeout: 40_000, intervals: [1000] }).toBeGreaterThanOrEqual(30_000);
    expect(responses.map(value => value.status), 'the unavailable path must be exercised').toContain(503);
    for (const response of responses) {
      expect([200, 503]).toContain(response.status);
      if (response.status === 503) {
        expect(response.retryAfter).toBe('1');
        expect(response.code).toBe('auth_unavailable');
      }
    }
    const metrics = await node.metrics();
    expect(failures(metrics)).toBeGreaterThan(failures(before));
    expect(metrics).toMatch(/^terrabase_auth_key_fetches_total\{[^\n]*outcome="failure"[^\n]*\} [1-9]\d*$/m);
    node.outage(false);
    await accepts(request, token);
    const healthy: number[] = [];
    await expect.poll(async () => {
      const index = healthy.push(0) - 1;
      healthy[index] = (await request.get(url, { headers: { authorization: `Bearer ${token}` } })).status();
      return healthy.length;
    }, { message: 'twelve consecutive healthy responses over at least five seconds', intervals: [500] }).toBe(12);
    expect(healthy).toEqual(Array.from({ length: 12 }, () => 200));
    console.log(`outage responses: ${JSON.stringify(responses)}; sustained recovery: twelve 200s`);
  } finally {
    await node.finish();
  }
});

const badFiles: [string, (snapshot: Snapshot) => string | Buffer][] = [
  ['oversized', () => Buffer.alloc(2 * 1024 * 1024 + 1, 32)],
  ['truncated', () => '{"version":'],
  ['wrong version', snapshot => JSON.stringify({ ...snapshot, version: 99 })],
  ['other issuer', snapshot => JSON.stringify({ ...snapshot, issuer: 'https://other.example/terrascale' })],
  ['zero keys', snapshot => JSON.stringify({ ...snapshot, jwks: { keys: [] } })],
  ['no convertible keys', snapshot => JSON.stringify({ ...snapshot, jwks: { keys: [{ kty: 'RSA', use: 'sig', n: '!', e: 'AQAB' }] } })],
  ['one bad signing entry', snapshot => JSON.stringify({ ...snapshot, jwks: { keys: [...snapshot.jwks.keys, { kty: 'RSA', kid: 'broken', use: 'sig', n: '!', e: 'AQAB' }] } })],
  ['symmetric signing key', snapshot => JSON.stringify({ ...snapshot, jwks: { keys: [...snapshot.jwks.keys, { kty: 'oct', kid: 'symmetric', use: 'sig', k: 'c2VjcmV0' }] } })],
  ['empty key operations', snapshot => JSON.stringify({ ...snapshot, jwks: { keys: snapshot.jwks.keys.map(key => ({ ...key, key_ops: [] })) } })],
];

for (const [label, contents] of badFiles) {
  test(`a ${label} stored file cannot prevent startup or retain partial trust`, async ({ request }) => {
    const token = await grant(request);
    const node = await signingNode(request);
    try {
      await node.start();
      await accepts(request, token);
      await expect.poll(() => node.loaded()).toBe(1);
      const snapshot = JSON.parse(readFileSync(node.stored, 'utf8')) as Snapshot;
      for (const key of snapshot.jwks.keys) {
        for (const secret of ['d', 'p', 'q', 'dp', 'dq', 'qi', 'oth', 'k']) expect(key[secret]).toBeUndefined();
      }
      await node.stop();
      writeFileSync(node.stored, contents(snapshot));
      node.outage(true);
      await node.start();
      await expect.poll(() => node.rejected(), { message: 'the whole bad snapshot is rejected once' }).toBe(1);
      expect(await node.loaded()).toBe(0);
      await unavailable(await request.get(url, { headers: { authorization: `Bearer ${token}` } }));
      node.outage(false);
      await accepts(request, token);
      expect(await node.loaded()).toBe(1);
    } finally {
      await node.finish();
    }
  });
}

for (const reason of ['fetch time', 'high-water mark']) {
  test(`stored keys are rejected when the clock goes back before their ${reason}`, async ({ request }) => {
    const token = await grant(request);
    const node = await signingNode(request);
    try {
      await node.start();
      await accepts(request, token);
      const snapshot = JSON.parse(readFileSync(node.stored, 'utf8')) as Snapshot;
      await node.stop();
      const future = new Date(Date.now() + 60_000).toISOString();
      writeFileSync(node.stored, JSON.stringify({ ...snapshot,
        fetchedAt: reason === 'fetch time' ? future : new Date(Date.now() - 60_000).toISOString(), lastKnownTime: future }));
      node.outage(true);
      await node.start();
      await expect.poll(() => node.rejected('clock_backwards')).toBe(1);
      await unavailable(await request.get(url, { headers: { authorization: `Bearer ${token}` } }));
    } finally {
      await node.finish();
    }
  });
}

test('elapsed allowance expires across two restarts and a backward wall step cannot extend a loaded snapshot', async ({ request }) => {
  const token = await grant(request);
  const node = await signingNode(request);
  try {
    await node.start();
    await accepts(request, token);
    const snapshot = JSON.parse(readFileSync(node.stored, 'utf8')) as Snapshot;
    await node.stop();
    writeFileSync(node.stored, JSON.stringify({ ...snapshot, fetchedAt: new Date(Date.now() - hours + 8000).toISOString(), lastKnownTime: new Date().toISOString() }));
    node.outage(true);
    await node.start();
    await expect.poll(() => node.loaded()).toBe(1);
    await node.stop();
    await node.start();
    await expect.poll(() => node.loaded()).toBe(1);
    node.setClock(-60_000);
    await expect.poll(() => node.loaded(), { message: 'monotonic expiry survives the backwards wall step' }).toBe(0);
    node.setClock(0);
    await node.stop();
    await node.start();
    await expect.poll(() => node.rejected('expired')).toBe(1);
    await unavailable(await request.get(url, { headers: { authorization: `Bearer ${token}` } }));
  } finally {
    await node.finish();
  }
});

test('malformed, expired and other independently invalid tokens are refused before unavailable keys', async ({ request }) => {
  const token = await grant(request);
  const node = await signingNode(request);
  node.outage(true);
  try {
    await node.start();
    for (const invalid of ['broken.token', rewrite(token, {}, { exp: 'invalid' }), rewrite(token, {}, { nbf: {} }),
      rewrite(token, {}, { exp: Math.floor(Date.now() / 1000) - 1 }),
      rewrite(token, {}, { nbf: Math.floor(Date.now() / 1000) + 300 }), rewrite(token, {}, { iss: 'https://wrong.example' }),
      rewrite(token, {}, { aud: 'wrong-audience' }), rewrite(token, { alg: 'none' }, {})]) {
      saveSecret('independently-invalid-token', invalid);
      expect((await request.get(url, { headers: { authorization: `Bearer ${invalid}` } })).status()).toBe(401);
    }
    await unavailable(await request.get(url, { headers: { authorization: `Bearer ${token}` } }));
  } finally {
    await node.finish();
  }
});

test('declared algorithms and verification operations constrain keys from the issuer and disk', async ({ request }) => {
  const token = await grant(request);
  const pair = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const key = { ...pair.publicKey.export({ format: 'jwk' }), kid: 'declared-rs256', alg: 'RS256', use: 'sig', key_ops: ['verify'] };
  const node = await signingNode(request);
  node.keys([key]);
  const payload = { project_id: undefined, org_id: undefined, database: undefined, key_id: undefined, key_gen: undefined };
  const matching = rewrite(token, { alg: 'RS256', kid: key.kid }, payload, pair.privateKey);
  const mismatched = rewrite(token, { alg: 'RS512', kid: key.kid }, payload, pair.privateKey);
  try {
    await node.start();
    await accepts(request, matching);
    expect((await request.get(url, { headers: { authorization: `Bearer ${mismatched}` } })).status()).toBe(401);
    await node.stop();
    node.outage(true);
    await node.start();
    await accepts(request, matching);
    expect((await request.get(url, { headers: { authorization: `Bearer ${mismatched}` } })).status()).toBe(401);
    const [head, body, signature] = matching.split('.');
    const corrupted = Buffer.from(signature, 'base64url');
    corrupted[0] ^= 1;
    const invalid = `${head}.${body}.${corrupted.toString('base64url')}`;
    saveSecret('stored-key-invalid-signature', invalid);
    expect((await request.get(url, { headers: { authorization: `Bearer ${invalid}` } })).status()).toBe(401);
    const failures = (value: string) => [...value.matchAll(/^terrabase_auth_key_fetches_total\{[^\n]*outcome="failure"[^\n]*\} (\d+)/gm)].reduce((sum, match) => sum + Number(match[1]), 0);
    const before = failures(await node.metrics());
    for (let index = 0; index < 2; index++) {
      const unknown = rewrite(matching, { kid: `unavailable-${index}` }, {});
      await unavailable(await request.get(url, { headers: { authorization: `Bearer ${unknown}` } }));
    }
    const additional = failures(await node.metrics()) - before;
    expect(additional).toBeGreaterThanOrEqual(1);
    expect(additional).toBeLessThanOrEqual(2);
    await node.stop();
    const snapshot = JSON.parse(readFileSync(node.stored, 'utf8')) as Snapshot;
    writeFileSync(node.stored, JSON.stringify({ ...snapshot, jwks: { keys: snapshot.jwks.keys.map(value => ({ ...value, key_ops: ['sign'] })) } }));
    await node.start();
    await expect.poll(() => node.rejected()).toBe(1);
    await unavailable(await request.get(url, { headers: { authorization: `Bearer ${matching}` } }));
    const discovery = await json<{ jwks_uri: string }>(await request.get(`${auth}/terrascale/.well-known/openid-configuration`));
    const published = await json<{ keys: Record<string, unknown>[] }>(await request.get(discovery.jwks_uri));
    node.keys(published.keys, true);
    await node.stop();
    node.outage(false);
    await node.start();
    await accepts(request, token);
    expect((await request.get(url, { headers: { authorization: `Bearer ${matching}` } })).status()).toBe(401);
    const smaller = JSON.parse(readFileSync(node.stored, 'utf8')) as Snapshot;
    expect(smaller.jwks.keys).toHaveLength(published.keys.length);
    expect(smaller.jwks.keys.length).toBeLessThan(snapshot.jwks.keys.length);
  } finally {
    await node.finish();
  }
});

test('one follow-up refresh admits a rotated key within twenty-five seconds under random key-id traffic', async ({ request }) => {
  const token = await grant(request);
  const node = await signingNode(request);
  const pair = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const key = { ...pair.publicKey.export({ format: 'jwk' }), kid: 'rotated-key', alg: 'ES256', use: 'sig' };
  const rotated = rewrite(token, { kid: key.kid, alg: 'ES256' }, {}, pair.privateKey);
  try {
    await node.start();
    await accepts(request, token);
    node.hold();
    const unknown = rewrite(token, { kid: randomBytes(8).toString('hex') }, {});
    const first = request.get(url, { headers: { authorization: `Bearer ${unknown}` } });
    await expect.poll(() => node.held()).toBe(true);
    node.keys([key]);
    const before = node.fetches();
    const begun = performance.now();
    const arriving = request.get(url, { headers: { authorization: `Bearer ${rotated}` } });
    node.release();
    const attacks: Promise<APIResponse>[] = [];
    const traffic = setInterval(() => {
      const attack = rewrite(token, { kid: randomBytes(8).toString('hex') }, {});
      attacks.push(request.get(url, { headers: { authorization: `Bearer ${attack}` } }));
    }, 3000);
    try {
      await expect.poll(async () => (await request.get(url, { headers: { authorization: `Bearer ${rotated}` } })).status(), {
        message: 'the rotated key cannot be starved by new attacker ids', timeout: 25_000, intervals: [500],
      }).toBe(200);
    } finally {
      clearInterval(traffic);
    }
    expect(attacks.length).toBeGreaterThanOrEqual(3);
    for (const response of await Promise.all(attacks)) {
      expect([401, 503]).toContain(response.status());
      if (response.status() === 503) await unavailable(response);
    }
    expect(performance.now() - begun).toBeLessThan(25_000);
    expect((await arriving).status()).toBe(200);
    expect([401, 503]).toContain((await first).status());
    expect(node.fetches() - before).toBe(1);
    for (let index = 0; index < 5; index++) expect((await request.get(url, { headers: { authorization: `Bearer ${rotated}` } })).status()).toBe(200);
  } finally {
    await node.finish();
  }
});

test('a WebSocket survives unavailable verification and closes at its remembered token expiry', async ({ request }) => {
  const token = await grant(request, true);
  const current = journey();
  const pair = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const key = { ...pair.publicKey.export({ format: 'jwk' }), kid: 'socket-expiry', alg: 'ES256', use: 'sig' };
  const node = await signingNode(request);
  node.keys([key]);
  let socket: WebSocket | undefined;
  try {
    await node.start();
    await accepts(request, await grant(request));
    const created = await json<{ id: string }>(await request.post(`http://127.0.0.1:18780/v1/projects/${current.projectId}/databases`, {
      headers: { authorization: `Bearer ${token}` }, data: { name: 'signing-socket', allRegions: true },
    }), 201);
    const snapshot = JSON.parse(readFileSync(node.stored, 'utf8')) as Snapshot;
    await node.stop();
    writeFileSync(node.stored, JSON.stringify({ ...snapshot, fetchedAt: new Date(Date.now() - hours + 9000).toISOString(), lastKnownTime: new Date().toISOString() }));
    node.outage(true);
    await node.start();
    await expect.poll(() => node.loaded()).toBe(1);
    const expiry = Math.floor(Date.now() / 1000) + 16;
    const short = rewrite(token, { kid: key.kid }, { exp: expiry, key_id: undefined, key_gen: undefined }, pair.privateKey);
    socket = new WebSocket(`ws://127.0.0.1:18780/v1/projects/${current.projectId}/databases/${created.id}/sql/ws`, 'terrascale.sql.v1');
    const messages: Record<string, unknown>[] = [];
    socket.addEventListener('message', event => messages.push(JSON.parse(String(event.data))));
    const closed = new Promise<CloseEvent>(resolve => socket!.addEventListener('close', resolve, { once: true }));
    await once(socket, 'open');
    socket.send(JSON.stringify({ id: 1, type: 'auth', token: short }));
    await expect.poll(() => messages.length).toBe(1);
    expect(messages.shift()).toMatchObject({ id: 1, type: 'result' });
    socket.send(JSON.stringify({ id: 2, type: 'execute', statements: [{ sql: 'SELECT 1' }] }));
    await expect.poll(() => messages.length).toBe(1);
    expect(messages.shift()).toMatchObject({ id: 2, type: 'result', results: [{ rows: [[1]] }] });
    await expect.poll(() => node.loaded()).toBe(0);
    socket.send(JSON.stringify({ id: 3, type: 'execute', statements: [{ sql: 'SELECT 1' }] }));
    await expect.poll(() => messages.length).toBe(1);
    expect(messages.shift()).toMatchObject({ id: 3, type: 'error', error: { code: 'unavailable' } });
    expect(socket.readyState).toBe(WebSocket.OPEN);
    await expect.poll(() => socket!.readyState, { message: 'expiry closes the idle session even while keys remain unavailable' }).toBe(WebSocket.CLOSED);
    expect((await closed).code).toBe(1008);
    expect(Date.now()).toBeLessThan((expiry + 3) * 1000);
    expect(await node.loaded()).toBe(0);
  } finally {
    socket?.close();
    await node.finish();
  }
});

test('PostgreSQL preserves 57P03 on renewal and pending Execute, refuses credentials with 28P01, and expires sessions', async ({ request }) => {
  const token = await grant(request, true);
  const current = journey();
  const pair = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const key = { ...pair.publicKey.export({ format: 'jwk' }), kid: 'postgres-expiry', alg: 'ES256', use: 'sig' };
  const node = await signingNode(request);
  node.keys([key]);
  const connections: Awaited<ReturnType<typeof postgres>>[] = [];
  try {
    await node.start();
    await accepts(request, await grant(request));
    const created = await json<{ id: string }>(await request.post(`http://127.0.0.1:18780/v1/projects/${current.projectId}/databases`, {
      headers: { authorization: `Bearer ${token}` }, data: { name: 'signing-postgres', allRegions: true },
    }), 201);
    const expiry = Math.floor(Date.now() / 1000) + 22;
    node.exchange(value => rewrite(value, { kid: key.kid }, { exp: expiry, key_id: undefined, key_gen: undefined }, pair.privateKey));
    const simple = await postgres(`${current.projectId}/${created.id}`, current.apiKey);
    const pending = await postgres(`${current.projectId}/${created.id}`, current.apiKey);
    connections.push(simple, pending);
    expect(simple.authenticated).toEqual([]);
    expect(pending.authenticated).toEqual([]);
    expect(await simple.query()).toEqual([]);
    expect(await pending.prepare()).toEqual([]);
    node.outage(true);
    await expect.poll(() => Date.now(), { message: 'the remembered grant reaches its ten-second renewal margin', intervals: [500] }).toBeGreaterThanOrEqual((expiry - 9) * 1000);
    expect(await simple.query()).toEqual(['57P03']);
    expect(await pending.execute()).toEqual(['57P03']);
    expect(simple.ended()).toBe(false);
    node.outage(false);
    const refused = await postgres(`${current.projectId}/${created.id}`, current.apiKey.slice(0, -4) + 'AAAA');
    connections.push(refused);
    expect(refused.authenticated).toEqual(['28P01']);
    node.outage(true);
    await expect.poll(() => simple.ended(), { message: 'a known token expiry ends PostgreSQL even during unavailable renewal' }).toBe(true);
    expect(Date.now()).toBeLessThan((expiry + 3) * 1000);
    await node.stop();
    const snapshot = JSON.parse(readFileSync(node.stored, 'utf8')) as Snapshot;
    writeFileSync(node.stored, JSON.stringify({ ...snapshot, fetchedAt: new Date(Date.now() - hours - 1000).toISOString() }));
    node.outage(false);
    node.keyOutage(true);
    await node.start();
    await expect.poll(() => node.rejected('expired')).toBe(1);
    for (const transform of [() => 'broken.token', (value: string) => rewrite(value, {}, { exp: Math.floor(Date.now() / 1000) - 1 })]) {
      node.exchange(transform);
      const invalid = await postgres(`${current.projectId}/${created.id}`, current.apiKey);
      connections.push(invalid);
      expect(invalid.authenticated).toEqual(['28P01']);
    }
    node.exchange();
    const couldBeValid = await postgres(`${current.projectId}/${created.id}`, current.apiKey);
    connections.push(couldBeValid);
    expect(couldBeValid.authenticated).toEqual(['57P03']);
  } finally {
    for (const connection of connections) connection.close();
    await node.finish();
  }
});

test('a malformed or secret issuer signing entry fails the entire fetch, an entry the node does not understand is skipped', async ({ request }) => {
  const token = await grant(request);
  const node = await signingNode(request);
  const stored = () => JSON.parse(readFileSync(node.stored, 'utf8')) as Snapshot;
  try {
    await node.start();
    await accepts(request, token);
    const published = stored().jwks.keys[0];
    for (const bad of [{ kty: 'RSA', use: 'sig', n: '!', e: 'AQAB' }, { kty: 'oct', use: 'sig', k: 'c2VjcmV0' }]) {
      const before = node.fetches();
      const fetched = stored().fetchedAt;
      node.keys([bad]);
      await node.stop();
      await node.start();
      await expect.poll(() => node.fetches()).toBeGreaterThan(before);
      await expect.poll(async () => (await node.metrics()).includes('outcome="failure"')).toBe(true);
      await accepts(request, token);
      expect(await node.loaded()).toBe(1);
      expect(stored().fetchedAt, 'a failed fetch does not renew the stored set').toBe(fetched);
    }
    for (const skipped of [{ kty: 'OKP', crv: 'Ed25519', kid: 'future-type', alg: 'EdDSA', use: 'sig', x: '11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo' },
      { ...published, kid: 'future-algorithm', alg: 'ES3072' }, { ...published, kid: 'other-operation', key_ops: ['sign'] }]) {
      const fetched = stored().fetchedAt;
      node.keys([skipped]);
      await node.stop();
      await node.start();
      await expect.poll(() => stored().fetchedAt, { message: `a set with ${skipped.kid} still refreshes` }).not.toBe(fetched);
      await accepts(request, token);
      expect(await node.loaded()).toBe(1);
      expect(stored().jwks.keys.map(key => key.kid), `${skipped.kid} is not stored`).not.toContain(skipped.kid);
    }
    node.keys([], true);
    await node.stop();
    await node.start();
    await expect.poll(async () => (await node.metrics()).includes('outcome="failure"')).toBe(true);
    await accepts(request, token);
  } finally {
    await node.finish();
  }
});
