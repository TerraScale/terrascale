import { expect, type Page } from '@playwright/test';

export async function regionsSettled(page: Page): Promise<void> {
  const card = page.locator('#home-regions');
  await expect(card).toBeVisible();
  await expect(card.locator('[aria-busy="true"]')).toHaveCount(0);
}
