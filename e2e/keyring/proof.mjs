import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createDecipheriv, createHash, createHmac, randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { closeSync, existsSync, mkdirSync, openSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
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
  requests.push({ method: request.method, path: request.url, range: request.headers.range });
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
async function object(method, bucket, path, query = '') {
  const host = bucket === 'journey-backups' ? 'journey.backups.terrascale.tech' : '6cd3f3639e928077593d9b470765efeb.r2.cloudflarestorage.com';
  const target = `/${bucket}/${path}`;
  const date = new Date().toISOString().replace(/[-:]|\.\d{3}/g, '');
  const scope = `${date.slice(0, 8)}/auto/s3/aws4_request`;
  const payload = sha('');
  const headers = 'host;x-amz-content-sha256;x-amz-date';
  const canonical = [method, target, query, `host:${host}\nx-amz-content-sha256:${payload}\nx-amz-date:${date}\n`, headers, payload].join('\n');
  const key = scope.split('/').reduce((key, part) => hmac(key, part), `AWS4${secret}`);
  const signature = hmac(key, ['AWS4-HMAC-SHA256', date, scope, sha(canonical)].join('\n')).toString('hex');
  return fetch(`https://${host}${target}${query ? `?${query}` : ''}`, { method, headers: {
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
function node(binary, active, extra = {}) {
  const ring = join(profile, 'ring-' + sequence + '.json');
  writeFileSync(ring, JSON.stringify({ active, keys: active === 'k0' ? { k0: master } : { k0: master, k1: next } }), { mode: 0o600 });
  return startProcess('dotnet', [binary], `node-${++sequence}`, {
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
  await stop(child);
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
  assert.ok(value(current, 'terrabase_keys_oldest_snapshot_age', 'key_id="k0"') > 0, 'embedded k0 snapshot still blocks retirement');
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
  assert.ok(value(current, 'terrabase_keys_wrapped', 'key_id="k1"') >= keyFiles.size + custody.size + 2,
    'current and historical credentials are included and verified');
  const databaseKey = join(data, scope.organization, scope.project, database.id + '.db.key');
  assert.equal(existsSync(databaseKey), true);
  writeFileSync(databaseKey + '.rewrapping', readFileSync(databaseKey), { mode: 0o600 });
  const promoteKey = databaseKey.replace('.db.key', '.promote.db.key');
  writeFileSync(promoteKey, readFileSync(databaseKey), { mode: 0o600 });
  await internal(peer, 'http://127.0.0.1:18381', 'DELETE', projectPath + '/databases/' + database.id);
  assert.equal(existsSync(databaseKey), false);
  assert.equal(existsSync(databaseKey + '.rewrapping'), false);
  assert.equal(existsSync(promoteKey), false);
  console.log('two keys: local files, custody and current/historical credentials rewrapped and authenticated; retained k0 snapshot age>0');
  console.log('leftovers: next multi-key pass removed .rewrapping; database deletion removed .rewrapping and .promote.db.key');
  await stop(child);
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
    env: { ...clean, E2E_STATE: join(profile, 'journey'), E2E_KEYRING_BASELINE: baseline }, stdio: 'inherit',
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
