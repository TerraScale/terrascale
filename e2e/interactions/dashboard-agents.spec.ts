import { createHash, generateKeyPairSync } from 'node:crypto';
import { expect, test } from '../support/audit';
import { createDedicatedProjectOwner } from '../support/project-owner';
import { auth } from '../support/stack';

test('project agents show their owner and keys and administrators can revoke access', async ({ browser }, info) => {
  const account = await createDedicatedProjectOwner(browser, info, 'Dashboard agents owner');
  const { page, audit, current } = account;
  const root = auth + '/v1/' + current.projectId;
  const headers = { Authorization: 'Bearer ' + current.apiKey };
  try {
    await page.goto('/auth/agents');
    await expect(page.getByText('No agents yet. Agents appear here when a user enrolls one or your backend creates one through the API.', { exact: true })).toBeVisible();
    await audit.check('agents empty and policy');
    await page.getByLabel('Allowed scopes', { exact: true }).fill('invoices:read\ninvoices:write\ninvoices:read,limited');
    await Promise.all([
      page.waitForResponse(response => response.url().includes('/auth-management/agents/policy') && response.request().method() === 'POST'),
      page.getByRole('button', { name: 'Save policy', exact: true }).click(),
    ]);
    const policy = await page.request.get(root + '/agent-policy', { headers });
    expect(policy.status()).toBe(200);
    expect((await policy.json()).allowed_scopes).toEqual(['invoices:read', 'invoices:write', 'invoices:read,limited']);

    const createdUser = await page.request.post(root + '/users', {
      headers, data: { email: 'agent-owner-' + current.runId + '@example.com' },
    });
    expect(createdUser.status()).toBe(201);
    const user = await createdUser.json() as { id: string; email: string };
    const { publicKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
    const { kty, crv, x, y } = publicKey.export({ format: 'jwk' });
    const thumbprint = createHash('sha256').update(JSON.stringify({ crv, kty, x, y })).digest('base64url');
    const createdAgent = await page.request.post(root + '/users/' + user.id + '/agents', {
      headers, data: { name: 'Invoice assistant', allowed_scopes: ['invoices:read'], public_jwk: { kty, crv, x, y } },
    });
    expect(createdAgent.status()).toBe(201);
    const agent = await createdAgent.json() as { id: string; keys: { kid: string }[] };
    const otherUser = await page.request.post(root + '/users', {
      headers, data: { email: 'other-agent-owner-' + current.runId + '@example.com' },
    });
    expect(otherUser.status()).toBe(201);
    const other = await otherUser.json() as { id: string };
    const otherKey = generateKeyPairSync('ec', { namedCurve: 'prime256v1' }).publicKey.export({ format: 'jwk' });
    const otherAgent = await page.request.post(root + '/users/' + other.id + '/agents', {
      headers, data: { name: 'Other owner assistant', allowed_scopes: ['invoices:read'],
        public_jwk: { kty: otherKey.kty, crv: otherKey.crv, x: otherKey.x, y: otherKey.y } },
    });
    expect(otherAgent.status()).toBe(201);
    await page.reload();
    const row = page.getByRole('row').filter({ hasText: 'Invoice assistant' });
    await expect(row).toContainText(user.id);
    await expect(row).toContainText('active');
    await expect(row).toContainText('invoices:read');
    await expect(row).toContainText(agent.keys[0].kid.slice(0, 12));
    await expect(row).toContainText(thumbprint.slice(0, 12));
    await expect(row).toContainText('Never');
    await audit.check('agents list');
    await row.getByRole('link', { name: user.id, exact: true }).click();
    await expect(page.getByRole('heading', { name: user.email, exact: true })).toBeVisible();
    await expect(page.locator('#auth-user-profile')).toBeVisible();
    await audit.check('agent owner link');
    const firstPage = await page.request.get(root + '/agents', { headers, params: { limit: 1 } });
    expect(firstPage.status()).toBe(200);
    const cursor = (await firstPage.json()).next as string;
    expect(cursor).toBeTruthy();
    await page.goto('/auth/agents?owner=' + user.id + '&after=' + cursor);
    const empty = page.getByText('No agents yet. Agents appear here when a user enrolls one or your backend creates one through the API.', { exact: true });
    await expect(empty).toBeVisible();
    await expect(page.getByLabel('Allowed scopes', { exact: true })).toHaveValue('invoices:read\ninvoices:write\ninvoices:read,limited');
    await Promise.all([
      page.waitForResponse(response => response.url().includes('/auth-management/agents/policy') && response.request().method() === 'POST'),
      page.getByRole('button', { name: 'Save policy', exact: true }).click(),
    ]);
    await expect(page.getByRole('status').filter({ hasText: 'Changes saved.' })).toBeVisible();
    await expect(empty).toBeVisible();
    await expect(page.getByRole('link', { name: 'Other owner assistant', exact: true })).toHaveCount(0);
    expect(new URL(page.url()).searchParams.get('owner')).toBe(user.id);
    expect(new URL(page.url()).searchParams.get('after')).toBe(cursor);
    const policyForm = page.locator('form').filter({ has: page.getByLabel('Allowed scopes', { exact: true }) });
    const action = new URL((await policyForm.getAttribute('action'))!, page.url());
    expect(action.searchParams.get('owner')).toBe(user.id);
    expect(action.searchParams.get('after')).toBe(cursor);
    const nativeSave = await page.request.post(action.href, {
      headers: { Accept: 'text/html' }, maxRedirects: 0,
      form: { _csrf: await policyForm.locator('[name=_csrf]').inputValue(),
        allowed_scopes: await page.getByLabel('Allowed scopes', { exact: true }).inputValue() },
    });
    expect(nativeSave.status()).toBe(303);
    const destination = new URL(nativeSave.headers().location, page.url());
    expect(destination.searchParams.get('owner')).toBe(user.id);
    expect(destination.searchParams.get('after')).toBe(cursor);
    expect(destination.searchParams.get('saved')).toBe('1');
    await page.goto(destination.href);
    await expect(empty).toBeVisible();
    const savedPolicy = await page.request.get(root + '/agent-policy', { headers });
    expect((await savedPolicy.json()).allowed_scopes).toEqual(['invoices:read', 'invoices:write', 'invoices:read,limited']);
    await audit.check('filtered policy saves preserve cursor and comma scope');
    await page.getByRole('link', { name: 'First page', exact: true }).click();
    await expect(page.getByRole('row').filter({ hasText: 'Invoice assistant' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Other owner assistant', exact: true })).toHaveCount(0);
    await audit.check('agents owner filter');
    await page.getByRole('link', { name: 'Invoice assistant', exact: true }).click();
    await expect(page.locator('#auth-agents')).toContainText(agent.id);
    await expect(page.getByRole('region', { name: 'Agent keys', exact: true })).toContainText(thumbprint.slice(0, 12));
    await audit.check('agent detail');
    const keyRow = page.getByRole('region', { name: 'Agent keys', exact: true }).getByRole('row').filter({ hasText: thumbprint.slice(0, 12) });
    await keyRow.getByRole('button', { name: 'Revoke key', exact: true }).click();
    const keyDialog = page.locator('#revoke-agent-key-' + agent.keys[0].kid);
    await expect(keyDialog).toContainText('The agent can still sign in with its other key, if it has one.');
    await audit.check('agent key revoke dialog');
    await keyDialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(keyDialog).not.toBeVisible();
    await keyRow.getByRole('button', { name: 'Revoke key', exact: true }).click();
    await keyDialog.getByRole('button', { name: 'Revoke key', exact: true }).click();
    await expect(page.locator('#auth-agents').getByRole('button', { name: 'Revoke key', exact: true })).toHaveCount(0);
    const keyRevoked = await page.request.get(root + '/agents/' + agent.id, { headers });
    expect(keyRevoked.status()).toBe(200);
    expect((await keyRevoked.json()).keys[0].revoked_at).not.toBeNull();
    await audit.check('agent key revoked');
    await page.getByRole('button', { name: 'Revoke agent', exact: true }).first().click();
    const agentDialog = page.locator('#revoke-agent[open]');
    await expect(agentDialog).toContainText('Revoke Invoice assistant?');
    await expect(agentDialog).toContainText('Tokens it already holds expire within 10 minutes.');
    await audit.check('agent revoke dialog');
    await agentDialog.getByRole('button', { name: 'Revoke agent', exact: true }).click();
    await expect(page.locator('#auth-agents')).toContainText('revoked');
    await expect(page.locator('#auth-agents').getByRole('button', { name: 'Revoke agent', exact: true })).toHaveCount(0);
    const revoked = await page.request.get(root + '/agents/' + agent.id, { headers });
    expect(revoked.status()).toBe(200);
    expect((await revoked.json()).status).toBe('revoked');
    await audit.check('agent revoked');
    await page.getByRole('link', { name: 'All agents', exact: true }).click();
    await expect(page.getByRole('row').filter({ hasText: 'Invoice assistant' })).toContainText('revoked');
    await page.getByLabel('Allowed scopes', { exact: true }).fill('');
    await Promise.all([
      page.waitForResponse(response => response.url().includes('/auth-management/agents/policy') && response.request().method() === 'POST'),
      page.getByRole('button', { name: 'Save policy', exact: true }).click(),
    ]);
    const blocked = await page.request.get(root + '/agent-policy', { headers });
    expect((await blocked.json()).allowed_scopes).toEqual([]);
    await audit.check('empty agent policy');
  } finally {
    await account.remove();
  }
});
