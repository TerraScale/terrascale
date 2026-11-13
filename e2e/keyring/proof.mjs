import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createDecipheriv, createHash, createHmac, randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { closeSync, cpSync, existsSync, mkdirSync, openSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { request as httpRequest } from 'node:http';
import { createServer } from 'node:https';
import { join, resolve } from 'node:path';
import { setInterval } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { internal, project } from './operations.mjs';

const root = resolve(fileURLToPath(new URL('../..', import.meta.url)));
const profile = process.env.E2E_STATE;
const baseline = resolve(process.env.E2E_KEYRING_BASELINE);
const candidate = join(process.env.E2E_BUILD, 'terrabase', 'terrabase.dll');
const data = join(profile, 'data');
const master = randomBytes(32).toString('base64');
const next = randomBytes(32).toString('base64');
const peer = randomBytes(32).toString('hex');
const access = randomBytes(16).toString('hex');
const secret = randomBytes(32).toString('hex');
writeFileSync(join(profile, 'r2'), `${access}\n${secret}\n`, { mode: 0o600 });
writeFileSync(join(profile, 'secrets'), `${master}\n${next}\n${peer}\n${access}\n${secret}\n`, { mode: 0o600 });
const clean = Object.fromEntries(Object.entries(process.env).filter(([name]) =>
  !name.startsWith('TERRABASE_') && !name.startsWith('TS_') && !name.startsWith('AWS_') && name !== 'BUCKET_NAME'));
const children = new Set();
function startProcess(binary, args, name, environment) {
  const output = openSync(join(profile, `${name}.log`), 'w', 0o600);
  const child = spawn(binary, args, { env: environment, stdio: ['ignore', output, output] });
  closeSync(output);
  children.add(child);
  return child;
}
async function stop(child) {
  if (child.exitCode === null && child.signalCode === null) {
    const exited = once(child, 'exit');
    child.kill('SIGTERM');
    await exited;
  }
  children.delete(child);
}
let standin = startProcess('node', [join(root, 'e2e/env/r2.mjs')], 'object-store', { ...clean, E2E_REGIONS: '2' });
const requests = [];
const proxy = createServer({ key: readFileSync(join(profile, 'tls.key')), cert: readFileSync(join(profile, 'tls.pem')) }, (request, response) => {
  requests.push({ method: request.method, path: request.url, range: request.headers.range, match: request.headers['if-match'], absent: request.headers['if-none-match'] });
  const upstream = httpRequest({ hostname: '127.0.0.1', port: 8028, method: request.method, path: request.url, headers: request.headers }, answer => {
    response.writeHead(answer.statusCode, answer.headers);
    answer.pipe(response);
  });
  upstream.on('error', () => { response.writeHead(502); response.end(); });
  request.pipe(upstream);
});
proxy.listen(443, '0.0.0.0');
await once(proxy, 'listening');
const sha = body => createHash('sha256').update(body).digest('hex');
const hmac = (key, value) => createHmac('sha256', key).update(value).digest();
async function object(method, bucket, path, query = '', body, conditions = {}) {
  const host = bucket === 'journey-backups' ? 'journey.backups.terrascale.tech' : '6cd3f3639e928077593d9b470765efeb.r2.cloudflarestorage.com';
  const target = `/${bucket}/${path}`;
  const date = new Date().toISOString().replace(/[-:]|\.\d{3}/g, '');
  const scope = `${date.slice(0, 8)}/auto/s3/aws4_request`;
  const payload = sha(body ?? '');
  const headers = 'host;x-amz-content-sha256;x-amz-date';
  const canonical = [method, target, query, `host:${host}\nx-amz-content-sha256:${payload}\nx-amz-date:${date}\n`, headers, payload].join('\n');
  const key = scope.split('/').reduce((key, part) => hmac(key, part), `AWS4${secret}`);
  const signature = hmac(key, ['AWS4-HMAC-SHA256', date, scope, sha(canonical)].join('\n')).toString('hex');
  return fetch(`https://${host}${target}${query ? `?${query}` : ''}`, { method, body, headers: {
    ...conditions,
    'x-amz-date': date, 'x-amz-content-sha256': payload,
    authorization: `AWS4-HMAC-SHA256 Credential=${access}/${scope}, SignedHeaders=${headers}, Signature=${signature}`,
  } });
}
async function objects(bucket, prefix) {
  const response = await object('GET', bucket, '', `list-type=2&prefix=${encodeURIComponent(prefix)}`);
  assert.equal(response.status, 200);
  const text = await response.text();
  return [...text.matchAll(/<Key>([^<]+)<\/Key>/g)].map(match => match[1]);
}
const files = directory => readdirSync(directory, { withFileTypes: true }).flatMap(entry =>
  entry.isDirectory() ? files(join(directory, entry.name)) : entry.name.endsWith('.key') ? [join(directory, entry.name)] : []);
let sequence = 0;
function node(binary, active, extra = {}, arguments_ = []) {
  const ring = join(profile, 'ring-' + sequence + '.json');
  writeFileSync(ring, JSON.stringify({ active, keys: active === 'k0' ? { k0: master } : { k0: master, k1: next } }), { mode: 0o600 });
  const command = binary === process.env.E2E_KEYRING_DRIVER
    ? ['exec', '--depsfile', join(process.env.E2E_BUILD, 'terrabase/terrabase.deps.json'),
      '--runtimeconfig', join(process.env.E2E_BUILD, 'terrabase/terrabase.runtimeconfig.json'), binary, ...arguments_]
    : [binary, ...arguments_];
  return startProcess('dotnet', command, `node-${++sequence}`, {
    ...clean, DOTNET_ROOT: process.env.DOTNET_ROOT, DOTNET_PROCESSOR_COUNT: '4', DOTNET_ENVIRONMENT: 'Development',
    TERRABASE_AUTH_ISSUER: 'https://auth.terrascale.tech/terrascale', TERRABASE_AUTH_AUDIENCE: 'https://api.terrascale.tech',
    ...(binary === baseline ? { TERRABASE_MASTER_KEY: master } : { TERRABASE_MASTER_KEYS_FILE: ring }),
    TERRABASE_PEER_SECRET: peer, TERRABASE_DATA_DIRECTORY: data, TERRABASE_MEMORY_BUDGET: '67108864', TERRABASE_STORAGE_THREADS: '1',
    TERRABASE_PORT: '18380', TERRABASE_INTERNAL_PORT: '18381', TERRABASE_POSTGRES_PORT: '18382', TERRABASE_METRICS_PORT: '18383',
    AWS_ENDPOINT_URL_S3: 'https://journey.backups.terrascale.tech', AWS_REGION: 'auto', BUCKET_NAME: 'journey-backups',
    AWS_ACCESS_KEY_ID: access, AWS_SECRET_ACCESS_KEY: secret,
    TERRABASE_SHAPES_ACCESS_KEY_ID: access, TERRABASE_SHAPES_SECRET_ACCESS_KEY: secret, TERRABASE_SHAPES_CDN_API_TOKEN: peer,
    ...extra,
  });
}
async function records(operation, active, output = join(profile, operation + '.json')) {
  const child = node(process.env.E2E_KEYRING_DRIVER, active, {}, [operation, output]);
  const [code] = await once(child, 'exit');
  children.delete(child);
  assert.equal(code, 0, operation + ' driver completed');
  return operation === 'credentials' ? JSON.parse(readFileSync(output, 'utf8')) : undefined;
}
async function until(name, child, condition) {
  const deadline = Date.now() + 30_000;
  for await (const tick of setInterval(200)) {
    void tick;
    assert.equal(child.exitCode, null, `${name}: node remains running`);
    if (await condition()) return;
    assert.ok(Date.now() < deadline, `${name}: condition did not hold`);
  }
}
async function ready(child) {
  await until('readiness', child, async () => {
    try { return (await fetch('http://127.0.0.1:18380/readyz')).status === 200; } catch { return false; }
  });
}
const metrics = async () => (await fetch('http://127.0.0.1:18383/metrics')).text();
const value = (text, name, label = '') => Number(text.split('\n').find(line => line.startsWith(name) && (!label || line.includes(label)))?.split(' ').at(-1) ?? 0);
try {
  let child = node(baseline, 'k0');
  await ready(child);
  await until('origin custody and snapshots', child, async () =>
    (await objects('ts-terrabase-shapes-prod', 'v1/keys/')).length > 0 && (await objects('journey-backups', 'v1/')).some(key => key.includes('/snapshots/')));
  const scope = await project(peer, 'http://127.0.0.1:18381', 'keyring-proof');
  const projectPath = '/v1/projects/' + scope.project;
  const database = await internal(peer, 'http://127.0.0.1:18381', 'POST', projectPath + '/databases', { name: 'keyring-delete' });
  await internal(peer, 'http://127.0.0.1:18381', 'PUT', projectPath + '/backup-destination', {
    endpoint: 'https://journey.backups.terrascale.tech', region: 'auto', bucket: 'journey-backups',
    accessKeyId: access, secretKey: secret, mode: 'both',
  });
  await stop(child);
  await records('seed-rows', 'k0');
  const originalCredentials = await records('credentials', 'k0', join(profile, 'original-credentials.json'));
  assert.equal(originalCredentials.length, 2, 'both current and historical credential records opened');
  const keyFiles = new Map(files(data).map(path => [path, sha(readFileSync(path))]));
  const custody = new Map();
  for (const path of await objects('ts-terrabase-shapes-prod', 'v1/keys/')) {
    const response = await object('GET', 'ts-terrabase-shapes-prod', path);
    assert.equal(response.status, 200);
    custody.set(path, sha(Buffer.from(await response.arrayBuffer())));
  }
  console.log(`origin/main created ${keyFiles.size} key files and ${custody.size} custody objects`);
  const readsBefore = requests.length;
  child = node(candidate, 'k0');
  await ready(child);
  const skipped = await metrics();
  assert.equal(value(skipped, 'terrabase_keys_last_complete'), 0);
  assert.equal(value(skipped, 'terrabase_keys_wrapped', 'key_id="k0"'), -1);
  assert.equal(value(skipped, 'terrabase_keys_oldest_snapshot_age', 'key_id="k0"'), -1);
  const passRequests = requests.slice(readsBefore);
  const rootListings = passRequests.filter(request => {
    const query = new URL(request.path, 'https://standin').searchParams;
    return request.method === 'GET' && query.get('prefix') === 'v1/keys/' && !query.has('start-after');
  });
  const snapshotReads = passRequests.filter(request => request.method === 'GET' && request.range === 'bytes=0-2097151');
  assert.equal(rootListings.length, 1, 'only the independent shred sweep lists the custody root');
  assert.equal(snapshotReads.length, 0, 'single-key inventory made no snapshot reads');
  await internal(peer, 'http://127.0.0.1:18381', 'PUT', '/v1/management/organizations/' + scope.organization + '/plan', { plan: 'pro' });
  const created = await internal(peer, 'http://127.0.0.1:18381', 'POST', projectPath + '/databases', { name: 'created-by-upgrade' });
  const createdKey = join(data, scope.organization, scope.project, created.id + '.db.key');
  const createdBytes = readFileSync(createdKey);
  assert.equal(createdBytes.toString('ascii').split('\n')[0], 'TERRABASE-DB-KEY-1');
  assert.equal(Buffer.from(createdBytes.toString('ascii').split('\n')[1], 'base64').length, 60);
  const sqlPath = projectPath + '/databases/' + created.id + '/sql/execute';
  await internal(peer, 'http://127.0.0.1:18381', 'POST', sqlPath, { statements: [{ sql: 'CREATE TABLE compatibility(value INTEGER)' }, { sql: 'INSERT INTO compatibility VALUES(42)' }, { sql: 'CREATE TABLE keyring_startup(value INTEGER)' }, { sql: 'INSERT INTO keyring_startup VALUES(42)' }] }, false);
  await until('new custody wrapper', child, async () =>
    (await objects('ts-terrabase-shapes-prod', 'v1/keys/')).some(path => path.includes('/' + created.id + '/')));
  const createdCustody = (await objects('ts-terrabase-shapes-prod', 'v1/keys/')).filter(path => path.includes('/' + created.id + '/'));
  const createdObjects = new Map();
  for (const path of createdCustody) {
    const response = await object('GET', 'ts-terrabase-shapes-prod', path);
    const bytes = Buffer.from(await response.arrayBuffer());
    assert.equal(bytes.length, 80);
    assert.equal(Buffer.from(bytes.toString('ascii'), 'base64').length, 60);
    createdObjects.set(path, sha(bytes));
  }
  for (const [path, hash] of keyFiles) assert.equal(sha(readFileSync(path)), hash, 'key file byte identity');
  await stop(child);
  for (const [path, hash] of custody) {
    const response = await object('GET', 'ts-terrabase-shapes-prod', path);
    assert.equal(response.status, 200);
    assert.equal(sha(Buffer.from(await response.arrayBuffer())), hash, 'custody byte identity');
  }
  console.log('one key: maintenance skipped; extra custody root listings=0 (shred sweep=1), snapshot range reads=0; wrapped=-1 last_complete=0 snapshot_age=-1; existing wrappers byte-identical');
  child = node(baseline, 'k0');
  await ready(child);
  const opened = await internal(peer, 'http://127.0.0.1:18381', 'POST', sqlPath, { statements: [{ sql: 'SELECT value FROM compatibility' }] }, false);
  assert.deepEqual(opened.results[0].rows, [[42]]);
  await stop(child);
  assert.equal(readFileSync(createdKey).equals(createdBytes), true, "new single-key sidecar byte identity");
  for (const [path, hash] of createdObjects) {
    const response = await object('GET', 'ts-terrabase-shapes-prod', path);
    assert.equal(sha(Buffer.from(await response.arrayBuffer())), hash);
  }
  const credentialFile = join(profile, 'new-credentials.json');
  await records('new-credentials', 'k0', credentialFile);
  const newCredential = JSON.parse(readFileSync(credentialFile, 'utf8'));
  const previousInput = join(profile, 'previous.json');
  const previousWrappers = [];
  for (const path of createdCustody) {
    const response = await object('GET', 'ts-terrabase-shapes-prod', path);
    previousWrappers.push(await response.text());
  }
  writeFileSync(previousInput, JSON.stringify({ baseline, master, wrappers: previousWrappers, credential: newCredential }), { mode: 0o600 });
  const previous = startProcess('dotnet', [process.env.E2E_KEYRING_DRIVER, 'previous', previousInput], 'previous-wrapper-reader', clean);
  const [previousCode] = await once(previous, 'exit');
  children.delete(previous);
  assert.equal(previousCode, 0, 'origin/main opened new custody wrappers and credential plaintext');
  console.log('one key creation: origin/main opened new database sidecar and custody wrappers unchanged; SQL row=42');
  console.log('rollback: origin/main opened the same directory after the single-key upgrade');
  const leftover = join(data, '_system.db.key.rewrapping');
  writeFileSync(leftover, readFileSync(join(data, '_system.db.key')), { mode: 0o600 });
  child = node(candidate, 'k1');
  await ready(child);
  await until('rotation and snapshot inventory', child, async () => {
    const current = await metrics();
    return value(current, 'terrabase_keys_last_complete') > 0 && value(current, 'terrabase_keys_wrapped', 'key_id="k0"') === 0;
  });
  const current = await metrics();
  assert.equal(value(current, 'terrabase_keys_wrapped', 'status="unreadable"'), 0);
  assert.ok(value(current, 'terrabase_keys_oldest_snapshot_age', 'key_id="k0"') > 0, 'embedded k0 snapshot still needs k0');
  assert.equal(existsSync(leftover), false);
  for (const path of files(data)) {
    const wrapped = readFileSync(path, 'ascii').trim().split('\n')[1];
    assert.ok(wrapped.startsWith('v2.k1.'));
    const body = Buffer.from(wrapped.split('.')[2], 'base64');
    const cipher = createDecipheriv('aes-256-gcm', Buffer.from(next, 'base64'), body.subarray(0, 12));
    cipher.setAAD(Buffer.from('v2.k1'));
    cipher.setAuthTag(body.subarray(-16));
    assert.equal(Buffer.concat([cipher.update(body.subarray(12, -16)), cipher.final()]).length, 32);
  }
  for (const path of custody.keys()) {
    const response = await object('GET', 'ts-terrabase-shapes-prod', path);
    const wrapped = await response.text();
    assert.ok(wrapped.startsWith('v2.k1.'));
    const body = Buffer.from(wrapped.split('.')[2], 'base64');
    const cipher = createDecipheriv('aes-256-gcm', Buffer.from(next, 'base64'), body.subarray(0, 12));
    cipher.setAAD(Buffer.from('v2.k1'));
    cipher.setAuthTag(body.subarray(-16));
    assert.equal(Buffer.concat([cipher.update(body.subarray(12, -16)), cipher.final()]).length, 32);
  }

  const databaseKey = join(data, scope.organization, scope.project, database.id + '.db.key');
  assert.equal(existsSync(databaseKey), true);
  writeFileSync(databaseKey + '.rewrapping', readFileSync(databaseKey), { mode: 0o600 });
  const promoteKey = databaseKey.replace('.db.key', '.promote.db.key');
  writeFileSync(promoteKey, readFileSync(databaseKey), { mode: 0o600 });
  const unrelatedPromote = createdKey.replace('.db.key', '.promote.db.key');
  writeFileSync(unrelatedPromote, readFileSync(createdKey), { mode: 0o600 });
  await internal(peer, 'http://127.0.0.1:18381', 'DELETE', projectPath + '/databases/' + database.id);
  assert.equal(existsSync(databaseKey), false);
  assert.equal(existsSync(databaseKey + '.rewrapping'), false);
  assert.equal(existsSync(promoteKey), false);
  assert.equal(existsSync(unrelatedPromote), true, 'deletion retained another database detached key');
  rmSync(unrelatedPromote);
  console.log('two keys: local files, custody and current/historical credentials rewrapped and authenticated; retained k0 snapshot age>0');
  console.log('leftovers: next multi-key pass removed .rewrapping; database deletion removed only its own .rewrapping and .promote.db.key');
  await stop(child);
  await records('rows', 'k1');
  const rotatedCredentials = await records('credentials', 'k1', join(profile, 'rotated-credentials.json'));
  assert.equal(rotatedCredentials.length, originalCredentials.length);
  for (const original of originalCredentials) {
    const rotated = rotatedCredentials.find(record => record.Id === original.Id);
    assert.deepEqual(rotated, { ...original, KeyId: 'k1' }, 'reopened credential plaintext and ciphertext match for ' + original.Id);
  }
  const restored = join(profile, 'restored-without-cache');
  cpSync(data, restored, { recursive: true });
  rmSync(join(restored, '.master-key-history'));
  const refusal = node(candidate, 'k0', { TERRABASE_DATA_DIRECTORY: restored });
  const [refusalCode] = await once(refusal, 'exit');
  children.delete(refusal);
  assert.equal(refusalCode, 2, 'restored node refuses a fleet key missing from configuration');
  assert.ok(readFileSync(join(profile, 'node-' + sequence + '.log'), 'utf8').includes("configured master key id 'k1' cannot be removed"));
  assert.equal(existsSync(join(restored, '.master-key-history')), false, 'refusal does not invent local history');
  console.log('re-provisioned empty disk plus restored data without cache: shared history refused and named missing k1');
  const historyResponse = await object('GET', 'journey-backups', 'v1/master-key-history');
  assert.equal(historyResponse.status, 200);
  const historyDocument = await historyResponse.text();
  for (const [id, material] of [['k0', master], ['k1', next]]) {
    const fingerprint = createHmac('sha256', Buffer.from(material, 'base64')).update('TerraBase/master-key-history/v1').digest('hex').toUpperCase();
    assert.ok(historyDocument.includes(id + ' ' + fingerprint + '\n'), 'shared history binds ' + id + ' to its material');
  }
  const historyWrites = requests.filter(request => request.method === 'PUT' && request.path.endsWith('/v1/master-key-history'));
  assert.ok(historyWrites.some(request => request.absent === '*'), 'shared history creation is conditional');
  assert.ok(historyWrites.some(request => typeof request.match === 'string'), 'shared history extension is conditional');
  assert.ok(historyWrites.every(request => request.absent === '*' || typeof request.match === 'string'), 'no unconditional product history writes');
  const malformed = await object('PUT', 'journey-backups', 'v1/master-key-history', '', Buffer.from('unreadable'), { 'if-match': historyResponse.headers.get('etag') });
  assert.equal(malformed.status, 200);
  const badHistory = node(candidate, 'k1', { TERRABASE_DATA_DIRECTORY: restored });
  const [badHistoryCode] = await once(badHistory, 'exit');
  children.delete(badHistory);
  assert.equal(badHistoryCode, 2, 'unreadable shared history refuses even a complete configured ring');
  assert.ok(readFileSync(join(profile, 'node-' + sequence + '.log'), 'utf8').includes('master key history cannot be read'));
  const repaired = await object('PUT', 'journey-backups', 'v1/master-key-history', '', Buffer.from(historyDocument), { 'if-match': malformed.headers.get('etag') });
  assert.equal(repaired.status, 200);
  const conflict = await object('PUT', 'journey-backups', 'v1/master-key-history', '', Buffer.from('shorter'), { 'if-match': malformed.headers.get('etag') });
  assert.equal(conflict.status, 412, 'stale conditional writes cannot shorten shared history');
  console.log('startup read row=42 from every database; reopened both current and historical credentials, compared plaintext and unchanged ciphertext');
  const firstLog = await (await fetch('http://127.0.0.1:8028/log')).json();
  assert.deepEqual(firstLog.unexpected, [], 'first scenario had no unexpected object-store requests');
  await stop(standin);
  standin = startProcess('node', [join(root, 'e2e/env/r2.mjs')], 'object-store-promotion', { ...clean, E2E_REGIONS: '2' });
  mkdirSync(join(profile, 'journey'), { recursive: true });
  writeFileSync(join(profile, 'journey', 'r2'), readFileSync(join(profile, 'r2')), { mode: 0o600 });
  const config = join(profile, 'playwright.config.ts');
  writeFileSync(config, 'import { defineConfig } from ' + JSON.stringify(join(root, 'e2e/node_modules/@playwright/test'))
    + '; export default defineConfig(' + JSON.stringify({ testDir: join(root, 'e2e'), testMatch: ['interactions/keyring.spec.ts', 'keyring/promotion.spec.ts'],
      timeout: 90000, expect: { timeout: 20000 }, retries: 0, workers: 1, reporter: 'list', outputDir: join(profile, 'journey-results') }) + ');');
  const journey = spawn(join(root, 'e2e/node_modules/.bin/playwright'), ['test', '--config', config], {
    env: { ...clean, E2E_STATE: join(profile, 'journey'), E2E_KEYRING_BASELINE: baseline, E2E_KEYRING_DRIVER: process.env.E2E_KEYRING_DRIVER }, stdio: 'inherit',
  });
  children.add(journey);
  const [code] = await once(journey, 'exit');
  children.delete(journey);
  assert.equal(code, 0, 'rotation journey');
  const log = await (await fetch('http://127.0.0.1:8028/log')).json();
  assert.deepEqual(log.unexpected, [], 'object-store stand-in rejected no unexpected calls');
} finally {
  for (const child of children) await stop(child);
  await new Promise(resolve => proxy.close(resolve));
}
