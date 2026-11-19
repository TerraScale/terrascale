import type { Page } from '@playwright/test';
import { createTsAuthClient, verifyAgentToken } from '../../sdk/js/src/ts-auth.js';
import { expect, type Audit } from '../support/audit';
import { hostedReady } from '../support/hosted';
import { enterSecret } from '../support/secret';
import { saveSecret } from '../support/state';
import { auth } from '../support/stack';

export async function sdkAgentEnrollment(page: Page, audit: Audit, project: string, credential: string, owner: string, password: string): Promise<void> {
  const transport: typeof fetch = async (input, init) => {
    const request = new Request(input, init);
    const response = await page.request.fetch(request.url, {
      method: request.method, headers: Object.fromEntries(request.headers),
      ...(request.body === null ? {} : { data: await request.text() }), maxRedirects: 0,
    });
    return new Response(response.status() === 204 ? null : Uint8Array.from(await response.body()), { status: response.status(), headers: response.headers() });
  };
  const client = createTsAuthClient({ apiBaseUrl: auth, instanceSlug: project, fetch: transport });
  const started = await client.agents.enroll({ project, name: 'SDK agent', scopes: ['invoices:read'], allowSelfSubject: true });
  expect(started.ok).toBe(true);
  if (!started.ok) throw new Error('SDK enrollment failed');
  const waiting = started.value.wait();
  await approveAgentEnrollment(page, audit, started.value, password);
  const enrolled = await waiting;
  expect(enrolled.ok).toBe(true);
  if (!enrolled.ok) throw new Error('SDK enrollment completion failed');
  expect(enrolled.value.key.extractable).toBe(false);
  for (const subject of ['owner', 'agent'] as const) {
    const token = await client.agents.getToken({ ...enrolled.value, project, subject });
    expect(token.ok).toBe(true);
    if (!token.ok) throw new Error('SDK agent token failed');
    saveSecret('sdk-agent-' + subject, token.value.access_token);
    const verified = await verifyAgentToken(token.value.access_token, { project, apiBaseUrl: auth, fetch: transport });
    expect(verified.ok && verified.value).toEqual({ isAgent: true, agentId: enrolled.value.agentId, ownerId: owner, actingFor: subject === 'owner' ? 'owner' : 'self', scopes: ['invoices:read'] });
    const parts = token.value.access_token.split('.');
    parts[2] = (parts[2][0] === 'A' ? 'B' : 'A') + parts[2].slice(1);
    const tampered = await verifyAgentToken(parts.join('.'), { project, fetch: transport });
    expect(tampered.ok).toBe(false);
    const wrongProject = await verifyAgentToken(token.value.access_token, { project: '00000000000000000000000001', fetch: transport });
    expect(wrongProject.ok).toBe(false);
    const wrongAudience = await verifyAgentToken(token.value.access_token, { project, audience: 'customer-api', fetch: transport });
    expect(wrongAudience.ok).toBe(false);
    const cached = await client.agents.getToken({ ...enrolled.value, project, subject });
    expect(cached.ok && cached.value.access_token === token.value.access_token).toBe(true);
  }
  const revoked = await client.projects.revokeAgent(credential, project, enrolled.value.agentId);
  expect(revoked.ok).toBe(true);
  const fresh = createTsAuthClient({ apiBaseUrl: auth, instanceSlug: project, fetch: transport });
  const refused = await fresh.agents.getToken({ ...enrolled.value, project });
  expect(!refused.ok && refused.error.code).toBe('invalid_grant');
}

export async function approveAgentEnrollment(page: Page, audit: Audit, enrollment: { verificationUriComplete: string, userCode: string, fingerprint: string }, password: string): Promise<void> {
  await page.goto(enrollment.verificationUriComplete);
  await hostedReady(page);
  await expect(page.locator('.device-code')).toHaveText(enrollment.userCode);
  await expect(page.locator('code')).toHaveText(enrollment.fingerprint);
  await audit.check('SDK agent enrollment');
  await page.getByRole('button', { name: 'Approve', exact: true }).click();
  await page.waitForURL(url => url.pathname.includes('/step-up/'));
  await hostedReady(page);
  await enterSecret(page.locator('input[type=password]'), password);
  await page.getByRole('button', { name: 'Verify and continue' }).click();
  await page.waitForURL(enrollment.verificationUriComplete);
  await hostedReady(page);
  await page.getByRole('button', { name: 'Approve', exact: true }).click();
  await expect(page.locator('.alert[data-state=success]')).toHaveText('Agent approved. You can close this page.');
}
