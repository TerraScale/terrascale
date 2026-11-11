import { deleteAccount } from '../support/account';
import { test, expect } from '../support/audit';
import { journey, setupFinished } from '../support/state';

test.beforeAll(({}, info) => {
  expect(setupFinished(info), 'setup did not finish').toBe(true);
});

test('the account is deleted through the profile danger tab and a new sign-in is refused', async ({ page, audit, browser }, info) => {
  await page.goto('/databases');
  await expect(page.locator('main tr').filter({ hasText: journey().databaseName }), 'the account still holds its database').toHaveCount(1);
  const pending: string[] = [];
  page.on('framenavigated', (frame) => {
    if (frame === page.mainFrame() && new URL(frame.url()).pathname === '/account-deletion') pending.push(frame.url());
  });
  await deleteAccount(page, audit, browser, info);
  expect(pending, 'the account and its database are erased in the deletion request, without the pending page').toEqual([]);
});
