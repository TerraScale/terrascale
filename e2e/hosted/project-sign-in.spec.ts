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
          await dialog.locator(`[name=${field}]`).fill(`https://identity.journey.terrascale.tech/${field}`);
        }
        await dialog.locator('[name=scopes]').fill('profile email');
      }
      await dialog.getByRole('button', { name: `Save ${label}`, exact: true }).click();
      await expect(admin.locator(`#${id} .ts-badge`)).toHaveText('Configured');
      if (key === 'oauth2') {
        await admin.locator(`#${id} button[command=show-modal]`).click();
        await expect(admin.locator(`#${id}-dialog [name=userinfo_url]`)).toHaveValue('https://identity.journey.terrascale.tech/userinfo_url');
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
    const duplicateResponse = duplicate.waitForResponse(response => response.url() === base + '/sign-up' && response.request().method() === 'POST');
    await duplicate.locator('form:has(#sign-up-email) button[type=submit]').click();
    expect((await duplicateResponse).status()).toBe(400);
    await expect(duplicate.locator('#sign-in-error')).toContainText('Username taken');
    expect((await other.cookies()).some(cookie => cookie.name === 'tsauth_session_' + projectId)).toBe(false);
    const reserved = await other.request.post(base + '/sign-up', {
      headers: { Origin: auth }, form: { email: duplicateEmail, username: 'AdMiN', password },
    });
    expect(reserved.status()).toBe(400);
    expect(await reserved.text()).toContain('Use a username');
    const raceName = 'race_' + current.runId;
    const raceEmail = await newAddress('race-first-' + current.runId);
    const lostEmail = await newAddress('race-second-' + current.runId);
    const starts = [];
    for (const address of [raceEmail, lostEmail]) {
      const response = await other.request.post(base + '/sign-up', {
        headers: { Origin: auth }, form: { email: address, username: raceName, password },
      });
      expect(response.status()).toBe(200);
      const html = await response.text();
      const pending = /name="pending_token" value="([^"]+)"/.exec(html)?.[1] ?? '';
      expect(pending).not.toBe('');
      saveSecret('race-signup-token-' + address, pending);
      starts.push({ address, pending });
    }
    for (const [index, start] of starts.entries()) {
      const mail = await nextMail(start.address, 0);
      const raceCode = /^\s*(\d{6})\s*$/m.exec(mail.text)?.[1] ?? '';
      const response = await other.request.post(base + '/sign-up/verify', {
        headers: { Origin: auth }, form: { email: start.address, code: raceCode, pending_token: start.pending },
      });
      expect(response.status()).toBe(index === 0 ? 200 : 400);
      expect(await response.text()).toContain(index === 0 ? 'signed in' : 'Username taken');
    }
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

    const challenge = await passwordPage.locator('form[action$="/sign-in/verify"] input[name=challenge]').inputValue();
    const reusedCode = await other.request.post(`${base}/sign-in/verify`, {
      headers: { Origin: auth }, form: { email, code, challenge },
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

test('unverified social email creates and links an account only after the code, then its subject signs in directly', async ({ browser }, info) => {
  await onLocalStack(async () => {
    const run = randomBytes(4).toString('hex');
    const owner = await auditedContext(browser, { viewport: desktop });
    const customer = await auditedContext(browser, { viewport: desktop });
    const admin = await owner.newPage();
    const page = await customer.newPage();
    const adminAudit = new Audit(admin, info);
    const audit = new Audit(page, info);
    const password = randomBytes(24).toString('base64url');
    const ownerEmail = await newAddress('social-owner-' + run);
    const email = await newAddress('social-new-' + run);
    const subject = 'social-' + run;
    saveSecret('social-sign-up-password', password);
    try {
      await signUp(admin, adminAudit, ownerEmail, password, 'unverified social owner');
      await verify(admin, ownerEmail);
      await onboard(admin, adminAudit, 'Social owner', 'Social ' + run, 'unverified social owner');
      const projectId = new URL(admin.url()).searchParams.get('projectId') ?? '';
      expect(projectId).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
      const base = auth + '/ui/' + projectId;
      await admin.goto('/auth/social');
      await admin.locator('#auth-provider-oauth2 button[command=show-modal]').click();
      const dialog = admin.locator('#auth-provider-oauth2-dialog');
      await dialog.locator('[name=client_id]').fill('journey-client');
      await enterSecret(dialog.locator('[name=client_secret]'), password);
      for (const [field, path] of [['authorize_url', 'authorize'], ['token_url', 'token'], ['userinfo_url', 'userinfo']]) {
        await dialog.locator('[name=' + field + ']').fill('https://identity.journey.terrascale.tech/' + path);
      }
      await dialog.locator('[name=scopes]').fill('profile email');
      await dialog.getByRole('button', { name: 'Save Custom OAuth2', exact: true }).click();
      await expect(admin.locator('#auth-provider-oauth2 .ts-badge')).toHaveText('Configured');
      await admin.goto('/auth/sign-in');
      await admin.locator('#auth-method-username').check();
      await admin.locator('#auth-method-passcode').uncheck();
      await admin.getByRole('button', { name: 'Save sign-in methods', exact: true }).click();
      await expect(admin.locator('#auth-method-username')).toBeChecked();
      await adminAudit.check('unverified OAuth2 provider configured');

      const providerSignIn = async (address: string, id: string, verified = false) => {
        await page.goto(base + '/social/oauth2');
        await page.locator('#email').fill(address);
        await page.locator('#subject').fill(id);
        if (verified) await page.locator('#verified').check();
        await page.getByRole('button', { name: 'Continue', exact: true }).click();
      };
      await providerSignIn(email, subject);
      await expect(page.locator('#sign-in-code-digit')).toBeVisible();
      expect((await customer.cookies()).some(cookie => cookie.name === 'tsauth_session_' + projectId)).toBe(false);
      await expect(page.locator('main')).toContainText('Enter this code to connect Custom OAuth2 to your');
      await expect(page.locator('main')).toContainText("If you didn't try to sign in with Custom OAuth2, ignore this email.");
      await audit.check('new unverified social identity requires email proof');
      const otherEmail = await newAddress('social-other-' + run);
      for (const path of ['/sign-in', '/sign-in/verify']) {
        const refused = await customer.request.post(base + path, {
          headers: { Origin: auth }, form: { email: otherEmail, code: '000000' },
        });
        expect(refused.status()).toBe(403);
      }
      const pending = (await customer.cookies()).filter(cookie => cookie.name.startsWith('tsauth_social_link_'));
      expect(pending).toHaveLength(1);
      pending.forEach(cookie => saveSecret('pending-social-cookie', cookie.value));
      const mail = await nextMail(email, 0);
      expect(mail.text).toContain('Enter this code to connect Custom OAuth2 to your');
      expect(mail.text).toContain("If you didn't try to sign in with Custom OAuth2, ignore this email.");
      const code = /^\s*(\d{6})\s*$/m.exec(mail.text)?.[1] ?? '';
      expect(code).toMatch(/^\d{6}$/);
      const wrongEmail = await customer.request.post(base + '/sign-in/verify', {
        headers: { Origin: auth }, form: { email: otherEmail, code },
      });
      expect(wrongEmail.status()).toBe(403);
      const anotherBrowser = await auditedContext(browser, { viewport: desktop });
      try {
        const missingPending = await anotherBrowser.request.post(base + '/sign-in/verify', {
          headers: { Origin: auth }, form: { email, code },
        });
        expect(missingPending.status()).toBe(404);
        expect((await anotherBrowser.cookies()).some(cookie => cookie.name === 'tsauth_session_' + projectId)).toBe(false);
      } finally {
        await saveCookies(anotherBrowser);
        await anotherBrowser.close();
      }
      const wrongCode = await customer.request.post(base + '/sign-in/verify', {
        headers: { Origin: auth }, form: { email, code: code === '000000' ? '000001' : '000000' },
      });
      expect(wrongCode.status()).toBe(401);
      expect((await customer.cookies()).some(cookie => cookie.name === 'tsauth_session_' + projectId)).toBe(false);
      await enterSecret(page.locator('#sign-in-code-digit'), code);
      await page.getByRole('button', { name: 'Verify and sign in', exact: true }).click();
      await expect(page.locator('[data-state=success]')).toContainText('signed in');
      await audit.check('email code creates the social account');
      await page.goto(base + '/security');
      await expect(page.getByRole('button', { name: 'Remove Custom OAuth2', exact: true })).toBeVisible();
      const username = 'social_' + run;
      await page.locator('#security-username').fill(username.toUpperCase());
      await page.getByRole('button', { name: 'Save username', exact: true }).click();
      await expect(page.locator('#security-username')).toHaveValue(username);
      await expect(page.locator('[data-state=success]')).toContainText('Username saved');
      await audit.check('an existing social user sets a username from Security');
      const replayCookies = await customer.cookies();
      const pendingCookie = replayCookies.filter(cookie => cookie.name.startsWith('tsauth_social_link_'));
      expect(pendingCookie).toHaveLength(0);

      await customer.clearCookies();
      await customer.addCookies(pending);
      const replay = await customer.request.post(base + '/sign-in/verify', { headers: { Origin: auth }, form: { email, code } });
      expect(replay.status()).toBe(401);
      expect((await customer.cookies()).some(cookie => cookie.name === 'tsauth_session_' + projectId)).toBe(false);
      await customer.clearCookies();
      await providerSignIn('changed-' + email, subject);
      await expect(page.locator('[data-state=success]')).toContainText('signed in');
      await expect(page.locator('#sign-in-code-digit')).toHaveCount(0);
      await page.goto(base + '/security');
      await expect(page.locator('main')).toContainText(email);
      await expect(page.locator('#security-username')).toHaveValue(username);
      await audit.check('bound subject signs into the same account without another email code');

      await customer.clearCookies();
      const existingEmail = await newAddress('social-existing-' + run);
      await providerSignIn(existingEmail, 'original-subject-' + run, true);
      await expect(page.locator('[data-state=success]')).toContainText('signed in');
      await customer.clearCookies();
      await providerSignIn(existingEmail, 'unverified-existing-' + run);
      await expect(page.locator('#sign-in-code-digit')).toBeVisible();
      expect((await customer.cookies()).some(cookie => cookie.name === 'tsauth_session_' + projectId)).toBe(false);
      await audit.check('an unbound subject cannot select an existing account by email');
      const existingMail = await nextMail(existingEmail, 0);
      expect(existingMail.text).toContain('Enter this code to connect Custom OAuth2 to your');
      const existingCode = /^\s*(\d{6})\s*$/m.exec(existingMail.text)?.[1] ?? '';
      await enterSecret(page.locator('#sign-in-code-digit'), existingCode);
      await page.getByRole('button', { name: 'Verify and sign in', exact: true }).click();
      await expect(page.locator('[data-state=success]')).toContainText('signed in');
      await customer.clearCookies();
      await providerSignIn('changed-' + existingEmail, 'unverified-existing-' + run);
      await expect(page.locator('[data-state=success]')).toContainText('signed in');
      await expect(page.locator('#sign-in-code-digit')).toHaveCount(0);
      await page.goto(base + '/security');
      await expect(page.locator('main')).toContainText(existingEmail);
      await audit.check('existing account connects after proof and its next sign-in needs no code');
      await expect.poll(async () => {
        await admin.goto('/auth/logs');
        return admin.locator('main').innerText();
      }).toContain('user.updated');
      await expect(admin.locator('main')).toContainText('user.signed_up');
      await expect(admin.locator('main')).toContainText('user.provider_linked');
      await expect(admin.locator('main')).toContainText('user.updated');
      await adminAudit.check('social creation and linking emit lifecycle events');

      await admin.goto('/profile?tab=danger');
      await admin.locator('main').getByRole('button', { name: 'Delete account', exact: true }).click();
      const deletion = admin.locator('#delete-account');
      await deletion.locator('#delete-account-email').fill(ownerEmail);
      await deletion.getByRole('button', { name: 'Delete account', exact: true }).click();
      await confirmWithPassword(admin, adminAudit, password, 'unverified social owner deletion');
      await admin.waitForURL(url => url.origin === dash && url.pathname === '/account-deleted');
      await expect(admin.locator('main')).toContainText('Account deleted');
    } finally {
      await saveCookies(customer);
      await saveCookies(owner);
      await customer.close();
      await owner.close();
    }
  });
});
