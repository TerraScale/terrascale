import { connect } from '@tursodatabase/sync';
import { spawn, execFile } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { performance } from 'node:perf_hooks';
import { promisify } from 'node:util';

const [hostBin, cliBin, outPath, repeatsArg = '3'] = process.argv.slice(2);
if (!hostBin || !cliBin || !outPath) {
  throw new Error('usage: qualify.mjs <ts-sync-revision-host> <tursodb> <out.json> [repeats]');
}
const repeats = Number(repeatsArg);
const DB = 'orders';
const PAD = 'x'.repeat(512);
const BULK_PAD = 'y'.repeat(1024);
const WAL_FRAME_BYTES = 4096 + 24;
const CHECKPOINT_FRAMES = 1024;
const RETAINED_GENERATIONS = 8;
const HORIZON_GENERATIONS = 64 + 8;
const WAL_BOUND_BYTES = 32 + (CHECKPOINT_FRAMES + 256) * WAL_FRAME_BYTES;
const hex = () => randomBytes(24).toString('hex');
const run = promisify(execFile);

function freePort() {
  return new Promise((resolve, reject) => {
    const srv = createServer();
    srv.once('error', reject);
    srv.listen(0, '127.0.0.1', () => {
      const { port } = srv.address();
      srv.close(() => resolve(port));
    });
  });
}

async function waitForPort(port, proc) {
  for (let i = 0; i < 200; i++) {
    if (proc.exitCode !== null) throw new Error(`process exited with ${proc.exitCode}`);
    try {
      await fetch(`http://127.0.0.1:${port}/`, { method: 'OPTIONS', signal: AbortSignal.timeout(500) });
      return;
    } catch {
      await new Promise((r) => setTimeout(r, 25));
    }
  }
  throw new Error(`port ${port} never accepted connections`);
}

class Host {
  constructor(root, tokensPath) {
    this.root = root;
    this.tokensPath = tokensPath;
    this.port = 0;
  }

  async start() {
    const proc = spawn(hostBin, ['--listen', `127.0.0.1:${this.port}`, '--data', join(this.root, 'data'), '--tokens', this.tokensPath], {
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    this.stderr = '';
    proc.stderr.on('data', (d) => (this.stderr += d));
    this.port = await new Promise((resolve, reject) => {
      let out = '';
      proc.stdout.on('data', (d) => {
        out += d;
        const m = out.match(/listening 127\.0\.0\.1:(\d+)/);
        if (m) resolve(Number(m[1]));
      });
      proc.once('exit', (code) => reject(new Error(`host exited ${code}: ${this.stderr}`)));
    });
    this.proc = proc;
  }

  async stop(signal) {
    const exited = once(this.proc, 'exit');
    this.proc.kill(signal);
    const [code, sig] = await exited;
    return { code, signal: sig };
  }
}

class Remote {
  constructor(base, token, adminToken) {
    this.base = base;
    this.token = token;
    this.adminToken = adminToken;
  }

  async pipeline(sqls) {
    const r = await fetch(`${this.base}/db/${DB}/v2/pipeline`, {
      method: 'POST',
      headers: { authorization: `Bearer ${this.token}`, 'content-type': 'application/json' },
      body: JSON.stringify({ requests: sqls.map((sql) => ({ type: 'execute', stmt: { sql } })) }),
      signal: AbortSignal.timeout(30000),
    });
    if (!r.ok) throw new Error(`pipeline HTTP ${r.status}: ${await r.text()}`);
    const body = await r.json();
    const failed = body.results.find((x) => x.type === 'error');
    if (failed) throw new Error(`pipeline error: ${JSON.stringify(failed.error)}`);
    return body.results.map((x) => x.response.result);
  }

  async rows(sql) {
    const [result] = await this.pipeline([sql]);
    return result.rows.map((row) => row.map((c) => c.value));
  }

  async ids() {
    return (await this.rows('SELECT id FROM t ORDER BY id')).map((r) => r[0]);
  }

  async admin(op) {
    const r = await fetch(`${this.base}/admin/db/${DB}/${op}`, {
      method: 'POST',
      headers: { authorization: `Bearer ${this.adminToken}` },
      signal: AbortSignal.timeout(120000),
    });
    if (!r.ok) throw new Error(`admin ${op} HTTP ${r.status}: ${await r.text()}`);
    return r.json();
  }
}

class Client {
  constructor(name, root, url, token, opts = {}) {
    this.name = name;
    this.path = join(root, `${name}.db`);
    this.url = url;
    this.token = token;
    this.opts = opts;
    this.pending = new Set();
    this.offline = false;
    this.requests = 0;
    this.pulls = [];
    this.seq = 0;
    this.onFirstPull = null;
  }

  fetch = async (u, o) => {
    if (this.offline) throw new Error('offline by test');
    this.requests++;
    const r = await fetch(u, { ...o, signal: AbortSignal.timeout(60000) });
    if (String(u).endsWith('/pull-updates')) {
      this.pulls.push({ status: r.status, kind: r.headers.get('x-ts-sync-pull') ?? 'none' });
      if (this.onFirstPull) {
        const hook = this.onFirstPull;
        this.onFirstPull = null;
        const body = await r.arrayBuffer();
        await hook();
        return new Response(body, { status: r.status, headers: r.headers });
      }
    }
    if (!r.ok) throw new Error(`HTTP ${r.status}: ${await r.text()}`);
    return r;
  };

  async open() {
    this.db = await connect({ path: this.path, url: this.url, authToken: this.token, clientName: this.name, fetch: this.fetch, ...this.opts });
  }

  async ids() {
    return (await (await this.db.prepare('SELECT id FROM t ORDER BY id')).all()).map((r) => r.id);
  }

  async integrity() {
    const rows = await (await this.db.prepare('PRAGMA integrity_check')).all();
    return rows.map((r) => Object.values(r)[0]).join(',');
  }

  async writeOffline(n) {
    this.offline = true;
    const before = this.requests;
    try {
      for (let i = 0; i < n; i++) {
        const id = `${this.name}-${String(++this.seq).padStart(5, '0')}`;
        await this.db.exec(`INSERT INTO t(id, origin, v) VALUES ('${id}', '${this.name}', '${PAD}')`);
        this.pending.add(id);
      }
    } finally {
      this.offline = false;
    }
    if (this.requests !== before) throw new Error(`${this.name} used the network while offline`);
  }
}

class Repeat {
  constructor(index) {
    this.index = index;
    this.server = new Set();
    this.rolledBack = new Set();
    this.serial = 0;
    this.totals = { lost_acked_writes: 0, lost_local_writes: 0, skipped_commits: 0, phantom_rows: 0, stale_false_pulls: 0, unexpected_server_rows: 0, plan_mismatches: 0 };
    this.pulls = [];
    this.phases = [];
    this.notes = [];
    this.peak = { max_wal_bytes: 0, max_sealed_generations: 0, max_horizon_buckets: 0, max_manifest_bytes: 0 };
    this.counters = {};
  }

  observe(state, final) {
    for (const k of Object.keys(this.peak)) this.peak[k] = Math.max(this.peak[k], state.stats[k]);
    if (!final) return;
    for (const [k, v] of Object.entries(state.stats)) {
      if (typeof v === 'number' && !k.startsWith('max_')) this.counters[k] = (this.counters[k] ?? 0) + v;
    }
    for (const [k, v] of Object.entries(state.stats.full_reasons)) {
      this.counters[`full_${k}`] = (this.counters[`full_${k}`] ?? 0) + v;
    }
  }

  async serverWrite(n, tag = 's', pad = PAD, perRequest = 25) {
    const written = [];
    for (let done = 0; done < n; done += perRequest) {
      const ids = [];
      for (let i = done; i < Math.min(n, done + perRequest); i++) ids.push(`${tag}-${String(++this.serial).padStart(6, '0')}`);
      await this.remote.pipeline(ids.map((id) => `INSERT INTO t(id, origin, v) VALUES ('${id}', '${tag}', '${pad}')`));
      for (const id of ids) {
        this.server.add(id);
        written.push(id);
      }
    }
    return written;
  }

  async pull(c, expect) {
    const expected = new Set(this.server);
    const start = c.pulls.length;
    const t0 = performance.now();
    const changed = await c.db.pull();
    const ms = performance.now() - t0;
    const kinds = c.pulls.slice(start).map((p) => p.kind);
    const rows = new Set(await c.ids());
    const missing = [...expected].filter((id) => !rows.has(id));
    const lostLocal = [...c.pending].filter((id) => !rows.has(id));
    const phantom = [...rows].filter((id) => !expected.has(id) && !c.pending.has(id));
    const planOk = !expect || (kinds[0] ?? '').startsWith(expect);
    this.totals.skipped_commits += missing.length;
    this.totals.lost_local_writes += lostLocal.length;
    this.totals.phantom_rows += phantom.length;
    if (!changed && missing.length > 0) this.totals.stale_false_pulls++;
    if (!planOk) this.totals.plan_mismatches++;
    this.pulls.push({ phase: this.phase, client: c.name, changed, kinds, expect: expect ?? null, ms: Math.round(ms * 100) / 100, missing: missing.length, lost_local: lostLocal.length, phantom: phantom.length });
  }

  async push(c) {
    await c.db.push();
    for (const id of c.pending) this.server.add(id);
    c.pending.clear();
    await this.verifyServer();
  }

  async verifyServer() {
    const actual = new Set(await this.remote.ids());
    this.totals.lost_acked_writes += [...this.server].filter((id) => !actual.has(id)).length;
    this.totals.unexpected_server_rows += [...actual].filter((id) => !this.server.has(id)).length;
  }

  async phaseRun(name, fn) {
    this.phase = name;
    const t0 = performance.now();
    await fn();
    const state = await this.remote.admin('state');
    this.observe(state, false);
    this.phases.push({ name, ms: Math.round(performance.now() - t0), generation: state.generation, incarnation: state.incarnation, db_pages: state.db_pages, wal_bytes: state.wal_bytes });
  }

  async bootstrapExpiry(d) {
    d.onFirstPull = async () => {
      await this.serverWrite(5);
      await this.remote.admin('checkpoint');
    };
    let error = null;
    try {
      await d.open();
    } catch (e) {
      error = String(e?.message ?? e);
    }
    const explicit = error !== null && error.includes('409') && error.includes('revision_expired');
    this.expiry = { first_attempt_error: error, explicit, kinds: d.pulls.map((p) => p.kind) };
    if (!explicit) this.totals.plan_mismatches++;
    await d.open();
    await this.pull(d);
  }

  async execute() {
    const root = await mkdtemp(join(tmpdir(), `w02-durable-r${this.index}-`));
    this.root = root;
    const tokens = { admin: hex(), databases: { [DB]: hex() } };
    const tokensPath = join(root, 'tokens.json');
    await writeFile(tokensPath, JSON.stringify(tokens));
    const host = new Host(root, tokensPath);
    await host.start();
    const base = `http://127.0.0.1:${host.port}`;
    const url = `${base}/db/${DB}`;
    this.remote = new Remote(base, tokens.databases[DB], tokens.admin);
    const client = (name, opts) => new Client(name, root, url, tokens.databases[DB], opts);
    const a = client('a');
    const b = client('b');
    const c = client('c', { pullBytesThreshold: 16384 });
    const d = client('d', { pullBytesThreshold: 16384 });
    const e = client('e');
    const restartHost = async (signal) => {
      this.observe(await this.remote.admin('state'), true);
      const stopped = await host.stop(signal);
      this.notes.push({ phase: this.phase, stopped });
      if (signal === 'SIGTERM' && stopped.code !== 0) throw new Error(`graceful stop exited ${JSON.stringify(stopped)}: ${host.stderr}`);
    };
    try {
      await this.phaseRun('seed-and-bootstrap', async () => {
        await this.remote.pipeline(['CREATE TABLE t(id TEXT PRIMARY KEY, origin TEXT NOT NULL, v TEXT NOT NULL)']);
        await this.serverWrite(200);
        await a.open();
        await b.open();
        await this.pull(a);
        await this.pull(b);
      });
      await this.phaseRun('coordinated-checkpoint', async () => {
        await a.writeOffline(5);
        await b.writeOffline(5);
        await this.serverWrite(20);
        await this.remote.admin('checkpoint');
        await this.serverWrite(20);
        await this.pull(a, 'incremental');
        await this.push(a);
      });
      await this.phaseRun('raw-pragma-checkpoint', async () => {
        await a.writeOffline(5);
        await this.remote.pipeline(['PRAGMA wal_checkpoint(TRUNCATE)']);
        await this.serverWrite(1);
        await this.pull(a, 'full;rewritten');
        await this.push(a);
      });
      await this.phaseRun('raw-pragma-checkpoint-inside-one-request', async () => {
        await a.writeOffline(3);
        const x = `s-${String(++this.serial).padStart(6, '0')}`;
        const y = `s-${String(++this.serial).padStart(6, '0')}`;
        await this.remote.pipeline([
          `INSERT INTO t(id, origin, v) VALUES ('${x}', 's', '${PAD}')`,
          'PRAGMA wal_checkpoint(TRUNCATE)',
          `INSERT INTO t(id, origin, v) VALUES ('${y}', 's', '${PAD}')`,
        ]);
        this.server.add(x);
        this.server.add(y);
        await this.pull(a, 'full;rewritten');
        await this.push(a);
      });
      await this.phaseRun('chunked-bootstrap-after-checkpoints', async () => {
        await c.open();
        await this.pull(c);
        this.cBootstrapKinds = c.pulls.map((p) => p.kind);
      });
      await this.phaseRun('chunked-bootstrap-revision-expires', async () => {
        await this.bootstrapExpiry(d);
      });
      await this.phaseRun('retained-history-window', async () => {
        await c.writeOffline(4);
        for (let i = 1; i <= 10; i++) {
          await this.serverWrite(10);
          await this.remote.admin('checkpoint');
          await this.pull(a, 'incremental');
          if (i === 3) await this.pull(c, 'incremental');
        }
        await this.pull(c, 'incremental');
        await this.push(c);
        await this.pull(b, 'full;');
        await this.push(b);
      });
      await this.phaseRun('history-horizon-expires', async () => {
        await e.open();
        await e.writeOffline(3);
        for (let i = 1; i <= HORIZON_GENERATIONS + RETAINED_GENERATIONS + 2; i++) {
          await this.serverWrite(2);
          await this.remote.admin('checkpoint');
          if (i % RETAINED_GENERATIONS === 0) {
            await this.pull(a, 'incremental');
            await this.pull(c, i === RETAINED_GENERATIONS ? null : 'incremental');
          }
        }
        await this.pull(a, 'incremental');
        await this.pull(e, 'full;expired');
        await this.push(e);
      });
      await this.phaseRun('automatic-checkpoint-policy', async () => {
        await a.writeOffline(5);
        const before = (await this.remote.admin('state')).stats.checkpoints;
        this.bulk = await this.serverWrite(8000, 'bulk', BULK_PAD, 50);
        const after = (await this.remote.admin('state')).stats.checkpoints;
        this.autoCheckpoints = after - before;
        if (this.autoCheckpoints < 2) this.totals.plan_mismatches++;
        await this.pull(a, 'incremental');
        await this.push(a);
      });
      await this.phaseRun('graceful-restart', async () => {
        await a.writeOffline(3);
        await c.writeOffline(3);
        await this.serverWrite(10);
        await restartHost('SIGTERM');
        await host.start();
        await this.serverWrite(10);
        await this.pull(a, 'incremental');
        await this.push(a);
        await this.pull(c, 'incremental');
        await this.push(c);
      });
      await this.phaseRun('crash-restart', async () => {
        await a.writeOffline(3);
        await this.serverWrite(10);
        await restartHost('SIGKILL');
        await host.start();
        await this.serverWrite(5);
        await this.pull(a, 'incremental');
        await this.push(a);
      });
      await this.phaseRun('external-checkpoint-while-down', async () => {
        await a.writeOffline(3);
        await this.serverWrite(10);
        await restartHost('SIGKILL');
        const ext = await run(cliBin, [join(root, 'data', DB, 'data'), 'PRAGMA wal_checkpoint(TRUNCATE)']);
        this.notes.push({ phase: this.phase, external_checkpoint: ext.stdout.trim() });
        await host.start();
        await this.serverWrite(5);
        await this.pull(a, 'full;rewritten');
        await this.push(a);
      });
      await this.phaseRun('rewrite-vacuum-swap', async () => {
        for (let i = 0; i < this.bulk.length; i += 200) {
          const ids = this.bulk.slice(i, i + 200).map((id) => `'${id}'`).join(',');
          await this.remote.pipeline([`DELETE FROM t WHERE id IN (${ids})`]);
        }
        for (const id of this.bulk) this.server.delete(id);
        await a.writeOffline(3);
        const before = await this.remote.admin('state');
        const after = await this.remote.admin('rewrite');
        this.rewrite = { pages_before: before.db_pages, pages_after: after.db_pages };
        await this.serverWrite(5);
        await this.pull(a, 'full;rewritten');
        await this.push(a);
        await this.pull(c, 'full;rewritten');
      });
      await this.phaseRun('restore-backup-new-incarnation', async () => {
        await this.remote.admin('backup/b1');
        await a.writeOffline(3);
        const before = await this.remote.admin('state');
        const after = await this.remote.admin('restore/b1');
        if (before.incarnation === after.incarnation) this.totals.plan_mismatches++;
        await this.serverWrite(5);
        await this.pull(a, 'full;other-incarnation');
        await this.push(a);
        await this.pull(c, 'full;other-incarnation');
        await this.push(c);
      });
      await this.phaseRun('restore-rolls-back-unbacked-writes', async () => {
        await this.remote.admin('backup/b2');
        const lost = await this.serverWrite(20, 'rb');
        await this.pull(a, 'incremental');
        await a.writeOffline(3);
        await this.remote.admin('restore/b2');
        for (const id of lost) {
          this.server.delete(id);
          this.rolledBack.add(id);
        }
        await this.verifyServer();
        await this.pull(a, 'full;other-incarnation');
        await this.push(a);
      });
      await this.phaseRun('converge', async () => {
        for (const cl of [a, b, c, d, e]) {
          await this.pull(cl);
          await this.push(cl);
        }
        const server = await this.remote.ids();
        const want = [...this.server].sort();
        this.converged = { server_rows: server.length, clients: {} };
        if (JSON.stringify(server) !== JSON.stringify(want)) this.totals.unexpected_server_rows++;
        for (const cl of [a, b, c, d, e]) {
          await this.pull(cl);
          const ids = await cl.ids();
          const same = JSON.stringify(ids) === JSON.stringify(server);
          if (!same) this.totals.skipped_commits++;
          this.converged.clients[cl.name] = { rows: ids.length, equal_to_server: same, integrity: await cl.integrity() };
        }
        this.converged.server_integrity = (await this.remote.rows('PRAGMA integrity_check')).map((r) => r[0]).join(',');
      });
      this.state = await this.remote.admin('state');
      this.observe(this.state, true);
      for (const cl of [a, b, c, d, e]) await cl.db.close();
      const stopped = await host.stop('SIGTERM');
      this.notes.push({ phase: 'end', stopped });
    } catch (e) {
      this.error = `${this.phase}: ${e?.stack ?? e}\n${host.stderr ?? ''}`;
      if (host.proc && host.proc.exitCode === null && host.proc.signalCode === null) await host.stop('SIGKILL');
    }
    await rm(root, { recursive: true, force: true });
    const s = this.state ? this.peak : null;
    const bounded = !!s && s.max_sealed_generations <= RETAINED_GENERATIONS && s.max_horizon_buckets <= 8 && s.max_wal_bytes <= WAL_BOUND_BYTES;
    const clientsOk = !!this.converged && Object.values(this.converged.clients).every((x) => x.equal_to_server && x.integrity === 'ok') && this.converged.server_integrity === 'ok';
    const zero = Object.values(this.totals).every((v) => v === 0);
    return {
      repeat: this.index,
      pass: !this.error && zero && bounded && clientsOk,
      error: this.error ?? null,
      totals: this.totals,
      bounded: { pass: bounded, wal_bound_bytes: WAL_BOUND_BYTES, max_wal_bytes: s?.max_wal_bytes, max_sealed_generations: s?.max_sealed_generations, max_horizon_buckets: s?.max_horizon_buckets, max_manifest_bytes: s?.max_manifest_bytes },
      rolled_back_by_restore: this.rolledBack.size,
      automatic_checkpoints: this.autoCheckpoints,
      rewrite: this.rewrite,
      expiry: this.expiry,
      chunked_bootstrap_kinds: this.cBootstrapKinds,
      converged: this.converged,
      server_counters: this.counters,
      phases: this.phases,
      pulls: this.pulls,
      notes: this.notes,
    };
  }
}

async function control(index) {
  const root = await mkdtemp(join(tmpdir(), `w02-control-r${index}-`));
  const port = await freePort();
  await mkdir(join(root, 'data'));
  const proc = spawn(cliBin, ['--sync-server', `127.0.0.1:${port}`, '--sync-dir', join(root, 'data')], { stdio: ['ignore', 'ignore', 'pipe'] });
  let stderr = '';
  proc.stderr.on('data', (d) => (stderr += d));
  const result = { repeat: index };
  try {
    await waitForPort(port, proc);
    const remote = new Remote(`http://127.0.0.1:${port}`, 'unused', 'unused');
    await remote.pipeline(['CREATE TABLE t(id TEXT PRIMARY KEY, origin TEXT NOT NULL, v TEXT NOT NULL)']);
    for (let i = 0; i < 100; i++) await remote.pipeline([`INSERT INTO t(id, origin, v) VALUES ('s-${String(i).padStart(6, '0')}', 's', '${PAD}')`]);
    const a = new Client('a', root, `http://127.0.0.1:${port}/db/${DB}`, 'unused');
    await a.open();
    const before = (await a.ids()).length;
    await remote.pipeline(['PRAGMA wal_checkpoint(TRUNCATE)']);
    await remote.pipeline([`INSERT INTO t(id, origin, v) VALUES ('s-000100', 's', '${PAD}')`]);
    const changed = await a.db.pull();
    const after = (await a.ids()).length;
    await a.db.close();
    Object.assign(result, { rows_before: before, pull_returned: changed, rows_after: after, server_rows: (await remote.ids()).length });
    result.bug_reproduced = changed === false && after < result.server_rows;
  } catch (e) {
    result.error = `${e?.stack ?? e}\n${stderr}`;
  }
  if (proc.exitCode === null && proc.signalCode === null) {
    const exited = once(proc, 'exit');
    proc.kill('SIGKILL');
    await exited;
  }
  await rm(root, { recursive: true, force: true });
  return result;
}

const durable = [];
const controls = [];
const save = (pass) => writeFile(outPath, JSON.stringify({ node: process.version, pass, durable, controls }, null, 2));
for (let r = 1; r <= repeats; r++) {
  const t0 = performance.now();
  const result = await new Repeat(r).execute();
  result.ms = Math.round(performance.now() - t0);
  durable.push(result);
  await save(false);
  console.log(JSON.stringify({ repeat: r, pass: result.pass, totals: result.totals, bounded: result.bounded, error: result.error }));
}
for (let r = 1; r <= repeats; r++) {
  const result = await control(r);
  controls.push(result);
  console.log(JSON.stringify({ control: r, ...result }));
}
const pass = durable.every((r) => r.pass) && controls.every((r) => r.bug_reproduced);
await save(pass);
console.log(JSON.stringify({ pass }));
process.exit(pass ? 0 : 1);
