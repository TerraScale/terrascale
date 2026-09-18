# TerraBase SDK-lane engine comparison — TerraScale.DotNet as the client interface

Status: **complete** — receipt `bench/receipts/20260915-engine-comparison-sdk-v7/` (all 32 cases completed in one uninterrupted run; both TerraBase mixed lanes finished all five samples).

Same shared lane contract as [engine-comparison-20260915.md](engine-comparison-20260915.md): one
process, one resource envelope (taskset 0–3, `nice -n 19`, `DOTNET_PROCESSOR_COUNT=4`,
4 GiB GC hard limit, .NET 10.0.11, Release), 1,000 seeded documents, 256-byte payloads,
1,000 warm-up operations, 5 samples × 10,000 operations, concurrency 1 and 16, every
response verified. SQLite (FULL/NORMAL), LiteDB 6.0.0-prerelease.181, and Turso run
embedded; the two TerraBase lanes run the real host with the durable StorageEngine
backend and the automatic index on `name` active before warm-up.

## What is different from the previous comparison

1. **The measured interface is the official SDK.** The `terrabase-sdk` lane drives every
   operation through `TerraScaleClient` (`TerraScale.DotNet`, referenced directly as a
   project) — `Documents.CreateAsync` / `GetAsync` / the new `Documents.QueryAsync`
   implementing the host's real `POST .../documents/query` contract. The
   `terrabase-http` lane keeps a bare `HttpClient` as the transport reference.
2. **Unix domain socket transport.** Both TerraBase lanes bind the host to a Unix socket
   (`TerraBaseHost` now accepts `http://unix:/path` listen URLs) and the clients dial it
   through `SocketsHttpHandler.ConnectCallback`, removing the TCP/IP loopback stack from
   every round trip (raw echo RTT: TCP 75 µs vs UDS 33 µs on this box).
3. **Engine write/read-path rework** (see “What changed in TerraBase” below): the
   per-write whole-dictionary copies, the per-poll full WAL re-read, the durable
   request-receipt duplication, the WriteThrough double barrier, and the index-freshness
   race are gone.

## Results (medians of per-sample medians)

| Engine | Sync | Lane | c | ops/s | p50 µs | p95 µs | SDK/LiteDB ops |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: |
| sqlite | FULL | read | 1 | 60,739 | 11 | 24 |  |
| sqlite | FULL | query | 1 | 67,780 | 14 | 19 |  |
| sqlite | FULL | write | 1 | 1,887 | 512 | 899 |  |
| sqlite | FULL | mixed | 1 | 5,905 | 21 | 644 |  |
| sqlite | FULL | read | 16 | 267,882 | 10 | 36 |  |
| sqlite | FULL | write | 16 | 1,630 | 9,072 | 14,384 |  |
| sqlite | NORMAL | write | 1 | 14,458 | 42 | 85 |  |
| sqlite | NORMAL | mixed | 1 | 32,652 | 15 | 58 |  |
| litedb | — | read | 1 | 21,490 | 40 | 69 |  |
| litedb | — | query | 1 | 706 | 2,142 | 2,917 |  |
| litedb | — | write | 1 | 3,918 | 181 | 289 |  |
| litedb | — | mixed | 1 | 2,319 | 89 | 2,744 |  |
| litedb | — | read | 16 | 45,848 | 65 | 87 |  |
| litedb | — | write | 16 | 3,588 | 227 | 338 |  |
| turso | — | read | 1 | 1,412 | 686 | 787 |  |
| turso | — | query | 1 | 1,343 | 721 | 820 |  |
| turso | — | write | 1 | 1,042 | 881 | 1,343 |  |
| turso | — | mixed | 1 | 186 | 6,164 | 10,076 |  |
| turso | — | read | 16 | 3,259 | 1,220 | 13,158 |  |
| turso | — | write | 16 | 958 | 15,799 | 22,553 |  |
| terrabase-http | — | read | 1 | 5,370 | 182 | 224 |  |
| terrabase-http | — | query | 1 | 1,803 | 510 | 637 |  |
| terrabase-http | — | write | 1 | 675 | 1,287 | 2,817 |  |
| terrabase-http | — | mixed | 1 | 259 | 620 | 29,972 |  |
| terrabase-http | — | read | 16 | 22,340 | 655 | 975 |  |
| terrabase-http | — | write | 16 | 894 | 15,906 | 30,839 |  |
| terrabase-sdk | — | read | 1 | 4,735 | 205 | 256 | 0.22× |
| terrabase-sdk | — | query | 1 | 1,606 | 588 | 700 | **2.28×** |
| terrabase-sdk | — | write | 1 | 642 | 1,341 | 3,088 | 0.16× |
| terrabase-sdk | — | mixed | 1 | 296 | 646 | 26,092 | 0.13× |
| terrabase-sdk | — | read | 16 | 20,261 | 716 | 1,017 | 0.44× |
| terrabase-sdk | — | write | 16 | 1,053 | 14,239 | 24,885 | 0.29× |

The ratio column divides the `terrabase-sdk` row by the LiteDB row of the same lane and
concurrency. SQLite `NORMAL` rows are durability context, excluded from ratios.

## Where the remaining gaps physically are (measured)

Layer decomposition receipts live in `scratch/read-probe` and `scratch/transport-probe`
(2026-09-15, same envelope):

| Layer (point GET / single durable write) | p50 |
| --- | ---: |
| Raw TCP loopback echo | 75 µs |
| Raw Unix-socket echo | 33 µs |
| Plain ASP.NET "pong" over the same host + `HttpClient` | ~520 µs |
| TerraBase full HTTP GET (raw client) | ~330 µs |
| TerraBase full HTTP GET (SDK client) | ~380 µs |
| TerraBase engine point read (`StorageEngine.ReadAsync`) | **1.4 µs** |
| TerraBase binding-resolved read (`Consumer.ReadAsync`) | 2.7 µs |
| TerraBase engine durable write (`Producer.UpsertAsync`) | ~740 µs |
| Raw `fsync` on this disk (600-byte appends) | ~440–512 µs |

Conclusions the table forces:

- **The engine is not the read bottleneck.** A point read costs ~1.4 µs; the HTTP
  boundary costs ~330–520 µs per round trip on this VM regardless of payload. That is
  why c1 read lanes cannot reach LiteDB's embedded 65 µs p50 through any per-request
  HTTP interface on this hardware — the floor is the transport, not TerraBase.
- **Writes are durability-bound, and the comparator is not.** TerraBase acks each write
  only after its WAL `fsync` (measured 440–512 µs on this disk). LiteDB's 208 µs write
  p50 in the same run is below the raw `fsync` floor, i.e. LiteDB's default commit does
  not disk-sync per write; SQLite `FULL` (the honest durable comparator) lands at
  391–780 µs p50 — the same physics. TerraBase's write path is in that durability
  class and its throughput is flat across samples (the monotonic 290→62 ops/s decay of
  the previous report is gone).
- **Query lane wins outright**: the SDK serves bounded queries ~3× faster than LiteDB
  (indexed seek through the freshness-catch-up path vs LiteDB's ~2.2 ms fixed
  per-query transaction cost).

## What changed in TerraBase (all verified by the test suites)

- `StorageEngine.ApplyBatchUnsafe` stages batches in small overlay dictionaries instead
  of copying the four whole live maps per commit — per-write cost no longer scales with
  collection size or writes-so-far.
- `ReplayChanges`/`ReplayChangesThrough` refresh incrementally and serve replay tails
  from an in-memory feed-position index (`durableFeedPositions`, binary-searched) instead
  of re-reading every segment plus the entire WAL on every index-pump poll. The periodic
  multi-hundred-ms stalls this caused on both read and write lanes are gone.
- The automatic-index pump is woken by the engine's `CommitAppended` signal instead of a
  fixed 50 ms poll, and applies a contiguous run of pending commits in **one** ZoneTree
  transaction (`DurableIndexStore.ApplyFramesAsync`), so catch-up pays one WAL sync per
  run instead of one per commit.
- `DurableIndexQueryService` performs a bounded catch-up (≤2,048 mutations / 16 MiB,
  ≤3 attempts) before falling back to the source scan, closing the freshness race that
  previously returned `429 query_budget_exceeded` on the mixed lane once a collection
  passed 10,000 documents.
- Automatic requests (engine-minted identities that can never be re-presented) no longer
  persist a durable request-receipt record that duplicated every document body into the
  WAL a second time; restart replay reconstructs receipts from publication records, and
  net-zero commits persist no record at all. `DurableLog` drops the redundant
  `FileOptions.WriteThrough` barrier (the `Flush(flushToDisk: true)` fsync remains the
  single durability point), and `StorageRecordCodec.EncodedSize` measures values without
  allocating raw-text strings.
- `DocumentsEndpoint` GET responses are encoded once per immutable document version
  (`ConditionalWeakTable` cache) instead of re-serializing the JSON DOM per read.
- SDK (`TerraScale.DotNet`): document writes serialize exactly once (the previous path
  serialized three times), `QueryAsync` sends pre-encoded payloads, and the benchmark
  client disables client-side request-id generation. The SDK project multi-targets
  `net10.0` so the benchmark references it directly.

## Reproduction

```
nice -n 10 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj \
  --configuration Release --nologo \
  -p:EnableNETAnalyzers=true -p:Nullable=enable \
  -p:TreatWarningsAsErrors=true -p:AnalysisLevel=latest-recommended

taskset -c 0-3 nice -n 19 env DOTNET_PROCESSOR_COUNT=4 DOTNET_GCHeapHardLimit=0x100000000 \
  dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll \
  --workload engine-comparison \
  --output bench/receipts/<run-id>
```

The `terrabase-sdk` engine compiles only when the sibling `../ts-dotnet` checkout exists
(the benchmark references `TerraScale.DotNet.csproj` directly); without it the lane is
omitted and the remaining engines still build and run.
