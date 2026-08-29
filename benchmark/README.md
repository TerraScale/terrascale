# TerraBaseV2

TerraBaseV2 is TerraScale's private Rust database engine. The target is a
leaderless, multi-writer document database with TerraBase-native delta CRDTs,
Partition-scoped causal metadata, local-Region durable writes, direct QUIC
replication, anti-entropy, reactive query synchronization, a ZoneTree-inspired
LSM, TQL, automatic indexes, typed document relationships, optional schemas,
and multi-tenant resource governance.

The current cross-repository charter is [the workspace goal](../GOAL.md).
[AGENTS.md](AGENTS.md) defines repository rules, and [CONTEXT.md](CONTEXT.md)
defines current terms. RavenDB and TerraBase V1 parity artifacts are legacy
deletion targets, not product contracts or qualification gates.

## Supported boundary

TerraBaseV2 is not a public customer server. The independent C#/.NET 10
`TerraScale.Database` API is the sole engine gateway. It communicates with
TerraBaseV2 through generated MessagePack TBEP contracts over QUIC with mTLS,
including same-host loopback.

Public TypeScript and .NET clients use versioned TerraScale HTTPS and reactive
contracts. Dashboard uses the same public TypeScript SDK as customer Projects.
No customer, SDK, Dashboard module, or MCP tool connects to a TerraBase Member,
TBEP, NATS, or storage directly.

Region replication and client sync are separate:

~~~text
TerraBase Region <---- native delta CRDT + DVV + QUIC ----> TerraBase Region

Public client <---- authorized query snapshot/patch/checkpoint ----> Database API
~~~

NATS is an optional control/configuration/discovery adapter. Database data and
correctness do not depend on it.

## Honest implementation status

The accepted architecture is not yet the implemented architecture. At the
2026-08-29 charter revision, the current tree still contains these useful
components:

- Rust storage, WAL, recovery, document, transaction, session, query, index,
  TQL, family, streaming, sharding, backup, restore, and schema-migration
  benchmark modules with focused tests;
- a bounded, versioned MessagePack TBEP implementation and generated Rust/C#
  contract artifacts;
- C# protocol, API host, and CLI projects that exercise TBEP;
- a Vinext Operations Studio;
- native CRDT and causal sync and replication crates;
- older range-owner replication and sharding contracts;
- schema validation and migration foundations; and
- NATS KV/config/discovery integration.

Those facts describe the current source. They do not qualify the target
architecture.

## Required replacement work

The following code and evidence block an architecture-complete claim:

- The Loro removal from persistence, replication, sync, and supported wire
  formats is complete as of 2026-08-29: `grep -ri loro crates/` (excluding
  `target/`) returns nothing, the root `Cargo.lock` carries no `loro` pin, and
  the payload codecs are the TerraBase-native `terrabase-native/1` and
  `terrabase-snapshot/1` of ADR 0022/0024, and `TerraScale.Database` vendors
  the byte-identical regenerated TBEP registry. Historical Loro mentions
  survive only in ADRs, the workspace charter, the `tracer/` record, and
  immutable past receipts.
- Single range ownership, fencing, and leader-shaped replication must become
  symmetric replica sets with request-scoped coordination only.
- Production active placement must support RF=2 per selected Region.
  `RegionalDurable` requires both local Members. `GlobalDurable` is the
  explicit WAN-bound acknowledgement level.
- Region replication needs direct QUIC delta streaming, durable range
  acknowledgements, anti-entropy, Merkle repair, membership epochs, and
  deterministic fault simulation.
- TBEP must qualify QUIC and mTLS as its only process transport. UDS is legacy
  implementation debt, not a supported target binding.
- The C# public gateway must move to the independent
  `TerraScale.Database` repository. C# projects under this repository are
  migration sources and test references until that cutover completes.
- Reactive sync must track query and authorization dependencies, maintain
  incremental results, use view-scoped checkpoints, bound slow clients, and
  share one golden corpus across Rust, TypeScript, and .NET.
- Every foreground and background subsystem must enforce tenant and database
  resource budgets before Orchestrator scaling is needed.
- Snapshot, compaction, PITR, tombstone collection, replica retirement, and
  actor summarization need one stable-frontier safety proof.
- Current storage write amplification, tiering, immutable backup, and recovery
  gaps remain open until the matching Recovery and Benchmark Receipts pass.

Existing Loro-era, UDS, host, replication, sync, and auth-dependent results
cannot qualify their replacements.

The PostgreSQL performance objective remains `inconclusive` until
TerraBase.Benchmark produces comparable, valid receipts. No statement in this
README claims production readiness or performance parity.

## Local checks

The pinned Rust toolchain is in `rust-toolchain.toml`. Use focused checks while
editing:

~~~bash
cargo fmt --all -- --check
cargo check --workspace --locked
cargo test --workspace --locked
cargo clippy --workspace --all-targets --all-features --locked -- -D warnings
cargo doc --workspace --no-deps --locked
python3 scripts/check_license.py
./scripts/check_secrets.sh
~~~

Do not run `./scripts/check.sh` locally. The full gate is CI-owned under the
workspace development constraints.

Until the C# migration finishes, the legacy protocol projects can be checked
with:

~~~bash
dotnet build host/TerraBase.Protocol/TerraBase.Protocol.csproj --configuration Release
dotnet run --project host/TerraBase.Protocol.Tests/TerraBase.Protocol.Tests.csproj --configuration Release
dotnet build host/TerraBase.Api/TerraBase.Api.csproj --configuration Release
dotnet run --project host/TerraBase.Api.Tests/TerraBase.Api.Tests.csproj --configuration Release
dotnet build cli/TerraBase.Cli/TerraBase.Cli.csproj --configuration Release
dotnet run --project cli/TerraBase.Cli.Tests/TerraBase.Cli.Tests.csproj --configuration Release
~~~

The Studio uses its pinned package manager and runs its native unit, type, and
browser checks.

No global tools or credentials are required for focused foundation checks.
