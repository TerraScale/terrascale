//! The attachment content plane: typed byte storage for attachments.
//!
//! The metadata family records *that* a document has an attachment; this
//! plane stores *the bytes*. Content is addressed by the hex SHA-256 digest
//! the plane computes itself — the V1 shared-hash deduplication model, where
//! the stream key is the content hash — and lives in the reserved family
//! collection as one manifest record plus bounded base64 segment records, so
//! a reader never holds more than one segment in memory.
//!
//! Content records keep a reserved kind key of their own
//! (`family:attachment-content/...`), disjoint from the metadata owner space
//! `family:attachments/<document>`, so a content record identifier can never
//! collide with an attachment-metadata record however a document is named;
//! and every segment identifier is read before it is written or cleared, so
//! a record that already holds foreign state fails closed as
//! [`AttachmentContentError::ReservedStateInvalid`] instead of being
//! destroyed.
//!
//! Every write rides the caller's Engine [`Transaction`] and every read
//! observes one immutable snapshot through a never-committed transaction,
//! exactly like the rest of the family seam. The plane is attachment content,
//! not a generic byte escape hatch: it is reachable only through the family
//! seam, is tenant-scoped, is bounded by explicit limits, and carries no
//! page, frame, or codec buffer of the storage layer.
//!
//! The V1-observable semantics pinned here — range normalization, disposal,
//! end-of-stream, and truncation — are cited against the pinned V1 commit in
//! `docs/families/attachment-content-plane.md`.

use crate::{
    engine::Transaction,
    operations::RequestValidationError,
    types::{Cancellation, Deadline, DocumentId, OperationId, TenantScope},
};

use super::super::{
    FamilyError, FamilyReadContext, FamilyStore, map_begin_error,
    state::{ATTACHMENT_CONTENT_KIND, Records, record_id},
    store,
};
use super::codec::{self, ContentManifest};
use super::digest::{Sha256, to_hex};

/// The largest byte length of one staged segment.
///
/// One segment becomes one reserved record, and one record must stay far
/// below the storage document ceiling, so the plane splits content at this
/// bound and every decode holds at most this many bytes.
pub const MAX_SEGMENT_BYTES: usize = 64 * 1024;

/// The largest staged content size the plane accepts today.
///
/// V1 streams attachments of unbounded size; the V2 engine plane starts with
/// an explicit, typed ceiling so over-limit requests fail with
/// [`AttachmentContentError::SizeLimitExceeded`] instead of exhausting
/// memory. Raising or lifting it is a recorded decision, never a silent one
/// (see `docs/families/attachment-content-plane.md`).
pub const MAX_CONTENT_BYTES: u64 = 8 * 1024 * 1024;

/// The largest accepted segment count: `ceil(MAX_CONTENT_BYTES /
/// MAX_SEGMENT_BYTES)`, the canonical bound the byte ceiling implies at the
/// segment cap.
///
/// A byte-budgeted write can never stage more records than that — 8 MiB of
/// one-byte segments must not become millions of records and an unbounded
/// reader index — so a payload with more segments is rejected at
/// construction with
/// [`RequestValidationError::InvalidFamilyPayload`]. Callers holding
/// unsegmented bytes chunk to the segment cap instead of splitting by
/// record-amplifying unit.
#[allow(
    clippy::cast_possible_truncation,
    reason = "the ceil of the 8 MiB ceiling over the 64 KiB segment cap is 128, far inside every usize range"
)]
pub const MAX_SEGMENT_COUNT: usize = MAX_CONTENT_BYTES.div_ceil(MAX_SEGMENT_BYTES as u64) as usize;

/// Validates a content digest: sixty-four lowercase hex characters.
fn is_digest(value: &str) -> bool {
    value.len() == 64
        && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

/// Rejects a deadline that has already elapsed.
fn validate_deadline(deadline: &Deadline) -> Result<(), RequestValidationError> {
    if deadline.is_expired() {
        return Err(RequestValidationError::InvalidDeadline);
    }
    Ok(())
}

/// Rejects a cancellation signal that is already cancelled.
fn validate_cancellation(signal: &Cancellation) -> Result<(), RequestValidationError> {
    if signal.is_cancelled() {
        return Err(RequestValidationError::InvalidCancellation);
    }
    Ok(())
}

/// One bounded piece of attachment content.
///
/// A segment is the unit of staged byte storage: at most
/// [`MAX_SEGMENT_BYTES`] bytes, so one segment record stays far below the
/// storage document ceiling and one reader decode stays bounded.
#[derive(Clone, Debug, PartialEq)]
pub struct AttachmentSegment {
    bytes: Vec<u8>,
}

impl AttachmentSegment {
    /// Creates one segment, rejecting a byte length above the segment cap.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] when `bytes`
    /// is longer than [`MAX_SEGMENT_BYTES`].
    pub fn try_new(bytes: &[u8]) -> Result<Self, RequestValidationError> {
        if bytes.len() > MAX_SEGMENT_BYTES {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        Ok(Self { bytes: bytes.to_vec() })
    }

    /// Returns the segment bytes.
    #[must_use]
    pub fn bytes(&self) -> &[u8] {
        &self.bytes
    }

    /// Returns the segment byte length.
    #[must_use]
    pub fn len(&self) -> usize {
        self.bytes.len()
    }

    /// Reports whether the segment holds no bytes.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.bytes.is_empty()
    }
}

/// A typed attachment-content write: whole content as bounded segments.
///
/// The plane computes the content digest itself; `expected_digest` only
/// declares what the caller believes the digest to be, and a disagreement
/// is the typed [`AttachmentContentError::DigestMismatch`], never a silent
/// acceptance.
#[derive(Clone, Debug, PartialEq)]
pub struct AttachmentContentWrite {
    scope: TenantScope,
    expected_digest: Option<String>,
    segments: Vec<AttachmentSegment>,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl AttachmentContentWrite {
    /// Creates a validated content write.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for a
    /// malformed declared digest or a segment count above
    /// [`MAX_SEGMENT_COUNT`],
    /// [`RequestValidationError::InvalidDeadline`] for an elapsed deadline,
    /// and [`RequestValidationError::InvalidCancellation`] for an already
    /// cancelled signal.
    pub fn try_new(
        scope: TenantScope,
        expected_digest: Option<String>,
        segments: Vec<AttachmentSegment>,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if expected_digest.as_ref().is_some_and(|digest| !is_digest(digest)) {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        if segments.len() > MAX_SEGMENT_COUNT {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { scope, expected_digest, segments, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the caller-declared digest, if any.
    #[must_use]
    pub fn expected_digest(&self) -> Option<&str> {
        self.expected_digest.as_deref()
    }

    /// Returns the staged segments in content order.
    #[must_use]
    pub fn segments(&self) -> &[AttachmentSegment] {
        &self.segments
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// A typed attachment-content deletion by digest.
#[derive(Clone, Debug, PartialEq)]
pub struct AttachmentContentDelete {
    scope: TenantScope,
    digest: String,
    operation_id: OperationId,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl AttachmentContentDelete {
    /// Creates a validated content deletion.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for a
    /// malformed digest,
    /// [`RequestValidationError::InvalidDeadline`] for an elapsed deadline,
    /// and [`RequestValidationError::InvalidCancellation`] for an already
    /// cancelled signal.
    pub fn try_new(
        scope: TenantScope,
        digest: String,
        operation_id: OperationId,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if !is_digest(&digest) {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { scope, digest, operation_id, deadline, cancellation })
    }

    /// Returns the validated tenant scope.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the digest of the content to remove.
    #[must_use]
    pub fn digest(&self) -> &str {
        &self.digest
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One typed attachment byte range.
///
/// The shapes mirror V1's single-range requests: an explicit `from` with an
/// optional inclusive `to`, or a suffix of the last `bytes` bytes. V1's
/// server normalizes both before serving; this plane resolves the same
/// normalization at open time.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AttachmentRange {
    /// The bytes from `from` through `to`, or through the last byte when
    /// `to` is `None`.
    Bytes {
        /// The inclusive first byte offset.
        from: u64,
        /// The inclusive last byte offset, or the content end when `None`.
        to: Option<u64>,
    },
    /// The last `bytes` bytes of the content.
    Suffix {
        /// How many trailing bytes the range covers.
        bytes: u64,
    },
}

/// A typed attachment-content read.
#[derive(Clone, Debug, PartialEq)]
pub struct AttachmentContentRead {
    digest: String,
    range: Option<AttachmentRange>,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl AttachmentContentRead {
    /// Creates a validated content read.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for a
    /// malformed digest, [`RequestValidationError::InvalidRange`] for an
    /// inverted `from`/`to` pair,
    /// [`RequestValidationError::InvalidDeadline`] for an elapsed deadline,
    /// and [`RequestValidationError::InvalidCancellation`] for an already
    /// cancelled signal.
    pub fn try_new(
        digest: String,
        range: Option<AttachmentRange>,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if !is_digest(&digest) {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        if let Some(AttachmentRange::Bytes { from, to: Some(to) }) = range
            && to < from
        {
            return Err(RequestValidationError::InvalidRange);
        }
        validate_deadline(&deadline)?;
        validate_cancellation(&cancellation)?;
        Ok(Self { digest, range, deadline, cancellation })
    }

    /// Returns the digest of the content to read.
    #[must_use]
    pub fn digest(&self) -> &str {
        &self.digest
    }

    /// Returns the requested range, if any.
    #[must_use]
    pub fn range(&self) -> Option<AttachmentRange> {
        self.range
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// The outcome of one staged content write.
///
/// Constructed only by the family store; callers observe it read-only.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AttachmentContentRecorded {
    digest: String,
    size: u64,
    segments: u64,
}

impl AttachmentContentRecorded {
    /// Creates one recorded outcome. Crate-only.
    pub(crate) fn new(digest: String, size: u64, segments: u64) -> Self {
        Self { digest, size, segments }
    }

    /// Returns the computed content digest.
    #[must_use]
    pub fn digest(&self) -> &str {
        &self.digest
    }

    /// Returns the staged content size in bytes.
    #[must_use]
    pub fn size(&self) -> u64 {
        self.size
    }

    /// Returns the number of staged segment records.
    #[must_use]
    pub fn segments(&self) -> u64 {
        self.segments
    }
}

/// Attachment content-plane failures.
///
/// Every case is typed and carries no byte value, storage detail, path, or
/// credential. The V1-observable distinctions stay distinct: a missing
/// attachment, an unsatisfiable range, a position past the declared length,
/// truncated bytes, a digest disagreement, an explicit size limit, and use
/// after disposal never collapse into one generic case.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AttachmentContentError {
    /// The Engine has closed.
    EngineClosed,
    /// The context and request use different tenants, or the transaction is
    /// not bound to the request's tenant scope.
    TenantMismatch,
    /// The snapshot belongs to another Engine.
    SnapshotEngineMismatch,
    /// The transaction belongs to another Engine.
    TransactionEngineMismatch,
    /// The write identity does not match the transaction.
    TransactionIdentityMismatch,
    /// The request deadline elapsed before the operation ran.
    DeadlineExceeded,
    /// The operation was cancelled.
    Cancelled,
    /// Durable storage returned a typed failure.
    StorageFailure,
    /// A reserved content record could not be read as content state.
    ReservedStateInvalid,
    /// No content is staged for the digest in this tenant's view.
    ContentMissing,
    /// The requested range cannot be satisfied by the content.
    RangeInvalid,
    /// The requested position lies past the reader's declared length.
    PositionInvalid,
    /// The staged content exceeds the plane's explicit size limit.
    SizeLimitExceeded,
    /// The caller-declared digest disagrees with the computed digest.
    DigestMismatch,
    /// Staged bytes end before the manifest's declared size.
    Truncated,
    /// The reader was disposed; V1 fails such reads as disposed-object use.
    Disposed,
}

impl From<FamilyError> for AttachmentContentError {
    #[allow(clippy::match_same_arms)] // every family case keeps its closed mapping
    fn from(error: FamilyError) -> Self {
        match error {
            FamilyError::EngineClosed => Self::EngineClosed,
            FamilyError::TenantMismatch => Self::TenantMismatch,
            FamilyError::SnapshotEngineMismatch => Self::SnapshotEngineMismatch,
            FamilyError::TransactionEngineMismatch => Self::TransactionEngineMismatch,
            FamilyError::TransactionIdentityMismatch => Self::TransactionIdentityMismatch,
            FamilyError::DeadlineExceeded => Self::DeadlineExceeded,
            FamilyError::Cancelled => Self::Cancelled,
            FamilyError::StorageFailure => Self::StorageFailure,
            FamilyError::ReservedStateInvalid => Self::ReservedStateInvalid,
            FamilyError::InvalidRequest => Self::StorageFailure,
            FamilyError::Conflict => Self::StorageFailure,
            FamilyError::DocumentMissing => Self::ContentMissing,
            FamilyError::CounterOverflow => Self::StorageFailure,
            FamilyError::TimeSeriesArityInvalid => Self::StorageFailure,
            FamilyError::TimeSeriesRangeInvalid => Self::StorageFailure,
            FamilyError::TimeSeriesPolicyInvalid => Self::StorageFailure,
            FamilyError::RevisionReferenceInvalid => Self::StorageFailure,
            FamilyError::ValidationPolicyMalformed => Self::StorageFailure,
            FamilyError::SchemaViolation => Self::StorageFailure,
            FamilyError::AttachmentNameDuplicate => Self::StorageFailure,
            FamilyError::LifecycleValueInvalid => Self::StorageFailure,
            FamilyError::CompareExchangeIndexStale => Self::StorageFailure,
            FamilyError::IdentitySeedInvalid => Self::StorageFailure,
            FamilyError::IdentityExhausted => Self::StorageFailure,
            FamilyError::TombstoneConflict => Self::StorageFailure,
        }
    }
}

/// Builds the manifest record identifier of one digest.
///
/// Content records live under
/// [`ATTACHMENT_CONTENT_KIND`](super::super::state::ATTACHMENT_CONTENT_KIND)
/// — a distinct reserved kind key, never inside the attachments metadata
/// owner space — so a manifest identifier cannot collide with a metadata
/// record however a document is named.
pub(super) fn manifest_id(digest: &str) -> Result<DocumentId, AttachmentContentError> {
    record_id(ATTACHMENT_CONTENT_KIND, &[digest])
        .map_err(|_| AttachmentContentError::StorageFailure)
}

/// Builds the segment record identifier of one digest and index.
///
/// Segment identifiers share the content plane's distinct kind key, so they
/// cannot collide with attachment-metadata records either.
pub(super) fn segment_id(digest: &str, index: u64) -> Result<DocumentId, AttachmentContentError> {
    let index = index.to_string();
    record_id(ATTACHMENT_CONTENT_KIND, &[digest, &index])
        .map_err(|_| AttachmentContentError::StorageFailure)
}

/// Guards one segment record identifier before the plane touches it.
///
/// An occupied segment id must already hold a decodable segment — the same
/// fail-closed guard the manifest read applies — so a foreign record
/// (attachment metadata, a reserved-collection collision) is reported as
/// typed reserved-state corruption instead of being silently overwritten or
/// cleared.
fn require_free_or_segment(
    records: &mut Records<'_, '_>,
    id: &DocumentId,
) -> Result<(), AttachmentContentError> {
    if let Some(occupied) = records.read(id)?
        && codec::decode_segment(&occupied).is_err()
    {
        return Err(AttachmentContentError::ReservedStateInvalid);
    }
    Ok(())
}

/// Reads and decodes one content manifest through the transaction.
fn load_manifest(
    records: &mut Records<'_, '_>,
    digest: &str,
) -> Result<ContentManifest, AttachmentContentError> {
    let id = manifest_id(digest)?;
    match records.read(&id)? {
        None => Err(AttachmentContentError::ContentMissing),
        Some(value) => {
            let manifest = codec::decode_manifest(&value)
                .map_err(|()| AttachmentContentError::ReservedStateInvalid)?;
            if manifest.digest != digest {
                return Err(AttachmentContentError::ReservedStateInvalid);
            }
            Ok(manifest)
        }
    }
}

/// Resolves one requested range against the content size.
///
/// This is V1's range normalization pinned at the engine: an open or
/// past-the-end `to` clamps to the last byte, a suffix longer than the
/// content resolves to the whole content, and a start at or past the size, a
/// zero suffix, or any range over empty content is unsatisfiable.
fn resolve_range(
    range: Option<AttachmentRange>,
    size: u64,
) -> Result<(u64, u64), AttachmentContentError> {
    match range {
        None => Ok((0, size)),
        Some(AttachmentRange::Bytes { from, to }) => {
            if size == 0 || from >= size {
                return Err(AttachmentContentError::RangeInvalid);
            }
            let end = match to {
                None => size,
                Some(to) if to < from => return Err(AttachmentContentError::RangeInvalid),
                Some(to) => to.min(size - 1) + 1,
            };
            Ok((from, end))
        }
        Some(AttachmentRange::Suffix { bytes }) => {
            if size == 0 || bytes == 0 {
                return Err(AttachmentContentError::RangeInvalid);
            }
            let take = bytes.min(size);
            Ok((size - take, size))
        }
    }
}

/// A bounded, read-only reader over one attachment's staged bytes.
///
/// The reader holds one open snapshot transaction and at most one decoded
/// segment at a time, so reading content of any accepted size never
/// materializes the whole content. It has no write path at all: V1 attachment
/// streams are not writable, and this typed surface keeps that property by
/// construction. Disposal ends the snapshot transaction; any use after
/// disposal reports [`AttachmentContentError::Disposed`], the typed form of
/// V1's disposed-stream failure.
///
/// ```compile_fail
/// // There is no independent reader constructor: readers open only from a
/// // FamilyStore bound to a durable Engine. This must not compile.
/// let reader = terrabase_v2::families::attachments::AttachmentContentReader::new();
/// ```
pub struct AttachmentContentReader<'engine> {
    transaction: Option<Transaction<'engine>>,
    digest: String,
    content_size: u64,
    /// The half-open absolute byte interval this reader serves.
    start: u64,
    end: u64,
    /// The reader's position relative to `start`.
    position: u64,
    /// The absolute offset of the first buffered byte.
    buffer_start: u64,
    /// The currently buffered segment; at most [`MAX_SEGMENT_BYTES`] bytes.
    buffer: Vec<u8>,
    /// Segment start offsets, one entry per segment plus a final `content_size`.
    offsets: Vec<u64>,
}

impl std::fmt::Debug for AttachmentContentReader<'_> {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        // Never formats the transaction, the buffered bytes, or any storage
        // detail: reader identity is its digest and its observable window.
        formatter
            .debug_struct("AttachmentContentReader")
            .field("digest", &self.digest)
            .field("content_size", &self.content_size)
            .field("start", &self.start)
            .field("end", &self.end)
            .field("position", &self.position)
            .field("disposed", &self.is_disposed())
            .finish_non_exhaustive()
    }
}

impl AttachmentContentReader<'_> {
    /// Returns the digest of the content being read.
    #[must_use]
    pub fn digest(&self) -> &str {
        &self.digest
    }

    /// Returns the whole content's size in bytes.
    #[must_use]
    pub fn content_size(&self) -> u64 {
        self.content_size
    }

    /// Returns the served range's length in bytes — V1's stream length.
    #[must_use]
    pub fn length(&self) -> u64 {
        self.end - self.start
    }

    /// Returns the position within the served range — V1's stream position.
    #[must_use]
    pub fn position(&self) -> u64 {
        self.position
    }

    /// Reports whether the reader was disposed.
    #[must_use]
    pub fn is_disposed(&self) -> bool {
        self.transaction.is_none()
    }

    /// Reads bytes into `buffer`, returning how many were read.
    ///
    /// A return of `0` means end-of-stream; it is not an error, and reading
    /// again keeps returning `0`. The read never crosses the served range's
    /// end and never decodes more than one segment at a time.
    ///
    /// # Errors
    ///
    /// Returns [`AttachmentContentError::Disposed`] after disposal,
    /// [`AttachmentContentError::Truncated`] when the staged bytes end
    /// before the manifest's declared size — V1's end-of-stream-before-length
    /// failure — and [`AttachmentContentError::ReservedStateInvalid`] or
    /// [`AttachmentContentError::StorageFailure`] for a corrupt or
    /// unreadable segment record.
    pub fn read(&mut self, buffer: &mut [u8]) -> Result<usize, AttachmentContentError> {
        if self.is_disposed() {
            return Err(AttachmentContentError::Disposed);
        }
        if buffer.is_empty() || self.position >= self.length() {
            return Ok(0);
        }
        let absolute = self.start + self.position;
        self.ensure_buffered(absolute)?;
        let offset = usize::try_from(absolute - self.buffer_start).unwrap_or(usize::MAX);
        let range_remaining = usize::try_from(self.length() - self.position).unwrap_or(usize::MAX);
        let copy = buffer.len().min(range_remaining).min(self.buffer.len() - offset);
        buffer[..copy].copy_from_slice(&self.buffer[offset..offset + copy]);
        self.position += u64::try_from(copy).unwrap_or(u64::MAX);
        Ok(copy)
    }

    /// Moves the position within the served range.
    ///
    /// # Errors
    ///
    /// Returns [`AttachmentContentError::Disposed`] after disposal and
    /// [`AttachmentContentError::PositionInvalid`] for a position past the
    /// served range's length, V1's out-of-range seek failure.
    pub fn seek(&mut self, position: u64) -> Result<(), AttachmentContentError> {
        if self.is_disposed() {
            return Err(AttachmentContentError::Disposed);
        }
        if position > self.length() {
            return Err(AttachmentContentError::PositionInvalid);
        }
        self.position = position;
        Ok(())
    }

    /// Disposes the reader, ending its snapshot transaction.
    ///
    /// Disposal is idempotent; any later read or seek reports
    /// [`AttachmentContentError::Disposed`].
    pub fn dispose(&mut self) {
        self.transaction = None;
        self.buffer = Vec::new();
        self.buffer_start = 0;
    }

    /// Buffers the segment containing one absolute offset.
    fn ensure_buffered(&mut self, absolute: u64) -> Result<(), AttachmentContentError> {
        let buffered = absolute >= self.buffer_start
            && usize::try_from(absolute - self.buffer_start)
                .is_ok_and(|offset| offset < self.buffer.len());
        if buffered {
            return Ok(());
        }
        let Some(transaction) = self.transaction.as_mut() else {
            return Err(AttachmentContentError::Disposed);
        };
        let found = self
            .offsets
            .windows(2)
            .position(|bounds| absolute >= bounds[0] && absolute < bounds[1])
            .ok_or(AttachmentContentError::Truncated)?;
        // Segment counts stay far below either integer range; a failed u64
        // conversion of a valid index is unrepresentable reserved state.
        let index = found;
        let id = segment_id(
            &self.digest,
            u64::try_from(index).map_err(|_| AttachmentContentError::ReservedStateInvalid)?,
        )?;
        let mut records = Records::new(transaction);
        let encoded = records.read(&id)?;
        let value = encoded.ok_or(AttachmentContentError::Truncated)?;
        let mut bytes = codec::decode_segment(&value)
            .map_err(|()| AttachmentContentError::ReservedStateInvalid)?;
        let declared = self.offsets[index + 1] - self.offsets[index];
        if u64::try_from(bytes.len()).unwrap_or(u64::MAX) != declared {
            return Err(AttachmentContentError::Truncated);
        }
        bytes.shrink_to_fit();
        self.buffer = bytes;
        self.buffer_start = self.offsets[index];
        Ok(())
    }
}

impl<'engine> FamilyStore<'engine> {
    /// Stages attachment content as bounded segment records.
    ///
    /// The write joins the caller's transaction: nothing is durable until
    /// that transaction commits, and an abort hides every staged record. The
    /// plane computes the content digest itself and deduplicates by it, so
    /// staging bytes that already exist keeps the existing segment records
    /// and restages only the manifest. Empty segments carry no bytes and are
    /// dropped, so a write of only empty segments stages as the canonical
    /// empty content.
    ///
    /// # Errors
    ///
    /// Returns [`AttachmentContentError::TransactionIdentityMismatch`] when
    /// the write names another operation identity,
    /// [`AttachmentContentError::TransactionEngineMismatch`] or
    /// [`AttachmentContentError::TenantMismatch`] when the transaction is
    /// not live under the write's scope, `DeadlineExceeded`/`Cancelled` for
    /// an elapsed deadline or cancelled signal,
    /// [`AttachmentContentError::SizeLimitExceeded`] when the total exceeds
    /// [`MAX_CONTENT_BYTES`],
    /// [`AttachmentContentError::DigestMismatch`] when the declared digest
    /// disagrees with the computed digest, and the storage or reserved-state
    /// cases for unreadable reserved records — including a segment record
    /// identifier already occupied by a value that is not a decodable
    /// segment, which is never blindly overwritten.
    #[allow(clippy::needless_pass_by_value)] // the seam contract passes payloads by value
    pub fn stage_attachment_content(
        &self,
        transaction: &mut Transaction<'engine>,
        write: AttachmentContentWrite,
    ) -> Result<AttachmentContentRecorded, AttachmentContentError> {
        if transaction.operation_id() != write.operation_id() {
            return Err(AttachmentContentError::TransactionIdentityMismatch);
        }
        if write.deadline().is_expired() {
            return Err(AttachmentContentError::DeadlineExceeded);
        }
        if write.cancellation().is_cancelled() {
            return Err(AttachmentContentError::Cancelled);
        }
        self.bind_transaction(transaction, write.tenant_scope())?;
        let total = write.segments().iter().try_fold(0_u64, |sum, segment| {
            sum.checked_add(u64::try_from(segment.len()).unwrap_or(u64::MAX))
        });
        let Some(size) = total else {
            return Err(AttachmentContentError::SizeLimitExceeded);
        };
        if size > MAX_CONTENT_BYTES {
            return Err(AttachmentContentError::SizeLimitExceeded);
        }
        let mut hasher = Sha256::new();
        for segment in write.segments() {
            hasher.update(segment.bytes());
        }
        let digest = to_hex(&hasher.finish());
        if write.expected_digest().is_some_and(|declared| declared != digest) {
            return Err(AttachmentContentError::DigestMismatch);
        }
        // Empty segments carry no bytes, so they are dropped before the
        // manifest is built: a manifest never records a zero-length segment,
        // which keeps every manifest this plane writes inside the canonical
        // shape its own decoder enforces (empty content has no segments).
        let present: Vec<&AttachmentSegment> =
            write.segments().iter().filter(|segment| !segment.is_empty()).collect();
        let lengths: Vec<u64> =
            present.iter().map(|segment| u64::try_from(segment.len()).unwrap_or(0)).collect();
        let id = manifest_id(&digest)?;
        let mut records = Records::new(transaction);
        if let Some(existing) = records.read(&id)? {
            let manifest = codec::decode_manifest(&existing)
                .map_err(|()| AttachmentContentError::ReservedStateInvalid)?;
            if manifest.digest != digest {
                return Err(AttachmentContentError::ReservedStateInvalid);
            }
            // Shared-hash dedup: identical bytes keep the existing segment
            // records. The manifest itself is restaged so the caller's
            // transaction still carries the content identity — V1 writes the
            // attachment row even when the byte stream is deduplicated.
            records.write(id, existing)?;
            return Ok(AttachmentContentRecorded::new(
                digest,
                manifest.size,
                u64::try_from(manifest.lengths.len()).unwrap_or(u64::MAX),
            ));
        }
        for (index, segment) in present.iter().enumerate() {
            let index =
                u64::try_from(index).map_err(|_| AttachmentContentError::SizeLimitExceeded)?;
            let id = segment_id(&digest, index)?;
            require_free_or_segment(&mut records, &id)?;
            records.write(id, codec::encode_segment(segment.bytes()))?;
        }
        records.write(
            id,
            codec::encode_manifest(&ContentManifest {
                digest: digest.clone(),
                size,
                lengths: lengths.clone(),
            }),
        )?;
        Ok(AttachmentContentRecorded::new(
            digest,
            size,
            u64::try_from(lengths.len()).unwrap_or(u64::MAX),
        ))
    }

    /// Removes one content's manifest and segment records.
    ///
    /// The deletion joins the caller's transaction like every family write.
    /// The caller owns reference tracking: the plane does not count metadata
    /// references to a digest, so deleting content another attachment still
    /// names removes its bytes (the recorded handoff in
    /// `docs/families/attachment-content-plane.md`).
    ///
    /// # Errors
    ///
    /// Returns [`AttachmentContentError::ContentMissing`] when no content is
    /// staged for the digest in this transaction's view, with the same
    /// identity, binding, and deadline cases as staging. A segment record
    /// identifier occupied by a value that is not a decodable segment is
    /// never blindly cleared: the deletion fails closed as
    /// [`AttachmentContentError::ReservedStateInvalid`] before touching it.
    #[allow(clippy::needless_pass_by_value)] // the seam contract passes payloads by value
    pub fn stage_attachment_content_delete(
        &self,
        transaction: &mut Transaction<'engine>,
        delete: AttachmentContentDelete,
    ) -> Result<(), AttachmentContentError> {
        if transaction.operation_id() != delete.operation_id() {
            return Err(AttachmentContentError::TransactionIdentityMismatch);
        }
        if delete.deadline().is_expired() {
            return Err(AttachmentContentError::DeadlineExceeded);
        }
        if delete.cancellation().is_cancelled() {
            return Err(AttachmentContentError::Cancelled);
        }
        self.bind_transaction(transaction, delete.tenant_scope())?;
        let mut records = Records::new(transaction);
        let manifest = load_manifest(&mut records, delete.digest())?;
        for index in 0..u64::try_from(manifest.lengths.len()).unwrap_or(u64::MAX) {
            let id = segment_id(delete.digest(), index)?;
            require_free_or_segment(&mut records, &id)?;
            records.clear(&id)?;
        }
        records.clear(&manifest_id(delete.digest())?)?;
        Ok(())
    }

    /// Opens one bounded reader over staged attachment content.
    ///
    /// The context's tenant, snapshot, and Engine binding are validated
    /// before any content access, the reader observes the context's
    /// immutable snapshot for its whole life, and the reader's transaction is
    /// never committed.
    ///
    /// # Errors
    ///
    /// Returns [`AttachmentContentError::TenantMismatch`] or
    /// [`AttachmentContentError::SnapshotEngineMismatch`] for an invalid
    /// context, `DeadlineExceeded`/`Cancelled` before the open runs,
    /// [`AttachmentContentError::ContentMissing`] when no content is staged
    /// for the digest,
    /// [`AttachmentContentError::RangeInvalid`] for an unsatisfiable range,
    /// and the reserved-state or storage cases for corrupt records.
    #[allow(clippy::needless_pass_by_value)] // the seam contract passes the context by value
    pub fn open_attachment_content(
        &self,
        context: FamilyReadContext,
        request: AttachmentContentRead,
    ) -> Result<AttachmentContentReader<'engine>, AttachmentContentError> {
        FamilyReadContext::validate(context.tenant(), context.snapshot())?;
        if request.deadline().is_expired() {
            return Err(AttachmentContentError::DeadlineExceeded);
        }
        if request.cancellation().is_cancelled() {
            return Err(AttachmentContentError::Cancelled);
        }
        let operation = store::internal_read_operation()?;
        let mut transaction = self
            .engine
            .begin(context.tenant(), context.snapshot().clone(), operation)
            .map_err(|error| map_begin_error(&error))?;
        let manifest = load_manifest(&mut Records::new(&mut transaction), request.digest())?;
        let (start, end) = resolve_range(request.range(), manifest.size)?;
        let mut offsets = Vec::with_capacity(manifest.lengths.len() + 1);
        let mut cursor = 0_u64;
        offsets.push(cursor);
        for length in &manifest.lengths {
            cursor =
                cursor.checked_add(*length).ok_or(AttachmentContentError::ReservedStateInvalid)?;
            offsets.push(cursor);
        }
        if cursor != manifest.size {
            return Err(AttachmentContentError::ReservedStateInvalid);
        }
        Ok(AttachmentContentReader {
            transaction: Some(transaction),
            digest: request.digest().to_owned(),
            content_size: manifest.size,
            start,
            end,
            position: 0,
            buffer: Vec::new(),
            buffer_start: 0,
            offsets,
        })
    }
}
