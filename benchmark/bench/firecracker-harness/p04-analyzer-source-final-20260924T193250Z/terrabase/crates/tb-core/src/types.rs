//! Shared value types and wire-contract primitives.

use std::fmt;
use std::num::NonZeroU16;
use std::str::FromStr;

use ulid::Ulid;

use crate::CoreError;

/// Maximum encoded key size accepted by the initial engine contract.
pub const MAX_KEY_BYTES: usize = 4_096;

/// Maximum encoded value size accepted by the initial engine contract.
pub const MAX_VALUE_BYTES: usize = 1_048_576;

/// Maximum encoded bytes in one complete local committed WAL group.
pub const MAX_WAL_GROUP_BYTES: usize = 2 * 1_048_576;

/// Maximum mutations in one local committed WAL group.
pub const MAX_WAL_OPERATIONS: usize = 1_024;

/// Maximum replication event metadata in one event.
pub const MAX_EVENT_METADATA_BYTES: usize = 256 * 1_024;

/// Maximum encoded replication batch size, including one complete committed group.
pub const MAX_REPLICATION_BATCH_BYTES: usize = 4 * 1_048_576;

macro_rules! define_ulid_id {
    ($name:ident, $description:literal) => {
        #[doc = $description]
        #[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
        pub struct $name(Ulid);

        impl $name {
            /// Generates a new ULID identifier.
            ///
            /// Identifiers are generated explicitly so default construction cannot
            /// silently create a new identity.
            #[allow(
                clippy::new_without_default,
                reason = "A generated ID is an explicit identity choice; Default would hide that choice."
            )]
            #[must_use]
            pub fn new() -> Self {
                Self(Ulid::generate())
            }

            /// Creates an identifier from its canonical 16-byte ULID representation.
            #[must_use]
            pub const fn from_bytes(bytes: [u8; 16]) -> Self {
                Self(Ulid::from_bytes(bytes))
            }

            /// Returns the canonical big-endian 16-byte representation.
            #[must_use]
            pub const fn to_bytes(self) -> [u8; 16] {
                self.0.to_bytes()
            }

            /// Parses a canonical uppercase 26-character ULID string.
            ///
            /// The conversion rejects strings which parse to a ULID but are not in the
            /// canonical representation returned by [`fmt::Display`].
            ///
            /// # Errors
            ///
            /// Returns [`CoreError::InvalidIdentifier`] if `value` is not a canonical
            /// uppercase 26-character ULID string.
            pub fn parse_canonical(value: &str) -> Result<Self, CoreError> {
                let parsed = Ulid::from_string(value).map_err(|_| CoreError::InvalidIdentifier)?;
                if parsed.to_string() != value {
                    return Err(CoreError::InvalidIdentifier);
                }
                Ok(Self(parsed))
            }
        }

        impl FromStr for $name {
            type Err = CoreError;

            fn from_str(value: &str) -> Result<Self, Self::Err> {
                Self::parse_canonical(value)
            }
        }

        impl fmt::Display for $name {
            fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
                fmt::Display::fmt(&self.0, formatter)
            }
        }
    };
}

define_ulid_id!(TenantId, "A fixed-width ULID identifying a tenant.");
define_ulid_id!(DatabaseId, "A fixed-width ULID identifying a database.");
define_ulid_id!(CollectionId, "A fixed-width ULID identifying a collection.");
define_ulid_id!(DocumentId, "A fixed-width ULID identifying a document.");
define_ulid_id!(ReplicaId, "A fixed-width ULID identifying a replica.");

/// A bounded caller-supplied stable identity for one transaction attempt.
///
/// The bytes are scoped by database and shard. They are not a clock and must not be
/// ordered to infer transaction or causal order. The expiration is retry-window
/// metadata; it is not a commit timestamp or causal clock.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct TransactionId {
    expires_at_unix_ms: u64,
    nonce: [u8; 16],
}

impl TransactionId {
    /// Creates a transaction ID from its expiry and 16-byte random nonce.
    #[must_use]
    pub const fn new(expires_at_unix_ms: u64, nonce: [u8; 16]) -> Self {
        Self {
            expires_at_unix_ms,
            nonce,
        }
    }

    /// Decodes the canonical 24-byte representation: little-endian expiry, then nonce.
    #[must_use]
    pub fn from_bytes(bytes: [u8; 24]) -> Self {
        let mut expiry_bytes = [0; 8];
        expiry_bytes.copy_from_slice(&bytes[..8]);
        let expires_at_unix_ms = u64::from_le_bytes(expiry_bytes);
        let mut nonce = [0; 16];
        nonce.copy_from_slice(&bytes[8..]);
        Self::new(expires_at_unix_ms, nonce)
    }

    /// Returns the canonical 24-byte representation: little-endian expiry, then nonce.
    #[must_use]
    pub fn to_bytes(self) -> [u8; 24] {
        let mut bytes = [0; 24];
        bytes[..8].copy_from_slice(&self.expires_at_unix_ms.to_le_bytes());
        bytes[8..].copy_from_slice(&self.nonce);
        bytes
    }

    /// Returns the token's retry-window expiry in Unix milliseconds.
    #[must_use]
    pub const fn expires_at_unix_ms(self) -> u64 {
        self.expires_at_unix_ms
    }

    /// Returns the caller-supplied nonce.
    #[must_use]
    pub const fn nonce(self) -> [u8; 16] {
        self.nonce
    }

    /// Validates that this token is live and no farther ahead than the configured window.
    ///
    /// # Errors
    ///
    /// Returns [`CoreError::IdempotencyExpired`] if the token has expired, or
    /// [`CoreError::InvalidTransactionExpiry`] if it exceeds the configured retry window.
    pub fn validate_window(self, now_unix_ms: u64, max_window_ms: u64) -> Result<(), CoreError> {
        if self.expires_at_unix_ms <= now_unix_ms {
            return Err(CoreError::IdempotencyExpired);
        }
        let latest_allowed = now_unix_ms.saturating_add(max_window_ms);
        if self.expires_at_unix_ms > latest_allowed {
            return Err(CoreError::InvalidTransactionExpiry);
        }
        Ok(())
    }
}

/// A stable numeric shard identity scoped by database.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct ShardId(u64);

impl ShardId {
    /// Creates a shard identity from its numeric representation.
    #[must_use]
    pub const fn new(value: u64) -> Self {
        Self(value)
    }

    /// Returns the numeric shard identity.
    #[must_use]
    pub const fn get(self) -> u64 {
        self.0
    }
}

/// A monotonically increasing membership configuration generation.
#[derive(Clone, Copy, Debug, Default, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct MembershipEpoch(u64);

impl MembershipEpoch {
    /// Creates an epoch from its numeric representation.
    #[must_use]
    pub const fn new(value: u64) -> Self {
        Self(value)
    }

    /// Returns the numeric epoch.
    #[must_use]
    pub const fn get(self) -> u64 {
        self.0
    }
}

/// A replica identity paired with its restart incarnation.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct OriginId {
    /// Stable replica identity.
    pub replica_id: ReplicaId,
    /// Replica-local incarnation that changes when its causal history is reset.
    pub incarnation: [u8; 16],
}

/// A monotonically increasing sequence within one database shard.
#[derive(Clone, Copy, Debug, Default, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct CommitSeq(u64);

impl CommitSeq {
    /// The sequence before the first committed transaction.
    pub const ZERO: Self = Self(0);

    /// Creates a sequence from its numeric representation.
    #[must_use]
    pub const fn new(value: u64) -> Self {
        Self(value)
    }

    /// Returns the numeric sequence value.
    #[must_use]
    pub const fn get(self) -> u64 {
        self.0
    }
}

impl fmt::Display for CommitSeq {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{}", self.0)
    }
}

/// A stable point-in-time read position within one shard.
///
/// The database/shard identity is carried by the storage handle that created it.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct ReadSnapshot {
    /// The last commit visible to this snapshot.
    pub seq: CommitSeq,
}

impl ReadSnapshot {
    /// Creates a snapshot at the supplied commit sequence.
    #[must_use]
    pub const fn new(seq: CommitSeq) -> Self {
        Self { seq }
    }
}

/// Independently reported progress for each persistence and replication stage.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct CommitFrontiers {
    /// Highest contiguous transaction received by this node.
    pub received: Option<CommitSeq>,
    /// Highest contiguous transaction satisfying the selected durable-storage barrier.
    pub durable: Option<CommitSeq>,
    /// Highest contiguous transaction applied to query-visible state.
    pub applied: Option<CommitSeq>,
    /// Highest contiguous transaction included in verified object-storage publication.
    pub object_archived: Option<CommitSeq>,
}

/// The result and progress frontiers for one committed transaction.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CommitReceipt {
    /// Stable caller-supplied identity used for retry reconciliation.
    pub transaction_id: TransactionId,
    /// Sequence assigned to this committed transaction.
    pub commit_seq: CommitSeq,
    /// Highest contiguous progress at each independently tracked stage.
    pub frontiers: CommitFrontiers,
}

/// A validated primary or index key stored as owned encoded bytes.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct Key(Box<[u8]>);

impl Key {
    /// Creates a nonempty owned key, rejecting values longer than [`MAX_KEY_BYTES`].
    ///
    /// # Errors
    ///
    /// Returns [`CoreError::EmptyKey`] for an empty key or [`CoreError::KeyTooLong`]
    /// when the encoded bytes exceed [`MAX_KEY_BYTES`].
    pub fn new(bytes: impl Into<Box<[u8]>>) -> Result<Self, CoreError> {
        let bytes = bytes.into();
        if bytes.is_empty() {
            return Err(CoreError::EmptyKey);
        }
        if bytes.len() > MAX_KEY_BYTES {
            return Err(CoreError::KeyTooLong {
                actual_bytes: bytes.len(),
                max_bytes: MAX_KEY_BYTES,
            });
        }
        Ok(Self(bytes))
    }

    /// Returns the key's encoded bytes.
    #[must_use]
    pub fn as_bytes(&self) -> &[u8] {
        &self.0
    }

    /// Consumes the key and returns its owned encoded bytes.
    #[must_use]
    pub fn into_bytes(self) -> Box<[u8]> {
        self.0
    }
}

impl AsRef<[u8]> for Key {
    fn as_ref(&self) -> &[u8] {
        self.as_bytes()
    }
}

/// One inclusive, exclusive, or open endpoint in a byte-ordered key range.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub enum KeyBound {
    /// No endpoint is imposed on this side of the range.
    Unbounded,
    /// Include the key at this endpoint.
    Included(Key),
    /// Exclude the key at this endpoint.
    Excluded(Key),
}

/// A byte-ordered half-open-capable range over encoded keys.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub struct KeyRange {
    /// Lower range endpoint.
    start: KeyBound,
    /// Upper range endpoint.
    end: KeyBound,
}

impl KeyRange {
    /// Creates an ordered range from its lower and upper endpoint bounds.
    ///
    /// Equal exclusive endpoints form an empty range. Endpoints are compared by
    /// their encoded bytes, in lexicographic order.
    ///
    /// # Errors
    ///
    /// Returns [`CoreError::InvalidKeyRange`] when the lower endpoint sorts after the
    /// upper endpoint.
    pub fn new(start: KeyBound, end: KeyBound) -> Result<Self, CoreError> {
        let start_key = match &start {
            KeyBound::Unbounded => None,
            KeyBound::Included(key) | KeyBound::Excluded(key) => Some(key),
        };
        let end_key = match &end {
            KeyBound::Unbounded => None,
            KeyBound::Included(key) | KeyBound::Excluded(key) => Some(key),
        };
        if start_key
            .zip(end_key)
            .is_some_and(|(lower, upper)| lower > upper)
        {
            return Err(CoreError::InvalidKeyRange);
        }
        Ok(Self { start, end })
    }

    /// Returns the lower range endpoint.
    #[must_use]
    pub const fn start(&self) -> &KeyBound {
        &self.start
    }

    /// Returns the upper range endpoint.
    #[must_use]
    pub const fn end(&self) -> &KeyBound {
        &self.end
    }

    /// Creates an unbounded range containing every key.
    #[must_use]
    pub const fn all() -> Self {
        Self {
            start: KeyBound::Unbounded,
            end: KeyBound::Unbounded,
        }
    }
}

/// A named failure domain, such as a region or availability zone.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct FailureDomainId(Box<str>);

impl FailureDomainId {
    /// Creates a nonempty failure-domain name of at most 255 UTF-8 bytes.
    ///
    /// # Errors
    ///
    /// Returns [`CoreError::InvalidFailureDomain`] when the name is empty or exceeds
    /// 255 UTF-8 bytes.
    pub fn new(name: impl Into<Box<str>>) -> Result<Self, CoreError> {
        let name = name.into();
        if name.is_empty() || name.len() > 255 {
            return Err(CoreError::InvalidFailureDomain);
        }
        Ok(Self(name))
    }

    /// Returns the failure-domain name.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

/// One configured replica and its named failure domain.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub struct ReplicaMember {
    /// Replica identity.
    pub replica_id: ReplicaId,
    /// Failure domain in which the replica resides.
    pub failure_domain: FailureDomainId,
}

/// A configured replicated acknowledgment requirement.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicatedDurability {
    replica_set: Box<[ReplicaMember]>,
    required_replicas: NonZeroU16,
    required_failure_domains: NonZeroU16,
}

impl ReplicatedDurability {
    /// Creates a validated replica set and acknowledgment requirement.
    ///
    /// # Errors
    ///
    /// Returns [`CoreError::InvalidAckProfile`] when the replica set is empty, a
    /// replica ID is duplicated, or the requested replica/domain quorum is not
    /// satisfiable by the configured set.
    pub fn new(
        replica_set: Vec<ReplicaMember>,
        required_replicas: NonZeroU16,
        required_failure_domains: NonZeroU16,
    ) -> Result<Self, CoreError> {
        if replica_set.is_empty() {
            return Err(CoreError::InvalidAckProfile {
                reason: "replica set must not be empty",
            });
        }
        if usize::from(required_replicas.get()) > replica_set.len() {
            return Err(CoreError::InvalidAckProfile {
                reason: "required replicas exceed configured replica set",
            });
        }
        if required_failure_domains > required_replicas {
            return Err(CoreError::InvalidAckProfile {
                reason: "required failure domains exceed required replicas",
            });
        }

        for (index, member) in replica_set.iter().enumerate() {
            if replica_set[..index]
                .iter()
                .any(|previous| previous.replica_id == member.replica_id)
            {
                return Err(CoreError::InvalidAckProfile {
                    reason: "replica IDs must be unique",
                });
            }
        }

        let unique_domains = replica_set
            .iter()
            .map(|member| &member.failure_domain)
            .collect::<std::collections::HashSet<_>>()
            .len();
        if usize::from(required_failure_domains.get()) > unique_domains {
            return Err(CoreError::InvalidAckProfile {
                reason: "required failure domains exceed configured domains",
            });
        }

        Ok(Self {
            replica_set: replica_set.into_boxed_slice(),
            required_replicas,
            required_failure_domains,
        })
    }

    /// Returns the configured replicas and their failure domains.
    #[must_use]
    pub fn replica_set(&self) -> &[ReplicaMember] {
        &self.replica_set
    }

    /// Returns the minimum number of replicas that must durably record a commit.
    #[must_use]
    pub const fn required_replicas(&self) -> NonZeroU16 {
        self.required_replicas
    }

    /// Returns the minimum number of distinct failure domains required for a commit.
    #[must_use]
    pub const fn required_failure_domains(&self) -> NonZeroU16 {
        self.required_failure_domains
    }
}

/// The explicit durability policy selected for a database.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum AckProfile {
    /// Require a synced local SSD write-ahead log before acknowledgment.
    LocalDurable,
    /// Require the configured replica and failure-domain quorum before acknowledgment.
    ReplicatedDurable(ReplicatedDurability),
    /// Require verified, recoverable object-storage publication before acknowledgment.
    ObjectDurable,
}

/// Stable category of an acknowledgment profile, without its configuration details.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum AckProfileKind {
    /// Local synced storage durability.
    LocalDurable,
    /// Configured replica and failure-domain durability.
    ReplicatedDurable,
    /// Verified object-storage publication durability.
    ObjectDurable,
}

impl AckProfile {
    /// Returns the profile category without exposing its membership configuration.
    #[must_use]
    pub const fn kind(&self) -> AckProfileKind {
        match self {
            Self::LocalDurable => AckProfileKind::LocalDurable,
            Self::ReplicatedDurable(_) => AckProfileKind::ReplicatedDurable,
            Self::ObjectDurable => AckProfileKind::ObjectDurable,
        }
    }
}

#[cfg(test)]
mod collection_id_tests {
    use super::CollectionId;

    #[test]
    fn collection_id_roundtrips_canonical_ulid_bytes_and_text() {
        let bytes = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        let id = CollectionId::from_bytes(bytes);
        let canonical = id.to_string();

        assert_eq!(id.to_bytes(), bytes);
        assert_eq!(canonical.len(), 26);
        assert!(matches!(
            CollectionId::parse_canonical(&canonical),
            Ok(parsed) if parsed == id
        ));
        assert!(matches!(
            canonical.parse::<CollectionId>(),
            Ok(parsed) if parsed == id
        ));
        assert!(CollectionId::parse_canonical(&canonical.to_ascii_lowercase()).is_err());
    }
}
