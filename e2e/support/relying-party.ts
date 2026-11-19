import { createHash, createPublicKey, randomBytes, verify, type webcrypto } from 'node:crypto';
import type { APIRequestContext } from '@playwright/test';
import { expect } from './audit';

export type SignInStart = { url: string; state: string; nonce: string; verifier: string };

export type ClientAuthentication = 'none' | 'basic' | 'post';

export class RelyingParty {
  constructor(private readonly request: APIRequestContext, readonly issuer: string, readonly clientId: string,
    readonly redirectUri = clientId + '/auth/callback', private readonly secret?: string) {}

  async discovery(): Promise<Record<string, unknown>> {
    const response = await this.request.get(this.issuer + '/.well-known/openid-configuration');
    expect(response.status()).toBe(200);
    return await response.json();
  }

  async start(scope = 'openid profile'): Promise<SignInStart> {
    const endpoint = (await this.discovery()).authorization_endpoint as string;
    const [state, nonce, verifier] = [randomBytes(16), randomBytes(16), randomBytes(32)].map((value) => value.toString('base64url'));
    const url = new URL(endpoint);
    url.search = new URLSearchParams({
      response_type: 'code', client_id: this.clientId, redirect_uri: this.redirectUri, scope, state, nonce,
      code_challenge: createHash('sha256').update(verifier).digest('base64url'), code_challenge_method: 'S256',
    }).toString();
    return { url: url.href, state, nonce, verifier };
  }

  callback(location: string, started: SignInStart): string {
    const url = new URL(location);
    expect(url.origin + url.pathname).toBe(this.redirectUri);
    expect(url.searchParams.get('state')).toBe(started.state);
    expect(url.searchParams.get('iss')).toBe(this.issuer);
    return url.searchParams.get('code')!;
  }

  async redeem(code: string, started: SignInStart, secret = this.secret, method: ClientAuthentication = secret === undefined ? 'none' : 'basic'): Promise<{ status: number; body: Record<string, unknown> }> {
    const token = (await this.discovery()).token_endpoint as string;
    const form: Record<string, string> = { grant_type: 'authorization_code', code, redirect_uri: this.redirectUri, code_verifier: started.verifier };
    const headers: Record<string, string> = {};
    if (method === 'basic') headers.Authorization = 'Basic ' + Buffer.from(`${encodeURIComponent(this.clientId)}:${encodeURIComponent(secret!)}`).toString('base64');
    else form.client_id = this.clientId;
    if (method === 'post') form.client_secret = secret!;
    const response = await this.request.post(token, { form, headers });
    return { status: response.status(), body: await response.json() };
  }

  async verify(idToken: string, started: SignInStart): Promise<Record<string, unknown>> {
    const [header, payload, signature] = idToken.split('.');
    const head = JSON.parse(Buffer.from(header, 'base64url').toString()) as { alg: string; kid: string };
    expect(head.alg).toBe('ES256');
    const jwks = await (await this.request.get((await this.discovery()).jwks_uri as string)).json() as { keys: (webcrypto.JsonWebKey & { kid: string })[] };
    const jwk = jwks.keys.find((key) => key.kid === head.kid);
    expect(jwk).toBeDefined();
    const valid = verify('sha256', Buffer.from(`${header}.${payload}`), { key: createPublicKey({ key: jwk!, format: 'jwk' }), dsaEncoding: 'ieee-p1363' }, Buffer.from(signature, 'base64url'));
    expect(valid).toBe(true);
    const claims = JSON.parse(Buffer.from(payload, 'base64url').toString()) as Record<string, unknown>;
    const now = Math.floor(Date.now() / 1000);
    expect(claims).toMatchObject({ iss: this.issuer, aud: this.clientId, nonce: started.nonce });
    expect(claims.exp as number).toBeGreaterThan(now);
    expect((claims.exp as number) - (claims.iat as number)).toBeLessThanOrEqual(600);
    return claims;
  }
}

const acceptJson = { Accept: 'application/json' };

export type Pending = { status: string; transaction_id: string; proof_url: string; client_id: string; client_name: string; expires_at: string };

export async function pending(request: APIRequestContext, started: SignInStart): Promise<Pending> {
  const response = await request.get(started.url, { headers: acceptJson, maxRedirects: 0 });
  expect(response.status()).toBe(200);
  const body = await response.json() as Pending;
  expect(response.headers().link).toBe(`<${body.proof_url}>; rel="agent-proof"`);
  return body;
}

export async function authorizeStatus(request: APIRequestContext, started: SignInStart, overrides: Record<string, string>): Promise<{ status: number; location?: string }> {
  const url = new URL(started.url);
  for (const [name, value] of Object.entries(overrides)) url.searchParams.set(name, value);
  const response = await request.get(url.href, { headers: acceptJson, maxRedirects: 0 });
  return { status: response.status(), location: response.headers().location };
}

export async function prove(request: APIRequestContext, url: string, proof: string): Promise<number> {
  return (await request.post(url, { data: { proof } })).status();
}

export async function completed(request: APIRequestContext, rp: RelyingParty, started: SignInStart): Promise<string> {
  const response = await request.get(started.url, { headers: acceptJson, maxRedirects: 0 });
  expect(response.status()).toBe(302);
  return rp.callback(response.headers().location, started);
}
