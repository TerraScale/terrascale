# Rust parity output-failure qualification, 2026-10-28

Follow-up to the synthetic controls: no live tenant or latency result. The runner
now records output-write failures, refuses mutation if the owned identity row
could not be written, and rejects a lost cell/deletion row while preserving cleanup.
The staging script also pins the toolchain bin directory: in the first recheck,
a mise cargo-clippy shim selected rustc 1.99.1 dependencies under cargo 1.98.1,
which failed with E0514. Those artifacts were not accepted.

The fixed source `f359faf926b0d25f638a8a3b21d2610b8797b78f` passed rustfmt,
clippy for example/tests with warnings denied, seven controls (zero failures or
ignored), Release build, and all eight offline CLI cases. The lost-row paths were
reviewed in source; no live setup/cleanup or simulated disk-full session ran.

Ran on delta via `ts-heavy-any nice -n 19 bash /tmp/perf-api-rust-qualify.sh
--repo terrascale --branch fix/api-rust-parity`. SDK and Cargo.lock are unchanged.
Main source SHA-256: `6905fed0bae6862c2c392b50a3eb4cb1408ab8109ebe10926a5f1d4078ba7100`.
Release binary: `db1ae6b303dab20fe5101806d395883fef7114c20246c7ff59d2c72636697cd3`.
Raw log/script/source identity: delta managed `fix-api-rust-parity-9af0251ebbd63b43768b65541559f1a88c451410738ab8f7645e9fa4f1243f49`,
`benchmark/bench/rust-output-qualification-b/`; staged clean SDK:
`~/remote-builds/rust-parity/output-qualification-b`.

Lifecycle follow-up: `ff62897a5` passed the same full-stack checks, eight controls
and eight CLI cases on delta via `ts-heavy-any nice -n 19`; no live requests.
The added policy control requires reconciliation for uncertain-create/404 and retains
logical-acceptance/known-absence cases. The runner now retains recovery for that uncertainty.
Artifacts: same managed checkout, `benchmark/bench/rust-lifecycle-qualification-a/`;
SDK stage `~/remote-builds/rust-parity/lifecycle-qualification-a`, lock unchanged.
Main SHA-256 `6b61709879220f1616907bdcac1ac6803ca20944e10e40d8090d1fc518e0080c`;
binary `3f2074fdc1d37b21d8da8e4d0640725a720f7ee77509ddfb9b69978a3e458681`.

Final gate correction: `c6fb8a6cf` also rejects a panicked worker rather than silently
dropping its join error. Delta's same pinned full-stack command passed nine controls,
eight CLI cases, rustfmt, both clippy checks and Release build, with no live calls.
Artifacts: `benchmark/bench/rust-worker-qualification-a/` in the same managed checkout;
stage `~/remote-builds/rust-parity/worker-qualification-a`, lock unchanged, binary
`bee9d40d3655f55704e8e245ca5ae9ea49a0a481098d2fc94e4d6c07ea8f7110`.
