# TerraScale performance baseline, 2026-10-27

Status: **provisional routing handoff, not a completed baseline or parity proof**.
The three interleaved repeats are running. This early list lets the other performance
workers choose their area before this report's PR merges; final measurements will replace it.

Main source: `9682f0ec3ffb934945dde49c277ccaef6ba08d0a`. Calibration harness:
`d83db80292fd34a529612e627395e2199d8002fb`; final candidate: `eb5d3ed1f`.
Host `mariogk-dev`, Linux 7.3.0 x86_64, 46 logical CPUs, .NET 11 RC1, workstation
concurrent GC and tiered PGO. Run through `ts-heavy-any`, pinned to that host, at nice 19.
Load before the calibration was 13.07/46; CPU PSI some avg10 was 0.60%.
The host is shared; load/CPU-pressure samples are retained, so this is not an exclusive-host claim.

## Biggest gaps vs target — preliminary

These are **one repeat, small payload only**, from the calibration run that was
superseded to fix sample starvation in slow SQL reads. They are planning signals,
not the required three-repeat results. No outlier was discarded to claim parity.

| Rank | Area | Observed calibration cell | Read target | Gap |
| ---: | --- | --- | --- | --- |
| 1 | SQL HTTP / Hrana / PostgreSQL point SELECT | c=1 p50 415–530 ms; SQL/Hrana c=16 finished no measured requests in 5 s | c=1 p50 ≤167 µs | ~2,487–3,174× p50; c=16 percentiles unavailable |
| 2 | Document query | c=1 p99 4,921 µs; c=16 9,160 requests/s | p99 ≤421 µs; ≥101,601 requests/s | 11.68× p99; 11.09× throughput deficit |
| 3 | Document GET | c=16 12,750 requests/s | ≥101,601 requests/s | 7.97× throughput deficit |
| 4 | Wire GET | c=1 p99 932 µs; c=16 59,093 requests/s | p99 ≤421 µs; ≥101,601 requests/s | 2.21× p99; 1.72× throughput deficit |

The SQL lanes use a point predicate through TerraBase's current full-table relational
projection, against 20,000 preloaded rows (10,000 per size). Raw Turso uses an indexed
point read. The scan is a product gap, but the work performed is different; these
ratios do not isolate transport overhead. No service optimization is part of this work.

## Reproducible suite

```sh
TS_HEAVY_HOST=mariogk-dev ts-heavy-any nice -n 19 \
  bash benchmark/scripts/perf-suite.sh benchmark/bench/<new-run-directory> \
  --repo terrascale --branch feat/perf-baseline-suite
```

The entry point verifies the pinned native Turso asset, builds the existing benchmark
host, runs three interleaved repeats, and emits `results.json`, `summary.md`, `local.raw`,
`metadata.json`, `load.jsonl` and the build/runtime log. An existing output directory is refused.
Final cells use 256 B and 4 KiB bodies, c=1/c=16, 1 s warmup, 5 s measurement
(30 s for the slow SQL read lanes), with sample counts and measurement durations in JSON.

Point documents use GET/PUT by key. `api-query` reads one document through a filter and
writes a one-put transaction. `api-batch` queries or atomically puts 16 documents;
its throughput is requests/s, and documents/s is 16 times that value. Exact result
counts are checked. SQL HTTP, Hrana and pooled unprepared Npgsql use a relational
table with the JSON body stored in a text column. They are different API paths.
Authentication, admission, metering, change capture and FULL commits remain on.
Raw Turso, captured Turso, in-process engine and bare TCP echo remain controls.

Standing Q-PARITY **read** targets: c=1 each percentile ≤1.1 × (raw Turso + TCP echo);
c=16 throughput ≥0.9 × min(raw Turso, TCP echo). Applying the Wire budget to HTTP,
query and relational endpoints is an aspirational comparison. Batch requests are
excluded from this one-document ranking. Writes are recorded without inventing a new target.

## Live regional evidence

Production currently serves `9731313c08ed48823823f7355e58dcc22ba8142c` in gru, sjc and fra,
which differs from the local source SHA. Live results are not a same-build local/production comparison.
The runner discovers serving regions from the running service's ts-orch peers file,
uses OCI-Vault-delivered operator credentials only on the host, creates its own tenant,
and verifies project deletion in every region after revoking its key and deleting its database.
SIGTERM runs cleanup. For an abrupt benchmark-process kill or host crash, its persisted
acting-user receipt supports `perf-suite.sh cleanup <live-output-directory> <probe-binary> <source-sha>`
on the same serving host. Recovery is confined to that synthetic user and refuses
unexpected organization/project/database counts. It does not claim cleanup while a host is unavailable.

Live latency uses c=1, 60 reads per endpoint/size/repeat at at most 10 requests/s,
with persistent clients on each serving host. This measures normal service load, not
maximum production throughput. Native clients traverse the local TLS edge; HTTP uses
the local authenticated client listener. Replication uses `xregion-probe`, 60 probes per
direction/repeat in all six directions, with the existing RTT/2 correction and 200 ms p99 target.

Three SJC read repeats completed, followed by successful tenant cleanup. That attempt's
replication binary was x86_64 on ARM64 and did not start; it is excluded from qualification.
The static ARM64 probe is now checked before tenant creation. SJC and FRA have since
completed all three repeats, including both inbound replication directions on each host,
with zero missing probes and successful project deletion in all regions.

| Direction | Probes | p50 ms | Pooled p99 ms | Target p99 <200 ms |
| --- | ---: | ---: | ---: | --- |
| fra → sjc | 180 | 127.515 | 345.612 | not met |
| gru → sjc | 180 | 240.135 | 2,431.839 | not met |
| gru → fra | 180 | 202.543 | 1,107.936 | not met |
| sjc → fra | 180 | 131.725 | 1,443.139 | not met |

The two inbound GRU directions and GRU endpoint latency await trusted NetBird SSH access;
two host-key verification failures were escalated rather than disabling verification.
No repeat/stress runs or workflow changes were added to GitHub CI.
The superseded short-window local calibration and the SJC x86 probe attempt are retained
under `perf-baseline-20261027/diagnostics/` and excluded from qualification.
