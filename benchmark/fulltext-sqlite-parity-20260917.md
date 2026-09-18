# TerraBase full-text search vs SQLite FTS5 — embedded parity comparison

Status: **complete** — receipt `bench/receipts/fulltext-sqlite-parity-20260917/run.log`.
Same lane discipline as [engine-sqlite-parity-20260917.md](engine-sqlite-parity-20260917.md):
one process, taskset cores 0–3, `nice -n 19`, `DOTNET_PROCESSOR_COUNT=4`, .NET 10,
Release, workstation GC. The `terrabase` lane drives `DurableFullTextIndex`
(the durable scoped full-text index over the `StorageEngine` change feed)
directly in-process; the `sqlite` lane drives `Microsoft.Data.Sqlite` with an
`fts5(text)` virtual table, WAL journal, `synchronous=NORMAL`. Per-sample
pairing interleaves the terrabase and sqlite measurement of every lane.

Corpus: 2,000 seeded documents, 12 tokens per document drawn from a 501-word
letter-only vocabulary (`wordaaa`, `wordaab`, …), seed `1337`, five samples,
100 warm-up and 200 measured operations for search lanes. **Every search lane
verifies equal hit counts between the engines before measuring** (the run
fails on mismatch): single 43/43, AND 1/1, OR 86/86, NOT 42/42.

## Results (medians of per-sample medians)

| Lane | Hits | terrabase p50 µs | sqlite p50 µs | terrabase/sqlite | terrabase alloc B/op | sqlite alloc B/op |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| single (`wordaea`) | 43 | 69.5 | 144.4 | **0.48×** | 5,812 | 10,329 |
| and (`wordaea wordafl`) | 1 | 20.6 | 49.1 | **0.42×** | 4,996 | 1,257 |
| or (`wordaea OR wordafl`) | 86 | 132.0 | 442.0 | **0.30×** | 13,316 | 19,617 |
| not (`wordaea NOT wordafl`) | 42 | 148.0 | 263.0 | **0.56×** | 58,983 | 10,113 |
| rebuild (full backfill) | 2,000 docs | 90,835.6 | 24,903.8 | 3.65× | 7,470,924 | 598,628 |
| write (catch-up of 32 publications) | 32 docs | 17,638.3 | 440.6 | 40.03× | 1,377,619 | 9,808 |

- **Every search shape beats SQLite FTS5**: single-term 2.1×, two-term AND
  2.4×, OR 3.3×, NOT 1.8× (p50). The previous implementation measured
  ~6,600 µs for a two-token query over 1,000 records through the ZoneTree
  seek-based engine ([fulltext-search-performance.md](fulltext-search-performance.md));
  the durable path is now tens of microseconds at twice the corpus.
- **Rebuild and write remain checkpoint-bound by design** (the honest gaps):
  every visibility commit serializes and write-through-persists the complete
  index state JSON, so both lanes pay O(corpus) per commit — 2000 documents
  ≈ 1.4 MB serialized per catch-up batch in this run. SQLite FTS5 persists
  only the incremental index delta per transaction. The upgrade path is an
  incremental state log or a lagging checkpoint (a lagged cursor is safely
  recoverable today: reopen replays retained publications after the persisted
  cursor and falls back to a snapshot rebuild on a retained-feed gap); add it
  when write-path parity matters more than restart simplicity.

## What changed in TerraBase

- **`FullTextTokenIndex`** (new, `src/TerraBase/Indexing/FullText/`): the
  in-memory inverted index — token hash → set of document ids — with boolean
  evaluation (AND/OR/NOT/IN, parentheses) over the existing
  `ZoneTree.FullTextSearch` query parser tree. Postings hold document ids
  directly, so a query is dictionary lookups plus set algebra; the result is
  ordered by document id and paginated (the pinned deterministic-order
  contract).
- **`DurableFullTextIndex` no longer uses `HashedSearchEngine`**: the
  ZoneTree search engine (three ZoneTrees, block cache, staging build,
  active/previous directory swap) is deleted from the durable path. Commits
  are now `CommitRebuild` / `CommitDelta` / `CommitCursor`: token deltas are
  computed before any mutation, the state file is persisted under the gate,
  and the in-memory index swaps in only after the durable write succeeds. A
  failed tokenization or persist leaves the searchable index and watermark at
  the last durable position (the injected-failure contract test pins this).
- **Analyzer identity without reflection**: `WordTokenizer`,
  `DefaultHashCodeGenerator`, `NormalizableHashCodeGenerator`, and
  `DiacriticNormalizer` now implement
  `ZoneTree.FullTextSearch.IFullTextConfigurationIdentity` (moved from
  TerraBase), each producing a stable canonical identity string from its own
  configuration. The 440-line reflection-based fingerprint in
  `DurableFullTextIndex` is now ~40 lines of interface dispatch with the same
  fail-closed reopen behavior (changed minimum token length, stop words, or
  diacritic map still reject the open).
- **State layout v3**: persisted state drops the synthetic record-id
  allocation (`RecordId`/`NextRecordId`) since postings address documents by
  id. Legacy ZoneTree `active`/`previous` directories are removed on open;
  older state files (v2 or earlier analyzer identities) fail closed with the
  existing rebuild-required error — recreate the index.
- Query semantics are unchanged: exact tokens, `AND`/`OR`/`NOT`/`IN`,
  parentheses; phrases raise `FullTextPhraseQueryException`. The one
  behavioral edge: a query term that re-slices into multiple tokens (for
  example a hyphenated word) is evaluated as an unordered AND of its tokens
  instead of the previous token-order chain; the durable contract never
  exposed token-order queries.

## Reproduction

```
nice -n 10 dotnet build bench/TerraBase.FullTextBenchmarks/TerraBase.FullTextBenchmarks.csproj \
  --configuration Release --nologo \
  -p:EnableNETAnalyzers=true -p:Nullable=enable \
  -p:TreatWarningsAsErrors=true -p:AnalysisLevel=latest-recommended

taskset -c 0-3 nice -n 19 env DOTNET_PROCESSOR_COUNT=4 \
  dotnet bench/TerraBase.FullTextBenchmarks/bin/Release/net10.0/TerraBase.FullTextBenchmarks.dll
```

Tests after the change: TerraBase.Api.Tests 1068/1068 (includes the five
durable full-text contract suites), TerraBase.ZoneTree.FullTextSearch.Tests
73/73, TerraBase.Tests 447/447 (4 skipped). One added query-language contract
test (`FullTextRuntimeTests.SearchSupportsKeywordListsParenthesesAndEmptyQueries`,
covering `IN [...]` keyword lists, parentheses, and empty queries) is verified
through an equivalent scratch harness against the same assemblies and will
run with the suite once an unrelated in-flight reactive-delivery change in
the working tree compiles again. The benchmark executable records SHA-256
identities for the benchmark, TerraBase, TerraBase.ZoneTree, and
Microsoft.Data.Sqlite assemblies in the receipt.
