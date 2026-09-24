import assert from "node:assert/strict";
import { describe, test } from "vitest";
import { createTerraScaleClient } from "./factory.js";
import { TerraScaleDiscoveryRouter, TerraScaleRegionCatalogCache, TerraScaleRoutingCache } from "./discovery.js";

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
  syncV1ReconnectDelayMs,
  syncV1ResultDigest,
  type SyncV1ResultItem,
  type SyncV1WebSocket,
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

class FakeSocket implements SyncV1WebSocket {
  binaryType = "";
  readonly sent: Uint8Array[] = [];
  #closed = false;
  readonly #listeners = new Map<
    "open" | "message" | "error" | "close",
    Set<(event: unknown) => void>
  >();

  addEventListener(
    type: "open" | "message" | "error" | "close",
    listener: (event: unknown) => void,
  ): void {
    let listeners = this.#listeners.get(type);
    if (listeners === undefined) {
      listeners = new Set();
      this.#listeners.set(type, listeners);
    }
    listeners.add(listener);
  }

  removeEventListener(
    type: "open" | "message" | "error" | "close",
    listener: (event: unknown) => void,
  ): void {
    this.#listeners.get(type)?.delete(listener);
  }

  send(data: Uint8Array | ArrayBuffer): void {
    if (this.#closed) throw new Error("fake socket is closed");
    this.sent.push(new Uint8Array(data));
  }

  close(code?: number, reason?: string): void {
    if (this.#closed) return;
    this.#closed = true;
    this.emit("close", { code, reason });
  }

  open(): void {
    this.emit("open", {});
  }

  message(data: Uint8Array): void {
    this.emit("message", { data });
  }

  get closed(): boolean {
    return this.#closed;
  }

  private emit(type: "open" | "message" | "error" | "close", event: unknown): void {
    for (const listener of this.#listeners.get(type) ?? []) listener(event);
  }
}

function base64(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("base64");
}

/** One text/event-stream response whose chunks land exactly as written. */
function sseResponse(chunks: readonly string[], leaveOpen = false): Response {
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) controller.enqueue(encoder.encode(chunk));
      if (!leaveOpen) controller.close();
    },
  });
  return new Response(stream, { status: 200, headers: { "content-type": "text/event-stream; charset=utf-8" } });
}

function attachedViewBody(generation: bigint): Record<string, unknown> {
  const snapshot = wireSnapshot([item("row", `generation-${generation}`)], generation, generation);
  return {
    viewId: `orders-${generation}`,
    generation: Number(generation),
    capability: `cap-${generation}`,
    checkpoint: base64(snapshot.checkpoint),
    snapshot: {
      view_generation: Number(snapshot.view_generation),
      authorization_generation: Number(snapshot.authorization_generation),
      revision: Number(snapshot.revision),
      items: snapshot.items.map(entry => ({
        identity: entry.identity,
        fields: entry.fields.map(field => ({ path: field.path, value: base64(field.value) })),
      })),
      digest: snapshot.digest,
      checkpoint: base64(snapshot.checkpoint),
    },
  };
}

async function waitFor(predicate: () => boolean, description: string): Promise<void> {
  const deadline = Date.now() + 1_000;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error(`timed out waiting for ${description}`);
    await new Promise<void>(resolve => setTimeout(resolve, 0));
  }
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

  test("reopens a closed WebSocket and sends the subscribe handshake before becoming online", async () => {
    const sockets: FakeSocket[] = [];
    let attaches = 0;
    const client = new TerraScaleSyncV1Client({
      apiBaseUrl: "https://api.terrascale.tech",
      fetch: async () => {
        attaches += 1;
        return Response.json(attachedViewBody(BigInt(attaches)), {
          headers: { "x-terrascale-region": "GRU" },
        });
      },
      webSocketFactory: (url, protocols) => {
        const generation = sockets.length + 1;
        assert.equal(url, `wss://gru.api.terrascale.tech/api/v1/reactive/views/orders-${generation}/${generation}/websocket`);
        assert.deepEqual(protocols, ["terrascale.sync.v1", `terrascale.cap.cap-${generation}`]);
        const socket = new FakeSocket();
        sockets.push(socket);
        if (sockets.length === 1) queueMicrotask(() => socket.open());
        return socket;
      },
    });
    const live = await client.subscribe({
      subscription: "orders",
      databaseId: "db",
      query: "select * from orders",
      parameters: new Uint8Array(),
      transport: "websocket",
    });
    assert.equal(client.getSnapshot().online, true);
    assert.equal(sockets.length, 1);
    assert.equal(sockets[0]!.sent.length, 1);

    sockets[0]!.close(1006, "network");
    assert.equal(client.getSnapshot().online, false);
    const reconnecting = live.reconnect();
    await waitFor(() => sockets.length === 2, "the replacement WebSocket");
    assert.equal(client.getSnapshot().online, false);
    sockets[1]!.open();
    await reconnecting;

    assert.equal(client.getSnapshot().online, true);
    assert.equal(sockets[1]!.sent.length, 1);
    const handshake = decodeTerrascaleSyncV1MsgpackEnvelope(sockets[1]!.sent[0]!);
    assert.equal(handshake.message.kind, "subscribe");
    assert.equal(handshake.message.subscription, "orders-2");
    const changed = [item("row", "reconnected")];
    sockets[1]!.message(serverFrame({
      kind: "delta",
      subscription: "orders-2",
      view_generation: 2n,
      from_revision: 2n,
      to_revision: 3n,
      operations: [{
        kind: "update",
        identity: "row",
        fields: [{ path: "value", value: encoder.encode("reconnected") }],
      }],
      digest: syncV1ResultDigest(changed),
      checkpoint: encoder.encode("checkpoint-2-3"),
    }));
    await waitFor(
      () => decoder.decode(live.view.getItems()[0]?.value.value ?? new Uint8Array()) === "reconnected",
      "the replacement delta",
    );
    assert.equal(live.view.getRevision(), 3n);
    live.close();
  });

  test("reconnects a WebSocket after a region route change and uses the new member", async () => {
    const sockets: FakeSocket[] = [];
    let attaches = 0;
    const client = new TerraScaleSyncV1Client({
      apiBaseUrl: "https://api.terrascale.tech",
      reconnectDelayMs: 0,
      fetch: async () => {
        attaches += 1;
        const region = attaches === 1 ? "GRU" : "FRA";
        return Response.json(attachedViewBody(BigInt(attaches)), {
          headers: { "x-terrascale-region": region },
        });
      },
      webSocketFactory: (url) => {
        const socket = new FakeSocket();
        sockets.push(socket);
        assert.match(url, new RegExp(`wss://${sockets.length === 1 ? "gru" : "fra"}\\.api\\.terrascale\\.tech/`));
        if (sockets.length === 1) queueMicrotask(() => socket.open());
        return socket;
      },
    });
    const live = await client.subscribe({
      subscription: "orders",
      databaseId: "db",
      query: "select * from orders",
      parameters: new Uint8Array(),
      transport: "websocket",
    });

    sockets[0]!.message(serverFrame({ kind: "region_route_change", region: "FRA" }));
    await waitFor(() => sockets.length === 2, "the region-routed replacement WebSocket");
    assert.equal(client.getSnapshot().online, false);
    sockets[1]!.open();
    await waitFor(() => client.getSnapshot().online, "the replacement handshake");

    assert.equal(client.getSnapshot().region, "FRA");
    assert.equal(sockets[1]!.sent.length, 1);
    const handshake = decodeTerrascaleSyncV1MsgpackEnvelope(sockets[1]!.sent[0]!);
    assert.equal(handshake.message.kind, "subscribe");
    assert.equal(handshake.message.subscription, "orders-2");
    const changed = [item("row", "rerouted")];
    sockets[1]!.message(serverFrame({
      kind: "delta",
      subscription: "orders-2",
      view_generation: 2n,
      from_revision: 2n,
      to_revision: 3n,
      operations: [{
        kind: "update",
        identity: "row",
        fields: [{ path: "value", value: encoder.encode("rerouted") }],
      }],
      digest: syncV1ResultDigest(changed),
      checkpoint: encoder.encode("checkpoint-2-3"),
    }));
    await waitFor(
      () => decoder.decode(live.view.getItems()[0]?.value.value ?? new Uint8Array()) === "rerouted",
      "the region-routed delta",
    );
    assert.equal(live.view.getRevision(), 3n);
    live.close();
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
        assert.equal(request.headers.get("x-terrascale-capability"), "cap-equivalent");
        assert.equal(request.headers.get("x-terrascale-view-capability"), null);
        assert.equal([...request.headers.keys()].filter(header => header === "x-terrascale-capability").length, 1);
      }
    }
  });

  test("accepts only the canonical capability header in attach responses", async () => {
    for (const capabilityHeader of ["x-terrascale-capability", "X-TerraScale-Capability", undefined]) {
      const client = new TerraScaleSyncV1Client({
        fetch: async () => Response.json({
          viewId: "canonical-header",
          generation: 1,
          capability: "cap",
          ...(capabilityHeader === undefined ? {} : { capability_header: capabilityHeader }),
          checkpoint: "AAE=",
          snapshotUrls: [],
        }),
      });
      await assert.doesNotReject(() => client.subscribe({
        subscription: "canonical-header",
        databaseId: "db",
        query: "select * from docs",
        parameters: new Uint8Array(),
        autoStart: false,
      }));
    }

    for (const capabilityHeader of ["x-terrascale-view-capability", "x-terrascale-unsupported"]) {
      const client = new TerraScaleSyncV1Client({
        fetch: async () => Response.json({
          viewId: "legacy-header",
          generation: 1,
          capability: "cap",
          capability_header: capabilityHeader,
          checkpoint: "AAE=",
          snapshotUrls: [],
        }),
      });
      await assert.rejects(
        () => client.subscribe({
          subscription: "legacy-header",
          databaseId: "db",
          query: "select * from docs",
          parameters: new Uint8Array(),
          autoStart: false,
        }),
        (error: unknown) => error instanceof TerraScaleSyncV1ClientError && error.code === "protocol_error",
      );
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

  test("records an authoritative reset reason while replacing rows", async () => {
    let pollCount = 0;
    const fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const request = new Request(input, init);
      if (request.method === "POST") {
        return Response.json({
          viewId: "reset-view",
          generation: 1,
          capability: "cap-reset",
          snapshot: {
            view_generation: 1,
            authorization_generation: 1,
            revision: 1,
            items: [{ identity: "old", fields: [{ path: "value", value: "b2xk" }] }],
            digest: syncV1ResultDigest([item("old", "old")]),
            checkpoint: "Y3AtMQ==",
          },
        });
      }
      pollCount++;
      return new Response(serverFrame({
        kind: "reset",
        subscription: "reset-view",
        reason: "generation_changed",
        snapshot: wireSnapshot([item("new", "new")], 1n, 2n, 1n),
      }).buffer as ArrayBuffer, { headers: { "content-type": "application/msgpack" } });
    };
    const client = new TerraScaleSyncV1Client({ apiBaseUrl: "https://member.example", fetch });
    const live = await client.subscribe({ subscription: "reset", databaseId: "db", query: "select * from docs", parameters: new Uint8Array(), autoStart: false });
    await live.poll();
    assert.equal(pollCount, 1);
    assert.deepEqual(live.view.getItems().map(entry => entry.identity), ["new"]);
    assert.deepEqual(live.reset, { reason: "generation-changed", subscription: "reset" });
  });

  test("streams server-sent events from the log origin with capability and auth headers", async () => {
    const requests: Request[] = [];
    const changed = [item("row", "streamed")];
    const delta = serverFrame({
      kind: "delta",
      subscription: "orders-1",
      view_generation: 1n,
      from_revision: 1n,
      to_revision: 2n,
      operations: [{ kind: "update", identity: "row", fields: [{ path: "value", value: encoder.encode("streamed") }] }],
      digest: syncV1ResultDigest(changed),
      checkpoint: encoder.encode("checkpoint-1-2"),
    });
    const fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const request = new Request(input, init);
      requests.push(request);
      if (request.method === "POST") return Response.json(attachedViewBody(1n));
      return sseResponse([": ping\n\n", "id: 2\nda", `ta: ${base64(delta)}\n\n`], true);
    };
    const client = createTerraScaleClient({
      apiBaseUrl: "https://member.example",
      accessToken: "session-token",
      fetch,
    }).reactive;
    const live = await client.subscribe({
      subscription: "orders",
      databaseId: "db-1",
      query: "select * from orders",
      parameters: new Uint8Array(),
      transport: "server-sent-events",
    });
    assert.equal(live.transport, "server-sent-events");
    await waitFor(() => live.view.getRevision() === 2n, "the streamed delta");
    assert.equal(decoder.decode(live.view.getItems()[0]!.value.value), "streamed");
    const stream = requests[1]!;
    assert.equal(requests[0]!.headers.get("authorization"), "Bearer session-token");
    assert.equal(new URL(stream.url).pathname, "/api/v1/reactive/views/orders-1/1/stream");
    assert.equal(new URL(stream.url).searchParams.get("offset"), "1");
    assert.equal(stream.headers.get("accept"), "text/event-stream");
    assert.equal(stream.headers.get("x-terrascale-capability"), "cap-1");
    assert.equal(stream.headers.get("authorization"), "Bearer session-token");
    live.close();
  });

  test("treats a terminal error event as a typed reset and reattaches from the last offset", async () => {
    const streams: Request[] = [];
    let attaches = 0;
    const deltaTo = (view: string, generation: bigint, to: bigint, value: string) => serverFrame({
      kind: "delta",
      subscription: view,
      view_generation: generation,
      from_revision: to - 1n,
      to_revision: to,
      operations: [{ kind: "update", identity: "row", fields: [{ path: "value", value: encoder.encode(value) }] }],
      digest: syncV1ResultDigest([item("row", value)]),
      checkpoint: encoder.encode(`checkpoint-${generation}-${to}`),
    });
    const fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const request = new Request(input, init);
      if (request.method === "POST") {
        attaches += 1;
        return Response.json(attachedViewBody(BigInt(attaches)));
      }
      streams.push(request);
      return attaches === 1
        ? sseResponse([
          `id: 2\ndata: ${base64(deltaTo("orders-1", 1n, 2n, "before-error"))}\n\n`,
          "id: 2\nevent: error\ndata: reactive_unavailable\n\n",
        ])
        : sseResponse([`id: 3\ndata: ${base64(deltaTo("orders-2", 2n, 3n, "resumed"))}\n\n`], true);
    };
    const client = new TerraScaleSyncV1Client({ reconnectDelayMs: 0, fetch });
    const live = await client.subscribe({
      subscription: "orders",
      databaseId: "db",
      query: "select * from orders",
      parameters: new Uint8Array(),
      transport: "server-sent-events",
    });
    await waitFor(() => streams.length === 2, "the replacement stream");
    assert.equal(attaches, 2);
    assert.equal(new URL(streams[0]!.url).pathname, "/api/v1/reactive/views/orders-1/1/stream");
    assert.equal(new URL(streams[0]!.url).searchParams.get("offset"), "1");
    assert.equal(new URL(streams[1]!.url).pathname, "/api/v1/reactive/views/orders-2/2/stream");
    assert.equal(new URL(streams[1]!.url).searchParams.get("offset"), "2");
    await waitFor(() => live.view.getRevision() === 3n, "the resumed delta");
    assert.equal(decoder.decode(live.view.getItems()[0]!.value.value), "resumed");
    assert.deepEqual(live.reset, { reason: "checkpoint-expired", subscription: "orders", message: "reactive_unavailable" });
    assert.equal(client.getSnapshot().online, true);
    live.close();
  });

  test("reattaches from the installed revision after the stream is dropped", async () => {
    const streams: Request[] = [];
    let attaches = 0;
    const deltaTo = (view: string, generation: bigint, to: bigint, value: string) => serverFrame({
      kind: "delta",
      subscription: view,
      view_generation: generation,
      from_revision: to - 1n,
      to_revision: to,
      operations: [{ kind: "update", identity: "row", fields: [{ path: "value", value: encoder.encode(value) }] }],
      digest: syncV1ResultDigest([item("row", value)]),
      checkpoint: encoder.encode(`checkpoint-${generation}-${to}`),
    });
    const fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const request = new Request(input, init);
      if (request.method === "POST") {
        attaches += 1;
        return Response.json(attachedViewBody(BigInt(attaches)));
      }
      streams.push(request);
      return attaches === 1
        ? sseResponse([`id: 2\ndata: ${base64(deltaTo("orders-1", 1n, 2n, "dropped"))}\n\n`])
        : sseResponse([`id: 3\ndata: ${base64(deltaTo("orders-2", 2n, 3n, "recovered"))}\n\n`], true);
    };
    const client = new TerraScaleSyncV1Client({ reconnectDelayMs: 0, fetch });
    const live = await client.subscribe({
      subscription: "orders",
      databaseId: "db",
      query: "select * from orders",
      parameters: new Uint8Array(),
      transport: "server-sent-events",
    });
    await waitFor(() => streams.length === 2, "the replacement stream after the drop");
    assert.equal(new URL(streams[1]!.url).searchParams.get("offset"), "2");
    await waitFor(() => live.view.getRevision() === 3n, "the recovered delta");
    assert.equal(decoder.decode(live.view.getItems()[0]!.value.value), "recovered");
    assert.equal(live.reset, undefined);
    live.close();
  });
});

describe("reactive discovery routing, backoff, and deduplication (ADR 0050 §5.7)", () => {
  const fra = { region: "FRA", url: "https://fra.api.terrascale.tech" };
  const gru = { region: "GRU", url: "https://gru.api.terrascale.tech" };

  function discoveryRouter(
    endpoints: readonly { region: string; url: string }[],
    options: { fail?: boolean; failDiscovery?: boolean; catalog?: () => Response } = {},
  ): TerraScaleDiscoveryRouter {
    const routingDocument = () => Response.json({
      v: 1,
      database: "db",
      branch: "main",
      revision: 1,
      updated: 1,
      residency: null,
      placement: "known",
      refreshSeconds: 300,
      endpoints: endpoints.map((endpoint, index) => ({
        region: endpoint.region,
        url: endpoint.url,
        priority: index + 1,
        capabilities: ["read", "reactive"],
      })),
    });
    return new TerraScaleDiscoveryRouter({
      origin: "https://api.terrascale.tech",
      discoveryBaseUrl: undefined,
      fetch: async (input) => {
        const url = new URL(input instanceof Request ? input.url : String(input));
        if (url.pathname === "/api/v1/regions") {
          if (options.catalog !== undefined) return options.catalog();
          return new Response("unavailable", { status: 503 });
        }
        if (options.fail === true || options.failDiscovery === true) {
          return new Response("unavailable", { status: 503 });
        }
        return routingDocument();
      },
      timeoutMs: 30_000,
      discoveryTimeoutMs: 1_000,
    }, new TerraScaleRoutingCache(), new TerraScaleRegionCatalogCache());
  }

  test("attaches directly to the discovered regional endpoint", async () => {
    const attachUrls: string[] = [];
    const client = new TerraScaleSyncV1Client({
      apiBaseUrl: "https://api.terrascale.tech",
      discoveryRouter: discoveryRouter([fra, gru]),
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.method !== "POST") return new Response(null, { status: 204 });
        attachUrls.push(request.url);
        return Response.json(attachedViewBody(1n), { headers: { "x-terrascale-region": "FRA" } });
      },
    });
    const live = await client.subscribe({
      subscription: "orders", databaseId: "db", query: "select * from orders",
      parameters: new Uint8Array(), autoStart: false,
    });
    assert.deepEqual(attachUrls, ["https://fra.api.terrascale.tech/api/v1/reactive/views"]);
    // Reactive delivery (snapshot/log) still targets the sync origin.
    assert.equal(new URL(live.logUrl).origin, "https://sync.api.terrascale.tech");
    live.close();
  });

  test("falls back to the catalog-selected regional origin when discovery cannot answer", async () => {
    const attachUrls: string[] = [];
    const client = new TerraScaleSyncV1Client({
      apiBaseUrl: "https://api.terrascale.tech",
      discoveryRouter: discoveryRouter([fra], {
        failDiscovery: true,
        catalog: () => Response.json({
          regions: [
            { code: "FRA", name: "Frankfurt", country: "DE", latitude: 50.1109, longitude: 8.6821, available: true },
          ],
          hosts: { FRA: "https://fra.api.terrascale.tech" },
        }),
      }),
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.method !== "POST") return new Response(null, { status: 204 });
        attachUrls.push(request.url);
        return Response.json(attachedViewBody(1n), { headers: { "x-terrascale-region": "FRA" } });
      },
    });
    const live = await client.subscribe({
      subscription: "orders", databaseId: "db", query: "select * from orders",
      parameters: new Uint8Array(), autoStart: false,
    });
    // No proxy exists: the degraded attach still targets a regional member.
    assert.deepEqual(attachUrls, ["https://fra.api.terrascale.tech/api/v1/reactive/views"]);
    live.close();
  });

  test("falls back to the configured attach origin when discovery and catalog cannot answer", async () => {
    const attachUrls: string[] = [];
    const client = new TerraScaleSyncV1Client({
      apiBaseUrl: "https://api.terrascale.tech",
      discoveryRouter: discoveryRouter([fra], { fail: true }),
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.method !== "POST") return new Response(null, { status: 204 });
        attachUrls.push(request.url);
        return Response.json(attachedViewBody(1n), { headers: { "x-terrascale-region": "GRU" } });
      },
      webSocketFactory: (url) => {
        assert.equal(new URL(url).hostname, "gru.api.terrascale.tech");
        const socket = new FakeSocket();
        queueMicrotask(() => socket.open());
        return socket;
      },
    });
    const live = await client.subscribe({
      subscription: "orders", databaseId: "db", query: "select * from orders",
      parameters: new Uint8Array(), transport: "websocket",
    });
    assert.deepEqual(attachUrls, ["https://api.terrascale.tech/api/v1/reactive/views"]);
    assert.equal(client.getSnapshot().online, true);
    live.close();
  });

  test("reports the discovered region on attach transport failure and reattaches to the next endpoint", async () => {
    const router = discoveryRouter([fra, gru]);
    const attachUrls: string[] = [];
    const client = new TerraScaleSyncV1Client({
      apiBaseUrl: "https://api.terrascale.tech",
      discoveryRouter: router,
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.method !== "POST") return new Response(null, { status: 204 });
        attachUrls.push(request.url);
        if (request.url.startsWith("https://fra.api.terrascale.tech")) throw new TypeError("connection reset");
        return Response.json(attachedViewBody(1n), { headers: { "x-terrascale-region": "GRU" } });
      },
    });
    const options = {
      subscription: "orders", databaseId: "db", query: "select * from orders",
      parameters: new Uint8Array(), autoStart: false,
    } as const;
    await assert.rejects(
      () => client.subscribe(options),
      (error: unknown) => error instanceof TerraScaleSyncV1ClientError && error.code === "transport",
    );
    assert.equal(router.cachedDocument("db")?.endpoints[0]?.region, "GRU");
    const live = await client.subscribe(options);
    assert.deepEqual(attachUrls, [
      "https://fra.api.terrascale.tech/api/v1/reactive/views",
      "https://gru.api.terrascale.tech/api/v1/reactive/views",
    ]);
    live.close();
  });

  test("reports the discovered region when the regional WebSocket fails before it opens", async () => {
    const router = discoveryRouter([fra, gru]);
    const client = new TerraScaleSyncV1Client({
      apiBaseUrl: "https://api.terrascale.tech",
      discoveryRouter: router,
      // No region header: the member origin and reported region come from the
      // discovered endpoint itself (the capability is region-bound).
      fetch: async () => Response.json(attachedViewBody(1n)),
      webSocketFactory: (url) => {
        assert.equal(new URL(url).hostname, "fra.api.terrascale.tech");
        const socket = new FakeSocket();
        queueMicrotask(() => socket.close(1006, "network"));
        return socket;
      },
    });
    await assert.rejects(
      () => client.subscribe({
        subscription: "orders", databaseId: "db", query: "select * from orders",
        parameters: new Uint8Array(), transport: "websocket",
      }),
      (error: unknown) => error instanceof TerraScaleSyncV1ClientError && error.code === "transport",
    );
    assert.equal(router.cachedDocument("db")?.endpoints[0]?.region, "GRU");
  });

  test("reattaches to the next discovered endpoint after a region route change", async () => {
    const router = discoveryRouter([fra, gru]);
    const attachUrls: string[] = [];
    const sockets: FakeSocket[] = [];
    const client = new TerraScaleSyncV1Client({
      apiBaseUrl: "https://api.terrascale.tech",
      reconnectDelayMs: 0,
      discoveryRouter: router,
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.method !== "POST") return new Response(null, { status: 204 });
        attachUrls.push(request.url);
        const region = request.url.startsWith("https://fra.api.terrascale.tech") ? "FRA" : "GRU";
        return Response.json(attachedViewBody(BigInt(attachUrls.length)), { headers: { "x-terrascale-region": region } });
      },
      webSocketFactory: (url) => {
        assert.equal(new URL(url).hostname, sockets.length === 0 ? "fra.api.terrascale.tech" : "gru.api.terrascale.tech");
        const socket = new FakeSocket();
        sockets.push(socket);
        if (sockets.length === 1) queueMicrotask(() => socket.open());
        return socket;
      },
    });
    const live = await client.subscribe({
      subscription: "orders", databaseId: "db", query: "select * from orders",
      parameters: new Uint8Array(), transport: "websocket",
    });
    sockets[0]!.message(serverFrame({ kind: "region_route_change", region: "GRU" }));
    await waitFor(() => sockets.length === 2, "the replacement WebSocket on the next endpoint");
    assert.deepEqual(attachUrls, [
      "https://fra.api.terrascale.tech/api/v1/reactive/views",
      "https://gru.api.terrascale.tech/api/v1/reactive/views",
    ]);
    sockets[1]!.open();
    await waitFor(() => client.getSnapshot().online, "the replacement handshake");
    assert.equal(client.getSnapshot().region, "GRU");
    live.close();
  });

  test("reconnect backoff grows exponentially with bounded jitter and caps at 30 s", () => {
    const at = (u: number) => () => u;
    const epsilon = 1e-9;
    for (let failures = 0; failures <= 10; failures += 1) {
      const step = Math.min(250 * 2 ** failures, 30_000);
      for (let u = 0; u <= 1; u += 0.25) {
        const delay = syncV1ReconnectDelayMs(250, failures, at(u));
        assert.ok(delay >= 0.8 * step - epsilon, `failure ${failures} jitter ${u}: ${delay} >= 0.8 * ${step}`);
        assert.ok(delay <= 1.2 * step + epsilon, `failure ${failures} jitter ${u}: ${delay} <= 1.2 * ${step}`);
      }
    }
    assert.equal(syncV1ReconnectDelayMs(250, 0, at(0.5)), 250);
    assert.equal(syncV1ReconnectDelayMs(250, 1, at(0.5)), 500);
    assert.equal(syncV1ReconnectDelayMs(250, 2, at(0.5)), 1_000);
    assert.equal(syncV1ReconnectDelayMs(250, 6, at(0.5)), 16_000);
    assert.equal(syncV1ReconnectDelayMs(1_000, 5, at(0.5)), 30_000);
    assert.equal(syncV1ReconnectDelayMs(10_000, 2, at(1)), 30_000);
  });

  test("the HTTP poll loop reconnects through backoff after a transport failure", async () => {
    let attaches = 0;
    let logRequests = 0;
    const recovered = [item("row", "recovered")];
    const fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const request = new Request(input, init);
      if (request.method === "POST") {
        attaches += 1;
        return Response.json(attachedViewBody(1n));
      }
      logRequests += 1;
      if (logRequests === 1) throw new TypeError("connection reset");
      if (logRequests > 2) return new Response(null, { status: 204 });
      return new Response(serverFrame({
        kind: "delta",
        subscription: "orders-1",
        view_generation: 1n,
        from_revision: 1n,
        to_revision: 2n,
        operations: [{ kind: "update", identity: "row", fields: [{ path: "value", value: encoder.encode("recovered") }] }],
        digest: syncV1ResultDigest(recovered),
        checkpoint: encoder.encode("checkpoint-1-2"),
      }).buffer as ArrayBuffer, { headers: { "content-type": "application/msgpack" } });
    };
    const client = new TerraScaleSyncV1Client({ apiBaseUrl: "https://member.example", reconnectDelayMs: 10, fetch });
    const live = await client.subscribe({
      subscription: "orders", databaseId: "db", query: "select * from orders", parameters: new Uint8Array(),
    });
    await waitFor(() => live.view.getRevision() === 2n, "the post-backoff delta");
    assert.equal(attaches, 2);
    assert.equal(client.getSnapshot().online, true);
    live.close();
  });

  test("deduplicates identical subscriptions onto one attach and socket", async () => {
    const sockets: FakeSocket[] = [];
    let attaches = 0;
    const client = new TerraScaleSyncV1Client({
      apiBaseUrl: "https://api.terrascale.tech",
      fetch: async () => {
        attaches += 1;
        return Response.json(attachedViewBody(BigInt(attaches)), { headers: { "x-terrascale-region": "GRU" } });
      },
      webSocketFactory: () => {
        const socket = new FakeSocket();
        sockets.push(socket);
        queueMicrotask(() => socket.open());
        return socket;
      },
    });
    const options = {
      subscription: "orders", databaseId: "db", query: "select * from orders",
      parameters: new Uint8Array(), transport: "websocket",
    } as const;
    const first = await client.subscribe(options);
    const second = await client.subscribe(options);
    assert.equal(attaches, 1);
    assert.equal(sockets.length, 1);
    assert.equal(second.id, first.id);
    assert.equal(second.view, first.view);
    assert.equal(client.getSnapshot().subscriptions, 1);

    first.close();
    assert.equal(sockets[0]!.closed, false);
    assert.equal(client.getSnapshot().online, true);
    assert.equal(client.getSnapshot().subscriptions, 1);

    second.close();
    assert.equal(sockets[0]!.closed, true);
    assert.equal(client.getSnapshot().subscriptions, 0);
    assert.equal(client.getSnapshot().online, false);
  });

  test("concurrent identical subscribes share one in-flight attach", async () => {
    let attaches = 0;
    const client = new TerraScaleSyncV1Client({
      fetch: async () => {
        attaches += 1;
        return Response.json(attachedViewBody(1n), { headers: { "x-terrascale-region": "GRU" } });
      },
    });
    const options = {
      subscription: "orders", databaseId: "db", query: "select * from orders",
      parameters: new Uint8Array(), autoStart: false,
    } as const;
    const [first, second] = await Promise.all([client.subscribe(options), client.subscribe(options)]);
    assert.equal(attaches, 1);
    assert.equal(second.view, first.view);
    assert.equal(client.getSnapshot().subscriptions, 1);

    first.close();
    assert.equal(client.getSnapshot().subscriptions, 1);
    second.close();
    assert.equal(client.getSnapshot().subscriptions, 0);
  });

  test("does not deduplicate an identical subscribe onto a terminally dead view", async () => {
    let attaches = 0;
    let polls = 0;
    const client = new TerraScaleSyncV1Client({
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.method === "POST") {
          attaches += 1;
          return Response.json(attachedViewBody(BigInt(attaches)));
        }
        polls += 1;
        return polls === 1
          ? new Response(serverFrame({ kind: "error", code: "authentication_expired", detail: null }).buffer as ArrayBuffer, {
            headers: { "content-type": "application/msgpack" },
          })
          : new Response(null, { status: 204 });
      },
    });
    const options = {
      subscription: "orders", databaseId: "db", query: "select * from orders",
      parameters: new Uint8Array(),
    } as const;
    await client.subscribe(options);
    // The terminal authentication error ends the poll loop; the dead view is
    // torn down instead of masking the error for every future subscriber.
    await waitFor(() => client.getSnapshot().subscriptions === 0, "the dead view to be torn down");
    const fresh = await client.subscribe(options);
    assert.equal(attaches, 2);
    assert.equal(client.getSnapshot().subscriptions, 1);
    assert.equal(client.getSnapshot().online, true);
    assert.deepEqual(fresh.view.getItems().map(entry => entry.identity), ["row"]);
    fresh.close();
  });

  test("scopes a region route change to the database whose wire delivered it", async () => {
    const routingPayload = (database: string) => ({
      v: 1,
      database,
      branch: "main",
      revision: 1,
      updated: 1,
      residency: null,
      placement: "known",
      refreshSeconds: 300,
      endpoints: [
        { region: fra.region, url: fra.url, priority: 1, capabilities: ["read", "reactive"] },
        { region: gru.region, url: gru.url, priority: 2, capabilities: ["read", "reactive"] },
      ],
    });
    const router = new TerraScaleDiscoveryRouter({
      origin: "https://api.terrascale.tech",
      discoveryBaseUrl: undefined,
      fetch: async input => Response.json(routingPayload(
        new URL(input instanceof Request ? input.url : String(input)).searchParams.get("database") ?? "db",
      )),
      timeoutMs: 30_000,
      discoveryTimeoutMs: 1_000,
    }, new TerraScaleRoutingCache(), new TerraScaleRegionCatalogCache());
    const attachUrls: string[] = [];
    const client = new TerraScaleSyncV1Client({
      apiBaseUrl: "https://api.terrascale.tech",
      reconnectDelayMs: 0,
      discoveryRouter: router,
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.method === "POST") {
          attachUrls.push(request.url);
          const body = JSON.parse(await request.clone().text()) as { databaseId: string };
          return Response.json({
            ...attachedViewBody(BigInt(attachUrls.length)),
            viewId: `${body.databaseId}-${attachUrls.length}`,
          }, { headers: { "x-terrascale-region": "FRA" } });
        }
        if (new URL(request.url).pathname.includes("db-alpha-1")) {
          return new Response(serverFrame({ kind: "region_route_change", region: "GRU" }).buffer as ArrayBuffer, {
            headers: { "content-type": "application/msgpack" },
          });
        }
        return new Response(null, { status: 204 });
      },
    });
    await client.subscribe({ subscription: "alpha", databaseId: "db-alpha", query: "select * from orders", parameters: new Uint8Array() });
    await client.subscribe({ subscription: "beta", databaseId: "db-beta", query: "select * from orders", parameters: new Uint8Array() });
    await waitFor(() => attachUrls.length === 3, "alpha's reattach");
    // Only db-alpha's FRA was demoted; db-beta keeps its healthy region.
    assert.equal(router.cachedDocument("db-alpha")?.endpoints[0]?.region, "GRU");
    assert.equal(router.cachedDocument("db-beta")?.endpoints[0]?.region, "FRA");
    // Three attaches total: alpha's first, alpha's post-route-change reattach
    // on GRU, and beta's own first attach — still on its healthy FRA.
    const origins = attachUrls.map(url => new URL(url).origin);
    assert.equal(origins.length, 3);
    assert.equal(origins[0], "https://fra.api.terrascale.tech");
    assert.equal(origins.filter(origin => origin === "https://gru.api.terrascale.tech").length, 1);
    assert.equal(origins.filter(origin => origin === "https://fra.api.terrascale.tech").length, 2);
    client.closeSubscription("alpha");
    client.closeSubscription("beta");
  });

  test("backs off between reattaches when route changes arrive in quick succession", async () => {
    const attachTimes: number[] = [];
    let polls = 0;
    const client = new TerraScaleSyncV1Client({
      apiBaseUrl: "https://api.terrascale.tech",
      reconnectDelayMs: 250,
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.method === "POST") {
          attachTimes.push(Date.now());
          return Response.json(attachedViewBody(1n), { headers: { "x-terrascale-region": "FRA" } });
        }
        polls += 1;
        return polls <= 2
          ? new Response(serverFrame({ kind: "region_route_change", region: "GRU" }).buffer as ArrayBuffer, {
            headers: { "content-type": "application/msgpack" },
          })
          : new Response(null, { status: 204 });
      },
    });
    const live = await client.subscribe({
      subscription: "orders", databaseId: "db", query: "select * from orders", parameters: new Uint8Array(),
    });
    const deadline = Date.now() + 5_000;
    while (attachTimes.length < 3 && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal(attachTimes.length, 3);
    // Every server-directed move waits through the backoff step (≥ 0.8 × the
    // 250 ms base, less clock slack), bounding any attach storm. Without the
    // backoff the second reattach follows within a few milliseconds.
    const secondMoveDelay = attachTimes[2]! - attachTimes[1]!;
    assert.ok(secondMoveDelay >= 190, `second reattach waited only ${secondMoveDelay}ms`);
    live.close();
  });

  test("closeSubscription keeps a refcounted shared view live and acknowledging", async () => {
    let attaches = 0;
    const client = new TerraScaleSyncV1Client({
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.method === "POST") {
          attaches += 1;
          return Response.json(attachedViewBody(BigInt(attaches)), { headers: { "x-terrascale-region": "GRU" } });
        }
        return new Response(null, { status: 204 });
      },
    });
    const options = {
      subscription: "orders", databaseId: "db", query: "select * from orders",
      parameters: new Uint8Array(), autoStart: false,
    } as const;
    await client.subscribe(options);
    await client.subscribe(options);
    assert.equal(attaches, 1);
    const unsubscribe = client.closeSubscription("orders");
    assert.equal(unsubscribe.subscription, "orders-1");
    // One consumer remains: the shared view stays open, online, and keeps
    // answering checkpoint acknowledgements.
    assert.equal(client.getSnapshot().subscriptions, 1);
    assert.equal(client.getSnapshot().online, true);
    assert.doesNotThrow(() => client.checkpointAcknowledgement("orders"));
    client.closeSubscription("orders");
    assert.equal(client.getSnapshot().subscriptions, 0);
    assert.equal(client.getSnapshot().online, false);
    assert.throws(() => client.checkpointAcknowledgement("orders"), /is not open/u);
  });
});
