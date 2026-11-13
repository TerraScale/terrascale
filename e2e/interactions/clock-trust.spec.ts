import { createHash, randomBytes } from 'node:crypto';
import { spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import { closeSync, mkdirSync, mkdtempSync, openSync, readFileSync, renameSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { connect, createServer, type IncomingHttpHeaders, type ServerHttp2Session, type ServerHttp2Stream } from 'node:http2';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';
import { test, expect, type APIRequestContext, type TestInfo } from '@playwright/test';
import { saveSecret } from '../support/state';
import { e2eRoot, production, stateDir } from '../support/stack';

test.skip(production, 'clock trust runs only against throwaway nodes');
test.describe.configure({ mode: 'default' });

type Node = { name: string; region: string; address: string; port: number; metrics: number; postgres: number };
type Answer = { status: number; body: string; retryAfter?: string };
type StandIn = { asked: () => number; answeredAt: () => number; close: () => Promise<void> };

const portOffset = Number(process.env.TEST_PARALLEL_INDEX ?? '0') * 100;
const internalPort = 18281 + portOffset;
const run = randomBytes(3).toString('hex');
const nodes: Record<'a' | 'b' | 'c', Node> = {
  a: { name: `clock-a-${run}`, region: `gru-${run}`, address: '127.0.0.21', port: 18280 + portOffset, metrics: 19280 + portOffset, postgres: 16280 + portOffset },
  b: { name: `clock-b-${run}`, region: `fra-${run}`, address: '127.0.0.22', port: 18290 + portOffset, metrics: 19290 + portOffset, postgres: 16290 + portOffset },
  c: { name: `clock-c-${run}`, region: `sjc-${run}`, address: '127.0.0.23', port: 18300 + portOffset, metrics: 19300 + portOffset, postgres: 16300 + portOffset },
};
const lone = (letter: string, octet: number, port: number, partners: 'near' | 'far'): Record<'node' | 'first' | 'second', Node> => ({
  node: { name: `clock-${letter}-${run}`, region: `gru${letter}-${run}`, address: `127.0.0.${octet}`, port: port + portOffset, metrics: port + 1000 + portOffset, postgres: port - 2000 + portOffset },
  first: { name: `clock-${letter}1-${run}`, region: partners === 'near' ? `gru${letter}-${run}` : `fra${letter}-${run}`, address: `127.0.0.${octet + 1}`, port: 0, metrics: 0, postgres: 0 },
  second: { name: `clock-${letter}2-${run}`, region: partners === 'near' ? `gru${letter}-${run}` : `sjc${letter}-${run}`, address: `127.0.0.${octet + 2}`, port: 0, metrics: 0, postgres: 0 },
});

const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
function id(): string {
  let time = Date.now();
  let head = '';
  for (let index = 0; index < 10; index++) {
    head = alphabet[time % 32] + head;
    time = Math.floor(time / 32);
  }
  return head + Array.from(randomBytes(16), (value) => alphabet[value % 32]).join('');
}

async function cluster(request: APIRequestContext, members: Node[], label: string) {
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
  const start = async (node: Node) => {
    const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) => !/^(TS_|TERRABASE_|AWS_|BUCKET_NAME)/.test(name)));
    const output = openSync(join(root, `${node.name}.log`), 'a');
    logs.push(output);
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
  const opens = async (node: Node, label: string) => {
    const { spawned, ready } = await start(node);
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
  };
  const database = async (node: Node) => {
    const user = id();
    const organization = await json(node, 'POST', '/v1/management/organizations', { name: `Clock ${id()}` }, user, 201);
    const project = await json(node, 'POST', `/v1/management/organizations/${organization.id}/projects`, { name: 'Clock trust' }, user, 201);
    const created = await json(node, 'POST', `/v1/projects/${project.id}/databases`, { name: 'clock-trust', allRegions: true }, user, 201);
    const sqlPath = `/v1/projects/${project.id}/databases/${created.id}/sql/execute`;
    const sql = (target: Node, statement: string) => internal(target, 'POST', sqlPath, { statements: [{ sql: statement }] });
    const applyPath = `/v1/sync/organizations/${organization.id}/projects/${project.id}/databases/${created.id}/apply`;
    expect((await sql(node, 'CREATE TABLE clock_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)')).status).toBe(200);
    const rows = async (target: Node) =>
      (await json(target, 'POST', sqlPath, { statements: [{ sql: 'SELECT id FROM clock_rows ORDER BY id' }] })).results[0].rows as number[][];
    return { sql, applyPath, rows };
  };
  const finish = async (info: TestInfo) => {
    for (const node of started) {
      await info.attach(`${node.name}-readiness`, { body: readiness.get(node) ?? 'not requested', contentType: 'text/plain' });
      await info.attach(`${node.name}-log`, { body: log(node), contentType: 'text/plain' });
    }
    await info.attach(`clock-trust-${label}`, { body: observations.join('\n'), contentType: 'text/plain' });
    await Promise.all([...running.keys()].map(stop));
    for (const stand of [...standIns]) await stand.close();
    for (const output of logs) closeSync(output);
    rmSync(account);
    rmSync(root, { recursive: true, force: true });
  };
  return {
    set, stepAfterKernel, setPeers, log, metric, allowed, mode, transitions, previousBuild, proxy, standIn, holds, start, stop, opens, ready, internal, json, clockPeer, refused, prepare,
    database, finish, observations, opened,
  };
}

test('a lone node opens on agreeing peers and a clock step requires two new rounds', async ({ request }, info) => {
  const { a, b, c } = nodes;
  const { set, metric, allowed, mode, proxy, standIn, holds, start, stop, opens, prepare, finish, observations } =
    await cluster(request, [a, b, c], 'opening');
  try {
    await prepare();

    await start(a);
    await expect.poll(() => metric(a, 'terrabase_clock_agreeing_rounds'), { message: 'a lone node with no answering peer stays closed', intervals: [500] })
      .toBe(0);
    expect(await allowed(a)).toBe(0);
    expect(await mode(a)).toBe('closed/no_contact');
    observations.push('first node with no peer reachable: local_writes_allowed=0, mode closed/no_contact');
    const proxies = [await standIn(b, proxy), await standIn(c, proxy)];
    await holds(a, 'a bare 404 from the peer address is no contact, not a previous build', 'closed/no_contact 0', proxies, 3);
    for (const stand of proxies) await stand.close();
    observations.push('peers answering a bare 404 without the TerraBase error body: mode closed/no_contact through three rounds');
    await opens(b, 'second node');
    await expect.poll(() => allowed(a), { message: 'the first node opens once a second node answers' }).toBe(1);
    await opens(c, 'third node');

    await stop(c);
    await opens(c, 'healthy third-node restart');

    const stepsBefore = await metric(a, 'terrabase_clock_steps_total');
    set(a, 600_000);
    await expect.poll(() => allowed(a), { message: 'a step closes the node at once', intervals: [100] }).toBe(0);
    set(a, 0);
    await expect.poll(() => metric(a, 'terrabase_clock_steps_total'), { message: 'the step is counted' }).toBeGreaterThan(stepsBefore);
    const readings: string[] = [];
    await expect.poll(async () => {
      const open = await allowed(a);
      const rounds = await metric(a, 'terrabase_clock_agreeing_rounds');
      readings.push(`${open}@${rounds}`);
      if (open === 1) expect(rounds).toBeGreaterThanOrEqual(2);
      return open;
    }, { message: 'a step reopens only after two agreeing rounds begun after it', intervals: [100] }).toBe(1);
    expect(readings[0]).toMatch(/^0@[01]$/);
    observations.push(`step while open: closed at once, readings (allowed@rounds) ${readings.join(' ')}`);
    expect(await allowed(b)).toBe(1);
    expect(await allowed(c)).toBe(1);
    observations.push('one wrong clock among three closed alone: b and c stayed open');

    console.log(observations.join('\n'));
  } finally {
    await finish(info);
  }
});

test('a home and replica started ahead never stamp, and healthy restarts recover', async ({ request }, info) => {
  const { a, b, c } = nodes;
  const { set, metric, allowed, start, stop, opens, internal, json, refused, prepare, finish, observations, opened } =
    await cluster(request, [a, b, c], 'writes');
  try {
    await prepare();
    await Promise.all([opens(a, 'first node'), opens(b, 'second node'), opens(c, 'third node')]);

    const user = id();
    const organization = await json(a, 'POST', '/v1/management/organizations', { name: `Clock ${id()}` }, user, 201);
    const project = await json(a, 'POST', `/v1/management/organizations/${organization.id}/projects`, { name: 'Clock trust' }, user, 201);
    const database = await json(a, 'POST', `/v1/projects/${project.id}/databases`, { name: 'clock-trust', allRegions: true }, user, 201);
    const sqlPath = `/v1/projects/${project.id}/databases/${database.id}/sql/execute`;
    const sql = (node: Node, statement: string) => internal(node, 'POST', sqlPath, { statements: [{ sql: statement }] });
    expect((await sql(a, 'CREATE TABLE clock_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)')).status).toBe(200);
    expect((await sql(a, "INSERT INTO clock_rows VALUES (1, 'trusted home')")).status).toBe(200);
    await expect.poll(() => metric(a, 'terrabase_clock_stamps_total', 'outcome="issued"'), { message: 'the trusted home issued stamps' }).toBeGreaterThan(0);

    await stop(a);
    set(a, 600_000);
    await start(a);
    await expect.poll(async () => [await metric(a, 'terrabase_clock_peers_answering'), await metric(a, 'terrabase_clock_peers_agreeing'), await allowed(a)],
      { message: 'a home started 600 s ahead hears two disagreeing peers and stays closed' }).toEqual([2, 0, 0]);
    refused(await sql(a, "INSERT INTO clock_rows VALUES (2, 'home ahead')"), 'the home ahead refuses its own write');
    expect(await metric(a, 'terrabase_clock_stamps_total', 'outcome="issued"')).toBe(0);
    await expect.poll(() => metric(a, 'terrabase_clock_stamps_total', 'outcome="refused"'), { message: 'the home ahead refused its stamp' }).toBeGreaterThanOrEqual(1);
    observations.push('home started 600 s ahead: issued=0, write at the home 409 serialization_conflict with Retry-After 1 and not committed');

    await stop(a);
    set(a, 0);
    await opens(a, 'healthy restart 1');
    await stop(b);
    set(b, 600_000);
    await start(b);
    await expect.poll(async () => [await metric(b, 'terrabase_clock_peers_agreeing'), await allowed(b)],
      { message: 'a replica started 600 s ahead stays closed' }).toEqual([0, 0]);
    expect((await sql(a, "INSERT INTO clock_rows VALUES (4, 'home trusted again')")).status).toBe(200);
    expect(await metric(b, 'terrabase_clock_stamps_total', 'outcome="issued"')).toBe(0);
    expect((await json(a, 'POST', sqlPath, { statements: [{ sql: 'SELECT id, value FROM clock_rows ORDER BY id' }] })).results[0].rows)
      .toEqual([[1, 'trusted home'], [4, 'home trusted again']]);
    observations.push('replica started 600 s ahead: closed, issued=0; the restarted home commits again and the refused row is absent');
    await stop(b);
    set(b, 0);
    await opens(b, 'healthy restart 2');
    observations.push(`open times after /readyz in ms (initial nodes and healthy restarts): ${opened.map(Math.round).join(', ')}`);
    console.log(observations.join('\n'));
  } finally {
    await finish(info);
  }
});

test('clock trust distinguishes unreachable peers, previous builds and disagreeing peers', async ({ request }, info) => {
  const { a, b, c } = nodes;
  const { set, metric, allowed, mode, transitions, previousBuild, standIn, holds, start, stop, opens, database, refused, prepare, finish, observations } =
    await cluster(request, [a, b, c], 'previous-build');
  try {
    await prepare();
    await start(a);
    await opens(b, 'second node');
    await opens(c, 'third node');
    const { sql, rows } = await database(a);
    expect((await sql(a, "INSERT INTO clock_rows VALUES (1, 'trusted home'), (4, 'trusted again')")).status).toBe(200);
    await stop(c);
    const previousC = await standIn(c, previousBuild);
    await expect.poll(async () => [previousC.asked() > 2, await metric(a, 'terrabase_clock_peers_answering'), await allowed(a), await mode(a)],
      { message: 'a peer of the previous build does not count as answering when another peer answers' }).toEqual([true, 1, 1, 'peers_agree/none']);
    observations.push(`peer of the previous build (${previousC.asked()} requests): a counts 1 answering peer and stays open, mode peers_agree`);

    set(b, 600_000);
    await expect.poll(async () => [await allowed(a), await allowed(b), await metric(a, 'terrabase_clock_peers_agreeing'), await metric(b, 'terrabase_clock_peers_agreeing')],
      { message: 'two nodes that disagree with nobody else answering both close' }).toEqual([0, 0, 0, 0]);
    const askedWhenClosed = previousC.asked();
    await expect.poll(async () => {
      expect([await allowed(a), await allowed(b)]).toEqual([0, 0]);
      return previousC.asked() - askedWhenClosed;
    }, { message: 'both stay closed through three more rounds' }).toBeGreaterThanOrEqual(6);
    observations.push('two disagreeing nodes, third on the previous build: both closed through three more rounds');
    set(b, 0);
    await expect.poll(async () => [await allowed(a), await allowed(b)], { message: 'both reopen once they agree' }).toEqual([1, 1]);

    await stop(b);
    const previousB = await standIn(b, previousBuild);
    await stop(a);
    await start(a);
    const bootReadings: string[] = [];
    await expect.poll(async () => {
      const open = await allowed(a);
      bootReadings.push(`${open}@${await metric(a, 'terrabase_clock_agreeing_rounds')}`);
      return open;
    }, { message: 'a node whose only peers run the previous build opens', intervals: [100] }).toBe(1);
    expect(bootReadings.filter((reading) => reading.startsWith('1@')).map((reading) => Number(reading.slice(2))).every((rounds) => rounds >= 2), bootReadings.join(' '))
      .toBe(true);
    expect([await mode(a), await metric(a, 'terrabase_clock_peers_answering')]).toEqual(['first_of_build/none', 0]);
    const bootWrite = await sql(a, "INSERT INTO clock_rows VALUES (5, 'first of its build')");
    expect(bootWrite.status, bootWrite.body).toBe(200);
    await expect.poll(() => metric(a, 'terrabase_clock_stamps_total', 'outcome="issued"'), { message: 'the home first of its build issued the stamp' }).toBeGreaterThan(0);
    observations.push(`home whose peers all run the previous build: opened (readings allowed@rounds ${bootReadings.join(' ')}), mode first_of_build, home write 200`);

    await stop(a);
    set(a, 600_000, 64);
    await start(a);
    await holds(a, 'a node 600 s ahead with an unsynchronised kernel stays closed while its peers run the previous build', 'closed/kernel 0', [previousB, previousC], 4);
    refused(await sql(a, "INSERT INTO clock_rows VALUES (6, 'first of its build ahead')"), 'first of its build ahead with an unsynchronised kernel refuses');
    expect(await metric(a, 'terrabase_clock_stamps_total', 'outcome="issued"')).toBe(0);
    observations.push('first of its build, 600 s ahead with the kernel unsynchronised: mode closed/kernel through four rounds, home write 409 serialization_conflict, issued=0');
    set(a, 0);
    await expect.poll(async () => [await mode(a), await allowed(a)], { message: 'the kernel is good again and a reopens as first of its build' })
      .toEqual(['first_of_build/none', 1]);

    await previousB.close();
    set(b, 600_000);
    await start(b);
    await expect.poll(() => mode(a), { message: 'a peer that answers the route and disagrees closes the node', intervals: [100] }).toBe('closed/peers_disagree');
    expect(transitions(a)).toEqual(['first_of_build/none', 'closed/peers_disagree']);
    await holds(a, 'an answering peer outranks a peer of the previous build', 'closed/peers_disagree 0', [previousC], 3);
    expect(await rows(a)).toEqual([[1], [4], [5]]);
    observations.push('first of its build, then one peer answers 600 s ahead while the other still runs the previous build: first_of_build -> closed/peers_disagree with no step between, closed through three rounds');
    console.log(observations.join('\n'));
  } finally {
    await finish(info);
  }
});

test('a received write never carries a wrong local clock into the HLC, and a clean restart issues at once', async ({ request }, info) => {
  const { node: h, first, second } = lone('h', 51, 18330, 'far');
  const { set, metric, allowed, mode, standIn, stop, opens, ready, internal, json, clockPeer, refused, prepare, database, finish, observations, log } =
    await cluster(request, [h, first, second], 'ahead');
  const peer = { offset: 0 as number | null };
  const ahead: number[] = [];
  const sample = async () => {
    ahead.push(await metric(h, 'terrabase_clock_hlc_ahead_milliseconds'));
    return ahead[ahead.length - 1];
  };
  try {
    await prepare();
    await standIn(first, clockPeer(peer));
    await standIn(second, clockPeer(peer));
    await opens(h, 'node with agreeing peers');
    const { sql, applyPath, rows } = await database(h);
    expect((await sql(h, "INSERT INTO clock_rows VALUES (1, 'trusted')")).status).toBe(200);
    const received = (row: number) => json(h, 'POST', applyPath, {
      id: id(), region: first.region, hlc: { physicalMilliseconds: Date.now(), logical: 0, nodeId: first.name },
      changes: [{ op: 'insert', table: 'clock_rows', values: { id: row, value: 'received' } }],
    });

    set(h, 600_000);
    await expect.poll(async () => [await allowed(h), await mode(h)], { message: 'a step ten minutes ahead closes the node' }).toEqual([0, 'closed/peers_disagree']);
    for (const row of [10, 11]) expect(await received(row)).toMatchObject({ state: 'confirmed', conflicts: [] });
    expect(await sample()).toBeLessThanOrEqual(50);
    refused(await sql(h, "INSERT INTO clock_rows VALUES (12, 'closed ahead')"), 'the closed node ahead refuses its own write');
    await ready(h);
    expect(await rows(h)).toEqual([[1], [10], [11]]);
    expect((await internal(h, 'GET', '/v1/sync/clock')).status).toBe(200);
    observations.push('clock closed by peer disagreement: readyz 200, reads and received writes 200, peer clock 200, home writes retryable 409');
    set(h, 0);
    await expect.poll(async () => [await sample(), await allowed(h)], { message: 'the corrected clock reopens', intervals: [100] }).toEqual([expect.any(Number), 1]);
    const reopened = await sql(h, "INSERT INTO clock_rows VALUES (13, 'issued after reopening')");
    expect(reopened.status, reopened.body).toBe(200);
    expect(await sample()).toBeLessThanOrEqual(50);
    observations.push(`closed 600 s ahead, applied two received writes, corrected: the first write after reopening committed; hlc_ahead samples ${ahead.join(' ')}`);

    set(h, 600_000);
    await expect.poll(() => allowed(h), { message: 'a second step ahead closes the node' }).toBe(0);
    expect(await received(14)).toMatchObject({ state: 'confirmed', conflicts: [] });
    await stop(h);
    set(h, 0);
    await opens(h, 'restart after applying while ahead');
    const restarted = await sql(h, "INSERT INTO clock_rows VALUES (15, 'issued after the restart')");
    expect(restarted.status, restarted.body).toBe(200);
    expect(await sample()).toBeLessThanOrEqual(50);
    expect(Math.max(...ahead)).toBeLessThanOrEqual(50);
    observations.push(`closed 600 s ahead, applied a received write, restarted with the clock corrected: the first write committed; largest hlc_ahead ${Math.max(...ahead)} ms`);

    const before = Date.now();
    expect((await sql(h, "INSERT INTO clock_rows VALUES (16, 'before a clean restart')")).status).toBe(200);
    await stop(h);
    expect(log(h)).toContain('Saved the last HLC stamp');
    await opens(h, 'clean restart');
    const resumed = await sql(h, "INSERT INTO clock_rows VALUES (17, 'after a clean restart')");
    const after = Date.now();
    expect(resumed.status, resumed.body).toBe(200);
    expect(await metric(h, 'terrabase_clock_stamps_total', 'outcome="refused"')).toBe(0);
    expect(await rows(h)).toEqual([[1], [10], [11], [13], [14], [15], [16], [17]]);
    observations.push(`clean restart: last stamp saved at shutdown; the first write after reopening committed ${after - before} ms after the write before the stop, refused=0`);
    console.log(observations.join('\n'));
  } finally {
    await finish(info);
  }
});

test('home writes recover in two agreeing rounds after a step during issuance, including across restart', async ({ request }, info) => {
  const { node: h, first, second } = lone('w', 61, 18340, 'near');
  const { set, stepAfterKernel, setPeers, metric, mode, standIn, start, stop, opens, clockPeer, refused, prepare, database, finish, observations } =
    await cluster(request, [h, first, second], 'issuing');
  const peer = { offset: 0 };
  try {
    await prepare();
    const peers = [await standIn(first, clockPeer(peer)), await standIn(second, clockPeer(peer))];
    await opens(h, 'writing home with agreeing peers');
    const { sql, rows } = await database(h);
    const committed: number[][] = [];
    for (const restart of [false, true]) {
      const base = restart ? 100 : 0;
      const warm = await sql(h, `INSERT INTO clock_rows VALUES (${base + 1}, 'before step')`);
      expect(warm.status, warm.body).toBe(200);
      committed.push([base + 1]);
      stepAfterKernel(h, 600_000);
      const answers = await Promise.all(Array.from({ length: 2 }, (_, index) =>
        sql(h, `INSERT INTO clock_rows VALUES (${base + 2 + index}, 'writing through step')`)));
      expect(answers.some((answer) => answer.status === 409)).toBe(true);
      for (const [index, answer] of answers.entries()) {
        if (answer.status === 200) committed.push([base + 2 + index]);
        else refused(answer, 'a write crossing the forward step');
      }
      refused(await sql(h, `INSERT INTO clock_rows VALUES (${base + 20}, 'during step')`), 'writes stay refused while ahead');
      set(h, 0);
      if (restart) {
        await stop(h);
        await start(h);
      }
      const from = peers.map((stand) => stand.asked());
      await expect.poll(async () => {
        const asked = Math.min(...peers.map((stand, index) => stand.asked() - from[index]));
        const current = await mode(h);
        if (current !== 'peers_agree/none') expect(asked).toBeLessThanOrEqual(2);
        return current;
      }, { message: 'the corrected home opens by the second agreeing round', intervals: [100] }).toBe('peers_agree/none');
      expect(await metric(h, 'terrabase_clock_agreeing_rounds')).toBeGreaterThanOrEqual(2);
      const recovered = await sql(h, `INSERT INTO clock_rows VALUES (${base + 21}, 'after correction')`);
      expect(recovered.status, recovered.body).toBe(200);
      committed.push([base + 21]);
      expect(await metric(h, 'terrabase_clock_hlc_ahead_milliseconds')).toBeLessThanOrEqual(50);
      expect(await rows(h)).toEqual(committed.sort((left, right) => left[0] - right[0]));
      observations.push(`home writes across a 600 s step: refused while ahead, first write after two agreeing rounds committed${restart ? ' after restarting between correction and the write' : ' without restarting'}`);
    }
    setPeers([]);
    await expect.poll(() => mode(h), { message: 'a configured but empty peers file closes as no_contact' }).toBe('closed/no_contact');
    refused(await sql(h, "INSERT INTO clock_rows VALUES (200, 'empty peers file')"), 'a configured empty peers file refuses writes');
    setPeers([h, first, second]);
    await expect.poll(() => mode(h), { message: 'restoring agreeing peers reopens the home' }).toBe('peers_agree/none');
    expect(await rows(h)).toEqual(committed);
    observations.push('configured peers file rewritten empty: closed/no_contact and home write refused; restored peers reopen it');
    console.log(observations.join('\n'));
  } finally {
    await finish(info);
  }
});

test('an open node that loses every peer keeps stamping; a closed one stays closed', async ({ request }, info) => {
  const { node: p, first, second } = lone('p', 31, 18310, 'far');
  const { metric, allowed, mode, transitions, standIn, holds, opens, ready, clockPeer, refused, prepare, database, finish, observations } =
    await cluster(request, [p, first, second], 'lost');
  const peer = { offset: 0 as number | null };
  try {
    await prepare();
    const peers = [await standIn(first, clockPeer(peer)), await standIn(second, clockPeer(peer))];
    await opens(p, 'node with agreeing far peers');
    await expect.poll(() => mode(p), { message: 'two agreeing peers' }).toBe('peers_agree/none');
    const { sql, rows } = await database(p);
    expect((await sql(p, "INSERT INTO clock_rows VALUES (1, 'peers agree')")).status).toBe(200);

    peer.offset = null;
    await holds(p, 'one failed round to the only answering peers', 'peers_agree/none 1', peers, 1);
    peer.offset = 0;
    await holds(p, 'the node stays open after the failed round', 'peers_agree/none 1', peers, 1);
    observations.push('one failed round to both far peers: peers_agree throughout');

    peer.offset = null;
    const silent = Math.max(...peers.map((stand) => stand.answeredAt()));
    await expect.poll(async () => {
      expect(await allowed(p)).toBe(1);
      return mode(p);
    }, { message: 'no answer for 15 s moves the node to peers_lost and it stays open' }).toBe('peers_lost/none');
    const lostAfter = performance.now() - silent;
    expect(lostAfter).toBeGreaterThan(14_000);
    expect(transitions(p)).toEqual(['peers_agree/none', 'peers_lost/none']);
    expect((await sql(p, "INSERT INTO clock_rows VALUES (2, 'peers lost')")).status).toBe(200);
    observations.push(`no answer from any peer: peers_lost after ${Math.round(lostAfter)} ms with no closed mode between, home write 200`);

    peer.offset = 0;
    await expect.poll(() => mode(p), { message: 'an agreeing answer returns the node to peers_agree', intervals: [100] }).toBe('peers_agree/none');
    expect(transitions(p)).toEqual(['peers_lost/none', 'peers_agree/none']);
    peer.offset = null;
    await expect.poll(() => mode(p), { message: 'silent again: peers_lost' }).toBe('peers_lost/none');
    peer.offset = 600_000;
    await expect.poll(() => mode(p), { message: 'a peer that answers and disagrees closes the node at once', intervals: [100] }).toBe('closed/peers_disagree');
    expect(transitions(p)).toEqual(['peers_lost/none', 'closed/peers_disagree']);
    refused(await sql(p, "INSERT INTO clock_rows VALUES (3, 'disagreed')"), 'the node closed by a disagreeing peer refuses');
    observations.push('peers_lost -> peers_agree on an agreeing answer; peers_lost -> closed/peers_disagree on a disagreeing answer, home write 409');

    peer.offset = null;
    await expect.poll(() => mode(p), { message: 'closed with no peer in contact' }).toBe('closed/no_contact');
    await holds(p, 'a closed node with no peer in contact does not open', 'closed/no_contact 0', peers, 5);
    await ready(p);
    expect(await rows(p)).toEqual([[1], [2]]);
    refused(await sql(p, "INSERT INTO clock_rows VALUES (5, 'no contact')"), 'closed with no contact refuses home writes while serving reads');
    peer.offset = 0;
    await expect.poll(() => mode(p), { message: 'agreeing peers reopen it' }).toBe('peers_agree/none');
    expect((await sql(p, "INSERT INTO clock_rows VALUES (4, 'reopened')")).status).toBe(200);
    expect(await rows(p)).toEqual([[1], [2], [4]]);
    expect(await metric(p, 'terrabase_clock_failed_rounds_total')).toBe(0);
    observations.push('closed and isolated: closed/no_contact through five rounds; agreeing peers reopen it');
    console.log(observations.join('\n'));
  } finally {
    await finish(info);
  }
});

test('a brief unsynchronised kernel does not close an open node; a minute of it does', async ({ request }, info) => {
  test.slow();
  const { node: k, first, second } = lone('k', 41, 18320, 'near');
  const { set, metric, allowed, mode, standIn, holds, opens, ready, prepare, database, refused, finish, observations, clockPeer } = await cluster(request, [k, first, second], 'kernel');
  const peer = { offset: 0 as number | null };
  try {
    await prepare();
    const peers = [await standIn(first, clockPeer(peer)), await standIn(second, clockPeer(peer))];
    await opens(k, 'node with agreeing partners');
    await expect.poll(() => mode(k), { message: 'two agreeing partners' }).toBe('peers_agree/none');
    const { sql, rows } = await database(k);
    expect((await sql(k, "INSERT INTO clock_rows VALUES (1, 'trusted')")).status).toBe(200);

    set(k, 0, 64);
    await holds(k, 'unsynchronised for the first 10 s', 'peers_agree/none 1', peers, 10);
    await holds(k, 'unsynchronised for 20 s', 'peers_agree/none 1', peers, 10);
    set(k, 0);
    await holds(k, 'synchronised again', 'peers_agree/none 1', peers, 2);
    observations.push('kernel unsynchronised for 20 s on an open node: peers_agree and open throughout');

    set(k, 0, 64);
    const unsynchronised = performance.now();
    for (let window = 0; window < 5; window++) await holds(k, `unsynchronised, window ${window + 1} of 5`, 'peers_agree/none 1', peers, 10);
    await expect.poll(() => mode(k), { message: 'a minute unsynchronised closes the node', intervals: [100] }).toBe('closed/kernel');
    const closedAfter = performance.now() - unsynchronised;
    expect(closedAfter).toBeGreaterThanOrEqual(60_000);
    expect(closedAfter).toBeLessThan(70_000);
    expect(await allowed(k)).toBe(0);
    await ready(k);
    expect(await rows(k)).toEqual([[1]]);
    refused(await sql(k, "INSERT INTO clock_rows VALUES (2, 'unsynchronised')"), 'unsynchronised kernel refuses home writes while serving reads');
    observations.push(`kernel unsynchronised again: closed/kernel after ${Math.round(closedAfter)} ms`);

    set(k, 0, 0, 50_001);
    await holds(k, 'a closed node gets no grace: an error estimate over 50 ms keeps it closed', 'closed/kernel 0', peers, 3);
    expect(await metric(k, 'terrabase_clock_agreeing_rounds')).toBe(0);
    await ready(k);
    expect(await rows(k)).toEqual([[1]]);
    refused(await sql(k, "INSERT INTO clock_rows VALUES (3, 'estimated error')"), 'excessive kernel error refuses home writes while serving reads');
    set(k, 0);
    await expect.poll(() => allowed(k), { message: 'a synchronised kernel within 50 ms reopens it, with a 16 s maximum error' }).toBe(1);
    expect((await sql(k, "INSERT INTO clock_rows VALUES (4, 'synchronised again')")).status).toBe(200);
    expect(await rows(k)).toEqual([[1], [4]]);
    observations.push('closed: estimated error 50.001 ms keeps it closed; 1 ms with maximum error 16 s reopens it');
    console.log(observations.join('\n'));
  } finally {
    await finish(info);
  }
});
