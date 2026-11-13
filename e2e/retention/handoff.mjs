import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes, createHash, createHmac } from 'node:crypto';
import { once } from 'node:events';
import { closeSync, existsSync, mkdirSync, openSync, readFileSync, readdirSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { dirname, join } from 'node:path';
import { expect } from '@playwright/test';

const state = process.env.E2E_STATE;
const build = process.env.E2E_BUILD;
assert(state && build && process.env.DOTNET_ROOT, 'run inside the journey namespace with DOTNET_ROOT set');
const owner = JSON.parse(readFileSync(join(state, 'placement-setup.journey.json'), 'utf8'));
const secret = readFileSync(join(state, 'secrets'), 'utf8').split('\n')[0];
const directory = join(state, 'retained-handoff');
mkdirSync(directory, { recursive: true, mode: 0o700 });
const tokenResponse = await fetch('https://auth.terrascale.tech/terrascale/oauth/token', {
  method: 'POST',
  headers: { 'content-type': 'application/x-www-form-urlencoded' },
  body: new URLSearchParams({
    grant_type: 'client_credentials', client_id: owner.apiKey.slice(0, 26), client_secret: owner.apiKey,
    project: owner.projectId, scope: 'sql:read sql:write databases:read databases:write',
  }),
});
assert.equal(tokenResponse.status, 200);
const token = (await tokenResponse.json()).access_token;
writeFileSync(join(state, 'secrets.d', 'retained-handoff-token'), token, { mode: 0o600 });
const environment = Object.fromEntries(readFileSync('/proc/' + readFileSync(join(state, 'pids', 'terrabase.pid'), 'utf8').trim() + '/environ', 'utf8')
  .split('\0').filter(Boolean).map(value => { const at = value.indexOf('='); return [value.slice(0, at), value.slice(at + 1)]; }));
const nodes = [];
const natsToken = randomBytes(32).toString('hex');
writeFileSync(join(state, 'secrets.d', 'retained-handoff-nats'), natsToken, { mode: 0o600 });
writeFileSync(join(directory, 'nats-token'), natsToken, { mode: 0o600 });
const clock = join(state, 'retained-handoff-clock');
function setClock(seconds, node) {
  const file = node === undefined ? clock : clock + '-' + node;
  writeFileSync(file + '.next', String(seconds));
  renameSync(file + '.next', file);
}
setClock(0);
const peers = join(directory, 'peers');
writeFileSync(peers, 'journey-1 gru 127.0.0.1\njourney-2 gru 127.0.0.2\n');
const natsConfig = join(directory, 'nats.conf');
writeFileSync(natsConfig, JSON.stringify({ listen: '127.0.0.1:4222', authorization: { token: natsToken }, jetstream: { store_dir: join(directory, 'nats') } }), { mode: 0o600 });
const clockLibrary = process.env.E2E_RETENTION_CLOCK;
assert(clockLibrary && existsSync(clockLibrary), 'run retention/run.sh to build the wall clock');

function start(name, executable, args, env) {
  const output = openSync(join(directory, name + '.log'), 'w', 0o600);
  const process = spawn(executable, args, { env, stdio: ['ignore', output, output] });
  closeSync(output);
  const stopped = once(process, 'exit');
  nodes.push({ name, process, stopped });
  return process;
}

async function request(node, path, method = 'GET', body, headers = {}, localAddress) {
  if (path.startsWith('/v1/projects/')) {
    const response = await fetch('http://127.0.0.1:' + (node === 1 ? 8080 : 8180) + path, {
      method, headers: { authorization: 'Bearer ' + token, ...headers },
      ...(body === undefined ? {} : { body: typeof body === 'string' ? body : JSON.stringify(body) }),
    });
    const text = await response.text();
    return { status: response.status, headers: Object.fromEntries(response.headers), text, json: () => JSON.parse(text) };
  }
  const session = connect('http://127.0.0.' + node + ':8081', localAddress ? { localAddress } : {});
  try {
    return await new Promise((resolve, reject) => {
      session.once('error', reject);
      const stream = session.request({ ':path': path, ':method': method, 'x-terrabase-peer-credential': secret, authorization: 'Bearer ' + token, ...headers });
      const chunks = [];
      let status;
      let responseHeaders;
      stream.on('response', headers => { status = headers[':status']; responseHeaders = headers; });
      stream.on('data', chunk => chunks.push(chunk));
      stream.on('error', reject);
      stream.on('end', () => {
        const text = Buffer.concat(chunks).toString();
        resolve({ status, headers: responseHeaders, text, json: () => JSON.parse(text) });
      });
      stream.end(body === undefined ? undefined : Buffer.isBuffer(body) ? body : JSON.stringify(body));
    });
  } finally {
    session.destroy();
  }
}

const project = '/v1/projects/' + owner.projectId;
const database = id => project + '/databases/' + id;
const deleted = id => project + '/deleted-databases/' + id;
async function checked(node, path, method, body, expected = 200) {
  const answer = await request(node, path, method, body, body === undefined ? {} : { 'content-type': 'application/json' });
  assert.equal(answer.status, expected, method + ' ' + path + ': ' + answer.text);
  if (expected === 503) assert.equal(answer.headers['retry-after'], '1');
  return answer;
}

const sha256 = value => createHash('sha256').update(value).digest('hex');
const hmac = (key, value) => createHmac('sha256', key).update(value).digest();
async function objects(prefix, bucket = 'journey-backups') {
  const [id, secret] = readFileSync(join(state, 'r2'), 'utf8').split('\n');
  const host = 'journey.backups.terrascale.tech';
  const path = '/' + bucket;
  const query = 'prefix=' + encodeURIComponent(prefix) + '&versions=';
  const date = new Date().toISOString().replace(/[-:]|\.\d{3}/g, '');
  const scope = date.slice(0, 8) + '/auto/s3/aws4_request';
  const payload = sha256('');
  const names = 'host;x-amz-content-sha256;x-amz-date';
  const canonical = ['GET', path, query, 'host:' + host + '\nx-amz-content-sha256:' + payload + '\nx-amz-date:' + date + '\n', names, payload].join('\n');
  const key = scope.split('/').reduce((key, part) => hmac(key, part), 'AWS4' + secret);
  const signature = hmac(key, ['AWS4-HMAC-SHA256', date, scope, sha256(canonical)].join('\n')).toString('hex');
  const response = await fetch('https://' + host + path + '?' + query, { headers: {
    'x-amz-date': date, 'x-amz-content-sha256': payload,
    authorization: 'AWS4-HMAC-SHA256 Credential=' + id + '/' + scope + ', SignedHeaders=' + names + ', Signature=' + signature,
  } });
  assert.equal(response.status, 200);
  return [...(await response.text()).matchAll(/<Key>([^<]+)<\/Key>/g)].map(match => match[1]);
}
const scope = id => owner.organizationId + '/' + owner.projectId + '/' + id;
const keys = id => objects('v1/keys/' + scope(id) + '/', 'ts-terrabase-shapes-prod');
const backups = id => objects('v1/' + scope(id) + '/');
const files = (root, id) => readdirSync(root, { recursive: true }).filter(file => file.split('/').pop().startsWith(id));
const roots = [join(state, 'terrabase'), join(state, 'terrabase-fra')];
function displace(root, id, target) {
  const moved = files(root, id).map(file => [join(root, file), join(target, file)]);
  for (const [source, held] of moved) {
    mkdirSync(dirname(held), { recursive: true });
    renameSync(source, held);
  }
  return () => {
    for (const [source, held] of moved) {
      mkdirSync(dirname(source), { recursive: true });
      renameSync(held, source);
    }
  };
}

try {
  for (const name of ['terrabase', 'terrabase-fra']) {
    const pid = Number(readFileSync(join(state, 'pids', name + '.pid'), 'utf8'));
    process.kill(pid, 'SIGTERM');
    await expect.poll(() => existsSync('/proc/' + pid) && !readFileSync('/proc/' + pid + '/stat', 'utf8').split(')')[1].trim().startsWith('Z'), { message: name + ' stopped', timeout: 45_000 }).toBe(false);
  }
  start('nats', 'nats-server', ['-c', natsConfig], environment);
  for (const node of [1, 2]) {
    start('node-' + node, 'dotnet', [join(build, 'terrabase', 'terrabase.dll')], {
      ...environment, DOTNET_ROOT: process.env.DOTNET_ROOT, DOTNET_PROCESSOR_COUNT: '4',
      TS_REGION: 'gru', TS_NODE: 'journey-' + node, TS_PEERS_FILE: peers,
      TS_NATS_URL: 'nats://127.0.0.1:4222', TERRABASE_NATS_TOKEN_FILE: join(directory, 'nats-token'),
      TERRABASE_DATA_DIRECTORY: roots[node - 1], TERRABASE_PORT: node === 1 ? '8080' : '8180',
      TERRABASE_METRICS_PORT: node === 1 ? '9091' : '9191', TERRABASE_POSTGRES_PORT: node === 1 ? '6432' : '6532',
      LD_PRELOAD: clockLibrary, E2E_RETENTION_CLOCK_FILE: clock + '-' + node,
    });
  }
  await expect.poll(async () => {
    try { return (await request(1, '/v1/rollout/status')).json().writer; } catch { return false; }
  }, { message: 'source acquires regional writer lease', timeout: 45_000 }).toBe(true);
  await expect.poll(async () => {
    try { return (await request(2, '/v1/rollout/status')).json().ready; } catch { return false; }
  }, { message: 'peer is ready for handoff', timeout: 45_000 }).toBe(true);
  for (const address of ['127.0.0.1:9091', '127.0.0.2:9191']) {
    await expect.poll(async () => (await fetch('http://' + address + '/metrics')).text(),
      { message: 'node loaded the real auth signing keys', timeout: 45_000 })
      .toMatch(/terrabase_auth_key_fetches_total\{[^}]*outcome="success"[^}]*\} [1-9]/);
  }
  const contract = await fetch('http://127.0.0.1:8081/openapi/v1.json', { headers: { 'x-terrabase-peer-credential': secret } });
  if (contract.status === 200) writeFileSync(join(directory, 'openapi-v1.json'), await contract.text());
  console.log('real nodes: journey-1 and journey-2, gru, shared NATS writer lease, auth keys loaded');
  const seed = await request(1, database(owner.databaseId));
  if (seed.status === 200) await checked(1, database(owner.databaseId), 'DELETE', undefined, 204);
  const created = [];
  for (const name of ['restore', 'purge', 'unreachable', 'expiry']) {
    const record = (await checked(1, project + '/databases', 'POST', { name: 'retained-handoff-' + name, regions: ['gru'] }, 201)).json();
    await checked(1, database(record.id) + '/sql/execute', 'POST', { statements: [{ sql: 'CREATE TABLE kept (value INTEGER)' }, { sql: 'INSERT INTO kept VALUES (11), (22), (33)' }] });
    await expect.poll(async () => (await keys(record.id)).filter(key => !key.endsWith('/shredded')).length, { timeout: 45_000 }).toBeGreaterThan(0);
    await expect.poll(async () => (await backups(record.id)).length, { timeout: 45_000 }).toBeGreaterThan(0);
    await checked(1, database(record.id), 'DELETE', undefined, 204);
    const hidden = await request(1, database(record.id) + '/sql/execute', 'POST', JSON.stringify({ statements: [{ sql: 'SELECT * FROM kept' }] }), { 'content-type': 'application/json' });
    assert.equal(hidden.status, 404);
    created.push(record.id);
    console.log('created rows [11,22,33], backup and key present, DELETE 204, client SQL 404: ' + record.id);
  }
  const [restoreId, purgeId, unreachableId, expiryId] = created;
  await expect.poll(() => files(roots[1], restoreId), { message: 'candidate has no retained source files before handoff', timeout: 45_000 }).toEqual([]);
  await expect.poll(() => files(roots[1], purgeId), { message: 'candidate has no second retained source files before handoff', timeout: 45_000 }).toEqual([]);
  console.log('candidate has no retained files before handoff');
  const pull = '/v1/sync/organizations/' + owner.organizationId + '/projects/' + owner.projectId + '/databases/' + restoreId + '/pull-updates';
  const publicPull = await fetch('http://127.0.0.1:8080' + pull, {
    method: 'POST', body: Buffer.alloc(0),
    headers: { authorization: 'Bearer ' + token, 'x-terrabase-peer-credential': secret, 'content-type': 'application/protobuf' },
  });
  assert.equal(publicPull.status, 404);
  console.log('client listener: retained pull 404');
  writeFileSync(peers, 'journey-1 gru 127.0.0.1\njourney-2 gru 127.0.0.2\njourney-foreign fra 127.0.0.4\n');
  const denied = await request(1, pull, 'POST', Buffer.alloc(0), { 'content-type': 'application/protobuf' }, '127.0.0.4');
  assert.equal(denied.status, 404, denied.text);
  writeFileSync(peers, 'journey-1 gru 127.0.0.1\njourney-2 gru 127.0.0.2\n');
  console.log('authenticated known peer in fra: retained pull 404');
  const keptKeys = await keys(purgeId);
  const keptBackups = await backups(purgeId);
  const held = join(directory, 'held');
  mkdirSync(held, { recursive: true });
  const returnSourceFiles = displace(roots[0], purgeId, join(held, 'source'));
  try {
    await checked(1, deleted(purgeId) + '/restore', 'POST', undefined, 503);
    await checked(1, deleted(purgeId), 'DELETE', undefined, 503);
    assert.deepEqual(await keys(purgeId), keptKeys);
    assert.deepEqual(await backups(purgeId), keptBackups);
    console.log('missing files: restore 503, purge 503, keys and backups unchanged');
  } finally {
    returnSourceFiles();
  }
  const pageIds = [];
  const beforePages = (await checked(1, project + '/deleted-databases?limit=100', 'GET')).json();
  let retainedCount = beforePages.items.length;
  let next = beforePages.next;
  while (next) {
    const page = (await checked(1, project + '/deleted-databases?limit=100&after=' + encodeURIComponent(next), 'GET')).json();
    retainedCount += page.items.length;
    next = page.next;
  }
  for (let index = retainedCount; index < 300; index++) {
    const record = (await checked(1, project + '/databases', 'POST', { name: 'retained-page-' + index, regions: ['gru'] }, 201)).json();
    await checked(1, database(record.id) + '/sql/execute', 'POST', { statements: [{ sql: 'CREATE TABLE proof (value INTEGER)' }, { sql: 'INSERT INTO proof VALUES (' + index + ')' }] });
    await checked(1, database(record.id), 'DELETE', undefined, 204);
    pageIds.push(record.id);
  }
  const handoffStarted = performance.now();
  await checked(1, '/v1/rollout/drain?target=journey-2', 'POST', undefined, 204);
  assert.equal((await checked(2, '/v1/rollout/status')).json().writer, true);
  assert(files(roots[1], restoreId).some(file => file.endsWith('.db')));
  assert(files(roots[1], purgeId).some(file => file.endsWith('.db')));
  console.log('POST /v1/rollout/drain?target=journey-2 -> 204; peer writer=true; both retained databases have files');
  await checked(2, deleted(restoreId) + '/restore', 'POST');
  const rows = (await checked(2, database(restoreId) + '/sql/execute', 'POST', { statements: [{ sql: 'SELECT value FROM kept ORDER BY value' }] })).json().results[0].rows;
  assert.deepEqual(rows, [[11], [22], [33]]);
  console.log('restore on peer -> 200; original rows ' + JSON.stringify(rows));
  assert.equal(pageIds.length + retainedCount, 300);
  const received = [];
  let retainedAfter;
  do {
    const page = (await checked(2, project + '/deleted-databases?limit=100' + (retainedAfter ? '&after=' + encodeURIComponent(retainedAfter) : ''), 'GET')).json();
    received.push(...page.items.map(record => record.id));
    retainedAfter = page.next;
  } while (retainedAfter);
  assert.equal(received.length, 299);
  assert(pageIds.every(id => received.includes(id)));
  console.log('300 retained databases crossed handoff in bounded pages; verified manifest; elapsed ms=' + Math.round(performance.now() - handoffStarted));

  const eightDays = 8 * 24 * 60 * 60;
  setClock(eightDays, 1);
  await expect.poll(async () => (await fetch('http://127.0.0.1:9091/metrics')).text(),
    { message: 'outgoing maintenance runs after the old retention deadline', timeout: 45_000 }).toContain('terrabase_purge_pending');
  const stillLive = (await checked(2, database(restoreId) + '/sql/execute', 'POST', { statements: [{ sql: 'SELECT value FROM kept ORDER BY value' }] })).json().results[0].rows;
  assert.deepEqual(stillLive, [[11], [22], [33]]);
  assert((await keys(restoreId)).some(key => !key.endsWith('/shredded')));
  console.log('A clock +8d after restore on B: live rows [11,22,33], keys intact, A remains nonwriter');
  setClock(0, 1);

  const purge = await checked(2, deleted(purgeId), 'DELETE', undefined, 202);
  assert.equal(purge.json().state, 'destroying');
  await expect.poll(() => files(roots[0], purgeId).filter(file => file.endsWith('.db') || file.endsWith('.key')), { timeout: 45_000 }).toEqual([]);
  await expect.poll(() => files(roots[1], purgeId).filter(file => file.endsWith('.db') || file.endsWith('.key')), { timeout: 45_000 }).toEqual([]);
  await expect.poll(async () => (await keys(purgeId)).filter(key => !key.endsWith('/shredded')), { timeout: 45_000 }).toEqual([]);
  console.log('purge on B with A alive: A and B files=0, wrapped keys=0, durable job returned 202');

  const outgoing = nodes.find(node => node.name === 'node-1');
  outgoing.process.kill('SIGSTOP');
  try {
    const pending = await checked(2, deleted(unreachableId), 'DELETE', undefined, 202);
    assert.equal(pending.json().state, 'destroying');
    assert(pending.json().outstanding.includes('keys'));
    assert(files(roots[0], unreachableId).some(file => file.endsWith('.db')));
    const same = await checked(2, deleted(unreachableId), 'GET');
    assert.equal(same.json().id, unreachableId);
    assert.equal(same.json().state, 'destroying');
    console.log('A unreachable but alive: DELETE 202; GET same job destroying; A files still present');
  } finally {
    outgoing.process.kill('SIGCONT');
  }
  const repeated = await request(2, deleted(unreachableId), 'DELETE');
  assert([202, 204].includes(repeated.status), repeated.text);
  await expect.poll(() => files(roots[0], unreachableId).filter(file => file.endsWith('.db') || file.endsWith('.key')), { timeout: 45_000 }).toEqual([]);
  console.log('A returns: repeated purge addresses same job, A files and keys erased');

  await expect.poll(async () => (await checked(2, deleted(purgeId), 'GET')).json().state, { timeout: 45_000 }).toBe('complete');
  assert.deepEqual((await checked(2, deleted(purgeId), 'GET')).json().outstanding, []);
  assert.deepEqual(await backups(purgeId), []);
  await checked(2, deleted(purgeId), 'DELETE', undefined, 404);
  console.log('verified files=0, keys=0, backup objects=0: GET complete; subsequent DELETE 404 gone');

  const beforeExpiryKeys = await keys(expiryId);
  setClock(eightDays, 2);
  setClock(eightDays);
  await expect.poll(async () => (await fetch('http://127.0.0.2:9191/metrics')).text(), { timeout: 45_000 })
    .toMatch(/terrabase_retention_expiries_waiting_confirmation [1-9]/);
  assert(files(roots[1], expiryId).some(file => file.endsWith('.db')));
  assert.deepEqual(await keys(expiryId), beforeExpiryKeys);
  console.log('writer B clock +8d, partner A correct: expiry waits for confirmation; retained files and keys intact');
  setClock(eightDays, 1);
  await expect.poll(() => files(roots[1], expiryId).filter(file => file.endsWith('.db') || file.endsWith('.key')), { timeout: 45_000 }).toEqual([]);
  await expect.poll(() => files(roots[0], expiryId).filter(file => file.endsWith('.db') || file.endsWith('.key')), { timeout: 45_000 }).toEqual([]);
  await expect.poll(async () => (await keys(expiryId)).filter(key => !key.endsWith('/shredded')), { timeout: 45_000 }).toEqual([]);
  console.log('both clocks +8d: scheduled expiry authorized, A and B files=0, wrapped keys=0');

} finally {
  for (const node of nodes.reverse()) {
    if (node.process.exitCode === null && node.process.signalCode === null) {
      node.process.kill('SIGCONT');
      node.process.kill('SIGTERM');
    }
    await node.stopped;
  }
  console.log('all proof nodes and NATS stopped');
}
