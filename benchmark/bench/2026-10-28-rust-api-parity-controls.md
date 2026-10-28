# Rust SDK / raw HTTPS parity: synthetic controls (2026-10-28)

Offline controls for [`benchmark/clients/rust/parity`](../clients/rust/parity/README.md).
No credentials, tenant, production request or latency measurement was involved; this is
not a performance result. Live setup, cleanup and every timing remain unqualified.

| Item | Value |
| --- | --- |
| Host | delta, through `ts-heavy-any nice -n 19` |
| SDK | ts-rust `v0.0.5`, `38a33829dd7099f22783ba79d8b187450328a59b`, clean HTTPS clone |
| Toolchain | cargo 1.98.1 (797e8a9bc 2026-08-05), rustc 1.98.1 (48a229cea 2026-09-01) |
| SDK `Cargo.lock` SHA-256 | `f8fd03b8c4e9a85efb407e2183428a73e09ab7695343f08a7eadbd52e93a312e` (unchanged, `--locked`) |
| `main.rs` SHA-256 | `6f6a11dc608307454bc5256983d3b3ff58c9fce70ee785314339499bbb360899` |
| `support.rs` SHA-256 | `38980f43dfb7547f9b009eefd6cdf1a2e1b33169a4ac25af4ca3afc78b2de723` |
| `controls.rs` SHA-256 | `b920d401a1926cf9760bd060e31c6e4cdb93d581150463aba547c75c09214b22` |

| Check (`stage.sh`) | Result |
| --- | --- |
| rustfmt `--check` | pass |
| clippy `-D warnings`, example and example tests | pass |
| peers: all regions, gru/sjc/fra required, malformed lines rejected | pass |
| seeds: compact JSON of exactly 256 and 4096 bytes | pass |
| raw answer: status, protocol, exactly one matching region, id/version/state/body | pass |
| readiness bounded at 60 s; interrupt stops a guarded phase | pass |
| window: warmup start excluded, two in-window starts kept, slow tail drained, elapsed 5.5 s | pass |
| first failure stops workers; a stuck cell is reported after the 30 s drain | pass |
| reservoir bounded at 16,384 of 20,000 samples | pass |
| release build | pass |
| CLI: usage (2), unreadable credentials, existing output kept, new output 0600, missing/partial/malformed peers (1), symlinked binary | pass |
| staged checkout differs from the pin only by `examples/api_parity/` | pass |

Paused-clock controls exercise the measurement code, not network timing. SDK attempts,
protocol and TCP connections stay `null` by design (see the README limitations).
