import { createHash, randomBytes } from 'node:crypto';
import { Audit, auditedContext, expect, test } from '../support/audit';
import { prepareAuthManagement } from '../support/auth-management';
import { confirmWithPassword, hostedReady } from '../support/hosted';
import { auth, dash, desktop, onLocalStack } from '../support/stack';
import { journey, saveSecret } from '../support/state';

test('project admin impersonates one client with an actor claim and can end it', async ({ page, browser }, info) => {
  await onLocalStack(async () => {
    const audit = new Audit(page, info);
    const owner = journey();
    const management = await prepareAuthManagement(page, 'impersonation-' + randomBytes(4).toString('hex'));
    const root = auth + '/v1/' + owner.projectId;
    const headers = { Authorization: 'Bearer ' + management.ownerToken };
    const actorResponse = await page.request.get(auth + '/terrascale/oauth/userinfo', { headers });
    expect(actorResponse.status()).toBe(200);
    const actor = (await actorResponse.json()).sub;
    const clients: string[] = [];
    const redirect = dash + '/impersonation-callback';
    for (const name of ['Support app', 'Other app']) {
      const step = await page.request.post(auth + '/v1/terrascale/step-up', {
        headers, data: { purpose: 'create_oauth_client', audience: 'ts-auth', return_uri: dash + '/auth/step-up/callback' },
      });
      expect(step.status()).toBe(201);
      const proof = await step.json();
      saveSecret('impersonation-client-proof', proof.proof);
      const approved = await page.request.post(proof.step_up_url, { headers: { Origin: auth }, form: { password: owner.password } });
      expect(approved.status()).toBe(200);
      const created = await page.request.post(root + '/clients', {
        headers: { ...headers, 'X-TerraScale-Step-Up-Proof': proof.proof },
        data: { name, type: 'public', redirect_uris: [redirect] },
      });
      expect(created.status()).toBe(201);
      clients.push((await created.json()).client_id);
    }

    const endpoint = root + '/users/' + management.user + '/impersonations';
    for (const key of [management.readKey, management.writeKey]) {
      const refused = await page.request.post(endpoint, { headers: { Authorization: 'Bearer ' + key } });
      expect(refused.status()).toBe(403);
      expect((await refused.json()).error.code).toBe('person_required');
    }
    const unconfirmed = await page.request.post(endpoint, { headers, data: { reason: 'Support request', client_id: clients[0] } });
    expect(unconfirmed.status()).toBe(403);
    expect((await unconfirmed.json()).error.code).toBe('step_up_required');

    await page.goto('/auth/users');
    await page.locator('button[command=show-modal][commandfor="impersonate-' + management.user + '"]').click();
    const dialog = page.locator('#impersonate-' + management.user);
    await dialog.locator('input[name=reason]').fill('Investigate support request C50');
    await dialog.locator('select[name=client_id]').selectOption(clients[0]);
    await dialog.getByRole('button', { name: 'Impersonate', exact: true }).click();
    await confirmWithPassword(page, audit, owner.password, 'impersonation step-up');
    const ticket = await page.locator('#impersonation-ticket').inputValue();
    saveSecret('impersonation-ticket', ticket);
    const issuedAt = Date.now();
    const verifier = randomBytes(32).toString('base64url');
    saveSecret('impersonation-pkce', verifier);
    const state = randomBytes(16).toString('hex');
    const nonce = randomBytes(16).toString('hex');
    const parameters = {
      client_id: clients[0], redirect_uri: redirect, response_type: 'code', scope: 'openid email offline_access',
      code_challenge: createHash('sha256').update(verifier).digest('base64url'), code_challenge_method: 'S256',
      state, nonce, impersonation_ticket: ticket,
    };
    const context = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
    const customer = await context.newPage();
    const customerAudit = new Audit(customer, info);
    try {
      const wrongClient = await context.request.get(auth + '/' + owner.projectId + '/oauth/authorize', { params: { ...parameters, client_id: clients[1] }, maxRedirects: 0 });
      expect(new URL(wrongClient.headers().location).searchParams.get('error')).toBe('invalid_request');
      const authorized = await context.request.get(auth + '/' + owner.projectId + '/oauth/authorize', { params: parameters, maxRedirects: 0 });
      expect(authorized.status()).toBe(302);
      const callback = new URL(authorized.headers().location);
      expect(callback.searchParams.get('state')).toBe(state);
      const code = callback.searchParams.get('code')!;
      saveSecret('impersonation-code', code);
      const replay = await context.request.get(auth + '/' + owner.projectId + '/oauth/authorize', { params: parameters, maxRedirects: 0 });
      expect(new URL(replay.headers().location).searchParams.get('error')).toBe('invalid_request');
      const exchanged = await context.request.post(auth + '/' + owner.projectId + '/oauth/token', {
        form: { grant_type: 'authorization_code', client_id: clients[0], redirect_uri: redirect, code, code_verifier: verifier },
      });
      expect(exchanged.status()).toBe(200);
      const tokens = await exchanged.json();
      saveSecret('impersonation-access-token', tokens.access_token);
      saveSecret('impersonation-id-token', tokens.id_token);
      saveSecret('impersonation-refresh-token', tokens.refresh_token);
      for (const jwt of [tokens.id_token, tokens.access_token]) {
        const claims = JSON.parse(Buffer.from(jwt.split('.')[1], 'base64url').toString());
        expect(claims).toMatchObject({ sub: management.user, act: { sub: actor } });
        expect(claims.exp * 1000).toBeLessThanOrEqual(issuedAt + 3_600_000);
      }
      const access = { Authorization: 'Bearer ' + tokens.access_token };
      const userinfo = await context.request.get(auth + '/' + owner.projectId + '/oauth/userinfo', { headers: access });
      expect(userinfo.status()).toBe(200);
      const profile = await userinfo.json();
      expect(profile).toMatchObject({ sub: management.user, act: { sub: actor } });
      await customer.goto(auth + '/ui/' + owner.projectId + '/sign-in');
      await hostedReady(customer);
      await expect(customer.getByRole('status')).toContainText('on behalf of');
      await expect(customer.getByRole('button', { name: 'End impersonation' })).toBeVisible();
      await customerAudit.check('hosted impersonation banner');
      for (const path of ['reset/complete', 'security/totp', 'security/totp/disable', 'security/recovery/regenerate', 'security/passkeys/delete']) {
        const refused = await context.request.post(auth + '/ui/' + owner.projectId + '/' + path, { headers: { Origin: auth }, form: {} });
        expect(refused.status()).toBe(403);
        expect((await refused.json()).error.code).toBe('impersonation_read_only');
      }
      await page.goto('/auth/sessions?user=' + management.user);
      await expect(page.getByRole('region', { name: 'Sessions', exact: true })).toContainText(profile.email);
      await audit.check('impersonated user sessions');
      await page.goto('/auth/users');
      const active = page.getByRole('region', { name: 'Active impersonations' });
      await expect(active).toContainText('Investigate support request C50');
      await active.getByRole('row').filter({ hasText: 'Investigate support request C50' }).getByRole('button', { name: 'End', exact: true }).click();
      const ended = await context.request.get(auth + '/' + owner.projectId + '/oauth/userinfo', { headers: access });
      expect(ended.status()).toBe(401);
      const refreshed = await context.request.post(auth + '/' + owner.projectId + '/oauth/token', {
        form: { grant_type: 'refresh_token', client_id: clients[0], refresh_token: tokens.refresh_token },
      });
      expect(refreshed.status()).toBe(400);
      await customer.getByRole('button', { name: 'End impersonation' }).click();
      await expect(customer.getByRole('button', { name: 'End impersonation' })).toHaveCount(0);
      await audit.check('ended impersonation');
    } finally {
      await context.close();
    }
  });
});
