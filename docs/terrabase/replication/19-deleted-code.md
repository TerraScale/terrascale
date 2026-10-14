# 19. Deleted code

This list is for slice owners. Each slice deletes what it replaces (greenfield: no dual paths).

- **Cross-region:** `Features/CrossRegion/*` (`WanPuller`, `WanResponder`, `WanProtocol`, `WanMetrics`, `WanUsage`,
  `RegionPeers` from 3a, `CrossRegionExtensions`), `wan.proto`, the test `RegionBus`.
- **Replication:** today's `ReplicationSender`, `ReplicationService` and `ReplicationProtocol`, plus `DeltaGroup`,
  `OriginPositions`, `IReplicationJournal`, `IReplicationWait` (write-wait), `IReplicationPressure`, `PeerCredentials`,
  `MerkleTree`, `AntiEntropy*`, `ChangeGroup` (`TBDCG001`) and `anti_entropy.proto`. They are rewritten smaller under
  the same feature folder.
- **Catalog:** see section 15.
- **Engine:**
  - `MvRegister`, `CausalContext`, `Dot`, `DocumentWrite`, `GCounter`, the old `AwSet`, and the register parts of
    `LatticeEncoding`;
  - `SqliteStorageTransaction.Registers`, `.ChangeFeed` and `.ImportHistory`;
  - the tables `documents`, `change_log*`, `shape_history`, `commit_offset`, `changes`, `journal`,
    `replication_state` and `applied`;
  - `StampingStorageEngine` and `IWriteStamps`.
- **API:** preconditions (`If-Match`, `If-None-Match`, `assert`, `assert_count`), the per-collection change feed,
  shapes, subscriptions, `ShapeHeaders` and `IShapeSubscriptionService`.
- **Docs:** `replication.md`, `cross-region.md`, `change-feeds.md`, `shape-reads.md` and `subscriptions.md` are
  replaced by this document and the feed docs. anna-core §3 to §9 are marked superseded.
