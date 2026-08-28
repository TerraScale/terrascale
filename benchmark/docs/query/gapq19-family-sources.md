# GAP-Q19: family query/index sources — counters and time-series (folds F3)

Status: decision record for Forgejo ticket TerraBaseV2#70 (GAP-Q19), recorded
2026-08-28 against the `fleet/gapq19-2` worktree of `develop` @ `d2ba76f`.

Parity anchor: the fold-3 gap "counters and time-series are not query
sources or index-definition inputs" — V1 answers both halves with a typed
source-type vocabulary (`IndexSourceType`), per-family map inputs
(`CounterEntry`, `TimeSeriesSegment`), and the time-series query surface.
This packet builds the **engine half** of both: family sources in the typed
query seam (counter and time-series sources, per-family row semantics,
F04-seam paging, include/read parity) and family map sources in the typed
static index-definition language (GAP-Q06). The TQL/V1-grammar fold, the
public TBEP/host surface, and index **serving** over family sources stay
with their owning waves (D7).

V1 evidence identity: `fa99b9257776dbe625e6a2388c74c9ce916637ee` under
`/home/mariogk/Projects/TerraScale/.worktrees/terrabase-v1-fa99b925-readonly`
(read-only). Every V1 file reference below is inside that pinned tree, cited
by exact file and line.

Test evidence: `src/query/family_sources_tests.rs` (the in-crate contract
proofs) and `tests/query/family_sources_contract.rs` (public construction
vocabulary). `TenantScope` has no public constructor by design, so the
execution proofs run in-crate, following the convention the engine, query,
includes, and bulk seams already record.

Disposition vocabulary: **closed-here**, **pinned-compatible**, **defer**,
**accepted-difference** — used exactly as the neighboring records use them.

## D1 — V1's source-type vocabulary, mapped from the pinned tree

V1 names the source of an index with one enum, and the two family kinds are
first-class members beside documents:

- `IndexSourceType` (`src/TerraBase.Client/Documents/Indexes/IndexSourceType.cs:3-8`):
  `None = 0`, `Documents = 1`, `TimeSeries = 2`, `Counters = 3`.
- The definition types carry the source type structurally:
  `CountersIndexDefinition.SourceType => IndexSourceType.Counters`
  (`src/TerraBase.Client/Documents/Indexes/Counters/CountersIndexDefinition.cs:3-6`)
  and `TimeSeriesIndexDefinition.SourceType => IndexSourceType.TimeSeries`
  (`src/TerraBase.Client/Documents/Indexes/TimeSeries/TimeSeriesIndexDefinition.cs:3-6`).
- The map-source spelling — the text a map stage starts from:
  `IndexDefinitionHelper.GetQuerySource`
  (`src/TerraBase.Client/Documents/Indexes/IndexDefinitionHelper.cs:215-233`)
  renders `"docs"` for documents, `"counters"`, and `"timeSeries"`, with the
  collection appended (`counters.Users`) when the map is typed to a
  collection. The counters builder spells the name-restricted form
  `"counters"` / `"counters.{name}"` / `counters[@"{name}"]`
  (`src/TerraBase.Client/Documents/Indexes/Counters/CountersIndexDefinitionBuilder.cs:76-90`),
  and the time-series builder the mirror forms `"timeSeries"` /
  `"timeSeries.{name}"` / `timeSeries[@"{name}"]`
  (`src/TerraBase.Client/Documents/Indexes/TimeSeries/TimeSeriesIndexDefinitionBuilder.cs:76-90`).
- Detection is by that same prefix
  (`src/TerraBase.Client/Documents/Indexes/IndexDefinitionHelper.cs:252-277`):
  a map starting `timeSeries…` is a time-series index, `counters…` a
  counters index, anything else falls back to documents — including the
  `from ts in timeSeries.Users.HeartRate` query-syntax form at `:268-276`.

## D2 — per-family source semantics in V1 (the cite sweep)

**Counters as an index source.** The counters map enumerates *entries*, not
documents: the server's `MapCountersIndex.GetMapEnumerator` wraps the item
stream in `StaticIndexItemEnumerator<DynamicCounterEntry>` with a
`CounterItemFilterBehavior` that dedups repeated item ids
(`src/TerraBase.Server/Documents/Indexes/Static/Counters/MapCountersIndex.cs:197-199`;
the filter at
`src/TerraBase.Server/Documents/Indexes/Static/Counters/CounterItemFilterBehavior.cs:20-33`).
The mapped value — the per-entry input space — is exactly three fields:
`DynamicCounterEntry` exposes `DocumentId`, `Name`, and `Value`
(`src/TerraBase.Server/Documents/Indexes/Static/Counters/DynamicCounterEntry.cs:46-52`),
mirrored by the client-side LINQ input type `CounterEntry { DocumentId,
Name, Value }`
(`src/TerraBase.Client/Documents/Indexes/Counters/CounterEntry.cs:3-17`).
A counters index may also reduce: the server has `MapReduceCountersIndex`
(`src/TerraBase.Server/Documents/Indexes/Static/Counters/MapReduceCountersIndex.cs`),
so reduce over family sources is pinned, not an extension.

**Time-series as an index source.** The time-series map enumerates
*segments*: the builder's map expression is over
`IEnumerable<TimeSeriesSegment>`
(`src/TerraBase.Client/Documents/Indexes/TimeSeries/TimeSeriesIndexDefinitionBuilder.cs:23,53`),
and the segment is the summarized unit — `DocumentId`, `Name`, `Min`,
`Max`, `Sum`, `Count`, `Start`, `End`, `Entries`
(`src/TerraBase.Client/Documents/Indexes/TimeSeries/TimeSeriesSegment.cs:5-33`).
The server counterparts are `MapTimeSeriesIndex` and
`MapReduceTimeSeriesIndex`
(`src/TerraBase.Server/Documents/Indexes/Static/TimeSeries/`), with Corax and
Lucene converters that share the counters/time-series item shape
(`CountersAndTimeSeriesJintCoraxDocumentConverter.cs`,
`CountersAndTimeSeriesJintLuceneDocumentConverter.cs` under
`src/TerraBase.Server/Documents/Indexes/Persistence/`).

**Time-series as a query source.** V1's time-series query is a typed
sub-language inside the document-query surface: `declare timeseries fn()`
parses `from <source>` with `BETWEEN`, `FIRST`, and `LAST` windows — the
three window forms are mutually exclusive, checked pairwise — then `LOAD`,
`GROUP BY`, and `SELECT` with aggregations
(`src/TerraBase.Server/Documents/Queries/Parser/QueryParser.cs:397-470`).
The stream unit is the entry: `TimeSeriesEntry { Timestamp, Values, Tag }`
(`src/TerraBase.Client/Documents/Session/TimeSeries/TimeSeriesEntry.cs:11-45`),
with `Values` as the numeric vector and `Tag` the optional label — the same
entry shape V2's family seam already carries (GAP-F04).

**Counters as a query source.** The pinned tree has no `from counters` raw
query: the counters query surface is the typed per-document read
(`GetCountersOperation`, `src/TerraBase.Client/Documents/Operations/Counters/GetCountersOperation.cs:15-80`,
including the "all counters of a document" empty-name form at `:66-73`),
the includes fold (GAP-Q07, already shipped), and the index map input above.
V2's counter **query** source therefore inherits its row semantics from the
map input space — `(DocumentId, Name, Value)` — which is the same space the
read seam serves. **accepted-difference** in spelling only: V2 serves the
entry space as a typed query source where V1 serves it as an index map
input; the entry fields are pinned, the surface is new.

## D3 — the V2 source-type vocabulary (decision)

`QuerySource` becomes a closed three-kind value, constructed only through
validated builders (the seam's existing discipline):

- `QuerySource::try_new(CollectionName)` — documents of one user collection,
  exactly as before, still rejecting the reserved family collection.
- `QuerySource::counters()` — every live counter entry of the tenant (V1
  `counters`).
- `QuerySource::counters_named(name)` — one counter name across every
  document (V1 `counters.{name}`); the name is validated by the shared
  field-name rule.
- `QuerySource::time_series()` — every retained entry of every series (V1
  `timeSeries`).
- `QuerySource::time_series_named(name)` — one series name across every
  document (V1 `timeSeries.{name}`); validated the same way, and the series
  name rules already reject the reserved-record separator `/`
  (`src/families/payloads.rs`, `validate_series_name`).

`QuerySource::kind()` discloses the kind (`Documents | Counters |
TimeSeries`); `collection()` returns `Option<&CollectionName>` now (`None`
for family kinds). Alternatives rejected: exposing a public enum of variants
(breaks the one-validated-constructor rule — a variant would bypass name
validation); a separate `FamilyQuerySpec` type (forks the compile seam and
the one-plan contract for no benefit).

**Index definitions.** `MapSource` in the GAP-Q06 language gains the two
family kinds with the V1 spellings: `MapSource::Counters { name }` and
`MapSource::TimeSeries { name }` (`None` = the all-entries form). New rule
`SourceKindMismatch`: one definition's map stages draw from document sources
(collections / all-documents) or from one family kind — never a mix, and
never both family kinds. V1 anchors: the definition types are separate
(`CountersIndexDefinition.cs`, `TimeSeriesIndexDefinition.cs` are distinct
classes), and the all-documents rule already refuses document-side mixing
(`AllDocumentsWithCollections`). Duplicate family sources are refused by the
existing whole-value `DuplicateMapSource` check. Identity rendering spells
`counters`, `counters.{name}`, `timeSeries`, `timeSeries.{name}` — the V1
map-source grammar — so definition identity is stable across the fold.

## D4 — per-family query semantics (what each source returns)

**Counter source.** Rows are live counter entries: one row per
`(document, name)` with a present value. Enumeration walks the snapshot's
user documents in canonical key order and reads each counter group through
the family seam's own reader — the same `FamilyReadRequest::CounterGroup`
the includes contract uses — so a query row, an include entry, and a direct
family read never disagree (D6). A counter that does not exist or is
tombstoned contributes no row (the read seam's `live_names` rule). The
predicate, ordering, and projection field space is the V1 entry space:
`document` (string), `name` (string), `value` (integer). Evaluation reuses
the seam's own condition/ordering evaluators over that field map — one
evaluation path, no family-specific drift. `matched`, `scanned`, and
`skipped` count entries (the FILTER accounting parity the document path
records), and `QueryPaging` pages entry rows exactly as it pages document
rows.

**Time-series source.** Rows are retained series entries: one row per
`(document, name, timestamp)` with `values` and `tag` — V1's
`TimeSeriesEntry` field space. Enumeration discovers series from the
snapshot's reserved time-series record keys
(`family:time-series/<document>/<name>`; series names reject `/`, so the
split is unambiguous) and reads each through `FamilyReadRequest::
TimeSeriesRange`. **Paging rides the F04 seam verbatim:** the spec binds an
optional range and an optional `TimeSeriesPaging` (cursor `after`, `limit`)
through `with_time_series_window`, each series read serves its bounded,
cursor-stable page (`TimeSeriesPage`, total before paging, `next_cursor`
exactly when more matched entries follow), and the result reports one
`SeriesPageCursor` per read series so pages chain through the same cursor
contract a direct family read chains through. The query's own `QueryPaging`
then pages the assembled entry rows (uniform with the counter source);
`scanned`/`skipped` count entries.

Both family channels live on the result beside the document rows:
`QueryResult::family_rows()` (typed `CounterQueryRow` / `TimeSeriesQueryRow`
values, never synthetic documents — family state is not a document and must
not leak through the document channel) and
`QueryResult::series_cursors()`. A document-source query carries an empty
family channel; a family-source query carries empty document rows.

## D5 — what family sources may not do (fail-closed rules)

- A family source with a non-empty staleness policy fails compile with
  `InvalidSpec`: family queries are served index-less (the tracer base
  path); index namespaces serve document collections today.
- A family source with loads or includes fails construction: both clauses
  walk result-row document paths, which family rows do not have.
  (`with_loads`/`with_includes` reject; the grammar cannot produce them.)
- The optimizer pass is skipped for family sources: auto-indexes are keyed
  by collection demand, and V1's auto-index surface is document-only. No
  auto index is created or selected for a family query.
- Bulk-by-query (patch/delete) refuses a family source with its typed
  refusal: V1's bulk mutations operate on documents; counter and series
  mutations go through their family batches.
- The sharded fan-out refuses a family source with its typed error: shard
  routing and the merge path are keyed by document rows and collection
  shard keys. Rider for the SH wave (D8).

## D6 — include/read parity that comes with source status

The family-source execution reads through `FamilyStore::read` against the
query's own snapshot — the identical helper the includes contract uses —
so: (a) a counter query row's value equals the counter include's value and
the direct `CounterGroup` read; (b) a time-series query row equals the
time-series include's page entries and the direct `TimeSeriesRange` read on
the same snapshot; (c) document-source queries with counter/time-series
includes are unchanged (the include path never touches the source kind).
Tests assert all three equalities on one engine, one snapshot.

## D7 — deliberately not built here

- **TQL / V1-grammar source spellings.** `from counters` / `from ts` text
  forms and the `declare timeseries()` sub-language lower through the TQL
  wave's parser; this packet lands the typed seam they will lower onto.
  **defer** (TQL wave).
- **TBEP + host + studio surface.** The C# host family and the SDKs speak
  the new sources only after a protocol fold. **defer** (SE/host wave).
- **Index serving over family sources.** The definition language accepts
  family map sources (authoring, identity, drift, reduce shape); the
  in-memory catalog's serving shape stays document-collection keyed, so no
  family-sourced index serves a query yet. **defer** (IX-04).
- **Time-series aggregation folds** (`group by` / `select min…` over
  windows) and rollup *queries* — GAP-F04 owns rollup generation; querying
  a rollup section stays on the family read seam. **defer**.
- Parity overlay records (`parity/overlays/**`) — outside this packet's
  write scope; the WS-PARITY proposals are recorded in D9.

## D8 — limits and non-claims

- No performance claim: family queries are bounded snapshot scans through
  the family read seam (one group read per document, one page per series),
  with the seam's checkpoint bounds. v2-faster status: **not claimed**.
- Family rows are bounded by the F04 page limit per series (1024) and the
  query page limit; a family-source query with no window reads each
  series' whole retained view (the family seam's own retention bound).
- The counter `value` channel is `Option<i64>` on the family seam; query
  rows exist only for present values (D4), so no row carries a null value.
- Multi-map family indexes: one family kind per definition (D3); mixing
  counters and time-series maps is refused, matching V1's separate
  definition types. V1's JavaScript indexes that mix sources in one body
  are out of the typed language by the no-arbitrary-code rule.

## D9 — WS-PARITY proposals

- `PB-4014`-family rows (counters as query/index surface): propose
  **implemented** for the engine typed seam (query source + index map
  source + read parity); public-surface rows stay with the host fold.
- Time-series query/index surface: propose **implemented** for the engine
  typed seam (query source with F04 paging + index map source); the
  aggregation sub-language rows stay **unresolved** (defer, D7).

## RED / GREEN evidence

Recorded below as the runs happened; commands and exit codes verbatim.

### RED (before implementation)

The contract tests were written first and observed RED (unresolved-item
compile failure, the exact shapes below), then GREEN against the
implementation. Behavior-testable contract rows and their tests:

| Contract row | Test |
| --- | --- |
| Counter source returns one row per live entry, `(document, name, value)` | `counter_source_returns_entry_rows_across_documents` |
| `counters.Named` restricts to one name across documents (V1 `counters.{name}`) | `counter_source_named_restricts_to_one_name` |
| Predicate/ordering evaluate over the V1 entry field space; FILTER accounting | `counter_source_predicate_and_ordering_evaluate_over_entry_fields` |
| `QueryPaging` pages entry rows, `matched` stays the whole set | `counter_source_pages_entry_rows` |
| Query row == counter include == direct `CounterGroup` read | `counter_source_values_match_include_and_direct_read` |
| Series source returns entry rows `(document, name, timestamp, values, tag)` with one cursor per read series | `time_series_source_returns_entry_rows_with_series_cursors` |
| Unnamed series source enumerates every series (V1 `timeSeries`) | `time_series_source_enumerates_every_series_when_unnamed` |
| Paging chains through the F04 seam cursor, each entry exactly once | `time_series_source_pages_through_the_f04_seam` |
| The range window bounds the read | `time_series_source_range_window_bounds_the_read` |
| Index-naming staleness policy on a family source fails compile | `family_source_refuses_index_staleness_policy` |
| Loads/includes/missplaced-window/field-projection refusals | `family_source_refuses_loads_includes_window_and_projection` |
| Document queries keep document rows and an empty family channel | `document_queries_keep_document_rows_and_empty_family_channel` |
| Source constructor vocabulary and validation (public) | `source_constructors_validate_names`, `family_query_source_constructors_are_public_vocabulary` |
| Family specs compile through the public seam; window placement (public) | `family_source_specs_compile_through_the_public_seam` |
| Index definitions accept family map sources, identity renders V1 spellings (public) | `index_definitions_accept_family_map_sources` |
| Index definitions refuse mixed kinds, overlapping all+named, duplicates (public) | `index_definitions_refuse_mixed_and_overlapping_family_sources` |

RED evidence (observed before the implementation existed, exact compiler
output):

- `env -u RUSTUP_TOOLCHAIN cargo test --lib --offline --locked query::family_sources`
  — `error[E0432]: unresolved imports super::{CounterQueryRow, FamilyQueryRow,
  QuerySourceKind, SeriesPageCursor, TimeSeriesPaging, TimeSeriesQueryRow,
  TimeSeriesRange}` (`src/query/family_sources_tests.rs:24-31`) plus
  `error[E0599]: no associated function or constant named counters /
  counters_named / time_series / time_series_named / kind found for struct
  QuerySource` and ``&CollectionName` is not an iterator`` on the
  Option-returning `collection()` accessor — 43 errors total,
  `could not compile terrabase-v2 (lib test)`.
- `env -u RUSTUP_TOOLCHAIN cargo test --test query --offline --locked family_sources`
  — `error[E0432]: unresolved import terrabase_v2::query::QuerySourceKind`,
  the same `QuerySource` constructor `E0599` set, `no method named
  with_time_series_window found for struct QuerySpec`, and `no variant
  named Counters / TimeSeries found for enum MapSource` —
  `could not compile terrabase-v2 (test "query")`.

### GREEN (after implementation)

- `env -u RUSTUP_TOOLCHAIN cargo test --lib --offline --locked query::family_sources`
  — `test result: ok. 13 passed; 0 failed` (all thirteen in-crate contract
  rows of the table above).
- `env -u RUSTUP_TOOLCHAIN cargo test --test query --offline --locked` —
  `test result: ok. 60 passed; 0 failed` (the five public
  `family_sources_contract` rows beside the fifty-five existing public
  query rows).
- `env -u RUSTUP_TOOLCHAIN cargo test --lib --offline --locked` —
  `test result: ok. 883 passed; 0 failed; 1 ignored` (whole in-crate
  suite; the documents path, includes, bulk, optimizer, and sharding
  suites are unchanged green beside the new source kinds).

Two implementation rules the GREEN run surfaced and the tests now pin:

- The family-source window travels boxed inside the specification and the
  family rows and cursors travel behind one boxed channel on the result:
  both values ride by-value enums (`QueryInput`, the operation responses),
  and the pedantic large-variant gate is part of the packet's clippy
  receipt — the boxing keeps the seam's frozen shapes under it.
- A counter name with interior spaces is a legal name under the shared
  name rule (no padding, no control characters, non-empty); the RED test
  row that expected otherwise was corrected to the padded-name refusal,
  which is the rule the family seam's own counter names follow.

### Receipts (from the `fleet/gapq19-2` worktree root, exit codes verbatim)

1. `cargo fmt --all -- --check` — exit 0.
2. `env -u RUSTUP_TOOLCHAIN cargo +1.97.1 clippy --offline --locked
   --all-targets -- -D warnings` — exit 0 (`Finished \`dev\` profile …`).
3. `timeout 5400 env -u RUSTUP_TOOLCHAIN cargo test --workspace --offline
   --locked` — exit 0 (dev profile; doc-tests `14 passed`, every target
   `test result: ok`). The wall-clock row
   `streaming_publication_sink_never_blocks_a_commit_behind_a_wedged_poll`
   did not fail in this run — no isolated rerun was needed.
4. `timeout 5400 env -u RUSTUP_TOOLCHAIN
   TERRABASE_V1_ROOT=/home/mariogk/Projects/TerraScale/.worktrees/terrabase-v1-fa99b925-readonly
   TERRASCALE_WORKSPACE_ROOT=/home/mariogk/Projects/TerraScale bash
   scripts/check.sh` — exit 0, `TerraBaseV2 foundation checks passed`,
   `STRICT V1 QUALIFICATION PASSED` (258 rows, 267 source citations, 346
   test citations, 1 adjudicated gap, 0 unresolved). `cargo-deny
   unavailable: dependency/license/advisory check recorded as unrun` —
   the script's own host note, not a packet failure.
