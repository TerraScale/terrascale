import assert from "node:assert/strict";
import { describe, test } from "node:test";

import { TerrascaleMsgpackError } from "../dist/msgpack/runtime/bounded-msgpack.js";
import {
  decodeTerrascaleSyncV2MsgpackEnvelope,
  encodeTerrascaleSyncV2MsgpackEnvelope,
  terrascaleSyncV2MsgpackMessageTags,
  validateTerrascaleSyncV2MsgpackMessage,
} from "../dist/msgpack/generated/terrascale-sync-v2-msgpack.js";

const encoder = new TextEncoder();
const field = (path, value) => ({ path, value: encoder.encode(value) });
const snapshot = {
  view_generation: 1n,
  authorization_generation: 1n,
  revision: 1n,
  items: [{ identity: "document-1", fields: [field("title", "ready")] }],
  digest: "a".repeat(64),
  checkpoint: encoder.encode("checkpoint-1"),
};

const corpus = [
  {
    kind: "subscribe",
    subscription: "documents-by-status",
    query: "from documents where status = $status",
    parameters: encoder.encode("ready"),
    checkpoint: null,
  },
  {
    kind: "subscribe",
    subscription: "documents-resumed",
    query: "from documents",
    parameters: new Uint8Array(),
    checkpoint: encoder.encode("resume-token"),
  },
  { kind: "unsubscribe", subscription: "documents-resumed" },
  { kind: "snapshot", subscription: "documents-by-status", snapshot },
  {
    kind: "delta",
    subscription: "documents-by-status",
    view_generation: 1n,
    from_revision: 1n,
    to_revision: 2n,
    operations: [
      { kind: "insert", identity: "document-2", position: 1, fields: [field("title", "new")] },
      { kind: "update", identity: "document-1", fields: [field("title", "updated")] },
      {
        kind: "patch",
        identity: "document-1",
        changes: [
          { path: "title", value: encoder.encode("patched") },
          { path: "removed", value: null },
        ],
      },
      { kind: "move", identity: "document-2", position: 0 },
      { kind: "remove", identity: "document-1" },
    ],
    digest: "b".repeat(64),
    checkpoint: encoder.encode("checkpoint-2"),
  },
  {
    kind: "reset",
    subscription: "documents-by-status",
    reason: "checkpoint_expired",
    snapshot: { ...snapshot, view_generation: 2n, revision: 2n },
  },
  { kind: "heartbeat" },
  { kind: "checkpoint_ack", subscription: "documents-by-status", checkpoint: encoder.encode("checkpoint-2") },
  {
    kind: "authorization_changed",
    subscription: "documents-by-status",
    authorization_generation: 2n,
    removed: ["document-1"],
    snapshot: { ...snapshot, view_generation: 2n, authorization_generation: 2n, revision: 2n },
  },
  { kind: "region_route_change", region: "FRA" },
  { kind: "error", code: "authentication_expired", detail: null },
  { kind: "error", code: "overloaded", detail: "retry after reconnect" },
  { kind: "backpressure", mode: "pause" },
  { kind: "backpressure", mode: "resume" },
];

function roundTrip(message, index) {
  const envelope = {
    features: 0,
    correlation: BigInt(index + 1),
    causation: BigInt(index),
    message,
  };
  const wire = encodeTerrascaleSyncV2MsgpackEnvelope(envelope);
  assert.deepStrictEqual(decodeTerrascaleSyncV2MsgpackEnvelope(wire), envelope);
  return wire;
}

describe("msgpack corpus round-trip", () => {
  test("the current semantic corpus covers and round-trips every manifest message", () => {
    const covered = new Set();
    let bytes = 0;
    corpus.forEach((message, index) => {
      bytes += roundTrip(message, index).length;
      covered.add(message.kind);
    });
    assert.deepEqual([...covered].sort(), Object.keys(terrascaleSyncV2MsgpackMessageTags).sort());
    assert.ok(bytes > 500);
  });

  test("all reset reasons and public error codes round-trip", () => {
    const resetReasons = [
      "checkpoint_expired",
      "retention_advanced",
      "generation_changed",
      "checkpoint_not_current",
      "view_diverged",
      "oversized",
      "invalid_operations",
      "invalid_checkpoint",
      "backpressure",
    ];
    resetReasons.forEach((reason, index) => {
      roundTrip({ kind: "reset", subscription: "view", reason, snapshot }, index);
    });
    const errorCodes = [
      "unsupported_version",
      "malformed_frame",
      "authentication_expired",
      "permission_denied",
      "checkpoint_refused",
      "slow_consumer",
      "overloaded",
      "unavailable",
    ];
    errorCodes.forEach((code, index) => roundTrip({ kind: "error", code, detail: null }, index));
  });

  test("exact-shape validation rejects values outside the manifest", () => {
    const bad = [
      null,
      { kind: "unknown" },
      { kind: "heartbeat", extra: true },
      { kind: "subscribe", subscription: "view", query: "from docs", parameters: "not-bytes", checkpoint: null },
      { kind: "delta", subscription: "view", view_generation: 1, from_revision: 1n, to_revision: 2n, operations: [], digest: "a".repeat(64), checkpoint: encoder.encode("c") },
      { kind: "error", code: "other", detail: null },
      { kind: "backpressure", mode: "wait" },
    ];
    for (const value of bad) {
      assert.throws(() => validateTerrascaleSyncV2MsgpackMessage(value), TerrascaleMsgpackError);
    }
  });

  test("write-side bounds reject oversized query, checkpoint, and projected value fields", () => {
    const envelope = (message) => ({ features: 0, correlation: 1n, causation: 0n, message });
    assert.throws(
      () => encodeTerrascaleSyncV2MsgpackEnvelope(envelope({
        kind: "subscribe",
        subscription: "view",
        query: "x".repeat(262_145),
        parameters: new Uint8Array(),
        checkpoint: null,
      })),
      TerrascaleMsgpackError,
    );
    assert.throws(
      () => encodeTerrascaleSyncV2MsgpackEnvelope(envelope({
        kind: "checkpoint_ack",
        subscription: "view",
        checkpoint: new Uint8Array(4097),
      })),
      TerrascaleMsgpackError,
    );
    assert.throws(
      () => encodeTerrascaleSyncV2MsgpackEnvelope(envelope({
        kind: "snapshot",
        subscription: "view",
        snapshot: {
          ...snapshot,
          items: [{ identity: "document-1", fields: [{ path: "value", value: new Uint8Array(65_537) }] }],
        },
      })),
      TerrascaleMsgpackError,
    );
  });
});
