//! Immutable participant preparation records for cross-file storage commits.
use crate::types::{CollectionName, Document, DocumentId, ExpectedVersion, OperationId, StorageSequence, TenantId, DocumentValue};

#[derive(Clone, Debug)]
pub(crate) enum ParticipantWrite {
    Put { document: Document, expected: ExpectedVersion },
    Delete { collection: Option<CollectionName>, id: DocumentId, expected: ExpectedVersion },
    ReplicatedPut { document: Document, commit_ulid: String, hlc: u64, origin: String, origin_sequence: u64, context: Vec<u8> },
    ReplicatedDelete { collection: CollectionName, id: DocumentId, commit_ulid: String, hlc: u64, origin: String, origin_sequence: u64, context: Vec<u8> },
}

#[derive(Clone, Debug)]
pub(crate) struct ParticipantIdentity {
    pub(crate) tenant: TenantId,
    pub(crate) database_incarnation: u128,
    pub(crate) operation: OperationId,
    pub(crate) participant: String,
    pub(crate) owner: String,
    pub(crate) digest: [u8; 32],
    pub(crate) epoch: u64,
    pub(crate) snapshot: StorageSequence,
}

#[derive(Clone, Debug)]
pub(crate) struct ParticipantPrepare {
    pub(crate) identity: ParticipantIdentity,
    pub(crate) writes: Vec<ParticipantWrite>,
    pub(crate) metadata: Vec<(String, Option<DocumentValue>)>,
}

#[derive(Clone, Debug)]
pub(crate) struct ParticipantPrepareReceipt { pub(crate) identity: ParticipantIdentity }
