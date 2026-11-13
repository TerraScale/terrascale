import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, createHmac, randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { closeSync, mkdirSync, openSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { request as httpRequest } from 'node:http';
import { createServer } from 'node:https';
import { join, resolve } from 'node:path';
import { setInterval } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

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
const standin = startProcess('node', [join(root, 'e2e/env/r2.mjs')], 'object-store', { ...clean, E2E_REGIONS: '2' });
const proxy = createServer({ key: readFileSync(join(profile, 'tls.key')), cert: readFileSync(join(profile, 'tls.pem')) }, (request, response) => {
  const upstream = httpRequest({ hostname: '127.0.0.1', port: 8028, method: request.method, path: request.url, headers: request.headers }, answer => {
    response.writeHead(answer.statusCode, answer.headers);
    answer.pipe(response);
  });
  upstream.on('error', () => { response.writeHead(502); response.end(); });
  request.pipe(upstream);
});
proxy.listen(443, '127.0.0.1');
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
function node(binary, active) {
  const ring = join(profile, 'ring.json');
  writeFileSync(ring, JSON.stringify({ active, keys: active === 'k0' ? { k0: master } : { k0: master, k1: next } }), { mode: 0o600 });
  return startProcess('dotnet', [binary], `node-${++sequence}`, {
    ...clean, DOTNET_ROOT: process.env.DOTNET_ROOT, DOTNET_PROCESSOR_COUNT: '4', DOTNET_ENVIRONMENT: 'Development',
    ...(binary === baseline ? { TERRABASE_MASTER_KEY: master } : { TERRABASE_MASTER_KEYS_FILE: ring }),
    TERRABASE_PEER_SECRET: peer, TERRABASE_DATA_DIRECTORY: data, TERRABASE_MEMORY_BUDGET: '67108864', TERRABASE_STORAGE_THREADS: '1',
    TERRABASE_PORT: '18380', TERRABASE_INTERNAL_PORT: '18381', TERRABASE_POSTGRES_PORT: '18382', TERRABASE_METRICS_PORT: '18383',
    AWS_ENDPOINT_URL_S3: 'https://journey.backups.terrascale.tech', AWS_REGION: 'auto', BUCKET_NAME: 'journey-backups',
    AWS_ACCESS_KEY_ID: access, AWS_SECRET_ACCESS_KEY: secret,
    TERRABASE_SHAPES_ACCESS_KEY_ID: access, TERRABASE_SHAPES_SECRET_ACCESS_KEY: secret, TERRABASE_SHAPES_CDN_API_TOKEN: peer,
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
  await stop(child);
  const keyFiles = new Map(files(data).map(path => [path, sha(readFileSync(path))]));
  const custody = new Map();
  for (const path of await objects('ts-terrabase-shapes-prod', 'v1/keys/')) {
    const response = await object('GET', 'ts-terrabase-shapes-prod', path);
    assert.equal(response.status, 200);
    custody.set(path, sha(Buffer.from(await response.arrayBuffer())));
  }
  console.log(`origin/main created ${keyFiles.size} key files and ${custody.size} custody objects`);
  child = node(candidate, 'k0');
  await ready(child);
  await until('inventory completed', child, async () => value(await metrics(), 'terrabase_keys_last_complete') > 0);
  const started = Date.now();
  for await (const tick of setInterval(1000)) {
    void tick;
    assert.equal(child.exitCode, null);
    const current = await metrics();
    assert.ok(value(current, 'terrabase_keys_last_complete') > 0);
    assert.equal(value(current, 'terrabase_keys_wrapped', 'status="unreadable"'), 0);
    for (const [path, hash] of keyFiles) assert.equal(sha(readFileSync(path)), hash, 'key file byte identity');
    for (const [path, hash] of custody) {
      const response = await object('GET', 'ts-terrabase-shapes-prod', path);
      assert.equal(response.status, 200);
      assert.equal(sha(Buffer.from(await response.arrayBuffer())), hash, 'custody byte identity');
    }
    if (Date.now() - started >= 300_000) break;
  }
  await stop(child);
  console.log(`upgrade: 300 seconds, ${keyFiles.size} key files and ${custody.size} custody objects byte-identical; unreadable=0`);
  child = node(baseline, 'k0');
  await ready(child);
  await stop(child);
  console.log('rollback: origin/main opened the same directory after the five-minute upgrade');
  child = node(candidate, 'k1');
  await ready(child);
  await until('rotation and snapshot inventory', child, async () => {
    const current = await metrics();
    return value(current, 'terrabase_keys_last_complete') > 0 && value(current, 'terrabase_keys_wrapped', 'key_id="k0"') === 0;
  });
  const current = await metrics();
  assert.equal(value(current, 'terrabase_keys_wrapped', 'status="unreadable"'), 0);
  assert.ok(value(current, 'terrabase_keys_oldest_snapshot_age', 'key_id="k0"') > 0, 'embedded k0 snapshot still blocks retirement');
  console.log('rotation with custody: k0 wrappers=0, unreadable=0, oldest retained k0 snapshot age>0 (retirement blocked)');
  await stop(child);
  mkdirSync(join(profile, 'journey'), { recursive: true });
  const journey = spawn(join(root, 'e2e/node_modules/.bin/playwright'), ['test', '--config', join(root, 'artifacts/C33-playwright.config.ts')], {
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
