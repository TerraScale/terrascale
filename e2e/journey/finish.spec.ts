import { deleteAccount } from '../support/account';
import { test, expect } from '../support/audit';
import { completeUpload, deleteFile, leftFiles, putReserved, reserveUpload, stored, type Reservation } from '../support/files';
import { runOperations, sdks } from '../support/sdk';
import { shell } from '../support/shell';
import { onLocalStack } from '../support/stack';
import { journey, setupFinished } from '../support/state';

test.describe.configure({ mode: 'default' });

test.beforeAll(({}, info) => {
  expect(setupFinished(info), 'setup did not finish').toBe(true);
});

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

  test('the account is deleted through the profile danger tab, a new sign-in is refused and its files leave the object store', async ({ page, audit, browser }, info) => {
    const content = Buffer.from(`held ${journey().runId}`);
    const files: { live: string; held: Reservation; late: Reservation }[] = [];
    await onLocalStack(async () => {
      const left = leftFiles();
      await page.goto(`/storage?bucket=${left.bucket}`);
      const held = await reserveUpload(page, left.bucket, `held-${journey().runId}.txt`, 'text/plain', content);
      expect(await putReserved(held, content), 'the file to delete is uploaded').toBe(200);
      expect(await completeUpload(page, left.bucket, held.id), 'the file to delete is completed').toBe(204);
      expect(await deleteFile(page, left.bucket, held.id), 'the file is deleted just before the account').toBe(303);
      const late = await reserveUpload(page, left.bucket, `late-${journey().runId}.txt`, 'text/plain', content);
      expect([await stored(left.live), await stored(held.path), await stored(late.path)], 'the bucket holds a live file and a just-deleted file, and nothing for the reserved upload').toEqual([true, true, false]);
      files.push({ live: left.live, held, late });
    });
    await deleteAccount(page, audit, browser, info);
    for (const { live, held, late } of files) {
      expect([await stored(live), await stored(held.path)], 'the deleted account left no file in the object store').toEqual([false, false]);
      expect(await putReserved(late, content), 'an upload link issued before the deletion still takes its file').toBe(200);
      await expect.poll(() => stored(late.path), { message: 'a file uploaded after the deletion is erased again' }).toBe(false);
    }
  });
});
