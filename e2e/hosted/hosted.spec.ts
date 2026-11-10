import { randomBytes } from 'node:crypto';
import { Audit, auditedContext, test, expect } from '../support/audit';
import { onHosted, signInWithPassword, signUp } from '../support/hosted';
import { auth, dash, desktop, mobile } from '../support/stack';
import { journey } from '../support/state';

const sizes = [
  { label: '1440x900', options: { viewport: desktop } },
  { label: '390x844', options: { viewport: mobile, isMobile: true, hasTouch: true, deviceScaleFactor: 2 } },
] as const;

for (const size of sizes) {
  for (const theme of ['light', 'dark'] as const) {
    test(`hosted pages at ${size.label} in ${theme}`, async ({ browser }, info) => {
      const owner = journey();
      const label = `${size.label} ${theme}`;
      const context = await auditedContext(browser, { ...size.options, colorScheme: theme });
      const page = await context.newPage();
      const audit = new Audit(page, info);

      await signUp(page, audit, `hosted-${owner.runId}-${size.label}-${theme}@example.com`, randomBytes(24).toString('base64url'), label);

      await page.goto(`${auth}/ui/terrascale/reset`);
      await expect(page.locator('#reset-email')).toBeVisible();
      await audit.check(`${label} password reset request`);

      await page.goto(`${dash}/login`);
      await signInWithPassword(page, audit, owner.email, owner.password, label);
      await page.waitForURL((url) => url.origin === dash);

      await page.goto(`${auth}/ui/terrascale/security`);
      await page.waitForURL(onHosted('security'));
      await audit.check(`${label} account security`);

      await page.goto(`${dash}/profile?tab=access-tokens`);
      await page.locator('#accessTokenName').fill(`hosted-${size.label}-${theme}`);
      await page.locator('#profile-access-token-create form').getByRole('button', { name: 'Create token', exact: true }).click();
      await page.waitForURL((url) => url.origin === auth && url.pathname.startsWith('/ui/terrascale/step-up/'));
      await expect(page.locator('input[type=password]')).toBeVisible();
      await audit.check(`${label} confirmation`);

      await audit.finish();
      await context.close();
    });
  }
}
