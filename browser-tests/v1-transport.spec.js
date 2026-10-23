import { createServer } from "node:http";
import { expect, test } from "@playwright/test";

// Native browser fetch against loopback HTTP fixtures; not producer qualification.
const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
/** @type {import("node:http").Server} */
let server;
let baseUrl = "";
/** @type {{ method: string | undefined; path: string; key: string | null; body: string; authorization: string | undefined; lastEventId: string | undefined; contentType: string | undefined }[]} */
let requests = [];
let mutationEffects = 0;

test.beforeAll(async () => {
  server = createServer((request, response) => {
    response.setHeader("access-control-allow-origin", "http://127.0.0.1:4173");
    response.setHeader("access-control-allow-methods", "GET, POST, PUT, PATCH, DELETE");
    response.setHeader("access-control-allow-headers", "authorization, content-type, last-event-id");
    response.setHeader("access-control-expose-headers", "x-request-id, retry-after");
    if (request.method === "OPTIONS") { response.writeHead(204).end(); return; }
    void (async () => {
      let body = "";
      for await (const chunk of request) body += chunk.toString();
      const url = new URL(request.url ?? "/", "http://localhost");
      requests.push({ method: request.method, path: url.pathname, key: url.searchParams.get("id"), body, authorization: request.headers.authorization, lastEventId: request.headers["last-event-id"]?.toString(), contentType: request.headers["content-type"] });
      response.setHeader("content-type", "application/json");
      if (url.pathname.includes("/misdirected/")) {
        response.writeHead(421).end('{"error":{"code":"wrong_region","message":"Wrong region"}}');
      } else if (url.pathname.endsWith("/changes")) {
        response.setHeader("content-type", "text/event-stream");
        if (request.headers["last-event-id"] === undefined) response.end('event: cursor\nid: AQ\ndata: {"cursor":"AQ"}\n\n');
        else response.end('event: change\nid: AQEjRWeJq83vACAAAAAAAAE\ndata: {"collection":"users","id":"natural/key","op":"upsert","version":"9007199254740993","origin":"0123456789abcdef","document":{"value":1}}\n\n');
      } else if (url.pathname.endsWith("/sql/check") || url.pathname.endsWith("/sql/explain")) {
        response.end(JSON.stringify({ ok: true, error: null, collection: "users", hasLimit: true, plan: ["scan"], indexes: [], suggestions: [] }));
      } else if (url.pathname.endsWith("/sql")) {
        response.end('{"matched":1,"truncated":false,"rows":[{"id":"natural/key","value":{"count":9007199254740993}}]}');
      } else if (url.pathname.endsWith("/schema")) {
        response.end(JSON.stringify({ schema: request.method === "PUT" ? JSON.parse(body) : null }));
      } else if (url.pathname.endsWith("/documents") && request.method === "GET") {
        response.end('{"items":[],"next_cursor":null}');
      } else if (url.pathname.includes("/unauthorized/")) {
        response.writeHead(401).end('{"error":{"code":"invalid_key","message":"Expired"}}');
      } else if (url.pathname.includes("/denied/")) {
        response.setHeader("x-request-id", "browser-fixture-request");
        response.writeHead(403).end('{"error":{"code":"forbidden_scope","message":"Denied","details":[{"path":"/value"}]},"extension":"kept"}');
      } else if (url.pathname.includes("/partial")) {
        const denied = url.pathname.includes("/partial_denied/");
        response.writeHead(denied ? 403 : 200).write(denied ? '{"error":' : '{"items":[');
      } else if (url.pathname.includes("/empty/")) {
        response.end();
      } else if (url.pathname.includes("/malformed/")) {
        response.end("{");
      } else if (url.pathname.endsWith("/query")) {
        const query = JSON.parse(body);
        response.end(JSON.stringify({ items: ["a", "b"].map(id => ({ id, version: "opaque", state: "value", document: {} })),
          next_cursor: url.pathname.includes("/cycle/") ? "same" : query.cursor === undefined ? "next+cursor" : null }));
      } else {
        if (request.method === "PATCH") mutationEffects++;
        const id = url.searchParams.get("id");
        response.end(JSON.stringify(request.method === "GET" ? { id, version: "opaque", state: "value", document: { id: "customer-data" } }
          : { id, version: "opaque", offset: "1", durability: "local_durable", document: { id: "customer-data" } }));
      }
    })().catch(error => response.destroy(error));
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => resolve(undefined));
  });
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("Expected loopback address.");
  baseUrl = `http://127.0.0.1:${address.port}`;
});

test.beforeEach(async ({ page }) => {
  requests = [];
  mutationEffects = 0;
  await page.goto("/browser-tests/fixture.html");
});

test.afterAll(async () => {
  server.closeAllConnections();
  await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve(undefined)));
});

test("packed browser uses native fetch and preserves query keys and PATCH data", async ({ page }) => {
  const result = await page.evaluate(async ({ baseUrl, project }) => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    const docs = sdk.createTerraBaseV1Client({ project, token: "fixture.payload.signature", baseUrl }).database("app").collection("users");
    const keys = ["a/b", "a%2Fb", ".", "..", "+", " ", "é", "e\u0301", "😀"];
    for (const key of keys) {
      const read = await docs.get(key);
      if (read.id !== key || read.document.id !== "customer-data") throw new Error("Key/data changed.");
    }
    const patched = await docs.patch(keys[0] ?? "", { ops: [
      { op: "inc", path: "/count", by: 1 }, { op: "add", path: "/tags", value: "new" }, { op: "remove", path: "/tags", value: "old" },
    ] });
    return { keys, document: patched.document };
  }, { baseUrl, project });
  expect(requests.slice(0, -1).map(request => request.key)).toEqual(result.keys);
  expect(requests.every(request => request.path.endsWith("/documents/by-key") && request.authorization === "Bearer fixture.payload.signature")).toBe(true);
  expect(result.document).toEqual({ id: "customer-data" });
  expect(requests.at(-1)?.contentType).toBe("application/vnd.terrabase.ops+json");
  expect(JSON.parse(requests.at(-1)?.body ?? "{}").ops.map(/** @param {{ op: string }} op */ op => op.op)).toEqual(["inc", "add", "remove"]);
});

test("packed browser preserves typed denied errors and exposed diagnostic headers", async ({ page }) => {
  const result = await page.evaluate(async ({ baseUrl, project }) => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    const docs = sdk.createTerraBaseV1Client({ project, token: "fixture.payload.signature", baseUrl }).database("app").collection("denied");
    try { await docs.get("key"); throw new Error("Expected denial."); }
    catch (error) {
      if (!(error instanceof sdk.V1ForbiddenScopeError)) throw error;
      return { status: error.status, code: error.code, requestId: error.requestId, body: error.body, text: error.responseText };
    }
  }, { baseUrl, project });
  expect(result).toMatchObject({ status: 403, code: "forbidden_scope", requestId: "browser-fixture-request",
    body: { error: { details: [{ path: "/value" }] }, extension: "kept" } });
  expect(result.text).toContain('"extension":"kept"');
  expect(requests).toHaveLength(1);
});

test("packed browser terminates pagination, rejects cycles, and cancels buffered items", async ({ page }) => {
  const result = await page.evaluate(async ({ baseUrl, project }) => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    const db = sdk.createTerraBaseV1Client({ project, token: "fixture.payload.signature", baseUrl }).database("app");
    let pages = 0;
    for await (const _page of db.collection("users").pages({ count: true })) pages++;
    const cycle = db.collection("cycle").pages();
    await cycle.next();
    let cyclic = false;
    try { await cycle.next(); } catch (error) { cyclic = error instanceof TypeError; }
    const controller = new AbortController();
    const reason = new Error("cancel buffered browser item");
    const items = db.collection("users").items({}, { signal: controller.signal });
    await items.next();
    controller.abort(reason);
    let cancelled = false;
    try { await items.next(); } catch (error) { cancelled = error === reason; }
    return { pages, cyclic, cancelled };
  }, { baseUrl, project });
  expect(result).toEqual({ pages: 2, cyclic: true, cancelled: true });
  expect(requests).toHaveLength(5);
  expect(requests.slice(0, 2).map(request => JSON.parse(request.body))).toEqual([{ count: true }, { cursor: "next+cursor" }]);
});

test("packed browser retains AbortSignal reasons before fetch and during partial bodies", async ({ page }) => {
  const result = await page.evaluate(async ({ baseUrl, project }) => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    const pre = new AbortController();
    const preReason = new Error("pre-abort browser");
    pre.abort(preReason);
    const docs = sdk.createTerraBaseV1Client({ project, token: "fixture.payload.signature", baseUrl }).database("app").collection("users");
    let preCancelled = false;
    try { await docs.query({}, { signal: pre.signal }); } catch (error) { preCancelled = error === preReason; }
    const cancelled = [];
    for (const collection of ["partial", "partial_denied"]) {
      const controller = new AbortController();
      const reason = new DOMException("abort partial browser body", "AbortError");
      const db = sdk.createTerraBaseV1Client({ project, token: "fixture.payload.signature", baseUrl, fetch: async (input, init) => {
        const response = await globalThis.fetch(input, init);
        setTimeout(() => controller.abort(reason), 0);
        return response;
      } }).database("app");
      try { await db.collection(collection).query({}, { signal: controller.signal }); cancelled.push(false); }
      catch (error) { cancelled.push(error === reason); }
    }
    return { preCancelled, cancelled };
  }, { baseUrl, project });
  expect(result).toEqual({ preCancelled: true, cancelled: [true, true] });
  expect(requests).toHaveLength(2);
});

test("packed browser rejects empty and malformed successful responses", async ({ page }) => {
  const result = await page.evaluate(async ({ baseUrl, project }) => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    const db = sdk.createTerraBaseV1Client({ project, token: "fixture.payload.signature", baseUrl }).database("app");
    const names = [];
    for (const collection of ["empty", "malformed"]) {
      try { await db.collection(collection).query(); names.push("unexpected-success"); }
      catch (error) { names.push(error instanceof Error ? error.name : "unexpected-error"); }
    }
    return names;
  }, { baseUrl, project });
  expect(result).toEqual(["TypeError", "SyntaxError"]);
  expect(requests).toHaveLength(2);
});

test("packed browser exposes read-only SQL, document listing and declared schemas", async ({ page }) => {
  const result = await page.evaluate(async ({ baseUrl, project }) => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    const db = sdk.createTerraBaseV1Client({ project, token: "fixture.payload.signature", baseUrl }).database("app");
    const sql = "SELECT * FROM users LIMIT 1";
    const rows = await db.sql(sql);
    const checked = await db.checkSql(sql);
    const explained = await db.explainSql(sql);
    const docs = db.collection("users");
    const page = await docs.list({ limit: 1 });
    const attached = await docs.putSchema(false);
    const detached = await docs.deleteSchema();
    return { count: String(rows.rows[0]?.value.count), checked: checked.ok, explained: explained.ok,
      next: page.nextCursor, attached: attached.schema, detached: detached.schema };
  }, { baseUrl, project });
  expect(result).toEqual({ count: "9007199254740993", checked: true, explained: true, next: null, attached: false, detached: null });
  expect(requests.map(request => request.method)).toEqual(["POST", "POST", "POST", "GET", "PUT", "DELETE"]);
});

test("packed browser resumes SSE with latest cursor and bearer token and closes the iterator", async ({ page }) => {
  const result = await page.evaluate(async ({ baseUrl, project }) => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    let token = "fixture.payload.signature";
    const db = sdk.createTerraBaseClient({ project, token: async () => token, baseUrl }).database("app");
    const stream = db.changes({ cursor: "now", collections: ["users"] });
    const initial = await stream.next();
    token = "replacement.payload.signature";
    const change = await stream.next();
    const closed = await stream.return();
    return { initial: initial.value, change: change.value, closed: closed.done };
  }, { baseUrl, project });
  expect(result).toMatchObject({ initial: { type: "cursor", cursor: "AQ" }, change: { type: "change", id: "natural/key", version: "9007199254740993" }, closed: true });
  expect(requests).toHaveLength(2);
  expect(requests.map(request => request.lastEventId)).toEqual([undefined, "AQ"]);
  expect(requests.map(request => request.authorization)).toEqual(["Bearer fixture.payload.signature", "Bearer replacement.payload.signature"]);
});

test("packed browser resolves sync and async providers for each read, write and query", async ({ page }) => {
  await page.evaluate(async ({ baseUrl, project }) => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    for (const asynchronous of [false, true]) {
      let current = "fixture.payload.signature";
      const token = () => asynchronous ? Promise.resolve(current) : current;
      const docs = sdk.createTerraBaseClient({ project, token, baseUrl }).database("app").collection("users");
      await docs.get("01ARZ3NDEKTSV4RRFFQ69G5FA2");
      current = "replacement.payload.signature";
      await docs.put("01ARZ3NDEKTSV4RRFFQ69G5FA2", { value: 1 });
      await docs.query();
    }
  }, { baseUrl, project });
  expect(requests.map(request => request.authorization)).toEqual(Array(2).fill([
    "Bearer fixture.payload.signature", "Bearer replacement.payload.signature", "Bearer replacement.payload.signature",
  ]).flat());
  expect(requests.map(request => request.method)).toEqual(["GET", "PUT", "POST", "GET", "PUT", "POST"]);
});

test("packed browser rejects invalid or failing providers without requests or credential leakage", async ({ page }) => {
  const result = await page.evaluate(async ({ baseUrl, project }) => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    const errors = [];
    for (const token of [
      () => "invalid token-canary",
      async () => "a.b.c\r\nx-secret: token-canary",
      /** @type {import('../src/index.js').V1TokenProvider} */ (/** @type {unknown} */ (async () => undefined)),
      () => { throw new Error("sync token-canary"); },
      async () => { throw new Error("async token-canary"); },
    ]) {
      const docs = sdk.createTerraBaseClient({ project, token, baseUrl }).database("app").collection("users");
      try { await docs.put("01ARZ3NDEKTSV4RRFFQ69G5FA2", {}); throw new Error("Unexpected success."); }
      catch (error) {
        if (!(error instanceof Error)) throw error;
        errors.push({ message: error.message, leaked: `${error.stack}${JSON.stringify(error)}`.includes("token-canary"), cause: error.cause !== undefined });
      }
    }
    return errors;
  }, { baseUrl, project });
  expect(result.map(error => error.message)).toEqual([
    ...Array(3).fill("token must be a compact access JWT or a legacy C# tb_ API key."),
    ...Array(2).fill("TerraBase token provider failed."),
  ]);
  expect(result.every(error => !error.leaked && !error.cause)).toBe(true);
  expect(requests).toHaveLength(0);
});

test("packed browser cancels pending token resolution before native fetch", async ({ page }) => {
  const result = await page.evaluate(async ({ baseUrl, project }) => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    /** @type {(value: string) => void} */
    let finish = () => {};
    /** @type {() => void} */
    let invoked = () => {};
    const started = new Promise(resolve => { invoked = () => resolve(undefined); });
    const pending = new Promise(resolve => { finish = resolve; });
    const token = () => { invoked(); return /** @type {Promise<string>} */ (pending); };
    const docs = sdk.createTerraBaseClient({ project, token, baseUrl }).database("app").collection("users");
    const controller = new AbortController();
    const reason = new Error("cancel pending browser token");
    const read = docs.get("01ARZ3NDEKTSV4RRFFQ69G5FA2", { signal: controller.signal });
    await started;
    controller.abort(reason);
    let cancelled = false;
    try { await read; } catch (error) { cancelled = error === reason; }
    finish("fixture.payload.signature");
    await new Promise(resolve => setTimeout(resolve, 0));
    return cancelled;
  }, { baseUrl, project });
  expect(result).toBe(true);
  expect(requests).toHaveLength(0);
});

test("packed browser never refreshes or replays mutations after 401 or 403", async ({ page }) => {
  const result = await page.evaluate(async ({ baseUrl, project }) => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    const errors = [];
    for (const collection of ["unauthorized", "denied"]) {
      let calls = 0;
      const token = async () => { calls++; return "fixture.payload.signature"; };
      const docs = sdk.createTerraBaseClient({ project, token, baseUrl }).database("app").collection(collection);
      try { await docs.patch("01ARZ3NDEKTSV4RRFFQ69G5FA2", { ops: [{ op: "inc", path: "/count", by: 1 }] }); throw new Error("Unexpected success."); }
      catch (error) {
        if (!(error instanceof sdk.TerraBaseV1Error)) throw error;
        errors.push({ status: error.status, calls });
      }
    }
    return errors;
  }, { baseUrl, project });
  expect(result).toEqual([{ status: 401, calls: 1 }, { status: 403, calls: 1 }]);
  expect(requests.map(request => request.method)).toEqual(["PATCH", "PATCH"]);
  expect(mutationEffects).toBe(0);
});

test("packed browser requires one wire PATCH despite native 421 retransmission", async ({ page }) => {
  const result = await page.evaluate(async ({ baseUrl, project }) => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    let fetchCalls = 0;
    const docs = sdk.createTerraBaseV1Client({ project, token: "fixture.payload.signature", baseUrl,
      fetch: (input, init) => { fetchCalls++; return globalThis.fetch(input, init); },
    }).database("app").collection("misdirected");
    try { await docs.patch("key", { ops: [{ op: "inc", path: "/count", by: 1 }] }); throw new Error("Expected 421."); }
    catch (error) { if (!(error instanceof sdk.TerraBaseV1Error)) throw error; return { code: error.code, fetchCalls }; }
  }, { baseUrl, project });
  expect(result).toEqual({ code: "wrong_region", fetchCalls: 1 });
  expect(mutationEffects).toBe(0);
  expect(requests).toHaveLength(1);
});

test("native browser request streams reject HTTP/1 instead of providing a portable mitigation", async ({ page }) => {
  const result = await page.evaluate(async ({ baseUrl, project }) => {
    const bytes = new TextEncoder().encode('{"ops":[{"op":"inc","path":"/count","by":1}]}');
    /** @type {RequestInit & { duplex: "half" }} */
    const init = { method: "PATCH", redirect: "manual", duplex: "half",
      headers: { authorization: "Bearer fixture.payload.signature", "content-type": "application/vnd.terrabase.ops+json" },
      body: new ReadableStream({ start(controller) { controller.enqueue(bytes); controller.close(); } }),
    };
    try { await globalThis.fetch(`${baseUrl}/v1/projects/${project}/databases/app/collections/misdirected/documents/by-key?id=key`, init); return "unexpected-success"; }
    catch (error) { return error instanceof Error ? error.name : "unexpected-error"; }
  }, { baseUrl, project });
  expect(result).toBe("TypeError");
  expect(requests).toHaveLength(0);
  expect(mutationEffects).toBe(0);
});

test("packed browser requires one wire DELETE despite native 421 retransmission", async ({ page }) => {
  const result = await page.evaluate(async ({ baseUrl, project }) => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    let fetchCalls = 0;
    const docs = sdk.createTerraBaseV1Client({ project, token: "fixture.payload.signature", baseUrl,
      fetch: (input, init) => { fetchCalls++; return globalThis.fetch(input, init); },
    }).database("app").collection("misdirected");
    try { await docs.delete("key"); throw new Error("Expected 421."); }
    catch (error) { if (!(error instanceof sdk.TerraBaseV1Error)) throw error; return { code: error.code, fetchCalls }; }
  }, { baseUrl, project });
  expect(result).toEqual({ code: "wrong_region", fetchCalls: 1 });
  expect(mutationEffects).toBe(0);
  expect(requests).toHaveLength(1);
});
