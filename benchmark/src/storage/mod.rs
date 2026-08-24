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
mod intent;
mod ledger;
mod root;
mod wal;

#[cfg(test)]
mod tests;

#[cfg(test)]
pub(crate) use faults::FaultPoint;
pub(crate) use file::{FileStorage, StorageOpenError};
pub(crate) use intent::{CommitIntent, CommitResult, OperationResolution, StagedWrite};
pub(crate) use root::StorageRootId;

use crate::types::{
    CollectionName, Document, DocumentId, LocalDurability, OperationId, StorageSequence, TenantId,
};

/// The private storage adapter seam the Engine depends on.
///
/// The production implementation is [`FileStorage`]. The trait exists so the
/// storage seam stays substitutable behind one typed contract instead of
/// leaking file, codec, or WAL details into the Engine.
pub(crate) trait StoragePort: Send {
    /// Runs the publication order for one validated commit intent.
    fn commit(&mut self, intent: CommitIntent) -> CommitResult;

    /// Returns the durable resolution of one operation identity, if recorded.
    fn resolve_operation(
        &self,
        tenant: &TenantId,
        operation: &OperationId,
    ) -> Option<OperationResolution>;

    /// Returns one document as the given read snapshot observed it.
    fn read_document_at(
        &self,
        sequence: StorageSequence,
        tenant: &TenantId,
        id: &DocumentId,
    ) -> Option<Document>;

    /// Returns every document of one tenant as the given read snapshot
    /// observed them, in canonical key order.
    fn documents_at(&self, sequence: StorageSequence, tenant: &TenantId) -> Vec<Document>;

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

    /// Returns the currently published sequence and root identity.
    fn published_state(&self) -> (StorageSequence, StorageRootId);

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
