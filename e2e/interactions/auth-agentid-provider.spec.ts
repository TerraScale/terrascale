import type { APIRequestContext } from '@playwright/test';
import { Agent, approvePlatformAgent, signInProof } from '../support/agents';
import { expect, test } from '../support/audit';
import { signInToken } from '../support/auth-management';
import { hostedReady } from '../support/hosted';
import { createProjectOwner } from '../support/project-owner';
import { RelyingParty, type SignInStart } from '../support/relying-party';
import { auth } from '../support/stack';

const issuer = auth + '/agents';
const json = { Accept: 'application/json' };

type Pending = { status: string; transaction_id: string; proof_url: string; client_id: string; client_name: string; expires_at: string };

async function pending(request: APIRequestContext, started: SignInStart): Promise<Pending> {
  const response = await request.get(started.url, { headers: json, maxRedirects: 0 });
  expect(response.status()).toBe(200);
  const body = await response.json() as Pending;
  expect(response.headers().link).toBe(`<${body.proof_url}>; rel="agent-proof"`);
  return body;
}

async function prove(request: APIRequestContext, url: string, proof: string): Promise<number> {
  return (await request.post(url, { data: { proof } })).status();
}

async function completed(request: APIRequestContext, rp: RelyingParty, started: SignInStart): Promise<string> {
  const response = await request.get(started.url, { headers: json, maxRedirects: 0 });
  expect(response.status()).toBe(302);
  return rp.callback(response.headers().location, started);
}

test('an app signs in a TerraScale account\'s agent with the public Sign in as agent provider', async ({ browser, request }, info) => {
  const account = await createProjectOwner(browser, info);
  const { page, current, audit } = account;
  const root = auth + '/v1/terrascale';
  const owned = { Authorization: 'Bearer ' + await signInToken(page.request, 'agentid-owner') };
  let agentId: string | undefined;
  try {
    const agent = await Agent.start(request, root, { name: 'Release agent', requested_scopes: ['mcp:read'] });
    const approved = await approvePlatformAgent(page, agent, current.password);
    agentId = approved.agentId;
    const kid = approved.kid;
    const proofFor = (body: Pending, claimed: { txn?: string; client_id?: string } = {}) =>
      signInProof(agent.key.privateKey, kid, approved.agentId, body.proof_url, { txn: body.transaction_id, client_id: body.client_id, ...claimed });

    const rp = new RelyingParty(request, issuer, 'https://rp.journey.terrascale.tech');
    const metadata = await rp.discovery();
    expect(metadata).toMatchObject({
      issuer, authorization_endpoint: issuer + '/oauth/authorize', token_endpoint: issuer + '/oauth/token', userinfo_endpoint: issuer + '/oauth/userinfo',
      jwks_uri: issuer + '/.well-known/jwks.json', response_types_supported: ['code'], grant_types_supported: ['authorization_code'],
      code_challenge_methods_supported: ['S256'], subject_types_supported: ['pairwise'], id_token_signing_alg_values_supported: ['ES256'],
      token_endpoint_auth_methods_supported: ['none'],
    });
    expect([...metadata.scopes_supported as string[]].sort()).toEqual(['openid', 'profile']);
    expect(metadata.end_session_endpoint).toBeUndefined();

    const evil = await request.get((await rp.start()).url.replace(encodeURIComponent(rp.redirectUri), encodeURIComponent('https://evil.example/auth/callback')), { headers: json, maxRedirects: 0 });
    expect(evil.status()).toBe(400);

    const started = await rp.start();
    const opened = await pending(request, started);
    expect(opened).toMatchObject({ status: 'pending', client_id: rp.clientId, client_name: rp.clientId, proof_url: `${issuer}/v1/authorize/${opened.transaction_id}/proof` });
    expect(await pending(request, started)).toEqual(opened);
    const other = await pending(request, await rp.start());
    expect(await prove(request, opened.proof_url, proofFor(opened, { client_id: 'https://other.journey.terrascale.tech' }))).toBe(401);
    expect(await prove(request, opened.proof_url, proofFor(other))).toBe(401);
    expect(await prove(request, opened.proof_url, proofFor(opened, { txn: other.transaction_id }))).toBe(401);
    expect(await pending(request, started)).toEqual(opened);
    const proof = proofFor(opened);
    expect(await prove(request, opened.proof_url, proof)).toBe(204);
    expect(await prove(request, opened.proof_url, proof)).toBe(401);
    expect(await prove(request, other.proof_url, proofFor(other))).toBe(204);

    const code = await completed(request, rp, started);
    const replay = await request.get(started.url, { headers: json, maxRedirects: 0 });
    expect(replay.status()).toBe(400);
    expect((await replay.json()).error).toBe('expired_transaction');
    const tokens = await rp.redeem(code, started);
    expect(tokens.status, JSON.stringify(tokens.body)).toBe(200);
    expect(tokens.body).toMatchObject({ token_type: 'Bearer', scope: 'openid profile' });
    expect(tokens.body.refresh_token).toBeUndefined();
    expect(tokens.body.expires_in as number).toBeLessThanOrEqual(600);

    const identity = await rp.verify(tokens.body.id_token as string, started);
    expect(identity).toMatchObject({ actor_type: 'agent', name: 'Release agent', jti: expect.any(String) });
    const subject = identity.sub as string;
    const ownerSubject = identity.owner_sub as string;
    expect(subject).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(ownerSubject).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(subject).not.toBe(ownerSubject);
    expect(JSON.stringify(identity)).not.toContain(approved.agentId);
    expect(identity.email).toBeUndefined();

    const accessToken = tokens.body.access_token as string;
    expect(accessToken.split('.')).toHaveLength(5);
    const userInfo = await request.get(issuer + '/oauth/userinfo', { headers: { Authorization: 'Bearer ' + accessToken } });
    expect(userInfo.status(), userInfo.headers()['www-authenticate']).toBe(200);
    expect(await userInfo.json()).toEqual({ sub: subject, actor_type: 'agent', name: 'Release agent', owner_sub: ownerSubject });
    expect((await request.get(auth + '/terrascale/oauth/userinfo', { headers: { Authorization: 'Bearer ' + accessToken } })).status()).toBe(401);
    expect((await request.get(issuer + '/oauth/userinfo', { headers: owned })).status()).toBe(401);
    expect((await request.get(root + '/me/agents', { headers: { Authorization: 'Bearer ' + accessToken } })).status()).toBe(401);
    expect(await rp.redeem(code, started)).toMatchObject({ status: 400, body: { error: 'invalid_grant' } });
    expect((await request.get(issuer + '/oauth/userinfo', { headers: { Authorization: 'Bearer ' + accessToken } })).status()).toBe(401);

    const elsewhere = new RelyingParty(request, issuer, 'https://elsewhere.journey.terrascale.tech');
    const narrow = await elsewhere.start('openid');
    const narrowOpened = await pending(request, narrow);
    expect(await prove(request, narrowOpened.proof_url, proofFor(narrowOpened))).toBe(204);
    const narrowTokens = await elsewhere.redeem(await completed(request, elsewhere, narrow), narrow);
    expect(narrowTokens.status, JSON.stringify(narrowTokens.body)).toBe(200);
    const narrowIdentity = await elsewhere.verify(narrowTokens.body.id_token as string, narrow);
    expect(narrowIdentity).toMatchObject({ actor_type: 'agent' });
    expect(narrowIdentity.sub).not.toBe(subject);
    expect(narrowIdentity.name).toBeUndefined();
    expect(narrowIdentity.owner_sub).toBeUndefined();
    const narrowAccess = { Authorization: 'Bearer ' + (narrowTokens.body.access_token as string) };
    const narrowInfo = await request.get(issuer + '/oauth/userinfo', { headers: narrowAccess });
    expect(narrowInfo.status()).toBe(200);
    expect(await narrowInfo.json()).toEqual({ sub: narrowIdentity.sub, actor_type: 'agent' });

    let returned = '';
    const continuing = (url: URL) => url.pathname === '/agents/oauth/authorize' && url.searchParams.has('ts_agentid_continue');
    await page.route(continuing, async (route) => {
      const answer = await route.fetch({ maxRedirects: 0 });
      expect(answer.status()).toBe(302);
      returned = answer.headers().location;
      await route.fulfill({ contentType: 'text/html', body: '<!doctype html><html lang="en"><title>App</title><main><h1>App</h1></main></html>' });
    });
    const browserStart = await rp.start();
    await page.goto(browserStart.url);
    await hostedReady(page);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Sign in as an agent');
    await expect(page.locator('.sub')).toHaveText(`${rp.clientId} wants to know which AI agent you are.`);
    await expect(page.locator('.alert[data-state=limited]')).toHaveText('Waiting for the agent to prove its key…');
    const proofUrl = await page.locator('[data-agent-proof]').getAttribute('data-agent-proof');
    const browserTransaction = new URL(proofUrl!).pathname.split('/')[4];
    expect(await prove(request, proofUrl!, signInProof(agent.key.privateKey, kid, approved.agentId, proofUrl!, { txn: browserTransaction, client_id: rp.clientId }))).toBe(204);
    await expect(page.locator('.alert[data-state=success]')).toHaveText(`Signed in. Returning to ${rp.clientId}…`);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('App');
    expect(rp.callback(returned, browserStart)).toBeTruthy();
    await page.unroute(continuing);
    audit.expectStatus(/\/agents\/oauth\/authorize\?/, 400);
    await page.goto(browserStart.url);
    await hostedReady(page);
    await expect(page.locator('.alert[data-state=error]')).toHaveText('This sign-in expired. Start again from the app.');
    await audit.check('sign in as agent expired');

    const again = await rp.start();
    const againOpened = await pending(request, again);
    expect(await prove(request, againOpened.proof_url, proofFor(againOpened))).toBe(204);
    const unredeemed = await completed(request, rp, again);
    expect((await request.delete(`${root}/me/agents/${approved.agentId}`, { headers: owned })).status()).toBe(204);
    agentId = undefined;
    expect((await request.get(issuer + '/oauth/userinfo', { headers: narrowAccess })).status()).toBe(401);
    const afterRevoke = await rp.start();
    const revokedOpened = await pending(request, afterRevoke);
    expect(await prove(request, revokedOpened.proof_url, proofFor(revokedOpened))).toBe(401);
    expect(await rp.redeem(unredeemed, again)).toMatchObject({ status: 400, body: { error: 'invalid_grant' } });
    await audit.finish();
  } finally {
    if (agentId) {
      await request.delete(`${root}/me/agents/${agentId}`, { headers: owned });
    }
    await account.close();
  }
});
