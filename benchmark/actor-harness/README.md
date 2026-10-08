# TS-648 actor transport harness

This console driver references the real `IActorRouter` from a TerraBase checkout. It measures deterministic key
hashing plus bounded actor dispatch and actor-owned CPU work. It does not start a database, use SQLite, exchange
lattice deltas or pin threads. Results cannot establish full Anna execution or database scaling; those gates stay
open until TS-647 storage/protocol integration and TS-648 per-actor state/flushers land.

From this repository's root, with the pinned .NET 11 SDK available:

```sh
nice -n 19 make actor-check TERRABASE_ROOT=/absolute/path/to/terrabase
nice -n 19 make actor-run TERRABASE_ROOT=/absolute/path/to/terrabase \
  ACTOR_ARGS='--output=../bench/ts648-actor-transport-YYYYMMDD.md'
```

`DOTNET=/absolute/path/to/dotnet` selects an installed SDK host explicitly when a toolchain shim tries to resolve
the repository's unrelated Rust tools. `actor-check` builds with warnings as errors, verifies C# whitespace and
runs process-level regression checks. The checks use Python 3 (override with `ACTOR_PYTHON=/absolute/path/to/python3`
if needed) and inject a stalled consumer into the real router in a separate process to verify timeout reporting
and bounded disposal. Supplied `TERRABASE_ROOT` paths are resolved relative to the Make invocation directory;
`actor-run` refuses to launch if either Git revision lookup fails.

`actor-run` executes from `actor-harness`, so output paths are relative to that directory. Evidence belongs under
`bench/` as Markdown tables. Existing outputs are refused. Capture the TerraBase working diff alongside the report
when measuring an uncommitted checkout; the revision alone does not identify that build. Include **untracked source
files** as well as staged/unstaged diffs from both repositories: `git diff` alone omits newly added implementation.
For reproducible evidence, commit both implementations before running; the report records both repository revisions.
Results are flushed after each trial, and a failed trial records its exception type before exiting unsuccessfully.

The matrix uses actor counts 1/2/4/8 within available processors, uniform keys (Zipf exponent 0) and Zipf exponents
0.99 and 1.2, three repeats, 8192 keys, 20000 operations and 2000 CPU iterations per callback. Thirty-two producers
keep one operation in flight each. Every actor admits at most 64 executing plus queued items and 64 KiB payload;
each operation accounts for 64 captured bytes. Key sequences and workload size stay fixed across actor counts.

Each trial warms up with up to 1000 operations before measuring throughput, end-to-end p99, CPU seconds,
process-lifetime peak RSS and an exact callback-count check. CPU/RSS include the load generator. Repeats share a
process, so peak RSS is cumulative; do not interpret it as a per-trial server memory bound. Per-actor mutable arrays
are bounded by the validated key count and actor count. The harness itself bounds latency samples to one million.

For a bounded smoke run, use `ACTOR_ARGS='--operations=128 --iterations=20 --repeats=1 --keys=64 --actors=1'`.
Other options are `--actors=1,2,4`, `--keys=8192`, `--operations=20000`, `--iterations=2000`, and `--repeats=3`.
`--trial-timeout-seconds=60` bounds each trial including warmup (range 1–3600 seconds). The deadline cancels
dispatch and bounds the producer aggregate even if dispatch never completes. Cancellation and router disposal
have a separate five-second limit; a timeout records a `FAILED` row and exits unsuccessfully.
The harness refuses actor counts above available processors (or 32), invalid options and oversized workloads.

After database integration, add real single-database and multiple-database workloads using identical datasets,
payloads, concurrency and CPU allocation. Keep uniform and Zipf sweeps, failure/restart correctness checks and
separate service/load-generator CPU/RSS evidence. A hot key remains serialized until TS-649 adds replication policy.
