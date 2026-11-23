import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { closeSync, existsSync, openSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { setInterval } from 'node:timers/promises';
import { holdSync } from '../env/ports.mjs';
import { internal, project } from './operations.mjs';

const profile = process.env.E2E_STATE;
const service = join(process.env.E2E_BUILD, 'terrabase');
const driver = process.env.E2E_KEYRING_DRIVER;
const ports = holdSync({ client: 18380, internal: 18381, postgres: 18382, metrics: 18383, quic: 18384 }, 'block').ports;
const address = 'http://127.0.0.1:' + ports.internal;
const data = join(profile, 'data');
const peer = randomBytes(32).toString('hex');
const k0 = randomBytes(32).toString('base64');
const k1 = randomBytes(32).toString('base64');
const forged = randomBytes(32).toString('base64');
writeFileSync(join(profile, 'secrets'), `${peer}\n${k0}\n${k1}\n${forged}\n`, { mode: 0o600 });
const rings = {
  single: { active: 'k0', keys: { k0 } },
  rotated: { active: 'k1', keys: { k0, k1 } },
  retired: { active: 'k1', keys: { k1 } },
  forged: { active: 'k1', keys: { k0: forged, k1 } },
};
const clean = Object.fromEntries(Object.entries(process.env).filter(([name]) =>
  !name.startsWith('TERRABASE_') && !name.startsWith('TS_') && !name.startsWith('AWS_') && name !== 'BUCKET_NAME'));
const children = new Set();
let sequence = 0;

function environment(ring) {
  const file = join(profile, 'ring-' + ring + '.json');
  writeFileSync(file, JSON.stringify(rings[ring]), { mode: 0o600 });
  return {
    ...clean, DOTNET_PROCESSOR_COUNT: '4', DOTNET_ENVIRONMENT: 'Development', TERRABASE_MASTER_KEYS_FILE: file,
    TERRABASE_PEER_SECRET: peer, TERRABASE_DATA_DIRECTORY: data, TERRABASE_MEMORY_BUDGET: '67108864', TERRABASE_STORAGE_THREADS: '1',
    TERRABASE_PORT: String(ports.client), TERRABASE_INTERNAL_PORT: String(ports.internal), TERRABASE_POSTGRES_PORT: String(ports.postgres),
    TERRABASE_METRICS_PORT: String(ports.metrics), TERRABASE_QUIC_PORT: String(ports.quic),
  };
}

function start(name, args, ring) {
  const log = join(profile, `${++sequence}-${name}.log`);
  const output = openSync(log, 'w', 0o600);
  const child = spawn('dotnet', args, { env: environment(ring), stdio: ['ignore', output, output] });
  closeSync(output);
  children.add(child);
  return { child, log };
}

async function stop(child) {
  if (child.exitCode === null && child.signalCode === null) {
    const exited = once(child, 'exit');
    child.kill('SIGTERM');
    const [code, signal] = await exited;
    assert.equal(code, 0, 'node stopped cleanly after SIGTERM, not ' + (signal ?? code));
  }
  children.delete(child);
}

async function until(name, condition) {
  const deadline = Date.now() + 30_000;
  for await (const tick of setInterval(200)) {
    void tick;
    if (await condition()) return;
    assert.ok(Date.now() < deadline, `${name}: condition did not hold`);
  }
}

async function readyz() {
  try { return (await fetch('http://127.0.0.1:' + ports.client + '/readyz')).status; } catch { return 0; }
}

async function serve(ring) {
  const node = start('node-' + ring, [join(service, 'terrabase.dll')], ring);
  await until('readiness under ' + ring, async () => {
    assert.equal(node.child.exitCode, null, `node under ${ring} exited: ${readFileSync(node.log, 'utf8').slice(-2000)}`);
    return await readyz() === 200;
  });
  return node.child;
}

const files = directory => readdirSync(directory, { withFileTypes: true }).flatMap(entry =>
  entry.isDirectory() ? files(join(directory, entry.name)) : [join(directory, entry.name)]);
const fingerprint = () => new Map(files(data).map(path => [relative(data, path), createHash('sha256').update(readFileSync(path)).digest('hex')]));
const holds = marker => files(data).filter(path => readFileSync(path).includes(Buffer.from(marker)));

async function drive(ring, args) {
  const before = fingerprint();
  const run = start('driver-' + args[0], ['exec', '--depsfile', join(service, 'terrabase.deps.json'),
    '--runtimeconfig', join(service, 'terrabase.runtimeconfig.json'), driver, ...args], ring);
  const [code, signal] = await once(run.child, 'exit');
  children.delete(run.child);
  const output = readFileSync(run.log, 'utf8');
  assert.equal(code, 0, `keyring driver ${args[0]} ended with ${signal ?? code}: ${output}`);
  assert.deepEqual(fingerprint(), before, `keyring driver ${args[0]} left every data byte unchanged`);
  const line = output.split('\n').find(text => text.startsWith('keyring-driver:'));
  assert.ok(line, `keyring driver ${args[0]} reported its proof`);
  console.log(line);
  return line;
}

async function inventory(ring) {
  const output = join(profile, `inventory-${sequence + 1}.json`);
  await drive(ring, ['inventory', output]);
  const read = JSON.parse(readFileSync(output, 'utf8'));
  assert.equal(read.active, rings[ring].active);
  return { system: read.systemKeyId, keys: new Map(read.databases.map(entry => [entry.id, entry.keyId])) };
}

async function evidence(ring, database, keyId, marker) {
  const request = join(profile, `evidence-${sequence + 1}.json`);
  writeFileSync(request, JSON.stringify({ database, keyId, marker }), { mode: 0o600 });
  const line = await drive(ring, ['evidence', request]);
  assert.ok(line.includes(`database=${database} key=${keyId} `) && line.endsWith(' plaintext=absent header=encrypted other-keys=refused row=42'));
}

async function refused(ring, reason) {
  const before = fingerprint();
  const node = start('refused-' + ring, [join(service, 'terrabase.dll')], ring);
  await until('refusal under ' + ring, async () => {
    assert.notEqual(await readyz(), 200, `node served under the ${ring} keyring`);
    return node.child.exitCode !== null || node.child.signalCode !== null;
  });
  children.delete(node.child);
  assert.notEqual(node.child.exitCode, 0, `node under the ${ring} keyring refused to start`);
  const output = readFileSync(node.log, 'utf8');
  assert.ok(output.includes('could not open its system catalog') && output.includes(reason), `node under the ${ring} keyring named its refusal: ${output.slice(-2000)}`);
  assert.deepEqual(fingerprint(), before, `refused start under the ${ring} keyring left every data byte unchanged`);
  return node.child.signalCode ?? node.child.exitCode;
}

try {
  const markers = Object.fromEntries(['before', 'after', 'shredded'].map(name => [name, `keyring-${name}-${randomBytes(16).toString('hex')}`]));
  let child = await serve('single');
  const scope = await project(peer, address, 'keyring-proof');
  const projectPath = '/v1/projects/' + scope.project;
  const sql = (database, statements) => internal(peer, address, 'POST', `${projectPath}/databases/${database}/sql/execute`,
    { statements: statements.map(text => ({ sql: text })) }, false);
  const rows = async database => (await sql(database, ['SELECT value, marker FROM keyring_rows'])).results[0].rows;
  const seeded = async (name, marker) => {
    const created = await internal(peer, address, 'POST', projectPath + '/databases', { name });
    await sql(created.id, ['CREATE TABLE keyring_rows(id INTEGER PRIMARY KEY, value INTEGER NOT NULL, marker TEXT NOT NULL)',
      `INSERT INTO keyring_rows(id, value, marker) VALUES(1, 42, '${marker}')`]);
    assert.deepEqual(await rows(created.id), [[42, marker]]);
    return created.id;
  };
  const before = await seeded('before-rotation', markers.before);
  await stop(child);
  const single = await inventory('single');
  assert.equal(single.system, 'k0');
  assert.equal(single.keys.get(before), 'k0');
  assert.deepEqual([...new Set(single.keys.values())], ['k0'], 'one configured key holds every database');
  await evidence('single', before, 'k0', markers.before);
  console.log('one key: every database file opens under k0 alone; row 42 is ciphertext at rest');

  child = await serve('rotated');
  assert.deepEqual(await rows(before), [[42, markers.before]], 'a k0 database serves after k1 becomes active');
  const after = await seeded('after-rotation', markers.after);
  const branch = await internal(peer, address, 'POST', `${projectPath}/databases/${before}/branches`, { name: 'branch-rotation' });
  assert.equal(branch.branchedFrom, before);
  assert.deepEqual(await rows(branch.id), [[42, markers.before]], 'a branch of a k0 database reads its rows');
  const restore = await internal(peer, address, 'POST', `${projectPath}/databases/${before}/point-in-time/restore`,
    { name: 'restored-rotation', target: { pointInTime: new Date().toISOString() } });
  assert.equal(restore.sourceDatabaseId, before);
  let operation = restore;
  await until('restore under k1', async () => {
    operation = await internal(peer, address, 'GET', `${projectPath}/databases/${before}/restores/${restore.operationId}`);
    assert.notEqual(operation.state, 'failed', 'restore failed: ' + operation.failureMessage);
    return operation.state === 'succeeded';
  });
  const restored = operation.targetDatabaseId;
  assert.deepEqual(await rows(restored), [[42, markers.before]], 'a restore of a k0 database reads its rows');
  const shredded = await seeded('shredded', markers.shredded);
  await internal(peer, address, 'DELETE', `${projectPath}/databases/${shredded}`);
  await stop(child);
  const rotated = await inventory('rotated');
  assert.equal(rotated.system, 'k0', 'rotation leaves the system database under its creation key');
  assert.equal(rotated.keys.get(before), 'k0', 'rotation leaves existing databases under their creation key');
  for (const database of [after, branch.id, restored]) assert.equal(rotated.keys.get(database), 'k1', `new database ${database} is under the active key`);
  assert.equal(rotated.keys.has(shredded), false, 'a deleted database leaves the inventory');
  assert.equal(existsSync(join(data, 'databases', shredded)), false, 'a deleted database leaves no local files');
  assert.deepEqual(holds(markers.shredded), [], 'no file holds a deleted database row');
  await evidence('rotated', after, 'k1', markers.after);
  await evidence('rotated', branch.id, 'k1', markers.before);
  await evidence('rotated', restored, 'k1', markers.before);
  console.log('two keys: k0 files keep k0, branch and restore re-encrypt under k1, deletion erased the files; no plaintext at rest');

  const retired = await refused('retired', "the database's master key is not configured on this node");
  const changed = await refused('forged', 'the system database could not be opened');
  console.log(`key removal and key material change: node refused to start (${retired}, ${changed}) and left every data byte unchanged`);

  child = await serve('rotated');
  for (const database of [before, branch.id, restored]) assert.deepEqual(await rows(database), [[42, markers.before]]);
  assert.deepEqual(await rows(after), [[42, markers.after]]);
  await stop(child);
  const recovered = await inventory('rotated');
  assert.deepEqual(recovered, rotated, 'restoring the keyring serves the same inventory');
  console.log('restored keyring: every database reads row 42 under its recorded key');
} finally {
  for (const running of children) {
    if (running.exitCode === null && running.signalCode === null) {
      const exited = once(running, 'exit');
      running.kill('SIGKILL');
      await exited;
    }
  }
}
