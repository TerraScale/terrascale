import { createHash, generateKeyPairSync, randomUUID, sign, type KeyObject } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { APIRequestContext, Page } from '@playwright/test';
import { expect } from './audit';
import { hostedReady } from './hosted';
import { enterSecret } from './secret';
import { auth, stateDir } from './stack';

export const platformTokenEndpoint = auth + '/terrascale/oauth/token';

const dashboardClient = '01M4EQ2FRJ8S8B29E8TQVWNW4J';

export function dashboardSecret(): string {
  return readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[5];
}

export async function exchangeAgentToken(request: APIRequestContext, subject: string, secret = dashboardSecret()): Promise<{ status: number; body: Record<string, unknown> }> {
  const response = await request.post(platformTokenEndpoint, {
    headers: { Authorization: 'Basic ' + Buffer.from(`${dashboardClient}:${secret}`).toString('base64') },
    form: { grant_type: 'urn:terrascale:params:oauth:grant-type:agent-exchange', subject_token: subject, subject_token_type: 'urn:ietf:params:oauth:token-type:access_token' },
  });
  return { status: response.status(), body: await response.json() };
}

export async function exchangeForProject(request: APIRequestContext, subject: string, organization: string, project: string, scope?: string): Promise<{ status: number; body: Record<string, unknown> }> {
  const response = await request.post(platformTokenEndpoint, {
    headers: { Authorization: 'Basic ' + Buffer.from(`${dashboardClient}:${dashboardSecret()}`).toString('base64') },
    form: {
      grant_type: 'urn:ietf:params:oauth:grant-type:token-exchange', subject_token: subject, subject_token_type: 'urn:ietf:params:oauth:token-type:access_token',
      organization, project, ...(scope ? { scope } : {}),
    },
  });
  return { status: response.status(), body: await response.json() };
}

export type Jwk = { kty: string; crv: string; x: string; y: string };
export type Enrollment = { enrollment_id: string; user_code: string; verification_uri: string; verification_uri_complete: string; expires_in: number; interval: number };

export function agentKey(): { jwk: Jwk; privateKey: KeyObject; thumbprint: string } {
  const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const jwk = publicKey.export({ format: 'jwk' }) as Jwk;
  const canonical = JSON.stringify({ crv: jwk.crv, kty: jwk.kty, x: jwk.x, y: jwk.y });
  return { jwk, privateKey, thumbprint: createHash('sha256').update(canonical).digest('base64url') };
}

function signed(privateKey: KeyObject, header: object, payload: object): string {
  const part = (value: object) => Buffer.from(JSON.stringify(value)).toString('base64url');
  const input = `${part(header)}.${part(payload)}`;
  return `${input}.${sign('sha256', Buffer.from(input), { key: privateKey, dsaEncoding: 'ieee-p1363' }).toString('base64url')}`;
}

export function proof(privateKey: KeyObject, audience: string, issuedAt = Math.floor(Date.now() / 1000)): string {
  return signed(privateKey, { alg: 'ES256', typ: 'JWT' }, { aud: audience, jti: randomUUID(), iat: issuedAt, exp: issuedAt + 120 });
}

export function assertion(privateKey: KeyObject, kid: string, agent: string, audience: string): string {
  const now = Math.floor(Date.now() / 1000);
  return signed(privateKey, { alg: 'ES256', typ: 'JWT', kid }, { iss: agent, sub: agent, aud: audience, iat: now, exp: now + 60, jti: randomUUID() });
}

export function signInProof(privateKey: KeyObject, kid: string, agent: string, proofUrl: string, claimed: { txn: string; client_id: string }): string {
  const now = Math.floor(Date.now() / 1000);
  return signed(privateKey, { alg: 'ES256', typ: 'JWT', kid }, { iss: agent, sub: agent, aud: proofUrl, iat: now, exp: now + 60, jti: randomUUID(), ...claimed });
}

export async function approvePlatformAgent(page: Page, agent: Agent, password: string): Promise<{ agentId: string; kid: string }> {
  await page.goto(agent.enrollment.verification_uri_complete);
  await hostedReady(page);
  await page.getByRole('button', { name: 'Approve', exact: true }).click();
  await page.waitForURL((url) => url.pathname.startsWith('/ui/terrascale/step-up/'));
  await hostedReady(page);
  await enterSecret(page.locator('input[type=password]'), password);
  await page.getByRole('button', { name: 'Verify and continue' }).click();
  await page.waitForURL(agent.enrollment.verification_uri_complete);
  await hostedReady(page);
  await page.getByRole('button', { name: 'Approve', exact: true }).click();
  await expect(page.locator('.alert[data-state=success]')).toHaveText('Agent approved. You can close this page.');
  const approved = await agent.poll();
  expect(approved.status).toBe(200);
  return { agentId: approved.body.agent_id!, kid: approved.body.kid! };
}

export function claims(token: string): Record<string, unknown> {
  return JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()) as Record<string, unknown>;
}

export class Agent {
  constructor(private readonly request: APIRequestContext, private readonly root: string, readonly key: ReturnType<typeof agentKey>, readonly enrollment: Enrollment) {}

  get url(): string {
    return `${this.root}/agent-enrollments/${this.enrollment.enrollment_id}/token`;
  }

  async poll(signedProof = proof(this.key.privateKey, this.url)): Promise<{ status: number; body: { error?: { code: string }; agent_id?: string; kid?: string } }> {
    const response = await this.request.post(this.url, { data: { proof: signedProof } });
    return { status: response.status(), body: await response.json() };
  }

  static async start(request: APIRequestContext, root: string, data: { name: string; requested_scopes: string[]; allow_self_subject?: boolean }): Promise<Agent> {
    const key = agentKey();
    const started = await request.post(root + '/agent-enrollments', { data: { ...data, public_jwk: key.jwk } });
    expect(started.status()).toBe(201);
    return new Agent(request, root, key, await started.json() as Enrollment);
  }
}
