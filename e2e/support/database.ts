import { expect, type Locator, type Page } from '@playwright/test';

export async function waitForCreatedDatabase(page: Page, name: string, databasesUrl: string): Promise<{ id: string; link: Locator }> {
  const row = page.locator('#project-databases tbody tr').filter({ hasText: name });
  await expect(row).toBeVisible();
  await expect(row.locator('.ts-badge')).toContainText(/ready|provisioning|accepted|pending/);
  await expect(page.locator('#create-database')).not.toBeVisible();
  await expect(page.locator('dialog:modal')).toHaveCount(0);
  await expect(page).toHaveURL(databasesUrl);
  await expect(page.locator('#database-create-result')).toContainText(name);
  const link = row.getByRole('link');
  await expect(link).toHaveAttribute('href', /^\/databases\/[0-9A-HJKMNP-TV-Z]{26}(\?|$)/);
  const href = await link.getAttribute('href');
  return { id: new URL(href!, page.url()).pathname.split('/')[2], link };
}
