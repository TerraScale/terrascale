import { connect } from '@tursodatabase/sync';
import { spawn, execFile } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { access, chmod, mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { performance } from 'node:perf_hooks';
import { promisify } from 'node:util';
import { ulid } from 'ulid';

const [hostBin, cliBin, outPath, repeatsArg = '3'] = process.argv.slice(2);
const repeats = Number(repeatsArg);
if (!hostBin || !cliBin || !outPath || !Number.isInteger(repeats) || repeats < 1) {
  throw new Error('usage: qualify.mjs <ts-sync-revision-host> <tursodb> <out.json> [repeats >= 1]');
}
const DB = 'orders';
const PAD = 'x'.repeat(512);
const BULK_PAD = 'y'.repeat(1024);
const WAL_FRAME_BYTES = 4096 + 24;
const CHECKPOINT_FRAMES = 1024;
const RETAINED_GENERATIONS = 8;
const HORIZON_GENERATIONS = 64 + 8;
const hex = () => randomBytes(24).toString('hex');
const run = promisify(execFile);
const same = (x, y) => !!x && !!y && x.origin === y.origin && x.v === y.v;
const pick = (items, n) => [...items].sort(() => Math.random() - 0.5).slice(0, n);

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

  async raw(sqls) {
    const r = await fetch(`${this.base}/db/${DB}/v2/pipeline`, {
      method: 'POST',
      headers: { authorization: `Bearer ${this.token}`, 'content-type': 'application/json' },
      body: JSON.stringify({ requests: sqls.map((sql) => ({ type: 'execute', stmt: { sql } })) }),
      signal: AbortSignal.timeout(60000),
    });
    const text = await r.text();
    return { status: r.status, text, body: r.headers.get('content-type')?.includes('json') ? JSON.parse(text) : null };
  }

  async pipeline(sqls) {
    const { status, text, body } = await this.raw(sqls);
    if (status !== 200) throw new Error(`pipeline HTTP ${status}: ${text}`);
    const failed = body.results.find((x) => x.type === 'error');
    if (failed) throw new Error(`pipeline error: ${JSON.stringify(failed.error)}`);
    return body.results.map((x) => x.response.result);
  }

  async table() {
    const [result] = await this.pipeline(['SELECT id, origin, v FROM t ORDER BY id']);
    return new Map(result.rows.map((row) => [row[0].value, { origin: row[1].value, v: row[2].value }]));
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
    this.pending = new Map();
    this.own = new Set();
    this.offline = false;
    this.requests = 0;
    this.pulls = [];
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

  async table() {
    const rows = await (await this.db.prepare('SELECT id, origin, v FROM t ORDER BY id')).all();
    return new Map(rows.map((r) => [r.id, { origin: r.origin, v: r.v }]));
  }

  async integrity() {
    const rows = await (await this.db.prepare('PRAGMA integrity_check')).all();
    return rows.map((r) => Object.values(r)[0]).join(',');
  }

  async offlineChange({ inserts = 0, updates = 0, deletes = 0 }) {
    this.offline = true;
    const before = this.requests;
    try {
      for (let i = 0; i < inserts; i++) {
        const id = ulid();
        const row = { origin: this.name, v: `${id}|0|${PAD}` };
        await this.db.exec(`INSERT INTO t(id, origin, v) VALUES ('${id}', '${row.origin}', '${row.v}')`);
        this.pending.set(id, row);
      }
      const candidates = [...this.own].filter((id) => !this.pending.has(id));
      const chosen = pick(candidates, updates + deletes);
      for (const id of chosen.slice(0, updates)) {
        const row = { origin: this.name, v: `${id}|${ulid()}|${PAD}` };
        await this.db.exec(`UPDATE t SET v = '${row.v}' WHERE id = '${id}'`);
        this.pending.set(id, row);
      }
      for (const id of chosen.slice(updates)) {
        await this.db.exec(`DELETE FROM t WHERE id = '${id}'`);
        this.pending.set(id, null);
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
    this.truth = new Map();
    this.backups = new Map();
    this.rolledBack = 0;
    this.totals = {
      lost_acked_writes: 0,
      lost_local_writes: 0,
      skipped_commits: 0,
      phantom_rows: 0,
      stale_false_pulls: 0,
      unexpected_server_rows: 0,
      plan_mismatches: 0,
      sandbox_failures: 0,
    };
    this.pulls = [];
    this.phases = [];
    this.notes = [];
    this.peak = { max_wal_bytes: 0, max_sealed_generations: 0, max_horizon_buckets: 0, max_manifest_bytes: 0, max_item_frames: 0 };
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

  async serverChange({ inserts = 0, updates = 0, deletes = 0, origin = 's', pad = PAD, perRequest = 25 }) {
    const inserted = [];
    for (let done = 0; done < inserts; done += perRequest) {
      const rows = [];
      for (let i = done; i < Math.min(inserts, done + perRequest); i++) {
        const id = ulid();
        rows.push([id, { origin, v: `${id}|0|${pad}` }]);
      }
      await this.remote.pipeline(rows.map(([id, r]) => `INSERT INTO t(id, origin, v) VALUES ('${id}', '${r.origin}', '${r.v}')`));
      for (const [id, r] of rows) {
        this.truth.set(id, r);
        inserted.push(id);
      }
    }
    const serverRows = [...this.truth].filter(([, r]) => r.origin === 's').map(([id]) => id);
    const chosen = pick(serverRows, updates + deletes);
    if (updates > 0) {
      const changed = chosen.slice(0, updates).map((id) => [id, { origin: 's', v: `${id}|${ulid()}|${PAD}` }]);
      await this.remote.pipeline(changed.map(([id, r]) => `UPDATE t SET v = '${r.v}' WHERE id = '${id}'`));
      for (const [id, r] of changed) this.truth.set(id, r);
    }
    if (deletes > 0) {
      const gone = chosen.slice(updates);
      await this.remote.pipeline(gone.map((id) => `DELETE FROM t WHERE id = '${id}'`));
      for (const id of gone) this.truth.delete(id);
    }
    return inserted;
  }

  churn() {
    return this.serverChange({ inserts: 10, updates: 3, deletes: 1 });
  }

  expectedFor(c) {
    const view = new Map(this.truth);
    for (const [id, row] of c.pending) {
      if (row === null) view.delete(id);
      else view.set(id, row);
    }
    return view;
  }

  async pull(c, expect) {
    const expected = this.expectedFor(c);
    const start = c.pulls.length;
    const t0 = performance.now();
    const changed = await c.db.pull();
    const ms = performance.now() - t0;
    const kinds = c.pulls.slice(start).map((p) => p.kind);
    const actual = await c.table();
    let skipped = 0;
    let lostLocal = 0;
    let phantom = 0;
    for (const [id, row] of expected) {
      if (same(actual.get(id), row)) continue;
      if (c.pending.has(id)) lostLocal++;
      else skipped++;
    }
    for (const id of actual.keys()) {
      if (expected.has(id)) continue;
      if (c.pending.get(id) === null) lostLocal++;
      else phantom++;
    }
    const planOk = !expect || (kinds[0] ?? '').startsWith(expect);
    this.totals.skipped_commits += skipped;
    this.totals.lost_local_writes += lostLocal;
    this.totals.phantom_rows += phantom;
    if (!changed && skipped > 0) this.totals.stale_false_pulls++;
    if (!planOk) this.totals.plan_mismatches++;
    this.pulls.push({ phase: this.phase, client: c.name, changed, kinds, expect: expect ?? null, ms: Math.round(ms * 100) / 100, missing: skipped, lost_local: lostLocal, phantom });
  }

  async push(c) {
    await c.db.push();
    for (const [id, row] of c.pending) {
      if (row === null) {
        this.truth.delete(id);
        c.own.delete(id);
      } else {
        this.truth.set(id, row);
        c.own.add(id);
      }
    }
    c.pending.clear();
    await this.verifyServer();
  }

  async verifyServer() {
    const actual = await this.remote.table();
    for (const [id, row] of this.truth) if (!same(actual.get(id), row)) this.totals.lost_acked_writes++;
    for (const id of actual.keys()) if (!this.truth.has(id)) this.totals.unexpected_server_rows++;
  }

  async backup(id) {
    await this.remote.admin(`backup/${id}`);
    this.backups.set(id, new Map(this.truth));
  }

  async restore(id) {
    const snapshot = this.backups.get(id);
    const before = this.truth;
    const state = await this.remote.admin(`restore/${id}`);
    for (const [k, row] of before) if (!same(snapshot.get(k), row)) this.rolledBack++;
    for (const k of snapshot.keys()) if (!before.has(k)) this.rolledBack++;
    this.truth = new Map(snapshot);
    await this.verifyServer();
    return state;
  }

  check(ok, what) {
    if (!ok) {
      this.totals.sandbox_failures++;
      this.notes.push({ phase: this.phase, failed: what });
    }
  }

  async phaseRun(name, fn) {
    this.phase = name;
    const t0 = performance.now();
    await fn();
    const state = await this.remote.admin('state');
    this.observe(state, false);
    this.phases.push({ name, ms: Math.round(performance.now() - t0), generation: state.generation, incarnation: state.incarnation, db_pages: state.db_pages, wal_bytes: state.wal_bytes });
  }

  async sandbox(root) {
    const before = await this.remote.admin('state');
    const denied = (result) => result?.type === 'error' && result.error.code === 'SQL_DENIED';
    const pragma = await this.remote.raw(['PRAGMA wal_checkpoint(TRUNCATE)']);
    this.check(pragma.status === 200 && denied(pragma.body.results[0]), 'checkpoint pragma denied');
    const escape = join(root, 'escape.db');
    const vacuum = await this.remote.raw([`VACUUM INTO '${escape}'`]);
    const escaped = await access(escape).then(() => true, () => false);
    this.check(vacuum.status === 200 && denied(vacuum.body.results[0]) && !escaped, 'VACUUM INTO denied');
    const attach = await this.remote.raw([`ATTACH DATABASE '${join(root, 'other.db')}' AS other`]);
    this.check(attach.status === 200 && denied(attach.body.results[0]), 'ATTACH denied');
    const x = ulid();
    const y = ulid();
    const mixed = await this.remote.raw([
      `INSERT INTO t(id, origin, v) VALUES ('${x}', 's', '${x}|0|${PAD}')`,
      'PRAGMA wal_checkpoint(TRUNCATE)',
      `INSERT INTO t(id, origin, v) VALUES ('${y}', 's', '${y}|0|${PAD}')`,
    ]);
    const kinds = mixed.body?.results.map((r) => r.type).join(',');
    this.check(mixed.status === 200 && kinds === 'ok,error,ok', 'statement around a denied pragma still commits');
    this.truth.set(x, { origin: 's', v: `${x}|0|${PAD}` });
    this.truth.set(y, { origin: 's', v: `${y}|0|${PAD}` });
    const z = ulid();
    const open = await this.remote.raw(['BEGIN', `INSERT INTO t(id, origin, v) VALUES ('${z}', 's', '${z}|0|${PAD}')`]);
    this.check(open.status === 400 && open.text.includes('transaction_left_open'), 'open transaction rejected');
    await this.serverChange({ inserts: 1 });
    const after = await this.remote.admin('state');
    this.check(after.generation === before.generation && after.incarnation === before.incarnation, 'no generation change from denied SQL');
    this.check(after.stats.open_transactions_rolled_back >= 1 && after.stats.denied_statements >= 4, 'host counted denials');
    await this.verifyServer();
  }

  async bootstrapExpiry(d) {
    d.onFirstPull = async () => {
      await this.churn();
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
    const all = [a, b, c, d, e];
    const restartHost = async (signal) => {
      this.observe(await this.remote.admin('state'), true);
      const stopped = await host.stop(signal);
      this.notes.push({ phase: this.phase, stopped });
      if (signal === 'SIGTERM' && stopped.code !== 0) throw new Error(`graceful stop exited ${JSON.stringify(stopped)}: ${host.stderr}`);
    };
    try {
      await this.phaseRun('seed-and-bootstrap', async () => {
        await this.remote.pipeline(['CREATE TABLE t(id TEXT PRIMARY KEY, origin TEXT NOT NULL, v TEXT NOT NULL)']);
        await this.serverChange({ inserts: 200 });
        await a.open();
        await b.open();
        await this.pull(a);
        await this.pull(b);
        await a.offlineChange({ inserts: 6 });
        await this.push(a);
      });
      await this.phaseRun('coordinated-checkpoint', async () => {
        await a.offlineChange({ inserts: 5, updates: 1, deletes: 1 });
        await b.offlineChange({ inserts: 5 });
        await this.churn();
        await this.remote.admin('checkpoint');
        await this.churn();
        await this.pull(a, 'incremental');
        await this.push(a);
      });
      await this.phaseRun('sql-sandbox', async () => {
        await a.offlineChange({ inserts: 3, updates: 1 });
        await this.sandbox(root);
        await this.pull(a, 'incremental');
        await this.push(a);
      });
      await this.phaseRun('chunked-bootstrap-after-checkpoints', async () => {
        await this.remote.admin('checkpoint');
        await c.open();
        await this.pull(c);
        this.cBootstrapKinds = c.pulls.map((p) => p.kind);
        await c.offlineChange({ inserts: 4 });
        await this.push(c);
      });
      await this.phaseRun('chunked-bootstrap-revision-expires', async () => {
        await this.bootstrapExpiry(d);
      });
      await this.phaseRun('retained-history-window', async () => {
        await c.offlineChange({ inserts: 4, updates: 1, deletes: 1 });
        for (let i = 1; i <= 10; i++) {
          await this.churn();
          await this.remote.admin('checkpoint');
          await this.pull(a, 'incremental');
          if (i === 3) await this.pull(c, 'incremental');
        }
        await this.pull(c, 'incremental');
        await this.push(c);
        await this.pull(b, 'incremental');
        await this.push(b);
      });
      await this.phaseRun('history-horizon-expires', async () => {
        await e.open();
        await e.offlineChange({ inserts: 3 });
        for (let i = 1; i <= HORIZON_GENERATIONS + RETAINED_GENERATIONS + 2; i++) {
          await this.serverChange({ inserts: 2, updates: 1 });
          await this.remote.admin('checkpoint');
          if (i % RETAINED_GENERATIONS === 0) {
            await this.pull(a, 'incremental');
            await this.pull(c, 'incremental');
          }
        }
        await this.pull(a, 'incremental');
        await this.pull(e, 'full;expired');
        await this.push(e);
      });
      await this.phaseRun('automatic-checkpoint-policy', async () => {
        await a.offlineChange({ inserts: 5, updates: 2, deletes: 1 });
        const before = (await this.remote.admin('state')).stats.checkpoints;
        this.bulk = await this.serverChange({ inserts: 8000, origin: 'bulk', pad: BULK_PAD, perRequest: 50 });
        const after = (await this.remote.admin('state')).stats.checkpoints;
        this.autoCheckpoints = after - before;
        if (this.autoCheckpoints < 2) this.totals.plan_mismatches++;
        await this.pull(a, 'incremental');
        await this.push(a);
      });
      await this.phaseRun('graceful-restart', async () => {
        await a.offlineChange({ inserts: 3, updates: 1, deletes: 1 });
        await c.offlineChange({ inserts: 3, updates: 1 });
        await this.churn();
        await restartHost('SIGTERM');
        await host.start();
        await this.churn();
        await this.pull(a, 'incremental');
        await this.push(a);
        await this.pull(c, 'incremental');
        await this.push(c);
      });
      await this.phaseRun('crash-restart', async () => {
        await a.offlineChange({ inserts: 3, updates: 1, deletes: 1 });
        await this.churn();
        await restartHost('SIGKILL');
        await host.start();
        await this.churn();
        await this.pull(a, 'incremental');
        await this.push(a);
      });
      await this.phaseRun('external-checkpoint-while-down', async () => {
        await a.offlineChange({ inserts: 3, updates: 1 });
        await this.churn();
        await restartHost('SIGKILL');
        const ext = await run(cliBin, [join(root, 'data', DB, 'data'), 'PRAGMA wal_checkpoint(TRUNCATE)']);
        this.notes.push({ phase: this.phase, external_checkpoint: ext.stdout.trim() });
        await host.start();
        await this.churn();
        await this.pull(a, 'full;rewritten');
        await this.push(a);
      });
      await this.phaseRun('external-change-after-clean-stop', async () => {
        await a.offlineChange({ inserts: 2, updates: 1 });
        await restartHost('SIGTERM');
        const id = ulid();
        const row = { origin: 's', v: `${id}|0|${PAD}` };
        await run(cliBin, [join(root, 'data', DB, 'data'), `INSERT INTO t(id, origin, v) VALUES ('${id}', '${row.origin}', '${row.v}')`]);
        await run(cliBin, [join(root, 'data', DB, 'data'), 'PRAGMA wal_checkpoint(TRUNCATE)']);
        this.truth.set(id, row);
        await host.start();
        await this.pull(a, 'full;rewritten');
        await this.push(a);
      });
      await this.phaseRun('manifest-write-failure-fences', async () => {
        await a.offlineChange({ inserts: 2, updates: 1 });
        await this.churn();
        const tenantDir = join(root, 'data', DB);
        await chmod(tenantDir, 0o555);
        let checkpointFailed = false;
        try {
          await this.remote.admin('checkpoint');
        } catch {
          checkpointFailed = true;
        }
        const write = await this.remote.raw([`INSERT INTO t(id, origin, v) VALUES ('${ulid()}', 's', 'never')`]);
        let pullFailed = false;
        try {
          await a.db.pull();
        } catch {
          pullFailed = true;
        }
        await chmod(tenantDir, 0o755);
        this.check(checkpointFailed && write.status === 500 && pullFailed, 'failed manifest write fences checkpoint, writes and pulls');
        this.notes.push({ phase: this.phase, checkpoint_failed: checkpointFailed, write_status: write.status, pull_failed: pullFailed });
        await host.stop('SIGKILL');
        await host.start();
        await this.churn();
        await this.pull(a, 'incremental');
        await this.push(a);
      });
      await this.phaseRun('rewrite-vacuum-swap', async () => {
        for (let i = 0; i < this.bulk.length; i += 200) {
          const ids = this.bulk.slice(i, i + 200);
          await this.remote.pipeline([`DELETE FROM t WHERE id IN (${ids.map((x) => `'${x}'`).join(',')})`]);
          for (const x of ids) this.truth.delete(x);
        }
        await a.offlineChange({ inserts: 3, updates: 1, deletes: 1 });
        const before = await this.remote.admin('state');
        const after = await this.remote.admin('rewrite');
        this.rewrite = { pages_before: before.db_pages, pages_after: after.db_pages };
        await this.churn();
        await this.pull(a, 'full;rewritten');
        await this.push(a);
        await this.pull(c, 'full;rewritten');
      });
      await this.phaseRun('restore-backup-new-incarnation', async () => {
        await this.backup('b1');
        await a.offlineChange({ inserts: 3, updates: 1, deletes: 1 });
        const before = await this.remote.admin('state');
        const after = await this.restore('b1');
        if (before.incarnation === after.incarnation) this.totals.plan_mismatches++;
        await this.churn();
        await this.pull(a, 'full;other-incarnation');
        await this.push(a);
        await this.pull(c, 'full;other-incarnation');
        await this.push(c);
      });
      await this.phaseRun('restore-rolls-back-unbacked-writes', async () => {
        await this.backup('b2');
        await this.serverChange({ inserts: 20, updates: 5, deletes: 2 });
        await this.pull(a, 'incremental');
        await a.offlineChange({ inserts: 3, updates: 1 });
        await this.restore('b2');
        await this.pull(a, 'full;other-incarnation');
        await this.push(a);
      });
      await this.phaseRun('converge', async () => {
        for (const cl of all) {
          await this.pull(cl);
          await this.push(cl);
        }
        const server = await this.remote.table();
        const serverJson = JSON.stringify([...server]);
        if (serverJson !== JSON.stringify([...this.truth].sort(([x], [y]) => (x < y ? -1 : x > y ? 1 : 0)))) this.totals.unexpected_server_rows++;
        this.converged = { server_rows: server.size, clients: {} };
        for (const cl of all) {
          await this.pull(cl);
          const equal = JSON.stringify([...(await cl.table())]) === serverJson;
          if (!equal) this.totals.skipped_commits++;
          this.converged.clients[cl.name] = { rows: (await cl.table()).size, equal_to_server: equal, integrity: await cl.integrity() };
        }
        this.converged.server_integrity = (await this.remote.admin('state')).integrity;
      });
      this.state = await this.remote.admin('state');
      this.observe(this.state, true);
      for (const cl of all) await cl.db.close();
      const stopped = await host.stop('SIGTERM');
      this.notes.push({ phase: 'end', stopped });
    } catch (err) {
      this.error = `${this.phase}: ${err?.stack ?? err}\n${host.stderr ?? ''}`;
      if (host.proc && host.proc.exitCode === null && host.proc.signalCode === null) await host.stop('SIGKILL');
    }
    await rm(root, { recursive: true, force: true });
    const s = this.state ? this.peak : null;
    const walBound = s ? 32 + (CHECKPOINT_FRAMES + s.max_item_frames) * WAL_FRAME_BYTES : null;
    const bounded = !!s && s.max_sealed_generations <= RETAINED_GENERATIONS && s.max_horizon_buckets <= 8 && s.max_wal_bytes <= walBound;
    const clientsOk = !!this.converged && Object.values(this.converged.clients).every((x) => x.equal_to_server && x.integrity === 'ok') && this.converged.server_integrity === 'ok';
    const zero = Object.values(this.totals).every((v) => v === 0);
    return {
      repeat: this.index,
      pass: !this.error && zero && bounded && clientsOk,
      error: this.error ?? null,
      totals: this.totals,
      bounded: {
        pass: bounded,
        wal_bound_bytes: walBound,
        max_wal_bytes: s?.max_wal_bytes,
        max_item_frames: s?.max_item_frames,
        max_sealed_generations: s?.max_sealed_generations,
        max_horizon_buckets: s?.max_horizon_buckets,
        max_manifest_bytes: s?.max_manifest_bytes,
      },
      rolled_back_by_restore: this.rolledBack,
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
    for (let i = 0; i < 100; i++) {
      const id = ulid();
      await remote.pipeline([`INSERT INTO t(id, origin, v) VALUES ('${id}', 's', '${id}|0|${PAD}')`]);
    }
    const a = new Client('a', root, `http://127.0.0.1:${port}/db/${DB}`, 'unused');
    await a.open();
    const before = (await a.table()).size;
    await remote.pipeline(['PRAGMA wal_checkpoint(TRUNCATE)']);
    const id = ulid();
    await remote.pipeline([`INSERT INTO t(id, origin, v) VALUES ('${id}', 's', '${id}|0|${PAD}')`]);
    const changed = await a.db.pull();
    const after = (await a.table()).size;
    await a.db.close();
    Object.assign(result, { rows_before: before, pull_returned: changed, rows_after: after, server_rows: (await remote.table()).size });
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
const save = (pass) => writeFile(outPath, JSON.stringify({ node: process.version, repeats, pass, durable, controls }, null, 2));
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
const pass =
  durable.length === repeats &&
  controls.length === repeats &&
  durable.every((r) => r.pass) &&
  controls.every((r) => r.bug_reproduced);
await save(pass);
console.log(JSON.stringify({ pass }));
process.exit(pass ? 0 : 1);
