import { describe, expect, it } from "vitest";
import {
  createTerraScaleManagementClient,
  type IndexDescriptor,
  ManagementContractGapError,
  validatePlacementLock,
} from "./management.js";
import type { WebhookDelivery } from "./management-contracts.js";

const completeIndex = {
  field: "email",
  state: "building",
  backfill_remaining: 123n,
} satisfies IndexDescriptor;
// @ts-expect-error Producer responses require both state and backfill_remaining.
const incompleteIndex: IndexDescriptor = { field: "email" };
const completeDelivery = {
  id: "delivery-1",
  event_id: "event-1",
  revision: 1n,
  write_index: 2,
  retry_started_at: 3n,
  attempts: 1,
  next_attempt_at: 4n,
  status: null,
  delivered: false,
  terminal: false,
} satisfies WebhookDelivery;
const { write_index: _writeIndex, ...deliveryWithoutWriteIndex } = completeDelivery;
// @ts-expect-error Producer deliveries require write_index.
const incompleteDelivery: WebhookDelivery = deliveryWithoutWriteIndex;
void completeIndex;
void incompleteIndex;
void completeDelivery;
void incompleteDelivery;

function fixtureFetch(requests: Request[]): typeof fetch {
  return async (input) => {
    const request = new Request(input);
    requests.push(request);
    const body = request.method === "POST" && new URL(request.url).pathname === "/api/v1/databases"
      ? { created_at: 1, deleted: false, name: "primary", placement: "GRU", residency: "", parent: null }
      : {};
    return Response.json(body, { status: request.method === "POST" ? 201 : 200 });
  };
}

describe("management entrypoint", () => {
  it("lists project operations with bounded pagination and lossless revisions", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      projectId: "project-1",
      fetch: async input => {
        const request = new Request(input);
        requests.push(request);
        return new Response('{"operations":[{"operation_id":"op-1","kind":"database_create","database_id":"db-1","name":"primary","state":"running","stage":"accepted","created_at":"2026-09-06T12:00:00Z","revision":9223372036854775807,"cancellable":false,"cancellation_reason":"Provisioning has no safe cancellation action"}],"next_cursor":"cursor-1","covered_kinds":["database_create","branch_create"]}', {
          status: 200,
          headers: { "content-type": "application/json" },
        });
      },
    });

    const result = await client.operations.list({ after: "cursor-0", limit: 1 });
    expect(result.ok).toBe(true);
    if (result.ok) expect(result.value.operations[0]?.revision).toBe(9223372036854775807n);
    expect(new URL(requests[0]!.url).pathname).toBe("/api/v1/operations");
    expect(new URL(requests[0]!.url).search).toBe("?after=cursor-0&limit=1");
    expect(requests[0]!.headers.get("x-terrascale-project-id")).toBe("project-1");
  });

  it("bounds operation page sizes before sending", () => {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      fetch: fixtureFetch(requests),
    });
    for (const limit of [0, 51, 1.5, NaN]) expect(() => client.operations.list({ limit })).toThrow(TypeError);
    expect(requests).toHaveLength(0);
  });

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

it("rejects non-public or ambiguous webhook endpoints before issuing a request", () => {
  const requests: Request[] = [];
  const client = createTerraScaleManagementClient({ fetch: fixtureFetch(requests) });
  for (const endpoint of [
    "https://user:pass@example.test/hook",
    "https://example.test/hook?",
    "https://example.test/hook#fragment",
    "https://localhost/hook",
    "https://127.0.0.1/hook",
    "https://192.168.1.10/hook",
    "https://example..test/hook",
  ]) {
    expect(() => client.webhooks.create("db", { endpoint })).toThrow(TypeError);
  }
  expect(requests).toHaveLength(0);
});

it("cancels an in-flight export download through its typed options", async () => {
  const controller = new AbortController();
  let requestStarted: (() => void) | undefined;
  const started = new Promise<void>(resolve => { requestStarted = resolve; });
  const client = createTerraScaleManagementClient({
    fetch: async input => {
      const request = new Request(input);
      requestStarted!();
      return new Promise<Response>((_resolve, reject) => {
        request.signal.addEventListener("abort", () => reject(request.signal.reason), { once: true });
      });
    },
  });
  const pending = client.exports.download("db", "job", "manifest.json", { signal: controller.signal });
  await started;
  controller.abort();
  const result = await pending;
  expect(result.ok).toBe(false);
  if (!result.ok) expect(result.error.kind).toBe("transport");
});

it("returns typed export artifact metadata and rejects invalid headers", async () => {
  const bytes = new Uint8Array([1, 2, 3]);
  const client = createTerraScaleManagementClient({
    fetch: async () => new Response(bytes, {
      headers: {
        "content-type": "application/octet-stream",
        "x-terrascale-artifact-length": "3",
        "x-terrascale-artifact-checksum": "A".repeat(64),
      },
    }),
  });
  const result = await client.exports.download("db", "job", "member");
  expect(result.ok).toBe(true);
  if (result.ok) {
    expect(result.value.bytes).toEqual(bytes);
    expect(result.value.length).toBe(3);
    expect(result.value.checksum).toBe("a".repeat(64));
  }

  for (const headers of [
    { "x-terrascale-artifact-length": "03" },
    { "x-terrascale-artifact-length": "18446744073709551616" },
    { "x-terrascale-artifact-length": "3, 3" },
    { "x-terrascale-artifact-length": "4" },
    { "x-terrascale-artifact-length": "3", "x-terrascale-artifact-checksum": "0".repeat(64) + ", " + "0".repeat(64) },
  ]) {
    const invalid = createTerraScaleManagementClient({
      fetch: async () => new Response(bytes, { headers: { "content-type": "application/octet-stream", "x-terrascale-artifact-checksum": "0".repeat(64), ...headers } }),
    });
    const rejected = await invalid.exports.download("db", "job", "member");
    expect(rejected.ok).toBe(false);
  }
});

it("distinguishes accepted production provisioning from a ready database", async () => {
  const accepted = {
    environment: "production", tenant_id: "tenant-id", project_id: "project-id",
    operation_id: "create-one", database_id: "database-id", key_version: "key-version",
    name: "primary", regions: ["GRU"], residency: "br-only", parent_database_id: null, state: "pending",
  };
  const requests: Request[] = [];
  const client = createTerraScaleManagementClient({ fetch: async input => {
    requests.push(new Request(input));
    return Response.json(accepted, { status: 202 });
  } });
  const result = await client.databases.create({ name: "primary", placement: ["GRU"], residency: "br-only", operation_id: "create-one" });
  expect(result.ok).toBe(true);
  if (result.ok && result.status === 202) {
    expect(result.value.operation_id).toBe("create-one");
    expect(result.value.state).toBe("pending");
    expect("created_at" in result.value).toBe(false);
  } else throw new Error("expected pending provisioning");
  expect(await requests[0]?.json()).toEqual({ name: "primary", placement: ["GRU"], residency: "br-only", operation_id: "create-one" });
  expect(() => client.databases.create({ name: "primary", placement: ["GRU"], operation_id: "create-one" } as never)).toThrow(TypeError);
  const mismatched = createTerraScaleManagementClient({ fetch: async () => Response.json(accepted, { status: 201 }) });
  const rejected = await mismatched.databases.create({ name: "primary", placement: ["GRU"] });
  expect(rejected.ok).toBe(false);
  if (!rejected.ok) expect(rejected.error.kind).toBe("invalid-response");
  const incomplete = { ...accepted, project_id: undefined };
  const incompleteClient = createTerraScaleManagementClient({ fetch: async () => Response.json(incomplete, { status: 202 }) });
  const incompleteResult = await incompleteClient.databases.create({ name: "primary", placement: ["GRU"], residency: "br-only", operation_id: "create-one" });
  expect(incompleteResult.ok).toBe(false);
  if (!incompleteResult.ok) expect(incompleteResult.error.code).toBe("invalid_create_response");
});

it("requires exact branch confirmation and bounds admission faults before transport", async () => {
  const requests: Request[] = [];
  const client = createTerraScaleManagementClient({ fetch: fixtureFetch(requests) });
  const request = { confirmBranch: "branch", fault: "latency", durationMs: 1000, maxOperations: 1, delayMs: 10 } as const;
  expect(() => client.development.setFault("primary", request)).toThrow(TypeError);
  for (const changed of [{ durationMs: 30001 }, { maxOperations: 101 }, { delayMs: 1001 }, { fault: "unavailable" as const, delayMs: 1 }]) {
    expect(() => client.development.setFault("branch", { ...request, ...changed })).toThrow(TypeError);
  }
  expect(() => client.development.logs("branch", { limit: 101 })).toThrow(TypeError);
  expect(requests).toHaveLength(0);
});

it("retries webhook creation only with an explicit stable identity and never retries rotation", async () => {
  for (const operation of ["create", "keyed-create", "rotate"] as const) {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      maxRetryAttempts: 1, retryDelayMs: 0,
      fetch: async input => {
        requests.push(new Request(input));
        return Response.json({ error: { code: "unavailable", message: "key provider unavailable", fix: "retry when available" } }, { status: 503 });
      },
    });
    if (operation === "rotate") await client.webhooks.rotateSecret("db", "hook", { overlap_seconds: 300 });
    else await client.webhooks.create("db", { endpoint: "https://example.test/hooks" }, operation === "keyed-create" ? { idempotencyKey: "stable-one" } : {});
    expect(requests).toHaveLength(operation === "keyed-create" ? 2 : 1);
    expect(requests.map(request => request.headers.get("idempotency-key"))).toEqual(operation === "keyed-create" ? ["stable-one", "stable-one"] : [null]);
  }
});
