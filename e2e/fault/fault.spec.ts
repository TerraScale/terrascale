import { randomBytes } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { readFileSync, renameSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect } from '@playwright/test';
import { test, database, json, name, project, removeDatabase, sql } from '../placement/support';
import { auth, dash, e2eRoot, stateDir } from '../support/stack';
import { keyId } from '../support/api-key';
import { journey, ownerSession, saveSecret } from '../support/state';
import { receiptHandoff } from './receipt-handoff';
import { backupPuts, backupStore, continuity, crashWrite, endpoint, faultId, holeRecords, Ledger, metric, nodeHoles, nodes, nodeRequest, nodeStatus, plantHole, probe, regions, restartAfterKill, scrape, tokenReady, topology } from './support';

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
      form: { grant_type: 'client_credentials', client_id: keyId(current.apiKey), client_secret: current.apiKey, project: current.projectId, scope: 'sql:read sql:write databases:read databases:write' },
    }));
    saveSecret('fault-token', grant.access_token);
    const writer = (await topology()).find(node => node.region === 'gru')!;
    const writing = crashWrite(writer, ledger, grant.access_token);
    await writing.sent;
    const restart = restartAfterKill(writer, true, { token: grant.access_token, path: database(created.id) });
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

    const firstMark = (await backupPuts(shipped.file)).length;
    await backupStore('down');
    outage = true;
    const inside = await churn(75);
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
    await expect.poll(counted, { message: 'history discarded while the replacement cannot be stored is one more hole, counted once' }).toEqual({ holes: holes + 2, recorded: recordedHoles + 2 });
    const twoHoles = (await continuity(writer, created.id)).holes;
    expect(twoHoles).toHaveLength(2);
    expect(twoHoles[1]).toMatchObject({ incarnation: kept.incarnation, from: kept.storedPosition, open: true });
    await churn(65);
    await expect.poll(async () => (await continuity(writer, created.id)).holes[1]!.to,
      { message: 'a later discard moves the open hole to the new start of the change log' }).toBeGreaterThan(twoHoles[1]!.to);
    expect(await metric(writer, 'terrabase_backup_holes_recorded_total')).toBe(recordedHoles + 2);

    const beforeRestart = await nodeHoles(writer);
    const restart = restartAfterKill(writer, true, undefined, async () => {
      expect(await probe(writer, '/readyz')).toBe(503);
      expect(await metric(writer, 'terrabase_catalog_closed', 'reason="store_unavailable"')).toBe(1);
      await backupStore('up');
      outage = false;
    });
    await restart();
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
    expect(new Set(afterRestart).size, `every hole on the node is recorded once: ${afterRestart.join(', ')}`).toBe(afterRestart.length);
    expect(afterRestart.filter(hole => hole.startsWith(`${settled.file} `))).toEqual(beforeRestart.filter(hole => hole.startsWith(`${settled.file} `)));
    const restartHoles = afterRestart.filter(hole => !beforeRestart.includes(hole));
    console.log(`holes the restarted writer recorded for history it discarded before its backups resumed: ${JSON.stringify(restartHoles)}`);
    await expect.poll(counted, { message: 'the restarted writer counts only the holes the node did not hold before, each once' })
      .toEqual({ holes: holes + 2 + restartHoles.length, recorded: restartHoles.length });

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
  } finally {
    if (unlisted) await holeRecords('listed');
    if (restored) await removeDatabase(cells, restored);
    await removeDatabase(cells, created.id);
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
  } finally {
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
  }
});
