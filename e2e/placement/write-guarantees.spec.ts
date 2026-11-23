import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { expect } from '@playwright/test';
import { localPort, production } from '../support/stack';
import { journey } from '../support/state';
import { test, gru, fra, project, database, name, json, sql, answer, converged, removeDatabase, type Database } from './support';

test.skip(production || process.env.E2E_REGIONS !== '2' || process.env.E2E_NODES_PER_REGION !== '1',
  'one node in each of two regions');

const notes = 'CREATE TABLE notes (id INTEGER PRIMARY KEY, value TEXT NOT NULL)';
const fraPostgres = localPort('postgres_fra', 6532);

test('fra commits REST, row and PostgreSQL writes itself and gru converges on them', async ({ cells }) => {
  const created = await json<Database>(await cells.post(`${gru}${project()}/databases`, {
    data: { name: name('active-writes'), regions: ['gru', 'fra'] },
  }), 201);
  try {
    await sql(cells, gru, created.id, notes);
    await converged(cells, fra, created.id, 'SELECT count(*) FROM notes', rows => rows.length === 1);

    const written = await answer(cells, fra, created.id, ["INSERT INTO notes (id, value) VALUES (1, 'rest')"]);
    expect(written.consistency).toBe('fast');
    expect(written.results[0].rowsAffected).toBe(1);
    expect(written.bookmark).not.toBe('');
    const read = await answer(cells, gru, created.id, ['SELECT value FROM notes WHERE id = 1'], { bookmark: written.bookmark });
    expect(read.results[0].rows, 'gru reads the fra write with its bookmark').toEqual([['rest']]);

    const row = await cells.post(`${fra}${database(created.id)}/tables/notes/rows`, { data: { row: { id: 2, value: 'row' } } });
    const inserted = await json<{ key: string; row: { id: number; value: string } }>(row, 201);
    expect(inserted.row.value).toBe('row');
    expect(row.headers()['x-terrabase-region'], 'fra answered the row write itself').toBe('fra');

    await psql(created.id, "INSERT INTO notes (id, value) VALUES (3, 'postgres');");

    const expected = JSON.stringify([[1, 'rest'], [2, 'row'], [3, 'postgres']]);
    for (const origin of [gru, fra]) {
      await converged(cells, origin, created.id, 'SELECT id, value FROM notes ORDER BY id', rows => JSON.stringify(rows) === expected);
    }
    console.log('fra commits REST, row and PostgreSQL writes; gru converges on ' + expected);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('consistency follows the request header, then the database default', async ({ cells }) => {
  const fast = await json<Database>(await cells.post(`${gru}${project()}/databases`, {
    data: { name: name('fast-default'), regions: ['gru', 'fra'] },
  }), 201);
  const strong = await json<Database>(await cells.post(`${gru}${project()}/databases`, {
    data: { name: name('strong-default'), regions: ['gru', 'fra'], defaultConsistency: 'strong' },
  }), 201);
  try {
    expect(fast.defaultConsistency).toBe('fast');
    expect(strong.defaultConsistency).toBe('strong');
    for (const created of [fast, strong]) {
      await sql(cells, gru, created.id, notes);
      await converged(cells, fra, created.id, 'SELECT count(*) FROM notes', rows => rows.length === 1);
    }
    expect((await answer(cells, fra, fast.id, ["INSERT INTO notes (id, value) VALUES (1, 'default')"])).consistency).toBe('fast');
    expect((await answer(cells, fra, fast.id, ["INSERT INTO notes (id, value) VALUES (2, 'strong')"], { consistency: 'strong' })).consistency).toBe('strong');
    const strongCommit = await answer(cells, fra, strong.id, ["INSERT INTO notes (id, value) VALUES (1, 'default')"]);
    expect(strongCommit.consistency).toBe('strong');
    expect((await answer(cells, fra, strong.id, ["INSERT INTO notes (id, value) VALUES (2, 'fast')"], { consistency: 'fast' })).consistency).toBe('fast');
    const committed = await answer(cells, gru, strong.id, ['SELECT id, value FROM notes WHERE id = 1'], { bookmark: strongCommit.bookmark });
    expect(committed.results[0].rows, 'gru reads the strong commit made in fra with its bookmark').toEqual([[1, 'default']]);
  } finally {
    await removeDatabase(cells, strong.id);
    await removeDatabase(cells, fast.id);
  }
});

async function psql(id: string, statements: string) {
  const current = journey();
  const child = spawn('psql', ['-X', '-v', 'ON_ERROR_STOP=1', '-A', '-t',
    `postgresql://any@127.0.0.1:${fraPostgres}/${current.projectId}/${id}?sslmode=disable`], {
    env: { PATH: process.env.PATH, PGPASSWORD: current.apiKey },
  });
  let out = '';
  let err = '';
  child.stdout.setEncoding('utf8');
  child.stderr.setEncoding('utf8');
  child.stdout.on('data', chunk => { out += chunk; });
  child.stderr.on('data', chunk => { err += chunk; });
  const closed = once(child, 'close');
  child.stdin.end(statements + '\n');
  const [code] = await closed;
  expect(code, err + out).toBe(0);
  return out;
}
