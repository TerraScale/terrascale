import { createHmac, randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { test, expect } from '../support/audit';
import { onboard, signUp, verify } from '../support/hosted';
import { regionsSettled } from '../support/overview';
import { createApiKey, createDatabase, databaseNames, stripeLog } from '../support/paid';
import { shell } from '../support/shell';
import { dash, stateDir, stripeStandIn } from '../support/stack';
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
  await regionsSettled(page);
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

async function organizationOwner(page: Page, audit: import('../support/audit').Audit) {
  const run = randomBytes(4).toString('hex');
  const email = `organization-${run}@example.com`;
  const password = randomBytes(24).toString('base64url');
  saveSecret('organization-password', password);
  await signUp(page, audit, email, password, 'organization owner');
  await verify(page, email);
  await onboard(page, audit, `Owner ${run}`, `Personal ${run}`, 'organization owner');
  const deviceResponse = await page.request.post('https://auth.terrascale.tech/terrascale/oauth/device', {
    form: { client_id: '00000000000000000000000007', scope: 'openid email profile' },
  });
  expect(deviceResponse.status()).toBe(200);
  const device = await deviceResponse.json();
  saveSecret('organization-device', device.device_code);
  await page.goto(device.verification_uri_complete);
  await audit.check('organization owner approves CLI access');
  await page.locator('button[name=decision][value=approve]').click();
  await expect(page.locator('[data-state=success]')).toBeVisible();
  const tokenResponse = await page.request.post('https://auth.terrascale.tech/terrascale/oauth/token', {
    form: { client_id: '00000000000000000000000007', grant_type: 'urn:ietf:params:oauth:grant-type:device_code', device_code: device.device_code },
  });
  expect(tokenResponse.status()).toBe(200);
  const token = (await tokenResponse.json()).access_token as string;
  saveSecret('organization-sign-in-token', token);
  const headers = { authorization: `Bearer ${token}` };
  const organizations = async () => {
    const response = await page.request.get('https://auth.terrascale.tech/v1/terrascale/organizations', { headers });
    expect(response.status()).toBe(200);
    return (await response.json()).items as { id: string; name: string }[];
  };
  return { run, headers, organizations };
}

test('organization checkout creates nothing before payment and completes exactly once on authenticated return', async ({ page, audit }) => {
  const owner = await organizationOwner(page, audit);
  const before = await owner.organizations();
  const name = `Confirmed ${owner.run}`;
  await page.goto('/organization?newOrganization=organization');
  const wizard = page.locator('#create-organization[open]');
  const input = wizard.locator('input[name=name]');
  await expect(input).toHaveAttribute('required', '');
  await expect(input).toHaveAttribute('minlength', '1');
  await expect(input).toHaveAttribute('maxlength', '128');
  await expect(input).toHaveAttribute('pattern', /p\{L\}/);
  await input.fill('!!!');
  await wizard.getByRole('button', { name: 'Continue to billing', exact: true }).evaluate((button) => { (button as HTMLButtonElement).formNoValidate = true; });
  audit.expectStatus(/\/api\/v1\/dashboard\/organization\/new-organization(?:\?|$)/, 422);
  await wizard.getByRole('button', { name: 'Continue to billing', exact: true }).click();
  await expect(wizard.getByRole('alert')).toContainText('Names need');
  await expect(wizard.locator('input[name=name]')).toHaveValue('!!!');
  await input.fill(name);
  await wizard.getByRole('button', { name: 'Continue to billing', exact: true }).click();
  await expect(wizard.getByRole('heading', { name: 'Set up billing' })).toBeVisible();
  expect(await owner.organizations()).toEqual(before);
  await wizard.locator('input[name=plan][value=pro]').check();
  await wizard.getByRole('button', { name: 'Continue to checkout', exact: true }).click();
  await page.waitForURL((url) => url.origin === checkout);
  const checkoutId = new URL(page.url()).pathname.split('/').at(-1) ?? '';
  expect(await owner.organizations()).toEqual(before);
  const checkoutUrl = page.url();
  await page.goto('/organization?newOrganization=organization');
  const cancelForm = await wizard.locator('form').evaluate((form) => Object.fromEntries(new FormData(form as HTMLFormElement).entries()) as Record<string, string>);
  const paid = await page.request.post(checkoutUrl, { maxRedirects: 0 });
  expect(paid.status()).toBe(303);
  const stripeKey = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n').find((line) => line.startsWith('sk_test_'));
  if (!stripeKey) throw new Error('The local stack has no Stripe credential');
  const stripeHeaders = { authorization: 'Bearer ' + stripeKey, 'stripe-version': '2026-09-30.endive' };
  const paidResponse = await page.request.get('https://api.stripe.com/v1/checkout/sessions/' + checkoutId, { headers: stripeHeaders });
  expect(paidResponse.status()).toBe(200);
  const paidSession = await paidResponse.json();
  const failure = await fetch(stripeStandIn + '/failure', {
    method: 'POST', body: JSON.stringify({ method: 'POST', path: '/v1/subscriptions/' + paidSession.subscription, status: 404, remaining: 2 }),
  });
  expect(failure.status).toBe(200);
  const authFailure = await fetch(stripeStandIn + '/auth-failure', {
    method: 'POST', body: JSON.stringify({ checkout: checkoutId }),
  });
  expect(authFailure.status).toBe(200);
  const interrupted = await page.request.get(paid.headers().location, { maxRedirects: 0 });
  expect(interrupted.status()).toBe(303);
  expect(interrupted.headers().location).toBe('/organization');
  const unfinished = (await owner.organizations()).filter((org) => org.name === name);
  expect(unfinished).toHaveLength(1);
  const ordinary = await page.request.get('/organization', {
    headers: { 'Datastar-Request': 'true', accept: 'text/html' },
  });
  expect(ordinary.status()).toBe(200);
  const ordinaryHtml = await ordinary.text();
  expect(ordinaryHtml).toContain('data-organization-option="' + before[0].id + '"');
  expect(ordinaryHtml).not.toContain('data-organization-option="' + unfinished[0].id + '"');
  await page.goto(paid.headers().location);
  await page.waitForURL((url) => url.origin === dash && url.pathname === '/organization' && url.searchParams.get('newOrganization') === 'project');
  const afterFailure = await stripeLog();
  expect(afterFailure.calls.filter((call) => call.path === '/v1/subscriptions/' + paidSession.subscription && call.status === 404)).toHaveLength(2);
  const staleCancel = await page.request.post('/api/v1/dashboard/organization/new-organization', { form: cancelForm, maxRedirects: 0 });
  expect(staleCancel.status()).toBe(303);
  await page.goto(staleCancel.headers().location);
  await expect(page.locator('#organization-checkout-notice')).toContainText('Checkout cannot be canceled.');
  await page.goto('/organization?tab=billing');
  await expect(page.locator('#billing-summary .ts-billing-plan-name')).toHaveText('Pro');
  await expect(page.locator('#billing-summary .ts-billing-plan .ts-badge')).toHaveText('Active');
  const afterResume = await stripeLog();
  expect(afterResume.cancels.filter((entry) => entry.subscription === paidSession.subscription)).toHaveLength(0);
  expect(afterResume.refunds.filter((entry) => entry.subscription === paidSession.subscription)).toHaveLength(0);
  console.log('organization checkout after mark: Stripe metadata 404 twice; stale wizard cancel refused; no cancel or refund; next navigation has Pro with its binding');
  const created = (await owner.organizations()).filter((org) => org.name === name);
  expect(created).toHaveLength(1);
  const organization = created[0];
  const again = await page.request.post('https://auth.terrascale.tech/v1/terrascale/organizations', {
    headers: owner.headers, data: { name, idempotency_key: checkoutId },
  });
  expect(again.status()).toBe(201);
  expect((await again.json()).id).toBe(organization.id);
  await page.goto(`/billing/checkout/return?session_id=${checkoutId}`);
  await page.goto(`/billing/checkout/return?session_id=${checkoutId}`);
  expect((await owner.organizations()).filter((org) => org.name === name)).toEqual([organization]);
  const members = await page.request.get(`https://auth.terrascale.tech/v1/terrascale/organizations/${organization.id}/members`, { headers: owner.headers });
  expect(members.status()).toBe(200);
  expect((await members.json()).items).toHaveLength(1);
  const events = await page.request.get(`https://auth.terrascale.tech/v1/terrascale/organizations/${organization.id}/audit-events`, { headers: owner.headers });
  expect(events.status()).toBe(200);
  expect((await events.json()).items.filter((event: { action: string }) => event.action === 'organization.created')).toHaveLength(1);
  await expect(page.locator('#create-organization[open]')).toBeVisible();
  await audit.check('confirmed organization project step after repeated completion');
  await expect(wizard.getByRole('button', { name: 'Cancel pending checkout', exact: true })).toHaveCount(0);
  console.log('organization checkout: response lost after real ts-auth create; ordinary signed-in request hides creating organization; resumed Pro with billing binding; exactly one organization, one owner membership, one creation audit and no pending checkout');
  const canceledName = 'Canceled payment ' + owner.run;
  await page.goto('/organization?newOrganization=organization');
  const canceledWizard = page.locator('#create-organization[open]');
  await canceledWizard.locator('input[name=name]').fill(canceledName);
  await canceledWizard.getByRole('button', { name: 'Continue to billing', exact: true }).click();
  await canceledWizard.locator('input[name=plan][value=pro]').check();
  await canceledWizard.getByRole('button', { name: 'Continue to checkout', exact: true }).click();
  await page.waitForURL((url) => url.origin === checkout);
  const canceledId = new URL(page.url()).pathname.split('/').at(-1) ?? '';
  const refusedPayment = await page.request.post(page.url(), { maxRedirects: 0 });
  expect(refusedPayment.status()).toBe(303);
  const sessionResponse = await page.request.get('https://api.stripe.com/v1/checkout/sessions/' + canceledId, { headers: stripeHeaders });
  expect(sessionResponse.status()).toBe(200);
  const refusedSession = await sessionResponse.json();
  const canceled = await page.request.delete('https://api.stripe.com/v1/subscriptions/' + refusedSession.subscription, { headers: stripeHeaders });
  expect(canceled.status()).toBe(200);
  const invoices = await page.request.get('https://api.stripe.com/v1/invoices', {
    headers: stripeHeaders, params: { customer: refusedSession.customer, subscription: refusedSession.subscription, status: 'paid' },
  });
  expect(invoices.status()).toBe(200);
  const payments = await page.request.get('https://api.stripe.com/v1/invoice_payments', {
    headers: stripeHeaders, params: { invoice: (await invoices.json()).data[0].id, status: 'paid' },
  });
  expect(payments.status()).toBe(200);
  const paymentId = (await payments.json()).data[0].payment.payment_intent;
  const pendingRefund = await fetch(stripeStandIn + '/refund-status', { method: 'POST', body: JSON.stringify({ payment: paymentId, status: 'requires_action' }) });
  expect(pendingRefund.status).toBe(200);
  await page.goto(refusedPayment.headers().location);
  await expect(page.locator('#organization-checkout-notice')).toContainText('Your refund is requires_action.');
  await expect(page.locator('#organization-checkout-notice')).toContainText('You can start a new checkout.');
  await page.reload();
  await expect(page.locator('#organization-checkout-notice')).toHaveCount(0);
  await page.goto('/organization?newOrganization=organization');
  await canceledWizard.locator('input[name=name]').fill(canceledName);
  await canceledWizard.getByRole('button', { name: 'Continue to billing', exact: true }).click();
  await canceledWizard.locator('input[name=plan][value=pro]').check();
  await canceledWizard.getByRole('button', { name: 'Continue to checkout', exact: true }).click();
  await page.waitForURL((url) => url.origin === checkout);
  const unsettled = await page.request.get('https://api.stripe.com/v1/refunds', { headers: stripeHeaders, params: { payment_intent: paymentId } });
  expect(unsettled.status()).toBe(200);
  expect((await unsettled.json()).data[0].status).toBe('requires_action');
  await page.getByRole('link', { name: 'Cancel', exact: true }).click();
  await page.waitForURL((url) => url.origin === dash);
  const settledRefund = await fetch(stripeStandIn + '/refund-status', { method: 'POST', body: JSON.stringify({ payment: paymentId, status: 'succeeded' }) });
  expect(settledRefund.status).toBe(200);
  await page.goto('/organization');
  await expect(page.locator('#organization-checkout-notice')).toContainText("The payment didn't go through.");
  await expect(page.locator('#organization-checkout-notice')).toHaveAttribute('role', 'status');
  await expect(page.locator('#organization-checkout-notice')).toContainText('Your payment was refunded.');
  const refusedLog = await stripeLog();
  expect(refusedLog.cancels.filter((entry) => entry.subscription === refusedSession.subscription)).toHaveLength(1);
  const refunds = refusedLog.refunds.filter((entry) => entry.subscription === refusedSession.subscription);
  expect(refunds).toHaveLength(1);
  expect(refunds[0].amount).toBe(2000);
  expect((await owner.organizations()).filter((org) => org.name === canceledName)).toHaveLength(0);
  await audit.check('payment canceled before return creates no organization');
  await page.reload();
  await expect(page.locator('#organization-checkout-notice')).toHaveCount(0);
  console.log('organization checkout: requires_action refund retained beside a fresh draft; new draft canceled; settled refund keeps original refusal reason once; no organization; one full refund');

  const conflictName = 'Existing billing ' + owner.run;
  await page.goto('/organization?newOrganization=organization');
  await wizard.locator('input[name=name]').fill(conflictName);
  await wizard.getByRole('button', { name: 'Continue to billing', exact: true }).click();
  await wizard.locator('input[name=plan][value=pro]').check();
  await wizard.getByRole('button', { name: 'Continue to checkout', exact: true }).click();
  await page.waitForURL((url) => url.origin === checkout);
  const conflictUrl = page.url();
  const conflictId = new URL(conflictUrl).pathname.split('/').at(-1) ?? '';
  const external = await page.request.post('https://auth.terrascale.tech/v1/terrascale/organizations', {
    headers: owner.headers, data: { name: conflictName, idempotency_key: conflictId },
  });
  expect(external.status()).toBe(201);
  const conflictOrganization = await external.json();
  await page.goto('/organization?tab=billing&organizationId=' + conflictOrganization.id);
  await expect(page.locator('#billing-summary .ts-billing-plan-name')).toHaveText('No plan');
  await page.locator('.ts-context-trigger').click();
  await page.locator('[data-organization-option="' + conflictOrganization.id + '"]').click();
  await expect(page.locator('[data-active-organization-id="' + conflictOrganization.id + '"]')).toHaveText('Current organization: ' + conflictName);
  await page.locator('article.ts-plan', { has: page.locator('#plan-pro') }).getByRole('button', { name: 'Choose Pro', exact: true }).click();
  await page.waitForURL((url) => url.origin === checkout);
  const ownId = new URL(page.url()).pathname.split('/').at(-1) ?? '';
  expect(ownId === conflictId, 'the organization has a separate checkout').toBe(false);
  await page.getByRole('button', { name: 'Subscribe', exact: true }).click();
  await page.waitForURL((url) => url.origin === dash && url.searchParams.get('payment') === 'success');
  await expect(page.locator('#billing-summary .ts-billing-plan-name')).toHaveText('Pro');
  const ownResponse = await page.request.get('https://api.stripe.com/v1/checkout/sessions/' + ownId, { headers: stripeHeaders });
  expect(ownResponse.status()).toBe(200);
  const ownSession = await ownResponse.json();
  const conflictPayment = await page.request.post(conflictUrl, { maxRedirects: 0 });
  expect(conflictPayment.status()).toBe(303);
  const conflictResponse = await page.request.get('https://api.stripe.com/v1/checkout/sessions/' + conflictId, { headers: stripeHeaders });
  expect(conflictResponse.status()).toBe(200);
  const conflictSession = await conflictResponse.json();
  await page.goto(conflictPayment.headers().location);
  await expect(page.locator('#organization-checkout-notice')).toContainText('This organization already has billing.');
  await expect(page.locator('#organization-checkout-notice')).toContainText('Your payment was refunded.');
  await expect(page.locator('[data-organization-option="' + conflictOrganization.id + '"]')).toHaveCount(1);
  await page.goto('/organization?tab=billing&organizationId=' + conflictOrganization.id);
  await expect(page.locator('#organization-checkout-notice')).toHaveCount(0);
  await expect(page.locator('#billing-summary .ts-billing-plan-name')).toHaveText('Pro');
  await expect(page.locator('#billing-summary .ts-billing-plan .ts-badge')).toHaveText('Active');
  const ownSubscription = await page.request.get('https://api.stripe.com/v1/subscriptions/' + ownSession.subscription, { headers: stripeHeaders });
  expect(ownSubscription.status()).toBe(200);
  const retainedSubscription = await ownSubscription.json();
  expect(retainedSubscription.status).toBe('active');
  expect(retainedSubscription.customer).toBe(ownSession.customer);
  await page.goto('/organization?newOrganization=organization');
  await expect(wizard.getByRole('button', { name: 'Cancel pending checkout', exact: true })).toHaveCount(0);
  const conflictLog = await stripeLog();
  const conflictRefunds = conflictLog.refunds.filter((entry) => entry.subscription === conflictSession.subscription);
  expect(conflictRefunds).toHaveLength(1);
  expect(conflictRefunds[0].amount).toBe(2000);
  expect(conflictLog.cancels.filter((entry) => entry.subscription === conflictSession.subscription)).toHaveLength(1);
  expect(conflictLog.cancels.filter((entry) => entry.subscription === ownSession.subscription)).toHaveLength(0);
  expect(conflictLog.refunds.filter((entry) => entry.subscription === ownSession.subscription)).toHaveLength(0);
  expect((await owner.organizations()).filter((org) => org.name === conflictName)).toHaveLength(1);
  await audit.check('conflicting organization billing refunds only the paid draft');
  console.log('organization checkout conflicting binding: public keyed ts-auth create; separate Pro billing retained; paid draft fully refunded once; refusal shown once; organization visible in switcher; no pending checkout');
});

test('canceling organization checkout leaves no organization and preserves the wizard draft', async ({ page, audit }) => {
  const owner = await organizationOwner(page, audit);
  const before = await owner.organizations();
  const name = `Canceled ${owner.run}`;
  await page.goto('/organization?newOrganization=organization');
  const wizard = page.locator('#create-organization[open]');
  await wizard.locator('input[name=name]').fill(name);
  await wizard.getByRole('button', { name: 'Continue to billing', exact: true }).click();
  await wizard.locator('input[name=plan][value=pro]').check();
  await wizard.getByRole('button', { name: 'Continue to checkout', exact: true }).click();
  await page.waitForURL((url) => url.origin === checkout);
  expect(await owner.organizations()).toEqual(before);
  await page.getByRole('link', { name: 'Cancel', exact: true }).click();
  await page.waitForURL((url) => url.origin === dash && url.searchParams.get('payment') === 'canceled');
  await expect(wizard).toBeVisible();
  await expect(wizard.locator('input[name=name]')).toHaveValue(name);
  await expect(wizard.locator('input[name=plan][value=pro]')).toBeChecked();
  expect(await owner.organizations()).toEqual(before);
  await audit.check('canceled organization checkout keeps its draft and creates nothing');
  await wizard.getByRole('button', { name: 'Continue to checkout', exact: true }).click();
  await page.waitForURL((url) => url.origin === checkout);
  await page.goto('/organization?newOrganization=organization');
  await expect(wizard.getByRole('button', { name: 'Cancel pending checkout', exact: true })).toBeVisible();
  await wizard.getByRole('button', { name: 'Cancel pending checkout', exact: true }).click();
  await expect(wizard.locator('input[name=name]')).toHaveValue(name);
  await expect(wizard.locator('input[name=plan][value=pro]')).toBeChecked();
  expect(await owner.organizations()).toEqual(before);
  await audit.check('pending checkout canceled from organization wizard');
  console.log('organization checkout canceled: organization list unchanged; name and plan preserved in wizard; own pending checkout canceled from wizard');
});
