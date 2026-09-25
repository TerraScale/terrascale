//! Local archive-candidate behavior through the public storage integration boundary.
//!
//! Object references in this module are synthetic shape-matched fixtures. They
//! model the higher layer's verified-reference input; no provider upload,
//! readback, object durability, or remote recovery authority is exercised here.

use std::fs::{self, OpenOptions};
#[cfg(feature = "slow-tests")]
use std::os::unix::fs::symlink;
use std::path::{Path, PathBuf};
#[cfg(feature = "slow-tests")]
use std::process::Command;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use tb_core::{
    BudgetConfig, DatabaseId, DocumentId, Key, MembershipEpoch, MemoryBudget, MemoryCategory,
    Mutation, OriginId, ReplicaId, ShardId, TenantId, TransactionId, WorkerId,
};
use tb_format::{FormatLimits, decode_wal_frame, wal_frame_len};
use tb_storage::{
    ArchiveCut, ArchiveObjectRef, ArchiveProgress, ArchiveRunRefs, ArchiveVerifiedRefs,
    ArchiveWalRefs, LocalDocumentMode, Storage, StorageConfig,
};

static NEXT_ROOT: AtomicU64 = AtomicU64::new(0);

const TEST_DESTINATION: [u8; 32] = [0x42; 32];
const OTHER_DESTINATION: [u8; 32] = [0x43; 32];
const CRASH_CHILD_ENV: &str = "TB_STORAGE_ARCHIVE_CRASH_CHILD";
const CRASH_DIRECTORY_ENV: &str = "TB_STORAGE_PROCESS_TEST_DIRECTORY";
const CRASH_POINT_ENV: &str = "TB_STORAGE_CRASH_POINT";

struct TempRoot(PathBuf);

impl TempRoot {
    fn new() -> Self {
        let tick = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock is after Unix epoch")
            .as_nanos();
        let ordinal = NEXT_ROOT.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "terrabase-archive-{}-{tick}-{ordinal}",
            std::process::id()
        ));
        fs::create_dir_all(&path).expect("create archive test root");
        Self(path)
    }

    fn shard(&self) -> PathBuf {
        self.0.join("documents")
    }
}

impl Drop for TempRoot {
    fn drop(&mut self) {
        if self.0.exists() {
            fs::remove_dir_all(&self.0).expect("remove archive test root");
        }
    }
}

fn mode() -> LocalDocumentMode {
    LocalDocumentMode::new(
        TenantId::from_bytes([1; 16]),
        DatabaseId::from_bytes([2; 16]),
        ShardId::new(3),
        OriginId {
            replica_id: ReplicaId::from_bytes([4; 16]),
            incarnation: [5; 16],
        },
        MembershipEpoch::new(6),
        [7; 32],
    )
}

fn config_with_clock(path: &Path, clock_ms: Arc<AtomicU64>) -> StorageConfig {
    StorageConfig::local(path, TenantId::from_bytes([1; 16]), WorkerId(1))
        .with_clock(Arc::new(move || clock_ms.load(Ordering::SeqCst)))
}

fn config(path: &Path) -> StorageConfig {
    config_with_clock(path, Arc::new(AtomicU64::new(1_000)))
}

fn budget() -> MemoryBudget {
    MemoryBudget::new(BudgetConfig::new(128 * 1024 * 1024, 0).expect("valid archive budget"))
}

fn commit(storage: &Storage, sequence: u8) {
    let key = Key::new(vec![sequence]).expect("valid archive fixture key");
    storage
        .commit_batch(
            storage.snapshot(),
            TransactionId::new(50_000, [sequence; 16]),
            &[Mutation::Put {
                key,
                value: vec![sequence; 8],
            }],
        )
        .expect("commit archive fixture row");
}

fn read(storage: &Storage, sequence: u8) -> Option<Vec<u8>> {
    let key = Key::new(vec![sequence]).expect("valid archive fixture key");
    storage
        .get(storage.snapshot(), &key)
        .expect("read local archive fixture row")
        .map(|value| value.bytes().to_vec())
}

fn object_ref(cut_id: [u8; 32], label: u64, len: u64, sha256: [u8; 32]) -> ArchiveObjectRef {
    let mut object_id = [0_u8; 16];
    object_id[..8].copy_from_slice(&cut_id[..8]);
    object_id[8..].copy_from_slice(&label.to_be_bytes());
    ArchiveObjectRef {
        key: format!("tb-objects/v1/{}", DocumentId::from_bytes(object_id)),
        len,
        sha256,
    }
}

/// Build synthetic refs that match the local sources exactly, while independently
/// checking that every emitted WAL payload is a contiguous whole-frame interval.
fn synthetic_verified_refs(cut: &mut ArchiveCut) -> ArchiveVerifiedRefs {
    let cut_id = cut.id();
    let limits = FormatLimits::default();
    let mut runs = Vec::new();
    for (index, run) in cut.runs().iter().enumerate() {
        let sidecar = run.remote_sidecar().expect("read checked run sidecar");
        let label = u64::try_from(index).expect("run index fits object label");
        runs.push(ArchiveRunRefs {
            run_id: run.id(),
            segment: object_ref(cut_id, label + 1, run.len(), run.sha256()),
            sidecar: object_ref(
                cut_id,
                label + (1 << 32),
                sidecar.bytes().len() as u64,
                sidecar.sha256(),
            ),
        });
    }

    let mut wal = Vec::new();
    let mut expected = cut
        .checkpoint()
        .get()
        .checked_add(1)
        .expect("checkpoint sequence can advance");
    while let Some(chunk) = cut.next_wal_chunk().expect("export bounded WAL chunk") {
        assert_eq!(chunk.first.get(), expected, "WAL chunks must have no gaps");
        let mut sequences = Vec::new();
        let mut remaining = chunk.bytes();
        while !remaining.is_empty() {
            let frame_len = wal_frame_len(remaining, &limits).expect("read whole WAL frame size");
            assert!(frame_len <= remaining.len(), "chunk cannot end mid-frame");
            let frame = decode_wal_frame(&remaining[..frame_len], &limits)
                .expect("decode a complete source WAL frame");
            sequences.push(frame.sequence());
            remaining = &remaining[frame_len..];
        }
        assert_eq!(sequences.first().copied(), Some(chunk.first.get()));
        assert_eq!(sequences.last().copied(), Some(chunk.last.get()));
        for pair in sequences.windows(2) {
            assert_eq!(pair[1], pair[0] + 1, "frames inside a chunk are contiguous");
        }
        expected = chunk
            .last
            .get()
            .checked_add(1)
            .expect("WAL sequence can advance");
        let label = u64::try_from(wal.len()).expect("WAL chunk index fits object label");
        wal.push(ArchiveWalRefs {
            first: chunk.first,
            last: chunk.last,
            object: object_ref(
                cut_id,
                label + (1 << 48),
                chunk.bytes().len() as u64,
                chunk.sha256(),
            ),
        });
    }
    assert_eq!(
        expected,
        cut.frontier()
            .get()
            .checked_add(1)
            .expect("frontier sequence can advance"),
        "export must cover checkpoint + 1 through the captured frontier exactly"
    );
    ArchiveVerifiedRefs { runs, wal }
}

fn synthetic_manifest_ref(
    cut: &ArchiveCut,
    candidate: &tb_storage::ArchiveCandidate,
) -> ArchiveObjectRef {
    object_ref(
        cut.id(),
        u64::MAX,
        candidate.bytes().len() as u64,
        candidate.sha256(),
    )
}

fn complete_candidate(storage: &Storage, cut: &ArchiveCut, refs: &ArchiveVerifiedRefs) {
    let candidate = cut
        .encode_candidate(refs)
        .expect("encode complete candidate");
    let verified_manifest = synthetic_manifest_ref(cut, &candidate);
    storage
        .complete_archive_cut(cut, &candidate, verified_manifest)
        .expect("persist synthetic verified candidate reference");
}

fn progress_candidate(storage: &Storage) -> ([u8; 32], u64, u64, ArchiveObjectRef) {
    match storage.archive_progress() {
        ArchiveProgress::Candidate {
            cut_id,
            frontier,
            manifest,
        } => (
            cut_id,
            frontier.sequence.get(),
            frontier.retry_floor_ms,
            manifest,
        ),
        other => panic!("expected readable candidate progress, got {other:?}"),
    }
}

#[test]
fn bounded_cut_covers_whole_frames_and_captures_current_retry_floor() {
    let root = TempRoot::new();
    let clock = Arc::new(AtomicU64::new(1_000));
    let storage = Storage::bootstrap_local_document(
        config_with_clock(&root.shard(), Arc::clone(&clock)),
        budget(),
        mode(),
    )
    .expect("bootstrap local document shard");
    assert!(matches!(
        storage.archive_progress(),
        ArchiveProgress::NoCandidate
    ));
    storage
        .ensure_archive_destination(TEST_DESTINATION)
        .expect("bind fixed destination identity before capture");

    commit(&storage, 1);
    storage.checkpoint().expect("checkpoint source event one");
    clock.store(2_000, Ordering::SeqCst);
    storage
        .checkpoint()
        .expect("advance the persisted retry floor");
    assert_eq!(storage.stats().expired_floor_ms, 2_000);
    commit(&storage, 2);
    commit(&storage, 3);

    let mut cut = storage
        .begin_archive_cut(TEST_DESTINATION, 128)
        .expect("capture bounded local archive cut");
    assert_eq!(cut.checkpoint().get(), 1);
    assert_eq!(cut.frontier().get(), 3);
    assert_eq!(cut.floor_ms(), storage.stats().expired_floor_ms);
    assert_eq!(cut.floor_ms(), 2_000);
    assert_eq!(cut.runs().len(), 1);

    let incomplete = ArchiveVerifiedRefs {
        runs: Vec::new(),
        wal: Vec::new(),
    };
    assert!(
        cut.encode_candidate(&incomplete).is_err(),
        "an incomplete export cannot produce a candidate"
    );

    let refs = synthetic_verified_refs(&mut cut);
    assert_eq!(
        refs.wal
            .first()
            .expect("post-checkpoint WAL refs")
            .first
            .get(),
        2
    );
    assert_eq!(
        refs.wal
            .last()
            .expect("post-checkpoint WAL refs")
            .last
            .get(),
        3
    );
    let mut wrong_sidecar = refs.clone();
    wrong_sidecar.runs[0].sidecar.sha256[0] ^= 1;
    assert!(
        cut.encode_candidate(&wrong_sidecar).is_err(),
        "sidecar references must match the checked run sidecar"
    );

    let candidate = cut
        .encode_candidate(&refs)
        .expect("encode complete cut candidate");
    let manifest_ref = synthetic_manifest_ref(&cut, &candidate);
    let mut wrong_manifest_ref = manifest_ref.clone();
    wrong_manifest_ref.sha256[0] ^= 1;
    assert!(
        storage
            .complete_archive_cut(&cut, &candidate, wrong_manifest_ref)
            .is_err(),
        "candidate readback identity must bind the exact encoded bytes"
    );
    let frontier = storage
        .complete_archive_cut(&cut, &candidate, manifest_ref.clone())
        .expect("persist exact candidate reference");
    assert_eq!(frontier.sequence.get(), 3);
    assert_eq!(frontier.retry_floor_ms, 2_000);
    assert_eq!(
        progress_candidate(&storage),
        (cut.id(), 3, 2_000, manifest_ref),
        "progress exposes the captured cut and current retry floor"
    );
    assert_eq!(read(&storage, 3).as_deref(), Some(&[3; 8][..]));
}

#[test]
fn captured_cut_fails_on_premature_wal_eof_without_publishing_a_prefix() {
    let root = TempRoot::new();
    let path = root.shard();
    let storage = Storage::bootstrap_local_document(config(&path), budget(), mode())
        .expect("bootstrap local document shard");
    storage
        .ensure_archive_destination(TEST_DESTINATION)
        .expect("bind archive destination");
    commit(&storage, 1);
    storage.checkpoint().expect("checkpoint source event one");
    commit(&storage, 2);
    let mut cut = storage
        .begin_archive_cut(TEST_DESTINATION, 128)
        .expect("capture source cut before truncation");
    let captured_frontier = cut.frontier();

    let wal = OpenOptions::new()
        .write(true)
        .open(path.join("wal.log"))
        .expect("open captured source WAL");
    wal.set_len(0).expect("truncate source below captured EOF");
    wal.sync_all().expect("sync premature WAL EOF fixture");

    assert!(
        cut.next_wal_chunk().is_err(),
        "the archive exporter must reject a WAL ending before captured frontier {}",
        captured_frontier.get()
    );
    assert!(matches!(
        storage.archive_progress(),
        ArchiveProgress::NoCandidate
    ));
    assert_eq!(read(&storage, 2).as_deref(), Some(&[2; 8][..]));
}

#[test]
fn older_cut_can_complete_after_newer_local_commits() {
    let root = TempRoot::new();
    let storage = Storage::bootstrap_local_document(config(&root.shard()), budget(), mode())
        .expect("bootstrap local document shard");
    storage
        .ensure_archive_destination(TEST_DESTINATION)
        .expect("bind archive destination");
    commit(&storage, 1);
    storage.checkpoint().expect("checkpoint source event one");
    commit(&storage, 2);

    let mut old_cut = storage
        .begin_archive_cut(TEST_DESTINATION, 128)
        .expect("capture old cut");
    assert_eq!(old_cut.frontier().get(), 2);
    let refs = synthetic_verified_refs(&mut old_cut);
    let candidate = old_cut
        .encode_candidate(&refs)
        .expect("encode old cut candidate");
    let manifest_ref = synthetic_manifest_ref(&old_cut, &candidate);

    commit(&storage, 3);
    assert_eq!(storage.stats().visible.get(), 3);
    let frontier = storage
        .complete_archive_cut(&old_cut, &candidate, manifest_ref.clone())
        .expect("complete captured candidate after newer commit");
    assert_eq!(frontier.sequence.get(), 2);
    assert_eq!(
        progress_candidate(&storage),
        (old_cut.id(), 2, 1_000, manifest_ref)
    );
    assert_eq!(read(&storage, 3).as_deref(), Some(&[3; 8][..]));
}

#[test]
fn destination_mismatch_degrades_archive_progress_but_keeps_local_data() {
    let root = TempRoot::new();
    let storage = Storage::bootstrap_local_document(config(&root.shard()), budget(), mode())
        .expect("bootstrap local document shard");
    storage
        .ensure_archive_destination(TEST_DESTINATION)
        .expect("bind archive destination");
    commit(&storage, 1);

    assert!(
        storage.begin_archive_cut(OTHER_DESTINATION, 128).is_err(),
        "a cut for another object destination must be rejected"
    );
    assert!(matches!(
        storage.archive_progress(),
        ArchiveProgress::Degraded
    ));
    assert_eq!(read(&storage, 1).as_deref(), Some(&[1; 8][..]));
}

#[cfg(feature = "slow-tests")]
#[test]
fn archive_index_crash_child() {
    if std::env::var_os(CRASH_CHILD_ENV).is_none() {
        return;
    }
    let directory = PathBuf::from(
        std::env::var_os(CRASH_DIRECTORY_ENV).expect("parent supplies archive shard"),
    );
    let storage = Storage::open_local_document(config(&directory), budget(), mode())
        .expect("open child archive shard");
    storage
        .ensure_archive_destination(TEST_DESTINATION)
        .expect("recover existing archive destination binding");
    commit(&storage, 66);
    let mut cut = storage
        .begin_archive_cut(TEST_DESTINATION, 128)
        .expect("capture next child candidate");
    let refs = synthetic_verified_refs(&mut cut);
    let candidate = cut
        .encode_candidate(&refs)
        .expect("encode complete child candidate");
    let manifest_ref = synthetic_manifest_ref(&cut, &candidate);
    storage
        .complete_archive_cut(&cut, &candidate, manifest_ref)
        .expect("crash hook must run during candidate publication");
    panic!("configured archive-index crash hook did not terminate the child");
}

#[cfg(feature = "slow-tests")]
fn run_archive_crash_child(directory: &TempRoot, point: &str) -> Option<i32> {
    let executable = std::env::current_exe().expect("locate Slow integration binary");
    Command::new(executable)
        .arg("--exact")
        .arg("storage_slow::archive_contract::archive_index_crash_child")
        .env(CRASH_CHILD_ENV, "1")
        .env(CRASH_DIRECTORY_ENV, directory.shard())
        .env(CRASH_POINT_ENV, point)
        .status()
        .expect("launch archive-index crash child")
        .code()
}

#[cfg(feature = "slow-tests")]
fn seed_two_candidate_indexes(storage: &Storage) -> ([u8; 32], [u8; 32]) {
    commit(storage, 1);
    storage.checkpoint().expect("checkpoint source event one");
    let mut first_cut = storage
        .begin_archive_cut(TEST_DESTINATION, 128)
        .expect("capture first candidate cut");
    let first_refs = synthetic_verified_refs(&mut first_cut);
    let first_cut_id = first_cut.id();
    complete_candidate(storage, &first_cut, &first_refs);
    drop(first_cut);

    for sequence in 2..=65 {
        commit(storage, sequence);
    }
    let mut second_cut = storage
        .begin_archive_cut(TEST_DESTINATION, 128)
        .expect("capture larger second candidate");
    let second_refs = synthetic_verified_refs(&mut second_cut);
    let second_cut_id = second_cut.id();
    complete_candidate(storage, &second_cut, &second_refs);
    drop(second_cut);
    (first_cut_id, second_cut_id)
}

#[cfg(feature = "slow-tests")]
#[test]
fn archive_index_crashes_recover_only_published_candidates() {
    for (point, advances_candidate) in [
        ("after_archive_index_pending_sync", false),
        ("after_archive_index_rename_before_directory_sync", true),
        ("after_archive_index_directory_sync", true),
    ] {
        let root = TempRoot::new();
        let path = root.shard();
        let storage = Storage::bootstrap_local_document(config(&path), budget(), mode())
            .expect("bootstrap archive crash shard");
        storage
            .ensure_archive_destination(TEST_DESTINATION)
            .expect("bind archive destination");
        let (_older_cut_id, published_cut_id) = seed_two_candidate_indexes(&storage);
        assert_eq!(progress_candidate(&storage).0, published_cut_id);
        commit(&storage, 66);
        let predicted_cut = storage
            .begin_archive_cut(TEST_DESTINATION, 128)
            .expect("predict child cut identity before process restart");
        let new_cut_id = predicted_cut.id();
        drop(predicted_cut);
        drop(storage);

        assert_eq!(
            run_archive_crash_child(&root, point),
            Some(86),
            "child must exit at archive boundary {point}"
        );
        let reopened = Storage::open_local_document(config(&path), budget(), mode())
            .expect("local shard reopens after candidate-index process crash");
        let (cut_id, frontier, _, _) = progress_candidate(&reopened);
        if advances_candidate {
            assert_eq!(cut_id, new_cut_id);
            assert_eq!(frontier, 66);
        } else {
            assert_eq!(
                cut_id, published_cut_id,
                "pending sync crash keeps prior candidate"
            );
            assert_eq!(frontier, 65);
        }
        assert_eq!(read(&reopened, 66).as_deref(), Some(&[66; 8][..]));
    }
}

#[cfg(feature = "slow-tests")]
#[test]
fn corrupt_newest_candidate_falls_back_but_two_invalid_slots_degrade_archive_only() {
    let root = TempRoot::new();
    let path = root.shard();
    let storage = Storage::bootstrap_local_document(config(&path), budget(), mode())
        .expect("bootstrap archive recovery shard");
    storage
        .ensure_archive_destination(TEST_DESTINATION)
        .expect("bind archive destination");
    let (older_cut_id, newer_cut_id) = seed_two_candidate_indexes(&storage);
    assert_eq!(progress_candidate(&storage).0, newer_cut_id);
    drop(storage);

    let newest_slot = path.join("archive.0");
    let mut bytes = fs::read(&newest_slot).expect("read newest candidate index");
    let last = bytes.len() - 1;
    bytes[last] ^= 1;
    fs::write(&newest_slot, bytes).expect("corrupt newest slot CRC");

    let fallback = Storage::open_local_document(config(&path), budget(), mode())
        .expect("structural archive corruption must not hide local shard");
    let (cut_id, frontier, _, _) = progress_candidate(&fallback);
    assert_eq!(
        cut_id, older_cut_id,
        "older fully valid candidate is selected"
    );
    assert_eq!(frontier, 1);
    assert_eq!(read(&fallback, 65).as_deref(), Some(&[65; 8][..]));
    drop(fallback);

    let older_slot = path.join("archive.1");
    let mut bytes = fs::read(&older_slot).expect("read older candidate index");
    let last = bytes.len() - 1;
    bytes[last] ^= 1;
    fs::write(&older_slot, bytes).expect("corrupt older slot CRC");

    let degraded = Storage::open_local_document(config(&path), budget(), mode())
        .expect("invalid archive slots do not hide local shard");
    assert!(matches!(
        degraded.archive_progress(),
        ArchiveProgress::Degraded
    ));
    assert_eq!(read(&degraded, 65).as_deref(), Some(&[65; 8][..]));
}

#[cfg(feature = "slow-tests")]
#[test]
fn archive_slot_open_error_does_not_fall_back_to_an_older_candidate() {
    let root = TempRoot::new();
    let path = root.shard();
    let storage = Storage::bootstrap_local_document(config(&path), budget(), mode())
        .expect("bootstrap archive I/O recovery shard");
    storage
        .ensure_archive_destination(TEST_DESTINATION)
        .expect("bind archive destination");
    let (older_cut_id, newer_cut_id) = seed_two_candidate_indexes(&storage);
    assert_eq!(progress_candidate(&storage).0, newer_cut_id);
    drop(storage);

    let newest_slot = path.join("archive.0");
    let saved_newest = path.join("archive.0.saved");
    fs::rename(&newest_slot, &saved_newest).expect("save newest valid slot");
    symlink(path.join("archive.1"), &newest_slot).expect("inject no-follow slot-open error");

    let reopened = Storage::open_local_document(config(&path), budget(), mode())
        .expect("archive slot I/O error leaves local shard available");
    assert!(
        matches!(reopened.archive_progress(), ArchiveProgress::Degraded),
        "an I/O-class open error must not silently select the older slot {older_cut_id:?}"
    );
    assert_eq!(read(&reopened, 65).as_deref(), Some(&[65; 8][..]));
}

#[cfg(feature = "slow-tests")]
#[test]
fn archive_index_admission_failure_does_not_fall_back_to_older_candidate() {
    let root = TempRoot::new();
    let path = root.shard();
    let storage = Storage::bootstrap_local_document(config(&path), budget(), mode())
        .expect("bootstrap archive admission shard");
    storage
        .ensure_archive_destination(TEST_DESTINATION)
        .expect("bind archive destination");
    let (older_cut_id, newer_cut_id) = seed_two_candidate_indexes(&storage);
    assert_eq!(progress_candidate(&storage).0, newer_cut_id);
    drop(storage);

    let older_len = fs::metadata(path.join("archive.1"))
        .expect("stat older valid slot")
        .len() as usize;
    let newer_len = fs::metadata(path.join("archive.0"))
        .expect("stat newer valid slot")
        .len() as usize;
    let measurement_budget = budget();
    let measured = Storage::open_local_document(config(&path), measurement_budget.clone(), mode())
        .expect("open with both candidate indexes admitted");
    let resident = measurement_budget.snapshot().categories[MemoryCategory::DatabaseState as usize];
    drop(measured);
    let newer_reservation = newer_len
        .checked_mul(4)
        .expect("new slot accounting size fits");
    let older_reservation = older_len
        .checked_mul(4)
        .expect("old slot accounting size fits");
    assert!(
        newer_reservation > older_reservation + 4_096,
        "newer slot must be large enough to separate admission thresholds"
    );
    let baseline = resident
        .checked_sub(newer_reservation)
        .expect("resident state includes the newest slot reservation");
    let category_limit = baseline
        .checked_add(older_reservation)
        .and_then(|limit| limit.checked_add(1_024))
        .expect("archive admission threshold fits");
    let limited_budget = MemoryBudget::new(
        BudgetConfig::new(128 * 1024 * 1024, 0)
            .expect("valid limited archive budget")
            .with_category_limit_bytes(MemoryCategory::DatabaseState, category_limit),
    );
    let reopened = Storage::open_local_document(config(&path), limited_budget, mode())
        .expect("archive admission failure degrades only candidate progress");
    assert!(
        matches!(reopened.archive_progress(), ArchiveProgress::Degraded),
        "an admission failure while reading the newest slot must not select older cut {older_cut_id:?}"
    );
    assert_eq!(read(&reopened, 65).as_deref(), Some(&[65; 8][..]));
}
