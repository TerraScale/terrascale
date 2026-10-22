# Three-process test host protocol

DECISION: run the existing `ManagementApi`/`TerraBaseTestServer` harness once per
process, with a directory-backed member list matching `RegionProcessHost`.
`TestTopology` alone cannot represent members in different regions. The old
`BenchmarkClusterHost` keeps all nodes in one process; its `Cluster` cannot be
linked into Slow without also pulling in unrelated E2E infrastructure.

1. Create an absolute private run directory (0700), its `members` directory,
   and `gru`, `sjc`, `fra` subdirectories. Create `control` in each region directory.
2. Write one JSON config per region, with the same tenant/user IDs in all three:
   `{"run_directory":"/absolute/private/run","region":"gru","machine":"<ULID>",
   "organization":"<ULID>","project":"<ULID>","database":"<ULID>","user":"<ULID>"}`.
   Machine IDs differ per region. All IDs are ULIDs. Optional `advertised_peer_url`
   is an HTTP origin with a literal loopback IP, pointing to the worker's incoming
   TCP counting proxy. Do not reuse a run directory between independent runs.
3. Set `TERRABASE_BENCH_PROCESS_CONFIG` to that worker's absolute config path and
   `TERRABASE_TEST_PEER_SECRET` to one shared random 64-character hexadecimal secret
   in the process environment (never argv). Launch each worker with:
   `nice -n 19 dotnet run --project tests/TerraBase.Slow --configuration Release --no-build -- --treenode-filter '/*/*/BenchmarkProcessHost/*' --results-directory /absolute/private/run/gru/results`.
   Launch all three before waiting for targets. Build beforehand with
   `nice -n 19 make terrabase-build CONFIGURATION=Release`.
4. Watch `<run>/<region>/listening.json` first: it contains `peer_url`, the actual
   listener, and appears immediately after server startup, before membership
   publication or tenant registration. Route the TCP proxy there. The member file
   advertises `advertised_peer_url` when supplied, otherwise the actual listener.
   Wait for `<run>/<region>/target.json` from all three. Each is atomically written,
   mode 0600, and contains `kind`, `region`, `machine`, `organization`, `project`,
   `database` (route name `canary`), `database_id`, `url`, `token`, `metrics_url`,
   `pid`, `data_directory`, `remote_confirmation`, `peer_url` (actual listener).
   The public URL is loopback HTTP,
   matching the existing test proxy boundary. Authentication remains enforced.
   Each JWT belongs to its own worker/issuer and expires after 12 hours.

The host does not create benchmark collections: orchestration uses the real API.
Readiness proves the database is readable with its JWT on that worker; it does
not promise remote write admission is already available. Retry admission until
peer streams connect. Metrics URL includes `/metrics` on the administrative listener.

Scrape runtime counters `dotnet.gc.heap.total_allocated` and
`dotnet.process.cpu.time` through `/metrics`; take differences within one PID.
Wire traffic is `terrabase.replication.sent.encoded`. A test-only decorator
exports histograms `terrabase.benchmark.admission.duration` and
`terrabase.benchmark.remote_wait.duration` (seconds), including failures and
cancellation. It forwards every method, result, lease and cancellation unchanged.
These do **not** measure local durable commit latency: do not subtract sums from
client percentiles or label the remainder as local commit latency.

For server dispatch-to-local-durable timing, atomically replace
`<run>/<region>/sample.request` with a fresh ULID as plain text. Wait for
`sample.json` with matching `id`; it reports `local_ack_count`,
`local_ack_p50_ms`, `local_ack_p99_ms`, `local_ack_capped_count`, `pid`,
`window_started_ticks`, `reset_at_ticks`, `timestamp_frequency`.
The snapshot resets the histogram: request one before load and one after load,
waiting for each response. Quantiles are null for zero samples, rounded up in
0.1 ms bins and capped at 60,000 ms. The fixed histogram is about 4.8 MB per worker.
Middleware timestamps dispatch; the unchanged remote-durability decorator records
at entry to `WaitAsync`, after local commit and before the remote wait. This
includes validation, admission and queueing; it excludes client network time and
remote acknowledgement waiting. Only requests reaching that boundary count,
including requests whose later remote confirmation fails; it is not the same
population as successful client responses. Single-node local-ack references do
not enter `WaitAsync`, so this histogram is empty there.

Ordinary Slow
execution without a config runs a real authenticated single-node integration
smoke using the same decorator; it never skips or waits for external control.

Delete a worker's `control` file for graceful stop; its `target.json`,
`listening.json` and member entry disappear on shutdown. For crash/catch-up,
SIGKILL its exported PID, remove its stale `members/<machine>`, `listening.json`
and `target.json`, retain `data`, recreate `control`,
and relaunch the identical config. Reread its new target/token/PID/ports.
For a clean rebuild, stop first and use a new machine ULID and empty data directory
(move the old `data` aside while stopped); the remaining peers supply replica data.
Never edit a live data directory. The harness preserves explicitly supplied data;
the orchestration owns deleting its run directory after all workers exit.
