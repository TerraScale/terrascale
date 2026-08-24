# W4 row mapping: transfer and replication-storage parity rows

Status: audit note supporting
[`docs/design/w4-transfer-replication-contract.md`](../design/w4-transfer-replication-contract.md),
recorded 2026-08-24 at base commit `46bccd5`. Documentation only: no parity
status, disposition, Proof ID, or overlay changes were made.

This note maps every W4-facing parity row to the first packet that owns its
V2-side evidence. Sources are `parity/contracts/transfer.json` (37 rows,
PB-6000..PB-6036) and `parity/contracts/replication-storage.json` (49 rows,
PB-5000..PB-5048) at the pinned V1 source identity
`fa99b9257776dbe625e6a2388c74c9ce916637ee`.

Ownership here means "first V2 owner of tests and evidence". Every row remains
`unresolved` with an empty Proof ID list. A row carrying a decision marker
(D-1..D-7 in the W4 contract) cannot receive its first proof until the parent
adjudicates that decision. Decision index:

- D-1 replication algorithm; D-2 consistency/acknowledgement defaults;
  D-3 conflict policy; D-4 production topology limits; D-5 transfer
  transform-script mechanism; D-6 optional/legacy transfer dispositions;
  D-7 V2 sharding scope.

## Transfer rows (PB-6000..PB-6036, 37 rows)

| Row | Name | First owner | Decision | Notes |
| --- | --- | --- | --- | --- |
| PB-6000 | streaming | W4-STREAM | — | Ordering, backpressure, cancellation, bounded memory at the `StreamStore` seam |
| PB-6001 | backup-and-import | W4-BACKUP | — | Umbrella row; manifest-verified operations, progress, cancellation, loss reporting |
| PB-6002 | changes-api | W4-STREAM | — | `ChangeEvent`/`ChangeKind`/`ChangeFilter`; committed-publication ordering, reconnect duplicates |
| PB-6003 | subscription-wire-and-batches | W4-STREAM | — | `SubscriptionSpec`/`SubscriptionBatch` bounded batches and status transitions |
| PB-6004 | subscription-checkpoints-acknowledgements-and-resend | W4-STREAM | — | Checkpoint advances only at the acknowledgement seam; resend state |
| PB-6005 | subscription-reconnect-failover-and-backpressure | W4-STREAM | — | Reconnect preserves the durable checkpoint; cross-member failover scenarios shared with W4-REPLICATION |
| PB-6006 | subscription-criteria-scripts-and-revisions | W4-STREAM | D-5 (script portion) | Typed filters in W4; projection/script criteria blocked on the script-mechanism decision |
| PB-6007 | document-streaming | W4-STREAM | — | Id/prefix/match/exclusion selectors, ordering, typed errors |
| PB-6008 | query-streaming | W4-STREAM | — | Plan ordering, generation reporting, staleness policy |
| PB-6009 | time-series-streaming | W4-STREAM | — | Timestamp/tag/value order, ranges, deleted and empty ranges |
| PB-6010 | full-incremental-and-snapshot-backups | W4-BACKUP | — | `BackupKind` full/incremental; snapshot mode is a later disposition |
| PB-6011 | periodic-backup-scheduling-and-status | W4-BACKUP | — | Engine-side durable status and idempotency; the typed task-control surface is W5 (PB-6032) |
| PB-6012 | backup-destinations-and-upload-modes | W4-BACKUP | — | W4 owns the local destination and the typed destination seam; the cloud/FTP provider matrix is later qualification |
| PB-6013 | backup-encryption-and-compression | W4-BACKUP | — | Archive identity, wrong-key failure, malformed-input rejection; key management unresolved later |
| PB-6014 | backup-retention | W4-BACKUP | — | Chain-preserving deletion; local runner in W4 |
| PB-6015 | recovery-point-manifests-and-integrity | W4-BACKUP | — | `BackupManifest` identity, digests, traversal and in-progress rejection |
| PB-6016 | restore-selection-and-execution | W4-BACKUP | — | `RestorePlan`/`RestoreResult`, clean-target rule, admission before materialization |
| PB-6017 | backup-errors-throttling-and-concurrency | W4-BACKUP | — | Single-job admission, typed errors, throttling without reorder or loss |
| PB-6018 | smuggler-export-and-import | W4-BACKUP | — | Logical package format version, manifest, checksum, duplicate policy |
| PB-6019 | smuggler-data-families-and-selection | W4-BACKUP | — | Item-type selection incl. family records; explicit per-type disposition |
| PB-6020 | smuggler-transform-scripts-and-revision-policy | W4-BACKUP | D-5 | Revision policy in W4; transform scripts blocked on the sandbox decision |
| PB-6021 | smuggler-resume-idempotency-and-loss-reporting | W4-BACKUP | — | Durable positions, visible skips, no silent loss |
| PB-6022 | legacy-offline-migration | W4-BACKUP | — | `src/migration/` logical, manifest-verified, restartable path |
| PB-6023 | relational-migration | later | D-6 | SQL provider matrix is a later wave; not silently dropped |
| PB-6024 | etl-task-configuration-and-transformations | later | D-6 | Consumes streaming + Platform task control; later wave |
| PB-6025 | etl-document-attachment-counter-and-time-series-inputs | later | D-6 | Family input ordering for ETL; later wave |
| PB-6026 | etl-destination-variants | later | D-6 | Provider set and adapter ownership unresolved |
| PB-6027 | etl-delivery-retry-dead-letter-and-metrics | later | D-6 | Retry/dead-letter with durable checkpoints; later wave |
| PB-6028 | queue-etl-providers-and-delivery | later | D-6 | Broker providers; later wave |
| PB-6029 | queue-sink-configuration-and-processing | later | D-6 | Later wave |
| PB-6030 | queue-sink-brokers-and-dead-letter-behavior | later | D-6 | Broker-specific acknowledgment rules; later wave |
| PB-6031 | transfer-connection-strings-and-secret-audit | W5 | — | Platform Adapter typed secret handling |
| PB-6032 | ongoing-transfer-task-controls | W5 | — | Platform Adapter task inventory and controls |
| PB-6033 | transfer-replication-and-failure-domain-interactions | W4-REPLICATION | — | Checkpoint survival, fencing, replay during member loss and migration |
| PB-6034 | sharded-transfer-and-migration-interactions | later | D-7 | Depends on the sharding scope decision |
| PB-6035 | transfer-progress-status-and-operational-observability | W5 | — | Platform Adapter status shapes; engine-side progress facts come from W4 packets |
| PB-6036 | transfer-authorization-and-private-route-separation | W5 | — | Platform Adapter authorization and private-route separation |

## Replication-storage rows (PB-5000..PB-5048, 49 rows)

| Row | Name | First owner | Decision | Notes |
| --- | --- | --- | --- | --- |
| PB-5000 | replication | W4-REPLICATION | D-1 | Umbrella row; algorithm, retry, and lag bound blocked on the algorithm decision |
| PB-5001 | storage-wal-and-recovery | later | — | Storage seam (W3-1 implemented); formal resolution needs the exclusive recovery lane |
| PB-5002 | storage-environment-lifecycle | later | — | Same storage-seam recovery-lane rule |
| PB-5003 | storage-file-headers-and-versioning | later | — | V2 header contract is storage-owned; recovery lane |
| PB-5004 | storage-wal-ordering-and-sync | later | — | Storage seam; recovery lane |
| PB-5005 | storage-transactions-mvcc-and-concurrency | later | — | Storage seam; recovery lane |
| PB-5006 | storage-trees-tables-and-paging | later | — | Storage seam; recovery lane |
| PB-5007 | storage-scratch-buffers | later | — | Storage seam; recovery lane |
| PB-5008 | storage-compaction | later | — | Storage seam; recovery lane |
| PB-5009 | storage-restart-and-recovery | later | — | Storage seam; recovery lane (WS-QUALIFY owns the substitute fixture question) |
| PB-5010 | storage-corruption-and-disk-full | later | — | Storage seam; recovery lane |
| PB-5011 | storage-encryption | later | — | Storage/backup seam; key management unresolved later |
| PB-5012 | storage-schema-upgrade | later | — | PD-2026-08-24-02: V2-side storage tests plus a Recovery Receipt in the exclusive lane |
| PB-5013 | storage-full-backup-and-snapshot | W4-BACKUP | — | Full backup archive and restore verification |
| PB-5014 | storage-incremental-backup-chain | W4-BACKUP | — | Chain completeness, ordering, restart cursor |
| PB-5015 | storage-recovery-point-manifest | W4-BACKUP | — | Manifest binding, tamper and traversal rejection |
| PB-5016 | cluster-consensus-commit | W4-REPLICATION | D-1 | Commit/apply outcomes depend on the algorithm decision |
| PB-5017 | cluster-consensus-elections | W4-REPLICATION | D-1 | Elections exist only under algorithm options A/C |
| PB-5018 | cluster-log-divergence-and-snapshots | W4-REPLICATION | D-1 | Divergence repair and snapshot catch-up |
| PB-5019 | cluster-membership-and-topology | W4-REPLICATION | D-1 | `TopologyView` transitions; stale-topology protection |
| PB-5020 | cluster-failover-and-member-loss | W4-REPLICATION | D-1 | Leader loss, rollback, survivor behaviour |
| PB-5021 | replication-change-vectors | W4-REPLICATION | — | Vector identity and comparison under the adjudicated policy |
| PB-5022 | replication-conflict-resolution | W4-REPLICATION | D-3 | Conflict policy decision gates the resolution contract |
| PB-5023 | replication-tombstones | W4-REPLICATION | — | Retention until every consumer advanced; cleanup watermark |
| PB-5024 | replication-anti-entropy-and-catch-up | W4-REPLICATION | — | `ReplicationCursor`, catch-up boundary, convergence condition |
| PB-5025 | replication-external-destinations | W4-REPLICATION | — | Destination failure ≠ remote durability claim |
| PB-5026 | replication-write-acknowledgement | W4-REPLICATION | D-2 | Opt-in acknowledgement separate from the local result |
| PB-5027 | replication-pull-hub-and-sink | W4-REPLICATION | — | Hub/sink roles, credentials via typed adapters, secret-safe receipts |
| PB-5028 | replication-attachments | W4-REPLICATION | — | Attachment metadata/bytes with document ordering |
| PB-5029 | replication-counters-and-time-series | W4-REPLICATION | — | Family merge-by-construction under D-3 |
| PB-5030 | replication-revisions | W4-REPLICATION | — | Legacy revision disposition stays explicit |
| PB-5031 | replication-local-progress | W4-REPLICATION | — | Local observation only; never cluster convergence |
| PB-5032 | replication-health-and-convergence-observations | W4-REPLICATION | — | Health separate from convergence; bounded, content-free |
| PB-5033 | replication-restart-and-rejoin | W4-REPLICATION | — | Durable cursor resume, identity validation, no duplicates |
| PB-5034 | replication-active-active | W4-REPLICATION | D-3 | Process-local V1 observations; conflict policy gates the V2 claim |
| PB-5035 | replication-commands-and-snapshots | W4-REPLICATION | — | Typed bounded observations; snapshot catch-up is member state |
| PB-5036 | sharding-topology | later | D-7 | Sharding scope decision |
| PB-5037 | sharding-buckets | later | D-7 | Sharding scope decision |
| PB-5038 | sharding-resharding-and-bucket-migration | later | D-7 | Sharding scope decision |
| PB-5039 | sharding-orchestrator-coordination | later | D-7 | Sharding scope decision |
| PB-5040 | sharding-replication-backup-and-rejoin | later | D-7 | Sharding scope decision |
| PB-5041 | sharding-failover-and-cluster-transactions | later | D-7 | Sharding scope decision |
| PB-5042 | v2-multi-region-acknowledgement-level | W4-REPLICATION | D-2 | `AcknowledgementLevel`/`AcknowledgementReport` |
| PB-5043 | v2-multi-region-convergence | W4-REPLICATION | D-2 | `Convergence` observation contract |
| PB-5044 | v2-failure-domain-and-region-loss | W4-REPLICATION | D-4 | Region-loss scenario; topology limits gate the claim |
| PB-5045 | v2-multi-region-partition-and-conflict | W4-REPLICATION | D-3 | Partition scenario; conflict policy gates the outcome |
| PB-5046 | v2-replay-idempotency-and-external-effects | W4-REPLICATION | — | Duplicate identity, replay boundary, no exactly-once claim |
| PB-5047 | v2-member-rejoin-and-recovery-receipt | W4-REPLICATION | — | Rejoin validation plus Recovery/Replication Receipts |
| PB-5048 | v2-multi-region-sharding-orchestration | later | D-7 | Sharding scope decision |

## Count cross-check

| Owner | Transfer | Replication-storage | Total |
| --- | --- | --- | --- |
| W4-STREAM | 9 | 0 | 9 |
| W4-REPLICATION | 1 | 27 | 28 |
| W4-BACKUP | 14 | 3 | 17 |
| W5 Platform Adapter | 4 | 0 | 4 |
| later | 9 | 19 | 28 |
| Total | 37 | 49 | 86 |

Every row appears exactly once. Row ID ranges are contiguous and complete:
PB-6000..PB-6036 and PB-5000..PB-5048, matching the fragment files.

Decision-marker counts (31 of 86 rows): D-1: 6 (PB-5000, PB-5016..PB-5020);
D-2: 3 (PB-5026, PB-5042, PB-5043); D-3: 3 (PB-5022, PB-5034, PB-5045);
D-4: 1 (PB-5044); D-5: 2 (PB-6006, PB-6020 — partial rows; script portions
only); D-6: 8 (PB-6023..PB-6030); D-7: 8 (PB-5036..PB-5041, PB-5048,
PB-6034). Of those 31, 15 are W4-owned rows (all of D-1 through D-5, which
sit on W4-REPLICATION, W4-STREAM, and W4-BACKUP rows) and 16 are later-wave
rows (all of D-6 and D-7).

Two rows carry a partial decision marker (PB-6006 and PB-6020): their typed
portions proceed in W4-STREAM/W4-BACKUP; only the script-execution portions
wait on D-5. They are counted once each in the 31 above.
