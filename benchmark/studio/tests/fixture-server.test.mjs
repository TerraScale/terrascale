// Unit tests for the fixture server's operation envelope behaviors — the
// fail-closed rules below the conformance vectors, plus the regression cases
// fixed alongside the DB-02 salvage adoption (idempotent write replay).
//
// Run with: vp run test:unit

import assert from "node:assert/strict";
import test from "node:test";

import { createFixtureServer } from "../scripts/fixture-server.mjs";

async function withFixture(options, run) {
  const { server, state } = createFixtureServer({ host: "127.0.0.1", ...options });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address();
  const baseUrl = `http://127.0.0.1:${port}`;
  try {
    await run(baseUrl);
  } finally {
    state.dispose();
    server.closeAllConnections();
    server.close();
  }
}

async function callOperation(baseUrl, op, request, { version = 2, opOverride = null } = {}) {
  const response = await fetch(`${baseUrl}/api/v2/operations/${op}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      v: version,
      op: opOverride ?? op,
      correlationId: "unit-correlation-1",
      causationId: null,
      idempotencyKey: null,
      request,
    }),
  });
  return { status: response.status, body: await response.json() };
}

test("operations reject non-POST methods", async () => {
  await withFixture({}, async (baseUrl) => {
    const response = await fetch(`${baseUrl}/api/v2/operations/engine.status`);
    const body = await response.json();
    assert.equal(response.status, 405);
    assert.equal(body.error.code, "method.not-allowed");
  });
});

test("operations reject non-JSON bodies", async () => {
  await withFixture({}, async (baseUrl) => {
    const response = await fetch(`${baseUrl}/api/v2/operations/engine.status`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: "not json",
    });
    const body = await response.json();
    assert.equal(response.status, 400);
    assert.equal(body.error.code, "protocol.envelope-invalid");
  });
});

test("operations reject an empty correlationId", async () => {
  await withFixture({}, async (baseUrl) => {
    const response = await fetch(`${baseUrl}/api/v2/operations/engine.status`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ v: 2, op: "engine.status", correlationId: "", request: {} }),
    });
    const body = await response.json();
    assert.equal(response.status, 400);
    assert.equal(body.error.code, "protocol.envelope-invalid");
  });
});

test("document.write replays the original receipt for a repeated operationId", async () => {
  await withFixture({}, async (baseUrl) => {
    const request = {
      document: { id: "unit-001", collection: "unit-tests", value: { n: 1 } },
      operationId: "unit-operation-001",
    };
    const first = await callOperation(baseUrl, "document.write", request);
    assert.equal(first.status, 200);
    const second = await callOperation(baseUrl, "document.write", request);
    assert.equal(second.status, 200);
    // Idempotent replay returns the SAME durable receipt, not a new version.
    assert.deepEqual(second.body.result, first.body.result);
    assert.equal(second.body.result.version, first.body.result.version);
  });
});

test("document.write honors the expectedVersion precondition", async () => {
  await withFixture({}, async (baseUrl) => {
    const request = {
      document: { id: "unit-002", collection: "unit-tests", value: { n: 1 } },
      operationId: "unit-operation-002",
    };
    const created = await callOperation(baseUrl, "document.write", request);
    assert.equal(created.status, 200);
    const version = created.body.result.version;

    const stale = await callOperation(baseUrl, "document.write", {
      ...request,
      operationId: "unit-operation-003",
      expectedVersion: version + 5,
    });
    assert.equal(stale.status, 409);
    assert.equal(stale.body.error.code, "write.conflict");

    const matching = await callOperation(baseUrl, "document.write", {
      ...request,
      operationId: "unit-operation-004",
      expectedVersion: version,
    });
    assert.equal(matching.status, 200);
    assert.equal(matching.body.result.version, version + 1);
  });
});

test("document.read fails closed on invalid input shapes", async () => {
  await withFixture({}, async (baseUrl) => {
    const invalid = await callOperation(baseUrl, "document.read", {
      collection: "",
      documentId: "x",
    });
    assert.equal(invalid.status, 400);
    assert.equal(invalid.body.error.code, "request.invalid");
  });
});

test("family operations fail closed on kinds outside the closed set", async () => {
  await withFixture({}, async (baseUrl) => {
    const read = await callOperation(baseUrl, "family.read", { kind: "nope", scope: "s" });
    assert.equal(read.status, 400);
    assert.equal(read.body.error.code, "family.kind-unknown");
    const write = await callOperation(baseUrl, "family.write", {
      kind: "nope",
      scope: "s",
      operation: {},
      operationId: "f1",
    });
    assert.equal(write.status, 400);
    assert.equal(write.body.error.code, "family.kind-unknown");
  });
});

test("every engine family kind is accepted by family.read", async () => {
  await withFixture({}, async (baseUrl) => {
    // The closed set from src/families/state.rs (family_key), including the
    // kinds the salvage-era contract was missing.
    const kinds = [
      "attachments",
      "counters",
      "time-series",
      "revisions",
      "compare-exchange",
      "identities",
      "lifecycle",
      "archival",
      "validation",
      "tombstones",
    ];
    for (const kind of kinds) {
      const read = await callOperation(baseUrl, "family.read", { kind, scope: "unit" });
      assert.equal(read.status, 200, `kind ${kind} rejected`);
      assert.equal(read.body.result.kind, kind);
    }
  });
});

test("the session gate answers typed operations with session.unauthenticated and a loginUrl", async () => {
  await withFixture({}, async (baseUrl) => {
    await fetch(`${baseUrl}/api/v2/_fixture/control`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ behavior: "session-gate" }),
    });
    const gated = await callOperation(baseUrl, "engine.status", {});
    assert.equal(gated.status, 401);
    assert.equal(gated.body.error.code, "session.unauthenticated");
    assert.equal(gated.body.error.retryable, false);
    assert.equal(gated.body.error.loginUrl, "/fixture/login");

    const login = await fetch(new URL("/fixture/login", baseUrl).toString());
    assert.equal(login.status, 200);
    assert.match(await login.text(), /data-fixture="host-login"/);
  });
});

test("the session gate lifts without restarting the fixture", async () => {
  await withFixture({}, async (baseUrl) => {
    const control = (body) =>
      fetch(`${baseUrl}/api/v2/_fixture/control`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
      });
    await control({ behavior: "session-gate" });
    const gated = await callOperation(baseUrl, "engine.status", {});
    assert.equal(gated.status, 401);
    await control({ behavior: "session-open" });
    const open = await callOperation(baseUrl, "engine.status", {});
    assert.equal(open.status, 200);
    assert.equal(open.body.result.engineId, "fixture-engine-01");
  });
});
