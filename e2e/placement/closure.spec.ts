import { createHash, randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';
import type { APIRequestContext, APIResponse, Page } from '@playwright/test';
import { test as base, expect, type Audit } from '../support/audit';
import { csrfToken, stored } from '../support/files';
import { confirmWithPassword, onboard, signUp, verify } from '../support/hosted';
import { newAddress } from '../support/inbox';
import { createApiKey } from '../support/paid';
import { auth, dash, production, stateDir } from '../support/stack';
import { saveSecret } from '../support/state';

const first = 'https://gru.api.terrascale.tech';
const second = 'https://fra.api.terrascale.tech';
const firstInternal = 'http://127.0.0.1:8081';
const secondInternal = 'http://127.0.0.2:8081';
const checkout = 'https://checkout.stripe.com';
const crockford = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
const platformOrganization = '00000000000000000000000001';
const dashboardProject = '00000000000000000000000005';

type Owner = { email: string; password: string; organizationId: string; projectId: string; projectName: string };
type Closure = { version: number; closureId: string; authorityOrganizationId: string };
type Answer = { status: number; body: { status?: string; closureId?: string; pendingHolders?: number; notEmptyRegions?: string[]; error?: { code: string } } };
type Cells = (page: Page, audit: Audit, projectId: string, label: string) => Promise<APIRequestContext>;
type Database = { id: string; homeRegion: string };
type Bucket = { id: string; region: string };
type Signed = { id: string; url: string; headers: Record<string, string> };

const test = base.extend<{ cells: Cells }>({
  cells: async ({ playwright, request }, use) => {
    const opened: APIRequestContext[] = [];
    await use(async (page, audit, projectId, label) => {
      const apiKey = await createApiKey(page, audit, `${label}-key`, `${label} API key`);
      const exchange = await request.post(`${auth}/terrascale/oauth/token`, {
        form: {
          grant_type: 'client_credentials',
          client_id: apiKey.slice(0, 26),
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

function ulid(): string {
  let time = Date.now();
  let head = '';
  for (let index = 0; index < 10; index++) {
    head = crockford[time % 32] + head;
    time = Math.floor(time / 32);
  }
  return head + Array.from(randomBytes(16), (byte) => crockford[byte % 32]).join('');
}

function internal(cell: string, method: 'GET' | 'PUT', path: string, body?: Closure, scope: Record<string, string> = {}): Promise<Answer> {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  return new Promise<Answer>((resolve, reject) => {
    const session = connect(cell);
    session.on('error', reject);
    const stream = session.request({ ':method': method, ':path': path, 'content-type': 'application/json', 'x-terrabase-peer-credential': credential, ...scope });
    let status = 0;
    let text = '';
    stream.setEncoding('utf8');
    stream.on('response', (headers) => { status = Number(headers[':status']); });
    stream.on('data', (chunk: string) => { text += chunk; });
    stream.on('error', reject);
    stream.on('end', () => {
      session.close();
      resolve({ status, body: JSON.parse(text) as Answer['body'] });
    });
    stream.end(body ? JSON.stringify(body) : undefined);
  });
}

const close = (cell: string, projectId: string, closure: Closure) => internal(cell, 'PUT', `/admin/projects/${projectId}/closure`, closure);
const forward = (cell: string, projectId: string, closure: Closure) =>
  internal(cell, 'PUT', `/admin/projects/${projectId}/closure`, closure, { 'x-terrabase-closure-scope': 'region' });
const inspect = (cell: string, projectId: string, closure: Closure) =>
  internal(cell, 'GET', `/admin/projects/${projectId}/closure?version=${closure.version}&closureId=${closure.closureId}&authorityOrganizationId=${closure.authorityOrganizationId}`);

async function createDatabase(cell: APIRequestContext, region: string, projectId: string): Promise<Database> {
  const homeRegion = new URL(region).hostname.split('.')[0];
  const created = await json<Database>(await cell.post(`${region}${api(projectId)}/databases`, { data: { name: named('closing'), homeRegion, replicaRegions: [] } }), 201);
  return created;
}

function createBucket(cell: APIRequestContext, region: string, projectId: string): Promise<APIResponse> {
  const policy = { owner: false, public: false, roles: [] };
  return cell.post(`${region}${api(projectId)}/buckets`, { data: { name: named('closing-files'), rules: { owner: null, read: policy, write: policy } } });
}

function reserve(cell: APIRequestContext, bucket: string, bytes: Buffer): Promise<APIResponse> {
  return cell.post(`${bucket}/files/upload`, {
    data: { name: 'closure.bin', contentType: 'application/octet-stream', size: bytes.length, sha256: createHash('sha256').update(bytes).digest('base64'), metadata: {} },
  });
}

async function deleteProject(page: Page, projectId: string, projectName: string): Promise<APIResponse> {
  await page.goto('/organization?tab=projects');
  await expect(page.locator(`#delete-project-confirmation-${projectId}`), 'the console offers the deletion: it lists no database of the project').toHaveCount(1);
  return page.request.post(`/api/v1/dashboard/organization/projects/${projectId}/delete`, {
    form: { _csrf: await csrfToken(page), confirmation: projectName },
    maxRedirects: 0,
  });
}

test('a version 2 closure sent to gru erases the bucket, the file and the database the project holds in fra', async ({ page, audit, cells, request }, info) => {
  const current = await owner(page, audit, 'closure-erase');
  const cell = await cells(page, audit, current.projectId, 'closure-erase');
  const buckets = `${second}${api(current.projectId)}/buckets`;
  const bucket = await json<Bucket>(await createBucket(cell, second, current.projectId), 201);
  expect(bucket.region).toBe('fra');
  const path = `${buckets}/${bucket.id}`;
  const bytes = randomBytes(4096);
  const signed = await json<Signed>(await reserve(cell, path, bytes), 201);
  saveSecret('closure-upload', signed.url);
  const object = new URL(signed.url).pathname;
  expect(object).toMatch(/^\/terrascale-fra-files\//);
  await status(await request.put(signed.url, { headers: signed.headers, data: bytes }), 200);
  await json(await cell.post(`${path}/files/${signed.id}/complete`));
  expect(await stored(object), 'the file is in the object store of fra').toBe(true);
  const database = await createDatabase(cell, second, current.projectId);
  try {
    expect(database.homeRegion).toBe('fra');
    expect((await json<{ items: Bucket[] }>(await cell.get(buckets))).items.map((item) => item.id)).toEqual([bucket.id]);

    const closure = { version: 2, closureId: ulid(), authorityOrganizationId: current.organizationId };
    const answers: Answer[] = [];
    await expect.poll(async () => {
      answers.push(await close(firstInternal, current.projectId, closure));
      return answers.at(-1)?.status;
    }, { message: 'gru accepts the closure once fra has confirmed it', timeout: 60_000, intervals: [500] }).toBe(200);
    expect(answers.slice(0, -1).map((answer) => answer.status).filter((answered) => answered !== 202), 'gru answers 202 pending until then').toEqual([]);
    expect(answers.at(-1)?.body).toMatchObject({ status: 'accepted', closureId: closure.closureId, pendingHolders: 0, notEmptyRegions: [] });
    info.annotations.push({ type: 'closure answers of gru', description: answers.map((answer) => answer.status).join(' ') });
    console.log(`closure answers of gru: ${answers.map((answer) => `${answer.status} ${JSON.stringify(answer.body)}`).join(' | ')}`);
    expect(await inspect(secondInternal, current.projectId, closure), 'fra holds the fence of the same closure and has confirmed it').toMatchObject({ status: 200, body: { status: 'accepted', closureId: closure.closureId } });

    await status(await createBucket(cell, second, current.projectId), 404);
    await status(await reserve(cell, path, bytes), 404);
    expect((await json<{ items: Bucket[] }>(await cell.get(buckets))).items, 'fra lists no bucket of the project').toEqual([]);
    await status(await cell.get(`${path}/files`), 404);
    expect(await stored(object), 'the file left the object store of fra').toBe(false);
    await status(await cell.get(`${second}${api(current.projectId)}/databases/${database.id}`), 404);
    await status(await cell.post(`${second}${api(current.projectId)}/databases`, { data: { name: named('closed'), homeRegion: 'fra', replicaRegions: [] } }), 404);
  } finally {
    const removed = await cell.delete(`${second}${api(current.projectId)}/databases/${database.id}`);
    expect([204, 404], 'delete the database unless closure already erased it').toContain(removed.status());
  }
});

test('a project deletion through gru is refused while fra holds a database of the project, fences nothing, and completes once fra is empty', async ({ page, audit, cells }) => {
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
    expect(held.homeRegion).toBe('fra');
    const probe = { version: 1, closureId: ulid(), authorityOrganizationId: current.organizationId };
    expect(await inspect(firstInternal, projectId, probe), 'gru itself holds nothing of the project').toMatchObject({ status: 200, body: { status: 'empty' } });
    expect(await inspect(secondInternal, projectId, probe), 'fra holds a database of the project').toMatchObject({ status: 409, body: { error: { code: 'not_empty' } } });

    const refused = await deleteProject(page, projectId, projectName);
    await status(refused, 409);
    expect(await inspect(firstInternal, projectId, probe), 'the refused deletion wrote no fence in gru').toMatchObject({ status: 200, body: { status: 'empty' } });
    expect(await inspect(secondInternal, projectId, probe), 'the refused deletion wrote no fence in fra').toMatchObject({ status: 409, body: { error: { code: 'not_empty' } } });
    const kept = await createDatabase(cell, first, projectId);
    try {
      expect(kept.homeRegion).toBe('gru');
    } finally {
      await status(await cell.delete(`${first}${api(projectId)}/databases/${kept.id}`), 204);
    }

    await status(await cell.delete(`${second}${api(projectId)}/databases/${held.id}`), 204);
    heldRemoved = true;
    await expect.poll(async () => (await deleteProject(page, projectId, projectName)).status(), { message: 'the same call completes once fra is empty', timeout: 60_000 }).toBe(303);
    await page.goto('/organization?tab=projects');
    await expect(page.locator(`#delete-project-${projectId}`)).toHaveCount(0);
    for (const cellInternal of [firstInternal, secondInternal]) {
      expect(await inspect(cellInternal, projectId, probe), `${cellInternal} holds the fence of the completed deletion`).toMatchObject({ status: 409, body: { error: { code: 'closure_conflict' } } });
    }
  } finally {
    if (!heldRemoved) await status(await cell.delete(`${second}${api(projectId)}/databases/${held.id}`), 204);
  }

  await deleteAccount(page, audit, current, 'closure-check');
});

test('fra refuses a closure, a closure sent on by another region and a check of the dashboard platform project', async () => {
  const refused = { status: 409, body: { error: { code: 'platform_project_protected' } } };
  for (const version of [1, 2]) {
    const closure = { version, closureId: ulid(), authorityOrganizationId: platformOrganization };
    expect(await close(secondInternal, dashboardProject, closure), `a version ${version} closure`).toMatchObject(refused);
    expect(await forward(secondInternal, dashboardProject, closure), `a version ${version} closure sent on by gru`).toMatchObject(refused);
    expect(await inspect(secondInternal, dashboardProject, closure), `the check of that version ${version} closure`).toMatchObject(refused);
  }
});
