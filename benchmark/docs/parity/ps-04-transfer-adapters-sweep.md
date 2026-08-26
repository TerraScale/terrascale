# PS-04 — Replication, streaming/transfer, and adapters parity sweep + exclusion records

Status: sweep record for ticket TerraScale/TerraBaseV2#56 (PS-04, wave PS of
`specs/2026-08-26-terrabase-v2-gap-closure-tickets.md`). Documentation only.
Row-by-row verification of 99 parity rows against the tree at `develop`
commit `d8569bc` (2026-08-26), with disposition **proposals** handed to
WS-PARITY. No parity overlay, contract, adjudication, inventory, CONTEXT.md,
or code file was changed by this sweep; every overlay named here remains
`unresolved` with an empty Proof ID list. Builders never flip overlays.

Scope (99 rows, exactly the PS-04 assignment):

- Replication family, `parity/contracts/replication-storage.json`:
  PB-5000..PB-5035, PB-5042..PB-5048, and PB-6033 (44 rows). The sharding
  rows PB-5036..PB-5041 stay with wave SH and are out of scope here.
- Streaming/transfer family, `parity/contracts/transfer.json`: PB-6000..
  PB-6009, PB-6024..PB-6032, PB-6035, PB-6036 (21 rows). The backup block
  PB-6010..PB-6023 belongs to wave BK and is not swept here except PB-6001,
  which is inside the assigned range.
- Adapter family, `parity/contracts/adapters.json`: PB-1000..PB-1033
  (34 rows).

Method: each non-excluded row was checked against the implementing tree
(`src/replication/`, `src/streaming/`, `src/adapters/`, plus `src/storage/`,
`src/backup/`, `src/migration/`, `src/engine/` for the storage and backup
rows that live in this contract file), the implementation records
(`docs/replication/w4-replication-r{1,2,3}.md`, `docs/streaming/w4-stream.md`,
`docs/adapters/protocol/w3-protocol.md`), and the lane receipts under
`evidence/replication/`. Line numbers are from the current checkout.
"Implemented" below means implemented and tested at the engine seam, mostly
lane-scoped under ADR-0004 D-4 (three synthetic Regions, no production
claim); it is **not** a parity qualification and adds no Proof ID.

## Decision references applied

- **PD-2026-08-26-10** (gap closure): D2 ETL/queue exclusion; D3 sharding
  full parity with PB-5016..PB-5020 dispositions landing in SH-01/SH-05;
  D4 no legacy wire compatibility (PB-1023/1024, PB-1015..1022, PB-1025
  accepted-difference; migration via BK-03 logical migration); D5 pull and
  external replication (PB-5025/5027) follow SH-01, monitoring/notification
  observability is Platform and host territory, PS-01..PS-04 operationalize
  WS-PARITY.
- **PD-2026-08-26-11** (local backend-only surface): the engine/host boundary
  is local (loopback/UDS; TBEP UDS binding required, QUIC deferred); no
  authentication or authorization surface at TerraBaseV2 — PB-3042 and
  customer auth live exclusively at the TerraScale Platform; backup/export/
  migration are typed, agent-drivable operations through the backend.
- **ADR-0004** (D-1 single leader per data range with epochs and fencing;
  D-2 acknowledgement lattice with `Local` default; D-3 last-writer-wins on a
  bounded hybrid logical clock; D-4 three synthetic Regions, lane-scoped
  only), **ADR-0005** (workspace stack policy: TBEP is the only service
  boundary; C ABI only for SDK-side sync-core embedding), **ADR-0002**
  (engine/Platform/control-plane separation), **PD-2026-08-24-02**
  (adjudicated gap for PB-5012).

## Status vocabulary used by this sweep

Implementation status recorded here (the parity overlay status stays
`unresolved` until WS-PARITY disposes): `implemented-at-seam` (tested engine
behavior exists; lane-scoped where stated), `partial` (some semantics exist,
named gaps remain), `not-implemented`, `in-progress-BK` (backup wave owns),
`adjudicated-gap` (PD-2026-08-24-02), `deferred-SH` (disposition pending
SH-01/SH wave), `accepted-exclusion` (PD-2026-08-26-10 D2),
`accepted-difference` (PD-2026-08-26-10 D4 / PD-2026-08-26-11, or
surface-superseded proposal), `Platform/host` (obligation owned by the
Platform or the C# host, per PD-2026-08-26-10 D5 / PD-2026-08-26-11).

## Exclusion records (charter PD-2026-08-26-10)

### 1. ETL / queue ETL / queue sinks — accepted-exclusion (7 rows)

PD-2026-08-26-10 **D2**: "ETL, queue ETL, and queue sinks (PB-6024..PB-6030)
are accepted exclusions on owner decision — no value for TerraScale. The rows
receive accepted-exclusion dispositions naming this record; no
implementation."

| Row | Name | Disposition proposal |
| --- | --- | --- |
| PB-6024 | etl-task-configuration-and-transformations | accepted-exclusion — PD-2026-08-26-10 D2 |
| PB-6025 | etl-document-attachment-counter-and-time-series-inputs | accepted-exclusion — PD-2026-08-26-10 D2 |
| PB-6026 | etl-destination-variants | accepted-exclusion — PD-2026-08-26-10 D2 |
| PB-6027 | etl-delivery-retry-dead-letter-and-metrics | accepted-exclusion — PD-2026-08-26-10 D2 |
| PB-6028 | queue-etl-providers-and-delivery | accepted-exclusion — PD-2026-08-26-10 D2 |
| PB-6029 | queue-sink-configuration-and-processing | accepted-exclusion — PD-2026-08-26-10 D2 |
| PB-6030 | queue-sink-brokers-and-dead-letter-behavior | accepted-exclusion — PD-2026-08-26-10 D2 |

No ETL or queue-sink code exists in the tree (verified: no `src/etl`, no
queue modules), consistent with the exclusion. Consequence to keep visible:
the "ongoing transfer task" inventory of PB-6032 shrinks to subscriptions
and backup; PB-6033's transfer-consumer set shrinks to subscriptions and
backup checkpoints.

### 2. Legacy wire compatibility — accepted-difference (11 rows)

PD-2026-08-26-10 **D4**: V2 ships exactly one service surface — TBEP, the
C# API host, and the TerraScale SDKs/MCP. PD-2026-08-26-11 D1 confirms the
boundary is local (UDS; QUIC deferred) with no customer-facing surface at
the engine. Migration runs through logical migration (BK-03, #44), never
wire compatibility.

| Row | Name | Disposition proposal |
| --- | --- | --- |
| PB-1015 | legacy-client-command-contract | accepted-difference — PD-2026-08-26-10 D4; typed TBEP operations replace the V1 command wire (SE-14 #10, SE-08 #11) |
| PB-1016 | legacy-client-topology-discovery | accepted-difference — D4; topology is caller-supplied `TopologyView` input at the engine; client topology discovery is host/SDK concern (SE-08 #11) |
| PB-1017 | legacy-client-node-selection-and-read-balance | accepted-difference — D4; read-balance policy belongs to host/SDK |
| PB-1018 | legacy-client-failover-and-request-headers | accepted-difference — D4; retry/failover policy belongs to host/SDK; engine-side idempotency identity exists (OperationId ledger) |
| PB-1019 | tcp-negotiation-and-versioned-features | accepted-difference — D4; TBEP versioning (unknown major versions fail closed) replaces V1 TCP negotiation (SE-14 #10) |
| PB-1020 | tcp-changes-stream | accepted-difference — D4 as wire; the *semantic* obligation transfers to the change backbone and TBEP streams (SE-03 #5, SE-14 #10) |
| PB-1021 | tcp-subscription-stream | accepted-difference — D4 as wire; semantics transfer to TBEP subscription streams (SE-14 #10, SE-08 #11) |
| PB-1022 | tcp-replication-and-cluster-streams | accepted-difference — D4 as wire; member lanes are engine-owned transports under ADR-0008 (SE-06 #8), separated from customer access |
| PB-1023 | postgresql-wire-server | accepted-difference — PD-2026-08-26-10 D4 (named); no PostgreSQL wire server exists in V2 |
| PB-1024 | postgresql-admin-configuration-api | accepted-difference — PD-2026-08-26-10 D4 (named); PostgreSQL integration has no V2 surface |
| PB-1025 | embedded-server-entry-point | accepted-difference — PD-2026-08-26-10 D4 (named) + ADR-0005: the C ABI exists only for SDK-side sync-core embedding (SE-05 #7), never as an embedded *server* compat entry |

### 3. Monitoring / observability rows — disposition to Platform and host (4 rows)

PD-2026-08-26-10 **D5**: "monitoring/notification-center observability is
Platform and host territory." These rows are not excluded from the product;
their obligation moves off the engine public seam. Engine-side progress
*facts* exist and are cited per row.

| Row | Name | Disposition proposal |
| --- | --- | --- |
| PB-6035 | transfer-progress-status-and-operational-observability | Platform/host-owned per PD D5; engine supplies progress facts (`ApplyReport`, `SubscriptionBatch::filtered_dropped`/`dropped_notifications`, `ReplicationReceipt::summary`) |
| PB-1006 | http-server-and-admin-route-family | Platform/host-owned per PD D5 (plus D4: no V1 HTTP surface); server-level operations and monitoring live at host/Platform |
| PB-1028 | public-operations-endpoints | Platform/host-owned per PD D5; operation status/kill is a host/Platform task-control concern (SE-08 #11) |
| PB-1029 | tcp-information-and-management-endpoints | Platform/host-owned per PD D5; topology/TCP diagnostics are host/operator concerns, never a tenant-facing engine surface |

## Family A — replication-storage rows (44)

### A.1 Umbrella and storage/WAL/recovery rows (PB-5000..PB-5012)

Verified against `src/storage/` (codec, wal, ledger, root, file, intent,
faults), `src/engine/`, and the storage test suite. The formal parity
qualification for these rows runs in the exclusive recovery lane with
Recovery Receipts; this sweep records implementation status only.

| Row | Name | Status | Evidence | Disposition proposal |
| --- | --- | --- | --- | --- |
| PB-5000 | replication | partial | `src/replication/mod.rs:1-25` (seam contract); `member.rs:573-673` (catch-up serve/request), `member.rs:2383-2404` (`synchronize` anti-entropy); `evidence/replication/w4-lane-three-region-composite.md` | Asynchronous, at-least-once, idempotent-by-identity replication exists at the engine seam under ADR-0004 D-1..D-3 (a designed difference from V1's Rachis mesh). Production transport (SE-06 #8) and multi-region qualification (SE-07 #9, SE-12 #14) open. Propose `in-progress` once WS-PARITY accepts lane evidence; not `qualified`. |
| PB-5001 | storage-wal-and-recovery | implemented-at-seam | `src/storage/mod.rs:1-20` (W3 publication order: prepare, commit marker, durability, ledger, atomic root publish); `src/storage/wal.rs:191-225` (verified-commit scan, torn-tail handling); tests `recovery_replays_a_verified_commit_that_was_never_published`, `recovery_discards_prepare_frames_without_a_valid_commit_marker` (`src/storage/tests.rs`) | New format by charter; V1 file compatibility excluded (logical migration BK-03 #44). Recovery-lane receipts pending (WS-QUALIFY); no Proof ID here. |
| PB-5002 | storage-environment-lifecycle | partial | `Engine::open`/`shutdown` via `src/engine/mod.rs`; `src/storage/mod.rs:31-33` (`FileStorage`, `StorageOpenError`); test `open_rejects_a_foreign_storage_format`; restart survival via `single_region_single_member_write_reads_back_and_survives_restart` (`src/replication/tests.rs:244`) | Open/reopen/dispose exists; explicit reopen-failure taxonomy narrower than V1's. Gap: focused lifecycle fault tests in the recovery lane. |
| PB-5003 | storage-file-headers-and-versioning | implemented-at-seam | `src/storage/root.rs` (root identity), manifests verified in recovery — tests `manifests_that_do_not_verify_are_ignored_during_recovery`, `open_rejects_a_foreign_storage_format` | V2 format identity and rejection exist; V1 header layout is intentionally not reused (accepted-difference on layout, parity on behavior: invalid identity fails closed). |
| PB-5004 | storage-wal-ordering-and-sync | implemented-at-seam | `src/storage/wal.rs:23-58` (frame types, `WalCommit`), `wal.rs:179-181` (`sync`); `LocalDurability` levels through `src/engine/mod.rs:955`; tests `failed_wal_append_rolls_back_to_the_last_good_offset`, `journal_records_the_achieved_local_durability_of_each_commit` | Committed-only visibility holds (prepare without commit marker is discarded at recovery). Flush/rotation scheduling differs by design; recovery-lane evidence pending. |
| PB-5005 | storage-transactions-mvcc-and-concurrency | partial | Snapshot reads + `ExpectedVersion` in `src/engine/mod.rs`; single-writer publication order in `src/storage/mod.rs:9-19` | V2 uses snapshot-plus-optimistic-concurrency, not V1's Voron MVCC reader/writer paging. Propose accepted-difference on the mechanism with parity on observable isolation; concurrency qualification tests still open. |
| PB-5006 | storage-trees-tables-and-paging | accepted-difference (proposed) | No B-tree/table/paging layer exists; documents persist via the codec with canonical-key windowed reads — `documents_at_from_pages_canonical_key_order_exactly`, `documents_at_from_resumes_strictly_after_the_cursor_identifier` (`src/storage/tests.rs`) | Internal storage model replaced wholesale (charter: new format). Public document ordering/counts semantics preserved at the engine seam; V1 physical-tree parity excluded, logical parity via migration (BK-03 #44). |
| PB-5007 | storage-scratch-buffers | accepted-difference (proposed) | No scratch-buffer subsystem exists in the V2 storage design; bounded memory is enforced per-operation instead | Implementation-detail row; V1 scratch visibility was never a public seam. Propose accepted-difference with bounded-memory obligation kept at the engine seam. |
| PB-5008 | storage-compaction | not-implemented | No compaction module under `src/storage/` | Gap ticket GAP-S1 (below). Servable-history windowing (`Engine::servable_history_start`, `src/engine/mod.rs:807`) is the only history-bound mechanism today. |
| PB-5009 | storage-restart-and-recovery | implemented-at-seam | `src/storage/tests.rs`: `torn_trailing_wal_frame_is_truncated_at_recovery_and_data_survives`, `recovery_rejects_bad_checksum_and_truncated_frame`, `publication_history_survives_reopen_and_serves_the_same_commits`; journal servable-window tests | Restart/replay covered by storage tests; formal Recovery Receipts in the exclusive lane still pending (WS-QUALIFY). |
| PB-5010 | storage-corruption-and-disk-full | partial | `src/storage/faults.rs:8-33` (`FaultPoint` injection: WAL append, commit-marker apply, ledger syncs, pointer publish/sync, journal append); corruption tests above | Torn/corrupt frames fail closed; **no quota or disk-full handling exists**. Gap: quota/disk-full fault classes in the recovery lane. |
| PB-5011 | storage-encryption | not-implemented | No encryption anywhere in `src/storage/` | Gap ticket GAP-S2; key-management policy is a charter-unresolved decision. |
| PB-5012 | storage-schema-upgrade | adjudicated-gap | `parity/adjudications.json` (PD-2026-08-24-02); V2 obligation restated there | Status unchanged by this sweep; obligation open at the storage seam with recovery-lane evidence. |

### A.2 Backup rows carried in this contract (PB-5013..PB-5015)

| Row | Name | Status | Evidence | Disposition proposal |
| --- | --- | --- | --- | --- |
| PB-5013 | storage-full-backup-and-snapshot | in-progress-BK | `src/backup/mod.rs:1-16` (declaration-only seam; `BackupManifest` binds ordered names, lengths, SHA-256 digests) | Implemented by BK-01 (#42) under the accepted WS-BACKUP contract; declarations exist, no behavior yet. |
| PB-5014 | storage-incremental-backup-chain | in-progress-BK | same declaration seam | BK-01 (#42)/BK-02 (#43). |
| PB-5015 | storage-recovery-point-manifest | in-progress-BK | same declaration seam; `src/backup/mod.rs:22-28` (manifest identity, traversal/in-progress rejection) | BK-02 (#43). |

### A.3 Cluster consensus / membership rows (PB-5016..PB-5020) — dispositions pending SH-01/SH-05

PD-2026-08-26-10 D3: "dispositions for PB-5016..PB-5020 land in SH-01/SH-05."
SH-01 (#47) records them. The tree has **no Rachis-style consensus**: V2
uses ADR-0004 D-1 (one owner Member per data range, ownership epochs,
admission and apply-time fencing). Partial V2 analogs exist and are cited so
SH-01 sees what it disposes against.

| Row | Name | Status | Evidence | Disposition proposal |
| --- | --- | --- | --- | --- |
| PB-5016 | cluster-consensus-commit | deferred-SH | Ownership epochs: `src/replication/topology.rs:116-135` (`with_owner_at_epoch`), `src/replication/mod.rs:166-172` (`RangeOwnership.epoch`); quorum lattice `member.rs:2139-2174` | No Raft log/quorum commit; D-1 leader-per-range replaces it. Pending SH-01 (#47) / SH-05 (#51). |
| PB-5017 | cluster-consensus-elections | deferred-SH | No elections; leader transitions are control-plane view changes: `member.rs:1139-1199` (`apply_view`), `StaleView` rejection `member.rs:1947-1955`; tests `leader_loss_stops_new_commits_until_a_new_leader_is_confirmed`, `old_owner_is_fenced_after_leader_transition` | Pending SH-01. |
| PB-5018 | cluster-log-divergence-and-snapshots | deferred-SH | Divergence handled by D-3 conflict convergence + journal catch-up, not log rollback: `member.rs:920-999`; evidence `evidence/replication/w4-lane-region-partition-isolated-writes.md` | Pending SH-01. |
| PB-5019 | cluster-membership-and-topology | deferred-SH | `TopologyView` builder/validity `src/replication/topology.rs:24-245`; stale-view protection `member.rs:1144-1189`; deposition recording `member.rs:1180-1188` | Pending SH-01. |
| PB-5020 | cluster-failover-and-member-loss | deferred-SH | `MemberError::NotOwner`/`FencedBatch` (`member.rs:1941-1965`); test `member_loss_before_acknowledgement_returns_a_typed_error_not_silent_success` (`src/replication/tests.rs:1355`) | Pending SH-01/SH-05. |

### A.4 Replication core rows (PB-5021..PB-5035)

| Row | Name | Status | Evidence | Disposition proposal |
| --- | --- | --- | --- | --- |
| PB-5021 | replication-change-vectors | partial (designed replacement) | V2 identity: `ReplicationClockStamp` `src/replication/mod.rs:92-116`, `(stamp, Member)` total order `member.rs:920-999`; sequences/`OperationId` on every `PublishedCommit` | V1 vector strings/parse/compare are not implemented and are not the V2 encoding. Propose accepted-difference (new identity) + GAP-R5 mapping for migration (BK-03 #44). |
| PB-5022 | replication-conflict-resolution | implemented-at-seam (V2 policy) | `ConflictRecord`/`ConflictOutcome` `src/replication/mod.rs:870-943`; bounded history `member.rs:65` (`MAX_CONFLICTS`); tests `conflicted_writes_produce_the_recorded_policy_outcome_and_a_content_free_conflict_record`, `isolated_concurrent_writes_converge_deterministically_after_healing`, `a_peer_stamp_beyond_the_skew_bound_converges_as_a_conflict` | D-3 LWW-on-HLC with content-free records is the accepted policy (ADR-0004). V1 scripted resolution and revision-preservation are **not** implemented: propose accepted-difference (script mechanism blocked on D-5) with the PS-03 revisions linkage noted. |
| PB-5023 | replication-tombstones | partial | Deletes replay with recorded key/version binding: `member.rs:1036-1057` (`ApplyFailure::KeyMismatch`, no-op delete at version 0); gap fallback `member.rs:739-757` | Deletion evidence is preserved in the journal and re-sent, but there is **no retention watermark** that holds tombstones for offline members, backup chains, or migrations beyond `servable_history_start`. Gap ticket GAP-R2. |
| PB-5024 | replication-anti-entropy-and-catch-up | implemented-at-seam | `synchronize` `member.rs:2383-2404`; `request_catch_up`/`serve_catch_up` `member.rs:573-673`; `ApplyReport.caught_up` `member.rs:1782-1788`; tests `anti_entropy_repairs_a_lagging_member_after_a_gap`, `a_cursor_below_servable_history_falls_back_to_full_catch_up_never_silently_skips` (`src/replication/tests.rs:598`, `:714`) | Lane-proven; production transport pending SE-06 (#8). |
| PB-5025 | replication-external-destinations | deferred-SH (not excluded) | No external-destination configuration/retry/delay machinery in the tree; `TopologyView` is a member mesh only | **Carries SH-01's pending disposition** (PD D5, SH-01 #47). Destination failure ≠ remote durability is already contract fact (`ReplicationOutcome`, mod.rs:349-366). |
| PB-5026 | replication-write-acknowledgement | implemented-at-seam | `AcknowledgementLevel` with `Local` default `src/replication/mod.rs:196-217`; the separate post-commit wait `member.rs:289-396`; timeout carries the partial acknowledgement naming who had durably recorded (`member.rs:356-370`); tests `acknowledgement_report_names_the_regions_and_members_that_contributed` (`tests.rs:1092`), `acknowledgement_timeout_is_distinct_from_local_commit_failure` (`tests.rs:1145`), `write_admission_is_per_range_and_stronger_levels_commit_locally_then_wait` (`tests.rs:962`) | V1's opt-in post-save wait shape preserved exactly (local commit first, wait separate). Lane-scoped only. |
| PB-5027 | replication-pull-hub-and-sink | deferred-SH (not excluded) | No hub/sink roles, access registration, or certificate machinery in the tree | **Carries SH-01's pending disposition** (SH-01 #47). |
| PB-5028 | replication-attachments | partial | Family records persist as documents in the reserved collection and ride the generic put/delete replication (`PublishedWrite`, `member.rs:1029-1058`); change kinds map family records `src/streaming/subscription.rs:669-680` | No attachment-specific replication item, hash/metadata ordering, or failover test exists. Gap ticket GAP-R1 (family-record replication evidence). |
| PB-5029 | replication-counters-and-time-series | partial | Same generic-document mechanism as PB-5028; w4 mapping said "family merge-by-construction under D-3" but no counter/time-series replication test exists in `src/replication/tests.rs` | Gap ticket GAP-R1. |
| PB-5030 | replication-revisions | not-implemented | No revision storage or replication in the tree | V1 revisions family disposition unresolved (PS-03 #55 owns the family rows); this row cannot dispose before that. Gap ticket GAP-R1 includes the linkage. |
| PB-5031 | replication-local-progress | partial (designed replacement) | Journal-derived per-member cursor: `ReplicationCursor::from_journal` `member.rs:1569-1580`; `Engine::journaled_high_water` `src/engine/mod.rs:825`; survives restart (`restarted_member_resumes_from_its_durable_replication_cursor`, `tests.rs:544`) | Local observation only — contract fact held (`src/replication/mod.rs:20-32` compile-fail proofs). V1 change-vector wait API replaced by the acknowledgement/convergence seams. Propose accepted-difference (identity) + GAP-R5 mapping. |
| PB-5032 | replication-health-and-convergence-observations | implemented-at-seam | `ReplicationStatus` seven values `src/replication/mod.rs:372-388` (matches the V1 observation vocabulary: assigned/active/responsive/stale/unavailable/degraded/unknown); derivation `src/replication/status.rs:38-55`; test `member_health_reports_status_only_and_never_convergence` (`tests.rs:808`) | Health never implies convergence (compile-fail proof, `mod.rs:27-32`). Cross-region aggregation open (SE-07 #9). |
| PB-5033 | replication-restart-and-rejoin | implemented-at-seam | `restart`/`rejoin` with cursor-regression rejection `member.rs:1296-1377`, `RejoinRejection` `member.rs:1880-1928`; tests `member_rejoin_checks_identity_and_catches_up_without_duplicates` (`tests.rs:488`), `rejoin_rejects_a_location_whose_format_does_not_verify` (`tests.rs:936`); receipt `evidence/replication/w4-lane-member-restart-rejoin.md` | Lane-proven including format-identity check on reopen. |
| PB-5034 | replication-active-active | partial (lane-level) | `lane_region_partition_with_isolated_writers_converges` (`src/replication/lane_tests.rs`); `isolated_concurrent_writes_converge_deterministically_after_healing` (`tests.rs`); receipt `evidence/replication/w4-lane-region-partition-isolated-writes.md` | Process-local lane only — exactly the V1 evidence class. Deployed-Region qualification pending SE-07 (#9)/SE-12 (#14); no bounded-lag claim exists anywhere in the seam. |
| PB-5035 | replication-commands-and-snapshots | partial | Bounded observations exist as crate accessors: `conflicts()`/`fenced()`/`depositions()`/`denials()`/`cursor()`/`health()` (`member.rs:1279-1428`); bounded logs `member.rs:50-73` | No typed host operation exposes them yet (no GetConflicts/progress-operations surface). Gap ticket GAP-R3; TBEP dependency SE-14 (#10)/SE-08 (#11). |

### A.5 V2 multi-region rows (PB-5042..PB-5048) and PB-6033

All PB-5042..PB-5047 are lane-scoped under ADR-0004 D-4 (three synthetic
Regions `r-alpha/r-beta/r-gamma`, six Members, lane clock only). The
`ReplicationReceipt` (scenario, topology, faults, acknowledgement facts,
convergence, RPO/RTO/convergence/lag with definitions, limits) is
`src/replication/mod.rs:404-816`; lane harness `src/replication/lane.rs`
(Regions/Members at `lane.rs:32-42`, partition at `:308-323`, convergence
observation at `:374-389`, lag at `:394-408`, RPO at `:410-432`).

| Row | Name | Status | Evidence | Disposition proposal |
| --- | --- | --- | --- | --- |
| PB-5042 | v2-multi-region-acknowledgement-level | implemented-at-seam (lane) | Lattice + degenerate normalization `member.rs:2108-2124`; receipt facts; tests `two_regions_healthy_links_achieve_cross_region_acknowledgement_and_read_visibility` (`tests.rs:1301`), `one_region_multiple_members_reach_region_quorum_under_member_delay` (`tests.rs:1222`) | Lane-proven; production topology limits unresolved (D-4); SE-07 #9. |
| PB-5043 | v2-multi-region-convergence | implemented-at-seam (lane) | `Convergence` `src/replication/mod.rs:295-344`; `observe_convergence` `member.rs:2347-2366`; test `convergence_report_names_members_operation_identity_and_observation_time` (`tests.rs:1546`) | Never inferred from acknowledgement or health. SE-12 #14 for qualification close-out. |
| PB-5044 | v2-failure-domain-and-region-loss | implemented-at-seam (lane) | `observe_region_loss` + `RegionLossObservation`/`UnknownUntilRejoin` `member.rs:519-557`, `:2279-2333`; failure domains may not span Regions `topology.rs:74-84`; test `region_loss_after_acknowledged_write_states_durable_visibility_and_recovery_limit` (`tests.rs:1413`) | States durable visibility and recovery limit without guessing lost state. |
| PB-5045 | v2-multi-region-partition-and-conflict | implemented-at-seam (lane) | `FaultClass::RegionPartition` `src/replication/mod.rs:834`; isolated-writers lane test + receipt (A.4 PB-5034) | Conflict outcome recorded content-free; no LWW claim beyond D-3's accepted policy. |
| PB-5046 | v2-replay-idempotency-and-external-effects | implemented-at-seam (engine side) | Operation-ledger replay by identity `member.rs:828-880`, `apply_one` `member.rs:1011-1119`; typed `OperationIdReuse` rejection (`member.rs:2019-2028`); test `duplicate_delivery_applies_each_operation_once` (`tests.rs:444`); receipt `evidence/replication/w4-lane-duplicate-replay.md` | Engine-side duplicate identity proven. External-effect/metering boundary is Platform/backend (PD-2026-08-26-11) — recorded as the row's limit, not an exactly-once claim. |
| PB-5047 | v2-member-rejoin-and-recovery-receipt | partial | Rejoin validation (A.4 PB-5033) plus `ReplicationReceipt` artifacts; formal Recovery Receipt for member rejoin in the exclusive recovery lane still pending | Propose `in-progress`; recovery-lane evidence is the missing half. |
| PB-5048 | v2-multi-region-sharding-orchestration | deferred-SH | No sharding exists in the tree (no `src/sharding`); multi-region receipt vocabulary exists (above) | Routed to SH-02..SH-05 (#48..#51) with qualification extension SE-12 (#14). Not dispositive here. |
| PB-6033 | transfer-replication-and-failure-domain-interactions | partial | Subscription checkpoints are engine-lifetime, not restart-durable (`docs/streaming/w4-stream.md` limits; `src/streaming/subscription.rs:224-228` registry is process-wide); exactly-one-consumer rule `subscription.rs:425-477`; w4-stream notes the lane dependency at `docs/streaming/w4-stream.md:269` | ETL/queue consumers excluded (D2), so this row reduces to subscription and backup checkpoints. Gap ticket GAP-R4 (restart-durable checkpoints, cross-member fencing); backup side lands with BK (#42..#45). |

## Family B — transfer rows (21)

### B.1 Streaming and subscriptions (PB-6000..PB-6009)

Engine seam: `src/streaming/` (`StreamStore` `mod.rs:78-280`; sources
`sources.rs`; subscriptions `subscription.rs`; tests `tests.rs`).
Per-row coverage notes: `docs/streaming/w4-stream.md:243-251`.

| Row | Name | Status | Evidence | Disposition proposal |
| --- | --- | --- | --- | --- |
| PB-6000 | streaming | implemented-at-seam | Batch contract `mod.rs:114-149` (bounded limit, resume cursor, terminal completion exactly once); bounds `mod.rs:48-59`; tests `stream_batches_honor_the_requested_bound_under_a_slow_consumer`, `stream_backpressure_never_blocks_an_engine_commit`, `stream_cancellation_closes_promptly_without_leaking_a_lock_or_buffer`, `stream_memory_stays_bounded_when_the_consumer_never_drains` | Bounded, ordered, cancellable, commit-never-blocked. |
| PB-6001 | backup-and-import | in-progress-BK | `src/backup/mod.rs` + `src/migration/mod.rs` declaration-only seams (manifest-verified, clean-target, loss-matrix contracts stated) | BK-01..BK-04 (#42..#45); agent-drivable per PD-2026-08-26-11 D2. |
| PB-6002 | changes-api | partial | `ChangeEvent`/`ChangeKind`/`ChangeFilter` `mod.rs:741-881` (V1 event-shape set: document/index/counter/time-series/operation/database); committed-only, sequence-ordered, at-least-once; tests `changes_events_carry_operation_identity_and_storage_sequence_in_order`, `changes_never_expose_an_uncommitted_or_rejected_transaction`, `changes_subscription_observes_only_committed_publication_events` | V1 wire shape, connection state machine, reconnect, throttling not implemented — owned by SE-03 (#5 backbone + selector parity) and SE-08 (#11)/SE-14 (#10) transport. Note: `ChangeKind::Index` and `Operation`/`Database` variants exist in the closed set but no producer emits them yet (index events depend on the IX wave). |
| PB-6003 | subscription-wire-and-batches | partial | `SubscriptionSpec`/`SubscriptionBatch`/`SubscriptionStatus` `mod.rs:883-1066`; one-batch-then-ack negotiation; tests `subscription_batches_are_bounded_and_acknowledged_at_the_checkpoint_seam`, `subscription_close_reports_an_explicit_terminal_status` | No wire framing/heartbeat/compression/buffer negotiation — SE-14 (#10)/SE-08 (#11). |
| PB-6004 | subscription-checkpoints-acknowledgements-and-resend | implemented-at-seam (named limit) | Boundary recorded before the wait; checkpoint advances only at `acknowledge` `subscription.rs:683-707`; stale ack refused (`StreamError::InvalidCursor`); redelivery verbatim `subscription.rs:527-531`; tests `subscription_checkpoint_does_not_advance_before_acknowledgement`, `unacknowledged_subscription_items_are_resent_after_reconnect`, `duplicate_subscription_delivery_is_identifiable_and_at_least_once` | Named limit: checkpoint durability is engine-lifetime, not restart-durable (GAP-R4). |
| PB-6005 | subscription-reconnect-failover-and-backpressure | partial | In-process disconnect/reconnect preserves checkpoint and resend state `subscription.rs:425-477`, `:750-759`; bounded non-blocking ingress `subscription.rs:66-100`, `:367-395`; test `publication_sink_never_blocks_a_commit_behind_a_wedged_poll` | Cross-member failover, worker takeover, retry timing not implemented (lane/replication work; SE-08 #11 reattach). |
| PB-6006 | subscription-criteria-scripts-and-revisions | partial (D-5) | Typed filters only: `ChangeFilter` `mod.rs:832-881`; drops reported without advancing the checkpoint (`subscription.rs:604-611`; test `stream_filters_report_drops_without_advancing_the_checkpoint`, `subscription_reaches_events_beyond_a_long_filtered_run`) | No script execution (blocked on decision D-5); projections and revision criteria not implemented. Script portion proposes accepted-difference-or-deferred pending the D-5 mechanism decision. |
| PB-6007 | document-streaming | implemented-at-seam (named limit) | `DocumentStreamSelector` id/prefix/match/exclusion `mod.rs:301-451`; canonical key order via windowed reads `sources.rs:57`, `:322`, `:449`; tests `document_stream_selectors_support_id_prefix_match_and_exclusion`, `document_stream_preserves_key_order_and_result_shape`, `document_stream_missing_documents_and_invalid_selectors_return_typed_errors` | Limit: `match` is substring containment, not V1 regex (recorded in w4-stream). |
| PB-6008 | query-streaming | partial | Plan-order streaming with per-batch index-generation reporting (`StreamBatch::index_generation`, honestly `None` on the index-less path) `mod.rs:613-651`; tests `query_stream_reports_the_index_generation_it_used` | Result-format parity, statistics, and sharded merging not implemented (IX wave, SH-04 #50). |
| PB-6009 | time-series-streaming | implemented-at-seam (family-seam limits) | `TimeSeriesStreamRange` `mod.rs:453-531`; timestamp/tag/value order through the family readers; test `time_series_stream_preserves_timestamp_tag_and_value_order` | Rollups, offsets, incremental series = family seam's current behavior only (PS-03 #55 owns family gaps). |

### B.2 ETL/queue rows — accepted-exclusion

PB-6024..PB-6030: recorded in Exclusion record 1 (PD-2026-08-26-10 D2).

### B.3 Platform-adapter and boundary rows (PB-6031, PB-6032, PB-6035, PB-6036)

| Row | Name | Status | Evidence | Disposition proposal |
| --- | --- | --- | --- | --- |
| PB-6031 | transfer-connection-strings-and-secret-audit | not-implemented (Platform seam) | `src/adapters/platform/mod.rs:15-34` — `PlatformAdapter` is `unimplemented!()` (W5); with ETL excluded, connection strings remain only for backup destinations (BK-04 #45) | Platform-owned per PD-2026-08-26-11; secret audit rules land at the Platform adapter (W5) and host (SE-08 #11). |
| PB-6032 | ongoing-transfer-task-controls | partial | Subscription task controls exist at the engine seam (`subscribe`/`subscription_status`/`disconnect_subscription`/`close_subscription` `mod.rs:177-279`); no backup/ETL task inventory (ETL excluded; backup pending BK) | Task inventory and controls are Platform/host-owned (PD D5; SE-08 #11). Engine side completes with BK-04 (#45) status surface. |
| PB-6035 | transfer-progress-status-and-operational-observability | Platform/host (monitoring row) | Engine progress facts: `ApplyReport` (`member.rs:1745-1788`), `SubscriptionBatch::filtered_dropped`/`dropped_notifications` (`mod.rs:994-1014`), `ReplicationReceipt::summary` (`mod.rs:694-815`) | Disposition to Platform/host per PD-2026-08-26-10 D5 (Exclusion record 3). |
| PB-6036 | transfer-authorization-and-private-route-separation | partial (boundary implemented; Platform pending) | Closed `PublicOperation` registry — no route/body/bytes/handle variant can be expressed (`src/adapters/protocol/mod.rs:1-15`, `:313-355` `PrivateOperation`/`GenericAccessRejected` refusal vocabulary); Platform dispatch `unimplemented!()` | Per PD-2026-08-26-11 there is intentionally no engine auth surface; authorization, confirmation, audit, and secret rules land at the Platform adapter (W5) and host (SE-08 #11). No transfer feature is excluded by this row. |

## Family C — adapter rows (34)

The V1 HTTP/TCP adapter surface does not exist in V2 and will not: PD
D4 + PD-2026-08-26-11 make TBEP (SE-14 #10) behind the C# host (SE-08 #11)
and CLI (SE-13 #12) the only surface. Rows below therefore dispose into:
accepted-difference (wire), accepted-difference proposal (HTTP mechanics
superseded by the typed TBEP registry), Platform/host-owned, or
implemented/confirmed at the in-process typed seam
(`src/adapters/protocol/`).

| Row | Name | Status | Evidence | Disposition proposal |
| --- | --- | --- | --- | --- |
| PB-1000 | approved-protocol-adapter-boundary | implemented-at-seam (in-process) | `ProtocolAdapter::call` `src/adapters/protocol/mod.rs:530-544`; closed registry, typed refusals; red tests `src/adapters/protocol/tests.rs`; record `docs/adapters/protocol/w3-protocol.md` (explicitly no Proof ID) | The service-boundary form of this row is TBEP (SE-14 #10). In-process seam holds the boundary today. |
| PB-1001 | platform-adapter-boundary | not-implemented (W5) | `src/adapters/platform/mod.rs:20-33` (`unimplemented!`); `PlatformError` vocabulary `:36-75` | Platform-owned per PD-2026-08-26-11; W5 wiring + SE-08 (#11) terminate authorization at the host/Platform. |
| PB-1002 | admin-and-control-plane-capabilities | Platform/host | No admin/debug/setup surface anywhere in `src/` | Per PD-2026-08-26-11 and ADR-0002: capabilities remain visible in the inventory but are Platform/host-owned; none copied into the engine. |
| PB-1003 | http-route-catalog-and-generated-coverage | accepted-difference (proposed) | No HTTP routes exist; the V2 catalog is the closed `PublicOperation` set (`src/operations.rs` consumed by `adapters/protocol/mod.rs:361-373`) | Surface superseded by TBEP (PD D4). Obligation transfer: a generated TBEP operation-registry coverage ledger (GAP-A1, SE-14 #10). |
| PB-1004 | http-route-registration-and-dispatch | accepted-difference (proposed) | Dispatch is exhaustive match on the closed enum (`mod.rs:361-391`) — duplicate-registration cannot occur by construction | Superseded by TBEP frame dispatch. |
| PB-1005 | http-database-route-family | accepted-difference (proposed) | Database-scoped operations exist as typed variants only (DocumentRead/Write/Query/Family, `mod.rs:363-372`) | Superseded by TBEP; per-operation behavior parity proceeds through the typed seams (PS-01 #53, PS-02 #54, PS-03 #55 own the operation families). |
| PB-1006 | http-server-and-admin-route-family | Platform/host (monitoring row) | No server/admin routes in the engine | Exclusion record 3 (PD D5). |
| PB-1007 | http-authorization-and-endpoint-markers | partial (transferred) | Operations carry no permission claims; every denial collapses to `ProtocolError::PlatformDenied` (`mod.rs:424-441`) | Authorization metadata is Platform policy (W5; PD-2026-08-26-11). Engine keeps the no-claim rule. |
| PB-1008 | http-cors-preflight | Platform/host | No HTTP surface | Browser/CORS concerns live at the host (SE-08 #11); engine N/A. |
| PB-1009 | http-method-path-and-connect-semantics | accepted-difference (proposed) | No method/path machinery; WebSocket changes transport to TBEP/host streams | Superseded by TBEP. |
| PB-1010 | http-request-body-decompression | accepted-difference (proposed) | No Content-Encoding handling; request bodies are typed operations | Superseded by TBEP frames (codec rules owned by SE-14 #10; bounded-frame discipline). |
| PB-1011 | http-response-compression | accepted-difference (proposed) | No response compression | Same as PB-1010. |
| PB-1012 | http-streaming-cancellation-and-backpressure | partial (transferred) | Cancellation, bounded buffers, and prompt close are implemented at the streaming seam (Family B.1 evidence: `stream_cancellation_…`, `stream_backpressure_…`) | The HTTP-level row is superseded by TBEP supervision/stream rules (SE-14 #10); the behavioral obligation is already met engine-side. |
| PB-1013 | http-status-headers-etags-and-conditional-results | accepted-difference (wire) / transferred (semantics) | Conditional results exist engine-side as `ExpectedVersion` optimistic concurrency (`src/engine/mod.rs`); no status/header surface | Wire shape superseded (host, SE-08 #11); GAP-A2 maps typed errors/conditions to host responses. |
| PB-1014 | http-error-envelope-and-status-mapping | partial (transferred) | Typed error taxonomy exists (`StreamError`, `ReplicationError`, `MemberError`, `ProtocolError`, `EngineError`); no HTTP mapping | Status mapping is a host obligation (GAP-A2; SE-08 #11). |
| PB-1015 | legacy-client-command-contract | accepted-difference | Exclusion record 2 (PD D4) | — |
| PB-1016 | legacy-client-topology-discovery | accepted-difference | Exclusion record 2 | — |
| PB-1017 | legacy-client-node-selection-and-read-balance | accepted-difference | Exclusion record 2 | — |
| PB-1018 | legacy-client-failover-and-request-headers | accepted-difference | Exclusion record 2 | — |
| PB-1019 | tcp-negotiation-and-versioned-features | accepted-difference | Exclusion record 2 (TBEP versioning, SE-14 #10, replaces it) | — |
| PB-1020 | tcp-changes-stream | accepted-difference (wire) | Exclusion record 2; semantics transfer to SE-03 (#5)/SE-14 (#10) | — |
| PB-1021 | tcp-subscription-stream | accepted-difference (wire) | Exclusion record 2; semantics transfer to SE-14 (#10)/SE-08 (#11) | — |
| PB-1022 | tcp-replication-and-cluster-streams | accepted-difference (wire) | Exclusion record 2; member lanes are engine-owned native transports (ADR-0008; SE-06 #8) | — |
| PB-1023 | postgresql-wire-server | accepted-difference | Exclusion record 2 (PD D4 named row) | — |
| PB-1024 | postgresql-admin-configuration-api | accepted-difference | Exclusion record 2 (PD D4 named row) | — |
| PB-1025 | embedded-server-entry-point | accepted-difference | Exclusion record 2 (PD D4 named row + ADR-0005; C ABI is SDK-side embedding only, SE-05 #7) | — |
| PB-1026 | build-version-identity | Platform/host | No build/version endpoint in the engine | Obligation transfers to TBEP versioning/supervision frames (SE-14 #10: unknown major versions fail closed) and host identity (SE-08 #11). Exact-source-identity discipline continues via benchmark receipts (PD-2026-08-26-03). |
| PB-1027 | startup-readiness-and-safe-mode | Platform/host | No readiness/safe-mode surface; engine is a synchronous library behind a host process | Supervision contract is SE-14 (#10)/SE-08 (#11) (engine-process start/health/graceful shutdown). |
| PB-1028 | public-operations-endpoints | Platform/host (monitoring row) | No operations endpoint surface | Exclusion record 3 (PD D5). |
| PB-1029 | tcp-information-and-management-endpoints | Platform/host (monitoring row) | No TCP info/management surface | Exclusion record 3 (PD D5). |
| PB-1030 | private-debug-route-exclusion | boundary-confirmed | No debug routes, info packages, or memory-dump surfaces exist in `src/` (verified by survey) | The exclusion the row demands already holds; keep it as a standing invariant for TBEP (no debug frames in the public set — SE-14 #10). |
| PB-1031 | setup-and-private-control-plane-exclusion | boundary-confirmed | No setup mode, certificate enrollment, or cluster lifecycle in the engine; provisioning is Platform-owned (ADR-0002, workspace charter) | Boundary holds; SH-01 (#47) records topology control-plane ownership. |
| PB-1032 | tls-and-certificate-authentication-hooks | Platform/host | No TLS anywhere in the engine; PD-2026-08-26-11 D1 binds the engine locally (UDS; QUIC+mTLS deferred) | TLS terminates at host/Platform; revisit only if/when a networked-engine need is recorded. |
| PB-1033 | platform-only-client-and-generic-http-exclusion | boundary-confirmed / implemented-at-seam | Closed registry cannot express a route/body/handle (`mod.rs:9-15`, `:313-355`); opaque non-cloneable `CallerContext` with redacted credentials (`mod.rs:100-156`, `:240-296`) | The boundary is implemented in-process and mandated charter-wide; the concrete Platform-only consumer surface arrives with TBEP/host (SE-14 #10, SE-08 #11) and the SDKs (SE-09/SE-10). |

## Summary counts

| Category | Replication (44) | Transfer (21) | Adapters (34) | Total |
| --- | --- | --- | --- | --- |
| implemented-at-seam (incl. named limits, lane-scoped) | 14 | 4 (PB-6000/6004/6007/6009) | 1 (PB-1000) | 19 |
| boundary-confirmed (exclusion holds by construction) | 0 | 0 | 3 (PB-1030/1031/1033) | 3 |
| partial with named gaps | 13 | 7 (PB-6002/6003/6005/6006/6008, PB-6032, PB-6036) | 3 (PB-1007/1012/1014) | 23 |
| not-implemented (engine- or seam-side gap) | 3 (PB-5008/5011/5030) | 1 (PB-6031, Platform seam) | 1 (PB-1001, W5) | 5 |
| in-progress-BK | 3 (PB-5013..5015) | 1 (PB-6001) | 0 | 4 |
| adjudicated-gap (PD-2026-08-24-02) | 1 (PB-5012) | 0 | 0 | 1 |
| deferred-SH (SH-01/SH wave dispositions) | 8 (PB-5016..5020, PB-5025/5027, PB-5048) | 0 | 0 | 8 |
| accepted-exclusion (PD D2, ETL/queue) | 0 | 7 (PB-6024..6030) | 0 | 7 |
| accepted-difference (PD D4 wire-compat, named) | 0 | 0 | 11 (PB-1015..1025) | 11 |
| accepted-difference proposal (surface superseded) | 2 (PB-5006/5007) | 0 | 8 (PB-1003..1011, PB-1013) | 10 |
| Platform/host-owned — monitoring/observability (PD D5) | 0 | 1 (PB-6035) | 3 (PB-1006/1028/1029) | 4 |
| Platform/host-owned — admin/identity/TLS/supervision | 0 | 0 | 4 (PB-1002/1026/1027/1032) | 4 |
| **Total** | **44** | **21** | **34** | **99** |

Notes on the tally: PB-6032 and PB-6036 are counted once each under
"partial" (they are Platform-pending and boundary-pending respectively);
PB-6001 sits under in-progress-BK. Exclusion-class rows total **18 firm**
(7 accepted-exclusion + 11 named accepted-difference) plus **10 proposed**
accepted-differences (surface-superseded) = 28 rows proposed for
difference/exclusion dispositions in total. Deferred-to-SH rows total 8, of
which 7 (PB-5016..PB-5020, PB-5025, PB-5027) await the SH-01 (#47) record
and 1 (PB-5048) awaits the SH implementation wave.

## SE-wave cross-reference (tickets #4..#23)

Issue numbers from `specs/2026-08-26-terrabase-v2-sync-engine-event-system-tickets.md`
(filed 2026-08-26). This sweep read no Forgejo state; numbers are the filed
indexes in that spec and `specs/2026-08-26-terrabase-v2-gap-closure-tickets.md`.

| SE ticket | Issue | Rows it closes or carries |
| --- | --- | --- |
| SE-02 base-engine spike | TerraScale/TerraBaseV2#4 | Native-sync qualification of the replication data plane feeding PB-5000/5024 |
| SE-03 change-event backbone | #5 | PB-6002 (selector parity with V1 Changes API, replay-from-checkpoint), PB-1020 semantics |
| SE-04 terrascale.sync.v2 | #6 | Client-session contract behind PB-1020/1021 semantics |
| SE-05 embeddable SDK builds | #7 | PB-1025's C-ABI-for-SDK-embedding-only rule |
| SE-06 NATS member lanes | #8 | Production transport for PB-5000/5024, PB-1022 semantics |
| SE-07 multi-region efficiency/qualification | #9 | PB-5034, PB-5042..PB-5047 production qualification, PB-5032 aggregation |
| SE-14 TBEP spec/server/C# client | #10 | PB-1000 (service form), PB-1003 catalog obligation, PB-1019 versioning, PB-1026 identity, PB-1027 supervision, PB-1030 no-debug-frames rule, PB-6003/6005 transport |
| SE-08 C# API host | #11 | PB-6031/6032/6035/6036, PB-1001/1002/1006..1014, PB-1028/1029/1032, PB-1033 consumer surface |
| SE-13 C# CLI | #12 | Operator surface for the same rows (changes --follow, status) |
| SE-11 live-update tracer | #13 | End-to-end evidence for PB-6002/6003/6005 semantics |
| SE-12 parity overlays + qualification extension | #14 | The WS-PARITY disposition close-out this sweep feeds; multi-region matrix extension for PB-5042..PB-5048, PB-6033 |
| SE-15 PostgreSQL benchmark baseline | #23 | No speed claims anywhere in this sweep; standing discipline |

Non-SE cross-refs: SH-01 #47 (PB-5016..5020, PB-5025/5027 dispositions),
SH-02..SH-05 #48..#51 (PB-5048, PB-5036..5041 out of sweep scope),
BK-01..BK-04 #42..#45 (PB-5013..5015, PB-6001, PB-6010..6023 out of sweep
scope), PS-01..PS-03 #53..#55 (operation-family rows referenced by
PB-1005's transfer).

## Proposed gap tickets (for WS-PARITY / the parent to file)

1. **GAP-R1 — Family-record replication evidence (PB-5028, PB-5029,
   PB-5030).** Prove counters/time-series/attachment-family records replicate
   and converge through the generic put/delete plane (tests + a lane
   receipt); record the revisions-family disposition jointly with PS-03
   (#55).
2. **GAP-R2 — Tombstone retention watermark (PB-5023).** A retention rule
   that holds deletion evidence while offline members, backup chains
   (BK-01/02), or migrations still need it; today only
   `servable_history_start` bounds history.
3. **GAP-R3 — Typed replication observation operations (PB-5035).** Expose
   conflicts/fenced/depositions/denials/cursor/health as typed TBEP
   operations with redaction rules (depends on SE-14 #10, SE-08 #11).
4. **GAP-R4 — Restart-durable subscription checkpoints and cross-member
   fencing (PB-6004 limit, PB-6005, PB-6033).** Checkpoints survive engine
   restart; single-active-consumer ownership survives member loss.
5. **GAP-R5 — V1 change-vector mapping for logical migration (PB-5021,
   PB-5031).** Define how V1 change vectors map onto
   `(ReplicationClockStamp, Member)`/sequence identity inside BK-03 (#44).
6. **GAP-S1 — Storage compaction (PB-5008).** Engine capability, progress
   reporting, and its interaction with the servable-history window.
7. **GAP-S2 — Encryption at rest (PB-5011; engine half of PB-6013).**
   Requires the charter-unresolved key-management decision first.
8. **GAP-A1 — TBEP operation-registry coverage ledger (PB-1003
   obligation).** Generated inventory of typed TBEP operations with a
   per-operation parity mapping, owned by SE-14 (#10).
9. **GAP-A2 — Host error/status/conditional mapping table (PB-1013,
   PB-1014).** Typed engine errors and `ExpectedVersion` outcomes mapped to
   host response shapes with snapshot tests; owned by SE-08 (#11).
10. **GAP-P1 — Platform Adapter implementation (PB-1001, PB-1007, PB-6031,
    PB-6032, PB-6036).** W5 platform wiring is `unimplemented!()` today;
    under PD-2026-08-26-11 it lands at the Platform/host boundary, not the
    engine.

A storage-gap rider: quota/disk-full fault classes for PB-5010 should join
the recovery-lane backlog alongside the PB-5012 obligation.

## Blockers, limits, and honesty notes

- **No row was flipped.** Every overlay remains `unresolved`/empty; all
  dispositions above are proposals for WS-PARITY (SE-12 #14 close-out),
  per the PS wave rule that builders never flip overlays.
- **Lane-scoped only.** All PB-5042..PB-5047 (and PB-5034) evidence is from
  the synthetic three-Region lane under ADR-0004 D-4: no production
  topology, timing, or durability claim is made or implied.
- **SH-01 (#47) gates** the consensus/membership dispositions
  (PB-5016..5020) and the pull/external rows (PB-5025/5027). This sweep
  records their current tree state but does not pre-decide them.
- **Decision D-5 (script mechanism)** still blocks the script portions of
  PB-6006 and PB-6020's V1 transform behavior.
- **W5/Platform and host surfaces do not exist yet** (`host/` and `cli/`
  are README-only skeletons; `src/adapters/platform` is `unimplemented!()`),
  so Platform/host-owned rows could only be verified as boundary facts.
- **Forgejo was not accessed** (read or write) from this task; ticket
  numbers are the filed indexes recorded in the specs directory.
- The working tree carries another owner's uncommitted change
  (`parity/v1-test-gaps.json`) and an untracked `spike/` directory; both
  were left untouched, as was every file except this one.
- **Concurrent fleet work**: while this sweep ran, SE-03 (#5) change-backbone
  implementation appeared as uncommitted working-tree changes under
  `src/streaming/` (a new `backbone.rs` adding journal-driven
  `replay_changes` and bounded conflict observations, extending
  `StreamStore`). That is another owner's in-flight work, not merged
  evidence: the PB-6002 assessment above reflects the committed seam, and
  the SE-03 cross-reference stands as its owner. Streaming line numbers are
  as-of-read in this moving checkout.
- Storage rows (PB-5001..PB-5012) were surveyed at module/test-name depth
  from `src/storage/`; their formal disposition belongs to the storage-seam
  owner and the exclusive recovery lane, not to this sweep.
