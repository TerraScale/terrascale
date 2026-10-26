import { describe, expect, test, vi } from "vitest";
import { createTerraBaseClient, TerraBaseV1Error, V1KeyRevokedError, V1KeyExpiredError } from "../src/index.js";
import { fixture, json, project } from "./helpers/v1-http.js";

const first = "first.payload.signature";
const second = "second.payload.signature";
const document = { id: "01ARZ3NDEKTSV4RRFFQ69G5FA2", version: "opaque", state: "value", document: { value: 1 } };

describe("per-request bearer token providers over HTTP", () => {
  test.each([false, true])("replaces tokens for reads, writes and queries (async: %s)", async asynchronous => {
    const { baseUrl, requests } = await fixture((request, response) => {
      if (request.url.pathname.endsWith("/healthz"))
        json(response, { status: "ok", version: "0.0.1", region: "fixture", storageFormat: "1", revision: null, machine: "fixture" });
      else if (request.url.pathname.endsWith("/query")) json(response, { items: [document], next_cursor: null });
      else if (request.method === "GET") json(response, document);
      else json(response, { id: "01ARZ3NDEKTSV4RRFFQ69G5FA2", version: "opaque", offset: "1", durability: "local_durable" });
    });
    let current = first;
    const provider = vi.fn(() => asynchronous ? Promise.resolve(current) : current);
    const client = createTerraBaseClient({ project, baseUrl, token: provider });
    expect(provider).not.toHaveBeenCalled();
    const docs = client.database("app").collection("users");
    await docs.get("01ARZ3NDEKTSV4RRFFQ69G5FA2");
    current = second;
    await docs.put("01ARZ3NDEKTSV4RRFFQ69G5FA2", { value: 2 });
    await docs.query();
    await client.health.liveness();
    expect(requests.map(request => request.headers.authorization)).toEqual([
      `Bearer ${first}`, `Bearer ${second}`, `Bearer ${second}`, undefined,
    ]);
    expect(provider).toHaveBeenCalledTimes(3);
    expect(JSON.stringify(client)).not.toContain("payload");
  });

  test.each([undefined, null, false, 123, {}, "", " ", "a.b", "a..c", "Bearer a.b.c", "a.b.c ",
    "a.b.c\n", "a.b.c\r", "a.b.c\r\n", "a.b.c\u2028", "a.b.c\u2029",
    "a.b.c\r\nx-secret: token-canary", "tbk1.retired.key"])("invalid provider result %j sends no request", async value => {
    const { baseUrl, requests } = await fixture((_request, response) => json(response, document));
    const token = /** @type {import('../src/index.js').V1TokenProvider} */ (async () => value);
    const docs = createTerraBaseClient({ project, baseUrl, token }).database("app").collection("users");
    const error = await docs.get("01ARZ3NDEKTSV4RRFFQ69G5FA2").catch(error => error);
    expect(error).toBeInstanceOf(TypeError);
    expect(error.message).toBe("token must be a compact access JWT or a legacy C# tb_ API key.");
    expect(error.cause).toBeUndefined();
    expect(requests).toHaveLength(0);
  });

  test.each([false, true])("provider failure is sanitized without a request (async: %s)", async asynchronous => {
    const { baseUrl, requests } = await fixture((_request, response) => json(response, document));
    const sensitive = new Error("token-canary", { cause: { token: first } });
    const token = () => { if (asynchronous) return Promise.reject(sensitive); throw sensitive; };
    const docs = createTerraBaseClient({ project, baseUrl, token }).database("app").collection("users");
    const error = await docs.put("01ARZ3NDEKTSV4RRFFQ69G5FA2", {}).catch(error => error);
    expect(error).toBeInstanceOf(Error);
    expect(error.message).toBe("TerraBase token provider failed.");
    expect(error.cause).toBeUndefined();
    expect(`${error.stack}${JSON.stringify(error)}`).not.toMatch(/token-canary|first\.payload/u);
    expect(requests).toHaveLength(0);
  });

  test("cancels a pending provider promptly without cancelling another caller or sending late I/O", async () => {
    const { baseUrl, requests } = await fixture((_request, response) => json(response, document));
    /** @type {(value: string) => void} */
    let finish = () => {};
    const pending = new Promise(resolve => { finish = resolve; });
    const started = Promise.withResolvers();
    let calls = 0;
    const token = () => {
      if (++calls === 2) started.resolve(undefined);
      return /** @type {Promise<string>} */ (pending);
    };
    const docs = createTerraBaseClient({ project, baseUrl, token }).database("app").collection("users");
    const controller = new AbortController();
    const reason = new Error("cancel token wait");
    const cancelled = docs.get("01ARZ3NDEKTSV4RRFFQ69G5FA2", { signal: controller.signal });
    const remaining = docs.get("01ARZ3NDEKTSV4RRFFQ69G5FA2");
    await started.promise;
    controller.abort(reason);
    await expect(cancelled).rejects.toBe(reason);
    expect(requests).toHaveLength(0);
    finish(second);
    await expect(remaining).resolves.toEqual(document);
    expect(requests.map(request => request.headers.authorization)).toEqual([`Bearer ${second}`]);
    expect(calls).toBe(2);
  });

  test("pre-aborted requests never invoke the provider", async () => {
    const { baseUrl, requests } = await fixture((_request, response) => json(response, document));
    const token = vi.fn(() => first);
    const docs = createTerraBaseClient({ project, baseUrl, token }).database("app").collection("users");
    const reason = new Error("pre-cancel token wait");
    await expect(docs.get("01ARZ3NDEKTSV4RRFFQ69G5FA2", { signal: AbortSignal.abort(reason) })).rejects.toBe(reason);
    expect(token).not.toHaveBeenCalled();
    expect(requests).toHaveLength(0);
  });

  test("returning a changes iterator interrupts pending token resolution", async () => {
    const { baseUrl, requests } = await fixture((_request, response) => json(response, {}));
    const started = Promise.withResolvers();
    const pending = Promise.withResolvers();
    const token = () => { started.resolve(undefined); return /** @type {Promise<string>} */ (pending.promise); };
    const stream = createTerraBaseClient({ project, baseUrl, token }).database("app").changes();
    const next = stream.next();
    await started.promise;
    await expect(stream.return()).resolves.toMatchObject({ done: true });
    await expect(next).resolves.toEqual({ done: true, value: undefined });
    pending.reject(new Error("late provider rejection token-canary"));
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(requests).toHaveLength(0);
  });

  test.each([401, 403])("does not refresh or replay a mutation on %i", async status => {
    const { baseUrl, requests } = await fixture((_request, response) =>
      json(response, { error: { code: status === 401 ? "invalid_key" : "forbidden_scope" } }, status));
    const token = vi.fn(() => first);
    const docs = createTerraBaseClient({ project, baseUrl, token }).database("app").collection("users");
    const error = await docs.patch("01ARZ3NDEKTSV4RRFFQ69G5FA2", { ops: [{ op: "inc", path: "/value", by: 1 }] }).catch(error => error);
    expect(error).toBeInstanceOf(TerraBaseV1Error);
    expect(error.status).toBe(status);
    expect(token).toHaveBeenCalledTimes(1);
    expect(requests).toHaveLength(1);
    expect(requests[0]?.method).toBe("PATCH");
  });

  test("token providers do not change the redirect guard", async () => {
    const { baseUrl, requests } = await fixture((_request, response) =>
      response.writeHead(302, { location: "/credential-leak" }).end());
    const token = vi.fn(() => first);
    const docs = createTerraBaseClient({ project, baseUrl, token }).database("app").collection("users");
    await expect(docs.get("01ARZ3NDEKTSV4RRFFQ69G5FA2")).rejects.toMatchObject({ status: 302 });
    expect(token).toHaveBeenCalledTimes(1);
    expect(requests).toHaveLength(1);
  });
  test.each([["key_revoked", V1KeyRevokedError], ["key_expired", V1KeyExpiredError]])(
    "%s is typed without retrying or resolving the provider again", async (code, type) => {
      const { baseUrl, requests } = await fixture((_request, response) => {
        response.setHeader("www-authenticate", 'Bearer error="invalid_token"');
        json(response, { error: { code, message: "Replace the key." } }, 401);
      });
      const token = vi.fn(() => first);
      const docs = createTerraBaseClient({ project, baseUrl, token }).database("app").collection("users");
      const error = await docs.get("01ARZ3NDEKTSV4RRFFQ69G5FA2").catch(error => error);
      expect(error).toBeInstanceOf(type);
      expect(error).toMatchObject({ status: 401, code });
      expect(error.message).toContain("Replace the key.");
      expect(requests).toHaveLength(1);
      expect(token).toHaveBeenCalledTimes(1);
    });

  test("auth_unavailable retries safe reads while retaining the token", async () => {
    let calls = 0;
    const { baseUrl, requests } = await fixture((_request, response) => {
      if (++calls === 1) {
        response.setHeader("retry-after", "1");
        json(response, { error: { code: "auth_unavailable", message: "Try later." } }, 503);
      } else json(response, document);
    });
    const token = vi.fn(() => first);
    const docs = createTerraBaseClient({ project, baseUrl, token }).database("app").collection("users");
    await expect(docs.get("01ARZ3NDEKTSV4RRFFQ69G5FA2")).resolves.toEqual(document);
    expect(requests).toHaveLength(2);
    expect(token).toHaveBeenCalledTimes(1);
  });

});
