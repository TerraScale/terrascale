import AxeBuilder from '@axe-core/playwright';
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

test('the SQL editor suggests CREATE TABLE while typing create', async ({ page }) => {
  const current = journey();
  await page.goto(`/databases/${current.databaseId}/sql`);
  await shell(page, current);
  const editor = page.locator('#sql-editor ts-code-editor');
  await expect(editor).toHaveAttribute('data-ready', '');
  await editor.locator('.monaco-editor').click();
  await page.keyboard.type('create');
  const createTable = page.locator('.suggest-widget .monaco-list-row').filter({
    has: page.locator('.monaco-highlighted-label', { hasText: /^CREATE TABLE$/ }),
  });
  await expect(createTable).toBeVisible();
  await createTable.click();
  await expect(page.locator('#sql')).toHaveValue(/^CREATE TABLE/);
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

test('an alert webhook secret is shown once, including two parallel reads, and its rules are deleted', async ({ page, audit }) => {
  const name = unique('latency');
  await page.goto('/notifications/alerts');
  await page.locator('#alert-name').fill(name);
  await page.locator('#alert-threshold').fill('250');
  await page.locator('#alert-webhook').fill('https://example.com/alerts');
  await page.getByRole('button', { name: 'Create rule', exact: true }).click();
  const revealed = page.locator('#alert-secret');
  await expect(revealed).toBeVisible();
  const secret = (await revealed.innerText()).trim();
  saveSecret('alert-webhook-secret', secret);
  expect(/^[A-Za-z0-9_-]{43}$/.test(secret), 'the webhook signing secret has the expected format').toBe(true);
  await expect(page).toHaveURL((url) => url.origin === dash && url.pathname === '/notifications/alerts' && [...url.searchParams.keys()].every((key) => ['organizationId', 'projectId'].includes(key)));
  await expect(page.locator('#alert-rules tr').filter({ hasText: name })).toHaveCount(1);
  await audit.check('alert webhook secret created');
  await page.getByRole('button', { name: 'Continue to alerts', exact: true }).click();
  await expect(page.locator('#alert-secret-panel')).toBeHidden();
  await expect(revealed).toBeEmpty();
  expect((await page.content()).includes(secret), 'dismissing the panel clears the secret from the DOM').toBe(false);
  await audit.check('alert webhook secret dismissed');
  const reloaded = await page.reload();
  expect(reloaded?.headers()['set-cookie'] ?? '').not.toContain('ts-dashboard-alert-secret=');
  await expect(revealed).toHaveCount(0);
  expect((await page.content()).includes(secret), 'the webhook secret is never shown again').toBe(false);

  const parallelName = unique('parallel');
  const form = await page.locator('form').filter({ has: page.locator('#alert-name') }).evaluate((element) => ({ action: (element as HTMLFormElement).action, values: Object.fromEntries(new FormData(element as HTMLFormElement)) }));
  const created = await page.request.post(form.action, {
    form: { ...form.values, name: parallelName, threshold: '250', webhook: 'https://example.com/alerts' },
    headers: { Origin: dash },
    maxRedirects: 0,
  });
  expect(created.status()).toBe(303);
  const target = new URL(created.headers().location, dash);
  expect(target.origin).toBe(dash);
  expect(target.pathname).toBe('/notifications/alerts');
  expect([...target.searchParams.keys()].every((key) => ['organizationId', 'projectId'].includes(key)), 'the redirect contains only workspace selectors').toBe(true);
  const headers = { Cookie: (await page.context().cookies(dash)).map((cookie) => `${cookie.name}=${cookie.value}`).join('; ') };
  const reads = await Promise.all([page.request.get(target.href, { headers }), page.request.get(target.href, { headers })]);
  let disclosures = 0;
  for (const response of reads) {
    expect(response.status()).toBe(200);
    const value = /<pre[^>]*id="alert-secret"[^>]*>([^<]+)<\/pre>/.exec(await response.text())?.[1];
    if (value) {
      saveSecret('parallel-alert-webhook-secret', value);
      disclosures += 1;
    }
  }
  expect(disclosures, 'two parallel GETs disclose the secret exactly once').toBe(1);
  await page.reload();
  await expect(revealed).toHaveCount(0);
  for (const ruleName of [name, parallelName]) {
    const rule = page.locator('#alert-rules tr').filter({ hasText: ruleName });
    await expect(rule).toHaveCount(1);
    await rule.locator('form[action*="/delete"]').locator('button[type=submit]').click();
    await expect(rule).toHaveCount(0);
  }
  await audit.check('alert rules after delete');
});

for (const area of ['usage', 'analytics']) {
  test(`${area} filters keep text readable while measurements refresh`, async ({ page, audit }) => {
    await page.goto(`/${area}?range=7d`);
    let release!: () => void;
    const held = new Promise<void>(resolve => { release = resolve; });
    await page.route(`**/${area}?*`, async route => {
      await held;
      await route.continue();
    }, { times: 1 });
    const updated = page.waitForResponse(response => new URL(response.url()).pathname === `/${area}`);
    try {
      await page.locator(area === 'usage' ? '#usage-database' : 'select[name="database"]').selectOption(journey().databaseId);
      await expect(page.locator('body')).toHaveClass(/ts-nav-pending/);
      await page.locator('main').evaluate(async element => {
        await Promise.all(element.getAnimations({ subtree: true })
          .filter(animation => animation.effect?.getComputedTiming().endTime !== Infinity)
          .map(animation => animation.finished));
      });
      const contrast = await new AxeBuilder({ page }).include('main').withRules(['color-contrast']).analyze();
      expect(contrast.violations, 'refreshing measurements retain readable text').toEqual([]);
    } finally {
      release();
      await updated;
    }
    await expect(page.locator('body')).not.toHaveClass(/ts-nav-pending/);
    await audit.check(`${area} measurements refreshed`);
  });
}

test('native dropdowns preserve filters and context while browser popovers dismiss and return focus', async ({ page, audit }) => {
  const current = journey();
  await page.goto('/analytics?range=7d');
  const database = page.locator('select[name="database"]');
  await database.selectOption(current.databaseId);
  await expect(page).toHaveURL(url => url.searchParams.get('database') === current.databaseId && url.searchParams.get('project') === current.projectId && url.searchParams.get('range') === '7d');
  await expect(database).toHaveValue(current.databaseId);
  await audit.check('native analytics filters');

  await page.goto('/usage?range=7d');
  const project = page.locator('#usage-project');
  const projectUpdated = page.waitForResponse(response => new URL(response.url()).pathname === '/usage' && new URL(response.url()).searchParams.get('project') === current.projectId);
  await project.selectOption(current.projectId);
  const projectResponse = await projectUpdated;
  expect(projectResponse.status()).toBe(200);
  await projectResponse.finished();
  await expect(page.locator('body')).not.toHaveClass(/ts-nav-pending/);
  await expect(page).toHaveURL(url => url.searchParams.get('project') === current.projectId && url.searchParams.get('range') === '7d');
  await page.locator('#usage-database').selectOption(current.databaseId);
  await expect(page).toHaveURL(url => url.searchParams.get('database') === current.databaseId && url.searchParams.get('project') === current.projectId && url.searchParams.get('range') === '7d');
  await audit.check('native usage filters');

  await page.goto('/organization?tab=members');
  await page.getByRole('searchbox', { name: 'Filter members', exact: true }).fill(current.email);
  const role = page.getByRole('combobox', { name: 'Organization role', exact: true });
  await role.selectOption('owner');
  await expect(role).toHaveValue('owner');
  await expect(page.locator('#access-filter-form input[name="q"]')).toHaveValue(current.email);
  expect(await role.evaluate(element => new FormData((element as HTMLSelectElement).form!).getAll('role'))).toEqual(['owner']);
  await expect(page.locator('main tbody tr').filter({ hasText: current.email })).toContainText('Owner');
  await audit.check('native member role filter');

  const account = page.locator('.ts-account-trigger');
  const context = page.locator('.ts-context-trigger');
  const accountPanel = page.locator('#ts-account-menu');
  const contextPanel = page.locator('#ts-context-menu');
  await account.focus();
  await account.press('Enter');
  await expect(accountPanel).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(accountPanel).not.toBeVisible();
  await expect(account).toBeFocused();
  await account.click();
  const notificationsLoaded = page.waitForResponse(response => new URL(response.url()).pathname === '/notifications/dialog');
  await page.locator('#ts-notifications-menu').click();
  expect((await notificationsLoaded).status()).toBe(200);
  const notifications = page.locator('#ts-notifications-dialog');
  await expect(notifications).toBeVisible();
  await expect(accountPanel).not.toBeVisible();
  await expect(notifications.locator('.ts-notifications-summary')).toContainText(/unread.*total/);
  await audit.check('notifications from native account popover');
  await page.keyboard.press('Escape');
  await expect(notifications).not.toBeVisible();
  await expect(account).toBeFocused();
  await account.click();
  await context.click();
  await expect(contextPanel).toBeVisible();
  await expect(accountPanel).not.toBeVisible();
  await audit.check('native context popover');
  await page.locator('main h1').click();
  await expect(contextPanel).not.toBeVisible();
  await context.click();
  await page.locator('[data-organization-option="' + current.organizationId + '"]').click();
  await page.waitForLoadState('load');
  await shell(page, current);
  await context.click();
  await page.locator('[data-project-option="' + current.projectId + '"]').click();
  await page.waitForLoadState('load');
  await shell(page, current);

  await page.goto('/organization?tab=audit');
  const exporter = page.getByRole('button', { name: 'Export audit events', exact: true });
  await exporter.click();
  await expect(page.locator('#audit-export-menu')).toBeVisible();
  await expect(page.locator('#audit-export-menu a')).toHaveCount(3);
  await audit.check('native audit export popover');
  await page.keyboard.press('Escape');
  await expect(page.locator('#audit-export-menu')).not.toBeVisible();
  await expect(exporter).toBeFocused();

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await page.locator('#ts-nav-toggle').click();
  await context.click();
  await expect(contextPanel).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(contextPanel).not.toBeVisible();
  await expect(context).toBeFocused();
  await expect(page.locator('.ts-shell')).toHaveClass(/nav-open/);
  await account.click();
  await page.locator('#ts-notifications-menu').click();
  await expect(notifications).toBeVisible();
  await expect(accountPanel).not.toBeVisible();
  await expect(notifications.locator('.ts-notifications-summary')).toContainText(/unread.*total/);
  await audit.check('notifications from native account popover on phone');
  await page.keyboard.press('Escape');
  await expect(notifications).not.toBeVisible();
  await expect(account).toBeFocused();
  await expect(page.locator('.ts-shell')).toHaveClass(/nav-open/);
  await page.keyboard.press('Escape');
  await expect(page.locator('.ts-shell')).not.toHaveClass(/nav-open/);
  await expect(page.locator('#ts-nav-toggle')).toBeFocused();
});
