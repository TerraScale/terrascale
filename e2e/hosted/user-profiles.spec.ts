import { createTsAuthClient } from '../../sdk/js/src/ts-auth.js';
import { createHash, randomBytes } from 'node:crypto';
import { readFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { connect } from 'node:http2';
import { Audit, auditedContext, test, expect } from '../support/audit';
import { prepareAuthManagement, closeAuthManagement, stepUp } from '../support/auth-management';
import { hostedReady } from '../support/hosted';
import { auth, dash, stateDir, onLocalStack } from '../support/stack';
import { journey, ownerSession, saveCookies, saveSecret } from '../support/state';

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
    ] });
    expect(imported.ok).toBe(true);
    if (!imported.ok) throw new Error('import failed');
    expect(imported.value.items).toHaveLength(1);
    expect(imported.value.errors).toMatchObject([{ index: 1, code: 'invalid_request' }]);
    importedId = imported.value.items[0].user.id;
    const storedAlgorithm = async () => {
      const peer = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
      const connection = connect('http://127.0.0.1:8081');
      try {
        return await new Promise<string>((resolve, reject) => {
          const request = connection.request({ ':method': 'POST',
            ':path': '/v1/projects/00000000000000000000000002/databases/ts-auth/sql/execute',
            'content-type': 'application/json', 'X-TerraBase-Peer-Credential': peer });
          let response = '';
          request.setEncoding('utf8');
          request.on('error', reject);
          request.on('data', (chunk) => { response += chunk; });
          request.on('end', () => {
            const result = JSON.parse(response);
            if (result.error) reject(new Error(result.error.code));
            else resolve(result.results[0].rows[0][0]);
          });
          request.end(JSON.stringify({ statements: [{ sql: "SELECT CASE WHEN password_hash LIKE '$argon2id$v=19$m=19456,t=2,p=1$%' THEN 'current' ELSE 'imported' END FROM users WHERE instance_id = ? AND id = ?",
            args: [current.projectId, importedId] }] }));
        });
      } finally {
        connection.close();
      }
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
    for (const id of [createdId, importedId]) if (id) await sdk.deleteUser(management.writeKey, current.projectId, id);
    await closeAuthManagement(page, management);
    await saveCookies(context);
    await context.close();
  }
});
