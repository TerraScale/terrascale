import { randomBytes } from 'node:crypto';
import { expect, test } from '../support/audit';
import { auth } from '../support/stack';
import { journey, saveSecret } from '../support/state';

test('table browser follows API writes without reloading or losing a table draft', async ({ page, request, audit }) => {
  const current = journey();
  const table = 'live_rows_' + randomBytes(4).toString('hex');
  const tokenResponse = await request.post(auth + '/terrascale/oauth/token', {
    form: {
      grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey,
      project: current.projectId, scope: 'sql:read sql:write',
    },
  });
  expect(tokenResponse.status()).toBe(200);
  const token = (await tokenResponse.json()).access_token as string;
  saveSecret('live-rows-token', token);
  const execute = async (sql: string) => {
    const response = await request.post(`https://api.terrascale.tech/v1/projects/${current.projectId}/databases/${current.databaseId}/sql/execute`, {
      headers: { authorization: 'Bearer ' + token }, data: { statements: [{ sql }] },
    });
    expect(response.status(), sql).toBe(200);
  };
  const path = `/databases/${current.databaseId}/tables?table=${table}&organizationId=${current.organizationId}&projectId=${current.projectId}`;
  await execute(`CREATE TABLE ${table} (id INTEGER PRIMARY KEY, value TEXT NOT NULL)`);
  try {
    await page.goto(path);
    const viewUrl = page.url();
    await page.locator('html').evaluate(el => el.setAttribute('data-in-place', 'live-rows'));
    await execute(`CREATE TABLE ${table}_catalog (id INTEGER PRIMARY KEY)`);
    await expect(page.locator(`[data-table="${table}_catalog"]`)).toBeVisible();
    await expect(page.locator(`[data-table="${table}"]`)).toBeVisible();
    await expect(page.locator('#table-columns-title')).toHaveText(table);
    await execute(`INSERT INTO ${table} VALUES (1, 'arrived-live')`);
    await expect(page.locator('#table-rows tbody')).toContainText('arrived-live');
    await page.getByRole('button', { name: 'New table', exact: true }).first().click();
    await page.locator('#create-table-name').fill('unsaved_live_draft');
    await execute(`UPDATE ${table} SET value = 'updated-live' WHERE id = 1`);
    await expect(page.locator('#table-rows tbody')).toContainText('updated-live');
    await expect(page.locator('#create-table-name')).toHaveValue('unsaved_live_draft');
    await expect(page.locator('#create-table')).toBeVisible();
    await page.locator('#create-table').getByRole('button', { name: 'Cancel', exact: true }).click();
    await execute(`DELETE FROM ${table} WHERE id = 1`);
    await expect(page.locator('#table-rows')).toContainText('No rows yet.');
    await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toHaveCount(0);
    await expect(page).toHaveURL(viewUrl);
    await expect(page.locator('html')).toHaveAttribute('data-in-place', 'live-rows');
    await audit.check('API insert, update and delete arrive in the open rows browser');
  } finally {
    await execute(`DROP TABLE ${table}`);
    await execute(`DROP TABLE ${table}_catalog`);
  }
  await expect(page.locator(`[data-table="${table}"]`)).toHaveCount(0);
  await expect(page.locator(`[data-table="${table}_catalog"]`)).toHaveCount(0);
});
