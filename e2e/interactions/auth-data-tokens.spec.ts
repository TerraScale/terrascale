import { createHash, randomBytes } from 'node:crypto';
import { type APIResponse } from '@playwright/test';
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

async function logged(method: string, path: string, response: APIResponse, status: number) {
  const text = await response.text();
  console.log(method + ' ' + path + ' -> ' + response.status());
  expect(response.status(), text).toBe(status);
  return text ? JSON.parse(text) : null;
}

test('tenant tokens name the project data audience, carry trusted claims, and custom tokens need only the mint scope', async ({ browser, request }, info) => {
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
  const api = 'https://api.terrascale.tech/v1/projects/' + project;
  const databases: string[] = [];
  const owner = { Authorization: '' };
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

    const exchanged = await request.post(auth + '/terrascale/oauth/token', {
      form: { grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey, project,
        scope: 'sql:read sql:write databases:read databases:write' },
    });
    expect(exchanged.status()).toBe(200);
    owner.Authorization = 'Bearer ' + (await exchanged.json()).access_token;
    saveSecret('data-token-project-token', owner.Authorization.slice(7));
    for (const name of ['custom-token-home', 'custom-token-other']) {
      const created = await request.post(api + '/databases', { headers: owner, data: { name: name + '-' + randomBytes(4).toString('hex') } });
      expect(created.status(), await created.text()).toBe(201);
      databases.push((await created.json()).id as string);
    }
    const [home, other] = databases.map(id => api + '/databases/' + id);
    const schema = await request.post(home + '/sql/execute', {
      headers: owner, data: { statements: [{ sql: 'CREATE TABLE notes (id INTEGER PRIMARY KEY, owner_id TEXT NOT NULL, title TEXT NOT NULL)' }] },
    });
    expect(schema.status(), await schema.text()).toBe(200);
    const rules = await request.put(home + '/rules', {
      headers: { ...owner, 'if-none-match': '*' },
      data: { tables: { notes: {
        read: "row.owner_id = auth.uid AND 'editor' IN auth.claims.roles",
        create: "auth.claims.plan = 'pro' AND auth.org_id IS NULL AND auth.org_role IS NULL AND auth.is_anonymous = 0",
        writable: ['title'],
        set: { create: { owner_id: 'auth.uid' } },
      } } },
    });
    expect(rules.status(), await rules.text()).toBe(200);

    const minting = { user_id: user, database: databases[0], claims: { plan: 'pro', roles: ['editor'] } };
    const mint = async (key: string, body: Record<string, unknown>, status: number) =>
      logged('POST', '/v1/{project}/custom-tokens', await request.post(auth + '/v1/' + project + '/custom-tokens', { headers: { Authorization: 'Bearer ' + key }, data: body }), status);
    expect((await mint(current.apiKey, minting, 403)).error.code).toBe('insufficient_scope');
    expect((await mint(mintKey, { ...minting, tables: ['notes'] }, 400)).error.code).toBe('invalid_request');
    expect((await mint(mintKey, { ...minting, user_id: user.toLowerCase() }, 400)).error.code).toBe('invalid_request');
    const custom = await mint(mintKey, minting, 200) as { access_token: string; token_type: string; expires_in: number };
    saveSecret('data-token-custom', custom.access_token);
    expect(custom).toMatchObject({ token_type: 'Bearer', expires_in: 300 });
    const token = decode(custom.access_token);
    expect(token.header).toMatchObject({ typ: 'at+jwt', alg: 'ES256', kid: expect.any(String) });
    expect(token.claims).toMatchObject({ iss: auth + '/' + project, sub: user, token_use: 'custom', database: databases[0], claims: minting.claims });
    const audiences = [token.claims.aud].flat() as string[];
    expect(audiences).toContain(dataAudience);
    for (const audience of audiences) expect(audience.endsWith('/v1/projects/' + project)).toBe(true);
    expect((token.claims.exp as number) - (token.claims.iat as number)).toBeLessThanOrEqual(300);
    for (const absent of ['roles', 'tables', 'token_kind', 'project_id', 'instance_id', 'scope', 'org_id', 'is_anonymous']) expect(token.claims).not.toHaveProperty(absent);
    console.log('decoded custom token header: ' + JSON.stringify(token.header));
    console.log('decoded custom token payload: ' + JSON.stringify(token.claims));

    const bearer = { Authorization: 'Bearer ' + custom.access_token };
    const written = await logged('POST', '…/tables/notes/rows', await request.post(home + '/tables/notes/rows', { headers: bearer, data: { row: { title: 'from the backend' } } }), 201);
    expect(written.row).toMatchObject({ owner_id: user, title: 'from the backend' });
    const read = await logged('GET', '…/tables/notes/rows/{key}', await request.get(home + '/tables/notes/rows/' + written.key, { headers: bearer }), 200);
    expect(read.row).toMatchObject({ owner_id: user, title: 'from the backend' });
    expect((await logged('GET', '{other database}/tables/notes/rows/{key}', await request.get(other + '/tables/notes/rows/' + written.key, { headers: bearer }), 404)).error.code).toBe('unknown_database');
    expect((await logged('POST', '{other database}/tables/notes/rows', await request.post(other + '/tables/notes/rows', { headers: bearer, data: { row: { title: 'x' } } }), 404)).error.code).toBe('unknown_database');
    await logged('GET', '/{project}/oauth/userinfo', await request.get(auth + '/' + project + '/oauth/userinfo', { headers: bearer }), 401);

    const manage = await ownerPage.request.get(auth + '/v1/' + project + '/users', { headers: { Authorization: 'Bearer ' + mintKey } });
    expect(manage.status()).toBe(403);
    await audit.finish();
  } finally {
    if (client) {
      const removed = await ownerPage.request.delete(auth + '/v1/' + project + '/clients/' + client, { headers: managed });
      expect(removed.status()).toBe(204);
    }
    for (const id of databases) expect((await request.delete(api + '/databases/' + id, { headers: owner })).status()).toBe(204);
    await customer.close();
    await account.remove();
  }
});
