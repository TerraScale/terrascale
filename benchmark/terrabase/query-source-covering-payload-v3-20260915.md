# Authenticated query source scan measurement

This report measures the accepted covering-payload verification v3 source with
real authenticated HTTP requests. It is a local data-volume measurement for
the unindexed `StorageEngine` scan path; it does not qualify a production
capacity or multi-process service limit.

## Identity and environment

| Item | Measured identity |
| --- | --- |
| Source root | `.codex/evidence/terrabase-covering-payload-verification-20260915-v3/source` |
| Source manifest | `meta/source-manifest.final.tsv`, SHA-256 `32df9bf4565ce995083bf3288ff5f29f58889f8aaeba0641c5a467d3cc2b2d95` |
| Manifest entries | 985; every path, size, and SHA-256 verified before build and run |
| SDK / declared framework / RID | .NET SDK `10.0.401` / runtimeconfig declares `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` `10.0.0` / `linux-x64`; loaded runtime not captured |
| OS / machine | NixOS 26.05, x86_64, 46 logical host CPUs |
| Artifact | `QuerySourceMeasurement.dll`, SHA-256 `362c52505d0aaf96c07e044f5abb6cde051d172ce388681e606937044cd752f2` |
| Artifact receipt | [`QuerySourceMeasurement.dll.identity.json`](../../.codex/swarm/measurement/query-source-20260915/bin/Release/net10.0/QuerySourceMeasurement.dll.identity.json) |
| CPU execution | `taskset -c 2,3`, `DOTNET_PROCESSOR_COUNT=2`, `nice -n 19` |
| Build properties | serial (`-m:1`, `BuildInParallel=false`), `UseSharedCompilation=false`, analyzers enabled, `AnalysisLevel=latest-recommended`, nullable enabled, warnings as errors |
| Cgroup | `/sys/fs/cgroup/user.slice/user-1000.slice/session-7.scope`; `cpu.max=max 100000`, `memory.max=max`, `memory.high=max`, inherited `cpuset.cpus.effective=0-45` |

The cgroup has no CPU quota or memory ceiling. The affinity restriction to
CPUs 2–3 is therefore the affinity limit for this run, while host-level
contention remains possible. At metadata capture, `cpu.stat` reported
`nr_throttled 0` and `throttled_usec 0`; its other counters are cumulative for
the shared session cgroup. `memory.current` was 20,992,548,864 bytes at that
point.

## Measurement boundary

The harness seeded a fresh path with one direct `StorageEngine.ApplyBatchAsync`
containing exactly 1,000 or 10,000 documents. The matching document was the
last generated ID (`doc-000999` or `doc-009999`); all other documents had the
same body size and a nonmatching partition. No index was registered or
recovered. Each request was:

```text
POST /api/v1/databases/query-perf/collections/items/documents/query
Authorization: Bearer query-perf-token
{"predicates":[{"path":"partition","operator":"equal","value":"match"}],"projection":["name"],"limit":1}
```

The query body is 104 bytes. The measured request includes HTTP send and full
response-body receipt. Warm cases used one host process, three discarded
warmups, and 30 recorded requests. Process-reopen cases launched a new OS
process for each recorded request over the already seeded path; host startup
and seed work were outside the timed request, and the OS page cache was not
evicted. These are `process-cold` results, not OS-cold results.

The build and run commands were:

```sh
cd /home/mariogk/Projects/TerraScale/.codex/swarm/measurement/query-source-20260915
./build-source-matched.sh
./run.sh \
  --artifact "$PWD/bin/Release/net10.0/QuerySourceMeasurement.dll" \
  --output-dir "$PWD/results/full-20260915" \
  --repetitions 30 --warmups 3
```

The strict Release build completed with 0 warnings and 0 errors. The retained
JSONL and metadata are in [`results/full-20260915`](../../.codex/swarm/measurement/query-source-20260915/results/full-20260915), including the
per-process console and stderr files.

## Results

Latency is measured per successful HTTP request. “Actual RPS” is the 30
successful requests divided by the sum of their measured request durations. For
process-reopen, this is the inverse of summed request time per request and does
not include process startup gaps between launches.

| Case | Samples | HTTP 200 | Latency p50 (ms) | p95 (ms) | p99 (ms) | Actual RPS | CPU delta p50 / p95 (ms) | RSS after p50 / max (B) | Peak RSS max (B) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| warm, 1,000 docs | 30 | 30 | 15.828400 | 32.734625 | 42.105313 | 56.784025 | 14.358 / 36.766 | 134,987,776 / 135,168,000 | 135,168,000 |
| warm, 10,000 docs | 30 | 30 | 53.560700 | 178.716055 | 296.448809 | 12.901546 | 55.587 / 299.207 | 177,623,040 / 178,593,792 | 183,787,520 |
| process-cold, 1,000 docs | 30 | 30 | 720.171850 | 974.577730 | 1,027.464886 | 1.323393 | 825.562 / 992.673 | 125,687,808 / 126,332,928 | 126,332,928 |
| process-cold, 10,000 docs | 30 | 30 | 1,810.012250 | 2,630.120070 | 2,720.999063 | 0.531271 | 1,912.829 / 2,482.968 | 183,889,920 / 184,991,744 | 184,991,744 |

The receipt and plan values were constant across all samples:

| Case | Rows examined | Bytes read | Work units | Response bytes | Plan | Returned ID |
| --- | ---: | ---: | ---: | ---: | --- | --- |
| warm, 1,000 docs | 1,000 | 147,000 | 1,002 | 568 | `Scan`, no index, uncovered, no snapshot fallback | `doc-000999` |
| warm, 10,000 docs | 10,000 | 1,470,000 | 10,002 | 571 | `Scan`, no index, uncovered, no snapshot fallback | `doc-009999` |
| process-cold, 1,000 docs | 1,000 | 147,000 | 1,002 | 568 | `Scan`, no index, uncovered, no snapshot fallback | `doc-000999` |
| process-cold, 10,000 docs | 10,000 | 1,470,000 | 10,002 | 571 | `Scan`, no index, uncovered, no snapshot fallback | `doc-009999` |

The 10,000-to-1,000 data-volume ratios were:

| Phase | Latency p50 | Latency p95 | Actual RPS ratio | Rows | Bytes | Work units |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| warm | 3.383835x | 5.459542x | 0.227204x | 10.0x | 10.0x | 9.982036x |
| process-cold | 2.513306x | 2.698728x | 0.401446x | 10.0x | 10.0x | 9.982036x |

## Failures and omissions

All 120 recorded requests were successful HTTP 200 responses. There were 0
query budget failures, 0 non-200 responses, 0 incomplete receipt records, and
0 samples with an unconfirmed source path. `work.pagesRead` was JSON `null` in
all 120 samples because this source does not expose a qualified page counter.
Per-query allocations were `null` in all 120 samples because the HTTP
boundary cannot isolate server async-thread allocations. The source commit
sequence was 1 in all samples. The artifact/source identity object was
identical in all 120 samples.

The 62 measurement process console files contain normal host lifecycle logs;
each has one `BackgroundService failed` error and one critical stop-host log
from cancellation of the periodic runtime metrics sampler during disposal.
They occur after the timed request. All 62 stderr files are empty, and no
request failure or budget omission resulted. This shutdown logging remains a
runtime observation to resolve before treating the harness as a clean service
qualification.

The retained failed attempts are [`smoke2-20260915`](../../.codex/swarm/measurement/query-source-20260915/results/smoke2-20260915), which records the wrapper argument failure, and the earlier source-root gate rejection in the run transcript. The successful one-sample smoke is [`smoke3-20260915`](../../.codex/swarm/measurement/query-source-20260915/results/smoke3-20260915).

## Bottleneck and scaling interpretation

Measured evidence locates the work at the source scan over the storage map:
rows examined, bytes read, and work units all scale approximately 10x when
document count scales 10x, while the query remains an unindexed, uncovered
`Scan`. Warm p50 latency rises 3.38x and process-cold p50 rises 2.51x. The
working hypothesis is that scan work contributes to this latency scaling; it is
not a proven dominant latency bottleneck without a profile. CPU and RSS are
process-wide observations that include the HTTP client, host, and background
runtime, so they cannot isolate a specific internal method. Confidence is high
that the measured work is the intended source scan and medium for its causal
latency attribution.

This run measured data-volume scaling only. It did not measure horizontal
replicas, concurrent clients, or vertical CPU/memory scaling. The fixed
two-CPU affinity and absent cgroup ceilings prevent a capacity claim; host
contention and storage state remain confounders.

The smallest next measurement is a short `dotnet-trace` CPU/lock capture of a
warm 10,000-document run under the same artifact, affinity, and environment.
That would separate scan, HTTP, and runtime scheduling cost without changing
the product. An indexed control query and an enforced memory/CPU cgroup would
be separate measurements, not assumptions from these results.
