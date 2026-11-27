import { existsSync, mkdirSync, readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, type APIRequestContext } from '@playwright/test';
import { auth, stateDir } from './stack';
import { keyId } from './api-key';
import { journey, saveSecret } from './state';
import { cleanUpAll } from './cleanup';

export const functionSource = 'async function handler(event) { console.log("functions journey"); return { ok: true }; }';

const removeBudgetMs = 15_000;
export const removeAllBudgetMs = 20_000;
const removeRequestTimeoutMs = 10_000;

function deployedFunctionsDir(): string {
  return join(stateDir, 'functions.d', journey().databaseId);
}

export function recordJourneyFunction(name: string): void {
  const dir = deployedFunctionsDir();
  mkdirSync(dir, { recursive: true, mode: 0o700 });
  writeFileSync(join(dir, name), '', { mode: 0o600 });
}

export function recordedJourneyFunctions(): string[] {
  const dir = deployedFunctionsDir();
  return existsSync(dir) ? readdirSync(dir).sort() : [];
}

export async function functionAccess(request: APIRequestContext, deadline?: number): Promise<Record<string, string>> {
  const current = journey();
  let timeout: number | undefined;
  if (deadline !== undefined) {
    const remainingMs = deadline - Date.now();
    if (remainingMs <= 0) {
      throw new Error('exchange the journey API key for project authority: cleanup deadline expired');
    }
    timeout = Math.min(remainingMs, removeRequestTimeoutMs);
  }
  const response = await request.post(`${auth}/terrascale/oauth/token`, {
    headers: { Authorization: `Basic ${Buffer.from(`${keyId(current.apiKey)}:${current.apiKey}`).toString('base64')}` },
    form: { grant_type: 'client_credentials', project: current.projectId },
    ...(timeout !== undefined ? { timeout } : {}),
  });
  expect(response.status(), 'exchange the journey API key for project authority').toBe(200);
  const token = (await response.json()).access_token;
  saveSecret('functions-project-token', token);
  return { Authorization: `Bearer ${token}` };
}

export function functionsUrl(database = journey().databaseName): string {
  const current = journey();
  return `https://api.terrascale.tech/v1/projects/${current.projectId}/databases/${database}/functions`;
}

export function functionUrl(name: string, database = journey().databaseName): string {
  return `${functionsUrl(database)}/${name}`;
}

export async function removeFunction(
  request: APIRequestContext,
  headers: Record<string, string>,
  name: string,
  url = functionUrl(name, journey().databaseId),
  deadline = Date.now() + removeBudgetMs,
): Promise<void> {
  const remainingMs = deadline - Date.now();
  if (remainingMs <= 0) {
    throw new Error(`remove the function ${name}: cleanup deadline expired before DELETE`);
  }
  const response = await request.delete(url, { headers, timeout: Math.min(remainingMs, removeRequestTimeoutMs) });
  if (response.status() === 204) {
    return;
  }
  const text = await response.text();
  if (response.status() === 404) {
    let code: unknown;
    try {
      const body = JSON.parse(text) as { error?: { code?: unknown } };
      code = body.error?.code;
    } catch {
      code = undefined;
    }
    expect(code, `remove the function ${name}: ${text}`).toBe('function_not_found');
    return;
  }
  expect(response.status(), `remove the function ${name}: ${text}`).toBe(204);
}

export async function removeFunctions(
  request: APIRequestContext,
  headers: Record<string, string>,
  names: Iterable<string>,
  deadline = Date.now() + removeAllBudgetMs,
  databaseId = journey().databaseId,
): Promise<void> {
  await cleanUpAll([...names].map((name) => () => removeFunction(request, headers, name, functionUrl(name, databaseId), deadline)));
}

export async function findJourneyDatabase(
  request: APIRequestContext,
  headers: Record<string, string>,
  deadline = Date.now() + removeAllBudgetMs,
): Promise<{ id: string; name: string } | null> {
  const current = journey();
  let after: string | undefined;
  while (true) {
    const remainingMs = deadline - Date.now();
    if (remainingMs <= 0) {
      throw new Error('list project databases: cleanup deadline expired');
    }
    const query = after ? `?limit=100&after=${encodeURIComponent(after)}` : '?limit=100';
    const response = await request.get(`https://api.terrascale.tech/v1/projects/${current.projectId}/databases${query}`, {
      headers,
      timeout: Math.min(remainingMs, removeRequestTimeoutMs),
    });
    if (response.status() !== 200) {
      expect(response.status(), `list project databases: ${await response.text()}`).toBe(200);
    }
    const body = (await response.json()) as { items: { id: string; name: string }[]; next?: string | null };
    const found = body.items.find((database) => database.id === current.databaseId);
    if (found) {
      return found;
    }
    if (!body.next) {
      return null;
    }
    after = body.next;
  }
}

export async function removeAllFunctions(
  request: APIRequestContext,
  headers: Record<string, string>,
  deadline = Date.now() + removeAllBudgetMs,
  knownNames: Iterable<string> = recordedJourneyFunctions(),
  databaseId = journey().databaseId,
): Promise<string[]> {
  const remainingMs = deadline - Date.now();
  if (remainingMs <= 0) {
    throw new Error('list the journey database functions: cleanup deadline expired');
  }
  const listed = await request.get(functionsUrl(databaseId), { headers, timeout: Math.min(remainingMs, removeRequestTimeoutMs) });
  if (listed.status() !== 200) {
    expect(listed.status(), `list the journey database functions: ${await listed.text()}`).toBe(200);
  }
  const names = [...new Set([...((await listed.json()) as { name: string }[]).map((item) => item.name), ...knownNames])];
  await removeFunctions(request, headers, names, deadline, databaseId);
  return names;
}

export async function deployFunction(request: APIRequestContext, headers: Record<string, string>, name: string): Promise<void> {
  recordJourneyFunction(name);
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
