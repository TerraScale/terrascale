import { randomBytes } from 'node:crypto';
import type { APIResponse, Page } from '@playwright/test';
import { test, expect } from '../support/audit';
import { signInToken, stepUp } from '../support/auth-management';
import { createDedicatedProjectOwner } from '../support/project-owner';
import { deployFunction, functionAccess, functionSource, functionTool, functionUrl } from '../support/functions';
import { deletionHandoff, mcpTool } from '../support/mcp';
import { confirmWithPassword } from '../support/hosted';
import { journey, saveSecret } from '../support/state';
import { auth, dash, production } from '../support/stack';

const unique = (prefix: string) => `${prefix}-${journey().runId}${Date.now().toString(36)}`.toLowerCase();

test('functions list, detail, invocation logs and deletion work through the page and MCP', async ({ page, audit }) => {
  const current = journey();
  const headers = await functionAccess(page.request);
  const name = unique('page');
  const mcpName = unique('mcp');
  await deployFunction(page.request, headers, name);
  await deployFunction(page.request, headers, mcpName);
  const invoked = await page.request.post(`${functionUrl(name)}/invoke`, { headers, data: {} });
  expect(invoked.status(), 'invoke the real function to produce logs').toBe(200);
  expect(await invoked.json()).toEqual({ ok: true });

  expect((await page.request.post(`${functionUrl(name)}/invoke`, { headers, data: {} })).status()).toBe(200);
  await page.goto(`/databases/${current.databaseId}/functions`);
  const row = page.locator('#functions li').filter({ hasText: name });
  await expect(row).toHaveCount(1);
  await row.getByRole('link', { name: 'Open', exact: true }).click();
  await page.waitForURL((url) => url.searchParams.get('function') === name);
  await expect(page.locator('#function-source')).toHaveValue(functionSource);
  await expect(page.locator('#function-invoke')).toContainText(functionUrl(name));
  await expect(page.locator('#function-logs li')).toHaveCount(2);
  await expect(page.locator('#function-stats .ts-stat').filter({ hasText: 'Invocations' }).locator('dd')).toHaveText('2');
  await expect(page.locator('#function-stats .ts-stat').filter({ hasText: 'p50 latency' }).locator('dd')).toHaveText(/^\d+ ms$/);
  for (const [window, label] of [['1h', 'Last hour'], ['7d', 'Last 7 days'], ['24h', 'Last 24 hours']]) {
    await page.getByRole('link', { name: label, exact: true }).click();
    await page.waitForURL((url) => url.searchParams.get('window') === window);
    await expect(page.locator('#function-stats .ts-stat').filter({ hasText: 'Invocations' }).locator('dd')).toHaveText('2');
  }
  const failedFilter = page.waitForURL(url => url.searchParams.get('function') === name && url.searchParams.get('status') === 'error');
  await page.locator('#function-logs-status').selectOption('error');
  await failedFilter;
  await expect(page.locator('#function-logs-card')).toContainText('No invocations match these filters.');
  const succeededFilter = page.waitForURL(url => url.searchParams.get('function') === name && url.searchParams.get('status') === 'ok');
  await page.locator('#function-logs-status').selectOption('ok');
  await succeededFilter;
  await expect(page.locator('#function-logs li')).toHaveCount(2);
  await page.locator('#function-logs summary').first().click();
  await expect(page.locator('#function-logs')).toContainText('functions journey');
  await audit.check('function detail and real invocation logs');
  await page.getByRole('button', { name: 'Delete function', exact: true }).click();
  const dialog = page.locator('#delete-function');
  await expect(dialog).toBeVisible();
  await audit.check('delete function confirmation');
  await dialog.getByRole('button', { name: 'Delete function', exact: true }).click();
  await page.waitForURL((url) => url.pathname.endsWith('/functions') && !url.searchParams.has('function'));
  await expect(page.locator('#functions li').filter({ hasText: name })).toHaveCount(0);
  await audit.check('function removed from the page');

  await page.goto('/profile?tab=access-tokens');
  await page.locator('#accessTokenName').fill(unique('functions'));
  await page.locator('#profile-access-token-create form').getByRole('button', { name: 'Create token', exact: true }).click();
  await confirmWithPassword(page, audit, current.password, 'functions MCP access');
  const token = await page.locator('#access-token-secret').inputValue();
  saveSecret('functions-mcp-token', token);
  const scope = { databaseId: current.databaseId, organizationId: current.organizationId, projectId: current.projectId };
  for (const [index, credential] of [current.apiKey, token].entries()) {
    const reference = await functionTool(page.request, credential, 'get_functions_reference', {});
    expect(reference.reference).toContain('sql.transaction');
    expect(reference.reference).toContain('get_function_stats');
    expect(reference.reference).toContain('query object maps strings to strings');
    expect(reference.reference).toContain('strict mode');
    expect(reference.reference).toContain('SQLite dialect');
    expect(reference.reference).toContain('ALWAYS a response wrapper');
    const args = { ...scope, name: `${mcpName}-${index}`, source: 'async function handler(event) { console.log("MCP debug", event.body); return { status: 201, body: { echo: event.body, query: event.query } }; }' };
    const deployed = await functionTool(page.request, credential, 'deploy_function', args);
    expect(deployed.name).toBe(args.name);
    expect(deployed.cron).toBeNull();
    expect(Date.parse(deployed.updated_at)).toBeGreaterThan(0);
    expect(new URL(deployed.invoke_url).protocol).toBe('https:');
    const query = { name: 'Terra Scale', encoded: 'a&b=c+%', empty: '', city: 'São Paulo', tag: 'first,second' };
    const invoked = await functionTool(page.request, credential, 'invoke_function', { ...scope, name: args.name, body: { hello: 'agent' }, query });
    expect(invoked.status).toBe(201);
    expect(invoked.content_type).toContain('application/json');
    expect(invoked.body).toEqual({ echo: { hello: 'agent' }, query });
    expect(invoked.log?.status).toBe('ok');
    expect(invoked.log?.duration_ms).toBeGreaterThanOrEqual(0);
    expect(invoked.log?.lines).toContain('MCP debug {"hello":"agent"}');
    const stats = await functionTool(page.request, credential, 'get_function_stats', { ...scope, name: args.name });
    expect(stats.window).toBe('24h');
    expect(stats.invocations).toBeGreaterThanOrEqual(1);
    expect(stats.errors).toBe(0);
    for (const metric of ['p50Ms', 'p95Ms', 'p99Ms', 'maxMs', 'sqlCalls', 'rowsRead', 'rowsWritten'] as const) {
      expect(stats[metric], metric).toBeGreaterThanOrEqual(0);
    }
    expect(stats.buckets).toHaveLength(24);
    expect(stats.buckets.reduce((sum, bucket) => sum + bucket.invocations, 0)).toBe(stats.invocations);
    for (const [window, count] of [['1h', 60], ['7d', 168]] as const) {
      const selected = await functionTool(page.request, credential, 'get_function_stats', { ...scope, name: args.name, window });
      expect(selected.window).toBe(window);
      expect(selected.invocations).toBeGreaterThanOrEqual(1);
      expect(selected.buckets).toHaveLength(count);
    }
    const invalidWindow = await functionTool(page.request, credential, 'get_function_stats', { ...scope, name: args.name, window: '30d' }, true);
    expect(invalidWindow.error).toMatchObject({ code: 'validation', message: 'window must be 1h, 24h or 7d' });
    const inventory = await functionTool(page.request, credential, 'list_functions', scope);
    expect(inventory.functions.map((item) => item.name)).toContain(args.name);
    const saved = await functionTool(page.request, credential, 'get_function', { ...scope, name: args.name });
    expect(saved.function.source).toBe(args.source);
    const debug = await functionTool(page.request, credential, 'list_function_logs', { ...scope, name: args.name });
    expect(debug.logs[0].lines).toContain('MCP debug {"hello":"agent"}');
    const invalid = await functionTool(page.request, credential, 'deploy_function', { ...args, source: 'function (' }, true);
    expect(invalid.error?.message).toContain('JavaScript');
    expect(invalid.error?.line).toBe(1);
    expect(invalid.error?.column).toBe(10);
    await functionTool(page.request, credential, 'deploy_function', { ...args, source: 'function handler() { console.error("debug failure"); throw new Error("MCP failure"); }' });
    const failed = await functionTool(page.request, credential, 'invoke_function', { ...scope, name: args.name });
    expect(failed.status).toBe(500);
    expect(failed.log?.status).toBe('error');
    expect(failed.log?.error).toContain('MCP failure');
    expect(failed.log?.lines).toContain('debug failure');
    await functionTool(page.request, credential, 'deploy_function', { ...args, source: 'function handler(event) { return { status: event.body.status, body: { error: { code: "conflict", message: "taken" }, requested: event.body.status } }; }' });
    for (const status of [403, 404, 409, 500, 503]) {
      const answer = await functionTool(page.request, credential, 'invoke_function', { ...scope, name: args.name, body: { status } });
      expect(answer.status).toBe(status);
      expect(answer.content_type).toContain('application/json');
      expect(answer.body).toEqual({ error: { code: 'conflict', message: 'taken' }, requested: status });
      expect(answer.log?.status).toBe('ok');
    }
    await functionTool(page.request, credential, 'deploy_function', { ...args, source: 'function handler() { return { status: 404, items: [1] }; }' });
    const wrapper = await functionTool(page.request, credential, 'invoke_function', { ...scope, name: args.name });
    expect(wrapper.status).toBe(404);
    expect(wrapper.body).toBe('');
    expect(wrapper.log?.status).toBe('ok');
    await functionTool(page.request, credential, 'deploy_function', { ...args, source: 'function handler() { return { status: 200, body: { id: 1, status: 404 } }; }' });
    const row = await functionTool(page.request, credential, 'invoke_function', { ...scope, name: args.name });
    expect(row.status).toBe(200);
    expect(row.body).toEqual({ id: 1, status: 404 });
    await functionTool(page.request, credential, 'deploy_function', { ...args, source: 'function handler() { undeclared = 1; return {}; }' });
    const strict = await functionTool(page.request, credential, 'invoke_function', { ...scope, name: args.name });
    expect(strict.status).toBe(500);
    expect(strict.log?.error).toContain('undeclared');
    await mcpTool(page.request, credential, 'invoke_function', { ...scope, name: unique('missing') }, 'The requested resource is unavailable.');
  }
  const read = await mcpTool(page.request, current.apiKey, 'create_scoped_key', { ...scope, access: 'read', ttlSeconds: 3600 });
  saveSecret('functions-read-key', read.key);
  await mcpTool(page.request, read.key, 'deploy_function', { ...scope, name: mcpName, source: functionSource }, 'The API key does not grant sql:write.');
  await mcpTool(page.request, read.key, 'list_function_logs', { ...scope, name: mcpName }, 'The API key does not grant sql:write.');
  await mcpTool(page.request, read.key, 'get_function_stats', { ...scope, name: mcpName }, 'The API key does not grant sql:write.');
  await mcpTool(page.request, read.key, 'invoke_function', { ...scope, databaseId: current.projectId, name: mcpName }, 'Database was not found in this project.');
  await mcpTool(page.request, read.key, 'invoke_function', { ...scope, projectId: current.organizationId, name: mcpName }, 'Project is unavailable.');
  const noLog = await functionTool(page.request, read.key, 'invoke_function', { ...scope, name: mcpName });
  expect(noLog.status).toBe(200);
  expect(noLog.body).toEqual({ ok: true });
  expect(noLog.log).toBeNull();
  expect(noLog.log_note).toBe('Log lines need SQL write access; call list_function_logs with a key that has it.');
  const listed = await functionTool(page.request, token, 'list_functions', scope);
  expect(listed.functions.map((item: { name: string }) => item.name)).toContain(mcpName);
  const detail = await functionTool(page.request, token, 'get_function', { ...scope, name: mcpName });
  expect(detail.function.source).toBe(functionSource);
  expect(detail.invokeUrl).toBe(`${functionUrl(mcpName)}/invoke`);
  expect((await page.request.post(`${functionUrl(mcpName)}/invoke`, { headers: await functionAccess(page.request), data: {} })).status()).toBe(200);
  const logs = await functionTool(page.request, token, 'list_function_logs', { ...scope, name: mcpName });
  expect(logs.logs).toHaveLength(2);
  expect(logs.logs[0].lines).toContain('functions journey');
  for (const credential of [current.apiKey, token]) {
    const confirmation = await deletionHandoff(page.request, credential, 'delete_function', mcpName, `/databases/${current.databaseId}/functions`, { ...scope, name: mcpName, confirmName: mcpName });
    expect(confirmation.searchParams.get('function')).toBe(mcpName);
  }
  const remaining = await functionTool(page.request, token, 'list_functions', scope);
  expect(remaining.functions.map((item: { name: string }) => item.name)).toContain(mcpName);
  console.log('Functions owner: page list/read/logs/delete OK; MCP reference/deploy/invoke/debug and read-key boundaries OK; API-key and signed-in deletion require dashboard confirmation (real REST deployment and invocation).');
});

test('failed function deletion preserves the selection and confirmation dialog with an alert', async ({ page, audit }) => {
  const current = journey();
  const headers = await functionAccess(page.request);
  const name = unique('failure');
  await deployFunction(page.request, headers, name);
  await page.goto(`/databases/${current.databaseId}/functions?function=${name}`);
  await page.getByRole('button', { name: 'Delete function', exact: true }).click();
  const dialog = page.locator('#delete-function');
  expect((await page.request.delete(functionUrl(name), { headers })).status()).toBe(204);
  audit.expectStatus(/^https:\/\/dash\.terrascale\.tech\/api\/v1\/dashboard\/functions\/delete(\?|$)/, 404);
  const response = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/v1/dashboard/functions/delete');
  await dialog.getByRole('button', { name: 'Delete function', exact: true }).click();
  expect((await response).status()).toBe(404);
  expect((await response).headers()['content-type']).toContain('text/html');
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('heading')).toHaveText(`Delete ${name}?`);
  await expect(dialog.getByRole('alert')).toContainText('This function no longer exists.');
  await audit.check('a false delete stays in the confirmation dialog');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Database sections' }).locator('a[aria-current="page"]', { hasText: /^Functions$/ })).toBeVisible();
  await audit.check('the functions page remains usable after a stale delete');
  console.log('Functions delete failure: already-deleted function returns HTML 404 with modal alert; Cancel returns to the usable page.');
});

async function setSource(page: Page, source: string, id = '#function-editor'): Promise<void> {
  await page.locator(id).evaluate((editor: HTMLTextAreaElement, text: string) => {
    editor.value = text;
    editor.dispatchEvent(new Event('input', { bubbles: true }));
  }, source);
  await expect(page.locator(id)).toHaveValue(source);
}

async function sqlCompletion(page: Page, id: string, screen: string): Promise<void> {
  const editor = page.locator('ts-code-editor').filter({ has: page.locator(id) });
  await expect(editor).toHaveAttribute('data-ready', '');
  await editor.locator('.monaco-editor').click();
  await page.keyboard.press('ControlOrMeta+End');
  await page.keyboard.press('Enter');
  await page.keyboard.type('sql.');
  await expect(page.locator('.suggest-widget.visible')).toContainText('transaction');
  if (!production) {
    for (const colorScheme of ['light', 'dark'] as const) {
      await page.emulateMedia({ colorScheme });
      await test.info().attach(`function-${screen}-sql-members-${colorScheme}.png`, {
        body: await page.screenshot({ animations: 'disabled', caret: 'hide' }),
        contentType: 'image/png',
      });
    }
  }
  await page.keyboard.press('Escape');
  for (const sql of [
    'sql.query({ sql: "',
    'sql.batch(["',
    'sql.batch([{ sql: "',
    'sql.transaction(async connection => { await connection.query("',
    'sql.transaction(async connection => { await connection.batch([{ sql: "',
  ]) {
    await setSource(page, `const handler = async event => { await ${sql}`, id);
    await editor.locator('.monaco-editor').click();
    await page.keyboard.press('ControlOrMeta+End');
    await page.keyboard.type('SEL');
    await expect(page.locator('.suggest-widget.visible')).toContainText('SELECT');
    await page.keyboard.press('Escape');
  }
}

async function schemaCompletion(page: Page, id: string, table: string): Promise<void> {
  const editor = page.locator('ts-code-editor').filter({ has: page.locator(id) });
  await expect(editor).toHaveAttribute('data-ready', '');
  for (const [sql, expected, delimiter = '`'] of [
    ['SELECT * FROM sch|', table],
    ['SELECT * FROM sch|', table, '"'],
    ['SELECT * FROM sch|', table, "'"],
    [`SELECT * FROM ${table} s JOIN sch|`, table],
    ['INSERT INTO sch|', table],
    ['UPDATE sch|', table],
    [`SELECT sch| FROM ${table}`, 'schema_title'],
    [`SELECT * FROM ${table} WHERE sch|`, 'schema_title'],
    [`UPDATE ${table} SET sch|`, 'schema_title'],
    [`SELECT * FROM ${table} s JOIN ${table} t ON s.sch|`, 'schema_title'],
    [`SELECT s.sch| FROM ${table} AS s`, 'schema_title'],
  ]) {
    const source = `async function handler() { return sql.query(${delimiter}${sql}${delimiter}); }`;
    const offset = source.indexOf('|');
    await setSource(page, source.replace('|', ''), id);
    await editor.evaluate((host: HTMLElement & { editor: { getModel(): { getPositionAt(offset: number): unknown }; setPosition(position: unknown): void; focus(): void } }, offset) => {
      host.editor.setPosition(host.editor.getModel().getPositionAt(offset));
      host.editor.focus();
    }, offset);
    await page.keyboard.press('Control+Space');
    const suggestion = page.locator('.suggest-widget.visible .monaco-list-row').filter({ hasText: expected }).first();
    await expect(suggestion).toBeVisible();
    if (expected === 'schema_title') await expect(suggestion).toContainText('TEXT');
    await suggestion.click();
    const inserted = delimiter === '"' ? `\\"${expected}\\"` : `"${expected}"`;
    await expect(page.locator(id)).toHaveValue(source.replace('sch|', inserted));
    await page.keyboard.press('Escape');
  }
  await setSource(page, 'const text = "sch', id);
  await editor.locator('.monaco-editor').click();
  await page.keyboard.press('ControlOrMeta+End');
  await page.keyboard.press('Control+Space');
  await expect(page.locator('.suggest-widget.visible .monaco-list-row').filter({ hasText: table })).toHaveCount(0);
  await page.keyboard.press('Escape');
}

test('create, deploy, test run, edit and redeploy a function from the editor', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const current = journey();
  const name = unique('editor');
  const table = unique('schema').replaceAll('-', '_');
  const headers = await functionAccess(page.request);
  const sqlUrl = functionUrl(name).split('/functions/')[0] + '/sql/execute';
  const ddl = async (sql: string) => {
    const response = await page.request.post(sqlUrl, { headers, data: { statements: [{ sql }] } });
    expect(response.status()).toBe(200);
  };
  await ddl(`CREATE TABLE ${table} (schema_title TEXT, schema_count INTEGER)`);
  const schemaRequests: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname === '/api/v1/dashboard/data/schema') schemaRequests.push(request.url());
  });
  const created = 'async function handler(event) { console.log("editor journey"); return { echo: event.body }; }';
  const edited = 'async function handler(event) { console.log("editor revised"); return { edited: true }; }';
  const schemaLoaded = page.waitForResponse(response => new URL(response.url()).pathname === '/api/v1/dashboard/data/schema');
  await page.goto(`/databases/${current.databaseId}/functions?edit=new`);
  const catalog = await schemaLoaded;
  expect(catalog.status()).toBe(200);
  expect((await catalog.json()).tables).toContainEqual({ name: table, fields: [{ path: 'schema_title', type: 'TEXT' }, { path: 'schema_count', type: 'INTEGER' }] });
  await expect(page.locator('#function-editor')).toHaveValue(/async function handler\(event\)/);
  await expect(page.locator('#function-editor')).toHaveValue(/sql\.query\("SELECT 1 AS ok"\)/);
  await sqlCompletion(page, '#function-editor', 'create');
  await schemaCompletion(page, '#function-editor', table);
  expect(schemaRequests).toHaveLength(1);
  expect(new URL(schemaRequests[0]).searchParams.get('databaseId')).toBe(current.databaseId);
  await page.locator('#function-name').fill(name);
  await setSource(page, 'function (');
  await page.getByRole('button', { name: 'Deploy function', exact: true }).click();
  await expect(page.locator('#function-deploy-error')).toContainText('JavaScript');
  await expect(page.locator('#function-editor')).toHaveValue('function (');
  await setSource(page, created);
  await page.getByRole('button', { name: 'Deploy function', exact: true }).click();
  await page.waitForURL((url) => url.searchParams.get('function') === name && url.searchParams.get('deployed') === '1');
  await expect(page.locator('#function-notice')).toHaveText('Function deployed.');
  await expect(page.locator('#function-source')).toHaveValue(created);
  await sqlCompletion(page, '#function-source', 'edit');
  await schemaCompletion(page, '#function-source', table);
  await setSource(page, created, '#function-source');
  await expect(page.getByText('HTTPS calls', { exact: true }).first()).toBeVisible();
  await page.getByRole('link', { name: 'Last 7 days', exact: true }).click();
  const detailUrl = page.url();
  await page.locator('#function-event').fill('{"hello":"world"}');
  await page.getByRole('button', { name: 'Run', exact: true }).click();
  await expect(page.locator('#function-result')).toContainText('world');
  await expect(page.locator('#function-stats .ts-stat').filter({ hasText: 'Invocations' }).locator('dd')).toHaveText('1');
  await expect(page.getByRole('link', { name: 'Last 7 days', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(page).toHaveURL(detailUrl);
  await expect(page.locator('#function-source')).toHaveValue(created);
  await page.locator('#function-logs summary').first().click();
  await expect(page.locator('#function-logs')).toContainText('editor journey');
  await page.locator('#function-event').fill('{');
  await page.getByRole('button', { name: 'Run', exact: true }).click();
  await expect(page.locator('#function-event-error')).toHaveText('Event must be JSON.');
  await expect(page).toHaveURL(detailUrl);
  await expect(page.locator('#function-event')).toHaveValue('{');
  await setSource(page, edited, '#function-source');
  await page.getByRole('button', { name: 'Deploy', exact: true }).click();
  await expect(page.locator('#function-notice')).toHaveText('Function deployed.');
  await expect(page).toHaveURL(detailUrl);
  await expect(page.locator('#function-source')).toHaveValue(edited);
  await page.locator('label.ts-chip').filter({ hasText: 'Every 5 min' }).click();
  await page.getByRole('button', { name: 'Save trigger', exact: true }).click();
  await expect(page).toHaveURL(detailUrl);
  await expect(page.getByText('Cron */5 * * * * UTC').first()).toBeVisible();
  await expect(page.getByRole('radio', { name: 'Every 5 min', exact: true })).toBeChecked();
  await ddl(`DROP TABLE ${table}`);
  console.log('Functions editor: invalid JavaScript stays in the editor, deploy, in-place run, invalid JSON, in-place redeploy and trigger save.');
});

test('a handler status is the test-run result', async ({ page }) => {
  const current = journey();
  const name = unique('status');
  const source = 'async function handler() { return { status: 404, body: { missing: true } }; }';
  await page.goto(`/databases/${current.databaseId}/functions?edit=new`);
  await page.locator('#function-name').fill(name);
  await setSource(page, source);
  await page.getByRole('button', { name: 'Deploy function', exact: true }).click();
  await page.waitForURL((url) => url.searchParams.get('function') === name && url.searchParams.get('deployed') === '1');
  await page.getByRole('button', { name: 'Run', exact: true }).click();
  const result = page.locator('#function-result');
  await expect(result).toContainText('404');
  await expect(result).toContainText('application/json');
  await expect(result).toContainText(/\{\s*"missing"\s*:\s*true\s*\}/);
  await expect(page.locator('#function-event-error')).toHaveCount(0);
  console.log('Functions test run: a handler 404 and its JSON body are the result.');
});

test('function query parameters match HTTPS in the dashboard test run', async ({ page, request, audit }) => {
  const name = unique('query');
  const headers = await functionAccess(request);
  const source = 'async function handler(event) { return event.query; }';
  expect((await request.put(functionUrl(name), { headers, data: { source } })).status()).toBe(200);
  const query = 'name=Terra+Scale&tag=first&tag=second&encoded=a%26b%3Dc%2B%25&empty=&city=S%C3%A3o+Paulo';
  const expected = { name: 'Terra Scale', tag: 'first,second', encoded: 'a&b=c+%', empty: '', city: 'São Paulo' };
  const direct = await request.post(`${functionUrl(name)}/invoke?${query}`, { headers, data: {} });
  expect(direct.status()).toBe(200);
  expect(await direct.json()).toEqual(expected);
  await page.goto(`/databases/${journey().databaseId}/functions?function=${name}`);
  await page.getByLabel('Query parameters', { exact: true }).fill(query);
  await page.getByLabel('Event JSON', { exact: true }).fill('{');
  await page.getByRole('button', { name: 'Run', exact: true }).click();
  await expect(page.locator('#function-event-error')).toHaveText('Event must be JSON.');
  await expect(page.getByLabel('Query parameters', { exact: true })).toHaveValue(query);
  await page.getByLabel('Event JSON', { exact: true }).fill('{}');
  await page.getByRole('button', { name: 'Run', exact: true }).click();
  const result = page.locator('#function-result');
  await expect(result).toContainText('Terra Scale');
  expect(JSON.parse((await result.innerText()).split('\n').slice(2).join('\n'))).toEqual(expected);
  await expect(page.getByLabel('Query parameters', { exact: true })).toHaveValue(query);
  await audit.check('function test run returns decoded and repeated query parameters');
});

test('function metrics include SQL calls and zero-filled latency buckets', async ({ request, page, audit }) => {
  const headers = await functionAccess(request);
  const name = unique('metrics');
  const url = functionUrl(name);
  const table = name.replaceAll('-', '_');
  const source = `async function handler() {
    await sql.execute('CREATE TABLE "${table}" (value INTEGER NOT NULL)');
    await sql.execute('INSERT INTO "${table}" VALUES (1)');
    await sql.execute('UPDATE "${table}" SET value = 2');
    const rows = await sql.query('SELECT value FROM "${table}"');
    await sql.execute('DROP TABLE "${table}"');
    return { status: 201, body: { ok: rows[0].value === 2 } };
  }`;
  expect((await request.put(url, { headers, data: { source } })).status()).toBe(200);
  const empty = await request.get(`${url}/stats`, { headers });
  expect(empty.status()).toBe(200);
  expect(await empty.json()).toMatchObject({ window: '24h', invocations: 0, p50Ms: 0 });
  const dashboard = `/databases/${journey().databaseId}/functions?function=${name}`;
  await page.goto(dashboard);
  await expect(page.locator('#function-logs-card')).toContainText('No invocations in this window yet. Run it from the Test tab or call its HTTPS URL.');
  await expect(page.locator('#function-stats .ts-stat').filter({ hasText: 'Error rate' }).locator('dd')).toHaveText('—');
  await audit.check('empty invocation metrics');
  for (let i = 0; i < 2; i++) {
    const invoked = await request.post(`${url}/invoke`, { headers, data: {} });
    expect(invoked.status()).toBe(201);
    expect(await invoked.json()).toEqual({ ok: true });
  }
  for (const [window, count] of [['1h', 60], ['24h', 24], ['7d', 168]] as const) {
    const response = await request.get(`${url}/stats?window=${window}`, { headers });
    expect(response.status()).toBe(200);
    const stats = await response.json();
    expect(stats.window).toBe(window);
    expect(stats.invocations).toBeGreaterThanOrEqual(2);
    expect(stats.p50Ms).toBeGreaterThan(0);
    expect(stats.p95Ms).toBeGreaterThanOrEqual(stats.p50Ms);
    expect(stats.p99Ms).toBeGreaterThanOrEqual(stats.p95Ms);
    expect(stats.sqlCalls).toBe(10);
    expect(stats.rowsRead).toBe(2);
    expect(stats.rowsWritten).toBeGreaterThanOrEqual(4);
    expect(stats.buckets).toHaveLength(count);
    expect(stats.buckets.reduce((sum: number, bucket: { invocations: number }) => sum + bucket.invocations, 0)).toBe(stats.invocations);
    expect(stats.buckets.map((bucket: { at: string }) => bucket.at)).toEqual(stats.buckets.map((bucket: { at: string }) => bucket.at).sort());
    expect(stats.buckets.filter((bucket: { invocations: number }) => bucket.invocations === 0).every((bucket: { p50Ms: number; p95Ms: number }) => bucket.p50Ms === 0 && bucket.p95Ms === 0)).toBe(true);
  }
  const logsResponse = await request.get(`${url}/logs`, { headers });
  expect(logsResponse.status()).toBe(200);
  const logs = await logsResponse.json();
  expect(logs).toHaveLength(2);
  expect(logs[0]).toMatchObject({ sqlCalls: 5, rowsRead: 1, responseBytes: 11, httpStatus: 201 });
  expect(logs[0].rowsWritten).toBeGreaterThanOrEqual(2);
  expect(logs[0].sqlMs).toBeGreaterThan(0);
  expect(logs[0].sqlMs).toBeLessThanOrEqual(logs[0].durationMs);
  const failedSource = 'async function handler() { await sql.query("SELECT 1 AS ok"); throw new Error("metrics failure"); }';
  expect((await request.put(url, { headers, data: { source: failedSource } })).status()).toBe(200);
  const failed = await request.post(`${url}/invoke`, { headers, data: {} });
  expect(failed.status()).toBe(500);
  expect((await failed.json()).error.code).toBe('function_error');
  const failedLogs = await request.get(`${url}/logs?limit=1`, { headers });
  expect(failedLogs.status()).toBe(200);
  expect((await failedLogs.json())[0]).toMatchObject({ status: 'error', sqlCalls: 1, rowsRead: 1, rowsWritten: 0, httpStatus: null });
  const failedStats = await request.get(`${url}/stats`, { headers });
  expect(failedStats.status()).toBe(200);
  expect(await failedStats.json()).toMatchObject({ invocations: 3, errors: 1, sqlCalls: 11, rowsRead: 3 });
  await page.goto(`${dashboard}&window=1h&status=error&trigger=http`);
  await expect(page.locator('#function-logs li')).toHaveCount(1);
  await page.locator('#function-logs summary').click();
  await expect(page.locator('#function-logs details[open]')).toContainText('metrics failure');
  await expect(page.locator('#function-logs details[open]')).toContainText('SQL calls');
  await expect(page.locator('#function-stats .ts-stat').filter({ hasText: 'Invocations' }).locator('dd')).toHaveText('3');
  await expect(page.locator('#function-stats .ts-stat').filter({ hasText: 'Error rate' }).locator('dd')).toHaveText('33.3%');
  await audit.check('expanded failed invocation and window metrics');
  for (const action of ['Deploy', 'Save trigger']) {
    const deployed = page.waitForResponse((response) => new URL(response.url()).pathname === '/api/v1/dashboard/functions/deploy' && response.request().method() === 'POST');
    await page.getByRole('button', { name: action, exact: true }).click();
    expect((await deployed).status()).toBe(200);
    await expect(page.locator('#function-notice')).toHaveText('Function deployed.');
    await expect(page.locator('#function-trigger-form input[name="status"]')).toHaveValue('error');
    await expect(page.locator('#function-trigger-form input[type="hidden"][name="trigger"]')).toHaveValue('http');
    await page.getByRole('link', { name: 'Last 7 days', exact: true }).click();
    expect(new URL(page.url()).searchParams.get('window')).toBe('7d');
    expect(new URL(page.url()).searchParams.get('status')).toBe('error');
    expect(new URL(page.url()).searchParams.get('trigger')).toBe('http');
    await expect(page.locator('#function-logs-status')).toHaveValue('error');
    await expect(page.locator('#function-logs-trigger')).toHaveValue('http');
    await expect(page.locator('#function-logs li')).toHaveCount(1);
    await expect(page.locator('#function-logs summary')).toContainText('Failed');
    await audit.check(`${action} retains invocation filters when switching windows`);
  }
  const validationHeaders = await functionAccess(request);
  const invalid = await request.get(`${url}/stats?window=2h`, { headers: validationHeaders });
  expect(invalid.status()).toBe(400);
  expect((await invalid.json()).error.code).toBe('invalid_request');
  expect((await request.delete(url, { headers: validationHeaders })).status()).toBe(204);
  expect((await request.get(`${url}/stats`, { headers: validationHeaders })).status()).toBe(404);
});

test('function secrets are write-only, reach the next invocation and go away with delete', async ({ request, page, audit }) => {
  const current = journey();
  const headers = await functionAccess(request);
  const name = unique('secrets');
  const url = functionUrl(name);
  const value = `secret-${current.runId}-${Date.now().toString(36)}`;
  const panelValue = `panel-${current.runId}-${Date.now().toString(36)}`;
  saveSecret('function-secret-value', value);
  saveSecret('function-secret-panel-value', panelValue);
  const checksum = (text: string) => [...text].reduce((sum, char) => (sum * 31 + char.charCodeAt(0)) % 1000000007, 0);
  const source = `async function handler(event) {
    const token = event.secrets.API_TOKEN;
    let checksum = 0;
    for (const char of token ?? "") checksum = (checksum * 31 + char.charCodeAt(0)) % 1000000007;
    let refused = false;
    try { event.secrets.API_TOKEN = "changed"; } catch (error) { refused = true; }
    return { present: token !== undefined, length: (token ?? "").length, checksum, frozen: Object.isFrozen(event.secrets), refused, keys: Object.keys(event), names: Object.keys(event.secrets) };
  }`;
  expect((await request.put(url, { headers, data: { source } })).status()).toBe(200);
  const invoke = async () => {
    const response = await request.post(`${url}/invoke`, { headers, data: {} });
    expect(response.status()).toBe(200);
    const text = await response.text();
    expect(text).not.toContain(value);
    return JSON.parse(text);
  };
  expect(await invoke()).toMatchObject({ present: false, length: 0, names: [] });

  const set = await request.put(`${url}/secrets/API_TOKEN`, { headers, data: { value } });
  expect(set.status(), 'set a function secret through TerraBase REST').toBe(200);
  const setText = await set.text();
  expect(setText).not.toContain(value);
  expect(Object.keys(JSON.parse(setText)).sort()).toEqual(['name', 'updatedAt']);
  expect(await invoke(), 'the next invocation sees the secret without a redeploy').toEqual({
    present: true, length: value.length, checksum: checksum(value), frozen: true, refused: true, keys: ['trigger', 'body', 'query'], names: ['API_TOKEN'],
  });

  const listed = await request.get(`${url}/secrets`, { headers });
  expect(listed.status()).toBe(200);
  const listText = await listed.text();
  expect(listText).not.toContain(value);
  expect(JSON.parse(listText)).toEqual([{ name: 'API_TOKEN', updatedAt: expect.any(String) }]);
  const badName = await request.put(`${url}/secrets/api_token`, { headers, data: { value } });
  expect(badName.status()).toBe(400);
  expect(await badName.text()).not.toContain(value);
  const tooLong = await request.put(`${url}/secrets/API_TOKEN`, { headers, data: { value: 'x'.repeat(4097) } });
  expect(tooLong.status()).toBe(400);
  expect((await tooLong.json()).error.message).toBe('value is required and at most 4 KiB');
  const logs = await request.get(`${url}/logs`, { headers });
  expect(logs.status()).toBe(200);
  expect(await logs.text()).not.toContain(value);

  const scope = { databaseId: current.databaseId, organizationId: current.organizationId, projectId: current.projectId };
  const mcpSet = await functionTool(page.request, current.apiKey, 'set_function_secret', { ...scope, name, secret: 'MCP_TOKEN', value: panelValue });
  expect(mcpSet).toEqual({ name: 'MCP_TOKEN', updated_at: expect.any(String) });
  const mcpList = await functionTool(page.request, current.apiKey, 'list_function_secrets', { ...scope, name });
  expect(mcpList.secrets.map((secret) => secret.name)).toEqual(['API_TOKEN', 'MCP_TOKEN']);
  expect(JSON.stringify(mcpList)).not.toContain(value);
  expect(await functionTool(page.request, current.apiKey, 'delete_function_secret', { ...scope, name, secret: 'MCP_TOKEN' })).toEqual({ name: 'MCP_TOKEN', deleted: true });

  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto(`/databases/${current.databaseId}/functions?function=${name}`);
  await expect(page.locator('#function-secret-list li')).toHaveCount(1);
  await expect(page.locator('#function-secret-list')).toContainText('API_TOKEN');
  expect(await page.content()).not.toContain(value);
  await audit.check('function secrets panel lists names without values');
  await page.locator('#function-secret-name').fill('PANEL_TOKEN');
  await page.locator('#function-secret-value').fill(panelValue);
  await page.getByRole('button', { name: 'Save secret', exact: true }).click();
  await expect(page.locator('#function-notice')).toHaveText('Secret saved.');
  await expect(page.locator('#function-secret-list li')).toHaveCount(2);
  await expect(page.locator('#function-secret-value')).toHaveValue('');
  expect(await page.content()).not.toContain(panelValue);
  await page.getByRole('button', { name: 'Delete PANEL_TOKEN', exact: true }).click();
  await expect(page.locator('#function-notice')).toHaveText('Secret deleted.');
  await expect(page.locator('#function-secret-list li')).toHaveCount(1);
  await audit.check('function secret saved and deleted from the panel');

  expect((await request.delete(`${url}/secrets/API_TOKEN`, { headers })).status()).toBe(204);
  expect((await request.delete(`${url}/secrets/API_TOKEN`, { headers })).status()).toBe(404);
  expect(await invoke(), 'a deleted secret is undefined on the next invocation').toMatchObject({ present: false, length: 0, names: [] });
  expect((await request.put(`${url}/secrets/API_TOKEN`, { headers, data: { value } })).status()).toBe(200);
  expect((await request.delete(url, { headers })).status()).toBe(204);
  expect((await request.put(url, { headers, data: { source } })).status()).toBe(200);
  expect(await (await request.get(`${url}/secrets`, { headers })).json(), 'deleting the function deleted its secrets').toEqual([]);
  expect((await request.delete(url, { headers })).status()).toBe(204);
  console.log('Function secrets: REST set, invoke sees length and checksum only, list without values, MCP set/list/delete, panel save/delete, delete and function delete.');
});

test('guest and end-user tokens get 403 on every function route while developer credentials keep working', async ({ browser, request }, info) => {
  const account = await createDedicatedProjectOwner(browser, info, 'Functions access owner');
  const { current, page } = account;
  const project = current.projectId;
  const exchanged = await request.post(auth + '/terrascale/oauth/token', {
    form: { grant_type: 'client_credentials', client_id: current.apiKey.slice(0, 26), client_secret: current.apiKey, project,
      scope: 'sql:read sql:write databases:read databases:write' },
  });
  expect(exchanged.status()).toBe(200);
  const developerToken = (await exchanged.json()).access_token as string;
  saveSecret('functions-access-developer-token', developerToken);
  const developer = { Authorization: 'Bearer ' + developerToken };
  const created = await request.post(`https://api.terrascale.tech/v1/projects/${project}/databases`,
    { headers: developer, data: { name: 'functions-access-' + randomBytes(4).toString('hex') } });
  expect(created.status(), await created.text()).toBe(201);
  const databaseId = (await created.json()).id as string;
  const functions = `https://api.terrascale.tech/v1/projects/${project}/databases/${databaseId}/functions`;
  const url = `${functions}/${unique('access')}`;
  const padding = 'x'.repeat(100_000);
  const paddedSource = `const padding = "${padding}"; async function handler(event) { return { ok: padding.length === ${padding.length} }; }`;
  const deployed = await request.put(url, { headers: developer, data: { source: paddedSource } });
  expect(deployed.status(), 'a developer deploys a source larger than the 64 KiB pre-auth body limit').toBe(200);

  const ownerToken = await signInToken(page.request, 'functions-access-owner');
  const clients = await page.request.post(auth + '/v1/' + project + '/clients', {
    headers: { Authorization: 'Bearer ' + ownerToken, 'X-TerraScale-Step-Up-Proof': await stepUp(page.request, ownerToken, 'create_oauth_client', current.password) },
    data: { name: 'Functions access journey', type: 'public', redirect_uris: [dash + '/auth/functions-access'], allow_anonymous: true },
  });
  expect(clients.status(), await clients.text()).toBe(201);
  const guestIssued = await request.post(auth + '/' + project + '/oauth/anonymous', { form: { client_id: (await clients.json()).client_id as string } });
  expect(guestIssued.status(), await guestIssued.text()).toBe(200);
  const guestToken = (await guestIssued.json()).access_token as string;
  saveSecret('functions-access-guest-token', guestToken);
  const guestUser = JSON.parse(Buffer.from(guestToken.split('.')[1], 'base64url').toString()).sub as string;

  const mintKeyCreated = await page.request.post(auth + '/v1/terrascale/organizations/' + current.organizationId + '/projects/' + project + '/api-keys', {
    headers: { Authorization: 'Bearer ' + ownerToken }, data: { name: 'Functions access mint', scopes: ['auth:tokens:mint'] },
  });
  expect(mintKeyCreated.status(), await mintKeyCreated.text()).toBe(201);
  const mintKey = (await mintKeyCreated.json()).key as string;
  saveSecret('functions-access-mint-key', mintKey);
  const minted = await request.post(auth + '/v1/' + project + '/custom-tokens', {
    headers: { Authorization: 'Bearer ' + mintKey }, data: { user_id: guestUser, database: databaseId, claims: { plan: 'journey' } },
  });
  expect(minted.status(), await minted.text()).toBe(200);
  const endUserToken = (await minted.json()).access_token as string;
  saveSecret('functions-access-end-user-token', endUserToken);

  const routes = (headers: Record<string, string>): [string, string, () => Promise<APIResponse>, number][] => [
    ['PUT', '{function}', () => request.put(url, { headers, data: { source: functionSource } }), 200],
    ['GET', '{function}', () => request.get(url, { headers }), 200],
    ['GET', '', () => request.get(functions, { headers }), 200],
    ['GET', '{function}/logs', () => request.get(url + '/logs', { headers }), 200],
    ['GET', '{function}/stats', () => request.get(url + '/stats', { headers }), 200],
    ['POST', '{function}/invoke', () => request.post(url + '/invoke', { headers, data: {} }), 200],
    ['PUT', '{function}/secrets/API_TOKEN', () => request.put(url + '/secrets/API_TOKEN', { headers, data: { value: 'journey-secret' } }), 200],
    ['GET', '{function}/secrets', () => request.get(url + '/secrets', { headers }), 200],
    ['DELETE', '{function}/secrets/API_TOKEN', () => request.delete(url + '/secrets/API_TOKEN', { headers }), 204],
    ['DELETE', '{function}', () => request.delete(url, { headers }), 204],
  ];
  for (const [label, token] of [['guest', guestToken], ['end user', endUserToken]] as const) {
    for (const [method, path, send] of routes({ Authorization: 'Bearer ' + token })) {
      const refused = await send();
      const text = await refused.text();
      console.log(`${label} ${method} …/functions/${path} -> ${refused.status()} ${text}`);
      expect(refused.status(), `${label} ${method} ${path}: ${text}`).toBe(403);
      expect(JSON.parse(text).error.code).toBe('developer_credential_required');
    }
  }

  for (const [method, path, send, status] of routes(developer)) {
    const answered = await send();
    console.log(`developer ${method} …/functions/${path} -> ${answered.status()}`);
    expect(answered.status(), `developer ${method} ${path}: ${await answered.text()}`).toBe(status);
  }
});
