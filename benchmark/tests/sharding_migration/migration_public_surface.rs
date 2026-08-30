//! Public-surface tests for the bucket-migration seam (SH-03).
//!
//! These tests reach only what an external caller can construct through
//! `terrabase::sharding` over the public `TopologyView` and `Engine`
//! seams: the migration's typed lifecycle operations (start, resume,
//! confirm, cutover, rollback), the state machine's observable contract,
//! and the seating rules the cutover enforces. The record-streaming rounds
//! need a caller-supplied `TenantScope`, which the engine seam keeps
//! crate-internal, so streaming and kill-point recovery are proved by the
//! in-crate test lane (`src/sharding/migration_tests.rs`).
//!
//! Non-claims held here: no sharded query fan-out (SH-04), no failover
//! qualification (SH-05), and no performance fact is asserted.

use std::path::PathBuf;

use terrabase::TopologyView;
use terrabase::engine::{Engine, EngineConfig};
use terrabase::sharding::{
    BUCKET_SPACE, Bucket, BucketMigration, BucketMigrationState, BucketRange, BucketRangeTable,
    MigrationError, MoveConfirmation, ShardId, ShardTopology, ShardingError,
};

fn shard(number: u32) -> ShardId {
    ShardId::from_number(number)
}

fn bucket(value: u32) -> Bucket {
    Bucket::try_new(value).expect("bucket inside the fixed space")
}

fn range(start: u32, end: u32, number: u32) -> BucketRange {
    BucketRange::try_new(start, end, shard(number)).expect("valid bucket range")
}

/// The Platform-issued pair an external caller holds: shard/0 owns the
/// migrating bucket and everything below it, shard/1 everything above.
fn two_shard_fixture() -> (ShardTopology, Bucket) {
    let migrating = bucket(17);
    let table = BucketRangeTable::try_new(&[range(0, 18, 0), range(18, BUCKET_SPACE, 1)])
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
    (ShardTopology::try_new(table, view).expect("seats"), migrating)
}

/// The same membership with every shard's epoch raised.
fn raised_view() -> TopologyView {
    TopologyView::new()
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
        .expect("servable view")
}

/// One empty durable Engine: a source whose publication journal holds no
/// committed history (the external lifecycle drives a bucket with no
/// records; the streaming rounds are the in-crate lane's scope).
fn empty_source(tag: &str) -> (Engine, PathBuf) {
    let dir =
        std::env::temp_dir().join(format!("terrabase-sh03-public-{tag}-{}", std::process::id()));
    std::fs::remove_dir_all(&dir).ok();
    let config = EngineConfig::durable(&dir).expect("durable location");
    let engine = Engine::open(config).expect("engine opens");
    (engine, dir)
}

#[test]
fn a_migration_lifecycle_is_public_and_v1_shaped() {
    let (topology, migrating) = two_shard_fixture();
    let (source, dir) = empty_source("lifecycle");

    // Start: pending, with the observable progress naming both shards.
    let mut migration =
        BucketMigration::try_new(&topology, migrating, shard(0), shard(1)).expect("starts");
    let progress = migration.progress();
    assert_eq!(progress.state(), BucketMigrationState::Pending);
    assert_eq!(progress.bucket(), migrating);
    assert_eq!(progress.source(), shard(0));
    assert_eq!(progress.destination(), shard(1));
    assert_eq!(progress.resolved_records(), 0);
    assert_eq!(progress.confirmed_through(), None);

    // A source with no committed history confirms moved with nothing
    // outstanding: an empty bucket has no records to stream.
    assert_eq!(migration.confirm_moved(&source), Ok(MoveConfirmation::Confirmed { through: None }));
    assert_eq!(migration.progress().state(), BucketMigrationState::Moved);

    // The cutover seats exactly the bucket move with raised epochs.
    let moved =
        topology.table().with_bucket_moved(migrating, shard(0), shard(1)).expect("explicit move");
    let outcome = migration.cut_over(&source, moved.clone(), raised_view()).expect("cutover seats");
    assert!(!outcome.replayed());
    assert_eq!(outcome.topology().shard_of(migrating), shard(1));
    assert_eq!(outcome.topology().epoch_of(shard(0)), Some(5));
    assert_eq!(outcome.topology().epoch_of(shard(1)), Some(5));
    assert_eq!(migration.progress().state(), BucketMigrationState::OwnershipTransferred);

    source.shutdown().expect("source flushes");
    std::fs::remove_dir_all(dir).ok();
}

#[test]
fn migrations_refuse_foreign_sources_and_self_migration() {
    let (topology, migrating) = two_shard_fixture();
    assert_eq!(
        BucketMigration::try_new(&topology, migrating, shard(1), shard(0)),
        Err(MigrationError::Routing(ShardingError::BucketNotOwnedBySource {
            bucket: migrating,
            source: shard(1),
            owner: shard(0),
        }))
    );
    assert_eq!(
        BucketMigration::try_new(&topology, migrating, shard(0), shard(0)),
        Err(MigrationError::SourceIsDestination { bucket: migrating, shard: shard(0) })
    );
}

#[test]
fn cutover_seats_only_the_bucket_move_with_raised_epochs() {
    let (topology, migrating) = two_shard_fixture();
    let (source, dir) = empty_source("cutover");
    let mut migration =
        BucketMigration::try_new(&topology, migrating, shard(0), shard(1)).expect("starts");

    // Cutover before the move is confirmed refuses with the state named.
    let moved =
        topology.table().with_bucket_moved(migrating, shard(0), shard(1)).expect("explicit move");
    assert_eq!(
        migration.cut_over(&source, moved.clone(), raised_view()),
        Err(MigrationError::StepRefused { state: BucketMigrationState::Pending, step: "cut_over" })
    );

    migration.confirm_moved(&source).expect("empty history confirms");
    // Any other table edit is not this migration's cutover.
    assert_eq!(
        migration.cut_over(&source, topology.table().clone(), raised_view()),
        Err(MigrationError::TableIsNotTheBucketMove {
            bucket: migrating,
            source: shard(0),
            destination: shard(1),
        })
    );
    // Unraised epochs refuse through the seating rules.
    assert_eq!(
        migration.cut_over(&source, moved, topology.view().clone()),
        Err(MigrationError::SeatingRefused(ShardingError::EpochNotRaised {
            shard: shard(0),
            held: 4,
            offered: 4,
        }))
    );

    source.shutdown().expect("source flushes");
    std::fs::remove_dir_all(dir).ok();
}

#[test]
fn resume_replays_an_interrupted_seat_idempotently() {
    let (topology, migrating) = two_shard_fixture();
    let (source, dir) = empty_source("resume");
    let moved =
        topology.table().with_bucket_moved(migrating, shard(0), shard(1)).expect("explicit move");

    // The seat happened; the migration value is gone.
    let mut first =
        BucketMigration::try_new(&topology, migrating, shard(0), shard(1)).expect("starts");
    first.confirm_moved(&source).expect("empty history confirms");
    let seated = first.cut_over(&source, moved.clone(), raised_view()).expect("seats");
    drop(first);

    // Recovery derives the transferred state from the routing facts, and a
    // re-issued cutover replays idempotently against the seated pair.
    let mut resumed =
        BucketMigration::resume(seated.topology(), migrating, shard(0), shard(1)).expect("resumes");
    assert_eq!(resumed.progress().state(), BucketMigrationState::OwnershipTransferred);
    let replay = resumed.cut_over(&source, moved, raised_view()).expect("replayed cutover");
    assert!(replay.replayed());
    assert_eq!(replay.topology().shard_of(migrating), shard(1));

    source.shutdown().expect("source flushes");
    std::fs::remove_dir_all(dir).ok();
}

#[test]
fn rollback_records_a_clean_abandon_before_any_transfer() {
    let (topology, migrating) = two_shard_fixture();
    let (source, dir) = empty_source("rollback");
    let mut migration =
        BucketMigration::try_new(&topology, migrating, shard(0), shard(1)).expect("starts");
    migration.confirm_moved(&source).expect("empty history confirms");

    let record = migration.roll_back().expect("rolls back cleanly");
    assert_eq!(record.bucket(), migrating);
    assert_eq!(record.source(), shard(0));
    assert_eq!(record.destination(), shard(1));
    assert_eq!(record.resolved_records(), 0);
    assert_eq!(migration.progress().state(), BucketMigrationState::RolledBack);
    // Ownership never changed and no further step runs.
    assert_eq!(topology.shard_of(migrating), shard(0));
    assert_eq!(
        migration.roll_back(),
        Err(MigrationError::StepRefused {
            state: BucketMigrationState::RolledBack,
            step: "roll_back",
        })
    );

    source.shutdown().expect("source flushes");
    std::fs::remove_dir_all(dir).ok();
}
