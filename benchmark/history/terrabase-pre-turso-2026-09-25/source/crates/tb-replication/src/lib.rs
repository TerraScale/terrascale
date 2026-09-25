//! Convergent events, signed fixed-scope authority, and a private durable inbox.
//!
//! This crate does not run the network transport or issue client acknowledgments.
//! The receiver exposes local inbox durability only; document apply is separate.

mod authority;
mod cursor;
mod event;
mod journal;
mod merge;
mod metadata;

pub use authority::{
    AcceptedAuthority, AppendFailureKind, AuthorityCertificateScope, AuthorityError,
    AuthorityMemberInput, AuthorityPayload, AuthoritySchemaInput, AuthorityVersion, CertifiedEvent,
    CertifiedMember, CertifiedMemberInfo, CertifiedReplayEvent, CertifiedShardView, DurablePrefix,
    DurableReadKind, LocalAuthorityScope, PeerHelloScope, ReceiveError, ReplicaReceiver,
};
pub use cursor::{
    CursorError, CursorLimits, CursorScope, MAX_CURSOR_PENDING, MAX_CURSOR_SEQUENCE_DISTANCE,
    OfferOutcome, OriginCursor,
};
pub use event::{
    CAUSAL_MV_REGISTER_POLICY_V1, CausalContext, CollectionBinding, CommitEvent, ConfigurationId,
    DocumentChange, DocumentValue, EncodedEventSizes, EventDigest, EventHeader, EventId,
    EventVersions, MAX_BATCH_BYTES, MAX_DISTRIBUTED_METADATA_BYTES, MAX_EVENT_CHANGES,
    MAX_WAL_GROUP_BYTES, MembershipEpoch, OriginId, ReplicationError, ReplicationLimits, ShardId,
    TransferEnvelopeSizes,
};
pub use journal::OwnedDurableGroup;
pub use merge::{DocumentState, DocumentVersion};
pub use metadata::{
    EventMetadata, MetadataChange, MetadataError, MetadataHeader, VerifiedIncomingGroup,
    VerifiedSourceChange, WalRole, compute_event_digest, decode_metadata, encode_metadata_into,
    verify_event, verify_incoming,
};

#[cfg(test)]
mod tests;
