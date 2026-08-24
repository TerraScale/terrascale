//! Reserved family-record mapping over Engine documents.
//!
//! W3-F keeps every family value in documents of one reserved collection,
//! `terrabase.families`, addressed by reserved record identifiers of the form
//! `family:<kind>/<owner...>`. The Engine already keys documents by tenant and
//! applies one transaction marker per commit, so family state inherits tenant
//! isolation, atomicity, and durability from the Engine without families
//! touching storage bytes.
//!
//! This mapping is logical, not a storage-format fact: the record layout is
//! encoded as structured [`DocumentValue`]s and never exposes pages, frames,
//! or codec buffers. The Engine does not yet reserve the collection name, so a
//! caller writing the same collection and identifier directly through the
//! document seam collides with family state; that collision surfaces as the
//! typed [`FamilyError::ReservedStateInvalid`] and is recorded as a handoff in
//! the packet record.

use std::collections::BTreeMap;

use super::{
    FamilyError, RollupPolicy, SchemaDefinition, SchemaFieldType, SchemaRule, TimeSeriesAppend,
    TimeSeriesPolicy, payloads::Aggregation, payloads::RevisionPolicy,
};

use crate::{
    engine::Transaction,
    types::{CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion},
};

/// The reserved collection holding every family record.
pub(crate) const RESERVED_COLLECTION: &str = "terrabase.families";

/// The reserved record-identifier prefix.
const RECORD_PREFIX: &str = "family:";

/// Builds one reserved record identifier.
pub(crate) fn record_id(kind: &str, parts: &[&str]) -> Result<DocumentId, FamilyError> {
    let mut text = format!("{RECORD_PREFIX}{kind}");
    for part in parts {
        text.push('/');
        text.push_str(part);
    }
    DocumentId::try_new(&text).map_err(|_| FamilyError::InvalidRequest)
}

/// Returns the reserved tombstone map key of one family.
pub(crate) fn family_key(family: super::FamilyKind) -> &'static str {
    match family {
        super::FamilyKind::Attachments => "attachments",
        super::FamilyKind::Counters => "counters",
        super::FamilyKind::TimeSeries => "time-series",
        super::FamilyKind::Revisions => "revisions",
        super::FamilyKind::CompareExchange => "compare-exchange",
        super::FamilyKind::Identities => "identities",
        super::FamilyKind::ExpirationRefresh => "lifecycle",
        super::FamilyKind::Archival => "archival",
        super::FamilyKind::SchemaValidation => "validation",
        super::FamilyKind::ConflictTombstones => "tombstones",
    }
}

/// The current epoch time in milliseconds.
///
/// Family records carry logical times only; time qualification belongs to the
/// later streaming and replication packets.
pub(crate) fn now_millis() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |elapsed| i64::try_from(elapsed.as_millis()).unwrap_or(i64::MAX))
}

/// Read-and-write access to family records through one Engine transaction.
///
/// Every access rides the transaction: reads observe the transaction's
/// snapshot plus its staged writes, and writes join the transaction's single
/// commit marker. A family operation never commits independently.
pub(crate) struct Records<'a, 'engine> {
    transaction: &'a mut Transaction<'engine>,
}

impl<'a, 'engine> Records<'a, 'engine> {
    /// Wraps one transaction.
    pub(crate) fn new(transaction: &'a mut Transaction<'engine>) -> Self {
        Self { transaction }
    }

    /// Reads one family record's encoded value.
    ///
    /// `Ok(None)` means no record exists. A document that occupies the
    /// reserved identifier without belonging to the reserved collection is a
    /// namespace collision and reports
    /// [`FamilyError::ReservedStateInvalid`].
    pub(crate) fn read(&mut self, id: &DocumentId) -> Result<Option<DocumentValue>, FamilyError> {
        let document = self.transaction.read(id).map_err(|_| FamilyError::StorageFailure)?;
        match document {
            None => Ok(None),
            Some(document) => {
                if document.collection().as_str() != RESERVED_COLLECTION {
                    return Err(FamilyError::ReservedStateInvalid);
                }
                Ok(Some(document.value().clone()))
            }
        }
    }

    /// Stages one family record value.
    pub(crate) fn write(
        &mut self,
        id: DocumentId,
        value: DocumentValue,
    ) -> Result<(), FamilyError> {
        let collection = CollectionName::try_new(RESERVED_COLLECTION)
            .map_err(|_| FamilyError::StorageFailure)?;
        let document = Document::new(id, collection, value);
        self.transaction.put(document, ExpectedVersion::Any).map_err(|error| match error {
            crate::engine::EngineError::InvalidDocument => FamilyError::ReservedStateInvalid,
            _ => FamilyError::StorageFailure,
        })
    }

    /// Stages the deletion of one family record.
    pub(crate) fn clear(&mut self, id: &DocumentId) -> Result<(), FamilyError> {
        self.transaction
            .delete(id.clone(), ExpectedVersion::Any)
            .map_err(|_| FamilyError::StorageFailure)
    }

    /// Reads one user document, returning its identity, collection, version,
    /// and value.
    pub(crate) fn user_document(
        &mut self,
        id: &DocumentId,
    ) -> Result<Option<(CollectionName, u64, DocumentValue)>, FamilyError> {
        let document = self.transaction.read(id).map_err(|_| FamilyError::StorageFailure)?;
        Ok(document.map(|document| {
            (document.collection().clone(), document.metadata().version(), document.value().clone())
        }))
    }

    /// Stages one user-document write, used by revision revert.
    pub(crate) fn write_user_document(
        &mut self,
        id: DocumentId,
        collection: CollectionName,
        value: DocumentValue,
    ) -> Result<(), FamilyError> {
        let document = Document::new(id, collection, value);
        self.transaction.put(document, ExpectedVersion::Any).map_err(|error| match error {
            crate::engine::EngineError::InvalidDocument => FamilyError::InvalidRequest,
            _ => FamilyError::StorageFailure,
        })
    }
}

// ---------------------------------------------------------------------------
// Encoding helpers
// ---------------------------------------------------------------------------

/// Builds one object value.
fn object(fields: Vec<(&str, DocumentValue)>) -> DocumentValue {
    DocumentValue::Object(
        fields.into_iter().map(|(key, value)| (key.to_owned(), value)).collect::<BTreeMap<_, _>>(),
    )
}

/// Reads one nested object field.
fn field<'v>(value: &'v DocumentValue, key: &str) -> Option<&'v DocumentValue> {
    match value {
        DocumentValue::Object(fields) => fields.get(key),
        _ => None,
    }
}

/// Reads one integer field.
fn integer(value: &DocumentValue, key: &str) -> Option<i64> {
    match field(value, key) {
        Some(DocumentValue::Integer(number)) => Some(*number),
        _ => None,
    }
}

/// Reads one optional integer field, rejecting a malformed encoding.
fn optional_integer(value: &DocumentValue, key: &str) -> Result<Option<i64>, FamilyError> {
    match field(value, key) {
        None | Some(DocumentValue::Null) => Ok(None),
        Some(DocumentValue::Integer(number)) => Ok(Some(*number)),
        _ => Err(FamilyError::ReservedStateInvalid),
    }
}

/// Reads one text field.
fn text<'v>(value: &'v DocumentValue, key: &str) -> Option<&'v str> {
    match field(value, key) {
        Some(DocumentValue::String(text)) => Some(text.as_str()),
        _ => None,
    }
}

/// Reads one boolean field.
fn boolean(value: &DocumentValue, key: &str) -> Option<bool> {
    match field(value, key) {
        Some(DocumentValue::Boolean(flag)) => Some(*flag),
        _ => None,
    }
}

/// Reads one array field.
fn array<'v>(value: &'v DocumentValue, key: &str) -> Option<&'v [DocumentValue]> {
    match field(value, key) {
        Some(DocumentValue::Array(items)) => Some(items.as_slice()),
        _ => None,
    }
}

/// Reads one object-map field.
fn map<'v>(value: &'v DocumentValue, key: &str) -> Option<&'v BTreeMap<String, DocumentValue>> {
    match field(value, key) {
        Some(DocumentValue::Object(fields)) => Some(fields),
        _ => None,
    }
}

// ---------------------------------------------------------------------------
// Attachment records
// ---------------------------------------------------------------------------

/// One stored attachment-metadata entry.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct AttachmentEntry {
    pub(crate) content_type: String,
    pub(crate) size: u64,
    pub(crate) hash: String,
}

/// The stored attachments of one document.
#[derive(Clone, Debug, Default, PartialEq)]
pub(crate) struct AttachmentRecord {
    pub(crate) entries: BTreeMap<String, AttachmentEntry>,
}

impl AttachmentRecord {
    /// Decodes one record value.
    pub(crate) fn decode(value: &DocumentValue) -> Result<Self, FamilyError> {
        let Some(fields) = map(value, "attachments") else {
            return Err(FamilyError::ReservedStateInvalid);
        };
        let mut entries = BTreeMap::new();
        for (name, encoded) in fields {
            let content_type = text(encoded, "content_type");
            let size = integer(encoded, "size");
            let hash = text(encoded, "hash");
            match (content_type, size, hash) {
                (Some(content_type), Some(size), Some(hash)) if size >= 0 => {
                    entries.insert(
                        name.clone(),
                        AttachmentEntry {
                            content_type: content_type.to_owned(),
                            size: u64::try_from(size).unwrap_or(0),
                            hash: hash.to_owned(),
                        },
                    );
                }
                _ => return Err(FamilyError::ReservedStateInvalid),
            }
        }
        Ok(Self { entries })
    }

    /// Encodes one record value.
    pub(crate) fn encode(&self) -> DocumentValue {
        object(vec![(
            "attachments",
            DocumentValue::Object(
                self.entries
                    .iter()
                    .map(|(name, entry)| {
                        (
                            name.clone(),
                            object(vec![
                                ("content_type", DocumentValue::String(entry.content_type.clone())),
                                (
                                    "size",
                                    DocumentValue::Integer(i64::try_from(entry.size).unwrap_or(0)),
                                ),
                                ("hash", DocumentValue::String(entry.hash.clone())),
                            ]),
                        )
                    })
                    .collect(),
            ),
        )])
    }
}

// ---------------------------------------------------------------------------
// Counter records
// ---------------------------------------------------------------------------

/// The stored counters of one document.
#[derive(Clone, Debug, Default, PartialEq)]
pub(crate) struct CounterRecord {
    pub(crate) counters: BTreeMap<String, i64>,
}

impl CounterRecord {
    /// Decodes one record value.
    pub(crate) fn decode(value: &DocumentValue) -> Result<Self, FamilyError> {
        let Some(fields) = map(value, "counters") else {
            return Err(FamilyError::ReservedStateInvalid);
        };
        let mut counters = BTreeMap::new();
        for (name, encoded) in fields {
            let DocumentValue::Integer(number) = encoded else {
                return Err(FamilyError::ReservedStateInvalid);
            };
            counters.insert(name.clone(), *number);
        }
        Ok(Self { counters })
    }

    /// Encodes one record value.
    pub(crate) fn encode(&self) -> DocumentValue {
        object(vec![(
            "counters",
            DocumentValue::Object(
                self.counters
                    .iter()
                    .map(|(name, value)| (name.clone(), DocumentValue::Integer(*value)))
                    .collect(),
            ),
        )])
    }
}

// ---------------------------------------------------------------------------
// Time-series records
// ---------------------------------------------------------------------------

/// The stored policy of one series.
fn encode_policy(policy: &TimeSeriesPolicy) -> DocumentValue {
    object(vec![
        (
            "retention_ms",
            match policy.retention_ms() {
                Some(value) => DocumentValue::Integer(value),
                None => DocumentValue::Null,
            },
        ),
        (
            "rollups",
            DocumentValue::Array(
                policy
                    .rollups()
                    .iter()
                    .map(|rollup| {
                        object(vec![
                            ("name", DocumentValue::String(rollup.name().to_owned())),
                            (
                                "aggregation",
                                DocumentValue::String(
                                    match rollup.aggregation() {
                                        Aggregation::Min => "min",
                                        Aggregation::Max => "max",
                                        Aggregation::Sum => "sum",
                                        Aggregation::Mean => "mean",
                                        Aggregation::Count => "count",
                                    }
                                    .to_owned(),
                                ),
                            ),
                            ("window_ms", DocumentValue::Integer(rollup.window_ms())),
                        ])
                    })
                    .collect(),
            ),
        ),
    ])
}

fn decode_policy(value: &DocumentValue) -> Result<TimeSeriesPolicy, FamilyError> {
    let retention = optional_integer(value, "retention_ms")?;
    let mut rollups = Vec::new();
    for encoded in array(value, "rollups").unwrap_or(&[]) {
        let Some(name) = text(encoded, "name") else {
            return Err(FamilyError::ReservedStateInvalid);
        };
        let aggregation = match text(encoded, "aggregation") {
            Some("min") => Aggregation::Min,
            Some("max") => Aggregation::Max,
            Some("sum") => Aggregation::Sum,
            Some("mean") => Aggregation::Mean,
            Some("count") => Aggregation::Count,
            _ => return Err(FamilyError::ReservedStateInvalid),
        };
        let Some(window) = integer(encoded, "window_ms") else {
            return Err(FamilyError::ReservedStateInvalid);
        };
        rollups.push(RollupPolicy::new(name.to_owned(), aggregation, window));
    }
    TimeSeriesPolicy::try_new(retention, rollups).map_err(|_| FamilyError::ReservedStateInvalid)
}

/// The stored state of one named series.
#[derive(Clone, Debug, Default, PartialEq)]
pub(crate) struct TimeSeriesRecord {
    pub(crate) arity: Option<usize>,
    pub(crate) entries: Vec<TimeSeriesAppend>,
    pub(crate) policy: Option<TimeSeriesPolicy>,
}

impl TimeSeriesRecord {
    /// Decodes one record value.
    pub(crate) fn decode(value: &DocumentValue) -> Result<Self, FamilyError> {
        let arity = match optional_integer(value, "arity")? {
            Some(value) if value >= 0 => Some(usize::try_from(value).unwrap_or(0)),
            Some(_) => return Err(FamilyError::ReservedStateInvalid),
            None => None,
        };
        let mut entries = Vec::new();
        for encoded in array(value, "entries").ok_or(FamilyError::ReservedStateInvalid)? {
            let Some(timestamp) = integer(encoded, "timestamp") else {
                return Err(FamilyError::ReservedStateInvalid);
            };
            let Some(values) = array(encoded, "values") else {
                return Err(FamilyError::ReservedStateInvalid);
            };
            let values = values
                .iter()
                .map(|item| match item {
                    DocumentValue::Number(number) => Some(*number),
                    _ => None,
                })
                .collect::<Option<Vec<_>>>()
                .ok_or(FamilyError::ReservedStateInvalid)?;
            let tag = match field(encoded, "tag") {
                None | Some(DocumentValue::Null) => None,
                Some(DocumentValue::String(tag)) => Some(tag.clone()),
                _ => return Err(FamilyError::ReservedStateInvalid),
            };
            entries.push(TimeSeriesAppend::new(timestamp, values, tag));
        }
        entries.sort_by_key(TimeSeriesAppend::timestamp);
        let policy = match field(value, "policy") {
            None | Some(DocumentValue::Null) => None,
            Some(encoded) => Some(decode_policy(encoded)?),
        };
        Ok(Self { arity, entries, policy })
    }

    /// Encodes one record value.
    pub(crate) fn encode(&self) -> DocumentValue {
        object(vec![
            (
                "arity",
                match self.arity {
                    Some(arity) => DocumentValue::Integer(i64::try_from(arity).unwrap_or(0)),
                    None => DocumentValue::Null,
                },
            ),
            (
                "entries",
                DocumentValue::Array(
                    self.entries
                        .iter()
                        .map(|entry| {
                            let mut fields = vec![
                                ("timestamp", DocumentValue::Integer(entry.timestamp())),
                                (
                                    "values",
                                    DocumentValue::Array(
                                        entry
                                            .values()
                                            .iter()
                                            .map(|v| DocumentValue::Number(*v))
                                            .collect(),
                                    ),
                                ),
                            ];
                            match entry.tag().map(str::to_owned) {
                                Some(tag) => fields.push(("tag", DocumentValue::String(tag))),
                                None => fields.push(("tag", DocumentValue::Null)),
                            }
                            object(fields)
                        })
                        .collect(),
                ),
            ),
            (
                "policy",
                match &self.policy {
                    Some(policy) => encode_policy(policy),
                    None => DocumentValue::Null,
                },
            ),
        ])
    }

    /// Removes every entry inside one inclusive delete range.
    pub(crate) fn delete_range(&mut self, start: i64, end: i64) {
        self.entries.retain(|entry| entry.timestamp() < start || entry.timestamp() > end);
    }

    /// Removes one timestamp.
    pub(crate) fn delete_at(&mut self, timestamp: i64) {
        self.entries.retain(|entry| entry.timestamp() != timestamp);
    }
}

// ---------------------------------------------------------------------------
// Revision records
// ---------------------------------------------------------------------------

/// The stored revisions of one document.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct RevisionRecord {
    pub(crate) next: u64,
    pub(crate) entries: Vec<(u64, i64, DocumentValue)>,
    pub(crate) policy: Option<RevisionPolicy>,
}

impl Default for RevisionRecord {
    fn default() -> Self {
        Self { next: 1, entries: Vec::new(), policy: None }
    }
}

impl RevisionRecord {
    /// Decodes one record value.
    pub(crate) fn decode(value: &DocumentValue) -> Result<Self, FamilyError> {
        let next = u64::try_from(integer(value, "next").ok_or(FamilyError::ReservedStateInvalid)?)
            .map_err(|_| FamilyError::ReservedStateInvalid)?;
        let mut entries = Vec::new();
        for encoded in array(value, "entries").ok_or(FamilyError::ReservedStateInvalid)? {
            let revision = u64::try_from(
                integer(encoded, "revision").ok_or(FamilyError::ReservedStateInvalid)?,
            )
            .map_err(|_| FamilyError::ReservedStateInvalid)?;
            let at = integer(encoded, "at").ok_or(FamilyError::ReservedStateInvalid)?;
            let value = field(encoded, "value").ok_or(FamilyError::ReservedStateInvalid)?.clone();
            entries.push((revision, at, value));
        }
        entries.sort_by_key(|(revision, _, _)| *revision);
        let policy = match field(value, "policy") {
            None | Some(DocumentValue::Null) => None,
            Some(encoded) => {
                let minimum = u32::try_from(
                    integer(encoded, "minimum").ok_or(FamilyError::ReservedStateInvalid)?,
                )
                .map_err(|_| FamilyError::ReservedStateInvalid)?;
                let maximum = match optional_integer(encoded, "maximum")? {
                    Some(value) if value >= 0 => {
                        Some(u32::try_from(value).map_err(|_| FamilyError::ReservedStateInvalid)?)
                    }
                    Some(_) => return Err(FamilyError::ReservedStateInvalid),
                    None => None,
                };
                Some(
                    RevisionPolicy::try_new(minimum, maximum)
                        .map_err(|_| FamilyError::ReservedStateInvalid)?,
                )
            }
        };
        Ok(Self { next, entries, policy })
    }

    /// Encodes one record value.
    pub(crate) fn encode(&self) -> DocumentValue {
        object(vec![
            ("next", DocumentValue::Integer(i64::try_from(self.next).unwrap_or(0))),
            (
                "entries",
                DocumentValue::Array(
                    self.entries
                        .iter()
                        .map(|(revision, at, value)| {
                            object(vec![
                                (
                                    "revision",
                                    DocumentValue::Integer(i64::try_from(*revision).unwrap_or(0)),
                                ),
                                ("at", DocumentValue::Integer(*at)),
                                ("value", value.clone()),
                            ])
                        })
                        .collect(),
                ),
            ),
            (
                "policy",
                match &self.policy {
                    Some(policy) => object(vec![
                        ("minimum", DocumentValue::Integer(i64::from(policy.minimum_revisions()))),
                        (
                            "maximum",
                            match policy.maximum_revisions() {
                                Some(value) => DocumentValue::Integer(i64::from(value)),
                                None => DocumentValue::Null,
                            },
                        ),
                    ]),
                    None => DocumentValue::Null,
                },
            ),
        ])
    }

    /// Appends one revision and trims the log to the configured limits.
    pub(crate) fn push(&mut self, at: i64, value: DocumentValue) -> u64 {
        let revision = self.next;
        self.next = self.next.saturating_add(1);
        self.entries.push((revision, at, value));
        self.trim_to_policy();
        revision
    }

    /// Trims the oldest revisions until the log fits the configured maximum,
    /// never dropping below the configured minimum.
    pub(crate) fn trim_to_policy(&mut self) {
        if let Some(maximum) = self.policy.and_then(|policy| policy.maximum_revisions()) {
            let limit = usize::try_from(maximum).unwrap_or(usize::MAX);
            if self.entries.len() > limit {
                let minimum =
                    self.policy.map_or(1, |policy: RevisionPolicy| policy.minimum_revisions());
                let keep = limit.max(usize::try_from(minimum).unwrap_or(1));
                let drop = self.entries.len().saturating_sub(keep);
                self.entries.drain(..drop);
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Compare-exchange records
// ---------------------------------------------------------------------------

/// The stored state of one compare-exchange key.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct CompareExchangeRecord {
    pub(crate) index: u64,
    pub(crate) value: Option<DocumentValue>,
    pub(crate) expires_at: Option<i64>,
    pub(crate) deleted: bool,
}

impl Default for CompareExchangeRecord {
    fn default() -> Self {
        Self { index: 0, value: None, expires_at: None, deleted: true }
    }
}

impl CompareExchangeRecord {
    /// Reports whether the stored value has expired.
    pub(crate) fn is_expired(&self, now: i64) -> bool {
        self.expires_at.is_some_and(|at| at <= now)
    }

    /// Reports whether the key holds a live value.
    pub(crate) fn is_live(&self, now: i64) -> bool {
        !self.deleted && !self.is_expired(now)
    }

    /// Decodes one record value.
    pub(crate) fn decode(value: &DocumentValue) -> Result<Self, FamilyError> {
        let index =
            u64::try_from(integer(value, "index").ok_or(FamilyError::ReservedStateInvalid)?)
                .map_err(|_| FamilyError::ReservedStateInvalid)?;
        let stored = field(value, "value").ok_or(FamilyError::ReservedStateInvalid)?;
        let stored_value = match stored {
            DocumentValue::Null => None,
            other => Some(other.clone()),
        };
        let expires_at = optional_integer(value, "expires_at")?;
        let deleted = boolean(value, "deleted").ok_or(FamilyError::ReservedStateInvalid)?;
        Ok(Self { index, value: stored_value, expires_at, deleted })
    }

    /// Encodes one record value.
    pub(crate) fn encode(&self) -> DocumentValue {
        object(vec![
            ("index", DocumentValue::Integer(i64::try_from(self.index).unwrap_or(0))),
            (
                "value",
                match &self.value {
                    Some(value) => value.clone(),
                    None => DocumentValue::Null,
                },
            ),
            (
                "expires_at",
                match self.expires_at {
                    Some(at) => DocumentValue::Integer(at),
                    None => DocumentValue::Null,
                },
            ),
            ("deleted", DocumentValue::Boolean(self.deleted)),
        ])
    }
}

// ---------------------------------------------------------------------------
// Identity, lifecycle, and archival records
// ---------------------------------------------------------------------------

/// The stored next value of one identity.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub(crate) struct IdentityRecord {
    pub(crate) next: u64,
}

impl IdentityRecord {
    /// Decodes one record value.
    pub(crate) fn decode(value: &DocumentValue) -> Result<Self, FamilyError> {
        let next = u64::try_from(integer(value, "next").ok_or(FamilyError::ReservedStateInvalid)?)
            .map_err(|_| FamilyError::ReservedStateInvalid)?;
        Ok(Self { next })
    }

    /// Encodes one record value.
    pub(crate) fn encode(self) -> DocumentValue {
        object(vec![("next", DocumentValue::Integer(i64::try_from(self.next).unwrap_or(0)))])
    }
}

/// The stored lifecycle targets of one document.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub(crate) struct LifecycleRecord {
    pub(crate) expires_at: Option<i64>,
    pub(crate) refresh_at: Option<i64>,
}

impl LifecycleRecord {
    /// Decodes one record value.
    pub(crate) fn decode(value: &DocumentValue) -> Result<Self, FamilyError> {
        let expires_at = optional_integer(value, "expires_at")?;
        let refresh_at = optional_integer(value, "refresh_at")?;
        Ok(Self { expires_at, refresh_at })
    }

    /// Encodes one record value.
    pub(crate) fn encode(&self) -> DocumentValue {
        object(vec![
            (
                "expires_at",
                match self.expires_at {
                    Some(at) => DocumentValue::Integer(at),
                    None => DocumentValue::Null,
                },
            ),
            (
                "refresh_at",
                match self.refresh_at {
                    Some(at) => DocumentValue::Integer(at),
                    None => DocumentValue::Null,
                },
            ),
        ])
    }

    /// Reports whether the configured expiry has elapsed.
    pub(crate) fn is_expired(&self, now: i64) -> bool {
        self.expires_at.is_some_and(|at| at <= now)
    }
}

/// The stored archival state of one document.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub(crate) struct ArchivalRecord {
    pub(crate) archived: bool,
    pub(crate) at: Option<i64>,
    pub(crate) after_ms: Option<i64>,
}

impl ArchivalRecord {
    /// Decodes one record value.
    pub(crate) fn decode(value: &DocumentValue) -> Result<Self, FamilyError> {
        let archived = boolean(value, "archived").ok_or(FamilyError::ReservedStateInvalid)?;
        let at = optional_integer(value, "at")?;
        let after_ms = optional_integer(value, "after_ms")?;
        Ok(Self { archived, at, after_ms })
    }

    /// Encodes one record value.
    pub(crate) fn encode(&self) -> DocumentValue {
        object(vec![
            ("archived", DocumentValue::Boolean(self.archived)),
            (
                "at",
                match self.at {
                    Some(at) => DocumentValue::Integer(at),
                    None => DocumentValue::Null,
                },
            ),
            (
                "after_ms",
                match self.after_ms {
                    Some(value) => DocumentValue::Integer(value),
                    None => DocumentValue::Null,
                },
            ),
        ])
    }
}

// ---------------------------------------------------------------------------
// Validation and tombstone records
// ---------------------------------------------------------------------------

/// Encodes one schema field type.
fn encode_field_type(kind: SchemaFieldType) -> DocumentValue {
    DocumentValue::String(
        match kind {
            SchemaFieldType::Boolean => "boolean",
            SchemaFieldType::Integer => "integer",
            SchemaFieldType::Number => "number",
            SchemaFieldType::Text => "text",
            SchemaFieldType::Object => "object",
            SchemaFieldType::Array => "array",
        }
        .to_owned(),
    )
}

fn decode_field_type(value: &DocumentValue) -> Option<SchemaFieldType> {
    match value {
        DocumentValue::String(text) => match text.as_str() {
            "boolean" => Some(SchemaFieldType::Boolean),
            "integer" => Some(SchemaFieldType::Integer),
            "number" => Some(SchemaFieldType::Number),
            "text" => Some(SchemaFieldType::Text),
            "object" => Some(SchemaFieldType::Object),
            "array" => Some(SchemaFieldType::Array),
            _ => None,
        },
        _ => None,
    }
}

/// The stored validation policy of one collection.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct ValidationRecord {
    pub(crate) schema: SchemaDefinition,
}

impl ValidationRecord {
    /// Decodes one record value.
    pub(crate) fn decode(value: &DocumentValue) -> Result<Self, FamilyError> {
        let mut rules = Vec::new();
        for encoded in array(value, "rules").ok_or(FamilyError::ReservedStateInvalid)? {
            let Some(field_name) = text(encoded, "field") else {
                return Err(FamilyError::ReservedStateInvalid);
            };
            let rule = match text(encoded, "rule") {
                Some("required") => SchemaRule::Required { field: field_name.to_owned() },
                Some("field_type") => {
                    let Some(kind) = field(encoded, "kind").and_then(decode_field_type) else {
                        return Err(FamilyError::ReservedStateInvalid);
                    };
                    SchemaRule::FieldType { field: field_name.to_owned(), kind }
                }
                Some("enum") => {
                    let Some(values) = array(encoded, "values") else {
                        return Err(FamilyError::ReservedStateInvalid);
                    };
                    let values = values
                        .iter()
                        .map(|item| match item {
                            DocumentValue::String(value) => Some(value.clone()),
                            _ => None,
                        })
                        .collect::<Option<Vec<_>>>()
                        .ok_or(FamilyError::ReservedStateInvalid)?;
                    SchemaRule::EnumValues { field: field_name.to_owned(), values }
                }
                Some("number_range") => {
                    let min = optional_integer(encoded, "min")?;
                    let max = optional_integer(encoded, "max")?;
                    SchemaRule::NumberRange { field: field_name.to_owned(), min, max }
                }
                Some("text_length") => {
                    let min = optional_integer(encoded, "min")?
                        .map(|value| u64::try_from(value).unwrap_or(0));
                    let max = optional_integer(encoded, "max")?
                        .map(|value| u64::try_from(value).unwrap_or(0));
                    SchemaRule::TextLength { field: field_name.to_owned(), min, max }
                }
                _ => return Err(FamilyError::ReservedStateInvalid),
            };
            rules.push(rule);
        }
        let schema =
            SchemaDefinition::try_new(rules).map_err(|_| FamilyError::ReservedStateInvalid)?;
        Ok(Self { schema })
    }

    /// Encodes one record value.
    pub(crate) fn encode(&self) -> DocumentValue {
        object(vec![(
            "rules",
            DocumentValue::Array(
                self.schema
                    .rules()
                    .iter()
                    .map(|rule| {
                        let field = rule.field().to_owned();
                        match rule {
                            SchemaRule::Required { .. } => object(vec![
                                ("rule", DocumentValue::String("required".to_owned())),
                                ("field", DocumentValue::String(field)),
                            ]),
                            SchemaRule::FieldType { kind, .. } => object(vec![
                                ("rule", DocumentValue::String("field_type".to_owned())),
                                ("field", DocumentValue::String(field)),
                                ("kind", encode_field_type(*kind)),
                            ]),
                            SchemaRule::EnumValues { values, .. } => object(vec![
                                ("rule", DocumentValue::String("enum".to_owned())),
                                ("field", DocumentValue::String(field)),
                                (
                                    "values",
                                    DocumentValue::Array(
                                        values
                                            .iter()
                                            .map(|v| DocumentValue::String(v.clone()))
                                            .collect(),
                                    ),
                                ),
                            ]),
                            SchemaRule::NumberRange { min, max, .. } => object(vec![
                                ("rule", DocumentValue::String("number_range".to_owned())),
                                ("field", DocumentValue::String(field)),
                                (
                                    "min",
                                    match min {
                                        Some(value) => DocumentValue::Integer(*value),
                                        None => DocumentValue::Null,
                                    },
                                ),
                                (
                                    "max",
                                    match max {
                                        Some(value) => DocumentValue::Integer(*value),
                                        None => DocumentValue::Null,
                                    },
                                ),
                            ]),
                            SchemaRule::TextLength { min, max, .. } => object(vec![
                                ("rule", DocumentValue::String("text_length".to_owned())),
                                ("field", DocumentValue::String(field)),
                                (
                                    "min",
                                    match min {
                                        Some(value) => DocumentValue::Integer(
                                            i64::try_from(*value).unwrap_or(0),
                                        ),
                                        None => DocumentValue::Null,
                                    },
                                ),
                                (
                                    "max",
                                    match max {
                                        Some(value) => DocumentValue::Integer(
                                            i64::try_from(*value).unwrap_or(0),
                                        ),
                                        None => DocumentValue::Null,
                                    },
                                ),
                            ]),
                        }
                    })
                    .collect(),
            ),
        )])
    }
}

/// The stored family tombstones of one document.
#[derive(Clone, Debug, Default, PartialEq)]
pub(crate) struct TombstoneRecord {
    pub(crate) entries: BTreeMap<String, (u64, i64)>,
}

impl TombstoneRecord {
    /// Decodes one record value.
    pub(crate) fn decode(value: &DocumentValue) -> Result<Self, FamilyError> {
        let DocumentValue::Object(fields) = value else {
            return Err(FamilyError::ReservedStateInvalid);
        };
        let mut entries = BTreeMap::new();
        for (family, encoded) in fields {
            let version =
                integer(encoded, "deletion_version").ok_or(FamilyError::ReservedStateInvalid)?;
            let recorded_at =
                integer(encoded, "recorded_at").ok_or(FamilyError::ReservedStateInvalid)?;
            let version = u64::try_from(version).map_err(|_| FamilyError::ReservedStateInvalid)?;
            entries.insert(family.clone(), (version, recorded_at));
        }
        Ok(Self { entries })
    }

    /// Encodes one record value.
    pub(crate) fn encode(&self) -> DocumentValue {
        DocumentValue::Object(
            self.entries
                .iter()
                .map(|(family, (version, recorded_at))| {
                    (
                        family.clone(),
                        object(vec![
                            (
                                "deletion_version",
                                DocumentValue::Integer(i64::try_from(*version).unwrap_or(0)),
                            ),
                            ("recorded_at", DocumentValue::Integer(*recorded_at)),
                        ]),
                    )
                })
                .collect(),
        )
    }

    /// Returns the recorded deletion version of one family.
    pub(crate) fn deletion_version(&self, family: super::FamilyKind) -> Option<u64> {
        self.entries.get(family_key(family)).map(|(version, _)| *version)
    }
}
