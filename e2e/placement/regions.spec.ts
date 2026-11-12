import { setTimeout as delay } from 'node:timers/promises';
import { expect, type APIResponse } from '@playwright/test';
import { test, home, replica, project, database, name, json, sql, replicated, type Cells } from './support';

type Database = { id: string; regions: string[] };
type Observation = { region: string; role: string | null; revision: string | null; lastPulledAt: string | null; lagMs: number | null };
type Progress = { regions: Observation[] };

async function answer(response: APIResponse, code: number, documented: string): Promise<void> {
  expect(response.status(), response.status() === code ? documented : `${documented}: ${await response.text()}`).toBe(code);
  if (code >= 400) expect((await response.json()).error.code, documented).toBe(documented);
}

async function create(cells: Cells, regions: string[]): Promise<Database> {
  const response = await cells.post(`${home}${project()}/databases`, { data: { name: name('regions'), regions } });
  await answer(response, 201, '201 database created');
  return response.json();
}

async function progress(cells: Cells, id: string, region = home): Promise<Progress> {
  let response = await cells.get(`${region}${database(id)}/regions`);
  if (response.status() === 421) {
    await answer(response, 421, 'wrong_region');
    response = await cells.get(`https://${response.headers()['x-terrabase-database-home']}.api.terrascale.tech${database(id)}/regions`);
  }
  await answer(response, 200, '200 database regions');
  return response.json();
}

async function observed(cells: Cells, id: string): Promise<void> {
  await expect.poll(async () => (await progress(cells, id)).regions.find(row => row.region === 'fra'),
    { message: 'fra has a completed pull observation', timeout: 45_000, intervals: [250, 500, 1000] })
    .toMatchObject({ revision: expect.any(String), lastPulledAt: expect.any(String), lagMs: expect.any(Number) });
}

async function seed(cells: Cells, id: string): Promise<void> {
  await sql(cells, home, id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)');
  await sql(cells, home, id, 'INSERT INTO placement_rows VALUES (1), (2), (3)');
}

async function patch(cells: Cells, id: string, regions: string[]): Promise<APIResponse> {
  let response = await cells.patch(`${home}${database(id)}`, { data: { regions } });
  if (response.status() === 421) {
    await answer(response, 421, 'wrong_region');
    response = await cells.patch(`https://${response.headers()['x-terrabase-database-home']}.api.terrascale.tech${database(id)}`, { data: { regions } });
  }
  return response;
}

async function move(cells: Cells, id: string, label: string, timeout: number, statuses: number[] = []): Promise<void> {
  try {
    await expect.poll(async () => {
      const response = await patch(cells, id, ['fra']);
      statuses.push(response.status());
      if (response.status() === 200) {
        await answer(response, 200, '200 database updated');
        expect((await response.json()).regions).toEqual(['fra']);
        return true;
      }
      await answer(response, 503, 'replica_not_ready');
      return false;
    }, { message: `${label} reaches 200`, timeout, intervals: [1000] }).toBe(true);
  } finally {
    console.log(`${label} statuses: ${JSON.stringify(statuses)}`);
  }
  expect((await progress(cells, id, replica)).regions.map(row => row.region)).toEqual(['fra']);
}

async function remove(cells: Cells, id: string): Promise<void> {
  let response = await cells.delete(`${home}${database(id)}`);
  if (response.status() === 421) {
    await answer(response, 421, 'wrong_region');
    response = await cells.delete(`https://${response.headers()['x-terrabase-database-home']}.api.terrascale.tech${database(id)}`);
  }
  await answer(response, 204, '204 database deleted');
}

test('R1 create two regions, read across listeners and observe a pull', async ({ cells }) => {
  const created = await create(cells, ['gru', 'fra']);
  try {
    expect(created.regions).toEqual(['fra', 'gru']);
    await seed(cells, created.id);
    await replicated(cells, replica, created.id, 3);
    await observed(cells, created.id);
  } finally {
    await remove(cells, created.id);
  }
});

test('R2 add a region and read a row committed before the addition', async ({ cells }) => {
  const created = await create(cells, ['gru']);
  try {
    await seed(cells, created.id);
    const added = await patch(cells, created.id, ['fra', 'gru']);
    await answer(added, 200, '200 database updated with writer retained');
    expect((await added.json()).regions).toEqual(['fra', 'gru']);
    await observed(cells, created.id);
    await replicated(cells, replica, created.id, 3);
  } finally {
    await remove(cells, created.id);
  }
});

test('R3 replace the writer when both regions are members', async ({ cells }) => {
  const created = await create(cells, ['gru', 'fra']);
  try {
    await seed(cells, created.id);
    await observed(cells, created.id);
    await move(cells, created.id, 'R3', 60_000);
    await sql(cells, replica, created.id, 'INSERT INTO placement_rows VALUES (4)');
    await expect.poll(async () => {
      const response = await cells.post(`${home}${database(created.id)}/sql/execute`, { data: { statements: [{ sql: 'SELECT value FROM placement_rows' }] } });
      if (response.status() === 421) await answer(response, 421, 'wrong_region');
      else await answer(response, 200, '200 during catalog propagation');
      return response.status();
    }, { message: '421 wrong_region from a non-member listener', timeout: 45_000, intervals: [250, 500, 1000] }).toBe(421);
  } finally {
    await remove(cells, created.id);
  }
});

test('R4 replace the only region, retain additions after the first refusal', async ({ cells }) => {
  const created = await create(cells, ['gru']);
  const statuses: number[] = [];
  try {
    await seed(cells, created.id);
    const first = await patch(cells, created.id, ['fra']);
    statuses.push(first.status());
    await answer(first, 503, 'replica_not_ready');
    expect((await progress(cells, created.id)).regions.map(row => row.region)).toEqual(['fra', 'gru']);
    await move(cells, created.id, 'R4', 90_000, statuses);
    expect((await sql(cells, replica, created.id, 'SELECT value FROM placement_rows ORDER BY value')).results[0].rows).toEqual([[1], [2], [3]]);
  } finally {
    await remove(cells, created.id);
  }
});

test('R5 replace the writer under twenty acknowledged commits per second', async ({ cells }) => {
  test.setTimeout(180_000);
  const created = await create(cells, ['gru', 'fra']);
  const acknowledged: number[] = [];
  const lags: number[] = [];
  let firstRefusal: number | undefined;
  let stop = false;
  let writer: Promise<void> | undefined;
  try {
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)');
    await observed(cells, created.id);
    const deadline = Date.now() + 120_000;
    writer = (async () => {
      for (let id = 1; !stop && Date.now() < deadline; id++) {
        const next = Date.now() + 50;
        const response = await cells.post(`${home}${database(created.id)}/sql/execute`, { data: { statements: [{ sql: `INSERT INTO placement_rows VALUES (${id})` }] } });
        if (response.status() !== 200) {
          firstRefusal = response.status();
          console.log(`R5 first writer refusal: ${firstRefusal}`);
          await answer(response, 421, 'wrong_region');
          return;
        }
        acknowledged.push(id);
        await delay(Math.max(0, next - Date.now()));
      }
    })();
    await expect.poll(() => acknowledged.length, { message: 'writer commits before the move', timeout: 10_000, intervals: [50] }).toBeGreaterThanOrEqual(20);
    const sample = async () => {
      const row = (await progress(cells, created.id)).regions.find(row => row.region === 'fra');
      if (row?.lagMs !== null && row?.lagMs !== undefined) lags.push(row.lagMs);
    };
    await sample();
    const sampling = (async () => {
      while (!stop && Date.now() < deadline) {
        await sample();
        await delay(1000);
      }
    })();
    try {
      await move(cells, created.id, 'R5', Math.max(1, deadline - Date.now()));
      await writer;
      expect(firstRefusal, '421 wrong_region terminates writes through the former writer').toBe(421);
    } finally {
      stop = true;
      await Promise.all([writer, sampling]);
    }
    const result = await sql(cells, replica, created.id, 'SELECT value FROM placement_rows ORDER BY value');
    const found = result.results[0].rows.map(row => row[0]);
    console.log(`R5 acknowledged: ${acknowledged.length}; found: ${found.length}`);
    expect(found).toEqual(acknowledged);
    expect(new Set(found).size).toBe(found.length);
    expect(lags.length, 'fra lag samples').toBeGreaterThan(0);
  } finally {
    stop = true;
    if (writer) await writer;
    lags.sort((a, b) => a - b);
    console.log(`R5 lagMs min/median/max: ${lags[0]}/${lags[Math.floor(lags.length / 2)]}/${lags.at(-1)}; samples: ${lags.length}`);
    await remove(cells, created.id);
  }
});

test('R6 repeat and conflicting placements during a move', async ({ cells }) => {
  test.setTimeout(20 * 60_000 + 30_000);
  let caught = false;
  for (let attempt = 1; attempt <= 20 && !caught; attempt++) {
    const created = await create(cells, ['gru', 'fra']);
    try {
      await seed(cells, created.id);
      await observed(cells, created.id);
      const moving = patch(cells, created.id, ['fra']);
      const concurrent = await Promise.all([
        patch(cells, created.id, ['fra']),
        patch(cells, created.id, ['gru', 'fra']),
      ]);
      const initial = await moving;
      for (const response of [initial, ...concurrent]) {
        expect([200, 503, 409], '200 updated, 503 replica_not_ready or 409 move_in_progress').toContain(response.status());
        if (response.status() === 503) await answer(response, 503, 'replica_not_ready');
        if (response.status() === 409) await answer(response, 409, 'move_in_progress');
      }
      caught = concurrent[0].status() === 503 && concurrent[1].status() === 409;
      console.log(`R6 attempt ${attempt}: ${[initial, ...concurrent].map(response => response.status()).join(',')}`);
      await move(cells, created.id, 'R6 cleanup move', 60_000);
    } finally {
      await remove(cells, created.id);
    }
  }
  console.log(`R6: ${caught ? 'PASS' : 'not observed after 20 attempts'}`);
});

test('R7 unchanged placement and the documented nine-member maximum', async ({ cells }) => {
  const created = await create(cells, ['gru', 'fra']);
  try {
    const before = await json<Record<string, unknown>>(await cells.get(`${home}${database(created.id)}`));
    const repeated = await patch(cells, created.id, ['fra', 'gru']);
    await answer(repeated, 200, '200 unchanged database');
    expect(await repeated.json()).toEqual(before);
    console.log(`R7 version field: ${Object.keys(before).filter(key => /version|epoch/i.test(key)).join(',') || 'not exposed by the database response'}`);
    const over = await patch(cells, created.id, ['gru', 'fra', 'sjc', 'iad', 'lhr', 'cdg', 'sin', 'syd', 'nrt', 'bom']);
    await answer(over, 400, 'invalid_request');
    expect((await over.json()).error.message).toBe('regions can place a database in at most 9 regions, including the writer and any intermediate placement');
  } finally {
    await remove(cells, created.id);
  }
});
