import { generateKeyPairSync, sign, type KeyObject } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { once } from 'node:events';
import { join } from 'node:path';
import { performance } from 'node:perf_hooks';
import { expect, type APIRequestContext, type APIResponse } from '@playwright/test';
import { test, json, name, project } from '../placement/support';
import { auth, stateDir } from '../support/stack';
import { keyId } from '../support/api-key';
import { journey, ownerSession, saveSecret } from '../support/state';
import { signInToken } from '../support/auth-management';
import { request as addressedRequest } from '../support/client-address';
import { node, peersReady, probe, restartAfterKill } from './support';
import { postgres, signingNode } from './signing-keys-support';

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
  const value = `${encoded}.${key ? sign(header.alg === 'RS512' ? 'sha512' : header.alg === 'ES256' ? 'sha256' : 'sha256', Buffer.from(encoded), { key, dsaEncoding: 'ieee-p1363' }).toString('base64url') : oldSignature}`;
  saveSecret('signing-keys-mutated-token', value);
  return value;
}

async function unavailable(response: APIResponse) {
  const text = await response.text();
  expect(response.status(), text).toBe(503);
  expect((JSON.parse(text) as { error: { code: string } }).error.code).toBe('token_keys_pending');
  expect(response.headers()['retry-after']).toBe('1');
}

async function accepts(request: APIRequestContext, url: string, token: string) {
  await expect.poll(async () => (await request.get(url, { headers: { authorization: `Bearer ${token}` } })).status(), {
    message: 'verification refetches signing keys and accepts the valid token',
  }).toBe(200);
}

test('a project token keeps working on the other gru peer while one peer is down, and the restarted peer verifies it again', async ({ request }) => {
  await peersReady();
  const token = await grant(request, true);
  const headers = { authorization: `Bearer ${token}` };
  const survivor = `http://127.0.0.1:${node('journey-3').port}${project()}/databases`;
  const restarted = `http://127.0.0.1:${node('journey-1').port}${project()}/databases`;
  expect((await request.get(survivor, { headers })).status()).toBe(200);
  const stopped = performance.now();
  const restart = restartAfterKill(node('journey-1'));
  try {
    await expect.poll(async () => {
      const response = await request.get(survivor, { headers });
      expect(response.status(), await response.text()).toBe(200);
      expect(response.headers()['x-terrabase-machine']).toBe('journey-3');
      const pid = Number(readFileSync(join(stateDir, 'pids/terrabase.pid'), 'utf8').trim());
      const exited = !existsSync(`/proc/${pid}/stat`) || readFileSync(`/proc/${pid}/stat`, 'utf8').split(') ')[1].startsWith('Z');
      return exited && performance.now() - stopped >= 30_000;
    }, { message: 'journey-1 stays down for thirty seconds while journey-3 keeps verifying the project token', timeout: 40_000, intervals: [1000] }).toBe(true);
  } finally {
    await restart();
  }
  await accepts(request, restarted, token);
  expect(await probe(node('journey-1'), '/readyz')).toBe(200);
  const home = await request.get(restarted, { headers });
  expect(home.status()).toBe(200);
  expect(home.headers()['x-terrabase-machine']).toBe('journey-1');
});

test('a malformed token is refused while signing keys are unreachable, and a valid token is retryable until the issuer returns', async ({ request }) => {
  const token = await grant(request);
  const isolated = await signingNode(request);
  const url = 'http://127.0.0.1:18780/v1/management/organizations';
  isolated.outage(true);
  try {
    await isolated.start();
    expect(existsSync(isolated.stored), 'v2 does not keep a local signing-key snapshot').toBe(false);
    expect((await request.get(url, { headers: { authorization: 'Bearer broken.token' } })).status()).toBe(401);
    const wrongIssuer = rewrite(token, {}, { iss: 'https://wrong.example' });
    expect((await request.get(url, { headers: { authorization: `Bearer ${wrongIssuer}` } })).status()).toBe(401);
    await unavailable(await request.get(url, { headers: { authorization: `Bearer ${token}` } }));
    isolated.outage(false);
    await accepts(request, url, token);
    await isolated.stop();
    await isolated.start();
    await accepts(request, url, token);
    expect(existsSync(isolated.stored)).toBe(false);
    expect(isolated.fetches()).toBeGreaterThan(0);
  } finally {
    await isolated.finish();
  }
});

test('a rotated signing key is accepted on the refresh that publishes it, including after the node restarts', async ({ request }) => {
  const token = await grant(request);
  const isolated = await signingNode(request);
  const url = 'http://127.0.0.1:18780/v1/management/organizations';
  const pair = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const key = { ...pair.publicKey.export({ format: 'jwk' }), kid: 'rotated-key', alg: 'ES256', use: 'sig' };
  const rotated = rewrite(token, { kid: key.kid, alg: 'ES256' }, {}, pair.privateKey);
  try {
    await isolated.start();
    await accepts(request, url, token);
    const before = isolated.fetches();
    isolated.keys([key]);
    await expect.poll(async () => (await request.get(url, { headers: { authorization: `Bearer ${rotated}` } })).status(), {
      message: 'an unknown key id refreshes the issuer document once and then verifies', timeout: 25_000, intervals: [500],
    }).toBe(200);
    expect(isolated.fetches()).toBeGreaterThan(before);
    await isolated.stop();
    const fetched = isolated.fetches();
    await isolated.start();
    await accepts(request, url, rotated);
    expect(isolated.fetches()).toBeGreaterThan(fetched);
    expect((await request.get(url, { headers: { authorization: `Bearer ${token}` } })).status()).toBe(200);
  } finally {
    await isolated.finish();
  }
});

test('an open SQL socket keeps its verified token when signing keys drop, and a new socket reports keys pending', async ({ request }) => {
  const token = await grant(request);
  const isolated = await signingNode(request);
  const root = 'http://127.0.0.1:18780';
  let socket: WebSocket | undefined;
  let fresh: WebSocket | undefined;
  try {
    await isolated.start();
    await accepts(request, `${root}/v1/management/organizations`, token);
    const organization = await json<{ id: string }>(await request.post(`${root}/v1/management/organizations`, {
      headers: { authorization: `Bearer ${token}` }, data: { name: name('signing-org') },
    }), 201);
    const createdProject = await json<{ id: string }>(await request.post(`${root}/v1/management/organizations/${organization.id}/projects`, {
      headers: { authorization: `Bearer ${token}` }, data: { name: name('signing-project') },
    }), 201);
    const database = await json<{ id: string }>(await request.post(`${root}/v1/projects/${createdProject.id}/databases`, {
      headers: { authorization: `Bearer ${token}` }, data: { name: name('signing-socket') },
    }), 201);
    socket = new WebSocket(`ws://127.0.0.1:18780/v1/projects/${createdProject.id}/databases/${database.id}/sql/ws`, 'terrascale.sql.v1');
    const messages: Record<string, unknown>[] = [];
    socket.addEventListener('message', event => messages.push(JSON.parse(String(event.data))));
    await once(socket, 'open');
    socket.send(JSON.stringify({ id: 1, type: 'auth', token }));
    await expect.poll(() => messages.length).toBe(1);
    expect(messages.shift()).toMatchObject({ id: 1, type: 'result' });
    socket.send(JSON.stringify({ id: 2, type: 'execute', statements: [{ sql: 'SELECT 1' }] }));
    await expect.poll(() => messages.length).toBe(1);
    expect(messages.shift()).toMatchObject({ id: 2, type: 'result', results: [{ rows: [[1]] }] });
    isolated.outage(true);
    socket.send(JSON.stringify({ id: 3, type: 'execute', statements: [{ sql: 'SELECT 1' }] }));
    await expect.poll(() => messages.length).toBe(1);
    expect(messages.shift()).toMatchObject({ id: 3, type: 'result', results: [{ rows: [[1]] }] });
    const pendingToken = rewrite(token, { kid: 'pending-key' }, {});
    const next = new WebSocket(`ws://127.0.0.1:18780/v1/projects/${createdProject.id}/databases/${database.id}/sql/ws`, 'terrascale.sql.v1');
    fresh = next;
    const freshMessages: Record<string, unknown>[] = [];
    next.addEventListener('message', event => freshMessages.push(JSON.parse(String(event.data))));
    const closed = new Promise<CloseEvent>(resolve => next.addEventListener('close', resolve, { once: true }));
    await once(next, 'open');
    next.send(JSON.stringify({ id: 1, type: 'auth', token: pendingToken }));
    await expect.poll(() => freshMessages.length).toBe(1);
    expect(freshMessages.shift()).toMatchObject({ id: 1, type: 'error', error: { code: 'token_keys_pending' } });
    expect((await closed).code).toBe(1008);
    expect(socket.readyState).toBe(WebSocket.OPEN);
  } finally {
    socket?.close();
    fresh?.close();
    await isolated.finish();
  }
});

test('PostgreSQL refuses a bad password with 28P01, reports 57P03 while signing keys are unreachable, and accepts the same token after restart', async ({ request }) => {
  const token = await grant(request);
  const isolated = await signingNode(request);
  const root = 'http://127.0.0.1:18780';
  const connections: Awaited<ReturnType<typeof postgres>>[] = [];
  try {
    await isolated.start();
    const organization = await json<{ id: string }>(await request.post(`${root}/v1/management/organizations`, {
      headers: { authorization: `Bearer ${token}` }, data: { name: name('signing-pg') },
    }), 201);
    const createdProject = await json<{ id: string }>(await request.post(`${root}/v1/management/organizations/${organization.id}/projects`, {
      headers: { authorization: `Bearer ${token}` }, data: { name: name('signing-pg') },
    }), 201);
    const database = await json<{ id: string }>(await request.post(`${root}/v1/projects/${createdProject.id}/databases`, {
      headers: { authorization: `Bearer ${token}` }, data: { name: name('signing-pg') },
    }), 201);
    const target = `${createdProject.id}/${database.id}`;
    const opened = await postgres(target, token);
    connections.push(opened);
    expect(opened.authenticated).toEqual([]);
    expect(await opened.query()).toEqual([]);
    const refused = await postgres(target, 'broken.token');
    connections.push(refused);
    expect(refused.authenticated).toEqual(['28P01']);
    isolated.outage(true);
    await isolated.stop();
    await isolated.start();
    const pending = await postgres(target, token);
    connections.push(pending);
    expect(pending.authenticated).toEqual(['57P03']);
    isolated.outage(false);
    await isolated.stop();
    await isolated.start();
    const again = await postgres(target, token);
    connections.push(again);
    expect(again.authenticated).toEqual([]);
    expect(await again.query()).toEqual([]);
  } finally {
    for (const connection of connections) connection.close();
    await isolated.finish();
  }
});
