// Unit tests for the DB-06 data-families page seam (TerraBase/TerraBaseV2#20):
// the shared family vocabulary (the closed FamilyKind set mirrored from the
// engine, the pre-SE-08 family scope-selector grammar, typed state parsing,
// bounded entry paging, the time-series range view bound, and the engine's
// counter/time-series/attachment semantics labels) and the families fixture
// extension that stands in for the fixture core's pre-SE-08 gap — per-document
// family.read state — without modifying DB-02's fixture server core.
//
// Run with: node --test tests/families-page.test.mts
// (`vp run unit:test` enumerates the landed test files only; adding this file
// to that task's command needs a vite.config.ts edit outside this ticket's
// write scope — recorded as a collision action.)

import assert from "node:assert/strict";
import test from "node:test";
import { readFile, readdir } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import {
  ATTACHMENT_DUPLICATE_SEMANTICS,
  ATTACHMENT_STORE_SEMANTICS,
  COUNTER_DELETE_SEMANTICS,
  COUNTER_INCREMENT_SEMANTICS,
  FAMILY_ENTRY_PAGE_SIZE,
  FAMILY_KINDS,
  MAX_ENTRY_WINDOW_LIMIT,
  MAX_LOADED_ENTRY_PAGES,
  NOT_YET_VIEWABLE_FAMILY_KINDS,
  NOT_YET_VIEWABLE_LABEL,
  TIME_SERIES_RANGE_SEMANTICS,
  TIME_SERIES_RANGE_VIEW_BOUND_MS,
  VIEWABLE_FAMILY_KINDS,
  attachmentEntriesScope,
  counterEntriesScope,
  documentScope,
  entryPagingStatus,
  familiesHref,
  parseEpochMsInput,
  parseAttachmentFamilyState,
  parseCounterFamilyState,
  parseTimeSeriesCatalogState,
  parseTimeSeriesPageState,
  rangeSpanMs,
  rangeWithinViewBound,
  timeSeriesCatalogScope,
  timeSeriesEntriesScope,
} from "../src/components/families/vocabulary.ts";
import { createFamiliesFixtureServer } from "../app/families/fixtures/families-demo.mjs";

const studioRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = resolve(studioRoot, "..");
const familiesStateSource = await readFile(join(repoRoot, "src", "families", "state.rs"), "utf8");
const payloadsSource = await readFile(join(repoRoot, "src", "families", "payloads.rs"), "utf8");

// ---------------------------------------------------------------------------
// Engine grounding: the closed FamilyKind set and the semantics labels are the
// engine's, extracted from source and compared — never re-typed from memory.
// ---------------------------------------------------------------------------

test("FAMILY_KINDS mirrors the engine family_key set verbatim, in order", () => {
  const arms = [...familiesStateSource.matchAll(/FamilyKind::(\w+) => "([a-z-]+)",/g)].map(
    (found) => found[2],
  );
  assert.equal(arms.length, 10, "the engine family_key match has ten arms");
  assert.deepEqual(FAMILY_KINDS, arms);
});

test("the viewable/not-yet-viewable partition is exactly the closed kind set", () => {
  assert.deepEqual(VIEWABLE_FAMILY_KINDS, ["attachments", "counters", "time-series"]);
  assert.deepEqual(
    NOT_YET_VIEWABLE_FAMILY_KINDS,
    [
      "revisions",
      "compare-exchange",
      "identities",
      "lifecycle",
      "archival",
      "validation",
      "tombstones",
    ].sort(),
  );
  const union = [...VIEWABLE_FAMILY_KINDS, ...NOT_YET_VIEWABLE_FAMILY_KINDS].sort();
  assert.deepEqual(union, [...FAMILY_KINDS].sort(), "no kind is missing or invented");
  assert.equal(NOT_YET_VIEWABLE_LABEL, "Not yet viewable");
});

test("the semantics labels are the engine's, verbatim", () => {
  // CounterOperation::Increment doc (src/families/payloads.rs).
  assert.match(payloadsSource, /\/\/\/ Add a signed delta to a counter, creating it from zero\./);
  assert.equal(
    COUNTER_INCREMENT_SEMANTICS,
    "Add a signed delta to a counter, creating it from zero.",
  );
  // CounterOperation::Delete doc.
  assert.match(payloadsSource, /\/\/\/ Delete a counter by name\./);
  assert.equal(COUNTER_DELETE_SEMANTICS, "Delete a counter by name.");
  // TimeSeriesRange struct doc.
  assert.match(payloadsSource, /\/\/\/ An inclusive timestamp range in epoch milliseconds\./);
  assert.equal(TIME_SERIES_RANGE_SEMANTICS, "An inclusive timestamp range in epoch milliseconds.");
  // AttachmentMutation::Store doc (both sentences).
  assert.match(payloadsSource, /\/\/\/ Record attachment metadata\./);
  assert.equal(ATTACHMENT_STORE_SEMANTICS, "Record attachment metadata.");
  assert.match(
    payloadsSource,
    /A name that already exists is a duplicate\n\s*\/\/\/ unless `replace` is set\./,
  );
  assert.equal(
    ATTACHMENT_DUPLICATE_SEMANTICS,
    "A name that already exists is a duplicate unless `replace` is set.",
  );
});

// ---------------------------------------------------------------------------
// Scope-selector grammar: the pre-SE-08 way one typed family.read addresses a
// document family window (the typed request carries kind + scope strings only).
// ---------------------------------------------------------------------------

test("document scopes are canonical, encoded, and shared by the viewable kinds", () => {
  assert.equal(documentScope("studio-seed", "seed-001"), "doc/studio-seed/seed-001");
  assert.equal(
    documentScope("ops/demo", "doc#1"),
    "doc/ops%2Fdemo/doc%231",
    "path separators and fragments are encoded into one segment each",
  );
  assert.equal(
    counterEntriesScope("studio-seed", "seed-001", { limit: 10, offset: 0 }),
    "doc/studio-seed/seed-001?limit=10&offset=0",
  );
  assert.equal(
    attachmentEntriesScope("studio-seed", "seed-001", { limit: 10, offset: 0 }),
    "doc/studio-seed/seed-001?limit=10&offset=0",
    "counters and attachments share the document window scope; the kind distinguishes them",
  );
  assert.equal(
    timeSeriesCatalogScope("studio-seed", "seed-001"),
    "doc/studio-seed/seed-001/series",
  );
  assert.equal(
    timeSeriesEntriesScope("studio-seed", "seed-001", "cpu-temp", { limit: 10, offset: 20 }),
    "doc/studio-seed/seed-001/series/cpu-temp?limit=10&offset=20",
  );
  assert.equal(
    timeSeriesEntriesScope("a", "b", "cpu", { limit: 10, offset: 0 }, { start: 1000, end: 2000 }),
    "doc/a/b/series/cpu?limit=10&offset=0&start=1000&end=2000",
    "a selected range rides the scope in canonical parameter order",
  );
});

test("scope builders reject empty identities and invalid windows", () => {
  assert.throws(() => documentScope("  ", "seed-001"), /collection/);
  assert.throws(() => documentScope("studio-seed", ""), /document/);
  assert.throws(() => timeSeriesEntriesScope("a", "b", " ", { limit: 10, offset: 0 }), /series/);
  assert.throws(() => counterEntriesScope("a", "b", { limit: 0, offset: 0 }), /limit/);
  assert.throws(
    () => counterEntriesScope("a", "b", { limit: MAX_ENTRY_WINDOW_LIMIT + 1, offset: 0 }),
    /limit/,
  );
  assert.throws(() => counterEntriesScope("a", "b", { limit: 2.5, offset: 0 }), /limit/);
  assert.throws(() => counterEntriesScope("a", "b", { limit: 10, offset: -1 }), /offset/);
  assert.throws(
    () =>
      timeSeriesEntriesScope("a", "b", "cpu", { limit: 10, offset: 0 }, { start: 2000, end: 1000 }),
    /range/,
  );
});

test("the families route href encodes both identity segments", () => {
  assert.equal(familiesHref("studio-seed", "seed-001"), "/families/studio-seed/seed-001");
  assert.equal(familiesHref("ops/demo", "doc#1"), "/families/ops%2Fdemo/doc%231");
});

test("the time-series range view bound is explicit and checkable", () => {
  assert.equal(TIME_SERIES_RANGE_VIEW_BOUND_MS, 604_800_000);
  assert.equal(rangeSpanMs({ start: 1000, end: 3000 }), 2000);
  assert.equal(rangeWithinViewBound({ start: 0, end: TIME_SERIES_RANGE_VIEW_BOUND_MS }), true);
  assert.equal(rangeWithinViewBound({ start: 0, end: TIME_SERIES_RANGE_VIEW_BOUND_MS + 1 }), false);
});

test("epoch-millisecond inputs parse strictly", () => {
  assert.equal(parseEpochMsInput("1690000000000"), 1_690_000_000_000);
  assert.equal(parseEpochMsInput(" 1690000000000 "), 1_690_000_000_000);
  assert.equal(parseEpochMsInput("-1000"), -1000);
  assert.equal(parseEpochMsInput(""), null);
  assert.equal(parseEpochMsInput("12.5"), null);
  assert.equal(parseEpochMsInput("1e9"), null);
  assert.equal(parseEpochMsInput("abc"), null);
});

// ---------------------------------------------------------------------------
// Typed state parsing: malformed entries are counted and surfaced, never
// coerced; a state that is not the expected shape is unrecognized, fail closed.
// ---------------------------------------------------------------------------

test("counter family state parses typed entries and counts malformed ones", () => {
  const parsed = parseCounterFamilyState({
    entries: [
      { name: "reads", value: 41 },
      { name: "errors", value: -3 },
      { name: "gone", value: null },
      { name: 7, value: 1 },
      { name: "fraction", value: 1.5 },
    ],
    matched: 5,
  });
  assert.equal(parsed.kind, "recognized");
  if (parsed.kind !== "recognized") return;
  assert.deepEqual(parsed.state.entries, [
    { name: "reads", value: 41 },
    { name: "errors", value: -3 },
    { name: "gone", value: null },
  ]);
  assert.equal(parsed.state.matched, 5);
  assert.equal(parsed.malformed, 2);
});

test("counter family state is unrecognized outside the expected shape", () => {
  assert.equal(parseCounterFamilyState({ entries: 0 }).kind, "unrecognized");
  assert.equal(parseCounterFamilyState(null).kind, "unrecognized");
  assert.equal(parseCounterFamilyState({ entries: [] }).kind, "unrecognized");
  assert.equal(parseCounterFamilyState({ entries: [], matched: -1 }).kind, "unrecognized");
  assert.equal(parseCounterFamilyState({ entries: [], matched: 1.5 }).kind, "unrecognized");
});

test("attachment family state parses typed metadata entries and counts malformed ones", () => {
  const parsed = parseAttachmentFamilyState({
    entries: [
      { name: "log-001", contentType: "text/plain", size: 512, hash: "sha256:aa" },
      { name: "img", contentType: "image/png", size: 0, hash: "sha256:bb" },
      { name: "bad", contentType: "text/plain", size: -1, hash: "sha256:cc" },
      { nope: true },
    ],
    matched: 4,
  });
  assert.equal(parsed.kind, "recognized");
  if (parsed.kind !== "recognized") return;
  assert.equal(parsed.state.entries.length, 2);
  assert.equal(parsed.state.entries[1]?.size, 0);
  assert.equal(parsed.malformed, 2);
  assert.equal(parseAttachmentFamilyState({ entries: [] }).kind, "unrecognized");
});

test("time-series catalog and page states parse with the same discipline", () => {
  const catalog = parseTimeSeriesCatalogState({
    series: [
      { name: "cpu-temp", matched: 26 },
      { name: 3, matched: 4 },
      { name: "disk-io", matched: "many" },
    ],
  });
  assert.equal(catalog.kind, "recognized");
  if (catalog.kind === "recognized") {
    assert.deepEqual(catalog.state.series, [{ name: "cpu-temp", matched: 26 }]);
    assert.equal(catalog.malformed, 2);
  }
  assert.equal(parseTimeSeriesCatalogState({ series: "cpu-temp" }).kind, "unrecognized");

  const page = parseTimeSeriesPageState({
    name: "cpu-temp",
    entries: [
      { timestamp: 1_690_000_000_000, values: [36.5], tag: "rack-a" },
      { timestamp: 1_690_000_060_000, values: [36.6, 0.2], tag: null },
      { timestamp: 1_690_000_120_000, values: [], tag: null },
      { timestamp: "soon", values: [1], tag: null },
    ],
    matched: 26,
    range: { start: 1_690_000_000_000, end: 1_690_003_600_000 },
  });
  assert.equal(page.kind, "recognized");
  if (page.kind === "recognized") {
    assert.equal(page.state.name, "cpu-temp");
    assert.equal(page.state.entries.length, 2);
    assert.equal(page.state.entries[0]?.tag, "rack-a");
    assert.deepEqual(page.state.range, { start: 1_690_000_000_000, end: 1_690_003_600_000 });
    assert.equal(page.malformed, 2);
  }
  assert.equal(
    parseTimeSeriesPageState({ name: "cpu", entries: [], matched: 0, range: { start: 9, end: 1 } })
      .kind,
    "unrecognized",
    "an invalid echoed range fails closed",
  );
  assert.equal(
    parseTimeSeriesPageState({ name: "cpu", entries: [], matched: 0, range: null }).kind,
    "recognized",
    "an absent range is the whole-series read",
  );
});

test("entry paging distinguishes more, complete, and the bounded window cap", () => {
  const more = entryPagingStatus({
    loadedEntries: FAMILY_ENTRY_PAGE_SIZE,
    matched: 23,
    pageSize: FAMILY_ENTRY_PAGE_SIZE,
    maxPages: MAX_LOADED_ENTRY_PAGES,
  });
  assert.equal(more.kind, "more");
  assert.equal(more.kind === "more" ? more.nextOffset : null, FAMILY_ENTRY_PAGE_SIZE);

  const complete = entryPagingStatus({
    loadedEntries: 23,
    matched: 23,
    pageSize: FAMILY_ENTRY_PAGE_SIZE,
    maxPages: MAX_LOADED_ENTRY_PAGES,
  });
  assert.equal(complete.kind, "complete");

  const capped = entryPagingStatus({
    loadedEntries: MAX_LOADED_ENTRY_PAGES * FAMILY_ENTRY_PAGE_SIZE,
    matched: 9999,
    pageSize: FAMILY_ENTRY_PAGE_SIZE,
    maxPages: MAX_LOADED_ENTRY_PAGES,
  });
  assert.equal(capped.kind, "window-capped");
});

// ---------------------------------------------------------------------------
// Subtree and copy: the route modules exist and speak CONTEXT.md exactly.
// ---------------------------------------------------------------------------

async function collectSources(dir) {
  const entries = await readdir(dir, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...(await collectSources(path)));
    } else if (/\.(ts|tsx|mjs)$/.test(entry.name)) {
      files.push(path);
    }
  }
  return files;
}

const familiesSubtree = join(studioRoot, "app", "families");
const familiesComponents = join(studioRoot, "src", "components", "families");
const subtreeFiles = await collectSources(familiesSubtree).catch(() => []);
const componentFiles = await collectSources(familiesComponents).catch(() => []);
const subtreeNames = subtreeFiles.map((path) => path.slice(familiesSubtree.length + 1));
const familiesCopy = (
  await Promise.all(
    [...subtreeFiles, ...componentFiles].map(async (path) => readFile(path, "utf8")),
  )
).join("\n");

test("the families subtree exists with page modules", () => {
  for (const expected of [
    join("page.tsx"),
    join("loading.tsx"),
    join("error.tsx"),
    join("[collection]", "[documentId]", "page.tsx"),
    join("[collection]", "[documentId]", "loading.tsx"),
  ]) {
    assert.ok(subtreeNames.includes(expected), `app/families/${expected} is missing`);
  }
  assert.ok(
    componentFiles.some((path) => path.endsWith("vocabulary.ts")),
    "src/components/families/vocabulary.ts is missing",
  );
});

test("page copy uses the CONTEXT.md Data Family vocabulary", () => {
  assert.ok(familiesCopy.includes("Data Family"), "the term Data Family must appear");
  assert.ok(
    familiesCopy.includes(NOT_YET_VIEWABLE_LABEL),
    "the not-yet-viewable label must appear",
  );
  assert.ok(
    familiesCopy.includes(COUNTER_INCREMENT_SEMANTICS),
    "the counter semantics label is engine-verbatim",
  );
  assert.ok(
    familiesCopy.includes(TIME_SERIES_RANGE_SEMANTICS),
    "the time-series range semantics label is engine-verbatim",
  );
  // CONTEXT.md avoid list for Data Family: collection type, feature bucket.
  for (const banned of [/\bcollection type\b/i, /\bfeature bucket\b/i]) {
    assert.ok(!banned.test(familiesCopy), `families copy uses avoided wording ${banned}`);
  }
});

test("the families route is linked from the studio navigation", async () => {
  const nav = await readFile(
    join(studioRoot, "src", "components", "shell", "StudioNav.tsx"),
    "utf8",
  );
  assert.match(nav, /href: "\/families"/);
  assert.match(nav, /label: "Families"/);
  const planned = nav.match(/const PLANNED = \[([^\]]*)\]/)?.[1] ?? "";
  assert.ok(!planned.includes("Families"), "Families must leave the planned list");
});

// ---------------------------------------------------------------------------
// The families fixture: the unmodified DB-02 core composed with per-document
// family state, seeded through typed family.write envelopes only.
// ---------------------------------------------------------------------------

async function withFamiliesFixture(run) {
  const families = createFamiliesFixtureServer({ host: "127.0.0.1" });
  await families.ready();
  await new Promise((resolve) => families.server.listen(0, "127.0.0.1", resolve));
  const { port } = families.server.address();
  const baseUrl = `http://127.0.0.1:${port}`;
  try {
    await run(baseUrl, families);
  } finally {
    families.state.dispose();
    families.server.closeAllConnections();
    families.server.close();
    families.coreState.dispose();
    families.coreServer.closeAllConnections();
    families.coreServer.close();
  }
}

async function callOperation(
  baseUrl,
  op,
  request,
  { version = 2, correlationId = "unit-db06-1" } = {},
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

async function controlFamilies(baseUrl, body) {
  const response = await fetch(`${baseUrl}/api/v2/_fixture/families-control`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  assert.equal(response.status, 200);
  return response.json();
}

async function seedCounters(baseUrl, collection, documentId, operations, operationId) {
  const outcome = await callOperation(baseUrl, "family.write", {
    kind: "counters",
    scope: documentScope(collection, documentId),
    operation: { operations },
    operationId,
  });
  assert.equal(
    outcome.status,
    200,
    `seed counters ${collection}/${documentId} failed: ${JSON.stringify(outcome.body)}`,
  );
  return outcome.body.result;
}

const MINUTE_MS = 60_000;
const T0 = 1_750_000_000_000;

test("typed counter batches create counters from zero and apply signed deltas", async () => {
  await withFamiliesFixture(async (baseUrl) => {
    await seedCounters(
      baseUrl,
      "families-demo",
      "sensors-001",
      [
        { kind: "increment", name: "reads", delta: 41 },
        { kind: "increment", name: "errors", delta: -3 },
      ],
      "seed-c1",
    );
    // Increment again: the delta applies to the existing value.
    await seedCounters(
      baseUrl,
      "families-demo",
      "sensors-001",
      [{ kind: "increment", name: "reads", delta: 9 }],
      "seed-c2",
    );

    const outcome = await callOperation(baseUrl, "family.read", {
      kind: "counters",
      scope: counterEntriesScope("families-demo", "sensors-001", { limit: 10, offset: 0 }),
    });
    assert.equal(outcome.status, 200);
    assert.equal(outcome.body.result.kind, "counters");
    assert.equal(
      outcome.body.result.scope,
      counterEntriesScope("families-demo", "sensors-001", { limit: 10, offset: 0 }),
    );
    const parsed = parseCounterFamilyState(outcome.body.result.state);
    assert.equal(parsed.kind, "recognized");
    if (parsed.kind !== "recognized") return;
    assert.deepEqual(
      [...parsed.state.entries].sort((a, b) => (a.name < b.name ? -1 : 1)),
      [
        { name: "errors", value: -3 },
        { name: "reads", value: 50 },
      ],
    );
    assert.equal(parsed.state.matched, 2);
  });
});

test("an idempotent counter batch replays its receipt without applying twice", async () => {
  await withFamiliesFixture(async (baseUrl) => {
    const first = await seedCounters(
      baseUrl,
      "families-demo",
      "sensors-001",
      [{ kind: "increment", name: "reads", delta: 5 }],
      "seed-once",
    );
    const replay = await callOperation(baseUrl, "family.write", {
      kind: "counters",
      scope: documentScope("families-demo", "sensors-001"),
      operation: { operations: [{ kind: "increment", name: "reads", delta: 5 }] },
      operationId: "seed-once",
    });
    assert.equal(replay.status, 200);
    assert.deepEqual(replay.body.result, first, "the durable receipt is replayed verbatim");

    const outcome = await callOperation(baseUrl, "family.read", {
      kind: "counters",
      scope: counterEntriesScope("families-demo", "sensors-001", { limit: 10, offset: 0 }),
    });
    const parsed = parseCounterFamilyState(outcome.body.result.state);
    if (parsed.kind !== "recognized") assert.fail("state must be recognized");
    const reads = parsed.state.entries.find((entry) => entry.name === "reads");
    assert.equal(reads?.value, 5, "the replayed batch applied exactly once");
  });
});

test("counter batches page deterministically and delete removes a counter", async () => {
  await withFamiliesFixture(async (baseUrl) => {
    const operations = [];
    for (let index = 1; index <= 23; index += 1) {
      operations.push({
        kind: "increment",
        name: `counter-${String(index).padStart(3, "0")}`,
        delta: index,
      });
    }
    await seedCounters(baseUrl, "families-demo", "sensors-001", operations, "seed-page");
    await seedCounters(
      baseUrl,
      "families-demo",
      "sensors-001",
      [{ kind: "delete", name: "counter-023" }],
      "seed-del",
    );

    const first = await callOperation(baseUrl, "family.read", {
      kind: "counters",
      scope: counterEntriesScope("families-demo", "sensors-001", { limit: 10, offset: 0 }),
    });
    const second = await callOperation(baseUrl, "family.read", {
      kind: "counters",
      scope: counterEntriesScope("families-demo", "sensors-001", { limit: 10, offset: 10 }),
    });
    const third = await callOperation(baseUrl, "family.read", {
      kind: "counters",
      scope: counterEntriesScope("families-demo", "sensors-001", { limit: 10, offset: 20 }),
    });
    for (const outcome of [first, second, third]) {
      assert.equal(outcome.status, 200);
    }
    const names = (outcome) => {
      const parsed = parseCounterFamilyState(outcome.body.result.state);
      if (parsed.kind !== "recognized") assert.fail("state must be recognized");
      return parsed.state.entries.map((entry) => entry.name);
    };
    assert.deepEqual(
      names(first),
      Array.from({ length: 10 }, (_, i) => `counter-${String(i + 1).padStart(3, "0")}`),
    );
    assert.deepEqual(
      names(second),
      Array.from({ length: 10 }, (_, i) => `counter-${String(i + 11).padStart(3, "0")}`),
    );
    assert.deepEqual(names(third), ["counter-021", "counter-022"], "counter-023 is deleted");
    const matched = parseCounterFamilyState(third.body.result.state);
    if (matched.kind !== "recognized") assert.fail("state must be recognized");
    assert.equal(matched.state.matched, 22);

    const repeat = await callOperation(baseUrl, "family.read", {
      kind: "counters",
      scope: counterEntriesScope("families-demo", "sensors-001", { limit: 10, offset: 0 }),
    });
    assert.deepEqual(repeat.body.result, first.body.result, "identical reads answer identically");
  });
});

test("counter overflow fails closed with the engine's typed case", async () => {
  await withFamiliesFixture(async (baseUrl) => {
    await seedCounters(
      baseUrl,
      "families-demo",
      "sensors-001",
      [{ kind: "increment", name: "big", delta: Number.MAX_SAFE_INTEGER }],
      "seed-of-1",
    );
    const outcome = await callOperation(baseUrl, "family.write", {
      kind: "counters",
      scope: documentScope("families-demo", "sensors-001"),
      operation: {
        operations: [{ kind: "increment", name: "big", delta: Number.MAX_SAFE_INTEGER }],
      },
      operationId: "seed-of-2",
    });
    assert.equal(outcome.status, 400);
    assert.equal(outcome.body.error.code, "family.counter-overflow");
    assert.equal(outcome.body.error.retryable, false);
  });
});

test("attachment stores page, mirror the duplicate rule, and delete removes", async () => {
  await withFamiliesFixture(async (baseUrl) => {
    for (let index = 1; index <= 14; index += 1) {
      const outcome = await callOperation(baseUrl, "family.write", {
        kind: "attachments",
        scope: documentScope("families-demo", "sensors-001"),
        operation: {
          mutation: {
            kind: "store",
            name: `log-${String(index).padStart(3, "0")}`,
            contentType: "text/plain",
            size: index * 256,
            hash: `sha256:${String(index).padStart(64, "0")}`,
            replace: false,
          },
        },
        operationId: `seed-a-${index}`,
      });
      assert.equal(outcome.status, 200);
    }

    const duplicate = await callOperation(baseUrl, "family.write", {
      kind: "attachments",
      scope: documentScope("families-demo", "sensors-001"),
      operation: {
        mutation: {
          kind: "store",
          name: "log-001",
          contentType: "text/plain",
          size: 1,
          hash: "sha256:ff",
          replace: false,
        },
      },
      operationId: "seed-a-dup",
    });
    assert.equal(duplicate.status, 409);
    assert.equal(duplicate.body.error.code, "family.attachment-name-duplicate");

    const replaced = await callOperation(baseUrl, "family.write", {
      kind: "attachments",
      scope: documentScope("families-demo", "sensors-001"),
      operation: {
        mutation: {
          kind: "store",
          name: "log-001",
          contentType: "application/json",
          size: 2,
          hash: "sha256:ee",
          replace: true,
        },
      },
      operationId: "seed-a-repl",
    });
    assert.equal(replaced.status, 200, "replace=true stores over an existing name");

    await callOperation(baseUrl, "family.write", {
      kind: "attachments",
      scope: documentScope("families-demo", "sensors-001"),
      operation: { mutation: { kind: "delete", name: "log-014" } },
      operationId: "seed-a-del",
    });

    const page = await callOperation(baseUrl, "family.read", {
      kind: "attachments",
      scope: attachmentEntriesScope("families-demo", "sensors-001", { limit: 10, offset: 0 }),
    });
    assert.equal(page.status, 200);
    const parsed = parseAttachmentFamilyState(page.body.result.state);
    if (parsed.kind !== "recognized") assert.fail("state must be recognized");
    assert.equal(parsed.state.entries.length, 10);
    assert.equal(
      parsed.state.entries[0]?.contentType,
      "application/json",
      "the replaced metadata is served",
    );
    assert.equal(parsed.state.matched, 13, "log-014 is deleted");
  });
});

test("time-series appends read back in timestamp order with inclusive range filtering", async () => {
  await withFamiliesFixture(async (baseUrl) => {
    const appends = [];
    for (let index = 0; index < 26; index += 1) {
      appends.push({
        timestamp: T0 + index * MINUTE_MS,
        values: [36.5 + index * 0.1],
        tag: index % 2 === 0 ? "rack-a" : null,
      });
    }
    const write = await callOperation(baseUrl, "family.write", {
      kind: "time-series",
      scope: timeSeriesEntriesScope("families-demo", "sensors-001", "cpu-temp", {
        limit: 10,
        offset: 0,
      }),
      operation: { appends },
      operationId: "seed-ts-1",
    });
    assert.equal(write.status, 200, JSON.stringify(write.body));

    const catalog = await callOperation(baseUrl, "family.read", {
      kind: "time-series",
      scope: timeSeriesCatalogScope("families-demo", "sensors-001"),
    });
    assert.equal(catalog.status, 200);
    const catalogParsed = parseTimeSeriesCatalogState(catalog.body.result.state);
    if (catalogParsed.kind !== "recognized") assert.fail("catalog must be recognized");
    assert.deepEqual(catalogParsed.state.series, [{ name: "cpu-temp", matched: 26 }]);

    const window = await callOperation(baseUrl, "family.read", {
      kind: "time-series",
      scope: timeSeriesEntriesScope("families-demo", "sensors-001", "cpu-temp", {
        limit: 10,
        offset: 0,
      }),
    });
    assert.equal(window.status, 200);
    let page = parseTimeSeriesPageState(window.body.result.state);
    if (page.kind !== "recognized") assert.fail("page must be recognized");
    assert.equal(page.state.name, "cpu-temp");
    assert.equal(page.state.entries.length, 10);
    assert.equal(page.state.entries[0]?.timestamp, T0);
    assert.equal(page.state.range, null, "a read without a selected range echoes null");
    assert.equal(page.state.matched, 26);

    const ranged = await callOperation(baseUrl, "family.read", {
      kind: "time-series",
      scope: timeSeriesEntriesScope(
        "families-demo",
        "sensors-001",
        "cpu-temp",
        { limit: 10, offset: 0 },
        { start: T0 + 5 * MINUTE_MS, end: T0 + 8 * MINUTE_MS },
      ),
    });
    assert.equal(ranged.status, 200);
    page = parseTimeSeriesPageState(ranged.body.result.state);
    if (page.kind !== "recognized") assert.fail("ranged page must be recognized");
    assert.deepEqual(
      page.state.entries.map((entry) => entry.timestamp),
      [T0 + 5 * MINUTE_MS, T0 + 6 * MINUTE_MS, T0 + 7 * MINUTE_MS, T0 + 8 * MINUTE_MS],
      "the range is inclusive on both ends",
    );
    assert.deepEqual(page.state.range, { start: T0 + 5 * MINUTE_MS, end: T0 + 8 * MINUTE_MS });
    assert.equal(page.state.matched, 4, "matched counts the in-range entries only");
  });
});

test("an invalid time-series range or window fails closed with typed errors", async () => {
  await withFamiliesFixture(async (baseUrl) => {
    const inverted = await callOperation(baseUrl, "family.read", {
      kind: "time-series",
      scope: `doc/families-demo/sensors-001/series/cpu-temp?limit=10&offset=0&start=${T0 + MINUTE_MS}&end=${T0}`,
    });
    assert.equal(inverted.status, 400);
    assert.equal(inverted.body.error.code, "family.range-invalid");
    assert.equal(inverted.body.error.retryable, false);

    const badWindow = await callOperation(baseUrl, "family.read", {
      kind: "counters",
      scope: "doc/families-demo/sensors-001?limit=0&offset=0",
    });
    assert.equal(badWindow.status, 400);
    assert.equal(badWindow.body.error.code, "family.window-invalid");

    const wrongShape = await callOperation(baseUrl, "family.read", {
      kind: "counters",
      scope: "doc/families-demo/sensors-001/series/cpu-temp?limit=10&offset=0",
    });
    assert.equal(wrongShape.status, 400);
    assert.equal(wrongShape.body.error.code, "family.scope-invalid");
  });
});

test("unknown scopes and kinds keep core behavior: proxy, default state, fail closed", async () => {
  await withFamiliesFixture(async (baseUrl, families) => {
    // A non-document scope is the core's own surface, verbatim.
    const coreScoped = await callOperation(baseUrl, "family.read", {
      kind: "counters",
      scope: "studio",
    });
    assert.equal(coreScoped.status, 200);
    assert.deepEqual(
      coreScoped.body.result.state,
      { entries: 2, totals: { reads: 0 } },
      "the core's seeded counters:studio state is proxied unmodified",
    );

    // The unmodified core answers document scopes with its default state: the
    // pre-SE-08 shape the panels render honestly as unrecognized.
    const onCore = await callOperation(families.coreBaseUrl, "family.read", {
      kind: "counters",
      scope: counterEntriesScope("families-demo", "sensors-001", { limit: 10, offset: 0 }),
    });
    assert.equal(onCore.status, 200);
    assert.deepEqual(onCore.body.result.state, { entries: 0 });

    const unknownKind = await callOperation(baseUrl, "family.read", {
      kind: "not-a-family",
      scope: documentScope("families-demo", "sensors-001"),
    });
    assert.equal(unknownKind.status, 400);
    assert.equal(unknownKind.body.error.code, "family.kind-unknown");

    // A not-yet-viewable kind with a document scope proxies to the core too —
    // the page never reads those kinds; it marks them not yet viewable.
    const revisions = await callOperation(baseUrl, "family.read", {
      kind: "revisions",
      scope: documentScope("families-demo", "sensors-001"),
    });
    assert.equal(revisions.status, 200);
    assert.deepEqual(revisions.body.result.state, { entries: 0 });
  });
});

test("the families fixture fails closed on envelope discipline like the core", async () => {
  await withFamiliesFixture(async (baseUrl) => {
    const badVersion = await callOperation(
      baseUrl,
      "family.read",
      { kind: "counters", scope: "doc/a/b?limit=10&offset=0" },
      { version: 1 },
    );
    assert.equal(badVersion.status, 400);
    assert.equal(badVersion.body.error.code, "protocol.version-unsupported");

    const response = await fetch(`${baseUrl}/api/v2/operations/family.read`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        v: 2,
        op: "engine.status",
        correlationId: "unit-db06-2",
        request: {},
      }),
    });
    const mismatch = await response.json();
    assert.equal(response.status, 400);
    assert.equal(mismatch.error.code, "protocol.envelope-invalid");
  });
});

test("family failure, delay, and session-gate controls behave typed", async () => {
  await withFamiliesFixture(async (baseUrl) => {
    await controlFamilies(baseUrl, { familyBehavior: "fail" });
    try {
      const failed = await callOperation(baseUrl, "family.read", {
        kind: "counters",
        scope: counterEntriesScope("families-demo", "sensors-001", { limit: 10, offset: 0 }),
      });
      assert.equal(failed.status, 503);
      assert.equal(failed.body.error.code, "family.unavailable");
      assert.equal(failed.body.error.retryable, true);
      assert.equal(failed.body.correlationId, "unit-db06-1");
    } finally {
      await controlFamilies(baseUrl, { familyBehavior: "ok" });
    }

    await controlFamilies(baseUrl, { familyDelayMs: 250 });
    try {
      const startedAt = Date.now();
      const delayed = await callOperation(baseUrl, "family.read", {
        kind: "counters",
        scope: counterEntriesScope("families-demo", "sensors-001", { limit: 10, offset: 0 }),
      });
      assert.equal(delayed.status, 200);
      assert.ok(Date.now() - startedAt >= 240, "family reads honor the delay");
    } finally {
      await controlFamilies(baseUrl, { familyDelayMs: 0 });
    }

    await controlFamilies(baseUrl, { behavior: "session-gate" });
    try {
      const gated = await callOperation(baseUrl, "family.read", {
        kind: "counters",
        scope: counterEntriesScope("families-demo", "sensors-001", { limit: 10, offset: 0 }),
      });
      assert.equal(gated.status, 401);
      assert.equal(gated.body.error.code, "session.unauthenticated");
      assert.equal(gated.body.error.loginUrl, "/fixture/login");
    } finally {
      await controlFamilies(baseUrl, { behavior: "session-open" });
      await controlFamilies(baseUrl, { reset: true });
    }
  });
});
