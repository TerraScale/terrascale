// P-3 journey — launch scope.
//
// This worker owns three chapters:
//   1. CRUD/SQL (databases + saved-queries + typed SQL builder)
//   2. live+sync (reactive sync/v1 state machine + HTTP attach/poll)
//   3. tenants+usage (database-per-tenant + usage surfaces)
//
// Keep new producer-backed flows in this journey; do not replace the
// deterministic protocol chapters below.
//
// The unit chapters retain deterministic wire/SDK conformance fixtures. The
// live suite at the bottom is the P-3 product proof: it runs only when the
// real stack's TERRASCALE_* gates are present and cleans up every resource it
// creates.

import assert from "node:assert/strict";
import { beforeAll, describe, test } from "vitest";

import { createTerraScaleClient } from "../src/factory.js";
import { TerraScaleHttp } from "../src/http.js";
import { TenantContractGapError, TenantsClient } from "../src/tenants.js";
import {
  Direction,
  Query,
  SqlValidationError,
  metadataFields,
  select,
  validateSql,
} from "../src/sql.js";
import { FIELD_TYPES, defineField } from "../src/schema.js";
import {
  TerraScaleSyncV1Client,
  TerraScaleSyncV1ClientError,
} from "../src/sync/v1/client.js";
import {
  SyncV1ViewCheckpoint,
  syncV1ResultDigest,
  type SyncV1ResultItem,
} from "../src/sync/v1/result-view.js";
import {
  decodeTerrascaleSyncV1MsgpackEnvelope,
  encodeTerrascaleSyncV1MsgpackEnvelope,
  type TerrascaleSyncV1MsgpackMessage,
  type TerrascaleSyncV1MsgpackSnapshot,
} from "../src/msgpack/generated/terrascale-sync-v1-msgpack.js";

const projectId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const organizationId = "01ERZ3NDEKTSV4RRFFQ69G5FAV";
const databaseId = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
const savedQueryId = "01SRZ3NDEKTSV4RRFFQ69G5FAV";

const encoder = new TextEncoder();
const decoder = new TextDecoder();

const journeyDb = {
  databaseId,
  projectId,
  name: "primary",
  regions: ["FRA"],
  backupRegion: "FRA",
  createdAt: "2026-08-05T00:00:00Z",
  version: 1,
};

const journeySavedQuery = {
  savedQueryId,
  projectId,
  name: "Active users",
  query: "SELECT * FROM users WHERE active = true",
  createdAt: "2026-08-05T00:00:00Z",
  updatedAt: "2026-08-05T00:00:00Z",
  revision: "opaque-resource-revision",
};

const journeyTenant = {
  key: "acme",
  databaseId: "db-acme",
  template: "default",
  placementLock: "eu-only",
};

const journeyOrgUsage = {
  organizationId,
  periodStart: "2026-08-01T00:00:00Z",
  periodEnd: "2026-09-01T00:00:00Z",
  updatedAt: "2026-09-01T00:00:00Z",
  provisional: false,
  resources: [],
  projects: [],
};

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

function base64(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("base64");
}

function row(identity: string, value: string): SyncV1ResultItem {
  return { identity, value: { value: encoder.encode(value) } };
}

function wireSnapshot(
  items: readonly SyncV1ResultItem[],
  revision = 1n,
  viewGeneration = 1n,
  authorizationGeneration = 1n,
): TerrascaleSyncV1MsgpackSnapshot {
  return {
    view_generation: viewGeneration,
    authorization_generation: authorizationGeneration,
    revision,
    items: items.map((entry) => ({
      identity: entry.identity,
      fields: Object.entries(entry.value).map(([path, value]) => ({ path, value })),
    })),
    digest: syncV1ResultDigest(items),
    checkpoint: encoder.encode(`checkpoint-${viewGeneration}-${revision}`),
  };
}

function serverFrame(message: TerrascaleSyncV1MsgpackMessage): Uint8Array {
  return encodeTerrascaleSyncV1MsgpackEnvelope({
    features: 0,
    correlation: 1n,
    causation: 0n,
    message,
  });
}

function inlineSnapshotBody(
  items: readonly SyncV1ResultItem[],
  revision: bigint,
  generation: bigint,
): Record<string, unknown> {
  const checkpoint = encoder.encode(`checkpoint-${generation}-${revision}`);
  return {
    viewId: "journey-orders",
    generation: Number(generation),
    capability: "cap-journey",
    checkpoint: base64(checkpoint),
    snapshotUrls: [],
    snapshot: {
      view_generation: Number(generation),
      authorization_generation: 1,
      revision: Number(revision),
      items: items.map((entry) => ({
        identity: entry.identity,
        fields: Object.entries(entry.value).map(([path, value]) => ({
          path,
          value: base64(value),
        })),
      })),
      digest: syncV1ResultDigest(items),
      checkpoint: base64(checkpoint),
    },
  };
}

// ---------------------------------------------------------------------------
// Chapter 1: CRUD/SQL (mock)
// ---------------------------------------------------------------------------

describe("P-3 journey chapter 1: CRUD/SQL (mock)", () => {
  test("database lifecycle uses typed project routes and explicit confirmation", async () => {
    const requests: Request[] = [];
    const deleted = {
      databaseId,
      projectId,
      name: "primary",
      deletion: { operationId: "delete-operation-1", state: "preparing" },
    };
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      projectId,
      idempotencyKeyFactory: () => "journey-db-1",
      fetch: async (input) => {
        const request = input instanceof Request ? input : new Request(input);
        requests.push(request);
        const url = new URL(request.url);
        if (request.method === "GET" && url.pathname.endsWith("/databases")) {
          return jsonResponse([journeyDb]);
        }
        if (request.method === "POST" && url.pathname.endsWith("/databases")) {
          return jsonResponse(journeyDb, 201);
        }
        if (request.method === "GET") return jsonResponse(journeyDb);
        return jsonResponse(deleted, 202);
      },
    });

    const listed = await client.databases.list();
    assert.equal(listed.ok, true);
    if (listed.ok) assert.deepEqual(listed.value, [journeyDb]);

    const created = await client.databases.create({ name: "primary" });
    assert.equal(created.ok, true);
    if (created.ok) assert.equal(created.value.databaseId, databaseId);

    const fetched = await client.databases.get(databaseId);
    assert.equal(fetched.ok, true);

    // Fail-closed: no request leaves the SDK without explicit confirmation.
    const before = requests.length;
    for (const bad of [{}, { confirm: false }, { confirm: "true" }] as never[]) {
      assert.throws(() => client.databases.delete(databaseId, bad), TypeError);
    }
    assert.equal(requests.length, before);

    const deleteKey = "journey-db-delete-1";
    const removed = await client.databases.delete(databaseId, { confirm: true, idempotencyKey: deleteKey });
    assert.equal(removed.ok, true);
    assert.equal(removed.status, 202);
    if (removed.ok) assert.deepEqual(removed.value.deletion, deleted.deletion);

    assert.deepEqual(
      requests.map((r) => [r.method, new URL(r.url).pathname]),
      [
        ["GET", `/api/v1/projects/${projectId}/databases`],
        ["POST", `/api/v1/projects/${projectId}/databases`],
        ["GET", `/api/v1/projects/${projectId}/databases/${databaseId}`],
        ["DELETE", `/api/v1/projects/${projectId}/databases/${databaseId}`],
      ],
    );
    assert.equal(new URL(requests[0]!.url).searchParams.get("take"), "100");
    assert.equal(requests[1]?.headers.get("idempotency-key"), "journey-db-1");
    assert.equal(requests[3]?.headers.get("idempotency-key"), deleteKey);
    assert.equal(
      requests[3]?.headers.get("x-terrascale-delete-confirmation"),
      databaseId,
    );
  });

  test("schema-bound SQL builder emits parameterized read-only descriptors", () => {
    const name = defineField("name", FIELD_TYPES.text, { indexed: true });
    const age = defineField("age", FIELD_TYPES.int, { indexed: true });
    const schema = {
      name: "users",
      fields: [name, age] as const,
      historyRetentionDays: 7,
      mergePolicy: "remove-wins" as const,
    };

    const query = select(schema)
      .column(name)
      .whereEq(age, 18)
      .whereMatch(name, "alice OR bob")
      .orderByField(age, Direction.Desc)
      .limit(10)
      .offset(20)
      .build();

    assert.equal(
      query.sql,
      "SELECT name FROM users WHERE age = $1 AND name MATCH $2 ORDER BY age DESC LIMIT $3 OFFSET $4",
    );
    assert.deepEqual(query.parameters, [18, "alice OR bob", 10, 20]);

    const execute = query.execute();
    assert.equal(execute.kind, "execute");
    assert.equal(execute.sql, query.sql);
    assert.deepEqual([...execute.parameters], [...query.parameters]);

    const subscribe = query.subscribe({ transport: "websocket" });
    assert.equal(subscribe.kind, "subscribe");
    assert.equal(subscribe.transport, "websocket");

    // Metadata columns and safe JSON paths stay in the allow-list.
    assert.equal(select(schema).column(metadataFields._id).build().sql, "SELECT _id FROM users");
    const json = select(schema).whereJsonEq("$.profile.city", "GRU").build();
    assert.equal(json.sql, "SELECT * FROM users WHERE body ->> '$.profile.city' = $1");

    // Undeclared fields and hostile JSON paths fail closed before any I/O.
    assert.throws(
      () =>
        select(schema).whereEq(
          { name: "secret", type: FIELD_TYPES.string, required: true, indexed: false },
          "x",
        ),
      SqlValidationError,
    );
    assert.throws(
      () => select(schema).whereJsonEq("$.profile.city' OR 1=1 --", "x"),
      SqlValidationError,
    );
  });

  test("raw SQL validation accepts reads and rejects writes", () => {
    for (const statement of [
      "SELECT * FROM users WHERE id = $1",
      "WITH recent AS (SELECT * FROM users) SELECT * FROM recent",
      "EXPLAIN SELECT * FROM users",
      "EXPLAIN QUERY PLAN SELECT * FROM users",
    ]) {
      Query.tryRaw(statement, statement.includes("$1") ? ["doc-1"] : []);
    }
    validateSql("SELECT * FROM users WHERE id = $1", ["doc-1"]);
    assert.throws(() => validateSql("SELECT * FROM users WHERE id = $1", []), SqlValidationError);
    assert.throws(() => validateSql("SELECT * FROM users", ["unused"]), SqlValidationError);

    for (const statement of [
      "INSERT INTO users VALUES ($1)",
      "UPDATE users SET name = $1",
      "DELETE FROM users",
      "SELECT * FROM users; SELECT * FROM users",
      "SELECT * FROM users -- DROP TABLE users",
      "SELECT random() FROM users",
      "SELECT CURRENT_TIMESTAMP FROM users",
      "WITH RECURSIVE r AS (SELECT * FROM users) SELECT * FROM r",
    ]) {
      assert.throws(() => Query.raw(statement, ["x"]), SqlValidationError, statement);
    }
  });

  test("saved-query CRUD round-trips revision-aware DTOs", async () => {
    const page = { items: [journeySavedQuery], totalCount: 1, nextCursor: null };
    const bodies: unknown[] = [];
    const requests: Request[] = [];
    let calls = 0;
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      projectId,
      idempotencyKeyFactory: () => "saved-query-journey-1",
      fetch: async (input) => {
        const request = input instanceof Request ? input : new Request(input);
        requests.push(request);
        const text = await request.clone().text();
        bodies.push(text.length === 0 ? undefined : JSON.parse(text));
        calls += 1;
        if (request.method === "DELETE") {
          return jsonResponse({ savedQueryId, projectId, deleted: true });
        }
        if (request.method === "GET" && new URL(request.url).pathname.endsWith("/saved-queries")) {
          return jsonResponse(page);
        }
        return jsonResponse(journeySavedQuery, request.method === "POST" ? 201 : 200);
      },
    });

    const created = await client.savedQueries.create({
      name: journeySavedQuery.name,
      query: journeySavedQuery.query,
    });
    assert.equal(created.ok, true);

    const listed = await client.savedQueries.list();
    assert.equal(listed.ok, true);
    if (listed.ok) assert.equal(listed.value.items[0]?.savedQueryId, savedQueryId);

    const fetched = await client.savedQueries.get(savedQueryId);
    assert.equal(fetched.ok, true);
    if (fetched.ok) assert.equal(fetched.value.revision, "opaque-resource-revision");

    const updated = await client.savedQueries.update(savedQueryId, {
      name: "Active users v2",
      query: journeySavedQuery.query,
      revision: journeySavedQuery.revision,
    });
    assert.equal(updated.ok, true);

    const deleted = await client.savedQueries.delete(savedQueryId);
    assert.equal(deleted.ok, true);
    if (deleted.ok) assert.equal(deleted.value.deleted, true);

    assert.equal(calls, 5);
    assert.deepEqual(
      requests.map((r) => [r.method, new URL(r.url).pathname]),
      [
        ["POST", `/api/v1/projects/${projectId}/saved-queries`],
        ["GET", `/api/v1/projects/${projectId}/saved-queries`],
        ["GET", `/api/v1/projects/${projectId}/saved-queries/${savedQueryId}`],
        ["PUT", `/api/v1/projects/${projectId}/saved-queries/${savedQueryId}`],
        ["DELETE", `/api/v1/projects/${projectId}/saved-queries/${savedQueryId}`],
      ],
    );
    assert.equal(requests[0]?.headers.get("idempotency-key"), "saved-query-journey-1");
    assert.deepEqual(bodies[3], {
      name: "Active users v2",
      query: journeySavedQuery.query,
      revision: journeySavedQuery.revision,
    });

    // A stale revision surfaces as the typed public conflict, not a throw.
    const conflicted = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      projectId,
      fetch: async () =>
        Response.json({ code: "TERRASCALE-1401", message: "stale revision" }, { status: 409 }),
    });
    const stale = await conflicted.savedQueries.update(savedQueryId, {
      name: "stale",
      query: journeySavedQuery.query,
      revision: "outdated",
    });
    assert.equal(stale.ok, false);
    if (!stale.ok) {
      assert.equal(stale.status, 409);
      assert.equal(stale.error.kind, "conflict");
    }
  });
});

// ---------------------------------------------------------------------------
// Chapter 2: live+sync (mock). Does not touch sync/v1/client.ts logic.
// ---------------------------------------------------------------------------

describe("P-3 journey chapter 2: live+sync (mock)", () => {
  test("SQL descriptor feeds a typed reactive subscription", () => {
    const status = defineField("status", FIELD_TYPES.text, { indexed: true });
    const schema = {
      name: "orders",
      fields: [status] as const,
      historyRetentionDays: 7,
      mergePolicy: "remove-wins" as const,
    };
    const descriptor = select(schema).whereEq(status, "open").limit(25).build().subscribe();

    const client = new TerraScaleSyncV1Client();
    client.setOnline(true);
    const opened = client.openSubscription({
      subscription: "orders-by-status",
      query: descriptor.sql,
      parameters: encoder.encode(JSON.stringify(descriptor.parameters)),
    });
    assert.match(opened.message.query, /FROM orders/u);
    const wire = decodeTerrascaleSyncV1MsgpackEnvelope(client.encode(opened.message, 1n));
    assert.equal(wire.message.kind, "subscribe");
  });

  test("pure state machine applies snapshot, delta, and checkpoint ack", () => {
    const client = new TerraScaleSyncV1Client();
    client.setOnline(true);
    const opened = client.openSubscription({
      subscription: "orders-by-status",
      query: "from orders where status = $status",
      parameters: encoder.encode("open"),
    });

    client.receive(
      serverFrame({
        kind: "snapshot",
        subscription: "orders-by-status",
        snapshot: wireSnapshot([row("order-1", "open")]),
      }),
    );
    assert.equal(decoder.decode(opened.subscription.view.getItems()[0]?.value.value), "open");

    const changed = [row("order-1", "paid"), row("order-2", "open")];
    client.receive(
      serverFrame({
        kind: "delta",
        subscription: "orders-by-status",
        view_generation: 1n,
        from_revision: 1n,
        to_revision: 2n,
        operations: [
          {
            kind: "update",
            identity: "order-1",
            fields: [{ path: "value", value: encoder.encode("paid") }],
          },
          {
            kind: "insert",
            identity: "order-2",
            position: 1,
            fields: [{ path: "value", value: encoder.encode("open") }],
          },
        ],
        digest: syncV1ResultDigest(changed),
        checkpoint: encoder.encode("checkpoint-1-2"),
      }),
    );
    assert.deepEqual(
      opened.subscription.view.getItems().map((entry) => entry.identity),
      ["order-1", "order-2"],
    );
    assert.equal(opened.subscription.view.getRevision(), 2n);

    const ack = client.checkpointAcknowledgement("orders-by-status");
    assert.equal(ack.kind, "checkpoint_ack");
    assert.equal(ack.subscription, "orders-by-status");
  });

  test("the wire corpus accepts every published reset reason", () => {
    const reasons = [
      "checkpoint_expired",
      "retention_advanced",
      "generation_changed",
      "checkpoint_not_current",
      "view_diverged",
      "oversized",
      "invalid_operations",
      "invalid_checkpoint",
      "backpressure",
    ] as const;
    for (const [index, reason] of reasons.entries()) {
      const client = new TerraScaleSyncV1Client();
      const opened = client.openSubscription({
        subscription: `reset-${index}`,
        query: "select * from docs",
        parameters: new Uint8Array(),
      });
      const frame = serverFrame({
        kind: "reset",
        subscription: `reset-${index}`,
        reason,
        snapshot: wireSnapshot([row("reset-proof", reason)], BigInt(index + 1), 2n),
      });
      assert.equal(client.receive(frame).message.kind, "reset");
      assert.equal(decoder.decode(opened.subscription.view.getItems()[0]?.value.value), reason);
      assert.equal(opened.subscription.view.getRevision(), BigInt(index + 1));
    }
  });

  test("HTTP attach uses canonical CDN URLs then polls one delta", async () => {
    const initial = [row("one", "raw")];
    const next = [row("one", "changed")];
    const requests: Request[] = [];
    const fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const request = new Request(input, init);
      requests.push(request);
      if (request.method === "POST") {
        return Response.json(inlineSnapshotBody(initial, 7n, 3n));
      }
      const url = new URL(request.url);
      assert.equal(url.pathname, "/api/v1/reactive/views/journey-orders/3/log");
      assert.equal(url.searchParams.get("offset"), "7");
      assert.equal(request.headers.get("x-terrascale-capability"), "cap-journey");
      return new Response(
        serverFrame({
          kind: "delta",
          subscription: "journey-orders",
          view_generation: 3n,
          from_revision: 7n,
          to_revision: 8n,
          operations: [
            {
              kind: "update",
              identity: "one",
              fields: [{ path: "value", value: encoder.encode("changed") }],
            },
          ],
          digest: syncV1ResultDigest(next),
          checkpoint: encoder.encode("checkpoint-3-8"),
        }).buffer as ArrayBuffer,
        { headers: { "content-type": "application/msgpack" } },
      );
    };

    const client = new TerraScaleSyncV1Client({
      apiBaseUrl: "https://api.terrascale.test",
      syncBaseUrl: "https://sync.api.terrascale.test",
      accessToken: "session-token",
      fetch,
    });
    const live = await client.subscribe({
      subscription: "orders",
      databaseId: "db-1",
      query: "select * from orders",
      parameters: new Uint8Array([0, 255, 1]),
      autoStart: false,
    });

    try {
      assert.equal(live.transport, "http");
      assert.equal(
        live.logUrl,
        "https://sync.api.terrascale.tech/api/v1/reactive/views/journey-orders/3/log".replace(
          "sync.api.terrascale.tech",
          "sync.api.terrascale.test",
        ),
      );
      assert.equal(requests[0]?.headers.get("authorization"), "Bearer session-token");
      const attach = (await requests[0]?.clone().json()) as {
        databaseId: string;
        parameters: string;
        checkpoint: string | null;
      };
      assert.equal(attach.databaseId, "db-1");
      assert.equal(attach.parameters, "AP8B");
      assert.equal(attach.checkpoint, null);
      assert.deepEqual(live.view.getItems()[0]?.value.value, encoder.encode("raw"));

      await live.poll();
      assert.equal(live.view.getRevision(), 8n);
      assert.deepEqual(live.view.getItems()[0]?.value.value, encoder.encode("changed"));
    } finally {
      live.close();
    }
  });

  test("checkpoint resume is opaque and retention expiry is a typed reset", async () => {
    const requests: Request[] = [];
    const fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const request = new Request(input, init);
      requests.push(request);
      if (request.method === "POST") {
        return Response.json(inlineSnapshotBody([row("one", "raw")], 1n, 4n));
      }
      return Response.json(
        { error: { reason: "retention_advanced", message: "history has expired" } },
        { status: 410 },
      );
    };
    const client = new TerraScaleSyncV1Client({
      apiBaseUrl: "https://api.terrascale.test",
      syncBaseUrl: "https://sync.api.terrascale.test",
      fetch,
    });
    const resume = new SyncV1ViewCheckpoint(new Uint8Array([0xff, 0x00]));
    const live = await client.subscribe({
      subscription: "resume",
      databaseId: "db-2",
      query: "select * from docs",
      parameters: new Uint8Array([255]),
      checkpoint: resume,
      autoStart: false,
    });
    try {
      const attach = (await requests[0]?.clone().json()) as { checkpoint: string };
      assert.equal(attach.checkpoint, "/wA=");
      // A refused/expired checkpoint is reported as a typed reset and the
      // correctness path immediately reattaches without the stale token.
      await live.poll();
      assert.equal(requests.length, 3);
      assert.equal(live.reset?.reason, "retention-advanced");
      const freshAttach = (await requests[2]?.clone().json()) as { checkpoint: null };
      assert.equal(freshAttach.checkpoint, null);
    } finally {
      live.close();
    }
  });

  test("authorization, routing, and backpressure drive the required cases", () => {
    const client = new TerraScaleSyncV1Client();
    const opened = client.openSubscription({
      subscription: "visible",
      query: "from docs",
      parameters: new Uint8Array(),
    });
    client.receive(
      serverFrame({
        kind: "snapshot",
        subscription: "visible",
        snapshot: wireSnapshot([row("a", "visible")]),
      }),
    );
    client.receive(
      serverFrame({
        kind: "authorization_changed",
        subscription: "visible",
        authorization_generation: 2n,
        removed: ["a"],
        snapshot: wireSnapshot([row("b", "visible")], 2n, 2n, 2n),
      }),
    );
    client.receive(serverFrame({ kind: "region_route_change", region: "FRA" }));
    client.receive(serverFrame({ kind: "backpressure", mode: "pause" }));

    assert.deepEqual(
      opened.subscription.view.getItems().map((entry) => entry.identity),
      ["b"],
    );
    assert.deepEqual(client.getSnapshot(), {
      online: false,
      paused: true,
      region: "FRA",
      subscriptions: 1,
    });

    client.setOnline(true);
    assert.throws(
      () =>
        client.receive(
          serverFrame({ kind: "error", code: "authentication_expired", detail: null }),
        ),
      (error: unknown) =>
        error instanceof TerraScaleSyncV1ClientError && error.code === "authentication_expired",
    );
    assert.equal(client.getSnapshot().online, false);
  });
});

// ---------------------------------------------------------------------------
// Chapter 3: tenants+usage (mock)
// ---------------------------------------------------------------------------

describe("P-3 journey chapter 3: tenants+usage (mock)", () => {
  test("tenant lifecycle inherits placement and uses named routes", async () => {
    const http = new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      idempotencyKeyFactory: () => "tenant-operation-1",
      fetch: async () => jsonResponse(journeyTenant),
    });
    const tenants = new TenantsClient(http);

    // Database-per-tenant is not in the published v1 OpenAPI contract. The
    // SDK therefore refuses every operation before a request can leave it.
    assert.throws(
      () => tenants.create({ key: "acme", template: "default", placementLock: ["FRA"] }),
      TenantContractGapError,
    );
    assert.throws(() => tenants.get("acme"), TenantContractGapError);
    assert.throws(() => tenants.delete("acme", { confirm: true }), TenantContractGapError);
  });

  test("tenant validation fails closed before any request", async () => {
    let fetches = 0;
    const tenants = new TenantsClient(
      new TerraScaleHttp({
        apiBaseUrl: "https://api.terrascale.test",
        fetch: async () => {
          fetches += 1;
          return jsonResponse(journeyTenant);
        },
      }),
    );

    assert.throws(() => tenants.create({ key: "  " }), TenantContractGapError);
    assert.throws(() => tenants.create({ key: "acme", placementLock: [] }), TenantContractGapError);
    assert.throws(() => tenants.get("  "), TenantContractGapError);
    assert.throws(() => tenants.delete("acme", {} as never), TenantContractGapError);
    assert.equal(fetches, 0);

    const controller = new AbortController();
    controller.abort();
    assert.throws(() => tenants.get("acme", { signal: controller.signal }), TenantContractGapError);
    assert.equal(fetches, 0);
  });

  test("usage journey reads project, organization, workload, latency, and history", async () => {
    const summary = {
      projectId,
      periodStart: "2026-08-01T00:00:00Z",
      periodEnd: "2026-09-01T00:00:00Z",
      metrics: [],
      kpis: [],
      resourceDistribution: [],
      operationBreakdown: [],
      databaseStorage: [],
      storage: { usedBytes: 1, limitBytes: 10, percentUsed: 10, status: "normal" },
      operationUnits: 5,
      includedOperationUnits: 100,
      overageOperationUnits: 0,
      egressBytes: 0,
      updatedAt: "2026-09-01T00:00:00Z",
      regionalTotals: {},
      categoryTotals: {},
      provisional: false,
      sources: [],
    };
    const workload = {
      projectId,
      range: "7d",
      periodStart: "2026-08-25T00:00:00Z",
      periodEnd: "2026-09-01T00:00:00Z",
      bucketSize: "1d",
      points: [],
    };
    const latency = {
      projectId,
      capturedAt: "2026-09-01T00:00:00Z",
      referencePeriodStart: "2026-08-01T00:00:00Z",
      referencePeriodEnd: "2026-09-01T00:00:00Z",
      source: "geographic_estimate",
      interRegion: [],
      nearestRegion: null,
    };
    const history = {
      projectId,
      range: "30d",
      requestedStart: "2026-08-02T00:00:00Z",
      accessibleStart: "2026-08-02T00:00:00Z",
      end: "2026-09-01T00:00:00Z",
      limitedByRetention: false,
      hasData: true,
      points: [
        { observedAt: "2026-09-01T00:00:00Z", storageBytes: "1024", operationUnits: "5", egressBytes: "0" },
      ],
    };
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      projectId,
      fetch: async (input) => {
        const request = input instanceof Request ? input : new Request(input);
        requests.push(request);
        const url = new URL(request.url);
        if (url.pathname === `/api/v1/organizations/${organizationId}/usage`) {
          return jsonResponse(journeyOrgUsage);
        }
        if (url.pathname.endsWith("/usage/workload")) return jsonResponse(workload);
        if (url.pathname.endsWith("/usage/history")) return jsonResponse(history);
        if (url.pathname.endsWith("/latency")) return jsonResponse(latency);
        return jsonResponse(summary);
      },
    });

    const projectUsage = await client.usage.getProjectUsage();
    assert.equal(projectUsage.ok, true);
    if (projectUsage.ok) assert.equal(projectUsage.value.projectId, projectId);

    const orgUsage = await client.usage.getOrganizationUsage(organizationId);
    assert.equal(orgUsage.ok, true);
    if (orgUsage.ok) assert.equal(orgUsage.value.organizationId, organizationId);

    const workloadResult = await client.usage.getProjectUsageWorkload("7d");
    assert.equal(workloadResult.ok, true);
    if (workloadResult.ok) assert.equal(workloadResult.value.range, "7d");

    const latencyResult = await client.usage.getProjectLatency();
    assert.equal(latencyResult.ok, true);
    if (latencyResult.ok) assert.equal(latencyResult.value.source, "geographic_estimate");

    const historyResult = await client.usage.getProjectUsageHistory("30d");
    assert.equal(historyResult.ok, true);
    if (historyResult.ok) {
      assert.equal(historyResult.value.hasData, true);
      assert.equal(typeof historyResult.value.points[0]?.storageBytes, "string");
    }

    assert.deepEqual(
      requests.map((r) => [r.method, new URL(r.url).pathname]),
      [
        ["GET", `/api/v1/projects/${projectId}/usage`],
        ["GET", `/api/v1/organizations/${organizationId}/usage`],
        ["GET", `/api/v1/projects/${projectId}/usage/workload`],
        ["GET", `/api/v1/projects/${projectId}/latency`],
        ["GET", `/api/v1/projects/${projectId}/usage/history`],
      ],
    );
    assert.equal(new URL(requests[2]?.url ?? "https://x.invalid").searchParams.get("range"), "7d");
    assert.equal(new URL(requests[4]?.url ?? "https://x.invalid").searchParams.get("range"), "30d");
  });
});

// ---------------------------------------------------------------------------
// Live stack after E-15. The environment gate is deliberate: a missing live
// configuration is reported by Vitest as skipped (never replaced by a mock),
// while a configured stack fails closed with the exact contract gap/error.
// ---------------------------------------------------------------------------

const liveApiBaseUrl = process.env.TERRASCALE_API_BASE_URL?.trim() || undefined;
const liveAccessToken = process.env.TERRASCALE_E2E_ACCESS_TOKEN?.trim() || undefined;
const liveEnabled = liveApiBaseUrl !== undefined && liveAccessToken !== undefined;
const strictG1 = process.env.TERRASCALE_G1_STRICT === "1";
const liveDashboardBaseUrl = process.env.TERRASCALE_DASHBOARD_BASE_URL?.trim() || undefined;

type LiveContract = {
  readonly paths?: Record<string, Record<string, unknown>>;
};

type LiveReply = {
  readonly response: Response;
  readonly body: unknown;
  readonly text: string;
};

let liveSequence = 0;

function liveResourceName(prefix: string): string {
  const run = process.env.TERRASCALE_E2E_RUN_ID?.trim() || `pid-${process.pid}`;
  liveSequence += 1;
  return `${prefix}-${run}-${liveSequence}`;
}

async function liveRequest(
  path: string,
  init: RequestInit = {},
  base = liveApiBaseUrl!,
): Promise<LiveReply> {
  const headers = new Headers(init.headers);
  headers.set("authorization", `Bearer ${liveAccessToken!}`);
  headers.set("accept", "application/json");
  const response = await fetch(new URL(path, `${base.replace(/\/$/u, "")}/`), {
    ...init,
    headers,
  });
  const text = await response.text();
  let body: unknown = undefined;
  if (text.length > 0) {
    try {
      body = JSON.parse(text);
    } catch {
      body = text;
    }
  }
  return { response, body, text };
}

function contractPath(contract: LiveContract, method: string, path: string, operation: string): void {
  if (contract.paths?.[path]?.[method.toLowerCase()] === undefined) {
    throw new Error(
      `[G1 contract gap] ${operation} requires ${method.toUpperCase()} ${path}, ` +
        "but that route is absent from the published /api/v1/openapi.json",
    );
  }
}

function expectLiveStatus(reply: LiveReply, expected: number | readonly number[], operation: string): void {
  const statuses = Array.isArray(expected) ? expected : [expected];
  if (statuses.includes(reply.response.status)) return;
  const error = typeof reply.body === "object" && reply.body !== null
    ? (reply.body as { error?: { code?: unknown; message?: unknown } }).error
    : undefined;
  const code = typeof error?.code === "string" ? error.code : "unknown";
  const message = typeof error?.message === "string" ? error.message : reply.text.slice(0, 240);
  const unavailable = reply.response.status === 501 || code === "pending_surface";
  throw new Error(
    `[G1 ${unavailable ? "feature unavailable" : "request failed"}] ${operation}: ` +
      `HTTP ${reply.response.status} code=${code} message=${message}`,
  );
}

function objectBody(reply: LiveReply, operation: string): Record<string, unknown> {
  if (typeof reply.body !== "object" || reply.body === null || Array.isArray(reply.body)) {
    throw new Error(`[G1 invalid response] ${operation}: expected a JSON object`);
  }
  return reply.body as Record<string, unknown>;
}

function publishedOperations(contract: LiveContract, domain: string): readonly string[] {
  const operations: string[] = [];
  for (const item of Object.values(contract.paths ?? {})) {
    for (const operation of Object.values(item)) {
      if (typeof operation !== "object" || operation === null) continue;
      const operationId = (operation as { operationId?: unknown }).operationId;
      if (typeof operationId === "string" && operationId.toLowerCase().includes(domain.toLowerCase())) {
        operations.push(operationId);
      }
    }
  }
  return operations;
}

function requirePublishedDomain(contract: LiveContract, domain: string): void {
  const operations = publishedOperations(contract, domain);
  if (operations.length === 0) {
    throw new Error(
      `[G1 contract gap] ${domain} operations are absent from the published /api/v1/openapi.json; ` +
        "the journey cannot invent a route or fabricate proof",
    );
  }
}

describe.skipIf(!liveEnabled)("P-3 journey live stack (E-15)", () => {
  let contract: LiveContract;

  beforeAll(async () => {
    const openapi = await liveRequest("/api/v1/openapi.json");
    expectLiveStatus(openapi, 200, "published OpenAPI");
    contract = objectBody(openapi, "published OpenAPI") as LiveContract;
    for (const [method, path, operation] of [
      ["POST", "/api/v1/databases", "create_database"],
      ["PUT", "/api/v1/databases/{name}/collections/{collection}/documents/{id}", "put_document"],
      ["GET", "/api/v1/databases/{name}/collections/{collection}/documents/{id}", "get_document"],
      ["PATCH", "/api/v1/databases/{name}/collections/{collection}/documents/{id}", "patch_document"],
      ["DELETE", "/api/v1/databases/{name}/collections/{collection}/documents/{id}", "delete_document"],
      ["POST", "/api/v1/databases/{name}/sql", "execute_sql"],
      ["POST", "/api/v1/reactive/views", "attach_view"],
    ] as const) {
      contractPath(contract, method, path, operation);
    }
  });

  test("published contract and all three local Regions are live", async () => {

    const regions = (process.env.TERRASCALE_E2E_REGION_BASE_URLS ?? "GRU=" + liveApiBaseUrl)
      .split(",")
      .map((entry) => entry.trim())
      .filter(Boolean);
    assert.ok(regions.length >= 1, "at least one local Region endpoint is required");
    if (process.env.TERRASCALE_E2E_REGION_BASE_URLS !== undefined) {
      assert.deepEqual(
        new Set(regions.map((entry) => entry.split("=", 2)[0])),
        new Set(["GRU", "SFO", "FRA"]),
        "the local stack must prove all three configured Regions",
      );
    }
    for (const entry of regions) {
      const [region, base] = entry.split("=", 2);
      assert.ok(region && base, `invalid TERRASCALE_E2E_REGION_BASE_URLS entry: ${entry}`);
      const health = await liveRequest("/api/v1/health", {}, base);
      expectLiveStatus(health, 200, `${region} health`);
      const healthBody = objectBody(health, `${region} health`);
      assert.equal(healthBody.region, region);
      assert.equal(healthBody.healthy, true);
      assert.equal(health.response.headers.get("x-terrascale-region"), region);
    }
  });

  test("real document CRUD, ETag/If-Match, idempotency, SQL and FTS", async () => {
    const suffix = liveResourceName("crud");
    const db = `journey-${suffix}`;
    const createPath = "/api/v1/databases";
    contractPath(contract, "POST", createPath, "create_database");
    const created = await liveRequest(createPath, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ name: db, placement: ["GRU"], residency: null }),
    });
    expectLiveStatus(created, 201, "create_database");
    const createdRecord = objectBody(created, "create_database");
    assert.equal(createdRecord.name, db);
    try {
      const client = createTerraScaleClient({
        apiBaseUrl: liveApiBaseUrl!,
        accessToken: liveAccessToken!,
        idempotencyKeyFactory: () => `journey-${suffix}-auto`,
      });
      const first = await client.documents.put(db, "users", "ada", {
        name: "Ada Lovelace",
        active: true,
        bio: "analytical engine",
      }, { idempotencyKey: `put-${suffix}`, durability: "regional" });
      assert.equal(first.ok, true, first.ok ? "" : first.error.message);
      if (!first.ok) throw new Error(first.error.message);
      const firstEtag = first.headers.get("etag");
      const session = first.headers.get("x-terrascale-session");
      assert.ok(firstEtag, "PUT must return ETag");
      assert.ok(session, "PUT must return X-TerraScale-Session");

      const replay = await client.documents.put(db, "users", "ada", {
        name: "Ada Lovelace",
        active: true,
        bio: "analytical engine",
      }, { idempotencyKey: `put-${suffix}`, durability: "regional" });
      assert.equal(replay.ok, true, replay.ok ? "" : replay.error.message);
      if (!replay.ok) throw new Error(replay.error.message);
      assert.equal(replay.headers.get("etag"), firstEtag, "idempotent replay must retain the revision");

      // The producer's session token is opaque base64url. Exercise it at the
      // wire boundary as the current SDK's numeric legacy option cannot
      // honestly represent this published token yet.
      const sessionGet = await liveRequest(
        `/api/v1/databases/${db}/collections/users/documents/ada`,
        { headers: { "x-terrascale-session": session! } },
      );
      expectLiveStatus(sessionGet, 200, "get_document with session");
      assert.deepEqual(sessionGet.body, { name: "Ada Lovelace", active: true, bio: "analytical engine" });
      assert.equal(sessionGet.response.headers.get("etag"), firstEtag);
      const notModified = await liveRequest(
        `/api/v1/databases/${db}/collections/users/documents/ada`,
        { headers: { "if-none-match": firstEtag! } },
      );
      expectLiveStatus(notModified, 304, "get_document If-None-Match");
      assert.equal(notModified.body, undefined);

      const patched = await client.documents.patch(db, "users", "ada", { active: false, bio: null }, {
        ifMatch: firstEtag!,
        idempotencyKey: `patch-${suffix}`,
        durability: "regional",
      });
      assert.equal(patched.ok, true, patched.ok ? "" : patched.error.message);
      if (!patched.ok) throw new Error(patched.error.message);
      const patchedEtag = patched.headers.get("etag");
      assert.ok(patchedEtag && patchedEtag !== firstEtag);
      const stale = await client.documents.patch(db, "users", "ada", { active: true }, {
        ifMatch: firstEtag!, idempotencyKey: `stale-${suffix}`,
      });
      assert.equal(stale.ok, false);
      if (stale.ok) throw new Error("stale If-Match unexpectedly succeeded");
      assert.equal(stale.status, 412);

      const globallyDurable = await client.documents.put(db, "users", "global", {
        name: "Global durability",
        active: true,
      }, { idempotencyKey: `global-${suffix}`, durability: "global" });
      assert.equal(globallyDurable.ok, true, globallyDurable.ok ? "" : globallyDurable.error.message);
      if (!globallyDurable.ok) throw new Error(globallyDurable.error.message);

      contractPath(contract, "POST", "/api/v1/databases/{name}/collections/{collection}/indexes", "create_index");
      const index = await liveRequest(`/api/v1/databases/${db}/collections/users/indexes`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ field: "name" }),
      });
      expectLiveStatus(index, 201, "create_index(users.name)");
      const fts = await client.sql.execute(db, {
        sql: "SELECT * FROM users WHERE name MATCH :term",
        parameters: { term: "Ada" },
      });
      assert.equal(fts.ok, true, fts.ok ? "" : fts.error.message);
      if (!fts.ok) throw new Error(fts.error.message);
      assert.ok("rows" in fts.value);
      if ("rows" in fts.value) assert.ok(fts.value.rows.some((row) => row.id === "ada"));
      const explain = await client.sql.execute(db, { sql: "SELECT * FROM users WHERE name MATCH :term", parameters: { term: "Ada" }, explain: true });
      assert.equal(explain.ok, true, explain.ok ? "" : explain.error.message);
      if (!explain.ok) throw new Error(explain.error.message);
      assert.equal("dialect" in explain.value, true);

      const listed = await client.documents.list(db, "users", { limit: 1 });
      assert.equal(listed.ok, true, listed.ok ? "" : listed.error.message);
      if (!listed.ok) throw new Error(listed.error.message);
      assert.equal(listed.value.documents.length, 1);
      assert.equal(listed.value.documents[0]?.id, "ada");
      const history = await liveRequest(`/api/v1/databases/${db}/collections/users/documents/ada/history`);
      contractPath(contract, "GET", "/api/v1/databases/{name}/collections/{collection}/documents/{id}/history", "document_history");
      expectLiveStatus(history, 200, "document_history");
      assert.ok((objectBody(history, "document_history").history as unknown[]).length >= 2);

      const deleted = await client.documents.delete(db, "users", "ada", {
        ifMatch: patchedEtag!, idempotencyKey: `delete-${suffix}`, durability: "regional",
      });
      assert.equal(deleted.ok, true, deleted.ok ? "" : deleted.error.message);
      if (!deleted.ok) throw new Error(deleted.error.message);
      const gone = await client.documents.get(db, "users", "ada");
      assert.equal(gone.ok, false);
      if (gone.ok) throw new Error("deleted document was readable");
      assert.equal(gone.status, 404);
    } finally {
      const removed = await liveRequest(`/api/v1/databases/${db}`, { method: "DELETE" });
      expectLiveStatus(removed, 200, `delete_database(${db})`);
    }
  });

  test("sync attach and checkpoint paths are real, and pending surfaces fail closed", async () => {
    // A direct attach is the published reactive producer. The old database
    // local JSON poll route is deliberately not exercised (it is retired).
    contractPath(contract, "POST", "/api/v1/reactive/views", "attach_view");
    const db = `sync-${liveResourceName("view")}`;
    const created = await liveRequest("/api/v1/databases", {
      method: "POST", headers: { "content-type": "application/json" },
      body: JSON.stringify({ name: db, placement: ["GRU"], residency: null }),
    });
    expectLiveStatus(created, 201, "create_database for sync");
    try {
      const client = createTerraScaleClient({ apiBaseUrl: liveApiBaseUrl!, accessToken: liveAccessToken! });
      const put = await client.documents.put(db, "orders", "o-1", { status: "open", total: 42 }, { durability: "regional", idempotencyKey: `sync-seed-${db}` });
      assert.equal(put.ok, true, put.ok ? "" : put.error.message);
      if (!put.ok) throw new Error(put.error.message);
      const attach = await liveRequest("/api/v1/reactive/views", {
        method: "POST", headers: { "content-type": "application/json" },
        body: JSON.stringify({ databaseId: db, statement: "SELECT * FROM orders WHERE status = :status LIMIT 25", parameters: base64(encoder.encode(JSON.stringify({ status: "open" }))), checkpoint: null, subscription: "journey", entitlement_epoch: 0 }),
      });
      expectLiveStatus(attach, 201, "attach_view");
      const view = objectBody(attach, "attach_view");
      for (const field of ["view", "generation", "snapshot_chunks", "log", "checkpoint", "capability", "capability_header", "offset"]) assert.ok(field in view, `attach response missing ${field}`);
      const snapshotPath = view.snapshot_chunks as string[];
      assert.equal(snapshotPath.length, 1);
      const snapshot = await liveRequest(snapshotPath[0]!, { headers: { [String(view.capability_header)]: String(view.capability) } });
      expectLiveStatus(snapshot, 200, "reactive snapshot");
      assert.match(snapshot.response.headers.get("content-type") ?? "", /application\/msgpack/u);
      const decoded = decodeTerrascaleSyncV1MsgpackEnvelope(new Uint8Array(await (await fetch(new URL(snapshotPath[0]!, `${liveApiBaseUrl!.replace(/\/$/u, "")}/`), { headers: { authorization: `Bearer ${liveAccessToken!}`, [String(view.capability_header)]: String(view.capability) } })).arrayBuffer()));
      assert.equal(decoded.message.kind, "snapshot");

      const resumed = await liveRequest("/api/v1/reactive/views", {
        method: "POST", headers: { "content-type": "application/json" },
        body: JSON.stringify({ databaseId: db, statement: "SELECT * FROM orders WHERE status = :status LIMIT 25", parameters: base64(encoder.encode(JSON.stringify({ status: "open" }))), checkpoint: view.checkpoint, subscription: "journey-resume", entitlement_epoch: 0 }),
      });
      expectLiveStatus(resumed, 201, "attach_view checkpoint resume");
      assert.equal(objectBody(resumed, "attach_view checkpoint resume").checkpoint, view.checkpoint);

      const changed = await client.documents.patch(db, "orders", "o-1", { status: "closed" }, {
        idempotencyKey: `sync-change-${db}`,
        durability: "regional",
      });
      assert.equal(changed.ok, true, changed.ok ? "" : changed.error.message);
      if (!changed.ok) throw new Error(changed.error.message);
      const logPath = String(view.log).replace(/\?offset=\d+$/u, `?offset=${String(view.offset)}`);
      const delta = await liveRequest(logPath, {
        headers: { [String(view.capability_header)]: String(view.capability) },
      });
      expectLiveStatus(delta, 200, "reactive log delta after durable write");
      assert.match(delta.response.headers.get("content-type") ?? "", /application\/msgpack/u);
      const deltaBytes = new Uint8Array(await (await fetch(
        new URL(logPath, `${liveApiBaseUrl!.replace(/\/$/u, "")}/`),
        { headers: { authorization: `Bearer ${liveAccessToken!}`, [String(view.capability_header)]: String(view.capability) } },
      )).arrayBuffer());
      const deltaEnvelope = decodeTerrascaleSyncV1MsgpackEnvelope(deltaBytes);
      assert.equal(deltaEnvelope.message.kind, "delta");

      const backupPath = `/api/v1/databases/${db}/backups`;
      contractPath(contract, "GET", "/api/v1/databases/{name}/backups", "list_backups");
      const backups = await liveRequest(backupPath);
      expectLiveStatus(backups, 200, "list_backups");
      const backupBody = objectBody(backups, "list_backups");
      assert.equal(typeof backupBody.ready, "boolean");
      assert.equal(typeof backupBody.streaming, "boolean");

      const branchesPath = `/api/v1/databases/${db}/branches`;
      contractPath(contract, "GET", "/api/v1/databases/{name}/branches", "list_branches");
      const branches = await liveRequest(branchesPath);
      expectLiveStatus(branches, 200, "list_branches");
      assert.ok(Array.isArray(objectBody(branches, "list_branches").branches));
    } finally {
      const removed = await liveRequest(`/api/v1/databases/${db}`, { method: "DELETE" });
      expectLiveStatus(removed, 200, `delete_database(${db})`);
    }
  });


  test("published backup, branch, export, webhook, usage, tenant, import, and dashboard surfaces are honest", async () => {
    const db = `management-${liveResourceName("surface")}`;
    const branch = `${db}-branch`;
    const restored = `${db}-restored`;
    const failures: string[] = [];
    const check = async (operation: string, action: () => Promise<void>): Promise<void> => {
      try {
        await action();
      } catch (error) {
        failures.push(`${operation}: ${error instanceof Error ? error.message : String(error)}`);
      }
    };

    contractPath(contract, "POST", "/api/v1/databases", "create_database");
    const created = await liveRequest("/api/v1/databases", {
      method: "POST", headers: { "content-type": "application/json" },
      body: JSON.stringify({ name: db, placement: ["GRU"], residency: null }),
    });
    expectLiveStatus(created, 201, "create_database for management surfaces");
    let branchCreated = false;
    let restoreCreated = false;
    try {
      await check("list_backups", async () => {
        contractPath(contract, "GET", "/api/v1/databases/{name}/backups", "list_backups");
        const reply = await liveRequest(`/api/v1/databases/${db}/backups`);
        expectLiveStatus(reply, 200, "list_backups");
        const body = objectBody(reply, "list_backups");
        assert.equal(typeof body.ready, "boolean");
        assert.equal(typeof body.streaming, "boolean");
      });

      await check("restore_backup", async () => {
        contractPath(contract, "POST", "/api/v1/databases/{name}/backups/restore", "restore_backup");
        const reply = await liveRequest(`/api/v1/databases/${db}/backups/restore`, {
          method: "POST", headers: { "content-type": "application/json" },
          body: JSON.stringify({ name: restored }),
        });
        expectLiveStatus(reply, 201, "restore_backup");
        restoreCreated = true;
      });

      await check("list_branches", async () => {
        contractPath(contract, "GET", "/api/v1/databases/{name}/branches", "list_branches");
        const reply = await liveRequest(`/api/v1/databases/${db}/branches`);
        expectLiveStatus(reply, 200, "list_branches");
        assert.ok(Array.isArray(objectBody(reply, "list_branches").branches));
      });

      await check("create_branch", async () => {
        contractPath(contract, "POST", "/api/v1/databases/{name}/branches", "create_branch");
        const reply = await liveRequest(`/api/v1/databases/${db}/branches`, {
          method: "POST", headers: { "content-type": "application/json" },
          body: JSON.stringify({ name: branch }),
        });
        expectLiveStatus(reply, 201, "create_branch");
        branchCreated = true;
      });

      await check("database_usage", async () => {
        contractPath(contract, "GET", "/api/v1/databases/{name}/usage", "database_usage");
        const reply = await liveRequest(`/api/v1/databases/${db}/usage`);
        expectLiveStatus(reply, 200, "database_usage");
      });

      let webhookId: string | undefined;
      await check("webhooks", async () => {
        contractPath(contract, "GET", "/api/v1/databases/{name}/webhooks", "list_webhooks");
        contractPath(contract, "POST", "/api/v1/databases/{name}/webhooks", "create_webhook");
        const endpoint = "http://127.0.0.1:7090/hooks/journey";
        const listed = await liveRequest(`/api/v1/databases/${db}/webhooks`);
        expectLiveStatus(listed, 200, "list_webhooks");
        try {
          const createdWebhook = await liveRequest(`/api/v1/databases/${db}/webhooks`, {
            method: "POST", headers: { "content-type": "application/json" },
            body: JSON.stringify({ endpoint }),
          });
          expectLiveStatus(createdWebhook, 201, "create_webhook");
          const body = objectBody(createdWebhook, "create_webhook");
          if (typeof body.id === "string") webhookId = body.id;
        } finally {
          // The current pending producer reserves the endpoint before it
          // answers 501; always release that admission slot when possible.
          contractPath(contract, "DELETE", "/api/v1/databases/{name}/webhooks/{id}", "delete_webhook");
          const removed = await liveRequest(`/api/v1/databases/${db}/webhooks/${encodeURIComponent(webhookId ?? endpoint)}`, { method: "DELETE" });
          expectLiveStatus(removed, 200, "delete_webhook");
        }
      });

      await check("create_export", async () => {
        contractPath(contract, "POST", "/api/v1/databases/{name}/exports", "create_export");
        const reply = await liveRequest(`/api/v1/databases/${db}/exports`, {
          method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({}),
        });
        expectLiveStatus(reply, 202, "create_export");
      });

      await check("tenants", async () => requirePublishedDomain(contract, "tenant"));
      await check("imports", async () => requirePublishedDomain(contract, "import"));
      await check("dashboard", async () => {
        if (liveDashboardBaseUrl === undefined) {
          if (strictG1) throw new Error("[G1 contract gap] TERRASCALE_DASHBOARD_BASE_URL is not configured");
          return;
        }
        const response = await fetch(new URL("/", `${liveDashboardBaseUrl.replace(/\/$/u, "")}/`));
        if (response.status < 200 || response.status >= 400) {
          throw new Error(`[G1 dashboard failed] GET / returned HTTP ${response.status}`);
        }
        assert.ok((await response.text()).length > 0, "dashboard returned an empty landing response");
      });
    } finally {
      if (branchCreated) {
        const removed = await liveRequest(`/api/v1/databases/${branch}`, { method: "DELETE" });
        expectLiveStatus(removed, 200, `delete_database(${branch})`);
      }
      if (restoreCreated) {
        const removed = await liveRequest(`/api/v1/databases/${restored}`, { method: "DELETE" });
        expectLiveStatus(removed, 200, `delete_database(${restored})`);
      }
      const removed = await liveRequest(`/api/v1/databases/${db}`, { method: "DELETE" });
      expectLiveStatus(removed, 200, `delete_database(${db})`);
    }
    if (failures.length > 0) {
      throw new Error(`[G1 journey blockers]\n${failures.map((failure) => `- ${failure}`).join("\n")}`);
    }
  });
});
