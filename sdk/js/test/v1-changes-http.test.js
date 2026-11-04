import { createServer } from "node:http";
import { afterEach, describe, expect, test } from "vitest";
import { createTerraBaseV1Client, ExactDecimal, TerraBaseV1Error, V1ForbiddenScopeError, V1InvalidKeyError } from "../src/index.js";

// Native loopback HTTP verifies the SDK stream, not a C# producer qualification.
const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
const token = "fixture.payload.signature";
const path = `/v1/projects/${project}/databases/app/changes`;
// ChangeCursor.cs format 1: AQ has no origins; the second vector holds the change below.
const emptyCursor = "AQ";
const committedCursor = "AQEjRWeJq83vACAAAAAAAAE";
const advancedCursor = "AQEjRWeJq83vACAAAAAAAAI";
/** @type {import("node:http").Server[]} */
const servers = [];

afterEach(async () => {
  for (const server of servers.splice(0)) {
    server.closeAllConnections();
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve(undefined)));
  }
});

/** @typedef {{ url: URL; headers: import("node:http").IncomingHttpHeaders }} WireRequest */
/** @param {(request: WireRequest, response: import("node:http").ServerResponse) => void} handle */
async function fixture(handle) {
  /** @type {WireRequest[]} */
  const requests = [];
  const server = createServer((request, response) => {
    const wire = { url: new URL(request.url ?? "/", "http://localhost"), headers: request.headers };
    requests.push(wire);
    handle(wire, response);
  });
  servers.push(server);
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(0, "127.0.0.1", () => resolve(undefined)); });
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("Expected loopback address.");
  const baseUrl = `http://127.0.0.1:${address.port}`;
  return { requests, baseUrl, db: createTerraBaseV1Client({ project, token, baseUrl }).database("app") };
}

/** @param {string} type @param {unknown} data @param {string | undefined} [id] */
const event = (type, data, id) => `event: ${type}\n${id === undefined ? "" : `id: ${id}\n`}data: ${JSON.stringify(data)}\n\n`;
/** @param {string} value */
const cursor = value => event("cursor", { cursor: value }, value);
const change = { collection: "users", id: "a/b", op: "upsert", version: "9007199254740993", origin: "0123456789abcdef", document: { title: "é😀" } };
/** @param {import("node:http").ServerResponse} response @param {string | Uint8Array} value @param {boolean} [end] */
function sse(response, value, end = false) {
  response.writeHead(200, { "content-type": "text/event-stream; charset=utf-8", "x-request-id": "stream-request" });
  if (end) response.end(value); else response.write(value);
}

describe("current database SSE contract", () => {
  test("resolves the latest bearer token on reconnect while retaining the cursor", async () => {
    const { baseUrl, requests } = await fixture((_request, response) =>
      sse(response, cursor(requests.length === 1 ? committedCursor : advancedCursor), true));
    let current = token;
    let calls = 0;
    const db = createTerraBaseV1Client({ project, baseUrl, token: async () => { calls++; return current; } }).database("app");
    const stream = db.changes();
    expect((await stream.next()).value).toEqual({ type: "cursor", cursor: committedCursor });
    current = "replacement.payload.signature";
    expect((await stream.next()).value).toEqual({ type: "cursor", cursor: advancedCursor });
    await stream.return();
    expect(requests.map(request => request.headers.authorization)).toEqual([`Bearer ${token}`, `Bearer ${current}`]);
    expect(requests.map(request => request.headers["last-event-id"])).toEqual([undefined, committedCursor]);
    expect(calls).toBe(2);
  });

  test.each([false, true])("a failed token refresh stops reconnect before HTTP (reject: %s)", async reject => {
    const { baseUrl, requests } = await fixture((_request, response) => sse(response, cursor(committedCursor), true));
    let calls = 0;
    const db = createTerraBaseV1Client({ project, baseUrl, token: async () => {
      if (++calls === 1) return token;
      if (reject) throw new Error("refresh token-canary");
      return "invalid token-canary";
    } }).database("app");
    const stream = db.changes();
    await stream.next();
    await expect(stream.next()).rejects.toThrow(reject ? "TerraBase token provider failed." : "token must be");
    expect(requests).toHaveLength(1);
    expect(calls).toBe(2);
  });

  test("resumes before an interrupted transaction until its final change issues a cursor", async () => {
    const finalChange = { ...change, id: "second" };
    const { db, requests } = await fixture((_request, response) => {
      if (requests.length === 1) sse(response, cursor(committedCursor) + event("change", change), true);
      else sse(response, event("change", change) + event("change", finalChange, advancedCursor));
    });
    const iter = db.changes();
    expect((await iter.next()).value).toEqual({ type: "cursor", cursor: committedCursor });
    const intermediate = { type: "change", cursor: undefined, ...change };
    expect((await iter.next()).value).toEqual(intermediate);
    expect((await iter.next()).value).toEqual(intermediate);
    expect(requests.map(request => request.headers["last-event-id"])).toEqual([undefined, committedCursor]);
    expect((await iter.next()).value).toEqual({ type: "change", cursor: advancedCursor, ...finalChange });
    await iter.return();
    expect(requests).toHaveLength(2);
  });

  test("normal EOF reconnects with the latest Last-Event-ID and identical snapshotted filter", async () => {
    const advancedChange = { ...change, version: "9007199254740994" };
    const { db, requests } = await fixture((_request, response) => {
      if (requests.length === 1) sse(response, cursor(committedCursor), true);
      else sse(response, event("change", advancedChange, advancedCursor));
    });
    const collections = ["users", "orders"];
    const iter = db.changes({ cursor: "now", lastEventId: emptyCursor, collections });
    expect((await iter.next()).value).toEqual({ type: "cursor", cursor: committedCursor });
    collections.push("later");
    expect((await iter.next()).value).toEqual({ type: "change", cursor: advancedCursor, ...advancedChange });
    await iter.return();
    expect(requests).toHaveLength(2);
    for (const request of requests) {
      expect(request.url.pathname).toBe(path);
      expect([...request.url.searchParams]).toEqual([["cursor", "now"], ["collections", "users,orders"]]);
      expect(request.headers.authorization).toBe(`Bearer ${token}`);
      expect(request.headers.accept).toBe("text/event-stream");
    }
    expect(requests.map(request => request.headers["last-event-id"])).toEqual([emptyCursor, committedCursor]);
  });

  test("at-least-once repeated events reach the caller, including nullable delete data", async () => {
    const deleted = { ...change, op: "delete", document: null };
    const { db, requests } = await fixture((_request, response) => sse(response,
      event("change", change, "same") + event("change", change, "same") + event("change", deleted, "next")));
    const iter = db.changes();
    const first = (await iter.next()).value;
    expect((await iter.next()).value).toEqual(first);
    expect((await iter.next()).value).toEqual({ type: "change", cursor: "next", ...deleted });
    await iter.return();
    expect(requests).toHaveLength(1);
  });

  test("decodes BOM, split UTF-8/CRLF, comments and multiline data through native HTTP", async () => {
    const text = '\ufeff: keepalive\r\nevent: change\r\nid: opaque\r\nunknown: ignored\r\ndata: {"collection":"users","id":"a/b","op":"upsert",\r\ndata: "version":"9007199254740993","origin":"0123456789abcdef","document":{"title":"é😀"}}\r\n\r\n';
    const { baseUrl, requests } = await fixture((_request, response) => sse(response, text));
    const db = createTerraBaseV1Client({ project, token, baseUrl, fetch: async (input, init) => {
      const response = await globalThis.fetch(input, init);
      if (response.body === null) throw new Error("Expected HTTP stream.");
      // Split real received bytes; never manufacture a service response.
      const body = response.body.pipeThrough(new TransformStream({ transform(chunk, controller) {
        for (let i = 0; i < chunk.byteLength; i++) controller.enqueue(chunk.subarray(i, i + 1));
      } }));
      return new Response(body, { status: response.status, headers: response.headers });
    } }).database("app");
    const iter = db.changes();
    expect((await iter.next()).value).toEqual({ type: "change", cursor: "opaque", ...change });
    await iter.return();
    expect(requests).toHaveLength(1);
  });

  test("decodes a document containing an exact integer", async () => {
    const text = 'event: change\nid: exact\ndata: {"collection":"users","id":"key","op":"upsert","version":"1","origin":"0000000000000001","document":{"counter":9007199254740993}}\n\n';
    const { db } = await fixture((_request, response) => sse(response, text));
    const iter = db.changes();
    const value = (await iter.next()).value;
    expect(value?.type).toBe("change");
    if (value?.type !== "change") throw new Error("Expected change event.");
    expect(value.document?.counter).toBeInstanceOf(ExactDecimal);
    expect(String(value.document?.counter)).toBe("9007199254740993");
    await iter.return();
  });

  test("normal empty streams fail even when the caller supplied a resume cursor", async () => {
    const { db, requests } = await fixture((_request, response) => sse(response, ": heartbeat\n\n", true));
    await expect(db.changes({ lastEventId: "previous" }).next()).rejects.toThrow(/empty changes stream/u);
    expect(requests).toHaveLength(1);
  });

  test.each([
    {}, { cursor: "" }, { cursor: "white space" }, { lastEventId: "line\r\nbreak" }, { collections: [] },
    { collections: ["Users"] }, { collections: Array.from({ length: 65 }, () => "users") }, { unknown: "value" },
  ])("validates request %j before HTTP", async request => {
    const { db, requests } = await fixture((_request, response) => sse(response, cursor("never")));
    if (Object.keys(request).length === 0) {
      const iter = db.changes(request);
      await iter.next(); await iter.return();
      expect(requests[0]?.url.search).toBe("");
    } else {
      await expect(db.changes(request).next()).rejects.toThrow(TypeError);
      expect(requests).toHaveLength(0);
    }
  });
});

describe("SSE failures preserve errors without reconnecting", () => {
  test.each([[401, "invalid_key"], [403, "forbidden_scope"], [410, "cursor_expired"], [429, "changes_busy"]])(
    "returns %i %s with HTTP diagnostics", async (status, code) => {
      const body = { error: { code, message: "Rule failed" } };
      const { db, requests } = await fixture((_request, response) => response.writeHead(status,
        { "content-type": "application/json", "retry-after": "1", "x-request-id": "denied-request" }).end(JSON.stringify(body)));
      const error = await db.changes({ cursor: "previous" }).next().catch(error => error);
      expect(error).toBeInstanceOf(TerraBaseV1Error);
      if (status === 401) expect(error).toBeInstanceOf(V1InvalidKeyError);
      if (status === 403) expect(error).toBeInstanceOf(V1ForbiddenScopeError);
      expect(error).toMatchObject({ status, code, requestId: "denied-request", retryAfter: 1, body });
      expect(requests).toHaveLength(1);
    },
  );

  test("returns issuer-routing metadata without another SDK fetch", async () => {
    const { baseUrl } = await fixture((_request, response) => response.writeHead(421,
      { "content-type": "application/json", "ts-replay": "region=sjc" })
      .end(JSON.stringify({ error: { code: "cursor_elsewhere" } })));
    let calls = 0;
    const db = createTerraBaseV1Client({ project, token, baseUrl, fetch: (input, init) => {
      calls++;
      // Native fetch may retry a bodyless GET on 421 within this single invocation.
      return globalThis.fetch(input, init);
    } }).database("app");
    const error = await db.changes({ cursor: "previous" }).next().catch(error => error);
    expect(error).toBeInstanceOf(TerraBaseV1Error);
    expect(error).toMatchObject({ status: 421, code: "cursor_elsewhere" });
    expect(error.headers.get("ts-replay")).toBe("region=sjc");
    expect(calls).toBe(1);
  });

  test.each(["cursor_expired", "unknown_database", "tenant_memory", "storage_busy"])("returns terminal %s with the actual stream status", async code => {
    const body = { error: { code, message: "Terminal stream failure" } };
    const { db, requests } = await fixture((_request, response) => sse(response, cursor("head") + event("error", body), true));
    const iter = db.changes();
    await iter.next();
    const error = await iter.next().catch(error => error);
    expect(error).toBeInstanceOf(TerraBaseV1Error);
    expect(error).toMatchObject({ status: 200, code, body, requestId: "stream-request" });
    expect(error.responseText).toBe(JSON.stringify(body));
    expect(requests).toHaveLength(1);
  });

  test.each([
    "", 'event: cursor\nid: head\ndata: {"cursor":"head"}\n',
    'event: cursor\nid: head\ndata: {"cursor":"head"}',
    'event: cursor\nid: head\ndata: {"cursor":"other"}\n\n',
    'event: cursor\ndata: {"cursor":"head"}\n\n',
    'event: cursor\nid: head\ndata: {\n\n',
    'event: unknown\nid: head\ndata: {}\n\n',
    'event: error\ndata: {"error":{}}\n\n',
    event("change", { ...change, op: "patch" }, "head"),
    event("change", { ...change, version: 1 }, "head"),
    event("change", { ...change, version: "18446744073709551616" }, "head"),
    event("change", { ...change, origin: "ABCDEF0123456789" }, "head"),
    event("change", { ...change, document: undefined }, "head"),
  ])("rejects malformed or incomplete SSE %j", async text => {
    const { db, requests } = await fixture((_request, response) => sse(response, text, true));
    await expect(db.changes().next()).rejects.toThrow(TypeError);
    expect(requests).toHaveLength(1);
  });

  test("rejects malformed UTF-8 before checkpoint delivery", async () => {
    const bytes = new Uint8Array([0xc3, 0x28, 10, 10]);
    const { db, requests } = await fixture((_request, response) => sse(response, bytes, true));
    await expect(db.changes().next()).rejects.toThrow(TypeError);
    expect(requests).toHaveLength(1);
  });

  test("bounds an unterminated event before buffering an unbounded stream", async () => {
    const { db, requests } = await fixture((_request, response) => sse(response, ":" + "x".repeat(8 * 1024 * 1024), true));
    await expect(db.changes().next()).rejects.toThrow(/exceeding 8 MiB/u);
    expect(requests).toHaveLength(1);
  });

  test("a lost response body is surfaced without replay", async () => {
    /** @type {import("node:http").ServerResponse | undefined} */
    let connection;
    const { db, requests } = await fixture((_request, response) => { connection = response; sse(response, cursor("head")); });
    const iter = db.changes();
    await iter.next();
    const next = iter.next();
    connection?.destroy();
    await expect(next).rejects.toMatchObject({ name: "BodyTransportError" });
    expect(requests).toHaveLength(1);
  });

  test("rejects a successful response with the wrong media type", async () => {
    const { db, requests } = await fixture((_request, response) => response.writeHead(200,
      { "content-type": "application/json" }).end('{}'));
    await expect(db.changes().next()).rejects.toThrow(TypeError);
    expect(requests).toHaveLength(1);
  });
});

describe("SSE cancellation releases HTTP streams", () => {
  test("pre-abort retains the exact reason with no request", async () => {
    const { db, requests } = await fixture((_request, response) => sse(response, cursor("head")));
    const controller = new AbortController();
    const reason = new Error("cancel before streaming");
    controller.abort(reason);
    await expect(db.changes({}, { signal: controller.signal }).next()).rejects.toBe(reason);
    expect(requests).toHaveLength(0);
  });

  test("abort while a partial event is pending cancels the native body", async () => {
    /** @type {() => void} */
    let sent;
    const received = new Promise(resolve => { sent = () => resolve(undefined); });
    /** @type {() => void} */
    let closed;
    const disconnected = new Promise(resolve => { closed = () => resolve(undefined); });
    const { db, requests } = await fixture((_request, response) => {
      response.once("close", closed);
      sse(response, 'event: cursor\nid: head\ndata: {"cursor":');
      sent();
    });
    const controller = new AbortController();
    const reason = new DOMException("cancel pending SSE event", "AbortError");
    const next = db.changes({}, { signal: controller.signal }).next();
    await received;
    controller.abort(reason);
    await expect(next).rejects.toBe(reason);
    await disconnected;
    expect(requests).toHaveLength(1);
  });

  test("abort between buffered events stops delivery without reconnect", async () => {
    const { db, requests } = await fixture((_request, response) => sse(response, cursor("one") + cursor("two")));
    const controller = new AbortController();
    const reason = new Error("cancel buffered event");
    const iter = db.changes({}, { signal: controller.signal });
    await iter.next();
    controller.abort(reason);
    await expect(iter.next()).rejects.toBe(reason);
    expect(requests).toHaveLength(1);
  });

  test("consumer return closes an open native response without continuation", async () => {
    /** @type {() => void} */
    let closed;
    const disconnected = new Promise(resolve => { closed = () => resolve(undefined); });
    const { db, requests } = await fixture((_request, response) => { response.once("close", closed); sse(response, cursor("head")); });
    const iter = db.changes();
    await iter.next();
    await iter.return();
    await disconnected;
    expect(requests).toHaveLength(1);
  });

  test("return while next is pending closes the partial response", async () => {
    /** @type {() => void} */
    let sent;
    const received = new Promise(resolve => { sent = () => resolve(undefined); });
    /** @type {() => void} */
    let closed;
    const disconnected = new Promise(resolve => { closed = () => resolve(undefined); });
    const { db, requests } = await fixture((_request, response) => {
      response.once("close", closed);
      sse(response, 'event: cursor\nid: AQ\ndata: {"cursor":');
      sent();
    });
    const iter = db.changes();
    const next = iter.next();
    void next.catch(() => undefined);
    await received;
    expect(await iter.return()).toEqual({ done: true, value: undefined });
    await disconnected;
    expect(await next).toEqual({ done: true, value: undefined });
    expect(requests).toHaveLength(1);
  });

  test("abort after a normal EOF stops the reconnect", async () => {
    const { db, requests } = await fixture((_request, response) => sse(response, cursor("head"), true));
    const controller = new AbortController();
    const reason = new Error("cancel reconnect");
    const iter = db.changes({}, { signal: controller.signal });
    await iter.next();
    const next = iter.next();
    await new Promise(resolve => setImmediate(resolve));
    controller.abort(reason);
    await expect(next).rejects.toBe(reason);
    expect(requests).toHaveLength(1);
  });
});
