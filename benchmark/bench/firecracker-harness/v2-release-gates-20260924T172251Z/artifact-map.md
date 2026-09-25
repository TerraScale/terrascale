# V2 release gate artifacts

Repository revision: `5685f163bb7e9339e8eb0c839fe2512aaf562648`. The checkout contains concurrent uncommitted changes, so `ts-benchmark-gate-source.sha256` records the first frozen producer snapshot. A final source inventory and gate rerun are pending lint fixes.

| Artifact | Size | SHA-256 | Guest dependency closure |
| --- | ---: | --- | --- |
| `fc_harness-tests-x86_64-musl` | 17,407,896 bytes | `3e1840d6f5b01ad8e8cba3db7000d0b13506182abb7ad29087c27ba048697d7e` | x86_64 static PIE; no ELF interpreter or `DT_NEEDED` shared libraries |

The test binary is an immutable copy of the `make -C firecracker-harness test-build` output. It has **not** been executed on the host or in a guest. The Firecracker guest runner still needs the controller and guest agent executables, `/dev/kvm`, pinned kernel and rootfs, four allowed host CPUs, at least 9 GiB available host memory, a unique `RUN_ID`, and `TEST_BINARY` pointing to a staged copy of the test ELF. Tests that reference source checkout paths require explicit guest staging; the static ELF has no shared-library dependency.

| Gate command | Outcome | Log |
| --- | --- | --- |
| `make -C firecracker-harness check-image-builder` | Passed at initial producer freeze | `ts-benchmark-check-image-builder.log` |
| `make -C firecracker-harness check-controller` | Passed | `ts-benchmark-check-controller.log` |
| `make -C firecracker-harness lint-controller` | Failed on three new reader Clippy findings; corrected | `ts-benchmark-lint-controller.log` |
| `make -C firecracker-harness lint-controller` | Passed after correction | `ts-benchmark-lint-controller-rerun.log` |
| `make -C firecracker-harness test-build` | Passed; compile only | `ts-benchmark-test-build-controller.log` |
| `make -C firecracker-harness check-image-builder` | Passed at second producer freeze | `ts-benchmark-check-image-builder-rerun.log` |
| `make -C firecracker-harness lint-image-builder` | Failed on strict Clippy findings and test-only E0624; corrections in progress | `ts-benchmark-lint-image-builder.log` |
| `make -C firecracker-harness test-build-image-builder` | Failed on test-only E0624; correction in progress | `ts-benchmark-test-build-image-builder.log` |

No benchmark timings were collected, and no test executable was run.
