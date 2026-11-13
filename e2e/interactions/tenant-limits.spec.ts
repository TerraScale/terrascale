import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { connect as peer } from 'node:http2';
import { join } from 'node:path';
import { expect, test, type APIRequestContext } from '@playwright/test';
import { frame, pacedWrite, rawSqlSocket, scheduleStatement } from '../support/sql-socket';
import { message, postgres } from '../support/postgres-wire';
import { auth, onLocalStack, stateDir } from '../support/stack';
import { journey, saveSecret } from '../support/state';

test.describe.configure({ mode: 'default' });

const api = 'https://api.terrascale.tech';
const path = () => `/v1/projects/${journey().projectId}/databases/${journey().databaseId}`;

async function token(request: APIRequestContext, scope: string): Promise<string> {
  const current = journey();
  const response = await request.post(auth + '/terrascale/oauth/token', {
    form: {
      grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26),
      client_secret: current.apiKey, project: current.projectId, scope,
    },
  });
  expect(response.status()).toBe(200);
  const credential = (await response.json()).access_token as string;
  saveSecret('session-token', credential);
  return credential;
}

async function websocket(credential: string) {
  const socket = new WebSocket('ws://127.0.0.1:8080' + path() + '/sql/ws', 'terrascale.sql.v1');
  const closed = once(socket, 'close').then(([event]) => event as CloseEvent);
  const opened = once(socket, 'open');
  const received = once(socket, 'message');
  await opened;
  socket.send(JSON.stringify({ id: 1, type: 'auth', token: credential }));
  const reply = JSON.parse(((await received)[0] as MessageEvent<string>).data) as {
    type: string; error?: { code: string }; results?: { rows: unknown[][] }[];
  };
  return { socket, closed, reply };
}

async function partialWebsocket(credential: string) {
  const raw = await rawSqlSocket('http://127.0.0.1:8080' + path() + '/sql/ws', credential);
  const result = once(raw.events, 'message');
  raw.socket.write(frame(1, Buffer.from(JSON.stringify({ id: 1, type: 'execute', statements: [{ sql: 'SELECT 1' }] }))));
  expect((await result)[0].results[0].rows).toEqual([[1]]);
  raw.closing.then(() => raw.socket.destroy());
  return raw;
}

async function openSockets(count: number, credentials: readonly string[], clients: Awaited<ReturnType<typeof websocket>>[]) {
  let next = 0;
  const outcomes = await Promise.allSettled(Array.from({ length: Math.min(8, count) }, async () => {
    for (let i = next++; i < count; i = next++) {
      const accepted = await websocket(credentials[i % credentials.length]);
      clients.push(accepted);
      expect(accepted.reply.type, 'session ' + (i + 1)).toBe('result');
    }
  }));
  for (const outcome of outcomes) if (outcome.status === 'rejected') throw outcome.reason;
  expect(clients).toHaveLength(count);
  for (const client of clients) expect(client.socket.readyState).toBe(WebSocket.OPEN);
}

async function setPlan(plan: 'hobby' | 'enterprise'): Promise<void> {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  saveSecret('session-peer', credential);
  const session = peer('http://127.0.0.1:8081');
  try {
    const stream = session.request({
      ':method': 'PUT', ':path': `/admin/organizations/${journey().organizationId}/plan`,
      'content-type': 'application/json', 'x-terrabase-peer-credential': credential,
    });
    session.on('error', error => stream.destroy(error));
    const response = once(stream, 'response');
    stream.end(JSON.stringify({ plan }));
    expect((await response)[0][':status']).toBe(200);
    let body = '';
    for await (const chunk of stream) body += chunk.toString();
    expect(JSON.parse(body).plan).toBe(plan);
  } finally {
    session.close();
  }
}

test('one serving process shares session caps and bounds incomplete messages without expiring pooled clients', async ({ request }) => {
  await onLocalStack(async () => {
    const credential = await token(request, 'sql:read sql:write');
    const read = await token(request, 'sql:read');
    const sockets: Awaited<ReturnType<typeof websocket>>[] = [];
    const clients: Awaited<ReturnType<typeof postgres>>[] = [];
    const partials: Awaited<ReturnType<typeof partialWebsocket>>[] = [];
    try {
      const first = await postgres();
      clients.push(first);
      expect(await first.connected).toBe(true);
      const pooled = await postgres();
      clients.push(pooled);
      expect(await pooled.connected).toBe(true);
      await openSockets(5, [credential, read], sockets);
      const fragmented = await partialWebsocket(credential);
      partials.push(fragmented);
      expect(first.socket.destroyed).toBe(false);
      expect(fragmented.socket.destroyed).toBe(false);

      const refused = await postgres();
      clients.push(refused);
      expect(await refused.connected).toBe(false);
      const pgRefusal = await refused.closed;
      expect(pgRefusal).toContain('C53300');
      expect(pgRefusal).toContain('Morganization session limit exceeded');
      const socketRefusal = await websocket(credential);
      sockets.push(socketRefusal);
      expect(socketRefusal.reply).toMatchObject({ type: 'error', error: { code: 'tenant_busy' } });
      expect((await socketRefusal.closed).code).toBe(1008);

      const pgTrickle = pacedWrite(first.socket, message('Q', Buffer.from('SELECT 42' + ' '.repeat(100) + '\0')), 1, 1);
      const wsTrickle = pacedWrite(fragmented.socket, frame(1, Buffer.from(' '.repeat(100))), 1, 1);
      const [pgPartial, wsPartial] = await Promise.all([first.closed, fragmented.closing]);
      expect(await pgTrickle).toBe(false);
      expect(await wsTrickle).toBe(false);
      expect(pgPartial).toContain('C57P05');
      expect(pgPartial).toContain('Mincomplete message timeout exceeded');
      expect(wsPartial).toEqual({ code: 1008, reason: 'incomplete message timeout exceeded' });
      expect(await pooled.query()).toBe('42');
      for (const client of sockets.slice(0, 5)) {
        expect(client.socket.readyState).toBe(WebSocket.OPEN);
        const result = once(client.socket, 'message');
        client.socket.send(JSON.stringify({ id: 2, type: 'execute', statements: [{ sql: 'SELECT 42' }] }));
        expect(JSON.parse(((await result)[0] as MessageEvent<string>).data).results[0].rows).toEqual([[42]]);
      }

      const recovered = await postgres();
      clients.push(recovered);
      expect(await recovered.connected).toBe(true);
      const recoveredSocket = await websocket(credential);
      sockets.push(recoveredSocket);
      expect(recoveredSocket.reply.type).toBe('result');
      const result = once(recoveredSocket.socket, 'message');
      recoveredSocket.socket.send(JSON.stringify({ id: 2, type: 'execute', statements: [{ sql: 'SELECT 42' }] }));
      expect(JSON.parse(((await result)[0] as MessageEvent<string>).data).results[0].rows).toEqual([[42]]);
    } finally {
      for (const client of partials) client.socket.destroy();
      for (const client of clients) if (!client.socket.destroyed) client.socket.end(message('X', Buffer.alloc(0)));
      for (const client of sockets) if (client.socket.readyState === WebSocket.OPEN) client.socket.close();
      await Promise.all([...clients.map(client => client.closed), ...sockets.map(client => client.closed)]);
    }

    const enterprise: Awaited<ReturnType<typeof websocket>>[] = [];
    try {
      await setPlan('enterprise');
      await openSockets(128, [credential], enterprise);
      const refused = await websocket(credential);
      enterprise.push(refused);
      expect(refused.reply).toMatchObject({ type: 'error', error: { code: 'tenant_busy' } });
      expect((await refused.closed).code).toBe(1008);
    } finally {
      for (const client of enterprise) if (client.socket.readyState === WebSocket.OPEN) client.socket.close();
      await Promise.all(enterprise.map(client => client.closed));
      await setPlan('hobby');
    }
  });
});

test('data-modifying WITH requires write scope and read-only WITH returns data', async ({ request }) => {
  const full = await token(request, 'sql:read sql:write');
  const read = await token(request, 'sql:read');
  const table = 'session_cte_rows';
  const execute = (credential: string, sql: string) => request.post(api + path() + '/sql/execute', {
    headers: { authorization: 'Bearer ' + credential }, data: { statements: [{ sql }] },
  });
  expect((await execute(full, `CREATE TABLE ${table} (id INTEGER PRIMARY KEY)`)).status()).toBe(200);
  try {
    expect((await execute(full, `WITH source AS (SELECT 7 AS id) INSERT INTO ${table} SELECT id FROM source`)).status()).toBe(200);
    for (const sql of [
      `WITH source AS (SELECT 8 AS id) INSERT INTO ${table} SELECT id FROM source`,
      `WITH source AS (SELECT 7 AS id) UPDATE ${table} SET id = 8 WHERE id IN (SELECT id FROM source)`,
      `WITH source AS (SELECT 7 AS id) DELETE FROM ${table} WHERE id IN (SELECT id FROM source)`,
    ]) {
      const response = await execute(read, sql);
      expect(response.status()).toBe(403);
      expect((await response.json()).error.code).toBe('forbidden_scope');
    }
    const remaining = await execute(read, `WITH source AS (SELECT id FROM ${table}) SELECT id FROM source`);
    expect(remaining.status()).toBe(200);
    expect((await remaining.json()).results[0].rows).toEqual([[7]]);
  } finally {
    expect((await execute(full, `DROP TABLE ${table}`)).status()).toBe(200);
  }
});

test('silent transactions close, roll back and return admission on both public protocols', async ({ request }) => {
  await onLocalStack(async () => {
    const credential = await token(request, 'sql:read sql:write');
    const execute = async (sql: string) => {
      const response = await request.post(api + path() + '/sql/execute', {
        headers: { authorization: 'Bearer ' + credential }, data: { statements: [{ sql }] },
      });
      expect(response.status()).toBe(200);
      return response.json();
    };
    await execute('CREATE TABLE silent_session_rows (value INTEGER)');
    const pg = await postgres();
    const ws = await rawSqlSocket('http://127.0.0.1:8080' + path() + '/sql/ws', credential);
    try {
      expect(await pg.connected).toBe(true);
      const result = once(ws.events, 'message');
      ws.socket.write(frame(1, Buffer.from(JSON.stringify({ id: 1, type: 'begin' }))));
      await pg.begin();
      expect((await result)[0]).toMatchObject({ type: 'result', transaction: true });
      const inserted = once(ws.events, 'message');
      ws.socket.write(frame(1, Buffer.from(JSON.stringify({ id: 2, type: 'execute', statements: [{ sql: 'INSERT INTO silent_session_rows VALUES (1)' }] }))));
      expect((await inserted)[0].type).toBe('result');
      const error = once(ws.events, 'message');
      const [pgErrors, wsError, close] = await Promise.all([pg.closed, error, ws.closing]);
      expect(pgErrors).toContain('C57014');
      expect(pgErrors).toContain('Mthe transaction expired: it was open for more than 5 seconds and was rolled back');
      expect(wsError[0]).toMatchObject({ type: 'error', error: { code: 'query_canceled', sqlstate: '57014' } });
      expect(close).toEqual({ code: 1008, reason: 'the transaction expired: it was open for more than 5 seconds and was rolled back' });
      expect((await execute('SELECT value FROM silent_session_rows')).results[0].rows).toEqual([]);
      const replacement = await postgres();
      expect(await replacement.connected).toBe(true);
      replacement.socket.end(message('X', Buffer.alloc(0)));
      await replacement.closed;
    } finally {
      pg.socket.destroy();
      ws.socket.destroy();
      await Promise.all([pg.closed, ws.closed]);
      await execute('DROP TABLE silent_session_rows');
    }
  });
});

for (const protocol of ['WebSocket', 'Postgres'] as const) {
  test(protocol + ' commit bytes arriving before expiry finish under the progress budget', async ({ request }) => {
    await onLocalStack(async () => {
      const credential = await token(request, 'sql:read sql:write');
      const call = async (sql: string) => {
        const response = await request.post(api + path() + '/sql/execute', {
          headers: { authorization: 'Bearer ' + credential }, data: { statements: [{ sql }] },
        });
        expect(response.status()).toBe(200);
        return response.json();
      };
      await call('CREATE TABLE progress_commit_rows (value INTEGER)');
      const cancel: (() => void)[] = [];
      const pg = protocol === 'Postgres' ? await postgres() : undefined;
      const ws = protocol === 'WebSocket' ? await rawSqlSocket('http://127.0.0.1:8080' + path() + '/sql/ws', credential) : undefined;
      const run = async (sql: string) => {
        if (pg) {
          const ready = once(pg.results, 'ready');
          pg.socket.write(message('Q', Buffer.from(sql + '\0')));
          await ready;
          expect(pg.errors).toEqual([]);
        } else {
          const answer = once(ws!.events, 'message');
          ws!.socket.write(frame(1, Buffer.from(JSON.stringify(sql === 'BEGIN' ? { id: 1, type: 'begin' }
            : { id: 1, type: 'execute', statements: [{ sql }] }))));
          expect((await answer)[0].type).toBe('result');
        }
      };
      try {
        if (pg) expect(await pg.connected).toBe(true);
        const began = performance.now();
        await run('BEGIN');
        await run('INSERT INTO progress_commit_rows VALUES (7)');
        const answer = pg ? once(pg.results, 'ready') : once(ws!.events, 'message');
        const socket = pg?.socket ?? ws!.socket;
        const bytes = pg ? message('Q', Buffer.from('COMMIT\0')) : frame(1, Buffer.from(JSON.stringify({ id: 2, type: 'commit' })));
        cancel.push(scheduleStatement(socket, bytes.subarray(0, 1), began, 4500));
        cancel.push(scheduleStatement(socket, bytes.subarray(1), began, 6500));
        const result = await answer;
        if (ws) expect(result[0]).toMatchObject({ id: 2, type: 'result', transaction: false });
        if (pg) expect(pg.errors).toEqual([]);
        expect(performance.now() - began).toBeGreaterThan(5000);
        expect((await call('SELECT value FROM progress_commit_rows')).results[0].rows).toEqual([[7]]);
      } finally {
        for (const stop of cancel) stop();
        pg?.socket.destroy();
        ws?.socket.destroy();
        if (pg) await pg.closed;
        if (ws) await ws.closed;
        await call('DROP TABLE progress_commit_rows');
      }
    });
  });
}

test('a statement budget failure leaves an explicit transaction aborted until commit', async ({ request }) => {
  await onLocalStack(async () => {
    const credential = await token(request, 'sql:read sql:write');
    const call = (sql: string) => request.post(api + path() + '/sql/execute', {
      headers: { authorization: 'Bearer ' + credential }, data: { statements: [{ sql }] },
    });
    expect((await call('CREATE TABLE cancelled_session_rows (value INTEGER)')).status()).toBe(200);
    const pg = await postgres();
    const run = async (sql: string) => {
      const ready = once(pg.results, 'ready');
      const errors = pg.errors.length;
      pg.socket.write(message('Q', Buffer.from(sql + '\0')));
      await ready;
      return pg.errors.slice(errors);
    };
    try {
      expect(await pg.connected).toBe(true);
      expect(await run('BEGIN')).toEqual([]);
      expect(await run('INSERT INTO cancelled_session_rows VALUES (1)')).toEqual([]);
      expect(await run('WITH RECURSIVE r(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM r WHERE x<1000000000) SELECT SUM(x) FROM r')).toContain('C57014');
      expect(await run('INSERT INTO cancelled_session_rows VALUES (2)')).toContain('C25P02');
      expect((await call('INSERT INTO cancelled_session_rows VALUES (7)')).status()).toBe(200);
      expect(pg.socket.destroyed).toBe(false);
      await run('COMMIT');
      const read = await call('SELECT value FROM cancelled_session_rows');
      expect(read.status()).toBe(200);
      expect((await read.json()).results[0].rows).toEqual([[7]]);
    } finally {
      pg.socket.destroy();
      await pg.closed;
      expect((await call('DROP TABLE cancelled_session_rows')).status()).toBe(200);
    }
  });
});

test('a prohibited 64-bit frame length fails the connection and leaves SQL available', async ({ request }) => {
  await onLocalStack(async () => {
    const credential = await token(request, 'sql:read sql:write');
    const ws = await rawSqlSocket('http://127.0.0.1:8080' + path() + '/sql/ws', credential);
    try {
      ws.socket.write(Buffer.from([0x81, 0xff, ...Array(8).fill(0xff), 0, 0, 0, 0]));
      const close = await ws.closing;
      expect(close.code).toBe(1002);
    } finally {
      ws.socket.destroy();
      await ws.closed;
    }
    const healthy = await websocket(credential);
    try {
      expect(healthy.reply.type).toBe('result');
      const result = once(healthy.socket, 'message');
      healthy.socket.send(JSON.stringify({ id: 2, type: 'execute', statements: [{ sql: 'SELECT 42' }] }));
      expect(JSON.parse(((await result)[0] as MessageEvent<string>).data).results[0].rows).toEqual([[42]]);
    } finally {
      healthy.socket.close();
      await healthy.closed;
    }
  });
});

test('honest uploads that progress beyond thirty seconds finish on both protocols', async ({ request }) => {
  await onLocalStack(async () => {
    const credential = await token(request, 'sql:read sql:write');
    const pg = await postgres();
    const ws = await partialWebsocket(credential);
    try {
      expect(await pg.connected).toBe(true);
      const received = once(ws.events, 'message');
      const row = once(pg.results, 'row');
      const padding = ' '.repeat(896 * 1024);
      const pgBytes = message('Q', Buffer.from('SELECT 42' + padding + '\0'));
      const wsBytes = frame(1, Buffer.from(JSON.stringify({ id: 7, type: 'execute', statements: [{ sql: 'SELECT 42' }] }) + padding));
      const began = performance.now();
      expect(await Promise.all([
        pacedWrite(pg.socket, pgBytes, 24 * 1024, 4096),
        pacedWrite(ws.socket, wsBytes, 24 * 1024, 4096),
      ])).toEqual([true, true]);
      expect(performance.now() - began).toBeGreaterThan(30_000);
      expect((await row)[0]).toBe('42');
      expect((await received)[0].results[0].rows).toEqual([[42]]);
    } finally {
      if (!pg.socket.destroyed) pg.socket.end(message('X', Buffer.alloc(0)));
      ws.socket.destroy();
      await Promise.all([pg.closed, ws.closed]);
    }
  });
});

test('a WebSocket managed cancellation releases the writer while the failed session stays open', async ({ request }) => {
  await onLocalStack(async () => {
    const credential = await token(request, 'sql:read sql:write');
    const call = (sql: string) => request.post(api + path() + '/sql/execute', {
      headers: { authorization: 'Bearer ' + credential }, data: { statements: [{ sql }] },
    });
    expect((await call('CREATE TABLE ws_cancelled_rows (value INTEGER)')).status()).toBe(200);
    const ws = await rawSqlSocket('http://127.0.0.1:8080' + path() + '/sql/ws', credential);
    const run = async (request: object) => {
      const reply = once(ws.events, 'message');
      ws.socket.write(frame(1, Buffer.from(JSON.stringify({ id: 1, ...request }))));
      return (await reply)[0];
    };
    try {
      expect((await run({ type: 'begin' })).transaction).toBe(true);
      expect((await run({ type: 'execute', statements: [{ sql: 'INSERT INTO ws_cancelled_rows VALUES (1)' }] })).type).toBe('result');
      expect(await run({ type: 'execute', statements: [{ sql: 'WITH RECURSIVE r(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM r WHERE x<1000000000) SELECT SUM(x) FROM r' }] }))
        .toMatchObject({ type: 'error', error: { code: 'query_canceled', sqlstate: '57014' } });
      expect((await call('INSERT INTO ws_cancelled_rows VALUES (7)')).status()).toBe(200);
      expect(ws.socket.destroyed).toBe(false);
      expect(await run({ type: 'execute', statements: [{ sql: 'INSERT INTO ws_cancelled_rows VALUES (2)' }] }))
        .toMatchObject({ type: 'error', error: { sqlstate: '25P02' } });
      await run({ type: 'commit' });
      const read = await call('SELECT value FROM ws_cancelled_rows');
      expect(read.status()).toBe(200);
      expect((await read.json()).results[0].rows).toEqual([[7]]);
    } finally {
      ws.socket.destroy();
      await ws.closed;
      expect((await call('DROP TABLE ws_cancelled_rows')).status()).toBe(200);
    }
  });
});
