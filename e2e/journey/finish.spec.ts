import { spawn } from 'node:child_process';
import { once } from 'node:events';
import type { APIRequestContext } from '@playwright/test';
import { deleteAccount } from '../support/account';
import { test, expect } from '../support/remote-database';
import { completeUpload, deleteFile, leftFiles, putReserved, putUnnamed, reserveUpload, stored, unnamedPath, type Reservation } from '../support/files';
import { runOperations, runSessionFault, sdks } from '../support/sdk';
import { prepareAuthManagement, closeAuthManagement } from '../support/auth-management';
import { deleteProjectOwner } from '../support/project-owner';
import { checkPasswordFairness } from '../support/password-fairness';
import { shell } from '../support/shell';
import { auth, onLocalStack } from '../support/stack';
import { keyId } from '../support/api-key';
import { journey, saveSecret, setupFinished } from '../support/state';

async function placementHeaders(request: APIRequestContext): Promise<{ Authorization: string }> {
  const current = journey();
  const basic = Buffer.from(`${keyId(current.apiKey)}:${current.apiKey}`).toString('base64');
  const answer = await request.post('https://auth.terrascale.tech/terrascale/oauth/token', {
    headers: { Authorization: `Basic ${basic}` },
    form: { grant_type: 'client_credentials', project: current.projectId },
  });
  expect(answer.status()).toBe(200);
  const token = (await answer.json()).access_token as string;
  saveSecret('placement-project-token', token);
  return { Authorization: `Bearer ${token}` };
}

const sweepPass = 15_000;

test.describe.configure({ mode: 'default' });

test.beforeAll(({}, info) => {
  expect(setupFinished(info), 'setup did not finish').toBe(true);
});

test('delete the dedicated auth feature owner after all auth flows', async ({ browser }, info) => {
  await deleteProjectOwner(browser, info);
});

test('a tenant at its hashing cap leaves other tenants normal sign-in capacity', async ({ browser }) => {
  await checkPasswordFairness(browser);
});

test('SQL sessions retain uncertainty after lost committing replies', async () => {
  const failures: string[] = [];
  for (const sdk of sdks) {
    for (const asynchronous of sdk === 'python' ? [false, true] : [false]) {
      const modes = ['commit', 'before', 'autocommit', 'batch', 'read'];
      if (sdk === 'go') modes.push('overtake-commit', 'overtake-rollback', 'overtake-begin', 'overtake-batch', 'sql-error');
      if (sdk === 'go' || sdk === 'rust') modes.push('retry');
      if (sdk !== 'js') modes.push('cancel');
      if (sdk === 'python' && !asynchronous) modes.push('dbapi');
      for (const mode of modes) {
        await test.step(`${sdk} ${mode}${asynchronous ? ' async' : ''}`, async () => {
          try { await runSessionFault(sdk, mode, asynchronous); }
          catch (error) { failures.push(`${sdk} ${mode}${asynchronous ? ' async' : ''}: ${String(error)}`); }
        });
      }
    }
  }
  expect(failures).toEqual([]);
});


const source = `WITH input(data, key) AS (VALUES ('{"name":"Ada","b":{"c":"nested"},"items":["zero","one"]}', 'name')) SELECT `;
const cases: { name: string; sql: string; args?: string[]; pgSql?: string; expected: string }[] = [
  { name: 'single quotes', sql: source + `data->>'name' FROM input`, expected: 'Ada' },
  { name: 'JSON arrow', sql: source + `data->'name' FROM input`, expected: '"Ada"' },
  { name: 'identifier', sql: source + 'data->>key FROM input', expected: 'Ada' },
  { name: 'quoted identifiers', sql: source + '"data"->>"key" FROM input', expected: 'Ada' },
  { name: 'numbered parameter', sql: source + 'data->>?1 FROM input', pgSql: source + 'data->>$1 FROM input', args: ['name'], expected: 'Ada' },
  { name: 'question parameter', sql: source + 'data->>? FROM input', pgSql: source + 'data->>$1 FROM input', args: ['name'], expected: 'Ada' },
  { name: 'nested path', sql: source + `data->'b'->>'c' FROM input`, expected: 'nested' },
  { name: 'array index', sql: source + `data->'items'->>1 FROM input`, expected: 'one' },
  { name: 'JSON literal', sql: `SELECT '{"name":"Ada"}'->>'name'`, expected: 'Ada' },
  { name: 'spaced operator', sql: source + `data ->> 'name' FROM input`, expected: 'Ada' },
];

test('JSON text arrows preserve adjacent SQL tokens over REST, WebSocket and PostgreSQL', async ({ request }) => {
  const current = journey();
  const exchanged = await request.post(`${auth}/terrascale/oauth/token`, {
    form: {
      grant_type: 'client_credentials', client_id: keyId(current.apiKey),
      client_secret: current.apiKey, project: current.projectId, scope: 'sql:read sql:write',
    },
  });
  expect(exchanged.status()).toBe(200);
  const token = (await exchanged.json()).access_token as string;
  saveSecret('json-arrow-token', token);
  const path = `/v1/projects/${current.projectId}/databases/${current.databaseId}/sql`;

  async function rest(sql: string, args?: string[]): Promise<{ rows: unknown[][] }> {
    const response = await request.post(`https://api.terrascale.tech${path}/execute`, {
      headers: { authorization: 'Bearer ' + token },
      data: { statements: [{ sql, args }] },
    });
    const body = await response.json();
    expect(response.status(), `REST ${sql}: ${JSON.stringify(body)}`).toBe(200);
    return body.results[0];
  }

  for (const entry of cases) {
    expect((await rest(entry.sql, entry.args)).rows, `REST ${entry.name}`).toEqual([[entry.expected]]);
  }

  function definitions(table: string): string[] {
    return [
      `CREATE TABLE ${table} (id INTEGER PRIMARY KEY, data TEXT NOT NULL)`,
      `INSERT INTO ${table} VALUES (1, '{"name":"Ada"}'), (2, '{"name":"Grace"}')`,
      `CREATE INDEX ${table}_name ON ${table} (data->>'name')`,
      `CREATE VIEW ${table}_view AS SELECT id, data->>'name' AS name, data->'name' AS json_name FROM ${table}`,
    ];
  }

  async function cleanup(table: string): Promise<void> {
    await rest(`DROP VIEW IF EXISTS ${table}_view`);
    await rest(`DROP TABLE IF EXISTS ${table}`);
  }

  const table = 'journey_json_arrow_rest';
  try {
    for (const sql of definitions(table)) await rest(sql);
    expect((await rest(`SELECT id FROM ${table} WHERE data->>'name'=?`, ['Ada'])).rows).toEqual([[1]]);
    expect((await rest(`SELECT id FROM ${table} WHERE data->>'name'=?`, ['Missing'])).rows).toEqual([]);
    expect((await rest(`SELECT id, name, json_name FROM ${table}_view ORDER BY id`)).rows).toEqual([
      [1, 'Ada', '"Ada"'], [2, 'Grace', '"Grace"'],
    ]);
  } finally {
    await cleanup(table);
  }

  const socket = new WebSocket(`wss://api.terrascale.tech${path}/ws`, 'terrascale.sql.v1');
  try {
    await once(socket, 'open');
    const authenticated = once(socket, 'message');
    socket.send(JSON.stringify({ id: 1, type: 'auth', token }));
    expect(JSON.parse(((await authenticated)[0] as MessageEvent<string>).data)).toMatchObject({ id: 1, type: 'result' });
    for (const [index, entry] of cases.entries()) {
      const received = once(socket, 'message');
      socket.send(JSON.stringify({ id: index + 2, type: 'execute', statements: [{ sql: entry.sql, args: entry.args }] }));
      const body = JSON.parse(((await received)[0] as MessageEvent<string>).data);
      expect(body, `WebSocket ${entry.name}`).toMatchObject({
        id: index + 2, type: 'result', results: [{ rows: [[entry.expected]] }],
      });
    }
  } finally {
    if (socket.readyState !== WebSocket.CLOSED) {
      const closed = once(socket, 'close');
      socket.close();
      await closed;
    }
  }

  const url = `postgresql://any@pg.terrascale.tech:5432/${current.projectId}/${current.databaseId}?sslmode=verify-full&sslrootcert=system`;
  const pg = spawn('psql', ['-X', '-v', 'ON_ERROR_STOP=1', '-qAt', url], {
    env: { PATH: process.env.PATH, HOME: process.env.HOME, SSL_CERT_FILE: process.env.SSL_CERT_FILE, PGPASSWORD: current.apiKey },
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  let stdout = '';
  let stderr = '';
  pg.stdout.on('data', (chunk: Buffer) => { stdout += chunk.toString(); });
  pg.stderr.on('data', (chunk: Buffer) => { stderr += chunk.toString(); });
  const completed = once(pg, 'close');
  const pgTable = 'journey_json_arrow_pg';
  try {
    pg.stdin.end(cases.map(entry => entry.args
      ? `${entry.pgSql ?? entry.sql}\n\\bind ${entry.args.join(' ')}\n\\g\n`
      : `${entry.sql};\n`).join('') + definitions(pgTable).map(sql => `${sql};\n`).join('') +
      `SELECT id FROM ${pgTable} WHERE data->>'name'=$1\n\\bind Ada\n\\g\n` +
      `SELECT count(*) FROM ${pgTable} WHERE data->>'name'=$1\n\\bind Missing\n\\g\n` +
      `SELECT id, name, json_name FROM ${pgTable}_view ORDER BY id;\n`);
    expect((await completed)[0], `PostgreSQL: ${stderr}`).toBe(0);
    expect(stdout.trim().split('\n')).toEqual([
      ...cases.map(entry => entry.expected), '1', '0', '1|Ada|"Ada"', '2|Grace|"Grace"',
    ]);
  } finally {
    await cleanup(pgTable);
  }
});


test.describe('SDK group A', () => {
  for (const sdk of sdks) {
    test(`${sdk} SDK: SQL, values, sessions, conflicts and ids on the journey database`, async ({ page }) => {
      const management = await prepareAuthManagement(page, sdk);
      try { await runOperations(sdk, 'a', management, page); }
      finally { await closeAuthManagement(page, management); }
    });
  }
});

test.describe('database deletion', () => {
  test('placement refuses removing the last region and keeps serving', async ({ request }) => {
    const current = journey();
    const headers = await placementHeaders(request);
    const url = `https://api.terrascale.tech/v1/projects/${current.projectId}/databases/${current.databaseId}`;
    const before = await request.get(url, { headers });
    expect(before.status()).toBe(200);
    const original = await before.json();
    for (const data of [
      { regions: [] },
      { regions: null },
      { regions: ['c18-unserved-region'] },
      { regions: [current.databaseRegion.toLowerCase(), current.databaseRegion.toLowerCase()] },
      { regions: [current.databaseRegion.toLowerCase()], homeRegion: current.databaseRegion.toLowerCase() },
      { regions: [current.databaseRegion.toLowerCase()], replicaRegions: [] },
      { regions: [current.databaseRegion.toLowerCase()], allRegions: false },
    ]) {
      const refused = await request.patch(url, { headers, data });
      expect(refused.status()).toBe(400);
      expect((await refused.json()).error.code).toBe('invalid_request');
    }
    const after = await request.get(url, { headers });
    expect(after.status()).toBe(200);
    expect((await after.json()).regions).toEqual(original.regions);
    const query = await request.post(`${url}/sql/execute`, { headers, data: { statements: [{ sql: 'SELECT 1' }] } });
    expect(query.status()).toBe(200);
  });

  test('delete the journey database on its danger page by typing its name', async ({ page, audit }) => {
    const current = journey();
    await page.goto(`/databases/${current.databaseId}/danger`);
    await shell(page, current);
    await audit.check('database danger zone');

    await page.locator('main').getByRole('button', { name: 'Delete database', exact: true }).click();
    const dialog = page.locator('dialog[open]');
    await expect(dialog).toBeVisible();
    const confirm = dialog.locator('input[name="confirmName"]');
    const submit = dialog.getByRole('button', { name: 'Delete database', exact: true });
    await confirm.fill(current.databaseName);
    await audit.check('delete database dialog');
    await submit.click();

    await page.waitForURL((url) => url.pathname === '/databases');
    await expect(page.locator('main tr').filter({ hasText: current.databaseName })).toHaveCount(0);
    await audit.check('databases after deletion');
  });
  test('the dashboard creates, renames and deletes a database outside its own region', async ({ remoteDatabase }) => {
    await remoteDatabase();
  });

  test('placement regions create, add and remove a non-writer', async ({ request }) => {
    const current = journey();
    const headers = await placementHeaders(request);
    const databases = `https://api.terrascale.tech/v1/projects/${current.projectId}/databases`;
    const createdIds: string[] = [];
    try {
      const servingResponse = await request.get('https://api.terrascale.tech/v1/regions');
      expect(servingResponse.status()).toBe(200);
      const serving = (await servingResponse.json()).regions as string[];
      const writer = current.databaseRegion.toLowerCase();
      const other = serving.find(region => region !== writer);
      const name = `placement-${current.runId}`;
      const created = await request.post(databases, { headers, data: { name, regions: [writer] } });
      expect(created.status()).toBe(201);
      const database = await created.json();
      createdIds.push(database.id);
      expect(database.regions).toEqual([writer]);
      expect(database.homeRegion).toBe(writer);
      expect(database.replicaRegions).toEqual([]);
      const url = `${databases}/${database.id}`;
      const selected = other ? [writer, other] : [writer];
      const added = await request.patch(url, { headers, data: { regions: selected } });
      expect(added.status()).toBe(200);
      expect((await added.json()).regions).toEqual([...selected].sort());
      const observations = await request.get(`${url}/regions`, { headers });
      expect(observations.status()).toBe(200);
      const status = await observations.json();
      expect(status.regions.map((region: { region: string }) => region.region)).toEqual([...selected].sort());
      expect(status.regions.filter((region: { role: string | null }) => region.role === 'writer').map((region: { region: string }) => region.region)).toEqual([writer]);
      const removed = await request.patch(url, { headers, data: { regions: [writer] } });
      expect(removed.status()).toBe(200);
      expect((await removed.json()).regions).toEqual([writer]);
      const repeated = await request.patch(url, { headers, data: { regions: [writer] } });
      expect(repeated.status()).toBe(200);
      expect((await repeated.json()).regions).toEqual([writer]);
      expect((await request.delete(url, { headers })).status()).toBe(204);
      if (other) {
        const ordered = [writer, other].sort().reverse();
        const two = await request.post(databases, { headers, data: { name, regions: ordered } });
        expect(two.status()).toBe(201);
        const body = await two.json();
        createdIds.push(body.id);
        expect(body.homeRegion).toBe(ordered[0]);
        expect(body.regions).toEqual([writer, other].sort());
        expect((await request.delete(`${databases}/${body.id}`, { headers })).status()).toBe(204);
      }
      const legacy = await request.post(databases, { headers, data: { name, homeRegion: writer, replicaRegions: [] } });
      expect(legacy.status()).toBe(201);
      const body = await legacy.json();
      createdIds.push(body.id);
      expect(body.homeRegion).toBe(writer);
      expect(body.replicaRegions).toEqual([]);
      expect(body.regions).toEqual([writer]);
      expect((await request.delete(`${databases}/${body.id}`, { headers })).status()).toBe(204);
    } finally {
      for (const id of createdIds) await request.delete(`${databases}/${id}`, { headers });
    }
  });
});

test.describe('SDK group B', () => {
  for (const sdk of sdks) {
    test(`${sdk} SDK: agent databases with TTL and scoped keys`, async () => {
      await runOperations(sdk, 'b');
    });
  }
});

test.describe('teardown', { tag: '@production-cleanup' }, () => {
  test('a new display name shows in the shell', async ({ page, audit }) => {
    const current = journey();
    const name = `Renamed ${current.runId}`;
    await page.goto('/profile');
    await page.locator('#displayName').fill(name);
    const before = page.url();
    await page.locator('form[action^="/api/v1/dashboard/profile"]').getByRole('button', { name: 'Save profile', exact: true }).click();
    await expect(page.locator('#displayName')).toHaveValue(name);
    await expect(page).toHaveURL(before);
    await expect(page.locator('h1.ts-profile-name')).toHaveText(name);
    await shell(page, { ...current, displayName: name });
    await audit.check('profile renamed');
  });

  test('the account is deleted through the profile danger tab, a new sign-in is refused and its files leave the object store', async ({ page, audit, browser }, info) => {
    const content = Buffer.from(`held ${journey().runId}`);
    const files: { live: string; held: Reservation; late: Reservation }[] = [];
    await onLocalStack(async () => {
      const left = leftFiles();
      await page.goto(`/storage?bucket=${left.bucket}`);
      const held = await reserveUpload(page, left.bucket, `held-${journey().runId}.txt`, 'text/plain', content);
      expect(await putReserved(held, content), 'the file to delete is uploaded').toBe(200);
      expect(await completeUpload(page, left.bucket, held.id), 'the file to delete is completed').toBe(204);
      expect(await deleteFile(page, left.bucket, held.id), 'the file is deleted just before the account').toBe(303);
      const late = await reserveUpload(page, left.bucket, `late-${journey().runId}.txt`, 'text/plain', content);
      expect([await stored(left.live), await stored(held.path), await stored(late.path)], 'the bucket holds a live file and a just-deleted file, and nothing for the reserved upload').toEqual([true, true, false]);
      files.push({ live: left.live, held, late });
    });
    const closing = Date.now();
    await deleteAccount(page, audit, browser, info);
    for (const { live, held, late } of files) {
      expect([await stored(live), await stored(held.path)], 'the deleted account left no file in the object store').toEqual([false, false]);
      expect(await putReserved(late, content), 'an upload link issued before the deletion still takes its file').toBe(200);
      await expect.poll(() => stored(late.path), { message: 'a file uploaded after the deletion is erased again' }).toBe(false);
      const unnamed = unnamedPath(late.path);
      expect(await putUnnamed(unnamed, content), 'an object that no record names arrives under the deleted project').toBe(200);
      const nextSweep = Date.now() - closing + sweepPass;
      info.setTimeout(info.timeout + nextSweep);
      await expect.poll(() => stored(unnamed), { message: 'the sweep of the project prefix erases an object that no record names', timeout: nextSweep }).toBe(false);
    }
  });
});
