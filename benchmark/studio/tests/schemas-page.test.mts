// Unit tests for the SM-04 schemas page seam (TerraBase/TerraBaseV2#28): the
// shared schema vocabulary (the closed Schema Migration state set and the
// validation modes mirrored from the engine and the TBEP registry, typed
// state parsing, the collection-bound read discipline) and the schemas
// fixture extension that stands in for the fixture core's pre-integration
// gap — `schema.status` — by composing the UNMODIFIED DB-02 core with exactly
// that operation served, the same shape the families fixture (DB-06) and the
// topology route namespace (DB-07) established.
//
// Run with: node --test tests/schemas-page.test.mts
// (also wired into `vp run unit:test` by this ticket.)

import assert from "node:assert/strict";
import test from "node:test";
import { readFile, readdir } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import {
  MIGRATION_STATE_KINDS,
  MIGRATION_STATE_LABELS,
  SCHEMA_COLLECTIONS_BOUND,
  SCHEMA_LIVE_SEAM,
  SCHEMA_LIVE_SEAM_LABEL,
  VALIDATION_MODES,
  parseSchemaStateResult,
  schemasHref,
} from "../src/components/schemas/vocabulary.ts";
import {
  SCHEMA_FIXTURE_COLLECTIONS,
  createSchemasFixtureServer,
} from "../app/schemas/fixtures/schemas-demo.mjs";

const studioRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = resolve(studioRoot, "..");
const recordSource = await readFile(
  join(repoRoot, "src", "families", "migrations", "record.rs"),
  "utf8",
);
const registry = JSON.parse(
  await readFile(
    join(repoRoot, "crates", "terrabase-tbep", "schema", "tbep-registry.json"),
    "utf8",
  ),
);

// ---------------------------------------------------------------------------
// Engine grounding: the migration-state vocabulary is the engine's, extracted
// from source and from the TBEP registry — never re-typed from memory.
// ---------------------------------------------------------------------------

test("MIGRATION_STATE_KINDS mirrors the engine MigrationStatus variants and the TBEP registry enum", () => {
  // src/families/migrations/record.rs: `pub enum MigrationStatus` arms.
  const enumBody = recordSource.slice(
    recordSource.indexOf("pub enum MigrationStatus"),
    recordSource.indexOf("pub(crate) fn status_of"),
  );
  const arms = [...enumBody.matchAll(/^    (None|InProgress|RollingBack|Complete)[ ,{]/gm)].map(
    (found) => found[1],
  );
  assert.deepEqual(arms, ["None", "InProgress", "RollingBack", "Complete"]);
  const wireNames = {
    None: "none",
    InProgress: "in_progress",
    RollingBack: "rolling_back",
    Complete: "complete",
  };
  assert.deepEqual(
    MIGRATION_STATE_KINDS,
    arms.map((arm) => wireNames[arm]),
  );

  // The same closed set is the TBEP registry's migration_state_kind enum —
  // the wire the C# host and the CLI already speak.
  const registryEnum = registry.enums.find((entry) => entry.name === "migration_state_kind");
  assert.ok(registryEnum, "the registry declares migration_state_kind");
  assert.deepEqual(
    registryEnum.variants.map((variant) => variant.name),
    [...MIGRATION_STATE_KINDS],
  );
});

test("the migration-state labels are the engine's doc comments, verbatim", () => {
  // Variant docs of pub enum MigrationStatus (src/families/migrations/record.rs).
  assert.match(recordSource, /\/\/\/ No migration record exists for the collection\./);
  assert.equal(MIGRATION_STATE_LABELS.none, "No migration record exists for the collection.");
  assert.match(recordSource, /\/\/\/ A forward drive is applying pages\./);
  assert.equal(MIGRATION_STATE_LABELS.in_progress, "A forward drive is applying pages.");
  assert.match(recordSource, /\/\/\/ A rollback drive is restoring pages\./);
  assert.equal(MIGRATION_STATE_LABELS.rolling_back, "A rollback drive is restoring pages.");
  assert.match(recordSource, /\/\/\/ A migration completed with the recorded receipt\./);
  assert.equal(MIGRATION_STATE_LABELS.complete, "A migration completed with the recorded receipt.");
});

test("VALIDATION_MODES is the recorded-policy vocabulary: enforced or none", () => {
  assert.deepEqual(VALIDATION_MODES, ["enforced", "none"]);
});

// ---------------------------------------------------------------------------
// Typed state parsing: fail closed on anything that is not the contract's
// SchemaStateResult shape; never coerce, never guess.
// ---------------------------------------------------------------------------

test("parseSchemaStateResult recognizes each migration state", () => {
  const cases = [
    {
      name: "enforced with a completed drive",
      result: {
        collection: "users",
        enforcedVersion: 2,
        ruleCount: 3,
        validation: "enforced",
        migrationKind: "complete",
        migrationManifest: "sha256:0123abcd",
        migrationFromVersion: 1,
        migrationToVersion: 2,
        migrationDocuments: 128,
        migrationTransformed: 128,
        migrationPages: 3,
      },
      expect: {
        enforcedVersion: 2,
        migrationKind: "complete",
        drive: { manifest: "sha256:0123abcd", fromVersion: 1, toVersion: 2 },
      },
    },
    {
      name: "enforced with an in-progress drive",
      result: {
        collection: "events",
        enforcedVersion: 1,
        ruleCount: 2,
        validation: "enforced",
        migrationKind: "in_progress",
        migrationManifest: "sha256:4567",
        migrationFromVersion: 1,
        migrationToVersion: 2,
        migrationDocuments: 4096,
        migrationTransformed: 1024,
        migrationPages: 2,
      },
      expect: {
        enforcedVersion: 1,
        migrationKind: "in_progress",
        drive: { manifest: "sha256:4567", fromVersion: 1, toVersion: 2 },
      },
    },
    {
      name: "enforced with a rolling-back drive",
      result: {
        collection: "audit",
        enforcedVersion: 3,
        ruleCount: 4,
        validation: "enforced",
        migrationKind: "rolling_back",
        migrationManifest: "sha256:89ab",
        migrationFromVersion: 2,
        migrationToVersion: 3,
        migrationPages: 1,
      },
      expect: {
        enforcedVersion: 3,
        migrationKind: "rolling_back",
        drive: { manifest: "sha256:89ab", fromVersion: 2, toVersion: 3 },
      },
    },
    {
      name: "schema-free with no drive",
      result: {
        collection: "studio-seed",
        ruleCount: 0,
        validation: "none",
        migrationKind: "none",
      },
      expect: { enforcedVersion: null, migrationKind: "none" },
    },
  ];
  for (const { name, result, expect } of cases) {
    const parsed = parseSchemaStateResult(result);
    assert.equal(parsed.kind, "recognized", name);
    if (parsed.kind !== "recognized") {
      continue;
    }
    assert.equal(parsed.malformed, 0, name);
    assert.equal(parsed.state.collection, result.collection, name);
    assert.equal(parsed.state.enforcedVersion, expect.enforcedVersion, name);
    assert.equal(parsed.state.migrationKind, expect.migrationKind, name);
    if (expect.drive === undefined) {
      assert.equal(parsed.state.migration, null, name);
    } else {
      assert.deepEqual(
        {
          manifest: parsed.state.migration?.manifest,
          fromVersion: parsed.state.migration?.fromVersion,
          toVersion: parsed.state.migration?.toVersion,
        },
        expect.drive,
        name,
      );
    }
  }
});

test("parseSchemaStateResult fails closed on drift", () => {
  const unrecognized = [
    "not an object",
    { collection: 7, ruleCount: 0, validation: "none", migrationKind: "none" },
    { collection: "users", ruleCount: 3, validation: "enforced", migrationKind: "none" },
    {
      collection: "users",
      enforcedVersion: 0,
      ruleCount: 3,
      validation: "enforced",
      migrationKind: "none",
    },
    {
      collection: "users",
      enforcedVersion: 1,
      ruleCount: 3,
      validation: "sometimes",
      migrationKind: "none",
    },
    {
      collection: "users",
      enforcedVersion: 1,
      ruleCount: 3,
      validation: "enforced",
      migrationKind: "paused",
    },
    {
      collection: "users",
      enforcedVersion: 1,
      ruleCount: -1,
      validation: "enforced",
      migrationKind: "none",
    },
    {
      collection: "users",
      ruleCount: 0,
      validation: "none",
      migrationKind: "none",
      enforcedVersion: 2,
    },
    {
      collection: "users",
      enforcedVersion: 1,
      ruleCount: 3,
      validation: "enforced",
      migrationKind: "complete",
      migrationManifest: 5,
    },
  ];
  for (const value of unrecognized) {
    assert.deepEqual(
      parseSchemaStateResult(value),
      { kind: "unrecognized" },
      `expected unrecognized for ${JSON.stringify(value)}`,
    );
  }
});

test("parseSchemaStateResult counts malformed optional facts instead of dropping them silently", () => {
  const parsed = parseSchemaStateResult({
    collection: "users",
    enforcedVersion: 1,
    ruleCount: 3,
    validation: "enforced",
    migrationKind: "in_progress",
    migrationManifest: "sha256:1",
    migrationFromVersion: "one",
    migrationToVersion: 2,
    migrationDocuments: 10,
    migrationTransformed: "many",
    migrationPages: 1,
  });
  assert.equal(parsed.kind, "recognized");
  if (parsed.kind !== "recognized") {
    return;
  }
  assert.equal(parsed.malformed, 2);
  assert.equal(parsed.state.migration?.fromVersion, null);
  assert.equal(parsed.state.migration?.documents, 10);
});

// ---------------------------------------------------------------------------
// Route vocabulary: the collection bound and the href grammar.
// ---------------------------------------------------------------------------

test("schemasHref addresses the page and one selected collection", () => {
  assert.equal(schemasHref(), "/schemas");
  assert.equal(schemasHref("users"), "/schemas?collection=users");
  assert.equal(schemasHref("a b"), "/schemas?collection=a%20b");
});

test("the collections bound is finite and disclosed", () => {
  assert.ok(Number.isInteger(SCHEMA_COLLECTIONS_BOUND) && SCHEMA_COLLECTIONS_BOUND > 0);
});

// ---------------------------------------------------------------------------
// The DB-04 live seam: named and typed, never built here.
// ---------------------------------------------------------------------------

test("the live seam names DB-04's subscription pattern and its landing state", () => {
  assert.equal(SCHEMA_LIVE_SEAM.owner, "DB-04");
  assert.equal(SCHEMA_LIVE_SEAM.carrier, "contract subscriptions (SSE)");
  assert.equal(SCHEMA_LIVE_SEAM.landed, false);
  assert.ok(SCHEMA_LIVE_SEAM.note.length > 0);
  assert.ok(SCHEMA_LIVE_SEAM_LABEL.length > 0);
});

// ---------------------------------------------------------------------------
// Route wiring: the page subtree, its engine-verbatim labels, and the nav
// link (the shell's one shared registration root — additive only).
// ---------------------------------------------------------------------------

test("the schemas route subtree is complete and linked from the studio navigation", async () => {
  const subtree = await readdir(join(studioRoot, "app", "schemas"), { recursive: true });
  const subtreeNames = subtree.map((entry) => entry.split("/").pop());
  for (const expected of ["page.tsx", "data.ts", "error.tsx", "loading.tsx"]) {
    assert.ok(subtreeNames.includes(expected), `app/schemas/${expected} is missing`);
  }
  const components = await readdir(join(studioRoot, "src", "components", "schemas"));
  assert.ok(
    components.includes("vocabulary.ts"),
    "src/components/schemas/vocabulary.ts is missing",
  );

  const nav = await readFile(
    join(studioRoot, "src", "components", "shell", "StudioNav.tsx"),
    "utf8",
  );
  assert.match(nav, /href: "\/schemas"/);
  assert.match(nav, /label: "Schemas"/);
  const planned = nav.match(/const PLANNED = \[([^\]]*)\]/)?.[1] ?? "";
  assert.ok(!planned.includes("Schemas"), "Schemas must leave the planned list");
});

test("page copy uses the schema model's vocabulary, engine-verbatim where it labels states", async () => {
  const page = await readFile(join(studioRoot, "app", "schemas", "page.tsx"), "utf8");
  const panel = await readFile(
    join(studioRoot, "src", "components", "schemas", "SchemaStatePanel.tsx"),
    "utf8",
  );
  // The page names the two concepts the schema model owns (CONTEXT.md's
  // Document Schema and Schema Migration capitalization).
  assert.ok(page.includes("Document Schema"), "the term Document Schema must appear");
  assert.ok(page.includes("Schema Migration"), "the term Schema Migration must appear");
  // The panel's migration hint labels come from the engine-verbatim map
  // (guarded above), never a paraphrase in the component source.
  assert.ok(
    panel.includes("MIGRATION_STATE_LABELS"),
    "the panel labels states through the engine-verbatim map",
  );
  assert.ok(
    !panel.includes("No migration record exists"),
    "the panel does not re-type the engine's docs out of band",
  );
  // The page states honestly that migration driving is a backend action.
  assert.ok(page.includes("backend-operator actions"));
});

// ---------------------------------------------------------------------------
// The fixture extension: the unmodified core composed with schema.status.
// ---------------------------------------------------------------------------

async function withSchemasFixture(run) {
  const fixture = createSchemasFixtureServer({ host: "127.0.0.1" });
  await fixture.ready();
  await new Promise((resolveListen) => fixture.server.listen(0, "127.0.0.1", resolveListen));
  const { port } = fixture.server.address();
  const baseUrl = `http://127.0.0.1:${port}`;
  try {
    await run(baseUrl, fixture);
  } finally {
    fixture.state.dispose();
    fixture.server.closeAllConnections();
    fixture.server.close();
    fixture.coreServer.closeAllConnections();
    fixture.coreServer.close();
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

test("schema.status answers the seeded states through the composed fixture", async () => {
  await withSchemasFixture(async (baseUrl) => {
    for (const [name, seed] of Object.entries(SCHEMA_FIXTURE_COLLECTIONS)) {
      const { status, body } = await callOperation(baseUrl, "schema.status", {
        collection: name,
      });
      assert.equal(status, 200, `${name}: ${JSON.stringify(body)}`);
      assert.equal(body.result.collection, name);
      assert.equal(body.result.validation, seed.validation);
      assert.equal(body.result.migrationKind, seed.migrationKind);
      assert.equal(body.result.enforcedVersion ?? null, seed.enforcedVersion ?? null);
    }
    // Every state of the closed set is seeded somewhere.
    const kinds = new Set(
      Object.values(SCHEMA_FIXTURE_COLLECTIONS).map((seed) => seed.migrationKind),
    );
    for (const kind of MIGRATION_STATE_KINDS) {
      assert.ok(kinds.has(kind), `the fixture seeds a ${kind} collection`);
    }
  });
});

test("an unknown collection answers schema-free, matching the engine's absent-record read", async () => {
  await withSchemasFixture(async (baseUrl) => {
    const { status, body } = await callOperation(baseUrl, "schema.status", {
      collection: "never-declared",
    });
    assert.equal(status, 200);
    assert.deepEqual(body.result, {
      collection: "never-declared",
      ruleCount: 0,
      validation: "none",
      migrationKind: "none",
    });
  });
});

test("schema.status keeps the shared envelope discipline", async () => {
  await withSchemasFixture(async (baseUrl) => {
    // Non-POST.
    const get = await fetch(`${baseUrl}/api/v2/operations/schema.status`);
    assert.equal(get.status, 405);
    assert.equal((await get.json()).error.code, "method.not-allowed");

    // Wrong envelope version.
    const version = await callOperation(
      baseUrl,
      "schema.status",
      { collection: "users" },
      { version: 3 },
    );
    assert.equal(version.status, 400);
    assert.equal(version.body.error.code, "protocol.version-unsupported");

    // Op/route mismatch.
    const mismatch = await callOperation(
      baseUrl,
      "schema.status",
      { collection: "users" },
      { opOverride: "schema.validate" },
    );
    assert.equal(mismatch.status, 400);
    assert.equal(mismatch.body.error.code, "protocol.envelope-invalid");

    // Unknown operation inside the route namespace.
    const unknown = await callOperation(baseUrl, "schema.revalidate", { collection: "users" });
    assert.equal(unknown.status, 404);
    assert.equal(unknown.body.error.code, "operation.unknown");

    // Malformed request body.
    const malformed = await callOperation(baseUrl, "schema.status", { collection: 7 });
    assert.equal(malformed.status, 400);
    assert.equal(malformed.body.error.code, "request.invalid");
  });
});

test("the composed fixture fronts the unmodified core for every other operation", async () => {
  await withSchemasFixture(async (baseUrl, fixture) => {
    // The core still answers through the wrapper.
    const status = await callOperation(baseUrl, "engine.status", {});
    assert.equal(status.status, 200);
    assert.equal(status.body.result.engineId, "fixture-engine-01");

    // The demo collections were seeded through the core's own typed
    // document.write, so the collections catalog the page reads lists them.
    const catalog = await callOperation(baseUrl, "query.execute", {
      input: { kind: "v1", text: "FROM collections" },
    });
    assert.equal(catalog.status, 200);
    const names = catalog.body.result.rows.map((row) => row.document.value.name);
    for (const name of Object.keys(SCHEMA_FIXTURE_COLLECTIONS)) {
      assert.ok(names.includes(name), `the catalog lists ${name}`);
    }

    // The core's schema.status (the honest pre-integration state) is a
    // designed typed refusal, not a silent proxy.
    const core = await callOperation(`${fixture.coreBaseUrl}`, "schema.status", {
      collection: "users",
    });
    assert.equal(core.status, 404);
    assert.equal(core.body.error.code, "operation.unknown");
  });
});
