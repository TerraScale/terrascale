import { expect, test } from '../support/audit';
import { journey } from '../support/state';
import { createDedicatedProjectOwner } from '../support/project-owner';
import { createDatabase } from '../support/paid';

test('database settings and filters save without navigating', async ({ browser }, info) => {
  const account = await createDedicatedProjectOwner(browser, info, 'Database filters owner');
  const { current, page, audit } = account;
  try {
    const original = `settings-${current.runId}`;
    const databaseId = await createDatabase(page, audit, original, journey().databaseRegion, 'settings database');
    const renamed = `saved-${current.runId}`;
    const path = `/databases/${databaseId}`;
    await page.goto(path + '/settings');
    const settingsUrl = page.url();
    await page.locator('html').evaluate(el => el.setAttribute('data-in-place', 'settings'));
    await page.locator('#rename-name').fill(renamed);
    await page.locator('#rename-confirm').fill('different-name');
    await page.locator('#database-general').getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.locator('#rename-error')).toContainText(/confirm/i);
    await expect(page.locator('#rename-name')).toHaveValue(renamed);
    await expect(page.locator('.ts-db-title')).toHaveText(original);
    await expect(page).toHaveURL(settingsUrl);
    await expect(page.locator('html')).toHaveAttribute('data-in-place', 'settings');
    const expiry = page.locator('#database-expiry');
    await expiry.locator('label.ts-chip').filter({ hasText: /^1 day$/ }).click();
    await expect(expiry.getByRole('radio', { name: '1 day', exact: true })).toBeChecked();
    await expiry.getByRole('button', { name: 'Save expiry', exact: true }).click();
    await expect(expiry.getByRole('radio', { name: '1 day', exact: true })).toBeChecked();
    await expect(page.locator('#expiry-saved')).toBeVisible();
    await expect(page).toHaveURL(settingsUrl);
    await expiry.locator('label.ts-chip').filter({ hasText: /^Never$/ }).click();
    await expect(expiry.getByRole('radio', { name: 'Never', exact: true })).toBeChecked();
    await expiry.getByRole('button', { name: 'Save expiry', exact: true }).click();
    await expect(page.locator('#expiry-current')).toHaveText('Never');
    await expect(page).toHaveURL(settingsUrl);
    await expect(page.locator('html')).toHaveAttribute('data-in-place', 'settings');
    await audit.check('settings saved in place');

    await page.goto(path + '/tables');
    await page.getByRole('button', { name: 'New table', exact: true }).first().click();
    await page.locator('#create-table-name').fill('records');
    await page.locator('#create-table').getByRole('button', { name: 'Create table', exact: true }).click();
    await expect(page.locator('[data-table="records"]')).toBeVisible();

    await page.goto(path + '/replication');
    const replicationUrl = page.url();
    await page.locator('html').evaluate(el => el.setAttribute('data-in-place', 'replication'));
    const placed = page.locator('#database-regions input[name="regions"]:checked');
    const region = await placed.inputValue();
    await page.getByRole('button', { name: 'Save regions', exact: true }).click();
    await expect(page.locator('#regions-saved')).toBeVisible();
    await expect(placed).toHaveValue(region);
    await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toHaveCount(0);
    await expect(page.locator('#replication')).toContainText('This database runs in one region.');
    await expect(page).toHaveURL(replicationUrl);
    await expect(page.locator('html')).toHaveAttribute('data-in-place', 'replication');

    const conflictsLive = page.waitForResponse(response => new URL(response.url()).pathname === path + '/conflicts/live');
    await page.goto(path + '/conflicts');
    expect((await conflictsLive).status()).toBe(200);
    await page.locator('html').evaluate(el => el.setAttribute('data-in-place', 'filters'));
    await page.locator('#conflict-table').fill('records');
    await page.locator('#conflict-table').press('Enter');
    await expect(page).toHaveURL(url => url.pathname === path + '/conflicts' && url.searchParams.get('table') === 'records' && url.searchParams.get('organizationId') === current.organizationId && url.searchParams.get('projectId') === current.projectId);
    await expect(page.locator('#conflicts')).toContainText('No conflicts match these filters.');
    await expect(page.locator('html')).toHaveAttribute('data-in-place', 'filters');
    await audit.check('conflict filters applied in place');
    const logsLive = page.waitForResponse(response => new URL(response.url()).pathname === path + '/logs/live');
    await page.goto(path + '/logs');
    expect((await logsLive).status()).toBe(200);
    await page.locator('html').evaluate(el => el.setAttribute('data-in-place', 'logs'));
    await page.locator('#request-status').selectOption('5xx');
    await expect(page).toHaveURL(url => url.pathname === path + '/logs' && url.searchParams.get('status') === '5xx' && url.searchParams.get('organizationId') === current.organizationId && url.searchParams.get('projectId') === current.projectId);
    await expect(page.locator('#request-status')).toHaveValue('5xx');
    await expect(page.locator('#request-logs')).toContainText('No recorded requests match this filter');
    await expect(page.locator('html')).toHaveAttribute('data-in-place', 'logs');
    await audit.check('database filters applied in place');
  } finally {
    await account.remove();
  }
});
