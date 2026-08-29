# TerraBaseV2

TerraBaseV2 is the independent Rust implementation of the TerraBase
database engine: an active-active, multi-writer, leaderless document
database with Loro-backed CRDT convergence, a ZoneTree-inspired LSM
storage engine with NVMe and S3-compatible tiers, and the TerraBase
Engine Protocol (TBEP) serving C#/.NET as the only supported client
language.

The workspace goal, acceptance criteria, and execution plan (P0-P7) live
at `../docs/specs/GOAL.md`. The glossary is in `CONTEXT.md`. Contract
facts live in the domain-owned fragments under `parity/contracts/`.
Mutable V2 status and Proof IDs live in the disjoint state overlays
under `parity/overlays/`. The canonical `parity/inventory.json` is
generated output. Workers update fragments or overlays, then the
generator owns the inventory write.

## Honest status (2026-08-28, P0 truthfulness pass)

Claim vocabulary is defined in `CONTEXT.md` and the charter's claim
discipline; the words below use it exactly.

**Implemented (focused tests exist, no multi-region qualification):**

- Rust engine workspace: behavioral modules for storage/WAL/recovery,
  documents and transactions, sessions, query and indexes (including
  auto-indexing and TQL foundations), data families (attachments,
  counters with ratified PN-counter merge encoding, time-series,
  revisions, compare-exchange, HiLo, lifecycle, schema validation),
  streaming with bounded backpressure, sharding, backup/restore,
  smuggler export/import with V1→V2 logical migration, and benchmark
  adapters — each landing with focused test suites through the fleet's
  two-gate discipline. See `parity/inventory.json` for per-row state.
- `crates/terrabase-tbep`: a Rust UDS server with bounded, versioned,
  typed MessagePack frames and generated Rust/C# contracts.
- `host/TerraBase.Protocol`: the C# client library — multiplexed
  channel, cancellation, idempotency, typed errors, subscription
  routing. `host/TerraBase.Api` and `cli/TerraBase.Cli` consume it.
- `studio/`: the local operations Studio (Vinext).
- `crates/terrabase-sync-core`: Loro 1.13.9 deterministic deltas,
  snapshots, checkpoints, compaction, and replay.
- Schema validation, versioned schema migrations, and typed
  TBEP/C# schema shapes.
- NATS integration covering KV/config/discovery audit.

**Unresolved or conflicting with the accepted architecture (named, not
hidden):**

- The replication contract and implementation use a single leader per
  range with ownership and fencing. That conflicts with the accepted
  leaderless, masterless architecture (ADR 0018) and is replaced by
  P2 — symmetric replica sets, request-scoped coordination,
  quorum/idempotency evidence.
- `terrabase-sync-core` fixes one server peer, accepts only one
  server's journal materialization, and maps only a flat scalar
  document subset: convergent delivery, not the required multi-server
  CRDT database.
- The storage engine rewrites a full tenant root per commit (a 512-write
  commissioning window appended 154,246,326 durable bytes for ~300-byte
  documents). P1 replaces it with the LSM organization of ADR 0016.
- No NVMe/S3 tier manifest, cold-object format, tier-aware cache,
  adaptive multi-tenant Storage Tier Mover, remote cold-read fallback,
  `backup` cluster mode, or `BackupAck` exists yet (ADR 0016/0017; P1,
  P2, P5).
- The QUIC client binding, TLS identity, and remote admission path are
  not qualified; UDS is the tested TBEP binding (ADR 0013).
- Typed NATS request/reply for cluster join, topology, health, and
  route lookup is not built (ADR 0014 D-4; P5).
- The schema×CRDT-merge interaction (three modes, post-merge
  validation, quarantine, delete-wins shadow history) is frozen in
  `docs/design/p0-schema-merge-contract.md` and not yet implemented
  (P3).

**Evidence state:**

- The parity inventory validates structurally; per-row dispositions and
  Proof IDs are recorded in the overlays as behavior lands (P6 owns the
  complete sweep). A row with an empty proof list is not a claim.
- The PostgreSQL performance objective is `inconclusive`: no comparable
  PostgreSQL or V1 reference receipts exist yet (P7 owns the benchmark
  program; the structural write-amplification blocker above is P1's
  exit criterion).
- The frozen P0 design contracts that govern the remaining phases live
  under `docs/design/p0-*.md`; decisions are recorded in `docs/adr/`
  (0013-0018 cover the 2026-08-28 architecture revisions).

## Local checks

The pinned Rust toolchain is in `rust-toolchain.toml`. Run the complete
local checks with:

```bash
./scripts/check.sh
```

The individual checks are useful while editing:

```bash
cargo fmt --all -- --check
cargo check --workspace --locked
cargo test --workspace --locked
cargo test --workspace --locked --release
cargo clippy --workspace --all-targets --all-features --locked -- -D warnings
cargo doc --workspace --no-deps --locked
python3 scripts/validate_parity.py --check
python3 scripts/test_parity_tooling.py
python3 scripts/test_v1_citations.py
python3 scripts/check_license.py
./scripts/check_secrets.sh
```

The C#/.NET side (pinned SDK in `global.json`) builds and tests the
same way CI does (TUnit projects are executables; `dotnet test`'s
VSTest target is unsupported on .NET 10):

```bash
dotnet build host/TerraBase.Protocol/TerraBase.Protocol.csproj --configuration Release
dotnet run --project host/TerraBase.Protocol.Tests/TerraBase.Protocol.Tests.csproj --configuration Release
dotnet build host/TerraBase.Api/TerraBase.Api.csproj --configuration Release
dotnet run --project host/TerraBase.Api.Tests/TerraBase.Api.Tests.csproj --configuration Release
dotnet build cli/TerraBase.Cli/TerraBase.Cli.csproj --configuration Release
dotnet run --project cli/TerraBase.Cli.Tests/TerraBase.Cli.Tests.csproj --configuration Release
```

The Studio app (Vinext, `studio/`, pinned pnpm via `packageManager`)
runs `pnpm run test:unit` and `pnpm run check`; its Playwright e2e lane
runs where browsers are provisioned.

The V1 contract audit is a separate qualification input because it needs
an exact TerraBase V1 checkout. Set `TERRABASE_V1_ROOT` and run
`./scripts/check.sh` to execute the strict gate. Without that checkout the
gate reports `UNRUN` and fails. W2 contract review can use the non-release
audit command below, which records unresolved test obligations in
`parity/v1-test-gaps.json`:

```bash
python3 scripts/validate_v1_citations.py \
  --mode contract \
  --v1-root /path/to/TerraBase \
  --workspace-root /path/to/TerraScale \
  --gap-report parity/v1-test-gaps.json
```

The current extraction has 258 rows, 251 V1-backed rows, 267 source
citations, and 346 exact test citations. One row (PB-5012) keeps a test gap
adjudicated by parent decision PD-2026-08-24-02 with an open V2-side proof
obligation; the adjudication input is `parity/adjudications.json`. Those
counts describe citation coverage, not implementation: per-row V2 status
lives in the overlays and the generated inventory.

No global tools or credentials are required for the foundation checks.
