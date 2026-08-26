// Contract consistency tests: the studio contract must carry the engine's
// closed vocabulary and stay internally coherent, because the typed client is
// generated from it.
//
// Run with: vp run test:unit

import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const studioRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const contract = JSON.parse(
  await readFile(resolve(studioRoot, "contract", "terrabase-api.contract.json"), "utf8"),
);

test("FamilyKind carries the engine's closed family kind set", async () => {
  // The wire names are the reserved family record keys from
  // src/families/state.rs (family_key) — the salvage-era contract shipped only
  // 6 of 10 kinds; this pins the full closed set.
  const engineFamilyKeys = [
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
  const declared = contract.types.FamilyKind.values;
  assert.deepEqual([...declared].sort(), [...engineFamilyKeys].sort());
});

test("operation vocabulary mirrors the engine PublicOperation set", async () => {
  // src/operations.rs PublicOperation: DocumentRead, DocumentWrite, Query,
  // Family(Read|Write) — plus the host-level engine.status read.
  const names = contract.operations.map((operation) => operation.name);
  assert.deepEqual(names.sort(), [
    "document.read",
    "document.write",
    "engine.status",
    "family.read",
    "family.write",
    "query.execute",
  ]);
  for (const operation of contract.operations) {
    assert.equal(operation.path, `/api/v2/operations/${operation.name}`);
  }
});

test("every subscription payload type is declared", async () => {
  for (const subscription of contract.subscriptions) {
    for (const type of Object.values(subscription.payloads)) {
      assert.ok(contract.types[type], `${subscription.name} references undeclared type ${type}`);
    }
    for (const frame of subscription.frames) {
      assert.ok(
        subscription.payloads[frame],
        `${subscription.name} frame ${frame} has no payload type`,
      );
    }
  }
});

test("every operation request/response type is declared (or none)", async () => {
  for (const operation of contract.operations) {
    for (const side of [operation.request, operation.response]) {
      if (side.type !== "none") {
        assert.ok(
          contract.types[side.type],
          `${operation.name} references undeclared type ${side.type}`,
        );
      }
    }
  }
});

test("frame digest format is pinned to sha256:<hex>", async () => {
  assert.match(contract.envelope.frame.fields.digest, /^'sha256:<hex>'/);
});

test("session bootstrap is host-enforced with a typed unauthenticated error", async () => {
  assert.equal(contract.session.enforcedBy, "host");
  assert.equal(contract.session.unauthenticated.errorCode, "session.unauthenticated");
  assert.equal(contract.session.unauthenticated.httpStatus, 401);
  const loginUrl = contract.types.ApiError.fields.find((field) => field.name === "loginUrl");
  assert.ok(loginUrl?.optional, "ApiError.loginUrl must be optional");
});

test("no credential-shaped field names exist anywhere in the contract", async () => {
  const serialized = JSON.stringify(contract).toLowerCase();
  for (const banned of ["password", "secret", "apikey", "access_key", "bearer"]) {
    assert.ok(!serialized.includes(banned), `contract mentions banned field ${banned}`);
  }
});
