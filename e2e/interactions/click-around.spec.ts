import type { Page } from '@playwright/test';
import { test, expect } from '../support/audit';
import { confirmWithPassword } from '../support/hosted';
import { auth, dash } from '../support/stack';
import { shell } from '../support/shell';
import { saveSecret, journey } from '../support/state';

const apiKeyFormat = /^[0-9A-HJKMNP-TV-Z]{26}_[0-9a-hjkmnp-tv-z]{52}$/;
const tokenFormat = /^tspat_[A-Za-z0-9_-]{43}$/;
const unique = (base: string) => `${base}-${journey().runId}${Date.now().toString(36)}`;

async function runSql(page: Page, sql: string): Promise<void> {
  const checked = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/v1/dashboard/data/sql/check');
  await page.locator('#sql').evaluate((editor: HTMLTextAreaElement, text: string) => {
    editor.value = text;
    editor.dispatchEvent(new Event('input', { bubbles: true }));
  }, sql);
  await expect(page.locator('#sql')).toHaveValue(sql);
  expect((await checked).status(), 'the editor checked the statement').toBe(200);
  await page.locator('form[action^="/api/v1/dashboard/data/sql"]').getByRole('button', { name: 'Run query', exact: true }).click();
  await page.waitForLoadState('load');
}

test('SQL editor creates a table, inserts and selects; a saved query reopens; the Tables tab lists the rows', async ({ page, audit }) => {
  const current = journey();
  const table = unique('editor').replaceAll('-', '_');
  const queryName = unique('scores');
  await page.goto(`/databases/${current.databaseId}/sql`);
  await shell(page, current);
  await audit.check('SQL editor');
  await runSql(page, `CREATE TABLE ${table} (id INTEGER PRIMARY KEY, name TEXT, score INTEGER)`);
  await runSql(page, `INSERT INTO ${table} (name, score) VALUES ('Ada', 7), ('Grace', 9)`);
  const select = `SELECT name, score FROM ${table} ORDER BY name`;
  await runSql(page, select);
  const results = page.locator('main table').last();
  await expect(results.locator('tbody tr')).toHaveCount(2);
  await expect(results.locator('tbody tr').first()).toContainText('Ada');
  await expect(results.locator('tbody tr').last()).toContainText('Grace');
  await audit.check('SQL editor results');

  await page.getByRole('button', { name: 'Save query', exact: true }).click();
  const dialog = page.locator('#save-query');
  await expect(dialog).toBeVisible();
  await dialog.locator('#query-name').fill(queryName);
  await expect(dialog.locator('#saved-sql')).toHaveValue(select);
  await audit.check('save query dialog');
  await dialog.locator('form[action^="/api/v1/dashboard/data/saved-queries"]').locator('button[type=submit]').click();
  await page.waitForLoadState('load');
  const saved = page.getByRole('region', { name: 'Saved queries' }).getByRole('link').filter({ hasText: queryName });
  await expect(saved).toBeVisible();
  await saved.click();
  await expect(page.locator('#sql')).toHaveValue(select);
  await audit.check('saved query reopened');

  await page.goto(`/databases/${current.databaseId}/tables`);
  const link = page.getByRole('navigation', { name: 'Tables' }).getByRole('link').filter({ hasText: table });
  await expect(link).toContainText('2 rows');
  await link.click();
  await expect(page.locator('main').getByText('Grace', { exact: true })).toBeVisible();
  await expect(page.locator('main').getByText('Ada', { exact: true })).toBeVisible();
  await audit.check('table rows');
});

test('an API key is shown once and rotates twice during grace without another click before revocation', async ({ page, audit }) => {
  const current = journey();
  const name = unique('second-key');
  await page.goto('/api-keys?create=key');
  await page.locator('#key-name').fill(name);
  await page.locator('#create-key form').getByRole('button', { name: 'Create key', exact: true }).click();
  const secret = page.locator('#key-secret #api-key-secret');
  await expect(secret).toBeVisible();
  const key = (await secret.innerText()).trim();
  saveSecret('second-api-key', key);
  expect(apiKeyFormat.test(key), 'the second API key has the canonical format').toBe(true);
  await audit.check('second API key created');

  await page.locator('#key-secret').getByRole('link', { name: 'Done', exact: true }).click();
  const secrets = [key];
  for (let generation = 1; generation <= 2; generation += 1) {
    const target = generation === 1 ? '/api-keys' : `/api-keys/${key.slice(0, 26)}`;
    await page.goto(target);
    expect((await page.content()).includes(secrets.at(-1)!), 'the previous reveal is gone').toBe(false);
    const rotating = generation === 1 ? page.locator('main tr').filter({ hasText: name }) : page.locator('main');
    await rotating.getByRole('button', { name: 'Rotate', exact: true }).click();
    const dialog = page.locator('dialog[open]');
    const warning = dialog.getByText(/The previous secret still works until .*Rotating now ends it immediately\./);
    if (generation === 1) await expect(warning).toHaveCount(0);
    else await expect(warning).toBeVisible();
    await expect(dialog.locator('[name=dialog]')).toHaveValue(`rotate-${key.slice(0, 26)}`);
    if (generation === 2) await expect(dialog.locator('[name=view]')).toHaveValue('details');
    await dialog.locator('[name=previousSecretTtl]').selectOption('3600');
    await dialog.getByRole('button', { name: 'Rotate key', exact: true }).click();
    let fallback = '';
    const approved = page.waitForResponse(async (response) => {
      if (response.request().method() !== 'POST' || !new URL(response.url()).pathname.startsWith('/ui/terrascale/step-up/')) return false;
      fallback = await response.text();
      return true;
    });
    await confirmWithPassword(page, audit, current.password, `rotation ${generation}`);
    const confirmation = await approved;
    expect(confirmation.status()).toBe(200);
    expect(confirmation.headers()['cache-control']).toBe('no-store');
    expect(confirmation.headers()['refresh']).toBe(`0;url=${dash}/auth/step-up/callback`);
    expect(confirmation.headers()['content-security-policy']).toContain("style-src 'self' 'nonce-");
    expect(fallback).toContain(`href="${dash}/auth/step-up/callback"`);
    expect(fallback).toContain('class="btn delayed-continue"');
    await expect(secret).toBeVisible();
    expect(new URL(page.url()).origin).toBe(dash);
    const rotated = (await secret.innerText()).trim();
    saveSecret(`rotated-api-key-${generation}`, rotated);
    expect(apiKeyFormat.test(rotated), 'rotation reveals a canonical API key').toBe(true);
    expect(rotated === secrets.at(-1), 'rotation changes the secret').toBe(false);
    expect(rotated.slice(0, 26), 'rotation preserves the key id').toBe(key.slice(0, 26));
    secrets.push(rotated);
    await page.locator('#key-secret').getByRole('link', { name: 'Done', exact: true }).click();
    await expect(page).toHaveURL((url) => url.pathname === target);
  }

  for (const [generation, credential] of secrets.entries()) {
    const exchange = await page.request.post(`${auth}/terrascale/oauth/token`, {
      form: {
        grant_type: 'client_credentials',
        client_id: credential.slice(0, 26),
        client_secret: credential,
        project: current.projectId,
        scope: 'sql:read',
      },
    });
    expect(exchange.status(), `secret generation ${generation} authentication`).toBe(generation === 0 ? 401 : 200);
    if (generation > 0) saveSecret(`rotation-token-${generation}`, (await exchange.json()).access_token);
  }

  const replay = await page.request.get(`${dash}/auth/step-up/callback`);
  expect(replay.status(), 'a consumed callback fails closed').toBe(403);
  const queryCallback = await page.request.get(`${dash}/auth/step-up/callback?keyId=${key.slice(0, 26)}`);
  expect(queryCallback.status(), 'a callback with a query string is refused').toBe(403);
  await audit.check('API key rotated twice');

  await page.goto('/api-keys');
  for (const credential of secrets) expect((await page.content()).includes(credential), 'API key secrets are never shown again').toBe(false);
  const row = page.locator('main tr').filter({ hasText: name });
  await row.getByRole('button', { name: 'Revoke', exact: true }).click();
  await expect(page.locator('dialog[open]')).toBeVisible();
  await audit.check('revoke API key dialog');
  await page.locator('dialog[open]').getByRole('button', { name: 'Revoke key', exact: true }).click();
  await page.waitForURL((url) => url.pathname === '/api-keys');
  await expect(page.locator('main tr').filter({ hasText: name })).toHaveCount(0);
  await expect(page.locator('main tr').filter({ hasText: current.apiKeyName })).toHaveCount(1);
  await audit.check('API keys after revoke');
});

test('a personal access token is created with a confirmation, shown once, and revoked', async ({ page, audit }) => {
  const current = journey();
  const name = unique('token');
  await page.goto('/profile?tab=access-tokens');
  await page.locator('#accessTokenName').fill(name);
  await page.locator('#profile-access-token-create form').getByRole('button', { name: 'Create token', exact: true }).click();
  await confirmWithPassword(page, audit, current.password, 'access token');
  const secret = page.locator('#access-token-secret');
  await expect(secret).toBeVisible();
  const token = await secret.inputValue();
  saveSecret('access-token', token);
  expect(tokenFormat.test(token), 'the access token has the canonical format').toBe(true);
  await audit.check('access token created');

  await page.goto('/profile?tab=access-tokens');
  await expect(page.locator('#access-token-secret')).toHaveCount(0);
  expect((await page.content()).includes(token), 'the access token is never shown again').toBe(false);
  const row = page.locator('#profile-access-tokens tbody tr').filter({ hasText: name });
  await row.getByRole('button', { name: 'Revoke', exact: true }).click();
  await expect(page.locator('dialog[open]')).toBeVisible();
  await audit.check('revoke access token dialog');
  await page.locator('dialog[open]').getByRole('button', { name: 'Revoke token', exact: true }).click();
  await page.waitForURL((url) => url.pathname === '/profile' && url.searchParams.get('status') === 'token-revoked');
  await expect(page.locator('#profile-access-tokens tbody tr').filter({ hasText: name })).toHaveCount(0);
  await audit.check('access tokens after revoke');
});

test('an alert rule is created and deleted', async ({ page, audit }) => {
  const name = unique('latency');
  await page.goto('/notifications/alerts');
  await page.locator('#alert-name').fill(name);
  await page.locator('#alert-threshold').fill('250');
  await page.getByRole('button', { name: 'Create rule', exact: true }).click();
  await page.waitForLoadState('load');
  const rule = page.locator('main tr, main li').filter({ hasText: name });
  await expect(rule).toHaveCount(1);
  await audit.check('alert rule created');
  await rule.locator('form[action*="/delete"]').locator('button[type=submit]').click();
  await page.waitForLoadState('load');
  await expect(page.locator('main tr, main li').filter({ hasText: name })).toHaveCount(0);
  await audit.check('alert rules after delete');
});
