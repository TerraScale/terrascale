import { createServer } from "node:http";
import { afterEach, describe, expect, test } from "vitest";
import { createTerraBaseV1Client } from "../src/index.js";
import { ExactDecimal } from "../src/v1/json.js";
import { TerraBaseV1Error } from "../src/v1/errors.js";
import { runSql, checkSql } from "../src/v1/sql.js";
import { V1Transport } from "../src/v1/transport.js";

// Real loopback HTTP verifies SDK contracts, not C# producer qualification.
const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
const token = "fixture.payload.signature";
const prefix = `/v1/projects/${project}/databases/app/sql`;
const validResult = { matched: 1, truncated: false, rows: [{ id: "customer/key", value: { name: "Ada", missing: null } }] };
const validCheck = { ok: true, error: null, collection: "people", hasLimit: true, plan: ["SEARCH people USING INDEX people_name"], indexes: ["people_name"], suggestions: [{ field: "age", reason: "field has no leading index" }] };
const invalidCheck = { ok: false, error: "only SELECT statements are supported", collection: null, hasLimit: false, plan: [], indexes: [], suggestions: [] };
/** @typedef {{ method: string | undefined; url: URL; headers: import("node:http").IncomingHttpHeaders; body: string }} SqlRequest */
/** @type {import("node:http").Server[]} */
const servers = [];

afterEach(async () => {
  for (const server of servers.splice(0)) {
    server.closeAllConnections();
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve(undefined)));
  }
});

/**
 * @param {(request: SqlRequest, response: import("node:http").ServerResponse) => void} handle
 * @param {import("../src/config.js").FetchLike | undefined} [fetchHandler]
 */
async function fixture(handle, fetchHandler) {
  /** @type {SqlRequest[]} */
  const requests = [];
  const server = createServer((request, response) => {
    void (async () => {
      let body = "";
      for await (const chunk of request) body += chunk.toString();
      const entry = { method: request.method, url: new URL(request.url ?? "/", "http://localhost"), headers: request.headers, body };
      requests.push(entry);
      handle(entry, response);
    })().catch(error => response.destroy(error));
  });
  servers.push(server);
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => resolve(undefined));
  });
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("Expected loopback address.");
  const config = { project, token, baseUrl: `http://127.0.0.1:${address.port}`, ...(fetchHandler === undefined ? {} : { fetch: fetchHandler }) };
  return { transport: new V1Transport(config), db: createTerraBaseV1Client(config).database("app"), requests };
}

/** @param {import("node:http").ServerResponse} response @param {unknown} value @param {number} [status] */
function json(response, value, status = 200) {
  response.writeHead(status, { "content-type": "application/json" }).end(JSON.stringify(value));
}

describe("current C# SQL HTTP contracts", () => {
  test("uses only the three named POST routes and preserves SQL text and camelCase DTOs", async () => {
    const { db, requests } = await fixture((request, response) => json(response, request.url.pathname === prefix ? validResult : validCheck));
    const sql = " SELECT name, missing FROM people WHERE name = 'Ada' LIMIT 1;\n";
    expect(await db.sql(sql)).toEqual(validResult);
    expect(await db.checkSql(sql)).toEqual(validCheck);
    expect(await db.explainSql(sql)).toEqual(validCheck);
    expect(requests.map(request => request.url.pathname)).toEqual([prefix, `${prefix}/check`, `${prefix}/explain`]);
    for (const request of requests) {
      expect(request.method).toBe("POST");
      expect(request.url.search).toBe("");
      expect(JSON.parse(request.body)).toEqual({ sql });
      expect(request.headers.authorization).toBe(`Bearer ${token}`);
      expect(request.headers.accept).toBe("application/json");
      expect(request.headers["content-type"]).toBe("application/json");
      expect(request.headers["idempotency-key"]).toBeUndefined();
      expect(request.headers["if-match"]).toBeUndefined();
    }
  });

  test("preserves exact integers and decimals in projected SQL rows", async () => {
    const { transport } = await fixture((_request, response) => response.writeHead(200, { "content-type": "application/json" })
      .end('{"matched":1,"truncated":false,"rows":[{"id":"é/😀","value":{"integer":9007199254740993,"decimal":0.123456789012345678901,"missing":null}}]}'));
    const result = await runSql(transport, "app", "SELECT integer, decimal, missing FROM people");
    expect(result.rows[0]?.id).toBe("é/😀");
    expect(result.rows[0]?.value.integer).toBeInstanceOf(ExactDecimal);
    expect(String(result.rows[0]?.value.integer)).toBe("9007199254740993");
    expect(String(result.rows[0]?.value.decimal)).toBe("0.123456789012345678901");
    expect(result.rows[0]?.value.missing).toBeNull();
  });

  test("returns empty and truncated results without inventing or following a cursor", async () => {
    const { transport, requests } = await fixture((_request, response) => json(response, requests.length === 1
      ? { matched: 0, truncated: false, rows: [] }
      : { ...validResult, truncated: true }));
    expect(await runSql(transport, "app", "SELECT * FROM nobody")).toEqual({ matched: 0, truncated: false, rows: [] });
    const result = await runSql(transport, "app", "SELECT * FROM people LIMIT 1");
    expect(result).toEqual({ ...validResult, truncated: true });
    expect("nextCursor" in result).toBe(false);
    expect("cursor" in result).toBe(false);
    expect(requests).toHaveLength(2);
  });

  test.each(["DELETE FROM people", "SELECT * FROM people WHERE age = $1", "", "   "])("lets the producer report invalid SQL %j on check and explain", async sql => {
    const error = { error: { code: "invalid_request", message: "only SELECT statements are supported" } };
    const { transport, requests } = await fixture((request, response) => json(response,
      request.url.pathname.endsWith("/check") ? invalidCheck : error,
      request.url.pathname.endsWith("/check") ? 200 : 400));
    expect(await checkSql(transport, "app", sql)).toEqual(invalidCheck);
    await expect(checkSql(transport, "app", sql, {}, "explainSql")).rejects.toMatchObject({ status: 400, code: "invalid_request", body: error });
    expect(requests.map(request => JSON.parse(request.body))).toEqual([{ sql }, { sql }]);
  });

  test("bounds UTF-16 statement length without trimming or converting legacy descriptors", async () => {
    const { transport, requests } = await fixture((_request, response) => json(response, validResult));
    const sql = (`SELECT '${"😀".repeat(8180)}' FROM people`).padEnd(16_384, " ");
    await runSql(transport, "app", sql);
    expect(JSON.parse(requests[0]?.body ?? "").sql).toBe(sql);
    await expect(runSql(transport, "app", sql + " ")).rejects.toThrow(/16384/u);
    // @ts-expect-error Runtime callers must not supply a legacy builder descriptor.
    await expect(runSql(transport, "app", { statement: "SELECT * FROM people", parameters: [] })).rejects.toThrow(TypeError);
    // @ts-expect-error Runtime callers must not supply pagination outside the producer DTO.
    await expect(runSql(transport, "app", "SELECT * FROM people", { cursor: "unpublished" })).rejects.toThrow(/option/u);
    // @ts-expect-error The check helper cannot select another transport operation.
    await expect(checkSql(transport, "app", "SELECT * FROM people", {}, "getDocument")).rejects.toThrow(/checkSql or explainSql/u);
    expect(requests).toHaveLength(1);
  });

  test.each([
    [401, "invalid_key"], [403, "forbidden_scope"], [404, "unknown_database"], [503, "capacity"],
  ])("preserves denial/error payloads and request IDs without retry for HTTP %s", async (status, code) => {
    const body = { error: { code, message: "access denied", detail: { permission: "databases:read" } }, diagnostic: "preserved" };
    const { transport, requests } = await fixture((_request, response) => {
      response.setHeader("x-request-id", "sql-fixture-request");
      response.setHeader("retry-after", "2");
      json(response, body, Number(status));
    });
    const caught = await runSql(transport, "app", "SELECT * FROM people").catch(error => error);
    expect(caught).toBeInstanceOf(TerraBaseV1Error);
    expect(caught).toMatchObject({ status, code, body, details: body.error, requestId: "sql-fixture-request", retryAfter: 2 });
    expect(caught.responseText).toBe(JSON.stringify(body));
    expect(requests).toHaveLength(1);
  });

  test.each([
    undefined, null, [], {}, { ...validResult, matched: "1" }, { ...validResult, matched: -1 },
    { ...validResult, matched: 1.5 }, { ...validResult, matched: 0 }, { ...validResult, truncated: null },
    { ...validResult, rows: [null] }, { ...validResult, rows: [{ id: "key", value: null }] },
    { ...validResult, rows: [{ id: "", value: {} }] },
    { matched: 2, truncated: false, rows: [validResult.rows[0], validResult.rows[0]] },
    { matched: 1001, truncated: false, rows: Array.from({ length: 1001 }, (_unused, index) => ({ id: `key${index}`, value: {} })) },
  ])("rejects malformed SQL result %#", async value => {
    const { transport } = await fixture((_request, response) => json(response, value));
    await expect(runSql(transport, "app", "SELECT * FROM people")).rejects.toThrow(TypeError);
  });

  test.each([
    undefined, null, [], {}, { ...validCheck, ok: "true" }, { ...validCheck, error: "conflicting outcome" },
    { ...validCheck, collection: null }, { ...validCheck, hasLimit: "true" }, { ...validCheck, plan: [null] },
    { ...validCheck, indexes: [1] }, { ...validCheck, suggestions: [null] },
    { ...validCheck, suggestions: [{ field: "age", reason: null }] },
    { ...invalidCheck, error: null }, { ...invalidCheck, collection: "people" },
  ])("rejects malformed SQL check %#", async value => {
    const { transport } = await fixture((_request, response) => json(response, value));
    await expect(checkSql(transport, "app", "SELECT * FROM people")).rejects.toThrow(TypeError);
  });

  test.each(["{", "not JSON"])("rejects malformed JSON %j", async body => {
    const { transport } = await fixture((_request, response) => response.writeHead(200, { "content-type": "application/json" }).end(body));
    await expect(runSql(transport, "app", "SELECT * FROM people")).rejects.toThrow();
  });

  test("rejects an invalid SQL outcome returned as successful EXPLAIN", async () => {
    const { transport } = await fixture((_request, response) => json(response, invalidCheck));
    await expect(checkSql(transport, "app", "SELECT * FROM people", {}, "explainSql")).rejects.toThrow(/explain outcome/u);
  });

  test("pre-aborted calls send no SQL request", async () => {
    const { transport, requests } = await fixture((_request, response) => json(response, validResult));
    const reason = new Error("cancel SQL before dispatch");
    for (const operation of ["runSql", "checkSql", "explainSql"]) {
      const options = { signal: AbortSignal.abort(reason) };
      await expect(operation === "runSql" ? runSql(transport, "app", "SELECT * FROM people", options)
        : checkSql(transport, "app", "SELECT * FROM people", options, operation === "checkSql" ? "checkSql" : "explainSql")).rejects.toBe(reason);
    }
    expect(requests).toHaveLength(0);
  });

  test("cancels a SQL response stalled after headers with the original signal reason", async () => {
    /** @type {() => void} */
    let arrived = () => undefined;
    const headersArrived = new Promise(resolve => { arrived = () => resolve(undefined); });
    const { transport, requests } = await fixture((_request, response) => {
      response.writeHead(200, { "content-type": "application/json" });
      response.write('{"matched":');
    }, async (input, init) => {
      const response = await fetch(input, init);
      arrived();
      return response;
    });
    const controller = new AbortController();
    const pending = runSql(transport, "app", "SELECT * FROM people", { signal: controller.signal });
    await headersArrived;
    const reason = new Error("cancel stalled SQL body");
    controller.abort(reason);
    await expect(pending).rejects.toBe(reason);
    expect(requests).toHaveLength(1);
  });
});
