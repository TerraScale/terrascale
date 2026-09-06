import { describe, expect, it } from "vitest";
import {
  createTerraScaleManagementClient,
  ManagementContractGapError,
  validatePlacementLock,
} from "./management.js";

function fixtureFetch(requests: Request[]): typeof fetch {
  return async (input) => {
    const request = new Request(input);
    requests.push(request);
    return Response.json({}, { status: request.method === "POST" ? 201 : 200 });
  };
}

describe("management entrypoint", () => {
  it("uses the producer-published database, branch, and backup routes", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      fetch: fixtureFetch(requests),
    });

    const listed = await client.databases.list();
    const created = await client.databases.create({ name: "primary", placement: ["GRU"], residency: null });
    const fetched = await client.databases.get("primary");
    const deleted = await client.databases.delete("primary", { confirm: true });
    const restored = await client.databases.restore("primary");
    const branches = await client.branches.list("primary");
    const branch = await client.branches.create("primary", { name: "branch" });
    const backups = await client.backups.listBackups("primary");
    const backupRestore = await client.backups.restoreBackup("primary", { name: "restored" });

    for (const result of [listed, created, fetched, deleted, restored, branches, branch, backups, backupRestore]) {
      expect(result.ok).toBe(true);
    }
    expect(requests.map(request => `${request.method} ${new URL(request.url).pathname}`)).toEqual([
      "GET /api/v1/databases",
      "POST /api/v1/databases",
      "GET /api/v1/databases/primary",
      "DELETE /api/v1/databases/primary",
      "POST /api/v1/databases/primary/restore",
      "GET /api/v1/databases/primary/branches",
      "POST /api/v1/databases/primary/branches",
      "GET /api/v1/databases/primary/backups",
      "POST /api/v1/databases/primary/backups/restore",
    ]);
    expect(JSON.parse(await requests[1]!.text())).toEqual({ name: "primary", placement: ["GRU"], residency: null });
    expect(JSON.parse(await requests[6]!.text())).toEqual({ name: "branch" });
    expect(JSON.parse(await requests[8]!.text())).toEqual({ name: "restored" });
  });

  it("fails closed before validating or sending unsupported management routes", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      fetch: fixtureFetch(requests),
    });

    expect(() => client.branches.getCapability()).toThrow(ManagementContractGapError);
    expect(() => client.branches.requestClone()).toThrow(ManagementContractGapError);
    expect(() => client.backups.configurePolicy("db", { fullBackupCron: "*" })).toThrow(ManagementContractGapError);
    expect(() => client.usage.getProject()).toThrow(ManagementContractGapError);
    expect(requests).toHaveLength(0);
  });

  it("normalizes and bounds residency locks", () => {
    expect(validatePlacementLock(["GRU", "FRA"])).toEqual(["GRU", "FRA"]);
    expect(validatePlacementLock("eu-only")).toBe("eu-only");
    expect(() => validatePlacementLock([])).toThrow("regions must contain between 1 and 20");
    expect(() => validatePlacementLock("unknown" as never)).toThrow("placement");
  });

  it("uses the producer-published collection and index routes", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      projectId: "project",
      fetch: fixtureFetch(requests),
    });

    const collections = await client.collections.list("db");
    const indexes = await client.collections.listIndexes("db", "users");
    const created = await client.collections.createIndex("db", "users", { field: "email" });
    const deleted = await client.collections.deleteIndex("db", "users", "email");
    expect(collections.ok).toBe(true);
    expect(indexes.ok).toBe(true);
    expect(created.ok).toBe(true);
    expect(deleted.ok).toBe(true);
    expect(requests.map(request => `${request.method} ${new URL(request.url).pathname}`)).toEqual([
      "GET /api/v1/databases/db/collections",
      "GET /api/v1/databases/db/collections/users/indexes",
      "POST /api/v1/databases/db/collections/users/indexes",
      "DELETE /api/v1/databases/db/collections/users/indexes/email",
    ]);
    expect(requests).toHaveLength(4);
  });
});

it("carries schema generation preconditions and a stable apply identity", async () => {
  const requests: Request[] = [];
  const client = createTerraScaleManagementClient({ fetch: fixtureFetch(requests) });
  const schema = { name: "users", fields: [], historyRetentionDays: 30, mergePolicy: "remove-wins" } as const;
  await client.schemas.put("db", "users", schema, { idempotencyKey: "apply-one", ifMatch: '"7"' });
  expect(requests[0]?.headers.get("if-match")).toBe('"7"');
  expect(requests[0]?.headers.get("idempotency-key")).toBe("apply-one");
  expect(await requests[0]?.json()).toEqual(schema);
  expect(() => client.schemas.put("db", "users", schema, { idempotencyKey: "" })).toThrow(TypeError);
  expect(() => client.schemas.put("db", "users", schema, { idempotencyKey: "key", ifMatch: 'W/"7"' })).toThrow(TypeError);
  expect(() => client.schemas.put("db", "other", schema, { idempotencyKey: "key" })).toThrow(TypeError);
  expect(requests).toHaveLength(1);
});

it("bounds recommendations and keeps export idempotency in the published request body", async () => {
  const requests: Request[] = [];
  const client = createTerraScaleManagementClient({ fetch: fixtureFetch(requests) });
  await client.collections.recommendations("db", "users", { limit: 64 });
  expect(new URL(requests[0]!.url).search).toBe("?limit=64");
  for (const limit of [0, 65, 1.5, NaN]) expect(() => client.collections.recommendations("db", "users", { limit })).toThrow(TypeError);
  await client.exports.create("db", { idempotency_key: "export-one" });
  expect(await requests[1]?.json()).toEqual({ idempotency_key: "export-one" });
  expect(requests[1]?.headers.has("idempotency-key")).toBe(false);
  expect(() => client.webhooks.create("db", { endpoint: "http://example.test/hook" })).toThrow(TypeError);
  expect(requests).toHaveLength(2);
});
