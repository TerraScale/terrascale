# TerraBase vs SQLite, LiteDB, and Turso full-flow engine comparison

Status: **25 of 26 cases complete; the TerraBase mixed case failed with a bounded-query budget rejection after 2 of 5 samples** (receipt `20260915-engine-comparison-v1`, status `failed` for that reason; every other case row is `complete` inside the same receipt).

The comparison runs one shared lane contract through four C#/.NET 10 engines in one process and one resource envelope, so the language and runtime are not differentiators. SQLite is the baseline, not a target.

## Compared-case boundary

| Engine | Boundary | Durability |
| --- | --- | --- |
| `terrabase-http` | The real host (`TerraBaseHost.Create`, loopback HTTP): `PUT` with `If-None-Match: *` creates, point `GET`, bounded JSON-DSL query `POST .../documents/query`, durable StorageEngine backend, automatic unique index on the `name` field built and shadow-verified before warm-up | StorageEngine synced WAL/segment |
| `sqlite` | Embedded `Microsoft.Data.Sqlite`, cached prepared statements, one serialized writer connection plus per-slot readers, autocommit per statement, PK `(tenant,id)` and secondary index `(tenant,name)` | WAL journal, `synchronous=FULL` (baseline) and `synchronous=NORMAL` (context rows) |
| `litedb` | Embedded `LiteDatabase` (v6 prerelease), shared thread-safe engine, unique index on `$.name`, autocommit per operation | v6 WAL rewrite, engine defaults |
| `turso` | Embedded `Turso.Data.Sqlite.Provider` (Turso native SDK runtime) over the same ADO.NET shape as the SQLite rows | `journal_mode=wal` returned by the engine; provider defaults otherwise |

The boundaries are deliberately different and are part of the result: TerraBase is the only server-style subject (every operation pays HTTP, JSON, authorization, and index-maintenance costs inside a real host), while the comparators are embedded. Ratios below therefore measure system-vs-system, not engine-core-vs-engine-core.

Lane contract (identical for every engine, fresh database per case): 1,000 seeded documents (`seed-%05d`, `name=user-%d`, `group=i%100`, 256-byte payload), then `read` (uniform point lookups), `query` (equality on the indexed `name` field), `write` (unique-id creates `write-%08d`), and `mixed` (50% read / 30% write / 20% query by operation index) at concurrency 1 and 16; 1,000 warm-up operations; 5 samples × 10,000 operations; seed 1337. Per-operation responses are verified (status code plus document identity), so a wrong answer fails the case.

## Source, build, and environment identity

- Commit `3d12aea0d7c8d6fe2e1c39506afc7b5d7b05204b` (working tree dirty: the benchmark harness itself, plus in-flight host work on `main`).
- Benchmark artifact SHA-256: `a8a6e445ed8d6831e80459850a1c0f1d6347e5b93d0ff96d4c90224c4d5c8ff19` (recorded in full in the receipt).
- Comparators: `Microsoft.Data.Sqlite 10.0.12`, `LiteDB 6.0.0-prerelease.181` (dev build `a50661a9d1a25b5713586d0096c6325d76bf5dfe`), `Turso.Data.Sqlite.Provider 0.8.0-pre.11` (`Turso.Data.Native` linux-x64 native runtime). Assembly and native-runtime SHA-256 digests are recorded per comparator in the receipt.
- Runtime: .NET 10 (SDK 10.0.400 pin), Release build, workstation GC, `DOTNET_PROCESSOR_COUNT=4`, `DOTNET_GCHeapHardLimit=0x100000000` (4 GiB).
- Host envelope: Intel Xeon E5-2660 v4, taskset pinned to CPUs 0–3, `nice -n 19`, ~10–11 GiB of other tenant memory on a 31 GiB machine with active swap pressure during the run.

## Retained results (medians of per-sample medians)

| Engine | Sync | Lane | c | ops/s | p50 µs | p95 µs | p99 µs | TB/SQLite ops |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| sqlite | FULL | read | 1 | 64,373.8 | 13.4 | 23.2 | 33.6 | — |
| sqlite | FULL | query | 1 | 61,015.6 | 15.0 | 20.5 | 38.9 | — |
| sqlite | FULL | write | 1 | 2,181.1 | 318.9 | 750.6 | 2,014.3 | — |
| sqlite | FULL | mixed | 1 | 6,479.1 | 20.1 | 619.3 | 808.3 | — |
| sqlite | FULL | read | 16 | 153,526.8 | 8.9 | 15.7 | 33.0 | — |
| sqlite | FULL | write | 16 | 1,934.7 | 7,870.9 | 11,016.4 | 13,653.7 | — |
| sqlite | NORMAL | write | 1 | 17,486.7 | 32.4 | 66.5 | 110.8 | — |
| sqlite | NORMAL | mixed | 1 | 35,931.9 | 14.5 | 52.6 | 74.1 | — |
| litedb | — | read | 1 | 21,017.4 | 41.4 | 61.4 | 99.1 | 0.33 |
| litedb | — | query | 1 | 702.9 | 2,164.3 | 2,847.8 | 4,452.3 | 0.012 |
| litedb | — | write | 1 | 4,498.5 | 160.0 | 253.3 | 860.2 | 2.06 |
| litedb | — | mixed | 1 | 2,522.4 | 82.3 | 2,615.6 | 2,994.0 | 0.39 |
| litedb | — | read | 16 | 23,176.5 | 67.5 | 92.6 | 7,190.6 | 0.15 |
| litedb | — | write | 16 | 3,438.4 | 247.6 | 300.9 | 1,352.8 | 1.78 |
| turso | — | read | 1 | 1,443.7 | 659.4 | 738.6 | 1,454.1 | 0.022 |
| turso | — | query | 1 | 1,388.2 | 715.9 | 790.3 | 1,030.1 | 0.023 |
| turso | — | write | 1 | 1,116.6 | 825.6 | 1,212.5 | 2,518.2 | 0.51 |
| turso | — | mixed | 1 | 195.9 | 5,743.3 | 9,735.4 | 12,025.0 | 0.030 |
| turso | — | read | 16 | 2,254.1 | 1,243.5 | 19,475.5 | 24,117.4 | 0.015 |
| turso | — | write | 16 | 1,015.4 | 15,181.0 | 19,180.5 | 21,884.7 | 0.52 |
| terrabase-http | — | read | 1 | 1,545.9 | 244.3 | 305.7 | 493.3 | 0.024 |
| terrabase-http | — | query | 1 | 610.0 | 570.0 | 1,248.4 | 70,637.4 | 0.010 |
| terrabase-http | — | write | 1 | 114.1 | 7,252.9 | 15,927.5 | 25,748.8 | 0.052 |
| terrabase-http | — | mixed | 1 | **failed** | 1,844.1 | 536,790.1 | 1,152,611.1 | — |
| terrabase-http | — | read | 16 | 5,853.5 | 659.1 | 4,800.7 | 84,071.7 | 0.038 |
| terrabase-http | — | write | 16 | 114.2 | 133,765.2 | 190,107.0 | 219,946.0 | 0.059 |

The `TB/SQLite ops` column is throughput relative to the SQLite `synchronous=FULL` row of the same lane and concurrency; SQLite `NORMAL` rows are durability context and are excluded from ratios. Per-case query plans: SQLite `IndexSeek` on `users_tenant_name`; Turso `IndexSeek` on the primary-key autoindex (`sqlite_autoindex_users_1`); LiteDB unique `$.name` index verified via `$indexes` with no EXPLAIN surface in this prerelease; TerraBase `EqualitySeek` on the automatic index (`rowsExamined ≈ 3`, no snapshot fallback in the pure query lane).

## Measured, bottleneck, and confidence

- **SQLite baseline**: point reads 13.4 µs p50 at c1 and 153k ops/s at c16; durable (`FULL`) writes 319 µs p50. `NORMAL` writes are 8× faster (17.5k ops/s) — the durability dial, not an engine difference.
- **TerraBase read path**: p50 244 µs per point GET through the full HTTP boundary (18× SQLite's embedded read), scaling to 5.9k ops/s at c16 under the 4-CPU pinned envelope. The boundary (HTTP, JSON, auth, host) is the dominant cost, not the storage engine.
- **TerraBase write path degrades with collection size**: the unique-create lane declines monotonically across samples — 290 → 167 → 114 → 86 → 62 ops/s at c1 (same curve at c16: 328 → 67) as the collection grows from 1,000 to ~51,000 documents. Late-sample p50 reaches ~12–16 ms per durable write. This super-linear growth is the single most actionable TerraBase finding of this run.
- **TerraBase mixed failed by design envelope, not by accident**: with sustained writes the automatic index goes stale at the query cut and the planner falls back to the bounded source scan; once the collection passed 10,000 documents (during sample 3) every fallback scan exceeded the endpoint's 10,000-rows-examined budget and returned `429 query_budget_exceeded`. The two completed samples (37.5, then 16.9 ops/s; p95 537 ms, p99 1.15 s) show the same collapse the write lane shows, plus query staleness catch-up. The bounded lane refused the work rather than scanning unbounded — correct safety behavior, but it means predicate-query-plus-sustained-write workloads on >10k-document collections fail closed unless indexes stay fresh at the cut.
- **LiteDB v6 prerelease**: reads are respectable (21k ops/s, 0.33× SQLite) and its `FULL`-ish journal beats SQLite `FULL` writes (2.06×), but every query pays a ~2 ms fixed cost (702 ops/s; probe evidence: 1.98 ms/op at 1k documents vs 2.20 ms/op at 10k — flat, so index-selected, but the per-query transaction cost dominates). Mixed p95 2.6 ms is query-dominated.
- **Turso prerelease in-process**: 1.4k ops/s reads (0.022× SQLite) through the ADO provider/native interop, and the mixed lane degrades with WAL growth (587 → 109 ops/s across samples; the engine did not auto-checkpoint under this write rate).
- **Confidence**: single run, single machine, pinned 4-CPU + `nice -n 19` envelope with ambient swap pressure; the historical Rust-era SQLite comparator bounded ambient noise at ±10% for read lanes and observed ~3× day-to-day swings on write lanes. Cross-day comparisons of the absolute numbers are not supported; the within-run ratios are.

## Evidence copies

Retained at `bench/receipts/20260915-engine-comparison-v1/`:

| File | SHA-256 |
| --- | --- |
| `engine-comparison-receipt.json` | `cb487b77d66bed8d5a0cceec5d10f8a193767e50aafdd0ad4c974c34e2266422` |
| `engine-comparison-summary.md` | `5096d1eda95ec2515d9ab76f2ada15042c83c532b7409307b325fe0b04525a21` |
| `raw-samples-*.jsonl` (26 cases, per-sample JSONL flushed incrementally) | digests recorded per file at run time in the receipt directory listing |

Per-sample storage byte deltas (write amplification numerators) are recorded per sample in the receipt JSON. The case databases themselves (`data/` subdirectories, 512 MiB total) were deleted after capture; the per-sample `storageBytesBefore`/`storageBytesAfter` fields in the receipt preserve the on-disk evidence.

## Reproduction

```
nice -n 10 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj \
  --configuration Release --nologo \
  -p:EnableNETAnalyzers=true -p:Nullable=enable \
  -p:TreatWarningsAsErrors=true -p:AnalysisLevel=latest-recommended

taskset -c 0-3 nice -n 19 env DOTNET_PROCESSOR_COUNT=4 DOTNET_GCHeapHardLimit=0x100000000 \
  dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll \
  --workload engine-comparison \
  --output bench/receipts/20260915-engine-comparison-v1
```

Exit code 1 with this receipt is expected: the TerraBase mixed case failed, so the aggregate receipt status is `failed` while the other 25 case rows are `complete`.

## Next measurements

1. **TerraBase write-path growth**: profile the unique-create lane's monotonic decline (290 → 62 ops/s at 51k documents) — index maintenance, WAL replay, or document-store growth; the answer decides the next fix.
2. **Bounded mixed lane**: re-run mixed with create-or-replace writes over a rotating 1,000-document id space (symmetrically for all engines) so every engine's mixed row completes inside TerraBase's 10,000-row query budget; the current receipt keeps the unbounded variant as the failure boundary.
3. **Turso checkpoint behavior**: re-run with periodic `wal_checkpoint` to separate WAL-growth artifacts from steady-state cost.
4. **SQLite `NORMAL` parity**: TerraBase's durability semantics should be characterized against the `NORMAL` dial as well, since LiteDB's journal defaults sit closer to it.
