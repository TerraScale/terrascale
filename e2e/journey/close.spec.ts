import { deleteAccount } from '../support/account';
import { test, expect } from '../support/audit';
import { setupFinished } from '../support/state';

test.beforeAll(({}, info) => {
  expect(setupFinished(info), 'setup did not finish').toBe(true);
});

test('the account is deleted through the profile danger tab and a new sign-in is refused', async ({ page, audit, browser }, info) => {
  await deleteAccount(page, audit, browser, info);
});
