import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { chromium } from 'playwright-core';

const base = process.argv[2]?.replace(/\/$/, ''); assert(base, 'Pass the running test-only dashboard URL');
const browser = await chromium.launch({ headless: true });
const output = resolve(new URL('../../test-results/dotnet/billing-plan-journey', import.meta.url).pathname); await mkdir(output, { recursive: true });
const axe = await readFile(new URL('../../node_modules/axe-core/axe.min.js', import.meta.url), 'utf8');
const owner = await browser.newContext({ viewport: { width: 1440, height: 900 } }); const page = await owner.newPage(); page.setDefaultTimeout(20000); let guest;
const errors = [], violations = [], checks = []; page.on('pageerror', error => errors.push(error.message));
const suffix = randomUUID().slice(0, 8);
async function goto(path) { const response = await page.goto(base + path, { waitUntil: 'domcontentloaded' }); assert.equal(response.status(), 200, path); }
async function submit(selector, name, expected = 303) {
  const form = page.locator(selector); const path = new URL(await form.getAttribute('action'), base).pathname;
  const [response] = await Promise.all([page.waitForResponse(value => new URL(value.url()).pathname === path && value.request().method() === 'POST'), page.waitForNavigation({ waitUntil: 'domcontentloaded' }), form.getByRole('button', { name, exact: true }).click()]);
  assert.equal(response.status(), expected, path); return response;
}
async function post(path, form = {}, expected = 303) {
  const csrf = await page.locator('input[name=_csrf]').first().inputValue();
  const response = await owner.request.post(base + path, { headers: { Origin: base }, form: { _csrf: csrf, ...form }, maxRedirects: 0 }); assert.equal(response.status(), expected, path + ': ' + await response.text()); return response;
}
async function scenario(value) { await post('/__test/billing/plans/scenario', { scenario: value }, 204); }
async function probe() { const response = await owner.request.get(base + '/__test/billing/plans/probe'); assert.equal(response.status(), 200); return (await response.json()).mutations; }
async function selected() { const me = await (await owner.request.get(base + '/api/v1/auth/me')).json(); const context = await (await owner.request.get(base + '/api/v1/dashboard/context')).json(); return context.organizations.find(org => org.id === me.organizationId); }
async function switchOrganization(id) {
  await page.setViewportSize({ width: 1440, height: 900 }); await goto('/organization?tab=billing');
  await page.locator('.ts-context-menu > summary').click();
  const [response] = await Promise.all([page.waitForResponse(value => new URL(value.url()).pathname === '/api/v1/dashboard/switch-organization' && value.request().method() === 'POST'), page.waitForNavigation({ waitUntil: 'domcontentloaded' }), page.locator('button[data-organization-option="' + id + '"]').click()]);
  assert.equal(response.status(), 303); assert.equal((await selected()).id, id);
}
async function choose(plan, interval = 'monthly') {
  await goto('/organization?tab=billing'); await page.getByRole('button', { name: 'Change plan', exact: true }).click(); await page.locator('#change-billing-plan[open]').waitFor();
  for (const [name, value] of [['plan', plan], ['interval', interval]]) {
    const selector = 'input[name=' + name + '][value=' + value + ']'; const input = page.locator('#change-billing-plan').locator(selector);
    await page.locator('#change-billing-plan label').filter({ has: page.locator(selector) }).click(); assert(await input.isChecked());
  }
  await Promise.all([page.waitForNavigation({ waitUntil: 'domcontentloaded' }), page.locator('#change-billing-plan form').getByRole('button', { name: 'Review change', exact: true }).click()]);
  await page.locator('#review-billing-plan[open]').waitFor();
}
async function confirm() { return submit('#review-billing-plan form', 'Confirm change'); }
async function screenshot(name) {
  await page.screenshot({ path: resolve(output, name + '.png'), fullPage: true, caret: 'hide' }); assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 2), name + ' has no horizontal overflow');
  await page.evaluate(source => { const script = document.createElement('script'); script.nonce = document.querySelector('script[nonce]').nonce; script.textContent = source; document.head.append(script); }, axe);
  const result = await page.evaluate(() => axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] } }));
  await writeFile(resolve(output, name + '-axe.json'), JSON.stringify(result.violations, null, 2)); if (result.violations.length) violations.push({ name, ids: result.violations.map(item => item.id) });
}
try {
  await goto('/onboarding?plan=starter'); await page.locator('#displayName').fill('Plan owner ' + suffix); await page.locator('#email').fill('plan-owner-' + suffix + '@example.test');
  const password = 'E2E-plan-' + randomUUID(); await page.locator('#password').fill(password); await page.locator('#confirmPassword').fill(password); await page.locator('#terms').check();
  await submit('form[action="/onboarding"]', 'Create account', 200); await page.locator('#code').fill('123456'); await submit('form[action="/login/code/verify"]', 'Continue');
  await submit('form[action="/onboarding/step"]', 'Save and continue');
  const starter = page.locator('input[name=plan][value=starter]'); await page.locator('label').filter({ has: starter }).click(); assert(await starter.isChecked());
  await submit('form[action="/onboarding/step"]', 'Continue to checkout');
  await page.locator('#projectName').fill('Plan project ' + suffix); await page.locator('#region').selectOption('GRU'); await submit('form[action="/onboarding/step"]', 'Create project and open Dashboard');
  await choose('pro'); assert.match(await page.locator('#review-billing-plan').innerText(), /US\$ 6\.66 estimated by Stripe \(including US\$ 1\.66 in prorations\)/); await screenshot('upgrade-review-desktop');
  await confirm(); assert.match(await page.locator('#billing-summary').innerText(), /Pro/); assert.equal((await selected()).planId, '7CNW2H87JE68ATZX224CV2G625');
  const upgraded = (await probe()).filter(path => path.startsWith('/v1/subscriptions/')).length;
  await post('/billing/change', { plan: 'pro', interval: 'monthly' }); assert.equal((await probe()).filter(path => path.startsWith('/v1/subscriptions/')).length, upgraded, 'repeated confirmed change never calls the vendor again');
  checks.push('native Stripe invoice preview, confirmed immediate upgrade and idempotent replay');
  await scenario('pending'); await choose('business', 'annual'); await confirm(); await page.getByText('Awaiting payment', { exact: true }).waitFor();
  assert.equal((await selected()).planId, '7CNW2H87JE68ATZX224CV2G625', 'pending payment keeps the current paid entitlement');
  const pending = (await probe()).filter(path => path.startsWith('/v1/subscriptions/')).length;
  await post('/billing/change', { plan: 'business', interval: 'annual' }); assert.equal((await probe()).filter(path => path.startsWith('/v1/subscriptions/')).length, pending);
  await post('/billing/change', { plan: 'starter', interval: 'monthly' }, 409); await scenario('confirm-payment'); await goto('/organization?tab=billing&payment=success'); assert.equal((await selected()).planId, '09XCWRH05BWWBADSBPTPDMFA7D');
  await choose('starter'); assert.match(await page.locator('#review-billing-plan').innerText(), /No charge today/); await confirm(); await page.getByText('Change scheduled', { exact: true }).waitFor();
  assert.equal((await selected()).planId, '09XCWRH05BWWBADSBPTPDMFA7D', 'a scheduled downgrade keeps current resources until renewal');
  const schedules = (await probe()).filter(path => path === '/v1/subscription_schedules').length;
  await post('/billing/change', { plan: 'starter', interval: 'monthly' }); assert.equal((await probe()).filter(path => path === '/v1/subscription_schedules').length, schedules);
  await post('/billing/change', { plan: 'pro', interval: 'monthly' }, 409); await submit('form[action="/billing/change/undo"]', 'Undo scheduled change'); assert.equal(await page.getByText('Change scheduled', { exact: true }).count(), 0);
  await choose('free'); await confirm(); await page.getByText('Change scheduled', { exact: true }).waitFor(); assert.match(await page.getByRole('status').filter({ hasText: 'Change scheduled' }).innerText(), /Free/);
  assert.equal((await selected()).planId, '09XCWRH05BWWBADSBPTPDMFA7D'); await submit('form[action="/billing/change/undo"]', 'Undo scheduled change');
  checks.push('pending payment cannot grant a new plan; provider confirmation, scheduled downgrade/replay/undo and Personal Free cancellation use real SDK operations');
  const personalOrganization = await selected(); const personalProject = (await (await owner.request.get(base + '/api/v1/auth/me')).json()).projectId;
  await scenario('foreign-customer'); await goto('/organization?tab=billing&review=1&plan=pro&interval=monthly'); assert.equal(await page.locator('#review-billing-plan button').count(), 0); assert.match(await page.locator('#billing-summary').innerText(), /Payment method\s+Not reported/); assert.equal((await selected()).planId, '09XCWRH05BWWBADSBPTPDMFA7D');
  await scenario('confirmed'); await page.setViewportSize({ width: 390, height: 844 }); await choose('pro'); await screenshot('downgrade-review-mobile');
  await goto('/organization?newOrganization=organization'); await page.locator('#create-organization input[name=name]').fill('Pending billing ' + suffix); await submit('#create-organization form', 'Create organization');
  await goto('/organization?tab=billing'); await post('/billing/change', { plan: 'free' }, 400);
  const started = await post('/billing/checkout', { plan: 'starter', interval: 'monthly', return: '/organization?tab=billing' }); const checkout = new URL(started.headers()['location'], base).searchParams.get('id'); assert(checkout);
  await goto('/organization?tab=billing'); await submit('form[action="/billing/checkout/cancel"]', 'Cancel checkout'); assert.equal((await selected()).billingStatus, 'billing_required');
  const ended = await owner.request.get(base + '/billing/checkout/return?session_id=' + checkout, { maxRedirects: 0 }); assert.equal(ended.status(), 303); assert.match(ended.headers()['location'], /payment=canceled/);
  await post('/checkout/foreign-checkout/cancel', {}, 404);
  const replacement = await post('/billing/checkout', { plan: 'pro', interval: 'monthly', return: '/organization?tab=billing' }); assert.notEqual(new URL(replacement.headers()['location'], base).searchParams.get('id'), checkout); await post('/billing/checkout/cancel');
  await screenshot('canceled-checkout-mobile'); checks.push('cross-customer state is rejected, Business Free is refused, checkout expiration/cancel/replacement stays bound and does not grant resources');
  const businessOrganization = await selected();
  const originalCheckout = await post('/billing/checkout', { plan: 'starter', interval: 'monthly', return: '/organization?tab=billing' }); const originalCheckoutUrl = originalCheckout.headers()['location']; const originalCheckoutId = new URL(originalCheckoutUrl, base).searchParams.get('id'); assert(originalCheckoutId);
  await switchOrganization(personalOrganization.id); assert.equal((await selected()).planId, personalOrganization.planId); assert.equal((await (await owner.request.get(base + '/api/v1/auth/me')).json()).projectId, personalProject);
  await page.goto(new URL(originalCheckoutUrl, base).href, { waitUntil: 'domcontentloaded' }); assert.match(page.url(), /\/organization\?tab=billing&payment=success$/);
  assert.equal((await selected()).id, businessOrganization.id, 'callback restores the original organization after a native shell switch'); assert.equal((await selected()).planId, '443RCMMF70MNP6S1QMT230NF99'); assert.equal((await (await owner.request.get(base + '/api/v1/auth/me')).json()).projectId, null, 'the other organization project is never adopted by the original checkout');
  const afterCallback = await (await owner.request.get(base + '/api/v1/dashboard/context')).json(); assert.equal(afterCallback.organizations.find(org => org.id === personalOrganization.id).planId, personalOrganization.planId, 'only the original organization receives the checkout entitlement');
  await switchOrganization(personalOrganization.id); const obsolete = await owner.request.get(base + '/billing/checkout/return?session_id=' + checkout, { maxRedirects: 0 }); assert.equal(obsolete.status(), 404, 'replaced checkout cannot change selection'); assert.equal((await selected()).id, personalOrganization.id);
  const repeated = await owner.request.get(base + '/billing/checkout/return?session_id=' + originalCheckoutId, { maxRedirects: 0 }); assert.equal(repeated.status(), 303); assert.match(repeated.headers()['location'], /payment=success/); assert.equal((await selected()).id, businessOrganization.id);
  checks.push('native two-organization checkout completion restores only its bound original organization, preserves the other plan/project, safely replays and rejects replaced callbacks');
  guest = await browser.newContext({ viewport: { width: 390, height: 844 } }); const guestPage = await guest.newPage(); guestPage.setDefaultTimeout(20000); guestPage.on('pageerror', error => errors.push(error.message));
  const email = 'plan-member-' + suffix + '@example.test'; await guestPage.goto(base + '/onboarding', { waitUntil: 'domcontentloaded' }); await guestPage.locator('#displayName').fill('Plan member ' + suffix); await guestPage.locator('#email').fill(email);
  await guestPage.locator('#password').fill(password); await guestPage.locator('#confirmPassword').fill(password); await guestPage.locator('#terms').check();
  async function guestSubmit(selector, label) { const form = guestPage.locator(selector); const action = new URL(await form.getAttribute('action'), base).pathname; await Promise.all([guestPage.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === action), guestPage.waitForNavigation({ waitUntil: 'domcontentloaded' }), form.getByRole('button', { name: label, exact: true }).click()]); }
  await guestSubmit('form[action="/onboarding"]', 'Create account'); await guestPage.locator('#code').fill('123456'); await guestSubmit('form[action="/login/code/verify"]', 'Continue'); await guestSubmit('form[action="/onboarding/step"]', 'Save and continue');
  await guestSubmit('form[action="/onboarding/step"]', 'Continue on Free'); await guestPage.locator('#projectName').fill('Member project ' + suffix); await guestPage.locator('#region').selectOption('GRU'); await guestSubmit('form[action="/onboarding/step"]', 'Create project and open Dashboard');
  const unrelated = await guest.request.get(base + '/billing/checkout/return?session_id=' + originalCheckoutId, { maxRedirects: 0 }); assert.equal(unrelated.status(), 404, 'an unrelated authenticated account cannot resolve the original organization');
  await goto('/organization?tab=members'); await page.locator('#invite-email').fill(email); await page.locator('#invite-role').selectOption('member'); await submit('#team-invite form', 'Create invitation', 200);
  await guestPage.goto(await page.locator('#invitation-link').getAttribute('href'), { waitUntil: 'domcontentloaded' }); await guestSubmit('form[action$="/respond"]', 'Accept invitation'); await guestPage.goto(base + '/organization?tab=billing&review=1&plan=pro&interval=monthly', { waitUntil: 'domcontentloaded' });
  assert.equal(await guestPage.locator('#review-billing-plan').count(), 0); assert.equal(await guestPage.getByRole('button', { name: 'Change plan', exact: true }).count(), 0);
  const guestCsrf = await guestPage.locator('input[name=_csrf]').first().inputValue();
  for (const action of ['/billing/change', '/billing/change/undo', '/billing/checkout/cancel']) { const denied = await guest.request.post(base + action, { headers: { Origin: base }, form: { _csrf: guestCsrf, plan: 'pro', interval: 'monthly' }, maxRedirects: 0 }); assert.equal(denied.status(), 403, 'fresh non-owner role denies ' + action); }
  const memberCallback = await guest.request.get(base + '/billing/checkout/return?session_id=' + originalCheckoutId, { maxRedirects: 0 }); assert.equal(memberCallback.status(), 403, 'an accepted Member cannot confirm or restore owner checkout scope');
  checks.push('native accepted Member sees no billing controls and cannot forge plan change, undo or checkout cancellation');
  assert.deepEqual(errors, [], 'no Datastar or browser errors'); assert.deepEqual(violations, [], 'billing dialog WCAG 2.2 checks pass');
  await writeFile(resolve(output, 'report.json'), JSON.stringify({ passed: true, checks, browserErrors: errors, accessibilityFailures: violations }, null, 2)); console.log('PASS: ' + checks.join('; '));
} catch (error) {
  await page.screenshot({ path: resolve(output, 'failure.png'), fullPage: true }); await writeFile(resolve(output, 'failure.html'), await page.content());
  await writeFile(resolve(output, 'report.json'), JSON.stringify({ passed: false, checks, browserErrors: errors, accessibilityFailures: violations, url: page.url(), failure: error.stack }, null, 2)); throw error;
} finally { if (guest) await guest.close(); await owner.close(); await browser.close(); }
