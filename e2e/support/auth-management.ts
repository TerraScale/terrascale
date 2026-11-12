import { createHash, createPublicKey, randomBytes, verify as verifySignature } from 'node:crypto';
import { once } from 'node:events';
import { connect } from 'node:http2';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { APIRequestContext, Page } from '@playwright/test';
import { expect, request } from '@playwright/test';
import { newAddress, nextMail } from './inbox';
import { auth, dash, onLocalStack, stateDir } from './stack';
import { journey, saveCookies, saveSecret } from './state';

export type AuthManagement = { user: string; readKey: string; writeKey: string; csrf: string; ownerToken: string };

async function signInToken(context: APIRequestContext, label: string): Promise<string> {
  const client = '00000000000000000000000007';
  const started = await context.post(auth + '/terrascale/oauth/device', { form: { client_id: client, scope: 'openid email' } });
  expect(started.status()).toBe(200);
  const device = await started.json();
  saveSecret(label + '-device', device.device_code);
  const approved = await context.post(auth + '/ui/terrascale/device', {
    headers: { Origin: auth }, form: { user_code: device.user_code, decision: 'approve' },
  });
  expect(approved.status()).toBe(200);
  const exchanged = await context.post(auth + '/terrascale/oauth/token', {
    form: { client_id: client, grant_type: 'urn:ietf:params:oauth:grant-type:device_code', device_code: device.device_code },
  });
  expect(exchanged.status()).toBe(200);
  const token = (await exchanged.json()).access_token as string;
  saveSecret(label + '-token', token);
  return token;
}

async function stepUp(context: APIRequestContext, token: string, purpose: string): Promise<string> {
  const started = await context.post(auth + '/v1/terrascale/step-up', {
    headers: { Authorization: 'Bearer ' + token },
    data: { purpose, audience: 'ts-auth', return_uri: dash + '/auth/step-up/callback' },
  });
  expect(started.status()).toBe(201);
  const proof = await started.json();
  saveSecret('member-' + purpose + '-proof', proof.proof);
  const approved = await context.post(proof.step_up_url, {
    headers: { Origin: auth }, form: { password: journey().password },
  });
  expect(approved.status()).toBe(200);
  return proof.proof;
}

async function checkMemberAuthority(page: Page, adminKey: string, ownerToken: string): Promise<void> {
  const current = journey();
  const email = await newAddress('auth-member-' + current.runId);
  const member = await request.newContext({ storageState: { cookies: [], origins: [] } });
  try {
    const started = await member.post(auth + '/ui/terrascale/sign-up', {
      headers: { Origin: auth }, form: { email, password: current.password },
    });
    expect(started.status()).toBe(200);
    const pending = /name="pending_token" value="([^"]+)"/.exec(await started.text())?.[1];
    expect(pending).toBeTruthy();
    saveSecret('auth-member-pending', pending!);
    const mail = await nextMail(email, 0);
    const code = /^\s*(\d{6})\s*$/m.exec(mail.text)?.[1];
    expect(code).toBeTruthy();
    const verified = await member.post(auth + '/ui/terrascale/sign-up/verify', {
      headers: { Origin: auth }, form: { email, code: code!, pending_token: pending! },
    });
    expect(verified.status()).toBe(200);
    const memberToken = await signInToken(member, 'auth-member');
    const ownerHeaders = { Authorization: 'Bearer ' + ownerToken };
    const memberHeaders = { Authorization: 'Bearer ' + memberToken };
    const organization = auth + '/v1/terrascale/organizations/' + current.organizationId;
    const platformProject = organization + '/projects/' + current.projectId;
    const invited = await page.request.post(organization + '/invitations', {
      headers: ownerHeaders, data: { email, role: 'member', project_id: current.projectId },
    });
    expect(invited.status()).toBe(201);
    const invitation = (await invited.json()).token as string;
    saveSecret('auth-member-invitation', invitation);
    const accepted = await member.post(auth + '/v1/terrascale/invitations/accept', {
      headers: memberHeaders, data: { token: invitation },
    });
    expect(accepted.status()).toBe(200);
    const proof = await stepUp(member, memberToken, 'rotate_api_key');
    const rotated = await member.post(platformProject + '/api-keys/' + adminKey.slice(0, 26) + '/rotate', {
      headers: { ...memberHeaders, 'X-TerraScale-Step-Up-Proof': proof }, data: { previous_secret_ttl: 0 },
    });
    expect(rotated.status()).toBe(403);
    expect((await rotated.json()).error.code).toBe('forbidden_role');
    const cleared = await member.put(platformProject + '/api-keys/' + adminKey.slice(0, 26) + '/allowed-cidrs', {
      headers: memberHeaders, data: { allowed_cidrs: [] },
    });
    expect(cleared.status()).toBe(403);
    const created = await member.post(platformProject + '/api-keys', {
      headers: memberHeaders, data: { name: 'member-auth-read', scopes: ['auth:read'] },
    });
    expect(created.status()).toBe(201);
    const key = (await created.json()).key as string;
    saveSecret('auth-member-read-key', key);
    const listed = await member.get(auth + '/v1/' + current.projectId + '/users', { headers: { Authorization: 'Bearer ' + key } });
    expect(listed.status()).toBe(200);
    for (const path of ['/users/export', '/webhook-endpoints']) {
      const refused = await member.get(auth + '/v1/' + current.projectId + path, { headers: { Authorization: 'Bearer ' + key } });
      expect(refused.status()).toBe(403);
      expect((await refused.json()).error.code).toBe('insufficient_scope');
    }
    const revoked = await member.delete(platformProject + '/api-keys/' + key.slice(0, 26), { headers: memberHeaders });
    expect(revoked.status()).toBe(204);
    const deletionProof = await stepUp(member, memberToken, 'delete_account');
    const deleted = await member.delete(auth + '/v1/terrascale/account', {
      headers: { ...memberHeaders, 'X-TerraScale-Step-Up-Proof': deletionProof },
    });
    expect(deleted.status()).toBe(204);
  } finally {
    await member.dispose();
  }
}

export async function prepareAuthManagement(page: Page, sdk: string): Promise<AuthManagement> {
  const current = journey();
  const root = `${auth}/v1/${current.projectId}`;
  await page.goto('/api-keys?create=key');
  const csrf = await page.locator('#create-key input[name="_csrf"]').inputValue();
  const keys: string[] = [];
  for (const level of ['read', 'write']) {
    const created = await page.request.post('/api/v1/dashboard/credentials/key', {
      headers: { Accept: 'application/json' },
      form: { _csrf: csrf, name: `auth-${sdk}-${level}`, scopes: `auth:${level}` },
    });
    expect(created.status()).toBe(201);
    const key = (await created.json()).secret as string;
    saveSecret(`auth-${sdk}-${level}`, key);
    keys.push(key);
  }
  const [readKey, writeKey] = keys;
  const ownerToken = await signInToken(page.request, 'auth-owner');
  if (sdk === 'dotnet') await checkMemberAuthority(page, writeKey, ownerToken);
  const email = await newAddress(`auth-${sdk}-${current.runId}`);
  const started = await page.request.post(`${auth}/ui/${current.projectId}/sign-up`, {
    headers: { Origin: auth },
    form: { email, password: current.password },
  });
  expect(started.status()).toBe(200);
  const pending = /name="pending_token" value="([^"]+)"/.exec(await started.text())?.[1];
  expect(pending).toBeTruthy();
  saveSecret(`auth-${sdk}-pending`, pending!);
  const mail = await nextMail(email, 0);
  const code = /^\s*(\d{6})\s*$/m.exec(mail.text)?.[1];
  expect(code).toBeTruthy();
  const verified = await page.request.post(`${auth}/ui/${current.projectId}/sign-up/verify`, {
    headers: { Origin: auth },
    form: { email, code: code!, pending_token: pending! },
  });
  expect(verified.status()).toBe(200);
  await saveCookies(page.context());
  const users = await page.request.get(`${root}/users`, {
    headers: { Authorization: `Bearer ${readKey}` }, params: { q: email },
  });
  expect(users.status()).toBe(200);
  const items = (await users.json()).items as { id: string; email: string; disabled: boolean }[];
  expect(items).toHaveLength(1);
  expect(items[0]).toMatchObject({ email, disabled: false });
  const user = items[0].id;
  const outside = await page.request.get(`${auth}/v1/${user}/users`, {
    headers: { Authorization: `Bearer ${readKey}` },
  });
  expect(outside.status()).toBe(404);
  const writeRead = await page.request.get(root + '/users', { headers: { Authorization: `Bearer ${writeKey}` } });
  expect(writeRead.status()).toBe(403);
  const refused = await page.request.post(`${root}/users/${user}/disable`, {
    headers: { Authorization: `Bearer ${readKey}` },
  });
  expect(refused.status()).toBe(403);
  expect((await refused.json()).error.code).toBe('insufficient_scope');
  for (const path of ['/webhook-endpoints', `/webhook-endpoints/${user}/rotate-secret`, '/clients', '/providers/google',
    `/clients/${user}`, `/clients/${user}/secret/rotate`]) {
    const response = await page.request.fetch(root + path, {
      method: path === '/providers/google' || path === `/clients/${user}` ? 'PUT' : 'POST',
      headers: { Authorization: `Bearer ${writeKey}` },
    });
    expect(response.status()).toBe(403);
    expect((await response.json()).error).toMatchObject({ code: 'person_required' });
  }
  const missing = await page.request.post(root + '/users/' + current.projectId + '/disable', {
    headers: { Authorization: 'Bearer ' + writeKey },
  });
  expect(missing.status()).toBe(404);
  const invalid = await page.request.put(root + '/settings', {
    headers: { Authorization: 'Bearer ' + writeKey }, data: { methods: { passcode: false, password: false } },
  });
  expect(invalid.status()).toBe(400);
  const policy = await page.request.put(root + '/user-api-key-policy', {
    headers: { Authorization: 'Bearer ' + writeKey }, data: { allowed_scopes: ['invoices:read'] },
  });
  expect(policy.status()).toBe(200);
  const outsideKeyVerify = await page.request.post(auth + '/v1/' + user + '/api-keys/verify', {
    headers: { Authorization: 'Bearer ' + readKey }, data: { key: 'tsk_invalid' },
  });
  expect(outsideKeyVerify.status()).toBe(404);
  const forbiddenScopes = await page.request.post(root + '/users/' + user + '/api-keys', {
    headers: { Authorization: 'Bearer ' + writeKey }, data: { name: 'forbidden', scopes: ['invoices:write'] },
  });
  expect(forbiddenScopes.status()).toBe(400);
  const own = await page.request.post(root + '/me/api-keys', {
    headers: { Origin: auth }, data: { name: 'self-service', scopes: ['invoices:read'] },
  });
  expect(own.status()).toBe(201);
  const ownKey = await own.json();
  saveSecret('self-user-key-' + sdk, ownKey.key);
  expect(new Date(ownKey.expires_at).getTime() - new Date(ownKey.created_at).getTime()).toBeGreaterThan(89 * 86400_000);
  expect(new Date(ownKey.expires_at).getTime() - new Date(ownKey.created_at).getTime()).toBeLessThanOrEqual(90 * 86400_000);
  const tooLong = await page.request.post(root + '/me/api-keys', {
    headers: { Origin: auth }, data: { name: 'too long', scopes: ['invoices:read'], expires_at: new Date(Date.now() + 366 * 86400_000).toISOString() },
  });
  expect(tooLong.status()).toBe(400);
  const ownList = await page.request.get(root + '/me/api-keys');
  expect((await ownList.json()).items.some((item: { id: string }) => item.id === ownKey.id)).toBe(true);
  const csrfRefused = await page.request.delete(root + '/me/api-keys/' + ownKey.id, { headers: { Origin: dash } });
  expect(csrfRefused.status()).toBe(403);
  if (sdk === 'dotnet') {
    const keysPage = '/auth/users?user=' + user;
    await page.goto(keysPage);
    const row = page.locator('#auth-user-api-keys tbody tr').filter({ hasText: ownKey.prefix });
    await expect(row).toContainText('self-service');
    await expect(row).toContainText('invoices:read');
    const uiCreated = await page.request.post(root + '/users/' + user + '/api-keys', {
      headers: { Authorization: 'Bearer ' + writeKey }, data: { name: 'dashboard revoke', scopes: ['invoices:read'] },
    });
    expect(uiCreated.status()).toBe(201);
    const uiKey = await uiCreated.json();
    saveSecret('dashboard-user-key', uiKey.key);
    await page.goto(keysPage);
    await page.locator('#auth-user-api-keys tbody tr').filter({ hasText: uiKey.prefix }).getByRole('button', { name: 'Revoke', exact: true }).click();
    await expect(page.locator('#auth-user-api-keys')).not.toContainText(uiKey.prefix);
    const uiVerify = await page.request.post(root + '/api-keys/verify', {
      headers: { Authorization: 'Bearer ' + readKey }, data: { key: uiKey.key },
    });
    expect((await uiVerify.json()).valid).toBe(false);
    const otherProject = await page.request.post(auth + '/v1/terrascale/organizations/' + current.organizationId + '/projects', {
      headers: { Authorization: 'Bearer ' + ownerToken }, data: { name: 'Key isolation ' + current.runId },
    });
    expect(otherProject.status()).toBe(201);
    const otherId = (await otherProject.json()).id as string;
    const otherKeys = auth + '/v1/terrascale/organizations/' + current.organizationId + '/projects/' + otherId + '/api-keys';
    const otherCreated = await page.request.post(otherKeys, {
      headers: { Authorization: 'Bearer ' + ownerToken }, data: { name: 'isolation verifier', scopes: ['auth:read'] },
    });
    expect(otherCreated.status()).toBe(201);
    const otherKey = (await otherCreated.json()).key as string;
    saveSecret('isolation-project-key', otherKey);
    const crossVerify = await page.request.post(auth + '/v1/' + otherId + '/api-keys/verify', {
      headers: { Authorization: 'Bearer ' + otherKey }, data: { key: ownKey.key },
    });
    expect(crossVerify.status()).toBe(200);
    expect((await crossVerify.json()).valid).toBe(false);
    const otherRevoked = await page.request.delete(otherKeys + '/' + otherKey.slice(0, 26), {
      headers: { Authorization: 'Bearer ' + ownerToken },
    });
    expect(otherRevoked.status()).toBe(204);
  }
  const ownRevoke = await page.request.delete(root + '/me/api-keys/' + ownKey.id, { headers: { Origin: auth } });
  expect(ownRevoke.status()).toBe(204);
  if (sdk === 'dotnet') {
    await checkMachineClient(page, ownerToken, current.projectId);
    await checkUserKeyLifecycle(page.request, root, user, readKey, writeKey);
  }
  return { user, readKey, writeKey, csrf, ownerToken };
}

export async function closeAuthManagement(page: Page, management: AuthManagement): Promise<void> {
  const current = journey();
  const root = `${auth}/v1/${current.projectId}`;
  for (const action of ['disable_project_user.succeeded', 'enable_project_user.succeeded']) {
    const audit = await page.request.get('/api/v1/dashboard/audit-events', { params: { action, range: '7d' } });
    expect(audit.status()).toBe(200);
    const events = (await audit.json()).events as { actor: string; action: string; source: string }[];
    expect(events.some(event => event.actor === management.writeKey.slice(0, 26)
      && event.action === action && event.source === 'ts-auth')).toBe(true);
  }
  const events: { actor: string; action: string; detail: string }[] = [];
  let after: string | undefined;
  do {
    const audit = await page.request.get(auth + '/v1/terrascale/organizations/' + current.organizationId + '/audit-events', {
      headers: { Authorization: 'Bearer ' + management.ownerToken }, params: { limit: 100, ...(after ? { after } : {}) },
    });
    expect(audit.status()).toBe(200);
    const result = await audit.json();
    events.push(...result.items);
    after = result.next ?? undefined;
  } while (after);
  for (const action of ['apiKey.created', 'apiKey.revoked']) {
    expect(events.some(event => event.action === action)).toBe(true);
  }
  const keyEvents = await page.request.get(root + '/events', { headers: { Authorization: 'Bearer ' + management.ownerToken } });
  expect(keyEvents.status()).toBe(200);
  const outboxEvents = (await keyEvents.json()).items as { type: string }[];
  expect(outboxEvents.some(event => event.type === 'apiKey.created')).toBe(true);
  expect(outboxEvents.some(event => event.type === 'apiKey.revoked')).toBe(true);

  for (const [action, key, status] of [
    ['disable_project_user.failed', management.readKey, 403],
    ['disable_project_user.failed', management.writeKey, 404],
    ['update_project_settings.failed', management.writeKey, 400],
    ['set_project_provider.failed', management.writeKey, 403],
  ] as const) {
    expect(events.some(event => event.actor === key.slice(0, 26) && event.action === action
      && event.detail.includes('status=' + status))).toBe(true);
  }
  const deleted = await page.request.delete(`${root}/users/${management.user}`, {
    headers: { Authorization: `Bearer ${management.writeKey}` },
  });
  expect(deleted.status()).toBe(204);
  for (const key of [management.readKey, management.writeKey]) {
    const revoked = await page.request.post('/api/v1/dashboard/credentials/revoke', {
      headers: { Accept: 'application/json' },
      form: { _csrf: management.csrf, keyId: key.slice(0, 26) },
    });
    expect(revoked.status()).toBe(204);
    const users = await page.request.get(root + '/users', { headers: { Authorization: `Bearer ${key}` } });
    expect(users.status()).toBe(401);
  }
}


async function authStorage(sql: string, args: unknown[]): Promise<void> {
  const peer = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  const session = connect('http://127.0.0.1:8081');
  const stream = session.request({ ':method': 'POST', ':path': '/v1/projects/00000000000000000000000002/databases/ts-auth/sql/execute',
    'content-type': 'application/json', 'x-terrabase-peer-credential': peer });
  session.on('error', error => stream.destroy(error));
  try {
    const response = once(stream, 'response');
    stream.end(JSON.stringify({ statements: [{ sql, args }] }));
    const [headers] = await response;
    let body = '';
    stream.setEncoding('utf8');
    for await (const chunk of stream) body += chunk;
    expect(Number(headers[':status']), body).toBe(200);
    expect(JSON.parse(body).results[0].rows_affected).toBeGreaterThan(0);
  } finally {
    session.close();
  }
}

async function checkUserKeyLifecycle(context: APIRequestContext, root: string, user: string, readKey: string, writeKey: string): Promise<void> {
  const created: { id: string; key: string }[] = [];
  for (let index = 0; index < 25; index++) {
    const answer = await context.post(root + '/users/' + user + '/api-keys', {
      headers: { Authorization: 'Bearer ' + writeKey }, data: { name: 'cap-' + index, scopes: ['invoices:read'] },
    });
    expect(answer.status()).toBe(201);
    const key = await answer.json();
    saveSecret('capped-user-key-' + index, key.key);
    created.push(key);
  }
  const capped = await context.post(root + '/users/' + user + '/api-keys', {
    headers: { Authorization: 'Bearer ' + writeKey }, data: { name: '26th', scopes: ['invoices:read'] },
  });
  expect(capped.status()).toBe(409);
  expect((await capped.json()).error).toMatchObject({ code: 'user_key_limit' });
  const cappedSelf = await context.post(root + '/me/api-keys', {
    headers: { Origin: auth }, data: { name: '26th self', scopes: ['invoices:read'] },
  });
  expect(cappedSelf.status()).toBe(409);
  for (const key of created.slice(1)) {
    const removed = await context.delete(root + '/users/' + user + '/api-keys/' + key.id, {
      headers: { Authorization: 'Bearer ' + writeKey },
    });
    expect(removed.status()).toBe(204);
  }
  const own = await context.post(root + '/me/api-keys', {
    headers: { Origin: auth }, data: { name: 'self epoch', scopes: ['invoices:read'] },
  });
  expect(own.status()).toBe(201);
  const ownKey = await own.json();
  saveSecret('self-epoch-key', ownKey.key);
  const verify = async (key: string, valid: boolean) => {
    const answer = await context.post(root + '/api-keys/verify', {
      headers: { Authorization: 'Bearer ' + readKey }, data: { key },
    });
    expect(answer.status()).toBe(200);
    expect((await answer.json()).valid).toBe(valid);
  };
  await verify(created[0].key, true);
  await verify(ownKey.key, true);
  await onLocalStack(async () => {
    await authStorage('UPDATE sessions SET created_at = ? WHERE user_id = ?', [new Date(Date.now() - 11 * 60_000).toISOString(), user]);
    const old = await context.post(root + '/me/api-keys', {
      headers: { Origin: auth }, data: { name: 'old session', scopes: ['invoices:read'] },
    });
    expect(old.status()).toBe(403);
    expect((await old.json()).error).toMatchObject({ code: 'reauthentication_required', message: expect.stringMatching(/sign in again/i) });
  });
  const signedOut = await context.post(root + '/me/sign-out', { headers: { Origin: auth } });
  expect(signedOut.status()).toBe(204);
  await verify(created[0].key, false);
  await verify(ownKey.key, false);
  const ended = await context.get(root + '/me/api-keys');
  expect(ended.status()).toBe(401);
}

async function checkMachineClient(page: Page, ownerToken: string, project: string): Promise<void> {
  const context = page.request;
  const proof = await stepUp(context, ownerToken, 'create_oauth_client');
  const clients = auth + '/v1/' + project + '/clients';
  const created = await context.post(clients, {
    headers: { Authorization: 'Bearer ' + ownerToken, 'X-TerraScale-Step-Up-Proof': proof },
    data: { name: 'journey machine', type: 'machine', redirect_uris: [], allowed_scopes: ['invoices:read'],
      allowed_audiences: ['https://invoices.example'], token_lifetime_minutes: 15 },
  });
  expect(created.status()).toBe(201);
  const client = await created.json();
  saveSecret('machine-client-secret', client.client_secret);
  expect(client.type).toBe('machine');
  await page.goto('/auth/clients');
  const clientRow = page.getByRole('row').filter({ hasText: client.client_id });
  await expect(clientRow).toContainText('machine');
  await expect(clientRow).toContainText('invoices:read');
  await clientRow.getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(page.locator('dialog[open] [name="allowed_scopes"]')).toHaveValue('invoices:read');
  await page.locator('dialog[open]').getByRole('button', { name: 'Close', exact: true }).click();
  const form = { grant_type: 'client_credentials', client_id: client.client_id, client_secret: client.client_secret,
    scope: 'invoices:read', resource: 'https://invoices.example' };
  const endpoint = auth + '/' + project + '/oauth/token';
  const granted = await context.post(endpoint, { form });
  const grantBody = await granted.json();
  expect(granted.status(), 'machine grant: ' + (grantBody.error ?? 'ok') + ' ' + (grantBody.error_description ?? '')).toBe(200);
  const token = grantBody.access_token as string;
  saveSecret('machine-access-token', token);
  const [header, payload, signature] = token.split('.');
  const decodedHeader = JSON.parse(Buffer.from(header, 'base64url').toString());
  const decoded = JSON.parse(Buffer.from(payload, 'base64url').toString());
  expect(decodedHeader.alg).toBe('ES256');
  expect(decoded).toMatchObject({ iss: auth + '/' + project, sub: client.client_id, scope: 'invoices:read', aud: 'https://invoices.example', token_use: 'machine' });
  expect(decoded.exp - decoded.iat).toBe(900);
  const jwks = await context.get(auth + '/' + project + '/.well-known/jwks.json');
  expect(jwks.status()).toBe(200);
  const key = (await jwks.json()).keys.find((item: { kid: string }) => item.kid === decodedHeader.kid);
  expect(verifySignature('sha256', Buffer.from(header + '.' + payload),
    { key: createPublicKey({ key, format: 'jwk' }), dsaEncoding: 'ieee-p1363' }, Buffer.from(signature, 'base64url'))).toBe(true);
  const expanded = await context.post(endpoint, { form: { ...form, scope: 'invoices:write' } });
  expect(expanded.status()).toBe(400);
  const wrongAudience = await context.post(endpoint, { form: { ...form, resource: 'https://other.example' } });
  expect(wrongAudience.status()).toBe(400);
  const crossProject = await context.post(auth + '/00000000000000000000000001/oauth/token', { form });
  expect(crossProject.status()).not.toBe(200);
  const interactiveProof = await stepUp(context, ownerToken, 'create_oauth_client');
  const interactive = await context.post(clients, {
    headers: { Authorization: 'Bearer ' + ownerToken, 'X-TerraScale-Step-Up-Proof': interactiveProof },
    data: { name: 'journey interactive', type: 'confidential', redirect_uris: ['https://app.example/callback'] },
  });
  expect(interactive.status()).toBe(201);
  const userClient = await interactive.json();
  saveSecret('interactive-client-secret', userClient.client_secret);
  const refusedUserClient = await context.post(endpoint, { form: { grant_type: 'client_credentials',
    client_id: userClient.client_id, client_secret: userClient.client_secret, scope: 'invoices:read' } });
  expect(refusedUserClient.status()).toBe(400);
  const verifier = randomBytes(32).toString('base64url');
  saveSecret('project-user-pkce', verifier);
  const authorization = await context.get(auth + '/' + project + '/oauth/authorize', {
    params: { response_type: 'code', client_id: userClient.client_id, redirect_uri: 'https://app.example/callback',
      scope: 'openid email', code_challenge: createHash('sha256').update(verifier).digest('base64url'), code_challenge_method: 'S256' },
    maxRedirects: 0,
  });
  expect(authorization.status()).toBe(302);
  const code = new URL(authorization.headers().location).searchParams.get('code');
  expect(code).toBeTruthy();
  saveSecret('project-user-code', code!);
  const userGrant = await context.post(endpoint, { form: { grant_type: 'authorization_code', client_id: userClient.client_id,
    client_secret: userClient.client_secret, redirect_uri: 'https://app.example/callback', code: code!, code_verifier: verifier } });
  expect(userGrant.status()).toBe(200);
  const userToken = (await userGrant.json()).access_token as string;
  saveSecret('project-user-access-token', userToken);
  expect(JSON.parse(Buffer.from(userToken.split('.')[1], 'base64url').toString()).token_use).toBe('user');
  for (let attempt = 0; attempt < 10; attempt++) {
    const badSecret = await context.post(endpoint, { form: { ...form, client_secret: 'incorrect-secret' } });
    expect(badSecret.status()).toBe(401);
  }
  const locked = await context.post(endpoint, { form });
  expect(locked.status()).toBe(401);
  const removedUserClient = await context.delete(clients + '/' + userClient.client_id,
    { headers: { Authorization: 'Bearer ' + ownerToken } });
  expect(removedUserClient.status()).toBe(204);
  const deleted = await context.delete(clients + '/' + client.client_id, { headers: { Authorization: 'Bearer ' + ownerToken } });
  expect(deleted.status()).toBe(204);
}
