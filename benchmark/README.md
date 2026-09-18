# ts-benchmark

TerraScale benchmark suite — workloads, run receipts, and results history,
consolidated from their source repos with full git history preserved.

## Layout

| Path | Origin | History |
| --- | --- | --- |
| `terrabase/` | `TerraBase` repo, `bench/` (imported 2026-09-18) | full — 1300+ commits, via `git subtree split` |
| `ts-proxy/reader-ramp/` | `ts-proxy` repo, `bench/reader-ramp/` | full |
| `loro/` | `scratch/loro-bench-20260917` (was untracked) | none — imported as snapshot |

`terrabase/` contains the BenchmarkDotNet-style workload projects
(`TerraBase.Benchmarks`, `TerraBase.FullTextBenchmarks`), per-run receipts under
`receipts/` (receipt JSON, summaries, raw samples), and the dated benchmark
reports (`*.md`) at the top level.

Runtime databases under `receipts/*/data/` are tracked where they were part of
the receipt evidence in the source repo; large untracked runtime data from
in-progress runs is not.
