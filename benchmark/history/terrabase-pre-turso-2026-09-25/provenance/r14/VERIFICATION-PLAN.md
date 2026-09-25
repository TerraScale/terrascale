# R14 source freeze and verification plan

R14 freezes the reviewed Fast test corrections at repository HEAD `352d9f61185d1b685e3032519579040d169ad47d`. The immutable input is `source/`, with 138 files and manifest SHA-256 `9b4483ea61bb76b02fb122192f552cc36669c7fe835e877da2eb3ca7cacc23e2`. `SOURCE-SHA256SUMS.before`, `SOURCE-SHA256SUMS`, and `SOURCE-SHA256SUMS.after` match byte-for-byte; the corresponding current-tree/copy/current-tree manifests were computed around the copy.

R13 (`70fe69b7f8525d7a607e387773ce0181f0d3d55ae0a6b062d10c1bcdc7907117`) was preserved. R14 differs from it in four source-manifest paths: `crates/tb-server/tests/replication_process.rs` changed from `eef17a45509f3501d93de9f606f82c17b6e4bbecf629f2b7ff778e2494ea798c` to reviewed release `254b0fd190ac544cd51858bef2027a2c94f0512bc225c0530d1cb8305302d689`; the accepted R12 Fast outcome and R13 freeze status were refreshed in `docs/acceptance.md`, `docs/coordination-test-runtime-20260925.md`, and `docs/evidence-ledger.md`. All other 134 paths match R13. The test correction sequence applies the reviewed `seed_local_source_events` expiry fix (R13 preimage to `87807d4b…`) then the guarded wrong-signed-leaf r2 rebase; Distributed_design reports final exact hash, `rustfmt --check --edition 2024`, reverse-apply guard, and byte comparison with its reviewed staged candidate all passed. The test file is the only Rust-source delta from R13.

R12 Fast runtime evidence remains 35 passed / 6 failed out of 41, bound to R12. R13 has no compile/runtime result. R11 product/MSRV results cannot be reused because R13 changed production source and was not built. R11's recorded Rust 1.88 target metadata is genuine, but does not qualify the later production closure. R14 therefore requires all listed compilation gates against this frozen source.

The source boundary retains the existing storage/object workspace files as build dependencies and includes no pair/harness paths from the sibling benchmark repository. Fast/Slow no-run compile gates are new for the corrected test source. No VM or test execution is authorized by this compile-gate handoff.

## Gate sequence

Use only Make targets. Prefix every Make command with `ionice -c 3 nice -n 19`; Make recipes also apply `nice -n 19`. Use two Cargo jobs. Pin direct tool binaries and `PATH` for the selected toolchain, and explicitly unset `RUSTUP_TOOLCHAIN` for each gate. Before compiling, save the selected `cargo --version`, `rustc --version`, and `rustdoc --version` outputs. Do not rely on rust-toolchain override behavior or mise shims.

Default toolchain directory: `/home/mariogk/.rustup/toolchains/1.98.1-x86_64-unknown-linux-gnu/bin`. Set `CARGO`, `RUSTC`, and `RUSTDOC` to absolute binaries in that directory; put it first in `PATH`. Use fresh shared target `/home/mariogk/Projects/TerraScale/terrabase/target/verification/tb-server-runtime-freeze-20260925-r14/cargo-target-default` for default gates. MSRV directory: `/home/mariogk/.rustup/toolchains/1.88.0-x86_64-unknown-linux-gnu/bin`, with separate fresh target `.../cargo-target-msrv188`.

Run sequentially and stop on the first real gate failure:

1. `make fmt-check` on the pinned default toolchain.
2. `make build CARGO_ARGS="-p tb-server" CARGO_TARGET=x86_64-unknown-linux-musl CARGO_BUILD_JOBS=2`.
3. `make test-fast-build CARGO_TARGET=x86_64-unknown-linux-musl CARGO_BUILD_JOBS=2` and `make test-slow-build CARGO_TARGET=x86_64-unknown-linux-musl CARGO_BUILD_JOBS=2`.
4. Strict default `make clippy` with Make defaults, then `make clippy CARGO_ARGS="--workspace --features slow-tests"`.
5. With explicit Rust 1.88 binaries and `RUSTUP_TOOLCHAIN` unset, run `make build CARGO_ARGS="-p tb-server" CARGO_TARGET=x86_64-unknown-linux-gnu CARGO_BUILD_JOBS=2`.

Capture each full command, log, and exit code under this R14 directory. After the first successful default build, save and inspect the target's `.rustc_info.json`; it must identify Rust 1.98.1 and its installed 1.98.1 toolchain path. After the successful MSRV build, save and inspect the separate target metadata; it must identify Rust 1.88.0 and the 1.88.0 toolchain path. Recheck both metadata files after their gate sequences. A target metadata mismatch invalidates that gate even if Cargo exits zero. Keep R12 runtime results separate and do not run a VM in this lease.

At source-freeze time, no R14 Cargo build, Clippy, VM, runtime test, or sccache daemon was started.
