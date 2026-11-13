import { connect } from 'node:net';
import { request as upgrade } from 'node:http';
import { randomBytes } from 'node:crypto';
import { EventEmitter } from 'node:events';
import type { Socket } from 'node:net';
import { once } from 'node:events';
import { readFileSync } from 'node:fs';
import { connect as peer } from 'node:http2';
import { join } from 'node:path';
import { expect, test, type APIRequestContext } from '@playwright/test';
import { auth, onLocalStack, stateDir } from '../support/stack';
import { journey, saveSecret } from '../support/state';

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
      if (packet.type === 'Z') admitted(true);
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

function frame(opcode: number, payload: Buffer): Buffer {
  expect(payload.length).toBeLessThan(126);
  const mask = randomBytes(4);
  const masked = Buffer.from(payload);
  for (let i = 0; i < masked.length; i++) masked[i] ^= mask[i % 4];
  return Buffer.concat([Buffer.from([128 | opcode, 128 | payload.length]), mask, masked]);
}

async function partialWebsocket(credential: string) {
  const request = upgrade('http://127.0.0.1:8080' + path() + '/sql/ws', {
    headers: {
      connection: 'Upgrade', upgrade: 'websocket', 'sec-websocket-version': '13',
      'sec-websocket-key': randomBytes(16).toString('base64'), 'sec-websocket-protocol': 'terrascale.sql.v1',
      authorization: 'Bearer ' + credential,
    },
  });
  request.on('response', response => request.destroy(new Error('upgrade refused with ' + response.statusCode)));
  const upgraded = once(request, 'upgrade');
  request.end();
  const [response, socket, head] = await upgraded;
  expect(response.statusCode).toBe(101);
  const events = new EventEmitter();
  const closed = once(events, 'closed').then(([event]) => event as { code: number; reason: string });
  let buffered = Buffer.alloc(0);
  socket.on('data', (chunk: Buffer) => {
    buffered = Buffer.concat([buffered, chunk]);
    while (buffered.length >= 2) {
      const short = buffered[1] & 127;
      const prefix = short === 126 ? 4 : short === 127 ? 10 : 2;
      if (buffered.length < prefix) return;
      const length = short === 126 ? buffered.readUInt16BE(2) : short === 127 ? Number(buffered.readBigUInt64BE(2)) : short;
      if (buffered.length < prefix + length) return;
      const opcode = buffered[0] & 15;
      const payload = buffered.subarray(prefix, prefix + length);
      buffered = buffered.subarray(prefix + length);
      if (opcode === 1) events.emit('message', JSON.parse(payload.toString()));
      if (opcode === 8) {
        socket.destroy();
        events.emit('closed', { code: payload.readUInt16BE(), reason: payload.subarray(2).toString() });
      }
    }
  });
  socket.on('error', (error: Error) => events.emit('error', error));
  socket.on('close', () => events.emit('closed', { code: 1006, reason: 'transport closed' }));
  const result = once(events, 'message');
  if (head.length > 0) socket.unshift(head);
  socket.write(frame(1, Buffer.from(JSON.stringify({ id: 1, type: 'execute', statements: [{ sql: 'SELECT 1' }] }))));
  expect((await result)[0].results[0].rows).toEqual([[1]]);
  return { socket: socket as Socket, closed };
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
