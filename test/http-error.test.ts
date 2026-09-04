import assert from "node:assert/strict";
import { test } from "vitest";

import { TerraScaleHttp } from "../src/http.js";
import type { TerraScaleError } from "../src/result.js";

function httpWith(body: unknown, status = 422, headers?: HeadersInit): TerraScaleHttp {
  return new TerraScaleHttp({
    apiBaseUrl: "https://api.terrascale.test",
    fetch: async () => Response.json(body, { status, headers }),
  });
}

async function requestError(http: TerraScaleHttp): Promise<TerraScaleError> {
  const result = await http.request({
    method: "GET",
    path: "/api/v1/databases/demo/sql",
    routeName: "sql.execute",
  });
  assert.equal(result.ok, false);
  if (result.ok) throw new Error("expected a typed HTTP error");
  return result.error;
}

test("preserves the published nested error fix as a typed public field", async () => {
  const error = await requestError(httpWith({
    error: {
      code: "invalid_query",
      message: "The query is not subscribable.",
      fix: "Use execute() for joins.",
    },
  }));

  assert.equal(error.code, "invalid_query");
  assert.equal(error.message, "The query is not subscribable.");
  assert.equal(error.fix, "Use execute() for joins.");
});

test("keeps flat legacy errors and producer fix compatible", async () => {
  const error = await requestError(httpWith({
    error: "A valid projectId is required.",
    code: "validation_error",
    fix: "Provide a projectId.",
  }, 400));

  assert.equal(error.code, "validation_error");
  assert.equal(error.message, "A valid projectId is required.");
  assert.equal(error.fix, "Provide a projectId.");
});

test("does not treat non-string fixes as public producer metadata", async () => {
  const error = await requestError(httpWith({
    error: { code: "invalid_query", message: "bad query", fix: { url: "https://secret.test" } },
  }));

  assert.equal(error.fix, undefined);
});

test("retains only published request and routing observations alongside a fix", async () => {
  const http = httpWith(
    {
      error: {
        code: "misdirected_request",
        message: "This Region does not host the database.",
        fix: "Retry against the advertised placement.",
      },
    },
    421,
    {
      "x-request-id": "request-123",
      "x-terrascale-placement": "SFO",
      "x-terrascale-operator-host": "node.internal",
    },
  );

  const result = await http.request({
    method: "GET",
    path: "/api/v1/databases/demo",
    routeName: "databases.get",
  });

  assert.equal(result.ok, false);
  if (result.ok) throw new Error("expected a typed HTTP error");
  assert.equal(result.error.fix, "Retry against the advertised placement.");
  assert.equal(result.error.requestId, "request-123");
  assert.equal(result.routing, undefined);
  assert.equal("operatorHost" in result, false);
  assert.equal("url" in result.error, false);
});
