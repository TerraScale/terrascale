# TerraBase full SDK vs LiteDB — embedded comparison

Status: **see the receipt table below** — receipt `bench/receipts/20260917-sdk-litedb-final/`.

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
- **Reactive streams** (`ReactiveStreamEndpoint` + SDK `ServerSentEvents`
  transport): server-push delivery of the same v1 envelopes the bounded long-poll
  log returns —
  `GET /api/v1/reactive/views/{view}/{generation}/stream` (SSE, base64 data
  events, `id:` carries the resume offset, `Last-Event-ID` resumes) and
  `GET .../websocket` (binary frames, `terrascale.sync.v1` subprotocol, the
  protocol the SDK's WebSocket transport already speaks). The SDK reactive
  surface now covers HTTP long-poll, WebSocket, and SSE.

## Results (medians of per-sample medians)

<!-- RESULTS_TABLE -->

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
  --output bench/receipts/20260917-sdk-litedb-final
```
