# GAP-Q11: the result-statistics contract

Status: contract record for Forgejo ticket TerraBaseV2#66 (GAP-Q11, the
adapter-facing query result shape and statistics contract), recorded
2026-08-27 against the `fleet/gapq11-2` worktree of `develop` @ `d7d8563`.

V1 evidence identity: `fa99b9257776dbe625e6a2388c74c9ce916637ee` under
`.worktrees/terrabase-v1-fa99b925-readonly` (read-only). Every V1 file
reference below is inside that pinned tree.

Test evidence: `src/query/tests.rs` (the in-crate contract tests: the scan
accounting, the typed count caps, the etag's state tracking, the diagnostic
duration's shape, and the aggregate stale flag) and
`tests/query/result_statistics.rs` (the public count-cap refusal).
`TenantScope` has no public constructor by design, so execution-derived
statistics are in-crate tests, following the convention the query seam
already records; the public test pins the construction-time refusal.

Disposition vocabulary: **closed-here** (implemented and pinned),
**pinned-compatible** (no code change needed; the pin proves V1
compatibility), **deferred** (recorded residual with evidence and a named
gate), **accepted-difference** (a deliberate V2 shape, recorded).

## What the surface is

V1 answers a query with a result array plus a metadata block. The client
shape is `QueryResultBase` — the includes siblings, `IsStale`,
`IndexTimestamp`, `IndexName`, `ResultEtag`, `LastQueryTime`, `NodeTag`,
and `Timings`
(`src/TerraBase.Client/Documents/Queries/QueryResultBase.cs:13-91`) —
extended by `QueryResult` with the counts `TotalResults`,
`CappedMaxResults`, `SkippedResults`, `ScannedResults`, the
highlighting/explanation maps, and `DurationInMs`
(`src/TerraBase.Client/Documents/Queries/QueryResult.cs:10-55`), and
mirrored client-side as `QueryStatistics`
(`src/TerraBase.Client/Documents/Session/QueryStatistics.cs:10-86`).

V2 carries the same inventory in typed shape. Sibling-of-rows data already
lives on `QueryResult` — the include set (GAP-Q07's precedent), the
staleness report, the index generation reference — and this contract adds
the one sibling that was missing: `QueryResult::statistics()`, returning
the `QueryStatistics` of `src/query/statistics.rs`:

| Field | Type | Carries |
| --- | --- | --- |
| `scanned` | `u64` | candidates of the source collection the predicate evaluated |
| `skipped` | `u64` | scanned candidates that did not match |
| `capped_matched` | `u64` | the matched count as the paging window caps it |
| `result_etag` | `ResultEtag` | the state token of the state the answer came from |
| `duration` | `DiagnosticDuration` | the observed execution window (diagnostic-only) |

## The full V1 inventory, mapped

| V1 field (cite) | V2 mapping | Disposition |
| --- | --- | --- |
| `Results` (`QueryResultBase.cs:13`) | `QueryResult::rows()` | pinned-compatible |
| `Includes` (`QueryResultBase.cs:18`) | `QueryResult::includes()` (`IncludeSet`) | closed-here (GAP-Q07) |
| `CounterIncludes` (`QueryResultBase.cs:23`) | `IncludeSet` counters sub-contract | closed-here (GAP-Q07) |
| `RevisionIncludes` (`QueryResultBase.cs:28`) | — | deferred (GAP-Q07 limits; revisions stay with their owning ticket) |
| `IncludedCounterNames` (`QueryResultBase.cs:34`) | the typed `CounterInclude` name list on the plan (`QueryPlan::includes()`), not an echo | accepted-difference (the clause is typed at the seam, so the echo would duplicate the plan) |
| `TimeSeriesIncludes` (`QueryResultBase.cs:39`) | `IncludeSet` time-series sub-contract | closed-here (GAP-Q07) |
| `CompareExchangeValueIncludes` (`QueryResultBase.cs:44`) | — | deferred (GAP-Q07 limits) |
| `IncludedPaths` (`QueryResultBase.cs:49`) | `QueryPlan::includes()` clause list | accepted-difference (echoes the request, not the result; V1 fills it from `query.Metadata.Includes`, `src/TerraBase.Server/Documents/Indexes/Index.cs:3592`) |
| `IsStale` (`QueryResultBase.cs:58`) | `StalenessReport::is_stale()` | closed-here (this packet) |
| `IndexTimestamp` (`QueryResultBase.cs:64`) | — | accepted-difference (V2's index state is epoch/generation, not a wall-clock; `IndexGenerationRef` names the state V1's timestamp dated) |
| `IndexName` (`QueryResultBase.cs:69`) | `IndexGenerationRef::namespace()` (typed tenant + name) | accepted-difference (typed namespace, never a bare string; `None` for index-less execution) |
| `ResultEtag` (`QueryResultBase.cs:75`) | `QueryStatistics::result_etag()` | closed-here (this packet) |
| `LastQueryTime` (`QueryResultBase.cs:80`) | — | deferred (index-side idle bookkeeping driving V1's `LastQueriedTimeTracker`; the IX wave's retirement policy owns any V2 analog) |
| `NodeTag` (`QueryResultBase.cs:85`) | — | accepted-difference (no cluster node identity at this seam; node topology is the TBEP/host plane's vocabulary) |
| `Timings` (`QueryResultBase.cs:90`) | — | deferred (per-part timing scopes; see the timing section) |
| `TotalResults` (`QueryResult.cs:15`) | `QueryResult::matched()` | pinned-compatible |
| `CappedMaxResults` (`QueryResult.cs:21`) | `QueryStatistics::capped_matched()` | closed-here (this packet, saturating rule below) |
| `SkippedResults` (`QueryResult.cs:26`) | `QueryStatistics::skipped()` | closed-here (this packet, FILTER accounting below) |
| `ScannedResults` (`QueryResult.cs:33`) | `QueryStatistics::scanned()` | closed-here (this packet) |
| `Highlightings` (`QueryResult.cs:38`) | — | deferred (text-search surface; no highlighting exists at this seam) |
| `Explanations` (`QueryResult.cs:43`) | — | deferred (query-plan explanation; the optimizer's records are the nearest V2 surface) |
| `DurationInMs` (`QueryResult.cs:48`) | `QueryStatistics::duration()` | closed-here (this packet, diagnostic-only) |
| `IndexDefinitionRaftIndex` / `AutoIndexCreationRaftIndex` (`QueryResult.cs:50-54`) | — | deferred (cluster raft index of index creation; no raft at this seam) |
| `QueryStatistics.Timestamp` (client, `QueryStatistics.cs:43`) | — | accepted-difference (client-side alias of `IndexTimestamp`; same disposition) |
| `QueryStatistics.RequestedByUser` (client, `QueryStatistics.cs:85`) | — | accepted-difference (session-side flag, not result metadata) |

Every field of V1's three statistics shapes maps to a row above; nothing is
silently dropped.

## The FILTER accounting

V1's scan accounting lives in two places. On the index path, the filter
wrapper counts what its JavaScript filter saw:
`AbstractQueryFilter.Apply` increments the skipped count for a null
candidate and for a rejected script result, increments the scanned count
per examined document, and stops at `Query.FilterLimit`
(`src/TerraBase.Server/Documents/Indexes/Persistence/AbstractQueryFilter.cs:37-60`,
abstract counters at `:18` and `:32-33`, wired to the result by
`QueryFilter.cs:40` and `ShardedQueryFilter.cs:34-41`); the result is
filled as `resultToFill.SkippedResults = skippedResults.Value;
resultToFill.ScannedResults = scannedResults.Value;` with the scanned
value re-asserted when a filter script ran
(`src/TerraBase.Server/Documents/Indexes/Index.cs:3590-3595`). On the
collection path, the enumerator increments its skipped reference for a
deleted candidate and for a DISTINCT duplicate
(`src/TerraBase.Server/Documents/Queries/CollectionQueryEnumerable.cs:207`,
`:228`, `:261`, `:277`) and its scanned reference per examined document
under the same filter limit (`:304-308`).

V2's execution is an index-less scan, so the predicate **is** the filter:
`scanned` counts the candidate documents of the source collection the
predicate was evaluated against, and `skipped` counts the candidates that
did not match. Documents of other collections are not candidates and are
counted nowhere. V1's deleted-candidate skip has no V2 analog because a
snapshot view lists live documents only; V1's DISTINCT duplicate skip
belongs to the DISTINCT operator, which this seam does not implement
(deferred with it). Both counts saturate; neither wraps.

**accepted-difference — no skipped-count paging correction.** V1's client
doc pins that `SkippedResults > 0` "invalidated" `TotalResults`
(`QueryStatistics.cs:27-31`): a filtered collection query pages by adding
the server's skipped count to the next page's start, because the storage
cursor cannot jump past non-matches. V2 pages over the ordered matched set
with a typed offset, so `matched()` is exact for every page and no caller
ever adjusts a start by the skipped count. The `skipped` V2 reports is
pure diagnostic accounting, never a paging input.

## Count caps: the decision, against V1

V1 has three count-cap behaviors, all in-band:

1. An unbounded collection query answers with the marker `TotalResults =
   -1` when the count is unknown and results exist
   (`src/TerraBase.Server/Documents/Queries/Dynamic/CollectionQueryRunner.cs:254`,
   with `UnboundedQueryResultMarker = 0` at `:25` and the startsWith
   unbounded path at
   `src/TerraBase.Server/Documents/Queries/CollectionQueryEnumerable.cs:410-422`).
2. The capped count clamps at zero on the index path — `Math.Max(0,
   Math.Min(query.Limit ?? long.MaxValue, totalResults - offset))`
   (`src/TerraBase.Server/Documents/Indexes/Index.cs:3552-3555`) — but not
   on the collection path, where `Math.Min(query.Limit ?? int.MaxValue,
   TotalResults - offset)` (`CollectionQueryRunner.cs:266-268`) can go
   negative, and where the unknown-count arm answers the limit or `-1`
   outright (`:262`).
3. A non-positive `FilterLimit` refuses
   (`src/TerraBase.Server/Documents/Queries/IndexQueryServerSide.cs:382-383`),
   but the limit is otherwise clamped into `int.MaxValue`
   (`IndexQueryServerSide.cs:230-232`).

**Decision: V2 refuses; it never answers with a marker.** Concretely:

- A page limit above `MAX_PAGE_LIMIT` (`1024`) refuses typed at
  construction with `RequestValidationError::InvalidQuery`
  (`QueryPaging::try_new`, `src/query/spec.rs`), pinned publicly by
  `tests/query/result_statistics.rs`. A count the engine cannot bound is a
  refusal, not a `-1`.
- A refused execution produces **no** statistics: the existing
  `Deadline`, `Cancelled`, and `StorageFailure` refusals stop the query
  before a result exists, so a partial count can never masquerade as a
  complete one.
- Where V1 would emit a sentinel or a negative value, V2 saturates: counts
  saturate at `u64::MAX` instead of wrapping, and `capped_matched` is
  `min(limit, matched - offset)` with a saturating subtract — zero when
  the offset reaches past the matched set, never negative, never `-1`.

The reason is typed-shape, not convenience: V1's `-1` is an in-band marker
in a numeric field, so a caller must know the sentinel to know whether it
read a count. In V2 an unknown count is structurally impossible for this
execution (the scan materializes every match before paging), and the day
an execution shape cannot know its count, that shape must refuse rather
than invent a marker value that a real count could equal. This is the
decision AC 2 of the ticket names as "against V1, cited".

## The state token

V1 computes `ResultEtag` as a non-cryptographic `XXHash64` over a buffer
of storage etags: on the collection path, the last document etag, the last
tombstone etag, the number of documents, and the family etags the query
touches (`src/TerraBase.Server/Documents/Queries/Dynamic/CollectionQueryRunner.cs:275-337`,
the hash at `:335`); on the index path, `CalculateIndexEtag` hashes the
index state including staleness (`src/TerraBase.Server/Documents/Indexes/Index.cs:4173-4189`).

V2's `ResultEtag` is the same class of object: a non-cryptographic 64-bit
FNV-1a hash over the state the answer was computed from — the snapshot
sequence and, per required namespace in plan order, the epoch/generation
the snapshot observed and the one the staleness gate accepted. It answers
exactly one question: whether a re-execution against the same engine would
read the same state. It is **not** a content digest (two different queries
over the same state share a token, as V1's do), not a cross-engine
identity, not a consistency token, and not ordered.

## Diagnostic timing: the wording audit

PD-2026-08-26-03 (the hard-data rule) governs every timing word in this
packet. V1's timing fields are `DurationInMs` — "The duration of actually
executing the query server side" (`QueryResult.cs:45-48`), measured as
`(long)sw.Elapsed.TotalMilliseconds` around the runner dispatch
(`src/TerraBase.Server/Documents/Queries/QueryRunner.cs:71`) — and the
per-part `Timings` tree with its `DurationInMs` root and named-part map
(`src/TerraBase.Client/Documents/Queries/Timings/QueryTimings.cs:19` and
`:24`, server scopes at
`src/TerraBase.Server/Documents/Queries/Timings/QueryTimingsScope.cs:21-67`).

V2 implements one timing field, `DiagnosticDuration`, and the audit of
what it **is** and **is not** is written into the type itself
(`src/query/statistics.rs`), repeated here as the record:

- **Is**: the wall-clock time the executing thread observed between
  entering the execution path and constructing the result — the binding
  checks, the staleness gate, the snapshot scan, the ordering, the
  projection, and the include materialization of this one call, on a
  monotonic clock, recorded for every successful execution. The optimizer
  pass a dynamic plan additionally runs is a separate catalog operation
  outside the window.
- **Is not**: a benchmark. Not comparable across engines, builds,
  machines, or workloads; not a latency guarantee, budget, or service
  level; and it proves nothing about speed. No performance claim may rest
  on this number — only TerraBase.Benchmark receipts from the exclusive
  benchmark lane carry performance evidence.

Two further non-claim guards are load-bearing. First, the type is named
`DiagnosticDuration`, not `Duration`/`Latency`, so every use site names
the number's class. Second, `QueryStatistics` equality deliberately
excludes the duration: an answer's identity is its rows, counts, and state
token, never its timing — two executions of one plan against one snapshot
are equal results with independently observed durations. The per-part
`Timings` tree is **deferred**: it exists to attribute Lucene/Corax/load
phases V2's execution does not have, and re-adding it belongs with the
execution shapes that would give the parts meaning.

## Limits

- `IndexTimestamp`, `LastQueryTime`, `NodeTag`, the raft indices, the
  highlighting/explanation maps, and the per-part timings are deferred or
  accepted-difference per the table; each stays with its owning wave.
- The etag is engine-scoped. A client caching across engine restarts
  re-validates, exactly as V1's per-database etags require.
- Parity status records (`parity/overlays/**`) are outside this packet's
  write scope; this document is the evidence the parent adjudication uses
  for any PB row status change.
- No performance claim is made; nothing here asserts speed, and
  PD-2026-08-26-03's gates were not triggered. No benchmark lane was
  entered and no Benchmark Receipt exists for this packet.
