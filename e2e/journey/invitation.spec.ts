import { randomBytes } from 'node:crypto';
import { Audit, auditedContext, test, expect } from '../support/audit';
import { hostedReady, onboard, onHosted, signInWithPassword, signUp, verify } from '../support/hosted';
import { regionsSettled } from '../support/overview';
import { newAddress } from '../support/inbox';
import { shell } from '../support/shell';
import { ownerSession, saveCookies, saveSecret, journey } from '../support/state';
import { dash, desktop, mobile } from '../support/stack';

const phone = { viewport: mobile, isMobile: true, hasTouch: true, deviceScaleFactor: 2 };

test('a second user signs up on a phone, the Free plan refuses to invite them, and after signing out a protected page leads through a password sign-in back to the dashboard', async ({ browser }, info) => {
  const owner = journey();
  const email = await newAddress(`second-${owner.runId}${Date.now().toString(36)}`);
  const password = randomBytes(24).toString('base64url');
  saveSecret('second-user-password', password);
  const name = `Second ${owner.runId}`;
  const projectName = `Second project ${owner.runId}`;

  const second = await auditedContext(browser, phone);
  const page = await second.newPage();
  const audit = new Audit(page, info);
  await signUp(page, audit, email, password, 'second user');
  await verify(page, email);
  await onboard(page, audit, name, projectName, 'second user');
  const selection = new URL(page.url()).searchParams;
  const identity = { email, displayName: name, organizationId: selection.get('organizationId') ?? '', organizationName: 'Personal', projectId: selection.get('projectId') ?? '', projectName };
  await shell(page, identity);
  await regionsSettled(page);
  await audit.check('second user overview');

  const ownerContext = await auditedContext(browser, { viewport: desktop, storageState: ownerSession() });
  const ownerPage = await ownerContext.newPage();
  const ownerAudit = new Audit(ownerPage, info);
  ownerAudit.expectStatus(/^https:\/\/dash\.terrascale\.tech\/api\/v1\/access\/invitations(\?|$)/, 409);
  await ownerPage.goto(`${dash}/organization?tab=members`);
  const invitations = await ownerPage.locator('#team-invitations').innerText();
  for (const path of ['/api/v1/access/invitations?tab=invitations', '/api/v1/dashboard/organization/members/invite']) {
    const refused = await ownerPage.request.post(`${dash}${path}`, { form: { email, role: 'member' }, headers: { Accept: 'application/json' } });
    expect(refused.status(), `an invitation posted to ${path} without its form token`).toBe(400);
    expect(await refused.json(), `an invitation posted to ${path} without its form token`).toBe('Invalid form token.');
  }
  await ownerPage.reload();
  await expect(ownerPage.locator('#team-invitations'), 'the invitations after two refused form tokens').toHaveText(invitations, { useInnerText: true });
  await ownerPage.locator('.ts-org-header').getByRole('button', { name: 'Invite', exact: true }).click();
  await expect(ownerPage.locator('dialog#team-invite')).toBeVisible();
  await ownerPage.locator('#invite-email').fill(email);
  await ownerPage.locator('#invite-role').selectOption('member');
  await ownerPage.locator('#team-invite').locator('button[type=submit]').click();
  await ownerPage.waitForLoadState('load');
  await expect(ownerPage.locator('dialog#team-invite[open] [role=alert]')).toContainText('The Free plan allows 1 member including pending invitations.');
  await expect(ownerPage.locator('#invite-email')).toHaveValue(email);
  await expect(ownerPage.locator('main tr').filter({ hasText: email })).toHaveCount(0);
  await ownerAudit.check('invitation refused on the Free plan');
  await ownerAudit.finish();
  await saveCookies(ownerContext);
  await ownerContext.close();

  await page.locator('#ts-nav-toggle').click();
  await page.locator('.ts-account-trigger').click();
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await page.waitForURL(onHosted('sign-in'));
  await hostedReady(page);
  await page.goto(`${dash}/databases`);
  await signInWithPassword(page, audit, email, password, 'second user');
  await page.waitForURL((url) => url.origin === dash && url.pathname === '/databases');
  await shell(page, identity);
  await audit.check('second user back on the dashboard');
  await audit.finish();
  await saveCookies(second);
  await second.close();
});
