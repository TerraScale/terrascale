import assert from "node:assert/strict";
import { createServer, type Server } from "node:http";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { once } from "node:events";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createStructuredLogger } from "./logger.js";
import { readRuntimeConfig } from "./config.js";
import { createHttpMcpServer } from "./http.js";
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
  return { url: `http://127.0.0.1:${address.port}`, close: async () => { server.close(); await once(server, "close"); } };
}
