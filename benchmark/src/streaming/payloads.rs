//! The family change-payload vocabulary (GAP-F02).
//!
//! A change event names *what* changed; a family change payload carries the
//! bounded typed body of *how* one family record changed, derived from the
//! published record value at event-expansion time. The vocabulary mirrors
//! V1's per-family change shapes — the counter change's name and value, the
//! time-series change's name and covered range, and the attachment
//! metadata's content-addressed entry — and nothing else: no document body,
//! no time-series values, and no attachment bytes ever ride an event.
//!
//! The payload plane is a notification plane, and its bounds are part of
//! the contract:
//!
//! - At most [`MAX_CHANGE_PAYLOAD_ENTRIES`] per-name entries ride one
//!   payload; a wider record delivers the alphabetically-first bound and
//!   reports the truncation through `complete`/`omitted_entries`, never
//!   silently and never by refusing the event.
//! - Names and content types are bounded by
//!   [`MAX_CHANGE_PAYLOAD_TEXT_BYTES`]; a content digest is exactly
//!   [`CHANGE_PAYLOAD_DIGEST_BYTES`] lowercase hex characters.
//! - Every payload carries the family record's published version, so a
//!   consumer applies payloads under the same optimistic discipline as the
//!   write seam's `ExpectedVersion`.
//!
//! Payloads are constructed only through validated constructors, and the
//! one crate-internal producer — [`derive`] — reads only the typed fields
//! of a published family record through the family state decoders. A
//! record that does not decode, or whose derived fields would violate the
//! bounds, mints no payload: the event still delivers, payload-less,
//! exactly as before this module existed.
//!
//! The decision record — vocabulary, bounds, the versioning story, the
//! includes interaction, and the recorded limits — is
//! `docs/families/gapf02-change-payloads.md`.

use crate::{
    families::{AttachmentRecord, CounterRecord, RESERVED_COLLECTION, TimeSeriesRecord},
    operations::RequestValidationError,
    storage::PublishedWrite,
    types::DocumentValue,
};

/// The largest number of per-name entries one change payload may carry.
///
/// A family record wider than this bound delivers its alphabetically-first
/// entries and reports the truncation; the bound keeps one event's payload
/// small and bounded whatever the record holds.
pub const MAX_CHANGE_PAYLOAD_ENTRIES: usize = 64;

/// The largest byte length of one payload name or content-type field,
/// matching the family seam's own name bound.
pub const MAX_CHANGE_PAYLOAD_TEXT_BYTES: usize = 255;

/// The exact byte length of one content digest: hex SHA-256, the content
/// address the GAP-F01 plane addresses bytes by.
pub const CHANGE_PAYLOAD_DIGEST_BYTES: usize = 64;

/// The closed write shape a payload reports: the journal write shape of
/// the family record it was derived from.
///
/// V1's finer-grained change types (`Increment`, `Mixed`) are write-time
/// facts computed against the pre-state; TerraBase derives payloads from the
/// published post-state alone, so the recoverable shapes are exactly these
/// two (decision record, recorded limit 1).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PayloadShape {
    /// The record was inserted or replaced.
    Put,
    /// The record was removed from the journal's key.
    Delete,
}

/// Validates one payload entry name under the family seam's name rules:
/// non-empty, bounded, free of control characters, untrimmed.
fn validate_entry_name(name: &str) -> Result<(), RequestValidationError> {
    if name.is_empty()
        || name.len() > MAX_CHANGE_PAYLOAD_TEXT_BYTES
        || name.chars().any(char::is_control)
        || name != name.trim()
    {
        return Err(RequestValidationError::InvalidFamilyPayload);
    }
    Ok(())
}

/// Validates one payload free-text field, for example a content type.
fn validate_entry_text(value: &str) -> Result<(), RequestValidationError> {
    if value.is_empty()
        || value.len() > MAX_CHANGE_PAYLOAD_TEXT_BYTES
        || value.chars().any(char::is_control)
    {
        return Err(RequestValidationError::InvalidFamilyPayload);
    }
    Ok(())
}

/// Reports whether one value is a well-formed content digest: exactly
/// [`CHANGE_PAYLOAD_DIGEST_BYTES`] lowercase hex characters.
fn is_digest(value: &str) -> bool {
    value.len() == CHANGE_PAYLOAD_DIGEST_BYTES
        && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

/// One live counter of a changed counter record: the name and its live
/// total, V1 `CounterChange.Name` and `CounterChange.Value` parity.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CounterChangeEntry {
    name: String,
    total: i64,
}

impl CounterChangeEntry {
    /// Creates one validated counter entry.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an
    /// invalid name.
    pub fn try_new(name: &str, total: i64) -> Result<Self, RequestValidationError> {
        validate_entry_name(name)?;
        Ok(Self { name: name.to_owned(), total })
    }

    /// Returns the counter name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the counter's live total as published.
    #[must_use]
    pub const fn total(&self) -> i64 {
        self.total
    }
}

/// The change payload of one counter record: every live counter's total,
/// ascending by name (V1 `CounterChange` parity).
///
/// Tombstoned names are not live and do not appear; a consumer tracking a
/// name's disappearance compares successive payloads (decision record,
/// recorded limit 4).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CounterChangePayload {
    shape: PayloadShape,
    record_version: u64,
    entries: Vec<CounterChangeEntry>,
    complete: bool,
    omitted_entries: u64,
}

impl CounterChangePayload {
    /// Creates one validated counter payload from ascending entries.
    ///
    /// The constructor is the payload's only public assembly point and
    /// refuses an over-bound or non-canonical entry list, so no caller can
    /// mint an oversize payload.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for more
    /// than [`MAX_CHANGE_PAYLOAD_ENTRIES`] entries or entries not in
    /// strictly ascending name order.
    pub fn try_new(
        shape: PayloadShape,
        record_version: u64,
        entries: Vec<CounterChangeEntry>,
    ) -> Result<Self, RequestValidationError> {
        if entries.len() > MAX_CHANGE_PAYLOAD_ENTRIES {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        if entries.windows(2).any(|pair| pair[0].name >= pair[1].name) {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        Ok(Self { shape, record_version, entries, complete: true, omitted_entries: 0 })
    }

    /// Assembles one payload from the mint's derived facts. Crate-only:
    /// payloads originate from [`derive`] alone, which guarantees canonical
    /// order and reports its own truncation.
    pub(crate) fn derived(
        shape: PayloadShape,
        record_version: u64,
        entries: Vec<CounterChangeEntry>,
        omitted_entries: u64,
    ) -> Self {
        Self { shape, record_version, entries, complete: omitted_entries == 0, omitted_entries }
    }

    /// Returns the journal write shape of the changed record.
    #[must_use]
    pub const fn shape(&self) -> PayloadShape {
        self.shape
    }

    /// Returns the published version of the changed record — for a delete,
    /// the version that existed at the removed key.
    #[must_use]
    pub const fn record_version(&self) -> u64 {
        self.record_version
    }

    /// Returns the live counter entries, ascending by name, at most
    /// [`MAX_CHANGE_PAYLOAD_ENTRIES`] of them.
    #[must_use]
    pub fn entries(&self) -> &[CounterChangeEntry] {
        &self.entries
    }

    /// Reports whether the payload carries every live name of the record.
    #[must_use]
    pub const fn complete(&self) -> bool {
        self.complete
    }

    /// Returns the number of live names omitted past the entry bound.
    #[must_use]
    pub const fn omitted_entries(&self) -> u64 {
        self.omitted_entries
    }
}

/// The change payload of one time-series record: a bounded segment
/// summary, never the values (V1 `TimeSeriesChange.Name` and `From`/`To`
/// parity).
///
/// `from`/`to` name the span the *published record covers* — first to
/// last entry timestamp — not the touched subrange, which is a write-time
/// fact the journal's post-state cannot recover (decision record,
/// recorded limit 2). An empty or deleted series carries no range.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TimeSeriesChangePayload {
    shape: PayloadShape,
    record_version: u64,
    name: String,
    arity: Option<u32>,
    from: Option<i64>,
    to: Option<i64>,
    entry_count: u64,
}

impl TimeSeriesChangePayload {
    /// Creates one validated series payload.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an
    /// invalid series name (empty, over
    /// [`MAX_CHANGE_PAYLOAD_TEXT_BYTES`] bytes, containing a control
    /// character, or containing `/`, which would be ambiguous against the
    /// record identifier's owner segment) or an inverted `from`/`to`
    /// range.
    pub fn try_new(
        shape: PayloadShape,
        record_version: u64,
        name: &str,
        arity: Option<u32>,
        from: Option<i64>,
        to: Option<i64>,
        entry_count: u64,
    ) -> Result<Self, RequestValidationError> {
        validate_entry_name(name)?;
        if name.contains('/') {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        if let (Some(from), Some(to)) = (from, to)
            && from > to
        {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        Ok(Self { shape, record_version, name: name.to_owned(), arity, from, to, entry_count })
    }

    /// Returns the journal write shape of the changed record.
    #[must_use]
    pub const fn shape(&self) -> PayloadShape {
        self.shape
    }

    /// Returns the published version of the changed record — for a delete,
    /// the version that existed at the removed key.
    #[must_use]
    pub const fn record_version(&self) -> u64 {
        self.record_version
    }

    /// Returns the series name parsed from the record identifier.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the series' value arity, or `None` for an unset arity.
    #[must_use]
    pub const fn arity(&self) -> Option<u32> {
        self.arity
    }

    /// Returns the covered span's first entry timestamp, or `None` for an
    /// empty or deleted series.
    #[must_use]
    pub const fn from(&self) -> Option<i64> {
        self.from
    }

    /// Returns the covered span's last entry timestamp, or `None` for an
    /// empty or deleted series.
    #[must_use]
    pub const fn to(&self) -> Option<i64> {
        self.to
    }

    /// Returns the number of entries the published record holds.
    #[must_use]
    pub const fn entry_count(&self) -> u64 {
        self.entry_count
    }
}

/// One stored attachment of a changed attachment-metadata record: the
/// name, content type, size, and content digest — V1 `AttachmentName`
/// parity, content-addressed.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AttachmentChangeEntry {
    name: String,
    content_type: String,
    size: u64,
    hash: String,
}

impl AttachmentChangeEntry {
    /// Creates one validated attachment entry.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an
    /// invalid name or content type, or a digest that is not exactly
    /// [`CHANGE_PAYLOAD_DIGEST_BYTES`] lowercase hex characters.
    pub fn try_new(
        name: &str,
        content_type: &str,
        size: u64,
        hash: &str,
    ) -> Result<Self, RequestValidationError> {
        validate_entry_name(name)?;
        validate_entry_text(content_type)?;
        if !is_digest(hash) {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        Ok(Self {
            name: name.to_owned(),
            content_type: content_type.to_owned(),
            size,
            hash: hash.to_owned(),
        })
    }

    /// Returns the attachment name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the logical content type.
    #[must_use]
    pub fn content_type(&self) -> &str {
        &self.content_type
    }

    /// Returns the logical content size in bytes.
    #[must_use]
    pub const fn size(&self) -> u64 {
        self.size
    }

    /// Returns the content digest — the address the content plane stores
    /// the bytes under.
    #[must_use]
    pub fn hash(&self) -> &str {
        &self.hash
    }
}

/// The change payload of one attachment-metadata record: every stored
/// attachment's content-addressed entry, ascending by name.
///
/// The digest names bytes in the GAP-F01 content plane; the payload never
/// carries the bytes themselves, and content-plane records mint no payload
/// at all.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AttachmentChangePayload {
    shape: PayloadShape,
    record_version: u64,
    entries: Vec<AttachmentChangeEntry>,
    complete: bool,
    omitted_entries: u64,
}

impl AttachmentChangePayload {
    /// Creates one validated attachment payload from ascending entries.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for more
    /// than [`MAX_CHANGE_PAYLOAD_ENTRIES`] entries or entries not in
    /// strictly ascending name order.
    pub fn try_new(
        shape: PayloadShape,
        record_version: u64,
        entries: Vec<AttachmentChangeEntry>,
    ) -> Result<Self, RequestValidationError> {
        if entries.len() > MAX_CHANGE_PAYLOAD_ENTRIES {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        if entries.windows(2).any(|pair| pair[0].name >= pair[1].name) {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        Ok(Self { shape, record_version, entries, complete: true, omitted_entries: 0 })
    }

    /// Assembles one payload from the mint's derived facts. Crate-only:
    /// payloads originate from [`derive`] alone.
    pub(crate) fn derived(
        shape: PayloadShape,
        record_version: u64,
        entries: Vec<AttachmentChangeEntry>,
        omitted_entries: u64,
    ) -> Self {
        Self { shape, record_version, entries, complete: omitted_entries == 0, omitted_entries }
    }

    /// Returns the journal write shape of the changed record.
    #[must_use]
    pub const fn shape(&self) -> PayloadShape {
        self.shape
    }

    /// Returns the published version of the changed record — for a delete,
    /// the version that existed at the removed key.
    #[must_use]
    pub const fn record_version(&self) -> u64 {
        self.record_version
    }

    /// Returns the attachment entries, ascending by name, at most
    /// [`MAX_CHANGE_PAYLOAD_ENTRIES`] of them.
    #[must_use]
    pub fn entries(&self) -> &[AttachmentChangeEntry] {
        &self.entries
    }

    /// Reports whether the payload carries every stored name of the
    /// record.
    #[must_use]
    pub const fn complete(&self) -> bool {
        self.complete
    }

    /// Returns the number of stored names omitted past the entry bound.
    #[must_use]
    pub const fn omitted_entries(&self) -> u64 {
        self.omitted_entries
    }
}

/// The typed family change payload one event may carry (GAP-F02).
///
/// The closed vocabulary is keyed by what the changed record is; the
/// per-kind disposition of all ten family kinds — including the seven
/// payload-less kinds and the attachment content plane — is the decision
/// record's table.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum FamilyChangePayload {
    /// The counter record's live totals (`family:counters/<document>`).
    Counters(CounterChangePayload),
    /// The time-series record's segment summary
    /// (`family:time-series/<document>/<name>`).
    TimeSeries(TimeSeriesChangePayload),
    /// The attachment-metadata record's content-addressed entries
    /// (`family:attachments/<document>`).
    Attachments(AttachmentChangePayload),
}

impl FamilyChangePayload {
    /// Returns the journal write shape of the changed record.
    #[must_use]
    pub const fn shape(&self) -> PayloadShape {
        match self {
            Self::Counters(value) => value.shape(),
            Self::TimeSeries(value) => value.shape(),
            Self::Attachments(value) => value.shape(),
        }
    }

    /// Returns the published version of the changed record — for a delete,
    /// the version that existed at the removed key.
    #[must_use]
    pub const fn record_version(&self) -> u64 {
        match self {
            Self::Counters(value) => value.record_version(),
            Self::TimeSeries(value) => value.record_version(),
            Self::Attachments(value) => value.record_version(),
        }
    }
}

/// Derives the family change payload of one published write (GAP-F02).
///
/// The mint runs at event-expansion time on the consumer's thread — never
/// on the committing thread — and is the only producer of payloads. A put
/// decodes the published record value through the family state decoders
/// and projects the typed fields; a delete reports the payload kind's
/// delete shape with the version that existed at the removed key. Records
/// outside the three payload-bearing keys — the seven payload-less family
/// kinds, the attachment content plane, and every user document — mint
/// `None`; so does any record that fails to decode or whose derived fields
/// would violate the bounds: the payload is derived, and a derived payload
/// is either valid or absent, never sanitized.
pub(crate) fn derive(write: &PublishedWrite) -> Option<FamilyChangePayload> {
    match write {
        PublishedWrite::Put { document } => {
            if document.collection().as_str() != RESERVED_COLLECTION {
                return None;
            }
            let id = document.id().as_str();
            let version = document.metadata().version();
            derive_put(id, version, document.value())
        }
        PublishedWrite::Delete { collection: Some(collection), id, version_at_delete } => {
            if collection.as_str() != RESERVED_COLLECTION {
                return None;
            }
            derive_delete(id.as_str(), *version_at_delete)
        }
        // A provable no-op delete names no key, so no record changed.
        PublishedWrite::Delete { collection: None, .. } => None,
    }
}

/// Derives the payload of one reserved-collection put.
fn derive_put(id: &str, version: u64, value: &DocumentValue) -> Option<FamilyChangePayload> {
    if let Some(document) = id.strip_prefix("family:counters/") {
        if document.is_empty() {
            return None;
        }
        let record = CounterRecord::decode(value).ok()?;
        let names = record.live_names();
        let mut entries = Vec::new();
        for name in &names {
            if entries.len() >= MAX_CHANGE_PAYLOAD_ENTRIES {
                break;
            }
            // A live name with no derivable total is an inconsistent
            // record: the payload is absent, not guessed.
            let Ok(Some(total)) = record.total(name) else {
                return None;
            };
            entries.push(CounterChangeEntry::try_new(name, total).ok()?);
        }
        let omitted = u64::try_from(names.len().saturating_sub(entries.len())).unwrap_or(u64::MAX);
        return Some(FamilyChangePayload::Counters(CounterChangePayload::derived(
            PayloadShape::Put,
            version,
            entries,
            omitted,
        )));
    }
    if let Some(owner) = id.strip_prefix("family:time-series/") {
        let name = series_name_of(owner)?;
        let record = TimeSeriesRecord::decode(value).ok()?;
        let arity = record.arity.map(|arity| u32::try_from(arity).unwrap_or(u32::MAX));
        let from = record.entries.first().map(crate::families::TimeSeriesAppend::timestamp);
        let to = record.entries.last().map(crate::families::TimeSeriesAppend::timestamp);
        let entry_count = u64::try_from(record.entries.len()).unwrap_or(u64::MAX);
        return Some(FamilyChangePayload::TimeSeries(
            TimeSeriesChangePayload::try_new(
                PayloadShape::Put,
                version,
                &name,
                arity,
                from,
                to,
                entry_count,
            )
            .ok()?,
        ));
    }
    if let Some(document) = id.strip_prefix("family:attachments/") {
        if document.is_empty() {
            return None;
        }
        let record = AttachmentRecord::decode(value).ok()?;
        let mut entries = Vec::new();
        for (name, entry) in &record.entries {
            if entries.len() >= MAX_CHANGE_PAYLOAD_ENTRIES {
                break;
            }
            entries.push(
                AttachmentChangeEntry::try_new(name, &entry.content_type, entry.size, &entry.hash)
                    .ok()?,
            );
        }
        let omitted =
            u64::try_from(record.entries.len().saturating_sub(entries.len())).unwrap_or(u64::MAX);
        return Some(FamilyChangePayload::Attachments(AttachmentChangePayload::derived(
            PayloadShape::Put,
            version,
            entries,
            omitted,
        )));
    }
    // Every other reserved key — the seven payload-less family kinds and
    // the attachment content plane — mints nothing.
    None
}

/// Derives the payload of one reserved-collection delete.
fn derive_delete(id: &str, version: u64) -> Option<FamilyChangePayload> {
    if id.strip_prefix("family:counters/").is_some_and(|document| !document.is_empty()) {
        return Some(FamilyChangePayload::Counters(CounterChangePayload::derived(
            PayloadShape::Delete,
            version,
            Vec::new(),
            0,
        )));
    }
    if let Some(owner) = id.strip_prefix("family:time-series/") {
        let name = series_name_of(owner)?;
        return Some(FamilyChangePayload::TimeSeries(
            TimeSeriesChangePayload::try_new(
                PayloadShape::Delete,
                version,
                &name,
                None,
                None,
                None,
                0,
            )
            .ok()?,
        ));
    }
    if id.strip_prefix("family:attachments/").is_some_and(|document| !document.is_empty()) {
        return Some(FamilyChangePayload::Attachments(AttachmentChangePayload::derived(
            PayloadShape::Delete,
            version,
            Vec::new(),
            0,
        )));
    }
    None
}

/// Parses the series name of one time-series record identifier's owner
/// segment: the segment after the last `/`, which the series-name rules
/// keep separator-free, so it is unambiguous however the document is named.
fn series_name_of(owner: &str) -> Option<String> {
    let name = owner.rsplit('/').next()?;
    if name.is_empty() || name.contains('/') {
        return None;
    }
    Some(name.to_owned())
}
