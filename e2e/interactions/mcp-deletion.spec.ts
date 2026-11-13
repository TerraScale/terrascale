import { test, expect } from '../support/audit';
import { confirmWithPassword } from '../support/hosted';
import { deletionHandoff, mcpTool } from '../support/mcp';
import { journey, saveSecret } from '../support/state';

test('database, project and saved-query MCP deletion hands off without deleting resources', async ({ page, audit }) => {
  const current = journey();
  await page.goto('/profile?tab=access-tokens');
  await page.locator('#accessTokenName').fill(`deletion-${current.runId}-${Date.now()}`);
  await page.locator('#profile-access-token-create form').getByRole('button', { name: 'Create token', exact: true }).click();
  await confirmWithPassword(page, audit, current.password, 'MCP deletion handoffs');
  const token = await page.locator('#access-token-secret').inputValue();
  saveSecret('deletion-mcp-token', token);
  const scope = { organizationId: current.organizationId, projectId: current.projectId, databaseId: current.databaseId };
  const query = await mcpTool(page.request, token, 'save_query', { ...scope, name: `kept-${Date.now()}`, sql: 'SELECT 1' });
  for (const credential of [current.apiKey, token]) {
    const database = await deletionHandoff(page.request, credential, 'delete_database', current.databaseId, `/databases/${current.databaseId}/danger`, { ...scope, confirmName: current.databaseName });
    expect(database.searchParams.get('delete')).toBe('1');
    await deletionHandoff(page.request, credential, 'delete_project', current.projectId, '/organization', { ...scope, confirmName: current.projectName });
    const saved = await deletionHandoff(page.request, credential, 'delete_saved_query', query.id, `/databases/${current.databaseId}/sql`, { ...scope, queryId: query.id });
    expect(saved.hash).toBe('#saved-queries');
    const kept = await mcpTool(page.request, token, 'get_database', scope);
    expect(kept.id).toBe(current.databaseId);
    const queries = await mcpTool(page.request, token, 'list_saved_queries', scope);
    expect(queries.queries.map((item: { id: string }) => item.id)).toContain(query.id);
    const context = await mcpTool(page.request, token, 'get_context', scope);
    expect(context.selection.projectId).toBe(current.projectId);
  }
});

test('retired administration routes are absent for signed-in and anonymous requests', async ({ page, playwright }) => {
  const anonymous = await playwright.request.newContext({ baseURL: 'https://dash.terrascale.tech', ignoreHTTPSErrors: true });
  try {
    for (const request of [page.request, anonymous]) {
      for (const path of ['/admin', '/admin/plans']) {
        expect((await request.get(path, { maxRedirects: 0 })).status(), path).toBe(404);
      }
      for (const path of [
        '/api/v1/admin/users/account',
        '/api/v1/admin/projects/account/organization/project',
        '/api/v1/admin/organizations/account/organization',
        '/api/v1/admin/estate/action',
        '/api/v1/admin/plans',
        '/api/v1/admin/plans/plan/visibility',
        '/api/v1/admin/plans/plan/delete',
      ]) {
        expect((await request.post(path, { maxRedirects: 0 })).status(), path).toBe(404);
      }
    }
  } finally {
    await anonymous.dispose();
  }
});
