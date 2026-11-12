import { randomBytes } from 'node:crypto';
import { Audit, auditedContext, test, expect } from '../support/audit';
import { hostedReady, onboard, onHosted, openSignUp, signInWithPassword, signUp, submitSignUp, verify } from '../support/hosted';
import { newAddress } from '../support/inbox';
import { auth, dash, desktop, mobile, onLocalStack } from '../support/stack';
import { journey, saveCookies, saveSecret } from '../support/state';

const sizes = [
  { label: '1440x900', options: { viewport: desktop } },
  { label: '390x844', options: { viewport: mobile, isMobile: true, hasTouch: true, deviceScaleFactor: 2 } },
] as const;

test('a second user enrolls a passkey and signs back in to the dashboard and a requested page', async ({ browser }, info) => {
  const owner = journey();
  const email = await newAddress(`passkey-${owner.runId}-${Date.now().toString(36)}`);
  const password = randomBytes(24).toString('base64url');
  saveSecret('passkey-user-password', password);
  const context = await auditedContext(browser, { viewport: desktop });
  const page = await context.newPage();
  const audit = new Audit(page, info);
  const cdp = await context.newCDPSession(page);
  await cdp.send('WebAuthn.enable');
  await cdp.send('WebAuthn.addVirtualAuthenticator', { options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true } });
  try {
    await signUp(page, audit, email, password, 'passkey user');
    await verify(page, email);
    await onboard(page, audit, 'Passkey user', `Passkey ${owner.runId}`, 'passkey user');
    await page.goto(`${auth}/ui/terrascale/security`);
    await hostedReady(page);
    await page.locator('#passkey-name').fill('Journey passkey');
    await page.locator('#passkey-add button[type=submit]').click();
    await expect(page.locator('input[name=name][value="Journey passkey"]')).toBeVisible();
    await audit.check('passkey enrolled through account security');

    const dialogs: string[] = [];
    page.on('dialog', async (dialog) => { dialogs.push(dialog.type()); await dialog.dismiss(); });
    for (const path of ['/', '/databases', null, 'post']) {
      await page.goto(`${dash}/profile`);
      await page.locator('.ts-account-trigger').click();
      await page.getByRole('button', { name: 'Sign out', exact: true }).click();
      await page.waitForURL(onHosted('sign-in'));
      await page.goto(path === null || path === 'post' ? `${auth}/ui/terrascale/sign-in` : `${dash}${path}`);
      await page.waitForURL(onHosted('sign-in'));
      await hostedReady(page);
      if (path === 'post') {
        audit.expectStatus(/\/ui\/terrascale\/sign-in\/mfa$/, 401);
        await Promise.all([
          page.waitForURL(onHosted('sign-in/mfa')),
          page.evaluate(() => {
            const form = document.createElement('form');
            form.method = 'post';
            form.action = '/ui/terrascale/sign-in/mfa';
            document.body.append(form);
            form.submit();
          }),
        ]);
        await hostedReady(page);
        await expect(page.locator('#sign-in-error')).toContainText('Sign-in expired');
        await audit.check('expired MFA POST still offers passkey sign-in');
      }
      await page.locator('#sign-in-email').fill(email);
      if (path === null || path === 'post') {
        const [navigation] = await Promise.all([
          page.waitForRequest((request) => request.isNavigationRequest() && request.frame() === page.mainFrame()),
          page.locator('#passkey-sign-in').click(),
        ]);
        expect(navigation.method()).toBe('GET');
        expect(new URL(navigation.url()).pathname).toBe('/ui/terrascale/sign-in');
        await page.waitForURL(onHosted('sign-in'));
        await hostedReady(page);
        expect(dialogs).toEqual([]);
        await expect(page.getByRole('link', { name: 'Continue', exact: true })).toHaveAttribute('href', dash);
        await audit.check('signed in without a continuation offers the dashboard');
        await page.getByRole('link', { name: 'Continue', exact: true }).click();
      } else {
        await page.locator('#passkey-sign-in').click();
      }
      await expect(page, `passkey sign-in continues to ${path ?? 'the dashboard after GET'}`).toHaveURL((url) => url.origin === dash && url.pathname === (path === null || path === 'post' ? '/' : path));
      await expect(page.locator('.ts-account-trigger')).toContainText('Passkey user');
      await audit.check(`passkey sign-in reaches ${path}`);
    }
    await audit.finish();
  } finally {
    await saveCookies(context);
    await context.close();
  }
});

for (const size of sizes) {
  for (const theme of ['light', 'dark'] as const) {
    test(`hosted pages at ${size.label} in ${theme}`, async ({ browser }, info) => {
      const owner = journey();
      const label = `${size.label} ${theme}`;
      const context = await auditedContext(browser, { ...size.options, colorScheme: theme });
      const page = await context.newPage();
      const audit = new Audit(page, info);

      await openSignUp(page, audit, label);
      await onLocalStack(async () => {
        const password = randomBytes(24).toString('base64url');
        saveSecret('hosted-password', password);
        await submitSignUp(page, audit, `hosted-${owner.runId}-${size.label}-${theme}@example.com`, password, label);
      });

      await page.goto(`${auth}/ui/terrascale/reset`);
      await hostedReady(page);
      await expect(page.locator('#reset-email')).toBeVisible();
      await audit.check(`${label} password reset request`);

      await page.goto(`${dash}/login`);
      await signInWithPassword(page, audit, owner.email, owner.password, label);
      await page.waitForURL((url) => url.origin === dash);

      await page.goto(`${auth}/ui/terrascale/security`);
      await page.waitForURL(onHosted('security'));
      await hostedReady(page);
      await audit.check(`${label} account security`);

      await page.goto(`${dash}/profile?tab=access-tokens`);
      await page.locator('#accessTokenName').fill(`hosted-${size.label}-${theme}`);
      await page.locator('#profile-access-token-create form').getByRole('button', { name: 'Create token', exact: true }).click();
      await page.waitForURL((url) => url.origin === auth && url.pathname.startsWith('/ui/terrascale/step-up/'));
      await hostedReady(page);
      await expect(page.locator('input[type=password]')).toBeVisible();
      await audit.check(`${label} confirmation`);

      await audit.finish();
      await saveCookies(context);
      await context.close();
    });
  }
}
