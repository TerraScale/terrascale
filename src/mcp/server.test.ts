import assert from "node:assert/strict";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { loadMcpConfig } from "./config.js";
import { createTerraScaleMcpServer } from "./server.js";

test("MCP foundation registers the documented read-only protocol surface", async () => {
  const server = createTerraScaleMcpServer({ apiBaseUrl: "https://api.example.test", accessToken: "test-token" });
  const client = new Client({ name: "protocol-check", version: "1.0.0" });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  await Promise.all([server.connect(serverTransport), client.connect(clientTransport)]);

  const tools = await client.listTools();
  const resources = await client.listResources();
  const prompts = await client.listPrompts();

  assert.deepEqual(tools.tools.map((tool) => tool.name).sort(), [
    "terrascale_backup_restore_status", "terrascale_get_document", "terrascale_get_schema",
    "terrascale_get_usage", "terrascale_list_databases", "terrascale_list_documents",
    "terrascale_list_projects", "terrascale_list_repositories", "terrascale_query_tql",
  ]);
  assert.deepEqual(resources.resources.map((resource) => resource.uri).sort(), [
    "terrascale://guides/api-overview", "terrascale://guides/safe-mutations", "terrascale://guides/tql-guidance",
  ]);
  assert.deepEqual(prompts.prompts.map((prompt) => prompt.name).sort(), [
    "backup-and-restore-plan", "explore-project", "investigate-data-issue", "plan-schema-change", "safe-data-change",
  ]);

  const mutation = await client.callTool({ name: "terrascale_query_tql", arguments: {
    repositoryId: "01ARZ3NDEKTSV4RRFFQ69G5FAV", query: "delete from Orders", skip: 0, take: 1,
  } });
  assert.equal(mutation.isError, true);

  await Promise.all([client.close(), server.close()]);
});

test("MCP configuration validates required public connection settings", () => {
  assert.throws(() => loadMcpConfig({}), /TERRASCALE_API_BASE_URL must be set/);
  assert.throws(() => loadMcpConfig({ TERRASCALE_API_BASE_URL: "ftp://example.test", TERRASCALE_ACCESS_TOKEN: "x" }), /http\(s\)/);
  assert.deepEqual(loadMcpConfig({ TERRASCALE_API_BASE_URL: "https://api.example.test/", TERRASCALE_ACCESS_TOKEN: "x" }), {
    apiBaseUrl: "https://api.example.test", accessToken: "x",
  });
});
