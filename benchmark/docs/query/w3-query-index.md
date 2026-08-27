# W3-2 query and index record: the typed compile/execute seam

Status: implementation record for the W3-2 query/index packet, 2026-08-24.
Worktree branch `fleet/w3-query`, based on `c620ac6` (W3-1 storage and engine
accepted, W3-F families at `6c40c98`, plus the integrator wiring of
`Engine::queries` and the crate-private `Engine::snapshot_documents` snapshot
view). This record describes focused-test evidence only. It is not a parity
qualification: all 63 query parity rows (PB-4000 through PB-4062) and the
family-mapped rows PB-3010, PB-3020, and PB-3040 remain `unresolved` with
empty Proof ID lists, and this packet adds no Proof ID to any row.

Repair history pinned on this record: loop a1 landed at `283b049`
(implementation) and `3623b34` (this record); loop a2 — the gate quality
findings — landed at `794353b` on top of `3623b34` and covers the NaN-safe
total document order, the alias-proof index-namespace catalog key, the
bounded checkpoint after the sort and per materialized rows, softened
generation-accessor wording, the conservative snapshot-observation
assumption, the grammar identifier note, a corrected `compile_fail` example,
temp-directory cleanup, and the test-only gating of the `with_index_catalog`
seam; loop a3 — the Q1b residual — landed at `1a561c9` on top of `a2ee067`
with the record pin commit `0a2c78d` closing the loop
and replaces the
mixed Integer↔Number comparison with an exact cross-type comparison (sign
gates at 2^63, `i128` whole parts, symmetric fractional tie-break) so the
numeric order is genuinely total across the two encodings above 2^53, and
extends the totality property with the substitution axiom, the 2^53/i64
extremes value set, and nested array/object values.

## Scope and ownership

Owned paths only: `src/query/**`, `src/index/**`, `tests/query/**`, and this
document. No file outside that set was edited. `src/engine/**`,
`src/storage/**`, `src/families/**`, `src/adapters/**`, `src/lib.rs`,
`src/types.rs`, `src/operations.rs`, `parity/**`, and the pre-existing test
targets are untouched; `tests/public_exports.rs` still compiles unchanged
because it names types only. `tests/index/**` was not created because the
index module owns no public type; its evidence lives in `src/index/tests.rs`.

## Design as implemented

### One compile seam

`QueryStore::compile(QueryInput) -> Result<QueryPlan, QueryError>` is the only
plan producer. The typed form is re-validated while it is normalized into the
crate-private `NormalizedQuery`; the V1-compatible form is parsed by the one
grammar module and normalizes into the same model. `QueryPlan` is immutable,
owns no index, no lock, and no storage handle, and can be reused with any
compatible snapshot. `QueryStore::execute(&plan, &snapshot)` consumes only the
caller's snapshot. `Engine::queries()` is the only acquisition path and the
borrowed store cannot outlive its Engine (proved statically in
`query_store_borrow_prevents_engine_shutdown_while_live` and by the
`compile_fail` examples on `QueryStore`).

The typed clause values (`QuerySource`, `QueryPredicate` with the closed
`QueryCondition` set, `QueryOrdering`/`OrderingKey`/`OrderDirection`,
`QueryProjection`, `QueryPaging`, `StalenessPolicy`/`StalenessMode` with
`IndexNamespace`) keep private fields, `try_new` or named-builder validation,
and read-only accessors. `QuerySpec::try_new` keeps the frozen nine-argument
shape and rejects an expired deadline or an already cancelled signal.

### Supported V1-compatible grammar subset

The grammar module defines this subset, and only this subset, in this fixed
clause order with each clause optional:

```text
query      := "from" collection
              ["where" condition *("and" condition)]
              ["order by" key *("," key)]
              ["limit" uint]
              ["offset" uint]
              ["select" field *("," field)]
condition  := field op literal | "exists" "(" field ")" | "missing" "(" field ")"
op         := "=" | "!=" | "<" | "<=" | ">" | ">="
key        := field ["asc" | "desc"]
literal    := "'" text "'" | integer | "true" | "false" | "null"
```

Keywords are lowercase exactly as written; identifiers draw their characters
from the shared identifier alphabet (`[A-Za-z0-9_-]`) and follow the shared
identifier discipline — no control characters and no leading or trailing
whitespace. There is deliberately **no first-character rule**: field names in
V1 are caller-chosen strings, numeric-leading names are accepted there, and
rejecting them here would narrow the grammar without a parity reason. The
shared rules already reject the dangerous shapes, and the tokenizer
independently refuses a leading `-` (it parses as a negative literal, which a
name cannot complete). Single-quoted literals have no escape sequence.
Everything else is
`QueryInputError::UnsupportedGrammar`: SQL shapes such as `SELECT * FROM …`,
uppercase keywords, parentheses, `or`, double-quoted strings, `*`, misplaced
or repeated clauses, trailing tokens, and boundary violations such as
`limit 0`. Empty or whitespace-only text is `EmptyText`; a structurally valid
text whose collection or field name fails identifier validation is
`InvalidIdentifier`. This is a V1-inspired subset definition, not a claim
about V1's RQL grammar (PB-4000 stays unresolved).

The V1 form carries no tenant, deadline, or cancellation clause, so its model
is unbound: the executing snapshot supplies the tenant binding, and the plan
enforces no deadline or cancellation. Its default staleness policy is strict
with no required index namespace, which selects the index-less base path.

### Execution path

`execute` validates, in order, before any data access: the plan's tenant
claim against the snapshot tenant (`QueryError::SnapshotTenantMismatch`), and
every required index namespace's tenant against the snapshot tenant
(`QueryError::TenantMismatch` — a namespace identifies the tenant and the
logical definition, so a namespace of another tenant is a wrong tenant scope,
also rejected at compile). It then evaluates the staleness policy, checks the
execution bounds, and reads the tenant's documents through
`Engine::snapshot_documents`: the snapshot supplies its own Engine identity
and tenant, the Engine validates both against its real identity inside that
view, and a snapshot from another Engine surfaces as
`QueryError::SnapshotEngineMismatch` without any data access.

Execution is an index-less full scan of that materialized document list: it
filters by source collection and predicate conjunction, sorts with a stable
sort under the plan's ordering, projects, and pages. Because the list is
materialized, no Engine or storage lock is held while scanning, ordering,
projecting, or paging; the bounded checkpoints re-read the deadline and the
cancellation signal every 64 scanned documents, once at the scan/sort
boundary, and then every 64 materialized rows — the first of those fires
before any row is produced, so a bound that expires during the sort still
stops the query.

The W3-2 document order used by `order by` is a defined total order: missing
first, then null, booleans, finite numbers, non-finite numbers (`NaN` and the
infinities, ordered by the float total order so `NaN` consistently equals
`NaN`), strings, arrays, objects (arrays element-wise, objects by sorted
field names then values). The unified numeric rank compares `Integer` and
finite `Number` by **exact mathematical value across the two encodings**: a
number at or beyond 2^63 in magnitude is decided by sign against the whole
`i64` range, and every other pair compares exact whole parts through `i128`
with a symmetric fractional tie-break. No integer is widened into `f64`,
which rounds above 2^53 and would collapse one-apart integers (snowflake
identifiers, nanosecond timestamps) onto one float.
The order is a true total order — antisymmetric, transitive, and
substitution-consistent (values that compare equal are interchangeable)
over the whole domain including `NaN` and every mixed Integer/Number pair —
so the stable sort can never observe an inconsistent comparator.
Predicate comparisons are defined inside the
numeric, text, and boolean domains only; any other pair, including a cross-type
pair, a `NaN` operand, or an absent field, makes the comparison
conditions false rather than error. `Equals` matches a present value
structurally, so a present null and an
absent field are distinct, and `Exists` matches a present field including a
null value. These are V2 tracer semantics, not V1 parity claims.

### Staleness and index generations

Each required namespace is gated on two independent facts: the generation the
snapshot observed for that namespace (`observed`), and the generation the
index catalog can currently supply (`used`). The production view is the
private index catalog; the crate-private `GenerationView` seam lets the
focused tests supply the two facts independently, which one monotonic catalog
can never show at the same time.

A strict policy requires, per namespace: an observed generation, a supplied
generation, the same epoch, and a supplied generation at least the observed
one. Otherwise the query fails with `QueryError::IndexNotReady` and never
silently returns an older or not-comparable index for a strict namespace. An
allow-stale policy accepts any supplied generation and records `observed`,
`used`, and a `stale` flag per namespace in the `StalenessReport`; the result
also names the primary `IndexGenerationRef` (the used generation of the first
required namespace). An empty namespace list under strict selects no index
and executes index-less.

Generations of two namespaces, or of two epochs of one namespace, are never
ordered or compared as one sequence: the epoch is part of every comparison
key, and a different epoch is treated as not comparable even when its
sequence number is higher.

### Index publication

Publication follows storage publication. `IndexDelta::from_event` derives one
immutable delta (covered storage sequence plus published root identity) from
the crate-private `CommitPublicationEvent`, which the Engine emits after
storage locks release — proved engine-side by the existing W3-1 test
`commit_publication_event_is_emitted_after_storage_locks_release`. The
catalog sink applies deltas under the index lock only, ignoring replays and
reorderings. `IndexBuilder::rebuild_from` reads documents through the
Engine's validated snapshot view with no lock held, builds the segment
privately for the namespace's registered field, and then publishes under the
index lock with a compare-and-publish rule: a build whose covered sequence
does not strictly advance the published coverage is rejected as stale, so a
stale, replayed, or reordered build can never lower or overwrite the visible
generation. The generation counter is monotonic and non-wrapping per
namespace and epoch (`checked_add`, typed exhaustion error). A namespace is
recreated only explicitly and only under a strictly newer epoch
(`recreate_namespace`), which restarts the generation sequence at one inside
the new epoch. Index publication never asks Storage to publish a data root:
the storage sequence is asserted unchanged across build-and-publish cycles in
`index_publication_does_not_hold_the_storage_lock`, and the lock-order case
is proved one-way — a test holding the index lock still reads storage.

Catalog entries key on the namespace's crate-private catalog key, which joins
the tenant and the logical definition name with the unit separator
`\u{1f}`. Both parts reject control characters — the tenant through the
shared identifier rules, the name through the field-name rules — so the
separator cannot occur inside either part and the pair is injective: a plain
`'/'` join would collapse the namespace `("a", "b/c")` and the namespace
`("a/b", "c")` into one entry and let one namespace's generation open the
other's staleness gate, which
`tenant_and_name_separator_cannot_alias_a_namespace` pins as impossible.

## Invariants held

- One compile seam for both input forms; no raw route, arbitrary SQL, generic
  map, or byte payload enters through any public constructor.
- The plan is immutable and owns no lock; execution never writes a snapshot
  back (`plan_reuse_does_not_mutate_the_snapshot`).
- Tenant, snapshot-identity, and namespace-tenant validation happens before
  any data access; the strict staleness gate also rejects before the snapshot
  view is touched (asserted with a foreign-Engine snapshot).
- Strict never silently serves a missing, older, or not-comparable index
  generation; allow-stale always reports what it used.
- Index publication is event-driven from the immutable commit publication
  event, monotonic per namespace and epoch, replay-safe, and never holds the
  storage lock or publishes a storage root.
- Cancellation and deadline are checked at bounded checkpoints with no lock
  held — before the scan, every 64 scanned documents, once at the scan/sort
  boundary, and every 64 materialized rows — and a cancelled or expired query
  leaves the storage lock free.
- The document order used by `order by` is a true total order over the whole
  value domain including `NaN` and every mixed Integer/Number pair — the
  property is checked for antisymmetry, transitivity, and the substitution
  axiom over a value set spanning the 2^53 boundary, the `i64` extremes, the
  float extremes, nested arrays, and nested objects — so the stable sort
  never observes an inconsistent comparator
  (`nan_and_non_finite_numbers_keep_a_total_deterministic_order`).
- One namespace's generation never opens another namespace's staleness gate,
  including tenant names that end where another tenant's key would begin
  (`tenant_and_name_separator_cannot_alias_a_namespace`).
- std-only, no dependencies, no unsafe, `deny(missing_docs)`, clippy pedantic
  clean under `-D warnings`.

## Focused-test evidence

All checks were run in this worktree on the final commit (commands and exit
codes in the packet report). The W3-2 red-test list is green:

| Test | File |
| --- | --- |
| `query_plan_preserves_result_ordering_on_an_immutable_snapshot` | `src/query/tests.rs` |
| `null_missing_and_exists_have_distinct_results` | `src/query/tests.rs` |
| `strict_generation_rejects_an_index_that_is_too_old` | `src/query/tests.rs` |
| `allowed_stale_query_reports_the_generation_it_used` | `src/query/tests.rs` |
| `index_delta_is_derived_from_the_immutable_commit_publication_event` | `src/index/tests.rs` |
| `index_publication_does_not_hold_the_storage_lock` | `src/index/tests.rs` |
| `index_generation_never_moves_backward_within_its_namespace_and_epoch` | `src/index/tests.rs` |
| `index_rebuild_publishes_a_monotonic_generation` | `src/index/tests.rs` |
| `replayed_index_build_cannot_overwrite_a_newer_generation` | `src/index/tests.rs` |
| `query_store_compiles_and_executes_against_supplied_snapshot` | `src/query/tests.rs` |
| `query_input_normalizes_typed_and_v1_text_through_one_compile_seam` | `src/query/tests.rs` |
| `query_input_rejects_unsupported_or_generic_query_text` | `src/query/tests.rs` |
| `query_store_borrow_prevents_engine_shutdown_while_live` | `src/query/tests.rs` |
| `query_rejects_snapshot_from_another_engine` | `src/query/tests.rs` |
| `query_rejects_snapshot_for_another_tenant` | `src/query/tests.rs` |
| `query_rejects_generation_from_another_namespace_or_epoch` | `src/query/tests.rs` |
| `plan_reuse_does_not_mutate_the_snapshot` | `src/query/tests.rs` |
| `projection_and_paging_have_typed_errors_and_boundaries` | `src/query/tests.rs` |
| `query_cancellation_is_bounded_and_does_not_leak_a_lock` | `src/query/tests.rs` |
| `query_rejects_a_wrong_tenant_scope` | `src/query/tests.rs` |

Focused extras: `v1_grammar_parses_the_supported_subset_into_typed_clauses`,
`typed_clause_validations_are_typed_and_bounded`,
`staleness_selection_reports_every_required_namespace`,
`total_document_order_ranks_missing_and_types_deterministically`,
`nan_and_non_finite_numbers_keep_a_total_deterministic_order`,
`materialization_checkpoint_stops_a_query_whose_bound_expired_after_the_scan`,
`a_large_corpus_stays_bounded_and_leaks_no_lock_after_cancellation`
(`src/query/tests.rs`);
`catalog_ignores_replayed_or_reordered_events`,
`namespace_epochs_move_only_forward`,
`segments_index_the_registered_field_of_their_own_tenant`,
`builder_fails_typed_before_any_namespace_or_event_exists`,
`index_lock_holders_never_block_storage_reads`,
`tenant_and_name_separator_cannot_alias_a_namespace` (`src/index/tests.rs`);
plus the five public-construction tests in
`tests/query/query_public_construction.rs`. The pre-existing suite (117
tests before this packet) stays green.

## Per-row behavior coverage notes

These notes say which focused tests EXPRESS a row's semantics. No row is
resolved, implemented, or qualified by them; every row keeps its `unresolved`
status and empty Proof ID list until its parity test and Proof ID exist.

| Rows | Semantics expressed by this packet's tests |
| --- | --- |
| PB-4000 | Result shape, ordering, and typed rejection classes through one seam: `query_store_compiles_and_executes_against_supplied_snapshot`, `query_plan_preserves_result_ordering_on_an_immutable_snapshot`, `query_input_rejects_unsupported_or_generic_query_text` |
| PB-4004 | Null/missing/exists distinction: `null_missing_and_exists_have_distinct_results` (V2 tracer semantics; V1's exact equality-null rule is not claimed) |
| PB-4006 | Range operators over the numeric and text domains in the condition set and grammar (`v1_grammar_parses_the_supported_subset_into_typed_clauses`) |
| PB-4013 | A defined total order with missing-first, unified numerics compared exactly across the Integer and Number encodings, and a NaN-safe non-finite slot: `query_plan_preserves_result_ordering_on_an_immutable_snapshot`, `total_document_order_ranks_missing_and_types_deterministically`, `nan_and_non_finite_numbers_keep_a_total_deterministic_order` |
| PB-4014 | Typed paging boundaries: `projection_and_paging_have_typed_errors_and_boundaries` |
| PB-4015 | Whole-document and named-field projection: `projection_and_paging_have_typed_errors_and_boundaries` |
| PB-4026 | Strict versus allow-stale selection and the used-generation report: `strict_generation_rejects_an_index_that_is_too_old`, `allowed_stale_query_reports_the_generation_it_used` |
| PB-4041 | Typed error contract for compile and execute across the `QueryError` variants exercised above |
| PB-4054 | Index state, staleness, and rebuild monotonicity: the five index tests plus `query_rejects_generation_from_another_namespace_or_epoch` |
| PB-4055 | Typed builder failures: `builder_fails_typed_before_any_namespace_or_event_exists` |
| PB-4062 | The namespace/tenant scope rule rejects cross-tenant index requirements before any plan exists (`query_rejects_a_wrong_tenant_scope`) and one namespace's generation never opens another namespace's gate (`tenant_and_name_separator_cannot_alias_a_namespace`) |

Rows PB-4001 through PB-4003, PB-4005, PB-4007 through PB-4012, PB-4016
through PB-4025, PB-4027 through PB-4040, PB-4042 through PB-4053, and
PB-4056 through PB-4061 are not expressed by this packet beyond the generic
typed-input discipline; they stay unresolved.

Family handoffs, kept explicit and unresolved: PB-3010 (counters as query and
index sources), PB-3020 (time-series query and index forms), and PB-3040
(family query/index inspection and multi-family projection). None of them is
expressible in this packet: `QuerySource` selects one user collection only,
the grammar has no family clause, and family records are not a query source.
`QuerySource::try_new` rejects the reserved family collection
`terrabase.families`, so a query cannot read raw family state through this
seam.

## Deviations from the declaration tree

- The `PhantomData` placeholders were replaced by real typed fields, as the
  contract's packet instruction allows. `QueryPlan` and `QueryResult` keep
  `Clone`, `Debug`, and `PartialEq` but drop `Eq`: predicate literals and
  documents carry `DocumentValue`, whose float variant has no total equality.
- `QueryError` keeps every declared variant and adds `SnapshotTenantMismatch`
  and `StorageFailure`, mirroring the accepted `FamilyError` pattern: the
  contract's tenant checks distinguish a snapshot of another tenant from a
  wrong tenant scope, and the Engine's snapshot view needs a typed failure
  case. `QueryError::InvalidSpec` now also covers rejected query text.
- New public query-module types carry the result contract the declaration
  tree left opaque: `QueryRow`, `ObservedGeneration`, `StaleIndexObservation`,
  `StalenessReport`, `IndexGenerationRef`, `IndexNamespace`,
  `QueryCondition`, `OrderingKey`, `OrderDirection`, `StalenessMode`, and the
  bound constants. They are reachable through `terrabase_v2::query::…` and
  deliberately not re-exported at the crate root, which would require editing
  `src/lib.rs`.
- `StalenessPolicy::try_new` rejects `AllowStale` with an empty namespace
  list: an allow-stale policy that requires no index would report nothing.
  Only strict may select the index-less base path.
- `QuerySource::try_new` rejects the reserved family collection. The family
  module owns the canonical constant and does not re-export it, so the query
  seam spells the same name locally.

## Assumptions

- The tenant binding for the direct `QueryStore::execute` path is the
  snapshot's tenant plus the plan's optional claim; the Platform-bound
  `EngineRequest::Query { scope, .. }` flow does not exist yet and will
  compare the claim against the authorized scope instead.
- A V1-compatible query adopts the executing snapshot's tenant because the
  grammar subset carries no tenant clause.
- The generation a snapshot "observed" for a namespace is, in this packet,
  derived from the catalog's publication history at the snapshot's storage
  sequence: the newest generation of any epoch whose covered sequence is at
  most the snapshot's sequence. The contract's `ReadSnapshot` set is the
  authoritative observation surface once it is readable (see the collision
  actions). Until then the two can disagree in exactly one direction: a
  covering segment may publish after the snapshot was created — for example
  a rebuild, or a namespace recreated under a new epoch, at the same covered
  sequence — so the catalog-derived observation can conservatively exceed
  the generation the snapshot actually observed at creation. It can never
  fall below it. That direction is the safe one for both policies: strict
  may reject with `IndexNotReady` more often than the recorded observation
  would, and allow-stale may report a required generation that is still not
  newer than what the catalog can supply, so neither policy can silently
  pass a generation the snapshot never observed as current.
- `Engine::snapshot_documents` is the trusted, validated read path; the query
  module performs no storage access of its own.
- Publication events are engine-global (they carry no tenant), so delta
  coverage is engine-global and namespaces choose their tenant scope.

## Limits

- Execution is a full snapshot scan. Segments are built and published, but no
  query is narrowed or ordered by index data; the staleness machinery selects
  and gates generations only. This is the documented tracer base path.
- The index catalog is in memory. Neither the epoch nor the generation
  history survives a restart, and there is no index manifest; the
  epoch-survives-restart rule is accepted as an input (`recreate_namespace`
  with a strictly newer epoch) and cannot be proved by this packet.
- Nothing wires the catalog into the Engine outside tests: the sink
  installation API is test-only, so a production Engine has an empty default
  catalog and every strict query that requires an index returns
  `IndexNotReady`, which is the contract's conservative behavior. For the
  same reason, `QueryStore::with_index_catalog` — the seam the in-crate
  tests use to observe a live catalog through the public execute path — is
  `#[cfg(test)]`-gated as of loop a2: it exists only to test the staleness
  gate against a fed catalog before the Engine owns one, it is unreachable
  from any non-test build, and collision action CA-3 removes it entirely
  when the Engine installs the catalog itself.
- A caller cannot cancel a compiled plan mid-flight: `Cancellation` clones
  snapshot the flag state, and the public construction path hands the signal
  over by value. The bounded checkpoints re-read the owned signal — at the
  pre-scan check, every 64 scanned documents, once at the scan/sort boundary,
  and every 64 materialized rows — and are proved through the crate-private
  model reference; the wire adapter owns the live-signal lifecycle later.
- Fields are top-level only; there are no nested paths, aliases, parameters,
  aggregations, grouping, distinct, suggestions, spatial, vector, or
  streaming forms, and no query explanation, timing, or statistics output.
  Includes are no longer a limit here: GAP-Q07 (ticket #64) added the typed
  includes contract — document paths, counters, and time-series, with stream
  includes and the stream/page equivalence proof — in
  `src/query/includes.rs`; see `docs/query/gapq07-includes-contract.md`.
  An include path is the one place this seam walks dotted segments, because
  V1's include walker does; predicate, ordering, and projection fields stay
  top-level.
- The a3 exact comparison originally covered the `order by` comparator
  only, and range predicate evaluation (`>`, `>=`, `<`, `<=`) widened an
  `Integer` operand into `f64` against a `Number`. GAP-EVAL (ticket #62)
  closed that residual: the predicate path now runs the same exact
  cross-encoding comparison as the order-by path, pinned by
  `predicate_comparison_is_exact_across_integer_and_number_domains`
  (`src/query/tests.rs`); see `docs/query/gap-eval-evaluator-parity.md`.
- The totality property is exhaustive over its listed value set (the 2^53
  neighborhood, the `i64` and finite-`f64` extremes, the smallest magnitudes,
  the two zeroes, and nested containers), not over every representable float
  bit pattern; the construction itself (exact cross-type comparison plus
  rank-gated `total_cmp` for the non-finite slot) is total by argument, with
  the property set as the sampled witness.
- The too-old strict rejection is proved through the `GenerationView` seam
  with independent observed and supplied generations, because a single
  monotonic catalog can never supply less than it observed. The live catalog
  proves the missing and not-comparable (epoch) arms.

## What is not claimed

- No V1 parity claim for any row: all 63 query rows and PB-3010, PB-3020,
  PB-3040 remain unresolved with no Proof ID.
- No claim that the V1 grammar subset matches V1's RQL beyond the subset
  definition above; no LINQ translation, wire format, or parameter form.
- No performance claim, benchmark, or speed statement of any kind; full-scan
  execution is not a performance position.
- No durability, recovery, replication, or convergence claim for index state;
  in-memory segments are not a storage format.
- No Platform authorization, audit, or grant claim; the tenant checks here
  are database-owned validation, not authorization.

## Proposed parent collision actions

These edits touch files this packet must not change. They are proposals for
the integrator; none is applied here.

1. `src/operations.rs`: implement `V1QueryText::try_new` by delegating to
   `crate::query` grammar validation, so the public V1 wrapper validates
   before use:

   ```rust
   pub fn try_new(text: &str) -> Result<Self, QueryInputError> {
       crate::query::validate_v1_text(text)?;
       Ok(Self(text.to_owned()))
   }
   ```

   with `pub(crate) fn validate_v1_text(text: &str) -> Result<(), QueryInputError>`
   added to `src/query/v1.rs` (it is `v1::validate`, currently compiled for
   tests only; `query/mod.rs` already re-exports it as
   `crate::query::validate_v1_text` under `#[allow(unused_imports)]` so the
   delegation above compiles the moment the constructor uses it).
   `compile`'s `V1Compatible` arm already parses `as_str()`
   through the same function, so after this action the public path is
   complete; add the public-path assertions to
   `query_input_normalizes_typed_and_v1_text_through_one_compile_seam` and
   `query_input_rejects_unsupported_or_generic_query_text`.
2. `src/types.rs`: give the generation types crate-private accessors and
   constructors — `IndexGeneration::{try_parts, namespace, persisted_epoch,
   sequence}`, `IndexGenerationSet::{from_generations, generations, find}`,
   and `ReadSnapshot::indexes()` — so execution can cross-check the
   snapshot's authoritative `IndexGenerationSet` against the catalog-derived
   observation and `QueryResult` can report the shared `IndexGeneration`
   type. Until then, the query seam reports generations through its own
   typed triple and derives the observation from catalog history.
3. `src/engine/mod.rs`: make the publication sink installable outside tests
   (drop `#[cfg(test)]` from `set_publication_sink`), have `Engine::open`
   own one `IndexCatalog`, install its sink, hand it to
   `QueryStore::from_engine`, and record the observed generations into
   `ReadSnapshot` when `read_snapshot` builds one. This action also removes
   the `#[cfg(test)]` `QueryStore::with_index_catalog` seam (the index tests
   then observe the Engine-owned catalog directly) and retires the
   empty-default-catalog limit and the catalog-derived-observation
   assumption above.
4. `src/lib.rs` (optional): re-export the new query-module value types at the
   crate root if the SDK wants them at `terrabase_v2::…` instead of
   `terrabase_v2::query::…`.
