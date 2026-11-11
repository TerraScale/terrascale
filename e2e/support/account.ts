import type { Browser, Page, TestInfo } from '@playwright/test';
import { Audit, auditedContext, expect } from './audit';
import { confirmWithPassword, onHosted } from './hosted';
import { enterSecret } from './secret';
import { auth, dash, desktop } from './stack';
import { journey, saveCookies } from './state';

export async function deleteAccount(page: Page, audit: Audit, browser: Browser, info: TestInfo): Promise<void> {
  const current = journey();
  await page.goto('/profile?tab=danger');
  await audit.check('profile danger zone');
  await page.locator('main').getByRole('button', { name: 'Delete account', exact: true }).click();
  const dialog = page.locator('#delete-account');
  await expect(dialog).toBeVisible();
  await dialog.locator('#delete-account-email').fill(current.email);
  await audit.check('delete account dialog');
  await dialog.getByRole('button', { name: 'Delete account', exact: true }).click();
  await confirmWithPassword(page, audit, current.password, 'delete account');
  await page.waitForURL((url) => url.origin === dash && url.pathname === '/account-deleted');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Account deleted');
  await audit.check('account deleted');

  const fresh = await auditedContext(browser, { viewport: desktop });
  const again = await fresh.newPage();
  const refusal = new Audit(again, info);
  refusal.expectStatus(/^https:\/\/auth\.terrascale\.tech\/ui\/terrascale\/sign-in\/password$/, 401);
  await again.goto(`${dash}/login`);
  await again.waitForURL(onHosted('sign-in'));
  await again.locator('#sign-in-email').fill(current.email);
  await again.getByRole('button', { name: 'Use a password instead' }).click();
  await enterSecret(again.locator('#sign-in-password'), current.password);
  await again.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(again.locator('#sign-in-error')).toBeVisible();
  expect(new URL(again.url()).origin, 'the deleted account stays on the hosted sign-in').toBe(auth);
  await refusal.check('sign-in refused');
  await refusal.finish();
  await saveCookies(fresh);
  await fresh.close();
}
