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

use crate::types::{Document, DocumentId, OperationId, StorageSequence, TenantId};

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

    /// Returns the currently published sequence and root identity.
    fn published_state(&self) -> (StorageSequence, StorageRootId);

    /// Flushes appended durable bytes on shutdown.
    fn flush(&mut self) -> Result<(), ()>;

    /// Arms one test fault inside the publication order. Test-only.
    #[cfg(test)]
    fn arm_test_fault(&mut self, point: FaultPoint);
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
