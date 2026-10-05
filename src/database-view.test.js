import assert from "node:assert/strict";
import { randomBytes, randomUUID } from "node:crypto";
import { describe, test } from "vitest";
import { createTerraBaseClient } from "./local/legacy-client.js";
import { createDatabaseQuery, DatabaseSubscriptionView } from "./database-view.js";
/** @import { DatabasePoint, DatabaseQueryPredicate, DatabaseQueryRow, DatabaseResult, DatabaseValue } from "./database-types.js" */

const nativeTestKey = `tbk1.${"A".repeat(22)}.${"A".repeat(43)}`;
/** @returns {Promise<Response>} */
const unavailableDiscovery = async () => { throw new Error("Discovery transport is disabled in this lifecycle test."); };
/** @type {(tail: number) => string} */
const uuid = tail => `00000000-0000-4000-8000-${tail.toString(16).padStart(12, "0")}`;
const scope = { tenant: uuid(1), database: uuid(2), shard: 9n };
const collection = uuid(3);
/** @type {DatabaseQueryPredicate} */
const predicate = { op: "equal", path: [1], value: { type: "utf8", value: "match" } };
const event = (sequence = 1n, replica = uuid(4)) => ({ database: scope.database, shard: scope.shard, replica, incarnation: "11".repeat(16), sequence });
/** @type {(value?: DatabaseValue) => DatabasePoint} */
const point = (value = { type: "utf8", value: "match" }) => ({
  version: 1, state: "value", state_version: "22".repeat(32),
  context: [{ replica: uuid(4), incarnation: "11".repeat(16), sequence: 1n }],
  siblings: [{ schema_generation: 1n, schema_digest: "33".repeat(32), event: event(), kind: "live", fields: [{ id: 1, value }] }],
});
/** @type {(id: number, value?: DatabaseValue) => DatabaseQueryRow} */
const row = (id, value) => ({ document: uuid(id), point: point(value) });
/** @type {(maximumRows?: number) => DatabaseSubscriptionView} */
const view = (maximumRows = 10_000) => new DatabaseSubscriptionView(scope, { collection, predicate, limit: 2, maximumRows });

/**
 * @template T
 * @param {T | undefined} value
 * @returns {T}
 */
function defined(value) {
  assert.ok(value !== undefined);
  return value;
}

describe("native database view reducer", () => {
  test("publishes only a terminal complete snapshot cut", () => {
    const state = view();
    assert.equal(state.apply({ version: 1, phase: "snapshot", rows: [row(10)], next_snapshot_cursor: "snapshot-page-2" }), undefined);
    assert.equal(state.getSnapshot(), undefined);
    assert.deepEqual(state.getRequest(), { phase: "snapshot", predicate, limit: 2, snapshot_cursor: "snapshot-page-2" });
    const complete = defined(state.apply({ version: 1, phase: "snapshot", rows: [row(20)], poll_cursor: "poll-cut-1" }));
    assert.deepEqual(complete.rows.map(value => value.document), [uuid(10), uuid(20)]);
    assert.equal(complete.position.poll_cursor, "poll-cut-1");
    assert.deepEqual(state.getRequest(), { phase: "poll", predicate, poll_cursor: "poll-cut-1" });
  });

  test("applies complete ordered groups without changing previously published rows", () => {
    const state = view();
    const first = defined(state.apply({ version: 1, phase: "snapshot", rows: [row(10), row(20)], poll_cursor: "cut-1" }));
    const next = defined(state.apply({ version: 1, phase: "poll", poll_cursor: "cut-3", groups: [
      { sequence: 2n, effects: [{ document: uuid(10), point: point({ type: "i64", value: -9_223_372_036_854_775_808n }) }, { document: uuid(20), point: null }] },
      { sequence: 3n, effects: [{ document: uuid(30), point: point() }] },
    ] }));
    assert.deepEqual(first.rows.map(value => value.document), [uuid(10), uuid(20)]);
    assert.deepEqual(next.rows.map(value => value.document), [uuid(10), uuid(30)]);
    assert.equal(next.position.sequence, 3n);
    assert.equal(next.position.poll_cursor, "cut-3");
    const sibling = defined(defined(next.rows[0]).point.siblings[0]);
    assert.equal(sibling.kind, "live");
    if (sibling.kind === "live") assert.deepEqual(defined(sibling.fields[0]).value, { type: "i64", value: -9_223_372_036_854_775_808n });
  });

  test("accepts empty projected groups and advances max-u64 position", () => {
    const state = view();
    const first = defined(state.apply({ version: 1, phase: "snapshot", rows: [row(10)], poll_cursor: "old" }));
    const next = defined(state.apply({ version: 1, phase: "poll", groups: [{ sequence: 18_446_744_073_709_551_615n, effects: [] }], poll_cursor: "new" }));
    assert.deepEqual(next.rows, first.rows);
    assert.equal(next.rows, first.rows, "an empty projection reuses its immutable rows");
    assert.equal(next.position.sequence, 18_446_744_073_709_551_615n);
    assert.equal(next.position.poll_cursor, "new");
    const timeout = defined(state.apply({ version: 1, phase: "poll", groups: [], poll_cursor: "newer" }));
    assert.equal(timeout.position.sequence, next.position.sequence);
    assert.equal(timeout.position.poll_cursor, "newer");
  });

  test("rejects invalid later groups without partially committing earlier effects", () => {
    const state = view();
    const first = defined(state.apply({ version: 1, phase: "snapshot", rows: [row(10)], poll_cursor: "old" }));
    assert.throws(() => state.apply({ version: 1, phase: "poll", poll_cursor: "invalid", groups: [
      { sequence: 2n, effects: [{ document: uuid(10), point: null }] },
      { sequence: 1n, effects: [] },
    ] }), /group_order_invalid/u);
    assert.equal(state.getSnapshot(), first);
    assert.equal(state.getRequest().phase, "poll");
    assert.equal(defined(state.getSnapshot()).position.poll_cursor, "old");
    assert.throws(() => state.apply({ version: 1, phase: "poll", poll_cursor: "invalid", groups: [{ sequence: 0n, effects: [] }] }), /group_order_invalid/u);
  });

  test("preserves conflicts, siblings, causal context, absence, deletion and explicit null", () => {
    /** @type {DatabasePoint} */
    const conflict = {
      ...point(), state: "conflict", state_version: null,
      context: [...point().context, { replica: uuid(5), incarnation: "44".repeat(16), sequence: 7n }],
      siblings: [...point().siblings, { schema_generation: 1n, schema_digest: "33".repeat(32), event: { ...event(7n, uuid(5)), incarnation: "44".repeat(16) }, kind: "tombstone" }],
    };
    const state = view();
    const cut = defined(state.apply({ version: 1, phase: "snapshot", rows: [{ document: uuid(10), point: conflict }, row(20, { type: "map", fields: [{ id: 8, value: { type: "null" } }] })], poll_cursor: "conflicts" }));
    assert.deepEqual(defined(cut.rows[0]).point, conflict);
    assert.equal(defined(cut.rows[0]).point.siblings.length, 2);
    assert.equal(defined(defined(cut.rows[0]).point.context[1]).sequence, 7n);
    /** @type {DatabasePoint} */
    const absent = { version: 1, state: "absent", state_version: null, context: [], siblings: [] };
    /** @type {DatabasePoint} */
    const deleted = { ...point(), state: "deleted", siblings: [{ schema_generation: 1n, schema_digest: "33".repeat(32), event: event(), kind: "tombstone" }] };
    const changed = defined(state.apply({ version: 1, phase: "poll", groups: [{ sequence: 8n, effects: [{ document: uuid(10), point: absent }, { document: uuid(20), point: deleted }] }], poll_cursor: "states" }));
    assert.deepEqual(changed.rows.map(value => value.point.state), ["absent", "deleted"]);
  });

  test("keeps supplied and exposed bytes separate from the immutable cut", () => {
    const bytes = new Uint8Array([1, 2, 3]);
    const state = view();
    const cut = defined(state.apply({ version: 1, phase: "snapshot", rows: [row(10, { type: "array", items: [{ type: "bytes", value: bytes }] })], poll_cursor: "bytes" }));
    bytes[0] = 9;
    const sibling = defined(defined(cut.rows[0]).point.siblings[0]);
    assert.equal(sibling.kind, "live");
    if (sibling.kind !== "live") throw new Error("expected live sibling");
    const value = defined(sibling.fields[0]).value;
    if (value.type !== "array" || value.items[0]?.type !== "bytes") throw new Error("expected byte array");
    const exposed = value.items[0].value;
    assert.deepEqual([...exposed], [1, 2, 3]);
    exposed[1] = 9;
    assert.deepEqual([...value.items[0].value], [1, 2, 3]);
    assert.equal(Object.isFrozen(cut.rows), true);
    assert.equal(Object.isFrozen(sibling.fields), true);
  });

  test("captures scope and predicate while separating subscription bindings", () => {
    const bytes = new Uint8Array([1]);
    const path = [1];
    const mutableScope = { ...scope };
    const state = new DatabaseSubscriptionView(mutableScope, { collection, predicate: { op: "equal", path, value: { type: "bytes", value: bytes } } });
    mutableScope.database = uuid(99);
    bytes[0] = 9;
    path[0] = 9;
    const captured = state.binding.predicate;
    assert.equal(state.binding.scope.database, scope.database);
    assert.deepEqual(captured.path, [1]);
    if (captured.op === "equal" && captured.value.type === "bytes") assert.deepEqual([...captured.value.value], [1]);
    assert.throws(() => state.apply({ version: 1, phase: "snapshot", rows: [{ document: uuid(10), point: { ...point(), siblings: point().siblings.map(sibling => ({ ...sibling, event: { ...sibling.event, database: uuid(99) } })) } }], poll_cursor: "wrong-scope" }), /scope_mismatch/u);
    const client = createTerraBaseClient({ ...scope, apiKey: nativeTestKey, fetch: unavailableDiscovery });
    const first = createDatabaseQuery(client, { collection, predicate });
    const otherCollection = createDatabaseQuery(client, { collection: uuid(99), predicate });
    assert.notDeepEqual(first.key, otherCollection.key);
  });

  test("restarts without mixing earlier pages or poll state", () => {
    const state = view();
    state.apply({ version: 1, phase: "snapshot", rows: [row(10)], next_snapshot_cursor: "partial" });
    state.restart();
    assert.deepEqual(state.getRequest(), { phase: "snapshot", predicate, limit: 2 });
    const cut = defined(state.apply({ version: 1, phase: "snapshot", rows: [row(20)], poll_cursor: "fresh" }));
    assert.deepEqual(cut.rows.map(value => value.document), [uuid(20)]);
    state.restart();
    const fresh = defined(state.apply({ version: 1, phase: "snapshot", rows: [], poll_cursor: "fresh-2" }));
    assert.equal(fresh.position.generation, cut.position.generation + 1);
    assert.equal(fresh.position.sequence, undefined);
  });

  test("rejects phase mixing, cursor cycles, duplicate rows and memory overflow", () => {
    const state = view(1);
    assert.throws(() => state.apply({ version: 1, phase: "poll", groups: [], poll_cursor: "poll" }), /phase_mismatch/u);
    state.apply({ version: 1, phase: "snapshot", rows: [row(10)], next_snapshot_cursor: "page" });
    assert.throws(() => state.apply({ version: 1, phase: "snapshot", rows: [], next_snapshot_cursor: "page" }), /snapshot_cursor_cycle/u);
    assert.throws(() => state.apply({ version: 1, phase: "snapshot", rows: [row(10)], poll_cursor: "bad" }), /snapshot_order_invalid/u);
    assert.throws(() => state.apply({ version: 1, phase: "snapshot", rows: [row(20)], poll_cursor: "large" }), /view_limit_exceeded/u);
    assert.equal(state.getSnapshot(), undefined);
  });

  test("fails closed when discovery transport fails and fences pre-cancelled delivery", async () => {
    const client = createTerraBaseClient({ ...scope, apiKey: nativeTestKey, fetch: unavailableDiscovery });
    const source = createDatabaseQuery(client, { collection, predicate });
    /** @type {unknown[]} */
    const errors = [];
    /** @type {() => void} */
    let finish = () => {};
    /** @type {Promise<void>} */
    const terminal = new Promise(resolve => { finish = resolve; });
    const stop = source.subscribe({ next: () => assert.fail("unavailable discovery cannot publish data"), reset: () => assert.fail("no reset was admitted"), error: error => { errors.push(error); finish(); } }, { signal: new AbortController().signal });
    await terminal;
    assert.equal(errors.length, 1);
    assert.match(String(errors[0]), /transport_unavailable/u);
    stop();
    const abort = new AbortController();
    abort.abort();
    const cancelled = source.subscribe({ next: () => assert.fail("cancelled query published"), reset: () => assert.fail("cancelled query reset"), error: () => assert.fail("cancelled query emitted an error") }, { signal: abort.signal });
    await Promise.resolve();
    cancelled();
  });
});

const liveNames = ["TERRABASE_ENDPOINT", "TERRABASE_TENANT", "TERRABASE_DATABASE", "TERRABASE_SHARD", "TERRABASE_COLLECTION", "TERRABASE_API_KEY", "TERRABASE_SCHEMA_GENERATION", "TERRABASE_SCHEMA_DIGEST", "TERRABASE_TEST_FIELD_ID", "TERRABASE_TEST_FIELD_TYPE"];
const liveConfigured = liveNames.every(name => process.env[name] !== undefined);

test.skipIf(!liveConfigured)("native view observes a real admitted producer write and removal", async () => {
  const env = process.env;
  const client = createTerraBaseClient({ tenant: defined(env.TERRABASE_TENANT), database: defined(env.TERRABASE_DATABASE), shard: BigInt(defined(env.TERRABASE_SHARD)), endpoint: defined(env.TERRABASE_ENDPOINT), apiKey: defined(env.TERRABASE_API_KEY) });
  const liveCollection = defined(env.TERRABASE_COLLECTION);
  const document = randomUUID();
  const field = Number(env.TERRABASE_TEST_FIELD_ID);
  /** @type {DatabaseValue} */
  const value = env.TERRABASE_TEST_FIELD_TYPE === "i64" ? { type: "i64", value: BigInt(Date.now()) } : { type: "utf8", value: `native-view-${document}` };
  if (value.type !== "i64" && value.type !== "utf8") throw new Error("invalid qualification value");
  /**
   * @template T
   * @param {DatabaseResult<T>} result
   * @returns {T}
   */
  const successful = result => { if (!result.ok) assert.fail(result.error.code); return result.value; };
  /**
   * @param {DatabasePoint | undefined} current
   * @param {boolean} tombstone
   * @returns {Promise<void>}
   */
  const commit = async (current, tombstone) => {
    const prepared = await client.prepareCommit({ transaction_id: { expires_at_unix_ms: BigInt(Date.now() + 600_000), nonce: randomBytes(16).toString("hex") }, authority: { mode: "leaderless" }, writes: [{ collection: liveCollection, document, schema_generation: BigInt(defined(env.TERRABASE_SCHEMA_GENERATION)), schema_digest: defined(env.TERRABASE_SCHEMA_DIGEST), condition: current === undefined ? { kind: "absent" } : { kind: "version", version: /** @type {string} */ (current.state_version) }, observed: current?.context ?? [], value: tombstone ? { kind: "tombstone" } : { kind: "live", fields: [{ id: field, value }] } }] });
    successful(await client.commit(prepared));
  };
  const abort = new AbortController();
  /** @type {(() => void) | undefined} */
  let stop;
  /** @type {(point: DatabasePoint) => void} */
  let initialResolve = () => {};
  /** @type {() => void} */
  let removedResolve = () => {};
  /** @type {(error: unknown) => void} */
  let failureReject = () => {};
  /** @type {Promise<DatabasePoint>} */
  const initial = new Promise(resolve => { initialResolve = resolve; });
  /** @type {Promise<void>} */
  const removed = new Promise(resolve => { removedResolve = resolve; });
  /** @type {Promise<never>} */
  const failure = new Promise((_resolve, reject) => { failureReject = reject; });
  void failure.catch(() => undefined);
  let seen = false;
  const timer = setTimeout(() => failureReject(new Error("real native view deadline exceeded")), 20_000);
  try {
    await commit(undefined, false);
    stop = createDatabaseQuery(client, { collection: liveCollection, predicate: { op: "equal", path: [field], value }, limit: 1, pollIntervalMs: 25 }).subscribe({
      next: rows => {
        const found = rows.find(candidate => candidate.document === document);
        if (found !== undefined && !seen) { seen = true; initialResolve(found.point); }
        if (found === undefined && seen) removedResolve();
      }, error: failureReject, reset: () => undefined,
    }, { signal: abort.signal });
    const current = await Promise.race([initial, failure]);
    assert.equal(current.state, "value");
    await commit(current, true);
    await Promise.race([removed, failure]);
  } finally {
    clearTimeout(timer);
    abort.abort();
    stop?.();
    const final = successful(await client.read(liveCollection, document));
    if (final.state === "value") await commit(final, true);
    assert.ok(["deleted", "absent"].includes(successful(await client.read(liveCollection, document)).state));
  }
});
