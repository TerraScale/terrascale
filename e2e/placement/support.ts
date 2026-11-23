import { randomBytes } from 'node:crypto';
import { test as base, expect, type APIRequestContext, type APIResponse, type APIRequest } from '@playwright/test';
import { auth } from '../support/stack';
import { keyId } from '../support/api-key';
import { journey, saveSecret } from '../support/state';

export const gru = 'https://gru.api.terrascale.tech';
export const fra = 'https://fra.api.terrascale.tech';
export const bookmarkHeader = 'terrascale-bookmark';
export const consistencyHeader = 'terrascale-consistency';
export type Cells = Pick<APIRequestContext, 'get' | 'post' | 'put' | 'patch' | 'delete'>;
export async function authenticatedCells(api: APIRequest, request: APIRequestContext, apiKey: string, projectId: string): Promise<Cells & { dispose(): Promise<void> }> {
  const context = await api.newContext({ timeout: 15_000 });
  let authorization = '';
  let expires = 0;
  let renewing: Promise<Record<string, string>> | undefined;
  const exchange = async () => {
    const response = await request.post(`${auth}/terrascale/oauth/token`, {
      form: {
        grant_type: 'client_credentials',
        client_id: keyId(apiKey),
        client_secret: apiKey,
        project: projectId,
        scope: 'sql:read sql:write databases:read databases:write',
      },
    });
    expect(response.status(), '200 exchanged project credential').toBe(200);
    const grant = await response.json() as { access_token: string; expires_in: number };
    saveSecret('placement-token', grant.access_token);
    authorization = `Bearer ${grant.access_token}`;
    expires = Date.now() + grant.expires_in * 1000 - 10_000;
    return { authorization };
  };
  const headers = async () => {
    if (Date.now() < expires) return { authorization };
    renewing ??= exchange().finally(() => { renewing = undefined; });
    return renewing;
  };
  return {
    get: async (url, options) => context.get(url, { ...options, headers: { ...await headers(), ...options?.headers } }),
    post: async (url, options) => context.post(url, { ...options, headers: { ...await headers(), ...options?.headers } }),
    put: async (url, options) => context.put(url, { ...options, headers: { ...await headers(), ...options?.headers } }),
    patch: async (url, options) => context.patch(url, { ...options, headers: { ...await headers(), ...options?.headers } }),
    delete: async (url, options) => context.delete(url, { ...options, headers: { ...await headers(), ...options?.headers } }),
    dispose: () => context.dispose(),
  };
}

export const test = base.extend<{ cells: Cells }>({
  cells: async ({ playwright, request }, use) => {
    const current = journey();
    const cells = await authenticatedCells(playwright.request, request, current.apiKey, current.projectId);
    try {
      const seeded = `${gru}/v1/projects/${current.projectId}/databases/${current.databaseId}`;
      const existing = await cells.get(seeded);
      if (existing.status() === 200) await status(await cells.delete(seeded), 204);
      else await status(existing, 404);
      await use(cells);
    } finally {
      await cells.dispose();
    }
  },
});


export type Database = { id: string; name: string; region: string; regions: string[]; allRegions: boolean; placementVersion: number; defaultConsistency: Consistency };
export type Replication = { databaseId: string; observedAtUtc: string; regions: { region: string; appliedBookmark: string; lagMs: number; healthy: boolean }[] };
export type Sql = { results: { rows: unknown[][] }[] };
export type Consistency = 'fast' | 'strong';
export type Answer = { results: { columns: { name: string; type: string | null }[]; rows: unknown[][]; rowsAffected: number; lastInsertRowId: number }[]; bookmark: string; consistency: Consistency };
export type Causality = { bookmark?: string; consistency?: Consistency };

export const project = (projectId = journey().projectId) => `/v1/projects/${projectId}`;
export const database = (id: string, projectId?: string) => `${project(projectId)}/databases/${id}`;
export const name = (prefix: string) => `${prefix}-${randomBytes(6).toString('hex')}`;
export const regionOf = (origin: string) => new URL(origin).hostname.split('.')[0];

export async function status(response: APIResponse, expected: number): Promise<void> {
  const failure = response.status() === expected ? '' : '\n' + await response.text();
  expect(response.status(), new URL(response.url()).pathname + failure).toBe(expected);
}

export async function json<T>(response: APIResponse, expected = 200): Promise<T> {
  await status(response, expected);
  return response.json() as Promise<T>;
}

export async function sql(cells: Cells, region: string, id: string, statement: string, projectId?: string): Promise<Sql> {
  return json<Sql>(await cells.post(`${region}${database(id, projectId)}/sql/execute`, { data: { statements: [{ sql: statement }] } }));
}

export function run(cells: Cells, region: string, id: string, statements: string[], causality: Causality = {}, projectId?: string): Promise<APIResponse> {
  return cells.post(`${region}${database(id, projectId)}/sql`, {
    data: { statements: statements.map(statement => ({ sql: statement })) },
    headers: {
      ...(causality.bookmark === undefined ? {} : { [bookmarkHeader]: causality.bookmark }),
      ...(causality.consistency === undefined ? {} : { [consistencyHeader]: causality.consistency }),
    },
  });
}

export async function answer(cells: Cells, region: string, id: string, statements: string[], causality: Causality = {}, projectId?: string): Promise<Answer> {
  const response = await run(cells, region, id, statements, causality, projectId);
  const body = await json<Answer>(response);
  expect(response.headers()[bookmarkHeader], 'the answer carries the bookmark of its body').toBe(body.bookmark);
  expect(response.headers()[consistencyHeader], 'the answer carries the consistency of its body').toBe(body.consistency);
  expect(response.headers()['x-terrabase-region'], 'the addressed region answered').toBe(regionOf(region));
  return body;
}

export async function replicated(cells: Cells, region: string, id: string, value: number, projectId?: string): Promise<void> {
  await converged(cells, region, id, 'SELECT value FROM placement_rows ORDER BY value', rows => rows.some(row => row[0] === value), projectId);
}

export async function converged(cells: Cells, region: string, id: string, statement: string, done: (rows: unknown[][]) => boolean, projectId?: string): Promise<unknown[][]> {
  let rows: unknown[][] = [];
  await expect.poll(async () => {
    const response = await run(cells, region, id, [statement], {}, projectId);
    if (response.status() === 404) return false;
    if (response.status() === 400) {
      expect((await response.json() as { error: { code: string } }).error.code, 'only a table the region has not received yet is refused').toBe('undefined_table');
      return false;
    }
    const result = await json<Answer>(response);
    expect(response.headers()['x-terrabase-region']).toBe(regionOf(region));
    rows = result.results[0].rows;
    return done(rows);
  }, { message: `${regionOf(region)} converges on ${statement}`, timeout: 45_000, intervals: [250, 500, 1000] }).toBe(true);
  return rows;
}

export async function regions(cells: Cells, region: string, id: string, projectId?: string): Promise<Replication> {
  const response = await cells.get(`${region}${database(id, projectId)}/regions`);
  const body = await json<Replication>(response);
  expect(body.databaseId).toBe(id);
  return body;
}

export async function removeDatabase(cells: Cells, id: string, projectId?: string): Promise<void> {
  await expect.poll(async () => {
    const response = await cells.delete(`${gru}${database(id, projectId)}`);
    if (response.status() === 429) return false;
    await status(response, 204);
    return true;
  }, { message: 'delete the database once the organization budget refills', timeout: 60_000, intervals: [250, 500, 1000] }).toBe(true);
}
