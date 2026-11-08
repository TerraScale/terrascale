# TerraScale SQL benchmarks

The maintained tools measure SQL replication, table-shape subscriptions and
endpoint and logging overhead. Results and raw measurement evidence belong in
[`bench/`](bench/). Historical results describe the revision and environment
measured; they do not qualify current capacity.

Run builds, checks and measurements on a dev host (delta, mariogk-dev, beta or rafael-dev).
`make -C benchmark check` builds the Go SQL tools, checks their
formatting and runs the Fast suite. `make benchmark-build` also builds the
.NET endpoint and logging benchmarks.

- [`cmd/replica-lag`](cmd/replica-lag/main.go) measures SQL write acknowledgement
  to replica visibility, optionally timing replication notices.
- [`cmd/insert-bench`](cmd/insert-bench/main.go) measures batched multi-row INSERT
  throughput of one local TerraBase node over SQL REST and the Postgres wire
  (`make -C benchmark insert-batch`).
- `region-sim` drives a local SQL cluster with the real CLI.
- `shape-tails` measures SQL subscription delivery.
- `overlay-proof` proves sjc provisional, confirmed
  and revert states; [`cmd/overlay-bench`](cmd/overlay-bench/main.go) measures
  forwarded-write visibility on a region's live shape tail.
- [`orm-matrix`](orm-matrix/) exercises PostgreSQL clients and ORMs.
- [`dotnet`](dotnet/README.md) holds endpoint, logging and engine diagnostics.

Credentials stay in stdin or private files and never enter reports or logs.
Live runs own and clean up their synthetic accounts and databases.

## F10 SQL shape free reads

[`scripts/free-reads.mjs`](scripts/free-reads.mjs) uses the JavaScript SDK's
table-shape subscriptions against the production CDN. It measures table and
filtered shapes at 1, 10, 100 and 1,000 listeners, with three repeats per cell.
After F12, identical listeners on one SDK client share one upstream stream;
this workload does not qualify independent-client fan-out.
Run it on a dev host after installing the locked `sdk/js` dependencies.
The host needs its existing regional SSH access and a mode-0600 stdin
file containing a throwaway account's `project` and `apiKey`. The key needs
`sql:read`, `sql:write`, `databases:read` and `databases:write`; never pass it
in arguments. An optional `completed` array contains cohort names already
proven by retained receipts when resuming a stopped run.

The driver stops on gru TerraBase CPU above 70% of its cgroup/affinity
capacity, or unavailable process/capacity observations. A
180-second cohort deadline records incomplete reader delivery without
reporting percentiles from the successful subset. The origin control plane
and live tail remain part of request accounting.

[`scripts/free-reads-analytics.py`](scripts/free-reads-analytics.py) reads a
Cloudflare analytics token from stdin and queries R2 operations for each
recorded reader window. Pass it cleaned JSON-object records, then pass both
receipt files to [`scripts/free-reads-report.py`](scripts/free-reads-report.py).
Unavailable analytics remain unavailable. The method, limits and results are
in `bench/2026-11-04-f10-free-reads.md`.
The F12 rerun and CPU comparison are in
`bench/2026-11-04-f10-post-f12.md`; its
[`report generator`](scripts/free-reads-post-f12-report.py) takes the new
cohorts, original cohorts, new R2 receipts and live deployment receipts in
that order. The rerun calls `runFreeReads({ holdMs: 2000 })` from the shipped
driver, retaining a two-second idle tail before each measured update.

## Cloud benchmark boundary

Production runs on OCI under `ts-orch`. Region codes map to OCI regions: `gru` → `sa-saopaulo-1`, `sjc` → `us-sanjose-1`, `fra` → `eu-frankfurt-1`. CI uses GitHub-hosted runners and GHCR.

Production secrets belong in OCI Vault; authorized hosts read narrowly granted secrets with instance principals. Do not copy production credentials into benchmark reports or target files. See the orchestrator's [secrets contract](https://github.com/TerraScale/ts-orchestrator/blob/main/docs/secrets.md).

Local replication runs are local evidence. Cloud runs must record verified per-region endpoints and measured host limits.

## Two-region replica simulation

region-sim starts a gru home node and an sjc replica node locally and drives them with the
real `ts` CLI. It reports pass/fail and replica lag for each scenario (`make region-sim`).
