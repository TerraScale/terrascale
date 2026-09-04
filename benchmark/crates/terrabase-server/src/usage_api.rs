//! Database/admin usage service used by the public usage route.
//!
//! This module is deliberately an adapter boundary.  It does not derive
//! storage from document counts, maintain a second ledger, or invent a value
//! when the database source is unavailable.  A [`UsageSource`] supplies an
//! authoritative snapshot and page, and is also responsible for applying the
//! engine's TTL, history, and selected-Region rules.

use std::collections::BTreeSet;
use std::fmt;
use std::sync::Arc;

use serde::Serialize;

/// The only billable meters in the public pricing contract.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum BillableMeter {
    /// Canonical document mutation bytes rounded to 4 KiB units.
    DocumentWriteUnits,
    /// Logical retained bytes multiplied by elapsed seconds and selected
    /// Region copies.
    StorageByteSeconds,
}

impl BillableMeter {
    /// Returns the stable wire key for this meter.
    #[must_use]
    pub const fn key(self) -> &'static str {
        match self {
            Self::DocumentWriteUnits => "document_write_units",
            Self::StorageByteSeconds => "storage_byte_seconds",
        }
    }

    /// Resolves a wire key from the closed vocabulary.
    pub fn from_key(key: &str) -> Result<Self, UsageApiError> {
        match key {
            "document_write_units" => Ok(Self::DocumentWriteUnits),
            "storage_byte_seconds" => Ok(Self::StorageByteSeconds),
            _ => Err(UsageApiError::UnknownMeter { key: key.to_owned() }),
        }
    }
}

/// A tenant/database identity supplied by the authenticated API layer.
///
/// The service never takes tenant or database identity from a usage record.
/// It binds every source call to this value, so a route parameter cannot
/// accidentally become a cross-tenant accounting query.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct UsageAccess {
    tenant_id: String,
    database_id: String,
}

impl UsageAccess {
    /// Creates an authenticated tenant/database authorization input.
    pub fn try_new(tenant_id: &str, database_id: &str) -> Result<Self, UsageApiError> {
        validate_identifier(tenant_id, "tenant_id")?;
        validate_identifier(database_id, "database_id")?;
        Ok(Self { tenant_id: tenant_id.to_owned(), database_id: database_id.to_owned() })
    }

    /// Returns the authenticated tenant identity.
    #[must_use]
    pub fn tenant_id(&self) -> &str {
        &self.tenant_id
    }

    /// Returns the authorized database identity.
    #[must_use]
    pub fn database_id(&self) -> &str {
        &self.database_id
    }
}

/// A half-open millisecond filter for usage measurements.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct UsageWindow {
    /// Inclusive start in Unix milliseconds.
    pub start_millis: u64,
    /// Exclusive end in Unix milliseconds.
    pub end_millis: u64,
}

impl UsageWindow {
    /// Creates a non-empty half-open window.
    pub const fn try_new(start_millis: u64, end_millis: u64) -> Result<Self, UsageApiError> {
        if end_millis <= start_millis {
            return Err(UsageApiError::InvalidWindow);
        }
        Ok(Self { start_millis, end_millis })
    }
}

/// A bounded usage page request.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct UsagePageRequest {
    /// Optional measurement window.
    pub window: Option<UsageWindow>,
    /// Requested page size, bounded to [`MAX_PAGE_SIZE`].
    pub page_size: u16,
    /// Opaque continuation returned by an earlier page.
    pub cursor: Option<String>,
}

impl UsagePageRequest {
    /// Creates a validated page request.
    pub fn try_new(
        window: Option<UsageWindow>,
        page_size: u16,
        cursor: Option<String>,
    ) -> Result<Self, UsageApiError> {
        if page_size == 0 || page_size > MAX_PAGE_SIZE {
            return Err(UsageApiError::InvalidPageSize);
        }
        if cursor.as_ref().is_some_and(|value| value.len() > MAX_CURSOR_TEXT_BYTES) {
            return Err(UsageApiError::InvalidCursor);
        }
        Ok(Self { window, page_size, cursor })
    }
}

/// A typed usage observation submitted by an engine/admin adapter.
///
/// The source must atomically deduplicate `event_id`.  A reconciliation uses
/// `reconciliation_id` to identify the durable correction lineage; it cannot
/// silently overwrite an earlier event.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct UsageObservation {
    /// One of the two closed billable meters.
    pub meter: BillableMeter,
    /// Stable idempotency identity, bounded by [`MAX_EVENT_ID_BYTES`].
    pub event_id: String,
    /// Optional stable identity for an append-only reconciliation.
    pub reconciliation_id: Option<String>,
    /// Signed integer delta.  A correction may be negative.
    pub integer_delta: i128,
    /// Event/interval start in Unix milliseconds.
    pub measurement_start_millis: u64,
    /// Event/interval end in Unix milliseconds.  Events use equal bounds.
    pub measurement_end_millis: u64,
}

/// One source page row.  Its sequence is the source's durable ordering, not
/// a client-provided offset.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct UsageEntry {
    /// The billable meter.
    pub meter: BillableMeter,
    /// Stable source identity.
    pub event_id: String,
    /// Source commit sequence.
    pub sequence: u64,
    /// Integer amount represented by this event.
    pub integer_delta: i128,
    /// Measurement start in Unix milliseconds.
    pub measurement_start_millis: u64,
    /// Measurement end in Unix milliseconds.
    pub measurement_end_millis: u64,
}

/// Read counters are attribution-only and never appear as billable meters.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize)]
pub struct ReadAttribution {
    /// Point/document reads served.
    pub point_reads: u64,
    /// SQL/query reads served.
    pub query_reads: u64,
    /// Reactive read deliveries served.
    pub reactive_reads: u64,
}

/// Authoritative totals returned by a source snapshot.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct UsageTotals {
    /// Total document write units.
    pub document_write_units: i128,
    /// Total logical storage byte-seconds.
    pub storage_byte_seconds: i128,
}

impl UsageTotals {
    /// Returns zero totals for a source that has no billable observations.
    #[must_use]
    pub const fn zero() -> Self {
        Self { document_write_units: 0, storage_byte_seconds: 0 }
    }

    fn validate(self) -> Result<Self, UsageApiError> {
        if self.document_write_units < 0 || self.storage_byte_seconds < 0 {
            return Err(UsageApiError::CorruptSource {
                detail: "authoritative billable totals cannot be negative".into(),
            });
        }
        Ok(self)
    }
}

/// The source's stable high-water and accounting totals.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct UsageSnapshot {
    /// Durable sequence shared by every page in this export chain.
    pub snapshot_sequence: u64,
    /// Billable totals computed by the source.
    pub totals: UsageTotals,
    /// Attribution-only read counters computed by the source.
    pub reads: ReadAttribution,
}

/// Position used by a source page.  It is intentionally typed and never
/// exposed as a user-facing offset or database key.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct UsagePosition {
    /// Durable sequence.
    pub sequence: u64,
    /// Event identity tie-breaker.
    pub event_id: String,
}

/// Input to the authoritative source page port.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct UsageSourcePage {
    /// Authorized scope.
    pub access: UsageAccess,
    /// Stable snapshot high-water.
    pub snapshot_sequence: u64,
    /// Position strictly before the requested page, if any.
    pub after: Option<UsagePosition>,
    /// Optional measurement filter.
    pub window: Option<UsageWindow>,
    /// Number of rows requested, including one look-ahead row.
    pub limit: u16,
}

/// The append result from the durable source.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AppendOutcome {
    /// A new fact was durably appended.
    Inserted { sequence: u64 },
    /// The exact same identity/content was already present.
    AlreadyPresent { sequence: u64 },
    /// An append-only correction was durably accepted.
    Reconciled { sequence: u64 },
}

/// Typed failures a real database source may return.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum UsageSourceError {
    /// The durable usage source is unavailable; no fallback is valid.
    Unavailable,
    /// The source has not got an authoritative usage view for this database.
    NotReady,
    /// Persisted usage data failed integrity validation.
    Corrupt { detail: String },
    /// The event identity was reused with different content.
    IdentityConflict,
    /// A correction failed source reconciliation policy.
    ReconciliationRejected { detail: String },
    /// The source rejected the authorized scope.
    ScopeMismatch,
}

impl fmt::Display for UsageSourceError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Unavailable => formatter.write_str("usage source unavailable"),
            Self::NotReady => formatter.write_str("authoritative usage view is not ready"),
            Self::Corrupt { detail } => write!(formatter, "corrupt usage source: {detail}"),
            Self::IdentityConflict => {
                formatter.write_str("usage event identity conflicts with durable content")
            }
            Self::ReconciliationRejected { detail } => {
                write!(formatter, "usage reconciliation rejected: {detail}")
            }
            Self::ScopeMismatch => {
                formatter.write_str("usage source rejected the authorized scope")
            }
        }
    }
}

impl std::error::Error for UsageSourceError {}

/// Typed source port implemented by the real database/storage adapter.
///
/// `snapshot` and `page` must read the same durable view.  The source owns
/// TTL expiry, document/history retention, and multiplication by selected
/// Region copies; this service never approximates those policies.  `append`
/// must provide an atomic durable identity check, including after restart.
pub trait UsageSource: Send + Sync + 'static {
    /// Takes an immutable high-water snapshot with authoritative totals.
    fn snapshot(
        &self,
        access: &UsageAccess,
        window: Option<UsageWindow>,
        snapshot_sequence: Option<u64>,
    ) -> Result<UsageSnapshot, UsageSourceError>;

    /// Reads rows strictly after `after` and no later than the snapshot.
    fn page(&self, request: UsageSourcePage) -> Result<Vec<UsageEntry>, UsageSourceError>;

    /// Appends one billable observation with durable duplicate protection.
    fn append(
        &self,
        access: &UsageAccess,
        observation: UsageObservation,
    ) -> Result<AppendOutcome, UsageSourceError>;
}

/// A bounded response DTO suitable for the pending HTTP route.
#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct UsageResponse {
    /// Tenant identity bound by authorization.
    pub tenant_id: String,
    /// Database identity bound by authorization.
    pub database_id: String,
    /// Snapshot high-water shared by all pages in this chain.
    pub snapshot_sequence: u64,
    /// Exactly the two billable meters, in stable order.
    pub meters: [MeterTotal; 2],
    /// Attribution-only read counters.
    pub reads: ReadAttribution,
    /// At most the requested page size of entries.
    pub entries: Vec<UsageEntryDto>,
    /// Opaque continuation, absent at end of snapshot.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub next_cursor: Option<String>,
}

/// One bounded billable total DTO. Integer values stay strings to avoid JSON
/// number precision loss in JavaScript clients.
#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct MeterTotal {
    /// Closed billable meter.
    pub meter: BillableMeter,
    /// Canonical base-10 integer total.
    pub units: String,
}

/// One bounded usage entry DTO.
#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct UsageEntryDto {
    /// Billable meter.
    pub meter: BillableMeter,
    /// Stable event identity.
    pub event_id: String,
    /// Durable source sequence.
    pub sequence: u64,
    /// Canonical base-10 integer delta.
    pub integer_delta: String,
    /// Measurement start in Unix milliseconds.
    pub measurement_start_millis: u64,
    /// Measurement end in Unix milliseconds.
    pub measurement_end_millis: u64,
}

/// Result of an idempotent usage append.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
pub struct UsageReceipt {
    /// Durable source sequence.
    pub sequence: u64,
    /// Whether this request replayed an existing identity.
    pub already_recorded: bool,
    /// Whether this request appended an explicit reconciliation.
    pub reconciled: bool,
}

/// Actionable failures returned by the service boundary.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum UsageApiError {
    /// Tenant/database input is missing or malformed.
    InvalidScope { field: &'static str },
    /// The meter key is outside the closed billable vocabulary.
    UnknownMeter { key: String },
    /// The request's source identity is too large or empty.
    InvalidEventId,
    /// An event amount is zero or outside the signed integer contract.
    InvalidDelta,
    /// Measurement bounds are reversed.
    InvalidWindow,
    /// Page size is outside the fixed bound.
    InvalidPageSize,
    /// Cursor is malformed, forged, expired by snapshot mismatch, or bound
    /// to another scope/filter.
    InvalidCursor,
    /// Source data violates ordering or identity invariants.
    CorruptSource { detail: String },
    /// The source is unavailable and no provisional answer exists.
    SourceUnavailable { retryable: bool, detail: String },
    /// Durable identity reuse with different content.
    IdentityConflict,
    /// Source reconciliation policy rejected a correction.
    ReconciliationRejected { detail: String },
}

impl UsageApiError {
    /// Returns the stable machine-readable error code for HTTP mapping.
    #[must_use]
    pub const fn code(&self) -> &'static str {
        match self {
            Self::InvalidScope { .. } => "invalid_scope",
            Self::UnknownMeter { .. } => "unknown_meter",
            Self::InvalidEventId => "invalid_event_id",
            Self::InvalidDelta => "invalid_delta",
            Self::InvalidWindow => "invalid_window",
            Self::InvalidPageSize => "invalid_page_size",
            Self::InvalidCursor => "invalid_cursor",
            Self::CorruptSource { .. } => "usage_corrupt",
            Self::SourceUnavailable { .. } => "usage_unavailable",
            Self::IdentityConflict => "usage_identity_conflict",
            Self::ReconciliationRejected { .. } => "usage_reconciliation_rejected",
        }
    }

    /// Returns a one-line remedy suitable for an API error envelope.
    #[must_use]
    pub fn fix(&self) -> String {
        match self {
            Self::InvalidScope { .. } => "use the authenticated tenant and database scope".into(),
            Self::UnknownMeter { .. } => "use document_write_units or storage_byte_seconds".into(),
            Self::InvalidEventId => "send a non-empty event_id of at most 128 bytes".into(),
            Self::InvalidDelta => {
                "send a nonzero integer delta and valid measurement bounds".into()
            }
            Self::InvalidWindow => "send end_millis greater than start_millis".into(),
            Self::InvalidPageSize => "send page_size between 1 and 256".into(),
            Self::InvalidCursor => {
                "restart the export without the cursor, or reuse it unchanged with the same filters"
                    .into()
            }
            Self::CorruptSource { .. } => {
                "stop billing export and repair the authoritative database usage ledger".into()
            }
            Self::SourceUnavailable { .. } => {
                "retry after the authoritative database usage source is healthy".into()
            }
            Self::IdentityConflict => {
                "use a new event_id; never reuse an identity for different content".into()
            }
            Self::ReconciliationRejected { .. } => {
                "submit a correction with its durable reconciliation identity and target".into()
            }
        }
    }

    /// Returns whether retrying can produce a different result without
    /// changing the request.
    #[must_use]
    pub const fn retryable(&self) -> bool {
        matches!(self, Self::SourceUnavailable { retryable: true, .. })
    }
}

impl fmt::Display for UsageApiError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidScope { field } => write!(formatter, "invalid usage {field}"),
            Self::UnknownMeter { key } => write!(formatter, "meter '{key}' is not billable"),
            Self::InvalidEventId => formatter.write_str("usage event_id is empty or too large"),
            Self::InvalidDelta => formatter.write_str("usage integer_delta must be nonzero"),
            Self::InvalidWindow => {
                formatter.write_str("usage window must be a non-empty half-open interval")
            }
            Self::InvalidPageSize => {
                formatter.write_str("usage page_size must be between 1 and 256")
            }
            Self::InvalidCursor => {
                formatter.write_str("usage cursor is invalid or not bound to this request")
            }
            Self::CorruptSource { detail } => {
                write!(formatter, "authoritative usage is corrupt: {detail}")
            }
            Self::SourceUnavailable { detail, .. } => {
                write!(formatter, "authoritative usage is unavailable: {detail}")
            }
            Self::IdentityConflict => {
                formatter.write_str("usage event identity conflicts with an existing fact")
            }
            Self::ReconciliationRejected { detail } => {
                write!(formatter, "usage reconciliation rejected: {detail}")
            }
        }
    }
}

impl std::error::Error for UsageApiError {}

/// Maximum number of rows returned by one page.
pub const MAX_PAGE_SIZE: u16 = 256;
/// Maximum opaque cursor text accepted by this service.
pub const MAX_CURSOR_TEXT_BYTES: usize = 2_048;
/// Maximum event identity length.
pub const MAX_EVENT_ID_BYTES: usize = 128;
const CURSOR_VERSION: u8 = 1;
const CURSOR_MAC_BYTES: usize = 32;

/// The usage service over one authoritative source.
pub struct UsageService<S> {
    source: Arc<S>,
    cursor_key: [u8; 32],
}

impl<S: UsageSource> UsageService<S> {
    /// Creates a service.  `cursor_key` must be stable across restarts and
    /// members that are allowed to resume the same administrative export.
    #[must_use]
    pub fn new(source: Arc<S>, cursor_key: [u8; 32]) -> Self {
        Self { source, cursor_key }
    }

    /// Returns the source handle for composition or health instrumentation.
    #[must_use]
    pub fn source(&self) -> &Arc<S> {
        &self.source
    }

    /// Records one bounded, billable observation for the authorized database.
    pub fn record(
        &self,
        access: &UsageAccess,
        observation: UsageObservation,
    ) -> Result<UsageReceipt, UsageApiError> {
        validate_observation(&observation)?;
        match self.source.append(access, observation) {
            Ok(AppendOutcome::Inserted { sequence }) => {
                ensure_sequence(sequence)?;
                Ok(UsageReceipt { sequence, already_recorded: false, reconciled: false })
            }
            Ok(AppendOutcome::AlreadyPresent { sequence }) => {
                ensure_sequence(sequence)?;
                Ok(UsageReceipt { sequence, already_recorded: true, reconciled: false })
            }
            Ok(AppendOutcome::Reconciled { sequence }) => {
                ensure_sequence(sequence)?;
                Ok(UsageReceipt { sequence, already_recorded: false, reconciled: true })
            }
            Err(error) => Err(map_source_error(error)),
        }
    }

    /// Exports one immutable, tenant/database-authorized page.
    pub fn page(
        &self,
        access: &UsageAccess,
        request: UsagePageRequest,
    ) -> Result<UsageResponse, UsageApiError> {
        let cursor_state = request
            .cursor
            .as_deref()
            .map(|cursor| CursorState::decode(cursor, &self.cursor_key))
            .transpose()?;
        let expected_snapshot = cursor_state.as_ref().map(|state| state.snapshot_sequence);
        let (snapshot, after) = if let Some(state) = cursor_state.as_ref() {
            if state.tenant_id != access.tenant_id
                || state.database_id != access.database_id
                || state.window != request.window
                || state.page_size != request.page_size
            {
                return Err(UsageApiError::InvalidCursor);
            }
            (
                self.source
                    .snapshot(access, request.window, Some(state.snapshot_sequence))
                    .map_err(map_source_error)?,
                Some(state.after.clone()),
            )
        } else {
            (self.source.snapshot(access, request.window, None).map_err(map_source_error)?, None)
        };
        if expected_snapshot.is_some_and(|expected| snapshot.snapshot_sequence != expected) {
            return Err(UsageApiError::InvalidCursor);
        }
        let snapshot = UsageSnapshot { totals: snapshot.totals.validate()?, ..snapshot };
        let source_request = UsageSourcePage {
            access: access.clone(),
            snapshot_sequence: snapshot.snapshot_sequence,
            after,
            window: request.window,
            limit: request.page_size.saturating_add(1),
        };
        let mut entries = self.source.page(source_request).map_err(map_source_error)?;
        validate_page(
            &entries,
            snapshot.snapshot_sequence,
            request.cursor.as_deref(),
            &self.cursor_key,
            access,
            &request,
        )?;
        let has_more = entries.len() > usize::from(request.page_size);
        if has_more {
            entries.truncate(usize::from(request.page_size));
        }
        let next_cursor = if has_more {
            let last = entries.last().ok_or_else(|| UsageApiError::CorruptSource {
                detail: "source returned an empty look-ahead page".into(),
            })?;
            Some(
                CursorState::new(
                    access,
                    request.window,
                    request.page_size,
                    snapshot.snapshot_sequence,
                    last,
                )
                .encode(&self.cursor_key)?,
            )
        } else {
            None
        };
        Ok(UsageResponse {
            tenant_id: access.tenant_id.clone(),
            database_id: access.database_id.clone(),
            snapshot_sequence: snapshot.snapshot_sequence,
            meters: [
                MeterTotal {
                    meter: BillableMeter::DocumentWriteUnits,
                    units: snapshot.totals.document_write_units.to_string(),
                },
                MeterTotal {
                    meter: BillableMeter::StorageByteSeconds,
                    units: snapshot.totals.storage_byte_seconds.to_string(),
                },
            ],
            reads: snapshot.reads,
            entries: entries.into_iter().map(UsageEntryDto::from).collect(),
            next_cursor,
        })
    }
}

impl From<UsageEntry> for UsageEntryDto {
    fn from(entry: UsageEntry) -> Self {
        Self {
            meter: entry.meter,
            event_id: entry.event_id,
            sequence: entry.sequence,
            integer_delta: entry.integer_delta.to_string(),
            measurement_start_millis: entry.measurement_start_millis,
            measurement_end_millis: entry.measurement_end_millis,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct CursorState {
    tenant_id: String,
    database_id: String,
    window: Option<UsageWindow>,
    page_size: u16,
    snapshot_sequence: u64,
    after: UsagePosition,
}

impl CursorState {
    fn new(
        access: &UsageAccess,
        window: Option<UsageWindow>,
        page_size: u16,
        snapshot_sequence: u64,
        entry: &UsageEntry,
    ) -> Self {
        Self {
            tenant_id: access.tenant_id.clone(),
            database_id: access.database_id.clone(),
            window,
            page_size,
            snapshot_sequence,
            after: UsagePosition { sequence: entry.sequence, event_id: entry.event_id.clone() },
        }
    }

    fn encode(&self, key: &[u8; 32]) -> Result<String, UsageApiError> {
        let body = self.body()?;
        let mac = blake3::keyed_hash(key, &body);
        let mut bytes = body;
        bytes.extend_from_slice(mac.as_bytes());
        let text = hex_encode(&bytes);
        if text.len() > MAX_CURSOR_TEXT_BYTES {
            return Err(UsageApiError::InvalidCursor);
        }
        Ok(text)
    }

    fn decode(text: &str, key: &[u8; 32]) -> Result<Self, UsageApiError> {
        if text.is_empty() || text.len() > MAX_CURSOR_TEXT_BYTES || text.len() % 2 != 0 {
            return Err(UsageApiError::InvalidCursor);
        }
        let bytes = hex_decode(text).ok_or(UsageApiError::InvalidCursor)?;
        if bytes.len() <= CURSOR_MAC_BYTES {
            return Err(UsageApiError::InvalidCursor);
        }
        let (body, mac) = bytes.split_at(bytes.len() - CURSOR_MAC_BYTES);
        if blake3::keyed_hash(key, body).as_bytes() != mac {
            return Err(UsageApiError::InvalidCursor);
        }
        Self::from_body(body)
    }

    fn body(&self) -> Result<Vec<u8>, UsageApiError> {
        let mut body = vec![b'T', b'S', b'U', b'C', CURSOR_VERSION];
        put_text(&mut body, &self.tenant_id)?;
        put_text(&mut body, &self.database_id)?;
        match self.window {
            Some(window) => {
                body.push(1);
                body.extend_from_slice(&window.start_millis.to_le_bytes());
                body.extend_from_slice(&window.end_millis.to_le_bytes());
            }
            None => body.push(0),
        }
        body.extend_from_slice(&self.page_size.to_le_bytes());
        body.extend_from_slice(&self.snapshot_sequence.to_le_bytes());
        body.extend_from_slice(&self.after.sequence.to_le_bytes());
        put_text(&mut body, &self.after.event_id)?;
        Ok(body)
    }

    fn from_body(body: &[u8]) -> Result<Self, UsageApiError> {
        let mut reader = CursorReader { bytes: body, offset: 0 };
        if reader.take(5)? != b"TSUC\x01" {
            return Err(UsageApiError::InvalidCursor);
        }
        let tenant_id = reader.text()?;
        let database_id = reader.text()?;
        let window = match reader.byte()? {
            0 => None,
            1 => Some(UsageWindow::try_new(reader.u64()?, reader.u64()?)?),
            _ => return Err(UsageApiError::InvalidCursor),
        };
        let page_size = reader.u16()?;
        if page_size == 0 || page_size > MAX_PAGE_SIZE {
            return Err(UsageApiError::InvalidCursor);
        }
        let snapshot_sequence = reader.u64()?;
        let sequence = reader.u64()?;
        ensure_sequence(sequence)?;
        let event_id = reader.text()?;
        if reader.remaining() != 0 {
            return Err(UsageApiError::InvalidCursor);
        }
        Ok(Self {
            tenant_id,
            database_id,
            window,
            page_size,
            snapshot_sequence,
            after: UsagePosition { sequence, event_id },
        })
    }
}

fn validate_page(
    entries: &[UsageEntry],
    snapshot_sequence: u64,
    cursor_text: Option<&str>,
    key: &[u8; 32],
    access: &UsageAccess,
    request: &UsagePageRequest,
) -> Result<(), UsageApiError> {
    let after = cursor_text
        .map(|text| CursorState::decode(text, key))
        .transpose()?
        .map(|state| state.after);
    let mut previous = after;
    let mut identities = BTreeSet::new();
    if entries.len() > usize::from(request.page_size) + 1 {
        return Err(UsageApiError::CorruptSource {
            detail: "source exceeded the page bound".into(),
        });
    }
    for entry in entries {
        validate_event_id(&entry.event_id).map_err(|_| UsageApiError::CorruptSource {
            detail: "source returned an invalid event identity".into(),
        })?;
        ensure_sequence(entry.sequence)?;
        if entry.sequence > snapshot_sequence
            || entry.integer_delta == 0
            || entry.measurement_end_millis < entry.measurement_start_millis
        {
            return Err(UsageApiError::CorruptSource {
                detail: "source returned an invalid usage entry".into(),
            });
        }
        let position = UsagePosition { sequence: entry.sequence, event_id: entry.event_id.clone() };
        if previous.as_ref().is_some_and(|prior| position <= *prior)
            || !identities.insert(entry.event_id.clone())
        {
            return Err(UsageApiError::CorruptSource {
                detail: "source page is not strictly ordered or duplicates an identity".into(),
            });
        }
        previous = Some(position);
    }
    let _ = access;
    Ok(())
}

fn validate_observation(observation: &UsageObservation) -> Result<(), UsageApiError> {
    validate_event_id(&observation.event_id).map_err(|_| UsageApiError::InvalidEventId)?;
    if observation
        .reconciliation_id
        .as_ref()
        .is_some_and(|id| validate_identifier(id, "reconciliation_id").is_err())
    {
        return Err(UsageApiError::InvalidEventId);
    }
    if observation.integer_delta == 0
        || observation.measurement_end_millis < observation.measurement_start_millis
    {
        return Err(UsageApiError::InvalidDelta);
    }
    Ok(())
}

fn map_source_error(error: UsageSourceError) -> UsageApiError {
    match error {
        UsageSourceError::Unavailable => UsageApiError::SourceUnavailable {
            retryable: true,
            detail: "retry after the authoritative database source is healthy".into(),
        },
        UsageSourceError::NotReady => UsageApiError::SourceUnavailable {
            retryable: true,
            detail: "wait for the database usage snapshot to become authoritative".into(),
        },
        UsageSourceError::Corrupt { detail } => UsageApiError::CorruptSource { detail },
        UsageSourceError::IdentityConflict => UsageApiError::IdentityConflict,
        UsageSourceError::ReconciliationRejected { detail } => {
            UsageApiError::ReconciliationRejected { detail }
        }
        UsageSourceError::ScopeMismatch => {
            UsageApiError::InvalidScope { field: "tenant/database authorization" }
        }
    }
}

fn validate_identifier(value: &str, field: &'static str) -> Result<(), UsageApiError> {
    if value.is_empty()
        || value.len() > 255
        || value.bytes().any(|byte| byte.is_ascii_control() || byte == b'/')
    {
        return Err(UsageApiError::InvalidScope { field });
    }
    Ok(())
}

fn validate_event_id(value: &str) -> Result<(), UsageApiError> {
    validate_identifier(value, "event_id")?;
    if value.len() > MAX_EVENT_ID_BYTES {
        return Err(UsageApiError::InvalidEventId);
    }
    Ok(())
}

fn ensure_sequence(sequence: u64) -> Result<(), UsageApiError> {
    (sequence != 0)
        .then_some(())
        .ok_or(UsageApiError::CorruptSource { detail: "source returned zero sequence".into() })
}

fn put_text(output: &mut Vec<u8>, value: &str) -> Result<(), UsageApiError> {
    validate_identifier(value, "cursor identity")?;
    let length = u16::try_from(value.len()).map_err(|_| UsageApiError::InvalidCursor)?;
    output.extend_from_slice(&length.to_le_bytes());
    output.extend_from_slice(value.as_bytes());
    Ok(())
}

fn hex_encode(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut text = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        text.push(char::from(HEX[usize::from(byte >> 4)]));
        text.push(char::from(HEX[usize::from(byte & 0x0f)]));
    }
    text
}

fn hex_decode(text: &str) -> Option<Vec<u8>> {
    let bytes = text.as_bytes();
    let mut output = Vec::with_capacity(bytes.len() / 2);
    for pair in bytes.chunks_exact(2) {
        let high = char::from(pair[0]).to_digit(16)? as u8;
        let low = char::from(pair[1]).to_digit(16)? as u8;
        output.push((high << 4) | low);
    }
    Some(output)
}

struct CursorReader<'a> {
    bytes: &'a [u8],
    offset: usize,
}

impl<'a> CursorReader<'a> {
    fn take(&mut self, length: usize) -> Result<&'a [u8], UsageApiError> {
        let end = self.offset.checked_add(length).ok_or(UsageApiError::InvalidCursor)?;
        let value = self.bytes.get(self.offset..end).ok_or(UsageApiError::InvalidCursor)?;
        self.offset = end;
        Ok(value)
    }

    fn byte(&mut self) -> Result<u8, UsageApiError> {
        Ok(*self.take(1)?.first().ok_or(UsageApiError::InvalidCursor)?)
    }

    fn u16(&mut self) -> Result<u16, UsageApiError> {
        Ok(u16::from_le_bytes(self.take(2)?.try_into().map_err(|_| UsageApiError::InvalidCursor)?))
    }

    fn u64(&mut self) -> Result<u64, UsageApiError> {
        Ok(u64::from_le_bytes(self.take(8)?.try_into().map_err(|_| UsageApiError::InvalidCursor)?))
    }

    fn text(&mut self) -> Result<String, UsageApiError> {
        let length = usize::from(self.u16()?);
        let text =
            std::str::from_utf8(self.take(length)?).map_err(|_| UsageApiError::InvalidCursor)?;
        validate_identifier(text, "cursor identity")?;
        Ok(text.to_owned())
    }

    fn remaining(&self) -> usize {
        self.bytes.len().saturating_sub(self.offset)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    #[derive(Default)]
    struct MemorySource {
        entries: Mutex<Vec<UsageEntry>>,
    }

    impl UsageSource for MemorySource {
        fn snapshot(
            &self,
            _access: &UsageAccess,
            _window: Option<UsageWindow>,
            _snapshot_sequence: Option<u64>,
        ) -> Result<UsageSnapshot, UsageSourceError> {
            let entries = self.entries.lock().unwrap();
            Ok(UsageSnapshot {
                snapshot_sequence: entries.last().map_or(1, |entry| entry.sequence.max(1)),
                totals: UsageTotals {
                    document_write_units: entries
                        .iter()
                        .filter(|entry| entry.meter == BillableMeter::DocumentWriteUnits)
                        .map(|entry| entry.integer_delta)
                        .sum(),
                    storage_byte_seconds: entries
                        .iter()
                        .filter(|entry| entry.meter == BillableMeter::StorageByteSeconds)
                        .map(|entry| entry.integer_delta)
                        .sum(),
                },
                reads: ReadAttribution { point_reads: 7, query_reads: 3, reactive_reads: 1 },
            })
        }

        fn page(&self, request: UsageSourcePage) -> Result<Vec<UsageEntry>, UsageSourceError> {
            let entries = self.entries.lock().unwrap();
            Ok(entries
                .iter()
                .filter(|entry| {
                    request.after.as_ref().is_none_or(|after| {
                        UsagePosition { sequence: entry.sequence, event_id: entry.event_id.clone() }
                            > *after
                    })
                })
                .take(usize::from(request.limit))
                .cloned()
                .collect())
        }

        fn append(
            &self,
            _access: &UsageAccess,
            observation: UsageObservation,
        ) -> Result<AppendOutcome, UsageSourceError> {
            let mut entries = self.entries.lock().unwrap();
            if let Some(existing) =
                entries.iter().find(|entry| entry.event_id == observation.event_id)
            {
                return if existing.integer_delta == observation.integer_delta {
                    Ok(AppendOutcome::AlreadyPresent { sequence: existing.sequence })
                } else {
                    Err(UsageSourceError::IdentityConflict)
                };
            }
            let sequence = entries.last().map_or(1, |entry| entry.sequence + 1);
            entries.push(UsageEntry {
                meter: observation.meter,
                event_id: observation.event_id,
                sequence,
                integer_delta: observation.integer_delta,
                measurement_start_millis: observation.measurement_start_millis,
                measurement_end_millis: observation.measurement_end_millis,
            });
            Ok(if observation.reconciliation_id.is_some() {
                AppendOutcome::Reconciled { sequence }
            } else {
                AppendOutcome::Inserted { sequence }
            })
        }
    }

    fn access() -> UsageAccess {
        UsageAccess::try_new("tenant-a", "database-a").unwrap()
    }

    #[test]
    fn only_closed_meter_keys_are_accepted() {
        assert_eq!(
            BillableMeter::from_key("document_write_units").unwrap().key(),
            "document_write_units"
        );
        assert!(matches!(
            BillableMeter::from_key("requests"),
            Err(UsageApiError::UnknownMeter { .. })
        ));
    }

    #[test]
    fn cursor_is_opaque_authenticated_and_restart_safe() {
        let source = Arc::new(MemorySource::default());
        let service = UsageService::new(Arc::clone(&source), [9; 32]);
        service
            .record(
                &access(),
                UsageObservation {
                    meter: BillableMeter::DocumentWriteUnits,
                    event_id: "fact-1".into(),
                    reconciliation_id: None,
                    integer_delta: 2,
                    measurement_start_millis: 10,
                    measurement_end_millis: 10,
                },
            )
            .unwrap();
        service
            .record(
                &access(),
                UsageObservation {
                    meter: BillableMeter::DocumentWriteUnits,
                    event_id: "fact-2".into(),
                    reconciliation_id: None,
                    integer_delta: 1,
                    measurement_start_millis: 11,
                    measurement_end_millis: 11,
                },
            )
            .unwrap();
        let first =
            service.page(&access(), UsagePageRequest::try_new(None, 1, None).unwrap()).unwrap();
        let cursor = first.next_cursor.unwrap();
        let restarted = UsageService::new(source, [9; 32]);
        let second = restarted
            .page(&access(), UsagePageRequest::try_new(None, 1, Some(cursor.clone())).unwrap())
            .unwrap();
        assert_eq!(second.entries.len(), 1);
        assert_eq!(second.entries[0].event_id, "fact-2");
        let mut forged = cursor.into_bytes();
        let last = forged.len() - 1;
        forged[last] = if forged[last] == b'0' { b'1' } else { b'0' };
        assert!(matches!(
            restarted.page(
                &access(),
                UsagePageRequest::try_new(None, 1, Some(String::from_utf8(forged).unwrap()))
                    .unwrap()
            ),
            Err(UsageApiError::InvalidCursor)
        ));
    }

    #[test]
    fn duplicate_and_conflicting_identities_are_not_minted_twice() {
        let source = Arc::new(MemorySource::default());
        let service = UsageService::new(source, [1; 32]);
        let observation = UsageObservation {
            meter: BillableMeter::DocumentWriteUnits,
            event_id: "same".into(),
            reconciliation_id: None,
            integer_delta: 1,
            measurement_start_millis: 1,
            measurement_end_millis: 1,
        };
        assert!(!service.record(&access(), observation.clone()).unwrap().already_recorded);
        assert!(service.record(&access(), observation).unwrap().already_recorded);
        assert!(matches!(
            service.record(
                &access(),
                UsageObservation {
                    meter: BillableMeter::DocumentWriteUnits,
                    event_id: "same".into(),
                    reconciliation_id: None,
                    integer_delta: 2,
                    measurement_start_millis: 1,
                    measurement_end_millis: 1
                }
            ),
            Err(UsageApiError::IdentityConflict)
        ));
    }

    #[test]
    fn response_contains_attribution_but_only_two_billable_meters() {
        let source = Arc::new(MemorySource::default());
        let service = UsageService::new(source, [2; 32]);
        let response =
            service.page(&access(), UsagePageRequest::try_new(None, 1, None).unwrap()).unwrap();
        assert_eq!(response.meters[0].meter, BillableMeter::DocumentWriteUnits);
        assert_eq!(response.meters[1].meter, BillableMeter::StorageByteSeconds);
        assert_eq!(response.reads.point_reads, 7);
    }
}
