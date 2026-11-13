import { connect } from 'node:net';
import { connect as secure, type TLSSocket } from 'node:tls';
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

async function* messages(socket: TLSSocket) {
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
  const transport = connect(5432, 'pg.terrascale.tech');
  const supported = once(transport, 'data');
  transport.write(Buffer.from([0, 0, 0, 8, 4, 210, 22, 47]));
  expect(((await supported)[0] as Buffer).toString()).toBe('S');
  const socket = secure({ socket: transport, servername: 'pg.terrascale.tech' });
  await once(socket, 'secureConnect');
  const parameters = Buffer.from(`user\0any\0database\0${current.projectId}/${current.databaseId}\0\0`);
  const prefix = Buffer.alloc(8);
  prefix.writeInt32BE(parameters.length + 8);
  prefix.writeInt32BE(196608, 4);
  let admitted!: (value: boolean) => void;
  const connected = new Promise<boolean>(resolve => { admitted = resolve; });
  const errors: string[] = [];
  const closed = (async () => {
    for await (const packet of messages(socket)) {
      if (packet.type === 'R' && packet.body.readInt32BE() === 3) {
        socket.write(message('p', Buffer.from(current.apiKey + '\0')));
      }
      if (packet.type === 'Z') admitted(true);
      if (packet.type === 'E') {
        errors.push(...packet.body.toString().split('\0').filter(Boolean));
        admitted(false);
      }
    }
    admitted(false);
    return errors;
  })();
  socket.write(Buffer.concat([prefix, parameters]));
  return { socket, connected, closed };
}

async function websocket(credential: string) {
  const socket = new WebSocket(api.replace('https:', 'wss:') + path() + '/sql/ws', 'terrascale.sql.v1');
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

test('Postgres and WebSocket share the organization session cap and release idle sessions', async ({ request }) => {
  const credential = await token(request, 'sql:read sql:write');
  const read = await token(request, 'sql:read');
  const sockets: Awaited<ReturnType<typeof websocket>>[] = [];
  const clients: Awaited<ReturnType<typeof postgres>>[] = [];
  try {
    const first = await postgres();
    clients.push(first);
    expect(await first.connected).toBe(true);
    for (let i = 0; i < 7; i++) {
      const accepted = await websocket(i % 2 === 0 ? credential : read);
      sockets.push(accepted);
      expect(accepted.reply.type).toBe('result');
    }

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
    const [pgIdle, ...wsIdle] = await Promise.all([first.closed, ...sockets.slice(0, 7).map(client => client.closed)]);
    expect(pgIdle).toContain('C57P05');
    expect(pgIdle).toContain('Msession idle timeout exceeded');
    for (const event of wsIdle) {
      expect(event.code).toBe(1008);
      expect(event.reason).toBe('session idle timeout exceeded');
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
    for (const client of clients) if (!client.socket.destroyed) client.socket.end(message('X', Buffer.alloc(0)));
    for (const client of sockets) if (client.socket.readyState === WebSocket.OPEN) client.socket.close();
    await Promise.all([...clients.map(client => client.closed), ...sockets.map(client => client.closed)]);
  }

  await onLocalStack(async () => {
    const enterprise: Awaited<ReturnType<typeof websocket>>[] = [];
    try {
      await setPlan('enterprise');
      for (let i = 0; i < 128; i++) {
        const accepted = await websocket(credential);
        enterprise.push(accepted);
        expect(accepted.reply.type, `Enterprise session ${i + 1}`).toBe('result');
      }
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
