import { test, expect } from '../support/audit';
import { onLocalStack } from '../support/stack';
import { journey } from '../support/state';

test('a short request ID shows inline validation and blocks submission without loading the button', async ({ page, audit }) => {
  await page.goto('/support');
  const field = page.locator('#support-related-request');
  await field.fill('0');
  await field.press('Tab');
  const message = page.locator('.ts-validation-message').filter({ hasText: 'Use at least 26 characters' });
  await expect(message).toBeVisible();
  await expect(field).toHaveAttribute('aria-invalid', 'true');
  await expect(field).toHaveAttribute('aria-describedby', await message.getAttribute('id') as string);
  await page.locator('#support-subject').fill('Validation request');
  await page.locator('#support-message').fill('Please check the request ID.');
  const submissions: string[] = [];
  page.on('request', request => { if (request.method() === 'POST') submissions.push(request.url()); });
  const button = page.locator('form:has(#support-related-request) button[type="submit"]');
  await button.click();
  await expect(field).toBeFocused();
  await expect(button).not.toHaveAttribute('aria-busy', 'true');
  await expect(page.locator('body')).not.toHaveClass(/ts-nav-pending/);
  await audit.check('inline request ID validation');
  expect(submissions).toEqual([]);
  await field.fill('01JBX8Y3Z4N5P6Q7R8S9T0V1W2');
  await expect(message).toBeHidden();
  await expect(field).not.toHaveAttribute('aria-invalid');
  await onLocalStack(async () => {
    const before = page.url();
    const requestId = await page.locator('input[name="requestId"]').inputValue();
    const main = await page.locator('main').elementHandle();
    await page.locator('#support-message').fill('   ');
    const rejected = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/dashboard/support' && response.request().method() === 'POST');
    await button.click();
    expect((await rejected).status()).toBe(200);
    await expect(page.getByRole('alert')).toContainText('Describe the problem in at most 8000 characters.');
    await expect(page.locator('#support-subject')).toHaveValue('Validation request');
    await expect(page.locator('#support-message')).toHaveValue('   ');
    await expect(field).toHaveValue('01JBX8Y3Z4N5P6Q7R8S9T0V1W2');
    await expect(page.locator('input[name="requestId"]')).toHaveValue(requestId);
    await audit.check('support request error keeps the draft');
    await page.locator('#support-message').fill('Please check the request ID.');
    await page.locator('#support-severity').selectOption('high');
    const sent = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/dashboard/support' && response.request().method() === 'POST');
    await button.click();
    const response = await sent;
    expect(response.status()).toBe(200);
    expect(response.request().headers()['datastar-request']).toBe('true');
    await expect(page.locator('#support-sent')).toContainText(requestId.slice(-8));
    await expect(page).toHaveURL(before);
    expect(await main!.evaluate(element => element.isConnected)).toBe(true);
    await expect(page.locator('#support-subject')).toHaveValue('');
    await expect(page.locator('#support-message')).toHaveValue('');
    await expect(page.locator('#support-related-request')).toHaveValue('');
    await expect(page.locator('#support-severity')).toHaveValue('normal');
    await expect(page.locator('input[name="requestId"]')).not.toHaveValue(requestId);
    await audit.check('support request sent in place');
    await main!.dispose();
  });
});

test('validated dialog errors keep drafts and expired forms reject drafts', async ({ page, audit }) => {
  await page.goto('/databases?range=7d');
  const databasesUrl = page.url();
  await page.getByRole('button', { name: 'New database', exact: true }).first().click();
  const create = page.locator('#create-database');
  await create.locator('#database-name').fill(journey().databaseName);
  const failed = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/dashboard/databases' && response.request().method() === 'POST');
  await create.getByRole('button', { name: 'Create database', exact: true }).click();
  const response = await failed;
  expect(response.status()).toBe(200);
  expect(response.request().headers()['datastar-request']).toBe('true');
  await expect(create).toBeVisible();
  await expect(page.locator('dialog:modal')).toHaveAttribute('id', 'create-database');
  await expect(create.getByRole('alert')).toContainText('Database not created');
  await expect(create.locator('#database-name')).toHaveValue(journey().databaseName);
  await expect(create.locator('form')).not.toHaveAttribute('inert');
  await expect(page).toHaveURL(databasesUrl);
  await audit.check('database creation error stays inside dialog');
  await create.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.locator('dialog:modal')).toHaveCount(0);
  await page.locator('#project-databases tbody tr').filter({ hasText: journey().databaseName }).getByRole('link').click();
  await expect(page.locator('.ts-db-title')).toHaveText(journey().databaseName);

  await page.goto('/profile?tab=danger');
  await page.getByRole('button', { name: 'Delete account', exact: true }).click();
  const dialog = page.locator('#delete-account');
  await dialog.locator('#delete-account-email').fill('different@example.com');
  audit.expectStatus(/\/api\/v1\/dashboard\/profile\/delete(?:\?|$)/, 422);
  await dialog.getByRole('button', { name: 'Delete account', exact: true }).click();
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('alert')).toContainText('Type your account email to confirm deletion.');
  await expect(dialog.locator('#delete-account-email')).toHaveValue('different@example.com');
  await audit.check('account deletion error inside dialog');
  await Promise.all(['/profile/delete', '/profile/sign-out-everywhere', '/profile/access-tokens/invalid/revoke',
    '/project/grants', '/data/sql', '/backups/policy', '/log-drain', '/organization/sso'].map(async action => {
    const response = await page.request.post(`/api/v1/dashboard${action}`, {
      headers: { Accept: 'text/html' },
      form: { _csrf: 'expired', email: 'untrusted@example.com', userId: 'untrusted-user', role: 'admin', sql: 'SELECT untrusted_statement' },
    });
    expect(response.status(), action).toBe(400);
    const html = await response.text();
    expect(html).toContain('Nothing was changed. Open the form again to send it.');
    expect(html).not.toContain('untrusted');
    expect(html).not.toContain('name="_csrf"');
    expect(html).not.toContain('<dialog');
  }));
});
