import { createHash, randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { expect } from '@playwright/test';
import { stored } from '../support/files';
import { journey, saveSecret } from '../support/state';
import { test, gru, fra, project, database, name, regionOf, status, json, sql, answer, converged, regions, replicated, removeDatabase, type Database } from './support';
import { production } from '../support/stack';
import { issueKey, postgres } from './internal';

test.skip(production || process.env.E2E_REGIONS !== '2', 'placement requires the local stack started with E2E_REGIONS=2');
test.describe.configure({ mode: 'default', timeout: 180_000 });

type Signed = { id: string; url: string; headers: Record<string, string> };
type Discovery = { region: string; machine: string; regions: string[] };

test('discovery lists both active-active regions through both listeners', async ({ request }) => {
  for (const origin of [gru, fra]) {
    const discovery = await json<Discovery>(await request.get(`${origin}/v1/regions`));
    expect(discovery.region).toBe(regionOf(origin));
    expect(discovery.regions).toEqual(['fra', 'gru']);
  }
});

test('create in two regions, write in both, converge, then remove a region', async ({ cells }) => {
  const created = await json<Database>(await cells.post(`${gru}${project()}/databases`, {
    data: { name: name('placed'), regions: ['gru', 'fra'] },
  }), 201);
  try {
    expect(created.regions).toEqual(['fra', 'gru']);
    await sql(cells, gru, created.id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)');
    await sql(cells, gru, created.id, 'INSERT INTO placement_rows VALUES (1)');
    await replicated(cells, fra, created.id, 1);
    for (const origin of [gru, fra]) {
      const replication = await regions(cells, origin, created.id);
      expect(replication.regions.map(row => [row.region, row.healthy]), `${regionOf(origin)} reports every region healthy`).toEqual([['fra', true], ['gru', true]]);
      for (const row of replication.regions) {
        expect(row.appliedBookmark, `${row.region} reports an applied bookmark`).not.toBe('');
        expect(row).not.toHaveProperty('role');
      }
    }

    await sql(cells, fra, created.id, 'INSERT INTO placement_rows VALUES (2)');
    await replicated(cells, gru, created.id, 2);
    for (const origin of [gru, fra]) {
      await converged(cells, origin, created.id, 'SELECT value FROM placement_rows ORDER BY value', rows => JSON.stringify(rows) === '[[1],[2]]');
    }

    const single = await json<Database>(await cells.patch(`${fra}${database(created.id)}`, { data: { regions: ['fra'] } }));
    expect(single.regions).toEqual(['fra']);
    expect(single.placementVersion).toBeGreaterThan(created.placementVersion);
    const remaining = await regions(cells, fra, created.id);
    expect(remaining.regions.map(row => [row.region, row.healthy])).toEqual([['fra', true]]);
    const written = await answer(cells, fra, created.id, ['INSERT INTO placement_rows VALUES (3)']);
    expect((await answer(cells, fra, created.id, ['SELECT value FROM placement_rows ORDER BY value'], { bookmark: written.bookmark })).results[0].rows).toEqual([[1], [2], [3]]);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('a new database in two regions creates a table at once over REST, WebSocket and PostgreSQL', async ({ cells, page }) => {
  const token = await issueKey(journey().organizationId, journey().projectId);
  for (const protocol of ['REST', 'WebSocket', 'PostgreSQL']) {
    const created = await json<Database>(await cells.post(`${gru}${project()}/databases`, {
      data: { name: name('ddl'), regions: ['gru', 'fra'] },
    }), 201);
    try {
      const statements = ['CREATE TABLE first_rows (value INTEGER PRIMARY KEY)', 'INSERT INTO first_rows VALUES (7)'];
      if (protocol === 'REST') {
        for (const statement of statements) await sql(cells, gru, created.id, statement);
      } else if (protocol === 'WebSocket') {
        expect(await page.evaluate(({ url, token, statements }) => new Promise<string[]>((resolve, reject) => {
          const socket = new WebSocket(url, 'terrascale.sql.v1');
          const answers: string[] = [];
          socket.onopen = () => socket.send(JSON.stringify({ id: 1, type: 'auth', token }));
          socket.onerror = () => reject(new Error('the WebSocket upgrade failed'));
          socket.onmessage = message => {
            const answer = JSON.parse(String(message.data));
            answers.push(answer.error?.code ?? answer.type);
            if (answer.id === 1 && answer.type === 'result') {
              statements.forEach((statement, index) => socket.send(JSON.stringify({ id: index + 2, type: 'execute', statements: [{ sql: statement }] })));
            }
            if (answers.length === statements.length + 1) { socket.close(); resolve(answers); }
          };
        }), { url: gru.replace('https:', 'wss:') + database(created.id) + '/sql/ws', token, statements }), 'auth, CREATE TABLE and INSERT answer results').toEqual(['result', 'result', 'result']);
      } else {
        const process = postgres(created.id, token);
        let errors = '';
        process.stderr.on('data', (chunk: Buffer) => { errors += chunk; });
        process.stdout.resume();
        const ended = once(process, 'close');
        process.stdin.end(statements.map(statement => statement + ';\n').join(''));
        const [code] = await ended;
        expect(errors, 'psql reports no error').toBe('');
        expect(code).toBe(0);
      }
      expect((await sql(cells, gru, created.id, 'SELECT value FROM first_rows')).results[0].rows, protocol + ' created the table and stored its row').toEqual([[7]]);
      await converged(cells, fra, created.id, 'SELECT value FROM first_rows', rows => JSON.stringify(rows) === '[[7]]');
    } finally {
      await removeDatabase(cells, created.id);
    }
  }
});

test('add a region to a database created in one region', async ({ cells }) => {
  const created = await json<Database>(await cells.post(`${gru}${project()}/databases`, {
    data: { name: name('added'), regions: ['gru'] },
  }), 201);
  try {
    expect(created.regions).toEqual(['gru']);
    await sql(cells, gru, created.id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)');
    await sql(cells, gru, created.id, 'INSERT INTO placement_rows VALUES (4)');
    const updated = await json<Database>(await cells.patch(`${gru}${database(created.id)}`, { data: { regions: ['gru', 'fra'] } }));
    expect(updated.regions).toEqual(['fra', 'gru']);
    expect(updated.placementVersion).toBeGreaterThan(created.placementVersion);
    await replicated(cells, fra, created.id, 4);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('a bucket created through the fra host uploads and returns the same bytes', async ({ cells, request }) => {
  const policy = { owner: false, public: false, roles: [] };
  const bucket = await json<{ id: string; region: string }>(await cells.post(`${fra}${project()}/buckets`, {
    data: { name: name('fra-files'), rules: { owner: null, read: policy, write: policy } },
  }), 201);
  const path = `${fra}${project()}/buckets/${bucket.id}`;
  let file: string | undefined;
  try {
    expect(['gru', 'fra']).toContain(bucket.region);
    const bytes = randomBytes(4096);
    const signed = await json<Signed>(await cells.post(`${path}/files/upload`, {
      data: { name: 'placement.bin', contentType: 'application/octet-stream', size: bytes.length, sha256: createHash('sha256').update(bytes).digest('base64'), metadata: {} },
    }), 201);
    file = signed.id;
    saveSecret('placement-upload', signed.url);
    await status(await request.put(signed.url, { headers: signed.headers, data: bytes }), 200);
    await json(await cells.post(`${path}/files/${file}/complete`));
    const download = await json<Signed>(await cells.post(`${path}/files/${file}/download`));
    saveSecret('placement-download', download.url);
    const received = await request.get(download.url);
    await status(received, 200);
    expect(await received.body()).toEqual(bytes);
    expect(await stored(new URL(download.url).pathname)).toBe(true);
  } finally {
    if (file) await status(await cells.delete(`${path}/files/${file}`), 204);
    await status(await cells.delete(path), 204);
  }
});
