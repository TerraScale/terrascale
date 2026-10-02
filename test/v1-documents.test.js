// TerraBase v1 documents and transactions through a real HTTP boundary.
// The server is the in-test contract fixture (test/support/terrabase-v1-fixture.js),
// a stand-in until a packaged tb-server serves the v1 routes.
import { inspect } from "node:util";
import { afterAll, beforeAll, describe, expect, test } from "vitest";
import {
  createTerraBaseV1Client, documentIdFor, ExactDecimal, newIdempotencyKey, TerraBaseV1Error, V1ErrorCode, v1Filter,
} from "../src/index.js";
import { startTerraBaseV1Fixture } from "./support/terrabase-v1-fixture.js";

/** @type {Awaited<ReturnType<typeof startTerraBaseV1Fixture>>} */
let fixture;
/** @type {import("../src/index.js").TerraBaseV1Client} */
let client;

beforeAll(async () => {
  fixture = await startTerraBaseV1Fixture();
  client = createTerraBaseV1Client({ baseUrl: fixture.baseUrl, token: fixture.token });
});
afterAll(async () => {
  await fixture.close();
});

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
 * @param {string} method
 * @param {string} pathSuffix
 */
const requestsTo = (method, pathSuffix) =>
  fixture.requests.filter((request) => request.method === method && request.path.endsWith(pathSuffix));

describe("TerraBase v1 documents", () => {
  test("documentIdFor follows the contract UUIDv5 rule", async () => {
    // Vectors computed independently with Python's uuid.uuid5.
    expect(await documentIdFor("users", "ana@example.com")).toBe("b85e5c40-9a0a-517f-ba69-779a0cd2b159");
    expect(await documentIdFor("login_throttle", "ünïcode-key")).toBe("0626913b-a545-50f9-b7ae-8c80467733d6");
    await expect(documentIdFor("Users", "x")).rejects.toThrow(TypeError);
  });

  test("put, get, conditional writes, patch and delete follow the wire contract", async () => {
    const accounts = client.database("main").collection("accounts");
    const id = await documentIdFor("accounts", "acct-1");

    const created = await accounts.put(id, { owner: "ana", tags: ["a"] }, { ifNoneMatchAny: true });
    expect(created).toMatchObject({ id, created: true, durability: "local_durable" });
    expect(created.version).toMatch(/^tbv1-[0-9a-f]{32}$/u);
    expect(created.offset).toMatch(/^[1-9]\d*$/u);
    expect(requestsTo("PUT", id).at(-1)?.headers["if-none-match"]).toBe("*");

    const read = await accounts.get(id);
    expect(read).toEqual({ id, version: created.version, state: "value", document: { owner: "ana", tags: ["a"] } });

    const notModified = await failure(accounts.get(id, { ifNoneMatch: created.version }));
    expect(notModified).toMatchObject({ status: 304, code: "not_modified" });

    const again = await failure(accounts.put(id, { owner: "bo" }, { ifNoneMatchAny: true }));
    expect(again).toMatchObject({ status: 412, code: "precondition_failed" });

    const replaced = await accounts.put(id, { owner: "bo", tags: ["a"], count: 1 }, { ifMatch: created.version });
    expect(replaced.created).toBe(false);
    expect(BigInt(replaced.offset)).toBeGreaterThan(BigInt(created.offset));
    expect(requestsTo("PUT", id).at(-1)?.headers["if-match"]).toBe(`"${created.version}"`);

    const stale = await failure(accounts.put(id, { owner: "cy" }, { ifMatch: created.version }));
    expect(stale).toMatchObject({ status: 412, code: "precondition_failed" });

    const merged = await accounts.patch(id, { merge: { owner: null, nick: "b" } }, { ifMatch: replaced.version });
    expect(requestsTo("PATCH", id).at(-1)?.headers["content-type"]).toBe("application/merge-patch+json");
    const ops = await accounts.patch(id, {
      ops: [
        { op: "set", path: "/profile/name", value: "Bo" },
        { op: "unset", path: "/nick" },
        { op: "inc", path: "/count", by: 2 },
        { op: "append", path: "/tags", value: "beta" },
      ],
    });
    expect(requestsTo("PATCH", id).at(-1)?.headers["content-type"]).toBe("application/vnd.terrabase.ops+json");
    expect(BigInt(ops.offset)).toBeGreaterThan(BigInt(merged.offset));
    expect((await accounts.get(id))).toMatchObject({
      state: "value", document: { tags: ["a", "beta"], count: 3, profile: { name: "Bo" } },
    });

    const conflict = await failure(accounts.patch(id, { ops: [{ op: "inc", path: "/profile/name", by: 1 }] }));
    expect(conflict).toMatchObject({ status: 409, code: "patch_conflict" });

    const deleted = await accounts.delete(id);
    const gone = await failure(accounts.get(id));
    expect(gone).toMatchObject({ status: 404, code: "not_found", etag: deleted.version });
    expect(await failure(accounts.delete(id))).toMatchObject({ status: 404, code: "not_found" });
    expect(await failure(accounts.patch(id, { merge: { a: 1 } }))).toMatchObject({ status: 404, code: "not_found" });

    const recreated = await accounts.put(id, { owner: "dee" }, { ifMatch: /** @type {string} */ (gone.etag) });
    expect(recreated.created).toBe(true);
  });

  test("patches branch on own members and paths follow RFC 6901", async () => {
    const coll = client.database("main").collection("paths");
    const id = await documentIdFor("paths", "rfc6901");
    await coll.put(id, { "a/b": 1, "m~n": 2 });
    // An inherited merge must not replace the own ops.
    const patch = Object.assign(Object.create({ merge: { wrong: true } }), { ops: [{ op: "inc", path: "/a~1b", by: 1 }, { op: "set", path: "/m~0n", value: 3 }] });
    await coll.patch(id, patch);
    expect(requestsTo("PATCH", id).at(-1)?.headers["content-type"]).toBe("application/vnd.terrabase.ops+json");
    expect(await coll.get(id)).toMatchObject({ document: { "a/b": 2, "m~n": 3 } });
    expect((await coll.query({ filter: v1Filter.eq("/a~1b", 2) })).items.map((item) => item.id)).toEqual([id]);
    for (const bad of ["/a~2b", "/a~", "a", ""]) {
      expect(() => v1Filter.eq(bad, 1)).toThrow(TypeError);
      await expect(coll.patch(id, { ops: [{ op: "unset", path: bad }] })).rejects.toThrow(TypeError);
    }
  });

  test("prototype-sensitive member names stay ordinary data and projections never alias stored data", async () => {
    const coll = client.database("main").collection("proto");
    const id = await documentIdFor("proto", "proto");
    await coll.put(id, /** @type {any} */ (JSON.parse('{"__proto__":{"x":1},"profile":{"name":"Ana"}}')));
    await coll.patch(id, { merge: /** @type {any} */ (JSON.parse('{"__proto__":{"y":2}}')) });
    await coll.patch(id, { ops: [{ op: "set", path: "/__proto__/z", value: 3 }] });
    const projected = await coll.query({ project: ["/__proto__/x", "/profile", "/profile/name/child"] });
    expect(JSON.stringify(projected.items[0]?.state === "value" && projected.items[0].document))
      .toBe('{"__proto__":{"x":1},"profile":{"name":"Ana"}}');
    const read = await coll.get(id);
    expect(JSON.stringify(read.state === "value" && read.document)).toBe('{"__proto__":{"x":1,"y":2,"z":3},"profile":{"name":"Ana"}}');
    expect(/** @type {Record<string, unknown>} */ ({}).x).toBeUndefined();
  });

  test("conflicted documents decode every sibling and reject a live sibling without a document", async () => {
    const coll = client.database("main").collection("conflicts");
    const id = await documentIdFor("conflicts", "c");
    const event = (/** @type {string} */ sequence) => ({ replica: "0192aaaa-0000-7000-8000-000000000001", incarnation: "0".repeat(32), sequence });
    const conflict = { id, version: `tbv1-${"a".repeat(32)}`, state: "conflict", siblings: [
      { event: event("12"), deleted: false, document: { n: 1 } },
      { event: event("14"), deleted: true },
    ] };
    const onDocument = (/** @type {{ path: string }} */ request) => request.path.endsWith(id);
    fixture.failNext({ match: onDocument, status: 200, code: "", body: JSON.stringify(conflict) });
    const read = await coll.get(id);
    expect(read).toMatchObject({ state: "conflict", siblings: [{ deleted: false, document: { n: 1 } }, { deleted: true, document: undefined }] });
    const broken = { ...conflict, siblings: [{ event: event("12"), deleted: false }] };
    fixture.failNext({ match: onDocument, status: 200, code: "", body: JSON.stringify(broken) });
    await expect(coll.get(id)).rejects.toThrow(/live sibling document/u);
  });

  test("create returns a server-generated UUIDv7 and its Location", async () => {
    const notes = client.database("main").collection("notes");
    const created = await notes.create({ text: "hello" });
    expect(created.id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u);
    expect(created).toMatchObject({ created: true, location: `/v1/databases/main/collections/notes/documents/${created.id}` });
    expect((await notes.get(created.id)).state).toBe("value");
  });

  test("numbers stay exact in both directions", async () => {
    const ledger = client.database("main").collection("ledger");
    const id = await documentIdFor("ledger", "exact");
    await ledger.put(id, {
      big: 123456789012345678901234567890n,
      fine: new ExactDecimal("0.100000000000000000000000000001"),
      small: 1.5,
      int: 42,
    });
    const body = requestsTo("PUT", id).at(-1)?.body ?? "";
    expect(body).toContain('"big":123456789012345678901234567890');
    expect(body).toContain('"fine":0.100000000000000000000000000001');

    await ledger.patch(id, { ops: [{ op: "inc", path: "/big", by: 10n }, { op: "inc", path: "/fine", by: ExactDecimal.from("0.000000000000000000000000000001") }] });
    const read = await ledger.get(id);
    if (read.state !== "value") throw new Error("expected a value");
    const { big, fine, small, int } = read.document;
    expect(big).toBeInstanceOf(ExactDecimal);
    expect(/** @type {ExactDecimal} */ (big).toBigInt()).toBe(123456789012345678901234567900n);
    expect(fine).toBeInstanceOf(ExactDecimal);
    expect(String(fine)).toBe("0.100000000000000000000000000002");
    expect(/** @type {ExactDecimal} */ (fine).equals("1.00000000000000000000000000002e-1")).toBe(true);
    expect(small).toBe(1.5);
    expect(int).toBe(42);
    expect(JSON.stringify(read.document)).toContain('"big":123456789012345678901234567900');
    await expect(ledger.put(id, { bad: Number.NaN })).rejects.toThrow(TypeError);
  });

  test("unique violations name the constraint", async () => {
    const users = client.database("main").collection("users");
    await users.put(await documentIdFor("users", "ana"), { email: "Ana@Example.com" });
    const error = await failure(users.put(await documentIdFor("users", "ana-2"), { email: "ana@example.COM" }));
    expect(error).toMatchObject({ status: 409, code: "unique_violation", constraint: "email" });
  });

  test("idempotent creates replay the original response and reject changed bodies", async () => {
    const notes = client.database("main").collection("notes");
    const key = newIdempotencyKey();
    expect(key).toMatch(/^[A-Za-z0-9_-]{1,64}$/u);
    const first = await notes.create({ text: "once" }, { idempotencyKey: key });
    const second = await notes.create({ text: "once" }, { idempotencyKey: key });
    expect(second).toEqual(first);
    expect(requestsTo("POST", "/notes/documents").at(-1)?.headers["idempotency-key"]).toBe(key);
    const changed = await failure(notes.create({ text: "twice" }, { idempotencyKey: key }));
    expect(changed).toMatchObject({ status: 409, code: "idempotency_conflict" });
    await expect(notes.create({ text: "x" }, { idempotencyKey: "has space" })).rejects.toThrow(TypeError);
  });
});

describe("TerraBase v1 retry policy", () => {
  /** @param {string} id */
  const onDocument = (id) => (/** @type {{ path: string }} */ request) => request.path.endsWith(id);

  test("429 is retried after Retry-After, even for unkeyed writes", async () => {
    const coll = client.database("main").collection("retry");
    const id = await documentIdFor("retry", "429");
    fixture.failNext({ match: onDocument(id), status: 429, code: "rate_limited", retryAfter: 0 });
    const before = requestsTo("PUT", id).length;
    await coll.put(id, { n: 1 });
    expect(requestsTo("PUT", id).length - before).toBe(2);
  });

  test("transient 503s are retried for reads but not for unkeyed writes", async () => {
    const coll = client.database("main").collection("retry");
    const id = await documentIdFor("retry", "503");
    await coll.put(id, { n: 1 });
    fixture.failNext({ match: onDocument(id), status: 503, code: "capacity" });
    expect((await coll.get(id)).state).toBe("value");
    expect(requestsTo("GET", id)).toHaveLength(2);

    fixture.failNext({ match: onDocument(id), status: 503, code: "storage_unavailable" });
    const before = requestsTo("PUT", id).length;
    expect(await failure(coll.put(id, { n: 2 }))).toMatchObject({ status: 503, code: "storage_unavailable" });
    expect(requestsTo("PUT", id).length - before).toBe(1);

    fixture.failNext({ match: onDocument(id), status: 503, code: "stored_corruption" });
    expect(await failure(coll.get(id))).toMatchObject({ status: 503, code: "stored_corruption" });
  });

  test("commit_uncertain is retried only with an Idempotency-Key and returns the original outcome", async () => {
    const coll = client.database("main").collection("retry");
    const match = (/** @type {{ method: string; path: string }} */ request) =>
      request.method === "POST" && request.path.endsWith("/retry/documents");
    fixture.failNext({ match, status: 503, code: "commit_uncertain", afterCommit: true });
    const key = newIdempotencyKey();
    const result = await coll.create({ n: "keyed" }, { idempotencyKey: key });
    const attempts = fixture.requests.filter((request) => request.headers["idempotency-key"] === key);
    expect(attempts).toHaveLength(2);
    expect((await coll.get(result.id)).state).toBe("value");

    fixture.failNext({ match, status: 503, code: "commit_uncertain", afterCommit: true });
    const before = fixture.requests.filter(match).length;
    expect(await failure(coll.create({ n: "unkeyed" }))).toMatchObject({ status: 503, code: "commit_uncertain" });
    expect(fixture.requests.filter(match).length - before).toBe(1);
  });

  test("500 internal_error is retried only with an Idempotency-Key", async () => {
    const coll = client.database("main").collection("retry");
    const match = (/** @type {{ method: string; path: string }} */ request) =>
      request.method === "POST" && request.path.endsWith("/retry/documents");
    fixture.failNext({ match, status: 500, code: "internal_error", afterCommit: true });
    const key = newIdempotencyKey();
    const keyed = await coll.create({ n: "internal-keyed" }, { idempotencyKey: key });
    expect(fixture.requests.filter((request) => request.headers["idempotency-key"] === key)).toHaveLength(2);
    expect((await coll.get(keyed.id)).state).toBe("value");

    fixture.failNext({ match, status: 500, code: "internal_error" });
    const before = fixture.requests.filter(match).length;
    const error = await failure(coll.create({ n: "internal-unkeyed" }));
    expect(error).toMatchObject({ status: 500, code: V1ErrorCode.internalError });
    expect(fixture.requests.filter(match).length - before).toBe(1);
  });

  test("autoIdempotencyKey keys every mutation so commit_uncertain is retried", async () => {
    const auto = createTerraBaseV1Client({ baseUrl: fixture.baseUrl, token: fixture.token, autoIdempotencyKey: true });
    const coll = auto.database("main").collection("autokey");
    const match = (/** @type {{ method: string; path: string }} */ request) =>
      request.method === "POST" && request.path.endsWith("/autokey/documents");
    fixture.failNext({ match, status: 503, code: "commit_uncertain", afterCommit: true });
    const created = await coll.create({ n: 1 });
    const attempts = fixture.requests.filter(match);
    expect(attempts).toHaveLength(2);
    const key = attempts[0]?.headers["idempotency-key"];
    expect(key).toMatch(/^[A-Za-z0-9_-]{1,64}$/u);
    expect(attempts[1]?.headers["idempotency-key"]).toBe(key);
    expect((await coll.get(created.id)).state).toBe("value");

    const txMatch = (/** @type {{ method: string; path: string }} */ request) =>
      request.method === "POST" && request.path === "/v1/databases/main/transactions";
    fixture.failNext({ match: txMatch, status: 500, code: "internal_error", afterCommit: true });
    const result = await auto.database("main").transaction().create("autokey", { n: 2 }).commit();
    const txAttempts = fixture.requests.filter(txMatch);
    expect(txAttempts).toHaveLength(2);
    expect(txAttempts[1]?.headers["idempotency-key"]).toBe(txAttempts[0]?.headers["idempotency-key"]);
    expect(result.results).toHaveLength(1);

    // Reads and queries never carry one; an explicit key wins.
    await coll.get(created.id);
    await coll.query({ limit: 1 });
    expect(fixture.requests.filter((request) => request.path.includes("/autokey/") && (request.method === "GET" || request.path.endsWith("/query")))
      .every((request) => request.headers["idempotency-key"] === undefined)).toBe(true);
    await coll.create({ n: 3 }, { idempotencyKey: "explicit-key" });
    expect(fixture.requests.filter(match).at(-1)?.headers["idempotency-key"]).toBe("explicit-key");
    expect(() => createTerraBaseV1Client({ baseUrl: fixture.baseUrl, token: fixture.token, autoIdempotencyKey: /** @type {any} */ ("yes") })).toThrow(TypeError);
  });

  test("a Retry-After above 30 seconds is not shortened: the error is returned with it", async () => {
    const coll = client.database("main").collection("retry");
    const id = await documentIdFor("retry", "long-wait");
    fixture.failNext({ match: onDocument(id), status: 429, code: "rate_limited", retryAfter: 120 });
    const error = await failure(coll.get(id));
    expect(error).toMatchObject({ status: 429, code: "rate_limited", retryAfter: 120 });
    expect(requestsTo("GET", id)).toHaveLength(1);
  });

  test("a body cut off after the headers is retried for reads, not for unkeyed writes", async () => {
    const coll = client.database("main").collection("retry");
    const id = await documentIdFor("retry", "truncated");
    await coll.put(id, { n: 1 });
    fixture.failNext({ match: (request) => request.method === "GET" && onDocument(id)(request), status: 200, code: "", truncate: true });
    expect(await coll.get(id)).toMatchObject({ state: "value", document: { n: 1 } });
    expect(requestsTo("GET", id)).toHaveLength(2);

    fixture.failNext({ match: (request) => request.method === "PUT" && onDocument(id)(request), status: 200, code: "", truncate: true });
    const before = requestsTo("PUT", id).length;
    await expect(coll.put(id, { n: 2 })).rejects.toMatchObject({ name: "BodyTransportError" });
    expect(requestsTo("PUT", id).length - before).toBe(1);
  });

  test("authentication failures are not retried and the key never leaks", async () => {
    const intruder = createTerraBaseV1Client({ baseUrl: fixture.baseUrl, token: `tbk1.${"x".repeat(22)}.${"y".repeat(43)}` });
    const error = await failure(intruder.database("main").collection("accounts").get(await documentIdFor("accounts", "z")));
    expect(error).toMatchObject({ status: 401, code: "invalid_key" });
    expect(`${error.message}${error.stack}`).not.toContain("y".repeat(43));
    expect(JSON.stringify(client)).not.toContain(fixture.token);
    expect(inspect(client, { depth: 8, showHidden: true })).not.toContain(fixture.token.split(".")[2]);
  });

  test("configuration is validated", () => {
    expect(() => createTerraBaseV1Client({ baseUrl: "http://example.com", token: fixture.token })).toThrow(/loopback/u);
    expect(() => createTerraBaseV1Client({ baseUrl: "https://user:pw@example.com", token: fixture.token })).toThrow(TypeError);
    expect(() => createTerraBaseV1Client({ baseUrl: "https://example.com", token: "tbk1.short" })).toThrow(/tbk1/u);
    expect(() => client.database("Main")).toThrow(TypeError);
    expect(() => client.database("main").collection("c").get("not-a-uuid")).rejects.toThrow(TypeError);
  });
});

describe("TerraBase v1 transactions", () => {
  test("commits ops across collections atomically and returns per-op results", async () => {
    const db = client.database("tx");
    const org = await documentIdFor("orgs", "acme");
    const user = await documentIdFor("members", "ana");
    const plan = await documentIdFor("plans", "pro");
    const basic = await documentIdFor("plans", "basic");
    await db.collection("plans").put(plan, { visible: true, seats: 5 });
    await db.collection("plans").put(basic, { visible: true, seats: 1 });
    const planVersion = (await db.collection("plans").get(plan)).version;

    const result = await db.transaction()
      .create("orgs", { name: "Acme", db_count: 0 }, org)
      .put("members", user, { org, role: "owner" }, { ifNoneMatchAny: true })
      .patch("plans", plan, { ops: [{ op: "inc", path: "/seats", by: -1 }] }, { ifMatch: planVersion })
      .assert("plans", basic, v1Filter.eq("/visible", true))
      .assertAbsent("invites", await documentIdFor("invites", "ana"))
      .create("audit", { event: "signup" })
      .assertCount("plans", v1Filter.eq("/visible", true), { gte: 1, lte: 2 })
      .commit({ idempotencyKey: newIdempotencyKey() });

    expect(result.offset).toMatch(/^[1-9]\d*$/u);
    expect(result.results).toHaveLength(7);
    expect(result.results[0]).toMatchObject({ id: org });
    expect(result.results[3]).toEqual({ asserted: true });
    expect(result.results[4]).toEqual({ asserted: true });
    expect(result.results[5]).toHaveProperty("id");
    expect(result.results[6]).toEqual({ asserted: true, count: "2" });
    const sent = JSON.parse(requestsTo("POST", "/tx/transactions").at(-1)?.body ?? "{}");
    expect(sent.ops[1]).toMatchObject({ op: "put", collection: "members", if_none_match: "*" });
    expect(sent.ops[2]).toMatchObject({ op: "patch", if_match: planVersion, ops: [{ op: "inc", path: "/seats", by: -1 }] });
    expect(sent.ops[4]).toMatchObject({ op: "assert", absent: true });
    expect(sent.ops[6]).toEqual({ op: "assert_count", collection: "plans", filter: { eq: ["/visible", true] }, gte: 1, lte: 2 });
    const seats = await db.collection("plans").get(plan);
    expect(seats).toMatchObject({ document: { seats: 4 } });
  });

  test("failures name the failing op and leave nothing applied", async () => {
    const db = client.database("tx");
    const users = db.collection("users");
    await users.put(await documentIdFor("users", "taken"), { email: "taken@example.com" });
    const fresh = await documentIdFor("users", "fresh");

    const unique = await failure(db.transaction()
      .put("users", fresh, { email: "new@example.com" })
      .create("users", { email: "TAKEN@example.com" })
      .commit());
    expect(unique).toMatchObject({ status: 409, code: "unique_violation", op: 1, constraint: "email" });
    expect(await failure(users.get(fresh))).toMatchObject({ code: "not_found" });

    const asserted = await failure(db.transaction()
      .put("users", fresh, { email: "new@example.com" })
      .assert("users", await documentIdFor("users", "taken"), v1Filter.eq("/email", "other"))
      .commit());
    expect(asserted).toMatchObject({ status: 412, code: "precondition_failed", op: 1 });

    const counted = await failure(db.transaction()
      .put("users", fresh, { email: "new@example.com" })
      .assertCount("users", v1Filter.prefix(v1Filter.lower("/email"), "taken@"), { lte: 0 })
      .commit());
    expect(counted).toMatchObject({ status: 412, code: "precondition_failed", op: 1 });

    const missing = await failure(db.transaction()
      .patch("users", await documentIdFor("users", "nobody"), { merge: { a: 1 } })
      .commit());
    expect(missing).toMatchObject({ status: 404, code: "not_found", op: 0 });

    const duplicate = await failure(db.transaction()
      .put("users", fresh, { email: "a@example.com" })
      .delete("users", fresh)
      .commit());
    expect(duplicate).toMatchObject({ status: 400, code: "invalid_request", op: 1 });
  });

  test("the builder rejects malformed ops before sending", () => {
    const tx = client.database("tx").transaction();
    expect(() => tx.patch("users", "00000000-0000-0000-0000-000000000000", /** @type {any} */ ({ merge: {}, ops: [] }))).toThrow(TypeError);
    expect(() => tx.delete("users", "00000000-0000-0000-0000-000000000000", /** @type {any} */ ({ ifNoneMatchAny: true }))).toThrow(TypeError);
    expect(() => tx.put("users", "00000000-0000-0000-0000-000000000000", {}, { ifMatch: "nope" })).toThrow(TypeError);
    expect(() => tx.assertCount("users", v1Filter.exists("/email"), {})).toThrow(TypeError);
    expect(() => tx.assertCount("users", /** @type {any} */ (undefined), { lte: 1 })).toThrow(/filter/u);
    expect(() => tx.patch("users", "00000000-0000-0000-0000-000000000000", { merge: {} }, /** @type {any} */ ({ ifNoneMatchAny: true }))).toThrow(TypeError);
    expect(tx.size).toBe(0);
    return expect(tx.commit()).rejects.toThrow(TypeError);
  });
});
