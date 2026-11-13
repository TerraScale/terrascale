import { connect } from 'node:net';
import { EventEmitter } from 'node:events';
import type { Socket } from 'node:net';
import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { connect as peer } from 'node:http2';
import { join } from 'node:path';
import { expect, test, type APIRequestContext } from '@playwright/test';
import { frame, rawSqlSocket } from '../support/sql-socket';
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

async function* messages(socket: Socket) {
  let buffered = Buffer.alloc(0);
  for await (const chunk of socket) {
    buffered = Buffer.concat([buffered, chunk]);
    while (buffered.length >= 5 && buffered.length >= 1 + buffered.readInt32BE(1)) {
      const length = buffered.readInt32BE(1);
      yield { type: String.fromCharCode(buffered[0]), body: buffered.subarray(5, length + 1) };
      buffered = buffered.subarray(length + 1);
    }
  }
}

function message(type: string, body: Buffer): Buffer {
  const prefix = Buffer.alloc(5);
  prefix.write(type);
  prefix.writeInt32BE(body.length + 4, 1);
  return Buffer.concat([prefix, body]);
}

async function postgres() {
  const current = journey();
  const socket = connect(6432, '127.0.0.1');
  await once(socket, 'connect');
  const parameters = Buffer.from(`user\0any\0database\0${current.projectId}/${current.databaseId}\0\0`);
  const prefix = Buffer.alloc(8);
  prefix.writeInt32BE(parameters.length + 8);
  prefix.writeInt32BE(196608, 4);
  let admitted!: (value: boolean) => void;
  const connected = new Promise<boolean>(resolve => { admitted = resolve; });
  const errors: string[] = [];
  const results = new EventEmitter();
  const closed = (async () => {
    for await (const packet of messages(socket)) {
      if (packet.type === 'R' && packet.body.readInt32BE() === 3) {
        socket.write(message('p', Buffer.from(current.apiKey + '\0')));
      }
      if (packet.type === 'Z') { admitted(true); results.emit('ready'); }
      if (packet.type === 'D') results.emit('row', packet.body.subarray(6).toString());
      if (packet.type === 'E') {
        errors.push(...packet.body.toString().split('\0').filter(Boolean));
        admitted(false);
      }
    }
    admitted(false);
    return errors;
  })();
  socket.write(Buffer.concat([prefix, parameters]));
  return {
    socket, connected, closed,
    begin: async () => {
      const ready = once(results, 'ready');
      socket.write(message('Q', Buffer.from('BEGIN\0')));
      await ready;
    },
    query: async () => {
      const row = once(results, 'row');
      socket.write(message('Q', Buffer.from('SELECT 42\0')));
      return (await row)[0] as string;
    },
  };
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
  const closed = raw.closing.then(event => { raw.socket.destroy(); return event; });
  return { ...raw, closed };
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

      first.socket.write(Buffer.from('Q'));
      fragmented.socket.write(Buffer.from([1]));
      const [pgPartial, wsPartial] = await Promise.all([first.closed, fragmented.closed]);
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

test('transaction silence and partial input close both protocols with the transaction timeout', async ({ request }) => {
  await onLocalStack(async () => {
    const credential = await token(request, 'sql:read sql:write');
    for (const partial of [false, true]) {
      const pg = await postgres();
      const ws = await rawSqlSocket('http://127.0.0.1:8080' + path() + '/sql/ws', credential);
      try {
        expect(await pg.connected).toBe(true);
        const result = once(ws.events, 'message');
        const started = performance.now();
        ws.socket.write(frame(1, Buffer.from(JSON.stringify({ id: 1, type: 'begin' }))));
        await pg.begin();
        expect((await result)[0]).toMatchObject({ type: 'result', transaction: true });
        const error = once(ws.events, 'message');
        if (partial) {
          pg.socket.write(Buffer.from('Q'));
          ws.socket.write(Buffer.from([1]));
        }
        const [pgErrors, wsError, close] = await Promise.all([pg.closed, error, ws.closing]);
        expect(performance.now() - started).toBeLessThan(8000);
        expect(pgErrors).toContain('C57014');
        expect(pgErrors).toContain('Mthe transaction expired: it was open for more than 5 seconds and was rolled back');
        expect(wsError[0]).toMatchObject({ type: 'error', error: { code: 'query_canceled', sqlstate: '57014' } });
        expect(close).toEqual({ code: 1008, reason: 'the transaction expired: it was open for more than 5 seconds and was rolled back' });
      } finally {
        pg.socket.destroy();
        ws.socket.destroy();
        await Promise.all([pg.closed, ws.closed]);
      }
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
