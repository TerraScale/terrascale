import { expect } from '@playwright/test';
import { test, database, json, name, project, removeDatabase, sql, type Cells } from '../placement/support';
import { crashWrite, endpoint, Kv, Ledger, logged, metric, node, nodeStatus, peer, regions, restartAfterKill, topology, writerToken, type HomeLease, type Row } from './support';

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

async function holdOutage(cells: Cells, kv: Kv, ledgers: Ledger[], killedAt: number, token: string): Promise<void> {
  const retained = new Map(ledgers.map(ledger => [ledger.id, [...ledger.acknowledged]]));
  const fra = node('journey-2');
  let localWrites = 0;
  await expect.poll(async () => {
    for (const ledger of ledgers) {
      const lease = (await homeLease(kv, ledger.id)).value;
      expect(lease, 'a lapsed home lease is not moved to another node').toMatchObject({ home: 'gru', node: 'journey-1' });
      const rows = await ledger.served(cells, 'fra');
      for (const row of retained.get(ledger.id)!) expect(rows, 'fra keeps serving every row it had pulled').toContainEqual(row);
    }
    if (Date.now() - killedAt > outageHold / 2 && localWrites < ledgers.length) {
      const ledger = ledgers[localWrites++];
      await ledger.direct(fra, token, [[9000 + localWrites, `during-outage-${localWrites}`]]);
    }
    return Date.now() - killedAt >= outageHold;
  }, { message: `the home stays on journey-1 and fra serves its replicas for ${outageHold / 1000} s`, timeout: outageHold + 30_000, intervals: [1000] }).toBe(true);
  for (const peerNode of ['journey-2', 'journey-4']) {
    expect(await metric(node(peerNode), 'terrabase_home_lapses_total'), `${peerNode} saw the lapse and moved nothing`).toBeGreaterThanOrEqual(ledgers.length);
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

async function recovered(cells: Cells, ledgers: Ledger[]): Promise<void> {
  for (const ledger of ledgers) {
    await ledger.compare(cells, [...ledger.acknowledged]);
    for (const row of ledger.failed) {
      if (ledger.acknowledged.some(acknowledged => acknowledged[0] === row[0])) continue;
      expect(ledger.submitted).toContainEqual(row);
    }
  }
}

test('a region writer that stays down keeps its homes, and every acknowledged row survives in both regions', async ({ cells, request }) => {
  test.setTimeout(2 * (outageHold + 150_000));
  const token = await writerToken(request);
  const kv = await Kv.open();
  try {
    for (const round of [1, 2]) {
      const ledgers = await createLedgers(cells, 3, `home-outage-${round}`);
      const before = await Promise.all(ledgers.map(ledger => homeLease(kv, ledger.id)));
      const { killedAt, restart } = await killDuringBurst(ledgers, token);
      console.log(`round ${round}: journey-1 killed after ${ledgers.reduce((sum, ledger) => sum + ledger.acknowledged.length, 0)} acknowledged rows`);
      try {
        await holdOutage(cells, kv, ledgers, killedAt, token);
      } finally {
        await restart();
      }
      await recovered(cells, ledgers);
      for (const [index, ledger] of ledgers.entries()) {
        expect((await homeLease(kv, ledger.id)).value, 'the returning writer resumes as home at the same epoch').toMatchObject({ home: 'gru', node: 'journey-1', epoch: before[index].value.epoch });
      }
      for (const ledger of ledgers) await removeDatabase(cells, ledger.id);
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
    const killedAt = Date.now();
    const restart = restartAfterKill(node('journey-1'));
    try {
      await holdOutage(cells, kv, ledgers, killedAt, token);
    } finally {
      await restart();
    }
    await recovered(cells, ledgers);
    for (const ledger of ledgers) expect(await ledger.served(cells, 'gru')).toContainEqual([500, 'last-before-kill']);
    for (const ledger of ledgers) await removeDatabase(cells, ledger.id);
  } finally {
    kv.close();
  }
});

test('an old home returning to a non-writer lease left by a failed takeover keeps its file and takes the home back', async ({ cells }) => {
  test.setTimeout(240_000);
  const kv = await Kv.open();
  try {
    const [ledger] = await createLedgers(cells, 1, 'home-reclaim');
    const before = await homeLease(kv, ledger.id);
    const restart = restartAfterKill(node('journey-1'));
    try {
      await expect.poll(async () => await kv.get(homes, 'node.journey-1'), { message: 'journey-1 node lease expires', timeout: 30_000, intervals: [500] }).toBeNull();
      const broken = { ...before.value, home: 'fra', node: 'journey-4', epoch: before.value.epoch + 1 };
      await kv.put(homes, `home.${ledger.id}`, broken, before.seq);
      await expect.poll(async () => {
        const response = await cells.post(`${endpoint('fra')}${database(ledger.id)}/sql/execute`, { data: { statements: [{ sql: 'SELECT count(*) FROM fault_rows' }] } });
        return response.status() === 200 && response.headers()['x-terrabase-region'] === 'fra';
      }, { message: 'fra follows the broken lease and nothing serves the database as home' }).toBe(false);
    } finally {
      await restart();
    }
    await expect.poll(async () => (await homeLease(kv, ledger.id)).value, { message: 'journey-1 takes the home back', timeout: 60_000 })
      .toMatchObject({ home: 'gru', node: 'journey-1', epoch: before.value.epoch + 2 });
    expect(logged(node('journey-1'), 'This node took a database back as home')).toBeGreaterThanOrEqual(1);
    expect(await metric(node('journey-1'), 'terrabase_home_reclaims_total')).toBeGreaterThanOrEqual(1);
    await ledger.compare(cells, [...ledger.acknowledged]);
    await ledger.write(cells, 'gru', [[600, 'after-reclaim-gru']]);
    await ledger.write(cells, 'fra', [[601, 'after-reclaim-fra']]);
    await ledger.compare(cells);
    await removeDatabase(cells, ledger.id);
  } finally {
    kv.close();
  }
});

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
    expect(await metric(node('journey-4'), 'terrabase_home_refusals_total', { reason: 'not_writer' })).toBe(refusals + 1);
    expect(await homeLease(kv, ledger.id)).toEqual(before);
    await ledger.write(cells, 'gru', [[700, 'after-refused-move']]);
    await ledger.write(cells, 'fra', [[701, 'after-refused-move-fra']]);
    await ledger.compare(cells);
    await removeDatabase(cells, ledger.id);
  } finally {
    kv.close();
  }
});

test('a regional writer role moves only with the target certificate and current primary files', async ({ cells }) => {
  test.setTimeout(300_000);
  const kv = await Kv.open();
  const [ledger] = await createLedgers(cells, 1, 'writer-proof');
  const gru = node('journey-1');
  const partner = node('journey-3');
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
    await removeDatabase(cells, ledger.id);
  } finally {
    kv.close();
  }
});
