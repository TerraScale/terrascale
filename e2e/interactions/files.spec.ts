import { createHash, randomBytes } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import type { Page } from '@playwright/test';
import { test, expect } from '../support/audit';
import { production } from '../support/stack';
import { journey } from '../support/state';

const unique = (base: string) => `${base}-${journey().runId}${Date.now().toString(36)}`;
const bucketId = /[?&]bucket=([0-9A-HJKMNP-TV-Z]{26})(&|$)/;

test.skip(production, "the browser may reach only the stack's own host names on production, never the object store");

async function createBucket(page: Page, name: string): Promise<string> {
  await page.goto('/storage');
  await page.getByRole('button', { name: 'Create bucket', exact: true }).first().click();
  const dialog = page.locator('dialog#create-bucket');
  await expect(dialog).toBeVisible();
  await dialog.locator('#bucket-name').fill(name);
  await dialog.getByRole('button', { name: 'Create bucket', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1, name, exact: true })).toBeVisible();
  return bucketId.exec(page.url())?.[1] ?? '';
}

async function upload(page: Page, name: string, mimeType: string, buffer: Buffer): Promise<void> {
  await page.locator('[data-upload-input]').setInputFiles({ name, mimeType, buffer });
  await expect(page.locator('.ts-files-item').filter({ hasText: name })).toHaveAttribute('data-state', 'done');
  await expect(page.locator('#bucket-files tr').filter({ hasText: name })).toBeVisible();
}

test('a bucket is created, takes an upload, returns the same bytes, is emptied, and refuses deletion while the deleted file is held', async ({ page, audit }) => {
  const name = unique('files');
  await page.goto('/storage');
  await audit.check('files');
  await page.getByRole('button', { name: 'Create bucket', exact: true }).first().click();
  await expect(page.locator('dialog#create-bucket')).toBeVisible();
  await audit.check('create bucket dialog');
  await page.locator('dialog#create-bucket #bucket-name').fill(name);
  await page.locator('dialog#create-bucket').getByRole('button', { name: 'Create bucket', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1, name, exact: true })).toBeVisible();
  await expect(page.locator('#bucket-files')).toContainText('No files yet.');
  await audit.check('empty bucket');

  const file = `report-${journey().runId}.pdf`;
  const bytes = randomBytes(96 * 1024);
  await upload(page, file, 'application/pdf', bytes);
  const row = page.locator('#bucket-files tr').filter({ hasText: file });
  await expect(row).toContainText('98.3 kB');
  await expect(row).toContainText('application/pdf');
  await audit.check('bucket with a file');

  const downloading = page.waitForEvent('download');
  await row.getByRole('button', { name: `Download ${file}`, exact: true }).click();
  const download = await downloading;
  expect(download.suggestedFilename(), 'the download keeps the file name').toBe(file);
  const received = await readFile(await download.path());
  expect(createHash('sha256').update(received).digest('hex'), 'the downloaded bytes are the uploaded bytes').toBe(createHash('sha256').update(bytes).digest('hex'));

  await row.getByRole('button', { name: `Delete ${file}`, exact: true }).click();
  const deleteFile = page.locator('dialog[open][id^="delete-file-"]');
  await expect(deleteFile).toBeVisible();
  await audit.check('delete file dialog');
  await deleteFile.getByRole('button', { name: 'Delete file', exact: true }).click();
  await expect(page.locator('#bucket-files')).toContainText('No files yet.');

  await page.getByRole('navigation', { name: 'Bucket sections' }).getByRole('link', { name: 'Settings', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Access rules', exact: true })).toBeVisible();
  await audit.check('bucket settings');
  audit.expectStatus(/\/api\/v1\/dashboard\/storage\/delete-bucket/, 409);
  await page.getByRole('button', { name: 'Delete bucket', exact: true }).click();
  const deleteBucket = page.locator('dialog#delete-bucket');
  await expect(deleteBucket).toBeVisible();
  await deleteBucket.locator('#delete-bucket-name').fill(name);
  await deleteBucket.getByRole('button', { name: 'Delete bucket', exact: true }).click();
  await expect(deleteBucket.getByRole('alert'), 'a bucket whose file was just deleted is refused inside the dialog').toContainText('deleted in the last 6 minutes');
  await expect(deleteBucket.locator('#delete-bucket-name'), 'the typed name is kept').toHaveValue(name);
  await audit.check('delete bucket refused while its deleted file is held');
});

test('an upload or delete that names a bucket the file is not in is refused and changes nothing', async ({ page }) => {
  const keptName = unique('kept');
  const kept = await createBucket(page, keptName);
  const file = `kept-${journey().runId}.txt`;
  await upload(page, file, 'text/plain', Buffer.from('kept'));
  const fileId = (await page.locator('#bucket-files tr').filter({ hasText: file }).getAttribute('data-file-id')) ?? '';
  const otherName = unique('other');
  const other = await createBucket(page, otherName);
  const csrf = await page.locator('input[name=_csrf]').first().inputValue();
  const post = (path: string, form: Record<string, string>) => page.request.post(path, { headers: { Accept: 'application/json' }, form: { _csrf: csrf, ...form }, maxRedirects: 0 });
  const absent = `01${randomBytes(12).toString('hex').toUpperCase()}`;
  const content = Buffer.from('forged');
  const sha256 = createHash('sha256').update(content).digest('base64');

  const forgedUpload = await post('/api/v1/dashboard/storage/upload', { bucket: absent, metadata: '{}', name: 'forged.txt', contentType: 'text/plain', size: String(content.length), sha256 });
  expect(forgedUpload.status(), 'an upload to a bucket outside the project').toBe(404);
  const forgedDelete = await post('/api/v1/dashboard/storage/delete', { bucket: other, fileId });
  expect(forgedDelete.status(), 'a delete that names another bucket than the file is in').toBe(404);
  await page.goto(`/storage?bucket=${kept}`);
  await expect(page.locator('#bucket-files tr').filter({ hasText: file }), 'the file is still there').toBeVisible();

  expect((await post('/api/v1/dashboard/storage/delete', { bucket: kept, fileId })).status(), 'the file is deleted from its own bucket').toBe(303);
  await page.goto(`/storage?bucket=${other}&tab=settings`);
  await page.getByRole('button', { name: 'Delete bucket', exact: true }).click();
  await page.locator('#delete-bucket-name').fill(otherName);
  await page.locator('dialog#delete-bucket').getByRole('button', { name: 'Delete bucket', exact: true }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Files', exact: true })).toBeVisible();
  await expect(page.locator('main').getByRole('link', { name: otherName, exact: true }), 'the empty bucket is deleted').toHaveCount(0);
  await expect(page.locator('main').getByRole('link', { name: keptName, exact: true })).toBeVisible();
});
