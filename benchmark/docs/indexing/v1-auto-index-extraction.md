# V1 auto-index and query-optimizer extraction

Ticket: TerraScale/TerraBaseV2#37 (IX-01). Charter decision:
PD-2026-08-26-09 (automatic indexing and the always-optimal query path).

V1 source identity: commit `fa99b9257776dbe625e6a2388c74c9ce916637ee`,
the charter's pinned TerraBase commit. This extraction was read from the
pinned clean read-only Git worktree at that commit (the checkout reached
through `TERRABASE_V1_ROOT`); `git rev-parse HEAD` was verified to match
the charter pin and the worktree status to be clean before reading.
Citations use the parity-contract form `[v1:path::Symbol]`; every cited
path is relative to the repository root of that checkout. This document
records V1 behavior only. V2 decisions live in
`docs/indexing/dispositions.md` and
`docs/adr/0010-auto-indexing-and-query-optimizer.md`.

## Where the optimizer lives in V1

V1 has no class literally named `QueryOptimizer`. The name appears in
three places, and the distinction matters for V2:

1. The **query planner** that selects or creates an index per query. V1
   names it "the query optimizer" only through its off-switch
   (`Indexing.DisableQueryOptimizerGeneratedIndexes`, described as
   "Disable query optimizer generated indexes (auto-indexes). Dynamic
   queries will not be supported."
   [v1:src/TerraBase.Server/Config/Categories/IndexingConfiguration.cs::IndexingConfiguration]),
   the error text of the replacement runner
   ([v1:src/TerraBase.Server/Documents/Queries/InvalidQueryRunner.cs::InvalidQueryRunner.ErrorMessage]),
   and the query-timing scope that wraps planning
   (`QueryTimingsScope.Names.Optimizer`,
   [v1:src/TerraBase.Server/Documents/Queries/Timings/QueryTimingsScope.cs]).
   Its logic is `DynamicQueryRunner`, `DynamicQueryMapping`,
   `DynamicQueryToIndexMatcher`, and `AutoIndexNameFinder`.
2. The **Corax boolean query optimizer** at
   `src/TerraBase.Server/Documents/Indexes/Persistence/Corax/QueryOptimizer/`
   (`CoraxAndQueries`, `CoraxOrQueries`, `CoraxBooleanQueryBase`), which
   merges AND/OR match subtrees inside one Corax index evaluation. It is
   below index selection and is not the planner.
3. The **static index-merge suggestion engine**
   (`IndexMerger`), which proposes merges of explicitly created index
   definitions; it is reachable only through the suggest-index-merge
   handler and never runs automatically
   [v1:src/TerraBase.Server/Documents/Indexes/IndexMerging/IndexMerger.cs::IndexMerger],
   [v1:src/TerraBase.Server/Documents/Handlers/Processors/Indexes/AbstractIndexHandlerProcessorForSuggestIndexMerge.cs::AbstractIndexHandlerProcessorForSuggestIndexMerge].

## AX behavior inventory

Each behavior has a stable ID (`AX-n`) reused by
`docs/indexing/dispositions.md`.

### Routing: who plans a query

**AX-1 Three-way routing per query.**
`QueryRunner.GetRunner` routes by query metadata
[v1:src/TerraBase.Server/Documents/Queries/QueryRunner.cs::QueryRunner.GetRunner]:

- `from index 'Name'` (metadata `IsDynamic == false`) goes to the
  `StaticIndexQueryRunner`; the named index is used directly, whether it
  is static or auto.
- a dynamic collection query that is *not* a pure collection query
  (`IsDynamic && IsCollectionQuery == false`) goes to the
  `DynamicQueryRunner` (the optimizer path).
- a pure collection query (`IsDynamic && IsCollectionQuery`) goes to the
  `CollectionQueryRunner`, which scans documents storage directly and
  creates no index
  [v1:src/TerraBase.Server/Documents/Queries/Dynamic/CollectionQueryRunner.cs::CollectionQueryRunner.ExecuteQuery]
  (streaming index-entries on that route throws `NotSupportedException`
  — "Collection query is handled directly by documents storage so index
  entries aren't created underneath",
  [v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryRunner.cs::DynamicQueryRunner.ExecuteStreamIndexEntriesQuery]).

`IsDynamic` is `Query.From.Index == false`
[v1:src/TerraBase.Server/Documents/Queries/QueryMetadata.cs::QueryMetadata].
A query stays a collection query only while every where-clause is an
`id()` equality (or absent) and there is no order-by/exists/aggregation;
any other operator demotes it (`QueryMetadata.AddWhereField`,
`AddExistField`, order-by handling in `DynamicQueryMapping.Create`).

**AX-2 Plan cache pins the chosen auto index.**
After a dynamic query is planned once, the selected index name is cached
on the query-metadata cache entry
(`metadata.AutoIndexName = indexName`
[v1:src/TerraBase.Server/Documents/Queries/QueryMetadataCache.cs::QueryMetadataCache.MaybeAddToCache];
only cacheable metadata is pinned, `metadata.CanCache`). The next
identical query text short-circuits matching:
`DynamicQueryRunner.MatchIndex` first resolves
`query.Metadata.AutoIndexName` directly and only falls back to full
matching when that index no longer exists
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryRunner.cs::DynamicQueryRunner.MatchIndex].

**AX-3 Optimizer can be disabled.**
`Indexing.DisableQueryOptimizerGeneratedIndexes` (default `false`)
replaces the dynamic runner with `InvalidQueryRunner`
([v1:src/TerraBase.Server/Documents/Queries/QueryRunner.cs::QueryRunner.QueryRunner]),
which throws `NotSupportedException` ("Dynamic queries are not supported
by this database because the configuration '...' is set to true and the
query optimizer needs an index for this query") for every dynamic-query
entry point — execute, stream, index-entries, stream-index-entries,
delete-by-query, patch-by-query, and suggestions
[v1:src/TerraBase.Server/Documents/Queries/InvalidQueryRunner.cs::InvalidQueryRunner].
The explain surface fails the same way when the runner is disabled
([v1:src/TerraBase.Server/Documents/Queries/QueryRunner.cs::QueryRunner.ExplainDynamicIndexSelection]).

### Creation triggers

**AX-4 Create on unmatched dynamic query.**
`DynamicQueryRunner.CreateAutoIndexIfNeeded` loops: build a
`DynamicQueryMapping` from the query, try to match an existing auto index
(`TryMatchExistingIndexToQuery`); on no complete match, create the auto
index through `IndexStore.CreateIndex` with a fresh Raft id
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryRunner.cs::DynamicQueryRunner.CreateAutoIndexIfNeeded].
Every dynamic entry point plans with `createAutoIndexIfNoMatchIsFound:
true` — execute, stream, delete-by-query, patch-by-query, and suggestion
queries can all trigger auto-index creation. Two creation guards exist:

- index-entries queries (`ExecuteIndexEntriesQuery`) pass
  `createAutoIndexIfNoMatchIsFound: false` and throw
  `IndexDoesNotExistException` when nothing matches;
- `query.DisableAutoIndexCreation` (a per-query opt-out) throws
  `InvalidOperationException` when no match exists.

**AX-5 Definition derived from the query shape.**
`DynamicQueryMapping.CreateAutoIndexDefinition` builds the auto-index
definition from query metadata
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryMapping.cs::DynamicQueryMapping.CreateAutoIndexDefinition]:

- map fields = where-clause fields (`Metadata.IndexFieldNames`, minus
  `id()`), plus order-by fields (minus random/score/id), with per-field
  flags derived from the predicates: `search()` sets
  `AutoFieldIndexing.Search`, `exact()` sets `Exact`, `highlight()` sets
  `Highlighting`, `suggest()` sets `HasSuggestions`, and spatial/vector
  predicates attach `AutoSpatialOptions`/`AutoVectorOptions`;
- fields are collected in an ordinal-keyed map, which makes the field set
  canonical for naming;
- `group by` queries produce an `AutoMapReduceIndexDefinition` whose map
  fields carry `Aggregation` operations and whose group-by fields carry
  `GroupByArrayBehavior`; a map field that is neither an aggregation nor
  a group key is rejected (`InvalidQueryException`,
  "Field '...' is neither an aggregation operation nor part of the group
  by key")
  [v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryMapping.cs::DynamicQueryMapping.CreateGroupByFields];
- a `search()` engine type is chosen per query
  (`IndexSearchEngineHelper.GetSearchEngineType`), defaulting to the
  configured `Indexing.AutoIndexingEngineType` (configuration entry
  `Indexing.Auto.SearchEngineType`, default `Corax` set in the
  constructor)
  [v1:src/TerraBase.Server/Config/Categories/IndexingConfiguration.cs::IndexingConfiguration].

**AX-6 Vector-search creation guard.** Before creating an auto index for
a query with `vector.search` on an embeddings-generation-backed field,
the runner validates that the referenced task exists and is enabled;
otherwise the query fails with `InvalidQueryException` and no index is
created
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryRunner.cs::DynamicQueryRunner.CreateAutoIndexIfNeeded]
(`ValidateVectorFields` local function).

**AX-7 Auto indexes are durable, cluster-replicated state.** Creation
goes through the Raft command `PutAutoIndexCommand`, whose
`UpdateDatabaseRecord` stores the definition in
`DatabaseRecord.AutoIndexes`
[v1:src/TerraBase.Server/ServerWide/Commands/Indexes/PutAutoIndexCommand.cs::PutAutoIndexCommand.UpdateDatabaseRecord],
[v1:src/TerraBase.Client/ServerWide/DatabaseRecord.cs::DatabaseRecord.AddIndex].
Through the record path a same-named auto index whose definition differs
in fields is refused (`NotSupportedException`, "Can not update
auto-index"); node-local application re-creates or updates the in-memory
index from that record
[v1:src/TerraBase.Server/Documents/Indexes/IndexStore.cs::IndexStore.HandleChangesForAutoIndexes].
Query results carry the creation Raft index
(`AutoIndexCreationRaftIndex`) so clients can wait for cluster-wide
visibility of a just-created auto index
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryRunner.cs::DynamicQueryRunner.ExecuteQuery].

### Naming

**AX-8 Deterministic `Auto/` names.**
`AutoIndexNameFinder` builds names of the form
`Auto/{Collection}/By{Field1}And{Field2}` with fields sorted by ordinal
and joined with `And`; a field-less map index is
`Auto/{Collection}/ById()` (the `id()` document field name,
first-letter-uppercased, parentheses included); a map-reduce index
appends `ReducedBy{GroupFields}`
[v1:src/TerraBase.Server/Documents/Indexes/AutoIndexNameFinder.cs::AutoIndexNameFinder.FindName].
Test-observed forms include `Auto/Users/ByTerm`,
`Auto/Users/ByAgeAndName`, `Auto/Users/ByCountReducedByLocation`, and
`Auto/Users/ById()`
[v1:test/TerraBase.EndToEndTests/Server/Documents/Queries/Dynamic/Map/CreationOfAutoMapIndexDefinition.cs],
[v1:test/TerraBase.EndToEndTests/Server/Documents/Queries/Dynamic/MapReduce/CreationOfAutoMapReduceIndexDefinition.cs],
[v1:test/TerraBase.SlowTests.Issues/Issues/TerraBase_10464.cs].
Details: the `@all_docs` collection name normalizes to `AllDocs`;
function flags are embedded in field names (`Search`, `Highlighting`,
`Exact`, `Suggestions` markers via `GetName`); quoted field names stay
quoted; spatial/vector markers are uppercased into the field name; names
longer than 256 characters are truncated and suffixed with `...` plus an
`XXHash64` of the full name. The name is therefore the canonical
identity of the (collection, field set, per-field flags) tuple. Sort
direction (ascending/descending) is *not* encoded in the name.

**AX-9 Same name, drifted definition = in-place update; concurrent
replacement retries the plan.** Because the name is deterministic on the
field set and its capability flags, a strictly wider field set produces a
*different name* and flows through supersede (AX-16); what stays under
one name is definition drift that the name does not encode (for example
order-by direction). `IndexStore.GetIndexCreationOptions` classifies
field differences as `IndexCreationOptions.Update`, and
`HandleAutoIndexChange` updates the existing definition in place
[v1:src/TerraBase.Server/Documents/Indexes/IndexStore.cs::IndexStore.GetIndexCreationOptions],
[v1:src/TerraBase.Server/Documents/Indexes/IndexStore.cs::IndexStore.HandleAutoIndexChange].
If the create path returns no live instance (the index was deleted or
replaced concurrently), the planner loop retries matching ("the index
was deleted, we'll try to find a better match (replaced by a wider
index)")
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryRunner.cs::DynamicQueryRunner.CreateAutoIndexIfNeeded].
At execution level, a query whose index is being replaced underneath it
(`ObjectDisposedException`/`OperationCanceledException`) is retried up
to `NumberOfRetries` times after a 500 ms pause
(`WaitForIndexBeingLikelyReplacedDuringQuery`)
[v1:src/TerraBase.Server/Documents/Queries/QueryRunner.cs::QueryRunner.ExecuteQuery].

**AX-10 The `Auto/` namespace is reserved.** A static (explicit) index
whose name starts with `Auto/` is rejected
("Static index name cannot start with 'Auto/'")
[v1:src/TerraBase.Server/Documents/Indexes/IndexStore.cs::IndexStore.IsValidIndexName].
Auto indexes carry dedicated types `IndexType.AutoMap` (1) and
`IndexType.AutoMapReduce` (2) in the persistent client enum
[v1:src/TerraBase.Client/Documents/Indexes/IndexType.cs::IndexType].

### Selection rules (how a query picks an index)

**AX-11 Candidate filters.** `DynamicQueryToIndexMatcher.Match` iterates
`IndexStore.GetIndexesForCollection(collection)` and keeps only indexes
whose search-engine type equals the query's, and whose type is
`AutoMap` (plain dynamic queries) or `AutoMapReduce` (group-by queries)
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryToIndexMatcher.cs::DynamicQueryToIndexMatcher.Match].
Static indexes are never candidates: dynamic queries are served only by
auto indexes, and static indexes require explicit `from index` targeting
(asserted at
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryMapping.cs::DynamicQueryMapping.ExtendMappingBasedOn]
— "Dynamic queries are handled only by auto indexes").

**AX-12 Per-index verdicts.** `ConsiderUsageOfIndex` returns
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryToIndexMatcher.cs::DynamicQueryToIndexMatcher.ConsiderUsageOfIndex]:

- `Failure`: index does not cover the collection or covers more than one
  collection; index state is `Error`/`Disabled` or the index is invalid;
  spatial options differ; a group-by field is missing, has a different
  array-grouping behavior, or the index has extra group-by fields; a map
  field's aggregation differs from the query's
  ([v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryToIndexMatcher.cs::DynamicQueryToIndexMatcher.AssertMapReduceFields]).
- `Partial`: a map field is missing, or exists without a required
  `Search`/`Highlighting`/`Exact`/suggestions capability, or vector
  options differ.
- `Complete`: every query field is covered with sufficient capabilities.
- `CompleteButIdle`: complete, but the index state is `Idle`.

**AX-13 Ranking among complete matches.** In priority order
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryToIndexMatcher.cs::DynamicQueryToIndexMatcher.Match]:

1. higher match type (a complete beats a partial/failure candidate);
2. among two complete matches, the most up-to-date one (highest
   `LastMappedEtag` from `Index.GetLastMappedEtagFor`);
3. at equal etag, the non-idle one (idle is demoted with reason "The
   index is idle. The preference is for active indexes...");
4. otherwise the widest one (`NumberOfMappedFields` — most map fields).

**AX-14 Recorded rejection reasons.** Every skipped candidate is
recorded as an `Explanation(index, reason)` pair with stable reason
strings ("A better match was available", "Wasn't the most up to date
index matching this query", "The index is idle...", "Wasn't the widest
index matching this query", plus per-field failures from AX-12)
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryToIndexMatcher.cs::DynamicQueryToIndexMatcher.Match].

**AX-15 Explain surface.** A query request with `?debug=explain`
returns the selected index name and the full explanation list without
executing the query: handler
[v1:src/TerraBase.Server/Documents/Handlers/Processors/Queries/AbstractQueriesHandlerProcessorForGet.cs],
[v1:src/TerraBase.Server/Documents/Handlers/Processors/Queries/DatabaseQueriesHandlerProcessorForGet.cs::DatabaseQueriesHandlerProcessorForGet.ExplainAsync]
→ `QueryRunner.ExplainDynamicIndexSelection` →
`DynamicQueryRunner.ExplainIndexSelection`. Planning time is separately
observable via the `Optimizer` timing scope around `MatchIndex`
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryRunner.cs::DynamicQueryRunner.ExecuteQuery].

### Merge and supersede

**AX-16 Query-time supersede on partial match.** When the best existing
match is `Partial`, the planner adds that index to
`map.SupersededIndexes`, merges its fields into the mapping
(`DynamicQueryMapping.ExtendMappingBasedOn` — union of fields and
per-field capabilities; group-by fields are never invented), and creates
a wider auto index that covers both the query and the old index's fields
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryRunner.cs::DynamicQueryRunner.TryMatchExistingIndexToQuery],
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryMapping.cs::DynamicQueryMapping.ExtendMappingBasedOn].

**AX-17 Superseded indexes are deleted after catch-up.**
`CleanupSupersededAutoIndexes` waits until the new index's last mapped
etag for the collection reaches the maximum of the superseded indexes'
etags, waits `Indexing.TimeBeforeDeletionOfSupersededAutoIndex`
(default 15 s, "give it a few seconds to drain any pending queries"),
then deletes each superseded index. The cleanup is fire-and-forget with
logged failures; the idle timer is the stated backstop ("they'll also be
cleaned by the idle timer, so we don't worry too much about this being
in memory only operation")
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryRunner.cs::DynamicQueryRunner.CleanupSupersededAutoIndexes].

**AX-18 Background surpass pass.** On idle operations (database
cleanup), `IndexStore.DeleteOrMergeSurpassedAutoIndexes` re-runs the
matcher index-vs-index: an auto index that another same-type index for
the same collection *completely* serves, where the server is at least as
up to date, is deleted ("Deleted index '{name}' because it is
surpassed."); an index only partially served by another is extended by
creating the wider definition and deleting the narrower one, one
extension per pass ("extending only one auto-index at a time"). The pass
is guarded by the database-record etag to avoid redundant work
[v1:src/TerraBase.Server/Documents/Indexes/IndexStore.cs::IndexStore.DeleteOrMergeSurpassedAutoIndexes],
[v1:src/TerraBase.Server/Documents/Indexes/IndexStore.cs::IndexStore.RunIdleOperations].

**AX-19 Static index merging is suggestion-only.** `IndexMerger`
computes merge proposals for explicitly created index definitions (the
studio "merge indexes" suggestion); it is never applied automatically
[v1:src/TerraBase.Server/Documents/Indexes/IndexMerging/IndexMerger.cs::IndexMerger].

### Retirement

**AX-20 Last-queried tracking.** Every query execution marks the index
queried (`LastQueriedTimeTracker.MarkQueried` from
`Index.QueryInternalPreparation`/facet path
[v1:src/TerraBase.Server/Documents/Indexes/Index.cs::Index.QueryInternalPreparation];
the tracker persists elapsed-since-query through indexing threads
[v1:src/TerraBase.Server/Documents/Indexes/LastQueriedTimeTracker.cs::LastQueriedTimeTracker]).

**AX-21 Idle after no queries, delete after idle.** The cluster observer
collects per-index `LastQueried` from all nodes/shards — and only acts
when every node of every shard has reported — takes the smallest
elapsed across the cluster, and issues
[v1:src/TerraBase.Server/ServerWide/Maintenance/ClusterObserver.cs::ClusterObserver.GetUnusedAutoIndexes]:

- `Normal` + unqueried >= `Indexing.TimeToWaitBeforeMarkingAutoIndexAsIdle`
  (default 30 min) → `SetIndexStateCommand(Idle)`;
- `Idle` + unqueried >=
  `Indexing.TimeToWaitBeforeDeletingAutoIndexMarkedAsIdle` (default 72 h)
  → `DeleteIndexCommand`;
- `Idle` + queried again below the idle threshold → back to `Normal`.

Only `Auto/`-prefixed indexes are considered. Every command carries the
measured value and threshold in its reason string.

**AX-22 Querying an idle auto index wakes it.** Before executing, an
index in `Idle` state is set back to `Normal` (failure to do so is
logged and the query proceeds) and marked queried
[v1:src/TerraBase.Server/Documents/Indexes/Index.cs::Index.QueryInternalPreparation].
Idle auto indexes therefore remain correct to select (as
`CompleteButIdle`), just deprioritized (AX-13 rule 3).

### Stale handling

**AX-23 Fresh auto index triggers a bounded staleness wait.** When the
planner creates a new auto index and the query did not set a
staleness-wait timeout, it sets `WaitForNonStaleResultsTimeout` to 15 s
(60 s for streaming queries)
[v1:src/TerraBase.Server/Documents/Queries/Dynamic/DynamicQueryRunner.cs::DynamicQueryRunner.CreateAutoIndexIfNeeded].

**AX-24 Stale results are the default unless non-stale was requested.**
`Index.WillResultBeAcceptable` accepts a stale result unless
`WaitForNonStaleResults` was requested; the wait is bounded by
`WaitForNonStaleResultsTimeout` (client default 15 s matches
`DefaultWaitForNonStaleResultsTimeout`, "this matches default timeout
from client"), after which the result is returned stale. The result
reports `IsStale`, index timestamp, and last query time
[v1:src/TerraBase.Server/Documents/Indexes/Index.cs::Index.WillResultBeAcceptable],
[v1:src/TerraBase.Server/Documents/Indexes/Index.cs::Index.DefaultWaitForNonStaleResultsTimeout].

**AX-25 Sharded dynamic queries tolerate per-shard auto-index
differences.** Each shard may select a different compatible auto index;
differing auto-index names (and local creation Raft indexes) do not mark
the merged sharded result stale — staleness is evaluated against the
index each shard actually selected ("A dynamic sharded query is allowed
to select a different compatible auto-index on each shard")
[v1:src/TerraBase.Server/Documents/Sharding/Operations/Queries/AbstractShardedQueryOperation.cs::ShardedQueryIndexDefinitionCompatibility.ShouldMarkStale].

### Explicit-vs-auto precedence

**AX-26 Explicit targeting bypasses the optimizer entirely.** Precedence
in V1 is structural, not a ranking:

- `from index 'Name'` executes on exactly that index (static or auto);
  no auto-index creation ever happens on that path, and facet queries
  explicitly require a static index ("Facet query must be executed
  against static index.")
  [v1:src/TerraBase.Server/Documents/Queries/QueryRunner.cs::QueryRunner.ExecuteFacetedQuery].
- Dynamic queries consider only auto indexes (AX-11), so an explicitly
  created index is never silently chosen for, or disturbed by, a dynamic
  query; the `Auto/` prefix keeps the two namespaces disjoint (AX-10).
- A user may convert a proven auto index into an explicit one
  (`AutoToStaticIndexConverter`, name `Auto/X/ByY` → sanitized `Index/…`
  name, fields/maps/reduce reconstructed)
  [v1:src/TerraBase.Server/Documents/Handlers/Processors/Indexes/IndexHandlerProcessorForConvertAutoIndex.cs::AutoToStaticIndexConverter],
  after which it is targeted explicitly.
- Auto-index lifecycle operations that would apply to explicit indexes
  are refused: side-by-side reset is not supported for auto indexes
  [v1:src/TerraBase.Server/Documents/Indexes/IndexStore.cs::IndexStore.ResetIndexSideBySideInternal]
  ("Side by side index reset is not supported for auto indexes.").

### Configuration knobs (with V1 defaults)

| Knob | Default | Effect | Citation |
| --- | --- | --- | --- |
| `Indexing.DisableQueryOptimizerGeneratedIndexes` | `false` | replaces the dynamic runner with a throwing runner (AX-3) | [v1:src/TerraBase.Server/Config/Categories/IndexingConfiguration.cs::IndexingConfiguration] |
| `Indexing.Auto.SearchEngineType` (`AutoIndexingEngineType`) | `Corax` | default search engine for new auto indexes (AX-5) | same |
| `Indexing.TimeBeforeDeletionOfSupersededAutoIndexInSec` | `15` | drain wait before deleting superseded indexes (AX-17) | same |
| `Indexing.TimeToWaitBeforeMarkingAutoIndexAsIdleInMin` | `30` | unqueried period that marks an auto index idle (AX-21) | same |
| `Indexing.TimeToWaitBeforeDeletingAutoIndexMarkedAsIdleInHrs` | `72` | idle period after which the auto index is deleted (AX-21) | same |

### Observability summary

- `?debug=explain` returns `{ IndexName, Results: [{Index, Reason}] }`
  without executing (AX-15).
- `QueryTimingsScope.Names.Optimizer` measures planning inside normal
  query timings (AX-15).
- `DocumentQueryResult.AutoIndexCreationRaftIndex` exposes the creation
  Raft index to clients (AX-7).
- SNMP exposes the per-database auto-index count
  [v1:src/TerraBase.Server/Monitoring/Snmp/Objects/Database/5.2.X/5/DatabaseNumberOfAutoIndexes.cs::DatabaseNumberOfAutoIndexes].
- Cluster-observer state commands carry human-readable reasons quoting
  the measured idle values and thresholds (AX-21).

## Test evidence in the pinned checkout

The parity rows for this area cite, among others:

- [v1:test/TerraBase.EndToEndTests/Server/Documents/Queries/Dynamic/Map/CreationOfAutoMapIndexDefinition.cs]
  and
  [v1:test/TerraBase.EndToEndTests/Server/Documents/Queries/Dynamic/Map/BasicDynamicMapQueries.cs]
  (PB-4045);
- [v1:test/TerraBase.EndToEndTests/Server/Documents/Queries/Dynamic/MapReduce/CreationOfAutoMapReduceIndexDefinition.cs]
  and
  [v1:test/TerraBase.EndToEndTests/Server/Documents/Queries/Dynamic/MapReduce/BasicDynamicMapReduceQueries.cs]
  (PB-4046).

## Limits of this extraction

- Sharded orchestration of auto-index creation (auto indexes must be
  created with identical definitions cluster-wide via the database
  record) is described only to the extent the cited sharded-query code
  shows it; the full sharded lifecycle belongs to the SH wave rows.
- The Corax/Lucene writer internals below index selection (AX-2 area 2)
  are out of scope for the planner design.
- This document makes no performance claim; selection-quality
  measurement obligations are set by PD-2026-08-26-09 D3 and
  PD-2026-08-26-03.
