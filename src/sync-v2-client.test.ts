import assert from "node:assert/strict";
import { describe, test } from "vitest";

import {
  decodeTerrascaleSyncV2MsgpackEnvelope,
  encodeTerrascaleSyncV2MsgpackEnvelope,
  type TerrascaleSyncV2MsgpackMessage,
  type TerrascaleSyncV2MsgpackSnapshot,
} from "./msgpack/generated/terrascale-sync-v2-msgpack.js";
import {
  TerraScaleSyncV2Client,
  TerraScaleSyncV2ClientError,
  SyncV2ViewError,
  syncV2ResultDigest,
  type SyncV2ResultItem,
} from "./index.js";

const encoder = new TextEncoder();
const decoder = new TextDecoder();

const item = (identity: string, value: string): SyncV2ResultItem => ({
  identity,
  value: { value: encoder.encode(value) },
});

function wireSnapshot(
  items: readonly SyncV2ResultItem[],
  revision = 1n,
  viewGeneration = 1n,
  authorizationGeneration = 1n,
): TerrascaleSyncV2MsgpackSnapshot {
  return {
    view_generation: viewGeneration,
    authorization_generation: authorizationGeneration,
    revision,
    items: items.map(entry => ({
      identity: entry.identity,
      fields: Object.entries(entry.value).map(([path, value]) => ({ path, value })),
    })),
    digest: syncV2ResultDigest(items),
    checkpoint: encoder.encode(`checkpoint-${viewGeneration}-${revision}`),
  };
}

function serverFrame(message: TerrascaleSyncV2MsgpackMessage): Uint8Array {
  return encodeTerrascaleSyncV2MsgpackEnvelope({
    features: 0,
    correlation: 1n,
    causation: 0n,
    message,
  });
}

describe("current reactive client", () => {
  test("uses generated frames for subscriptions, snapshots, and deltas", () => {
    const client = new TerraScaleSyncV2Client();
    client.setOnline(true);
    const opened = client.openSubscription({
      subscription: "orders-by-status",
      query: "from orders where status = $status",
      parameters: encoder.encode("open"),
    });
    const subscribe = decodeTerrascaleSyncV2MsgpackEnvelope(
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
      digest: syncV2ResultDigest(changed),
      checkpoint: encoder.encode("checkpoint-1-2"),
    }));
    assert.deepEqual(opened.subscription.view.getItems().map(entry => entry.identity), ["order-1", "order-2"]);

    const acknowledgement = client.checkpointAcknowledgement("orders-by-status");
    assert.equal(acknowledgement.kind, "checkpoint_ack");
  });

  test("applies authorization replacement, routing, and backpressure frames", () => {
    const client = new TerraScaleSyncV2Client();
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
    const client = new TerraScaleSyncV2Client();
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
        (error: unknown) => error instanceof SyncV2ViewError && error.code === "reset-required",
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
    const client = new TerraScaleSyncV2Client();
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
    const client = new TerraScaleSyncV2Client();
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
      (error: unknown) => error instanceof SyncV2ViewError && error.code === "reset-required",
    );
    assert.deepEqual(opened.subscription.view.getItems().map(entry => entry.identity), ["secret"]);
    assert.equal(opened.subscription.view.getRevision(), 4n);
    assert.equal(opened.subscription.view.getAuthorizationGeneration(), 5n);
  });

  test("rejects wrong-direction, unknown-subscription, and remote error frames", () => {
    const client = new TerraScaleSyncV2Client();
    assert.throws(
      () => client.receive(serverFrame({ kind: "unsubscribe", subscription: "missing" })),
      (error: unknown) => error instanceof TerraScaleSyncV2ClientError && error.code === "protocol_error",
    );
    assert.throws(
      () => client.receive(serverFrame({ kind: "snapshot", subscription: "missing", snapshot: wireSnapshot([]) })),
      (error: unknown) => error instanceof TerraScaleSyncV2ClientError && error.code === "unknown_subscription",
    );
    client.setOnline(true);
    assert.throws(
      () => client.receive(serverFrame({ kind: "error", code: "authentication_expired", detail: null })),
      (error: unknown) => error instanceof TerraScaleSyncV2ClientError && error.code === "remote_error",
    );
    assert.equal(client.getSnapshot().online, false);
  });
});
