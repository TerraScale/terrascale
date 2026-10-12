import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { chromium } from 'playwright-core';

const base = process.argv[2]?.replace(/\/$/, '');
assert(base, 'Pass the running test-only C# demo URL');
const suite = process.argv[3] ?? 'all';
assert(['all', 'sso'].includes(suite), 'Supported browser suites: all, sso');
const browser = await chromium.launch({ headless: true });
const output = resolve(new URL('../../test-results/dotnet/auth-journey', import.meta.url).pathname);
const axe = await readFile(new URL('../../node_modules/axe-core/axe.min.js', import.meta.url), 'utf8');
await mkdir(output, { recursive: true });
const errors = [];
const checks = [];
let activePage;
const contexts = [];

async function newContext(options = {}) {
  const context = await browser.newContext(options);
  contexts.push(context);
  const page = await context.newPage();
  page.setDefaultTimeout(15000);
  page.on('pageerror', error => errors.push(error.message));
  activePage = page;
  return { context, page };
}
async function goto(page, path, status = 200) {
  activePage = page;
  const response = await page.goto(base + path, { waitUntil: 'domcontentloaded' });
  assert.equal(response.status(), status, path);
}
async function submit(page, form, name, { keyboard = false } = {}) {
  activePage = page;
  name = name.replace(/ →$/, '');
  const action = await page.locator(form).getAttribute('action');
  const [response] = await Promise.all([
    page.waitForResponse(value => new URL(value.url()).pathname === action && value.request().method() === 'POST'),
    page.waitForNavigation({ waitUntil: 'domcontentloaded' }),
    keyboard
      ? page.locator(form).getByRole('button', { name, exact: true }).press('Enter')
      : page.locator(form).getByRole('button', { name, exact: true }).click()
  ]);
  return response;
}
async function accessible(page, name) {
  await page.evaluate(source => {
    const script = document.createElement('script');
    script.nonce = document.querySelector('script[nonce]').nonce;
    script.textContent = source;
    document.head.append(script);
  }, axe);
  const result = await page.evaluate(() => axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] } }));
  assert.deepEqual(result.violations.map(value => value.id), [], name + ' accessibility');
}
async function shot(page, name) {
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: resolve(output, name + '.png'), fullPage: true, caret: 'hide' });
}
async function autofocus() {
  const evidence = [];
  for (const [key, path, expected] of [['login', '/login', []], ['code', '/login/code', ['email']], ['reset', '/reset-password', []], ['login-rejected', '/login', ['password']]]) {
    const { context, page } = await newContext();
    let response = await page.goto(base + path);
    if (key === 'login-rejected') {
      await page.locator('#email').fill('unregistered-focus@example.com');
      await page.locator('#password').fill('wrong-password');
      response = await submit(page, 'form[action="/login"]', 'Sign in');
      assert.equal(response.status(), 401);
    }
    const raw = (await response.text()).match(/<input\b[^>]*\bautofocus(?:\s|=|\/|>)[^>]*>/g) ?? [];
    const rawIds = raw.map(input => /\bid="([^"]+)"/.exec(input)?.[1]);
    assert.deepEqual(rawIds, expected, key + ' raw HTML autofocus');
    assert.deepEqual(await page.locator('input[autofocus]').evaluateAll(inputs => inputs.map(input => input.id)), expected, key + ' DOM autofocus');
    const expectedFocus = expected[0] ?? 'body';
    await page.waitForFunction(id => (document.activeElement?.id || document.activeElement?.tagName.toLowerCase()) === id, expectedFocus);
    const activeElement = await page.evaluate(() => document.activeElement.id || document.activeElement.tagName.toLowerCase());
    evidence.push({ key, rawInputs: raw, autofocus: rawIds, activeElement });
    await context.close();
  }
  await writeFile(resolve(output, 'autofocus.json'), JSON.stringify(evidence, null, 2));
  checks.push('raw HTML and browser focus match Go login/code/reset/rejected-login rules');
  console.log('PASS: login/code/reset/rejected-login raw autofocus and activeElement');
}
async function fillSignup(page, user, override = {}) {
  const values = { name: user.name, email: user.email, password: user.password, confirm: user.password, terms: true, ...override };
  await page.locator('#displayName').fill(values.name);
  await page.locator('#email').fill(values.email);
  await page.locator('#password').fill(values.password);
  await page.locator('#confirmPassword').fill(values.confirm);
  await page.locator('#terms').setChecked(values.terms);
}
async function signup(page, user, validate, resend) {
  await goto(page, '/onboarding?plan=starter');
  await accessible(page, 'signup');
  if (validate) {
    await fillSignup(page, user, { name: '' });
    await page.getByRole('button', { name: 'Create account', exact: false }).click();
    assert.equal(await page.locator('#displayName').evaluate(input => input.validity.valueMissing), true, 'native required field blocks empty signup');
    for (const [override, field, text] of [
      [{ name: '' }, 'displayName', 'Enter your full name.'],
      [{ email: 'invalid' }, 'email', 'Enter a valid email address'],
      [{ password: 'short', confirm: 'short' }, 'password', 'Use a password with at least 12 characters.'],
      [{ confirm: 'different-password' }, 'confirmPassword', 'Passwords do not match.'],
      [{ terms: false }, 'terms', 'Accept the Terms of Service']
    ]) {
      await fillSignup(page, user, override);
      await page.locator('form[action="/onboarding"]').evaluate(form => { form.noValidate = true; });
      const response = await submit(page, 'form[action="/onboarding"]', 'Create account →');
      assert.equal(response.status(), 422, field + ' rejects before provider enrollment');
      assert((await page.locator('#' + field + '-error').innerText()).includes(text));
      assert.equal(await page.locator('#' + field).getAttribute('aria-invalid'), 'true');
      assert.equal(await page.locator('#password').inputValue(), '', 'rejected signup does not echo password');
    }
    checks.push('native signup validation and all five server field errors');
  }
  await fillSignup(page, user);
  const response = await submit(page, 'form[action="/onboarding"]', 'Create account →');
  assert.equal(response.status(), 200);
  await page.getByRole('heading', { name: 'Verify your email', exact: true }).waitFor();
  assert.equal(await page.locator('input[name=email]').inputValue(), user.email);
  assert.equal(await page.locator('form[action="/login/code/verify"] input[name=signUp]').inputValue(), '1');
  const code = page.getByRole('textbox', { name: 'Six-digit code', exact: true });
  await code.fill('000000');
  assert.equal((await submit(page, 'form[action="/login/code/verify"]', 'Continue')).status(), 401);
  await page.locator('#code-error').waitFor();
  await accessible(page, 'rejected signup verification');
  if (resend) {
    const button = page.locator('form[action="/login/code/resend"] button');
    assert.equal(await button.isDisabled(), true, 'resend countdown is enforced by native button state');
    const csrf = await page.locator('form[action="/login/code/resend"] input[name=_csrf]').inputValue();
    const tooSoon = await page.request.post(base + '/login/code/resend', { form: { _csrf: csrf } });
    assert.equal(tooSoon.status(), 429, 'server also refuses early resend');
    assert(Number(tooSoon.headers()['retry-after']) > 0);
    console.log('Waiting for the real signup resend cooldown…');
    await button.waitFor({ state: 'visible' });
    await page.waitForFunction(() => !document.querySelector('form[action="/login/code/resend"] button').disabled, null, { timeout: 65000 });
    assert.equal((await submit(page, 'form[action="/login/code/resend"]', 'Resend code')).status(), 200);
    await page.getByRole('status').filter({ hasText: 'Check your email for a new code.' }).waitFor();
    checks.push('signup resend countdown, server cooldown and successful resend');
  }
  await page.getByRole('textbox', { name: 'Six-digit code', exact: true }).fill('123456');
  assert.equal((await submit(page, 'form[action="/login/code/verify"]', 'Continue')).status(), 303);
  await page.getByRole('heading', { name: 'Review your account information', exact: true }).waitFor();
  assert.equal(await page.locator('#displayName').inputValue(), user.name, 'registered name is persisted after verification');
  assert.equal(await page.locator('#accountEmail').inputValue(), user.email);
  checks.push('signup wrong-code rejection and successful verification');
}
async function onboarding(context, page, user, paid, size) {
  user.name += ' Updated';
  await page.locator('#displayName').fill(user.name);
  await submit(page, 'form[action="/onboarding/step"]', 'Save and continue →');
  await page.getByRole('heading', { name: 'Choose your plan', exact: true }).waitFor();
  await goto(page, '/onboarding');
  await page.getByRole('heading', { name: 'Choose your plan', exact: true }).waitFor();
  await accessible(page, 'onboarding plans');
  if (paid) {
    await goto(page, '/onboarding?payment=success&plan=starter');
    await page.getByRole('alert').filter({ hasText: 'Payment setup could not be confirmed.' }).waitFor();
    await goto(page, '/onboarding?payment=canceled&plan=starter');
    await page.getByRole('status').filter({ hasText: 'Payment setup canceled' }).waitFor();
    assert.equal(await page.locator('input[name=plan][value=starter]').isChecked(), true, 'cancel keeps selected plan');
    await page.locator('.ts-segmented label').filter({ hasText: 'Annual' }).click();
    assert.equal(await page.locator('input[name=interval][value=annual]').isChecked(), true);
    await page.locator('label.ts-onboarding-plan').filter({ has: page.locator('input[name=plan][value=pro]') }).click();
    assert.equal(await page.locator('input[name=plan][value=pro]').isChecked(), true);
    await page.locator('label.ts-onboarding-plan').filter({ has: page.locator('input[name=plan][value=starter]') }).click();
    assert.equal(await page.locator('input[name=plan][value=starter]').isChecked(), true);
    const checkout = await submit(page, 'form[action="/onboarding/step"]', 'Continue to checkout →');
    assert.equal(checkout.status(), 303);
    assert.match(checkout.headers()['location'], /\/__test\/checkout\?id=cs_/, 'official Stripe SDK uses test-only provider checkout');
    checks.push('paid annual checkout, unconfirmed payment rejection and cancellation recovery');
  } else {
    await submit(page, 'form[action="/onboarding/step"]', 'Continue on Free');
    checks.push('free personal onboarding bypass');
  }
  await page.getByRole('heading', { name: 'Create your first project', exact: true }).waitFor();
  await accessible(page, 'onboarding project');
  await shot(page, 'project-' + size);
  await page.locator('#projectName').fill('Browser ' + size + ' Project');
  if (!paid) {
    await page.locator('#region').evaluate(select => { select.add(new Option('Invalid', 'MARS')); select.value = 'MARS'; });
    await submit(page, 'form[action="/onboarding/step"]', 'Create project and open Dashboard →');
    await page.getByRole('alert').filter({ hasText: 'Choose a supported project region.' }).waitFor();
    await page.locator('#projectName').fill('Browser ' + size + ' Project');
    checks.push('unsupported project region is rejected without advancing onboarding');
  }
  await page.locator('#region').selectOption(paid ? 'SFO' : 'GRU');
  await submit(page, 'form[action="/onboarding/step"]', 'Create project and open Dashboard →');
  await page.waitForURL(base + '/');
  const me = await (await context.request.get(base + '/api/v1/auth/me')).json();
  assert.equal(me.email, user.email);
  assert.equal(me.name, user.name);
  assert.equal(me.onboardingCompleted, true);
  assert(me.organizationId && me.projectId && me.role === 'owner');
  await goto(page, '/onboarding');
  assert.equal(page.url(), base + '/', 'completed onboarding redirects to dashboard');
  await shot(page, 'complete-' + size);
  await goto(page, '/organization?tab=billing');
  const summary = await page.locator('#billing-summary').innerText();
  assert(summary.includes(paid ? 'Starter' : 'Free'), 'confirmed plan is persisted');
  if (paid) assert(summary.includes('On file'), 'confirmed Stripe subscription stores payment status');
  if (paid) assert(summary.includes('Annual'), 'confirmed subscription retains selected annual interval');
  checks.push(size + ' completed account/project persistence and billing');
}
async function login(page, user, password = user.password) {
  await goto(page, '/login?returnTo=%2Fapi%2Fv1%2Fauth%2Fme');
  await page.locator('#email').fill(user.email);
  await page.locator('#password').fill(password);
  const response = await submit(page, 'form[action="/login"]', 'Sign in');
  return response;
}
async function logout(page, keyboard = false) {
  await goto(page, '/');
  if (!await page.locator('details.ts-account summary').isVisible()) {
    await page.getByRole('button', { name: 'Open navigation', exact: true }).click();
  }
  await page.locator('details.ts-account summary').click();
  assert.equal((await submit(page, 'form[action="/api/v1/auth/logout"]', 'Sign out', { keyboard })).status(), 303);
  assert.match(new URL(page.url()).pathname, /^\/login$/);
  assert.equal((await page.request.get(base + '/api/v1/auth/me')).status(), 401);
}
async function reset(context, page, user) {
  const old = user.password;
  const changed = 'reset-' + randomUUID();
  await goto(page, '/reset-password');
  await page.locator('#email').fill(user.email);
  await submit(page, 'form[action="/reset-password"]', 'Email me a code');
  await page.locator('#code').fill('000000');
  await page.locator('#password').fill(changed);
  await page.locator('#confirmPassword').fill(changed);
  assert.equal((await submit(page, 'form[action="/reset-password/complete"]', 'Change password')).status(), 401);
  await page.locator('#code-error').waitFor();
  await page.locator('#code').fill('123456');
  await page.locator('#password').fill(changed);
  await page.locator('#confirmPassword').fill('mismatching-password');
  assert.equal((await submit(page, 'form[action="/reset-password/complete"]', 'Change password')).status(), 422);
  await page.locator('#confirmPassword-error').waitFor();
  await page.locator('#code').fill('123456');
  await page.locator('#password').fill(changed);
  await page.locator('#confirmPassword').fill(changed);
  assert.equal((await submit(page, 'form[action="/reset-password/complete"]', 'Change password')).status(), 303);
  await page.getByRole('status').filter({ hasText: 'Your password was changed.' }).waitFor();
  assert.equal((await context.request.get(base + '/api/v1/auth/me')).status(), 401, 'password reset invalidates earlier browser grant');
  assert.equal((await login(page, user, old)).status(), 401, 'old password cannot sign in');
  user.password = changed;
  assert.equal((await login(page, user)).status(), 303);
  await page.waitForURL(base + '/api/v1/auth/me');
  checks.push('reset wrong-code/mismatch rejection, success, old-grant invalidation and new-password login');
}
async function cli(context, page, user) {
  const publicContext = await browser.newContext();
  contexts.push(publicContext);
  const client = publicContext.request;
  const issue = async () => {
    const response = await client.post(base + '/api/v1/dashboard/cli/device-authorization', { form: { client_id: 'terrascale-cli' } });
    assert.equal(response.status(), 200);
    const grant = await response.json();
    assert.equal(grant.expires_in, 600);
    assert.equal(grant.interval, 5);
    assert.equal(grant.verification_uri, base + '/cli/approve');
    return grant;
  };
  const poll = grant => client.post(base + '/api/v1/dashboard/cli/token', { form: { client_id: 'terrascale-cli', grant_type: 'urn:ietf:params:oauth:grant-type:device_code', device_code: grant.device_code } });
  let grant = await issue();
  const tooSoon = await poll(grant);
  assert.equal(tooSoon.status(), 400);
  assert.equal((await tooSoon.json()).error, 'slow_down');
  const anonymous = await publicContext.newPage();
  await anonymous.goto(grant.verification_uri_complete);
  await anonymous.getByRole('link', { name: 'Sign in to approve', exact: true }).waitFor();
  await goto(page, '/cli/approve?user_code=' + grant.user_code);
  await page.locator('#cliPassword').fill('wrong-password');
  await submit(page, 'form[action="/cli/approve/submit"]', 'Authorize CLI');
  await page.getByRole('alert').filter({ hasText: 'Current password is incorrect.' }).waitFor();
  await page.locator('#cliPassword').fill(user.password);
  const reveal = page.getByRole('button', { name: 'Show password', exact: true });
  await reveal.click();
  assert.equal(await page.locator('#cliPassword').getAttribute('type'), 'text');
  await page.getByRole('button', { name: 'Hide password', exact: true }).click();
  await submit(page, 'form[action="/cli/approve/submit"]', 'Authorize CLI');
  await page.getByRole('status').filter({ hasText: 'CLI authorized' }).waitFor();
  await shot(page, 'cli-authorized');
  await page.waitForTimeout(11000);
  const polls = await Promise.all([poll(grant), poll(grant)]);
  assert.equal(polls.filter(response => response.status() === 200).length, 1, 'concurrent public polls return one token');
  const delivered = await polls.find(response => response.status() === 200).json();
  assert.match(delivered.access_token, /^tspat_[A-Za-z0-9_-]{43}$/);
  assert.equal(delivered.token_type, 'Bearer');
  const mcp = () => client.post(base + '/mcp', { headers: { Authorization: 'Bearer ' + delivered.access_token, Accept: 'application/json, text/event-stream' }, data: { jsonrpc: '2.0', id: 1, method: 'tools/list', params: {} } });
  assert.equal((await mcp()).status(), 200);
  const replay = await poll(grant);
  assert.equal(replay.status(), 400);
  assert.equal((await replay.json()).error, 'invalid_grant');
  await logout(page);
  assert.equal((await mcp()).status(), 200, 'independent CLI grant survives browser logout');
  assert.equal((await login(page, user)).status(), 303);
  grant = await issue();
  await goto(page, '/cli/approve?user_code=' + grant.user_code);
  await submit(page, 'form[action="/cli/approve/submit"]', 'Deny request');
  await page.getByRole('status').filter({ hasText: 'Request denied' }).waitFor();
  await page.waitForTimeout(5300);
  assert.equal((await (await poll(grant)).json()).error, 'access_denied');
  grant = await issue();
  await goto(page, '/cli/approve?user_code=' + grant.user_code);
  await page.locator('#cliPassword').fill(user.password);
  await submit(page, 'form[action="/cli/approve/submit"]', 'Authorize CLI');
  await logout(page);
  await page.waitForTimeout(5300);
  assert.equal((await (await poll(grant)).json()).error, 'access_denied', 'browser logout before redemption invalidates approval');
  assert.equal((await login(page, user)).status(), 303);
  await goto(page, '/profile?tab=access-tokens');
  const row = page.locator('#profile-access-tokens tbody tr').filter({ hasText: 'TerraScale CLI' });
  assert.equal(await row.count(), 1, 'only successful single-use flow created a token');
  await row.getByRole('button', { name: 'Revoke', exact: true }).click();
  await submit(page, 'dialog[open] form', 'Revoke token');
  assert.equal((await mcp()).status(), 401, 'native profile revocation invalidates CLI token');
  checks.push('CLI anonymous/reauth/password-toggle approval, slow poll, single use, denial, logout binding, MCP and revocation');
}
async function sso() {
  const { context, page } = await newContext();
  await goto(page, '/login/callback?state=forged&code=test-code', 400);
  await page.getByRole('status').filter({ hasText: 'This sign-in link expired or was started in another browser.' }).waitFor();
  await accessible(page, 'SSO recovery');
  await page.getByRole('link', { name: 'Try again', exact: true }).click();
  assert.equal(new URL(page.url()).searchParams.get('prompt'), 'login');
  await goto(page, '/login?returnTo=%2Fapi%2Fv1%2Fauth%2Fme');
  const callbackRequest = page.waitForRequest(request => new URL(request.url()).pathname === '/login/callback' && new URL(request.url()).searchParams.get('code') === 'test-code');
  await page.getByRole('link', { name: 'Single sign-on', exact: true }).click();
  const callbackUrl = new URL((await callbackRequest).url());
  assert.match(callbackUrl.searchParams.get('state'), /^[a-f0-9]{64}$/, 'test-only OIDC producer returns the issued state');
  const callback = callbackUrl.pathname + callbackUrl.search;
  await page.waitForURL(base + '/api/v1/auth/me');
  assert.equal((await context.request.get(base + '/api/v1/auth/me')).status(), 200);
  await goto(page, callback, 400);
  await page.getByRole('status').filter({ hasText: 'This sign-in link expired or was started in another browser.' }).waitFor();
  await logout(page);
  checks.push('SSO browser recovery, test-only producer callback, state verification and single-use callback');
}

try {
  if (suite === 'all') {
  await autofocus();
  for (const [size, viewport, paid] of [['desktop', { width: 1440, height: 900 }, false], ['mobile', { width: 390, height: 844 }, true]]) {
    const { context, page } = await newContext({ viewport });
    const key = randomUUID();
    const user = { name: 'Browser ' + size + ' User', email: 'browser-' + key + '@example.com', password: 'signup-' + key };
    await signup(page, user, !paid, !paid);
    console.log('PASS: ' + size + ' signup and verification');
    await onboarding(context, page, user, paid, size);
    console.log('PASS: ' + size + (paid ? ' paid annual' : ' Free') + ' onboarding and persisted billing');
    if (!paid) {
      await reset(context, page, user);
      await cli(context, page, user);
      console.log('PASS: reset and full CLI lifecycle');
      const native = await newContext({ javaScriptEnabled: false });
      assert.equal((await login(native.page, user)).status(), 303);
      await native.page.waitForURL(base + '/api/v1/auth/me');
      await logout(native.page, true);
      checks.push('new-account login/logout work with JavaScript disabled');
      console.log('PASS: JavaScript-disabled native login and keyboard logout');
    }
    await logout(page);
  }
  }
  await sso();
  assert.deepEqual(errors, [], 'no browser script errors');
  await writeFile(resolve(output, suite === 'all' ? 'report.json' : 'sso-report.json'), JSON.stringify({ passed: true, base, suite, checks }, null, 2));
  console.log('PASS: ' + checks.join('; '));
} catch (error) {
  if (activePage && !activePage.isClosed()) {
    await activePage.screenshot({ path: resolve(output, 'failure.png'), fullPage: true }).catch(() => {});
    await writeFile(resolve(output, 'failure.html'), await activePage.content()).catch(() => {});
  }
  await writeFile(resolve(output, suite === 'all' ? 'report.json' : 'sso-report.json'), JSON.stringify({ passed: false, base, suite, url: activePage?.url(), checks, errors, error: error.message }, null, 2));
  throw error;
} finally {
  await Promise.allSettled(contexts.map(context => context.close()));
  await browser.close();
}
