import assert from "node:assert/strict";
import { describe, test } from "node:test";

import {
  decodeTerrascaleSyncV2MsgpackEnvelope,
  encodeTerrascaleSyncV2MsgpackEnvelope,
  type TerrascaleSyncV2MsgpackMessage,
  type TerrascaleSyncV2MsgpackSnapshot,
} from "./msgpack/generated/terrascale-sync-v2-msgpack.js";
import {
  TerraScaleSyncV2Client,
  TerraScaleSyncV2ClientError,
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
