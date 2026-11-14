import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { EventEmitter, once } from 'node:events';
import { connect } from 'node:net';
import { expect } from '@playwright/test';
import { rawSqlSocket, frame, scheduleStatement } from '../support/sql-socket';
import { holdPeerStream, holdPeerReplies } from '../support/peer-stream-fault';
import { delayCommitSync } from '../support/native-commit-fault';
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
  ws.socket.write(frame(1, Buffer.from(JSON.stringify(['BEGIN', 'COMMIT', 'ROLLBACK'].includes(sql) ? { id: 1, type: sql.toLowerCase() } : { id: 1, type: 'execute', statements: [{ sql }] }))));
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

test('replica promotion retains the original BEGIN at the next input wait', async ({ cells, request }) => {
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('transaction-wait'), homeRegion: 'gru', replicaRegions: ['fra'] },
  }), 201);
  const token = await credential(request);
  try {
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER)');
    await sql(cells, home, created.id, 'INSERT INTO placement_rows VALUES (1)');
    await replicated(cells, replica, created.id, 1);
    const ws = await rawSqlSocket('http://127.0.0.1:8180' + database(created.id) + '/sql/ws', token);
    let cancel = () => {};
    try {
      const began = performance.now();
      expect((await execute(ws, 'BEGIN')).transaction).toBe(true);
      const inserted = once(ws.events, 'message');
      cancel = scheduleStatement(ws.socket, frame(1, Buffer.from(JSON.stringify({
        id: 2, type: 'execute', statements: [{ sql: 'INSERT INTO placement_rows VALUES (2)' }],
      }))), began, 3000);
      expect((await inserted)[0]).toMatchObject({ type: 'result', transaction: true });
      expect(await ws.closing).toEqual({ code: 1008, reason: 'the transaction expired: it was open for more than 5 seconds and was rolled back' });
      expect(performance.now() - began).toBeLessThan(6500);
      ws.socket.destroy();
      await ws.closed;
    } finally {
      cancel();
      ws.socket.destroy();
      await ws.closed;
    }
    expect((await sql(cells, home, created.id, 'SELECT value FROM placement_rows')).results[0].rows).toEqual([[1]]);
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
  let cancelCommit = () => {};
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
    cancelCommit = scheduleStatement(ws.socket, frame(1, Buffer.from(JSON.stringify({ id: 3, type: 'commit' }))), began, 4500);
    await held;
    release = setTimeout(fault.release, Math.max(0, began + 6500 - performance.now()));
    expect((await answer)[0]).toMatchObject({ id: 3, type: 'result', transaction: false });
    expect(performance.now() - began).toBeGreaterThan(5000);
    expect(replies.filter(reply => reply.error?.message.includes('rolled back'))).toEqual([]);
    const read = await sql(cells, home, created.id, 'SELECT value FROM placement_rows ORDER BY value');
    expect(read.results[0].rows).toEqual([[1], [2]]);
    ws.socket.destroy();
    await ws.closed;
    ws = await rawSqlSocket('http://127.0.0.1:8180' + database(created.id) + '/sql/ws', token);
    expect((await execute(ws, 'BEGIN')).transaction).toBe(true);
    expect((await execute(ws, 'INSERT INTO placement_rows VALUES (3)')).type).toBe('result');
    fault.hold();
    const cutAnswer = once(fault.events, 'held');
    const unknown = once(ws.events, 'message');
    ws.socket.write(frame(1, Buffer.from(JSON.stringify({ id: 4, type: 'commit' }))));
    await cutAnswer;
    fault.cut();
    expect((await unknown)[0]).toMatchObject({ id: 4, type: 'error', error: { code: 'write_outcome_unknown', sqlstate: '08007' } });
    const closedUnknown = await Promise.race([
      ws.closing,
      once(AbortSignal.timeout(3000), 'abort').then(() => { throw new Error('unknown outcome did not close the public session'); }),
    ]);
    expect(closedUnknown.reason).toBe('write_outcome_unknown');
    expect((await sql(cells, home, created.id, 'SELECT value FROM placement_rows ORDER BY value')).results[0].rows).toEqual([[1], [2], [3]]);
  } finally {
    cancelCommit();
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
  const stalls = async () => {
    const response = await request.get('http://127.0.0.1:9191/metrics');
    expect(response.status()).toBe(200);
    const metric = (await response.text()).split('\n').find(line => /^terrabase_qos_relay_output_timeouts_total(?:\{| )/.test(line));
    return metric ? Number(metric.split(' ').at(-1)) : 0;
  };
  const stalledBefore = await stalls();
  let pg: Awaited<ReturnType<typeof stalledPostgres>> | undefined;
  try {
    const prior = await active();
    const query = message('Q', Buffer.from('WITH RECURSIVE r(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM r WHERE x<2000) SELECT x, hex(zeroblob(1500)) FROM r\0'));
    pg = await stalledPostgres(6532, created.id, Buffer.concat(Array.from({ length: 16 }, () => query)));
    await expect.poll(active, { message: 'the relay holds an admission while its client is connected' }).toBe(prior + 1);
    await expect.poll(active, { message: 'the relay whose client stopped reading returns its admission', timeout: 45_000, intervals: [250, 500, 1000] })
      .toBe(prior);
    const drained = await pg.drain();
    expect(drained.rows, 'the unread query produced real rows before backpressure closed it').toBeGreaterThan(0);
    expect(drained.errors).toEqual([]);
    await expect.poll(stalls, { message: 'the relay output flush exceeded its own budget' }).toBe(stalledBefore + 1);
  } finally {
    pg?.socket.destroy();
    if (pg) await pg.closed;
    await removeDatabase(cells, created.id);
  }
});

for (const kind of ['local WebSocket', 'local Postgres', 'forwarded SQL', 'WebSocket relay', 'Postgres relay'] as const) {
  test(kind + ' expires a silent transaction, rolls back and returns both admissions', async ({ cells, request }) => {
    const relayed = kind.endsWith('relay');
    const remote = relayed || kind === 'forwarded SQL';
    const created = await json<Database>(await cells.post(home + project() + '/databases', {
      data: { name: name('silent-' + kind.replaceAll(' ', '-').toLowerCase()), homeRegion: 'gru', replicaRegions: relayed ? [] : ['fra'] },
    }), 201);
    const token = await credential(request);
    const active = async (port: number) => {
      const response = await request.get('http://127.0.0.1:' + port + '/metrics');
      expect(response.status()).toBe(200);
      const metric = (await response.text()).split('\n').find(line => /^terrabase_qos_active_sessions(?:\{| )/.test(line));
      expect(metric).toBeDefined();
      return Number(metric!.split(' ').at(-1));
    };
    const ports = remote ? [9091, 9191] : [9091];
    const before = await Promise.all(ports.map(active));
    let ws: Awaited<ReturnType<typeof rawSqlSocket>> | undefined;
    let pg: Awaited<ReturnType<typeof postgres>> | undefined;
    try {
      await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER)');
      await sql(cells, home, created.id, 'INSERT INTO placement_rows VALUES (1)');
      if (!relayed) await replicated(cells, replica, created.id, 1);
      if (kind.includes('Postgres')) {
        pg = await postgres(remote ? 6532 : 6432, created.id);
        expect(await pg.connected).toBe(true);
        await pg.begin();
        const ready = once(pg.results, 'ready');
        pg.socket.write(message('Q', Buffer.from('INSERT INTO placement_rows VALUES (2)\0')));
        await ready;
        expect(pg.errors).toEqual([]);
        const errors = await pg.closed;
        expect(errors).toContain('C57014');
        expect(errors).toContain('Mthe transaction expired: it was open for more than 5 seconds and was rolled back');
      } else {
        ws = await rawSqlSocket('http://127.0.0.1:' + (remote ? 8180 : 8080) + database(created.id) + '/sql/ws', token);
        expect((await execute(ws, 'BEGIN')).transaction).toBe(true);
        expect(await execute(ws, 'INSERT INTO placement_rows VALUES (2)')).toMatchObject({ type: 'result', transaction: true });
        expect(await ws.closing).toEqual({ code: 1008, reason: 'the transaction expired: it was open for more than 5 seconds and was rolled back' });
        ws.socket.destroy();
        await ws.closed;
      }
      expect((await sql(cells, home, created.id, 'SELECT value FROM placement_rows')).results[0].rows).toEqual([[1]]);
      for (let index = 0; index < ports.length; index++) {
        await expect.poll(() => active(ports[index]), { message: kind + ' returned admission on port ' + ports[index] }).toBe(before[index]);
      }
    } finally {
      pg?.socket.destroy();
      ws?.socket.destroy();
      if (pg) await pg.closed;
      if (ws) await ws.closed;
      await removeDatabase(cells, created.id);
    }
  });
}

test('an established Postgres relay delivers a late commit answer without a transaction timer or retry error', async ({ cells }) => {
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('relay-late-commit'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  const peersPath = join(stateDir, 'peers');
  const peers = readFileSync(peersPath, 'utf8');
  const fault = await holdPeerReplies('127.0.0.5', 8081, 'http://127.0.0.1:8081', '/v1/internal/postgres');
  let pg: Awaited<ReturnType<typeof postgres>> | undefined;
  let cancel = () => {};
  let release: NodeJS.Timeout | undefined;
  try {
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER)');
    writeFileSync(peersPath, peers.replace('journey-1 gru 127.0.0.1', 'journey-1 gru 127.0.0.5'));
    pg = await postgres(6532, created.id);
    expect(await pg.connected).toBe(true);
    const began = performance.now();
    await pg.begin();
    const inserted = once(pg.results, 'ready');
    pg.socket.write(message('Q', Buffer.from('INSERT INTO placement_rows VALUES (7)\0')));
    await inserted;
    fault.hold();
    const held = once(fault.events, 'held');
    const committed = once(pg.results, 'ready');
    cancel = scheduleStatement(pg.socket, message('Q', Buffer.from('COMMIT\0')), began, 4500);
    await held;
    release = setTimeout(fault.release, Math.max(0, began + 6500 - performance.now()));
    await committed;
    expect(performance.now() - began).toBeGreaterThan(5000);
    expect(pg.errors).toEqual([]);
    expect((await sql(cells, home, created.id, 'SELECT value FROM placement_rows')).results[0].rows).toEqual([[7]]);
    fault.cut();
    expect(await pg.closed).not.toContain('C08001');
  } finally {
    cancel();
    clearTimeout(release);
    fault.release();
    writeFileSync(peersPath, peers);
    pg?.socket.destroy();
    if (pg) await pg.closed;
    await fault.close();
    await removeDatabase(cells, created.id);
  }
});

test('an entry WebSocket relay preserves a commit answer held beyond the total limit', async ({ cells, request }) => {
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('websocket-late-commit'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  const token = await credential(request);
  const peersPath = join(stateDir, 'peers');
  const peers = readFileSync(peersPath, 'utf8');
  const route = database(created.id) + '/sql/ws';
  const fault = await holdPeerReplies('127.0.0.5', 8081, 'http://127.0.0.1:8081', route);
  let ws: Awaited<ReturnType<typeof rawSqlSocket>> | undefined;
  let cancel = () => {};
  let release: NodeJS.Timeout | undefined;
  try {
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER)');
    writeFileSync(peersPath, peers.replace('journey-1 gru 127.0.0.1', 'journey-1 gru 127.0.0.5'));
    ws = await rawSqlSocket('http://127.0.0.1:8180' + route, token);
    const began = performance.now();
    expect((await execute(ws, 'BEGIN')).transaction).toBe(true);
    expect((await execute(ws, 'INSERT INTO placement_rows VALUES (7)')).type).toBe('result');
    fault.hold();
    const held = once(fault.events, 'held');
    const committed = once(ws.events, 'message');
    cancel = scheduleStatement(ws.socket, frame(1, Buffer.from(JSON.stringify({ id: 9, type: 'commit' }))), began, 4500);
    await held;
    release = setTimeout(fault.release, Math.max(0, began + 6500 - performance.now()));
    expect((await committed)[0]).toMatchObject({ id: 9, type: 'result', transaction: false });
    expect(performance.now() - began).toBeGreaterThan(5000);
    expect((await sql(cells, home, created.id, 'SELECT value FROM placement_rows')).results[0].rows).toEqual([[7]]);
  } finally {
    cancel();
    clearTimeout(release);
    fault.release();
    writeFileSync(peersPath, peers);
    ws?.socket.destroy();
    if (ws) await ws.closed;
    await fault.close();
    await removeDatabase(cells, created.id);
  }
});

for (const kind of ['local WebSocket', 'local Postgres', 'forwarded SQL', 'WebSocket relay', 'Postgres relay'] as const) {
  test(kind + ' delivers a complete commit accepted before expiry after native sync crosses the limit', async ({ cells, request }) => {
    const remote = kind.endsWith('relay') || kind === 'forwarded SQL';
    const forwarded = kind === 'forwarded SQL';
    const current = journey();
    const created = await json<Database>(await cells.post(home + project() + '/databases', {
      data: { name: name('native-commit'), homeRegion: 'gru', replicaRegions: forwarded ? ['fra'] : [] },
    }), 201);
    let ws: Awaited<ReturnType<typeof rawSqlSocket>> | undefined;
    let pg: Awaited<ReturnType<typeof postgres>> | undefined;
    let fault: Awaited<ReturnType<typeof delayCommitSync>> | undefined;
    let cancel = () => {};
    try {
      await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER)');
      await sql(cells, home, created.id, 'INSERT INTO placement_rows VALUES (1)');
      if (forwarded) await replicated(cells, replica, created.id, 1);
      if (kind.includes('Postgres')) {
        pg = await postgres(remote ? 6532 : 6432, created.id);
        expect(await pg.connected).toBe(true);
      } else {
        const token = await credential(request);
        ws = await rawSqlSocket('http://127.0.0.1:' + (remote ? 8180 : 8080) + database(created.id) + '/sql/ws', token);
      }
      const beganWall = Date.now();
      const began = performance.now();
      if (pg) {
        await pg.begin();
        const inserted = once(pg.results, 'ready');
        pg.socket.write(message('Q', Buffer.from('INSERT INTO placement_rows VALUES (7)\0')));
        await inserted;
        expect(pg.errors).toEqual([]);
      } else {
        expect((await execute(ws!, 'BEGIN')).transaction).toBe(true);
        expect((await execute(ws!, 'INSERT INTO placement_rows VALUES (7)')).type).toBe('result');
      }
      const wal = join(stateDir, 'terrabase', current.organizationId, current.projectId, created.id + '.db-wal');
      fault = await delayCommitSync(wal);
      const answer = pg ? once(pg.results, 'ready') : once(ws!.events, 'message');
      cancel = scheduleStatement(pg?.socket ?? ws!.socket, pg ? message('Q', Buffer.from('COMMIT\0'))
        : frame(1, Buffer.from(JSON.stringify({ id: 4, type: 'commit' }))), began, 4500);
      const result = await Promise.race([answer, (pg ? pg.closed : ws!.closing).then(reason => { throw new Error('native commit connection closed: ' + JSON.stringify(reason)); })]);
      if (pg) expect(pg.errors).toEqual([]);
      else expect(result[0]).toMatchObject({ id: 4, type: 'result', transaction: false });
      expect(performance.now() - began, 'native commit completion crosses the transaction limit').toBeGreaterThan(5000);
      const trace = await fault.finish();
      const sync = trace.split('\n').find(line => line.includes('fsync(') && line.includes(wal) && line.includes('(DELAYED)'));
      expect(sync, 'the real commit entered a delayed fsync on its database WAL').toBeDefined();
      expect(sync).toContain('= 0');
      const entered = Number(/(?:^|\s)(\d{10}\.\d+)\s+fsync\(/.exec(sync!)?.[1]) * 1000;
      expect(entered, 'the complete commit reached native I/O before expiry').toBeLessThan(beganWall + 5000);
      expect(entered, 'the syscall belongs to the scheduled commit').toBeGreaterThan(beganWall + 4000);
      console.log(kind + ': native fsync entered at ' + Math.round(entered - beganWall) + ' ms; commit answered after ' + Math.round(performance.now() - began) + ' ms');
      expect((await sql(cells, home, created.id, 'SELECT value FROM placement_rows ORDER BY value')).results[0].rows).toEqual([[1], [7]]);
    } finally {
      cancel();
      await fault?.finish();
      pg?.socket.destroy();
      ws?.socket.destroy();
      if (pg) await pg.closed;
      if (ws) await ws.closed;
      await removeDatabase(cells, created.id);
    }
  });
}

for (const kind of ['forwarded SQL', 'WebSocket relay', 'Postgres relay', 'Postgres extended relay'] as const) {
  test(kind + ' bounds a live peer stalled inside a commit answer and closes the unknown session', async ({ cells, request }) => {
    const forwarded = kind === 'forwarded SQL';
    const pgRelay = kind.startsWith('Postgres');
    const extended = kind === 'Postgres extended relay';
    const created = await json<Database>(await cells.post(home + project() + '/databases', {
      data: { name: name('partial-answer'), homeRegion: 'gru', replicaRegions: forwarded ? ['fra'] : [] },
    }), 201);
    const token = await credential(request);
    const route = database(created.id) + '/sql/ws';
    const peersPath = join(stateDir, 'peers');
    const peers = readFileSync(peersPath, 'utf8');
    const fault = await holdPeerReplies('127.0.0.5', 8081, 'http://127.0.0.1:8081',
      forwarded ? '/v1/internal/sql/session' : pgRelay ? '/v1/internal/postgres' : route);
    let ws: Awaited<ReturnType<typeof rawSqlSocket>> | undefined;
    let pg: Awaited<ReturnType<typeof postgres>> | undefined;
    const active = async (port: number) => {
      const response = await request.get('http://127.0.0.1:' + port + '/metrics');
      expect(response.status()).toBe(200);
      const line = (await response.text()).split('\n').find(row => /^terrabase_qos_active_sessions(?:\{| )/.test(row));
      expect(line).toBeDefined();
      return Number(line!.split(' ').at(-1));
    };
    try {
      await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER)');
      await sql(cells, home, created.id, 'INSERT INTO placement_rows VALUES (1)');
      if (forwarded) await replicated(cells, replica, created.id, 1);
      writeFileSync(peersPath, peers.replace('journey-1 gru 127.0.0.1', 'journey-1 gru 127.0.0.5'));
      if (pgRelay) {
        pg = await postgres(6532, created.id);
        expect(await pg.connected).toBe(true);
        await pg.begin();
        const ready = once(pg.results, 'ready');
        pg.socket.write(message('Q', Buffer.from('INSERT INTO placement_rows VALUES (7)\0')));
        await ready;
        if (extended) {
          const bound = once(pg.results, 'bound');
          pg.socket.write(Buffer.concat([
            message('P', Buffer.concat([Buffer.from('commit-statement\0COMMIT\0'), Buffer.alloc(2)])),
            message('B', Buffer.concat([Buffer.from('commit-portal\0commit-statement\0'), Buffer.alloc(6)])),
            message('H', Buffer.alloc(0)),
          ]));
          await bound;
        }
      } else {
        ws = await rawSqlSocket('http://127.0.0.1:8180' + route, token);
        const beganTransaction = await execute(ws, 'BEGIN');
        expect(beganTransaction, JSON.stringify(beganTransaction)).toMatchObject({ type: 'result', transaction: true });
        expect((await execute(ws, 'INSERT INTO placement_rows VALUES (7)')).type).toBe('result');
      }
      fault.hold(pgRelay ? 6 : 8);
      const prefix = once(fault.events, 'prefix', { signal: AbortSignal.timeout(10_000) });
      const answer = Promise.race([
        pg ? pg.closed : once(ws!.events, 'message'),
        once(AbortSignal.timeout(35_000), 'abort').then(() => { throw new Error('stalled answer exceeded its progress bound'); }),
        ...(ws ? [ws.closing.then(close => { throw new Error('closed before outcome answer: ' + JSON.stringify(close)); })] : []),
      ]);
      const began = performance.now();
      (pg?.socket ?? ws!.socket).write(pg ? extended
        ? message('E', Buffer.concat([Buffer.from('commit-portal\0'), Buffer.alloc(4)])) : message('Q', Buffer.from('COMMIT\0'))
        : frame(1, Buffer.from(JSON.stringify({ id: 9, type: 'commit' }))));
      await prefix;
      console.log(kind + ': prefix forwarded after ' + Math.round(performance.now() - began) + ' ms');
      await fault.ping();
      console.log(kind + ': peer ping answered after ' + Math.round(performance.now() - began) + ' ms');
      if (pg) {
        expect(await answer).toContain('C08007');
        expect(pg.errors.some(error => error.includes('the session is closed'))).toBe(true);
        expect(pg.errors.join(' ')).not.toContain('rolled back');
      } else {
        expect((await answer)[0]).toMatchObject({ id: 9, type: 'error', error: { code: 'write_outcome_unknown', sqlstate: '08007' } });
        expect((await ws!.closing).reason).toBe('write_outcome_unknown');
      }
      expect(performance.now() - began, 'partial answer has a 30-second base plus bytes/rate budget').toBeLessThan(35_000);
      for (const port of [9091, 9191]) {
        await expect.poll(() => active(port), { message: 'returned session admission on ' + port }).toBe(0);
      }
      expect((await sql(cells, home, created.id, 'SELECT value FROM placement_rows ORDER BY value')).results[0].rows).toEqual([[1], [7]]);
      fault.release();
      if (ws) {
        ws.socket.destroy();
        await ws.closed;
        ws = await rawSqlSocket('http://127.0.0.1:8180' + route, await credential(request));
        const beganTransaction = await execute(ws, 'BEGIN');
        expect(beganTransaction, JSON.stringify(beganTransaction)).toMatchObject({ type: 'result', transaction: true });
        expect((await execute(ws, 'SELECT 42')).results[0].rows).toEqual([[42]]);
        expect((await execute(ws, 'ROLLBACK')).transaction).toBe(false);
      } else {
        pg = await postgres(6532, created.id);
        expect(await pg.connected).toBe(true);
        await pg.begin();
        expect(await pg.query()).toBe('42');
      }
    } finally {
      fault.release();
      writeFileSync(peersPath, peers);
      ws?.socket.destroy();
      pg?.socket.destroy();
      if (ws) await ws.closed;
      if (pg) await pg.closed;
      await fault.close();
      await removeDatabase(cells, created.id);
    }
  });
}

test('a WebSocket relay close drains a stalled answer within its closing budget', async ({ cells, request }) => {
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('drain-answer'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  const token = await credential(request);
  const route = database(created.id) + '/sql/ws';
  const peersPath = join(stateDir, 'peers');
  const peers = readFileSync(peersPath, 'utf8');
  const fault = await holdPeerReplies('127.0.0.5', 8081, 'http://127.0.0.1:8081', route);
  let ws: Awaited<ReturnType<typeof rawSqlSocket>> | undefined;
  try {
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER)');
    writeFileSync(peersPath, peers.replace('journey-1 gru 127.0.0.1', 'journey-1 gru 127.0.0.5'));
    ws = await rawSqlSocket('http://127.0.0.1:8180' + route, token);
    const beganTransaction = await execute(ws, 'BEGIN');
    expect(beganTransaction, JSON.stringify(beganTransaction)).toMatchObject({ type: 'result', transaction: true });
    expect((await execute(ws, 'INSERT INTO placement_rows VALUES (7)')).type).toBe('result');
    fault.hold();
    const held = once(fault.events, 'held');
    ws.socket.write(frame(1, Buffer.from(JSON.stringify({ id: 9, type: 'commit' }))));
    await held;
    await fault.ping();
    const incomplete = frame(1, Buffer.from(JSON.stringify({ id: 10, type: 'execute', statements: [{ sql: 'SELECT 42' }] })));
    ws.socket.write(incomplete.subarray(0, incomplete.length - 1));
    const reply = Promise.race([
      once(ws.events, 'message', { signal: AbortSignal.timeout(33_000) }),
      ws.closing.then(close => { throw new Error('closed before drain outcome: ' + JSON.stringify(close)); }),
    ]);
    const began = performance.now();
    expect((await reply)[0]).toMatchObject({ id: 9, type: 'error', error: { code: 'write_outcome_unknown', sqlstate: '08007' } });
    expect((await ws.closing).reason).toBe('write_outcome_unknown');
    await ws.closed;
    expect(performance.now() - began).toBeLessThan(33_000);
    expect((await sql(cells, home, created.id, 'SELECT value FROM placement_rows')).results[0].rows).toEqual([[7]]);
    const metrics = await request.get('http://127.0.0.1:9191/metrics');
    expect(await metrics.text()).toMatch(/terrabase_qos_relay_sessions(?:\{[^\n]*\})? 0(?:\n|$)/);
  } finally {
    fault.release();
    writeFileSync(peersPath, peers);
    ws?.socket.destroy();
    if (ws) await ws.closed;
    await fault.close();
    await removeDatabase(cells, created.id);
  }
});

test('verified platform EF transactions bypass new session accounting and retain request admission', async ({ request }) => {
  const metrics = async () => {
    const response = await request.get('http://127.0.0.1:9091/metrics');
    expect(response.status()).toBe(200);
    return response.text();
  };
  const admitted = (body: string) => body.split('\n').filter(line => /^terrabase_qos_request_duration_seconds_count\{/.test(line))
    .reduce((sum, line) => sum + Number(line.split(' ').at(-1)), 0);
  const before = await metrics();
  const runner = spawn('dotnet', [join(process.env.E2E_BUILD!, 'social/Journey.Social.dll'), 'platform-sessions'], {
    env: { ...process.env, E2E_STATE: stateDir }, stdio: ['pipe', 'pipe', 'pipe'],
  });
  const exited = once(runner, 'exit');
  let stderr = '';
  runner.stderr.on('data', chunk => { stderr += chunk; });
  try {
    const ready = await Promise.race([
      once(runner.stdout, 'data').then(([data]) => data.toString()),
      exited.then(([code]) => { throw new Error('platform EF runner exited ' + code + ': ' + stderr); }),
    ]);
    expect(ready).toContain('16 platform EF transactions completed');
    let during = '';
    await expect.poll(async () => admitted(during = await metrics())).toBeGreaterThan(admitted(before) + 32);
    expect(during).toMatch(/terrabase_qos_active_sessions(?:\{[^\n]*\})? 0(?:\n|$)/);
    expect(during).toMatch(/terrabase_qos_preauthentication_sessions(?:\{[^\n]*\})? 0(?:\n|$)/);
    expect(during).toMatch(/terrabase_qos_organization_session_utilization(?:\{[^\n]*\})? 0(?:\n|$)/);
    expect(admitted(during)).toBeGreaterThan(admitted(before) + 32);
    const refusals = (body: string) => body.split('\n').filter(line => /^terrabase_qos_session_refusals_total(?:\{| )/.test(line));
    expect(refusals(during)).toEqual(refusals(before));
    const admissions = (body: string) => body.split('\n').filter(line => /^terrabase_qos_(session|preauthentication)_admissions_total(?:\{| )/.test(line));
    expect(admissions(during)).toEqual(admissions(before));
    runner.stdin.end('release\n');
    expect((await exited)[0], stderr).toBe(0);
  } finally {
    if (runner.exitCode === null) {
      runner.kill('SIGTERM');
      await exited;
    }
  }
});

async function wire(port: number, db: string, startup = true) {
  const current = journey();
  const socket = connect(port, '127.0.0.1');
  await once(socket, 'connect');
  const types: string[] = [];
  const errors: string[] = [];
  const events = new EventEmitter();
  let buffered = Buffer.alloc(0);
  socket.on('data', (chunk: Buffer) => {
    buffered = Buffer.concat([buffered, chunk]);
    while (buffered.length >= 5 && buffered.length >= 1 + buffered.readInt32BE(1)) {
      const length = buffered.readInt32BE(1);
      const type = String.fromCharCode(buffered[0]);
      const body = buffered.subarray(5, length + 1);
      buffered = buffered.subarray(length + 1);
      if (type === 'R' && body.readInt32BE() === 3 && startup) socket.write(message('p', Buffer.from(current.apiKey + '\0')));
      types.push(type);
      if (type === 'E') errors.push(body.toString().split('\0').filter(Boolean).join(' | '));
      events.emit('message', type, body);
    }
  });
  socket.on('error', () => {});
  const closed = new Promise<void>(resolve => socket.once('close', () => { events.emit('message', 'closed', Buffer.alloc(0)); resolve(); }));
  const next = (...wanted: string[]) => new Promise<{ type: string; body: Buffer }>(resolve => {
    const on = (type: string, body: Buffer) => {
      if (wanted.includes(type) || type === 'closed') { events.off('message', on); resolve({ type, body }); }
    };
    events.on('message', on);
  });
  const parameters = Buffer.from(`user\0any\0database\0${current.projectId}/${db}\0\0`);
  const prefix = Buffer.alloc(8);
  prefix.writeInt32BE(parameters.length + 8);
  prefix.writeInt32BE(196608, 4);
  const opened = next(startup ? 'Z' : 'R', 'E');
  socket.write(Buffer.concat([prefix, parameters]));
  return { socket, types, errors, closed, next, opened: (await opened).type };
}

async function slowReader(port: number, bytesPerSecond: number) {
  const proxy = spawn('python3', ['support/slow-reader.py', String(port), String(bytesPerSecond)], { stdio: ['pipe', 'pipe', 'pipe'] });
  const exited = once(proxy, 'exit');
  let stderr = '';
  proxy.stderr.on('data', data => { stderr += data.toString(); });
  const listening = await Promise.race([
    once(proxy.stdout, 'data').then(([data]) => Number(data.toString().trim())),
    exited.then(([code]) => { throw new Error('slow reader exited ' + code + ': ' + stderr); }),
  ]);
  return {
    port: listening,
    start: () => proxy.stdin.write('pace\n'),
    stop: async () => {
      if (proxy.exitCode === null) proxy.kill('SIGTERM');
      await exited;
    },
  };
}

function insertBatch(count: number, first: number) {
  const batch: Buffer[] = [];
  for (let index = 0; index < count; index++) {
    batch.push(message('P', Buffer.concat([Buffer.from(`\0INSERT INTO pipelined VALUES (${first + index})\0`), Buffer.alloc(2)])));
    batch.push(message('B', Buffer.concat([Buffer.from('\0\0'), Buffer.alloc(6)])));
    batch.push(message('E', Buffer.concat([Buffer.from('\0'), Buffer.alloc(4)])));
  }
  batch.push(message('S', Buffer.alloc(0)));
  return Buffer.concat(batch);
}

test('a pipelined extended-protocol batch of any size runs the same through the relay as at the home', async ({ cells }) => {
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('pipelined'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  try {
    await sql(cells, home, created.id, 'CREATE TABLE pipelined (value INTEGER)');
    let committed = 0;
    for (const port of [6432, 6532]) {
      for (const count of [80, 86, 100, 120]) {
        const client = await wire(port, created.id);
        expect(client.opened).toBe('Z');
        const before = client.types.length;
        const ready = client.next('Z');
        client.socket.write(insertBatch(count, port * 1_000_000 + count * 1000));
        const label = `${count} statements on port ${port}: ${client.errors.join(';')}`;
        expect((await ready).type, label).toBe('Z');
        expect(client.errors.every(error => error.startsWith('SERROR | VERROR | C53300 | Mtenant busy')), label).toBe(true);
        const completed = client.types.slice(before).filter(type => type === 'C').length;
        expect(completed + client.errors.length, label).toBeGreaterThan(0);
        committed += completed;
        const again = client.next('Z');
        client.socket.write(message('Q', Buffer.from('SELECT 1\0')));
        expect((await again).type, label).toBe('Z');
        client.socket.destroy();
        await client.closed;
      }
    }
    await expect.poll(async () => {
      const counted = await cells.post(home + database(created.id) + '/sql/execute', { data: { statements: [{ sql: 'SELECT COUNT(*) FROM pipelined' }] } });
      return counted.status() === 200 ? (await counted.json()).results[0].rows : counted.status();
    }, { message: 'every completed statement committed, and nothing else' }).toEqual([[committed]]);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('a WebSocket relay answers 300 pipelined calls in order like the home', async ({ cells, request }) => {
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('ws-pipelined'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  try {
    const token = await credential(request);
    for (const port of [8080, 8180]) {
      const ws = await rawSqlSocket('http://127.0.0.1:' + port + database(created.id) + '/sql/ws', token);
      const answers: { id: number; type: string; error?: { code: string } }[] = [];
      const last = new Promise<void>(resolve => ws.events.on('message', (answer: { id: number; type: string; error?: { code: string } }) => {
        answers.push(answer);
        if (answer.id === 300) resolve();
      }));
      ws.socket.write(Buffer.concat(Array.from({ length: 300 }, (_, index) =>
        frame(1, Buffer.from(JSON.stringify({ id: index + 1, type: 'execute', statements: [{ sql: 'SELECT ' + (index + 1) }] }))))));
      expect(await Promise.race([last.then(() => 'answered'), ws.closing.then(close => JSON.stringify(close))]), 'port ' + port).toBe('answered');
      expect(answers.map(answer => answer.id)).toEqual(Array.from({ length: 300 }, (_, index) => index + 1));
      expect(answers.filter(answer => answer.type !== 'result' && answer.error?.code !== 'tenant_busy'), 'port ' + port).toEqual([]);
      let call = 300;
      await expect.poll(async () => {
        const reply = once(ws.events, 'message');
        ws.socket.write(frame(1, Buffer.from(JSON.stringify({ id: ++call, type: 'execute', statements: [{ sql: 'SELECT 1' }] }))));
        return ((await reply)[0] as { type: string }).type;
      }, { message: 'the session stays open and serves once the organization budget refills' }).toBe('result');
      ws.socket.destroy();
      await ws.closed;
    }
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('a Postgres client that reads a large answer slowly but steadily keeps its session at the home and through the relay', async ({ cells }) => {
  test.setTimeout(150_000);
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('slow-reader'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  const large = 'SELECT hex(zeroblob(262144))';
  try {
    const viaPostgres = async (port: number) => {
      const proxy = await slowReader(port, 4096);
      const client = await wire(proxy.port, created.id);
      try {
        expect(client.opened).toBe('Z');
        proxy.start();
        const began = performance.now();
        const row = client.next('D', 'E');
        const ready = client.next('Z');
        client.socket.write(message('Q', Buffer.from(large + '\0')));
        const delivered = await row;
        expect(delivered.type, `Postgres port ${port} after ${Math.round(performance.now() - began)} ms, ${client.socket.bytesRead} bytes: ${client.errors.join(';')}`).toBe('D');
        expect(delivered.body.readInt32BE(2)).toBe(524_288);
        expect((await ready).type).toBe('Z');
        const again = client.next('Z');
        client.socket.write(message('Q', Buffer.from('SELECT 7\0')));
        expect((await again).type).toBe('Z');
        expect(client.errors).toEqual([]);
        client.socket.destroy();
        await client.closed;
        console.log('Postgres ' + port + ': delivered 524288 bytes and reused session after ' + Math.round(performance.now() - began) + ' ms');
      } finally {
        client.socket.destroy();
        await proxy.stop();
      }
    };
    await Promise.all([viaPostgres(6432), viaPostgres(6532)]);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('a WebSocket client reading an answer slowly keeps its session at the home and through the relay', async ({ cells, request }) => {
  test.setTimeout(75_000);
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('ws-slow-reader'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  try {
    const token = await credential(request);
    await Promise.all([8080, 8180].map(async port => {
      const proxy = await slowReader(port, 24_000);
      const ws = await rawSqlSocket('http://127.0.0.1:' + proxy.port + database(created.id) + '/sql/ws', token);
      try {
        proxy.start();
        const began = performance.now();
        const response = once(ws.events, 'message');
        ws.socket.write(frame(1, Buffer.from(JSON.stringify({ id: 1, type: 'execute', statements: [{ sql: 'SELECT hex(zeroblob(655360))' }] }))));
        const result = await Promise.race([response.then(([answer]) => answer), ws.closing.then(close => { throw new Error('WebSocket ' + port + ' closed: ' + JSON.stringify(close)); })]);
        expect(result.type).toBe('result');
        expect(result.results[0].rows[0][0]).toHaveLength(1_310_720);
        expect(/^0+$/.test(result.results[0].rows[0][0])).toBe(true);
        expect((await execute(ws, 'SELECT 7')).results[0].rows).toEqual([[7]]);
        console.log('WebSocket ' + port + ': delivered 1310720 bytes and reused session after ' + Math.round(performance.now() - began) + ' ms');
      } finally {
        ws.socket.destroy();
        await proxy.stop();
      }
    }));
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('an answer that makes no transport progress closes the Postgres and WebSocket sessions', async ({ cells, request }) => {
  test.setTimeout(60_000);
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('stopped-reader'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  const active = async () => Promise.all([9091, 9191].map(async port => {
    const response = await request.get('http://127.0.0.1:' + port + '/metrics');
    expect(response.status()).toBe(200);
    const metric = (await response.text()).split('\n').find(line => /^terrabase_qos_active_sessions(?:\{| )/.test(line));
    expect(metric).toBeDefined();
    return Number(metric!.split(' ').at(-1));
  }));
  const proxies: Awaited<ReturnType<typeof slowReader>>[] = [];
  let pg: Awaited<ReturnType<typeof wire>> | undefined;
  let ws: Awaited<ReturnType<typeof rawSqlSocket>> | undefined;
  try {
    await expect.poll(active).toEqual([0, 0]);
    const token = await credential(request);
    const pgProxy = await slowReader(6432, 0);
    proxies.push(pgProxy);
    pg = await wire(pgProxy.port, created.id);
    expect(pg.opened).toBe('Z');
    const wsProxy = await slowReader(8180, 0);
    proxies.push(wsProxy);
    ws = await rawSqlSocket('http://127.0.0.1:' + wsProxy.port + database(created.id) + '/sql/ws', token);
    await expect.poll(active).toEqual([2, 1]);
    pgProxy.start();
    wsProxy.start();
    const began = performance.now();
    pg.socket.write(message('Q', Buffer.from('SELECT hex(zeroblob(1500000))\0')));
    ws.socket.write(frame(1, Buffer.from(JSON.stringify({ id: 1, type: 'execute', statements: [{ sql: 'SELECT hex(zeroblob(1500000))' }] }))));
    await expect.poll(active, { timeout: 40_000, message: 'a blocked writer releases its session after 30 seconds without progress' }).toEqual([0, 0]);
    console.log('Stopped readers released sessions after ' + Math.round(performance.now() - began) + ' ms');
  } finally {
    pg?.socket.destroy();
    ws?.socket.destroy();
    await Promise.all(proxies.map(proxy => proxy.stop()));
    await removeDatabase(cells, created.id);
  }
});

test('one address may hold as many connection attempts as the largest organization session allowance', async ({ cells }) => {
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('attempts'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  const attempts: Awaited<ReturnType<typeof wire>>[] = [];
  try {
    for (let index = 0; index < 128; index++) {
      const attempt = await wire(6432, created.id, false);
      attempts.push(attempt);
      expect(attempt.opened, 'attempt ' + (index + 1) + ': ' + attempt.errors.join(';')).toBe('R');
    }
    const refused = await wire(6432, created.id, false);
    attempts.push(refused);
    expect(refused.opened).toBe('E');
    expect(refused.errors).toEqual(['SFATAL | VFATAL | C53300 | Mtoo many concurrent connection attempts from this address']);
    const ws = await rawSqlSocket('http://127.0.0.1:8080' + database(created.id) + '/sql/ws');
    expect((await once(ws.events, 'message'))[0]).toMatchObject({ type: 'error', error: { code: 'tenant_busy', message: 'too many concurrent connection attempts from this address' } });
    expect((await ws.closing).code).toBe(1008);
    ws.socket.destroy();
  } finally {
    for (const attempt of attempts) attempt.socket.destroy();
    await Promise.all(attempts.map(attempt => attempt.closed));
    await removeDatabase(cells, created.id);
  }
});

test('a busy organization is refused only between Postgres batches, never inside one', async ({ cells, request }) => {
  const created = await json<Database>(await cells.post(home + project() + '/databases', {
    data: { name: name('busy-batch'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  const gru = Number(readFileSync(join(stateDir, 'pids', 'terrabase.pid'), 'utf8').trim());
  const clients: Awaited<ReturnType<typeof wire>>[] = [];
  const rejected = async () => {
    const response = await request.get('http://127.0.0.1:9191/metrics');
    expect(response.status()).toBe(200);
    return (await response.text()).split('\n').filter(line => /^terrabase_qos_rejected_total\{[^\n]*reason="concurrency"/.test(line))
      .reduce((sum, line) => sum + Number(line.split(' ').at(-1)), 0);
  };
  try {
    await sql(cells, home, created.id, 'CREATE TABLE pipelined (value INTEGER)');
    const batch = await wire(6532, created.id);
    clients.push(batch);
    for (let index = 0; index < 6; index++) clients.push(await wire(6532, created.id));
    expect(clients.map(client => client.opened)).toEqual(Array(7).fill('Z'));
    const parsed = batch.next('1');
    batch.socket.write(message('P', Buffer.concat([Buffer.from('\0INSERT INTO pipelined VALUES (1)\0'), Buffer.alloc(2)])));
    expect((await parsed).type).toBe('1');
    const before = await rejected();
    process.kill(gru, 'SIGSTOP');
    let holder = 1;
    await expect.poll(async () => {
      if (holder < clients.length) clients[holder++].socket.write(message('Q', Buffer.from('SELECT 1\0')));
      return rejected();
    }, { message: 'the organization is busy at the entry region' }).toBeGreaterThan(before);
    const ready = batch.next('Z');
    batch.socket.write(Buffer.concat([
      message('B', Buffer.concat([Buffer.from('\0\0'), Buffer.alloc(6)])),
      message('E', Buffer.concat([Buffer.from('\0'), Buffer.alloc(4)])),
      message('S', Buffer.alloc(0)),
    ]));
    process.kill(gru, 'SIGCONT');
    expect((await ready).type, batch.errors.join(';')).toBe('Z');
    expect(batch.errors).toEqual([]);
    expect(batch.types.filter(type => type === 'C')).toHaveLength(1);
    expect((await sql(cells, home, created.id, 'SELECT value FROM pipelined')).results[0].rows).toEqual([[1]]);
  } finally {
    process.kill(gru, 'SIGCONT');
    for (const client of clients) client.socket.destroy();
    await Promise.all(clients.map(client => client.closed));
    await removeDatabase(cells, created.id);
  }
});
