import { expect } from '@playwright/test';
import { test, database, json, name, project, removeDatabase, sql } from '../placement/support';
import { auth } from '../support/stack';
import { journey, saveSecret } from '../support/state';
import { backupStore, continuity, crashWrite, endpoint, issuerDown, Ledger, metric, probe, regions, restartAfterKill, topology } from './support';
import type { Cells } from '../placement/support';

async function neverUnauthorized(cells: Cells, url: string): Promise<void> {
  const answers: string[] = [];
  await expect.poll(async () => {
    const response = await cells.get(url).catch(() => undefined);
    if (response === undefined) return 0;
    const body = await response.text();
    answers.push(`${response.status()} ${response.status() === 503 ? body.slice(0, 160) : ''}`);
    expect(response.status(), `a valid token after a restart: ${answers.join(' | ')}`).not.toBe(401);
    return response.status();
  }, { message: 'the restarted node answers a valid token without 401' }).toBe(200);
  console.log(`first answers to a valid token after the restart: ${[...new Set(answers)].join(' | ')}`);
}

async function keysPending(cells: Cells, url: string, node: Parameters<typeof metric>[0]): Promise<void> {
  const response = await cells.get(url);
  const body = await response.text();
  expect(response.status(), `a valid token before the restarted node could load the signing keys: ${body}`).toBe(503);
  expect(JSON.parse(body).error.code).toBe('auth_unavailable');
  expect(response.headers()['retry-after']).toBe('1');
  expect(await metric(node, 'terrabase_auth_signing_keys_loaded')).toBe(0);
}

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
  let issuer: (() => Promise<void>) | undefined;
  try {
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await ledger.write(cells, 'gru', [[1, 'before-gru']]);
    await ledger.compare(cells);
    await ledger.write(cells, 'fra', [[2, 'before-fra']]);
    await ledger.compare(cells);
    const retained = [...ledger.acknowledged];
    const current = journey();
    const grant = await json<{ access_token: string }>(await request.post(`${auth}/terrascale/oauth/token`, {
      form: { grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey, project: current.projectId, scope: 'sql:read sql:write databases:read databases:write' },
    }));
    saveSecret('fault-token', grant.access_token);
    const writer = (await topology()).find(node => node.region === 'gru')!;
    issuer = issuerDown();
    const writing = crashWrite(writer, ledger, grant.access_token);
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
    await keysPending(cells, `${endpoint('gru')}${database(created.id)}`, writer);
    await issuer();
    issuer = undefined;
    await neverUnauthorized(cells, `${endpoint('gru')}${database(created.id)}`);
    await ledger.compare(cells, retained);
    await ledger.write(cells, 'gru', [[3, 'after-gru']]);
    await ledger.write(cells, 'fra', [[4, 'after-fra']]);
    await ledger.compare(cells);
  } finally {
    if (issuer) await issuer();
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

test('history lost while the backup store is unreachable is one hole each time, also across a generation restart and a node restart, and restores refuse every hole', async ({ cells }) => {
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

    await backupStore('down');
    outage = true;
    const inside = await churn(75);
    const duringSent = Date.now();
    await ledger.write(cells, 'gru', [[2, 'during-outage']]);
    const duringAcked = Date.now();
    await expect.poll(() => metric(writer, 'terrabase_backup_holes'), { message: 'the discarded history becomes exactly one hole' }).toBe(holes + 1);
    expect(await metric(writer, 'terrabase_backup_holes_recorded_total')).toBe(recordedHoles + 1);
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
    const uploaded = await metric(writer, 'terrabase_backup_uploaded_bytes_total', 'kind="segment"');
    await ledger.write(cells, 'gru', [[3, 'after-outage']]);
    await expect.poll(() => metric(writer, 'terrabase_backup_uploaded_bytes_total', 'kind="segment"'), { message: 'later passes store the writes after the outage' }).toBeGreaterThan(uploaded);
    const closed = await continuity(writer, created.id);
    expect(closed).toMatchObject({ replacing: false, storedIncarnation: closed.incarnation });
    expect(closed.holes).toHaveLength(1);
    expect(closed.holes[0]).toMatchObject({ incarnation: closed.incarnation, from: opened.holes[0]!.from, open: false });
    expect(closed.holes[0]!.to).toBeGreaterThanOrEqual(opened.holes[0]!.to);
    expect(closed.holes[0]!.to).toBeLessThanOrEqual(closed.storedPosition!);

    const unplaceable = await metric(writer, 'terrabase_backup_generation_restarts_total', 'reason="log_unplaceable"');
    await backupStore('down');
    outage = true;
    await sql(cells, endpoint('gru'), created.id, 'ALTER TABLE fault_rows ADD COLUMN note TEXT');
    await expect.poll(() => metric(writer, 'terrabase_backup_generation_restarts_total', 'reason="log_unplaceable"'),
      { message: 'the schema change restarts the generation' }).toBe(unplaceable + 1);
    const kept = await continuity(writer, created.id);
    expect(kept, 'the restart keeps the last stored position until a replacement is stored').toMatchObject({ replacing: true, storedIncarnation: kept.incarnation });
    expect(kept.storedPosition).toBeGreaterThanOrEqual(closed.storedPosition!);
    const insideSecond = await churn(75);
    await expect.poll(() => metric(writer, 'terrabase_backup_holes'), { message: 'history discarded while the replacement cannot be stored is one more hole' }).toBe(holes + 2);
    expect(await metric(writer, 'terrabase_backup_holes_recorded_total')).toBe(recordedHoles + 2);
    const twoHoles = (await continuity(writer, created.id)).holes;
    expect(twoHoles).toHaveLength(2);
    expect(twoHoles[1]).toMatchObject({ incarnation: kept.incarnation, from: kept.storedPosition, open: true });
    await churn(65);
    await expect.poll(async () => (await continuity(writer, created.id)).holes[1]!.to,
      { message: 'a later discard moves the open hole to the new start of the change log' }).toBeGreaterThan(twoHoles[1]!.to);
    expect(await metric(writer, 'terrabase_backup_holes_recorded_total')).toBe(recordedHoles + 2);

    const restart = restartAfterKill(writer);
    await restart(async () => {
      expect(await probe(writer, '/readyz')).toBe(503);
      expect(await metric(writer, 'terrabase_catalog_closed', 'reason="store_unavailable"')).toBe(1);
      await backupStore('up');
      outage = false;
    });
    await neverUnauthorized(cells, `${endpoint('gru')}${database(created.id)}`);
    await expect.poll(() => probe(writer, '/readyz'), { message: 'the restarted writer is ready' }).toBe(200);
    expect(await metric(writer, 'terrabase_catalog_closed', 'reason="store_unavailable"')).toBe(0);
    await expect.poll(() => probe(writer, '/backupz'), { message: 'a replacement snapshot after the restart closes the second hole' }).toBe(200);
    const settled = await continuity(writer, created.id);
    expect(settled.holes.map(hole => hole.open)).toEqual([false, false]);
    expect(settled.holes[1]!.from).toBe(kept.storedPosition);
    expect(await metric(writer, 'terrabase_backup_holes_recorded_total')).toBe(0);
    expect(await metric(writer, 'terrabase_backup_holes')).toBe(holes + 2);

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
  } finally {
    if (outage) await backupStore('up');
    for (const copy of restored) await removeDatabase(cells, copy);
    await removeDatabase(cells, created.id);
  }
});
