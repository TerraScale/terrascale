import assert from "node:assert/strict";
import { describe, test } from "node:test";

import { createTerraScaleClient } from "./index.js";
import { TerraScaleHttp } from "./http.js";
import { toUlid } from "./ids.js";

const repositoryId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV");
const documentId = toUlid("01BX5ZZKBKACTAV9WEVGEMMVRZ");

describe("TerraScaleHttp request policy", () => {
  test("attaches idempotency keys to all requested mutation methods only", async () => {
    const captured: Request[] = [];
    const http = new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      idempotencyKeyFactory: () => "generated-key",
      fetch: async input => {
        captured.push(input instanceof Request ? input : new Request(input));
        return Response.json({});
      },
    });

    for (const method of ["POST", "PATCH", "PUT", "DELETE", "GET"] as const) {
      await http.request({
        method,
        path: "/api/v1/test",
        routeName: "savedQueries.create",
      });
    }
    await http.request({
      method: "DELETE",
      path: "/api/v1/test",
      routeName: "regions.list",
      idempotencyKey: false,
    });

    assert.deepEqual(
      captured.map(request => request.headers.get("idempotency-key")),
      ["generated-key", "generated-key", "generated-key", "generated-key", null, null],
    );
  });

  test("normalizes explicit base URLs when a custom fetch is configured", () => {
    const http = new TerraScaleHttp({
      apiBaseUrl: " https://api.terrascale.test/// ",
      authBaseUrl: " https://auth.terrascale.test//// ",
      fetch: async () => Response.json({}),
    });

    assert.equal(http.config.apiBaseUrl, "https://api.terrascale.test");
    assert.equal(http.config.authBaseUrl, "https://auth.terrascale.test");
  });

  test("strips every identity header from anonymous token exchange", async () => {
    let captured: Request | undefined;
    const http = new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      projectId: "project" as never,
      accessToken: () => {
        throw new Error("anonymous requests must not resolve an access token");
      },
      defaultHeaders: { authorization: "Bearer configured" },
      fetch: async input => {
        captured = input instanceof Request ? input : new Request(input);
        return Response.json({ accessToken: "issued" });
      },
    });

    const result = await http.request({
      method: "POST",
      path: "/api/v1/auth/password/login",
      routeName: "auth.signIn",
      headerPolicy: "anonymous",
      headers: {
        authorization: "Bearer request",
        "x-terrascale-project-id": "request-project",
      },
      body: { email: "user@example.test", password: "secret" },
    });

    assert.equal(result.ok, true);
    assert.ok(captured);
    for (const name of ["authorization", "x-terrascale-project-id"]) {
      assert.equal(captured.headers.has(name), false, name);
    }
  });

  test("returns preparation failures and isolates per-client metric recorders", async () => {
    const recorded: string[] = [];
    const failing = new TerraScaleHttp({
      apiBaseUrl: "not-a-url",
      requestMetricRecorder: metric => recorded.push(metric.routeName),
      fetch: async () => Response.json({}),
    });
    const isolated = new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      requestMetricRecorder: () => {
        throw new Error("recorder failure");
      },
      fetch: async () => Response.json({ ok: true }),
    });

    const preparationFailure = await failing.request({
      method: "GET",
      path: "/api/v1/regions",
      routeName: "regions.list",
    });
    const successful = await isolated.request({
      method: "GET",
      path: "/api/v1/regions",
      routeName: "regions.list",
    });

    assert.equal(preparationFailure.ok, false);
    assert.equal(preparationFailure.ok ? undefined : preparationFailure.error.kind, "transport");
    assert.equal(successful.ok, true);
    assert.deepEqual(recorded, ["regions.list"]);
  });

  test("rejects request paths that escape the configured origin", async () => {
    let fetches = 0;
    let tokenResolutions = 0;
    const http = new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      accessToken: () => {
        tokenResolutions++;
        return "secret";
      },
      fetch: async () => {
        fetches++;
        return Response.json({});
      },
    });

    for (const path of ["https://attacker.test/capture", "\\\\attacker.test/capture"]) {
      const result = await http.request({
        method: "GET",
        path,
        routeName: "regions.list",
      });

      assert.equal(result.ok, false);
      assert.equal(result.ok ? undefined : result.error.kind, "transport");
    }

    assert.equal(tokenResolutions, 0);
    assert.equal(fetches, 0);
  });

  test("provides a useful message when an error response has no status text", async () => {
    const http = new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () => new Response(null, { status: 500 }),
    });

    const result = await http.request({
      method: "GET",
      path: "/api/v1/regions",
      routeName: "regions.list",
    });

    assert.equal(result.ok, false);
    assert.equal(
      result.ok ? undefined : result.error.message,
      "The TerraScale API request failed.",
    );
  });

  test("retries transient document reads in order and stops on deterministic responses", async () => {
    const statuses = [503, 429, 400];
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      maxRetryAttempts: 2,
      retryDelayMs: 0,
      fetch: async input => {
        requests.push(input instanceof Request ? input : new Request(input));
        const status = statuses[requests.length - 1];
        if (status === undefined) {
          throw new Error("The retry test received more requests than expected.");
        }
        return Response.json(
          { code: `status_${status}`, message: `Response ${status}` },
          { status },
        );
      },
    });

    const result = await client.documents.get({ repositoryId }, documentId);

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.status, 400);
      assert.equal(result.error.kind, "domain");
      assert.equal(result.error.code, "status_400");
    }
    assert.deepEqual(requests.map(request => request.method), ["GET", "GET", "GET"]);
  });

  test("does not retry a non-idempotent document create after a transient failure", async () => {
    let fetchCount = 0;
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      maxRetryAttempts: 2,
      retryDelayMs: 0,
      fetch: async () => {
        fetchCount++;
        return Response.json(
          { code: "temporary_failure", message: "Try again later." },
          { status: 503 },
        );
      },
    });

    const result = await client.documents.create({ repositoryId }, { name: "Ada" });

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.status, 503);
      assert.equal(result.error.kind, "server");
    }
    assert.equal(fetchCount, 1);
  });

  test("returns a transport cancellation when aborting during retry backoff", async () => {
    const controller = new AbortController();
    const abortReason = new Error("caller cancelled the retry");
    let fetchCount = 0;
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      maxRetryAttempts: 1,
      retryDelayMs: 1_000,
      fetch: async () => {
        fetchCount++;
        setTimeout(() => controller.abort(abortReason), 5);
        return Response.json(
          { code: "temporary_failure", message: "Try again later." },
          { status: 503 },
        );
      },
    });

    const result = await client.documents.get(
      { repositoryId },
      documentId,
      { signal: controller.signal },
    );

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.error.kind, "transport");
      assert.equal(result.error.message, abortReason.message);
    }
    assert.equal(fetchCount, 1);
  });
});
