import { randomBytes } from 'node:crypto';
import { spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import { closeSync, mkdirSync, mkdtempSync, openSync, readFileSync, renameSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { connect, createServer, type IncomingHttpHeaders, type ServerHttp2Session, type ServerHttp2Stream } from 'node:http2';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';
import { test, expect } from '@playwright/test';
import { saveSecret } from '../support/state';
import { e2eRoot, production, stateDir } from '../support/stack';

test.skip(production, 'clock trust runs only against throwaway nodes');

type Node = { name: string; region: string; address: string; port: number; metrics: number; postgres: number };
type Answer = { status: number; body: string };
type StandIn = { asked: () => number; close: () => Promise<void> };

const internalPort = 18281;
const run = randomBytes(3).toString('hex');
const nodes: Record<'a' | 'b' | 'c', Node> = {
  a: { name: `clock-a-${run}`, region: `gru-${run}`, address: '127.0.0.21', port: 18280, metrics: 19280, postgres: 16280 },
  b: { name: `clock-b-${run}`, region: `fra-${run}`, address: '127.0.0.22', port: 18290, metrics: 19290, postgres: 16290 },
  c: { name: `clock-c-${run}`, region: `sjc-${run}`, address: '127.0.0.23', port: 18300, metrics: 19300, postgres: 16300 },
};

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

test('a node stamps writes only while its peers and kernel agree with its clock', async ({ request }, info) => {
  const root = mkdtempSync(join(tmpdir(), 'terrabase-clock-trust-'));
  const library = join(root, 'libclock-replay.so');
  const peers = join(root, 'peers');
  const peerSecret = randomBytes(32).toString('hex');
  const masterKey = randomBytes(32).toString('base64');
  saveSecret('clock-trust-peer', peerSecret);
  saveSecret('clock-trust-master', masterKey);
  const [r2Key, r2Secret] = readFileSync(join(stateDir, 'r2'), 'utf8').split('\n');
  const cloudflareToken = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[9];
  const running = new Map<Node, ChildProcess>();
  const logs: number[] = [];
  const observations: string[] = [];
  const opened: number[] = [];
  const standIns = new Set<StandIn>();

  const set = (node: Node, offset: number, status = 0, error = 1000) => {
    writeFileSync(join(root, `${node.name}.next`), `${offset} ${status} ${error}\n`);
    renameSync(join(root, `${node.name}.next`), join(root, node.name));
  };
  const log = (node: Node) => readFileSync(join(root, `${node.name}.log`), 'utf8');
  const metric = async (node: Node, name: string, label = '') => {
    expect(running.get(node)?.exitCode, log(node)).toBeNull();
    const response = await request.get(`http://127.0.0.1:${node.metrics}/metrics`);
    expect(response.ok()).toBe(true);
    const lines = (await response.text()).split('\n').filter((line) => line.startsWith(`${name}{`) || line.startsWith(`${name} `));
    const line = lines.find((candidate) => candidate.includes(label));
    return line ? Number(line.slice(line.lastIndexOf(' ') + 1)) : 0;
  };
  const allowed = (node: Node) => metric(node, 'terrabase_clock_local_writes_allowed');
  const mode = async (node: Node) => {
    expect(running.get(node)?.exitCode, log(node)).toBeNull();
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
    const sessions = new Set<ServerHttp2Session>();
    const server = createServer();
    server.on('session', (session) => {
      sessions.add(session);
      session.on('close', () => sessions.delete(session));
    });
    server.on('stream', (stream: ServerHttp2Stream, headers: IncomingHttpHeaders) => {
      asked++;
      const { status, body } = answer(headers);
      stream.respond({ ':status': status, ...(body === undefined ? {} : { 'content-type': 'application/json' }) });
      stream.end(body === undefined ? undefined : JSON.stringify(body));
    });
    server.listen(internalPort, node.address);
    await once(server, 'listening');
    const handle: StandIn = {
      asked: () => asked,
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
  const start = async (node: Node) => {
    const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) => !/^(TS_|TERRABASE_|AWS_|BUCKET_NAME)/.test(name)));
    const output = openSync(join(root, `${node.name}.log`), 'a');
    logs.push(output);
    const spawned = performance.now();
    running.set(node, spawn('nice', ['-n', '19', 'dotnet', join(build, 'terrabase.dll')], {
      env: {
        ...environment, DOTNET_ENVIRONMENT: 'Production', DOTNET_PROCESSOR_COUNT: '2',
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
      expect(running.get(node)?.exitCode, log(node)).toBeNull();
      return request.get(`http://127.0.0.1:${node.port}/readyz`).then((response) => response.status(), () => 0);
    }, { message: `${node.name} ready` }).toBe(200);
    return { spawned, ready: performance.now() };
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
    stream.on('response', (headers) => { status = Number(headers[':status']); });
    stream.on('data', (chunk: string) => { text += chunk; });
    stream.on('error', reject);
    stream.on('end', () => { session.close(); resolveAnswer({ status, body: text }); });
    stream.end(payload);
  });
  const json = async (node: Node, method: string, path: string, body: unknown, user?: string, expected = 200) => {
    const answer = await internal(node, method, path, body, user);
    expect(answer.status, answer.body).toBe(expected);
    return JSON.parse(answer.body);
  };
  const build = process.env.E2E_BUILD ? join(process.env.E2E_BUILD, 'terrabase') : resolve(e2eRoot, '../src/TerraBase/bin/Release/net11.0');
  const { a, b, c } = nodes;
  try {
    const compiler = spawn('cc', ['-shared', '-fPIC', '-Wall', '-Wextra', '-Werror', join(e2eRoot, 'clock/replay.c'), '-o', library, '-ldl']);
    let compileErrors = '';
    compiler.stderr.on('data', (chunk: Buffer) => { compileErrors += chunk.toString(); });
    expect((await once(compiler, 'exit'))[0], compileErrors).toBe(0);
    symlinkSync(library, join(root, 'libc.so'));
    writeFileSync(peers, Object.values(nodes).map((node) => `${node.name} ${node.region} ${node.address}\n`).join(''));
    for (const node of Object.values(nodes)) {
      set(node, 0);
      mkdirSync(join(root, `${node.name}-data`));
    }

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
    const refusedAtHome = await sql(a, "INSERT INTO clock_rows VALUES (2, 'home ahead')");
    expect(refusedAtHome.status, refusedAtHome.body).toBe(503);
    expect(JSON.parse(refusedAtHome.body).error.code).toBe('unavailable');
    expect(await metric(a, 'terrabase_clock_stamps_total', 'outcome="issued"')).toBe(0);
    await expect.poll(() => metric(a, 'terrabase_clock_stamps_total', 'outcome="refused"'), { message: 'the home ahead refused its stamp' }).toBeGreaterThanOrEqual(1);
    observations.push('home started 600 s ahead: issued=0, write at the home 503 unavailable and not committed');

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
    await stop(c);
    await opens(c, 'healthy restart 3');

    const stepsBefore = await metric(a, 'terrabase_clock_steps_total');
    set(a, 600_000);
    await expect.poll(() => allowed(a), { message: 'a step closes the node at once', intervals: [100] }).toBe(0);
    set(a, 0);
    await expect.poll(() => metric(a, 'terrabase_clock_steps_total'), { message: 'the step is counted' }).toBeGreaterThan(stepsBefore);
    const readings: string[] = [];
    await expect.poll(async () => {
      const rounds = await metric(a, 'terrabase_clock_agreeing_rounds');
      const open = await allowed(a);
      readings.push(`${open}@${rounds}`);
      if (open === 1) expect(rounds).toBeGreaterThanOrEqual(2);
      return open;
    }, { message: 'a step reopens only after two agreeing rounds begun after it', intervals: [100] }).toBe(1);
    expect(readings[0]).toMatch(/^0@[01]$/);
    observations.push(`step while open: closed at once, readings (allowed@rounds) ${readings.join(' ')}`);
    expect(await allowed(b)).toBe(1);
    expect(await allowed(c)).toBe(1);
    observations.push('one wrong clock among three closed alone: b and c stayed open');

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
    observations.push('two disagreeing nodes, third silent: both closed through three more rounds');
    set(b, 0);
    await expect.poll(async () => [await allowed(a), await allowed(b)], { message: 'both reopen once they agree' }).toEqual([1, 1]);
    set(a, 0, 64);
    await expect.poll(() => allowed(a), { message: 'an unsynchronised kernel closes the node' }).toBe(0);
    set(a, 0, 0, 50_001);
    await expect.poll(() => metric(a, 'terrabase_clock_agreeing_rounds'), { message: 'a kernel error estimate over 50 ms keeps it closed' }).toBe(0);
    expect(await allowed(a)).toBe(0);
    set(a, 0);
    await expect.poll(() => allowed(a), { message: 'a synchronised kernel within 50 ms reopens it, with a 16 s maximum error' }).toBe(1);
    observations.push('kernel unsynchronised: closed; estimated error 50.001 ms: closed; 1 ms with maximum error 16 s: open');

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
    const refusedFirst = await sql(a, "INSERT INTO clock_rows VALUES (6, 'first of its build ahead')");
    expect(refusedFirst.status, refusedFirst.body).toBe(503);
    expect(JSON.parse(refusedFirst.body).error.code).toBe('unavailable');
    expect(await metric(a, 'terrabase_clock_stamps_total', 'outcome="issued"')).toBe(0);
    observations.push('first of its build, 600 s ahead with the kernel unsynchronised: mode closed/kernel through four rounds, home write 503 unavailable, issued=0');
    set(a, 0);
    await expect.poll(async () => [await mode(a), await allowed(a)], { message: 'the kernel is good again and a reopens as first of its build' })
      .toEqual(['first_of_build/none', 1]);

    await previousB.close();
    set(b, 600_000);
    await start(b);
    await expect.poll(() => mode(a), { message: 'a peer that answers the route and disagrees closes the node', intervals: [100] }).toBe('closed/peers_disagree');
    expect(transitions(a)).toEqual(['first_of_build/none', 'closed/peers_disagree']);
    await holds(a, 'an answering peer outranks a peer of the previous build', 'closed/peers_disagree 0', [previousC], 3);
    const rows = await json(a, 'POST', sqlPath, { statements: [{ sql: 'SELECT id FROM clock_rows ORDER BY id' }] });
    expect(rows.results[0].rows).toEqual([[1], [4], [5]]);
    observations.push('first of its build, then one peer answers 600 s ahead while the other still runs the previous build: first_of_build -> closed/peers_disagree with no step between, closed through three rounds');
    observations.push(`open times after /readyz in ms (second and third node, then restarts 1-3): ${opened.map(Math.round).join(', ')}`);
    console.log(observations.join('\n'));
  } finally {
    for (const node of [...running.keys()]) await stop(node);
    for (const stand of [...standIns]) await stand.close();
    for (const output of logs) closeSync(output);
    await info.attach('clock-trust', { body: observations.join('\n'), contentType: 'text/plain' });
    rmSync(root, { recursive: true, force: true });
  }
});
