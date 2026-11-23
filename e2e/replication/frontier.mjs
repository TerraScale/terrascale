import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { appendFileSync, closeSync, mkdirSync, openSync, writeFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { setPriority } from 'node:os';
import { resolve } from 'node:path';
import { holdSync } from '../env/ports.mjs';

const journeyState = process.env.E2E_STATE;
assert(journeyState, 'E2E_STATE names the isolated local journey namespace');
assert(process.env.E2E_BUILD, 'E2E_BUILD holds the TerraBase build');
assert(process.env.DOTNET_ROOT, 'DOTNET_ROOT holds the .NET runtime');
assert.equal(process.env.E2E_REGIONS, '2');

const P = holdSync({
  internal: 8081, quic: 8443, gru: 8080, fra: 8180, extra: 8280,
  metrics: 9091, metricsFra: 9191, metricsExtra: 9291, pg: 6432, pgFra: 6532, pgExtra: 6632,
}, 'block').ports;
const state = resolve(journeyState, 'replication-' + randomBytes(6).toString('hex'));
mkdirSync(state + '/logs', { recursive: true, mode: 0o700 });
console.log('local state: ' + state);
const peer = randomBytes(32).toString('hex');
const master = randomBytes(32);
appendFileSync(journeyState + '/secrets', [peer, master.toString('base64')].join('\n') + '\n', { mode: 0o600 });

const nodes = {
  gru: { name: 'journey-1', region: 'gru', host: '127.0.0.1', port: P.gru, metrics: P.metrics, pg: P.pg },
  fra: { name: 'journey-2', region: 'fra', host: '127.0.0.2', port: P.fra, metrics: P.metricsFra, pg: P.pgFra },
  gru2: { name: 'journey-3', region: 'gru', host: '127.0.0.3', port: P.extra, metrics: P.metricsExtra, pg: P.pgExtra },
};
const all = Object.values(nodes);
writeFileSync(state + '/peers', all.map(node => [node.name, node.region, node.host, P.quic, P.internal].join(' ') + '\n').join(''));

const processes = new Map();
const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
function id() {
  let value = BigInt(Date.now()) << 80n | BigInt('0x' + randomBytes(10).toString('hex'));
  let encoded = '';
  for (let i = 0; i < 26; i++) {
    encoded = alphabet[Number(value & 31n)] + encoded;
    value >>= 5n;
  }
  return encoded;
}

function start(node) {
  const log = openSync(resolve(state, 'logs', node.name + '.log'), 'a', 0o600);
  const child = spawn(resolve(process.env.DOTNET_ROOT, 'dotnet'), [resolve(process.env.E2E_BUILD, 'terrabase/terrabase.dll')], {
    env: {
      ...Object.fromEntries(Object.entries(process.env).filter(([name]) => !/^(TS_|TERRABASE_|AWS_|BUCKET_NAME)/.test(name))),
      DOTNET_ENVIRONMENT: 'Production', DOTNET_PROCESSOR_COUNT: '4',
      TS_REGION: node.region, TS_NODE: node.name, TS_PEERS_FILE: state + '/peers',
      TERRABASE_DATA_DIRECTORY: state + '/' + node.name, TERRABASE_PORT: String(node.port), TERRABASE_INTERNAL_PORT: String(P.internal),
      TERRABASE_QUIC_PORT: String(P.quic), TERRABASE_METRICS_PORT: String(node.metrics), TERRABASE_POSTGRES_PORT: String(node.pg),
      TERRABASE_AUTH_ISSUER: 'https://auth.terrascale.tech/terrascale', TERRABASE_AUTH_AUDIENCE: 'https://api.terrascale.tech',
      TERRABASE_PEER_SECRET: peer, TERRABASE_MASTER_KEY: master.toString('base64'),
    },
    stdio: ['ignore', log, log],
  });
  closeSync(log);
  setPriority(child.pid, 19);
  child.finished = once(child, 'exit');
  processes.set(node, child);
}

async function stop(node) {
  const child = processes.get(node);
  processes.delete(node);
  child.kill('SIGTERM');
  await child.finished;
}

async function call(node, method, path, { body, user, headers = {} } = {}) {
  const session = connect('http://' + node.host + ':' + P.internal);
  session.on('error', () => {});
  const payload = body === undefined ? undefined : JSON.stringify(body);
  try {
    const stream = session.request({
      ':method': method, ':path': path, 'x-terrabase-peer-credential': peer, ...headers,
      ...(payload === undefined ? {} : { 'content-type': 'application/json', 'content-length': Buffer.byteLength(payload) }),
      ...(user ? { 'x-terrabase-user-id': user } : {}),
    });
    const response = once(stream, 'response');
    const chunks = [];
    stream.on('data', chunk => chunks.push(chunk));
    stream.end(payload);
    const [answered] = await response;
    await once(stream, 'end');
    const text = Buffer.concat(chunks).toString();
    return { status: Number(answered[':status']), headers: answered, body: text ? JSON.parse(text) : null };
  } finally {
    session.close();
  }
}

async function expectStatus(node, method, path, expected, options) {
  const answer = await call(node, method, path, options);
  assert.equal(answer.status, expected, method + ' ' + path + ' on ' + node.name + ': ' + JSON.stringify(answer.body));
  return answer.body;
}

async function until(label, probe, timeout = 90_000) {
  const deadline = Date.now() + timeout;
  let last;
  while (Date.now() < deadline) {
    last = await probe().catch(error => ({ done: false, seen: error.message }));
    if (last.done) return last.value;
    await new Promise(resolveWait => setTimeout(resolveWait, 250));
  }
  assert.fail(label + ' did not happen within ' + timeout + ' ms; last seen ' + JSON.stringify(last?.seen));
}

const ready = node => until(node.name + ' ready', async () => {
  const answer = await call(node, 'GET', '/readyz');
  return { done: answer.status === 200 && answer.body.region === node.region, seen: answer.body };
});

let sqlPath;
const sql = (node, statements, headers = {}) => call(node, 'POST', sqlPath, { body: { statements: statements.map(text => ({ sql: text })) }, headers });

async function committed(node, statements, headers = {}) {
  const answer = await sql(node, statements, headers);
  assert.equal(answer.status, 200, node.name + ': ' + JSON.stringify(answer.body));
  assert.equal(answer.headers['terrascale-bookmark'], answer.body.bookmark, 'the bookmark header matches the body');
  assert.equal(answer.headers['terrascale-consistency'], answer.body.consistency, 'the consistency header matches the body');
  assert.equal(answer.body.consistency, headers['terrascale-consistency'] ?? 'fast');
  return answer.body;
}

const rows = async (node, query) => (await committed(node, [query])).results[0].rows;

async function readable(node, query) {
  const answer = await sql(node, [query]);
  if (answer.status === 200) return { done: true, value: answer.body.results[0].rows };
  assert(answer.status === 404 || answer.body?.error?.code === 'undefined_table', node.name + ': ' + JSON.stringify(answer.body));
  return { done: false, seen: answer.body };
}

async function converged(label, query, accept) {
  return until(label, async () => {
    const seen = [];
    for (const node of all) {
      const read = await readable(node, query);
      if (!read.done) return { done: false, seen: node.name + ' ' + JSON.stringify(read.seen) };
      seen.push(read.value);
    }
    const same = seen.every(value => JSON.stringify(value) === JSON.stringify(seen[0]));
    return { done: same && accept(seen[0]), value: seen[0], seen };
  });
}

try {
  for (const node of all) start(node);
  await Promise.all(all.map(ready));

  const user = id();
  const org = await expectStatus(nodes.gru, 'POST', '/v1/management/organizations', 201, { body: { name: 'Frontier' }, user });
  await expectStatus(nodes.gru, 'PUT', '/v1/management/organizations/' + org.id + '/plan', 200, { body: { plan: 'pro' } });
  const project = await expectStatus(nodes.gru, 'POST', '/v1/management/organizations/' + org.id + '/projects', 201, { body: { name: 'Frontier' }, user });
  const database = await expectStatus(nodes.gru, 'POST', '/v1/projects/' + project.id + '/databases', 201, { body: { name: 'frontier', regions: ['gru', 'fra'] }, user });
  assert.deepEqual(database.regions, ['fra', 'gru']);
  sqlPath = '/v1/projects/' + project.id + '/databases/' + database.id + '/sql';

  const peers = await until('every peer region healthy', async () => {
    const status = await call(nodes.gru, 'GET', '/v1/projects/' + project.id + '/databases/' + database.id + '/regions', { user });
    return { done: status.status === 200 && status.body.regions.every(row => row.healthy), value: status.body.regions, seen: status.body };
  });
  assert.deepEqual(peers.map(row => row.region), ['fra', 'gru']);
  console.log('regions: fra and gru are healthy active-active peers of the database');

  await committed(nodes.gru, ['CREATE TABLE items (id INTEGER PRIMARY KEY, value TEXT NOT NULL)', "INSERT INTO items VALUES (100, 'seed')"]);
  await committed(nodes.gru, ['CREATE TABLE counter (id INTEGER PRIMARY KEY, value INTEGER NOT NULL)', 'INSERT INTO counter VALUES (1, 0)']);
  await converged('the schema reaches every node', 'SELECT id, value FROM items ORDER BY id', value => value.length === 1);
  await converged('the counter reaches every node', 'SELECT value FROM counter', value => value.length === 1);

  const written = ['gru', 'fra', 'gru2'];
  const started = Date.now();
  await Promise.all([
    ...Array.from({ length: 10 }, (_, index) => committed(nodes.gru, [`INSERT INTO items VALUES (${index + 1}, 'gru')`])),
    ...Array.from({ length: 10 }, (_, index) => committed(nodes.fra, [`INSERT INTO items VALUES (${index + 11}, 'fra')`])),
    ...written.map(key => committed(nodes[key], [`UPDATE items SET value = '${key}' WHERE id = 100`])),
  ]);
  const merged = await converged('concurrent gru and fra writes converge on every node', 'SELECT id, value FROM items ORDER BY id', value => value.length === 21);
  assert.deepEqual(merged.slice(0, 20), Array.from({ length: 20 }, (_, index) => [index + 1, index < 10 ? 'gru' : 'fra']));
  assert(written.includes(merged[20][1]), 'the same-row winner is one of the concurrent writes: ' + merged[20][1]);
  console.log('(a) 20 distinct-key writes and 3 same-key updates from gru, fra and gru2 converged on all 3 nodes in '
    + (Date.now() - started) + ' ms; row 100 = ' + merged[20][1]);

  const write = await committed(nodes.fra, ["INSERT INTO items VALUES (200, 'read your writes')"]);
  assert(write.bookmark.length > 0, 'a write returns a non-empty bookmark');
  const read = await committed(nodes.gru2, ['SELECT value FROM items WHERE id = 200'], { 'terrascale-bookmark': write.bookmark });
  assert.deepEqual(read.results[0].rows, [['read your writes']]);
  console.log('(b) a write on fra read on gru2 with its TerraScale-Bookmark at once');

  const strong = { 'terrascale-consistency': 'strong' };
  const increment = 'UPDATE counter SET value = value + 1 WHERE id = 1';
  let commits = 0;
  let conflicts = 0;
  for (let round = 0; round < 20 && conflicts === 0; round++) {
    const answers = await Promise.all([sql(nodes.gru, [increment], strong), sql(nodes.fra, [increment], strong)]);
    for (const answer of answers) {
      if (answer.status === 200) {
        assert.equal(answer.body.consistency, 'strong');
        commits++;
        continue;
      }
      assert.equal(answer.status, 409, JSON.stringify(answer.body));
      assert.equal(answer.body.error.code, 'serialization_conflict');
      assert.equal(answer.body.error.sqlState, '40001');
      conflicts++;
    }
    assert(answers.some(answer => answer.status === 200), 'one of two conflicting strong writes commits');
  }
  assert(conflicts > 0, 'concurrent conflicting strong writes from gru and fra produced a serialization conflict');
  await converged('every strong commit is applied once on every node', 'SELECT value FROM counter', value => value[0][0] === commits);
  console.log('(c) strong OCC: ' + commits + ' commits, ' + conflicts + ' answered 409 serialization_conflict 40001; counter = ' + commits + ' everywhere');

  await stop(nodes.fra);
  for (let index = 0; index < 10; index++) await committed(nodes.gru, [`INSERT INTO items VALUES (${300 + index}, 'while fra was down')`]);
  start(nodes.fra);
  await ready(nodes.fra);
  const caught = await converged('fra catches up through anti-entropy after a restart', 'SELECT COUNT(*) FROM items', value => value[0][0] === 32);
  assert.deepEqual(caught, [[32]]);
  assert.deepEqual(await rows(nodes.fra, "SELECT COUNT(*) FROM items WHERE value = 'while fra was down'"), [[10]]);
  console.log('(d) 10 writes on gru while fra was stopped reached fra after its restart');

  const malformed = await sql(nodes.gru, ['SELECT 1'], { 'terrascale-bookmark': 'not a bookmark!' });
  assert.equal(malformed.status, 400, JSON.stringify(malformed.body));
  assert.equal(malformed.body.error.code, 'invalid_bookmark');
  console.log('(e) a malformed TerraScale-Bookmark answers 400 invalid_bookmark');
  console.log('replication frontier proof passed');
} finally {
  await Promise.all([...processes.keys()].map(stop));
}
