import { expect, test } from "vitest";
import { createTsAuthClient, TsAuthUserPatch } from "./ts-auth.js";
const config = {
  apiBaseUrl: "https://auth.example.test",
  instanceSlug: "example",
};
test("caller cancellation preserves a sanitized cause and cannot dispatch late credentials", async () => {
  let calls = 0;
  /** @type {(token: string) => void} */
  let resolveToken = () => undefined;
  const token = new Promise((resolve) => { resolveToken = resolve; });
  const controller = new AbortController();
  const client = createTsAuthClient({ ...config, accessToken: () => token,
    fetch: async () => { calls++; return Response.json({ data: [] }); } });
  const pending = client.users.update("01ARZ3NDEKTSV4RRFFQ69G5FA1", new TsAuthUserPatch().withName("example"), { signal: controller.signal });
  controller.abort({ reason: "caller cancelled", token: "opaque-secret", url: "https://example.test/?code=private" });
  const result = await pending;
  expect(result.ok).toBe(false);
  if (!result.ok) {
    expect(result.error.code).toBe("request_aborted");
    expect(result.error.details).toEqual({ cause: { reason: "caller cancelled", token: "[redacted]", url: "[redacted URL]" } });
  }
  resolveToken("sk_late_secret");
  await new Promise((resolve) => setImmediate(resolve));
  expect(calls).toBe(0);
});
test("caller TimeoutError and network failure stay distinct from the SDK deadline", async () => {
  const client = createTsAuthClient({ ...config, maxRetryAttempts: 0,
    fetch: async () => { throw new Error("network unavailable"); } });
  const cancelled = await client.oidc.discovery({ signal: AbortSignal.abort(new DOMException("caller", "TimeoutError")) });
  const network = await client.users.list();
  if (cancelled.ok || network.ok) throw new Error("Expected failures.");
  expect(cancelled.error.code).toBe("request_aborted");
  expect(network.error.code).toBe("transport_error");
  expect(cancelled.error.details).toEqual({ cause: { name: "TimeoutError", message: "caller" } });
});
test("hosted credential resolution shares the finite request deadline and cannot dispatch after expiry", async () => {
  let calls = 0;
  const client = createTsAuthClient({
    ...config,
    requestTimeoutMs: 10,
    accessToken: () => new Promise(() => undefined),
    fetch: async () => {
      calls++;
      return Response.json({ data: [] });
    },
  });
  const result = await client.users.list();
  expect(result.ok).toBe(false);
  if (!result.ok) expect(result.error.code).toBe("request_timeout");
  expect(calls).toBe(0);
});
test("noncooperative hosted fetch and body streams remain bounded by cancellation", async () => {
  for (const fetch of [
    async () => /** @type {Promise<Response>} */ (new Promise(() => undefined)),
    async () =>
      new Response(new ReadableStream({ start() {} }), {
        headers: { "content-type": "application/json" },
      }),
  ]) {
    const client = createTsAuthClient({
      ...config,
      requestTimeoutMs: 10,
      fetch,
    });
    const result = await client.users.list();
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.error.code).toBe("request_timeout");
  }
});
test("hosted errors never retain instance keys or arbitrary secret fields", async () => {
  const client = createTsAuthClient({
    ...config,
    accessToken: "sk_test_do_not_echo",
    fetch: async () =>
      Response.json(
        {
          error: {
            code: "denied",
            message: "Bearer sk_test_do_not_echo",
            token: "opaque-secret",
            password: "secret-password",
          },
        },
        { status: 403 },
      ),
  });
  const result = await client.users.list();
  expect(JSON.stringify(result)).not.toMatch(
    /sk_test_do_not_echo|opaque-secret|secret-password/u,
  );
});

test("the private resource Basic option rejects other routes before fetch", async () => {
  const { TsAuthHttp } = await import("./http.js");
  let calls = 0;
  const http = new TsAuthHttp({
    apiBaseUrl: config.apiBaseUrl,
    fetch: async () => {
      calls++;
      throw new Error("No dispatch permitted.");
    },
  });
  for (const routeName of /** @type {const} */ ([
    "tsauth.oidc.token",
    "tsauth.oidc.revoke",
    "tsauth.users.list",
  ])) {
    const result = await http.request({
      method: "POST",
      path: "example/oauth/token",
      routeName,
      formBody: new URLSearchParams({ token: "opaque" }),
      resourceIntrospectionBasic: "Basic dGVzdDp0ZXN0",
    });
    expect(result.ok).toBe(false);
  }
  expect(calls).toBe(0);
});

test("terminal hosted cancellation disposes a late custom-fetch response without retry", async () => {
  // Promise executors run synchronously, so both placeholders are replaced before use.
  /** @type {(response: Response) => void} */
  let delivered = () => undefined;
  /** @type {() => void} */
  let dispatch = () => undefined;
  /** @type {Promise<void>} */
  const started = new Promise((resolve) => {
    dispatch = resolve;
  });
  /** @type {Promise<Response>} */
  const late = new Promise((resolve) => {
    delivered = resolve;
  });
  let calls = 0;
  let cancelled = 0;
  const controller = new AbortController();
  const client = createTsAuthClient({
    ...config,
    maxRetryAttempts: 2,
    fetch: async () => {
      calls++;
      dispatch();
      return late;
    },
  });
  const pending = client.users.delete("01ARZ3NDEKTSV4RRFFQ69G5FA1", { signal: controller.signal });
  await started;
  controller.abort(new Error("Terminal request cancellation."));
  const result = await pending;
  expect(result.ok).toBe(false);
  delivered(
    new Response(
      new ReadableStream({
        cancel: () => {
          cancelled++;
        },
      }),
      { status: 500 },
    ),
  );
  await new Promise((resolve) => {
    setImmediate(resolve);
  });
  expect(calls).toBe(1);
  expect(cancelled).toBe(1);
});

test("strict OAuth envelopes reject duplicate members and lossy numeric spellings", async () => {
  for (const raw of [
    '{"active":true,"active":false}',
    '{"auth_epoch":1.0000000000000001}',
    '{"auth_epoch":9007199254740991.1}',
    '{"auth_epoch":1e0}',
  ]) {
    const client = createTsAuthClient({
      ...config,
      fetch: async () =>
        new Response(raw, { headers: { "content-type": "application/json" } }),
    });
    const result = raw.includes("active")
      ? await client.oidc.introspectResource(
          {
            issuer: "https://issuer.example.test/example",
            audience: "https://api.example.test",
            introspection_client_id: "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            introspection_secret: "secret",
          },
          "opaque",
        )
      : await client.oauthClients.update("01ARZ3NDEKTSV4RRFFQ69G5FAV", {});
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.error.kind).toBe("invalid-response");
  }
});
