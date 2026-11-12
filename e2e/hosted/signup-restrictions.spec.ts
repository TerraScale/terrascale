import { randomBytes } from 'node:crypto';
import { Audit, auditedContext, test, expect } from '../support/audit';
import { confirmWithPassword, onboard, signUp, verify } from '../support/hosted';
import { newAddress, nextMail } from '../support/inbox';
import { enterSecret } from '../support/secret';
import { auth, dash, desktop } from '../support/stack';
import { journey, saveCookies, saveSecret } from '../support/state';

test('restricted sign-up, single-use invitations and waitlist approval', async ({ browser }, info) => {
  const run = journey().runId + Date.now().toString(36);
  const ownerEmail = await newAddress('restrictions-owner-' + run);
  const invitedEmail = await newAddress('restrictions-invited-' + run);
  const waitingEmail = await newAddress('restrictions-waiting-' + run);
  const password = randomBytes(24).toString('base64url');
  saveSecret('restrictions-password', password);
  const owner = await auditedContext(browser, { viewport: desktop });
  const member = await auditedContext(browser, { viewport: desktop });
  const page = await owner.newPage();
  const memberPage = await member.newPage();
  const audit = new Audit(page, info);
  const memberAudit = new Audit(memberPage, info);
  try {
    await signUp(page, audit, ownerEmail, password, 'restrictions owner');
    await verify(page, ownerEmail);
    await onboard(page, audit, 'Restrictions owner', 'Restrictions ' + run, 'restrictions owner');
    await page.goto(dash + '/auth/overview');
    const hosted = await page.locator('#auth-sign-in-url').getAttribute('href');
    expect(hosted).toBeTruthy();
    const project = new URL(hosted!).pathname.split('/')[2];
    const signup = auth + '/ui/' + project + '/sign-up';

    await page.goto(dash + '/auth/restrictions');
    await page.locator('#signup-mode').selectOption('restricted');
    await page.getByRole('button', { name: 'Save restrictions' }).click();
    await expect(page.locator('#signup-mode')).toHaveValue('restricted');
    await audit.check('restricted project settings saved');

    await memberPage.goto(signup);
    await memberPage.locator('#sign-up-email').fill(invitedEmail);
    await enterSecret(memberPage.locator('#sign-up-password'), password);
    memberAudit.expectStatus(new RegExp('/ui/' + project + '/sign-up$'), 403);
    await memberPage.locator('form:has(#sign-up-email) button[type=submit]').click();
    await expect(memberPage.getByRole('alert')).toContainText('Sign-up is unavailable for this address.');
    await memberAudit.check('restricted stranger refused');

    await page.goto(dash + '/auth/invitations');
    await page.locator('#invitation-email').fill(invitedEmail);
    await page.getByRole('button', { name: 'Invite user' }).click();
    await expect(page.locator('.ts-list-item').filter({ hasText: invitedEmail })).toContainText('pending');
    const expires = await page.locator('.ts-list-item').filter({ hasText: invitedEmail }).locator('time').getAttribute('datetime');
    expect(Math.abs(Date.parse(expires!) - Date.now() - 7 * 24 * 60 * 60 * 1000)).toBeLessThan(60_000);
    await audit.check('project invitation created');
    const invitation = await nextMail(invitedEmail, 0);
    const link = invitation.text.match(new RegExp(auth + '/ui/' + project + '/invitations/[0-9A-HJKMNP-TV-Z]{26}'))?.[0];
    expect(link).toBeTruthy();
    saveSecret('project-invitation-token', new URL(link!).pathname.split('/').at(-1)!);
    await memberPage.goto(link!);
    await expect(memberPage.locator('#sign-up-email')).toHaveValue(invitedEmail);
    await memberAudit.check('invitation acceptance page');
    await enterSecret(memberPage.locator('#sign-up-password'), password);
    await memberPage.locator('form:has(#sign-up-email) button[type=submit]').click();
    await expect(memberPage.locator('#sign-up-code-digit')).toBeVisible();
    const verification = await nextMail(invitedEmail, 1);
    const code = /^\s*(\d{6})\s*$/m.exec(verification.text)?.[1];
    expect(code).toMatch(/^\d{6}$/);
    await enterSecret(memberPage.locator('#sign-up-code-digit'), code!);
    await memberPage.getByRole('button', { name: 'Verify email' }).click();
    await expect(memberPage.locator('#sign-in-error')).toHaveCount(0);
    await expect.poll(async () => (await member.cookies()).some(cookie => cookie.name.includes(project))).toBe(true);
    await page.reload();
    await expect(page.locator('.ts-list-item').filter({ hasText: invitedEmail })).toContainText('accepted');
    const replay = await memberPage.request.get(link!);
    expect(replay.status()).toBe(404);

    await page.goto(dash + '/auth/restrictions');
    await page.locator('#signup-mode').selectOption('public');
    await page.locator('#signup-allowlist').fill('example.com');
    const blockedEmail = await newAddress('restrictions-blocked-' + run);
    await page.locator('#signup-blocklist').fill(invitedEmail + '\n' + blockedEmail + '\nexample.net');
    await page.locator('input[name=block_email_aliases]').check();
    await page.locator('input[name=block_disposable_emails]').check();
    await page.getByRole('button', { name: 'Save restrictions' }).click();
    for (const email of [invitedEmail, blockedEmail, 'alias+blocked@example.com', 'blocked@mailinator.com', 'blocked@example.net']) {
      const blockedPage = await member.newPage();
      const blockedAudit = new Audit(blockedPage, info);
      try {
        await blockedPage.goto(signup);
        await blockedPage.locator('#sign-up-email').fill(email);
        await enterSecret(blockedPage.locator('#sign-up-password'), password);
        blockedAudit.expectStatus(new RegExp('/ui/' + project + '/sign-up$'), 403);
        await blockedPage.locator('form:has(#sign-up-email) button[type=submit]').click();
        await expect(blockedPage.getByRole('alert')).toContainText('Sign-up is unavailable for this address.');
        await blockedAudit.check('project email restriction refused');
        await blockedAudit.finish();
      } finally {
        await blockedPage.close();
      }
    }
    await page.locator('#signup-allowlist').fill('');
    await page.locator('#signup-blocklist').fill('');
    await page.locator('#signup-mode').selectOption('waitlist');
    await page.getByRole('button', { name: 'Save restrictions' }).click();
    await memberPage.goto(auth + '/ui/' + project + '/waitlist');
    await memberPage.locator('#waitlist-email').fill(waitingEmail);
    await memberPage.getByRole('button', { name: 'Join waitlist' }).click();
    await expect(memberPage.getByRole('status')).toContainText('Your request has been received.');
    await memberAudit.check('project waitlist joined');
    await page.goto(dash + '/auth/waitlist');
    const entry = page.locator('.ts-list-item').filter({ hasText: waitingEmail });
    await expect(entry).toContainText('pending');
    await entry.getByRole('button', { name: 'Approve', exact: true }).click();
    await expect(page.locator('.ts-list-item').filter({ hasText: waitingEmail })).toContainText('approved');
    const approved = await nextMail(waitingEmail, 0);
    expect(approved.text).toContain('/ui/' + project + '/invitations/');
    await audit.check('waitlist approval sends an invitation');

    await page.goto(dash + '/profile?tab=danger');
    await page.locator('main').getByRole('button', { name: 'Delete account', exact: true }).click();
    await page.locator('#delete-account-email').fill(ownerEmail);
    await page.locator('#delete-account').getByRole('button', { name: 'Delete account', exact: true }).click();
    await confirmWithPassword(page, audit, password, 'restrictions owner deletion');
    await page.waitForURL(url => url.pathname === '/account-deleted');
  } finally {
    await saveCookies(owner);
    await saveCookies(member);
    await audit.finish();
    await memberAudit.finish();
    await owner.close();
    await member.close();
  }
});
