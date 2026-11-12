import type { APIRequestContext, Page } from '@playwright/test';
import { expect, request } from '@playwright/test';
import { newAddress, nextMail } from './inbox';
import { auth, dash } from './stack';
import { journey, saveCookies, saveSecret } from './state';

export type AuthManagement = { user: string; readKey: string; writeKey: string; csrf: string; ownerToken: string };

export async function signInToken(context: APIRequestContext, label: string): Promise<string> {
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
  const audit = await page.request.get(auth + '/v1/terrascale/organizations/' + current.organizationId + '/audit-events', {
    headers: { Authorization: 'Bearer ' + management.ownerToken }, params: { limit: 100 },
  });
  expect(audit.status()).toBe(200);
  const events = (await audit.json()).items as { actor: string; action: string; detail: string }[];
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
