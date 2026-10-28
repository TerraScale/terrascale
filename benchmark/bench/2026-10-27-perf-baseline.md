# TerraScale performance baseline, 2026-10-27

**Complete measured baseline; no optimization or speedup claim.** All 88 local cells completed three interleaved repeats (264 measured cells) with zero operation errors. Live evidence covers all three regions, seven read surfaces, and all six replication directions. Every replication direction missed the 200 ms p99 target.

Local service-code base (main): `9682f0ec3ffb934945dde49c277ccaef6ba08d0a`. Measured harness: `58fae77420428e3c2b576ff9a62913d0b3a97abc`, retained by tag `bench/perf-baseline-2026-10-27`.
Production serving revision: `64fdad763ce4d777e665dd606cdf7e9672d0deca`. It differs from the local base; live latency is not a same-build comparison with the local fixture.

## Biggest gaps vs target

Ratios normalize different budgets to rank investigation areas; they are not speedup measurements. SQL HTTP, Hrana and PostgreSQL dominate the local ranking.

| Rank | Area | Measured cell | Target | Gap |
| ---: | --- | --- | --- | ---: |
| 1 | SQL HTTP / Hrana / PostgreSQL | sql small c=16 throughput: 4.167 ops/s | ≥116,605.620 ops/s | 27,983.11× |
| 2 | Document query | api-query small c=1 p99: 3,741.800 µs | ≤354.420 µs | 10.56× |
| 3 | Replication lag | sjc->gru pooled p99: 1,705.507 ms | <200 ms | 8.53× |
| 4 | Document GET | api small c=16 throughput: 21,057.400 ops/s | ≥116,605.620 ops/s | 5.54× |
| 5 | Wire | wire medium c=16 throughput: 31,653.000 ops/s | ≥77,359.860 ops/s | 2.44× |

Q-PARITY read budgets: c=1 each percentile ≤1.1 × (raw Turso + TCP echo); c=16 throughput ≥0.9 × min(raw Turso, TCP echo). Applying the Wire budget to HTTP/query/SQL is aspirational. Batch requests contain 16 documents and are excluded from the one-document ranking. Writes have no invented target.
SQL point SELECT currently scans the relational projection over 20,000 preloaded rows (10,000 per payload). Raw Turso uses an indexed point read. Their work differs; these gaps do not isolate transport overhead. The complete ranked cells are in [the local table](perf-baseline-20261027/local/summary.md).

## Host and method

Host `mariogk-dev`, Linux-7.3.0-x86_64-with-glibc2.42, 46 logical CPUs, Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz, SDK `11.0.100-rc.1.26425.128`. Production workstation concurrent GC and tiered PGO; actual runtime/cgroup settings are in [runtime evidence](perf-baseline-20261027/local/runtime.json).
Ran at nice 19 through ts-heavy-any. Admission at `2026-10-28T00:08:41.017377+00:00` reserved all four scheduler slots through completion; starting load 11.04/46, CPU PSI some avg10 0.01%.
All 551 load/pressure observations are retained, including build and preload: one-minute load median/max 5.03/20.52; CPU PSI some avg10 median/max 0.12%/16.77%.
Quietness is checked at admission and other scheduled heavy work is excluded. Ordinary host activity is not excluded; inspect load and control spreads before attributing a delta. A previous completed run was rejected for contention (load max 68.71/46; CPU PSI max 49.90%; TCP control collapse). No outlier from it was discarded to claim parity.
Local cells: 256 B and 4 KiB JSON, c=1 and c=16, one-second warmup, five-second measurement except 30-second SQL read cells, three interleaved repeats. All sample counts/durations and repeat results are retained in [local JSON](perf-baseline-20261027/local/results.json). Percentiles below are medians of per-repeat percentiles; these are closed-loop measurements.
Point documents use GET/PUT. Query uses one-document filtering and a one-put transaction. Batch reads/query and atomic writes contain 16 documents; throughput counts requests. SQL HTTP, Hrana and pooled unprepared Npgsql store JSON text in the same relational table. Authentication, admission, metering, change capture, FULL commits and local acknowledgements remain enabled. Raw/captured Turso, engine and TCP echo remain controls.

## Local headline cells

All rates count requests/s; batch documents/s is 16× the request rate. TCP echo is a transport control, without database durability. The [full 88-cell table](perf-baseline-20261027/local/summary.md) includes repeat ranges, counts and every control.

| Lane | Payload | Read c=1 p50 / p99 µs | Read c=16 ops/s | Write c=1 ops/s | Write c=16 ops/s |
| --- | --- | ---: | ---: | ---: | ---: |
| api | 256 B | 475.4 / 1,087.4 | 21,057.4 | 179.0 | 698.0 |
| api-query | 256 B | 1,365.8 / 3,741.8 | 14,068.8 | 174.2 | 675.2 |
| api-batch | 256 B | 3,000.9 / 5,867.8 | 1,442.4 | 62.6 | 86.2 |
| sql | 256 B | 410,999.2 / 486,938.8 | 4.2 | 140.2 | 281.0 |
| hrana | 256 B | 409,926.1 / 459,496.8 | 4.2 | 137.4 | 275.0 |
| pg | 256 B | 418,549.6 / 660,782.6 | 4.5 | 156.4 | 290.8 |
| wire | 256 B | 134.1 / 557.4 | 70,814.2 | 192.6 | 674.6 |
| turso | 256 B | 82.1 / 147.0 | 151,192.4 | 1,025.8 | 962.8 |
| tcp-echo | 256 B | 66.1 / 175.2 | 129,561.8 | 10,941.4 | 135,631.6 |
| api | 4 KiB | 528.3 / 2,062.0 | 15,002.2 | 134.4 | 588.4 |
| api-query | 4 KiB | 1,455.9 / 3,640.2 | 10,491.6 | 147.2 | 566.4 |
| api-batch | 4 KiB | 3,606.6 / 9,882.6 | 657.2 | 45.8 | 60.8 |
| sql | 4 KiB | 403,928.1 / 487,660.0 | 4.1 | 127.0 | 251.0 |
| hrana | 4 KiB | 410,768.6 / 545,090.3 | 4.4 | 124.8 | 254.2 |
| pg | 4 KiB | 413,114.2 / 706,623.7 | 3.5 | 148.2 | 275.0 |
| wire | 4 KiB | 206.8 / 786.2 | 31,653.0 | 184.6 | 592.0 |
| turso | 4 KiB | 108.4 / 188.5 | 85,955.4 | 834.8 | 921.6 |
| tcp-echo | 4 KiB | 65.5 / 185.4 | 121,919.2 | 8,266.8 | 59,992.8 |

## Live production evidence

The same five protocols plus document query and 16-document batch reads ran in gru/sjc/fra. Each host owns a synthetic tenant, with 16 documents per payload; this is a small live dataset, unlike the local 20,000-row SQL fixture. Three repeats of 60 reads per surface/payload give 7,560 samples. Clients are persistent and c=1, paced at most 10 requests/s. HTTP uses the host-local authenticated client listener; native protocols use standard local TLS edges (PostgreSQL 5432, Wire 7464). These are host-local latencies under observed production load, not WAN latency or capacity. [Regional p50/p99 tables](perf-baseline-20261027/live-summary.md) retain all repeat maxima.
Nearest-rank pooled p99 is the second-largest of 180 samples; a 60-sample repeat p99 equals its maximum. These are descriptive tails, not confidence bounds. Peers-file discovery and readiness responses identify serving regions and unchanged revisions. Caller source hints, staged-file hashes and serving revisions are distinct in [live JSON](perf-baseline-20261027/live-results.json).

Replication uses the existing warmed xregion-probe with RTT/2 correction: three repeats of 60 probes per direction, zero missing probes.

| Direction | N | p50 ms | Pooled p99 ms | Target p99 <200 ms |
| --- | ---: | ---: | ---: | --- |
| fra->gru | 180 | 233.462 | 1,597.094 | NOT MET |
| fra->sjc | 180 | 160.462 | 1,611.384 | NOT MET |
| gru->fra | 180 | 229.824 | 617.958 | NOT MET |
| gru->sjc | 180 | 214.104 | 912.471 | NOT MET |
| sjc->fra | 180 | 168.582 | 1,310.032 | NOT MET |
| sjc->gru | 180 | 214.648 | 1,705.507 | NOT MET |

Every tenant key was revoked and its database/project/organization deleted; regional absence receipts are required by aggregation. Credentials come from OCI Vault delivery, remain in private tmpfs, and are removed afterward. [Lifecycle proof](perf-baseline-20261027/diagnostics/lifecycle/proof.json) covers group-wide TERM, SIGKILL followed by recovery, and an injected lost organization-create response in an isolated test harness. Recovery is scoped to the persisted synthetic user; its receipt is ignored by git.
Excluded diagnostics are retained locally under diagnostics/: short-window calibration, contended r2, older live runs, and the initial wrong-architecture probe attempt. They are not qualification evidence.

## Reproduce and compare

```sh
TS_HEAVY_HOST=mariogk-dev ts-heavy-any nice -n 19 \
  bash benchmark/scripts/perf-suite.sh benchmark/bench/<new-run> \
  --repo terrascale --branch <branch-containing-suite>
```

The entry point refuses an existing output directory, reserves a quiet scheduler host, verifies pinned native assets and emits JSON plus Markdown. Use the same host for comparisons. The measured local source is retained by the archive tag above; current main contains subsequent cancellation/provenance hardening without service optimization. [Usage](2026-10-27-perf-suite-usage.md) covers live runs, crash recovery and comparison against a saved results.json.
Comparisons require complete zero-error repeats, host reservation, matching host/CPU/SDK and measurement durations. Deltas alone do not establish statistical significance. No repeat/stress runs or workflow changes were added to GitHub CI.
