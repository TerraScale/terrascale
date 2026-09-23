# ts-benchmark

TerraScale benchmark suite — workloads, run receipts, and results history,
consolidated from their source repos with full git history preserved.

## Layout

| Path | Origin | History |
| --- | --- | --- |
| `terrabase/` | `TerraBase` repo, `bench/` (imported 2026-09-18) | full — 1300+ commits, via `git subtree split` |
| `ts-atlas/reader-ramp/` | `ts-proxy` repo (renamed `ts-atlas`), `bench/reader-ramp/` | full |
| `loro/` | `scratch/loro-bench-20260917` (was untracked) | none — imported as snapshot |
| `ts-auth/` | ts-auth SDK client benchmarks (Go/Rust/TS harnesses + receipts; the .NET lane is the `tsauth-sdk` engine inside `terrabase/`) | none — born here 2026-09-20 |

`terrabase/` contains the BenchmarkDotNet-style workload projects
(`TerraBase.Benchmarks`, `TerraBase.FullTextBenchmarks`), per-run receipts under
`receipts/` (receipt JSON, summaries, raw samples), and the dated benchmark
reports (`*.md`) at the top level.

Markdown benchmark tables and compact receipt JSON remain trackable. Generated
database snapshots, executables, raw samples, and run logs are ignored and
removed from repository history.

## Subject resolution and receipts

Only `bench/` was imported here; the measured TerraBase code lives in the
sibling `terrabase` checkout. `terrabase/Directory.Build.props` pins the subject
paths (`TerraBaseRepo`, overridable with `/p:TerraBaseRepo=<path>`), and
`terrabase/Directory.Build.targets` fails the build with the exact missing path
when that checkout is absent — a benchmark never silently skips a missing
subject. The optional `terrabase-sdk` engine lane additionally reaches into the
sibling `ts-dotnet` checkout when present; requesting it against a binary built
without that checkout fails loudly with the path that was absent.

Every runner invocation emits a compact `run-receipt.json` next to the run
(`--output DIR`, or `bench/receipts/run-<timestamp>/` by default) recording the
subject repository, commit SHA, tree digest, and dirty state; machine and
toolchain identity; UTC start/finish; the run status and exit code; digests of
the detailed workload receipts in the directory; and a `receiptSha256` digest
over the receipt payload itself.

Validate the run-receipt logic with the runner self-check (exits nonzero on any
failure):

```sh
nice -n 10 dotnet run --project terrabase/TerraBase.Benchmarks -- --self-check
```
