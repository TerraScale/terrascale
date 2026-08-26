//! Public-construction tests for the sharding seam (SH-02).
//!
//! These tests reach only what an external caller can construct through
//! `terrabase_v2::sharding` over the public `TopologyView` seam: the fixed
//! bucket space and its derivation, the engine-validated bucket-range table,
//! the canonical shard identities, the explicit bucket move, and
//! deterministic routing to the owning Member at its ownership epoch.
//!
//! Non-claims held here: no migration streaming exists (SH-03), no sharded
//! query fan-out (SH-04), no failover qualification (SH-05), and no
//! performance fact is asserted.

use terrabase_v2::sharding::{
    BUCKET_SPACE, Bucket, BucketRange, BucketRangeTable, RoutingRequest, ShardId, ShardKey,
    ShardTopology, ShardingError,
};
use terrabase_v2::types::CollectionName;
use terrabase_v2::{DocumentId, MemberId, TenantId, TopologyView};

fn shard(number: u32) -> ShardId {
    ShardId::from_number(number)
}

fn bucket(value: u32) -> Bucket {
    Bucket::try_new(value).expect("bucket inside the fixed space")
}

fn range(start: u32, end: u32, number: u32) -> BucketRange {
    BucketRange::try_new(start, end, shard(number)).expect("valid bucket range")
}

/// The Platform-issued pair an external caller holds: a two-shard
/// bucket-range table and a `TopologyView` seating each shard.
fn two_shard_fixture() -> (BucketRangeTable, TopologyView) {
    let half = BUCKET_SPACE / 2;
    let table = BucketRangeTable::try_new(&[range(0, half, 0), range(half, BUCKET_SPACE, 1)])
        .expect("table partitions the space");
    let view = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-alpha", "r-alpha", "r-alpha")
        .expect("valid member")
        .with_member("m-beta", "r-beta", "r-beta")
        .expect("valid member")
        .with_owner_at_epoch(&shard(0).as_range(), "m-alpha", 4)
        .expect("valid owner")
        .with_owner_at_epoch(&shard(1).as_range(), "m-beta", 4)
        .expect("valid owner")
        .validated()
        .expect("servable view");
    (table, view)
}

#[test]
fn a_two_shard_database_routes_documents_to_their_owner() {
    let (table, view) = two_shard_fixture();
    let topology = ShardTopology::try_new(table, view).expect("seats");

    let tenant = TenantId::try_new("tenant-a").expect("valid tenant");
    let collection = CollectionName::try_new("orders").expect("valid collection");
    let identity = DocumentId::try_new("orders/1001").expect("valid identity");

    // Two deterministic lookups: shard key -> bucket -> shard -> owner and
    // epoch. The bucket never depends on the topology that routes it.
    let key_bucket = ShardKey::identity(&identity).bucket();
    let placement = topology
        .route(&RoutingRequest::by_identity(&tenant, &collection, &identity))
        .expect("routes");
    assert_eq!(placement.bucket(), key_bucket);
    let expected_shard = if key_bucket.get() < BUCKET_SPACE / 2 { shard(0) } else { shard(1) };
    assert_eq!(placement.shard(), expected_shard);
    let expected_owner =
        MemberId::try_new(if expected_shard == shard(0) { "m-alpha" } else { "m-beta" })
            .expect("valid member");
    assert_eq!(placement.owner(), &expected_owner);
    assert_eq!(placement.epoch(), 4);
    // The placement is tenant-bound and authorizes only for that tenant.
    assert_eq!(topology.authorize_placement(&placement, &tenant), Ok(()));
    let other = TenantId::try_new("tenant-b").expect("valid tenant");
    assert_eq!(
        topology.authorize_placement(&placement, &other),
        Err(ShardingError::TenantMismatch { routed_for: tenant.clone(), requested_by: other })
    );
}

#[test]
fn bucket_moves_are_public_explicit_operations() {
    let (table, view) = two_shard_fixture();
    let topology = ShardTopology::try_new(table.clone(), view.clone()).expect("seats");

    let moved = table.with_bucket_moved(bucket(1), shard(0), shard(1)).expect("explicit move");
    assert_eq!(moved.shard_of(bucket(1)), shard(1));
    // The transfer seats only with raised epochs on the affected shards.
    assert_eq!(
        topology.transition(moved.clone(), view.clone()),
        Err(ShardingError::EpochNotRaised { shard: shard(0), held: 4, offered: 4 })
    );
    let raised = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-alpha", "r-alpha", "r-alpha")
        .expect("valid member")
        .with_member("m-beta", "r-beta", "r-beta")
        .expect("valid member")
        .with_owner_at_epoch(&shard(0).as_range(), "m-alpha", 5)
        .expect("valid owner")
        .with_owner_at_epoch(&shard(1).as_range(), "m-beta", 5)
        .expect("valid owner")
        .validated()
        .expect("servable view");
    let transferred = topology.transition(moved, raised).expect("raised epochs seat");
    assert_eq!(transferred.shard_of(bucket(1)), shard(1));
    // An epoch regression never seats.
    assert!(matches!(
        transferred.transition(table, view),
        Err(ShardingError::EpochRegression { .. })
    ));
}

#[test]
fn the_engine_refuses_tables_that_do_not_partition_the_space() {
    let half = BUCKET_SPACE / 2;
    // Overlap.
    assert_eq!(
        BucketRangeTable::try_new(&[range(0, half + 1, 0), range(half, BUCKET_SPACE, 1)]),
        Err(ShardingError::TableOverlap { at: half })
    );
    // Non-canonical identity in a range entry is impossible to construct
    // (`ShardId` is the only way in), and a view range outside the
    // canonical form refuses at seating.
    let plain = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_member("m-alpha", "r-alpha", "r-alpha")
        .expect("valid member")
        .with_owner("main", "m-alpha")
        .expect("valid owner")
        .validated()
        .expect("servable view");
    assert_eq!(
        ShardTopology::try_new(BucketRangeTable::single_shard(shard(0)), plain),
        Err(ShardingError::ShardIdentityOutsideCanonicalForm { range: "main".to_owned() })
    );
}
