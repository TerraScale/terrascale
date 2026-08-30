//! Private storage, write-ahead log, operation ledger, and recovery.
//!
//! This module owns the durable representation of accepted writes. It exposes
//! one crate-private port (`StoragePort`) that the Engine calls, one
//! file-backed implementation of that port, and one immutable
//! `CommitPublicationEvent` emitted after storage locks release. Raw bytes,
//! frames, manifests, and lock state never leave this module: the Engine
//! receives typed intents, receipts, and resolutions only.
//!
//! The publication order implemented here is the one mandated by the W3 core
//! contract: validate, encode frames, append the prepare record, apply to a
//! new private root with a commit marker, apply the requested local durability
//! action, durably append the `ReadyToPublish` ledger record, atomically
//! publish the root pointer, durably append the `Published` ledger record,
//! release locks, and emit the publication event.

mod codec;
mod faults;
mod file;
mod frontier;
mod intent;
mod ledger;
mod ownership;
mod retained;
mod root;
mod wal;

mod compaction;
pub(crate) mod lsm;

#[cfg(test)]
mod compaction_tests;

#[cfg(test)]
mod tests;

#[cfg(test)]
pub(crate) use faults::FaultPoint;
pub(crate) use file::{FileStorage, StorageOpenError};
pub(crate) use frontier::{
    LedgerFrontierActivationError, LedgerFrontierActivationReceipt,
    LedgerFrontierActivationRequest, LedgerFrontierStatus,
};
pub(crate) use intent::{
    CommitIntent, CommitRejection, CommitResult, OperationResolution, StagedWrite, UnknownCause,
};
pub use ownership::{DatabaseOwner, EnvironmentName};
pub(crate) use ownership::{OwnershipError, OwnershipMode, load_or_create};
pub(crate) use root::StorageRootId;

pub use compaction::{
    CompactionError, CompactionPhase, CompactionPolicy, CompactionProgress, CompactionProgressSink,
    CompactionReceipt, CompactionRequest, HistoryReclaimOutcome,
};

use crate::types::{
    CollectionName, Document, DocumentId, LocalDurability, OperationId, StorageSequence, TenantId,
};
use crate::usage::{DurableUsageFact, MeasurementWindow, MintingRule, UsageFact, UsageFactId};

/// Storage-side usage command failures, mapped to the public Engine contract.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum UsageStorageError {
    /// The selected storage backend predates durable usage facts.
    UnsupportedBackend,
    /// The location contains legacy facts that cannot be billed truthfully.
    UsageUnavailable,
    /// Persisted usage is malformed or contradicts its durable binding.
    Corrupt,
    /// One scoped fact identity is already bound to different content.
    IdentityConflict,
    /// Durable storage could not complete the command.
    Unavailable,
}

/// Failures while serving a bounded snapshot page.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum SnapshotReadError {
    /// A persisted document value or key failed semantic decoding.
    Integrity,
}

/// The total order used by usage export and its continuation cursor.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(crate) struct UsageFactPosition {
    pub(crate) sequence: u64,
    pub(crate) rule: MintingRule,
    pub(crate) id: UsageFactId,
}

impl UsageFactPosition {
    pub(crate) fn of(fact: &DurableUsageFact) -> Self {
        Self {
            sequence: fact.sequence(),
            rule: fact.fact().identity().rule(),
            id: fact.fact().identity().id().clone(),
        }
    }
}

/// The private storage adapter seam the Engine depends on.
///
/// The production implementation is [`FileStorage`]. The trait exists so the
/// storage seam stays substitutable behind one typed contract instead of
/// leaking file, codec, or WAL details into the Engine.
pub(crate) trait StoragePort: Send {
    /// Runs the publication order for one validated commit intent.
    fn commit(&mut self, intent: CommitIntent) -> CommitResult;

    /// Runs the publication order for a batch of validated commit intents
    /// under one shared durable append where the implementation supports
    /// it (group commit). The default commits each intent alone, which is
    /// exactly the serial behavior.
    fn commit_batch(&mut self, intents: Vec<CommitIntent>, defer_flips: bool) -> Vec<CommitResult> {
        let _ = defer_flips;
        intents.into_iter().map(|intent| self.commit(intent)).collect()
    }

    /// Durable-side flush of any records a deferred batch left unsynced.
    /// The default has nothing to flush.
    fn flush_pending_flips(&mut self) {}

    /// Returns the durable resolution of one operation identity, if recorded.
    fn resolve_operation(
        &self,
        tenant: &TenantId,
        operation: &OperationId,
    ) -> Option<OperationResolution>;

    /// Appends one standalone authoritative usage fact durably and
    /// idempotently. The default rejects legacy backends.
    fn record_usage(
        &mut self,
        _tenant: &TenantId,
        _fact: UsageFact,
    ) -> Result<(DurableUsageFact, bool), UsageStorageError> {
        Err(UsageStorageError::UnsupportedBackend)
    }

    /// Returns at most `limit` tenant facts at or below the fixed snapshot,
    /// ordered strictly after the optional continuation position. The Engine
    /// asks for one extra row to decide whether another page exists.
    fn usage_facts_page(
        &self,
        _tenant: &TenantId,
        _snapshot: StorageSequence,
        _after: Option<&UsageFactPosition>,
        _window: Option<MeasurementWindow>,
        _limit: u32,
    ) -> Result<Vec<DurableUsageFact>, UsageStorageError> {
        Err(UsageStorageError::UnsupportedBackend)
    }

    /// Returns one document as the given read snapshot observed it.
    fn read_document_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        id: &DocumentId,
    ) -> Option<Document>;

    /// Returns one document as the given read snapshot observed it, resolved
    /// by the full storage key: tenant, collection, and identifier.
    ///
    /// Documents are keyed by all three parts and nothing forbids reusing one
    /// identifier across a tenant's collections, so a read that names a
    /// collection must resolve by the exact key. An identifier-only lookup
    /// answers whichever copy canonical key order happens to put first, which
    /// is a different document, not an absent one.
    fn read_document_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Option<Document>;

    /// Returns every document of one tenant as the given read snapshot
    /// observed them, in canonical key order.
    fn documents_at(&self, sequence: StorageSequence, tenant: &TenantId) -> Vec<Document>;

    /// Returns every document of one tenant's collection as the given read
    /// snapshot observed them, in canonical key order.
    ///
    /// A query whose source is one collection needs no other collection's
    /// documents, and this read stops at the collection's key range instead
    /// of materializing (and in the LSM engine, decoding) the rest of the
    /// tenant. Canonical order is the order of [`StoragePort::documents_at`]
    /// restricted to the collection.
    fn documents_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
    ) -> Vec<Document>;

    /// Returns the documents the given read snapshot observed at the named
    /// exact keys of one tenant's collection, in canonical key order of the
    /// requested identifiers.
    ///
    /// This is the batched form of [`StoragePort::read_document_in_collection_at`]:
    /// each named key resolves by its exact `(tenant, collection, identifier)`
    /// triple, a key absent at the snapshot (never written, or deleted at or
    /// below it) contributes nothing, and one identifier is never resolved in
    /// another collection. The result is the keyed-candidate fetch of the
    /// index-narrowed query path: a bounded identifier set in, the surviving
    /// documents out, in the same canonical order a scan of the collection
    /// would have produced.
    fn read_documents_in_collection_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        collection: &CollectionName,
        ids: &[DocumentId],
    ) -> Vec<Document>;

    /// Returns one bounded window of a tenant's documents as the given read
    /// snapshot observed them, in canonical key order resuming at the
    /// boundary named by `after` (W4-CA-7).
    fn documents_at_from(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        after: Option<&DocumentId>,
        limit: u32,
    ) -> Vec<Document>;

    /// Returns one bounded window of a tenant's documents in canonical full
    /// key order, resuming strictly after `(collection, identifier)`.
    /// Unlike the identifier-only compatibility seam above, this cursor is
    /// lossless when an identifier is reused across collections.
    fn documents_at_from_key(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        after: Option<(&CollectionName, &DocumentId)>,
        limit: u32,
    ) -> Result<Vec<Document>, SnapshotReadError>;

    /// Reports whether the immutable state needed for a historical boundary
    /// is still retained. A false result must fail closed instead of letting
    /// a backend fall back to its current published state.
    fn snapshot_boundary_available(&self, sequence: StorageSequence) -> bool;

    /// Resolves the durability of each requested final-state key without
    /// cloning complete publication write sets. `None` means at least one
    /// winner is no longer provable from retained history.
    fn winning_durabilities_at(
        &self,
        tenant: &TenantId,
        sequence: StorageSequence,
        keys: &[(&str, &str)],
    ) -> Option<Vec<LocalDurability>>;

    /// Returns one tenant's published commits after `after`, in global
    /// storage-sequence order, up to `limit` entries (W4-CA-4).
    fn tenant_commits(
        &self,
        tenant: &TenantId,
        after: StorageSequence,
        limit: u32,
    ) -> Vec<PublishedCommit>;

    /// Returns every tenant that has at least one recorded published commit,
    /// in canonical tenant order (W4-CA-4).
    fn committed_tenants(&self) -> Vec<TenantId>;

    /// Returns the tenant's latest recorded sequence at or below `at`
    /// (W4-CA-4): the mapping entry that turns one global
    /// storage-sequence cursor into this tenant's own progress.
    fn tenant_sequence(&self, tenant: &TenantId, at: StorageSequence) -> Option<StorageSequence>;

    /// Returns the oldest sequence the durable publication history can
    /// serve, or `None` when no history is servable (W4-CA-4).
    ///
    /// A cursor older than this bound cannot be served incrementally: the
    /// caller must fall back to a full catch-up or backup instead of
    /// silently skipping the gap.
    fn servable_history_start(&self) -> Option<StorageSequence>;

    /// Returns the last global storage sequence durably recorded in the
    /// publication-history journal (W4-CA-4 repair F2).
    ///
    /// Sequences have legitimate holes, so a consumer cannot detect a
    /// skipped journal append by discontinuity; this durable high-water
    /// mark is the real signal. `journaled_high_water() <
    /// published_state().0` means at least one published commit is not
    /// servable from the journal, and the consumer must fall back instead
    /// of assuming completeness.
    fn journaled_high_water(&self) -> Option<StorageSequence>;

    /// Returns the number of published commits the publication-history
    /// journal does not serve (W4-CA-4 repair F2's divergence, as a
    /// count).
    ///
    /// The count is derived, never stored: a commit counts when the
    /// operation ledger resolves it as committed while the journal holds
    /// no record at its sequence. Because a later successful journal
    /// append advances the high-water mark past an earlier skipped one,
    /// `journaled_high_water() < published_state().0` only covers the
    /// unhealed window; this count keeps naming every skipped append,
    /// across appends that heal the mark and across reopens. A nonzero
    /// count means the journal cannot prove completeness for any
    /// consumer, and such a consumer (the SH-03 bucket migration) must
    /// fail closed instead of assuming the missing records away.
    fn journal_divergence(&self) -> u64;

    /// Returns the currently published sequence and root identity.
    fn published_state(&self) -> (StorageSequence, StorageRootId);

    /// Reports whether the root-file operation-ledger frontier is inactive or
    /// has crossed its coordinated activation boundary. Other backends do not
    /// claim this root-format feature.
    fn ledger_frontier_status(&self) -> LedgerFrontierStatus {
        LedgerFrontierStatus::UnsupportedBackend
    }

    /// Activates the required root-file ledger frontier after an external
    /// coordinator has fenced every old writer. The raw seam stays private
    /// until the authenticated administration operation is integrated.
    fn activate_ledger_frontier(
        &mut self,
        _request: &LedgerFrontierActivationRequest,
    ) -> Result<LedgerFrontierActivationReceipt, LedgerFrontierActivationError> {
        Err(LedgerFrontierActivationError::UnsupportedBackend)
    }

    /// Runs one compaction pass (GAP-S1): checkpoint, write-ahead log
    /// reclaim, gated history reclaim with durable retained evidence, and
    /// superseded-root reclamation.
    fn compact(
        &mut self,
        request: crate::storage::compaction::StorageCompaction,
    ) -> Result<
        (
            crate::storage::compaction::CompactionReceipt,
            crate::storage::compaction::CompactionDriverFacts,
        ),
        crate::storage::compaction::CompactionError,
    >;

    /// Returns the durable retained-evidence state (GAP-S1 D3): the
    /// declared bound, the retained deletion facts, and the per-tenant
    /// non-deletion facts, as the store holds them.
    fn retained_state(&self) -> crate::storage::compaction::RetainedState;

    /// Runs one bounded tier-mover pass (P4's tier administration): the
    /// LSM engine's demotion of the coldest over-budget segment to the
    /// object-store tier. The root-file engine has no tiers and answers
    /// [`lsm::tier::TierUnsupported`].
    fn mover_pass(
        &mut self,
        policy: &lsm::tier::TierMovePolicy,
        home_region: &str,
    ) -> Result<lsm::tier::MoverOutcome, lsm::tier::TierUnsupported>;

    /// Returns the raw tier snapshot — the manifest epoch, the number of
    /// verified objects it names, and the resident `NVMe` bytes. `None`
    /// means this backend has no tiers.
    fn tier_snapshot(&self) -> Option<lsm::tier::TierSnapshot>;

    /// Flushes appended durable bytes on shutdown.
    fn flush(&mut self) -> Result<(), ()>;

    /// Arms one test fault inside the publication order. Test-only.
    #[cfg(test)]
    fn arm_test_fault(&mut self, point: FaultPoint);
}

/// One published commit reconstructed from the durable publication history
/// (W4-CA-4).
///
/// The commit carries the logical write set itself — full documents for puts
/// and identifier-plus-version for deletes — never a digest: a digest is not
/// invertible, and a follower must be able to replay both puts and deletes
/// idempotently by operation identity. Each entry is annotated with the
/// commit's achieved [`LocalDurability`] (repair F4): a `Buffered` or
/// `Written` entry may describe data the source lost after a restart, and a
/// consumer must reconcile that disclosure rather than assume the entry's
/// bytes are recoverable.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct PublishedCommit {
    tenant: TenantId,
    operation_id: OperationId,
    sequence: StorageSequence,
    durability: LocalDurability,
    writes: Vec<PublishedWrite>,
}

impl PublishedCommit {
    /// Creates one commit record from the published facts.
    pub(crate) fn new(
        tenant: TenantId,
        operation_id: OperationId,
        sequence: StorageSequence,
        durability: LocalDurability,
        writes: Vec<PublishedWrite>,
    ) -> Self {
        Self { tenant, operation_id, sequence, durability, writes }
    }

    /// Returns the tenant that owns the commit.
    pub(crate) fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the stable operation identity of the commit.
    pub(crate) fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the global storage sequence of the publication.
    pub(crate) fn sequence(&self) -> StorageSequence {
        self.sequence
    }

    /// Returns the local durability the commit had achieved when the entry
    /// was journaled (repair F4).
    pub(crate) fn durability(&self) -> LocalDurability {
        self.durability
    }

    /// Returns the reconstructable write set of the commit.
    pub(crate) fn writes(&self) -> &[PublishedWrite] {
        &self.writes
    }
}

/// One reconstructable write of a published commit (W4-CA-4).
///
/// A put carries the published document — the form the commit assigned,
/// version included. A delete carries the exact key it removed — the
/// collection, the document identifier, and the version that existed at that
/// key (`0` when nothing existed there) — because a snapshot read cannot
/// distinguish a deleted identifier from one that never existed, and an
/// identifier alone is ambiguous when one tenant reuses it across
/// collections. The `None` collection case is exactly the provable no-op
/// delete: nothing existed at any key for that identifier, so
/// `version_at_delete` is `0` and a replay removes nothing.
#[derive(Clone, Debug, PartialEq)]
pub(crate) enum PublishedWrite {
    /// Insert or replace one document in its published form.
    Put {
        /// The document as the publication stored it, version included.
        document: Document,
    },
    /// Delete one document by its exact key.
    Delete {
        /// The collection of the deleted key, or `None` exactly when the
        /// identifier was absent from every collection of the tenant.
        collection: Option<CollectionName>,
        /// The deleted document's identifier.
        id: DocumentId,
        /// The version that existed at the deleted key; `0` when nothing
        /// existed there.
        version_at_delete: u64,
    },
}

/// An immutable event emitted after storage locks release for one published
/// commit.
///
/// The event carries the committed root and its storage sequence. Index
/// publication is a separate concern that may lag; it never blocks storage
/// lock release.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct CommitPublicationEvent {
    operation: OperationId,
    root: StorageRootId,
    sequence: StorageSequence,
}

impl CommitPublicationEvent {
    /// Creates one event from the published facts of a commit.
    pub(crate) fn new(
        operation: OperationId,
        root: StorageRootId,
        sequence: StorageSequence,
    ) -> Self {
        Self { operation, root, sequence }
    }

    // Read accessors are consumed by the W3-2 index-publication seam; W3-1
    // proves the emission point and ordering only.
    /// Returns the published operation identity.
    #[allow(dead_code)]
    pub(crate) fn operation_id(&self) -> &OperationId {
        &self.operation
    }

    /// Returns the immutable root that became visible.
    #[allow(dead_code)]
    pub(crate) fn root(&self) -> &StorageRootId {
        &self.root
    }

    /// Returns the storage sequence of the publication.
    #[allow(dead_code)]
    pub(crate) fn sequence(&self) -> StorageSequence {
        self.sequence
    }
}

/// Computes the logical digest binding an operation identity to its write set.
pub(crate) use codec::write_set_digest;

/// Reports whether one document is inside the codec's structural limits.
pub(crate) use codec::document_within_limits;
