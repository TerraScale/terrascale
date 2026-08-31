import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { describe, test } from "node:test";

import { TerrascaleMsgpackError } from "../dist/msgpack/runtime/bounded-msgpack.js";
import {
  decodeTerrascaleSyncV2MsgpackEnvelope,
  encodeTerrascaleSyncV2MsgpackEnvelope,
  terrascaleSyncV2MsgpackManifestSha256,
  terrascaleSyncV2MsgpackMessageTags,
  validateTerrascaleSyncV2MsgpackMessage,
  validateTerrascaleSyncV2MsgpackProjectedField,
  validateTerrascaleSyncV2MsgpackResultItem,
} from "../dist/msgpack/generated/terrascale-sync-v2-msgpack.js";
import { encodeReferenceMessagePack } from "./support/reference-msgpack.mjs";

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

const goldenMessages = [
  { kind: "subscribe", subscription: "s", query: "q", parameters: Uint8Array.from([1, 2]), checkpoint: null },
  { kind: "unsubscribe", subscription: "s" },
  {
    kind: "snapshot",
    subscription: "s",
    snapshot: {
      view_generation: 1n,
      authorization_generation: 2n,
      revision: 3n,
      items: [{ identity: "d", fields: [field("x", "y")] }],
      digest: "a".repeat(64),
      checkpoint: Uint8Array.from([0x63]),
    },
  },
  {
    kind: "delta",
    subscription: "s",
    view_generation: 1n,
    from_revision: 3n,
    to_revision: 4n,
    operations: [
      { kind: "insert", identity: "e", position: 1, fields: [field("x", "i")] },
      { kind: "update", identity: "d", fields: [field("x", "u")] },
      {
        kind: "patch",
        identity: "d",
        changes: [
          { path: "x", value: Uint8Array.from([0x70]) },
          { path: "z", value: null },
        ],
      },
      { kind: "remove", identity: "e" },
      { kind: "move", identity: "d", position: 0 },
    ],
    digest: "b".repeat(64),
    checkpoint: Uint8Array.from([0x64]),
  },
  {
    kind: "reset",
    subscription: "s",
    reason: "generation_changed",
    snapshot: {
      view_generation: 2n,
      authorization_generation: 2n,
      revision: 5n,
      items: [{ identity: "d", fields: [field("x", "y")] }],
      digest: "a".repeat(64),
      checkpoint: Uint8Array.from([0x65]),
    },
  },
  { kind: "heartbeat" },
  { kind: "checkpoint_ack", subscription: "s", checkpoint: Uint8Array.from([0x66]) },
  {
    kind: "authorization_changed",
    subscription: "s",
    authorization_generation: 3n,
    removed: ["d"],
    snapshot: {
      view_generation: 2n,
      authorization_generation: 3n,
      revision: 5n,
      items: [],
      digest: "a".repeat(64),
      checkpoint: Uint8Array.from([0x67]),
    },
  },
  { kind: "region_route_change", region: "FRA" },
  { kind: "error", code: "overloaded", detail: "retry" },
  { kind: "backpressure", mode: "pause" },
];

// These are the hand-audited numeric wire arrays. They intentionally repeat
// tag, enum, union, and field positions instead of consulting generated maps.
// The test-only generic writer then acts as a producer independent of both
// the generated SDK codec and its bounded MessagePack runtime.
const binary = (...bytes) => Uint8Array.from(bytes);
const projectedFields = [["x", binary(0x79)]];
const snapshotWire = (view, authorization, revision, checkpoint, items = [["d", projectedFields]]) => [
  view,
  authorization,
  revision,
  items,
  "a".repeat(64),
  binary(checkpoint),
];
const goldenWireValues = [
  [2, 1, 0, 1, 1, 0, ["s", "q", binary(1, 2), null]],
  [2, 1, 0, 2, 2, 1, ["s"]],
  [2, 1, 0, 3, 3, 2, ["s", snapshotWire(1, 2, 3, 0x63)]],
  [2, 1, 0, 4, 4, 3, [
    "s",
    1,
    3,
    4,
    [
      [1, "e", 1, [["x", binary(0x69)]]],
      [2, "d", [["x", binary(0x75)]]],
      [3, "d", [["x", binary(0x70)], ["z", null]]],
      [4, "e"],
      [5, "d", 0],
    ],
    "b".repeat(64),
    binary(0x64),
  ]],
  [2, 1, 0, 5, 5, 4, ["s", 3, snapshotWire(2, 2, 5, 0x65)]],
  [2, 1, 0, 6, 6, 5, []],
  [2, 1, 0, 7, 7, 6, ["s", binary(0x66)]],
  [2, 1, 0, 8, 8, 7, ["s", 3, ["d"], snapshotWire(2, 3, 5, 0x67, [])]],
  [2, 1, 0, 9, 9, 8, ["FRA"]],
  [2, 1, 0, 10, 10, 9, [7, "retry"]],
  [2, 1, 0, 11, 11, 10, [1]],
];

const goldenFixture = JSON.parse(await readFile(
  new URL("./fixtures/terrascale.sync.v2.msgpack/message-vectors.json", import.meta.url),
  "utf8",
));

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
  test("committed independent bytes pin every current message", () => {
    assert.equal(goldenFixture.protocol, "terrascale.sync.v2.msgpack");
    assert.equal(goldenFixture.version, "2.1");
    assert.equal(goldenFixture.manifestSha256, terrascaleSyncV2MsgpackManifestSha256);
    assert.deepEqual(
      goldenFixture.vectors.map(vector => vector.name).sort(),
      Object.keys(terrascaleSyncV2MsgpackMessageTags).sort(),
    );
    assert.equal(goldenFixture.vectors.length, goldenMessages.length);

    goldenFixture.vectors.forEach((vector, index) => {
      const message = goldenMessages[index];
      assert.equal(vector.name, message.kind);
      const envelope = {
        features: 0,
        correlation: BigInt(vector.correlation),
        causation: BigInt(vector.causation),
        message,
      };
      const wire = Uint8Array.from(Buffer.from(vector.hex, "hex"));
      assert.equal(
        Buffer.from(encodeReferenceMessagePack(goldenWireValues[index])).toString("hex"),
        vector.hex,
        `${vector.name} differs from the independent MessagePack oracle`,
      );
      assert.deepStrictEqual(decodeTerrascaleSyncV2MsgpackEnvelope(wire), envelope);
      assert.equal(
        Buffer.from(encodeTerrascaleSyncV2MsgpackEnvelope(envelope)).toString("hex"),
        vector.hex,
      );
    });
  });

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
          items: [{ identity: "document-1", fields: [{ path: "value", value: new Uint8Array(1_048_577) }] }],
        },
      })),
      TerrascaleMsgpackError,
    );
  });

  test("projected paths must be nonempty and item values must stay below the aggregate bound", () => {
    const envelope = (message) => ({ features: 0, correlation: 1n, causation: 0n, message });
    assert.throws(
      () => validateTerrascaleSyncV2MsgpackProjectedField({ path: "", value: new Uint8Array() }),
      (error) => error instanceof TerrascaleMsgpackError && error.code === "invalid_shape",
    );

    const oversizedFields = [
      field("first", "a".repeat(524_289)),
      field("second", "b".repeat(524_289)),
    ];
    assert.throws(
      () => validateTerrascaleSyncV2MsgpackResultItem({ identity: "document-1", fields: oversizedFields }),
      (error) => error instanceof TerrascaleMsgpackError && error.code === "invalid_shape",
    );
    assert.throws(
      () => encodeTerrascaleSyncV2MsgpackEnvelope(envelope({
        kind: "snapshot",
        subscription: "view",
        snapshot: {
          ...snapshot,
          items: [{ identity: "document-1", fields: oversizedFields }],
        },
      })),
      (error) => error instanceof TerrascaleMsgpackError && error.code === "invalid_shape",
    );
  });
});
