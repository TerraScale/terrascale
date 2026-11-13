import { createTsAuthClient } from '../../sdk/js/src/ts-auth.js';
import { createHash, randomBytes } from 'node:crypto';
import { readFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { authRows } from '../support/auth-storage';
import { request } from '../support/client-address';
import { Audit, auditedContext, test, expect } from '../support/audit';
import { prepareAuthManagement, closeAuthManagement, stepUp } from '../support/auth-management';
import { createDedicatedProjectOwner } from '../support/project-owner';
import { hostedReady } from '../support/hosted';
import { auth, dash, onLocalStack } from '../support/stack';
import { saveCookies, saveSecret } from '../support/state';

const median = (samples: number[]) => [...samples].sort((left, right) => left - right)[Math.floor(samples.length / 2)];

const djangoHash = 'pbkdf2_sha256$600000$seasalt$OAXyhAQ/4ZDA9V5RMExt3C1OwQdUpLZ99vm1McFlLRA=';
const django52Hash = 'pbkdf2_sha256$1000000$seasalt$r1uLUxoxpP2Ued/qxvmje7UH9PUJBkRrvf9gGPL7Cps=';
const nested = (depth: number): Record<string, unknown> => Array.from({ length: depth - 1 }).reduce<Record<string, unknown>>(value => ({ child: value }), { value: true });


test('SDK profiles and imported passwords work on hosted pages and OIDC', async ({ browser }, info) => {
  const account = await createDedicatedProjectOwner(browser, info, 'Profiles owner');
  const { current, page, audit } = account;
  const management = await prepareAuthManagement(page, 'profiles', current);
  const sdk = createTsAuthClient({ apiBaseUrl: auth, instanceSlug: 'terrascale' }).projects;
  await sdk.disableUser(management.writeKey, current.projectId, management.user);
  await sdk.enableUser(management.writeKey, current.projectId, management.user);
  const root = `${auth}/v1/${current.projectId}`;
  const vector = JSON.parse(readFileSync(join(__dirname, '../sdk/operations.json'), 'utf8')).operations.find((op: { id: string }) => op.id === 'auth.user_profiles');
  const password = 'Journey import password 48!';
  saveSecret('import-password', password);
  saveSecret('import-password-hash', vector.password_hash);
  const shots = join(process.env.HOME!, '.local/state/shots/cos/db4a5ba7-C48-user-profile');
  mkdirSync(shots, { recursive: true });
  const extraIds: string[] = [];
  let importedId: string | undefined;
  let createdId: string | undefined;
  let clientId: string | undefined;
  try {
    const created = await sdk.createUser(management.writeKey, current.projectId, {
      email: `profile-ui-${current.runId}@example.com`, first_name: 'Ada', last_name: 'Lovelace',
      public_metadata: { theme: 'blue', nested: { retained: true, removed: true } }, private_metadata: { backend_secret: 'backend-only' },
    });
    expect(created.ok).toBe(true);
    if (!created.ok) throw new Error('create failed');
    expect(created.value.email_verified).toBe(false);
    createdId = created.value.id;
    const changed = await sdk.updateUser(management.writeKey, current.projectId, createdId, {
      public_metadata: { nested: { removed: null, added: 48 } }, unsafe_metadata: { locale: 'en' },
    });
    expect(changed.ok).toBe(true);
    if (!changed.ok) throw new Error('update failed');
    expect(changed.value.public_metadata).toEqual({ theme: 'blue', nested: { retained: true, added: 48 } });
    for (const field of ['public_metadata', 'private_metadata', 'unsafe_metadata']) {
      const accepted = await sdk.updateUser(management.writeKey, current.projectId, createdId, { [field]: { boundary: nested(9) } });
      expect(accepted.ok).toBe(true);
      const refused = await sdk.updateUser(management.writeKey, current.projectId, createdId, { [field]: { boundary: nested(10) } });
      expect(refused.ok).toBe(false);
      const cleared = await sdk.updateUser(management.writeKey, current.projectId, createdId, { [field]: { boundary: null } });
      expect(cleared.ok).toBe(true);
    }
    const arrays = (depth: number) => Array.from({ length: depth }).reduce<unknown>(value => [value], true);
    expect((await sdk.updateUser(management.writeKey, current.projectId, createdId, { public_metadata: { arrays: arrays(9) } })).ok).toBe(true);
    expect((await sdk.updateUser(management.writeKey, current.projectId, createdId, { public_metadata: { arrays: arrays(10) } })).ok).toBe(false);
    expect((await sdk.updateUser(management.writeKey, current.projectId, createdId, { public_metadata: { arrays: null, large: 'x'.repeat(4100) } })).ok).toBe(true);
    expect((await sdk.updateUser(management.writeKey, current.projectId, createdId, { public_metadata: { other: 'x'.repeat(4100) } })).ok).toBe(false);
    expect((await sdk.updateUser(management.writeKey, current.projectId, createdId, { public_metadata: { large: null } })).ok).toBe(true);
    for (const marker of ['\u200e', '\u200f', '\u061c', '\u202a', '\u202b', '\u202c', '\u202d', '\u202e', '\u2066', '\u2067', '\u2068', '\u2069']) {
      const refused = await sdk.updateUser(management.writeKey, current.projectId, createdId, { first_name: 'Ada' + marker });
      expect(refused.ok).toBe(false);
    }
    await page.goto(`${dash}/auth/users?user=${createdId}`);
    await expect(page.locator('#user-first-name')).toHaveValue('Ada');
    await expect(page.locator('#user-public_metadata')).toContainText('"theme":"blue"');
    await audit.check('project user profile and metadata');
    await page.screenshot({ path: join(shots, 'user-detail-dark-after.png'), fullPage: true });
    await page.locator('#user-first-name').fill('Augusta');
    await page.locator('#user-public_metadata').fill('{"theme":"green"}');
    await page.locator('#auth-user-profile button[type=submit]').click();
    await expect(page.locator('#user-first-name')).toHaveValue('Augusta');
    await expect(page.locator('#user-public_metadata')).toContainText('"theme":"green"');
    await page.goto(dash + '/auth/users');
    await expect(page.getByRole('link', { name: created.value.email, exact: true })).toBeVisible();
    await audit.check('user list links profile');
    await page.goto(`${dash}/auth/sessions?user=${createdId}`);
    await expect(page.locator('main')).toContainText('Sessions');
    await audit.check('profile user sessions');
    await page.goto(dash + '/auth/users');
    await page.screenshot({ path: join(shots, 'users-dark-after.png'), fullPage: true });
    const imported = await sdk.importUsers(management.writeKey, current.projectId, { users: [
      { email: `import-hosted-${current.runId}@example.com`, email_verified: true, password_hash: vector.password_hash,
        first_name: 'Grace', last_name: 'Hopper', image_url: auth + '/admin/logo-icon-blue.svg',
        public_metadata: { team: 'compiler' }, private_metadata: { backend_secret: 'never-in-claims' } },
      { email: `import-invalid-${current.runId}@example.com`, password_hash: '$2b$99$malformed' },
      { email: `import-cost-${current.runId}@example.com`, password_hash: vector.password_hash.replace(/^(\$2[aby]\$)\d{2}/, (_: string, prefix: string) => prefix + '13') },
      { email: `import-pbkdf-cost-${current.runId}@example.com`, password_hash: django52Hash.replace('$1000000$', '$1000001$') },
      { email: `import-argon-cost-${current.runId}@example.com`, password_hash: '$argon2id$v=19$m=38913,t=2,p=1$dHMtYXV0aC12ZWN0b3IxNg$DSktwS9SktskGJvD3C/lWK0F3U9f7EbVnqf4UsOE8FQ' },
      { email: `import-depth-${current.runId}@example.com`, public_metadata: nested(11) },
      { email: `import-bidi-${current.runId}@example.com`, first_name: 'Grace\u2066' },
      { email: `import-django-${current.runId}@example.com`, password_hash: djangoHash },
      { email: `import-django52-${current.runId}@example.com`, password_hash: django52Hash },

    ] });
    expect(imported.ok).toBe(true);
    if (!imported.ok) throw new Error('import failed');
    expect(imported.value.items).toHaveLength(3);
    expect(imported.value.errors.map(error => error.index)).toEqual([1, 2, 3, 4, 5, 6]);
    for (const error of imported.value.errors) expect(error.code).toBe('invalid_request');
    expect(imported.value.errors[1].message).toContain('04–12');
    expect(imported.value.errors[2].message).toContain('1000000');
    expect(imported.value.errors[3].message).toContain('77824');
    extraIds.push(...imported.value.items.slice(1).map(item => item.user.id));
    saveSecret('django-password', 'lètmein');
    saveSecret('django-hash', djangoHash);
    saveSecret('django52-hash', django52Hash);
    for (const item of imported.value.items) {
      const django = await request.newContext();
      try {
        const failures = [[], []] as number[][];
        for (let sample = 0; sample < 5; sample++) {
          for (const [index, email] of [item.user.email, `unknown-${item.user.id}-${current.runId}@example.com`].entries()) {
            const started = performance.now();
            const response = await django.post(`${auth}/ui/${current.projectId}/sign-in/password`, {
              headers: { Origin: auth }, form: { email, password: 'lètmeinz' },
            });
            expect(response.status()).toBe(401);
            failures[index].push(performance.now() - started);
          }
        }
        for (const samples of failures) expect(Math.min(...samples)).toBeGreaterThanOrEqual(295);
        const [importedFloor, unknownFloor] = failures.map(median);
        expect(Math.abs(importedFloor - unknownFloor)).toBeLessThan(unknownFloor * 0.5);
        console.log(`password failure medians: imported ${Math.round(importedFloor)} ms, unknown floor ${Math.round(unknownFloor)} ms, band ${Math.round(unknownFloor * 0.5)} ms`);
        if (item === imported.value.items[0]) continue;
        const signedIn = await django.post(`${auth}/ui/${current.projectId}/sign-in/password`, {
          headers: { Origin: auth }, form: { email: item.user.email, password: 'lètmein' },
        });
        expect(signedIn.status()).toBe(200);
        expect(await signedIn.text()).toContain('You are signed in.');
        for (const cookie of (await django.storageState()).cookies) saveSecret('django-cookie', cookie.value);
      } finally {
        await django.dispose();
      }
    }
    importedId = imported.value.items[0].user.id;
    const storedAlgorithm = async () => {
      const rows = await authRows("SELECT CASE WHEN password_hash LIKE '$argon2id$v=19$m=19456,t=2,p=1$%' THEN 'current' ELSE 'imported' END FROM users WHERE instance_id = ? AND id = ?",
        [current.projectId, importedId!]);
      return rows[0][0];
    };
    await onLocalStack(async () => { expect(await storedAlgorithm()).toBe('imported'); });

    const proof = await stepUp(page.request, management.ownerToken, 'create_oauth_client', current.password);
    const client = await page.request.post(root + '/clients', {
      headers: { Authorization: 'Bearer ' + management.ownerToken, 'X-TerraScale-Step-Up-Proof': proof },
      data: { name: 'Profile journey', type: 'public', redirect_uris: [dash + '/profile'] },
    });
    expect(client.status()).toBe(201);
    clientId = (await client.json()).client_id;
    const verifier = randomBytes(32).toString('base64url');
    saveSecret('profile-verifier', verifier);
    const authorize = new URL(`${auth}/${current.projectId}/oauth/authorize`);
    authorize.search = new URLSearchParams({ client_id: clientId!, redirect_uri: dash + '/profile', response_type: 'code',
      scope: 'openid email profile', state: 'profile', nonce: 'profile', code_challenge_method: 'S256',
      code_challenge: createHash('sha256').update(verifier).digest('base64url') }).toString();
    const userContext = await auditedContext(browser, {});
    const userPage = await userContext.newPage();
    const userAudit = new Audit(userPage, info);
    try {
      const racers = await Promise.all([request.newContext(), request.newContext()]);
      try {
        const responses = await Promise.all(racers.map(racer => racer.post(`${auth}/ui/${current.projectId}/sign-in/password`, {
          headers: { Origin: auth }, form: { email: imported.value.items[0].user.email, password },
        })));
        for (const response of responses) {
          expect(response.status()).toBe(200);
          expect(await response.text()).toContain('You are signed in.');
        }
        for (const racer of racers) for (const cookie of (await racer.storageState()).cookies) saveSecret('race-cookie', cookie.value);
      } finally {
        await Promise.all(racers.map(racer => racer.dispose()));
      }
      await userPage.goto(`${auth}/ui/${current.projectId}/sign-in`);
      await hostedReady(userPage);
      await userPage.locator('#sign-in-email').fill(imported.value.items[0].user.email);
      await userPage.getByRole('button', { name: 'Use a password instead' }).click();
      await hostedReady(userPage);
      await userPage.locator('#sign-in-password').fill(password);
      await userPage.getByRole('button', { name: 'Sign in', exact: true }).click();
      await hostedReady(userPage);
      await expect(userPage.getByRole('status')).toContainText('You are signed in.');
      await onLocalStack(async () => { expect(await storedAlgorithm()).toBe('current'); });
      await userPage.goto(`${auth}/ui/${current.projectId}/security`);
      await expect(userPage.locator('#account-profile-name')).toHaveText('Grace Hopper');
      await userAudit.check('imported bcrypt user on hosted account page');
      await userPage.screenshot({ path: join(shots, 'hosted-profile-dark-after.png'), fullPage: true });
      const self = await userPage.request.patch(root + '/me/profile', {
        headers: { Origin: auth }, data: { first_name: 'Amazing', unsafe_metadata: { locale: 'en' } },
      });
      expect(self.status()).toBe(200);
      expect(await self.json()).toMatchObject({ first_name: 'Amazing', unsafe_metadata: { locale: 'en' } });
      expect(await self.json()).not.toHaveProperty('private_metadata');
      const forbidden = await userPage.request.patch(root + '/me/profile', {
        headers: { Origin: auth }, data: { private_metadata: { role: 'admin' } },
      });
      expect(forbidden.status()).toBe(400);
      const depth = await userPage.request.patch(root + '/me/profile', {
        headers: { Origin: auth }, data: { unsafe_metadata: nested(11) },
      });
      expect(depth.status()).toBe(400);
      let before: unknown[][] = [];
      await onLocalStack(async () => {
        before = await authRows("SELECT version, (SELECT COUNT(*) FROM webhook_outbox WHERE user_id = users.id AND event_type = 'user.updated') FROM users WHERE instance_id = ? AND id = ?", [current.projectId, importedId!]);
      });
      for (let attempt = 0; attempt < 17; attempt++) {
        const unchanged = await userPage.request.patch(root + '/me/profile', {
          headers: { Origin: auth }, data: { first_name: 'Amazing', unsafe_metadata: { locale: 'en' } },
        });
        expect(unchanged.status()).toBe(200);
      }
      await onLocalStack(async () => {
        expect(await authRows("SELECT version, (SELECT COUNT(*) FROM webhook_outbox WHERE user_id = users.id AND event_type = 'user.updated') FROM users WHERE instance_id = ? AND id = ?", [current.projectId, importedId!])).toEqual(before);
      });
      const limited = await userPage.request.patch(root + '/me/profile', { headers: { Origin: auth }, data: {} });
      expect(limited.status()).toBe(429);
      expect(Number(limited.headers()['retry-after'])).toBeGreaterThan(0);
      const authorization = await userPage.request.get(authorize.href, { maxRedirects: 0 });
      const authorizationPage = await authorization.text();
      expect(authorization.status(), authorizationPage.match(/<(?:title|h1)[^>]*>[^<]*/g)?.join('; ')).toBe(302);
      const code = new URL(authorization.headers().location).searchParams.get('code')!;
      saveSecret('profile-code', code);
      const token = await userPage.request.post(`${auth}/${current.projectId}/oauth/token`, {
        form: { grant_type: 'authorization_code', client_id: clientId!, code, redirect_uri: dash + '/profile', code_verifier: verifier },
      });
      expect(token.status()).toBe(200);
      const tokens = await token.json();
      saveSecret('profile-access', tokens.access_token);
      saveSecret('profile-id-token', tokens.id_token);
      const claims = JSON.parse(Buffer.from(tokens.id_token.split('.')[1], 'base64url').toString());
      expect(claims).toMatchObject({ public_metadata: { team: 'compiler' }, given_name: 'Amazing', email_verified: true });
      for (const key of ['private_metadata', 'unsafe_metadata', 'password_hash']) expect(claims).not.toHaveProperty(key);
      const userinfo = await userPage.request.get(`${auth}/${current.projectId}/oauth/userinfo`, {
        headers: { Authorization: 'Bearer ' + tokens.access_token },
      });
      expect(userinfo.status()).toBe(200);
      const body = await userinfo.json();
      expect(body).toMatchObject({ public_metadata: { team: 'compiler' }, given_name: 'Amazing' });
      for (const key of ['private_metadata', 'unsafe_metadata', 'password_hash']) expect(body).not.toHaveProperty(key);
      const readBack = await sdk.getUser(management.readKey, current.projectId, importedId);
      expect(readBack.ok && readBack.value.first_name).toBe('Amazing');
      await userAudit.finish();
    } finally {
      await saveCookies(userContext);
      await userContext.close();
    }
    await audit.finish();
  } finally {
    if (clientId) await sdk.deleteClient(management.ownerToken, current.projectId, clientId);
    for (const id of [createdId, importedId, ...extraIds]) if (id) await sdk.deleteUser(management.writeKey, current.projectId, id);
    await closeAuthManagement(page, management, current);
    await account.remove();
  }
});
