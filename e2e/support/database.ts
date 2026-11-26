import { expect, type Locator, type Page } from '@playwright/test';

async function databaseLink(page: Page, row: Locator): Promise<{ id: string; link: Locator }> {
  const link = row.getByRole('link');
  await expect(link).toHaveAttribute('href', /^\/databases\/[0-9A-HJKMNP-TV-Z]{26}(\?|$)/);
  const href = await link.getAttribute('href');
  return { id: new URL(href!, page.url()).pathname.split('/')[2], link };
}

export async function waitForCreatedDatabase(page: Page, name: string, databasesUrl: string): Promise<{ id: string; link: Locator }> {
  const row = page.locator('#project-databases tbody tr').filter({ hasText: name });
  await expect(row).toBeVisible();
  await expect(row.locator('.ts-badge')).toContainText(/ready|provisioning|accepted|pending/);
  await expect(page.locator('#create-database')).not.toBeVisible();
  await expect(page.locator('dialog:modal')).toHaveCount(0);
  await expect(page).toHaveURL(databasesUrl);
  await expect(page.locator('#database-create-result')).toContainText(name);
  return databaseLink(page, row);
}

export async function listedDatabase(page: Page, name: string): Promise<{ id: string; link: Locator }> {
  await page.goto('/databases');
  const row = page.locator('#project-databases tbody tr').filter({ hasText: name });
  await expect(row).toBeVisible();
  return databaseLink(page, row);
}

export async function deleteDatabase(page: Page, id: string, name: string): Promise<void> {
  await page.goto(`/databases/${id}/danger`);
  await page.locator('main').getByRole('button', { name: 'Delete database', exact: true }).click();
  const dialog = page.locator('dialog[open]');
  await dialog.locator('input[name="confirmName"]').fill(name);
  await dialog.getByRole('button', { name: 'Delete database', exact: true }).click();
  await page.waitForURL(url => url.pathname === '/databases');
  await expect(page.locator('main tr').filter({ hasText: name })).toHaveCount(0);
}
