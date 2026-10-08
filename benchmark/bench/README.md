# Benchmark evidence transition (TS-761)

Superseded Rust-engine, Firecracker, SQLite/Turso and local acceptance archives
are being removed under TS-761's authorized greenfield cleanup. The original
receipts and frozen source snapshots remain accessible in Git history.

[PR #13](https://git.terrascale.tech/TerraScale/ts-benchmark/pulls/13) introduces
the public-SDK regional capacity protocol and runner. Its SDK/lifecycle and live
run prerequisites remain explicit; these deleted diagnostics do not qualify
regional capacity. Fresh existing smoke outputs still land in `bench/` until
that PR replaces the old harness. No measured result is invented or relabelled.
