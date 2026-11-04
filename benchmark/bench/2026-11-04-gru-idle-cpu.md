# GRU idle CPU investigation, 2026-11-04

The F12 guard's 72.96% observation was a TerraBase cold-start measurement, 23 seconds after a deployment restarted the process. It does not establish sustained idle CPU consumption. Keep benchmark clients stopped until a quiet ten-minute baseline has completed, and start subsequent proofs at least two minutes after the most recent TerraBase restart.

## Evidence

- The retained F12 CPU-stop record samples PID `3073744`, process start ticks `115717099`, at Unix time `1793833562.5214944` (23:06:02.521Z). Its calculation correctly uses the two-core cgroup quota; this was not a lifetime `ps` average.
- `/proc/3073744/stat` and `ps` identify the process start at 23:05:39Z. The service's `updatedAt` is 23:05:49.034Z. The deployed ARM64 image is `ghcr.io/terrascale/terrabase@sha256:5dcc150a76b4194098633e65654552cb6c50b37cfc78ac0e35421d60a7744e28`; its OCI revision label is `d8d25cc1722eddf7abcdfad684cc3ab00d9677c8`.
- The cgroup has `cpu.max = 200000 100000`, with four affinity CPUs. Report CPU as consumed cores divided by **two**, regardless of host affinity.
- By the first retained Prometheus sample after the restart, 23:06:00Z, TerraBase had already started eleven backup generations and passed eleven restore drills. A later low-rate profile (22 samples over ten seconds) still included seven tiered-JIT samples. This supports startup initialization, recovery/backup work and JIT as the cause of the short spike; that profile is too small to apportion the original spike precisely.
- A direct fifteen-second `pidstat` observation after startup measured TerraBase at 0.0973 core. Thirty-second observations measured SJC at 0.0753 core and FRA at 0.0387 core. These are different time windows, not a controlled regional performance comparison.
- Initial GRU measurements still contained SQL/RPC traffic and 5–11 shape reads per thirty seconds. Those intervals are excluded from the strict quiet proof. F10, F12, F9 and the GLM coordinator were asked to hold GRU client traffic; service probes, metering, backups and replication remain running.
- `ts-orch service ls`, `service ps`, `service inspect` and `service logs` established the running task, image, deployment timing and startup activity. `ts-orch usage` could not supply regional history: it reports that authenticated regional read access/foundation discovery is pending. CPU evidence therefore comes directly from the kernel counters rather than that unavailable CLI history.

## Method

Read-only SSH sampling collects cumulative `/proc/<pid>/stat` user/system CPU ticks, the process start tick, `/proc/stat`, every TerraScale container's `cpu.stat`, and the loopback TerraBase `/metrics` counters every thirty seconds. The retained window covers at least 600 seconds. CPU uses the actual monotonic elapsed time, not the nominal sample interval. Reject a window if its TerraBase PID/start tick changes. Check shape-read, flight-start and WebSocket request deltas to detect client-load contamination.

The host reports `CLK_TCK = 100`. For each interval, `consumed cores = delta(utime + stime) / 100 / elapsed_seconds`; quota CPU percent is `consumed cores / 2 * 100`. Container cross-checks use `delta(cpu.stat usage_usec) / 1,000,000 / elapsed_seconds`. Host busy time is `delta(user + nice + system + irq + softirq + steal) / 100 / elapsed_seconds`.

Whole-host busy CPU excludes idle and I/O wait and is also shown against the two-core reference. Embedded NATS/JetStream and the HTTPS edge are part of the orchestrator process, so they cannot be separated as independent containers. The container list has no separate Caddy task. Probe and application polling traffic is retained in the measured CPU; no estimated load is subtracted.

## Quiet proof

**PASS:** 23:15:54.641Z–23:26:05.490Z, **610.849 seconds**, with PID `3073744` and start tick `115717099` unchanged throughout. TerraBase used **0.10050 core**, or **5.025%** of its two-core quota. The largest interval was **5.453%**. The independent container CPU counter agrees at **0.10048 core**.

Whole-host busy CPU averaged **0.19543 core / 9.772%** of the two-core reference; the largest interval was **13.688%**. Every retained interval was below 15% for both TerraBase and the whole host. Shape-read and flight-start counters each advanced **zero**; all shape HTTP/WebSocket request counters also advanced zero. No benchmark client load ran during this window. The final interval is longer because a final independent sample extends the initial sampler beyond ten quiet minutes.

| Process/container | Mean consumed cores |
|---|---:|
| TerraBase | 0.10050 |
| ts-orch main process, including embedded NATS/JetStream and edge | 0.03526 |
| ts-auth | 0.00624 |
| Dashboard (`dotnet`) | 0.00252 |
| NetBird server container | 0.00080 |
| containerd | 0.00591 |
| Prometheus | 0.00614 |

Normal service traffic remained: 3,567 successful HTTP/2 SQL calls, 600 management SQL RPCs, 40 replica catalog reads, 122 usage-month reads, health/readiness/status probes, and platform plan reconciliation. These counts are included in the measured CPU. This is a conservative baseline with normal platform polling, not an assertion that every HTTP request stopped.

| Interval ending UTC | Seconds | TerraBase / 2 CPUs | Host busy / 2 CPUs |
|---|---:|---:|---:|
| 23:16:24 | 30.03 | 5.00% | 9.29% |
| 23:16:54 | 30.03 | 5.18% | 10.37% |
| 23:17:24 | 30.03 | 4.86% | 8.84% |
| 23:17:54 | 30.03 | 5.10% | 9.72% |
| 23:18:24 | 30.03 | 4.88% | 8.79% |
| 23:18:54 | 30.03 | 5.26% | 9.82% |
| 23:19:24 | 30.03 | 4.90% | 13.69% |
| 23:19:54 | 30.03 | 5.10% | 10.87% |
| 23:20:24 | 30.03 | 4.86% | 8.91% |
| 23:20:54 | 30.03 | 5.11% | 9.47% |
| 23:21:24 | 30.03 | 4.93% | 11.51% |
| 23:21:54 | 30.03 | 5.11% | 9.84% |
| 23:22:24 | 30.03 | 4.93% | 8.74% |
| 23:22:55 | 30.03 | 5.03% | 9.34% |
| 23:23:25 | 30.03 | 4.80% | 8.64% |
| 23:23:55 | 30.03 | 5.03% | 9.92% |
| 23:24:25 | 30.03 | 4.91% | 8.66% |
| 23:24:55 | 30.03 | 5.11% | 9.48% |
| 23:25:25 | 30.03 | 4.81% | 8.71% |
| 23:26:05 | 40.35 | 5.45% | 10.53% |

Validation: read-only live measurement and healthy GRU `/healthz`. Report-only changes require no Fast suite or application deployment. The deployed image and source revision did not change during the proof. `baseline ok` was sent to F12 and F10 after the measurement passed; F12 runs first and releases F10 afterward.


## Decision

No runtime or orchestrator configuration change is justified by the observations. The operational correction is to exclude deploy startup and concurrent proof traffic from the idle baseline. F12/F10 benchmark code is unchanged. This report does not claim a code fix or a new deployment. Main CI for the deployed revision is [green](https://github.com/TerraScale/terrascale/actions/runs/41929794650).

Parked: the existing system-database backup warning about missing `_tb_shape_chunk` belongs to the backup/SQL transition lane; it is not evidence of an idle spin.
