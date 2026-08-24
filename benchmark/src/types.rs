//! Shared logical values used by the `TerraBaseV2` public seams.

#![allow(dead_code)]
#![allow(
    clippy::doc_markdown,
    clippy::missing_errors_doc,
    clippy::must_use_candidate,
    clippy::unimplemented
)]

use std::{
    collections::BTreeMap,
    fmt,
    time::{Duration, Instant},
};

use crate::operations::RequestValidationError;

macro_rules! logical_identifier {
    ($name:ident, $error:ident, $description:literal) => {
        #[doc = $description]
        #[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
        pub struct $name(String);

        impl $name {
            /// Creates a validated logical identifier.
            pub fn try_new(value: &str) -> Result<Self, RequestValidationError> {
                let _ = (value, RequestValidationError::$error);
                unimplemented!("logical identifier validation belongs to a later behavior packet")
            }

            /// Returns the canonical identifier text.
            pub fn as_str(&self) -> &str {
                &self.0
            }
        }

        impl fmt::Display for $name {
            fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
                formatter.write_str(&self.0)
            }
        }
    };
}

logical_identifier!(TenantId, InvalidIdentifier, "A validated tenant identifier.");
logical_identifier!(DocumentId, InvalidIdentifier, "A validated document identifier.");
logical_identifier!(CollectionName, InvalidIdentifier, "A validated collection name.");
logical_identifier!(OperationId, InvalidOperationIdentity, "A stable operation identity.");

/// A validated tenant and engine pairing accepted by the Engine seam.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TenantScope {
    tenant: TenantId,
    engine_identity: u128,
}

impl TenantScope {
    pub(crate) fn from_parts(tenant: TenantId, engine_identity: u128) -> Self {
        Self { tenant, engine_identity }
    }

    pub(crate) fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    pub(crate) fn engine_identity(&self) -> u128 {
        self.engine_identity
    }
}

/// A monotonically increasing local storage sequence.
#[derive(Clone, Copy, Debug, Default, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct StorageSequence(u64);

impl StorageSequence {
    pub(crate) const fn new(value: u64) -> Self {
        Self(value)
    }

    /// Returns the sequence number.
    pub const fn get(self) -> u64 {
        self.0
    }
}

/// A structured value suitable for a document field.
#[derive(Clone, Debug, PartialEq)]
pub enum DocumentValue {
    /// A null value.
    Null,
    /// A boolean value.
    Boolean(bool),
    /// A signed integer value.
    Integer(i64),
    /// A floating-point value.
    Number(f64),
    /// A text value.
    String(String),
    /// An ordered sequence of structured values.
    Array(Vec<Self>),
    /// A map of field names to structured values.
    Object(BTreeMap<String, Self>),
}

/// Metadata attached to a logical document.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct DocumentMetadata {
    version: u64,
}

/// A logical document independent of its durable representation.
#[derive(Clone, Debug, PartialEq)]
pub struct Document {
    id: DocumentId,
    collection: CollectionName,
    value: DocumentValue,
    metadata: DocumentMetadata,
}

/// A requested upper bound for a typed operation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Deadline {
    instant: Instant,
}

impl Deadline {
    /// Creates a deadline after a positive duration.
    pub fn after(duration: Duration) -> Result<Self, RequestValidationError> {
        let _ = duration;
        unimplemented!("deadline validation belongs to a later behavior packet")
    }

    pub(crate) fn instant(self) -> Instant {
        self.instant
    }
}

/// A shareable cancellation signal for a typed operation.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct Cancellation {
    marker: std::marker::PhantomData<()>,
}

impl Cancellation {
    /// Creates a cancellation signal in the not-cancelled state.
    pub fn new() -> Self {
        unimplemented!("cancellation behavior belongs to a later behavior packet")
    }

    /// Marks this signal as cancelled.
    pub fn cancel(&self) {
        let _ = self;
        unimplemented!("cancellation behavior belongs to a later behavior packet")
    }

    /// Reports whether cancellation has been requested.
    pub fn is_cancelled(&self) -> bool {
        let _ = self;
        unimplemented!("cancellation behavior belongs to a later behavior packet")
    }
}

/// An immutable read view identified by one Engine, tenant, and sequence.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReadSnapshot {
    engine_identity: u128,
    tenant: TenantId,
    sequence: StorageSequence,
    indexes: IndexGenerationSet,
}

impl ReadSnapshot {
    pub(crate) fn new(
        engine_identity: u128,
        tenant: TenantId,
        sequence: StorageSequence,
        indexes: IndexGenerationSet,
    ) -> Self {
        Self { engine_identity, tenant, sequence, indexes }
    }

    pub(crate) fn engine_identity(&self) -> u128 {
        self.engine_identity
    }

    pub(crate) fn tenant(&self) -> &TenantId {
        &self.tenant
    }
}

/// The generation of one named index.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexGeneration {
    namespace: String,
    persisted_epoch: u64,
    sequence: StorageSequence,
}

/// The immutable set of index generations visible to a read snapshot.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct IndexGenerationSet(Vec<IndexGeneration>);

/// The local durability point requested for a commit.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LocalDurability {
    /// The commit is accepted by the local buffer.
    Buffered,
    /// The commit has reached local durable storage writes.
    Written,
    /// The commit has completed local post-publication synchronization.
    Synced,
}

/// The expected version for a document mutation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ExpectedVersion {
    /// Accept any current version.
    Any,
    /// Require that the document does not yet exist.
    New,
    /// Require the given current version.
    Exact(u64),
}

/// The allowed disposition for a rejected or uncertain retry.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RetryDisposition {
    /// Do not retry the operation.
    Never,
    /// Retry with the original operation identity.
    SameOperation,
    /// Retry as a new logical operation.
    NewOperation,
    /// Resolve the durable outcome before deciding how to retry.
    AfterRecovery,
}
