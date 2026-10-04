# ts-benchmark

This repository holds the test environment, drivers, fixtures, raw evidence, and
scoreboards for the **Rust** TerraBase engine. The acceptance contract is
[`../terrabase/GOAL.md`](../terrabase/GOAL.md), especially sections 10 and 11.
The [v2 matrix](bench/terrabase-rust-acceptance-v2.md) registers the required
cases and the GOAL.md throughput floor: every applicable case must reach at
least 0.40x tuned SQLite throughput, judged by the lower bound of a paired 95%
confidence interval. It supersedes the [v1 matrix](bench/terrabase-rust-acceptance-v1.md)
(1.05x win rule), which stays as history. Open rows are open.

| Path | Purpose |
| --- | --- |
| `terra-rust/` | Rust benchmark driver and embedded SQLite baseline |
| `https-document/` | Process-based HTTPS point-read diagnostic driver |
| `firecracker-harness/` | Optional development microVM tooling and environment verification |
| `local-engine/` | Pinned Turso-vs-SQLite local CRUD driver |
| `tests/fast.sh` | Fast suite run by CI |
| `bench/terrabase-rust-acceptance-v2.md` | Current versioned required-case scoreboard (v1 kept as history) |
| `bench/firecracker-harness/` | Firecracker environment receipts |
| `bench/fixtures/` | Versioned seeds, data generators, dataset definitions, and fixture manifests |
| `bench/<matrix-version>/<case>/<trial>/` | Matrix-qualified results, raw samples, receipts, and profiles |

## Build and test boundary

The root `rust-toolchain.toml` pins Rust 1.98.1, Clippy, rustfmt, and the musl
target. Each Rust subproject repeats the same version in its local toolchain
file so standalone commands select it too. `mise.toml` reads the root version
file instead of keeping a second Rust version. With `mise` and `rustup` on
`PATH`, check the local setup from this directory with `mise current rust`,
`rustup show active-toolchain`,
and `rustup target list --installed`. If a component is missing, install only
the pinned toolchain pieces with `rustup toolchain install 1.98.1 --profile
minimal --component clippy,rustfmt --target x86_64-unknown-linux-musl`; the
guest build also needs a musl C compiler. No global Rust version change is
needed. `mise exec --` puts rustup's Cargo and rustc ahead of system binaries
and selects the pinned version even if another `RUSTUP_TOOLCHAIN` was inherited.
Without mise, prefix Make commands with
`env -u RUSTUP_TOOLCHAIN PATH="$HOME/.cargo/bin:$PATH"`.

Use the root Makefile for the Rust driver's format check, compile check, and
release build. The engine-enabled `check-engine` and `lint-engine` targets
compile the real embedded TerraBase caller:

```sh
mise exec -- make fmt check lint build
mise exec -- make build-guest MUSL_CC=/path/to/musl-gcc
```

The HTTPS diagnostic driver has its own Make targets: `fmt`, `check`, `lint`,
and `build` under `https-document/`. It measures a running, packaged TerraBase
server through the real TLS endpoint with an already committed fixture. It does
not provision the server or produce a SQLite comparison. See
[`https-document/README.md`](https-document/README.md) for its input and receipt
contract. The `test-build`, `test-vm`, and `vm-*` Make targets remain available
for optional Firecracker development work. Bare `make` in the root and harness
directories prints help.

## Fast suite and CI

The drivers compile against a sibling TerraBase checkout (`../terrabase`) at
the revision in [`terrabase.rev`](terrabase.rev), with its Turso v0.8.1
submodule patched by TerraBase's `scripts/ci/prepare-turso.sh`. With that
checkout in place, `make fast` runs the blocking Fast suite: `lint-fast`
(format and strict Clippy for the SQLite-only `terra-rust` build,
`local-engine`, and `https-document`), `build-fast` (release builds),
and `test-fast` ([`tests/fast.sh`](tests/fast.sh)). The suite runs the real
executables as processes: it checks `throughput-floor` verdicts and input
rejections, and runs a 50-cycle SQLite and Turso CRUD smoke benchmark through
`local-engine`, requiring both lanes to pass reopen and integrity checks with
identical trace and final-state hashes. Smoke timings are not results.

Forgejo CI ([`.forgejo/workflows/fast.yml`](.forgejo/workflows/fast.yml))
runs the same targets on `terrascale-builder` in the `ts-builder` image on
every pull request and push to `main`. `scripts/ci/checkout-terrabase.sh`
fetches the pinned TerraBase revision with the `ORG_READ_TOKEN` secret.
Bump `terrabase.rev` deliberately; a new Turso pin or patch series also needs
the `local-engine` pin update. There is no Slow suite yet: no long-running
benchmark check exists that could run on the shared runner without
pretending to qualify a scoreboard row.

The feature-gated embedded TerraBase caller (`check-engine`, `lint-engine`)
is not in the Fast suite: it calls the retired custom storage engine's
`tb-storage` API (immutable runs, `local.rs`/`run.rs`/`floor.rs`) and does not
compile against the Turso-backed TerraBase at `terrabase.rev`. Porting it to
the current engine is open work; until then every TerraBase comparison row
stays `UNSUPPORTED`.

`build` is a host artifact. `build-guest` produces the statically linked
`terra-rust/target/x86_64-unknown-linux-musl/release/terrabase-sqlite-baseline`
used by the optional guest image. The pinned Rust musl target and a musl C
compiler are required for that build because bundled SQLite contains C code.

For an optional development VM run, `make vm-preflight` checks pinned inputs
and KVM, and `make vm-smoke` boots a guest and checks its effective limits.
`make vm-run` delegates a caller-supplied `PROGRAM`, `SUBJECT_REPO`, `RUN_ID`,
and optional `ARGS` and `TIMEOUT_SECONDS` (default 1800, maximum 3500) to the
guest harness;
see [`firecracker-harness/README.md`](firecracker-harness/README.md) for its
runner and output contract.

CI and product integration run as binaries and processes on
`terrascale-builder`; KVM and guest images are not prerequisites. The optional
Firecracker harness remains useful for development experiments. The registered
matrix separately requires verified 4-vCPU, 8,192-MiB guests for
hardware-comparable performance qualification, with swap disabled and separate
service/load-generator guests where applicable. A host process diagnostic, or
a run without that matrix evidence, leaves comparison rows open. Do not treat
missing KVM or images as a reason to skip ordinary process CI.

The current Rust driver implements SQLite and a matching initial embedded
TerraBase subset. Its
[`README`](terra-rust/README.md) describes the real executable, fixture, receipt,
and guest invocation contract. Many matrix axes and full resource gates remain
open. No performance comparison is valid until equivalent
engine calls, durability, data, index shape, resource budgets, guest setup, and
repeated paired trials are recorded in the matrix.

## Results and evidence

Create a stable fixture ID before each experiment. Save its generator/source
revision, seed, dataset specification, and digest under `bench/fixtures/`.
Copy qualified JSON receipts, raw JSONL samples, profiles, and environment
manifests to `bench/<matrix-version>/<case>/<trial>/`. Put an easy-to-compare
Markdown table in `bench/` for each measured case or run series, as required by
[`AGENTS.md`](AGENTS.md). Rows identify both repository revisions and binary
hashes, the matrix version, case, trial, environment manifest, engine,
durability mode, primary metric, variance/confidence interval, and result
status. Retain failed and negative experiments. Keep the SQLite scoreboard
statuses distinct: `MEETS_FLOOR`, `BELOW_FLOOR`, `INCONCLUSIVE`,
`UNSUPPORTED`, or `NOT_COMPARABLE`, with the receipt and reason; compute
throughput verdicts with `terra-rust`'s `throughput-floor`. Do not fill an open row from a mock,
diagnostic host run, historic report, or a SQLite-only measurement.

The [matrix](bench/terrabase-rust-acceptance-v2.md) defines the pinned SQLite
version, fair tuning and guarantees, minimum paired trials, floor
criterion, required workload/resource axes, and full evidence standard. Changes
to cases or thresholds require a new matrix version registered before evaluating
a candidate.
