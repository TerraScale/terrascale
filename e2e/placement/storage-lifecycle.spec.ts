import { expect, type APIResponse } from '@playwright/test';
import { production } from '../support/stack';
import { test, gru, fra, project, name, json, run, answer, converged, removeDatabase, type Cells, type Database } from './support';

test.skip(production || process.env.E2E_REGIONS !== '2', 'storage lifecycle needs the local stack started with E2E_REGIONS=2');
test.describe.configure({ mode: 'default', timeout: 180_000 });

type Refusal = { error: { code: string; message: string; sqlState?: string | null } };

async function create(cells: Cells, prefix: string): Promise<Database> {
  return json<Database>(await cells.post(`${gru}${project()}/databases`, { data: { name: name(prefix), regions: ['gru', 'fra'] } }), 201);
}

async function refusal(response: APIResponse, status: number): Promise<Refusal['error']> {
  expect(response.status(), await response.text()).toBe(status);
  return (await response.json() as Refusal).error;
}

async function same(cells: Cells, id: string, statement: string): Promise<unknown[][]> {
  let rows: unknown[][] = [];
  await expect.poll(async () => {
    const [atGru, atFra] = await Promise.all([gru, fra].map(async origin => (await answer(cells, origin, id, [statement])).results[0].rows));
    rows = atGru;
    return JSON.stringify(atGru) === JSON.stringify(atFra) ? 'same' : JSON.stringify({ gru: atGru, fra: atFra });
  }, { message: `gru and fra converge on ${statement}`, timeout: 45_000, intervals: [250, 500, 1000] }).toBe('same');
  return rows;
}

test('strong DML that reads another table by primary key is serializable across regions', async ({ cells }) => {
  const created = await create(cells, 'strong-point-reads');
  try {
    const schema = await answer(cells, gru, created.id, [
      'CREATE TABLE left_flag (id INTEGER PRIMARY KEY, flag INTEGER NOT NULL)',
      'CREATE TABLE right_flag (id INTEGER PRIMARY KEY, flag INTEGER NOT NULL)',
    ], { consistency: 'strong' });
    await converged(cells, fra, created.id, 'SELECT count(*) FROM right_flag', rows => rows.length === 1);
    let committedBoth = 0;
    for (let round = 0; round < 8; round++) {
      const reset = await answer(cells, gru, created.id, [
        'INSERT INTO left_flag (id, flag) VALUES (1, 0) ON CONFLICT (id) DO UPDATE SET flag = 0',
        'INSERT INTO right_flag (id, flag) VALUES (1, 0) ON CONFLICT (id) DO UPDATE SET flag = 0',
      ], { consistency: 'strong', bookmark: schema.bookmark });
      const [fromGru, fromFra] = await Promise.all([
        run(cells, gru, created.id, ['UPDATE left_flag SET flag = 1 WHERE id = 1 AND (SELECT flag FROM right_flag WHERE id = 1) = 0'], { consistency: 'strong', bookmark: reset.bookmark }),
        run(cells, fra, created.id, ['UPDATE right_flag SET flag = 1 WHERE id = 1 AND (SELECT flag FROM left_flag WHERE id = 1) = 0'], { consistency: 'strong', bookmark: reset.bookmark }),
      ]);
      for (const response of [fromGru, fromFra]) {
        if (response.status() !== 200) {
          const error = await refusal(response, 409);
          expect(error.code, 'a strong write that lost the race is refused as a serialization conflict').toBe('serialization_conflict');
        }
      }

      const flags = (await answer(cells, gru, created.id, [
        'SELECT (SELECT flag FROM left_flag WHERE id = 1) + (SELECT flag FROM right_flag WHERE id = 1)',
      ], { consistency: 'strong' })).results[0].rows[0][0] as number;
      expect(flags, 'at most one of two write-skewed strong updates takes effect').toBeLessThanOrEqual(1);
      committedBoth += fromGru.status() === 200 && fromFra.status() === 200 ? 1 : 0;
    }

    console.log(`strong point-read write skew never committed both sides; ${committedBoth} rounds serialized one after the other`);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('cross-region foreign key conflicts never wedge replication', async ({ cells }) => {
  const created = await create(cells, 'fk-apply');
  try {
    await answer(cells, gru, created.id, [
      'CREATE TABLE parents (id INTEGER PRIMARY KEY, label TEXT NOT NULL)',
      'CREATE TABLE children (id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES parents (id), label TEXT NOT NULL)',
      'CREATE TABLE markers (id INTEGER PRIMARY KEY, region TEXT NOT NULL)',
    ], { consistency: 'strong' });
    for (let round = 1; round <= 5; round++) {
      const parent = await answer(cells, gru, created.id, [`INSERT INTO parents (id, label) VALUES (${round}, 'parent')`]);
      await answer(cells, fra, created.id, ['SELECT count(*) FROM parents'], { bookmark: parent.bookmark });
      const [deleted, adopted] = await Promise.all([
        run(cells, gru, created.id, [`DELETE FROM parents WHERE id = ${round}`]),
        run(cells, fra, created.id, [`INSERT INTO children (id, parent_id, label) VALUES (${round}, ${round}, 'child')`]),
      ]);
      expect([200, 400], await deleted.text()).toContain(deleted.status());
      expect([200, 400], await adopted.text()).toContain(adopted.status());
    }

    await answer(cells, gru, created.id, ["INSERT INTO markers (id, region) VALUES (1, 'gru')"]);
    await answer(cells, fra, created.id, ["INSERT INTO markers (id, region) VALUES (2, 'fra')"]);
    expect(await same(cells, created.id, 'SELECT id, region FROM markers ORDER BY id'), 'writes after the conflicts still replicate both ways')
      .toEqual([[1, 'gru'], [2, 'fra']]);
    const parents = await same(cells, created.id, 'SELECT id FROM parents ORDER BY id');
    const children = await same(cells, created.id, 'SELECT id, parent_id FROM children ORDER BY id');
    console.log(`foreign key races converged: parents ${JSON.stringify(parents)}, children ${JSON.stringify(children)}`);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('generated rowids stay cluster-unique for DEFAULT VALUES and trigger inserts, and CREATE TABLE AS SELECT is refused', async ({ cells }) => {
  const created = await create(cells, 'cluster-rowids');
  try {
    const schema = await answer(cells, gru, created.id, [
      "CREATE TABLE events (id INTEGER PRIMARY KEY, kind TEXT NOT NULL DEFAULT 'default')",
      'CREATE TABLE audit (id INTEGER PRIMARY KEY, event_id INTEGER NOT NULL, kind TEXT NOT NULL)',
      'CREATE TRIGGER events_audit AFTER INSERT ON events BEGIN INSERT INTO audit (event_id, kind) VALUES (NEW.id, NEW.kind); END',
    ], { consistency: 'strong' });
    await converged(cells, fra, created.id, 'SELECT count(*) FROM audit', rows => rows.length === 1);
    await Promise.all([gru, fra].flatMap(origin => [
      answer(cells, origin, created.id, ['INSERT INTO events DEFAULT VALUES'], { bookmark: schema.bookmark }),
      answer(cells, origin, created.id, ["INSERT INTO events (kind) VALUES ('named')"], { bookmark: schema.bookmark }),
    ]));
    const minimum = 1_000_000_000;
    for (const table of ['events', 'audit']) {
      const [[total, distinct, smallest]] = await same(cells, created.id, `SELECT count(*), count(DISTINCT id), min(id) FROM ${table}`) as number[][];
      expect({ total, distinct }, `${table} keeps one row per insert in both regions`).toEqual({ total: 4, distinct: 4 });
      expect(smallest, `${table} ids come from the cluster-wide generator`).toBeGreaterThan(minimum);
    }

    const ctas = await refusal(await run(cells, gru, created.id, ['CREATE TABLE copied AS SELECT * FROM events'], { consistency: 'strong' }), 400);
    expect(ctas.sqlState, 'CREATE TABLE AS SELECT is refused as unsupported').toBe('0A000');
    console.log('DEFAULT VALUES and trigger inserts produced 4 cluster-unique ids per table; CREATE TABLE AS SELECT was refused');
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('non-finite REAL values are refused instead of turning into text', async ({ cells }) => {
  const created = await create(cells, 'non-finite');
  try {
    await answer(cells, gru, created.id, ['CREATE TABLE measures (id INTEGER PRIMARY KEY, value REAL)'], { consistency: 'strong' });
    const read = await refusal(await run(cells, gru, created.id, ['SELECT 1e999']), 400);
    expect(read).toMatchObject({ code: 'numeric_value_out_of_range', sqlState: '22003' });
    const written = await refusal(await run(cells, gru, created.id, ['INSERT INTO measures (id, value) VALUES (1, 1e999)']), 400);
    expect(written).toMatchObject({ code: 'numeric_value_out_of_range', sqlState: '22003' });
    const nan = await answer(cells, gru, created.id, ['SELECT 1e999 - 1e999']);
    expect(nan.results[0].rows, 'SQLite turns NaN into NULL').toEqual([[null]]);
    await answer(cells, gru, created.id, ['INSERT INTO measures (id, value) VALUES (2, 2.5)']);
    expect(await same(cells, created.id, 'SELECT id, value FROM measures ORDER BY id'), 'only the finite value was stored and replicated').toEqual([[2, 2.5]]);
  } finally {
    await removeDatabase(cells, created.id);
  }
});
