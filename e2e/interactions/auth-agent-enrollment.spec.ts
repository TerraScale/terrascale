import { randomBytes } from 'node:crypto';
import type { Page } from '@playwright/test';
import { Agent, agentKey, proof } from '../support/agents';
import { Audit, auditedContext, expect, test } from '../support/audit';
import { authStorage, signInToken } from '../support/auth-management';
import { hostedReady } from '../support/hosted';
import { createProjectOwner } from '../support/project-owner';
import { enterSecret } from '../support/secret';
import { auth, dash, desktop, onLocalStack } from '../support/stack';
import { saveSecret } from '../support/state';
import { sdkAgentEnrollment } from '../sdk/agent-identities';
import { cliAgentEnrollment } from '../sdk/cli-agent-identities';

async function openRequest(page: Page, audit: Audit, agent: Agent, label: string): Promise<void> {
  await page.goto(agent.enrollment.verification_uri_complete);
  await hostedReady(page);
  await expect(page.locator('.device-code')).toHaveText(agent.enrollment.user_code);
  await audit.check(label);
}

test('an agent enrolls itself and its owner approves it with a fresh verification', async ({ browser, request }, info) => {
  const account = await createProjectOwner(browser, info);
  const { page, current } = account;
  const run = randomBytes(4).toString('hex');
  const ownerToken = await signInToken(page.request, 'agent-enroll-owner');
  const owned = { Authorization: 'Bearer ' + ownerToken };
  const projects = `${auth}/v1/terrascale/organizations/${current.organizationId}/projects`;
  const projectName = 'Agent enrollment ' + run;
  const createdProject = await page.request.post(projects, { headers: owned, data: { name: projectName } });
  expect(createdProject.status()).toBe(201);
  const projectId = (await createdProject.json()).id as string;
  const root = auth + '/v1/' + projectId;
  const browserContext = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  const otherContext = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  try {
    const keys: string[] = [];
    for (const level of ['read', 'write']) {
      const created = await page.request.post(`${projects}/${projectId}/api-keys`, { headers: owned, data: { name: `enroll ${level}`, scopes: [`auth:${level}`] } });
      expect(created.status()).toBe(201);
      const key = (await created.json()).key as string;
      saveSecret(`agent-enroll-${level}`, key);
      keys.push(key);
    }
    const read = { Authorization: 'Bearer ' + keys[0] };
    const write = { Authorization: 'Bearer ' + keys[1] };
    expect((await page.request.put(root + '/agent-policy', { headers: write, data: { allowed_scopes: ['invoices:read', 'invoices:write'] } })).status()).toBe(200);
    const email = `agent-enroller-${run}@example.com`;
    const password = randomBytes(18).toString('base64url');
    saveSecret('agent-enroller-password', password);
    const owner = await page.request.post(root + '/users', { headers: write, data: { email, password } });
    expect(owner.status()).toBe(201);
    const user = (await owner.json()).id as string;

    const refused = await request.post(root + '/agent-enrollments', {
      data: { name: 'Invoice agent', requested_scopes: ['invoices:read'], public_jwk: { ...agentKey().jwk, d: 'AAAA' } },
    });
    expect((await refused.json()).error.code).toBe('private_key_material');
    const agent = await Agent.start(request, root, { name: 'Invoice agent', requested_scopes: ['invoices:read', 'reports:read'], allow_self_subject: true });
    expect(agent.enrollment).toMatchObject({ verification_uri: `${auth}/ui/${projectId}/agent-enrollments`, expires_in: 600, interval: 5 });
    expect(agent.enrollment.user_code).toMatch(/^[BCDFGHJKLMNPQRSTVWXZ]{4}-[BCDFGHJKLMNPQRSTVWXZ]{4}$/);
    expect(agent.enrollment.verification_uri_complete).toBe(`${agent.enrollment.verification_uri}?user_code=${agent.enrollment.user_code}`);

    const watcher = await Agent.start(request, root, { name: 'Watcher agent', requested_scopes: ['invoices:read'] });
    const forged = await watcher.poll(proof(agentKey().privateKey, watcher.url));
    expect(forged).toEqual({ status: 401, body: { error: expect.objectContaining({ code: 'invalid_proof' }) } });
    expect((await watcher.poll(proof(watcher.key.privateKey, root + '/agent-enrollments/other/token'))).status).toBe(401);
    expect((await watcher.poll(proof(watcher.key.privateKey, watcher.url + '/'))).status).toBe(401);
    const pending = await watcher.poll();
    expect(pending).toEqual({ status: 400, body: { error: expect.objectContaining({ code: 'authorization_pending' }) } });
    const early = await watcher.poll(proof(watcher.key.privateKey, watcher.url, Math.floor(Date.now() / 1000) + 1));
    expect(early.body.error?.code).toBe('slow_down');

    const browserPage = await browserContext.newPage();
    const audit = new Audit(browserPage, info);
    await browserPage.goto(agent.enrollment.verification_uri_complete);
    await browserPage.waitForURL((url) => url.pathname === `/ui/${projectId}/sign-in`);
    await hostedReady(browserPage);
    await browserPage.locator('#sign-in-email').fill(email);
    await browserPage.getByRole('button', { name: 'Use a password instead' }).click();
    await browserPage.waitForURL((url) => url.pathname === `/ui/${projectId}/sign-in/password`);
    await hostedReady(browserPage);
    await enterSecret(browserPage.locator('#sign-in-password'), password);
    await browserPage.getByRole('button', { name: 'Sign in', exact: true }).click();
    await browserPage.waitForURL(agent.enrollment.verification_uri_complete);

    await openRequest(browserPage, audit, agent, 'agent enrollment request');
    await expect(browserPage.getByRole('heading', { level: 1 })).toHaveText('Approve an AI agent');
    await expect(browserPage.locator('.sub')).toHaveText(`Invoice agent wants to act for you in ${projectName}.`);
    await expect(browserPage.locator('.scopes li')).toHaveText(['invoices:read']);
    await expect(browserPage.getByText('It also asks to act as itself, not only for you.')).toBeVisible();
    await expect(browserPage.locator('code')).toHaveText(agent.key.thumbprint.slice(0, 16));
    await expect(browserPage.locator('.chip')).toHaveText(email);

    const otherEmail = `agent-other-${run}@example.com`;
    const otherUser = await page.request.post(root + '/users', { headers: write, data: { email: otherEmail, password } });
    expect(otherUser.status()).toBe(201);
    const otherUserId = (await otherUser.json()).id as string;
    const otherCreated = await page.request.post(`${root}/users/${otherUserId}/agents`, {
      headers: write, data: { name: 'Other agent', allowed_scopes: ['invoices:read'], public_jwk: agentKey().jwk },
    });
    expect(otherCreated.status()).toBe(201);
    const otherAgent = (await otherCreated.json()).id as string;
    expect((await otherContext.request.post(`${auth}/ui/${projectId}/sign-in/password`, {
      headers: { Origin: auth }, form: { email: otherEmail, password },
    })).status()).toBe(200);
    const crossSite = await browserContext.request.post(`${auth}/ui/${projectId}/agent-enrollments`, {
      headers: { Origin: dash }, form: { user_code: agent.enrollment.user_code, decision: 'approve' },
    });
    expect(crossSite.status()).toBe(403);
    const confirmation = await browserPage.locator('input[name=confirmation]').inputValue();
    const forgedConfirmation = await browserContext.request.post(`${auth}/ui/${projectId}/agent-enrollments`, {
      headers: { Origin: auth }, form: { user_code: agent.enrollment.user_code, decision: 'approve', confirmation: 'forged' }, maxRedirects: 0,
    });
    expect(forgedConfirmation.status()).toBe(409);
    expect(await forgedConfirmation.text()).toContain('The request changed. Check it again before you approve.');
    const otherAccount = await otherContext.request.post(`${auth}/ui/${projectId}/agent-enrollments`, {
      headers: { Origin: auth }, form: { user_code: agent.enrollment.user_code, decision: 'approve', confirmation }, maxRedirects: 0,
    });
    expect(otherAccount.status()).toBe(409);
    expect(await otherAccount.text()).toContain('The request changed. Check it again before you approve.');
    await browserPage.getByRole('button', { name: 'Approve', exact: true }).click();
    await browserPage.waitForURL((url) => url.pathname.startsWith(`/ui/${projectId}/step-up/`));
    await hostedReady(browserPage);
    await expect(browserPage.getByRole('heading', { level: 1 })).toHaveText('Approve an AI agent');
    await audit.check('agent approval verification');
    await enterSecret(browserPage.locator('input[type=password]'), password);
    await browserPage.getByRole('button', { name: 'Verify and continue' }).click();
    await browserPage.waitForURL(agent.enrollment.verification_uri_complete);
    await hostedReady(browserPage);
    await browserPage.getByRole('button', { name: 'Approve', exact: true }).click();
    await expect(browserPage.locator('.alert[data-state=success]')).toHaveText('Agent approved. You can close this page.');
    await audit.check('agent approved');

    const replayed = proof(agent.key.privateKey, agent.url);
    const approved = await agent.poll(replayed);
    expect(approved.status).toBe(200);
    const created = await (await page.request.get(`${root}/agents/${approved.body.agent_id}`, { headers: read })).json();
    expect(created).toMatchObject({
      owner_user_id: user, name: 'Invoice agent', status: 'active', allowed_scopes: ['invoices:read'], allow_self_subject: true,
      keys: [{ kid: approved.body.kid, thumbprint: agent.key.thumbprint, revoked_at: null }],
    });
    expect((await agent.poll(replayed)).status).toBe(401);
    expect((await agent.poll(proof(agent.key.privateKey, agent.url, Math.floor(Date.now() / 1000) + 1))).body.error?.code).toBe('expired_token');

    audit.expectStatus(new RegExp(`/agent-enrollments\\?user_code=${agent.enrollment.user_code}$`), 400);
    await browserPage.goto(agent.enrollment.verification_uri_complete);
    await expect(browserPage.locator('#agent-enrollment-error')).toHaveText('That code is wrong or expired. Start again from the agent.');

    const denied = await Agent.start(request, root, { name: 'Report agent', requested_scopes: ['reports:read'] });
    await openRequest(browserPage, audit, denied, 'second agent enrollment request');
    await expect(browserPage.getByText('It also asks to act as itself, not only for you.')).toHaveCount(0);
    await expect(browserPage.getByText('This project allows none of what the agent asks for.')).toBeVisible();
    await expect(browserPage.getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0);
    await browserPage.getByRole('button', { name: 'Deny', exact: true }).click();
    await expect(browserPage.locator('.alert[data-state=success]')).toHaveText('Request denied.');
    expect((await denied.poll()).body.error?.code).toBe('access_denied');

    await onLocalStack(async () => {
      const stale = await Agent.start(request, root, { name: 'Stale agent', requested_scopes: ['invoices:read'] });
      expect((await stale.poll()).body.error?.code).toBe('authorization_pending');
      const aged = Date.now() - 20 * 60_000;
      await authStorage('UPDATE agent_enrollments SET created_at = ?, expires_at = ? WHERE project_id = ? AND id = ?',
        [new Date(aged).toISOString(), new Date(aged + 600_000).toISOString(), projectId, stale.enrollment.enrollment_id]);
      audit.expectStatus(new RegExp(`/agent-enrollments\\?user_code=${stale.enrollment.user_code}$`), 400);
      await browserPage.goto(stale.enrollment.verification_uri_complete);
      await expect(browserPage.locator('#agent-enrollment-error')).toHaveText('That code is wrong or expired. Start again from the agent.');
      const lateDecision = await browserContext.request.post(`${auth}/ui/${projectId}/agent-enrollments`, {
        headers: { Origin: auth }, form: { user_code: stale.enrollment.user_code, decision: 'deny' },
      });
      expect(lateDecision.status()).toBe(400);
      expect(await lateDecision.text()).toContain('That code is wrong or expired.');
      expect((await stale.poll(proof(stale.key.privateKey, stale.url, Math.floor(Date.now() / 1000) + 1))).body.error?.code).toBe('expired_token');
    });

    expect((await request.get(root + '/me/agents')).status()).toBe(401);
    const mine = await browserContext.request.get(root + '/me/agents');
    expect(mine.status()).toBe(200);
    expect((await mine.json()).items.map((item: { id: string }) => item.id)).toEqual([approved.body.agent_id]);
    expect((await browserContext.request.delete(`${root}/me/agents/${approved.body.agent_id}`, { headers: { Origin: dash } })).status()).toBe(403);
    expect((await browserContext.request.delete(`${root}/me/agents/${otherAgent}`, { headers: { Origin: auth } })).status()).toBe(404);
    expect((await (await page.request.get(`${root}/agents/${otherAgent}`, { headers: read })).json()).status).toBe('active');
    const otherMine = await otherContext.request.get(root + '/me/agents');
    expect((await otherMine.json()).items.map((item: { id: string }) => item.id)).toEqual([otherAgent]);
    expect((await browserContext.request.delete(`${root}/me/agents/${approved.body.agent_id}`, { headers: { Origin: auth } })).status()).toBe(204);
    const revoked = await (await page.request.get(`${root}/agents/${approved.body.agent_id}`, { headers: read })).json();
    expect(revoked.status).toBe('revoked');
    await sdkAgentEnrollment(browserPage, audit, projectId, keys[1], user, password);
    await cliAgentEnrollment(browserPage, audit, projectId, keys[1], user, password, keys[0]);

    await audit.finish();
  } finally {
    await browserContext.close();
    await otherContext.close();
    expect((await page.request.delete(`${projects}/${projectId}`, { headers: owned })).status()).toBe(204);
    await account.close();
  }
});
