import { createHash, randomBytes } from 'node:crypto';
import { test as base, expect, type APIRequestContext, type APIResponse } from '@playwright/test';
import { stored } from '../support/files';
import { auth, production } from '../support/stack';
import { journey, saveSecret } from '../support/state';

const home = 'https://gru.api.terrascale.tech';
const replica = 'https://fra.api.terrascale.tech';
const test = base.extend<{ cells: APIRequestContext }>({
  cells: async ({ playwright, request }, use) => {
    const current = journey();
    const exchange = await request.post(`${auth}/terrascale/oauth/token`, {
      form: {
        grant_type: 'client_credentials',
        client_id: current.apiKey.slice(0, 26),
        client_secret: current.apiKey,
        project: current.projectId,
        scope: 'sql:read sql:write databases:read databases:write',
      },
    });
    expect(exchange.status(), 'exchange the setup project key').toBe(200);
    const token = (await exchange.json()).access_token as string;
    saveSecret('placement-token', token);
    const cells = await playwright.request.newContext({
      extraHTTPHeaders: { authorization: `Bearer ${token}` },
      timeout: 15_000,
    });
    try {
      const seeded = `${home}/v1/projects/${current.projectId}/databases/${current.databaseId}`;
      const existing = await cells.get(seeded);
      if (existing.status() === 200) await status(await cells.delete(seeded), 204);
      else await status(existing, 404);
      await use(cells);
    } finally {
      await cells.dispose();
    }
  },
});

test.skip(production || process.env.E2E_REGIONS !== '2', 'placement requires the local stack started with E2E_REGIONS=2');
test.describe.configure({ mode: 'default', timeout: 180_000 });

type Database = { id: string; homeRegion: string; replicaRegions: string[] };
type Progress = { homeRegion: string; replicas: { region: string; revision: string | null; lastPulledAt: string | null; lagMs: number | null }[] };
type Sql = { results: { rows: unknown[][] }[] };
type Signed = { id: string; url: string; headers: Record<string, string> };

const project = () => `/v1/projects/${journey().projectId}`;
const database = (id: string) => `${project()}/databases/${id}`;
const name = (prefix: string) => `${prefix}-${randomBytes(6).toString('hex')}`;

async function status(response: APIResponse, expected: number): Promise<void> {
  expect(response.status(), new URL(response.url()).pathname).toBe(expected);
}

async function json<T>(response: APIResponse, expected = 200): Promise<T> {
  await status(response, expected);
  return response.json() as Promise<T>;
}

async function sql(cells: APIRequestContext, region: string, id: string, statement: string): Promise<Sql> {
  return json<Sql>(await cells.post(`${region}${database(id)}/sql/execute`, { data: { statements: [{ sql: statement }] } }));
}

async function replicated(cells: APIRequestContext, region: string, id: string, value: number): Promise<void> {
  await expect.poll(async () => {
    const response = await cells.post(`${region}${database(id)}/sql/execute`, {
      data: { statements: [{ sql: 'SELECT value FROM placement_rows ORDER BY value' }] },
    });
    if (response.status() === 404 || response.status() === 503) return [];
    if (response.status() === 400) {
      const body = await response.json() as { error: { code: string; message: string } };
      expect(body.error.code).toBe('sql_error');
      expect(body.error.message).toContain('no such table');
      return [];
    }
    const result = await json<Sql>(response);
    if (response.headers()['x-terrabase-region'] !== new URL(region).hostname.split('.')[0]) return [];
    return result.results[0].rows;
  }, { message: `the row is served by ${region}`, timeout: 45_000, intervals: [250, 500, 1000] }).toContainEqual([value]);
}

test('discovery lists both serving regions through both listeners', async ({ request }) => {
  for (const region of [home, replica]) {
    const discovery = await json<{ regions: string[] }>(await request.get(`${region}/v1/regions`));
    expect(discovery.regions).toEqual(['fra', 'gru']);
  }
});

test('create with a replica, observe its pull, move home and remove the old home', async ({ cells }) => {
  const created = await json<Database>(await cells.post(`${home}${project()}/databases`, {
    data: { name: name('placed'), homeRegion: 'gru', replicaRegions: ['fra'] },
  }), 201);
  let servingHome = home;
  try {
    expect(created.homeRegion).toBe('gru');
    expect(created.replicaRegions).toEqual(['fra']);
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)');
    await sql(cells, home, created.id, 'INSERT INTO placement_rows VALUES (1)');
    await replicated(cells, replica, created.id, 1);
    await expect.poll(async () => {
      const progress = await json<Progress>(await cells.get(`${home}${database(created.id)}/regions`));
      expect(progress.homeRegion).toBe('gru');
      return progress.replicas.find((row) => row.region === 'fra');
    }, { message: 'the home reports a completed replica pull', timeout: 45_000, intervals: [250, 500, 1000] })
      .toMatchObject({ region: 'fra', revision: expect.any(String), lastPulledAt: expect.any(String), lagMs: expect.any(Number) });

    const moved = await json<Database>(await cells.post(`${home}${database(created.id)}/move-home`, { data: { homeRegion: 'fra' } }));
    servingHome = replica;
    expect(moved.homeRegion).toBe('fra');
    expect(moved.replicaRegions).toEqual(['gru']);
    await sql(cells, replica, created.id, 'INSERT INTO placement_rows VALUES (2)');
    await replicated(cells, home, created.id, 2);

    const single = await json<Database>(await cells.patch(`${replica}${database(created.id)}`, { data: { replicaRegions: [] } }));
    expect(single.homeRegion).toBe('fra');
    expect(single.replicaRegions).toEqual([]);
    const progress = await json<Progress>(await cells.get(`${replica}${database(created.id)}/regions`));
    expect(progress.replicas).toEqual([]);
    await sql(cells, replica, created.id, 'INSERT INTO placement_rows VALUES (3)');
    expect((await sql(cells, replica, created.id, 'SELECT value FROM placement_rows ORDER BY value')).results[0].rows).toEqual([[1], [2], [3]]);
  } finally {
    await status(await cells.delete(`${servingHome}${database(created.id)}`), 204);
  }
});

test('add a replica to a database created in one region', async ({ cells }) => {
  const created = await json<Database>(await cells.post(`${home}${project()}/databases`, {
    data: { name: name('added'), homeRegion: 'gru', replicaRegions: [] },
  }), 201);
  try {
    expect(created.replicaRegions).toEqual([]);
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)');
    await sql(cells, home, created.id, 'INSERT INTO placement_rows VALUES (4)');
    const updated = await json<Database>(await cells.patch(`${home}${database(created.id)}`, { data: { replicaRegions: ['fra'] } }));
    expect(updated.replicaRegions).toEqual(['fra']);
    await replicated(cells, replica, created.id, 4);
  } finally {
    await status(await cells.delete(`${home}${database(created.id)}`), 204);
  }
});

test('a bucket on the second listener uploads and returns the same bytes', async ({ cells, request }) => {
  const policy = { owner: false, public: false, roles: [] };
  const bucket = await json<{ id: string; region: string }>(await cells.post(`${replica}${project()}/buckets`, {
    data: { name: name('fra-files'), rules: { owner: null, read: policy, write: policy } },
  }), 201);
  const path = `${replica}${project()}/buckets/${bucket.id}`;
  let file: string | undefined;
  try {
    expect(bucket.region).toBe('fra');
    const bytes = randomBytes(4096);
    const signed = await json<Signed>(await cells.post(`${path}/files/upload`, {
      data: { name: 'placement.bin', contentType: 'application/octet-stream', size: bytes.length, sha256: createHash('sha256').update(bytes).digest('base64'), metadata: {} },
    }), 201);
    file = signed.id;
    saveSecret('placement-upload', signed.url);
    expect(new URL(signed.url).pathname).toMatch(/^\/terrascale-fra-files\//);
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
