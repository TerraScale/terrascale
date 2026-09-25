//! Shard-local durable storage and ordered MVCC access.
//!
//! The first increment supports only `AckProfile::LocalDurable`. A receipt is
//! returned only after the complete batch frame reaches a successful WAL sync.

#![forbid(unsafe_code)]

mod floor;
mod local;
mod mode;
mod run;
mod work;

pub use local::{
    BatchFootprint, CommittedBatch, CommittedBatchCursor, D02PhaseTimes, PlannedBatch,
    PreparedBatch, PreparedBatchOutcome, StageWorkPlan, StageWorkReservation, Storage,
    StorageConfig, StorageStats,
};
pub use mode::DistributedDocumentMode;
pub use work::ScanWork;

use std::path::PathBuf;

use tb_core::{Key, TransactionId};

/// One owned mutation in a shard-local atomic batch.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum Mutation {
    /// Store or replace a value under a byte-ordered key.
    Put { key: Key, value: Vec<u8> },
    /// Hide the current value under a byte-ordered key.
    Delete { key: Key },
}

impl Mutation {
    /// Key affected by this mutation.
    #[must_use]
    pub fn key(&self) -> &Key {
        match self {
            Self::Put { key, .. } | Self::Delete { key } => key,
        }
    }
}

/// A returned value and the sequence that last changed it.
pub struct VersionedValue {
    /// The committed sequence of this version.
    pub sequence: tb_core::CommitSeq,
    /// The bytes supplied by the mutation caller.
    value: Vec<u8>,
    pub(crate) _permit: tb_core::Reservation,
}

impl VersionedValue {
    /// Borrow the admitted value bytes.
    #[must_use]
    pub fn bytes(&self) -> &[u8] {
        &self.value
    }
}

/// A row returned by a bounded ordered scan.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ScanRow {
    /// Byte-ordered key.
    pub key: Key,
    /// Selected visible value and its version.
    pub sequence: tb_core::CommitSeq,
    /// The selected value bytes.
    pub value: Vec<u8>,
}

/// A bounded scan page that owns its output-memory reservation.
pub struct ScanPage {
    /// Rows sorted by byte key ascending.
    rows: Vec<ScanRow>,
    pub(crate) _permit: tb_core::Reservation,
}

impl ScanPage {
    /// Borrow the rows while their output reservation remains held.
    #[must_use]
    pub fn rows(&self) -> &[ScanRow] {
        &self.rows
    }
}

/// An error at the local storage boundary.
#[derive(Debug, thiserror::Error)]
pub enum StorageError {
    /// Shared contract rejected this operation.
    #[error(transparent)]
    Core(#[from] tb_core::CoreError),
    /// An encoded file or frame failed validation.
    #[error(transparent)]
    Format(#[from] tb_format::FormatError),
    /// Memory admission rejected the proposed operation.
    #[error(transparent)]
    Admission(#[from] tb_core::AdmissionError),
    /// Host filesystem operation failed.
    #[error(transparent)]
    Io(#[from] std::io::Error),
    /// A file or state invariant did not hold.
    #[error("storage corruption: {0}")]
    Corrupt(String),
    /// A configured size or count limit would be exceeded.
    #[error("storage capacity exceeded: {0}")]
    Capacity(&'static str),
    /// A batch was empty, repeated a key, or had invalid value size.
    #[error("invalid batch: {0}")]
    InvalidBatch(&'static str),
    /// This shard stopped writes after a durability or apply uncertainty.
    #[error("shard requires recovery after an uncertain write")]
    Poisoned,
    /// The retry token expired or was pruned from its declared window.
    #[error("transaction idempotency window expired")]
    IdempotencyExpired,
    /// A retry token was reused with different canonical batch bytes.
    #[error("transaction ID reused with a different batch")]
    IdempotencyConflict,
    /// A key has a newer committed version than the caller's snapshot.
    #[error("write conflict")]
    Conflict,
    /// Only local durable acknowledgment is implemented by this crate.
    #[error("unsupported acknowledgment profile")]
    UnsupportedProfile,
    /// The requested snapshot is beyond this shard's committed frontier.
    #[error("invalid snapshot sequence")]
    InvalidSnapshot,
    /// An unsupported or invalid local directory layout was encountered.
    #[error("invalid storage directory {path}: {reason}")]
    InvalidDirectory { path: PathBuf, reason: &'static str },
}

/// Stable retry identity used by the local batch API.
pub type BatchId = TransactionId;

/// Deliberately terminate a feature-enabled subprocess at a durability boundary.
pub(crate) fn crash_at(point: &str) {
    #[cfg(feature = "test-hooks")]
    if matches!(std::env::var("TB_STORAGE_CRASH_POINT").as_deref(), Ok(name) if name == point) {
        std::process::exit(86);
    }
    #[cfg(not(feature = "test-hooks"))]
    let _ = point;
}
