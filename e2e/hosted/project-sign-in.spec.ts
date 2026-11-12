import { randomBytes } from 'node:crypto';
import { promisify } from 'node:util';
import { execFile } from 'node:child_process';
import { resolve } from 'node:path';
import { test, expect, Audit, auditedContext } from '../support/audit';
import { auth, dash, desktop, onLocalStack } from '../support/stack';
import { confirmWithPassword, hostedReady, signUp, verify, onboard } from '../support/hosted';
import { newAddress, nextMail } from '../support/inbox';
import { saveCookies, saveSecret } from '../support/state';
import { enterSecret } from '../support/secret';

test('project usernames and browser-bound magic links share single-use email verification', async ({ browser }, info) => {
  const current = { runId: randomBytes(4).toString("hex") };
  const owner = await auditedContext(browser, { viewport: desktop });
  const customer = await auditedContext(browser, { viewport: desktop });
  const other = await auditedContext(browser, { viewport: desktop });
  const page = await customer.newPage();
  const audit = new Audit(page, info);
  const admin = await owner.newPage();
  const adminAudit = new Audit(admin, info);
  let projectId = "";
  const email = await newAddress(`magic-${current.runId}-${Date.now().toString(36)}`);
  const username = `user_${current.runId}`;
  const password = randomBytes(24).toString('base64url');
  saveSecret('project-sign-in-password', password);
  try {
    const ownerEmail = await newAddress(`magic-owner-${current.runId}-${Date.now().toString(36)}`);
    await signUp(admin, adminAudit, ownerEmail, password, 'project sign-in owner');
    await verify(admin, ownerEmail);
    await onboard(admin, adminAudit, 'Sign-in owner', `Sign-in ${current.runId}`, 'project sign-in owner');
    projectId = new URL(admin.url()).searchParams.get('projectId') ?? '';
    expect(projectId).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
    const base = `${auth}/ui/${projectId}`;
    await admin.goto('/auth/sign-in');
    await admin.locator('#auth-method-magic_link').check();
    await admin.locator('#auth-method-username').check();
    await admin.getByRole('button', { name: 'Save sign-in methods', exact: true }).click();
    await expect(admin.locator('#auth-method-magic_link')).toBeChecked();
    await expect(admin.locator('#auth-method-username')).toBeChecked();
    await adminAudit.check('project magic links and usernames configured');
    await page.goto(`${base}/sign-up`);
    await page.locator('#sign-up-email').fill(email);
    await page.locator('#sign-up-username').fill(username);
    await enterSecret(page.locator('#sign-up-password'), password);
    await audit.check('project sign-up requests a username');
    await page.locator('form:has(#sign-up-email) button[type=submit]').click();
    const signupMail = await nextMail(email, 0);
    const signupCode = /^\s*(\d{6})\s*$/m.exec(signupMail.text)?.[1] ?? '';
    await enterSecret(page.locator('#sign-up-code-digit'), signupCode);
    await page.getByRole('button', { name: 'Verify email', exact: true }).click();
    await expect(page.locator('[data-state=success]')).toContainText('signed in');
    await audit.check('project username account verified');
    await customer.clearCookies();
    await page.goto(`${base}/sign-in`);
    await page.locator('#sign-in-email').fill(email);
    audit.expectStatus(/\/sign-in\/magic$/, 429);
    await page.getByRole('button', { name: 'Email a sign-in link', exact: true }).click();
    await expect(page.locator('button[data-wait-seconds]')).toBeDisabled();
    await expect(page.locator('#sign-in-error')).toContainText('No link was sent');
    await audit.check('magic-link mail cooldown disables resend without claiming delivery');

    await admin.goto('/auth/social');
    const providers = [
      ['microsoft', 'Microsoft'], ['gitlab', 'GitLab'], ['linkedin', 'LinkedIn'], ['slack', 'Slack'],
      ['discord', 'Discord'], ['facebook', 'Facebook'], ['x', 'X'], ['oauth2', 'Custom OAuth2'],
    ] as const;
    for (const [key, label] of providers) {
      const id = `auth-provider-${key}`;
      await admin.locator(`#${id} button[command=show-modal]`).click();
      const dialog = admin.locator(`#${id}-dialog`);
      await dialog.locator('[name=client_id]').fill('journey-client');
      await enterSecret(dialog.locator('[name=client_secret]'), password);
      if (key === 'microsoft') await dialog.locator('[name=tenant]').selectOption('organizations');
      if (key === 'oauth2') {
        for (const field of ['authorize_url', 'token_url', 'userinfo_url']) {
          await dialog.locator(`[name=${field}]`).fill(`https://identity.example.invalid/${field}`);
        }
        await dialog.locator('[name=scopes]').fill('profile email');
      }
      await dialog.getByRole('button', { name: `Save ${label}`, exact: true }).click();
      await expect(admin.locator(`#${id} .ts-badge`)).toHaveText('Configured');
      if (key === 'oauth2') {
        await admin.locator(`#${id} button[command=show-modal]`).click();
        await expect(admin.locator(`#${id}-dialog [name=userinfo_url]`)).toHaveValue('https://identity.example.invalid/userinfo_url');
        await admin.locator(`#${id}-dialog`).getByRole('button', { name: 'Cancel', exact: true }).click();
      }
      expect((await admin.content()).includes(password), 'provider secrets stay write-only').toBe(false);
    }
    await adminAudit.check('all eight providers configured with write-only secrets');

    const duplicateEmail = await newAddress(`duplicate-${current.runId}-${Date.now().toString(36)}`);
    const duplicate = await other.newPage();
    await duplicate.goto(`${base}/sign-up`);
    await duplicate.locator('#sign-up-email').fill(duplicateEmail);
    await duplicate.locator('#sign-up-username').fill(username.toUpperCase());
    await enterSecret(duplicate.locator('#sign-up-password'), password);
    await duplicate.locator('form:has(#sign-up-email) button[type=submit]').click();
    const duplicateMail = await nextMail(duplicateEmail, 0);
    const duplicateCode = /^\s*(\d{6})\s*$/m.exec(duplicateMail.text)?.[1] ?? '';
    const duplicateToken = await duplicate.locator('input[name=pending_token]').first().inputValue();
    saveSecret('duplicate-sign-up-token', duplicateToken);
    const refusedDuplicate = await other.request.post(`${base}/sign-up/verify`, {
      headers: { Origin: auth }, form: { email: duplicateEmail, code: duplicateCode, pending_token: duplicateToken },
    });
    expect(refusedDuplicate.status()).toBe(401);
    expect((await other.cookies()).some(cookie => cookie.name === `tsauth_session_${projectId}`)).toBe(false);
    await duplicate.close();

    const passwordPage = await other.newPage();
    const passwordAudit = new Audit(passwordPage, info);
    await passwordPage.goto(`${base}/sign-in`);
    for (const [key, label] of providers) {
      await expect(passwordPage.getByRole('link', { name: `Continue with ${label}`, exact: true })).toHaveAttribute('href', `/ui/${projectId}/social/${key}`);
    }
    await passwordPage.locator('#sign-in-email').fill(username.toUpperCase());
    await passwordPage.getByRole('button', { name: 'Use a password instead' }).click();
    await enterSecret(passwordPage.locator('#sign-in-password'), password);
    await passwordPage.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(passwordPage.locator('[data-state=success]')).toContainText('signed in');
    await passwordAudit.check('username signs in case-insensitively');

    await admin.goto('/auth/sign-in');
    await admin.locator('#auth-method-passcode').uncheck();
    await admin.locator('#auth-method-password').uncheck();
    await admin.getByRole('button', { name: 'Save sign-in methods', exact: true }).click();
    await expect(admin.locator('#auth-method-magic_link')).toBeChecked();
    await expect(admin.locator('#auth-method-passcode')).not.toBeChecked();
    await expect(admin.locator('#auth-method-password')).not.toBeChecked();
    await adminAudit.check('magic links can be the only first-factor method');

    await other.clearCookies();
    await page.locator('button[data-wait-seconds], button[data-label]').click();
    await hostedReady(page);
    const magicMail = await nextMail(email, 1);
    const link = magicMail.text.match(/https:\/\/auth\.terrascale\.tech\/ui\/[^\s<>]+\/sign-in\/magic\?token=[^\s<>]+/)?.[0] ?? '';
    expect(link).not.toBe('');
    saveSecret('project-magic-link', link);
    const code = /^\s*(\d{6})\s*$/m.exec(magicMail.text)?.[1] ?? '';
    expect(code).toMatch(/^\d{6}$/);

    await passwordPage.goto(link);
    await expect(passwordPage.locator('#sign-in-error')).toContainText('browser that requested');
    await expect(passwordPage.locator('#sign-in-code-digit')).toBeVisible();
    expect((await other.cookies()).some(cookie => cookie.name === `tsauth_session_${projectId}`)).toBe(false);
    await passwordAudit.check('another browser must enter the email code');

    await page.goto(link);
    await expect(page.locator('[data-state=success]')).toContainText('signed in');
    await audit.check('magic link signs in in its original browser');
    await passwordPage.goto(link);
    await expect(passwordPage.locator('[data-state=success]')).toHaveCount(0);
    await expect(passwordPage.locator('#sign-in-code-digit')).toBeVisible();
    expect((await other.cookies()).some(cookie => cookie.name === `tsauth_session_${projectId}`)).toBe(false);
    await customer.clearCookies();
    await page.goto(link);
    await expect(page.locator('[data-state=success]')).toHaveCount(0);
    expect((await customer.cookies()).some(cookie => cookie.name === `tsauth_session_${projectId}`)).toBe(false);

    const reusedCode = await other.request.post(`${base}/sign-in/verify`, {
      headers: { Origin: auth }, form: { email, code },
    });
    expect(reusedCode.status()).toBe(401);
    await admin.goto('/profile?tab=danger');
    await admin.locator('main').getByRole('button', { name: 'Delete account', exact: true }).click();
    const deletion = admin.locator('#delete-account');
    await deletion.locator('#delete-account-email').fill(ownerEmail);
    await deletion.getByRole('button', { name: 'Delete account', exact: true }).click();
    await confirmWithPassword(admin, adminAudit, password, 'project sign-in owner deletion');
    await admin.waitForURL(url => url.origin === dash && url.pathname === '/account-deleted');
    await expect(admin.locator('main')).toContainText('Account deleted');
    await adminAudit.check('project sign-in owner and project removed');
  } finally {
    await saveCookies(customer);
    await saveCookies(other);
    await saveCookies(owner);
    await customer.close();
    await other.close();
    await owner.close();
  }
});

test('social provider responses enforce identity, verification, PKCE and state bindings', async () => {
  await onLocalStack(async () => {
    const { stdout } = await promisify(execFile)('dotnet', ['run', '--project', resolve(__dirname, '../social/Journey.Social.csproj'), '-c', 'Release'], { maxBuffer: 1024 * 1024 });
    expect(stdout).toContain('8 providers: token/userinfo, verification, PKCE, state and configuration binding passed.');
  });
});
