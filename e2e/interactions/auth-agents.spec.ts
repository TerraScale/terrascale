import { createPrivateKey, createPublicKey, generateKeyPairSync, randomBytes, sign, verify } from 'node:crypto';
import { expect, test } from '../support/audit';
import { keyId } from '../support/api-key';
import { signInToken } from '../support/auth-management';
import { createProjectOwner } from '../support/project-owner';
import { auth } from '../support/stack';
import { saveSecret } from '../support/state';

type Jwk = { kty: string; crv: string; x: string; y: string; d?: string };
type Agent = { id: string; owner_user_id: string; status: string; allowed_scopes: string[]; keys: { kid: string; thumbprint: string; revoked_at: string | null }[]; revoked_at: string | null; last_used_at: string | null };

const jwtBearer = 'urn:ietf:params:oauth:grant-type:jwt-bearer';

const base64url = (value: unknown) => Buffer.from(JSON.stringify(value)).toString('base64url');

function assertion(privateJwk: Jwk, kid: string, agent: string, audience: string, header: Record<string, unknown> = {}): string {
  const now = Math.floor(Date.now() / 1000);
  const signed = base64url({ alg: 'ES256', typ: 'JWT', kid, ...header }) + '.' + base64url({
    iss: agent, sub: agent, aud: audience, iat: now, exp: now + 60, jti: randomBytes(16).toString('hex'),
  });
  const signature = sign('sha256', Buffer.from(signed), { key: createPrivateKey({ key: privateJwk, format: 'jwk' }), dsaEncoding: 'ieee-p1363' });
  return signed + '.' + signature.toString('base64url');
}

function claims(token: string): Record<string, unknown> {
  return JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()) as Record<string, unknown>;
}

function keyPair(): { publicJwk: Jwk; privateJwk: Jwk } {
  const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  return { publicJwk: publicKey.export({ format: 'jwk' }) as Jwk, privateJwk: privateKey.export({ format: 'jwk' }) as Jwk };
}

test('project agents hold their own public keys, stay within their limits and can be revoked', async ({ browser }, info) => {
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

    const owner = await page.request.post(root + '/users', { headers: write, data: { email: `agent-owner-${run}@example.com` } });
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
    const ownerToken = await signInToken(page.request, 'agents-owner');
    const projects = `${auth}/v1/terrascale/organizations/${current.organizationId}/projects`;
    const otherProject = await page.request.post(projects, {
      headers: { Authorization: 'Bearer ' + ownerToken }, data: { name: 'Agent isolation ' + run },
    });
    expect(otherProject.status()).toBe(201);
    const other = (await otherProject.json()).id as string;
    try {
      const otherKey = await page.request.post(`${projects}/${other}/api-keys`, {
        headers: { Authorization: 'Bearer ' + ownerToken }, data: { name: 'agent isolation reader', scopes: ['auth:read'] },
      });
      expect(otherKey.status()).toBe(201);
      const otherSecret = (await otherKey.json()).key as string;
      saveSecret('agents-isolation-key', otherSecret);
      const otherRead = { Authorization: 'Bearer ' + otherSecret };
      expect((await page.request.get(`${auth}/v1/${other}/agents/${agent.id}`, { headers: otherRead })).status()).toBe(404);
      const otherList = await page.request.get(`${auth}/v1/${other}/agents`, { headers: otherRead, params: { owner: user } });
      expect(otherList.status()).toBe(200);
      expect((await otherList.json()).items).toEqual([]);
      expect((await page.request.get(`${auth}/v1/${other}/agents/${agent.id}`, { headers: read })).status()).toBe(404);
      const otherEndpoint = `${auth}/${other}/oauth/token`;
      const crossProject = await page.request.post(otherEndpoint, {
        form: { grant_type: jwtBearer, assertion: assertion(first.privateJwk, agent.keys[0].kid, agent.id, otherEndpoint) },
      });
      expect(crossProject.status()).toBe(400);
      expect((await crossProject.json()).error).toBe('invalid_grant');
    } finally {
      const deleted = await page.request.delete(`${projects}/${other}`, { headers: { Authorization: 'Bearer ' + ownerToken } });
      expect(deleted.status()).toBe(204);
    }

    const issuer = `${auth}/${current.projectId}`;
    const tokenEndpoint = issuer + '/oauth/token';
    const discovery = await (await page.request.get(issuer + '/.well-known/openid-configuration')).json() as { grant_types_supported: string[]; jwks_uri: string };
    expect(discovery.grant_types_supported).toContain(jwtBearer);
    const grant = (jwt: string, form: Record<string, string> = {}) => page.request.post(tokenEndpoint, { form: { grant_type: jwtBearer, assertion: jwt, ...form } });
    const refused = async (jwt: string, form: Record<string, string> = {}) => {
      const response = await grant(jwt, form);
      expect(response.status()).toBe(400);
      expect((await response.json()).error).toBe('invalid_grant');
    };
    const firstKid = agent.keys[0].kid;
    const delegatedAssertion = assertion(first.privateJwk, firstKid, agent.id, tokenEndpoint);
    const delegated = await grant(delegatedAssertion);
    expect(delegated.status()).toBe(200);
    const delegatedBody = await delegated.json();
    saveSecret('agent-delegated-token', delegatedBody.access_token);
    expect(delegatedBody).toMatchObject({ token_type: 'Bearer', scope: 'invoices:read' });
    expect(delegatedBody.expires_in).toBeLessThanOrEqual(600);
    expect(delegatedBody).not.toHaveProperty('refresh_token');
    expect(delegatedBody).not.toHaveProperty('id_token');
    const delegatedClaims = claims(delegatedBody.access_token);
    expect(delegatedClaims).toMatchObject({
      iss: issuer, sub: user, act: { sub: agent.id }, token_use: 'agent', actor_type: 'agent', agent_id: agent.id, scope: 'invoices:read',
    });
    expect(delegatedClaims).not.toHaveProperty('aud');
    expect((delegatedClaims.exp as number) - (delegatedClaims.iat as number)).toBeLessThanOrEqual(600);
    const [signedPart, signaturePart] = [delegatedBody.access_token.split('.').slice(0, 2).join('.'), delegatedBody.access_token.split('.')[2]];
    const header = JSON.parse(Buffer.from(delegatedBody.access_token.split('.')[0], 'base64url').toString()) as { alg: string; kid: string };
    expect(header.alg).toBe('ES256');
    const jwks = await (await page.request.get(discovery.jwks_uri)).json() as { keys: (Jwk & { kid: string })[] };
    const signingKey = jwks.keys.find(key => key.kid === header.kid)!;
    expect(verify('sha256', Buffer.from(signedPart), { key: createPublicKey({ key: signingKey, format: 'jwk' }), dsaEncoding: 'ieee-p1363' },
      Buffer.from(signaturePart, 'base64url'))).toBe(true);
    expect((await page.request.get(issuer + '/oauth/userinfo', { headers: { Authorization: 'Bearer ' + delegatedBody.access_token } })).status()).toBe(401);
    expect((await (await page.request.get(`${root}/agents/${agent.id}`, { headers: read })).json() as Agent).last_used_at).not.toBeNull();

    await refused(delegatedAssertion);
    await refused(assertion(first.privateJwk, firstKid, agent.id, issuer));
    await refused(assertion(first.privateJwk, firstKid, agent.id, tokenEndpoint), { scope: 'invoices:write' });
    await refused(assertion(first.privateJwk, firstKid, agent.id, tokenEndpoint), { requested_subject: 'agent' });
    await refused(assertion(keyPair().privateJwk, firstKid, agent.id, tokenEndpoint));
    await refused(assertion(first.privateJwk, firstKid, agent.id, tokenEndpoint, { alg: 'HS256' }));
    await refused(assertion(first.privateJwk, firstKid, agent.id, tokenEndpoint, { jwk: first.publicJwk }));
    const racedAssertion = assertion(first.privateJwk, firstKid, agent.id, tokenEndpoint);
    const replayRace = await Promise.all([grant(racedAssertion), grant(racedAssertion)]);
    expect(replayRace.map(response => response.status()).sort()).toEqual([200, 400]);

    const selfKeys = [keyPair(), keyPair()];
    const selfCreated = await page.request.post(`${root}/users/${user}/agents`, {
      headers: write, data: { name: 'Self agent', allowed_scopes: ['invoices:read'], allow_self_subject: true, public_jwk: selfKeys[0].publicJwk },
    });
    expect(selfCreated.status()).toBe(201);
    const selfAgent = await selfCreated.json() as Agent;
    const self = await grant(assertion(selfKeys[0].privateJwk, selfAgent.keys[0].kid, selfAgent.id, tokenEndpoint), { requested_subject: 'agent' });
    expect(self.status()).toBe(200);
    const selfToken = (await self.json()).access_token as string;
    saveSecret('agent-self-token', selfToken);
    const selfClaims = claims(selfToken);
    expect(selfClaims).toMatchObject({ sub: selfAgent.id, owner_sub: user, token_use: 'agent', actor_type: 'agent', agent_id: selfAgent.id });
    expect(selfClaims).not.toHaveProperty('act');
    await refused(assertion(first.privateJwk, firstKid, selfAgent.id, tokenEndpoint));

    const expiring = keyPair();
    const expiringCreated = await page.request.post(`${root}/users/${user}/agents`, {
      headers: write, data: { name: 'Expiring agent', allowed_scopes: ['invoices:read'], expires_at: new Date(Date.now() + 5_000).toISOString(), public_jwk: expiring.publicJwk },
    });
    expect(expiringCreated.status()).toBe(201);
    const expiringAgent = await expiringCreated.json() as Agent;
    const expiringGrant = () => grant(assertion(expiring.privateJwk, expiringAgent.keys[0].kid, expiringAgent.id, tokenEndpoint));
    expect((await expiringGrant()).status()).toBe(200);
    await expect.poll(async () => (await expiringGrant()).status()).toBe(400);

    const secondOwner = await page.request.post(root + '/users', { headers: write, data: { email: `agent-second-owner-${run}@example.com` } });
    expect(secondOwner.status()).toBe(201);
    const secondUser = (await secondOwner.json()).id as string;
    const disabledKeys = keyPair();
    const disabledCreated = await page.request.post(`${root}/users/${secondUser}/agents`, {
      headers: write, data: { name: 'Disabled owner agent', allowed_scopes: ['invoices:read'], public_jwk: disabledKeys.publicJwk },
    });
    expect(disabledCreated.status()).toBe(201);
    const disabledAgent = await disabledCreated.json() as Agent;
    const disabledGrant = () => assertion(disabledKeys.privateJwk, disabledAgent.keys[0].kid, disabledAgent.id, tokenEndpoint);
    expect((await grant(disabledGrant())).status()).toBe(200);
    expect((await page.request.post(`${root}/users/${secondUser}/disable`, { headers: write })).status()).toBe(200);
    await refused(disabledGrant());
    expect((await page.request.delete(`${root}/users/${secondUser}`, { headers: write })).status()).toBe(204);
    await refused(disabledGrant());
    const selfSecond = await page.request.post(`${root}/agents/${selfAgent.id}/keys`, { headers: write, data: { public_jwk: selfKeys[1].publicJwk } });
    expect(selfSecond.status()).toBe(201);
    const selfSecondKid = (await selfSecond.json()).kid as string;
    expect((await page.request.delete(`${root}/agents/${selfAgent.id}/keys/${selfSecondKid}`, { headers: write })).status()).toBe(204);
    await refused(assertion(selfKeys[1].privateJwk, selfSecondKid, selfAgent.id, tokenEndpoint));

    const addKey = (jwk: Jwk) => page.request.post(`${root}/agents/${agent.id}/keys`, { headers: write, data: { public_jwk: jwk } });
    const racing = await Promise.all([addKey(keyPair().publicJwk), addKey(keyPair().publicJwk)]);
    expect(racing.map(response => response.status()).sort()).toEqual([201, 409]);
    const second = racing.find(response => response.status() === 201)!;
    const secondKey = await second.json() as { kid: string; revoked_at: string | null };
    expect(secondKey.revoked_at).toBeNull();
    const raced = await (await page.request.get(`${root}/agents/${agent.id}`, { headers: read })).json() as Agent;
    expect(raced.keys.filter(key => key.revoked_at === null)).toHaveLength(2);
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
    await refused(assertion(first.privateJwk, firstKid, agent.id, tokenEndpoint));
    const afterRevoke = await addKey(keyPair().publicJwk);
    expect(afterRevoke.status()).toBe(409);
    expect((await afterRevoke.json()).error.code).toBe('agent_inactive');

    const signedOutKeys = keyPair();
    const signedOutAgent = await create(signedOutKeys.publicJwk);
    expect(signedOutAgent.status()).toBe(201);
    const live = await signedOutAgent.json() as Agent;
    expect((await grant(assertion(signedOutKeys.privateJwk, live.keys[0].kid, live.id, tokenEndpoint))).status()).toBe(200);
    expect((await page.request.post(`${root}/users/${user}/sign-out`, { headers: write })).status()).toBe(204);
    expect((await (await page.request.get(`${root}/agents/${live.id}`, { headers: read })).json()).status).toBe('revoked');
    await refused(assertion(signedOutKeys.privateJwk, live.keys[0].kid, live.id, tokenEndpoint));

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
