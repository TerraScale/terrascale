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
- `storage_reads_delta` (primary): `terrabase.storage.reads{path="feed"}` (Prometheus `terrabase_storage_reads_total`)
  after minus before the step, from the same gru scrape. It counts every physical backend SELECT the feed runs on that
  node: head, floor, prefix and retention reads, and each document body, including zero-row reads and misses. Page
  cache hits count; this is not disk I/O. The harness reads the counter's `path` label only and accepts `point`,
  `query`, `feed` and `shape`; any other label, a repeated path, a missing feed series or a fallen counter fails the
  step. The counter carries no tenant label.
- The step's window opens before admission and closes after the 5 s grace and a 6 s settle. The delta therefore
  includes admission, warmup and grace reads, not the 30 s measured window alone.
- `accounting_reads_delta` (secondary): `terrabase.stats.operations{kind="read"}` after minus before the step, from the
  node's Prometheus scrape (identified by opening one calibration stream on gru and finding the host TCP listener whose
  scoped `/metrics` reports that stream, then closing it before measurement). It is document accounting
  (GETs and queries), published per closed 5 s window, and is not a storage read count.
- The raw `terrabase_storage_reads_total` and `terrabase_stats_operations` lines before and after each step are in the report.
- Both deltas cover gru only; sjc reads are not included.

## Limits

- `inferred_min_doc_selects` (secondary) is inferred from `SqliteStorageTransaction.Feed.cs` (one document SELECT per
  received upsert of an in-window write per stream, including redeliveries through grace), not measured. It excludes
  warmup writes and other storage work. Compare it with `storage_reads_delta`, which is measured.
- Two regions are required for database placement. All client traffic targets gru; sjc replicates in the same process.
  Both nodes use loopback: no WAN, Fly Proxy or TLS.
- A step stops the run with an error when a measurement is invalid: a follower refused other than by `changes_busy`
  (for example `tenant_memory` or a QoS 429), a stream that ends early, a failed write or one unanswered at the end of
  the grace, a node stream count that disagrees with the admitted followers, or a window without writes or deliveries.
  The report is still written with the completed steps and the error.
