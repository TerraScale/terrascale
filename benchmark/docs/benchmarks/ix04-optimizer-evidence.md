# IX-04 optimizer evidence: budgets, measurements, and receipts

Ticket: TerraBaseV2#40 (IX-04). Charter decisions: PD-2026-08-26-09 D3
(the optimizer is measured), PD-2026-08-26-03 (benchmark-mandatory, exact
verdict vocabulary, objective status per run). ADR 0010 D-6 names this
packet as the owner of the optimizer's receipts. This document is written
in two halves, and the order is the discipline: the **budgets came
first** (committed before any measurement code ran), and every budget
below names the test or benchmark measurement that decides it. The run
half is appended by the same packet after the exclusive-lane runs.

Status of every measured value while the budgets were written:
**unrun**. Nothing in the budget half rests on a number.

## The measured surface

The optimizer under measurement is the landed IX-01..IX-03 surface at the
query/index seam:

- every dynamic query executes through the bounded snapshot scan and then
  runs one optimizer pass (`src/query/execute.rs`, `execute`), which
  records one
  [`IndexSelectionRecord`](../query/optimizer/mod.rs) per query — the
  query identity, every candidate with its verdict, every tie-break rule
  applied, the chosen index or the creation event, whether the answer came
  from the bounded selection cache, and the staleness basis;
- an unmatched dynamic query creates the deterministic `Auto/` index
  (ADR 0010 D-2), widening from partial matches (V1 AX-16), and a
  fail-closed guard refuses creation instead of silently scanning;
- the automatic lifecycle merges, supersedes, and retires auto indexes
  (ADR 0010 D-4), and the typed reads report states and transitions.

Everything this packet measures is observable through those typed reads
(`QueryStore::recent_index_selections`, `QueryStore::registered_indexes`)
plus the benchmark lane's deterministic durable-byte counter. **No
measurement hook was added to `src/query/optimizer/` or `src/index/`**;
both stay read-only for this packet.

## The auto-indexed ad-hoc workload class

Four workloads, mapped on both sides like the SE-15 CRUD class
(`src/benchmarks/lane.rs`). They live as data in
`src/benchmarks/optimizer.rs` so the mapping below cannot drift from what
the driver drives.

| Workload | TerraBaseV2 operation | PostgreSQL equivalent |
| --- | --- | --- |
| `auto-index-adhoc-single-collection` | S sequential ad-hoc shapes (distinct equality/ordering field sets) over one seeded collection through `QueryStore::compile + execute` — each first sighting triggers one optimizer pass on a served execution | one `CREATE INDEX` per distinct shape, then the `SELECT` — PostgreSQL has **no** automatic index creation, so the comparable act is the operator's manual `CREATE INDEX`; that asymmetry is stated, not hidden |
| `auto-index-adhoc-cross-collection` | one ad-hoc shape per collection across C seeded collections, each repeated R times | the same manual-index-then-select act per table, C times |
| `auto-index-adhoc-write-path` | the acknowledged write stream (`Transaction::put + commit(LocalDurability::Synced)`) with K active auto indexes, against the same stream with none | `INSERT ... ON CONFLICT ... DO UPDATE` on a table carrying K indexes, against the same table carrying none |
| `auto-index-adhoc-registry-bound` | distinct ad-hoc shapes driven past the per-tenant auto-index registry bound | no equivalent: PostgreSQL's index count is operator-chosen and unbounded by any engine rule |

The fairness asymmetry that matters for the objective question, stated
rather than worked around: on the first two workloads TerraBaseV2 does
work PostgreSQL does not do (it decides and creates the index itself), and
on the write-path workload the comparison is TerraBaseV2 against itself —
which is why that workload's verdict is a budget check and never a
PostgreSQL-parity statement.

## Budgets

Each budget has an identifier, a value, and the measurement that decides
it. `unrun` in this table is the state while the budgets were written.

### Selection quality (PD-2026-08-26-09 D3: no query served by a worse-than-available index without a recorded reason)

- **SQ-B1 — every unchosen complete candidate carries its reason.**
  Over the selection records of any driven window, the count of records
  in which a candidate with verdict `Complete` or `CompleteButIdle` was
  not chosen and carried no `CandidateNote` is exactly **0**. This is the
  charter rule made mechanical: the audit surface is the record, so the
  measurement counts records, not vibes.
  *Measured by:* `optimizer_tests::no_worse_than_available_selection_without_a_recorded_reason`
  over the single-collection and cross-collection windows, and reported
  per window in the ad-hoc workload receipt. Status: unrun.
- **SQ-B2 — the first sighting creates exactly one index and still
  answers.** S distinct ad-hoc shapes on one collection produce exactly
  S `CreatedAutoIndex` outcomes (each a widening of the union when a
  partial exists), 0 refusals, and every triggering query returns its
  rows — the bounded first-query behavior.
  *Measured by:* `the_first_sighting_of_each_shape_creates_exactly_one_index`
  and the receipt's per-window creation row. Status: unrun.
- **SQ-B3 — a repeated shape is served, not re-created.** A shape
  repeated R times produces 1 creation and R−1 `ChosenIndex` outcomes of
  the same deterministic auto index, each reporting its cache-hit origin.
  *Measured by:* `a_repeated_shape_is_served_not_recreated`. Status: unrun.
- **SQ-B4 — an equivalent explicit index wins and says so.** With an
  equivalent explicit index registered, 0 auto indexes are created, the
  explicit index is chosen, and the losing auto candidates carry
  `ExplicitPreferredOverAuto` (ADR 0010 D-3).
  *Measured by:* `an_equivalent_explicit_index_is_preferred_and_records_why`.
  Status: unrun.
- **SQ-B5 — a superseded partial is recorded, not silently dropped.**
  When a wider covering index supersedes a partial, the covered index's
  retirement is observable through the typed reads (a bounded retirement
  record), so the lifecycle never hides what it deleted.
  *Measured by:* `a_covered_partial_is_superseded_observably`. Status: unrun.

### Auto-index overhead

- **OH-B1 — creation appends no durable bytes.** One auto-index creation
  appends exactly **0** bytes to the engine's durable location: the
  catalog and its segments are in-memory engine state derived from
  storage publication order, and creation writes no journal record.
  *Measured by:* the durable-byte counter across the single-collection
  ad-hoc window, asserted by
  `auto_index_creation_appends_no_durable_bytes`. Status: unrun.
- **OH-B2 — creation does exactly one initial build.** Each creation
  performs one initial build that publishes at generation 1 covering the
  latest observed publication; the record carries the build outcome and
  the staleness basis, so a deferred or rejected build is visible rather
  than absorbed.
  *Measured by:* `each_creation_performs_one_published_initial_build`.
  Status: unrun.
- **OH-B3 — write-path durable overhead is zero.** For the same
  acknowledged write stream, the durable bytes per write with K active
  auto indexes equal the durable bytes per write with none, for
  K ∈ {1, 8, 32}. Budget: **0 additional durable bytes per write per
  active auto index**; index maintenance is off the durable write path.
  *Measured by:* the deterministic counter compared through the SE-15
  gate (`no-improvement` is the budget met; `v2-regression` is a budget
  breach owed a follow-up owner), asserted by
  `active_auto_indexes_add_no_durable_bytes_to_the_write_path`, and
  receipted in `ix04-write-path-overhead-receipt.md`. Status: unrun.
- **OH-B4 — selection is bounded work per query.** One dynamic query
  produces exactly one selection record, whatever the candidate count;
  the record store is bounded, so a long ad-hoc stream cannot grow
  engine state without bound.
  *Measured by:* `one_query_produces_exactly_one_selection_record` and
  the record-bound budget MB-B2. Status: unrun.

### Memory bounds

- **MB-B1 — the per-tenant auto-index registry is bounded.** Live auto
  indexes per tenant never exceed `MAX_AUTO_INDEXES_PER_TENANT` (64);
  the 65th distinct shape that cannot reuse a slot refuses creation with
  `CreationRefusal::RegistryFull` and the query fails closed — never an
  unbounded registry.
  *Measured by:* `the_auto_index_registry_is_bounded_and_refuses_past_it`.
  Status: unrun.
- **MB-B2 — the selection-record store is bounded.** Retained selection
  records never exceed `MAX_SELECTION_RECORDS` (128), oldest first.
  *Measured by:* `the_selection_record_store_is_bounded`. Status: unrun.
- **MB-B3 — the selection cache holds every live shape.** After C
  distinct shapes are driven (C at the registry bound), every one of them
  is still served from the bounded selection cache on its next
  execution — the cache's bound (64) is not below the registry's.
  *Measured by:* `every_live_shape_stays_served_from_the_selection_cache`.
  Status: unrun.
- **MB-B4 — derived entry bound.** In-memory index entries are bounded
  by live auto namespaces × the collection's document count: one segment
  entry per document that carries the indexed fields. This packet states
  the arithmetic and measures its two inputs (live auto namespaces
  through the typed read; documents through the seeded count); it does
  **not** claim a measured byte figure, because the catalog exposes no
  entry-count read and this packet adds no hook to get one.
  *Measured by:* the live-auto counter in the registry-bound test.
  Status: unrun.

## Decisions this packet owes

- **D1 (PROPOSED — the ad-hoc workload class is a separate class, not
  more rows of the SE-15 `Workload` enum).** The SE-15 enum's `ALL`
  length, token spellings, and driver slots are pinned by SE-15's tests
  and receipts; a second class measured by a second driver keeps both
  auditable without rewriting a landed receipt's vocabulary. The parent
  ratifies or folds them together.
- **D2 (PROPOSED — the write-path overhead verdict is a budget check,
  not an objective statement).** The only comparison that exists on this
  host is TerraBaseV2-with-auto-indexes against TerraBaseV2-without; its
  verdict is recorded with the exact vocabulary and its objective status
  is `inconclusive` against PostgreSQL, because the PostgreSQL baseline
  is unrun. The parent ratifies this reading.
- **D3 (decided in-packet — zero drain is the lane's lifecycle
  configuration).** The single-collection workload's supersessions must
  resolve inside the window for the live-auto counter to be observable,
  so the lane opens its engines with
  `LifecycleThresholds::supersede_drain = 0` — V1's documented test mode
  for the same pass (`Indexing.TimeBeforeDeletionOfSupersededAutoIndex`
  at 0). Idle thresholds stay at V1's defaults; no retirement fires
  inside a window.

## Run half

Appended after the exclusive-lane runs. Until then every measured value
above is **unrun**; nothing in the budget half depends on one.
