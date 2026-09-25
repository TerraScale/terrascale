//! Shared errors used by `TerraBase` engine contracts.

use thiserror::Error;

use crate::{AckProfileKind, CommitSeq};

/// A typed failure reported at an engine contract boundary.
#[derive(Debug, Error)]
pub enum CoreError {
    /// An identifier string is malformed or is not in canonical ULID form.
    #[error("identifier is not a canonical ULID")]
    InvalidIdentifier,

    /// A key exceeded the engine's fixed maximum encoded length.
    #[error("key is {actual_bytes} bytes; the maximum is {max_bytes} bytes")]
    KeyTooLong {
        /// Actual key length in bytes.
        actual_bytes: usize,
        /// Maximum supported key length in bytes.
        max_bytes: usize,
    },

    /// A key must contain at least one encoded byte.
    #[error("key must not be empty")]
    EmptyKey,

    /// A range's lower endpoint sorts after its upper endpoint.
    #[error("key range lower endpoint sorts after its upper endpoint")]
    InvalidKeyRange,

    /// A failure-domain name was empty or exceeded its supported length.
    #[error("failure-domain name is empty or too long")]
    InvalidFailureDomain,

    /// A replication acknowledgment profile has inconsistent membership or quorum constraints.
    #[error("invalid replicated acknowledgment profile: {reason}")]
    InvalidAckProfile {
        /// A stable explanation of the invalid configuration.
        reason: &'static str,
    },

    /// A caller attempted an operation whose resource budget cannot admit it.
    #[error(
        "capacity exceeded: requested {requested_bytes} bytes, {available_bytes} bytes available"
    )]
    CapacityExceeded {
        /// Requested bytes.
        requested_bytes: u64,
        /// Bytes available under the relevant budget.
        available_bytes: u64,
    },

    /// A write conflicts with a value or version observed by another transaction.
    #[error("transaction conflict")]
    Conflict,

    /// A caller reused a transaction ID with a different payload.
    #[error("transaction ID was already used for a different batch")]
    IdempotencyConflict,

    /// The stored result for a transaction ID is outside the retained idempotency window.
    #[error("transaction ID is older than the retained idempotency window")]
    IdempotencyExpired,

    /// A transaction ID expiry exceeds the configured maximum retry window.
    #[error("transaction ID expiry is outside the configured retry window")]
    InvalidTransactionExpiry,

    /// Persisted data failed an integrity or format check.
    #[error("corrupt data in {object}: {reason}")]
    Corrupt {
        /// The data object or logical region that failed validation.
        object: Box<str>,
        /// A concise reason for the failure.
        reason: Box<str>,
    },

    /// The selected profile cannot be honored by the current implementation or operation.
    #[error("acknowledgment profile {profile:?} is unsupported")]
    UnsupportedProfile {
        /// The selected profile category.
        profile: AckProfileKind,
    },

    /// A requested snapshot predates the oldest retained version.
    #[error("snapshot at sequence {requested} is older than retained sequence {oldest_retained}")]
    StaleSnapshot {
        /// Requested snapshot sequence.
        requested: CommitSeq,
        /// Oldest sequence for which data remains available.
        oldest_retained: CommitSeq,
    },

    /// A host I/O operation failed.
    #[error("I/O operation failed")]
    Io(#[from] std::io::Error),

    /// A write failed while crossing its selected durability barrier.
    #[error("durability barrier failed for sequence {commit_seq}: {source}")]
    Durability {
        /// Sequence for the transaction that could not be made durable.
        commit_seq: CommitSeq,
        /// The underlying I/O failure.
        #[source]
        source: std::io::Error,
    },
}
