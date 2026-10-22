# Feed reader fanout benchmark

Measures the real `IDatabaseChanges.StreamAsync` service and storage engine with a `DefaultHttpContext` and an SSE-consuming stream. No fake storage or production configuration changes. This deliberately excludes HTTP, authentication, replication and network costs; integration suites cover those behaviors separately.

Run the build and measurement in one managed dev-host slot, using the pushed branch that contains the benchmark:

```sh
nice -n 19 ts-heavy-any bash -c '
  set -euo pipefail
  nice -n 19 dotnet build benchmark/dotnet/FeedCursorBench -c Release
  nice -n 19 dotnet benchmark/dotnet/FeedCursorBench/bin/Release/net11.0/FeedCursorBench.dll readers > benchmark/bench/YYYY-MM-DD-feed-readers.md
' --repo terrascale --branch <pushed-branch>
```

Set `TsDotnetRoot` inside the managed command when the sibling ts-dotnet checkout is elsewhere. For a comparison, hold one slot across both builds/runs, record exact commits and source hashes, and preserve output outside the checkout before switching refs. Record host load; one slot does not reserve every CPU on a shared host.

Each of five repeats requests 1, 10 or 100 readers of one fresh database. Admission counts are observed, and every rejection must produce HTTP status 429. A 32-stream cap admits 32 and rejects 68 in the 100-reader case; raising the cap changes those counts. Admission and ten warmup changes precede measurement. Each of 200 measured writes updates one ULID-named document; the next write waits until every admitted reader has validated the previous change. Counts and document sequence must match exactly, every stream must cancel within its deadline, and missing feed instrumentation fails the run. Temporary engine files are removed after disposal.

`all-path SELECTs` sums every measurement of `terrabase.storage.reads` through `MeterListener`; `/write` divides that total by 200 producer writes. These are instrumented SELECTs, not every SQL statement the engine executes. `feed SELECTs` restricts the same instrument to `path=feed`; it includes producer append prefix/rehash SQL as well as reader work. `/delivery` divides by 200 times the actual admitted reader count.

CPU is the process `TotalProcessorTime` delta; bytes use `GC.GetTotalAllocatedBytes(true)`. Both include the producer, JSON consumer and synchronization running in the same process, and exclude setup/admission/warmup/cancellation. Latencies use nearest-rank p50/p99 across all measured deliveries, from before transaction creation to validated SSE delivery. The workload has no artificial delay or queued producer backlog. Compare baseline and changed builds back to back on the same host, using identical CPU/memory constraints; repeat 1 includes remaining JIT effects. This is a focused local fanout measurement, not a regional serving benchmark.
