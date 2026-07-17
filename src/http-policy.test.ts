import assert from "node:assert/strict";
import { describe, test } from "node:test";

import { TerraScaleHttp } from "./http.js";

describe("TerraScaleHttp request policy", () => {
  test("normalizes explicit base URLs when a custom fetch is configured", () => {
    const http = new TerraScaleHttp({
      apiBaseUrl: " https://api.terrascale.test/// ",
      authBaseUrl: " https://auth.terrascale.test//// ",
      dashboardClientId: "test",
      fetch: async () => Response.json({}),
    });

    assert.equal(http.config.apiBaseUrl, "https://api.terrascale.test");
    assert.equal(http.config.authBaseUrl, "https://auth.terrascale.test");
  });

  test("strips every identity header from anonymous token exchange", async () => {
    let captured: Request | undefined;
    const http = new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      dashboardClientId: "dashboard",
      tenantId: "tenant" as never,
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
        "x-terrascale-dashboard-client-id": "request-dashboard",
        "x-terrascale-project-id": "request-project",
        "x-terrascale-tenant-id": "request-tenant",
      },
      body: { email: "user@example.test", password: "secret" },
    });

    assert.equal(result.ok, true);
    assert.ok(captured);
    for (const name of [
      "authorization",
      "x-terrascale-dashboard-client-id",
      "x-terrascale-project-id",
      "x-terrascale-tenant-id",
    ]) {
      assert.equal(captured.headers.has(name), false, name);
    }
  });

  test("returns preparation failures and isolates per-client metric recorders", async () => {
    const recorded: string[] = [];
    const failing = new TerraScaleHttp({
      apiBaseUrl: "not-a-url",
      dashboardClientId: "test",
      requestMetricRecorder: metric => recorded.push(metric.routeName),
      fetch: async () => Response.json({}),
    });
    const isolated = new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      dashboardClientId: "test",
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

  test("provides a useful message when an error response has no status text", async () => {
    const http = new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      dashboardClientId: "test",
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
});
