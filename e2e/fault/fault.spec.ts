import { randomBytes } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { once } from 'node:events';
import { request as httpRequest } from 'node:http';
import { existsSync, readFileSync, readdirSync, renameSync, truncateSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect } from '@playwright/test';
import { test, database, json, name, project, removeDatabase, sql, type Cells } from '../placement/support';
import { auth, dash, e2eRoot, localPort, stateDir } from '../support/stack';
import { keyId } from '../support/api-key';
import { journey, ownerSession, saveSecret } from '../support/state';
import { Nats, putKey, seal } from '../replication/push.mjs';
import { receiptHandoff } from './receipt-handoff';
import { regionalHook } from './regional-hook';
import { keyStatusProxy } from './key-status-proxy';
import { postgres } from './signing-keys-support';
import { verifyRestoreDrill } from './restore-drill';
import { extendPeers, startRegion } from '../placement/fleet';
import { backupPuts, backupStore, cleanUp, continuity, counted, crashWrite, endpoint, faultId, fileHash, holdsFile, holeRecords, keptCopies, keptHash, Kv, Ledger, logged, metric, node, nodeBuild, nodeHoles, nodes, nodeRequest, nodeStatus, plantHole, probe, promoting, refuseConnections, regions, restartAfterKill, scrape, signIn, exchangedToken, systemContinuity, tokenReady, topology, ulidAt, writerIs, writerToken, type HomeLease, type Node, type Row } from './support';

test.beforeEach(async () => {
  expect(process.env.E2E_TARGET ?? 'local').toBe('local');
  expect(process.env.E2E_REGIONS).toBe('2');
  expect(process.env.E2E_NODES_PER_REGION).toBe('2');
  await topology();
});

test('a writer killed during a write restarts without losing acknowledged rows in either region', async ({ cells, request }) => {
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('fault'), regions: [...regions] },
  }), 201);
  const ledger = new Ledger(created.id);
  let failure: unknown;
  try {
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await ledger.write(cells, 'gru', [[1, 'before-gru']]);
    await ledger.compare(cells);
    await ledger.write(cells, 'fra', [[2, 'before-fra']]);
    await ledger.compare(cells);
    const retained = [...ledger.acknowledged];
    const token = await writerToken(request);
    const writer = (await topology()).find(node => node.region === 'gru')!;
    const writing = crashWrite(writer, ledger, token);
    await writing.sent;
    const restart = restartAfterKill(writer, true, { token, path: database(created.id) });
    try {
      const outcome = await writing.outcome;
      if (outcome.error) throw outcome.error;
    } finally {
      await restart();
    }
    await expect.poll(async () => {
      const response = await request.get(`http://127.0.0.1:${writer.port}/readyz`);
      return response.status();
    }, { message: 'the crashed writer recovers from its existing data directory' }).toBe(200);
    await ledger.compare(cells, retained);
    await ledger.write(cells, 'gru', [[3, 'after-gru']]);
    await ledger.write(cells, 'fra', [[4, 'after-fra']]);
    await ledger.compare(cells);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      await removeDatabase(cells, created.id);
    });
  }
});

test('each regional writer records a restore drill with matching row content and exact recovery position', async () => {
  for (const writer of await topology()) await verifyRestoreDrill(writer);
});

test('sjc automatically records the same verified restore evidence as the other regions', async () => {
  const restorePeers = extendPeers(['sjc']);
  let region: Awaited<ReturnType<typeof startRegion>> | undefined;
  try {
    region = await startRegion('sjc', 0);
    const { hostname, port } = new URL(region.endpoint);
    await verifyRestoreDrill({ name: 'journey-extra-sjc', region: 'sjc', address: hostname, port: Number(port), metricsPort: region.metricsPort });
  } finally {
    if (region) await region.stop();
    restorePeers();
  }
});

test('the recovery-point age keeps rising while the backup bucket is unreachable, also across a writer restart', async () => {
  const writer = (await topology()).find(node => node.region === 'gru')!;
  await expect.poll(() => probe(writer, '/backupz'), { message: 'the writer ships every file before the outage' }).toBe(200);
  await backupStore('down');
  let outage = true;
  const down = Date.now();
  const rising = async (above: number, message: string) => {
    let age = 0;
    await expect.poll(async () => {
      const elapsed = (Date.now() - down) / 1000;
      age = await metric(writer, 'terrabase_backup_recovery_point_age_seconds', 'region="gru"');
      return Number.isFinite(age) && age > above && age >= elapsed;
    }, { message }).toBe(true);
    return age;
  };
  try {
    const first = await rising(0, 'the age is a finite number covering the outage');
    const second = await rising(first, 'the age rises between two observations');
    const restart = restartAfterKill(writer, true, undefined, async () => {
      expect(await probe(writer, '/readyz')).toBe(503);
      await rising(second, 'the restarted writer reports a finite age that still covers the outage before the bucket is back');
      await backupStore('up');
      outage = false;
    });
    await restart();
  } finally {
    if (outage) await backupStore('up');
  }
  await expect.poll(() => probe(writer, '/readyz'), { message: 'the restarted writer is ready' }).toBe(200);
  await expect.poll(() => probe(writer, '/backupz'), { message: 'the writer ships again once the bucket is back' }).toBe(200);
});

test('restore a backup into another database and compare its rows with the snapshot ledger in every region', async ({ cells }) => {
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('backup-ledger'), regions: [...regions] },
  }), 201);
  const ledger = new Ledger(created.id);
  let restored: string | undefined;
  let failure: unknown;
  try {
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await ledger.write(cells, 'gru', [[1, 'snapshot-gru']]);
    await ledger.compare(cells);
    await ledger.write(cells, 'fra', [[2, 'snapshot-fra']]);
    await ledger.compare(cells);
    const snapshotRows = [...ledger.acknowledged];
    const backup = await json<{ id: string; status: string }>(await cells.post(`${endpoint('gru')}${database(created.id)}/backups`), 201);
    expect(backup.status).toBe('ready');
    ledger.record('snapshot', snapshotRows, 'gru');
    await ledger.write(cells, 'gru', [[3, 'after-snapshot']]);
    const operation = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${database(created.id)}/backups/${backup.id}/restore`, {
      data: { name: name('restored-ledger') },
    }), 202);
    await expect.poll(async () => {
      const state = await json<{ status: string; database: string; errorCode?: string }>(await cells.get(`${endpoint('gru')}${database(created.id)}/restores/${operation.id}`));
      expect(state.status, state.errorCode).not.toBe('failed');
      if (state.status === 'completed') restored = state.database;
      return state.status;
    }, { message: 'the backup restore completes' }).toBe('completed');
    for (const region of regions) {
      await expect.poll(() => ledger.read(cells, region, restored), { message: `the restored ${region} data matches the snapshot`, timeout: 45_000, intervals: [250, 500, 1000] }).toEqual(snapshotRows);
    }
    await ledger.compare(cells);
    console.log(`backup ${backup.id}: restored snapshot ledger in both regions; later source write excluded`);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      if (restored) await removeDatabase(cells, restored);
      await removeDatabase(cells, created.id);
    });
  }
});

const homes = 'terrabase-homes';
const owners = 'terrabase-region-owners';
const outageHold = 30_000;
const homeDown = { status: 503, code: 'unavailable' };

async function createLedgers(cells: Cells, count: number, label: string): Promise<Ledger[]> {
  const ledgers: Ledger[] = [];
  for (let index = 0; index < count; index++) {
    const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
      data: { name: name(`${label}-${index}`), regions: [...regions] },
    }), 201);
    const ledger = new Ledger(created.id);
    ledgers.push(ledger);
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await ledger.write(cells, 'gru', Array.from({ length: 20 }, (_, row): Row => [row + 1, `base-${row + 1}`]));
  }
  for (const ledger of ledgers) await ledger.compare(cells);
  return ledgers;
}

async function homeLease(kv: Kv, id: string) {
  const stored = await kv.get<HomeLease>(homes, `home.${id}`);
  expect(stored, `database ${id} has a home lease`).not.toBeNull();
  return stored!;
}

async function holdOutage(kv: Kv, ledgers: Ledger[], killedAt: number, before: { value: HomeLease }[], token: string): Promise<void> {
  await expect.poll(async () => {
    for (const [index, ledger] of ledgers.entries()) {
      const lease = (await homeLease(kv, ledger.id)).value;
      expect(lease, 'nothing claims a lapsed home lease: node, epoch and session stay as they were').toEqual(before[index].value);
    }
    return Date.now() - killedAt >= outageHold;
  }, { message: `the home stays on journey-1 for ${outageHold / 1000} s`, timeout: outageHold + 30_000, intervals: [1000] }).toBe(true);
  for (const ledger of ledgers) {
    const base = Array.from({ length: 20 }, (_, row): Row => [row + 1, `base-${row + 1}`]);
    const rows = await ledger.readNode(node('journey-2'), token);
    for (const row of base) expect(rows, 'fra keeps serving reads from its pulled replica during the outage').toContainEqual(row);
    const id = 2000 + ledgers.indexOf(ledger);
    expect(await ledger.refusal(node('journey-3'), token, [[id, `needs-home-${id}`]]), 'a write that needs the home fails while the home is down')
      .toEqual(homeDown);
  }
}

async function killDuringBurst(ledgers: Ledger[], token: string): Promise<{ killedAt: number; restart: () => Promise<void> }> {
  const writer = node('journey-1');
  const ids = Array.from({ length: 240 }, (_, index) => 1000 + index);
  let next = 0;
  let acknowledged = 0;
  let killed: { killedAt: number; restart: () => Promise<void> } | undefined;
  try {
    await Promise.all(Array.from({ length: 12 }, async () => {
      while (next < ids.length) {
        const id = ids[next++];
        const ledger = ledgers[id % ledgers.length];
        if (await ledger.direct(writer, token, [[id, `burst-${id}`]]) && ++acknowledged === 45 && !killed) {
          killed = { killedAt: Date.now(), restart: restartAfterKill(writer, true, { token, path: database(ledgers[0].id) }) };
        }
      }
    }));
  } catch (error) {
    await cleanUp(error, async () => { await killed?.restart(); });
    throw error;
  }
  expect(killed, 'the writer was killed during the burst').toBeDefined();
  return killed!;
}

const writers = () => [node('journey-1'), node('journey-2')];

async function recovered(ledgers: Ledger[], token: string): Promise<void> {
  for (const ledger of ledgers) {
    await ledger.converge(token, writers(), [...ledger.acknowledged]);
    for (const writer of writers()) {
      const rows = await ledger.readNode(writer, token);
      for (const row of ledger.refused) expect(rows, 'a refused write was never applied').not.toContainEqual(row);
    }
  }
}

test('a planned move to a node that is not its region writer is refused and changes nothing', async ({ cells }) => {
  const kv = await Kv.open();
  let failure: unknown;
  try {
    const [ledger] = await createLedgers(cells, 1, 'home-move-refused');
    const before = await homeLease(kv, ledger.id);
    const refusals = await counted(node('journey-4'), 'terrabase_home_refusals_total', { reason: 'not_writer' });
    const answer = await nodeRequest(node('journey-4'), `/v1/sync/organizations/${before.value.organizationId}/projects/${before.value.projectId}/databases/${ledger.id}/promote`, 'POST', {
      moveId: `fault-${ledger.id}`, fromRegion: 'gru', version: before.value.epoch + 1, replicaRegions: ['gru'], allRegions: false,
    });
    expect(answer.status, answer.body).toBe(409);
    expect(JSON.parse(answer.body).error.code).toBe('move_aborted');
    await expect.poll(() => counted(node('journey-4'), 'terrabase_home_refusals_total', { reason: 'not_writer' }), { message: 'journey-4 counts the refusal' }).toBe(refusals + 1);
    expect(await homeLease(kv, ledger.id)).toEqual(before);
    await ledger.write(cells, 'gru', [[700, 'after-refused-move']]);
    await ledger.write(cells, 'fra', [[701, 'after-refused-move-fra']]);
    await ledger.compare(cells);
    await removeDatabase(cells, ledger.id);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      kv.close();
    });
  }
});

test('a move that fails before its commit point rolls back: the target acknowledged nothing, both files stay, the old home serves and no push is lost', async ({ cells, request }) => {
  test.setTimeout(240_000);
  const token = await writerToken(request);
  const kv = await Kv.open();
  const proxy = await keyStatusProxy();
  let hook: Awaited<ReturnType<typeof regionalHook>> | undefined;
  let failure: unknown;
  try {
    const [ledger] = await createLedgers(cells, 1, 'move-rollback');
    await ledger.write(cells, 'fra', [[21, 'pushed-from-fra']]);
    await ledger.compare(cells);
    hook = await regionalHook(node('journey-1'), proxy.environment);
    await json(await cells.get(`${endpoint('gru')}${database(ledger.id)}`));
    const before = await homeLease(kv, ledger.id);
    const { organizationId, projectId } = before.value;
    const gru = node('journey-1');
    const target = node('journey-2');
    const drainFailed = await counted(target, 'terrabase_home_refusals_total', { reason: 'drain_failed' });
    const demotions = await counted(gru, 'terrabase_home_demotions_total');
    const published = await kv.publish(`terrabase.push.fra.gru.${organizationId}.${projectId}.${ledger.id}`, { undecodable: true }, { 'Tb-Epoch': String(before.value.epoch) });
    expect(published.error, JSON.stringify(published)).toBeUndefined();
    const started = Date.now();
    const heldStatus = proxy.arm();
    const move = cells.post(`${endpoint('gru')}${database(ledger.id)}/move-home`, { data: { homeRegion: 'fra' }, timeout: 120_000 });

    await test.step('a slow confirmed-valid ts-auth answer crosses the admission window while the long move retains its existing revocation budget', async () => {
      await heldStatus;
      expect(await hook!.command('regional-auth-expiry', organizationId, projectId, ledger.id)).toBe('expired');
      proxy.release();
    });

    await test.step('the lease keeps naming the old home, and the target stays fenced and acknowledges no write before its commit point', async () => {
      await expect.poll(() => promoting(target, ledger.id), { message: 'the promotion fence is on disk', timeout: 60_000, intervals: [100] }).toBe(true);
      expect((await homeLease(kv, ledger.id)).value, 'the target writes the lease only at its commit point').toMatchObject({ home: 'gru', node: 'journey-1', epoch: before.value.epoch });
      await expect.poll(async () => (await (await fetch(`http://127.0.0.1:${target.port}${database(ledger.id)}`, { headers: { authorization: `Bearer ${token}` } })).json()).homeRegion,
        { message: 'journey-2 recorded the new placement before its commit point', timeout: 30_000, intervals: [100] }).toBe('fra');
      expect(await ledger.refusal(target, token, [[22, 'during-move']]), 'the target refuses a write before its commit point')
        .toEqual({ status: 503, code: 'unavailable' });
    });

    await test.step('the drain fails, the move rolls back and the old home serves again', async () => {
      const answer = await move;
      expect(answer.status(), await answer.text()).toBe(503);
      expect((await answer.json()).error.code).toBe('replica_not_ready');
      expect(Date.now() - started, 'the move outlasted one lease scan of the old home').toBeGreaterThan(15_000);
      await expect.poll(async () => (await homeLease(kv, ledger.id)).value, { message: 'the lease names journey-1 again at the next epoch', timeout: 60_000 })
        .toMatchObject({ home: 'gru', node: 'journey-1', epoch: before.value.epoch + 2 });
      expect((await homeLease(kv, ledger.id)).value.adopted ?? null).toBeNull();
      expect(await counted(target, 'terrabase_home_refusals_total', { reason: 'drain_failed' })).toBe(drainFailed + 1);
      expect(promoting(target, ledger.id), 'the promotion fence is lifted').toBe(false);
      await ledger.write(cells, 'gru', [[23, 'after-rollback-gru']]);
      await ledger.write(cells, 'fra', [[24, 'after-rollback-fra']]);
      await ledger.compare(cells);
      for (const writer of writers()) {
        const rows = await ledger.readNode(writer, token);
        expect(rows, 'the push from fra before the move is served').toContainEqual([21, 'pushed-from-fra']);
        expect(rows, 'the write the fenced target refused was never applied').not.toContainEqual([22, 'during-move']);
      }
    });

    await test.step('both files exist: the old home serves its file and the target keeps its imported copy in its inventory', async () => {
      expect(holdsFile(gru, before.value), 'the old home keeps its database file').toBe(true);
      expect(holdsFile(target, before.value), 'the target moved its imported copy out of the database path').toBe(false);
      const copies = await keptCopies(target, ledger.id);
      expect(copies).toEqual([expect.objectContaining({ organizationId, projectId, databaseId: ledger.id })]);
      expect(copies[0].files).toContain(`${ledger.id}.db`);
      expect(copies[0].bytes).toBeGreaterThan(0);
      await expect.poll(() => logged(target, 'Preserved a database copy', `"Database":"${ledger.id}"`, '"Reason":"move_rolled_back"'), { message: 'the target logs why it preserved its copy' }).toBe(1);
      await expect.poll(() => counted(target, 'terrabase_storage_retained_files'), { message: 'the retained inventory counts the preserved copy' })
        .toBeGreaterThanOrEqual(copies[0].files.length);
    });

    await test.step('the old home judged nothing during its own move and deleted nothing', async () => {
      expect(await counted(gru, 'terrabase_home_demotions_total')).toBe(demotions);
      expect(await keptCopies(gru, ledger.id)).toEqual([]);
    });
    await removeDatabase(cells, ledger.id);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      kv.close();
      await hook?.close();
      await proxy.close();
    });
  }
});

test('a rollback keeps both copies in place until the lease names the old home again, then serves every acknowledged row', async ({ cells, request }) => {
  test.setTimeout(300_000);
  const token = await writerToken(request);
  const kv = await Kv.open();
  let failure: unknown;
  try {
    const [ledger] = await createLedgers(cells, 1, 'rollback-unproven');
    await ledger.write(cells, 'fra', [[31, 'pushed-from-fra']]);
    await ledger.compare(cells);
    const before = await homeLease(kv, ledger.id);
    const { organizationId, projectId } = before.value;
    const gru = node('journey-1');
    const target = node('journey-2');
    const published = await kv.publish(`terrabase.push.fra.gru.${organizationId}.${projectId}.${ledger.id}`, { undecodable: true }, { 'Tb-Epoch': String(before.value.epoch) });
    expect(published.error, JSON.stringify(published)).toBeUndefined();
    const move = cells.post(`${endpoint('gru')}${database(ledger.id)}/move-home`, { data: { homeRegion: 'fra' }, timeout: 120_000 });

    await test.step('while the target drains, the lease is rewritten to name a node that never held the database', async () => {
      await expect.poll(() => promoting(target, ledger.id), { message: 'journey-2 starts its promotion', timeout: 60_000, intervals: [100] }).toBe(true);
      const current = await homeLease(kv, ledger.id);
      expect(current.value, 'the target writes the lease only at its commit point').toMatchObject({ home: 'gru', node: 'journey-1', epoch: before.value.epoch });
      await kv.put(homes, `home.${ledger.id}`, { ...current.value, home: 'fra', node: 'journey-4', epoch: before.value.epoch + 1 }, current.seq);
    });

    await test.step('the move stays unresolved: neither node sets its copy aside', async () => {
      const answer = await move;
      expect(answer.status(), await answer.text()).toBe(503);
      expect((await answer.json()).error.code).toBe('move_unresolved');
      expect(holdsFile(gru, before.value), 'the old home keeps its file in place').toBe(true);
      expect(holdsFile(target, before.value), 'the target keeps its imported copy in place').toBe(true);
      expect(promoting(target, ledger.id), 'the target stays fenced').toBe(true);
      expect(await keptCopies(gru, ledger.id)).toEqual([]);
      expect(await keptCopies(target, ledger.id)).toEqual([]);
      expect(logged(target, 'is not rolled back: the lease names', '"Node":"journey-4"'), 'the target says why it keeps its copy').toBeGreaterThanOrEqual(1);
    });

    await test.step('once the lease names the old home again, the rollback finishes and every acknowledged row is served', async () => {
      const current = await homeLease(kv, ledger.id);
      await kv.put(homes, `home.${ledger.id}`, { ...current.value, home: 'gru', node: 'journey-1', epoch: before.value.epoch + 2, session: '', adopted: null }, current.seq);
      await expect.poll(() => keptCopies(target, ledger.id), { message: 'the target sets its imported copy aside only now', timeout: 60_000 })
        .toEqual([expect.objectContaining({ databaseId: ledger.id })]);
      await expect.poll(() => logged(target, 'Preserved a database copy', `"Database":"${ledger.id}"`, '"Reason":"move_rolled_back"'), { message: 'the target logs why it preserved its copy' }).toBe(1);
      expect(promoting(target, ledger.id), 'the promotion fence is lifted').toBe(false);
      expect(holdsFile(gru, before.value), 'the old home still holds its file').toBe(true);
      expect(await keptCopies(gru, ledger.id)).toEqual([]);
      await ledger.write(cells, 'gru', [[33, 'after-rollback-gru']]);
      await ledger.write(cells, 'fra', [[34, 'after-rollback-fra']]);
      await ledger.compare(cells);
      for (const writer of writers()) expect(await ledger.readNode(writer, token), `${writer.name} serves the push from fra before the move`).toContainEqual([31, 'pushed-from-fra']);
    });
    await removeDatabase(cells, ledger.id);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      kv.close();
    });
  }
});

test('a home move never applies a push again that the durable stream frontier covers, also after its applied id left the 7-day retention', async ({ cells }) => {
  test.setTimeout(180_000);
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('frontier-replay'), regions: [...regions] },
  }), 201);
  await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE items (id TEXT PRIMARY KEY, value TEXT NOT NULL)');
  const read = async (region: string) => (await sql(cells, endpoint(region), created.id, 'SELECT id, value FROM items ORDER BY id')).results[0].rows;
  const kv = await Kv.open();
  const nats = new Nats(readFileSync(join(stateDir, 'nats-token'), 'utf8').trim(), localPort('nats', 4222));
  let failure: unknown;
  try {
    await nats.connect();
    const lease = (await homeLease(kv, created.id)).value;
    const scope = `${lease.organizationId}/${lease.projectId}/${created.id}`;
    const subject = scope.replaceAll('/', '.');
    const secrets = readFileSync(join(stateDir, 'secrets'), 'utf8').trim().split('\n');
    const keyId = ulidAt(Date.now());
    const dataKey = randomBytes(32);
    await putKey({ master: Buffer.from(secrets[1], 'base64'), access: secrets[12], secret: secrets[13] }, scope, keyId, dataKey);
    const at = Date.now() - 8 * 86_400_000;
    const pushed = {
      id: ulidAt(at), region: 'fra', hlc: { physicalMilliseconds: at, logical: 0, nodeId: 'journey-2' },
      changes: [{ op: 'insert', table: 'items', key: null, values: { id: 'old', value: 'removed-by-table-replacement' } }],
    };

    await test.step('the home applies an 8-day-old push once; its applied id leaves the retention at once, its stream position stays in the frontier', async () => {
      const applied = nats.event(`terrabase.applied.fra.${subject}`, (value: { id?: string }) => value.id === pushed.id);
      const sent = await nats.request(`terrabase.push.fra.gru.${subject}`, seal(pushed, scope, keyId, dataKey), { 'Tb-Epoch': String(lease.epoch) });
      expect(sent.error, JSON.stringify(sent)).toBeUndefined();
      expect(await applied).toMatchObject({ id: pushed.id, state: 'confirmed' });
      await expect.poll(() => read('gru')).toEqual([['old', 'removed-by-table-replacement']]);
    });

    await test.step('an acknowledged table replacement removes the pushed row in both regions', async () => {
      const replaced = await cells.post(`${endpoint('gru')}${database(created.id)}/sql/execute`, { data: { statements: [
        { sql: 'DROP TABLE items' },
        { sql: 'CREATE TABLE items (id TEXT PRIMARY KEY, value TEXT NOT NULL)' },
        { sql: "INSERT INTO items VALUES ('new', 'acknowledged-replacement')" },
      ] } });
      expect(replaced.status(), await replaced.text()).toBe(200);
      for (const region of regions) await expect.poll(() => read(region), { message: `${region} serves only the replacement` }).toEqual([['new', 'acknowledged-replacement']]);
    });

    await test.step('the new home leaves the old push to its sequencer, which skips it by the frontier: the removed row stays removed', async () => {
      const moved = await json<{ homeRegion: string }>(await cells.post(`${endpoint('gru')}${database(created.id)}/move-home`, { data: { homeRegion: 'fra' }, timeout: 120_000 }));
      expect(moved.homeRegion).toBe('fra');
      for (const region of regions) expect(await read(region), `${region} serves the acknowledged replacement after the home move`).toEqual([['new', 'acknowledged-replacement']]);
      expect(logged(node('journey-2'), 'is older than the applied-id retention', `"Database":"${created.id}"`), 'the promotion drain left the old push to the sequencer').toBe(1);
      const after = await cells.post(`${endpoint('fra')}${database(created.id)}/sql/execute`, { data: { statements: [{ sql: "INSERT INTO items VALUES ('later', 'after-the-move')" }] } });
      expect(after.status(), await after.text()).toBe(200);
      for (const region of regions) {
        await expect.poll(() => read(region), { message: `${region} serves the write after the move and never the removed row` })
          .toEqual([['later', 'after-the-move'], ['new', 'acknowledged-replacement']]);
      }
    });
    await removeDatabase(cells, created.id);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      nats.close();
      kv.close();
    });
  }
});

test('a source on the previous build keeps its file while a new target drains past the source\'s lease scan, and serves writes again after the rollback', async ({ cells, request }) => {
  test.setTimeout(300_000);
  const previous = process.env.E2E_PREVIOUS_TERRABASE;
  expect(previous, 'E2E_PREVIOUS_TERRABASE names the base revision\'s TerraBase build').toMatch(/terrabase\.dll$/);
  expect(existsSync(previous!), `${previous} exists`).toBe(true);
  const gru = node('journey-1');
  const target = node('journey-2');
  const own = nodeBuild(gru);
  await restartAfterKill(gru, true, undefined, undefined, previous)();
  const token = await writerToken(request);
  const kv = await Kv.open();
  let failure: unknown;
  try {
    expect(nodeBuild(gru), 'journey-1 runs the previous build').toBe(previous);
    const [ledger] = await createLedgers(cells, 1, 'mixed-rollback');
    const before = await homeLease(kv, ledger.id);
    const { organizationId, projectId } = before.value;
    expect(holdsFile(gru, before.value)).toBe(true);
    const demotions = await counted(gru, 'terrabase_home_demotions_total');
    const published = await kv.publish(`terrabase.push.fra.gru.${organizationId}.${projectId}.${ledger.id}`, { undecodable: true }, { 'Tb-Epoch': String(before.value.epoch) });
    expect(published.error, JSON.stringify(published)).toBeUndefined();
    const started = Date.now();
    const move = cells.post(`${endpoint('gru')}${database(ledger.id)}/move-home`, { data: { homeRegion: 'fra' }, timeout: 120_000 });

    await test.step('while the new target is fenced, the lease names only the old source', async () => {
      await expect.poll(() => promoting(target, ledger.id), { message: 'journey-2 starts its promotion', timeout: 60_000, intervals: [100] }).toBe(true);
      expect((await homeLease(kv, ledger.id)).value, 'the target writes the lease only at its commit point').toMatchObject({ home: 'gru', node: 'journey-1', epoch: before.value.epoch });
    });

    await test.step('the drain outlasts the old source\'s lease scan and rolls back; the source kept its file and serves reads and writes', async () => {
      const answer = await move;
      expect(answer.status(), await answer.text()).toBe(503);
      expect((await answer.json()).error.code).toBe('replica_not_ready');
      expect(Date.now() - started, 'the move outlasted one lease scan of the old source').toBeGreaterThan(15_000);
      expect(holdsFile(gru, before.value), 'the old source keeps its database file').toBe(true);
      expect(await counted(gru, 'terrabase_home_demotions_total'), 'the old source never demoted itself').toBe(demotions);
      await expect.poll(async () => (await homeLease(kv, ledger.id)).value, { message: 'the lease names journey-1 at the next epoch', timeout: 60_000 })
        .toMatchObject({ home: 'gru', node: 'journey-1', epoch: before.value.epoch + 2 });
      expect(promoting(target, ledger.id), 'the promotion fence is lifted').toBe(false);
      await ledger.write(cells, 'gru', [[41, 'after-rollback-gru']]);
      await ledger.write(cells, 'fra', [[42, 'after-rollback-fra']]);
      await ledger.compare(cells);
      expect(await ledger.readNode(gru, token), 'the old source serves every acknowledged row').toEqual(expect.arrayContaining(ledger.acknowledged));
    });
    await removeDatabase(cells, ledger.id);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      kv.close();
      await restartAfterKill(gru, true, undefined, undefined, own)();
      expect(nodeBuild(gru), 'journey-1 runs this build again').toBe(own);
    });
  }
});

test('a lease that names a node that cannot serve leaves the database unserved, and the old home keeps its file without serving it', async ({ cells, request }) => {
  test.setTimeout(240_000);
  const token = await writerToken(request);
  const kv = await Kv.open();
  let failure: unknown;
  try {
    const [ledger] = await createLedgers(cells, 1, 'home-unserved');
    const before = await homeLease(kv, ledger.id);
    const gru = node('journey-1');
    const restart = restartAfterKill(gru, true, { token, path: `${project()}/databases` });
    try {
      await expect.poll(async () => await kv.get(homes, 'node.journey-1'), { message: 'journey-1 node lease expires', timeout: 30_000, intervals: [500] }).toBeNull();
      await kv.put(homes, `home.${ledger.id}`, { ...before.value, home: 'fra', node: 'journey-4', epoch: before.value.epoch + 1 }, before.seq);
      expect(holdsFile(gru, before.value)).toBe(true);
    } finally {
      await restart();
    }

    const hash = await test.step('the old home keeps its file in place, fenced, and serves nothing', async () => {
      await expect.poll(() => logged(gru, 'keeps its file in place and fenced', `"Database":"${ledger.id}"`), { message: 'journey-1 keeps its file in place', timeout: 60_000 }).toBe(1);
      expect(holdsFile(gru, before.value), 'the file stays in the database path').toBe(true);
      expect(await keptCopies(gru, ledger.id), 'nothing is set aside while no copy is proven').toEqual([]);
      return fileHash(gru, before.value);
    });

    await test.step('the database stays unserved, visibly', async () => {
      const unserved = { databaseId: ledger.id, region: 'fra', node: 'journey-4', epoch: before.value.epoch + 1, cause: 'not_writer' };
      for (const watcher of [node('journey-2'), node('journey-3')]) {
        await expect.poll(async () => (await nodeStatus(watcher)).unserved?.filter(entry => entry.databaseId === ledger.id), { message: `${watcher.name} lists the unserved database`, timeout: 60_000 })
          .toEqual([unserved]);
        expect(await counted(watcher, 'terrabase_home_unserved', { region: 'fra' })).toBeGreaterThanOrEqual(1);
        expect(logged(watcher, 'is unserved: its lease names', `"Database":"${ledger.id}"`, '"Node":"journey-4"'), 'one log line per change of state').toBe(1);
      }
      expect(await ledger.refusal(gru, token, [[600, 'while-unserved']]), 'the old home refuses the write').toEqual({ status: 503, code: 'unavailable' });
      expect((await homeLease(kv, ledger.id)).value, 'nothing claims the lease, and the old home does not take it back').toMatchObject({ home: 'fra', node: 'journey-4', epoch: before.value.epoch + 1 });
    });

    await test.step('the file in place stays byte-identical', async () => {
      await expect.poll(() => counted(gru, 'terrabase_home_unserved', { region: 'fra' }), { message: 'journey-1 scanned its leases again' }).toBeGreaterThanOrEqual(1);
      expect(fileHash(gru, before.value)).toBe(hash);
      expect(logged(gru, 'keeps its file in place and fenced', `"Database":"${ledger.id}"`), 'one log line per lease').toBe(1);
    });

    await test.step('deleting the unserved database through the API leaves nothing unserved behind', async () => {
      await removeDatabase(cells, ledger.id);
      for (const watcher of nodes) {
        await expect.poll(async () => (await nodeStatus(watcher)).unserved?.filter(entry => entry.databaseId === ledger.id) ?? [], { message: `${watcher.name} no longer lists the database` })
          .toEqual([]);
      }
    });
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      kv.close();
    });
  }
});

test('a revoked API key cannot serve a pending request body beyond its documented budget', async ({ cells, request }) => {
  const [ledger] = await createLedgers(cells, 1, 'revoked-work');
  const current = journey();
  const headers = { authorization: `Bearer ${await signIn()}` };
  const root = `${auth}/v1/terrascale/organizations/${current.organizationId}/projects/${current.projectId}/api-keys`;
  const created = await json<{ key: string }>(await request.post(root, { headers, data: { name: name('revocation-budget'), scopes: ['sql:read', 'sql:write'] } }), 201);
  saveSecret('revocation-budget-key', created.key);
  const grant = await json<{ access_token: string }>(await request.post(`${auth}/terrascale/oauth/token`, {
    form: { grant_type: 'client_credentials', client_id: keyId(created.key), client_secret: created.key, project: current.projectId, scope: 'sql:read sql:write' },
  }));
  saveSecret('revocation-budget-token', grant.access_token);
  const call = httpRequest(`http://127.0.0.1:${node('journey-1').port}${database(ledger.id)}/sql/execute`, {
    method: 'POST', headers: { authorization: `Bearer ${grant.access_token}`, 'content-type': 'application/json', expect: '100-continue', 'content-length': '1024' },
  });
  const answer = new Promise<{ status: number | undefined; body: string }>((resolve, reject) => {
    call.on('error', reject);
    call.on('response', response => {
      let body = '';
      response.on('data', data => { body += data; });
      response.on('error', reject);
      response.on('end', () => resolve({ status: response.statusCode, body }));
    });
  });
  try {
    const reading = once(call, 'continue');
    call.flushHeaders();
    await reading;
    const revoked = await request.delete(`${root}/${keyId(created.key)}`, { headers });
    expect(revoked.status()).toBe(204);
    const acknowledged = performance.now();
    const result = await answer;
    expect(result.status, result.body).toBe(401);
    expect(JSON.parse(result.body).error.code).toBe('key_revoked');
    expect(performance.now() - acknowledged, 'a definitive revocation cuts running work within the existing 2.5 s budget').toBeLessThanOrEqual(2500);
  } finally {
    call.destroy();
    await removeDatabase(cells, ledger.id);
  }
});

test('a regional writer role moves with its fence preserved across an outstanding commit check', async ({ cells, request }) => {
  const [ledger] = await createLedgers(cells, 1, 'regional-commit-race');
  const token = await writerToken(request);
  const kv = await Kv.open();
  const hook = await regionalHook(node('journey-1'));
  const { organizationId, projectId } = (await homeLease(kv, ledger.id)).value;
  try {
    expect(await hook.command('regional-writer-arm', organizationId, projectId, ledger.id)).toBe('ok');
    try {
      expect(await ledger.refusal(node('journey-1'), token, [[970, 'writer-check-before-demotion']])).toEqual({ status: 503, code: 'unavailable' });
      expect((await nodeStatus(node('journey-1'))).fenced ?? [], 'the status clears while the retired fence still refuses the commit').toEqual([]);
    } finally {
      expect(await hook.command('regional-release', organizationId, projectId, ledger.id)).toBe('ok');
    }
  } finally {
    kv.close();
    await hook.close();
  }
  await ledger.compare(cells);
  await removeDatabase(cells, ledger.id);
});

test('a regional writer role moves without publishing a revision during unproved activation', async ({ cells }) => {
  const [ledger] = await createLedgers(cells, 1, 'regional-publish-race');
  const kv = await Kv.open();
  const hook = await regionalHook(node('journey-1'));
  const { organizationId, projectId } = (await homeLease(kv, ledger.id)).value;
  try {
    expect(await hook.command('regional-activation-proof', organizationId, projectId, ledger.id)).toBe('suppressed');
  } finally {
    kv.close();
    await hook.close();
  }
  await ledger.compare(cells);
  await removeDatabase(cells, ledger.id);
});

test('a regional prime whose writer refuses its replicas answers at once with the home and its answer, the next one resumes, and a drain is admitted before it fences or refused with its reason', async ({ cells }) => {
  test.setTimeout(240_000);
  const kv = await Kv.open();
  const gru = node('journey-1');
  const partner = node('journey-3');
  const foreign = node('journey-2');
  const [ledger, fraLedger] = await createLedgers(cells, 2, 'prime-resume');
  const moved = await json<{ homeRegion: string }>(await cells.post(`${endpoint('gru')}${database(fraLedger.id)}/move-home`, { data: { homeRegion: 'fra' }, timeout: 120_000 }));
  expect(moved.homeRegion).toBe('fra');
  await fraLedger.write(cells, 'fra', [[100, 'homed-in-fra']]);
  await fraLedger.compare(cells);
  const refusal = (response: { status: number; body: string }) => {
    expect(response.status, response.body).toBe(503);
    return (JSON.parse(response.body) as { error: { code: string; details: Record<string, string> } }).error;
  };
  const transferOf = async () => (await kv.get<{ node: string; transfer: { id: string; phase: string } | null }>(owners, 'gru'))?.value;
  let blocked = false;
  const firewall = async (operation: '-I' | '-D') => {
    await promisify(execFile)('iptables', [operation, 'OUTPUT', '-d', gru.address, '-p', 'tcp', '--dport', '8081', '-j', 'REJECT', '--reject-with', 'tcp-reset']);
    blocked = operation === '-I';
  };
  let failure: unknown;
  try {
    expect(refusal(await nodeRequest(gru, `/v1/rollout/drain/admit?target=${foreign.name}&spec=journey-admit`, 'POST')), 'an admission to a node of another region names its reason')
      .toMatchObject({ code: 'target_not_admitted', details: { phase: 'admit', peer: gru.name } });
    expect((await transferOf())?.transfer ?? null, 'a refused admission reserves nothing').toBeNull();
    expect(refusal(await nodeRequest(gru, `/v1/rollout/drain?target=${foreign.name}`, 'POST')), 'a drain to a node of another region names its reason')
      .toMatchObject({ code: 'target_not_admitted', details: { phase: 'admit', peer: gru.name } });
    expect(refusal(await nodeRequest(gru, `/v1/rollout/drain?target=${partner.name}&transfer=never-admitted`, 'POST')), 'a drain that names no held admission is refused before it fences')
      .toMatchObject({ code: 'admission_unknown', details: { phase: 'reserve', peer: gru.name } });
    expect(await nodeStatus(gru), 'the refusals leave the writer serving').toMatchObject({ writer: true, ready: true });

    await ledger.write(cells, 'gru', [[100, 'pulled-after-the-block']]);
    await firewall('-I');
    const asked = Date.now();
    const unavailable = refusal(await nodeRequest(partner, `/v1/rollout/prime?source=${gru.name}`, 'POST'));
    const answeredIn = Date.now() - asked;
    await firewall('-D');
    expect(unavailable, 'a prime whose replicas the writer refuses names the home and its answer')
      .toMatchObject({ code: 'replica_unavailable', details: { phase: 'prime', peer: gru.name, home: 'gru', homeCode: 'unreachable' } });
    expect(Number(unavailable.details.databases), 'the refusal counts the replicas it could not pull').toBeGreaterThan(0);
    expect(answeredIn, 'the refusal comes well before the thirty-second prime budget').toBeLessThan(15_000);
    const resumed = await nodeRequest(partner, `/v1/rollout/prime?source=${gru.name}`, 'POST');
    expect(resumed.status, `the next prime resumes from the first one's progress and completes: ${resumed.body}`).toBe(204);

    const admit = `/v1/rollout/drain/admit?target=${partner.name}&spec=journey-admit`;
    const admitted = await nodeRequest(gru, admit, 'POST');
    expect(admitted.status, admitted.body).toBe(200);
    const { transfer } = JSON.parse(admitted.body) as { transfer: string };
    expect(JSON.parse((await nodeRequest(gru, admit, 'POST')).body), 'the same target and spec return the same admission').toEqual({ transfer });
    expect(await transferOf(), 'the admission reserves the transfer without moving the writer').toMatchObject({ node: gru.name, transfer: { id: transfer, phase: 'admitted' } });
    expect(await nodeStatus(gru), 'an admission fences nothing').toMatchObject({ writer: true, ready: true });
    await ledger.write(cells, 'gru', [[101, 'written-while-admitted']]);
    const drained = await nodeRequest(gru, `/v1/rollout/drain?target=${partner.name}&transfer=${transfer}`, 'POST');
    expect(drained.status, drained.body).toBe(204);
    await writerIs('gru', partner.name);
    await ledger.compare(cells);

    expect(refusal(await nodeRequest(partner, `/v1/rollout/drain/admit?target=${gru.name}&spec=journey-back`, 'POST')), 'the retired source must restart before it takes the role back')
      .toMatchObject({ code: 'source_retiring' });
    await restartAfterKill(gru, false)();
    const primed = await nodeRequest(gru, `/v1/rollout/prime?source=${partner.name}`, 'POST');
    expect(primed.status, primed.body).toBe(204);
    const back = await nodeRequest(partner, `/v1/rollout/drain?target=${gru.name}`, 'POST');
    expect(back.status, back.body).toBe(204);
    await writerIs('gru', gru.name);
    await ledger.write(cells, 'gru', [[102, 'after-the-drain-back']]);
    await ledger.compare(cells);
    await removeDatabase(cells, ledger.id);
    await removeDatabase(cells, fraLedger.id);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      if (blocked) await firewall('-D');
      const owner = await kv.get<Record<string, unknown> & { node: string; epoch: number }>(owners, 'gru');
      if (owner?.value.node !== gru.name) {
        await kv.put(owners, 'gru', { ...owner!.value, node: gru.name, epoch: 1, previous: null, activated: false, transfer: null }, owner!.seq);
        await writerIs('gru', gru.name);
      }
      kv.close();
    });
  }
});

test('a retired source whose fra replica cannot catch up hands the role back only while that replica\'s fra home is unserved, and the replica catches up once fra serves it again', async ({ cells, request }) => {
  test.setTimeout(300_000);
  const token = await writerToken(request);
  const kv = await Kv.open();
  const gru = node('journey-1');
  const partner = node('journey-3');
  const fra = [node('journey-2'), node('journey-4')];
  const refusal = (response: { status: number; body: string }) => {
    expect(response.status, response.body).toBe(503);
    return (JSON.parse(response.body) as { error: { code: string; details: Record<string, string> } }).error;
  };
  const prime = () => nodeRequest(gru, `/v1/rollout/prime?source=${partner.name}`, 'POST');
  const admit = () => nodeRequest(partner, `/v1/rollout/drain/admit?target=${gru.name}&spec=journey-unserved`, 'POST');
  let lease: { key: string; served: { value: HomeLease } } | undefined;
  let stopRefusing: (() => Promise<void>) | undefined;
  let restartPartner: (() => Promise<void>) | undefined;
  const created: string[] = [];
  let failure: unknown;
  try {
    await restartAfterKill(partner, false)();
    const [ledger] = await createLedgers(cells, 1, 'unserved-retiring');
    created.push(ledger.id);
    const homedInFra = await json<{ id: string; homeRegion: string }>(await cells.post(`${endpoint('fra')}${project()}/databases`, {
      data: { name: name('unserved-retiring-fra'), homeRegion: 'fra', replicaRegions: ['gru'] },
    }), 201);
    created.push(homedInFra.id);
    expect(homedInFra.homeRegion, 'a database created in fra is homed there').toBe('fra');
    const fraLedger = new Ledger(homedInFra.id);
    await sql(cells, endpoint('fra'), homedInFra.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await sql(cells, endpoint('fra'), homedInFra.id, 'CREATE TABLE local_rows (id TEXT PRIMARY KEY, value TEXT NOT NULL)');
    await sql(cells, endpoint('fra'), homedInFra.id, "INSERT INTO local_rows (id, value) VALUES ('acknowledged', 'homed-in-fra')");
    await fraLedger.write(cells, 'fra', [[100, 'homed-in-fra']]);
    await fraLedger.compare(cells);
    const leaseKey = `home.${fraLedger.id}`;
    const served = await homeLease(kv, fraLedger.id);
    lease = { key: leaseKey, served };
    const fraWriter = fra.find(member => member.name === served.value.node)!;
    const unservedLease = { ...served.value, node: fra.find(member => member !== fraWriter)!.name };
    const heldRoute = `/v1/sync/organizations/${served.value.organizationId}/projects/${served.value.projectId}/databases/${fraLedger.id}/held`;
    const held = async (member: Node) => (JSON.parse((await nodeRequest(member, heldRoute)).body) as { revision?: string }).revision;
    const sameHeld = async () => { const [mine, theirs] = await Promise.all([held(gru), held(partner)]); return mine !== undefined && mine === theirs; };
    await expect.poll(sameHeld, { message: 'journey-3 holds what journey-1 holds of the fra database', timeout: 60_000 }).toBe(true);
    const primedPartner = await nodeRequest(partner, `/v1/rollout/prime?source=${gru.name}`, 'POST');
    expect(primedPartner.status, primedPartner.body).toBe(204);
    const away = await nodeRequest(gru, `/v1/rollout/drain?target=${partner.name}`, 'POST');
    expect(away.status, away.body).toBe(204);
    await writerIs('gru', partner.name);
    await restartAfterKill(gru, false)();
    const beforeRestartWrite = await held(partner);
    await fraLedger.write(cells, 'fra', [[99, 'pulled-by-the-restarted-journey-1']]);
    await expect.poll(async () => { const [mine, theirs] = await Promise.all([held(gru), held(partner)]); return theirs !== beforeRestartWrite && mine === theirs; },
      { message: 'the restarted journey-1 follows the fra database and pulls its new row', timeout: 60_000 }).toBe(true);
    const caughtUp = await prime();
    expect(caughtUp.status, `journey-1 catches every replica up before the fault: ${caughtUp.body}`).toBe(204);
    expect(caughtUp.headers['terrabase-primed-without'], 'a prime with every home served goes without nothing').toBeUndefined();
    const behind = await held(gru);

    stopRefusing = await refuseConnections(gru, fra);
    await fraLedger.write(cells, 'fra', [[101, 'missed-by-journey-1']]);
    await expect.poll(() => held(partner), { message: 'journey-3 pulls the new fra row that journey-1 cannot' }).not.toBe(behind);
    expect(refusal(await prime()), 'a prime refuses a replica whose fra home is served but cannot be pulled')
      .toMatchObject({ code: 'replica_unavailable', details: { phase: 'prime', peer: partner.name, home: 'fra' } });
    expect(refusal(await admit()), 'so the retired source still waits for its catch-up')
      .toMatchObject({ code: 'source_retiring', details: { peer: gru.name, waiting: 'catch_up' } });

    const unserve = async () => {
      await kv.put(homes, leaseKey, unservedLease, (await homeLease(kv, fraLedger.id)).seq);
      await expect.poll(async () => (await nodeStatus(gru)).unserved?.filter(entry => entry.databaseId === fraLedger.id && entry.node === unservedLease.node).map(entry => entry.cause) ?? [],
        { message: 'journey-1 lists the fra database as unserved', timeout: 60_000 }).toEqual(['not_writer']);
    };
    await unserve();
    await stopRefusing();
    stopRefusing = await refuseConnections(gru, fra, '3s');
    const racing = prime();
    await kv.put(homes, leaseKey, served.value, (await homeLease(kv, fraLedger.id)).seq);
    expect(refusal(await racing), 'a home served again while the prime waits is caught up, not skipped')
      .toMatchObject({ code: 'replica_unavailable', details: { phase: 'prime', home: 'fra' } });
    await stopRefusing();
    stopRefusing = await refuseConnections(gru, fra);

    await unserve();
    const unique = `gru-local-${randomBytes(6).toString('hex')}`;
    const onNode = (member: Node, statement: string) => fetch(`http://127.0.0.1:${member.port}${database(fraLedger.id)}/sql/execute`, {
      method: 'POST', headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
      body: JSON.stringify({ statements: [{ sql: statement }] }), signal: AbortSignal.timeout(15_000),
    });
    const acknowledged = async (member: Node) => {
      const response = await onNode(member, "SELECT value FROM local_rows WHERE id = 'acknowledged'");
      const body = await response.json() as { results?: { rows: [string][] }[] };
      return response.status === 200 && response.headers.get('x-terrabase-region') === member.region ? body.results?.[0]?.rows[0]?.[0] : undefined;
    };
    const local = await onNode(partner, `UPDATE local_rows SET value = '${unique}' WHERE id = 'acknowledged'`);
    expect(local.status, await local.clone().text()).toBe(200);
    expect(local.headers.get('x-terrabase-region')).toBe('gru');
    expect(local.headers.get('terrascale-write-route'), 'journey-3, the gru writer, acknowledges the fra database write locally').toContain('commit=local');
    expect(await counted(gru, 'terrabase_localack_held'), 'journey-1 holds the locally acknowledged transaction for its partner').toBeGreaterThanOrEqual(1);
    expect(await acknowledged(fraWriter), 'fra has not applied it').toBe('homed-in-fra');

    const primed = await prime();
    expect(primed.status, `a prime completes without a replica whose fra home is unserved: ${primed.body}`).toBe(204);
    expect(primed.headers['terrabase-primed-without'], 'the prime names the replica it went without').toBe(fraLedger.id);
    expect(primed.headers['terrabase-primed-without-count']).toBe('1');
    expect((await nodeStatus(gru)).primedWithout, 'the rollout status names it too').toEqual([fraLedger.id]);
    expect(await held(gru), 'journey-1 still lacks the fra position journey-3 holds').not.toBe(await held(partner));

    const admitted = await admit();
    expect(admitted.status, `the retired source clears its retirement: ${admitted.body}`).toBe(200);
    const { transfer } = JSON.parse(admitted.body) as { transfer: string };
    const back = await nodeRequest(partner, `/v1/rollout/drain?target=${gru.name}&transfer=${transfer}`, 'POST');
    expect(back.status, `the drain does not wait for the fra position journey-1 cannot pull: ${back.body}`).toBe(204);
    await writerIs('gru', gru.name);
    expect(logged(gru, 'does not wait for database', fraLedger.id, 'preparation'), 'the preparation names the position it went without').toBeGreaterThanOrEqual(1);
    await ledger.write(cells, 'gru', [[102, 'after-the-drain-back']]);
    await ledger.compare(cells);

    restartPartner = restartAfterKill(partner, false);
    await stopRefusing();
    await kv.put(homes, leaseKey, served.value, (await homeLease(kv, fraLedger.id)).seq);
    await expect.poll(async () => (await nodeStatus(fraWriter)).unserved?.filter(entry => entry.databaseId === fraLedger.id) ?? [],
      { message: `${fraWriter.name} serves the fra database again`, timeout: 60_000 }).toEqual([]);
    for (const reader of [gru, fraWriter]) {
      await expect.poll(() => acknowledged(reader),
        { message: `${reader.name} reads the write journey-3 acknowledged locally, without journey-3 and without a resubmission`, timeout: 60_000 }).toBe(unique);
    }
    await expect.poll(async () => (await fraLedger.readNode(gru, token)).find(([id]) => id === 101), { message: 'journey-1 reads the fra row it missed' })
      .toEqual([101, 'missed-by-journey-1']);
    await restartPartner();
    restartPartner = undefined;
    while (created.length > 0) await removeDatabase(cells, created.pop()!);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      await stopRefusing?.();
      await restartPartner?.();
      const current = lease ? await kv.get<HomeLease>(homes, lease.key) : null;
      if (lease && current && current.value.node !== lease.served.value.node) {
        await kv.put(homes, lease.key, lease.served.value, current.seq);
      }
      const owner = await kv.get<Record<string, unknown> & { node: string; epoch: number }>(owners, 'gru');
      if (owner?.value.node !== gru.name) {
        await kv.put(owners, 'gru', { ...owner!.value, node: gru.name, epoch: 1, previous: null, activated: false, transfer: null }, owner!.seq);
        await writerIs('gru', gru.name);
      }
      while (created.length > 0) await removeDatabase(cells, created.pop()!);
      kv.close();
    });
  }
});

test('a regional writer role moves only with the target certificate, and a database whose primary file is stale stays fenced while the region serves the others', async ({ cells, request }) => {
  test.setTimeout(480_000);
  const token = await writerToken(request);
  const kv = await Kv.open();
  const [ledger] = await createLedgers(cells, 1, 'writer-proof');
  const gru = node('journey-1');
  const partner = node('journey-3');
  const moveHome = async (from: string, to: string) => {
    const moved = await json<{ homeRegion: string }>(await cells.post(`${endpoint(from)}${database(ledger.id)}/move-home`, { data: { homeRegion: to }, timeout: 120_000 }));
    expect(moved.homeRegion).toBe(to);
    await ledger.compare(cells);
  };
  await moveHome('gru', 'fra');
  await moveHome('fra', 'gru');
  const adopted = (await homeLease(kv, ledger.id)).value;
  expect(adopted, 'a planned move back to journey-1 records the position it adopted').toMatchObject({ home: 'gru', node: 'journey-1', adopted: expect.any(String) });
  const keptBefore = (await nodeStatus(gru)).kept ?? [];
  const hashes = new Map(keptBefore.map(copy => [copy.id, keptHash(gru, copy)]));
  const keptUnchanged = async () => {
    const now = new Map(((await nodeStatus(gru)).kept ?? []).map(copy => [copy.id, copy]));
    for (const [id, hash] of hashes) expect(keptHash(gru, now.get(id)!), `kept copy ${id} on journey-1 is byte-identical`).toBe(hash);
  };
  let failure: unknown;
  try {
    const owner = await kv.get<Record<string, unknown> & { node: string; epoch: number }>(owners, 'gru');
    expect(owner?.value.node).toBe('journey-1');
    const refusals = await counted(partner, 'terrabase_home_refusals_total', { scope: 'writer' });
    await kv.put(owners, 'gru', {
      ...owner!.value, node: 'journey-3', epoch: owner!.value.epoch + 1, previous: 'journey-1', activated: true, transfer: null, version: 2,
      prepared: { source: 'journey-1', target: 'journey-3', epoch: owner!.value.epoch, operation: null },
    }, owner!.seq);
    await expect.poll(() => counted(partner, 'terrabase_home_refusals_total', { scope: 'writer', reason: 'unproven' }), { message: 'journey-3 refuses the rewritten owner record' })
      .toBeGreaterThan(refusals);
    expect((await nodeStatus(partner)).writer).toBe(false);
    const rewritten = await kv.get<{ node: string }>(owners, 'gru');
    await kv.remove(owners, 'gru', rewritten!.seq);
    await writerIs('gru', 'journey-1');
    await expect.poll(async () => (await kv.get<{ node: string; epoch: number }>(owners, 'gru'))?.value, { message: 'journey-1 writes a new owner record at epoch 1' })
      .toMatchObject({ node: 'journey-1', epoch: 1 });
    console.log('rewritten owner record refused; a deleted key brought journey-1 back as writer at epoch 1 with current primary files');

    const drained = await nodeRequest(gru, '/v1/rollout/drain?target=journey-3', 'POST');
    expect(drained.status, drained.body).toBe(204);
    await writerIs('gru', 'journey-3');
    await expect.poll(async () => (await homeLease(kv, ledger.id)).value, { message: 'the new writer\'s lease carries the adopted position' })
      .toMatchObject({ node: 'journey-3', adopted: adopted.adopted });
    await ledger.write(cells, 'gru', [[800, 'written-on-journey-3']]);
    await ledger.compare(cells);
    const lease = (await homeLease(kv, ledger.id)).value;
    await expect.poll(async () => (await nodeRequest(gru, `/v1/sync/organizations/${lease.organizationId}/projects/${lease.projectId}/databases/${ledger.id}/held`)).status,
      { message: 'journey-1 holds a replica newer than its stale primary file' }).toBe(200);
    const stale = await counted(gru, 'terrabase_home_refusals_total', { scope: 'database' });
    const current = await kv.get<{ node: string }>(owners, 'gru');
    await kv.remove(owners, 'gru', current!.seq);
    await expect.poll(() => counted(gru, 'terrabase_home_refusals_total', { scope: 'database' }), { message: 'journey-1 fences the database whose primary file is stale' })
      .toBeGreaterThan(stale);
    const recreated = await kv.get<Record<string, unknown> & { node: string; epoch: number }>(owners, 'gru');
    expect(recreated?.value.node).toBe('journey-1');
    await writerIs('gru', 'journey-1');
    const fenced = (await nodeStatus(gru)).fenced ?? [];
    expect(fenced.filter(entry => entry.databaseId === ledger.id), 'journey-1 names the database whose replica it cannot prove its primary file covers')
      .toEqual([expect.objectContaining({ reason: expect.stringMatching(/^(replica_newer|position_unknown)$/), epoch: recreated!.value.epoch })]);
    expect(await counted(gru, 'terrabase_regional_fenced'), 'every fenced database is counted while it lasts').toBe(fenced.length);
    expect(await ledger.refusal(gru, token, [[803, 'while-fenced']]), 'the fenced database refuses the write on the region writer').toEqual({ status: 503, code: 'unavailable' });
    const regional = new Ledger((await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
      data: { name: name('writer-proof-regional'), regions: [...regions] },
    }), 201)).id);
    await sql(cells, endpoint('gru'), regional.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await regional.write(cells, 'gru', [[1, 'while-another-is-fenced']]);
    expect(await regional.readNode(gru, token), 'another database of the region accepts writes on the same writer').toEqual(regional.acknowledged);
    expect(((await nodeStatus(gru)).fenced ?? []).map(entry => entry.databaseId), 'the ledger stays fenced while the other database is served').toContain(ledger.id);
    await removeDatabase(cells, regional.id);
    const primed = await nodeRequest(partner, '/v1/rollout/prime?source=journey-1', 'POST');
    expect(primed.status, primed.body).toBe(503);
    expect(JSON.parse(primed.body).error, 'journey-3 cannot read its replica of the fenced ledger from journey-1 and names the prime as incomplete when its budget runs out')
      .toMatchObject({ code: 'prime_incomplete', details: { phase: 'prime', peer: 'journey-1' } });
    await kv.put(owners, 'gru', { ...recreated!.value, node: 'journey-3', epoch: 1, previous: null, activated: false, transfer: null }, recreated!.seq);
    await writerIs('gru', 'journey-3');
    expect((await nodeStatus(partner)).fenced ?? [], 'journey-3 holds the lease with a primary file that covers its adopted position, so its unreadable replica fences nothing').toEqual([]);
    expect((await nodeStatus(gru)).fenced ?? [], 'the status clears when this node retires its fenced primary').toEqual([]);
    expect(await counted(gru, 'terrabase_regional_fenced')).toBe(0);
    await ledger.compare(cells, [[800, 'written-on-journey-3']]);
    const back = await nodeRequest(partner, '/v1/rollout/drain?target=journey-1', 'POST');
    expect(back.status, back.body).toBe(204);
    await writerIs('gru', 'journey-1');
    await ledger.write(cells, 'gru', [[801, 'after-proofs']]);
    await ledger.compare(cells);
    await expect.poll(async () => (await homeLease(kv, ledger.id)).value, { message: 'the lease carries the adopted position back to journey-1' })
      .toMatchObject({ node: 'journey-1', adopted: adopted.adopted });
    const prepared = (await keptCopies(gru, ledger.id)).filter(copy => !hashes.has(copy.id));
    expect(prepared, 'the drain back set journey-1\'s earlier primary file aside instead of deleting it').toHaveLength(1);
    hashes.set(prepared[0].id, keptHash(gru, prepared[0]));
    await keptUnchanged();
    console.log(`journey-1 keeps ${hashes.size} copies through the handoff prepare`);

    await moveHome('gru', 'fra');
    await moveHome('fra', 'gru');
    await keptUnchanged();
    const moved = (await homeLease(kv, ledger.id)).value;
    expect(moved).toMatchObject({ home: 'gru', node: 'journey-1', adopted: expect.any(String) });
    const restart = restartAfterKill(gru);
    await restart();
    await expect.poll(async () => (await homeLease(kv, ledger.id)).value, { message: 'the restarted writer\'s activation takes the lease at the next epoch and keeps the adopted position', timeout: 60_000 })
      .toMatchObject({ node: 'journey-1', home: 'gru', epoch: moved.epoch + 1, adopted: moved.adopted });
    expect((await homeLease(kv, ledger.id)).value.session, 'journey-1 holds the lease under its new session').not.toBe(moved.session);
    await keptUnchanged();
    await ledger.write(cells, 'gru', [[802, 'after-restart']]);
    await ledger.compare(cells);
    await removeDatabase(cells, ledger.id);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      kv.close();
    });
  }
});

for (const firstPull of ['succeeds', 'fails'] as const) {
  test(`a regional writer role moves onto a torn replica whose first pull ${firstPull}: its set-aside copy keeps stale primary reads and writes fenced until a drain proves the primary covers it`, async ({ cells, request }) => {
    const token = await writerToken(request);
    const kv = await Kv.open();
    const gru = node('journey-1');
    const partner = node('journey-3');
    await restartAfterKill(partner, false)();
    const [ledger] = await createLedgers(cells, 1, 'torn-replica');
    const drain = async (source: Node, target: Node) => {
      const primed = await nodeRequest(target, `/v1/rollout/prime?source=${source.name}`, 'POST');
      expect(primed.status, `${target.name}, restarted after it last gave up the writer role, catches up before it takes it: ${primed.body}`).toBe(204);
      const drained = await nodeRequest(source, `/v1/rollout/drain?target=${target.name}`, 'POST');
      expect(drained.status, drained.body).toBe(204);
      await writerIs('gru', target.name);
    };
    let blocked = false;
    let foreignLease: HomeLease | undefined;
    let failure: unknown;
    const firewall = async (operation: '-I' | '-D') => {
      await promisify(execFile)('iptables', [operation, 'OUTPUT', '-d', gru.address, '-p', 'tcp', '--dport', '8081', '-j', 'REJECT']);
      blocked = operation === '-I';
    };
    try {
      await drain(gru, partner);
      const lease = (await homeLease(kv, ledger.id)).value;
      const route = `/v1/sync/organizations/${lease.organizationId}/projects/${lease.projectId}/databases/${ledger.id}`;
      const replicas = (owner: Node) => join(stateDir, owner.service, '.replicas', lease.organizationId, lease.projectId);
      const setAside = (owner: Node) => readdirSync(replicas(owner)).filter(file => file.startsWith(`${ledger.id}.db.retained.`));
      const archived = (owner: Node) => readdirSync(join(replicas(owner), '.retained')).flatMap(archive => readdirSync(join(replicas(owner), '.retained', archive)));
      const position = async (writer: Node) => JSON.parse((await nodeRequest(writer, `${route}/checksums?revision=ts1:00000000000000000000000000:1:0`)).body).revision as string;
      const holds = async (owner: Node, writer: Node, message = `${owner.name} holds ${writer.name}'s current position in its replica`) => {
        await expect.poll(async () => {
          const written = await position(writer);
          const response = await nodeRequest(owner, `${route}/held`);
          return response.status === 200 && JSON.parse(response.body).revision === written;
        }, { message }).toBe(true);
      };
      await holds(gru, partner);
      await ledger.compare(cells);
      const torn = restartAfterKill(gru, false, undefined, async () => {
        await expect.poll(() => setAside(gru).filter(file => /\.db\.retained\.[A-Z0-9]+$/.test(file)), { message: 'journey-1 sets aside its torn replica when it restarts' }).not.toEqual([]);
      });
      truncateSync(join(replicas(gru), `${ledger.id}.db-info`), 0);
      await torn();
      const tornAside = setAside(gru);
      expect(tornAside, 'journey-1 keeps its set-aside replica database and torn info after the restart').toEqual(expect.arrayContaining([
        expect.stringMatching(/\.db\.retained\.[A-Z0-9]+$/), expect.stringMatching(/\.db\.retained\.[A-Z0-9]+-info$/)]));
      await drain(partner, gru);
      expect(((await nodeStatus(gru)).fenced ?? []).filter(entry => entry.databaseId === ledger.id), 'the drain proves journey-1\'s primary file covers the source writer\'s certified position, so it serves the database')
        .toEqual([]);
      expect(setAside(gru), 'the proven set-aside copy leaves the replica path').toEqual([]);
      expect(archived(gru), 'the proven set-aside copy is archived, not deleted').toEqual(expect.arrayContaining(tornAside));
      await expect.poll(() => counted(gru, 'terrabase_storage_retained_files'), { message: 'the retained inventory counts the archived copy' })
        .toBeGreaterThanOrEqual(tornAside.length);
      await ledger.write(cells, 'gru', [[800, 'newer-than-journey-3-primary']]);
      await ledger.compare(cells);
      await holds(partner, gru, 'journey-3 holds the acknowledged row missing from its primary file');
      const directory = replicas(partner);
      const replica = join(directory, `${ledger.id}.db`);
      const restarted = restartAfterKill(partner, true, undefined, async () => {
        await expect.poll(() => readdirSync(directory).filter(file => file.startsWith(`${ledger.id}.db.retained.`) && !file.endsWith('-info') && !file.endsWith('-wal') && !file.includes('.key')),
          { message: 'the failed reopen sets aside the original replica database' }).not.toEqual([]);
        const owner = await kv.get<Record<string, unknown> & { node: string; epoch: number }>(owners, 'gru');
        await kv.put(owners, 'gru', { ...owner!.value, node: partner.name, epoch: 1, previous: null, activated: false, transfer: null }, owner!.seq);
      });
      truncateSync(replica + '-info', 0);
      if (firstPull === 'fails') await firewall('-I');
      await restarted();
      const retained = readdirSync(directory).filter(file => file.startsWith(`${ledger.id}.db.retained.`));
      expect(retained, 'recovery preserves the set-aside database and torn info even after a failed first pull').toContainEqual(expect.stringMatching(/\.db\.retained\.[A-Z0-9]+$/));
      expect(retained).toContainEqual(expect.stringMatching(/\.db\.retained\.[A-Z0-9]+-info$/));
      if (blocked) await firewall('-D');
      await expect.poll(async () => ((await nodeStatus(partner)).fenced ?? []).filter(entry => entry.databaseId === ledger.id),
        { message: 'a replacement replica cannot disprove the set-aside copy' }).toEqual([expect.objectContaining({ reason: 'position_unknown' })]);
      expect(await ledger.readRefusal(partner, token)).toEqual(homeDown);
      expect(await ledger.refusal(partner, token, [[803, 'unproven-write']])).toEqual(homeDown);
      expect(readdirSync(directory), 'the set-aside evidence survives writer activation and requests').toEqual(expect.arrayContaining(retained));
      if (firstPull === 'succeeds') {
        const before = await homeLease(kv, ledger.id);
        foreignLease = before.value;
        const kept = logged(partner, 'keeps its file in place and fenced', ledger.id);
        for (const [epoch, seen] of [[before.value.epoch + 1, kept], [before.value.epoch + 2, kept + 1]]) {
          const current = await homeLease(kv, ledger.id);
          await kv.put(homes, `home.${ledger.id}`, { ...before.value, home: 'fra', node: 'journey-2', epoch, adopted: null }, current.seq);
          await expect.poll(() => logged(partner, 'keeps its file in place and fenced', ledger.id),
            { message: 'only a database that is still fenced is checked against the changed foreign lease again' }).toBeGreaterThan(seen);
        }
        expect(((await nodeStatus(partner)).fenced ?? []).filter(entry => entry.databaseId === ledger.id), 'keeping a file in place does not lift its existing fence')
          .toEqual([expect.objectContaining({ reason: 'position_unknown' })]);
        expect(await ledger.readRefusal(partner, token)).toEqual(homeDown);
        const foreign = await homeLease(kv, ledger.id);
        await kv.put(homes, `home.${ledger.id}`, { ...before.value, epoch: foreign.value.epoch + 1 }, foreign.seq);
        foreignLease = undefined;
      }
      expect(readdirSync(directory).filter(file => file.startsWith(`${ledger.id}.db.retained.`)), 'the set-aside copy holding row 800, which journey-3\'s primary file lacks, stays in place without a drain')
        .toEqual(expect.arrayContaining(retained));
      const owner = await kv.get<Record<string, unknown> & { node: string; epoch: number }>(owners, 'gru');
      await kv.put(owners, 'gru', { ...owner!.value, node: gru.name, epoch: 1, previous: null, activated: false, transfer: null }, owner!.seq);
      await writerIs('gru', gru.name);
      expect(((await nodeStatus(gru)).fenced ?? []).filter(entry => entry.databaseId === ledger.id), 'journey-3 served no pull of its unchecked primary file, so journey-1 holds nothing to fence')
        .toEqual([]);
      if (firstPull === 'fails') {
        await drain(gru, partner);
        expect(((await nodeStatus(partner)).fenced ?? []).filter(entry => entry.databaseId === ledger.id), 'the drain proves journey-3\'s primary file covers journey-1\'s certified position, so the fence lifts')
          .toEqual([]);
        expect(setAside(partner), 'journey-3\'s proven set-aside copy leaves the replica path').toEqual([]);
        expect(archived(partner), 'journey-3\'s proven set-aside copy is archived, not deleted').toEqual(expect.arrayContaining(retained));
        await ledger.write(cells, 'gru', [[801, 'after-the-proving-drain']]);
        await ledger.compare(cells);
      }
      await removeDatabase(cells, ledger.id);
    } catch (error) {
      failure = error;
      throw error;
    } finally {
      await cleanUp(failure, async () => {
        if (blocked) await firewall('-D');
        if (foreignLease) {
          const foreign = await homeLease(kv, ledger.id);
          await kv.put(homes, `home.${ledger.id}`, { ...foreignLease, epoch: foreign.value.epoch + 1 }, foreign.seq);
        }
        const owner = await kv.get<Record<string, unknown> & { node: string; epoch: number }>(owners, 'gru');
        if (owner?.value.node !== gru.name) {
          await kv.put(owners, 'gru', { ...owner!.value, node: gru.name, epoch: 1, previous: null, activated: false, transfer: null }, owner!.seq);
          await writerIs('gru', gru.name);
        }
        kv.close();
      });
    }
  });
}

test('a writer that starts with a local replica it has not opened fences that database and serves no stale file until the replica is proven', async ({ cells, request }) => {
  test.setTimeout(480_000);
  const token = await writerToken(request);
  const kv = await Kv.open();
  const [ledger] = await createLedgers(cells, 1, 'cold-replica');
  const gru = node('journey-1');
  const partner = node('journey-3');
  const moveHome = async (from: string, to: string) => {
    const moved = await json<{ homeRegion: string }>(await cells.post(`${endpoint(from)}${database(ledger.id)}/move-home`, { data: { homeRegion: to }, timeout: 120_000 }));
    expect(moved.homeRegion).toBe(to);
    await ledger.compare(cells);
  };
  await moveHome('gru', 'fra');
  await moveHome('fra', 'gru');
  let failure: unknown;
  try {
    await restartAfterKill(partner, false)();
    const primed = await nodeRequest(partner, '/v1/rollout/prime?source=journey-1', 'POST');
    expect(primed.status, 'journey-3 catches up before it takes the writer role').toBe(204);
    const drained = await nodeRequest(gru, '/v1/rollout/drain?target=journey-3', 'POST');
    expect(drained.status, drained.body).toBe(204);
    await writerIs('gru', 'journey-3');
    await ledger.write(cells, 'gru', [[800, 'acknowledged-on-journey-3']]);
    await ledger.compare(cells);
    const lease = (await homeLease(kv, ledger.id)).value;
    const route = `/v1/sync/organizations/${lease.organizationId}/projects/${lease.projectId}/databases/${ledger.id}`;
    const current = JSON.parse((await nodeRequest(partner, `${route}/checksums?revision=ts1:00000000000000000000000000:1:0`)).body).revision as string;
    const covers = (held: string) => {
      const [, incarnation, generation, frame] = held.split(':');
      const [, wanted, wantedGeneration, wantedFrame] = current.split(':');
      return incarnation === wanted && (+generation > +wantedGeneration || +generation === +wantedGeneration && +frame >= +wantedFrame);
    };
    await expect.poll(async () => {
      const held = await nodeRequest(gru, `${route}/held`);
      return held.status === 200 && typeof JSON.parse(held.body).revision === 'string' && covers(JSON.parse(held.body).revision);
    }, { message: 'journey-1\'s replica holds the row its primary file lacks' }).toBe(true);
    const held = JSON.parse((await nodeRequest(gru, `${route}/held`)).body).revision as string;
    const replicaFile = (holder: Node) => existsSync(join(stateDir, holder.service, '.replicas', lease.organizationId, lease.projectId, `${ledger.id}.db`));

    const restartGru = restartAfterKill(gru, true, { token, path: database(ledger.id) });
    expect(replicaFile(gru), 'journey-1 keeps its replica file on disk').toBe(true);
    const owner = await kv.get<Record<string, unknown> & { node: string; epoch: number }>(owners, 'gru');
    await kv.put(owners, 'gru', { ...owner!.value, node: 'journey-1', epoch: 1, previous: null, activated: false, transfer: null }, owner!.seq);
    await expect.poll(async () => (await nodeStatus(partner)).writer, { message: 'journey-3 steps down for the owner record that names journey-1' }).toBe(false);
    await restartGru();

    await test.step('journey-1 starts as the gru writer: the database whose replica it has not opened is fenced and refuses writes and peers', async () => {
      expect(((await nodeStatus(gru)).fenced ?? []).filter(entry => entry.databaseId === ledger.id), 'the unopened replica fences its database')
        .toEqual([expect.objectContaining({ reason: 'position_unknown', epoch: 1 })]);
      expect((await nodeRequest(gru, `${route}/checksums?revision=${encodeURIComponent(held)}`)).status, 'the stale primary file is not offered to peers').toBe(503);
      expect(await ledger.readRefusal(gru, token), 'the fenced database refuses a SQL read instead of serving the primary file that lacks row 800').toEqual(homeDown);
      const wire = await postgres(`${lease.projectId}/${ledger.id}`, journey().apiKey, gru.postgres);
      try {
        expect(wire.authenticated, 'the PostgreSQL session opens on journey-1').toEqual([]);
        expect(await wire.query('SELECT id, value FROM fault_rows ORDER BY id'), 'the fenced database refuses a PostgreSQL read with the retryable SQLSTATE').toEqual(['57P03']);
      } finally {
        wire.close();
      }
      expect(await ledger.refusal(gru, token, [[803, 'while-fenced']]), 'the fenced database refuses the write').toEqual(homeDown);
    });

    await test.step('writer renewals keep the fence without proof, and it clears once journey-3, which holds the row, is the writer', async () => {
      const fencedAt = await counted(gru, 'terrabase_regional_fenced');
      expect(fencedAt, 'the fenced database is counted').toBeGreaterThan(0);
      expect(((await nodeStatus(gru)).fenced ?? []).map(entry => entry.databaseId)).toContain(ledger.id);
      expect((await nodeRequest(gru, `${route}/checksums?revision=${encodeURIComponent(held)}`)).status).toBe(503);
      const named = await kv.get<Record<string, unknown> & { node: string; epoch: number }>(owners, 'gru');
      expect(named?.value.node).toBe('journey-1');
      await kv.put(owners, 'gru', { ...named!.value, node: 'journey-3', epoch: 1, previous: null, activated: false, transfer: null }, named!.seq);
      await writerIs('gru', 'journey-3');
      await expect.poll(async () => (await nodeStatus(partner)).fenced ?? [], { message: 'journey-3 fences nothing' }).toEqual([]);
      await expect.poll(async () => (await nodeStatus(gru)).fenced ?? [], { message: 'the fence clears once the owner record names another node' }).toEqual([]);
      await ledger.compare(cells);
      expect(await ledger.readNode(partner, token), 'the row acknowledged on journey-3 is served').toContainEqual([800, 'acknowledged-on-journey-3']);
    });

    const back = await nodeRequest(partner, '/v1/rollout/drain?target=journey-1', 'POST');
    expect(back.status, back.body).toBe(204);
    await writerIs('gru', 'journey-1');
    await ledger.write(cells, 'gru', [[801, 'after-the-proof']]);
    await ledger.compare(cells);
    await removeDatabase(cells, ledger.id);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      kv.close();
    });
  }
});

test('a gru node that is not its region writer pulls a fra-homed replica after a restart that finds its home lease lapsed, and takes the fra writer\'s placement of a home moved to fra, also when fra is down at its restart and when the move leaves gru', async ({ cells, request }) => {
  test.setTimeout(420_000);
  const kv = await Kv.open();
  const gru = node('journey-1');
  const partner = node('journey-3');
  const created: string[] = [];
  const pending: (() => Promise<void>)[] = [];
  let failure: unknown;
  const route = async (id: string) => {
    const { value } = await homeLease(kv, id);
    return `/v1/sync/organizations/${value.organizationId}/projects/${value.projectId}/databases/${id}`;
  };
  const writerOf = async (id: string) => node((await homeLease(kv, id)).value.node);
  const reaches = (held: string, written: string) => {
    const [prefix, incarnation, generation, frame] = held.split(':');
    const [writtenPrefix, writtenIncarnation, writtenGeneration, writtenFrame] = written.split(':');
    return prefix === writtenPrefix && incarnation === writtenIncarnation
      && (Number(generation) > Number(writtenGeneration) || Number(generation) === Number(writtenGeneration) && Number(frame) >= Number(writtenFrame));
  };
  const holds = async (id: string, message: string) => {
    const path = await route(id);
    const writer = await writerOf(id);
    const answer = await nodeRequest(writer, `${path}/checksums?revision=ts1:00000000000000000000000000:1:0`);
    expect(answer.status, `${writer.name} answers its position: ${answer.body}`).toBe(200);
    const written = (JSON.parse(answer.body) as { revision: string }).revision;
    await expect.poll(async () => {
      const response = await nodeRequest(gru, `${path}/held`);
      return response.status === 200 && reaches((JSON.parse(response.body) as { revision: string }).revision, written);
    }, { message: `journey-1 holds ${writer.name}'s position ${written} ${message}`, timeout: 60_000 }).toBe(true);
  };
  const placement = async (member: Node, id: string) => {
    const response = await nodeRequest(member, `${await route(id)}/placement`);
    expect(response.status, `${member.name} answers the placement as its region writer: ${response.body}`).toBe(200);
    const { homeRegion, replicaRegions, placementVersion, allRegions } = JSON.parse(response.body) as Record<string, unknown>;
    return { homeRegion, replicaRegions, placementVersion, allRegions };
  };
  try {
    const token = await writerToken(request);
    const [moving, leaving, late] = await createLedgers(cells, 3, 'follower-rebind');
    created.push(moving.id, leaving.id, late.id);
    const fraCreated = await json<{ id: string; homeRegion: string }>(await cells.post(`${endpoint('fra')}${project()}/databases`, {
      data: { name: name('follower-rebind-fra'), homeRegion: 'fra', replicaRegions: ['gru'] },
    }), 201);
    created.push(fraCreated.id);
    expect(fraCreated.homeRegion, 'a database created in fra is homed there').toBe('fra');
    const fraLedger = new Ledger(fraCreated.id);
    await sql(cells, endpoint('fra'), fraLedger.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await fraLedger.write(cells, 'fra', [[100, 'homed-in-fra']]);
    await fraLedger.compare(cells);
    await holds(fraLedger.id, 'before its restart');

    await restartAfterKill(partner, false)();
    const primedPartner = await nodeRequest(partner, `/v1/rollout/prime?source=${gru.name}`, 'POST');
    expect(primedPartner.status, primedPartner.body).toBe(204);
    const away = await nodeRequest(gru, `/v1/rollout/drain?target=${partner.name}`, 'POST');
    expect(away.status, away.body).toBe(204);
    await writerIs('gru', partner.name);
    await late.write(cells, 'gru', [[100, 'before-the-move']]);
    await holds(late.id, 'from its gru home before it moves while journey-1 is down');
    const lapsed = logged(gru, 'Home lease lapsed', fraLedger.id);
    const startGru = restartAfterKill(gru, false);
    pending.push(startGru);
    const lateMove = await fetch(`http://127.0.0.1:${partner.port}${database(late.id)}/move-home`, {
      method: 'POST', headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
      body: JSON.stringify({ homeRegion: 'fra' }), signal: AbortSignal.timeout(120_000),
    });
    expect(lateMove.status, `journey-3, the gru writer, moves the database while journey-1 is down: ${await lateMove.clone().text()}`).toBe(200);
    const lateMoved = await lateMove.json() as { homeRegion: string };
    expect(lateMoved.homeRegion).toBe('fra');
    await late.write(cells, 'fra', [[101, 'after-the-move']]);
    const fraWriter = await writerOf(late.id);
    const startFra = restartAfterKill(fraWriter, true);
    pending.push(startFra);
    await startGru();
    pending.splice(pending.indexOf(startGru), 1);
    await expect.poll(() => logged(gru, 'Home lease lapsed', fraLedger.id), { message: 'the restarted journey-1 first sees the fra home lease without its node lease' })
      .toBeGreaterThan(lapsed);
    await startFra();
    pending.splice(pending.indexOf(startFra), 1);
    await holds(late.id, 'once its fra home answers again after journey-1 restarted without it');

    await fraLedger.write(cells, 'fra', [[101, 'after-the-restart']]);
    await holds(fraLedger.id, 'after fra wrote past its restart');

    await moving.write(cells, 'gru', [[100, 'before-the-move']]);
    await holds(moving.id, 'from its gru home before the move');
    const moved = await json<{ homeRegion: string }>(await cells.post(`${endpoint('gru')}${database(moving.id)}/move-home`, { data: { homeRegion: 'fra' }, timeout: 120_000 }));
    expect(moved.homeRegion).toBe('fra');
    await moving.write(cells, 'fra', [[101, 'after-the-move']]);
    await holds(moving.id, 'from its new fra home');
    await moving.compare(cells);

    await holds(leaving.id, 'from its gru home before it leaves gru');
    await expect.poll(async () => {
      const response = await cells.get(`${endpoint('gru')}${database(leaving.id)}/regions`);
      expect(response.status(), await response.text()).toBe(200);
      return (await response.json() as { regions: { region: string }[] }).regions.find(row => row.region === 'fra');
    }, { message: 'fra has a completed pull observation of the database', timeout: 60_000 })
      .toMatchObject({ revision: expect.any(String), lastPulledAt: expect.any(String), lagMs: expect.any(Number) });
    const left = await cells.patch(`${endpoint('gru')}${database(leaving.id)}`, { data: { regions: ['fra'] } });
    expect(left.status(), await left.text()).toBe(200);
    expect((await left.json()).regions, 'the database leaves gru for fra').toEqual(['fra']);
    await expect.poll(async () => (await nodeRequest(gru, `${await route(leaving.id)}/held`)).status, { message: 'journey-1 drops the replica gru no longer holds' }).toBe(404);

    const primed = await nodeRequest(gru, `/v1/rollout/prime?source=${partner.name}`, 'POST');
    expect(primed.status, `journey-1 catches every replica it follows up: ${primed.body}`).toBe(204);
    const back = await nodeRequest(partner, `/v1/rollout/drain?target=${gru.name}`, 'POST');
    expect(back.status, back.body).toBe(204);
    await writerIs('gru', gru.name);
    for (const id of [moving.id, leaving.id]) {
      expect(await placement(gru, id), 'journey-1 recorded the fra writer\'s placement, not one it derived from its own').toEqual(await placement(await writerOf(id), id));
    }
    expect((await placement(gru, leaving.id)).replicaRegions, 'the placement that left gru names no gru replica').not.toContain('gru');
    for (const id of [...created]) {
      await removeDatabase(cells, id);
      created.splice(created.indexOf(id), 1);
    }
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      try {
        for (const start of pending.splice(0)) await start();
        const owner = await kv.get<Record<string, unknown> & { node: string; epoch: number }>(owners, 'gru');
        if (owner?.value.node !== gru.name) {
          await kv.put(owners, 'gru', { ...owner!.value, node: gru.name, epoch: 1, previous: null, activated: false, transfer: null }, owner!.seq);
          await writerIs('gru', gru.name);
        }
        for (const id of created) await removeDatabase(cells, id);
      } finally {
        kv.close();
      }
    });
  }
});

test('a deletion whose holder hands the writer role over before the new writer has its erasure proof still completes erasure', async ({ cells }) => {
  test.setTimeout(300_000);
  const gru = node('journey-1');
  const partner = node('journey-3');
  await restartAfterKill(partner, false)();
  const [ledger] = await createLedgers(cells, 1, 'holder-handoff');
  await removeDatabase(cells, ledger.id);
  const passed = (target: Node) => logged(target, 'Backup erasure of database', `"Database":"${ledger.id}"`, 'is verified')
    + logged(target, 'Backup erasure of database', `"Database":"${ledger.id}"`, '"Reason":"verification_pending"');
  let failure: unknown;
  let handedOver = false;
  try {
    const primed = await nodeRequest(partner, '/v1/rollout/prime?source=journey-1', 'POST');
    expect(primed.status, 'journey-3, restarted after it last gave up the writer role, catches up before it takes it again').toBe(204);
    const drained = await nodeRequest(gru, '/v1/rollout/drain?target=journey-3', 'POST');
    expect(drained.status, drained.body).toBe(204);
    handedOver = true;
    await writerIs('gru', 'journey-3');
    await expect.poll(() => passed(partner), {
      message: 'the new writer gets the erasure proof of journey-1, the holder that deleted the database, and its backup erasure goes past the active copies', timeout: 180_000,
    }).toBeGreaterThan(0);
    expect(logged(partner, 'Backup erasure of database', `"Database":"${ledger.id}"`, '"Reason":"active_copies_pending"', '"Holders":"journey-1"'),
      'once the proof arrives no pass waits on the holder again').toBeLessThanOrEqual(1);
    const receipts = async (target: Node) => {
      const response = await nodeRequest(target, '/v1/sync/purge-receipts/' + ledger.id);
      expect(response.status, response.body).toBe(200);
      return JSON.parse(response.body) as { known: boolean; settled: boolean };
    };
    expect(await receipts(partner), 'the new writer recorded the erasure receipt of every holder its purge record names').toEqual({ known: true, settled: true });
    expect(await receipts(gru), 'journey-1 still holds its own erasure receipt bound to the purge record').toEqual({ known: true, settled: true });
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      if (!handedOver) return;
      await restartAfterKill(gru, false)();
      const caughtUp = await nodeRequest(gru, '/v1/rollout/prime?source=journey-3', 'POST');
      expect(caughtUp.status, 'journey-1, restarted after it gave up the writer role, catches up before it takes it back').toBe(204);
      const back = await nodeRequest(partner, '/v1/rollout/drain?target=journey-1', 'POST');
      expect(back.status, back.body).toBe(204);
      await writerIs('gru', 'journey-1');
    });
  }
});

test('a region writer that stays down keeps its homes, and every acknowledged row survives in both regions', async ({ cells, request }) => {
  test.setTimeout(2 * (outageHold + 150_000));
  const signedIn = await signIn();
  const kv = await Kv.open();
  let failure: unknown;
  try {
    for (const round of [1, 2]) {
      const ledgers = await createLedgers(cells, 3, `home-outage-${round}`);
      const before = await Promise.all(ledgers.map(ledger => homeLease(kv, ledger.id)));
      const watchers = [node('journey-2'), node('journey-4')];
      const lapses = await Promise.all(watchers.map(watcher => counted(watcher, 'terrabase_home_lapses_total')));
      let token = await exchangedToken(request, signedIn);
      const { killedAt, restart } = await killDuringBurst(ledgers, token);
      console.log(`round ${round}: journey-1 killed after ${ledgers.reduce((sum, ledger) => sum + ledger.acknowledged.length, 0)} acknowledged rows`);
      try {
        await holdOutage(kv, ledgers, killedAt, before, token);
        for (const [index, watcher] of watchers.entries()) {
          expect(await counted(watcher, 'terrabase_home_lapses_total') - lapses[index], `${watcher.name} counted this round's lapses and moved nothing`)
            .toBeGreaterThanOrEqual(ledgers.length);
        }
      } finally {
        await restart();
      }
      token = await exchangedToken(request, signedIn);
      await recovered(ledgers, token);
      for (const [index, ledger] of ledgers.entries()) {
        const after = (await homeLease(kv, ledger.id)).value;
        expect(after, 'the returning writer resumes as home').toMatchObject({ home: 'gru', node: 'journey-1' });
        expect(after.epoch, 'only its own activation raised the epoch').toBeLessThanOrEqual(before[index].value.epoch + 1);
      }
      for (const ledger of ledgers) await ledger.drop(token, node('journey-1'));
    }
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      kv.close();
    });
  }
});

test('a writer killed right after an acknowledgement that fra pulled keeps the home and the row', async ({ cells, request }) => {
  test.setTimeout(outageHold + 180_000);
  const signedIn = await signIn();
  const kv = await Kv.open();
  let failure: unknown;
  try {
    const ledgers = await createLedgers(cells, 2, 'home-pulled');
    for (const ledger of ledgers) {
      await ledger.write(cells, 'gru', [[500, 'last-before-kill']]);
      await ledger.compare(cells);
    }
    const before = await Promise.all(ledgers.map(ledger => homeLease(kv, ledger.id)));
    let token = await exchangedToken(request, signedIn);
    const killedAt = Date.now();
    const restart = restartAfterKill(node('journey-1'), true, { token, path: database(ledgers[0].id) });
    try {
      await holdOutage(kv, ledgers, killedAt, before, token);
      for (const ledger of ledgers) expect(await ledger.readNode(node('journey-2'), token), 'fra serves the pulled row while the home is down').toContainEqual([500, 'last-before-kill']);
    } finally {
      await restart();
    }
    token = await exchangedToken(request, signedIn);
    await recovered(ledgers, token);
    for (const ledger of ledgers) expect(await ledger.readNode(node('journey-1'), token)).toContainEqual([500, 'last-before-kill']);
    for (const ledger of ledgers) await ledger.drop(token, node('journey-1'));
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      kv.close();
    });
  }
});

test('an idle backup reaches acknowledged writes without another write, including after a writer restart', async ({ cells }) => {
  const writer = (await topology()).find(node => node.region === 'gru')!;
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('idle-backup'), regions: ['gru'] },
  }), 201);
  const ledger = new Ledger(created.id);
  const restored: string[] = [];
  const latest = async () => {
    const window = await cells.get(`${endpoint('gru')}${database(created.id)}/point-in-time`);
    return window.status() === 200 ? Date.parse((await window.json() as { latest: string }).latest) : 0;
  };
  const recover = async (acked: number) => {
    await expect.poll(latest, { message: 'the idle backup proves every acknowledged write without a later source write' }).toBeGreaterThanOrEqual(acked);
    const at = await latest();
    const plan = await json<{ window: { latest: string } }>(await cells.post(`${endpoint('gru')}${database(created.id)}/point-in-time/plan`, {
      data: { pointInTime: new Date(at).toISOString() },
    }));
    expect(Date.parse(plan.window.latest), 'the stored objects also prove the acknowledged write').toBeGreaterThanOrEqual(acked);
    const operation = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${database(created.id)}/point-in-time/restore`, {
      data: { pointInTime: new Date(at).toISOString(), name: name('idle-restored') },
    }), 202);
    let copy = '';
    await expect.poll(async () => {
      const state = await json<{ status: string; database: string; errorCode?: string }>(await cells.get(`${endpoint('gru')}${database(created.id)}/restores/${operation.id}`));
      expect(state.status, state.errorCode).not.toBe('failed');
      if (state.status === 'completed') copy = state.database;
      return state.status;
    }, { message: 'the idle backup restores its acknowledged rows' }).toBe('completed');
    restored.push(copy);
    expect(await ledger.read(cells, 'gru', copy)).toEqual(ledger.acknowledged);
    console.log(`idle backup ${created.id}: acknowledged=${acked} proven=${at} restored=${JSON.stringify(ledger.acknowledged)}`);
  };
  let failure: unknown;
  try {
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await expect.poll(latest, { message: 'the initial snapshot is stored before the isolated write' }).toBeGreaterThan(0);
    await ledger.write(cells, 'gru', [[1, 'before-restart']]);
    await recover(Date.now());
    await ledger.write(cells, 'gru', [[2, 'restart-immediately']]);
    const acked = Date.now();
    const restart = restartAfterKill(writer, true);
    await restart();
    await expect.poll(() => probe(writer, '/readyz'), { message: 'the restarted writer is ready' }).toBe(200);
    await tokenReady(writer, cells, database(created.id));
    await recover(acked);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      for (const copy of restored) await removeDatabase(cells, copy);
      await removeDatabase(cells, created.id);
    });
  }
});

test('history lost while the backup store is unreachable is one hole each time, also across a generation restart and a node restart, and restores refuse every hole', async ({ cells }) => {
  test.setTimeout(180_000);
  const writer = (await topology()).find(node => node.region === 'gru')!;
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('backup-hole'), regions: ['gru'] },
  }), 201);
  const ledger = new Ledger(created.id);
  const restored: string[] = [];
  let outage = false;
  const churn = async (batches: number) => {
    let inside = 0;
    for (let batch = 0; batch < batches; batch++) {
      if (batch === Math.floor(batches / 2)) inside = Date.now();
      const response = await cells.post(`${endpoint('gru')}${database(created.id)}/sql/execute`, {
        data: { statements: Array.from({ length: 2 }, (_, index) => ({ sql: `UPDATE fault_blob SET body = zeroblob(${1_048_576 + batch * 2 + index}) WHERE id = 1` })) },
      });
      expect(response.status(), await response.text()).toBe(200);
    }
    return inside;
  };
  const refusal = async (at: number) => {
    const refused = await cells.post(`${endpoint('gru')}${database(created.id)}/point-in-time/restore`, {
      data: { pointInTime: new Date(at).toISOString(), name: name('hole-inside') },
    });
    expect(refused.status()).toBe(409);
    const error = (await refused.json() as { error: { code: string; message: string; unavailableFrom: string; unavailableTo: string } }).error;
    expect(error.code).toBe('backup_history_incomplete');
    expect(error.message).toContain('was lost before it was stored');
    return error;
  };
  let failure: unknown;
  try {
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await ledger.write(cells, 'gru', [[1, 'before-outage']]);
    const storedAt = Date.now();
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_blob (id INTEGER PRIMARY KEY, body BLOB NOT NULL)');
    await sql(cells, endpoint('gru'), created.id, 'INSERT INTO fault_blob (id, body) VALUES (1, zeroblob(1))');
    const blobAt = Date.now();
    await expect.poll(async () => {
      const window = await cells.get(`${endpoint('gru')}${database(created.id)}/point-in-time`);
      return window.status() === 200 ? Date.parse((await window.json() as { latest: string }).latest) : 0;
    }, { message: 'the proven restore time reaches the acknowledgement of the last write before the outage' }).toBeGreaterThanOrEqual(blobAt);
    await expect.poll(() => probe(writer, '/backupz'), { message: 'the writer ships every file before the outage' }).toBe(200);
    const holes = await metric(writer, 'terrabase_backup_holes');
    const recordedHoles = await metric(writer, 'terrabase_backup_holes_recorded_total');
    const generations = await metric(writer, 'terrabase_backup_generations_total');
    const shipped = await continuity(writer, created.id);
    expect(shipped).toMatchObject({ replacing: false, storedIncarnation: shipped.incarnation, holes: [] });

    const firstMark = (await backupPuts(shipped.file)).length;
    await backupStore('down');
    outage = true;
    const inside = await churn(75);
    console.log('backup holes: first discard completed');
    const duringSent = Date.now();
    await ledger.write(cells, 'gru', [[2, 'during-outage']]);
    const duringAcked = Date.now();
    const counted = async () => {
      const read = await scrape(writer);
      return { holes: read('terrabase_backup_holes'), recorded: read('terrabase_backup_holes_recorded_total') };
    };
    await expect.poll(counted, { message: 'the discarded history becomes exactly one hole, counted once' }).toEqual({ holes: holes + 1, recorded: recordedHoles + 1 });
    await expect.poll(async () => {
      const state = await continuity(writer, created.id);
      return state.holes.length === 1 && state.holes[0]!.to === state.historyStart && state.oldestRetained === state.historyStart;
    }, { message: 'the open hole ends where the change log now starts' }).toBe(true);
    const opened = await continuity(writer, created.id);
    expect(opened).toMatchObject({ replacing: true, storedPosition: shipped.storedPosition, storedIncarnation: opened.incarnation });
    expect(opened.holes[0]).toMatchObject({ incarnation: opened.incarnation, from: shipped.storedPosition, open: true });
    expect(opened.holes[0]!.from).toBeLessThan(opened.holes[0]!.to);
    expect(await probe(writer, '/backupz')).toBe(503);
    expect(await probe(writer, '/readyz')).toBe(200);
    const stillOpen = await refusal(inside);
    expect(stillOpen.message).toContain('no recovery point after it is stored yet');
    expect(Date.parse(stillOpen.unavailableFrom)).toBeLessThan(inside);
    expect(Date.parse(stillOpen.unavailableTo)).toBeGreaterThanOrEqual(Date.parse(stillOpen.unavailableFrom));

    await backupStore('up');
    outage = false;
    await expect.poll(() => probe(writer, '/backupz'), { message: 'a replacement snapshot makes the writer healthy again' }).toBe(200);
    expect(await metric(writer, 'terrabase_backup_generations_total')).toBeGreaterThan(generations);
    expect(await metric(writer, 'terrabase_backup_holes')).toBe(holes + 1);
    const firstHole = new RegExp(`/holes/${opened.incarnation}-${String(opened.holes[0]!.from).padStart(16, '0')}-\\d{16}-\\d{13}-(\\d{13}|unknown)-open$`);
    const afterFirst = (await backupPuts(shipped.file)).slice(firstMark);
    expect(afterFirst[0], `the first object the bucket receives after the outage is the hole: ${afterFirst.join(' ')}`).toMatch(firstHole);
    const uploaded = await metric(writer, 'terrabase_backup_uploaded_bytes_total', 'kind="segment"');
    await ledger.write(cells, 'gru', [[3, 'after-outage']]);
    const afterAcked = Date.now();
    await expect.poll(() => metric(writer, 'terrabase_backup_uploaded_bytes_total', 'kind="segment"'), { message: 'later passes store the writes after the outage' }).toBeGreaterThan(uploaded);
    await expect.poll(async () => {
      const window = await cells.get(`${endpoint('gru')}${database(created.id)}/point-in-time`);
      return window.status() === 200 ? Date.parse((await window.json() as { latest: string }).latest) : 0;
    }, { message: 'the proven restore time reaches the write after the outage' }).toBeGreaterThanOrEqual(afterAcked);
    const closed = await continuity(writer, created.id);
    expect(closed).toMatchObject({ replacing: false, storedIncarnation: closed.incarnation });
    expect(closed.holes).toHaveLength(1);
    expect(closed.holes[0]).toMatchObject({ incarnation: closed.incarnation, from: opened.holes[0]!.from, open: false });
    expect(closed.holes[0]!.to).toBeGreaterThanOrEqual(opened.holes[0]!.to);
    expect(closed.holes[0]!.to).toBeLessThanOrEqual(closed.storedPosition!);

    const unplaceable = await metric(writer, 'terrabase_backup_generation_restarts_total', 'reason="log_unplaceable"');
    const secondMark = (await backupPuts(shipped.file)).length;
    await backupStore('down');
    outage = true;
    await sql(cells, endpoint('gru'), created.id, 'ALTER TABLE fault_rows ADD COLUMN note TEXT');
    await expect.poll(() => metric(writer, 'terrabase_backup_generation_restarts_total', 'reason="log_unplaceable"'),
      { message: 'the schema change restarts the generation' }).toBe(unplaceable + 1);
    const kept = await continuity(writer, created.id);
    expect(kept, 'the restart keeps the last stored position until a replacement is stored').toMatchObject({ replacing: true, storedIncarnation: kept.incarnation });
    expect(kept.storedPosition).toBeGreaterThanOrEqual(closed.storedPosition!);
    const insideSecond = await churn(75);
    console.log('backup holes: second discard completed');
    await expect.poll(counted, { message: 'history discarded while the replacement cannot be stored is one more hole, counted once' }).toEqual({ holes: holes + 2, recorded: recordedHoles + 2 });
    const twoHoles = (await continuity(writer, created.id)).holes;
    expect(twoHoles).toHaveLength(2);
    expect(twoHoles[1]).toMatchObject({ incarnation: kept.incarnation, from: kept.storedPosition, open: true });
    await churn(65);
    console.log('backup holes: later discard completed');
    await expect.poll(async () => (await continuity(writer, created.id)).holes[1]!.to,
      { message: 'a later discard moves the open hole to the new start of the change log' }).toBeGreaterThan(twoHoles[1]!.to);
    expect(await metric(writer, 'terrabase_backup_holes_recorded_total')).toBe(recordedHoles + 2);

    console.log('backup holes: restarting writer');
    const beforeRestart = await nodeHoles(writer);
    const systemBefore = await systemContinuity(writer);
    const restart = restartAfterKill(writer, true, undefined, async () => {
      expect(await probe(writer, '/readyz')).toBe(503);
      expect(await metric(writer, 'terrabase_catalog_closed', 'reason="store_unavailable"')).toBe(1);
      await backupStore('up');
      outage = false;
    });
    await restart();
    console.log('backup holes: writer restarted');
    await expect.poll(() => probe(writer, '/readyz'), { message: 'the restarted writer is ready' }).toBe(200);
    await tokenReady(writer, cells, database(created.id));
    expect(await metric(writer, 'terrabase_catalog_closed', 'reason="store_unavailable"')).toBe(0);
    await expect.poll(() => probe(writer, '/backupz'), { message: 'a replacement snapshot after the restart closes the second hole' }).toBe(200);
    const settled = await continuity(writer, created.id);
    expect(settled.holes.map(hole => hole.open)).toEqual([false, false]);
    expect(settled.holes[1]!.from).toBe(kept.storedPosition);
    const afterSecond = (await backupPuts(shipped.file)).slice(secondMark);
    const secondHole = new RegExp(`/holes/${kept.incarnation}-${String(kept.storedPosition).padStart(16, '0')}-\\d{16}-\\d{13}-(\\d{13}|unknown)-open$`);
    expect(afterSecond[0], `the first object the bucket receives after the second outage is its hole: ${afterSecond.join(' ')}`).toMatch(secondHole);
    expect(afterSecond.some(key => key.includes('/holes/') && key.endsWith('-closed'))).toBe(true);
    expect(settled.holes.map(hole => [hole.incarnation, hole.from])).toEqual([[opened.incarnation, opened.holes[0]!.from], [kept.incarnation, kept.storedPosition]]);
    const afterRestart = await nodeHoles(writer);
    expect(afterRestart.toSorted(), 'every change the restarted writer made before its backup taps opened is kept until they ship it: no new hole on any file')
      .toEqual(beforeRestart.toSorted());
    await expect.poll(counted, { message: 'the restarted writer records no hole' }).toEqual({ holes: holes + 2, recorded: 0 });
    await expect.poll(async () => (await systemContinuity(writer)).storedPosition!,
      { message: 'the system database ships the writes it made before its backup tap opened' }).toBeGreaterThan(systemBefore.storedPosition!);
    const systemAfter = await systemContinuity(writer);
    console.log(`system backup of ${writer.name}: stored ${systemBefore.storedPosition} before the kill, ${systemAfter.storedPosition} after the restart`);
    expect(systemAfter).toMatchObject({ incarnation: systemBefore.incarnation, storedIncarnation: systemBefore.incarnation });
    expect(systemAfter.holes.map(hole => [hole.incarnation, hole.from])).toEqual(systemBefore.holes.map(hole => [hole.incarnation, hole.from]));

    const error = await refusal(inside);
    const [from, to] = [Date.parse(error.unavailableFrom), Date.parse(error.unavailableTo)];
    expect(from).toBeLessThan(inside);
    expect(to).toBeGreaterThan(inside);
    expect([from, to]).toEqual([Date.parse(settled.holes[0]!.fromAt!), Date.parse(settled.holes[0]!.toAt!)]);
    const secondError = await refusal(insideSecond);
    expect([Date.parse(secondError.unavailableFrom), Date.parse(secondError.unavailableTo)]).toEqual([Date.parse(settled.holes[1]!.fromAt!), Date.parse(settled.holes[1]!.toAt!)]);
    expect(Date.parse(secondError.unavailableFrom)).toBeGreaterThan(to);
    console.log(`holes of ${created.id}: ${JSON.stringify(settled.holes)}; refused inside at ${new Date(inside).toISOString()} and ${new Date(insideSecond).toISOString()}`);

    const restore = async (at: number) => {
      const started = await cells.post(`${endpoint('gru')}${database(created.id)}/point-in-time/restore`, {
        data: { pointInTime: new Date(at).toISOString(), name: name('hole-restore') },
      });
      expect(started.status(), await started.text()).toBe(202);
      const operation = await started.json() as { id: string };
      let copy = '';
      await expect.poll(async () => {
        const state = await json<{ status: string; database: string; errorCode?: string }>(await cells.get(`${endpoint('gru')}${database(created.id)}/restores/${operation.id}`));
        expect(state.status, state.errorCode).not.toBe('failed');
        if (state.status === 'completed') copy = state.database;
        return state.status;
      }, { message: 'the point-in-time restore completes' }).toBe('completed');
      restored.push(copy);
      return ledger.read(cells, 'gru', copy);
    };
    const window = await json<{ earliest: string }>(await cells.get(`${endpoint('gru')}${database(created.id)}/point-in-time`));
    const second = Math.max(Date.parse(window.earliest), from - from % 1000);
    const before = await restore(second);
    expect(storedAt < second ? [[[1, 'before-outage']]] : [[], [[1, 'before-outage']]]).toContainEqual(before);
    const atSnapshot = await restore(to);
    const [one, both] = [[[1, 'before-outage']], [[1, 'before-outage'], [2, 'during-outage']]];
    expect(to < duringSent ? [one] : to > duringAcked ? [both] : [one, both]).toContainEqual(atSnapshot);
    console.log(`restores of ${created.id}: ${JSON.stringify(before)} at ${new Date(second).toISOString()}, before the hole; ${JSON.stringify(atSnapshot)} at the replacement snapshot ${new Date(to).toISOString()}`);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      if (outage) await backupStore('up');
      for (const copy of restored) await removeDatabase(cells, copy);
      await removeDatabase(cells, created.id);
    });
  }
});

test('restores read the holes recorded with the backup: one the restoring node has no account of, the first of 33, an open one with an unknown end, and none while they cannot be listed', async ({ cells }) => {
  const writer = (await topology()).find(node => node.region === 'gru')!;
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('hole-records'), regions: ['gru'] },
  }), 201);
  const ledger = new Ledger(created.id);
  let unlisted = false;
  let restored: string | undefined;
  const latest = async () => {
    const window = await cells.get(`${endpoint('gru')}${database(created.id)}/point-in-time`);
    return window.status() === 200 ? Date.parse((await window.json() as { latest: string }).latest) : 0;
  };
  const planned = async (at: number) => {
    const response = await cells.post(`${endpoint('gru')}${database(created.id)}/point-in-time/plan`, { data: { pointInTime: new Date(at).toISOString() } });
    return `${response.status()} ${response.status() === 200 ? '' : await response.text()}`.trim();
  };
  const refused = async (at: number) => {
    const response = await cells.post(`${endpoint('gru')}${database(created.id)}/point-in-time/restore`, {
      data: { pointInTime: new Date(at).toISOString(), name: name('hole-record') },
    });
    const text = await response.text();
    expect(response.status(), text).toBe(409);
    const error = (JSON.parse(text) as { error: { code: string; message: string; unavailableFrom?: string; unavailableTo?: string } }).error;
    expect(error.code).toBe('backup_history_incomplete');
    return error;
  };
  let failure: unknown;
  try {
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await expect.poll(latest, { message: 'the backup stores its first snapshot before the writes' }).toBeGreaterThan(0);
    const firstSent = Date.now();
    await ledger.write(cells, 'gru', [[1, 'first']]);
    const firstAcked = Date.now();
    const secondSent = Date.now();
    await ledger.write(cells, 'gru', [[2, 'second']]);
    const secondAcked = Date.now();
    await expect.poll(latest, { message: 'the backup proves a time at or after the second acknowledgement' }).toBeGreaterThanOrEqual(secondAcked);
    const shipped = await continuity(writer, created.id);
    expect(shipped.holes).toEqual([]);
    const newest = await latest();
    expect(await planned(newest), 'the newest proven time is admitted before any hole is recorded').toBe('200');
    const started = await cells.post(`${endpoint('gru')}${database(created.id)}/point-in-time/restore`, {
      data: { pointInTime: new Date(newest).toISOString(), name: name('hole-records-latest') },
    });
    expect(started.status(), await started.text()).toBe(202);
    const operation = await started.json() as { id: string };
    await expect.poll(async () => {
      const state = await json<{ status: string; database: string; errorCode?: string }>(await cells.get(`${endpoint('gru')}${database(created.id)}/restores/${operation.id}`));
      expect(state.status, state.errorCode).not.toBe('failed');
      if (state.status === 'completed') restored = state.database;
      return state.status;
    }, { message: 'the restore of the newest proven time completes' }).toBe('completed');
    const written: [[number, string], number, number][] = [[[1, 'first'], firstSent, firstAcked], [[2, 'second'], secondSent, secondAcked]];
    const acknowledged = written.filter(([, , acked]) => acked <= newest).map(([row]) => row);
    expect(acknowledged, 'both writes were acknowledged before the newest proven time').toHaveLength(2);
    const sent = written.filter(([, at]) => at <= newest).map(([row]) => row);
    expect([acknowledged, sent], 'every row acknowledged before the newest proven time is in its restore').toContainEqual(await ledger.read(cells, 'gru', restored));
    const target = newest - 100;
    const [before, after, half] = [target - 20, target + 20, 5];

    await plantHole(shipped.file, { incarnation: shipped.incarnation, from: 1, to: 2, fromAt: target - half, toAt: target + half, open: false });
    const unknownToNode = await refused(target);
    expect([Date.parse(unknownToNode.unavailableFrom!), Date.parse(unknownToNode.unavailableTo!)]).toEqual([target - half, target + half]);
    expect((await continuity(writer, created.id)).holes, 'the restoring node has no account of the hole it refuses').toEqual([]);

    for (let index = 0; index < 32; index++) {
      await plantHole(shipped.file, { incarnation: shipped.incarnation, from: 10 + index, to: 11 + index, fromAt: newest + 1000 * (index + 1), toAt: newest + 1000 * (index + 1) + 10, open: false });
    }
    const firstOf33 = await refused(target);
    expect([Date.parse(firstOf33.unavailableFrom!), Date.parse(firstOf33.unavailableTo!)]).toEqual([target - half, target + half]);

    await plantHole(shipped.file, { incarnation: shipped.incarnation, from: 100, to: 101, fromAt: after - 1, toAt: null, open: true });
    const unknownEnd = await refused(after);
    expect(Date.parse(unknownEnd.unavailableFrom!)).toBe(after - 1);
    expect(unknownEnd.unavailableTo).toBeUndefined();
    expect(unknownEnd.message).toContain('to an unknown time');

    await holeRecords('unlisted');
    unlisted = true;
    const unreadable = await refused(before);
    expect(unreadable.message).toContain('record of lost history could not be read');
    expect(unreadable.unavailableFrom).toBeUndefined();
    await holeRecords('listed');
    unlisted = false;
    expect(await planned(before), 'once the hole records can be listed again, the time outside every hole is not refused for them').not.toContain('could not be read');
    await plantHole(shipped.file, { incarnation: shipped.incarnation, from: 2, to: 1, fromAt: before + 100, toAt: before, open: false });
    const inconsistent = await refused(before);
    expect(inconsistent.message, 'a hole record whose bounds contradict each other is unreadable, not an empty range').toContain('record of lost history could not be read');
    expect(inconsistent.unavailableFrom).toBeUndefined();
    console.log(`hole records of ${created.id}: refused ${new Date(target).toISOString()} (planted, unknown to the node; first of 33), ${new Date(after).toISOString()} (open, unknown end) and ${new Date(before).toISOString()} while unlisted`);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      if (unlisted) await holeRecords('listed');
      if (restored) await removeDatabase(cells, restored);
      await removeDatabase(cells, created.id);
    });
  }
});

test('handoff receives receipts with their original bindings before exporting them', async () => {
  const environment: NodeJS.ProcessEnv = { ...process.env, DOTNET_ENVIRONMENT: 'Development', ASPNETCORE_ENVIRONMENT: 'Development' };
  for (const key of Object.keys(environment)) {
    if (key.startsWith('TS_') || key.startsWith('TERRABASE_')) delete environment[key];
  }
  const master = randomBytes(32).toString('base64');
  saveSecret('receipt-import-master', master);
  environment.TERRABASE_MASTER_KEY = master;
  const build = process.env.E2E_BUILD ?? join(stateDir, 'build');
  const result = await promisify(execFile)('dotnet', [join(build, 'receipt-import', 'terrabase.dll'),
    `--data-directory=${join(stateDir, `receipt-import-${faultId()}`)}`,
  ], { env: environment, cwd: e2eRoot, timeout: 20_000 });
  expect(result.stdout).toContain('RECEIPT_CASE absent-refusal passed');
  expect(result.stdout).toContain('RECEIPT_CASE closure-version-two passed');
  expect(result.stdout).toContain('RECEIPT_IMPORT identical=accepted invalid=16 retained_bytes=2 ordinary_export=0');
  console.log(result.stdout.split('\n').filter(line => line.startsWith('RECEIPT_')).join('\n'));
});

test('same-region retained receiver preserves its copy and resumes after restoration', async () => {
  await receiptHandoff('retained');
});

test('a receipt reaching its writer before the purge record still completes backup erasure without deleting live data on absence', async () => {
  await receiptHandoff('early');
});

test('a receipt its writer refused is recorded after the writer role moves to the holder and returns at the same epoch, and backup erasure completes', async () => {
  await receiptHandoff('refused');
});

for (const scenario of ['purge-holder', 'purge-authority', 'closure-holder', 'closure-authority', 'stale-closure', 'stale-refusal',
  'absent-holder', 'absent-authority', 'absent-database', 'absent-project', 'conflict-count', 'rows', 'bytes', 'identical']) {
  test(`receipt handoff ${scenario} preserves evidence and serving authority`, async () => {
    await receiptHandoff(scenario);
  });
}

test('another region outside admitted peers cannot interrupt local writer renewal', async ({ cells, request, playwright }) => {
  const writers = await topology();
  const local = writers.find(node => node.region === 'gru')!;
  const foreign = writers.find(node => node.region === 'fra')!;
  const target = nodes.find(node => node.region === 'fra' && node.name !== foreign.name)!;
  const user = faultId();
  const manage = async (node: typeof foreign, method: string, path: string, body?: unknown, expected = 200) => {
    const response = await nodeRequest(node, path, method, body, user);
    expect(response.status, response.body).toBe(expected);
    return response.body ? JSON.parse(response.body) as { id: string } : undefined;
  };
  const organization = (await manage(foreign, 'POST', '/v1/management/organizations', { name: name('handoff-closure') }, 201))!;
  const organizationPath = `/v1/management/organizations/${organization.id}`;
  const projectRecord = (await manage(foreign, 'POST', `${organizationPath}/projects`, { name: name('handoff-closure') }, 201))!;
  const projectPath = `${organizationPath}/projects/${projectRecord.id}`;
  const retired = (await manage(foreign, 'POST', `/v1/projects/${projectRecord.id}/databases`, { name: name('handoff-purge'), regions: ['fra'] }, 201))!;
  await manage(foreign, 'DELETE', `/v1/projects/${projectRecord.id}/databases/${retired.id}`, undefined, 204);
  const peers = join(stateDir, 'peers');
  const original = readFileSync(peers, 'utf8');
  const replacePeers = (value: string) => {
    writeFileSync(`${peers}.next`, value);
    renameSync(`${peers}.next`, peers);
  };
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('owner-isolation'), regions: ['gru'] },
  }), 201);
  const ledger = new Ledger(created.id);
  const metric = async () => {
    const response = await request.get(`http://${local.address}:${local.port + 1011}/metrics`);
    expect(response.status()).toBe(200);
    const text = await response.text();
    return text.split('\n').filter(line => line.startsWith('terrabase_ownership_refresh_errors_total{') && line.includes('region="fra"'))
      .reduce((total, line) => total + Number(line.split(' ').at(-1)), 0);
  };
  let failure: unknown;
  try {
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await expect.poll(() => nodeStatus(local, 'fra')).toMatchObject({ owner: foreign.name });
    const before = await metric();
    const log = join(stateDir, 'logs', `${local.service}.log`);
    const offset = readFileSync(log).length;
    const inconsistent = original.trim().split('\n').filter(line => !line.startsWith(`${foreign.name} `))
      .sort((a, b) => a.split(' ')[1].localeCompare(b.split(' ')[1])).join('\n') + '\n';
    replacePeers(inconsistent);
    const started = Date.now();
    let row = 0;
    while (Date.now() - started < 30_000) {
      expect(await nodeStatus(local), 'the same local writer remains ready throughout the foreign fault')
        .toMatchObject({ owner: local.name, writer: true, ready: true });
      for (const node of nodes.filter(node => node.region === 'gru' && node.name !== local.name)) {
        expect(await nodeStatus(node)).toMatchObject({ writer: false, ready: true });
      }
      const ready = await request.get(`http://${local.address}:${local.port}/readyz`);
      expect(ready.status()).toBe(200);
      await ledger.write(cells, 'gru', [[++row, 'uninterrupted']]);
    }
    expect(row).toBeGreaterThan(1);
    expect(await metric()).toBeGreaterThan(before);
    const failures = readFileSync(log).subarray(offset).toString().split('\n')
      .filter(line => line.includes('Regional ownership observation failed for ') && line.includes('fra'));
    expect(failures, 'unchanged foreign failure logs once').toHaveLength(1);
    expect(await nodeStatus(foreign), 'invalid local authority still stops its own writer').toMatchObject({ writer: false });
    expect(await nodeStatus(local, 'fra')).toMatchObject({ owner: foreign.name });
    console.log(`foreign ownership fault: ${Date.now() - started} ms, ${row} acknowledged local writes, fra counter increased`);
    replacePeers(original);
    await expect.poll(() => nodeStatus(foreign), { message: 'the restored foreign writer reacquires local admission' })
      .toMatchObject({ writer: true, ready: true });
    await topology();
    const drain = await nodeRequest(foreign, `/v1/rollout/drain?target=${target.name}`, 'POST');
    expect(drain.status, drain.body).toBe(204);
    await expect.poll(() => nodeStatus(local, 'fra'), { message: 'the restored foreign owner is observed after handoff' })
      .toMatchObject({ owner: target.name });
    await expect.poll(() => nodeStatus(target)).toMatchObject({ owner: target.name, writer: true, ready: true });
    expect(await ledger.read(cells, 'gru')).toEqual(ledger.acknowledged);
    console.log(`foreign ownership restored: ${target.name} observed from ${local.name}; acknowledged rows retained`);
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await cleanUp(failure, async () => {
      replacePeers(original);
      await expect.poll(() => nodeStatus(foreign), { message: 'discover the committed foreign writer even when drain failed' })
        .toMatchObject({ ready: true });
      let observedOwner = '';
      await expect.poll(async () => {
        const status = await nodeStatus(foreign);
        observedOwner = status.owner;
        const owner = nodes.find(node => node.name === status.owner && node.region === foreign.region);
        return owner ? nodeStatus(owner) : undefined;
      }, { message: 'the actual foreign writer settles before restoration' }).toMatchObject({ writer: true, ready: true });
      const actualWriter = nodes.find(node => node.name === observedOwner && node.region === foreign.region);
      expect(actualWriter, 'the actual foreign writer is admitted').toBeDefined();
      if (actualWriter!.name !== foreign.name) {
        await restartAfterKill(foreign, false)();
        const primed = await nodeRequest(foreign, `/v1/rollout/prime?source=${actualWriter!.name}`, 'POST');
        expect(primed.status, primed.body).toBe(204);
        const restored = await nodeRequest(actualWriter!, `/v1/rollout/drain?target=${foreign.name}`, 'POST');
        expect(restored.status, restored.body).toBe(204);
        await restartAfterKill(target, false)();
        const caughtUp = await nodeRequest(target, `/v1/rollout/prime?source=${foreign.name}`, 'POST');
        expect(caughtUp.status, caughtUp.body).toBe(204);
        await restartAfterKill(target, false)();
        const primedAgain = await nodeRequest(target, `/v1/rollout/prime?source=${foreign.name}`, 'POST');
        expect(primedAgain.status, primedAgain.body).toBe(204);
      }
      await expect.poll(() => nodeStatus(local), { message: 'local authority is restored before cleanup' })
        .toMatchObject({ writer: true, ready: true });
      await removeDatabase(cells, created.id);
      expect(readFileSync(peers, 'utf8')).toBe(original);
      const restoredWriters = await topology();
      expect(restoredWriters.map(node => node.name).sort()).toEqual(writers.map(node => node.name).sort());
      for (const node of nodes) {
        for (const writer of writers) {
          expect(await nodeStatus(node, writer.region)).toMatchObject({ owner: writer.name, ready: true });
        }
      }
      for (const node of [foreign, target]) await tokenReady(node, cells, `${project()}/databases`);
      const closure = { version: 2, closureId: faultId(), authorityOrganizationId: organization.id };
      const closurePath = `/admin/projects/${projectRecord.id}/closure`;
      const closing = await nodeRequest(local, closurePath, 'PUT', closure);
      expect([200, 202]).toContain(closing.status);
      const query = `?version=2&closureId=${closure.closureId}&authorityOrganizationId=${organization.id}`;
      await expect.poll(async () => {
        const response = await nodeRequest(foreign, closurePath + query);
        expect([200, 202]).toContain(response.status);
        return JSON.parse(response.body);
      }, { message: 'closure completes after returning to the original receipt holder' })
        .toMatchObject({ status: 'accepted', pendingHolders: 0, closureId: closure.closureId });
      await manage(foreign, 'DELETE', projectPath, undefined, 204);
      await manage(foreign, 'DELETE', organizationPath, undefined, 204);
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
      console.log('restored original writers, all four peer observations, and existing account credentials and session');
    });
  }
});

test('a regional writer role moves over a stale home lease of its own region: the new writer takes it with the activation proof and serves writes', async ({ cells }) => {
  const kv = await Kv.open();
  const gru = node('journey-1');
  const partner = node('journey-3');
  const drain = async (source: Node, target: Node) => {
    const primed = await nodeRequest(target, `/v1/rollout/prime?source=${source.name}`, 'POST');
    expect(primed.status, primed.body).toBe(204);
    const drained = await nodeRequest(source, `/v1/rollout/drain?target=${target.name}`, 'POST');
    expect(drained.status, drained.body).toBe(204);
    await writerIs('gru', target.name);
  };
  await writerIs('gru', gru.name);
  const [ledger] = await createLedgers(cells, 1, 'stale-own-region');
  try {
    const stored = await homeLease(kv, ledger.id);
    expect(stored.value).toMatchObject({ home: 'gru', node: gru.name });
    await kv.put(homes, `home.${ledger.id}`, { ...stored.value, node: partner.name, epoch: stored.value.epoch - 1 }, stored.seq);
    await drain(gru, partner);
    await expect.poll(async () => (await homeLease(kv, ledger.id)).value, { message: 'journey-3 takes its own region\'s stale lease at the placement version' })
      .toMatchObject({ home: 'gru', node: partner.name, epoch: stored.value.epoch });
    await ledger.write(cells, 'gru', [[901, 'after-stale-takeover']]);
    await ledger.compare(cells);
    await restartAfterKill(gru, false)();
    await drain(partner, gru);
    await ledger.write(cells, 'gru', [[902, 'after-drain-back']]);
    await ledger.compare(cells);
  } finally {
    kv.close();
  }
  await removeDatabase(cells, ledger.id);
});

test('a home lease of another region below the placement version cannot serve: the database is unserved and refuses writes until the repair re-stamps it', async ({ cells, request }) => {
  const token = await writerToken(request);
  const kv = await Kv.open();
  const gru = node('journey-1');
  await writerIs('gru', gru.name);
  await writerIs('fra', 'journey-2');
  const [ledger] = await createLedgers(cells, 1, 'stale-other-region');
  try {
    const stored = await homeLease(kv, ledger.id);
    await kv.put(homes, `home.${ledger.id}`, { ...stored.value, home: 'fra', node: 'journey-2', epoch: stored.value.epoch - 1 }, stored.seq);
    await expect.poll(async () => ((await nodeStatus(gru)).unserved ?? []).find(entry => entry.databaseId === ledger.id),
      { message: 'journey-1 lists the database as unserved by a stale lease of fra', timeout: 45_000 })
      .toMatchObject({ region: 'fra', node: 'journey-2', cause: 'stale_lease' });
    expect(await counted(gru, 'terrabase_home_unserved', { region: 'fra' }), 'the unserved gauge counts it under the lease\'s region').toBeGreaterThanOrEqual(1);
    expect(logged(gru, 'is unserved', ledger.id, 'stale_lease'), 'journey-1 logs the stale lease once').toBeGreaterThanOrEqual(1);
    let row = 910;
    await expect.poll(async () => ledger.refusal(gru, token, [[row++, 'while-stale']]), { message: 'journey-1 refuses writes while the lease names fra', timeout: 45_000 })
      .toEqual({ status: 409, code: 'serialization_conflict' });
    const repaired = await nodeRequest(gru, `/admin/organizations/${stored.value.organizationId}/projects/${stored.value.projectId}/databases/${ledger.id}/home-lease`, 'POST');
    expect(repaired.status, repaired.body).toBe(200);
    expect(JSON.parse(repaired.body)).toMatchObject({
      outcome: 'restamped', node: gru.name, epoch: stored.value.epoch, previousNode: 'journey-2', previousEpoch: stored.value.epoch - 1,
    });
    expect((await homeLease(kv, ledger.id)).value).toMatchObject({ home: 'gru', node: gru.name, epoch: stored.value.epoch });
    await ledger.write(cells, 'gru', [[930, 'after-repair']]);
    await ledger.compare(cells);
    const again = await nodeRequest(gru, `/admin/organizations/${stored.value.organizationId}/projects/${stored.value.projectId}/databases/${ledger.id}/home-lease`, 'POST');
    expect(JSON.parse(again.body), 'a second repair finds the lease held and changes nothing').toMatchObject({ outcome: 'unchanged', node: gru.name });
    await expect.poll(async () => ((await nodeStatus(gru)).unserved ?? []).some(entry => entry.databaseId === ledger.id),
      { message: 'the database is served again', timeout: 45_000 }).toBe(false);
  } finally {
    kv.close();
  }
  await removeDatabase(cells, ledger.id);
});
