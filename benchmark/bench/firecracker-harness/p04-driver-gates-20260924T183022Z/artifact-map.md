# P04 driver and current TerraBase engine build evidence

Status: all seven final Make gates passed on 2026-09-24 UTC. This is compile and artifact evidence only; neither the test executable nor the benchmark driver was run on the host. Guest execution and P04 measurement remain pending.

## Immutable source identity

- ts-benchmark revision: `5685f163bb7e9339e8eb0c839fe2512aaf562648` plus the uncommitted, hash-pinned driver changes below.
- terrabase revision: `352d9f61185d1b685e3032519579040d169ad47d`. The engine was copied exclusively from Foundation's corrected read-only `target/verification/tb-mode-c0-147bf-f160-modefix-8641bd67-20260924-source-snapshot`, not from the changing live tree. Its 91-entry origin `source.sha256` has SHA-256 `6e48da3dc56163ee29ff18c42e24bfc6eae5384114d53609ea198be10a775d4e`; all 73 copied engine entries passed `sha256sum -c --ignore-missing` against it. The 18 omitted origin entries are repository documentation and metadata outside the copied Cargo source tree.
- Final read-only packet: `../p04-driver-current-engine-final-20260924T183724Z`. Its `packet-source.sha256` covers every one of the 81 copied source, manifest, lock, toolchain, and matrix files. Manifest SHA-256: `b5f251d333300086f15bba8df583ad6e267c7012f8975d5f892c459aff07969e`. `final-packet-source-check.log` records a successful post-build check of all entries.
- The active local path dependency closure is driver → `tb-core` and `tb-storage` → `tb-format` and `tb-core`. Other sealed workspace crates are present only for Cargo workspace resolution; changing live Packet A files were not used. The complete inventory includes `tb-storage/src/mode.rs` SHA-256 `8641bd67bdcfa36da8e8a959c04cf763afc76c162030cf0e64446aedf1748c73` and all transitive source and `include_bytes!` inputs, even where the compact runtime receipt has only selected per-file hashes.
- Driver `src/main.rs` SHA-256 `b3d08a1d988c66092fb6f1d1b7624d35d5f41a4e5b63d64bebdcfe61b100d360`; adapter `src/terrabase.rs` `c150595f145b2c6cd6bb841f437fe2ce17b148f71046c03a2624101bd66e3aa9`; `Cargo.lock` `3c1a4f2aabd538ad3c6e106dcf1cb6dcec62d302b33d4b4f1666be3868a2a723`; driver `Makefile` `22775bcb0ee3cc3b0c0c0cef00bcd0143914858ae304b5a9758dc6b78be20b21`. The lock update added only `libc` and `rustix` to the `tb-storage` package dependency list; package versions and checksums were preserved.

## Gates and payloads

Each command used `CARGO_TARGET_DIR=<this directory>/target` and `ionice -c 3 nice -n 19 make -C <final packet>/ts-benchmark/terra-rust <gate>`. Each Make target itself uses `nice -n 19 ionice -c 3`; Cargo commands use `--locked -j 2`. The test gate used `cargo test --no-run` for musl and never executed the test binary.

| Final gate | Exit | Log |
| --- | ---: | --- |
| `fmt` | 0 | `final-fmt.log` |
| `check` | 0 | `final-check.log` |
| `lint` (`--all-targets -D warnings`) | 0 | `final-lint.log` |
| `check-engine` | 0 | `final-check-engine.log` |
| `lint-engine` (`--all-targets -D warnings`) | 0 | `final-lint-engine.log` |
| `test-build` (feature-enabled musl, compile only) | 0 | `final-test-build.log` |
| `build-guest-engine` (feature-enabled musl release) | 0 | `final-build-guest-engine.log` |

Both payloads below bind to the **same** 81-entry packet manifest SHA-256 `b5f251d333300086f15bba8df583ad6e267c7012f8975d5f892c459aff07969e`. They are mode 0555 copies in `payloads/`; `file` reports x86-64 static PIE and `readelf` found neither `PT_INTERP` nor `DT_NEEDED` in either ELF.

| Payload | Bytes | SHA-256 | Use |
| --- | ---: | --- | --- |
| `payloads/terrabase-sqlite-baseline-tests` | 23,028,728 | `1679d397b482bf4df896ee6dac8a84ab4c013d9837d275782b60149abb0c71c8` | Firecracker `test-vm` only |
| `payloads/terrabase-sqlite-baseline` | 4,942,960 | `93e2099f1797033aab0263732bacb551209dad064ccc5847dcd66b3a17cf5ed2` | paired SQLite/TerraBase guest lanes |

The first packet's `check.log` preserves the original `--locked` failure from the stale `tb-storage` dependency entry. `cargo-lock-diff.txt` records the two-edge fix. `corrected-lint.log` preserves the two Clippy findings in the old driver revision; the reviewed main.rs correction is in the final packet. `results.txt` records every attempt and exit status. No guest result or P04 performance verdict is asserted here.
