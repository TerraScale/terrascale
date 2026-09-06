//! The durable migration record and its private transactional mapping (SM-03).
//!
//! One migration's drive state is private control metadata at
//! `family:migration/<collection>`: the manifest identity that owns the
//! drive, the state (`in-progress`, `rolling-back`, or `complete` with the
//! receipt), the scan cursor the next page resumes from, and the running
//! evidence (counts and checksum chains) accumulated so far. The record is
//! what makes a migration resumable: an interrupted drive is restarted from
//! exactly the recorded step, and a completed drive answers a re-run as an
//! idempotent no-op with its recorded receipt.
//!
//! The record also retargets write-path enforcement while the drive is
//! in progress: a collection mid-migration validates puts against the
//! migration's target schema — where the collection is going — never against
//! both versions and never against neither. The hook is consulted by the
//! engine write-path schema enforcement in `schemas`; this module owns the
//! record's meaning.

use crate::engine::{EngineError, Transaction};
use crate::families::payloads::{SchemaDefinition, SchemaVersion};
use crate::families::state::{Records, ValidationRecord, record_id};
use crate::types::{CollectionName, DocumentId, DocumentValue};

use super::evidence::{DocumentChain, MigrationReceipt, from_hex};
use super::transform::{RollbackBlockedCause, RollbackLimit, TransformCounts, object};

/// The reserved record kind of the migration record.
const RECORD_KIND: &str = "migration";

/// Converts one non-negative count into the record's integer domain.
fn counter(value: u64) -> DocumentValue {
    DocumentValue::Integer(i64::try_from(value).unwrap_or(i64::MAX))
}

/// The composite scan cursor: the last (collection, identifier) key a page
/// processed. `None` means the scan starts (or restarted) from the
/// beginning.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct ScanCursor {
    /// The collection name of the last processed key.
    pub(crate) collection: String,
    /// The document identifier of the last processed key.
    pub(crate) document: String,
}

/// The durable state of one migration drive.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct MigrationRecord {
    /// The manifest digest of the driving contract.
    pub(crate) manifest: String,
    /// The from-version of the driving contract.
    pub(crate) from: SchemaVersion,
    /// The to-version of the driving contract.
    pub(crate) to: SchemaVersion,
    /// The drive state and its per-state facts.
    pub(crate) state: RecordState,
    /// The accumulated document count.
    pub(crate) documents: u64,
    /// The accumulated changed-document count.
    pub(crate) transformed: u64,
    /// The accumulated per-kind transform counts.
    pub(crate) counts: TransformCounts,
    /// The running pre-state checksum chain.
    pub(crate) source_chain: DocumentChain,
    /// The running result-state checksum chain.
    pub(crate) result_chain: DocumentChain,
    /// The committed page count.
    pub(crate) pages: u64,
    /// Whether the drive was begun under the explicit irreversible
    /// acknowledgment.
    pub(crate) acknowledged: bool,
}

/// The per-state facts of one migration record.
#[derive(Clone, Debug, PartialEq)]
pub(crate) enum RecordState {
    /// The forward drive is applying pages.
    InProgress {
        /// The scan cursor the next page resumes from, or `None` when the
        /// next page starts from the beginning.
        cursor: Option<ScanCursor>,
        /// The scan reached the end of the collection's documents.
        exhausted: bool,
        /// The target schema puts are enforced against while the drive is
        /// in progress.
        target: SchemaDefinition,
    },
    /// The rollback drive is restoring pages.
    RollingBack {
        /// The scan cursor the next rollback page resumes from.
        cursor: Option<ScanCursor>,
        /// The scan reached the end of the collection's documents.
        exhausted: bool,
    },
    /// The forward drive stopped before committing a page.
    Stopped {
        /// The bounded reason the drive could not continue.
        reason: MigrationStopReason,
    },
    /// The migration completed; the receipt is the durable evidence.
    Complete {
        /// The recorded receipt of the completed migration.
        receipt: MigrationReceipt,
    },
}

/// Why a migration drive was durably stopped.
//
// The stop record deliberately carries no document values or transform
// details. Callers can inspect the dry-run plan for those bounded facts while
// the durable record remains safe to expose through status and recovery APIs.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MigrationStopReason {
    /// A transform rejected a document.
    TransformRejected,
    /// A transformed value violated the target schema.
    TargetViolation,
}

impl MigrationStopReason {
    fn as_str(self) -> &'static str {
        match self {
            Self::TransformRejected => "transform-rejected",
            Self::TargetViolation => "target-violation",
        }
    }

    fn decode(value: &DocumentValue) -> Result<Self, EngineError> {
        match value {
            DocumentValue::String(value) if value == "transform-rejected" => {
                Ok(Self::TransformRejected)
            }
            DocumentValue::String(value) if value == "target-violation" => {
                Ok(Self::TargetViolation)
            }
            _ => Err(EngineError::StorageFailure),
        }
    }
}

/// Returns the reserved record identifier of one collection's migration
/// record.
pub(crate) fn migration_record_id(collection: &CollectionName) -> Result<DocumentId, EngineError> {
    record_id(RECORD_KIND, &[collection.as_str()]).map_err(|_| EngineError::StorageFailure)
}

/// Reads one collection's migration record through one transaction.
///
/// `Ok(None)` means no migration record exists. A record that does not decode
/// fails closed; customer documents cannot occupy this private address.
pub(crate) fn read_record(
    transaction: &Transaction<'_>,
    collection: &CollectionName,
) -> Result<Option<MigrationRecord>, EngineError> {
    let id = migration_record_id(collection)?;
    transaction.read_private_metadata(id.as_str())?.as_ref().map(decode).transpose()
}

/// Stages one migration record value inside the caller's transaction.
pub(crate) fn write_record(
    transaction: &mut Transaction<'_>,
    collection: &CollectionName,
    record: &MigrationRecord,
) -> Result<(), EngineError> {
    let id = migration_record_id(collection)?;
    let mut records = Records::new(transaction);
    records.write(id, encode(record)).map_err(|_| EngineError::StorageFailure)
}

/// Clears one collection's migration record inside the caller's transaction.
pub(crate) fn clear_record(
    transaction: &mut Transaction<'_>,
    collection: &CollectionName,
) -> Result<(), EngineError> {
    let id = migration_record_id(collection)?;
    let mut records = Records::new(transaction);
    records.clear(&id).map_err(|_| EngineError::StorageFailure)
}

// ---------------------------------------------------------------------------
// Encoding
// ---------------------------------------------------------------------------

/// Encodes one record value.
pub(crate) fn encode(record: &MigrationRecord) -> DocumentValue {
    let drive = match &record.state {
        RecordState::InProgress { cursor, exhausted, target } => object(vec![
            ("state", DocumentValue::String("in-progress".to_owned())),
            ("cursor", encode_cursor(cursor.as_ref())),
            ("exhausted", DocumentValue::Boolean(*exhausted)),
            ("target", ValidationRecord { schema: target.clone() }.encode()),
        ]),
        RecordState::RollingBack { cursor, exhausted } => object(vec![
            ("state", DocumentValue::String("rolling-back".to_owned())),
            ("cursor", encode_cursor(cursor.as_ref())),
            ("exhausted", DocumentValue::Boolean(*exhausted)),
        ]),
        RecordState::Stopped { reason } => object(vec![
            ("state", DocumentValue::String("stopped".to_owned())),
            ("reason", DocumentValue::String(reason.as_str().to_owned())),
        ]),
        RecordState::Complete { receipt } => object(vec![
            ("state", DocumentValue::String("complete".to_owned())),
            ("receipt", encode_receipt(receipt)),
        ]),
    };
    object(vec![
        ("manifest", DocumentValue::String(record.manifest.clone())),
        ("from", counter(record.from.get())),
        ("to", counter(record.to.get())),
        ("documents", counter(record.documents)),
        ("transformed", counter(record.transformed)),
        ("counts", encode_counts(&record.counts)),
        ("source_chain", encode_chain(&record.source_chain)),
        ("result_chain", encode_chain(&record.result_chain)),
        ("pages", counter(record.pages)),
        ("acknowledged", DocumentValue::Boolean(record.acknowledged)),
        ("drive", drive),
    ])
}

/// Decodes one record value, failing closed on any malformed field.
pub(crate) fn decode(value: &DocumentValue) -> Result<MigrationRecord, EngineError> {
    let malformed = || EngineError::StorageFailure;
    let DocumentValue::Object(fields) = value else {
        return Err(malformed());
    };
    let manifest = match fields.get("manifest") {
        Some(DocumentValue::String(text)) => text.clone(),
        _ => return Err(malformed()),
    };
    let version = |key: &str| -> Result<SchemaVersion, EngineError> {
        match fields.get(key) {
            Some(DocumentValue::Integer(number)) if *number > 0 => {
                SchemaVersion::try_new(u64::try_from(*number).map_err(|_| malformed())?)
                    .map_err(|_| malformed())
            }
            _ => Err(malformed()),
        }
    };
    let count = |key: &str| -> Result<u64, EngineError> {
        match fields.get(key) {
            Some(DocumentValue::Integer(number)) if *number >= 0 => {
                u64::try_from(*number).map_err(|_| malformed())
            }
            _ => Err(malformed()),
        }
    };
    let drive = fields.get("drive").ok_or_else(malformed)?;
    let DocumentValue::Object(drive_fields) = drive else {
        return Err(malformed());
    };
    let flag = |key: &str| -> Result<bool, EngineError> {
        match drive_fields.get(key) {
            Some(DocumentValue::Boolean(flag)) => Ok(*flag),
            _ => Err(malformed()),
        }
    };
    let state = match drive_fields.get("state") {
        Some(DocumentValue::String(text)) if text == "in-progress" => RecordState::InProgress {
            cursor: decode_cursor(drive_fields.get("cursor"))?,
            exhausted: flag("exhausted")?,
            target: ValidationRecord::decode(drive_fields.get("target").ok_or_else(malformed)?)
                .map_err(|_| malformed())?
                .schema,
        },
        Some(DocumentValue::String(text)) if text == "rolling-back" => RecordState::RollingBack {
            cursor: decode_cursor(drive_fields.get("cursor"))?,
            exhausted: flag("exhausted")?,
        },
        Some(DocumentValue::String(text)) if text == "complete" => RecordState::Complete {
            receipt: decode_receipt(drive_fields.get("receipt").ok_or_else(malformed)?)?,
        },
        Some(DocumentValue::String(text)) if text == "stopped" => RecordState::Stopped {
            reason: MigrationStopReason::decode(drive_fields.get("reason").ok_or_else(malformed)?)?,
        },
        _ => return Err(malformed()),
    };
    Ok(MigrationRecord {
        manifest,
        from: version("from")?,
        to: version("to")?,
        state,
        documents: count("documents")?,
        transformed: count("transformed")?,
        counts: decode_counts(fields.get("counts"))?,
        source_chain: decode_chain(fields.get("source_chain"))?,
        result_chain: decode_chain(fields.get("result_chain"))?,
        pages: count("pages")?,
        acknowledged: matches!(fields.get("acknowledged"), Some(DocumentValue::Boolean(true))),
    })
}

/// Encodes one scan cursor.
fn encode_cursor(cursor: Option<&ScanCursor>) -> DocumentValue {
    match cursor {
        None => DocumentValue::Null,
        Some(cursor) => object(vec![
            ("collection", DocumentValue::String(cursor.collection.clone())),
            ("document", DocumentValue::String(cursor.document.clone())),
        ]),
    }
}

/// Decodes one scan cursor.
fn decode_cursor(value: Option<&DocumentValue>) -> Result<Option<ScanCursor>, EngineError> {
    let malformed = || EngineError::StorageFailure;
    match value {
        None | Some(DocumentValue::Null) => Ok(None),
        Some(DocumentValue::Object(fields)) => {
            let (Some(DocumentValue::String(collection)), Some(DocumentValue::String(document))) =
                (fields.get("collection"), fields.get("document"))
            else {
                return Err(malformed());
            };
            Ok(Some(ScanCursor { collection: collection.clone(), document: document.clone() }))
        }
        _ => Err(malformed()),
    }
}

/// Encodes one per-kind count set.
fn encode_counts(counts: &TransformCounts) -> DocumentValue {
    object(vec![
        ("added", counter(counts.added)),
        ("removed", counter(counts.removed)),
        ("renamed", counter(counts.renamed)),
        ("retyped", counter(counts.retyped)),
    ])
}

/// Decodes one per-kind count set.
fn decode_counts(value: Option<&DocumentValue>) -> Result<TransformCounts, EngineError> {
    let malformed = || EngineError::StorageFailure;
    let DocumentValue::Object(fields) = value.ok_or_else(malformed)? else {
        return Err(malformed());
    };
    let count = |key: &str| -> Result<u64, EngineError> {
        match fields.get(key) {
            Some(DocumentValue::Integer(number)) if *number >= 0 => {
                u64::try_from(*number).map_err(|_| malformed())
            }
            _ => Err(malformed()),
        }
    };
    Ok(TransformCounts {
        added: count("added")?,
        removed: count("removed")?,
        renamed: count("renamed")?,
        retyped: count("retyped")?,
    })
}

/// Encodes one running chain.
fn encode_chain(chain: &DocumentChain) -> DocumentValue {
    object(vec![
        ("digest", DocumentValue::String(hex::encode(chain.running()))),
        ("links", counter(chain.links())),
    ])
}

/// Decodes one running chain.
fn decode_chain(value: Option<&DocumentValue>) -> Result<DocumentChain, EngineError> {
    let malformed = || EngineError::StorageFailure;
    let DocumentValue::Object(fields) = value.ok_or_else(malformed)? else {
        return Err(malformed());
    };
    let digest = match fields.get("digest") {
        Some(DocumentValue::String(text)) => from_hex(text).ok_or_else(malformed)?,
        _ => return Err(malformed()),
    };
    let links = match fields.get("links") {
        Some(DocumentValue::Integer(number)) if *number >= 0 => {
            u64::try_from(*number).map_err(|_| malformed())?
        }
        _ => return Err(malformed()),
    };
    Ok(DocumentChain::from_parts(digest, links))
}

/// Encodes one receipt for the record.
fn encode_receipt(receipt: &MigrationReceipt) -> DocumentValue {
    object(vec![
        ("collection", DocumentValue::String(receipt.collection().as_str().to_owned())),
        ("manifest", DocumentValue::String(receipt.manifest().to_owned())),
        ("from", counter(receipt.from_version().get())),
        ("to", counter(receipt.to_version().get())),
        ("source_digest", DocumentValue::String(receipt.source_digest().to_owned())),
        ("result_digest", DocumentValue::String(receipt.result_digest().to_owned())),
        ("documents", counter(receipt.documents())),
        ("transformed", counter(receipt.transformed())),
        ("counts", encode_counts(&receipt.transform_counts())),
        ("pages", counter(receipt.pages())),
        ("rollback", encode_rollback_limit(receipt.rollback_limit())),
        ("acknowledged", DocumentValue::Boolean(receipt.acknowledged_not_rollbackable())),
        ("already_applied", DocumentValue::Boolean(receipt.already_applied())),
    ])
}

/// Decodes one receipt from the record, failing closed on any field.
fn decode_receipt(value: &DocumentValue) -> Result<MigrationReceipt, EngineError> {
    let malformed = || EngineError::StorageFailure;
    let DocumentValue::Object(fields) = value else {
        return Err(malformed());
    };
    let text = |key: &str| -> Result<String, EngineError> {
        match fields.get(key) {
            Some(DocumentValue::String(text)) => Ok(text.clone()),
            _ => Err(malformed()),
        }
    };
    let count = |key: &str| -> Result<u64, EngineError> {
        match fields.get(key) {
            Some(DocumentValue::Integer(number)) if *number >= 0 => {
                u64::try_from(*number).map_err(|_| malformed())
            }
            _ => Err(malformed()),
        }
    };
    let version = |key: &str| -> Result<SchemaVersion, EngineError> {
        match fields.get(key) {
            Some(DocumentValue::Integer(number)) if *number > 0 => {
                SchemaVersion::try_new(u64::try_from(*number).map_err(|_| malformed())?)
                    .map_err(|_| malformed())
            }
            _ => Err(malformed()),
        }
    };
    let rollback = match fields.get("rollback") {
        Some(DocumentValue::String(text)) if text == "reversible" => RollbackLimit::Reversible,
        Some(DocumentValue::Object(limit)) => {
            let Some(DocumentValue::Array(items)) = limit.get("causes") else {
                return Err(malformed());
            };
            let mut causes = Vec::new();
            for item in items {
                let cause = match item {
                    DocumentValue::String(text) if text == "field-removed" => {
                        RollbackBlockedCause::FieldRemoved
                    }
                    DocumentValue::String(text) if text == "field-defaulted" => {
                        RollbackBlockedCause::FieldDefaulted
                    }
                    _ => return Err(malformed()),
                };
                causes.push(cause);
            }
            RollbackLimit::NotReversible { causes }
        }
        _ => return Err(malformed()),
    };
    Ok(MigrationReceipt::new(
        CollectionName::try_new(&text("collection")?).map_err(|_| malformed())?,
        text("manifest")?,
        version("from")?,
        version("to")?,
        text("source_digest")?,
        text("result_digest")?,
        count("documents")?,
        count("transformed")?,
        decode_counts(fields.get("counts"))?,
        count("pages")?,
        rollback,
        matches!(fields.get("acknowledged"), Some(DocumentValue::Boolean(true))),
        matches!(fields.get("already_applied"), Some(DocumentValue::Boolean(true))),
    ))
}

/// Encodes one rollback limit.
fn encode_rollback_limit(limit: &RollbackLimit) -> DocumentValue {
    match limit {
        RollbackLimit::Reversible => DocumentValue::String("reversible".to_owned()),
        RollbackLimit::NotReversible { causes } => object(vec![(
            "causes",
            DocumentValue::Array(
                causes
                    .iter()
                    .map(|cause| {
                        let text = match cause {
                            RollbackBlockedCause::FieldRemoved => "field-removed",
                            RollbackBlockedCause::FieldDefaulted => "field-defaulted",
                        };
                        DocumentValue::String(text.to_owned())
                    })
                    .collect(),
            ),
        )]),
    }
}

/// The typed public view of one collection's migration state.
#[derive(Clone, Debug, PartialEq)]
pub enum MigrationStatus {
    /// No migration record exists for the collection.
    None,
    /// A forward drive is applying pages.
    InProgress {
        /// The manifest digest of the driving contract.
        manifest: String,
        /// The from-version.
        from: SchemaVersion,
        /// The to-version.
        to: SchemaVersion,
        /// Documents scanned so far.
        documents: u64,
        /// Documents transformed so far.
        transformed: u64,
        /// Pages committed so far.
        pages: u64,
    },
    /// A rollback drive is restoring pages.
    RollingBack {
        /// The manifest digest of the driving contract.
        manifest: String,
        /// The from-version.
        from: SchemaVersion,
        /// The to-version being abandoned.
        to: SchemaVersion,
        /// Pages committed so far.
        pages: u64,
    },
    /// A forward drive stopped before committing its failing page.
    Stopped {
        /// The manifest digest of the stopped contract.
        manifest: String,
        /// The from-version.
        from: SchemaVersion,
        /// The target version that was not seated.
        to: SchemaVersion,
        /// The bounded reason for the stop.
        reason: MigrationStopReason,
        /// Documents scanned by committed pages.
        documents: u64,
        /// Documents transformed by committed pages.
        transformed: u64,
        /// Pages committed before the stop.
        pages: u64,
    },
    /// A migration completed with the recorded receipt.
    Complete {
        /// The recorded receipt.
        receipt: MigrationReceipt,
    },
}

/// Projects one record into its public status view.
pub(crate) fn status_of(record: &MigrationRecord) -> MigrationStatus {
    match &record.state {
        RecordState::InProgress { .. } => MigrationStatus::InProgress {
            manifest: record.manifest.clone(),
            from: record.from,
            to: record.to,
            documents: record.documents,
            transformed: record.transformed,
            pages: record.pages,
        },
        RecordState::RollingBack { .. } => MigrationStatus::RollingBack {
            manifest: record.manifest.clone(),
            from: record.from,
            to: record.to,
            pages: record.pages,
        },
        RecordState::Stopped { reason } => MigrationStatus::Stopped {
            manifest: record.manifest.clone(),
            from: record.from,
            to: record.to,
            reason: *reason,
            documents: record.documents,
            transformed: record.transformed,
            pages: record.pages,
        },
        RecordState::Complete { receipt } => MigrationStatus::Complete { receipt: receipt.clone() },
    }
}
