import { expect } from '@playwright/test';
import { test, database, json, name, project, removeDatabase, sql } from '../placement/support';
import { auth } from '../support/stack';
import { journey, saveSecret } from '../support/state';
import { backupStore, crashWrite, endpoint, Ledger, logLength, logLines, metric, probe, regions, restartAfterKill, topology } from './support';

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
    const current = journey();
    const grant = await json<{ access_token: string }>(await request.post(`${auth}/terrascale/oauth/token`, {
      form: { grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey, project: current.projectId, scope: 'sql:read sql:write databases:read databases:write' },
    }));
    saveSecret('fault-token', grant.access_token);
    const writer = (await topology()).find(node => node.region === 'gru')!;
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

test('history lost while the backup store is unreachable is one hole, survives a restart and is refused by restores', async ({ cells }) => {
  const writer = (await topology()).find(node => node.region === 'gru')!;
  const created = await json<{ id: string }>(await cells.post(`${endpoint('gru')}${project()}/databases`, {
    data: { name: name('backup-hole'), regions: ['gru'] },
  }), 201);
  const ledger = new Ledger(created.id);
  const restored: string[] = [];
  let outage = false;
  try {
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_rows (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await ledger.write(cells, 'gru', [[1, 'before-outage']]);
    const storedAt = Date.now();
    await sql(cells, endpoint('gru'), created.id, 'CREATE TABLE fault_blob (id INTEGER PRIMARY KEY, body BLOB NOT NULL)');
    await sql(cells, endpoint('gru'), created.id, 'INSERT INTO fault_blob (id, body) VALUES (1, zeroblob(1))');
    await expect.poll(() => probe(writer, '/backupz'), { message: 'the writer ships every file before the outage' }).toBe(200);
    const holes = await metric(writer, 'terrabase_backup_holes');
    const generations = await metric(writer, 'terrabase_backup_generations_total');
    const logStart = logLength(writer);

    await backupStore('down');
    outage = true;
    let inside = 0;
    for (let batch = 0; batch < 75; batch++) {
      if (batch === 38) inside = Date.now();
      const response = await cells.post(`${endpoint('gru')}${database(created.id)}/sql/execute`, {
        data: { statements: Array.from({ length: 2 }, (_, index) => ({ sql: `UPDATE fault_blob SET body = zeroblob(${1_048_576 + batch * 2 + index}) WHERE id = 1` })) },
      });
      expect(response.status(), await response.text()).toBe(200);
    }
    await ledger.write(cells, 'gru', [[2, 'during-outage']]);
    await expect.poll(() => metric(writer, 'terrabase_backup_holes'), { message: 'the discarded history becomes exactly one hole' }).toBe(holes + 1);
    expect(await metric(writer, 'terrabase_backup_holes_recorded_total')).toBe(1);
    const events = logLines(writer, logStart, `/${created.id}/nodes/`);
    const recorded = events.flatMap(line => /lost its history from position (\d+) to (\d+) before it was stored/.exec(line) ?? []);
    expect(recorded, events.join('\n')).toHaveLength(3);
    const [cursor, start] = [Number(recorded[1]), Number(recorded[2])];
    const discards = events.slice(0, events.findIndex(line => line.includes('lost its history')))
      .map(line => /its history now starts at position (\d+), oldest retained change (\d+)/.exec(line)).filter(match => match !== null);
    expect(discards.length, events.join('\n')).toBeGreaterThan(0);
    const discarded = discards[discards.length - 1]!;
    expect(Number(discarded[1])).toBe(Number(discarded[2]));
    expect(start).toBe(Number(discarded[1]));
    expect(cursor).toBeLessThan(start);
    expect(events.filter(line => line.includes('differs from its oldest retained change'))).toEqual([]);
    expect(await probe(writer, '/backupz')).toBe(503);
    expect(await probe(writer, '/readyz')).toBe(200);

    await backupStore('up');
    outage = false;
    await expect.poll(() => probe(writer, '/backupz'), { message: 'a replacement snapshot makes the writer healthy again' }).toBe(200);
    expect(await metric(writer, 'terrabase_backup_generations_total')).toBeGreaterThan(generations);
    expect(await probe(writer, '/readyz')).toBe(200);
    expect(await metric(writer, 'terrabase_backup_holes')).toBe(holes + 1);
    const uploaded = await metric(writer, 'terrabase_backup_uploaded_bytes_total');
    await ledger.write(cells, 'gru', [[3, 'after-outage']]);
    await expect.poll(() => metric(writer, 'terrabase_backup_uploaded_bytes_total'), { message: 'later passes store the writes after the outage' }).toBeGreaterThan(uploaded);
    expect(await probe(writer, '/backupz')).toBe(200);
    expect(await metric(writer, 'terrabase_backup_holes')).toBe(holes + 1);
    expect(await metric(writer, 'terrabase_backup_holes_recorded_total')).toBe(1);

    const restart = restartAfterKill(writer);
    await restart();
    await expect.poll(() => probe(writer, '/readyz'), { message: 'the restarted writer is ready' }).toBe(200);
    expect((await cells.get(`${endpoint('gru')}${database(created.id)}`)).status(), 'the first request after readiness is authenticated').toBe(200);
    await expect.poll(() => metric(writer, 'terrabase_backup_holes'), { message: 'the restarted writer publishes the durable hole total' }).toBe(holes + 1);
    await expect.poll(() => probe(writer, '/backupz'), { message: 'the restarted writer resumes its generation' }).toBe(200);
    expect(await metric(writer, 'terrabase_backup_holes_recorded_total')).toBe(0);

    const refused = await cells.post(`${endpoint('gru')}${database(created.id)}/point-in-time/restore`, {
      data: { pointInTime: new Date(inside).toISOString(), name: name('hole-inside') },
    });
    expect(refused.status()).toBe(409);
    const error = (await refused.json() as { error: { code: string; message: string; unavailableFrom: string; unavailableTo: string } }).error;
    expect(error.code).toBe('backup_history_incomplete');
    expect(error.message).toContain('was lost before it was stored');
    const [from, to] = [Date.parse(error.unavailableFrom), Date.parse(error.unavailableTo)];
    expect(from).toBeLessThan(inside);
    expect(to).toBeGreaterThan(inside);
    console.log(`hole of ${created.id}: unavailable ${error.unavailableFrom} to ${error.unavailableTo}; ${holes + 1} on ${writer.name} after its restart`);

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
    expect(await restore(to)).toEqual([[1, 'before-outage'], [2, 'during-outage']]);
    console.log(`restores of ${created.id}: ${JSON.stringify(before)} at ${new Date(second).toISOString()}, before the hole; both rows at the replacement snapshot`);
  } finally {
    if (outage) await backupStore('up');
    for (const copy of restored) await removeDatabase(cells, copy);
    await removeDatabase(cells, created.id);
  }
});
