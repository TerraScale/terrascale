# GAP-Q07: the includes contract (documents, counters, time-series)

Status: contract record for Forgejo ticket TerraBaseV2#64 (GAP-Q07, folding
D11 document includes and Q15's stream-includes/page-equivalence half; Q15's
CSV-format half belongs to the host/CLI tickets #11/#12 per parent
adjudication `PD-2026-08-26-14` D3), recorded 2026-08-27 against the
`fleet/gapq07-2` worktree of `develop` @ `8e4c4c5`.

Parity row: PB-4016 `document-counter-and-timeseries-includes` (core),
currently `cross-seam-partial` in `docs/parity/ps-02-query-sweep.md`. This
packet adds the include contract that row names; the row's own status record
lives in the parity overlays, which are outside this packet's write scope.

V1 evidence identity: `fa99b9257776dbe625e6a2388c74c9ce916637ee` under
`.worktrees/terrabase-v1-fa99b925-readonly` (read-only). Every V1 file
reference below is inside that pinned tree.

Test evidence: `src/query/includes_tests.rs` (the in-crate contract tests:
document, counter, and time-series includes, the cycle and bound refusals,
the page scoping, and the stream/page equivalence proof, which drives the
event backbone's query stream source over `Engine::streams`) and
`tests/query/includes_contract.rs` (public construction: the typed clause
values, the `with_includes` binding, the frozen clause list on the plan,
and the typed bounds). `TenantScope` has no public constructor by design,
so snapshot-bound materialization and the equivalence proof are in-crate
tests rather than integration tests, following the convention the query and
streaming seams already record.

Disposition vocabulary: **closed-here** (implemented and pinned),
**pinned-compatible** (no code change needed; the pin proves V1
compatibility), **deferred** (recorded residual with evidence and a named
gate), **accepted-difference** (a deliberate V2 shape, recorded).

## What an include is

An include is **same-request secondary materialization**: one clause of a
query that names a path out of each result row and asks the engine to
materialize what that path points at, in the same response, beside the rows.
It is not a join, not a projection, and not a second round trip.

The materialization is carried on the result, never merged into it. V1 pins
that shape: `QueryResultBase` carries `Includes`, `CounterIncludes`,
`RevisionIncludes`, `TimeSeriesIncludes`, and `CompareExchangeValueIncludes`
as siblings of the result array
(`src/TerraBase.Client/Documents/Queries/QueryResultBase.cs:18-44`).
V2's `QueryResult::includes()` returns one `IncludeSet` with the three
sub-contracts this ticket owns; revisions, compare-exchange values, and the
highlighting/timing tokens V1 also folds into its `include` clause are not
in scope here and stay with their owning tickets.

## The clause vocabulary

All clause values live in `src/query/includes.rs` and are constructed only
through validated `try_new` paths, observed only through read-only
accessors, and bounded by closed constants:

| Constant | Value | Bounds |
| --- | --- | --- |
| `MAX_INCLUDES` | 8 | clauses per query, matching `MAX_LOADS` |
| `MAX_INCLUDE_PATH_SEGMENTS` | 8 | dotted segments in one include path |
| `MAX_INCLUDE_DEPTH` | 4 | levels of document include chaining |
| `MAX_INCLUDE_DOCUMENTS` | 1024 | distinct included documents per execution |
| `MAX_INCLUDE_COUNTER_NAMES` | 32 | named counters per clause, matching `MAX_IN_VALUES` |

### Document includes — `DocumentInclude`

`DocumentInclude::try_new(path, depth)` carries an [`IncludePath`] and a
chaining depth, and `with_id_prefix(prefix)` binds the optional identifier
prefix afterwards.

**Path walking.** An [`IncludePath`] is a dotted segment walk. From one
source document the walk navigates `Object` fields by name and flattens
`Array` elements at every step, exactly as V1's
`SelectTokenWithTerraBaseSyntaxReturningFlatStructure` plus the array
recursion in `IncludesUtil.ExecuteInternal`
(`src/TerraBase.Client/Documents/Session/IncludesUtil.cs`). A leaf is a
candidate reference when it is `String` or `Integer` — V1's
`token is string or LazyStringValue` and its `long longValue` arm, which
formats an integer reference as its invariant decimal string. Every other
leaf shape contributes nothing.

**The prefix form.** V1 spells a collection-prefix include `path(prefix)`
and a format include `path([{0}/]`
(`IncludesUtil.GetIncludePath`, same file): the prefix is prepended to each
gathered reference, and the original reference is *also* loaded alongside
the transformed one. V2 types that as `id_prefix: Option<String>` and keeps
both behaviors — a prefixed reference and, when a prefix applies, the bare
reference — because V1 loads both and a caller relying on either must not
change answers. V1's `string.Format` form is **not** carried: it is a
client-side string-template convenience over the same one-substitution
shape, and the typed seam has no template surface. **accepted-difference**.

**Depth.** V1's document includes are exactly one level deep:
`IncludeDocumentsCommand.Gather(Document)` collects the referenced ids of
the result rows and `Fill` loads them, but `Fill` never calls `Gather` again
(`src/TerraBase.Server/Documents/Includes/IncludeDocumentsCommand.cs` — `Gather(Document)`:50-71, `Fill`:126-163)
— the included documents are never themselves gathered from. `depth = 1` is
therefore V1 parity and is the default.

`depth` in `2..=MAX_INCLUDE_DEPTH` chains the *same* path out of each
included document. This is a **closed-here** V2 extension, and it is the
reason the contract needs cycle detection at all: V1 cannot loop because it
never takes a second step. The chaining is bounded by construction — the
level counter runs exactly `depth` times and never recurses past it.

**The row's own document.** V1's `AddToIgnore(document.Id)` puts each result
row's own identifier into `_idsToIgnore` unless the query is a projection,
so a row is never re-materialized as an include of itself
(`IncludeDocumentsCommand.cs:60`, method at `:172-181`). V2 keeps that rule: a reference that
resolves to a document already carried as a row of this result is not
materialized into the include set. V1's special case for the include path
literally spelling `id` (`Constants.Documents.Indexing.Fields.
DocumentIdFieldName`) is a no-op under the same ignore rule and is not
carried separately. **pinned-compatible**.

### Counter includes — `CounterInclude`

`CounterInclude::try_new(path, names)` carries an [`IncludePath`] and a
bounded name list. An empty name list reads the whole counter group, which
is V1's `CounterIncludesToken.All(sourcePath)`; a non-empty list is V1's
`CounterIncludesToken.Create(sourcePath, name)` repeated per name
(`src/TerraBase.Client/Documents/Session/Tokens/CounterIncludesToken.cs`,
and the `IncludeCounters` walk that emits both forms in
`src/TerraBase.Client/Documents/Session/AbstractDocumentQuery.Includes.cs`).

Counter includes gather from the result rows at depth 1 only. V1's counter
include has no chaining form, and counters are not documents, so there is
nothing to chain *through*; a deeper counter include is **not** a shape this
contract defines.

Counter values are read through the family seam's own typed reader —
`FamilyReadRequest::CounterGroup(CounterRead)` through
`FamilyStore::read` — so the includes module **coordinates** the read and
never re-derives counter state. The returned entries are the family seam's
own `CounterEntry` values.

### Time-series includes — `TimeSeriesInclude`

`TimeSeriesInclude::try_new(path, name, range)` carries an [`IncludePath`],
one series name, and an optional inclusive range. This is V1's
`TimeSeriesIncludesToken.Create(sourcePath, range)` with its
`timeseries(sourcePath, 'name', from, to)` spelling
(`src/TerraBase.Client/Documents/Session/Tokens/TimeSeriesIncludesToken.cs`).
`range: None` reads the whole series, V1's `null, null` form.

V1 additionally spells `last(n)` and `last(time, unit)` range forms. Those
are **deferred**: they need a rollup/retention decision against the family
seam's `TimeSeriesPolicy`, which is the families wave's surface, not this
packet's. The inclusive from/to range is the form this contract pins.

Entries are read through `FamilyReadRequest::TimeSeriesRange(TimeSeriesRead)`
and returned as the family seam's own `TimeSeriesPage`, whose entries are
already in timestamp order.

## Bounded, deterministic, typed refusals

The bar the ticket names is that cycle detection and depth limits are typed
refusals, never unbounded recursion, and that ordering is deterministic.

**Construction refusals** happen at the seam, before any data access, as
`RequestValidationError::InvalidQuery` — the same class the loads clause
uses: an include list longer than `MAX_INCLUDES`, a path with more than
`MAX_INCLUDE_PATH_SEGMENTS` segments or an empty/invalid segment, a depth of
zero or above `MAX_INCLUDE_DEPTH`, an empty or invalid series name, a
counter name list longer than `MAX_INCLUDE_COUNTER_NAMES` or carrying a
duplicate name, and an invalid optional prefix.

**`QueryError::IncludeCycle`.** Chaining introduces the possibility of a
reference cycle: `A`'s path names `B`, and `B`'s path names `A`. The
materializer records the reference *edges* it actually walks — one edge
per (gather source, resolved gather target) pair — into a finite graph,
and a three-color depth-first search over that graph reports a back edge
as a cycle: the query refuses with `IncludeCycle` rather than following
it.

The rule is exact over the traversed graph, not approximate: edges are
recorded whether or not the target is new at that step, so a reference
that closes a loop through any *resolved non-row* document refuses. A
diamond (`A → B`, `A → C`, `B → D`, `C → D`) is not a cycle and does not
refuse — `D` is simply materialized once. Deduplication is the set
semantics V1 already applies through its `HashSet<string> _includedIds`.

One edge shape is deliberately absent from the graph: an edge whose
target is a *row* of the result. Rows are level-0 gather sources, and V1
puts every result row's id on the ignore set before gathering
(`AddToIgnore`, `IncludeDocumentsCommand.cs:60`/`172-181`), so a
reference back to a row truncates exactly as an unresolved reference
does — it is never walked as a gather target, and a "cycle" that closes
through a row answers instead of refusing. That truncation is V1 parity,
pinned by
`document_includes_truncate_a_cycle_that_closes_through_a_result_row`.

**`QueryError::IncludeBoundExceeded`.** Depth bounds the number of levels,
not the width. A single array field can carry an unbounded number of
references, so the materializer counts distinct included documents and
refuses past `MAX_INCLUDE_DOCUMENTS`. Without this bound the include set
would be the one unbounded allocation in an otherwise bounded execution.

Time-series include width is bounded by the family seam's own retention
policy, which bounds the page a series read can return; that bound is the
family seam's contract and is not re-derived here.

**Determinism.** Every set in the materializer is a `BTreeMap`/`BTreeSet`
over the reference string or the document identifier, so documents
materialize in ascending identifier order, counters in ascending
(document, name) order with entries sorted by name, and time-series in
ascending (document, name) order with entries in the family reader's
timestamp order. Resolution of one bare reference uses the same
first-match-in-canonical-key-order rule the loads clause and the engine's
identifier-only point read apply, built from the same materialized snapshot
view — so a load, a point read, and an include never disagree about which
document one bare identifier names.

## The seam

`QuerySpec::with_includes(Vec<IncludeClause>)` attaches the clause list,
mirroring `with_loads`; a query without includes materializes nothing and
pays nothing. `QueryPlan::includes()` exposes the frozen clause list.
`QueryResult::includes()` returns the `IncludeSet`.

The include set is materialized after ordering and paging, from exactly the
documents the result's rows carry. Paging an include-bearing query therefore
pages its includes: page 2 materializes the includes of page 2's rows, not
of the whole matched set. That is the V1 behavior — `Gather` runs per result
document of the response being built — and it is what makes the
stream equivalence below provable rather than approximate.

## Stream includes and page equivalence

The event backbone's query stream source
(`StreamSource::Query(QueryPlan)` in `src/streaming`) executes the immutable
plan once per batch and slices the plan's bounded result window at the
cursor offset (`src/streaming/sources.rs`, `query_batch`). A plan that
carries includes now materializes them **over the sliced rows of that batch
only**, and carries the `IncludeSet` on the `StreamBatch`.

Because both paths materialize from row sets, and materialization is a
function of the *set* of source documents (identifiers are deduplicated
through an ordered map, and the rows' own documents are ignored by the same
rule in both paths), the batches partition the plan's window and their
include sets are equal in union to the single paged execution's include set:

> For one plan, one include set, and one snapshot, the union of the
> `IncludeSet`s of every batch of the streamed query equals the `IncludeSet`
> of the single paged execution over the same window.

`src/query/includes_tests.rs`, in
`stream_includes_and_paged_includes_materialize_the_same_set`, proves that
equality directly: one engine, one snapshot, one include-bearing plan
covering document, counter, and time-series clauses, drained through the
event backbone's stream source at a batch limit smaller than the plan's
page limit, and compared field by field against one paged execution.

The backbone itself is unchanged. This packet adds the `includes` field to
`StreamBatch`, populates it in `query_batch`, and maps the two new
`QueryError` refusals onto `StreamError::InvalidSource` in the existing
`map_query_error` — the same place every other query refusal already maps.
Event-side includes are SE-03's surface to extend; nothing here duplicates
the backbone.

## V1 differences recorded

- **accepted-difference — exact identifiers.** V1 gathers and resolves
  include identifiers with `StringComparer.OrdinalIgnoreCase`
  (`IncludeDocumentsCommand.cs:58`, also `:180`). V2's identifier rules are exact-match
  with no case folding, consistent with the loads clause, the point read,
  and every other identifier comparison at this seam. Folding case would
  make an include disagree with the rest of the seam.
- **accepted-difference — no projection ignore exemption.** V1's
  `_idsToIgnore` is suppressed when the query is a projection, so a
  projected row *can* be re-materialized as its own include. V2's projection
  is a field subset of the same row document, and the row's document is
  already carried on every `QueryRow`, so the exemption would only
  duplicate data the caller already holds.
- **accepted-difference — missing references are omitted.** V1's `Fill` has
  an `includeMissingAsNull` flag that materializes an absent reference as a
  null-bearing placeholder. The default path omits it, and V2 keeps only
  that: an include reference that resolves to no document contributes
  nothing to the include set. Absence stays observable by the reference
  simply not being present, which is the same shape the loads clause gives
  a null binding on the projection side.
- **accepted-difference — one family read per target.** Counter and
  time-series includes read through `FamilyStore::read`, one read per
  distinct target. The read count carries no numeric typed bound of its
  own (unlike the document include's `MAX_INCLUDE_DOCUMENTS`): it is
  page-window-bounded — one read per distinct target referenced by the
  rows of the page being materialized, width bounded per read by the
  family retention policy — and wall-clock bounded by the deadline
  checkpoints every read passes through (`INCLUDE_READ_WINDOW` when the
  plan carries no deadline). No performance claim is made about that
  shape, and none may be: no benchmark lane was entered and no Benchmark
  Receipt exists for this packet.

## Limits

- The `include` keyword is not in the V1-compatible grammar subset
  (`src/query/v1.rs`) and is not added here. Surface spelling is the TQL
  wave's (`docs/tql/`), which owns the parser; this packet owns the typed
  seam beneath it.
- Revisions, compare-exchange, highlighting, timings, and explanation
  includes are V1 `include` clause members that this contract does not
  define; they stay with their owning tickets.
- No row outside the result's own page has its includes materialized, in
  either the paged or the streamed path. A caller wanting the whole matched
  set's includes pages through it.
- Parity status records (`parity/overlays/**`) are outside this packet's
  write scope; this document is the evidence the parent adjudication uses
  for any PB-4016 status change.
- No performance claim is made; nothing here asserts speed, and
  `PD-2026-08-26-03`'s gates were not triggered.
