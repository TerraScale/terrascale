import { pbkdf2Sync, randomBytes } from 'node:crypto';
import { createTsAuthClient } from '../../sdk/js/src/ts-auth.js';
import { auditedContext, Audit, test, expect } from '../support/audit';
import { request } from '../support/client-address';
import { authRows } from '../support/auth-storage';
import { signInToken, stepUp } from '../support/auth-management';
import { newAddress } from '../support/inbox';
import { onboard, signUp, verify } from '../support/hosted';
import { auth, onLocalStack } from '../support/stack';
import { saveSecret } from '../support/state';

test('eight concurrent first sign-ins preserve imported credentials and failure accounting', async ({ browser }, info) => {
  const context = await auditedContext(browser, {});
  const page = await context.newPage();
  const audit = new Audit(page, info);
  const ownerPassword = randomBytes(24).toString('base64url');
  saveSecret('import-race-owner-password', ownerPassword);
  const email = await newAddress('import-race-owner-' + randomBytes(8).toString('hex'));
  await signUp(page, audit, email, ownerPassword, 'import race owner');
  await verify(page, email);
  await onboard(page, audit, 'Import race owner', 'Import race ' + randomBytes(8).toString('hex'), 'import race owner');
  const project = new URL(page.url()).searchParams.get('projectId')!;
  const token = await signInToken(page.request, 'import-race-owner');
  const password = randomBytes(24).toString('base64url');
  const salt = randomBytes(12).toString('hex');
  const hash = `pbkdf2_sha256$10000$${salt}$${pbkdf2Sync(password, salt, 10000, 32, 'sha256').toString('base64')}`;
  saveSecret('concurrent-import-password', password);
  saveSecret('concurrent-import-hash', hash);
  const sdk = createTsAuthClient({ apiBaseUrl: auth, instanceSlug: 'terrascale' }).projects;
  const clients = await Promise.all(Array.from({ length: 8 }, () => request.newContext()));
  const ids: string[] = [];
  try {
    for (const correct of [true, false]) {
      const email = `race-${randomBytes(8).toString('hex')}@example.com`;
      const imported = await sdk.importUsers(token, project, {
        users: [{ email, email_verified: true, password_hash: hash }],
      });
      if (!imported.ok) throw new Error(JSON.stringify(imported.error));
      expect(imported.value.errors).toEqual([]);
      const user = imported.value.items[0].user;
      ids.push(user.id);
      let before: unknown[][] = [];
      const failures = () => authRows('SELECT COALESCE(SUM(failures), 0) FROM account_locks WHERE instance_id = ? AND user_id = ?', [project, user.id]);
      await onLocalStack(async () => { before = await failures(); });
      const responses = await Promise.all(clients.map(client => client.post(`${auth}/ui/${project}/sign-in/password`, {
        headers: { Origin: auth }, form: { email, password: correct ? password : 'wrong-' + password },
      })));
      for (const response of responses) {
        expect(response.status()).toBe(correct ? 200 : 401);
        expect(await response.text()).toContain(correct ? 'You are signed in.' : 'Wrong email or password');
      }
      await onLocalStack(async () => {
        const rows = await authRows('SELECT password_hash FROM users WHERE instance_id = ? AND id = ?', [project, user.id]);
        if (correct) {
          expect(rows[0][0]).toMatch(/^\$argon2id\$v=19\$m=19456,t=2,p=1\$/);
          expect(await failures()).toEqual(before);
        } else {
          expect(rows[0][0]).toBe(hash);
        }
      });
      console.log(`import race: N=8 ${correct ? 'correct 8/8 succeeded; native hash; failures unchanged' : 'wrong 8/8 refused; imported hash unchanged'}`);
      for (const client of clients) for (const cookie of (await client.storageState()).cookies) saveSecret('import-race-cookie', cookie.value);
    }
  } finally {
    for (const id of ids) await sdk.deleteUser(token, project, id);
    await Promise.all(clients.map(client => client.dispose()));
    const proof = await stepUp(page.request, token, 'delete_account', ownerPassword);
    const deleted = await page.request.delete(`${auth}/v1/terrascale/account`, {
      headers: { Authorization: 'Bearer ' + token, 'X-TerraScale-Step-Up-Proof': proof },
    });
    expect(deleted.status()).toBe(204);
    await context.close();
  }
});
