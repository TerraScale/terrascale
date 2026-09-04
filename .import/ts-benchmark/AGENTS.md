AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!

# RULES

- This repo must use Rust only
- This is terrabase a multi region database with a really efficient and fast sync engine
- When interacting with forgejo use the "fj" cli
- We need to expose metrics to prometheus compatible
- We should use crates that are tested and popular whenever possible instead of creating our own solutions
- When the human asks for a commit, commit and push exactly as asked — do not run tests, checks, or builds first


- Rust. SQLite is the only storage; the member serves the public API itself.
  LSM, Studio, spatial, vector, TQL, Postgres-anything: deleted, never back.
- Engine designs: replication and sync = ADR 0044; auto-indexing = ADR 0045;
  storage and bytes-everywhere = ADR 0040. Build to them.
- SQL is the only query language; writes are document operations. Zero
  stale reads and "no index may slow a query" are invariants, not goals.
- This repository publishes the contracts and the SDK conformance suite
  (`make contracts`); consumers pin them.
- The binary never leaves our infrastructure. `make stack` is ours, not
  shipped. TBEP gRPC is private; `terrabase-go/` is its only client.

# Benchmark
- We need to save all benchmarks in an easy to see .md tables so we can compare from the very beginning the difference and improvements
- Benchmarks land as `.md` tables in `bench/`.
