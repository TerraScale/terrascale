//! Tiered storage: manifest, mover policy, receipts, and fallback reads
//! (P1; `docs/design/p0-storage-tiering-contract.md`, ADR 0016 D-4 /
//! ADR 0017).
//!
//! The mover transfers immutable SEGMENTS to the object-store tier, never
//! one object per document. A manifest is published only after every
//! object it names is uploaded and read-after-write verified; before the
//! last `NVMe` copy of a cold range is reclaimed the manifest names a
//! verified fallback source. One verified home region satisfies placement
//! (ADR 0017 D-1).
//!
//! Honest limit of this first landing: manifest publication is a local
//! durable acceptance. The `RegionalQuorum` acceptance the contract
//! requires rides the replication layer (P2) — the hook is the
//! `manifest_accepted` receipt field, wired when members can vote. The
//! operator surface (typed explain/dry-run/pause/resume administration)
//! lands with P4's TBEP admin operations.
#![allow(dead_code, reason = "P2 quorum acceptance and P4 typed administration wire the callers")]

use std::path::Path;

use super::{
    LsmStorage, codec,
    manifest::SegmentManifest,
    object_store::{ObjectKey, ObjectStore, ObjectStoreError},
    segment::SegmentMeta,
};

/// The tier-manifest magic.
const TIER_MANIFEST_MAGIC: &[u8; 8] = b"TBLSMTI1";

/// One cold-object entry of the tier manifest.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct TierObject {
    /// The segment the object holds.
    pub(crate) segment_id: u64,
    /// The segment's merge level.
    pub(crate) level: u16,
    /// The checksum address of the object's bytes.
    pub(crate) object_key: ObjectKey,
    /// The object's byte length.
    pub(crate) byte_len: u64,
    /// Upload and read-after-write verification both completed.
    pub(crate) verified: bool,
}

/// The published tier manifest (contract §1).
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub(crate) struct TierManifest {
    /// Monotonic publication epoch.
    pub(crate) epoch: u64,
    /// The one verified home region.
    pub(crate) home_region: String,
    /// The cold objects the manifest names.
    pub(crate) objects: Vec<TierObject>,
    /// The manifest's publication epoch predecessor.
    pub(crate) previous_epoch: Option<u64>,
}

impl TierManifest {
    /// Encodes the manifest with a trailing checksum.
    fn encode(&self) -> Vec<u8> {
        let mut body = Vec::new();
        body.extend_from_slice(TIER_MANIFEST_MAGIC);
        codec::push_u64(&mut body, self.epoch);
        codec::push_str(&mut body, &self.home_region);
        codec::push_u64(&mut body, self.previous_epoch.unwrap_or(0));
        #[allow(clippy::cast_possible_truncation)]
        let count = self.objects.len() as u32;
        codec::push_u32(&mut body, count);
        for object in &self.objects {
            codec::push_u64(&mut body, object.segment_id);
            codec::push_u16(&mut body, object.level);
            codec::push_str(&mut body, object.object_key.as_str());
            codec::push_u64(&mut body, object.byte_len);
            codec::push_u8(&mut body, u8::from(object.verified));
        }
        let mut out = body.clone();
        codec::push_u64(&mut out, codec::fnv1a(&body));
        out
    }

    /// Decodes and verifies one manifest body.
    fn decode(bytes: &[u8]) -> Option<Self> {
        if bytes.len() < 8 + 8 + 8 + 8 + 8 || &bytes[..8] != TIER_MANIFEST_MAGIC {
            return None;
        }
        let body_end = bytes.len() - 8;
        let expected = codec::fnv1a(&bytes[..body_end]);
        let stored = u64::from_le_bytes(bytes[body_end..].try_into().expect("eight bytes"));
        if expected != stored {
            return None;
        }
        let mut reader = codec::Reader::new(&bytes[8..body_end]);
        let epoch = reader.read_u64()?;
        let home_region = reader.read_string()?;
        let previous_raw = reader.read_u64()?;
        let count = reader.read_u32()? as usize;
        let mut objects = Vec::with_capacity(count.min(1024));
        for _ in 0..count {
            let segment_id = reader.read_u64()?;
            let level = reader.read_u16()?;
            let key = reader.read_string()?;
            let byte_len = reader.read_u64()?;
            let verified = reader.read_u8()? != 0;
            objects.push(TierObject {
                segment_id,
                level,
                object_key: ObjectKey::from_recorded(&key),
                byte_len,
                verified,
            });
        }
        if reader.remaining() != 0 {
            return None;
        }
        Some(Self {
            epoch,
            home_region,
            objects,
            previous_epoch: (previous_raw > 0).then_some(previous_raw),
        })
    }

    /// Publishes the manifest atomically at `dir/TIERMANIFEST`.
    pub(crate) fn publish(&self, dir: &Path) -> Result<(), ObjectStoreError> {
        let encoded = self.encode();
        let tmp = dir.join("TIERMANIFEST.tmp");
        let live = dir.join("TIERMANIFEST");
        std::fs::write(&tmp, &encoded).map_err(|_| ObjectStoreError::Unavailable)?;
        if let Ok(file) = std::fs::File::open(&tmp) {
            let _ = file.sync_all();
        }
        std::fs::rename(&tmp, &live).map_err(|_| ObjectStoreError::Unavailable)?;
        if let Ok(dir_handle) = std::fs::File::open(dir) {
            let _ = dir_handle.sync_all();
        }
        Ok(())
    }

    /// Reads and verifies the manifest at `dir/TIERMANIFEST`.
    #[must_use]
    pub(crate) fn read(dir: &Path) -> Option<Self> {
        let bytes = std::fs::read(dir.join("TIERMANIFEST")).ok()?;
        Self::decode(&bytes)
    }
}

/// One versioned tier-move policy (contract §2).
///
/// Policies inherit cluster → tenant → database → collection; the most
/// specific wins. Every field is a bounded, explainable input — no opaque
/// model.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct TierMovePolicy {
    /// The policy's stable identity.
    pub(crate) policy_id: String,
    /// The policy's version.
    pub(crate) version: u32,
    /// The `NVMe` byte budget this scope may consume before demotion starts.
    pub(crate) nvme_budget_bytes: u64,
    /// The minimum age (in move attempts) before a range is demotable.
    pub(crate) min_residence_moves: u32,
    /// The access-count threshold at which a cold range promotes back.
    pub(crate) promotion_threshold: u32,
    /// Ranges pinned hot never demote.
    pub(crate) pinned_segment_ids: Vec<u64>,
    /// A dry run decides and receipts without transferring anything.
    pub(crate) dry_run: bool,
}

impl TierMovePolicy {
    /// A default policy for tests and local deployments.
    #[must_use]
    pub(crate) fn default_policy() -> Self {
        Self {
            policy_id: "default".to_owned(),
            version: 1,
            nvme_budget_bytes: 64 << 20,
            min_residence_moves: 1,
            promotion_threshold: 2,
            pinned_segment_ids: Vec::new(),
            dry_run: false,
        }
    }
}

/// One machine-readable reason a move decision considered.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct MoveReason {
    /// The fact the policy weighed.
    pub(crate) fact: String,
    /// The value observed.
    pub(crate) observed: String,
}

/// The decision one mover run made for one segment.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) enum MoveDecision {
    /// The segment moved to the object-store tier.
    Promote,
    /// The segment moved from the object-store tier back to `NVMe`.
    Demote,
    /// Nothing moved for this segment.
    None,
    /// The policy refused the move (residence, pin, budget, or
    /// hysteresis).
    Refused,
}

/// One tier-move receipt (contract §3).
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct TierMoveReceipt {
    /// The exact effective policy.
    pub(crate) policy_id: String,
    /// The policy's version.
    pub(crate) policy_version: u32,
    /// The decision for the segment this run considered.
    pub(crate) decision: MoveDecision,
    /// The ordered reasons the policy weighed.
    pub(crate) reasons: Vec<MoveReason>,
    /// The segment the run considered.
    pub(crate) segment_id: u64,
    /// Bytes the run transferred (0 for dry runs and refusals).
    pub(crate) bytes: u64,
    /// The manifest epochs before and after the run.
    pub(crate) manifest_before: u64,
    /// The manifest epoch after the run (equal on failure or refusal).
    pub(crate) manifest_after: u64,
    /// Whether the run was a dry run.
    pub(crate) dry_run: bool,
    /// Whether the published manifest was accepted (the `RegionalQuorum`
    /// hook; single-node acceptance until P2 wires member votes).
    pub(crate) manifest_accepted: bool,
}

/// The outcome of one mover run.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) enum MoverOutcome {
    /// The run completed and produced its receipt.
    Completed(TierMoveReceipt),
    /// The run failed; the receipt names the stage.
    Failed(TierMoveReceipt, ObjectStoreError),
}

/// The backend carries no tiers (the root-file engine under P4's tier
/// administration): the typed refusal the port answers.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct TierUnsupported;

/// The raw tier snapshot of one LSM location: the manifest epoch, the
/// verified objects it names, and the resident `NVMe` bytes.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct TierSnapshot {
    /// The published tier-manifest epoch.
    pub(crate) manifest_epoch: u64,
    /// The verified objects the manifest names.
    pub(crate) object_count: usize,
    /// The bytes resident on `NVMe`.
    pub(crate) nvme_bytes: u64,
}

/// Reads one segment's raw bytes from its `NVMe` file.
fn segment_bytes(dir: &Path, meta: &SegmentMeta) -> Option<Vec<u8>> {
    std::fs::read(super::segment_path(dir, meta.id)).ok()
}

/// Runs one bounded mover pass: demote the coldest segment that exceeds
/// the policy's `NVMe` budget, never a pinned one.
///
/// The pass uploads the segment's bytes, verifies them read-after-write
/// (inside [`ObjectStore::put`]), publishes the next tier-manifest epoch,
/// and only then reclaims the `NVMe` copy — the manifest names the verified
/// object as the fallback source for the range (contract §1, §4).
#[allow(clippy::too_many_lines, reason = "one bounded pass, the contract phase order in sequence")]
pub(crate) fn run_mover(
    storage: &mut LsmStorage,
    store: &mut dyn ObjectStore,
    policy: &TierMovePolicy,
    home_region: &str,
) -> MoverOutcome {
    let manifest_before = TierManifest::read(storage.dir()).map_or(0, |manifest| manifest.epoch);
    let mut reasons: Vec<MoveReason> = Vec::new();
    let nvme_bytes: u64 = storage.segment_metas().iter().map(|meta| meta.byte_len).sum();
    reasons.push(MoveReason {
        fact: "nvme_resident_bytes".to_owned(),
        observed: nvme_bytes.to_string(),
    });
    reasons.push(MoveReason {
        fact: "nvme_budget_bytes".to_owned(),
        observed: policy.nvme_budget_bytes.to_string(),
    });
    if nvme_bytes <= policy.nvme_budget_bytes {
        return MoverOutcome::Completed(TierMoveReceipt {
            policy_id: policy.policy_id.clone(),
            policy_version: policy.version,
            decision: MoveDecision::None,
            reasons,
            segment_id: 0,
            bytes: 0,
            manifest_before,
            manifest_after: manifest_before,
            dry_run: policy.dry_run,
            manifest_accepted: false,
        });
    }
    // Candidates: unpinned segments, coldest (highest id = oldest merge
    // output; level-1 before level-0 keeps hot data local).
    let mut candidates: Vec<SegmentMeta> = storage
        .segment_metas()
        .iter()
        .filter(|meta| !policy.pinned_segment_ids.contains(&meta.id))
        .cloned()
        .collect();
    candidates.sort_by_key(|meta| (std::cmp::Reverse(meta.level), std::cmp::Reverse(meta.id)));
    if candidates.is_empty() {
        reasons.push(MoveReason {
            fact: "no_unpinned_candidate".to_owned(),
            observed: "0".to_owned(),
        });
        return MoverOutcome::Completed(TierMoveReceipt {
            policy_id: policy.policy_id.clone(),
            policy_version: policy.version,
            decision: MoveDecision::Refused,
            reasons,
            segment_id: 0,
            bytes: 0,
            manifest_before,
            manifest_after: manifest_before,
            dry_run: policy.dry_run,
            manifest_accepted: false,
        });
    }
    let candidate = candidates[0].clone();
    reasons.push(MoveReason {
        fact: "candidate_segment".to_owned(),
        observed: format!("id={} level={}", candidate.id, candidate.level),
    });
    if policy.dry_run {
        return MoverOutcome::Completed(TierMoveReceipt {
            policy_id: policy.policy_id.clone(),
            policy_version: policy.version,
            decision: MoveDecision::Demote,
            reasons,
            segment_id: candidate.id,
            bytes: 0,
            manifest_before,
            manifest_after: manifest_before,
            dry_run: true,
            manifest_accepted: false,
        });
    }
    let Some(bytes) = segment_bytes(storage.dir(), &candidate) else {
        return MoverOutcome::Failed(
            failed_receipt(policy, &candidate, manifest_before, "segment_read"),
            ObjectStoreError::Unavailable,
        );
    };
    let key = ObjectKey::of(&bytes);
    if let Err(error) = store.put(&key, &bytes) {
        return MoverOutcome::Failed(
            failed_receipt(policy, &candidate, manifest_before, "object_put"),
            error,
        );
    }
    // Publish the next manifest epoch naming the verified object.
    let mut manifest = TierManifest::read(storage.dir()).unwrap_or_default();
    manifest.epoch += 1;
    home_region.clone_into(&mut manifest.home_region);
    manifest.previous_epoch = (manifest_before > 0).then_some(manifest_before);
    manifest.objects.push(TierObject {
        segment_id: candidate.id,
        level: candidate.level,
        object_key: key,
        byte_len: bytes.len() as u64,
        verified: true,
    });
    if let Err(error) = manifest.publish(storage.dir()) {
        return MoverOutcome::Failed(
            failed_receipt(policy, &candidate, manifest_before, "manifest_publish"),
            error,
        );
    }
    // The manifest names the verified object as the fallback source; the
    // `NVMe` copy may now be reclaimed. The manifest's acceptance is local
    // until P2 wires the `RegionalQuorum` vote.
    let reclaimed = storage.demote_segment(candidate.id);
    MoverOutcome::Completed(TierMoveReceipt {
        policy_id: policy.policy_id.clone(),
        policy_version: policy.version,
        decision: MoveDecision::Demote,
        reasons,
        segment_id: candidate.id,
        bytes: if reclaimed { bytes.len() as u64 } else { 0 },
        manifest_before,
        manifest_after: manifest.epoch,
        dry_run: false,
        manifest_accepted: true,
    })
}

/// Builds the receipt of a failed run: the DEMOTE it was attempting,
/// the candidate, the named failed stage, and the manifest untouched —
/// the failure receipt names the stage (tiering contract §3).
fn failed_receipt(
    policy: &TierMovePolicy,
    candidate: &SegmentMeta,
    before: u64,
    stage: &str,
) -> TierMoveReceipt {
    TierMoveReceipt {
        policy_id: policy.policy_id.clone(),
        policy_version: policy.version,
        decision: MoveDecision::Demote,
        reasons: vec![
            MoveReason { fact: "failed_stage".to_owned(), observed: stage.to_owned() },
            MoveReason {
                fact: "candidate_segment".to_owned(),
                observed: format!("id={} level={}", candidate.id, candidate.level),
            },
        ],
        segment_id: candidate.id,
        bytes: 0,
        manifest_before: before,
        manifest_after: before,
        dry_run: false,
        manifest_accepted: false,
    }
}

/// Serves one segment's bytes in the frozen fallback order (contract §5):
/// local `NVMe` first, then the home object store.
///
/// Active-Region QUIC sources and the backup cluster join the order when
/// the replication layer exists (P2); this landing proves the local legs
/// and the typed exhaustion error.
pub(crate) fn read_segment_with_fallback(
    storage: &LsmStorage,
    store: &dyn ObjectStore,
    segment_id: u64,
) -> Result<Vec<u8>, ObjectStoreError> {
    // 1. Local `NVMe`.
    if let Some(meta) = storage.segment_metas().iter().find(|meta| meta.id == segment_id)
        && let Ok(bytes) = std::fs::read(super::segment_path(storage.dir(), meta.id))
    {
        return Ok(bytes);
    }
    // 2. Home object store, through the manifest's verified reference.
    if let Some(manifest) = TierManifest::read(storage.dir())
        && let Some(object) = manifest.objects.iter().find(|object| object.segment_id == segment_id)
    {
        return store.get(&object.object_key);
    }
    Err(ObjectStoreError::NotFound)
}

/// Reports the segment metadata the mover considered (crate-private
/// accessor; the segment list is the mover's candidate set).
impl LsmStorage {
    /// The live segment metadata list.
    pub(super) fn segment_metas(&self) -> &[SegmentMeta] {
        &self.manifest.segments
    }

    /// Removes one segment from the active set and deletes its `NVMe` file
    /// after the tier manifest names its verified object. Returns whether
    /// the copy was reclaimed.
    pub(crate) fn demote_segment(&mut self, segment_id: u64) -> bool {
        let before = self.manifest.segments.len();
        self.manifest.segments.retain(|meta| meta.id != segment_id);
        self.readers.remove(&segment_id);
        let path = super::segment_path(&self.dir, segment_id);
        match std::fs::remove_file(&path) {
            Ok(()) => {}
            Err(_) if !path.exists() => {}
            Err(_) => return false,
        }
        // Keep the naming manifest consistent with the reclaim.
        let mut manifest: SegmentManifest = self.manifest.clone();
        manifest.epoch += 1;
        if manifest.publish(&self.dir).is_ok() {
            self.manifest = manifest;
        }
        self.manifest.segments.len() < before || !path.exists()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::storage::lsm::{object_store::LocalObjectStore, tests_support};

    #[test]
    fn mover_demotes_publishes_and_serves_from_the_object_store() {
        let dir = tests_support::location("tier-demote");
        let mut storage = tests_support::open(&dir);
        tests_support::seed_segments(&mut storage, 8);
        let object_dir = dir.join("objects");
        let mut store = LocalObjectStore::open(&object_dir).unwrap();
        let mut policy = TierMovePolicy::default_policy();
        policy.nvme_budget_bytes = 1; // force pressure
        let outcome = run_mover(&mut storage, &mut store, &policy, "home-a");
        let MoverOutcome::Completed(receipt) = outcome else {
            panic!("the mover completes");
        };
        assert_eq!(receipt.decision, MoveDecision::Demote);
        assert!(receipt.bytes > 0);
        assert!(receipt.manifest_after > receipt.manifest_before);
        assert!(receipt.manifest_accepted);
        // The demoted segment still serves, from the object store.
        let bytes = read_segment_with_fallback(&storage, &store, receipt.segment_id);
        assert!(bytes.is_ok());
        tests_support::cleanup(&dir);
    }

    #[test]
    fn dry_run_decides_without_transferring() {
        let dir = tests_support::location("tier-dry");
        let mut storage = tests_support::open(&dir);
        tests_support::seed_segments(&mut storage, 8);
        let object_dir = dir.join("objects");
        let mut store = LocalObjectStore::open(&object_dir).unwrap();
        let mut policy = TierMovePolicy::default_policy();
        policy.nvme_budget_bytes = 1;
        policy.dry_run = true;
        let MoverOutcome::Completed(receipt) =
            run_mover(&mut storage, &mut store, &policy, "home-a")
        else {
            panic!("the dry run completes");
        };
        assert_eq!(receipt.decision, MoveDecision::Demote);
        assert_eq!(receipt.bytes, 0, "a dry run transfers nothing");
        assert_eq!(receipt.manifest_after, receipt.manifest_before);
        // Nothing was demoted: every segment still serves locally.
        let metas: Vec<u64> = storage.segment_metas().iter().map(|meta| meta.id).collect();
        assert!(!metas.is_empty());
        tests_support::cleanup(&dir);
    }

    #[test]
    fn below_budget_moves_nothing() {
        let dir = tests_support::location("tier-quiet");
        let mut storage = tests_support::open(&dir);
        tests_support::seed_segments(&mut storage, 2);
        let object_dir = dir.join("objects");
        let mut store = LocalObjectStore::open(&object_dir).unwrap();
        let policy = TierMovePolicy::default_policy();
        let MoverOutcome::Completed(receipt) =
            run_mover(&mut storage, &mut store, &policy, "home-a")
        else {
            panic!("the quiet run completes");
        };
        assert_eq!(receipt.decision, MoveDecision::None);
        assert_eq!(receipt.bytes, 0);
        tests_support::cleanup(&dir);
    }

    #[test]
    fn pinned_segments_are_refused() {
        let dir = tests_support::location("tier-pin");
        let mut storage = tests_support::open(&dir);
        tests_support::seed_segments(&mut storage, 8);
        let ids: Vec<u64> = storage.segment_metas().iter().map(|meta| meta.id).collect();
        let object_dir = dir.join("objects");
        let mut store = LocalObjectStore::open(&object_dir).unwrap();
        let mut policy = TierMovePolicy::default_policy();
        policy.nvme_budget_bytes = 1;
        policy.pinned_segment_ids = ids;
        let MoverOutcome::Completed(receipt) =
            run_mover(&mut storage, &mut store, &policy, "home-a")
        else {
            panic!("the pinned run completes");
        };
        assert_eq!(receipt.decision, MoveDecision::Refused);
        tests_support::cleanup(&dir);
    }

    #[test]
    fn tier_manifest_round_trips_and_rejects_corruption() {
        let dir = tests_support::location("tier-manifest");
        std::fs::create_dir_all(&dir).unwrap();
        let manifest = TierManifest {
            epoch: 4,
            home_region: "home-a".to_owned(),
            objects: vec![TierObject {
                segment_id: 9,
                level: 1,
                object_key: ObjectKey::of(b"bytes"),
                byte_len: 5,
                verified: true,
            }],
            previous_epoch: Some(3),
        };
        manifest.publish(&dir).unwrap();
        assert_eq!(TierManifest::read(&dir), Some(manifest));
        let path = dir.join("TIERMANIFEST");
        let mut bytes = std::fs::read(&path).unwrap();
        let mid = bytes.len() / 2;
        bytes[mid] ^= 0xFF;
        std::fs::write(&path, &bytes).unwrap();
        assert_eq!(TierManifest::read(&dir), None);
        tests_support::cleanup(&dir);
    }

    #[test]
    fn exhaustion_is_typed_after_every_local_source_fails() {
        let dir = tests_support::location("tier-miss");
        let storage = tests_support::open(&dir);
        let object_dir = dir.join("objects");
        let store = LocalObjectStore::open(&object_dir).unwrap();
        assert_eq!(
            read_segment_with_fallback(&storage, &store, 4242),
            Err(ObjectStoreError::NotFound)
        );
        tests_support::cleanup(&dir);
    }
}

/// The P7 tier-failure lane: the storage-tiering contract §4 failure
/// table, as focused rows.
#[cfg(test)]
mod failure_lane {
    use super::*;
    use crate::storage::lsm::{object_store::LocalObjectStore, tests_support};

    /// An object store that cannot write: the remote tier is down.
    struct UnavailableStore;

    impl ObjectStore for UnavailableStore {
        fn put(&mut self, _key: &ObjectKey, _bytes: &[u8]) -> Result<(), ObjectStoreError> {
            Err(ObjectStoreError::Unavailable)
        }
        fn get(&self, _key: &ObjectKey) -> Result<Vec<u8>, ObjectStoreError> {
            Err(ObjectStoreError::NotFound)
        }
        fn exists(&self, _key: &ObjectKey) -> bool {
            false
        }
        fn delete(&mut self, _key: &ObjectKey) -> Result<(), ObjectStoreError> {
            Err(ObjectStoreError::Unavailable)
        }
    }

    #[test]
    fn an_unavailable_object_store_fails_typed_with_the_stage_receipt() {
        let dir = tests_support::location("tier-fail-unavailable");
        let mut storage = tests_support::open(&dir);
        tests_support::seed_segments(&mut storage, 8);
        let mut policy = TierMovePolicy::default_policy();
        policy.nvme_budget_bytes = 1; // force pressure
        let mut store = UnavailableStore;
        let MoverOutcome::Failed(receipt, error) =
            run_mover(&mut storage, &mut store, &policy, "home-a")
        else {
            panic!("the unavailable store fails the pass");
        };
        assert_eq!(error, ObjectStoreError::Unavailable);
        // The receipt names the failed stage: the candidate, the
        // decision it was attempting, and the manifest untouched.
        assert_eq!(receipt.decision, MoveDecision::Demote);
        assert!(receipt.segment_id > 0);
        assert!(
            receipt
                .reasons
                .iter()
                .any(|reason| reason.fact == "failed_stage" && reason.observed == "object_put"),
            "the stage is named: {:?}",
            receipt.reasons
        );
        assert_eq!(receipt.bytes, 0, "nothing transferred");
        assert_eq!(receipt.manifest_after, receipt.manifest_before, "no epoch moved");
        assert!(!receipt.manifest_accepted);
        // The NVMe copy stands: nothing was demoted behind the failure.
        assert!(!storage.segment_metas().is_empty());
        tests_support::cleanup(&dir);
    }

    #[test]
    fn a_corrupt_object_is_never_served_but_quarantined_by_address() {
        let dir = tests_support::location("tier-fail-corrupt");
        let mut storage = tests_support::open(&dir);
        tests_support::seed_segments(&mut storage, 8);
        let object_dir = dir.join("objects");
        let mut store = LocalObjectStore::open(&object_dir).unwrap();
        let mut policy = TierMovePolicy::default_policy();
        policy.nvme_budget_bytes = 1;
        let MoverOutcome::Completed(receipt) =
            run_mover(&mut storage, &mut store, &policy, "home-a")
        else {
            panic!("the demote completes");
        };
        // The manifest references only the verified object.
        let manifest = TierManifest::read(&dir).expect("published");
        let entry = manifest
            .objects
            .iter()
            .find(|object| object.segment_id == receipt.segment_id)
            .expect("referenced");
        assert!(entry.verified);

        // Corrupt the stored bytes behind the manifest's back: the
        // checksum address no longer matches, so the read refuses
        // typed — never serving the corrupt bytes (contract §4).
        let path = object_dir.join(entry.object_key.as_str());
        let mut bytes = std::fs::read(&path).unwrap();
        bytes[0] ^= 0xFF;
        std::fs::write(&path, bytes).unwrap();
        assert_eq!(
            store.get(&entry.object_key).err(),
            Some(ObjectStoreError::ChecksumMismatch),
            "the corrupt object is quarantined by its address"
        );
        tests_support::cleanup(&dir);
    }

    #[test]
    fn a_torn_upload_is_orphaned_and_never_referenced() {
        let dir = tests_support::location("tier-fail-torn");
        let mut storage = tests_support::open(&dir);
        tests_support::seed_segments(&mut storage, 8);
        let object_dir = dir.join("objects");
        let mut store = LocalObjectStore::open(&object_dir).unwrap();
        let mut policy = TierMovePolicy::default_policy();
        policy.nvme_budget_bytes = 1;
        let MoverOutcome::Completed(_) = run_mover(&mut storage, &mut store, &policy, "home-a")
        else {
            panic!("the demote completes");
        };
        // A torn upload: a partial object in the store under no key the
        // manifest references. It can never be served — the manifest
        // is the only source of truth for fallback reads.
        let manifest = TierManifest::read(&dir).expect("published");
        std::fs::write(object_dir.join("orphan-partial"), b"half an object").unwrap();
        for object in &manifest.objects {
            assert_ne!(object.object_key.as_str(), "orphan-partial");
            assert!(store.exists(&object.object_key), "only verified objects are referenced");
        }
        assert!(
            !store.exists(&ObjectKey::of(b"half an object")),
            "the orphan is unreachable by any verified address"
        );
        tests_support::cleanup(&dir);
    }
}
