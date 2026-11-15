import { createServer, type Server } from 'node:http';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, request, test } from '@playwright/test';
import { keyId } from '../support/api-key';
import { signInToken } from '../support/auth-management';
import { auth, production, stateDir } from '../support/stack';
import { saveSecret, type Journey } from '../support/state';
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

function owner(): Journey {
  return JSON.parse(readFileSync(join(stateDir, 'setup.journey.json'), 'utf8')) as Journey;
}

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
  const [apiResponse, pageResponse, summaryResponse] = await Promise.all([
    fetch(`${origin}/api/v1/status`),
    fetch(`${origin}/`),
    fetch(`${origin}/api/v1/summary.json`),
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

async function sql(statements: { sql: string }[]): Promise<void> {
  const current = owner();
  const context = await request.newContext({ storageState: join(stateDir, 'setup.session.json') });
  try {
    const exchanged = await context.post(`${auth}/terrascale/oauth/token`, {
      form: {
        grant_type: 'client_credentials',
        client_id: keyId(current.apiKey),
        client_secret: current.apiKey,
        project: current.projectId,
        scope: 'sql:read sql:write',
      },
    });
    expect(exchanged.status()).toBe(200);
    const write = (await exchanged.json()).access_token as string;
    saveSecret('status-canary-sql', write);
    const response = await context.post(`https://api.terrascale.tech/v1/projects/${current.projectId}/databases/${current.databaseId}/sql/execute`, {
      headers: { Authorization: `Bearer ${write}` },
      data: { statements },
    });
    expect(response.status()).toBe(200);
  } finally {
    await context.dispose();
  }
}

let site: StatusSite | undefined;
let canaryKeyId = '';
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

test.describe('canary credential', () => {
  test.beforeAll(async () => {
    if (production) return;
    const current = owner();
    const context = await request.newContext({ storageState: join(stateDir, 'setup.session.json') });
    try {
      const token = await signInToken(context, 'status-canary');
      const created = await context.post(`${auth}/v1/terrascale/organizations/${current.organizationId}/projects/${current.projectId}/api-keys`, {
        headers: { Authorization: `Bearer ${token}` },
        data: {
          name: 'status canary',
          scopes: ['sql:read'],
          database: current.databaseId,
          expires_at: new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString(),
        },
      });
      expect(created.status()).toBe(201);
      const body = await created.json();
      const secret = body.key as string;
      canaryKeyId = body.api_key.id as string;
      saveSecret('status-canary-key', secret);
      await sql([
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
      site = await startStatusSite(catalog, {
        DATA_API_CANARY: JSON.stringify({
          [canaryEndpoint]: {
            origin: 'https://api.terrascale.tech',
            region: current.databaseRegion.toLowerCase(),
            project: current.projectId,
            database: current.databaseName,
            issuer: 'https://auth.terrascale.tech/terrascale',
            key: secret,
          },
        }),
      });
    } finally {
      await context.dispose();
    }
  });

  test.afterAll(async () => {
    await site?.stop();
    site = undefined;
    if (production) return;
    await sql([{ sql: 'DROP TABLE IF EXISTS canary' }]);
  });

  test('a revoked canary credential is unknown and an unreachable component is unknown', async () => {
    test.skip(production, 'the status worker runs against the local stack');
    const origin = site?.origin ?? '';
    const current = owner();
    await expect.poll(async () => healthy(await view(origin)), { timeout: 45_000 }).toEqual(healthyState);

    const context = await request.newContext({ storageState: join(stateDir, 'setup.session.json') });
    try {
      const token = await signInToken(context, 'status-canary-revoke');
      const revoked = await context.delete(`${auth}/v1/terrascale/organizations/${current.organizationId}/projects/${current.projectId}/api-keys/${canaryKeyId}`, {
        headers: { Authorization: `Bearer ${token}` },
      });
      expect(revoked.status()).toBe(204);
    } finally {
      await context.dispose();
    }

    await expect.poll(async () => {
      const seen = await view(origin);
      const canaryBadge = badge(seen.page, 'Canary');
      return {
        status: seen.api.status,
        canary: component(seen.api, 'Canary')?.status ?? '',
        closed: component(seen.api, 'Closed port')?.status ?? '',
        summaryCanary: summaryStatus(seen.summary, 'Canary'),
        summaryClosed: summaryStatus(seen.summary, 'Closed port'),
        headline: seen.page.includes('Component status unknown'),
        issue: seen.api.monitoringIssues.some((item) => item.note === 'Canary credential invalid'),
        pageIssue: seen.page.includes('Canary credential invalid'),
        operational: canaryBadge.includes('Operational'),
        closedBadge: badge(seen.page, 'Closed port').includes('Unknown'),
        operationalHeadline: seen.page.includes('All systems operational'),
      };
    }, { timeout: 50_000 }).toEqual({
      status: 'unknown',
      canary: 'none',
      closed: 'none',
      summaryCanary: 'unknown',
      summaryClosed: 'unknown',
      headline: true,
      issue: true,
      pageIssue: true,
      operational: false,
      closedBadge: true,
      operationalHeadline: false,
    });
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
    site = await startStatusSite(catalog, {});
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
    await expect.poll(async () => {
      const seen = await view(site?.origin ?? '');
      const mark = badge(seen.page, 'Failing check');
      return {
        status: seen.api.status,
        component: component(seen.api, 'Failing check')?.status ?? '',
        summary: summaryStatus(seen.summary, 'Failing check'),
        headline: seen.page.includes('Major system outage') && seen.page.includes('data-state="major_outage"'),
        outage: mark.includes('Major outage'),
        unknown: mark.includes('Unknown'),
      };
    }, { timeout: 75_000 }).toEqual({
      status: 'major_outage',
      component: 'major_outage',
      summary: 'major_outage',
      headline: true,
      outage: true,
      unknown: false,
    });
  });
});
