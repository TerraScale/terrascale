# ts-benchmark

This repository holds the test environment, drivers, fixtures, raw evidence, and
scoreboards for the **Rust** TerraBase engine. The acceptance contract is
[`../terrabase/GOAL.md`](../terrabase/GOAL.md), especially sections 10 and 11.
The [v1 matrix](bench/terrabase-rust-acceptance-v1.md) registers the required
cases and thresholds before measurement. Its open rows are open; old .NET
results in [`history/`](history/README.md) do not qualify the Rust engine.

| Path | Purpose |
| --- | --- |
| `terra-rust/` | Rust benchmark driver and embedded SQLite baseline |
| `firecracker-harness/` | Pinned microVM definitions, launch, and environment verification |
| `bench/terrabase-rust-acceptance-v1.md` | Versioned required-case scoreboard |
| `bench/firecracker-harness/` | Firecracker environment receipts |
| `bench/fixtures/` | Versioned seeds, data generators, dataset definitions, and fixture manifests |
| `bench/<matrix-version>/<case>/<trial>/` | New guest results, raw samples, receipts, and profiles |
| `history/legacy-pre-rust-2026-09-24/` | Preserved earlier benchmark code, reports, and generated run data |

## Build and test boundary

Use the root Makefile for the Rust driver's format check, compile check, and
release build. Build work runs at the lowest CPU and I/O scheduling priority:

```sh
nice -n 19 ionice -c 3 make fmt check build
nice -n 19 ionice -c 3 make build-guest MUSL_CC=/path/to/musl-gcc
```

`build` is a host artifact. `build-guest` produces the statically linked
`terra-rust/target/x86_64-unknown-linux-musl/release/terrabase-sqlite-baseline`
used by the current guest image. The pinned Rust musl target and a musl C
compiler are required because bundled SQLite contains C code.

Run `make vm-preflight` to check pinned inputs and KVM, then `make vm-smoke`
to boot the guest and check the effective limits. `make vm-run` delegates a
caller-supplied `PROGRAM`, `SUBJECT_REPO`, and `RUN_ID` to the guest harness;
see [`firecracker-harness/README.md`](firecracker-harness/README.md) for its
runner and output contract once the harness is available.

Building on the host is allowed. **Tests and measurements qualify only when
run inside verified Firecracker guests** with 4 vCPUs and 8,192 MiB RAM per
guest and swap disabled. The environment harness checks the effective guest
configuration and records pinned Firecracker, kernel/rootfs image hashes, CPU
topology, SSD/filesystem, cache state, and network topology. RustFS, Von, and
load generators occupy separate guests when applicable. Lack of KVM, images,
or a required service is an environment failure, never a host fallback.

The current Rust driver measures an initial SQLite-only subset. Its
[`README`](terra-rust/README.md) describes the real executable, fixture, receipt,
and guest invocation contract. The matching TerraBase embedded caller and many
matrix axes are still open. No performance comparison is valid until equivalent
engine calls, durability, data, index shape, resource budgets, guest setup, and
repeated paired trials are recorded in the matrix.

## Results and evidence

Create a stable fixture ID before each experiment. Save its generator/source
revision, seed, dataset specification, and digest under `bench/fixtures/`.
Copy guest JSON receipts, raw JSONL samples, profiles, and environment manifest
to `bench/<matrix-version>/<case>/<trial>/`. Put an easy-to-compare Markdown
table in `bench/` for each measured case or run series, as required by
[`AGENTS.md`](AGENTS.md). Rows identify both repository revisions and binary
hashes, the matrix version, case, trial, environment manifest, engine,
durability mode, primary metric, variance/confidence interval, and result
status. Retain failed and negative experiments. Keep the SQLite scoreboard
statuses distinct: win, loss, statistically inconclusive, unsupported, or not
comparable, with the receipt and reason. Do not fill an open row from a mock,
host run, historic report, or a SQLite-only measurement.

The [matrix](bench/terrabase-rust-acceptance-v1.md) defines the pinned SQLite
version, fair tuning and guarantees, minimum paired trials, significance
threshold, required workload/resource axes, and full evidence standard. Changes
to cases or thresholds require a new matrix version registered before evaluating
a candidate.
