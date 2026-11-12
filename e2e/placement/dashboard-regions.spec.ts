import { expect, type APIResponse, type Page } from '@playwright/test';
import { test, home, replica, database, name, json, sql, project, type Cells } from './support';
import { production, sessionFile } from '../support/stack';

test.skip(production || process.env.E2E_REGIONS !== '2', 'placement requires the local stack started with E2E_REGIONS=2');
test.describe.configure({ mode: 'default', timeout: 180_000 });
test.use({ storageState: sessionFile('placement-setup') });

type Database = { id: string; name: string; regions: string[] };

const copying = 'Frankfurt added. It is still copying this database, which usually takes about half a minute. Save again to finish; until then São Paulo keeps serving.';
const stillCopying = 'Frankfurt is still copying this database, which usually takes about half a minute. Save again to finish; until then São Paulo keeps serving.';

async function routed(send: (origin: string) => Promise<APIResponse>): Promise<APIResponse> {
  const response = await send(home);
  return response.status() === 421 ? send(`https://${response.headers()['x-terrabase-database-home']}.api.terrascale.tech`) : response;
}

async function read(cells: Cells, id: string): Promise<Database> {
  return json<Database>(await routed((origin) => cells.get(`${origin}${database(id)}`)));
}

async function stored(cells: Cells, id: string): Promise<string[]> {
  return (await read(cells, id)).regions;
}

async function settled(cells: Cells, id: string): Promise<void> {
  await expect.poll(() => stored(cells, id), { message: 'every listener reports Frankfurt only', timeout: 45_000, intervals: [250, 500, 1000] }).toEqual(['fra']);
}

async function remove(cells: Cells, id: string): Promise<void> {
  const response = await routed((origin) => cells.delete(`${origin}${database(id)}`));
  expect([204, 404], 'the temporary database is deleted').toContain(response.status());
}

async function moveToSaoPaulo(cells: Cells, id: string): Promise<void> {
  const statuses: number[] = [];
  await expect.poll(async () => {
    const response = await routed((origin) => cells.patch(`${origin}${database(id)}`, { data: { regions: ['gru'] } }));
    statuses.push(response.status());
    expect([200, 503], '200 updated or 503 still copying').toContain(response.status());
    return response.status();
  }, { message: 'the API moves the database to São Paulo', timeout: 90_000, intervals: [1_000] }).toBe(200);
  console.log(`API move to São Paulo statuses: ${JSON.stringify(statuses)}`);
  expect(await stored(cells, id)).toEqual(['gru']);
}

async function renameInDashboard(page: Page, cells: Cells, id: string): Promise<string> {
  const current = (await read(cells, id)).name;
  const renamed = name('ui-renamed');
  await page.goto(`/databases/${id}/settings`);
  await page.locator('#rename-name').fill(renamed);
  await page.locator('#rename-confirm').fill(current);
  const answer = page.waitForResponse((response) => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/v1/dashboard/databases/rename');
  await page.getByRole('button', { name: 'Rename database', exact: true }).click();
  const status = (await answer).status();
  await page.waitForLoadState('load');
  await expect(page.locator('#rename-error'), 'the rename shows no error').toHaveCount(0);
  expect(status, 'the rename saves and redirects').toBe(303);
  await expect(page.locator('.ts-db-head .ts-db-title')).toHaveText(renamed);
  await expect(page.locator('main .ts-alert-success .ts-alert-title')).toHaveText(`Database renamed to ${renamed}`);
  expect((await read(cells, id)).name).toBe(renamed);
  return renamed;
}

async function deleteFromStaleDangerPage(page: Page, cells: Cells, id: string, current: string): Promise<void> {
  await page.goto('about:blank');
  await moveToSaoPaulo(cells, id);
  const fresh = await page.context().newPage();
  try {
    await fresh.goto(`/databases/${id}/danger`);
    expect(await badges(fresh), 'the danger page shows the record from before the API move').toEqual(['fra']);
    await fresh.locator('main').getByRole('button', { name: 'Delete database', exact: true }).click();
    const dialog = fresh.locator('dialog[open]');
    await expect(dialog).toBeVisible();
    await dialog.locator('input[name="confirmName"]').fill(current);
    await dialog.getByRole('button', { name: 'Delete database', exact: true }).click();
    await fresh.waitForURL((url) => url.pathname === '/databases');
    await expect(fresh.locator('main tr').filter({ hasText: current })).toHaveCount(0);
    expect((await routed((origin) => cells.get(`${origin}${database(id)}`))).status(), 'TerraBase no longer has the database').toBe(404);
  } finally {
    await fresh.close();
  }
}

async function badges(page: Page): Promise<string[]> {
  return (await page.locator('.ts-db-head .ts-db-region').evaluateAll((chips) => chips.map((chip) => chip.getAttribute('data-region') ?? ''))).sort();
}

async function inSaoPaulo(cells: Cells): Promise<Database> {
  const created = await json<Database>(await cells.post(`${home}${project()}/databases`, { data: { name: name('ui-regions'), regions: ['gru'] } }), 201);
  expect(created.regions).toEqual(['gru']);
  return created;
}

function region(page: Page, map: string, code: string) {
  return page.locator(`#${map} input[name="regions"][value="${code}" i]`);
}

async function save(page: Page): Promise<number> {
  const answer = page.waitForResponse((response) => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/v1/dashboard/databases/placement');
  await page.getByRole('button', { name: 'Save regions', exact: true }).click();
  const status = (await answer).status();
  await page.waitForLoadState('load');
  return status;
}

async function saveUntilDone(page: Page, alert: string | null, label: string): Promise<number[]> {
  const statuses: number[] = [];
  await expect.poll(async () => {
    const status = await save(page);
    statuses.push(status);
    expect([303, 202], `${label}: 303 saved or 202 still copying`).toContain(status);
    if (status === 202 && alert !== null) await expect(page.locator('#regions-pending')).toHaveText(alert);
    return status;
  }, { message: `${label} finishes`, timeout: 90_000, intervals: [2_000] }).toBe(303);
  console.log(`${label} statuses: ${JSON.stringify(statuses)}`);
  await expect(page.locator('#regions-pending')).toHaveCount(0);
  return statuses;
}

test('D1 the new database dialog shows both regions and creates a database in both', async ({ page, cells }) => {
  await page.goto('/databases');
  await page.getByRole('button', { name: 'New database', exact: true }).first().click();
  await expect(page.locator('#create-database[open]')).toBeVisible();
  await expect(page.locator('#create-database-regions .ts-region-callout[data-region="gru"]')).toContainText('São Paulo');
  await expect(page.locator('#create-database-regions .ts-region-callout[data-region="fra"]')).toContainText('Frankfurt');
  await expect(region(page, 'create-database-regions', 'gru')).toBeChecked();
  await region(page, 'create-database-regions', 'fra').check();
  await page.locator('#database-name').fill(name('ui-both'));
  await page.locator('#create-database form').getByRole('button', { name: 'Create database', exact: true }).click();
  await page.waitForURL(/\/databases\/[0-9A-HJKMNP-TV-Z]{26}(\?|$)/);
  const id = new URL(page.url()).pathname.split('/')[2];
  try {
    expect(await stored(cells, id)).toEqual(['fra', 'gru']);
    await page.goto(`/databases/${id}/replication`);
    await expect(region(page, 'database-regions', 'gru')).toBeChecked();
    await expect(region(page, 'database-regions', 'fra')).toBeChecked();
  } finally {
    await remove(cells, id);
  }
});

test('D2 D3 add Frankfurt, then drop São Paulo and read an earlier row through Frankfurt', async ({ page, cells }) => {
  const created = await inSaoPaulo(cells);
  try {
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)');
    await sql(cells, home, created.id, 'INSERT INTO placement_rows VALUES (1)');
    await page.goto(`/databases/${created.id}/replication`);
    await expect(region(page, 'database-regions', 'gru')).toBeChecked();
    await region(page, 'database-regions', 'fra').check();
    expect(await save(page), 'adding a region saves at once').toBe(303);
    expect(await stored(cells, created.id)).toEqual(['fra', 'gru']);
    await expect(region(page, 'database-regions', 'fra')).toBeChecked();

    await region(page, 'database-regions', 'gru').uncheck();
    await saveUntilDone(page, null, 'D3 drop São Paulo');
    await expect(region(page, 'database-regions', 'fra')).toBeChecked();
    await expect(region(page, 'database-regions', 'gru')).not.toBeChecked();
    await settled(cells, created.id);
    expect((await sql(cells, replica, created.id, 'SELECT value FROM placement_rows ORDER BY value')).results[0].rows).toEqual([[1]]);
    await deleteFromStaleDangerPage(page, cells, created.id, await renameInDashboard(page, cells, created.id));
  } finally {
    await remove(cells, created.id);
  }
});

test('D4 replace São Paulo with Frankfurt in one save', async ({ page, cells }) => {
  const created = await inSaoPaulo(cells);
  try {
    await sql(cells, home, created.id, 'CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)');
    await sql(cells, home, created.id, 'INSERT INTO placement_rows VALUES (1)');
    await page.goto(`/databases/${created.id}/replication`);
    await region(page, 'database-regions', 'fra').check();
    await region(page, 'database-regions', 'gru').uncheck();
    expect(await save(page), 'the first save adds Frankfurt and answers that it is still copying').toBe(202);
    await expect(page.locator('#regions-pending')).toHaveText(copying);
    expect(await badges(page), 'the header shows the stored regions').toEqual(['fra', 'gru']);
    await expect(region(page, 'database-regions', 'fra')).toBeChecked();
    await expect(region(page, 'database-regions', 'gru')).not.toBeChecked();
    expect(await stored(cells, created.id)).toEqual(['fra', 'gru']);
    await saveUntilDone(page, stillCopying, 'D4 save again');
    await expect(region(page, 'database-regions', 'fra')).toBeChecked();
    await expect(region(page, 'database-regions', 'gru')).not.toBeChecked();
    await settled(cells, created.id);
    expect((await sql(cells, replica, created.id, 'SELECT value FROM placement_rows ORDER BY value')).results[0].rows).toEqual([[1]]);
    await deleteFromStaleDangerPage(page, cells, created.id, await renameInDashboard(page, cells, created.id));
  } finally {
    await remove(cells, created.id);
  }
});

test('D5 the last region cannot be unchecked in the browser', async ({ page, cells }) => {
  const created = await inSaoPaulo(cells);
  try {
    await page.goto(`/databases/${created.id}/replication`);
    const only = region(page, 'database-regions', 'gru');
    await expect(only).toBeChecked();
    await only.press('Space');
    await expect(only).toBeChecked();
    await expect(page.locator('#database-regions').getByRole('status')).toHaveText('A database needs one region.');
  } finally {
    await remove(cells, created.id);
  }
});
