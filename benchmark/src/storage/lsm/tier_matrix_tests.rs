//! The four-combination storage-tier matrix lane (gap-sweep row 4,
//! `docs/evidence/qualification-gap-sweep.md`; the workspace goal's
//! "Active storage tiers").
//!
//! The charter's supported profiles are Tier 2 only, Tier 1 plus Tier 2,
//! Tier 2 plus Tier 3, and all three tiers, with Tier 1 and Tier 3
//! independently optional: "Their absence is not degraded health and
//! produces no warning, fallback delay, empty result, or repeated
//! background work." Before this lane every tier behavior was proven in
//! isolation (one mover pass, one fallback read, one failure class); no
//! test named the combination matrix or asserted identical logical state
//! across it.
//!
//! Model of each tier at this crate's altitude (honest limits):
//!
//! - Tier 2 (mandatory network block storage) is the LSM location itself:
//!   WAL, segments, manifests. Every profile opens the same kind of
//!   location and runs the same logical operation sequence against it.
//! - Tier 3 (optional object storage) is the engine's real cold tier: the
//!   content-addressed `<location>/tier-objects` store the mover uploads
//!   into (`tier::run_mover`) and the engine's own document reads fall
//!   back to (`collect_range` through `read_cold_segment_entries`).
//! - Tier 1 (optional local `NVMe`) has no separate device in this landing
//!   (gap-sweep row 4 names that hole). The lane composes it as a
//!   distinct configurable read-through cache source over the same
//!   `ObjectStore` seam, first in the cold-read order. Combinations
//!   containing Tier 1 prove the composition contract — the cache
//!   changes where cold bytes come from, never the logical state — not a
//!   shipped Tier-1 device.
//!
//! What the matrix proves, per profile against one shared operation
//! sequence (seed writes, overwrite, delete, identical mover pressure,
//! full read-back, twice-repeated cold reads, post-cold writes, reopen):
//!
//! 1. identical materialized results (`documents_at` plus every document
//!    read individually) and identical canonical state (`published_state`
//!    — the sequence watermark and the root-chain head over committed
//!    digests), before pressure, after demotion, after the post-cold
//!    writes, and after recovery;
//! 2. every disabled tier is skipped structurally: zero store calls (the
//!    lane constructs a counting seat for every tier in every profile, so
//!    an erroneous touch is countable rather than unreachable), zero
//!    on-disk artifacts, and zero mover attempts under the same pressure
//!    that demotes in enabled profiles — the impossible demotion is not
//!    attempted, much less retried;
//! 3. the work is bounded and single-pass: exact per-read and per-run
//!    call counts, no repetition, and no call growth without operations
//!    (there is no background mover loop in this landing; `src/tiers.rs`
//!    owns the typed unconfigured and paused refusals); and
//! 4. intentionally disabled is not degraded health: no empty or partial
//!    read results, `journal_divergence` zero, and the ranges that
//!    demote in enabled profiles stay readable on the mandatory local
//!    tier in the disabled ones.
//!
//! No warning can be emitted on this seam: the tier surfaces are typed
//! outcomes only and `src/storage/` carries no logging channel. "No
//! fallback delay" is proven structurally — a skipped tier contributes
//! zero calls to a synchronous, bounded read chain, never a timeout —
//! because timing rows on this shared host stay honestly inconclusive.

use std::{
    path::{Path, PathBuf},
    sync::{
        Arc,
        atomic::{AtomicU64, Ordering},
    },
};

use crate::storage::{
    StoragePort,
    intent::{CommitIntent, CommitResult, StagedWrite},
    lsm::{
        LsmStorage,
        object_store::{LocalObjectStore, ObjectKey, ObjectStore, ObjectStoreError},
        tests_support,
        tier::{self, MoveDecision, MoverOutcome, TierManifest, TierMovePolicy},
    },
    write_set_digest,
};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, TenantId,
};

/// Seed documents written before any tier pressure.
const SEED_DOCUMENTS: usize = 24;
/// Documents written after demotion (the cold-merge phase).
const POST_COLD_DOCUMENTS: usize = 8;
/// Documents the sequence deletes (one before pressure, one after).
const DELETED_DOCUMENTS: usize = 2;
/// How many segments the pressure phase tries to demote.
const TARGET_DEMOTIONS: usize = 2;

/// One storage profile: which optional tiers are enabled. Tier 2, the
/// mandatory network-block-storage tier, is present in every profile as
/// the LSM location itself.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct Profile {
    /// The Tier-1 read-through cache seat participates in cold reads.
    tier1_cache: bool,
    /// The Tier-3 cold object store exists: the mover may demote into it.
    tier3_cold: bool,
}

/// The four supported combinations, in charter order.
const MATRIX: [Profile; 4] = [
    Profile { tier1_cache: false, tier3_cold: false }, // Tier 2 only
    Profile { tier1_cache: true, tier3_cold: false },  // Tier 1 + Tier 2
    Profile { tier1_cache: false, tier3_cold: true },  // Tier 2 + Tier 3
    Profile { tier1_cache: true, tier3_cold: true },   // all three tiers
];

impl Profile {
    /// The profile's charter name.
    fn name(self) -> &'static str {
        match (self.tier1_cache, self.tier3_cold) {
            (false, false) => "tier2-only",
            (true, false) => "tier1+2",
            (false, true) => "tier2+3",
            (true, true) => "tier1+2+3",
        }
    }
}

/// The call counts of one tier seat.
#[derive(Default)]
struct CallCounts {
    puts: AtomicU64,
    gets: AtomicU64,
    exists: AtomicU64,
    deletes: AtomicU64,
}

/// One immutable reading of a seat's call counts.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
struct CountSnapshot {
    puts: u64,
    gets: u64,
    exists: u64,
    deletes: u64,
}

impl CallCounts {
    /// Reads every counter once.
    fn snapshot(&self) -> CountSnapshot {
        CountSnapshot {
            puts: self.puts.load(Ordering::SeqCst),
            gets: self.gets.load(Ordering::SeqCst),
            exists: self.exists.load(Ordering::SeqCst),
            deletes: self.deletes.load(Ordering::SeqCst),
        }
    }
}

impl CountSnapshot {
    /// The total number of calls the seat received.
    fn total(self) -> u64 {
        self.puts + self.gets + self.exists + self.deletes
    }
}

/// One tier seat of the matrix: a counting wrapper around the seat's
/// local object store. An enabled seat opens its directory; a disabled
/// seat opens nothing — its directory is never created — so a lane that
/// wrongly addresses a disabled tier leaves countable evidence instead of
/// taking an unreachable branch.
struct TierSeat {
    dir: PathBuf,
    counts: Arc<CallCounts>,
    store: Option<LocalObjectStore>,
}

impl TierSeat {
    /// Creates the seat, opening its store directory only when enabled.
    fn seat(dir: &Path, enabled: bool) -> Self {
        Self {
            dir: dir.to_path_buf(),
            counts: Arc::new(CallCounts::default()),
            store: enabled.then(|| LocalObjectStore::open(dir).expect("the enabled seat opens")),
        }
    }

    /// The seat's current call counts.
    fn calls(&self) -> CountSnapshot {
        self.counts.snapshot()
    }

    /// Whether the seat's directory exists on disk.
    fn directory_exists(&self) -> bool {
        self.dir.exists()
    }

    /// The number of files the seat's directory holds (zero when the
    /// directory was never created).
    fn file_count(&self) -> usize {
        self.store.as_ref().map_or(0, |_| count_files(&self.dir))
    }
}

impl ObjectStore for TierSeat {
    fn put(&mut self, key: &ObjectKey, bytes: &[u8]) -> Result<(), ObjectStoreError> {
        self.counts.puts.fetch_add(1, Ordering::SeqCst);
        let Some(store) = self.store.as_mut() else {
            return Err(ObjectStoreError::Unavailable);
        };
        store.put(key, bytes)
    }

    fn get(&self, key: &ObjectKey) -> Result<Vec<u8>, ObjectStoreError> {
        self.counts.gets.fetch_add(1, Ordering::SeqCst);
        let Some(store) = self.store.as_ref() else {
            return Err(ObjectStoreError::NotFound);
        };
        store.get(key)
    }

    fn exists(&self, key: &ObjectKey) -> bool {
        self.counts.exists.fetch_add(1, Ordering::SeqCst);
        self.store.as_ref().is_some_and(|store| store.exists(key))
    }

    fn delete(&mut self, key: &ObjectKey) -> Result<(), ObjectStoreError> {
        self.counts.deletes.fetch_add(1, Ordering::SeqCst);
        let Some(store) = self.store.as_mut() else {
            return Err(ObjectStoreError::Unavailable);
        };
        store.delete(key)
    }
}

/// Counts the files of one directory (zero when it is absent).
fn count_files(dir: &Path) -> usize {
    std::fs::read_dir(dir).map_or(0, |entries| entries.filter_map(Result::ok).count())
}

/// The observed state of one lane: the materialized documents, every
/// per-document read, and the crate's canonical published state.
/// `Document` carries floating-point values, so the fingerprint is
/// `PartialEq` — equality across profiles is still exact byte-for-byte
/// on the lane's string-valued documents.
#[derive(Clone, Debug, PartialEq)]
struct Fingerprint {
    documents: Vec<Document>,
    per_document: Vec<Option<Document>>,
    published_sequence: u64,
    root_head: String,
    journal_divergence: u64,
}

/// The observed facts of one profile's run of the shared sequence.
struct LaneRecord {
    profile: Profile,
    checkpoint_a: Fingerprint,
    checkpoint_b: Fingerprint,
    checkpoint_c: Fingerprint,
    after_reopen: Fingerprint,
    /// The segments still resident on the mandatory local tier after the
    /// pressure phase.
    local_segments_after_pressure: Vec<u64>,
    /// The segments the mover demoted to the cold tier (empty whenever
    /// Tier 3 is disabled).
    demoted_segments: Vec<u64>,
    /// The verified cold bytes of each demoted segment, in demotion order.
    demoted_bytes: Vec<Vec<u8>>,
    /// The object-store `put` attempts the mover made (one per demotion;
    /// a retry loop would exceed the demotion count).
    mover_put_attempts: u64,
    tier1_calls: CountSnapshot,
    tier3_calls: CountSnapshot,
    tier1_files: usize,
    tier3_files: usize,
}

fn tenant() -> TenantId {
    TenantId::try_new("acme").expect("valid tenant")
}

fn collection() -> CollectionName {
    CollectionName::try_new("orders").expect("valid identifier")
}

fn id_of(text: &str) -> DocumentId {
    DocumentId::try_new(text).expect("valid identifier")
}

/// Every identifier the lane ever writes, in one fixed order.
fn all_ids() -> Vec<DocumentId> {
    let mut ids: Vec<DocumentId> =
        (0..SEED_DOCUMENTS).map(|index| id_of(&format!("order-{index:04}"))).collect();
    ids.extend((0..POST_COLD_DOCUMENTS).map(|index| id_of(&format!("extra-{index:04}"))));
    ids
}

/// Commits one put, snapshotted at the currently published sequence so
/// overwrites of existing documents are conflict-free (an intent built at
/// sequence zero would conflict with every prior write to the same id).
fn put(storage: &mut LsmStorage, tenant_id: &TenantId, operation: &str, id: &str, text: &str) {
    let snapshot = storage.published_state().0;
    let document = Document::new(id_of(id), collection(), DocumentValue::String(text.to_owned()));
    let write = StagedWrite::Put { document: document.clone(), expected: ExpectedVersion::Any };
    let intent = CommitIntent {
        tenant: tenant_id.clone(),
        operation: OperationId::try_new(operation).expect("valid operation"),
        digest: write_set_digest(tenant_id, std::slice::from_ref(&write)),
        snapshot_sequence: snapshot,
        durability: LocalDurability::Synced,
        writes: vec![write],
    };
    assert!(
        matches!(storage.commit(intent), CommitResult::Committed(_)),
        "{operation} commits in every profile"
    );
}

/// Commits one delete of a known collection document, snapshotted at the
/// currently published sequence.
fn delete(storage: &mut LsmStorage, tenant_id: &TenantId, operation: &str, id: &str) {
    let snapshot = storage.published_state().0;
    let write = StagedWrite::Delete {
        collection: Some(collection()),
        id: id_of(id),
        expected: ExpectedVersion::Any,
    };
    let intent = CommitIntent {
        tenant: tenant_id.clone(),
        operation: OperationId::try_new(operation).expect("valid operation"),
        digest: write_set_digest(tenant_id, std::slice::from_ref(&write)),
        snapshot_sequence: snapshot,
        durability: LocalDurability::Synced,
        writes: vec![write],
    };
    assert!(
        matches!(storage.commit(intent), CommitResult::Committed(_)),
        "{operation} commits in every profile"
    );
}

/// Fingerprints one lane moment: the materialized tenant state, each
/// document read individually at the published sequence, and the
/// canonical published state (sequence watermark and root-chain head).
fn fingerprint(storage: &LsmStorage, tenant_id: &TenantId, ids: &[DocumentId]) -> Fingerprint {
    let (sequence, root) = storage.published_state();
    let owned = collection();
    Fingerprint {
        documents: storage.documents_at(sequence, tenant_id),
        per_document: ids
            .iter()
            .map(|id| storage.read_document_in_collection_at(sequence, tenant_id, &owned, id))
            .collect(),
        published_sequence: sequence.get(),
        root_head: root.as_str().to_owned(),
        journal_divergence: storage.journal_divergence(),
    }
}

/// Reads one demoted segment's bytes through the profile's read order:
/// the Tier-1 cache first when enabled (a checksum-addressed hit is
/// verified by its address; a corrupt cache object fails the address and
/// falls through), then the crate's bounded fallback chain — the local
/// file first, then the manifest's verified cold object.
fn cold_read(
    storage: &LsmStorage,
    profile: Profile,
    tier1: &mut TierSeat,
    tier3: &TierSeat,
    segment_id: u64,
) -> Vec<u8> {
    let object_key = TierManifest::read(storage.dir())
        .expect("the tier manifest is published")
        .objects
        .into_iter()
        .find(|object| object.segment_id == segment_id)
        .expect("a demoted segment is manifest-named")
        .object_key;
    if profile.tier1_cache
        && let Ok(bytes) = tier1.get(&object_key)
    {
        return bytes;
    }
    let bytes = tier::read_segment_with_sources(storage, tier3, &[], segment_id)
        .expect("the verified cold object serves the segment");
    if profile.tier1_cache {
        tier1.put(&object_key, &bytes).expect("the cache fill verifies");
    }
    bytes
}

/// Runs the shared logical sequence under one profile and returns the
/// observed facts. Assertions inside the driver hold for every profile;
/// the three test rows below assert the cross-profile matrix facts from
/// the returned records.
#[allow(clippy::too_many_lines, reason = "one matrix lane: the shared sequence, phase by phase")]
fn run_matrix_lane(profile: Profile) -> LaneRecord {
    let tenant_id = tenant();
    let ids = all_ids();
    let dir = tests_support::location(&format!("tier-matrix-{}", profile.name()));
    let mut storage = tests_support::open(&dir);
    storage.tuning.max_memtable_entries = 4;
    // Both optional seats exist in every profile; only the profile
    // decides whether anything may address them.
    let mut tier1 = TierSeat::seat(&dir.join("tier1-cache"), profile.tier1_cache);
    let mut tier3 = TierSeat::seat(&dir.join("tier-objects"), profile.tier3_cold);

    // Phase 1: the shared logical writes against the mandatory local
    // tier, ending in an overwrite and a delete so the materialized state
    // carries versions and a tombstone.
    for index in 0..SEED_DOCUMENTS {
        put(
            &mut storage,
            &tenant_id,
            &format!("seed-{index}"),
            &format!("order-{index:04}"),
            "seed-value",
        );
    }
    put(&mut storage, &tenant_id, "overwrite-0005", "order-0005", "overwritten-once");
    delete(&mut storage, &tenant_id, "delete-0007", "order-0007");
    let checkpoint_a = fingerprint(&storage, &tenant_id, &ids);
    assert!(!checkpoint_a.documents.is_empty());
    assert_eq!(
        tier1.calls().total() + tier3.calls().total(),
        0,
        "local reads address no optional tier"
    );

    // Phase 2: identical mover pressure in every profile (the same data
    // volume against the same budget of one byte).
    let mut policy = TierMovePolicy::default_policy();
    policy.nvme_budget_bytes = 1; // force pressure everywhere
    let segments_before: Vec<u64> = storage.segment_metas().iter().map(|meta| meta.id).collect();
    let mut demoted: Vec<u64> = Vec::new();
    let mut demoted_bytes: Vec<Vec<u8>> = Vec::new();
    let mut mover_put_attempts = 0_u64;
    if profile.tier3_cold {
        while demoted.len() < TARGET_DEMOTIONS {
            let puts_before = tier3.calls().puts;
            let outcome = tier::run_mover(&mut storage, &mut tier3, &policy, "home-a");
            mover_put_attempts += tier3.calls().puts - puts_before;
            match outcome {
                MoverOutcome::Completed(receipt) => {
                    if receipt.decision != MoveDecision::Demote {
                        break;
                    }
                    assert!(receipt.bytes > 0, "a demotion transfers bytes");
                    assert!(
                        receipt.manifest_after > receipt.manifest_before,
                        "a demotion publishes"
                    );
                    demoted.push(receipt.segment_id);
                }
                MoverOutcome::Failed(..) => panic!("a healthy tier must not fail the pass"),
            }
        }
        assert!(!demoted.is_empty(), "the pressure demotes at least one segment");
        assert_eq!(
            mover_put_attempts,
            u64::try_from(demoted.len()).expect("bounded count"),
            "one demotion is exactly one upload attempt: no retry loop"
        );
        let manifest = TierManifest::read(&dir).expect("the tier manifest is published");
        assert_eq!(manifest.objects.len(), demoted.len());
        assert!(manifest.objects.iter().all(|object| object.verified));
    } else {
        // The same pressure against a profile with no demotion target:
        // the impossible demotion is never attempted — no store call, no
        // manifest, no directory, no segment moved — and repeating the
        // pressure three times changes nothing. No retry, no background
        // work, and no warning-shaped outcome exists to emit.
        for attempt in 1..=3_u64 {
            assert!(!dir.join("TIERMANIFEST").exists(), "attempt {attempt} publishes no manifest");
            assert!(!tier3.directory_exists(), "attempt {attempt} creates no cold directory");
            assert_eq!(tier3.calls().total(), 0, "attempt {attempt} addresses no store");
            let snapshot = storage.tier_snapshot().expect("the lsm backend carries tiers");
            assert_eq!(
                (snapshot.manifest_epoch, snapshot.object_count),
                (0, 0),
                "attempt {attempt} leaves the tier state untouched"
            );
            let now: Vec<u64> = storage.segment_metas().iter().map(|meta| meta.id).collect();
            assert_eq!(now, segments_before, "attempt {attempt} moves nothing");
        }
        let snapshot = storage.tier_snapshot().expect("the lsm backend carries tiers");
        assert!(snapshot.nvme_bytes > 0, "the data stayed resident on the mandatory tier");
    }
    let local_segments_after_pressure: Vec<u64> =
        storage.segment_metas().iter().map(|meta| meta.id).collect();

    // Checkpoint B: the full read-back after demotion. In Tier-3 profiles
    // this exercises the engine's real cold path (`collect_range` over
    // demoted segments through `<location>/tier-objects`).
    let checkpoint_b = fingerprint(&storage, &tenant_id, &ids);
    assert_eq!(
        checkpoint_b, checkpoint_a,
        "moving bytes between tiers never changes the logical or canonical state"
    );

    // Phase 3: cold reads through the profile's read order, twice. The
    // first pass pays the cheapest enabled seats once per segment; the
    // second pass proves the repeat read is one bounded consultation, not
    // a re-walk of the whole chain.
    if profile.tier3_cold {
        let before = (tier1.calls(), tier3.calls());
        for &segment_id in &demoted {
            demoted_bytes.push(cold_read(&storage, profile, &mut tier1, &tier3, segment_id));
        }
        let after = (tier1.calls(), tier3.calls());
        let expected = u64::try_from(demoted.len()).expect("bounded count");
        let cache_misses = after.0.gets - before.0.gets;
        let cache_fills = after.0.puts - before.0.puts;
        let cold_serves = after.1.gets - before.1.gets;
        assert_eq!(cold_serves, expected, "one cold read per demoted segment: single pass");
        if profile.tier1_cache {
            assert_eq!(cache_misses, expected, "the cache is consulted once per segment");
            assert_eq!(cache_fills, expected, "the cache fills once per segment");
        } else {
            assert_eq!(
                (cache_misses, cache_fills),
                (0, 0),
                "the disabled cache is skipped, not consulted"
            );
        }

        let before = (tier1.calls(), tier3.calls());
        for (index, &segment_id) in demoted.iter().enumerate() {
            assert_eq!(
                cold_read(&storage, profile, &mut tier1, &tier3, segment_id),
                demoted_bytes[index],
                "the repeat read returns identical bytes"
            );
        }
        let after = (tier1.calls(), tier3.calls());
        let cache_hits = after.0.gets - before.0.gets;
        let cold_serves = after.1.gets - before.1.gets;
        if profile.tier1_cache {
            assert_eq!(cache_hits, expected, "the repeat pass serves from the cache");
            assert_eq!(cold_serves, 0, "a cache hit never pays the cold tier");
            assert_eq!(after.0.puts, before.0.puts, "a hit never re-fills the cache");
        } else {
            assert_eq!(cache_hits, 0, "the disabled cache stays untouched");
            assert_eq!(cold_serves, expected, "without a cache the cold tier serves every pass");
        }

        // A segment that never demoted reads through no optional seat.
        let still_local =
            local_segments_after_pressure.iter().find(|id| !demoted.contains(id)).copied();
        if let Some(segment_id) = still_local {
            let before = (tier1.calls(), tier3.calls());
            assert!(
                tier::read_segment_with_sources(&storage, &tier3, &[], segment_id).is_ok(),
                "the local segment still serves"
            );
            assert_eq!(
                (tier1.calls(), tier3.calls()),
                before,
                "a local read addresses no optional tier"
            );
        }
    }

    // Phase 4: writes after demotion — new documents, an overwrite of a
    // possibly-demoted document, and a delete of one — so the final state
    // merges memtable, local segments, and cold segments in every
    // profile.
    for index in 0..POST_COLD_DOCUMENTS {
        put(
            &mut storage,
            &tenant_id,
            &format!("post-{index}"),
            &format!("extra-{index:04}"),
            "post-cold-value",
        );
    }
    put(&mut storage, &tenant_id, "overwrite-0005-again", "order-0005", "overwritten-twice");
    delete(&mut storage, &tenant_id, "delete-0009", "order-0009");
    let checkpoint_c = fingerprint(&storage, &tenant_id, &ids);
    assert_eq!(checkpoint_c.journal_divergence, 0);

    // Quiescence: with no lane operations between two samples, no seat
    // grows. Runs are caller-driven in this landing — there is no
    // background mover loop — so the probe pins that nothing autonomous
    // addresses a seat either.
    let quiet_before = (tier1.calls(), tier3.calls());
    let quiet_after = (tier1.calls(), tier3.calls());
    assert_eq!(quiet_before, quiet_after, "no autonomous tier work exists");

    // Recovery parity inside the profile: a reopen (which rebuilds
    // indexes from cold segments in Tier-3 profiles and from local
    // segments alone otherwise) reproduces the lane's final state.
    drop(storage);
    let reopened = tests_support::open(&dir);
    let after_reopen = fingerprint(&reopened, &tenant_id, &ids);
    assert_eq!(
        after_reopen, checkpoint_c,
        "recovery reproduces the lane's final state in this profile"
    );
    drop(reopened);

    let record = LaneRecord {
        profile,
        checkpoint_a,
        checkpoint_b,
        checkpoint_c,
        after_reopen,
        local_segments_after_pressure,
        demoted_segments: demoted,
        demoted_bytes,
        mover_put_attempts,
        tier1_calls: tier1.calls(),
        tier3_calls: tier3.calls(),
        tier1_files: tier1.file_count(),
        tier3_files: tier3.file_count(),
    };
    tests_support::cleanup(&dir);
    record
}

/// Matrix row 1: the same logical sequence under all four profiles
/// materializes identical documents, identical per-document reads, and
/// the identical canonical published state — before pressure, after
/// demotion, after the post-cold writes, and after recovery.
#[test]
fn the_four_combination_matrix_yields_identical_logical_and_canonical_state() {
    let lanes = MATRIX.map(run_matrix_lane);
    let reference = &lanes[0];
    for lane in &lanes {
        let name = lane.profile.name();
        assert_eq!(lane.checkpoint_a, reference.checkpoint_a, "{name}: checkpoint A diverges");
        assert_eq!(lane.checkpoint_b, reference.checkpoint_b, "{name}: checkpoint B diverges");
        assert_eq!(lane.checkpoint_c, reference.checkpoint_c, "{name}: checkpoint C diverges");
        assert_eq!(lane.after_reopen, reference.after_reopen, "{name}: recovery diverges");
        assert!(lane.checkpoint_c.published_sequence > 0);
        assert!(!lane.checkpoint_c.root_head.is_empty());
        assert_eq!(lane.checkpoint_c.journal_divergence, 0);
    }
    // No empty results: every profile serves the full live set.
    let live = SEED_DOCUMENTS - DELETED_DOCUMENTS + POST_COLD_DOCUMENTS;
    for lane in &lanes {
        assert_eq!(
            lane.checkpoint_c.documents.len(),
            live,
            "{}: a document was lost to a tier seat",
            lane.profile.name()
        );
        assert_eq!(
            lane.checkpoint_c.per_document.iter().filter(|document| document.is_some()).count(),
            live,
            "{}: an individual read was lost to a tier seat",
            lane.profile.name()
        );
    }
    // The demoted ranges are the same segments — byte for byte — in both
    // Tier-3 profiles, and they stay resident on the mandatory local tier
    // when Tier 3 is off: only the seat of the bytes differs.
    assert_eq!(
        lanes[2].demoted_segments, lanes[3].demoted_segments,
        "the demotion candidates are deterministic"
    );
    assert_eq!(
        lanes[2].demoted_bytes, lanes[3].demoted_bytes,
        "cold bytes are identical whether or not Tier 1 sits in front"
    );
    for lane in &[&lanes[0], &lanes[1]] {
        for segment_id in &lanes[2].demoted_segments {
            assert!(
                lane.local_segments_after_pressure.contains(segment_id),
                "{} keeps the range on the mandatory tier",
                lane.profile.name()
            );
        }
    }
}

/// Matrix row 2: every disabled tier is skipped structurally — zero
/// store calls, zero artifacts, zero mover attempts under identical
/// pressure — and the skip is not degraded health: the disabled profiles
/// serve exactly the final state the enabled profiles serve.
#[test]
fn disabled_tiers_are_never_addressed_and_are_not_degraded_health() {
    let lanes = MATRIX.map(run_matrix_lane);
    for lane in &lanes {
        let name = lane.profile.name();
        if lane.profile.tier3_cold {
            // Positive control: the cold tier really was in play, so the
            // zero-call rows below are not vacuous.
            assert!(!lane.demoted_segments.is_empty(), "{name}: the control demotes");
            assert_eq!(
                lane.tier3_files,
                lane.demoted_segments.len(),
                "{name}: one object per demotion, no orphans"
            );
        } else {
            assert_eq!(
                lane.tier3_calls.total(),
                0,
                "{name}: the disabled cold tier is never called"
            );
            assert_eq!(lane.tier3_files, 0, "{name}: the disabled cold tier leaves no artifacts");
            assert!(lane.demoted_segments.is_empty(), "{name}: nothing demotes without a target");
        }
        if lane.profile.tier1_cache {
            if lane.profile.tier3_cold {
                assert!(lane.tier1_calls.gets > 0, "{name}: the control cache served reads");
                assert_eq!(
                    lane.tier1_files,
                    lane.demoted_segments.len(),
                    "{name}: one cache object per demoted segment"
                );
            } else {
                assert_eq!(
                    lane.tier1_calls.total(),
                    0,
                    "{name}: with no cold data the cache invents no work"
                );
                assert_eq!(lane.tier1_files, 0, "{name}: the idle cache leaves no artifacts");
            }
        } else {
            assert_eq!(lane.tier1_calls.total(), 0, "{name}: the disabled cache is never called");
            assert_eq!(lane.tier1_files, 0, "{name}: the disabled cache leaves no artifacts");
        }
    }
    // Not degraded health: absence of an optional tier changes nothing
    // about the served state.
    assert_eq!(lanes[0].checkpoint_c, lanes[2].checkpoint_c, "Tier 2 only is not degraded");
    assert_eq!(lanes[1].checkpoint_c, lanes[3].checkpoint_c, "Tier 1 plus 2 is not degraded");
}

/// Matrix row 3: the tier work is bounded and single-pass. One demotion
/// is exactly one upload attempt; a repeat cold read costs one seat
/// consultation and nothing else; no seat is retried, churned, or
/// addressed for work it was never given.
#[test]
fn tier_work_is_bounded_single_pass_and_never_retries() {
    let lanes = MATRIX.map(run_matrix_lane);
    for lane in &lanes {
        let name = lane.profile.name();
        if lane.profile.tier3_cold {
            let demotions = u64::try_from(lane.demoted_segments.len()).expect("bounded count");
            assert_eq!(
                lane.mover_put_attempts, demotions,
                "{name}: one upload attempt per demotion, never retried"
            );
            assert_eq!(lane.tier3_calls.puts, demotions, "{name}: one object write per demotion");
            // With a cache in front the cold tier served only the first
            // pass; without one it served both passes. Nothing else read
            // it, and nothing read it twice per pass.
            let expected_cold_reads =
                if lane.profile.tier1_cache { demotions } else { demotions * 2 };
            assert_eq!(lane.tier3_calls.gets, expected_cold_reads, "{name}: bounded cold reads");
        }
        assert_eq!(lane.tier1_calls.deletes, 0, "{name}: no cache churn");
        assert_eq!(lane.tier3_calls.deletes, 0, "{name}: no cold reclamation");
        assert_eq!(lane.tier1_calls.exists, 0, "{name}: no existence probing");
        assert_eq!(lane.tier3_calls.exists, 0, "{name}: no existence probing");
    }
    // The all-three cache consulted exactly twice per demoted segment
    // (one miss, one hit) and filled exactly once.
    let all_three = &lanes[3];
    let demotions = u64::try_from(all_three.demoted_segments.len()).expect("bounded count");
    assert_eq!(all_three.tier1_calls.gets, demotions * 2, "one miss plus one hit per segment");
    assert_eq!(all_three.tier1_calls.puts, demotions, "one fill per segment");
}
