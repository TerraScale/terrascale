import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { Nats, putKey as putSealedKey, seal } from './push.mjs';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { appendFileSync, openSync, closeSync, readFileSync, mkdirSync, writeFileSync, watch, rmSync } from 'node:fs';
import { connect as http2 } from 'node:http2';
import { createConnection, createServer } from 'node:net';
import { setPriority } from 'node:os';
import { resolve } from 'node:path';

const journeyState = process.env.E2E_STATE;
assert(journeyState, 'E2E_STATE names the isolated local journey namespace');
assert(process.env.E2E_BUILD, 'E2E_BUILD holds the TerraBase build');
assert(process.env.E2E_TOOLS, 'E2E_TOOLS holds nats-server');
assert(process.env.DOTNET_ROOT, 'DOTNET_ROOT holds the .NET runtime');
assert.equal(process.env.E2E_REGIONS, '2');
const state = resolve(journeyState, 'replication-' + randomBytes(6).toString('hex'));
mkdirSync(state + '/logs', { recursive: true, mode: 0o700 });
console.log('local state: ' + state);
const peer = randomBytes(32).toString('hex');
const master = randomBytes(32);
const access = randomBytes(16).toString('hex');
const secret = randomBytes(32).toString('hex');
const token = randomBytes(24).toString('hex');
appendFileSync(journeyState + '/secrets', [peer, master.toString('base64'), access, secret, token].join('\n') + '\n', { mode: 0o600 });
const processes = new Set();
const alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
function id(at = Date.now()) {
  let value = BigInt(at) << 80n | BigInt('0x' + randomBytes(10).toString('hex'));
  let encoded = '';
  for (let i = 0; i < 26; i++) {
    encoded = alphabet[Number(value & 31n)] + encoded;
    value >>= 5n;
  }
  return encoded;
}
function start(name, command, args, extra = {}) {
  const log = openSync(resolve(state, 'logs', name + '.log'), 'a', 0o600);
  const child = spawn(command, args, { env: { ...process.env, DOTNET_PROCESSOR_COUNT: '4', ...extra }, stdio: ['ignore', log, log] });
  closeSync(log);
  setPriority(child.pid, 19);
  child.log = resolve(state, 'logs', name + '.log');
  child.finished = once(child, 'exit');
  processes.add(child);
  return child;
}
async function stop(child, signal = 'SIGINT') {
  child.kill(signal);
  await child.finished;
  processes.delete(child);
}
async function request(address, path, body, user, method = 'POST', expected = 200) {
  const session = http2(address);
  const payload = body === undefined ? undefined : JSON.stringify(body);
  try {
    const response = session.request({
      ':method': method, ':path': path, 'content-type': 'application/json',
      'x-terrabase-peer-credential': peer, ...(payload ? { 'content-length': Buffer.byteLength(payload) } : {}), ...(user ? { 'x-terrabase-user-id': user } : {}),
    });
    const chunks = [];
    response.on('data', data => chunks.push(data));
    const headers = once(response, 'response');
    response.end(payload);
    await once(response, 'end');
    const status = (await headers)[0][':status'];
    const text = Buffer.concat(chunks).toString();
    assert((Array.isArray(expected) ? expected : [expected]).includes(status), method + ' ' + path + ': status=' + status + ' ' + text);
    return text ? JSON.parse(text) : null;
  } finally {
    session.close();
  }
}
function logged(child, text, fields = {}, after = 0) {
  return new Promise((resolveEvent, reject) => {
    const deadline = setTimeout(() => finish(new Error('Missing log event: ' + text)), 90_000);
    const watcher = watch(child.log, check);
    function finish(error) {
      clearTimeout(deadline);
      watcher.close();
      error ? reject(error) : resolveEvent();
    }
    function check() {
      const matched = readFileSync(child.log, 'utf8').slice(after).split('\n').some(line => {
        if (!line.includes(text)) return false;
        if (!Object.keys(fields).length) return true;
        try {
          const record = JSON.parse(line.replace(/^<\d+>/, ''));
          return Object.entries(fields).every(([key, value]) => record[key] === value);
        } catch { return false; }
      });
      if (matched) finish();
    }
    check();
  });
}
class Relay {
  links = new Set();
  trap = null;
  constructor() {
    this.server = createServer(client => {
      const upstream = createConnection({ port: 4222, host: '127.0.0.1' });
      const link = { client, upstream, held: Buffer.alloc(0) };
      this.links.add(link);
      const close = () => {
        this.links.delete(link);
        client.destroy();
        upstream.destroy();
      };
      for (const socket of [client, upstream]) {
        socket.on('error', close);
        socket.on('close', close);
      }
      upstream.on('data', data => client.write(data));
      client.on('data', data => {
        const buffered = Buffer.concat([link.held, data]);
        const text = buffered.toString('latin1');
        const at = this.trap ? text.indexOf(this.trap.subject) : -1;
        if (at < 0) {
          const cut = text.lastIndexOf('\r\n');
          const end = cut < 0 ? 0 : cut + 2;
          upstream.write(buffered.subarray(0, end));
          link.held = buffered.subarray(end);
          return;
        }
        const line = text.lastIndexOf('\r\n', at);
        const trap = this.trap;
        this.trap = null;
        upstream.end(buffered.subarray(0, line < 0 ? 0 : line + 2), close);
        trap.resolve();
      });
    });
  }
  async listen() {
    this.server.listen(4223, '127.0.0.1');
    await once(this.server, 'listening');
  }
  drop(subject) {
    return new Promise(resolveDrop => { this.trap = { subject, resolve: resolveDrop }; });
  }
  close() {
    for (const link of this.links) {
      link.client.destroy();
      link.upstream.destroy();
    }
    this.server.close();
  }
}
const home = 'http://127.0.0.1:8081';
function cell(region, second = false) {
  const gru = region === 'gru';
  return start('frontier-' + region + (second ? '-b' : ''), resolve(process.env.DOTNET_ROOT, 'dotnet'), [resolve(process.env.E2E_BUILD, 'terrabase/terrabase.dll')], {
    DOTNET_ENVIRONMENT: 'Production', TS_REGION: region, TS_NODE: second ? 'journey-3' : gru ? 'journey-1' : 'journey-2', TS_PEERS_FILE: state + '/peers',
    TERRABASE_DATA_DIRECTORY: state + '/frontier-' + region + (second ? '-b' : ''), TERRABASE_PORT: second ? '8280' : gru ? '8080' : '8180',
    TERRABASE_INTERNAL_PORT: '8081', TERRABASE_METRICS_PORT: second ? '9291' : gru ? '9091' : '9191', TERRABASE_POSTGRES_PORT: second ? '6632' : gru ? '6432' : '6532',
    TERRABASE_AUTH_ISSUER: 'https://auth.terrascale.tech/terrascale', TERRABASE_AUTH_AUDIENCE: 'https://api.terrascale.tech',
    TERRABASE_PEER_SECRET: peer, TERRABASE_MASTER_KEY: master.toString('base64'),
    AWS_ENDPOINT_URL_S3: 'https://journey.backups.terrascale.tech/', AWS_REGION: 'auto', BUCKET_NAME: 'journey-backups',
    AWS_ACCESS_KEY_ID: access, AWS_SECRET_ACCESS_KEY: secret,
    TERRABASE_SHAPES_ACCESS_KEY_ID: access, TERRABASE_SHAPES_SECRET_ACCESS_KEY: secret, TERRABASE_SHAPES_CDN_API_TOKEN: randomBytes(24).toString('hex'),
    TS_NATS_URL: gru && !second ? 'nats://127.0.0.1:4223' : 'nats://127.0.0.1:4222', TERRABASE_NATS_TOKEN: token,
  });
}
async function trusted(port) {
  const deadline = Date.now() + 90_000;
  while (true) {
    const text = await fetch('http://127.0.0.1:' + port + '/metrics').then(response => response.text(), () => '');
    if (text.split('\n').some(line => line.startsWith('terrabase_clock_local_writes_allowed') && line.endsWith(' 1'))) return;
    assert(Date.now() < deadline, 'the node on metrics port ' + port + ' never trusted its clock');
    await new Promise(resolveWait => setTimeout(resolveWait, 100));
  }
}
let nats;
const relay = new Relay();
try {
  writeFileSync(journeyState + '/r2', access + '\n' + secret + '\n', { mode: 0o600 });
  writeFileSync(state + '/peers', 'journey-1 gru 127.0.0.1\njourney-2 fra 127.0.0.2\njourney-3 gru 127.0.0.3\n');
  writeFileSync(state + '/nats.conf', JSON.stringify({ host: '127.0.0.1', port: 4222, authorization: { token }, jetstream: { store_dir: state + '/nats' } }), { mode: 0o600 });
  const broker = start('frontier-nats', resolve(process.env.E2E_TOOLS, 'nats-server'), ['-c', state + '/nats.conf']);
  await logged(broker, 'Server is ready');
  await relay.listen();
  nats = new Nats(token);
  await nats.connect();
  const previousLayout = await nats.request('$JS.API.STREAM.CREATE.TERRABASE_PUSH_PARK_GRU', { name: 'TERRABASE_PUSH_PARK_GRU', subjects: ['terrabase.push-park.gru.>'],
    storage: 'file', retention: 'limits', discard: 'new', max_bytes: 1 << 30, duplicate_window: 600_000_000_000, num_replicas: 1 });
  assert(!previousLayout.error, JSON.stringify(previousLayout));
  const previousPayload = randomBytes(64);
  assert(!(await nats.request('terrabase.push-park.gru.fra.' + id() + '.' + id() + '.' + id() + '.7', previousPayload)).error);
  const gruStarted = Date.now();
  const consuming = nats.event('$JS.EVENT.ADVISORY.CONSUMER.CREATED.TERRABASE_PUSH_FRA.*', created => created.consumer?.startsWith('home-gru-epoch-'));
  let gru = cell('gru');
  cell('fra');
  await consuming;
  await logged(gru, 'Application started');
  await logged(gru, 'This node holds a new home lease session');
  await trusted(9091);
  const user = id();
  const org = await request(home, '/v1/management/organizations', { name: 'Frontier' }, user, 'POST', 201);
  await request(home, '/v1/management/organizations/' + org.id + '/plan', { plan: 'pro' }, user, 'PUT');
  const project = await request(home, '/v1/management/organizations/' + org.id + '/projects', { name: 'Frontier' }, user, 'POST', 201);
  const create = name => request(home, '/v1/projects/' + project.id + '/databases', { name, regions: ['gru', 'fra'] }, user, 'POST', 201);
  const first = await create('frontier');
  const other = await create('independent');
  const sql = (db, statement, expected = 200) => request(home, '/v1/projects/' + project.id + '/databases/' + db.id + '/sql/execute', { statements: [{ sql: statement }] }, undefined, 'POST', expected);
  for (const db of [first, other]) {
    await sql(db, 'CREATE TABLE items (id INTEGER PRIMARY KEY, value INTEGER)');
    await sql(db, 'CREATE TABLE applications (tx TEXT PRIMARY KEY, value INTEGER)');
    await sql(db, 'INSERT INTO items VALUES (1, 0)');
  }
  const dataKey = randomBytes(32);
  const keyId = id();
  for (const db of [first, other]) await putSealedKey({ master, access, secret }, org.id + '/' + project.id + '/' + db.id, keyId, dataKey);
  const transaction = changes => ({ id: id(), region: 'fra', hlc: { physicalMilliseconds: Date.now(), logical: 0, nodeId: 'journey-2' }, changes });
  const effect = value => ({ op: 'update', table: 'items', key: { id: 1 }, values: { value } });
  const send = async (db, tx, encryptionId = keyId, addressed = 'gru') => {
    const scope = org.id + '/' + project.id + '/' + db.id;
    const ack = await nats.request('terrabase.push.fra.' + addressed + '.' + scope.replaceAll('/', '.'), seal(tx, scope, encryptionId, dataKey));
    assert(!ack.error, JSON.stringify(ack));
    return { tx, seq: ack.seq };
  };
  const value = async db => {
    const result = await sql(db, 'SELECT value FROM items WHERE id = 1', [200, 503]);
    if (result.error) {
      assert.equal(result.error.code, 'unavailable');
      return null;
    }
    return result.results[0].rows[0][0];
  };
  const notice = (db, tx) => nats.event('terrabase.applied.fra.' + org.id + '.' + project.id + '.' + db.id, result => result.id === tx.id);
  const applied = async (db, tx) => {
    const observed = notice(db, tx);
    const sent = await send(db, tx);
    assert.equal((await observed).state, 'confirmed');
    return sent;
  };
  const warm = await applied(first, transaction([effect(1)]));
  assert.equal(await value(first), 1);
  await sql(first, 'UPDATE items SET value = 9 WHERE id = 1');
  const replayed = notice(first, warm.tx);
  await stop(gru);
  gru = cell('gru');
  await replayed;
  assert.equal(await value(first), 9);
  console.log('restart: retained entry returns its recorded result without repeating effects');
  const expired = { ...transaction([{ op: 'insert', table: 'items', values: { id: 1, value: 5 } }]), id: id(Date.now() - 8 * 86_400_000) };
  const rejected = notice(first, expired);
  await send(first, expired);
  assert.equal((await rejected).state, 'reverted');
  let unknownOutcome = null;
  const unknownNotice = notice(first, expired).then(result => { unknownOutcome = result; }, () => {});
  await stop(gru);
  gru = cell('gru');
  await logged(gru, 'Application started');
  await trusted(9091);
  await applied(first, transaction([effect(9)]));
  assert.equal(unknownOutcome, null);
  console.log('expired reverted result: its redelivery publishes no notice and the next message applies');

  const b = cell('gru', true);
  await logged(b, 'Application started');
  await logged(b, 'This node holds a new home lease session');
  const heldKey = id(Date.now() - 60_000);
  await putSealedKey({ master, access, secret }, org.id + '/' + project.id + '/' + first.id, heldKey, dataKey);
  writeFileSync(journeyState + '/r2-unavailable-keys', JSON.stringify({ ['v1/keys/' + org.id + '/' + project.id + '/' + first.id + '/' + heldKey]: 503 }), { mode: 0o600 });
  const progress = (sent, count, consumer = () => true) => {
    let seen = 0;
    return nats.event('$JS.ACK.>', reply => {
      const tokens = reply.subject.split('.');
      const at = tokens[2] === 'TERRABASE_PUSH_FRA' ? 3 : 5;
      return ['+WPI', '+ACK'].includes(reply.raw) && consumer(tokens[at]) && tokens[at + 2] === String(sent.seq) && ++seen >= count;
    });
  };
  const rounds = (db, sent, count) => {
    let seen = 0;
    return nats.event('$JS.API.STREAM.MSG.GET.TERRABASE_PUSH_FRA', read => read.next_by_subj?.endsWith('.' + db.id) && read.seq <= sent.seq && ++seen >= count);
  };
  const held = async (db, pending, count = 2) => {
    const sent = await pending;
    await rounds(db, sent, count);
    return sent;
  };
  const late = transaction([{ op: 'insert', table: 'items', values: { id: 2, value: 17 } }]);
  const lateNotice = notice(first, late);
  const lateSent = await held(first, send(first, late, heldKey));
  const changeWriter = async node => {
    const stored = await nats.request('$JS.API.STREAM.MSG.GET.KV_terrabase-region-owners', { last_by_subj: '$KV.terrabase-region-owners.gru' });
    const owner = JSON.parse(Buffer.from(stored.message.data, 'base64'));
    const changed = { ...owner, node, epoch: owner.epoch + 1, previous: owner.node, activated: true, transfer: null,
      prepared: { source: owner.node, target: node, epoch: owner.epoch } };
    const seen = progress(lateSent, 1, consumer => consumer === 'home-gru-epoch-' + changed.epoch);
    const ack = await nats.request('$KV.terrabase-region-owners.gru', changed, { 'Nats-Expected-Last-Subject-Sequence': String(stored.message.seq) });
    assert(!ack.error, JSON.stringify(ack));
    await seen;
  };
  await changeWriter('journey-3');
  await changeWriter('journey-1');
  const successor = transaction([effect(6)]);
  const successorNotice = notice(first, successor);
  await send(first, successor);
  rmSync(journeyState + '/r2-unavailable-keys');
  assert.equal((await lateNotice).state, 'confirmed');
  assert.equal((await successorNotice).state, 'confirmed');
  assert.equal((await sql(first, 'SELECT value FROM items WHERE id = 2')).results[0].rows[0][0], 17);
  console.log('writer flips twice through broker ownership records with a retrying head: both effects are present');
  const stream = await nats.request('$JS.API.STREAM.INFO.TERRABASE_PUSH_FRA');
  assert((await nats.request('$JS.API.STREAM.DELETE.TERRABASE_PUSH_FRA')).success);
  assert(!(await nats.request('$JS.API.STREAM.CREATE.TERRABASE_PUSH_FRA', stream.config)).error);
  const recreated = await applied(first, transaction([effect(8)]));
  assert.equal(recreated.seq, 1);
  assert.equal(await value(first), 8);
  console.log('recreated origin stream: sequence 1 applies under its new identity');
  const scope = db => org.id + '/' + project.id + '/' + db.id;
  const blocked = {};
  const block = (db, key) => {
    blocked['v1/keys/' + scope(db) + '/' + key] = 403;
    writeFileSync(journeyState + '/r2-unavailable-keys', JSON.stringify(blocked), { mode: 0o600 });
  };
  const unblock = () => {
    for (const key of Object.keys(blocked)) delete blocked[key];
    rmSync(journeyState + '/r2-unavailable-keys', { force: true });
  };
  const refusedKey = async db => {
    const key = id(Date.now() - 60_000);
    await putSealedKey({ master, access, secret }, scope(db), key, dataKey);
    block(db, key);
    return key;
  };
  const scrape = async (pattern, port = 9091) => {
    const lines = (await (await fetch('http://127.0.0.1:' + port + '/metrics')).text()).split('\n').filter(line => pattern.test(line));
    return lines.reduce((sum, line) => sum + Number(line.split(' ').at(-1)), 0);
  };
  const heldGauge = () => scrape(/^terrabase_push_held[ {]/);
  const ageGauge = () => scrape(/^terrabase_push_held_age(_seconds)?[ {]/);
  const rows = async (db, statement) => (await sql(db, statement)).results[0].rows;
  const offset = () => readFileSync(gru.log, 'utf8').length;

  const insert = transaction([{ op: 'insert', table: 'items', values: { id: 30, value: 1 } }]);
  const update = transaction([{ op: 'update', table: 'items', key: { id: 30 }, values: { value: 2 } }]);
  update.hlc = { ...insert.hlc, logical: 1 };
  const insertKey = await refusedKey(first);
  const insertNotice = notice(first, insert);
  const updateNotice = notice(first, update);
  const sentInsert = await held(first, send(first, insert, insertKey));
  const attempts = rounds(first, sentInsert, 5);
  await send(first, update);
  const firstAge = await ageGauge();
  assert.equal(await heldGauge(), 1);
  for (const amount of [100, 101, 102]) await applied(other, transaction([effect(amount)]));
  await attempts;
  assert.deepEqual(await rows(first, 'SELECT count(*) FROM items WHERE id = 30'), [[0]]);
  await applied(other, transaction([effect(103)]));
  assert.equal(await value(other), 103);
  const laterAge = await ageGauge();
  assert.equal(await heldGauge(), 1);
  assert(laterAge > firstAge, laterAge + ' > ' + firstAge);
  console.log('held insert: at least 6 failed attempts, its update not applied and not reverted, the other database applied 4 effects; held=1, oldest age ' + firstAge.toFixed(1) + 's -> ' + laterAge.toFixed(1) + 's');
  unblock();
  assert.equal((await insertNotice).state, 'confirmed');
  assert.equal((await updateNotice).state, 'confirmed');
  assert.deepEqual(await rows(first, 'SELECT value FROM items WHERE id = 30'), [[2]]);
  console.log('released: insert then update applied in order, both confirmed, final value 2');

  const database = async name => {
    const db = await create(name);
    await sql(db, 'CREATE TABLE items (id INTEGER PRIMARY KEY, value INTEGER)');
    await sql(db, 'CREATE TABLE applications (tx TEXT PRIMARY KEY, value INTEGER)');
    await sql(db, 'INSERT INTO items VALUES (1, -1)');
    await putSealedKey({ master, access, secret }, scope(db), keyId, dataKey);
    return db;
  };
  const audited = tx => ({ ...tx, changes: [...tx.changes, { op: 'insert', table: 'applications', values: { tx: tx.id, value: 0 } }] });
  const applications = async (db, from = 0, on = rows) => (await on(db, 'SELECT rowid, tx FROM applications WHERE rowid > ' + from + ' ORDER BY rowid')).map(([, tx]) => tx);
  const lastApplication = async db => (await rows(db, 'SELECT coalesce(max(rowid), 0) FROM applications'))[0][0];
  const series = count => {
    const at = Date.now();
    return Array.from({ length: count }, (_, n) => audited({ ...transaction([effect(n)]), hlc: { physicalMilliseconds: at, logical: n, nodeId: 'journey-2' } }));
  };
  const collect = (db, txs) => {
    const order = [];
    const states = new Map();
    const notices = new Map();
    const ids = new Set(txs.map(tx => tx.id));
    return nats.event('terrabase.applied.fra.' + org.id + '.' + project.id + '.' + db.id, result => {
      if (ids.has(result.id)) {
        notices.set(result.id, (notices.get(result.id) ?? 0) + 1);
        if (!states.has(result.id)) {
          order.push(result.id);
          states.set(result.id, result.state);
        }
      }
      return result.id === txs.at(-1).id;
    }).then(() => ({ order, states, repeated: [...notices.values()].reduce((sum, count) => sum + count - 1, 0) }));
  };
  const inOrder = async (db, txs, seen, from = 0) => {
    const { order, states, repeated } = await seen;
    assert.deepEqual(order, txs.map(tx => tx.id));
    assert([...states.values()].every(state => state === 'confirmed'), JSON.stringify([...states.values()]));
    assert.deepEqual(await applications(db, from), txs.map(tx => tx.id));
    assert.equal(await value(db), txs.length - 1);
    return repeated;
  };
  const release = (db, key) => {
    delete blocked['v1/keys/' + scope(db) + '/' + key];
    writeFileSync(journeyState + '/r2-unavailable-keys', JSON.stringify(blocked), { mode: 0o600 });
  };
  const moved = await database('moved');
  const movedInsert = audited(transaction([{ op: 'insert', table: 'items', values: { id: 40, value: 1 } }]));
  const movedUpdate = audited(transaction([{ op: 'update', table: 'items', key: { id: 40 }, values: { value: 2 } }]));
  movedUpdate.hlc = { ...movedInsert.hlc, logical: 1 };
  const movedKey = await refusedKey(moved);
  let movedEarly = false;
  const movedInsertNotice = notice(moved, movedInsert);
  const movedUpdateNotice = notice(moved, movedUpdate).then(result => { movedEarly = true; return result; });
  const movedAttempts = held(moved, send(moved, movedInsert, movedKey, 'fra'), 4);
  await send(moved, movedUpdate, keyId, 'gru');
  await applied(other, transaction([effect(108)]));
  await movedAttempts;
  assert(!movedEarly);
  assert.deepEqual(await rows(moved, 'SELECT count(*) FROM items WHERE id = 40'), [[0]]);
  assert.deepEqual(await applications(moved), []);
  unblock();
  assert.equal((await movedInsertNotice).state, 'confirmed');
  assert.equal((await movedUpdateNotice).state, 'confirmed');
  assert.deepEqual(await rows(moved, 'SELECT value FROM items WHERE id = 40'), [[2]]);
  assert.deepEqual(await applications(moved), [movedInsert.id, movedUpdate.id]);
  console.log('home moved under a held message: the insert addressed to the former home fra held the update addressed to the current home gru in the same queue; after release both applied in stream order, final value 2');

  await sql(first, 'CREATE TABLE parents (id INTEGER PRIMARY KEY)');
  await sql(first, 'CREATE TABLE children (id INTEGER PRIMARY KEY, parent INTEGER NOT NULL REFERENCES parents (id))');
  const parent = transaction([{ op: 'insert', table: 'parents', values: { id: 1 } }]);
  const child = transaction([{ op: 'insert', table: 'children', values: { id: 1, parent: 1 } }]);
  child.hlc = { ...parent.hlc, logical: 1 };
  const parentKey = await refusedKey(first);
  const parentNotice = notice(first, parent);
  const childNotice = notice(first, child);
  const parentAttempts = held(first, send(first, parent, parentKey), 6);
  await send(first, child);
  await applied(other, transaction([effect(104)]));
  await parentAttempts;
  assert.deepEqual(await rows(first, 'SELECT count(*) FROM children'), [[0]]);
  unblock();
  assert.equal((await parentNotice).state, 'confirmed');
  assert.equal((await childNotice).state, 'confirmed');
  assert.deepEqual(await rows(first, 'SELECT children.id, parents.id FROM children JOIN parents ON parents.id = children.parent'), [[1, 1]]);
  assert.equal(await heldGauge(), 0);
  assert.equal(await ageGauge(), 0);
  console.log('held parent insert: the child insert waited behind it and both applied in order; held=0, age 0');

  const consumers = async () => ((await nats.request('$JS.API.CONSUMER.NAMES.TERRABASE_PUSH_FRA')).consumers ?? []).filter(name => name.startsWith('home-gru-epoch-'));
  const bulk = await create('bulk');
  await sql(bulk, 'CREATE TABLE items (id INTEGER PRIMARY KEY, value INTEGER)');
  await sql(bulk, 'CREATE TABLE applications (tx TEXT PRIMARY KEY, value INTEGER)');
  await sql(bulk, 'INSERT INTO items VALUES (1, 0)');
  await putSealedKey({ master, access, secret }, scope(bulk), keyId, dataKey);
  const bulkKey = await refusedKey(bulk);
  const head = audited(transaction([effect(0)]));
  await held(bulk, send(bulk, head, bulkKey));
  const window = 4096;
  const later = [];
  for (let n = 1; n <= window + 64; n++) {
    const next = audited({ ...transaction([effect(n)]), hlc: { ...head.hlc, physicalMilliseconds: head.hlc.physicalMilliseconds + 1, logical: n } });
    later.push(next);
    await send(bulk, next);
  }
  await applied(other, transaction([effect(105)]));
  const consumer = await nats.request('$JS.API.CONSUMER.INFO.TERRABASE_PUSH_FRA.' + (await consumers())[0]);
  assert(consumer.num_ack_pending <= 32, JSON.stringify(consumer));
  assert.equal(await heldGauge(), 1);
  console.log('held database with ' + (window + 65) + ' messages: the other database applied after all of them; consumer ack pending ' + consumer.num_ack_pending + ' (bound 32)');
  const order = [];
  const states = new Map();
  let firstNoticeAt = 0;
  const drained = nats.event('terrabase.applied.fra.' + org.id + '.' + project.id + '.' + bulk.id, result => {
    firstNoticeAt ||= Date.now();
    if (!states.has(result.id)) {
      order.push(result.id);
      states.set(result.id, result.state);
    }
    return result.id === later.at(-1).id;
  });
  const releasedAt = Date.now();
  unblock();
  await drained;
  const drainSeconds = (Date.now() - releasedAt) / 1000;
  const applySeconds = (Date.now() - firstNoticeAt) / 1000;
  assert.deepEqual(order, [head.id, ...later.map(tx => tx.id)]);
  assert([...states.values()].every(state => state === 'confirmed'));
  assert.equal(await value(bulk), window + 64);
  assert.deepEqual(await applications(bulk), [head.id, ...later.map(tx => tx.id)]);
  console.log('released: ' + order.length + ' messages applied in stream order, all confirmed, final value ' + (window + 64) + '; ' + drainSeconds.toFixed(1) + ' s from release to the last notice, ' + applySeconds.toFixed(1) + ' s from the first notice to the last, ' + Math.round(order.length / applySeconds) + ' messages/s');

  const relocated = await create('relocated');
  await sql(relocated, 'CREATE TABLE pairs (id INTEGER PRIMARY KEY, a INTEGER, b INTEGER)');
  await sql(relocated, 'CREATE TABLE applications (tx TEXT PRIMARY KEY, value INTEGER)');
  await sql(relocated, 'INSERT INTO pairs VALUES (1, 0, 0)');
  await putSealedKey({ master, access, secret }, scope(relocated), keyId, dataKey);
  const onFra = async statement => (await request('http://127.0.0.2:8081', '/v1/projects/' + project.id + '/databases/' + relocated.id + '/sql/execute', { statements: [{ sql: statement }] })).results[0].rows;
  const columnA = audited(transaction([{ op: 'update', table: 'pairs', key: { id: 1 }, values: { a: 1 } }]));
  const columnB = audited(transaction([{ op: 'update', table: 'pairs', key: { id: 1 }, values: { b: 2 } }]));
  columnB.hlc = { ...columnA.hlc, logical: 1 };
  const relocatedKey = await refusedKey(relocated);
  const fraAcks = new Set();
  let skipTarget = null;
  const skipped = nats.event('$JS.ACK.>', reply => {
    const tokens = reply.subject.split('.');
    const at = tokens[2] === 'TERRABASE_PUSH_FRA' ? 3 : 5;
    if (tokens[at]?.startsWith('home-fra-epoch-') && reply.raw === '+ACK') fraAcks.add(tokens[at + 2]);
    return skipTarget !== null && fraAcks.has(skipTarget);
  });
  const sentA = await send(relocated, columnA, relocatedKey);
  skipTarget = String(sentA.seq);
  await rounds(relocated, sentA, 2);
  await skipped;
  const moveStarted = Date.now();
  const relocatedPath = '/v1/projects/' + project.id + '/databases/' + relocated.id;
  const forwarded = await request(home, relocatedPath, { regions: ['fra'] }, user, 'PATCH', [200, 401]);
  assert(forwarded.homeRegion === 'fra' || forwarded.error?.code === 'invalid_key', JSON.stringify(forwarded));
  const relocatedBody = await request(home, relocatedPath, undefined, user, 'GET');
  assert.equal(relocatedBody.homeRegion, 'fra', JSON.stringify(relocatedBody));
  const moveSeconds = (Date.now() - moveStarted) / 1000;
  const sentB = await send(relocated, columnB, keyId, 'fra');
  await progress(sentB, 1, consumer => consumer.startsWith('home-fra-epoch-'));
  assert.deepEqual(await onFra('SELECT a, b FROM pairs WHERE id = 1'), [[0, 0]]);
  assert.deepEqual(await onFra('SELECT count(*) FROM applications'), [[0]]);
  const aNotice = notice(relocated, columnA);
  const bNotice = notice(relocated, columnB);
  unblock();
  assert.equal((await aNotice).state, 'confirmed');
  assert.equal((await bNotice).state, 'confirmed');
  assert.deepEqual(await onFra('SELECT a, b FROM pairs WHERE id = 1'), [[1, 2]]);
  assert.deepEqual((await onFra('SELECT tx FROM applications ORDER BY rowid')).map(([tx]) => tx), [columnA.id, columnB.id]);
  console.log('home moved to fra through the regions API (' + moveSeconds.toFixed(1) + ' s) while sequence ' + sentA.seq + ' (column a) was held at gru and acknowledged unapplied by fra\'s consumer; sequence ' + sentB.seq + ' (column b), addressed to fra after the move, waited behind it; both applied once at fra in stream order (application log), a=1 b=2');

  const unknownId = id();
  const unknownHeld = logged(gru, 'A pushed database is held', { Database: unknownId, Kind: 'unknown_database' }, offset());
  assert(!(await nats.request('terrabase.push.fra.gru.' + org.id + '.' + project.id + '.' + unknownId, Buffer.from('catalog-missing'))).error);
  await unknownHeld;
  await applied(other, transaction([effect(106)]));
  const doomed = await create('doomed');
  await request(home, '/v1/projects/' + project.id + '/databases/' + doomed.id, undefined, user, 'DELETE', 204);
  const parkedDeleted = nats.event('terrabase.push-parked.gru.deleted_database.>');
  const deletedPayload = Buffer.from('owed-to-nobody');
  const deletedAck = await nats.request('terrabase.push.fra.gru.' + scope(doomed).replaceAll('/', '.'), deletedPayload);
  await parkedDeleted;
  const parkedEmpty = nats.event('terrabase.push-parked.gru.empty.>');
  await nats.request('terrabase.push.fra.gru.' + scope(other).replaceAll('/', '.'), Buffer.alloc(0));
  await parkedEmpty;
  await applied(other, transaction([effect(107)]));
  assert.equal(await value(other), 107);
  const parked = await nats.request('$JS.API.STREAM.INFO.TERRABASE_PUSH_PARKED_GRU', { subjects_filter: '>' });
  const parkedSubjects = Object.keys(parked.state.subjects);
  assert.equal(parked.state.messages, 2);
  assert(parkedSubjects.some(subject => subject.includes('.deleted_database.')));
  assert(parkedSubjects.some(subject => subject.includes('.empty.')));
  assert(!parkedSubjects.some(subject => subject.includes('.unknown_database.')));
  const stored = await nats.request('$JS.API.STREAM.MSG.GET.TERRABASE_PUSH_PARKED_GRU', { last_by_subj: parkedSubjects.find(subject => subject.includes('.deleted_database.')) });
  const record = JSON.parse(Buffer.from(stored.message.data, 'base64'));
  assert.equal(record.sequence, deletedAck.seq);
  assert.equal(record.database, scope(doomed));
  assert.equal(Buffer.from(record.payload, 'base64').toString(), deletedPayload.toString());
  const replacement = await nats.request('$JS.API.STREAM.INFO.TERRABASE_PUSH_FRA');
  assert.equal(Date.parse(record.source.split('/')[1]), Date.parse(replacement.created));
  const metrics = (await (await fetch('http://127.0.0.1:9091/metrics')).text()).split('\n');
  for (const reason of ['deleted_database', 'empty']) {
    assert(metrics.some(line => line.startsWith('terrabase_push_parked_total{') && line.includes('reason="' + reason + '"') && line.endsWith(' 1')));
  }
  assert(metrics.some(line => line.startsWith('terrabase_push_apply_retries_total{') && line.includes('reason="unknown_database"')));
  assert(metrics.some(line => line.startsWith('terrabase_push_parked_records{') && line.includes('reason="previous_layout"') && line.endsWith(' 1')));
  assert.equal(parked.config.max_age, 0);
  assert.equal(parked.config.discard, 'new');
  const kept = await nats.request('$JS.API.STREAM.INFO.TERRABASE_PUSH_PARK_GRU');
  assert.equal(kept.state.messages, 1);
  assert.deepEqual([kept.config.max_age, kept.config.discard, kept.config.max_msgs, kept.config.subjects], [0, 'new', -1, ['terrabase.push-park.gru.>']]);
  const keptRecord = await nats.request('$JS.API.STREAM.MSG.GET.TERRABASE_PUSH_PARK_GRU', { seq: 1 });
  assert.equal(Buffer.from(keptRecord.message.data, 'base64').toString('hex'), previousPayload.toString('hex'));
  assert.equal(await heldGauge(), 1);
  console.log('unknown database: held and retried, not parked, other databases applied; deleted database and empty payload: parked with durable records in a stream without expiry; the previous layout\'s record, created ' + Math.round((Date.now() - gruStarted) / 1000) + 's ago, is kept unchanged and counted');

  const lapse = async (stopped = () => {}) => {
    gru.kill('SIGSTOP');
    await new Promise(resolveStop => setTimeout(resolveStop, 8000));
    stopped();
    let renewals = 0;
    const renewed = nats.event('$KV.terrabase-region-writers.gru', () => ++renewals >= 3);
    gru.kill('SIGCONT');
    await renewed;
    await trusted(9091);
  };
  const flip = async node => {
    const stored = await nats.request('$JS.API.STREAM.MSG.GET.KV_terrabase-region-owners', { last_by_subj: '$KV.terrabase-region-owners.gru' });
    const owner = JSON.parse(Buffer.from(stored.message.data, 'base64'));
    const epoch = owner.epoch + 1;
    const changed = { ...owner, node, epoch, previous: owner.node, activated: true, transfer: null,
      prepared: { source: owner.node, target: node, epoch: owner.epoch } };
    const ack = await nats.request('$KV.terrabase-region-owners.gru', changed, { 'Nats-Expected-Last-Subject-Sequence': String(stored.message.seq) });
    assert(!ack.error, JSON.stringify(ack));
    return epoch;
  };
  const advisory = (kind, name) => nats.event('$JS.EVENT.ADVISORY.CONSUMER.' + kind + '.TERRABASE_PUSH_FRA.' + name, event => event.consumer?.startsWith('home-gru-epoch-'));

  const dropped = await database('dropped');
  const droppedKey = await refusedKey(dropped);
  const droppedTxs = series(42);
  const droppedSeen = collect(dropped, droppedTxs);
  await held(dropped, send(dropped, droppedTxs[0], droppedKey));
  for (const tx of droppedTxs.slice(1, 41)) await send(dropped, tx);
  await applied(other, transaction([effect(200)]));
  const readBack = relay.drop('$JS.API.STREAM.MSG.GET.TERRABASE_PUSH_FRA');
  unblock();
  await readBack;
  await send(dropped, droppedTxs[41]);
  const droppedRepeats = await inOrder(dropped, droppedTxs, droppedSeen);
  console.log('read-back dropped: the broker connection closed on the held database\'s read-back call; all ' + droppedTxs.length + ' messages applied exactly once in stream order (application log), all confirmed, final value ' + (droppedTxs.length - 1) + '; ' + droppedRepeats + ' repeated notices of recorded results');

  const lapsed = await database('lapsed');
  const headKey = await refusedKey(lapsed);
  const rereadKey = await refusedKey(lapsed);
  const lapsedTxs = series(72);
  const lapsedSeen = collect(lapsed, lapsedTxs);
  await held(lapsed, send(lapsed, lapsedTxs[0], headKey));
  for (const tx of lapsedTxs.slice(1, 32)) await send(lapsed, tx);
  const rereadHead = await send(lapsed, lapsedTxs[32], rereadKey);
  for (const tx of lapsedTxs.slice(33, 71)) await send(lapsed, tx);
  await applied(other, transaction([effect(201)]));
  const rereadHeld = logged(gru, 'A pushed database is held', { Database: lapsed.id, Sequence: String(rereadHead.seq) }, offset());
  release(lapsed, headKey);
  await rereadHeld;
  const reopened = advisory('CREATED', '*');
  await lapse(() => release(lapsed, rereadKey));
  await send(lapsed, lapsedTxs[71]);
  const lapsedRepeats = await inOrder(lapsed, lapsedTxs, lapsedSeen);
  await reopened;
  console.log('writer lease lapsed and renewed on the same node while a read-back head was held: the consumer was recreated and all ' + lapsedTxs.length + ' messages applied exactly once in stream order (application log), all confirmed; ' + lapsedRepeats + ' repeated notices of recorded results');

  const flipped = await database('flipped');
  const [previous] = await consumers();
  const flippedTxs = series(300);
  const removed = advisory('DELETED', previous);
  const stopped = logged(gru, 'ended its consumer run at writer epoch', {}, offset());
  const createdB = advisory('CREATED', '*');
  const toB = await flip('journey-3');
  await stopped;
  const appliesBefore = await scrape(/^terrabase_push_applies_total[ {]/);
  assert.equal((await createdB).consumer, 'home-gru-epoch-' + toB);
  await removed;
  const flippedSeen = collect(first, flippedTxs);
  for (const tx of flippedTxs) await send(first, tx);
  const { order: flippedOrder, states: flippedStates, repeated: flippedRepeats } = await flippedSeen;
  assert.deepEqual(flippedOrder, flippedTxs.map(tx => tx.id));
  assert.equal(flippedRepeats, 0);
  const flippedCounts = Object.groupBy([...flippedStates.values()], state => state);
  assert.equal(flippedStates.size, flippedTxs.length);
  assert(!flippedCounts.provisional, JSON.stringify(flippedCounts));
  assert.equal(await scrape(/^terrabase_push_applies_total[ {]/), appliesBefore);
  assert.deepEqual(await consumers(), ['home-gru-epoch-' + toB]);
  const fresh = await nats.request('$JS.API.CONSUMER.INFO.TERRABASE_PUSH_FRA.home-gru-epoch-' + toB);
  assert.equal(fresh.num_redelivered, 0, JSON.stringify(fresh));
  console.log('writer change with a pull outstanding: ' + previous + ' deleted, home-gru-epoch-' + toB + ' created; the new writer decided ' + flippedTxs.length + ' messages once each in stream order (' + Object.entries(flippedCounts).map(([state, list]) => state + '=' + list.length).join(', ') + ' against its own copy, which this proof does not prepare), the old writer applied none after the change, redelivered 0');

  const newer = 'home-gru-epoch-' + (toB + 5);
  const fake = await nats.request('$JS.API.CONSUMER.CREATE.TERRABASE_PUSH_FRA.' + newer, { stream_name: 'TERRABASE_PUSH_FRA',
    config: { name: newer, durable_name: newer, ack_policy: 'explicit', deliver_policy: 'all', filter_subject: 'terrabase.push.fra.>' } });
  assert(!fake.error, JSON.stringify(fake));
  const staleWaits = logged(gru, 'a consumer of a newer writer epoch', {}, offset());
  const toA = await flip('journey-1');
  await staleWaits;
  const pending = series(1);
  let early = false;
  const pendingNotice = notice(flipped, pending[0]).then(result => { early = true; return result; });
  await send(flipped, pending[0]);
  await logged(gru, 'a consumer of a newer writer epoch', {}, offset());
  assert(!early);
  const waitingNames = await consumers();
  assert(waitingNames.includes(newer), JSON.stringify(waitingNames));
  assert(!waitingNames.includes('home-gru-epoch-' + toA), JSON.stringify(waitingNames));
  const createdA = advisory('CREATED', 'home-gru-epoch-' + toA);
  assert((await nats.request('$JS.API.CONSUMER.DELETE.TERRABASE_PUSH_FRA.' + newer)).success);
  await createdA;
  assert.equal((await pendingNotice).state, 'confirmed');
  assert.deepEqual(await consumers(), ['home-gru-epoch-' + toA]);
  console.log('stale epoch ' + toA + ' beside consumer ' + newer + ': neither deleted nor read it and applied nothing; after it was removed, home-gru-epoch-' + toA + ' applied the waiting message');

  const quiet = await database('replayed');
  const quietTxs = series(20);
  const quietSeen = collect(quiet, quietTxs);
  for (const tx of quietTxs) await send(quiet, tx);
  await inOrder(quiet, quietTxs, quietSeen);
  const walFrames = () => {
    const wal = readFileSync(resolve(state, 'frontier-gru', org.id, project.id, quiet.id + '.db-wal'));
    const page = wal.readUInt32BE(8) === 1 ? 65536 : wal.readUInt32BE(8);
    const salt = wal.readUInt32BE(16) + ':' + wal.readUInt32BE(20);
    const pages = [];
    for (let at = 32; at + 24 + page <= wal.length && wal.readUInt32BE(at + 8) + ':' + wal.readUInt32BE(at + 12) === salt; at += 24 + page) pages.push(wal.readUInt32BE(at));
    return { salt, pages };
  };
  const written = (from, to) => {
    assert.equal(to.salt, from.salt);
    const counts = new Map();
    for (const number of to.pages.slice(from.pages.length)) counts.set(number, (counts.get(number) ?? 0) + 1);
    return counts;
  };
  const beforeReplay = walFrames();
  const replayedLast = notice(quiet, quietTxs.at(-1));
  await lapse();
  assert.equal((await replayedLast).state, 'confirmed');
  const afterReplay = walFrames();
  const replayWrites = written(beforeReplay, afterReplay);
  assert.deepEqual(await applications(quiet), quietTxs.map(tx => tx.id));
  const replayedFrom = await lastApplication(quiet);
  const controlTxs = series(quietTxs.length);
  const controlSeen = collect(quiet, controlTxs);
  for (const tx of controlTxs) await send(quiet, tx);
  await inOrder(quiet, controlTxs, controlSeen, replayedFrom);
  const controlWrites = written(afterReplay, walFrames());
  const perMessage = [...controlWrites].filter(([, count]) => count >= controlTxs.length).map(([number]) => number);
  assert(perMessage.length > 0, JSON.stringify([...controlWrites]));
  assert(perMessage.every(number => !replayWrites.has(number)), JSON.stringify({ perMessage, replay: [...replayWrites] }));
  console.log('replay of ' + quietTxs.length + ' applied messages after a same-node reopen: the pages every new apply writes (' + perMessage.join(', ') + ') were written 0 times by the replay (' + [...replayWrites.values()].reduce((sum, count) => sum + count, 0) + ' frames of other pages by storage bookkeeping); ' + controlTxs.length + ' new messages wrote each of them at least ' + controlTxs.length + ' times');


  const finished = await database('finished');
  const finishedKey = await refusedKey(finished);
  const finishedTxs = series(2);
  const finishedSeen = collect(finished, finishedTxs);
  await held(finished, send(finished, finishedTxs[0], finishedKey));
  const finishedLater = await send(finished, finishedTxs[1]);
  const applyPath = '/v1/sync/organizations/' + org.id + '/projects/' + project.id + '/databases/' + finished.id + '/apply';
  const positioned = await request(home, applyPath + '?position=' + finishedLater.seq, finishedTxs[1], undefined, 'POST', 503);
  const unpositioned = await request(home, applyPath, finishedTxs[1], undefined, 'POST', 503);
  assert.equal(positioned.error.code, 'unavailable');
  assert.equal(unpositioned.error.code, 'unavailable');
  assert.deepEqual(await applications(finished), []);
  release(finished, finishedKey);
  await inOrder(finished, finishedTxs, finishedSeen);
  assert.equal((await request(home, applyPath + '?position=' + finishedLater.seq, finishedTxs[1])).state, 'confirmed');
  assert.equal((await request(home, applyPath, finishedTxs[1])).state, 'confirmed');
  console.log('a partner finishing a lost node\'s held transaction at sequence ' + finishedLater.seq + ' while the earlier position was held: 503 with and without its position, nothing applied; after release both applied in stream order and both request shapes answer confirmed');

  const gapped = await database('gapped');
  const gappedKey = await refusedKey(gapped);
  const [gapHead, gapTail] = series(2);
  const gapSent = await held(gapped, send(gapped, gapHead, gappedKey));
  const gapHeld = logged(gru, 'A pushed database is held', { Database: gapped.id, Kind: 'purged' }, offset());
  const build = await nats.request('terrabase.push-state.fra.build.journey-2', Buffer.from('markers'));
  assert(!build.error, JSON.stringify(build));
  const marker = await nats.request('terrabase.push-state.fra.purged.' + scope(gapped).replaceAll('/', '.'), Buffer.from(String(gapSent.seq)));
  assert(!marker.error, JSON.stringify(marker));
  const purged = await nats.request('$JS.API.STREAM.PURGE.TERRABASE_PUSH_FRA', { filter: 'terrabase.push.fra.*.' + scope(gapped).replaceAll('/', '.'), seq: gapSent.seq + 1 });
  assert(purged.success, JSON.stringify(purged));
  let gapApplied = null;
  const gapNotice = notice(gapped, gapTail).then(result => { gapApplied = result; }, () => {});
  await send(gapped, gapTail);
  release(gapped, gappedKey);
  await gapHeld;
  await applied(other, transaction([effect(300)]));
  assert.equal(gapApplied, null);
  assert.deepEqual(await applications(gapped), []);
  assert.equal(await scrape(/^terrabase_push_held\{.*reason="purged"/), 1);
  console.log('purge marker at sequence ' + gapSent.seq + ' ahead of the frontier of a database whose head was purged unapplied: held with reason purged, the later message not applied, the other database applied');

  const fraStream = await nats.request('$JS.API.STREAM.INFO.TERRABASE_PUSH_FRA');
  assert((await nats.request('$JS.API.STREAM.DELETE.TERRABASE_PUSH_FRA')).success);
  assert(!(await nats.request('$JS.API.STREAM.CREATE.TERRABASE_PUSH_FRA', fraStream.config)).error);
  const mixed = await database('mixed');
  const [mixedFirst, mixedRemoved, mixedLater] = series(3);
  await applied(mixed, mixedFirst);
  const mixedKey = await refusedKey(mixed);
  const mixedSent = await held(mixed, send(mixed, mixedRemoved, mixedKey));
  const mixedHeld = logged(gru, 'A pushed database is held', { Database: mixed.id, Kind: 'origin_previous_build' }, offset());
  const mixedPurge = await nats.request('$JS.API.STREAM.PURGE.TERRABASE_PUSH_FRA', { filter: 'terrabase.push.fra.*.' + scope(mixed).replaceAll('/', '.'), seq: mixedSent.seq + 1 });
  assert(mixedPurge.success, JSON.stringify(mixedPurge));
  let mixedApplied = null;
  const mixedNotice = notice(mixed, mixedLater).then(result => { mixedApplied = result; return result; });
  await send(mixed, mixedLater);
  release(mixed, mixedKey);
  await mixedHeld;
  assert.equal(mixedApplied, null);
  assert.deepEqual(await applications(mixed), [mixedFirst.id]);
  const upgraded = await nats.request('terrabase.push-state.fra.build.journey-2', Buffer.from('markers'));
  assert(!upgraded.error, JSON.stringify(upgraded));
  assert.equal((await mixedNotice).state, 'confirmed');
  assert.deepEqual(await applications(mixed), [mixedFirst.id, mixedLater.id]);
  console.log('previous-build origin (no build record) removed a database\'s messages through sequence ' + mixedSent.seq + ' without a marker: the new-build home held it with reason origin_previous_build and applied nothing past the removal; once the origin node wrote its build record the later message applied');

  const strays = Array.from({ length: 130 }, () => id());
  const strayStarted = Date.now();
  for (const stray of strays) {
    for (let n = 0; n < 32; n++) {
      const ack = await nats.request('terrabase.push.fra.gru.' + org.id + '.' + project.id + '.' + stray, Buffer.from('catalog-missing'));
      assert(!ack.error, JSON.stringify(ack));
    }
  }
  await applied(other, transaction([effect(301)]));
  assert.equal(await value(other), 301);
  const strayConsumer = await nats.request('$JS.API.CONSUMER.INFO.TERRABASE_PUSH_FRA.' + (await consumers())[0]);
  assert(strayConsumer.num_ack_pending < 4096, JSON.stringify(strayConsumer));
  console.log('130 unknown databases with 32 messages each (' + strays.length * 32 + ' messages) held on one origin: the healthy database applied after them in ' + ((Date.now() - strayStarted) / 1000).toFixed(1) + ' s; consumer ack pending ' + strayConsumer.num_ack_pending + ', unknown_database holds ' + await scrape(/^terrabase_push_held\{.*reason="unknown_database"/));
} finally {
  rmSync(journeyState + '/r2-unavailable-keys', { force: true });
  nats?.close();
  relay.close();
  await Promise.all([...processes].map(child => stop(child)));
}
