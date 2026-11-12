import { createHash, createHmac, randomBytes } from 'node:crypto';
import type { BrowserContext } from '@playwright/test';
import { Audit, auditedContext, test, expect } from '../support/audit';
import { confirmWithPassword, hostedReady, onboard, signUp, verify } from '../support/hosted';
import { newAddress, nextMail } from '../support/inbox';
import { enterSecret } from '../support/secret';
import { auth, dash, desktop } from '../support/stack';
import { journey, saveCookies, saveSecret } from '../support/state';

function totp(secret: string): string {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  const bits = [...secret].map((letter) => alphabet.indexOf(letter).toString(2).padStart(5, '0')).join('');
  const key = Buffer.from(bits.match(/.{8}/g)!.map((byte) => parseInt(byte, 2)));
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const digest = createHmac('sha1', key).update(counter).digest();
  const offset = digest[digest.length - 1] & 15;
  return ((digest.readUInt32BE(offset) & 0x7fffffff) % 1000000).toString().padStart(6, '0');
}

test('projects require MFA enrollment, replace sessions and sign users out everywhere', async ({ browser }, info) => {
  const email = await newAddress('mfa-owner-' + journey().runId);
  const endUser = await newAddress('mfa-end-user-' + journey().runId);
  const password = randomBytes(24).toString('base64url');
  saveSecret('project-mfa-password', password);
  const owner = await auditedContext(browser, { viewport: desktop });
  const first = await auditedContext(browser, { viewport: desktop });
  const second = await auditedContext(browser, { viewport: desktop });
  const admin = await owner.newPage();
  const page = await first.newPage();
  const audit = new Audit(page, info);
  const adminAudit = new Audit(admin, info);
  try {
    await signUp(admin, adminAudit, email, password, 'MFA project owner');
    await verify(admin, email);
    await onboard(admin, adminAudit, 'MFA owner', 'MFA policy', 'MFA project owner');
    await admin.goto(dash + '/auth/overview');
    const issuer = (await admin.locator('#auth-issuer').textContent())!;
    const project = new URL(issuer).pathname.split('/').filter(Boolean)[0];
    const cookie = 'tsauth_session_' + project;
    await admin.goto(dash + '/auth/clients');
    await admin.getByRole('button', { name: 'Add application', exact: true }).first().click();
    await admin.locator('#auth-client-name').fill('MFA journey');
    await admin.locator('#auth-client-uris').fill('http://127.0.0.1/callback');
    await admin.locator('#auth-create-client').getByRole('button', { name: 'Add application', exact: true }).click();
    await confirmWithPassword(admin, adminAudit, password, 'create project application');
    await expect(admin.locator('#auth-created-client-id')).toBeVisible();
    const client = (await admin.locator('#auth-created-client-id').textContent())!;

    await admin.goto(dash + '/auth/mfa-sessions');
    await admin.locator('input[name=require_mfa]').check();
    await admin.locator('input[name=allow_passkey]').uncheck();
    await admin.locator('input[name=single_session]').check();
    await admin.locator('#auth-inactivity').fill('15');
    await admin.getByRole('button', { name: 'Save MFA and sessions' }).click();
    await expect(admin.locator('input[name=require_mfa]')).toBeChecked();
    await expect(admin.locator('#auth-inactivity')).toHaveValue('15');
    await adminAudit.check('required TOTP and session policy saved');

    await page.goto(auth + '/ui/' + project + '/sign-up');
    await page.locator('#sign-up-email').fill(endUser);
    await enterSecret(page.locator('#sign-up-password'), password);
    await page.locator('form:has(#sign-up-email) button[type=submit]').click();
    const mail = await nextMail(endUser, 0);
    await page.locator('#sign-up-code').fill(/^\s*(\d{6})\s*$/m.exec(mail.text)![1]);
    await page.getByRole('button', { name: 'Verify email', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Set up an authenticator app', exact: true })).toBeVisible();
    expect((await first.cookies()).some((item) => item.name === cookie)).toBe(false);
    await audit.check('MFA enrollment required before a session exists');
    await page.getByRole('button', { name: 'Set up an authenticator app', exact: true }).click();
    const secret = (await page.locator('[data-mfa-secret]').textContent())!;
    saveSecret('project-totp-secret', secret);
    const challenge = await page.locator('input[name=challenge]').inputValue();
    saveSecret('project-enrollment-challenge', challenge);
    const bypass = await first.request.post(auth + '/ui/' + project + '/sign-in/enroll/totp/confirm', {
      headers: { Origin: auth }, form: { challenge, code: 'A'.repeat(24) },
    });
    expect(bypass.status()).toBe(403);
    expect((await first.cookies()).some((item) => item.name === cookie)).toBe(false);
    await page.locator('#totp-code').fill(totp(secret));
    await page.getByRole('button', { name: 'Turn on two-factor authentication', exact: true }).click();
    await expect(page.locator('[data-mfa-secret]')).toHaveCount(8);
    const recovery = await page.locator('[data-mfa-secret]').allTextContents();
    recovery.forEach((value, index) => saveSecret('project-recovery-' + index, value));
    await saveCookies(first);
    expect((await first.cookies()).some((item) => item.name === cookie)).toBe(true);
    await audit.check('required TOTP enrolled and sign-in completed');

    const tokens = async (context: BrowserContext) => {
      const verifier = randomBytes(32).toString('base64url');
      const authorization = await context.request.get(issuer + '/oauth/authorize', {
        params: { client_id: client, redirect_uri: 'http://127.0.0.1/callback', response_type: 'code',
          scope: 'openid offline_access', code_challenge_method: 'S256', code_challenge: createHash('sha256').update(verifier).digest('base64url') },
        maxRedirects: 0,
      });
      expect(authorization.status()).toBe(302);
      const code = new URL(authorization.headers().location).searchParams.get('code')!;
      saveSecret('project-authorization-code', code);
      const response = await context.request.post(issuer + '/oauth/token', {
        form: { grant_type: 'authorization_code', client_id: client, redirect_uri: 'http://127.0.0.1/callback', code, code_verifier: verifier },
      });
      expect(response.status()).toBe(200);
      const body = await response.json();
      saveSecret('project-access-token', body.access_token);
      saveSecret('project-refresh-token', body.refresh_token);
      return body as { access_token: string; refresh_token: string };
    };
    const original = await tokens(first);
    const stepUp = await first.request.post(auth + '/v1/' + project + '/step-up', {
      headers: { Authorization: 'Bearer ' + original.access_token }, data: { client_id: client, password },
    });
    expect(stepUp.status()).toBe(200);
    const proof = (await stepUp.json()).proof as string;
    saveSecret('project-step-up-proof', proof);
    const claims = JSON.parse(Buffer.from(proof.split('.')[1], 'base64url').toString());
    expect(claims).toMatchObject({ iss: issuer, aud: client, amr: ['pwd'] });
    expect(claims.exp - claims.auth_time).toBe(300);

    const other = await second.newPage();
    const otherAudit = new Audit(other, info);
    await other.goto(auth + '/ui/' + project + '/sign-in');
    await other.locator('#sign-in-email').fill(endUser);
    await other.getByRole('button', { name: 'Use a password instead', exact: true }).click();
    await enterSecret(other.locator('#sign-in-password'), password);
    await other.getByRole('button', { name: 'Sign in', exact: true }).click();
    await other.locator('input[name=code]').fill(recovery[0]);
    await other.locator('form:has(input[name=challenge]) button[type=submit]').click();
    await hostedReady(other);
    await saveCookies(second);
    await otherAudit.check('recovery proof after enrollment completes a new sign-in');
    const replacement = await tokens(second);
    const refresh = (token: string) => first.request.post(issuer + '/oauth/token', {
      form: { grant_type: 'refresh_token', client_id: client, refresh_token: token },
    });
    expect((await refresh(original.refresh_token)).status()).toBe(400);
    await page.goto(auth + '/ui/' + project + '/security');
    await expect(page).toHaveURL(new RegExp('/ui/' + project + '/sign-in'));

    await admin.goto(dash + '/auth/users');
    await admin.getByRole('row').filter({ hasText: endUser }).getByRole('button', { name: 'Sign out everywhere', exact: true }).click();
    expect((await refresh(replacement.refresh_token)).status()).toBe(400);
    await other.goto(auth + '/ui/' + project + '/security');
    await expect(other).toHaveURL(new RegExp('/ui/' + project + '/sign-in'));
    await audit.finish();
    await otherAudit.finish();
    await adminAudit.finish();
  } finally {
    for (const context of [owner, first, second]) {
      await saveCookies(context);
      await context.close();
    }
  }
});
