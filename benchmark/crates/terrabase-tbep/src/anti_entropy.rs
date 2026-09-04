//! Journal-independent catch-up over the Merkle kernels (S-05 slice 2e,
//! D-45): state comparison, bounded repair, and snapshot bootstrap.
//!
//! The journal tailer ([`crate::replication`], [`crate::replication_lanes`])
//! converges members while the publication journal still holds the writes.
//! This module owns what happens when it cannot: the cursor is past
//! retention, the journal never saw the writes (a partition predates the
//! tailer), or a member restarts so far behind that replaying is the wrong
//! shape. The decision surfaces are the kept
//! `terrabase-replication-core` kernels — [`MerkleTree`] build, roots, and
//! mismatch planning over canonical entries — while the bytes compared and
//! shipped come from the engines themselves:
//!
//! - **State scan**: every live document plus its replication stamp (the
//!   flagged [`Engine::document_replication_stamp`](terrabase::engine::Engine::document_replication_stamp)
//!   port — the same `(hlc, origin)` pair the deterministic merge compares,
//!   so the Merkle causal bytes and the merge order can never disagree)
//!   becomes one [`MerkleEntry`]: the key is `collection 0x00 id`, the
//!   causal bytes fold `(hlc, origin, deleted)`, and the materialized bytes
//!   are the deterministic value encoding below.
//! - **Bounded repair**: [`repair_once`] plans with the kernels'
//!   [`plan_repair`](terrabase_replication_core::anti_entropy::MerkleTree::plan_repair),
//!   verifies the chunk transfer against the expected roots before
//!   publishing anything, then ships each mismatched key's winning version
//!   through [`Engine::apply_replicated`](terrabase::engine::Engine::apply_replicated)
//!   — the `(hlc, origin)` order with remove-wins picks the same winner the
//!   merge would, so every key publishes exactly once and both members
//!   converge without coordination. Divergence past the range budget is not
//!   repaired; the caller bootstraps instead ([`RepairDecision::NeedsSnapshot`]).
//! - **Snapshot bootstrap**: [`snapshot_bootstrap`] copies the peer's
//!   quiesced durable file set into a fresh location — the in-process form
//!   of installing the region-local Litestream snapshot (production reads
//!   the snapshot artifact from its own region's object storage via the
//!   S-06 `BackupStreams::restore_to_location` path, which lands the
//!   identical file set). The tailer's cursor resumes from
//!   [`journal_end`] past the snapshot, so pre-snapshot rows never re-ship.
//!
//! Composition barriers, stated exactly:
//!
//! - Repair and bootstrap assume quiescent members — no commit wave in
//!   flight on either side — the same barrier the WAL fold and the
//!   follower bootstrap already take. A write landing mid-repair fails the
//!   closing roots check and the caller retries the pass.
//! - Tombstone-tombstone stamp divergence is invisible and needs no
//!   repair: both sides serve absence, and every future origin tick
//!   dominates both stamps. Only tombstone-vs-live and live-vs-live
//!   differences ship.
//! - An exact `(hlc, origin)` tie with different bodies resolves
//!   remove-wins per side, which is order-dependent when both sides are
//!   live. Sequential and replicate-then-write patterns never tie; truly
//!   concurrent same-stamp writes to one key need distinct origins to
//!   converge deterministically.

use std::collections::BTreeMap;
use std::path::Path;

use terrabase::engine::ReplicatedRecord;
use terrabase::streaming::ChangeCheckpoint;
use terrabase::types::{CollectionName, DocumentId, DocumentValue};
use terrabase_replication_core::anti_entropy::{MerkleEntry, MerkleTree, RepairError};

use crate::engine_host::EngineHost;
use crate::host::HostingError;
use crate::registry::BoundScope;

/// The bounded count of key ranges one repair pass may ship. Past this
/// budget the members diverge by more than a catch-up pass should move and
/// the caller bootstraps from a snapshot instead.
pub const MAX_REPAIR_RANGES: usize = 64;

/// What one repair pass decided.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RepairDecision {
    /// Both members serve the identical state; `repaired` records published
    /// on this pass (replays and merge-superseded records count zero).
    Converged {
        /// Records this pass published.
        repaired: usize,
    },
    /// The divergence exceeds the range budget: bootstrap the lagging
    /// member from a snapshot ([`snapshot_bootstrap`]) instead of repairing.
    NeedsSnapshot {
        /// Differing ranges the plan found.
        ranges: usize,
        /// Budget the pass ran under.
        max_ranges: usize,
    },
}

/// One repair pass between two members of the same database: compare the
/// Merkle states, ship every mismatched key's winning version through the
/// deterministic merge, and confirm both members serve the identical
/// state afterwards.
///
/// # Errors
///
/// Returns [`HostingError`] when either state cannot be scanned, the chunk
/// transfer fails verification, a record cannot be applied, or a write
/// landed mid-repair (retry the pass).
pub fn repair_once(
    local: &EngineHost,
    peer: &EngineHost,
    scope: &BoundScope,
) -> Result<RepairDecision, HostingError> {
    repair_once_bounded(local, peer, scope, MAX_REPAIR_RANGES)
}

/// One repair pass under an explicit range budget.
///
/// # Errors
///
/// Returns [`HostingError::Invalid`] for a zero budget, and the failures
/// of [`repair_once`] otherwise.
pub fn repair_once_bounded(
    local: &EngineHost,
    peer: &EngineHost,
    scope: &BoundScope,
    max_ranges: usize,
) -> Result<RepairDecision, HostingError> {
    if max_ranges == 0 {
        return Err(HostingError::Invalid("the repair range budget must be nonzero".into()));
    }
    let local_state = member_state(local, scope)?;
    let peer_state = member_state(peer, scope)?;
    if local_state.tree.roots() == peer_state.tree.roots() {
        return Ok(RepairDecision::Converged { repaired: 0 });
    }
    let plan = match local_state.tree.plan_repair(&peer_state.tree, max_ranges) {
        Ok(plan) => plan,
        Err(RepairError::RepairBudgetExceeded { ranges, max_ranges }) => {
            return Ok(RepairDecision::NeedsSnapshot { ranges, max_ranges });
        }
        Err(error) => {
            return Err(HostingError::EngineFailure(format!(
                "the anti-entropy repair plan failed: {error:?}"
            )));
        }
    };
    let chunks = plan.source_chunks(&peer_state.tree).map_err(|error| {
        HostingError::EngineFailure(format!("the repair source changed under repair: {error:?}"))
    })?;
    // The transfer gate: the shipped bytes verify against the expected
    // roots on a clone before any engine publishes them.
    {
        let mut verified = local_state.tree.clone();
        verified.apply_verified_chunks(&plan, &chunks).map_err(|error| {
            HostingError::EngineFailure(format!(
                "the repair transfer failed verification: {error:?}"
            ))
        })?;
    }
    let mut repaired = 0;
    for range in &plan.ranges {
        let key = range_key(range)?;
        // One ship per key, winner to loser: the `(hlc, origin)` order
        // (remove-wins on ties) picks the same winner the merge would,
        // so the merge publishes exactly once and the counts stay exact.
        // Shipping both directions would re-ship a just-received version
        // back into a tie and double-publish under the repair identity.
        let local_side = key_state(local, scope, &local_state, &key)?;
        let peer_side = key_state(peer, scope, &peer_state, &key)?;
        match (&local_side, &peer_side) {
            (KeySide::Absent, KeySide::Absent) => {
                return Err(HostingError::EngineFailure(
                    "a repair range names a key neither member holds".into(),
                ));
            }
            (KeySide::Present(record), KeySide::Absent) => {
                repaired += apply_one(peer, scope, record)?;
            }
            (KeySide::Absent, KeySide::Present(record)) => {
                repaired += apply_one(local, scope, record)?;
            }
            (KeySide::Present(local_record), KeySide::Present(peer_record)) => {
                if peer_wins(local_record, peer_record) {
                    repaired += apply_one(local, scope, peer_record)?;
                } else {
                    repaired += apply_one(peer, scope, local_record)?;
                }
            }
        }
    }
    let local_after = member_state(local, scope)?;
    let peer_after = member_state(peer, scope)?;
    if local_after.tree.roots() == peer_after.tree.roots() {
        Ok(RepairDecision::Converged { repaired })
    } else {
        Err(HostingError::EngineFailure(
            "the members still diverge after repair; retry the pass".into(),
        ))
    }
}

/// The journal cursor past everything the member's journal currently holds:
/// the position a link adopts after a snapshot bootstrap so pre-snapshot
/// rows never re-ship (the copied file set already serves them).
///
/// # Errors
///
/// Returns [`HostingError`] when the durable journal cannot be replayed
/// under the bound scope.
pub fn journal_end(
    host: &EngineHost,
    scope: &BoundScope,
) -> Result<Option<ChangeCheckpoint>, HostingError> {
    let mut cursor = None;
    loop {
        let batch = host.replay_sync_journal(scope, cursor)?;
        if batch.next_checkpoint().is_some() {
            cursor = batch.next_checkpoint();
        }
        if batch.drained() {
            return Ok(cursor);
        }
    }
}

/// What a snapshot bootstrap installed.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SnapshotBootstrap {
    /// Location files copied, in copy order.
    pub files_copied: Vec<String>,
    /// Whether the peer held a WAL beside its database file.
    pub had_wal: bool,
}

/// Installs the peer's snapshot into a fresh location: copies the quiesced
/// durable file set (`format`, the ownership record when present,
/// `terrabase.sqlite3`, and its `-wal` when present) so opening the target
/// serves the peer's state byte-identically — the in-process form of
/// installing the region-local Litestream snapshot. The caller owns the
/// barrier: the peer must be quiescent (no commit wave or checkpoint in
/// flight), exactly the bootstrap contract [`wal_follower`](crate::wal_follower)
/// documents. The target directory must not hold a live engine.
///
/// [`crate::replication_lanes::RegionLink::set_cursor`] adopts
/// [`journal_end`] afterwards; the composition never re-ships what the
/// snapshot already serves.
///
/// # Errors
///
/// Returns [`HostingError::Invalid`] when the target is not a fresh
/// directory, and [`HostingError::EngineFailure`] when a file cannot be
/// copied durably.
pub fn snapshot_bootstrap(
    peer_location: &Path,
    target_location: &Path,
) -> Result<SnapshotBootstrap, HostingError> {
    if target_location.exists() && !is_empty_dir(target_location) {
        return Err(HostingError::Invalid("the snapshot target must be a fresh directory".into()));
    }
    std::fs::create_dir_all(target_location).map_err(|_| {
        HostingError::EngineFailure("the snapshot target refused to initialize".into())
    })?;
    let mut files_copied = Vec::new();
    copy_required(peer_location, target_location, "format")?;
    files_copied.push("format".to_owned());
    if copy_optional(peer_location, target_location, "owner.tbv1")? {
        files_copied.push("owner.tbv1".to_owned());
    }
    copy_required(peer_location, target_location, "terrabase.sqlite3")?;
    files_copied.push("terrabase.sqlite3".to_owned());
    let had_wal = copy_optional(peer_location, target_location, "terrabase.sqlite3-wal")?;
    if had_wal {
        files_copied.push("terrabase.sqlite3-wal".to_owned());
    }
    std::fs::File::open(target_location).and_then(|directory| directory.sync_all()).map_err(
        |_| HostingError::EngineFailure("the snapshot target could not be made durable".into()),
    )?;
    Ok(SnapshotBootstrap { files_copied, had_wal })
}

/// One member's scanned state: its Merkle tree plus the full repair record
/// behind every live entry key.
struct MemberState {
    tree: MerkleTree,
    records: BTreeMap<Vec<u8>, ReplicatedRecord>,
}

/// Scans one member's live documents with their replication stamps into a
/// [`MemberState`].
fn member_state(host: &EngineHost, scope: &BoundScope) -> Result<MemberState, HostingError> {
    fn engine_failure(error: impl std::fmt::Debug) -> HostingError {
        HostingError::EngineFailure(format!("the anti-entropy scan failed: {error:?}"))
    }

    let engine_scope = host.engine_scope_of(scope)?;
    let engine = host.engine();
    let snapshot = engine.read_snapshot(&engine_scope).map_err(engine_failure)?;
    let documents = engine.snapshot_documents(&engine_scope, &snapshot).map_err(engine_failure)?;
    let mut entries = Vec::with_capacity(documents.len());
    let mut records = BTreeMap::new();
    for document in &documents {
        let (hlc, origin, deleted) = engine
            .document_replication_stamp(&engine_scope, document.collection(), document.id())
            .map_err(engine_failure)?
            .ok_or_else(|| {
                HostingError::EngineFailure("a live document carries no replication stamp".into())
            })?;
        if deleted {
            // The scan saw the document live but its stamp now reads
            // tombstoned: a delete landed mid-scan. Fail closed; the caller
            // retries the pass past it.
            return Err(HostingError::EngineFailure(
                "the member changed under the anti-entropy scan; retry the pass".into(),
            ));
        }
        let key = entry_key(document.collection(), document.id());
        let materialized = materialized_bytes(document.value());
        entries.push(MerkleEntry::new(
            key.clone(),
            causal_bytes(hlc, &origin, false),
            materialized.clone(),
        ));
        records.insert(
            key,
            ReplicatedRecord {
                collection: document.collection().clone(),
                id: document.id().clone(),
                body: document.value().clone(),
                deleted: false,
                commit_ulid: repair_ulid(
                    hlc,
                    &origin,
                    document.collection(),
                    document.id(),
                    false,
                    &materialized,
                ),
                hlc,
                origin,
            },
        );
    }
    let tree = MerkleTree::build(entries).map_err(engine_failure)?;
    Ok(MemberState { tree, records })
}

/// One side of a mismatched key: what the member holds for it.
#[derive(Clone, Debug)]
enum KeySide {
    /// The member never held the key.
    Absent,
    /// The member's live record or tombstone for the key.
    Present(ReplicatedRecord),
}

/// One member's state for a mismatched key: its live repair record, its
/// tombstone when provenance says deleted, or absence when the member never
/// held the key. A live stamp for a key the scan did not see means a write
/// landed between the scan and this lookup — fail closed so the caller
/// retries past it.
fn key_state(
    host: &EngineHost,
    scope: &BoundScope,
    state: &MemberState,
    key: &[u8],
) -> Result<KeySide, HostingError> {
    if let Some(record) = state.records.get(key) {
        return Ok(KeySide::Present(record.clone()));
    }
    let (collection, id) = split_key(key)?;
    let engine_scope = host.engine_scope_of(scope)?;
    let stamp = host.engine().document_replication_stamp(&engine_scope, &collection, &id).map_err(
        |error| {
            HostingError::EngineFailure(format!(
                "the anti-entropy tombstone read failed: {error:?}"
            ))
        },
    )?;
    match stamp {
        None => Ok(KeySide::Absent),
        Some((.., false)) => Err(HostingError::EngineFailure(
            "a repair key turned live under repair; retry the pass".into(),
        )),
        Some((hlc, origin, true)) => {
            let materialized = Vec::new();
            Ok(KeySide::Present(ReplicatedRecord {
                commit_ulid: repair_ulid(hlc, &origin, &collection, &id, true, &materialized),
                hlc,
                origin,
                collection,
                id,
                body: DocumentValue::Null,
                deleted: true,
            }))
        }
    }
}

/// Whether the peer's version wins over the local one: the `(hlc, origin)`
/// total order with remove-wins on exact ties — the same verdict
/// `Engine::apply_replicated`'s deterministic merge reaches, mirrored here
/// so the pass ships winner to loser exactly once. A both-live tie ships
/// peer to local, which converges (the local adopts the peer's bytes at
/// the shared stamp); any drift from the engine's verdict fails closed at
/// the closing roots check instead of diverging silently.
fn peer_wins(local: &ReplicatedRecord, peer: &ReplicatedRecord) -> bool {
    match (peer.hlc, peer.origin.as_str()).cmp(&(local.hlc, local.origin.as_str())) {
        std::cmp::Ordering::Greater => true,
        std::cmp::Ordering::Less => false,
        std::cmp::Ordering::Equal => peer.deleted || !local.deleted,
    }
}

/// Applies one repair record through the deterministic merge, returning one
/// when it published and zero when it replayed or was superseded.
fn apply_one(
    host: &EngineHost,
    scope: &BoundScope,
    record: &ReplicatedRecord,
) -> Result<usize, HostingError> {
    let engine_scope = host.engine_scope_of(scope)?;
    let outcome = host.engine().apply_replicated(&engine_scope, record).map_err(|error| {
        HostingError::EngineFailure(format!("the repair record could not be applied: {error:?}"))
    })?;
    Ok(usize::from(outcome.receipt().is_some() && !outcome.replayed()))
}

/// Decodes one planned range back to its entry key. The kernels name one
/// logical key per mismatch range; anything else fails closed.
fn range_key(
    range: &terrabase_replication_core::anti_entropy::KeyRange,
) -> Result<Vec<u8>, HostingError> {
    match (&range.start, &range.end) {
        (Some(start), Some(end)) if start == end => Ok(start.clone()),
        _ => Err(HostingError::EngineFailure("a repair range names no single key".into())),
    }
}

/// The entry key for one document: `collection 0x00 id`. Neither part can
/// hold a zero byte (the identifier discipline rejects control characters),
/// so the split is unambiguous.
fn entry_key(collection: &CollectionName, id: &DocumentId) -> Vec<u8> {
    let mut key = Vec::with_capacity(collection.as_str().len() + id.as_str().len() + 1);
    key.extend_from_slice(collection.as_str().as_bytes());
    key.push(0);
    key.extend_from_slice(id.as_str().as_bytes());
    key
}

/// Splits one entry key back into its collection and document identities.
fn split_key(key: &[u8]) -> Result<(CollectionName, DocumentId), HostingError> {
    let separator = key.iter().position(|byte| *byte == 0).ok_or_else(|| {
        HostingError::EngineFailure("a repair key breaks the key discipline".into())
    })?;
    let collection = std::str::from_utf8(&key[..separator])
        .ok()
        .and_then(|text| CollectionName::try_new(text).ok())
        .ok_or_else(|| {
            HostingError::EngineFailure("a repair key breaks the key discipline".into())
        })?;
    let id = std::str::from_utf8(&key[separator + 1..])
        .ok()
        .and_then(|text| DocumentId::try_new(text).ok())
        .ok_or_else(|| {
            HostingError::EngineFailure("a repair key breaks the key discipline".into())
        })?;
    Ok((collection, id))
}

/// The causal bytes for one version: the merge's comparison input —
/// `hlc` big-endian, the origin length-prefixed, one tombstone byte — so
/// the Merkle causal root and the merge verdict order the same values.
fn causal_bytes(hlc: u64, origin: &str, deleted: bool) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(8 + 8 + origin.len() + 1);
    bytes.extend_from_slice(&hlc.to_be_bytes());
    bytes.extend_from_slice(&(origin.len() as u64).to_be_bytes());
    bytes.extend_from_slice(origin.as_bytes());
    bytes.push(u8::from(deleted));
    bytes
}

/// The deterministic materialized encoding of one document value:
/// domain-tagged, big-endian integers, length-prefixed strings, objects in
/// key order (`BTreeMap` iteration). A tombstone encodes as the empty
/// vector, which no live value (not even null) collides with.
fn materialized_bytes(value: &DocumentValue) -> Vec<u8> {
    let mut bytes = Vec::new();
    encode_value(&mut bytes, value);
    bytes
}

/// Encodes one value into the materialized form.
fn encode_value(output: &mut Vec<u8>, value: &DocumentValue) {
    match value {
        DocumentValue::Null => output.push(0x00),
        DocumentValue::Boolean(flag) => {
            output.push(0x01);
            output.push(u8::from(*flag));
        }
        DocumentValue::Integer(number) => {
            output.push(0x02);
            output.extend_from_slice(&number.to_be_bytes());
        }
        DocumentValue::Number(number) => {
            output.push(0x03);
            output.extend_from_slice(&number.to_bits().to_be_bytes());
        }
        DocumentValue::String(text) => {
            output.push(0x04);
            push_bytes(output, text.as_bytes());
        }
        DocumentValue::Array(items) => {
            output.push(0x05);
            output.extend_from_slice(&(items.len() as u64).to_be_bytes());
            for item in items {
                encode_value(output, item);
            }
        }
        DocumentValue::Object(fields) => {
            output.push(0x06);
            output.extend_from_slice(&(fields.len() as u64).to_be_bytes());
            for (name, field) in fields {
                push_bytes(output, name.as_bytes());
                encode_value(output, field);
            }
        }
    }
}

/// Pushes length-prefixed bytes.
fn push_bytes(output: &mut Vec<u8>, bytes: &[u8]) {
    output.extend_from_slice(&(bytes.len() as u64).to_be_bytes());
    output.extend_from_slice(bytes);
}

/// The deterministic repair identity for one version: `merkle-` plus the
/// stamp and a content hash. Every member holding the same version mints
/// the same identity, so a re-shipped repair replays instead of
/// double-writing; distinct versions never share one.
fn repair_ulid(
    hlc: u64,
    origin: &str,
    collection: &CollectionName,
    id: &DocumentId,
    deleted: bool,
    materialized: &[u8],
) -> String {
    let mut keyed = Vec::new();
    keyed.extend_from_slice(&hlc.to_be_bytes());
    push_bytes(&mut keyed, origin.as_bytes());
    push_bytes(&mut keyed, collection.as_str().as_bytes());
    push_bytes(&mut keyed, id.as_str().as_bytes());
    keyed.push(u8::from(deleted));
    keyed.extend_from_slice(materialized);
    format!("merkle-{hlc:016x}-{:016x}", fnv1a(&keyed))
}

/// The 64-bit FNV-1a hash: the repair identity's content binding. Short,
/// deterministic, dependency-free.
fn fnv1a(bytes: &[u8]) -> u64 {
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0100_0000_01b3);
    }
    hash
}

/// Reports whether the path names an empty directory: the snapshot target
/// a bootstrap may populate.
fn is_empty_dir(path: &Path) -> bool {
    std::fs::read_dir(path).is_ok_and(|mut entries| entries.next().is_none())
}

/// Copies one required location file, fsyncing the copy by content before
/// returning. Permissions travel with the bytes.
fn copy_required(peer: &Path, target: &Path, name: &str) -> Result<(), HostingError> {
    let source = peer.join(name);
    let permissions = std::fs::metadata(&source)
        .map_err(|_| HostingError::EngineFailure("the snapshot source is incomplete".into()))?
        .permissions();
    let mut reader = std::fs::File::open(&source)
        .map_err(|_| HostingError::EngineFailure("the snapshot source is incomplete".into()))?;
    let mut writer = std::fs::File::create(target.join(name)).map_err(|_| {
        HostingError::EngineFailure("the snapshot target refused to initialize".into())
    })?;
    std::io::copy(&mut reader, &mut writer)
        .map_err(|_| HostingError::EngineFailure("the snapshot copy failed".into()))?;
    writer
        .sync_all()
        .map_err(|_| HostingError::EngineFailure("the snapshot copy failed".into()))?;
    drop(writer);
    std::fs::set_permissions(target.join(name), permissions)
        .map_err(|_| HostingError::EngineFailure("the snapshot copy failed".into()))?;
    Ok(())
}

/// Copies one optional location file, reporting whether it existed.
fn copy_optional(peer: &Path, target: &Path, name: &str) -> Result<bool, HostingError> {
    match std::fs::metadata(peer.join(name)) {
        Ok(permissions) => {
            let mut reader = std::fs::File::open(peer.join(name)).map_err(|_| {
                HostingError::EngineFailure("the snapshot source is incomplete".into())
            })?;
            let mut writer = std::fs::File::create(target.join(name)).map_err(|_| {
                HostingError::EngineFailure("the snapshot target refused to initialize".into())
            })?;
            std::io::copy(&mut reader, &mut writer)
                .map_err(|_| HostingError::EngineFailure("the snapshot copy failed".into()))?;
            writer
                .sync_all()
                .map_err(|_| HostingError::EngineFailure("the snapshot copy failed".into()))?;
            drop(writer);
            std::fs::set_permissions(target.join(name), permissions.permissions())
                .map_err(|_| HostingError::EngineFailure("the snapshot copy failed".into()))?;
            Ok(true)
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(false),
        Err(_) => Err(HostingError::EngineFailure("the snapshot source is incomplete".into())),
    }
}
