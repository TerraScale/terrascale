import { test, expect } from '../support/audit';
import { runOperations, sdks } from '../support/sdk';
import { shell } from '../support/shell';
import { journey } from '../support/state';

test.describe.configure({ mode: 'default' });

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
