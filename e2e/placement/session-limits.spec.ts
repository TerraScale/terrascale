import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { EventEmitter, once } from 'node:events';
import { connect, type Socket } from 'node:net';
import { connect as tlsConnect } from 'node:tls';
import { expect, type APIRequestContext } from '@playwright/test';
import { rawSqlSocket, frame } from '../support/sql-socket';
import { postgres, message } from '../support/postgres-wire';
import { localPort, production, stateDir } from '../support/stack';
import { journey, saveSecret } from '../support/state';
import { test, gru, fra, project, database, name, json, removeDatabase, sql, converged, replicated, type Cells, type Database } from './support';

test.describe.configure({ mode: 'serial' });

test.skip(production || process.env.E2E_REGIONS !== '2', 'placement requires the local stack started with E2E_REGIONS=2');

const peers = ['gru', 'fra'] as const;
type Peer = typeof peers[number];

function node(region: Peer) {
  return region === 'gru'
    ? { origin: gru, client: localPort('terrabase', 8080), metrics: localPort('metrics', 9091), postgres: localPort('postgres', 6432) }
    : { origin: fra, client: localPort('terrabase_fra', 8180), metrics: localPort('metrics_fra', 9191), postgres: localPort('postgres_fra', 6532) };
}

const socketUrl = (region: Peer, id: string, projectId?: string) => `http://127.0.0.1:${node(region).client}${database(id, projectId)}/sql/ws`;

async function placed(cells: Cells, prefix: string): Promise<Database> {
  const created = await json<Database>(await cells.post(`${gru}${project()}/databases`, {
    data: { name: name(prefix), regions: ['gru', 'fra'] },
  }), 201);
  for (const region of peers) await converged(cells, node(region).origin, created.id, 'SELECT 1', rows => rows.length === 1);
  return created;
}

async function credential(request: APIRequestContext) {
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

async function activeConnections(request: APIRequestContext, region: Peer): Promise<number> {
  const response = await request.get(`http://127.0.0.1:${node(region).metrics}/metrics`);
  expect(response.status()).toBe(200);
  const rows = (await response.text()).split('\n').filter(line => /^kestrel_active_connections\{/.test(line) && line.includes(`server_port="${node(region).postgres}"`));
  return rows.reduce((total, line) => total + Number(line.split(' ').at(-1)), 0);
}

for (const region of peers) {
  test(`an unauthenticated SQL WebSocket in ${region} closes after five seconds and an authenticated one answers`, async ({ cells, request }) => {
    const created = await placed(cells, 'session-auth');
    const url = socketUrl(region, created.id);
    try {
      for (let sample = 0; sample < 3; sample++) {
        const ws = await rawSqlSocket(url);
        try {
          expect(await ws.closing).toEqual({ code: 1008, reason: 'authenticate within 5 seconds' });
        } finally {
          ws.socket.destroy();
          await ws.closed;
        }
      }
      const ws = await rawSqlSocket(url, await credential(request));
      try {
        expect((await execute(ws, 'SELECT 42')).results[0].rows).toEqual([[42]]);
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

  test(`a SQL WebSocket in ${region} closes and rolls back a transaction open for more than 5 seconds`, async ({ cells, request }) => {
    const created = await placed(cells, 'session-expiry');
    let ws: Awaited<ReturnType<typeof rawSqlSocket>> | undefined;
    let replacement: Awaited<ReturnType<typeof rawSqlSocket>> | undefined;
    try {
      await sql(cells, gru, created.id, 'CREATE TABLE placement_rows (value INTEGER)');
      await sql(cells, gru, created.id, 'INSERT INTO placement_rows VALUES (1)');
      await replicated(cells, node(region).origin, created.id, 1);
      const token = await credential(request);
      ws = await rawSqlSocket(socketUrl(region, created.id), token);
      const began = performance.now();
      expect((await execute(ws, 'BEGIN')).transaction).toBe(true);
      expect(await execute(ws, 'INSERT INTO placement_rows VALUES (2)')).toMatchObject({ type: 'result', transaction: true });
      const expired = once(ws.events, 'message');
      const [wsError, close] = await Promise.all([expired, ws.closing]);
      expect(wsError[0]).toMatchObject({ type: 'error', transaction: false, error: {
        code: 'query_canceled', sqlstate: '57014', message: 'the transaction expired: it was open for more than 5 seconds and was rolled back',
      } });
      expect(close).toEqual({ code: 1008, reason: 'the transaction expired: it was open for more than 5 seconds and was rolled back' });
      expect(performance.now() - began).toBeGreaterThan(5000);
      replacement = await rawSqlSocket(socketUrl(region, created.id), token);
      expect(await execute(replacement, 'SELECT value FROM placement_rows')).toMatchObject({ type: 'result', transaction: false, results: [{ rows: [[1]] }] });
      expect((await sql(cells, gru, created.id, 'SELECT value FROM placement_rows')).results[0].rows).toEqual([[1]]);
    } finally {
      ws?.socket.destroy();
      replacement?.socket.destroy();
      if (ws) await ws.closed;
      if (replacement) await replacement.closed;
      await removeDatabase(cells, created.id);
    }
  });

  test(`a WebSocket in ${region} answers 300 pipelined calls in order`, async ({ cells, request }) => {
    const created = await placed(cells, 'ws-pipelined');
    try {
      const ws = await rawSqlSocket(socketUrl(region, created.id), await credential(request));
      try {
        const answers: { id: number; type: string; error?: { code: string } }[] = [];
        const last = new Promise<void>(resolve => ws.events.on('message', (answer: { id: number; type: string; error?: { code: string } }) => {
          answers.push(answer);
          if (answer.id === 300) resolve();
        }));
        ws.socket.write(Buffer.concat(Array.from({ length: 300 }, (_, index) =>
          frame(1, Buffer.from(JSON.stringify({ id: index + 1, type: 'execute', statements: [{ sql: 'SELECT ' + (index + 1) }] }))))));
        expect(await Promise.race([last.then(() => 'answered'), ws.closing.then(close => JSON.stringify(close))]), region).toBe('answered');
        expect(answers.map(answer => answer.id)).toEqual(Array.from({ length: 300 }, (_, index) => index + 1));
        expect(answers.filter(answer => answer.type !== 'result'), region).toEqual([]);
        expect((await execute(ws, 'SELECT 1')).results[0].rows).toEqual([[1]]);
      } finally {
        ws.socket.destroy();
        await ws.closed;
      }
    } finally {
      await removeDatabase(cells, created.id);
    }
  });
}

test('verified platform ADO.NET transactions complete and release their sessions', async () => {
  const runner = spawn('dotnet', [join(process.env.E2E_BUILD!, 'social/Journey.Social.dll'), 'platform-sessions'], {
    env: { ...process.env, E2E_STATE: stateDir }, stdio: ['pipe', 'pipe', 'pipe'],
  });
  const exited = once(runner, 'exit');
  let stderr = '';
  runner.stderr.on('data', chunk => { stderr += chunk; });
  try {
    const ready = await Promise.race([
      once(runner.stdout, 'data').then(([data]) => data.toString()),
      exited.then(([code]) => { throw new Error('platform ADO.NET runner exited ' + code + ': ' + stderr); }),
    ]);
    expect(ready).toContain('16 platform ADO.NET transactions completed');
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

test('a pipelined extended-protocol batch of any size runs the same in gru and fra and converges', async ({ cells }) => {
  const created = await placed(cells, 'pipelined');
  try {
    await sql(cells, gru, created.id, 'CREATE TABLE pipelined (value INTEGER)');
    await converged(cells, fra, created.id, 'SELECT COUNT(*) FROM pipelined', rows => rows.length === 1);
    let committed = 0;
    for (const region of peers) {
      const port = node(region).postgres;
      for (const count of [80, 86, 100, 120]) {
        const client = await wire(port, created.id);
        expect(client.opened).toBe('Z');
        const before = client.types.length;
        const ready = client.next('Z');
        client.socket.write(insertBatch(count, port * 1_000_000 + count * 1000));
        const label = `${count} statements in ${region}: ${client.errors.join(';')}`;
        expect((await ready).type, label).toBe('Z');
        expect(client.errors, label).toEqual([]);
        expect(client.types.slice(before).filter(type => type === 'C'), label).toHaveLength(count);
        committed += count;
        const again = client.next('Z');
        client.socket.write(message('Q', Buffer.from('SELECT 1\0')));
        expect((await again).type, label).toBe('Z');
        client.socket.destroy();
        await client.closed;
      }
    }
    for (const region of peers) {
      await converged(cells, node(region).origin, created.id, 'SELECT COUNT(*) FROM pipelined', rows => rows[0]?.[0] === committed);
    }
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('a Postgres client that reads a large answer slowly but steadily keeps its session in gru and fra', async ({ cells }) => {
  test.setTimeout(150_000);
  const created = await placed(cells, 'slow-reader');
  const large = 'SELECT hex(zeroblob(30720))';
  try {
    const viaPostgres = async (region: Peer) => {
      const port = node(region).postgres;
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
        expect(delivered.type, `Postgres in ${region} after ${Math.round(performance.now() - began)} ms, ${client.socket.bytesRead} bytes: ${client.errors.join(';')}`).toBe('D');
        expect(delivered.body.readInt32BE(2)).toBe(61_440);
        expect((await ready).type).toBe('Z');
        const again = client.next('Z');
        client.socket.write(message('Q', Buffer.from('SELECT 7\0')));
        expect((await again).type).toBe('Z');
        expect(client.errors).toEqual([]);
        client.socket.destroy();
        await client.closed;
        console.log('Postgres ' + region + ': delivered 61440 bytes and reused session after ' + Math.round(performance.now() - began) + ' ms');
      } finally {
        client.socket.destroy();
        await proxy.stop();
      }
    };
    await Promise.all(peers.map(viaPostgres));
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('a WebSocket client reading an answer slowly keeps its session in gru and fra', async ({ cells, request }) => {
  test.setTimeout(75_000);
  const created = await placed(cells, 'ws-slow-reader');
  try {
    const token = await credential(request);
    await Promise.all(peers.map(async region => {
      const proxy = await slowReader(node(region).client, 24_000);
      const ws = await rawSqlSocket('http://127.0.0.1:' + proxy.port + database(created.id) + '/sql/ws', token);
      try {
        proxy.start();
        const began = performance.now();
        const response = once(ws.events, 'message');
        ws.socket.write(frame(1, Buffer.from(JSON.stringify({ id: 1, type: 'execute', statements: [{ sql: 'SELECT hex(zeroblob(180000))' }] }))));
        const result = await Promise.race([response.then(([answer]) => answer), ws.closing.then(close => { throw new Error('WebSocket in ' + region + ' closed: ' + JSON.stringify(close)); })]);
        expect(result.type).toBe('result');
        expect(result.results[0].rows[0][0]).toHaveLength(360_000);
        expect(/^0+$/.test(result.results[0].rows[0][0])).toBe(true);
        expect((await execute(ws, 'SELECT 7')).results[0].rows).toEqual([[7]]);
        console.log('WebSocket ' + region + ': delivered 360000 bytes and reused session after ' + Math.round(performance.now() - began) + ' ms');
      } finally {
        ws.socket.destroy();
        await proxy.stop();
      }
    }));
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('a Postgres answer that makes no transport progress closes its session in gru and fra within the write budget', async ({ cells, request }) => {
  test.setTimeout(60_000);
  const created = await placed(cells, 'stopped-reader');
  const proxies: Awaited<ReturnType<typeof slowReader>>[] = [];
  const clients: Awaited<ReturnType<typeof wire>>[] = [];
  try {
    const before = await Promise.all(peers.map(region => activeConnections(request, region)));
    for (const region of peers) {
      const proxy = await slowReader(node(region).postgres, 0);
      proxies.push(proxy);
      const client = await wire(proxy.port, created.id);
      clients.push(client);
      expect(client.opened).toBe('Z');
    }
    await expect.poll(() => Promise.all(peers.map(region => activeConnections(request, region)))).toEqual(before.map(count => count + 1));
    for (const proxy of proxies) proxy.start();
    const began = performance.now();
    for (const client of clients) client.socket.write(message('Q', Buffer.from('SELECT hex(zeroblob(1500000))\0')));
    await expect.poll(() => Promise.all(peers.map(region => activeConnections(request, region))),
      { timeout: 40_000, message: 'a blocked answer releases its session after 30 seconds without progress' }).toEqual(before);
    console.log('Stopped readers released sessions after ' + Math.round(performance.now() - began) + ' ms');
  } finally {
    for (const client of clients) client.socket.destroy();
    await Promise.all(proxies.map(proxy => proxy.stop()));
    await removeDatabase(cells, created.id);
  }
});

test('thirty-two idle or slow sockets cannot close the postgres port', async ({ cells }) => {
  const created = await placed(cells, 'idle-sockets');
  const port = node('gru').postgres;
  const live = await postgres(port, created.id);
  try {
    expect(await live.connected).toBe(true);
    expect(await live.query()).toBe('42');
    for (const open of [
      () => silentSockets(port, '127.0.0.1'),
      () => startupSilence(port, '127.0.0.1', created.id),
      () => silentSockets(5432, 'pg.terrascale.tech'),
      () => stalledTls(),
      () => startupSilence(5432, 'pg.terrascale.tech', created.id, true),
    ]) {
      const sockets = await open();
      try {
        expect(sockets).toHaveLength(32);
        expect(sockets.every(socket => !socket.destroyed)).toBe(true);
        expect(await live.query()).toBe('42');
        const fresh = await postgres(port, created.id);
        try {
          expect(await fresh.connected).toBe(true);
          expect(await fresh.query()).toBe('42');
        } finally {
          fresh.socket.destroy();
          await fresh.closed;
        }
        expect(await publicRow(created.id)).toBe('42');
      } finally {
        for (const socket of sockets) socket.destroy();
      }
    }
  } finally {
    live.socket.destroy();
    await live.closed;
    await removeDatabase(cells, created.id);
  }
});

const handshakeSlots = 32;
const sslRequest = Buffer.from([0, 0, 0, 8, 4, 210, 22, 47]);

function startupPacket(databaseId: string): Buffer {
  const parameters = Buffer.from(`user\0any\0database\0${journey().projectId}/${databaseId}\0\0`);
  const prefix = Buffer.alloc(8);
  prefix.writeInt32BE(parameters.length + 8);
  prefix.writeInt32BE(196608, 4);
  return Buffer.concat([prefix, parameters]);
}

async function openMany(open: () => Promise<Socket>): Promise<Socket[]> {
  const opened = await Promise.allSettled(Array.from({ length: handshakeSlots }, () => open()));
  const sockets = opened.flatMap(result => result.status === 'fulfilled' ? [result.value] : []);
  const rejected = opened.find(result => result.status === 'rejected');
  if (rejected?.status === 'rejected') {
    for (const socket of sockets) socket.destroy();
    throw rejected.reason;
  }
  return sockets;
}

async function silentSockets(port: number, host: string): Promise<Socket[]> {
  return openMany(async () => {
    const socket = connect(port, host);
    socket.on('error', () => {});
    await once(socket, 'connect');
    return socket;
  });
}

async function startupSilence(port: number, host: string, databaseId: string, tls = false): Promise<Socket[]> {
  return openMany(async () => {
    const socket = tls ? await edgeSocket() : await plainSocket(port, host);
    await untilPassword(socket, startupPacket(databaseId));
    return socket;
  });
}

async function stalledTls(): Promise<Socket[]> {
  return openMany(async () => {
    const socket = connect(5432, 'pg.terrascale.tech');
    socket.on('error', () => {});
    await once(socket, 'connect');
    const answered = once(socket, 'data');
    socket.write(sslRequest);
    const [chunk] = await answered as [Buffer];
    expect(chunk.toString()).toBe('S');
    return socket;
  });
}

async function plainSocket(port: number, host: string): Promise<Socket> {
  const socket = connect(port, host);
  socket.on('error', () => {});
  await once(socket, 'connect');
  return socket;
}

async function edgeSocket(): Promise<Socket> {
  const raw = await plainSocket(5432, 'pg.terrascale.tech');
  const answered = once(raw, 'data');
  raw.write(sslRequest);
  const [chunk] = await answered as [Buffer];
  expect(chunk.toString()).toBe('S');
  const secure = tlsConnect({
    socket: raw,
    servername: 'pg.terrascale.tech',
    ca: readFileSync(join(stateDir, 'pki', 'ca.pem')),
    ALPNProtocols: ['postgresql'],
  });
  secure.on('error', () => {});
  await once(secure, 'secureConnect');
  return secure;
}

async function untilPassword(socket: Socket, startup: Buffer): Promise<void> {
  let buffered = Buffer.alloc(0);
  const asked = new Promise<void>((resolve, reject) => {
    const fail = (error: Error) => {
      socket.off('data', onData);
      reject(error);
    };
    const onData = (chunk: Buffer) => {
      buffered = Buffer.concat([buffered, chunk]);
      while (buffered.length >= 5 && buffered.length >= 1 + buffered.readInt32BE(1)) {
        const length = buffered.readInt32BE(1);
        const type = String.fromCharCode(buffered[0]);
        const body = buffered.subarray(5, length + 1);
        buffered = buffered.subarray(length + 1);
        if (type === 'E') {
          fail(new Error(body.toString().split('\0').filter(Boolean).join(' | ')));
          return;
        }
        if (type === 'R' && body.length >= 4 && body.readInt32BE() === 3) {
          socket.off('data', onData);
          socket.pause();
          resolve();
          return;
        }
      }
    };
    socket.on('data', onData);
    socket.once('close', () => fail(new Error('closed before the password request')));
  });
  socket.write(startup);
  await asked;
}

async function publicRow(databaseId: string): Promise<string> {
  const socket = await edgeSocket();
  const current = journey();
  let buffered = Buffer.alloc(0);
  let ready = false;
  const row = new Promise<string>((resolve, reject) => {
    socket.on('data', (chunk: Buffer) => {
      buffered = Buffer.concat([buffered, chunk]);
      while (buffered.length >= 5 && buffered.length >= 1 + buffered.readInt32BE(1)) {
        const length = buffered.readInt32BE(1);
        const type = String.fromCharCode(buffered[0]);
        const body = buffered.subarray(5, length + 1);
        buffered = buffered.subarray(length + 1);
        if (type === 'R' && body.readInt32BE() === 3) socket.write(message('p', Buffer.from(current.apiKey + '\0')));
        if (type === 'Z' && !ready) {
          ready = true;
          socket.write(message('Q', Buffer.from('SELECT 42\0')));
        }
        if (type === 'D' && ready) resolve(body.subarray(6).toString());
        if (type === 'E') reject(new Error(body.toString().split('\0').filter(Boolean).join(' | ')));
      }
    });
    socket.once('close', () => reject(new Error('public postgres closed before a row')));
  });
  socket.write(startupPacket(databaseId));
  try {
    return await row;
  } finally {
    socket.destroy();
  }
}
