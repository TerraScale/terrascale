//! SH-03 contract tests: live bucket migration over the replication plane's
//! catch-up machinery (PB-5038/5039, ADR 0011 D3).
//!
//! Scope proved here: the migration state machine's observable contract
//! (pending -> moving -> moved -> ownership transferred, V1's status shape),
//! bucket-scoped streaming that resolves only the migrating bucket's
//! committed records at the destination through the durable operation ledger,
//! restartability (a fresh drive over durable engines re-resolves every
//! record idempotently), the bounded in-flight window (cutover refuses new
//! records past the confirmed point), idempotent cutover seating exactly the
//! bucket-move table edit with raised epochs, clean rollback, and in-flight
//! query resolution across cutover through the routing seam.
//!
//! Non-claims: no sharded query fan-out (SH-04), no failover qualification
//! (SH-05), and no performance fact appears in any test name or assertion.
//! Kill-point tests run in-process; the exclusive recovery lane is reserved
//! for BK-02 and no recovery-lane claim is made here.

use std::path::PathBuf;
use std::sync::LazyLock;
use std::sync::atomic::{AtomicU64, Ordering};

use crate::engine::{CommitOutcome, Engine, EngineConfig};
use crate::replication::TopologyView;
use crate::sharding::{
    BUCKET_SPACE, Bucket, BucketMigration, BucketMigrationState, BucketRange, BucketRangeTable,
    MigrationError, MoveConfirmation, RoutingRequest, ShardId, ShardKey, ShardTopology,
    ShardingError,
};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, StorageSequence, TenantId, TenantScope,
};

/// The migration corpus, computed once: the migrating bucket is the bucket of
/// the `mig/anchor` identity, five further identities that route to it, and
/// one identity that routes elsewhere. Finding co-bucket identities is a
/// search over the fixed hash (roughly one hit per 2^20 identities), so the
/// corpus is shared by every test in this file.
static CORPUS: LazyLock<Corpus> = LazyLock::new(|| {
    let anchor = DocumentId::try_new("mig/anchor").expect("valid identity");
    let bucket = ShardKey::identity(&anchor).bucket();
    let mut in_bucket: Vec<String> = Vec::new();
    let mut out_of_bucket: Option<String> = None;
    let mut index = 0;
    while in_bucket.len() < 5 || out_of_bucket.is_none() {
        let name = format!("mig/{index}");
        let identity = DocumentId::try_new(&name).expect("valid identity");
        if ShardKey::identity(&identity).bucket() == bucket {
            in_bucket.push(name);
        } else {
            out_of_bucket = Some(name);
        }
        index += 1;
    }
    Corpus { bucket, in_bucket, out_of_bucket: out_of_bucket.expect("distinct buckets exist") }
});

/// The shared routing corpus of the migration tests.
struct Corpus {
    bucket: Bucket,
    in_bucket: Vec<String>,
    out_of_bucket: String,
}

fn shard(number: u32) -> ShardId {
    ShardId::from_number(number)
}

fn range(start: u32, end: u32, number: u32) -> BucketRange {
    BucketRange::try_new(start, end, shard(number)).expect("valid bucket range")
}

/// One Region per shard owner, one Member per Region, both shards seated at
/// `epoch`: the membership pair the migration tests seat tables against.
fn view_at(epoch: u64) -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-alpha", "r-alpha", "r-alpha")
        .expect("valid member")
        .with_member("m-beta", "r-beta", "r-beta")
        .expect("valid member")
        .with_owner_at_epoch(&shard(0).as_range(), "m-alpha", epoch)
        .expect("valid owner")
        .with_owner_at_epoch(&shard(1).as_range(), "m-beta", epoch)
        .expect("valid owner")
        .validated()
        .expect("servable view")
}

/// Seats the migrating bucket with everything below it on shard/0 (the
/// migration source, owned by `m-alpha`); everything above routes to shard/1
/// (the destination, owned by `m-beta`), both at epoch 1. The source keeps
/// buckets after the move; the drained-source shape is
/// [`drained_seating_for`].
fn seating_for(bucket: Bucket) -> ShardTopology {
    let value = bucket.get();
    assert!(value + 1 < BUCKET_SPACE, "the corpus bucket must not be the space's last");
    let table =
        BucketRangeTable::try_new(&[range(0, value + 1, 0), range(value + 1, BUCKET_SPACE, 1)])
            .expect("table partitions the space");
    ShardTopology::try_new(table, view_at(1)).expect("valid shard topology")
}

/// Seats `bucket` alone on shard/0, so moving it drains the source shard.
fn drained_seating_for(bucket: Bucket) -> ShardTopology {
    let value = bucket.get();
    let mut entries = Vec::new();
    if value > 0 {
        entries.push(range(0, value, 1));
    }
    entries.push(range(value, value + 1, 0));
    if value + 1 < BUCKET_SPACE {
        entries.push(range(value + 1, BUCKET_SPACE, 1));
    }
    let table = BucketRangeTable::try_new(&entries).expect("table partitions the space");
    ShardTopology::try_new(table, view_at(1)).expect("valid shard topology")
}

/// The next view with every shard's epoch raised to 2.
fn raised_view() -> TopologyView {
    view_at(2)
}

/// The bucket-move edit of the seated table: the corpus bucket from shard/0
/// to shard/1.
fn moved_table(topology: &ShardTopology, bucket: Bucket) -> BucketRangeTable {
    topology.table().with_bucket_moved(bucket, shard(0), shard(1)).expect("explicit bucket move")
}

fn migration_dir(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("terrabase-sh03-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> (Engine, PathBuf) {
    let dir = migration_dir(tag);
    std::fs::remove_dir_all(&dir).ok();
    let config = EngineConfig::durable(&dir)
        .expect("durable location")
        .with_storage_backend(crate::engine::StorageBackend::RootFiles);
    let engine = Engine::open(config).expect("engine opens");
    (engine, dir)
}

fn reopen_engine(dir: &PathBuf) -> Engine {
    Engine::open(
        EngineConfig::durable(dir)
            .expect("durable location")
            .with_storage_backend(crate::engine::StorageBackend::RootFiles),
    )
    .expect("engine reopens")
}

fn scope_of(engine: &Engine, name: &str) -> TenantScope {
    TenantScope::from_parts(TenantId::try_new(name).expect("valid tenant"), engine.identity())
}

fn document(name: &str) -> Document {
    Document::new(
        DocumentId::try_new(name).expect("valid identity"),
        CollectionName::try_new("orders").expect("valid collection"),
        DocumentValue::String(format!("payload-{name}")),
    )
}

fn identity(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identity")
}

fn put_at(engine: &Engine, scope: &TenantScope, operation: &str, name: &str) {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let mut transaction = engine
        .begin(scope, snapshot, OperationId::try_new(operation).expect("valid operation"))
        .expect("begin");
    transaction.put(document(name), ExpectedVersion::Any).expect("stage put");
    assert!(
        matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)),
        "fixture put commits"
    );
}

fn delete_at(engine: &Engine, scope: &TenantScope, operation: &str, name: &str) {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    let mut transaction = engine
        .begin(scope, snapshot, OperationId::try_new(operation).expect("valid operation"))
        .expect("begin");
    transaction.delete(identity(name), ExpectedVersion::Any).expect("stage delete");
    assert!(
        matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)),
        "fixture delete commits"
    );
}

/// The document identities one engine holds for one tenant, in canonical key
/// order.
fn documents_at(engine: &Engine, scope: &TenantScope) -> Vec<String> {
    let snapshot = engine.read_snapshot(scope).expect("snapshot");
    engine
        .snapshot_documents(scope, &snapshot)
        .expect("documents")
        .into_iter()
        .map(|document| document.id().as_str().to_owned())
        .collect()
}

/// The committed operation identities one engine's publication journal holds
/// for one tenant, in journal order.
fn journal_at(engine: &Engine, name: &str) -> Vec<String> {
    engine
        .tenant_commits(
            &TenantId::try_new(name).expect("valid tenant"),
            StorageSequence::new(0),
            crate::sharding::MAX_MIGRATION_STREAM_BATCH,
        )
        .expect("journal read")
        .into_iter()
        .map(|commit| commit.operation_id().as_str().to_owned())
        .collect()
}

/// The source fixture of the streaming tests: `tenant-a` holds three bucket
/// records, one out-of-bucket record, and one put-then-delete pair inside the
/// bucket; `tenant-b` holds one bucket record.
fn source_fixture(source: &Engine) {
    let corpus = &*CORPUS;
    let scope_a = scope_of(source, "tenant-a");
    put_at(source, &scope_a, "op-a1", &corpus.in_bucket[0]);
    put_at(source, &scope_a, "op-a2", &corpus.in_bucket[1]);
    put_at(source, &scope_a, "op-foreign", &corpus.out_of_bucket);
    put_at(source, &scope_a, "op-del", &corpus.in_bucket[2]);
    delete_at(source, &scope_a, "op-del-delete", &corpus.in_bucket[2]);
    put_at(source, &scope_a, "op-a3", &corpus.in_bucket[3]);
    let scope_b = scope_of(source, "tenant-b");
    put_at(source, &scope_b, "op-b1", &corpus.in_bucket[4]);
}

/// Drives streaming rounds for every tenant until each round reports caught
/// up, returning the last report.
fn drive_to_caught_up(
    migration: &mut BucketMigration,
    source: &Engine,
    destination: &Engine,
) -> crate::sharding::MigrationStreamReport {
    let mut last = None;
    for name in ["tenant-a", "tenant-b"] {
        let scope = scope_of(destination, name);
        loop {
            let report =
                migration.stream_round(source, destination, &scope).expect("stream round runs");
            let caught = report.caught_up();
            last = Some(report);
            if caught {
                break;
            }
        }
    }
    last.expect("at least one round ran")
}

/// Streams every tenant to caught up and confirms the move.
fn drive_to_moved(
    migration: &mut BucketMigration,
    source: &Engine,
    destination: &Engine,
) -> MoveConfirmation {
    drive_to_caught_up(migration, source, destination);
    match migration.confirm_moved(source).expect("confirmation runs") {
        confirmed @ MoveConfirmation::Confirmed { .. } => confirmed,
        MoveConfirmation::Outstanding { tenant, .. } => {
            panic!("every tenant is caught up, but {tenant} reports outstanding")
        }
    }
}

#[test]
fn migrations_start_only_from_owned_buckets() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);

    // The named source must own the bucket.
    assert_eq!(
        BucketMigration::try_new(&topology, corpus.bucket, shard(1), shard(0)),
        Err(MigrationError::Routing(ShardingError::BucketNotOwnedBySource {
            bucket: corpus.bucket,
            source: shard(1),
            owner: shard(0),
        }))
    );
    // A bucket never migrates to itself.
    assert_eq!(
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(0)),
        Err(MigrationError::SourceIsDestination { bucket: corpus.bucket, shard: shard(0) })
    );
    // The valid start reports the V1-shaped pending state.
    let migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    let progress = migration.progress();
    assert_eq!(progress.state(), BucketMigrationState::Pending);
    assert_eq!(progress.bucket(), corpus.bucket);
    assert_eq!(progress.source(), shard(0));
    assert_eq!(progress.destination(), shard(1));
    assert_eq!(progress.resolved_records(), 0);
    assert_eq!(progress.confirmed_through(), None);
}

#[test]
fn resume_derives_state_from_routing_facts() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);

    // The bucket still routes to the source: the migration restarts pending.
    let restart =
        BucketMigration::resume(&topology, corpus.bucket, shard(0), shard(1)).expect("resumes");
    assert_eq!(restart.progress().state(), BucketMigrationState::Pending);

    // The bucket already routes to the destination: the migration resumes
    // transferred, so a re-driven cutover is an idempotent replay.
    let transferred_topology =
        ShardTopology::try_new(moved_table(&topology, corpus.bucket), raised_view())
            .expect("transferred seating");
    let resumed = BucketMigration::resume(&transferred_topology, corpus.bucket, shard(0), shard(1))
        .expect("resumes");
    assert_eq!(resumed.progress().state(), BucketMigrationState::OwnershipTransferred);

    // The bucket moved to a third shard: this migration is void.
    let elsewhere = transferred_topology
        .table()
        .with_bucket_moved(corpus.bucket, shard(1), shard(5))
        .expect("reassigned");
    let view = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-alpha", "r-alpha", "r-alpha")
        .expect("valid member")
        .with_member("m-beta", "r-beta", "r-beta")
        .expect("valid member")
        .with_owner_at_epoch(&shard(0).as_range(), "m-alpha", 3)
        .expect("valid owner")
        .with_owner_at_epoch(&shard(1).as_range(), "m-beta", 3)
        .expect("valid owner")
        .with_owner_at_epoch(&shard(5).as_range(), "m-alpha", 1)
        .expect("valid owner")
        .validated()
        .expect("servable view");
    let reassigned = ShardTopology::try_new(elsewhere, view).expect("reassigned seating");
    assert_eq!(
        BucketMigration::resume(&reassigned, corpus.bucket, shard(0), shard(1)),
        Err(MigrationError::Routing(ShardingError::BucketNotOwnedBySource {
            bucket: corpus.bucket,
            source: shard(0),
            owner: shard(5),
        }))
    );
}

#[test]
fn streaming_resolves_only_the_buckets_records() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("stream-scope");
    let (destination, destination_dir) = open_engine("stream-scope-dest");
    source_fixture(&source);

    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    let last = drive_to_caught_up(&mut migration, &source, &destination);

    // Every tenant reached its high water, and the state left pending.
    assert!(last.caught_up());
    assert_eq!(migration.progress().state(), BucketMigrationState::Moving);
    // The bucket holds six records (four puts, one delete, one tenant-b
    // put); the out-of-bucket record never streamed.
    assert_eq!(migration.progress().resolved_records(), 6);
    let destination_a = scope_of(&destination, "tenant-a");
    let destination_b = scope_of(&destination, "tenant-b");
    // The destination resolved the live documents of the bucket: the deleted
    // identity is absent, the out-of-bucket identity never arrived.
    let mut held = documents_at(&destination, &destination_a);
    held.sort();
    let mut expected: Vec<String> = corpus.in_bucket[0..4]
        .iter()
        .filter(|name| **name != corpus.in_bucket[2])
        .cloned()
        .collect();
    expected.sort();
    assert_eq!(held, expected);
    assert_eq!(documents_at(&destination, &destination_b), vec![corpus.in_bucket[4].clone()]);
    // The destination's journal names exactly the five tenant-a bucket
    // operations (the foreign record is absent).
    let journal_a = journal_at(&destination, "tenant-a");
    let journal_b = journal_at(&destination, "tenant-b");
    assert_eq!(journal_a.len(), 5);
    assert_eq!(journal_b, vec!["op-b1".to_owned()]);
    assert!(!journal_a.contains(&"op-foreign".to_owned()));

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

#[test]
fn streaming_is_restartable_without_double_apply() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("restart");
    let (destination, destination_dir) = open_engine("restart-dest");
    source_fixture(&source);

    // One interrupted drive: a single round for tenant-a, then the process
    // is dropped (migration value, advisory progress, and engines all lost).
    let destination_scope = scope_of(&destination, "tenant-a");
    let mut first =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    first.stream_round(&source, &destination, &destination_scope).expect("round runs");
    drop(first);
    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");

    // Restart from the durable locations and resume the migration.
    let source = reopen_engine(&source_dir);
    let destination = reopen_engine(&destination_dir);
    let mut resumed = BucketMigration::resume(&topology, corpus.bucket, shard(0), shard(1))
        .expect("resumes after interruption");
    let destination_scope = scope_of(&destination, "tenant-a");
    let re_scan = resumed
        .stream_round(&source, &destination, &destination_scope)
        .expect("re-drive round runs");
    // The re-drive re-scans the history the interrupted drive already
    // streamed and resolves every duplicate through the operation ledger:
    // nothing applies twice (PB-5039's no-double-apply rule).
    assert_eq!(re_scan.applied(), 0);
    assert_eq!(re_scan.duplicates_resolved(), 5);
    drive_to_caught_up(&mut resumed, &source, &destination);
    assert_eq!(resumed.progress().resolved_records(), 6);
    let destination_scope = scope_of(&destination, "tenant-a");
    assert_eq!(documents_at(&destination, &destination_scope).len(), 3);
    let confirmation = drive_to_moved(&mut resumed, &source, &destination);
    assert!(matches!(confirmation, MoveConfirmation::Confirmed { .. }));

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

#[test]
fn move_confirmation_requires_every_tenant_scanned() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("confirm");
    let (destination, destination_dir) = open_engine("confirm-dest");
    source_fixture(&source);

    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    // Stream only tenant-a to its high water.
    let scope_a = scope_of(&destination, "tenant-a");
    loop {
        let report = migration.stream_round(&source, &destination, &scope_a).expect("round runs");
        if report.caught_up() {
            break;
        }
    }
    // The confirmation names tenant-b as outstanding and stays moving.
    let outstanding = migration.confirm_moved(&source).expect("confirmation runs");
    assert_eq!(
        outstanding,
        MoveConfirmation::Outstanding {
            tenant: TenantId::try_new("tenant-b").expect("valid tenant")
        }
    );
    assert_eq!(migration.progress().state(), BucketMigrationState::Moving);
    // Streaming tenant-b completes the confirmation at the source's durable
    // high water.
    let confirmed = drive_to_moved(&mut migration, &source, &destination);
    let MoveConfirmation::Confirmed { through } = confirmed else {
        panic!("every tenant is caught up");
    };
    let high_water = source.journaled_high_water().expect("high water").expect("journaled");
    assert_eq!(through, Some(high_water));
    assert_eq!(migration.progress().state(), BucketMigrationState::Moved);
    assert_eq!(migration.progress().confirmed_through(), Some(high_water));

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

#[test]
fn cutover_seats_exactly_the_bucket_move_with_raised_epochs() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("cutover");
    let (destination, destination_dir) = open_engine("cutover-dest");
    source_fixture(&source);

    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    // Cutover before the move is confirmed refuses.
    assert_eq!(
        migration.cut_over(&source, moved_table(&topology, corpus.bucket), raised_view()),
        Err(MigrationError::StepRefused { state: BucketMigrationState::Pending, step: "cut_over" })
    );
    drive_to_moved(&mut migration, &source, &destination);

    // A table that is not this migration's bucket move refuses: the cutover
    // is exactly the explicit edit, never a Platform-combined table.
    let unmoved = topology.table().clone();
    assert_eq!(
        migration.cut_over(&source, unmoved, raised_view()),
        Err(MigrationError::TableIsNotTheBucketMove {
            bucket: corpus.bucket,
            source: shard(0),
            destination: shard(1),
        })
    );
    // Unraised epochs refuse through the seating rules.
    assert_eq!(
        migration.cut_over(&source, moved_table(&topology, corpus.bucket), view_at(1)),
        Err(MigrationError::SeatingRefused(ShardingError::EpochNotRaised {
            shard: shard(0),
            held: 1,
            offered: 1,
        }))
    );
    // The valid pair seats the transfer and reports it as a fresh cutover.
    let outcome = migration
        .cut_over(&source, moved_table(&topology, corpus.bucket), raised_view())
        .expect("cutover seats");
    assert!(!outcome.replayed());
    let transferred = outcome.topology();
    assert_eq!(transferred.shard_of(corpus.bucket), shard(1));
    assert_eq!(transferred.epoch_of(shard(0)), Some(2));
    assert_eq!(transferred.epoch_of(shard(1)), Some(2));
    assert_eq!(migration.progress().state(), BucketMigrationState::OwnershipTransferred);

    // Streaming after the transfer refuses.
    let destination_scope = scope_of(&destination, "tenant-a");
    assert_eq!(
        migration.stream_round(&source, &destination, &destination_scope),
        Err(MigrationError::StepRefused {
            state: BucketMigrationState::OwnershipTransferred,
            step: "stream_round",
        })
    );
    // Rolling back after the transfer refuses: the inverse move is a new
    // migration the Platform issues, never a silent undo.
    assert_eq!(
        migration.roll_back(),
        Err(MigrationError::StepRefused {
            state: BucketMigrationState::OwnershipTransferred,
            step: "roll_back",
        })
    );

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

#[test]
fn cutover_is_idempotent_across_an_interrupted_seat() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("cutover-replay");
    let (destination, destination_dir) = open_engine("cutover-replay-dest");
    source_fixture(&source);

    // Drive to the cutover and "interrupt" right after the seating: the
    // migration value is lost, the topology facts are not.
    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    drive_to_moved(&mut migration, &source, &destination);
    let (moved, raised) = (moved_table(&topology, corpus.bucket), raised_view());
    let seated = migration.cut_over(&source, moved.clone(), raised.clone()).expect("seats");
    drop(migration);
    drop(seated);

    // The Platform re-issues the same cutover pair against the seated facts.
    let transferred = ShardTopology::try_new(moved, raised).expect("seated topology");
    let mut resumed = BucketMigration::resume(&transferred, corpus.bucket, shard(0), shard(1))
        .expect("resumes transferred");
    let replay = resumed
        .cut_over(&source, transferred.table().clone(), transferred.view().clone())
        .expect("replayed cutover");
    assert!(replay.replayed());
    assert_eq!(replay.topology().table(), transferred.table());
    assert_eq!(resumed.progress().state(), BucketMigrationState::OwnershipTransferred);

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

#[test]
fn a_draining_cutover_needs_a_view_that_drops_the_source_shard() {
    let corpus = &*CORPUS;
    // The migrating bucket is the source shard's last bucket: the move
    // drains shard/0 out of the table entirely.
    let topology = drained_seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("drain");
    let (destination, destination_dir) = open_engine("drain-dest");

    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    drive_to_moved(&mut migration, &source, &destination);
    let moved = moved_table(&topology, corpus.bucket);

    // A next view that still owns the drained source shard refuses: the
    // seating rules give every owned range buckets.
    assert_eq!(
        migration.cut_over(&source, moved.clone(), raised_view()),
        Err(MigrationError::SeatingRefused(ShardingError::OwnedRangeWithoutBuckets {
            shard: shard(0),
        }))
    );
    // The Platform's draining view drops the source shard; the destination
    // keeps its raised epoch and the transfer seats.
    let draining_view = TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-alpha", "r-alpha", "r-alpha")
        .expect("valid member")
        .with_member("m-beta", "r-beta", "r-beta")
        .expect("valid member")
        .with_owner_at_epoch(&shard(1).as_range(), "m-beta", 2)
        .expect("valid owner")
        .validated()
        .expect("servable view");
    let outcome =
        migration.cut_over(&source, moved, draining_view).expect("draining cutover seats");
    assert_eq!(outcome.topology().shard_of(corpus.bucket), shard(1));
    assert_eq!(outcome.topology().table().shards(), vec![shard(1)]);

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

#[test]
fn cutover_refuses_records_in_flight_past_the_confirmed_point() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("window");
    let (destination, destination_dir) = open_engine("window-dest");
    source_fixture(&source);

    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    drive_to_moved(&mut migration, &source, &destination);

    // A write lands on the source bucket after the confirmation: the
    // in-flight window is not empty, so the cutover refuses and the
    // migration returns to moving (the bounded in-flight window rule).
    let scope_a = scope_of(&source, "tenant-a");
    put_at(&source, &scope_a, "op-late", &corpus.in_bucket[0]);
    assert!(matches!(
        migration.cut_over(&source, moved_table(&topology, corpus.bucket), raised_view()),
        Err(MigrationError::InFlightDelta { .. })
    ));
    assert_eq!(migration.progress().state(), BucketMigrationState::Moving);
    assert_eq!(migration.progress().confirmed_through(), None);

    // The delta streams, the move re-confirms, and the cutover then seats.
    drive_to_moved(&mut migration, &source, &destination);
    let outcome = migration
        .cut_over(&source, moved_table(&topology, corpus.bucket), raised_view())
        .expect("cutover seats after the delta drains");
    assert_eq!(outcome.topology().shard_of(corpus.bucket), shard(1));

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

#[test]
fn in_flight_queries_resolve_correctly_across_cutover() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("queries");
    let (destination, destination_dir) = open_engine("queries-dest");
    source_fixture(&source);

    let tenant = TenantId::try_new("tenant-a").expect("valid tenant");
    let collection = CollectionName::try_new("orders").expect("valid collection");

    // The query routed while the source still owned the bucket.
    let migrating_identity = identity(&corpus.in_bucket[0]);
    let migrating = RoutingRequest::by_identity(&tenant, &collection, &migrating_identity);
    let before = topology.route(&migrating).expect("routes before");
    assert_eq!(before.shard(), shard(0));
    // A query for a bucket the migration does not touch.
    let untouched_identity = identity(&corpus.out_of_bucket);
    let untouched = RoutingRequest::by_identity(&tenant, &collection, &untouched_identity);
    let untouched_before = topology.route(&untouched).expect("routes before");

    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    drive_to_moved(&mut migration, &source, &destination);
    let outcome = migration
        .cut_over(&source, moved_table(&topology, corpus.bucket), raised_view())
        .expect("cutover seats");
    let transferred = outcome.topology();

    // The in-flight placement for the migrated bucket is fenced with the
    // typed stale-placement refusal: it never resolves against the old owner.
    assert_eq!(
        transferred.authorize_placement(&before, &tenant),
        Err(ShardingError::PlacementStale {
            bucket: corpus.bucket,
            was_shard: shard(0),
            now_shard: shard(1),
        })
    );
    // Re-routing the same query resolves to the destination owner at the
    // raised epoch, and the fresh placement authorizes.
    let after = transferred.route(&migrating).expect("routes after");
    assert_eq!(after.shard(), shard(1));
    assert_eq!(
        after.owner(),
        topology.view().owner_of(&shard(1).as_range()).expect("destination owner")
    );
    assert_eq!(after.epoch(), 2);
    assert_eq!(transferred.authorize_placement(&after, &tenant), Ok(()));
    // The untouched bucket still routes to its owner, and because the
    // destination shard's epoch rose, its pre-cutover placement is fenced by
    // the epoch, never silently honored: every in-flight query on an
    // affected shard resolves with a typed outcome, and re-routing resolves
    // to the same owner at the new epoch.
    assert_eq!(
        transferred.authorize_placement(&untouched_before, &tenant),
        Err(ShardingError::PlacementEpochSuperseded { shard: shard(1), was: 1, now: 2 })
    );
    let untouched_after = transferred.route(&untouched).expect("routes after");
    assert_eq!(untouched_after.owner(), untouched_before.owner());
    assert_eq!(untouched_after.bucket(), untouched_before.bucket());
    assert_eq!(untouched_after.epoch(), 2);
    assert_eq!(transferred.authorize_placement(&untouched_after, &tenant), Ok(()));

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

#[test]
fn kill_points_recover_to_completion_or_clean_rollback() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (moved, raised) = (moved_table(&topology, corpus.bucket), raised_view());

    // K1: interrupted before any streaming.
    {
        let (source, source_dir) = open_engine("kill-1");
        let (destination, destination_dir) = open_engine("kill-1-dest");
        source_fixture(&source);
        drop(BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)));
        let mut recovered =
            BucketMigration::resume(&topology, corpus.bucket, shard(0), shard(1)).expect("resumes");
        drive_to_moved(&mut recovered, &source, &destination);
        let outcome =
            recovered.cut_over(&source, moved.clone(), raised.clone()).expect("completes");
        assert_eq!(outcome.topology().shard_of(corpus.bucket), shard(1));
        source.shutdown().expect("source flushes");
        destination.shutdown().expect("destination flushes");
        std::fs::remove_dir_all(source_dir).ok();
        std::fs::remove_dir_all(destination_dir).ok();
    }

    // K2: interrupted mid-streaming, then rolled back cleanly.
    {
        let (source, source_dir) = open_engine("kill-2");
        let (destination, destination_dir) = open_engine("kill-2-dest");
        source_fixture(&source);
        let destination_scope = scope_of(&destination, "tenant-a");
        let mut interrupted =
            BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
        interrupted.stream_round(&source, &destination, &destination_scope).expect("round runs");
        let rolled_back = interrupted.roll_back().expect("rolls back cleanly");
        assert_eq!(rolled_back.bucket(), corpus.bucket);
        assert_eq!(rolled_back.source(), shard(0));
        assert_eq!(rolled_back.destination(), shard(1));
        // Ownership never changed: the seated table is the original one.
        assert_eq!(topology.shard_of(corpus.bucket), shard(0));
        assert_eq!(interrupted.progress().state(), BucketMigrationState::RolledBack);
        // Every further step refuses, and a later migration of the same
        // bucket completes with the rolled-back records re-resolved
        // idempotently instead of double-applied.
        assert_eq!(
            interrupted.cut_over(&source, moved.clone(), raised.clone()),
            Err(MigrationError::StepRefused {
                state: BucketMigrationState::RolledBack,
                step: "cut_over",
            })
        );
        assert_eq!(
            interrupted.roll_back(),
            Err(MigrationError::StepRefused {
                state: BucketMigrationState::RolledBack,
                step: "roll_back",
            })
        );
        let mut fresh =
            BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
        drive_to_moved(&mut fresh, &source, &destination);
        let destination_scope = scope_of(&destination, "tenant-a");
        assert_eq!(documents_at(&destination, &destination_scope).len(), 3);
        let outcome = fresh.cut_over(&source, moved.clone(), raised.clone()).expect("completes");
        assert_eq!(outcome.topology().shard_of(corpus.bucket), shard(1));
        source.shutdown().expect("source flushes");
        destination.shutdown().expect("destination flushes");
        std::fs::remove_dir_all(source_dir).ok();
        std::fs::remove_dir_all(destination_dir).ok();
    }

    // K3: interrupted after the confirmation, before the cutover.
    {
        let (source, source_dir) = open_engine("kill-3");
        let (destination, destination_dir) = open_engine("kill-3-dest");
        source_fixture(&source);
        let mut interrupted =
            BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
        drive_to_moved(&mut interrupted, &source, &destination);
        drop(interrupted);
        let mut recovered =
            BucketMigration::resume(&topology, corpus.bucket, shard(0), shard(1)).expect("resumes");
        drive_to_moved(&mut recovered, &source, &destination);
        let outcome =
            recovered.cut_over(&source, moved.clone(), raised.clone()).expect("completes");
        assert_eq!(outcome.topology().shard_of(corpus.bucket), shard(1));
        source.shutdown().expect("source flushes");
        destination.shutdown().expect("destination flushes");
        std::fs::remove_dir_all(source_dir).ok();
        std::fs::remove_dir_all(destination_dir).ok();
    }

    // K4: interrupted after the cutover: the routing facts already seat the
    // transfer, so recovery is the idempotent replay.
    {
        let (source, source_dir) = open_engine("kill-4");
        let (destination, destination_dir) = open_engine("kill-4-dest");
        source_fixture(&source);
        let mut driven =
            BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
        drive_to_moved(&mut driven, &source, &destination);
        driven.cut_over(&source, moved.clone(), raised.clone()).expect("seats");
        drop(driven);
        let seated = ShardTopology::try_new(moved, raised).expect("seated topology");
        let recovered = BucketMigration::resume(&seated, corpus.bucket, shard(0), shard(1))
            .expect("resumes transferred");
        assert_eq!(recovered.progress().state(), BucketMigrationState::OwnershipTransferred);
        source.shutdown().expect("source flushes");
        destination.shutdown().expect("destination flushes");
        std::fs::remove_dir_all(source_dir).ok();
        std::fs::remove_dir_all(destination_dir).ok();
    }
}

#[test]
fn operation_identity_reuse_at_the_destination_fails_typed() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("reuse");
    let (destination, destination_dir) = open_engine("reuse-dest");
    source_fixture(&source);

    // The destination already committed `op-a1` against a different write
    // set: the operation ledger must refuse the streamed record, never
    // silently skip or double-apply it.
    let destination_scope = scope_of(&destination, "tenant-a");
    put_at(&destination, &destination_scope, "op-a1", &corpus.out_of_bucket);

    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    let failure = migration
        .stream_round(&source, &destination, &destination_scope)
        .expect_err("the reused identity refuses");
    assert!(matches!(
        failure,
        MigrationError::ApplyFailed {
            reason: crate::sharding::MigrationApplyFailure::Rejected(
                crate::engine::CommitError::OperationIdReuse
            ),
            ..
        }
    ));

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

#[test]
fn streaming_rounds_are_bounded_and_terminate() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("bound");
    let (destination, destination_dir) = open_engine("bound-dest");
    source_fixture(&source);

    // The streaming bound mirrors the replication plane's catch-up batch:
    // one round is never unbounded.
    assert_eq!(crate::sharding::MAX_MIGRATION_STREAM_BATCH, 1024);

    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    let destination_scope = scope_of(&destination, "tenant-a");
    let report =
        migration.stream_round(&source, &destination, &destination_scope).expect("round runs");
    // One round scanned the tenant's whole journal (it fits one bounded
    // batch) and resolved exactly the bucket's records; the out-of-bucket
    // record never resolved at the destination.
    assert_eq!(report.scanned(), journal_at(&source, "tenant-a").len());
    assert!(report.scanned() <= crate::sharding::MAX_MIGRATION_STREAM_BATCH as usize);
    assert_eq!(report.bucket_records(), 5);
    assert_eq!(report.applied(), 5);
    assert!(report.caught_up());

    // A further round on a caught-up tenant scans nothing new and still
    // reports caught up: the per-tenant drive terminates, it never loops.
    let next =
        migration.stream_round(&source, &destination, &destination_scope).expect("round runs");
    assert_eq!(next.scanned(), 0);
    assert_eq!(next.bucket_records(), 0);
    assert!(next.caught_up());

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

#[test]
fn delete_records_stream_and_replay_idempotently() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("delete");
    let (destination, destination_dir) = open_engine("delete-dest");

    // One put and one delete of the same bucket identity.
    let scope_a = scope_of(&source, "tenant-a");
    put_at(&source, &scope_a, "op-put", &corpus.in_bucket[0]);
    delete_at(&source, &scope_a, "op-delete", &corpus.in_bucket[0]);

    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    let destination_scope = scope_of(&destination, "tenant-a");
    let first =
        migration.stream_round(&source, &destination, &destination_scope).expect("round runs");
    assert_eq!(first.bucket_records(), 2);
    assert_eq!(first.applied(), 2);
    assert_eq!(documents_at(&destination, &destination_scope), Vec::<String>::new());

    // The same journal re-scanned resolves both records as duplicates.
    let mut rescan =
        BucketMigration::resume(&topology, corpus.bucket, shard(0), shard(1)).expect("resumes");
    let second =
        rescan.stream_round(&source, &destination, &destination_scope).expect("round runs");
    assert_eq!(second.applied(), 0);
    assert_eq!(second.duplicates_resolved(), 2);

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

/// Commits `count` replacements of one document at
/// [`LocalDurability::Written`]: the cheapest level that still journals
/// every publication, so each commit is exactly one journal entry for the
/// cutover's window scan to page past.
fn bulk_writes(engine: &Engine, scope: &TenantScope, name: &str, prefix: &str, count: usize) {
    let target = document(name);
    for index in 0..count {
        let operation =
            OperationId::try_new(&format!("{prefix}-{index}")).expect("valid operation");
        let snapshot = engine.read_snapshot(scope).expect("snapshot");
        let mut transaction = engine.begin(scope, snapshot, operation).expect("begin");
        transaction.put(target.clone(), ExpectedVersion::Any).expect("stage put");
        assert!(
            matches!(transaction.commit(LocalDurability::Written), CommitOutcome::Committed(_)),
            "bulk write {index} commits"
        );
    }
}

/// Drops the first `count` frames of the publication-history journal's
/// byte stream, aging it below the servable bound. The frames are private
/// to storage, but the framing — `u32` length, `u64` checksum, payload —
/// is exactly what a torn-tail scan walks, so removing whole leading
/// frames leaves a structurally valid journal whose servable window starts
/// later while the ledger keeps every published record.
fn age_journal_head(dir: &std::path::Path, count: usize) {
    let path = dir.join("history");
    let bytes = std::fs::read(&path).expect("journal reads");
    let mut offset = 0_usize;
    for _ in 0..count {
        let length =
            u32::from_le_bytes(bytes[offset..offset + 4].try_into().expect("frame length prefix"))
                as usize;
        offset += 4 + 8 + length;
    }
    std::fs::write(&path, &bytes[offset..]).expect("journal rewrites");
}

#[test]
fn confirmation_refuses_a_diverged_publication_journal() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("diverge-confirm");
    let (destination, destination_dir) = open_engine("diverge-confirm-dest");
    source_fixture(&source);

    // Every tenant is scanned to its journaled high water. The journal
    // append is deliberately best-effort (W4-CA-4 repair F2): one armed
    // failure publishes a bucket record the journal never records.
    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    drive_to_caught_up(&mut migration, &source, &destination);
    source.arm_storage_fault(crate::storage::FaultPoint::JournalAppend);
    let scope_a = scope_of(&source, "tenant-a");
    put_at(&source, &scope_a, "op-stranded", &corpus.in_bucket[0]);
    assert_eq!(source.journal_divergence().expect("divergence reads"), 1);
    assert!(
        !journal_at(&source, "tenant-a").contains(&"op-stranded".to_owned()),
        "the stranded record is published but not journaled"
    );

    // A journal-only completeness proof is blind to that record: the
    // confirmation must refuse with the typed divergence instead of
    // declaring the move complete while the record strands on the source.
    assert_eq!(
        migration.confirm_moved(&source),
        Err(MigrationError::JournalDivergence { unjournaled: 1 })
    );
    assert_eq!(migration.progress().state(), BucketMigrationState::Moving);
    assert_eq!(migration.progress().confirmed_through(), None);

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

#[test]
fn cutover_refuses_a_diverged_publication_journal() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("diverge-cutover");
    let (destination, destination_dir) = open_engine("diverge-cutover-dest");
    source_fixture(&source);

    // The move confirmed against a clean journal, then a bucket record
    // published past the confirmed point without journaling: the delta
    // re-check's pages cannot see it, so only the divergence check can
    // keep the cutover from seating a transfer that strands the record.
    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    drive_to_moved(&mut migration, &source, &destination);
    source.arm_storage_fault(crate::storage::FaultPoint::JournalAppend);
    let scope_a = scope_of(&source, "tenant-a");
    put_at(&source, &scope_a, "op-stranded", &corpus.in_bucket[0]);
    assert_eq!(source.journal_divergence().expect("divergence reads"), 1);

    assert_eq!(
        migration.cut_over(&source, moved_table(&topology, corpus.bucket), raised_view()),
        Err(MigrationError::JournalDivergence { unjournaled: 1 })
    );
    assert_eq!(migration.progress().state(), BucketMigrationState::Moving);
    assert_eq!(migration.progress().confirmed_through(), None);

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

#[test]
fn co_tenant_width_does_not_starve_a_quiesced_buckets_cutover() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("co-tenant");
    let (destination, destination_dir) = open_engine("co-tenant-dest");
    source_fixture(&source);

    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    drive_to_moved(&mut migration, &source, &destination);

    // Sustained co-tenant writes after the confirmation: more than one
    // bounded page for a third tenant that never touches the migrating
    // bucket, plus one out-of-bucket record for the migrating tenant. The
    // Platform boundary quiesces the migrating bucket only (PB-5039), so
    // none of this width may refuse the cutover: it is scan cost, never
    // an in-flight delta of the bucket.
    let scope_c = scope_of(&source, "tenant-c");
    bulk_writes(&source, &scope_c, &corpus.out_of_bucket, "op-co", 1025);
    let scope_a = scope_of(&source, "tenant-a");
    put_at(&source, &scope_a, "op-foreign-late", &corpus.out_of_bucket);

    let outcome = migration
        .cut_over(&source, moved_table(&topology, corpus.bucket), raised_view())
        .expect("co-tenant width never starves a quiesced bucket's cutover");
    assert!(!outcome.replayed());
    assert_eq!(outcome.topology().shard_of(corpus.bucket), shard(1));
    assert_eq!(migration.progress().state(), BucketMigrationState::OwnershipTransferred);

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

#[test]
fn a_bucket_bearing_delta_wider_than_one_page_refuses_and_returns_to_moving() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("width");
    let (destination, destination_dir) = open_engine("width-dest");
    source_fixture(&source);

    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    drive_to_moved(&mut migration, &source, &destination);
    let confirmed = migration.progress().confirmed_through().expect("confirmed");

    // More than one bounded page of unstreamed bucket records past the
    // confirmed point: the first page alone cannot prove the window empty,
    // so the cutover refuses with the page's own last sequence and the
    // migration returns to moving (the drain-then-seat convergence is the
    // in-flight test's story above).
    let scope_a = scope_of(&source, "tenant-a");
    bulk_writes(&source, &scope_a, &corpus.in_bucket[0], "op-wide", 1025);
    let tenant_a = TenantId::try_new("tenant-a").expect("valid tenant");
    let page = source
        .tenant_commits(&tenant_a, confirmed, crate::sharding::MAX_MIGRATION_STREAM_BATCH)
        .expect("page reads");
    assert_eq!(
        page.len(),
        crate::sharding::MAX_MIGRATION_STREAM_BATCH as usize,
        "the delta past the confirmed point is wider than one bounded page"
    );
    let page_last = page.last().expect("a full page holds records").sequence();
    assert_eq!(
        migration.cut_over(&source, moved_table(&topology, corpus.bucket), raised_view()),
        Err(MigrationError::InFlightDelta { past: page_last })
    );
    assert_eq!(migration.progress().state(), BucketMigrationState::Moving);
    assert_eq!(migration.progress().confirmed_through(), None);

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}

#[test]
fn an_aged_journal_falls_back_to_a_full_rescan_of_duplicates() {
    let corpus = &*CORPUS;
    let topology = seating_for(corpus.bucket);
    let (source, source_dir) = open_engine("aged");
    let (destination, destination_dir) = open_engine("aged-dest");

    // Three bucket records, streamed to caught up and confirmed.
    let scope_a = scope_of(&source, "tenant-a");
    for name in &corpus.in_bucket[0..3] {
        let operation = format!("op-{}", name.strip_prefix("mig/").unwrap_or(name));
        put_at(&source, &scope_a, &operation, name);
    }
    let mut migration =
        BucketMigration::try_new(&topology, corpus.bucket, shard(0), shard(1)).expect("starts");
    drive_to_moved(&mut migration, &source, &destination);

    // Age the source journal below the servable bound: the first commit
    // frame is gone, so the reopened journal cannot serve below sequence
    // two and a resumed drive must fall back to a full re-scan instead of
    // silently skipping the gap.
    source.shutdown().expect("source flushes");
    age_journal_head(&source_dir, 2);
    let source = reopen_engine(&source_dir);
    assert_eq!(
        source.servable_history_start().expect("servable bound"),
        Some(StorageSequence::new(2)),
        "the aged journal serves only from the second record"
    );

    let mut resumed = BucketMigration::resume(&topology, corpus.bucket, shard(0), shard(1))
        .expect("resumes after aging");
    let destination_scope = scope_of(&destination, "tenant-a");
    let report =
        resumed.stream_round(&source, &destination, &destination_scope).expect("round runs");
    assert!(report.fell_back_to_full_scan(), "the advisory cursor fell below the servable bound");
    assert_eq!(report.applied(), 0);
    assert_eq!(
        report.duplicates_resolved(),
        2,
        "the re-scan resolves duplicates, never re-applies"
    );

    // The aged record is published but unjournaled: the completeness
    // proof fails closed on the divergence instead of confirming a move
    // whose first record the journal can no longer serve.
    assert_eq!(
        resumed.confirm_moved(&source),
        Err(MigrationError::JournalDivergence { unjournaled: 1 })
    );

    source.shutdown().expect("source flushes");
    destination.shutdown().expect("destination flushes");
    std::fs::remove_dir_all(source_dir).ok();
    std::fs::remove_dir_all(destination_dir).ok();
}
