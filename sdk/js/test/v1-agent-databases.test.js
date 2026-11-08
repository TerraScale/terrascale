import { readFileSync } from "node:fs";
import { expect, test } from "vitest";
import { createTerraBaseClient, TerraBaseV1Error } from "../src/index.js";

const contract = JSON.parse(readFileSync(new URL("../contracts/terrabase/agent/lifecycle.json", import.meta.url), "utf8"));

test("agent database lifecycle matches the recorded contract and reuses a scoped key", async () => {
  const operations = [...contract.operations];
  /** @type {string[]} */
  const exchanges = [];
  const client = createTerraBaseClient({ project: contract.project, apiKey: contract.apiKey, baseUrl: "https://database.example.invalid",
    fetch: async (input, init) => {
      const url = new URL(String(input));
      const headers = new Headers(init?.headers);
      expect(init?.redirect).toBe("manual");
      const request = new Request(input, init);
      const text = await request.text();
      if (url.pathname === "/terrascale/oauth/token") {
        expect(url.origin).toBe("https://auth.terrascale.tech");
        expect(init?.method).toBe("POST");
        expect(headers.get("authorization")).toBe(`Basic ${btoa(`${contract.apiKey.slice(0, 26)}:${contract.apiKey}`)}`);
        expect(new URLSearchParams(text).get("project")).toBe(contract.project);
        expect(new URLSearchParams(text).get("grant_type")).toBe("client_credentials");
        exchanges.push(text);
        return Response.json({ access_token: contract.accessToken, expires_in: 3600, token_type: "Bearer" });
      }
      const operation = operations.shift();
      expect(operation).toBeDefined();
      expect([init?.method, url.pathname, text === "" ? null : JSON.parse(text)]).toEqual([
        operation.request.method, operation.request.path, operation.request.body,
      ]);
      expect(headers.get("authorization")).toBe(operation.operation === "createScopedKey"
        ? `Basic ${btoa(`${contract.apiKey.slice(0, 26)}:${contract.apiKey}`)}` : `Bearer ${contract.accessToken}`);
      expect(url.origin).toBe(operation.operation === "createScopedKey" ? "https://auth.terrascale.tech" : "https://database.example.invalid");
      return operation.response.status === 204 ? new Response(null, { status: 204 })
        : Response.json(operation.response.body, { status: operation.response.status });
    },
  });
  const created = await client.createDatabase(contract.project, "agent_run", { ttlSeconds: 3600 });
  expect(created).toMatchObject(contract.operations[0].response.body);
  expect(await client.forkDatabase(contract.project, created.id, "agent_fork", { ttlSeconds: 7200 })).toMatchObject(contract.operations[1].response.body);
  expect(await client.forkDatabase(contract.project, created.name, "agent_past", { pointInTime: "2026-11-04T12:00:00Z" })).toMatchObject(contract.operations[4].response.body);
  expect(await client.setDatabaseTtl(contract.project, created.name, 1800)).toMatchObject(contract.operations[5].response.body);
  expect(await client.clearDatabaseTtl(contract.project, created.id)).toMatchObject(contract.operations[6].response.body);
  expect(await client.deleteDatabase(contract.project, created.id)).toBeUndefined();
  const scoped = await client.createScopedKey({ project: contract.project, database: created.id, access: "read_write", ttlSeconds: 600 });
  const wireKey = contract.operations[8].response.body;
  expect(scoped).toEqual({ key: wireKey.key, id: wireKey.id, projectId: wireKey.project_id,
    database: wireKey.database, access: wireKey.access, expiresAt: wireKey.expires_at });
  const agent = createTerraBaseClient({ project: contract.project, apiKey: scoped.key, baseUrl: "https://database.example.invalid", fetch: clientFetch });
  /** @type {import("../src/config.js").FetchLike} */
  async function clientFetch(input, init) {
    const url = new URL(String(input));
    if (url.pathname === "/terrascale/oauth/token") {
      expect(new Headers(init?.headers).get("authorization")).toBe(`Basic ${btoa(`${scoped.id}:${scoped.key}`)}`);
      expect(new URLSearchParams(await new Request(input, init).text()).get("project")).toBe(contract.project);
      return Response.json({ access_token: contract.accessToken, expires_in: 600 });
    }
    expect(url.pathname).toBe(`/v1/projects/${contract.project}/databases/${created.id}/sql/execute`);
    expect(new Headers(init?.headers).get("authorization")).toBe(`Bearer ${contract.accessToken}`);
    return Response.json({ results: [{ columns: [{ name: "n", decltype: "INTEGER" }], rows: [[1]], rows_affected: 0, last_insert_rowid: null }] });
  }
  expect(await agent.database(created.id).query("SELECT 1")).toEqual([{ n: 1 }]);
  expect(exchanges).toHaveLength(1);
  expect(operations).toHaveLength(0);
  expect(JSON.stringify(client)).not.toContain(contract.apiKey);
});

test("agent database validation and scoped-key refusals preserve errors without replay", async () => {
  /** @type {{ method: string; path: string; body: unknown }[]} */
  const requests = [];
  const client = createTerraBaseClient({ project: contract.project, apiKey: contract.apiKey, baseUrl: "https://database.example.invalid",
    fetch: async (input, init) => {
      const request = new Request(input, init);
      requests.push({ method: request.method, path: new URL(request.url).pathname, body: JSON.parse(await request.text()) });
      return Response.json(contract.forbidden.response.body, { status: 403, headers: { "retry-after": "7" } });
    },
  });
  await expect(client.createDatabase(contract.project, "agent_run", { ttlSeconds: 59 })).rejects.toThrow(TypeError);
  await expect(client.forkDatabase(contract.project, "agent_run", "agent_fork", { ttlSeconds: 31_536_001 })).rejects.toThrow(TypeError);
  await expect(client.forkDatabase(contract.project, "agent_run", "agent_fork", { pointInTime: "yesterday" })).rejects.toThrow(TypeError);
  await expect(client.setDatabaseTtl(contract.project, "agent_run", 1.5)).rejects.toThrow(TypeError);
  await expect(client.createScopedKey({ database: "agent_run", access: "read" })).rejects.toThrow(TypeError);
  await expect(client.createScopedKey({ database: contract.database, access: "read", ttlSeconds: 2_592_001 })).rejects.toThrow(TypeError);
  expect(requests).toHaveLength(0);
  const error = await client.createScopedKey({ database: contract.database, access: "read" }).catch(error => error);
  expect(error).toBeInstanceOf(TerraBaseV1Error);
  expect(error).toMatchObject({ status: 403, code: "forbidden", retryAfter: 7, body: contract.forbidden.response.body });
  expect(requests).toEqual([contract.forbidden.request]);
});
