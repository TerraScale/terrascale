import { KeyObject } from 'node:crypto';
import type { Page } from '@playwright/test';
import { createTsAuthClient, verifyAgentToken } from '../../sdk/js/src/ts-auth.js';
import { expect, type Audit } from '../support/audit';
import { hostedReady } from '../support/hosted';
import { enterSecret } from '../support/secret';
import { saveSecret } from '../support/state';
import { auth, onLocalStack } from '../support/stack';
import { authStorage } from '../support/auth-management';

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
  expect(() => KeyObject.from(enrolled.value.key as unknown as Parameters<typeof KeyObject.from>[0])).toThrow();
  await expect(crypto.subtle.exportKey('jwk', enrolled.value.key as unknown as CryptoKey)).rejects.toThrow();
  expect(JSON.stringify(enrolled.value.key)).toBe('{}');
  const replacement = await client.agents.createKey();
  expect(replacement.publicJwk.kty).toBe('EC');
  expect(replacement.publicJwk.crv).toBe('P-256');
  expect(replacement.publicJwk.d).toBeUndefined();
  expect(() => KeyObject.from(replacement.signer as unknown as Parameters<typeof KeyObject.from>[0])).toThrow();
  await expect(crypto.subtle.exportKey('jwk', replacement.signer as unknown as CryptoKey)).rejects.toThrow();
  expect(JSON.stringify(replacement.signer)).toBe('{}');
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
    const originalToken = token.value.access_token;
    const originalScope = token.value.scope;
    token.value.access_token = 'changed';
    token.value.expires_in = 0;
    token.value.scope = 'different:scope';
    const cached = await client.agents.getToken({ ...enrolled.value, project, subject });
    expect(cached.ok && cached.value.access_token === originalToken && cached.value.scope === originalScope && cached.value.expires_in > 0).toBe(true);
  }
  const controller = new AbortController();
  const abandoned = await client.agents.enroll({ project, name: 'Canceled SDK agent', scopes: ['invoices:read'], signal: controller.signal });
  expect(abandoned.ok).toBe(true);
  if (!abandoned.ok) throw new Error('Could not start abort journey');
  const canceled = abandoned.value.wait();
  controller.abort();
  const aborted = await canceled;
  expect(!aborted.ok && aborted.error.code).toBe('request_aborted');
  await onLocalStack(async () => {
    await authStorage('UPDATE agents SET expires_at = ? WHERE project_id = ? AND id = ?',
      [new Date(Date.now() + 8000).toISOString(), project, enrolled.value.agentId]);
    const freshToken = await createTsAuthClient({ apiBaseUrl: auth, instanceSlug: project, fetch: transport }).agents.getToken({ ...enrolled.value, project });
    expect(freshToken.ok).toBe(true);
    if (!freshToken.ok) throw new Error('Could not mint expiry journey token');
    saveSecret('sdk-agent-expiring', freshToken.value.access_token);
    const exp = Number(JSON.parse(Buffer.from(freshToken.value.access_token.split('.')[1], 'base64url').toString('utf8')).exp);
    expect(exp * 1000).toBeGreaterThan(Date.now());
    let fetched = false;
    const expired = await verifyAgentToken(freshToken.value.access_token, { project, fetch: async (input, init) => {
      const response = await transport(input, init);
      fetched = true;
      await expect.poll(() => Date.now()).toBeGreaterThanOrEqual(exp * 1000);
      return response;
    } });
    expect(fetched).toBe(true);
    expect(!expired.ok && expired.error.code).toBe('invalid_agent_token');
  });
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
