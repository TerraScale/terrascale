import assert from "node:assert/strict";
import { describe, test } from "vitest";
import { createCollection } from "@tanstack/db";
import { createTerraBaseClient } from "../local/legacy-client.js";
import { DatabaseSubscriptionView } from "../database-view.js";
import { TerraScaleTanStackCollectionPublisher, TerraScaleTanStackMutationError, terrascaleCollectionOptions } from "./index.js";
/** @import { DatabaseViewSnapshot } from "../database-view.js" */
/** @import { DatabasePoint, DatabaseQueryRow } from "../database-types.js" */
/** @import { TerraScaleTanStackChange, TerraScaleTanStackSyncParams } from "./index.js" */

const nativeTestKey = `tbk1.${"A".repeat(22)}.${"A".repeat(43)}`;
/** @returns {Promise<Response>} */
const unavailableDiscovery = async () => { throw new Error("Discovery transport is disabled in this lifecycle test."); };
/**
 * @param {number} tail
 * @returns {string}
 */
const uuid = tail => `00000000-0000-4000-8000-${tail.toString(16).padStart(12, "0")}`;
const scope = { tenant: uuid(1), database: uuid(2), shard: 9n };
const collection = uuid(3);
const predicate = { op: /** @type {const} */ ("is_missing"), path: [1] };
/** @type {DatabasePoint} */
const point = { version: 1, state: "absent", state_version: null, context: [], siblings: [] };
/**
 * @typedef {{
 *   readonly id: string;
 *   readonly point: DatabasePoint;
 * }} Row
 */
/**
 * @param {number} id
 * @returns {DatabaseQueryRow}
 */
const row = id => ({ document: uuid(id), point });
/**
 * @param {DatabaseQueryRow} value
 * @returns {Row}
 */
const mapRow = value => ({ id: value.document, point: value.point });

/**
 * @param {readonly DatabaseQueryRow[]} rows
 * @param {string} poll_cursor
 * @param {number} [generation]
 * @returns {DatabaseViewSnapshot}
 */
function cut(rows, poll_cursor, generation = 1) {
  return { rows, position: { generation, poll_cursor } };
}

/**
 * @template T
 * @returns {{ promise: Promise<T>; resolve: (value: T) => void; reject: (error: unknown) => void }}
 */
function deferred() {
  /** @type {(value: T) => void} */
  let resolve = () => undefined;
  /** @type {(error: unknown) => void} */
  let reject = () => undefined;
  /** @type {Promise<T>} */
  const promise = new Promise((accept, fail) => { resolve = accept; reject = fail; });
  return { promise, resolve, reject };
}

/**
 * @param {TerraScaleTanStackSyncParams<Row, string>["commit"]} [commit]
 */
function callbacks(commit = () => true) {
  /** @type {Array<TerraScaleTanStackChange<Row, string> | { readonly type: "truncate" }>} */
  const writes = [];
  let ready = 0;
  let commits = 0;
  /** @type {TerraScaleTanStackSyncParams<Row, string>} */
  const params = {
    collection: {}, begin: () => undefined, write: message => writes.push(message),
    commit: signal => { commits += 1; return commit(signal); },
    markReady: () => { ready += 1; }, markError: () => undefined,
    truncate: () => writes.push({ type: "truncate" }),
  };
  return { params, writes, ready: () => ready, commits: () => commits };
}

describe("native TanStack collection publication", () => {
  test("publishes complete snapshots and atomic poll responses through collection callbacks", async () => {
    const state = new DatabaseSubscriptionView(scope, { collection, predicate });
    const output = callbacks();
    const publisher = new TerraScaleTanStackCollectionPublisher(output.params, mapRow, value => value.id);
    const first = state.apply({ version: 1, phase: "snapshot", rows: [row(10), row(20)], poll_cursor: "initial" });
    assert.ok(first);
    await publisher.publish(first);
    assert.deepEqual(output.writes.map(value => value.type), ["truncate", "insert", "insert"]);
    const second = state.apply({ version: 1, phase: "poll", groups: [{ sequence: 5n, effects: [{ document: uuid(10), point: null }, { document: uuid(30), point }] }], poll_cursor: "changed" });
    assert.ok(second);
    await publisher.publish(second);
    assert.deepEqual(output.writes.slice(3).map(value => value.type), ["delete", "insert"]);
    assert.equal(publisher.getPosition()?.poll_cursor, "changed");
    assert.equal(output.ready(), 1);
    publisher.dispose();
  });

  test("commits empty projected groups while preserving the rows", async () => {
    const state = new DatabaseSubscriptionView(scope, { collection, predicate });
    const output = callbacks();
    const publisher = new TerraScaleTanStackCollectionPublisher(output.params, mapRow, value => value.id);
    const initial = state.apply({ version: 1, phase: "snapshot", rows: [row(10)], poll_cursor: "initial" });
    assert.ok(initial);
    await publisher.publish(initial);
    const writes = output.writes.length;
    const advanced = state.apply({ version: 1, phase: "poll", groups: [{ sequence: 18_446_744_073_709_551_615n, effects: [] }], poll_cursor: "advanced" });
    assert.ok(advanced);
    await publisher.publish(advanced);
    assert.equal(output.writes.length, writes);
    assert.equal(output.commits(), 2);
    assert.equal(publisher.getPosition()?.poll_cursor, "advanced");
    assert.equal(publisher.getPosition()?.sequence, 18_446_744_073_709_551_615n);
    publisher.dispose();
  });

  test("advances no cursor or readiness until an async collection commit succeeds", async () => {
    /** @type {ReturnType<typeof deferred<void>>} */
    const commit = deferred();
    const output = callbacks(() => commit.promise);
    const publisher = new TerraScaleTanStackCollectionPublisher(output.params, mapRow, value => value.id);
    const publication = publisher.publish(cut([row(10)], "pending"));
    await Promise.resolve();
    assert.equal(publisher.getPosition(), undefined);
    assert.equal(output.ready(), 0);
    commit.resolve();
    await publication;
    assert.equal(publisher.getPosition()?.poll_cursor, "pending");
    assert.equal(output.ready(), 1);
    publisher.dispose();
  });

  test("captures concurrent cuts before the prior async collection commit completes", async () => {
    /** @type {ReturnType<typeof deferred<void>>} */
    const commit = deferred();
    let count = 0;
    const output = callbacks(() => ++count === 1 ? commit.promise : true);
    /** @type {{ id: string; point: DatabasePoint }} */
    const mutable = { id: uuid(20), point };
    const publisher = new TerraScaleTanStackCollectionPublisher(output.params, value => value.document === uuid(20) ? mutable : mapRow(value), value => value.id);
    const first = publisher.publish(cut([row(10)], "first"));
    const second = publisher.publish(cut([row(20)], "second"));
    mutable.id = uuid(99);
    await Promise.resolve();
    assert.equal(output.commits(), 1);
    commit.resolve();
    await Promise.all([first, second]);
    const inserts = output.writes.filter(value => value.type === "insert");
    assert.deepEqual(inserts.flatMap(value => "value" in value ? [value.value.id] : []), [uuid(10), uuid(20)]);
    assert.equal(publisher.getPosition()?.poll_cursor, "second");
    publisher.dispose();
  });

  test("a failed commit keeps the prior cut and forces a full subsequent publication", async () => {
    let count = 0;
    const output = callbacks(() => ++count === 2 ? Promise.reject(new Error("collection rejected")) : true);
    const publisher = new TerraScaleTanStackCollectionPublisher(output.params, mapRow, value => value.id);
    await publisher.publish(cut([row(10)], "first"));
    await assert.rejects(publisher.publish(cut([row(20)], "failed")), /collection rejected/u);
    assert.equal(publisher.getPosition()?.poll_cursor, "first");
    const before = output.writes.length;
    await publisher.publish(cut([row(30)], "fresh"));
    assert.deepEqual(output.writes.slice(before).map(value => value.type), ["truncate", "insert"]);
    assert.equal(publisher.getPosition()?.poll_cursor, "fresh");
    publisher.dispose();
  });

  test("cancellation and reset fence late commits from advancing position", async () => {
    for (const action of /** @type {const} */ (["cancel", "reset"])) {
      /** @type {ReturnType<typeof deferred<void>>} */
      const commit = deferred();
      const output = callbacks(() => commit.promise);
      const publisher = new TerraScaleTanStackCollectionPublisher(output.params, mapRow, value => value.id);
      const publication = publisher.publish(cut([row(10)], "late"));
      const rejected = assert.rejects(publication, /abort|reset/iu);
      await Promise.resolve();
      if (action === "cancel") publisher.dispose(); else publisher.reset();
      commit.resolve();
      await rejected;
      assert.equal(publisher.getPosition(), undefined);
      assert.equal(output.ready(), 0);
      publisher.dispose();
    }
  });

  test("consumer changes cannot corrupt private publication values", async () => {
    const output = callbacks();
    const publisher = new TerraScaleTanStackCollectionPublisher(output.params, mapRow, value => value.id);
    await publisher.publish(cut([row(10)], "first"));
    const inserted = output.writes.find(value => value.type === "insert");
    assert.equal(inserted?.type, "insert");
    if (inserted?.type !== "insert") throw new Error("expected insert");
    (/** @type {{ id: string }} */ (inserted.value)).id = uuid(99);
    const before = output.writes.length;
    await publisher.publish(cut([row(10)], "same"));
    assert.equal(output.writes.length, before);
    assert.equal(publisher.getPosition()?.poll_cursor, "same");
    publisher.dispose();
  });

  test("preserves full conflicts and causal context in explicitly mapped rows", async () => {
    /** @type {DatabasePoint} */
    const conflict = {
      version: 1, state: "conflict", state_version: null,
      context: [{ replica: uuid(4), incarnation: "11".repeat(16), sequence: 18_446_744_073_709_551_615n }, { replica: uuid(5), incarnation: "22".repeat(16), sequence: 1n }],
      siblings: [
        { schema_generation: 1n, schema_digest: "33".repeat(32), event: { database: scope.database, shard: scope.shard, replica: uuid(4), incarnation: "11".repeat(16), sequence: 18_446_744_073_709_551_615n }, kind: "live", fields: [{ id: 1, value: { type: "utf8", value: "one" } }] },
        { schema_generation: 1n, schema_digest: "33".repeat(32), event: { database: scope.database, shard: scope.shard, replica: uuid(5), incarnation: "22".repeat(16), sequence: 1n }, kind: "tombstone" },
      ],
    };
    const output = callbacks();
    const publisher = new TerraScaleTanStackCollectionPublisher(output.params, mapRow, value => value.id);
    await publisher.publish(cut([{ document: uuid(10), point: conflict }], "conflict"));
    const inserted = output.writes.find(value => value.type === "insert");
    if (inserted?.type !== "insert") throw new Error("expected insert");
    assert.deepEqual(inserted.value.point, conflict);
    assert.equal(inserted.value.point.context[0]?.sequence, 18_446_744_073_709_551_615n);
    publisher.dispose();
  });

  test("rejects colliding application keys before collection writes", async () => {
    const output = callbacks();
    const publisher = new TerraScaleTanStackCollectionPublisher(output.params, mapRow, () => "collision");
    await assert.rejects(publisher.publish(cut([row(10), row(20)], "bad")), /distinct/u);
    assert.equal(output.writes.length, 0);
    assert.equal(publisher.getPosition(), undefined);
    publisher.dispose();
  });
});

describe("native TanStack adapter admission", () => {
  test("actual TanStack cleanup permits a fresh documented collection restart", async () => {
    const client = createTerraBaseClient({ ...scope, apiKey: nativeTestKey, fetch: unavailableDiscovery });
    const options = terrascaleCollectionOptions({ client, collection, predicate, mapRow, getKey: value => value.id });
    const value = createCollection(options);
    try {
      await assert.rejects(value.preload(), /transport_unavailable/u);
      await value.cleanup();
      assert.equal(value.status, "cleaned-up");
      assert.doesNotThrow(() => value.startSyncImmediate());
      await assert.rejects(value.preload(), /transport_unavailable/u);
      assert.equal(value.status, "error", "missing discovery must still fail closed after restart");
      assert.equal(options.adapter.getCheckpoint(), undefined);
    } finally {
      await value.cleanup();
      options.adapter.dispose();
    }
    assert.throws(() => value.startSyncImmediate(), /disposed/u);
  });

  test("requires a native snapshot before mutation and fails closed on discovery transport failure", async () => {
    /** @type {unknown[]} */
    const errors = [];
    let prepared = 0;
    const client = createTerraBaseClient({ ...scope, apiKey: nativeTestKey, fetch: unavailableDiscovery });
    const options = terrascaleCollectionOptions({ client, collection, predicate, mapRow, getKey: value => value.id, prepareMutation: async () => { prepared += 1; throw new Error("must not prepare an offline mutation"); } });
    const output = callbacks();
    /** @type {ReturnType<typeof deferred<void>>} */
    const terminal = deferred();
    const stop = options.sync.sync({ ...output.params, markError: error => { errors.push(error); terminal.resolve(); } });
    await terminal.promise;
    assert.equal(errors.length, 1);
    assert.match(String(errors[0]), /transport_unavailable/u);
    await assert.rejects(options.onInsert({ transaction: { id: "never-queued", mutations: [{ key: uuid(10), type: "insert", modified: mapRow(row(10)) }] } }), /current native subscription/u);
    const frameworkTransaction = {
      id: "real-shape-not-cloneable", mutationFn: () => undefined,
      mutations: [{ key: uuid(10), type: /** @type {const} */ ("insert"), modified: mapRow(row(10)), original: {}, collection: { runtime: () => undefined }, createdAt: new Date() }],
    };
    await assert.rejects(options.onInsert({ transaction: frameworkTransaction }), /current native subscription/u);
    assert.equal(prepared, 0);
    assert.equal(options.adapter.getCheckpoint(), undefined);
    stop();
    await assert.rejects(options.onInsert({ transaction: { id: "cleaned-up", mutations: [{ key: uuid(10), type: "insert", modified: mapRow(row(10)) }] } }), /current native subscription/u);
    options.adapter.dispose();
    await assert.rejects(options.onInsert({ transaction: { id: "disposed", mutations: [{ key: uuid(10), type: "insert", modified: mapRow(row(10)) }] } }), /disposed/u);
  });

  test("keeps typed uncertain outcomes unresolved", () => {
    const detail = { code: "transport_unavailable", mutation_outcome: /** @type {const} */ ("uncertain"), transaction: { transaction_id: { expires_at_unix_ms: 100n, nonce: "11".repeat(16) }, authority: { mode: /** @type {const} */ ("leaderless") }, effect_digest: "22".repeat(32) } };
    const error = new TerraScaleTanStackMutationError(detail);
    assert.equal(error.detail.mutation_outcome, "uncertain");
    assert.equal(error.cause, detail);
    assert.match(error.message, /uncertain/u);
  });
});
