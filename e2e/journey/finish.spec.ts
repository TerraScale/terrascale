import { Audit, auditedContext, test, expect } from '../support/audit';
import { confirmWithPassword, onHosted } from '../support/hosted';
import { runOperations, sdks } from '../support/sdk';
import { shell } from '../support/shell';
import { auth, dash, desktop } from '../support/stack';
import { journey } from '../support/state';
import { enterSecret } from '../support/secret';

test.describe.configure({ mode: 'default' });

test.describe('SDK group A', () => {
  for (const sdk of sdks) {
    test(`${sdk} SDK: SQL, values, sessions, conflicts and ids on the journey database`, async () => {
      await runOperations(sdk, 'a');
    });
  }
});

test.describe('database deletion', () => {
  test('delete the journey database on its danger page by typing its name', async ({ page, audit }) => {
    const current = journey();
    await page.goto(`/databases/${current.databaseId}/danger`);
    await shell(page, current);
    await audit.check('database danger zone');

    await page.locator('main').getByRole('button', { name: 'Delete database', exact: true }).click();
    const dialog = page.locator('dialog[open]');
    await expect(dialog).toBeVisible();
    const confirm = dialog.locator('input[name="confirmName"]');
    const submit = dialog.getByRole('button', { name: 'Delete database', exact: true });
    await confirm.fill(current.databaseName);
    await audit.check('delete database dialog');
    await submit.click();

    await page.waitForURL((url) => url.pathname === '/databases');
    await expect(page.locator('main tr').filter({ hasText: current.databaseName })).toHaveCount(0);
    await audit.check('databases after deletion');
  });
});

test.describe('SDK group B', () => {
  for (const sdk of sdks) {
    test(`${sdk} SDK: agent databases with TTL and scoped keys`, async () => {
      await runOperations(sdk, 'b');
    });
  }
});

test.describe('teardown', () => {
  test('a new display name shows in the shell', async ({ page, audit }) => {
    const current = journey();
    const name = `Renamed ${current.runId}`;
    await page.goto('/profile');
    await page.locator('#displayName').fill(name);
    await page.locator('form[action^="/api/v1/dashboard/profile"]').getByRole('button', { name: 'Save profile', exact: true }).click();
    await page.waitForLoadState('load');
    await shell(page, { ...current, displayName: name });
    await audit.check('profile renamed');
  });

  test('the account is deleted through the profile danger tab and a new sign-in is refused', async ({ page, audit, browser }, info) => {
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
    await fresh.close();
  });
});
