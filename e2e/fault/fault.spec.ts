import { randomBytes } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { join } from 'node:path';
import { expect } from '@playwright/test';
import { test, answer, database, json, name, project, regions as replication, removeDatabase, type Cells } from '../placement/support';
import { dash, e2eRoot, stateDir } from '../support/stack';
import { journey, ownerSession, saveSecret } from '../support/state';
import { verifyRestoreDrill } from './restore-drill';
import { extendPeers, startRegion } from '../placement/fleet';
import { backupPuts, backupStore, cleanUp, clock, crashWrite, endpoint, faultId, Ledger, metric, node, nodes, nodeRequest, peersIn, peersReady, probe, projectToken, regions, restartAfterKill, systemBackup, tokenReady, type Node, type Row } from './support';

test.beforeEach(async () => {
  expect(process.env.E2E_TARGET ?? 'local').toBe('local');
  expect(process.env.E2E_REGIONS).toBe('2');
  expect(process.env.E2E_NODES_PER_REGION).toBe('2');
  await peersReady();
});

test('a peer killed during a write restarts without losing acknowledged rows on any peer', async ({ cells, request }) => {
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('fault'), regions: [...regions] },
  }), 201);
  const ledger = new Ledger(created.id);
  let failure: unknown;
  try {
    await answer(cells, endpoint('gru'), created.id, ['CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)']);
    await ledger.write(cells, 'gru', [[1, 'before-gru']]);
    await ledger.compare(cells);
    await ledger.write(cells, 'fra', [[2, 'before-fra']]);
    await ledger.compare(cells);
    const retained = [...ledger.acknowledged];
    const token = await projectToken(request);
    const peer = node('journey-1');
    const writing = crashWrite(peer, ledger, token);
    await writing.sent;
    const restart = restartAfterKill(peer, { token, path: database(created.id) });
    try {
      const outcome = await writing.outcome;
      if (outcome.error) throw outcome.error;
    } finally {
      await restart();
    }
    await expect.poll(() => probe(peer, '/readyz'), { message: 'the crashed peer recovers from its existing data directory' }).toBe(200);
    await ledger.compare(cells, retained);
    await ledger.converge(token, nodes, retained);
    await ledger.write(cells, 'gru', [[3, 'after-gru']]);
    await ledger.write(cells, 'fra', [[4, 'after-fra']]);
    await ledger.compare(cells);
    expect(await metric(peer, 'terrabase_backup_disabled')).toBe(0);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      await removeDatabase(cells, created.id);
    });
  }
});

test('each peer reports system backup continuity, and a newly started region does too', async () => {
  for (const peer of nodes) await verifyRestoreDrill({ ...peer, metricsPort: peer.metrics });
  const restorePeers = extendPeers(['sjc']);
  let region: Awaited<ReturnType<typeof startRegion>> | undefined;
  try {
    region = await startRegion('sjc', 0);
    const { hostname, port } = new URL(region.endpoint);
    await verifyRestoreDrill({ name: 'journey-extra-sjc', region: 'sjc', address: hostname, port: Number(port), metricsPort: region.metricsPort }, false);
    expect((await fetch(`${region.endpoint}/readyz`)).status).toBe(200);
  } finally {
    if (region) await region.stop();
    restorePeers();
  }
});

test('backup failures rise while the bucket is unreachable, including across a peer restart', async () => {
  const peer = node('journey-1');
  await expect.poll(async () => {
    const status = await probe(peer, '/backupz');
    return status === 200 || status === 503;
  }, { message: 'backupz answers before the outage' }).toBe(true);
  const before = await metric(peer, 'terrabase_backup_failures_total');
  await backupStore('down');
  let outage = true;
  try {
    await expect.poll(() => metric(peer, 'terrabase_backup_failures_total'), { message: 'archive passes fail while the bucket is unreachable', timeout: 60_000 }).toBeGreaterThan(before);
    expect(await probe(peer, '/readyz'), 'the peer keeps serving while backup is down').toBe(200);
    const restart = restartAfterKill(peer, undefined, async () => {
      await expect.poll(() => probe(peer, '/backupz'), { message: 'a cold peer cannot read the archive while the bucket is down' }).toBe(503);
      const cold = await systemBackup(peer);
      expect(cold.status).toBe(503);
      expect(cold.body).toMatchObject({ status: 'unavailable', region: 'gru' });
      await backupStore('up');
      outage = false;
    });
    await restart();
  } finally {
    if (outage) await backupStore('up');
  }
  await expect.poll(() => probe(peer, '/readyz'), { message: 'the restarted peer is ready' }).toBe(200);
  await expect.poll(() => probe(peer, '/backupz'), { message: 'backupz answers again once the bucket is back' }).toBe(200);
  expect((await clock(peer)).machine).toBe(peer.name);
});

test('a bookmark restore copies the snapshot ledger into every region and leaves later writes behind', async ({ cells }) => {
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('backup-ledger'), regions: [...regions] },
  }), 201);
  const ledger = new Ledger(created.id);
  const copies: string[] = [];
  let failure: unknown;
  try {
    await answer(cells, endpoint('gru'), created.id, ['CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)']);
    ledger.record('submitted', [[1, 'snapshot-gru']], 'gru');
    const snapshot = await answer(cells, endpoint('gru'), created.id, ["INSERT INTO fault_rows (id, value) VALUES (1, 'snapshot-gru')"]);
    ledger.record('acknowledged', [[1, 'snapshot-gru']], 'gru');
    await ledger.compare(cells);
    ledger.record('submitted', [[2, 'snapshot-fra']], 'fra');
    const fraWrite = await answer(cells, endpoint('fra'), created.id, ["INSERT INTO fault_rows (id, value) VALUES (2, 'snapshot-fra')"]);
    ledger.record('acknowledged', [[2, 'snapshot-fra']], 'fra');
    await ledger.compare(cells);
    const archived = await json<{ databaseId: string; archivedThrough: string; pendingSegments: number }>(await cells.post(`${endpoint('gru')}${database(created.id)}/backups`));
    expect(archived.databaseId).toBe(created.id);
    expect(archived.archivedThrough).toEqual(expect.any(String));
    await ledger.write(cells, 'gru', [[3, 'after-snapshot']]);
    const operation = await json<{ operationId: string; state: string; sourceDatabaseId: string }>(await cells.post(`${endpoint('gru')}${database(created.id)}/point-in-time/restore`, {
      data: { bookmark: snapshot.bookmark, name: name('restored-ledger') },
    }), 202);
    expect(operation).toMatchObject({ sourceDatabaseId: created.id });
    copies.push(await completed(cells, created.id, operation.operationId));
    for (const region of regions) {
      await expect.poll(async () => (await answer(cells, endpoint(region), copies[0], ['SELECT id, value FROM fault_rows ORDER BY id'])).results[0].rows, {
        message: `the restored ${region} copy matches the bookmark`, timeout: 45_000, intervals: [250, 500, 1000],
      }).toEqual([[1, 'snapshot-gru']]);
    }
    expect((await answer(cells, endpoint('gru'), created.id, ['SELECT id, value FROM fault_rows ORDER BY id'])).results[0].rows).toEqual([[1, 'snapshot-gru'], [2, 'snapshot-fra'], [3, 'after-snapshot']]);
    expect(fraWrite.bookmark).not.toBe(snapshot.bookmark);
    const succeeded = await metric(node('journey-1'), 'terrabase_backup_operations_total', 'state="succeeded"')
      + await metric(node('journey-3'), 'terrabase_backup_operations_total', 'state="succeeded"');
    expect(succeeded).toBeGreaterThan(0);
    console.log(`bookmark ${snapshot.bookmark}: restored snapshot ledger in both regions; later source writes excluded`);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      for (const copy of copies) await removeDatabase(cells, copy);
      await removeDatabase(cells, created.id);
    });
  }
});

async function createLedgers(cells: Cells, count: number, label: string): Promise<Ledger[]> {
  const ledgers: Ledger[] = [];
  for (let index = 0; index < count; index++) {
    const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
      data: { name: name(`${label}-${index}`), regions: [...regions] },
    }), 201);
    const ledger = new Ledger(created.id);
    ledgers.push(ledger);
    await answer(cells, endpoint('gru'), created.id, ['CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)']);
    await ledger.write(cells, 'gru', Array.from({ length: 20 }, (_, row): Row => [row + 1, `base-${row + 1}`]));
  }
  for (const ledger of ledgers) await ledger.compare(cells);
  return ledgers;
}

async function holdOutage(ledgers: Ledger[], token: string): Promise<void> {
  for (const [index, ledger] of ledgers.entries()) {
    const base = Array.from({ length: 20 }, (_, row): Row => [row + 1, `base-${row + 1}`]);
    for (const peer of [node('journey-3'), node('journey-2'), node('journey-4')]) {
      const rows = await ledger.readNode(peer, token);
      for (const row of base) expect(rows, `${peer.name} keeps serving reads while journey-1 is down`).toContainEqual(row);
    }
    const id = 2000 + index;
    expect(await ledger.direct(node('journey-3'), token, [[id, `gru-peer-${id}`]]), 'the other gru peer accepts writes').toBe(true);
    expect(await ledger.direct(node('journey-2'), token, [[id + 100, `fra-peer-${id}`]]), 'fra accepts writes while one gru peer is down').toBe(true);
  }
}

async function killDuringBurst(ledgers: Ledger[], token: string): Promise<() => Promise<void>> {
  const peer = node('journey-1');
  const ids = Array.from({ length: 240 }, (_, index) => 1000 + index);
  let next = 0;
  let acknowledged = 0;
  let restart: (() => Promise<void>) | undefined;
  try {
    await Promise.all(Array.from({ length: 12 }, async () => {
      while (next < ids.length) {
        const id = ids[next++];
        const ledger = ledgers[id % ledgers.length];
        if (await ledger.direct(peer, token, [[id, `burst-${id}`]]) && ++acknowledged === 45 && !restart) {
          restart = restartAfterKill(peer, { token, path: database(ledgers[0].id) });
        }
      }
    }));
  } catch (error) {
    await cleanUp(error, async () => { await restart?.(); });
    throw error;
  }
  expect(restart, 'journey-1 was killed during the burst').toBeDefined();
  return restart!;
}

test('a gru peer that stays down loses no acknowledged row: the other peers keep serving, and every row converges after the restart', async ({ cells, request }) => {
  test.setTimeout(300_000);
  const token = await projectToken(request);
  const ledgers = await createLedgers(cells, 3, 'node-outage');
  for (const ledger of ledgers) await ledger.converge(token, nodes);
  const restart = await killDuringBurst(ledgers, token);
  console.log(`journey-1 killed after ${ledgers.reduce((sum, ledger) => sum + ledger.acknowledged.length, 0)} acknowledged rows`);
  try {
    await holdOutage(ledgers, token);
  } finally {
    await restart();
  }
  for (const ledger of ledgers) {
    await ledger.converge(token, nodes, [...ledger.acknowledged]);
    await ledger.drop(token, node('journey-1'));
  }
});

test('a gru peer killed after the row is on the other peers serves that row again after its restart', async ({ cells, request }) => {
  test.setTimeout(180_000);
  const ledgers = await createLedgers(cells, 2, 'node-replicated');
  const token = await projectToken(request);
  for (const ledger of ledgers) {
    await ledger.write(cells, 'gru', [[500, 'last-before-kill']]);
    await ledger.converge(token, nodes);
  }
  const restart = restartAfterKill(node('journey-1'), { token, path: database(ledgers[0].id) });
  try {
    await holdOutage(ledgers, token);
    for (const ledger of ledgers) {
      for (const peer of [node('journey-3'), node('journey-2')]) {
        expect(await ledger.readNode(peer, token), `${peer.name} serves the replicated row while journey-1 is down`).toContainEqual([500, 'last-before-kill']);
      }
    }
  } finally {
    await restart();
  }
  for (const ledger of ledgers) {
    await ledger.converge(token, nodes);
    expect(await ledger.readNode(node('journey-1'), token)).toContainEqual([500, 'last-before-kill']);
    await ledger.drop(token, node('journey-1'));
  }
});

test('an idle archive reaches acknowledged writes without another write, including after a peer restart', async ({ cells }) => {
  const peer = node('journey-1');
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('idle-backup'), regions: ['gru'] },
  }), 201);
  const ledger = new Ledger(created.id);
  const copies: string[] = [];
  const latest = async () => {
    const window = await cells.get(`${endpoint('gru')}${database(created.id)}/point-in-time`);
    return window.status() === 200 ? Date.parse((await window.json() as { latestRestorePointUtc: string }).latestRestorePointUtc) : 0;
  };
  const recover = async (acked: number) => {
    await json(await cells.post(`${endpoint('gru')}${database(created.id)}/backups`));
    await expect.poll(latest, { message: 'the idle archive proves every acknowledged write without a later source write', timeout: 60_000 }).toBeGreaterThanOrEqual(acked);
    const at = await latest();
    const plan = await json<{ feasible: boolean; latestRestorePointUtc: string }>(await cells.post(`${endpoint('gru')}${database(created.id)}/point-in-time/plan`, {
      data: { pointInTime: new Date(at).toISOString() },
    }));
    expect(plan.feasible, 'the stored archive also proves the acknowledged write').toBe(true);
    expect(Date.parse(plan.latestRestorePointUtc)).toBeGreaterThanOrEqual(acked);
    const operation = await json<{ operationId: string }>(await cells.post(`${endpoint('gru')}${database(created.id)}/point-in-time/restore`, {
      data: { pointInTime: new Date(at).toISOString(), name: name('idle-restored') },
    }), 202);
    const copy = await completed(cells, created.id, operation.operationId);
    copies.push(copy);
    expect(await ledger.read(cells, 'gru', copy)).toEqual(ledger.acknowledged);
    console.log(`idle archive ${created.id}: acknowledged=${acked} proven=${at} restored=${JSON.stringify(ledger.acknowledged)}`);
  };
  let failure: unknown;
  try {
    await answer(cells, endpoint('gru'), created.id, ['CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)']);
    await ledger.write(cells, 'gru', [[1, 'before-restart']]);
    await recover(Date.now());
    await ledger.write(cells, 'gru', [[2, 'restart-immediately']]);
    const acked = Date.now();
    await restartAfterKill(peer)();
    await expect.poll(() => probe(peer, '/readyz'), { message: 'the restarted peer is ready' }).toBe(200);
    await tokenReady(peer, cells, database(created.id));
    await recover(acked);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      for (const copy of copies) await removeDatabase(cells, copy);
      await removeDatabase(cells, created.id);
    });
  }
});

test('writes made while the archive is unreachable are still restorable from the peer, a future point is refused, and deletion removes the restore window', async ({ cells }) => {
  test.setTimeout(180_000);
  const peer = node('journey-1');
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('backup-hole'), regions: ['gru'] },
  }), 201);
  const ledger = new Ledger(created.id);
  const copies: string[] = [];
  let outage = false;
  let failure: unknown;
  try {
    await answer(cells, endpoint('gru'), created.id, ['CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)']);
    const snapshot = await answer(cells, endpoint('gru'), created.id, ["INSERT INTO fault_rows (id, value) VALUES (1, 'before-outage')"]);
    ledger.record('acknowledged', [[1, 'before-outage']], 'gru');
    const archived = await json<{ databaseId: string; archivedThrough: string }>(await cells.post(`${endpoint('gru')}${database(created.id)}/backups`));
    expect(archived).toMatchObject({ databaseId: created.id });
    const stored = await backupPuts(`databases/${created.id}/`);
    expect(stored.some(key => key.endsWith('/manifest.json') || key.includes('/snapshots/') || key.includes('/segments/'))).toBe(true);

    const future = await cells.post(`${endpoint('gru')}${database(created.id)}/point-in-time/restore`, {
      data: { pointInTime: new Date(Date.now() + 3_600_000).toISOString(), name: name('future-hole') },
    });
    const futureText = await future.text();
    expect(future.status(), futureText).toBe(400);
    expect((JSON.parse(futureText) as { error: { code: string } }).error.code).toBe('restore_point_in_future');

    const failures = await metric(peer, 'terrabase_backup_failures_total');
    await backupStore('down');
    outage = true;
    await ledger.write(cells, 'gru', [[2, 'during-outage']]);
    const during = await answer(cells, endpoint('gru'), created.id, ["INSERT INTO fault_rows (id, value) VALUES (3, 'still-local')"]);
    ledger.record('acknowledged', [[3, 'still-local']], 'gru');
    expect(await probe(peer, '/readyz'), 'the peer keeps serving while the archive cannot be reached').toBe(200);
    const operation = await json<{ operationId: string }>(await cells.post(`${endpoint('gru')}${database(created.id)}/point-in-time/restore`, {
      data: { bookmark: snapshot.bookmark, name: name('local-hole') },
    }), 202);
    copies.push(await completed(cells, created.id, operation.operationId));
    expect(await ledger.read(cells, 'gru', copies[0])).toEqual([[1, 'before-outage']]);
    await backupStore('up');
    outage = false;
    await expect.poll(() => metric(peer, 'terrabase_backup_failures_total'), { message: 'the outage was counted' }).toBeGreaterThanOrEqual(failures);
    const caughtUp = await json<{ databaseId: string; latestRestorePointUtc: string }>(await cells.post(`${endpoint('gru')}${database(created.id)}/backups`));
    expect(caughtUp.databaseId).toBe(created.id);
    expect(Date.parse(caughtUp.latestRestorePointUtc)).toBeGreaterThan(0);
    expect((await answer(cells, endpoint('gru'), created.id, ['SELECT id, value FROM fault_rows ORDER BY id'], { bookmark: during.bookmark })).results[0].rows)
      .toEqual([[1, 'before-outage'], [2, 'during-outage'], [3, 'still-local']]);

    await removeDatabase(cells, created.id);
    await expect.poll(async () => (await cells.get(`${endpoint('gru')}${database(created.id)}/point-in-time`)).status(), {
      message: 'deleting the database removes its restore window',
    }).toBe(404);
    expect((await cells.get(`${endpoint('gru')}${database(created.id)}`)).status()).toBe(404);
    const gone = await systemBackup(peer);
    expect([200, 503]).toContain(gone.status);
    console.log(`archive ${created.id}: local bookmark restore during the outage kept ${JSON.stringify([[1, 'before-outage']])}; deletion left no restore window`);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      if (outage) await backupStore('up');
      for (const copy of copies) await removeDatabase(cells, copy);
      const leftover = await cells.get(`${endpoint('gru')}${database(created.id)}`);
      if (leftover.status() === 200) await removeDatabase(cells, created.id);
    });
  }
});

async function completed(cells: Cells, source: string, operation: string): Promise<string> {
  let copy = '';
  await expect.poll(async () => {
    const state = await json<{ state: string; targetDatabaseId: string; failureMessage: string | null }>(
      await cells.get(`${endpoint('gru')}${database(source)}/restores/${operation}`));
    expect(state.state, state.failureMessage ?? '').not.toBe('failed');
    if (state.state === 'succeeded') copy = state.targetDatabaseId;
    return state.state;
  }, { message: 'the restore succeeds', timeout: 60_000 }).toBe('succeeded');
  return copy;
}

test('catalog replication ignores replays, keeps a deletion against a stale create, and refuses malformed commits', async () => {
  const environment: NodeJS.ProcessEnv = { ...process.env, DOTNET_ENVIRONMENT: 'Development', ASPNETCORE_ENVIRONMENT: 'Development' };
  for (const key of Object.keys(environment)) {
    if (key.startsWith('TS_') || key.startsWith('TERRABASE_')) delete environment[key];
  }
  const master = randomBytes(32).toString('base64');
  saveSecret('catalog-import-master', master);
  Object.assign(environment, {
    TERRABASE_MASTER_KEY: master, TERRABASE_PORT: '18720', TERRABASE_INTERNAL_PORT: '18721',
    TERRABASE_POSTGRES_PORT: '18722', TERRABASE_METRICS_PORT: '18723', TERRABASE_QUIC_PORT: '18724',
  });
  const build = process.env.E2E_BUILD ?? join(stateDir, 'build');
  const result = await promisify(execFile)('dotnet', [join(build, 'catalog-import', 'terrabase.dll'),
    `--data-directory=${join(stateDir, `catalog-import-${faultId()}`)}`,
  ], { env: environment, cwd: e2eRoot, timeout: 60_000 });
  for (const label of ['identical', 'foreign-delete', 'stale-create', 'duplicate-transaction', 'malformed', 'invalid-origin', 'stale-schema', 'local-ahead', 'gap', 'gap-filled']) {
    expect(result.stdout).toContain(`CATALOG_CASE ${label} passed`);
  }
  expect(result.stdout).toContain('CATALOG_IMPORT identical=duplicate foreign=applied stale_create=deleted refused=4 gap=buffered unchanged=true');
  console.log(result.stdout.split('\n').filter(line => line.startsWith('CATALOG_')).join('\n'));
});

test('closure reaches every peer, including one that was down, and a platform project stays protected', async ({ cells, request, playwright }) => {
  test.setTimeout(180_000);
  const user = faultId();
  const gru = node('journey-1');
  const fra = node('journey-2');
  const fraPeer = node('journey-4');
  const manage = async (peer: Node, method: string, path: string, body?: unknown, expected = 200) => {
    const response = await nodeRequest(peer, path, method, body, user);
    expect(response.status, response.body).toBe(expected);
    return response.body ? JSON.parse(response.body) as { id: string; error?: { code: string } } : undefined;
  };
  const organization = (await manage(fra, 'POST', '/v1/management/organizations', { name: name('closure') }, 201))!;
  const organizationPath = `/v1/management/organizations/${organization.id}`;
  const projectRecord = (await manage(fra, 'POST', `${organizationPath}/projects`, { name: name('closure') }, 201))!;
  const projectPath = `${organizationPath}/projects/${projectRecord.id}`;
  const databaseRecord = (await manage(fra, 'POST', `/v1/projects/${projectRecord.id}/databases`, { name: name('closure-db'), regions: ['fra'] }, 201))!;
  const closure = { version: 2, closureId: faultId(), authorityOrganizationId: organization.id };
  const closurePath = `/admin/projects/${projectRecord.id}/closure`;
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('closure-live'), regions: ['gru'] },
  }), 201);
  const ledger = new Ledger(created.id);
  let failure: unknown;
  let restart: (() => Promise<void>) | undefined;
  try {
    await answer(cells, endpoint('gru'), created.id, ['CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)']);
    const token = await projectToken(request);
    await ledger.write(cells, 'gru', [[1, 'before-fra-outage']]);
    await ledger.converge(token, peersIn('gru'));
    const occupied = await nodeRequest(gru, closurePath, 'PUT', closure);
    expect(occupied.status, occupied.body).toBe(409);
    expect(JSON.parse(occupied.body).error.code).toBe('not_empty');
    await manage(fra, 'DELETE', `/v1/projects/${projectRecord.id}/databases/${databaseRecord.id}`, undefined, 204);
    const protectedProject = await nodeRequest(gru, '/admin/projects/00000000000000000000000002/closure', 'PUT', {
      version: 2, closureId: faultId(), authorityOrganizationId: '00000000000000000000000001',
    });
    expect(protectedProject.status, protectedProject.body).toBe(409);
    expect(JSON.parse(protectedProject.body).error.code).toBe('platform_project_protected');

    restart = restartAfterKill(fra);
    await ledger.write(cells, 'gru', [[2, 'while-fra-peer-down']]);
    expect(await ledger.direct(node('journey-3'), token, [[3, 'other-gru-peer']]), 'the other gru peer accepts writes while a fra peer is down').toBe(true);
    const accepted = await nodeRequest(gru, closurePath, 'PUT', closure);
    expect(accepted.status, accepted.body).toBe(200);
    expect(JSON.parse(accepted.body)).toMatchObject({ status: 'accepted', closureId: closure.closureId, pendingHolders: 0, evidenceVersion: 2 });
    await expect.poll(async () => (await nodeRequest(fraPeer, projectPath)).status, {
      message: 'the live fra peer drops the closed project through anti-entropy', timeout: 60_000,
    }).toBe(404);
    const state = await replication(cells, endpoint('gru'), created.id);
    expect(state.regions.find(item => item.region === 'gru')?.healthy).toBe(true);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      if (restart) await restart();
      await expect.poll(async () => (await nodeRequest(fra, projectPath)).status, {
        message: 'the restarted fra peer learns the closure', timeout: 60_000,
      }).toBe(404);
      const again = await nodeRequest(fra, closurePath, 'PUT', closure);
      expect(again.status, again.body).toBe(200);
      expect(JSON.parse(again.body)).toMatchObject({ status: 'accepted', pendingHolders: 0, closureId: closure.closureId });
      await manage(fra, 'DELETE', organizationPath, undefined, 204);
      await removeDatabase(cells, created.id);
      for (const region of regions) expect((await cells.get(`${endpoint(region)}${project()}/databases`)).status()).toBe(200);
      const owner = await playwright.request.newContext({ storageState: ownerSession() });
      try {
        const profile = await owner.get(`${dash}/profile`);
        expect(new URL(profile.url()).pathname).toBe('/profile');
        expect(profile.status()).toBe(200);
        expect(await profile.text()).toContain(journey().email);
      } finally {
        await owner.dispose();
      }
    });
  }
});
