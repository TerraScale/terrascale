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
release build. The engine-enabled equivalents are check-engine, lint-engine,
and build-guest-engine; they compile the real embedded TerraBase caller into
the same guest binary as the SQLite lane. Build work runs at the lowest CPU
and I/O scheduling priority:

```sh
mise exec -- nice -n 19 ionice -c 3 make fmt check lint build
mise exec -- nice -n 19 ionice -c 3 make build-guest MUSL_CC=/path/to/musl-gcc
```

`make test-build` compiles the feature-enabled musl test executable without
running it on the host. Run it inside Firecracker with `make test` (or
`make test-vm`), passing `TEST_BINARY` as its absolute path, a unique `RUN_ID`,
and `HARNESS_BINARY`, `GUEST_AGENT_BINARY`, `HARNESS_SHA256`, and
`GUEST_AGENT_SHA256` from one frozen harness packet. The test entrypoint checks
the executable pair before launch. The harness has its own
`make -C firecracker-harness test-build` and `test-vm` targets for controller
tests. Bare `make` in the root and harness directories prints help.

Forgejo CI checks out this repository at the event revision under
`ts-benchmark/` and a paired TerraBase checkout under sibling `terrabase/`.
Configure `TERRABASE_CI_SHA` as a full, published TerraBase commit SHA whose
tree contains `crates/tb-core` and `crates/tb-storage`; CI checks the resolved
commit and both manifests before compiling either driver mode. Even SQLite-only
Cargo commands resolve those optional path dependencies, so the sibling
checkout is required. The default job token must have read access to the
TerraBase repository; when it does not, configure the read-only
`TERRABASE_READ_TOKEN` secret. The current local engine source needs publication
before a qualifying CI pair can be configured. CI compiles the engine-enabled
caller and musl binaries. Its Firecracker job runs the harness, image-builder,
and engine unit binaries and a small real `run-terrabase` caller smoke inside
verified guests. These checks establish runtime integration, not benchmark
acceptance or performance results. CI never executes test binaries or
measurements on the host. CI selects the same pinned Rust toolchain through
rustup proxies; it does not require mise on the runner.

`build` is a host artifact. `build-guest` produces the statically linked
`terra-rust/target/x86_64-unknown-linux-musl/release/terrabase-sqlite-baseline`
used by the current guest image. The pinned Rust musl target and a musl C
compiler are required because bundled SQLite contains C code.

Run `make vm-preflight` to check pinned inputs and KVM, then `make vm-smoke`
to boot the guest and check the effective limits. `make vm-run` delegates a
caller-supplied `PROGRAM`, `SUBJECT_REPO`, `RUN_ID`, and optional `ARGS` and
`TIMEOUT_SECONDS` (default 1800, maximum 3500) to the
guest harness;
see [`firecracker-harness/README.md`](firecracker-harness/README.md) for its
runner and output contract.

Building on the host is allowed. **Tests and measurements qualify only when
run inside verified Firecracker guests** with 4 vCPUs and 8,192 MiB RAM per
guest and swap disabled. The environment harness checks the effective guest
configuration and records Firecracker, kernel/rootfs image hashes, guest CPU,
memory and swap, host CPU affinity, and output filesystem. Dedicated SSD,
cache-state and networked topology qualification remain open. RustFS, Von, and
load generators occupy separate guests when applicable. Lack of KVM, images,
or a required service is an environment failure, never a host fallback.

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
