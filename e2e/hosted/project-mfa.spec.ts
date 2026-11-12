import { createHash, createHmac, createPublicKey, randomBytes, verify as verifySignature } from 'node:crypto';
import { spawn } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { once } from 'node:events';
import type { BrowserContext } from '@playwright/test';
import { Audit, auditedContext, test, expect } from '../support/audit';
import { confirmWithPassword, hostedReady, onboard, signUp, verify } from '../support/hosted';
import { newAddress, nextMail } from '../support/inbox';
import { enterSecret } from '../support/secret';
import { auth, dash, desktop, onLocalStack, stateDir } from '../support/stack';
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
  const runId = journey().runId + '-' + randomBytes(4).toString('hex');
  const email = await newAddress('mfa-owner-' + runId);
  const endUser = await newAddress('mfa-end-user-' + runId);
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
    await admin.locator('input[name=allow_passkey]').check();
    await admin.locator('input[name=single_session]').uncheck();
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
    await page.locator('#sign-up-code-digit').fill(/^\s*(\d{6})\s*$/m.exec(mail.text)![1]);
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
    await page.locator('#totp-code-digit').fill(totp(secret));
    await page.getByRole('button', { name: 'Turn on two-factor authentication', exact: true }).click();
    await expect(page.locator('[data-mfa-secret]')).toHaveCount(8);
    const recovery = await page.locator('[data-mfa-secret]').allTextContents();
    recovery.forEach((value, index) => saveSecret('project-recovery-' + index, value));
    await saveCookies(first);
    expect((await first.cookies()).some((item) => item.name === cookie)).toBe(true);
    await audit.check('required TOTP enrolled and sign-in completed');

    const tokens = async (context: BrowserContext, endpoint = issuer) => {
      const verifier = randomBytes(32).toString('base64url');
      const authorization = await context.request.get(endpoint + '/oauth/authorize', {
        params: { client_id: client, redirect_uri: 'http://127.0.0.1/callback', response_type: 'code',
          scope: 'openid offline_access', code_challenge_method: 'S256', code_challenge: createHash('sha256').update(verifier).digest('base64url') },
        maxRedirects: 0,
      });
      expect(authorization.status()).toBe(302);
      const code = new URL(authorization.headers().location).searchParams.get('code')!;
      saveSecret('project-authorization-code', code);
      const response = await context.request.post(endpoint + '/oauth/token', {
        form: { grant_type: 'authorization_code', client_id: client, redirect_uri: 'http://127.0.0.1/callback', code, code_verifier: verifier },
      });
      expect(response.status()).toBe(200);
      const body = await response.json();
      saveSecret('project-access-token', body.access_token);
      saveSecret('project-refresh-token', body.refresh_token);
      return body as { access_token: string; refresh_token: string };
    };
    const addedFactor = await nextMail(endUser, 1);
    expect(addedFactor.subject).toBe('New sign-in factor added');
    await onLocalStack(async () => {
      const pid = readFileSync(join(stateDir, 'pids', 'ts-auth.pid'), 'utf8').trim();
      const environment = Object.fromEntries(readFileSync('/proc/' + pid + '/environ', 'utf8').split('\0')
        .filter(Boolean).map((entry) => [entry.slice(0, entry.indexOf('=')), entry.slice(entry.indexOf('=') + 1)]));
      const legacy = spawn(join(process.env.DOTNET_ROOT!, 'dotnet'), [join(process.env.E2E_BUILD!, 'ts-auth-upgrade', 'ts-auth.dll')], {
        env: { ...environment, TS_AUTH_LISTEN: 'http://127.0.0.1:8091', TS_AUTH_METRICS_LISTEN: 'http://127.0.0.1:9093' },
        stdio: ['ignore', 'ignore', 'ignore'],
      });
      try {
        await expect.poll(async () => {
          if (legacy.exitCode !== null) throw new Error('Pre-binding auth service exited: ' + legacy.exitCode);
          return (await first.request.get('http://127.0.0.1:8091/readyz').catch(() => null))?.status();
        }).toBe(200);
        const legacyContext = await browser.newContext();
        try {
          const currentCookie = (await first.cookies()).find((item) => item.name === cookie)!;
          await legacyContext.addCookies([{ name: cookie, value: currentCookie.value, url: 'http://127.0.0.1:8091' }]);
          const old = await tokens(legacyContext, 'http://127.0.0.1:8091/' + project);
          const oldClaims = JSON.parse(Buffer.from(old.access_token.split('.')[1], 'base64url').toString());
          expect(oldClaims.ts_auth_session).toBeUndefined();
          const refreshed = await first.request.post(issuer + '/oauth/token', {
            form: { grant_type: 'refresh_token', client_id: client, refresh_token: old.refresh_token },
          });
          expect(refreshed.status()).toBe(200);
          const rotated = await refreshed.json();
          saveSecret('pre-binding-access-token', rotated.access_token);
          saveSecret('pre-binding-refresh-token', rotated.refresh_token);
        } finally {
          await saveCookies(legacyContext);
          await legacyContext.close();
        }
      } finally {
        if (legacy.exitCode === null) {
          const exited = once(legacy, 'exit');
          legacy.kill('SIGTERM');
          await exited;
        }
      }
    });
    const original = await tokens(first);
    const passwordOnly = await first.request.post(auth + '/v1/' + project + '/step-up', {
      headers: { Authorization: 'Bearer ' + original.access_token }, data: { client_id: client, password },
    });
    expect(passwordOnly.status()).toBe(401);
    const stepUp = await first.request.post(auth + '/v1/' + project + '/step-up', {
      headers: { Authorization: 'Bearer ' + original.access_token }, data: { client_id: client, password, code: recovery[7] },
    });
    expect(stepUp.status()).toBe(200);
    const proof = (await stepUp.json()).proof as string;
    saveSecret('project-step-up-proof', proof);
    const [header, payload, signature] = proof.split('.');
    const proofHeader = JSON.parse(Buffer.from(header, 'base64url').toString());
    expect(proofHeader.typ).toBe('step-up+jwt');
    const jwks = await (await first.request.get(issuer + '/.well-known/jwks.json')).json();
    const jwk = jwks.keys.find((key: { kid: string }) => key.kid === proofHeader.kid);
    expect(verifySignature('sha256', Buffer.from(header + '.' + payload), {
      key: createPublicKey({ key: jwk, format: 'jwk' }), dsaEncoding: 'ieee-p1363',
    }, Buffer.from(signature, 'base64url'))).toBe(true);
    const claims = JSON.parse(Buffer.from(payload, 'base64url').toString());
    expect(claims).toMatchObject({ iss: issuer, aud: client, amr: ['pwd', 'otp', 'mfa'] });
    expect(claims.sid).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
    expect(claims.exp - claims.auth_time).toBe(300);
    const renewed = await first.request.post(issuer + '/oauth/token', {
      form: { grant_type: 'refresh_token', client_id: client, refresh_token: original.refresh_token },
    });
    expect(renewed.status()).toBe(200);
    const renewedTokens = await renewed.json();
    saveSecret('project-renewed-access-token', renewedTokens.access_token);
    saveSecret('project-renewed-refresh-token', renewedTokens.refresh_token);
    expect(JSON.parse(Buffer.from(renewedTokens.access_token.split('.')[1], 'base64url').toString()).auth_time).toBe(claims.auth_time);
    original.refresh_token = renewedTokens.refresh_token;

    await page.goto(auth + '/ui/' + project + '/sign-in');
    await page.locator('#sign-in-email').fill(endUser);
    await page.getByRole('button', { name: 'Use a password instead', exact: true }).click();
    await enterSecret(page.locator('#sign-in-password'), password);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.locator('input[name=code]').fill(recovery[1]);
    await page.locator('form:has(input[name=challenge]) button[type=submit]').click();
    await hostedReady(page);
    const retained = await first.request.post(issuer + '/oauth/token', {
      form: { grant_type: 'refresh_token', client_id: client, refresh_token: original.refresh_token },
    });
    expect(retained.status()).toBe(200);
    const retainedTokens = await retained.json();
    saveSecret('project-retained-refresh-token', retainedTokens.refresh_token);
    saveSecret('project-retained-access-token', retainedTokens.access_token);
    original.refresh_token = retainedTokens.refresh_token;
    await audit.check('a browser sign-in preserves earlier app refresh tokens without single-session mode');

    await admin.goto(dash + '/auth/mfa-sessions');
    await admin.locator('input[name=single_session]').check();
    await admin.getByRole('button', { name: 'Save MFA and sessions' }).click();
    await expect(admin.locator('input[name=single_session]')).toBeChecked();

    const other = await second.newPage();
    const otherAudit = new Audit(other, info);
    await other.goto(auth + '/ui/' + project + '/sign-in');
    await other.locator('#sign-in-email').fill(endUser);
    await other.getByRole('button', { name: 'Use a password instead', exact: true }).click();
    await enterSecret(other.locator('#sign-in-password'), password);
    await other.getByRole('button', { name: 'Sign in', exact: true }).click();
    const verificationChallenge = await other.locator('input[name=challenge]').inputValue();
    saveSecret('totp-verification-challenge', verificationChallenge);
    for (const route of ['options', 'verify']) {
      const stolenChallenge = await second.request.post(auth + '/ui/' + project + '/security/passkeys/' + route, {
        headers: { Origin: auth, 'X-TerraScale-Mfa-Challenge': verificationChallenge }, data: {},
      });
      expect(stolenChallenge.status()).toBe(403);
    }
    expect((await second.cookies()).some((item) => item.name === cookie)).toBe(false);
    await otherAudit.check('a password-only TOTP verification challenge cannot enroll an attacker passkey');
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
    const passkeyUser = await newAddress('passkey-only-' + runId);
    const passkeyContext = await auditedContext(browser, { viewport: desktop });
    const passkeyPage = await passkeyContext.newPage();
    const passkeyAudit = new Audit(passkeyPage, info);
    const cdp = await passkeyContext.newCDPSession(passkeyPage);
    await cdp.send('WebAuthn.enable');
    await cdp.send('WebAuthn.addVirtualAuthenticator', { options: {
      protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true,
    } });
    try {
      await passkeyPage.goto(auth + '/ui/' + project + '/sign-up');
      await passkeyPage.locator('#sign-up-email').fill(passkeyUser);
      await enterSecret(passkeyPage.locator('#sign-up-password'), password);
      await passkeyPage.locator('form:has(#sign-up-email) button[type=submit]').click();
      const passkeyMail = await nextMail(passkeyUser, 0);
      await passkeyPage.locator('#sign-up-code-digit').fill(/^\s*(\d{6})\s*$/m.exec(passkeyMail.text)![1]);
      await passkeyPage.getByRole('button', { name: 'Verify email', exact: true }).click();
      await passkeyPage.locator('#passkey-add button[type=submit]').click();
      await expect(passkeyPage).toHaveURL(new RegExp('/ui/' + project + '/security'));
      await expect(passkeyPage.locator('input[id^="passkey-name-"]')).toHaveCount(1);
      await passkeyContext.clearCookies();
      await passkeyPage.goto(auth + '/ui/' + project + '/sign-in');
      await passkeyPage.locator('#sign-in-email').fill(passkeyUser);
      await passkeyPage.getByRole('button', { name: 'Use a password instead', exact: true }).click();
      await enterSecret(passkeyPage.locator('#sign-in-password'), password);
      await passkeyPage.getByRole('button', { name: 'Sign in', exact: true }).click();
      await expect(passkeyPage.locator('#passkey-sign-in')).toBeVisible();
      await expect(passkeyPage.locator('#passkey-add')).toHaveCount(0);
      const passkeyChallenge = await passkeyPage.locator('input[name=challenge]').inputValue();
      saveSecret('passkey-only-verification-challenge', passkeyChallenge);
      for (const route of ['totp', 'totp/confirm']) {
        const stolenPassword = await passkeyContext.request.post(auth + '/ui/' + project + '/sign-in/enroll/' + route, {
          headers: { Origin: auth }, form: { challenge: passkeyChallenge, code: '123456' },
        });
        expect(stolenPassword.status()).toBe(403);
      }
      expect((await passkeyContext.cookies()).some((item) => item.name === cookie)).toBe(false);
      await passkeyAudit.check('a passkey-only user cannot enroll TOTP with a password-only verification challenge');
      await passkeyPage.locator('#passkey-sign-in').click();
      await hostedReady(passkeyPage);
      await expect(passkeyPage.locator('input[name=challenge]')).toHaveCount(0);
      expect((await passkeyContext.cookies()).some((item) => item.name === cookie)).toBe(true);
      const passkeyTokens = await tokens(passkeyContext);
      const assertion = await passkeyPage.evaluate(async ({ project, email }) => {
        const response = await fetch('/ui/' + project + '/sign-in/passkey/options', {
          method: 'POST', body: new URLSearchParams({ email }),
        });
        if (!response.ok) throw new Error('Passkey options refused');
        const body = await response.json();
        const decode = (value: string) => Uint8Array.from(atob(value.replace(/-/g, '+').replace(/_/g, '/')), (c) => c.charCodeAt(0));
        const encode = (value: ArrayBuffer) => btoa(String.fromCharCode(...new Uint8Array(value))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
        const credential = await navigator.credentials.get({ publicKey: {
          ...body.options, challenge: decode(body.options.challenge),
          allowCredentials: body.options.allowCredentials.map((entry: { type: PublicKeyCredentialType; id: string }) => ({ ...entry, id: decode(entry.id) })),
        } }) as PublicKeyCredential;
        const proof = credential.response as AuthenticatorAssertionResponse;
        return { challenge: body.challenge, passkey: {
          id: credential.id, rawId: encode(credential.rawId), type: credential.type, response: {
            clientDataJSON: encode(proof.clientDataJSON), authenticatorData: encode(proof.authenticatorData),
            signature: encode(proof.signature), userHandle: proof.userHandle ? encode(proof.userHandle) : null,
          },
        } };
      }, { project, email: passkeyUser });
      saveSecret('passkey-step-up-challenge', assertion.challenge);
      const passkeyStepUp = await passkeyContext.request.post(auth + '/v1/' + project + '/step-up', {
        headers: { Authorization: 'Bearer ' + passkeyTokens.access_token }, data: { client_id: client, ...assertion },
      });
      expect(passkeyStepUp.status()).toBe(200);
      const passkeyProof = (await passkeyStepUp.json()).proof as string;
      saveSecret('passkey-step-up-proof', passkeyProof);
      expect(JSON.parse(Buffer.from(passkeyProof.split('.')[1], 'base64url').toString()).amr).toEqual(['swk']);
      const replay = await passkeyContext.request.post(auth + '/v1/' + project + '/step-up', {
        headers: { Authorization: 'Bearer ' + passkeyTokens.access_token }, data: { client_id: client, ...assertion },
      });
      expect(replay.status()).toBe(401);
      await passkeyAudit.check('required MFA accepts a fresh user-verified passkey for step-up and refuses replay');
      await passkeyAudit.finish();
    } finally {
      await saveCookies(passkeyContext);
      await passkeyContext.close();
    }
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
