import assert from "node:assert/strict";
import { describe, test } from "vitest";
import { createTerraScaleClient } from "./factory.js";

import {
  decodeTerrascaleSyncV1MsgpackEnvelope,
  encodeTerrascaleSyncV1MsgpackEnvelope,
  type TerrascaleSyncV1MsgpackMessage,
  type TerrascaleSyncV1MsgpackSnapshot,
} from "./msgpack/generated/terrascale-sync-v1-msgpack.js";
import {
  TerraScaleSyncV1Client,
  TerraScaleSyncV1ClientError,
  SyncV1ViewError,
  syncV1ResultDigest,
  type SyncV1ResultItem,
} from "./index.js";

const encoder = new TextEncoder();
const decoder = new TextDecoder();

const item = (identity: string, value: string): SyncV1ResultItem => ({
  identity,
  value: { value: encoder.encode(value) },
});

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
    items: items.map(entry => ({
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

describe("current reactive client", () => {
  test("uses generated frames for subscriptions, snapshots, and deltas", () => {
    const client = new TerraScaleSyncV1Client();
    client.setOnline(true);
    const opened = client.openSubscription({
      subscription: "orders-by-status",
      query: "from orders where status = $status",
      parameters: encoder.encode("open"),
    });
    const subscribe = decodeTerrascaleSyncV1MsgpackEnvelope(
      client.encode(opened.message, 1n),
    );
    assert.equal(subscribe.message.kind, "subscribe");

    client.receive(serverFrame({
      kind: "snapshot",
      subscription: "orders-by-status",
      snapshot: wireSnapshot([item("order-1", "open")]),
    }));
    assert.equal(decoder.decode(opened.subscription.view.getItems()[0]!.value.value), "open");

    const changed = [item("order-1", "paid"), item("order-2", "open")];
    client.receive(serverFrame({
      kind: "delta",
      subscription: "orders-by-status",
      view_generation: 1n,
      from_revision: 1n,
      to_revision: 2n,
      operations: [
        { kind: "update", identity: "order-1", fields: [{ path: "value", value: encoder.encode("paid") }] },
        { kind: "insert", identity: "order-2", position: 1, fields: [{ path: "value", value: encoder.encode("open") }] },
      ],
      digest: syncV1ResultDigest(changed),
      checkpoint: encoder.encode("checkpoint-1-2"),
    }));
    assert.deepEqual(opened.subscription.view.getItems().map(entry => entry.identity), ["order-1", "order-2"]);

    const acknowledgement = client.checkpointAcknowledgement("orders-by-status");
    assert.equal(acknowledgement.kind, "checkpoint_ack");
  });

  test("applies authorization replacement, routing, and backpressure frames", () => {
    const client = new TerraScaleSyncV1Client();
    const opened = client.openSubscription({ subscription: "visible", query: "from docs", parameters: new Uint8Array() });
    client.receive(serverFrame({ kind: "snapshot", subscription: "visible", snapshot: wireSnapshot([item("a", "visible")]) }));
    client.receive(serverFrame({
      kind: "authorization_changed",
      subscription: "visible",
      authorization_generation: 2n,
      removed: ["a"],
      snapshot: wireSnapshot([item("b", "visible")], 2n, 2n, 2n),
    }));
    client.receive(serverFrame({ kind: "region_route_change", region: "FRA" }));
    client.receive(serverFrame({ kind: "backpressure", mode: "pause" }));
    assert.deepEqual(opened.subscription.view.getItems().map(entry => entry.identity), ["b"]);
    assert.deepEqual(client.getSnapshot(), { online: false, paused: true, region: "FRA", subscriptions: 1 });
  });

  test("rejects stale and replayed snapshots and resets across every monotonic counter", () => {
    const client = new TerraScaleSyncV1Client();
    client.openSubscription({ subscription: "monotonic", query: "from docs", parameters: new Uint8Array() });
    const installed = wireSnapshot([item("current", "visible")], 11n, 5n, 7n);
    client.receive(serverFrame({ kind: "snapshot", subscription: "monotonic", snapshot: installed }));

    const rejected = [
      { kind: "snapshot" as const, subscription: "monotonic", snapshot: wireSnapshot([], 11n, 4n, 7n) },
      { kind: "snapshot" as const, subscription: "monotonic", snapshot: wireSnapshot([], 11n, 5n, 6n) },
      { kind: "snapshot" as const, subscription: "monotonic", snapshot: wireSnapshot([], 10n, 5n, 7n) },
      { kind: "snapshot" as const, subscription: "monotonic", snapshot: installed },
      { kind: "reset" as const, subscription: "monotonic", reason: "generation_changed" as const, snapshot: wireSnapshot([], 11n, 6n, 6n) },
      { kind: "reset" as const, subscription: "monotonic", reason: "generation_changed" as const, snapshot: installed },
    ];
    for (const message of rejected) {
      assert.throws(
        () => client.receive(serverFrame(message)),
        (error: unknown) => error instanceof SyncV1ViewError && error.code === "reset-required",
      );
    }

    client.receive(serverFrame({
      kind: "reset",
      subscription: "monotonic",
      reason: "generation_changed",
      snapshot: wireSnapshot([item("next", "visible")], 12n, 6n, 8n),
    }));
  });

  test("validates a complete subscribe before publishing client state", () => {
    const client = new TerraScaleSyncV1Client();
    assert.throws(() => client.openSubscription({
      subscription: "bounded",
      query: "from docs",
      parameters: new Uint8Array(1_048_577),
    }));
    assert.equal(client.getSnapshot().subscriptions, 0);
    client.openSubscription({
      subscription: "bounded",
      query: "from docs",
      parameters: new Uint8Array(),
    });
    assert.equal(client.getSnapshot().subscriptions, 1);
  });

  test("rejects authorization replacements that restore removed identities atomically", () => {
    const client = new TerraScaleSyncV1Client();
    const opened = client.openSubscription({ subscription: "authorized", query: "from docs", parameters: new Uint8Array() });
    client.receive(serverFrame({
      kind: "snapshot",
      subscription: "authorized",
      snapshot: wireSnapshot([item("secret", "visible")], 4n, 3n, 5n),
    }));
    assert.throws(
      () => client.receive(serverFrame({
        kind: "authorization_changed",
        subscription: "authorized",
        authorization_generation: 6n,
        removed: ["secret"],
        snapshot: wireSnapshot([item("secret", "restored")], 5n, 4n, 6n),
      })),
      (error: unknown) => error instanceof SyncV1ViewError && error.code === "reset-required",
    );
    assert.deepEqual(opened.subscription.view.getItems().map(entry => entry.identity), ["secret"]);
    assert.equal(opened.subscription.view.getRevision(), 4n);
    assert.equal(opened.subscription.view.getAuthorizationGeneration(), 5n);
  });

  test("rejects wrong-direction, unknown-subscription, and remote error frames", () => {
    const client = new TerraScaleSyncV1Client();
    assert.throws(
      () => client.receive(serverFrame({ kind: "unsubscribe", subscription: "missing" })),
      (error: unknown) => error instanceof TerraScaleSyncV1ClientError && error.code === "protocol_error",
    );
    assert.throws(
      () => client.receive(serverFrame({ kind: "snapshot", subscription: "missing", snapshot: wireSnapshot([]) })),
      (error: unknown) => error instanceof TerraScaleSyncV1ClientError && error.code === "unknown_subscription",
    );
    client.setOnline(true);
    assert.throws(
      () => client.receive(serverFrame({ kind: "error", code: "authentication_expired", detail: null })),
      (error: unknown) => error instanceof TerraScaleSyncV1ClientError && error.code === "authentication_expired",
    );
    assert.equal(client.getSnapshot().online, false);
  });

  test("close removes the live subscription without taking unrelated views offline", async () => {
    const client = new TerraScaleSyncV1Client({
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.method === "POST") {
          return Response.json({
            viewId: request.url.endsWith("/a") ? "a" : "b",
            generation: 1,
            checkpoint: "AAE=",
            snapshotUrls: [],
            capability: "cap",
          });
        }
        return new Response(null, { status: 204 });
      },
    });
    const a = await client.subscribe({ subscription: "a", databaseId: "db", query: "select * from docs", parameters: new Uint8Array(), autoStart: false });
    const b = await client.subscribe({ subscription: "b", databaseId: "db", query: "select * from docs", parameters: new Uint8Array(), autoStart: false });
    a.close();
    assert.equal(client.getSnapshot().subscriptions, 1);
    assert.equal(b.view.isOnline(), true);
    assert.throws(
      () => client.receive(serverFrame({ kind: "snapshot", subscription: "a", snapshot: wireSnapshot([]) })),
      (error: unknown) => error instanceof TerraScaleSyncV1ClientError && error.code === "unknown_subscription",
    );
  });

  test("installs a checkpoint-only attach as a resume state", async () => {
    const client = new TerraScaleSyncV1Client({
      fetch: async () => Response.json({
        viewId: "resume-only",
        generation: 9,
        checkpoint: "AAE=",
        snapshotUrls: [],
        capability: "cap-resume",
      }),
    });
    const live = await client.subscribe({
      subscription: "resume-only",
      databaseId: "db",
      query: "select * from docs",
      parameters: new Uint8Array(),
      autoStart: false,
    });
    assert.deepEqual(live.checkpoint()?.toBytes(), new Uint8Array([0, 1]));
    assert.equal(live.view.getViewGeneration(), 9n);
    assert.equal(live.view.getRevision(), 0n);
  });

  test("rejects foreign or malformed snapshot and log URLs", async () => {
    for (const body of [
      { viewId: "safe", generation: 1, capability: "cap", snapshotUrls: ["https://foreign.example/api/v1/reactive/views/safe/1/snapshot/0?cut=1"] },
      { viewId: "safe", generation: 1, capability: "cap", snapshotUrls: ["https://sync.api.terrascale.test/api/v1/reactive/views/safe/1/snapshot/0"] },
      { viewId: "safe", generation: 1, capability: "cap", logUrl: "https://sync.api.terrascale.test/api/v1/reactive/views/safe/2/log?offset=0", snapshotUrls: [] },
    ]) {
      const client = new TerraScaleSyncV1Client({
        syncBaseUrl: "https://sync.api.terrascale.test",
        fetch: async () => Response.json(body),
      });
      await assert.rejects(
        () => client.subscribe({ subscription: "safe", databaseId: "db", query: "select * from docs", parameters: new Uint8Array(), autoStart: false }),
        (error: unknown) => error instanceof TerraScaleSyncV1ClientError && error.code === "protocol_error",
      );
      assert.equal(client.getSnapshot().subscriptions, 0);
    }
  });

  test("fails closed on unsigned generations, cuts, and checkpoint mismatches", async () => {
    const validSnapshot = {
      view_generation: 1,
      authorization_generation: 1,
      revision: 1,
      items: [],
      digest: syncV1ResultDigest([]),
      checkpoint: "YQ==",
    };
    const bodies = [
      { viewId: "negative", generation: -1, capability: "cap", checkpoint: "YQ==", snapshotUrls: [] },
      { viewId: "bad-cut", generation: 1, capability: "cap", snapshotUrls: ["/api/v1/reactive/views/bad-cut/1/snapshot/0?cut=-1"] },
      { viewId: "bad-cut", generation: 1, capability: "cap", snapshotUrls: ["/api/v1/reactive/views/bad-cut/1/snapshot/0?cut=18446744073709551616"] },
      { viewId: "mismatch", generation: 1, capability: "cap", checkpoint: "Yg==", snapshot: validSnapshot },
    ];
    for (const body of bodies) {
      const client = new TerraScaleSyncV1Client({
        syncBaseUrl: "https://sync.api.terrascale.test",
        fetch: async () => Response.json(body),
      });
      await assert.rejects(
        () => client.subscribe({ subscription: body.viewId, databaseId: "db", query: "select * from docs", parameters: new Uint8Array(), autoStart: false }),
        (error: unknown) => error instanceof TerraScaleSyncV1ClientError && error.code === "protocol_error",
      );
      assert.equal(client.getSnapshot().subscriptions, 0);
    }
  });

  test("restores the prior global online state after snapshot installation fails", async () => {
    let attaches = 0;
    const client = new TerraScaleSyncV1Client({
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.method !== "POST") return new Response(null, { status: 204 });
        attaches += 1;
        if (attaches === 1) {
          return Response.json({ viewId: "existing", generation: 1, checkpoint: "YQ==", snapshotUrls: [], capability: "cap" });
        }
        return Response.json({
          viewId: "broken",
          generation: 1,
          capability: "cap",
          snapshot: { view_generation: 1, authorization_generation: 1, revision: 1, items: [], digest: "f".repeat(64), checkpoint: "YQ==" },
        });
      },
    });
    const existing = await client.subscribe({ subscription: "existing", databaseId: "db", query: "select * from docs", parameters: new Uint8Array(), autoStart: false });
    client.setOnline(false);
    await assert.rejects(() => client.subscribe({ subscription: "broken", databaseId: "db", query: "select * from docs", parameters: new Uint8Array(), autoStart: false }));
    assert.equal(client.getSnapshot().online, false);
    assert.equal(existing.view.isOnline(), false);
    assert.equal(client.getSnapshot().subscriptions, 1);
  });

  test("uses CDN HTTP by default with canonical URLs, capability headers, and byte-safe frames", async () => {
    const requests: Request[] = [];
    const initial = wireSnapshot([item("one", "raw-\u0000-\u00ff")], 7n, 3n, 2n);
    const nextItems = [item("one", "changed")];
    const next = {
      kind: "delta" as const,
      subscription: "orders",
      view_generation: 3n,
      from_revision: 7n,
      to_revision: 8n,
      operations: [{ kind: "update" as const, identity: "one", fields: [{ path: "value", value: encoder.encode("changed") }] }],
      digest: syncV1ResultDigest(nextItems),
      checkpoint: new Uint8Array([0, 255, 1]),
    };
    const fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const request = new Request(input, init);
      requests.push(request);
      if (request.method === "POST") {
        return Response.json({
          viewId: "orders",
          generation: 3,
          checkpoint: "Y3AtNw==",
          snapshotUrls: ["https://sync.api.terrascale.tech/api/v1/reactive/views/orders/3/snapshot/0?cut=9"],
          logUrl: "https://sync.api.terrascale.tech/api/v1/reactive/views/orders/3/log?offset=7",
          capability: "cap-equivalent",
        });
      }
      if (new URL(request.url).pathname.endsWith("/snapshot/0")) {
        return new Response(serverFrame({ kind: "snapshot", subscription: "orders", snapshot: initial }).buffer as ArrayBuffer, {
          headers: { "content-type": "application/msgpack" },
        });
      }
      assert.equal(new URL(request.url).pathname, "/api/v1/reactive/views/orders/3/log");
      assert.equal(new URL(request.url).searchParams.get("offset"), "7");
      assert.equal(request.headers.get("x-terrascale-capability"), "cap-equivalent");
      return new Response(serverFrame(next).buffer as ArrayBuffer, { headers: { "content-type": "application/msgpack" } });
    };

    const client = createTerraScaleClient({
      apiBaseUrl: "https://member.example",
      accessToken: "session-token",
      tenantId: "tenant-scope",
      projectId: "project-scope",
      fetch,
    }).reactive;
    const live = await client.subscribe({
      subscription: "orders",
      databaseId: "db-1",
      query: "select * from orders",
      parameters: new Uint8Array([0, 255, 1]),
      autoStart: false,
    });
    assert.equal(live.transport, "http");
    assert.equal(live.logUrl, "https://sync.api.terrascale.tech/api/v1/reactive/views/orders/3/log");
    assert.equal(live.snapshotUrls[0], "https://sync.api.terrascale.tech/api/v1/reactive/views/orders/3/snapshot/0?cut=9");
    assert.equal(requests[0]!.headers.get("authorization"), "Bearer session-token");
    assert.equal(requests[0]!.headers.get("x-terrascale-tenant-id"), "tenant-scope");
    assert.equal(requests[0]!.headers.get("x-terrascale-project-id"), "project-scope");
    const attach = JSON.parse(await requests[0]!.clone().text()) as { parameters: string; checkpoint: string | null };
    assert.equal(attach.parameters, "AP8B");
    assert.equal(attach.checkpoint, null);
    assert.deepEqual(live.view.getItems()[0]!.value.value, encoder.encode("raw-\u0000-\u00ff"));

    await live.poll();
    assert.equal(live.view.getRevision(), 8n);
    assert.deepEqual(live.view.getItems()[0]!.value.value, encoder.encode("changed"));
    for (const request of requests) {
      assert.equal(request.redirect, "manual");
      if (request.method === "GET") {
        assert.equal(request.headers.get("x-terrascale-tenant-id"), null);
        assert.equal(request.headers.get("x-terrascale-project-id"), null);
      }
    }
  });

  test("resumes with an opaque checkpoint and reports typed retention expiry", async () => {
    const requests: Request[] = [];
    let attachCount = 0;
    let notifications = 0;
    const fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const request = new Request(input, init);
      requests.push(request);
      if (request.method === "POST") {
        attachCount += 1;
        if (attachCount > 1) {
          return Response.json({
            viewId: "resume-view",
            generation: 5,
            capability: "cap-fresh",
            checkpoint: "Y3AtNS0xMg==",
            snapshot: {
              view_generation: 5,
              authorization_generation: 1,
              revision: 12,
              items: [{ identity: "fresh", fields: [{ path: "value", value: "YXV0aG9yaXplZA==" }] }],
              digest: syncV1ResultDigest([item("fresh", "authorized")]),
              checkpoint: "Y3AtNS0xMg==",
            },
          });
        }
        return Response.json({
          viewId: "resume-view",
          generation: 4,
          checkpoint: "AAE=",
          capability: "cap-resume",
          snapshotUrls: [],
        });
      }
      return Response.json({ error: { reason: "retention_advanced", message: "history has expired" } }, { status: 410 });
    };
    const client = new TerraScaleSyncV1Client({
      apiBaseUrl: "https://member.example",
      syncBaseUrl: "https://sync.api.terrascale.tech",
      fetch,
    });
    const live = await client.subscribe({
      subscription: "resume",
      databaseId: "db-2",
      query: "select * from docs",
      parameters: new Uint8Array([255]),
      checkpoint: new Uint8Array([0xff, 0x00]),
      autoStart: false,
    });
    live.view.subscribe(() => { notifications += 1; });
    const attach = JSON.parse(await requests[0]!.clone().text()) as { checkpoint: string };
    assert.equal(attach.checkpoint, "/wA=");
    await live.poll();
    assert.equal(attachCount, 2);
    assert.equal(live.view.getRevision(), 12n);
    assert.deepEqual(live.view.getItems().map(entry => entry.identity), ["fresh"]);
    assert.ok(notifications > 0);
  });
});
