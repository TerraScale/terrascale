# P0 implementation-state record

Status: recorded 2026-08-28 by the P0 truthfulness pass at base
`911197e`. Three read-only audits (replication vs the leaderless
target, storage vs the LSM target, TBEP/C# vs the serving-product
target). Facts only; each names what exists, what is reusable, and what
is missing. This record seeds the P1/P2/P4 execution plans.

## 1. Replication vs the leaderless target (ADR 0018)

**Verified**: the replication data plane is single-leader-per-range with
ownership fencing (ADR 0004 D-1) in two parallel implementations, plus
a server-authoritative (single-writer) Loro sync crate. Nothing
leaderless exists in code; no `RegionalQuorum`/`GlobalQuorum`/
`BackupAck` identifier appears anywhere in `src/` or `crates/`.

- Engine-side: `src/replication/member.rs` (~3,100 lines) is the
  member data plane; ownership validation at admission, epoch fencing
  at apply, deposed-leader conflict history, bounded-HLC LWW, at-least-
  once delivery idempotent through the W3 operation ledger, per-source
  advisory resumption, anti-entropy with servable-history fallback, and
  the three-Region fault lane (`r-alpha/r-beta/r-gamma`) in
  `src/replication/tests.rs`.
- Sync-side: `crates/terrabase-sync-core` fixes one server peer,
  accepts only one server's journal materialization, and maps a flat
  scalar document subset — convergent delivery, not a multi-server CRDT
  database.
- Transport: `crates/terrabase-member-transport` has in-process and
  real QUIC (quinn) lanes with determinism and 23-scenario contract
  tests — a working QUIC starting point for the leaderless fan-out.

**Gaps vs target**: no symmetric replica-set API, no any-Member write
entry, no request-scoped coordination, no quorum acknowledgement
lattice, no coordinator-failure retry path, no read repair, no
membership epochs with overlapping quorums, no delete-wins/shadow
history, no backup-cluster mode.

**Reusable**: transport lanes and doubles, the idempotent replay
ledger integration, anti-entropy exchange shapes, topology/status
types, receipts, and the fault-lane harness.

## 2. Storage vs the LSM target (ADR 0016)

**Verified**: single-store, single-directory, full-copy-per-commit. On
disk: `format`, `wal`, `ledger`, `history` (publication journal),
`retained`, `roots/<32-hex>.root`, `manifest`. Every commit rewrites a
full tenant root — the 154,246,326-byte / 512-write commissioning fact
comes from the deterministic durable-byte counter in
`src/benchmarks/driver.rs`. No LSM, no segments, no tiers.

**The load-bearing seam**: `StoragePort` (crate-private trait,
`src/storage/mod.rs:54`) behind `Arc<Mutex<StorageState>>` — the whole
engine calls it. An LSM engine swaps in by implementing
`commit/resolve_operation/read_document_at/documents_at/tenant_commits/
tenant_sequence/servable_history_start/journaled_high_water/
journal_divergence/published_state/compact/retained_state/flush` with
the Engine untouched.

**Also reusable**: the commit-intent pipeline (`CommitIntent`/
`StagedWrite`/write-set digest in `intent.rs`+`codec.rs`, built by
`Transaction::commit`) maps 1:1 onto a memtable insert path; the MVCC
read contract (`ReadSnapshot{engine, tenant, sequence}` +
sequence-floored reads) is implementation-independent;
`StorageKey`'s tenant>collection>document ordering is already a
canonical LSM key order; the crash-safe pointer-publish pattern
(temp manifest + rename + dir sync) extends to a segment-list manifest;
`FaultPlan`/`FaultPoint` (12 kill points + WAL poisons) extends to
merge/tier/rotation points; `src/storage/tests.rs`,
`compaction_tests.rs`, and the benchmark lane quantify and can prove
the rebuild.

**Gaps vs target**: no mutable/frozen segments, no disk levels, no
sparse indexes, no seekable iterators, no multipart merge reuse, no
tier manifest/mover/cold-read fallback, no backup-cluster storage path,
per-commit amplification unbounded by design.

## 3. TBEP/C# vs the serving-product target (ADR 0013)

**Verified**: one shared schema (`crates/terrabase-tbep/schema/
tbep-registry.json`, registry 1.0) generates both the Rust registry
(9.4k lines) and the C# `TbepRegistry.g.cs` (5.5k lines) with drift
tests on both sides and an 8-corpus golden-vector suite replayed by
both languages. 33 operations, 3 notifications, 16 typed error codes;
fail-closed envelope (FNV-1a digest, idempotency pairing, version
check); byte budgets per frame class. C# `TerraBase.Protocol` ships a
multiplexed `TbepChannel` plus a serialized `TbepClient`.

**No leadership vocabulary exists** in Rust or C# protocol code — the
ADR 0013 D-5 rule currently holds (the only leader-adjacent string is a
parity note on the superseded `replication.fenced` observation op).

**Gaps vs target**: UDS binding only (no QUIC/TLS client binding, no
tokens, no mTLS, no capability/version negotiation — `wire_version`
is a fail-closed constant); the real engine crate is NOT wired behind
TBEP (`SyncDeliveryHost`'s document/query/family methods answer
`operation_refused`; the C# tests synthesize a throwaway cargo server
under `/tmp` from the sync-delivery host); no tier-mover admin surface,
cluster mode, `BackupClusterReadOnly`, or `BackupAck`; no dedicated
overload code, client batching op, or protocol-level reconnect/resume.

**CI gap (closed by this pass)**: `ci.yml` was Rust+python only. P0
adds `dotnet` (build + run all three TUnit suites; `dotnet test`'s
VSTest target is unsupported on .NET 10) and `studio` (pinned pnpm via
corepack, `test:unit` + `vp check`) jobs.

## Receipts (this pass)

- `dotnet run` Release: Protocol 19/19, Api 87/87, Cli 135/135.
- `corepack pnpm run test:unit`: 134 pass / 0 fail; `vp check` green
  after fixing one pre-existing formatting drift in
  `studio/app/queries/indexes/page.tsx`.
- `scripts/validate_parity.py --generate` + `--check`: 258 items,
  schema 1.0, source `fa99b925…`.

## 4. P2 landing 1 (2026-08-29): the two leaderless cores

The leaderless replacement began (ADR 0018 D-8). Nothing was renamed or
routed around: the leader-per-range machinery in `src/replication/` is
untouched and still fails the contract until the replacement consumes
it; two new cores landed beside it.

**Coordination core** — `src/replication/leaderless.rs` (engine-side,
dependency-free): `ReplicaSet` (epoch + eligible voters per Region, no
owner/lease/term), `LeaderlessLevel` (`RegionalQuorum` as the minimum
success, `GlobalQuorum` as regional quorums in a majority of Regions
including the accepting one), `coordinate_write` (any-Member entry,
request-scoped coordination, fan-out to every eligible replica through
each replica's real engine, quorum evaluation, receipts naming every
contributing Member and Region), and `LeaderlessWrite` (stable
operation identity + write set; no range, no acknowledgement field).
Proven over real engines by 8 focused tests: write-through-every-member
equivalence, regional success under cross-Region partition, minority
failure naming the durable partial contributors, global-quorum
arithmetic, coordinator-failure retry through another Member with
exactly one commit per replica (engine-seam idempotency), ineligible
coordinator refusal.

**Multi-writer CRDT core** — new crate `terrabase-replication-core`
(carries the Loro dependency the engine workspace stays free of):
`ReplicaDoc` (one Member's materialized state under its own peer
identity), `ReplicaFrontier` (the per-peer causal summary anti-entropy
exchanges), `delta_for`/`absorb` (member-to-member delta exchange,
ownership-agnostic), `value_after` (shadow-history read). Six
convergence proofs over real Loro documents, per the frozen contract
§8: concurrent same-document writes through different Members merge to
one deterministic scalar winner with identical materialized state and
both writes recoverable from history (scenario 6); the winner is
invariant under delivery order; delete wins over a concurrent update
with the loser in shadow history (scenario 13); duplicated and
reordered delivery converges idempotently (scenario 11); a partitioned
Member catches up after rejoin (scenario 12's data plane); covered
frontiers answer `UpToDate` without exporting.

**Still open in P2** (recorded, not hidden): the composition — one
Member applying each accepted write to both its engine and its
`ReplicaDoc`, with the engine's publication journal feeding CRDT
materialization; durable persistence of a `ReplicaDoc` (snapshot bytes
staged through the object-store tier); the authenticated transport
carrying frontiers and deltas over the member-transport QUIC lanes;
read repair (§6); membership-epoch changes with overlapping quorums
(§7, scenario 5); `BackupAck` (lands with the P5 backup cluster);
qualification receipts for the full AC-10 scenario list; and the
eventual removal of the superseded leader machinery.

Receipts: `cargo test -p terrabase-v2 --lib` 1071/1071;
`cargo test -p terrabase-replication-core` 6/6; pinned
`cargo clippy -p terrabase-v2 -p terrabase-replication-core
--all-targets --all-features --locked -- -D warnings` clean;
`cargo fmt --all -- --check` clean. `scripts/check.sh` not run (AC-12).
