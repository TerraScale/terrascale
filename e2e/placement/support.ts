import { randomBytes } from 'node:crypto';
import { test as base, expect, type APIRequestContext, type APIResponse } from '@playwright/test';
import { auth } from '../support/stack';
import { journey, saveSecret } from '../support/state';

export const home = 'https://gru.api.terrascale.tech';
export const replica = 'https://fra.api.terrascale.tech';
export type Cells = Pick<APIRequestContext, 'get' | 'post' | 'put' | 'patch' | 'delete'>;
export const test = base.extend<{ cells: Cells }>({
  cells: async ({ playwright, request }, use) => {
    const current = journey();
    const context = await playwright.request.newContext({ timeout: 15_000 });
    let authorization = '';
    let expires = 0;
    let renewing: Promise<Record<string, string>> | undefined;
    const exchange = async () => {
      const response = await request.post(`${auth}/terrascale/oauth/token`, {
        form: {
          grant_type: 'client_credentials',
          client_id: current.apiKey.slice(0, 26),
          client_secret: current.apiKey,
          project: current.projectId,
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
    const cells: Cells = {
      get: async (url, options) => context.get(url, { ...options, headers: { ...await headers(), ...options?.headers } }),
      post: async (url, options) => context.post(url, { ...options, headers: { ...await headers(), ...options?.headers } }),
      put: async (url, options) => context.put(url, { ...options, headers: { ...await headers(), ...options?.headers } }),
      patch: async (url, options) => context.patch(url, { ...options, headers: { ...await headers(), ...options?.headers } }),
      delete: async (url, options) => context.delete(url, { ...options, headers: { ...await headers(), ...options?.headers } }),
    };
    try {
      const seeded = `${home}/v1/projects/${current.projectId}/databases/${current.databaseId}`;
      const existing = await cells.get(seeded);
      if (existing.status() === 200) await status(await cells.delete(seeded), 204);
      else await status(existing, 404);
      await use(cells);
    } finally {
      await context.dispose();
    }
  },
});


export type Database = { id: string; homeRegion: string; replicaRegions: string[] };
export type Progress = { homeRegion: string; replicas: { region: string; revision: string | null; lastPulledAt: string | null; lagMs: number | null }[] };
export type Sql = { results: { rows: unknown[][] }[] };

export const project = () => `/v1/projects/${journey().projectId}`;
export const database = (id: string) => `${project()}/databases/${id}`;
export const name = (prefix: string) => `${prefix}-${randomBytes(6).toString('hex')}`;

export async function status(response: APIResponse, expected: number): Promise<void> {
  expect(response.status(), new URL(response.url()).pathname).toBe(expected);
}

export async function json<T>(response: APIResponse, expected = 200): Promise<T> {
  await status(response, expected);
  return response.json() as Promise<T>;
}

export async function sql(cells: Cells, region: string, id: string, statement: string): Promise<Sql> {
  return json<Sql>(await cells.post(`${region}${database(id)}/sql/execute`, { data: { statements: [{ sql: statement }] } }));
}

export async function replicated(cells: Cells, region: string, id: string, value: number): Promise<void> {
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


export async function removeDatabase(cells: Cells, id: string): Promise<void> {
  await expect.poll(async () => {
    let response = await cells.delete(`${home}${database(id)}`);
    if (response.status() === 421) {
      expect((await response.json()).error.code).toBe('wrong_region');
      response = await cells.delete(`https://${response.headers()['x-terrabase-database-home']}.api.terrascale.tech${database(id)}`);
    }
    if (response.status() === 409) {
      expect((await response.json()).error.code).toBe('move_in_progress');
      return false;
    }
    await status(response, 204);
    return true;
  }, { message: 'delete the database after any pending move settles', timeout: 60_000, intervals: [250, 500, 1000] }).toBe(true);
}
