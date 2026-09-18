# TerraBase full SDK vs LiteDB — embedded comparison

Status: **complete — all six lanes beat LiteDB at matched durability** — receipt `bench/receipts/20260918-sdk-litedb-write-v2/` (the definitive run, after the write-path work below; the earlier `20260917-sdk-litedb-final/` receipt is kept as the pre-write-work record where write c1 sat at 0.85×).

The lane contract matches [engine-comparison-sdk-20260915.md](engine-comparison-sdk-20260915.md):
one process, one resource envelope (taskset 0–3, `nice -n 19`, `DOTNET_PROCESSOR_COUNT=4`,
4 GiB GC hard limit, .NET 10, Release), 1,000 seeded documents, 256-byte payloads,
1,000 warm-up operations, 5 samples × 10,000 operations, concurrency 1 and 16, every
response verified.

## What this comparison measures

The `terrabase-sdk` lane drives the **official TerraScale.DotNet SDK
(`TerraScaleClient`) through the full host pipeline** — routing, placement,
authorization, document endpoints, automatic index runtime, durable
StorageEngine — using `TerraBaseHost.CreateEmbedded`: in-process dispatch with
no socket. This is the same comparison class LiteDB occupies: LiteDB's lane is
its embedded C# API inside the same process. A socket-bound variant of the same
SDK remains measurable through the `terrabase-http` lane.

## Durability classes

LiteDB 6.0.0-prerelease.181 runs with engine defaults: its commit path does not
device-sync per write (its write p50 sits below the raw fdatasync floor of this
disk). The receipt therefore carries two TerraBase rows:

| Row | WAL barrier | Comparison class |
| --- | --- | --- |
| `terrabase-sdk` | `Synced` — every commit fdatasyncs before the ack | SQLite `synchronous=FULL` (see [engine-sqlite-parity-20260917.md](engine-sqlite-parity-20260917.md)) |
| `terrabase-sdk ASYNC` | `AsyncWal` — commits publish to the OS, a 100 ms background loop plus every compaction/truncate/close forces the barrier | SQLite `synchronous=NORMAL`, LiteDB engine defaults |

`Synced` is the TerraBase default. `AsyncWal` is selected per database binding
(`DatabaseBindingDescriptor.WalDurability`); acknowledged AsyncWal commits
survive process crashes (the bytes are in the kernel page cache) and may lose
the sub-100 ms tail on power loss.

## What changed in TerraBase

- **Embedded dispatch** (`TerraBaseHost.CreateEmbedded` + `EmbeddedTerraBaseServer`):
  an `IServer` that runs the built hosting pipeline in process. The SDK keeps its
  `HttpClient` shape and dials a handler instead of a socket — the embedded class
  LiteDB itself occupies. Measured layers (same envelope, warm):
  engine point read 1.0 µs → pipeline+dispatch 24.5 µs → SDK `Documents.GetAsync` 40 µs.
- **`ReadBodyAsync` allocation diet** (`DocumentsEndpoint`): request bodies with a
  declared content length parse straight off the request stream (no per-request
  80 KB scratch buffer, no `MemoryStream` copy, no `ToArray`); chunked bodies use
  a pooled buffer. SDK writes dropped from ~103 KB/op to ~20 KB/op allocated and
  the write p99 tail fell accordingly.
- **WAL durability dial** (`StorageWalDurability.Synced | AsyncWal`): per-binding
  WAL barrier selection plumbed from `DatabaseBindingDescriptor` through the
  provider open context into the engine append. AsyncWal adds a background flush
  loop (100 ms), a closing barrier on dispose, and no other semantic changes —
  segments still sync at compaction, and the recovery suite passes unchanged.
- **Write-path CPU cuts (2026-09-18)**: throttled automatic-index pump wake
  (one release per 2 ms; the parked-wake release previously ran on the
  writer's thread under the engine gate every commit), v3/v4 WAL frames with
  an 8-byte XxHash3 checksum (readers still verify v1/v2 SHA-256 frames;
  recovery suite passes through hard-kill restarts), LZ4 compress threshold
  256 B → 4 KiB, per-append fstat guard sampled every 64 appends, synchronous
  ValueTask consumption in the engine append, allocation-light
  `ReplayChanges` tail, request tracing gated behind a configured OTLP
  exporter (previously a discarded Activity per request and per SDK call),
  per-route placement header caching, completed-binding fast paths in the
  resolver and engine registry, pre-encoded SDK request bodies passed
  through request options instead of re-buffered, per-request 80 KB body
  buffer removed, and SDK auth-header/routing-clone trims.
- **Reactive streams** (`ReactiveStreamEndpoint` + SDK `ServerSentEvents`
  transport): server-push delivery of the same v1 envelopes the bounded long-poll
  log returns —
  `GET /api/v1/reactive/views/{view}/{generation}/stream` (SSE, base64 data
  events, `id:` carries the resume offset, `Last-Event-ID` resumes) and
  `GET .../websocket` (binary frames, `terrascale.sync.v1` subprotocol, the
  protocol the SDK's WebSocket transport already speaks). The SDK reactive
  surface now covers HTTP long-poll, WebSocket, and SSE.

## Results (medians of per-sample medians)

| Engine | Sync | Lane | c | ops/s | p50 µs | p95 µs | SDK-best/LiteDB ops |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| litedb | — | read | 1 | 14,541 | 59.5 | 91.5 |  |
| terrabase-sdk | — | read | 1 | 11,501 | 86.8 | 111.0 |  |
| terrabase-sdk | ASYNC | read | 1 | **15,721** | **57.0** | 81.0 | **1.08×** |
| litedb | — | query | 1 | 591 | 1,998 | 3,663 |  |
| terrabase-sdk | — | query | 1 | 6,284 | 152.4 | 194.2 |  |
| terrabase-sdk | ASYNC | query | 1 | **7,265** | **106.7** | 195.5 | **12.3×** |
| litedb | — | write | 1 | 3,579 | 194.2 | 353.3 |  |
| terrabase-sdk | — | write | 1 | 1,711 | 475.8 | 1,090.1 |  |
| terrabase-sdk | ASYNC | write | 1 | **4,428** | **196.8** | 247.4 | **1.24×** |
| litedb | — | mixed | 1 | 2,292 | 103.5 | 2,536.7 |  |
| terrabase-sdk | — | mixed | 1 | 3,726 | 155.4 | 829.7 |  |
| terrabase-sdk | ASYNC | mixed | 1 | **5,864** | **124.0** | 439.2 | **2.56×** |
| litedb | — | read | 16 | 29,732 | 71.6 | 100.8 |  |
| terrabase-sdk | — | read | 16 | 36,499 | 55.3 | 80.3 |  |
| terrabase-sdk | ASYNC | read | 16 | **50,606** | **53.9** | 80.8 | **1.70×** |
| litedb | — | write | 16 | 3,251 | 238.6 | 340.6 |  |
| terrabase-sdk | — | write | 16 | 2,041 | 7,117.7 | 13,167.8 |  |
| terrabase-sdk | ASYNC | write | 16 | **6,532** | **201.9** | 3,290.1 | **2.01×** |

Conclusions the table forces (single run, same envelope, within-run ratios only):

- **All six lanes beat LiteDB at matched durability class**: write c1 1.24×
  (4,428 vs 3,579 ops/s; p50 197 vs 194 µs with a 247 µs vs 353 µs p95),
  write c16 2.01×, read c1 1.08×, read c16 1.70×, query c1 12.3×, mixed 2.56×.
- **The write-c1 flip (0.85× → 1.24×)** came from the write-path work below:
  the automatic-index pump wake released a parked waiter on the writer's
  thread under the engine gate (~17 µs measured per commit at c1 cadence);
  it is now throttled to one wake per 2 ms so each pump pass batches commits.
  WAL frames carry an 8-byte XxHash3 checksum instead of a 32-byte SHA-256
  digest (software SHA on this CPU cost 7.2 µs per commit across record and
  frame passes), small frames skip the LZ4 compress attempt entirely, the
  per-append fstat tamper guard runs every 64th append, and the endpoint,
  dispatcher, and SDK layers dropped their per-write re-buffering, request
  cloning, and uncached resolution costs.
- **The Synced rows remain the SQLite-FULL comparison class**, not the
  LiteDB one — they device-sync every commit and are measured in
  [engine-sqlite-parity-20260917.md](engine-sqlite-parity-20260917.md).
- Cross-day absolute numbers on this box swing with the disk; the ratios
  above are within one run.

## Reproduction

```
nice -n 10 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj \
  --configuration Release --nologo \
  -p:EnableNETAnalyzers=true -p:Nullable=enable \
  -p:TreatWarningsAsErrors=true -p:AnalysisLevel=latest-recommended

taskset -c 0-3 nice -n 19 env DOTNET_PROCESSOR_COUNT=4 DOTNET_GCHeapHardLimit=0x100000000 \
  dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll \
  --workload engine-comparison \
  --engines terrabase-sdk,litedb \
  --output bench/receipts/20260918-sdk-litedb-write-v2
```
