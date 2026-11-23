import { randomBytes } from 'node:crypto';
import type { APIRequestContext, APIResponse, Page } from '@playwright/test';
import { test as base, expect, type Audit } from '../support/audit';
import { csrfToken } from '../support/files';
import { confirmWithPassword, onboard, signUp, verify } from '../support/hosted';
import { newAddress } from '../support/inbox';
import { createApiKey } from '../support/paid';
import { auth, dash, production } from '../support/stack';
import { keyId } from '../support/api-key';
import { saveSecret } from '../support/state';

const first = 'https://gru.api.terrascale.tech';
const second = 'https://fra.api.terrascale.tech';
const checkout = 'https://checkout.stripe.com';

type Owner = { email: string; password: string; organizationId: string; projectId: string; projectName: string };
type Cells = (page: Page, audit: Audit, projectId: string, label: string) => Promise<APIRequestContext>;
type Database = { id: string; regions: string[] };

const test = base.extend<{ cells: Cells }>({
  cells: async ({ playwright, request }, use) => {
    const opened: APIRequestContext[] = [];
    await use(async (page, audit, projectId, label) => {
      const apiKey = await createApiKey(page, audit, `${label}-key`, `${label} API key`);
      const exchange = await request.post(`${auth}/terrascale/oauth/token`, {
        form: {
          grant_type: 'client_credentials',
          client_id: keyId(apiKey),
          client_secret: apiKey,
          project: projectId,
          scope: 'sql:read sql:write databases:read databases:write',
        },
      });
      expect(exchange.status(), 'exchange the project key').toBe(200);
      const token = (await exchange.json()).access_token as string;
      saveSecret(`${label}-token`, token);
      const context = await playwright.request.newContext({ extraHTTPHeaders: { authorization: `Bearer ${token}` }, timeout: 15_000 });
      opened.push(context);
      return context;
    });
    for (const context of opened) await context.dispose();
  },
});

test.skip(production || process.env.E2E_REGIONS !== '2', 'placement requires the local stack started with E2E_REGIONS=2');
test.describe.configure({ mode: 'default', timeout: 240_000 });

const api = (projectId: string) => `/v1/projects/${projectId}`;
const named = (prefix: string) => `${prefix}-${randomBytes(6).toString('hex')}`;

async function status(response: APIResponse, expected: number): Promise<void> {
  expect(response.status(), new URL(response.url()).pathname).toBe(expected);
}

async function json<T>(response: APIResponse, expected = 200): Promise<T> {
  await status(response, expected);
  return response.json() as Promise<T>;
}

async function owner(page: Page, audit: Audit, label: string): Promise<Owner> {
  const runId = randomBytes(4).toString('hex');
  const email = await newAddress(`${label}-${runId}`);
  const password = randomBytes(24).toString('base64url');
  saveSecret(`${label}-password`, password);
  const projectName = `Closure project ${runId}`;
  await signUp(page, audit, email, password, label);
  await verify(page, email);
  await onboard(page, audit, `Closure ${runId}`, projectName, label);
  const selection = new URL(page.url()).searchParams;
  return { email, password, organizationId: selection.get('organizationId') ?? '', projectId: selection.get('projectId') ?? '', projectName };
}

async function deleteAccount(page: Page, audit: Audit, current: Owner, label: string): Promise<void> {
  await page.goto('/profile?tab=danger');
  await page.locator('main').getByRole('button', { name: 'Delete account', exact: true }).click();
  const dialog = page.locator('#delete-account');
  await expect(dialog).toBeVisible();
  await dialog.locator('#delete-account-email').fill(current.email);
  await dialog.getByRole('button', { name: 'Delete account', exact: true }).click();
  await confirmWithPassword(page, audit, current.password, `${label} delete account`);
  await page.waitForURL((url) => url.origin === dash && url.pathname === '/account-deleted');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Account deleted');
}

async function createDatabase(cell: APIRequestContext, region: string, projectId: string): Promise<Database> {
  const only = new URL(region).hostname.split('.')[0];
  return json<Database>(await cell.post(`${region}${api(projectId)}/databases`, { data: { name: named('closing'), regions: [only] } }), 201);
}

async function deleteProject(page: Page, projectId: string, projectName: string): Promise<APIResponse> {
  await page.goto('/organization?tab=projects');
  await expect(page.locator(`#delete-project-confirmation-${projectId}`), 'the console offers the deletion: it lists no database of the project').toHaveCount(1);
  return page.request.post(`/api/v1/dashboard/organization/projects/${projectId}/delete`, {
    form: { _csrf: await csrfToken(page), confirmation: projectName },
    maxRedirects: 0,
  });
}

test('a project deletion through gru is refused while a database created in fra exists, and completes once it is deleted', async ({ page, audit, cells }) => {
  const current = await owner(page, audit, 'closure-check');
  await page.goto('/organization?tab=billing');
  await page.locator('article.ts-plan', { has: page.locator('#plan-pro') }).getByRole('button', { name: 'Upgrade to Pro', exact: true }).click();
  await page.waitForURL((url) => url.origin === checkout && url.pathname.startsWith('/c/pay/cs_test_'));
  await page.getByRole('button', { name: 'Subscribe', exact: true }).click();
  await page.waitForURL((url) => url.origin === dash && url.pathname === '/organization' && url.searchParams.get('payment') === 'success');
  await expect(page.locator('#billing-summary .ts-billing-plan-name')).toHaveText('Pro');

  const projectName = named('Closing');
  await page.goto('/organization?tab=projects');
  await page.locator('button[commandfor="create-project"]').first().click();
  const dialog = page.locator('dialog#create-project');
  await expect(dialog).toBeVisible();
  await dialog.locator('input[name="name"]').fill(projectName);
  await dialog.locator('button[type="submit"]').click();
  await page.waitForLoadState('load');
  await page.goto('/organization?tab=projects');
  const projectId = await page.locator('dialog[id^="delete-project-"]').evaluateAll(
    (dialogs, title) => dialogs.find((candidate) => candidate.querySelector('h2')?.textContent === title)?.id.slice('delete-project-'.length) ?? '', `Delete ${projectName}?`);
  expect(projectId, 'the new project is listed').toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
  await status(await page.request.post('/api/v1/dashboard/switch-project', { form: { _csrf: await csrfToken(page), projectId, returnUrl: '/api-keys' }, maxRedirects: 0 }), 303);
  const cell = await cells(page, audit, projectId, 'closure-check');
  await status(await page.request.post('/api/v1/dashboard/switch-project', {
    form: { _csrf: await csrfToken(page), projectId: current.projectId, returnUrl: '/organization?tab=projects' }, maxRedirects: 0,
  }), 303);
  const held = await createDatabase(cell, second, projectId);
  let heldRemoved = false;
  try {
    expect(held.regions).toEqual(['fra']);
    await status(await cell.get(`${first}${api(projectId)}/databases/${held.id}`), 200);

    const refused = await deleteProject(page, projectId, projectName);
    await status(refused, 409);
    const kept = await createDatabase(cell, first, projectId);
    try {
      expect(kept.regions).toEqual(['gru']);
    } finally {
      await status(await cell.delete(`${first}${api(projectId)}/databases/${kept.id}`), 204);
    }

    await status(await cell.delete(`${second}${api(projectId)}/databases/${held.id}`), 204);
    heldRemoved = true;
    await expect.poll(async () => (await deleteProject(page, projectId, projectName)).status(), { message: 'the same call completes once fra is empty', timeout: 60_000 }).toBe(303);
    await page.goto('/organization?tab=projects');
    await expect(page.locator(`#delete-project-${projectId}`)).toHaveCount(0);
  } finally {
    if (!heldRemoved) await status(await cell.delete(`${second}${api(projectId)}/databases/${held.id}`), 204);
  }

  await deleteAccount(page, audit, current, 'closure-check');
});
