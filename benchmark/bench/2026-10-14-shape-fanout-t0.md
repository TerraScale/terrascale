# TerraBase FR-20 shape fan-out T0 — 2026-10-14

## Status

**INVALID for `shape.fanout`. No shape row was measured.** `changes.fanout` completed all four steps (N = 1, 10, 100, 1000).

- Failure: `shape.fanout N=1: metrics: terrabase_storage_reads sample without a bounded path label: "terrabase_storage_reads_total{otel_scope_name=\"TerraScale.TerraBase\",otel_metric_overflow=\"true\"} 119"`
- Exact cause: `terrabase.storage.reads` has `CardinalityLimit = 4` (`src/TerraBase/Hosting/Telemetry.cs:133`). The product now records five distinct `path` values: `feed`, `point`, `query`, `shape`, `shape_body` (`CountRead(...)` call sites in `src/TerraBase`). The fifth distinct set overflowed into `otel_metric_overflow="true"`. The harness correctly refused that sample.
- Completed N: changes 1, 10, 100, 1000; shape 0 of 4.
- Not retried: the cause is deterministic in the product code, not external flakiness. Root must fix the limit, then rerun.

## Provenance

| field | before run | after run |
| --- | --- | --- |
| Measured HEAD | `ca231cfa13a47f14957794e4e55cdd4e6fc87370` (binary source, checked at run start) | same |
| Last product/host commit (`src`, `tests`) | `49a8a34bdc6e980ce275cb3ec667c1ff702cd14d` (from run JSON notes) | same |
| Last harness commit | `ca231cfa13a47f14957794e4e55cdd4e6fc87370` | same |
| Working tree | clean (0 changed paths) | 4 new files under `benchmark/bench/` from this run only |
| Run ID | — | `01M4WJX8ZPZB525GSRYJD6X0ZE` |
| Run interval (UTC) | — | 2026-10-14T06:47:04Z to 06:52:00Z (4 min 56 s wall) |
| Host | `delta`, Linux 7.2.10 | same |
| CPU | 13th Gen Intel Core i7-13620H, 16 logical CPUs | same |
| RAM | 33,335,537,664 bytes total | same |
| Available RAM | 22,404,272,128 bytes | 22,366,617,600 bytes |
| Load 1 / 5 / 15 min | 6.61 / 11.77 / 13.54 | 17.91 / 11.01 / 12.03 |
| Top CPU process at capture | VBCSCompiler (~297%) and several MSBuild nodes from other work | one process at ~1255% CPU from other work |
| Toolchain the measuring binary used | Go `go1.25.0` (`go.mod` says `go 1.25.0`; ran from `benchmark/`) | same |
| Run JSON `ready.runtime` | `go1.25.0` | — |
| .NET SDK | `11.0.100-rc.1.26425.128` | same |
| ts-dotnet checkout | `bcd20603f11f3aa54dc54cec6e410b4fe9e24151` | same |
| Scheduling | `ts-heavy` slot 1, `nice -n 19`; shared host, no CPU isolation | same |

**Go version caveat.** The repository root `go version` reports `go1.27.2`. That is not the toolchain that measured this run. The harness ran from `benchmark/`, resolved to Go 1.25.0, and the run JSON records `go1.25.0`. Earlier draft text citing 1.27.2 is stale.

Raw snapshots: [before](2026-10-14-fr20-machine-r9-before.json), [after](2026-10-14-fr20-machine-r9-after.json).

The host was shared throughout. Other agents' compiler and MSBuild work was active before and after the run, and the after snapshot caught a ~1255% CPU process. CPU and latency numbers below are not from a quiet machine. This run is one sample, not repeated trials.

Raw evidence: [generated report](2026-10-14-delta-01M4WJX8ZPZB525GSRYJD6X0ZE.md), [run JSON](2026-10-14-delta-01M4WJX8ZPZB525GSRYJD6X0ZE.json).

## changes.fanout results (measured, gru)

Writes: 600 in-window per step, 20/s, 256-byte documents. All admitted followers delivered every in-window write.

| requested N | admitted | rejected (`changes_busy`) | in-window writes | delivered | missed | p50 ms | p90 ms | p99 ms | max ms | CPU % (one core = 100) | peak RSS MiB | SSE body bytes in 30 s | readiness (open feed streams at ready) | retries |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | 0 | 600 | 600 | 0 | 12.26 | 15.65 | 21.15 | 116.80 | 71.1 | 256.3 | 329,872 | 1 | 0 |
| 10 | 10 | 0 | 600 | 6,000 | 0 | 12.33 | 17.89 | 20.77 | 88.64 | 70.4 | 253.1 | 3,298,720 | 10 | 0 |
| 100 | 32 | 68 | 600 | 19,200 | 0 | 22.34 | 53.82 | 224.38 | 474.11 | 106.3 | 282.8 | 10,555,904 | 32 | 0 |
| 1000 | 32 | 968 | 600 | 19,200 | 0 | 21.92 | 31.73 | 40.64 | 68.67 | 142.2 | 263.7 | 10,555,904 | 32 | 0 |

Delivery lag runs from the write's send timestamp to the follower reading its event. It includes the write request and storage path, not commit acknowledgement. The 68 and 968 rejections are measured capacity rows (`changes_busy`, the 32-stream per-database cap). They are not stopped rows.

Storage reads (`terrabase.storage.reads{path="feed"}`, gru only, after minus before for each step, includes admission, warmup and grace):

| requested N | feed reads delta | inferred min doc SELECTs (source estimate, not measured) |
| --- | --- | --- |
| 1 | 2,106 | 600 |
| 10 | 21,120 | 6,000 |
| 100 | 67,172 | 19,200 |
| 1000 | 68,000 | 19,200 |

These are attempted SELECT executions including misses and page-cache hits. They are not disk I/O.

Other read paths: `point` is node-wide and includes the writer's point reads. It is not attributable to followers. Raw before/after lines for `point` and `query` are in the generated report. Accounting reads delta is 0 for every row.

## shape.fanout results

None. The step failed before its first window. Every shape field is n/a: admitted, rejected, writes, delivered, missed, latency, CPU, RSS, read paths, point writer, readiness and retries. The shape target has not been measured in this run.

## Baseline comparison (FR-17 instrumented run, not a same-machine result)

Baseline: run `01M4VTW4KTKF2VMVNQRG89034K` (JSON `2026-10-13-mariogk-dev-01M4VTW4KTKF2VMVNQRG89034K.json`), Intel Xeon E5-2660 v4 at 2.00 GHz, 46 logical CPUs, measured at `cc89673676657f9af9e3d9fff920e0a47b248411`.

This run is on different hardware (16 logical CPUs, i7-13620H) under different load. **These numbers do not establish performance improvement or regression.** They show what changed on the same workload only.

| N | baseline feed reads | this run | baseline p50 ms | this run p50 ms | baseline p99 ms | this run p99 ms | baseline CPU s | this run CPU s | baseline RSS MiB | this run RSS MiB |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 2,098 | 2,106 | 22.4 | 12.3 | 78.0 | 21.2 | 31.5 | 21.3 | 258.7 | 256.3 |
| 10 | 20,888 | 21,120 | 31.3 | 12.3 | 165.2 | 20.8 | 36.2 | 21.1 | 251.1 | 253.1 |
| 100 | 66,764 | 67,172 | 44.7 | 22.3 | 245.1 | 224.4 | 51.3 | 31.9 | 274.8 | 282.8 |
| 1000 | 67,026 | 68,000 | 46.0 | 21.9 | 378.6 | 40.6 | 50.6 | 42.7 | 274.6 | 263.7 |

Baseline latency is from the run JSON `latency_us`, converted to ms. The baseline p50 and p99 values come from its JSON, not its Markdown table.

Reads are within 0.4–1.4% of baseline at every N. Baseline and this run share the same N=1..1000 steps, admission cap (32), rejection counts (68 and 968) and 600 writes per step.

## Next

Root: raise `terrabase.storage.reads` `CardinalityLimit` in `src/TerraBase/Hosting/Telemetry.cs:133` to cover five paths plus the overflow series (6 or more). Then rerun `go run ./cmd/shape-bench` once from `benchmark/` through `ts-heavy`.
