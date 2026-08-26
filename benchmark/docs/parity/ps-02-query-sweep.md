# PS-02 query/index parity sweep: PB-4000..PB-4062

Status: row-by-row verification note for Forgejo ticket TerraBaseV2#54
(PS-02), recorded 2026-08-26 against the current `develop` tree.
Documentation only: no parity status, disposition, Proof ID, overlay,
`parity/contracts/`, `CONTEXT.md`, or code changes were made. All 63 rows
remain `unresolved` with empty Proof ID lists (`parity/overlays/PB-4000.json`
..`PB-4062.json`); this sweep changes no row's status.

Authoritative row source: `parity/contracts/query.json` (63 items,
PB-4000..PB-4062) at the pinned V1 source identity
`fa99b9257776dbe625e6a2388c74c9ce916637ee`. Primary V2 evidence sources:
`src/query/**` (the W3-2 typed compile/execute seam) and `src/index/**`
(the W3-2 private index catalog), with the design record
`docs/query/w3-query-index.md` and its focused tests in
`src/query/tests.rs`, `src/index/tests.rs`, and
`tests/query/query_public_construction.rs`.

## Verification vocabulary

- **tracer-partial**: the W3-2 seam and its focused tests express part of
  the row's semantics as V2 tracer behavior. Never a V1 parity claim; the
  design record says so explicitly
  (`docs/query/w3-query-index.md:283-306`).
- **cross-seam-partial**: another seam (streaming, families, operations)
  carries adjacent evidence; the query/index contract itself is not met.
- **absent**: no engine-side implementation or test evidence in this tree.
  Absence is a fact about the tree, not a disposition: wave-owned rows
  (below) are absent here because their wave has not landed.

## Wave ownership cross-reference (checked before flagging gaps)

| Wave | Tickets | Rows owned per the PS-02 brief |
| --- | --- | --- |
| TQL query language | #32-#35 | Grammar-surface rows: PB-4000 (parse/result/error contract surface), PB-4002 (FROM source/aliases), PB-4003 (boolean precedence), PB-4005 (IN), PB-4007 (prefix/suffix/regex), PB-4008 (search operators grammar), PB-4009, PB-4010, PB-4011 (parameters), PB-4012 (FILTER grammar), PB-4015 (SELECT/LOAD grammar), PB-4032 (LINQ/RQL parity mapping), PB-4041 (parser validation/error surface) |
| IX auto-index/optimizer | #37-#40 | PB-4045, PB-4046, and optimizer/selection-quality behavior (charter PD-2026-08-26-09) |
| SH sharding | #47-#51 | PB-4036, PB-4060 |
| Competitive matrix | CP-01 / AI-01 | PB-4024, PB-4051 (vector search is a **required** CP row, not an optional-discard candidate) |

Everything else was verified directly against `src/query/` and `src/index/`
and is proposed for routing in the gap-ticket list at the end.

## Summary table

Verdict describes the current tree; disposition proposes routing. All rows
stay `unresolved`.

| Row | Name | Class | Verdict | Disposition (proposed) |
| --- | --- | --- | --- | --- |
| PB-4000 | query-execution-results-and-errors | core | tracer-partial | TQL wave #32-#35 (grammar surface) + GAP-Q11 (result metadata) |
| PB-4001 | static-index-definition-and-planning | core | absent | GAP-Q6 (static index definition contract) |
| PB-4002 | query-source-selection-and-aliases | core | tracer-partial | TQL wave (grammar) + GAP-Q6 (index-name source) |
| PB-4003 | boolean-expression-precedence-and-negation | core | absent | TQL wave #32-#35 |
| PB-4004 | equality-null-missing-and-exists | core | tracer-partial | GAP-Q1 (V1 parity test) |
| PB-4005 | membership-in-and-array-contains | core | absent | TQL wave (grammar) + GAP-Q3 (predicate expansion) |
| PB-4006 | range-and-between-comparisons | core | tracer-partial | GAP-Q2 (exact predicate path, BETWEEN, dates) |
| PB-4007 | prefix-suffix-and-regex-matching | core | absent | TQL wave (grammar) + GAP-Q3 |
| PB-4008 | full-text-search-and-search-operators | core | absent | TQL wave (grammar) + GAP-Q13 (analyzer foundation) |
| PB-4009 | phrase-fuzzy-proximity-and-boosting | optional | absent | GAP-Q13 |
| PB-4010 | intersect-query | optional | absent | TQL wave (grammar) + GAP-Q3 |
| PB-4011 | named-parameters-and-parameter-prefix | core | absent | TQL wave #32-#35 |
| PB-4012 | filter-clause-and-filter-limit | core | absent | TQL wave (grammar) + GAP-Q11 (scan accounting) |
| PB-4013 | sorting-null-ordering-and-score | core | tracer-partial | GAP-Q4 (nulls placement, score, random seed parity) |
| PB-4014 | paging-limit-offset-and-caps | core | tracer-partial | Keep unresolved; extend in GAP-Q11 (skipped/capped accounting) |
| PB-4015 | select-load-and-projection | core | tracer-partial | TQL wave (grammar) + GAP-Q5 (LOAD/computed projection) |
| PB-4016 | document-counter-and-timeseries-includes | core | cross-seam-partial | GAP-Q7 (includes contract, families coordination) |
| PB-4017 | distinct-results-and-counting | core | absent | GAP-Q8 |
| PB-4018 | grouping-and-aggregation | core | absent | GAP-Q8 (static) + IX wave #37-#40 (dynamic, PB-4046) |
| PB-4019 | facet-aggregation | optional | absent | GAP-Q13 |
| PB-4020 | suggestions | optional | absent | GAP-Q13 |
| PB-4021 | more-like-this | optional | absent | GAP-Q13 |
| PB-4022 | spatial-predicates | optional | absent | GAP-Q14 |
| PB-4023 | spatial-distance-ordering | optional | absent | GAP-Q14 |
| PB-4024 | vector-search | optional | absent | **Required CP row** (CP-01/AI-01); engine work routed via CP |
| PB-4025 | query-result-shape-and-metadata | core | tracer-partial | GAP-Q11 (adapter-facing result shape) |
| PB-4026 | staleness-and-wait-for-non-stale-results | core | tracer-partial | GAP-Q9 (bounded wait semantics) |
| PB-4027 | query-customization-cache-tracking-and-tags | core | absent | Route to adapters/SDK waves; note in GAP-Q11 |
| PB-4028 | query-statistics | core | tracer-partial | GAP-Q11 |
| PB-4029 | query-timings-and-plan | optional | absent | GAP-Q11 (timing fields) + GAP-Q13 (plan tree) |
| PB-4030 | query-explanations | optional | absent | GAP-Q13 |
| PB-4031 | query-highlighting | optional | absent | GAP-Q13 (term-vector dependency PB-4047) |
| PB-4032 | linq-query-translation | protocol | absent | TQL wave (RQL parity mapping) + SDK waves |
| PB-4033 | terminal-cardinality-operations | core | absent (client) | Route to SDK/adapter; engine note in GAP-Q11 |
| PB-4034 | raw-async-and-lazy-query-execution | protocol | tracer-partial (one seam) | Route to adapters/SDK; GAP-Q12 (cancellation lifecycle) |
| PB-4035 | query-streaming-formats-and-cancellation | core | cross-seam-partial | W4-STREAM owns (PB-6008); GAP-Q15 extends |
| PB-4036 | sharded-query-routing-and-reduction | core | absent | SH wave #47-#51 |
| PB-4037 | patch-by-query | core | absent | GAP-Q10 |
| PB-4038 | delete-by-query | core | absent | GAP-Q10 |
| PB-4039 | subscription-query-grammar-and-restrictions | optional | cross-seam-partial | TQL wave (grammar restrictions) + streaming seam |
| PB-4040 | query-routes-and-authorization | protocol | tracer-partial (request enum only) | GAP-Q11 + adapters wave; charter PD-2026-08-26-11 noted |
| PB-4041 | query-validation-and-error-contract | core | tracer-partial | TQL wave (error surface) + GAP-Q1-adjacent error granularity |
| PB-4042 | static-map-reduce-indexes | core | absent | GAP-Q6 |
| PB-4043 | multi-map-indexes | core | absent | GAP-Q6 |
| PB-4044 | javascript-map-and-map-reduce-indexes | legacy | absent | GAP-Q16 (explicit legacy disposition) |
| PB-4045 | auto-map-indexes | core | absent | IX wave #37-#40 (PD-2026-08-26-09) |
| PB-4046 | auto-map-reduce-indexes | core | absent | IX wave #37-#40 |
| PB-4047 | index-field-storage-indexing-and-term-vectors | core | absent | GAP-Q6 (field options) |
| PB-4048 | analyzers-and-indexed-suggestions | optional | absent | GAP-Q13 |
| PB-4049 | dynamic-and-compound-index-fields | optional | absent | GAP-Q6 |
| PB-4050 | spatial-index-options | optional | absent | GAP-Q14 |
| PB-4051 | vector-index-options-and-quantization | optional | absent | **Required CP row**; routed via CP with PB-4024 |
| PB-4052 | corax-lucene-search-engine-selection | core | absent | GAP-Q17 (explicit engine disposition) |
| PB-4053 | index-create-delete-start-stop-reset-and-enable | core | absent | GAP-Q18 (lifecycle operations) |
| PB-4054 | index-state-staleness-and-rebuild | core | tracer-partial | GAP-Q18 (state/progress) + keep W3-2 evidence |
| PB-4055 | index-errors-and-failure-information | core | tracer-partial | GAP-Q18 |
| PB-4056 | index-metadata-terms-and-performance-observability | admin | absent | GAP-Q18 (observability ops) |
| PB-4057 | index-lock-priority-deployment-and-side-by-side | admin | absent | GAP-Q18 |
| PB-4058 | counter-and-timeseries-index-source-types | optional | absent | GAP-Q19 (families-index coordination) |
| PB-4059 | map-reduce-output-and-reference-collections | optional | absent | GAP-Q6 (after reduce exists) |
| PB-4060 | sharded-index-definition-and-query-readers | core | absent | SH wave #47-#51 |
| PB-4061 | index-benchmark-observation-contract | protocol | absent | GAP-Q20 (benchmark observation fields) |
| PB-4062 | index-admin-authorization-and-private-operations | admin | tracer-partial | GAP-Q18 + Platform Adapter (authorization class) |

Count: 63 rows. Verdicts: 16 tracer-partial, 3 cross-seam-partial,
44 absent. Every row's authoritative status stays `unresolved`.

---

## Row sections

Shared evidence anchors, cited throughout:

- The one compile seam: `QueryStore::compile`/`execute`
  (`src/query/mod.rs:204-288`).
- The typed clause set: `src/query/spec.rs` (conditions `:86-156`,
  ordering `:193-267`, projection `:271-309`, paging `:312-343`,
  staleness `:396-457`, spec `:459-569`).
- The V1-compatible grammar subset: `src/query/v1.rs:11-32` (defined
  subset) and `:180-247` (`parse_clauses`).
- The execution path: `src/query/execute.rs:101-175` (validation,
  index-less scan, stable sort, materialize).
- The result contract: `src/query/result.rs:186-237`.
- The index catalog: `src/index/mod.rs:82-483` (segments, epochs,
  generations, builder).
- The W3-2 design record and its per-row notes:
  `docs/query/w3-query-index.md:283-306`.

### PB-4000 query-execution-results-and-errors (core)

- Status: `unresolved`; verdict tracer-partial.
- Evidence: one compile/execute seam with typed errors —
  `src/query/mod.rs:204-230` (`compile` over `QueryInput::Typed` and
  `QueryInput::V1Compatible`), `:57-78` (`QueryError`), `:291-299`
  (grammar rejection maps to `InvalidSpec`); result shape
  `src/query/result.rs:186-237`. Tests:
  `query_store_compiles_and_executes_against_supplied_snapshot`
  (`src/query/tests.rs:469`),
  `query_input_rejects_unsupported_or_generic_query_text`
  (`src/query/tests.rs:582`),
  `query_plan_preserves_result_ordering_on_an_immutable_snapshot`
  (`src/query/tests.rs:225`). V1's documented result metadata (index name,
  stale flag, timestamp, etag, node tag, total, skipped, scanned, duration,
  includes) is absent — see PB-4025/PB-4028.
- Proposed disposition: keep unresolved. The executable-grammar surface
  (full RQL grammar, location-bearing errors) is TQL-wave scope; the V2
  result/error shape at the adapter is GAP-Q11.
- Cross-references: TQL wave #32-#35; PB-4025, PB-4041;
  `docs/query/w3-query-index.md:289-291`.

### PB-4001 static-index-definition-and-planning (core)

- Status: `unresolved`; verdict absent.
- Evidence: no typed index definition exists. A namespace is a tenant plus
  a logical name plus one registered field only —
  `IndexNamespace` (`src/query/spec.rs:351-394`),
  `IndexCatalog::ensure_namespace` (`src/index/mod.rs:220-227`). No maps,
  no reduce, no field options, no source type, no definition comparison,
  no replacement, no per-definition stale/error behavior. Query
  "selection" is generation-gating only (`src/query/execute.rs:69-98`);
  no definition-based planning exists.
- Proposed disposition: keep unresolved; GAP-Q6 (typed static index
  definition contract) in `engine/index`. Selection quality/optimizer
  behavior is additionally constrained by the IX wave (PD-2026-08-26-09:
  measured selection quality with recorded reasons).
- Cross-references: IX wave #37-#40 (optimizer); PB-4042, PB-4043,
  PB-4047; charter PD-2026-08-26-09.

### PB-4002 query-source-selection-and-aliases (core)

- Status: `unresolved`; verdict tracer-partial (collection source only).
- Evidence: `QuerySource` selects exactly one user collection and rejects
  the reserved family collection (`src/query/spec.rs:54-78`,
  `:66-71`); grammar `from collection` only
  (`src/query/v1.rs:190-197`). No index-name source (depends on PB-4001),
  no FROM aliases (grammar has none; identifiers are single words —
  `src/query/v1.rs:113-115`), no map-reduce mode selection. Source
  filtering at execution: `src/query/execute.rs:150-152`.
- Proposed disposition: keep unresolved. Grammar/alias surface is TQL
  wave; the index-name source waits on GAP-Q6.
- Cross-references: TQL wave #32-#35; PB-4001; family handoffs noted in
  `docs/query/w3-query-index.md:308-315`.

### PB-4003 boolean-expression-precedence-and-negation (core)

- Status: `unresolved`; verdict absent.
- Evidence: the typed predicate is a pure conjunction —
  `QueryPredicate::all` (`src/query/spec.rs:163-190`), evaluation
  `matches` (`src/query/model.rs:157-162`). The grammar explicitly rejects
  `or`, parentheses, and negation (`src/query/v1.rs:27-31`; tokenizer has
  `(`/`)` punct only for `exists()`/`missing()` — `src/query/v1.rs:63`,
  `:250-263`). No AST with precedence exists to flatten.
- Proposed disposition: keep unresolved; TQL wave #32-#35 owns the typed,
  versioned AST with boolean precedence (PD-2026-08-26-08). Engine-side
  evaluation of OR/negation follows that AST; no separate engine ticket
  until the AST lands.
- Cross-references: TQL wave #32-#35; charter PD-2026-08-26-08.

### PB-4004 equality-null-missing-and-exists (core)

- Status: `unresolved`; verdict tracer-partial.
- Evidence: `matches_condition` (`src/query/model.rs:165-182`):
  `Equals` is structural equality against a present value (present null
  distinct from absent), `Exists` matches a present field including null,
  `NotEquals` matches when the field is absent **or** not equal,
  `Missing` matches absence. Tested by
  `null_missing_and_exists_have_distinct_results`
  (`src/query/tests.rs:271`). The design record labels these V2 tracer
  semantics, not V1 parity claims (`docs/query/w3-query-index.md:141-147`,
  `:292`).
- Proposed disposition: keep unresolved; GAP-Q1 — a V1-parity test must
  pin V1's exact equality-null rule (V1 `WhereNotEquals` on a missing
  field, analyzed vs exact modes) before the tracer semantics can be
  called compatible or must change.
- Cross-references: PB-4000; W3-2 per-row note PB-4004
  (`docs/query/w3-query-index.md:292`).

### PB-4005 membership-in-and-array-contains (core)

- Status: `unresolved`; verdict absent.
- Evidence: the closed condition set has no IN/Contains variants
  (`src/query/spec.rs:86-156`); the grammar has no `in` keyword
  (`src/query/v1.rs:250-291`); array values are not indexable
  (`value_key` returns `None` for arrays — `src/index/mod.rs:474-483`).
- Proposed disposition: keep unresolved; TQL wave owns grammar; engine
  predicate/evaluator expansion tracked in GAP-Q3.
- Cross-references: TQL wave #32-#35; PB-4047 (array field options).

### PB-4006 range-and-between-comparisons (core)

- Status: `unresolved`; verdict tracer-partial.
- Evidence: comparison conditions with in-domain ordering
  (`src/query/model.rs:171-181`, `compare` `:190-215`); grammar operators
  `< <= > >=` (`src/query/v1.rs:266-274`); tested in
  `v1_grammar_parses_the_supported_subset_into_typed_clauses`
  (`src/query/tests.rs:1045`). Gaps: no `BETWEEN`, no date literal or date
  ordering (literals are string/integer/bool/null only —
  `src/query/v1.rs:275-282`), and a documented predicate-precision
  residual: the predicate path still widens `Integer` to `f64` above
  2^53 while the order-by path is exact
  (`docs/query/w3-query-index.md:398-406`).
- Proposed disposition: keep unresolved; GAP-Q2 — exact cross-type
  comparison in the predicate path (the a3 residual), `BETWEEN`, and
  date-typed ordering.
- Cross-references: PB-4013 (shared comparator); W3-2 per-row note
  PB-4006 (`docs/query/w3-query-index.md:293`).

### PB-4007 prefix-suffix-and-regex-matching (core)

- Status: `unresolved`; verdict absent.
- Evidence: no StartsWith/EndsWith/Regex conditions
  (`src/query/spec.rs:86-156`); grammar has no such operators; the index
  key space is exact scalar keys only (`src/index/mod.rs:474-483`).
- Proposed disposition: keep unresolved; TQL grammar plus GAP-Q3
  predicate expansion.
- Cross-references: TQL wave #32-#35; PB-4048 (analyzers affect pattern
  matching).

### PB-4008 full-text-search-and-search-operators (core)

- Status: `unresolved`; verdict absent.
- Evidence: no Search operator, no analyzed fields, no scoring, no
  stopwords (`src/query/**` and `src/index/**` contain no analyzer or
  search machinery; `value_key` builds exact scalar keys —
  `src/index/mod.rs:474-483`).
- Proposed disposition: keep unresolved; grammar is TQL wave; the
  analyzer/tokenization foundation is GAP-Q13. The contract requires an
  explicit V2 analyzer compatibility record before any parity claim.
- Cross-references: TQL wave #32-#35; PB-4048; PB-4047.

### PB-4009 phrase-fuzzy-proximity-and-boosting (optional)

- Status: `unresolved`; verdict absent.
- Evidence: no phrase/fuzzy/proximity/boost constructs anywhere in
  `src/query/` or `src/index/` (condition set closed at
  `src/query/spec.rs:86-156`; no scoring model exists).
- Proposed disposition: keep unresolved; optional row requiring an
  evidence-backed disposition (implement or accepted-exclusion) after
  GAP-Q13's analyzer foundation; contract forbids silent equality
  substitution.
- Cross-references: PB-4008, PB-4048; GAP-Q13.

### PB-4010 intersect-query (optional)

- Status: `unresolved`; verdict absent.
- Evidence: grammar rejects parentheses and `or`
  (`src/query/v1.rs:27-31`), so no intersect expression can be written;
  typed predicate is a flat conjunction (`src/query/spec.rs:163-190`).
- Proposed disposition: keep unresolved; TQL grammar first; evaluator
  work rides GAP-Q3 with PB-4003's boolean AST.
- Cross-references: PB-4003; TQL wave #32-#35.

### PB-4011 named-parameters-and-parameter-prefix (core)

- Status: `unresolved`; verdict absent.
- Evidence: literals only in the grammar (`src/query/v1.rs:275-282`); no
  parameter map on `QuerySpec` (`src/query/spec.rs:463-569`); no
  AddParameter equivalent anywhere in the seam.
- Proposed disposition: keep unresolved; TQL wave #32-#35 owns
  parameterization (typed AST binding). Secret-handling downstream impact
  must be checked when the parameter form exists.
- Cross-references: TQL wave #32-#35; PB-4041.

### PB-4012 filter-clause-and-filter-limit (core)

- Status: `unresolved`; verdict absent.
- Evidence: no FILTER clause in the grammar (`src/query/v1.rs:11-32`);
  no post-index filter evaluator (everything is one predicate —
  `src/query/execute.rs:145-156`); no scanned-results accounting (the
  result carries `matched` only — `src/query/result.rs:213-216`).
- Proposed disposition: keep unresolved; grammar is TQL wave; the
  post-index evaluator and `ScannedResults` accounting are engine/query
  work bundled into GAP-Q11 (result/statistics extension).
- Cross-references: TQL wave #32-#35; PB-4028; GAP-Q11.

### PB-4013 sorting-null-ordering-and-score (core)

- Status: `unresolved`; verdict tracer-partial.
- Evidence: multi-key ordering with per-key direction
  (`src/query/spec.rs:193-267`), stable sort under a defined total order
  with missing-first/null-second ranks (`src/query/model.rs:224-237`,
  `compare_values` `:294-338`, `compare_documents` `:379-395`,
  `src/query/execute.rs:160-161`). Tests:
  `total_document_order_ranks_missing_and_types_deterministically`
  (`src/query/tests.rs:1248`),
  `nan_and_non_finite_numbers_keep_a_total_deterministic_order`
  (`src/query/tests.rs:1279`). Absent: explicit NULLS FIRST/LAST (V1
  `NullsOrdering`), score ordering, random ordering with seed,
  `OrderingType` string/numeric distinction, custom sorters.
- Proposed disposition: keep unresolved; GAP-Q4 — null-placement
  direction, score and seeded-random ordering parity once PB-4008
  scoring exists; the W3-2 rank order is explicitly a tracer rule
  (`src/query/model.rs:217-223`).
- Cross-references: PB-4008; W3-2 per-row note PB-4013
  (`docs/query/w3-query-index.md:294`).

### PB-4014 paging-limit-offset-and-caps (core)

- Status: `unresolved`; verdict tracer-partial.
- Evidence: `QueryPaging` offset plus optional limit with typed bounds —
  zero limit and limits above `MAX_PAGE_LIMIT` (1024) rejected
  (`src/query/spec.rs:312-343`); grammar `limit`/`offset`
  (`src/query/v1.rs:222-232`); materialization honors offset/limit
  (`src/query/execute.rs:185-208`); tested in
  `projection_and_paging_have_typed_errors_and_boundaries`
  (`src/query/tests.rs:816`). Absent: `LIMIT offset,take` two-argument
  form, capped-total and skipped-result accounting (`CappedMaxResults`/
  `SkippedResults` — result carries `matched` only,
  `src/query/result.rs:213-216`).
- Proposed disposition: keep unresolved; two-argument form is TQL
  grammar; capped/skipped accounting rides GAP-Q11.
- Cross-references: PB-4028; W3-2 per-row note PB-4014
  (`docs/query/w3-query-index.md:295`).

### PB-4015 select-load-and-projection (core)

- Status: `unresolved`; verdict tracer-partial.
- Evidence: whole-document or named top-level field projection
  (`src/query/spec.rs:271-309`), applied at
  `src/query/execute.rs:211-222`; absent projected fields are omitted and
  stay distinct from present nulls (`src/query/result.rs:32-38`); tested
  in `projection_and_paging_have_typed_errors_and_boundaries`
  (`src/query/tests.rs:816`). Absent: LOAD, aliases, computed
  projections, stored-field vs source-document distinction (no stored
  fields exist — segments store document ids only,
  `src/index/mod.rs:82-124`), nested/array/metadata/dictionary
  projection.
- Proposed disposition: keep unresolved; SELECT/LOAD grammar is TQL
  wave; LOAD and computed projection semantics are GAP-Q5.
- Cross-references: TQL wave #32-#35; PB-4047 (stored fields); W3-2
  per-row note PB-4015 (`docs/query/w3-query-index.md:296`).

### PB-4016 document-counter-and-timeseries-includes (core)

- Status: `unresolved`; verdict cross-seam-partial (family data exists;
  include contract absent).
- Evidence: `QueryResult` has no includes field
  (`src/query/result.rs:186-237`); the grammar has no INCLUDE clause
  (`src/query/v1.rs:11-32`). Adjacent seam: the families module carries
  counter and time-series reads (`FamilyKind::Counters`/`TimeSeries`
  `src/families/mod.rs:54-60`; `FamilyReadRequest::CounterGroup`/
  `TimeSeriesRange` `src/families/mod.rs:143-183`), but nothing connects
  them to query results.
- Proposed disposition: keep unresolved; GAP-Q7 — an includes contract at
  the query result seam coordinating with the families seam and the W4
  stream includes surface.
- Cross-references: PB-3010/PB-3020 family handoffs
  (`docs/query/w3-query-index.md:308-315`); PB-6008; GAP-Q7.

### PB-4017 distinct-results-and-counting (core)

- Status: `unresolved`; verdict absent.
- Evidence: no DISTINCT in grammar (`src/query/v1.rs:11-32`), no distinct
  flag or dedup on `QuerySpec`/`QueryResult` (`src/query/spec.rs:459-569`,
  `src/query/result.rs:186-237`).
- Proposed disposition: keep unresolved; grammar is TQL wave; engine
  distinct/count interplay in GAP-Q8.
- Cross-references: PB-4018; GAP-Q8.

### PB-4018 grouping-and-aggregation (core)

- Status: `unresolved`; verdict absent.
- Evidence: no GROUP BY in the grammar (`src/query/v1.rs:11-32`), no
  grouping/aggregation clauses on the typed spec
  (`src/query/spec.rs:459-569`), no reduce in the index catalog
  (single-field map segments only — `src/index/mod.rs:446-470`).
- Proposed disposition: keep unresolved; GAP-Q8 (static aggregation with
  GAP-Q6 map-reduce) — dynamic GROUP BY auto-indexing is IX wave scope
  via PB-4046.
- Cross-references: PB-4042, PB-4046; IX wave #37-#40; GAP-Q6, GAP-Q8.

### PB-4019 facet-aggregation (optional)

- Status: `unresolved`; verdict absent.
- Evidence: no facet types or aggregation operations anywhere in
  `src/query/` or `src/index/` (verified by symbol search; the only
  "facet"-adjacent text is doc commentary).
- Proposed disposition: keep unresolved; optional row gated on GAP-Q13
  (analyzers/terms) and GAP-Q8 (aggregation); needs an explicit
  implement-or-exclude disposition before release.
- Cross-references: PB-4018, PB-4048; GAP-Q13.

### PB-4020 suggestions (optional)

- Status: `unresolved`; verdict absent.
- Evidence: no suggestion machinery (`src/query/**`, `src/index/**` — no
  suggestion options, no string-distance layer).
- Proposed disposition: keep unresolved; optional row gated on GAP-Q13
  (analyzer compatibility record required by the contract).
- Cross-references: PB-4048; GAP-Q13.

### PB-4021 more-like-this (optional)

- Status: `unresolved`; verdict absent.
- Evidence: no more-like-this scope, options, or analyzer-based scoring
  anywhere in the query/index seams.
- Proposed disposition: keep unresolved; optional row gated on GAP-Q13;
  explicit analyzer-assumption disposition required.
- Cross-references: PB-4008; GAP-Q13.

### PB-4022 spatial-predicates (optional)

- Status: `unresolved`; verdict absent.
- Evidence: no spatial types, criteria, WKT, or geometry anywhere in
  `src/query/` or `src/index/` (index keys are scalar strings —
  `src/index/mod.rs:474-483`).
- Proposed disposition: keep unresolved; optional pair with PB-4050;
  GAP-Q14 records the engine decision point (geometry library and unit
  contract) before implementation.
- Cross-references: PB-4023, PB-4050; GAP-Q14.

### PB-4023 spatial-distance-ordering (optional)

- Status: `unresolved`; verdict absent.
- Evidence: ordering keys are field names with direction only
  (`src/query/spec.rs:193-231`); no distance ordering, no WKT point
  forms, no rounding options.
- Proposed disposition: keep unresolved; rides GAP-Q14 with PB-4022.
- Cross-references: PB-4022, PB-4050; GAP-Q14.

### PB-4024 vector-search (optional classification; **required CP row**)

- Status: `unresolved`; verdict absent.
- Evidence: no vector conditions, embedding types, similarity thresholds,
  or quantization anywhere in `src/query/` or `src/index/`. The condition
  set is closed (`src/query/spec.rs:86-156`) and index keys are scalar
  (`src/index/mod.rs:474-483`).
- Proposed disposition: keep unresolved and **in scope as required**: the
  PS-02 brief and the charter's AI-native rule (PD-2026-08-26-07, CP-01,
  AI-01) make vector search a required competitive row, not an optional
  discard. Engine implementation is routed through the CP matrix
  coordination; no independent optional-exclusion question exists.
- Cross-references: PB-4051 (index options); CP-01, AI-01; charter
  PD-2026-08-26-07.

### PB-4025 query-result-shape-and-metadata (core)

- Status: `unresolved`; verdict tracer-partial.
- Evidence: `QueryResult` carries rows, matched count, applied ordering,
  staleness report, and index generation
  (`src/query/result.rs:186-237`); rows carry document plus projected
  fields (`src/query/result.rs:12-40`). Absent versus the V1 contract:
  document-vs-projection-vs-entry-vs-map-reduce distinction, index name,
  stale flag in V1 shape, timestamp, etag, node tag, skipped, scanned,
  duration, includes. No wire serialization exists yet (TBEP adapter
  unresolved).
- Proposed disposition: keep unresolved; GAP-Q11 defines the
  adapter-facing V2 result shape and its metadata fields before any
  adapter/SDK proof.
- Cross-references: PB-4028, PB-4040; adapters seam (unresolved).

### PB-4026 staleness-and-wait-for-non-stale-results (core)

- Status: `unresolved`; verdict tracer-partial.
- Evidence: `StalenessPolicy`/`StalenessMode` strict/allow-stale
  (`src/query/spec.rs:396-457`); generation gate with epoch comparability
  (`src/query/execute.rs:69-98`), failing strict with
  `QueryError::IndexNotReady` instead of silently serving stale results;
  per-namespace observations (`src/query/result.rs:76-150`). Tests:
  `strict_generation_rejects_an_index_that_is_too_old`
  (`src/query/tests.rs:320`),
  `allowed_stale_query_reports_the_generation_it_used`
  (`src/query/tests.rs:376`),
  `query_rejects_generation_from_another_namespace_or_epoch`
  (`src/query/tests.rs:720`). Absent: bounded wait-for-non-stale (timeout
  parameter, wait loop, V1 timeout error category), and staleness
  definitions for disabled/errored/deleted/side-by-side index states
  (no such states exist — see PB-4054).
- Proposed disposition: keep unresolved; GAP-Q9 — bounded wait semantics
  plus state-specific staleness once PB-4054 states exist.
- Cross-references: PB-4054; W3-2 per-row note PB-4026
  (`docs/query/w3-query-index.md:297`).

### PB-4027 query-customization-cache-tracking-and-tags (core)

- Status: `unresolved`; verdict absent.
- Evidence: no query-result cache, no session/unit-of-work tracking, no
  caller tags, and no before/after callbacks in the engine seam
  (`src/query/` exposes only compile/execute). These are client/session
  concerns over the wire.
- Proposed disposition: keep unresolved; route to the adapters/SDK waves
  when those surfaces exist. Engine prerequisite noted in GAP-Q11: no
  cache seam exists to bypass yet, so NoCaching parity is presently
  vacuous — record that explicitly rather than claiming it.
- Cross-references: PB-4040; GAP-Q11.

### PB-4028 query-statistics (core)

- Status: `unresolved`; verdict tracer-partial.
- Evidence: opt-in-free facts exist: `matched` (total before paging —
  `src/query/result.rs:213-216`), ordering, staleness report,
  index generation (`src/query/result.rs:186-237`). Absent: skipped and
  scanned counts, duration, index timestamp, result etag, node tag, and
  the request-opt-in distinction.
- Proposed disposition: keep unresolved; GAP-Q11 (statistics fields and
  opt-in semantics; benchmark-adapter dependency).
- Cross-references: PB-4012, PB-4025, PB-4061.

### PB-4029 query-timings-and-plan (optional)

- Status: `unresolved`; verdict absent.
- Evidence: no timing collection and no query plan/inspection tree
  anywhere in the seam (execution path records no timings —
  `src/query/execute.rs:101-175`).
- Proposed disposition: keep unresolved; timing fields in GAP-Q11; the
  plan/inspection tree is deferred until an index-using planner exists
  (GAP-Q6), since the current plan is the query itself. Contract's
  secret-safe rule applies when parameters exist (PB-4011).
- Cross-references: PB-4011, PB-4061; GAP-Q11, GAP-Q6.

### PB-4030 query-explanations (optional)

- Status: `unresolved`; verdict absent.
- Evidence: no explanation options, routes, or result fields
  (`src/query/result.rs:186-237` carries none).
- Proposed disposition: keep unresolved; depends on an index-serving
  executor (GAP-Q6) to have anything to explain; authorization-protected
  diagnostic routing happens at the Platform per charter.
- Cross-references: PB-4040; GAP-Q6.

### PB-4031 query-highlighting (optional)

- Status: `unresolved`; verdict absent.
- Evidence: no highlighting options, fragment parameters, or term-vector
  field settings (`src/index/mod.rs` has no field options at all).
- Proposed disposition: keep unresolved; hard dependency on PB-4047
  term-vector modes and GAP-Q13 analyzers; explicit disposition required
  before release.
- Cross-references: PB-4047, PB-4048; GAP-Q13.

### PB-4032 linq-query-translation (protocol)

- Status: `unresolved`; verdict absent.
- Evidence: no LINQ surface exists in the Rust engine (by charter the
  external surface is C# hosts speaking TBEP; engine input is the typed
  `QuerySpec` or V1-compatible text — `src/operations.rs:201-219`). V1
  LINQ translation is a client-adapter concern.
- Proposed disposition: keep unresolved; TQL wave owns the RQL parity
  mapping (PD-2026-08-26-08); LINQ-equivalent translation belongs to the
  .NET SDK wave and must target the typed AST, not a wire string.
- Cross-references: TQL wave #32-#35; charter PD-2026-08-26-08;
  PB-4034.

### PB-4033 terminal-cardinality-operations (core)

- Status: `unresolved`; verdict absent (client-side row; engine facts
  partial).
- Evidence: engine-side, `matched` reports the full matching count
  independent of the page (`src/query/result.rs:213-216`), which
  supports Count/LongCount semantics; no First/Single/Any surfaces exist
  in the engine (client cardinality checks). No caps on count itself are
  defined (the scan is bounded only by deadline/cancellation —
  `src/query/execute.rs:145-156`).
- Proposed disposition: keep unresolved; route to SDK/adapter waves;
  GAP-Q11 records the count-cap decision (documented caps per V1) that
  the engine must expose.
- Cross-references: PB-4014, PB-4028; GAP-Q11.

### PB-4034 raw-async-and-lazy-query-execution (protocol)

- Status: `unresolved`; verdict tracer-partial (one seam; execution modes
  absent).
- Evidence: both input forms normalize through one compile seam —
  `QueryStore::compile` (`src/query/mod.rs:204-230`), tested by
  `query_input_normalizes_typed_and_v1_text_through_one_compile_seam`
  (`src/query/tests.rs:512`); the public text wrapper validates the same
  grammar (`V1QueryText::try_new`, `src/operations.rs:182-190`). Raw
  text, sync, async, and lazy execution modes are wire/client concerns
  that do not exist yet. Cancellation is handed over by value and
  re-read at bounded checkpoints (`src/query/model.rs:126-140`;
  `src/query/execute.rs:29-40`); the live-signal lifecycle is assigned
  to the later wire adapter (`docs/query/w3-query-index.md:389-394`).
- Proposed disposition: keep unresolved; adapters/SDK waves own execution
  modes; GAP-Q12 tracks the cancellation lifecycle handoff.
- Cross-references: PB-4035; GAP-Q12.

### PB-4035 query-streaming-formats-and-cancellation (core)

- Status: `unresolved`; verdict cross-seam-partial.
- Evidence: the W4 streaming seam streams compiled query plans in plan
  order with bounded batches, position tracking, and generation
  reporting: `StreamSource::Query(QueryPlan)`
  (`src/streaming/mod.rs:288-293`), query source offset/exhausted
  positions (`src/streaming/sources.rs:35-36`, `:65`), batch index
  generation (`src/streaming/mod.rs:611-648`), typed query-error mapping
  (`src/streaming/sources.rs:155-156`). Absent versus this row: CSV
  format, includes in streams, and explicit equivalence proof to paged
  results for identical settings.
- Proposed disposition: keep unresolved; W4-STREAM owns the streaming
  contract (mapped to PB-6008 in `docs/parity/w4-row-mapping.md:38`);
  GAP-Q15 records the query-specific residuals (CSV, includes, page
  equivalence).
- Cross-references: PB-6008 (W4-STREAM); PB-4016; GAP-Q15.

### PB-4036 sharded-query-routing-and-reduction (core)

- Status: `unresolved`; verdict absent (wave-owned).
- Evidence: no shard routing, merger, or sharded map-reduce reduction
  anywhere in the tree; the engine is single-node at this seam.
- Proposed disposition: keep unresolved; SH wave #47-#51 owns routing
  and merge semantics. No new gap ticket — flagging one would duplicate
  the wave's scope.
- Cross-references: SH wave #47-#51; PB-4060; PB-5036..PB-5048 sharding
  rows (D-7 decision index, `docs/parity/w4-row-mapping.md:136-141`).

### PB-4037 patch-by-query (core)

- Status: `unresolved`; verdict absent.
- Evidence: no patch-by-query operation exists. `QueryOperation::try_new`
  is an explicit `unimplemented!` placeholder
  (`src/operations.rs:235-240`); the write path is single-document only
  (`EngineRequest::DocumentWrite`, `src/operations.rs:335-337`); there is
  no query-driven mutation path (`src/query/**` never mutates —
  `src/query/execute.rs:1-7` module contract).
- Proposed disposition: keep unresolved; GAP-Q10 — typed
  patch/delete-by-query operations with V1 option parity
  (allow-stale, stale-timeout, max-operations, details, operation-id,
  wait-for-index), audit and confirmation rules per the AI-native
  charter (PD-2026-08-26-07).
- Cross-references: PB-4038; PB-4026 (stale options); charter
  PD-2026-08-26-07.

### PB-4038 delete-by-query (core)

- Status: `unresolved`; verdict absent.
- Evidence: same as PB-4037 — no delete-by-query; the engine's deletions
  are document-level writes; no typed bulk-selection mutation exists.
- Proposed disposition: keep unresolved; GAP-Q10 together with
  PB-4037 (one operation family; overbroad-deletion risk requires the
  query-selection parity first).
- Cross-references: PB-4037; recovery/audit seams per contract.

### PB-4039 subscription-query-grammar-and-restrictions (optional)

- Status: `unresolved`; verdict cross-seam-partial.
- Evidence: W4 subscriptions exist with **typed change filters**, not
  query-grammar criteria: `SubscriptionSpec` carries a `ChangeFilter`
  and batch bound (`src/streaming/mod.rs:891-960`); delivery is
  kind/collection filtered (`src/streaming/subscription.rs:591-599`).
  There is no subscription query parser to restrict, so the V1
  restriction matrix (reject distinct/group-by/order-by) has no surface
  yet.
- Proposed disposition: keep unresolved; when the TQL wave defines the
  grammar, the subscription restriction set (collection-source-only,
  rejected clauses) must be encoded in the streaming seam's criteria;
  coordinate TQL #32-#35 with the streaming seam owners.
- Cross-references: PB-6003/PB-6006 (W4-STREAM; D-5 script-criteria
  marker); TQL wave #32-#35.

### PB-4040 query-routes-and-authorization (protocol)

- Status: `unresolved`; verdict tracer-partial (request plumbing only).
- Evidence: `EngineRequest::Query { scope, request }` exists as the
  trusted in-process request shape (`src/operations.rs:333-340`), but
  `QueryOperation::try_new` is `unimplemented!`
  (`src/operations.rs:235-240`). By charter there are intentionally no
  HTTP routes, no authentication, and no authorization at the engine
  boundary (PD-2026-08-26-11): the engine binds loopback/UDS, speaks
  TBEP to the local C# host, and authorization/tenant onboarding live at
  the TerraScale Platform. Engine-enforced tenant scoping does exist
  (`src/query/execute.rs:124-133`).
- Proposed disposition: keep unresolved; the "routes" obligation
  translates to typed TBEP operations at the adapters seam plus Platform
  authorization — not engine HTTP. GAP-Q11 and the adapters wave own the
  typed operation surface; no private-route or raw-HTTP escape hatch is
  permitted (workspace invariant).
- Cross-references: adapters seam; charter PD-2026-08-26-10/-11;
  PB-1000-family adapter rows.

### PB-4041 query-validation-and-error-contract (core)

- Status: `unresolved`; verdict tracer-partial.
- Evidence: typed error enums — `QueryError` (`src/query/mod.rs:57-78`),
  `QueryInputError` (`src/operations.rs:45-52`); grammar rejections are
  typed and tested (`query_input_rejects_unsupported_or_generic_query_text`
  `src/query/tests.rs:582`; `typed_clause_validations_are_typed_and_bounded`
  `src/query/tests.rs:1065`); errors carry no document values or paths,
  satisfying the secret-safe rule by construction. Gap: all grammar
  failures collapse into `QueryError::InvalidSpec` with no clause or
  location context (`src/query/mod.rs:291-299`) — V1 exposes line/column
  and clause identification.
- Proposed disposition: keep unresolved; the parser error surface
  (distinguishable categories, location context, secret-safe text) is
  TQL-wave scope on the typed AST; the engine keeps the typed enum
  surface.
- Cross-references: TQL wave #32-#35; PB-4000; W3-2 per-row note PB-4041
  (`docs/query/w3-query-index.md:298`).

### PB-4042 static-map-reduce-indexes (core)

- Status: `unresolved`; verdict absent.
- Evidence: the catalog's segments are single-field maps from scalar keys
  to document ids (`src/index/mod.rs:82-124`, `build_segment`
  `:446-470`); there is no reduce definition, no reduce-key identity, no
  persisted aggregate output, and no query projection from reduce
  outputs.
- Proposed disposition: keep unresolved; GAP-Q6 (map-reduce definitions
  with stable reduce keys and aggregation values) — prerequisite for
  PB-4018 static aggregation and PB-4059.
- Cross-references: PB-4018, PB-4046, PB-4059; GAP-Q6.

### PB-4043 multi-map-indexes (core)

- Status: `unresolved`; verdict absent.
- Evidence: a namespace registers exactly one field over the whole tenant
  (`ensure_namespace` `src/index/mod.rs:220-227`); there is no
  multi-source map list, no source-type tracking, and no inheritance or
  map-for-all behavior.
- Proposed disposition: keep unresolved; GAP-Q6.
- Cross-references: PB-4001, PB-4042; GAP-Q6.

### PB-4044 javascript-map-and-map-reduce-indexes (legacy)

- Status: `unresolved`; verdict absent.
- Evidence: no JavaScript index compilation, deployment, or execution
  anywhere; the engine has no scripting runtime at all. The charter
  forbids arbitrary code execution in the query language
  (PD-2026-08-26-08) and the contract requires an explicit disposition
  distinguishing a typed index language from legacy JS compatibility.
- Proposed disposition: keep unresolved; GAP-Q16 — parent records the
  explicit legacy disposition (typed index language replacement vs
  accepted-difference with migration story). Must not be silently
  dropped: legacy classification still requires an adjudicated
  disposition per the parity schema.
- Cross-references: charter PD-2026-08-26-08; PB-4042; migration seam
  (logical migration BK-03 for V1 JS index migration).

### PB-4045 auto-map-indexes (core)

- Status: `unresolved`; verdict absent (wave-owned).
- Evidence: no auto-index creation exists. Queries always execute as an
  index-less full scan (`src/query/execute.rs:142-156`); the only index
  registration is the explicit, crate-private `ensure_namespace`
  (`src/index/mod.rs:220-227`); nothing derives index definitions from
  query shape, and no auto-index identity, transition states, or
  merge/supersede/retire behavior exist.
- Proposed disposition: keep unresolved; IX wave #37-#40 owns
  implementation and evidence per charter PD-2026-08-26-09
  (zero-configuration, measured selection quality, explicit-index
  precedence). No new gap ticket.
- Cross-references: IX wave #37-#40; PB-4001, PB-4046; charter
  PD-2026-08-26-09.

### PB-4046 auto-map-reduce-indexes (core)

- Status: `unresolved`; verdict absent (wave-owned).
- Evidence: no GROUP BY exists to trigger auto-map-reduce (see PB-4018);
  the catalog has no reduce capability (`src/index/mod.rs:446-470`).
- Proposed disposition: keep unresolved; IX wave #37-#40. No new gap
  ticket.
- Cross-references: IX wave #37-#40; PB-4018, PB-4042, PB-4045.

### PB-4047 index-field-storage-indexing-and-term-vectors (core)

- Status: `unresolved`; verdict absent.
- Evidence: the only field configuration is the single registered field
  name per namespace (`src/index/mod.rs:220-227`); segments store exact
  scalar keys and document ids only (`src/index/mod.rs:82-124`,
  `value_key` `:474-483`). No storage, analyzed/exact/default indexing,
  term-vector, spatial, vector, or suggestion options; no stored-field
  projections.
- Proposed disposition: keep unresolved; GAP-Q6 — field options are the
  shared input to PB-4008 search, PB-4015 stored projections, PB-4031
  highlighting, and PB-4024 vectors.
- Cross-references: PB-4008, PB-4015, PB-4031, PB-4048; GAP-Q6.

### PB-4048 analyzers-and-indexed-suggestions (optional)

- Status: `unresolved`; verdict absent.
- Evidence: no analyzer definitions, registry, deployment validation, or
  suggestion-indexed fields anywhere in `src/index/` (keys are exact
  scalars, `src/index/mod.rs:474-483`).
- Proposed disposition: keep unresolved; GAP-Q13 — the typed, sandboxed
  analyzer foundation decision gates six optional relevance rows
  (PB-4009, 4019, 4020, 4021, 4031, and this row). Security-review
  downstream impact per contract.
- Cross-references: PB-4008, PB-4009, PB-4019-4021, PB-4031; GAP-Q13.

### PB-4049 dynamic-and-compound-index-fields (optional)

- Status: `unresolved`; verdict absent.
- Evidence: no runtime field creation (CreateField), no compound field
  definitions; field set is fixed at namespace registration
  (`src/index/mod.rs:220-227`).
- Proposed disposition: keep unresolved; rides GAP-Q6 once field options
  exist.
- Cross-references: PB-4047; GAP-Q6.

### PB-4050 spatial-index-options (optional)

- Status: `unresolved`; verdict absent.
- Evidence: no spatial options types (geography/cartesian, geohash/
  quad-tree/bounding-box, bounds, tree level, units) in `src/index/`.
- Proposed disposition: keep unresolved; GAP-Q14 with PB-4022/4023 —
  one geometry/unit decision covers the three rows.
- Cross-references: PB-4022, PB-4023; GAP-Q14.

### PB-4051 vector-index-options-and-quantization (optional classification; **required CP row**)

- Status: `unresolved`; verdict absent.
- Evidence: no vector options (dimensions, embedding types, candidate/
  edge counts, quantization, auto-embedding source) anywhere in
  `src/index/` or `src/query/`.
- Proposed disposition: keep unresolved and in scope as required with
  PB-4024 via the CP matrix; validation-error contract (dimensions,
  counts, quantization combinations) lands with the vector index work.
- Cross-references: PB-4024; CP-01, AI-01.

### PB-4052 corax-lucene-search-engine-selection (core)

- Status: `unresolved`; verdict absent.
- Evidence: V2 implements neither V1 engine — the catalog is the
  in-memory segment store (`src/index/mod.rs:167-312`); there is no
  engine-type selection, no per-index configuration, and no
  cross-engine agreement question yet.
- Proposed disposition: keep unresolved with an **explicit unresolved
  disposition** as the contract allows ("V2 may implement one engine
  first only with an explicit unresolved disposition"): GAP-Q17 records
  that the selection surface and the cross-engine observable-compatibility
  obligation are deferred until a V2 search engine exists; the accepted
  shape may be one engine (accepted-difference for the selection
  surface) but that is a parent decision, not a default.
- Cross-references: PB-4008, PB-4047; IX wave #37-#40 (index
  implementation adjacency).

### PB-4053 index-create-delete-start-stop-reset-and-enable (core)

- Status: `unresolved`; verdict absent.
- Evidence: no index lifecycle operations exist. The crate-private
  internals are `ensure_namespace`/`recreate_namespace`/`publish`
  (`src/index/mod.rs:220-311`); there are no typed create/replace/
  delete/reset/start/stop/enable/disable operations, no state
  transitions, and no query-visible effect of lifecycle changes.
- Proposed disposition: keep unresolved; GAP-Q18 — typed lifecycle
  operations with state transitions; administration authorization class
  resolves at the Platform Adapter (PB-4062).
- Cross-references: PB-4054, PB-4062; GAP-Q18.

### PB-4054 index-state-staleness-and-rebuild (core)

- Status: `unresolved`; verdict tracer-partial.
- Evidence: monotonic generations per namespace and epoch with
  replay/reorder rejection (`src/index/mod.rs:279-311`; tests
  `index_generation_never_moves_backward_within_its_namespace_and_epoch`
  `src/index/tests.rs:205`, `index_rebuild_publishes_a_monotonic_generation`
  `:254`, `replayed_index_build_cannot_overwrite_a_newer_generation`
  `:282`, `catalog_ignores_replayed_or_reordered_events` `:326`,
  `namespace_epochs_move_only_forward` `:344`); rebuild derives from the
  immutable publication event (`index_delta_is_derived_from_the_immutable_commit_publication_event`
  `src/index/tests.rs:113`); storage lock never held
  (`index_publication_does_not_hold_the_storage_lock` `:143`); staleness
  observations feed queries (`src/query/execute.rs:69-98`). Absent: the
  V1 state enum (Normal/Disabled/Idle/Error/Paused/Pending), progress
  reporting, tombstone accounting, and persistence across restart (the
  catalog is in memory — `docs/query/w3-query-index.md:375-379`).
- Proposed disposition: keep unresolved; GAP-Q18 adds the state model
  and persistence; W3-2 generation monotonicity stays as the foundation
  evidence (not a Proof ID).
- Cross-references: PB-4026, PB-4053; W3-2 per-row note PB-4054
  (`docs/query/w3-query-index.md:299`).

### PB-4055 index-errors-and-failure-information (core)

- Status: `unresolved`; verdict tracer-partial.
- Evidence: typed internal failures — `IndexError`
  (`src/index/mod.rs:33-44`) with the tested builder failure paths
  (`builder_fails_typed_before_any_namespace_or_event_exists`
  `src/index/tests.rs:402`). Absent: error listing/clearing operations,
  per-error text/timestamp/document context, state reporting, and the
  redaction contract for indexed data in diagnostics.
- Proposed disposition: keep unresolved; GAP-Q18.
- Cross-references: PB-4053, PB-4056; W3-2 per-row note PB-4055
  (`docs/query/w3-query-index.md:300`).

### PB-4056 index-metadata-terms-and-performance-observability (admin)

- Status: `unresolved`; verdict absent.
- Evidence: no typed metadata operations: definition retrieval, entry
  counts, terms, staleness/lag, performance counters are absent; segment
  internals are crate-private with `#[allow(dead_code)]` accessors
  (`src/index/mod.rs:91-124`).
- Proposed disposition: keep unresolved; GAP-Q18 (observability
  operations, stable names/units, redaction rules); benchmark-observation
  split tracked by PB-4061/GAP-Q20.
- Cross-references: PB-4061; GAP-Q18, GAP-Q20.

### PB-4057 index-lock-priority-deployment-and-side-by-side (admin)

- Status: `unresolved`; verdict absent.
- Evidence: no lock modes, priorities, deployment modes, rolling
  replacement, or side-by-side identities. Epoch recreation
  (`recreate_namespace` `src/index/mod.rs:238-255`) is the nearest
  mechanism but is an internal identity reset, not a deployment mode
  with cutover semantics.
- Proposed disposition: keep unresolved; GAP-Q18.
- Cross-references: PB-4053, PB-4054; GAP-Q18.

### PB-4058 counter-and-timeseries-index-source-types (optional)

- Status: `unresolved`; verdict absent.
- Evidence: no index source types exist (documents only, implicitly);
  the families seam carries counter/time-series data and typed reads
  (`src/families/mod.rs:54-60`, `:143-183`) but nothing indexes them.
- Proposed disposition: keep unresolved; GAP-Q19 — families/index
  coordination ticket aligned with the family handoffs PB-3010/PB-3020
  that W3-2 explicitly deferred (`docs/query/w3-query-index.md:308-315`).
- Cross-references: PB-3010, PB-3020; PB-4016; GAP-Q19.

### PB-4059 map-reduce-output-and-reference-collections (optional)

- Status: `unresolved`; verdict absent.
- Evidence: no reduce exists (PB-4042), hence no output collections or
  reference documents; no naming patterns or output-document write paths
  in the tree.
- Proposed disposition: keep unresolved; rides GAP-Q6 after map-reduce;
  isolation and migration tests required by the contract before any
  proof.
- Cross-references: PB-4042; GAP-Q6.

### PB-4060 sharded-index-definition-and-query-readers (core)

- Status: `unresolved`; verdict absent (wave-owned).
- Evidence: no shard-local index machinery exists; the catalog is
  single-node in-memory state.
- Proposed disposition: keep unresolved; SH wave #47-#51 owns it. No new
  gap ticket.
- Cross-references: SH wave #47-#51; PB-4036; D-7 sharding scope
  decision (`docs/parity/w4-row-mapping.md:136-141`).

### PB-4061 index-benchmark-observation-contract (protocol)

- Status: `unresolved`; verdict absent.
- Evidence: no performance counters, total-time observation, or
  workload-identity fields exist; the result carries no durations
  (`src/query/result.rs:186-237`).
- Proposed disposition: keep unresolved; GAP-Q20 — observation fields
  (build vs wait vs query time separation) designed together with
  GAP-Q11 statistics; every timing stays diagnostic-only under
  PD-2026-08-26-03 (no local timing becomes a speed claim; Benchmark
  Receipts are mandatory).
- Cross-references: PB-4028, PB-4029, PB-4056; charter
  PD-2026-08-26-03; GAP-Q20.

### PB-4062 index-admin-authorization-and-private-operations (admin)

- Status: `unresolved`; verdict tracer-partial (tenant scoping only).
- Evidence: engine-enforced tenant scoping of index namespaces:
  `IndexNamespace` carries the tenant (`src/query/spec.rs:351-394`);
  cross-tenant requirements are rejected at compile and before any data
  access (`src/query/mod.rs:254-263`, `src/query/execute.rs:124-133`);
  namespace keys cannot alias across tenants
  (`tenant_and_name_separator_cannot_alias_a_namespace`
  `src/index/tests.rs:467`; `query_rejects_a_wrong_tenant_scope`
  `src/query/tests.rs:987`). Absent: all admin operations (lifecycle,
  dump, optimize, test) and their authorization class; per charter there
  is no engine-side authorization — authorization, audit, and
  confirmation live at the Platform (PD-2026-08-26-11), and no
  private/debug admin operation may become a user capability.
- Proposed disposition: keep unresolved; GAP-Q18 provides the typed
  lifecycle operations; the admin authorization class and private-route
  exclusion are Platform Adapter obligations. W3-2's scope-rule evidence
  is recorded but adds no Proof ID.
- Cross-references: PB-4053; Platform Adapter; W3-2 per-row note PB-4062
  (`docs/query/w3-query-index.md:301`).

---

## Proposed gap tickets

Proposed for parent adjudication. IDs here are sweep-local labels
(GAP-Q1..GAP-Q20), not Forgejo tickets. Wave-owned rows (TQL #32-#35,
IX #37-#40, SH #47-#51, CP-required vector rows) deliberately get **no**
new ticket to avoid duplicating wave scope.

| Label | Title | Rows covered | Proposed owner | Priority note |
| --- | --- | --- | --- | --- |
| GAP-Q1 | V1-parity test for null/missing/exists and NotEquals-on-missing semantics | PB-4004 | engine/query | P1 correctness tracer divergence risk |
| GAP-Q2 | Exact cross-type comparison in the predicate path (2^53 residual), BETWEEN, date ordering | PB-4006 | engine/query | P1; residual documented in W3-2 limits |
| GAP-Q3 | Predicate/evaluator expansion: IN/ALL IN/ContainsAny/ContainsAll, prefix/suffix/regex, intersect after boolean AST | PB-4005, 4007, 4010 | engine/query (after TQL AST) | P2 |
| GAP-Q4 | Ordering parity: explicit nulls placement, score ordering, seeded random | PB-4013 | engine/query | P2 |
| GAP-Q5 | LOAD and computed projection semantics (stored-field vs source-document) | PB-4015 | engine/query | P2 |
| GAP-Q6 | Typed static index definition contract: maps, reduce, multi-map, field options, dynamic/compound fields, output collections | PB-4001, 4042, 4043, 4047, 4049, 4059 | engine/index | P1 prerequisite for 4002/4018/4030/4031 |
| GAP-Q7 | Includes contract: document paths, counters, time-series | PB-4016 | engine/query + families seam | P2 |
| GAP-Q8 | DISTINCT and static GROUP BY/aggregation engine semantics | PB-4017, 4018 | engine/query + engine/index | P2 (dynamic side in IX wave) |
| GAP-Q9 | Bounded wait-for-non-stale semantics and state-specific staleness | PB-4026 | engine/query | P2 after GAP-Q18 states |
| GAP-Q10 | Typed patch-by-query and delete-by-query operations with V1 option set, audit, confirmation | PB-4037, 4038 | engine/query + operations | P1 (destructive potential) |
| GAP-Q11 | Adapter-facing V2 result shape: metadata fields (index name, stale, timestamp, etag, node, skipped, scanned, duration), statistics opt-in, count caps, FILTER scan accounting | PB-4000, 4012, 4025, 4027, 4028, 4033, 4040 | engine/query + adapters wave | P1 blocker for SDK/adapter proofs |
| GAP-Q12 | Query cancellation lifecycle handoff for the wire adapter (live signal) | PB-4034 | adapters wave | P3 |
| GAP-Q13 | Analyzer/term-vector foundation decision (typed, sandboxed) gating relevance rows | PB-4008, 4009, 4019, 4020, 4021, 4031, 4048 | engine/index + security review | P1 decision, then implementation |
| GAP-Q14 | Spatial foundation decision: geometry library, units, coordinate contract | PB-4022, 4023, 4050 | engine/index + engine/query | P2 |
| GAP-Q15 | Query-streaming residuals: CSV format, stream includes, page-equivalence proof | PB-4035 | streaming seam (W4 follow-up) | P3 |
| GAP-Q16 | Legacy JavaScript index disposition: typed replacement vs accepted-difference with migration | PB-4044 | parent adjudication | P1 decision |
| GAP-Q17 | Search-engine surface disposition: one-engine-first explicit record; cross-engine obligation deferral | PB-4052 | parent adjudication + engine/index | P1 decision |
| GAP-Q18 | Index lifecycle typed operations, state model, error reporting/clearing, observability ops, lock/priority/side-by-side | PB-4053, 4054, 4055, 4056, 4057, 4062 | engine/index | P1 |
| GAP-Q19 | Counter/time-series index source types (families coordination) | PB-4058 | engine/index + families seam | P3 |
| GAP-Q20 | Benchmark observation fields: build/wait/query time separation, workload identity | PB-4061 | engine/index + benchmark adapter | P2; PD-2026-08-26-03 gates all claims |

Wave-routed (no new ticket): PB-4000 (grammar), 4002 (grammar),
4003, 4005 (grammar), 4007 (grammar), 4008 (grammar), 4009 (grammar),
4010 (grammar), 4011, 4012 (grammar), 4015 (grammar), 4032, 4036, 4039,
4041 (parser surface), 4045, 4046, 4060 — with the engine-side portions
of grammar rows carried by GAP-Q3/Q5/Q8/Q11 as noted per row;
PB-4024 and PB-4051 route through the CP matrix (required rows).

## Method and limits

- Row sources: `parity/contracts/query.json` (all 63 items read in full);
  statuses confirmed against every overlay `parity/overlays/PB-40xx.json`
  (all `unresolved`, empty `proof_ids`).
- Tree evidence: full reads of `src/query/{mod,spec,model,execute,result,
  v1,tests}.rs`, `src/index/{mod,tests}.rs`, targeted reads of
  `src/operations.rs`, `src/streaming/{mod,sources,subscription}.rs`,
  `src/families/mod.rs`, `docs/query/w3-query-index.md`,
  `docs/parity/w4-row-mapping.md`; absence claims verified by symbol
  search across `src/` for facet/suggest/spatial/vector/analyz/distinct/
  group/intersect/boost/fuzzy/regex/patch/delete-by-query (hits are doc
  comments or unrelated identifiers only).
- This note adds no Proof ID, changes no status, and proposes
  dispositions only. Wave ticket numbers (#32-#35 TQL, #37-#40 IX,
  #47-#51 SH) are taken from the PS-02 brief; the CP references
  (CP-01, AI-01) from the workspace charter.
