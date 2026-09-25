//! Validated identities and bounded, whole-transaction convergent events.

use std::collections::{BTreeMap, BTreeSet};
use std::error::Error;
use std::fmt;

use tb_core::{CollectionId, DatabaseId, DocumentId, TransactionId};

pub use tb_core::{MembershipEpoch, OriginId, ShardId};

/// Maximum complete WAL transaction group in the v1 common admission contract.
pub const MAX_WAL_GROUP_BYTES: usize = 2 * 1024 * 1024;
/// Maximum distributed metadata attached to one v1 committed event.
pub const MAX_DISTRIBUTED_METADATA_BYTES: usize = 256 * 1024;
/// Maximum document changes in one v1 complete transaction group.
pub const MAX_EVENT_CHANGES: usize = 1_024;
/// Maximum encoded or decoded application batch transferred by v1 gRPC.
pub const MAX_BATCH_BYTES: usize = 4 * 1024 * 1024;
/// Only the causal multi-value register is implemented by this crate.
pub const CAUSAL_MV_REGISTER_POLICY_V1: u16 = 1;

/// Identity of one certified membership configuration, separate from its epoch.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct ConfigurationId([u8; 32]);

impl ConfigurationId {
    /// Creates a nonzero configuration digest supplied by certified metadata.
    ///
    /// # Errors
    ///
    /// Returns an error for the unspecified all-zero identity.
    pub fn from_bytes(bytes: [u8; 32]) -> Result<Self, ReplicationError> {
        if bytes == [0; 32] {
            Err(ReplicationError::InvalidConfiguration)
        } else {
            Ok(Self(bytes))
        }
    }

    /// Returns the opaque configuration digest.
    #[must_use]
    pub const fn to_bytes(self) -> [u8; 32] {
        self.0
    }
}

/// Persisted compatibility metadata for one committed distributed event.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct EventVersions {
    /// Version of the encoded document record format.
    pub record_format: u16,
    /// Version of the committed distributed event format.
    pub event_format: u16,
    /// Versioned merge policy; v1 is the causal multi-value register.
    pub merge_policy: u16,
}

impl EventVersions {
    fn validate(self) -> Result<Self, ReplicationError> {
        if !matches!(self.record_format, 1 | 2) || self.event_format != 1 {
            return Err(ReplicationError::InvalidVersion);
        }
        if self.merge_policy != CAUSAL_MV_REGISTER_POLICY_V1 {
            return Err(ReplicationError::UnsupportedMergePolicy);
        }
        Ok(self)
    }
}

/// One committed transaction's distributed identity. Its sequence is scoped
/// by database, shard, and origin; it is distinct from local `CommitSeq`.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct EventId {
    database: DatabaseId,
    shard: ShardId,
    origin: OriginId,
    sequence: u64,
}

impl EventId {
    /// Creates an event with a nonzero, monotonically increasing origin sequence.
    ///
    /// # Errors
    ///
    /// Returns an error for sequence zero or an all-zero origin incarnation.
    pub fn new(
        database: DatabaseId,
        shard: ShardId,
        origin: OriginId,
        sequence: u64,
    ) -> Result<Self, ReplicationError> {
        if sequence == 0 {
            Err(ReplicationError::InvalidSequence)
        } else if origin.incarnation == [0; 16] {
            Err(ReplicationError::InvalidIncarnation)
        } else {
            Ok(Self {
                database,
                shard,
                origin,
                sequence,
            })
        }
    }

    /// Database that owns the event.
    #[must_use]
    pub const fn database(self) -> DatabaseId {
        self.database
    }

    /// Shard that owns the event.
    #[must_use]
    pub const fn shard(self) -> ShardId {
        self.shard
    }

    /// Writer incarnation that owns the sequence.
    #[must_use]
    pub const fn origin(self) -> OriginId {
        self.origin
    }

    /// Sequence in the origin's durable log.
    #[must_use]
    pub const fn sequence(self) -> u64 {
        self.sequence
    }
}

/// A format-verified digest of the canonical committed event. This crate
/// compares digests; the format/storage boundary computes and verifies them.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct EventDigest([u8; 32]);

impl EventDigest {
    /// Wraps a digest calculated over canonical committed event bytes.
    #[must_use]
    pub const fn from_bytes(bytes: [u8; 32]) -> Self {
        Self(bytes)
    }

    /// Returns the digest bytes.
    #[must_use]
    pub const fn to_bytes(self) -> [u8; 32] {
        self.0
    }
}

/// A contiguous causal prefix for each observed writer incarnation.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct CausalContext(BTreeMap<OriginId, u64>);

impl CausalContext {
    /// An empty context, before observing any committed event.
    #[must_use]
    pub const fn new() -> Self {
        Self(BTreeMap::new())
    }

    /// Builds a context from canonical, unique, nonzero origin prefixes.
    ///
    /// # Errors
    ///
    /// Returns an error for duplicate/zero entries or an origin-count limit.
    pub fn from_entries(
        entries: impl IntoIterator<Item = (OriginId, u64)>,
        max_origins: usize,
    ) -> Result<Self, ReplicationError> {
        let mut context = Self::new();
        for (origin, sequence) in entries {
            if sequence == 0 {
                return Err(ReplicationError::InvalidSequence);
            }
            if context.0.insert(origin, sequence).is_some() {
                return Err(ReplicationError::DuplicateOrigin);
            }
            if context.0.len() > max_origins {
                return Err(ReplicationError::LimitExceeded("causal origins"));
            }
        }
        Ok(context)
    }

    /// Returns zero when this origin has not been observed.
    #[must_use]
    pub fn get(&self, origin: OriginId) -> u64 {
        self.0.get(&origin).copied().unwrap_or(0)
    }

    /// Number of distinct writer incarnations retained.
    #[must_use]
    pub fn len(&self) -> usize {
        self.0.len()
    }

    /// Whether no writer incarnation has been observed.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.0.is_empty()
    }

    /// Whether a particular event is within the observed prefix.
    #[must_use]
    pub fn covers(&self, id: EventId) -> bool {
        self.get(id.origin()) >= id.sequence()
    }

    pub(crate) fn observe(&mut self, origin: OriginId, sequence: u64) {
        if sequence > self.get(origin) {
            self.0.insert(origin, sequence);
        }
    }

    pub(crate) fn join(&self, other: &Self, max_origins: usize) -> Result<Self, ReplicationError> {
        if self.len() > max_origins || other.len() > max_origins {
            return Err(ReplicationError::LimitExceeded("causal origins"));
        }
        let mut joined = self.clone();
        for (&origin, &sequence) in &other.0 {
            joined.observe(origin, sequence);
            if joined.len() > max_origins {
                return Err(ReplicationError::LimitExceeded("causal origins"));
            }
        }
        Ok(joined)
    }

    /// Canonical origin-prefix entries in ascending origin order.
    #[must_use]
    pub fn entries(&self) -> impl ExactSizeIterator<Item = (OriginId, u64)> + '_ {
        self.0.iter().map(|(&origin, &sequence)| (origin, sequence))
    }
}

/// An encoded replacement document or a causal deletion marker.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum DocumentValue {
    /// An encoded document in the negotiated binary format.
    Bytes(Box<[u8]>),
    /// A deletion retained with its causal dot and context.
    Tombstone,
}

impl DocumentValue {
    pub(crate) fn byte_len(&self) -> usize {
        match self {
            Self::Bytes(bytes) => bytes.len(),
            Self::Tombstone => 0,
        }
    }
}

/// An immutable schema binding for one collection touched by an event.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CollectionBinding {
    /// Immutable collection identity within the database.
    pub collection: CollectionId,
    /// Nonzero, immutable schema generation.
    pub generation: u64,
    /// Digest of the complete canonical schema definition.
    pub schema_digest: [u8; 32],
}

/// One replacement of a document within a committed transaction.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DocumentChange {
    /// Immutable schema binding of this change's collection.
    pub binding: CollectionBinding,
    /// Target document.
    pub document: DocumentId,
    /// Whole-shard causal prefixes observed by the writer.
    pub observed: CausalContext,
    /// New encoded value or deletion marker.
    pub value: DocumentValue,
}

/// Bounds for admitted events and retained multi-value state. These are logical
/// limits; the caller must reserve actual encoded/decoded memory and SSD bytes.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ReplicationLimits {
    /// Maximum distinct origins in one document context.
    pub max_origins: usize,
    /// Maximum document changes in one atomic event.
    pub max_changes: usize,
    /// Maximum concurrent siblings retained for one document.
    pub max_siblings: usize,
    /// Maximum encoded bytes in one document value.
    pub max_value_bytes: usize,
    /// Maximum total encoded value bytes in an event or document state.
    pub max_payload_bytes: usize,
}

impl ReplicationLimits {
    /// Rejects a zero or inconsistent limit rather than leaving it ambiguous.
    ///
    /// # Errors
    ///
    /// Returns an error when limits cannot satisfy the v1 admission contract.
    pub const fn validate(self) -> Result<Self, ReplicationError> {
        if self.max_origins == 0
            || self.max_changes == 0
            || self.max_siblings == 0
            || self.max_value_bytes == 0
            || self.max_payload_bytes < self.max_value_bytes
            || self.max_changes > MAX_EVENT_CHANGES
            || self.max_origins > 1_024
            || self.max_siblings > 1_024
            || self.max_value_bytes > tb_core::MAX_VALUE_BYTES
            || self.max_payload_bytes > MAX_WAL_GROUP_BYTES
        {
            Err(ReplicationError::InvalidLimits)
        } else {
            Ok(self)
        }
    }
}

/// Actual encoded sizes supplied by the format/storage boundary. Logical
/// payload limits alone cannot prove that a WAL group fits the wire contract.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct EncodedEventSizes {
    /// Whole committed WAL transaction group, including local WAL metadata.
    pub wal_group_bytes: usize,
    /// Additional distributed wrapper metadata, separate from local WAL bytes.
    pub distributed_metadata_bytes: usize,
}

/// Actual encoded and decoded application batch sizes after framing. The
/// transport verifies these against the bytes it produced or received.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TransferEnvelopeSizes {
    /// Complete encoded application batch, including envelope overhead.
    pub encoded_bytes: usize,
    /// Complete decoded application batch, including envelope overhead.
    pub decoded_bytes: usize,
}

/// Identity and compatibility metadata that must be persisted with one
/// complete committed transaction, independent of physical layout.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct EventHeader {
    /// Stable per-origin committed event identity.
    pub id: EventId,
    /// Certified membership generation.
    pub epoch: MembershipEpoch,
    /// Certified configuration identity paired with the epoch.
    pub configuration: ConfigurationId,
    /// Schema, binary record, event, and merge-policy versions.
    pub versions: EventVersions,
    /// Bounded caller retry identity, unrelated to causal order.
    pub transaction_id: TransactionId,
    /// Digest of canonical committed bytes verified by format/storage.
    pub digest: EventDigest,
}

/// One complete, committed local transaction as a replicated event. It may
/// contain several documents, but this crate does not publish their visibility.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CommitEvent {
    /// Stable distributed identity.
    id: EventId,
    /// Certified configuration under which it was admitted.
    epoch: MembershipEpoch,
    /// Certified configuration identity paired with the epoch.
    configuration: ConfigurationId,
    /// Persisted schema/record/event/merge-policy versions.
    versions: EventVersions,
    /// Stable transaction retry identity, unrelated to causal order.
    transaction_id: TransactionId,
    /// Digest verified by the format/storage boundary.
    digest: EventDigest,
    /// Validated whole-group and metadata encoded sizes.
    encoded_sizes: EncodedEventSizes,
    /// Sum of encoded document value bytes, independent of local WAL overhead.
    payload_bytes: usize,
    /// Sorted unique bindings of all collections touched by the event.
    bindings: Box<[CollectionBinding]>,
    /// Exactly the document changes in this atomic transaction group.
    changes: Box<[DocumentChange]>,
}

impl CommitEvent {
    /// Validates a complete transaction group before merge or cursor admission.
    ///
    /// # Errors
    ///
    /// Returns an error for invalid epoch, causal order, duplicate documents,
    /// or any encoded/logical event limit violation.
    pub fn new(
        header: EventHeader,
        encoded_sizes: EncodedEventSizes,
        changes: Vec<DocumentChange>,
        limits: ReplicationLimits,
    ) -> Result<Self, ReplicationError> {
        let EventHeader {
            id,
            epoch,
            configuration,
            versions,
            transaction_id,
            digest,
        } = header;
        let limits = limits.validate()?;
        let versions = versions.validate()?;
        if epoch.get() == 0 {
            return Err(ReplicationError::InvalidEpoch);
        }
        if encoded_sizes.wal_group_bytes == 0
            || encoded_sizes.wal_group_bytes > MAX_WAL_GROUP_BYTES
            || encoded_sizes.distributed_metadata_bytes > MAX_DISTRIBUTED_METADATA_BYTES
        {
            return Err(ReplicationError::LimitExceeded("encoded event sizes"));
        }
        if changes.is_empty() || changes.len() > limits.max_changes {
            return Err(ReplicationError::LimitExceeded("event changes"));
        }
        let mut documents = BTreeSet::new();
        let mut bindings = BTreeMap::new();
        let mut payload_bytes = 0_usize;
        for change in &changes {
            if change.binding.generation == 0 {
                return Err(ReplicationError::InvalidVersion);
            }
            if let Some(previous) = bindings.insert(change.binding.collection, change.binding)
                && previous != change.binding
            {
                return Err(ReplicationError::ConflictingBinding);
            }
            if !documents.insert((change.binding.collection, change.document)) {
                return Err(ReplicationError::DuplicateDocument);
            }
            if change.observed.len() > limits.max_origins {
                return Err(ReplicationError::LimitExceeded("causal origins"));
            }
            if change.observed.get(id.origin()) != id.sequence() - 1 {
                return Err(ReplicationError::InvalidOwnCausality);
            }
            let value_bytes = change.value.byte_len();
            if value_bytes > limits.max_value_bytes {
                return Err(ReplicationError::LimitExceeded("document value bytes"));
            }
            payload_bytes = payload_bytes
                .checked_add(value_bytes)
                .ok_or(ReplicationError::LimitExceeded("event payload bytes"))?;
            if payload_bytes > limits.max_payload_bytes {
                return Err(ReplicationError::LimitExceeded("event payload bytes"));
            }
        }
        if payload_bytes > encoded_sizes.wal_group_bytes {
            return Err(ReplicationError::LimitExceeded("encoded WAL group bytes"));
        }
        Ok(Self {
            id,
            epoch,
            configuration,
            versions,
            transaction_id,
            digest,
            encoded_sizes,
            payload_bytes,
            bindings: bindings.into_values().collect(),
            changes: changes.into_boxed_slice(),
        })
    }

    /// Stable distributed identity.
    #[must_use]
    pub const fn id(&self) -> EventId {
        self.id
    }

    /// Complete persisted event header.
    #[must_use]
    pub const fn header(&self) -> EventHeader {
        EventHeader {
            id: self.id,
            epoch: self.epoch,
            configuration: self.configuration,
            versions: self.versions,
            transaction_id: self.transaction_id,
            digest: self.digest,
        }
    }

    /// Certified membership epoch of the commit.
    #[must_use]
    pub const fn epoch(&self) -> MembershipEpoch {
        self.epoch
    }

    /// Certified membership configuration identity.
    #[must_use]
    pub const fn configuration(&self) -> ConfigurationId {
        self.configuration
    }

    /// Persisted schema, record, event, and merge-policy versions.
    #[must_use]
    pub const fn versions(&self) -> EventVersions {
        self.versions
    }

    /// Stable transaction retry identity.
    #[must_use]
    pub const fn transaction_id(&self) -> TransactionId {
        self.transaction_id
    }

    /// Digest of canonical committed bytes, verified by the integrating caller.
    #[must_use]
    pub const fn digest(&self) -> EventDigest {
        self.digest
    }

    /// Format-verified whole-group and metadata encoded sizes.
    #[must_use]
    pub const fn encoded_sizes(&self) -> EncodedEventSizes {
        self.encoded_sizes
    }

    /// Checks complete encoded and decoded application batches independently
    /// from local WAL size. The wire payload need not contain WAL-only bytes.
    ///
    /// # Errors
    ///
    /// Returns an error if a batch is empty, exceeds the v1 wire cap, or its
    /// decoded size cannot contain the document payload and wrapper metadata.
    pub fn validate_transfer_sizes(
        &self,
        sizes: TransferEnvelopeSizes,
    ) -> Result<(), ReplicationError> {
        let minimum_decoded = self
            .encoded_sizes
            .distributed_metadata_bytes
            .checked_add(self.payload_bytes)
            .ok_or(ReplicationError::LimitExceeded("decoded batch bytes"))?;
        if sizes.encoded_bytes == 0
            || sizes.encoded_bytes > MAX_BATCH_BYTES
            || sizes.decoded_bytes < minimum_decoded
            || sizes.decoded_bytes > MAX_BATCH_BYTES
        {
            return Err(ReplicationError::LimitExceeded("application batch bytes"));
        }
        Ok(())
    }

    /// Whole group of document changes.
    #[must_use]
    pub fn changes(&self) -> &[DocumentChange] {
        &self.changes
    }

    /// Sorted unique schema bindings used by this complete transaction.
    #[must_use]
    pub fn bindings(&self) -> &[CollectionBinding] {
        &self.bindings
    }
}

/// Validation or merge failure. Failures must not partially mutate state.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ReplicationError {
    /// Epoch zero is not certified.
    InvalidEpoch,
    /// All-zero membership configuration has no certified identity.
    InvalidConfiguration,
    /// A schema, record, or event format version is unspecified.
    InvalidVersion,
    /// This code does not implement the event's merge policy.
    UnsupportedMergePolicy,
    /// Origin sequence or causal prefix is invalid.
    InvalidSequence,
    /// All-zero origin incarnation is not a valid durable writer identity.
    InvalidIncarnation,
    /// A context encoded the same origin twice.
    DuplicateOrigin,
    /// A transaction changes a document more than once without canonicalization.
    DuplicateDocument,
    /// A collection was named with two schema generations or definitions.
    ConflictingBinding,
    /// Own previous transactions were not included in the observed prefix.
    InvalidOwnCausality,
    /// A configured limit is unusable.
    InvalidLimits,
    /// A bounded resource would be exceeded.
    LimitExceeded(&'static str),
    /// States or events address different documents or shard scopes.
    ScopeMismatch,
    /// The same dot names different values.
    ConflictingVersion,
}

impl fmt::Display for ReplicationError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{self:?}")
    }
}

impl Error for ReplicationError {}
