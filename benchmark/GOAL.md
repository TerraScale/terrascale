# TerraBase

**The single authority for this repo is the workspace goal: [`../GOAL.md`](../GOAL.md).**
This page is a pointer, not a plan. If anything here contradicts the
workspace goal, the workspace goal wins. Repository `AGENTS.md` remains a
hard rule; ADRs record durable local context but decide nothing the
workspace goal has decided.

## Mission

TerraBase is TerraScale's private multi-region database engine, in Rust:
**embedded SQLite storage** (one file per customer database — D-27),
document bodies as **canonical JSON bytes parsed exactly once at write**
(D-30), TQL translated to SQL over **auto-indexed extracted columns**
(D-31 — optimizer-driven, no stale reads), CRDT merge and causality, the
**sync engine** (reactive views), the **replication engine** (leaderless
cross-region, RF=2 per region), key-range shard split (D-28), **Litestream**
backup/PITR (D-29), per-database encryption, and authoritative usage
metering. Private protobuf/gRPC protocol (TBEP); `terrabase-go` is the only
network client. ADR 0040 records D-27..D-31 in-repo.

## STORAGE PIVOT (2026-08-30 — decisions D-27/D-28/D-29)

The custom storage stack (LSM, RootFiles, custom WAL, compaction,
segments/manifests) is **deleted, effective immediately** — the 2026-08-30
benchmarks measured it 20–70x behind PostgreSQL on reads, and the owner
ordered up-front deletion with no transition period (TB-21 runs in lockstep
with TB-20 as one delete-and-replace sequence; every push stays green). Data
lives in mainline SQLite (`rusqlite`, WAL mode, one writer per file, group
commit); every document write commits atomically with its
publication-journal row in one SQLite transaction. Everything above the
storage seam — CRDT/causality, sync, replication, TBEP — survives unchanged.
See workspace tasks TB-20 (SQLite core), TB-21 (immediate deletion), TB-22
(Litestream), TB-23 (auto-indexing), and the rescoped
TB-5/TB-7/TB-10/TB-12/TB-15/TB-17/TB-18. Docs and ADRs describing the old
storage stack are deleted or marked superseded in the same sequence.

## Verification set (must pass before any merge)

```bash
cargo fmt --all --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
buf lint
./scripts/validate-release-contracts.sh
cd terrabase-go && gofmt -l . && go vet ./... && go test -race ./...
```

## Working here

- Pick tasks only from the workspace goal (`../GOAL.md`, section 6, Lane TB)
  and claim them in its live-status table.
- **No PRs (D-32)**: commit directly to `develop`; the verification set
  above must pass locally BEFORE every push; commit messages start with the
  task ID; a red `develop` stops the line.
- Definition of done, commit discipline, and the simplification playbook are
  workspace goal section 4. The old evidence/receipt culture is retired.
