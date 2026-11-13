import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { once } from 'node:events';
import { expect } from '@playwright/test';
import { rawSqlSocket, frame, scheduleStatement } from '../support/sql-socket';
import { holdPeerStream, holdPeerReplies } from '../support/peer-stream-fault';
import { postgres, stalledPostgres, message } from '../support/postgres-wire';
import { auditedContext, Audit } from '../support/audit';
import { newAddress } from '../support/inbox';
import { signUp, verify, onboard, confirmWithPassword } from '../support/hosted';
import { createApiKey } from '../support/paid';
import { production, stateDir } from '../support/stack';
import { journey, saveSecret, saveCookies } from '../support/state';
import { test, home, project, database, name, json, removeDatabase, authenticatedCells, sql, replicated, replica, type Database } from './support';

test.describe.configure({ mode: 'serial' });

test.skip(production || process.env.E2E_REGIONS !== '2', 'placement requires the local stack started with E2E_REGIONS=2');

test('a relayed session finishes a bounded close and releases its process slot', async ({ cells, request }) => {
  const created = await json<Database>(await cells.post(`${home}${project()}/databases`, {
    data: { name: name('session-relay'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  const url = 'http://127.0.0.1:8180' + database(created.id) + '/sql/ws';
  const active = async () => {
    const response = await request.get('http://127.0.0.1:9191/metrics');
    expect(response.status()).toBe(200);
    const metric = (await response.text()).split('\n').find(line => /^terrabase_qos_relay_sessions(?:\{| )/.test(line));
    expect(metric).toBeDefined();
    return Number(metric!.split(' ').at(-1));
  };
  try {
    for (let sample = 0; sample < 3; sample++) {
      const ws = await rawSqlSocket(url);
      try {
        const close = await ws.closing;
        expect(close).toEqual({ code: 1008, reason: 'authentication did not finish within 5 seconds' });
        expect(await active()).toBe(0);
        const started = performance.now();
        await ws.closed;
        expect(performance.now() - started).toBeLessThan(3000);
        expect(await active()).toBe(0);
      } finally {
        ws.socket.destroy();
        await ws.closed;
      }
    }
    const current = journey();
    const token = await request.post('https://auth.terrascale.tech/terrascale/oauth/token', {
      form: {
        grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26),
        client_secret: current.apiKey, project: current.projectId, scope: 'sql:read sql:write',
      },
    });
    expect(token.status()).toBe(200);
    const credential = (await token.json()).access_token as string;
    saveSecret('relay-token', credential);
    const ws = await rawSqlSocket(url, credential);
    try {
      const result = once(ws.events, 'message');
      ws.socket.write(frame(1, Buffer.from(JSON.stringify({ id: 1, type: 'execute', statements: [{ sql: 'SELECT 42' }] }))));
      expect((await result)[0].results[0].rows).toEqual([[42]]);
      ws.socket.write(frame(8, Buffer.from([3, 232])));
      await ws.closed;
    } finally {
      ws.socket.destroy();
      await ws.closed;
    }
  } finally {
    await removeDatabase(cells, created.id);
  }
});

async function credential(request: import('@playwright/test').APIRequestContext) {
  const current = journey();
  const response = await request.post('https://auth.terrascale.tech/terrascale/oauth/token', {
    form: { grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey,
      project: current.projectId, scope: 'sql:read sql:write' },
  });
  expect(response.status()).toBe(200);
  const token = (await response.json()).access_token as string;
  saveSecret('session-placement-token', token);
  return token;
}

async function execute(ws: Awaited<ReturnType<typeof rawSqlSocket>>, sql: string) {
  const reply = once(ws.events, 'message');
  ws.socket.write(frame(1, Buffer.from(JSON.stringify(sql === 'BEGIN' ? { id: 1, type: 'begin' } : { id: 1, type: 'execute', statements: [{ sql }] }))));
  return (await reply)[0];
}

test('forwarded sessions take the same organisation admission as local sessions', async ({ cells, request }) => {
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('forwarded-cap'), homeRegion: 'gru', replicaRegions: ['fra'] },
  }), 201);
  const token = await credential(request);
  const held: Awaited<ReturnType<typeof rawSqlSocket>>[] = [];
  try {
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER)');
    await sql(cells, home, created.id, 'INSERT INTO placement_rows VALUES (1)');
    await replicated(cells, replica, created.id, 1);
    for (let index = 0; index < 8; index++) {
      const ws = await rawSqlSocket('http://127.0.0.1:8080' + database(created.id) + '/sql/ws', token);
      held.push(ws);
      expect((await execute(ws, 'SELECT 42')).results[0].rows).toEqual([[42]]);
    }
    const forwarded = await rawSqlSocket('http://127.0.0.1:8180' + database(created.id) + '/sql/ws', token);
    held.push(forwarded);
    expect(await execute(forwarded, 'INSERT INTO placement_rows VALUES (2)')).toMatchObject({
      type: 'error', error: { code: 'tenant_busy', sqlstate: '53300' },
    });
  } finally {
    for (const ws of held) ws.socket.destroy();
    await Promise.all(held.map(ws => ws.closed));
    await removeDatabase(cells, created.id);
  }
});

test('authenticated relays share the tenant cap and leave relay capacity for another organisation', async ({ cells, request, playwright, browser }, info) => {
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('relay-isolation'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  const token = await credential(request);
  const context = await auditedContext(browser, {});
  const page = await context.newPage();
  const audit = new Audit(page, info);
  const label = name('session-owner');
  const email = await newAddress(label);
  const password = name('session-password') + name('secret');
  saveSecret('session-owner-password', password);
  const held: Awaited<ReturnType<typeof rawSqlSocket>>[] = [];
  try {
    await signUp(page, audit, email, password, label);
    await verify(page, email);
    await onboard(page, audit, label, label, label);
    const projectId = new URL(page.url()).searchParams.get('projectId');
    expect(projectId).toBeTruthy();
    const apiKey = await createApiKey(page, audit, label, label);
    const otherCells = await authenticatedCells(playwright.request, request, apiKey, projectId!);
    try {
      const otherPath = '/v1/projects/' + projectId + '/databases';
      const second = await json<Database>(await otherCells.post(home + otherPath, {
        data: { name: name('relay-other'), homeRegion: 'gru', replicaRegions: [] },
      }), 201);
      try {
        const grant = await request.post('https://auth.terrascale.tech/terrascale/oauth/token', {
          form: { grant_type: 'client_credentials', client_id: apiKey.slice(0, 26), client_secret: apiKey,
            project: projectId!, scope: 'sql:read sql:write' },
        });
        expect(grant.status()).toBe(200);
        const otherToken = (await grant.json()).access_token as string;
        saveSecret('session-other-token', otherToken);
        for (let index = 0; index < 8; index++) {
          const ws = await rawSqlSocket('http://127.0.0.1:8180' + database(created.id) + '/sql/ws', token);
          held.push(ws);
          expect((await execute(ws, 'SELECT 42')).results[0].rows).toEqual([[42]]);
        }
        const refused = await rawSqlSocket('http://127.0.0.1:8180' + database(created.id) + '/sql/ws', token);
        held.push(refused);
        const response = once(refused.events, 'message');
        expect((await response)[0]).toMatchObject({ type: 'error', error: { code: 'tenant_busy', sqlstate: '53300' } });
        const accepted = await rawSqlSocket('http://127.0.0.1:8180' + otherPath + '/' + second.id + '/sql/ws', otherToken);
        held.push(accepted);
        expect((await execute(accepted, 'SELECT 42')).results[0].rows).toEqual([[42]]);
      } finally {
        for (const ws of held) ws.socket.destroy();
        await Promise.all(held.map(ws => ws.closed));
        expect((await otherCells.delete(home + otherPath + '/' + second.id)).status()).toBe(204);
      }
    } finally {
      await otherCells.dispose();
    }
    await page.goto('/profile?tab=danger');
    await page.locator('main').getByRole('button', { name: 'Delete account', exact: true }).click();
    const dialog = page.locator('#delete-account');
    await expect(dialog).toBeVisible();
    await dialog.locator('#delete-account-email').fill(email);
    await dialog.getByRole('button', { name: 'Delete account', exact: true }).click();
    await confirmWithPassword(page, audit, password, 'session owner delete account');
    await page.waitForURL(url => url.pathname === '/account-deleted');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Account deleted');
  } finally {
    for (const ws of held) ws.socket.destroy();
    await Promise.all(held.map(ws => ws.closed));
    await saveCookies(context);
    await context.close();
    await removeDatabase(cells, created.id);
  }
});

test('replica promotion and late replica reads retain the original transaction deadline', async ({ cells, request }) => {
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('transaction-deadline'), homeRegion: 'gru', replicaRegions: ['fra'] },
  }), 201);
  const token = await credential(request);
  try {
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER)');
    await sql(cells, home, created.id, 'INSERT INTO placement_rows VALUES (1)');
    await replicated(cells, replica, created.id, 1);
    for (let sample = 0; sample < 3; sample++) {
      for (const promotion of [true, false]) {
        const ws = await rawSqlSocket('http://127.0.0.1:8180' + database(created.id) + '/sql/ws', token);
        const expired = new Promise<unknown>(resolve => {
          ws.events.on('message', reply => { if (reply.type === 'error') resolve(reply); });
        });
        const began = performance.now();
        let cancel = () => {};
        try {
          expect((await execute(ws, 'BEGIN')).transaction).toBe(true);
          const statement = promotion ? 'INSERT INTO placement_rows SELECT value FROM placement_rows'
            : 'WITH RECURSIVE r(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM r WHERE x<1000000000) SELECT SUM(x) FROM r';
          cancel = scheduleStatement(ws.socket, frame(1, Buffer.from(JSON.stringify({
            id: 2, type: 'execute', statements: [{ sql: statement }],
          }))), began, promotion ? 4900 : 4500);
          expect(await expired).toMatchObject({ type: 'error', error: { code: 'query_canceled', sqlstate: '57014' } });
          expect((await ws.closing).code).toBe(1008);
          ws.socket.destroy();
          await ws.closed;
          expect(performance.now() - began).toBeLessThan(6500);
        } finally {
          cancel();
          ws.socket.destroy();
          await ws.closed;
        }
      }
    }
    const read = await sql(cells, home, created.id, 'SELECT value FROM placement_rows');
    expect(read.results[0].rows).toEqual([[1]]);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('a Postgres relay with an unresponsive real peer drains before returning its active lease', async ({ cells, request }) => {
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('postgres-stall'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  const active = async () => {
    const response = await request.get('http://127.0.0.1:9191/metrics');
    expect(response.status()).toBe(200);
    const metric = (await response.text()).split('\n').find(line => /^terrabase_qos_active_sessions(?:\{| )/.test(line));
    expect(metric).toBeDefined();
    return Number(metric!.split(' ').at(-1));
  };
  const peersPath = join(stateDir, 'peers');
  const peers = readFileSync(peersPath, 'utf8');
  const fault = await holdPeerStream('127.0.0.5', 8081, 'http://127.0.0.1:8081', '/v1/internal/postgres');
  let pg: Awaited<ReturnType<typeof postgres>> | undefined;
  try {
    await expect.poll(active).toBe(0);
    writeFileSync(peersPath, peers.replace('journey-1 gru 127.0.0.1', 'journey-1 gru 127.0.0.5'));
    for (let sample = 0; sample < 3; sample++) {
      const blocked = once(fault.events, 'blocked');
      const began = performance.now();
      pg = await postgres(6532, created.id);
      await blocked;
      await expect.poll(active, { message: 'the relay holds an active lease during establishment' }).toBe(1);
      await fault.ping();
      expect(await pg.connected).toBe(false);
      expect(await pg.closed).toContain('C08001');
      await expect.poll(active, { message: 'the cancelled relay returns its active lease' }).toBe(0);
      expect(performance.now() - began).toBeLessThan(13_000);
    }
  } finally {
    writeFileSync(peersPath, peers);
    pg?.socket.destroy();
    if (pg) await pg.closed;
    await fault.close();
    await removeDatabase(cells, created.id);
  }
});

test('a commit whose answer arrives after the transaction deadline is reported as committed', async ({ cells, request }) => {
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('late-commit'), homeRegion: 'gru', replicaRegions: ['fra'] },
  }), 201);
  const token = await credential(request);
  const peersPath = join(stateDir, 'peers');
  const peers = readFileSync(peersPath, 'utf8');
  const fault = await holdPeerReplies('127.0.0.5', 8081, 'http://127.0.0.1:8081', '/v1/internal/sql/session');
  let ws: Awaited<ReturnType<typeof rawSqlSocket>> | undefined;
  let release: NodeJS.Timeout | undefined;
  try {
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER)');
    await sql(cells, home, created.id, 'INSERT INTO placement_rows VALUES (1)');
    await replicated(cells, replica, created.id, 1);
    writeFileSync(peersPath, peers.replace('journey-1 gru 127.0.0.1', 'journey-1 gru 127.0.0.5'));
    ws = await rawSqlSocket('http://127.0.0.1:8180' + database(created.id) + '/sql/ws', token);
    const replies: { type: string; error?: { message: string } }[] = [];
    ws.events.on('message', reply => replies.push(reply));
    const opened = once(fault.events, 'opened');
    const began = performance.now();
    expect((await execute(ws, 'BEGIN')).transaction).toBe(true);
    expect(await execute(ws, 'INSERT INTO placement_rows VALUES (2)')).toMatchObject({ type: 'result', transaction: true });
    await opened;
    fault.hold();
    const held = once(fault.events, 'held');
    const answer = once(ws.events, 'message');
    ws.socket.write(frame(1, Buffer.from(JSON.stringify({ id: 3, type: 'commit' }))));
    await held;
    release = setTimeout(fault.release, Math.max(0, began + 6500 - performance.now()));
    expect((await answer)[0]).toMatchObject({ id: 3, type: 'result', transaction: false });
    expect(performance.now() - began).toBeGreaterThan(5000);
    expect(replies.filter(reply => reply.error?.message.includes('rolled back'))).toEqual([]);
    const read = await sql(cells, home, created.id, 'SELECT value FROM placement_rows ORDER BY value');
    expect(read.results[0].rows).toEqual([[1], [2]]);
  } finally {
    clearTimeout(release);
    fault.release();
    writeFileSync(peersPath, peers);
    ws?.socket.destroy();
    if (ws) await ws.closed;
    await fault.close();
    await removeDatabase(cells, created.id);
  }
});

test('a Postgres relay whose client stops reading returns its admission', async ({ cells, request }) => {
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('postgres-unread'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  const active = async () => {
    const response = await request.get('http://127.0.0.1:9191/metrics');
    expect(response.status()).toBe(200);
    const metric = (await response.text()).split('\n').find(line => /^terrabase_qos_active_sessions(?:\{| )/.test(line));
    expect(metric).toBeDefined();
    return Number(metric!.split(' ').at(-1));
  };
  let pg: Awaited<ReturnType<typeof stalledPostgres>> | undefined;
  try {
    const prior = await active();
    const query = message('Q', Buffer.from('WITH RECURSIVE r(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM r WHERE x<2000) SELECT x, hex(zeroblob(1500)) FROM r\0'));
    pg = await stalledPostgres(6532, created.id, Buffer.concat(Array.from({ length: 16 }, () => query)));
    await expect.poll(active, { message: 'the relay holds an admission while its client is connected' }).toBe(prior + 1);
    await expect.poll(active, { message: 'the relay whose client stopped reading returns its admission', timeout: 45_000, intervals: [250, 500, 1000] })
      .toBe(prior);
  } finally {
    pg?.socket.destroy();
    if (pg) await pg.closed;
    await removeDatabase(cells, created.id);
  }
});
