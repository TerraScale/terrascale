import { deleteAccount } from '../support/account';
import { test, expect } from '../support/audit';
import { setupFinished } from '../support/state';

test('delete the placement account after its temporary databases and buckets', async ({ page, audit, browser }, info) => {
  expect(setupFinished(info), 'setup did not finish').toBe(true);
  await deleteAccount(page, audit, browser, info);
});
