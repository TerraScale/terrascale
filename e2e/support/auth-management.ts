import type { Page } from '@playwright/test';
import { expect } from '@playwright/test';
import { newAddress, nextMail } from './inbox';
import { auth } from './stack';
import { journey, saveCookies, saveSecret } from './state';

export type AuthManagement = { user: string; readKey: string; writeKey: string; csrf: string };

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
  for (const path of ['/webhook-endpoints', `/webhook-endpoints/${user}/rotate-secret`, '/clients',
    `/clients/${user}`, `/clients/${user}/secret/rotate`]) {
    const response = await page.request.fetch(root + path, {
      method: path === `/clients/${user}` ? 'PUT' : 'POST',
      headers: { Authorization: `Bearer ${writeKey}` },
    });
    expect(response.status()).toBe(403);
    expect((await response.json()).error).toMatchObject({ code: 'person_required' });
  }
  return { user, readKey, writeKey, csrf };
}

export async function closeAuthManagement(page: Page, management: AuthManagement): Promise<void> {
  const current = journey();
  const root = `${auth}/v1/${current.projectId}`;
  for (const action of ['disable_project_user.requested', 'enable_project_user.requested']) {
    const audit = await page.request.get('/api/v1/dashboard/audit-events', { params: { action, range: '7d' } });
    expect(audit.status()).toBe(200);
    const events = (await audit.json()).events as { actor: string; action: string; source: string }[];
    expect(events.some(event => event.actor === management.writeKey.slice(0, 26)
      && event.action === action && event.source === 'ts-auth')).toBe(true);
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
