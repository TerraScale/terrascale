# GAP-Q06 WS-PARITY disposition proposals: static index definition rows

Status: proposals authored by ticket GAP-Q06 (TerraBase/TerraBaseV2#63,
parent adjudication PD-2026-08-26-14 D3, which folded GAP-Q8's
DISTINCT/static-`GROUP BY` half and GAP-Q16 into this ticket).
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

- **Decision record** — `docs/indexing/static-index-definitions.md`
  (decisions D1..D10, rejected alternatives R1..R6, per-rule V1 anchors).
- **Contract** — `src/index/definitions.rs`, public seam
  `terrabase_v2::index::definitions` (typed, versioned `idx/1.0`,
  fail-closed).
- **Tests** — `tests/index/static_index_definitions.rs` (14 public-path
  tests) and `src/index/definitions_tests.rs` (9 in-crate tests,
  including the catalog reserved-prefix agreement pin).
- **Row source** — `parity/contracts/query.json` (63 items,
  PB-4000..PB-4062) at V1 source identity
  `fa99b9257776dbe625e6a2388c74c9ce916637ee`; row scopes re-read from
  `parity/inventory.json`.
- **Adjacent proposals** — `docs/parity/tql04-ws-parity-proposals.md`
  owns the language-surface halves of PB-4017/PB-4018 and records that
  `from index` targeting rides PB-4001/GAP-Q6 (this file);
  `docs/indexing/dispositions.md` owns the auto-index (PB-4045/4046)
  proposals.

## Proposals

| Row | Name | Current | Proposed | Proposed disposition |
| --- | --- | --- | --- | --- |
| PB-4001 | static-index-definition-and-planning | unresolved | in-progress | The definition half of the row: a static index has a stable validated name (V1 name rules incl. reserved-namespace refusal), typed map stages, field options, and an optional reduce stage, versioned `idx/1.0` and fail-closed; identity, comparison (`compare`), reindex scope, and deterministic `identity_key` are landed as contract facts. **Accepted differences**: definitions are typed values, not compiled code (PD-2026-08-26-08 D2); the name byte bound is the seam's 255, not V1's path-derived limit; V1 folds output changes into its reduce difference, V2 reports output separately. Query planning/selection against static definitions is IX-wave follow-on (#38 explicit-vs-auto precedence); no planning behavior is claimed yet. |
| PB-4017 | distinct-results-and-counting | unresolved | in-progress (index-side contribution) | The static half: a map-reduce definition produces one output row per distinct grouping-key value — the reduce key set is the distinct identity of index output (decision D5). Query-side `select distinct` semantics, distinct counting, and paging agreement remain with the query seam (TQL-04's proposals record the language half); no counting behavior is claimed. |
| PB-4018 | grouping-and-aggregation | unresolved | in-progress (index-side contribution) | Static `GROUP BY` is the reduce stage: grouping keys over the map shape with V1's array grouping modes (`NotApplicable`/`ByContent`/`ByIndividualValues`) and the typed `count`/`sum` aggregation vocabulary (V1's `AggregationOperation`). **Accepted difference**: V1's arbitrary-code aggregations are not available — the typed set extends additively (min/max/average with their evaluator rows). The shape rule adapts V1's auto map-reduce derivation: map shape = grouping keys + aggregation inputs, exactly. Dynamic `GROUP BY` (auto map-reduce) stays with the IX wave per PB-4046. |
| PB-4042 | static-map-reduce-indexes | unresolved | in-progress | The map/reduce contract: typed reduce stage, stable grouping keys, aggregate bindings, coverage rule, and typed index creation errors for every invalid or incomplete definition (fail-closed at construction; `tests/index` pins each error). **Accepted difference**: JS reduce functions are replaced by the typed aggregation vocabulary (Q16 fold). Execution — building reduce segments and serving grouped queries from them — is follow-on work; no execution claim is made. |
| PB-4043 | multi-map-indexes | unresolved | in-progress | Multiple source maps over one common output shape with source-type coherence (documents-only in `idx/1.0`; family sources ride GAP-Q19), duplicate-map detection at construction, and the all-documents source with exclusivity. **Accepted differences**: the map-for-all descendant expansion (V1's reflection over hierarchy) is replaced by the explicit all-documents source; mixing all-documents with collection sources is refused to prevent silent double counting. Map order is canonical, not identity. |
| PB-4044 | javascript-map-and-map-reduce-indexes | unresolved | accepted-exclusion | **Q16 disposition (this is the row's explicit legacy adjudication)**: V1 JavaScript map and map-reduce index definitions are replaced by the typed index definition language — no scripting runtime, no JS index compilation or deployment surface, ever, per PD-2026-08-26-08 D2's no-arbitrary-code rule (this is a charter-mandated replacement, not an omission). Migration path, coordinating with #44 (BK-03 logical migration): the migration reads a V1 index definition and translates it into the typed language **when its semantics fall inside the typed subset** (field-path maps, count/sum reduce, output collections, field options); definitions outside the subset (arbitrary computations, format-suffixed patterns, family sources) are reported fail-closed in the migration report as untranslatable-with-reason, never silently dropped or approximated. The translation table and its failure modes are BK-03/#44 follow-on evidence owed when that seam consumes this contract. No wire or in-engine JS compatibility is retained. |
| PB-4047 | index-field-storage-indexing-and-term-vectors | unresolved | in-progress | The indexing/storage/analysis subset is landed: `FieldIndexing` (`Default`/`Exact`/`No`/`Search`), `FieldStorage` (`NotStored`/`Stored`), analyzer names for analyzed fields with the typed strengthening that an analyzer attaches only to `Search` fields; options bind to the definition's output shape and unknown fields are refused. Term-vector modes are deferred to the highlighting/search rows (PB-4048/GAP-Q13) as an additive minor bump; spatial/vector/suggestion options ride their rows; analyzer existence and the analyzer foundation are GAP-Q13's decision. Stored-field projection behavior (projecting from stored index fields vs source documents) is the IX seam's follow-on. |
| PB-4049 | dynamic-and-compound-index-fields | unresolved | in-progress | Dynamic fields become typed rules (name path, value path, per-rule options) validated at construction; runtime-created names are never statically addressable — reduce keys, field options, and compound members may not name them, and invalid names/paths return typed errors. Compound fields are ordered member groups over the output shape, hint-only by contract: they never change visible results, and a compound-only change does not require a reindex (V1's reindex mask parity). Invalid member lists return typed errors. |
| PB-4059 | map-reduce-output-and-reference-collections | unresolved | in-progress | Output collections, reference patterns, and references collections are typed declarations with V1's creation-time rules enforced at construction: requires reduce; not a mapped collection (loop); forbidden for all-documents indexes; references collection differs from the output collection; one output collection per index (cross-index conflict helper); patterns carry at least one unique placeholder naming reduce output fields. **Accepted differences**: V1's format-suffixed placeholders are rejected fail-closed in `idx/1.0` (returning with the date-format rows); placeholder-to-output binding is a construction error, not a runtime failure. Writing reduce outputs as documents — authorization, tenant isolation, update behavior — is the follow-on execution work; this contract adds the declarations and their validation only. |

## Notes for the parent

- PB-4044 is the only accepted-exclusion proposed here, and it is the
  Q16 decision the ticket was chartered to record; the migration-path
  obligation lands on #44 (BK-03) with the translation table named above.
- PB-4045/PB-4046 (auto indexes) are untouched; `docs/indexing/dispositions.md`
  owns their proposals and the catalog/optimizer code already landed
  (IX-02/IX-03). The static contract defined here feeds the
  explicit-vs-auto precedence follow-on on #38.
- GAP-Q19 (family index source types) and GAP-Q13 (analyzer foundation)
  are named coordination points, not scope of this ticket.
