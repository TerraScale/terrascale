import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, rmSync, mkdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect as baseExpect } from '@playwright/test';

const expect = baseExpect.configure({ timeout: 20000 });
import { token, transport } from './transports.mjs';

export async function runManagement(state, target, executable) {
  const credential = { apiKey: state.apiKey, token: await token(state.apiKey, state.projectId) };
  const ports = target === 'local' ? JSON.parse(readFileSync(resolve(process.env.E2E_STATE, 'ports.json'), 'utf8')) : undefined;
  if (target === 'local') {
    const directory = resolve(process.env.E2E_STATE, 'secrets.d');
    mkdirSync(directory, { recursive: true, mode: 0o700 });
    writeFileSync(resolve(directory, 'c35-management-' + state.databaseId), credential.token, { mode: 0o600 });
  }
  const root = '/v1/projects/' + state.projectId + '/databases/';
  const origin = region => 'https://' + region + '.api.terrascale.tech';
  const source = state.databaseId;
  const copies = [];
  const results = [];
  const check = async (name, action, region = home, surface = 'Management') => {
    try {
      await action();
      results.push({ target, region, surface, name, status: 'PASS' });
      process.stdout.write(JSON.stringify(results.at(-1)) + '\n');
      return true;
    } catch (error) {
      const status = error.ddl && (error.code === 'serialization_conflict' || error.sqlstate === '40001') ? 'C34' : 'FAIL';
      results.push({ target, region, surface, name, status, detail: (error.code ?? error.name) + ': ' + error.message.slice(0, 300) });
      process.stdout.write(JSON.stringify(results.at(-1)) + '\n');
      return false;
    }
  };
  const request = async (region, id, suffix, method = 'GET', body) => {
    const response = await fetch(origin(region) + root + id + suffix, {
      method, headers: { authorization: 'Bearer ' + credential.token, 'content-type': 'application/json' },
      ...(body && { body: JSON.stringify(body) }), signal: AbortSignal.timeout(15000), redirect: 'error',
    });
    const value = response.status === 204 ? null : await response.json();
    if (!response.ok) throw Object.assign(new Error('management HTTP ' + response.status + ': ' + (value?.error?.message ?? 'no error detail')), { code: value?.error?.code, status: response.status });
    return value;
  };
  const metadata = await request('gru', source, '');
  const home = metadata.homeRegion;
  assert((target === 'local' ? ['gru', 'fra'] : ['gru', 'sjc', 'fra']).includes(home));
  const client = await transport('REST', home, state.projectId, source, credential, ports);
  const sql = async (text, database = source, region = home) => {
    const reader = database === source && region === home ? client : await transport('REST', region, state.projectId, database, credential, ports);
    try { return (await reader.execute([{ sql: text }]))[0].rows; }
    finally { if (reader !== client) await reader.close(); }
  };
  const completed = async operation => {
    let id;
    await expect.poll(async () => {
      const current = await request(home, source, '/restores/' + operation.id);
      assert.notEqual(current.status, 'failed', current.errorCode);
      if (current.status === 'completed') id = current.database;
      return current.status;
    }).toBe('completed');
    copies.push(id);
    return id;
  };
  const matching = async (database, value) => {
    assert.deepEqual(await sql('SELECT value FROM c35_recovery_view ORDER BY value', database), [[value]]);
    assert.deepEqual(await sql("SELECT type,name FROM sqlite_schema WHERE name IN ('c35_recovery_index','c35_recovery_trigger') ORDER BY name", database),
      [['index', 'c35_recovery_index'], ['trigger', 'c35_recovery_trigger']]);
  };
  try {
    const prepared = await check('recovery-source', async () => {
      await sql('CREATE TABLE c35_recovery(id INTEGER PRIMARY KEY,value TEXT NOT NULL)');
      await sql('CREATE TABLE c35_audit(value TEXT)');
      await sql('CREATE INDEX c35_recovery_index ON c35_recovery(value)');
      await sql('CREATE VIEW c35_recovery_view AS SELECT value FROM c35_recovery');
      await sql("CREATE TRIGGER c35_recovery_trigger AFTER UPDATE ON c35_recovery BEGIN INSERT INTO c35_audit VALUES(CASE WHEN new.value IS NOT NULL THEN new.value ELSE '' END); END");
      await sql("INSERT INTO c35_recovery VALUES(1,'before')");
    });
    if (!prepared) return results;
    await check('fork-and-ttl', async () => {
      const copy = await request(home, source, '/branches', 'POST', { name: 'c35-fork-' + source.toLowerCase(), ttlSeconds: 3600 });
      copies.push(copy.id);
      assert.equal(copy.branchedFrom, source);
      assert(Number.isFinite(Date.parse(copy.expiresAt)));
      await matching(copy.id, 'before');
      const updated = await request(home, copy.id, '/ttl', 'PUT', { ttlSeconds: 7200 });
      assert(Date.parse(updated.expiresAt) > Date.parse(copy.expiresAt));
      const cleared = await request(home, copy.id, '/ttl', 'DELETE');
      assert.equal(cleared.expiresAt, null);
      const renamed = await request(home, copy.id, '', 'PATCH', { name: 'c35-renamed-' + copy.id.toLowerCase() });
      assert.equal(renamed.id, copy.id);
      assert.equal(renamed.name, 'c35-renamed-' + copy.id.toLowerCase());
      await sql("UPDATE c35_recovery SET value='fork'", copy.id);
      await matching(source, 'before');
      assert.deepEqual(await sql('SELECT value FROM c35_audit', copy.id), [['fork']]);
    });
    await check('operator-export-import-round-trip', async () => {
      const directory = mkdtempSync(resolve(tmpdir(), 'c35-import-'));
      const file = resolve(directory, 'database.sql');
      const cli = args => {
        const result = spawnSync(executable, ['-project', state.projectId, ...args], {
          encoding: 'utf8', env: { ...process.env, TERRASCALE_API_KEY: state.apiKey }, timeout: 30000,
        });
        assert.equal(result.status, 0, 'operator ' + args.at(-3) + ' failed: ' + (result.stderr ?? '').slice(0, 300));
      };
      try {
        cli(['-o', file, 'db', 'export', source]);
        const copy = await request(home, '', '', 'POST', { name: 'c35-import-' + source.toLowerCase(), regions: target === 'local' ? ['gru', 'fra'] : ['gru', 'sjc', 'fra'] });
        copies.push(copy.id);
        cli(['db', 'import', copy.id, file]);
        await matching(copy.id, 'before');
        await sql("UPDATE c35_recovery SET value='imported'", copy.id);
        assert.deepEqual(await sql('SELECT value FROM c35_audit', copy.id), [['imported']]);
      } finally { rmSync(directory, { recursive: true }); }
    }, home, 'Operator');
    let backup;
    await check('snapshot-create-list-restore', async () => {
      backup = await request(home, source, '/backups', 'POST');
      assert.equal(backup.status, 'ready');
      const listed = await request(home, source, '/backups');
      assert(listed.items.some(item => item.id === backup.id));
      const operation = await request(home, source, '/backups/' + backup.id + '/restore', 'POST', { name: 'c35-snapshot-' + source.toLowerCase() });
      await matching(await completed(operation), 'before');
    });
    let at;
    await check('pitr-window-and-plan', async () => {
      const acknowledged = Date.now();
      await expect.poll(async () => {
        try {
          const window = await request(home, source, '/point-in-time');
          at = window.latest;
          return Date.parse(at);
        } catch (error) {
          if (error.status === 404) return 0;
          throw error;
        }
      }).toBeGreaterThanOrEqual(acknowledged);
      const plan = await request(home, source, '/point-in-time/plan', 'POST', { pointInTime: at });
      assert.equal(Date.parse(plan.pointInTime), Date.parse(at));
      assert.equal(plan.historyCheck, 'metadata');
      assert(Date.parse(plan.snapshotAt) <= Date.parse(at));
    });
    if (at) {
      await check('pitr-restore-and-rewind', async () => {
        await sql("UPDATE c35_recovery SET value='later'");
        const operation = await request(home, source, '/point-in-time/restore', 'POST', { pointInTime: at, name: 'c35-pitr-' + source.toLowerCase() });
        await matching(await completed(operation), 'before');
        await matching(source, 'later');
        const shapeUrl = origin(home) + root + source + '/tables/c35_recovery/shape';
        const snapshot = await fetch(shapeUrl + '?offset=-1&where=id%20%3D%201&columns=id,value', {
          headers: { authorization: 'Bearer ' + credential.token }, signal: AbortSignal.timeout(15000),
        });
        assert.equal(snapshot.status, 200);
        const oldHandle = snapshot.headers.get('tb-shape-handle');
        assert(oldHandle);
        await snapshot.arrayBuffer();
        const rewound = await request(home, source, '/point-in-time/rewind', 'POST', { pointInTime: at });
        assert.equal(rewound.id, source);
        const stale = await fetch(shapeUrl + '?offset=0&handle=' + oldHandle + '&where=id%20%3D%201&columns=id,value', {
          headers: { authorization: 'Bearer ' + credential.token }, signal: AbortSignal.timeout(15000),
        });
        assert.equal(stale.status, 409);
        assert.equal((await stale.json()).error.code, 'must_refetch');
        assert.notEqual(stale.headers.get('tb-shape-handle'), oldHandle);
        await matching(source, 'before');
        assert.deepEqual(await sql('SELECT value FROM c35_audit'), []);
        await sql("UPDATE c35_recovery SET value='rewound'");
        assert.deepEqual(await sql('SELECT value FROM c35_audit'), [['rewound']]);
      });
    }
    await check('shape-raw-bootstrap', async () => {
      const response = await fetch(origin(home) + root + source + '/tables/c35_recovery/shape?offset=-1&where=id%20%3D%201&columns=id,value', {
        headers: { authorization: 'Bearer ' + credential.token }, signal: AbortSignal.timeout(15000),
      });
      const body = await response.json();
      process.stdout.write(JSON.stringify({ target, surface: 'REST', stage: 'shape-bootstrap', status: response.status,
        handle: body.handle, nextOffset: body.next_offset, error: body.error?.code, message: body.error?.message, changes: body.changes }) + '\n');
      assert.equal(response.status, 200);
      assert(body.changes.flatMap(change => change.ops).some(op => op.row?.value === 'rewound'));
    });
    await check('shape-snapshot-and-live-data', async () => {
      const { createTerraBaseClient } = await import('../../../sdk/js/src/index.js');
      const sdk = createTerraBaseClient({ token: credential.token, project: state.projectId });
      const before = (await sql('SELECT value FROM c35_recovery'))[0][0];
      let wanted = before;
      let received;
      let completedState;
      const next = () => new Promise(resolve => { completedState = resolve; });
      let pending = next();
      const subscription = sdk.database(source).shape('c35_recovery', { where: 'id = 1', columns: ['id', 'value'] }).subscribe(current => {
        process.stdout.write(JSON.stringify({ target, surface: 'SDK', stage: 'shape-data', offset: current.offset,
          upToDate: current.upToDate, values: [...current.rows.values()].map(row => row.value), wanted }) + '\n');
        if (current.upToDate && current.rows.get('1')?.value === wanted) {
          received = [...current.rows.values()];
          completedState();
        }
      }, { signal: AbortSignal.timeout(15000) });
      const wait = () => Promise.race([pending, subscription.done.then(() => { throw new Error('shape ended before expected data'); })]);
      try {
        await wait();
        assert.equal(received.length, 1);
        assert.equal(received[0].value, before);
        wanted = 'shape';
        pending = next();
        await sql("UPDATE c35_recovery SET value='shape'");
        await wait();
        assert.equal(received.length, 1);
        assert.equal(received[0].value, 'shape');
      } finally {
        subscription.close();
        await subscription.done;
      }
    }, home, 'SDK');
    await check('table-shape-grants', async () => {
      const grant = await request(home, source, '/tables/c35_recovery/read-grant', 'PUT', { columns: ['id', 'value'], where: 'id = 1' });
      assert.deepEqual(grant.columns, ['id', 'value']);
      const saved = await request(home, source, '/tables/c35_recovery/read-grant');
      assert.equal(saved.where, 'id = 1');
      await request(home, source, '/tables/c35_recovery/read-grant', 'DELETE');
    });
    for (const region of (target === 'local' ? ['gru', 'fra'] : ['gru', 'sjc', 'fra']).filter(value => value !== home)) {
      await check('cross-region-lag-and-home-read', async () => {
        let progress;
        await expect.poll(async () => {
          progress = await request(home, source, '/regions');
          return typeof progress.replicas.find(item => item.region === region)?.lastPulledAt === 'string';
        }).toBe(true);
        const previous = (await sql('SELECT value FROM c35_recovery'))[0][0];
        const written = 'cross-' + region;
        await sql("UPDATE c35_recovery SET value='" + written + "'");
        const response = await fetch(origin(region) + root + source + '/sql/execute', {
          method: 'POST', headers: { authorization: 'Bearer ' + credential.token, 'content-type': 'application/json' },
          body: JSON.stringify({ statements: [{ sql: 'SELECT value FROM c35_recovery' }] }), signal: AbortSignal.timeout(15000),
        });
        assert.equal(response.status, 200);
        const observed = await response.json();
        assert([previous, written].includes(observed.results[0].rows[0][0]));
        const execution = response.headers.get('x-terrabase-region');
        assert([home, region].includes(execution));
        const route = response.headers.get('terrascale-write-route');
        if (route !== null) assert.match(route, /route=(local|home)/);
        const replica = progress.replicas.find(item => item.region === region);
        assert(replica.lagMs === null || (Number.isFinite(replica.lagMs) && replica.lagMs >= 0));
        const routed = await transport('REST', region, state.projectId, source, credential, ports);
        try {
          const fresh = await routed.execute([{ sql: 'SELECT value FROM c35_recovery' }]);
          assert.deepEqual(fresh[0].rows, [[written]]);
          assert.equal(fresh.region, home);
        } finally { await routed.close(); }
        process.stdout.write(JSON.stringify({ target, region, execution, route, observed: observed.results[0].rows, lagMs: replica.lagMs }) + '\n');
      }, region, 'REST');
      for (const surface of ['WebSocket', 'Postgres']) await check('overlapping-writer-conflict', async () => {
        const table = 'c35_conflict_' + region + '_' + surface.toLowerCase();
        await sql(`CREATE TABLE ${table}(id INTEGER PRIMARY KEY,value TEXT)`);
        await sql(`INSERT INTO ${table} VALUES(1,'initial')`);
        await expect.poll(async () => {
          const response = await fetch(origin(region) + root + source + '/sql/execute', {
            method: 'POST', headers: { authorization: 'Bearer ' + credential.token, 'content-type': 'application/json' },
            body: JSON.stringify({ statements: [{ sql: `SELECT value FROM ${table}` }] }), signal: AbortSignal.timeout(15000),
          });
          const body = await response.json();
          if (body.error?.code === 'undefined_table' || body.error?.code === 'unavailable') return [];
          assert.equal(response.status, 200);
          return body.results[0].rows;
        }).toEqual([['initial']]);
        const stale = await transport(surface, region, state.projectId, source, credential, ports);
        const winner = await transport(surface, home, state.projectId, source, credential, ports);
        try {
          const stage = name => process.stdout.write(JSON.stringify({ target, region, surface, stage: name }) + '\n');
          stage('begin-stale');
          await stale.control('begin');
          await stale.execute([{ sql: `SELECT value FROM ${table}` }]);
          stage('begin-winner');
          await winner.control('begin');
          await winner.execute([{ sql: `UPDATE ${table} SET value='winner'` }]);
          stage('commit-winner');
          await winner.control('commit');
          stage('write-stale');
          await assert.rejects(stale.execute([{ sql: `UPDATE ${table} SET value='stale'` }]), error => {
            assert.equal(error.sqlstate, '40001');
            if (surface === 'WebSocket') assert.equal(error.code, 'serialization_conflict');
            return true;
          });
          await stale.control('rollback');
          assert.deepEqual(await sql(`SELECT value FROM ${table}`), [['winner']]);
        } finally {
          const closed = await Promise.allSettled([stale.close(), winner.close()]);
          const failed = closed.filter(result => result.status === 'rejected').map(result => result.reason);
          if (failed.length) throw new AggregateError(failed, 'SQL connection cleanup failed: ' + failed.map(error => error.message).join('; '));
        }
      }, region, surface);
    }
  } finally {
    for (const id of copies) await check('scratch-copy-cleanup-' + id, () => request(home, id, '', 'DELETE'));
    await client.close();

  }
  process.stdout.write('| Target | Region | Surface | Pass | Fail | C34 |\n| --- | --- | --- | ---: | ---: | ---: |\n');
  for (const region of [...new Set(results.map(cell => cell.region))]) for (const surface of ['Management', 'REST', 'WebSocket', 'Postgres', 'SDK', 'Operator']) {
    const cells = results.filter(cell => cell.region === region && cell.surface === surface);
    if (cells.length) process.stdout.write(`| ${target} | ${region} | ${surface} | ${['PASS', 'FAIL', 'C34'].map(status => cells.filter(cell => cell.status === status).length).join(' | ')} |\n`);
  }
  return results;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const results = await runManagement(JSON.parse(readFileSync(0, 'utf8')), process.argv[2], process.argv[2] === 'local' ? resolve(process.env.E2E_SDK_BUILD, 'go/ts') : process.argv[3] ?? 'ts');
    process.exitCode = results.some(result => result.status === 'FAIL') ? 1 : 0;
  } catch (error) {
    process.stderr.write('management matrix stopped: ' + error.name + '\n');
    process.exitCode = 1;
  }
}
