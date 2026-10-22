# Repeatable local replication benchmark

From the repository root on the chosen dev host:

```sh
TS_HEAVY_HOST=beta nice -n 19 make -f benchmark/repl.mk bench-repl PROFILE=loopback N=100000 REPEATS=3
```

This builds Release, launches three separate authenticated TerraBase processes,
and writes `benchmark/bench/<UTC date>-repl-baseline.json` and `.md`.
`TsDotnetRoot` must point to the sibling ts-dotnet checkout when it is elsewhere.
Every run owns private data, ports, member files and credentials; no production
services or shared host network settings are changed. The host pin is mandatory.

For a quick command check, use `SCENARIO=writes N=30 REPEATS=1`. Scenarios are
`writes`, `feeds`, `catchup`, `rebuild`, or `all` (default). Catch-up always uses
100,000 writes and empty-copy reconstruction always uses 1,000,000 documents;
`N` controls the ordinary write/feed phases. Every phase uses a fresh cluster.
WAN/netem is explicitly unsupported until isolated network namespaces are added.

For A/B, cherry-pick the harness commit onto each branch, push both branches,
then run the same command back-to-back on beta, changing only `--branch` and `OUT`:

```sh
TS_HEAVY_HOST=beta ts-heavy-any bash -c 'export TS_HEAVY_HOST=beta; nice -n 19 make -f benchmark/repl.mk bench-repl SCENARIO=writes N=100000 REPEATS=3 OUT=benchmark/bench/before' --repo terrascale --branch <before-with-harness>
TS_HEAVY_HOST=beta ts-heavy-any bash -c 'export TS_HEAVY_HOST=beta; nice -n 19 make -f benchmark/repl.mk bench-repl SCENARIO=writes N=100000 REPEATS=3 OUT=benchmark/bench/after' --repo terrascale --branch <after-with-harness>
```

Record the measured commit and TerraBase source-tree hash, host topology,
host load/CPU pressure, all repetitions and median/min/max from each report.
Use the same scenario, document count, batch sizes, concurrency and seed.
After an unchanged build, `bench-repl-run` runs the benchmark without rebuilding.
Per-node CPU/allocation and wire windows exclude exact listing verification.
The single/batch phases include low-rate visibility probes; feed phases omit them.
Probe PUT/GET requests retry HTTP 503 every 100 ms for at most 30 seconds;
retry time stays in dispatch-to-visible lag, and exhausted retries fail the run.
Feed admission reports the actual 32-reader database cap when 100 are requested.
