import { createTsAuthClient } from '../../sdk/js/src/ts-auth.js';
import { createHash, randomBytes } from 'node:crypto';
import { readFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { connect } from 'node:http2';
import { request } from '@playwright/test';
import { Audit, auditedContext, test, expect } from '../support/audit';
import { prepareAuthManagement, closeAuthManagement, stepUp } from '../support/auth-management';
import { hostedReady } from '../support/hosted';
import { auth, dash, stateDir, onLocalStack } from '../support/stack';
import { journey, ownerSession, saveCookies, saveSecret } from '../support/state';

const djangoHash = 'pbkdf2_sha256$600000$seasalt$OAXyhAQ/4ZDA9V5RMExt3C1OwQdUpLZ99vm1McFlLRA=';
const django52Hash = 'pbkdf2_sha256$1000000$seasalt$r1uLUxoxpP2Ued/qxvmje7UH9PUJBkRrvf9gGPL7Cps=';
const nested = (depth: number): Record<string, unknown> => Array.from({ length: depth - 1 }).reduce<Record<string, unknown>>(value => ({ child: value }), { value: true });

async function authRows(sql: string, args: string[]): Promise<unknown[][]> {
  const peer = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  const connection = connect('http://127.0.0.1:8081');
  try {
    return await new Promise((resolve, reject) => {
      const query = connection.request({ ':method': 'POST',
        ':path': '/v1/projects/00000000000000000000000002/databases/ts-auth/sql/execute',
        'content-type': 'application/json', 'X-TerraBase-Peer-Credential': peer });
      let response = '';
      query.setEncoding('utf8');
      query.on('error', reject);
      query.on('data', chunk => { response += chunk; });
      query.on('end', () => {
        const result = JSON.parse(response);
        if (result.error) reject(new Error(result.error.code));
        else resolve(result.results[0].rows);
      });
      query.end(JSON.stringify({ statements: [{ sql, args }] }));
    });
  } finally {
    connection.close();
  }
}

test('SDK profiles and imported passwords work on hosted pages and OIDC', async ({ browser }, info) => {
  const current = journey();
  const context = await auditedContext(browser, { storageState: ownerSession() });
  const page = await context.newPage();
  const audit = new Audit(page, info);
  const management = await prepareAuthManagement(page, 'profiles');
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
    for (const item of imported.value.items.slice(1)) {
      const django = await request.newContext();
      try {
        for (let sample = 0; sample < 3; sample++) {
          const failures = [];
          for (const email of [item.user.email, `unknown-${current.runId}@example.com`, imported.value.items[0].user.email]) {
            const started = performance.now();
            const response = await django.post(`${auth}/ui/${current.projectId}/sign-in/password`, {
              headers: { Origin: auth }, form: { email, password: 'lètmeinz' },
            });
            expect(response.status()).toBe(401);
            failures.push(performance.now() - started);
          }
          expect(Math.min(...failures)).toBeGreaterThanOrEqual(295);
          expect(Math.max(...failures) - Math.min(...failures)).toBeLessThan(150);
          console.log(`password failure floor: ${failures.map(value => Math.round(value)).join(', ')} ms`);
        }
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

    const proof = await stepUp(page.request, management.ownerToken, 'create_oauth_client');
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
      expect(authorization.status()).toBe(302);
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
    await closeAuthManagement(page, management);
    await saveCookies(context);
    await context.close();
  }
});

test('a tenant at its hashing cap leaves other tenants normal sign-in capacity', async ({ browser }) => {
  await onLocalStack(async () => {
    const current = journey();
    const context = await auditedContext(browser, { storageState: ownerSession() });
    const page = await context.newPage();
    const management = await prepareAuthManagement(page, 'hasher-fairness');
    const sdk = createTsAuthClient({ apiBaseUrl: auth, instanceSlug: 'terrascale' }).projects;
    await sdk.disableUser(management.writeKey, current.projectId, management.user);
    await sdk.enableUser(management.writeKey, current.projectId, management.user);
    const expensive = '$2b$12$abcdefghijklmnopqrstuuDcR.C7.jTI8Np2AcKw0NUM.aM/Z8Kl2';
    const password = 'Journey import password 48!';
    saveSecret('fairness-password', password);
    saveSecret('expensive-import-hash', expensive);
    const ids: string[] = [];
    const clients = await Promise.all(Array.from({ length: 9 }, () => request.newContext()));
    try {
      const imported = await sdk.importUsers(management.writeKey, current.projectId, { users: Array.from({ length: 20 }, (_, index) => ({
        email: `hasher-${index}-${current.runId}@example.com`, password_hash: expensive,
      })) });
      expect(imported.ok).toBe(true);
      if (!imported.ok) throw new Error('fairness import failed');
      expect(imported.value.errors).toEqual([]);
      expect(imported.value.items).toHaveLength(20);
      ids.push(...imported.value.items.map(item => item.user.id));
      const normal = async () => {
        const started = performance.now();
        const response = await clients[8].post(auth + '/ui/terrascale/sign-in/password', {
          headers: { Origin: auth }, form: { email: current.email, password: current.password },
        });
        expect(response.status()).toBe(200);
        expect(await response.text()).toContain('You are signed in.');
        return performance.now() - started;
      };
      const baseline = [];
      for (let sample = 0; sample < 3; sample++) baseline.push(await normal());
      const attempt = async (index: number, client: number) => {
        const started = performance.now();
        const response = await clients[client].post(`${auth}/ui/${current.projectId}/sign-in/password`, {
          headers: { Origin: auth }, form: { email: imported.value.items[index].user.email, password },
        });
        expect(response.status()).toBe(200);
        expect(await response.text()).toContain('You are signed in.');
        return performance.now() - started;
      };
      for (let sample = 0; sample < 3; sample++) {
        const offset = sample * 4;
        const single = await attempt(offset, 0);
        const burst = await Promise.all([attempt(offset + 1, 1), attempt(offset + 2, 2), attempt(offset + 3, 3)]);
        expect(Math.max(...burst)).toBeLessThan(single * 2);
        console.log(`hasher tenant sample ${sample + 1}: single ${Math.round(single)} ms, three ${burst.map(value => Math.round(value)).join(', ')} ms`);
      }
      for (let sample = 0; sample < 3; sample++) {
        let completed = 0;
        const flood = imported.value.items.slice(12).map((item, index) => clients[index].post(`${auth}/ui/${current.projectId}/sign-in/password`, {
          headers: { Origin: auth }, form: { email: item.user.email, password: current.password },
        }).then(response => { completed++; return response; }));
        await Promise.race(flood);
        expect(completed).toBeLessThan(flood.length);
        const elapsed = await normal();
        expect(elapsed).toBeLessThan(Math.max(...baseline) + 1000);
        expect(elapsed).toBeLessThan(5000);
        expect(completed).toBeLessThan(flood.length);
        for (const response of await Promise.all(flood)) expect(response.status()).toBe(401);
        console.log(`hasher fairness sample ${sample + 1}: baseline ${Math.round(Math.max(...baseline))} ms, other tenant ${Math.round(elapsed)} ms`);
      }
    } finally {
      for (const id of ids) await sdk.deleteUser(management.writeKey, current.projectId, id);
      for (const client of clients) {
        for (const cookie of (await client.storageState()).cookies) saveSecret('fairness-cookie', cookie.value);
        await client.dispose();
      }
      await closeAuthManagement(page, management);
      await saveCookies(context);
      await context.close();
    }
  });
});
