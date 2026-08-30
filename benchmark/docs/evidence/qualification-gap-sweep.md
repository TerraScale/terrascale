# Qualification scenario gap sweep

Status: direction and implementation audit refreshed 2026-08-30 against the
current working candidate. No external qualification lane was run to produce
this refresh, and uncommitted work is not release evidence. Every row of the workspace charter's
"Required qualification scenarios" table (`/home/mariogk/Projects/TerraScale/GOAL.md`,
40 rows) is mapped to the receipt files and in-code test evidence that exist in
this repository today, or is marked partial / missing / blocked-external.

How to read the status column:

- **covered** — a receipt or in-code lane exists whose asserted facts are the
  scenario's required result, with its honest limits stated on the receipt.
- **partial** — real evidence covers a named subset; the row names exactly what
  still separates the subset from the charter's required result.
- **missing** — no evidence exists. Nothing here may be claimed.
- **blocked-external** — the row requires isolated machines, production
  Regions, native hardware, or a third-party examination this development host
  cannot supply. Never claimed; the blocker is named.
- **removed/non-goal** — an accepted decision removed the scenario from the
  product contract. It is not missing implementation.

Every "covered" or "partial" row in this repository is a **deterministic
in-process or loopback lane fact** unless the row says otherwise. No row below
constitutes a production, timing, or WAN claim; the receipts themselves carry
those limits.

Evidence inventory swept (all under `docs/evidence/`): `benchmarks/` (4),
`recovery/` (3), `replication/` (10, including `adr0023-golden-vectors.md`,
which landed before this refresh and is cited by rows 1 and 2), `studio/` (1),
and the root receipts (`p7-leaderless-qualification-lane.md`, `p7-tier-failure-lane.md`,
`p7-ssd-budget-usage-record.md`). In-code evidence is
cited by test path where no receipt file exists. Platform-owned scenarios stay
in this inventory only to preserve the original 40-row numbering.

## The sweep

| # | Scenario | Status | Evidence path(s) | What remains |
| --- | --- | --- | --- | --- |
| 1 | native Linux x86_64 and ARM64 qualification | blocked-external | Requirement: `docs/adr/0026-decision-grade-performance-gates.md`, `docs/adr/0033-production-slos-oci-placement-and-operability.md`. Byte-compatibility substrate: `docs/evidence/replication/adr0023-golden-vectors.md` — the six pinned replication/snapshot/anti-entropy encodings are architecture-independent **by construction** (fixed-width integers, 16-byte identities, `BTree` ordering, BLAKE3; no `usize` width or platform float formatting), but the lane executed on x86_64 only. CI (`.forgejo/workflows/ci.yml`) runs `ubuntu-latest` x86_64; no `aarch64` runner exists. | Native x86_64 **and** native Ampere ARM64 machines build and pass the complete correctness suite, and the golden vectors are re-run on both targets. The vectors are ready to travel; the machines are not supplyable on this host. |
| 2 | mixed x86_64 and ARM64 replica set | blocked-external | Same substrate: `docs/evidence/replication/adr0023-golden-vectors.md` states the pinned encodings hold across mixed replica sets by construction, and records "no aarch64 (or mixed-architecture process-pair) run has been recorded" as an honest limit. No cross-architecture topology is constructible in any current lane. | A live replica set with members of both architectures exchanging deltas/snapshots/repair/upgrades/backups and choosing identical CRDT outcomes. Requires both architectures online (row 1 first). |
| 3 | Tier-2-only Ampere Member | blocked-external | In-repo substrate only: `src/storage/lsm/tier.rs` (mover, fallback reads), `src/tiers.rs` (`unconfigured_and_paused_runs_refuse_typed`), `docs/evidence/p7-tier-failure-lane.md`. | WAL/recovery/compaction/replication/rebuild/capacity and **hot-path SLO gates on network block storage alone, on Ampere hardware, with no absent-tier warning or retry loop**. SLO gates need isolated benchmark machines (SE-15 blocker). |
| 4 | optional Tier 1 and Tier 3 combinations | partial | `src/storage/lsm/tier.rs` (13 tier rows: mover demote/serve, dry run, exhaustion typed, checksum-source fallback), `src/tiers.rs` (policy validation, unconfigured/paused refusal), `docs/evidence/p7-tier-failure-lane.md`, and `docs/evidence/tier-combination-matrix.md` (four-profile logical-state and disabled-tier matrix). | The matrix now proves identical state and structural skipping for the four modeled profiles. A distinct configurable Tier-1 device/cache tier and production Tier-2 behavior remain open; the lane explicitly uses a composed seat and local storage. |
| 5 | Tier 1 or Tier 3 loss | partial | `docs/evidence/p7-tier-failure-lane.md` — unavailable store (typed stage receipt), corrupt object quarantined by address, torn upload never referenced; plus `exhaustion_is_typed_after_every_local_source_fails`, `missing_home_object_is_typed_after_bounded_exhaustion` in `src/storage/lsm/tier.rs`. | Live S3 failure classes (404 vs slow vs throttled) — recorded unrun, no object-store endpoint on this host; the §5 fallback QUIC/backup-cluster legs; **intentional disable is healthy** (no lane disables a tier mid-flight and asserts the correctness contract is unchanged). |
| 6 | one, two, and three active Regions | partial | `crates/terrabase-cluster/src/durability.rs` (`global_requires_all_regions_for_one_two_and_three_region_shapes`, `regional_requires_both_members_in_one_region`), `crates/terrabase-cluster/tests/qualification.rs` (2 regions, 5 active + 3 backup members), `src/replication/tests.rs` (one/two/three-region topologies), `src/regions.rs` and `docs/evidence/region-catalog-receipt.md`, `docs/evidence/replication/w4-lane-three-region-composite.md`. | Engine RegionCode/catalog semantics now exist, but the topology lanes still use synthetic identifiers (`eu-1`/`us-1`, `r-alpha`/`r-beta`/`r-gamma`). Production GRU/SFO/FRA admission, placement, routing, and cross-repository qualification remain open. |
| 7 | later twenty-Region capacity shape | missing | `src/regions.rs` and `docs/evidence/region-catalog-receipt.md` provide the typed `REGION_CEILING = 20` admission bound and unit coverage. | No twenty-Region capacity, fault, routing, or operational gate exists. Correctly sequenced after three-Region production readiness per the charter; do not dispatch before then. |
| 8 | write through every Member in every selected Region | partial | `crates/terrabase-cluster/tests/qualification.rs` (`ac2_a_failed_acceptance_completes_by_idempotent_retry_elsewhere`), `src/replication/tests.rs:221`, and the typed ULID layer under `src/identity/`. | A lane that writes through every Member in every selected Region and checks local RF=2 durability plus one stable operation ULID at each boundary. `ts-orchestrator` owns the production topology run. |
| 9 | coordinator fails before, during, or after local commits | partial | `docs/evidence/p7-leaderless-qualification-lane.md` (AC-2 idempotent retry, no leader/lease/redirect by construction), `docs/evidence/replication/coordinator-crash-matrix.md` (nine in-process crash boundaries and typed outcome lattice), `crates/terrabase-cluster/src/durability.rs`, and `crates/terrabase-replication-core/tests/composed_member.rs`. | The boundary matrix now runs at the in-process cluster seam. A real serving-host/transport crash-injection lane, ULID-based client identity, and production-scope retry evidence remain open. |
| 10 | one local Member fails after acknowledgement | partial | `docs/evidence/replication/member-loss-after-ack-lane.md` (engine and cluster seams), `docs/evidence/replication/w4-lane-member-restart-rejoin.md`, `crates/terrabase-cluster/src/durability.rs`, and `src/replication/tests.rs`. | The exact RF=2 continuation window is now proven in deterministic in-process lanes: the survivor retains the write, degraded local writes refuse typed, another Region accepts, and replacement restores RF=2. Production host routing, real process loss, and Orchestrator replacement evidence remain open. |
| 11 | concurrent conflict-free writes in all selected Regions | partial | `docs/evidence/replication/w4-lane-region-partition-isolated-writes.md` (both sides writable under divergent views, D-3 total-order convergence, content-free conflict history), `docs/evidence/p7-leaderless-qualification-lane.md` (AC-4), `src/replication/tests.rs:1690`. | Lane-scoped: two sides of one partition healing, or a synthetic 2-region cluster. "Each Region stays writable" across the full selected catalog with a per-region writability assertion has no dedicated lane. |
| 12 | duplicated, reordered, and delayed frames | covered (lane) | `docs/evidence/replication/w4-lane-duplicate-replay.md` (idempotent apply by operation identity), `docs/evidence/replication/w4-lane-three-region-composite.md` and `se06-lane-three-region-composite-{in-process,engine-socket}.md` (full 11-class fault vocabulary incl. delay/reorder/drop/duplicate on real QUIC frames), `docs/evidence/replication/se06-quic-lane-determinism.md` (causal pump, 5-run streaks idle and under saturated CPU). | Loopback and in-process only. WAN-scale duplication/reordering with production timing belongs to row 29 (blocked-external). The receipts themselves forbid citing their numbers outside the lane. |
| 13 | complete Region loss after `RegionalDurable` | covered (lane) | `docs/evidence/replication/w4-lane-three-region-composite.md` — `op-3` acked at `RegionQuorum` inside `r-alpha`, region lost, RPO = 1 with exact storage-sequence window `3..=3` and the honest RPO/RTO definitions; same script over sockets in `se06-lane-three-region-composite-engine-socket.md`; `src/replication/tests.rs:1432` (`region_loss_after_acknowledged_write_states_durable_visibility_and_recovery_limit`). | Lane clock, scripted detection window. A production-shape run (real region loss, real detection) is row 29's territory. |
| 14 | complete Region loss after `GlobalDurable` | partial | `docs/evidence/replication/global-durable-loss-lane.md`, `src/replication/tests.rs:1432`, `crates/terrabase-cluster/src/durability.rs`, and `crates/terrabase-cluster/tests/global_durable_loss.rs`. The implementation now refuses `GlobalDurable` with fewer than two active Regions. | The local positive and negative contract is implemented. `ts-orchestrator` still must run complete Region loss and WAN partitions on the exact production candidate. |
| 15 | replica-set epoch change | partial | `docs/evidence/model-checking-lane.md` (bounded exhaustive membership model over real cluster types), `crates/terrabase-cluster/src/durability.rs` (`Membership::overlaps`, stale/duplicate refusal), `crates/terrabase-cluster/tests/qualification.rs` AC-3, and `tests/sharding_migration/migration_public_surface.rs`. | Bounded model checking now covers quorum intersection, identity/counter reuse, and stale-epoch refusals. Unbounded/formal coverage, production membership transitions, and the remaining fault/upgrade matrix remain open. |
| 16 | NATS deletion and rebuild | partial | `crates/terrabase-cluster/src/control.rs` (`a_lost_plane_fails_closed_but_admitted_members_keep_serving` — pause answers typed against held metadata; `the_data_plane_has_no_dependence_on_the_control_plane` — admitted members keep serving/converging/reading with the plane gone), `docs/evidence/replication/se06-nats-usage-audit.md` (no database data rides NATS; account separation). | The row is *deletion and rebuild*: the control-plane state is destroyed and service + convergence continue **from durable configuration** and the plane is rebuilt. Outage-tolerance is proven; deletion-rebuild is not. Live `nats-server` integration is recorded unrun in the audit (fixture semantics only). |
| 17 | old client checkpoint | partial | `crates/terrabase-sync-core/src/native/mod.rs:1180` covers expiry, reset, drop, and lost engine generation in a local Rust lane. Retired client evidence was deleted. | Exercise a checkpoint below the retained floor through `terrabase-go` and the live private gRPC host, then prove snapshot bootstrap without history replay. |
| 18 | subscription expansion | partial | `docs/evidence/sync/view-expansion-lane.md` and `crates/terrabase-sync-core/tests/view_expansion.rs` (bounded diff, unbounded snapshot, pre-frontier visibility, removal ordering, deterministic property scenarios). | The transport-independent state machine now covers expansion semantics. A live host/TBEP/SSE/SDK serving lane, real causal scan frontier, and production authorization integration remain open. |
| 19 | permission revocation | partial | `docs/evidence/model-checking-lane.md` (bounded authorization-generation model), `crates/terrabase-sync-core/src/native/mod.rs:1180` (`transition_authorization(2, ["secret"], …)` removes the item from the view), and `src/authorization/tests.rs` (fail-closed policy publication, scope/audience/action/expiry/generation, claim predicates and field projection). | The model now covers bounded generation transitions and the sync state machine covers removal semantics. The end-to-end serving reconnect with a revoked principal and stale checkpoint remains open. |
| 20 | slow client | partial | `crates/terrabase-sync-core/src/native/mod.rs` and the bounded gRPC outbound queue in `crates/terrabase-tbep/src/grpc.rs`. | Prove typed resumable overload closure through the private gRPC host with `terrabase-go`, bounded memory, and an unaffected fast client. |
| 21 | noisy tenant | partial | `docs/evidence/replication/w4-lane-tenant-isolation-probe.md` (cross-tenant serve denied typed and observable, positive control), `src/replication/tests.rs:296`, `src/authorization/tests.rs` (bounded rules and evidence). | The row requires other tenants to stay within **measured latency and throughput isolation budgets** — a benchmark-lane quantity under multi-tenant saturation. The probe is explicitly "a verdict, not a measurement." Isolation-budget measurement is unrun (SE-15 blocker). |
| 22 | hot Partition | partial | `tests/sharding/sharding_public_construction.rs` (`bucket_moves_are_public_explicit_operations`), `tests/sharding_migration/migration_public_surface.rs` (cutover with raised epochs, interrupted-seat idempotent resume, clean rollback), `tests/sharding_coordination/coordination_public_surface.rs` (external failover assessment, refuses healthy-owner replacement). | **Split is not implemented** — no `split` anywhere in `src/sharding/` or `tests/sharding*`. No lane drives a move under hot load or asserts no-duplicate-ownership / no-lost-write / bounded-pause on a hot key range. |
| 23 | Better Auth concurrent uniqueness | missing | None in this repository. Subject lives in `TerraScale.Dashboard` (`tests/backend/auth/`); no test there covers concurrent identity creation under retry/member failure (grep for concurrent-uniqueness shapes returns nothing). | Exactly-one-accepted-identity lane under retries and member failure, in the owning repository. |
| 24 | one-time token replay | missing | None. `TerraScale.Dashboard` has idempotency tests (`tests/backend/backups/backups.test.ts:249`, `tests/backend/database-branches/service.test.ts:135`) but no one-time-token consume-once-with-durable-audit lane. | The consume-once lane with durable audit outcome, in the owning repository. |
| 25 | Better Auth adapter dependency audit | partial | `TerraScale.Dashboard/tests/backend/auth/better-auth-architecture.test.ts` (single row: "has one credential, session, and token authority"), `TerraScale.TypeScript/src/better-auth.ts` + `better-auth.test.ts`. | An automated audit proving only public `@terrascale/sdk` operations are reachable — no TBEP, TerraBase, raw HTTP, or in-process storage shortcut. The one architecture row asserts authority uniqueness, not the dependency boundary. |
| 26 | Go client to Rust engine protocol | partial | ADR 0025's protobuf schema, tonic server, bounded registry, `crates/terrabase-tbep/tests/grpc_service.rs`, `tbep-production-harness`, generated Go bindings, and `terrabase-go/integration_test.go` provide a real cross-process mTLS lane. | Make the live lane mandatory in candidate qualification and extend it across the valid, error, hostile, cancellation, restart, and version-skew matrix. |
| 27 | private protobuf contract generation and hostile decode | partial | ADR 0025's schema compiles at build time through `protox` and `tonic-prost-build`; CI regenerates the sole Go binding in isolation and rejects drift; registry bounds and TBEP tests fail closed. | Pin a schema digest and record the complete truncated, corrupt, oversized, unknown-field, cancellation, and version-skew release matrix. |
| 28 | retired public MessagePack generation and hostile decode | removed/non-goal | ADR 0025 and the workspace owner's 2026-08-30 decision remove public MessagePack, public WebSocket, FlatBuffers, and public TerraBase clients. | Keep these transports absent from code, generated bindings, CI, and current documentation. No compatibility lane is required for greenfield data. |
| 29 | multi-Region replication and reactive sync performance | blocked-external | The repository-owned `bench/` lane and `docs/evidence/benchmarks/` hold local counters, comparison policy, and history. None is WAN or production evidence. | `ts-orchestrator` must run GRU/SFO/FRA concurrency, WAN faults, catch-up, Region loss, reconnect, reset, and authorization changes with exact convergence and valid latency, throughput, bytes, CPU, allocation, and interference receipts. |
| 30 | Dashboard authorization Region failure | missing | None. No region-failover test exists in `TerraScale.Dashboard` (grep over `tests/`, `app/` for region-failure shapes returns nothing; `tests/backend/regions/` covers region listing, not authorization failover). | Database-API failover to another Dashboard region honoring only unexpired signed grants, never inventing or extending authorization. Lives in the Dashboard/Database-API repositories. |
| 31 | system Project cold bootstrap and complete recovery | missing | None. No cold-bootstrap implementation or lane in `TerraScale.Orchestrator` (grep hits are ADR prose only). | Workload identity and replay-safe grant restore with no static root secret and no customer-Project access. Lives in the Orchestrator/repository lane. |
| 32 | greenfield Auth, Payments, queue, and MCP cutover | missing | None, and the precondition is visibly absent: `TerraScale.MCP/` (src, tests, tools) and `TerraScale.Internals/src/{Auth,Payments}` still exist in the workspace. | The one-time cutover itself: fresh system state, fixed-route switch, then deletion of the legacy Auth/Payments/queue/MCP code and authorities — then its qualification receipt. (A separate MCP-cutover-readiness lane is in flight in this workspace.) |
| 33 | licensed Better Auth capability matrix | missing | None. `TerraScale.Dashboard/package.json` pins Better Auth packages (`@better-auth/{agent-auth,api-key,mcp,passkey,stripe}`); no matrix enumerates permitted core/official plugins versus license-blocked items. | The capability matrix with exact evidence for every license-blocked item and no product claim for it. |
| 34 | duplicate and reordered Stripe plugin webhooks | partial | `TerraScale.Dashboard/tests/backend/billing/webhooks-projection.test.ts` ("projects Stripe invoices exactly once and rejects identity reuse", "does not let an older invoice event roll back a newer payment state", dead-letter rows), `webhooks-signatures.test.ts`, `tests/backend/onboarding/better-auth-stripe.test.ts`. | Those rows exercise the Dashboard's own typed billing port (Stripe **and** Polar). The charter row names the **official Better Auth Stripe plugin** producing one authoritative transition, correct entitlement, and reconciled Stripe state — that specific path has no lane. |
| 35 | adding a production Region | partial | `docs/evidence/region-catalog-receipt.md` and `src/regions.rs` (strict `RegionCode`/catalog/parser and 20-Region ceiling), plus ADR 0033's admission requirements. | Engine identity/catalog semantics are implemented and tested. Dashboard, Database API, system-Project replica, routing, identity, recovery, capacity, and OCI admission qualification before public exposure remain open. |
| 36 | usage export outage and retry | partial | `src/usage/tests.rs` — durable fact ledger idempotent and fencing identity reuse, canonical round-trippable checksum-bound segment encoding, corrections append-only referencing an existing base, catalog rejects duplicate meter versions. | The row's failure half: an export outage with **resume from checkpoint**, then duplicates, reordering, and corrections reconstructing one total at the export boundary. The engine ledger is proven; the export/resume pipeline (and its Dashboard ingestion side, `TerraScale.Dashboard/tests/backend/usage/ingestion.test.ts`) has no outage lane. |
| 37 | backup disabled | partial | `crates/terrabase-cluster/src/reads.rs` (`ReadRoutingError::NoBackup` — active refusal held for the no-backup case; `BackupLastRouter` over an optional backup set), `crates/terrabase-tbep/tests/cluster_host_uds.rs:201` (`the_backup_ack_level_names_its_outcome_on_the_receipt_and_never_rolls_back` — the level with no backup set refuses typed **before** anything commits), `docs/evidence/p7-leaderless-qualification-lane.md` (backup members structurally absent from every voter list). | The row's full contract: creation, operation, failure messaging, **PITR limits**, and Region loss of a database with backup explicitly disabled. Read-refusal and ack-refusal are proven; creation-time messaging and the PITR/region-loss behavior of a no-backup database are not. |
| 38 | backup restore | partial | `docs/evidence/recovery/bk02-restore-recovery-receipt.md`, `docs/evidence/p7-leaderless-qualification-lane.md`, and the typed ULID identity layer cover local restore construction, corruption refusal, restart reads, and a new cluster identity. | Add Merkle agreement to restore verification and run snapshot-plus-delta recovery at multi-Region scale through `ts-orchestrator`. |
| 39 | NATS or Orchestrator outage | partial | `crates/terrabase-cluster/src/control.rs` (`a_lost_plane_fails_closed_but_admitted_members_keep_serving` — new joins, topology changes, and config changes pause typed while the plane is lost; held metadata survives recovery; `the_data_plane_has_no_dependence_on_the_control_plane` — admitted members keep serving writes, converging, and reading over their last verified topology). | The row's two halves beyond the fixture: a **live** `nats-server` outage (the audit records live-NATS integration as unrun — fixture semantics only) and a real **Orchestrator** outage (control-plane code here is the in-process `InProcessControlPlane`; the Orchestrator repository's own outage behavior is unevidenced). |
| 40 | deletion with active Regions and backup | partial | `docs/evidence/deletion-workflow-lane.md` and `src/deletion/` (typed globally durable admission, per-copy receipts, 30/90-day deadlines, legal holds, idempotent encoding/resume). | The domain contract exists, but storage/index/cache/view/backup execution, durable ADR 0028 operation wiring, authorization consults, and production restart/fault evidence remain open. |

## Summary counts

Recounted directly from the sweep table (sums to 40):

| Status | Count | Rows |
| --- | --- | --- |
| covered (lane-scoped) | 2 | 12, 13 |
| partial | 26 | 4, 5, 6, 8, 9, 10, 11, 14, 15, 16, 17, 18, 19, 20, 21, 22, 25, 26, 27, 34, 35, 36, 37, 38, 39, 40 |
| missing | 7 | 7, 23, 24, 30, 31, 32, 33 |
| blocked-external | 4 | 1, 2, 3, 29 |
| removed/non-goal | 1 | 28 |

Reading the distribution honestly: the engine's replication, durability, tier,
recovery, sync, region-identity, protocol-schema, model-checking, and deletion
seams carry real in-process and loopback/domain evidence. Platform and
production-infrastructure rows remain missing, partial, or blocked-external.
The retired public protocol row is intentionally absent. No row in this
repository is closed at production scope.

## Standing constraints on every later wave reading this file

1. **Nothing in `docs/evidence/benchmarks/` may be cited as a performance
   number.** The SE-07 receipts are deterministic counters and byte counts; the
   canonical objective status is `PostgreSQL-parity-or-better: inconclusive`
   until SE-15's reference slot runs on an operator-provided PostgreSQL 18
   endpoint.
2. **The deleted-receipt family is real.** The legacy parity scrub `8f697ba`
   removed several receipts cited by surviving documents (SE-07 member-lane and
   TBEP overhead receipts, `docs/benchmarks/*`, `docs/evidence/sync/se11-live-update-tracer.md`).
   The 2026-08-30 production audit also removed the remaining retired-client
   tracer receipts. Their rows survive only in git history. Any wave that
   needs those facts must regenerate them through the accepted Rust engine,
   private gRPC host, and `terrabase-go`, not resurrect them.
3. **Schema and identity decisions still require candidate evidence.** ADR
   0025's private schema, Go generation drift gate, mTLS harness, and ADR 0027
   ULID types are implemented. Row 27 still needs its candidate-bound hostile
   and version-skew receipt. Row 28 is a removed non-goal.
4. **Model-based checking is a charter requirement, not a nice-to-have** for
   membership, coordinated operations, causal compaction, and authorization
   generation transitions (GOAL.md line 2402). A bounded checker now covers
   membership and authorization generations (`docs/evidence/model-checking-lane.md`),
   but coordinated operations and causal compaction still have no model
   checker; rows 15 and 19 remain partial for their stated serving and
   production limits.
5. **The in-process control plane is a fixture.** Rows 16 and 39 close only
   against a live `nats-server` and the real Orchestrator; the NATS usage audit
   records that integration as unrun on this host.

## Honest limits of this sweep

- Documentation audit only. No test, lane, or benchmark was executed to produce
  this refresh; every "covered"/"partial" verdict restates what the cited
  receipt or test already asserts. Where a cited test was not re-run here, its
  status is the last recorded one on its own receipt or its presence at the
  `develop` audit baseline.
- Point-in-time against the current working candidate on 2026-08-30. Dirty
  files and currently running tests are not qualification receipts. Generated
  bindings, hostile-decode behavior, and cross-language integration become
  qualified only when the exact candidate's recorded gates pass.
- Cross-repository rows (23-25, 30-34) were surveyed by reading the owning
  repositories' test trees, not by running their suites.
- This file is dispatch input, not a qualification receipt: it confers no
  status on any row.
