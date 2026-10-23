# shape-bench

Fan-out on one of two local TerraBase nodes, for two targets run at every N. A writer PUTs 20 documents/s (256 bytes,
open loop) into one collection while N = 1, 10, 100 and 1000 followers read it, one N per step (5 s warmup, 30 s
recorded, 5 s delivery grace):

- `changes.fanout`: `GET …/changes?cursor=now` SSE stream (the feed).
- `shape.fanout`: `GET …/shape` (shape reads v1). Each follower bootstraps at `offset=-1` (a 307 to the newest
  snapshot's first page), pages the snapshot, catches up through chunks, then long-polls live at the sealed head with
  the bucket the last answer named. Its JSON bodies are decoded in `shape.go`.

A step first opens its followers one at a time at 20/s, each awaiting admission or rejection (the local tenant is on
the Hobby QoS plan, which refuses bursts; at most 2 minutes), then every admitted follower reads through the window
together. A shape step also waits until every admitted follower has sent its first live long-poll and the node's
`terrabase.shape.requests.waiting` gauge has held still for 1 s at one or more, before the window opens. The gauge is the
node's own count of parked requesters, so it can be below the admitted count (see the admission note below). It takes no flags: the target list (`targets` in `main.go`) and the follower counts are fixed in code. The
writer, measurement window, delivery aggregation, resource sampling and reporting are shared by both targets.

## Run

From the repository root, after a Release build of the host (the build reads the ts-dotnet sibling checkout; set
`TsDotnetRoot` if yours is elsewhere). Run both through the host's heavy-run wrapper, which applies `nice -n 19`:

```sh
~/.local/bin/ts-heavy make terrabase-build CONFIGURATION=Release
cd benchmark && ~/.local/bin/ts-heavy go run ./cmd/shape-bench
```

It starts `tests/TerraBase.E2E`'s `BenchmarkClusterHost` with `TERRABASE_BENCH_REGIONS=gru,sjc` (the local host of
`make benchmark-repl-local`: test-only ts-auth stand-in and temporary data directory, local only), waits for its targets
file, runs every step and writes `bench/<date>-<host>-<ULID>.md` and `.json` through `apibench.WriteReport`. Deleting
the targets file at the end stops the host.

## What it records

- Requested N, admitted and rejected followers, with each rejection's code. changes.fanout: `changes_busy`; the server
  admits 32 streams per database and 1024 per machine, and above 32 only admitted followers are measured, so
  deliveries/s and bytes do not grow with requested N. shape.fanout: `shape_busy` (32 running flights per machine and 8 per
  organization, no queue) and `shape_too_large`; concurrent identical live polls join one flight. Live requesters are capped at 4096 per
  machine and 1024 per organization across all flights, so a live poll beyond that is `shape_busy` and retried. Live polls also spend
  the organization's QoS request tokens, so a QoS 429 is retried too; `server_at_ready` reports how many were parked when the step became ready.
- Delivery lag (p50 … max): the write's send time, stored in the document, to the follower reading its change event.
- Missed deliveries of in-window writes to admitted followers, and response body bytes read by admitted followers: SSE
  stream bytes for changes.fanout, JSON response bodies for shape.fanout.
- CPU and RSS of the combined host process from `/proc/<pid>` (both nodes, replication work, ts-auth stand-in and test runner).
- Storage reads (primary, measured): `terrabase.storage.reads{path}` (Prometheus `terrabase_storage_reads_total`) after
  minus before the step, from the same gru scrape. Each target names its paths:
  - `changes.fanout`: `path="feed"` (`storage_reads_delta`): head, floor, prefix and retention reads, and each document
    body, including zero-row reads and misses.
  - `shape.fanout`: `path="shape"` (`storage_reads_delta_shape`, metadata SQL), `path="shape_body"`
    (`storage_reads_delta_shape_body`, payload-body SELECTs) and `path="point"` (`storage_reads_delta_point`, document
    checks made on each HTTP shape request). They are three separate deltas and the report never sums them. The point
    delta is node-wide: it also counts the writer's point reads during the step, so it is not attributable to shape
    followers alone.
  Page cache hits count; this is not disk I/O. The harness reads the counter's `path` label only and accepts `point`,
  `query`, `feed`, `shape` and `shape_body`; any other label or a repeated path fails the step. A path absent before the
  step counts as zero (its counter starts at zero in each process); a path absent after it, or a fallen counter, fails
  the step. The counter carries no tenant label.
- Shape retries: a request refused with `429 shape_busy` or `503` (`unavailable`, `shape_too_large`) after admission is
  repeated with its state kept, after 200 ms, and counted in the status `retries`. Neither a retry nor a rejection
  before admission counts as delivered or missed.
- The step's window opens before admission and closes after the 5 s grace and a 6 s settle. The delta therefore
  includes admission, warmup and grace reads, not the 30 s measured window alone.
- `accounting_reads_delta` (secondary): `terrabase.stats.operations{kind="read"}` after minus before the step, from the
  node's Prometheus scrape (identified by opening one calibration stream on gru and finding the host TCP listener whose
  scoped `/metrics` reports that stream, then closing it before measurement). It is document accounting
  (GETs and queries), published per closed 5 s window, and is not a storage read count.
- The raw `terrabase_storage_reads_total` and `terrabase_stats_operations` lines before and after each step are in the report.
- Both deltas cover gru only; sjc reads are not included.

## Limits

- `inferred_min_doc_selects` (secondary, changes target only; the shape target reports `n/a`) is inferred from `SqliteStorageTransaction.Feed.cs` (one document SELECT per
  received upsert of an in-window write per stream, including redeliveries through grace), not measured. It excludes
  warmup writes and other storage work. Compare it with `storage_reads_delta`, which is measured.
- Two regions are required for database placement. All client traffic targets gru; sjc replicates in the same process.
  Both nodes use loopback: no WAN, edge proxy or TLS.
- A step stops the run with an error when a measurement is invalid: a follower refused other than by its cap
  (`changes_busy`, `shape_busy`, `shape_too_large` before admission) or a retry the protocol allows (a QoS 429 is
  repeated, never an error), a stream that ends early, a failed write or one unanswered at the end of the grace, a node
  stream count that disagrees with the admitted followers, a metrics read that fails, or a window without writes. The
  report is still written with the completed steps and the error.
- A step that reaches a capacity limit is a row, not an error, so every requested N is in the report. Its `stopped`
  column names the limit: no follower admitted (all refused by a cap), the 2 minute admission deadline (with
  `unresolved` followers counted), a readiness timeout after admission (60 s), or no delivery of the expected count in
  the window (measured, so its figures are reported, except latency: no delivery was measured, so latency is `n/a`
  on that row). A row that stopped before its window has `n/a` for writes,
  deliveries, storage reads and accounting reads, and its raw metric lines are not printed. Its counts (`admitted`,
  rejections, `qos_busy_followers`, `retries`) are kept. The shared apibench table prints zeros for latency and
  requests/s on those rows because its format has no empty value; the notes table here is the measured view.

## Live load (V3)

`shape-bench load` drives many readers of one shape, step by step on a shared wall clock (`-steps`, default
1, 100, 1000 and 10000 readers in total), and `shape-bench merge` joins the load processes into one report. Each step
is 120 s from `-start`: readers open evenly over 60 s and repeat a `shape_busy` or `shape_too_large` refusal (counted as
`refused`) until 70 s, as a client would (an admitted reader answered `409 must_refetch` restarts at `offset=-1`; the 409 stays
in the status counts); the writer then sends 20 documents/s, the 30 s window opens after a 5 s warmup,
deliveries count through a 5 s grace, and the after scrape follows a 6 s settle.

- It starts the local two-node host (as above), runs one shard against gru, scrapes both nodes and merges
  the report itself.
- Per step and node: `terrabase.shape.seals` (sealed objects: chunks and snapshot pages), mirrored objects
  (`terrabase.shape.mirror.lag` count; FR-22), storage reads by path, the parked gauge at window start, and node CPU
  and RSS from the runtime metrics. Per step: admitted, unresolved and refused readers, delivered and missed writes
  (admitted × writes − delivered), latency p50/p99 (write send time to read; across machines it includes clock skew),
  shape responses by status, the 304 ratio (304 / (200 + 304); followers send no `If-None-Match`), and each load
  process's CPU and RSS.
- **Bound.** A node breaks it when its `shape` plus `shape_body` reads exceed one per sealed object per cache location:
  the node, plus Tigris for each mirrored object (`reads > sealed + mirrored`). `point` reads are reported, not gated:
  they are node-wide and include the writer's. Any step over the bound, or any shard error, makes `merge` exit non-zero
  and print why.
