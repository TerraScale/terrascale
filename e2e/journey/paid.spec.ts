import { createHmac, randomBytes } from 'node:crypto';
import type { Page } from '@playwright/test';
import { test, expect } from '../support/audit';
import { onboard, signUp, verify } from '../support/hosted';
import { createApiKey, createDatabase, databaseNames, stripeLog } from '../support/paid';
import { shell } from '../support/shell';
import { dash } from '../support/stack';
import { saveJourney, saveSecret, saveSession } from '../support/state';

const checkout = 'https://checkout.stripe.com';

function fact(page: Page, list: string, label: string) {
  return page.locator(`${list} dl > div`).filter({ has: page.locator('dt', { hasText: new RegExp(`^${label}$`) }) }).locator('dd');
}

function kv(page: Page, card: string, label: string) {
  return page.locator(`${card} dt`, { hasText: new RegExp(`^${label}$`) }).locator('xpath=following-sibling::dd[1]');
}

test('a new owner onboards on Free, upgrades to Pro through Stripe Checkout, and Pro lets the organization hold a second database', async ({ page, context, audit }) => {
  const runId = randomBytes(4).toString('hex');
  const email = `paid-${runId}@example.com`;
  const password = randomBytes(24).toString('base64url');
  saveSecret('paid-password', password);
  const displayName = `Paid ${runId}`;
  const organizationName = `Paid ${runId}`;
  const projectName = `Paid project ${runId}`;
  const databaseName = `paid-${runId}`;
  const secondDatabaseName = `paid-${runId}-second`;

  await signUp(page, audit, email, password, 'paid owner');
  await verify(page, email);
  const region = await onboard(page, audit, displayName, projectName, 'paid owner');
  const selection = new URL(page.url()).searchParams;
  const organizationId = selection.get('organizationId') ?? '';
  const projectId = selection.get('projectId') ?? '';
  await shell(page, { email, displayName, organizationId, organizationName: 'Personal', projectId, projectName });
  await audit.check('paid owner overview on Free');

  await page.goto('/organization');
  await page.locator('#organization-details').getByRole('button', { name: 'Edit name' }).click();
  await page.locator('#organizationName').fill(organizationName);
  await page.locator('#rename-organization').getByRole('button', { name: 'Save name', exact: true }).click();
  await expect(kv(page, '#organization-details', 'Name')).toHaveText(organizationName);
  const identity = { email, displayName, organizationId, organizationName, projectId, projectName };
  await shell(page, identity);
  await audit.check('paid owner organization renamed');

  const databaseId = await createDatabase(page, audit, databaseName, region, 'paid owner database on Free');

  await page.goto('/organization?tab=billing');
  await expect(page.locator('#billing-summary .ts-billing-plan-name')).toHaveText('Free');
  await expect(fact(page, '#billing-summary', 'Stripe charges')).toHaveText('None');
  await audit.check('paid owner billing on Free');
  await page.locator('article.ts-plan', { has: page.locator('#plan-pro') }).getByRole('button', { name: 'Upgrade to Pro', exact: true }).click();
  await page.waitForURL((url) => url.origin === checkout && url.pathname.startsWith('/c/pay/cs_test_'));
  const session = new URL(page.url()).pathname.split('/').at(-1) ?? '';
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Subscribe to TerraScale Pro');
  await expect(page.locator('main p')).toHaveText('US$20.00 per month, Visa ending in 4242');
  await page.getByRole('button', { name: 'Subscribe', exact: true }).click();
  await page.waitForURL((url) => url.origin === dash && url.pathname === '/organization' && url.searchParams.get('payment') === 'success');
  await expect(page.locator('#billing-summary .ts-billing-plan-name')).toHaveText('Pro');
  await expect(page.locator('#billing-summary .ts-billing-plan .ts-badge')).toHaveText('Active');
  await expect(fact(page, '#billing-summary', 'Billing interval')).toHaveText('Monthly');
  const renewal = new Date();
  renewal.setUTCMonth(renewal.getUTCMonth() + 1);
  await expect(fact(page, '#billing-summary', 'Renewal')).toHaveText(renewal.toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric', timeZone: 'UTC' }));
  await expect(page.locator('#billing-payment-method')).toHaveText('Visa •••• 4242, expires 12/34');
  await expect(fact(page, '#billing-summary', 'Price')).toHaveText('$20.00 / monthly');
  await expect(page.locator('#billing-usage')).toHaveText("Usage this period: $0.00 so far, within Pro's 2,000,000 included operation units. Units above them cost $5.00 per million.");
  await expect(page.locator('article.ts-plan[aria-current="true"] h3')).toHaveText('Pro');
  await shell(page, identity);
  await audit.check('paid owner billing on Pro');

  const delivered = (await stripeLog()).events.filter((event) => event.object === session);
  expect(delivered.map((event) => [event.type, event.status]), 'signed events the Stripe stand-in delivered for the checkout').toEqual([['checkout.session.completed', 200]]);
  const created = Math.floor(Date.now() / 1000);
  const payload = JSON.stringify({
    id: `evt_${randomBytes(12).toString('hex')}`, api_version: '2026-09-30.endive', data: { object: { id: session, object: 'checkout.session' } }, livemode: false,
    type: 'checkout.session.completed',
  });
  const signature = createHmac('sha256', `whsec_${randomBytes(24).toString('hex')}`).update(`${created}.${payload}`).digest('hex');
  const forged = await fetch(`${dash}/api/v1/billing/stripe/webhook`, { method: 'POST', headers: { 'content-type': 'application/json', 'stripe-signature': `t=${created},v1=${signature}` }, body: payload });
  expect(forged.status, 'a checkout.session.completed event signed with another secret').toBe(400);

  await page.goto('/organization');
  await expect(page.locator('.ts-org-plan-row strong')).toHaveText('Pro');
  await expect(page.locator('.ts-org-plan-row .ts-badge')).toHaveText('Active');
  await expect(page.locator('.ts-org-plan-row .ts-org-price')).toHaveText('$20.00 / month');
  for (const [label, value] of [['Databases', '25 databases'], ['Storage', '10 GB-region storage'], ['Team members', '25 team members'], ['Operation units', '2,000,000 operation units/month (4 KiB)']]) {
    await expect(kv(page, 'section[aria-labelledby="overview-billing-title"]', label), `${label} on the Plan & billing card`).toHaveText(value);
  }
  await audit.check('paid owner organization on Pro');

  const secondDatabaseId = await createDatabase(page, audit, secondDatabaseName, region, 'paid owner second database on Pro');
  await page.goto('/databases');
  await expect(databaseNames(page)).toHaveText([databaseName, secondDatabaseName]);
  await audit.check('paid owner databases on Pro');

  const apiKeyName = `paid-key-${runId}`;
  const apiKey = await createApiKey(page, audit, apiKeyName, 'paid owner API key');
  await saveSession(context);
  saveJourney({
    runId, email, password, displayName, organizationId, organizationName, projectId, projectName,
    databaseId, databaseName, databaseRegion: region, apiKeyName, apiKey, secondDatabaseId, secondDatabaseName,
  });
});
