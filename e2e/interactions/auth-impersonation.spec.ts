import { createHash, randomBytes } from 'node:crypto';
import { Audit, auditedContext, expect, test } from '../support/audit';
import { prepareAuthManagement } from '../support/auth-management';
import { createProjectOwner } from '../support/project-owner';
import { confirmWithPassword, hostedReady } from '../support/hosted';
import { auth, dash, desktop, onLocalStack } from '../support/stack';
import { saveCookies, saveSecret } from '../support/state';

test('project admin impersonates one client with an actor claim and can end it', async ({ browser }, info) => {
  await onLocalStack(async () => {
    const account = await createProjectOwner(browser, info);
    const { page, audit, current: owner } = account;
    try {
    const management = await prepareAuthManagement(page, 'impersonation-' + randomBytes(4).toString('hex'), owner);
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
    const stranger = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
    try {
      const refused = await stranger.request.get(auth + '/' + owner.projectId + '/oauth/authorize', { params: parameters, maxRedirects: 0 });
      expect(new URL(refused.headers().location).searchParams.get('error')).toBe('invalid_request');
    } finally {
      await saveCookies(stranger);
      await stranger.close();
    }
    const context = await auditedContext(browser, { viewport: desktop, storageState: await page.context().storageState() });
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
      await customer.goto(auth + '/ui/' + owner.projectId + '/sign-in');
      await hostedReady(customer);
      await expect(customer.getByRole('button', { name: 'End impersonation' })).toHaveCount(0);
      expect((await context.cookies(auth)).some(cookie => cookie.name === '__Host-tsauth_impersonation_' + owner.projectId)).toBe(false);
      const signedIn = await context.request.post(auth + '/ui/' + owner.projectId + '/sign-in/password', {
        headers: { Origin: auth }, form: { email: profile.email, password: owner.password }, maxRedirects: 0,
      });
      expect(signedIn.status()).toBe(200);
      const normal = await context.request.get(auth + '/' + owner.projectId + '/oauth/authorize', {
        params: Object.fromEntries(Object.entries({ ...parameters, prompt: 'none' }).filter(([key]) => key !== 'impersonation_ticket')), maxRedirects: 0,
      });
      expect(new URL(normal.headers().location).searchParams.has('code')).toBe(true);
      saveSecret('impersonation-normal-code', new URL(normal.headers().location).searchParams.get('code')!);
      await customerAudit.check('ended impersonation permits normal sign-in');
      await audit.check('ended impersonation');
      const challenge = await page.request.post(auth + '/v1/terrascale/step-up', {
        headers, data: { purpose: 'start_impersonation', audience: 'ts-auth', return_uri: dash + '/auth/step-up/callback',
          organization_id: owner.projectId + ':' + management.user },
      });
      expect(challenge.status()).toBe(201);
      const proof = await challenge.json();
      saveSecret('impersonation-delete-proof', proof.proof);
      const approved = await page.request.post(proof.step_up_url, { headers: { Origin: auth }, form: { password: owner.password } });
      expect(approved.status()).toBe(200);
      const requestHeaders = { ...headers, 'X-TerraScale-Step-Up-Proof': proof.proof };
      const data = { reason: 'Verify deletion ends support access', client_id: clients[0] };
      const wrongTarget = await page.request.post(root + '/users/' + actor + '/impersonations', { headers: requestHeaders, data });
      expect(wrongTarget.status()).toBe(403);
      expect((await wrongTarget.json()).error.code).toBe('step_up_required');
      const nextTicket = await page.request.post(endpoint, { headers: requestHeaders, data });
      expect(nextTicket.status()).toBe(201);
      const next = await nextTicket.json();
      saveSecret('impersonation-delete-ticket', next.ticket);
      const nextAuthorization = await context.request.get(auth + '/' + owner.projectId + '/oauth/authorize', {
        params: { ...parameters, impersonation_ticket: next.ticket }, maxRedirects: 0,
      });
      expect(new URL(nextAuthorization.headers().location).searchParams.has('code')).toBe(true);
      saveSecret('impersonation-delete-code', new URL(nextAuthorization.headers().location).searchParams.get('code')!);
      const deleted = await page.request.delete(root + '/users/' + management.user, { headers });
      expect(deleted.status()).toBe(204);
      const auditEvents = await page.request.get(auth + '/v1/terrascale/organizations/' + owner.organizationId + '/audit-events', { headers });
      expect(auditEvents.status()).toBe(200);
      const ends = (await auditEvents.json()).items.filter((event: { action: string; target: string }) =>
        event.action === 'impersonation.ended' && event.target === management.user);
      expect(ends).toEqual(expect.arrayContaining([expect.objectContaining({ actor, action: 'impersonation.ended', target: management.user })]));
      expect(ends.some((event: { detail: string }) => event.detail.includes('end_reason=manual'))).toBe(true);
      expect(ends.some((event: { detail: string }) => event.detail.includes('impersonation=' + next.id) && event.detail.includes('end_reason=target_deleted'))).toBe(true);
      await customer.goto(auth + '/ui/' + owner.projectId + '/sign-in');
      await hostedReady(customer);
      await expect(customer.getByRole('button', { name: 'End impersonation' })).toHaveCount(0);
      expect((await context.cookies(auth)).some(cookie => cookie.name === '__Host-tsauth_impersonation_' + owner.projectId)).toBe(false);
      await expect.poll(async () => {
        const reasons: string[] = [];
        let after = '';
        do {
          const response = await page.request.get(root + '/events', { headers, params: { limit: 100, after } });
          expect(response.status()).toBe(200);
          const body = await response.json();
          reasons.push(...body.items
            .filter((event: { type: string; data: { user_id: string } }) => event.type === 'impersonation.ended' && event.data.user_id === management.user)
            .map((event: { data: { reason: string } }) => event.data.reason));
          after = body.next ?? '';
        } while (after);
        return reasons;
      }).toEqual(expect.arrayContaining(['manual', 'target_deleted']));
    } finally {
      await saveCookies(context);
      await context.close();
    }
    } finally {
      await account.close();
    }
  });
});
