import { afterEach, describe, expect, test, vi } from "vitest";
import { createTerraBaseClient } from "../src/index.js";
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
  test("a connection failure of a write is surfaced, not resent", async () => {
    let calls = 0;
    /** @type {import("../src/config.js").FetchLike} */
    const fetch = async () => { calls++; throw new TypeError("fetch failed"); };
    const docs = createTerraBaseClient({ project, token, baseUrl: "http://127.0.0.1:9", fetch }).database("app").collection("users");
    await expect(docs.put(entity, { value: 2 })).rejects.toBeInstanceOf(RequestTransportError);
    expect(calls).toBe(1);
  });
});

