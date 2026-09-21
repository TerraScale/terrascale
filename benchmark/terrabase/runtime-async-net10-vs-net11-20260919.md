# .NET 10 vs .NET 11 RC1 (runtime async) — e2e volume comparison (2026-09-19)

Same source state, two runtimes. The .NET 11 lane lives in the worktree
`../terrabase-net11-wt` (branch `wt/net11-runtime-async`), created from the
main checkout's working state at `b78ceef5` (main's uncommitted work-in-progress
mirrored in, so both lanes compile identical code). The worktree's only
additional diff:

- `global.json`: SDK `11.0.100-rc.1.26425.128`, `allowPrerelease: true`
- `Directory.Build.props`: `TargetFramework` `net11.0`, plus
  `<Features>runtime-async=on</Features>` (.NET 11 RC1 preview feature:
  runtime-managed async suspension/resumption replacing compiler-generated
  state machines)
- `bench/TerraBase.Benchmarks/MagicOnionReplicationQualificationWorkload.cs`:
  two `Task.WhenAny` + `Task.Delay` sites rewritten with `WaitAsync` — required
  because CA2027 is an error under `TreatWarningsAsErrors` on net11; no
  behavior change (not on the e2e lane)

## Runtime async is active in the net11 build

Metadata inspection of `src/TerraBase/bin/Release/net11.0/TerraBase.dll` vs the
net10 build:

| Metric | net10 | net11 RC1 + runtime-async |
| --- | ---: | ---: |
| Compiler-generated state machine types | 542 | 58 |
| `AsyncStateMachineAttribute` methods | 492 | 1 |
| Async/sync iterator state machines (not rewritten by runtime async) | 52 + 4 | 52 + 4 |
| DLL size | 4,221,440 B | 3,659,776 B (−13%) |

## Run command (both lanes)

```text
nice -n 19 dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll \
  --workload e2e-volume --documents 100000 --batch-size 1000 --updates 8 \
  --seed 1337 --output bench/receipts/runtime-async-net10-20260919/run<N>
```

(identical for net11, from the worktree, DLL under `bin/Release/net11.0/`, run
with `~/.dotnet-sdk11/dotnet`). 5 interleaved runs per lane (net10 run N, then
net11 run N), 2026-09-19 09:07–09:14.

## Environment

| Item | Value |
| --- | --- |
| Source | `b78ceef5` + mirrored working state (identical both lanes); net11 lane adds only the diff above |
| net10 lane | SDK 10.0.400 (nix), runtime 10.0.11, `net10.0`, compiler state machines |
| net11 lane | SDK/runtime 11.0.100-rc.1.26425.128 (`~/.dotnet-sdk11`), `net11.0`, `Features=runtime-async=on` |
| Host | Intel Xeon E5-2660 v4 @ 2.00 GHz, 46 CPUs, NixOS 26.05, Linux 7.2.4, x64 |
| Disk | engine roots under the receipt dirs on `/dev/sda2` (QEMU virtual disk, shared-tenancy VM host, not NVMe); every committed batch `Flush(flushToDisk: true)` |
| Priority | `nice -n 19`, interleaved lanes |
| Workload | `e2e-volume`, seed 1337, 100,000 docs (~430 B), batches of 1,000, 8 updates |

## Phase results (ms, 5 samples per lane)

| Phase | net10 samples | net10 p50 | net11 samples | net11 p50 | Δ p50 |
| --- | --- | ---: | --- | ---: | ---: |
| Bulk write (100 fsync'd batches) | 3779, 4584, 4235, 3665, 4315 | 4235 | 5740, 4079, 3622, 4283, 3552 | 4079 | **−3.7%** |
| Read-back verify | 4875, 5307, 5598, 4854, 5787 | 5307 | 7962, 6487, 4866, 4961, 4746 | 4961 | **−6.5%** |
| Full backup (43.5 MB artifact) | 7468, 9342, 8017, 6242, 6781 | 7468 | 6541, 8201, 6558, 5801, 6127 | 6541 | **−12.4%** |
| Updates (8 publications) | 707, 830, 760, 802, 864 | 802 | 668, 667, 654, 638, 661 | 661 | **−17.6%** |
| Incremental backup | 683, 803, 736, 780, 838 | 780 | 644, 643, 632, 615, 638 | 638 | **−18.2%** |
| Full-boundary restore | 7211, 6321, 5134, 5874, 6465 | 6321 | 5569, 6715, 5065, 5843, 4802 | 5569 | **−11.9%** |
| Full restore verify | 3929, 4285, 3657, 3424, 3645 | 3657 | 3836, 3595, 3825, 3741, 3344 | 3741 | +2.3% |
| Interior PITR restore | 4769, 5088, 4683, 4778, 4397 | 4769 | 5027, 4935, 4756, 4622, 5329 | 4935 | +3.5% |
| PITR restore verify | 5516, 3432, 3701, 3464, 3196 | 3464 | 4180, 3423, 3331, 3200, 5582 | 3423 | −1.2% |
| **Sum of phase p50s** | | **36,804** | | **34,547** | **−6.1%** |
| **Per-run total p50** | | **36,522** | | **34,781** | **−4.8%** |

Backup, restore, and update paths improve 12–18%; PITR restore and its verify
regress ~2–4% (sample ranges overlap the net10 lane, so treat those as noise-
adjacent). Each lane has one first/last-run outlier (net11 write and read-back
run 1: 5740/7962 ms; net10 PITR-verify run 1: 5516 ms; net11 PITR-verify run 5:
5582 ms), which the medians absorb.

## Correctness (all 10 runs)

Every run: `status=complete`, 100,000/100,000 exact on all four verifications
(read-back, full-boundary restore, interior PITR restore, restore-point
listing). Deterministic corpus (seed 1337) byte-identical across lanes.

## Receipts

- net10: `bench/receipts/runtime-async-net10-20260919/run1..5/e2e-volume-receipt.json`
- net11: `bench/receipts/runtime-async-net11-20260919/run1..5/e2e-volume-receipt.json`

Runtime-async remains a preview feature; RC1 numbers, not a GA claim.
