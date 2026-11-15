import { createServer, type Server } from 'node:http';
import { expect, test, type APIRequestContext, type APIResponse, type Page } from '@playwright/test';
import { keyId } from '../support/api-key';
import { signInToken } from '../support/auth-management';
import { createDedicatedProjectOwner } from '../support/project-owner';
import { auth, production } from '../support/stack';
import { saveSecret } from '../support/state';
import { startStatusSite, ulid, type StatusSite } from '../support/status-worker';

test.describe.configure({ mode: 'serial' });

const fortnight = 14 * 24 * 60 * 60 * 1000;

type Component = { id: string; name: string; status: string };
type StatusBody = {
  status: string;
  credentials: { componentId: string; expiresAt: string }[];
  monitoringIssues: { note: string; componentId: string }[];
  groups: { components: Component[] }[];
};
type Summary = { status: { description: string }; components: { name: string; status: string }[] };

function component(body: StatusBody, name: string): Component | undefined {
  return body.groups.flatMap((group) => group.components).find((item) => item.name === name);
}

function summaryStatus(body: Summary, name: string): string {
  return body.components.find((item) => item.name === name)?.status ?? '';
}

function badge(html: string, name: string): string {
  const at = html.indexOf(`>${name}</a>`);
  if (at < 0) return '';
  const mark = html.indexOf('ts-badge', at);
  return mark < 0 || mark > at + 4000 ? '' : html.slice(mark, mark + 240);
}

async function view(origin: string): Promise<{ api: StatusBody; page: string; summary: Summary }> {
  const headers = { 'cache-control': 'no-cache' };
  const [apiResponse, pageResponse, summaryResponse] = await Promise.all([
    fetch(`${origin}/api/v1/status`, { headers }),
    fetch(`${origin}/`, { headers }),
    fetch(`${origin}/api/v1/summary.json`, { headers }),
  ]);
  expect(apiResponse.status).toBe(200);
  expect(pageResponse.status).toBe(200);
  expect(summaryResponse.status).toBe(200);
  return {
    api: await apiResponse.json() as StatusBody,
    page: await pageResponse.text(),
    summary: await summaryResponse.json() as Summary,
  };
}

function endpoint(id: string, componentId: string, url: string, kind: 'http' | 'canary', live: boolean): string {
  const since = live ? "datetime('now', '-1 day')" : 'NULL';
  return `INSERT INTO endpoints (id, component_id, cluster, provider, url, headers, expect_status, assertions, extract, indirect, kind, live_since) VALUES ('${id}', '${componentId}', 'journey', 'other', '${url}', '{}', '200', '[]', '{}', 0, '${kind}', ${since});`;
}

async function round(origin: string): Promise<void> {
  const response = await fetch(`${origin}/dev/round`, { method: 'POST' });
  const text = await response.text();
  expect(response.status, text).toBe(200);
}

async function answered(response: APIResponse, expected: number): Promise<void> {
  const status = response.status();
  expect(status, status === expected ? '' : await response.text()).toBe(expected);
}

async function writeToken(context: APIRequestContext, apiKey: string, projectId: string): Promise<string> {
  const exchanged = await context.post(`${auth}/terrascale/oauth/token`, {
    form: {
      grant_type: 'client_credentials',
      client_id: keyId(apiKey),
      client_secret: apiKey,
      project: projectId,
      scope: 'sql:read sql:write',
    },
  });
  await answered(exchanged, 200);
  const token = (await exchanged.json()).access_token as string;
  saveSecret('status-canary-sql', token);
  return token;
}

async function createOwnedDatabase(page: Page, name: string): Promise<{ id: string; region: string }> {
  await page.goto('/databases');
  await page.getByRole('button', { name: 'New database', exact: true }).first().click();
  await expect(page.locator('#create-database[open]')).toBeVisible();
  const picked = page.locator('#create-database-regions input[name="regions"]:checked');
  const region = (await picked.inputValue()).toLowerCase();
  await page.locator('#database-name').fill(name);
  await page.locator('#create-database form').getByRole('button', { name: 'Create database', exact: true }).click();
  await page.waitForURL(/\/databases\/[0-9A-HJKMNP-TV-Z]{26}(\?|$)/);
  return { id: new URL(page.url()).pathname.split('/')[2] ?? '', region };
}

async function sql(context: APIRequestContext, token: string, projectId: string, databaseId: string, statements: { sql: string }[]): Promise<void> {
  const response = await context.post(`https://api.terrascale.tech/v1/projects/${projectId}/databases/${databaseId}/sql/execute`, {
    headers: { Authorization: `Bearer ${token}` },
    data: { statements },
  });
  await answered(response, 200);
}

let site: StatusSite | undefined;
let failing: Server | undefined;

function healthy(seen: { api: StatusBody; page: string; summary: Summary }): {
  status: string; canary: string; closed: string; summaryCanary: string; summaryClosed: string;
  headline: boolean; warning: boolean; canaryBadge: boolean; closedBadge: boolean; expiresSoon: boolean; operationalHeadline: boolean;
} {
  const credential = seen.api.credentials[0];
  const expires = credential === undefined ? 0 : Date.parse(credential.expiresAt) - Date.now();
  return {
    status: seen.api.status,
    canary: component(seen.api, 'Canary')?.status ?? '',
    closed: component(seen.api, 'Closed port')?.status ?? '',
    summaryCanary: summaryStatus(seen.summary, 'Canary'),
    summaryClosed: summaryStatus(seen.summary, 'Closed port'),
    headline: seen.page.includes('Component status unknown') && seen.page.includes('data-state="unknown"'),
    warning: seen.page.includes('Canary credential expiring for Canary'),
    canaryBadge: badge(seen.page, 'Canary').includes('Operational'),
    closedBadge: badge(seen.page, 'Closed port').includes('Unknown'),
    expiresSoon: expires > 0 && expires < fortnight,
    operationalHeadline: seen.page.includes('All systems operational'),
  };
}

const healthyState = {
  status: 'unknown',
  canary: 'operational',
  closed: 'none',
  summaryCanary: 'operational',
  summaryClosed: 'unknown',
  headline: true,
  warning: true,
  canaryBadge: true,
  closedBadge: true,
  expiresSoon: true,
  operationalHeadline: false,
};

type CanaryFixture = {
  origin: string;
  site: StatusSite;
  account: Awaited<ReturnType<typeof createDedicatedProjectOwner>>;
  canaryKeyId: string;
  token: string;
};

let canary: CanaryFixture | undefined;

test.describe('canary credential', () => {
  test.beforeAll(async ({ browser }, info) => {
    if (production) return;
    const account = await createDedicatedProjectOwner(browser, info, 'Status canary');
    const current = account.current;
    const context = account.page.request;
    const writer = await writeToken(context, current.apiKey, current.projectId);
    const name = `canary-${current.runId}`;
    const database = await createOwnedDatabase(account.page, name);
    const token = await signInToken(context, `status-canary-${current.runId}`);
    const created = await context.post(`${auth}/v1/terrascale/organizations/${current.organizationId}/projects/${current.projectId}/api-keys`, {
      headers: { Authorization: `Bearer ${token}` },
      data: {
        name: `status canary ${current.runId}`,
        scopes: ['sql:read'],
        database: database.id,
        expires_at: new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString(),
      },
    });
    await answered(created, 201);
    const body = await created.json();
    const secret = body.key as string;
    saveSecret('status-canary-key', secret);
    await sql(context, writer, current.projectId, database.id, [
      { sql: 'CREATE TABLE IF NOT EXISTS canary (id TEXT PRIMARY KEY)' },
      { sql: "INSERT OR IGNORE INTO canary (id) VALUES ('00000000000000000000000007')" },
    ]);
    const group = ulid();
    const canaryComponent = ulid();
    const closedComponent = ulid();
    const canaryEndpoint = ulid();
    const catalog = [
      `INSERT INTO component_groups (id, name) VALUES ('${group}', 'Journey');`,
      `INSERT INTO components (id, group_id, name) VALUES ('${canaryComponent}', '${group}', 'Canary');`,
      `INSERT INTO components (id, group_id, name) VALUES ('${closedComponent}', '${group}', 'Closed port');`,
      endpoint(canaryEndpoint, canaryComponent, 'https://api.terrascale.tech/canary', 'canary', false),
      endpoint(ulid(), closedComponent, 'http://127.0.0.1:1/', 'http', true),
    ].join('\n');
    const started = await startStatusSite(catalog, {
      ROUND_NOW: '1',
      DATA_API_CANARY: JSON.stringify({
        [canaryEndpoint]: {
          origin: 'https://api.terrascale.tech',
          region: database.region,
          project: current.projectId,
          database: name,
          issuer: 'https://auth.terrascale.tech/terrascale',
          key: secret,
        },
      }),
    });
    await round(started.origin);
    canary = { origin: started.origin, site: started, account, canaryKeyId: body.api_key.id as string, token };
  });

  test.afterAll(async () => {
    const current = canary;
    canary = undefined;
    await current?.site.stop();
    await current?.account.remove();
  });

  test('a measured canary is operational and its credential expiry is warned', async () => {
    test.skip(production, 'the status worker runs against the local stack');
    expect(healthy(await view(canary?.origin ?? ''))).toEqual(healthyState);
  });

  test('an unreachable component is unknown', async () => {
    test.skip(production, 'the status worker runs against the local stack');
    const seen = await view(canary?.origin ?? '');
    expect(component(seen.api, 'Closed port')?.status).toBe('none');
    expect(summaryStatus(seen.summary, 'Closed port')).toBe('unknown');
    expect(badge(seen.page, 'Closed port')).toContain('Unknown');
    expect(seen.page).toContain('Component status unknown');
    expect(seen.page).not.toContain('All systems operational');
  });

  test('a revoked canary credential is unknown', async () => {
    test.skip(production, 'the status worker runs against the local stack');
    const current = canary;
    if (!current) throw new Error('canary fixture missing');
    const revoked = await current.account.page.request.delete(`${auth}/v1/terrascale/organizations/${current.account.current.organizationId}/projects/${current.account.current.projectId}/api-keys/${current.canaryKeyId}`, {
      headers: { Authorization: `Bearer ${current.token}` },
    });
    await answered(revoked, 204);
    await round(current.origin);
    const seen = await view(current.origin);
    expect(seen.api.status).toBe('unknown');
    expect(component(seen.api, 'Canary')?.status).toBe('none');
    expect(summaryStatus(seen.summary, 'Canary')).toBe('unknown');
    expect(seen.api.monitoringIssues.some((item) => item.note === 'Canary credential invalid')).toBe(true);
    expect(seen.page).toContain('Canary credential invalid');
    expect(badge(seen.page, 'Canary')).not.toContain('Operational');
    expect(seen.page).not.toContain('All systems operational');
  });
});

test.describe('http 500', () => {
  test.beforeAll(async () => {
    if (production) return;
    failing = createServer((_req, response) => {
      response.writeHead(500);
      response.end('fail');
    });
    await new Promise<void>((resolve) => failing?.listen(0, '127.0.0.1', () => resolve()));
    const address = failing.address();
    if (address === null || typeof address === 'string') throw new Error('the failing listener has no port');
    const group = ulid();
    const componentId = ulid();
    const catalog = [
      `INSERT INTO component_groups (id, name) VALUES ('${group}', 'Journey');`,
      `INSERT INTO components (id, group_id, name) VALUES ('${componentId}', '${group}', 'Failing check');`,
      endpoint(ulid(), componentId, `http://127.0.0.1:${address.port}/`, 'http', true),
    ].join('\n');
    site = await startStatusSite(catalog, { ROUND_NOW: '1' }, 8792);
    await round(site.origin);
    await round(site.origin);
  });

  test.afterAll(async () => {
    await site?.stop();
    site = undefined;
    if (failing) {
      await new Promise<void>((resolve, reject) => failing?.close((error) => (error ? reject(error) : resolve())));
      failing = undefined;
    }
  });

  test('an HTTP 500 stays an outage', async () => {
    test.skip(production, 'the status worker runs against the local stack');
    const seen = await view(site?.origin ?? '');
    const mark = badge(seen.page, 'Failing check');
    expect(seen.api.status).toBe('major_outage');
    expect(component(seen.api, 'Failing check')?.status).toBe('major_outage');
    expect(summaryStatus(seen.summary, 'Failing check')).toBe('major_outage');
    expect(seen.page).toContain('Major system outage');
    expect(seen.page).toContain('data-state="major_outage"');
    expect(mark).toContain('Major outage');
    expect(mark).not.toContain('Unknown');
  });
});
