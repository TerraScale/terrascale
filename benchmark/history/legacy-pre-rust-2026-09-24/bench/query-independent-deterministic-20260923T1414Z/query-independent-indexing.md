# Query-independent indexing comparison

Generated: 2026-09-23T14:14:45.1889635+00:00; documents: 2000 + 100 seeded maintenance documents; post-build updates: 100; repeat samples: 30; evaluation queries: `status = s17` and `profile.segment = g23`; result limit: 100.
Initial seed and maintenance updates use 100-document transactions in both TerraBase and SQLite; the final seed batch may contain fewer documents when the requested document count is not divisible by the batch size.
SQLite no-index and explicit-index lanes use separately seeded equivalent databases; each lane applies its own post-build maintenance batch from the same initial document state.
The evaluation query object and held-out expected IDs are materialized only after candidate selection, advisor decision, and lifecycle application in indexed TerraBase lanes.
Declared resource budget: MaximumIndexes=2; transaction batch=100; query scan budget=100000; query work budget=200000; query memory budget=67108864 bytes; SQLite page cache=65536 KiB; SQLite mmap=67108864 bytes.
Equal secondary-index budget: TerraBase policy MaximumIndexes=2 with no evaluation-field hint; SQLite tuned explicit lane creates exactly 2 expression index(es) in deterministic shape order over JSONB paths. SQLite uses WAL with synchronous=FULL, temp_store=MEMORY, and the declared cache/mmap settings; its table primary key remains present in both lanes.
SQLite stores the same document shape as TerraBase in a JSONB body and queries it with json_extract; only the relational primary key is separate. Candidate generation receives no query history or preferred paths. Declared cache, mmap, and query budgets are reproducibility settings, not hard process-wide CPU, memory, I/O, or storage caps.

| Shape | Strategy | Index budget | Active indexes | First query µs (cold lane) | First query CPU ms | First query RSS Δ | First query alloc B | Repeat p50 µs | Repeat p95 µs | First rows | Repeat rows | Access | First index served | Repeat index served | Result coverage | Query history before candidates | Initial ingest ms | Profile ms | Build ms | Index build phase detail | Inference ms | Maintenance ms | Catch-up + index probe phases | Maintenance storage Δ | Storage after writes | Storage before index | Final storage | Index storage Δ | Advice |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | ---: | --- | ---: | ---: | ---: | --- | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | --- |
| status | No secondary index | 0 secondary | 0 | 126202.0 | 122.2 | 794624 | 4153936 | 59029.2 | 110711.6 | 2100.0 | 2100.0 | TerraBase source scan | False | 0 % | True | — | 792.7 | 0.0 | 0.0 |  | 0.0 | 27.1 |  | 0 | 67131990 | 67131991 | 67131991 | 0 | not-run |
| nested profile.segment | No secondary index | 0 secondary | 0 | 78503.2 | 68.3 | 12288 | 5050744 | 13762.5 | 55258.3 | 2100.0 | 2100.0 | TerraBase source scan | False | 0 % | True | — | 792.7 | 0.0 | 0.0 |  | 0.0 | 27.1 |  | 0 | 67131990 | 67131991 | 67131991 | 0 | not-run |
| status | Deterministic schema statistics | 2 secondary | 2 | 837322.4 | 639.4 | 1159168 | 3869984 | 41.6 | 66.4 | 42.0 | 42.0 | TerraBase automatic index | True | 100 % | True | 0 | 171.7 | 179.3 | 3893.6 | — | 1.7 | 15.9 | — | 0 | 67131990 | 67131991 | 67796284 | 664293 | schema-statistics:query-independent-coverage-hypothesis |
| nested profile.segment | Deterministic schema statistics | 2 secondary | 2 | 1726.2 | 1.7 | 0 | 98400 | 40.9 | 56.8 | 42.0 | 42.0 | TerraBase automatic index | True | 100 % | True | 0 | 171.7 | 179.3 | 3893.6 | — | 1.7 | 15.9 | — | 0 | 67131990 | 67131991 | 67796284 | 664293 | schema-statistics:query-independent-coverage-hypothesis |
| status | SQLite tuned no secondary index | 0 secondary | 0 | 3393.0 | 3.4 | 0 | 32800 | 773.1 | 845.0 | — | — | SQLite table scan (EXPLAIN QUERY PLAN) | False | 0 % | True | — | 56.3 | 0.0 | 0.0 |  | 0.0 | 1.8 | not-applicable | 12360 | 696096 | 696096 | 708456 | 0 | comparator |
| nested profile.segment | SQLite tuned no secondary index | 0 secondary | 0 | 1742.5 | 1.7 | 0 | 32800 | 895.6 | 965.9 | — | — | SQLite table scan (EXPLAIN QUERY PLAN) | False | 0 % | True | — | 56.3 | 0.0 | 0.0 |  | 0.0 | 1.8 | not-applicable | 12360 | 696096 | 696096 | 708456 | 0 | comparator |
| status | SQLite tuned explicit JSONB indexes | 2 secondary | 2 | 954.3 | 0.9 | 0 | 24600 | 87.1 | 117.1 | — | — | SQLite ix_users_status (EXPLAIN QUERY PLAN) | True | 100 % | True | — | 39.7 | 0.0 | 5.1 | explicit indexes aggregate (2): 5.1 ms | 0.0 | 2.4 | not-applicable | 61800 | 696096 | 696096 | 823816 | 65920 | comparator |
| nested profile.segment | SQLite tuned explicit JSONB indexes | 2 secondary | 2 | 788.2 | 0.8 | 0 | 24600 | 85.5 | 105.8 | — | — | SQLite ix_users_profile_segment (EXPLAIN QUERY PLAN) | True | 100 % | True | — | 39.7 | 0.0 | 5.1 | explicit indexes aggregate (2): 5.1 ms | 0.0 | 2.4 | not-applicable | 61800 | 696096 | 696096 | 823816 | 65920 | comparator |

## Regressions versus the same-engine no-index baseline

Positive latency or maintenance values mean slower than that engine's no-secondary-index lane. Storage is the final logical file-size delta in bytes. TerraBase diagnostic rows omit first-query deltas because their first query follows an index probe; SQLite first queries remain cold even when `--index-perf` is supplied.

| Shape | Strategy | Baseline | First query Δ | Repeat p50 Δ | Maintenance Δ | Final storage Δ |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| status | No secondary index | No secondary index | 0.0% | 0.0% | 0.0% | 0 B |
| nested profile.segment | No secondary index | No secondary index | 0.0% | 0.0% | 0.0% | 0 B |
| status | Deterministic schema statistics | No secondary index | +563.5% | -99.9% | -41.3% | +664,293 B |
| nested profile.segment | Deterministic schema statistics | No secondary index | -97.8% | -99.7% | -41.3% | +664,293 B |
| status | SQLite tuned no secondary index | SQLite tuned no secondary index | 0.0% | 0.0% | 0.0% | 0 B |
| nested profile.segment | SQLite tuned no secondary index | SQLite tuned no secondary index | 0.0% | 0.0% | 0.0% | 0 B |
| status | SQLite tuned explicit JSONB indexes | SQLite tuned no secondary index | -71.9% | -88.7% | +30.6% | +115,360 B |
| nested profile.segment | SQLite tuned explicit JSONB indexes | SQLite tuned no secondary index | -54.8% | -90.5% | +30.6% | +115,360 B |

SQLite status no-index EXPLAIN QUERY PLAN: `SCAN users USING INDEX sqlite_autoindex_users_1`
SQLite status explicit-index EXPLAIN QUERY PLAN: `SEARCH users USING INDEX ix_users_status (<expr>=?); USE TEMP B-TREE FOR ORDER BY`
SQLite nested profile.segment no-index EXPLAIN QUERY PLAN: `SCAN users USING INDEX sqlite_autoindex_users_1`
SQLite nested profile.segment explicit-index EXPLAIN QUERY PLAN: `SEARCH users USING INDEX ix_users_profile_segment (<expr>=?); USE TEMP B-TREE FOR ORDER BY`
Timing semantics: `First query µs` is the one first evaluation call in that lane, not a percentile or minimum. Repeat percentiles are computed from the following calls; the first call is excluded from repeat statistics.
The deterministic and Jev lanes assert `AutomaticWorkloadAnalyzer.ObservationCount == 0` immediately before profiling/candidate generation and again after candidate generation. Storage values are summed logical file lengths from `FileInfo.Length` for each lane's database and lifecycle/index files; index storage Δ is final storage minus storage before index. They exclude allocated disk-block accounting, so sparse or preallocated WAL files can differ from physical disk usage and these values do not establish physical storage superiority.
Initial ingest ms is measured before any secondary index exists. The fixed 100 maintenance documents are always seeded; `--maintenance-documents` controls how many of those documents are updated after index build, so zero and 100 compare the same initial state. Maintenance ms and maintenance storage Δ measure that post-build batch; they are write-cost observations for this batch, not a sustained write-amplification estimate. Repeat samples are repeated measurements within one seeded process and do not constitute independent seeded trials; no cross-trial variance or confidence interval is claimed.
This invocation is the cold first-query lane: its first evaluation query follows post-build maintenance before any diagnostic index probes. Pair it with an identically seeded `--index-perf` invocation for per-index lifecycle build and post-build catch-up plus probe phases; diagnostic-lane first-query values are warmed and are not comparable to this lane.
Jev lane status: UNRUN: --jev was not supplied. Jev is only a measured external strategy when `--jev` and an approved `TERRABASE_JEV_API_KEY` are supplied; no fallback run is labeled as Jev. Model probabilities/confidence are not treated as measured index benefit.
