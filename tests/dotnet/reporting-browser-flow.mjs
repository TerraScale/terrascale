import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { chromium } from 'playwright-core';

const base = process.argv[2]?.replace(/\/$/, '');
assert(base, 'Pass the running test-only C# demo URL');
const browser = await chromium.launch({ headless: true });
const output = resolve(new URL('../../test-results/dotnet/reporting-journey', import.meta.url).pathname);
const axe = await readFile(new URL('../../node_modules/axe-core/axe.min.js', import.meta.url), 'utf8');
await mkdir(output, { recursive: true });
const contexts = [], checks = [], errors = [], accessibilityFailures = [];
const suffix = randomUUID().slice(0, 8);
let activePage;
async function session(viewport) {
  const context = await browser.newContext({ viewport }); contexts.push(context);
  const page = await context.newPage(); page.setDefaultTimeout(20000);
  page.on('pageerror', error => errors.push({ url: page.url(), message: error.message }));
  return { context, page };
}
async function goto(page, path, status = 200) {
  activePage = page; const response = await page.goto(base + path, { waitUntil: 'domcontentloaded' });
  assert.equal(response.status(), status, path); return response;
}
async function submit(page, selector, button) {
  activePage = page; const form = page.locator(selector); const path = new URL(await form.getAttribute('action'), base).pathname;
  const [response] = await Promise.all([page.waitForResponse(value => new URL(value.url()).pathname === path && value.request().method() === 'POST'), page.waitForNavigation({ waitUntil: 'domcontentloaded' }), form.getByRole('button', { name: button, exact: true }).click()]);
  return response;
}
async function me(context) { const response = await context.request.get(base + '/api/v1/auth/me'); assert.equal(response.status(), 200); return response.json(); }
async function post(context, page, path, form = {}, status = 303) {
  const csrf = await page.locator('input[name=_csrf]').first().inputValue();
  const response = await context.request.post(base + path, { form: { _csrf: csrf, ...form }, maxRedirects: 0 }); assert.equal(response.status(), status, path); return response;
}
async function scenario(owner, projectId, value) { await post(owner.context, owner.page, '/__test/reporting/scenario', { project: projectId, scenario: value }, 204); }
async function shot(page, name) {
  activePage = page; await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: resolve(output, name + '.png'), fullPage: true, caret: 'hide' });
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 2), name + ' has no horizontal body overflow');
  await page.evaluate(source => { const script = document.createElement('script'); script.nonce = document.querySelector('script[nonce]').nonce; script.textContent = source; document.head.append(script); }, axe);
  const result = await page.evaluate(() => axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] } }));
  await writeFile(resolve(output, name + '-axe.json'), JSON.stringify(result.violations, null, 2));
  if (result.violations.length) accessibilityFailures.push({ screen: name, violations: result.violations.map(value => ({ id: value.id, targets: value.nodes.map(node => node.target) })) });
}
async function signup(page, user) {
  await goto(page, '/onboarding'); await page.locator('#displayName').fill(user.name); await page.locator('#email').fill(user.email);
  await page.locator('#password').fill(user.password); await page.locator('#confirmPassword').fill(user.password); await page.locator('#terms').check();
  assert.equal((await submit(page, 'form[action="/onboarding"]', 'Create account')).status(), 200);
  await page.locator('#code').fill('123456'); await submit(page, 'form[action="/login/code/verify"]', 'Continue');
  await submit(page, 'form[action="/onboarding/step"]', 'Save and continue'); await submit(page, 'form[action="/onboarding/step"]', 'Continue on Free');
  await page.locator('#projectName').fill(user.name + ' Project'); await page.locator('#region').selectOption('GRU'); await submit(page, 'form[action="/onboarding/step"]', 'Create project and open Dashboard');
}
async function database(owner, name, placement) {
  await goto(owner.page, '/databases'); await owner.page.getByRole('button', { name: 'New database', exact: true }).first().click();
  await owner.page.locator('#create-database[open]').waitFor(); await owner.page.locator('#database-name').fill(name);
  for (const region of ['GRU', 'SFO']) await owner.page.locator('#database-region input[value="' + region + '"]').setChecked(placement.includes(region));
  await submit(owner.page, '#create-database form', 'Create database'); return new URL(owner.page.url()).pathname.split('/')[2];
}
function stat(page, card, name) { return page.locator(card + ' .ts-stat').filter({ has: page.locator('dt', { hasText: new RegExp('^' + name + '$') }) }).locator('dd.ts-stat-value'); }

try {
  const owner = await session({ width: 1440, height: 900 });
  const guest = await session({ width: 390, height: 844 });
  const ownerUser = { name: 'Reporting ' + suffix, email: `reporting-${suffix}@example.test`, password: 'e2e-password-' + randomUUID() };
  const guestUser = { name: 'Reader ' + suffix, email: `report-reader-${suffix}@example.test`, password: 'e2e-password-' + randomUUID() };
  await signup(owner.page, ownerUser); await signup(guest.page, guestUser);
  const personal = await me(owner.context);
  await goto(owner.page, '/organization?newOrganization=organization'); await owner.page.locator('#create-organization input[name=name]').fill('Reporting company ' + suffix);
  await submit(owner.page, '#create-organization form', 'Create organization'); await submit(owner.page, '#create-organization form', 'Continue to checkout');
  await owner.page.locator('#create-organization input[name=name]').fill('Measured project'); await owner.page.locator('#create-organization select[name=regions]').selectOption(['GRU', 'SFO']);
  await owner.page.locator('#create-organization select[name=region]').selectOption('GRU'); await owner.page.locator('#create-organization select[name=backupRegion]').selectOption('SFO');
  await submit(owner.page, '#create-organization form', 'Create project'); const original = await me(owner.context);
  const orders = await database(owner, 'orders-' + suffix, ['GRU', 'SFO']); const archive = await database(owner, 'archive-' + suffix, ['SFO']);
  await goto(owner.page, '/analytics?database=' + orders + '&region=GRU&range=24h');
  assert.equal(await stat(owner.page, '#analytics-summary', 'Requests').innerText(), '10'); assert.equal(await stat(owner.page, '#analytics-summary', 'Storage').innerText(), '4.0 KiB');
  assert.equal(await stat(owner.page, '#analytics-summary', 'Worst p95 latency').innerText(), '1.750 ms');
  assert.equal(await owner.page.locator('main .ts-grid-2 > .ts-card').count(), 7); await shot(owner.page, 'analytics-desktop');
  const chart = owner.page.locator('main .ts-grid-2 .ts-chart').first(); const latest = chart.locator('.ts-chart-column[tabindex="0"]');
  await latest.focus(); await owner.page.keyboard.press('Home'); assert.equal(await chart.locator('.ts-chart-column').first().getAttribute('tabindex'), '0');
  await owner.page.keyboard.press('End'); assert.equal(await chart.locator('.ts-chart-column').last().getAttribute('tabindex'), '0');
  for (const range of ['1h', '24h', '7d', '30d']) { await owner.page.locator('#analytics-range').selectOption(range); await Promise.all([owner.page.waitForNavigation({ waitUntil: 'domcontentloaded' }), owner.page.locator('form[action="/analytics"]').getByRole('button', { name: 'Apply', exact: true }).click()]); assert.equal(new URL(owner.page.url()).searchParams.get('range'), range); }
  await owner.page.locator('#analytics-database').selectOption(archive); await Promise.all([owner.page.waitForNavigation({ waitUntil: 'domcontentloaded' }), owner.page.locator('form[action="/analytics"]').getByRole('button', { name: 'Apply', exact: true }).click()]);
  assert.equal(await owner.page.locator('#analytics-region').inputValue(), '', 'native database changes clear a stale region');
  assert.equal((await me(owner.context)).projectId, original.projectId, 'reporting filters do not save a new selected project');
  checks.push('native Analytics range/database/region filters, measured counters/latest gauges and Datastar chart keyboard navigation');
  await goto(owner.page, '/analytics?database=' + orders); await scenario(owner, original.projectId, 'missing-latency'); await goto(owner.page, '/analytics?database=' + orders);
  assert.equal(await stat(owner.page, '#analytics-summary', 'Worst p95 latency').innerText(), 'Not reported');
  const latency = owner.page.locator('main .ts-card').filter({ has: owner.page.getByRole('heading', { name: 'Approximate request latency percentiles (µs)', exact: true }) });
  assert.equal(await latency.locator('.ts-chart-marker').count(), 0); await latency.getByText('No samples for Approximate request latency percentiles (µs).', { exact: true }).waitFor();
  await scenario(owner, original.projectId, 'zero'); await goto(owner.page, '/analytics?database=' + orders); assert.equal(await stat(owner.page, '#analytics-summary', 'Requests').innerText(), '0'); assert.equal(await stat(owner.page, '#analytics-summary', 'Worst p95 latency').innerText(), 'Not reported');
  for (const value of ['foreign-project', 'foreign-database', 'foreign-region', 'negative', 'oversized', 'offline']) { await scenario(owner, original.projectId, value); await goto(owner.page, '/analytics?database=' + orders); await owner.page.getByRole('alert').filter({ hasText: 'Analytics unavailable' }).waitFor(); assert.equal(await owner.page.locator('#analytics-summary .ts-stat').count(), 0, value + ' does not fabricate a zero total'); }
  await scenario(owner, original.projectId, 'received-only'); await goto(owner.page, '/analytics?database=' + orders);
  assert.equal(await stat(owner.page, '#analytics-summary', 'Replication sent').innerText(), '0 B');
  assert.equal(await stat(owner.page, '#analytics-summary', 'Replication received').innerText(), '9 B');
  await scenario(owner, original.projectId, 'not-metered'); await goto(owner.page, '/analytics'); await owner.page.getByText('No analytics measurements for these filters.', { exact: true }).waitFor();
  await scenario(owner, original.projectId, 'valid'); await goto(owner.page, '/usage?project=' + original.projectId + '&database=' + orders + '&range=24h');
  assert.equal(await stat(owner.page, '#usage-summary', 'Reads').innerText(), '12'); assert.equal(await stat(owner.page, '#usage-summary', 'Writes').innerText(), '7'); assert.equal(await stat(owner.page, '#usage-summary', 'Logical storage').innerText(), '4.0 KiB');
  assert.equal(await owner.page.locator('#replication tbody tr').count(), 2); await shot(owner.page, 'usage-desktop');
  for (const value of ['no-period', 'foreign-project', 'foreign-database', 'negative', 'oversized', 'offline', 'unauthorized']) {
    await scenario(owner, original.projectId, value); await goto(owner.page, '/usage?database=' + orders);
    assert.equal(await stat(owner.page, '#usage-summary', 'Reads').count(), 0, value + ' never presents lifetime reads as period usage');
    assert.equal(await stat(owner.page, '#usage-summary', 'Writes').count(), 0, value + ' never presents lifetime writes as period usage');
    assert.equal(await stat(owner.page, '#usage-summary', 'Logical storage').innerText(), '0 B', value + ' keeps the independently read empty database storage known');
  }
  await scenario(owner, original.projectId, 'valid'); checks.push('project-scoped analytics contract, tenant/scope/oversize failures and null-vs-zero observations through running routes');
  await goto(owner.page, '/usage?tab=settings'); await owner.page.locator('#organization-spend-cap').fill('50.25'); await owner.page.locator('[id="project-spend-cap-' + original.projectId + '"]').fill('20.00');
  await owner.page.locator('[id="database-spend-cap-' + orders + '"]').fill('10.00'); await owner.page.locator('[id="database-read-limit-' + orders + '"]').fill('12345'); await owner.page.locator('#paid-excess').check();
  assert.equal((await submit(owner.page, '#spending-controls', 'Save spending controls')).status(), 303); await owner.page.getByRole('alert').filter({ hasText: 'Changes saved' }).waitFor();
  const persisted = await (await owner.context.request.get(base + '/api/v1/dashboard/context')).json(); const org = persisted.organizations.find(item => item.id === original.organizationId);
  assert.equal(org.spendCapCents, 5025); assert.equal(org.projects[original.projectId].spendCapCents, 2000); assert.equal(org.projects[original.projectId].databases[orders].spendCapCents, 1000); assert.equal(org.projects[original.projectId].databases[orders].operationLimits.readLimit, 12345);
  await shot(owner.page, 'spending-desktop');
  const budgetResponse = await post(owner.context, owner.page, '/api/v1/dashboard/usage/spending-controls', { spendCap: '-1' }); assert.equal(budgetResponse.headers()['location'], '/usage?tab=settings&saved=invalid');
  await post(owner.context, owner.page, '/api/v1/dashboard/settings/audit-retention', { retentionDays: '0' }, 400); await post(owner.context, owner.page, '/api/v1/dashboard/settings/audit-retention', { retentionDays: '30' });
  const provider = await (await owner.context.request.get(base + '/__test/reporting/probe')).json(); assert(provider.requests.some(request => request.path.endsWith('/audit-retention')), 'native retention mutation reaches the real SDK producer request');
  await post(owner.context, owner.page, '/api/v1/dashboard/settings/backup-now', { databaseId: orders }, 503);
  await post(owner.context, owner.page, '/api/v1/dashboard/settings/restore', { databaseId: orders, backupId: 'demo-backup' }, 400);
  await post(owner.context, owner.page, '/api/v1/dashboard/settings/delete-project', { projectId: original.projectId, confirmName: 'Measured project' }, 409);
  checks.push('native persistent spending caps/operation limits, effective minimum budgets, invalid input and real SDK audit-retention with honest backup/protected-delete gaps');
  await goto(owner.page, '/organization?tab=members'); await owner.page.locator('#invite-email').fill(guestUser.email); await owner.page.locator('#invite-role').selectOption('member'); await submit(owner.page, '#team-invite form', 'Create invitation');
  const invitation = new URL(await owner.page.locator('#invitation-link').getAttribute('href')).pathname; await goto(guest.page, invitation); await submit(guest.page, 'form[action$="/respond"]', 'Accept invitation');
  await goto(guest.page, '/usage?tab=settings'); assert.equal(await guest.page.getByRole('button', { name: 'Save spending controls', exact: true }).count(), 0); assert(await guest.page.locator('#organization-spend-cap').isDisabled()); await post(guest.context, guest.page, '/api/v1/dashboard/usage/spending-controls', { spendCap: '1.00' }, 403);
  await post(guest.context, guest.page, '/api/v1/dashboard/settings/audit-retention', { retentionDays: '30' }, 403); await shot(guest.page, 'spending-member-mobile');
  await owner.page.setViewportSize({ width: 390, height: 844 }); for (const [path, name] of [['/analytics', 'analytics-mobile'], ['/usage', 'usage-mobile'], ['/usage?tab=settings', 'spending-owner-mobile']]) { await goto(owner.page, path); await shot(owner.page, name); }
  checks.push('fresh member permissions and desktop/mobile WCAG 2.2 accessibility for measured reporting and native spending controls');
  assert.deepEqual(errors, [], 'no browser or Datastar errors'); assert.deepEqual(accessibilityFailures, [], 'all reporting accessibility checks pass');
  await writeFile(resolve(output, 'report.json'), JSON.stringify({ passed: true, base, checks, browserErrors: errors, accessibilityFailures, gaps: ['Producer replication contract has no historical bandwidth samples; the UI shows that state explicitly.'] }, null, 2));
  console.log('PASS: ' + checks.join('; '));
} catch (error) {
  if (activePage && !activePage.isClosed()) { const probe = await activePage.context().request.get(base + '/__test/reporting/probe'); await writeFile(resolve(output, 'failure-provider.json'), await probe.text()); }
  if (activePage && !activePage.isClosed()) { await activePage.screenshot({ path: resolve(output, 'failure.png'), fullPage: true }); await writeFile(resolve(output, 'failure.html'), await activePage.content()); console.error('Failed at', activePage.url()); }
  await writeFile(resolve(output, 'report.json'), JSON.stringify({ passed: false, base, checks, browserErrors: errors, accessibilityFailures, failure: error.stack }, null, 2)); throw error;
} finally { for (const context of contexts) await context.close(); await browser.close(); }
