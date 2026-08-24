//! Crate-private commit intents, receipts, and storage outcomes.

use crate::types::{
    CollectionName, Document, DocumentId, ExpectedVersion, LocalDurability, OperationId,
    StorageSequence, TenantId,
};

use super::root::StorageRootId;

/// One staged document mutation inside a transaction.
#[derive(Clone, Debug, PartialEq)]
pub(crate) enum StagedWrite {
    /// Insert or replace one document.
    Put {
        /// The logical document to publish.
        document: Document,
        /// The version the transaction expects.
        expected: ExpectedVersion,
    },
    /// Delete one document.
    Delete {
        /// The collection the document was found in, or `None` when the
        /// snapshot does not contain the identifier.
        collection: Option<CollectionName>,
        /// The document identifier to delete.
        id: DocumentId,
        /// The version the transaction expects.
        expected: ExpectedVersion,
    },
}

impl StagedWrite {
    /// Returns the document identifier this write touches.
    pub(crate) fn document_id(&self) -> &DocumentId {
        match self {
            Self::Put { document, .. } => document.id(),
            Self::Delete { id, .. } => id,
        }
    }
}

/// The validated, digest-bound request storage publishes for one transaction.
#[derive(Clone, Debug)]
pub(crate) struct CommitIntent {
    /// The tenant whose data the write set touches.
    pub(crate) tenant: TenantId,
    /// The caller-supplied stable operation identity.
    pub(crate) operation: OperationId,
    /// The logical digest binding this identity to this write set.
    pub(crate) digest: u64,
    /// The storage sequence of the transaction's read snapshot.
    pub(crate) snapshot_sequence: StorageSequence,
    /// The requested local durability point.
    pub(crate) durability: LocalDurability,
    /// The staged writes to publish atomically.
    pub(crate) writes: Vec<StagedWrite>,
}

/// The storage-side view of one local commit receipt.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct StorageReceipt {
    /// The storage sequence the commit published.
    pub(crate) sequence: StorageSequence,
    /// The immutable root the commit published.
    pub(crate) root: StorageRootId,
    /// The local durability point actually reached.
    pub(crate) durability: LocalDurability,
}

/// Why storage refused to publish a commit.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum CommitRejection {
    /// The write set failed logical validation.
    Validation,
    /// The write set conflicted with the published view.
    Conflict,
    /// The operation identity is bound to a different write set.
    OperationIdReuse,
    /// Durable storage failed before publication.
    Storage,
}

impl CommitRejection {
    /// Converts the private rejection into the public commit error.
    pub(crate) fn to_commit_error(self) -> crate::engine::CommitError {
        match self {
            Self::Validation => crate::engine::CommitError::Validation,
            Self::Conflict => crate::engine::CommitError::Conflict,
            Self::OperationIdReuse => crate::engine::CommitError::OperationIdReuse,
            Self::Storage => crate::engine::CommitError::StorageFailure,
        }
    }
}

/// Why storage could not report a commit outcome.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum UnknownCause {
    /// A prepared operation is not published yet, so its outcome is unknown.
    ResponseInterrupted,
    /// The post-publication pointer sync failed.
    PostPublicationPointerSync,
    /// The post-publication containing-directory sync failed.
    PostPublicationDirectorySync,
    /// The post-publication published-ledger sync failed.
    PublishedLedgerSync,
}

impl UnknownCause {
    /// Converts the private cause into the public unknown reason.
    pub(crate) fn to_unknown_reason(self) -> crate::engine::UnknownReason {
        match self {
            Self::ResponseInterrupted => crate::engine::UnknownReason::ResponseInterrupted,
            Self::PostPublicationPointerSync => {
                crate::engine::UnknownReason::PostPublicationPointerSync
            }
            Self::PostPublicationDirectorySync => {
                crate::engine::UnknownReason::PostPublicationDirectorySync
            }
            Self::PublishedLedgerSync => crate::engine::UnknownReason::PublishedLedgerSync,
        }
    }
}

/// The storage-side result of one commit attempt.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) enum CommitResult {
    /// The publication rule completed and produced a receipt.
    Committed(StorageReceipt),
    /// Storage proved the operation was not published.
    Rejected(CommitRejection),
    /// Publication may or may not have completed.
    Unknown(UnknownCause),
}

/// The durable answer for one operation identity.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) enum OperationResolution {
    /// The operation published and reached the named durability point.
    Committed {
        /// The storage sequence of the publication.
        sequence: StorageSequence,
        /// The strongest local durability actually proven.
        durability: LocalDurability,
    },
    /// A durable rejection record exists for the operation.
    Rejected(CommitRejection),
    /// The operation is recorded but its publication is not yet decided.
    Pending,
}
