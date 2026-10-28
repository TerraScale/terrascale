# Reproducing the performance suite

Run the local suite on a quiet development host through the heavy-work scheduler:

```sh
TS_HEAVY_HOST=mariogk-dev ts-heavy-any nice -n 19 \
  bash benchmark/scripts/perf-suite.sh benchmark/bench/<new-run> \
  --repo terrascale --branch <benchmark-branch>
```

Use the same host for subsequent regression measurements. The fixed matrix is
three interleaved repeats, 256 B/4 KiB, c=1/16, reads and writes through documents,
document query/batch, SQL HTTP, PostgreSQL, Hrana and Wire. Raw/captured Turso,
the in-process engine and TCP echo are controls. Each cell warms for one second
and measures five seconds; SQL read cells measure 30 seconds to obtain samples.
Allow roughly an hour including preload. JSON records sample counts and durations.

For production, build `benchmark/cmd/xregion-probe` for the serving host's CPU
through `ts-heavy-any`; for ARM64 use `GOOS=linux GOARCH=arm64 CGO_ENABLED=0`.
Stage the checkout's `benchmark/scripts/perf*`, that executable, and
`scripts/terrabase/endpoint-replication-proof.py` with `ts-orch netbird ssh`.
On each serving host run the entry point below, using a fresh output directory:

```sh
nice -n 19 bash benchmark/scripts/perf-suite.sh live <output> <probe> <full-harness-source-sha>
```

Serving regions come from the running service's peers file; credentials come from
its OCI Vault delivery and remain in a private tmpfs directory. A per-output lock
prevents overlapping cleanup; recovery removes that output's stale credential directory.
The caller's full source SHA is a provenance hint; serving revisions come from
readiness responses, and staged harness/probe SHA-256 hashes are recorded separately.
Each host runs
three bounded read repeats and both inbound replication directions. This measures
host-local latency under observed production load, rather than WAN latency or
maximum throughput. Pooled p99 uses 180 samples; per-repeat p99 equals the maximum
of 60 samples. These are descriptive tails, not confidence bounds.
The synthetic tenant is deleted afterward, with organization/project/database
absence checked in every region. For recovery after
an abrupt process kill, rerun with `cleanup` replacing `live`, preserving `<output>/user`.
Native connections use the host's standard TLS edge ports, PostgreSQL 5432 and
Wire 7464, recorded in metadata; no private `native-relays.json` is required.
Identity receipts and output locks are ignored by git.

Collect regional output directories as `<evidence>/<region>/`, and the local output
as `<evidence>/local/`. Generate JSON and Markdown, optionally comparing a prior run:

```sh
nice -n 19 bash benchmark/scripts/perf-suite.sh report <evidence> [<baseline>/results.json]
```

Incomplete/error results fail validation. Comparisons require matching host, CPU,
SDK, cell matrix and durations. Inspect repeat spreads and load/pressure evidence
before interpreting a delta. The suite adds no CI repeat/stress job.
