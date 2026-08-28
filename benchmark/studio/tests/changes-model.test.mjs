// Unit tests for the DB-04 live changes model: the closed selector-kind
// vocabulary, V1 Changes selector filter parity, the structural
// ConflictRecord content restriction (no document bodies ever), the
// bounded feed list, at-least-once dedup, and drop accounting.
//
// The model is plain TypeScript with no imports, so node:test loads it
// through Node's type stripping.
//
// Run with: node --test tests/changes-model.test.mjs (also wired into
// `vp run unit:test`).

import assert from "node:assert/strict";
import test from "node:test";

import {
  CHANGE_KINDS,
  CONFLICT_METADATA_KEYS,
  FEED_BOUND,
  FILTER_KINDS,
  appendFeedEvent,
  collectionMatches,
  dropNotice,
  feedKindCounts,
  kindMatchesFilter,
  normalizeStreamEvent,
  projectConflict,
  selectorOf,
} from "../src/components/live/changes-model.ts";

function event(overrides = {}) {
  return {
    sequence: 7,
    operationId: "op-007",
    kind: "put",
    collection: "orders",
    document: "d-01",
    conflict: null,
    ...overrides,
  };
}

test("the closed kind vocabulary is exactly the wire forms plus the backbone selector set", () => {
  // put/delete cross the TBEP wire today; document..conflict are the
  // engine backbone's closed selector set (src/streaming/mod.rs
  // ChangeKind, SE-03). Anything else fails closed.
  assert.deepEqual(CHANGE_KINDS, [
    "put",
    "delete",
    "document",
    "index",
    "counter",
    "time-series",
    "operation",
    "database",
    "conflict",
  ]);
});

test("normalizeStreamEvent accepts every kind of the closed vocabulary", () => {
  for (const kind of CHANGE_KINDS) {
    if (kind === "conflict") {
      continue;
    }
    const answer = normalizeStreamEvent(
      event({ kind, collection: kind === "database" ? null : "orders" }),
    );
    assert.equal(answer.ok, true, kind);
    assert.equal(answer.ok && answer.event.kind, kind);
  }
});

test("normalizeStreamEvent fails closed on an unknown kind", () => {
  const answer = normalizeStreamEvent(event({ kind: "attachment" }));
  assert.equal(answer.ok, false);
  assert.match(!answer.ok ? answer.reason : "", /unknown change kind/);
});

test("normalizeStreamEvent fails closed on a non-finite sequence and a missing operation id", () => {
  assert.equal(normalizeStreamEvent(event({ sequence: Number.NaN })).ok, false);
  assert.equal(normalizeStreamEvent(event({ operationId: "" })).ok, false);
  assert.equal(normalizeStreamEvent(null).ok, false);
});

test("the wire put/delete forms select as documents (V1 ForAllDocuments parity)", () => {
  // The TBEP wire carries put/delete document forms; the feed's selector
  // group is the V1 document selector family.
  assert.equal(selectorOf("put"), "document");
  assert.equal(selectorOf("delete"), "document");
  assert.equal(selectorOf("index"), "index");
  assert.equal(selectorOf("counter"), "counter");
  assert.equal(selectorOf("time-series"), "time-series");
  assert.equal(selectorOf("operation"), "operation");
  assert.equal(selectorOf("database"), "database");
  assert.equal(selectorOf("conflict"), "conflict");
});

test("the filter set offers every V1-parity selector kind", () => {
  assert.deepEqual(
    FILTER_KINDS.map((kind) => kind.id),
    ["document", "index", "counter", "time-series", "operation", "database", "conflict"],
  );
});

test("kind filters select exactly their selector group", () => {
  const selected = new Set(["counter", "conflict"]);
  assert.equal(kindMatchesFilter({ kind: "counter" }, selected), true);
  assert.equal(kindMatchesFilter({ kind: "conflict" }, selected), true);
  // The wire document forms ride the document selector only.
  assert.equal(kindMatchesFilter({ kind: "put" }, selected), false);
  const documents = new Set(["document"]);
  assert.equal(kindMatchesFilter({ kind: "put" }, documents), true);
  assert.equal(kindMatchesFilter({ kind: "delete" }, documents), true);
  assert.equal(kindMatchesFilter({ kind: "index" }, documents), false);
  // An empty selection accepts everything (V1 forAll* with no refinement).
  assert.equal(kindMatchesFilter({ kind: "database" }, new Set()), true);
});

test("conflict events project exactly the ConflictRecord metadata keys, never a body", () => {
  // The content restriction is structural: the projection allowlist is
  // the ConflictRecord's field set (src/replication/mod.rs). A payload
  // carrying extra keys — including a document body — surfaces none of
  // them: only the five metadata keys survive.
  const raw = {
    tenant: "tenant-a",
    operations: ["op-1", "op-2"],
    members: ["member-a", "member-b"],
    clocks: ["clock-1", "clock-2"],
    outcome: "winner:member-a",
    body: { "document body": "must never surface" },
    documentValue: { secret: true },
  };
  const projected = projectConflict(raw);
  assert.deepEqual(Object.keys(projected).sort(), CONFLICT_METADATA_KEYS.slice().sort());
  assert.equal(Object.hasOwn(projected, "body"), false);
  assert.equal(Object.hasOwn(projected, "documentValue"), false);
  assert.equal(projected.tenant, "tenant-a");
  assert.deepEqual(projected.operations, ["op-1", "op-2"]);
});

test("a conflict event carries no collection or document identity (fail closed)", () => {
  const conflictPayload = {
    tenant: "tenant-a",
    operations: ["op-9"],
    members: ["member-a"],
    clocks: ["clock-9"],
    outcome: "winner:member-a",
  };
  const good = normalizeStreamEvent(
    event({ kind: "conflict", collection: null, document: null, conflict: conflictPayload }),
  );
  assert.equal(good.ok, true);
  assert.equal(good.ok && good.event.collection, null);
  assert.equal(good.ok && good.event.document, null);

  // A conflict event claiming a collection identity is refused: the
  // backbone's conflict observations carry none (src/streaming/mod.rs
  // ChangeEvent::collection).
  const bad = normalizeStreamEvent(
    event({ kind: "conflict", collection: "orders", document: null, conflict: conflictPayload }),
  );
  assert.equal(bad.ok, false);
  assert.match(!bad.ok ? bad.reason : "", /collection or document identity/);
});

test("a non-conflict event carrying a conflict record fails closed", () => {
  const answer = normalizeStreamEvent(
    event({
      kind: "put",
      conflict: {
        tenant: "tenant-a",
        operations: ["op-1"],
        members: ["member-a"],
        clocks: ["clock-1"],
        outcome: "winner:member-a",
      },
    }),
  );
  assert.equal(answer.ok, false);
  assert.match(!answer.ok ? answer.reason : "", /conflict record/);
});

test("a conflict event without a conflict record fails closed", () => {
  const answer = normalizeStreamEvent(
    event({ kind: "conflict", collection: null, document: null, conflict: null }),
  );
  assert.equal(answer.ok, false);
  assert.match(!answer.ok ? answer.reason : "", /conflict record/);
});

test("a collection text filter matches collection prefixes and excludes conflicts (recorded limit)", () => {
  // SE-03's recorded limit: conflict events carry no collection identity,
  // so a collection-refined filter does not observe them.
  assert.equal(collectionMatches({ collection: "orders" }, "ord"), true);
  assert.equal(collectionMatches({ collection: "orders" }, "users"), false);
  assert.equal(collectionMatches({ collection: null }, ""), true);
  assert.equal(collectionMatches({ collection: null }, "ord"), false);
});

test("the feed list is bounded and drops the oldest", () => {
  let feed = [];
  for (let index = 0; index < FEED_BOUND + 10; index += 1) {
    feed = appendFeedEvent(feed, event({ sequence: index + 1 })).feed;
  }
  assert.equal(feed.length, FEED_BOUND);
  assert.equal(feed[0].sequence, 11);
  assert.equal(feed.at(-1).sequence, FEED_BOUND + 10);
});

test("at-least-once identities dedup by sequence and are counted, never re-surfaced", () => {
  const first = appendFeedEvent([], event({ sequence: 5, operationId: "op-a" }));
  assert.equal(first.duplicate, false);
  const again = appendFeedEvent(first.feed, event({ sequence: 5, operationId: "op-a" }));
  assert.equal(again.duplicate, true);
  assert.equal(again.feed.length, 1);
});

test("feed counters count every selector kind and the wire forms as documents", () => {
  const counts = feedKindCounts([
    event({ sequence: 1, kind: "put" }),
    event({ sequence: 2, kind: "delete" }),
    event({ sequence: 3, kind: "index", collection: null }),
    event({ sequence: 4, kind: "counter" }),
    event({ sequence: 5, kind: "time-series" }),
    event({ sequence: 6, kind: "operation", collection: null }),
    event({ sequence: 7, kind: "database", collection: null }),
    event({
      sequence: 8,
      kind: "conflict",
      collection: null,
      document: null,
      conflict: {
        tenant: "tenant-a",
        operations: ["op-1"],
        members: ["member-a"],
        clocks: ["c1"],
        outcome: "winner:member-a",
      },
    }),
  ]);
  assert.equal(counts.document, 2);
  assert.equal(counts.index, 1);
  assert.equal(counts.counter, 1);
  assert.equal(counts["time-series"], 1);
  assert.equal(counts.operation, 1);
  assert.equal(counts.database, 1);
  assert.equal(counts.conflict, 1);
});

test("the drop notice states the count and the resume boundary, never hides it", () => {
  const notice = dropNotice({ dropped: 3, resumeFrom: 41 });
  assert.match(notice, /3/);
  assert.match(notice, /41/);
});
