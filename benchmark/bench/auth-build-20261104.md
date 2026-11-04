# Auth build profiling, 2026-11-04

## Method

Run through `ts-heavy-any` on delta with the pinned .NET
`11.0.100-rc.3.26522.148` SDK, Release configuration, eight MSBuild/compiler
workers, node reuse and shared compilation disabled, and unchanged analyzer
settings. Each dotnet invocation records `-bl`, `-clp:PerformanceSummary` and
`-p:ReportAnalyzer=true`. Replay binary logs with `dotnet msbuild FILE.binlog
-v:diag` to inspect compiler, analyzer and generator timings.

Cold means removing the isolated checkout's `bin` and `obj` directories before
each run. NuGet packages and verified native libraries remain cached. Incremental
means immediately repeating the target without source changes. Times include
the target's locked restores and native verification, excluding slot admission
and checkout preparation. These are loaded-host measurements, not an isolated
CPU benchmark. Before-run load1 ranged from 21.95 to 30.86 on 16 logical CPUs; after-run
load1 ranged from 36.50 to 52.48.

Baseline: `d4d89fb2b078feb5bdaebec8d5e7bb64c38c03a3`.
Measured change: `02694d6fc` (subsequently rebased; the measured auth targets are
unchanged). Raw binary logs, invocation times and target logs remain mode 0600
under `~/.local/state/auth-build-profile/{before,after}` on delta.

## Three measured repeats

All times are wall-clock seconds. Every measured build succeeded with no warnings
or compiler errors.

| Target | Before 1 | Before 2 | Before 3 | After 1 | After 2 | After 3 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `make auth-build CONFIGURATION=Release`, cold | 80.844 | 85.712 | 68.431 | 75.433 | 74.811 | 57.962 |
| Same target, incremental | 21.543 | 21.479 | 20.439 | 17.009 | 18.781 | 14.315 |

Mean cold times: 78.329 s before, 69.402 s after. Mean incremental times:
21.154 s before, 16.702 s after. All three after cold builds are below 120 s;
all incremental builds are below 30 s despite the higher observed load.

## Binary-log findings

- The baseline restores all 30 projects in the monorepo, then restores the
  unrelated TerraBase analyzer BuildCheck project. In the first repeat these
  invocations took 7.116 s and 6.674 s. The actual NuGet Restore tasks took less
  than one second each; evaluation, process startup and shutdown are material.
  Auth now restores its own graph once, in locked mode.
- The store explicitly requests the SDK's .NET 10 target while transitive
  references select .NET 11. Both SDK binaries compile. The first binary log
  reports 19.135 s and 19.347 s of analyzer execution respectively, dominated by
  ILLink analysis. Removing the store's target override lets the .NET 11 graph
  select the same SDK target everywhere. The SDK still supports both frameworks
  in its own builds. The after binary log has 13 compiler invocations instead
  of 14 and exactly one SDK compilation, for .NET 11.
- Auth reports 38.918 s of analyzer execution and 3.925 s of generator execution
  in the first baseline log. Fast reports 13.307 s / 2.358 s; Slow reports
  4.877 s / 1.140 s. Analyzer totals can exceed elapsed time because analyzers
  run concurrently. Retain every analyzer and the existing fixture source links.
- There is no AOT, trimming or ReadyToRun compilation in these build targets.
  The expensive ILLink work is compile-time analysis. Publishing is independent.
- Shipping previously built TerraBase, then auth Fast in separate MSBuild
  invocations. `auth-fast-build` restores and builds their shared solution graph
  once with `-m`, allowing independent server/auth work to overlap. It includes
  Fast and its references; Slow is built by `auth-build` and its own test target.
  Slow explicitly restores TerraBase before its separate `--no-restore` build.
  Baseline shipping spent 82.841 s in the TerraBase build and 69.240 s in the
  subsequent auth Fast build. TerraBase's compiler reports 114.357 s of analyzer
  execution and 7.646 s of generator execution; those checks are preserved.

## Shipping path

The baseline cold build portion of `make auth-test-fast CONFIGURATION=Release`
took 166.294 s before and 58.132 s after on delta, with load1 47.26 and 23.97
at completion respectively. Profiling omitted test execution; the independent
proof runs execute the actual ts-ship Fast runner.

The actual ts-ship auth runner passed on delta at
`23f54587a434810c120a49ea92798daea32c0e54`: cold build 97.953 s, all 51 Fast
tests passed with no skips, test phase 40.862 s. The existing warn-only 30 s
suite / 10 s per-test policy reported overruns; no timing gate was changed.

The ts-ship script is unchanged. All three hosts have SHA-256
`c082893368e2da143f4d558e6edbcaaa1ce77ee629a6052ca4410823ecf85f40`.
Its 600 s build cap, compiler budget, full test parallelism and Fast timing gates
remain intact. Warnings-as-errors, BannedSymbols and package-graph guards are
unchanged. No tests were added or removed.
