import type { APIRequestContext } from '@playwright/test';
import { Agent, assertion, platformTokenEndpoint } from '../support/agents';
import { expect, test } from '../support/audit';
import { hostedReady } from '../support/hosted';
import { mcpTool } from '../support/mcp';
import { createDedicatedProjectOwner } from '../support/project-owner';
import { enterSecret } from '../support/secret';
import { auth } from '../support/stack';
import { saveSecret } from '../support/state';

const refusal = "Agents can't do this. Ask the account owner to do it in the TerraScale dashboard.";

async function agentToken(request: APIRequestContext, agent: Agent, agentId: string, kid: string, scope: string, subject = 'owner'): Promise<string> {
  const response = await request.post(platformTokenEndpoint, {
    form: { grant_type: 'urn:ietf:params:oauth:grant-type:jwt-bearer', assertion: assertion(agent.key.privateKey, kid, agentId, platformTokenEndpoint), scope, requested_subject: subject },
  });
  expect(response.status()).toBe(200);
  const token = (await response.json()).access_token as string;
  saveSecret(`mcp-agent-${subject}-${scope.replaceAll(' ', '-')}`, token);
  return token;
}

async function mcpStatus(request: APIRequestContext, token: string): Promise<{ status: number; challenge: string }> {
  const response = await request.post('https://mcp.terrascale.tech/', {
    headers: { Authorization: `Bearer ${token}`, Accept: 'application/json, text/event-stream' },
    data: { jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'get_context', arguments: {} } },
  });
  return { status: response.status(), challenge: response.headers()['www-authenticate'] ?? '' };
}

test('a TerraScale account agent calls MCP read and write tools, is refused destructive ones, and stops when revoked in Profile › Agents', async ({ browser, request }, info) => {
  const account = await createDedicatedProjectOwner(browser, info, 'MCP agent owner');
  const { page, audit, current } = account;
  try {
    await page.goto('/profile?tab=agents');
    await expect(page.getByText("No agents yet. Run an agent's enrollment and approve it here.", { exact: true })).toBeVisible();
    await audit.check('profile agents empty');

    const agent = await Agent.start(request, auth + '/v1/terrascale', { name: 'MCP agent', requested_scopes: ['mcp:read', 'mcp:write'], allow_self_subject: true });
    await page.goto(agent.enrollment.verification_uri_complete);
    await hostedReady(page);
    await expect(page.locator('.device-code')).toHaveText(agent.enrollment.user_code);
    await page.getByRole('button', { name: 'Approve', exact: true }).click();
    await page.waitForURL((url) => url.pathname.startsWith('/ui/terrascale/step-up/'));
    await hostedReady(page);
    await enterSecret(page.locator('input[type=password]'), current.password);
    await page.getByRole('button', { name: 'Verify and continue' }).click();
    await page.waitForURL(agent.enrollment.verification_uri_complete);
    await hostedReady(page);
    await page.getByRole('button', { name: 'Approve', exact: true }).click();
    await expect(page.locator('.alert[data-state=success]')).toHaveText('Agent approved. You can close this page.');
    const approved = await agent.poll();
    expect(approved.status).toBe(200);
    const agentId = approved.body.agent_id!;
    const kid = approved.body.kid!;

    const writer = await agentToken(request, agent, agentId, kid, 'mcp:read mcp:write');
    const context = await mcpTool(request, writer, 'get_context', {});
    expect(context.user.email).toBe(current.email);
    expect(context.organizations.map((organization: { id: string }) => organization.id)).toContain(current.organizationId);
    expect(await mcpTool(request, writer, 'update_profile', { name: 'MCP agent owner renamed' })).toEqual({ saved: true });
    expect((await mcpTool(request, writer, 'get_profile', {})).name).toBe('MCP agent owner renamed');
    for (const destructive of ['delete_project', 'revoke_api_key', 'change_plan', 'create_api_key', 'execute_sql']) {
      await mcpTool(request, writer, destructive, {}, refusal);
    }

    const reader = await agentToken(request, agent, agentId, kid, 'mcp:read');
    await mcpTool(request, reader, 'update_profile', { name: 'Refused rename' }, 'This agent token has only mcp:read.');
    expect((await mcpTool(request, reader, 'get_profile', {})).name).toBe('MCP agent owner renamed');

    const self = await agentToken(request, agent, agentId, kid, 'mcp:read', 'agent');
    expect(await mcpStatus(request, self)).toEqual({ status: 401, challenge: expect.stringContaining('error="invalid_token"') });

    await page.goto('/profile?tab=agents');
    const row = page.locator('#profile-agents tbody tr').filter({ hasText: 'MCP agent' });
    await expect(row).toHaveCount(1);
    await expect(row.locator('td[data-label="Scopes"]')).toHaveText('mcp:read, mcp:write');
    await expect(row.locator('td[data-label="Keys"] li')).toHaveText([new RegExp('^' + kid.slice(0, 12) + ' · ')]);
    await expect(row.locator('td[data-label="Last used"]')).not.toHaveText('Never');
    await audit.check('profile agents listed');
    await row.getByRole('button', { name: 'Revoke', exact: true }).click();
    const dialog = page.locator(`#agent-revoke-${agentId}[open]`);
    await expect(dialog.getByRole('heading', { name: 'Revoke MCP agent?' })).toBeVisible();
    await expect(dialog.locator('.ts-dialog-description')).toHaveText('It stops working at once.');
    await audit.check('profile agent revoke dialog');
    await dialog.getByRole('button', { name: 'Revoke agent', exact: true }).click();
    await page.waitForURL((url) => url.searchParams.get('status') === 'agent-revoked');
    await expect(page.getByText('Agent revoked.', { exact: true })).toBeVisible();
    await expect(page.getByText("No agents yet. Run an agent's enrollment and approve it here.", { exact: true })).toBeVisible();
    await audit.check('profile agent revoked');

    expect(await mcpStatus(request, writer)).toEqual({ status: 401, challenge: expect.stringContaining('error="invalid_token"') });
    expect(await mcpStatus(request, reader)).toEqual({ status: 401, challenge: expect.stringContaining('error="invalid_token"') });
    await audit.finish();
  } finally {
    await account.remove();
  }
});
