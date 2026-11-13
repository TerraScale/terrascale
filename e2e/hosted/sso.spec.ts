import { createHmac, randomBytes } from 'node:crypto';
import type { BrowserContext, Page } from '@playwright/test';
import { Audit, auditedContext, expect, test } from '../support/audit';
import { signInToken } from '../support/auth-management';
import { createProjectOwner } from '../support/project-owner';
import { hostedReady } from '../support/hosted';
import { nextMail } from '../support/inbox';
import { auth, desktop, production } from '../support/stack';
import { saveCookies, saveSecret } from '../support/state';

const origin = { Origin: auth };
const ui = auth + '/ui/terrascale';
const issuer = 'https://login.journey-idp.example';
const security = '/ui/terrascale/security';

test.describe.configure({ mode: 'serial' });

let boundary: {
  state: Awaited<ReturnType<BrowserContext['storageState']>>;
  organization: string;
  domain: string;
  email: string;
  password: string;
};

async function token(context: BrowserContext) {
  const client = '00000000000000000000000007';
  const started = await context.request.post(auth + '/terrascale/oauth/device', { form: { client_id: client, scope: 'openid email' } });
  expect(started.status()).toBe(200);
  const device = await started.json();
  saveSecret('sso-device', device.device_code);
  const approved = await context.request.post(ui + '/device', {
    headers: origin, form: { user_code: device.user_code, decision: 'approve' },
  });
  expect(approved.status()).toBe(200);
  const exchanged = await context.request.post(auth + '/terrascale/oauth/token', {
    form: { client_id: client, grant_type: 'urn:ietf:params:oauth:grant-type:device_code', device_code: device.device_code },
  });
  expect(exchanged.status()).toBe(200);
  const value = (await exchanged.json()).access_token as string;
  saveSecret('sso-token', value);
  expect(JSON.parse(Buffer.from(value.split('.')[1], 'base64url').toString()).ts_auth_mfa).toBe('passkey');
  return value;
}

async function enrollPasskey(context: BrowserContext, page: Page, audit: Audit, email: string) {
  const cdp = await context.newCDPSession(page);
  await cdp.send('WebAuthn.enable');
  await cdp.send('WebAuthn.addVirtualAuthenticator', { options: {
    protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true,
  } });
  await page.goto(ui + '/security');
  await hostedReady(page);
  await page.locator('#passkey-name').fill('SSO journey');
  await page.locator('#passkey-add button[type=submit]').click();
  await expect(page.locator('input[name=name][value="SSO journey"]')).toBeVisible();
  await audit.check('SSO account passkey enrolled');
  await context.clearCookies();
  await page.goto(ui + '/sign-in?next=' + encodeURIComponent(security));
  await hostedReady(page);
  await page.locator('#sign-in-email').fill(email);
  await page.locator('#passkey-sign-in').click();
  await expect.poll(async () => (await context.cookies()).some(cookie => cookie.name === 'tsauth_session')).toBe(true);
  await expect(page).toHaveURL(auth + security);
  await expect(page.locator('input[name=name][value="SSO journey"]')).toBeVisible();
  await audit.check('SSO account signs in with its enrolled passkey');
}

async function linkPrompt(context: BrowserContext, response: { status(): number; text(): Promise<string> }, email: string) {
  expect(response.status()).toBe(200);
  const html = await response.text();
  expect(html).toContain('id="sso-link-confirm"');
  expect(html).toContain('<dd id="sso-link-organization">SSO journey</dd>');
  expect(html).toContain('<dd id="sso-link-issuer">' + new URL(issuer).host + '</dd>');
  expect(html).toContain('<dd id="sso-link-email">' + email + '</dd>');
  expect(html).not.toContain('id="sign-in-password"');
  const confirmed = await context.request.post(ui + '/sso/link', { headers: origin, form: {} });
  expect(confirmed.status()).toBe(200);
  expect(await confirmed.text()).toContain('id="sign-in-password"');
}

async function mfaChallenge(page: Page, html: string) {
  const input = /<input[^>]+name="challenge"[^>]*>/.exec(html)![0];
  return page.evaluate(markup => new DOMParser().parseFromString(markup, 'text/html')
    .querySelector<HTMLInputElement>('input[name=challenge]')!.value, input);
}

function totp(secret: string, step: number) {
  const bits = [...secret].map(letter => 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'.indexOf(letter).toString(2).padStart(5, '0')).join('');
  const key = Buffer.from(bits.match(/.{8}/g)!.map(byte => parseInt(byte, 2)));
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));
  const digest = createHmac('sha1', key).update(counter).digest();
  const offset = digest[digest.length - 1] & 15;
  return ((digest.readUInt32BE(offset) & 0x7fffffff) % 1000000).toString().padStart(6, '0');
}

test('SSO links identities once and completes required MFA without blocking enrollment', async ({ browser }, info) => {
  test.skip(production, 'OIDC and DNS stand-ins belong to the isolated local stack');
  const domain = randomBytes(8).toString('hex') + '.sso.example.com';
  const email = 'member@' + domain;
  const ownerEmail = 'owner@' + domain;
  const freshEmail = 'fresh@' + domain;
  const enrollEmail = 'enroll@' + domain;
  const requiredEmail = 'required@' + domain;
  const raceEmail = 'race@' + domain;
  const password = randomBytes(24).toString('base64url');
  saveSecret('sso-password', password);
  const bootstrap = await createProjectOwner(browser, info);
  const owner = await auditedContext(browser, { viewport: desktop });
  const member = await auditedContext(browser, { viewport: desktop });
  const freshUser = await auditedContext(browser, { viewport: desktop });
  const enrollUser = await auditedContext(browser, { viewport: desktop });
  const requiredUser = await auditedContext(browser, { viewport: desktop });
  const raceUser = await auditedContext(browser, { viewport: desktop });
  const admin = await owner.newPage();
  const memberPage = await member.newPage();
  const adminAudit = new Audit(admin, info);
  const memberAudit = new Audit(memberPage, info);
  const requiredPage = await requiredUser.newPage();
  const requiredAudit = new Audit(requiredPage, info);
  try {
    const bootstrapHeaders = { Authorization: 'Bearer ' + await signInToken(bootstrap.context.request, 'sso-bootstrap') };
    let headers = bootstrapHeaders;
    const created = await owner.request.post(auth + '/v1/terrascale/organizations', { headers, data: { name: 'SSO journey' } });
    expect(created.status()).toBe(201);
    const organization = (await created.json()).id;
    const root = auth + '/v1/terrascale/organizations/' + organization;
    const connection = await owner.request.put(root + '/sso', { headers, data: {
      domain, oidc: { issuer, client_id: domain, client_secret: 'journey-secret' },
    } });
    expect(connection.status()).toBe(200);
    const verification = (await connection.json()).verification.value;
    const configure = async (verified: boolean | string, subjects: Record<string, string> = {}) => {
      const configured = await owner.request.post('http://127.0.0.1:8033/configure', { data: { domain, verification, verified, subjects } });
      expect(configured.status()).toBe(200);
    };
    expect((await owner.request.post('http://127.0.0.1:8032/records', { data: { name: '_terrascale-sso.' + domain, value: verification } })).status()).toBe(200);
    await configure('missing');
    expect((await owner.request.post(root + '/sso/verify', { headers })).status()).toBe(200);
    const members = async () => {
      const response = await owner.request.get(root + '/members', { headers });
      expect(response.status()).toBe(200);
      return (await response.json()).items as { email: string; user_id: string }[];
    };
    const noSession = async (context: BrowserContext) =>
      expect((await context.cookies()).some(cookie => cookie.name === 'tsauth_session')).toBe(false);
    const start = () => member.request.get(ui + '/sso', { params: { email } });
    await member.clearCookies();
    expect((await start()).status()).toBe(401);
    await noSession(member);
    expect((await members()).some(item => item.email === email)).toBe(false);
    await configure(false);
    expect((await start()).status()).toBe(401);
    await noSession(member);
    await configure(true);
    for (const [context, address] of [[owner, ownerEmail], [member, email], [enrollUser, enrollEmail], [raceUser, raceEmail]] as const) {
      const provisioned = await context.request.get(ui + '/sso', { params: { email: address } });
      expect(provisioned.status()).toBe(200);
      expect((await context.cookies()).some(cookie => cookie.name === 'tsauth_session')).toBe(true);
      const membership = (await members()).find(item => item.email === address)!;
      if (context !== owner) {
        expect((await owner.request.delete(root + '/members/' + membership.user_id, { headers })).status()).toBe(204);
      }
      await context.clearCookies();
      expect((await context.request.post(ui + '/reset', { headers: origin, form: { email: address } })).status()).toBe(200);
      const mail = await nextMail(address, 0);
      const code = /^\s*(\d{6})\s*$/m.exec(mail.text)![1];
      saveSecret(address + '-reset-code', code);
      expect((await context.request.post(ui + '/reset/complete', {
        headers: origin, form: { email: address, code, password },
      })).status()).toBe(200);
      expect((await members()).some(item => item.email === address)).toBe(context === owner);
      await noSession(context);
    }
    const ownerId = (await members()).find(item => item.email === ownerEmail)!.user_id;
    const bootstrapId = (await members()).find(item => item.email === bootstrap.current.email)!.user_id;
    expect((await owner.request.put(root + '/members/' + ownerId + '/role', {
      headers, data: { role: 'owner' },
    })).status()).toBe(200);
    expect((await bootstrap.context.request.delete(root + '/members/' + bootstrapId, { headers })).status()).toBe(204);
    expect((await owner.request.post(ui + '/sign-in/password', {
      headers: origin, form: { email: ownerEmail, password },
    })).status()).toBe(200);
    await enrollPasskey(owner, admin, adminAudit, ownerEmail);
    headers = { Authorization: 'Bearer ' + await token(owner) };
    const hasSession = async (context: BrowserContext) =>
      expect((await context.cookies()).some(cookie => cookie.name === 'tsauth_session')).toBe(true);
    const freshFirst = await freshUser.request.get(ui + '/sso', { params: { email: freshEmail } });
    expect(freshFirst.status()).toBe(200);
    expect(await freshFirst.text()).not.toContain('id="sign-in-password"');
    expect(await freshFirst.text()).not.toContain('id="sign-in-code"');
    await hasSession(freshUser);
    expect((await members()).some(item => item.email === freshEmail)).toBe(true);
    await freshUser.clearCookies();
    const freshSecond = await freshUser.request.get(ui + '/sso', { params: { email: freshEmail } });
    expect(freshSecond.status()).toBe(200);
    expect(await freshSecond.text()).not.toContain('id="sign-in-password"');
    expect(await freshSecond.text()).not.toContain('id="sign-in-code"');
    await hasSession(freshUser);
    const unconfirmed = await start();
    expect(unconfirmed.status()).toBe(200);
    expect(await unconfirmed.text()).toContain('id="sso-link-confirm"');
    expect(await unconfirmed.text()).not.toContain('id="sign-in-password"');
    const skipped = await member.request.post(ui + '/sign-in/password', { headers: origin, form: { email, password } });
    expect(skipped.status()).toBe(200);
    expect(await skipped.text()).toContain('/ui/terrascale/sso?email=');
    expect((await member.cookies()).some(cookie => cookie.name === 'tsauth_sso_proof')).toBe(false);
    await noSession(member);
    expect((await members()).some(item => item.email === email)).toBe(false);
    await linkPrompt(member, await start(), email);
    const staleProof = (await member.cookies()).find(cookie => cookie.name === 'tsauth_sso_proof')!;
    saveSecret('sso-stale-proof', staleProof.value);
    await noSession(member);
    expect((await members()).some(item => item.email === email)).toBe(false);
    const wrong = await member.request.post(ui + '/sign-in/password', { headers: origin, form: { email, password: 'incorrect-password' } });
    expect(wrong.status()).toBe(401);
    await noSession(member);
    expect((await members()).some(item => item.email === email)).toBe(false);
    const completed = await member.request.post(ui + '/sign-in/password', { headers: origin, form: { email, password } });
    expect(completed.status()).toBe(200);
    expect((await member.cookies()).some(cookie => cookie.name === 'tsauth_session')).toBe(true);
    expect((await members()).some(item => item.email === email)).toBe(true);
    await member.clearCookies();
    const linked = await start();
    expect(linked.status()).toBe(200);
    expect(await linked.text()).not.toContain('id="sign-in-password"');
    await hasSession(member);
    await configure(true, { [email]: 'changed-subject' });
    await member.clearCookies();
    await linkPrompt(member, await start(), email);
    await noSession(member);
    const relinked = await member.request.post(ui + '/sign-in/password', { headers: origin, form: { email, password } });
    expect(relinked.status()).toBe(200);
    await hasSession(member);
    await configure(true);
    const reconfigured = await owner.request.put(root + '/sso', { headers, data: {
      domain, oidc: { issuer, client_id: domain, client_secret: 'journey-secret' },
    } });
    expect(reconfigured.status()).toBe(200);
    await member.clearCookies();
    await linkPrompt(member, await start(), email);
    await noSession(member);
    expect((await member.request.post(ui + '/sign-in/password', { headers: origin, form: { email, password } })).status()).toBe(200);
    await hasSession(member);
    const enrollment = await member.request.post(ui + '/security/totp', { headers: origin, form: {} });
    expect(enrollment.status()).toBe(200);
    const secret = /data-mfa-secret[^>]*>([^<]+)/.exec(await enrollment.text())![1].trim();
    saveSecret('sso-totp-secret', secret);
    const enrolledStep = Math.floor(Date.now() / 30000);
    const confirmed = await member.request.post(ui + '/security/totp/confirm', {
      headers: origin, form: { code: totp(secret, enrolledStep) },
    });
    expect(confirmed.status()).toBe(200);
    const recoveryCode = [...(await confirmed.text()).matchAll(/data-mfa-secret[^>]*>([^<]+)/g)][0][1];
    saveSecret('sso-recovery-code', recoveryCode);
    await member.clearCookies();
    await member.addCookies([staleProof]);
    const stalePrimary = await member.request.post(ui + '/sign-in/password', { headers: origin, form: { email, password } });
    expect(stalePrimary.status()).toBe(200);
    expect(await stalePrimary.text()).toContain('/ui/terrascale/sso?email=');
    expect(await stalePrimary.text()).not.toContain('id="mfa-code"');
    expect((await member.cookies()).some(cookie => cookie.name === 'tsauth_sso_proof')).toBe(false);
    await noSession(member);
    await configure(true, { [email]: 'factor-subject' });
    await memberPage.goto(ui + '/sso?email=' + encodeURIComponent(email));
    await hostedReady(memberPage);
    await expect(memberPage.locator('#sso-link-organization')).toHaveText('SSO journey');
    await expect(memberPage.locator('#sso-link-issuer')).toHaveText(new URL(issuer).host);
    await expect(memberPage.locator('#sso-link-email')).toHaveText(email);
    await expect(memberPage.locator('#sign-in-password')).toHaveCount(0);
    await memberAudit.check('An existing account confirms linking its SSO identity after a cross-site provider redirect');
    await memberPage.locator('#sso-link-confirm').click();
    await expect(memberPage.locator('#sign-in-password')).toBeVisible();
    await expect(memberPage.locator('form[action$="/sign-in/mfa"]')).toHaveCount(0);
    await noSession(member);
    await memberPage.locator('#sign-in-password').fill(password);
    await memberPage.locator('form[action$="/sign-in/password"] button[type=submit]').click();
    await expect(memberPage.locator('#mfa-code')).toBeVisible();
    const challenge = await memberPage.locator('input[name=challenge]').inputValue();
    saveSecret('sso-mfa-challenge', challenge);
    const ordinaryProof = (await member.cookies()).find(cookie => cookie.name === 'tsauth_sso_proof')!;
    saveSecret('sso-ordinary-factor-proof', ordinaryProof.value);
    await linkPrompt(member, await start(), email);
    const swappedOrdinary = await member.request.post(ui + '/sign-in/mfa', { headers: origin, form: { challenge, code: recoveryCode } });
    expect(swappedOrdinary.status()).toBe(401);
    await noSession(member);
    const stillUnlinked = await start();
    expect(stillUnlinked.status()).toBe(200);
    expect(await stillUnlinked.text()).toContain('id="sso-link-confirm"');
    await member.addCookies([ordinaryProof]);
    await noSession(member);
    await memberAudit.check('An unlinked SSO identity requires password before its enrolled authenticator');
    const mfaCode = totp(secret, Math.max(enrolledStep + 1, Math.floor(Date.now() / 30000)));
    const mfa = await member.request.post(ui + '/sign-in/mfa', {
      headers: origin, form: { challenge, code: mfaCode },
    });
    const mfaBody = await mfa.text();
    const mfaError = ['Sign-in expired', 'That code is wrong or already used'].filter(message => mfaBody.includes(message)).join(', ');
    expect(mfa.status(), mfaError).toBe(200);
    expect((await member.cookies()).some(cookie => cookie.name === 'tsauth_session')).toBe(true);
    const replay = await member.request.post(ui + '/sign-in/mfa', { headers: origin, form: { challenge, code: mfaCode } });
    expect(replay.status()).toBe(401);
    await member.clearCookies();
    const factorLinked = await start();
    expect(factorLinked.status()).toBe(200);
    expect(await factorLinked.text()).toContain('id="mfa-code"');
    expect(await factorLinked.text()).not.toContain('id="sign-in-password"');
    const linkedChallenge = await mfaChallenge(memberPage, await factorLinked.text());
    saveSecret('sso-linked-challenge', linkedChallenge);
    await configure(true, { [email]: 'unproven-subject' });
    await linkPrompt(member, await start(), email);
    const swappedFactor = await member.request.post(ui + '/sign-in/mfa', {
      headers: origin, form: { challenge: linkedChallenge, code: recoveryCode },
    });
    expect(swappedFactor.status()).toBe(401);
    await noSession(member);
    const notLinked = await start();
    expect(notLinked.status()).toBe(200);
    expect(await notLinked.text()).toContain('id="sso-link-confirm"');
    await configure(true, { [email]: 'factor-subject' });
    expect((await owner.request.put(root + '/mfa-policy', { headers, data: { require_mfa: true } })).status()).toBe(200);
    await enrollUser.clearCookies();
    await linkPrompt(enrollUser, await enrollUser.request.get(ui + '/sso', { params: { email: enrollEmail } }), enrollEmail);
    await noSession(enrollUser);
    const enrollProof = await enrollUser.request.post(ui + '/sign-in/password', {
      headers: origin, form: { email: enrollEmail, password },
    });
    expect(enrollProof.status()).toBe(200);
    const enrollBody = await enrollProof.text();
    expect(enrollBody).toContain('action="/ui/terrascale/sign-in/enroll/totp"');
    const enrollChallenge = await mfaChallenge(memberPage, enrollBody);
    saveSecret('sso-enrollment-challenge', enrollChallenge);
    await noSession(enrollUser);
    expect((await members()).some(item => item.email === enrollEmail)).toBe(false);
    const enrollBegin = await enrollUser.request.post(ui + '/sign-in/enroll/totp', {
      headers: origin, form: { challenge: enrollChallenge },
    });
    expect(enrollBegin.status(), enrollBegin.ok() ? '' : await enrollBegin.text()).toBe(200);
    const enrollSecret = /data-mfa-secret[^>]*>([^<]+)/.exec(await enrollBegin.text())![1].trim();
    saveSecret('sso-required-secret', enrollSecret);
    const enrollComplete = await enrollUser.request.post(ui + '/sign-in/enroll/totp/confirm', {
      headers: origin, form: { challenge: enrollChallenge, code: totp(enrollSecret, Math.floor(Date.now() / 30000)) },
    });
    expect(enrollComplete.status()).toBe(200);
    expect(await enrollComplete.text()).toContain('data-mfa-secret');
    await hasSession(enrollUser);
    expect((await members()).some(item => item.email === enrollEmail)).toBe(true);
    const requiredCdp = await requiredUser.newCDPSession(requiredPage);
    await requiredCdp.send('WebAuthn.enable');
    await requiredCdp.send('WebAuthn.addVirtualAuthenticator', { options: {
      protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true,
    } });
    await requiredPage.goto(ui + '/sso?email=' + encodeURIComponent(requiredEmail));
    await hostedReady(requiredPage);
    await expect(requiredPage.locator('form[action$="/sign-in/enroll/totp"]')).toBeVisible();
    await expect(requiredPage.locator('#sign-in-password')).toHaveCount(0);
    await noSession(requiredUser);
    await requiredAudit.check('First-use SSO requires factor enrollment without local primary proof');
    await requiredPage.locator('#passkey-add button[type=submit]').click();
    await expect(requiredPage).toHaveURL(ui + '/security');
    await expect(requiredPage.locator('input[id^="passkey-name-"]')).toHaveCount(1);
    await hasSession(requiredUser);
    await requiredAudit.check('First-use SSO completes after required passkey enrollment');
    await owner.clearCookies();
    await linkPrompt(owner, await owner.request.get(ui + '/sso', { params: { email: ownerEmail } }), ownerEmail);
    await noSession(owner);
    const primary = await owner.request.post(ui + '/sign-in/password', { headers: origin, form: { email: ownerEmail, password } });
    expect(primary.status()).toBe(200);
    expect(await primary.text()).toContain('id="mfa-code"');
    await noSession(owner);
    expect((await members()).filter(item => item.email === ownerEmail)).toHaveLength(1);
    await admin.goto(ui + '/sso?email=' + encodeURIComponent(ownerEmail) + '&next=' + encodeURIComponent(security));
    await hostedReady(admin);
    await expect(admin.locator('#sso-link-email')).toHaveText(ownerEmail);
    await admin.locator('#sso-link-confirm').click();
    await expect(admin.locator('#sign-in-password')).toBeVisible();
    await admin.locator('#sign-in-password').fill(password);
    await admin.locator('form[action$="/sign-in/password"] button[type=submit]').click();
    await expect(admin.locator('#mfa-code')).toBeVisible();
    await noSession(owner);
    expect((await members()).filter(item => item.email === ownerEmail)).toHaveLength(1);
    await adminAudit.check('SSO requires password then the enrolled passkey before linking');
    await admin.locator('#passkey-sign-in').click();
    await expect(admin.locator('#mfa-code')).toHaveCount(0);
    await expect.poll(async () => (await owner.cookies()).some(cookie => cookie.name === 'tsauth_session')).toBe(true);
    expect((await members()).some(item => item.email === ownerEmail)).toBe(true);
    await expect(admin).toHaveURL(auth + security);
    await expect(admin.locator('input[name=name][value="SSO journey"]')).toBeVisible();
    await adminAudit.check('SSO completes with a user-verified passkey');
    await configure(true, { [email]: 'factor-subject', [ownerEmail]: 'passkey-primary-subject' });
    await owner.clearCookies();
    await linkPrompt(owner, await owner.request.get(ui + '/sso', { params: { email: ownerEmail, next: security } }), ownerEmail);
    await noSession(owner);
    await admin.goto(ui + '/sign-in?next=' + encodeURIComponent(security));
    await hostedReady(admin);
    await admin.locator('#sign-in-email').fill(ownerEmail);
    await admin.locator('#passkey-sign-in').click();
    await expect(admin).toHaveURL(auth + security);
    await expect(admin.locator('input[name=name][value="SSO journey"]')).toBeVisible();
    await expect.poll(async () => (await owner.cookies()).some(cookie => cookie.name === 'tsauth_session')).toBe(true);
    await adminAudit.check('A complete ordinary passkey sign-in links the pending SSO identity');
    await owner.clearCookies();
    const passkeyLinked = await owner.request.get(ui + '/sso', { params: { email: ownerEmail } });
    expect(passkeyLinked.status()).toBe(200);
    expect(await passkeyLinked.text()).toContain('id="mfa-code"');
    expect(await passkeyLinked.text()).not.toContain('id="sign-in-password"');
    await member.clearCookies();
    const removedStart = await start();
    expect(removedStart.status()).toBe(200);
    const removedChallenge = await mfaChallenge(memberPage, await removedStart.text());
    saveSecret('sso-removed-challenge', removedChallenge);
    const memberId = (await members()).find(item => item.email === email)!.user_id;
    expect((await owner.request.delete(root + '/members/' + memberId, { headers })).status()).toBe(204);
    const removedFinish = await member.request.post(ui + '/sign-in/mfa', {
      headers: origin, form: { challenge: removedChallenge, code: recoveryCode },
    });
    expect(removedFinish.status()).toBe(403);
    await noSession(member);
    expect((await members()).some(item => item.email === email)).toBe(false);
    await linkPrompt(member, await member.request.get(ui + '/sso', { params: { email } }), email);
    const unrelatedProof = (await member.cookies()).find(cookie => cookie.name === 'tsauth_sso_proof')!;
    saveSecret('abandoned-sso-proof', unrelatedProof.value);
    await owner.clearCookies();
    await owner.addCookies([unrelatedProof]);
    const ownerPrimary = await owner.request.post(ui + '/sign-in/password', {
      headers: origin, form: { email: ownerEmail, password },
    });
    expect(ownerPrimary.status()).toBe(200);
    expect(await ownerPrimary.text()).not.toContain('id="mfa-code"');
    await hasSession(owner);
    expect((await owner.cookies()).some(cookie => cookie.name === 'tsauth_sso_proof')).toBe(false);
    expect((await members()).some(item => item.email === email)).toBe(false);
    await admin.goto(ui + '/sign-in?next=' + encodeURIComponent(security));
    await hostedReady(admin);
    await admin.locator('#sign-in-email').fill(ownerEmail);
    await admin.locator('#passkey-sign-in').click();
    await expect.poll(async () => (await owner.cookies()).some(cookie => cookie.name === 'tsauth_session')).toBe(true);
    await expect(admin).toHaveURL(auth + security);
    await expect(admin.locator('input[name=name][value="SSO journey"]')).toBeVisible();
    await adminAudit.check('An unrelated SSO continuation does not refuse owner sign-in');
    await linkPrompt(raceUser, await raceUser.request.get(ui + '/sso', { params: { email: raceEmail } }), raceEmail);
    const raceProof = await raceUser.request.post(ui + '/sign-in/password', { headers: origin, form: { email: raceEmail, password } });
    expect(raceProof.status()).toBe(200);
    const raceChallenge = await mfaChallenge(memberPage, await raceProof.text());
    saveSecret('sso-race-challenge', raceChallenge);
    const raceBegin = await raceUser.request.post(ui + '/sign-in/enroll/totp', { headers: origin, form: { challenge: raceChallenge } });
    expect(raceBegin.status()).toBe(200);
    const raceSecret = /data-mfa-secret[^>]*>([^<]+)/.exec(await raceBegin.text())![1].trim();
    saveSecret('sso-race-secret', raceSecret);
    expect((await owner.request.put(root + '/sso', { headers, data: {
      domain, oidc: { issuer, client_id: domain, client_secret: 'journey-secret' },
    } })).status()).toBe(200);
    const raceRefused = await raceUser.request.post(ui + '/sign-in/enroll/totp/confirm', {
      headers: origin, form: { challenge: raceChallenge, code: totp(raceSecret, Math.floor(Date.now() / 30000)) },
    });
    expect(raceRefused.status()).toBe(403);
    expect(await raceRefused.text()).not.toContain('data-mfa-secret');
    await noSession(raceUser);
    expect((await members()).some(item => item.email === raceEmail)).toBe(false);
    await raceUser.clearCookies();
    await linkPrompt(raceUser, await raceUser.request.get(ui + '/sso', { params: { email: raceEmail } }), raceEmail);
    const raceRetry = await raceUser.request.post(ui + '/sign-in/password', { headers: origin, form: { email: raceEmail, password } });
    expect(raceRetry.status()).toBe(200);
    expect(await raceRetry.text()).toContain('action="/ui/terrascale/sign-in/enroll/totp"');
    expect(await raceRetry.text()).not.toContain('id="mfa-code"');
    await noSession(raceUser);
    await adminAudit.finish();
    await memberAudit.finish();
    await requiredAudit.finish();
    boundary = { state: await owner.storageState(), organization, domain, email, password };
  } finally {
    await bootstrap.close();
    for (const context of [owner, member, freshUser, enrollUser, requiredUser, raceUser]) {
      await saveCookies(context);
      await context.close();
    }
  }
});

test('SSO admission stays on the platform and platform-only sign-in methods stay closed', async ({ browser }, info) => {
  test.skip(production, 'OIDC and DNS stand-ins belong to the isolated local stack');
  const { organization, domain, email, password } = boundary;
  const owner = await auditedContext(browser, { viewport: desktop, storageState: boundary.state });
  const member = await auditedContext(browser, { viewport: desktop });
  const page = await member.newPage();
  const audit = new Audit(page, info);
  const headers = { Authorization: 'Bearer ' + await token(owner) };
  const organizationRoot = auth + '/v1/terrascale/organizations/' + organization;
  try {
    const created = await owner.request.post(organizationRoot + '/projects', { headers, data: { name: 'SSO tenant boundaries' } });
    expect(created.status()).toBe(201);
    const project = (await created.json()).id;
    const root = auth + '/v1/' + project;
    const base = auth + '/ui/' + project;
    const settings = await owner.request.put(root + '/settings', {
      headers, data: { methods: { sign_up_mode: 'restricted' }, configuration: { redirect_urls: [], require_mfa: true } },
    });
    expect(settings.status()).toBe(200);
    expect((await settings.json()).methods.sign_up_mode).toBe('restricted');
    const stranger = 'closed@' + domain;
    const blocked = await member.request.post(base + '/sign-up', { headers: origin, form: { email: stranger, password } });
    expect(blocked.status()).toBe(403);
    const projectSso = await member.request.get(base + '/sso', { params: { email: stranger } });
    expect(projectSso.status()).toBe(404);
    const directory = await owner.request.get(root + '/users', { headers });
    expect(directory.status()).toBe(200);
    expect((await directory.json()).items.some((user: { email: string }) => user.email === stranger)).toBe(false);
    const platformCreated = await member.request.get(ui + '/sso', { params: { email: stranger } });
    expect(platformCreated.status()).toBe(200);
    expect(await platformCreated.text()).toContain('/sign-in/enroll/totp');
    expect((await member.cookies()).some(cookie => cookie.name === 'tsauth_session')).toBe(false);
    const members = async () => {
      const response = await owner.request.get(organizationRoot + '/members', { headers });
      expect(response.status()).toBe(200);
      return (await response.json()).items as { email: string }[];
    };
    expect((await members()).some(user => user.email === stranger)).toBe(true);
    const afterPlatform = await owner.request.get(root + '/users', { headers });
    expect(afterPlatform.status()).toBe(200);
    expect((await afterPlatform.json()).items.some((user: { email: string }) => user.email === stranger)).toBe(false);
    await member.clearCookies();

    const pending = await member.request.get(ui + '/sso', { params: { email } });
    expect(pending.status()).toBe(200);
    expect(await pending.text()).toContain('id="sso-link-confirm"');
    expect((await members()).some(user => user.email === email)).toBe(false);
    const platformMagic = await member.request.post(ui + '/sign-in/magic', { headers: origin, form: { email } });
    expect(platformMagic.status()).toBe(403);
    const platformMagicGet = await member.request.get(ui + '/sign-in/magic', { params: { token: 'invalid' } });
    expect(platformMagicGet.status()).toBe(404);
    expect((await member.cookies()).some(cookie => cookie.name === 'tsauth_session')).toBe(false);
    await page.goto(ui + '/sso?email=' + encodeURIComponent(email));
    await hostedReady(page);
    await expect(page.locator('#sso-link-email')).toHaveText(email);
    await audit.check('a pending platform SSO link waits for confirmation');
    await audit.finish();
  } finally {
    for (const context of [owner, member]) {
      await saveCookies(context);
      await context.close();
    }
  }
});
