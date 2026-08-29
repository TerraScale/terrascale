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

## 5. P2 landing 2 (2026-08-29): the composed Member

The composition landed: `ComposedMember`
(`crates/terrabase-replication-core/src/compose.rs`, a path dependency
on the engine crate) binds one durable engine to one same-peer
`ReplicaDoc`. Accepted writes land on both planes (`accept_put` /
`accept_delete`: engine commit at `Synced` + own-peer CRDT op);
`anti_entropy_with` merges the CRDTs of any two Members and reconciles
each engine to the merged state; `visible` reads the engine's served
body. The reconcile is idempotent per merged state — its operation
identities derive from the document and a frontier fingerprint, so an
unchanged frontier replays the recorded receipts and commits nothing.

Five end-to-end proofs over real engines: concurrent writes accepted
at two servers during a partition diverge visibly, then after the heal
both engines serve the identical merged body — one deterministic
winner, exactly one loser reconciled (§8 scenario 6 at server level);
delete wins between servers (scenario 13); a repeated heal at an
unchanged frontier commits nothing; a third server joins by exchanging
with one Member only; invalid tenant text is refused typed.

One public engine API was added: `Engine::scope_for(&TenantId)` mints
the tenant scope bound to that Engine — only a caller holding the
Engine can mint a scope that validates against it. Previously scopes
were minted only crate-internally (`TenantScope::from_parts` stays
`pub(crate)`).

**Still open in P2** (updated): durable CRDT persistence (the
`ReplicaDoc` is in-memory; its snapshot staging through the
object-store tier lands with the transport wiring); reconcile's
document enumeration tracks the Member's known set — durable
re-enumeration after a restart needs the public tenant-read surface
(P4); the QUIC transport carrying frontiers and deltas; read repair
(§6); membership epochs (§7, scenario 5); `BackupAck` (P5); the
qualification receipts for the full AC-10 list; removal of the
superseded leader machinery.

Receipts: `terrabase-replication-core` 11/11 (6 convergence + 5
composed); `terrabase-v2 --lib` 1071/1071; clippy `-D warnings` and
fmt clean across both packages. `scripts/check.sh` not run (AC-12).

## 6. P2 landing 3 (2026-08-29): the coordinated product write path

The write path of the frozen contract now exists end to end:
`coordinate_composed_write` (replication-core) accepts a
[`ComposedWrite`] at ANY eligible Member of a [`ReplicaSet`], fans the
same operation to every eligible reachable Member — each accepting on
BOTH planes (durable engine commit at `Synced` plus own-peer CRDT op,
snapshotted to `replica.snapshot` beside the engine) — and evaluates
`RegionalQuorum` / `GlobalQuorum` exactly as the engine-side core
does, returning a receipt naming every contributing Member and Region
at the epoch. The leaderless vocabulary (`ReplicaSet`,
`LeaderlessLevel`, quorum thresholds, `MemberId`/`RegionId`) is now
public at `terrabase_v2::replication`, sourced from one module — the
composed coordinator consumes it rather than restating it. `ComposedMember`
carries a durable identity: the Member name (validated `MemberId`),
with its CRDT peer id derived deterministically from that name.

Proven by the composed tests: a Region-partitioned set where each side
accepts its own client's write at `RegionalQuorum` (two contributors
each, same epoch), the untouched third Region serving nothing, then a
heal after which all six servers serve the identical merged body; a
single Member failing to reach a regional majority with the durable
partial contributors named; write-through-every-Member rotation with a
six-contributor fan-out and full convergence; `GlobalQuorum` requiring
a majority of Regions. One structural fact surfaced and is now pinned
by a test comment: multi-hop anti-entropy converges in ROUNDS — a
Member that exchanges before its peer absorbed a later merge is still
behind and needs the next round — so the heal helper iterates to the
frontier fixed point, which is what continuous background anti-entropy
is.

**Still open in P2** (updated): the authenticated QUIC transport
carrying frontiers and deltas over the member-transport lanes; read
repair (§6); membership epochs with overlapping quorums (§7, scenario
5) — `ReplicaSet` carries the epoch but nothing changes it yet;
`BackupAck` (lands with the P5 backup cluster); bounded CRDT history
compaction (the sync-core shallow-snapshot pattern applies); the
qualification receipts for the full AC-10 scenario list; removal of
the superseded leader machinery.

Receipts: `terrabase-replication-core` 15/15 (6 convergence + 9
composed); `terrabase-v2 --lib` 1071/1071; clippy `-D warnings` and
fmt clean across both packages. `scripts/check.sh` not run (AC-12).

## 7. P2 landing 4 (2026-08-29): bounded CRDT history with the guarded compaction

History compaction landed (`ReplicaDoc::compact_history`, the
sync-core shallow-snapshot pattern on the server plane): the document
compacts at its actual frontiers, deltas serve only from
`retained_floor`, and a peer behind the floor receives
`BehindRetainedFloor` and falls back to snapshot transfer — adoption
for a peer with no operations of its own, proven end to end by a
rejoining server.

Two structural facts surfaced by the tests and now pinned as contracts
of this seam:

- **Compaction is only safe below the frontier every live peer
  covers.** A compacted Member can neither serve a behind peer by
  delta nor absorb that peer's operations whose causal context reaches
  into the shallow region (Loro refuses such imports). The guarded
  form — `compact_history_below(peers)`, per-peer-max of the live
  frontiers — refuses while any live peer is behind, and admits after
  convergence. The unguarded form stays for the operator who knows the
  precondition.
- **Adoption replaces the behind Member's document**, so it is
  typed-refused (`SnapshotAdoptionWouldDropOwnOps`) while that Member
  holds writes of its own; its own engine keeps serving them and the
  correct procedure is to not compact below the live set.

Also fixed en route: compaction at the document's frontiers
(`oplog_frontiers`), not the peer's own last op — after absorbing
peers' operations the latest op is not necessarily this peer's own,
and only a valid frontier cut shallow-exports.

Receipts: `terrabase-replication-core` 19/19 (8 convergence + 11
composed); clippy `-D warnings` and fmt clean. `scripts/check.sh` not
run (AC-12).

## 8. P2 landing 5 (2026-08-29): the quorum read with repair

The leaderless read path (contract §4 + §6) landed:
`quorum_read_with_repair` accepts a read at ANY active Member, merges
the reader causally with the other eligible reachable Members of its
Region (the merged Loro frontier IS the read's view), returns the
merged document body immediately, and repairs the Members observed
behind in the same exchange — their engines reconciled too, the repair
report naming them. Below the Region's majority the read is
typed-refused (`ReadQuorumNotReached`).

Proven: a reader holding a write its regional peer lacks serves the
merged view AND the peer's engine serves the document after the read;
a write in a foreign Region stays invisible to the regional quorum
(the level discipline); a repeated read at the settled frontier
repairs nothing; a lone Member cannot quorum-read.

Receipts: `terrabase-replication-core` 21/21 (8 convergence + 13
composed); clippy `-D warnings` and fmt clean. `scripts/check.sh` not
run (AC-12).

## 9. P2 landing 6 (2026-08-29): membership epochs with overlapping quorums

The last P2-owned item of the frozen contract §7 landed:
`accept_epoch` transitions a replica set to the next configuration
epoch after (a) verifying the per-Region overlap precondition — any
old majority and any new majority must be able to intersect
(`thr_old + thr_new > |union of the Region's voters|`; a full
replacement is refused outright with `NoRegionalOverlap`) — and (b)
durable acceptance by BOTH sides' regional quorums: every reachable
Member records the epoch as replicated data (engine commit + CRDT op,
operation identity `epoch-{n}`, so a partially accepted retry
replays). Receipts name both sides' acceptors and the overlapping
Regions; skipped epochs are refused (`NotAStep`).

Proven (scenario 5): a write acknowledged at regional quorum under
epoch 1 survives the transition to epoch 2 (which replaces one of its
two holders) — the joining Member receives it through the overlap
holder — and the next write coordinates against the new set with the
retired Member absent from the fan-out. Also pinned: the full-replacement
refusal.

With this landing every P2-owned item of the charter is implemented:
durable Writer identities, symmetric replica sets, any-Member entry,
request-scoped coordination, idempotent quorum fan-out,
coordinator-failure recovery, membership epochs, per-document Loro
state, anti-entropy, read repair, snapshot transfer, history
compaction (guarded), delete-wins, shadow history. Still open, owned
by later phases per the frozen contracts: the authenticated QUIC
transport (P4), `BackupAck` (P5's backup cluster), qualification
receipts for the AC-10 list (P7), and the deletion of the superseded
leader-per-range machinery in `src/replication/member.rs` (ADR 0018
D-8) once its lanes' observations are migrated.

Receipts: `terrabase-replication-core` 23/23 (8 convergence + 15
composed); `terrabase-v2 --lib` 1071/1071; clippy `-D warnings` and
fmt clean. `scripts/check.sh` not run (AC-12).

## 10. P3 landing 1 (2026-08-29): the three-mode schema model, admission, and post-merge quarantine

The CRDT-aware schema modes landed (`src/families/modes.rs`, public at
`terrabase_v2::families::modes`): `SchemaMode`
{`schema-free` (default), `validated`, `strict`} exactly per the
frozen contract §1; `CollectionPolicy` binding a mode to its governing
`SchemaDefinition`; `ValueKind` — the per-path CRDT kind, the
stability unit of `schema-free` collections; `admit()` — the
admission point (§2.1): `schema-free` fails closed on re-kinding a
created path, `validated` enforces the SM-02 rule set while accepting
undeclared paths, `strict` also rejects undeclared paths with a typed
error; `merged_violations()` — the post-merge point (§2.2).

Both validation points are wired into the composed Member:
`accept_put` admits against the policy and the stable path kinds the
Member already merged (nothing durable on refusal — `ComposeError::
SchemaAdmission` carrying the typed detail), and `reconcile`
re-validates every merged document, quarantining violators from the
schema-aware read (`read_document` refuses with
`ComposeError::SchemaQuarantine`; the quarantine register with its
auditable violation receipts is exposed for audit). Delete-wins keeps
precedence: an absent document carries no body to violate.

Proven: strict's undeclared-path refusal with nothing durable;
validated's named rule violation; `schema-free` re-kinding refusal
with the admitted body still served; and the policy-skew scenario —
a write valid under the relaxed version merges into a Member already
upgraded, is quarantined there (raw engine bytes intact — a read
gate, not an erasure), and heals when the rolling upgrade aligns the
policies. The classification of coordinated rules (§3) and the
CRDT-aware migration wiring (§7) remain open P3 work; the policy's
own durable replication lands with the P5 configuration plane.

Receipts: `terrabase-v2 --lib` 1076/1076 (5 new modes tests);
`terrabase-replication-core` 25/25; clippy `-D warnings` and fmt
clean. `scripts/check.sh` not run (AC-12).

## 11. P3 landings 2-3 (2026-08-29): coordinated rules, migrations, negotiation

P3 is complete against the charter. Landing 2 — the coordinated path
(contract §3): `RuleClass` {`MergeSafe`, `Coordinated`} is the
reported classification; `CoordinatedConstraint::UniqueField` is the
canonical coordinated rule; `coordinate_composed_transaction` heals
the accepting Member with EVERY reachable Member to the frontier
fixed point, evaluates the constraint against the full merged view,
fans the write at `GlobalQuorum`, and — the contract's discipline —
REFUSES with `QuorumUnavailable` (naming the unreachable Regions)
while any Region lacks a reachable Member, never pretending
coordination-free availability. Proven: first-holder admission with
the class and level reported; the second claimant refused naming the
holder; refusal under a Region partition.

Landing 3 — migrations (contract §7): `migrate_collection` validates
the ENTIRE batch at the target version before any Member commits
(post-merge validation applies post-transform); a quarantinable
transform result stops the migration with the receipt naming the
document and violations, leaving the source version active — the
stopped path changes no Member's policy or state. Successful
migrations commit under deterministic identities
(`mig-{version}-{document}`), so re-runs replay and every Member
produces the same result from the merged state. Schema-version
negotiation: `accept_put_negotiated` fails closed
(`SchemaVersionSkew { local, offered }`) when a writer declares a
version the Member does not govern; `schema-free` has nothing to
negotiate.

Open (honest): the policy's durable replication as collection
metadata and the index-plane integration of quarantine (quarantined
documents are excluded from the served read today; the query-plane
gate lands with P4/P6).

Receipts: `terrabase-replication-core` 28/28 (8 convergence + 20
composed); clippy `-D warnings` and fmt clean. `scripts/check.sh` not
run (AC-12).

## 12. P4 landing 1 (2026-08-29): the engine-backed TBEP host

The load-bearing P4 gap closed: `EngineHost`
(`crates/terrabase-tbep/src/engine_host.rs`, a path dependency on the
engine crate) implements `TbepHost` over one real durable Engine —
the serving spine the C# client exercises. Channel binds validate the
tenant identifier before any engine scope exists; document writes
commit at `Synced` through the engine's transaction path with the
envelope's idempotency key as the operation identity (a retried frame
replays the recorded receipt — no second publication); reads serve
the published body with its version; deletes carry expected-version
outcomes; the query/family/schema/sync/topology surfaces answer the
typed refusal naming their landing (the sync-delivery specialist host
stays beside it).

Proven over a real UDS socket through the typed wire frames
(`engine_host_uds.rs`): write-commit with version and sequence;
idempotent retry replaying the same sequence; `Absent`-expectation
conflict on an existing document; read-found with body and version;
delete with `Exact(1)` committing, the read no longer finding it, and
the retried delete replaying; tenant isolation across channels (a
fresh stream for the second tenant — one bind per channel); the
committed body surviving a full server restart at the same durable
location; and an invalid tenant binding refused before any engine
scope.

Still open in P4: the QUIC/TLS binding, tokens/mTLS and capability
negotiation, the query/family/schema surfaces over this host, the
tier-administration operations, cluster mode, the dedicated overload
code and client batching op, and protocol-level reconnect/resume.

Receipts: `terrabase-tbep` suites all green (3 new engine-host tests
over the 78 existing); clippy `-D warnings` and fmt clean.
`scripts/check.sh` not run (AC-12).

## 13. P4 landing 2 (2026-08-29): the C# client drives the real Engine

The cross-language spine closed: the C# test harness's synthesized
server binary gained an engine mode (`engine:<location>` binds
`EngineHost` over a durable location; without it the sync-delivery
specialist binds as before), and `TbepClient` gained the document
mutation surface (`DocumentWriteAsync`, `DocumentDeleteAsync` — the
same `CallAsync` discipline as the existing client methods, the
idempotency key as the retry identity).

`EngineSpineTests.TheClientDrivesTheRealEngineAcrossARestart` proves
the full spine from C# against the repository's real Engine: commit
with version and sequence, the idempotent retry replaying the same
sequence, the `Absent`-expectation conflict, the read serving the
published body and version — then the process is stopped, a fresh
process opens over the SAME durable location, and the committed body
still serves. All 88 Api rows and 19 Protocol rows pass (one
harness-build fix en route: the throwaway cargo project now declares
edition 2024, and the host selection avoids edition-gated match
let-chains).

Still open in P4 (unchanged from §12): the QUIC/TLS binding,
tokens/mTLS, capability negotiation, the query/family/schema surfaces
over EngineHost, tier administration, cluster mode, the overload code,
client batching, reconnect/resume.

Receipts: `TerraBase.Api.Tests` 88/88 (1 new cross-language spine
row); `TerraBase.Protocol.Tests` 19/19. `scripts/check.sh` not run
(AC-12).

## 14. P4 landing 3 (2026-08-29): query and schema surfaces over the EngineHost

The engine-backed host serves the two read-declarative surfaces the
spine lacked. Query: `query.execute` compiles the V1-compatible query
text through the engine's real query seam
(`QueryStore::compile(QueryInput::v1_compatible(...))` → `execute`)
against the bound tenant's snapshot — the response's `count` is the
matched total, `documents` the page, and the terminal is `Truncated`
exactly when the page cut the matched set. Schema: `schema.declare`
stages the collection's policy through the real family write path
(`ValidationWrite` via `FamilyWriteRequest::schema_validation_update`,
committed at `Synced`, version derived forward-only from the recorded
policy), `schema.status` reads the enforced version and rule count
back, and `schema.validate` evaluates one offered body against the
recorded policy without staging anything — naming the violated field
and the typed kind. Enforcement is the engine's own write path, not a
host-side copy: after a declare, a violating document write is refused
at the staged put and nothing durable records, and clearing (declare
with no schema) returns the collection to exact schema-free behavior.
The evaluator surfaced publicly as `families::violations` (previously
crate-private) — one pure function over the public `SchemaDefinition`,
re-exported beside the payload types.

Proven over the real UDS socket (`engine_host_uds.rs`, 4 new rows over
landing 1's three): three seeded documents scanned complete; the page
below the matched total truncated with the total still named; the
filtered predicate matching its one document; declare/status pairing
versions; conforming write committing and violating write refused with
nothing recorded; validate valid/invalid with field and kind; clear
restoring schema-free behavior.

Still open in P4: the QUIC/TLS binding, tokens/mTLS and capability
negotiation, the family surfaces over this host, tier administration
(registry ops + `tbep-gen` regeneration), cluster mode,
`topology_read`, the dedicated overload code, client batching, and
reconnect/resume.

Receipts: `cargo test -p terrabase-tbep` all suites green (7
engine-host UDS rows); `cargo test -p terrabase-v2 --lib` 1076/1076;
clippy `-D warnings` and fmt clean on both crates.
`scripts/check.sh` not run (AC-12).

## 15. P4 landing 4 (2026-08-29): the frozen typed surface — acknowledgement levels, read levels, cluster mode

The registry took its first minor bump (1.0 → 1.1, ADD-only per the
protocol contract's versioning rule): the six error codes P4 froze by
name (`overloaded` 17, `protocol_version_mismatch` 18,
`backup_cluster_read_only` 19, `backup_cluster_immutable` 20,
`cold_tier_unavailable` 21, `frontier_unreachable` 22), the
`acknowledgement` enum (`regional_quorum` | `global_quorum`;
`backup_ack` lands with P5's machinery), the `read_level` enum (`local`
| `regional_quorum_read` — the name frozen in the leaderless contract
§4 — | `global_quorum_read`), and the `cluster_mode` enum (`standalone`
| `active` | `backup`). Optional ADD-only fields: `ack` on the write
and delete requests, `acknowledgement` on the commit receipt, `level`
+ `position` on the read and query request/response pairs, and
`cluster_mode` on the topology response. `tbep-gen` regenerated both
language bindings; the golden corpus regenerated (the positional-array
arity grew — the adversarial arity rows updated to match) and the
document-frames scenario now exercises a `global_quorum` write and a
`regional_quorum_read` read.

The engine-backed host serves the new surface honestly: one standalone
member IS its region's quorum, so a local durable commit satisfies both
acknowledgement levels (the receipt names which it achieved); every
read level observes this member's state directly and the read/query
responses name the storage sequence their snapshot observed — the
replication position a client measures lag against
(`ReadSnapshot::sequence` surfaced publicly for it). `topology.read`
stopped refusing: it answers the honest standalone mode (local UDS
binding, the subsystems this host serves, no engine topology,
`cluster_mode: standalone`) — the cluster-mode vocabulary exists on the
wire before P5 admits peers.

Proven: 9 engine-host UDS rows (a new one asserting the global-quorum
receipt, the regional default, read positions at or past the write's
sequence, the query position, and the standalone topology answer);
`terrabase-v2` 1076/1076; the C# spine asserts the receipt's
acknowledgement and the read's position against the real Engine
(Api 88/88, Protocol 19/19 over the regenerated corpus).

Still open in P4: the family plane (the `terrabase.family-request/1`
codec is still a registry name only — no host serves family frames),
tier administration (no registry operations exist yet; the engine's
tier/ mover surfaces are crate-private), capability/version negotiation
(`wire_version` stays a fail-closed constant), the dedicated
`Overloaded` control loop, client batching, reconnect/resume, and the
QUIC/TLS binding (quinn/rustls live only in
`terrabase-member-transport` today).

Receipts: `cargo test -p terrabase-tbep` 11 suites green; clippy
`-D warnings` and fmt clean on both crates; dotnet Api 88/88 and
Protocol 19/19. `scripts/check.sh` not run (AC-12).

## 16. P4 landing 5 (2026-08-29): query cancellation and the overload gate

Two of the contract's control surfaces stopped being dead ends.

**Cancellation.** The engine's query seam gained a bounded V1 input:
`QueryInput::v1_compatible_bounded(text, deadline, cancellation)` binds
execution control to the grammar's otherwise-unbound query (the grammar
has no control clauses of its own), so a served query runs with its own
`Cancellation` signal and a 30 s deadline. The engine-backed host
registers each live query's signal under its wire id (a bounded map) and
moves the id to a bounded 256-deep completed horizon when it finishes —
from any channel, `query.cancel` now answers the typed outcomes: a live
query cancels at its next bounded checkpoint (the query's own response
becomes the typed refusal `the query was cancelled`), a finished one
inside the horizon answers `already_complete`, and anything else is
`unknown_query`. `SyncDeliveryHost` still refuses the op (it serves no
queries) — the engine-backed host owns it.

**Overload control.** The server gained the overload gate: one
server-wide count of concurrently executing host operations
(`MAX_INFLIGHT_OPERATIONS = 128` by default; `bind_with_inflight_bound`
sets an explicit capacity). A gated frame arriving at a saturated bound
answers the typed `overloaded` error (the code landing 4 froze) with
machine-readable retry guidance (`retry_after_ms=50`) — never an
unbounded queue, never a dropped frame. Two frames bypass the gate by
design: supervision health (the operator must always observe the
server) and query cancellation (the remedy for a slow query must not be
refused by the pressure it relieves). The per-connection slow-consumer
policy from the first landing stays beside it — the gate bounds server
work, the outbound channel bounds per-consumer drift.

Proven: the overload-gate row drives a host whose one read blocks until
released — at a bound of one, the saturated read answers `overloaded`
with the retry hint, health and cancel still answer, and the gate
serves again after the release; the cancellation row pins
`already_complete` inside the horizon and `unknown_query` beyond it;
every prior suite stays green (12 tbep suites, engine 1076/1076, Api
88/88, Protocol 19/19).

Still open in P4: capability/version negotiation (`wire_version` stays
a fail-closed constant), tier administration (registry + a public
engine tier surface), client batching, reconnect/resume, and the
QUIC/TLS binding (quinn/rustls live in `terrabase-member-transport`).

Receipts: `cargo test -p terrabase-tbep` 12 suites green; clippy
`-D warnings` and fmt clean on both crates. `scripts/check.sh` not run
(AC-12).

## 17. P4 landing 6 (2026-08-29): capability and version negotiation

The connection-open exchange the protocol contract §1 froze now exists
on the wire: two new channel frames (registry codes 2 and 3, beside the
error frame) — `negotiation_request` carrying the client's supported
wire versions and offered capabilities, and `negotiation_response`
carrying the selected version and the accepted capabilities. The
server's rules are the contract's rules verbatim: the peers select the
highest COMMON version; a client naming no version the server speaks
fails closed with the typed `protocol_version_mismatch` error (code 18,
frozen in landing 4) and the connection ends before any operation
frame; unknown capabilities are ignored, never refused — the server
answers the intersection with its own served set
(`streaming-lanes`, `admin-surface`, `query-surface`, `schema-surface`;
compression stays absent until benchmark receipts justify it, per the
contract). The negotiation is connection-open control: it needs no
channel binding, carries no idempotency key, and bypasses the overload
gate beside health and cancellation. It is optional — a client may open
and operate directly (the envelope's version byte still fails closed on
unsupported versions); negotiation is the explicit discovery surface.

`TbepClient` gained `NegotiateAsync` (the call discipline over an
explicit frame kind — the channel frames live outside the operation
name table). The golden corpus gained the `negotiation` category (both
languages replay the same frames byte-exact), and the C# suite's
category guard learned the eighth file.

Proven: three Rust rows (highest-common selection with an unknown
capability ignored and operations still running after; the mismatch
failing closed — the typed error then the stream ends; the bare
negotiation before any bind) and the C# row against the real engine
(selection, ignored unknown, the typed mismatch exception). The corpus
replays byte-exact in both languages.

Still open in P4: tier administration (registry + a public engine tier
surface), client batching, reconnect/resume, and the QUIC/TLS binding.

Receipts: `cargo test -p terrabase-tbep` 13 suites green; clippy
`-D warnings` and fmt clean; Protocol 21/21, Api 89/89.
`scripts/check.sh` not run (AC-12).
