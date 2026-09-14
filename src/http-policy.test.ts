import assert from "node:assert/strict";
import { once } from "node:events";
import { createServer } from "node:http";
import { describe, test } from "vitest";

import { createTerraScaleClient } from "./index.js";
import { TerraScaleHttp } from "./http.js";

const projectId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const databaseId = "01BX5ZZKBKACTAV9WEVGEMMVRZ";

describe("TerraScaleHttp request policy", () => {
  test("native transport replays identical mutation bytes under one idempotency key", async () => {
    const document = { counter: 1, nested: { label: "original" } };
    const bodies: string[] = [];
    const keys: (string | string[] | undefined)[] = [];
    const server = createServer(async (request, response) => {
      const chunks: Buffer[] = [];
      for await (const chunk of request) chunks.push(Buffer.from(chunk));
      bodies.push(Buffer.concat(chunks).toString("utf8"));
      keys.push(request.headers["idempotency-key"]);
      if (bodies.length === 1) {
        document.counter = 2;
        document.nested.label = "changed while pending";
        response.writeHead(503, { "content-type": "application/json", "retry-after": "0" });
        response.end('{"code":"temporarily_unavailable"}');
      } else {
        response.writeHead(200, { "content-type": "application/json" });
        response.end('{"revision":"stored"}');
      }
    });
    server.listen(0, "127.0.0.1");
    await once(server, "listening");
    const address = server.address();
    assert.ok(address && typeof address === "object");
    try {
      const client = createTerraScaleClient({
        apiBaseUrl: `http://127.0.0.1:${address.port}`, maxRetryAttempts: 1, retryDelayMs: 0,
      });
      const result = await client.documents.put("demo", "records", "counter", document, {
        idempotencyKey: "stable-counter-update",
      });
      assert.ok(result.ok);
      assert.deepEqual(bodies, [
        '{"counter":1,"nested":{"label":"original"}}',
        '{"counter":1,"nested":{"label":"original"}}',
      ]);
      assert.deepEqual(keys, ["stable-counter-update", "stable-counter-update"]);
    } finally {
      server.closeAllConnections();
      await new Promise<void>(resolve => server.close(() => resolve()));
    }
  });

  test("cancels while the credential provider is pending without dispatching a request", async () => {
    const controller = new AbortController();
    let resolveToken!: (token: string) => void;
    let started!: () => void;
    const providerStarted = new Promise<void>(resolve => { started = resolve; });
    const http = new TerraScaleHttp({
      apiBaseUrl: "http://127.0.0.1:1",
      accessToken: () => {
        started();
        return new Promise<string>(resolve => { resolveToken = resolve; });
      },
    });
    const pending = http.request({
      method: "GET", path: "/api/v1/regions", routeName: "regions.list", signal: controller.signal,
    });
    await providerStarted;
    controller.abort(new Error("cancelled credential wait"));
    const result = await pending;
    assert.equal(result.ok, false);
    if (!result.ok) assert.equal(result.error.message, "cancelled credential wait");
    resolveToken("late-token");
  });

  test("an already cancelled request never invokes the credential provider", async () => {
    let calls = 0;
    const http = new TerraScaleHttp({
      apiBaseUrl: "http://127.0.0.1:1",
      accessToken: () => { calls++; return "token"; },
    });
    const result = await http.request({
      method: "GET", path: "/api/v1/regions", routeName: "regions.list", signal: AbortSignal.abort(),
    });
    assert.equal(result.ok, false);
    assert.equal(calls, 0);
  });

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
      fetch: async () => Response.json({}),
    });

    assert.equal(http.config.apiBaseUrl, "https://api.terrascale.test");
  });

  test("protects credentials from insecure or secret-bearing API endpoints", () => {
    const fetch = async () => Response.json({});
    assert.throws(
      () => new TerraScaleHttp({ apiBaseUrl: "http://api.example.com", fetch }),
      /must use HTTPS/u,
    );
    assert.throws(
      () => new TerraScaleHttp({ apiBaseUrl: "https://user:secret@api.example.com", fetch }),
      /cannot contain credentials/u,
    );
    assert.throws(
      () => new TerraScaleHttp({ apiBaseUrl: "https://api.example.com/?access_token=secret", fetch }),
      /secret-bearing query/u,
    );
    assert.doesNotThrow(
      () => new TerraScaleHttp({ apiBaseUrl: "http://127.0.0.1:8787", fetch }),
    );
  });

  test("attaches bearer and project identity headers to API requests", async () => {
    let captured: Request | undefined;
    const http = new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      projectId: "project" as never,
      accessToken: "configured",
      fetch: async input => {
        captured = input instanceof Request ? input : new Request(input);
        return Response.json({});
      },
    });

    const result = await http.request({
      method: "GET",
      path: "/api/v1/regions",
      routeName: "regions.list",
    });

    assert.equal(result.ok, true);
    assert.ok(captured);
    assert.equal(captured.headers.get("authorization"), "Bearer configured");
    assert.equal(captured.headers.get("x-terrascale-project-id"), "project");
  });

  test("sends typed tenant scope and create-only PUT preconditions without header overrides", async () => {
    let captured: Request | undefined;
    const client = createTerraScaleClient({
      tenantId: "authorized-tenant", projectId: projectId,
      accessToken: "scoped-token",
      fetch: async input => {
        captured = new Request(input);
        return Response.json({ revision: "created" });
      },
    });
    const result = await client.documents.put("demo", "records", "admission", { pending: true }, {
      ifNoneMatch: "*", idempotencyKey: "stable-admission",
    });
    assert.ok(result.ok);
    assert.equal(captured?.headers.get("x-terrascale-tenant-id"), "authorized-tenant");
    assert.equal(captured?.headers.get("x-terrascale-project-id"), projectId);
    assert.equal(captured?.headers.get("if-none-match"), "*");
    assert.equal(captured?.headers.get("idempotency-key"), "stable-admission");
    assert.throws(() => client.documents.put("demo", "records", "admission", {}, {
      ifNoneMatch: "*", ifMatch: '"existing"',
    }), /cannot be combined/);
    assert.throws(() => client.documents.patch("demo", "records", "admission", {}, {
      ifNoneMatch: "*",
    }), /only for document PUT/);
  });

  test("uses the named access-token credential option", async () => {
    let captured: Request | undefined;
    const http = new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      accessToken: "project-token",
      fetch: async input => {
        captured = input instanceof Request ? input : new Request(input);
        return Response.json({});
      },
    });

    const result = await http.request({
      method: "GET",
      path: "/api/v1/regions",
      routeName: "regions.list",
    });

    assert.equal(result.ok, true);
    assert.equal(captured?.headers.get("authorization"), "Bearer project-token");
  });

  test("rejects caller-selected headers before resolving credentials or sending a request", async () => {
    let fetches = 0;
    let credentialsResolved = 0;
    const http = new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      accessToken: () => {
        credentialsResolved++;
        return "secret";
      },
      fetch: async () => {
        fetches++;
        return Response.json({});
      },
    });

    const result = await http.request({
      method: "GET",
      path: "/api/v1/regions",
      routeName: "regions.list",
      headers: { authorization: "Bearer caller-selected" },
    });

    assert.equal(result.ok, false);
    if (!result.ok) assert.equal(result.error.kind, "transport");
    assert.equal(credentialsResolved, 0);
    assert.equal(fetches, 0);
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

  test("preserves flat error messages used by existing API routes", async () => {
    const http = new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () =>
        Response.json(
          {
            error: "A valid projectId is required.",
            code: "validation_error",
            traceId: "legacy-support-id",
          },
          { status: 400 },
        ),
    });

    const result = await http.request({
      method: "GET",
      path: "/api/v1/projects/invalid/usage",
      routeName: "usage.getProjectUsage",
    });

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.error.message, "A valid projectId is required.");
      assert.equal(result.error.code, "validation_error");
      assert.equal(result.error.traceId, "legacy-support-id");
      assert.equal(result.error.requestId, "legacy-support-id");
    }
  });

  test("retries transient idempotent-keyed writes in order and stops on deterministic responses", async () => {
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

    const result = await client.backups.executeBackup(
      databaseId,
      { operationSessionId: "backup-operation-session", confirmationToken: "backup-confirmation-token" },
      "backup-execution-key",
      projectId,
    );

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.status, 400);
      assert.equal(result.error.kind, "domain");
      assert.equal(result.error.code, "status_400");
    }
    assert.deepEqual(requests.map(request => request.method), ["POST", "POST", "POST"]);
  });

  test("rejects browser opaque redirects and preserves conditional 304 reads", async () => {
    const opaque = new Response(null);
    Object.defineProperty(opaque, "type", { value: "opaqueredirect" });
    for (const response of [opaque, new Response(null, { status: 308 })]) {
      const http = new TerraScaleHttp({ fetch: async () => response });
      const result = await http.request({ method: "GET", path: "/api/v1/regions", routeName: "regions.list" });
      assert.ok(!result.ok);
      assert.match(result.error.message, /unsupported HTTP redirect/);
    }
    const http = new TerraScaleHttp({ fetch: async () => new Response(null, { status: 304 }) });
    const result = await http.request({ method: "GET", path: "/api/v1/regions", routeName: "regions.list" });
    assert.ok(result.ok);
    assert.equal(result.status, 304);
  });

  test("native fetch refuses redirects before credentials or mutation bodies reach another endpoint", async () => {
    let targetRequests = 0;
    const target = createServer((_request, response) => {
      targetRequests++;
      response.writeHead(200, { "content-type": "application/json" });
      response.end("{}");
    });
    target.listen(0, "127.0.0.1");
    await once(target, "listening");
    const targetAddress = target.address();
    assert.ok(targetAddress && typeof targetAddress === "object");
    const source = createServer((request, response) => {
      response.writeHead(request.method === "POST" ? 307 : 302, {
        location: `http://127.0.0.1:${targetAddress.port}/api/v1/regions`,
      });
      response.end();
    });
    source.listen(0, "127.0.0.1");
    await once(source, "listening");
    const sourceAddress = source.address();
    assert.ok(sourceAddress && typeof sourceAddress === "object");
    try {
      const http = new TerraScaleHttp({
        apiBaseUrl: `http://127.0.0.1:${sourceAddress.port}`, accessToken: "private-credential",
      });
      for (const method of ["GET", "POST"] as const) {
        const result = await http.request({
          method, path: "/api/v1/regions", routeName: "regions.list",
          ...(method === "POST" ? { body: { privateValue: "mutation" } } : {}),
        });
        assert.ok(!result.ok);
        assert.equal(result.error.kind, "transport");
      }
      assert.equal(targetRequests, 0);
    } finally {
      source.closeAllConnections();
      target.closeAllConnections();
      await Promise.all([
        new Promise<void>(resolve => source.close(() => resolve())),
        new Promise<void>(resolve => target.close(() => resolve())),
      ]);
    }
  });

  test("preserves export artifact bytes and rejects an unexpected successful media type", async () => {
    const bytes = new Uint8Array([0, 255, 128, 10, 13, 195, 40]);
    const http = new TerraScaleHttp({
      fetch: async input => {
        assert.equal(new Request(input).headers.get("accept"), "application/octet-stream");
        return new Response(bytes, { headers: { "content-type": "application/octet-stream" } });
      },
    });
    const result = await http.request<Uint8Array>({
      method: "GET", path: "/api/v1/databases/demo/exports/job/artifacts/member",
      routeName: "databases.downloadExport",
    });
    assert.ok(result.ok);
    assert.deepEqual(result.value, bytes);
    const wrongType = new TerraScaleHttp({ fetch: async () => Response.json({}) });
    const rejected = await wrongType.request({
      method: "GET", path: "/api/v1/databases/demo/exports/job/artifacts/member",
      routeName: "databases.downloadExport",
    });
    assert.ok(!rejected.ok);
    assert.equal(rejected.error.code, "invalid_response");
  });

  test("returns server backoff beyond the automatic retry window without retrying early", async () => {
    for (const retryAfter of ["60", new Date(Date.now() + 120_000).toUTCString()]) {
      let requests = 0;
      const http = new TerraScaleHttp({
        maxRetryAttempts: 1,
        retryDelayMs: 0,
        fetch: async () => {
          requests++;
          return Response.json({ code: "overloaded", message: "Retry later." }, {
            status: 429, headers: { "retry-after": retryAfter },
          });
        },
      });
      const result = await http.request({ method: "GET", path: "/api/v1/regions", routeName: "regions.list" });
      assert.equal(result.ok, false);
      assert.equal(result.status, 429);
      assert.equal(result.headers?.get("retry-after"), retryAfter);
      assert.equal(requests, 1);
    }
  });

  test("does not retry a non-idempotent organization create after a transient failure", async () => {
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

    const result = await client.organizations.create({ name: "Ada" });

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.status, 503);
      assert.equal(result.error.kind, "server");
    }
    assert.equal(fetchCount, 1);
  });

});
