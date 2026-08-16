import assert from "node:assert/strict";
import { describe, test } from "node:test";

import { createTerraScaleAdminClient } from "./admin.js";
import { toUlid } from "./ids.js";
import type { BillingReconciliationAttempt } from "./models.js";

const organizationId = toUlid("01ERZ3NDEKTSV4RRFFQ69G5FAV");
const attemptId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV");

const attempt: BillingReconciliationAttempt = {
  attemptId,
  organizationId,
  provider: "stripe",
  state: "Queued",
  requestedAt: "2026-08-16T12:00:00Z",
  enqueuedAt: null,
  startedAt: null,
  finishedAt: null,
  reconciledAt: null,
  errorCode: null,
};

describe("TerraScaleAdminClient billing reconciliation routes", () => {
  test("triggers reconciliation with the exact route, caller key, and accepted response", async () => {
    let captured: Request | undefined;
    const client = createTerraScaleAdminClient({
      apiBaseUrl: "https://api.terrascale.test",
      accessToken: "admin-token",
      fetch: async input => {
        captured = input instanceof Request ? input : new Request(input);
        return Response.json(attempt, { status: 202 });
      },
    });

    const result = await client.triggerBillingReconciliation(organizationId, "reconcile-once");

    assert.ok(captured);
    assert.equal(captured.method, "POST");
    assert.equal(
      new URL(captured.url).pathname,
      `/api/v1/admin/billing/reconciliation/${organizationId}:trigger`,
    );
    assert.equal(captured.headers.get("authorization"), "Bearer admin-token");
    assert.equal(captured.headers.get("idempotency-key"), "reconcile-once");
    assert.equal(captured.headers.get("content-type"), null);
    assert.equal(await captured.clone().text(), "");
    assert.equal(result.ok, true);
    assert.equal(result.ok ? result.status : undefined, 202);
    assert.deepEqual(result.ok ? result.value : undefined, attempt);
  });

  test("gets reconciliation with nullable lifecycle fields and preserves the success status", async () => {
    let captured: Request | undefined;
    const client = createTerraScaleAdminClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async input => {
        captured = input instanceof Request ? input : new Request(input);
        return Response.json(attempt, { status: 200 });
      },
    });

    const result = await client.getBillingReconciliation(organizationId, attemptId);

    assert.ok(captured);
    assert.equal(captured.method, "GET");
    assert.equal(
      new URL(captured.url).pathname,
      `/api/v1/admin/billing/reconciliation/${organizationId}/${attemptId}`,
    );
    assert.equal(captured.headers.get("accept"), "application/json");
    assert.equal(captured.headers.get("idempotency-key"), null);
    assert.equal(await captured.clone().text(), "");
    assert.equal(result.ok, true);
    assert.equal(result.ok ? result.status : undefined, 200);
    assert.deepEqual(result.ok ? result.value : undefined, attempt);
  });

  test("requires a caller idempotency key and uses the shared authorization error pipeline", async () => {
    const client = createTerraScaleAdminClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () =>
        Response.json(
          { error: { code: "forbidden", message: "Owner access is required." } },
          { status: 403 },
        ),
    });

    assert.throws(
      () => client.triggerBillingReconciliation(organizationId, " "),
      /idempotencyKey must be nonblank/,
    );

    const result = await client.getBillingReconciliation(organizationId, attemptId);

    assert.equal(result.ok, false);
    assert.equal(result.ok ? undefined : result.error.kind, "authorization");
    assert.equal(result.ok ? undefined : result.error.code, "forbidden");
    assert.equal(result.ok ? undefined : result.error.message, "Owner access is required.");
  });
});
