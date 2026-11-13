import { createHash, randomBytes } from 'node:crypto';
import { spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import { closeSync, mkdirSync, mkdtempSync, openSync, readFileSync, renameSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { connect, createServer, type IncomingHttpHeaders, type ServerHttp2Session, type ServerHttp2Stream } from 'node:http2';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';
import { expect, type APIRequestContext, type TestInfo } from '@playwright/test';
import { saveSecret } from '../support/state';
import { e2eRoot, stateDir } from '../support/stack';
import { Nats, putKey, seal } from '../replication/push.mjs';

export type Node = { name: string; region: string; address: string; port: number; metrics: number; postgres: number };
export type Answer = { status: number; body: string; retryAfter?: string };
export type StandIn = { asked: () => number; answeredAt: () => number; close: () => Promise<void> };

export const portOffset = Number(process.env.TEST_PARALLEL_INDEX ?? '0') * 100;
export const internalPort = 18281 + portOffset;
export const run = randomBytes(3).toString('hex');
export const lone = (letter: string, octet: number, port: number, partners: 'near' | 'far'): Record<'node' | 'first' | 'second', Node> => ({
  node: { name: `clock-${letter}-${run}`, region: `gru${letter}-${run}`, address: `127.0.0.${octet}`, port: port + portOffset, metrics: port + 1000 + portOffset, postgres: port - 2000 + portOffset },
  first: { name: `clock-${letter}1-${run}`, region: partners === 'near' ? `gru${letter}-${run}` : `fra${letter}-${run}`, address: `127.0.0.${octet + 1}`, port: 0, metrics: 0, postgres: 0 },
  second: { name: `clock-${letter}2-${run}`, region: partners === 'near' ? `gru${letter}-${run}` : `sjc${letter}-${run}`, address: `127.0.0.${octet + 2}`, port: 0, metrics: 0, postgres: 0 },
});

const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
export function id(): string {
  let time = Date.now();
  let head = '';
  for (let index = 0; index < 10; index++) {
    head = alphabet[time % 32] + head;
    time = Math.floor(time / 32);
  }
  return head + Array.from(randomBytes(16), (value) => alphabet[value % 32]).join('');
}

export type Transaction = { id: string; region: string; hlc: { physicalMilliseconds: number; logical: number; nodeId: string }; changes: unknown[] };

export async function cluster(request: APIRequestContext, members: Node[], label: string, broker = false) {
  const root = mkdtempSync(join(tmpdir(), `terrabase-clock-trust-${label}-`));
  const library = join(root, 'libclock-replay.so');
  const masterKey = randomBytes(32).toString('base64');
  const r2Key = randomBytes(16).toString('hex');
  const r2Secret = randomBytes(32).toString('hex');
  const account = join(stateDir, 'r2-accounts', createHash('sha256').update(r2Key).digest('hex'));
  mkdirSync(join(stateDir, 'r2-accounts'), { recursive: true, mode: 0o700 });
  writeFileSync(account, `${r2Key}\n${r2Secret}\n`, { mode: 0o600 });
  saveSecret(`clock-r2-key-${label}`, r2Key);
  saveSecret(`clock-r2-secret-${label}`, r2Secret);
  const started = new Set<Node>();
  const peers = join(root, 'peers');
  const peerSecret = randomBytes(32).toString('hex');
  saveSecret(`clock-trust-peer-${label}`, peerSecret);
  saveSecret(`clock-trust-master-${label}`, masterKey);
  const cloudflareToken = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[9];
  const running = new Map<Node, ChildProcess>();
  const logs: number[] = [];
  const readiness = new Map<Node, string>();
  const observations: string[] = [];
  const opened: number[] = [];
  const standIns = new Set<StandIn>();
  const brokerPort = members[0].port + 5;
  const natsToken = randomBytes(24).toString('hex');
  if (broker) saveSecret(`clock-nats-${label}`, natsToken);
  const dataKey = randomBytes(32);
  const keyId = id();
  let nats: Nats | undefined;
  let brokerProcess: ChildProcess | undefined;

  const set = (node: Node, offset: number, status = 0, error = 1000) => {
    writeFileSync(join(root, `${node.name}.next`), `${offset} ${status} ${error}\n`);
    renameSync(join(root, `${node.name}.next`), join(root, node.name));
  };
  const stepAfterKernel = (node: Node, offset: number) => writeFileSync(join(root, `${node.name}.step`), `${offset} 0 1000\n`);
  const setPeers = (configured: Node[]) => writeFileSync(peers, configured.map((node) => `${node.name} ${node.region} ${node.address}\n`).join(''));
  const log = (node: Node) => readFileSync(join(root, `${node.name}.log`), 'utf8');
  const metric = async (node: Node, name: string, label = '') => {
    expect(running.get(node)?.exitCode, `${node.name} is running`).toBeNull();
    const response = await request.get(`http://127.0.0.1:${node.metrics}/metrics`);
    expect(response.ok()).toBe(true);
    const lines = (await response.text()).split('\n').filter((line) => line.startsWith(`${name}{`) || line.startsWith(`${name} `));
    const line = lines.find((candidate) => candidate.includes(label));
    return line ? Number(line.slice(line.lastIndexOf(' ') + 1)) : 0;
  };
  const allowed = (node: Node) => metric(node, 'terrabase_clock_local_writes_allowed');
  const mode = async (node: Node) => {
    expect(running.get(node)?.exitCode, `${node.name} is running`).toBeNull();
    const response = await request.get(`http://127.0.0.1:${node.metrics}/metrics`);
    expect(response.ok()).toBe(true);
    const line = (await response.text()).split('\n').find((candidate) => candidate.startsWith('terrabase_clock_mode{') && candidate.endsWith(' 1'));
    return `${line?.match(/mode="([a-z_]+)"/)?.[1]}/${line?.match(/reason="([a-z_]+)"/)?.[1]}`;
  };
  const clockState = async (node: Node) => {
    const at = new Date().toISOString();
    const response = await request.get(`http://127.0.0.1:${node.metrics}/metrics`);
    expect(response.ok()).toBe(true);
    const lines = (await response.text()).split('\n');
    const value = (name: string) => {
      const line = lines.find((candidate) => candidate.startsWith(`${name}{`) || candidate.startsWith(`${name} `));
      return line ? Number(line.slice(line.lastIndexOf(' ') + 1)) : 0;
    };
    const mode = lines.find((line) => line.startsWith('terrabase_clock_mode{') && line.endsWith(' 1'));
    return {
      at, allowed: value('terrabase_clock_local_writes_allowed'), rounds: value('terrabase_clock_agreeing_rounds'),
      mode: `${mode?.match(/mode="([a-z_]+)"/)?.[1]}/${mode?.match(/reason="([a-z_]+)"/)?.[1]}`,
      steps: value('terrabase_clock_steps_total'), answering: value('terrabase_clock_peers_answering'), agreeing: value('terrabase_clock_peers_agreeing'),
    };
  };
  const transitions = (node: Node) => log(node).split('\n').filter((line) => line.includes('Clock trust mode')).slice(-2)
    .map((line) => JSON.parse(line.slice(line.indexOf('{')))).map((entry: { Mode: string; Reason: string }) => `${entry.Mode}/${entry.Reason}`);
  const previousBuild = (headers: IncomingHttpHeaders) => headers['x-terrabase-peer-credential'] === peerSecret
    ? { status: 404, body: { error: { code: 'unknown_route', message: `no route matches ${headers[':method']} ${headers[':path']}; see the API reference at /scalar/` } } }
    : { status: 401, body: { error: { code: 'invalid_key' } } };
  const proxy = () => ({ status: 404, body: undefined });
  const standIn = async (node: Node, answer: (headers: IncomingHttpHeaders) => { status: number; body: unknown }) => {
    let asked = 0;
    let answeredAt = 0;
    const sessions = new Set<ServerHttp2Session>();
    const server = createServer();
    server.on('session', (session) => {
      sessions.add(session);
      session.on('close', () => sessions.delete(session));
    });
    server.on('stream', (stream: ServerHttp2Stream, headers: IncomingHttpHeaders) => {
      asked++;
      const { status, body } = answer(headers);
      if (status === 200) answeredAt = performance.now();
      stream.respond({ ':status': status, ...(body === undefined ? {} : { 'content-type': 'application/json' }) });
      stream.end(body === undefined ? undefined : JSON.stringify(body));
    });
    server.listen(internalPort, node.address);
    await once(server, 'listening');
    const handle: StandIn = {
      asked: () => asked,
      answeredAt: () => answeredAt,
      close: async () => {
        standIns.delete(handle);
        for (const session of sessions) session.destroy();
        const closed = once(server, 'close');
        server.close();
        await closed;
      },
    };
    standIns.add(handle);
    return handle;
  };
  const holds = async (node: Node, label: string, expected: string, peers: StandIn[], rounds: number) => {
    const from = peers.map((peer) => peer.asked());
    const seen = new Set<string>();
    await expect.poll(async () => {
      seen.add(`${await mode(node)} ${await allowed(node)}`);
      return Math.min(...peers.map((peer, index) => peer.asked() - from[index]));
    }, { message: label }).toBeGreaterThanOrEqual(rounds);
    expect([...seen], label).toEqual([expected]);
  };
  const clockPeer = (peer: { offset: number | null }) => (headers: IncomingHttpHeaders) => headers[':path'] !== '/v1/sync/clock'
    ? { status: 404, body: undefined }
    : peer.offset === null ? { status: 503, body: undefined } : { status: 200, body: { utcMilliseconds: Date.now() + peer.offset } };
  const refused = (answer: Answer, label: string) => {
    expect(answer.status, `${label}: ${answer.body}`).toBe(409);
    expect(JSON.parse(answer.body).error.code, label).toBe('serialization_conflict');
    expect(answer.retryAfter, label).toBe('1');
  };
  const start = async (node: Node, connected = broker) => {
    const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) => !/^(TS_|TERRABASE_|AWS_|BUCKET_NAME)/.test(name)));
    const output = openSync(join(root, `${node.name}.log`), 'a');
    logs.push(output);
    const from = readFileSync(join(root, `${node.name}.log`), 'utf8').length;
    const spawned = performance.now();
    started.add(node);
    running.set(node, spawn('dotnet', [join(build, 'terrabase.dll')], {
      env: {
        ...environment, DOTNET_ENVIRONMENT: 'Production', DOTNET_PROCESSOR_COUNT: '4',
        TS_PEERS_FILE: peers, TS_REGION: node.region, TS_NODE: node.name,
        TERRABASE_DATA_DIRECTORY: join(root, `${node.name}-data`), TERRABASE_PORT: String(node.port),
        TERRABASE_INTERNAL_PORT: String(internalPort), TERRABASE_METRICS_PORT: String(node.metrics), TERRABASE_POSTGRES_PORT: String(node.postgres),
        TERRABASE_AUTH_ISSUER: 'https://auth.terrascale.tech/terrascale', TERRABASE_AUTH_AUDIENCE: 'https://api.terrascale.tech',
        TERRABASE_PEER_SECRET: peerSecret, TERRABASE_MASTER_KEY: masterKey,
        AWS_ENDPOINT_URL_S3: 'https://journey.backups.terrascale.tech/', AWS_REGION: 'auto', BUCKET_NAME: 'journey-backups',
        AWS_ACCESS_KEY_ID: r2Key, AWS_SECRET_ACCESS_KEY: r2Secret,
        TERRABASE_FILES_S3_ENDPOINT: 'https://journey.r2.cloudflarestorage.com/', TERRABASE_FILES_S3_ACCESS_KEY_ID: r2Key,
        TERRABASE_FILES_S3_SECRET_ACCESS_KEY: r2Secret,
        TERRABASE_SHAPES_ACCESS_KEY_ID: r2Key, TERRABASE_SHAPES_SECRET_ACCESS_KEY: r2Secret, TERRABASE_SHAPES_CDN_API_TOKEN: cloudflareToken,
        ...(connected ? { TS_NATS_URL: `nats://127.0.0.1:${brokerPort}`, TERRABASE_NATS_TOKEN: natsToken } : {}),
        LD_PRELOAD: library, LD_LIBRARY_PATH: `${root}:${process.env.LD_LIBRARY_PATH ?? ''}`, E2E_CLOCK_REPLAY: join(root, node.name),
      }, stdio: ['ignore', output, output],
    }));
    await expect.poll(async () => {
      expect(running.get(node)?.exitCode, `${node.name} is running`).toBeNull();
      return request.get(`http://127.0.0.1:${node.port}/readyz`).then(async (response) => {
        const status = response.status();
        const body = await response.text();
        const metrics = status === 503 ? await request.get(`http://127.0.0.1:${node.metrics}/metrics`).then(async (answer) =>
          (await answer.text()).split('\n').filter((line) => /^terrabase_(clock_mode|catalog_verified|catalog_bucket_missing|platform_seed_pending)/.test(line)).join('\n'),
        (error: Error) => error.message) : '';
        readiness.set(node, `${status} ${body}\n${metrics}`);
        return { status, body, metrics };
      }, (error: Error) => {
        readiness.set(node, error.message);
        return { status: 0, body: error.message };
      });
    }, { message: `${node.name} ready` }).toMatchObject({ status: 200 });
    if (connected) {
      await expect.poll(() => log(node).slice(from).includes('This node holds a new home lease session'), { message: `${node.name} holds its home lease` }).toBe(true);
    }
    const ready = performance.now();
    observations.push(`${node.name} ready ${Math.round(ready - spawned)} ms after start`);
    return { spawned, ready };
  };
  const stop = async (node: Node) => {
    const child = running.get(node);
    running.delete(node);
    if (child && child.exitCode === null) {
      child.kill('SIGTERM');
      await once(child, 'exit');
    }
  };
  const opens = async (node: Node, label: string, connected = broker) => {
    const { spawned, ready } = await start(node, connected);
    await expect.poll(() => allowed(node), { message: `${label}: ${node.name} opens`, intervals: [100] }).toBe(1);
    const at = performance.now();
    opened.push(at - ready);
    observations.push(`${label}: ${node.name} trusted ${Math.round(at - ready)} ms after /readyz, ${Math.round(at - spawned)} ms after start`);
  };
  const ready = async (node: Node) => {
    const response = await request.get(`http://127.0.0.1:${node.port}/readyz`);
    expect(response.status(), `${node.name}: ${await mode(node)}; ${await response.text()}`).toBe(200);
  };
  const internal = (node: Node, method: string, path: string, body?: unknown, user?: string) => new Promise<Answer>((resolveAnswer, reject) => {
    const session = connect(`http://${node.address}:${internalPort}`);
    session.on('error', reject);
    const payload = body === undefined ? undefined : JSON.stringify(body);
    const stream = session.request({
      ':method': method, ':path': path, 'x-terrabase-peer-credential': peerSecret,
      ...(payload === undefined ? {} : { 'content-type': 'application/json', 'content-length': Buffer.byteLength(payload) }),
      ...(user ? { 'x-terrabase-user-id': user } : {}),
    });
    let status = 0;
    let text = '';
    stream.setEncoding('utf8');
    let retryAfter: string | undefined;
    stream.on('response', (headers) => { status = Number(headers[':status']); retryAfter = headers['retry-after'] as string | undefined; });
    stream.on('data', (chunk: string) => { text += chunk; });
    stream.on('error', reject);
    stream.on('end', () => { session.close(); resolveAnswer({ status, body: text, retryAfter }); });
    stream.end(payload);
  });
  const json = async (node: Node, method: string, path: string, body: unknown, user?: string, expected = 200) => {
    const answer = await internal(node, method, path, body, user);
    expect(answer.status, answer.body).toBe(expected);
    return JSON.parse(answer.body);
  };
  const build = process.env.E2E_BUILD ? join(process.env.E2E_BUILD, 'terrabase') : resolve(e2eRoot, '../src/TerraBase/bin/Release/net11.0');
  const prepare = async () => {
    const compiler = spawn('cc', ['-shared', '-fPIC', '-Wall', '-Wextra', '-Werror', join(e2eRoot, 'clock/replay.c'), '-o', library, '-ldl']);
    let compileErrors = '';
    compiler.stderr.on('data', (chunk: Buffer) => { compileErrors += chunk.toString(); });
    expect((await once(compiler, 'exit'))[0], compileErrors).toBe(0);
    symlinkSync(library, join(root, 'libc.so'));
    setPeers(members);
    for (const node of members) {
      set(node, 0);
      mkdirSync(join(root, `${node.name}-data`));
    }
    if (!broker) return;
    writeFileSync(join(root, 'nats.conf'), JSON.stringify({ host: '127.0.0.1', port: brokerPort, authorization: { token: natsToken }, jetstream: { store_dir: join(root, 'nats') } }));
    const output = openSync(join(root, 'nats.log'), 'a');
    logs.push(output);
    brokerProcess = spawn(join(process.env.E2E_TOOLS ?? '', 'nats-server'), ['-c', join(root, 'nats.conf')], { stdio: ['ignore', output, output] });
    await expect.poll(() => readFileSync(join(root, 'nats.log'), 'utf8').includes('Server is ready'), { message: 'the cluster broker is ready' }).toBe(true);
    nats = new Nats(natsToken, brokerPort);
    await nats.connect();
    for (const region of new Set(members.map((member) => member.region))) {
      const name = `TERRABASE_PUSH_${region.toUpperCase()}`;
      const created = await nats.request(`$JS.API.STREAM.CREATE.${name}`, {
        name, subjects: [`terrabase.push.${region}.>`, `terrabase.push-state.${region}.>`], storage: 'file', retention: 'limits', discard: 'new',
        max_bytes: 1 << 30, duplicate_window: 600_000_000_000, num_replicas: 1,
      });
      expect(created.error, JSON.stringify(created)).toBeUndefined();
    }
  };
  const database = async (node: Node) => {
    const user = id();
    const organization = await json(node, 'POST', '/v1/management/organizations', { name: `Clock ${id()}` }, user, 201);
    const project = await json(node, 'POST', `/v1/management/organizations/${organization.id}/projects`, { name: 'Clock trust' }, user, 201);
    const created = await json(node, 'POST', `/v1/projects/${project.id}/databases`, { name: 'clock-trust', allRegions: true }, user, 201);
    const sqlPath = `/v1/projects/${project.id}/databases/${created.id}/sql/execute`;
    const sql = (target: Node, statement: string) => internal(target, 'POST', sqlPath, { statements: [{ sql: statement }] });
    const scope = `${organization.id}/${project.id}/${created.id}`;
    if (nats) await putKey({ master: Buffer.from(masterKey, 'base64'), access: r2Key, secret: r2Secret }, scope, keyId, dataKey);
    const deliver = async (origin: Node, transaction: Transaction) => {
      const connection = nats;
      if (!connection) throw new Error(`cluster ${label} has no broker`);
      const applied = connection.event(`terrabase.applied.${origin.region}.${scope.replaceAll('/', '.')}`, (result: { id?: string }) => result.id === transaction.id);
      const ack = await connection.request(`terrabase.push.${origin.region}.${node.region}.${scope.replaceAll('/', '.')}`, seal(transaction, scope, keyId, dataKey));
      expect(ack.error, JSON.stringify(ack)).toBeUndefined();
      return applied;
    };
    expect((await sql(node, 'CREATE TABLE clock_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)')).status).toBe(200);
    const rows = async (target: Node) =>
      (await json(target, 'POST', sqlPath, { statements: [{ sql: 'SELECT id FROM clock_rows ORDER BY id' }] })).results[0].rows as number[][];
    return { sql, deliver, rows };
  };
  const finish = async (info: TestInfo) => {
    for (const node of started) {
      await info.attach(`${node.name}-readiness`, { body: readiness.get(node) ?? 'not requested', contentType: 'text/plain' });
      await info.attach(`${node.name}-log`, { body: log(node), contentType: 'text/plain' });
    }
    await info.attach(`clock-trust-${label}`, { body: observations.join('\n'), contentType: 'text/plain' });
    await Promise.all([...running.keys()].map(stop));
    for (const stand of [...standIns]) await stand.close();
    nats?.close();
    if (brokerProcess && brokerProcess.exitCode === null) {
      brokerProcess.kill('SIGTERM');
      await once(brokerProcess, 'exit');
    }
    if (broker) await info.attach(`clock-trust-${label}-broker`, { body: readFileSync(join(root, 'nats.log'), 'utf8'), contentType: 'text/plain' });
    for (const output of logs) closeSync(output);
    rmSync(account);
    rmSync(root, { recursive: true, force: true });
  };
  return {
    set, stepAfterKernel, setPeers, log, metric, allowed, mode, transitions, previousBuild, proxy, standIn, holds, start, stop, opens, ready, internal, json, clockPeer, refused, prepare,
    database, finish, observations, opened, clockState,
  };
}
