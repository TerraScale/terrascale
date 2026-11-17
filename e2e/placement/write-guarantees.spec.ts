import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { expect, type APIRequestContext, type APIResponse } from '@playwright/test';
import { frame, rawSqlSocket } from '../support/sql-socket';
import { localPort, production } from '../support/stack';
import { journey, saveSecret } from '../support/state';
import { test, home, replica, project, database, name, json, sql, removeDatabase, type Cells } from './support';

test.skip(production || process.env.E2E_REGIONS !== '2' || process.env.E2E_NODES_PER_REGION !== '1',
  'one node in each of two regions');

const insert = "INSERT INTO notes (value) VALUES ('a')";
const fraClient = localPort('terrabase_fra', 8180);
const homeMetrics = localPort('metrics', 9091);
const fraMetrics = localPort('metrics_fra', 9191);
const fraPostgres = localPort('postgres_fra', 6532);

test('a write-preference override cannot force an unsafe local commit', async ({ cells, request }) => {
  const created = await json<{ id: string }>(await cells.post(`${home}${project()}/databases`, {
    data: { name: name('write-pref'), regions: ['gru', 'fra'] },
  }), 201);
  try {
    await sql(cells, home, created.id, 'CREATE TABLE notes (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await held(cells, created.id, 'SELECT count(*) FROM notes');
    const omitted = await execute(cells, created.id, insert);
    const forced = await execute(cells, created.id, insert, { 'TerraScale-Write': 'local' });
    expect(forced.status(), 'TerraScale-Write: local is accepted and ignored').toBe(omitted.status());
    expect(route(forced), 'the header case uses the same route as the omitted case').toBe(route(omitted));
    expect(route(omitted), 'the commit is not local').not.toContain('commit=local');
    const omittedBody = await omitted.json() as { results: { rows_affected: number }[] };
    const forcedBody = await forced.json() as { results: { rows_affected: number }[] };
    expect(forcedBody.results[0].rows_affected).toBe(omittedBody.results[0].rows_affected);
    const query = await cells.post(`${replica}${database(created.id)}/sql/execute?write=local`, {
      data: { statements: [{ sql: insert }] },
    });
    expect(query.status()).toBe(400);
    expect((await query.json()).error.message).toContain('this route takes no query parameters');

    const plain = await postgres(created.id, `${insert};\nSHOW terrascale.write_route;`);
    const local = await postgres(created.id, `SET terrascale.write = 'local';\n${insert};\nSHOW terrascale.write_route;`);
    expect(shown(local), 'SET terrascale.write = local uses the same route').toBe(shown(plain));
    expect(shown(plain)).not.toContain('commit=local');

    const token = await credential(request);
    const socketUrl = 'http://127.0.0.1:' + fraClient + database(created.id) + '/sql/ws';
    const before = await routes(request);
    const open = await answered(socketUrl, token);
    const middle = await routes(request);
    const headed = await answered(socketUrl, token, { 'TerraScale-Write': 'local' });
    const after = await routes(request);
    const omittedDelta = changed(before, middle);
    const forcedDelta = changed(middle, after);
    expect(headed).toEqual(open);
    expect(forcedDelta, 'the WebSocket header uses the same route counters').toEqual(omittedDelta);
    expect(omittedDelta.every(([labels]) => !labels.includes('commit="local"')), JSON.stringify(omittedDelta)).toBe(true);
    const refused = await cells.get(`${replica}${database(created.id)}/sql/ws?write=local`);
    expect(refused.status(), await refused.text()).toBe(400);
    console.log(`write preference on fra: ${route(omitted)}; postgres ${shown(plain)}; websocket ${JSON.stringify(omittedDelta)}`);
  } finally {
    await removeDatabase(cells, created.id);
  }
});

test('one node never acknowledges a write locally', async ({ cells, request }) => {
  const alone = await json<{ id: string }>(await cells.post(`${home}${project()}/databases`, {
    data: { name: name('one-disk'), regions: ['gru'] },
  }), 201);
  const copied = await json<{ id: string }>(await cells.post(`${home}${project()}/databases`, {
    data: { name: name('one-replica'), regions: ['gru', 'fra'] },
  }), 201);
  try {
    expect(await gauge(request, homeMetrics)).toBe(0);
    expect(await gauge(request, fraMetrics)).toBe(0);
    await sql(cells, home, alone.id, 'CREATE TABLE notes (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    const homeWrite = await cells.post(`${home}${database(alone.id)}/sql/execute`, {
      data: { statements: [{ sql: insert }] },
    });
    expect(homeWrite.status()).toBe(200);
    expect(homeWrite.headers()['terrascale-write-route'] ?? '').not.toContain('commit=local');
    expect(await gauge(request, homeMetrics)).toBe(0);
    await sql(cells, home, copied.id, 'CREATE TABLE notes (id INTEGER PRIMARY KEY, value TEXT NOT NULL)');
    await sql(cells, home, copied.id, "INSERT INTO notes (id, value) VALUES (1, 'a')");
    await held(cells, copied.id, 'SELECT value FROM notes');
    const updated = await cells.post(`${replica}${database(copied.id)}/sql/execute`, {
      data: { statements: [{ sql: "UPDATE notes SET value = 'b' WHERE id = 1" }] },
    });
    expect(updated.status()).toBe(200);
    expect(updated.headers()['terrascale-write-route']).toBe('route=local; reason=update_by_key; commit=home');
    expect(await gauge(request, homeMetrics)).toBe(0);
    expect(await gauge(request, fraMetrics)).toBe(0);
    console.log('one node: home write has no commit=local; fra update-by-key commit=home; terrabase_localwrites_available 0');
  } finally {
    await removeDatabase(cells, copied.id);
    await removeDatabase(cells, alone.id);
  }
});

test('row writes from a replica region are answered by the home, and a region without a replica answers 421', async ({ cells }) => {
  const notes = 'CREATE TABLE notes (id INTEGER PRIMARY KEY, value TEXT NOT NULL)';
  const replicated = await json<{ id: string }>(await cells.post(`${home}${project()}/databases`, {
    data: { name: name('rows-forward'), regions: ['gru', 'fra'] },
  }), 201);
  try {
    await sql(cells, home, replicated.id, notes);
    await held(cells, replicated.id, 'SELECT count(*) FROM notes');
    const forwarded = await cells.post(`${replica}${database(replicated.id)}/tables/notes/rows`, { data: { row: { value: 'via fra' } } });
    const answer = await json<{ key: string; row: { value: string } }>(forwarded, 201);
    expect(answer.row.value).toBe('via fra');
    expect(forwarded.headers()['x-terrabase-region'], 'the home answered the forwarded write').toBe('gru');
    expect((await sql(cells, home, replicated.id, 'SELECT value FROM notes')).results[0].rows).toEqual([['via fra']]);
    console.log(`POST fra …/tables/notes/rows -> ${forwarded.status()} answered by ${forwarded.headers()['x-terrabase-region']}`);
  } finally {
    await removeDatabase(cells, replicated.id);
  }

  const homeOnly = await json<{ id: string }>(await cells.post(`${home}${project()}/databases`, {
    data: { name: name('rows-home'), regions: ['gru'] },
  }), 201);
  try {
    await sql(cells, home, homeOnly.id, notes);
    let misdirected: APIResponse | undefined;
    await expect.poll(async () => {
      misdirected = await cells.post(`${replica}${database(homeOnly.id)}/tables/notes/rows`, { data: { row: { value: 'via fra' } } });
      return misdirected.status();
    }, { message: 'fra knows the database and refuses the row write' }).toBe(421);
    expect((await misdirected!.json()).error.code).toBe('wrong_region');
    expect(misdirected!.headers()['x-terrabase-database-home']).toBe('gru');
    expect((await sql(cells, home, homeOnly.id, 'SELECT count(*) FROM notes')).results[0].rows).toEqual([[0]]);
    console.log(`POST fra …/tables/notes/rows (no replica in fra) -> ${misdirected!.status()} wrong_region`);
  } finally {
    await removeDatabase(cells, homeOnly.id);
  }
});

async function held(cells: Cells, id: string, statement = 'SELECT 1') {
  await expect.poll(async () => {
    const response = await cells.post(`${replica}${database(id)}/sql/execute`, { data: { statements: [{ sql: statement }] } });
    return response.status() === 200 && response.headers()['x-terrabase-region'] === 'fra';
  }, { message: 'fra serves the replica' }).toBe(true);
}

async function execute(cells: Cells, id: string, statement: string, headers?: Record<string, string>) {
  const response = await cells.post(`${replica}${database(id)}/sql/execute`, {
    data: { statements: [{ sql: statement }] }, headers,
  });
  expect(response.status()).toBe(200);
  return response;
}

function route(response: APIResponse) {
  return response.headers()['terrascale-write-route'];
}

async function postgres(id: string, statements: string) {
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

function shown(output: string) {
  const line = output.split('\n').map(item => item.trim()).find(item => item.includes('commit='));
  expect(line, output).toBeDefined();
  return line;
}

async function credential(request: APIRequestContext) {
  const current = journey();
  const response = await request.post('https://auth.terrascale.tech/terrascale/oauth/token', {
    form: {
      grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26),
      client_secret: current.apiKey, project: current.projectId, scope: 'sql:read sql:write',
    },
  });
  expect(response.status()).toBe(200);
  const token = (await response.json()).access_token as string;
  saveSecret('write-guarantee-token', token);
  return token;
}

async function answered(url: string, token: string, headers?: Record<string, string>) {
  const ws = await rawSqlSocket(url, token, headers);
  try {
    const result = once(ws.events, 'message');
    ws.socket.write(frame(1, Buffer.from(JSON.stringify({ id: 1, type: 'execute', statements: [{ sql: insert }] }))));
    const body = (await result)[0] as { type: string; results: { rows_affected: number }[] };
    expect(body.type).toBe('result');
    ws.socket.write(frame(8, Buffer.from([3, 232])));
    await ws.closed;
    return { type: body.type, rows: body.results[0].rows_affected };
  } finally {
    ws.socket.destroy();
    await ws.closed;
  }
}

async function routes(request: APIRequestContext) {
  const response = await request.get('http://127.0.0.1:' + fraMetrics + '/metrics');
  expect(response.status()).toBe(200);
  const counts = new Map<string, number>();
  for (const line of (await response.text()).split('\n')) {
    if (!line.startsWith('terrabase_sql_write_routes_total{') || !line.includes('region="fra"')) continue;
    counts.set(line.slice(line.indexOf('{'), line.indexOf('}') + 1), Number(line.split(' ').at(-1)));
  }
  return counts;
}

function changed(before: Map<string, number>, after: Map<string, number>) {
  return [...after].flatMap(([labels, value]) => {
    const diff = value - (before.get(labels) ?? 0);
    return diff > 0 ? [[labels, diff] as [string, number]] : [];
  });
}

async function gauge(request: APIRequestContext, port: number) {
  const response = await request.get(`http://127.0.0.1:${port}/metrics`);
  expect(response.status()).toBe(200);
  const lines = (await response.text()).split('\n').filter(line => line.startsWith('terrabase_localwrites_available'));
  expect(lines.length, 'the local-write gauge is exported').toBeGreaterThan(0);
  expect(lines.every(line => line.endsWith(' 0')), lines.join('\n')).toBe(true);
  return 0;
}
