import { test, expect } from '../support/audit';

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
});

test('a rejected account deletion keeps its dialog and typed email with an error', async ({ page, audit }) => {
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
});
