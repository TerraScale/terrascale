//! Local API success fixture: physical `SQLite` copies and scoped durable receipts.
//! This exercises in-process WAL transport, not provider or production qualification.

use crate::replication_runtime::ReplicationProgress;
use std::collections::BTreeSet;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock, Weak};
use terrabase::identity::MemberUlid;
use terrabase_cluster::production::{
    DatabaseRouteScope, MemberWorkloadIdentity, ProductionPlacement, ProductionRegionSeats,
};
use terrabase_cluster::wal_follower::{WalFollower, WalTail, WalTailStep};
use terrabase_cluster::wal_route::WalReplicaBinding;
use terrabase_cluster::wal_wire::ScopedWalBatch;

type Fixture = (Weak<ReplicationProgress>, Arc<Mutex<()>>);
static FIXTURES: OnceLock<Mutex<Vec<Fixture>>> = OnceLock::new();
static NEXT_COPY: AtomicU64 = AtomicU64::new(0);

pub(super) fn progress() -> Arc<ReplicationProgress> {
    let progress = Arc::new(ReplicationProgress::default());
    let mut fixtures = FIXTURES.get_or_init(Mutex::default).lock().unwrap();
    fixtures.retain(|(fixture, _)| fixture.strong_count() != 0);
    fixtures.push((Arc::downgrade(&progress), Arc::new(Mutex::new(()))));
    progress
}

struct CopyDirectory(PathBuf);

impl Drop for CopyDirectory {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// Only explicitly registered positive fixtures receive real replication work.
/// Replacing the progress ledger disables this helper, preserving refusal tests.
pub(in crate::api) fn replicate(
    progress: &Arc<ReplicationProgress>,
    engine: &terrabase::Engine,
    tenant: &terrabase::types::TenantScope,
) {
    let serial = FIXTURES.get().and_then(|fixtures| {
        fixtures.lock().unwrap().iter().find_map(|(fixture, serial)| {
            fixture
                .upgrade()
                .filter(|fixture| Arc::ptr_eq(fixture, progress))
                .map(|_| Arc::clone(serial))
        })
    });
    let Some(serial) = serial else {
        return;
    };
    let _serial = serial.lock().unwrap();
    let owner = engine.database_owner();
    let scope = DatabaseRouteScope::new(owner.tenant(), owner.project(), owner.database());
    if !progress.boundaries().unwrap().contains_key(&scope) {
        // Concurrent requests can discover the same database together.
        if progress.admit(scope, BTreeSet::from([owner.region().as_str().to_owned()])).is_err() {
            assert!(progress.boundaries().unwrap().contains_key(&scope));
        }
    }
    let writer_id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    let follower_id = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    let writer = MemberWorkloadIdentity::try_new("test", "api-rf2", writer_id).unwrap();
    let follower = MemberWorkloadIdentity::try_new("test", "api-rf2", follower_id).unwrap();
    let binding = |local| {
        WalReplicaBinding::new(
            scope,
            &ProductionPlacement::try_new(
                MemberUlid::from_text(local).unwrap(),
                1,
                1,
                [ProductionRegionSeats::try_new(
                    terrabase::RegionCode::GRU,
                    [
                        MemberUlid::from_text(writer_id).unwrap(),
                        MemberUlid::from_text(follower_id).unwrap(),
                    ],
                )
                .unwrap()],
            )
            .unwrap(),
            writer.clone(),
            follower.clone(),
        )
        .unwrap()
    };
    let writer_binding = binding(writer_id);
    let follower_binding = binding(follower_id);
    'snapshot: for _ in 0..32 {
        let copy = CopyDirectory(std::env::temp_dir().join(format!(
            "terrabase-api-rf2-{}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos(),
            NEXT_COPY.fetch_add(1, Ordering::Relaxed),
        )));
        // Capture the finite boundary before the quiescent snapshot. Never publish a
        // newer writer sequence merely because a prior batch was acknowledged.
        let sequence = engine.read_snapshot(tenant).unwrap().sequence().get();
        let (mut replica, checkpoint) = WalFollower::bootstrap_engine(engine, &copy.0).unwrap();
        let location = engine.with_quiescent_wal(std::path::Path::to_path_buf).unwrap();
        let mut tail = WalTail::resume(&location, checkpoint);
        loop {
            match tail.poll_engine(engine).unwrap() {
                WalTailStep::Frames(frames) => {
                    let batch = ScopedWalBatch { scope, replica_epoch: 1, frames: *frames };
                    let receipt = follower_binding.apply(&writer, &batch, &mut replica).unwrap();
                    writer_binding.acknowledge(&follower, receipt, &mut tail).unwrap();
                }
                WalTailStep::UpToDate => break,
                WalTailStep::GenerationChanged => continue 'snapshot,
            }
        }
        // Read the copied SQLite state itself before publishing the conservative
        // receipt boundary, including the empty-WAL snapshot-only case.
        let connection = terrabase::storage::open_physical_sqlite(
            &copy.0.join("terrabase.sqlite3"),
            rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY,
        )
        .unwrap();
        let copied: i64 = connection
            .query_row(
                "SELECT COALESCE(MAX(sequence), 0) FROM (
                SELECT MAX(commit_seq) AS sequence FROM documents
                UNION ALL SELECT MAX(seq) FROM usage_facts
                UNION ALL SELECT MAX(sequence) FROM operation_facts
                UNION ALL SELECT MAX(seq_hi) FROM metering_db_facts
            )",
                [],
                |row| row.get(0),
            )
            .unwrap();
        let copied = u64::try_from(copied).expect("nonnegative physical follower sequence");
        assert!(copied >= sequence, "physical follower must include the observed writer boundary");
        progress.advance_regional(scope, sequence);
        return;
    }
    panic!("API fixture WAL never stabilized for a durable receipt");
}
