import { createServer } from "node:http";
import { afterEach, describe, expect, test } from "vitest";
import { createTerraBaseV1Client, ExactDecimal, TerraBaseV1Error, V1ForbiddenScopeError, V1InvalidKeyError } from "../src/index.js";

// These loopback fixtures verify SDK wire/decoder behavior, not producer qualification.
const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
const entityId = "01ARZ3NDEKTSV4RRFFQ69G5FA2";
const token = "fixture.payload.signature";
const version = "opaque-etag";
const prefix = `/v1/projects/${project}/databases/app`;
/** @typedef {{ method: string | undefined; url: URL; headers: import("node:http").IncomingHttpHeaders; body: string }} WireRequest */
/** @type {import("node:http").Server[]} */
const servers = [];

afterEach(async () => {
  for (const server of servers.splice(0)) {
    server.closeAllConnections();
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve(undefined)));
  }
});

/**
 * @param {(request: WireRequest, response: import("node:http").ServerResponse) => void} handle
 */
async function fixture(handle) {
  /** @type {WireRequest[]} */
  const requests = [];
  const server = createServer((request, response) => {
    void (async () => {
      let body = "";
      for await (const chunk of request) body += chunk.toString();
      const record = { method: request.method, url: new URL(request.url ?? "/", "http://localhost"), headers: request.headers, body };
      requests.push(record);
      handle(record, response);
    })().catch(error => response.destroy(error));
  });
  servers.push(server);
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => resolve(undefined));
  });
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("Expected loopback address.");
  const baseUrl = `http://127.0.0.1:${address.port}`;
  const client = createTerraBaseV1Client({ project, token, baseUrl });
  return { requests, baseUrl, client, db: client.database("app") };
}

/** @param {import("node:http").ServerResponse} response @param {unknown} value @param {number} [status] */
function json(response, value, status = 200) {
  response.writeHead(status, { "content-type": "application/json" }).end(JSON.stringify(value));
}
/** @param {string} id @param {object} [document] */
const document = (id, document = { value: 1 }) => ({ id, version, state: "value", document });
/** @param {string} id */
const write = id => ({ id, version, offset: "1", durability: "local_durable" });

describe("current document wire contracts", () => {
  test.each(["a/b", "a%2Fb", ".", "..", "%2E", "%", "?", "#", "+", " ", "é", "e\u0301", "😀", "a".repeat(256)])(
    "round-trips key %j exactly once through every point method", async key => {
      const { db, requests } = await fixture((request, response) => {
        const id = request.url.searchParams.get("id") ?? "";
        json(response, request.method === "GET" ? document(id, { id: "unrelated-data" }) : write(id));
      });
      const docs = db.collection("users");
      expect((await docs.get(key)).id).toBe(key);
      expect((await docs.put(key, { id: "unrelated-data" })).id).toBe(key);
      await docs.patch(key, { merge: { id: "unrelated-data" } });
      await docs.delete(key);
      expect(requests.map(request => request.method)).toEqual(["GET", "PUT", "PATCH", "DELETE"]);
      for (const request of requests) {
        expect(request.url.pathname).toBe(`${prefix}/collections/users/documents/by-key`);
        expect([...request.url.searchParams]).toEqual([["id", key]]);
        expect(request.headers.authorization).toBe(`Bearer ${token}`);
        expect(request.headers.accept).toBe("application/json");
        expect(request.headers["idempotency-key"]).toBeUndefined();
      }
      expect(JSON.parse(requests[1]?.body ?? "")).toEqual({ id: "unrelated-data" });
      expect(requests[2]?.headers["content-type"]).toBe("application/merge-patch+json");
    },
  );

  test("preserves stored PATCH data and exact integers in both response forms", async () => {
    const { db, requests } = await fixture((request, response) => {
      const data = `{"id":"customer-data","counter":9007199254740993}`;
      response.writeHead(200, { "content-type": "application/json" }).end(request.url.pathname.endsWith("/transactions")
        ? `{"offset":"2","durability":"local_durable","results":[{"id":"a/b","version":"${version}","document":${data}}]}`
        : `{"id":"a/b","version":"${version}","offset":"1","durability":"local_durable","document":${data}}`);
    });
    const patched = await db.collection("users").patch("a/b", { ops: [{ op: "set", path: "/id", value: "customer-data" }] });
    expect(patched.document?.id).toBe("customer-data");
    expect(patched.document?.counter).toBeInstanceOf(ExactDecimal);
    expect(String(patched.document?.counter)).toBe("9007199254740993");
    const result = await db.transaction().patch("users", "a/b", { merge: { id: "customer-data" } }).commit();
    const entry = result.results[0];
    expect(entry).toMatchObject({ id: "a/b", version, document: { id: "customer-data" } });
    if (entry === undefined || !("document" in entry)) throw new Error("Missing PATCH transaction result.");
    expect(entry.document?.counter).toBeInstanceOf(ExactDecimal);
    expect(String(entry.document?.counter)).toBe("9007199254740993");
    expect(requests[0]?.headers["content-type"]).toBe("application/vnd.terrabase.ops+json");
    expect(JSON.parse(requests[1]?.body ?? "").ops[0]).toMatchObject({ id: "a/b", merge: { id: "customer-data" } });
  });

  test.each([null, [], "invalid"])("rejects malformed optional PATCH document %j", async value => {
    const { db } = await fixture((_request, response) => json(response, { ...write("key"), document: value }));
    await expect(db.collection("users").patch("key", { merge: {} })).rejects.toThrow(TypeError);
  });

  test("preserves collection identities across all four named operations", async () => {
    const collection = { id: entityId, name: "users", created_at: "42" };
    const { db, requests } = await fixture((request, response) => json(response,
      request.method === "GET" && request.url.pathname.endsWith("/collections")
        ? { items: [collection], next_cursor: null } : collection,
      request.method === "POST" ? 201 : 200));
    const expected = { id: entityId, name: "users", createdAt: "42" };
    expect(await db.collections.create("users")).toEqual(expected);
    expect(await db.collections.get("users")).toEqual(expected);
    expect(await db.collections.list({ limit: 1, cursor: "opaque+cursor" })).toEqual({ items: [expected], nextCursor: null });
    expect(await db.collections.drop("users")).toEqual(expected);
    expect(requests.map(request => request.method)).toEqual(["POST", "GET", "GET", "DELETE"]);
    expect(requests[2]?.url.searchParams.get("cursor")).toBe("opaque+cursor");
  });

  test.each([undefined, "not-an-entity-id", entityId.toLowerCase()])("rejects malformed collection identity %j", async id => {
    const { db } = await fixture((_request, response) => json(response, { id, name: "users", created_at: "42" }));
    await expect(db.collections.get("users")).rejects.toThrow(TypeError);
  });

  test.each([version, "tbv2-token", `tbv1-${"0".repeat(32)}`, "*"])("quotes transaction ETag %j without changing wildcard semantics", async tag => {
    const { db, requests } = await fixture((_request, response) => json(response,
      { offset: "1", durability: "local_durable", results: [write("key")] }));
    await db.transaction().put("users", "key", { id: "unrelated-data" }, { ifMatch: tag }).commit();
    expect(JSON.parse(requests[0]?.body ?? "").ops[0].if_match).toBe(tag === "*" ? "*" : `"${tag}"`);
  });

  test("keeps client-generated IDs stable on caller retries, without adding replay semantics", async () => {
    const { db, requests } = await fixture((_request, response) => json(response, write(entityId)));
    const docs = db.collection("users");
    await docs.put(entityId, { value: 1 });
    await docs.put(entityId, { value: 1 });
    expect(requests.map(request => request.url.searchParams.get("id"))).toEqual([entityId, entityId]);
    expect(requests.map(request => request.body)).toEqual(['{"value":1}', '{"value":1}']);
  });
});

describe("pagination termination and cancellation", () => {
  test("follows opaque cursors, sends count once, and stops at null", async () => {
    const { db, requests } = await fixture((_request, response) => json(response,
      requests.length === 1 ? { items: [document("key")], next_cursor: "next+cursor", count: { value: "1", exact: true } }
        : { items: [], next_cursor: null }));
    const pages = [];
    for await (const page of db.collection("users").pages({ count: true, limit: 1 })) pages.push(page);
    expect(pages).toHaveLength(2);
    expect(requests.map(request => JSON.parse(request.body))).toEqual([{ count: true, limit: 1 }, { limit: 1, cursor: "next+cursor" }]);
  });

  test.each([["same", "same"], ["a", "b", "a"]])("rejects repeated/cyclic cursors %j", async (...cursors) => {
    let position = 0;
    const { db, requests } = await fixture((_request, response) => json(response, { items: [], next_cursor: cursors[position++] }));
    const iter = db.collection("users").pages();
    for (let i = 0; i < cursors.length - 1; i++) await iter.next();
    await expect(iter.next()).rejects.toThrow(/repeated pagination cursor/u);
    expect(requests).toHaveLength(cursors.length);
  });

  test("rejects a cursor returning to the caller's initial position", async () => {
    const { db, requests } = await fixture((_request, response) => json(response, { items: [], next_cursor: "initial" }));
    await expect(db.collection("users").pages({ cursor: "initial" }).next()).rejects.toThrow(/repeated pagination cursor/u);
    expect(requests).toHaveLength(1);
  });

  test.each([undefined, "", 1])("rejects malformed next_cursor %j", async next_cursor => {
    const { db } = await fixture((_request, response) => json(response, { items: [], next_cursor }));
    await expect(db.collection("users").query()).rejects.toThrow(TypeError);
  });

  test("consumer return issues no continuation request", async () => {
    const { db, requests } = await fixture((_request, response) => json(response, { items: [], next_cursor: "next" }));
    const iter = db.collection("users").pages();
    await iter.next();
    await iter.return();
    expect(requests).toHaveLength(1);
  });

  test("abort between buffered items preserves the reason and issues no next request", async () => {
    const { db, requests } = await fixture((_request, response) => json(response, { items: [document("a"), document("b")], next_cursor: "next" }));
    const controller = new AbortController();
    const reason = new Error("cancel buffered iteration");
    const iter = db.collection("users").items({}, { signal: controller.signal });
    expect((await iter.next()).value?.id).toBe("a");
    controller.abort(reason);
    await expect(iter.next()).rejects.toBe(reason);
    expect(requests).toHaveLength(1);
  });
});

describe("native fetch failures and typed diagnostics", () => {
  test.each([401, 403, 404, 412, 429, 502, 503, 504])("preserves error %i without retrying a mutation", async status => {
    const code = status === 401 ? "invalid_key" : status === 403 ? "forbidden_scope" : status === 412 ? "precondition_failed" : "server_code";
    const body = { error: { code, message: "Rule failed", op: 0, details: [{ path: "/field", keyword: "type" }] }, extension: "retained" };
    const { db, requests } = await fixture((_request, response) => {
      response.writeHead(status, { "content-type": "application/json", "x-request-id": "fixture-request", "retry-after": "2",
        "www-authenticate": "Bearer", etag: '"tombstone"' }).end(JSON.stringify(body));
    });
    const error = await db.collection("users").put("key", {}).catch(error => error);
    expect(error).toBeInstanceOf(TerraBaseV1Error);
    if (status === 401) expect(error).toBeInstanceOf(V1InvalidKeyError);
    if (status === 403) expect(error).toBeInstanceOf(V1ForbiddenScopeError);
    expect(error).toMatchObject({ status, code, op: 0, retryAfter: 2, requestId: "fixture-request", etag: "tombstone", body, details: body.error });
    expect(error.responseText).toBe(JSON.stringify(body));
    expect(error.headers.get("www-authenticate")).toBe("Bearer");
    expect(requests).toHaveLength(1);
  });

  test.each(["", "{", "not-json"])("preserves unreadable error text %j and headers", async text => {
    const { db } = await fixture((_request, response) => response.writeHead(403,
      { "content-type": "application/json", "x-request-id": "fixture-request" }).end(text));
    await expect(db.collection("users").get("key")).rejects.toMatchObject({ status: 403, code: "http_403",
      responseText: text, body: undefined, requestId: "fixture-request" });
  });

  test("retains unknown JSON errors without inventing a problem-details contract", async () => {
    const body = { type: "about:blank", title: "Denied", status: 403 };
    const { db } = await fixture((_request, response) => response.writeHead(403,
      { "content-type": "application/problem+json" }).end(JSON.stringify(body)));
    await expect(db.collection("users").get("key")).rejects.toMatchObject({ status: 403, code: "http_403", body, details: undefined });
  });

  test.each([{ text: "", type: "application/json", error: TypeError }, { text: "{", type: "application/json", error: SyntaxError },
    { text: "{}", type: "text/html", error: TypeError }, { text: "null", type: "application/json", error: TypeError }])(
    "rejects malformed success $type/$text", async ({ text, type, error }) => {
      const { db } = await fixture((_request, response) => response.writeHead(200, { "content-type": type }).end(text));
      await expect(db.collection("users").query()).rejects.toThrow(error);
    },
  );

  test("pre-abort retains the caller's reason with zero requests", async () => {
    const { db, requests } = await fixture((_request, response) => json(response, document("key")));
    const controller = new AbortController();
    const reason = new Error("pre-aborted");
    controller.abort(reason);
    await expect(db.collection("users").get("key", { signal: controller.signal })).rejects.toBe(reason);
    expect(requests).toHaveLength(0);
  });

  test.each([200, 403])("abort during a partial %i body retains the exact reason", async status => {
    const { baseUrl, requests } = await fixture((_request, response) => {
      response.writeHead(status, { "content-type": "application/json" }).write(status === 200 ? '{"items":[' : '{"error":');
    });
    const controller = new AbortController();
    const reason = new DOMException("abort partial body", "AbortError");
    const db = createTerraBaseV1Client({ project, token, baseUrl, fetch: async (input, init) => {
      const response = await globalThis.fetch(input, init);
      setImmediate(() => controller.abort(reason));
      return response;
    } }).database("app");
    await expect(db.collection("users").query({}, { signal: controller.signal })).rejects.toBe(reason);
    expect(requests).toHaveLength(1);
  });

  test("redirects never forward credentials or retry the request", async () => {
    const { db, requests } = await fixture((_request, response) => response.writeHead(307, { location: "/redirect-target" }).end());
    await expect(db.collection("users").put("key", {})).rejects.toMatchObject({ status: 307, code: "http_307" });
    expect(requests).toHaveLength(1);
  });
});

describe("current document listing", () => {
  test("uses GET documents with opaque cursor and terminates an empty final page", async () => {
    const { db, requests } = await fixture((request, response) => json(response, {
      items: request.url.searchParams.has("cursor") ? [] : [document("natural/key")],
      next_cursor: request.url.searchParams.has("cursor") ? null : "opaque+/cursor=",
    }));
    const docs = db.collection("users");
    const first = await docs.list({ limit: 1 });
    const last = await docs.list({ limit: 1, cursor: first.nextCursor ?? "" });
    expect(first.items[0]?.id).toBe("natural/key");
    expect(last).toMatchObject({ items: [], nextCursor: null });
    expect(requests.map(request => [request.method, request.url.pathname, request.body])).toEqual([
      ["GET", `${prefix}/collections/users/documents`, ""], ["GET", `${prefix}/collections/users/documents`, ""],
    ]);
    expect(requests[1]?.url.searchParams.get("cursor")).toBe("opaque+/cursor=");
  });

  test.each(["", "{", '{"items":[],"next_cursor":2}'])("rejects invalid list response %j", async body => {
    const { db } = await fixture((_request, response) => response.writeHead(200, { "content-type": "application/json" }).end(body));
    await expect(db.collection("users").list()).rejects.toThrow();
  });

  test("rejects invalid list bounds before HTTP", async () => {
    const { db, requests } = await fixture((_request, response) => json(response, {}));
    for (const request of [{ limit: 0 }, { limit: 1001 }, { cursor: "" }, { unknown: true }])
      await expect(db.collection("users").list(/** @type {any} */ (request))).rejects.toThrow(TypeError);
    expect(requests).toHaveLength(0);
  });

  test("preserves a denied list response without retries", async () => {
    const { db, requests } = await fixture((_request, response) => response.writeHead(403, {
      "content-type": "application/json", "x-request-id": "list-denied",
    }).end('{"error":{"code":"forbidden_scope","message":"Denied"}}'));
    await expect(db.collection("users").list()).rejects.toMatchObject({ status: 403, code: "forbidden_scope", requestId: "list-denied" });
    expect(requests).toHaveLength(1);
  });
});

test.each(["document", "collection", "schema", "database"])("does not replay bodyless %s DELETE after native fetch receives 421", async surface => {
  const { client, db, requests } = await fixture((_request, response) => json(response, { error: { code: "wrong_region" } }, 421));
  const docs = db.collection("users");
  const deletion = surface === "document" ? docs.delete("key") : surface === "collection" ? db.collections.drop("users") :
    surface === "schema" ? docs.deleteSchema() : client.databases.delete("app");
  await expect(deletion).rejects.toMatchObject({ status: 421, code: "wrong_region" });
  expect(requests).toHaveLength(1);
  expect(requests[0]?.body).toBe("");
});

describe("collection TTL", () => {
  test("putTtl, getTtl and deleteTtl send the documented method, path and body", async () => {
    const { db, requests } = await fixture((request, response) =>
      json(response, { path: request.method === "PUT" ? JSON.parse(request.body).path : request.method === "GET" ? "/expiresAt" : null }));
    const users = db.collection("users");
    expect(await users.putTtl("/expiresAt")).toEqual({ path: "/expiresAt" });
    expect(await users.getTtl()).toEqual({ path: "/expiresAt" });
    expect(await users.deleteTtl()).toEqual({ path: null });
    expect(requests.map(request => [request.method, request.url.pathname, request.body, request.headers["content-type"]])).toEqual([
      ["PUT", `${prefix}/collections/users/ttl`, '{"path":"/expiresAt"}', "application/json"],
      ["GET", `${prefix}/collections/users/ttl`, "", undefined],
      ["DELETE", `${prefix}/collections/users/ttl`, "", undefined],
    ]);
  });

  test.each(["", "expiresAt", `/${Array(17).fill("a").join("/")}`])("rejects TTL path %j before HTTP", async path => {
    const { db, requests } = await fixture((_request, response) => json(response, { path }));
    await expect(db.collection("users").putTtl(path)).rejects.toThrow(TypeError);
    expect(requests).toHaveLength(0);
  });
});

describe("vector near sort", () => {
  test("sends the exact near query and decodes distance", async () => {
    const { db, requests } = await fixture((_request, response) => response.writeHead(200, { "content-type": "application/json" })
      .end(`{"items":[${JSON.stringify(document("a")).slice(0, -1)},"distance":0.125},${JSON.stringify(document("b"))}],"next_cursor":null}`));
    const page = await db.collection("docs").query({
      filter: { eq: ["/kind", "doc"] },
      sort: [{ near: { path: "/embedding", vector: [0.1, 0.2], metric: "cosine" } }],
      limit: 10,
    });
    expect(requests[0]?.body).toBe('{"filter":{"eq":["/kind","doc"]},"sort":[{"near":{"path":"/embedding","vector":[0.1,0.2],"metric":"cosine"}}],"limit":10}');
    expect(page.items[0]).toEqual({ ...document("a"), distance: 0.125 });
    expect(page.items[1]).not.toHaveProperty("distance");
    expect(page.nextCursor).toBeNull();
  });

  test.each([
    { sort: [{ near: { path: "/e", vector: [1] } }, { path: "/a" }] },
    { sort: [{ near: { path: "/e", vector: [1] }, order: "asc" }] },
    { sort: [{ near: { path: "/e", vector: [1] } }], limit: 101 },
    { sort: [{ near: { path: "/e", vector: [1] } }], cursor: "next" },
    { sort: [{ near: { path: "/e", vector: [] } }] },
    { sort: [{ near: { path: "/e", vector: [1e39] } }] },
    { sort: [{ near: { path: "/e", vector: [Number.NaN] } }] },
    { sort: [{ near: { path: "/e", vector: [0, 0] } }] },
    { sort: [{ near: { path: "/e", vector: [1], metric: "dot" } }] },
  ])("rejects invalid near query %j before HTTP", async query => {
    const { db, requests } = await fixture((_request, response) => json(response, { items: [], next_cursor: null }));
    await expect(db.collection("docs").query(/** @type {any} */ (query))).rejects.toThrow(TypeError);
    expect(requests).toHaveLength(0);
  });

  test("an all-zero l2 vector is allowed", async () => {
    const { db, requests } = await fixture((_request, response) => json(response, { items: [], next_cursor: null }));
    await db.collection("docs").query({ sort: [{ near: { path: "/e", vector: [0, 0], metric: "l2" } }] });
    expect(requests).toHaveLength(1);
  });
});
