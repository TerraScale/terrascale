//! The WAL-frame follower's gates (S-05 slice 2b, D-35/D-45): the
//! intra-region RF=2 copy is a physical follower — committed WAL frames
//! ship verbatim, the follower validates each frame's cumulative checksum,
//! appends to its own `-wal`, `fdatasync`s, and acks; promotion opens the
//! follower's location under a new replica epoch with byte-identical rows.
//!
//! Stock SQLite restarts the WAL in place after every fully-checkpointed
//! wave (the leader's between-waves passive checkpoint, then the next
//! wave's first write: salt-1 increments, frames overwrite from frame
//! zero), so generations rotate per commit wave and the steady state is
//! ship → ack → fold → adopt: the follower folds each generation with the
//! writer's own checkpoint and adopts the next one from frame zero, never
//! re-copying the database. Losing the per-wave race heals through
//! re-bootstrap.
//!
//! The QUIC socket adapter is the S-09 member-composition step; the ship
//! here is the in-process handoff ([`WalFrames`]) so these gates pin the
//! follower semantics the adapter will drive.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use terrabase::engine::CommitOutcome;
use terrabase::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion as EngineVersion,
    LocalDurability, OperationId, TenantId,
};
use terrabase_tbep::wal_follower::{
    FollowerAck, WalFollower, WalFrames, WalHeader, WalTail, WalTailStep,
};
use terrabase_tbep::{MemberHost, MemberHostOptions};

static NEXT_LOCATION: AtomicU64 = AtomicU64::new(1);

struct TestLocation(PathBuf);

impl TestLocation {
    fn new(label: &str) -> Self {
        let number = NEXT_LOCATION.fetch_add(1, Ordering::Relaxed);
        Self(
            std::env::temp_dir()
                .join(format!("terrabase-s05-follower-{label}-{}-{number}", std::process::id())),
        )
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TestLocation {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// One region's writer: its own durable location, the shared tenant.
fn member(label: &str) -> (TestLocation, MemberHost) {
    let location = TestLocation::new(label);
    let host = MemberHost::open(
        location.path(),
        7,
        "tenant-a",
        "project-a",
        "database-a",
        MemberHostOptions::tb1a(),
    )
    .expect("the member host opens");
    (location, host)
}

/// Promotion is opening the follower's directory under a new replica
/// epoch: the engine's recovery validates every appended frame and serves
/// the file.
fn promote(location: &TestLocation) -> MemberHost {
    MemberHost::open(
        location.path(),
        7,
        "tenant-a",
        "project-a",
        "database-a",
        MemberHostOptions::tb1a(),
    )
    .expect("the follower location promotes")
}

/// Writes one origin document through the member's engine, fsynced. The
/// commit returns only after the leader's between-waves passive checkpoint,
/// so the next write restarts the WAL: each `put` is one generation.
fn put(host: &MemberHost, id: &str, value: i64) {
    let engine = host.engine();
    let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
    let engine_scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&engine_scope).expect("the engine serves a snapshot");
    let operation =
        OperationId::try_new(&format!("put-{id}-{value}")).expect("the operation identity parses");
    let mut transaction =
        engine.begin(&engine_scope, snapshot, operation).expect("the write begins");
    transaction
        .put(
            Document::new(
                DocumentId::try_new(id).expect("the document id parses"),
                CollectionName::try_new("orders").expect("the collection parses"),
                DocumentValue::Object(BTreeMap::from([(
                    "value".to_owned(),
                    DocumentValue::Integer(value),
                )])),
            ),
            EngineVersion::Any,
        )
        .expect("the put stages");
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        outcome => panic!("the write committed: {outcome:?}"),
    }
}

/// The member's live rows as sorted `(id, value)` pairs.
fn rows(host: &MemberHost) -> Vec<(String, String)> {
    let engine = host.engine();
    let tenant = TenantId::try_new("tenant-a").expect("the tenant parses");
    let engine_scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&engine_scope).expect("the engine serves a snapshot");
    let mut rows: Vec<(String, String)> = engine
        .snapshot_documents_in_collection(
            &engine_scope,
            &snapshot,
            &CollectionName::try_new("orders").expect("the collection parses"),
        )
        .expect("the collection scan serves")
        .iter()
        .map(|document| (document.id().as_str().to_owned(), format!("{:?}", document.value())))
        .collect();
    rows.sort();
    rows
}

/// Adopts the wave the test just wrote: the tail positioned at the old
/// generation must see the wave boundary (each commit restarts the WAL),
/// the follower folds the acked generation, and a fresh tail ships the new
/// generation from frame zero for the follower to apply and ack.
fn adopt_wave(
    writer_location: &Path,
    follower: &mut WalFollower,
    old_tail: &mut WalTail,
) -> (WalFrames, FollowerAck) {
    assert_eq!(
        old_tail.poll().expect("the boundary polls"),
        WalTailStep::GenerationChanged,
        "each commit wave restarts the WAL generation"
    );
    follower.fold_generation().expect("the follower folds the acked generation");
    assert_eq!(
        follower.committed_through(),
        None,
        "the fold resets the follower past the old generation"
    );
    let mut fresh = WalTail::new(writer_location);
    let batch = match fresh.poll().expect("the fresh tail polls") {
        WalTailStep::Frames(batch) => *batch,
        step => panic!("the fresh tail shipped the new generation: {step:?}"),
    };
    assert_eq!(batch.from_frame, 0, "a new generation ships from frame zero");
    let ack = follower.apply(&batch).expect("the follower adopts the new generation");
    assert!(ack.fdatasynced, "the ack means the fdatasync completed");
    assert!(ack.committed_through.is_some(), "the ack carries the durable prefix");
    (batch, ack)
}

/// The RF=2 round trip across two commit waves: bootstrap copies the
/// snapshot, the tail ships the next wave, the follower appends with an
/// `fdatasync` ack, folds at the wave boundary, adopts the following wave,
/// and promotion serves byte-identical rows under a new epoch.
#[test]
fn ship_apply_ack_fold_adopt_and_promotion_serves_identical_rows() {
    let (writer_location, writer) = member("roundtrip-writer");
    let follower_location = TestLocation::new("roundtrip-follower");
    put(&writer, "order-a", 1);
    put(&writer, "order-b", 2);

    let (mut follower, checkpoint) =
        WalFollower::bootstrap(writer_location.path(), follower_location.path())
            .expect("the follower bootstraps");
    assert!(follower.committed_through().is_some(), "the snapshot carries durable frames");

    let mut tail = WalTail::resume(writer_location.path(), checkpoint);
    assert_eq!(
        tail.poll().expect("the tail polls"),
        WalTailStep::UpToDate,
        "the snapshot covered every committed frame"
    );

    put(&writer, "order-c", 3);
    let (_batch, _ack) = adopt_wave(writer_location.path(), &mut follower, &mut tail);

    let promoted = promote(&follower_location);
    assert_eq!(rows(&promoted), rows(&writer), "promotion serves the writer's rows");
    assert_eq!(rows(&promoted).len(), 3, "every write survived the round trip");
}

/// A lost acknowledgement re-ships what the follower already holds: the
/// duplicate re-acks the durable state without touching the file — no
/// double-append, no new durability claim.
#[test]
fn duplicate_delivery_re_acks_without_appending() {
    let (writer_location, writer) = member("duplicate-writer");
    let follower_location = TestLocation::new("duplicate-follower");
    put(&writer, "order-a", 1);
    put(&writer, "order-b", 2);

    let (mut follower, checkpoint) =
        WalFollower::bootstrap(writer_location.path(), follower_location.path())
            .expect("the follower bootstraps");
    let mut tail = WalTail::resume(writer_location.path(), checkpoint);
    assert_eq!(tail.poll().expect("the tail polls"), WalTailStep::UpToDate);

    put(&writer, "order-c", 3);
    let (batch, first) = adopt_wave(writer_location.path(), &mut follower, &mut tail);
    let wal_len = std::fs::metadata(follower_location.path().join("terrabase.sqlite3-wal"))
        .expect("the follower WAL exists")
        .len();
    let second = follower.apply(&batch).expect("the duplicate re-acks");
    assert_eq!(first, second, "the duplicate ack equals the first ack");
    assert_eq!(
        std::fs::metadata(follower_location.path().join("terrabase.sqlite3-wal"))
            .expect("the follower WAL exists")
            .len(),
        wal_len,
        "the duplicate appends nothing"
    );
    let promoted = promote(&follower_location);
    assert_eq!(rows(&promoted).len(), 3, "the rows hold exactly once");
}

/// Validation fails closed: a bit-flipped frame, a tampered header, a
/// batch of another writer's generation, and a non-contiguous batch are
/// all refused with the durable prefix untouched.
#[test]
fn corrupt_foreign_and_gapped_batches_fail_closed() {
    let (writer_location, writer) = member("closed-writer");
    let (other_location, other) = member("closed-other");
    let follower_location = TestLocation::new("closed-follower");
    put(&writer, "order-a", 1);
    put(&writer, "order-b", 2);
    put(&other, "order-x", 9);

    let (mut follower, checkpoint) =
        WalFollower::bootstrap(writer_location.path(), follower_location.path())
            .expect("the follower bootstraps");
    let mut tail = WalTail::resume(writer_location.path(), checkpoint);
    assert_eq!(tail.poll().expect("the tail polls"), WalTailStep::UpToDate);

    put(&writer, "order-c", 3);
    assert_eq!(
        tail.poll().expect("the boundary polls"),
        WalTailStep::GenerationChanged,
        "each commit wave restarts the WAL generation"
    );
    follower.fold_generation().expect("the follower folds the acked generation");
    let mut fresh = WalTail::new(writer_location.path());
    let batch = match fresh.poll().expect("the fresh tail polls") {
        WalTailStep::Frames(batch) => *batch,
        step => panic!("the fresh tail shipped the new generation: {step:?}"),
    };

    let mut torn = batch.bytes.clone();
    let middle = torn.len() / 2;
    torn[middle] ^= 0xff;
    let corrupt = WalFrames {
        header: batch.header.clone(),
        from_frame: batch.from_frame,
        bytes: torn,
        committed_through: batch.committed_through,
    };
    assert!(follower.apply(&corrupt).is_err(), "a bit-flipped frame is refused");

    let mut tampered = batch.header.clone();
    let last = tampered.len() - 1;
    tampered[last] ^= 0xff;
    let bad_header = WalFrames {
        header: tampered,
        from_frame: batch.from_frame,
        bytes: batch.bytes.clone(),
        committed_through: batch.committed_through,
    };
    assert!(follower.apply(&bad_header).is_err(), "a tampered header is refused");
    assert_eq!(follower.committed_through(), None, "no refusal moved the durable prefix");

    let ack = follower.apply(&batch).expect("the follower adopts the new generation");
    assert!(ack.fdatasynced, "the ack means the fdatasync completed");

    let mut foreign_tail = WalTail::new(other_location.path());
    let foreign = match foreign_tail.poll().expect("the foreign tail polls") {
        WalTailStep::Frames(batch) => *batch,
        step => panic!("the foreign tail shipped its generation: {step:?}"),
    };
    assert!(follower.apply(&foreign).is_err(), "a batch of another generation is refused");

    let gap = WalFrames {
        header: batch.header.clone(),
        from_frame: 99,
        bytes: Vec::new(),
        committed_through: None,
    };
    assert!(follower.apply(&gap).is_err(), "a batch past the follower's prefix is refused");
    assert_eq!(
        follower.committed_through(),
        ack.committed_through,
        "no refusal moved the durable prefix"
    );

    let promoted = promote(&follower_location);
    assert_eq!(rows(&promoted).len(), 3, "every committed row survives the refusals");
}

/// Losing the per-wave race heals through re-bootstrap, losing nothing:
/// waves the tail never shipped were folded into the writer's database
/// file, so the snapshot carries them and the tail resumes from the live
/// generation.
#[test]
fn missed_generations_heal_through_rebootstrap() {
    let (writer_location, writer) = member("heal-writer");
    let follower_location = TestLocation::new("heal-follower");
    put(&writer, "order-a", 1);
    put(&writer, "order-b", 2);
    put(&writer, "order-c", 3);

    let (follower, checkpoint) =
        WalFollower::bootstrap(writer_location.path(), follower_location.path())
            .expect("the follower bootstraps from the live generation");
    assert!(follower.committed_through().is_some(), "the snapshot carries durable frames");
    let mut tail = WalTail::resume(writer_location.path(), checkpoint);
    assert_eq!(
        tail.poll().expect("the tail polls"),
        WalTailStep::UpToDate,
        "the resumed tail continues past the snapshot"
    );

    let promoted = promote(&follower_location);
    assert_eq!(rows(&promoted).len(), 3, "the snapshot healed every missed wave");
}

/// The seed generation bootstraps and promotes empty: a fresh writer's
/// open-time writes carry no documents, and the resumed tail is current.
#[test]
fn seed_generation_bootstraps_and_promotes_empty() {
    let (writer_location, _writer) = member("seed-writer");
    let follower_location = TestLocation::new("seed-follower");

    let (_follower, checkpoint) =
        WalFollower::bootstrap(writer_location.path(), follower_location.path())
            .expect("the follower bootstraps the seed generation");
    let mut tail = WalTail::resume(writer_location.path(), checkpoint);
    assert_eq!(tail.poll().expect("the tail polls"), WalTailStep::UpToDate);

    let promoted = promote(&follower_location);
    assert!(rows(&promoted).is_empty(), "no documents exist yet");
}

/// The header parser rejects garbage before any file is touched.
#[test]
fn wal_header_rejects_garbage() {
    assert!(matches!(WalHeader::parse(&[0u8; 10]), Err(_)), "a truncated header is refused");
    assert!(matches!(WalHeader::parse(&[0x99u8; 32]), Err(_)), "a non-SQLite magic is refused");
}
