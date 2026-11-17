import { expect, type APIRequestContext } from '@playwright/test';
import { auth } from './stack';
import { keyId } from './api-key';
import { journey, saveSecret } from './state';

export const functionSource = 'async function handler(event) { console.log("functions journey"); return { ok: true }; }';

export async function functionAccess(request: APIRequestContext): Promise<Record<string, string>> {
  const current = journey();
  const response = await request.post(`${auth}/terrascale/oauth/token`, {
    headers: { Authorization: `Basic ${Buffer.from(`${keyId(current.apiKey)}:${current.apiKey}`).toString('base64')}` },
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
  get_functions_reference: { reference: string };
  deploy_function: { name: string; cron: string | null; updated_at: string; invoke_url: string; error?: { code: string; message: string; line?: number; column?: number } };
  invoke_function: { status: number; content_type: string; body: unknown; log: { status: string; duration_ms: number; lines: string[]; error: string | null } | null; log_note: string };
  list_functions: { functions: { name: string }[] };
  get_function: { function: { source: string }; invokeUrl: string };
  list_function_logs: { logs: { lines: string[] }[] };
  get_function_stats: { window: string; invocations: number; errors: number; p50Ms: number; p95Ms: number; p99Ms: number; maxMs: number; sqlCalls: number; rowsRead: number; rowsWritten: number;
    buckets: { at: string; invocations: number; errors: number; p50Ms: number; p95Ms: number }[]; error?: { code: string; message: string } };
  set_function_secret: { name: string; updated_at: string; error?: { code: string; message: string } };
  list_function_secrets: { secrets: { name: string; updated_at: string }[] };
  delete_function_secret: { name: string; deleted: boolean };
};

export async function functionTool<T extends keyof FunctionTools>(request: APIRequestContext, token: string, name: T, args: Record<string, unknown>, isError = false): Promise<FunctionTools[T]> {
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
  expect(message.result.isError === true, `MCP ${name} result`).toBe(isError);
  if (['get_functions_reference', 'deploy_function', 'invoke_function', 'get_function_stats'].includes(name)) {
    const result = message.result.structuredContent;
    const bucketCount = name === 'get_function_stats' && !isError ? result.buckets.length : undefined;
    const trimmed = name === 'get_functions_reference' ? { reference: result.reference.slice(0, 220) + ' [trimmed]' }
      : bucketCount !== undefined ? { ...result, buckets: result.buckets.slice(0, 1) } : result;
    console.log('Functions MCP transcript ' + JSON.stringify({ request: { jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name, arguments: args } }, response: { jsonrpc: '2.0', id: 1, result: { isError: message.result.isError === true, structuredContent: trimmed } }, bucketCount }));
  }
  return message.result.structuredContent;
}
