import { expect, test } from '@playwright/test';
import { keyId } from '../support/api-key';
import { auth } from '../support/stack';
import { journey, saveSecret } from '../support/state';

test('API key status answers every concurrent check of one hot key and reports no storage counts', async ({ request }) => {
  const current = journey();
  const issued = await request.post(auth + '/terrascale/oauth/token', {
    form: {
      grant_type: 'client_credentials', client_id: keyId(current.apiKey), client_secret: current.apiKey,
      project: current.projectId, scope: 'sql:read',
    },
  });
  expect(issued.status()).toBe(200);
  const token = (await issued.json()).access_token as string;
  saveSecret('api-key-status-token', token);
  const status = auth + '/v1/terrascale/api-keys/self';

  const checks = await Promise.all(Array.from({ length: 25 }, () => request.get(status, { headers: { authorization: 'Bearer ' + token } })));
  for (const check of checks) {
    expect(check.status(), await check.text()).toBe(200);
    expect(await check.json()).toEqual({ key_id: keyId(current.apiKey) });
    expect(check.headers()['cache-control']).toBe('no-store');
    expect(check.headers()['server-timing'] ?? '').not.toMatch(/calls|writes/);
  }

  const [header, payload] = token.split('.');
  const forged = await request.get(status, { headers: { authorization: `Bearer ${header}.${payload}.${'A'.repeat(86)}` } });
  expect(forged.status()).toBe(401);
  expect((await forged.json()).error.code).toBe('invalid_token');
});
