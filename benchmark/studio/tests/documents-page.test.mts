// Unit tests for the DB-03 documents page seam: the shared query vocabulary
// (canonical query texts, typed row parsing, pretty-JSON truncation, bounded
// pagination windows, filters) and the documents fixture extension that stands
// in for the fixture core's pre-SE-08 gap — per-collection query.execute
// listings — without modifying DB-02's fixture server core.
//
// Run with: node --test tests/documents-page.test.mts
// (`vp run unit:test` enumerates DB-02's three test files only; adding this
// file to that task's command needs a vite.config.ts edit outside this
// ticket's write scope — recorded as a collision action.)

import assert from "node:assert/strict";
import test from "node:test";

import {
  COLLECTIONS_QUERY_TEXT,
  DOCUMENT_PAGE_SIZE,
  MAX_LOADED_PAGES,
  PREVIEW_CHAR_LIMIT,
  PRETTY_JSON_CHAR_LIMIT,
  documentPageQueryText,
  documentPreview,
  filterCollections,
  filterDocuments,
  paginationStatus,
  parseCollectionSummaries,
  parseDocumentRows,
  prettyJsonTruncated,
} from "../src/components/documents/vocabulary.ts";
import { createDocumentsFixtureServer } from "../app/documents/fixtures/query-listing-fixture.mjs";

async function withDocumentsFixture(run) {
  const documents = createDocumentsFixtureServer({ host: "127.0.0.1" });
  await documents.ready();
  await new Promise((resolve) => documents.server.listen(0, "127.0.0.1", resolve));
  const { port } = documents.server.address();
  const baseUrl = `http://127.0.0.1:${port}`;
  try {
    await run(baseUrl, documents);
  } finally {
    documents.state.dispose();
    documents.server.closeAllConnections();
    documents.server.close();
    documents.coreState.dispose();
    documents.coreServer.closeAllConnections();
    documents.coreServer.close();
  }
}

async function callOperation(
  baseUrl,
  op,
  request,
  { version = 2, correlationId = "unit-db03-1" } = {},
) {
  const response = await fetch(`${baseUrl}/api/v2/operations/${op}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      v: version,
      op,
      correlationId,
      causationId: null,
      idempotencyKey: null,
      request,
    }),
  });
  return { status: response.status, body: await response.json() };
}

async function controlDocuments(baseUrl, body) {
  const response = await fetch(`${baseUrl}/api/v2/_fixture/documents-control`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  assert.equal(response.status, 200);
  return response.json();
}

async function seedDocument(baseUrl, collection, id, value) {
  const outcome = await callOperation(baseUrl, "document.write", {
    document: { id, collection, value },
    operationId: `seed-${collection}-${id}`,
  });
  assert.equal(
    outcome.status,
    200,
    `seed ${collection}/${id} failed: ${JSON.stringify(outcome.body)}`,
  );
  return outcome.body.result;
}

test("the canonical collections query is the contract's v1 catalog text", () => {
  // The conformance vector `query-v1-ok` fixes the catalog vocabulary the
  // fixture already serves; the documents page must speak exactly it.
  assert.equal(COLLECTIONS_QUERY_TEXT, "FROM collections");
});

test("per-collection pages ride the v1 text grammar with explicit LIMIT/OFFSET", () => {
  assert.equal(
    documentPageQueryText("studio-seed", { limit: 10, offset: 0 }),
    "FROM studio-seed LIMIT 10 OFFSET 0",
  );
  assert.equal(
    documentPageQueryText("parity", { limit: 25, offset: 25 }),
    "FROM parity LIMIT 25 OFFSET 25",
  );
  assert.throws(() => documentPageQueryText("  ", { limit: 10, offset: 0 }), /collection/);
});

test("catalog rows parse into typed collection summaries, malformed rows are counted", () => {
  const result = {
    rows: [
      {
        document: {
          id: "catalog:alpha",
          collection: "__catalog__",
          version: 1,
          value: { name: "alpha", documents: 3, sizeBytes: 768 },
        },
        projected: [],
      },
      {
        document: {
          id: "catalog:beta",
          collection: "__catalog__",
          version: 1,
          value: { name: "beta", documents: 0, sizeBytes: 0 },
        },
        projected: [],
      },
      {
        document: { id: "catalog:bad", collection: "__catalog__", version: 1, value: { name: 7 } },
        projected: [],
      },
    ],
    matched: 3,
    staleness: [],
  };
  const parsed = parseCollectionSummaries(result);
  assert.deepEqual(
    parsed.summaries.map((summary) => summary.name),
    ["alpha", "beta"],
  );
  assert.equal(parsed.summaries[0]?.documents, 3);
  assert.equal(parsed.malformed, 1);
});

test("query rows parse into typed document records, malformed rows are counted", () => {
  const result = {
    rows: [
      {
        document: { id: "doc-1", collection: "c", version: 4, value: { a: 1 } },
        projected: [],
      },
      { document: { id: 2, collection: "c", version: 1, value: null }, projected: [] },
      { document: { id: "doc-3", collection: "c", version: -1, value: null }, projected: [] },
    ],
    matched: 3,
    staleness: [],
  };
  const parsed = parseDocumentRows(result);
  assert.equal(parsed.documents.length, 1);
  assert.equal(parsed.documents[0]?.id, "doc-1");
  assert.equal(parsed.documents[0]?.version, 4);
  assert.equal(parsed.malformed, 2);
});

test("pretty JSON truncates at an explicit character limit with exact counts", () => {
  const small = prettyJsonTruncated({ kind: "studio-seed", label: "first" });
  assert.equal(small.truncated, false);
  assert.equal(small.text, JSON.stringify({ kind: "studio-seed", label: "first" }, null, 2));
  assert.equal(small.totalChars, small.text.length);

  const largeValue = { label: "large", blob: "x".repeat(PRETTY_JSON_CHAR_LIMIT * 3) };
  const full = JSON.stringify(largeValue, null, 2);
  const large = prettyJsonTruncated(largeValue);
  assert.equal(large.truncated, true);
  assert.equal(large.totalChars, full.length);
  assert.equal(large.text.length, PRETTY_JSON_CHAR_LIMIT);
  assert.equal(large.text, full.slice(0, PRETTY_JSON_CHAR_LIMIT));
});

test("document previews are single-line and bounded", () => {
  const preview = documentPreview({
    kind: "parity-check",
    source: "v1",
    pad: "y".repeat(PREVIEW_CHAR_LIMIT * 2),
  });
  assert.ok(!preview.includes("\n"), "preview must be single-line");
  assert.ok(preview.length <= PREVIEW_CHAR_LIMIT + 1, "preview must be bounded");
  assert.ok(preview.endsWith("…"));
});

test("filters are case-insensitive substring matches and empty queries match all", () => {
  const summaries = [
    { name: "studio-seed", documents: 2, sizeBytes: 512 },
    { name: "parity", documents: 1, sizeBytes: 256 },
  ];
  assert.deepEqual(
    filterCollections(summaries, "").map((s) => s.name),
    ["studio-seed", "parity"],
  );
  assert.deepEqual(
    filterCollections(summaries, "STUDIO").map((s) => s.name),
    ["studio-seed"],
  );

  const documents = [
    { id: "browse-001", collection: "browse-demo", version: 1, value: null },
    { id: "seed-001", collection: "studio-seed", version: 3, value: null },
  ];
  assert.deepEqual(
    filterDocuments(documents, "").map((d) => d.id),
    ["browse-001", "seed-001"],
  );
  assert.deepEqual(
    filterDocuments(documents, "BROWSE").map((d) => d.id),
    ["browse-001"],
  );
});

test("pagination status distinguishes more, complete, and the bounded window cap", () => {
  const more = paginationStatus({
    loadedRows: 10,
    matched: 23,
    pageSize: DOCUMENT_PAGE_SIZE,
    maxPages: MAX_LOADED_PAGES,
  });
  assert.equal(more.kind, "more");
  assert.equal(more.nextPageOffset, 10);

  const complete = paginationStatus({
    loadedRows: 23,
    matched: 23,
    pageSize: DOCUMENT_PAGE_SIZE,
    maxPages: MAX_LOADED_PAGES,
  });
  assert.equal(complete.kind, "complete");

  const capped = paginationStatus({
    loadedRows: MAX_LOADED_PAGES * DOCUMENT_PAGE_SIZE,
    matched: 9999,
    pageSize: DOCUMENT_PAGE_SIZE,
    maxPages: MAX_LOADED_PAGES,
  });
  assert.equal(capped.kind, "window-capped");
  assert.equal(capped.retainedPages, MAX_LOADED_PAGES);
});

test("the documents fixture proxies the catalog to the unmodified fixture core", async () => {
  await withDocumentsFixture(async (baseUrl) => {
    const outcome = await callOperation(baseUrl, "query.execute", {
      input: { kind: "v1", text: COLLECTIONS_QUERY_TEXT },
    });
    assert.equal(outcome.status, 200);
    const names = parseCollectionSummaries(outcome.body.result).summaries.map((s) => s.name);
    assert.ok(names.includes("studio-seed"), "seeded core collection is listed");
    assert.ok(names.includes("parity"), "parity core collection is listed");
  });
});

test("typed writes through the documents fixture list back per collection in ascending id order", async () => {
  await withDocumentsFixture(async (baseUrl) => {
    await seedDocument(baseUrl, "browse-demo", "browse-002", { n: 2 });
    await seedDocument(baseUrl, "browse-demo", "browse-001", { n: 1 });
    await seedDocument(baseUrl, "browse-demo", "browse-003", { n: 3 });

    const read = await callOperation(baseUrl, "document.read", {
      collection: "browse-demo",
      documentId: "browse-001",
    });
    assert.equal(read.status, 200, "writes are forwarded to the core and read back");
    assert.equal(read.body.result.document.id, "browse-001");

    const page = await callOperation(baseUrl, "query.execute", {
      input: { kind: "v1", text: documentPageQueryText("browse-demo", { limit: 10, offset: 0 }) },
    });
    assert.equal(page.status, 200);
    assert.equal(page.body.result.matched, 3);
    assert.deepEqual(
      parseDocumentRows(page.body.result).documents.map((d) => d.id),
      ["browse-001", "browse-002", "browse-003"],
    );
  });
});

test("per-collection listings page through LIMIT/OFFSET windows deterministically", async () => {
  await withDocumentsFixture(async (baseUrl) => {
    for (let index = 1; index <= 23; index += 1) {
      await seedDocument(baseUrl, "browse-demo", `browse-${String(index).padStart(3, "0")}`, {
        n: index,
      });
    }
    const first = await callOperation(baseUrl, "query.execute", {
      input: { kind: "v1", text: documentPageQueryText("browse-demo", { limit: 10, offset: 0 }) },
    });
    const second = await callOperation(baseUrl, "query.execute", {
      input: { kind: "v1", text: documentPageQueryText("browse-demo", { limit: 10, offset: 10 }) },
    });
    const third = await callOperation(baseUrl, "query.execute", {
      input: { kind: "v1", text: documentPageQueryText("browse-demo", { limit: 10, offset: 20 }) },
    });
    const past = await callOperation(baseUrl, "query.execute", {
      input: { kind: "v1", text: documentPageQueryText("browse-demo", { limit: 10, offset: 30 }) },
    });

    assert.equal(first.body.result.matched, 23);
    assert.deepEqual(
      parseDocumentRows(first.body.result).documents.map((d) => d.id),
      Array.from({ length: 10 }, (_, i) => `browse-${String(i + 1).padStart(3, "0")}`),
    );
    assert.deepEqual(
      parseDocumentRows(second.body.result).documents.map((d) => d.id),
      Array.from({ length: 10 }, (_, i) => `browse-${String(i + 11).padStart(3, "0")}`),
    );
    assert.deepEqual(
      parseDocumentRows(third.body.result).documents.map((d) => d.id),
      ["browse-021", "browse-022", "browse-023"],
    );
    assert.deepEqual(parseDocumentRows(past.body.result).documents, []);
    assert.equal(past.body.result.matched, 23);

    const repeat = await callOperation(baseUrl, "query.execute", {
      input: { kind: "v1", text: documentPageQueryText("browse-demo", { limit: 10, offset: 0 }) },
    });
    assert.deepEqual(repeat.body.result, first.body.result, "identical queries answer identically");
  });
});

test("the unmodified fixture core answers per-collection listings with an empty match set", async () => {
  // Documents the pre-SE-08 state the collection route renders honestly as
  // its empty state on the shared fixture: the core serves only the catalog.
  await withDocumentsFixture(async (baseUrl, documents) => {
    const onCore = await callOperation(documents.coreBaseUrl, "query.execute", {
      input: { kind: "v1", text: documentPageQueryText("studio-seed", { limit: 10, offset: 0 }) },
    });
    assert.equal(onCore.status, 200);
    assert.equal(onCore.body.result.matched, 0);
    assert.deepEqual(onCore.body.result.rows, []);
  });
});

test("the documents fixture fails closed on envelope discipline like the core", async () => {
  await withDocumentsFixture(async (baseUrl) => {
    const badVersion = await callOperation(
      baseUrl,
      "query.execute",
      { input: { kind: "v1", text: "FROM collections" } },
      { version: 1 },
    );
    assert.equal(badVersion.status, 400);
    assert.equal(badVersion.body.error.code, "protocol.version-unsupported");

    const response = await fetch(`${baseUrl}/api/v2/operations/query.execute`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        v: 2,
        op: "engine.status",
        correlationId: "unit-db03-2",
        request: {},
      }),
    });
    const mismatch = await response.json();
    assert.equal(response.status, 400);
    assert.equal(mismatch.error.code, "protocol.envelope-invalid");
  });
});

test("query failure under fixture control answers a typed retryable error", async () => {
  await withDocumentsFixture(async (baseUrl) => {
    await controlDocuments(baseUrl, { queryBehavior: "fail" });
    try {
      const outcome = await callOperation(baseUrl, "query.execute", {
        input: { kind: "v1", text: COLLECTIONS_QUERY_TEXT },
      });
      assert.equal(outcome.status, 503);
      assert.equal(outcome.body.error.code, "query.unavailable");
      assert.equal(outcome.body.error.retryable, true);
      assert.equal(outcome.body.correlationId, "unit-db03-1");
    } finally {
      await controlDocuments(baseUrl, { queryBehavior: "ok" });
    }
  });
});

test("the session gate forwards through the documents fixture control", async () => {
  await withDocumentsFixture(async (baseUrl) => {
    await controlDocuments(baseUrl, { behavior: "session-gate" });
    try {
      const gated = await callOperation(baseUrl, "query.execute", {
        input: { kind: "v1", text: COLLECTIONS_QUERY_TEXT },
      });
      assert.equal(gated.status, 401);
      assert.equal(gated.body.error.code, "session.unauthenticated");
      assert.equal(gated.body.error.loginUrl, "/fixture/login");
    } finally {
      await controlDocuments(baseUrl, { behavior: "session-open" });
    }
  });
});

test("query and read delays are observable under fixture control", async () => {
  await withDocumentsFixture(async (baseUrl) => {
    await controlDocuments(baseUrl, { queryDelayMs: 250, readDelayMs: 250 });
    try {
      let startedAt = Date.now();
      const query = await callOperation(baseUrl, "query.execute", {
        input: { kind: "v1", text: COLLECTIONS_QUERY_TEXT },
      });
      assert.equal(query.status, 200);
      assert.ok(Date.now() - startedAt >= 240, "catalog queries honor the delay");

      startedAt = Date.now();
      const read = await callOperation(baseUrl, "document.read", {
        collection: "studio-seed",
        documentId: "seed-001",
      });
      assert.equal(read.status, 200);
      assert.ok(Date.now() - startedAt >= 240, "document reads honor the delay");
    } finally {
      await controlDocuments(baseUrl, { queryDelayMs: 0, readDelayMs: 0 });
    }
  });
});
