//! Crate-private commit intents, receipts, and storage outcomes.

use crate::types::{
    CollectionName, Document, DocumentId, ExpectedVersion, LocalDurability, OperationId,
    StorageSequence, TenantId,
};
use crate::usage::UsageAdmissionContext;

use super::StorageRootId;

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
    /// Insert or replace one document replicated from a peer region
    /// (D-35/D-36), checked against the merge snapshot and stamped
    /// `kind='replicated'` with its causal metadata. The source commit
    /// identity is retained separately from the per-record replay binding.
    ReplicatedPut {
        /// The replicated document to publish.
        document: Document,
        /// The source region's commit identity this row carries.
        commit_ulid: String,
        /// The source region's hybrid logical clock stamp.
        hlc: u64,
        /// The source region's identity.
        origin: String,
        /// The source region's durable per-origin journal sequence.
        origin_sequence: u64,
        /// The source row's opaque versioned causal context.
        context: Vec<u8>,
    },
    /// Delete one document replicated from a peer region (D-35/D-36): the
    /// tombstone form of [`StagedWrite::ReplicatedPut`], stamped the same
    /// way so a replicated tombstone never re-ships as an origin write.
    ReplicatedDelete {
        /// The collection of the deleted key.
        collection: CollectionName,
        /// The deleted document's identifier.
        id: DocumentId,
        /// The source region's commit identity this row carries.
        commit_ulid: String,
        /// The source region's hybrid logical clock stamp.
        hlc: u64,
        /// The source region's identity.
        origin: String,
        /// The source region's durable per-origin journal sequence.
        origin_sequence: u64,
        /// The source row's opaque versioned causal context.
        context: Vec<u8>,
    },
}

impl StagedWrite {
    /// Returns the document identifier this write touches.
    pub(crate) fn document_id(&self) -> &DocumentId {
        match self {
            Self::Put { document, .. } | Self::ReplicatedPut { document, .. } => document.id(),
            Self::Delete { id, .. } | Self::ReplicatedDelete { id, .. } => id,
        }
    }
}

/// A private control record staged with the transaction's customer mutations.
#[derive(Clone, Debug)]
pub(crate) struct PrivateMetadataWrite {
    pub(crate) key: String,
    pub(crate) value: Option<crate::types::DocumentValue>,
}

/// One schema envelope CAS committed alongside active validation metadata.
#[derive(Clone, Debug)]
pub(crate) struct SchemaEnvelopeWrite {
    pub(crate) database: String,
    pub(crate) branch: String,
    pub(crate) collection: String,
    pub(crate) expected: Option<Vec<u8>>,
    pub(crate) replacement: Vec<u8>,
}

/// Source transaction identity and merge observations published as one unit.
#[derive(Clone, Debug)]
pub(crate) struct ReplicationPublicationIntent {
    pub(crate) origin: String,
    pub(crate) origin_sequence: u64,
    pub(crate) hlc: u64,
    pub(crate) commit_ulid: String,
    pub(crate) context: Vec<u8>,
    pub(crate) records: Vec<crate::engine::ReplicatedRecord>,
    pub(crate) private_payload: Vec<u8>,
    pub(crate) observations: Vec<(CollectionName, DocumentId)>,
    pub(crate) schema_observations: Vec<(CollectionName, Option<String>)>,
    pub(crate) conflicts: Vec<(CollectionName, DocumentId, super::MergeConflictEntry)>,
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
    /// Exact causal frontier previously proven uniform for a `GlobalDurable` write.
    /// Compared after replay lookup under the `SQLite` writer transaction.
    /// Captured origin authority epoch; checked before idempotent receipt replay.
    pub(crate) expected_origin_epoch: Option<u64>,
    pub(crate) expected_uniform_context: Option<Vec<u8>>,
    /// Trusted usage admission bound atomically to this origin mutation.
    /// Replication, repair, and internal writes carry `None` and never mint.
    pub(crate) usage_admission: Option<UsageAdmissionContext>,
    /// Engine-internal TTL cleanup and restore replay are unbilled.
    pub(crate) internal_maintenance: bool,
    /// Original source content, independent of the merged publication bytes.
    pub(crate) replication_fingerprint: Option<[u8; 32]>,
    pub(crate) replication_publication: Option<ReplicationPublicationIntent>,
    /// Conflict history staged atomically with a replicated document update.
    pub(crate) merge_conflict: Option<(CollectionName, DocumentId, super::MergeConflictEntry)>,
    /// The engine's replication clock stamp for an origin commit (the
    /// hybrid stamp the deterministic merge compares). A replicated
    /// intent carries `0` here — its stamp rides the staged variant.
    pub(crate) hlc: u64,
    /// The staged writes to publish atomically.
    pub(crate) writes: Vec<StagedWrite>,
    /// Private control changes committed atomically and excluded from customer billing.
    pub(crate) metadata: Vec<PrivateMetadataWrite>,
    /// Schema envelopes atomically bound to their active validation controls.
    pub(crate) schema_envelopes: Vec<SchemaEnvelopeWrite>,
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
}

impl UnknownCause {
    /// Converts the private cause into the public unknown reason.
    pub(crate) fn to_unknown_reason(self) -> crate::engine::UnknownReason {
        match self {
            Self::ResponseInterrupted => crate::engine::UnknownReason::ResponseInterrupted,
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
}

/// A merge decision that publishes no document, bound to its read snapshot.
pub(crate) struct SupersededReplication {
    pub(crate) tenant: TenantId,
    pub(crate) operation: OperationId,
    pub(crate) fingerprint: [u8; 32],
    pub(crate) snapshot_sequence: StorageSequence,
    pub(crate) collection: CollectionName,
    pub(crate) id: DocumentId,
    pub(crate) conflict: Option<super::MergeConflictEntry>,
}
