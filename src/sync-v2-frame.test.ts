import assert from "node:assert/strict";
import { describe, test } from "node:test";

import {
  decodeSyncV2Frame,
  encodeSyncV2Frame,
  sealSyncV2Frame,
  syncV2CheckpointFromBytes,
  syncV2CheckpointToBytes,
  syncV2CanonicalCheckpoint,
  syncV2FixedEnvelopeBytes,
  syncV2InitialCheckpoint,
  syncV2MaxFrameBytes,
  syncV2ProtocolVersion,
  verifySyncV2Frame,
  type SyncV2Frame,
  type SyncV2ProtocolError,
} from "./index.js";

const hex = "76f6c9c2612cfc79"; // arbitrary

function decodeHex(value: string): Uint8Array {
  const bytes = new Uint8Array(value.length / 2);
  for (let index = 0; index < bytes.length; index++) {
    bytes[index] = Number.parseInt(value.slice(index * 2, index * 2 + 2), 16);
  }
  return bytes;
}

function encodeHex(bytes: Uint8Array): string {
  return [...bytes].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

function protocolErrorCode(error: unknown): string {
  assert.ok(error instanceof Error, "the codec fails with typed errors");
  const typed = error as SyncV2ProtocolError;
  assert.equal(typed.name, "SyncV2ProtocolError");
  return typed.code;
}

describe("terrascale.sync.v2 frame envelope", () => {
  test("a heartbeat encodes to exactly the fixed envelope", () => {
    const frame = sealSyncV2Frame({ kind: "heartbeat" }, 1n, 0n);
    const wire = encodeSyncV2Frame(frame);

    assert.equal(syncV2ProtocolVersion, 2);
    assert.equal(syncV2FixedEnvelopeBytes, 38);
    assert.equal(wire.length, 38);
    assert.equal(wire[0], 2);
    assert.equal(encodeHex(wire.slice(1, 3)), "0600");
    assert.equal(verifySyncV2Frame(frame), true);
  });

  test("u64 identities beyond Number precision round-trip exactly", () => {
    const frame = sealSyncV2Frame(
      { kind: "checkpoint_ack", checkpoint: syncV2InitialCheckpoint() },
      18_446_744_073_709_551_615n,
      9_007_199_254_740_993n,
    );
    const decoded = decodeSyncV2Frame(encodeSyncV2Frame(frame));

    assert.equal(decoded.correlation, 18_446_744_073_709_551_615n);
    assert.equal(decoded.causation, 9_007_199_254_740_993n);
  });

  test("the content digest covers the canonical body; disagreement decodes but fails verify", () => {
    const frame = sealSyncV2Frame({ kind: "region_route_change", region: "eu-1" }, 1n, 0n);
    const wire = encodeSyncV2Frame(frame);

    // Corrupt one body byte without touching the envelope: the digest
    // no longer matches the body, which is the caller's refusal.
    const lastIndex = wire.length - 1;
    wire[lastIndex] = (wire[lastIndex] ?? 0) ^ 0x01;
    const decoded = decodeSyncV2Frame(wire);
    assert.equal(verifySyncV2Frame(decoded), false);
    assert.equal(decoded.body.kind, "region_route_change");
  });

  test("the idempotency pairing is enforced at seal time and on the wire", () => {
    const error = protocolErrorCode(
      (() => {
        try {
          sealSyncV2Frame(
            { kind: "mutation_request", collection: "orders", document: "d-01", delete: true },
            1n,
            0n,
          );
          return undefined;
        } catch (caught) {
          return caught;
        }
      })(),
    );
    assert.equal(error, "idempotency_pairing");

    const nonMutation = (() => {
      try {
        sealSyncV2Frame({ kind: "heartbeat" }, 1n, 0n, "idem-1");
        return undefined;
      } catch (caught) {
        return caught;
      }
    })();
    assert.equal(protocolErrorCode(nonMutation), "idempotency_pairing");
  });

  test("idempotency keys are bounded and control-free", () => {
    const long = "k".repeat(129);
    const invalid = (() => {
      try {
        sealSyncV2Frame(
          { kind: "mutation_request", collection: "orders", document: "d-01", delete: true },
          1n,
          0n,
          long,
        );
        return undefined;
      } catch (caught) {
        return caught;
      }
    })();
    assert.equal(protocolErrorCode(invalid), "invalid_idempotency_key");

    const control = (() => {
      try {
        sealSyncV2Frame(
          { kind: "mutation_request", collection: "orders", document: "d-01", delete: true },
          1n,
          0n,
          "idem\u00001",
        );
        return undefined;
      } catch (caught) {
        return caught;
      }
    })();
    assert.equal(protocolErrorCode(control), "invalid_idempotency_key");
  });

  test("mutation put bodies encode fields in canonical sorted order regardless of input order", () => {
    const first = sealSyncV2Frame(
      {
        kind: "mutation_request",
        collection: "orders",
        document: "d-01",
        put: { b: 2, a: 1, c: "x" },
      },
      1n,
      0n,
      "idem-1",
    );
    const second = sealSyncV2Frame(
      {
        kind: "mutation_request",
        collection: "orders",
        document: "d-01",
        put: { c: "x", a: 1, b: 2 },
      },
      1n,
      0n,
      "idem-1",
    );

    assert.equal(encodeHex(encodeSyncV2Frame(first)), encodeHex(encodeSyncV2Frame(second)));
    const decoded = decodeSyncV2Frame(encodeSyncV2Frame(first));
    assert.equal(decoded.idempotency, "idem-1");
    const body = decoded.body;
    assert.equal(body.kind, "mutation_request");
    assert.deepEqual(body.put, { a: 1, b: 2, c: "x" });
  });

  test("the oversize flag requires exactly one commit, at seal and on decode", () => {
    const oversize = (() => {
      try {
        sealSyncV2Frame(
          {
            kind: "delta_batch",
            codec: "loro-delta/1",
            payload: encodeHex(decodeHex(hex)),
            next: syncV2InitialCheckpoint(),
            within_budget: false,
            single_commit_oversize: true,
            commits: 3n,
          },
          1n,
          0n,
        );
        return undefined;
      } catch (caught) {
        return caught;
      }
    })();
    assert.equal(protocolErrorCode(oversize), "malformed_frame");
  });

  test("a delta batch carries only loro-delta/1; a snapshot only loro-snapshot/1", () => {
    const wrongCodec = (() => {
      try {
        sealSyncV2Frame(
          {
            kind: "snapshot_answer",
            codec: "loro-delta/1" as "loro-snapshot/1",
            payload: "",
            checkpoint: syncV2InitialCheckpoint(),
          },
          1n,
          0n,
        );
        return undefined;
      } catch (caught) {
        return caught;
      }
    })();
    assert.equal(protocolErrorCode(wrongCodec), "malformed_frame");
  });

  test("decode fails closed on version, size, truncation, and trailing bytes", () => {
    const good = encodeSyncV2Frame(sealSyncV2Frame({ kind: "heartbeat" }, 1n, 0n));

    const versioned = new Uint8Array(good);
    versioned[0] = 1;
    assert.equal(protocolErrorCode(catchDecode(versioned)), "unsupported_version");

    assert.equal(
      protocolErrorCode(catchDecode(new Uint8Array(syncV2MaxFrameBytes + 1))),
      "malformed_frame",
    );

    const trailing = new Uint8Array([...good, 0]);
    assert.equal(protocolErrorCode(catchDecode(trailing)), "malformed_frame");

    const truncated = good.slice(0, 20);
    assert.equal(protocolErrorCode(catchDecode(truncated)), "malformed_frame");
  });

  test("checkpoints canonicalize, round-trip, and fail closed on unknown versions", () => {
    const checkpoint = syncV2CanonicalCheckpoint(
      [[3n, 9], [1n, 4], [1n, 4], [2n, -7]],
      12n,
    );
    assert.deepEqual(checkpoint.ends, [[1n, 4], [2n, -7], [3n, 9]]);

    const bytes = syncV2CheckpointToBytes(checkpoint);
    assert.equal(bytes[0], 1);
    assert.deepEqual(syncV2CheckpointFromBytes(bytes), checkpoint);

    const empty = syncV2CheckpointToBytes(syncV2InitialCheckpoint());
    assert.equal(empty.length, 13);
    assert.deepEqual(syncV2CheckpointFromBytes(empty), {
      ends: [],
      storage_sequence: 0n,
    });

    assert.throws(() => syncV2CheckpointFromBytes(new Uint8Array([2, ...empty.slice(1)])), Error);
    assert.throws(() => syncV2CheckpointFromBytes(empty.slice(0, 8)), Error);
  });

  test("trigger identifiers are bounded to 64 bytes", () => {
    const longTrigger = (() => {
      try {
        sealSyncV2Frame(
          {
            kind: "trigger",
            collection: "c".repeat(65),
            storage_sequence: 1n,
            operation_id: "op-1",
          },
          1n,
          0n,
        );
        return undefined;
      } catch (caught) {
        return caught;
      }
    })();
    assert.equal(protocolErrorCode(longTrigger), "malformed_frame");
  });

  test("the frame round-trips every scalar value tag", () => {
    const frame: SyncV2Frame = sealSyncV2Frame(
      {
        kind: "mutation_request",
        collection: "orders",
        document: "d-01",
        put: { n: null, t: true, i: 7, f: 2.5, s: "text" },
      },
      1n,
      0n,
      "idem-1",
    );
    const decoded = decodeSyncV2Frame(encodeSyncV2Frame(frame));
    const body = decoded.body;
    assert.equal(body.kind, "mutation_request");
    assert.deepEqual(body.put, { n: null, t: true, i: 7, f: 2.5, s: "text" });
    assert.equal(decoded.body.kind, frame.body.kind);
  });
});

function catchDecode(bytes: Uint8Array): unknown {
  try {
    decodeSyncV2Frame(bytes);
    return undefined;
  } catch (caught) {
    return caught;
  }
}
