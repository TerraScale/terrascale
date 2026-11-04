import { createServer } from "node:http";
import { afterEach, describe, expect, test } from "vitest";
import { V1Databases, deleteCollectionSchema, getCollectionSchema, putCollectionSchema } from "../src/v1/catalog.js";
import { V1Database } from "../src/v1/terrabase-v1.js";
import { V1Transport } from "../src/v1/transport.js";
import { ExactDecimal } from "../src/v1/json.js";

const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
const id = "01ARZ3NDEKTSV4RRFFQ69G5FA2";
const token = "fixture.payload.signature";
const prefix = `/v1/projects/${project}/databases`;
const params = { database: "app", collection: "users" };
const dialect = "https://json-schema.org/draft/2020-12/schema";
const database = { id, projectId: project, name: "app", branchedFrom: null,
  createdAt: "2026-10-13T12:00:00+00:00", region: "region-123", homeRegion: "region-123", replicaRegions: ["other-region"], allRegions: false };
/** @typedef {{ method: string | undefined; url: URL; headers: import("node:http").IncomingHttpHeaders; body: string }} WireRequest */
/** @type {import("node:http").Server[]} */
const servers = [];

afterEach(async () => {
  for (const server of servers.splice(0)) {
    server.closeAllConnections();
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve(undefined)));
  }
});

/** @param {(request: WireRequest, response: import("node:http").ServerResponse) => void} handle */
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
  const transport = new V1Transport({ project, token, baseUrl: `http://127.0.0.1:${address.port}` });
  return { transport, databases: new V1Databases(transport), requests };
}

/** @param {import("node:http").ServerResponse} response @param {unknown} value @param {number} [status] */
function json(response, value, status = 200) {
  response.writeHead(status, { "content-type": "application/json" }).end(JSON.stringify(value));
}

describe("public database catalog HTTP contracts", () => {
  test("uses the six named routes, exact camelCase fields and an empty 204", async () => {
    const { databases, requests } = await fixture((request, response) => {
      if (request.method === "DELETE") { response.writeHead(204).end(); return; }
      json(response, request.method === "GET" && request.url.pathname === prefix ? { items: [database], next: null } : database,
        request.method === "POST" ? 201 : 200);
    });
    expect(await databases.create({ name: "app" })).toEqual(database);
    expect(await databases.list({ after: "opaque+cursor", limit: 100 })).toEqual({ items: [database], next: null });
    expect(await databases.get("app")).toEqual(database);
    expect(await databases.update("app", { replicaRegions: ["other-region"] })).toEqual(database);
    expect(await databases.branch("app", { name: "branch" })).toEqual(database);
    expect(await databases.delete("app")).toBeUndefined();
    expect(requests.map(request => [request.method, request.url.pathname])).toEqual([
      ["POST", prefix], ["GET", prefix], ["GET", `${prefix}/app`], ["PATCH", `${prefix}/app`],
      ["POST", `${prefix}/app/branches`], ["DELETE", `${prefix}/app`],
    ]);
    expect(requests.map(request => request.body)).toEqual(['{"name":"app"}', "", "", '{"replicaRegions":["other-region"]}', '{"name":"branch"}', ""]);
    expect([...(requests[1]?.url.searchParams ?? [])]).toEqual([["after", "opaque+cursor"], ["limit", "100"]]);
    for (const request of requests) {
      expect(request.headers.authorization).toBe(`Bearer ${token}`);
      expect(request.headers.accept).toBe(request.method === "DELETE" ? "*/*" : "application/json");
      expect(request.headers["idempotency-key"]).toBeUndefined();
      if (request.body !== "") expect(request.headers["content-type"]).toBe("application/json");
    }
  });

  // HTTP fixtures verify the SDK contract, not server-side rename invariants.
  test.each([id, "app"])("renames selector %s and preserves returned identity and placement", async selector => {
    const renamed = { ...database, name: "app_new" };
    const { transport, databases, requests } = await fixture((request, response) => json(response,
      request.url.pathname.endsWith("/documents/by-key")
        ? { id, version: "tbv1-00000000000000000000000000000000", state: "value", document: { kept: true } } : renamed));
    expect(await databases.rename(selector, "app_new")).toEqual(renamed);
    expect(requests).toHaveLength(1);
    expect(requests[0]).toMatchObject({ method: "PATCH", body: '{"name":"app_new"}' });
    expect(requests[0]?.url.pathname).toBe(`${prefix}/${selector}`);
    expect(requests[0]?.headers.authorization).toBe(`Bearer ${token}`);
    expect(requests[0]?.headers["content-type"]).toBe("application/json");
    expect(requests[0]?.headers["idempotency-key"]).toBeUndefined();
    for (const [databaseId, name] of /** @type {const} */ ([["../escape", "app_new"], [id, "Bad Name"], [id, ""]]))
      await expect(databases.rename(databaseId, name)).rejects.toThrow(TypeError);
    await expect(databases.rename(id, "app_new", /** @type {any} */ ({ idempotencyKey: "unsupported" }))).rejects.toThrow(/Idempotency-Key/u);
    const reason = new Error("cancel rename");
    await expect(databases.rename(id, "app_new", { signal: AbortSignal.abort(reason) })).rejects.toBe(reason);
    expect(await databases.get(id)).toEqual(renamed);
    expect((await new V1Database(transport, id).collection("orders").get(id)).document).toEqual({ kept: true });
    expect(requests.slice(1).map(request => [request.method, request.url.pathname])).toEqual([
      ["GET", `${prefix}/${id}`], ["GET", `${prefix}/${id}/collections/orders/documents/by-key`],
    ]);
    for (const selector of ["", "../escape", "Bad Name", `8${id.slice(1)}`, id.toLowerCase()]) {
      await expect(databases.get(selector)).rejects.toThrow(TypeError);
      expect(() => new V1Database(transport, selector)).toThrow(TypeError);
    }
    await expect(databases.create({ name: id })).rejects.toThrow(TypeError);
    await expect(databases.rename(id, id)).rejects.toThrow(TypeError);
    await expect(databases.branch(id, { name: id })).rejects.toThrow(TypeError);
    expect(requests).toHaveLength(3);
  });

  test.each([[400, "invalid_request"], [401, "unauthorized"], [403, "forbidden_scope"], [404, "unknown_database"],
    [409, "platform_placement_locked"], [409, "name_taken"], [421, "wrong_region"], [503, "storage_unavailable"]])("preserves rename error %i without replaying", async (status, code) => {
    const { databases, requests } = await fixture((_request, response) =>
      json(response, { error: { code, message: "Refused" } }, status));
    await expect(databases.rename(id, "app_new")).rejects.toMatchObject({ status, code });
    expect(requests).toHaveLength(1);
  });

  test("preserves explicit placement and nullable creation inputs without defaulting", async () => {
    const { databases, requests } = await fixture((_request, response) => json(response, database, 201));
    await databases.create({ name: "app", homeRegion: "region-123", replicaRegions: ["other-region"] });
    await databases.create({ name: "app", homeRegion: null, replicaRegions: null });
    expect(requests.map(request => JSON.parse(request.body))).toEqual([
      { name: "app", homeRegion: "region-123", replicaRegions: ["other-region"] },
      { name: "app", homeRegion: null, replicaRegions: null },
    ]);
  });

  test("sends allRegions as an exclusive placement mode and rejects combinations before HTTP", async () => {
    const { databases, requests } = await fixture((request, response) => json(response, { ...database, allRegions: true, replicaRegions: [] },
      request.method === "POST" ? 201 : 200));
    expect(await databases.create({ name: "app", allRegions: true })).toMatchObject({ allRegions: true, replicaRegions: [] });
    expect(await databases.update("app", { allRegions: true })).toMatchObject({ allRegions: true });
    expect(requests.map(request => request.body)).toEqual(['{"name":"app","allRegions":true}', '{"allRegions":true}']);
    for (const request of [{ name: "app", allRegions: true, replicaRegions: ["other-region"] }, { name: "app", allRegions: "yes" }])
      await expect(databases.create(/** @type {any} */ (request))).rejects.toThrow(TypeError);
    for (const request of [{ allRegions: true, replicaRegions: [] }, { allRegions: false, replicaRegions: [] }, { allRegions: false }])
      await expect(databases.update("app", /** @type {any} */ (request))).rejects.toThrow(TypeError);
    expect(requests).toHaveLength(2);
  });

  test("keeps the list cursor opaque and returns a separate terminal page", async () => {
    const { databases, requests } = await fixture((request, response) => json(response,
      request.url.searchParams.has("after") ? { items: [], next: null } : { items: [database], next: "next+cursor" }));
    const first = await databases.list();
    expect(first.next).toBe("next+cursor");
    if (first.next === null) throw new Error("Expected a continuation cursor.");
    expect(await databases.list({ after: first.next })).toEqual({ items: [], next: null });
    expect(requests).toHaveLength(2);
    expect([...(requests[0]?.url.searchParams ?? [])]).toEqual([]);
    expect(requests[1]?.url.searchParams.get("after")).toBe("next+cursor");
  });

  test("rejects unsupported members and invalid placement before any HTTP request", async () => {
    const { databases, requests } = await fixture((_request, response) => json(response, database));
    for (const request of [null, [], { name: "App" }, { name: "app", enabled: true },
      { name: "app", homeRegion: "region-" }, { name: "app", homeRegion: "1region" },
      { name: "app", replicaRegions: ["same", "same"] }, { name: "app", replicaRegions: Array(9).fill("region") },
      { name: "app", homeRegion: "region", replicaRegions: ["region"] }])
      await expect(databases.create(/** @type {any} */ (request))).rejects.toThrow(TypeError);
    for (const request of [{}, { replicaRegions: null }, { replicaRegions: [], name: "renamed" }, { replicaRegions: [], enabled: true }])
      await expect(databases.update("app", /** @type {any} */ (request))).rejects.toThrow(TypeError);
    await expect(databases.list({ limit: 101 })).rejects.toThrow(TypeError);
    await expect(databases.list(/** @type {any} */ ({ cursor: "wrong-domain" }))).rejects.toThrow(TypeError);
    await expect(databases.branch("app", /** @type {any} */ ({ name: "branch", homeRegion: "region" }))).rejects.toThrow(TypeError);
    await expect(databases.create({ name: "app" }, /** @type {any} */ ({ idempotencyKey: "unsupported" }))).rejects.toThrow(/Idempotency-Key/u);
    expect(requests).toHaveLength(0);
  });

  test.each([401, 403, 409, 421, 503])("retains denial/placement/conflict %i without retrying", async status => {
    const code = status === 401 ? "invalid_key" : status === 403 ? "forbidden_scope" : status === 409 ? "database_replicated" : status === 421 ? "wrong_region" : "storage_busy";
    const { databases, requests } = await fixture((_request, response) => response.writeHead(status,
      { "content-type": "application/json", "x-request-id": "catalog-request", "x-terrabase-database-home": "region", "retry-after": "1" })
      .end(JSON.stringify({ error: { code, message: "Refused" } })));
    const error = await databases.update("app", { replicaRegions: [] }).catch(error => error);
    expect(error).toMatchObject({ status, code, requestId: "catalog-request" });
    expect(error.headers.get("x-terrabase-database-home")).toBe("region");
    expect(requests).toHaveLength(1);
  });

  test.each([{ id: "invalid" }, { projectId: null }, { name: "App" }, { branchedFrom: undefined },
    { branchedFrom: "invalid" }, { createdAt: "not-a-date" }, { replicaRegions: null }, { homeRegion: null }, { allRegions: "yes" }])(
    "rejects malformed database response %j", async fields => {
      const { databases } = await fixture((_request, response) => json(response, { ...database, ...fields }));
      await expect(databases.get("app")).rejects.toThrow(TypeError);
    },
  );

  test.each([undefined, "", 1])("rejects malformed list next %j", async next => {
    const { databases } = await fixture((_request, response) => json(response, { items: [], next }));
    await expect(databases.list()).rejects.toThrow(TypeError);
  });

  test("preserves pre-abort reasons without dispatch", async () => {
    const { databases, requests } = await fixture((_request, response) => json(response, database));
    const reason = new Error("cancel catalog request");
    await expect(databases.get("app", { signal: AbortSignal.abort(reason) })).rejects.toBe(reason);
    expect(requests).toHaveLength(0);
  });
});

describe("declared collection schema HTTP contracts", () => {
  test("sends raw boolean/object schemas and preserves detached null and optional dialect", async () => {
    const { transport, requests } = await fixture((request, response) => json(response,
      request.method === "PUT" ? { schema: JSON.parse(request.body), dialect } : { schema: null }));
    expect(await getCollectionSchema(transport, params)).toEqual({ schema: null });
    for (const schema of [true, false, { type: "object", additionalProperties: false }])
      expect(await putCollectionSchema(transport, params, schema)).toEqual({ schema, dialect });
    expect(await deleteCollectionSchema(transport, params)).toEqual({ schema: null });
    expect(requests.map(request => request.method)).toEqual(["GET", "PUT", "PUT", "PUT", "DELETE"]);
    expect(requests.map(request => request.body)).toEqual(["", "true", "false", '{"type":"object","additionalProperties":false}', ""]);
    for (const request of requests) expect(request.url.pathname).toBe(`${prefix}/app/collections/users/schema`);
  });

  test("checks documented schema byte/depth/value bounds before dispatch", async () => {
    const { transport, requests } = await fixture((request, response) => json(response, { schema: JSON.parse(request.body), dialect }));
    const base = JSON.stringify({ $comment: "" }).length;
    const atLimit = { $comment: "a".repeat(64 * 1024 - base) };
    await putCollectionSchema(transport, params, atLimit);
    await expect(putCollectionSchema(transport, params, { $comment: `${atLimit.$comment}é` })).rejects.toThrow(/64 KiB/u);
    /** @type {any} */
    let deep = {};
    for (let i = 0; i < 14; i++) deep = { child: deep };
    await putCollectionSchema(transport, params, deep);
    await expect(putCollectionSchema(transport, params, { child: deep })).rejects.toThrow(/15 levels/u);
    await putCollectionSchema(transport, params, { enum: Array(4091).fill(null) });
    await expect(putCollectionSchema(transport, params, { enum: Array(4092).fill(null) })).rejects.toThrow(/4093 values/u);
    for (const schema of [null, [], "schema", 1])
      await expect(putCollectionSchema(transport, params, /** @type {any} */ (schema))).rejects.toThrow(TypeError);
    await expect(putCollectionSchema(transport, params, true, /** @type {any} */ ({ idempotencyKey: "unsupported" }))).rejects.toThrow(/Idempotency-Key/u);
    expect(requests).toHaveLength(3);
  });

  test.each([{}, { schema: [] }, { schema: "invalid" }, { schema: true, dialect: 1 }])(
    "rejects malformed schema response %j", async value => {
      const { transport } = await fixture((_request, response) => json(response, value));
      await expect(getCollectionSchema(transport, params)).rejects.toThrow(TypeError);
    },
  );

  test("preserves exact schema numbers on the wire and in the response", async () => {
    const { transport, requests } = await fixture((request, response) => response.writeHead(200,
      { "content-type": "application/json" }).end(`{"schema":${request.body},"dialect":"${dialect}"}`));
    const result = await putCollectionSchema(transport, params, { minimum: 9007199254740993n });
    expect(requests[0]?.body).toBe('{"minimum":9007199254740993}');
    if (result.schema === null || typeof result.schema !== "object") throw new Error("Expected a schema object.");
    expect(result.schema.minimum).toBeInstanceOf(ExactDecimal);
    expect(String(result.schema.minimum)).toBe("9007199254740993");
  });

  test.each([401, 403])("retains schema denial %i without retrying", async status => {
    const code = status === 401 ? "invalid_key" : "forbidden_scope";
    const { transport, requests } = await fixture((_request, response) => json(response, { error: { code, message: "Denied" } }, status));
    await expect(putCollectionSchema(transport, params, true)).rejects.toMatchObject({ status, code });
    expect(requests).toHaveLength(1);
  });

  test("retains producer semantic schema errors without retrying", async () => {
    const { transport, requests } = await fixture((_request, response) => response.writeHead(400,
      { "content-type": "application/json", "x-request-id": "schema-request" })
      .end(JSON.stringify({ error: { code: "invalid_schema", message: "External references are unsupported", details: [{ path: "/$ref", keyword: "$ref" }] } })));
    await expect(putCollectionSchema(transport, params, { $ref: "https://example.invalid/schema" })).rejects.toMatchObject({
      code: "invalid_schema", status: 400, requestId: "schema-request", details: { details: [{ path: "/$ref", keyword: "$ref" }] },
    });
    expect(requests).toHaveLength(1);
  });
});

// Conflict metadata must survive catalog list/get and clear on a successful rename.
test.each([{}, { previousName: null, conflictOf: null }, { previousName: "app", conflictOf: project }])(
  "decodes optional database conflict metadata %j", async fields => {
    const value = { ...database, ...fields };
    const cleared = { ...database, previousName: null, conflictOf: null };
    const { databases } = await fixture((request, response) => json(response, request.method === "PATCH" ? cleared :
      request.url.pathname === prefix ? { items: [value], next: null } : value));
    expect(await databases.get(id)).toEqual(value);
    expect((await databases.list()).items).toEqual([value]);
    expect(await databases.rename(id, "app")).toEqual(cleared);
  },
);

test.each([{ previousName: 123 }, { conflictOf: "app" }])("rejects malformed conflict metadata %j", async fields => {
  const { databases } = await fixture((_request, response) => json(response, { ...database, ...fields }));
  await expect(databases.get(id)).rejects.toThrow(TypeError);
});
