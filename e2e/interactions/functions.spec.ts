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
  const row = page.locator('#functions tbody tr').filter({ hasText: name });
  await expect(row).toHaveCount(1);
  await row.getByRole('link', { name: 'Details', exact: true }).click();
  await expect(page.locator('#function-source')).toHaveValue(functionSource);
  await expect(page.locator('#function-invoke')).toContainText(functionUrl(name));
  await expect(page.locator('#function-logs tbody tr')).toHaveCount(1);
  await expect(page.locator('#function-logs')).toContainText('functions journey');
  await audit.check('function detail and real invocation logs');
  await page.getByRole('button', { name: 'Delete function', exact: true }).click();
  const dialog = page.locator('#delete-function');
  await expect(dialog).toBeVisible();
  await audit.check('delete function confirmation');
  await dialog.getByRole('button', { name: 'Delete function', exact: true }).click();
  await page.waitForURL((url) => url.pathname.endsWith('/functions') && !url.searchParams.has('function'));
  await expect(page.locator('#functions tbody tr').filter({ hasText: name })).toHaveCount(0);
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
  await expect(page.getByRole('heading', { name: 'Functions', exact: true })).toBeVisible();
  await audit.check('the functions page remains usable after a stale delete');
  console.log('Functions delete failure: already-deleted function returns HTML 404 with modal alert; Cancel returns to the usable page.');
});

async function setSource(page: Page, source: string): Promise<void> {
  await page.locator('#function-editor').evaluate((editor: HTMLTextAreaElement, text: string) => {
    editor.value = text;
    editor.dispatchEvent(new Event('input', { bubbles: true }));
  }, source);
  await expect(page.locator('#function-editor')).toHaveValue(source);
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
  await page.locator('#function-event').fill('{"hello":"world"}');
  await page.getByRole('button', { name: 'Test run', exact: true }).click();
  await expect(page.locator('#function-result')).toContainText('world');
  await expect(page.locator('#function-logs')).toContainText('editor journey');
  await page.locator('#function-event').fill('{');
  await page.getByRole('button', { name: 'Test run', exact: true }).click();
  await expect(page.locator('#function-event-error')).toHaveText('Event must be JSON.');
  await expect(page.locator('#function-event')).toHaveValue('{');
  await page.getByRole('link', { name: 'Edit', exact: true }).click();
  await expect(page.locator('#function-name')).toHaveValue(name);
  await expect(page.locator('#function-name')).toHaveJSProperty('readOnly', true);
  await page.locator('#function-cron').fill('*/5 * * * *');
  await setSource(page, edited);
  await page.getByRole('button', { name: 'Deploy function', exact: true }).click();
  await page.waitForURL((url) => url.searchParams.get('function') === name && url.searchParams.get('deployed') === '1');
  await expect(page.locator('#function-source')).toHaveValue(edited);
  await expect(page.getByText('Cron */5 * * * * UTC', { exact: true })).toBeVisible();
  console.log('Functions editor: invalid JavaScript stays in the editor, deploy, test run, invalid JSON, edit and redeploy with cron.');
});
