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

## Query-independent held-out comparison

Run the bounded comparison from the `ts-benchmark` root with equal declared
index, query-work, query-memory, and SQLite cache settings:

```sh
dotnet run --project terrabase/TerraBase.Benchmarks --configuration Release -- \
  --workload=query-independent --documents=2000 --samples=30 \
  --maximum-indexes=2 --maintenance-documents=100 --physical-index-budget-mib=8 \
  --output=bench/query-independent-heldout-<run-id>
```

The report compares TerraBase without secondary indexes, deterministic
query-independent indexing, Von-assisted indexing when the local service
returns advice, and tuned SQLite without or with explicit JSONB indexes. The
`--physical-index-budget-mib` option applies one finite retained and transient
physical byte cap per collection and tenant to every TerraBase arm; `0` keeps
TerraBase defaults. Candidate selection receives no evaluation query or query history;
the held-out query and expected IDs are created after candidate selection,
advisor decision, lifecycle application, and post-build maintenance. The
report includes first-query and repeat latency, result coverage, profile/build
and inference time, maintenance write observations, logical storage, and
same-engine no-index regression deltas.

In the report table, `First rows` and `Repeat rows` are execution
`RowsExamined` counters, not returned-result counts. Each first and repeat
evaluation compares the sorted returned document IDs with the held-out expected
ID set; `Result coverage=True` records that exact check.

The Jev lane is opt-in: add `--jev` only with an approved
`TERRABASE_JEV_API_KEY`. Without that approval the report records Jev as
unrun and does not relabel deterministic results as Jev. Reported cache,
mmap, query, and index settings are reproducibility declarations rather than
hard process-wide resource caps.

For the current evaluation, keep Jev checks local: run the adapter's local
contract tests and omit `--jev` from benchmark commands. The held-out report
must leave Jev unrun; local test responses do not measure model benefit or
inference cost.

For a direct physical-index probe diagnostic, set
`--maintenance-documents=0`. The indexed TerraBase lane then runs one direct
`AutomaticIndexRuntime.MeasureOnIndexAsync` call followed by 30 direct repeats
per active index before measuring the ordinary runtime query repeats. The
report records direct first/p50/p95 timings beside full runtime repeat p50/p95;
the ordinary runtime first query is warmed and should be paired with a normal
cold-lane run when cold first-query timing is needed.

For the bounded point-mirror isolation probe, add `--point-mirror-probe`:

```sh
dotnet run --project terrabase/TerraBase.Benchmarks --configuration Release -- \
  --workload=query-independent --point-mirror-probe --documents=2000 --samples=30 \
  --maximum-indexes=2 --output=bench/query-independent-point-mirror-<run-id>
```

This opt-in lane builds completed snapshot-backed indexes once, freezes the
source cut, and reopens byte-identical copies for `TryPointSeek` and generic
`ExecuteBoundedAsync` entry reads. Its Markdown table records equivalent result
digests, latency, CPU, allocations, `EntryReads`, and `FullDocumentReads` for
each branch. It does not perform post-build writes and is a physical-path
diagnostic rather than a production performance claim.
