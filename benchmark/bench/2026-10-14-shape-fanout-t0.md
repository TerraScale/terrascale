# TerraBase FR-20 shape fan-out T0 — 2026-10-14 (run r15)

## 1. Status

- **Run r15: VALID. All eight rows are present** (changes and shape, N = 1, 10, 100, 1000). Exit code 0, host cleaned up, zero `otel_metric_overflow` series.
- **Changes fan-out: four measured rows.** Zero missed deliveries. N = 100 and 1000 are capped at 32 admitted followers (`changes_busy`).
- **Shape fan-out: two measured rows (N = 1, 10) and two capacity rows (N = 100, 1000).** The measured rows have zero missed deliveries. The capacity rows stopped at readiness, before the window: no writes, deliveries, latency or storage reads were measured for them (section 3).
- Run ID: `01M4WQ54KYRK9ZMR8CHRB3FENA`. Interval: 2026-10-14T07:57:28Z to 08:06:12Z (8 min 44 s).
- Measured HEAD: `1c5e7814c6935beabcef369d9bb59313690a057b`, clean.
- Earlier attempts r9 and r13 were invalid. They are kept below as history (section 9).

## 2. Provenance

| field | after build | before run | after run |
| --- | --- | --- | --- |
| Measured HEAD | `1c5e7814c6935beabcef369d9bb59313690a057b` | same | same |
| Last product/host and harness commit | `1c5e7814c` (generated report notes) | same | same |
| Working tree | clean (0) | clean (0) | 2 untracked: this run's generated report `.md` and `.json` |
| ts-dotnet commit (pinned) | `bcd20603f11f3aa54dc54cec6e410b4fe9e24151` | same | same |
| .NET SDK | `11.0.100-rc.1.26425.128` | same | same |
| Go toolchain for the run | `go1.25.0` (ran from `benchmark/`) | same | same |
| Host | `mariogk-dev`, Linux 7.2.10, Xeon E5-2660 v4 @ 2.00 GHz, 46 logical CPUs, 33.6 GB RAM | same | same |
| Available RAM | 28.5 GB | 30.0 GB | 30.8 GB |
| Load 1 / 5 / 15 min | 13.94 / 7.90 / 3.17 | 1.65 / 5.17 / 2.77 | 0.47 / 1.43 / 1.90 |
| Snapshot (UTC) | [07:55:09](2026-10-14-fr20-machine-r15-after-build.json) | [07:57:21](2026-10-14-fr20-machine-r15-before.json) | [08:06:37](2026-10-14-fr20-machine-r15-after.json) |

**Remote checkout.** The run used fresh clones on `mariogk-dev`, not the host's shared checkouts:

- Source: `/tmp/fr20-measurement-r15-repo`, cloned from bundle `/tmp/fr20-source-r15.bundle`, branch `feat/repl-fr20-shape-wireup` at `1c5e7814c`.
- SDK: `/tmp/fr20-measurement-r15-sdk`, cloned from bundle `/tmp/fr20-sdk-r15.bundle`, detached at `bcd20603f`. The host's own ts-dotnet checkout (`df852…`) was not used.

**Quiet interval.** The after-build load (13.94) was this run's own Release build decaying. Before the run: load 1.65 on 46 CPUs, no Fast suite or build running. The after-run snapshot read 0.47. Load was not sampled continuously during the run. The `top_cpu_processes` values come from `ps pcpu`, a lifetime average. The idle `VBCSCompiler` entries are leftover build servers, not active load. One sample, not repeated trials.

**Code state.** The measured binary is `1c5e7814c`, built clean from that commit. The page, cursor and fan-out diagnostics are in `1c5e7814c`; no uncommitted edits were measured. The r13 note about uncommitted edits applied only to r13 (section 9).

## 3. Key changes and shape table (measured, gru)

Writes: 600 in-window per step, 20/s, 256-byte documents. Source: [generated report](2026-10-14-mariogk-dev-01M4WQ54KYRK9ZMR8CHRB3FENA.md), [run JSON](2026-10-14-mariogk-dev-01M4WQ54KYRK9ZMR8CHRB3FENA.json).

### Changes fan-out

| requested N | admitted | rejected (`changes_busy`) | delivered | missed | p50 ms | p99 ms | max ms | CPU % | RSS MiB | MB in | feed reads | point (node-wide) | query |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | 0 | 600 | 0 | 14.54 | 24.22 | 53.12 | 96.1 | 250.7 | 0.31 | 2,106 | 7,964 | 15 |
| 10 | 10 | 0 | 6,000 | 0 | 18.62 | 31.58 | 56.32 | 107.7 | 251.4 | 3.15 | 21,112 | 8,015 | 15 |
| 100 | 32 | 68 | 19,200 | 0 | 28.46 | 43.81 | 89.66 | 150.4 | 269.9 | 10.07 | 67,596 | 8,785 | 18 |
| 1000 | 32 | 968 | 19,200 | 0 | 28.89 | 43.58 | 68.42 | 151.9 | 277.2 | 10.07 | 67,962 | 16,279 | 36 |

### Shape fan-out

| requested N | admitted | rejected (`shape_busy`) | retries | delivered | missed | p50 ms | p99 ms | CPU % | RSS MiB | MB in | `shape` reads | `shape_body` reads | `point` reads (node-wide) | stopped |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | 0 | 0 | 600 | 0 | 1126.40 | 1732.61 | 129.1 | 285.9 | 0.24 | 86,136 | 50 | 8,568 | - |
| 10 | 10 | 0 | 6 | 6,000 | 0 | 722.94 | 998.40 | 137.9 | 320.2 | 2.31 | 46,212 | 537 | 18,539 | - |
| 100 | 68 | 32 | 1,156 | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a | readiness: 66 of 68 at a live poll, gauge 64 |
| 1000 | 76 | 924 | 4,419 | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a | readiness: 73 of 76 at a live poll, gauge 64 |

- **The three read paths are separate deltas, never summed.** `shape`: metadata and proof SQL. `shape_body`: payload-body SELECTs. `point`: document checks, node-wide, so it includes the writer's own reads.
- **Shape proof hash cost.** The fresh-body proof path (`ShapeResume` / `ShapePrefix`) runs inside the `shape` path counts above. Its SQL is not split from other metadata SQL, and no hash CPU or time is measured on its own. No cost is attributed to it separately.
- `terrabase.shape.reads` (shape responses read, including sealed-data hits): 73 at N=1 and 1,290 at N=10, after minus before.
- **Capacity rows (N = 100, 1000).** Readiness needs every admitted follower to have sent a live poll within 1 min. The parked gauge held at 64 in both rows. 64 is `MaxRequestersPerOrganization` (`src/TerraBase/Features/Shapes/ShapeFlights.cs:85`). Followers past that answer `shape_busy` and retry (1,156 and 4,419 retries). This run does not show why 2 and 3 followers never reached a live poll. The harness counts a timeout after admission as a capacity row, not a failure. Shape fan-out at N ≥ 100 is **not measured**.
- Latency runs from the write's send time to the follower reading its event. Shape followers long-poll, so shape and changes latency are different measurements.
- CPU and RSS are the combined host process (both nodes, replication, auth stand-in, test runner). Counts include zero-row reads and page-cache hits. They are not disk I/O.
- `accounting_reads_delta` is 0 for every measured row. `qos_busy_followers` and `qos_busy_writes` are 0 everywhere.

## 4. Baseline comparison

FR-17 instrumented baseline, not a set of controlled paired trials: [2026-10-13-shape-fanout-baseline.md](2026-10-13-shape-fanout-baseline.md), run `01M4VTW4KTKF2VMVNQRG89034K`. Same host name and hardware (`mariogk-dev`, Xeon E5-2660 v4, 46 CPUs), same pinned SDK and toolchains. Source differs (`cc8967367` vs `1c5e7814c`). Load differed: FR-17 started at 15.51, r15 at 1.65.

Only the changes `feed` read deltas are compared. Both runs measure them the same way.

| requested N | FR-17 feed reads | r15 feed reads | difference |
| --- | --- | --- | --- |
| 1 | 2,098 | 2,106 | +8 |
| 10 | 20,888 | 21,112 | +224 |
| 100 | 66,764 | 67,596 | +832 |
| 1000 | 67,026 | 67,962 | +936 |

**No improvement or regression claim**, on reads, latency, CPU or memory. Shape has no FR-17 row to compare against. The FR-17 `point` series is not comparable (its baseline notes say so).

## 5. Reproduce and validation

Exact commands run on `mariogk-dev` (logs at `/tmp` on that host, copied to local `/tmp`):

```sh
cd /tmp/fr20-measurement-r15-repo
env TsDotnetRoot=/tmp/fr20-measurement-r15-sdk nice -n19 ~/.local/bin/ts-heavy \
  make terrabase-build CONFIGURATION=Release > /tmp/fr20-release-r15.log 2>&1
cd benchmark
python3 -I /tmp/fr20-snap-r15.py before /tmp/fr20-machine-r15-before.json
env TsDotnetRoot=/tmp/fr20-measurement-r15-sdk nice -n19 ~/.local/bin/ts-heavy \
  go run ./cmd/shape-bench > /tmp/fr20-run-r15.log 2>&1
rc=$?; echo $rc > /tmp/fr20-run-r15.exit   # the ssh command then exited with $rc; nothing masked it
python3 -I /tmp/fr20-snap-r15.py after /tmp/fr20-machine-r15-after.json
```

`/tmp/fr20-snap-r15.py` is `/tmp/fr20-snap-r11.py` with `REPO` and `TS` pointed at the two clones. It is not in the repo.

| check | result |
| --- | --- |
| Remote Release build (`/tmp/fr20-release-r15.log`) | passed: 0 warnings, 0 errors, 1 min 59 s |
| Benchmark run (`/tmp/fr20-run-r15.log`, `/tmp/fr20-run-r15.exit`) | exit 0; log lists the two generated files |
| `grep -c otel_metric_overflow` on the generated report | 0 |
| Rows present | 8 of 8 |
| Source after run | `1c5e7814c`, no tracked changes |
| Root: Release build | passed: 0 warnings, 0 errors |
| Root: `ShapeEndToEndTests` | 13/13 passed, including the four-reader single-payload page (one SELECT) assertion |
| Root: live cursor / HTTP organization isolation | 1/1 and 2/2 passed |
| Root: Go tests | passed |

No .NET tests ran on the remote host. The full suite was not run.

## 6. Raw metric lines

`terrabase_storage_reads_total`, `terrabase_shape_reads_total` and `terrabase_shape_requests_waiting` lines, gru, copied from the [generated report](2026-10-14-mariogk-dev-01M4WQ54KYRK9ZMR8CHRB3FENA.md) (which also has `terrabase_stats_operations_total`).

changes.fanout N=1 before:

```
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 196
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 3
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 2
```

changes.fanout N=1 after:

```
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 8160
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 18
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 2108
```

changes.fanout N=10 before:

```
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 8160
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 18
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 2108
```

changes.fanout N=10 after:

```
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 16175
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 33
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 23220
```

changes.fanout N=100 before:

```
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 16175
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 33
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 23220
```

changes.fanout N=100 after:

```
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 24960
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 51
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 90816
```

changes.fanout N=1000 before:

```
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 24960
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 51
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 90816
```

changes.fanout N=1000 after:

```
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 41239
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 87
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 158778
```

shape.fanout N=1 before:

```
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 41239
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 87
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 158778
```

shape.fanout N=1 after:

```
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 49807
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 102
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 158778
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="shape"} 86136
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="shape_body"} 50
terrabase_shape_reads_total{otel_scope_name="TerraScale.TerraBase"} 73
terrabase_shape_requests_waiting{otel_scope_name="TerraScale.TerraBase"} 0
```

shape.fanout N=10 before:

```
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 49807
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 102
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 158778
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="shape"} 86136
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="shape_body"} 50
terrabase_shape_reads_total{otel_scope_name="TerraScale.TerraBase"} 73
terrabase_shape_requests_waiting{otel_scope_name="TerraScale.TerraBase"} 0
```

shape.fanout N=10 after:

```
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="point"} 68346
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="query"} 120
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="feed"} 158778
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="shape"} 132348
terrabase_storage_reads_total{otel_scope_name="TerraScale.TerraBase",path="shape_body"} 587
terrabase_shape_reads_total{otel_scope_name="TerraScale.TerraBase"} 1363
terrabase_shape_requests_waiting{otel_scope_name="TerraScale.TerraBase"} 0
```

shape.fanout N=100: no window or final scrape recorded; see the result's stopped reason or the client error for the outcome.

shape.fanout N=1000: no window or final scrape recorded; see the result's stopped reason or the client error for the outcome.

## 7. Raw machine snapshots

[after-build](2026-10-14-fr20-machine-r15-after-build.json):

```json
{
  "label": "after-build",
  "captured_utc": "2026-10-14T07:55:09Z",
  "measured_commit": "1c5e7814c6935beabcef369d9bb59313690a057b",
  "working_tree_changes": 0,
  "ts_dotnet_commit": "bcd20603f11f3aa54dc54cec6e410b4fe9e24151",
  "host": "mariogk-dev",
  "kernel": "7.2.10",
  "cpu_model": "Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz",
  "logical_cpus": 46,
  "mem_total_bytes": 33614655488,
  "mem_available_bytes": 28520292352,
  "loadavg_1_5_15": "13.94 7.90 3.17",
  "go": "go version go1.25.0 linux/amd64",
  "dotnet_sdk": "11.0.100-rc.1.26425.128",
  "top_cpu_processes": [
    {
      "pcpu": 1659.0,
      "command": "VBCSCompiler"
    },
    {
      "pcpu": 200.0,
      "command": "ps"
    },
    {
      "pcpu": 66.6,
      "command": "python3"
    },
    {
      "pcpu": 19.7,
      "command": "MSBuild"
    },
    {
      "pcpu": 1.4,
      "command": "node-MainThread"
    }
  ]
}
```

[before](2026-10-14-fr20-machine-r15-before.json):

```json
{
  "label": "before",
  "captured_utc": "2026-10-14T07:57:21Z",
  "measured_commit": "1c5e7814c6935beabcef369d9bb59313690a057b",
  "working_tree_changes": 0,
  "ts_dotnet_commit": "bcd20603f11f3aa54dc54cec6e410b4fe9e24151",
  "host": "mariogk-dev",
  "kernel": "7.2.10",
  "cpu_model": "Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz",
  "logical_cpus": 46,
  "mem_total_bytes": 33614655488,
  "mem_available_bytes": 29976391680,
  "loadavg_1_5_15": "1.65 5.17 2.77",
  "go": "go version go1.25.0 linux/amd64",
  "dotnet_sdk": "11.0.100-rc.1.26425.128",
  "top_cpu_processes": [
    {
      "pcpu": 867.0,
      "command": "VBCSCompiler"
    },
    {
      "pcpu": 75.0,
      "command": "python3"
    },
    {
      "pcpu": 12.5,
      "command": "fish"
    },
    {
      "pcpu": 10.6,
      "command": "MSBuild"
    },
    {
      "pcpu": 2.7,
      "command": "sshd-session"
    }
  ]
}
```

[after](2026-10-14-fr20-machine-r15-after.json):

```json
{
  "label": "after",
  "captured_utc": "2026-10-14T08:06:37Z",
  "measured_commit": "1c5e7814c6935beabcef369d9bb59313690a057b",
  "working_tree_changes": 2,
  "ts_dotnet_commit": "bcd20603f11f3aa54dc54cec6e410b4fe9e24151",
  "host": "mariogk-dev",
  "kernel": "7.2.10",
  "cpu_model": "Intel(R) Xeon(R) CPU E5-2660 v4 @ 2.00GHz",
  "logical_cpus": 46,
  "mem_total_bytes": 33614655488,
  "mem_available_bytes": 30811951104,
  "loadavg_1_5_15": "0.47 1.43 1.90",
  "go": "go version go1.25.0 linux/amd64",
  "dotnet_sdk": "11.0.100-rc.1.26425.128",
  "top_cpu_processes": [
    {
      "pcpu": 286.0,
      "command": "VBCSCompiler <defunct>"
    },
    {
      "pcpu": 69.2,
      "command": "python3"
    },
    {
      "pcpu": 12.5,
      "command": "fish"
    },
    {
      "pcpu": 3.7,
      "command": "MSBuild"
    },
    {
      "pcpu": 2.7,
      "command": "sshd-session"
    }
  ]
}
```

## 8. Raw shared report JSON

- [2026-10-14-mariogk-dev-01M4WQ54KYRK9ZMR8CHRB3FENA.json](2026-10-14-mariogk-dev-01M4WQ54KYRK9ZMR8CHRB3FENA.json), written by `apibench.WriteReport`.

## 9. Earlier invalid attempts (history)

- **r9 (invalid, changes only, host `delta`):** `terrabase.storage.reads` hit its cardinality limit. A fifth `path` value overflowed to `otel_metric_overflow="true"`, and the harness refused that sample. Fixed by `CardinalityLimit = 6` in `src/TerraBase/Hosting/Telemetry.cs` (five paths plus overflow). Evidence: [r9 generated report](2026-10-14-delta-01M4WJX8ZPZB525GSRYJD6X0ZE.md), [r9 run JSON](2026-10-14-delta-01M4WJX8ZPZB525GSRYJD6X0ZE.json), [r9 before](2026-10-14-fr20-machine-r9-before.json), [r9 after](2026-10-14-fr20-machine-r9-after.json).
- **r13 (invalid shape, host `delta`, HEAD `a2a7ed906`, run `01M4WNQJTC4E1ZPV0ZJ49R8219`):** changes rows were complete. The shape N=1 writer PUT failed with `HTTP 503 write_outcome_unknown` ("no other region confirmed the write within 10 s"). The cause was not established. Load was 35.70 on 16 CPUs. At r13 the working tree held uncommitted shape, test, doc and harness edits that the run did not measure; those are now part of `1c5e7814c`. The r13 raw report's line "the step stopped at a capacity limit" is wrong: that step failed on `write_outcome_unknown`. Evidence: [r13 generated report](2026-10-14-delta-01M4WNQJTC4E1ZPV0ZJ49R8219.md), [r13 run JSON](2026-10-14-delta-01M4WNQJTC4E1ZPV0ZJ49R8219.json), [r13 before](2026-10-14-fr20-machine-r13-before.json), [r13 after](2026-10-14-fr20-machine-r13-after.json).
- **r15 (this report):** no writer failure. Every write succeeded in every measured step.
