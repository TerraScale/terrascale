import { createHash, randomBytes } from 'node:crypto';
import { Audit, auditedContext, expect, test } from '../support/audit';
import { createDedicatedProjectOwner } from '../support/project-owner';
import { signInToken, stepUp } from '../support/auth-management';
import { confirmWithPassword, hostedReady } from '../support/hosted';
import { newAddress, nextMail } from '../support/inbox';
import { enterSecret } from '../support/secret';
import { auth, dash, desktop } from '../support/stack';
import { saveSecret } from '../support/state';

type Jwt = { header: Record<string, unknown>; claims: Record<string, unknown> };

const decode = (token: string): Jwt => {
  const [header, claims] = token.split('.').slice(0, 2).map(part => JSON.parse(Buffer.from(part, 'base64url').toString()) as Record<string, unknown>);
  return { header, claims };
};

const ulid = /^[0-9A-HJKMNP-TV-Z]{26}$/;

test('tenant tokens name the project data audience, carry trusted claims, and custom tokens need only the mint scope', async ({ browser }, info) => {
  const account = await createDedicatedProjectOwner(browser, info, 'Data token owner');
  const { current } = account;
  const ownerPage = account.page;
  const customer = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  const page = await customer.newPage();
  const audit = new Audit(page, info);
  const project = current.projectId;
  const dataAudience = 'https://api.terrascale.tech/v1/projects/' + project;
  const redirect = dash + '/auth/data-tokens';
  const users = auth + '/v1/' + project + '/users/';
  const managed = { Authorization: 'Bearer ' + current.apiKey };
  let client = '';
  try {
    await ownerPage.goto(dash + '/auth/clients');
    await ownerPage.getByRole('button', { name: 'New client', exact: true }).first().click();
    await ownerPage.locator('#auth-client-name').fill('Data token journey');
    await ownerPage.locator('#auth-client-type').selectOption('public');
    await ownerPage.locator('#auth-client-uris').fill(redirect);
    await ownerPage.locator('#auth-create-client button[type=submit]').click();
    await confirmWithPassword(ownerPage, account.audit, current.password, 'data token OAuth client');
    client = await ownerPage.locator('#auth-created-client-id').innerText();

    const email = await newAddress('data-token-' + randomBytes(5).toString('hex'));
    const password = randomBytes(24).toString('base64url');
    saveSecret('data-token-user-password', password);
    await page.goto(auth + '/ui/' + project + '/sign-up');
    await hostedReady(page);
    await page.locator('#sign-up-email').fill(email);
    await enterSecret(page.locator('#sign-up-password'), password);
    await page.locator('form:has(#sign-up-email) button[type=submit]').click();
    const code = /^\s*(\d{6})\s*$/m.exec((await nextMail(email, 0)).text)?.[1];
    expect(code).toMatch(/^\d{6}$/);
    await enterSecret(page.locator('#sign-up-code-digit'), code!);
    await page.getByRole('button', { name: 'Verify email' }).click();
    await expect(page.locator('[data-state=success]')).toContainText('signed in');
    await audit.check('data token end user signed in');

    const verifier = randomBytes(32).toString('base64url');
    const authorized = await page.request.get(auth + '/' + project + '/oauth/authorize?' + new URLSearchParams({
      client_id: client, redirect_uri: redirect, response_type: 'code', scope: 'openid email offline_access', state: 'data-token-journey',
      code_challenge: createHash('sha256').update(verifier).digest('base64url'), code_challenge_method: 'S256',
    }), { maxRedirects: 0 });
    expect(authorized.status()).toBe(302);
    const location = new URL(authorized.headers().location);
    expect(location.searchParams.get('error')).toBeNull();
    const issued = await page.request.post(auth + '/' + project + '/oauth/token', {
      form: { grant_type: 'authorization_code', code: location.searchParams.get('code')!, client_id: client, redirect_uri: redirect, code_verifier: verifier },
    });
    expect(issued.status()).toBe(200);
    const first = await issued.json() as { access_token: string; refresh_token: string; id_token: string };
    saveSecret('data-token-access', first.access_token);
    saveSecret('data-token-refresh', first.refresh_token);
    saveSecret('data-token-id', first.id_token);

    const access = decode(first.access_token);
    expect(access.header).toMatchObject({ typ: 'at+jwt', alg: 'ES256' });
    expect(access.claims.iss).toBe(auth + '/' + project);
    expect(access.claims.aud).toEqual(expect.arrayContaining([client, dataAudience]));
    expect(access.claims.sub).toMatch(ulid);
    for (const absent of ['claims', 'project_id', 'instance_id', 'token_kind', 'platform_role', 'key_id']) expect(access.claims).not.toHaveProperty(absent);
    const identity = decode(first.id_token).claims;
    expect([identity.aud].flat()).not.toContain(dataAudience);
    const user = access.claims.sub as string;

    const trusted = { plan: 'pro', roles: ['editor', 'billing'], seats: 3, beta: true };
    for (const custom_claims of [{ Plan: 'pro' }, { plan: { tier: 'pro' } }, { plan: null }, { roles: ['editor', 1] }, Object.fromEntries(Array.from({ length: 33 }, (_, i) => ['c' + i, i]))]) {
      const refused = await ownerPage.request.patch(users + user, { headers: managed, data: { custom_claims } });
      expect(refused.status()).toBe(400);
    }
    const updated = await ownerPage.request.patch(users + user, { headers: managed, data: { custom_claims: trusted } });
    expect(updated.status()).toBe(200);
    expect((await updated.json()).custom_claims).toEqual(trusted);
    const self = await page.request.patch(auth + '/v1/' + project + '/me/profile', { headers: { Origin: auth }, data: { custom_claims: { plan: 'free' } } });
    expect(self.status()).toBe(400);
    const read = await ownerPage.request.get(users + user, { headers: managed });
    expect((await read.json()).custom_claims).toEqual(trusted);

    const refreshed = await page.request.post(auth + '/' + project + '/oauth/token', {
      form: { grant_type: 'refresh_token', refresh_token: first.refresh_token, client_id: client },
    });
    expect(refreshed.status()).toBe(200);
    const second = await refreshed.json() as { access_token: string; refresh_token: string };
    saveSecret('data-token-access-refreshed', second.access_token);
    saveSecret('data-token-refresh-rotated', second.refresh_token);
    const next = decode(second.access_token).claims;
    expect(next.claims).toEqual(trusted);
    expect(next.aud).toEqual(expect.arrayContaining([client, dataAudience]));
    console.log('decoded tenant access token claims: ' + JSON.stringify(next));

    const cleared = await ownerPage.request.patch(users + user, { headers: managed, data: { custom_claims: null } });
    expect((await cleared.json()).custom_claims).toBeNull();

    const ownerToken = await signInToken(ownerPage.request, 'data-token-owner');
    const machine = await ownerPage.request.post(auth + '/v1/' + project + '/clients', {
      headers: { Authorization: 'Bearer ' + ownerToken, 'X-TerraScale-Step-Up-Proof': await stepUp(ownerPage.request, ownerToken, 'create_oauth_client', current.password) },
      data: { name: 'Data machine', type: 'machine', redirect_uris: [], allowed_scopes: ['invoices:read'], allowed_audiences: [dataAudience], token_lifetime_minutes: 15 },
    });
    expect(machine.status()).toBe(400);
    expect((await machine.json()).error.code).toBe('invalid_request');

    const created = await ownerPage.request.post(auth + '/v1/terrascale/organizations/' + current.organizationId + '/projects/' + project + '/api-keys', {
      headers: { Authorization: 'Bearer ' + ownerToken }, data: { name: 'Mint only', scopes: ['auth:tokens:mint'] },
    });
    expect(created.status()).toBe(201);
    const mintKey = (await created.json()).key as string;
    saveSecret('data-token-mint-key', mintKey);

    const mint = (key: string) => ownerPage.request.post(auth + '/v1/terrascale/projects/' + project + '/end-user-tokens', {
      headers: { Authorization: 'Bearer ' + key }, data: { user_id: user, claims: { roles: ['editor'] } },
    });
    const withoutScope = await mint(current.apiKey);
    expect(withoutScope.status()).toBe(401);
    const minted = await mint(mintKey);
    expect(minted.status()).toBe(200);
    const custom = await minted.json() as { access_token: string; token_type: string; expires_in: number };
    saveSecret('data-token-custom', custom.access_token);
    expect(custom.token_type).toBe('Bearer');
    expect(decode(custom.access_token).claims).toMatchObject({ sub: user, token_kind: 'end_user', project_id: project, roles: ['editor'] });
    console.log('mint-only custom token request: POST /v1/terrascale/projects/{project}/end-user-tokens -> ' + minted.status() + ', without the scope -> ' + withoutScope.status());

    const manage = await ownerPage.request.get(auth + '/v1/' + project + '/users', { headers: { Authorization: 'Bearer ' + mintKey } });
    expect(manage.status()).toBe(403);
    await audit.finish();
  } finally {
    if (client) {
      const removed = await ownerPage.request.delete(auth + '/v1/' + project + '/clients/' + client, { headers: managed });
      expect(removed.status()).toBe(204);
    }
    await customer.close();
    await account.remove();
  }
});
