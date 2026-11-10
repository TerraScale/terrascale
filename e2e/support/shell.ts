import { expect, type Page } from '@playwright/test';
import type { Journey } from './state';

export async function shell(page: Page, journey: Pick<Journey, 'email' | 'displayName' | 'organizationId' | 'organizationName' | 'projectId' | 'projectName'>): Promise<void> {
  await expect(page.locator('.ts-account-trigger .ts-account-name')).toHaveText(journey.displayName);
  await expect(page.locator('.ts-account-trigger .ts-account-email')).toHaveText(journey.email);
  await expect(page.locator(`[data-active-organization-id="${journey.organizationId}"]`)).toHaveText(`Current organization: ${journey.organizationName}`);
  await expect(page.locator(`[data-active-project-id="${journey.projectId}"]`)).toHaveText(`Current project: ${journey.projectName}`);
}
