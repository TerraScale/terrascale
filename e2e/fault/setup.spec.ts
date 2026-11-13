import '../journey/setup.spec';
import { Audit, auditedContext, test, expect } from '../support/audit';
import { ownerSession, saveCookies } from '../support/state';

test('upgrade the fault owner so a backup can restore alongside its source', async ({ browser }, info) => {
  expect(process.env.E2E_TARGET ?? 'local').toBe('local');
  expect(process.env.E2E_REGIONS).toBe('2');
  expect(process.env.E2E_NODES_PER_REGION).toBe('2');
  const context = await auditedContext(browser, { storageState: ownerSession() });
  try {
    const page = await context.newPage();
    const audit = new Audit(page, info);
    await page.goto('https://dash.terrascale.tech/organization?tab=billing');
    await page.locator('article.ts-plan', { has: page.locator('#plan-pro') }).getByRole('button', { name: 'Upgrade to Pro', exact: true }).click();
    await page.waitForURL(url => url.origin === 'https://checkout.stripe.com' && url.pathname.startsWith('/c/pay/cs_test_'));
    await page.getByRole('button', { name: 'Subscribe', exact: true }).click();
    await page.waitForURL(url => url.origin === 'https://dash.terrascale.tech' && url.searchParams.get('payment') === 'success');
    await expect(page.locator('#billing-summary .ts-billing-plan-name')).toHaveText('Pro');
    await audit.check('fault owner on Pro');
    await audit.finish();
  } finally {
    await saveCookies(context);
    await context.close();
  }
});
