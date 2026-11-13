import { expect } from '@playwright/test';
import { test, database, json, name, project, removeDatabase, sql, type Cells } from '../placement/support';
import { crashWrite, endpoint, holdsFile, keptCopies, keptHash, Kv, Ledger, logged, metric, node, nodeStatus, peer, promoting, regions, restartAfterKill, topology, writerToken, type HomeLease, type Row } from './support';

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
    const restart = restartAfterKill(writer);
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
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('restore a backup into another database and compare its rows with the snapshot ledger in every region', async ({ cells }) => {
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('backup-ledger'), regions: [...regions] },
  }), 201);
  const ledger = new Ledger(created.id);
  let restored: string | undefined;
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
  } finally {
    if (restored) await removeDatabase(cells, restored);
    await removeDatabase(cells, created.id);
  }
});

const homes = 'terrabase-homes';
const owners = 'terrabase-region-owners';
const outageHold = 30_000;
const homeDown = { status: 503, code: 'tenant_busy' };

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
  await Promise.all(Array.from({ length: 12 }, async () => {
    while (next < ids.length) {
      const id = ids[next++];
      const ledger = ledgers[id % ledgers.length];
      if (await ledger.direct(writer, token, [[id, `burst-${id}`]]) && ++acknowledged === 45 && !killed) {
        killed = { killedAt: Date.now(), restart: restartAfterKill(writer) };
      }
    }
  }));
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
  try {
    const [ledger] = await createLedgers(cells, 1, 'home-move-refused');
    const before = await homeLease(kv, ledger.id);
    const refusals = await metric(node('journey-4'), 'terrabase_home_refusals_total', { reason: 'not_writer' });
    const answer = await peer(node('journey-4'), `/v1/sync/organizations/${before.value.organizationId}/projects/${before.value.projectId}/databases/${ledger.id}/promote`, {
      moveId: `fault-${ledger.id}`, fromRegion: 'gru', version: before.value.epoch + 1, replicaRegions: ['gru'], allRegions: false,
    });
    expect(answer.status, answer.body).toBe(409);
    expect(JSON.parse(answer.body).error.code).toBe('move_aborted');
    await expect.poll(() => metric(node('journey-4'), 'terrabase_home_refusals_total', { reason: 'not_writer' }), { message: 'journey-4 counts the refusal' }).toBe(refusals + 1);
    expect(await homeLease(kv, ledger.id)).toEqual(before);
    await ledger.write(cells, 'gru', [[700, 'after-refused-move']]);
    await ledger.write(cells, 'fra', [[701, 'after-refused-move-fra']]);
    await ledger.compare(cells);
    await removeDatabase(cells, ledger.id);
  } finally {
    kv.close();
  }
});

test('a move that fails before its commit point rolls back: the target acknowledged nothing, both files stay, the old home serves and no push is lost', async ({ cells, request }) => {
  test.setTimeout(240_000);
  const token = await writerToken(request);
  const kv = await Kv.open();
  try {
    const [ledger] = await createLedgers(cells, 1, 'move-rollback');
    await ledger.write(cells, 'fra', [[21, 'pushed-from-fra']]);
    await ledger.compare(cells);
    const before = await homeLease(kv, ledger.id);
    const { organizationId, projectId } = before.value;
    const gru = node('journey-1');
    const target = node('journey-2');
    const drainFailed = await metric(target, 'terrabase_home_refusals_total', { reason: 'drain_failed' });
    const demotions = await metric(gru, 'terrabase_home_demotions_total');
    const published = await kv.publish(`terrabase.push.fra.gru.${organizationId}.${projectId}.${ledger.id}`, { undecodable: true }, { 'Tb-Epoch': String(before.value.epoch) });
    expect(published.error, JSON.stringify(published)).toBeUndefined();
    const started = Date.now();
    const move = cells.post(`${endpoint('gru')}${database(ledger.id)}/move-home`, { data: { homeRegion: 'fra' }, timeout: 120_000 });

    await test.step('the target takes the lease, stays fenced and acknowledges no write before its commit point', async () => {
      await expect.poll(async () => (await kv.get<HomeLease>(homes, `home.${ledger.id}`))?.value, { message: 'journey-2 took the lease', timeout: 60_000, intervals: [100] })
        .toMatchObject({ home: 'fra', node: 'journey-2', epoch: before.value.epoch + 1 });
      expect((await homeLease(kv, ledger.id)).value.adopted ?? null, 'the lease records no adopted position before the commit point').toBeNull();
      expect(promoting(target, ledger.id), 'the promotion fence is on disk').toBe(true);
      await expect.poll(async () => (await (await fetch(`http://127.0.0.1:${target.port}${database(ledger.id)}`, { headers: { authorization: `Bearer ${token}` } })).json()).homeRegion,
        { message: 'journey-2 recorded the new placement before its commit point', timeout: 30_000, intervals: [100] }).toBe('fra');
      expect(await ledger.refusal(target, token, [[22, 'during-move']]), 'the target refuses a write before its commit point')
        .toEqual({ status: 409, code: 'serialization_conflict' });
    });

    await test.step('the drain fails, the move rolls back and the old home serves again', async () => {
      const answer = await move;
      expect(answer.status(), await answer.text()).toBe(503);
      expect((await answer.json()).error.code).toBe('replica_not_ready');
      expect(Date.now() - started, 'the move outlasted one lease scan of the old home').toBeGreaterThan(15_000);
      await expect.poll(async () => (await homeLease(kv, ledger.id)).value, { message: 'the lease names journey-1 again at the next epoch', timeout: 60_000 })
        .toMatchObject({ home: 'gru', node: 'journey-1', epoch: before.value.epoch + 2 });
      expect((await homeLease(kv, ledger.id)).value.adopted ?? null).toBeNull();
      expect(await metric(target, 'terrabase_home_refusals_total', { reason: 'drain_failed' })).toBe(drainFailed + 1);
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
      expect(copies).toEqual([expect.objectContaining({ reason: 'move_rolled_back', organizationId, projectId, databaseId: ledger.id })]);
      expect(copies[0].files).toContain(`${ledger.id}.db`);
      expect(copies[0].bytes).toBeGreaterThan(0);
      expect(await metric(target, 'terrabase_home_kept_files')).toBeGreaterThanOrEqual(copies[0].files.length);
    });

    await test.step('the old home judged nothing during its own move and deleted nothing', async () => {
      expect(await metric(gru, 'terrabase_home_demotions_total')).toBe(demotions);
      expect(await keptCopies(gru, ledger.id)).toEqual([]);
    });
    await removeDatabase(cells, ledger.id);
  } finally {
    kv.close();
  }
});

test('a lease that names a node that cannot serve leaves the database unserved, and the old home keeps its file without serving it', async ({ cells, request }) => {
  test.setTimeout(240_000);
  const token = await writerToken(request);
  const kv = await Kv.open();
  try {
    const [ledger] = await createLedgers(cells, 1, 'home-unserved');
    const before = await homeLease(kv, ledger.id);
    const gru = node('journey-1');
    const restart = restartAfterKill(gru);
    try {
      await expect.poll(async () => await kv.get(homes, 'node.journey-1'), { message: 'journey-1 node lease expires', timeout: 30_000, intervals: [500] }).toBeNull();
      await kv.put(homes, `home.${ledger.id}`, { ...before.value, home: 'fra', node: 'journey-4', epoch: before.value.epoch + 1 }, before.seq);
      expect(holdsFile(gru, before.value)).toBe(true);
    } finally {
      await restart();
    }

    const copies = await test.step('the old home sets its file aside and serves nothing', async () => {
      await expect.poll(() => keptCopies(gru, ledger.id), { message: 'journey-1 keeps its file as a kept copy', timeout: 60_000 })
        .toEqual([expect.objectContaining({ reason: 'lease_unproven' })]);
      expect(holdsFile(gru, before.value), 'nothing is served from the database path').toBe(false);
      return keptCopies(gru, ledger.id);
    });
    const hash = keptHash(gru, copies[0]);

    await test.step('the database stays unserved, visibly', async () => {
      const unserved = { databaseId: ledger.id, region: 'fra', node: 'journey-4', epoch: before.value.epoch + 1, cause: 'not_writer' };
      for (const watcher of [node('journey-2'), node('journey-3')]) {
        await expect.poll(async () => (await nodeStatus(watcher)).unserved?.filter(entry => entry.databaseId === ledger.id), { message: `${watcher.name} lists the unserved database`, timeout: 60_000 })
          .toEqual([unserved]);
        expect(await metric(watcher, 'terrabase_home_unserved', { region: 'fra' })).toBeGreaterThanOrEqual(1);
        expect(logged(watcher, `Database ${ledger.id} is unserved: its lease names journey-4 in fra at epoch ${before.value.epoch + 1}`), 'one log line per change of state').toBe(1);
      }
      expect(await ledger.refusal(node('journey-2'), token, [[600, 'while-unserved']]), 'the region its lease names refuses the write').toEqual(homeDown);
      expect((await homeLease(kv, ledger.id)).value, 'nothing claims the lease').toMatchObject({ home: 'fra', node: 'journey-4', epoch: before.value.epoch + 1 });
    });

    await test.step('the kept copy stays byte-identical', async () => {
      await expect.poll(() => metric(gru, 'terrabase_home_unserved', { region: 'fra' }), { message: 'journey-1 scanned its leases again' }).toBeGreaterThanOrEqual(1);
      expect(keptHash(gru, (await keptCopies(gru, ledger.id))[0])).toBe(hash);
    });
  } finally {
    kv.close();
  }
});

test('a regional writer role moves only with the target certificate and current primary files', async ({ cells }) => {
  test.setTimeout(480_000);
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
  const writerIs = async (name: string) => {
    await expect.poll(async () => {
      const statuses = await Promise.all([gru, partner].map(nodeStatus));
      return statuses.filter(status => status.writer).map(status => status.node);
    }, { message: `${name} is the only gru writer`, timeout: 60_000 }).toEqual([name]);
  };
  try {
    const owner = await kv.get<Record<string, unknown> & { node: string; epoch: number }>(owners, 'gru');
    expect(owner?.value.node).toBe('journey-1');
    const refusals = await metric(partner, 'terrabase_home_refusals_total', { scope: 'writer' });
    await kv.put(owners, 'gru', {
      ...owner!.value, node: 'journey-3', epoch: owner!.value.epoch + 1, previous: 'journey-1', activated: true, transfer: null, version: 2,
      prepared: { source: 'journey-1', target: 'journey-3', epoch: owner!.value.epoch, operation: null },
    }, owner!.seq);
    await expect.poll(() => metric(partner, 'terrabase_home_refusals_total', { scope: 'writer', reason: 'unproven' }), { message: 'journey-3 refuses the rewritten owner record' })
      .toBeGreaterThan(refusals);
    expect((await nodeStatus(partner)).writer).toBe(false);
    const rewritten = await kv.get<{ node: string }>(owners, 'gru');
    await kv.remove(owners, 'gru', rewritten!.seq);
    await writerIs('journey-1');
    console.log('rewritten owner record refused; a deleted key brought journey-1 back as writer at epoch 1 with current primary files');

    const drained = await peer(gru, '/v1/rollout/drain?target=journey-3', undefined, 'POST');
    expect(drained.status, drained.body).toBe(200);
    await writerIs('journey-3');
    await expect.poll(async () => (await homeLease(kv, ledger.id)).value, { message: 'the new writer\'s lease carries the adopted position' })
      .toMatchObject({ node: 'journey-3', adopted: adopted.adopted });
    await ledger.write(cells, 'gru', [[800, 'written-on-journey-3']]);
    await ledger.compare(cells);
    const lease = (await homeLease(kv, ledger.id)).value;
    await expect.poll(async () => (await peer(gru, `/v1/sync/organizations/${lease.organizationId}/projects/${lease.projectId}/databases/${ledger.id}/held`)).status,
      { message: 'journey-1 holds a replica newer than its stale primary file' }).toBe(200);
    const stale = await metric(gru, 'terrabase_home_refusals_total', { scope: 'writer' });
    const current = await kv.get<{ node: string }>(owners, 'gru');
    await kv.remove(owners, 'gru', current!.seq);
    await expect.poll(() => metric(gru, 'terrabase_home_refusals_total', { scope: 'writer' }), { message: 'journey-1 refuses to become writer over stale primary files' })
      .toBeGreaterThan(stale);
    expect((await nodeStatus(gru)).writer).toBe(false);
    const recreated = await kv.get<Record<string, unknown> & { node: string }>(owners, 'gru');
    expect(recreated?.value.node).toBe('journey-1');
    await kv.put(owners, 'gru', { ...recreated!.value, node: 'journey-3', epoch: 1, previous: null, activated: false, transfer: null }, recreated!.seq);
    await writerIs('journey-3');
    await ledger.compare(cells, [[800, 'written-on-journey-3']]);
    const back = await peer(partner, '/v1/rollout/drain?target=journey-1', undefined, 'POST');
    expect(back.status, back.body).toBe(200);
    await writerIs('journey-1');
    await ledger.write(cells, 'gru', [[801, 'after-proofs']]);
    await ledger.compare(cells);
    await expect.poll(async () => (await homeLease(kv, ledger.id)).value, { message: 'the lease carries the adopted position back to journey-1' })
      .toMatchObject({ node: 'journey-1', adopted: adopted.adopted });
    const prepared = (await keptCopies(gru, ledger.id)).filter(copy => copy.reason === 'handoff_prepare');
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
    await expect.poll(async () => (await homeLease(kv, ledger.id)).value.session, { message: 'journey-1 rewrites its lease under its new session', timeout: 60_000 }).not.toBe(moved.session);
    expect((await homeLease(kv, ledger.id)).value, 'a restart keeps the adopted position').toMatchObject({ node: 'journey-1', epoch: moved.epoch, adopted: moved.adopted });
    await keptUnchanged();
    await ledger.write(cells, 'gru', [[802, 'after-restart']]);
    await ledger.compare(cells);
    await removeDatabase(cells, ledger.id);
  } finally {
    kv.close();
  }
});

test('a region writer that stays down keeps its homes, and every acknowledged row survives in both regions', async ({ cells, request }) => {
  test.setTimeout(2 * (outageHold + 150_000));
  const token = await writerToken(request);
  const kv = await Kv.open();
  try {
    for (const round of [1, 2]) {
      const ledgers = await createLedgers(cells, 3, `home-outage-${round}`);
      const before = await Promise.all(ledgers.map(ledger => homeLease(kv, ledger.id)));
      const watchers = [node('journey-2'), node('journey-4')];
      const lapses = await Promise.all(watchers.map(watcher => metric(watcher, 'terrabase_home_lapses_total')));
      const { killedAt, restart } = await killDuringBurst(ledgers, token);
      console.log(`round ${round}: journey-1 killed after ${ledgers.reduce((sum, ledger) => sum + ledger.acknowledged.length, 0)} acknowledged rows`);
      try {
        await holdOutage(kv, ledgers, killedAt, before, token);
        for (const [index, watcher] of watchers.entries()) {
          expect(await metric(watcher, 'terrabase_home_lapses_total') - lapses[index], `${watcher.name} counted this round's lapses and moved nothing`)
            .toBeGreaterThanOrEqual(ledgers.length);
        }
      } finally {
        await restart();
      }
      await recovered(ledgers, token);
      for (const [index, ledger] of ledgers.entries()) {
        const after = (await homeLease(kv, ledger.id)).value;
        expect(after, 'the returning writer resumes as home').toMatchObject({ home: 'gru', node: 'journey-1' });
        expect(after.epoch, 'only its own activation raised the epoch').toBeLessThanOrEqual(before[index].value.epoch + 1);
      }
      for (const ledger of ledgers) await ledger.drop(token, node('journey-1'));
    }
  } finally {
    kv.close();
  }
});

test('a writer killed right after an acknowledgement that fra pulled keeps the home and the row', async ({ cells, request }) => {
  test.setTimeout(outageHold + 180_000);
  const token = await writerToken(request);
  const kv = await Kv.open();
  try {
    const ledgers = await createLedgers(cells, 2, 'home-pulled');
    for (const ledger of ledgers) {
      await ledger.write(cells, 'gru', [[500, 'last-before-kill']]);
      await ledger.compare(cells);
    }
    const before = await Promise.all(ledgers.map(ledger => homeLease(kv, ledger.id)));
    const killedAt = Date.now();
    const restart = restartAfterKill(node('journey-1'));
    try {
      await holdOutage(kv, ledgers, killedAt, before, token);
      for (const ledger of ledgers) expect(await ledger.readNode(node('journey-2'), token), 'fra serves the pulled row while the home is down').toContainEqual([500, 'last-before-kill']);
    } finally {
      await restart();
    }
    await recovered(ledgers, token);
    for (const ledger of ledgers) expect(await ledger.readNode(node('journey-1'), token)).toContainEqual([500, 'last-before-kill']);
    for (const ledger of ledgers) await ledger.drop(token, node('journey-1'));
  } finally {
    kv.close();
  }
});
