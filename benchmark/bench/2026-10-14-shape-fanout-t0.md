# TerraBase FR-20 shape fan-out T0 — 2026-10-14 (latest run r13)

## 1. Status

- **Changes fan-out: VALID, four rows** (N = 1, 10, 100, 1000). Zero missed deliveries, zero errors, no `otel_metric_overflow` series.
- **Shape fan-out: INVALID. No measured rows.** The writer failed during the first shape step; that step returned no complete measurement row.
- Measured HEAD: `a2a7ed906d5bd6a615e5e0997b604cbd859a7091`.
- Run ID: `01M4WNQJTC4E1ZPV0ZJ49R8219`. Interval: 2026-10-14T07:36:18Z to 07:41:19Z.
- Failure, verbatim from the run log (`/tmp/fr20-run-r13.log`):
  `shape.fanout N=1: write: HTTP 503 {"error":{"code":"write_outcome_unknown","message":"no other region confirmed the write within 10 s; its outcome is unknown","region":"gru","machine":"3e1be4a417ac61"}}`
- **Cause: unknown.** The 503 means the write's outcome is unknown: no other placed region confirmed it within 10 s. The run does not show whether the peer was slow, unreachable, or the write committed. The writer is the shape step's PUT.
- The failure happened after the last changes window started (07:39:48Z) and before the run ended (07:41:19Z).
- **Not caused by the storage-read label limit.** That was the r9 failure and is fixed (section 9).
- **Not attributed to host load.** Load was high (section 2), but nothing in this run ties it to the 10 s timeout.

## 2. Provenance

| field | before run | after run |
| --- | --- | --- |
| Measured HEAD | `a2a7ed906d5bd6a615e5e0997b604cbd859a7091` | same |
| Last harness commit (`benchmark/cmd/shape-bench`) | `ca231cfa13a47f14957794e4e55cdd4e6fc87370` | same |
| Working tree | clean (0 changed paths) | 2 untracked files: this run's raw artifacts |
| ts-dotnet commit | `bcd20603f11f3aa54dc54cec6e410b4fe9e24151` | same |
| .NET SDK | `11.0.100-rc.1.26425.128` | same |
| Go toolchain for the run | `go1.25.0` (ran from `benchmark/`) | same |
| Host | `delta`, Linux 7.2.10, i7-13620H, 16 logical CPUs, 33.3 GB RAM | same |
| Available RAM | 18.1 GB | 16.3 GB |
| Load 1 / 5 / 15 min | 35.70 / 29.44 / 21.95 | 28.87 / 24.23 / 21.93 |
| Top CPU at capture | VBCSCompiler 502%, mise 400%, ps 200%, TerraBase.Fast 178% | VBCSCompiler 357%, ps 300%, TerraBase.Fast 220% |
| Snapshots | [before](2026-10-14-fr20-machine-r13-before.json) | [after](2026-10-14-fr20-machine-r13-after.json) |

**Shared host.** Load was about 2.2x the logical CPU count (35.70 / 16) before the run and about 1.8x after. The host was not quiet. Latency and CPU numbers here are one sample, not repeated trials, and are not a quiet-machine result.

**Code state.** The measured binary is `a2a7ed906`. Uncommitted edits in the working tree now (`src/TerraBase/Features/Shapes/*`, `tests/TerraBase.Fast/Shapes/ShapeEndToEndTests.cs`, `docs/terrabase/shape-reads.md`, `benchmark/cmd/shape-bench/main.go`) were made after this run. This run does not measure them.

## 3. Key changes and shape table (measured, gru)

### Changes fan-out

Writes: 600 in-window per step, 20/s, 256-byte documents. Every admitted follower received every in-window write. Source: [generated report](2026-10-14-delta-01M4WNQJTC4E1ZPV0ZJ49R8219.md), [run JSON](2026-10-14-delta-01M4WNQJTC4E1ZPV0ZJ49R8219.json).

| requested N | admitted | rejected (`changes_busy`) | delivered | missed | p50 ms | p90 ms | p99 ms | max ms | CPU % | RSS MiB | MB in | req/s |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | 0 | 600 | 0 | 16.20 | 1377.28 | 2390.01 | 2639.87 | 46.3 | 268.8 | 0.31 | 20 |
| 10 | 10 | 0 | 6,000 | 0 | 53.85 | 866.82 | 1868.80 | 2119.68 | 59.3 | 274.9 | 3.15 | 200 |
| 100 | 32 | 68 | 19,200 | 0 | 53.50 | 802.30 | 2283.52 | 2643.97 | 85.8 | 303.1 | 10.07 | 640 |
| 1000 | 32 | 968 | 19,200 | 0 | 13.38 | 593.92 | 2877.44 | 3174.40 | 77.1 | 317.0 | 10.07 | 640 |

- Latency runs from the write's send time to the follower reading its event. It includes the write and storage path, not commit acknowledgement.
- The 68 and 968 rejections are capacity rows: `changes_busy`, the 32-stream per-database cap. Only the 32 admitted followers are measured.
- **Tail latency changed a lot from r9.** At N=1, p99 was 21 ms in r9 and 2390 ms in r13, on the same workload. This run does not establish the cause. Shared host load is one candidate, not proven.

Storage reads per step (gru, `terrabase.storage.reads`, after minus before, from the raw metric lines):

| requested N | feed (measured, = `storage_reads_delta`) | point (node-wide, includes writer) | query |
| --- | --- | --- | --- |
| 1 | 1,676 | 7,973 | 15 |
| 10 | 18,350 | 8,019 | 15 |
| 100 | 58,840 | 8,781 | 18 |
| 1000 | 63,406 | 16,331 | 36 |

- `feed` includes feed metadata, prefix, retention and document-body SELECTs; its delta covers the full step.
- `point` and `query` are node-wide. They include the writer's reads and are not attributable to followers.
- Counts include zero-row reads and page-cache hits. They are not disk I/O.
- `accounting_reads_delta` is 0 for every row.

### Shape fan-out

**No complete row measured.** The writer failed during the first step (section 1), so every shape field is n/a: admitted, rejected, writes, delivered, missed, latency, CPU, RSS, reads, readiness and retries.

**Path meanings, for the next run.** These are the labels the shape target will report, as written in the shape-bench source. Their values are not measured here:

- `shape`: metadata and proof SQL.
- `shape_body`: payload-body SELECTs.
- `point`: the writer's document checks, node-wide.

The fresh-body shape proof path (`ShapeResume` / `ShapePrefix`) computes retained-feed hashes per origin. No measured row in this run covers it, so no reads, CPU or time are attributed to it.

## 4. Baseline comparison

Not computed for this run. The changes rows are on different hardware (16 logical CPUs, i7-13620H, loaded) than the FR-17 baseline. Baseline file: [2026-10-13-shape-fanout-baseline.md](2026-10-13-shape-fanout-baseline.md) (Xeon E5-2660 v4, 46 CPUs). **No improvement or regression claim.** The shape baseline has nothing to compare against.

## 5. Reproduce and validation

**Run (form used; the exact r13 shell line was not saved, so this is the form, not a copied command):**

```sh
cd /home/mariogk/.t3/worktrees/terrascale/feat-repl-fr20-shape-wireup/benchmark
nice -n 19 ~/.local/bin/ts-heavy go run ./cmd/shape-bench > /tmp/fr20-run-r13.log 2>&1
```

**Validation status:**

| check | result |
| --- | --- |
| `go test ./cmd/shape-bench` (root) | passed |
| `make terrabase-build CONFIGURATION=Release` (r13 log `/tmp/fr20-release-r13.log`) | passed: 0 warnings, 0 errors |
| HTTP shape end-to-end tests after page collapsing (`make terrabase-test-fast CONFIGURATION=Release` with `ShapeEndToEndTests` filter) | passed: 13/13; includes four-reader single-payload page assertion |
| Live cursor / HTTP organization isolation after page collapsing | passed: 1/1 and 2/2 |
| Shape benchmark rows | **not measured** (section 1) |

The HTTP tests validate functionality; they do not substitute for missing fan-out measurement rows.

## 6. Raw metric lines

The generated report includes raw `terrabase_storage_reads_total` lines before and after each changes step. Source: [generated report](2026-10-14-delta-01M4WNQJTC4E1ZPV0ZJ49R8219.md), section "Raw metric lines".

- Overflow check: `grep -c otel_metric_overflow` on the generated report returns 0.
- Shape N=1 has no completed before/after scrape pair; the writer failed before the final scrape.
- **Known wording error in the raw report.** The generated line `shape.fanout N=1: no window, so no after scrape (the step stopped at a capacity limit before it).` is **incorrect**. The step did not stop at a capacity limit. It failed on `write_outcome_unknown` (section 1). The generator wording fix postdates this run (`benchmark/cmd/shape-bench/main.go:273`). The raw report still has the old line.

## 7. Raw machine snapshots

- [before](2026-10-14-fr20-machine-r13-before.json)
- [after](2026-10-14-fr20-machine-r13-after.json)

## 8. Raw shared report JSON

- [2026-10-14-delta-01M4WNQJTC4E1ZPV0ZJ49R8219.json](2026-10-14-delta-01M4WNQJTC4E1ZPV0ZJ49R8219.json), written by `apibench.WriteReport`.

## Prior run and fix

- **r9 (invalid, changes only):** `terrabase.storage.reads` hit its cardinality limit. A fifth `path` value overflowed to `otel_metric_overflow="true"`, and the harness refused that sample. Evidence: [r9 generated report](2026-10-14-delta-01M4WJX8ZPZB525GSRYJD6X0ZE.md), [r9 run JSON](2026-10-14-delta-01M4WJX8ZPZB525GSRYJD6X0ZE.json), [r9 snapshots](2026-10-14-fr20-machine-r9-before.json) and [r9 after](2026-10-14-fr20-machine-r9-after.json).
- **Fix:** `src/TerraBase/Hosting/Telemetry.cs:135` now sets `terrabase.storage.reads` `CardinalityLimit = 6` (five paths plus overflow). In r13 the overflow count is 0.
- **r13 (this run):** changes valid, shape invalid. Cause of the shape failure is unknown.

The acknowledgement failure must be diagnosed before a justified retry; no shape fan-out claim is supported by r13.
