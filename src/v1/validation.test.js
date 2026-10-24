import { describe, expect, it, vi } from "vitest";
import { spawnSync } from "node:child_process";
import { decodeDocument, decodeWrite } from "./decode.js";
import { parseV1Error, V1ErrorCode, V1PreconditionFailedError } from "./errors.js";
import { isJsonPointer, requireFilter, requirePath, v1Filter } from "./filter.js";
import { requireDocumentId, requireName, requireProject, requireVersion } from "./ids.js";
import { assertJsonObject, assertJsonValue, ExactDecimal, parseExactJson, stringifyExactJson } from "./json.js";
import { decodeShapePage, encodeShapeRequest, requireShapeOffset } from "./shapes.js";
import { createTerraBaseV1Client } from "./terrabase-v1.js";
import { staticBaseUrlResolver, V1Transport } from "./transport.js";
import { v1Route, v1RoutePath, v1Routes } from "./routes.js";

// Pure validators, encoders and decoders only: no HTTP fixture, fetch substitute, or producer emulation.
const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
const token = "eyJ0eXAiOiJhdCtqd3QifQ.eyJwcm9qZWN0X2lkIjoiMDE5YTAwMDAtMDAwMC03MDAwLTgwMDAtMDAwMDAwMDAwMDAxIn0.c2lnbmF0dXJl";
const id = "01ARZ3NDEKTSV4RRFFQ69G5FA2";
const version = `tbv1-${"0".repeat(32)}`;
const tbv2 = "tbv2-AYCBgoOEhYaHiImKi4yNjo-QkZKTlJWWl5iZmpucnZ6f";
const handle = `h1-${"1".repeat(16)}`;
const config = { project, token, baseUrl: "https://database.example/prefix/" };
const db = createTerraBaseV1Client(config).database("app");
const docs = db.collection("users");

describe("C# v1 pure contract validation", () => {
  it("keeps catalog project ids separate from database/collection names and credentials private", () => {
    expect(requireProject(project)).toBe(project);
    expect(() => requireProject("..")).toThrow(TypeError);
    expect(requireName("database", "app")).toBe("app");
    expect(() => requireName("collection", "Users")).toThrow(TypeError);
    expect(JSON.stringify(createTerraBaseV1Client(config))).toBe("{}");
    expect("request" in db).toBe(false);
    expect("subscribe" in db).toBe(false);
    expect("createShape" in db).toBe(false);
  });

  it("requires exactly one explicit endpoint source and a header-safe access credential", () => {
    expect(() => createTerraBaseV1Client(config)).not.toThrow();
    expect(() => createTerraBaseV1Client({ ...config, token: `tb_${"A".repeat(43)}` })).not.toThrow();
    for (const token of ["", " ", "Bearer a.b.c", "a.b", "a.b.c.d", "a.b.c\r\nx-secret: value", "a.b.c ", "a..c"])
      expect(() => createTerraBaseV1Client({ ...config, token })).toThrow(/token must/u);
    expect(() => createTerraBaseV1Client({ project, token })).toThrow(/exactly one/u);
    expect(() => createTerraBaseV1Client({ ...config, endpointResolver: staticBaseUrlResolver(config.baseUrl) })).toThrow(/exactly one/u);
    expect(() => createTerraBaseV1Client({ ...config, token: `tbk1.${"a".repeat(22)}.${"b".repeat(43)}` })).toThrow(/tb_/u);
    for (const baseUrl of ["http://remote.example", "https://user:secret@db.example", "https://db.example?token=secret", "https://db.example#secret"])
      expect(() => staticBaseUrlResolver(baseUrl)).toThrow(TypeError);
    for (const baseUrl of ["http://127.0.0.1:18447", "http://[::1]:18447", "https://db.example"])
      expect(() => staticBaseUrlResolver(baseUrl)).not.toThrow();
  });

  it("resolves once per client/database without passing the token", async () => {
    /** @type {import('./transport.js').V1EndpointContext[]} */
    const contexts = [];
    const transport = new V1Transport({ project, token, endpointResolver: async context => {
      contexts.push(context);
      return "https://database.example/prefix/";
    } });
    const params = { database: "app", collection: "users", id };
    const expected = `https://database.example/prefix/v1/projects/${project}/databases/app/collections/users/documents/by-key`;
    expect(await Promise.all([transport.url("getDocument", params), transport.url("getDocument", params)])).toEqual([expected, expected]);
    await transport.url("queryDocuments", { ...params, database: "other" });
    expect(contexts.map(context => [context.project, context.database])).toEqual([[project, "app"], [project, "other"]]);
    expect(contexts.every(context => !Object.hasOwn(context, "token"))).toBe(true);
  });

  it("cancels one resolver waiter without cancelling another or changing the session", async () => {
    let calls = 0;
    /** @type {(value: string) => void} */
    let finish = () => {};
    const resolving = new Promise(resolve => { finish = resolve; });
    const transport = new V1Transport({ project, token, endpointResolver: () => {
      calls++;
      return /** @type {Promise<string>} */ (resolving);
    } });
    const params = { database: "app", collection: "users", id };
    const abort = new AbortController();
    const cancelled = transport.url("getDocument", params, abort.signal);
    const remaining = transport.url("getDocument", params);
    abort.abort(new Error("cancel resolver wait"));
    await expect(cancelled).rejects.toThrow("cancel resolver wait");
    finish("https://database.example");
    expect(await remaining).toContain(`/v1/projects/${project}/`);
    expect(await transport.url("getDocument", params)).toBe(await remaining);
    expect(calls).toBe(1);
  });

  it("validates resolved URLs and respects pre-aborted calls without I/O", async () => {
    const transport = new V1Transport({ project, token, endpointResolver: () => "http://remote.example" });
    await expect(transport.url("queryDocuments", { database: "app", collection: "users" })).rejects.toThrow(/loopback/u);
    const stopped = new AbortController();
    stopped.abort(new Error("cancelled"));
    await expect(docs.get(id, { signal: stopped.signal })).rejects.toThrow("cancelled");
  });

  it("expires hung shared resolution so cancellation and a later retry can recover", async () => {
    vi.useFakeTimers();
    try {
      let calls = 0;
      /** @type {AbortSignal | undefined} */
      let resolverSignal;
      /** @type {(value: string) => void} */
      let finish = () => {};
      const transport = new V1Transport({ project, token, endpointResolver: context => {
        if (++calls === 1) {
          resolverSignal = context.signal;
          return new Promise(resolve => { finish = resolve; });
        }
        return "https://recovered.example";
      } });
      const params = { database: "app", collection: "users", id };
      const abort = new AbortController();
      const cancelled = transport.url("getDocument", params, abort.signal);
      const remaining = transport.url("getDocument", params);
      abort.abort(new Error("cancel hung resolver wait"));
      await expect(cancelled).rejects.toThrow("cancel hung resolver wait");
      expect(resolverSignal?.aborted).toBe(false);
      const expired = expect(remaining).rejects.toMatchObject({ name: "TimeoutError" });
      await vi.advanceTimersByTimeAsync(30_000);
      await expired;
      expect(resolverSignal?.aborted).toBe(true);
      const recovered = await transport.url("getDocument", params);
      expect(recovered).toContain("https://recovered.example/");
      finish("https://expired.example");
      expect(await transport.url("getDocument", params)).toBe(recovered);
      expect(calls).toBe(2);
      expect(vi.getTimerCount()).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });

  it("does not keep Node alive after its sole resolver waiter cancels", () => {
    const script = `
      import { V1Transport } from ${JSON.stringify(new URL("./transport.js", import.meta.url).href)};
      const transport = new V1Transport({ project: ${JSON.stringify(project)}, token: ${JSON.stringify(token)}, endpointResolver: () => new Promise(() => {}) });
      const abort = new AbortController();
      const waiting = transport.url("getDocument", { database: "app", collection: "users", id: ${JSON.stringify(id)} }, abort.signal);
      abort.abort();
      await waiting.catch(() => {});
    `;
    const result = spawnSync(process.execPath, ["--input-type=module", "-e", script], { encoding: "utf8", timeout: 2000 });
    expect(result.error).toBeUndefined();
    expect(result.status, result.stderr).toBe(0);
  });

  it("keeps Node alive until an active resolver waiter times out after another cancels", () => {
    const script = `
      import { V1Transport } from ${JSON.stringify(new URL("./transport.js", import.meta.url).href)};
      const transport = new V1Transport({ project: ${JSON.stringify(project)}, token: ${JSON.stringify(token)}, endpointResolver: () => new Promise(() => {}) });
      const params = { database: "app", collection: "users", id: ${JSON.stringify(id)} };
      const abort = new AbortController();
      const cancelled = transport.url("getDocument", params, abort.signal);
      const active = transport.url("getDocument", params);
      abort.abort();
      await cancelled.catch(() => {});
      try { await active; } catch (error) { console.log(error.name); }
    `;
    const result = spawnSync(process.execPath, ["--input-type=module", "-e", script], { encoding: "utf8", timeout: 35_000 });
    expect(result.error).toBeUndefined();
    expect(result.status, result.stderr).toBe(0);
    expect(result.stdout.trim()).toBe("TimeoutError");
  }, 40_000);

  it("inventories current producer routes and the historical shape route", () => {
    expect(v1Routes).toHaveLength(26);
    expect(v1RoutePath(v1Route("readShape"), { project, database: "app", collection: "users" })).toBe(`/v1/projects/${project}/databases/app/collections/users/shape`);
    expect(v1Route("readShape").permission).toBe("shapes:read");
    expect(v1Routes.every(route => route.idempotencyKey === "none")).toBe(true);
    expect(v1Route("dropCollection").successStatuses).toEqual([200]);
  });

  it("accepts only canonical ULIDs as document ids", () => {
    expect(requireDocumentId(id)).toBe(id);
    for (const bad of [undefined, null, 1, "", id.toLowerCase(), "a/b", "a%2Fb", "..", " ", "é", "a".repeat(257), "😀".repeat(129)])
      expect(() => requireDocumentId(bad)).toThrow(TypeError);
  });

  it("treats tbv1 and tbv2 version tokens as opaque If-Match-safe strings", () => {
    for (const token of [version, tbv2]) expect(requireVersion(token)).toBe(token);
    for (const bad of [undefined, 1, "", "tbv2-a b", 'tbv2-"x"', "tbv2-\n", "tbv2-é", "t".repeat(257)]) expect(() => requireVersion(bad)).toThrow(TypeError);
    expect(decodeWrite({ status: 200, headers: new Headers(), value: { id, version: tbv2, offset: "7", durability: "local_durable" } }).version).toBe(tbv2);
    const shape = decodeShapePage({ shape_id: "0".repeat(32), handle, offset: "-1", next_offset: "1", up_to_date: true,
      changes: [{ offset: "1", ops: [{ op: "upsert", id, version: tbv2, document: {} }] }] }, new Headers());
    expect(shape.changes[0]?.ops[0]).toMatchObject({ version: tbv2 });
  });

  it("decodes values, causal conflicts with siblings, and unknown states or durability without throwing", () => {
    expect(decodeDocument({ id, version, state: "value", document: { seats: 1 } })).toEqual({ id, version, state: "value", document: { seats: 1 } });
    const siblings = [{ version, document: { seats: 1 } }, { version: tbv2, document: { seats: 2 } }];
    expect(decodeDocument({ id, version: tbv2, state: "conflict", document: { seats: 2 }, siblings, future: true }))
      .toEqual({ id, version: tbv2, state: "conflict", document: { seats: 2 }, siblings });
    expect(decodeDocument({ id, version: tbv2, state: "merged", document: {} }).state).toBe("merged");
    expect(() => decodeDocument({ id, version, state: "conflict", siblings: [] })).toThrow(TypeError);
    expect(() => decodeDocument({ id, version, state: "conflict", document: {}, siblings: {} })).toThrow(TypeError);
    expect(() => decodeDocument({ id, version: "in valid", state: "value", document: {} })).toThrow(TypeError);
    expect(decodeWrite({ status: 201, headers: new Headers({ location: "/created" }), value: { id, version, offset: "42", durability: "local_durable" } })).toMatchObject({ created: true, location: "/created", offset: "42" });
    expect(decodeWrite({ status: 200, headers: new Headers(), value: { id, version, offset: "42", durability: "synced_durable" } }).durability).toBe("synced_durable");
    expect(() => decodeWrite({ status: 200, headers: new Headers(), value: { id, version, offset: "42" } })).toThrow(TypeError);
  });

  it("preserves exact decimals and enforces producer decimal bounds", () => {
    const number = new ExactDecimal("12345678901234567890.123456789");
    expect(stringifyExactJson({ number }, "document")).toBe('{"number":12345678901234567890.123456789}');
    expect(parseExactJson("12345678901234567890.123456789")).toBeInstanceOf(ExactDecimal);
    for (const value of [new ExactDecimal("1e-307"), new ExactDecimal("9e307"), 0, 1000n])
      expect(() => assertJsonValue(value, "value")).not.toThrow();
    for (const value of [new ExactDecimal("1e-308"), new ExactDecimal("1e308"), new ExactDecimal(`1${"0".repeat(128)}`), Infinity])
      expect(() => assertJsonValue(value, "value")).toThrow(TypeError);
  });

  it("enforces document depth, nodes, UTF-16 text and plain JSON", () => {
    /** @type {unknown} */
    let nested = {};
    for (let n = 0; n < 15; n++) nested = { child: nested };
    expect(() => assertJsonObject(nested, "document")).not.toThrow();
    expect(() => assertJsonObject({ child: nested }, "document")).toThrow(/16/u);
    expect(() => assertJsonValue(Array(4095).fill(null), "value")).not.toThrow();
    expect(() => assertJsonValue(Array(4096).fill(null), "value")).toThrow(/4096/u);
    for (const document of [{ value: "\ud800" }, { "\ud800": 1 }, { value: undefined }, { value: new Date() }])
      expect(() => assertJsonObject(document, "document")).toThrow(TypeError);
    expect(() => assertJsonObject({ value: "🦄" }, "document")).not.toThrow();
  });

  it("limits pointers and filters and removes unsupported lower", () => {
    expect(isJsonPointer("/a~1b/~0")).toBe(true);
    expect(isJsonPointer(`/${Array(17).fill("a").join("/")}`)).toBe(false);
    expect(() => requirePath(/** @type {any} */ ({ lower: "/email" }))).toThrow(TypeError);
    expect("lower" in v1Filter).toBe(false);
    expect(requireFilter(v1Filter.and(v1Filter.eq("/a", 1), v1Filter.exists("$id")))).toBeDefined();
    expect(() => requireFilter({ lt: ["/a", null] })).toThrow(TypeError);
    expect(() => requireFilter({ in: ["/a", []] })).toThrow(TypeError);
  });

  it("builds and validates within radius and box filters", () => {
    expect(v1Filter.withinRadius("/loc", { lat: -23.55, lon: -46.63, meters: 1000 })).toEqual({ within: ["/loc", { lat: -23.55, lon: -46.63, meters: 1000 }] });
    expect(v1Filter.withinBox("/loc", { south: -20, west: 179, north: -10, east: -170 })).toEqual({ within: ["/loc", { south: -20, west: 179, north: -10, east: -170 }] });
    expect(requireFilter(v1Filter.not(v1Filter.withinRadius("/a/loc", { lat: 90, lon: 180, meters: 20_000_000 })))).toBeDefined();
    for (const circle of [{ lat: 0, lon: 0, meters: 0 }, { lat: 0, lon: 0, meters: 20_000_001 }, { lat: 91, lon: 0, meters: 1 }, { lat: 0, lon: -181, meters: 1 }, { lat: Number.NaN, lon: 0, meters: 1 }])
      expect(() => v1Filter.withinRadius("/loc", circle)).toThrow(TypeError);
    expect(() => v1Filter.withinRadius("$id", { lat: 0, lon: 0, meters: 1 })).toThrow(TypeError);
    expect(() => v1Filter.withinBox("/loc", { south: 10, west: 0, north: -10, east: 1 })).toThrow(/south/u);
    expect(() => v1Filter.withinBox("/loc", /** @type {any} */ ({ lat: 0, lon: 0, meters: 1 }))).toThrow(TypeError);
    expect(() => requireFilter({ within: ["/loc", { lat: 0, lon: 0, meters: 1, extra: 1 }] })).toThrow(TypeError);
    expect(() => requireFilter({ within: ["/loc"] })).toThrow(TypeError);
  });

  it("rejects unsupported and unsafe operation inputs before dispatch", async () => {
    await expect(docs.put(id, {}, /** @type {any} */ ({ idempotencyKey: "old-key" }))).rejects.toThrow(/Idempotency/u);
    await expect(docs.create({}, /** @type {any} */ ({ idempotencyKey: "old-key" }))).rejects.toThrow(/Idempotency/u);
    await expect(docs.patch(id, /** @type {any} */ ({ ops: [{ op: "set", path: "/a", value: undefined }] }))).rejects.toThrow(TypeError);
    await expect(docs.put(id, { large: "x".repeat(1024 * 1024) })).rejects.toThrow(/1 MiB/u);
    await expect(docs.query(/** @type {any} */ ({ explain: "plan" }))).rejects.toThrow(/explain/u);
    await expect(docs.query({ project: [`/${Array(17).fill("a").join("/")}`] })).rejects.toThrow(TypeError);
    await expect(db.collections.list({ limit: 201 })).rejects.toThrow(TypeError);
    await expect(db.collections.create("Users")).rejects.toThrow(TypeError);
  });

  it("enforces the members of every patch operation", () => {
    for (const op of [{ op: "set", path: "/a", value: 1 }, { op: "append", path: "/a", value: 1 }, { op: "unset", path: "/a" }, { op: "inc", path: "/a", by: 1 }]) {
      expect(() => db.transaction().patch("users", id, /** @type {any} */ ({ ops: [op] }))).not.toThrow();
      expect(() => db.transaction().patch("users", id, /** @type {any} */ ({ ops: [{ ...op, extra: true }] }))).toThrow(/Unknown patch op member/u);
    }
  });

  it("validates atomic transaction targets and count assertions without I/O", async () => {
    const tx = db.transaction().put("users", id, {});
    expect(() => tx.assertAbsent("users", id)).toThrow(/same document/u);
    expect(tx.size).toBe(1);
    expect(() => tx.assertCount("users", null, { gte: 2, lte: 1 })).toThrow(TypeError);
    expect(() => tx.assertCount("users", null, {})).toThrow(TypeError);
    expect(tx.assertCount("users", null, { lte: 10 }).size).toBe(2);
    await expect(tx.commit(/** @type {any} */ ({ idempotencyKey: "old-key" }))).rejects.toThrow(/Idempotency/u);
    await expect(db.transaction().commit()).rejects.toThrow(/at least one/u);
  });

  it("rejects excessive document or merge depth in a later transaction operation before dispatch", () => {
    /** @type {Record<string, any>} */
    let deep = {};
    for (let depth = 0; depth < 65; depth++) deep = { child: deep };
    const transaction = db.transaction().put("users", id, {});
    const laterId = "01ARZ3NDEKTSV4RRFFQ69G5FA3";
    expect(() => transaction.put("users", laterId, deep)).toThrow(/16 levels/u);
    expect(() => transaction.patch("users", laterId, { merge: deep })).toThrow(/16 levels/u);
    expect(transaction.size).toBe(1);
  });

  it("validates documents at the shared encoder for both write methods", async () => {
    for (const invalid of [null, [], { field: undefined }, { field: Infinity }]) {
      await expect(docs.put(id, /** @type {any} */ (invalid))).rejects.toThrow(TypeError);
      await expect(docs.create(/** @type {any} */ (invalid))).rejects.toThrow(TypeError);
    }
  });

  it("uses canonical snapshot keysets, bounded log positions and live cursors", () => {
    const position = `snap.42.${btoa(id).replace(/=+$/u, "")}`;
    expect(requireShapeOffset(position)).toBe(position);
    expect(requireShapeOffset("snap.42.0")).toBe("snap.42.0");
    for (const offset of ["snap.42.1", "snap.42._w", "snap.042.0", "9223372036854775808", "snap.42.YR", "snap.42.YQ="])
      expect(() => requireShapeOffset(offset)).toThrow(TypeError);
    expect(encodeShapeRequest({ offset: "42", handle, live: true, cursor: "12" }).toString()).toBe(`offset=42&handle=${handle}&live=true&cursor=12`);
    expect(() => encodeShapeRequest({ offset: "42" })).toThrow(/handle/u);
    expect(() => encodeShapeRequest({ offset: "snap.42.0", handle, live: true })).toThrow(/log/u);
    expect(() => encodeShapeRequest({ offset: "42", handle, live: true, cursor: "9223372036854775807" })).toThrow(TypeError);
    expect(() => encodeShapeRequest(/** @type {any} */ ({ mode: "sse" }))).toThrow(TypeError);
  });

  it("decodes the actual shape upsert fields without synthetic state", () => {
    const page = decodeShapePage({ shape_id: "2".repeat(32), handle, offset: "snap.42.0", next_offset: "42", up_to_date: false,
      changes: [{ offset: "42", ops: [{ op: "upsert", id, version, document: { seats: 1 } }, { op: "delete", id }] }] }, new Headers());
    expect(page.nextOffset).toBe("42");
    expect(page.upToDate).toBe(false);
    expect(page.changes[0]?.ops[0]).toEqual({ op: "upsert", id, version, document: { seats: 1 } });
    expect("state" in /** @type {object} */ (page.changes[0]?.ops[0])).toBe(false);
  });

  it("retains producer failure metadata and new collection/shape codes", () => {
    const error = parseV1Error(412, new Headers({ etag: `"${version}"` }), '{"error":{"code":"precondition_failed","op":1}}');
    expect(error).toBeInstanceOf(V1PreconditionFailedError);
    expect(error.op).toBe(1);
    expect(error.etag).toBe(version);
    expect(V1ErrorCode).toMatchObject({ nameTaken: "name_taken", shapeBusy: "shape_busy", uriTooLong: "uri_too_long" });
    expect(Object.values(V1ErrorCode)).not.toContain("idempotency_conflict");
  });
});
