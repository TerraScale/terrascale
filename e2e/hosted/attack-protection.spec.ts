import { createHash, randomBytes } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { connect } from 'node:http2';
import { join } from 'node:path';
import { test, expect, Audit, auditedContext } from '../support/audit';
import { hostedReady, signUp, verify, onboard } from '../support/hosted';
import { newAddress, nextMail } from '../support/inbox';
import { enterSecret } from '../support/secret';
import { auth, dash, mailpit, production, stateDir } from '../support/stack';
import { saveCookies, saveSecret } from '../support/state';

test('project attack protection refuses breaches, locks passwords, unlocks by email and challenges bots', async ({ browser }, info) => {
  test.skip(production, 'Requires the journey HIBP and Turnstile stand-ins.');
  const context = await auditedContext(browser, {});
  const page = await context.newPage();
  const audit = new Audit(page, info);
  const suffix = randomBytes(4).toString('hex');
  const ownerEmail = await newAddress(`protection-owner-${suffix}`);
  const password = randomBytes(24).toString('base64url');
  const breached = 'TerraScale-journey-breached-2026!';
  saveSecret('attack-protection-password', password);
  saveSecret('attack-protection-breached-password', breached);
  try {
    await signUp(page, audit, ownerEmail, password, 'attack protection owner');
    await verify(page, ownerEmail);
    await onboard(page, audit, 'Protection owner', `Protection ${suffix}`, 'attack protection');
    await page.goto(`${dash}/auth/attack-protection`);
    await expect(page.locator('input[name=breached_passwords]')).toBeChecked();
    await expect(page.locator('input[name=bot_challenge]')).not.toBeChecked();
    await expect(page.locator('input[name=new_device_email]')).not.toBeChecked();
    await page.locator('input[name=new_device_email]').check();
    await page.getByRole('button', { name: 'Save attack protection', exact: true }).click();
    await expect(page.locator('input[name=new_device_email]')).toBeChecked();
    await audit.check('attack protection defaults');
    await page.goto(`${dash}/auth/overview`);
    const signInUrl = await page.locator('#auth-sign-in-url').textContent();
    const tenant = /\/ui\/([0-9A-HJKMNP-TV-Z]{26})\/sign-in/.exec(signInUrl ?? '')?.[1];
    expect(tenant).toBeTruthy();
    const email = await newAddress(`protected-user-${suffix}`);
    await page.goto(`${auth}/ui/${tenant}/sign-up`);
    await hostedReady(page);
    await page.locator('#sign-up-email').fill(email);
    await enterSecret(page.locator('#sign-up-password'), breached);
    audit.expectStatus(new RegExp(`/ui/${tenant}/sign-up$`), 400);
    await page.locator('form:has(#sign-up-email) button[type=submit]').click();
    await expect(page.locator('#sign-in-error')).toContainText('found in a data breach');
    await expect(page.locator('#sign-up-email')).toHaveValue(email);
    await audit.check('breached password refused on project hosted sign-up');
    const rangeCalls = await (await page.request.get('http://127.0.0.1:8029/calls')).json();
    const prefix = createHash('sha1').update(breached).digest('hex').slice(0, 5).toUpperCase();
    expect(rangeCalls).toContainEqual({ prefix, padded: true });
    expect(rangeCalls.every((call: { prefix: string }) => /^[A-F0-9]{5}$/.test(call.prefix))).toBe(true);
    await enterSecret(page.locator('#sign-up-password'), password);
    await page.locator('form:has(#sign-up-email) button[type=submit]').click();
    const verification = await nextMail(email, 0);
    const verificationCode = /^\s*(\d{6})\s*$/m.exec(verification.text)?.[1] ?? '';
    expect(verificationCode).toMatch(/^\d{6}$/);
    await enterSecret(page.locator('#sign-up-code-digit'), verificationCode);
    await page.getByRole('button', { name: 'Verify email', exact: true }).click();
    await expect(page.locator('h1')).toHaveText('Signed in');
    const signUpMail = await (await page.request.get(
      `${mailpit}/api/v1/search?query=${encodeURIComponent(`to:"${email}"`)}`)).json();
    expect(signUpMail.messages).toHaveLength(1);

    await page.goto(`${auth}/ui/${tenant}/reset`);
    await hostedReady(page);
    await expect(page.locator('#reset-email')).toBeVisible();
    await audit.check('project password reset form');
    const headers = { origin: auth, 'user-agent': await page.evaluate(() => navigator.userAgent) };
    const reset = await page.request.post(`${auth}/ui/${tenant}/reset/complete`, { headers,
      form: { email, code: '000000', password: breached } });
    expect(reset.status()).toBe(400);
    expect(await reset.text()).toContain('found in a data breach');
    const attempt = (secret: string, token?: string) => page.request.post(`${auth}/ui/${tenant}/sign-in/password`, {
      headers, form: { email, password: secret, ...(token === undefined ? {} : { 'cf-turnstile-response': token }) },
    });
    for (let index = 0; index < 10; index++) expect((await attempt('wrong-password-' + index)).status()).toBe(401);
    const locked = await attempt(password);
    expect(locked.status()).toBe(401);
    await expectStoredSubjectKeyed(tenant!, email);
    const unknown = await page.request.post(`${auth}/ui/${tenant}/sign-in/password`, {
      headers, form: { email: `unknown-${suffix}@example.com`, password },
    });
    expect(unknown.status()).toBe(locked.status());
    const cleanResponse = (body: string) => body.replace(/nonce="[^"]*"/g, 'nonce=""')
      .replace(/<span>[^<]*@example.com<\/span>/g, '<span>email</span>')
      .replace(/name="email" value="[^"]*"/g, 'name="email" value=""')
      .replace(/<p class="hint">Request ID:[^<]*<\/p>/g, '');
    expect(cleanResponse(await unknown.text())).toBe(cleanResponse(await locked.text()));
    const lockMail = await nextMail(email, 1);
    expect(lockMail.subject).toContain('Temporary authentication pause');
    const link = /https:\/\/[^\s]+\/attack-protection\/unlock\?[^\s]+/.exec(lockMail.text)?.[0];
    expect(link).toBeTruthy();
    const unlock = new URL(link!);
    saveSecret('account-unlock-token', unlock.searchParams.get('token')!);
    expect(unlock.searchParams.get('token')).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
    const unlockPage = await page.request.get(unlock.href);
    expect(unlockPage.status()).toBe(200);
    expect(await unlockPage.text()).toContain('button type="submit">Unlock account');
    expect((await attempt(password)).status()).toBe(401);
    const redeem = (url: URL) => page.request.post(`${url.origin}${url.pathname}`, {
      headers, form: { user: url.searchParams.get('user')!, token: url.searchParams.get('token')! },
    });
    expect((await redeem(unlock)).status()).toBe(200);
    expect((await attempt(password)).status()).toBe(200);
    for (let index = 0; index < 10; index++) expect((await attempt('wrong-again-' + index)).status()).toBe(401);
    expect((await redeem(unlock)).status()).toBe(200);
    expect((await attempt(password)).status()).toBe(401);
    const secondLock = await nextMail(email, 2);
    const secondLink = /https:\/\/[^\s]+\/attack-protection\/unlock\?[^\s]+/.exec(secondLock.text)?.[0];
    expect(secondLink).toBeTruthy();
    saveSecret('second-unlock-token', new URL(secondLink!).searchParams.get('token')!);
    await redeem(new URL(secondLink!));

    await page.goto(`${dash}/auth/attack-protection`);
    await page.locator('input[name=bot_challenge]').check();
    await page.getByRole('button', { name: 'Save attack protection', exact: true }).click();
    await expect(page.locator('input[name=bot_challenge]')).toBeChecked();
    await audit.check('bot challenge saved');
    expect((await attempt(password)).status()).toBe(403);
    expect((await attempt(password, 'invalid')).status()).toBe(403);
    expect((await attempt(password, 'unreachable')).status()).toBe(403);
    for (const route of ['Sign-In/Password', 'SIGN-UP', 'Reset']) {
      const variant = await page.request.post(`${auth}/ui/${tenant}/${route}`, { headers, form: { email, password } });
      expect(variant.status()).toBe(403);
    }
    const challenge = (host = new URL(auth).hostname, action = 'sign_in', project = tenant) =>
      ['XXXX.DUMMY.TOKEN.XXXX', host, action, project].join('|');
    expect((await attempt(password, challenge('other.example'))).status()).toBe(403);
    expect((await attempt(password, challenge(undefined, 'reset'))).status()).toBe(403);
    expect((await attempt(password, challenge(undefined, undefined, '00000000000000000000000000'))).status()).toBe(403);
    expect((await attempt(password, challenge())).status()).toBe(200);
    await page.goto(`${auth}/ui/${tenant}/sign-in`);
    await hostedReady(page);
    await expect(page.locator('input[name=cf-turnstile-response]')).toHaveValue(challenge());
    await expect(page.locator('.cf-turnstile')).toHaveAttribute('data-action', 'sign_in');
    await expect(page.locator('.cf-turnstile')).toHaveAttribute('data-cdata', tenant!);
    await audit.check('Turnstile widget rendered with the published test site key');
    const freshDevice = await auditedContext(browser, {});
    try {
      expect((await freshDevice.request.post(`${auth}/ui/${tenant}/sign-in/password`, {
        headers, form: { email, password, 'cf-turnstile-response': challenge() },
      })).status()).toBe(200);
      const deviceMail = await nextMail(email, 3);
      expect(deviceMail.subject).toContain('New sign-in');
      expect(deviceMail.text).toContain('Device:');
      expect(deviceMail.text).toContain('Location:');
      expect((await freshDevice.request.post(`${auth}/ui/${tenant}/sign-in/password`, {
        headers: { ...headers, 'user-agent': 'A changed browser agent' },
        form: { email, password, 'cf-turnstile-response': challenge() },
      })).status()).toBe(200);
      const deviceMessages = await (await page.request.get(
        `${mailpit}/api/v1/search?query=${encodeURIComponent(`to:"${email}"`)}`)).json();
      expect(deviceMessages.messages).toHaveLength(4);
    } finally {
      await saveCookies(freshDevice);
      await freshDevice.close();
    }
    await page.goto(`${dash}/auth/attack-protection`);
    await page.locator('input[name=account_lockout]').uncheck();
    await page.getByRole('button', { name: 'Save attack protection', exact: true }).click();
    await expect(page.locator('input[name=account_lockout]')).not.toBeChecked();
    for (let index = 0; index < 10; index++) expect((await attempt('wrong-unlocked-' + index, challenge())).status()).toBe(401);
    expect((await attempt(password, challenge())).status()).toBe(429);
    await audit.finish();
    await context.storageState({ path: join(stateDir, 'attack-protection.session.json') });
    writeFileSync(join(stateDir, 'attack-protection.json'), JSON.stringify({ tenant, email, ownerEmail }), { mode: 0o600 });
  } finally {
    await saveCookies(context);
    await context.close();
  }
});

async function expectStoredSubjectKeyed(tenant: string, email: string): Promise<void> {
  const credential = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[3];
  const session = connect('http://127.0.0.1:8081');
  const stream = session.request({
    ':method': 'POST',
    ':path': `/v1/projects/00000000000000000000000002/databases/ts-auth/sql/execute`,
    'content-type': 'application/json',
    'x-terrabase-peer-credential': credential,
  });
  stream.end(JSON.stringify({ statements: [{ sql: 'SELECT subject FROM account_locks WHERE instance_id = ?', args: [tenant] }] }));
  let body = '';
  for await (const chunk of stream) body += chunk;
  session.close();
  const answer = JSON.parse(body) as { results?: { rows: unknown[][] }[]; error?: { message: string } };
  expect(answer.error?.message ?? '').toBe('');
  const subjects = (answer.results?.[0]?.rows ?? []).map(row => String(row[0]));
  expect(subjects, 'the lock counter row exists').toHaveLength(1);
  expect(subjects[0], 'the stored counter key is the keyed fold, not the plain SHA-256 hex of the email')
    .toMatch(/^fold-\d+$/);
  expect(subjects[0]).not.toBe(createHash('sha256').update(email).digest('hex'));
}
