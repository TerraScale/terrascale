import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { chromium } from 'playwright-core';

const base = process.argv[2]?.replace(/\/$/, '');
assert(base, 'Pass the running test-only C# demo URL');
const browser = await chromium.launch({ headless: true });
const output = resolve(new URL('../../test-results/dotnet/account-journey', import.meta.url).pathname);
const axe = await readFile(new URL('../../node_modules/axe-core/axe.min.js', import.meta.url), 'utf8');
await mkdir(output, { recursive: true });
const checks = [], gaps = [], errors = [], contexts = [], accessibilityFailures = [];
let activePage;
const slug = randomUUID().slice(0, 8);
const ownerUser = { name: 'Account QA ' + slug, email: `account-owner-${slug}@example.test`, password: 'qa-password-' + randomUUID() };
const guestUser = { name: 'Invite QA ' + slug, email: `account-guest-${slug}@example.test`, password: 'qa-password-' + randomUUID() };
async function session(viewport) {
  const context = await browser.newContext({ viewport, acceptDownloads: true });
  contexts.push(context);
  const page = await context.newPage();
  page.setDefaultTimeout(15000);
  page.on('pageerror', error => errors.push({ url: page.url(), message: error.message }));
  return { context, page };
}
async function goto(page, path, status = 200) {
  activePage = page;
  const response = await page.goto(base + path, { waitUntil: 'domcontentloaded' });
  assert.equal(response.status(), status, path);
  if (status === 200) assert(await page.locator('main').count() > 0, 'full page body exists after navigation');
  return response;
}
async function submit(page, selector, button) {
  activePage = page;
  const form = page.locator(selector);
  const path = new URL(await form.getAttribute('action'), base).pathname;
  const [response] = await Promise.all([
    page.waitForResponse(value => new URL(value.url()).pathname === path && value.request().method() === 'POST'),
    page.waitForNavigation({ waitUntil: 'domcontentloaded' }),
    form.getByRole('button', { name: button, exact: true }).click()
  ]);
  if (path.endsWith('/respond') || path.endsWith('/switch-account')) { assert.equal(response.request().headers()['origin'], new URL(base).origin, 'invitation native POST retains its actual origin'); assert(!response.request().headers()['referer']?.includes('/invitations/'), 'invitation token path is absent from the referrer'); }
  return response;
}
async function csrf(page) { return page.locator('input[name=_csrf]').first().inputValue(); }
async function me(context) { const response = await context.request.get(base + '/api/v1/auth/me'); assert.equal(response.status(), 200); return response.json(); }
async function post(context, page, path, form = {}, status = 303) {
  const response = await context.request.post(base + path, { form: { _csrf: await csrf(page), ...form }, maxRedirects: 0 });
  assert.equal(response.status(), status, path);
  return response;
}
async function shot(page, name, accessibility = false) {
  activePage = page;
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: resolve(output, name + '.png'), fullPage: true, caret: 'hide' });
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 2), name + ' body has no horizontal overflow');
  if (accessibility) {
    await page.evaluate(source => { const script = document.createElement('script'); script.nonce = document.querySelector('script[nonce]').nonce; script.textContent = source; document.head.append(script); }, axe);
    const result = await page.evaluate(() => axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] } }));
    await writeFile(resolve(output, name + '-axe.json'), JSON.stringify(result.violations, null, 2));
    if (result.violations.length) accessibilityFailures.push({ screen: name, violations: result.violations.map(value => ({ id: value.id, targets: value.nodes.map(node => node.target) })) });
  }
}
async function signup(page, user) {
  // Same native signup/confirmation/free-onboarding sequence as auth-browser-flow.
  await goto(page, '/onboarding');
  await page.locator('#displayName').fill(user.name); await page.locator('#email').fill(user.email);
  await page.locator('#password').fill(user.password); await page.locator('#confirmPassword').fill(user.password); await page.locator('#terms').check();
  assert.equal((await submit(page, 'form[action="/onboarding"]', 'Create account')).status(), 200);
  await page.getByRole('heading', { name: 'Verify your email', exact: true }).waitFor();
  await page.locator('#code').fill('123456');
  assert.equal((await submit(page, 'form[action="/login/code/verify"]', 'Continue')).status(), 303);
  await page.getByRole('heading', { name: 'Review your account information', exact: true }).waitFor();
  await submit(page, 'form[action="/onboarding/step"]', 'Save and continue');
  await submit(page, 'form[action="/onboarding/step"]', 'Continue on Free');
  await page.locator('#projectName').fill(user.name + ' Project'); await page.locator('#region').selectOption('GRU');
  await submit(page, 'form[action="/onboarding/step"]', 'Create project and open Dashboard');
  await page.waitForURL(base + '/');
}
async function invite(page, email) {
  await goto(page, '/organization?tab=members');
  await page.locator('#invite-email').fill(email); await page.locator('#invite-role').selectOption('member');
  assert.equal((await submit(page, '#team-invite form', 'Create invitation')).status(), 200);
  const link = await page.locator('#invitation-link').getAttribute('href');
  assert.equal(new URL(link).origin, new URL(base).origin);
  return new URL(link).pathname;
}
async function signin(page, user, invitationPath) {
  await goto(page, '/login?returnTo=' + encodeURIComponent(invitationPath));
  await page.locator('#email').fill(user.email); await page.locator('#password').fill(user.password);
  assert.equal((await submit(page, 'form[action="/login"]', 'Sign in')).status(), 303);
  await page.waitForURL(base + invitationPath, { waitUntil: 'domcontentloaded' });
}
async function switchOrganization(context, page, id) {
  await post(context, page, '/api/v1/dashboard/switch-organization', { organizationId: id, returnUrl: '/organization' });
  await goto(page, '/organization');
  assert.equal((await me(context)).organizationId, id);
}

try {
  const owner = await session({ width: 1440, height: 900 });
  const guest = await session({ width: 390, height: 844 });
  await signup(owner.page, ownerUser);
  await signup(guest.page, guestUser);
  const original = await me(owner.context), originalGuest = await me(guest.context);
  assert.notEqual(original.organizationId, originalGuest.organizationId, 'isolated accounts have distinct actual scopes');
  assert.notEqual(original.userId, originalGuest.userId);
  checks.push('isolated owner/guest signup, email confirmation and persisted native free onboarding');
  await goto(owner.page, '/profile');
  await owner.page.locator('#displayName').fill(ownerUser.name + ' Updated'); await owner.page.locator('input[name=phoneNumber]').fill('+55 11 99999-0000');
  await submit(owner.page, 'form[action="/api/v1/dashboard/profile"]', 'Save profile');
  const saved = await me(owner.context); assert.equal(saved.name, ownerUser.name + ' Updated'); assert.equal(saved.phoneNumber, '+55 11 99999-0000');
  await owner.page.locator('form[action="/api/v1/dashboard/profile/language"] select[name=locale]').selectOption('pt-BR');
  await submit(owner.page, 'form[action="/api/v1/dashboard/profile/language"]', 'Save language');
  assert.equal((await me(owner.context)).locale, 'pt-BR'); assert.equal(await owner.page.locator('html').getAttribute('lang'), 'pt-BR');
  await owner.page.locator('form[action="/api/v1/dashboard/profile/language"] select[name=locale]').selectOption('en'); await submit(owner.page, 'form[action="/api/v1/dashboard/profile/language"]', 'Save language');
  await shot(owner.page, 'profile-desktop', true);
  assert((await owner.page.title()).startsWith('Profile'), 'profile metadata preserves the original page title');
  await goto(owner.page, '/profile?tab=sessions'); assert(await owner.page.locator('#profile-sessions tbody tr').count() > 0); await owner.page.getByText('Current session', { exact: true }).waitFor();
  await shot(owner.page, 'sessions-desktop', true);
  await goto(owner.page, '/profile?tab=security');
  await owner.page.getByRole('heading', { name: 'Multi-factor authentication and passkeys', exact: true }).waitFor();
  assert((await owner.page.locator('#profile-sign-in-factors').innerText()).includes('Sign-in factors are managed by ts-auth'));
  await owner.page.locator('#currentPassword').fill(ownerUser.password);
  await owner.page.getByRole('button', { name: 'Show password', exact: true }).first().click(); assert.equal(await owner.page.locator('#currentPassword').getAttribute('type'), 'text');
  await owner.page.getByRole('button', { name: 'Hide password', exact: true }).first().click();
  await shot(owner.page, 'security-desktop', true);
  await goto(owner.page, '/profile?tab=security&error=' + encodeURIComponent('Too many sign-in attempts. Try again in 3 seconds.')); await owner.page.getByRole('alert').filter({ hasText: 'Try again in 3 seconds' }).waitFor();
  await goto(owner.page, '/profile?tab=security&error=' + encodeURIComponent('Untrusted arbitrary error')); assert(!(await owner.page.locator('main').innerText()).includes('Untrusted arbitrary error'));
  checks.push('profile/name/phone/language persistence, actual sessions, ts-auth factors card, Datastar reveal and bounded visible errors');
  await goto(owner.page, '/notifications?status=unread&page=999');
  await owner.page.getByText("You're all caught up", { exact: true }).waitFor(); assert.equal(await owner.page.getByRole('button', { name: 'Mark all read', exact: true }).isDisabled(), true);
  assert.equal(await owner.page.locator('form[data-on\\:submit]').count(), 0, 'native notifications omit dialog-only event expressions');
  await shot(owner.page, 'notifications-desktop', true);
  await post(owner.context, owner.page, '/api/v1/notifications/missing/read', {}, 404);
  await post(owner.context, owner.page, '/api/v1/notifications/read-all');
  await owner.page.locator('details.ts-account summary').click();
  await owner.page.locator('#ts-notifications-menu').click(); await owner.page.locator('#ts-notifications-dialog[open]').waitFor();
  await owner.page.locator('#ts-notifications-body').getByText("You're all caught up", { exact: true }).waitFor();
  assert.equal(await owner.page.locator('#ts-notifications-body form').getAttribute('data-on:submit'), "@post('/api/v1/notifications/read-all', {contentType:'form'})");
  await shot(owner.page, 'notifications-dialog-desktop');
  await owner.page.locator('#ts-notifications-dialog').getByRole('button', { name: 'Close', exact: true }).click();
  checks.push('empty notification filtering/paging, native mutation authorization and live Datastar modal patch');
  checks.push('Empty new-account inbox checked here; NotificationBrowserJourneyTests separately covers seeded native per-item and Datastar read-all updates on desktop/mobile.');
  await goto(owner.page, '/organization?tab=projects&newProject=1'); await owner.page.locator('#create-project[open]').waitFor();
  await owner.page.locator('#project-name').fill('Disposable ' + slug); await owner.page.locator('#project-regions').selectOption(['GRU', 'SFO']);
  await owner.page.locator('#project-region').selectOption('GRU'); await owner.page.locator('#project-backup').selectOption('SFO');
  await submit(owner.page, '#create-project form', 'Create project');
  let row = owner.page.locator('#projects tbody tr').filter({ hasText: 'Disposable ' + slug });
  const projectId = await row.locator('.ts-mono').innerText();
  await row.getByRole('button', { name: 'Rename', exact: true }).click();
  await owner.page.locator('#rename-project-' + projectId + ' input[name=name]').fill('Renamed ' + slug);
  await submit(owner.page, '#rename-project-' + projectId + ' form', 'Save project');
  row = owner.page.locator('#projects tbody tr').filter({ hasText: 'Renamed ' + slug });
  await submit(owner.page, '#projects tbody tr:has-text("Renamed ' + slug + '") form[action="/api/v1/dashboard/switch-project"]', 'Select');
  assert.equal((await me(owner.context)).projectId, projectId);
  await shot(owner.page, 'projects-desktop', true);
  await post(owner.context, owner.page, '/api/v1/dashboard/settings/delete-project', { projectId, confirmName: 'Wrong' }, 400);
  await post(owner.context, owner.page, '/api/v1/dashboard/settings/delete-project', { projectId, confirmName: 'Renamed ' + slug });
  await goto(owner.page, '/organization?tab=projects');
  assert.equal(await owner.page.locator('#projects tbody tr').filter({ hasText: 'Renamed ' + slug }).count(), 0); assert.equal((await me(owner.context)).projectId, original.projectId);
  await post(owner.context, owner.page, '/api/v1/dashboard/switch-project', { projectId: originalGuest.projectId }, 404);
  checks.push('native project create/placement/rename/select, scoped rejection and confirmed empty-project deletion/fallback');
  await goto(owner.page, '/organization?newOrganization=organization'); await owner.page.locator('#create-organization[open]').waitFor();
  await owner.page.locator('#create-organization input[name=name]').fill('Invite company ' + slug); await submit(owner.page, '#create-organization form', 'Create organization');
  await owner.page.getByRole('heading', { name: 'Set up billing', exact: true }).waitFor();
  const business = await me(owner.context); assert.notEqual(business.organizationId, original.organizationId); assert.equal(business.projectId, null);
  await goto(owner.page, '/organization');
  const declined = await invite(owner.page, guestUser.email);
  await goto(guest.page, declined); assert.equal((await submit(guest.page, 'form[action$="/respond"]', 'Decline')).status(), 303);
  await goto(guest.page, declined, 404);
  const first = await invite(owner.page, guestUser.email); await goto(owner.page, first); await owner.page.getByRole('alert').filter({ hasText: 'but you are signed in as' }).waitFor();
  const anon = await session({ width: 1280, height: 800 }); await goto(anon.page, first); await anon.page.getByRole('link', { name: 'Sign in', exact: true }).waitFor();
  await signin(anon.page, ownerUser, first); await anon.page.getByRole('alert').filter({ hasText: 'but you are signed in as' }).waitFor();
  assert.equal((await submit(anon.page, 'form[action$="/switch-account"]', 'Sign out and switch account')).status(), 303);
  assert.equal((await anon.context.request.get(base + '/api/v1/auth/me')).status(), 401, 'switch-account ends the independently created dashboard session');
  assert.equal((await me(owner.context)).userId, original.userId, 'switch-account leaves the separate owner session usable');
  assert.equal(new URL(anon.page.url()).searchParams.get('returnTo'), first, 'switch-account preserves the invitation return path');
  await signin(anon.page, guestUser, first); await anon.page.getByRole('button', { name: 'Accept invitation', exact: true }).waitFor();
  await goto(owner.page, '/organization?tab=members');
  await submit(owner.page, '#team-invitations tbody tr:has-text("Pending") form[action*="/resend"]', 'Resend');
  const replacement = new URL(await owner.page.locator('#invitation-link').getAttribute('href')).pathname;
  assert.notEqual(replacement, first); await goto(guest.page, first, 404); await goto(guest.page, replacement);
  await shot(guest.page, 'invitation-mobile', true); await submit(guest.page, 'form[action$="/respond"]', 'Accept invitation');
  assert.equal((await me(guest.context)).organizationId, business.organizationId); assert.equal((await me(guest.context)).role, 'member');
  await goto(guest.page, replacement, 404); await goto(guest.page, '/organization?tab=members'); await shot(guest.page, 'member-access-mobile', true);
  await goto(owner.page, '/organization?tab=members');
  const guestId = (await me(guest.context)).userId;
  const memberRow = owner.page.locator('#team-members tbody tr').filter({ hasText: guestUser.email });
  await memberRow.locator('select[name=role]').selectOption('viewer');
  assert.equal((await submit(owner.page, '#team-members form[action*="/' + encodeURIComponent(guestId) + '/role"]', 'Save role')).status(), 422);
  await owner.page.getByText('The identity provider does not offer a viewer role. No membership was changed.', { exact: true }).waitFor();
  assert.equal((await me(guest.context)).role, 'member');
  await owner.page.locator('#team-members tbody tr').filter({ hasText: guestUser.email }).locator('select[name=role]').selectOption('member'); await submit(owner.page, '#team-members form[action*="/' + encodeURIComponent(guestId) + '/role"]', 'Save role');
  assert.equal((await me(guest.context)).role, 'member');
  await goto(guest.page, '/organization'); await post(guest.context, guest.page, '/api/v1/dashboard/organization/rename', { name: 'Forbidden' }, 403);
  await shot(owner.page, 'members-desktop', true);
  await owner.page.setViewportSize({ width: 390, height: 844 }); await shot(owner.page, 'members-owner-mobile', true); await owner.page.setViewportSize({ width: 1440, height: 900 });
  checks.push('invitation decline/wrong account/anonymous prompt, native session-isolated switch-account and invited-user reauthentication, revoke-and-resend, consumed-token rejection and scoped accepted Member changes and unsupported Viewer denial before mutation');
  await goto(owner.page, '/organization?tab=members');
  await goto(owner.page, '/organization'); await owner.page.getByRole('button', { name: 'Transfer ownership', exact: true }).click();
  await owner.page.locator('#new-owner').selectOption(guestId); await submit(owner.page, '#transfer-ownership form', 'Request transfer');
  await goto(owner.page, '/organization'); await owner.page.getByText('Ownership transfer pending', { exact: true }).waitFor(); await submit(owner.page, 'form[action$="/ownership/cancel"]', 'Cancel transfer');
  await goto(owner.page, '/organization'); await owner.page.getByRole('button', { name: 'Transfer ownership', exact: true }).click();
  await owner.page.locator('#new-owner').selectOption(guestId); await submit(owner.page, '#transfer-ownership form', 'Request transfer');
  await goto(guest.page, '/organization'); await submit(guest.page, 'form[action$="/ownership/confirm"]', 'Accept ownership');
  assert.equal((await me(guest.context)).role, 'owner'); assert.equal((await me(owner.context)).role, 'member');
  await post(owner.context, owner.page, '/api/v1/dashboard/organization/rename', { name: 'Not owner' }, 403);
  await goto(guest.page, '/organization'); await guest.page.getByRole('button', { name: 'Transfer ownership', exact: true }).click(); await guest.page.locator('#new-owner').selectOption(original.userId); await submit(guest.page, '#transfer-ownership form', 'Request transfer');
  await goto(owner.page, '/organization'); await submit(owner.page, 'form[action$="/ownership/confirm"]', 'Accept ownership');
  assert.equal((await me(owner.context)).role, 'owner'); assert.equal((await me(guest.context)).role, 'member');
  checks.push('native ownership start/cancel/recipient confirmation and reverse transfer with fresh role authorization');
  await goto(owner.page, '/organization?tab=members'); await owner.page.locator('#member-search').fill(ownerUser.email); await owner.page.locator('#access-filter-form summary').click();
  const filtered = owner.page.waitForResponse(response => new URL(response.url()).pathname === '/organization' && response.request().headers()['datastar-request'] === 'true');
  await owner.page.locator('#access-filter-form').getByRole('button', { name: 'Organization owner', exact: true }).click(); assert((await filtered).headers()['content-type'].startsWith('text/event-stream'));
  await owner.page.waitForFunction(() => document.querySelector('#member-role-filter-value')?.textContent === 'Organization owner' && document.querySelectorAll('#team-members tbody tr').length === 1);
  assert.equal(await owner.page.locator('#access-filter-form input[name=role]').inputValue(), 'owner');
  await goto(owner.page, '/organization?tab=members&q=' + encodeURIComponent(ownerUser.email) + '&role=owner&page=999'); assert.equal(await owner.page.locator('#team-members tbody tr').count(), 1);
  await goto(owner.page, '/organization?tab=audit');
  assert((await owner.page.title()).startsWith('Organization'), 'organization metadata preserves the original page title');
  assert.equal(await owner.page.locator('dialog[id=create-project]').count(), 0, 'audit does not include unrelated project mutation forms');
  assert.equal(await owner.page.locator('#audit-range').inputValue(), '7d'); await owner.page.locator('#audit-search').fill('invitation');
  await Promise.all([owner.page.waitForNavigation({ waitUntil: 'domcontentloaded' }), owner.page.getByRole('button', { name: 'Apply filters', exact: true }).click()]);
  assert((await owner.page.locator('#recent-activity tbody').innerText()).includes('invitation'));
  await shot(owner.page, 'audit-desktop', true);
  await owner.page.locator('summary[aria-label="Export audit events"]').click();
  const jsonHref = await owner.page.getByRole('link', { name: 'Export JSON', exact: true }).getAttribute('href');
  const exported = await owner.context.request.get(base + jsonHref); assert.equal(exported.status(), 200); const exportData = await exported.json(); assert(exportData.count > 0); assert(exportData.events.every(event => event.action.includes('invitation')));
  const csvHref = await owner.page.getByRole('link', { name: 'Export CSV', exact: true }).getAttribute('href'); const csvData = await owner.context.request.get(base + csvHref); assert((await csvData.text()).startsWith('auditEventId,occurredAt,actor,actorType'));
  await goto(guest.page, '/organization?tab=audit'); assert.equal((await guest.context.request.get(base + '/audit/export?format=json')).status(), 403);
  checks.push('member query/role/page filters, 7d audit defaults, action search, actual JSON/CSV export and member export authorization');
  await goto(guest.page, '/organization'); await guest.page.getByRole('button', { name: 'Leave organization', exact: true }).first().click();
  await submit(guest.page, '#leave-organization form', 'Leave organization'); assert.equal((await me(guest.context)).organizationId, originalGuest.organizationId);
  await post(guest.context, guest.page, '/api/v1/dashboard/switch-organization', { organizationId: business.organizationId }, 404);
  const repeatInvite = await invite(owner.page, guestUser.email); await goto(guest.page, repeatInvite); await submit(guest.page, 'form[action$="/respond"]', 'Accept invitation');
  await goto(owner.page, '/organization?tab=members'); await owner.page.locator('#team-members tbody tr').filter({ hasText: guestUser.email }).getByRole('button', { name: 'Remove access', exact: true }).first().click(); await submit(owner.page, '[id="remove-member-' + guestId + '"] form', 'Remove access');
  assert.equal((await guest.context.request.get(base + '/api/v1/auth/me')).status(), 401, 'member removal revokes the actual dashboard session');
  await signin(guest.page, guestUser, '/organization');
  assert.equal((await me(guest.context)).organizationId, originalGuest.organizationId);
  await post(guest.context, guest.page, '/api/v1/dashboard/switch-organization', { organizationId: business.organizationId }, 404);
  const revokedInvite = await invite(owner.page, guestUser.email); await goto(owner.page, '/organization?tab=members');
  await submit(owner.page, '#team-invitations tbody tr:has-text("Pending") form[action*="/revoke"]', 'Cancel'); await goto(guest.page, revokedInvite, 404);
  checks.push('native member removal causes live scope fallback; owner invitation revoke invalidates the scoped token');
  await goto(owner.page, '/organization'); await owner.page.getByRole('button', { name: 'Organization deletion', exact: true }).click();
  await owner.page.locator('#deletion-confirmation').fill('Wrong'); assert.equal((await submit(owner.page, '#delete-organization form', 'Request deletion')).status(), 422);
  await goto(owner.page, '/organization'); await owner.page.getByRole('button', { name: 'Organization deletion', exact: true }).click();
  await owner.page.locator('#deletion-confirmation').fill('Invite company ' + slug); await submit(owner.page, '#delete-organization form', 'Request deletion');
  assert.equal((await me(owner.context)).organizationId, business.organizationId);
  const pendingDeletion = await owner.context.request.get(base + '/api/v1/dashboard/context');
  const pendingOrg = (await pendingDeletion.json()).organizations.find(org => org.id === business.organizationId);
  assert.equal(pendingOrg.deletionRequested, true); assert.equal(pendingOrg.status, 'active');
  await owner.page.getByRole('button', { name: 'Organization deletion', exact: true }).click();
  assert.equal(await owner.page.getByRole('button', { name: 'Deletion pending', exact: true }).isDisabled(), true);
  await owner.page.getByRole('button', { name: 'Cancel', exact: true }).last().click();
  checks.push('native business member leave, actual session revocation after removal, and idempotent queued organization deletion retains owner access');
  await owner.page.setViewportSize({ width: 390, height: 844 });
  for (const [path, key] of [['/profile', 'profile-mobile'], ['/profile?tab=sessions', 'sessions-mobile'], ['/profile?tab=security', 'security-mobile'], ['/organization', 'organization-mobile'], ['/organization?tab=projects', 'projects-mobile'], ['/notifications', 'notifications-mobile']]) { await goto(owner.page, path); await shot(owner.page, key, true); }
  assert.deepEqual(errors, [], 'no browser runtime errors including Datastar expressions');
  assert.deepEqual(accessibilityFailures, [], 'all desktop/mobile accessibility checks pass');
  gaps.push('The producer does not support Viewer membership; enrollment fails before mutation. Queued organization deletion preserves data pending the external worker completion contract.');
  await writeFile(resolve(output, 'report.json'), JSON.stringify({ passed: true, base, checks, gaps, browserErrors: errors, accessibilityFailures }, null, 2));
  console.log('PASS: ' + checks.join('; '));
} catch (error) {
  if (activePage && !activePage.isClosed()) { await activePage.screenshot({ path: resolve(output, 'failure.png'), fullPage: true }); await writeFile(resolve(output, 'failure.html'), await activePage.content()); console.error('Failed at', activePage.url()); }
  await writeFile(resolve(output, 'report.json'), JSON.stringify({ passed: false, base, checks, gaps, browserErrors: errors, accessibilityFailures, failure: error.stack }, null, 2));
  throw error;
} finally { for (const context of contexts) await context.close(); await browser.close(); }
