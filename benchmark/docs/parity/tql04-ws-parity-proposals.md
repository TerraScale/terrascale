# TQL-04 WS-PARITY disposition proposals: query-family rows (PB-4000s)

Status: proposals authored by ticket TQL-04 (wave
`2026-08-26-terrabase-v2-tql-tickets.md`, charter PD-2026-08-26-08 D3).
Documentation only: **no overlay is mutated**. Every row this file touches
remains `unresolved` with an empty Proof ID list until WS-PARITY and the
parent dispose. Date: 2026-08-26.

Proposed statuses use the overlay vocabulary exactly
(`unresolved`, `in-progress`, `implemented`, `qualified`,
`accepted-exclusion`) and obey the recorded transition rule that a row
may move from `unresolved` only to `in-progress` or `accepted-exclusion`.
No Proof ID is minted here: Proof IDs are WS-PARITY's to assign. Each
proposal names its evidence by artifact path and test so the assignment
is mechanical once accepted.

Evidence base:

- **Mapping** — `docs/tql/rql-mapping.md` (85 rows, R01..R85; 42 direct,
  14 translated, 29 accepted-difference).
- **Conformance corpus** — `tests/query/tql_rql_parity_vectors.rs`
  (85 vectors: 24 lower, 24 not-planned, 8 gated, 29 rejected; the
  corpus tests fail when any row lacks a vector) and
  `src/query/tql/parity_tests.rs` (13 execution vectors proving
  result equivalence for the seam-expressible subset, plus the shared
  corpus pin).
- **Findings** — `docs/tql/rql-conformance.md` Section 3 (F1
  numeric-leading identifiers, F2 implicit any-element match not realized
  by the seam, F3 the planning-blocked construct list, F4 gate behavior).
- **Row source** — `parity/contracts/query.json` (63 items,
  PB-4000..PB-4062) at V1 source identity
  `fa99b9257776dbe625e6a2388c74c9ce916637ee`.
- **Wave ownership** — the TQL wave's grammar-surface rows per
  `docs/parity/ps-02-query-sweep.md`: PB-4000, PB-4002, PB-4003, PB-4005,
  PB-4007, PB-4008, PB-4009, PB-4010, PB-4011, PB-4012, PB-4015, PB-4032,
  PB-4041. TQL-04's mapping additionally feeds accepted-difference or
  gated-grammar evidence to PB-4004, PB-4006, PB-4013, PB-4014, PB-4016,
  PB-4017, PB-4018, PB-4021, PB-4022, PB-4023, PB-4024, PB-4037, PB-4038;
  those rows' broader concerns stay with their owners as noted per row.

## Proposals

| Row | Name | Current | Proposed | Proposed disposition |
| --- | --- | --- | --- | --- |
| PB-4000 | query-execution-results-and-errors | unresolved | in-progress | TQL side: typed, positioned, fail-closed error vocabulary landed at the parse/plan seam (`TqlErrorClass`, 13 classes; rejected vectors across the corpus) and lowered plans execute with typed results through the one seam. Result metadata and total-count behavior are the adapter surface (GAP-Q11); the language contributes the typed error half of this row. |
| PB-4002 | query-source-selection-and-aliases | unresolved | in-progress | Collection source and `as` aliases parse, lower, and execute (R01-R03 vectors; `aliased-source-and-double-equals` execution vector). **Accepted difference**: `from index` targeting is excluded by design — automatic indexing (PD-2026-08-26-09) selects indexes; the language never names one; `from-index-rejected` vector pins the fail-closed rejection. Revisit only with IX-wave evidence. The row's index-name source half therefore rides PB-4001/GAP-Q6. |
| PB-4003 | boolean-expression-precedence-and-negation | unresolved | in-progress | `and`/`or`/`not`/parentheses parse with pinned precedence (TQL-02 golden vectors; R08-R11 vectors execute the `and` and grouping forms). `or` and `not` fail closed at planning today (finding F3) — the seam-extension work named in the conformance record. |
| PB-4004 | equality-null-missing-and-exists | unresolved | in-progress | `= null` distinguishes present null from missing; `exists`/`missing` parse, lower, and execute (R15, R36 vectors; `projection-and-presence` execution vector; `the_shared_parity_corpus_carries_its_pinned_distinctions` pins the three-state field). Analyzed-vs-exact equality is search-capability work (PB-4008). |
| PB-4005 | membership-in-and-array-contains | unresolved | in-progress | `in` and `all in` parse with both quantifiers (R13, R14 vectors); both fail closed at planning (F3). Implicit any-element array matching over array-topped fields is **not realized by the current seam** (finding F2) — recorded as the row's execution-level caveat, not a disposition change. |
| PB-4006 | range-and-between-comparisons | unresolved | in-progress | The six comparison operators execute equivalently (R06 vector, execution proof); `between` parses and fails closed at planning (R12, F3). Date ordering and exact-mode ranges ride the search capability. |
| PB-4007 | prefix-suffix-and-regex-matching | unresolved | in-progress | `startsWith`/`endsWith` translate to `starts_with`/`ends_with` (R37, R38 vectors; parse-and-check pass, planning pending with F3). **Accepted difference**: `regex` is not in the `tql/1.0` registry — bounded, denial-safe regex evaluation must be proven before the registry admits it; planned as an additive minor bump (`regex-rejected` vector pins `UnknownFunction`). |
| PB-4008 | full-text-search-and-search-operators | unresolved | in-progress | `search(field, term [, and\|or])` grammar landed and capability-gated: the exact query text fails closed with `CapabilityUnavailable` until the engine ships search (R40 vector; ADR 0009 D-6). Analyzer compatibility is GAP-Q13; no analyzer evidence is claimed. |
| PB-4009 | phrase-fuzzy-proximity-and-boosting | unresolved | in-progress | `boost(predicate, weight)` grammar landed, gated with search (R41 vector). **Accepted difference**: `fuzzy` and `proximity` are not in the registry; they return with the shipped search capability as registry options (R44, R45 vectors pin `UnknownFunction`). No relevance behavior is claimed. |
| PB-4010 | intersect-query | unresolved | accepted-exclusion (language surface) | Mapping R47: "Fanout is an optimizer concern under automatic indexing, not a language concept." TQL has no `intersect` construct; conjunction expresses the intersection of clauses; the vector pins `UnknownFunction`. Revisit only with IX-wave optimizer evidence that a language-level fanout is required. The parent should confirm this exclusion explicitly: it removes a V1 language surface by charter PD-2026-08-26-09, not by omission. |
| PB-4011 | named-parameters-and-parameter-prefix | unresolved | in-progress | `$name` parameters with typed out-of-band binding parse, lower, and execute (R18-R20 vectors; `literals-and-parameters` execution vector). **Accepted difference recorded**: TQL's strict pairing fails closed on an unreferenced binding (`UnexpectedParameter`) where V1 tolerates unused parameters, and on an unbound reference (`UnboundParameter`) — deliberate, so stored queries, MCP tools, and vectors stay self-describing. Injection safety: parameter values are data, never parsed text. |
| PB-4012 | filter-clause-and-filter-limit | unresolved | accepted-exclusion (language surface) | Mapping R78/R79: "Single filter stage (`where`) keeps semantics simple and indexable under automatic indexing; V1's `filter`/`vector.search` exclusivity disappears with the clause. Revisit if parity evidence demands post-limit filtering." Both `filter` and `filter_limit` are grammar-rejected fail-closed (vectors). Scan accounting and `ScannedResults` observability are the operations seam's concern (GAP-Q11), not the language's. The parent should confirm; the revisit condition is part of the disposition. |
| PB-4013 | sorting-null-ordering-and-score | unresolved | in-progress | All four direction spellings and `nulls first`/`nulls last` execute equivalently with the engine's total document order (R68, R69 vectors with execution proof, including the explicit null placement over a null/string/absent field). `score()` grammar landed, gated with search (R72). **Accepted differences**: no `as string\|long\|double\|alphaNumeric` coercion hints (the total document order with exact cross-type numeric comparison does the work; R70 vector), no `random()` (deterministic replay; R71 vector), no named custom sorters (no-code-execution rule; R74 vector). Seeded-random repeatability is therefore moot at the language level; V1's unseeded behavior is excluded with `random()` itself. |
| PB-4014 | paging-limit-offset-and-caps | unresolved | in-progress | `limit n` / `limit $p` / `offset m` parse, lower, and execute (R75-R77 vectors; `paging-comma-form-translated` execution vector). **Translated**: RQL's `limit offset,size` comma form and `limit size offset n` map to the separate `limit … offset …` clauses (the most common dialect trap; the vector proves the translated page is the same page). Bounds: `limit >= 1`, `offset >= 0`, fail closed (`InvalidBound`); cap behavior beyond the seam maximum is seam-owned. |
| PB-4015 | select-load-and-projection | unresolved | in-progress | Field lists without renaming and whole-document projection execute (R28, R29 vectors); `select distinct` (R31), projection aliases (R29/R30), typed object/array constructors (R32), and `load` (R34, R80) parse and fail closed at planning (F3). **Flagship accepted difference recorded**: RQL's JavaScript projections and `declare function` are replaced by typed expressions over the closed registry — no arbitrary code execution in the language (ADR 0009 D-4; charter PD-2026-08-26-08 D2); the `declare-function-rejected` and `js-callback-rejected` vectors pin the fail-closed rejection, the constructor vector pins the typed replacement's static checks. Stored-field vs source-document projection distinction rides the IX seam (PB-4047). |
| PB-4016 | document-counter-and-timeseries-includes | unresolved | in-progress | Document-path `include` grammar landed and fails closed at planning (R81 vector; F3). **Accepted difference**: `include counters(…)` and `include timeseries(…)` are family-gated (PB-3010/PB-3020) and grammar-rejected today (R82 vector). The documents-include half needs the seam extension before any execution claim. |
| PB-4017 | distinct-results-and-counting | unresolved | in-progress | `select distinct` parses with the deduplicate-after-projection semantics specified; fails closed at planning (R31 vector; F3). No counting behavior is claimed until the seam lands. |
| PB-4018 | grouping-and-aggregation | unresolved | in-progress | `group by` keys with aliases and the five aggregates parse under the group-derivation static rule (R60, R66 vectors); all fail closed at planning (F3). **Accepted difference**: `group by … with <method>` custom grouping is rejected (R67 vector) — code-adjacent, revisited only with typed-registry evidence of need. |
| PB-4021 | more-like-this | unresolved | unresolved (disposition recorded, no status move) | Not in the `tql/1.0` registry (R46 vector pins `UnknownFunction`); the mapping's reason is "Revisit with the shipped search capability". No V2 work exists to point at, so the row stays unresolved; this record fixes the language-side position so the revisit lands as a registry addition, not a grammar change. |
| PB-4022 | spatial-predicates | unresolved | in-progress | The four `spatial.*` predicates and the three shape constructors are in the grammar and capability-gated (R63, R64 vectors; ADR 0009 D-6): the exact text fails closed with `CapabilityUnavailable` until the engine ships spatial. No geometry behavior is claimed. |
| PB-4023 | spatial-distance-ordering | unresolved | in-progress | `spatial.distance` in `order by` grammar landed, gated (R65/R73 vector). Same gate rule; no distance semantics claimed. |
| PB-4024 | vector-search | unresolved | in-progress | `vector.search(field, vector [, threshold])` grammar landed, gated (R61 vector). **Translated**: RQL's `embedding.text`-style in-query embedding generation is excluded — vectors are data; the query binds them as `$parameters` or `vector.of(…)` (R62 vector pins the translation). This is CP-01's required competitive row; the gate means the language and the capability land together, and no vector behavior is claimed before the engine ships it. |
| PB-4032 | linq-query-translation | unresolved | unresolved (disposition recorded, no status move) | LINQ is an SDK-side translation layer; no V2 SDK surface exists yet. The RQL parity corpus is the language-side analog and its vectors are the engine-side evidence the adapter wave will consume; the row stays with the adapters. |
| PB-4037 | patch-by-query | unresolved | unresolved (disposition recorded, no status move) | **Accepted difference recorded**: RQL's `update { … }` clause is rejected fail-closed (R83 vector) — TQL is read-only and code-free; patch-by-query reaches V2 as a typed, idempotent, confirmable operation, never a language clause. The operation itself is the operations/adapter seam's row. |
| PB-4038 | delete-by-query | unresolved | unresolved (disposition recorded, no status move) | Same as PB-4037: the query-selection half of delete-by-query is served by TQL's read surface; the deletion operation is a typed operation owned elsewhere. No status move proposed. |
| PB-4041 | query-validation-and-error-contract | unresolved | in-progress | Typed, positioned, fail-closed error vocabulary landed with 13 classes, every error carrying the grammar version and the offending construct where one applies (`TqlError::position`/`construct`; the 29 rejection vectors plus TQL-02's error corpus). Secret-safety holds trivially: error payloads carry construct names and positions, never values beyond the offending text. Client-visible error *serialization* is the adapter surface. |

## Rows this ticket deliberately does not propose

PB-4001, PB-4019, PB-4020, PB-4025–PB-4031, PB-4033–PB-4036, PB-4039,
PB-4040, and PB-4042–PB-4062 are outside the RQL language mapping: they
are owned by the IX wave (index definition, selection, auto-index,
lifecycle, analyzers, engine selection), the SH wave (sharded queries),
the streaming seam, the operations/adapters seam (result metadata,
statistics, routes), and the benchmark adapter. TQL-04's mapping feeds
two of them with accepted-difference inputs only — the `lucene`
passthrough rejection (PB-4052's cross-engine compatibility) and the
`cmpxchg`/`counters`/`revisions`/`timeseries`/`last` rejections
(PB-4044's JS-index stance and PB-4058's family sources) — and those rows'
dispositions belong to their owners.

## What the parent should review and accept

1. The **divergence list** — the 29 accepted-difference rows of the
   mapping, now with fail-closed proof per surface (`docs/tql/rql-mapping.md`
   Section 12 and `docs/tql/rql-conformance.md` Section 2 and 4). The two
   flagship divergences (no JavaScript projections; no wall-clock,
   randomness, or raw passthrough) follow accepted ADR 0009 and need no
   new decision; the two proposed exclusions below do.
2. The two **accepted-exclusion proposals** (PB-4010 `intersect`,
   PB-4012 `filter`/`filter_limit`): both remove a V1 language surface by
   charter rule rather than by omission, and both carry revisit
   conditions. They should not land without explicit parent acceptance.
3. The **strict parameter pairing** divergence (PB-4011): V1 tolerates
   unused parameters; TQL fails closed on them. Recorded as deliberate;
   confirm or overrule.
4. The **findings** F1–F4 (`docs/tql/rql-conformance.md` Section 3):
   F1 and F2 are gaps to route (parser owner, seam owner); F3 is the
   planning-blocked construct list that gates every "executed
   equivalently" claim on this page; F4 is the gate behavior both
   directions.
