import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { chromium } from 'playwright-core';

const base = process.argv[2];
assert(base, 'Pass the running test-only C# demo host URL');
const browser = await chromium.launch({ headless: true });
const output = resolve(new URL('../../test-results/dotnet/journey', import.meta.url).pathname);
await mkdir(output, { recursive: true });
let activePage;
try {
  for (const [size, viewport] of [['desktop', { width: 1440, height: 900 }], ['mobile', { width: 390, height: 844 }]]) {
    const context = await browser.newContext({ viewport });
    const page = await context.newPage();
    activePage = page;
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const name = `qa-${size}-${Date.now().toString(36)}`;
    async function goto(path, status = 200) {
      const response = await page.goto(base + path);
      assert.equal(response.status(), status, path);
      await page.evaluate(() => document.fonts.ready);
    }
    async function submit(form, button) {
      await Promise.all([page.waitForNavigation(), page.locator(form).getByRole('button', { name: button, exact: true }).click()]);
    }
    async function editor(id, value) {
      const textarea = page.locator(id);
      await textarea.evaluate((el, text) => { el.value = text; el.dispatchEvent(new Event('input', { bubbles: true })); }, value);
      assert.equal(await textarea.inputValue(), value);
    }
    async function shot(key) { await page.screenshot({ path: resolve(output, `${key}-${size}.png`), fullPage: true, caret: 'hide' }); }
    await goto('/login');
    await page.locator('#email').fill('user@example.com');
    await page.locator('#password').fill('test-password');
    await submit('form[action="/login"]', 'Sign in');
    await goto('/');
    const personalOrg = (await (await context.request.get(base + '/api/v1/auth/me')).json()).organizationId;
    await shot('home');
    await goto('/profile');
    await page.locator('#displayName').fill('QA Test User');
    await submit('form[action="/api/v1/dashboard/profile"]', 'Save profile');
    assert.equal(await page.locator('#displayName').inputValue(), 'QA Test User');
    await page.locator('#displayName').fill('Test User');
    await submit('form[action="/api/v1/dashboard/profile"]', 'Save profile');

    await goto('/databases');
    await page.getByRole('button', { name: 'New database', exact: true }).first().click();
    await page.locator('#create-database[open]').waitFor();
    for (const invalid of ['Orders', '-orders', 'orders-']) {
      await page.locator('#database-name').fill(invalid);
      assert.equal(await page.locator('#database-name').evaluate(el => el.validity.patternMismatch), true, 'Native database names reject uppercase and edge hyphens');
    }
    await page.locator('#database-name').fill(name);
    assert.equal(await page.locator('#database-name').evaluate(el => el.checkValidity()), true, 'Native database names accept inner hyphens');
    const map = page.locator('#create-database [data-select-id="database-region"]');
    const sfo = page.locator('#database-region input[value="SFO"]');
    const sfoInitially = await sfo.isChecked();
    await map.getByRole('button', { name: 'San Francisco (SFO)', exact: true }).click();
    assert.equal(await sfo.isChecked(), !sfoInitially, 'Map picks change the canonical native region checkbox');
    await page.waitForFunction(expected => document.querySelector('#create-database button[data-region="SFO"]').getAttribute('aria-pressed') === String(expected), !sfoInitially);
    await sfo.setChecked(sfoInitially);
    await page.waitForFunction(expected => document.querySelector('#create-database button[data-region="SFO"]').getAttribute('aria-pressed') === String(expected), sfoInitially);
    await page.locator('#create-database input[name="region"][value="GRU"]').check();
    await submit('#create-database form', 'Create database');
    const databaseId = new URL(page.url()).pathname.split('/')[2];
    assert(databaseId, 'Created database opens its details');
    await shot('database');
    await goto(`/databases/${databaseId}/documents`);
    await page.getByRole('button', { name: 'New collection', exact: true }).last().click();
    await page.locator('#new-collection[open]').waitFor();
    await page.locator('#new-collection-name').fill('users');
    await submit('#new-collection form', 'Create collection');
    await page.getByRole('button', { name: 'Add document', exact: true }).click();
    await page.locator('#add-document[open]').waitFor();
    await page.locator('#new-document-id').fill('customer-1');
    await editor('#new-document-json', '{"name":"Ada","score":7,"nested":{"keep":true,"remove":false}}');
    await submit('#add-document form', 'Add document');
    await page.getByRole('link', { name: 'customer-1', exact: true }).click();
    await page.locator('#edit-document[open]').waitFor();
    await page.locator('#operation').selectOption('patch');
    await editor('#document-json', '{"score":9,"nested":{"remove":null}}');
    await submit('#edit-document form', 'Save document');
    await page.getByRole('link', { name: 'customer-1', exact: true }).click();
    await page.locator('#edit-document[open]').waitFor();
    const patched = JSON.parse(await page.locator('#document-json').inputValue());
    assert.deepEqual(patched, { name: 'Ada', score: 9, nested: { keep: true } }, 'Patch preserves unrelated fields and removes null members');
    await page.locator('#edit-document').getByRole('button', { name: 'Cancel', exact: true }).click();
    await shot('documents');
    await goto(`/databases/${databaseId}/schema?collection=users`);
    assert((await page.locator('#schema-json').inputValue()).includes('score'), 'Schema includes live fields');
    await goto(`/databases/${databaseId}/sql?collection=users`);
    await editor('#sql', 'SELECT * FROM users LIMIT 100');
    await submit('form[action="/api/v1/dashboard/data/sql"]', 'Run query');
    await page.getByText('Ada', { exact: true }).waitFor();
    await shot('sql');

    await goto('/api-keys?kind=key&create=key');
    await page.locator('#create-key[open]').waitFor();
    await page.locator('#key-name').fill(name);
    await page.locator('#key-database').selectOption(databaseId);
    await page.locator('#key-access-write').check();
    await submit('#create-key form', 'Create key');
    const keySecret = page.locator('.ts-apikeys-secret-value');
    await keySecret.waitFor();
    const issuedKey = await keySecret.innerText();
    assert.match(issuedKey, /^tb_[A-Za-z0-9_-]+$/, 'The producer issues a usable native key');
    await shot('key-created');
    await goto('/api-keys?kind=key');
    assert(!(await page.locator('main').innerText()).includes(issuedKey), 'API-key plaintext disappears on refresh');
    const keyRow = page.locator('#credentials tbody tr').filter({ hasText: name });
    await keyRow.getByRole('button', { name: 'Revoke', exact: true }).click();
    const dialog = page.locator('dialog[open]');
    await dialog.getByRole('button', { name: 'Revoke credential', exact: true }).click();
    await page.waitForURL(/\/api-keys/);
    assert.equal(await page.locator('#credentials tbody tr').filter({ hasText: name }).count(), 0);

    await goto('/profile?tab=access-tokens');
    await page.locator('#accessTokenName').fill(name);
    await page.locator('#accessTokenPassword').fill('test-password');
    await submit('#profile-access-token-create form', 'Create token');
    const secret = await page.locator('#access-token-secret').inputValue();
    assert.match(secret, /^tspat_[A-Za-z0-9_-]{43}$/);
    const mcp = await context.request.post(base + '/mcp', { headers: { Authorization: 'Bearer ' + secret, Accept: 'application/json, text/event-stream' }, data: { jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'list_databases', arguments: {} } } });
    assert.equal(mcp.status(), 200);
    assert((await mcp.text()).includes(name), 'MCP observes the database created through native UI');
    await goto('/profile?tab=access-tokens');
    assert.equal(await page.locator('#access-token-secret').count(), 0);
    const tokenRow = page.locator('#profile-access-tokens tbody tr').filter({ hasText: name });
    await tokenRow.getByRole('button', { name: 'Revoke', exact: true }).click();
    await page.locator('dialog[open]').getByRole('button', { name: 'Revoke token', exact: true }).click();
    await page.waitForURL(/\/profile\?tab=access-tokens/);
    const revoked = await context.request.post(base + '/mcp', { headers: { Authorization: 'Bearer ' + secret, Accept: 'application/json, text/event-stream' }, data: { jsonrpc: '2.0', id: 2, method: 'tools/list', params: {} } });
    assert.equal(revoked.status(), 401);

    await goto('/organization?tab=billing');
    await shot('billing');
    await goto(`/databases/${databaseId}?delete=1`);
    await page.locator('dialog[open] input[name="confirmName"]').fill(name);
    await submit('dialog[open] form', 'Delete database');
    await goto('/databases');
    assert.equal(await page.getByRole('link', { name, exact: true }).count(), 0);
    if (size === 'desktop') {
      await goto('/organization?newOrganization=organization');
      await page.locator('#create-organization[open]').waitFor();
      await page.locator('#create-organization input[name="name"]').fill(name + ' company');
      await submit('#create-organization form', 'Create organization');
      await page.getByRole('heading', { name: 'Set up billing', exact: true }).waitFor();
      await submit('#create-organization form', 'Continue to checkout');
      await page.getByRole('heading', { name: 'Create the first project', exact: true }).waitFor();
      await page.locator('#create-organization input[name="name"]').fill('Production');
      await page.locator('#create-organization select[name="regions"]').selectOption(['GRU', 'SFO']);
      await page.locator('#create-organization select[name="region"]').selectOption('GRU');
      await page.locator('#create-organization select[name="backupRegion"]').selectOption('SFO');
      await submit('#create-organization form', 'Create project');
      await goto('/organization?tab=billing');
      await page.getByText('Starter', { exact: true }).first().waitFor();
      assert((await page.locator('#billing-summary').innerText()).includes('On file'), 'Stripe confirmation activates the bound organization');
      assert.equal(await page.locator('form[action="/billing/checkout"]').count(), 0, 'Active paid subscriptions use the customer portal');
      await shot('paid-billing');
      await page.getByRole('link', { name: 'Manage payment', exact: true }).click();
      await page.getByRole('heading', { name: 'Test-only Stripe customer portal' }).waitFor();
      await goto('/organization');
      const csrf = await page.locator('input[name="_csrf"]').first().inputValue();
      const switched = await context.request.post(base + '/api/v1/dashboard/switch-organization', { form: { _csrf: csrf, organizationId: personalOrg, returnUrl: '/' }, maxRedirects: 0 });
      assert.equal(switched.status(), 303, 'Organization selection restores the personal scope');
    }
    assert.deepEqual(errors, [], `${size}: no browser runtime errors`);
    await context.close();
  }
  console.log('PASS: desktop/mobile profile, database CRUD, collections, versioned JSON merge patches, schema/SQL, keys/PATs, native/MCP state and revocation, paid organization checkout/project wizard and customer portal');
} catch (error) {
  if (activePage && !activePage.isClosed()) {
    await activePage.screenshot({ path: resolve(output, 'failure.png'), fullPage: true });
    await writeFile(resolve(output, 'failure.html'), await activePage.content());
    console.error('Browser failed at', activePage.url());
    console.error((await activePage.locator('body').innerText()).slice(-2000));
  }
  throw error;
} finally { await browser.close(); }
