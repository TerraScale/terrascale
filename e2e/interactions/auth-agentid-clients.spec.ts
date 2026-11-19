import { Agent, approvePlatformAgent, signInProof } from '../support/agents';
import { expect, test } from '../support/audit';
import { signInToken, stepUp } from '../support/auth-management';
import { createDedicatedProjectOwner } from '../support/project-owner';
import { RelyingParty, authorizeStatus, completed, pending, prove } from '../support/relying-party';
import { auth } from '../support/stack';

const issuer = auth + '/agents';

test('a registered app signs in an agent with its client secret and sees the owner\'s email only while the owner shares it in Profile › Agents', async ({ browser, request }, info) => {
  const account = await createDedicatedProjectOwner(browser, info, 'Agent app owner');
  const { page, current, audit } = account;
  const root = auth + '/v1/terrascale';
  const clients = root + '/agentid-clients';
  const ownerToken = await signInToken(page.request, 'agentid-client-owner');
  const owned = { Authorization: 'Bearer ' + ownerToken };
  const stepped = async (purpose: string) => ({ ...owned, 'X-TerraScale-Step-Up-Proof': await stepUp(page.request, ownerToken, purpose, current.password) });
  const ownerEmail = (await (await request.get(auth + '/terrascale/oauth/userinfo', { headers: owned })).json()).email as string;
  const redirect = 'https://app.journey.terrascale.tech/auth/callback';
  try {
    const agent = await Agent.start(request, root, { name: 'Shopping agent', requested_scopes: ['mcp:read'] });
    const approved = await approvePlatformAgent(page, agent, current.password);
    const signIn = async (rp: RelyingParty, scope: string, method?: 'basic' | 'post') => {
      const started = await rp.start(scope);
      const opened = await pending(request, started);
      expect(opened).toMatchObject({ client_id: rp.clientId, client_name: 'Journey app' });
      expect(await prove(request, opened.proof_url,
        signInProof(agent.key.privateKey, approved.kid, approved.agentId, opened.proof_url, { txn: opened.transaction_id, client_id: opened.client_id }))).toBe(204);
      const tokens = await rp.redeem(await completed(request, rp, started), started, undefined, method);
      expect(tokens.status, JSON.stringify(tokens.body)).toBe(200);
      expect(tokens.body.scope).toBe(scope);
      return { identity: await rp.verify(tokens.body.id_token as string, started), access: { Authorization: 'Bearer ' + (tokens.body.access_token as string) } };
    };

    expect((await request.post(clients, { headers: owned, data: { name: 'Journey app', redirect_uris: [redirect] } })).status()).toBe(403);
    expect((await request.post(clients, { headers: owned, data: { name: 'Journey app', redirect_uris: ['http://app.journey.terrascale.tech/auth/callback'] } })).status()).toBe(400);
    const created = await request.post(clients, { headers: await stepped('create_oauth_client'), data: { name: 'Journey app', redirect_uris: [redirect] } });
    expect(created.status()).toBe(201);
    const client = await created.json() as { client_id: string; client_secret: string; name: string; redirect_uris: string[]; created_at: string };
    const clientId = client.client_id;
    expect(client).toMatchObject({ client_id: expect.stringMatching(/^[0-7][0-9A-HJKMNP-TV-Z]{25}$/), client_secret: expect.any(String), name: 'Journey app', redirect_uris: [redirect] });
    expect(created.headers().location).toBe('/v1/terrascale/agentid-clients/' + clientId);
    const listed = (await (await request.get(clients, { headers: owned })).json()).items as Record<string, unknown>[];
    expect(listed).toEqual([{ client_id: clientId, name: 'Journey app', redirect_uris: [redirect], created_at: client.created_at }]);

    const rp = new RelyingParty(request, issuer, clientId, redirect, client.client_secret);
    expect((await authorizeStatus(request, await rp.start(), { redirect_uri: 'https://app.journey.terrascale.tech/auth/other' })).status).toBe(400);
    const first = await signIn(rp, 'openid profile owner_email');
    expect(first.identity).toMatchObject({ aud: clientId, actor_type: 'agent', name: 'Shopping agent' });
    expect(first.identity.owner_email).toBeUndefined();
    expect(first.identity.owner_email_verified).toBeUndefined();
    const firstInfo = await (await request.get(issuer + '/oauth/userinfo', { headers: first.access })).json();
    expect(firstInfo).toEqual({ sub: first.identity.sub, actor_type: 'agent', name: 'Shopping agent', owner_sub: first.identity.owner_sub });

    await page.goto('/profile?tab=agents');
    const row = page.locator('#profile-agents tbody tr').filter({ hasText: 'Shopping agent' });
    const share = row.getByRole('switch', { name: 'Share my email with apps' });
    await expect(share).not.toBeChecked();
    await expect(row.locator('td[data-label="Email"] .ts-meta')).toHaveText('Apps you sign in to as this agent can see your email. Off by default.');
    await audit.check('profile agent email sharing off');
    await share.click();
    await page.waitForURL((url) => url.searchParams.get('status') === 'agent-email-shared');
    await expect(page.getByRole('status')).toHaveText('Email sharing on.');
    await expect(page.locator('#profile-agents tbody tr').filter({ hasText: 'Shopping agent' }).getByRole('switch', { name: 'Share my email with apps' })).toBeChecked();
    await audit.check('profile agent email sharing on');
    const mine = (await (await request.get(root + '/me/agents', { headers: owned })).json()).items.find((item: { id: string }) => item.id === approved.agentId);
    expect(mine.share_owner_email).toBe(true);

    expect(await (await request.get(issuer + '/oauth/userinfo', { headers: first.access })).json())
      .toEqual({ ...firstInfo, owner_email: ownerEmail, owner_email_verified: true });
    const second = await signIn(rp, 'openid owner_email', 'post');
    expect(second.identity).toMatchObject({ sub: first.identity.sub, owner_email: ownerEmail, owner_email_verified: true });
    expect(second.identity.name).toBeUndefined();
    expect(second.identity.owner_sub).toBeUndefined();
    const withoutScope = await signIn(rp, 'openid profile');
    expect(withoutScope.identity.owner_email).toBeUndefined();
    expect(await (await request.get(issuer + '/oauth/userinfo', { headers: withoutScope.access })).json()).toEqual(firstInfo);

    const open = new RelyingParty(request, issuer, 'https://app.journey.terrascale.tech');
    const openRefused = await authorizeStatus(request, await open.start('openid owner_email'), {});
    expect(openRefused.status).toBe(302);
    expect(new URL(openRefused.location!).searchParams.get('error')).toBe('invalid_scope');

    const unshared = await request.patch(`${root}/me/agents/${approved.agentId}`, { headers: owned, data: { share_owner_email: false } });
    expect(unshared.status()).toBe(200);
    expect(await unshared.json()).toMatchObject({ id: approved.agentId, share_owner_email: false });
    expect((await request.patch(`${root}/me/agents/${approved.agentId}`, { headers: owned, data: { share_owner_email: 'yes' } })).status()).toBe(400);
    expect(await (await request.get(issuer + '/oauth/userinfo', { headers: second.access })).json()).toEqual({ sub: first.identity.sub, actor_type: 'agent' });
    expect((await signIn(rp, 'openid owner_email')).identity.owner_email).toBeUndefined();

    const probe = await rp.start();
    const accepts = async (secret: string) => {
      const answer = await rp.redeem('not-a-code', probe, secret);
      return answer.status === 400 && answer.body.error === 'invalid_grant';
    };
    const refusesClient = async (secret: string | undefined) => {
      const answer = await rp.redeem('not-a-code', probe, secret);
      return { status: answer.status, error: answer.body.error };
    };
    const refused = { status: 401, error: 'invalid_client' };
    expect(await accepts(client.client_secret)).toBe(true);
    expect(await refusesClient('wrong-' + client.client_secret)).toEqual(refused);
    const secretless = await rp.start('openid');
    const secretlessOpened = await pending(request, secretless);
    expect(await prove(request, secretlessOpened.proof_url, signInProof(agent.key.privateKey, approved.kid, approved.agentId, secretlessOpened.proof_url,
      { txn: secretlessOpened.transaction_id, client_id: secretlessOpened.client_id }))).toBe(204);
    const secretlessCode = await completed(request, rp, secretless);
    const withoutSecret = await rp.redeem(secretlessCode, secretless, undefined, 'none');
    expect({ status: withoutSecret.status, error: withoutSecret.body.error }).toEqual(refused);
    expect((await rp.redeem(secretlessCode, secretless)).status).toBe(200);
    const rotated = await request.post(`${clients}/${clientId}/secret/rotate`, { headers: await stepped('rotate_oauth_client') });
    expect(rotated.status()).toBe(200);
    const rotatedSecret = await rotated.json() as { client_id: string; client_secret: string; previous_secret_expires_at: string };
    expect(rotatedSecret).toMatchObject({ client_id: clientId, previous_secret_expires_at: expect.any(String) });
    expect(await accepts(client.client_secret)).toBe(true);
    expect(await accepts(rotatedSecret.client_secret)).toBe(true);
    const ended = await request.post(`${clients}/${clientId}/secret/rotate`, { headers: await stepped('rotate_oauth_client'), data: { grace_seconds: 0 } });
    expect(ended.status()).toBe(200);
    const lastSecret = (await ended.json()).client_secret as string;
    expect(await refusesClient(client.client_secret)).toEqual(refused);
    expect(await refusesClient(rotatedSecret.client_secret)).toEqual(refused);
    expect(await accepts(lastSecret)).toBe(true);

    expect((await request.delete(`${clients}/${clientId}`, { headers: owned })).status()).toBe(403);
    expect((await request.delete(`${clients}/${clientId}`, { headers: await stepped('delete_oauth_client') })).status()).toBe(204);
    expect(await refusesClient(lastSecret)).toEqual(refused);
    expect((await authorizeStatus(request, probe, {})).status).toBe(400);
    expect((await (await request.get(clients, { headers: owned })).json()).items).toEqual([]);
    await audit.finish();
  } finally {
    await account.remove();
  }
});
