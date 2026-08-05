import assert from "node:assert/strict";
import { createServer, request as httpRequest, type IncomingMessage, type Server } from "node:http";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { once } from "node:events";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import { createStructuredLogger } from "./logger.js";
import { readHttpListenConfig, readHttpSessionLimits, readRuntimeConfig } from "./config.js";
import { createHttpMcpServer, type McpHttpServerOptions } from "./http.js";
import { listen, McpListenError } from "./listen.js";
import { createMcpServer } from "./server.js";

const projectA = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const projectB = "01ARZ3NDEKTSV4RRFFQ69G5FAW";

test("discovers only read-only tools and static MCP guidance", async () => {
  const { client, close } = await connectedClient("https://api.terrascale.example", "caller-token");
  try {
    const tools = await client.listTools();
    assert.deepEqual(tools.tools.map((tool) => tool.name).sort(), [
      "database_get", "database_list_backups", "database_list_restore_points", "identity_get_context",
      "organization_list_projects", "organizations_list", "project_get", "project_get_latency",
      "project_get_usage", "project_get_usage_history", "project_list_databases", "project_list_region_placements",
    ]);
    assert.ok(tools.tools.every((tool) => tool.annotations?.readOnlyHint === true && tool.annotations?.destructiveHint === false));

    const resources = await client.listResources();
    assert.deepEqual(resources.resources.map((resource) => resource.uri).sort(), ["terrascale://guide", "terrascale://withheld"]);
    const prompt = await client.getPrompt({ name: "read-only-scope" });
    assert.match(JSON.stringify(prompt), /read-only/);
  } finally {
    await close();
  }
});

test("uses public typed paths with explicit project identity and never returns credentials", async () => {
  const seen: { readonly authorization: string | undefined; readonly path: string }[] = [];
  const boundary = await publicBoundary((request, response) => {
    seen.push({ authorization: request.headers.authorization, path: request.url ?? "" });
    response.writeHead(200, { "content-type": "application/json" });
    response.end("[]");
  });
  const secret = "caller-secret-must-not-appear";
  const { client, close } = await connectedClient(boundary.url, secret);
  try {
    await client.callTool({ name: "project_list_databases", arguments: { projectId: projectA } });
    await client.callTool({ name: "project_list_databases", arguments: { projectId: projectB } });
    assert.deepEqual(seen.map((request) => request.path), [
      `/api/v1/projects/${projectA}/databases?take=50`,
      `/api/v1/projects/${projectB}/databases?take=50`,
    ]);
    assert.ok(seen.every((request) => request.authorization === `Bearer ${secret}`));
  } finally {
    await close();
    await boundary.close();
  }
});

test("redacts public API errors and structured logs", async () => {
  const secret = "caller-secret-must-not-appear";
  const boundary = await publicBoundary((_request, response) => {
    response.writeHead(401, { "content-type": "application/json" });
    response.end(JSON.stringify({ code: "authentication_failed", message: `authorization ${secret}` }));
  });
  const logs: string[] = [];
  const { client, close } = await connectedClient(boundary.url, secret, createStructuredLogger((line) => logs.push(line)));
  try {
    const result = await client.callTool({ name: "identity_get_context" });
    assert.equal(result.isError, true);
    assert.doesNotMatch(JSON.stringify(result), new RegExp(secret));
    assert.doesNotMatch(logs.join("\n"), new RegExp(secret));
  } finally {
    await close();
    await boundary.close();
  }
});

test("does not import or expose generic SDK transport escape hatches", async () => {
  const source = await readFile(new URL("../src/server.ts", import.meta.url), "utf8");
  assert.match(source, /createTerraScaleClient.*@terrascale\/sdk/);
  assert.doesNotMatch(source, /TerraScaleHttp|sdkRouteManifest|\.request\s*\(/);
  assert.doesNotMatch(source, /Platform|MagicOnion|TerraBase/);
});

test("validates configuration without echoing secrets and exposes non-sensitive health checks", async () => {
  const secret = "configuration-secret-must-not-appear";
  assert.throws(
    () => readRuntimeConfig({ apiBaseUrl: "https://api.terrascale.example", accessToken: `${secret}${"x".repeat(8_192)}` }),
    (error: unknown) => error instanceof Error && !error.message.includes(secret),
  );
  assert.throws(() => readHttpListenConfig({ TERRASCALE_MCP_HOST: "0.0.0.0" }));
  assert.deepEqual(readHttpListenConfig({ TERRASCALE_MCP_HOST: "0.0.0.0", TERRASCALE_MCP_ALLOWED_HOSTS: "mcp.test", TERRASCALE_MCP_ALLOWED_ORIGINS: "https://console.test" }), { host: "0.0.0.0", allowedHosts: ["mcp.test"], allowedOrigins: ["https://console.test"] });
  assert.deepEqual(readHttpSessionLimits({ TERRASCALE_MCP_MAX_SESSIONS: "2", TERRASCALE_MCP_SESSION_IDLE_TIMEOUT_MS: "1000", TERRASCALE_MCP_SESSION_TTL_MS: "2000" }), { maxSessions: 2, sessionIdleTimeoutMs: 1_000, sessionTtlMs: 2_000 });

  const server = createHttpMcpServer({ apiBaseUrl: "https://api.terrascale.example", requestTimeoutMs: 1_000 }, createStructuredLogger((_) => undefined));
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  assert.notEqual(address, null);
  if (address === null || typeof address === "string") {
    throw new Error("test_server_unavailable");
  }
  try {
    assert.equal((await fetch(`http://127.0.0.1:${address.port}/healthz`)).status, 200);
    assert.equal((await fetch(`http://127.0.0.1:${address.port}/readyz`)).status, 200);
  } finally {
    server.close();
    await once(server, "close");
  }
});

test("uses the Streamable HTTP session lifecycle without credential bleed", async () => {
  const secretA = "caller-a-secret";
  const secretB = "caller-b-secret";
  const logs: string[] = [];
  const publicRequests: string[] = [];
  const boundary = await publicBoundary((request, response) => {
    publicRequests.push(request.headers.authorization ?? "");
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({
      userId: "01ARZ3NDEKTSV4RRFFQ69G5FAV", email: "caller@example.test", name: "Caller", phoneNumber: "", avatarUrl: "", personalOrgId: "01ARZ3NDEKTSV4RRFFQ69G5FAW", activeOrgId: "01ARZ3NDEKTSV4RRFFQ69G5FAX", permissions: [], roles: [],
    }));
  });
  const mcp = await mcpHttpServer(boundary.url, createStructuredLogger((line) => logs.push(line)));
  const first = await connectedHttpClient(mcp.url, secretA);
  const second = await connectedHttpClient(mcp.url, secretB);
  try {
    assert.ok(first.transport.sessionId !== undefined);
    assert.ok(second.transport.sessionId !== undefined);
    assert.notEqual(first.transport.sessionId, second.transport.sessionId);
    assert.ok((await first.client.listTools()).tools.some((tool) => tool.name === "identity_get_context"));
    await first.client.callTool({ name: "identity_get_context" });
    await second.client.callTool({ name: "identity_get_context" });

    assert.equal((await mcpRequest(mcp.url, secretA, undefined, "tools/list")).status, 400);
    assert.equal((await mcpRequest(mcp.url, secretA, "unknown-session", "tools/list")).status, 404);
    const wrongCredential = await mcpRequest(mcp.url, secretB, first.transport.sessionId, "tools/list");
    assert.equal(wrongCredential.status, 404);
    assert.doesNotMatch(await wrongCredential.text(), new RegExp(secretA));
    assert.deepEqual(publicRequests, [`Bearer ${secretA}`, `Bearer ${secretB}`]);

    const firstSessionId = first.transport.sessionId;
    await first.transport.terminateSession();
    assert.equal((await mcpRequest(mcp.url, secretA, firstSessionId, "tools/list")).status, 404);
    await second.transport.terminateSession();
    assert.doesNotMatch(logs.join("\n"), new RegExp(`${secretA}|${secretB}`));
  } finally {
    await first.client.close();
    await second.client.close();
    await mcp.close();
    await boundary.close();
  }
});

test("rejects a slow request body and releases the connection", async () => {
  const mcp = await mcpHttpServer("https://api.terrascale.example", createStructuredLogger((_) => undefined), { bodyTimeoutMs: 1_000, headersTimeoutMs: 50, requestTimeoutMs: 200, keepAliveTimeoutMs: 25, initializeTimeoutMs: 100, maxSessions: 1 });
  const address = new URL(mcp.url);
  const request = httpRequest({ host: address.hostname, port: Number(address.port), method: "POST", path: "/mcp", headers: { authorization: "Bearer slow-caller", "content-type": "application/json", "content-length": "64" } });
  const blockedTransport = new StreamableHTTPClientTransport(new URL(mcp.url), { requestInit: { headers: { authorization: "Bearer blocked-caller" } } });
  const blockedClient = new Client({ name: "terrascale-mcp-pending-cap-test", version: "1.0.0" });
  request.on("error", () => undefined);
  try {
    const response = eventWithin(once(request, "response"), 1_000) as Promise<[IncomingMessage]>;
    const closed = once(request, "close");
    request.write("{");
    await assert.rejects(blockedClient.connect(blockedTransport as unknown as Transport));
    const [incoming] = await response;
    incoming.resume();
    await eventWithin(once(incoming, "end"), 1_000);
    await eventWithin(closed, 1_000);
    assert.equal(incoming.statusCode, 408);
    assert.equal(request.destroyed, true);
    const replacement = await connectedHttpClient(mcp.url, "released-caller");
    await replacement.transport.terminateSession();
  } finally {
    request.destroy();
    await blockedClient.close();
    await mcp.close();
  }
});

test("caps and expires sessions without retaining caller credentials", async () => {
  const secret = "expired-caller-secret";
  const mcp = await mcpHttpServer("https://api.terrascale.example", createStructuredLogger((_) => undefined), { maxSessions: 1, sessionIdleTimeoutMs: 1_000, sessionTtlMs: 200 });
  const first = await connectedHttpClient(mcp.url, secret);
  const sessionId = first.transport.sessionId;
  const blockedTransport = new StreamableHTTPClientTransport(new URL(mcp.url), { requestInit: { headers: { authorization: "Bearer another-caller" } } });
  const blockedClient = new Client({ name: "terrascale-mcp-cap-test", version: "1.0.0" });
  try {
    await assert.rejects(blockedClient.connect(blockedTransport as unknown as Transport));
    await delay(250);
    assert.equal((await mcpRequest(mcp.url, secret, sessionId, "tools/list")).status, 404);
    const replacement = await connectedHttpClient(mcp.url, "replacement-caller");
    await replacement.transport.terminateSession();
    const source = await readFile(new URL("../src/http.ts", import.meta.url), "utf8");
    assert.match(source, /type Session = \{ readonly transport: .*credentialHash/);
    assert.doesNotMatch(source, /type Session = \{[^}]*accessToken/);
    assert.match(source, /createMcpServer\(config, logger, \(extra\) => callerAccessToken\(requestAuthorization\(extra\.requestInfo\?\.headers\.authorization\)\)\)/);
    assert.doesNotMatch(source, /runtimeConfig/);
    const serverSource = await readFile(new URL("../src/server.ts", import.meta.url), "utf8");
    assert.match(serverSource, /accessToken: \(\) => context\.getStore\(\)\?\.accessToken/);
    assert.match(serverSource, /context\.run\(\{ signal: extra\.signal, accessToken: credentialProvider\(extra\) \}/);
  } finally {
    await first.client.close();
    await blockedClient.close();
    await mcp.close();
  }
});

test("expires idle sessions before their absolute TTL", async () => {
  const secret = "idle-caller-secret";
  const mcp = await mcpHttpServer("https://api.terrascale.example", createStructuredLogger((_) => undefined), { sessionIdleTimeoutMs: 25, sessionTtlMs: 1_000 });
  const connection = await connectedHttpClient(mcp.url, secret);
  try {
    await connection.client.close();
    await delay(60);
    assert.equal((await mcpRequest(mcp.url, secret, connection.transport.sessionId, "tools/list")).status, 404);
  } finally {
    await connection.client.close();
    await mcp.close();
  }
});

test("keeps a live SSE stream beyond the idle deadline", async () => {
  const secret = "stream-caller";
  const mcp = await mcpHttpServer("https://api.terrascale.example", createStructuredLogger((_) => undefined), { requestTimeoutMs: 25, keepAliveTimeoutMs: 25, sessionIdleTimeoutMs: 25, sessionTtlMs: 1_000 });
  const connection = await connectedHttpClient(mcp.url, secret);
  try {
    const sessionId = connection.transport.sessionId ?? "";
    await connection.client.close();
    await delay(10);
    const response = await fetch(mcp.url, { headers: { accept: "text/event-stream", authorization: `Bearer ${secret}`, "mcp-protocol-version": "2025-03-26", "mcp-session-id": sessionId } });
    assert.equal(response.status, 200);
    const reader = response.body?.getReader();
    assert.notEqual(reader, undefined);
    const closed = reader?.closed.then(() => true, () => true) ?? Promise.resolve(true);
    assert.equal(await Promise.race([closed, delay(60).then(() => false)]), false);
    await reader?.cancel();
    assert.equal((await mcpDelete(mcp.url, secret, sessionId)).status, 200);
  } finally {
    await connection.client.close();
    await mcp.close();
  }
});

test("reports listener failures without leaking bind details", async () => {
  const blocker = createServer();
  await listen(blocker, 0, "127.0.0.1", createStructuredLogger((_) => undefined));
  const address = blocker.address();
  if (address === null || typeof address === "string") throw new Error("test_server_unavailable");
  const logs: string[] = [];
  const logger = createStructuredLogger((line) => logs.push(line));
  const occupied = createServer();
  const invalid = createServer();
  try {
    await assert.rejects(listen(occupied, address.port, "127.0.0.1", logger), (error: unknown) => error instanceof McpListenError && error.code === "EADDRINUSE");
    await assert.rejects(listen(invalid, 0, "invalid host!", logger), (error: unknown) => error instanceof McpListenError && error.code.length > 0);
    assert.match(logs.join("\n"), /mcp\.http\.listen_failed/);
    assert.doesNotMatch(logs.join("\n"), /invalid host!/);
  } finally {
    occupied.close();
    invalid.close();
    await closeServer(blocker);
  }
});

test("rejects unexpected Host and Origin before session setup", async () => {
  const mcp = await mcpHttpServer("https://api.terrascale.example", createStructuredLogger((_) => undefined), { allowedHosts: ["mcp.test"], allowedOrigins: ["https://console.test"] });
  try {
    assert.equal(await httpStatus(mcp.url, { host: "other.test", origin: "https://console.test" }), 403);
    assert.equal(await httpStatus(mcp.url, { host: "mcp.test", origin: "https://other.test" }), 403);
    assert.equal(await httpStatus(mcp.url, { host: "mcp.test", origin: "https://console.test" }), 400);
  } finally {
    await mcp.close();
  }
});

async function connectedClient(apiBaseUrl: string, accessToken: string, logger = createStructuredLogger((_) => undefined)) {
  const server = createMcpServer({ apiBaseUrl, accessToken, requestTimeoutMs: 1_000 }, logger);
  const client = new Client({ name: "terrascale-mcp-test", version: "1.0.0" });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  await Promise.all([server.connect(serverTransport), client.connect(clientTransport)]);
  return { client, close: async () => { await client.close(); await server.close(); } };
}

async function publicBoundary(handler: (request: import("node:http").IncomingMessage, response: import("node:http").ServerResponse) => void): Promise<{ readonly url: string; readonly close: () => Promise<void> }> {
  const server: Server = createServer(handler);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  if (address === null || typeof address === "string") {
    throw new Error("test_server_unavailable");
  }
  return { url: `http://127.0.0.1:${address.port}`, close: () => closeServer(server) };
}

async function closeServer(server: Server): Promise<void> {
  if (!server.listening) return;
  server.close();
  await once(server, "close");
}

async function mcpHttpServer(apiBaseUrl: string, logger: ReturnType<typeof createStructuredLogger>, options: McpHttpServerOptions = {}) {
  const server = createHttpMcpServer({ apiBaseUrl, requestTimeoutMs: 1_000 }, logger, options);
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  if (address === null || typeof address === "string") {
    throw new Error("test_server_unavailable");
  }
  return { url: `http://127.0.0.1:${address.port}/mcp`, close: async () => { server.close(); server.closeAllConnections(); await once(server, "close"); } };
}

async function connectedHttpClient(url: string, accessToken: string) {
  const transport = new StreamableHTTPClientTransport(new URL(url), { requestInit: { headers: { authorization: `Bearer ${accessToken}` } } });
  const client = new Client({ name: "terrascale-mcp-http-test", version: "1.0.0" });
  await client.connect(transport as unknown as Transport);
  return { client, transport };
}

function mcpRequest(url: string, accessToken: string, sessionId: string | undefined, method: "tools/list"): Promise<Response> {
  return fetch(url, {
    method: "POST",
    headers: { accept: "application/json, text/event-stream", authorization: `Bearer ${accessToken}`, "content-type": "application/json", "mcp-protocol-version": "2025-03-26", ...(sessionId === undefined ? {} : { "mcp-session-id": sessionId }) },
    body: JSON.stringify({ jsonrpc: "2.0", id: 1, method, params: {} }),
  });
}

function eventWithin(event: Promise<unknown>, timeoutMs: number): Promise<unknown> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("event_timeout")), timeoutMs);
    event.then((value) => { clearTimeout(timer); resolve(value); }, (error: unknown) => { clearTimeout(timer); reject(error); });
  });
}

function delay(timeoutMs: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, timeoutMs));
}

function httpStatus(url: string, headers: Record<string, string>): Promise<number> {
  const address = new URL(url);
  return new Promise((resolve, reject) => {
    const request = httpRequest({ host: address.hostname, port: Number(address.port), method: "GET", path: "/mcp", headers }, (response) => {
      response.resume();
      response.on("end", () => resolve(response.statusCode ?? 0));
    });
    request.on("error", reject);
    request.end();
  });
}

function mcpDelete(url: string, accessToken: string, sessionId: string): Promise<Response> {
  return fetch(url, { method: "DELETE", headers: { authorization: `Bearer ${accessToken}`, "mcp-protocol-version": "2025-03-26", "mcp-session-id": sessionId } });
}
