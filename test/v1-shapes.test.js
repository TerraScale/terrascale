// TerraBase v1 shapes through a real HTTP boundary (long-poll, SSE and the
// edge) against the in-test contract fixture, a stand-in until a packaged
// tb-server serves v1.
import { afterAll, afterEach, beforeAll, describe, expect, test } from "vitest";
import { createTerraBaseV1Client, documentIdFor, v1Filter } from "../src/index.js";
import { startTerraBaseV1Fixture } from "./support/terrabase-v1-fixture.js";

/** @typedef {import("../src/index.js").V1ShapeEvent} V1ShapeEvent */

/** @type {Awaited<ReturnType<typeof startTerraBaseV1Fixture>>} */
let fixture;
/** @type {import("../src/index.js").TerraBaseV1Client} */
let client;
/** @type {import("../src/index.js").V1ShapeSubscription[]} */
const open = [];

beforeAll(async () => {
  fixture = await startTerraBaseV1Fixture({ snapshotPageSize: 2, liveTimeoutMs: 300, sseHeartbeatMs: 100 });
  client = createTerraBaseV1Client({ baseUrl: fixture.baseUrl, token: fixture.token, maxRetries: 5 });
});
afterEach(() => {
  for (const subscription of open.splice(0)) subscription.close();
});
afterAll(async () => {
  await fixture.close();
});

/**
 * @template T
 * @param {Promise<T>} promise
 * @param {number} [ms]
 * @returns {Promise<T>}
 */
function within(promise, ms = 5000) {
  return Promise.race([
    promise,
    new Promise((_resolve, reject) => setTimeout(() => reject(new Error("timed out")), ms)),
  ]);
}

/**
 * @param {AsyncIterator<V1ShapeEvent>} iterator
 * @param {(event: V1ShapeEvent) => boolean} predicate
 * @param {V1ShapeEvent[]} [seen]
 * @returns {Promise<V1ShapeEvent>}
 */
async function until(iterator, predicate, seen = []) {
  while (true) {
    const result = await within(iterator.next());
    if (result.done === true) throw new Error("subscription ended");
    seen.push(result.value);
    if (predicate(result.value)) return result.value;
  }
}

/**
 * @param {string} database
 * @param {string} user
 * @param {number} count
 */
async function seed(database, user, count) {
  const notifications = client.database(database).collection("notifications");
  for (let index = 0; index < count; index++)
    await notifications.put(await documentIdFor("notifications", `${user}-${index}`), { user_id: user, title: `n${index}`, read: false, body: "x" });
  await notifications.put(await documentIdFor("notifications", "other"), { user_id: "someone-else", title: "skip", read: false });
  return notifications;
}

/**
 * @param {V1ShapeEvent[]} events
 */
const upserted = (events) => events.flatMap((event) => event.changes.flatMap((change) => change.ops.filter((op) => op.op === "upsert").map((op) => op.id)));

describe("TerraBase v1 shapes", () => {
  test("long-poll follows 307, snapshot pages, the log and live mode with cursor echo", async () => {
    const notifications = await seed("poll", "u1", 3);
    const request = { collection: "notifications", filter: v1Filter.eq("/user_id", "u1"), project: ["/title", "/read"] };
    const subscription = await client.database("poll").subscribe(request);
    open.push(subscription);
    expect(subscription.info.shapeId).toMatch(/^[0-9a-f]{32}$/u);
    const iterator = subscription[Symbol.asyncIterator]();

    /** @type {V1ShapeEvent[]} */
    const snapshot = [];
    await until(iterator, (event) => event.upToDate, snapshot);
    expect(snapshot.every((event) => event.snapshot && event.type === "changes")).toBe(true);
    expect(snapshot.map((event) => event.changes[0]?.ops.length)).toEqual([2, 1]);
    expect(new Set(upserted(snapshot)).size).toBe(3);
    const first = snapshot[0]?.changes[0]?.ops[0];
    expect(first?.op === "upsert" && first.state === "value" ? first.document : undefined).toEqual({ title: expect.any(String), read: false });

    const shapePath = `/v1/shapes/${subscription.info.shapeId}`;
    const reads = () => fixture.requests.filter((entry) => entry.path === shapePath);
    expect(reads()[0]?.query).toEqual({ offset: "-1" });
    expect(reads()[1]?.query).toMatchObject({ handle: subscription.info.handle, offset: expect.stringMatching(/^snap\.\d+\.0$/u) });
    expect(reads()[0]?.headers.authorization).toBe(`Bearer ${fixture.token}`);

    // A matching write arrives in live mode; waitFor resolves once handled.
    const write = await notifications.put(await documentIdFor("notifications", "u1-new"), { user_id: "u1", title: "fresh", read: false });
    const waited = subscription.waitFor(write.offset);
    const live = await until(iterator, (event) => event.changes.length > 0);
    expect(live).toMatchObject({ type: "changes", snapshot: false, upToDate: true });
    expect(live.changes[0]?.ops[0]).toMatchObject({ op: "upsert", id: write.id, document: { title: "fresh", read: false } });
    const pending = iterator.next();
    await within(waited);
    expect(BigInt(subscription.position)).toBeGreaterThanOrEqual(BigInt(write.offset));

    // Leaving the shape and deletion both produce delete ops.
    const moved = await documentIdFor("notifications", "u1-0");
    await notifications.patch(moved, { merge: { user_id: "u2" } });
    const leave = await within(pending);
    if (leave.done === true) throw new Error("ended");
    const leaving = leave.value.changes.length > 0 ? leave.value : await until(iterator, (event) => event.changes.length > 0);
    expect(leaving.changes[0]?.ops).toEqual([{ op: "delete", id: moved }]);
    const removed = await documentIdFor("notifications", "u1-1");
    await notifications.delete(removed);
    const deletion = await until(iterator, (event) => event.changes.length > 0);
    expect(deletion.changes[0]?.ops).toEqual([{ op: "delete", id: removed }]);

    // Live requests use live=true and echo the previous TB-Shape-Cursor.
    const liveReads = reads().filter((entry) => entry.query.live === "true");
    expect(liveReads.length).toBeGreaterThanOrEqual(2);
    expect(liveReads.slice(1).every((entry) => /^\d+$/u.test(entry.query.cursor ?? ""))).toBe(true);
    expect(Object.keys(liveReads[1]?.query ?? {})).toEqual(["handle", "offset", "live", "cursor"]);
  });

  test("must_refetch yields a reset and restarts from the snapshot without a handle", async () => {
    await seed("refetch", "u1", 2);
    const subscription = await client.database("refetch").subscribe({ collection: "notifications", filter: v1Filter.eq("/user_id", "u1") });
    open.push(subscription);
    const iterator = subscription[Symbol.asyncIterator]();
    await until(iterator, (event) => event.upToDate);
    const before = fixture.requests.length;
    fixture.rotateShapeHandle(subscription.info.shapeId);
    const reset = await until(iterator, (event) => event.type === "reset");
    expect(reset.handle).not.toBe(subscription.info.handle);
    /** @type {V1ShapeEvent[]} */
    const again = [];
    await until(iterator, (event) => event.upToDate, again);
    expect(new Set(upserted(again)).size).toBe(2);
    const restart = fixture.requests.slice(before).find((entry) => entry.query.offset === "-1");
    expect(restart?.query).toEqual({ offset: "-1" });
  });

  test("SSE streams the snapshot, live changes, reconnects with Last-Event-ID and handles must-refetch", async () => {
    const notifications = await seed("sse", "u1", 3);
    const subscription = await client.database("sse").subscribe(
      { collection: "notifications", filter: v1Filter.eq("/user_id", "u1") }, { mode: "sse" });
    open.push(subscription);
    const iterator = subscription[Symbol.asyncIterator]();
    /** @type {V1ShapeEvent[]} */
    const snapshot = [];
    await until(iterator, (event) => event.upToDate, snapshot);
    expect(new Set(upserted(snapshot)).size).toBe(3);
    const ssePath = `/v1/shapes/${subscription.info.shapeId}/sse`;
    const streams = () => fixture.requests.filter((entry) => entry.path === ssePath);
    expect(streams()[0]?.query).toEqual({ offset: "-1" });
    expect(streams()[0]?.headers.accept).toBe("text/event-stream");

    const write = await notifications.put(await documentIdFor("notifications", "u1-sse"), { user_id: "u1", title: "live", read: false });
    const live = await until(iterator, (event) => event.changes.length > 0);
    expect(live.changes[0]?.ops[0]).toMatchObject({ op: "upsert", id: write.id });

    const resumeFrom = subscription.resumePoint.offset;
    fixture.dropSseStreams();
    const later = await notifications.put(await documentIdFor("notifications", "u1-later"), { user_id: "u1", title: "after", read: false });
    const resumed = await until(iterator, (event) => event.changes.length > 0);
    expect(resumed.changes.flatMap((change) => change.ops.map((op) => op.id))).toEqual([later.id]);
    const reconnect = streams().at(-1);
    expect(reconnect?.headers["last-event-id"]).toBe(resumeFrom);
    expect(reconnect?.query).toEqual({ handle: subscription.resumePoint.handle, offset: resumeFrom });

    fixture.rotateShapeHandle(subscription.info.shapeId);
    const reset = await until(iterator, (event) => event.type === "reset");
    expect(reset.nextOffset).toBe("-1");
    /** @type {V1ShapeEvent[]} */
    const again = [];
    await until(iterator, (event) => event.upToDate, again);
    expect(new Set(upserted(again)).size).toBe(5);
    expect(streams().at(-1)?.query).toEqual({ offset: "-1" });
  });

  test("edge mode reads with the shape token and refreshes it through the gatekeeper", async () => {
    const edgeFixture = await startTerraBaseV1Fixture({ liveTimeoutMs: 200, tokenLifetimeMs: 1200 });
    try {
      const edgeClient = createTerraBaseV1Client({ baseUrl: edgeFixture.baseUrl, token: edgeFixture.token });
      const notifications = edgeClient.database("edge").collection("notifications");
      await notifications.put(await documentIdFor("notifications", "a"), { user_id: "u1", title: "a" });
      const subscription = await edgeClient.database("edge").subscribe({ collection: "notifications" }, { edge: true });
      open.push(subscription);
      expect(subscription.info.url.startsWith(edgeFixture.edgeUrl)).toBe(true);
      const iterator = subscription[Symbol.asyncIterator]();
      await until(iterator, (event) => event.upToDate);
      const edgeReads = () => edgeFixture.requests.filter((entry) => entry.server === "edge");
      expect(edgeReads().length).toBeGreaterThan(0);
      expect(edgeReads().every((entry) => entry.headers.authorization?.startsWith("Bearer fxshape."))).toBe(true);

      // Keep polling past the token's refresh margin.
      const deadline = Date.now() + 1500;
      const pending = until(iterator, (event) => event.changes.length > 0);
      await new Promise((resolve) => setTimeout(resolve, Math.max(0, deadline - Date.now())));
      expect(edgeFixture.gatekeeperCalls()).toBeGreaterThanOrEqual(2);

      // A rejected token forces one refresh, and reading continues.
      edgeFixture.revokeShapeTokens();
      const calls = edgeFixture.gatekeeperCalls();
      await new Promise((resolve) => setTimeout(resolve, 400));
      const write = await notifications.put(await documentIdFor("notifications", "b"), { user_id: "u1", title: "b" });
      const event = await pending;
      expect(event.changes[0]?.ops[0]).toMatchObject({ op: "upsert", id: write.id });
      expect(edgeFixture.gatekeeperCalls()).toBeGreaterThan(calls);
      expect(edgeFixture.requests.some((entry) => entry.server === "edge" && entry.headers.authorization === `Bearer ${edgeFixture.token}`)).toBe(false);
    } finally {
      for (const subscription of open.splice(0)) subscription.close();
      await edgeFixture.close();
    }
  });

  test("the gatekeeper is retried like a read; an empty snapshot is up to date without changes", async () => {
    const database = client.database("empty");
    const match = (/** @type {{ method: string; path: string }} */ request) =>
      request.method === "POST" && request.path === "/v1/databases/empty/shapes";
    fixture.failNext({ match, status: 503, code: "capacity" });
    const subscription = await database.subscribe({ collection: "notifications" });
    open.push(subscription);
    expect(fixture.requests.filter(match)).toHaveLength(2);

    const iterator = subscription[Symbol.asyncIterator]();
    /** @type {V1ShapeEvent[]} */
    const seen = [];
    const first = await until(iterator, (event) => event.upToDate, seen);
    expect(seen).toEqual([first]);
    expect(first).toMatchObject({ type: "up-to-date", snapshot: true, changes: [] });
    const write = await database.collection("notifications").put(await documentIdFor("notifications", "first"), { title: "x" });
    const live = await until(iterator, (event) => event.changes.length > 0);
    expect(live.changes[0]?.ops[0]).toMatchObject({ op: "upsert", id: write.id });
  });

  test("shape reads do not retry 502 upstream_unavailable", async () => {
    await seed("upstream", "u1", 1);
    const subscription = await client.database("upstream").subscribe({ collection: "notifications" });
    open.push(subscription);
    const shapePath = `/v1/shapes/${subscription.info.shapeId}`;
    fixture.failNext({ match: (request) => request.path === shapePath, status: 502, code: "upstream_unavailable" });
    const iterator = subscription[Symbol.asyncIterator]();
    await expect(within(iterator.next())).rejects.toMatchObject({ status: 502, code: "upstream_unavailable" });
    expect(fixture.requests.filter((request) => request.path === shapePath)).toHaveLength(1);
  });

  test("close ends iteration and rejects pending waitFor; SSE cannot use the edge", async () => {
    await seed("close", "u1", 1);
    const database = client.database("close");
    await expect(database.subscribe({ collection: "notifications" }, { mode: "sse", edge: true })).rejects.toThrow(TypeError);
    const subscription = await database.subscribe({ collection: "notifications" });
    const iterator = subscription[Symbol.asyncIterator]();
    await until(iterator, (event) => event.upToDate);
    const waiting = subscription.waitFor("999999");
    const next = iterator.next();
    subscription.close();
    await expect(within(waiting)).rejects.toThrow(/closed/u);
    expect(await within(next)).toEqual({ done: true, value: undefined });

    const controller = new AbortController();
    const aborted = await database.subscribe({ collection: "notifications" }, { signal: controller.signal });
    const abortedIterator = aborted[Symbol.asyncIterator]();
    await until(abortedIterator, (event) => event.upToDate);
    const blocked = abortedIterator.next();
    controller.abort(new Error("stop"));
    await expect(within(blocked)).rejects.toThrow("stop");
    expect(aborted.closed).toBe(true);
  });
});
