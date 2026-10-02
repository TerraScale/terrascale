// One TerraBase v1 data-plane conformance suite, run through a real HTTP
// boundary against two targets:
//
// - "fixture": the in-test loopback contract fixture
//   (test/support/terrabase-v1-fixture.js). Always runs; it is a stand-in and
//   not evidence of producer behavior.
// - "live": a real tb-server, only when TB_V1_URL and TB_V1_DATA_KEY are set.
//   Otherwise it is reported as skipped. Use NODE_EXTRA_CA_CERTS for a private
//   CA (for example mkcert's root). Optional:
//     TB_V1_DATABASE           database name (default "app")
//     TB_V1_COLLECTION         collection name (default "todos"); schemaless,
//                              with a unique constraint on /email
//     TB_V1_UNIQUE_CONSTRAINT  that constraint's name (default "email"); set
//                              it to "" when the collection has none
//     TB_V1_REVOKED_KEY        a revoked tbk1 data key, to check 401 on revocation
//
// Every run writes documents under a fresh run ID and deletes them afterwards.
import { afterAll, beforeAll, describe, expect, test } from "vitest";
import {
  createTerraBaseV1Client, documentIdFor, ExactDecimal, isTerraBaseV1Error, newIdempotencyKey, TerraBaseV1Error,
  V1ForbiddenScopeError, V1InvalidKeyError, V1NotFoundError, V1PreconditionFailedError, V1UniqueViolationError, v1Filter,
} from "../src/index.js";
import { startTerraBaseV1Fixture } from "./support/terrabase-v1-fixture.js";

/**
 * @typedef {object} Target
 * @property {string} baseUrl
 * @property {string} token
 * @property {string} database
 * @property {string} collection
 * @property {string} uniqueConstraint "" when the collection has no unique constraint on /email.
 * @property {() => import("../src/index.js").TerraBaseV1Client | undefined} outOfScopeClient A client whose key has no grant on `database`, if available.
 * @property {(() => Promise<string>) | undefined} revokedKey A key that was valid and is now revoked.
 * @property {() => Promise<void>} close
 */

/**
 * @param {Promise<unknown>} promise
 * @returns {Promise<TerraBaseV1Error>}
 */
async function failure(promise) {
  const error = await promise.then(() => undefined, (/** @type {unknown} */ reason) => reason);
  expect(error).toBeInstanceOf(TerraBaseV1Error);
  return /** @type {TerraBaseV1Error} */ (error);
}

/**
 * @param {string} name
 * @param {() => Promise<Target>} connect
 */
function conformance(name, connect) {
  describe(`TerraBase v1 data-plane conformance (${name})`, () => {
    /** @type {Target} */
    let target;
    /** @type {import("../src/index.js").V1Collection} */
    let todos;
    /** @type {import("../src/index.js").V1Database} */
    let db;
    const run = `r${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;
    /** @type {Set<string>} */
    const created = new Set();

    beforeAll(async () => {
      target = await connect();
      db = createTerraBaseV1Client({ baseUrl: target.baseUrl, token: target.token }).database(target.database);
      todos = db.collection(target.collection);
    });
    afterAll(async () => {
      for (const id of created) await todos?.delete(id).catch(() => undefined);
      await target?.close();
    });

    /** @param {string} key */
    const idFor = async (key) => {
      const id = await documentIdFor(target.collection, `${run}-${key}`);
      created.add(id);
      return id;
    };

    test("create, get, conditional put, 304 and stale If-Match", async () => {
      const first = await todos.create({ run, title: "first", done: false }, { idempotencyKey: newIdempotencyKey() });
      created.add(first.id);
      expect(first).toMatchObject({ created: true });
      expect(first.version).toMatch(/^tbv1-[0-9a-f]{32}$/u);
      expect(first.offset).toMatch(/^(?:0|[1-9]\d*)$/u);

      const read = await todos.get(first.id);
      expect(read).toEqual({ id: first.id, version: first.version, state: "value", document: { run, title: "first", done: false } });
      expect(await failure(todos.get(first.id, { ifNoneMatch: first.version }))).toMatchObject({ status: 304, code: "not_modified" });

      const updated = await todos.put(first.id, { run, title: "first", done: true }, { ifMatch: read.version });
      expect(updated.created).toBe(false);
      expect(BigInt(updated.offset)).toBeGreaterThan(BigInt(first.offset));

      const stale = await failure(todos.put(first.id, { run, title: "lost" }, { ifMatch: read.version }));
      expect(stale).toBeInstanceOf(V1PreconditionFailedError);
      expect(stale).toMatchObject({ status: 412, code: "precondition_failed" });
      expect(await failure(todos.put(first.id, { run }, { ifNoneMatchAny: true }))).toBeInstanceOf(V1PreconditionFailedError);

      const id = await idFor("put-new");
      expect(await todos.put(id, { run, title: "natural" }, { ifNoneMatchAny: true })).toMatchObject({ id, created: true });
    });

    test("unique violations are 409 with the constraint name", async (context) => {
      if (target.uniqueConstraint === "") context.skip();
      await todos.put(await idFor("u1"), { run, email: `${run}@Example.com` });
      const duplicate = await failure(todos.put(await idFor("u2"), { run, email: `${run}@example.COM` }));
      expect(duplicate).toBeInstanceOf(V1UniqueViolationError);
      expect(duplicate).toMatchObject({ status: 409, code: "unique_violation", constraint: target.uniqueConstraint });
      expect(isTerraBaseV1Error(duplicate, "unique_violation")).toBe(true);
    });

    test("patch ops and numbers stay exact", async () => {
      const id = await idFor("numbers");
      const big = 2n ** 64n + 1n;
      const written = await todos.put(id, { run, n: 1, big, fine: new ExactDecimal("0.1000000000000000000001") });
      const patched = await todos.patch(id, {
        ops: [
          { op: "inc", path: "/n", by: 2 },
          { op: "inc", path: "/big", by: 1n },
          { op: "inc", path: "/fine", by: new ExactDecimal("0.0000000000000000000001") },
          { op: "set", path: "/meta/tag", value: "x" },
          { op: "append", path: "/tags", value: "a" },
        ],
      }, { ifMatch: written.version, idempotencyKey: newIdempotencyKey() });
      await todos.patch(id, { merge: { meta: null } }, { ifMatch: patched.version });
      const read = await todos.get(id);
      if (read.state !== "value") throw new Error("expected a value");
      expect(read.document.n).toBe(3);
      expect(read.document.big).toBeInstanceOf(ExactDecimal);
      expect(/** @type {ExactDecimal} */ (read.document.big).toBigInt()).toBe(big + 1n);
      expect(/** @type {ExactDecimal} */ (read.document.fine).equals("0.1000000000000000000002")).toBe(true);
      expect(read.document.tags).toEqual(["a"]);
      expect(read.document).not.toHaveProperty("meta");
      expect(await failure(todos.patch(id, { ops: [{ op: "inc", path: "/tags", by: 1 }] }))).toMatchObject({ status: 409, code: "patch_conflict" });
    });

    test("query with count and cursor pagination", async () => {
      for (const key of ["q1", "q2", "q3"]) await todos.put(await idFor(key), { run, kind: "query", title: key });
      const filter = v1Filter.and(v1Filter.eq("/run", run), v1Filter.eq("/kind", "query"));
      const first = await todos.query({ filter, limit: 2, count: true });
      expect(first.count).toEqual({ value: "3", exact: true });
      /** @type {string[]} */
      const titles = [];
      for await (const item of todos.items({ filter, limit: 2 }))
        if (item.state === "value") titles.push(String(item.document.title));
      expect(titles.sort()).toEqual(["q1", "q2", "q3"]);
      let pages = 0;
      for await (const page of todos.pages({ filter, limit: 2, count: true })) {
        pages++;
        if (pages > 1) expect(page.count).toBeUndefined();
      }
      expect(pages).toBeGreaterThanOrEqual(2);
    });

    test("transactions commit atomically and name the failing op", async () => {
      const a = await idFor("tx-a");
      const b = await idFor("tx-b");
      const committed = await db.transaction()
        .create(target.collection, { run, kind: "tx", title: "a" }, a)
        .put(target.collection, b, { run, kind: "tx", title: "b" }, { ifNoneMatchAny: true })
        .assertCount(target.collection, v1Filter.and(v1Filter.eq("/run", run), v1Filter.eq("/kind", "tx")), { lte: 0 })
        .commit({ idempotencyKey: newIdempotencyKey() });
      expect(committed.results).toHaveLength(3);
      expect(committed.results[0]).toMatchObject({ id: a });
      expect(committed.results[2]).toEqual({ asserted: true, count: "0" });

      const version = (await todos.get(a)).version;
      const failed = await failure(db.transaction()
        .patch(target.collection, a, { merge: { title: "a2" } }, { ifMatch: version })
        .assert(target.collection, b, v1Filter.eq("/title", "not-b"))
        .commit());
      expect(failed).toBeInstanceOf(V1PreconditionFailedError);
      expect(failed).toMatchObject({ status: 412, op: 1 });
      expect(await todos.get(a)).toMatchObject({ version, document: { title: "a" } });
    });

    test("delete tombstones and 404 carries the tombstone version", async () => {
      const id = await idFor("deleted");
      const written = await todos.put(id, { run });
      const deleted = await todos.delete(id, { ifMatch: written.version });
      const gone = await failure(todos.get(id));
      expect(gone).toBeInstanceOf(V1NotFoundError);
      expect(gone).toMatchObject({ status: 404, code: "not_found", etag: deleted.version });
      expect(await failure(todos.delete(id))).toMatchObject({ status: 404, code: "not_found" });
      expect(await todos.put(id, { run }, { ifMatch: /** @type {string} */ (gone.etag) })).toMatchObject({ created: true });
    });

    test("an unknown key is 401 invalid_key", async () => {
      const secret = "Z".repeat(43);
      const stranger = createTerraBaseV1Client({ baseUrl: target.baseUrl, token: `tbk1.${"Q".repeat(22)}.${secret}` });
      const error = await failure(stranger.database(target.database).collection(target.collection).get(await idFor("nobody")));
      expect(error).toBeInstanceOf(V1InvalidKeyError);
      expect(error).toMatchObject({ status: 401, code: "invalid_key" });
      expect(`${error.message}${error.stack}`).not.toContain(secret);
    });

    test("a revoked key is 401 invalid_key", async (context) => {
      if (target.revokedKey === undefined) return context.skip();
      const revoked = createTerraBaseV1Client({ baseUrl: target.baseUrl, token: await target.revokedKey() });
      const error = await failure(revoked.database(target.database).collection(target.collection).get(await idFor("revoked")));
      expect(error).toBeInstanceOf(V1InvalidKeyError);
    });

    test("requests outside the key's grants are 403 forbidden_scope", async (context) => {
      const outside = target.outOfScopeClient();
      if (outside === undefined) return context.skip();
      const error = await failure(outside.database(target.database).collection(target.collection).get(await idFor("scope")));
      expect(error).toBeInstanceOf(V1ForbiddenScopeError);
      expect(error).toMatchObject({ status: 403, code: "forbidden_scope" });
    });
  });
}

conformance("fixture", async () => {
  const fixture = await startTerraBaseV1Fixture();
  return {
    baseUrl: fixture.baseUrl,
    token: fixture.token,
    database: "app",
    collection: "todos",
    uniqueConstraint: "email",
    outOfScopeClient: () => createTerraBaseV1Client({
      baseUrl: fixture.baseUrl,
      token: fixture.issueKey({ database: "other", collections: ["*"], permissions: ["read", "write"] }),
    }),
    revokedKey: async () => {
      const token = fixture.issueKey({ database: "app", collections: ["todos"], permissions: ["read"] });
      const client = createTerraBaseV1Client({ baseUrl: fixture.baseUrl, token });
      await expect(client.database("app").collection("todos").get(await documentIdFor("todos", "probe")))
        .rejects.toBeInstanceOf(V1NotFoundError);
      fixture.revokeKey(token);
      return token;
    },
    close: () => fixture.close(),
  };
});

const liveUrl = process.env.TB_V1_URL;
const liveKey = process.env.TB_V1_DATA_KEY;
describe.skipIf(liveUrl === undefined || liveUrl === "" || liveKey === undefined || liveKey === "")("live tb-server", () => {
  conformance("live", async () => {
    const revoked = process.env.TB_V1_REVOKED_KEY;
    return {
      baseUrl: /** @type {string} */ (liveUrl),
      token: /** @type {string} */ (liveKey),
      database: process.env.TB_V1_DATABASE ?? "app",
      collection: process.env.TB_V1_COLLECTION ?? "todos",
      uniqueConstraint: process.env.TB_V1_UNIQUE_CONSTRAINT ?? "email",
      // A live key's grant cannot be narrowed from here; the control plane
      // issues scoped keys. Covered by the fixture target only.
      outOfScopeClient: () => undefined,
      revokedKey: revoked === undefined || revoked === "" ? undefined : async () => revoked,
      close: async () => undefined,
    };
  });
});
