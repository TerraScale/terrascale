// Unit tests for the queries and indexes view models (DB-05).
//
// The pure models in src/components/queries/ carry the page's contract at a
// testable seam: input normalization, paging, query metadata derivation, and
// index status classification. They are plain TypeScript with no imports, so
// node:test loads them through Node's type stripping.
//
// Run with: node --test tests/queries.test.mjs
// (wired into `vp run unit:test` by the integrator; see the ticket's
// collision actions — vite.config.ts is outside this packet's write scope)

import assert from "node:assert/strict";
import test from "node:test";

import {
  fixtureIndexCatalogRows,
  fixtureQueryResultWithStaleness,
  fixtureTypedErrors,
  startIndexCatalogHost,
} from "./fixtures/queries-fixtures.ts";
import {
  buildIndexCatalog,
  CATALOG_QUERY_TEXT,
  classifyIndexStatus,
  INDEX_STATUS_LABEL,
} from "../src/components/queries/index-catalog-model.ts";
import {
  clampPage,
  DEFAULT_PAGE_SIZE,
  errorOutcome,
  executionOutcomeFromResult,
  normalizePageSize,
  normalizeQueryText,
  PAGE_SIZES,
  pagerDescriptor,
  slicePage,
} from "../src/components/queries/query-console-model.ts";

test("query text is trimmed and empty text is rejected before dispatch", () => {
  const accepted = normalizeQueryText("  from studio-seed  ");
  assert.equal(accepted.ok, true);
  assert.equal(accepted.ok && accepted.text, "from studio-seed");

  for (const empty of ["", "   ", "\t\n"]) {
    const rejected = normalizeQueryText(empty);
    assert.equal(rejected.ok, false, `expected ${JSON.stringify(empty)} to be rejected`);
    assert.equal(rejected.ok === false && rejected.reason, "empty");
  }
});

test("page sizes are bounded to the closed set with a default", () => {
  assert.deepEqual(PAGE_SIZES, [5, 10, 25]);
  assert.equal(DEFAULT_PAGE_SIZE, 10);
  assert.equal(normalizePageSize(null), DEFAULT_PAGE_SIZE);
  assert.equal(normalizePageSize(5), 5);
  // Out-of-set and non-finite sizes fall back to the default, never to an
  // unbounded page.
  assert.equal(normalizePageSize(3), DEFAULT_PAGE_SIZE);
  assert.equal(normalizePageSize(-1), DEFAULT_PAGE_SIZE);
  assert.equal(normalizePageSize(Number.POSITIVE_INFINITY), DEFAULT_PAGE_SIZE);
});

test("paging math derives bounded pages over the returned rows", () => {
  const rows = Array.from({ length: 12 }, (_, index) => ({ index }));
  const descriptor = pagerDescriptor(rows.length, 5, 2);
  assert.equal(descriptor.rowCount, 12);
  assert.equal(descriptor.pageSize, 5);
  assert.equal(descriptor.pageCount, 3);
  assert.equal(descriptor.firstRowIndex, 6);
  assert.equal(descriptor.lastRowIndex, 10);
  assert.equal(descriptor.hasPrev, true);
  assert.equal(descriptor.hasNext, true);
  assert.deepEqual(slicePage(rows, 5, 2), rows.slice(5, 10));
});

test("the last page and the empty result page stay in bounds", () => {
  const last = pagerDescriptor(12, 5, 3);
  assert.equal(last.lastRowIndex, 12);
  assert.equal(last.hasNext, false);

  const empty = pagerDescriptor(0, 10, 1);
  assert.equal(empty.pageCount, 1);
  assert.equal(empty.rowCount, 0);
  assert.equal(empty.firstRowIndex, null);
  assert.equal(empty.lastRowIndex, null);
  assert.deepEqual(slicePage([], 10, 1), []);

  // A page past the end clamps back inside instead of rendering nothing.
  assert.equal(clampPage(9, 3), 3);
  assert.equal(clampPage(0, 3), 1);
  assert.equal(clampPage(2, 3), 2);
});

test("the execution outcome carries matched, returned rows, and timing as one payload", () => {
  const outcome = executionOutcomeFromResult(fixtureQueryResultWithStaleness, {
    queryText: "from studio-seed",
    elapsedMs: 12,
    executedAt: "2026-08-26T12:00:00.000Z",
  });
  assert.equal(outcome.kind, "results");
  assert.equal(outcome.queryText, "from studio-seed");
  assert.equal(outcome.rows.length, 2);
  assert.equal(outcome.matched, 2);
  assert.equal(outcome.returnedRows, 2);
  assert.equal(outcome.elapsedMs, 12);
  assert.equal(outcome.executedAt, "2026-08-26T12:00:00.000Z");
});

test("staleness observations reach the operator verbatim, never reworded", () => {
  const outcome = executionOutcomeFromResult(fixtureQueryResultWithStaleness, {
    queryText: "from studio-seed",
    elapsedMs: 1,
    executedAt: "2026-08-26T12:00:00.000Z",
  });
  assert.deepEqual(outcome.staleness, fixtureQueryResultWithStaleness.staleness);
  for (const observation of fixtureQueryResultWithStaleness.staleness) {
    const found = outcome.staleness.find((entry) => entry.indexId === observation.indexId);
    assert.ok(found, `observation ${observation.indexId} missing`);
    assert.equal(found.reason, observation.reason);
  }
});

test("typed error outcomes carry the host's code, message, and retryability verbatim", () => {
  for (const payload of Object.values(fixtureTypedErrors)) {
    const outcome = errorOutcome(payload);
    assert.equal(outcome.kind, "error");
    assert.equal(outcome.code, payload.code);
    assert.equal(outcome.message, payload.message);
    assert.equal(outcome.retryable, payload.retryable);
  }
  // Missing identity fields still render as typed failures, never as blanks.
  const bare = errorOutcome({ code: "", message: "", retryable: false });
  assert.equal(bare.code, "error.code-missing");
});

test("index statuses classify stale and rebuilding distinctly", () => {
  assert.equal(classifyIndexStatus("stale"), "stale");
  assert.equal(classifyIndexStatus("rebuilding"), "rebuilding");
  assert.equal(classifyIndexStatus("fresh"), "fresh");
  assert.notEqual(INDEX_STATUS_LABEL.stale, INDEX_STATUS_LABEL.rebuilding);
  assert.equal(INDEX_STATUS_LABEL.stale, "Stale");
  assert.equal(INDEX_STATUS_LABEL.rebuilding, "Rebuilding");
});

test("unknown index statuses stay unknown and are never coerced into a known state", () => {
  for (const unrecognized of ["warming-up", "", null, undefined, 7, {}, "STALE "]) {
    assert.equal(
      classifyIndexStatus(unrecognized),
      "unknown",
      `expected ${JSON.stringify(unrecognized)} to classify as unknown`,
    );
  }
  assert.equal(INDEX_STATUS_LABEL.unknown, "Unknown");
});

test("the index catalog parses rows, counts malformed rows, and never drops them silently", () => {
  const catalog = buildIndexCatalog(fixtureIndexCatalogRows);
  assert.equal(catalog.entries.length, 4);
  assert.equal(catalog.unparsedCount, 1);

  const byStatus = new Map(catalog.entries.map((entry) => [entry.status, entry]));
  assert.equal(byStatus.get("stale").namespace, "Auto/studio-seed|by-label");
  assert.equal(
    byStatus.get("stale").reason,
    "observed generation 41 is newer than the used generation 39",
  );
  assert.equal(byStatus.get("rebuilding").namespace, "Auto/studio-seed|by-kind");
  assert.equal(byStatus.get("fresh").namespace, "orders|by-status");
  assert.equal(byStatus.get("fresh").reason, null);
  assert.equal(byStatus.get("unknown").namespace, "events|by-ts");
  // The unrecognized status travels with the entry so the operator sees the
  // host's own value.
  assert.equal(byStatus.get("unknown").rawStatus, "warming-up");

  assert.deepEqual(catalog.statusCounts, { fresh: 1, rebuilding: 1, stale: 1, unknown: 1 });
});

test("an empty catalog read is an explicit empty state, not a silent pass", () => {
  const catalog = buildIndexCatalog([]);
  assert.equal(catalog.entries.length, 0);
  assert.equal(catalog.unparsedCount, 0);
  assert.deepEqual(catalog.statusCounts, { fresh: 0, rebuilding: 0, stale: 0, unknown: 0 });
});

test("the catalog read is one typed query operation with a fixed, visible query text", () => {
  // The catalog view rides the typed query.execute operation (the contract
  // has no dedicated index catalog operation yet). The text is a constant the
  // UI shows the operator, so the read is never a hidden channel.
  assert.equal(typeof CATALOG_QUERY_TEXT, "string");
  assert.equal(CATALOG_QUERY_TEXT.trim(), CATALOG_QUERY_TEXT);
  assert.match(CATALOG_QUERY_TEXT, /^from __index_catalog__$/);
});

test("the index catalog stand-in host answers through the contract envelope", async () => {
  const host = await startIndexCatalogHost();
  try {
    const response = await fetch(`${host.url}/api/v2/operations/query.execute`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        v: 2,
        op: "query.execute",
        correlationId: "unit-catalog-1",
        causationId: null,
        idempotencyKey: null,
        request: { input: { kind: "v1", text: "from __index_catalog__" } },
      }),
    });
    assert.equal(response.status, 200);
    const body = await response.json();
    assert.equal(body.v, 2);
    assert.equal(body.op, "query.execute");
    assert.equal(body.correlationId, "unit-catalog-1");
    // The stand-in serves the catalog rows and staleness observations verbatim
    // from the fixture payloads, so the view's distinct status labels are
    // provable end to end against contract-shaped envelopes.
    assert.deepEqual(body.result.rows, fixtureIndexCatalogRows);
    assert.deepEqual(body.result.staleness, fixtureQueryResultWithStaleness.staleness);
    assert.equal(body.result.matched, fixtureIndexCatalogRows.length);

    const unknown = await fetch(`${host.url}/api/v2/operations/document.read`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        v: 2,
        op: "document.read",
        correlationId: "unit-catalog-2",
        request: {},
      }),
    });
    assert.equal(unknown.status, 404);
    const unknownBody = await unknown.json();
    assert.equal(unknownBody.error.code, "operation.unknown");
  } finally {
    await host.close();
  }
});
