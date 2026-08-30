// Native-corpus conformance for the `terrascale.sync.v2` reactive client:
// runs the client codec, the client delivery store, and the authorized
// result-view engine against every vector of the vendored engine corpus.
//
// Corpus: TerraBase `crates/terrabase-sync-core/vectors/terrascale.sync.v2/`
// (schema `terrascale.sync.v2/vector-corpus@2`), vendored byte-exact under
// `test/fixtures/terrascale.sync.v2/` together with the engine's SHA-256
// integrity manifest. The report produced by the engine's own reference
// harness (`cargo run -p terrabase-sync-conformance -- --manifest ...`) is
// vendored next to it as `engine-reference-report.json`; this suite proves
// the TypeScript harness agrees with it on every check name both contracts
// define, and then drives the client beyond the wire contract:
//
//   - checkpoint round-trip: every delivery checkpoint decoded from real
//     corpus wire bytes round-trips through the checkpoint codec (and the
//     base64 persistence surface the session resumes from), and every view
//     checkpoint round-trips through its opaque token;
//   - patch application: each vector's journal (`journal_before` + `apply`
//     steps) is folded through an independent result model into
//     Insert/Patch/Remove revisions whose digest is computed from the model,
//     never from the store — `applyDelta` must reproduce the same result by
//     applying the operations, or the digest diverges and the view demands a
//     reset;
//   - reset: stale or regressing snapshots are refused, a fresh authorized
//     snapshot installs, and an old-generation delta afterwards reports
//     `reset-required` (the `resync_required` contract);
//   - authorization-generation change: stale generations, same-generation
//     replacements, and mismatched replacement generations are refused, and
//     a valid transition applies visibility removals before installing the
//     replacement.
//
// Run (the suite imports the built SDK):
//
//   npm run build && node --test test/sync-v2-native-corpus-view.test.mjs

import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readdir, readFile } from "node:fs/promises";
import { describe, test } from "node:test";

import {
  decodeSyncV2Frame,
  encodeSyncV2Frame,
  runSyncV2Conformance,
  syncV2CheckpointFromBytes,
  syncV2CheckpointToBytes,
  syncV2CorpusCategories,
  syncV2PayloadBytesOf,
  syncV2ResultDigest,
  SyncV2ResultViewStore,
  SyncV2ViewCheckpoint,
  verifySyncV2Frame,
} from "../dist/index.js";
import { TerraScaleSyncV2Store } from "../dist/sync/v2/session.js";

const fixtureDirectory = new URL("./fixtures/terrascale.sync.v2/", import.meta.url);
const encoder = new TextEncoder();

// The check names both the Rust reference harness and this SDK's harness
// define identically in the per-language harness contract: for these, the
// executed counts must agree per vector. (`apply_completes` and
// `manifest_entry_matches` are server-side/kit-side bookkeeping the client
// contract records differently; `corpus_protocol_identity` is folded into
// the client's schema-identity check; `serve_frame_bytes_exact` is counted
// one-sidedly — the reference also records serve-nothing confirmations, so
// the client count is asserted never to exceed it.)
const contractCheckNames = [
  "frame_encode_bytes_exact",
  "frame_decode_roundtrip",
  "frame_digest_verifies",
  "frame_envelope_identity",
  "decode_rejects_typed_error",
  "budget_class_matches",
  "budget_within_budget",
  "budget_payload_accounting",
  "budget_bytes_per_delivered_change",
  "budget_delta_below_snapshot",
  "serve_frame_matches_description",
  "trigger_content_free",
  "trigger_within_budget",
];

function bytesOfHex(hex) {
  const bytes = new Uint8Array(hex.length / 2);
  for (let index = 0; index < bytes.length; index++) {
    bytes[index] = Number.parseInt(hex.slice(index * 2, index * 2 + 2), 16);
  }
  return bytes;
}

function bytesEqual(left, right) {
  return left.length === right.length && left.every((byte, index) => byte === right[index]);
}

function bytesOfBase64(base64) {
  return Uint8Array.from(atob(base64), (char) => char.charCodeAt(0));
}

function base64Of(bytes) {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

function sha256Of(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

/** The checkpoints one frame body carries, by delivery kind. */
function checkpointsOf(body) {
  switch (body.kind) {
    case "session_setup":
      return body.resume === null ? [] : [body.resume];
    case "session_accepted":
      return [body.server_checkpoint];
    case "checkpoint_ack":
      return [body.checkpoint];
    case "delta_batch":
      return [body.next];
    case "snapshot_answer":
      return [body.checkpoint];
    case "resync_required":
      return [body.floor];
    default:
      return [];
  }
}

function checkpointEquals(left, right) {
  return left.storage_sequence === right.storage_sequence
    && left.ends.length === right.ends.length
    && left.ends.every((pair, index) => {
      const expected = right.ends[index];
      return expected !== undefined && pair[0] === expected[0] && pair[1] === expected[1];
    });
}

const initialDeliveryCheckpoint = { ends: [], storage_sequence: 0n };

// --- The independent result model the view engine is measured against ------

function encodeScalar(value) {
  if (value === null) {
    return null;
  }
  if (typeof value === "boolean") {
    return encoder.encode(`b:${value}`);
  }
  if (typeof value === "number") {
    return encoder.encode(`n:${value}`);
  }
  if (typeof value === "string") {
    return encoder.encode(`s:${value}`);
  }
  throw new Error(`unsupported journal value: ${typeof value}`);
}

/**
 * Folds journal commits into ordered result items, emitting the result
 * operation each commit corresponds to. The emitted operations and the
 * maintained items are the model; the store must independently agree.
 */
class ResultModel {
  constructor() {
    this.items = [];
  }

  apply(commit) {
    const index = this.items.findIndex((item) => item.identity === commit.document);
    if (commit.delete === true) {
      if (index === -1) {
        return null;
      }
      this.items.splice(index, 1);
      return { kind: "Remove", identity: commit.document };
    }
    const fields = Object.entries(commit.put ?? {});
    if (index === -1) {
      const value = {};
      for (const [path, raw] of fields) {
        const bytes = encodeScalar(raw);
        if (bytes !== null) {
          value[path] = bytes;
        }
      }
      this.items.push({ identity: commit.document, value });
      return { kind: "Insert", identity: commit.document, position: this.items.length - 1, value };
    }
    const value = { ...this.items[index].value };
    const changes = [];
    for (const [path, raw] of fields) {
      const bytes = encodeScalar(raw);
      changes.push(bytes === null ? { path, value: null } : { path, value: bytes });
      if (bytes === null) {
        delete value[path];
      } else {
        value[path] = bytes;
      }
    }
    this.items[index] = { identity: commit.document, value };
    return { kind: "Patch", identity: commit.document, changes };
  }
}

function modelSnapshot(items, { viewGeneration = 1n, authorizationGeneration = 1n, revision = 1n, token } = {}) {
  return {
    view_generation: viewGeneration,
    authorization_generation: authorizationGeneration,
    revision,
    items: items.map((item) => ({ identity: item.identity, value: { ...item.value } })),
    digest: syncV2ResultDigest(items),
    checkpoint: new SyncV2ViewCheckpoint(token ?? `view-checkpoint-${viewGeneration}-${revision}`),
  };
}

// --- Corpus loading ----------------------------------------------------------

async function loadCorpus() {
  const manifest = JSON.parse(await readFile(new URL("manifest.json", fixtureDirectory), "utf8"));
  const listed = (await readdir(new URL("vectors", fixtureDirectory))).sort();
  const entries = [...manifest.vectors].sort((left, right) => (left.file < right.file ? -1 : 1));

  const vectors = [];
  for (const entry of entries) {
    const bytes = new Uint8Array(await readFile(new URL(`vectors/${entry.file}`, fixtureDirectory)));
    vectors.push({ entry, bytes, doc: JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)) });
  }
  return { manifest, listed, entries, vectors };
}

const corpus = await loadCorpus();

describe("terrascale.sync.v2 native-corpus conformance (client codec + view engine)", () => {
  test("the vendored corpus is the engine's committed corpus, SHA-256 verified", () => {
    assert.equal(corpus.manifest.schema, "terrascale.sync.v2/corpus-manifest@1");
    assert.equal(corpus.manifest.protocol, "terrascale.sync.v2");
    assert.equal(corpus.manifest.protocol_version, 2);

    assert.deepEqual(
      corpus.entries.map((entry) => entry.file),
      corpus.listed,
      "the manifest file set and the vendored vector directory must agree exactly",
    );

    const categories = [];
    for (const vector of corpus.vectors) {
      const digest = sha256Of(vector.bytes);
      assert.equal(
        digest,
        vector.entry.sha256,
        `${vector.entry.file} must be byte-identical to the engine corpus entry`,
      );
      assert.equal(vector.bytes.length, vector.entry.bytes, `${vector.entry.file} must match its recorded size`);
      assert.equal(
        vector.doc.schema,
        "terrascale.sync.v2/vector-corpus@2",
        `${vector.entry.file} must declare the native corpus schema`,
      );
      assert.equal(vector.doc.protocol, "terrascale.sync.v2");
      assert.equal(vector.doc.protocol_version, 2);
      assert.equal(vector.doc.category, vector.entry.category);
      categories.push(vector.entry.category);
    }

    assert.deepEqual(
      [...categories].sort(),
      [...syncV2CorpusCategories].sort(),
      "the closed category set is covered exactly once",
    );
  });

  test("the client conformance run passes and agrees with the Rust reference report check-for-check", async () => {
    const reference = JSON.parse(
      await readFile(new URL("engine-reference-report.json", fixtureDirectory), "utf8"),
    );
    assert.equal(reference.schema, "terrascale.sync.v2/conformance-report@1");
    assert.equal(reference.summary.status, "pass", "the vendored reference run must itself pass");
    assert.equal(reference.summary.vectors_total, corpus.vectors.length);

    const report = await runSyncV2Conformance({
      corpus: {
        async read(relativePath) {
          const bytes = new Uint8Array(await readFile(new URL(relativePath, fixtureDirectory)));
          return { bytes, text: new TextDecoder("utf-8", { fatal: true }).decode(bytes) };
        },
        async list(relativeDirectory) {
          return readdir(new URL(relativeDirectory, fixtureDirectory)).then((files) => [...files].sort());
        },
      },
    });

    assert.equal(report.summary.status, "pass");
    assert.equal(report.summary.vectors_total, corpus.vectors.length);
    assert.equal(report.summary.vectors_failed, 0);
    assert.equal(report.summary.checks_failed, 0);
    assert.equal(report.manifest.status, "verified");

    const referenceByFile = new Map(reference.vectors.map((vector) => [vector.file, vector]));
    for (const vector of report.vectors) {
      const referenceVector = referenceByFile.get(vector.file);
      assert.ok(referenceVector !== undefined, `${vector.file} must exist in the reference report`);
      assert.equal(vector.category, referenceVector.category);
      assert.equal(vector.status, "pass", `${vector.file} must pass the client contract`);
      for (const name of contractCheckNames) {
        assert.equal(
          vector.checks[name] ?? 0,
          referenceVector.checks[name] ?? 0,
          `${vector.file} check ${name} must match the Rust reference execution count`,
        );
      }
      // `serve_frame_bytes_exact` is counted one-sidedly: the reference also
      // records a passed check for a serve step the engine answered with
      // "nothing to serve" (no bytes exist for a client to assert), so the
      // client count can only ever be lower or equal — never higher.
      assert.ok(
        (vector.checks.serve_frame_bytes_exact ?? 0) <= (referenceVector.checks.serve_frame_bytes_exact ?? 0),
        `${vector.file} must not assert more served bytes than the reference run recorded`,
      );
    }
    // The reference run also executes the server-side delivery core
    // (journal applies, per-vector manifest entries); the client contract
    // records those differently, so the reference total is at least as large.
    assert.ok(reference.summary.checks_total >= report.summary.checks_total);
  });

  for (const vector of corpus.vectors) {
    test(`${vector.entry.category}: every recorded wire decodes, verifies, and re-encodes byte-exactly; checkpoints round-trip; delivery applies idempotently`, () => {
      const store = new TerraScaleSyncV2Store();
      let appliedBatches = 0;
      let adoptedSnapshots = 0;
      let lastDeliveryCheckpoint;
      const checkpoints = [];

      const deliverFrame = (body, context) => {
        if (body.kind === "delta_batch") {
          const payload = syncV2PayloadBytesOf(body);
          assert.ok(payload.length > 0, `${context}: a delta batch carries payload bytes`);
          const applied = store.applyDeltaBatch(payload, body.codec, body.next);
          if (applied) {
            appliedBatches += 1;
            lastDeliveryCheckpoint = body.next;
          } else {
            // The at-least-once re-serve contract: a batch whose checkpoint
            // does not strictly advance the session state is a bounded no-op
            // (reconnect-replay's byte-identical re-delivery).
            assert.ok(
              checkpointEquals(store.getSnapshot().Checkpoint, body.next),
              `${context}: a refused re-serve must be one the store already absorbed`,
            );
          }
          assert.equal(
            store.applyDeltaBatch(payload, body.codec, body.next),
            false,
            `${context}: re-serving the same batch must be a no-op`,
          );
        } else if (body.kind === "snapshot_answer") {
          store.adoptSnapshot(syncV2PayloadBytesOf(body), body.codec, body.checkpoint);
          adoptedSnapshots += 1;
          lastDeliveryCheckpoint = body.checkpoint;
        }
      };

      const decodeRecorded = (hex, context) => {
        const wire = bytesOfHex(hex);
        const frame = decodeSyncV2Frame(wire);
        assert.ok(
          verifySyncV2Frame(frame),
          `${context}: the decoded frame must verify against its digest`,
        );
        assert.ok(
          bytesEqual(encodeSyncV2Frame(frame), wire),
          `${context}: decode -> encode must be byte-exact`,
        );
        return frame;
      };

      for (const [index, step] of vector.doc.steps.entries()) {
        const context = `${vector.entry.file} step ${index}`;
        if (step.client_frame !== undefined || step.server_frame !== undefined) {
          const entry = step.client_frame ?? step.server_frame;
          const frame = decodeRecorded(entry.wire, context);
          assert.equal(frame.body.kind, entry.frame.kind, `${context}: the decoded kind must match the description`);
          assert.equal(frame.correlation, BigInt(entry.frame.correlation ?? 0));
          assert.equal(frame.causation, BigInt(entry.frame.causation ?? 0));
          assert.equal(frame.idempotency ?? null, entry.frame.idempotency ?? null);
          checkpoints.push(...checkpointsOf(frame.body));
          deliverFrame(frame.body, context);
        } else if (step.serve_delta !== undefined) {
          if (typeof step.serve_delta.expect_wire === "string") {
            const frame = decodeRecorded(step.serve_delta.expect_wire, context);
            checkpoints.push(...checkpointsOf(frame.body));
            deliverFrame(frame.body, context);
          } else {
            // From the acknowledged checkpoint nothing is re-served: the
            // step records no bytes and no frame, and the client state the
            // step leaves behind is exactly the state it found.
            assert.equal(step.serve_delta.expect_wire ?? null, null);
            assert.equal(step.serve_delta.expect_frame ?? null, null);
          }
        } else if (step.serve_snapshot !== undefined) {
          const frame = decodeRecorded(step.serve_snapshot.expect_wire, context);
          checkpoints.push(...checkpointsOf(frame.body));
          deliverFrame(frame.body, context);
        } else if (step.serve_trigger !== undefined) {
          const frame = decodeRecorded(step.serve_trigger.expect_wire, context);
          assert.equal(frame.body.kind, "trigger");
          assert.ok(encodeSyncV2Frame(frame).length <= 256, `${context}: the trigger stays inside its budget`);
        } else if (step.decode_rejects !== undefined) {
          assert.throws(
            () => decodeSyncV2Frame(bytesOfHex(step.decode_rejects.wire)),
            (error) => error.code === step.decode_rejects.error,
            `${context}: the bytes must be rejected with the recorded typed error`,
          );
        } else if (step.budget !== undefined) {
          decodeRecorded(step.budget.wire, context);
        }
      }

      // Checkpoint round-trip through the delivery codec and the base64
      // persistence surface the session resumes from.
      for (const checkpoint of checkpoints) {
        const encoded = syncV2CheckpointToBytes(checkpoint);
        assert.ok(
          checkpointEquals(syncV2CheckpointFromBytes(encoded), checkpoint),
          `${vector.entry.file}: the checkpoint must round-trip its codec bytes`,
        );
        assert.ok(
          checkpointEquals(syncV2CheckpointFromBytes(bytesOfBase64(base64Of(encoded))), checkpoint),
          `${vector.entry.file}: the checkpoint must round-trip its base64 persistence form`,
        );
      }

      const snapshot = store.getSnapshot();
      assert.equal(snapshot.AppliedDeltaBatches, appliedBatches);
      assert.equal(snapshot.AdoptedSnapshots, adoptedSnapshots);
      assert.ok(
        checkpointEquals(
          snapshot.Checkpoint,
          lastDeliveryCheckpoint ?? initialDeliveryCheckpoint,
        ),
        `${vector.entry.file}: the client store must hold exactly the last delivered checkpoint`,
      );
    });

    test(`${vector.entry.category}: the result-view engine applies the journal as digested revisions, then resets and changes authorization generation`, () => {
      const journalBefore = vector.doc.server.journal_before ?? [];
      const applies = vector.doc.steps
        .filter((step) => step.apply !== undefined)
        .map((step) => step.apply);
      const commits = [...journalBefore, ...applies];

      // The bootstrap folds everything the session starts from; a vector
      // with an empty pre-session journal bootstraps from its first commit.
      const bootstrapCommits = journalBefore.length > 0 ? journalBefore : commits.slice(0, 1);
      const deltaCommits = journalBefore.length > 0 ? applies : commits.slice(1);

      const model = new ResultModel();
      for (const commit of bootstrapCommits) {
        model.apply(commit);
      }
      const bootstrapRevision = BigInt(bootstrapCommits.length);

      const store = new SyncV2ResultViewStore();
      store.bootstrap(modelSnapshot(model.items, { revision: bootstrapRevision }));
      assert.equal(store.getRevision(), bootstrapRevision);
      assert.equal(store.getViewGeneration(), 1n);
      assert.equal(store.getAuthorizationGeneration(), 1n);
      const bootstrapCheckpoint = store.getCheckpoint();
      assert.ok(bootstrapCheckpoint !== undefined);
      assert.ok(new SyncV2ViewCheckpoint(bootstrapCheckpoint.toBytes()).equals(bootstrapCheckpoint));
      assert.ok(new SyncV2ViewCheckpoint(bytesOfBase64(bootstrapCheckpoint.toBase64())).equals(bootstrapCheckpoint));

      // Patch application: every journal commit is one atomic revision whose
      // digest comes from the independent model, never from the store.
      let revision = bootstrapRevision;
      const viewCheckpoints = [bootstrapCheckpoint];
      for (const commit of deltaCommits) {
        const operation = model.apply(commit);
        revision += 1n;
        const delta = {
          view_generation: 1n,
          from_revision: revision - 1n,
          to_revision: revision,
          operations: operation === null ? [] : [operation],
          digest: syncV2ResultDigest(model.items),
          checkpoint: new SyncV2ViewCheckpoint(`view-checkpoint-1-${revision}`),
        };
        assert.equal(
          store.applyDelta(delta),
          "applied",
          `${vector.entry.category} revision ${revision} must apply`,
        );
        assert.equal(store.getRevision(), revision);
        assert.deepEqual(
          store.getItems().map((item) => item.identity),
          model.items.map((item) => item.identity),
          `${vector.entry.category} revision ${revision}: the applied result must match the model order`,
        );
        assert.equal(syncV2ResultDigest(store.getItems()), delta.digest);

        // Duplicate redelivery is safe; a reordered future delta is a gap.
        assert.equal(store.applyDelta(delta), "duplicate");
        assert.equal(
          store.applyDelta({ ...delta, from_revision: revision + 5n, to_revision: revision + 6n }),
          "gap",
        );
        // A duplicate revision carrying a different digest is divergence.
        assert.equal(store.applyDelta({ ...delta, digest: "f".repeat(64) }), "reset-required");
        // A fresh revision whose digest disagrees with the operations it
        // carries is divergence too — the applied result, not the claimed
        // digest, decides.
        assert.equal(
          store.applyDelta({
            view_generation: 1n,
            from_revision: revision,
            to_revision: revision + 2n,
            operations: delta.operations,
            digest: "0".repeat(63) + "1",
            checkpoint: new SyncV2ViewCheckpoint(`view-checkpoint-1-forged-${revision}`),
          }),
          "reset-required",
        );
        assert.equal(store.getRevision(), revision, "a refused revision must not advance the view");

        const current = store.getCheckpoint();
        assert.ok(current !== undefined);
        for (const previous of viewCheckpoints) {
          assert.equal(
            current.equals(previous),
            false,
            `${vector.entry.category} revision ${revision}: each revision must issue a distinct checkpoint`,
          );
        }
        viewCheckpoints.push(current);
      }

      // Reset: a regressing snapshot is refused; a fresh authorized snapshot
      // installs; an old-generation delta afterwards requires another reset.
      if (store.getRevision() > 0n) {
        assert.throws(
          () => store.reset(modelSnapshot(model.items, { revision: store.getRevision() - 1n, token: "stale" })),
          /reset-required/u,
          "a same-generation regressing snapshot must be refused",
        );
      }
      assert.throws(
        () => store.reset(modelSnapshot(model.items, { viewGeneration: 0n, token: "regressed" })),
        /reset-required/u,
        "a regressed view generation must be refused",
      );
      const resyncedAt = store.getRevision() + 7n;
      store.reset(modelSnapshot(model.items, { viewGeneration: 2n, revision: resyncedAt, token: "resync" }));
      assert.equal(store.getViewGeneration(), 2n);
      assert.equal(store.getRevision(), resyncedAt);
      assert.equal(
        store.applyDelta({
          view_generation: 1n,
          from_revision: revision,
          to_revision: revision + 1n,
          operations: [],
          digest: syncV2ResultDigest(model.items),
          checkpoint: new SyncV2ViewCheckpoint("view-checkpoint-1-post-resync"),
        }),
        "reset-required",
        "a delta from the pre-reset generation must demand a reset",
      );

      // Authorization-generation change: stale generations and mismatched
      // replacements are refused; a valid transition installs the new
      // generation after applying visibility removals.
      const authorization = store.getAuthorizationGeneration();
      const identities = store.getItems().map((item) => item.identity);
      const kept = identities.slice(1);
      const keptItems = model.items.filter((item) => kept.includes(item.identity));
      const replacement = modelSnapshot(keptItems, {
        viewGeneration: store.getViewGeneration() + 1n,
        authorizationGeneration: authorization + 1n,
        revision: store.getRevision() + 1n,
        token: "authorized",
      });
      assert.throws(
        () => store.transitionAuthorization(authorization, [], replacement),
        /reset-required/u,
        "a stale authorization generation must be refused",
      );
      assert.throws(
        () => store.transitionAuthorization(authorization, [], { ...replacement, authorization_generation: authorization }),
        /reset-required/u,
        "a stale transition must be refused even when its replacement claims the same stale generation",
      );
      assert.throws(
        () => store.transitionAuthorization(authorization + 1n, [], { ...replacement, view_generation: store.getViewGeneration() }),
        /reset-required/u,
        "a replacement from the same view generation must be refused",
      );
      assert.throws(
        () => store.transitionAuthorization(authorization + 1n, [], { ...replacement, authorization_generation: authorization }),
        /reset-required/u,
        "a replacement whose authorization generation disagrees must be refused",
      );
      const removed = identities[0];
      store.transitionAuthorization(
        authorization + 1n,
        removed === undefined ? ["not-a-present-identity"] : [removed, "not-a-present-identity"],
        replacement,
      );
      assert.equal(store.getAuthorizationGeneration(), authorization + 1n);
      assert.equal(store.getViewGeneration(), replacement.view_generation);
      assert.deepEqual(
        store.getItems().map((item) => item.identity),
        kept,
      );
      const finalCheckpoint = store.getCheckpoint();
      assert.ok(finalCheckpoint !== undefined);
      assert.ok(new SyncV2ViewCheckpoint(finalCheckpoint.toBytes()).equals(finalCheckpoint));
      assert.equal(finalCheckpoint.toString(), "[opaque SyncV2ViewCheckpoint]");
    });
  }

  test("view checkpoint tokens stay opaque and bounded", () => {
    const token = new SyncV2ViewCheckpoint("resume-token");
    assert.equal(token.toString(), "[opaque SyncV2ViewCheckpoint]");
    assert.throws(() => new SyncV2ViewCheckpoint(""), /invalid_checkpoint/u);
    assert.throws(() => new SyncV2ViewCheckpoint("x".repeat(4097)), /invalid_checkpoint/u);
    assert.equal(new SyncV2ViewCheckpoint("x".repeat(4096)).toBytes().length, 4096);
  });
});
