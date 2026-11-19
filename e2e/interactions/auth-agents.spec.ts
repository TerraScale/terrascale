import { generateKeyPairSync, randomBytes } from 'node:crypto';
import { expect, test } from '../support/audit';
import { keyId } from '../support/api-key';
import { newAddress } from '../support/inbox';
import { createProjectOwner } from '../support/project-owner';
import { auth, onLocalStack } from '../support/stack';
import { saveSecret } from '../support/state';

type Jwk = { kty: string; crv: string; x: string; y: string; d?: string };
type Agent = { id: string; owner_user_id: string; status: string; allowed_scopes: string[]; keys: { kid: string; thumbprint: string; revoked_at: string | null }[]; revoked_at: string | null };

function keyPair(): { publicJwk: Jwk; privateJwk: Jwk } {
  const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  return { publicJwk: publicKey.export({ format: 'jwk' }) as Jwk, privateJwk: privateKey.export({ format: 'jwk' }) as Jwk };
}

test('project agents hold their own public keys, stay within their limits and can be revoked', async ({ browser }, info) => {
  await onLocalStack(async () => {
    const account = await createProjectOwner(browser, info);
    const { page, current } = account;
    const root = auth + '/v1/' + current.projectId;
    const run = randomBytes(4).toString('hex');
    await page.goto('/api-keys?create=key');
    const csrf = await page.locator('#create-key input[name="_csrf"]').inputValue();
    const keys: string[] = [];
    try {
      for (const level of ['read', 'write']) {
        const created = await page.request.post('/api/v1/dashboard/credentials/key', {
          headers: { Accept: 'application/json' }, form: { _csrf: csrf, name: `agents-${run}-${level}`, scopes: `auth:${level}` },
        });
        expect(created.status()).toBe(201);
        const key = (await created.json()).secret as string;
        saveSecret(`agents-${level}`, key);
        keys.push(key);
      }
      const [readKey, writeKey] = keys;
      const read = { Authorization: 'Bearer ' + readKey };
      const write = { Authorization: 'Bearer ' + writeKey };

      const owner = await page.request.post(root + '/users', { headers: write, data: { email: await newAddress('agent-owner-' + run) } });
      expect(owner.status()).toBe(201);
      const user = (await owner.json()).id as string;

      const first = keyPair();
      const create = (jwk: Jwk, scopes = ['invoices:read']) => page.request.post(`${root}/users/${user}/agents`, {
        headers: write, data: { name: 'Invoice agent', allowed_scopes: scopes, public_jwk: jwk },
      });
      expect((await page.request.put(root + '/agent-policy', { headers: write, data: { allowed_scopes: [] } })).status()).toBe(200);
      expect((await create(first.publicJwk)).status()).toBe(400);
      const policy = await page.request.put(root + '/agent-policy', { headers: write, data: { allowed_scopes: ['invoices:read'] } });
      expect(policy.status()).toBe(200);
      expect(await (await page.request.get(root + '/agent-policy', { headers: read })).json()).toEqual({ allowed_scopes: ['invoices:read'] });
      expect((await page.request.put(root + '/agent-policy', { headers: read, data: { allowed_scopes: ['invoices:read'] } })).status()).toBe(403);

      expect((await create(first.publicJwk, ['invoices:write'])).status()).toBe(400);
      const withPrivate = await create(first.privateJwk);
      expect(withPrivate.status()).toBe(400);
      expect((await withPrivate.json()).error.code).toBe('private_key_material');
      const wrongCurve = await create({ ...first.publicJwk, crv: 'P-384' });
      expect((await wrongCurve.json()).error.code).toBe('invalid_jwk');
      expect((await page.request.post(`${root}/users/${user}/agents`, {
        headers: read, data: { name: 'Invoice agent', allowed_scopes: ['invoices:read'], public_jwk: first.publicJwk },
      })).status()).toBe(403);

      const created = await create(first.publicJwk);
      expect(created.status()).toBe(201);
      const agent = await created.json() as Agent;
      expect(agent).toMatchObject({ owner_user_id: user, status: 'active', allowed_scopes: ['invoices:read'], allow_self_subject: false, revoked_at: null, last_used_at: null });
      expect(agent.keys).toHaveLength(1);
      expect(JSON.stringify(agent)).not.toContain(first.publicJwk.x);
      expect((await create(first.publicJwk)).status()).toBe(409);

      const listed = await page.request.get(root + '/agents', { headers: read, params: { owner: user } });
      expect(listed.status()).toBe(200);
      expect((await listed.json()).items.map((item: Agent) => item.id)).toEqual([agent.id]);
      const owned = await page.request.get(`${root}/users/${user}/agents`, { headers: read });
      expect((await owned.json()).items.map((item: Agent) => item.id)).toEqual([agent.id]);
      expect((await page.request.get(`${root}/agents/${current.projectId}`, { headers: read })).status()).toBe(404);
      expect((await page.request.get(`${auth}/v1/${user}/agents/${agent.id}`, { headers: read })).status()).toBe(404);

      const addKey = (jwk: Jwk) => page.request.post(`${root}/agents/${agent.id}/keys`, { headers: write, data: { public_jwk: jwk } });
      const second = await addKey(keyPair().publicJwk);
      expect(second.status()).toBe(201);
      const secondKey = await second.json() as { kid: string; revoked_at: string | null };
      expect(secondKey.revoked_at).toBeNull();
      const third = await addKey(keyPair().publicJwk);
      expect(third.status()).toBe(409);
      expect((await third.json()).error.code).toBe('agent_key_limit');
      expect((await addKey(keyPair().privateJwk)).status()).toBe(400);

      expect((await page.request.delete(`${root}/agents/${agent.id}/keys/${secondKey.kid}`, { headers: read })).status()).toBe(403);
      expect((await page.request.delete(`${root}/agents/${agent.id}/keys/${secondKey.kid}`, { headers: write })).status()).toBe(204);
      expect((await page.request.delete(`${root}/agents/${agent.id}/keys/${secondKey.kid}`, { headers: write })).status()).toBe(204);
      const rotated = await addKey(keyPair().publicJwk);
      expect(rotated.status()).toBe(201);

      expect((await page.request.delete(`${root}/agents/${agent.id}`, { headers: write })).status()).toBe(204);
      expect((await page.request.delete(`${root}/agents/${agent.id}`, { headers: write })).status()).toBe(204);
      const revoked = await (await page.request.get(`${root}/agents/${agent.id}`, { headers: read })).json() as Agent;
      expect(revoked.status).toBe('revoked');
      expect(revoked.revoked_at).not.toBeNull();
      expect(revoked.keys).toHaveLength(3);
      expect(revoked.keys.every(key => key.revoked_at !== null)).toBe(true);
      const afterRevoke = await addKey(keyPair().publicJwk);
      expect(afterRevoke.status()).toBe(409);
      expect((await afterRevoke.json()).error.code).toBe('agent_inactive');

      const signedOutAgent = await create(keyPair().publicJwk);
      expect(signedOutAgent.status()).toBe(201);
      const live = await signedOutAgent.json() as Agent;
      expect((await page.request.post(`${root}/users/${user}/sign-out`, { headers: write })).status()).toBe(204);
      expect((await (await page.request.get(`${root}/agents/${live.id}`, { headers: read })).json()).status).toBe('revoked');

      await expect.poll(async () => {
        const types = new Set<string>();
        let cursor = '';
        do {
          const response = await page.request.get(root + '/events', { headers: read, params: { limit: 100, after: cursor } });
          expect(response.status()).toBe(200);
          const body = await response.json();
          for (const event of body.items as { type: string; data: { agent_id: string | null } }[]) {
            if (event.data.agent_id === agent.id) types.add(event.type);
          }
          cursor = body.next ?? '';
        } while (cursor);
        return ['agent.created', 'agent.key_added', 'agent.key_revoked', 'agent.revoked'].filter(type => types.has(type));
      }).toEqual(['agent.created', 'agent.key_added', 'agent.key_revoked', 'agent.revoked']);

      expect((await page.request.delete(`${root}/users/${user}`, { headers: write })).status()).toBe(204);
      expect((await page.request.get(`${root}/agents/${live.id}`, { headers: read })).status()).toBe(404);
    } finally {
      for (const key of keys) {
        const revoked = await page.request.post('/api/v1/dashboard/credentials/revoke', {
          headers: { Accept: 'application/json' }, form: { _csrf: csrf, keyId: keyId(key) },
        });
        expect(revoked.status()).toBe(204);
      }
      await account.close();
    }
  });
});
