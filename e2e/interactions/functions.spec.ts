import type { Page } from '@playwright/test';
import { test, expect } from '../support/audit';
import { deployFunction, functionAccess, functionSource, functionTool, functionUrl } from '../support/functions';
import { deletionHandoff } from '../support/mcp';
import { confirmWithPassword } from '../support/hosted';
import { journey, saveSecret } from '../support/state';

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

  await page.goto(`/databases/${current.databaseId}/functions`);
  const row = page.locator('#functions li').filter({ hasText: name });
  await expect(row).toHaveCount(1);
  await row.getByRole('link', { name: 'Open', exact: true }).click();
  await expect(page.locator('#function-source')).toHaveValue(functionSource);
  await expect(page.locator('#function-invoke')).toContainText(functionUrl(name));
  await expect(page.locator('#function-logs li')).toHaveCount(1);
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
  const listed = await functionTool(page.request, token, 'list_functions', scope);
  expect(listed.functions.map((item: { name: string }) => item.name)).toContain(mcpName);
  const detail = await functionTool(page.request, token, 'get_function', { ...scope, name: mcpName });
  expect(detail.function.source).toBe(functionSource);
  expect(detail.invokeUrl).toBe(`${functionUrl(mcpName)}/invoke`);
  expect((await page.request.post(`${functionUrl(mcpName)}/invoke`, { headers, data: {} })).status()).toBe(200);
  const logs = await functionTool(page.request, token, 'list_function_logs', { ...scope, name: mcpName });
  expect(logs.logs).toHaveLength(1);
  expect(logs.logs[0].lines).toContain('functions journey');
  for (const credential of [current.apiKey, token]) {
    const confirmation = await deletionHandoff(page.request, credential, 'delete_function', mcpName, `/databases/${current.databaseId}/functions`, { ...scope, name: mcpName, confirmName: mcpName });
    expect(confirmation.searchParams.get('function')).toBe(mcpName);
  }
  const remaining = await functionTool(page.request, token, 'list_functions', scope);
  expect(remaining.functions.map((item: { name: string }) => item.name)).toContain(mcpName);
  console.log('Functions owner: page list/read/logs/delete OK; MCP list/read/logs OK; API-key and signed-in deletion require dashboard confirmation (real REST deployment and invocation).');
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
  await expect(page.getByRole('heading', { name: 'Functions', exact: true, level: 1 })).toBeVisible();
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

test('create, deploy, test run, edit and redeploy a function from the editor', async ({ page }) => {
  const current = journey();
  const name = unique('editor');
  const created = 'async function handler(event) { console.log("editor journey"); return { echo: event.body }; }';
  const edited = 'async function handler(event) { console.log("editor revised"); return { edited: true }; }';
  await page.goto(`/databases/${current.databaseId}/functions?edit=new`);
  await expect(page.locator('#function-editor')).toHaveValue(/async function handler\(event\)/);
  await expect(page.locator('#function-editor')).toHaveValue(/sql\.query\("SELECT 1 AS ok"\)/);
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
  await expect(page.getByText('HTTPS calls', { exact: true }).first()).toBeVisible();
  const detailUrl = page.url();
  await page.locator('#function-event').fill('{"hello":"world"}');
  await page.getByRole('button', { name: 'Run', exact: true }).click();
  await expect(page.locator('#function-result')).toContainText('world');
  await expect(page).toHaveURL(detailUrl);
  await expect(page.locator('#function-source')).toHaveValue(created);
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

test('function metrics include SQL calls and zero-filled latency buckets', async ({ request }) => {
  const headers = await functionAccess(request);
  const name = unique('metrics');
  const url = functionUrl(name);
  const source = 'async function handler() { await sql.query("SELECT 1 AS ok"); return { status: 201, body: { ok: true } }; }';
  expect((await request.put(url, { headers, data: { source } })).status()).toBe(200);
  const empty = await request.get(`${url}/stats`, { headers });
  expect(empty.status()).toBe(200);
  expect(await empty.json()).toMatchObject({ window: '24h', invocations: 0, p50Ms: 0 });
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
    expect(stats.sqlCalls).toBe(2);
    expect(stats.buckets).toHaveLength(count);
    expect(stats.buckets.reduce((sum: number, bucket: { invocations: number }) => sum + bucket.invocations, 0)).toBe(stats.invocations);
    expect(stats.buckets.map((bucket: { at: string }) => bucket.at)).toEqual(stats.buckets.map((bucket: { at: string }) => bucket.at).sort());
    expect(stats.buckets.filter((bucket: { invocations: number }) => bucket.invocations === 0).every((bucket: { p50Ms: number; p95Ms: number }) => bucket.p50Ms === 0 && bucket.p95Ms === 0)).toBe(true);
  }
  const logsResponse = await request.get(`${url}/logs`, { headers });
  expect(logsResponse.status()).toBe(200);
  const logs = await logsResponse.json();
  expect(logs).toHaveLength(2);
  expect(logs[0]).toMatchObject({ sqlCalls: 1, rowsRead: 0, rowsWritten: 0, responseBytes: 11, httpStatus: 201 });
  expect(logs[0].sqlMs).toBeGreaterThanOrEqual(0);
  const invalid = await request.get(`${url}/stats?window=2h`, { headers });
  expect(invalid.status()).toBe(400);
  expect((await invalid.json()).error.code).toBe('invalid_request');
  expect((await request.delete(url, { headers })).status()).toBe(204);
  expect((await request.get(`${url}/stats`, { headers })).status()).toBe(404);
});
