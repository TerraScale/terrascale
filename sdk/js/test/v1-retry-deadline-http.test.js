import { afterEach, describe, expect, test, vi } from "vitest";
import { createTerraBaseClient, TerraBaseV1Error } from "../src/index.js";
import { RequestTransportError } from "../src/v1/transport.js";
import { entity, fixture, json, project, token } from "./helpers/v1-http.js";

const document = { id: entity, version: "opaque", state: "value", document: { value: 1 } };
const busy = { error: { code: "tenant_busy", message: "organization request budget exceeded" } };

afterEach(() => { vi.useRealTimers(); });

describe("replay-safe reads retry transient refusals", () => {
  test("429 tenant_busy reads wait for Retry-After and succeed", async () => {
    let calls = 0;
    const { client, requests } = await fixture((_request, response) => {
      if (++calls === 1) {
        response.setHeader("retry-after", "1");
        json(response, busy, 429);
      } else json(response, document);
    });
    const started = performance.now();
    await expect(client.database("app").collection("users").get(entity)).resolves.toEqual(document);
    expect(performance.now() - started).toBeGreaterThanOrEqual(990);
    expect(requests).toHaveLength(2);
  });

  test("read-only queries retry gateway failures; the fourth refusal surfaces typed", async () => {
    const { client, requests } = await fixture((_request, response) => {
      response.writeHead(502).end();
    });
    const error = await client.database("app").collection("users").query().catch(error => error);
    expect(error).toBeInstanceOf(TerraBaseV1Error);
    expect(error).toMatchObject({ status: 502, code: "http_502" });
    expect(requests).toHaveLength(4);
    expect(requests.every(request => request.method === "POST")).toBe(true);
  });

  test.each([
    [400, "invalid_query"], [401, "invalid_key"], [403, "forbidden_scope"], [404, "not_found"],
    [409, "conflict"], [429, "storage_quota"], [500, "internal_error"], [503, "write_outcome_unknown"],
  ])("deterministic %s %s is not retried", async (status, code) => {
    const { client, requests } = await fixture((_request, response) => json(response, { error: { code } }, status));
    const error = await client.database("app").collection("users").get(entity).catch(error => error);
    expect(error).toMatchObject({ status, code });
    expect(requests).toHaveLength(1);
  });

  test("a Retry-After beyond the read deadline surfaces the refusal without waiting", async () => {
    const { client, requests } = await fixture((_request, response) => {
      response.setHeader("retry-after", "120");
      json(response, busy, 429);
    });
    const started = performance.now();
    const error = await client.database("app").collection("users").get(entity).catch(error => error);
    expect(error).toMatchObject({ status: 429, code: "tenant_busy", retryAfter: 120 });
    expect(performance.now() - started).toBeLessThan(1_000);
    expect(requests).toHaveLength(1);
  });

  test("connection failures before a response resend reads only", async () => {
    let calls = 0;
    /** @type {import("../src/config.js").FetchLike} */
    const fetch = async (input, init) => {
      if (++calls === 1) throw new TypeError("fetch failed", { cause: new Error("ECONNRESET") });
      return globalThis.fetch(input, init);
    };
    const { baseUrl, requests } = await fixture((_request, response) => json(response, document));
    const docs = createTerraBaseClient({ project, token, baseUrl, fetch }).database("app").collection("users");
    await expect(docs.get(entity)).resolves.toEqual(document);
    expect(calls).toBe(2);
    expect(requests).toHaveLength(1);
  });
});

describe("mutations stay single-attempt", () => {
  test.each([
    [429, busy, "1"], [503, { error: { code: "storage_busy" } }, "1"], [502, undefined, undefined],
  ])("a %s refusal of a write is surfaced, not resent", async (status, body, retryAfter) => {
    const { client, requests } = await fixture((_request, response) => {
      if (retryAfter !== undefined) response.setHeader("retry-after", retryAfter);
      if (body === undefined) response.writeHead(status).end();
      else json(response, body, status);
    });
    const error = await client.database("app").collection("users").put(entity, { value: 2 }).catch(error => error);
    expect(error).toMatchObject({ status });
    expect(requests).toHaveLength(1);
  });

  test("a connection failure of a write is surfaced, not resent", async () => {
    let calls = 0;
    /** @type {import("../src/config.js").FetchLike} */
    const fetch = async () => { calls++; throw new TypeError("fetch failed"); };
    const docs = createTerraBaseClient({ project, token, baseUrl: "http://127.0.0.1:9", fetch }).database("app").collection("users");
    await expect(docs.put(entity, { value: 2 })).rejects.toBeInstanceOf(RequestTransportError);
    expect(calls).toBe(1);
  });
});

describe("read deadline", () => {
  test("a stalled read rejects with TimeoutError after 60 seconds", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
    /** @type {AbortSignal | undefined} */
    let seen;
    /** @type {import("../src/config.js").FetchLike} */
    const fetch = (_input, init) => new Promise((_resolve, reject) => {
      seen = init?.signal ?? undefined;
      seen?.addEventListener("abort", () => reject(seen?.reason), { once: true });
    });
    const docs = createTerraBaseClient({ project, token, baseUrl: "https://db.example", fetch }).database("app").collection("users");
    const pending = docs.get(entity).catch(error => error);
    await vi.advanceTimersByTimeAsync(59_999);
    expect(seen?.aborted).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    const error = await pending;
    expect(error).toBeInstanceOf(DOMException);
    expect(error.name).toBe("TimeoutError");
    expect(error.message).toBe("TerraBase read exceeded its 60-second deadline.");
  });

  test("the caller's abort reason wins over the deadline", async () => {
    /** @type {import("../src/config.js").FetchLike} */
    const fetch = (_input, init) => new Promise((_resolve, reject) => {
      init?.signal?.addEventListener("abort", () => reject(init.signal?.reason), { once: true });
    });
    const docs = createTerraBaseClient({ project, token, baseUrl: "https://db.example", fetch }).database("app").collection("users");
    const controller = new AbortController();
    const reason = new Error("caller cancelled");
    const pending = docs.get(entity, { signal: controller.signal });
    controller.abort(reason);
    await expect(pending).rejects.toBe(reason);
  });

  test("writes carry only the caller's signal", async () => {
    /** @type {RequestInit | undefined} */
    let seen;
    const { baseUrl } = await fixture((_request, response) => json(response, { id: entity, version: "v", offset: "1", durability: "local_durable" }));
    /** @type {import("../src/config.js").FetchLike} */
    const fetch = (input, init) => { seen = init; return globalThis.fetch(input, init); };
    const docs = createTerraBaseClient({ project, token, baseUrl, fetch }).database("app").collection("users");
    await docs.put(entity, { value: 2 });
    expect(seen?.signal).toBeUndefined();
  });
});
