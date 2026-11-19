import { createHash, generateKeyPairSync, randomBytes, randomUUID, sign, type KeyObject } from 'node:crypto';
import type { APIRequestContext, Page } from '@playwright/test';
import { Audit, auditedContext, expect, test } from '../support/audit';
import { keyId } from '../support/api-key';
import { signInToken } from '../support/auth-management';
import { hostedReady } from '../support/hosted';
import { createProjectOwner } from '../support/project-owner';
import { enterSecret } from '../support/secret';
import { auth, dash, desktop } from '../support/stack';
import { saveSecret } from '../support/state';

type Jwk = { kty: string; crv: string; x: string; y: string };
type Enrollment = { enrollment_id: string; user_code: string; verification_uri: string; verification_uri_complete: string; expires_in: number; interval: number };

function agentKey(): { jwk: Jwk; privateKey: KeyObject; thumbprint: string } {
  const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const jwk = publicKey.export({ format: 'jwk' }) as Jwk;
  const canonical = JSON.stringify({ crv: jwk.crv, kty: jwk.kty, x: jwk.x, y: jwk.y });
  return { jwk, privateKey, thumbprint: createHash('sha256').update(canonical).digest('base64url') };
}

function proof(privateKey: KeyObject, audience: string, issuedAt = Math.floor(Date.now() / 1000)): string {
  const part = (value: object) => Buffer.from(JSON.stringify(value)).toString('base64url');
  const input = `${part({ alg: 'ES256', typ: 'JWT' })}.${part({ aud: audience, jti: randomUUID(), iat: issuedAt, exp: issuedAt + 120 })}`;
  return `${input}.${sign('sha256', Buffer.from(input), { key: privateKey, dsaEncoding: 'ieee-p1363' }).toString('base64url')}`;
}

class Agent {
  constructor(private readonly request: APIRequestContext, private readonly root: string, readonly key: ReturnType<typeof agentKey>, readonly enrollment: Enrollment) {}

  get url(): string {
    return `${this.root}/agent-enrollments/${this.enrollment.enrollment_id}/token`;
  }

  async poll(signed = proof(this.key.privateKey, this.url)): Promise<{ status: number; body: { error?: { code: string }; agent_id?: string; kid?: string } }> {
    const response = await this.request.post(this.url, { data: { proof: signed } });
    return { status: response.status(), body: await response.json() };
  }

  static async start(request: APIRequestContext, root: string, data: { name: string; requested_scopes: string[]; allow_self_subject?: boolean }): Promise<Agent> {
    const key = agentKey();
    const started = await request.post(root + '/agent-enrollments', { data: { ...data, public_jwk: key.jwk } });
    expect(started.status()).toBe(201);
    return new Agent(request, root, key, await started.json() as Enrollment);
  }
}

async function openRequest(page: Page, audit: Audit, agent: Agent, label: string): Promise<void> {
  await page.goto(agent.enrollment.verification_uri_complete);
  await hostedReady(page);
  await expect(page.locator('.device-code')).toHaveText(agent.enrollment.user_code);
  await audit.check(label);
}

test('an agent enrolls itself and its owner approves it with a fresh verification', async ({ browser, request }, info) => {
  const account = await createProjectOwner(browser, info);
  const { page, current } = account;
  const root = auth + '/v1/' + current.projectId;
  const run = randomBytes(4).toString('hex');
  await page.goto('/api-keys?create=key');
  const csrf = await page.locator('#create-key input[name="_csrf"]').inputValue();
  const keys: string[] = [];
  const browserContext = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  const otherContext = await auditedContext(browser, { viewport: desktop, storageState: { cookies: [], origins: [] } });
  let otherUserId = '';
  try {
    for (const level of ['read', 'write']) {
      const created = await page.request.post('/api/v1/dashboard/credentials/key', {
        headers: { Accept: 'application/json' }, form: { _csrf: csrf, name: `enroll-${run}-${level}`, scopes: `auth:${level}` },
      });
      expect(created.status()).toBe(201);
      const key = (await created.json()).secret as string;
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
    const ownerToken = await signInToken(page.request, 'agent-enroll-owner');
    const project = await page.request.get(`${auth}/v1/terrascale/organizations/${current.organizationId}/projects/${current.projectId}`, {
      headers: { Authorization: 'Bearer ' + ownerToken },
    });
    expect(project.status()).toBe(200);
    const projectName = (await project.json()).name as string;

    const refused = await request.post(root + '/agent-enrollments', {
      data: { name: 'Invoice agent', requested_scopes: ['invoices:read'], public_jwk: { ...agentKey().jwk, d: 'AAAA' } },
    });
    expect((await refused.json()).error.code).toBe('private_key_material');
    const agent = await Agent.start(request, root, { name: 'Invoice agent', requested_scopes: ['invoices:read', 'reports:read'], allow_self_subject: true });
    expect(agent.enrollment).toMatchObject({ verification_uri: `${auth}/ui/${current.projectId}/agent-enrollments`, expires_in: 600, interval: 5 });
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
    await browserPage.waitForURL((url) => url.pathname === `/ui/${current.projectId}/sign-in`);
    await hostedReady(browserPage);
    await browserPage.locator('#sign-in-email').fill(email);
    await browserPage.getByRole('button', { name: 'Use a password instead' }).click();
    await browserPage.waitForURL((url) => url.pathname === `/ui/${current.projectId}/sign-in/password`);
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

    const crossSite = await browserContext.request.post(`${auth}/ui/${current.projectId}/agent-enrollments`, {
      headers: { Origin: dash }, form: { user_code: agent.enrollment.user_code, decision: 'approve' },
    });
    expect(crossSite.status()).toBe(403);
    const unseen = await browserContext.request.post(`${auth}/ui/${current.projectId}/agent-enrollments`, {
      headers: { Origin: auth }, form: { user_code: agent.enrollment.user_code, decision: 'approve', scopes: 'invoices:write' }, maxRedirects: 0,
    });
    expect(unseen.status()).toBe(409);
    expect(await unseen.text()).toContain('The request changed. Check it again before you approve.');
    await browserPage.getByRole('button', { name: 'Approve', exact: true }).click();
    await browserPage.waitForURL((url) => url.pathname.startsWith(`/ui/${current.projectId}/step-up/`));
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

    expect((await request.get(root + '/me/agents')).status()).toBe(401);
    const otherEmail = `agent-other-${run}@example.com`;
    const otherUser = await page.request.post(root + '/users', { headers: write, data: { email: otherEmail, password } });
    expect(otherUser.status()).toBe(201);
    otherUserId = (await otherUser.json()).id as string;
    const otherCreated = await page.request.post(`${root}/users/${otherUserId}/agents`, {
      headers: write, data: { name: 'Other agent', allowed_scopes: ['invoices:read'], public_jwk: agentKey().jwk },
    });
    expect(otherCreated.status()).toBe(201);
    const otherAgent = (await otherCreated.json()).id as string;
    expect((await otherContext.request.post(`${auth}/ui/${current.projectId}/sign-in/password`, {
      headers: { Origin: auth }, form: { email: otherEmail, password },
    })).status()).toBe(200);
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

    await audit.finish();
    for (const id of [user, otherUserId]) expect((await page.request.delete(`${root}/users/${id}`, { headers: write })).status()).toBe(204);
  } finally {
    await browserContext.close();
    await otherContext.close();
    for (const key of keys) {
      const revoked = await page.request.post('/api/v1/dashboard/credentials/revoke', {
        headers: { Accept: 'application/json' }, form: { _csrf: csrf, keyId: keyId(key) },
      });
      expect(revoked.status()).toBe(204);
    }
    await account.close();
  }
});
