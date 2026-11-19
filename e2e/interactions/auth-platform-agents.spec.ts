import type { APIRequestContext } from '@playwright/test';
import { Agent, assertion, claims, dashboardSecret, exchangeAgentToken, platformTokenEndpoint } from '../support/agents';
import { expect, test } from '../support/audit';
import { signInToken } from '../support/auth-management';
import { hostedReady } from '../support/hosted';
import { createProjectOwner } from '../support/project-owner';
import { enterSecret } from '../support/secret';
import { auth, onLocalStack } from '../support/stack';
import { saveSecret } from '../support/state';

const invalidGrant = { status: 400, body: expect.objectContaining({ error: 'invalid_grant' }) };

async function agentToken(request: APIRequestContext, agent: Agent, agentId: string, kid: string, subject = 'owner'): Promise<{ status: number; body: Record<string, unknown> }> {
  const response = await request.post(platformTokenEndpoint, {
    form: {
      grant_type: 'urn:ietf:params:oauth:grant-type:jwt-bearer', assertion: assertion(agent.key.privateKey, kid, agentId, platformTokenEndpoint),
      scope: 'mcp:read mcp:write', requested_subject: subject,
    },
  });
  return { status: response.status(), body: await response.json() };
}

test('a TerraScale account enrolls an agent, the dashboard exchanges its token for the owner, and revoking it stops both', async ({ browser, request }, info) => {
  const account = await createProjectOwner(browser, info);
  const { page, current, audit } = account;
  const root = auth + '/v1/terrascale';
  const ownerToken = await signInToken(page.request, 'platform-agent-owner');
  const owned = { Authorization: 'Bearer ' + ownerToken };
  let agentId: string | undefined;
  try {
    const agent = await Agent.start(request, root, { name: 'Coding agent', requested_scopes: ['mcp:read', 'mcp:write', 'invoices:read'], allow_self_subject: true });
    expect(agent.enrollment).toMatchObject({ verification_uri: `${auth}/ui/terrascale/agent-enrollments`, expires_in: 600, interval: 5 });

    await page.goto(agent.enrollment.verification_uri_complete);
    await hostedReady(page);
    await expect(page.locator('.device-code')).toHaveText(agent.enrollment.user_code);
    await expect(page.locator('.sub')).toHaveText('Coding agent wants to act for you in TerraScale.');
    await expect(page.locator('.scopes li')).toHaveText(['mcp:read', 'mcp:write']);
    await expect(page.getByText('It also asks to act as itself, not only for you.')).toBeVisible();
    await expect(page.locator('code')).toHaveText(agent.key.thumbprint.slice(0, 16));
    await audit.check('platform agent enrollment request');
    await page.getByRole('button', { name: 'Approve', exact: true }).click();
    await page.waitForURL((url) => url.pathname.startsWith('/ui/terrascale/step-up/'));
    await hostedReady(page);
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Approve an AI agent');
    await enterSecret(page.locator('input[type=password]'), current.password);
    await page.getByRole('button', { name: 'Verify and continue' }).click();
    await page.waitForURL(agent.enrollment.verification_uri_complete);
    await hostedReady(page);
    await page.getByRole('button', { name: 'Approve', exact: true }).click();
    await expect(page.locator('.alert[data-state=success]')).toHaveText('Agent approved. You can close this page.');
    await audit.check('platform agent approved');

    const approved = await agent.poll();
    expect(approved.status).toBe(200);
    agentId = approved.body.agent_id!;
    const kid = approved.body.kid!;

    const granted = await agentToken(request, agent, agentId, kid);
    expect(granted.status).toBe(200);
    expect(granted.body).toMatchObject({ token_type: 'Bearer', expires_in: 300, scope: 'mcp:read mcp:write' });
    const token = granted.body.access_token as string;
    saveSecret('platform-agent-token', token);
    const delegated = claims(token);
    expect(delegated).toMatchObject({ iss: auth + '/terrascale', token_use: 'agent', actor_type: 'agent', agent_id: agentId, act: { sub: agentId }, scope: 'mcp:read mcp:write' });
    expect(delegated.aud).toBeUndefined();
    const ownerId = delegated.sub as string;
    expect(ownerId).not.toBe(agentId);
    expect((await request.get(root + '/me/agents', { headers: { Authorization: 'Bearer ' + token } })).status()).toBe(401);
    const selfGranted = await agentToken(request, agent, agentId, kid, 'agent');
    expect(selfGranted.status).toBe(200);
    const selfToken = selfGranted.body.access_token as string;
    saveSecret('platform-agent-self-token', selfToken);
    expect(claims(selfToken)).toMatchObject({ sub: agentId, owner_sub: ownerId });

    const mine = await request.get(root + '/me/agents', { headers: owned });
    expect(mine.status()).toBe(200);
    const listed = (await mine.json()).items as { id: string; owner_user_id: string; status: string; allowed_scopes: string[]; keys: { kid: string }[]; last_used_at: string | null }[];
    expect(listed.find((item) => item.id === agentId)).toMatchObject({
      owner_user_id: ownerId, status: 'active', allowed_scopes: ['mcp:read', 'mcp:write'], keys: [{ kid }], last_used_at: expect.any(String),
    });

    let exchanged = '';
    await onLocalStack(async () => {
      expect((await exchangeAgentToken(request, token, dashboardSecret() + 'x')).status).toBe(401);
      expect(await exchangeAgentToken(request, ownerToken)).toEqual(invalidGrant);
      expect(await exchangeAgentToken(request, selfToken)).toEqual(invalidGrant);
      const owner = await exchangeAgentToken(request, token);
      expect(owner.status).toBe(200);
      expect(owner.body.refresh_token).toBeUndefined();
      expect(owner.body.expires_in as number).toBeLessThanOrEqual(300);
      exchanged = owner.body.access_token as string;
      saveSecret('platform-agent-owner-token', exchanged);
      const acting = claims(exchanged);
      expect(acting).toMatchObject({ sub: ownerId, agent_id: agentId, act: { sub: agentId }, token_use: 'user' });
      expect(acting.exp as number).toBeLessThanOrEqual(delegated.exp as number);
      expect(acting.ts_auth_mfa).toBeUndefined();
      expect(acting.platform_role).toBeUndefined();
      const actingList = await request.get(root + '/me/agents', { headers: { Authorization: 'Bearer ' + exchanged } });
      expect(actingList.status()).toBe(200);
      expect((await actingList.json()).items.map((item: { id: string }) => item.id)).toContain(agentId);
      const userInfo = await request.get(auth + '/terrascale/oauth/userinfo', { headers: { Authorization: 'Bearer ' + exchanged } });
      expect(userInfo.status()).toBe(200);
      expect((await userInfo.json()).sub).toBe(ownerId);
    });

    expect((await request.delete(`${root}/me/agents/${agentId}`, { headers: owned })).status()).toBe(204);
    expect((await request.delete(`${root}/me/agents/${agentId}`, { headers: owned })).status()).toBe(204);
    const revoked = (await (await request.get(root + '/me/agents', { headers: owned })).json()).items.find((item: { id: string }) => item.id === agentId);
    expect(revoked).toMatchObject({ status: 'revoked', revoked_at: expect.any(String) });
    expect(await agentToken(request, agent, agentId, kid)).toEqual(invalidGrant);
    await onLocalStack(async () => {
      expect((await request.get(root + '/me/agents', { headers: { Authorization: 'Bearer ' + exchanged } })).status()).toBe(401);
      expect((await request.get(auth + '/terrascale/oauth/userinfo', { headers: { Authorization: 'Bearer ' + exchanged } })).status()).toBe(401);
      expect(await exchangeAgentToken(request, token)).toEqual(invalidGrant);
    });
    agentId = undefined;
    await audit.finish();
  } finally {
    if (agentId) {
      await request.delete(`${root}/me/agents/${agentId}`, { headers: owned });
    }
    await account.close();
  }
});
