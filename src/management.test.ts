import { describe, expect, it } from "vitest";
import {
  createTerraScaleManagementClient,
  type IndexDescriptor,
  ManagementContractGapError,
  validatePlacementLock,
} from "./management.js";
import type { SchemaDocument, WebhookDelivery } from "./management-contracts.js";

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
const rollingBackMigration = {
  operation_id: "migration-1",
  state: "rolling_back",
  steps: [],
  total: 10,
  completed: 4,
  next_cursor: "cursor-4",
  message: "Restoring the previous schema version.",
} satisfies NonNullable<SchemaDocument["migration"]>;
const { write_index: _writeIndex, ...deliveryWithoutWriteIndex } = completeDelivery;
// @ts-expect-error Producer deliveries require write_index.
const incompleteDelivery: WebhookDelivery = deliveryWithoutWriteIndex;
void completeIndex;
void incompleteIndex;
void completeDelivery;
void incompleteDelivery;
void rollingBackMigration;

function fixtureFetch(requests: Request[]): typeof fetch {
  return async (input) => {
    const request = new Request(input);
    requests.push(request);
    const body = request.method === "POST" && new URL(request.url).pathname === "/api/v1/databases"
      ? { created_at: 1, deleted: false, name: "primary", placement: "GRU", residency: "", parent: null }
      : request.method === "POST" && (new URL(request.url).pathname.endsWith("/backups/restore") || new URL(request.url).pathname.endsWith("/branches"))
        ? { environment: "production", tenant_id: "tenant-1", project_id: "project-1", operation_id: "op-restore", database_id: "db-restore", key_version: "key-1", name: "restored", regions: ["GRU"], residency: "br-only", parent_database_id: "primary", state: "accepted" }
      : {};
    return Response.json(body, { status: request.method === "POST" && (new URL(request.url).pathname.endsWith("/backups/restore") || new URL(request.url).pathname.endsWith("/branches")) ? 202 : request.method === "POST" ? 201 : 200 });
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

  it("parses the canonical operation GET as an OperationSummary", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      fetch: async input => {
        requests.push(new Request(input));
        return new Response('{"operation_id":"op-1","kind":"database_create","database_id":"db-1","name":"primary","state":"running","stage":"accepted","created_at":"2026-09-06T12:00:00Z","revision":18446744073709551615,"cancellable":false,"cancellation_reason":"Provisioning has no safe cancellation action"}', {
          status: 200,
          headers: { "content-type": "application/json" },
        });
      },
    });

    const result = await client.operations.get("op-1");
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.value.kind).toBe("database_create");
      expect(result.value.stage).toBe("accepted");
      expect(result.value.revision).toBe(18446744073709551615n);
    }
    expect(new URL(requests[0]!.url).pathname).toBe("/api/v1/operations/op-1");
  });

  it("preserves omitted metadata and the maximum producer u64 revision", async () => {
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      fetch: async () => new Response(
        '{"operations":[{"operation_id":"op-2","kind":"recovery","database_id":"db-2","name":"db-2","state":"running","stage":"capturing","cancellable":false,"cancellation_reason":"Native recovery has no safe cancellation action"},{"operation_id":"op-3","kind":"database_create","database_id":"db-3","name":"db-3","state":"succeeded","stage":"ready","created_at":"2026-09-06T12:00:00Z","revision":18446744073709551615,"cancellable":false,"cancellation_reason":"Provisioning has no safe cancellation action"}],"covered_kinds":["export","recovery"]}',
        { status: 200, headers: { "content-type": "application/json" } },
      ),
    });

    const result = await client.operations.list();
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.value.operations[0]).not.toHaveProperty("created_at");
      expect(result.value.operations[0]).not.toHaveProperty("revision");
      expect(result.value.operations[1]?.revision).toBe(18446744073709551615n);
      expect(result.value.next_cursor).toBeUndefined();
    }
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
    const deleted = await client.databases.delete("primary", { confirm: true, idempotencyKey: "delete-primary" });
    const restored = await client.databases.restore("primary");
    const branches = await client.branches.list("primary");
    const branch = await client.branches.create("primary", { name: "branch", operation_id: "op-branch" });
    const backups = await client.backups.listBackups("primary");
    const backupRestore = await client.backups.restoreBackup("primary", { name: "restored", operation_id: "op-restore" });

    for (const result of [listed, created, fetched, deleted, restored, branches, branch, backups, backupRestore]) {
      expect(result.ok).toBe(true);
    }
    expect(backupRestore.ok && backupRestore.status).toBe(202);
    expect(backupRestore.ok && backupRestore.value.operation_id).toBe("op-restore");
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
    expect(JSON.parse(await requests[6]!.text())).toEqual({ name: "branch", operation_id: "op-branch" });
    expect(JSON.parse(await requests[8]!.text())).toEqual({ name: "restored", operation_id: "op-restore" });
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

it("publishes the producer import lifecycle routes and binary upload body", async () => {
  const requests: Request[] = [];
  const client = createTerraScaleManagementClient({
    apiBaseUrl: "https://api.example.test",
    fetch: async input => {
      const request = new Request(input);
      requests.push(request);
      const pathname = new URL(request.url).pathname;
      return Response.json(pathname.endsWith("/imports") && request.method === "GET" ? [] : {
        committed_records: 0, cursor: 0, database_id: "db", durability: "regional", error: null,
        format: "jsonl", input_committed: false, input_digest: null, input_length: null,
        operation_id: "import-1", pending_cursor: null, pending_operation: null, pending_records: null,
        project_id: "project", state: "uploading", tenant_id: "tenant",
      }, { status: request.method === "POST" && pathname.endsWith("/imports") ? 202 : 200 });
    },
  });

  await client.imports.list("db");
  await client.imports.create("db", { format: "jsonl", idempotency_key: "import-one" });
  await client.imports.get("db", "import-1");
  await client.imports.upload("db", "import-1", new Uint8Array([1, 2, 3]));
  await client.imports.resume("db", "import-1");

  expect(requests.map(request => `${request.method} ${new URL(request.url).pathname}`)).toEqual([
    "GET /api/v1/databases/db/imports",
    "POST /api/v1/databases/db/imports",
    "GET /api/v1/databases/db/imports/import-1",
    "PUT /api/v1/databases/db/imports/import-1/input",
    "POST /api/v1/databases/db/imports/import-1/resume",
  ]);
  expect(await requests[1]!.json()).toEqual({ format: "jsonl", idempotency_key: "import-one" });
  expect(requests[3]!.headers.get("content-type")).toBe("application/octet-stream");
  expect(new Uint8Array(await requests[3]!.arrayBuffer())).toEqual(new Uint8Array([1, 2, 3]));
});

it("preserves import uint64 fields above the JavaScript safe integer range", async () => {
  const value = "9007199254740993";
  const body = `{"committed_records":${value},"cursor":${value},"database_id":"db","durability":"regional","error":null,"format":"jsonl","input_committed":true,"input_digest":null,"input_length":${value},"operation_id":"import-1","pending_cursor":${value},"pending_operation":null,"pending_records":${value},"project_id":"project","state":"complete","tenant_id":"tenant"}`;
  const client = createTerraScaleManagementClient({
    apiBaseUrl: "https://api.example.test",
    fetch: async input => {
      const request = new Request(input);
      return new Response(request.method === "GET" && new URL(request.url).pathname.endsWith("/imports") ? `[${body}]` : body, {
        status: request.method === "POST" && new URL(request.url).pathname.endsWith("/imports") ? 202 : 200,
        headers: { "content-type": "application/json" },
      });
    },
  });

  const results = await Promise.all([
    client.imports.list("db"),
    client.imports.create("db", { format: "jsonl", idempotency_key: "import-1" }),
    client.imports.get("db", "import-1"),
    client.imports.upload("db", "import-1", new Uint8Array([1])),
    client.imports.resume("db", "import-1"),
  ]);

  for (const result of results) {
    expect(result.ok).toBe(true);
    if (!result.ok) continue;
    const job = Array.isArray(result.value) ? result.value[0] : result.value;
    expect(job?.committed_records).toBe(9007199254740993n);
    expect(job?.cursor).toBe(9007199254740993n);
    expect(job?.input_length).toBe(9007199254740993n);
    expect(job?.pending_cursor).toBe(9007199254740993n);
    expect(job?.pending_records).toBe(9007199254740993n);
  }
});

it("rejects an empty import upload before making a request", () => {
  const client = createTerraScaleManagementClient({
    apiBaseUrl: "https://api.example.test",
    fetch: async () => { throw new Error("fetch must not run"); },
  });

  expect(() => client.imports.upload("db", "import-1", new Uint8Array())).toThrow("input must not be empty");
});

it("carries schema generation preconditions and a stable apply identity", async () => {
  const requests: Request[] = [];
  // Pins the legacy ADR 0043 transport path; discovery has its own suites.
  const client = createTerraScaleManagementClient({ fetch: fixtureFetch(requests), routing: { enabled: false } });
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

it("preserves the published rolling_back schema migration state", async () => {
  const client = createTerraScaleManagementClient({
    fetch: async () => Response.json({
      generation: 1,
      schema_version: 2,
      schema: { name: "users", fields: [] },
      migration: rollingBackMigration,
    }),
  });

  const result = await client.schemas.get("db", "users");
  expect(result.ok).toBe(true);
  if (result.ok) expect(result.value.migration?.state).toBe("rolling_back");
});

it("rolls back a schema migration with the required identity and generation preconditions", async () => {
  const requests: Request[] = [];
  const client = createTerraScaleManagementClient({
    fetch: async input => {
      const request = new Request(input);
      requests.push(request);
      return Response.json({ generation: 2, schema_version: 1, schema: { name: "users", fields: [] } }, { status: 202 });
    },
  });

  const admitted = await client.schemas.rollback("db", "users", { idempotencyKey: "rollback-one", ifMatch: '"7"' });
  expect(admitted.ok).toBe(true);
  if (admitted.ok) expect(admitted.status).toBe(202);
  expect(new URL(requests[0]!.url).pathname).toBe("/api/v1/databases/db/collections/users/schema/migration/rollback");
  expect(requests[0]?.method).toBe("POST");
  expect(requests[0]?.headers.get("if-match")).toBe('"7"');
  expect(requests[0]?.headers.get("idempotency-key")).toBe("rollback-one");
  expect(await requests[0]?.text()).toBe("");
  expect(() => client.schemas.rollback("db", "users", { idempotencyKey: "", ifMatch: '"7"' })).toThrow(TypeError);
  expect(() => client.schemas.rollback("db", "users", { idempotencyKey: "key", ifMatch: "7" })).toThrow(TypeError);
  expect(() => client.schemas.rollback("db", "users", { idempotencyKey: "key", ifMatch: 'W/"7"' })).toThrow(TypeError);
  expect(requests).toHaveLength(1);
});

it("reads an authorization lease with lossless int64 timestamps and no auth requirement", async () => {
  const requests: Request[] = [];
  const client = createTerraScaleManagementClient({
    fetch: async input => {
      const request = new Request(input);
      requests.push(request);
      return new Response(
        '{"version":1,"tenantId":"tenant-1","epoch":"epoch-1","featureEpoch":"feature-epoch-1","audience":"producer","region":"GRU","issuedAt":9007199254740993,"validUntil":9223372036854775807,"keyId":"key-1","signature":"signature"}',
        { headers: { "content-type": "application/json" } },
      );
    },
  });

  const result = await client.authorization.getLease("tenant-1");
  expect(result.ok).toBe(true);
  if (result.ok) {
    expect(result.value.issuedAt).toBe(9007199254740993n);
    expect(result.value.validUntil).toBe(9223372036854775807n);
    expect(result.value.version).toBe(1);
    expect(result.value.signature).toBe("signature");
  }
  expect(new URL(requests[0]!.url).pathname).toBe("/api/v1/authorization/leases/tenant-1");
  expect(requests[0]?.headers.has("authorization")).toBe(false);
  expect(() => client.authorization.getLease(" ")).toThrow(TypeError);
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

it("uses per-call project scope and forwards management pagination", async () => {
  const requests: Request[] = [];
  const client = createTerraScaleManagementClient({
    projectId: "configured-project",
    // Pins the legacy ADR 0043 transport path; discovery has its own suites.
    routing: { enabled: false },
    fetch: async input => { requests.push(new Request(input)); return Response.json({ databases: [] }); },
  });
  await client.databases.list("call-project", { skip: 5, take: 10 });
  await client.backups.listBackups("db", { skip: 2, take: 3 }, "backup-project");
  expect(requests[0]!.headers.get("x-terrascale-project-id")).toBe("call-project");
  expect(new URL(requests[0]!.url).search).toBe("?skip=5&take=10");
  expect(requests[1]!.headers.get("x-terrascale-project-id")).toBe("backup-project");
  expect(new URL(requests[1]!.url).search).toBe("?skip=2&take=3");
});

it("rejects private IPv6 webhook destinations", async () => {
  const client = createTerraScaleManagementClient({ fetch: async () => Response.json({}) });
  for (const endpoint of [
    "https://[::1]/hook",
    "https://[fc00::1]/hook",
    "https://[fe80::1]/hook",
    "https://[::ffff:127.0.0.1]/hook",
  ]) {
    expect(() => client.webhooks.create("db", { endpoint })).toThrow("host is not public");
  }
});
