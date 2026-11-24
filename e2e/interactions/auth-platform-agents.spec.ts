import type { APIRequestContext } from '@playwright/test';
import { Agent, assertion, claims, dashboardSecret, exchangeAgentToken, exchangeForProject, platformTokenEndpoint } from '../support/agents';
import { expect, test } from '../support/audit';
import { signInToken } from '../support/auth-management';
import { hostedReady } from '../support/hosted';
import { createProjectOwner } from '../support/project-owner';
import { enterSecret } from '../support/secret';
import { auth, onLocalStack } from '../support/stack';
import { saveSecret } from '../support/state';

const invalidGrant = { status: 400, body: expect.objectContaining({ error: 'invalid_grant' }) };

async function agentToken(request: APIRequestContext, agent: Agent, agentId: string, kid: string, subject = 'owner', scope = 'mcp:read mcp:write'): Promise<{ status: number; body: Record<string, unknown> }> {
  const response = await request.post(platformTokenEndpoint, {
    form: {
      grant_type: 'urn:ietf:params:oauth:grant-type:jwt-bearer', assertion: assertion(agent.key.privateKey, kid, agentId, platformTokenEndpoint),
      scope, requested_subject: subject,
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
    const approvedAgent = agentId;
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
      expect(acting.agent_scope).toBe('mcp:read mcp:write');
      expect([acting.aud].flat()).toEqual([acting.iss]);

      const databases = `https://api.terrascale.tech/v1/projects/${current.projectId}/databases`;
      expect((await request.get(databases, { headers: { Authorization: 'Bearer ' + exchanged } })).status()).toBe(401);
      expect(await exchangeForProject(request, exchanged, current.organizationId, ''))
        .toEqual({ status: 400, body: expect.objectContaining({ error: 'invalid_target' }) });

      const projectToken = await exchangeForProject(request, exchanged, current.organizationId, current.projectId);
      expect(projectToken.status).toBe(200);
      const projectAccess = projectToken.body.access_token as string;
      saveSecret('platform-agent-project-token', projectAccess);
      expect((await request.get(databases, { headers: { Authorization: 'Bearer ' + projectAccess } })).status()).toBe(200);
      const projectClaims = claims(projectAccess);
      expect(projectClaims.exp as number).toBeLessThanOrEqual(acting.exp as number);
      expect(projectClaims).toMatchObject({ sub: ownerId, agent_id: agentId, act: { sub: agentId }, org_id: current.organizationId, project_id: current.projectId });
      expect((projectClaims.scope as string).split(' ').sort()).toEqual(['databases:read', 'databases:write', 'sql:read', 'sql:write']);
      expect(await exchangeForProject(request, exchanged, current.organizationId, current.projectId, 'auth:read'))
        .toEqual({ status: 400, body: expect.objectContaining({ error: 'invalid_scope' }) });

      const readGranted = await agentToken(request, agent, approvedAgent, kid, 'owner', 'mcp:read');
      expect(readGranted.status).toBe(200);
      const readToken = readGranted.body.access_token as string;
      saveSecret('platform-agent-read-token', readToken);
      const readOwner = await exchangeAgentToken(request, readToken);
      expect(readOwner.status).toBe(200);
      const readExchanged = readOwner.body.access_token as string;
      saveSecret('platform-agent-read-owner-token', readExchanged);
      expect(claims(readExchanged)).toMatchObject({ agent_id: agentId, act: { sub: agentId }, agent_scope: 'mcp:read' });
      expect((await request.get(databases, { headers: { Authorization: 'Bearer ' + readExchanged } })).status()).toBe(401);
      const readProject = await exchangeForProject(request, readExchanged, current.organizationId, current.projectId);
      expect(readProject.status).toBe(200);
      const readProjectAccess = readProject.body.access_token as string;
      saveSecret('platform-agent-read-project-token', readProjectAccess);
      expect((claims(readProjectAccess).scope as string).split(' ').sort()).toEqual(['databases:read', 'sql:read']);
      for (const scope of ['databases:write', 'sql:write', 'auth:write']) {
        expect(await exchangeForProject(request, readExchanged, current.organizationId, current.projectId, scope))
          .toEqual({ status: 400, body: expect.objectContaining({ error: 'invalid_scope' }) });
      }

      const actingHeaders = { Authorization: 'Bearer ' + exchanged, Origin: auth };
      const projectRoot = `${root}/organizations/${current.organizationId}/projects/${current.projectId}`;
      const refusedMutations = [
        request.post(`${projectRoot}/api-keys`, { headers: actingHeaders, data: { name: 'agent key', scopes: ['sql:read'] } }),
        request.post(`${root}/organizations/${current.organizationId}/api-keys`, { headers: actingHeaders, data: { name: 'agent key', scopes: ['sql:read'] } }),
        request.delete(projectRoot, { headers: actingHeaders }),
        request.post(`${root}/account/sign-out`, { headers: actingHeaders }),
        request.post(`${root}/organizations`, { headers: actingHeaders, data: { name: 'Agent organization' } }),
        request.put(`${projectRoot}/members/${ownerId}`, { headers: actingHeaders, data: { role: 'viewer' } }),
        request.delete(`${root}/me/agents/${agentId}`, { headers: actingHeaders }),
        request.put(`${auth}/v1/${current.projectId}/providers/google`, { headers: actingHeaders, data: { client_id: 'agent', client_secret: 'agent' } }),
        request.patch(`${auth}/v1/${current.projectId}/users/${ownerId}`, { headers: actingHeaders, data: { custom_claims: { role: 'admin' } } }),
      ];
      for (const refused of await Promise.all(refusedMutations)) {
        expect(refused.status()).toBe(403);
        expect((await refused.json()).error.code).toBe('agent_forbidden');
      }
      expect((await request.get(`${root}/organizations/${current.organizationId}/projects`, { headers: { Authorization: 'Bearer ' + exchanged } })).status()).toBe(200);
      const projectName = (await (await request.get(projectRoot, { headers: owned })).json()).name as string;
      const readRename = await request.patch(projectRoot, { headers: { Authorization: 'Bearer ' + readExchanged }, data: { name: projectName } });
      expect(readRename.status()).toBe(403);
      expect((await readRename.json()).error.code).toBe('agent_forbidden');
      const writeRename = await request.patch(projectRoot, { headers: { Authorization: 'Bearer ' + exchanged }, data: { name: projectName } });
      expect(writeRename.status()).toBe(200);
      expect((await writeRename.json()).name).toBe(projectName);
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

    const organizations = await request.get(root + '/organizations', { headers: owned });
    expect(organizations.status()).toBe(200);
    const organizationIds = (await organizations.json()).items.map((item: { id: string }) => item.id);
    expect(organizationIds).toContain(current.organizationId);
    expect(organizationIds).not.toContain('terrascale');
    const projects = await request.get(`${root}/organizations/${current.organizationId}/projects`, { headers: owned });
    expect(projects.status()).toBe(200);
    const projectIds = (await projects.json()).items.map((item: { id: string }) => item.id);
    expect(projectIds).toContain(current.projectId);
    expect(projectIds).not.toContain('terrascale');
    expect((await request.get(`${root}/organizations/terrascale/projects`, { headers: owned })).status()).toBe(404);
    expect((await request.get(`${root}/organizations/terrascale/projects/terrascale`, { headers: owned })).status()).toBe(404);
    expect((await request.delete(`${root}/organizations/terrascale/projects/terrascale`, { headers: owned })).status()).toBe(404);
    await audit.finish();
  } finally {
    if (agentId) {
      await request.delete(`${root}/me/agents/${agentId}`, { headers: owned });
    }
    await account.close();
  }
});

test('internal TerraScale projects are never customer end-user tenants', async ({ request }) => {
  for (const project of ['00000000000000000000000002', '00000000000000000000000005']) {
    const signUp = await request.post(`${auth}/ui/${project}/sign-up`, { headers: { Origin: auth }, form: { email: 'nobody@example.com', password: 'not-a-real-password-1' } });
    expect(signUp.status()).toBe(404);
    expect((await request.get(`${auth}/ui/${project}/sign-in`)).status()).toBe(404);
    expect((await request.get(`${auth}/${project}/.well-known/jwks.json`)).status()).toBe(404);
    expect((await request.get(`${auth}/${project}/.well-known/openid-configuration`)).status()).toBe(404);
  }
  expect((await request.get(`${auth}/terrascale/.well-known/jwks.json`)).status()).toBe(200);
});
