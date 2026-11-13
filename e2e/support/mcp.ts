import { expect, type APIRequestContext } from '@playwright/test';

export async function mcpTool(request: APIRequestContext, token: string, name: string, args: Record<string, string>) {
  const response = await request.post('https://mcp.terrascale.tech/', {
    headers: { Authorization: `Bearer ${token}`, Accept: 'application/json, text/event-stream' },
    data: { jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name, arguments: args } },
  });
  expect(response.status(), `MCP ${name} transport`).toBe(200);
  const body = await response.text();
  const message = response.headers()['content-type']?.includes('text/event-stream')
    ? JSON.parse(body.split('\n').find((line) => line.startsWith('data: '))!.slice(6))
    : JSON.parse(body);
  expect(message.error, `MCP ${name} protocol`).toBeUndefined();
  expect(message.result.isError, `MCP ${name} result`).not.toBe(true);
  return message.result.structuredContent;
}

export async function deletionHandoff(request: APIRequestContext, token: string, tool: string, resource: string, path: string, args: Record<string, string>) {
  const result = await mcpTool(request, token, tool, args);
  const url = new URL(result.confirmationUrl);
  expect(result).toEqual({ code: 'step_up_required', tool, resource, confirmationUrl: url.href });
  expect(url.origin).toBe('https://dash.terrascale.tech');
  expect(url.pathname).toBe(path);
  expect(url.searchParams.get('organizationId')).toBe(args.organizationId);
  expect(url.searchParams.get('projectId')).toBe(args.projectId);
  return url;
}
