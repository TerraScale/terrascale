# W2 replication-storage V1 test citation gap closure

Status: complete for 41 of 42 rows; PB-5012 honestly unclosable in the pinned
checkout. Strict qualification remains blocked on that single row.

Date: 2026-08-24

Workstream: WS-CONTRACT (replication-storage fragment). Assignment
`tbv2-w2-gap-closure`. Ticket: none, as required by the workspace charter
ledger exception.

This record closes the W2 strict-mode test-gap markers in
`parity/contracts/replication-storage.json` (rows PB-5000 through PB-5041) by
isolating exact V1 test citations from the pinned checkout. It changes
contract scope citations only; it does not claim that any TerraBaseV2 behavior
exists and does not alter any row obligation.

## Identities and inputs

- V1 source (read-only): TerraBase commit
  `fa99b9257776dbe625e6a2388c74c9ce916637ee` at
  `.worktrees/terrabase-v1-fa99b925-readonly`.
- V2 base: `2775b24` (develop at assignment start).
- Branch: `fleet/w2-gap-closure` (isolated worktree; not pushed).
- Method: every cited file was resolved in the pinned checkout and opened; the
  cited test class or test method names were confirmed in that file, and the
  test bodies were read where coverage was not evident from the declaration
  alone. Citations were written only after that verification.

## Result

- 41 of the 42 gapped rows now carry exact `[v1:test:...]` citations in place
  of their `[v1:test-gap:...]` marker.
- 1 row (PB-5012) keeps a test-gap marker with a refined concrete reason; see
  below. At the time of this change strict mode still failed closed with
  exactly that one gap; parent decision PD-2026-08-24-02 D2 later added the
  evidence-backed adjudicated-gap disposition (`parity/adjudications.json`),
  so strict mode now passes with PB-5012 reported as adjudicated while its
  V2 proof obligation stays open.
- Audit counts after this change: 258 rows, 251 V1-backed, 7 charter-backed,
  267 source citations, 346 test citations (was 231), 1 test gap (was 42).

## Honestly unclosable row

### PB-5012 storage-schema-upgrade

Claim: V1 Voron and database schema upgrade steps, version checks, and upgrade
recovery behavior (VoronSchemaUpdater, SchemaUpgrader, versioned update
classes).

What was searched in the pinned checkout:

- Direct test-class names implied by the W2 semantics note
  (`SchemaUpgradeTests`, fixtures for `DocumentsVersion` and
  `VoronCurrentVersion`): no such test class exists under `test/`.
- The fixture data itself: `test/TerraBase.SlowTests/SchemaUpgrade/Issues/`
  ships `DocumentsVersion/schema_9`, `DocumentsVersion/schema_999`,
  `VoronCurrentVersion/schema_999`, and `SystemVersion/*.zip`, all declared as
  `None`/`Folder` content items in `TerraBase.SlowTests.csproj`. No `.cs` file
  in the repository references these paths or names, so the fixtures are
  orphaned in this checkout.
- References to `VoronSchemaUpdater`, `SchemaUpgrader`, or `SchemaVersion` in
  any test project: none.
- Case-insensitive sweeps for `schemaupgrade`, `schema upgrade`,
  `unsupported version`, and `schema` across the Voron test trees.

Candidate near-miss tests and why they do not qualify:

- `test/TerraBase.SlowTests.Issues/Issues/TerraBase-19152.cs`
  (`ShouldNotSkipConflictedRevisions_InternalReplication`): the only test in
  the tree that mentions a revision schema upgrade. It is compiled with
  `Skip = "Skip until TerraBase-21796 is resolved (revision schema upgrade)"`
  and its subject is replication of conflicted revisions, not schema upgrade
  ordering, version rejection, or upgrade recovery. Citing it would not be
  evidence of the row's behavior.
- `test/TerraBase.EndToEndTests/Voron/ValidHeaders.cs` (cited by PB-5003):
  verifies header layout round-trips, not version transition or upgrade.
- The orphaned fixture directories: data without a test is not a test.

Proposed disposition (parent decides): record an evidence-backed exclusion for
the V1 schema-upgrade test obligation (V2 uses a new storage format and must
not open V1 files directly, so the V1 upgrade ladder is a compatibility
reference rather than a behavior to reproduce), or add a W3+ obligation to
port a schema-upgrade regression test into V2 with its own fixtures. This row
must not be marked qualified on the current evidence.

## Corrections to W2 semantics notes (not applied)

The W2 `public_contract.semantics` bullets name several tests with slightly
wrong owners or names. The citations use the real files; the semantics text
was left untouched to keep this change citation-only. Follow-ups for the
fragment owner:

- PB-5014 names `IncrementalBackupChainIntegrity.RestoreRejectsIncremental
  BackupChainWithMissingArchive`; the class is `Incremental` in
  `test/TerraBase.SlowTests/Voron/Backups/Incremental.cs`.
- PB-5022, PB-5028, PB-5029, and PB-5030 attribute
  `ResolveToLatestInClusterWithAttachment`,
  `ResolveToLatestInClusterWithCounter`, and
  `ModifyResolvedDocumentShouldKeepTheRevisions` to
  `ReplicationAutomaticConflictResolution`; they live in
  `ReplicationConflictsTests.cs` (whose class is
  `AutomaticConflictResolution` for the script-resolution cases).
- PB-5039 and PB-5040 attribute `DynamicNodeDistributionForOrchestrator`,
  `CanAddNodeToShard`, `AddingNewNodeToShardShouldUpdateShardRequestExecutor
  Topology_TerraBase_20702`, and
  `CompareExchangeTombstonesCleaner_ShouldBeClean_IfSomeShardsNeverBackedUp`
  (note the plural "Tombstones") to `ShardingTopologyTests`; they live in
  `ShardedClusterObserverTests.cs`.
- PB-5017 names `ForceStepDownAndElectNewLeader` without a file; it is a test
  in `test/TerraBase.RachisTests/ElectionTests.cs`.

## Caveats recorded with the citations

- `test/TerraBase.SlowTests.Tools/RecoveryTests/SampleDatabaseRecovery.cs`:
  `CanRecoverSampleData` is compiled with `Skip = "TerraBase-13765"`;
  `CanRecoverTimeSeries` is active. PB-5009 also cites `Recovery.cs` and
  `RecoveryMultipleJournals.cs`, which are active, so the row does not rest on
  the skipped case.
- `test/TerraBase.SlowTests/Sharding/Backup/ShardedBackupTests .cs` (the file
  name contains a space before `.cs`). The citation preserves that exact path;
  the audit's path resolution accepts it.
- `test/TerraBase.SlowTests/Sharding/Backup/SnapshotBackupTests.cs` asserts
  that snapshot backup throws `NotSupportedInShardingException` on a sharded
  database; it is cited for PB-5040 as the observable V1 boundary behavior,
  not as a successful-snapshot claim.

## Per-row citation map

| Row | Cited V1 test files |
| --- | --- |
| PB-5000 | SlowTests/Server/Replication: ReplicationDocuments.cs, ReplicationBasicTestsSlow.cs, ExternalReplicationTests.cs |
| PB-5001 | SlowTests/Voron: Recovery.cs, RecoveryMultipleJournals.cs |
| PB-5002 | EndToEndTests/Voron/StorageTest.cs; SlowTests/Voron/Recovery.cs |
| PB-5003 | EndToEndTests/Voron/ValidHeaders.cs |
| PB-5004 | EndToEndTests/Voron/Journal: BasicActions.cs, UncommittedTransactions.cs |
| PB-5005 | EndToEndTests/Voron: Journal/Mvcc.cs, ReaderWriterLockTests.cs, ClonedReadTransactions.cs |
| PB-5006 | EndToEndTests/Voron: Trees/Basic.cs, Trees/CanIterateBackward.cs, Tables/BasicUsage.cs, FixedSize/SimpleFixedSizeTrees.cs, Containers/ContainerTests.cs, RawData/SmallDataSection.cs |
| PB-5007 | EndToEndTests/Voron: ScratchBuffer/ActivelyUsedBytesTracking.cs, ScratchBuffer/ScratchCanForceToFlushOldPages.cs, Journal/UncommittedTransactions.cs |
| PB-5008 | EndToEndTests/Voron/Compaction/StorageCompactionTests.cs |
| PB-5009 | SlowTests/Voron: Recovery.cs, RecoveryMultipleJournals.cs; EndToEndTests/Voron/Backups/TerraBase_3115.cs; SlowTests.Tools/RecoveryTests/SampleDatabaseRecovery.cs |
| PB-5010 | SlowTests/Voron: RecoveryMultipleJournals.cs, Storage/Quotas.cs; StressTests/Voron/Issues/TerraBase_19278.cs |
| PB-5011 | EndToEndTests/Sparrow/EncryptionTests.cs; SlowTests/Voron/TerraBase_13940.cs; SlowTests/Server/Documents/PeriodicBackup/EncryptedBackupTest.cs |
| PB-5012 | test-gap retained (honestly unclosable; see above) |
| PB-5013 | EndToEndTests/Voron/Backups: Full.cs, BackupToOneZipFile.cs |
| PB-5014 | EndToEndTests/Voron/Backups/TerraBase_3115.cs; SlowTests/Voron: TerraBase_12725_2.cs, TerraBase_12725_4.cs, Backups/Incremental.cs |
| PB-5015 | SlowTests/Server/Documents/PeriodicBackup/RecoveryPointManifestTests.cs |
| PB-5016 | RachisTests: BasicTests.cs, BasicCluster.cs, DatabaseCluster/AtomicClusterReadWriteTests.cs; SlowTests/Cluster/ClusterTransactionTests.cs |
| PB-5017 | RachisTests: ElectionTests.cs, BasicCluster.cs |
| PB-5018 | RachisTests: BasicCluster.cs, ElectionTests.cs, LogDivergenceTests.cs |
| PB-5019 | RachisTests: TopologyChangesTests.cs, AddNodeToClusterTests.cs, DatabaseCluster/RemoveNodeFromCluster.cs, ClusterDemotePromoteTests.cs |
| PB-5020 | RachisTests: ElectionTests.cs, DatabaseCluster/PlatformConsistencyContractTests.cs, DatabaseCluster/ClusterDatabaseMaintenance.cs |
| PB-5021 | SlowTests/Server/Replication/ReplicationConflictsTests.cs; RachisTests/DatabaseCluster/PlatformConsistencyContractTests.cs |
| PB-5022 | SlowTests/Server/Replication: ReplicationConflictsTests.cs, ReplicationAutomaticConflictResolution.cs; RachisTests/DatabaseCluster/PlatformReplicationObservationTests.cs |
| PB-5023 | SlowTests/Server/Replication: ReplicationTombstoneTests.cs, ReplicationCleanTombstones.cs, PullReplicationPreventDeletionsTests.cs; SlowTests/Sharding/Cluster/ReshardingTombstoneRepickLoop.cs |
| PB-5024 | RachisTests/DatabaseCluster: PlatformConsistencyContractTests.cs, PlatformActiveActiveContractTests.cs |
| PB-5025 | SlowTests/Server/Replication/ExternalReplicationTests.cs |
| PB-5026 | SlowTests/Server/Replication/ReplicationWriteAssurance.cs |
| PB-5027 | SlowTests/Server/Replication: PullReplicationTests.cs, PullReplicationPreventDeletionsTests.cs |
| PB-5028 | SlowTests/Client/Attachments: AttachmentsReplication.cs, AttachmentFailover.cs; SlowTests/Server/Replication: ReplicationConflictsTests.cs, PullReplicationPreventDeletionsTests.cs |
| PB-5029 | SlowTests/Client/Counters/CountersReplication.cs; SlowTests/Client/TimeSeries: Replication/TimeSeriesReplicationTests.cs, TimeSeriesAndDocumentConflicts.cs; SlowTests/Server/Documents/PeriodicBackup/BackupAndRestoreForIncrementalTimeSeries.cs; SlowTests/Server/Replication/ReplicationConflictsTests.cs |
| PB-5030 | SlowTests/Server/Replication: ReplicationRevisionsTests.cs, ReplicationWithRevisions.cs, ReplicationConflictsTests.cs; SlowTests/Server/Documents/Revisions: RevisionsReplication.cs, RevisionsReplicationTransactionMarker.cs |
| PB-5031 | RachisTests/DatabaseCluster/PlatformConsistencyContractTests.cs |
| PB-5032 | RachisTests/DatabaseCluster/PlatformReplicationObservationTests.cs |
| PB-5033 | SlowTests/Server/Replication/ExternalReplicationTests.cs; RachisTests: BasicCluster.cs, DatabaseCluster/PlatformConsistencyContractTests.cs |
| PB-5034 | RachisTests/DatabaseCluster/PlatformActiveActiveContractTests.cs |
| PB-5035 | SlowTests/Server/Documents/OngoingTasks/ReplicationOngoingTaskProgressTests.cs; SlowTests/Sharding/Client/Operations/GetDocumentConflictsTests.cs; RachisTests/BasicCluster.cs |
| PB-5036 | EndToEndTests/Sharding/BasicSharding.cs; SlowTests/Sharding: ShardingTopologyTests.cs, Cluster/ShardedClusterObserverTests.cs |
| PB-5037 | SlowTests/Sharding/Cluster/BucketStatsTests.cs |
| PB-5038 | SlowTests/Sharding: Cluster/ReshardingTests.cs, Cluster/ReshardingTombstoneRepickLoop.cs, BucketMigration/BucketsMigratorTests.cs, BucketMigration/DocumentsMigrationTests.cs, Client/OptimisticConcurrencyAfterResharding.cs |
| PB-5039 | SlowTests/Sharding: Cluster/ShardedClusterObserverTests.cs, ShardingTopologyTests.cs |
| PB-5040 | SlowTests/Sharding: Replication/ShardedExternalReplicationTests.cs, Backup/ShardedBackupTests .cs, Backup/ShardedRestoreBackupTests.cs, Backup/SnapshotBackupTests.cs, Cluster/ShardedClusterObserverTests.cs |
| PB-5041 | SlowTests: Cluster/ShardedClusterTransactionTests.cs, Sharding/Cluster/ShardedClusterObserverTests.cs, Sharding/Cluster/SubscriptionsWithReshardingTests.cs, Sharding/Subscriptions/ShardedSubscriptionClusterTests.cs, Sharding/Replication/ShardedExternalReplicationTests.cs |

## Checks

All commands ran in the isolated worktree
`.worktrees/terrabasev2-w2-gap-closure` at base `2775b24`:

- Red baseline: `python3 scripts/validate_v1_citations.py --mode strict
  --v1-root <pinned V1> --workspace-root <workspace> --gap-report
  parity/v1-test-gaps.json` — exit 1, `STRICT V1 QUALIFICATION FAILED: 42
  UNRESOLVED TEST GAPS` (231 test citations), 2.5 s.
- After the citations: same command — exit 1, `STRICT V1 QUALIFICATION
  FAILED: 1 UNRESOLVED TEST GAPS` (258 rows, 251 V1-backed, 7 charter-backed,
  267 source citations, 346 test citations, 0 invalid citations), 2.6 s. The
  single remaining failure is the honest PB-5012 gap above, not an invalid
  citation.
- `python3 scripts/test_v1_citations.py` — exit 0, 18 tests OK, 3.8 s.
- `python3 scripts/test_parity_tooling.py` — exit 0, 9 tests OK, 2.2 s.
- `git diff --check -- parity/contracts/replication-storage.json
  parity/v1-test-gaps.json` — exit 0, clean.
- `python3 scripts/validate_parity.py --check` after the contract edits —
  exit 1 with generated-inventory drift, as expected for a contract-scope
  change. `--generate` ran twice and produced the byte-identical inventory
  (SHA-256 `554d9bb4446918ce38f1a220f03c7cd731de8bbe0d7a10d838a991d6353ce09e`),
  after which `--check` exited 0.

`parity/inventory.json` is integrator-owned: it was restored byte-identically
to HEAD (SHA-256
`70f9e65755c9169f3fafa93775a5dd0b9d8f2f2d5c5c4fafd7bf63271d18f004`, matching
the charter-recorded W2 inventory hash) and is not part of this change.
