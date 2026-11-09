# EF Core provider and SDK SQL read path (2026-11-09)

Tool: [`benchmark/dotnet/TerraScale.EntityFrameworkCore.Benchmarks`](../dotnet/TerraScale.EntityFrameworkCore.Benchmarks).
BenchmarkDotNet 0.16.0-preview.2, .NET SDK 11.0.100-rc.3.26522.148, mariogk-dev (Intel Xeon E5-2660 v4, 46 cores,
NixOS 26.05), `nice -n 19`. Every row is 3 measured launches of 10 iterations after 3 warmups (`launchCount: 3`).

```sh
nice -n 19 dotnet run -c Release --project benchmark/dotnet/TerraScale.EntityFrameworkCore.Benchmarks -- --filter '*SqlReadBenchmarks*'
TERRABASE_BINARY=src/TerraBase/bin/Release/net11.0/terrabase \
  nice -n 19 dotnet run -c Release --project benchmark/dotnet/TerraScale.EntityFrameworkCore.Benchmarks -- --filter '*SaveChangesBenchmarks*'
```

## Reading SQL results (client only)

`SqlReadBenchmarks` answers every `/sql/execute` from an in-process handler with a fixed body of 1 or 1,000 rows of
8 columns (ULID text, blob, offset timestamp text, email text, integer, boolean, ULID text, JSON text), so it
measures only the SDK and EF Core cost per query. `Records` reads every column through `TerraBaseSqlRecord`,
`DataReader` through `TerraBaseDbDataReader`, `EntityFramework` runs `context.Accounts.AsNoTracking().ToListAsync()`
on a new context, `EntityFrameworkPooled` on a context from `PooledDbContextFactory`.

Before: response rows deserialized into a `JsonElement` tree, validated by walking it again (decoding every blob),
then read cell by cell from the tree. After: a `Utf8JsonReader` converter reads rows straight into 16-byte typed
cells (integers unboxed, text and blobs decoded once) in a pooled buffer copied once to an exact-size array.

| Method | Rows | Before | After | Change | Allocated before | Allocated after |
|---|---:|---:|---:|---:|---:|---:|
| Records | 1 | 23.88 µs | 21.51 µs | -10% | 10.28 KB | 9.57 KB |
| DataReader | 1 | 27.64 µs | 25.03 µs | -9% | 11.23 KB | 10.53 KB |
| EntityFramework | 1 | 215.71 µs | 205.11 µs | -5% | 56.30 KB | 55.60 KB |
| EntityFrameworkPooled | 1 | | 68.50 µs | | | 16.88 KB |
| Records | 1000 | 2,834.08 µs | 1,715.45 µs | -39% | 1,105.14 KB | 812.32 KB |
| DataReader | 1000 | 3,505.63 µs | 2,232.94 µs | -36% | 1,106.08 KB | 813.27 KB |
| EntityFramework | 1000 | 7,875.36 µs | 6,619.76 µs | -16% | 2,382.75 KB | 2,089.91 KB |
| EntityFrameworkPooled | 1000 | | 6,563.44 µs | | | 2,051.13 KB |

Standard deviations were at most 4% of the mean except DataReader before (8%). A first variant that only split
each row into a `JsonElement[]` gained nothing (2,926 µs, 1,166.86 KB at 1,000 rows) and was dropped.

`DbContext` pooling cuts a one-row query from 205 µs to 69 µs (-67%) and 55.6 KB to 16.9 KB; at 1,000 rows the
materialization dominates and pooling makes no difference. The provider works with `AddDbContextPool`; the docs
recommend it.

## SaveChanges batching (real TerraBase)

`SaveChangesBenchmarks` starts the TerraBase built from this commit (one local node, loopback HTTP), creates a
database with `EnsureCreatedAsync` and inserts 10 or 100 accounts per `SaveChangesAsync` on a new context.
`MaxBatchSize = 1` is EF Core's SQLite behavior (one statement per command batch; more than one batch runs in a
WebSocket transaction); `256` is the provider default (the whole change set in one `/sql/execute` request).

| Entities | MaxBatchSize 1 | MaxBatchSize 256 | Speedup | Allocated 1 | Allocated 256 |
|---:|---:|---:|---:|---:|---:|
| 10 | 63.24 ms ± 3.86 | 14.63 ms ± 1.08 | 4.3x | 353.00 KB | 232.45 KB |
| 100 | 493.34 ms ± 48.08 | 82.24 ms ± 2.18 | 6.0x | 2,289.73 KB | 1,855.06 KB |

## Not changed

- HTTP/2: HTTPS requests already negotiate HTTP/2 with connection reuse (SDK 0.0.18). The local server is plain
  HTTP, so no number here supports a change to the handler.
- Compiled models: model building happens once per process and is outside these per-query numbers; nothing measured
  here supports shipping one.
