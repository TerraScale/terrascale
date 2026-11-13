import { expect, type APIRequestContext } from '@playwright/test';
import { auth } from './stack';
import { journey, saveSecret } from './state';

export const functionSource = 'async function handler(event) { console.log("functions journey"); return { ok: true }; }';

export async function functionAccess(request: APIRequestContext): Promise<Record<string, string>> {
  const current = journey();
  const response = await request.post(`${auth}/terrascale/oauth/token`, {
    headers: { Authorization: `Basic ${Buffer.from(`${current.apiKey.slice(0, 26)}:${current.apiKey}`).toString('base64')}` },
    form: { grant_type: 'client_credentials', project: current.projectId },
  });
  expect(response.status(), 'exchange the journey API key for project authority').toBe(200);
  const token = (await response.json()).access_token;
  saveSecret('functions-project-token', token);
  return { Authorization: `Bearer ${token}` };
}

export function functionUrl(name: string): string {
  const current = journey();
  return `https://api.terrascale.tech/v1/projects/${current.projectId}/databases/${current.databaseName}/functions/${name}`;
}

export async function deployFunction(request: APIRequestContext, headers: Record<string, string>, name: string): Promise<void> {
  const response = await request.put(functionUrl(name), { headers, data: { source: functionSource } });
  expect(response.status(), 'deploy a real function through TerraBase REST').toBe(200);
}

type FunctionTools = {
  list_functions: { functions: { name: string }[] };
  get_function: { function: { source: string }; invokeUrl: string };
  list_function_logs: { logs: { lines: string[] }[] };
};

export async function functionTool<T extends keyof FunctionTools>(request: APIRequestContext, token: string, name: T, args: Record<string, string>): Promise<FunctionTools[T]> {
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
