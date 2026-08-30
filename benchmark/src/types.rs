//! Shared logical values used by the `TerraBase` public seams.

#![allow(dead_code)]
#![allow(clippy::doc_markdown, clippy::missing_errors_doc, clippy::must_use_candidate)]

use std::{
    collections::BTreeMap,
    fmt,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::{Duration, Instant},
};

use crate::operations::RequestValidationError;

/// The maximum byte length of a logical identifier.
const MAX_IDENTIFIER_BYTES: usize = 255;

/// Validates one logical identifier text: non-empty, at most
/// [`MAX_IDENTIFIER_BYTES`] bytes, no control characters, and no leading or
/// trailing whitespace.
fn validate_identifier_text(value: &str) -> Result<(), RequestValidationError> {
    if value.is_empty() {
        return Err(RequestValidationError::EmptyField);
    }
    if value.len() > MAX_IDENTIFIER_BYTES {
        return Err(RequestValidationError::InvalidIdentifier);
    }
    if value.chars().any(char::is_control) {
        return Err(RequestValidationError::InvalidIdentifier);
    }
    if value != value.trim() {
        return Err(RequestValidationError::InvalidIdentifier);
    }
    Ok(())
}

macro_rules! logical_identifier {
    ($name:ident, $error:ident, $description:literal) => {
        #[doc = $description]
        #[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
        pub struct $name(String);

        impl $name {
            /// Creates a validated logical identifier.
            ///
            /// The text must be non-empty, at most 255 bytes, free of control
            /// characters, and carry no leading or trailing whitespace.
            pub fn try_new(value: &str) -> Result<Self, RequestValidationError> {
                validate_identifier_text(value)?;
                Ok(Self(value.to_owned()))
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

impl DocumentMetadata {
    pub(crate) fn with_version(version: u64) -> Self {
        Self { version }
    }

    /// Returns the document's current version.
    pub fn version(&self) -> u64 {
        self.version
    }
}

/// A logical document independent of its durable representation.
#[derive(Clone, Debug, PartialEq)]
pub struct Document {
    id: DocumentId,
    collection: CollectionName,
    value: DocumentValue,
    metadata: DocumentMetadata,
}

impl Document {
    /// Creates a logical document from validated parts.
    ///
    /// The document starts at version zero; the Engine assigns the published
    /// version when the containing transaction commits.
    pub fn new(id: DocumentId, collection: CollectionName, value: DocumentValue) -> Self {
        Self { id, collection, value, metadata: DocumentMetadata::default() }
    }

    /// Returns the document identity.
    pub fn id(&self) -> &DocumentId {
        &self.id
    }

    /// Returns the owning collection name.
    pub fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the structured document value.
    pub fn value(&self) -> &DocumentValue {
        &self.value
    }

    /// Returns the document metadata.
    pub fn metadata(&self) -> &DocumentMetadata {
        &self.metadata
    }

    pub(crate) fn set_version(&mut self, version: u64) {
        self.metadata = DocumentMetadata::with_version(version);
    }

    /// Replaces the structured value in place, keeping identity, collection,
    /// and metadata. Crate-only: the patch seam (GAP-D07) rewrites a read
    /// document's value; every other write path constructs a new document.
    pub(crate) fn set_value(&mut self, value: DocumentValue) {
        self.value = value;
    }
}

/// A requested upper bound for a typed operation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Deadline {
    instant: Instant,
}

impl Deadline {
    /// Creates a deadline after a positive duration.
    ///
    /// A zero or negative duration is invalid because every typed operation
    /// must retain a non-empty execution window.
    pub fn after(duration: Duration) -> Result<Self, RequestValidationError> {
        if duration.is_zero() {
            return Err(RequestValidationError::InvalidDeadline);
        }
        Ok(Self { instant: Instant::now() + duration })
    }

    pub(crate) fn instant(self) -> Instant {
        self.instant
    }

    /// Reports whether the deadline has passed.
    pub fn is_expired(&self) -> bool {
        Instant::now() >= self.instant
    }
}

/// A shareable cancellation signal for a typed operation.
///
/// Cloning a cancellation creates an independent signal: cancelling one clone
/// never cancels another. `PartialEq` and `Eq` compare signal identity, so two
/// distinct signals are never equal even when both remain uncancelled.
pub struct Cancellation {
    flag: Arc<AtomicBool>,
}

impl Clone for Cancellation {
    fn clone(&self) -> Self {
        Self { flag: Arc::new(AtomicBool::new(self.is_cancelled())) }
    }
}

impl fmt::Debug for Cancellation {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("Cancellation")
            .field("cancelled", &self.is_cancelled())
            .finish_non_exhaustive()
    }
}

impl Default for Cancellation {
    fn default() -> Self {
        Self::new()
    }
}

impl PartialEq for Cancellation {
    fn eq(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.flag, &other.flag)
    }
}

impl Eq for Cancellation {}

impl Cancellation {
    /// Creates a cancellation signal in the not-cancelled state.
    pub fn new() -> Self {
        Self { flag: Arc::new(AtomicBool::new(false)) }
    }

    /// Marks this signal as cancelled.
    pub fn cancel(&self) {
        self.flag.store(true, Ordering::Release);
    }

    /// Reports whether cancellation has been requested.
    pub fn is_cancelled(&self) -> bool {
        self.flag.load(Ordering::Acquire)
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

    /// Returns the storage sequence this snapshot observed — the
    /// replication position a read receipt reports (P4's read-level
    /// surface).
    #[must_use]
    pub fn sequence(&self) -> StorageSequence {
        self.sequence
    }

    /// Returns the immutable set of index generations this snapshot observed.
    pub(crate) fn indexes(&self) -> &IndexGenerationSet {
        &self.indexes
    }
}

/// The generation of one named index.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexGeneration {
    namespace: String,
    persisted_epoch: u64,
    sequence: StorageSequence,
}

impl IndexGeneration {
    /// Creates a generation from its namespace, persisted epoch, and
    /// monotonic sequence.
    pub(crate) fn new(namespace: String, persisted_epoch: u64, sequence: StorageSequence) -> Self {
        Self { namespace, persisted_epoch, sequence }
    }

    /// Returns the index namespace, which identifies the tenant and logical
    /// index definition.
    pub fn namespace(&self) -> &str {
        &self.namespace
    }

    /// Returns the persisted epoch of this generation.
    ///
    /// The epoch is created with the namespace and persists in the index
    /// manifest; a new epoch is allowed only when the namespace is explicitly
    /// recreated under its migration contract.
    pub fn epoch(&self) -> u64 {
        self.persisted_epoch
    }

    /// Returns the monotonic non-wrapping sequence of this generation.
    pub fn sequence(&self) -> StorageSequence {
        self.sequence
    }
}

/// The immutable set of index generations visible to a read snapshot.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct IndexGenerationSet(Vec<IndexGeneration>);

impl IndexGenerationSet {
    /// Creates a set from its generations.
    pub(crate) fn from_generations(generations: Vec<IndexGeneration>) -> Self {
        Self(generations)
    }

    /// Returns the generation recorded for one namespace and epoch, if any.
    pub fn generation(&self, namespace: &str, epoch: u64) -> Option<IndexGeneration> {
        self.0
            .iter()
            .find(|generation| generation.namespace() == namespace && generation.epoch() == epoch)
            .cloned()
    }

    /// Returns every recorded generation.
    pub fn generations(&self) -> impl Iterator<Item = &IndexGeneration> {
        self.0.iter()
    }
}

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

#[cfg(test)]
mod tests {
    use super::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue, OperationId,
        RequestValidationError, TenantId, validate_identifier_text,
    };
    use std::time::Duration;

    #[test]
    fn identifiers_reject_empty_text() {
        assert_eq!(TenantId::try_new(""), Err(RequestValidationError::EmptyField));
    }

    #[test]
    fn identifiers_reject_text_over_255_bytes() {
        let long = "a".repeat(256);
        assert_eq!(TenantId::try_new(&long), Err(RequestValidationError::InvalidIdentifier));
        let boundary = "a".repeat(255);
        assert!(TenantId::try_new(&boundary).is_ok());
    }

    #[test]
    fn identifiers_reject_control_characters_and_surrounding_whitespace() {
        assert_eq!(
            TenantId::try_new("tenant\u{7}"),
            Err(RequestValidationError::InvalidIdentifier)
        );
        assert_eq!(TenantId::try_new(" tenant"), Err(RequestValidationError::InvalidIdentifier));
        assert_eq!(TenantId::try_new("tenant "), Err(RequestValidationError::InvalidIdentifier));
        assert!(TenantId::try_new("tenant-a").is_ok());
        assert_eq!(
            validate_identifier_text("tenant\t"),
            Err(RequestValidationError::InvalidIdentifier)
        );
    }

    #[test]
    fn operation_identity_reports_its_own_error_category() {
        assert_eq!(OperationId::try_new(""), Err(RequestValidationError::EmptyField));
        assert!(OperationId::try_new("op-1").is_ok());
    }

    #[test]
    fn identifiers_keep_canonical_text() {
        let document = DocumentId::try_new("doc-42").expect("valid identifier");
        assert_eq!(document.as_str(), "doc-42");
        assert_eq!(document.to_string(), "doc-42");
        let collection = CollectionName::try_new("users").expect("valid identifier");
        assert_eq!(collection.as_str(), "users");
    }

    #[test]
    fn deadline_rejects_zero_duration() {
        assert_eq!(Deadline::after(Duration::ZERO), Err(RequestValidationError::InvalidDeadline));
        let deadline = Deadline::after(Duration::from_secs(30)).expect("valid deadline");
        assert!(!deadline.is_expired());
    }

    #[test]
    fn cancellation_clone_is_independent_and_identity_compared() {
        let signal = Cancellation::new();
        assert!(!signal.is_cancelled());
        let clone = signal.clone();
        assert_ne!(
            signal, clone,
            "equality compares signal identity, so a clone is a distinct signal"
        );
        signal.cancel();
        assert!(signal.is_cancelled());
        assert!(!clone.is_cancelled(), "clones must not share cancellation");
        assert_ne!(signal, Cancellation::new());
    }

    #[test]
    fn document_starts_at_version_zero_and_reports_parts() {
        let document = Document::new(
            DocumentId::try_new("doc-1").expect("valid identifier"),
            CollectionName::try_new("users").expect("valid identifier"),
            DocumentValue::Object(
                [("name".to_owned(), DocumentValue::String("ada".to_owned()))]
                    .into_iter()
                    .collect(),
            ),
        );
        assert_eq!(document.metadata().version(), 0);
        assert_eq!(document.id().as_str(), "doc-1");
        assert_eq!(document.collection().as_str(), "users");
        assert!(matches!(document.value(), DocumentValue::Object(_)));
    }
}
