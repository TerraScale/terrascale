// Round-trip conformance for the generated public
// `terrascale.sync.v2.msgpack` protocol (ADR 0025, qualification-gap-sweep
// row 28) over the engine's vendored golden-vector corpus:
// test/fixtures/terrascale.sync.v2/ (read-only byte-exact pin of TerraBaseV2
// crates/terrabase-sync-core/vectors/terrascale.sync.v2/).
//
// Every frame description in every vendored vector is mapped to the
// manifest's message vocabulary (same field names, checkpoints as bigint
// pairs, payloads as bytes), encoded with the generated TypeScript codec,
// decoded back, and compared for deep semantic equality. The vectors carry
// no mutation frames, so a synthetic sweep covers every manifest message —
// including every union variant and option branch — and asserts full
// manifest coverage.
//
// Run (imports the built SDK tree):
//
//   npm run build && node --test test/msgpack-corpus-roundtrip.test.mjs

import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import { describe, test } from "node:test";

import { msgpackHexToBytes, TerrascaleMsgpackError } from "../dist/msgpack/runtime/bounded-msgpack.js";
import {
  decodeTerrascaleSyncV2MsgpackEnvelope,
  encodeTerrascaleSyncV2MsgpackEnvelope,
  terrascaleSyncV2MsgpackMessageTags,
  validateTerrascaleSyncV2MsgpackMessage,
} from "../dist/msgpack/generated/terrascale-sync-v2-msgpack.js";

const vectorsDirectory = new URL("./fixtures/terrascale.sync.v2/vectors/", import.meta.url);

/** Maps one engine-corpus frame description onto the manifest vocabulary. */
function mapCheckpoint(checkpoint) {
  return {
    ends: checkpoint.ends.map(([peer, end]) => [BigInt(peer), BigInt(end)]),
    storage_sequence: BigInt(checkpoint.storage_sequence),
  };
}

function mapCorpusFrame(frame) {
  switch (frame.kind) {
    case "session_setup":
      return {
        kind: frame.kind,
        collections: frame.collections,
        resume: frame.resume === null ? null : mapCheckpoint(frame.resume),
      };
    case "session_accepted":
      return {
        kind: frame.kind,
        session: BigInt(frame.session),
        server_checkpoint: mapCheckpoint(frame.server_checkpoint),
        collections: frame.collections,
      };
    case "subscription_modify":
      return { kind: frame.kind, add: frame.add, remove: frame.remove };
    case "subscription_changed":
      return { kind: frame.kind, active: frame.active };
    case "checkpoint_ack":
      return { kind: frame.kind, checkpoint: mapCheckpoint(frame.checkpoint) };
    case "heartbeat":
      return { kind: frame.kind };
    case "delta_batch":
      return {
        kind: frame.kind,
        codec: "terrabase_native_1",
        payload: msgpackHexToBytes(frame.payload),
        next: mapCheckpoint(frame.next),
        within_budget: frame.within_budget,
        single_commit_oversize: frame.single_commit_oversize,
        commits: BigInt(frame.commits),
      };
    case "snapshot_answer":
      return {
        kind: frame.kind,
        codec: "terrabase_snapshot_1",
        payload: msgpackHexToBytes(frame.payload),
        checkpoint: mapCheckpoint(frame.checkpoint),
      };
    case "trigger":
      return {
        kind: frame.kind,
        collection: frame.collection,
        storage_sequence: BigInt(frame.storage_sequence),
        operation_id: frame.operation_id,
      };
    case "resync_required":
      return {
        kind: frame.kind,
        floor: mapCheckpoint(frame.floor),
        reason: frame.reason.replace(/-/gu, "_"),
      };
    case "region_route_change":
      return { kind: frame.kind, region: frame.region };
    case "error":
      return { kind: frame.kind, code: frame.code, detail: frame.detail ?? null };
    case "mutation_request":
      return {
        kind: frame.kind,
        collection: frame.collection,
        document: frame.document,
        idempotency: frame.idempotency,
        write: frame.put !== undefined
          ? {
            kind: "put",
            fields: Object.entries(frame.put).map(([key, value]) => ({
              key,
              value: typeof value === "number"
                ? { kind: "int_v", value: BigInt(Math.trunc(value)) }
                : typeof value === "boolean"
                  ? { kind: "bool_v", value }
                  : value === null
                    ? { kind: "null_v" }
                    : { kind: "text_v", value: String(value) },
            })),
          }
          : { kind: "delete" },
      };
    case "mutation_ack":
      return frame.outcome === "applied"
        ? {
          kind: frame.kind,
          outcome: {
            kind: "applied",
            storage_sequence: BigInt(frame.storage_sequence),
            operation_id: frame.operation_id,
          },
        }
        : { kind: frame.kind, outcome: { kind: "rejected", reason: frame.rejection } };
    default:
      assert.fail(`the vendored corpus carries the unmapped frame kind ${frame.kind}`);
  }
}

function roundTrip(envelope) {
  const wire = encodeTerrascaleSyncV2MsgpackEnvelope(envelope);
  const decoded = decodeTerrascaleSyncV2MsgpackEnvelope(wire);
  assert.deepStrictEqual(decoded, envelope);
  return wire.length;
}

describe("msgpack corpus round-trip", () => {
  const syntheticMessages = [
    { kind: "session_setup", collections: [], resume: null },
    {
      kind: "session_setup",
      collections: ["orders", "items"],
      resume: { ends: [[1n, 2n], [18446744073709551615n, 0n]], storage_sequence: 9007199254740993n },
    },
    {
      kind: "session_accepted",
      session: 18446744073709551615n,
      server_checkpoint: { ends: [], storage_sequence: 0n },
      collections: ["orders"],
    },
    { kind: "subscription_modify", add: ["orders"], remove: [] },
    { kind: "subscription_modify", add: [], remove: ["items", "audit"] },
    { kind: "subscription_changed", active: ["orders", "items", "audit"] },
    { kind: "checkpoint_ack", checkpoint: { ends: [[7n, 9n]], storage_sequence: 9n } },
    { kind: "heartbeat" },
    {
      kind: "delta_batch",
      codec: "terrabase_native_1",
      payload: new Uint8Array(0),
      next: { ends: [], storage_sequence: 1n },
      within_budget: true,
      single_commit_oversize: false,
      commits: 1n,
    },
    {
      kind: "delta_batch",
      codec: "terrabase_native_1",
      payload: new Uint8Array([1, 2, 3, 250, 255]),
      next: { ends: [[3n, 4n]], storage_sequence: 4n },
      within_budget: false,
      single_commit_oversize: true,
      commits: 1n,
    },
    {
      kind: "snapshot_answer",
      codec: "terrabase_snapshot_1",
      payload: new Uint8Array(4096).fill(0x5a),
      checkpoint: { ends: [[2n, 44n]], storage_sequence: 44n },
    },
    { kind: "trigger", collection: "orders", storage_sequence: 18446744073709551615n, operation_id: "op-000001" },
    { kind: "resync_required", floor: { ends: [], storage_sequence: 12n }, reason: "behind_retained_floor" },
    { kind: "resync_required", floor: { ends: [[1n, 1n]], storage_sequence: 12n }, reason: "history_compacted" },
    { kind: "region_route_change", region: "eu-central" },
    { kind: "history_expired", floor: { ends: [], storage_sequence: 3n } },
    { kind: "error", code: "unsupported_version", detail: null },
    { kind: "error", code: "overloaded", detail: "shedding: 42 live sessions" },
    {
      kind: "mutation_request",
      collection: "orders",
      document: "d-01",
      idempotency: "idem-000001",
      write: {
        kind: "put",
        fields: [
          { key: "f000", value: { kind: "text_v", value: "v0001" } },
          { key: "count", value: { kind: "int_v", value: -9223372036854775808n } },
          { key: "big", value: { kind: "int_v", value: 9223372036854775807n } },
          { key: "ratio", value: { kind: "float_v", value: 1.5 } },
          { key: "quiet", value: { kind: "float_v", value: Number.NaN } },
          { key: "flag", value: { kind: "bool_v", value: true } },
          { key: "absent", value: { kind: "null_v" } },
        ],
      },
    },
    { kind: "mutation_request", collection: "orders", document: "d-01", idempotency: "idem-000002", write: { kind: "delete" } },
    {
      kind: "mutation_ack",
      outcome: { kind: "applied", storage_sequence: 42n, operation_id: "op-000042" },
    },
    { kind: "mutation_ack", outcome: { kind: "rejected", reason: "scope_refused" } },
    { kind: "causal_token", token: new Uint8Array(64).fill(0xab), sequence: 7n },
    { kind: "causal_token", token: new Uint8Array(0), sequence: 0n },
    { kind: "auth_generation_changed", generation: 3n },
    {
      kind: "operation_progress",
      operation: new Uint8Array(16).fill(0x01),
      phase: "running",
      done: 5n,
      total: 10n,
      detail: null,
    },
    {
      kind: "operation_progress",
      operation: new Uint8Array([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]),
      phase: "completed",
      done: 10n,
      total: null,
      detail: "export finished",
    },
    { kind: "operation_progress", operation: new Uint8Array(16), phase: "failed", done: 0n, total: null, detail: null },
    { kind: "backpressure", mode: "pause" },
    { kind: "backpressure", mode: "resume" },
  ];

  test("every synthetic manifest message round-trips", () => {
    const covered = new Set();
    syntheticMessages.forEach((message, index) => {
      const wireLength = roundTrip({
        features: 0,
        correlation: BigInt(index + 1),
        causation: BigInt(index),
        message,
      });
      assert.ok(wireLength > 0);
      covered.add(message.kind);
    });
    const manifestKinds = Object.keys(terrascaleSyncV2MsgpackMessageTags);
    assert.deepEqual(
      [...covered].sort(),
      [...manifestKinds].sort(),
      "the synthetic sweep must cover every manifest message",
    );
  });

  test("every vendored engine vector frame round-trips through the generated codec", async () => {
    const files = (await readdir(vectorsDirectory)).filter((name) => name.endsWith(".json")).sort();
    assert.equal(files.length, 9, "the vendored corpus carries nine vectors");
    const kindsSeen = new Set();
    let frames = 0;
    let bytesEncoded = 0;
    // Frame descriptions appear as client_frame.frame, server_frame.frame,
    // and serve_delta.expect_frame; collect every object shaped like one.
    const corpusFrames = [];
    const collectFrames = (node) => {
      if (Array.isArray(node)) {
        for (const entry of node) collectFrames(entry);
        return;
      }
      if (typeof node !== "object" || node === null) return;
      if (
        typeof node.kind === "string"
        && terrascaleSyncV2MsgpackMessageTags[node.kind] !== undefined
        && typeof node.correlation === "number"
        && typeof node.causation === "number"
      ) {
        corpusFrames.push(node);
      }
      for (const value of Object.values(node)) collectFrames(value);
    };
    for (const name of files) {
      const vector = JSON.parse(await readFile(new URL(name, vectorsDirectory), "utf8"));
      assert.equal(vector.protocol, "terrascale.sync.v2");
      collectFrames(vector.steps ?? []);
    }
    for (const frame of corpusFrames) {
      const message = mapCorpusFrame(frame);
      assert.ok(
        terrascaleSyncV2MsgpackMessageTags[message.kind] !== undefined,
        `the manifest has no tag for the corpus kind ${message.kind}`,
      );
      bytesEncoded += roundTrip({
        features: 0,
        correlation: BigInt(frame.correlation),
        causation: BigInt(frame.causation),
        message,
      });
      frames++;
      kindsSeen.add(message.kind);
    }
    // The vendored corpus carries the delivery vocabulary; the synthetic
    // sweep covers the rest of the manifest (mutations, control additions).
    assert.deepEqual([...kindsSeen].sort(), [
      "checkpoint_ack",
      "delta_batch",
      "region_route_change",
      "resync_required",
      "session_accepted",
      "session_setup",
      "snapshot_answer",
      "trigger",
    ]);
    assert.ok(frames > 20, `expected a real frame population, saw ${frames}`);
    assert.ok(bytesEncoded > 1000);
  });

  test("the exact-shape validator rejects wrong in-memory values", () => {
    const badValues = [
      { kind: "not_a_message" },
      { kind: "heartbeat", extra: 1 },
      { kind: "session_setup", collections: "orders", resume: null },
      { kind: "session_setup", collections: ["orders"], resume: false },
      { kind: "checkpoint_ack", checkpoint: { ends: [], storage_sequence: 42 } },
      { kind: "trigger", collection: "orders", storage_sequence: -1n, operation_id: "op" },
      { kind: "error", code: "not_a_code", detail: null },
      { kind: "error", code: "overloaded", detail: 7 },
      { kind: "region_route_change", region: "x".repeat(257) },
      {
        kind: "mutation_request",
        collection: "orders",
        document: "d",
        idempotency: "i",
        write: { kind: "put", fields: [{ key: "k", value: { kind: "nope" } }] },
      },
      {
        kind: "operation_progress",
        operation: new Uint8Array(15),
        phase: "queued",
        done: 0n,
        total: null,
        detail: null,
      },
      null,
  ];
    for (const bad of badValues) {
      assert.throws(() => validateTerrascaleSyncV2MsgpackMessage(bad), TerrascaleMsgpackError);
    }
    validateTerrascaleSyncV2MsgpackMessage({ kind: "heartbeat" });
  });

  test("encoding enforces the manifest bounds on the write side", () => {
    const envelopeFor = (message) => ({ features: 0, correlation: 1n, causation: 0n, message });
    assert.throws(
      () => encodeTerrascaleSyncV2MsgpackEnvelope(
        envelopeFor({ kind: "region_route_change", region: "x".repeat(257) }),
      ),
      TerrascaleMsgpackError,
    );
    assert.throws(
      () => encodeTerrascaleSyncV2MsgpackEnvelope(
        envelopeFor({ kind: "trigger", collection: "orders", storage_sequence: -1n, operation_id: "op-1" }),
      ),
      /validate|does not match/u,
    );
    assert.throws(
      () => encodeTerrascaleSyncV2MsgpackEnvelope(
        envelopeFor({
          kind: "delta_batch",
          codec: "terrabase_native_1",
          payload: new Uint8Array(4194305),
          next: { ends: [], storage_sequence: 0n },
          within_budget: true,
          single_commit_oversize: false,
          commits: 1n,
        }),
      ),
      TerrascaleMsgpackError,
    );
  });

  test("a large-but-legal snapshot chunk round-trips", () => {
    const payload = new Uint8Array(4 * 1024 * 1024);
    for (let index = 0; index < payload.length; index++) {
      payload[index] = index & 0xff;
    }
    const wire = roundTrip({
      features: 0,
      correlation: 2n,
      causation: 1n,
      message: {
        kind: "snapshot_answer",
        codec: "terrabase_snapshot_1",
        payload,
        checkpoint: { ends: [[1n, 3n]], storage_sequence: 3n },
      },
    });
    assert.ok(wire > 4 * 1024 * 1024);
  });
});
