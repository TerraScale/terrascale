//! The WAL-frame follower's gates (S-05 slice 2b, D-35/D-45): the
//! intra-region RF=2 copy is a physical follower — committed WAL frames
//! ship verbatim, the follower validates each frame's cumulative checksum,
//! appends to its own `-wal`, `fdatasync`s, and acks; promotion opens the
//! follower's location under a new replica epoch with byte-identical rows.
//!
//! Stock `SQLite` restarts the WAL in place after every fully-checkpointed
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
    fresh.acknowledge(ack).expect("the writer accepts the durable receipt");
    *old_tail = fresh;
    (batch, ack)
}

/// A lost delivery never advances the restart position, and a lost receipt
/// replays the exact bytes even when the writer has overwritten its WAL.
#[test]
fn tail_retries_until_matching_durable_acknowledgment() {
    let (writer_location, writer) = member("ack-writer");
    let follower_location = TestLocation::new("ack-follower");
    put(&writer, "order-a", 1);
    let (mut follower, _) =
        WalFollower::bootstrap(writer_location.path(), follower_location.path())
            .expect("bootstrap");
    let mut seed = WalTail::new(writer_location.path());
    let WalTailStep::Frames(seed_batch) = seed.poll().expect("seed batch") else {
        panic!("seed must contain a commit");
    };
    let stale_ack = follower.apply(&seed_batch).expect("seed acknowledgment");
    follower.fold_generation().expect("fold seed");
    put(&writer, "order-b", 2);
    let mut tail = WalTail::new(writer_location.path());
    let pending = tail.poll().expect("new batch");
    let WalTailStep::Frames(ref batch) = pending else { panic!("new committed frames") };
    assert!(tail.acknowledge(stale_ack).is_err(), "an old generation cannot acknowledge this one");
    let mut restarted = WalTail::resume(writer_location.path(), tail.checkpoint());
    assert_eq!(
        restarted.poll().expect("retry after restart"),
        pending,
        "reading a batch does not checkpoint its delivery"
    );
    let ack = follower.apply(batch).expect("durable delivery");
    let mut not_durable = ack;
    not_durable.fdatasynced = false;
    assert!(tail.acknowledge(not_durable).is_err());
    assert_eq!(tail.poll().expect("retry after lost acknowledgment"), pending);
    writer
        .engine()
        .with_quiescent_wal(|location| {
            let checkpoint_connection =
                rusqlite::Connection::open(location.join("terrabase.sqlite3"))
                    .expect("checkpoint connection");
            let busy: i64 = checkpoint_connection
                .query_row("PRAGMA wal_checkpoint(TRUNCATE)", [], |row| row.get(0))
                .expect("force writer WAL reuse");
            assert_eq!(busy, 0);
        })
        .expect("exclusive checkpoint barrier");
    put(&writer, "order-c", 3);
    assert_eq!(
        tail.poll().expect("retained batch"),
        pending,
        "the pending bytes survive writer WAL reuse"
    );
    assert_eq!(follower.apply(batch).expect("duplicate delivery"), ack);
    tail.acknowledge(ack).expect("matching receipt");
    assert_eq!(tail.poll().expect("next generation"), WalTailStep::GenerationChanged);
    let mut resumed = WalTail::resume(writer_location.path(), tail.checkpoint());
    assert_eq!(resumed.poll().expect("resume confirmed prefix"), WalTailStep::GenerationChanged);
    assert!(tail.acknowledge(ack).is_err(), "there is no pending batch now");
    assert_eq!(
        rows(&promote(&follower_location)).len(),
        2,
        "promotion contains exactly the acknowledged writes"
    );
}

/// `SQLite` can spill valid WAL frames before commit and rewrite them after rollback.
#[test]
fn tail_holds_uncommitted_spills_until_a_complete_commit() {
    let writer_location = TestLocation::new("spill-writer");
    std::fs::create_dir(writer_location.path()).expect("writer directory");
    let mut connection =
        rusqlite::Connection::open(writer_location.path().join("terrabase.sqlite3"))
            .expect("SQLite writer");
    connection.execute_batch(
        "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;          PRAGMA cache_size=1; PRAGMA cache_spill=ON; CREATE TABLE items (value BLOB);",
    ).expect("spilling WAL setup");
    let follower_location = TestLocation::new("spill-follower");
    let (mut follower, checkpoint) =
        WalFollower::bootstrap(writer_location.path(), follower_location.path())
            .expect("bootstrap");
    let mut tail = WalTail::resume(writer_location.path(), checkpoint);
    let wal_path = writer_location.path().join("terrabase.sqlite3-wal");
    let before = std::fs::metadata(&wal_path).expect("initial WAL").len();
    let transaction = connection.transaction().expect("transaction");
    transaction.execute("INSERT INTO items VALUES (zeroblob(131072))", []).expect("spill");
    assert!(
        std::fs::metadata(&wal_path).expect("spilled WAL").len() > before,
        "the transaction wrote WAL frames before commit"
    );
    assert_eq!(tail.poll().expect("uncommitted spill"), WalTailStep::UpToDate);
    transaction.rollback().expect("rollback spilled frames");
    assert_eq!(tail.poll().expect("rolled-back spill"), WalTailStep::UpToDate);
    connection.execute("INSERT INTO items VALUES (zeroblob(8))", []).expect("commit replacement");
    let WalTailStep::Frames(batch) = tail.poll().expect("committed replacement") else {
        panic!("the replacement commit must ship");
    };
    let ack = follower.apply(&batch).expect("apply rewritten committed prefix");
    tail.acknowledge(ack).expect("acknowledge replacement");
    follower.fold_generation().expect("fold");
    let promoted = rusqlite::Connection::open(follower_location.path().join("terrabase.sqlite3"))
        .expect("promoted SQLite");
    let bytes: i64 = promoted
        .query_row("SELECT length(value) FROM items", [], |row| row.get(0))
        .expect("committed row");
    assert_eq!(bytes, 8, "rolled-back contents never reached the follower");
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

/// A restarted follower reconstructs its durable receipt and removes a torn suffix.
#[test]
fn follower_restart_recovers_receipts_and_trims_partial_frame() {
    let (writer_location, writer) = member("restart-writer");
    put(&writer, "order-a", 1);
    let follower_location = TestLocation::new("restart-follower");
    let (mut follower, _) =
        WalFollower::bootstrap(writer_location.path(), follower_location.path())
            .expect("bootstrap");
    let mut tail = WalTail::new(writer_location.path());
    let WalTailStep::Frames(batch) = tail.poll().expect("batch") else { panic!("commit") };
    let receipt = follower.apply(&batch).expect("durable receipt before restart");
    drop(follower);
    let (mut reopened, checkpoint) =
        WalFollower::open_existing(follower_location.path()).expect("recover follower");
    assert_eq!(reopened.apply(&batch).expect("receipt after restart"), receipt);
    let mut resumed = WalTail::resume(writer_location.path(), checkpoint);
    assert_eq!(resumed.poll().expect("resume writer"), WalTailStep::UpToDate);
    drop(reopened);
    let wal_path = follower_location.path().join("terrabase.sqlite3-wal");
    let valid = std::fs::read(&wal_path).expect("valid WAL");
    let mut torn = valid.clone();
    torn.extend_from_slice(b"torn-frame");
    std::fs::write(&wal_path, torn).expect("inject partial write");
    let (mut recovered, _) =
        WalFollower::open_existing(follower_location.path()).expect("recover partial suffix");
    assert_eq!(std::fs::read(&wal_path).expect("trimmed WAL"), valid);
    assert_eq!(recovered.apply(&batch).expect("recovered receipt"), receipt);
    recovered.fold_generation().expect("fold recovered replica");
    drop(recovered);
    let (_folded, _) =
        WalFollower::open_existing(follower_location.path()).expect("recover folded replica");
    assert_eq!(rows(&promote(&follower_location)).len(), 1);
}

/// Folding never silently adopts an unacknowledged suffix or discards corrupt frames.
#[test]
fn fold_requires_the_exact_acknowledged_wal_on_disk() {
    let (_writer_location, writer) = member("fold-prefix-writer");
    put(&writer, "order-a", 1);
    let follower_location = TestLocation::new("fold-prefix-follower");
    let (mut follower, _) =
        WalFollower::bootstrap_engine(writer.engine(), follower_location.path())
            .expect("bootstrap");
    let acknowledged = follower.committed_through();
    let wal_path = follower_location.path().join("terrabase.sqlite3-wal");
    let valid = std::fs::read(&wal_path).expect("acknowledged WAL");
    let mut extra = valid.clone();
    extra.extend_from_slice(b"unacknowledged-partial-append");
    std::fs::write(&wal_path, &extra).expect("inject incomplete append");
    assert!(follower.fold_generation().is_err());
    assert_eq!(std::fs::read(&wal_path).expect("preserved suffix"), extra);
    assert_eq!(follower.committed_through(), acknowledged);
    let mut corrupt = valid.clone();
    corrupt[64] ^= 0xff;
    std::fs::write(&wal_path, &corrupt).expect("inject complete-frame corruption");
    assert!(follower.fold_generation().is_err());
    assert_eq!(std::fs::read(&wal_path).expect("preserved corruption"), corrupt);
    assert_eq!(follower.committed_through(), acknowledged);
    std::fs::write(&wal_path, valid).expect("repair from captured bytes");
    follower.fold_generation().expect("verified generation folds");
    assert_eq!(rows(&promote(&follower_location)).len(), 1);
}

/// Corruption in a complete frame is refused without rewriting the replica.
#[test]
fn follower_restart_preserves_corrupt_frames_for_repair() {
    let (writer_location, writer) = member("corrupt-restart-writer");
    put(&writer, "order-a", 1);
    let follower_location = TestLocation::new("corrupt-restart-follower");
    let (follower, _) = WalFollower::bootstrap(writer_location.path(), follower_location.path())
        .expect("bootstrap");
    drop(follower);
    let wal_path = follower_location.path().join("terrabase.sqlite3-wal");
    let mut corrupt = std::fs::read(&wal_path).expect("WAL");
    corrupt[64] ^= 0xff;
    std::fs::write(&wal_path, &corrupt).expect("inject corrupt frame");
    assert!(WalFollower::open_existing(follower_location.path()).is_err());
    assert_eq!(std::fs::read(&wal_path).expect("preserved evidence"), corrupt);
    let absent = TestLocation::new("absent-restart-follower");
    assert!(WalFollower::open_existing(absent.path()).is_err());
    assert!(!absent.path().exists(), "recovery does not create an empty replica");
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
    let follower_wal = follower_location.path().join("terrabase.sqlite3-wal");
    let before = std::fs::read(&follower_wal).ok();
    assert!(follower.apply(&corrupt).is_err(), "a bit-flipped frame is refused");
    assert_eq!(std::fs::read(&follower_wal).ok(), before, "invalid adoption never writes a header");
    let wrong_commit = WalFrames {
        header: batch.header.clone(),
        from_frame: batch.from_frame,
        bytes: batch.bytes.clone(),
        committed_through: None,
    };
    assert!(follower.apply(&wrong_commit).is_err(), "adoption verifies commit metadata");
    assert_eq!(std::fs::read(&follower_wal).ok(), before);

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
    assert_eq!(follower.apply(&batch).expect("identical retry"), ack);
    assert!(follower.apply(&corrupt).is_err(), "a corrupt retransmission is refused");
    let overflow = WalFrames {
        header: batch.header.clone(),
        from_frame: usize::MAX,
        bytes: batch.bytes.clone(),
        committed_through: None,
    };
    assert!(follower.apply(&overflow).is_err(), "overflow is refused without panic");
    assert!(follower.apply(&wrong_commit).is_err(), "retry commit metadata must match frames");

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

/// A live-engine capture excludes concurrent commits until its snapshot is complete.
#[test]
fn engine_capture_keeps_snapshot_stable_during_concurrent_write() {
    let (writer_location, writer) = member("capture-writer");
    put(&writer, "order-a", 1);
    let follower_location = TestLocation::new("capture-follower");
    let (started_tx, started_rx) = std::sync::mpsc::channel();
    let (done_tx, done_rx) = std::sync::mpsc::channel();
    std::thread::scope(|threads| {
        writer
            .engine()
            .with_quiescent_wal(|location| {
                assert_eq!(location, writer_location.path());
                let before = std::fs::read(location.join("terrabase.sqlite3-wal")).expect("WAL");
                threads.spawn(|| {
                    started_tx.send(()).expect("writer starts");
                    put(&writer, "order-b", 2);
                    done_tx.send(()).expect("writer completes");
                });
                started_rx.recv().expect("concurrent writer scheduled");
                assert!(
                    matches!(
                        done_rx.recv_timeout(std::time::Duration::from_millis(50)),
                        Err(std::sync::mpsc::RecvTimeoutError::Timeout)
                    ),
                    "commit waits for capture"
                );
                assert_eq!(
                    std::fs::read(location.join("terrabase.sqlite3-wal")).expect("stable WAL"),
                    before
                );
                WalFollower::bootstrap(location, follower_location.path())
                    .expect("quiescent snapshot")
            })
            .expect("engine capture barrier");
    });
    done_rx.recv().expect("write completed after capture");
    assert_eq!(rows(&writer).len(), 2);
    assert_eq!(rows(&promote(&follower_location)).len(), 1, "snapshot predates blocked write");
}

/// Live polling binds the capture barrier to the database whose WAL is tailed.
#[test]
fn live_poll_rejects_wrong_engine_and_preserves_pending_delivery() {
    let (location, writer) = member("live-poll-writer");
    let (_other_location, other) = member("live-poll-other");
    put(&writer, "order-a", 1);
    let mut tail = WalTail::new(location.path());
    assert!(tail.poll_engine(other.engine()).is_err());
    let pending = tail.poll_engine(writer.engine()).expect("live capture");
    assert!(matches!(pending, WalTailStep::Frames(_)), "the rejected capture skipped no commit");
    assert!(tail.poll_engine(other.engine()).is_err());
    assert_eq!(tail.poll_engine(writer.engine()).expect("pending retry"), pending);
    let follower_location = TestLocation::new("live-poll-follower");
    let (mut follower, _) =
        WalFollower::bootstrap_engine(writer.engine(), follower_location.path())
            .expect("live bootstrap");
    let WalTailStep::Frames(batch) = pending else { unreachable!() };
    tail.acknowledge(follower.apply(&batch).expect("durable delivery")).expect("receipt");
    assert_eq!(tail.poll_engine(writer.engine()).expect("caught up"), WalTailStep::UpToDate);
}

/// A failed bootstrap cannot truncate its writer or overwrite an existing replica.
#[test]
fn bootstrap_preserves_occupied_destinations_and_writer() {
    let (writer_location, writer) = member("protected-writer");
    put(&writer, "order-a", 1);
    writer
        .engine()
        .with_quiescent_wal(|location| {
            let database = location.join("terrabase.sqlite3");
            let before = std::fs::read(&database).expect("writer database");
            assert!(WalFollower::bootstrap(location, location).is_err());
            assert_eq!(std::fs::read(&database).expect("writer survives"), before);
        })
        .expect("exclude normal background checkpoints from byte comparison");
    assert_eq!(rows(&writer).len(), 1);

    let follower_location = TestLocation::new("protected-follower");
    std::fs::create_dir(follower_location.path()).expect("empty destination");
    let (follower, _) = WalFollower::bootstrap_engine(writer.engine(), follower_location.path())
        .expect("first snapshot");
    let snapshot = std::fs::read(follower_location.path().join("terrabase.sqlite3"))
        .expect("follower database");
    assert!(WalFollower::bootstrap(writer_location.path(), follower_location.path()).is_err());
    assert_eq!(
        std::fs::read(follower_location.path().join("terrabase.sqlite3"))
            .expect("follower survives"),
        snapshot
    );
    drop(follower);
    assert_eq!(rows(&promote(&follower_location)).len(), 1);
}

/// Partial copies remain private and are cleaned up without publishing a location.
#[test]
fn bootstrap_copy_failure_cleans_staging_and_preserves_empty_target() {
    let root = TestLocation::new("failed-bootstrap");
    std::fs::create_dir_all(root.path()).expect("root");
    let source = root.path().join("source");
    std::fs::create_dir(&source).expect("source");
    std::fs::write(source.join("terrabase.sqlite3"), b"copied before owner failure")
        .expect("source file");
    std::fs::create_dir(source.join("owner.tbv1")).expect("unreadable owner file");
    let target = root.path().join("target");
    assert!(WalFollower::bootstrap(&source, &target).is_err());
    assert!(!target.exists(), "an incomplete snapshot is never published");
    std::fs::create_dir(&target).expect("empty destination");
    assert!(WalFollower::bootstrap(&source, &target).is_err());
    assert_eq!(std::fs::read_dir(&target).expect("target survives").count(), 0);
    std::fs::remove_dir(source.join("owner.tbv1")).expect("remove owner copy fault");
    assert!(WalFollower::bootstrap(&source, &target).is_err(), "invalid SQLite cannot publish");
    assert_eq!(std::fs::read_dir(&target).expect("target survives validation").count(), 0);
    assert_eq!(
        std::fs::read_dir(root.path()).expect("root entries").count(),
        2,
        "staging directories are removed"
    );
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
    assert!(WalHeader::parse(&[0u8; 10]).is_err(), "a truncated header is refused");
    assert!(WalHeader::parse(&[0x99u8; 32]).is_err(), "a non-SQLite magic is refused");
}

#[test]
fn large_committed_transaction_ships_in_bounded_batches_without_early_commit_ack() {
    let writer_location = TestLocation::new("bounded-writer");
    std::fs::create_dir(writer_location.path()).expect("directory");
    let connection = rusqlite::Connection::open(writer_location.path().join("terrabase.sqlite3"))
        .expect("writer");
    connection.execute_batch(
        "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE large_value (value BLOB);"
    ).expect("schema");
    let follower_location = TestLocation::new("bounded-follower");
    let (mut follower, checkpoint) =
        WalFollower::bootstrap(writer_location.path(), follower_location.path())
            .expect("bootstrap");
    let mut tail = WalTail::resume(writer_location.path(), checkpoint);
    let before = follower.committed_through();
    connection
        .execute("INSERT INTO large_value VALUES (zeroblob(3145728))", [])
        .expect("large commit");
    let mut batches = 0;
    let mut partials = 0;
    let mut restarted = false;
    loop {
        match tail.poll().expect("capture") {
            WalTailStep::UpToDate => break,
            WalTailStep::GenerationChanged => panic!("no checkpoint rotated this generation"),
            WalTailStep::Frames(batch) => {
                assert!(batch.bytes.len() <= terrabase_cluster::wal_follower::MAX_WAL_BATCH_BYTES);
                if batches == 0 {
                    let mut oversized = batch.clone();
                    oversized
                        .bytes
                        .resize(terrabase_cluster::wal_follower::MAX_WAL_BATCH_BYTES + 1, 0);
                    assert!(matches!(
                        follower.apply(&oversized),
                        Err(terrabase_cluster::wal_follower::WalFollowerError::BatchTooLarge)
                    ));
                }
                let ack = follower.apply(&batch).expect("durable batch");
                if batch.committed_through.is_none() {
                    partials += 1;
                    assert_eq!(
                        ack.committed_through, before,
                        "partial frames do not acknowledge the transaction"
                    );
                }
                tail.acknowledge(ack).expect("ack");
                batches += 1;
                if batch.committed_through.is_none() && !restarted {
                    drop(follower);
                    let (reopened, checkpoint) =
                        WalFollower::open_existing(follower_location.path())
                            .expect("recover partial transaction");
                    follower = reopened;
                    assert_eq!(follower.committed_through(), before);
                    tail = WalTail::resume(writer_location.path(), checkpoint);
                    restarted = true;
                }
            }
        }
    }
    assert!(batches >= 4);
    assert!(partials >= 3);
    drop(follower);
    let promoted = rusqlite::Connection::open(follower_location.path().join("terrabase.sqlite3"))
        .expect("promoted");
    let length: i64 = promoted
        .query_row("SELECT length(value) FROM large_value", [], |row| row.get(0))
        .expect("complete transaction");
    assert_eq!(length, 3_145_728);
    let snapshot_location = TestLocation::new("bounded-bootstrap");
    let (snapshot, _) = WalFollower::bootstrap(writer_location.path(), snapshot_location.path())
        .expect("bootstrap a multi-batch committed WAL");
    drop(snapshot);
    let (mut recovered, _) =
        WalFollower::open_existing(snapshot_location.path()).expect("recover the large snapshot");
    recovered.fold_generation().expect("validate and fold the large WAL");
    drop(recovered);
    let snapshot = rusqlite::Connection::open(snapshot_location.path().join("terrabase.sqlite3"))
        .expect("snapshot database");
    let length: i64 = snapshot
        .query_row("SELECT length(value) FROM large_value", [], |row| row.get(0))
        .expect("snapshot value");
    assert_eq!(length, 3_145_728);
}

#[test]
fn physical_wire_preserves_real_frames_and_rejects_malformed_messages() {
    use terrabase_cluster::production::DatabaseRouteScope;
    use terrabase_cluster::wal_wire::{
        MAX_WAL_WIRE_BYTES, ScopedWalAck, ScopedWalBatch, WalWireError,
    };

    let (location, writer) = member("wire-writer");
    put(&writer, "wire-document", 42);
    let mut tail = WalTail::new(location.path());
    let WalTailStep::Frames(frames) = tail.poll().unwrap() else { panic!("committed frames") };
    let scope = DatabaseRouteScope::try_new(
        "01ARZ3NDEKTSV4RRFFQ69G5FAV",
        "01ARZ3NDEKTSV4RRFFQ69G5FAW",
        "01ARZ3NDEKTSV4RRFFQ69G5FAX",
    )
    .unwrap();
    let message = ScopedWalBatch { scope, replica_epoch: 7, frames: *frames };
    let encoded = message.encode().unwrap();
    let decoded = ScopedWalBatch::decode(&encoded).unwrap();
    assert_eq!(decoded, message);
    let follower_location = TestLocation::new("wire-follower");
    let (mut follower, _) =
        WalFollower::bootstrap(location.path(), follower_location.path()).unwrap();
    let receipt = follower.apply(&decoded.frames).unwrap();
    let mut unsynced = receipt;
    unsynced.fdatasynced = false;
    assert!(ScopedWalAck::from_follower(&decoded, unsynced).is_err());
    let ack = ScopedWalAck::from_follower(&decoded, receipt).unwrap();
    let ack_bytes = ack.encode().unwrap();
    assert_eq!(ScopedWalAck::decode(&ack_bytes).unwrap(), ack);
    assert_eq!(ack.clone().acknowledge(scope, 8, &mut tail), Err(WalWireError::InvalidScope));
    let foreign = DatabaseRouteScope::try_new(
        "01ARZ3NDEKTSV4RRFFQ69G5FAV",
        "01ARZ3NDEKTSV4RRFFQ69G5FAW",
        "01ARZ3NDEKTSV4RRFFQ69G5FAY",
    )
    .unwrap();
    assert_eq!(ack.clone().acknowledge(foreign, 7, &mut tail), Err(WalWireError::InvalidScope));
    for length in 0..ack_bytes.len() {
        assert!(ScopedWalAck::decode(&ack_bytes[..length]).is_err());
    }
    let mut trailing = ack_bytes.clone();
    trailing.push(0);
    assert!(ScopedWalAck::decode(&trailing).is_err());
    let mut wrong_chain = ack_bytes.clone();
    *wrong_chain.last_mut().unwrap() ^= 1;
    assert_eq!(
        ScopedWalAck::decode(&wrong_chain).unwrap().acknowledge(scope, 7, &mut tail),
        Err(WalWireError::InvalidPosition)
    );
    assert_eq!(tail.poll().unwrap(), WalTailStep::Frames(Box::new(decoded.frames.clone())));
    ScopedWalAck::decode(&ack_bytes).unwrap().acknowledge(scope, 7, &mut tail).unwrap();
    assert_eq!(tail.poll().unwrap(), WalTailStep::UpToDate);
    for length in 0..142 {
        assert!(ScopedWalBatch::decode(&encoded[..length]).is_err());
    }
    for (offset, value) in [(7, 2), (8, b'z')] {
        let mut corrupt = encoded.clone();
        corrupt[offset] = value;
        assert!(ScopedWalBatch::decode(&corrupt).is_err());
    }
    let mut corrupt = encoded.clone();
    corrupt[86..94].fill(0);
    assert_eq!(ScopedWalBatch::decode(&corrupt), Err(WalWireError::InvalidScope));
    let mut corrupt = encoded.clone();
    corrupt[94..102].fill(255);
    assert_eq!(ScopedWalBatch::decode(&corrupt), Err(WalWireError::InvalidPosition));
    let mut corrupt = encoded.clone();
    corrupt.push(0);
    assert_eq!(ScopedWalBatch::decode(&corrupt), Err(WalWireError::InvalidFrame));
    assert_eq!(
        ScopedWalBatch::decode(&vec![0; MAX_WAL_WIRE_BYTES + 1]),
        Err(WalWireError::Oversized)
    );
    let mut invalid = message;
    invalid.frames.committed_through = Some(0);
    assert_eq!(invalid.encode(), Err(WalWireError::InvalidPosition));
}

#[test]
fn physical_routes_authorize_before_storage_or_tail_progress() {
    use terrabase::RegionCode;
    use terrabase::identity::MemberUlid;
    use terrabase_cluster::production::{
        DatabaseRouteScope, MemberWorkloadIdentity, ProductionPlacement, ProductionRegionSeats,
    };
    use terrabase_cluster::wal_route::WalReplicaBinding;
    use terrabase_cluster::wal_wire::ScopedWalBatch;
    let writer_id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    let follower_id = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    let writer_identity =
        MemberWorkloadIdentity::try_new("production", "cluster", writer_id).unwrap();
    let follower_identity =
        MemberWorkloadIdentity::try_new("production", "cluster", follower_id).unwrap();
    let scope = DatabaseRouteScope::try_new(writer_id, writer_id, writer_id).unwrap();
    let placement = |local| {
        ProductionPlacement::try_new(
            MemberUlid::from_text(local).unwrap(),
            1,
            7,
            [ProductionRegionSeats::try_new(
                RegionCode::GRU,
                [
                    MemberUlid::from_text(writer_id).unwrap(),
                    MemberUlid::from_text(follower_id).unwrap(),
                ],
            )
            .unwrap()],
        )
        .unwrap()
    };
    let binding = |local| {
        WalReplicaBinding::new(
            scope,
            &placement(local),
            writer_identity.clone(),
            follower_identity.clone(),
        )
        .unwrap()
    };
    let follower_binding = binding(follower_id);
    let writer_binding = binding(writer_id);
    let (location, writer) = member("route-writer");
    put(&writer, "routed", 1);
    let follower_location = TestLocation::new("route-follower");
    let (mut follower, _) =
        WalFollower::bootstrap(location.path(), follower_location.path()).unwrap();
    let mut tail = WalTail::new(location.path());
    let WalTailStep::Frames(frames) = tail.poll().unwrap() else { panic!("frames") };
    let batch = ScopedWalBatch { scope, replica_epoch: 7, frames: *frames };
    let before = std::fs::read(follower_location.path().join("terrabase.sqlite3-wal")).unwrap();
    assert!(follower_binding.apply(&follower_identity, &batch, &mut follower).is_err());
    assert!(writer_binding.apply(&writer_identity, &batch, &mut follower).is_err());
    let foreign_identity =
        MemberWorkloadIdentity::try_new("staging", "cluster", writer_id).unwrap();
    assert!(follower_binding.apply(&foreign_identity, &batch, &mut follower).is_err());
    let mut stale = batch.clone();
    stale.replica_epoch = 6;
    assert!(follower_binding.apply(&writer_identity, &stale, &mut follower).is_err());
    let mut foreign = batch.clone();
    foreign.scope = DatabaseRouteScope::try_new(writer_id, writer_id, follower_id).unwrap();
    assert!(follower_binding.apply(&writer_identity, &foreign, &mut follower).is_err());
    assert_eq!(
        std::fs::read(follower_location.path().join("terrabase.sqlite3-wal")).unwrap(),
        before
    );
    let ack = follower_binding.apply(&writer_identity, &batch, &mut follower).unwrap();
    assert!(writer_binding.acknowledge(&writer_identity, ack.clone(), &mut tail).is_err());
    assert!(follower_binding.acknowledge(&follower_identity, ack.clone(), &mut tail).is_err());
    assert_eq!(tail.poll().unwrap(), WalTailStep::Frames(Box::new(batch.frames.clone())));
    let writer_clone = writer_binding.clone();
    writer_binding.fence();
    writer_binding.fence();
    assert!(matches!(
        writer_clone.acknowledge(&follower_identity, ack.clone(), &mut tail),
        Err(terrabase_cluster::wal_route::WalRouteError::Fenced)
    ));
    assert!(matches!(
        writer_binding.acknowledge(&follower_identity, ack.clone(), &mut tail),
        Err(terrabase_cluster::wal_route::WalRouteError::Fenced)
    ));
    let follower_clone = follower_binding.clone();
    follower_binding.fence();
    assert!(matches!(
        follower_clone.apply(&writer_identity, &batch, &mut follower),
        Err(terrabase_cluster::wal_route::WalRouteError::Fenced)
    ));
    assert_eq!(
        std::fs::read(follower_location.path().join("terrabase.sqlite3-wal")).unwrap(),
        before
    );
    // A new binding represents a fresh authority decision; old handles stay fenced.
    binding(writer_id).acknowledge(&follower_identity, ack, &mut tail).unwrap();
    assert_eq!(tail.poll().unwrap(), WalTailStep::UpToDate);
}

#[path = "wal_quic_support/mod.rs"]
mod wal_quic_support;

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn physical_wal_and_receipt_cross_authenticated_quic() {
    use std::sync::{Arc, Mutex};
    use terrabase::{RegionCode, identity::MemberUlid};
    use terrabase_cluster::production::{
        DatabaseRouteScope, ProductionPlacement, ProductionRegionSeats,
    };
    use terrabase_cluster::wal_quic::{receive_batch, send_batch};
    use terrabase_cluster::wal_route::WalReplicaBinding;
    use terrabase_cluster::wal_wire::ScopedWalBatch;
    let (first, second) = wal_quic_support::endpoint_pair();
    let writer_identity = first.config().local_identity().clone();
    let follower_identity = second.config().local_identity().clone();
    let writer_id = MemberUlid::from_text(writer_identity.member().as_str()).unwrap();
    let follower_id = MemberUlid::from_text(follower_identity.member().as_str()).unwrap();
    let scope = DatabaseRouteScope::try_new(
        writer_identity.member().as_str(),
        writer_identity.member().as_str(),
        writer_identity.member().as_str(),
    )
    .unwrap();
    let binding = |local| {
        let placement = ProductionPlacement::try_new(
            local,
            9,
            12,
            [ProductionRegionSeats::try_new(RegionCode::GRU, [writer_id, follower_id]).unwrap()],
        )
        .unwrap();
        WalReplicaBinding::new(
            scope,
            &placement,
            writer_identity.clone(),
            follower_identity.clone(),
        )
        .unwrap()
    };
    let writer_binding = binding(writer_id);
    let follower_binding = binding(follower_id);
    let (outbound, inbound) =
        tokio::join!(first.connect(follower_identity.member()), second.accept());
    let mut outbound = outbound.unwrap();
    let inbound = inbound.unwrap().unwrap();
    let (location, writer) = member("quic-writer");
    put(&writer, "seed-document", 1);
    let follower_location = TestLocation::new("quic-follower");
    let (follower, _) = WalFollower::bootstrap(location.path(), follower_location.path()).unwrap();
    let mut follower = follower;
    follower.fold_generation().unwrap();
    put(&writer, "network-document", 77);
    let follower = Arc::new(Mutex::new(follower));
    let mut tail = WalTail::new(location.path());
    let WalTailStep::Frames(frames) = tail.poll().unwrap() else { panic!("committed WAL") };
    let batch = ScopedWalBatch { scope, replica_epoch: 12, frames: *frames };
    let tail = Arc::new(Mutex::new(tail));
    // Cancel the writer only after the follower has fsynced, withholding its receipt.
    let (durable_tx, durable_rx) = tokio::sync::oneshot::channel();
    let cancelled_send = async {
        tokio::select! {
            result = send_batch(&mut outbound, &writer_binding, &batch, tail.clone()) => {
                panic!("receipt was withheld: {result:?}");
            }
            result = durable_rx => { result.unwrap(); }
        }
    };
    let ((), lost_receipt) = tokio::join!(
        cancelled_send,
        wal_quic_support::withhold_receipt(&inbound, &follower_binding, &follower, durable_tx)
    );
    assert_eq!(
        tail.lock().unwrap().poll().unwrap(),
        WalTailStep::Frames(Box::new(batch.frames.clone()))
    );
    let wal_path = follower_location.path().join("terrabase.sqlite3-wal");
    let durable_bytes = std::fs::read(&wal_path).unwrap();
    drop(outbound);
    drop(inbound);
    let (outbound, inbound) =
        tokio::join!(first.connect(follower_identity.member()), second.accept());
    let mut outbound = outbound.unwrap();
    let mut inbound = inbound.unwrap().unwrap();
    let (sent, received) = tokio::join!(
        send_batch(&mut outbound, &writer_binding, &batch, tail.clone()),
        receive_batch(&mut inbound, &follower_binding, follower.clone()),
    );
    sent.unwrap();
    received.unwrap();
    assert_eq!(std::fs::read(&wal_path).unwrap(), durable_bytes);
    assert!(lost_receipt.acknowledge(scope, 12, &mut tail.lock().unwrap()).is_err());
    assert_eq!(tail.lock().unwrap().poll().unwrap(), WalTailStep::UpToDate);
    assert_eq!(follower.lock().unwrap().committed_through(), batch.frames.committed_through);
    drop(follower);
    let promoted = promote(&follower_location);
    assert_eq!(rows(&promoted), rows(&writer));
    writer_binding.fence();
    assert!(send_batch(&mut outbound, &writer_binding, &batch, tail.clone()).await.is_err());
    drop(outbound);
    drop(inbound);
    let _ = tokio::join!(first.drain(), second.drain());
}

#[test]
fn index_lifecycle_updates_replicate_in_wal_and_survive_promotion() {
    let (location, writer) = member("lifecycle-writer");
    let follower_location = TestLocation::new("lifecycle-follower");
    let (mut follower, checkpoint) =
        WalFollower::bootstrap_engine(writer.engine(), follower_location.path()).unwrap();
    let mut tail = WalTail::resume(location.path(), checkpoint);
    let tenant = TenantId::try_new("tenant-a").unwrap();
    assert_eq!(
        writer.engine().auto_index_forbid_shape(&tenant, "candidate", "score", 0).unwrap(),
        1
    );
    let mut next = tail.poll_engine(writer.engine()).unwrap();
    if next == WalTailStep::GenerationChanged {
        follower.fold_generation().unwrap();
        tail = WalTail::new(location.path());
        next = tail.poll_engine(writer.engine()).unwrap();
    }
    let WalTailStep::Frames(batch) = next else { panic!("lifecycle write must reach WAL") };
    tail.acknowledge(follower.apply(&batch).unwrap()).unwrap();
    assert_eq!(tail.poll_engine(writer.engine()).unwrap(), WalTailStep::UpToDate);
    let expected = writer.engine().auto_index_lifecycle_audit(&tenant, None, None, 10).unwrap();
    drop(follower);
    let promoted = promote(&follower_location);
    assert_eq!(promoted.engine().auto_index_lifecycle_generation(&tenant).unwrap(), 1);
    assert_eq!(
        promoted.engine().auto_index_lifecycle_audit(&tenant, None, None, 10).unwrap(),
        expected
    );
}
