# Auto-indexing and query-optimizer dispositions (IX-01 design input)

Ticket: TerraScale/TerraBaseV2#37 (IX-01). Input:
`docs/indexing/v1-auto-index-extraction.md` (behavior inventory AX-1..
AX-26, pinned V1 commit `fa99b9257776dbe625e6a2388c74c9ce916637ee`).
Architecture decision record: `docs/adr/0010-auto-indexing-and-query-optimizer.md`
(status: proposed). Glossary terms (Auto-Index, Query Optimizer, Index
Selection Record) are recorded in `CONTEXT.md`.

Disposition vocabulary (this document):

- **adopt** — V2 reproduces the behavior as extracted.
- **adapt** — V2 reproduces the behavior with a named difference.
- **accepted-difference** — V2 intentionally differs; the reason is
  recorded here.

This document does not edit parity overlays. It records the evidence
and proposals from which WS-PARITY (and the IX wave's later packets)
derive row dispositions. Nothing here marks a row `implemented`; the IX
wave owns implementation and evidence, and rows stay `unresolved` until
their proof obligations land (the PS-02 query sweep reached the same
verdict for PB-4045/PB-4046: keep unresolved, IX wave #37-#40 owns them).

## Per-behavior dispositions

### Routing

| ID | Behavior | Disposition | V2 difference / reason |
| --- | --- | --- | --- |
| AX-1 | Three-way routing (explicit index / optimizer / collection scan) | adapt | Same triage on the W3-2 query seam, but the entry language is TQL (PD-2026-08-26-08): an explicit `from index` target, a dynamic collection query, and a pure collection scan remain distinct routes. Collection scans stay index-free exactly as V1 (`CollectionQueryRunner` parity). |
| AX-2 | Plan cache pins the chosen auto index | adapt | V2 keeps a bounded plan cache keyed by the parsed, versioned TQL AST. Difference: a cached plan must never outlive catalog truth — cache entries invalidate on catalog generation changes, and a cache hit still counts as a selection (it can appear in an Index Selection Record as `cache-hit` with the origin recorded). V1 pinned the name until the index disappeared. |
| AX-3 | Optimizer kill-switch (`DisableQueryOptimizerGeneratedIndexes`) | adapt | V2 keeps a fail-closed engine configuration switch and the per-query opt-out (`DisableAutoIndexCreation` parity) as typed errors ("dynamic queries are not supported; the query optimizer needs an index"). Difference: it is engine configuration, not per-database HTTP settings; the default remains enabled per charter PD-2026-08-26-09 D1. |
| AX-4 | Create an auto index on an unmatched dynamic query (execute, stream, delete-by-query, patch-by-query, suggestions alike); refuse for index-entries queries | adopt | Core V1 parity and charter D1. Creation is engine-internal, never a client-side operation. The per-entry-point guard split (creation for result queries, refusal for index-entries reads) is reproduced as typed behavior. |

### Creation and definitions

| ID | Behavior | Disposition | V2 difference / reason |
| --- | --- | --- | --- |
| AX-5 | Definition derived from the query shape (where fields, order-by fields, per-field capabilities; group-by/aggregation for map-reduce; canonical ordinal field ordering) | adapt | Same derivation rules against the typed TQL AST. Differences: (1) per-field capabilities are a typed enum on the index field, not string-composed flag names; (2) the capability set initially covers what the W3-2 query subset supports (equality, ordering, aggregation); search/exact/highlight/suggestions capabilities follow PB-4047/PB-4048 rows rather than blocking AX behavior. |
| AX-6 | Vector-search creation guard (embeddings task must exist/enabled) | adapt (deferred) | The guard pattern is adopted: creation fails closed when a query needs a capability whose backing configuration is absent. Deferred with the vector-search rows (PB-4051, CP-01 vector row); no V2 vector guard exists before that row lands. |
| AX-7 | Auto indexes are durable cluster-replicated state (Raft `PutAutoIndexCommand`, creation Raft index on results) | adapt | V2 has no Raft and no cluster database record. Auto-index definitions are durable engine state through the engine-owned catalog (charter follow-up CA-3, landed as integrator action W4-CA-3: `Engine` owns an `Arc<IndexCatalog>` whose publication sink is registered at open — `src/index/mod.rs::IndexCatalog`, `docs/design/w4-collision-actions.md`), and definition replication follows the replication wave's contract. Difference: no client-visible creation Raft index; the V2 equivalent identity is the catalog's namespace epoch/generation vocabulary, which the seam already reports per namespace on results (the generation actually used, per the W3-2 staleness record). |
| AX-8 | Deterministic `Auto/{Collection}/By{Fields}` names, canonical ordering, `ReducedBy` suffix, 256-char hash tail | adapt | PB-4045's compatibility note already allows "a new index identity format". V2 adopts: the `Auto/` prefix, deterministic name-as-identity, ordinal field ordering, and the hash-truncation rule; the exact inner format is V2-owned and recorded when IX implements it. |
| AX-9 | Same name + drifted definition = in-place update; wider fields take the supersede path; planner retries on concurrent replacement; execution retries (bounded) when an index is replaced mid-query | adapt | The split is reproduced on the catalog's model: a definition drift that does not change identity updates in place under the same namespace; a strictly wider field set has a different deterministic name and flows through merge/supersede (AX-16/AX-17); creation races retry matching; replacement during execution never interrupts the query (bounded retry), feeding IX-03's "safe replacement without query interruption" contract. |
| AX-10 | `Auto/` namespace reserved; `IndexType.AutoMap`/`AutoMapReduce` distinct types | adopt | Explicit index names may not start with `Auto/`; auto and explicit stay distinct kinds in the catalog. |

### Selection

| ID | Behavior | Disposition | V2 difference / reason |
| --- | --- | --- | --- |
| AX-11 | Dynamic queries consider only auto indexes | adapt | Deliberate V2 difference, required by charter PD-2026-08-26-09 D2 ("Explicitly created indexes remain supported and take precedence where equivalent"): the V2 optimizer's candidate set includes explicit indexes and prefers an equivalent explicit index over creating or selecting an auto-index. Rationale: V1's exclusivity forces duplicate auto indexes next to equivalent static ones; V2 must not create a second index when an explicit one already serves, and competitive baselines (InstantDB, Convex — CP-01) are explicit-index products, so V2 wins by making explicit indexes first-class optimizer citizens. |
| AX-12 | Per-index verdict lattice: Failure / Partial / Complete (+idle variant) | adapt | Same lattice over typed capabilities (coverage + capability sufficiency + aggregation/group-by agreement + not-disabled/errored). The idle variant stays a first-class verdict so wake-up is observable. |
| AX-13 | Ranking: match type, then most up-to-date, then non-idle, then widest | adapt | Same preference order, with one change: V1's recency preference is retained only as the staleness tie-break it is; V2 selection is capability- and coverage-based, and every applied or skipped rule is recorded (see AX-14). "Widest wins" is kept — it is what bounds auto-index proliferation. |
| AX-14 | Rejection reasons recorded as explanation strings | adapt, strengthened | V1 records reasons only on demand (`?debug=explain`). V2 records an Index Selection Record for every selection — chosen index, candidate verdicts, applied tie-breaks, cache hits, and creation events — per charter D3 ("no query served by a worse-than-available index without a recorded reason"). See ADR 0010 D-1. |
| AX-15 | Explain surface + optimizer timing scope | adapt | Adopted as a typed, MCP-exposable operation (PD-2026-08-26-07) returning the would-be selection without executing, plus the `optimizer` timing scope in query timings. Difference: V2 explain is a typed operation on the seam, not a debug query-string flag. |

### Merge, supersede, retirement

| ID | Behavior | Disposition | V2 difference / reason |
| --- | --- | --- | --- |
| AX-16 | Partial match → extend mapping from the existing index's fields, create the covering index | adopt | The union rule (never invent group-by fields; union per-field capabilities) is reproduced against typed definitions. |
| AX-17 | Superseded indexes deleted only after the replacement covers at least as much, plus a drain wait; idle timer as backstop | adapt | Correctness rule adopted verbatim (delete only after coverage parity), expressed with the catalog's covered-sequence/monotonic-generation coverage instead of V1 etags. The 15 s drain default is the initial value, tunable and subject to measurement; fire-and-forget cleanup with the background pass as backstop is adopted. |
| AX-18 | Background surpass pass (delete surpassed, extend-and-replace partial, one extension per pass, etag-guarded) | adopt | Runs as engine background maintenance against the catalog; the guard becomes the catalog's definition version. |
| AX-19 | Static index merging is suggestion-only | adapt (deferred) | Suggestion-only in V1 and never automatic; the V2 equivalent is an Operations Studio tool on explicit indexes, deferred to the studio/DB wave rows. No auto behavior depends on it. |
| AX-20 | Every query marks the index queried (last-queried tracking) | adopt | Recorded per namespace in the catalog. |
| AX-21 | Idle after 30 min unqueried; delete after 72 h idle; back to Normal when queried; act only with complete reports; reasons quote measurements | adapt | V2 has no cluster observer: the idle/delete state machine runs as engine background maintenance over the catalog's last-queried record. The fail-closed reporting rule (no transition while any reporting unit is missing) carries over as engine-observable state completeness. Defaults (30 min / 72 h) are initial values carried from V1; every transition records the measured value and threshold, as V1's observer commands do. |
| AX-22 | Querying an idle auto index wakes it (Idle → Normal, marked queried) | adopt | Wake-up is synchronous with query preparation, log-and-continue on failure, exactly as V1. |
| AX-23 | Fresh auto index triggers a bounded non-stale wait (15 s default; 60 s streaming) | adapt | The bounded-default-wait behavior is adopted; the specific durations are V2 configuration recorded at implementation and validated in the benchmark lane, not silently copied as performance claims. |
| AX-24 | Stale results returned by default; non-stale only when requested; staleness observable in results | adapt | The observable contract maps onto the W3-2 seam's existing staleness vocabulary (`StalenessMode::Strict`/`AllowStale`, `GenerationView` observed/available generations, generation-used reporting); the optimizer adds no new staleness semantics. Which mode a dynamic query defaults to is an IX-02 decision recorded with its tests, not a silent default. |
| AX-25 | Sharded dynamic queries may select different compatible auto indexes per shard without becoming stale | adapt (deferred to SH) | The principle — staleness is evaluated against the index actually selected — is adopted into the selection-record contract. Full sharded selection parity lands with the SH wave (PB-4036/PB-4060 rows). |
| AX-26 | Explicit targeting bypasses the optimizer; `Auto/` namespace split; auto→static conversion; no side-by-side reset for auto indexes | adapt | Structural precedence adopted: a named target wins outright, dynamic queries never rename or mutate an explicit index, and auto indexes refuse side-by-side-style resets (epoch semantics only). Differences: (1) with AX-11, dynamic queries *do* prefer equivalent explicit indexes, so precedence is also ranking-based, not only structural; (2) auto→explicit conversion is a typed catalog operation (proposal for IX-02+), reproducing `AutoToStaticIndexConverter` semantics on V2 identity formats. |

## Parity-row proposals (for WS-PARITY; no row is dispositioned here)

### PB-4045 — auto-map-indexes (core, critical risk)

Proposal: **adapt** (evidence: AX-4, AX-5, AX-8, AX-9, AX-11, AX-16,
AX-17). The row's three semantics map as follows:

1. "Dynamic collection queries can create or select an auto-map index
   with field map, storage, indexing, spatial, vector, and suggestion
   options derived from the query" — adopted for the W3-2 capability
   subset; spatial/vector/suggestion option derivation rides their own
   rows (PB-4048/4050/4051) and composes with AX-5.
2. "Equivalent dynamic queries reuse compatible auto-index definitions;
   incompatible definitions create distinct identities" — adopted;
   deterministic naming (AX-8) plus the drift-update/wider-supersede
   split (AX-9) is exactly this contract.
3. "Auto-index creation and transition states are visible through query
   staleness and index metadata" — adopted via the staleness seam
   (AX-23/AX-24) and the catalog's namespace/epoch/generation metadata
   (AX-7 adaptation).

V2 differences to record on the row when WS-PARITY dispositions it:
new identity format (row compatibility note already permits this),
catalog-backed durability instead of a Raft database record, and
explicit-index candidate preference (AX-11).

### PB-4046 — auto-map-reduce-indexes (core, critical risk)

Proposal: **adapt** (evidence: AX-5 group-by path, AX-12 aggregation
verdicts, AX-16 union rule). Group-by field agreement, aggregation
agreement, array-grouping behavior, and the "invalid aggregation
combinations return typed errors rather than a partial auto-index"
semantic (V1's `InvalidQueryException` in
`DynamicQueryMapping.CreateGroupByFields`) are adopted. Difference: the
V2 aggregation vocabulary is the typed W3-2 subset; JS `count()`/`sum()`
composition follows the TQL rows.

### PB-4054 — index-state-staleness-and-rebuild (core, critical risk)

Proposal contribution only: AX-21/AX-22 (idle/deleted state machine,
wake-on-query) and AX-23/AX-24 (staleness wait semantics) are this row's
optimizer-owned facts; rebuild/reset progress remains the row owner's.
The idle state is proposed as an adopt-adapt (engine maintenance instead
of cluster observer).

### PB-4056 — index-metadata-terms-and-performance-observability (admin)

Proposal contribution: the Index Selection Record and the explain
operation (AX-14/AX-15 adaptations) extend this row's V2 evidence set;
V2 makes selection observability first-class rather than debug-only.

### PB-4052 — corax-lucene-search-engine-selection (core)

Proposal contribution: AX-5's per-query engine-type selection is moot in
V2 unless two search backends ever exist; the current V2 direction is a
single native engine, which would make the engine-type dimension an
accepted-difference on this row. This is flagged for the row owner; IX-01
does not decide it.

### PB-4057 — index-lock-priority-deployment-and-side-by-side (admin)

Proposal contribution: auto indexes refuse side-by-side resets (AX-26)
and lock/priority semantics on auto indexes are V1 operator controls on
automatically created objects; V2 should either adopt them as engine
maintenance controls or record accepted-difference. Flagged for the row
owner.

## Cross-references

- ADR 0010 (this packet) records the architecture decisions: selection
  with recorded reasoning, automatic creation, lifecycle hooks into the
  engine-owned catalog, and the rejected alternatives.
- The glossary terms **Auto-Index**, **Query Optimizer**, and
  **Index Selection Record** are recorded in `CONTEXT.md` (section
  "Indexing and query planning"); this packet was the wave's sole
  `CONTEXT.md` writer.
- Competitive angle: V1-parity auto-indexing exceeds InstantDB and
  Convex, which require explicit index declarations (PD-2026-08-26-09
  D4); the CP-01 matrix row already records this.
