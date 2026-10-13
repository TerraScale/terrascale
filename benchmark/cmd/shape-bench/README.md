# shape-bench

Fan-out of the changes feed on one of two local TerraBase nodes. A writer PUTs 20 documents/s (256 bytes, open loop) into one
collection while N = 1, 10, 100 and 1000 followers read the same database's `GET …/changes?cursor=now` SSE stream,
one N per step (5 s warmup, 30 s recorded, 5 s delivery grace). A step first opens its followers one at a time at
20/s, each awaiting admission or rejection (the local tenant is on the Hobby QoS plan, which refuses bursts; at most
2 minutes), then every admitted stream reads through the window together. It takes no flags: the target list
(`targets` in `main.go`, today only `changes.fanout`) and the follower counts are fixed in code.

A later shape target registers its endpoint, follower decoder and readiness check in `targets`. The writer,
measurement window, delivery aggregation, resource sampling and reporting remain on the same path.

## Run

From the repository root, after a Release build of the host (the build reads the ts-dotnet sibling checkout; set
`TsDotnetRoot` if yours is elsewhere). Run both through the host's heavy-run wrapper, which applies `nice -n 19`:

```sh
~/.local/bin/ts-heavy make terrabase-build CONFIGURATION=Release
cd benchmark && ~/.local/bin/ts-heavy go run ./cmd/shape-bench
```

It starts `tests/TerraBase.E2E`'s `BenchmarkClusterHost` with `TERRABASE_BENCH_REGIONS=gru,sjc` (the local host of
`make benchmark-repl-local`: test-only ts-auth stand-in and temporary data directory, no Fly), waits for its targets
file, runs every step and writes `bench/<date>-<host>-<ULID>.md` and `.json` through `apibench.WriteReport`. Deleting
the targets file at the end stops the host.

## What it records

- Requested N, admitted and rejected (`changes_busy`) followers. The server admits 32 streams per database and 1024 per
  machine; above 32 only admitted followers are measured, so deliveries/s and bytes do not grow with requested N.
- Delivery lag (p50 … max): the write's send time, stored in the document, to the follower reading its change event.
- Missed deliveries of in-window writes to admitted followers, and SSE response body bytes of admitted streams.
- CPU and RSS of the combined host process from `/proc/<pid>` (both nodes, replication work, ts-auth stand-in and test runner).
- `accounting_reads_delta`: `terrabase.stats.operations{kind="read"}` after minus before the step, from the node's
  Prometheus scrape (identified by opening one calibration stream on gru and finding the host TCP listener whose
  scoped `/metrics` reports that stream, then closing it before measurement). It is document accounting
  (GETs and queries), published per closed 5 s window, and the feed does not add to it. The raw lines are in the report.

## Limits

- Physical storage reads of the feed are not instrumented: the report says N/A. `inferred_min_doc_selects` is inferred
  from `SqliteStorageTransaction.Feed.cs` (one document SELECT per received upsert per stream), not measured.
- Two regions are required for database placement. All client traffic targets gru; sjc replicates in the same process.
  Both nodes use loopback: no WAN, Fly Proxy or TLS.
- A step stops the run with an error when a measurement is invalid: a follower refused other than by `changes_busy`
  (for example `tenant_memory` or a QoS 429), a stream that ends early, a failed write or one unanswered at the end of
  the grace, a node stream count that disagrees with the admitted followers, or a window without writes or deliveries.
  The report is still written with the completed steps and the error.
